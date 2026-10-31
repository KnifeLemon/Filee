// HWP 5.0 → HWPX: the records of a binary HWP written out as the OWPML package 한글 itself saves for the same
// document (header.xml from DocInfo, one section part per BodyText section, master pages, BinData), so HwpxReader
// reads both formats with one set of mappings and the stored line layout (linesegarray) reaches the layout engine.
// The same package is Filee's own HWP → HWPX conversion.
//
// Split over partial files:
//  * HwpToHwpx.cs          package: parts, manifest, BinData, XML helpers and the value tables both formats share;
//  * HwpToHwpx.Header.cs   DocInfo → header.xml: fonts, border fills, character and paragraph shapes, tabs,
//                          numberings, bullets, styles;
//  * HwpToHwpx.Body.cs     BodyText → section parts: paragraphs, text and controls, runs, line segments, section and
//                          column definitions, headers / footers, notes, fields, numbers, bookmarks, master pages;
//  * HwpToHwpx.Objects.cs  tables, drawing objects, pictures, groups and equations.
//
// Record layouts follow Hancom's published "한글 문서 파일 형식 5.0" document; where 한글's files differ from it (the
// document lists some arrays as all x then all y where files interleave the points, and line types without the
// leading "none"), what 한글 writes wins, checked against the HWPX 한글 saves for the same file. Values are copied, not
// interpreted, wherever OWPML has the same field: the reader does the interpreting.

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using Filee.Engines.Documents;

namespace Filee.Engines.Hwp.Binary;

/// <summary>Writes the HWPX package of an HWP 5.0 document.</summary>
internal sealed partial class HwpToHwpx
{
    private const string Hh = "http://www.hancom.co.kr/hwpml/2011/head";
    private const string Hp = "http://www.hancom.co.kr/hwpml/2011/paragraph";
    private const string Hc = "http://www.hancom.co.kr/hwpml/2011/core";
    private const string Hs = "http://www.hancom.co.kr/hwpml/2011/section";
    private const string HwpUnitChar = "http://www.hancom.co.kr/hwpml/2016/HwpUnitChar";

    /// <summary>The namespaces 한글 declares on the root of every part.</summary>
    private static readonly (string Prefix, string Uri)[] Namespaces =
    [
        ("ha", "http://www.hancom.co.kr/hwpml/2011/app"),
        ("hp", Hp),
        ("hp10", "http://www.hancom.co.kr/hwpml/2016/paragraph"),
        ("hs", Hs),
        ("hc", Hc),
        ("hh", Hh),
        ("hhs", "http://www.hancom.co.kr/hwpml/2011/history"),
        ("hm", "http://www.hancom.co.kr/hwpml/2011/master-page"),
        ("hpf", "http://www.hancom.co.kr/schema/2011/hpf"),
        ("dc", "http://purl.org/dc/elements/1.1/"),
        ("opf", "http://www.idpf.org/2007/opf/"),
        ("ooxmlchart", "http://www.hancom.co.kr/hwpml/2016/ooxmlchart"),
        ("hwpunitchar", HwpUnitChar),
        ("epub", "http://www.idpf.org/2007/ops"),
        ("config", "urn:oasis:names:tc:opendocument:xmlns:config:1.0"),
    ];

    private static readonly XmlWriterSettings Settings = new()
    {
        Encoding = new UTF8Encoding(false),
        Indent = false,
        // Text is cleaned of what XML can't hold before it is written (Clean); a damaged record must not stop the file.
        CheckCharacters = false,
    };

    private readonly HwpFile _file;
    private readonly DocumentBudget _budget;

    /// <summary>The part being written.</summary>
    private XmlWriter _x = null!;

    /// <summary>Embedded binaries by their BinData id.</summary>
    private readonly Dictionary<int, BinItem> _bins = [];

    /// <summary>Master page parts written so far: manifest id → XML.</summary>
    private readonly List<(string Id, byte[] Xml)> _masterPages = [];

    /// <summary>An embedded binary: its manifest id ("image1"), extension and how it is stored.</summary>
    private sealed record BinItem(int Id, string ItemId, string Extension, bool Compressed, bool Picture);

    private HwpToHwpx(HwpFile file)
    {
        _file = file;
        _budget = new DocumentBudget(file.Limits);
    }

    /// <summary>Writes the HWPX package of <paramref name="file"/> to <paramref name="output"/>.</summary>
    public static void Write(HwpFile file, string output)
    {
        var writer = new HwpToHwpx(file);
        var temp = output + ".tmp";
        try
        {
            using (var stream = File.Create(temp))
                writer.WritePackage(stream);
            File.Move(temp, output, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    // ───────────────────────── Package ─────────────────────────

    private void WritePackage(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

        // OCF rule: "mimetype" first and stored uncompressed.
        Text(zip, "mimetype", "application/hwp+zip", CompressionLevel.NoCompression);
        Text(zip, "version.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\" ?><hv:HCFVersion xmlns:hv=\"http://www.hancom.co.kr/hwpml/2011/version\" tagetApplication=\"WORDPROCESSOR\" major=\"5\" minor=\"1\" micro=\"1\" buildNumber=\"0\" os=\"1\" xmlVersion=\"1.5\" application=\"Filee\" appVersion=\"" + FileVersion() + "\"/>");

        var docInfo = _file.DocInfo();
        LoadBinData(docInfo);

        // header.xml names the number of sections, known once they are read: the sections go first into the zip.
        var sectionCount = 0;
        foreach (var records in _file.Sections())
        {
            using (var part = zip.CreateEntry($"Contents/section{sectionCount}.xml", CompressionLevel.Optimal).Open())
                WriteSection(part, HwpRecord.Tree(records));
            sectionCount++;
        }
        using (var part = zip.CreateEntry("Contents/header.xml", CompressionLevel.Optimal).Open())
            WriteHeader(part, docInfo, sectionCount);

        foreach (var (id, xml) in _masterPages)
        {
            using var part = zip.CreateEntry($"Contents/{id}.xml", CompressionLevel.Optimal).Open();
            part.Write(xml);
        }

        var binaries = new List<BinItem>();
        foreach (var bin in _bins.Values.OrderBy(b => b.Id))
        {
            if (_file.BinData($"BIN{bin.Id:X4}.{bin.Extension}", bin.Compressed) is not { } bytes)
                continue; // missing or damaged: pictures referring to it are left out by the reader
            using var part = zip.CreateEntry($"BinData/{bin.ItemId}.{bin.Extension.ToLowerInvariant()}", CompressionLevel.Optimal).Open();
            part.Write(bytes);
            binaries.Add(bin);
        }

        Text(zip, "Contents/content.hpf", Manifest(sectionCount, binaries));
        Text(zip, "META-INF/container.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\" ?><ocf:container xmlns:ocf=\"urn:oasis:names:tc:opendocument:xmlns:container\" xmlns:hpf=\"http://www.hancom.co.kr/schema/2011/hpf\"><ocf:rootfiles><ocf:rootfile full-path=\"Contents/content.hpf\" media-type=\"application/hwpml-package+xml\"/></ocf:rootfiles></ocf:container>");
        Text(zip, "META-INF/manifest.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\" ?><odf:manifest xmlns:odf=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\"/>");
        Text(zip, "META-INF/container.rdf", Rdf(sectionCount));
        Text(zip, "settings.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\" ?><ha:HWPApplicationSetting xmlns:ha=\"http://www.hancom.co.kr/hwpml/2011/app\" xmlns:config=\"urn:oasis:names:tc:opendocument:xmlns:config:1.0\"><ha:CaretPosition listIDRef=\"0\" paraIDRef=\"0\" pos=\"0\"/></ha:HWPApplicationSetting>");
    }

    /// <summary>content.hpf: the parts and the reading order (header, then the sections).</summary>
    private string Manifest(int sections, List<BinItem> binaries)
    {
        var items = new StringBuilder("<opf:item id=\"header\" href=\"Contents/header.xml\" media-type=\"application/xml\"/>");
        foreach (var bin in binaries)
        {
            var embedded = bin.Picture ? "1" : "0";
            items.Append(CultureInfo.InvariantCulture, $"<opf:item id=\"{bin.ItemId}\" href=\"BinData/{bin.ItemId}.{Escape(bin.Extension.ToLowerInvariant())}\" media-type=\"{MediaType(bin.Extension)}\" isEmbeded=\"{embedded}\"/>");
        }
        var spine = new StringBuilder("<opf:itemref idref=\"header\" linear=\"yes\"/>");
        for (var i = 0; i < sections; i++)
        {
            items.Append(CultureInfo.InvariantCulture, $"<opf:item id=\"section{i}\" href=\"Contents/section{i}.xml\" media-type=\"application/xml\"/>");
            spine.Append(CultureInfo.InvariantCulture, $"<opf:itemref idref=\"section{i}\" linear=\"yes\"/>");
        }
        foreach (var (id, _) in _masterPages)
            items.Append(CultureInfo.InvariantCulture, $"<opf:item id=\"{id}\" href=\"Contents/{id}.xml\" media-type=\"application/xml\"/>");
        items.Append("<opf:item id=\"settings\" href=\"settings.xml\" media-type=\"application/xml\"/>");

        var declarations = string.Concat(Namespaces.Select(n => $" xmlns:{n.Prefix}=\"{n.Uri}\""));
        var title = _file.Title() is { } text ? $"<opf:title>{Escape(text)}</opf:title>" : "<opf:title/>";
        // rhwp keeps the HWPX it converted in a stream of its own; the line layout it stores is its estimate, not 한글's.
        if (_file.MadeByRhwp)
            title += $"<opf:meta name=\"{Hwpx.HwpxReader.EstimatedLinesMeta}\" content=\"estimated\"/>";
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\" ?><opf:package{declarations} version=\"\" unique-identifier=\"\" id=\"\"><opf:metadata>{title}<opf:language>ko</opf:language></opf:metadata><opf:manifest>{items}</opf:manifest><opf:spine>{spine}</opf:spine></opf:package>";
    }

    private static string Rdf(int sections)
    {
        var parts = new StringBuilder("<rdf:Description rdf:about=\"\"><ns0:hasPart xmlns:ns0=\"http://www.hancom.co.kr/hwpml/2016/meta/pkg#\" rdf:resource=\"Contents/header.xml\"/></rdf:Description><rdf:Description rdf:about=\"Contents/header.xml\"><rdf:type rdf:resource=\"http://www.hancom.co.kr/hwpml/2016/meta/pkg#HeaderFile\"/></rdf:Description>");
        for (var i = 0; i < sections; i++)
        {
            parts.Append(CultureInfo.InvariantCulture, $"<rdf:Description rdf:about=\"\"><ns0:hasPart xmlns:ns0=\"http://www.hancom.co.kr/hwpml/2016/meta/pkg#\" rdf:resource=\"Contents/section{i}.xml\"/></rdf:Description>");
            parts.Append(CultureInfo.InvariantCulture, $"<rdf:Description rdf:about=\"Contents/section{i}.xml\"><rdf:type rdf:resource=\"http://www.hancom.co.kr/hwpml/2016/meta/pkg#SectionFile\"/></rdf:Description>");
        }
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\" ?><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">{parts}<rdf:Description rdf:about=\"\"><rdf:type rdf:resource=\"http://www.hancom.co.kr/hwpml/2016/meta/pkg#Document\"/></rdf:Description></rdf:RDF>";
    }

    private static string MediaType(string extension) => extension.ToLowerInvariant() switch
    {
        "png" => "image/png",
        "jpg" or "jpeg" => "image/jpeg",
        "gif" => "image/gif",
        "bmp" => "image/bmp",
        "tif" or "tiff" => "image/tiff",
        "wmf" => "image/x-wmf",
        "emf" => "image/x-emf",
        "svg" => "image/svg+xml",
        "ole" => "application/ole",
        _ => "application/octet-stream",
    };

    private static string FileVersion() =>
        typeof(HwpToHwpx).Assembly.GetName().Version?.ToString() ?? "0";

    private static void Text(ZipArchive zip, string name, string text, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, level).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    /// <summary>Starts a part: the XML declaration and the root element with every namespace 한글 declares.</summary>
    private XmlWriter StartPart(Stream stream, string prefix, string localName, string ns)
    {
        var writer = XmlWriter.Create(stream, Settings);
        writer.WriteStartDocument(standalone: true);
        writer.WriteStartElement(prefix, localName, ns);
        foreach (var (p, uri) in Namespaces)
            writer.WriteAttributeString("xmlns", p, null, uri);
        return writer;
    }

    // ───────────────────────── XML helpers ─────────────────────────

    private void Start(string ns, string name) => _x.WriteStartElement(Prefix(ns), name, ns);

    private void End() => _x.WriteEndElement();

    private void Attr(string name, string value) => _x.WriteAttributeString(name, Clean(value));

    private void Attr(string name, long value) => _x.WriteAttributeString(name, value.ToString(CultureInfo.InvariantCulture));

    private void Attr(string name, bool value) => _x.WriteAttributeString(name, value ? "1" : "0");

    private void Attr(string name, double value) =>
        _x.WriteAttributeString(name, Math.Round(value, 6).ToString("0.######", CultureInfo.InvariantCulture));

    /// <summary>An element with attributes only.</summary>
    private void Empty(string ns, string name, params (string Name, object Value)[] attributes)
    {
        Start(ns, name);
        foreach (var (attribute, value) in attributes)
        {
            switch (value)
            {
                case string s:
                    Attr(attribute, s);
                    break;
                case bool b:
                    Attr(attribute, b);
                    break;
                case double d:
                    Attr(attribute, d);
                    break;
                case IConvertible c:
                    Attr(attribute, c.ToInt64(CultureInfo.InvariantCulture));
                    break;
            }
        }
        End();
    }

    private static string Prefix(string ns) => ns switch
    {
        Hh => "hh",
        Hp => "hp",
        Hc => "hc",
        Hs => "hs",
        _ => "hp",
    };

    /// <summary>Text without what XML 1.0 can't hold: control characters and lone surrogates.</summary>
    internal static string Clean(string text)
    {
        StringBuilder? clean = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var ok = c is '\t' or '\n' or '\r' || (c >= 0x20 && c != 0xFFFE && c != 0xFFFF);
            if (ok && char.IsHighSurrogate(c))
                ok = i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
            else if (ok && char.IsLowSurrogate(c))
                ok = i > 0 && char.IsHighSurrogate(text[i - 1]);
            if (ok && clean is null)
                continue;
            clean ??= new StringBuilder(text, 0, i, text.Length);
            if (ok)
                clean.Append(c);
        }
        return clean?.ToString() ?? text;
    }

    private static string Escape(string text) => System.Security.SecurityElement.Escape(Clean(text)) ?? "";

    // ───────────────────────── Shared values ─────────────────────────

    /// <summary>A COLORREF (0x00BBGGRR) as "#RRGGBB"; 0xFFFFFFFF is "none" and a set alpha byte gives "#AARRGGBB".</summary>
    private static string Color(uint value)
    {
        if (value == 0xFFFFFFFF)
            return "none";
        var rgb = $"{value & 0xFF:X2}{(value >> 8) & 0xFF:X2}{(value >> 16) & 0xFF:X2}";
        return value >> 24 == 0 ? "#" + rgb : $"#{value >> 24:X2}{rgb}";
    }

    /// <summary>Line types of borders and drawing outlines: 0 is none, then the OWPML line types in order.</summary>
    private static string LineType(int value) => value switch
    {
        0 => "NONE",
        _ => LineShape(value - 1),
    };

    /// <summary>
    /// Line shapes of underlines, strikeouts and separators: the OWPML line types without "none". Dotted comes before
    /// dashed, unlike OWPML's list (and the order Hancom's document gives): 한글 saves a dotted border, tab leader or
    /// outline as 2 and a dashed one as 3 in HWP and writes them as DOT and DASH in HWPX.
    /// </summary>
    private static string LineShape(int value) => value switch
    {
        0 => "SOLID",
        1 => "DOT",
        2 => "DASH",
        3 => "DASH_DOT",
        4 => "DASH_DOT_DOT",
        5 => "LONG_DASH",
        6 => "CIRCLE",
        7 => "DOUBLE_SLIM",
        8 => "SLIM_THICK",
        9 => "THICK_SLIM",
        10 => "SLIM_THICK_SLIM",
        11 => "WAVE",
        12 => "DOUBLE_WAVE",
        13 => "THICK_3D",
        14 => "THICK_3D_REVERS_LIGHTING",
        15 => "3D",
        16 => "3D_REVERS_LIGHTING",
        _ => "SOLID",
    };

    private static readonly string[] BorderWidths =
        ["0.1 mm", "0.12 mm", "0.15 mm", "0.2 mm", "0.25 mm", "0.3 mm", "0.4 mm", "0.5 mm", "0.6 mm", "0.7 mm", "1.0 mm", "1.5 mm", "2.0 mm", "3.0 mm", "4.0 mm", "5.0 mm"];

    private static string BorderWidth(int value) => BorderWidths[Math.Clamp(value, 0, BorderWidths.Length - 1)];

    /// <summary>Number formats of numberings, notes and page numbers.</summary>
    private static string NumberFormat(int value) => value switch
    {
        0 => "DIGIT",
        1 => "CIRCLED_DIGIT",
        2 => "ROMAN_CAPITAL",
        3 => "ROMAN_SMALL",
        4 => "LATIN_CAPITAL",
        5 => "LATIN_SMALL",
        6 => "CIRCLED_LATIN_CAPITAL",
        7 => "CIRCLED_LATIN_SMALL",
        8 => "HANGUL_SYLLABLE",
        9 => "CIRCLED_HANGUL_SYLLABLE",
        10 => "HANGUL_JAMO",
        11 => "CIRCLED_HANGUL_JAMO",
        12 => "HANGUL_PHONETIC",
        13 => "IDEOGRAPH",
        14 => "CIRCLED_IDEOGRAPH",
        15 => "DECAGON_CIRCLE",
        16 => "DECAGON_CIRCLE_HANJA",
        0x80 => "SYMBOL",
        0x81 => "USER_CHAR",
        _ => "DIGIT",
    };

    /// <summary>A single character field (prefix, suffix, user symbol); the null character is no character.</summary>
    private static string Char(ushort value) => value == 0 ? "" : Clean(((char)value).ToString());

    /// <summary>A control id ("secd", "tbl ") from its packed form.</summary>
    private static string ControlId(uint id) =>
        new([(char)((id >> 24) & 0xFF), (char)((id >> 16) & 0xFF), (char)((id >> 8) & 0xFF), (char)(id & 0xFF)]);
}
