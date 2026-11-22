// Workbook → XLS (Excel 97–2003, BIFF8) and XLT, its template, without Excel or LibreOffice, written from [MS-XLS] and
// [MS-CFB]: a compound file (CompoundFileWriter) holding one "Workbook" stream.
//  * The globals substream holds the fonts, number formats, cell formats (XF) and the 56-colour palette, the sheets
//    (BOUNDSHEET), the print areas and titles (built-in NAMEs through a SUPBOOK / EXTERNSHEET to the workbook itself)
//    and the shared strings (SST, split over CONTINUE records, with the EXTSST index). A template adds TEMPLATE.
//  * Each worksheet substream holds the print options and page setup, the column widths (DEFCOLWIDTH, COLINFO,
//    STANDARDWIDTH), the rows in blocks of 32 with their cells (NUMBER, LABELSST, BOOLERR, BLANK) and a DBCELL each,
//    located by INDEX, the window (WINDOW2, PANE for frozen header rows) and the merged cells.
// Cells keep their values (the model holds no formulas): numbers and dates as numbers with their formats, booleans
// and errors as such, text in the SST. Colours become palette entries: 56 distinct ones exactly, others the nearest.
// Rows the model stores no height for get what Excel's autofit gives (SheetRowHeights), as Excel stores it.
// BIFF8 holds 65,536 rows, 256 columns, 32,767 characters a cell and about 4,000 cell formats: what doesn't fit is left
// out or cut, and Write says so (the converter shows notice.xls_limits).

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Filee.Engines.Hwp.Binary;

namespace Filee.Engines.Office.Sheets;

internal static class XlsWriter
{
    public const int MaxRows = 65_536;
    public const int MaxColumns = 256;

    /// <summary>Characters a cell holds at most.</summary>
    public const int MaxText = 32_767;

    /// <summary>Cell formats (XF records) besides the 16 every workbook starts with; Excel 97–2003 takes about 4,000.</summary>
    internal const int MaxCellFormats = 3_984;

    /// <summary>FONT records Excel 97–2003 takes at most (510), less the four for the Normal style.</summary>
    private const int MaxFonts = 500;

    /// <summary>Number formats of the workbook's own (FORMAT records past the built-in ones; 218 at most in all).</summary>
    private const int MaxFormats = 200;

    /// <summary>Bytes of data a BIFF8 record holds at most; longer data goes on in CONTINUE records.</summary>
    private const int MaxRecord = 8_224;

    private const int RowsPerBlock = 32;

    /// <summary>Excel 97–2003 reads a Workbook stream shorter than the mini stream cutoff badly: it is padded to it.</summary>
    private const int MinimumStream = CompoundFileWriter.MiniStreamCutoff;

    private const int DefaultCellXf = 15;
    private const int FirstCellXf = 16;
    private const int FirstCustomFormat = 164;
    private const int ShortDateFormat = 14;
    private const ushort AutomaticFontColor = 0x7FFF;
    private const int AutomaticForeground = 0x40;
    private const int AutomaticBackground = 0x41;

    private const int Bof = 0x0809, Eof = 0x000A, Continue = 0x003C;

    /// <summary>Writes <paramref name="book"/> as an XLS file (an XLT template with <paramref name="template"/>).</summary>
    /// <returns>True when something didn't fit BIFF8's limits and was left out or cut.</returns>
    public static bool Write(Workbook book, string path, bool template = false, CancellationToken cancellationToken = default)
    {
        var stream = Build(book, template, out var clipped, cancellationToken);
        var file = new CompoundFileWriter();
        file.Add("Workbook", stream);
        using var output = File.Create(path);
        file.Write(output);
        return clipped;
    }

    /// <summary>The Workbook stream of <paramref name="book"/>.</summary>
    internal static byte[] Build(Workbook book, bool template, out bool clipped, CancellationToken cancellationToken = default)
    {
        var sheets = book.Sheets.Count > 0 ? book.Sheets : [new Worksheet("Sheet1")];
        var names = XlsxWriter.UniqueNames(sheets.Select(s => s.Name));
        var context = new Context(book);
        // The cell formats and strings every cell uses, first: the globals come before the sheets.
        foreach (var sheet in sheets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Collect(sheet);
        }
        context.Styles.Palette.Assign();

        var (globals, sheetOffsets) = Globals(book, sheets, names, context, template);
        using var fonts = new SheetFonts();
        var parts = new List<byte[]> { globals };
        var offset = (long)globals.Length;
        for (var i = 0; i < sheets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (offset > int.MaxValue)
                throw new InvalidDataException("The workbook is too large for an Excel 97–2003 file.");
            BinaryPrimitives.WriteInt32LittleEndian(globals.AsSpan(sheetOffsets[i]), (int)offset);
            var part = SheetStream(book, sheets[i], context, (int)offset, selected: i == 0, fonts, cancellationToken);
            parts.Add(part);
            offset += part.Length;
        }
        if (offset > int.MaxValue)
            throw new InvalidDataException("The workbook is too large for an Excel 97–2003 file.");

        var stream = new byte[Math.Max(offset, MinimumStream)];
        var at = 0;
        foreach (var part in parts)
        {
            part.CopyTo(stream, at);
            at += part.Length;
        }
        clipped = context.Clipped || context.Styles.Clipped;
        return stream;
    }

    // ───────────────────────── Cells ─────────────────────────

    private enum Kind
    {
        Blank,
        Number,
        Text,
        Boolean,
        Error,
    }

    /// <summary>What a cell is written as, with its number, text (cut to <see cref="MaxText"/>) or error code.</summary>
    private readonly record struct Value(Kind Kind, double Number, string Text, byte Code, bool Cut);

    private static Value Classify(SheetCell cell)
    {
        if (cell.IsError && ErrorCode(cell.Text) is { } code)
            return new Value(Kind.Error, 0, "", code, false);
        var number = cell.Value ?? (cell.IsNumber && double.TryParse(cell.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null);
        if (cell.IsBoolean && number is { } flag)
            return new Value(Kind.Boolean, flag != 0 ? 1 : 0, "", 0, false);
        if (number is { } value && double.IsFinite(value))
            return new Value(Kind.Number, value, "", 0, false);
        if (cell.Text.Length == 0)
            return new Value(Kind.Blank, 0, "", 0, false);
        var cut = cell.Text.Length > MaxText;
        var text = cut ? cell.Text[..(char.IsHighSurrogate(cell.Text[MaxText - 1]) ? MaxText - 1 : MaxText)] : cell.Text;
        return new Value(Kind.Text, 0, text, 0, cut);
    }

    /// <summary>The BErr code of an error value's text, or null for text Excel has no error for.</summary>
    private static byte? ErrorCode(string text) => text switch
    {
        "#NULL!" => 0x00,
        "#DIV/0!" => 0x07,
        "#VALUE!" => 0x0F,
        "#REF!" => 0x17,
        "#NAME?" => 0x1D,
        "#NUM!" => 0x24,
        "#N/A" => 0x2A,
        "#GETTING_DATA" => 0x2B,
        _ => null,
    };

    /// <summary>The cell formats and shared strings of the whole workbook, and whether something was left out.</summary>
    private sealed class Context(Workbook book)
    {
        public Styles Styles { get; } = new(book);
        public Dictionary<string, int> Strings { get; } = new(StringComparer.Ordinal);
        public List<string> StringList { get; } = [];
        public int StringReferences { get; private set; }
        public bool Clipped { get; set; }

        public void Collect(Worksheet sheet)
        {
            foreach (var (row, cells) in sheet.Rows)
            {
                if (row < 0)
                    continue;
                if (row >= MaxRows)
                {
                    Clipped |= cells.Count > 0;
                    break;
                }
                foreach (var (column, cell) in cells)
                {
                    if (column < 0)
                        continue;
                    if (column >= MaxColumns)
                    {
                        Clipped = true;
                        break;
                    }
                    var value = Classify(cell);
                    Styles.Index(cell.Style, value.Kind == Kind.Number ? cell.NumberFormat : null);
                    if (value.Kind != Kind.Text)
                        continue;
                    Clipped |= value.Cut;
                    StringReferences++;
                    if (!Strings.ContainsKey(value.Text))
                    {
                        Strings[value.Text] = StringList.Count;
                        StringList.Add(value.Text);
                    }
                }
            }
            Clipped |= sheet.Merges.Any(m => m.Bottom >= MaxRows || m.Right >= MaxColumns);
        }
    }

    // ───────────────────────── Globals ─────────────────────────

    /// <summary>The globals substream, and where each BOUNDSHEET's offset to its sheet goes.</summary>
    private static (byte[] Stream, List<int> SheetOffsets) Globals(Workbook book, IList<Worksheet> sheets, List<string> names, Context context, bool template)
    {
        var w = new RecordWriter();
        w.Record(Bof, BofData(0x0005));
        if (template)
            w.Record(0x0060, []); // TEMPLATE: the workbook is a template
        w.Record(0x00E1, U16(1200)); // INTERFACEHDR: UTF-16
        w.Record(0x00C1, U16(0)); // MMS
        w.Record(0x00E2, []); // INTERFACEEND
        var access = new byte[112];
        Array.Fill(access, (byte)' ');
        UnicodeString("Filee").CopyTo(access, 0);
        w.Record(0x005C, access); // WRITEACCESS: padded with spaces
        w.Record(0x0042, U16(1200)); // CODEPAGE: UTF-16
        w.Record(0x0161, U16(0)); // DSF
        w.Long(0x013D, [.. Enumerable.Range(1, sheets.Count).SelectMany(i => U16(i))]); // RRTABID: sheet ids
        w.Record(0x0019, U16(0)); // WINPROTECT
        w.Record(0x0012, U16(0)); // PROTECT
        w.Record(0x0013, U16(0)); // PASSWORD
        w.Record(0x01AF, U16(0)); // PROT4REV
        w.Record(0x01BC, U16(0)); // PROT4REVPASS
        // WINDOW1: position and size in twips, scroll bars and sheet tabs shown, the first sheet active and selected.
        w.Record(0x003D, [.. U16(360), .. U16(270), .. U16(14_940), .. U16(9_150), .. U16(0x0038), .. U16(0), .. U16(0), .. U16(1), .. U16(600)]);
        w.Record(0x0040, U16(0)); // BACKUP
        w.Record(0x008D, U16(0)); // HIDEOBJ: objects shown
        w.Record(0x0022, U16(0)); // DATE1904: dates count from 1900
        w.Record(0x000E, U16(1)); // CALCPRECISION: full precision
        w.Record(0x01B7, U16(0)); // REFRESHALL
        w.Record(0x00DA, U16(0)); // BOOKBOOL

        var styles = context.Styles;
        foreach (var font in styles.FontRecords())
            w.Record(0x0031, font);
        foreach (var (id, code) in styles.FormatRecords())
            w.Record(0x041E, [.. U16(id), .. UnicodeString(code)]);
        foreach (var xf in styles.XfRecords())
            w.Record(0x00E0, xf);
        w.Record(0x0293, [.. U16(0x8000), 0x00, 0xFF]); // STYLE: XF 0 is the built-in Normal style
        w.Record(0x0092, [.. U16(56), .. styles.Palette.Colors.SelectMany(Rgb)]); // PALETTE
        w.Record(0x0160, U16(0)); // USESELFS

        var sheetOffsets = new List<int>();
        foreach (var name in names)
        {
            sheetOffsets.Add(w.Position + 4);
            w.Record(0x0085, [0, 0, 0, 0, 0, 0, .. ShortString(name)]); // BOUNDSHEET: offset (set later), visible worksheet
        }
        w.Record(0x008C, [.. U16(1), .. U16(1)]); // COUNTRY

        // Print areas and titles: built-in names whose 3D references go through the workbook's own SUPBOOK.
        var printNames = new List<(int Sheet, byte Kind, List<CellRange> Areas)>();
        for (var i = 0; i < sheets.Count; i++)
        {
            var sheet = sheets[i];
            var area = sheet.PrintArea.Select(Clip).OfType<CellRange>().ToList();
            if (area.Count > 0)
                printNames.Add((i, 0x06, area));
            var titles = new List<CellRange>();
            if (sheet.TitleRows is { } rows && Clip(new CellRange(rows.First, 0, rows.Last, MaxColumns - 1)) is { } titleRows)
                titles.Add(titleRows);
            if (sheet.TitleColumns is { } columns && Clip(new CellRange(0, columns.First, MaxRows - 1, columns.Last)) is { } titleColumns)
                titles.Add(titleColumns);
            if (titles.Count > 0)
                printNames.Add((i, 0x07, titles));
        }
        if (printNames.Count > 0)
        {
            var referenced = printNames.Select(n => n.Sheet).Distinct().ToList();
            w.Record(0x01AE, [.. U16(sheets.Count), 0x01, 0x04]); // SUPBOOK: this workbook
            w.Long(0x0017, [.. U16(referenced.Count), .. referenced.SelectMany(s => (byte[])[.. U16(0), .. U16(s), .. U16(s)])]); // EXTERNSHEET
            foreach (var (sheet, kind, areas) in printNames)
            {
                var formula = Formula(referenced.IndexOf(sheet), areas);
                // NAME: built-in, one-character name (Print_Area 0x06, Print_Titles 0x07), local to its sheet.
                w.Record(0x0018, [0x20, 0x00, 0x00, 0x01, .. U16(formula.Length), 0, 0, .. U16(sheet + 1), 0, 0, 0, 0, 0x00, kind, .. formula]);
            }
        }

        SharedStrings(w, context);
        w.Record(Eof, []);
        return (w.ToArray(), sheetOffsets);
    }

    /// <summary>A range within BIFF8's sheet, or null for one that starts past it (or isn't a range).</summary>
    private static CellRange? Clip(CellRange range) =>
        range.Top < 0 || range.Left < 0 || range.Top >= MaxRows || range.Left >= MaxColumns || range.Bottom < range.Top || range.Right < range.Left ? null
            : range with { Bottom = Math.Min(range.Bottom, MaxRows - 1), Right = Math.Min(range.Right, MaxColumns - 1) };

    /// <summary>ptgArea3d references (several joined by ptgUnion inside a ptgMemFunc), absolute rows and columns.</summary>
    private static byte[] Formula(int externSheet, List<CellRange> areas)
    {
        static byte[] Area(int ixti, CellRange area) =>
            [0x3B, .. U16(ixti), .. U16(area.Top), .. U16(area.Bottom), .. U16(area.Left), .. U16(area.Right)];
        if (areas.Count == 1)
            return Area(externSheet, areas[0]);
        var expression = new List<byte>(Area(externSheet, areas[0]));
        foreach (var area in areas.Skip(1))
        {
            expression.AddRange(Area(externSheet, area));
            expression.Add(0x10);
        }
        return [0x29, .. U16(expression.Count), .. expression];
    }

    /// <summary>
    /// SST and EXTSST. A string's header (length and flags) never splits; its characters may go on in the next
    /// CONTINUE record, which then starts with the flags byte again. EXTSST points at every dsst-th string.
    /// </summary>
    private static void SharedStrings(RecordWriter w, Context context)
    {
        var strings = context.StringList;
        var perSet = Math.Max(strings.Count / 128 + 1, 8);
        var sets = new List<(int Position, int Offset)>();
        var data = new List<byte>(MaxRecord);
        var id = 0x00FC;
        var start = w.Position;
        void Flush()
        {
            w.Record(id, [.. data]);
            data.Clear();
            id = Continue;
            start = w.Position;
        }

        data.AddRange(U32(context.StringReferences));
        data.AddRange(U32(strings.Count));
        for (var i = 0; i < strings.Count; i++)
        {
            var text = strings[i];
            var wide = text.Any(ch => ch > 0xFF);
            var size = wide ? 2 : 1;
            if (MaxRecord - data.Count < 3 + size)
                Flush();
            if (i % perSet == 0)
                sets.Add((start + 4 + data.Count, 4 + data.Count));
            data.AddRange(U16(text.Length));
            data.Add((byte)(wide ? 1 : 0));
            for (var at = 0; at < text.Length;)
            {
                var room = (MaxRecord - data.Count) / size;
                if (room == 0)
                {
                    Flush();
                    data.Add((byte)(wide ? 1 : 0));
                    room = (MaxRecord - 1) / size;
                }
                var count = Math.Min(room, text.Length - at);
                AddCharacters(data, text.AsSpan(at, count), wide);
                at += count;
            }
        }
        Flush();
        w.Record(0x00FF, [.. U16(perSet), .. sets.SelectMany(s => (byte[])[.. U32(s.Position), .. U16(s.Offset), 0, 0])]);
    }

    // ───────────────────────── Worksheets ─────────────────────────

    private static byte[] SheetStream(Workbook book, Worksheet sheet, Context context, int offset, bool selected, SheetFonts fonts,
        CancellationToken cancellationToken)
    {
        var w = new RecordWriter(offset);
        var setup = sheet.PageSetup;

        // Rows with cells, and rows with a height or hidden: each gets a ROW record.
        var rowCells = new SortedDictionary<int, List<(int Column, SheetCell Cell)>>();
        foreach (var (row, cells) in sheet.Rows)
        {
            if (row < 0)
                continue;
            if (row >= MaxRows)
                break;
            var kept = cells.Where(c => c.Key is >= 0 and < MaxColumns).Select(c => (c.Key, c.Value)).ToList();
            if (kept.Count > 0)
                rowCells[row] = kept;
        }
        var rows = new SortedSet<int>(rowCells.Keys);
        rows.UnionWith(sheet.RowHeights.Keys.Where(r => r is >= 0 and < MaxRows));
        rows.UnionWith(sheet.AutoRowHeights.Keys.Where(r => r is >= 0 and < MaxRows));
        rows.UnionWith(sheet.HiddenRows.Where(r => r is >= 0 and < MaxRows));

        // Heights Excel's autofit gives the rows with cells and no height of their own (as Excel stores them).
        var defaultFont = fonts.Pixels(book.DefaultFontName, book.DefaultFontSize);
        var standard = SheetRowHeights.Standard(sheet, defaultFont);
        double[] fitted = [];
        // (The measure takes the model's rows and columns as they are: a model with negative ones gets none.)
        if (rowCells.Count > 0 && sheet.Rows.Keys.First() >= 0 && sheet.Rows.Values.All(c => c.Count == 0 || c.Keys.First() >= 0))
        {
            var columns = rowCells.Values.Max(c => c[^1].Column) + 1;
            fitted = SheetRowHeights.Measure(book, sheet, rowCells.Keys.Max() + 1, fonts, defaultFont,
                SheetPagination.Widths(sheet, columns, defaultFont), new SheetIndex(sheet), cancellationToken);
        }

        var blocks = rows.GroupBy(r => r / RowsPerBlock).ToList();
        w.Record(Bof, BofData(0x0010));
        var index = w.Position;
        w.Record(0x020B, new byte[16 + 4 * blocks.Count]); // INDEX, filled in once the blocks are written
        w.Record(0x000D, U16(1)); // CALCMODE: automatic
        w.Record(0x000C, U16(100)); // CALCCOUNT
        w.Record(0x000F, U16(1)); // CALCREFMODE: A1
        w.Record(0x0011, U16(0)); // CALCITER
        w.Record(0x0010, BitConverter.GetBytes(0.001)); // CALCDELTA
        w.Record(0x005F, U16(1)); // CALCSAVERECALC
        w.Record(0x002A, U16(setup.PrintHeadings ? 1 : 0)); // PRINTROWCOL
        w.Record(0x002B, U16(setup.PrintGridLines ? 1 : 0)); // PRINTGRID
        w.Record(0x0082, U16(1)); // GRIDSET
        w.Record(0x0080, new byte[8]); // GUTS: no outline
        w.Record(0x0225, [.. U16(sheet.DefaultRowHeight is not null ? 1 : 0), .. U16(Twips(standard))]); // DEFAULTROWHEIGHT
        w.Record(0x0081, U16(0x04C1 | (setup.FitToPage ? 0x0100 : 0))); // WSBOOL
        Breaks(w, 0x001B, sheet.RowBreaks.Where(r => r is > 0 and < MaxRows), MaxColumns - 1); // HORIZONTALPAGEBREAKS
        Breaks(w, 0x001A, sheet.ColumnBreaks.Where(c => c is > 0 and < MaxColumns), MaxRows - 1); // VERTICALPAGEBREAKS
        PageSetup(w, setup);

        var defColWidth = w.Position;
        w.Record(0x0055, U16(Math.Clamp(sheet.BaseColumnWidth ?? 8, 0, 255))); // DEFCOLWIDTH
        if (sheet.HasDefaultColumnWidth || Math.Abs(sheet.DefaultColumnWidth - Worksheet.StandardColumnWidth) > 0.005)
            w.Record(0x0099, U16(Width(sheet.DefaultColumnWidth))); // STANDARDWIDTH
        Columns(w, sheet);

        // DIMENSIONS: the used range of the cells.
        var (firstColumn, lastColumn) = rowCells.Count == 0 ? (0, -1) : (rowCells.Values.Min(c => c[0].Column), rowCells.Values.Max(c => c[^1].Column));
        w.Record(0x0200, [.. U32(rowCells.Count == 0 ? 0 : rowCells.Keys.First()), .. U32(rowCells.Count == 0 ? 0 : rowCells.Keys.Last() + 1),
            .. U16(firstColumn), .. U16(lastColumn + 1), 0, 0]);

        var dbCells = new List<int>();
        foreach (var block in blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var firstRow = w.Position;
            foreach (var row in block)
            {
                var cells = rowCells.GetValueOrDefault(row);
                var custom = sheet.RowHeights.TryGetValue(row, out var height) && height > 0;
                var hidden = sheet.HiddenRows.Contains(row) || sheet.RowHeights.ContainsKey(row) && !custom;
                var points = custom ? height
                    : sheet.AutoRowHeights.TryGetValue(row, out var auto) && auto > 0 ? auto
                    : cells is not null && row < fitted.Length ? fitted[row]
                    : standard;
                var flags = 0x0100 | (hidden ? 0x20 : 0) | (custom ? 0x40 : 0);
                w.Record(0x0208, [.. U16(row), .. U16(cells?[0].Column ?? 0), .. U16(cells is null ? 0 : cells[^1].Column + 1),
                    .. U16(Twips(points)), 0, 0, 0, 0, .. U16(flags), .. U16(DefaultCellXf)]); // ROW
            }
            // DBCELL: back to the first ROW, then from each row's first cell to the next one's.
            var offsets = new List<int>();
            var previous = firstRow + 20;
            foreach (var row in block)
            {
                offsets.Add(w.Position - previous);
                previous = w.Position;
                foreach (var (column, cell) in rowCells.GetValueOrDefault(row) ?? [])
                    Cell(w, context, row, column, cell);
            }
            dbCells.Add(w.Position);
            w.Record(0x00D7, [.. U32(w.Position - firstRow), .. offsets.SelectMany(o => U16(o))]);
        }

        // WINDOW2: gridlines, headings and zeros shown, default gridline colour, outline symbols; the first sheet selected.
        var frozen = sheet.FrozenRows is > 0 and < MaxRows ? sheet.FrozenRows : 0;
        var window = 0x02 | 0x04 | 0x10 | 0x20 | 0x80 | (selected ? 0x0600 : 0) | (frozen > 0 ? 0x0108 : 0) | (sheet.RightToLeft ? 0x40 : 0);
        w.Record(0x023E, [.. U16(window), .. U16(0), .. U16(0), .. U16(AutomaticForeground), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        if (frozen > 0)
            w.Record(0x0041, [.. U16(0), .. U16(frozen), .. U16(frozen), .. U16(0), 0x02, 0x00]); // PANE: rows frozen, lower pane active

        var merges = sheet.Merges.Select(Clip).OfType<CellRange>().Where(m => m.Bottom > m.Top || m.Right > m.Left).ToList();
        foreach (var chunk in merges.Chunk(1026))
            w.Record(0x00E5, [.. U16(chunk.Length), .. chunk.SelectMany(m => (byte[])[.. U16(m.Top), .. U16(m.Bottom), .. U16(m.Left), .. U16(m.Right)])]); // MERGECELLS
        w.Record(Eof, []);

        // INDEX: the rows with ROW records, where DEFCOLWIDTH is and where each block's DBCELL is.
        var bytes = w.ToArray();
        var at = index - offset + 4;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at + 4), rows.Count == 0 ? 0 : rows.Min);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at + 8), rows.Count == 0 ? 0 : rows.Max + 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at + 12), defColWidth);
        for (var i = 0; i < dbCells.Count; i++)
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at + 16 + i * 4), dbCells[i]);
        return bytes;
    }

    private static void Cell(RecordWriter w, Context context, int row, int column, SheetCell cell)
    {
        var value = Classify(cell);
        var xf = context.Styles.Index(cell.Style, value.Kind == Kind.Number ? cell.NumberFormat : null);
        byte[] where = [.. U16(row), .. U16(column), .. U16(xf)];
        switch (value.Kind)
        {
            case Kind.Number:
                w.Record(0x0203, [.. where, .. BitConverter.GetBytes(value.Number)]); // NUMBER
                break;
            case Kind.Text:
                w.Record(0x00FD, [.. where, .. U32(context.Strings[value.Text])]); // LABELSST
                break;
            case Kind.Boolean:
                w.Record(0x0205, [.. where, (byte)value.Number, 0]); // BOOLERR: a boolean
                break;
            case Kind.Error:
                w.Record(0x0205, [.. where, value.Code, 1]); // BOOLERR: an error
                break;
            default:
                w.Record(0x0201, where); // BLANK: formatting only
                break;
        }
    }

    /// <summary>COLINFO: runs of neighbouring columns with the same width and visibility.</summary>
    private static void Columns(RecordWriter w, Worksheet sheet)
    {
        var columns = sheet.ColumnWidths.Keys.Union(sheet.HiddenColumns).Where(c => c is >= 0 and < MaxColumns).Order().ToList();
        var records = 0;
        for (var i = 0; i < columns.Count && records < 255; records++)
        {
            var first = columns[i];
            var width = Width(sheet.ColumnWidth(first));
            var hidden = sheet.HiddenColumns.Contains(first);
            var last = first;
            while (++i < columns.Count && columns[i] == last + 1 && Width(sheet.ColumnWidth(columns[i])) == width && sheet.HiddenColumns.Contains(columns[i]) == hidden)
                last = columns[i];
            var flags = (hidden ? 0x01 : 0) | (sheet.ColumnWidths.ContainsKey(first) ? 0x02 : 0);
            w.Record(0x007D, [.. U16(first), .. U16(last), .. U16(width), .. U16(DefaultCellXf), .. U16(flags), 0, 0]);
        }
    }

    /// <summary>A column width in characters as COLINFO stores it, 1/256 of a character.</summary>
    private static int Width(double characters) => (int)Math.Round(Math.Clamp(double.IsFinite(characters) ? characters : 0, 0, 255) * 256);

    private static int Twips(double points) => (int)Math.Round(Math.Clamp(double.IsFinite(points) ? points : 15, 0.1, 409.5) * 20);

    /// <summary>HORIZONTALPAGEBREAKS / VERTICALPAGEBREAKS: each break across the whole sheet.</summary>
    private static void Breaks(RecordWriter w, int id, IEnumerable<int> breaks, int end)
    {
        var list = breaks.Order().ToList();
        if (list.Count == 0)
            return;
        foreach (var chunk in list.Chunk(1026))
            w.Record(id, [.. U16(chunk.Length), .. chunk.SelectMany(b => (byte[])[.. U16(b), .. U16(0), .. U16(end)])]);
    }

    private static void PageSetup(RecordWriter w, SheetPageSetup setup)
    {
        w.Record(0x0014, HeaderFooter(setup.HeaderFooter.OddHeader)); // HEADER
        w.Record(0x0015, HeaderFooter(setup.HeaderFooter.OddFooter)); // FOOTER
        w.Record(0x0083, U16(setup.CenterHorizontally ? 1 : 0)); // HCENTER
        w.Record(0x0084, U16(setup.CenterVertically ? 1 : 0)); // VCENTER
        w.Record(0x0026, Inches(setup.LeftMargin)); // LEFTMARGIN
        w.Record(0x0027, Inches(setup.RightMargin)); // RIGHTMARGIN
        w.Record(0x0028, Inches(setup.TopMargin)); // TOPMARGIN
        w.Record(0x0029, Inches(setup.BottomMargin)); // BOTTOMMARGIN
        // SETUP: paper, scale, first page number, fit to pages, page order, orientation; 600 dpi, one copy.
        var flags = (setup.OverThenDown ? 0x01 : 0) | (setup.Landscape ? 0 : 0x02) | (setup.FirstPageNumber is not null ? 0x80 : 0);
        w.Record(0x00A1, [.. U16(PaperCode(setup)), .. U16(Math.Clamp(setup.Scale, 10, 400)), .. U16((ushort)(short)Math.Clamp(setup.FirstPageNumber ?? 1, short.MinValue, short.MaxValue)),
            .. U16(Math.Clamp(setup.FitWidth, 0, 32_767)), .. U16(Math.Clamp(setup.FitHeight, 0, 32_767)), .. U16(flags), .. U16(600), .. U16(600),
            .. Inches(setup.HeaderMargin), .. Inches(setup.FooterMargin), .. U16(1)]);
    }

    /// <summary>A header or footer (255 characters at most); an empty record for none.</summary>
    private static byte[] HeaderFooter(string? text) =>
        string.IsNullOrEmpty(text) ? [] : UnicodeString(text.Length > 255 ? text[..255] : text);

    private static byte[] Inches(double points) => BitConverter.GetBytes(double.IsFinite(points) ? Math.Clamp(points, 0, 49 * 72) / 72 : 0.75);

    /// <summary>The SETUP paper code of the page setup's paper; 0 (the printer's) for a size without one.</summary>
    private static int PaperCode(SheetPageSetup setup)
    {
        var (width, height) = (Math.Min(setup.PaperWidth, setup.PaperHeight), Math.Max(setup.PaperWidth, setup.PaperHeight));
        for (var code = 1; code <= 70; code++)
        {
            if (Paper.FromCode(code) is { } paper && Math.Abs(paper.Width - width) < 1 && Math.Abs(paper.Height - height) < 1)
                return code;
        }
        return 0;
    }

    // ───────────────────────── Formats ─────────────────────────

    /// <summary>Fonts, number formats and cell formats (XF records), shared by equal styles; the colours they use.</summary>
    private sealed class Styles
    {
        private readonly Workbook _book;
        private readonly Dictionary<(CellStyle, string?), int> _xfs = [];
        private readonly List<(CellStyle Style, int Font, int Format)> _cells = [];
        private readonly Dictionary<FontKey, int> _fontIndexes = [];
        private readonly List<FontKey> _fonts = [];
        private readonly Dictionary<string, int> _formats = new(StringComparer.Ordinal);

        public Styles(Workbook book)
        {
            _book = book;
            _fonts.Add(new FontKey(FontName(null), FontHeight(book.DefaultFontSize), false, false, 0, false, 0, null));
            _fontIndexes[_fonts[0]] = 0;
        }

        public Palette Palette { get; } = new();

        public bool Clipped { get; private set; }

        /// <summary>The XF index of a cell format with a number format (null = General).</summary>
        public int Index(CellStyle style, string? format)
        {
            if (_xfs.TryGetValue((style, format), out var index))
                return index;
            if (style == CellStyle.Default && format is null)
                return DefaultCellXf;
            if (_cells.Count >= MaxCellFormats)
            {
                Clipped = true;
                return DefaultCellXf;
            }
            foreach (var color in (string?[])[style.Fill, style.PatternBackground, style.Left.Color, style.Right.Color, style.Top.Color,
                         style.Bottom.Color, style.DiagonalDown.Color, style.DiagonalUp.Color])
                Palette.Use(color);
            _cells.Add((style, Font(style), FormatId(format)));
            return _xfs[(style, format)] = FirstCellXf + _cells.Count - 1;
        }

        /// <summary>The FONT index of a style's font: 0 is the Normal style's; BIFF has no font 4.</summary>
        private int Font(CellStyle style)
        {
            var key = new FontKey(FontName(style.FontName), FontHeight(style.FontSize ?? _book.DefaultFontSize), style.Bold, style.Italic,
                style.DoubleUnderline ? 2 : style.Underline ? 1 : 0, style.Strike, style.Script, Palette.Normalize(style.Color));
            if (_fontIndexes.TryGetValue(key, out var index))
                return index;
            if (_fonts.Count >= MaxFonts)
            {
                Clipped = true;
                return 0;
            }
            Palette.Use(key.Color);
            _fonts.Add(key);
            return _fontIndexes[key] = _fonts.Count + 3;
        }

        private string FontName(string? name)
        {
            var clean = new string([.. (name ?? _book.DefaultFontName).Where(ch => ch >= ' ')]).Trim();
            if (clean.Length == 0)
                clean = "Arial";
            return clean.Length > 31 ? clean[..31] : clean;
        }

        private static int FontHeight(double points) => (int)Math.Round(Math.Clamp(double.IsFinite(points) ? points : 10, 1, 409) * 20);

        private int FormatId(string? format)
        {
            if (format is null || NumberFormatter.IsGeneral(format))
                return 0;
            if (BuiltInFormats.TryGetValue(format, out var builtIn))
                return builtIn;
            // The PC's short date is written as format 14, which Excel shows in the short date of whoever opens it.
            if (NumberFormatter.IsShortDate(format))
                return ShortDateFormat;
            if (_formats.TryGetValue(format, out var id))
                return id;
            if (_formats.Count >= MaxFormats || format.Length > 255)
            {
                Clipped = true;
                return 0;
            }
            return _formats[format] = FirstCustomFormat + _formats.Count;
        }

        /// <summary>FONT records: the Normal style's font four times (BIFF has no font 4), then the others.</summary>
        public IEnumerable<byte[]> FontRecords()
        {
            for (var i = 0; i < 4; i++)
                yield return FontRecord(_fonts[0]);
            foreach (var font in _fonts.Skip(1))
                yield return FontRecord(font);
        }

        private byte[] FontRecord(FontKey font)
        {
            var flags = (font.Italic ? 0x02 : 0) | (font.Strike ? 0x08 : 0);
            var color = font.Color is null ? AutomaticFontColor : Palette.Index(font.Color);
            byte[] name = [(byte)font.Name.Length, 0x01, .. Encoding.Unicode.GetBytes(font.Name)];
            return [.. U16(font.Height), .. U16(flags), .. U16(color), .. U16(font.Bold ? 700 : 400), .. U16(font.Script switch { 1 => 1, -1 => 2, _ => 0 }),
                (byte)font.Underline, 0x00, 0x00, 0x00, .. name];
        }

        /// <summary>FORMAT records: Excel's eight currency formats, which every workbook lists, then the workbook's own.</summary>
        public IEnumerable<(int Id, string Code)> FormatRecords() =>
            CurrencyFormats.Select(c => (c.Value, c.Key)).Concat(_formats.Select(f => (f.Value, f.Key)));

        /// <summary>XF records: the Normal style and 14 more cell styles, the default cell format (15), then the cells' own.</summary>
        public IEnumerable<byte[]> XfRecords()
        {
            for (var i = 0; i < 15; i++)
            {
                // Style XFs: locked, no parent (0xFFF), bottom aligned, automatic pattern colours.
                yield return [.. U16(i is >= 1 and <= 4 ? 1 : 0), .. U16(0), .. U16(0xFFF5), 0x20, 0, 0, (byte)(i == 0 ? 0 : 0xF4), 0, 0, 0, 0, 0, 0, 0, 0, 0xC0, 0x20];
            }
            yield return [.. U16(0), .. U16(0), .. U16(0x0001), 0x20, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xC0, 0x20];
            foreach (var (style, font, format) in _cells)
                yield return CellXf(style, font, format);
        }

        private byte[] CellXf(CellStyle s, int font, int format)
        {
            var xf = new byte[20];
            BinaryPrimitives.WriteUInt16LittleEndian(xf, (ushort)font);
            BinaryPrimitives.WriteUInt16LittleEndian(xf.AsSpan(2), (ushort)format);
            BinaryPrimitives.WriteUInt16LittleEndian(xf.AsSpan(4), 0x0001); // locked, the Normal style its parent
            var horizontal = s.SpecialAlign switch
            {
                CellSpecialAlign.Fill => 4,
                CellSpecialAlign.Justify => 5,
                CellSpecialAlign.CenterAcross => 6,
                CellSpecialAlign.Distributed => 7,
                _ => s.Align switch { CellAlign.Left => 1, CellAlign.Center => 2, CellAlign.Right => 3, _ => 0 },
            };
            var vertical = s.VerticalJustify ? 3 : s.VerticalAlign switch { CellVerticalAlign.Top => 0, CellVerticalAlign.Center => 1, _ => 2 };
            xf[6] = (byte)(horizontal | (s.Wrap ? 0x08 : 0) | vertical << 4);
            xf[7] = s.Rotation switch
            {
                CellStyle.Stacked => 255,
                > 0 and <= 90 => (byte)s.Rotation,
                < 0 and >= -90 => (byte)(90 - s.Rotation),
                _ => 0,
            };
            xf[8] = (byte)(Math.Clamp(s.Indent, 0, 15) | (s.Shrink ? 0x10 : 0));
            xf[9] = 0xFC; // number format, font, alignment, borders, fill and protection of its own

            var diagonal = s.DiagonalDown.IsVisible ? s.DiagonalDown : s.DiagonalUp;
            var diagonals = (s.DiagonalDown.IsVisible ? 1u : 0) | (s.DiagonalUp.IsVisible ? 2u : 0);
            var borders = Border(s.Left) | Border(s.Right) << 4 | Border(s.Top) << 8 | Border(s.Bottom) << 12
                          | BorderColor(s.Left) << 16 | BorderColor(s.Right) << 23 | diagonals << 30;
            var (pattern, foreground, background) = Fill(s);
            var more = BorderColor(s.Top) | BorderColor(s.Bottom) << 7 | BorderColor(diagonal) << 14 | Border(diagonal) << 21 | (uint)pattern << 26;
            BinaryPrimitives.WriteUInt32LittleEndian(xf.AsSpan(10), borders);
            BinaryPrimitives.WriteUInt32LittleEndian(xf.AsSpan(14), more);
            BinaryPrimitives.WriteUInt16LittleEndian(xf.AsSpan(18), (ushort)(foreground | background << 7));
            return xf;
        }

        private static uint Border(CellBorder border) =>
            !border.IsVisible ? 0u : BorderStyles.TryGetValue(border.Style, out var style) ? (uint)style : 1u;

        private uint BorderColor(CellBorder border) =>
            !border.IsVisible ? 0u : border.Color is null ? AutomaticForeground : (uint)Palette.Index(border.Color);

        /// <summary>The fill pattern (fls) and its foreground and background colours.</summary>
        private (int Pattern, int Foreground, int Background) Fill(CellStyle s)
        {
            if (s.FillPattern is { } name && Patterns.TryGetValue(name, out var pattern))
            {
                return (pattern, Palette.Index(s.Fill ?? "#000000"),
                    s.PatternBackground is null ? AutomaticBackground : Palette.Index(s.PatternBackground));
            }
            if (s.Fill is { } fill && Palette.Normalize(fill) is not null)
                return (1, Palette.Index(fill), AutomaticBackground);
            return (0, AutomaticForeground, AutomaticBackground);
        }
    }

    private readonly record struct FontKey(string Name, int Height, bool Bold, bool Italic, int Underline, bool Strike, int Script, string? Color);

    /// <summary>Formats Excel knows by number in every language (ECMA-376 18.8.30).</summary>
    private static readonly Dictionary<string, int> BuiltInFormats = new(StringComparer.Ordinal)
    {
        ["0"] = 1,
        ["0.00"] = 2,
        ["#,##0"] = 3,
        ["#,##0.00"] = 4,
        ["0%"] = 9,
        ["0.00%"] = 10,
        ["0.00E+00"] = 11,
        ["@"] = 49,
    };

    /// <summary>The currency and accounting formats Excel writes into every workbook (ids 5–8 and 41–44).</summary>
    private static readonly Dictionary<string, int> CurrencyFormats = new(StringComparer.Ordinal)
    {
        ["\"$\"#,##0_);\\(\"$\"#,##0\\)"] = 5,
        ["\"$\"#,##0_);[Red]\\(\"$\"#,##0\\)"] = 6,
        ["\"$\"#,##0.00_);\\(\"$\"#,##0.00\\)"] = 7,
        ["\"$\"#,##0.00_);[Red]\\(\"$\"#,##0.00\\)"] = 8,
        ["_(\"$\"* #,##0_);_(\"$\"* \\(#,##0\\);_(\"$\"* \"-\"_);_(@_)"] = 42,
        ["_(* #,##0_);_(* \\(#,##0\\);_(* \"-\"_);_(@_)"] = 41,
        ["_(\"$\"* #,##0.00_);_(\"$\"* \\(#,##0.00\\);_(\"$\"* \"-\"??_);_(@_)"] = 44,
        ["_(* #,##0.00_);_(* \\(#,##0.00\\);_(* \"-\"??_);_(@_)"] = 43,
    };

    /// <summary>Border styles by their XF number (XlsLayout reads them back by the same table).</summary>
    private static readonly Dictionary<string, int> BorderStyles = new(StringComparer.Ordinal)
    {
        ["thin"] = 1,
        ["medium"] = 2,
        ["dashed"] = 3,
        ["dotted"] = 4,
        ["thick"] = 5,
        ["double"] = 6,
        ["hair"] = 7,
        ["mediumDashed"] = 8,
        ["dashDot"] = 9,
        ["mediumDashDot"] = 10,
        ["dashDotDot"] = 11,
        ["mediumDashDotDot"] = 12,
        ["slantDashDot"] = 13,
    };

    /// <summary>Fill patterns other than solid by their fls number.</summary>
    private static readonly Dictionary<string, int> Patterns = new(StringComparer.Ordinal)
    {
        ["mediumGray"] = 2,
        ["darkGray"] = 3,
        ["lightGray"] = 4,
        ["darkHorizontal"] = 5,
        ["darkVertical"] = 6,
        ["darkDown"] = 7,
        ["darkUp"] = 8,
        ["darkGrid"] = 9,
        ["darkTrellis"] = 10,
        ["lightHorizontal"] = 11,
        ["lightVertical"] = 12,
        ["lightDown"] = 13,
        ["lightUp"] = 14,
        ["lightGrid"] = 15,
        ["lightTrellis"] = 16,
        ["gray125"] = 17,
        ["gray0625"] = 18,
    };

    /// <summary>
    /// The 56 colours of the palette (indexes 8–63). The default palette's colours are kept where the workbook uses
    /// them; other colours take the places of unused ones, from the end; past 56, a colour gets the nearest one.
    /// </summary>
    private sealed class Palette
    {
        private readonly List<string> _used = [];
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
        private readonly string[] _colors = (string[])XlsPalette.Default.Clone();

        public IReadOnlyList<string> Colors => _colors;

        /// <summary>"#1f4e79", "1F4E79" or "#FF1F4E79" → "#1F4E79"; null for anything else.</summary>
        public static string? Normalize(string? color)
        {
            if (string.IsNullOrWhiteSpace(color))
                return null;
            var hex = color.Trim().TrimStart('#');
            if (hex.Length == 8)
                hex = hex[2..];
            return hex.Length == 6 && hex.All(char.IsAsciiHexDigit) ? "#" + hex.ToUpperInvariant() : null;
        }

        public void Use(string? color)
        {
            if (Normalize(color) is { } normal && _seen.Add(normal))
                _used.Add(normal);
        }

        public void Assign()
        {
            var taken = new bool[_colors.Length];
            var rest = new List<string>();
            foreach (var color in _used)
            {
                var at = Array.IndexOf(_colors, color);
                if (at >= 0)
                {
                    taken[at] = true;
                    _index[color] = 8 + at;
                }
                else
                {
                    rest.Add(color);
                }
            }
            var free = _colors.Length - 1;
            foreach (var color in rest)
            {
                while (free >= 0 && taken[free])
                    free--;
                if (free < 0)
                    break;
                _colors[free] = color;
                taken[free] = true;
                _index[color] = 8 + free;
            }
        }

        /// <summary>The palette index (8–63) of a colour: its own, else the nearest.</summary>
        public int Index(string color)
        {
            if (Normalize(color) is not { } normal)
                return AutomaticForeground;
            if (_index.TryGetValue(normal, out var index))
                return index;
            var (r, g, b) = Rgb(normal);
            var best = 0;
            var distance = long.MaxValue;
            for (var i = 0; i < _colors.Length; i++)
            {
                var (pr, pg, pb) = Rgb(_colors[i]);
                var d = (long)(r - pr) * (r - pr) + (long)(g - pg) * (g - pg) + (long)(b - pb) * (b - pb);
                if (d < distance)
                    (best, distance) = (i, d);
            }
            return 8 + best;
        }

        public static (int R, int G, int B) Rgb(string color) =>
            (int.Parse(color.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
             int.Parse(color.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
             int.Parse(color.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    private static byte[] Rgb(string color)
    {
        var (r, g, b) = Palette.Rgb(color);
        return [(byte)r, (byte)g, (byte)b, 0];
    }

    // ───────────────────────── Records ─────────────────────────

    /// <summary>Records one after another, with the stream position of the next one (from the substream's place in the stream).</summary>
    private sealed class RecordWriter(int offset = 0)
    {
        private readonly MemoryStream _stream = new();

        public int Position => offset + (int)_stream.Position;

        public void Record(int id, ReadOnlySpan<byte> data)
        {
            if (data.Length > MaxRecord)
                throw new ArgumentException($"Record 0x{id:X4} holds {data.Length} bytes; BIFF8 records hold {MaxRecord}.", nameof(data));
            Span<byte> header = stackalloc byte[4];
            BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)id);
            BinaryPrimitives.WriteUInt16LittleEndian(header[2..], (ushort)data.Length);
            _stream.Write(header);
            _stream.Write(data);
        }

        /// <summary>A record whose data may be longer than one record holds: the rest goes on in CONTINUE records.</summary>
        public void Long(int id, byte[] data)
        {
            Record(id, data.AsSpan(0, Math.Min(data.Length, MaxRecord)));
            for (var at = MaxRecord; at < data.Length; at += MaxRecord)
                Record(Continue, data.AsSpan(at, Math.Min(MaxRecord, data.Length - at)));
        }

        public byte[] ToArray() => _stream.ToArray();
    }

    /// <summary>BOF: BIFF8 of a substream type (0x0005 globals, 0x0010 worksheet), as Excel 97 writes it, last saved on Windows.</summary>
    private static byte[] BofData(int type) =>
        [.. U16(0x0600), .. U16(type), .. U16(0x0DBB), .. U16(0x07CC), .. U32(0x00000009), .. U32(0x00000006)];

    /// <summary>An XLUnicodeString: 16-bit length, flags, characters (one byte each when they all fit, else UTF-16).</summary>
    private static byte[] UnicodeString(string text)
    {
        var wide = text.Any(ch => ch > 0xFF);
        var bytes = new List<byte>(3 + text.Length * (wide ? 2 : 1));
        bytes.AddRange(U16(text.Length));
        bytes.Add((byte)(wide ? 1 : 0));
        AddCharacters(bytes, text, wide);
        return [.. bytes];
    }

    /// <summary>A ShortXLUnicodeString (8-bit length) of at most 255 characters.</summary>
    private static byte[] ShortString(string text)
    {
        text = text.Length > 255 ? text[..255] : text;
        var wide = text.Any(ch => ch > 0xFF);
        var bytes = new List<byte>(2 + text.Length * (wide ? 2 : 1)) { (byte)text.Length, (byte)(wide ? 1 : 0) };
        AddCharacters(bytes, text, wide);
        return [.. bytes];
    }

    private static void AddCharacters(List<byte> bytes, ReadOnlySpan<char> text, bool wide)
    {
        foreach (var ch in text)
        {
            bytes.Add((byte)ch);
            if (wide)
                bytes.Add((byte)(ch >> 8));
        }
    }

    private static byte[] U16(int value) => [(byte)value, (byte)(value >> 8)];

    private static byte[] U32(int value) => [(byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24)];
}
