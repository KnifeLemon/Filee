// HWPX → HWP 5.0: an OWPML package (as HwpxWriter writes it, or as 한글 saves it) written out as the binary document
// 한글 saves for the same file. It is the inverse of HwpToHwpx: every element that HwpToHwpx writes from a record is
// turned back into that record, field by field, so Filee's HWP reader reads the file into the same model as the HWPX,
// and the fields HwpToHwpx doesn't read get the values 한글 writes (checked against the HWP 한글 saves from the same
// HWPX). Anything → HWP goes through the HWPX writer first.
//
// Split over partial files:
//  * HwpxToHwp.cs          package: parts, FileHeader, summary information, previews, BinData, the compound file, XML
//                          helpers and the value tables;
//  * HwpxToHwp.Header.cs   header.xml → DocInfo;
//  * HwpxToHwp.Body.cs     section parts → BodyText records: paragraphs, text and controls, section and column
//                          definitions, headers / footers, notes, fields, numbers, bookmarks, master pages;
//  * HwpxToHwp.Objects.cs  tables, drawing objects, pictures, groups and equations.
//
// Record layouts follow Hancom's published "한글 문서 파일 형식 5.0" document, and where 한글's files differ from it what
// 한글 writes wins (see HwpToHwpx). The package is read within DocumentLimits like any HWPX (DocumentPackage, SafeXml),
// and its ids are looked up, never trusted: an id that names nothing becomes the first item of its kind.

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Filee.Engines.Documents;

namespace Filee.Engines.Hwp.Binary;

/// <summary>Writes the HWP 5.0 document of an HWPX package.</summary>
internal sealed partial class HwpxToHwp
{
    /// <summary>The format version written: 5.1.1.0, as 한글 2018 and later save.</summary>
    internal const uint FormatVersion = 0x05010100;

    private const string HwpUnitCharNamespace = "HwpUnitChar";
    private const string Paragraph2016Namespace = "http://www.hancom.co.kr/hwpml/2016/paragraph";

    private readonly DocumentPackage _package;
    private readonly DocumentBudget _budget;

    /// <summary>Manifest item id → package entry.</summary>
    private readonly Dictionary<string, string> _items = new(StringComparer.Ordinal);

    /// <summary>The line layouts the package stores are an estimate (FileeLineSegments): they are left out.</summary>
    private bool _estimatedLines;

    /// <summary>Embedded binaries in the order of their BIN_DATA records (the 1-based index is their id).</summary>
    private readonly List<BinItem> _bins = [];

    /// <summary>Manifest item id → index into <see cref="_bins"/> + 1 (0 when it can't be written).</summary>
    private readonly Dictionary<string, int> _binIds = new(StringComparer.Ordinal);

    /// <summary>An embedded binary: the package entry, the stream's extension and whether it is an OLE storage.</summary>
    private sealed record BinItem(string Entry, string Extension, bool Ole);

    private HwpxToHwp(DocumentPackage package)
    {
        _package = package;
        _budget = new DocumentBudget(package.Limits);
    }

    /// <summary>Writes the HWP document of the HWPX package at <paramref name="hwpx"/> to <paramref name="output"/>.</summary>
    public static void Write(string hwpx, string output, DocumentLimits? limits = null)
    {
        using var package = DocumentPackage.Open(hwpx, limits ?? DocumentLimits.Default, () => NotAPackage(hwpx));
        var writer = new HwpxToHwp(package);
        var file = writer.Build();
        var temp = output + ".tmp";
        try
        {
            using (var stream = File.Create(temp))
                file.Write(stream);
            File.Move(temp, output, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static InvalidDataException NotAPackage(string path)
    {
        var header = new byte[8];
        using (var stream = File.OpenRead(path))
            _ = stream.Read(header);
        return header is [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]
            ? new InvalidDataException("The file is already an HWP 5.0 (binary) document, not HWPX.")
            : new InvalidDataException("The file is not an HWPX package. It may be damaged or protected by document security (DRM) software.");
    }

    // ───────────────────────── Package ─────────────────────────

    private CompoundFileWriter Build()
    {
        if (Descendants(LoadXml("META-INF/manifest.xml", required: false)?.Root, "encryption-data").Any())
            throw new InvalidDataException("The HWPX file is password-protected. Open it in 한글 (Hangul), remove the password, save it and convert it again.");

        var container = LoadXml("META-INF/container.xml", required: false);
        var rootFiles = Descendants(container?.Root, "rootfile").ToList();
        var hpfPath = Attr(rootFiles.FirstOrDefault(r => Attr(r, "media-type") == "application/hwpml-package+xml")
                           ?? rootFiles.FirstOrDefault(r => Attr(r, "full-path")?.EndsWith(".hpf", StringComparison.OrdinalIgnoreCase) == true), "full-path")
                      ?? "Contents/content.hpf";
        var hpf = LoadXml(hpfPath, required: false);
        var folder = hpfPath.Contains('/') ? hpfPath[..(hpfPath.LastIndexOf('/') + 1)] : "";
        foreach (var item in Descendants(hpf?.Root, "item"))
        {
            if (Attr(item, "id") is { Length: > 0 } id && Attr(item, "href") is { Length: > 0 } href)
                _items[id] = EntryPath(href, folder);
        }
        _estimatedLines = Descendants(hpf?.Root, "meta").Any(m => Attr(m, "name") == Hwpx.HwpxReader.EstimatedLinesMeta && (Attr(m, "content") ?? m.Value) == "estimated");

        var headerPath = _items.Values.FirstOrDefault(p => p.EndsWith("header.xml", StringComparison.OrdinalIgnoreCase)) ?? "Contents/header.xml";
        var head = LoadXml(headerPath, required: true)!.Root!;
        LoadHeader(head);

        var sections = new List<byte[]>();
        var text = new StringBuilder();
        foreach (var path in SectionPaths(hpf))
        {
            var root = LoadXml(path, required: true)!.Root!;
            sections.Add(Section(root, text));
        }
        if (sections.Count == 0)
            throw new InvalidDataException("The HWPX file has no sections (Contents/section0.xml is missing).");

        var title = Descendants(hpf?.Root, "title").FirstOrDefault()?.Value is { } t && !string.IsNullOrWhiteSpace(t) ? t.Trim() : null;
        var docInfo = DocInfo(head, sections.Count);

        var file = new CompoundFileWriter();
        file.Add("FileHeader", FileHeader());
        file.Add("DocInfo", Deflate(docInfo));
        for (var i = 0; i < sections.Count; i++)
            file.Add($"BodyText/Section{i}", Deflate(sections[i]));
        file.Add("\u0005HwpSummaryInformation", SummaryInformation(title));
        file.Add("PrvText", PreviewText(text));
        if (PreviewImage() is { } image)
            file.Add("PrvImage", image);
        for (var i = 0; i < _bins.Count; i++)
        {
            var bin = _bins[i];
            if (Read(bin.Entry) is { } bytes)
                file.Add($"BinData/BIN{i + 1:X4}.{bin.Extension}", Deflate(bytes));
        }
        // What 한글 adds to every file: no linked documents, script version 1 and no document script.
        file.Add("DocOptions/_LinkDoc", new byte[524]);
        file.Add("Scripts/JScriptVersion", Deflate([1, 0, 0, 0, 0, 0, 0, 0]));
        file.Add("Scripts/DefaultJScript", Deflate([.. new byte[16], 0xFF, 0xFF, 0xFF, 0xFF]));
        return file;
    }

    /// <summary>Section parts in reading order: the spine, else Contents/sectionN.xml by number.</summary>
    private List<string> SectionPaths(XDocument? hpf)
    {
        static bool IsSection(string path) =>
            Path.GetFileName(path) is var name && name.StartsWith("section", StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

        var result = new List<string>();
        foreach (var itemRef in Descendants(hpf?.Root, "itemref"))
        {
            if (_items.TryGetValue(Attr(itemRef, "idref") ?? "", out var path) && IsSection(path) && !result.Contains(path))
                result.Add(path);
        }
        if (result.Count > 0)
            return result;
        return _package.Entries
            .Where(e => e.FullName.StartsWith("Contents/", StringComparison.OrdinalIgnoreCase) && IsSection(e.FullName))
            .OrderBy(e => int.TryParse(Path.GetFileNameWithoutExtension(e.Name).AsSpan(7), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue)
            .Select(e => e.FullName)
            .ToList();
    }

    private string EntryPath(string href, string folder)
    {
        var path = Uri.UnescapeDataString(href).Replace('\\', '/').TrimStart('/');
        if (_package.GetEntry(path) is not null)
            return path;
        return folder.Length > 0 && _package.GetEntry(folder + path) is not null ? folder + path : path;
    }

    private XDocument? LoadXml(string path, bool required)
    {
        var entry = _package.GetEntry(path);
        if (entry is null)
            return required ? throw new InvalidDataException($"The HWPX file is incomplete: {path} is missing.") : null;
        try
        {
            return _package.LoadXml(entry, preserveWhitespace: true);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"The HWPX file cannot be read ({path} is not valid XML). It may be encrypted or damaged. {ex.Message}", ex);
        }
    }

    /// <summary>The bytes of a package entry within the part limit; null when it is missing.</summary>
    private byte[]? Read(string path) =>
        _package.GetEntry(path) is { } entry ? _package.ReadBytes(entry, _package.Limits.MaxPartBytes) : null;

    // ───────────────────────── Binaries ─────────────────────────

    /// <summary>
    /// The BIN_DATA id (1-based) of a manifest item, registered on first use; 0 when the package doesn't have it.
    /// Without a manifest entry, 한글's naming (BinData/&lt;id&gt;.&lt;ext&gt;) still finds the file.
    /// </summary>
    private int BinId(string? itemId)
    {
        if (string.IsNullOrEmpty(itemId))
            return 0;
        if (_binIds.TryGetValue(itemId, out var known))
            return known;
        var entry = _items.TryGetValue(itemId, out var path) && _package.GetEntry(path) is not null
            ? path
            : _package.Entries.FirstOrDefault(e => e.FullName.StartsWith("BinData/", StringComparison.OrdinalIgnoreCase) &&
                                                  string.Equals(Path.GetFileNameWithoutExtension(e.Name), itemId, StringComparison.OrdinalIgnoreCase))?.FullName;
        var id = 0;
        if (entry is not null && _bins.Count < 0xFFFF)
        {
            var extension = new string(Path.GetExtension(entry).TrimStart('.').Where(char.IsAsciiLetterOrDigit).Take(8).ToArray());
            var ole = extension.Equals("ole", StringComparison.OrdinalIgnoreCase);
            _bins.Add(new BinItem(entry, extension.Length > 0 ? extension : "bin", ole));
            id = _bins.Count;
        }
        return _binIds[itemId] = id;
    }

    // ───────────────────────── Fixed streams ─────────────────────────

    /// <summary>FileHeader: the signature, the version, compressed (bit 0) and the encryption version 한글 writes (4).</summary>
    private static byte[] FileHeader()
    {
        var header = new byte[256];
        Encoding.ASCII.GetBytes("HWP Document File").CopyTo(header, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(32), FormatVersion);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44), 4);
        return header;
    }

    /// <summary>
    /// Raw deflate (no zlib header), as HWP compresses its streams, followed like 한글's by the CRC-32 and the length of
    /// what it holds (readers stop at the deflate stream's end).
    /// </summary>
    internal static byte[] Deflate(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(bytes);
        output.Write(new RecordData().U32(Archives.Crc32.Compute(bytes)).U32((uint)bytes.Length).ToArray());
        return output.ToArray();
    }

    /// <summary>
    /// \005HwpSummaryInformation: an OLE property set (byte order mark, version, the HWP format id) with one section
    /// holding the code page and the title (a UTF-16 string, as 한글 writes it).
    /// </summary>
    internal static byte[] SummaryInformation(string? title)
    {
        // {9FA2B660-1061-11D4-B4C6-006097C09D8C}: 한글's summary information format id.
        ReadOnlySpan<byte> format = [0x60, 0xB6, 0xA2, 0x9F, 0x61, 0x10, 0xD4, 0x11, 0xB4, 0xC6, 0x00, 0x60, 0x97, 0xC0, 0x9D, 0x8C];
        var properties = new List<(uint Id, byte[] Value)>
        {
            (1, new RecordData().U32(2).U16(1200).U16(0).ToArray()), // code page: UTF-16 (VT_I2)
        };
        var text = (title ?? "") + "\0";
        var value = new RecordData().U32(0x1F).U32((uint)text.Length).Chars(text);
        if (value.Length % 4 != 0)
            value.Zeros(4 - value.Length % 4);
        properties.Add((2, value.ToArray()));

        var section = new RecordData();
        var offset = 8 + properties.Count * 8;
        var table = new RecordData();
        foreach (var (id, bytes) in properties)
        {
            table.U32(id).U32((uint)offset);
            offset += bytes.Length;
        }
        section.U32((uint)offset).U32((uint)properties.Count).Bytes(table.ToArray());
        foreach (var (_, bytes) in properties)
            section.Bytes(bytes);

        // 한글 names its format id as the class id too.
        var set = new RecordData().U16(0xFFFE).U16(0).U32(0x0002000A).Bytes(format).U32(1).Bytes(format).U32(48);
        return [.. set.ToArray(), .. section.ToArray()];
    }

    /// <summary>PrvText: the beginning of the document's text in UTF-16, lines ending in CR LF, as 한글 shows it.</summary>
    private byte[] PreviewText(StringBuilder text)
    {
        if (Read("Preview/PrvText.txt") is { Length: > 0 } stored)
        {
            var decoded = Encoding.UTF8.GetString(stored).TrimStart('﻿');
            if (decoded.Length > 0)
                return Encoding.Unicode.GetBytes(Limit(decoded));
        }
        return Encoding.Unicode.GetBytes(Limit(text.ToString()));
    }

    private static string Limit(string text) => text.Length > 1024 ? text[..1024] : text;

    /// <summary>PrvImage: the package's preview picture (PNG, GIF or BMP), as 한글 stores it.</summary>
    private byte[]? PreviewImage()
    {
        var entry = _package.Entries.FirstOrDefault(e => e.FullName.StartsWith("Preview/PrvImage", StringComparison.OrdinalIgnoreCase));
        return entry is null ? null : _package.ReadBytes(entry, _package.Limits.MaxPartBytes);
    }

    // ───────────────────────── XML helpers ─────────────────────────

    /// <summary>Child elements with hp:switch blocks replaced by the branch the reader takes.</summary>
    private static IEnumerable<XElement> Elements(XElement? parent)
    {
        if (parent is null)
            yield break;
        foreach (var child in parent.Elements())
        {
            if (child.Name.LocalName == "switch")
            {
                foreach (var inner in Elements(Branch(child).Branch))
                    yield return inner;
            }
            else
            {
                yield return child;
            }
        }
    }

    /// <summary>The hp:case for HwpUnitChar / 2016 paragraphs (exact values), else hp:default (lengths doubled).</summary>
    private static (XElement? Branch, bool Exact) Branch(XElement switchElement)
    {
        foreach (var branch in switchElement.Elements())
        {
            if (branch.Name.LocalName != "case")
                continue;
            var required = branch.Attributes().FirstOrDefault(a => a.Name.LocalName == "required-namespace")?.Value ?? "";
            if (required.Contains(HwpUnitCharNamespace, StringComparison.Ordinal) || required == Paragraph2016Namespace)
                return (branch, true);
        }
        return (switchElement.Elements().FirstOrDefault(e => e.Name.LocalName == "default"), false);
    }

    /// <summary>The first child with this local name, looking into hp:switch blocks.</summary>
    private static XElement? Child(XElement? parent, string name)
    {
        if (parent is null)
            return null;
        foreach (var child in parent.Elements())
        {
            var local = child.Name.LocalName;
            if (local == name)
                return child;
            if (local == "switch" && Child(Branch(child).Branch, name) is { } inner)
                return inner;
        }
        return null;
    }

    private static IEnumerable<XElement> Children(XElement? parent, string name) => Elements(parent).Where(e => e.Name.LocalName == name);

    private static IEnumerable<XElement> Descendants(XElement? root, string name) =>
        root?.Descendants().Where(e => e.Name.LocalName == name) ?? [];

    private static string? Attr(XElement? element, string name) => (string?)element?.Attribute(name);

    /// <summary>An integer attribute; values above 2³¹ (unsigned offsets) wrap to negative, as the reader reads them.</summary>
    private static int Int(XElement? element, string name, int fallback = 0)
    {
        var text = Attr(element, name);
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) && double.IsFinite(real)
                ? (int)Math.Clamp(Math.Round(real), int.MinValue, int.MaxValue)
                : fallback;
        }
        if (value is > int.MaxValue and <= uint.MaxValue)
            value -= 1L << 32;
        return (int)Math.Clamp(value, int.MinValue, int.MaxValue);
    }

    /// <summary>An unsigned 32-bit attribute (ids that may be 4294967295).</summary>
    private static uint UInt(XElement? element, string name, uint fallback = 0) => (uint)Int(element, name, (int)fallback);

    private static double Real(XElement? element, string name, double fallback = 0) =>
        double.TryParse(Attr(element, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : fallback;

    private static bool Flag(XElement? element, string name, bool fallback = false) => Attr(element, name) switch
    {
        null => fallback,
        "1" or "true" => true,
        _ => false,
    };

    // ───────────────────────── Shared values ─────────────────────────

    /// <summary>"#RRGGBB" (or "#AARRGGBB") as a COLORREF 0x00BBGGRR; "none" and unknown values are 0xFFFFFFFF.</summary>
    private static uint Color(string? value, uint fallback = 0xFFFFFFFF)
    {
        if (value is null || !value.StartsWith('#'))
            return fallback;
        var hex = value[1..];
        if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var number))
            return fallback;
        var alpha = hex.Length == 8 ? number >> 24 : 0;
        var (r, g, b) = ((number >> 16) & 0xFF, (number >> 8) & 0xFF, number & 0xFF);
        return (alpha << 24) | (b << 16) | (g << 8) | r;
    }

    private static uint Color(XElement? element, string name, uint fallback = 0xFFFFFFFF) => Color(Attr(element, name), fallback);

    /// <summary>Line types of borders and drawing outlines: none first, then the OWPML line types (HwpToHwpx.LineType).</summary>
    private static int LineType(string? value) => value is null or "NONE" ? 0 : LineShape(value) + 1;

    /// <summary>Line shapes of underlines, strikeouts and separators: dotted before dashed, as 한글 saves them.</summary>
    private static int LineShape(string? value) => value switch
    {
        "SOLID" => 0,
        "DOT" => 1,
        "DASH" => 2,
        "DASH_DOT" => 3,
        "DASH_DOT_DOT" => 4,
        "LONG_DASH" => 5,
        "CIRCLE" => 6,
        "DOUBLE_SLIM" => 7,
        "SLIM_THICK" => 8,
        "THICK_SLIM" => 9,
        "SLIM_THICK_SLIM" => 10,
        "WAVE" => 11,
        "DOUBLE_WAVE" => 12,
        "THICK_3D" => 13,
        "THICK_3D_REVERS_LIGHTING" => 14,
        "3D" => 15,
        "3D_REVERS_LIGHTING" => 16,
        _ => 0,
    };

    private static readonly double[] BorderWidths = [0.1, 0.12, 0.15, 0.2, 0.25, 0.3, 0.4, 0.5, 0.6, 0.7, 1.0, 1.5, 2.0, 3.0, 4.0, 5.0];

    /// <summary>A border width like "0.12 mm" as the index of the nearest width HWP has.</summary>
    private static int BorderWidth(string? value)
    {
        var number = new string((value ?? "").TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm))
            return 0;
        var best = 0;
        for (var i = 1; i < BorderWidths.Length; i++)
        {
            if (Math.Abs(BorderWidths[i] - mm) < Math.Abs(BorderWidths[best] - mm))
                best = i;
        }
        return best;
    }

    /// <summary>Number formats of numberings, notes and page numbers.</summary>
    private static int NumberFormat(string? value) => value switch
    {
        "CIRCLED_DIGIT" => 1,
        "ROMAN_CAPITAL" => 2,
        "ROMAN_SMALL" => 3,
        "LATIN_CAPITAL" => 4,
        "LATIN_SMALL" => 5,
        "CIRCLED_LATIN_CAPITAL" => 6,
        "CIRCLED_LATIN_SMALL" => 7,
        "HANGUL_SYLLABLE" => 8,
        "CIRCLED_HANGUL_SYLLABLE" => 9,
        "HANGUL_JAMO" => 10,
        "CIRCLED_HANGUL_JAMO" => 11,
        "HANGUL_PHONETIC" => 12,
        "IDEOGRAPH" => 13,
        "CIRCLED_IDEOGRAPH" => 14,
        "DECAGON_CIRCLE" => 15,
        "DECAGON_CIRCLE_HANJA" => 16,
        "SYMBOL" => 0x80,
        "USER_CHAR" => 0x81,
        _ => 0,
    };

    /// <summary>A single character field (prefix, suffix, user symbol); none is the null character.</summary>
    private static ushort Char(string? value) => string.IsNullOrEmpty(value) ? (ushort)0 : value[0];

    /// <summary>A control id ("secd", "tbl ") packed as HWP stores it.</summary>
    private static uint ControlId(string id) => (uint)(id[0] << 24 | id[1] << 16 | id[2] << 8 | id[3]);
}
