// A workbook written in code twice: as an Excel binary workbook (BIFF12 records, [MS-XLSB]: a 1–2 byte record type
// and a 1–4 byte size, 7 bits per byte) and as the same workbook in XLSX, so the tests compare what the XLSB reader
// reads with what the XLSX reader reads. Both share one style sheet (fonts with theme, tinted and indexed colours,
// solid, pattern and gradient fills, borders with diagonals, number formats, alignment).

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Filee.Engines.Tests;

internal sealed class XlsbBuilder
{
    // Cell formats (cell XF index) of the shared style sheet.
    public const int Plain = 0;
    public const int Header = 1;    // bold white on #1F4E79, thin borders, centred both ways
    public const int Won = 2;       // #,##0"원" (custom 164)
    public const int Date = 3;      // built-in 14
    public const int Percent = 4;   // 0.0% (custom 165)
    public const int Accent = 5;    // italic struck-through underlined 14 pt dark red, right, top, wrapped
    public const int Pattern = 6;   // dark grey pattern (red on white), left, indent 2
    public const int Gradient = 7;  // white to blue, turned 45°, shrunk to fit
    public const int Diagonal = 8;  // both diagonals (medium, blue), stacked letters
    public const int Across = 9;    // tinted accent1 맑은 고딕, double underline; double top, dashed red bottom; centre across, justified vertically
    public const int Decimals = 10; // 0.00, distributed

    /// <summary>Fonts: height in twips, bold, italic, underline (BrtFont uls), strike, script (sss), colour, name.</summary>
    private static readonly (int Height, bool Bold, bool Italic, int Underline, bool Strike, int Script, XlsbColor Color, string Name)[] Fonts =
    [
        (220, false, false, 0, false, 0, XlsbColor.Theme(1), "Calibri"),
        (220, true, false, 0, false, 0, XlsbColor.Rgb(0xFFFFFF), "Calibri"),
        (280, false, true, 1, true, 0, XlsbColor.Rgb(0xC00000), "Calibri"),
        (220, false, false, 2, false, 0, XlsbColor.Theme(4, 0.39997558519241921), "맑은 고딕"),
        (220, false, false, 0, false, 1, XlsbColor.Indexed(10), "Calibri"),
    ];

    /// <summary>Fills: BrtFill fls (0x28 gradient), foreground, background; gradient degree and stops.</summary>
    private static readonly (int Pattern, XlsbColor Fore, XlsbColor Back, double Degree, (double Position, XlsbColor Color)[] Stops)[] Fills =
    [
        (0, XlsbColor.Auto, XlsbColor.Auto, 0, []),
        (17, XlsbColor.Auto, XlsbColor.Auto, 0, []),
        (1, XlsbColor.Rgb(0x1F4E79), XlsbColor.Indexed(64), 0, []),
        (3, XlsbColor.Rgb(0xFF0000), XlsbColor.Rgb(0xFFFFFF), 0, []),
        (0x28, XlsbColor.Auto, XlsbColor.Auto, 90, [(0, XlsbColor.Rgb(0xFFFFFF)), (1, XlsbColor.Rgb(0x0000FF))]),
    ];

    /// <summary>Borders: diagonal flags (1 down, 2 up), then top, bottom, left, right, diagonal as (Blxf dg, colour).</summary>
    private static readonly (int Flags, (int Style, XlsbColor Color)[] Sides)[] Borders =
    [
        (0, [(0, XlsbColor.Auto), (0, XlsbColor.Auto), (0, XlsbColor.Auto), (0, XlsbColor.Auto), (0, XlsbColor.Auto)]),
        (0, [(1, XlsbColor.Auto), (1, XlsbColor.Auto), (1, XlsbColor.Auto), (1, XlsbColor.Auto), (0, XlsbColor.Auto)]),
        (3, [(0, XlsbColor.Auto), (0, XlsbColor.Auto), (0, XlsbColor.Auto), (0, XlsbColor.Auto), (2, XlsbColor.Rgb(0x0000FF))]),
        (0, [(6, XlsbColor.Auto), (3, XlsbColor.Indexed(10)), (0, XlsbColor.Auto), (0, XlsbColor.Auto), (0, XlsbColor.Auto)]),
    ];

    private static readonly (int Id, string Code)[] Formats = [(164, "#,##0\"원\""), (165, "0.0%")];

    /// <summary>Cell XFs: number format, font, fill, border, rotation, indent, alc, alcv, wrap, shrink.</summary>
    private static readonly (int Format, int Font, int Fill, int Border, int Rotation, int Indent, int Horizontal, int Vertical, bool Wrap, bool Shrink)[] Xfs =
    [
        (0, 0, 0, 0, 0, 0, 0, 2, false, false),
        (0, 1, 2, 1, 0, 0, 2, 1, false, false),
        (164, 0, 0, 0, 0, 0, 0, 2, false, false),
        (14, 0, 0, 0, 0, 0, 0, 2, false, false),
        (165, 0, 0, 0, 0, 0, 0, 2, false, false),
        (0, 2, 0, 0, 0, 0, 3, 0, true, false),
        (0, 0, 3, 0, 0, 2, 1, 2, false, false),
        (0, 0, 4, 0, 45, 0, 0, 2, false, true),
        (0, 0, 0, 2, 255, 0, 0, 2, false, false),
        (0, 3, 0, 3, 0, 0, 6, 3, false, false),
        (2, 4, 0, 0, 0, 0, 7, 2, false, false),
    ];

    private static readonly string[] Horizontal = ["general", "left", "center", "right", "fill", "justify", "centerContinuous", "distributed"];
    private static readonly string[] Vertical = ["top", "center", "bottom", "justify", "distributed"];
    private static readonly string[] BorderNames = ["none", "thin", "medium", "dashed", "dotted", "thick", "double", "hair"];
    private static readonly string?[] PatternNames = [null, "solid", "mediumGray", "darkGray"];

    private readonly List<(string Text, (int Start, int Font)[]? Runs)> _strings = [];

    public List<XlsbSheet> Sheets { get; } = [];

    public bool Date1904 { get; set; }

    /// <summary>Print areas and print titles: the sheet's position, "Print_Area" or "Print_Titles", the ranges (0-based, inclusive).</summary>
    public List<(int Sheet, string Name, (int Top, int Left, int Bottom, int Right)[] Ranges)> Names { get; } = [];

    /// <summary>More parts (drawings, VML) shared by both packages, by zip path.</summary>
    public Dictionary<string, string> Parts { get; } = [];

    /// <summary>Binary parts (pictures), by zip path.</summary>
    public Dictionary<string, byte[]> Media { get; } = [];

    /// <summary>Records the workbook part gets after its sheets (for hostile tests).</summary>
    public List<byte[]> ExtraWorkbookRecords { get; } = [];

    public XlsbSheet Sheet(string name, int state = 0)
    {
        var sheet = new XlsbSheet(this, name, state);
        Sheets.Add(sheet);
        return sheet;
    }

    /// <summary>The index of a shared string (rich text when <paramref name="runs"/> are given: start, font).</summary>
    public int StringIndex(string text, (int Start, int Font)[]? runs = null)
    {
        var index = _strings.FindIndex(s => s.Text == text && (s.Runs ?? []).SequenceEqual(runs ?? []));
        if (index >= 0)
            return index;
        _strings.Add((text, runs));
        return _strings.Count - 1;
    }

    // ───────────────────────── XLSB ─────────────────────────

    public string SaveXlsb(string path)
    {
        var parts = new Dictionary<string, byte[]>
        {
            ["[Content_Types].xml"] = Utf8(ContentTypes(binary: true)),
            ["_rels/.rels"] = Utf8(OpcWriter.Declaration + OpcWriter.Relationships([("rId1", "officeDocument", "xl/workbook.bin")])),
            ["xl/_rels/workbook.bin.rels"] = Utf8(OpcWriter.Declaration + WorkbookRelationships("bin")),
            ["xl/workbook.bin"] = WorkbookBin(),
            ["xl/styles.bin"] = StylesBin(),
            ["xl/sharedStrings.bin"] = SharedStringsBin(),
        };
        for (var i = 0; i < Sheets.Count; i++)
        {
            parts[$"xl/worksheets/sheet{i + 1}.bin"] = Sheets[i].Bin();
            if (Sheets[i].Relationships.Count > 0)
                parts[$"xl/worksheets/_rels/sheet{i + 1}.bin.rels"] = Utf8(OpcWriter.Declaration + OpcWriter.Relationships(Sheets[i].Relationships));
        }
        return Save(path, parts);
    }

    private byte[] WorkbookBin()
    {
        var w = new RecordWriter();
        w.Record(131, []); // BrtBeginBook
        w.Record(153, [.. U32(Date1904 ? 1u : 0u), .. U32(0), .. WideString("ThisWorkbook")]); // BrtWbProp
        w.Record(143, []); // BrtBeginBundleShs
        for (var i = 0; i < Sheets.Count; i++)
            w.Record(156, [.. U32((uint)Sheets[i].State), .. U32((uint)i + 1), .. WideString($"rId{i + 1}"), .. WideString(Sheets[i].Name)]);
        w.Record(144, []); // BrtEndBundleShs
        foreach (var (sheet, name, ranges) in Names)
        {
            var rgce = new List<byte>();
            if (ranges.Length > 1)
                rgce.AddRange([0x29, .. U16((ushort)(ranges.Length * 15 + ranges.Length - 1))]); // PtgMemFunc
            for (var i = 0; i < ranges.Length; i++)
            {
                var (top, left, bottom, right) = ranges[i];
                rgce.AddRange([0x3B, .. U16(0), .. U32((uint)top), .. U32((uint)bottom), .. U16((ushort)left), .. U16((ushort)right)]); // PtgArea3d
                if (i > 0)
                    rgce.Add(0x10); // PtgUnion
            }
            w.Record(39, [.. U32(0x20), 0, .. U32((uint)sheet), .. WideString("_xlnm." + name), .. U32((uint)rgce.Count), .. rgce, .. U32(0),
                .. NullString(), .. NullString(), .. NullString(), .. NullString(), .. NullString()]);
        }
        foreach (var record in ExtraWorkbookRecords)
            w.Raw(record);
        w.Record(132, []); // BrtEndBook
        return w.ToArray();
    }

    private static byte[] StylesBin()
    {
        var w = new RecordWriter();
        w.Record(278, []); // BrtBeginStyleSheet
        w.Record(615, U32((uint)Formats.Length));
        foreach (var (id, code) in Formats)
            w.Record(44, [.. U16((ushort)id), .. WideString(code)]);
        w.Record(616, []);
        w.Record(611, U32((uint)Fonts.Length));
        foreach (var f in Fonts)
        {
            ushort flags = (ushort)((f.Italic ? 0x02 : 0) | (f.Strike ? 0x08 : 0));
            w.Record(43, [.. U16((ushort)f.Height), .. U16(flags), .. U16((ushort)(f.Bold ? 700 : 400)), .. U16((ushort)f.Script), (byte)f.Underline, 2, 1, 0,
                .. f.Color.Bytes(), 2, .. WideString(f.Name)]);
        }
        w.Record(612, []);
        w.Record(603, U32((uint)Fills.Length));
        foreach (var fill in Fills)
        {
            var data = new List<byte>([.. U32((uint)fill.Pattern), .. fill.Fore.Bytes(), .. fill.Back.Bytes(), .. U32(0), .. F64(fill.Degree),
                .. F64(0), .. F64(0), .. F64(0), .. F64(0), .. U32((uint)fill.Stops.Length)]);
            foreach (var (position, color) in fill.Stops)
                data.AddRange([.. color.Bytes(), .. F64(position)]);
            w.Record(45, [.. data]);
        }
        w.Record(604, []);
        w.Record(613, U32((uint)Borders.Length));
        foreach (var (flags, sides) in Borders)
            w.Record(46, [(byte)flags, .. sides.SelectMany(s => (byte[])[(byte)s.Style, 0, .. s.Color.Bytes()])]);
        w.Record(614, []);
        w.Record(626, U32(1)); // BrtBeginCellStyleXFs
        w.Record(47, Xf(0xFFFF, (0, 0, 0, 0, 0, 0, 0, 2, false, false)));
        w.Record(627, []);
        w.Record(617, U32((uint)Xfs.Length)); // BrtBeginCellXFs
        foreach (var xf in Xfs)
            w.Record(47, Xf(0, xf));
        w.Record(618, []);
        w.Record(279, []);
        return w.ToArray();
    }

    private static byte[] Xf(int parent, (int Format, int Font, int Fill, int Border, int Rotation, int Indent, int Horizontal, int Vertical, bool Wrap, bool Shrink) xf)
    {
        var alignment = (ushort)(xf.Horizontal | xf.Vertical << 3 | (xf.Wrap ? 0x40 : 0) | (xf.Shrink ? 0x100 : 0));
        return [.. U16((ushort)parent), .. U16((ushort)xf.Format), .. U16((ushort)xf.Font), .. U16((ushort)xf.Fill), .. U16((ushort)xf.Border),
            (byte)xf.Rotation, (byte)xf.Indent, .. U16(alignment), .. U16(0)];
    }

    private byte[] SharedStringsBin()
    {
        var w = new RecordWriter();
        w.Record(159, [.. U32((uint)_strings.Count), .. U32((uint)_strings.Count)]); // BrtBeginSst
        foreach (var (text, runs) in _strings)
        {
            if (runs is null)
                w.Record(19, [0, .. WideString(text)]);
            else
                w.Record(19, [1, .. WideString(text), .. U32((uint)runs.Length), .. runs.SelectMany(r => (byte[])[.. U16((ushort)r.Start), .. U16((ushort)r.Font)])]);
        }
        w.Record(160, []);
        return w.ToArray();
    }

    // ───────────────────────── XLSX ─────────────────────────

    private const string Namespaces = "xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";

    public string SaveXlsx(string path)
    {
        var names = Names.Count == 0 ? "" : "<definedNames>" + string.Concat(Names.Select(n =>
            $"<definedName name=\"_xlnm.{n.Name}\" localSheetId=\"{n.Sheet}\">" +
            string.Join(",", n.Ranges.Select(r => $"'{OpcWriter.Escape(Sheets[n.Sheet].Name)}'!{Reference(r)}")) + "</definedName>")) + "</definedNames>";
        var parts = new Dictionary<string, byte[]>
        {
            ["[Content_Types].xml"] = Utf8(ContentTypes(binary: false)),
            ["_rels/.rels"] = Utf8(OpcWriter.Declaration + OpcWriter.Relationships([("rId1", "officeDocument", "xl/workbook.xml")])),
            ["xl/_rels/workbook.xml.rels"] = Utf8(OpcWriter.Declaration + WorkbookRelationships("xml")),
            ["xl/workbook.xml"] = Utf8(OpcWriter.Declaration + $"<workbook {Namespaces}>" + (Date1904 ? "<workbookPr date1904=\"1\"/>" : "") + "<sheets>" +
                string.Concat(Sheets.Select((s, i) => $"<sheet name=\"{OpcWriter.Escape(s.Name)}\" sheetId=\"{i + 1}\"" +
                    (s.State switch { 1 => " state=\"hidden\"", 2 => " state=\"veryHidden\"", _ => "" }) + $" r:id=\"rId{i + 1}\"/>")) +
                "</sheets>" + names + "</workbook>"),
            ["xl/styles.xml"] = Utf8(OpcWriter.Declaration + $"<styleSheet {Namespaces}>{StylesXml()}</styleSheet>"),
            ["xl/sharedStrings.xml"] = Utf8(OpcWriter.Declaration + $"<sst {Namespaces}>" + string.Concat(_strings.Select(StringXml)) + "</sst>"),
        };
        for (var i = 0; i < Sheets.Count; i++)
        {
            parts[$"xl/worksheets/sheet{i + 1}.xml"] = Utf8(OpcWriter.Declaration + Sheets[i].Xml());
            if (Sheets[i].Relationships.Count > 0)
                parts[$"xl/worksheets/_rels/sheet{i + 1}.xml.rels"] = Utf8(OpcWriter.Declaration + OpcWriter.Relationships(Sheets[i].Relationships));
        }
        return Save(path, parts);
    }

    private static string Reference((int Top, int Left, int Bottom, int Right) r)
    {
        if (r.Left == 0 && r.Right == 16383)
            return $"${r.Top + 1}:${r.Bottom + 1}";
        if (r.Top == 0 && r.Bottom == 1048575)
            return $"${Column(r.Left)}:${Column(r.Right)}";
        return $"${Column(r.Left)}${r.Top + 1}:${Column(r.Right)}${r.Bottom + 1}";
    }

    internal static string Column(int column) => column < 26 ? ((char)('A' + column)).ToString() : Column(column / 26 - 1) + (char)('A' + column % 26);

    private static string StylesXml()
    {
        var sb = new StringBuilder();
        sb.Append("<numFmts>");
        foreach (var (id, code) in Formats)
            sb.Append(CultureInfo.InvariantCulture, $"<numFmt numFmtId=\"{id}\" formatCode=\"{OpcWriter.Escape(code)}\"/>");
        sb.Append("</numFmts><fonts>");
        foreach (var f in Fonts)
            sb.Append("<font>").Append(FontXml(f, "name")).Append("</font>");
        sb.Append("</fonts><fills>");
        foreach (var fill in Fills)
        {
            if (fill.Pattern == 0x28)
                sb.Append(CultureInfo.InvariantCulture, $"<fill><gradientFill degree=\"{fill.Degree}\">")
                    .Append(string.Concat(fill.Stops.Select(s => $"<stop position=\"{s.Position.ToString(CultureInfo.InvariantCulture)}\">{s.Color.Xml("color")}</stop>")))
                    .Append("</gradientFill></fill>");
            else
                sb.Append($"<fill><patternFill patternType=\"{(fill.Pattern == 17 ? "gray125" : PatternNames[fill.Pattern] ?? "none")}\">{fill.Fore.Xml("fgColor")}{fill.Back.Xml("bgColor")}</patternFill></fill>");
        }
        sb.Append("</fills><borders>");
        foreach (var (flags, sides) in Borders)
        {
            string Side(string element, (int Style, XlsbColor Color) side) =>
                side.Style == 0 ? $"<{element}/>" : $"<{element} style=\"{BorderNames[side.Style]}\">{side.Color.Xml("color")}</{element}>";
            sb.Append("<border").Append((flags & 2) != 0 ? " diagonalUp=\"1\"" : "").Append((flags & 1) != 0 ? " diagonalDown=\"1\"" : "").Append('>')
                .Append(Side("left", sides[2])).Append(Side("right", sides[3])).Append(Side("top", sides[0])).Append(Side("bottom", sides[1]))
                .Append(Side("diagonal", sides[4])).Append("</border>");
        }
        sb.Append("</borders><cellStyleXfs><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs>");
        foreach (var xf in Xfs)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<xf numFmtId=\"{xf.Format}\" fontId=\"{xf.Font}\" fillId=\"{xf.Fill}\" borderId=\"{xf.Border}\" xfId=\"0\">")
                .Append(CultureInfo.InvariantCulture, $"<alignment horizontal=\"{Horizontal[xf.Horizontal]}\" vertical=\"{Vertical[xf.Vertical]}\" textRotation=\"{xf.Rotation}\" indent=\"{xf.Indent}\"")
                .Append(xf.Wrap ? " wrapText=\"1\"" : "").Append(xf.Shrink ? " shrinkToFit=\"1\"" : "").Append("/></xf>");
        }
        sb.Append("</cellXfs>");
        return sb.ToString();
    }

    /// <summary>A font's properties as SpreadsheetML elements, all of them (so a rich text run sets each one).</summary>
    private static string FontXml((int Height, bool Bold, bool Italic, int Underline, bool Strike, int Script, XlsbColor Color, string Name) f, string nameElement) =>
        $"<b val=\"{(f.Bold ? 1 : 0)}\"/><i val=\"{(f.Italic ? 1 : 0)}\"/><strike val=\"{(f.Strike ? 1 : 0)}\"/>" +
        $"<u val=\"{f.Underline switch { 1 => "single", 2 => "double", _ => "none" }}\"/>" +
        $"<vertAlign val=\"{f.Script switch { 1 => "superscript", 2 => "subscript", _ => "baseline" }}\"/>" +
        $"<sz val=\"{(f.Height / 20.0).ToString(CultureInfo.InvariantCulture)}\"/>{f.Color.Xml("color")}<{nameElement} val=\"{OpcWriter.Escape(f.Name)}\"/>";

    private static string StringXml((string Text, (int Start, int Font)[]? Runs) s)
    {
        if (s.Runs is not { } runs)
            return $"<si><t xml:space=\"preserve\">{OpcWriter.Escape(s.Text)}</t></si>";
        var sb = new StringBuilder("<si>");
        if (runs[0].Start > 0)
            sb.Append($"<r><t xml:space=\"preserve\">{OpcWriter.Escape(s.Text[..runs[0].Start])}</t></r>");
        for (var i = 0; i < runs.Length; i++)
        {
            var end = i + 1 < runs.Length ? runs[i + 1].Start : s.Text.Length;
            sb.Append($"<r><rPr>{FontXml(Fonts[runs[i].Font], "rFont")}</rPr><t xml:space=\"preserve\">{OpcWriter.Escape(s.Text[runs[i].Start..end])}</t></r>");
        }
        return sb.Append("</si>").ToString();
    }

    // ───────────────────────── Package ─────────────────────────

    private string WorkbookRelationships(string extension) => OpcWriter.Relationships(
        Sheets.Select((_, i) => ($"rId{i + 1}", "worksheet", $"worksheets/sheet{i + 1}.{extension}"))
            .Append(("rIdS", "sharedStrings", $"sharedStrings.{extension}"))
            .Append(("rIdT", "styles", $"styles.{extension}")));

    private string ContentTypes(bool binary) => OpcWriter.Declaration +
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Default Extension=\"png\" ContentType=\"image/png\"/>" +
        (binary
            ? "<Default Extension=\"bin\" ContentType=\"application/vnd.ms-excel.sheet.binary.macroEnabled.main\"/>"
            : "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>") +
        string.Concat(Parts.Keys.Where(p => p.StartsWith("xl/drawings/drawing", StringComparison.Ordinal))
            .Select(p => $"<Override PartName=\"/{p}\" ContentType=\"application/vnd.openxmlformats-officedocument.drawing+xml\"/>")) +
        "</Types>";

    private string Save(string path, Dictionary<string, byte[]> parts)
    {
        foreach (var (name, xml) in Parts)
            parts[name] = Utf8(OpcWriter.Declaration + xml);
        foreach (var (name, bytes) in Media)
            parts[name] = bytes;
        using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        foreach (var (name, bytes) in parts)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(bytes);
        }
        return path;
    }

    private static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

    // ───────────────────────── Fields ─────────────────────────

    public static byte[] U16(ushort value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        return bytes;
    }

    public static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    public static byte[] F64(double value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(bytes, value);
        return bytes;
    }

    public static byte[] WideString(string text) => [.. U32((uint)text.Length), .. Encoding.Unicode.GetBytes(text)];

    public static byte[] NullString() => U32(0xFFFFFFFF);

    /// <summary>An RkNumber of an integer (fInt), perhaps stored × 100 (fx100).</summary>
    public static uint RkInteger(int value, bool times100 = false) => (uint)(value << 2) | 0x02u | (times100 ? 1u : 0u);

    /// <summary>An RkNumber of a double whose low 34 bits are zero.</summary>
    public static uint RkDouble(double value) => (uint)(BitConverter.DoubleToInt64Bits(value) >> 32) & 0xFFFFFFFC;
}

/// <summary>A BrtColor and its SpreadsheetML twin.</summary>
internal readonly record struct XlsbColor(int Type, int Index, short Tint, int Value)
{
    public static XlsbColor Auto => new(0, 0, 0, 0);

    public static XlsbColor Rgb(int rgb) => new(2, 0, 0, rgb);

    public static XlsbColor Indexed(int index) => new(1, index, 0, 0);

    public static XlsbColor Theme(int index, double tint = 0) => new(3, index, (short)Math.Round(tint * 32767), 0);

    public byte[] Bytes() =>
    [
        (byte)((Type == 2 ? 1 : 0) | Type << 1), (byte)Index, .. XlsbBuilder.U16((ushort)Tint),
        (byte)(Value >> 16), (byte)(Value >> 8), (byte)Value, 0xFF,
    ];

    public string Xml(string element)
    {
        var tint = Tint == 0 ? "" : $" tint=\"{(Tint / 32767.0).ToString("R", CultureInfo.InvariantCulture)}\"";
        return Type switch
        {
            1 => $"<{element} indexed=\"{Index}\"{tint}/>",
            2 => $"<{element} rgb=\"FF{Value:X6}\"{tint}/>",
            3 => $"<{element} theme=\"{Index}\"{tint}/>",
            _ => $"<{element} auto=\"1\"/>",
        };
    }
}

/// <summary>Writes BIFF12 records: the type in one or two bytes, the size in one to four, 7 bits each.</summary>
internal sealed class RecordWriter
{
    private readonly MemoryStream _stream = new();

    public RecordWriter Record(int type, byte[] data)
    {
        Raw(Encode(type, data));
        return this;
    }

    public void Raw(byte[] record) => _stream.Write(record);

    public static byte[] Encode(int type, byte[] data) => [.. Header(type, data.Length), .. data];

    /// <summary>A record header claiming <paramref name="size"/> bytes (which need not follow).</summary>
    public static byte[] Header(int type, int size)
    {
        var bytes = new List<byte>();
        if (type < 128)
            bytes.Add((byte)type);
        else
            bytes.AddRange([(byte)(type & 0x7F | 0x80), (byte)(type >> 7)]);
        do
        {
            var b = (byte)(size & 0x7F);
            size >>= 7;
            bytes.Add(size > 0 ? (byte)(b | 0x80) : b);
        }
        while (size > 0);
        return [.. bytes];
    }

    public byte[] ToArray() => _stream.ToArray();
}

/// <summary>One sheet of an <see cref="XlsbBuilder"/> workbook; rows and columns are 0-based.</summary>
internal sealed class XlsbSheet(XlsbBuilder book, string name, int state)
{
    private enum Kind { Blank, Rk, Real, Shared, Inline, Bool, Error, FormulaNumber, FormulaText, FormulaBool, FormulaError }

    private readonly SortedDictionary<(int Row, int Column), (Kind Kind, int Xf, object Value)> _cells = [];
    private readonly SortedDictionary<int, (double Height, bool Custom, bool Hidden)> _rows = [];
    private readonly List<(int First, int Last, double Width, bool Hidden)> _columns = [];
    private readonly List<(int Top, int Left, int Bottom, int Right)> _merges = [];
    private readonly List<int> _rowBreaks = [];
    private readonly List<int> _columnBreaks = [];

    public string Name => name;
    public int State => state;

    public int FrozenRows { get; set; }
    public bool RightToLeft { get; set; }

    /// <summary>Paper code, landscape, scale, fit to page (wide, tall), page order over then down, first page number.</summary>
    public (int Paper, bool Landscape, int Scale, bool Fit, int Wide, int Tall, bool OverThenDown, int? FirstPage)? PageSetup { get; set; }

    /// <summary>Margins in inches: left, right, top, bottom, header, footer.</summary>
    public double[]? Margins { get; set; }

    public bool GridLines { get; set; }
    public bool CenterHorizontally { get; set; }
    public (string? Header, string? Footer, string? FirstHeader, bool DifferentFirst)? HeaderFooter { get; set; }

    /// <summary>The relationship id of the sheet's drawing (its relationship goes into <see cref="Relationships"/>).</summary>
    public string? DrawingId { get; set; }

    public List<(string Id, string Type, string Target)> Relationships { get; } = [];

    /// <summary>Records written into the sheet data after the cells (for hostile tests).</summary>
    public List<byte[]> ExtraRecords { get; } = [];

    public XlsbSheet Text(int row, int column, string text, int xf = XlsbBuilder.Plain) => Cell(row, column, Kind.Shared, xf, book.StringIndex(text));

    public XlsbSheet Rich(int row, int column, string text, params (int Start, int Font)[] runs) =>
        Cell(row, column, Kind.Shared, XlsbBuilder.Plain, book.StringIndex(text, runs));

    /// <summary>A string stored in the cell itself (BrtCellSt).</summary>
    public XlsbSheet Inline(int row, int column, string text, int xf = XlsbBuilder.Plain) => Cell(row, column, Kind.Inline, xf, text);

    public XlsbSheet Number(int row, int column, double value, int xf = XlsbBuilder.Plain) => Cell(row, column, Kind.Real, xf, value);

    /// <summary>A number as an RkNumber (<see cref="XlsbBuilder.RkInteger"/>, <see cref="XlsbBuilder.RkDouble"/>).</summary>
    public XlsbSheet Rk(int row, int column, uint rk, int xf = XlsbBuilder.Plain) => Cell(row, column, Kind.Rk, xf, rk);

    public XlsbSheet Bool(int row, int column, bool value) => Cell(row, column, Kind.Bool, XlsbBuilder.Plain, value);

    public XlsbSheet Error(int row, int column, byte code) => Cell(row, column, Kind.Error, XlsbBuilder.Plain, code);

    public XlsbSheet Blank(int row, int column, int xf) => Cell(row, column, Kind.Blank, xf, 0);

    /// <summary>A formula cell: only the cached value is written (as XLSX, its formula text is a placeholder).</summary>
    public XlsbSheet Formula(int row, int column, object cached, int xf = XlsbBuilder.Plain) => cached switch
    {
        double number => Cell(row, column, Kind.FormulaNumber, xf, number),
        string text => Cell(row, column, Kind.FormulaText, xf, text),
        bool on => Cell(row, column, Kind.FormulaBool, xf, on),
        byte error => Cell(row, column, Kind.FormulaError, xf, error),
        _ => throw new ArgumentException("Not a cached value", nameof(cached)),
    };

    public XlsbSheet Height(int row, double points, bool custom = true, bool hidden = false)
    {
        _rows[row] = (points, custom, hidden);
        return this;
    }

    public XlsbSheet Width(int first, int last, double width, bool hidden = false)
    {
        _columns.Add((first, last, width, hidden));
        return this;
    }

    public XlsbSheet Merge(int top, int left, int bottom, int right)
    {
        _merges.Add((top, left, bottom, right));
        return this;
    }

    public XlsbSheet RowBreak(int row)
    {
        _rowBreaks.Add(row);
        return this;
    }

    public XlsbSheet ColumnBreak(int column)
    {
        _columnBreaks.Add(column);
        return this;
    }

    private XlsbSheet Cell(int row, int column, Kind kind, int xf, object value)
    {
        _cells[(row, column)] = (kind, xf, value);
        return this;
    }

    private IEnumerable<int> RowNumbers => _cells.Keys.Select(k => k.Row).Concat(_rows.Keys).Distinct().Order();

    private (double Height, bool Custom, bool Hidden) RowOf(int row) => _rows.TryGetValue(row, out var r) ? r : (15, false, false);

    // ───────────────────────── XLSB ─────────────────────────

    public byte[] Bin()
    {
        var w = new RecordWriter();
        w.Record(129, []); // BrtBeginSheet
        var fit = PageSetup is { Fit: true } ? 0x100 : 0;
        w.Record(147, [.. XlsbBuilder.U16((ushort)(0x04C0 | fit)), 0, .. XlsbColor.Auto.Bytes(), .. XlsbBuilder.U32(0xFFFFFFFF), .. XlsbBuilder.U32(0xFFFFFFFF), .. XlsbBuilder.U32(0)]); // BrtWsProp
        w.Record(133, []); // BrtBeginWsViews
        w.Record(137, [.. XlsbBuilder.U16((ushort)(0x03DC | (RightToLeft ? 0x20 : 0))), .. XlsbBuilder.U32(0), .. XlsbBuilder.U32(0), .. XlsbBuilder.U32(0), 64, 0,
            .. XlsbBuilder.U16(0), .. XlsbBuilder.U16(100), .. XlsbBuilder.U16(0), .. XlsbBuilder.U16(0), .. XlsbBuilder.U16(0), .. XlsbBuilder.U32(0)]);
        if (FrozenRows > 0)
            w.Record(151, [.. XlsbBuilder.F64(0), .. XlsbBuilder.F64(FrozenRows), .. XlsbBuilder.U32((uint)FrozenRows), .. XlsbBuilder.U32(0), .. XlsbBuilder.U32(2), 0x02]);
        w.Record(138, []);
        w.Record(134, []);
        w.Record(485, [.. XlsbBuilder.U32(0xFFFFFFFF), .. XlsbBuilder.U16(8), .. XlsbBuilder.U16(300), .. XlsbBuilder.U16(0), 0, 0]); // BrtWsFmtInfo
        if (_columns.Count > 0)
        {
            w.Record(390, []);
            foreach (var (first, last, width, hidden) in _columns)
                w.Record(60, [.. XlsbBuilder.U32((uint)first), .. XlsbBuilder.U32((uint)last), .. XlsbBuilder.U32((uint)Math.Round(width * 256)), .. XlsbBuilder.U32(0),
                    .. XlsbBuilder.U16((ushort)((hidden ? 1 : 0) | 2))]);
            w.Record(391, []);
        }
        w.Record(145, []); // BrtBeginSheetData
        foreach (var row in RowNumbers)
        {
            var (height, custom, hidden) = RowOf(row);
            var flags = (byte)((hidden ? 0x10 : 0) | (custom ? 0x20 : 0));
            w.Record(0, [.. XlsbBuilder.U32((uint)row), .. XlsbBuilder.U32(0), .. XlsbBuilder.U16((ushort)Math.Round(height * 20)), 0, flags, 0, .. XlsbBuilder.U32(0)]);
            foreach (var ((_, column), (kind, xf, value)) in _cells.Where(c => c.Key.Row == row))
            {
                List<byte> data = [.. XlsbBuilder.U32((uint)column), .. XlsbBuilder.U32((uint)xf)];
                var type = kind switch
                {
                    Kind.Blank => 1,
                    Kind.Rk => 2,
                    Kind.Error => 3,
                    Kind.Bool => 4,
                    Kind.Real => 5,
                    Kind.Inline => 6,
                    Kind.Shared => 7,
                    Kind.FormulaText => 8,
                    Kind.FormulaNumber => 9,
                    Kind.FormulaBool => 10,
                    _ => 11,
                };
                data.AddRange(kind switch
                {
                    Kind.Blank => [],
                    Kind.Rk => XlsbBuilder.U32((uint)value),
                    Kind.Error or Kind.FormulaError => [(byte)value],
                    Kind.Bool or Kind.FormulaBool => [(byte)((bool)value ? 1 : 0)],
                    Kind.Real or Kind.FormulaNumber => XlsbBuilder.F64((double)value),
                    Kind.Shared => XlsbBuilder.U32((uint)(int)value),
                    _ => XlsbBuilder.WideString((string)value),
                });
                if (type >= 8)
                    data.AddRange(Formula());
                w.Record(type, [.. data]);
            }
        }
        foreach (var record in ExtraRecords)
            w.Raw(record);
        w.Record(146, []); // BrtEndSheetData
        if (_merges.Count > 0)
        {
            w.Record(177, XlsbBuilder.U32((uint)_merges.Count));
            foreach (var (top, left, bottom, right) in _merges)
                w.Record(176, [.. XlsbBuilder.U32((uint)top), .. XlsbBuilder.U32((uint)bottom), .. XlsbBuilder.U32((uint)left), .. XlsbBuilder.U32((uint)right)]);
            w.Record(178, []);
        }
        w.Record(477, XlsbBuilder.U16((ushort)((CenterHorizontally ? 0x01 : 0) | (GridLines ? 0x08 : 0))));
        if (Margins is { } m)
            w.Record(476, [.. m.SelectMany(x => XlsbBuilder.F64(x))]);
        if (PageSetup is { } p)
        {
            var flags = (ushort)((p.OverThenDown ? 0x01 : 0) | (p.Landscape ? 0x02 : 0) | (p.FirstPage is not null ? 0x80 : 0));
            w.Record(478, [.. XlsbBuilder.U32((uint)p.Paper), .. XlsbBuilder.U32((uint)p.Scale), .. XlsbBuilder.U32(600), .. XlsbBuilder.U32(600), .. XlsbBuilder.U32(1),
                .. XlsbBuilder.U32((uint)(p.FirstPage ?? 1)), .. XlsbBuilder.U32((uint)p.Wide), .. XlsbBuilder.U32((uint)p.Tall), .. XlsbBuilder.U16(flags), .. XlsbBuilder.NullString()]);
        }
        if (HeaderFooter is { } hf)
        {
            byte[] Text(string? text) => text is null ? XlsbBuilder.NullString() : XlsbBuilder.WideString(text);
            w.Record(479, [.. XlsbBuilder.U16((ushort)((hf.DifferentFirst ? 0x02 : 0) | 0x0C)), .. Text(hf.Header), .. Text(hf.Footer), .. Text(null), .. Text(null),
                .. Text(hf.FirstHeader), .. Text(null)]);
            w.Record(480, []);
        }
        Breaks(w, 392, 393, _rowBreaks, 16383);
        Breaks(w, 394, 395, _columnBreaks, 1048575);
        if (DrawingId is not null)
            w.Record(550, XlsbBuilder.WideString(DrawingId));
        w.Record(130, []); // BrtEndSheet
        return w.ToArray();
    }

    /// <summary>A CellParsedFormula of one PtgInt (1) and no extra data.</summary>
    private static byte[] Formula() => [.. XlsbBuilder.U16(0), .. XlsbBuilder.U32(3), 0x1E, .. XlsbBuilder.U16(1), .. XlsbBuilder.U32(0)];

    private static void Breaks(RecordWriter w, int begin, int end, List<int> breaks, int span)
    {
        if (breaks.Count == 0)
            return;
        w.Record(begin, [.. XlsbBuilder.U32((uint)breaks.Count), .. XlsbBuilder.U32((uint)breaks.Count)]);
        foreach (var at in breaks)
            w.Record(396, [.. XlsbBuilder.U32((uint)at), .. XlsbBuilder.U32(0), .. XlsbBuilder.U32((uint)span), .. XlsbBuilder.U32(1), .. XlsbBuilder.U32(0)]);
        w.Record(end, []);
    }

    // ───────────────────────── XLSX ─────────────────────────

    public string Xml()
    {
        var sb = new StringBuilder("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
        if (PageSetup is { Fit: true })
            sb.Append("<sheetPr><pageSetUpPr fitToPage=\"1\"/></sheetPr>");
        sb.Append("<sheetViews><sheetView workbookViewId=\"0\"").Append(RightToLeft ? " rightToLeft=\"1\"" : "").Append('>');
        if (FrozenRows > 0)
            sb.Append(CultureInfo.InvariantCulture, $"<pane ySplit=\"{FrozenRows}\" topLeftCell=\"A{FrozenRows + 1}\" activePane=\"bottomLeft\" state=\"frozen\"/>");
        sb.Append("</sheetView></sheetViews><sheetFormatPr baseColWidth=\"8\" defaultRowHeight=\"15\"/>");
        if (_columns.Count > 0)
        {
            sb.Append("<cols>");
            foreach (var (first, last, width, hidden) in _columns)
                sb.Append(CultureInfo.InvariantCulture, $"<col min=\"{first + 1}\" max=\"{last + 1}\" width=\"{Math.Round(width * 256) / 256}\" customWidth=\"1\"{(hidden ? " hidden=\"1\"" : "")}/>");
            sb.Append("</cols>");
        }
        sb.Append("<sheetData>");
        foreach (var row in RowNumbers)
        {
            var (height, custom, hidden) = RowOf(row);
            sb.Append(CultureInfo.InvariantCulture, $"<row r=\"{row + 1}\" ht=\"{Math.Round(height * 20) / 20}\"{(custom ? " customHeight=\"1\"" : "")}{(hidden ? " hidden=\"1\"" : "")}>");
            foreach (var ((_, column), (kind, xf, value)) in _cells.Where(c => c.Key.Row == row))
            {
                var reference = $"{XlsbBuilder.Column(column)}{row + 1}";
                sb.Append(kind switch
                {
                    Kind.Blank => $"<c r=\"{reference}\" s=\"{xf}\"/>",
                    Kind.Rk => $"<c r=\"{reference}\" s=\"{xf}\"><v>{Office.Sheets.XlsbReader.Rk((uint)value).ToString("R", CultureInfo.InvariantCulture)}</v></c>",
                    Kind.Real or Kind.FormulaNumber => $"<c r=\"{reference}\" s=\"{xf}\"><v>{((double)value).ToString("R", CultureInfo.InvariantCulture)}</v></c>",
                    Kind.Shared => $"<c r=\"{reference}\" s=\"{xf}\" t=\"s\"><v>{value}</v></c>",
                    Kind.Inline => $"<c r=\"{reference}\" s=\"{xf}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{OpcWriter.Escape((string)value)}</t></is></c>",
                    Kind.FormulaText => $"<c r=\"{reference}\" s=\"{xf}\" t=\"str\"><f>1</f><v>{OpcWriter.Escape((string)value)}</v></c>",
                    Kind.Bool or Kind.FormulaBool => $"<c r=\"{reference}\" s=\"{xf}\" t=\"b\"><v>{((bool)value ? 1 : 0)}</v></c>",
                    _ => $"<c r=\"{reference}\" s=\"{xf}\" t=\"e\"><v>{ErrorText((byte)value)}</v></c>",
                });
            }
            sb.Append("</row>");
        }
        sb.Append("</sheetData>");
        if (_merges.Count > 0)
        {
            sb.Append("<mergeCells>");
            foreach (var (top, left, bottom, right) in _merges)
                sb.Append($"<mergeCell ref=\"{XlsbBuilder.Column(left)}{top + 1}:{XlsbBuilder.Column(right)}{bottom + 1}\"/>");
            sb.Append("</mergeCells>");
        }
        sb.Append("<printOptions").Append(CenterHorizontally ? " horizontalCentered=\"1\"" : "").Append(GridLines ? " gridLines=\"1\"" : "").Append("/>");
        if (Margins is { } m)
            sb.Append(CultureInfo.InvariantCulture, $"<pageMargins left=\"{m[0]}\" right=\"{m[1]}\" top=\"{m[2]}\" bottom=\"{m[3]}\" header=\"{m[4]}\" footer=\"{m[5]}\"/>");
        if (PageSetup is { } p)
            sb.Append(CultureInfo.InvariantCulture,
                $"<pageSetup paperSize=\"{p.Paper}\" scale=\"{p.Scale}\" fitToWidth=\"{p.Wide}\" fitToHeight=\"{p.Tall}\" orientation=\"{(p.Landscape ? "landscape" : "portrait")}\"" +
                $"{(p.OverThenDown ? " pageOrder=\"overThenDown\"" : "")}{(p.FirstPage is { } first ? $" firstPageNumber=\"{first}\" useFirstPageNumber=\"1\"" : "")}/>");
        if (HeaderFooter is { } hf)
        {
            sb.Append("<headerFooter").Append(hf.DifferentFirst ? " differentFirst=\"1\"" : "").Append(" scaleWithDoc=\"1\" alignWithMargins=\"1\">");
            if (hf.Header is not null)
                sb.Append($"<oddHeader>{OpcWriter.Escape(hf.Header)}</oddHeader>");
            if (hf.Footer is not null)
                sb.Append($"<oddFooter>{OpcWriter.Escape(hf.Footer)}</oddFooter>");
            if (hf.FirstHeader is not null)
                sb.Append($"<firstHeader>{OpcWriter.Escape(hf.FirstHeader)}</firstHeader>");
            sb.Append("</headerFooter>");
        }
        if (_rowBreaks.Count > 0)
            sb.Append("<rowBreaks>").Append(string.Concat(_rowBreaks.Select(b => $"<brk id=\"{b}\" max=\"16383\" man=\"1\"/>"))).Append("</rowBreaks>");
        if (_columnBreaks.Count > 0)
            sb.Append("<colBreaks>").Append(string.Concat(_columnBreaks.Select(b => $"<brk id=\"{b}\" max=\"1048575\" man=\"1\"/>"))).Append("</colBreaks>");
        if (DrawingId is not null)
            sb.Append($"<drawing r:id=\"{DrawingId}\"/>");
        return sb.Append("</worksheet>").ToString();
    }

    private static string ErrorText(byte code) => code switch
    {
        0x00 => "#NULL!",
        0x07 => "#DIV/0!",
        0x0F => "#VALUE!",
        0x17 => "#REF!",
        0x1D => "#NAME?",
        0x24 => "#NUM!",
        _ => "#N/A",
    };
}
