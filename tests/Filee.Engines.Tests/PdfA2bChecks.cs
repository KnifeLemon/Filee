// A check of the PDF/A-2b rules (ISO 19005-2, level B) Filee's own PDF/A writer keeps (Pdf/PdfAConformance.cs,
// PdfAMetadata.cs, PdfAFonts.cs, PdfAAppearances.cs), for tests: veraPDF isn't run here. Each rule is named by its
// clause of ISO 19005-2 (the numbers of veraPDF's published rule list for parts 2 and 3). It reads the file twice:
// as bytes for the file structure (header, every cross-reference section of the incremental updates, object and
// stream syntax, trailers) and with PDFsharp for the objects, and gives back the rules broken, so a failing test says
// all of them at once.
//
// Covered: 6.1.2 header; 6.1.3 trailer ID (the same first ID in every update), no Encrypt, nothing after %%EOF;
// 6.1.4 cross-reference sections (each offset at its object, /Prev chains, subsection syntax, /Size); 6.1.7.1 stream
// keyword, Length and endstream; 6.1.7.2 no LZW (streams and inline pictures); 6.1.8 "N G obj" syntax; 6.2.3 one
// PDF/A output intent with a valid ICC profile; 6.2.4.2 the ICC profiles of ICCBased spaces; 6.2.4.3 device colours
// only with the matching intent or a Default space in the resources the content is drawn with (names in objects
// strictly by the intent); 6.2.5 graphics states; 6.2.8 pictures; 6.2.9 forms and PostScript; 6.2.10 blend modes,
// soft masks and transparency group colour spaces; 6.2.11.3.2 CIDToGIDMap; 6.2.11.4.1 drawn fonts embedded and
// readable; 6.2.11.5 widths of embedded TrueType programs against the dictionary (simple and CIDFontType2, codes
// drawn, 1/1000 em); 6.2.11.6 symbolic and non-symbolic TrueType encodings and cmaps; 6.2.11.8 no .notdef drawn with
// TrueType programs; ToUnicode maps are CMap streams; 6.3 annotation types, flags and appearances; 6.4 forms;
// 6.5 actions, no JavaScript anywhere; 6.6.2 one XMP metadata stream (the catalog's), its header and syntax;
// 6.6.2.3.1 only predefined schemas; 6.6.3 Info matching XMP (dates included); 6.6.4 pdfaid part 2, conformance B;
// 6.9 optional content.
//
// Not covered: CFF and Type 1 programs' widths and glyphs, Type 3 fonts, ToUnicode values (level U), JPEG 2000,
// the implementation limits of 6.1.13, cross-reference streams (Filee writes tables), XMP extension schemas.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Filee.Engines.Fonts;
using Filee.Engines.Pdf;
using Filee.Engines.Pdf.PostScript;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Lexer = Filee.Engines.Pdf.PdfContentLexer;

namespace Filee.Engines.Tests;

internal static partial class PdfA2bChecks
{
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Xmp = "http://ns.adobe.com/xap/1.0/";
    private static readonly XNamespace Pdf = "http://ns.adobe.com/pdf/1.3/";
    private static readonly XNamespace PdfAId = "http://www.aiim.org/pdfa/ns/id/";
    private static readonly XNamespace Xml = "http://www.w3.org/XML/1998/namespace";

    /// <summary>Schemas the XMP specification (2005) and PDF/A predefine.</summary>
    private static readonly HashSet<string> Schemas =
    [
        "http://purl.org/dc/elements/1.1/", "http://ns.adobe.com/xap/1.0/", "http://ns.adobe.com/pdf/1.3/", "http://www.aiim.org/pdfa/ns/id/",
        "http://ns.adobe.com/xap/1.0/mm/", "http://ns.adobe.com/xap/1.0/rights/", "http://ns.adobe.com/xap/1.0/bj/",
        "http://ns.adobe.com/xap/1.0/t/pg/", "http://ns.adobe.com/xmp/1.0/DynamicMedia/", "http://ns.adobe.com/photoshop/1.0/",
        "http://ns.adobe.com/camera-raw-settings/1.0/", "http://ns.adobe.com/tiff/1.0/", "http://ns.adobe.com/exif/1.0/",
        "http://ns.adobe.com/exif/1.0/aux/", "http://ns.adobe.com/xmp/Identifier/qual/1.0/",
    ];

    private static readonly HashSet<string> Annotations =
    [
        "/Text", "/Link", "/FreeText", "/Line", "/Square", "/Circle", "/Polygon", "/PolyLine", "/Highlight", "/Underline",
        "/Squiggly", "/StrikeOut", "/Stamp", "/Caret", "/Ink", "/Popup", "/Widget", "/PrinterMark", "/TrapNet", "/Watermark", "/Redact",
    ];

    private static readonly HashSet<string> Actions = ["/GoTo", "/GoToR", "/GoToE", "/Thread", "/URI", "/SubmitForm", "/Named"];

    private static readonly HashSet<string> BlendModes =
    [
        "/Normal", "/Compatible", "/Multiply", "/Screen", "/Overlay", "/Darken", "/Lighten", "/ColorDodge", "/ColorBurn",
        "/HardLight", "/SoftLight", "/Difference", "/Exclusion", "/Hue", "/Saturation", "/Color", "/Luminosity",
    ];

    /// <summary>Fails with every rule <paramref name="path"/> breaks.</summary>
    public static void Assert2b(string path)
    {
        var broken = Check(File.ReadAllBytes(path));
        Assert.True(broken.Count == 0, "Not PDF/A-2b: " + string.Join("; ", broken));
    }

    private static PdfItem? V(PdfItem? item) => item is PdfReference reference ? reference.Value : item;

    private static string Name(PdfDictionary? dictionary, string key) => dictionary is not null && V(dictionary.Elements[key]) is PdfName name ? name.Value : "";

    private static double? Number(PdfItem? item) => V(item) switch
    {
        PdfInteger i => i.Value,
        PdfReal r => r.Value,
        PdfIntegerObject i => i.Value,
        PdfRealObject r => r.Value,
        _ => null,
    };

    public static List<string> Check(byte[] bytes)
    {
        var broken = new List<string>();
        var text = Encoding.Latin1.GetString(bytes);

        // 6.1.2: the header, then a comment of four bytes above 127.
        if (!HeaderLine().IsMatch(text))
            broken.Add("6.1.2 header");
        // 6.1.3: an /ID in the (last) trailer, no /Encrypt, nothing after the last %%EOF but one end of line.
        var last = text.LastIndexOf("trailer", StringComparison.Ordinal);
        if (last < 0 || !IdEntry().IsMatch(text[last..]))
            broken.Add("6.1.3 trailer /ID");
        foreach (Match each in TrailerDictionary().Matches(text))
        {
            if (each.Value.Contains("/Encrypt", StringComparison.Ordinal))
                broken.Add("6.1.3 /Encrypt");
        }
        if (!EndOfFile().IsMatch(text))
            broken.Add("6.1.3 data after %%EOF");
        CheckStructure(text, broken);

        PdfDocument document;
        try
        {
            document = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import);
        }
        catch (PdfReaderException ex)
        {
            broken.Add("PDFsharp can't read it: " + ex.Message.Split('\n')[0]);
            return broken;
        }
        using var _ = document;
        var catalog = document.Internals.Catalog;
        CheckMetadata(document, broken);
        var components = CheckOutputIntent(catalog, broken);

        // Everything the catalog leads to.
        var dictionaries = new List<PdfDictionary>();
        var arrays = new List<PdfArray>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<PdfItem?>([catalog]);
        while (pending.TryPop(out var item))
        {
            switch (V(item))
            {
                case PdfDictionary dictionary when seen.Add(dictionary):
                    dictionaries.Add(dictionary);
                    foreach (var (_, value) in dictionary.Elements)
                        pending.Push(value);
                    break;
                case PdfArray array when seen.Add(array):
                    arrays.Add(array);
                    foreach (var value in array.Elements)
                        pending.Push(value);
                    break;
            }
        }

        if (V(catalog.Elements["/Names"]) is PdfDictionary names)
        {
            foreach (var key in new[] { "/JavaScript", "/EmbeddedFiles", "/AlternatePresentations" })
            {
                if (names.Elements.ContainsKey(key))
                    broken.Add($"names {key}");
            }
        }
        if (V(catalog.Elements["/AcroForm"]) is PdfDictionary form)
        {
            if (form.Elements.ContainsKey("/XFA"))
                broken.Add("6.4.2 XFA");
            if (V(form.Elements["/NeedAppearances"]) is PdfBoolean { Value: true })
                broken.Add("6.4.1 NeedAppearances");
        }
        if (V(catalog.Elements["/OCProperties"]) is PdfDictionary properties)
            CheckOptionalContent(properties, broken);

        var metadataStreams = 0;
        foreach (var dictionary in dictionaries)
        {
            var type = Name(dictionary, "/Type");
            var subtype = Name(dictionary, "/Subtype");
            if (dictionary.Elements.ContainsKey("/AA"))
                broken.Add($"6.5.2 /AA in {type}{subtype}");
            if (dictionary.Elements.ContainsKey("/Metadata") && !ReferenceEquals(dictionary, catalog))
                broken.Add($"metadata in {type}{subtype}");
            if (type == "/Metadata" && dictionary.Stream is not null)
                metadataStreams++;
            if (dictionary.Elements.ContainsKey("/JS") || Name(dictionary, "/S") == "/JavaScript")
                broken.Add("6.5.1 JavaScript");
            foreach (var key in new[] { "/A", "/OpenAction", "/Next" })
            {
                var actions = V(dictionary.Elements[key]) switch
                {
                    PdfDictionary { Stream: null } one when one.Elements.ContainsKey("/S") => [one],
                    PdfArray many when key == "/Next" => many.Elements.Select(V).OfType<PdfDictionary>().ToList(),
                    _ => new List<PdfDictionary>(),
                };
                foreach (var action in actions.Select(a => Name(a, "/S")))
                {
                    if (!Actions.Contains(action))
                        broken.Add($"6.5.1 action {action}");
                }
                if (actions.FirstOrDefault(a => Name(a, "/S") == "/Named") is { } named
                    && Name(named, "/N") is not ("/NextPage" or "/PrevPage" or "/FirstPage" or "/LastPage"))
                    broken.Add("6.5.1 named action");
            }
            if (type == "/Annot" || (Annotations.Contains(subtype) && dictionary.Elements.ContainsKey("/Rect")))
                CheckAnnotation(dictionary, subtype, broken);
            if (dictionary.Stream is not null)
            {
                foreach (var key in new[] { "/F", "/FFilter", "/FDecodeParms" })
                {
                    if (dictionary.Elements.ContainsKey(key))
                        broken.Add($"6.1.7.1 stream {key}");
                }
                var filters = V(dictionary.Elements["/Filter"]) switch
                {
                    PdfName one => [one.Value],
                    PdfArray many => many.Elements.Select(V).OfType<PdfName>().Select(n => n.Value).ToList(),
                    _ => new List<string>(),
                };
                if (filters.Contains("/LZWDecode") || filters.Contains("/Crypt"))
                    broken.Add("6.1.7.2 LZW or Crypt");
            }
            if (subtype == "/Image")
            {
                if (V(dictionary.Elements["/Interpolate"]) is PdfBoolean { Value: true })
                    broken.Add("6.2.8 Interpolate");
                if (dictionary.Elements.ContainsKey("/Alternates") || dictionary.Elements.ContainsKey("/OPI"))
                    broken.Add("6.2.8 Alternates or OPI");
            }
            if (subtype == "/Form" && (dictionary.Elements.ContainsKey("/OPI") || dictionary.Elements.ContainsKey("/PS")
                || dictionary.Elements.ContainsKey("/Ref") || Name(dictionary, "/Subtype2") == "/PS"))
                broken.Add("6.2.9 form XObject");
            if (subtype == "/PS")
                broken.Add("6.2.9 PostScript XObject");
            if (Name(dictionary, "/S") == "/Transparency")
                CheckGroup(dictionary, components, broken);
            if (V(dictionary.Elements["/Resources"]) is PdfDictionary resources && V(resources.Elements["/ExtGState"]) is PdfDictionary states)
            {
                foreach (var (_, state) in states.Elements)
                {
                    if (V(state) is PdfDictionary g)
                        CheckGraphicsState(g, broken);
                }
            }
        }
        if (metadataStreams != 1)
            broken.Add($"6.6.2.1 {metadataStreams} metadata streams");
        foreach (var array in arrays)
        {
            if (array.Elements.Count == 2 && V(array.Elements[0]) is PdfName { Value: "/ICCBased" } && V(array.Elements[1]) is PdfDictionary { Stream: not null } icc)
                CheckIcc(icc.Stream.UnfilteredValue, icc.Elements.GetInteger("/N"), intent: false, "6.2.4.2 ICCBased", broken);
        }

        var content = new Content(components, broken);
        content.Read(document);
        foreach (var (font, codes) in content.Fonts)
            CheckFont(font, codes, broken);
        if (components == 0 && content.Transparency)
            broken.Add("6.2.10 transparency without an output intent");

        // 6.2.4.3: device colours named in objects only of the output intent's kind (Filee names an ICCBased space
        // there instead; a reader would apply the resources' Default space only to pictures drawn with them).
        var deviceNames = dictionaries.SelectMany(d => d.Elements.Where(e => !(e.Key == "/Alternate" && d.Stream is not null)).Select(e => V(e.Value)))
            .Concat(arrays.SelectMany(a => a.Elements.Select(V)))
            .OfType<PdfName>().Select(n => n.Value).ToHashSet();
        if (deviceNames.Contains("/DeviceRGB") && components != 3)
            broken.Add("6.2.4.3 DeviceRGB object without an RGB intent");
        if (deviceNames.Contains("/DeviceCMYK") && components != 4)
            broken.Add("6.2.4.3 DeviceCMYK object without a CMYK intent");
        return [.. broken.Distinct()];
    }

    // ---- the file structure

    /// <summary>
    /// 6.1.4, 6.1.7.1, 6.1.8: the cross-reference sections from the last startxref along /Prev (incremental updates),
    /// each entry at its object, each object's syntax and each stream's length.
    /// </summary>
    private static void CheckStructure(string text, List<string> broken)
    {
        var start = StartXref().Match(text);
        if (!start.Success)
        {
            broken.Add("6.1.4 no startxref");
            return;
        }
        var offsets = new Dictionary<int, (int Offset, int Generation)>();
        var ids = new List<string>();
        var size = 0;
        var at = (long?)long.Parse(start.Groups[1].Value, CultureInfo.InvariantCulture);
        var sections = new HashSet<long>();
        while (at is { } offset)
        {
            if (offset >= text.Length || !sections.Add(offset) || sections.Count > 1000)
            {
                broken.Add("6.1.4 cross-reference offset");
                return;
            }
            var section = XrefSection().Match(text, (int)offset);
            if (!section.Success || section.Index != offset)
            {
                if (XrefStreamStart().Match(text, (int)offset) is { Success: true } stream && stream.Index == offset)
                    return; // a cross-reference stream: not checked here
                broken.Add($"6.1.4 no xref table at {offset}");
                return;
            }
            var position = section.Index + section.Length;
            while (Subsection().Match(text, position) is { Success: true } header && header.Index == position)
            {
                var first = int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture);
                var count = int.Parse(header.Groups[2].Value, CultureInfo.InvariantCulture);
                position = header.Index + header.Length;
                for (var i = 0; i < count; i++, position += 20)
                {
                    if (position + 20 > text.Length || !XrefEntry().IsMatch(text.AsSpan(position, 20)))
                    {
                        broken.Add($"6.1.4 entry {first + i} isn't 20 bytes");
                        return;
                    }
                    if (text[position + 17] == 'n')
                    {
                        offsets.TryAdd(first + i, (int.Parse(text.AsSpan(position, 10), CultureInfo.InvariantCulture),
                            int.Parse(text.AsSpan(position + 11, 5), CultureInfo.InvariantCulture)));
                    }
                }
            }
            var trailer = TrailerAt().Match(text, position);
            if (!trailer.Success || trailer.Index != position)
            {
                broken.Add($"6.1.4 no trailer after the table at {offset}");
                return;
            }
            var dictionary = trailer.Groups[1].Value;
            if (IdEntry().Match(dictionary) is { Success: true } id)
                ids.Add(id.Groups[1].Value.ToUpperInvariant());
            if (sections.Count == 1 && SizeEntry().Match(dictionary) is { Success: true } sizeEntry)
                size = int.Parse(sizeEntry.Groups[1].Value, CultureInfo.InvariantCulture);
            at = PrevEntry().Match(dictionary) is { Success: true } prev ? long.Parse(prev.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        }
        if (ids.Distinct().Count() > 1)
            broken.Add("6.1.3 the first file ID changed in an update");
        if (offsets.Count > 0 && size <= offsets.Keys.Max())
            broken.Add($"6.1.4 trailer /Size {size} for object {offsets.Keys.Max()}");

        foreach (var (number, (offset, generation)) in offsets)
        {
            if (offset >= text.Length)
            {
                broken.Add($"6.1.4 object {number} past the end");
                continue;
            }
            var head = ObjectHeader().Match(text, offset);
            if (!head.Success || head.Index != offset || head.Groups[1].Value != number.ToString(CultureInfo.InvariantCulture)
                || head.Groups[2].Value != generation.ToString(CultureInfo.InvariantCulture))
            {
                broken.Add($"6.1.4 object {number} isn't at its offset ({text.Substring(offset, Math.Min(20, text.Length - offset)).Trim()})");
                continue;
            }
            var end = text.IndexOf("endobj", offset, StringComparison.Ordinal);
            var stream = text.IndexOf("stream", offset, StringComparison.Ordinal);
            if (end < 0 || stream < 0 || stream > end)
                continue;
            // 6.1.7.1: stream, then CR LF or LF; Length bytes; an end of line; endstream.
            var data = stream + "stream".Length;
            if (text.AsSpan(data).StartsWith("\r\n"))
                data += 2;
            else if (text.AsSpan(data).StartsWith("\n"))
                data += 1;
            else
            {
                broken.Add($"6.1.7.1 stream keyword of object {number}");
                continue;
            }
            var lengthMatch = LengthEntry().Match(text[offset..stream]);
            if (!lengthMatch.Success)
                continue;
            var length = (long)int.Parse(lengthMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            if (lengthMatch.Groups[2].Success)
            {
                var target = int.Parse(lengthMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                if (!offsets.TryGetValue(target, out var lengthObject)
                    || IndirectInteger().Match(text, lengthObject.Offset) is not { Success: true } value || value.Index != lengthObject.Offset)
                {
                    broken.Add($"6.1.7.1 length of object {number}");
                    continue;
                }
                length = long.Parse(value.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            var after = data + length;
            if (after > text.Length || !EndStream().IsMatch(text.AsSpan((int)after, (int)Math.Min(12, text.Length - after))))
                broken.Add($"6.1.7.1 Length of object {number} doesn't end at endstream");
        }
    }

    // ---- metadata

    /// <summary>6.6: an XMP packet with pdfaid 2 B, unfiltered, of predefined schemas, whose properties match the document information.</summary>
    private static void CheckMetadata(PdfDocument document, List<string> broken)
    {
        if (V(document.Internals.Catalog.Elements["/Metadata"]) is not PdfDictionary { Stream: not null } metadata)
        {
            broken.Add("6.6.2 no XMP");
            return;
        }
        if (metadata.Elements.ContainsKey("/Filter"))
            broken.Add("6.6.2 filtered XMP");
        if (Name(metadata, "/Type") != "/Metadata" || Name(metadata, "/Subtype") != "/XML")
            broken.Add("6.6.2 metadata type");
        var raw = metadata.Stream.Value;
        string packet;
        try
        {
            packet = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            broken.Add("6.6.2.1 XMP isn't UTF-8");
            return;
        }
        if (!packet.StartsWith("<?xpacket begin=\"﻿\"", StringComparison.Ordinal) || packet.Contains("bytes=", StringComparison.Ordinal)
            || packet.Contains("encoding=", StringComparison.Ordinal))
            broken.Add("6.6.2.1 xpacket header");
        var start = packet.IndexOf("<x:xmpmeta", StringComparison.Ordinal);
        var end = packet.LastIndexOf("</x:xmpmeta>", StringComparison.Ordinal);
        XDocument xmp;
        try
        {
            xmp = XDocument.Parse(packet[start..(end + "</x:xmpmeta>".Length)]);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or ArgumentOutOfRangeException)
        {
            broken.Add("6.6.2 XMP doesn't parse");
            return;
        }
        // 6.6.2.3.1: properties of predefined schemas only (Filee writes no extension schemas).
        foreach (var description in xmp.Descendants(Rdf + "Description"))
        {
            var used = description.Elements().Select(e => e.Name)
                .Concat(description.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name.Namespace != Rdf && a.Name.Namespace != Xml).Select(a => a.Name));
            foreach (var property in used)
            {
                if (!Schemas.Contains(property.NamespaceName))
                    broken.Add($"6.6.2.3.1 property {property} isn't predefined");
            }
        }
        string? Property(XNamespace ns, string name)
        {
            var element = xmp.Descendants(ns + name).FirstOrDefault();
            var attribute = xmp.Descendants(Rdf + "Description").Attributes(ns + name).FirstOrDefault();
            return element?.Value ?? attribute?.Value;
        }
        if (Property(PdfAId, "part") != "2" || Property(PdfAId, "conformance") != "B")
            broken.Add("6.6.4 pdfaid");

        // 6.6.3: each Info entry equals its XMP property.
        var info = document.Info;
        void Same(string key, string? xmpValue)
        {
            if (info.Elements.ContainsKey(key) && info.Elements.GetString(key) != xmpValue)
                broken.Add($"6.6.3 {key}: '{info.Elements.GetString(key)}' vs '{xmpValue}'");
        }
        string? Alt(XNamespace ns, string name) =>
            xmp.Descendants(ns + name).Descendants(Rdf + "li").FirstOrDefault(li => (string?)li.Attribute(Xml + "lang") == "x-default")?.Value;
        Same("/Title", Alt(Dc, "title"));
        Same("/Subject", Alt(Dc, "description"));
        var creators = xmp.Descendants(Dc + "creator").Descendants(Rdf + "li").Select(li => li.Value).ToList();
        if (info.Elements.ContainsKey("/Author") && (creators.Count != 1 || creators[0] != info.Elements.GetString("/Author")))
            broken.Add("6.6.3 /Author");
        Same("/Keywords", Property(Pdf, "Keywords"));
        Same("/Creator", Property(Xmp, "CreatorTool"));
        Same("/Producer", Property(Pdf, "Producer"));
        if (info.Elements.ContainsKey("/Trapped"))
            broken.Add("6.6.3 /Trapped");
        SameDate("/CreationDate", Property(Xmp, "CreateDate"));
        SameDate("/ModDate", Property(Xmp, "ModifyDate"));

        void SameDate(string key, string? xmpValue)
        {
            if (!info.Elements.ContainsKey(key))
                return;
            var pdf = PdfDateValue().Match(info.Elements.GetString(key));
            if (!pdf.Success || xmpValue is null || !DateTimeOffset.TryParse(xmpValue, CultureInfo.InvariantCulture, DateTimeStyles.None, out var fromXmp))
            {
                broken.Add($"6.6.3 {key} format");
                return;
            }
            var offset = pdf.Groups[7].Value is "Z" or "" ? TimeSpan.Zero
                : TimeSpan.FromHours(int.Parse(pdf.Groups[8].Value, CultureInfo.InvariantCulture)) + TimeSpan.FromMinutes(int.Parse(pdf.Groups[9].Value, CultureInfo.InvariantCulture));
            if (pdf.Groups[7].Value == "-")
                offset = -offset;
            int G(int i) => int.Parse(pdf.Groups[i].Value, CultureInfo.InvariantCulture);
            var fromPdf = new DateTimeOffset(G(1), G(2), G(3), G(4), G(5), G(6), offset);
            if (fromPdf != fromXmp)
                broken.Add($"6.6.3 {key}: {fromPdf:o} vs {fromXmp:o}");
        }
    }

    // ---- colour

    /// <summary>6.2.3: GTS_PDFA1 intents with one profile (ICC version 4 at most, RGB or CMYK); its components.</summary>
    private static int CheckOutputIntent(PdfCatalog catalog, List<string> broken)
    {
        if (V(catalog.Elements["/OutputIntents"]) is not PdfArray intents)
        {
            broken.Add("6.2.3 no output intent");
            return 0;
        }
        var profiles = intents.Elements.Select(V).OfType<PdfDictionary>().Where(i => Name(i, "/S") == "/GTS_PDFA1")
            .Select(i => V(i.Elements["/DestOutputProfile"])).OfType<PdfDictionary>().Distinct(ReferenceEqualityComparer.Instance).Cast<PdfDictionary>().ToList();
        if (profiles.Count != 1)
        {
            broken.Add($"6.2.3 {profiles.Count} PDF/A profiles");
            return 0;
        }
        var icc = profiles[0].Stream!.UnfilteredValue;
        var components = icc.Length >= 20 ? Encoding.ASCII.GetString(icc, 16, 4) switch { "RGB " => 3, "CMYK" => 4, _ => 0 } : 0;
        if (components == 0)
            broken.Add("6.2.3 profile colour space");
        CheckIcc(icc, profiles[0].Elements.GetInteger("/N"), intent: true, "6.2.3 profile", broken);
        return components;
    }

    /// <summary>An ICC profile's header and tag table, and the tags its kind needs (ICC.1:2001-04, ISO 15076-1).</summary>
    internal static void CheckIcc(byte[] icc, int components, bool intent, string where, List<string> broken)
    {
        if (icc.Length < 132 || BinaryPrimitives.ReadUInt32BigEndian(icc) != icc.Length)
        {
            broken.Add($"{where}: size");
            return;
        }
        var deviceClass = Encoding.ASCII.GetString(icc, 12, 4);
        var space = Encoding.ASCII.GetString(icc, 16, 4);
        var connection = Encoding.ASCII.GetString(icc, 20, 4);
        if (icc[8] is < 2 or > 4)
            broken.Add($"{where}: version {icc[8]}");
        if (intent ? deviceClass is not ("mntr" or "prtr") : deviceClass is not ("scnr" or "mntr" or "prtr" or "spac"))
            broken.Add($"{where}: class {deviceClass}");
        var n = space switch { "GRAY" => 1, "RGB " => 3, "Lab " => 3, "CMYK" => 4, _ => 0 };
        if (n == 0 || n != components)
            broken.Add($"{where}: {space} for N {components}");
        if (connection is not ("XYZ " or "Lab ") || Encoding.ASCII.GetString(icc, 36, 4) != "acsp")
            broken.Add($"{where}: header");
        var count = BinaryPrimitives.ReadUInt32BigEndian(icc.AsSpan(128));
        if (count > 200 || 132 + 12 * count > icc.Length)
        {
            broken.Add($"{where}: tag table");
            return;
        }
        var tags = new HashSet<string>();
        for (var i = 0; i < count; i++)
        {
            var entry = 132 + 12 * i;
            var offset = BinaryPrimitives.ReadUInt32BigEndian(icc.AsSpan(entry + 4));
            var size = BinaryPrimitives.ReadUInt32BigEndian(icc.AsSpan(entry + 8));
            if (offset < 132 || (long)offset + size > icc.Length)
                broken.Add($"{where}: tag {Encoding.ASCII.GetString(icc, entry, 4)} outside the profile");
            tags.Add(Encoding.ASCII.GetString(icc, entry, 4));
        }
        var needed = new List<string> { "desc", "wtpt", "cprt" };
        var matrix = new[] { "rXYZ", "gXYZ", "bXYZ", "rTRC", "gTRC", "bTRC" };
        if (!tags.Contains("A2B0") && !(space == "RGB " && matrix.All(tags.Contains)) && !(space == "GRAY" && tags.Contains("kTRC")))
            needed.Add("A2B0");
        foreach (var tag in needed.Where(t => !tags.Contains(t)))
            broken.Add($"{where}: no {tag}");
    }

    /// <summary>6.2.10, 6.2.4.3: a transparency group's blending space keeps the device colour rules.</summary>
    private static void CheckGroup(PdfDictionary group, int components, List<string> broken)
    {
        switch (V(group.Elements["/CS"]))
        {
            case PdfName { Value: "/DeviceRGB" } when components != 3:
                broken.Add("6.2.10 group in DeviceRGB without an RGB intent");
                break;
            case PdfName { Value: "/DeviceCMYK" } when components != 4:
                broken.Add("6.2.10 group in DeviceCMYK without a CMYK intent");
                break;
            case PdfName { Value: "/DeviceGray" } when components == 0:
                broken.Add("6.2.10 group in DeviceGray without an intent");
                break;
        }
    }

    /// <summary>
    /// The content streams of the pages and of everything they draw, each read with the resources it is drawn with:
    /// device colours (6.2.4.3), inline pictures (6.1.7.2, 6.2.8), transparency (6.2.10) and the codes each font draws.
    /// </summary>
    private sealed class Content(int components, List<string> broken)
    {
        private readonly HashSet<(PdfDictionary, PdfDictionary?)> _read = [];

        /// <summary>The fonts drawn with and the codes shown with each (two bytes for Type 0 fonts).</summary>
        public Dictionary<PdfDictionary, HashSet<int>> Fonts { get; } = new(ReferenceEqualityComparer.Instance);

        public bool Transparency { get; private set; }

        public void Read(PdfDocument document)
        {
            foreach (var page in document.Pages)
            {
                var resources = Inherited(page, "/Resources") as PdfDictionary;
                var data = V(page.Elements["/Contents"]) switch
                {
                    PdfDictionary { Stream: not null } one => one.Stream.UnfilteredValue,
                    PdfArray many => [.. many.Elements.Select(V).OfType<PdfDictionary>().Where(d => d.Stream is not null).SelectMany(d => d.Stream!.UnfilteredValue.Append((byte)'\n'))],
                    _ => [],
                };
                Stream(data, resources, 0);
                if (V(page.Elements["/Annots"]) is PdfArray annotations)
                {
                    foreach (var annotation in annotations.Elements.Select(V).OfType<PdfDictionary>())
                    {
                        if (V(annotation.Elements["/AP"]) is PdfDictionary appearance)
                            Appearance(V(appearance.Elements["/N"]), 0);
                    }
                }
            }
        }

        private void Appearance(PdfItem? item, int depth)
        {
            switch (item)
            {
                case PdfDictionary { Stream: not null } form:
                    Form(form, depth);
                    break;
                case PdfDictionary states:
                    foreach (var (_, state) in states.Elements)
                    {
                        if (V(state) is PdfDictionary { Stream: not null } form)
                            Form(form, depth);
                    }
                    break;
            }
        }

        private void Form(PdfDictionary form, int depth)
        {
            var resources = V(form.Elements["/Resources"]) as PdfDictionary;
            if (depth > 16 || !_read.Add((form, resources)))
                return;
            if (V(form.Elements["/Group"]) is PdfDictionary { } group && Name(group, "/S") == "/Transparency")
                Transparency = true;
            Stream(form.Stream!.UnfilteredValue, resources, depth + 1);
        }

        private static PdfItem? Inherited(PdfDictionary page, string key)
        {
            for (PdfDictionary? node = page; node is not null; node = V(node.Elements["/Parent"]) as PdfDictionary)
            {
                if (V(node.Elements[key]) is { } value)
                    return value;
            }
            return null;
        }

        private static PdfDictionary? Resource(PdfDictionary? resources, string kind, string name) =>
            V(resources?.Elements[kind]) is PdfDictionary listed ? V(listed.Elements["/" + name]) as PdfDictionary : null;

        private bool HasDefault(PdfDictionary? resources, string key) =>
            V(resources?.Elements["/ColorSpace"]) is PdfDictionary spaces && V(spaces.Elements[key]) is PdfArray { Elements.Count: > 0 } space
            && V(space.Elements[0]) is PdfName { Value: "/ICCBased" or "/CalRGB" or "/CalGray" or "/Lab" or "/DeviceN" };

        private void Device(string space, PdfDictionary? resources)
        {
            switch (space)
            {
                case "DeviceRGB" or "RGB" when components != 3 && !HasDefault(resources, "/DefaultRGB"):
                    broken.Add("6.2.4.3 DeviceRGB in content without an RGB intent or DefaultRGB");
                    break;
                case "DeviceCMYK" or "CMYK" when components != 4 && !HasDefault(resources, "/DefaultCMYK"):
                    broken.Add("6.2.4.3 DeviceCMYK in content without a CMYK intent or DefaultCMYK");
                    break;
                case "DeviceGray" or "G" when components == 0 && !HasDefault(resources, "/DefaultGray"):
                    broken.Add("6.2.4.3 DeviceGray in content without an intent or DefaultGray");
                    break;
            }
        }

        private void Stream(byte[] data, PdfDictionary? resources, int depth)
        {
            var lexer = new Lexer(data);
            var operands = new List<object?>();
            PdfDictionary? font = null;
            try
            {
                while (lexer.Next() is { } token)
                {
                    if (token is not Lexer.Operator op)
                    {
                        operands.Add(token);
                        continue;
                    }
                    switch (op.Name)
                    {
                        case "k" or "K":
                            Device("DeviceCMYK", resources);
                            break;
                        case "rg" or "RG":
                            Device("DeviceRGB", resources);
                            break;
                        case "g" or "G":
                            Device("DeviceGray", resources);
                            break;
                        case "cs" or "CS" when operands.LastOrDefault() is Lexer.Name name:
                            Device(name.Value, resources);
                            break;
                        case "Tf" when operands.Count >= 2 && operands[^2] is Lexer.Name name:
                            font = Resource(resources, "/Font", name.Value);
                            if (font is null)
                                broken.Add($"font /{name.Value} not in the resources");
                            else if (!Fonts.ContainsKey(font))
                                Fonts[font] = [];
                            break;
                        case "Tj" or "'" or "\"" or "TJ" when font is not null:
                            var two = Name(font, "/Subtype") == "/Type0";
                            foreach (var shown in operands.SelectMany(o => o is List<object?> list ? list : [o]).OfType<byte[]>())
                            {
                                for (var i = 0; two ? i + 1 < shown.Length : i < shown.Length; i += two ? 2 : 1)
                                    Fonts[font].Add(two ? (shown[i] << 8) | shown[i + 1] : shown[i]);
                            }
                            break;
                        case "Do" when operands.LastOrDefault() is Lexer.Name name && Resource(resources, "/XObject", name.Value) is { Stream: not null } xobject:
                            if (Name(xobject, "/Subtype") == "/Form")
                                Form(xobject, depth);
                            else if (xobject.Elements.ContainsKey("/SMask") || xobject.Elements.ContainsKey("/SMaskInData"))
                                Transparency = true;
                            break;
                        case "gs" when operands.LastOrDefault() is Lexer.Name name && Resource(resources, "/ExtGState", name.Value) is { } state:
                            if (Number(state.Elements["/ca"]) is < 1 || Number(state.Elements["/CA"]) is < 1 || V(state.Elements["/SMask"]) is PdfDictionary
                                || (state.Elements.ContainsKey("/BM") && Name(state, "/BM") is not ("/Normal" or "/Compatible")))
                                Transparency = true;
                            break;
                        case "scn" or "SCN" when operands.LastOrDefault() is Lexer.Name name && Resource(resources, "/Pattern", name.Value) is { Stream: not null } pattern:
                            Form(pattern, depth);
                            break;
                        case "BI":
                            InlineImage(lexer.ReadInlineImage(_ => null).Parameters, resources);
                            break;
                    }
                    operands.Clear();
                }
            }
            catch (InvalidDataException)
            {
                broken.Add("content stream doesn't parse");
            }
        }

        private void InlineImage(Dictionary<string, object?> parameters, PdfDictionary? resources)
        {
            foreach (var (key, value) in parameters)
            {
                var names = value switch
                {
                    Lexer.Name one => [one.Value],
                    List<object?> many => many.OfType<Lexer.Name>().Select(n => n.Value).ToList(),
                    _ => new List<string>(),
                };
                switch (key)
                {
                    case "F" or "Filter" when names.Any(n => n is "LZW" or "LZWDecode"):
                        broken.Add("6.1.7.2 inline picture in LZW");
                        break;
                    case "I" or "Interpolate" when value is Lexer.Operator { Name: "true" }:
                        broken.Add("6.2.8 inline picture interpolated");
                        break;
                    case "CS" or "ColorSpace":
                        foreach (var name in names)
                            Device(name, resources);
                        break;
                }
            }
        }
    }

    // ---- annotations and graphics states

    private static void CheckAnnotation(PdfDictionary annotation, string subtype, List<string> broken)
    {
        if (!Annotations.Contains(subtype))
            broken.Add($"6.3.1 annotation {subtype}");
        var flags = V(annotation.Elements["/F"]) is PdfInteger f ? f.Value : (int?)null;
        if (subtype != "/Popup" && flags is null)
            broken.Add($"6.3.2 {subtype} without /F");
        if (flags is { } value && ((value & 4) == 0 || (value & (1 | 2 | 32 | 256)) != 0))
            broken.Add($"6.3.2 {subtype} flags {value}");
        if (subtype == "/Widget" && (annotation.Elements.ContainsKey("/A") || annotation.Elements.ContainsKey("/AA")))
            broken.Add("6.4.1 widget action");
        var rect = V(annotation.Elements["/Rect"]) as PdfArray;
        var noArea = rect is not { Elements.Count: 4 } || (rect.Elements.GetReal(0) == rect.Elements.GetReal(2) && rect.Elements.GetReal(1) == rect.Elements.GetReal(3));
        if (V(annotation.Elements["/AP"]) is not PdfDictionary appearance)
        {
            if (subtype is not ("/Link" or "/Popup") && !noArea)
                broken.Add($"6.3.3 {subtype} without appearance");
            return;
        }
        if (appearance.Elements.Keys.Any(k => k != "/N"))
            broken.Add($"6.3.3 {subtype} appearance keys {string.Join(',', appearance.Elements.Keys)}");
        var normal = V(appearance.Elements["/N"]) as PdfDictionary;
        var button = subtype == "/Widget" && FieldType(annotation) == "/Btn";
        if (normal is null || (button ? normal.Stream is not null || normal.Elements.Count == 0 : normal.Stream is null))
            broken.Add($"6.3.3 {subtype} normal appearance");
    }

    private static string FieldType(PdfDictionary widget)
    {
        for (PdfDictionary? field = widget; field is not null; field = V(field.Elements["/Parent"]) as PdfDictionary)
        {
            if (Name(field, "/FT") is { Length: > 0 } type)
                return type;
        }
        return "";
    }

    private static void CheckGraphicsState(PdfDictionary state, List<string> broken)
    {
        if (state.Elements.ContainsKey("/TR") || state.Elements.ContainsKey("/HTO"))
            broken.Add("6.2.5 TR or HTO");
        if (state.Elements.ContainsKey("/TR2") && Name(state, "/TR2") != "/Default")
            broken.Add("6.2.5 TR2");
        if (state.Elements.ContainsKey("/BM") && !BlendModes.Contains(Name(state, "/BM")))
            broken.Add("6.2.10 blend mode");
        if (V(state.Elements["/SMask"]) is PdfName { Value: not "/None" })
            broken.Add("6.2.10 SMask name");
        if (state.Elements.ContainsKey("/RI") && Name(state, "/RI") is not ("/RelativeColorimetric" or "/AbsoluteColorimetric" or "/Perceptual" or "/Saturation"))
            broken.Add("6.2.5 RI");
    }

    // ---- fonts

    private static void CheckFont(PdfDictionary font, HashSet<int> codes, List<string> broken)
    {
        var subtype = Name(font, "/Subtype");
        if (subtype == "/Type3")
            return;
        var baseFont = Name(font, "/BaseFont");
        if (font.Elements.ContainsKey("/ToUnicode")
            && (V(font.Elements["/ToUnicode"]) is not PdfDictionary { Stream: not null } map
                || !Encoding.Latin1.GetString(map.Stream.UnfilteredValue).Contains("begincmap", StringComparison.Ordinal)))
            broken.Add($"ToUnicode of {baseFont} isn't a CMap");
        var fonts = subtype == "/Type0"
            ? (V(font.Elements["/DescendantFonts"]) as PdfArray)?.Elements.Select(V).OfType<PdfDictionary>().ToList() ?? []
            : [font];
        if (fonts.Count == 0)
            broken.Add("6.2.11.4 Type 0 without a CIDFont");
        foreach (var each in fonts)
        {
            if (V(each.Elements["/FontDescriptor"]) is not PdfDictionary descriptor
                || !new[] { "/FontFile", "/FontFile2", "/FontFile3" }.Any(k => V(descriptor.Elements[k]) is PdfDictionary { Stream: not null }))
            {
                broken.Add($"6.2.11.4.1 {Name(each, "/BaseFont")} not embedded");
                continue;
            }
            if (Name(each, "/Subtype") == "/CIDFontType2" && !each.Elements.ContainsKey("/CIDToGIDMap"))
                broken.Add("6.2.11.3.2 CIDToGIDMap");
            if (V(descriptor.Elements["/FontFile2"]) is not PdfDictionary { Stream: not null } program)
                continue;
            SfntFont sfnt;
            try
            {
                sfnt = SfntFont.Read(program.Stream.UnfilteredValue);
                _ = sfnt.Require("head");
                _ = sfnt.Require("hmtx");
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException)
            {
                broken.Add($"6.2.11.4.1 the TrueType program of {Name(each, "/BaseFont")} can't be read");
                continue;
            }
            if (subtype == "/Type0")
                CheckCidTrueType(each, sfnt, codes, broken);
            else
                CheckSimpleTrueType(font, descriptor, sfnt, codes, broken);
        }
    }

    private static double Advance(SfntFont sfnt, int glyph)
    {
        var hmtx = sfnt.Require("hmtx");
        var metrics = FontTables.NumberOfMetrics(sfnt.Require("hhea"));
        var index = Math.Min(glyph, metrics - 1);
        return BinaryPrimitives.ReadUInt16BigEndian(hmtx.AsSpan(index * 4)) * 1000.0 / FontTables.UnitsPerEm(sfnt.Require("head"));
    }

    /// <summary>6.2.11.5, 6.2.11.6, 6.2.11.8 for a simple TrueType font.</summary>
    private static void CheckSimpleTrueType(PdfDictionary font, PdfDictionary descriptor, SfntFont sfnt, HashSet<int> codes, List<string> broken)
    {
        var baseFont = Name(font, "/BaseFont");
        var cmaps = sfnt["cmap"] is { } cmap ? CmapTable.ReadAll(cmap) : [];
        Dictionary<int, int>? Cmap(int platform, int encoding) => cmaps.FirstOrDefault(c => c.Platform == platform && c.Encoding == encoding).Map;
        var symbolic = (descriptor.Elements.GetInteger("/Flags") & 4) != 0;
        string?[]? names = null;
        if (symbolic)
        {
            if (font.Elements.ContainsKey("/Encoding"))
                broken.Add($"6.2.11.6 symbolic {baseFont} has an Encoding");
            if (cmaps.Count != 1 && Cmap(3, 0) is null)
                broken.Add($"6.2.11.6 symbolic {baseFont} cmap");
        }
        else
        {
            var encoding = V(font.Elements["/Encoding"]);
            var baseEncoding = encoding is PdfDictionary e ? Name(e, "/BaseEncoding") : (encoding as PdfName)?.Value;
            if (baseEncoding is not ("/WinAnsiEncoding" or "/MacRomanEncoding"))
                broken.Add($"6.2.11.6 non-symbolic {baseFont} encoding {baseEncoding}");
            var differences = encoding is PdfDictionary d && V(d.Elements["/Differences"]) is PdfArray { Elements.Count: > 0 } list ? list : null;
            if (differences is not null && (differences.Elements.Select(V).OfType<PdfName>().Any(n => SimpleEncodings.Unicode(n.Value[1..]) < 0) || Cmap(3, 1) is null))
                broken.Add($"6.2.11.6 non-symbolic {baseFont} differences");
            if (cmaps.All(c => (c.Platform, c.Encoding) == (3, 0)))
                broken.Add($"6.2.11.6 non-symbolic {baseFont} without a non-symbolic cmap");
            names = SimpleEncodings.Resolve(font, null, out _, symbolic: false);
        }
        var first = font.Elements.GetInteger("/FirstChar");
        var widths = (V(font.Elements["/Widths"]) as PdfArray)?.Elements.Select(Number).ToList();
        foreach (var code in codes.Order())
        {
            int glyph = 0;
            if (symbolic)
            {
                foreach (var key in (int[])[code, 0xF000 + code, 0xF100 + code, 0xF200 + code])
                {
                    if (Cmap(3, 0)?.TryGetValue(key, out glyph) == true)
                        break;
                }
                if (glyph == 0)
                    Cmap(1, 0)?.TryGetValue(code, out glyph);
            }
            else if (names![code] is { } name && SimpleEncodings.Unicode(name) is var u and >= 0)
            {
                Cmap(3, 1)?.TryGetValue(u, out glyph);
            }
            if (glyph <= 0)
            {
                broken.Add($"6.2.11.8 .notdef drawn with {baseFont} (code {code})");
                continue;
            }
            if (widths is not null && code - first >= 0 && code - first < widths.Count && widths[code - first] is { } width
                && Math.Abs(width - Advance(sfnt, glyph)) > 1)
                broken.Add($"6.2.11.5 width of {baseFont} code {code}: {width} vs {Advance(sfnt, glyph):0.#}");
        }
    }

    /// <summary>6.2.11.5, 6.2.11.8 for a CIDFontType2.</summary>
    private static void CheckCidTrueType(PdfDictionary cidFont, SfntFont sfnt, HashSet<int> cids, List<string> broken)
    {
        var baseFont = Name(cidFont, "/BaseFont");
        var map = V(cidFont.Elements["/CIDToGIDMap"]) is PdfDictionary { Stream: not null } stream ? stream.Stream.UnfilteredValue : null;
        var widths = new Dictionary<int, double>();
        if (V(cidFont.Elements["/W"]) is PdfArray w)
        {
            var items = w.Elements.Select(V).ToList();
            for (var i = 0; i < items.Count;)
            {
                if (Number(items[i]) is not { } start)
                    break;
                if (i + 1 < items.Count && items[i + 1] is PdfArray list)
                {
                    for (var j = 0; j < list.Elements.Count; j++)
                        widths[(int)start + j] = Number(list.Elements[j]) ?? 0;
                    i += 2;
                }
                else if (i + 2 < items.Count && Number(items[i + 1]) is { } end && Number(items[i + 2]) is { } width)
                {
                    for (var cid = (int)start; cid <= end && cid < 0x10000; cid++)
                        widths[cid] = width;
                    i += 3;
                }
                else
                {
                    break;
                }
            }
        }
        var standard = Number(cidFont.Elements["/DW"]) ?? 1000;
        foreach (var cid in cids.Order())
        {
            var glyph = map is null ? cid : 2 * cid + 1 < map.Length ? BinaryPrimitives.ReadUInt16BigEndian(map.AsSpan(2 * cid)) : 0;
            if (glyph <= 0 || glyph >= sfnt.GlyphCount)
            {
                broken.Add($"6.2.11.8 .notdef drawn with {baseFont} (CID {cid})");
                continue;
            }
            var wanted = widths.TryGetValue(cid, out var given) ? given : standard;
            if (Math.Abs(wanted - Advance(sfnt, glyph)) > 1)
                broken.Add($"6.2.11.5 width of {baseFont} CID {cid}: {wanted} vs {Advance(sfnt, glyph):0.#}");
        }
    }

    private static void CheckOptionalContent(PdfDictionary properties, List<string> broken)
    {
        var configurations = new List<PdfDictionary>();
        if (V(properties.Elements["/D"]) is PdfDictionary standard)
            configurations.Add(standard);
        if (V(properties.Elements["/Configs"]) is PdfArray others)
            configurations.AddRange(others.Elements.Select(V).OfType<PdfDictionary>());
        var names = configurations.Select(c => c.Elements.GetString("/Name")).ToList();
        if (names.Any(n => n.Length == 0) || names.Distinct().Count() != names.Count)
            broken.Add("6.9 configuration names");
        if (configurations.Any(c => c.Elements.ContainsKey("/AS")))
            broken.Add("6.9 AS");
        var groups = (V(properties.Elements["/OCGs"]) as PdfArray)?.Elements.Select(V).OfType<PdfDictionary>().ToList() ?? [];
        foreach (var configuration in configurations)
        {
            if (V(configuration.Elements["/Order"]) is not PdfArray order)
                continue;
            var listed = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var pending = new Stack<PdfItem?>(order.Elements);
            while (pending.TryPop(out var item))
            {
                switch (V(item))
                {
                    case PdfArray nested:
                        foreach (var element in nested.Elements)
                            pending.Push(element);
                        break;
                    case PdfDictionary group:
                        listed.Add(group);
                        break;
                }
            }
            if (groups.Any(g => !listed.Contains(g)))
                broken.Add("6.9 Order misses a group");
        }
    }

    [GeneratedRegex(@"\A%PDF-1\.[0-7](\r\n|\r|\n)%[\x80-\xFF]{4}")]
    private static partial Regex HeaderLine();

    [GeneratedRegex(@"/ID\s*\[\s*<([0-9A-Fa-f]+)>\s*<[0-9A-Fa-f]+>\s*\]")]
    private static partial Regex IdEntry();

    [GeneratedRegex(@"trailer\s*<<.*?>>\s*(%[^\r\n]*)?\s*startxref", RegexOptions.Singleline)]
    private static partial Regex TrailerDictionary();

    [GeneratedRegex(@"%%EOF(\r\n|\r|\n)?\z")]
    private static partial Regex EndOfFile();

    [GeneratedRegex(@"startxref(?:\r\n|\r|\n)(\d+)(?:\r\n|\r|\n)%%EOF(?:\r\n|\r|\n)?\z")]
    private static partial Regex StartXref();

    [GeneratedRegex(@"xref(?:\r\n|\r|\n)")]
    private static partial Regex XrefSection();

    [GeneratedRegex(@"\d+ \d+ obj")]
    private static partial Regex XrefStreamStart();

    [GeneratedRegex(@"(\d+) (\d+)(?:\r\n|\r|\n)")]
    private static partial Regex Subsection();

    [GeneratedRegex(@"\A\d{10} \d{5} [nf]( \r| \n|\r\n)\z")]
    private static partial Regex XrefEntry();

    [GeneratedRegex(@"trailer\s*(<<(?:(?!trailer|endobj).)*?>>)\s*(?:%[^\r\n]*\s*)?(?:startxref|xref)", RegexOptions.Singleline)]
    private static partial Regex TrailerAt();

    [GeneratedRegex(@"/Size\s+(\d+)")]
    private static partial Regex SizeEntry();

    [GeneratedRegex(@"/Prev\s+(\d+)")]
    private static partial Regex PrevEntry();

    [GeneratedRegex(@"(\d+) (\d+) obj\b")]
    private static partial Regex ObjectHeader();

    [GeneratedRegex(@"/Length(?![0-9A-Za-z])\s*(\d+)(\s+\d+\s+R)?")]
    private static partial Regex LengthEntry();

    [GeneratedRegex(@"\d+ \d+ obj\s*(\d+)\s*endobj")]
    private static partial Regex IndirectInteger();

    [GeneratedRegex(@"\A(\r\n|\r|\n)endstream")]
    private static partial Regex EndStream();

    [GeneratedRegex(@"D:(\d{4})(\d{2})(\d{2})(\d{2})(\d{2})(\d{2})(?:([Z+-])(?:(\d{2})'(\d{2})'?)?)?")]
    private static partial Regex PdfDateValue();
}
