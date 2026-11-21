// XLSB (Excel binary workbook) → Workbook, read directly from Microsoft's published format ([MS-XLSB]): the same ZIP
// package and relationships as XLSX, with the workbook, shared strings, styles and worksheets stored as BIFF12
// records (a 1–2 byte type and a 1–4 byte size, 7 bits per byte) instead of XML. It fills the same sheet model as the
// XLSX reader, so every sheet output (XLSX, ODS, CSV, PDF, HWPX, HTML, ...) works from it.
//
// Read: sheets (hidden ones skipped), the 1904 date system, print areas and print titles (defined names), shared
// strings with rich text runs, number formats, fonts, fills (solid, patterns, gradients), borders, cell formats with
// their alignment, cell values (numbers, RK numbers, text, booleans, errors; formulas as the value Excel last
// calculated, the formula itself is not read), column widths and hidden columns, row heights and hidden rows, merged
// cells, frozen rows, right-to-left sheets, manual page breaks, the page setup, margins, print options, headers and
// footers (with their pictures), each sheet's drawing (pictures, shapes and charts; drawings are DrawingML XML in XLSB
// too) and chart sheets (their page setup and the chart of their drawing, with what is placed over it).
// Conditional formats are read as the XLSX reader reads them (BrtBeginConditionalFormatting, BrtBeginCFRule with its
// differential format from styles.bin's BrtDXF, colour scales' BrtCFVO and BrtColor). Tables are read as the XLSX
// reader reads them (BrtBeginListParts / BrtListPart → the table part's BrtBeginList with its formats, BrtBeginListCol
// and BrtTableStyleClient; the workbook's own styles from BrtBeginTableStyle / BrtTableStyleElement), and the notes on
// cells (the comments part's BrtCommentAuthor, BrtBeginComment and BrtCommentText; shown or not from the
// BrtLegacyDrawing VML; threaded comments from the same XML parts as in XLSX) with BrtPageSetup's fNotes / fEndNotes. Left out, as by the XLSX reader: data validation, rules with formulas of their own, data bars and
// icon sets.
//
// The input is untrusted: the package is read within DocumentLimits (part and total sizes counted while unpacking,
// inflate ratio), records count against MaxXmlElements like XML elements do, a record the reader takes apart may hold
// at most MaxRecordBytes, every field is read within its record, and rows, columns and expanded column ranges are
// bounded as in the XLSX reader.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Filee.Engines.Documents;
using Filee.Engines.Office.Ooxml;

namespace Filee.Engines.Office.Sheets;

internal sealed class XlsbReader
{
    public const string Format = "xlsb";

    /// <summary>Bytes of one record the reader takes apart (the largest real ones, strings and names, stay below 200 KB).</summary>
    internal const int MaxRecordBytes = 1024 * 1024;

    /// <summary>Column entries (BrtColInfo ranges expanded) one workbook may set, as in the XLSX reader.</summary>
    private const int MaxColumnEntries = 4_000_000;

    /// <summary>Days between the 1900 and 1904 date systems.</summary>
    private const double Date1904Offset = 1462;

    // Record types ([MS-XLSB] 2.3).
    private const int RowHdr = 0;
    private const int CellBlank = 1;
    private const int CellRk = 2;
    private const int CellError = 3;
    private const int CellBool = 4;
    private const int CellReal = 5;
    private const int CellSt = 6;
    private const int CellIsst = 7;
    private const int FmlaString = 8;
    private const int FmlaNum = 9;
    private const int FmlaBool = 10;
    private const int FmlaError = 11;
    private const int SstItem = 19;
    private const int Name = 39;
    private const int Font = 43;
    private const int Fmt = 44;
    private const int Fill = 45;
    private const int Border = 46;
    private const int Xf = 47;
    private const int ColInfo = 60;
    private const int CellRString = 62;
    private const int BeginWsView = 137;
    private const int EndWsView = 138;
    private const int WsProp = 147;
    private const int Pane = 151;
    private const int WbProp = 153;
    private const int BundleSh = 156;
    private const int MergeCell = 176;
    private const int BeginConditionalFormatting = 461;
    private const int EndConditionalFormatting = 462;
    private const int BeginCFRule = 463;
    private const int EndCFRule = 464;
    private const int BeginColorScale = 469;
    private const int EndColorScale = 470;
    private const int CFVO = 471;
    private const int BeginDXFs = 505;
    private const int EndDXFs = 506;
    private const int DXF = 507;
    private const int ColorRecord = 564;
    private const int BeginList = 343;
    private const int BeginListCol = 347;
    private const int TableStyleRecord = 512;
    private const int BeginTableStyle = 510;
    private const int EndTableStyle = 511;
    private const int TableStyleClient = 513;
    private const int LegacyDrawing = 551;
    private const int CommentAuthor = 632;
    private const int BeginComment = 635;
    private const int CommentText = 637;
    private const int BeginListParts = 660;
    private const int ListPart = 661;
    private const int EndListParts = 662;
    private const int BeginRwBrk = 392;
    private const int EndRwBrk = 393;
    private const int BeginColBrk = 394;
    private const int EndColBrk = 395;
    private const int Brk = 396;
    private const int Margins = 476;
    private const int PrintOptions = 477;
    private const int PageSetup = 478;
    private const int BeginHeaderFooter = 479;
    private const int WsFmtInfo = 485;
    private const int Drawing = 550;
    private const int LegacyDrawingHF = 552;
    private const int BeginCellXfs = 617;
    private const int EndCellXfs = 618;
    private const int BeginCellStyleXfs = 626;
    private const int CsPageSetup = 652;
    private const int BeginUserCsView = 655;
    private const int EndUserCsView = 656;
    private const int EndCellStyleXfs = 627;

    /// <summary>Fill patterns by BrtFill fls (1 = solid, 0x28 = gradient), as SpreadsheetML names them.</summary>
    private static readonly string?[] Patterns =
    [
        null, "solid", "mediumGray", "darkGray", "lightGray", "darkHorizontal", "darkVertical", "darkDown", "darkUp", "darkGrid",
        "darkTrellis", "lightHorizontal", "lightVertical", "lightDown", "lightUp", "lightGrid", "lightTrellis", "gray125", "gray0625",
    ];

    /// <summary>Border styles by Blxf dg.</summary>
    private static readonly string[] BorderStyles =
    [
        "none", "thin", "medium", "dashed", "dotted", "thick", "double", "hair", "mediumDashed", "dashDot", "mediumDashDot",
        "dashDotDot", "mediumDashDotDot", "slantDashDot",
    ];

    private readonly OpcPackage _package;
    private readonly DocumentLimits _limits;
    private readonly List<(string Text, IReadOnlyList<CellRun>? Runs)> _sharedStrings = [];
    private readonly List<(CellStyle Style, string Format)> _cellFormats = [];
    private readonly List<FontInfo> _fonts = [];
    private readonly List<DifferentialStyle> _dxfs = [];

    /// <summary>The borders between cells (vertical, horizontal) of each BrtDXF, which table styles use.</summary>
    private readonly List<(CellBorder? Vertical, CellBorder? Horizontal)> _dxfInner = [];

    /// <summary>The authors of threaded comments by person id, read when a sheet first has threaded comments.</summary>
    private Dictionary<string, string>? _persons;

    /// <summary>The workbook's own table styles by name (BrtBeginTableStyle).</summary>
    private readonly Dictionary<string, TableStyle> _tableStyles = new(StringComparer.Ordinal);

    /// <summary>The look of a cell that sets nothing (the Normal style's font).</summary>
    private CellStyle _normal = CellStyle.Default;

    /// <summary>What the tables may still cost in this workbook.</summary>
    private readonly TableBudget _tableBudget = new();

    private int _tables;
    private OoxmlColors _colors = OoxmlColors.FromTheme(null);
    private XDocument? _theme;
    private readonly Charts.ChartBudget _charts = new();
    private NumberFormatter _formatter = new();
    private bool _date1904;
    private int _records;
    private int _columnEntries;

    private XlsbReader(OpcPackage package, DocumentLimits limits)
    {
        _package = package;
        _limits = limits;
    }

    /// <summary>Reads every visible sheet of <paramref name="path"/>.</summary>
    public static Workbook Read(string path) => Read(path, DocumentLimits.Default);

    internal static Workbook Read(string path, DocumentLimits limits)
    {
        // An encrypted workbook is a compound file holding the encrypted package; an Excel 97–2003 workbook may also
        // carry an .xlsb name.
        if (CompoundFile.HasSignature(path))
        {
            using (var cfb = CompoundFile.Open(path, limits))
            {
                if (cfb.Exists("EncryptedPackage") || cfb.Exists("EncryptionInfo"))
                    throw new InvalidDataException("The workbook is protected with a password (encrypted). Remove the password in Excel and convert it again.");
            }
            return XlsReader.Read(path);
        }
        using var package = new OpcPackage(path, limits);
        var workbookPath = package.MainPartPath ?? "xl/workbook.bin";
        // An XLSX saved with an .xlsb name: its workbook part is XML.
        if (workbookPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            return XlsxReader.Read(path);
        return new XlsbReader(package, limits).ReadWorkbook(workbookPath);
    }

    // ───────────────────────── Workbook ─────────────────────────

    /// <summary>A sheet of the workbook part: its visibility (0 visible, 1 hidden, 2 very hidden), relationship and name.</summary>
    internal readonly record struct SheetEntry(uint State, string? RelationshipId, string Name);

    private sealed class WorkbookPart
    {
        public List<SheetEntry> Sheets { get; } = [];
        public bool Date1904 { get; set; }

        /// <summary>Print titles and print areas by the sheet's position (hidden sheets count).</summary>
        public Dictionary<int, List<CellRange>> Titles { get; } = [];
        public Dictionary<int, List<CellRange>> Areas { get; } = [];
    }

    /// <summary>The sheets the workbook part lists, in order (for SheetCapabilities, which reads no cells).</summary>
    internal static List<SheetEntry> ListSheets(OpcPackage package, string workbookPath, DocumentLimits limits) =>
        new XlsbReader(package, limits).ReadWorkbookPart(workbookPath).Sheets;

    private WorkbookPart ReadWorkbookPart(string workbookPath)
    {
        var result = new WorkbookPart();
        using var stream = _package.OpenPart(workbookPath)
            ?? throw new InvalidDataException("The file is not an Excel binary workbook (no xl/workbook.bin).");
        var records = new Records(stream, workbookPath, this);
        while (records.Next())
        {
            switch (records.Type)
            {
                case WbProp:
                    var properties = records.Fields();
                    result.Date1904 = (properties.U32() & 1) != 0;
                    break;
                case BundleSh:
                    // Sheet ids run from 1 to 0xFFFF: more sheets than that is no workbook Excel wrote.
                    if (result.Sheets.Count >= 0xFFFF)
                        throw DocumentLimits.TooLarge("The workbook lists more than 65,535 sheets.");
                    var sheet = records.Fields();
                    var state = sheet.U32();
                    sheet.U32(); // iTabID
                    var relationship = sheet.String(nullable: true);
                    result.Sheets.Add(new SheetEntry(state, relationship, sheet.String() ?? ""));
                    break;
                case Name:
                    DefinedName(records.Fields(), result);
                    break;
            }
        }
        return result;
    }

    /// <summary>A BrtName: print titles and print areas, which are built-in names scoped to a sheet.</summary>
    private static void DefinedName(Fields name, WorkbookPart result)
    {
        var flags = name.U32();
        name.U8(); // chKey
        var sheet = name.U32();
        var text = name.String() ?? "";
        // A built-in name is "_xlnm.Print_Area", or "Print_Area" with fBuiltin set.
        if (sheet > int.MaxValue || (flags & 0x20) == 0 && !text.StartsWith("_xlnm.", StringComparison.OrdinalIgnoreCase))
            return;
        var target = (text.StartsWith("_xlnm.", StringComparison.OrdinalIgnoreCase) ? text[6..] : text) switch
        {
            var n when n.Equals("Print_Titles", StringComparison.OrdinalIgnoreCase) => result.Titles,
            var n when n.Equals("Print_Area", StringComparison.OrdinalIgnoreCase) => result.Areas,
            _ => null,
        };
        if (target is null)
            return;
        var length = name.U32();
        if (length > name.Left)
            return;
        target[(int)sheet] = Areas(name.Bytes((int)length));
    }

    /// <summary>
    /// The ranges of a defined name's parsed formula: 3-D references and areas, joined by unions (PtgUnion inside a
    /// PtgMemFunc); anything else ends the list.
    /// </summary>
    private static List<CellRange> Areas(ReadOnlySpan<byte> rgce)
    {
        var ranges = new List<CellRange>();
        var at = 0;
        while (at < rgce.Length)
        {
            var ptg = rgce[at++];
            switch (ptg)
            {
                case 0x3B or 0x5B or 0x7B when at + 14 <= rgce.Length: // PtgArea3d: ixti, RgceAreaRel
                    Add(BinaryPrimitives.ReadUInt32LittleEndian(rgce[(at + 2)..]), BinaryPrimitives.ReadUInt32LittleEndian(rgce[(at + 6)..]),
                        BinaryPrimitives.ReadUInt16LittleEndian(rgce[(at + 10)..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(rgce[(at + 12)..]) & 0x3FFF);
                    at += 14;
                    break;
                case 0x3A or 0x5A or 0x7A when at + 8 <= rgce.Length: // PtgRef3d: ixti, RgceLocRel
                    var row = BinaryPrimitives.ReadUInt32LittleEndian(rgce[(at + 2)..]);
                    var column = BinaryPrimitives.ReadUInt16LittleEndian(rgce[(at + 6)..]) & 0x3FFF;
                    Add(row, row, column, column);
                    at += 8;
                    break;
                case 0x29 or 0x49 or 0x69 when at + 2 <= rgce.Length: // PtgMemFunc: the expression follows
                    at += 2;
                    break;
                case 0x10: // PtgUnion
                    break;
                default:
                    return ranges;
            }
        }
        return ranges;

        void Add(uint top, uint bottom, int left, int right)
        {
            if (top <= bottom && bottom <= SheetPagination.MaxRow && left <= right && right <= SheetPagination.MaxColumn)
                ranges.Add(new CellRange((int)top, left, (int)bottom, right));
        }
    }

    /// <summary>
    /// Every worksheet of an Excel binary workbook, hidden ones too, with its position in the workbook part and its
    /// part (the print studio lists them; <see cref="Workbook.Sheets"/> holds them in the same order). Chart sheets are
    /// left out.
    /// </summary>
    internal static (Workbook Book, List<(int Index, string Part, bool Hidden)> Sheets) ReadAll(string path)
    {
        using var package = new OpcPackage(path, DocumentLimits.Default);
        var workbookPath = package.MainPartPath ?? "xl/workbook.bin";
        var reader = new XlsbReader(package, DocumentLimits.Default) { _all = [] };
        var book = reader.ReadWorkbook(workbookPath);
        return (book, reader._all);
    }

    /// <summary>The worksheets read, hidden ones too, while <see cref="ReadAll"/> reads; null otherwise.</summary>
    private List<(int Index, string Part, bool Hidden)>? _all;

    private Workbook ReadWorkbook(string workbookPath)
    {
        var part = ReadWorkbookPart(workbookPath);
        _date1904 = part.Date1904;
        _formatter = new NumberFormatter(_date1904);
        _theme = _package.TargetOfType(workbookPath, "/theme") is { } theme ? _package.Part(theme) : null;
        _colors = OoxmlColors.FromTheme(_theme);

        var result = new Workbook();
        // Styles first: rich text runs of the shared strings point into the fonts.
        if (_package.TargetOfType(workbookPath, "/styles") is { } styles)
            ReadStyles(styles, result);
        if (_package.TargetOfType(workbookPath, "/sharedStrings") is { } shared)
            ReadSharedStrings(shared);

        var relationships = _package.Relationships(workbookPath);
        for (var index = 0; index < part.Sheets.Count; index++)
        {
            var entry = part.Sheets[index];
            if (entry.State != 0 && _all is null || entry.RelationshipId is null || !relationships.TryGetValue(entry.RelationshipId, out var relationship)
                || relationship.External)
                continue; // hidden sheets
            if (relationship.Type.EndsWith("/chartsheet", StringComparison.Ordinal))
            {
                if (_all is not null)
                    continue;
                if (ReadChartSheet(entry.Name.Length > 0 ? entry.Name : "Chart", relationship.Target) is { } chartSheet)
                    result.ChartSheets.Add((result.Sheets.Count, chartSheet));
                continue;
            }
            if (!relationship.Type.EndsWith("/worksheet", StringComparison.Ordinal))
                continue; // dialog and macro sheets
            var sheet = ReadSheet(entry.Name.Length > 0 ? entry.Name : "Sheet", relationship.Target);
            if (part.Titles.TryGetValue(index, out var titles))
            {
                // Whole rows repeat at the top, whole columns at the left.
                foreach (var range in titles)
                {
                    if (range.Left == 0 && range.Right == SheetPagination.MaxColumn)
                        sheet.TitleRows ??= (range.Top, range.Bottom);
                    else if (range.Top == 0 && range.Bottom == SheetPagination.MaxRow)
                        sheet.TitleColumns ??= (range.Left, range.Right);
                }
            }
            if (part.Areas.TryGetValue(index, out var areas))
                sheet.PrintArea.AddRange(areas);
            result.Sheets.Add(sheet);
            _all?.Add((index, relationship.Target, entry.State != 0));
        }
        return result;
    }

    // ───────────────────────── Shared strings ─────────────────────────

    private void ReadSharedStrings(string partPath)
    {
        using var stream = _package.OpenPart(partPath);
        if (stream is null)
            return;
        var records = new Records(stream, partPath, this);
        while (records.Next())
        {
            if (records.Type == SstItem)
            {
                var item = records.Fields();
                _sharedStrings.Add(RichText(ref item));
            }
        }
    }

    /// <summary>A RichStr: the text and, when it has runs, the pieces in their fonts (phonetic text skipped).</summary>
    private (string Text, IReadOnlyList<CellRun>? Runs) RichText(ref Fields fields)
    {
        var flags = fields.U8();
        var text = fields.String() ?? "";
        if ((flags & 1) == 0)
            return (text, null);
        var count = fields.U32();
        if (count == 0 || count > 0x7FFF || count > fields.Left / 4)
            return (text, null);
        var starts = new List<(int Start, int Font)>();
        for (var i = 0; i < count; i++)
        {
            var start = fields.U16();
            var font = fields.U16();
            if (start < text.Length && (starts.Count == 0 || start > starts[^1].Start))
                starts.Add((start, font));
        }
        if (starts.Count == 0)
            return (text, null);
        var runs = new List<CellRun>();
        if (starts[0].Start > 0)
            runs.Add(new CellRun(text[..starts[0].Start]));
        for (var i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1].Start : text.Length;
            var piece = text[starts[i].Start..end];
            if (starts[i].Font >= _fonts.Count)
            {
                runs.Add(new CellRun(piece));
                continue;
            }
            var font = _fonts[starts[i].Font];
            runs.Add(new CellRun(piece)
            {
                Bold = font.Bold,
                Italic = font.Italic,
                Underline = font.Underline,
                Strike = font.Strike,
                FontSize = font.Size,
                Color = font.Color,
                FontName = font.Name,
                Script = font.Script,
            });
        }
        return (text, runs);
    }

    // ───────────────────────── Styles ─────────────────────────

    private sealed record FontInfo(bool Bold, bool Italic, bool Underline, bool DoubleUnderline, bool Strike, double? Size, string? Color, string? Name, int Script);

    private void ReadStyles(string partPath, Workbook book)
    {
        using var stream = _package.OpenPart(partPath);
        if (stream is null)
            return;
        var formats = new Dictionary<int, string>();
        var fills = new List<(string? Color, string? Pattern, string? Background, CellGradient? Gradient)>();
        var borders = new List<(CellBorder Left, CellBorder Right, CellBorder Top, CellBorder Bottom, CellBorder Up, CellBorder Down)>();
        var styleFonts = new List<int>();
        var cellXfs = new List<(int Format, int Font, int Fill, int Border, int Rotation, int Indent, int Alignment)>();
        var inCellXfs = false;
        var inStyleXfs = false;
        var inDxfs = false;
        // The table style being read and its parts (dxf ids into the BrtDXFs).
        string? tableStyle = null;
        List<(string Type, int Size, uint Dxf)>? tableParts = null;
        var ownStyles = new List<(string Name, List<(string Type, int Size, uint Dxf)> Parts)>();
        var records = new Records(stream, partPath, this);
        while (records.Next())
        {
            switch (records.Type)
            {
                case Fmt:
                    var format = records.Fields();
                    var id = format.U16();
                    var code = format.String();
                    formats.TryAdd(id, code ?? "General");
                    break;
                case Font:
                    _fonts.Add(ReadFont(records.Fields()));
                    break;
                case Fill:
                    fills.Add(ReadFill(records.Fields()));
                    break;
                case Border:
                    borders.Add(ReadBorder(records.Fields()));
                    break;
                case BeginCellXfs:
                    inCellXfs = true;
                    break;
                case EndCellXfs:
                    inCellXfs = false;
                    break;
                case BeginCellStyleXfs:
                    inStyleXfs = true;
                    break;
                case EndCellStyleXfs:
                    inStyleXfs = false;
                    break;
                case BeginDXFs:
                    inDxfs = true;
                    break;
                case EndDXFs:
                    inDxfs = false;
                    break;
                case DXF when inDxfs:
                    _dxfs.Add(ReadDifferential(records.Fields(), out var inner));
                    _dxfInner.Add(inner);
                    break;
                case BeginTableStyle:
                    var begin = records.Fields();
                    var tableFlags = begin.U16();
                    begin.U32(); // ctse
                    tableStyle = (tableFlags & 0x04) != 0 ? begin.String(nullable: true) : null;
                    tableParts = [];
                    break;
                case TableStyleRecord when tableParts is not null && tableParts.Count < 32:
                    var element = records.Fields();
                    var tse = element.U32();
                    var size = element.U32();
                    var dxfId = element.U32();
                    if (SheetTables.PartOf(tse) is { } part)
                        tableParts.Add((part, (int)Math.Clamp(size, 1, 9), dxfId));
                    break;
                case EndTableStyle:
                    if (tableStyle is { Length: > 0 } && tableParts is not null && ownStyles.Count < SheetTables.MaxTables)
                        ownStyles.Add((tableStyle, tableParts));
                    (tableStyle, tableParts) = (null, null);
                    break;
                case Xf when inCellXfs || inStyleXfs:
                    var xf = records.Fields();
                    xf.U16(); // ixfeParent
                    var numberFormat = xf.U16();
                    var font = xf.U16();
                    var fill = xf.U16();
                    var border = xf.U16();
                    var rotation = xf.U8();
                    var indent = xf.U8();
                    var alignment = xf.U16();
                    if (inStyleXfs)
                        styleFonts.Add(font);
                    else
                        cellXfs.Add((numberFormat, font, fill, border, rotation, indent, alignment));
                    break;
            }
        }

        // The Normal style's font (the first cell style): Excel measures column widths in its digits.
        var normal = styleFonts.Count > 0 && styleFonts[0] < _fonts.Count ? _fonts[styleFonts[0]] : _fonts.FirstOrDefault();
        if (normal?.Name is { Length: > 0 } fontName)
            book.DefaultFontName = fontName;
        if (normal?.Size is > 0 and <= 409 && normal.Size is { } fontSize)
            book.DefaultFontSize = fontSize;
        if (normal is not null)
            _normal = new CellStyle { Bold = normal.Bold, Italic = normal.Italic, Underline = normal.Underline, Strike = normal.Strike, Color = normal.Color };
        // The table styles' parts point into the BrtDXFs, which may come before or after them.
        foreach (var (name, parts) in ownStyles)
        {
            _tableStyles[name] = new TableStyle(parts.Where(p => p.Dxf < _dxfs.Count)
                .GroupBy(p => p.Type).ToDictionary(g => g.Key, g =>
                {
                    var (_, size, dxf) = g.First();
                    return new TableStylePart(_dxfs[(int)dxf], _dxfInner[(int)dxf].Vertical, _dxfInner[(int)dxf].Horizontal, size);
                }, StringComparer.Ordinal));
        }

        foreach (var (formatId, fontId, fillId, borderId, rotation, indent, alignment) in cellXfs)
        {
            var font = fontId < _fonts.Count ? _fonts[fontId] : null;
            var fill = fillId < fills.Count ? fills[fillId] : default;
            var border = borderId < borders.Count ? borders[borderId] : NoBorders;
            var horizontal = alignment & 0x07;
            var vertical = (alignment >> 3) & 0x07;
            var style = new CellStyle
            {
                Bold = font?.Bold ?? false,
                Italic = font?.Italic ?? false,
                Underline = font?.Underline ?? false,
                DoubleUnderline = font?.DoubleUnderline ?? false,
                Strike = font?.Strike ?? false,
                FontSize = font?.Size,
                FontName = font?.Name,
                Script = font?.Script ?? 0,
                Color = font?.Color,
                Fill = fill.Color,
                FillPattern = fill.Pattern,
                PatternBackground = fill.Background,
                Gradient = fill.Gradient,
                // alc: general, left, center, right, fill, justify, center across selection, distributed.
                Align = horizontal switch
                {
                    1 => CellAlign.Left,
                    2 or 6 => CellAlign.Center,
                    3 => CellAlign.Right,
                    _ => CellAlign.General,
                },
                SpecialAlign = horizontal switch
                {
                    4 => CellSpecialAlign.Fill,
                    5 => CellSpecialAlign.Justify,
                    6 => CellSpecialAlign.CenterAcross,
                    7 => CellSpecialAlign.Distributed,
                    _ => CellSpecialAlign.None,
                },
                // alcv: top, center, bottom, justify, distributed.
                VerticalAlign = vertical switch
                {
                    0 or 3 or 4 => CellVerticalAlign.Top,
                    1 => CellVerticalAlign.Center,
                    _ => CellVerticalAlign.Bottom,
                },
                VerticalJustify = vertical is 3 or 4,
                Wrap = (alignment & 0x40) != 0,
                Shrink = (alignment & 0x100) != 0,
                Indent = Math.Min(indent, 250),
                Rotation = XlsxReader.Rotation(rotation),
                Left = border.Left,
                Right = border.Right,
                Top = border.Top,
                Bottom = border.Bottom,
                DiagonalUp = border.Up,
                DiagonalDown = border.Down,
            };
            _cellFormats.Add((style, formats.GetValueOrDefault(formatId) ?? NumberFormatter.BuiltIn(formatId) ?? "General"));
        }
    }

    private static readonly (CellBorder Left, CellBorder Right, CellBorder Top, CellBorder Bottom, CellBorder Up, CellBorder Down) NoBorders =
        (CellBorder.None, CellBorder.None, CellBorder.None, CellBorder.None, CellBorder.None, CellBorder.None);

    private FontInfo ReadFont(Fields font)
    {
        var height = font.U16();
        var flags = font.U16();
        var weight = font.U16();
        var script = font.U16();
        var underline = font.U8();
        font.Skip(3); // bFamily, bCharSet, unused
        var color = Color(ref font);
        font.U8(); // bFontScheme
        var name = font.String();
        return new FontInfo(
            weight >= 700,
            (flags & 0x02) != 0,
            underline is not 0,
            underline is 0x02 or 0x22,
            (flags & 0x08) != 0,
            height is >= 20 and <= 0x1FFF ? height / 20.0 : null,
            color,
            name is { Length: > 0 } ? name : null,
            script switch { 1 => 1, 2 => -1, _ => 0 });
    }

    private (string? Color, string? Pattern, string? Background, CellGradient? Gradient) ReadFill(Fields fill)
    {
        var pattern = fill.U32();
        var foreground = Color(ref fill);
        var background = Color(ref fill);
        if (pattern == 0x28)
        {
            var type = fill.U32();
            var degree = fill.F64();
            fill.Skip(32); // the inner rectangle of a rectangular gradient
            var count = fill.U32();
            var stops = new List<(double Position, string Color)>();
            for (var i = 0; i < Math.Min(count, 16u) && fill.Left >= 16; i++)
            {
                var color = Color(ref fill);
                var position = fill.F64();
                if (color is not null)
                    stops.Add((double.IsFinite(position) ? Math.Clamp(position, 0, 1) : 0, color));
            }
            if (stops.Count == 0)
                return default;
            // A rectangular (path) gradient is drawn as a linear one from its first colour to its last.
            return (stops[0].Color, null, null, stops.Count > 1 ? new CellGradient(type == 0 && double.IsFinite(degree) ? degree : 0, stops) : null);
        }
        if (pattern >= Patterns.Length || Patterns[pattern] is not { } name || name == "gray125")
            return default;
        return name == "solid"
            ? (foreground ?? background, null, null, null)
            : (foreground ?? "#000000", name, background, null);
    }

    private (CellBorder Left, CellBorder Right, CellBorder Top, CellBorder Bottom, CellBorder Up, CellBorder Down) ReadBorder(Fields border)
    {
        var flags = border.U8();
        var top = Side(ref border);
        var bottom = Side(ref border);
        var left = Side(ref border);
        var right = Side(ref border);
        var diagonal = Side(ref border);
        return (left, right, top, bottom, (flags & 2) != 0 ? diagonal : CellBorder.None, (flags & 1) != 0 ? diagonal : CellBorder.None);
    }

    /// <summary>A table style by name: the workbook's own, else a built-in one in the workbook's theme colours.</summary>
    private TableStyle? TableStyleOf(string? name) =>
        name is null ? null : _tableStyles.TryGetValue(name, out var own) ? own : SheetTables.BuiltIn(name, ThemeColor);

    private string? ThemeColor(int theme, double tint) => _colors.Spreadsheet(new XElement("color", new XAttribute("theme", theme),
        new XAttribute("tint", tint.ToString("R", CultureInfo.InvariantCulture))));

    /// <summary>
    /// A BrtDXF: what a conditional format or a table style part changes, as XFProps (fill, font colour, bold, italic,
    /// underline, strike, the four borders and those between cells; the rest is left as it is, as for XLSX).
    /// </summary>
    private DifferentialStyle ReadDifferential(Fields dxf, out (CellBorder? Vertical, CellBorder? Horizontal) inner)
    {
        inner = (null, null);
        dxf.U16(); // fNewBorder
        dxf.U16(); // XFProps.reserved
        var count = dxf.U16();
        string? foreground = null, background = null;
        var style = new DifferentialStyle();
        for (var i = 0; i < count && dxf.Left >= 4; i++)
        {
            var type = dxf.U16();
            var size = dxf.U16();
            // cb counts the property's own four bytes.
            if (size < 4 || size - 4 > dxf.Left)
                break;
            var data = new Fields(dxf.Bytes(size - 4), "styles");
            switch (type)
            {
                case 0x01 when data.Left >= 8:
                    foreground = Color(ref data);
                    break;
                case 0x02 when data.Left >= 8:
                    background = Color(ref data);
                    break;
                case 0x05 when data.Left >= 8:
                    style = style with { Color = Color(ref data) };
                    break;
                case (>= 0x06 and <= 0x09) or 0x0B or 0x0C when data.Left >= 10:
                    var color = Color(ref data);
                    var line = data.U16();
                    CellBorder? side = line is > 0 and < 14 ? new CellBorder(BorderStyles[line], color) : null;
                    if (type == 0x0B)
                        inner = (side, inner.Horizontal);
                    else if (type == 0x0C)
                        inner = (inner.Vertical, side);
                    else
                    {
                        style = type switch
                        {
                            0x06 => style with { Top = side },
                            0x07 => style with { Bottom = side },
                            0x08 => style with { Left = side },
                            _ => style with { Right = side },
                        };
                    }
                    break;
                case 0x19 when data.Left >= 2:
                    style = style with { Bold = data.U16() >= 700 };
                    break;
                case 0x1A when data.Left >= 1:
                    style = style with { Underline = (data.Left >= 2 ? data.U16() : data.U8()) != 0 };
                    break;
                case 0x1C when data.Left >= 1:
                    style = style with { Italic = data.U8() != 0 };
                    break;
                case 0x1D when data.Left >= 1:
                    style = style with { Strike = data.U8() != 0 };
                    break;
            }
        }
        // A differential fill's colour is its background (the foreground only for patterns), as in XLSX.
        return style with { Fill = background ?? foreground };
    }

    /// <summary>A Blxf: the line style and colour of one border side.</summary>
    private CellBorder Side(ref Fields fields)
    {
        var style = fields.U8();
        fields.U8();
        var color = Color(ref fields);
        return style is 0 or >= 14 ? CellBorder.None : new CellBorder(BorderStyles[style], color);
    }

    /// <summary>
    /// A BrtColor as "#RRGGBB", or null for automatic. Theme indexes and tints mean what they mean in SpreadsheetML,
    /// so they go through the same resolution.
    /// </summary>
    private string? Color(ref Fields fields)
    {
        var type = fields.U8() >> 1;
        var index = fields.U8();
        var tint = fields.I16();
        var (red, green, blue) = (fields.U8(), fields.U8(), fields.U8());
        fields.U8(); // alpha
        var color = type switch
        {
            1 => new XElement("color", new XAttribute("indexed", index)),
            2 => new XElement("color", new XAttribute("rgb", $"FF{red:X2}{green:X2}{blue:X2}")),
            3 => new XElement("color", new XAttribute("theme", index)),
            _ => null,
        };
        if (color is null)
            return null;
        if (tint != 0)
            color.SetAttributeValue("tint", Math.Clamp(tint / 32767.0, -1, 1).ToString("R", CultureInfo.InvariantCulture));
        return _colors.Spreadsheet(color);
    }

    // ───────────────────────── Sheets ─────────────────────────

    private Worksheet ReadSheet(string name, string partPath)
    {
        var sheet = new Worksheet(name);
        using var stream = _package.OpenPart(partPath);
        if (stream is null)
            return sheet;

        var row = -1;
        var views = 0;
        var inView = false;
        var breaks = 0; // 1 = row breaks, 2 = column breaks
        var fitToPage = false;
        (uint Paper, uint Scale, int FirstPage, uint FitWidth, uint FitHeight, int Flags)? page = null;
        double[]? margins = null;
        int? options = null;
        SheetHeaderFooter? headerFooter = null;
        string? headerPictures = null;
        string? drawing = null;
        string? notes = null;
        var inListParts = false;
        var tableParts = new List<string>();

        List<CellRange>? formatRanges = null;
        ConditionalFormat? rule = null;
        var inScale = false;
        var points = new List<(string Type, double Value)>();
        var colors = new List<string>();
        var records = new Records(stream, partPath, this);
        while (records.Next())
        {
            switch (records.Type)
            {
                case BeginConditionalFormatting:
                    formatRanges = FormatRanges(records.Fields());
                    break;
                case EndConditionalFormatting:
                    formatRanges = null;
                    break;
                case BeginCFRule when formatRanges is { Count: > 0 }:
                    rule = ReadRule(records.Fields(), formatRanges);
                    points.Clear();
                    colors.Clear();
                    break;
                case BeginColorScale:
                    inScale = true;
                    break;
                case EndColorScale:
                    inScale = false;
                    break;
                case CFVO when inScale && rule is not null:
                    var cfvo = records.Fields();
                    var kind = cfvo.U32();
                    var number = cfvo.F64();
                    points.Add((kind switch { 2 => "min", 3 => "max", 4 => "percent", 5 => "percentile", _ => "num" }, double.IsFinite(number) ? number : 0));
                    break;
                case ColorRecord when inScale && rule is not null:
                    var colorFields = records.Fields();
                    colors.Add(Color(ref colorFields) ?? "#FFFFFF");
                    break;
                case EndCFRule:
                    if (rule is not null && (rule.Type != "colorScale" || points.Count >= 2 && colors.Count >= 2))
                    {
                        sheet.ConditionalFormats.Add(rule.Type == "colorScale"
                            ? rule with { Scale = [.. points.Zip(colors, (point, color) => new ScalePoint(point.Type, point.Value, color)).Take(3)] }
                            : rule);
                    }
                    rule = null;
                    break;
                case RowHdr:
                    row = ReadRow(records.Fields(), sheet);
                    break;
                case >= CellBlank and <= FmlaError or CellRString:
                    if (row >= 0)
                        ReadCell(records.Type, records.Fields(), row, sheet);
                    break;
                case WsProp:
                    fitToPage = (records.Fields().U16() & 0x100) != 0;
                    break;
                case WsFmtInfo:
                    ReadFormatInfo(records.Fields(), sheet);
                    break;
                case BeginWsView:
                    // The first view is the one Excel opens with.
                    inView = views++ == 0;
                    if (inView)
                        sheet.RightToLeft = (records.Fields().U16() & 0x20) != 0;
                    break;
                case EndWsView:
                    inView = false;
                    break;
                case Pane when inView:
                    var pane = records.Fields();
                    pane.F64(); // xnumXSplit: columns (the spec's prose swaps the two; the order is BIFF8 PANE's x, y)
                    var frozen = pane.F64();
                    pane.Skip(12);
                    if ((pane.U8() & 0x03) != 0 && double.IsFinite(frozen) && frozen is >= 0 and <= SheetPagination.MaxRow)
                        sheet.FrozenRows = (int)frozen;
                    break;
                case ColInfo:
                    ReadColumns(records.Fields(), sheet);
                    break;
                case MergeCell:
                    var merge = records.Fields();
                    var (top, bottom, left, right) = (merge.U32(), merge.U32(), merge.U32(), merge.U32());
                    if (top <= bottom && left <= right && (bottom > top || right > left) && bottom <= SheetPagination.MaxRow && right <= SheetPagination.MaxColumn)
                        sheet.Merges.Add(new CellRange((int)top, (int)left, (int)bottom, (int)right));
                    break;
                case BeginRwBrk:
                    breaks = 1;
                    break;
                case BeginColBrk:
                    breaks = 2;
                    break;
                case EndRwBrk or EndColBrk:
                    breaks = 0;
                    break;
                case Brk when breaks != 0:
                    var brk = records.Fields();
                    var at = brk.U32();
                    brk.Skip(8);
                    var manual = brk.U32() != 0;
                    if (manual && at > 0 && at <= (breaks == 1 ? SheetPagination.MaxRow : SheetPagination.MaxColumn))
                        (breaks == 1 ? sheet.RowBreaks : sheet.ColumnBreaks).Add((int)at);
                    break;
                case PrintOptions:
                    options = records.Fields().U16();
                    break;
                case Margins:
                    var fields = records.Fields();
                    margins = [fields.F64(), fields.F64(), fields.F64(), fields.F64(), fields.F64(), fields.F64()];
                    break;
                case PageSetup:
                    var setup = records.Fields();
                    var paper = setup.U32();
                    var scale = setup.U32();
                    setup.Skip(12); // iRes, iVRes, iCopies
                    var firstPage = setup.I32();
                    var fitWidth = setup.U32();
                    var fitHeight = setup.U32();
                    page = (paper, scale, firstPage, fitWidth, fitHeight, setup.U16());
                    break;
                case BeginHeaderFooter:
                    headerFooter = ReadHeaderFooter(records.Fields());
                    break;
                case Drawing:
                    drawing = records.Fields().String(nullable: true);
                    break;
                case LegacyDrawingHF:
                    headerPictures = records.Fields().String(nullable: true);
                    break;
                case LegacyDrawing:
                    notes = records.Fields().String(nullable: true);
                    break;
                case BeginListParts:
                    inListParts = true;
                    break;
                case EndListParts:
                    inListParts = false;
                    break;
                case ListPart when inListParts && tableParts.Count < SheetTables.MaxTables:
                    if (records.Fields().String(nullable: true) is { } tableRelation)
                        tableParts.Add(tableRelation);
                    break;
            }
        }

        sheet.PageSetup = PageSetupOf(page, margins, options, fitToPage, headerFooter, partPath, headerPictures);
        var tables = new List<SheetTable>();
        foreach (var relation in tableParts)
        {
            if (++_tables <= SheetTables.MaxTables && _package.Target(partPath, relation) is { } tablePart && ReadTable(tablePart) is { } table)
                tables.Add(table);
        }
        SheetTables.Apply(sheet, tables, TableStyleOf, _normal, _tableBudget);
        if (_package.TargetOfType(partPath, "/comments") is { } commentsPart)
            ReadComments(commentsPart, sheet, SheetComments.Vml(_package, _package.Target(partPath, notes)));
        SheetComments.Threaded(_package, partPath, sheet, () => _persons ??= SheetComments.Persons(_package, _package.MainPartPath ?? "xl/workbook.bin"));
        if (_package.Target(partPath, drawing) is { } drawingPart)
            sheet.Drawings.AddRange(XlsxDrawing.Read(_package, drawingPart, _theme?.Root, read: true, _charts).Drawings);
        return sheet;
    }

    /// <summary>
    /// A table part ([MS-XLSB] 2.1.7.51): BrtBeginList's range, header / total rows and formats (nDxfHeader, nDxfData,
    /// nDxfAgg and the border ones), each BrtBeginListCol's formats (nDxfHdr, nDxfInsertRow taken as its data rows',
    /// nDxfAgg), BrtTableStyleClient's style and options; null without a range.
    /// </summary>
    private SheetTable? ReadTable(string partPath)
    {
        using var stream = _package.OpenPart(partPath);
        if (stream is null)
            return null;
        SheetTable? table = null;
        var formats = new List<TableFormat>();
        var column = 0;
        void Format(uint dxf, TableArea area, bool outline, int? at)
        {
            if (dxf < _dxfs.Count)
                formats.Add(new TableFormat(area, new TableStylePart(_dxfs[(int)dxf], _dxfInner[(int)dxf].Vertical, _dxfInner[(int)dxf].Horizontal), outline, at));
        }
        var records = new Records(stream, partPath, this);
        while (records.Next())
        {
            switch (records.Type)
            {
                case BeginList when table is null:
                    var list = records.Fields();
                    var (top, bottom, left, right) = (list.U32(), list.U32(), list.U32(), list.U32());
                    list.Skip(8); // lt, idList
                    var header = list.U32();
                    var totals = list.U32();
                    if (top > bottom || left > right || bottom > SheetPagination.MaxRow || right > SheetPagination.MaxColumn)
                        return null;
                    if (list.Left >= 28)
                    {
                        list.Skip(4); // fShownTotalRow, fSingleCell, ...
                        var (headerDxf, dataDxf, totalsDxf) = (list.U32(), list.U32(), list.U32());
                        var (borderDxf, headerBorderDxf, totalsBorderDxf) = (list.U32(), list.U32(), list.U32());
                        Format(headerDxf, TableArea.Header, false, null);
                        Format(dataDxf, TableArea.Data, false, null);
                        Format(totalsDxf, TableArea.Totals, false, null);
                        Format(headerBorderDxf, TableArea.Header, true, null);
                        Format(borderDxf, TableArea.Data, true, null);
                        Format(totalsBorderDxf, TableArea.Totals, true, null);
                    }
                    table = new SheetTable(new CellRange((int)top, (int)left, (int)bottom, (int)right), null)
                    {
                        HeaderRows = header == 0 ? 0 : 1,
                        TotalRows = totals == 0 ? 0 : 1,
                        RowStripes = false,
                    };
                    break;
                case BeginListCol when table is not null && column <= SheetPagination.MaxColumn:
                    var listColumn = records.Fields();
                    if (listColumn.Left >= 20)
                    {
                        listColumn.Skip(8); // idField, ilta
                        var (headerDxf, insertDxf, totalsDxf) = (listColumn.U32(), listColumn.U32(), listColumn.U32());
                        Format(headerDxf, TableArea.Header, false, column);
                        Format(insertDxf, TableArea.Data, false, column);
                        Format(totalsDxf, TableArea.Totals, false, column);
                    }
                    column++;
                    break;
                case TableStyleClient when table is not null:
                    var client = records.Fields();
                    var flags = client.U16();
                    return table with
                    {
                        StyleName = client.String(nullable: true),
                        FirstColumn = (flags & 0x01) != 0,
                        LastColumn = (flags & 0x02) != 0,
                        RowStripes = (flags & 0x04) != 0,
                        ColumnStripes = (flags & 0x08) != 0,
                        Formats = formats,
                    };
            }
        }
        return table is null ? null : table with { Formats = formats };
    }

    /// <summary>The notes of a comments part ([MS-XLSB] 2.1.7.8): authors, then each note's cell and rich text.</summary>
    private void ReadComments(string partPath, Worksheet sheet, Dictionary<(int Row, int Column), (bool Shown, SheetShape? Box)> vml)
    {
        using var stream = _package.OpenPart(partPath);
        if (stream is null)
            return;
        var authors = new List<string>();
        (int Row, int Column)? cell = null;
        var records = new Records(stream, partPath, this);
        while (records.Next() && sheet.Comments.Count < SheetComments.MaxComments)
        {
            switch (records.Type)
            {
                case CommentAuthor when authors.Count < SheetComments.MaxComments:
                    authors.Add(records.Fields().String() ?? "");
                    break;
                case BeginComment:
                    var begin = records.Fields();
                    begin.I32(); // iauthor
                    var (top, _, left, _) = (begin.U32(), begin.U32(), begin.U32(), begin.U32());
                    cell = top <= SheetPagination.MaxRow && left <= SheetPagination.MaxColumn ? ((int)top, (int)left) : null;
                    break;
                case CommentText when cell is { } at:
                    var text = records.Fields();
                    var (plain, runs) = RichText(ref text);
                    SheetComments.Add(sheet, at.Row, at.Column, plain, runs, vml);
                    cell = null;
                    break;
            }
        }
    }

    /// <summary>
    /// A chart sheet part ([MS-XLSB] 2.1.7.7): its margins, BrtCsPageSetup, header and footer, and the drawing that holds
    /// its chart (DrawingML XML, as in XLSX); custom views' page setups are skipped. Null without a chart ChartReader draws.
    /// </summary>
    private Worksheet? ReadChartSheet(string name, string partPath)
    {
        using var stream = _package.OpenPart(partPath);
        if (stream is null)
            return null;
        (uint Paper, uint Scale, int FirstPage, uint FitWidth, uint FitHeight, int Flags)? page = null;
        double[]? margins = null;
        SheetHeaderFooter? headerFooter = null;
        string? headerPictures = null;
        string? drawing = null;
        var custom = false;
        var records = new Records(stream, partPath, this);
        while (records.Next())
        {
            switch (records.Type)
            {
                case BeginUserCsView:
                    custom = true;
                    break;
                case EndUserCsView:
                    custom = false;
                    break;
                case Margins when !custom:
                    var fields = records.Fields();
                    margins = [fields.F64(), fields.F64(), fields.F64(), fields.F64(), fields.F64(), fields.F64()];
                    break;
                case CsPageSetup when !custom:
                    var setup = records.Fields();
                    var paper = setup.U32();
                    setup.Skip(12); // iRes, iVRes, iCopies
                    var firstPage = setup.I16();
                    var flags = setup.U16();
                    // fLandscape, fNoOrient and fUsePage where BrtPageSetup keeps them.
                    page = (paper, 100, firstPage, 1, 1, ((flags & 0x01) != 0 ? 0x02 : 0) | ((flags & 0x08) != 0 ? 0x40 : 0) | ((flags & 0x10) != 0 ? 0x80 : 0));
                    break;
                case BeginHeaderFooter when !custom:
                    headerFooter = ReadHeaderFooter(records.Fields());
                    break;
                case Drawing:
                    drawing = records.Fields().String(nullable: true);
                    break;
                case LegacyDrawingHF:
                    headerPictures = records.Fields().String(nullable: true);
                    break;
            }
        }
        if (_package.Target(partPath, drawing) is not { } drawingPart)
            return null;
        // BrtBeginCsView keeps no "sized with window" flag: a chart clearly smaller than the page keeps its size.
        return XlsxReader.ChartSheetOf(_package, drawingPart, name, PageSetupOf(page, margins, null, false, headerFooter, partPath, headerPictures),
            fillsPage: false, _theme?.Root, _charts);
    }

    /// <summary>The ranges of a BrtBeginConditionalFormatting (ccf, fPivot, then an UncheckedSqRfX).</summary>
    private static List<CellRange> FormatRanges(Fields fields)
    {
        fields.U32(); // ccf
        fields.U32(); // fPivot
        var count = fields.U32();
        var ranges = new List<CellRange>();
        for (var i = 0u; i < count && fields.Left >= 16; i++)
        {
            var (top, bottom, left, right) = (fields.U32(), fields.U32(), fields.U32(), fields.U32());
            if (top <= bottom && left <= right && bottom <= SheetPagination.MaxRow && right <= SheetPagination.MaxColumn)
                ranges.Add(new CellRange((int)top, (int)left, (int)bottom, (int)right));
        }
        return ranges;
    }

    /// <summary>
    /// A BrtBeginCFRule as the rule the XLSX reader makes of a cfRule: comparisons with constants, text, blanks,
    /// errors, top / bottom N, averages, duplicates and colour scales (their points follow). Rules with formulas of their
    /// own, time periods, data bars and icon sets are not evaluated (null), as for XLSX.
    /// </summary>
    private ConditionalFormat? ReadRule(Fields fields, List<CellRange> ranges)
    {
        var type = fields.U32();
        var template = fields.U32();
        var dxf = fields.U32();
        var priority = fields.I32();
        var parameter = fields.U32();
        fields.Skip(8); // reserved1, reserved2
        var flags = fields.U16();
        var first = fields.U32();
        var second = fields.U32();
        fields.U32(); // cbFmla3
        var text = fields.String(nullable: true);
        var formulas = new List<string>();
        foreach (var length in new[] { first, second })
        {
            if (length == 0)
                break;
            // A CFParsedFormula: cce, rgce, cb, rgcb.
            var cce = fields.U32();
            if (cce > fields.Left)
                return null;
            var constant = Constant(fields.Bytes((int)cce));
            var cb = fields.U32();
            if (cb > fields.Left)
                return null;
            fields.Skip((int)cb);
            if (constant is null)
                return null; // a reference or a formula: not evaluated
            formulas.Add(constant);
        }
        var name = (type, template) switch
        {
            (1, _) => "cellIs",
            (3, _) => "colorScale",
            (5, _) => "top10",
            (2, 7) => "uniqueValues",
            (2, 8) => parameter switch { 0 => "containsText", 1 => "notContainsText", 2 => "beginsWith", 3 => "endsWith", _ => null },
            (2, 9) => "containsBlanks",
            (2, 10) => "notContainsBlanks",
            (2, 11) => "containsErrors",
            (2, 12) => "notContainsErrors",
            (2, 25 or 26 or 29 or 30) => "aboveAverage",
            (2, 27) => "duplicateValues",
            _ => null,
        };
        if (name is null)
            return null;
        return new ConditionalFormat(ranges, name, priority)
        {
            Operator = type != 1 ? null : parameter switch
            {
                1 => "between",
                2 => "notBetween",
                3 => "equal",
                4 => "notEqual",
                5 => "greaterThan",
                6 => "lessThan",
                7 => "greaterThanOrEqual",
                8 => "lessThanOrEqual",
                _ => null,
            },
            Formulas = formulas,
            Text = text,
            Rank = type == 5 ? (int)Math.Min(parameter, 1000) : 10,
            Percent = (flags & 0x10) != 0,
            Bottom = (flags & 0x08) != 0,
            AboveAverage = template is not (26 or 30),
            EqualAverage = template is 29 or 30,
            StdDev = template is 25 or 26 ? (int)Math.Min(parameter, 3) : 0,
            StopIfTrue = (flags & 0x02) != 0,
            Style = type == 3 || dxf >= _dxfs.Count ? null : _dxfs[(int)dxf],
        };
    }

    /// <summary>
    /// A rule's formula as the XLSX reader keeps it, when it is a constant: a number (PtgInt, PtgNum, perhaps negated by
    /// PtgUminus), text in quotes (PtgStr) or a boolean (PtgBool, as 1 or 0); null for anything else.
    /// </summary>
    internal static string? Constant(ReadOnlySpan<byte> rgce)
    {
        var negate = false;
        string? value = null;
        var at = 0;
        while (at < rgce.Length)
        {
            var ptg = rgce[at++];
            switch (ptg)
            {
                case 0x1E when at + 2 <= rgce.Length && value is null: // PtgInt
                    value = BinaryPrimitives.ReadUInt16LittleEndian(rgce[at..]).ToString(CultureInfo.InvariantCulture);
                    at += 2;
                    break;
                case 0x1F when at + 8 <= rgce.Length && value is null: // PtgNum
                    var number = BinaryPrimitives.ReadDoubleLittleEndian(rgce[at..]);
                    if (!double.IsFinite(number))
                        return null;
                    value = number.ToString("R", CultureInfo.InvariantCulture);
                    at += 8;
                    break;
                case 0x1D when at + 1 <= rgce.Length && value is null: // PtgBool
                    value = rgce[at++] != 0 ? "1" : "0";
                    break;
                case 0x17 when at + 2 <= rgce.Length && value is null: // PtgStr: cch, then UTF-16
                    var count = BinaryPrimitives.ReadUInt16LittleEndian(rgce[at..]);
                    at += 2;
                    if (at + count * 2 > rgce.Length)
                        return null;
                    value = '"' + Encoding.Unicode.GetString(rgce.Slice(at, count * 2)).Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
                    at += count * 2;
                    break;
                case 0x13 when value is not null && value[0] != '"': // PtgUminus, after its operand
                    negate = !negate;
                    break;
                case 0x12 or 0x15: // PtgUplus, PtgParen
                    break;
                default:
                    return null;
            }
        }
        if (value is null || !negate)
            return value;
        return value.StartsWith('-') ? value[1..] : "-" + value;
    }

    /// <summary>A BrtRowHdr: the row's height and visibility; returns the row (or -1 for one beyond the sheet).</summary>
    private static int ReadRow(Fields fields, Worksheet sheet)
    {
        var row = fields.U32();
        fields.U32(); // ixfe
        var height = fields.U16();
        fields.U8();
        var flags = fields.U8();
        if (row > SheetPagination.MaxRow)
            return -1;
        var r = (int)row;
        if ((flags & 0x10) != 0) // fDyZero
            sheet.HiddenRows.Add(r);
        // miyRw in twips: the custom height (fUnsynced), else the height Excel last fitted the row to.
        if (height is > 0 and <= 8192)
        {
            if ((flags & 0x20) != 0)
                sheet.RowHeights[r] = height / 20.0;
            else
                sheet.AutoRowHeights[r] = height / 20.0;
        }
        return r;
    }

    private void ReadCell(int type, Fields fields, int row, Worksheet sheet)
    {
        var column = fields.U32();
        var s = (int)(fields.U32() & 0xFFFFFF);
        if (column > SheetPagination.MaxColumn)
            return;
        var (style, format) = s < _cellFormats.Count ? _cellFormats[s] : (CellStyle.Default, "General");
        var cell = type switch
        {
            CellBlank => new SheetCell("", style),
            CellRk => Number(Rk(fields.U32()), style, format),
            CellReal or FmlaNum => Number(fields.F64(), style, format),
            CellError or FmlaError => new SheetCell(ErrorText(fields.U8()), style) { IsError = true },
            CellBool or FmlaBool => fields.U8() != 0
                ? new SheetCell("TRUE", style) { Value = 1, IsBoolean = true }
                : new SheetCell("FALSE", style) { Value = 0, IsBoolean = true },
            CellSt or FmlaString => new SheetCell(fields.String() ?? "", style),
            CellIsst => fields.U32() is var index && index < _sharedStrings.Count
                ? new SheetCell(_sharedStrings[(int)index].Text, style) { Runs = _sharedStrings[(int)index].Runs }
                : new SheetCell("", style),
            _ => RichCell(ref fields, style),
        };
        if (cell.HasContent)
            sheet.Set(row, (int)column, cell);
    }

    private SheetCell RichCell(ref Fields fields, CellStyle style)
    {
        var (text, runs) = RichText(ref fields);
        return new SheetCell(text, style) { Runs = runs };
    }

    /// <summary>An RkNumber: the high 30 bits of a double or a 30-bit integer, perhaps × 100.</summary>
    internal static double Rk(uint raw)
    {
        var value = (raw & 0x02) != 0 ? (int)raw >> 2 : BitConverter.Int64BitsToDouble((long)(raw & 0xFFFFFFFC) << 32);
        return (raw & 0x01) != 0 ? value / 100 : value;
    }

    /// <param name="value">The stored number, in the workbook's own date system.</param>
    private SheetCell Number(double value, CellStyle style, string format) => !double.IsFinite(value)
        ? new SheetCell("#NUM!", style) { IsError = true }
        : new SheetCell(_formatter.Format(value, format), style, IsNumber: true)
        {
            Value = _date1904 && _formatter.IsDate(format) ? value + Date1904Offset : value,
            NumberFormat = NumberFormatter.IsGeneral(format) ? null : format,
        };

    private static string ErrorText(byte code) => code switch
    {
        0x00 => "#NULL!",
        0x07 => "#DIV/0!",
        0x0F => "#VALUE!",
        0x17 => "#REF!",
        0x1D => "#NAME?",
        0x24 => "#NUM!",
        0x2A => "#N/A",
        _ => "#GETTING_DATA",
    };

    /// <summary>A BrtWsFmtInfo: the default column width (or Excel's base width) and the default row height.</summary>
    private static void ReadFormatInfo(Fields fields, Worksheet sheet)
    {
        var width = fields.U32();
        var baseWidth = fields.U16();
        var height = fields.U16();
        if (width != 0xFFFFFFFF && width / 256.0 <= 255)
        {
            sheet.DefaultColumnWidth = width / 256.0;
            sheet.HasDefaultColumnWidth = true;
        }
        else if (width == 0xFFFFFFFF && baseWidth <= 255)
        {
            sheet.BaseColumnWidth = baseWidth;
        }
        if (height is > 0 and <= 8190)
            sheet.DefaultRowHeight = height / 20.0;
    }

    /// <summary>A BrtColInfo: the width (in characters × 256) and visibility of a range of columns.</summary>
    private void ReadColumns(Fields fields, Worksheet sheet)
    {
        var first = fields.U32();
        var last = Math.Min(fields.U32(), (uint)SheetPagination.MaxColumn);
        var width = Math.Min(fields.U32(), 65535) / 256.0; // Excel's widest column is 255 characters
        fields.U32(); // ixfe
        var hidden = (fields.U16() & 0x01) != 0 || width == 0;
        if (first > last)
            return;
        if ((_columnEntries += (int)(last - first + 1)) > MaxColumnEntries)
            throw DocumentLimits.Exceeded("The workbook sets more than 4,000,000 column widths.");
        for (var c = (int)first; c <= last; c++)
        {
            sheet.ColumnWidths[c] = width;
            if (hidden)
                sheet.HiddenColumns.Add(c);
        }
    }

    private static SheetHeaderFooter ReadHeaderFooter(Fields fields)
    {
        var flags = fields.U16();
        return new SheetHeaderFooter
        {
            OddHeader = fields.String(nullable: true),
            OddFooter = fields.String(nullable: true),
            EvenHeader = fields.String(nullable: true),
            EvenFooter = fields.String(nullable: true),
            FirstHeader = fields.String(nullable: true),
            FirstFooter = fields.String(nullable: true),
            DifferentOddEven = (flags & 0x01) != 0,
            DifferentFirst = (flags & 0x02) != 0,
            ScaleWithDocument = (flags & 0x04) != 0,
            AlignWithMargins = (flags & 0x08) != 0,
        };
    }

    /// <summary>The page setup the way the XLSX reader makes it from pageSetup, pageMargins, printOptions and headerFooter.</summary>
    private SheetPageSetup PageSetupOf((uint Paper, uint Scale, int FirstPage, uint FitWidth, uint FitHeight, int Flags)? page, double[]? margins,
        int? options, bool fitToPage, SheetHeaderFooter? headerFooter, string partPath, string? headerPictures)
    {
        var setup = page is null ? SheetPageSetup.ForRegion() : new SheetPageSetup { PaperWidth = Sheets.Paper.Letter.Width, PaperHeight = Sheets.Paper.Letter.Height };
        if (page is { } p)
        {
            if (p.Paper <= int.MaxValue && Sheets.Paper.FromCode((int)p.Paper) is { } paper)
                setup = setup with { PaperWidth = paper.Width, PaperHeight = paper.Height };
            setup = setup with
            {
                // fNoOrient leaves the orientation to the printer.
                Landscape = (p.Flags & 0x02) != 0 && (p.Flags & 0x40) == 0,
                Scale = p.Scale is >= 10 and <= 400 ? (int)p.Scale : 100,
                FitWidth = p.FitWidth <= 32767 ? (int)p.FitWidth : 1,
                FitHeight = p.FitHeight <= 32767 ? (int)p.FitHeight : 1,
                OverThenDown = (p.Flags & 0x01) != 0,
                FirstPageNumber = (p.Flags & 0x80) != 0 && p.FirstPage is >= -32768 and <= 32767 ? p.FirstPage : null,
                // fNotes prints the notes, fEndNotes after the sheet rather than as displayed.
                Comments = (p.Flags & 0x20) == 0 ? SheetCommentsPrint.None : (p.Flags & 0x100) != 0 ? SheetCommentsPrint.AtEnd : SheetCommentsPrint.AsDisplayed,
            };
        }
        setup = setup with { FitToPage = fitToPage };
        if (margins is not null)
        {
            double Margin(double inches, double fallback) => double.IsFinite(inches) && inches is >= 0 and <= 50 ? inches * 72 : fallback;
            setup = setup with
            {
                LeftMargin = Margin(margins[0], setup.LeftMargin),
                RightMargin = Margin(margins[1], setup.RightMargin),
                TopMargin = Margin(margins[2], setup.TopMargin),
                BottomMargin = Margin(margins[3], setup.BottomMargin),
                HeaderMargin = Margin(margins[4], setup.HeaderMargin),
                FooterMargin = Margin(margins[5], setup.FooterMargin),
            };
        }
        if (options is { } o)
        {
            setup = setup with
            {
                CenterHorizontally = (o & 0x01) != 0,
                CenterVertically = (o & 0x02) != 0,
                PrintHeadings = (o & 0x04) != 0,
                PrintGridLines = (o & 0x08) != 0,
            };
        }
        if (headerFooter is not null)
        {
            setup = setup with
            {
                HeaderFooter = headerFooter with
                {
                    Pictures = _package.Target(partPath, headerPictures) is { } vml ? XlsxReader.HeaderPictures(_package, vml) : [],
                },
            };
        }
        return setup;
    }

    // ───────────────────────── Records ─────────────────────────

    /// <summary>Counts a record against what one workbook may hold (as many as the XML elements an XLSX may).</summary>
    private void Count()
    {
        if (++_records > _limits.MaxXmlElements)
            throw DocumentLimits.Exceeded($"The workbook has more than {DocumentLimits.Count(_limits.MaxXmlElements)} records.");
    }

    /// <summary>The records of one part, read one after the other.</summary>
    private sealed class Records(Stream stream, string part, XlsbReader reader)
    {
        private readonly BufferedStream _stream = new(stream, 64 * 1024);
        private byte[] _data = new byte[256];
        private int _length;
        private bool _tooLarge;

        public int Type { get; private set; }

        /// <summary>Moves to the next record; false at the end of the part.</summary>
        public bool Next()
        {
            var low = _stream.ReadByte();
            if (low < 0)
                return false;
            var type = low & 0x7F;
            if ((low & 0x80) != 0)
                type |= (Byte() & 0x7F) << 7;
            var size = 0;
            for (var i = 0; i < 4; i++)
            {
                var b = Byte();
                size |= (b & 0x7F) << (7 * i);
                if ((b & 0x80) == 0 || i == 3)
                    break;
            }
            reader.Count();
            Type = type;
            _tooLarge = size > MaxRecordBytes;
            if (_tooLarge)
            {
                // Kept out of memory: only records the reader skips may be this large.
                Skip(size);
                _length = 0;
                return true;
            }
            if (_data.Length < size)
                _data = new byte[Math.Max(size, _data.Length * 2)];
            if (_stream.ReadAtLeast(_data.AsSpan(0, size), size, throwOnEndOfStream: false) != size)
                throw Damaged();
            _length = size;
            return true;
        }

        /// <summary>The fields of the current record.</summary>
        public Fields Fields() => _tooLarge
            ? throw DocumentLimits.TooLarge($"A record of {part} holds more than {DocumentLimits.Megabytes(MaxRecordBytes)}.")
            : new Fields(_data.AsSpan(0, _length), part);

        private int Byte()
        {
            var b = _stream.ReadByte();
            return b < 0 ? throw Damaged() : b;
        }

        private void Skip(int count)
        {
            Span<byte> buffer = stackalloc byte[4096];
            while (count > 0)
            {
                var read = _stream.Read(buffer[..Math.Min(count, buffer.Length)]);
                if (read <= 0)
                    throw Damaged();
                count -= read;
            }
        }

        private InvalidDataException Damaged() => new($"The part {part} of the workbook ends inside a record. The file may be damaged.");
    }

    /// <summary>The fields of one record, read in order; reading past its end is damage.</summary>
    private ref struct Fields(ReadOnlySpan<byte> data, string part)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _at;

        public readonly int Left => _data.Length - _at;

        public byte U8() => Take(1)[0];

        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

        public short I16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));

        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        public double F64() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

        public void Skip(int count) => Take(count);

        public ReadOnlySpan<byte> Bytes(int count) => Take(count);

        /// <summary>An XLWideString (or, <paramref name="nullable"/>, an XLNullableWideString whose 0xFFFFFFFF length is null).</summary>
        public string? String(bool nullable = false)
        {
            var count = U32();
            if (nullable && count == 0xFFFFFFFF)
                return null;
            if (count > Left / 2)
                throw Damaged();
            return Encoding.Unicode.GetString(Take((int)count * 2));
        }

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > Left)
                throw Damaged();
            var span = _data.Slice(_at, count);
            _at += count;
            return span;
        }

        private readonly InvalidDataException Damaged() => new($"A record of {part} in the workbook is shorter than its fields. The file may be damaged.");
    }
}
