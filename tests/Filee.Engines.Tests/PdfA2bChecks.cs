// A structural check of the PDF/A-2b rules Filee's own PDF/A writer keeps (Pdf/PdfAConformance.cs, PdfAMetadata.cs),
// for tests: veraPDF isn't run here. Each rule is named by its clause of ISO 19005-2. It reads the file twice, with
// PDFsharp for the objects and as bytes for the header, the trailers and the end of the file, and gives back the
// rules broken, so a failing test says all of them at once.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Tests;

internal static partial class PdfA2bChecks
{
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Xmp = "http://ns.adobe.com/xap/1.0/";
    private static readonly XNamespace Pdf = "http://ns.adobe.com/pdf/1.3/";
    private static readonly XNamespace PdfAId = "http://www.aiim.org/pdfa/ns/id/";
    private static readonly XNamespace Xml = "http://www.w3.org/XML/1998/namespace";

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

    private static string Name(PdfDictionary dictionary, string key) => V(dictionary.Elements[key]) is PdfName name ? name.Value : "";

    public static List<string> Check(byte[] bytes)
    {
        var broken = new List<string>();
        var text = Encoding.Latin1.GetString(bytes);

        // 6.1.2: the header, then a comment of four bytes above 127.
        if (!HeaderLine().IsMatch(text))
            broken.Add("6.1.2 header");
        // 6.1.3: an /ID in the (last) trailer, no /Encrypt, nothing after the last %%EOF but one end of line.
        var trailer = text[text.LastIndexOf("trailer", StringComparison.Ordinal)..];
        if (!IdEntry().IsMatch(trailer))
            broken.Add("6.1.3 trailer /ID");
        foreach (Match each in TrailerDictionary().Matches(text))
        {
            if (each.Value.Contains("/Encrypt", StringComparison.Ordinal))
                broken.Add("6.1.3 /Encrypt");
        }
        if (!EndOfFile().IsMatch(text))
            broken.Add("6.1.3 data after %%EOF");

        using var document = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import);
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

        var fonts = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var dictionary in dictionaries)
        {
            var type = Name(dictionary, "/Type");
            var subtype = Name(dictionary, "/Subtype");
            if (dictionary.Elements.ContainsKey("/AA"))
                broken.Add($"6.5.2 /AA in {type}{subtype}");
            if (dictionary.Elements.ContainsKey("/Metadata") && !ReferenceEquals(dictionary, catalog))
                broken.Add($"metadata in {type}{subtype}");
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
            if (V(dictionary.Elements["/Resources"]) is PdfDictionary resources)
            {
                if (V(resources.Elements["/Font"]) is PdfDictionary listed)
                {
                    foreach (var (_, font) in listed.Elements)
                    {
                        if (V(font) is PdfDictionary f)
                            fonts.Add(f);
                    }
                }
                if (V(resources.Elements["/ExtGState"]) is PdfDictionary states)
                {
                    foreach (var (_, state) in states.Elements)
                    {
                        if (V(state) is PdfDictionary g)
                            CheckGraphicsState(g, broken);
                    }
                }
            }
        }
        foreach (var font in fonts)
            CheckFont(font, broken);

        // 6.2.4.3: device colours only of the output intent's kind (gray goes with either).
        var deviceNames = dictionaries.SelectMany(d => d.Elements.Where(e => !(e.Key == "/Alternate" && d.Stream is not null)).Select(e => V(e.Value)))
            .Concat(arrays.SelectMany(a => a.Elements.Select(V)))
            .OfType<PdfName>().Select(n => n.Value).ToHashSet();
        var content = string.Concat(dictionaries.Where(d => d.Stream is not null && (Name(d, "/Subtype") == "/Form" || Name(d, "/Type") == "/XObject" && Name(d, "/Subtype") != "/Image"))
            .Concat(document.Pages.Cast<PdfPage>().SelectMany(p => V(p.Elements["/Contents"]) switch
            {
                PdfDictionary one => [one],
                PdfArray many => many.Elements.Select(V).OfType<PdfDictionary>(),
                _ => [],
            }))
            .Select(d => Encoding.Latin1.GetString(d.Stream!.UnfilteredValue)));
        var rgb = deviceNames.Contains("/DeviceRGB") || RgbOperator().IsMatch(content) || content.Contains("/DeviceRGB", StringComparison.Ordinal);
        var cmyk = deviceNames.Contains("/DeviceCMYK") || CmykOperator().IsMatch(content) || content.Contains("/DeviceCMYK", StringComparison.Ordinal);
        if (rgb && components != 3)
            broken.Add("6.2.4.3 DeviceRGB without an RGB intent");
        if (cmyk && components != 4)
            broken.Add("6.2.4.3 DeviceCMYK without a CMYK intent");
        return broken;
    }

    /// <summary>6.6: an XMP packet with pdfaid 2 B, unfiltered, whose properties match the document information.</summary>
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
        var packet = Encoding.UTF8.GetString(metadata.Stream.Value);
        if (!packet.StartsWith("<?xpacket begin=\"\uFEFF\"", StringComparison.Ordinal) || packet.Contains("bytes=", StringComparison.Ordinal)
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
        var space = Encoding.ASCII.GetString(icc, 16, 4);
        var components = space == "RGB " ? 3 : space == "CMYK" ? 4 : 0;
        if (icc[8] > 4 || Encoding.ASCII.GetString(icc, 12, 4) is not ("mntr" or "prtr") || Encoding.ASCII.GetString(icc, 36, 4) != "acsp"
            || components == 0 || profiles[0].Elements.GetInteger("/N") != components)
            broken.Add("6.2.3 profile");
        return components;
    }

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
        var area = rect is { Elements.Count: 4 } && rect.Elements.GetReal(0) != rect.Elements.GetReal(2) && rect.Elements.GetReal(1) != rect.Elements.GetReal(3);
        if (V(annotation.Elements["/AP"]) is not PdfDictionary appearance)
        {
            if (subtype is not ("/Link" or "/Popup") && area)
                broken.Add($"6.3.3 {subtype} without appearance");
            return;
        }
        if (appearance.Elements.Keys.Any(k => k != "/N"))
            broken.Add($"6.3.3 {subtype} appearance keys {string.Join(',', appearance.Elements.Keys)}");
        var normal = V(appearance.Elements["/N"]) as PdfDictionary;
        var button = subtype == "/Widget" && FieldType(annotation) == "/Btn";
        if (normal is null || (button ? normal.Stream is not null : normal.Stream is null))
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
        if (state.Elements.ContainsKey("/RI") && Name(state, "/RI") is not ("/RelativeColorimetric" or "/AbsoluteColorimetric" or "/Perceptual" or "/Saturation"))
            broken.Add("6.2.5 RI");
    }

    private static void CheckFont(PdfDictionary font, List<string> broken)
    {
        var subtype = Name(font, "/Subtype");
        if (subtype == "/Type3")
            return;
        var fonts = subtype == "/Type0"
            ? (V(font.Elements["/DescendantFonts"]) as PdfArray)?.Elements.Select(V).OfType<PdfDictionary>().ToList() ?? []
            : [font];
        if (fonts.Count == 0)
            broken.Add("6.2.11.4 Type 0 without a CIDFont");
        foreach (var each in fonts)
        {
            if (V(each.Elements["/FontDescriptor"]) is not PdfDictionary descriptor
                || !new[] { "/FontFile", "/FontFile2", "/FontFile3" }.Any(k => V(descriptor.Elements[k]) is PdfDictionary { Stream: not null }))
                broken.Add($"6.2.11.4.1 {Name(each, "/BaseFont")} not embedded");
            if (Name(each, "/Subtype") == "/CIDFontType2" && !each.Elements.ContainsKey("/CIDToGIDMap"))
                broken.Add("6.2.11.3.2 CIDToGIDMap");
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

    [GeneratedRegex(@"/ID\s*\[\s*<[0-9A-Fa-f]+>\s*<[0-9A-Fa-f]+>\s*\]")]
    private static partial Regex IdEntry();

    [GeneratedRegex(@"trailer\s*<<.*?>>\s*(%[^\r\n]*)?\s*startxref", RegexOptions.Singleline)]
    private static partial Regex TrailerDictionary();

    [GeneratedRegex(@"%%EOF(\r\n|\r|\n)?\z")]
    private static partial Regex EndOfFile();

    [GeneratedRegex(@"D:(\d{4})(\d{2})(\d{2})(\d{2})(\d{2})(\d{2})(?:([Z+-])(?:(\d{2})'(\d{2})'?)?)?")]
    private static partial Regex PdfDateValue();

    [GeneratedRegex(@"(^|\s)[-.0-9]+\s+[-.0-9]+\s+[-.0-9]+\s+(rg|RG)(\s|$)", RegexOptions.Multiline)]
    private static partial Regex RgbOperator();

    [GeneratedRegex(@"(^|\s)[-.0-9]+\s+[-.0-9]+\s+[-.0-9]+\s+[-.0-9]+\s+(k|K)(\s|$)", RegexOptions.Multiline)]
    private static partial Regex CmykOperator();
}
