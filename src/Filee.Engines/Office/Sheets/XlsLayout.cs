// What ExcelDataReader doesn't expose of an XLS file, read from the BIFF records themselves: column widths (COLINFO)
// and row heights with their "custom" flag (ROW), the cell formats (FONT, XF and PALETTE in the workbook globals and
// each cell's XF index, blank cells included) and printing (SETUP, the margin records, HCENTER / VCENTER,
// PRINTGRIDLINES / PRINTHEADERS, HEADER / FOOTER, page breaks, WSBOOL's fit to page, the Print_Area and Print_Titles
// names, DEFCOLWIDTH / STANDARDWIDTH / DEFAULTROWHEIGHT, WINDOW2's right-to-left flag and frozen panes with PANE).
// Fonts, formats, headers and names are read from BIFF8 (Excel 97–2003); older files get widths, heights and the page
// setup. Encrypted workbooks and pre-Excel 5 files return null: the reader then falls back to ExcelDataReader's values.
// Charts (BIFF8): an embedded chart's substream follows its OBJ record inside the worksheet's, placed by the
// OfficeArt client anchor of the MSODRAWING before it; a chart sheet is a substream of its own. BiffChartReader reads
// both; the workbook's charts count against one ChartBudget.

using System.Buffers.Binary;
using System.Text;
using Filee.Engines.Charts;

namespace Filee.Engines.Office.Sheets;

internal static class XlsLayout
{
    /// <summary>What the BIFF records say about the workbook.</summary>
    public sealed class Book
    {
        /// <summary>Cell formats by XF index.</summary>
        public List<CellStyle> Styles { get; } = [];

        public string? DefaultFontName { get; set; }
        public double? DefaultFontSize { get; set; }

        /// <summary>Worksheets in workbook order (chart sheets left out, like ExcelDataReader).</summary>
        public List<Sheet> Sheets { get; } = [];

        /// <summary>Chart sheets in workbook order.</summary>
        public List<ChartSheet> ChartSheets { get; } = [];
    }

    /// <summary>
    /// A chart sheet: how many worksheets come before it, whether it shows, its name, page setup and chart (null when
    /// BiffChartReader doesn't draw it).
    /// </summary>
    public sealed record ChartSheet(int WorksheetsBefore, bool Visible, string Name, Sheet Layout, Chart? Chart);

    /// <summary>Columns, rows, cell formats and the page setup of one worksheet.</summary>
    public sealed class Sheet
    {
        /// <summary>Width in characters (as XLSX stores it) and hidden flag, by 0-based column.</summary>
        public Dictionary<int, (double Width, bool Hidden)> Columns { get; } = [];

        /// <summary>Height in points, whether it is a custom height, and hidden flag, by 0-based row.</summary>
        public Dictionary<int, (double Points, bool Custom, bool Hidden)> Rows { get; } = [];

        /// <summary>The XF index of every cell record, blank cells included.</summary>
        public Dictionary<(int Row, int Column), int> Cells { get; } = [];

        public SheetPageSetup PageSetup { get; set; } = new();
        public List<CellRange> PrintArea { get; } = [];
        public (int First, int Last)? TitleRows { get; set; }
        public (int First, int Last)? TitleColumns { get; set; }
        public SortedSet<int> RowBreaks { get; } = [];
        public SortedSet<int> ColumnBreaks { get; } = [];
        public double? DefaultRowHeight { get; set; }
        public double? DefaultColumnWidth { get; set; }
        public int? BaseColumnWidth { get; set; }
        public bool RightToLeft { get; set; }

        /// <summary>Rows frozen at the top (WINDOW2 frozen panes, PANE's split row); 0 for none.</summary>
        public int FrozenRows { get; set; }

        /// <summary>The sheet shows (BOUNDSHEET hsState visible).</summary>
        public bool Visible { get; set; } = true;

        /// <summary>The embedded charts BiffChartReader draws, placed by their anchors.</summary>
        public List<SheetChart> Charts { get; } = [];

        /// <summary>The first object the sheet printer doesn't draw (pictures, text boxes, ..., charts not read), or null.</summary>
        public string? FirstObject { get; set; }
    }

    private const int MaxColumn = 16_383;

    /// <summary>The layout of the workbook; null for files it can't read (ExcelDataReader's values are used then).</summary>
    public static Book? Read(string path)
    {
        try
        {
            return WorkbookStream(File.ReadAllBytes(path)) is { } workbook ? Parse(workbook) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidDataException or IOException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// The first object of a visible sheet the sheet printer doesn't draw (pictures, text boxes, shapes, form controls,
    /// charts BiffChartReader doesn't read; comments aside, which print only when the page setup says so), or a visible
    /// chart sheet whose chart isn't read; null when there is none or the file can't be read.
    /// </summary>
    /// <exception cref="Core.Conversion.ConversionRefusedException">A chart has more series or points than Filee draws.</exception>
    public static string? FirstObject(string path)
    {
        if (Read(path) is not { } book)
            return null;
        if (book.ChartSheets.Any(c => c.Visible && c.Chart is null))
            return "charts";
        return book.Sheets.Where(s => s.Visible).Select(s => s.FirstObject).FirstOrDefault(o => o is not null);
    }

    // ───────────────────────── BIFF records ─────────────────────────

    private sealed record FontRecord(double Size, bool Bold, bool Italic, bool Strike, int Underline, int Script, int Color, string Name);

    private static Book? Parse(byte[] stream)
    {
        var book = new Book();
        var offsets = new List<int>();
        var sheetIndexes = new Dictionary<int, int>(); // BOUNDSHEET position (all sheets) → worksheet index
        var fonts = new List<FontRecord>();
        var xfs = new List<byte[]>();
        var palette = (string[])XlsPalette.Default.Clone();
        var names = new List<(int Sheet, int Kind, byte[] Formula)>();
        var formats = new Dictionary<int, string>();
        var chartSheets = new List<(int Offset, int WorksheetsBefore, bool Visible, string Name)>();
        var visible = new List<bool>();
        var date1904 = false;
        var biff8 = false;
        var position = 0;
        foreach (var (id, data) in Records(stream, 0))
        {
            switch (id)
            {
                case 0x0809 when data.Length >= 2:
                    biff8 = BinaryPrimitives.ReadUInt16LittleEndian(data) == 0x0600;
                    break;
                case 0x002F: // FILEPASS: everything after it is encrypted
                    return null;
                case 0x0085 when data.Length >= 6: // BOUNDSHEET
                    if (data[5] == 0)
                    {
                        sheetIndexes[position] = offsets.Count;
                        offsets.Add(BinaryPrimitives.ReadInt32LittleEndian(data));
                        visible.Add((data[4] & 0x03) == 0);
                    }
                    else if (data[5] == 0x02)
                    {
                        chartSheets.Add((BinaryPrimitives.ReadInt32LittleEndian(data), offsets.Count, (data[4] & 0x03) == 0, biff8 ? ShortString(data, 6) : ""));
                    }
                    position++;
                    break;
                case 0x041E when biff8 && data.Length >= 5: // FORMAT
                    formats[BinaryPrimitives.ReadUInt16LittleEndian(data)] = LongString(data, 2);
                    break;
                case 0x0022 when data.Length >= 2: // DATE1904
                    date1904 = data[0] != 0;
                    break;
                case 0x0031 when biff8 && data.Length >= 16: // FONT
                    fonts.Add(new FontRecord(BinaryPrimitives.ReadUInt16LittleEndian(data) / 20.0,
                        BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6)) >= 600,
                        (data[2] & 0x02) != 0, (data[2] & 0x08) != 0, data[10], BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8)),
                        BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4)), ShortString(data, 14)));
                    break;
                case 0x00E0 when biff8 && data.Length >= 20: // XF
                    xfs.Add(data);
                    break;
                case 0x0092 when data.Length >= 2: // PALETTE
                    var count = Math.Min(BinaryPrimitives.ReadUInt16LittleEndian(data), (data.Length - 2) / 4);
                    for (var i = 0; i < count && i < palette.Length; i++)
                        palette[i] = $"#{data[2 + i * 4]:X2}{data[3 + i * 4]:X2}{data[4 + i * 4]:X2}";
                    break;
                case 0x0018 when biff8 && data.Length >= 15: // NAME
                    if ((data[0] & 0x20) != 0 && data[3] == 1)
                    {
                        var formulaLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4));
                        var sheet = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8));
                        var nameLength = (data[14] & 0x01) != 0 ? 2 : 1;
                        var kind = 15 + nameLength <= data.Length ? data[15] : -1;
                        var start = 15 + nameLength;
                        if (start + formulaLength <= data.Length && sheet > 0)
                            names.Add((sheet - 1, kind, data.AsSpan(start, formulaLength).ToArray()));
                    }
                    break;
            }
        }

        string? Color(int index) => index switch
        {
            < 8 => XlsPalette.Default[index],
            < 64 => palette[index - 8],
            _ => null, // system colours and "automatic"
        };
        // Font 4 doesn't exist in BIFF: indexes above it are one lower in the list.
        FontRecord? Font(int index) => (index >= 4 ? index - 1 : index) is var i && i >= 0 && i < fonts.Count ? fonts[i] : null;
        if (Font(0) is { } normal)
        {
            book.DefaultFontName = normal.Name.Length > 0 ? normal.Name : null;
            book.DefaultFontSize = normal.Size is > 0 and <= 409 ? normal.Size : null;
        }
        foreach (var xf in xfs)
            book.Styles.Add(Style(xf, Font(BinaryPrimitives.ReadUInt16LittleEndian(xf)), Color));

        // Charts are read from BIFF8 only (BIFF5's chart records differ).
        string? Format(int id) => formats.TryGetValue(id, out var code) ? code : NumberFormatter.BuiltIn(id);
        var charts = !biff8 ? null : new ChartRecords(new BiffChartBook
        {
            Font = index => Font(index) is { } font ? new BiffFont(font.Name, font.Size, font.Bold, font.Italic, font.Color) : null,
            FontCount = fonts.Count,
            Color = icv => icv switch { 0x40 or 0x4D or 0x4F => "#000000", 0x41 or 0x4E => "#FFFFFF", _ => Color(icv) },
            Format = id => Format(id) is { } code && !NumberFormatter.IsGeneral(code) ? code : null,
            CellFormat = xf => xf >= 0 && xf < xfs.Count && Format(BinaryPrimitives.ReadUInt16LittleEndian(xfs[xf].AsSpan(2))) is { } code
                && !NumberFormatter.IsGeneral(code) ? code : null,
            Date1904 = date1904,
        });

        for (var i = 0; i < offsets.Count; i++)
        {
            var sheet = ReadSheet(stream, offsets[i], biff8, charts);
            sheet.Visible = visible[i];
            book.Sheets.Add(sheet);
        }
        foreach (var (offset, before, shows, name) in chartSheets)
        {
            var layout = ReadSheet(stream, offset, biff8, null);
            book.ChartSheets.Add(new ChartSheet(before, shows, name, layout, charts?.Read(Substream(stream, offset))));
        }
        foreach (var (sheetPosition, kind, formula) in names)
        {
            if (!sheetIndexes.TryGetValue(sheetPosition, out var index) || index >= book.Sheets.Count)
                continue;
            var areas = Areas(formula);
            if (kind == 0x06)
                book.Sheets[index].PrintArea.AddRange(areas);
            else if (kind == 0x07)
            {
                foreach (var area in areas)
                {
                    if (area.Left == 0 && area.Right == MaxColumn)
                        book.Sheets[index].TitleRows = (area.Top, area.Bottom);
                    else if (area.Top == 0 && area.Bottom == SheetPagination.MaxRow)
                        book.Sheets[index].TitleColumns = (area.Left, area.Right);
                }
            }
        }
        return book;
    }

    /// <param name="charts">The workbook's chart context (BIFF8), or null to read no chart.</param>
    private static Sheet ReadSheet(byte[] stream, int offset, bool biff8, ChartRecords? charts)
    {
        var sheet = new Sheet();
        Anchor? anchor = null;
        (Anchor? Anchor, bool Prints)? chartObject = null;
        var placed = new List<(Chart Chart, Anchor Anchor)>();
        int? paper = null, scale = null, firstPage = null, fitWidth = null, fitHeight = null;
        bool portrait = true, overThenDown = false, setupValid = false, usePage = false, fitToPage = false;
        double? left = null, right = null, top = null, bottom = null, header = null, footer = null;
        bool hCenter = false, vCenter = false, grid = false, headings = false;
        string? headerText = null, footerText = null;
        var frozen = false;
        var splitRow = 0;
        foreach (var (id, data, start) in RecordsAt(stream, offset))
        {
            switch (id)
            {
                case 0x00EC: // MSODRAWING: the shape the next OBJ record describes, with its anchor
                    anchor = ClientAnchor(data) ?? anchor;
                    break;
                case 0x005D when data.Length >= 6: // OBJ: the object's type is at offset 4, in BIFF8's ftCmo as in BIFF5's header
                    {
                        var kind = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4));
                        if (chartObject is { Prints: true })
                            sheet.FirstObject ??= "charts"; // a chart OBJ without its substream
                        chartObject = null;
                        if (kind == 0x05 && charts is not null)
                        {
                            // ftCmo fPrint: a chart set not to print is left out, as Excel leaves it out.
                            chartObject = (anchor, data.Length >= 10 && (data[8] & 0x10) != 0);
                        }
                        else if (kind != 0x19) // a comment's note prints only when the page setup says so
                        {
                            sheet.FirstObject ??= kind switch
                            {
                                0x05 => "charts",
                                0x08 => "pictures",
                                0x06 => "text boxes",
                                0x07 or (>= 0x0B and <= 0x14) => "form controls",
                                _ => "shapes",
                            };
                        }
                        anchor = null;
                        break;
                    }
                case 0x0809 when start != offset: // an embedded chart's substream, after its OBJ record
                    if (chartObject is { } chart && charts is not null)
                    {
                        if (charts.Read(Substream(stream, start)) is not { } read)
                            sheet.FirstObject ??= "charts";
                        else if (chart.Prints && chart.Anchor is { } where)
                            placed.Add((read, where));
                        else if (chart.Prints)
                            sheet.FirstObject ??= "charts"; // no anchor to place it by
                    }
                    chartObject = null;
                    break;
                case 0x007D when data.Length >= 10: // COLINFO
                    {
                        var first = BinaryPrimitives.ReadUInt16LittleEndian(data);
                        var last = Math.Min((int)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2)), MaxColumn);
                        var width = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4)) / 256.0;
                        var hidden = (data[8] & 0x01) != 0 || width == 0;
                        for (var c = first; c <= last; c++)
                            sheet.Columns[c] = (width, hidden);
                        break;
                    }
                case 0x0208 when data.Length >= 14: // ROW
                    {
                        var height = (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6)) & 0x7FFF) / 20.0;
                        var flags = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(12));
                        sheet.Rows[BinaryPrimitives.ReadUInt16LittleEndian(data)] = (height, (flags & 0x40) != 0, (flags & 0x20) != 0);
                        break;
                    }
                case 0x00FD or 0x0203 or 0x027E or 0x0201 or 0x0006 or 0x0205 or 0x0204 or 0x00D6 when data.Length >= 6:
                    // LABELSST, NUMBER, RK, BLANK, FORMULA, BOOLERR, LABEL, RSTRING: row, column, XF.
                    sheet.Cells[(BinaryPrimitives.ReadUInt16LittleEndian(data), BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2)))] =
                        BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4));
                    break;
                case 0x00BD or 0x00BE when data.Length >= 6: // MULRK, MULBLANK: row, first column, (XF[, RK]) per column
                    {
                        var row = BinaryPrimitives.ReadUInt16LittleEndian(data);
                        var column = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
                        var step = id == 0x00BD ? 6 : 2;
                        for (var at = 4; at + step <= data.Length - 2; at += step, column++)
                            sheet.Cells[(row, column)] = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at));
                        break;
                    }
                case 0x00A1 when data.Length >= 34: // SETUP
                    {
                        var flags = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(10));
                        setupValid = (flags & 0x04) == 0;
                        paper = BinaryPrimitives.ReadUInt16LittleEndian(data);
                        scale = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
                        firstPage = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(4));
                        fitWidth = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6));
                        fitHeight = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8));
                        overThenDown = (flags & 0x01) != 0;
                        portrait = (flags & 0x02) != 0 || (flags & 0x40) != 0;
                        usePage = (flags & 0x80) != 0;
                        header = Inches(data, 16);
                        footer = Inches(data, 24);
                        break;
                    }
                case 0x0026 when data.Length >= 8:
                    left = Inches(data, 0);
                    break;
                case 0x0027 when data.Length >= 8:
                    right = Inches(data, 0);
                    break;
                case 0x0028 when data.Length >= 8:
                    top = Inches(data, 0);
                    break;
                case 0x0029 when data.Length >= 8:
                    bottom = Inches(data, 0);
                    break;
                case 0x0083 when data.Length >= 2:
                    hCenter = data[0] != 0;
                    break;
                case 0x0084 when data.Length >= 2:
                    vCenter = data[0] != 0;
                    break;
                case 0x002B when data.Length >= 2:
                    grid = data[0] != 0;
                    break;
                case 0x002A when data.Length >= 2:
                    headings = data[0] != 0;
                    break;
                case 0x0014 when biff8 && data.Length >= 3:
                    headerText = LongString(data, 0);
                    break;
                case 0x0015 when biff8 && data.Length >= 3:
                    footerText = LongString(data, 0);
                    break;
                case 0x001B or 0x001A when data.Length >= 2: // HORIZONTALPAGEBREAKS, VERTICALPAGEBREAKS
                    {
                        var count = BinaryPrimitives.ReadUInt16LittleEndian(data);
                        var size = biff8 ? 6 : 2;
                        for (var i = 0; i < count && 2 + (i + 1) * size <= data.Length; i++)
                        {
                            var at = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2 + i * size));
                            if (at > 0)
                                (id == 0x001B ? sheet.RowBreaks : sheet.ColumnBreaks).Add(at);
                        }
                        break;
                    }
                case 0x0081 when data.Length >= 2: // WSBOOL
                    fitToPage = (BinaryPrimitives.ReadUInt16LittleEndian(data) & 0x0100) != 0;
                    break;
                case 0x0225 when data.Length >= 4: // DEFAULTROWHEIGHT
                    sheet.DefaultRowHeight = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2)) / 20.0 is var points and > 0 and <= 409.5 ? points : null;
                    break;
                case 0x0055 when data.Length >= 2: // DEFCOLWIDTH
                    sheet.BaseColumnWidth = Math.Min((int)BinaryPrimitives.ReadUInt16LittleEndian(data), 255);
                    break;
                case 0x0099 when data.Length >= 2: // STANDARDWIDTH
                    sheet.DefaultColumnWidth = Math.Min(BinaryPrimitives.ReadUInt16LittleEndian(data) / 256.0, 255);
                    break;
                case 0x023E when data.Length >= 2: // WINDOW2
                    sheet.RightToLeft = (BinaryPrimitives.ReadUInt16LittleEndian(data) & 0x0040) != 0;
                    frozen = (BinaryPrimitives.ReadUInt16LittleEndian(data) & 0x0008) != 0;
                    break;
                case 0x0041 when data.Length >= 4: // PANE: the split row of frozen panes
                    splitRow = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
                    break;
            }
        }

        if (chartObject is { Prints: true })
            sheet.FirstObject ??= "charts";
        sheet.FrozenRows = frozen ? splitRow : 0;

        // Excel 97–2003's defaults: 0.75 in left and right, 1 in top and bottom, 0.5 in header and footer.
        var setup = SheetPageSetup.ForRegion();
        if (setupValid && paper is { } code && Paper.FromCode(code) is { } paperSize)
            setup = setup with { PaperWidth = paperSize.Width, PaperHeight = paperSize.Height };
        static double Margin(double? inches, double fallback) => inches is { } value and >= 0 and <= 50 ? value * 72 : fallback;
        sheet.PageSetup = setup with
        {
            Landscape = setupValid && !portrait,
            Scale = setupValid && scale is >= 10 and <= 400 ? scale.Value : 100,
            FitToPage = fitToPage,
            FitWidth = fitWidth ?? 1,
            FitHeight = fitHeight ?? 1,
            OverThenDown = overThenDown,
            FirstPageNumber = usePage ? firstPage : null,
            LeftMargin = Margin(left, 0.75 * 72),
            RightMargin = Margin(right, 0.75 * 72),
            TopMargin = Margin(top, 72),
            BottomMargin = Margin(bottom, 72),
            HeaderMargin = Margin(header, 0.5 * 72),
            FooterMargin = Margin(footer, 0.5 * 72),
            CenterHorizontally = hCenter,
            CenterVertically = vCenter,
            PrintGridLines = grid,
            PrintHeadings = headings,
            HeaderFooter = new SheetHeaderFooter { OddHeader = headerText, OddFooter = footerText },
        };
        foreach (var (chart, where) in placed)
        {
            sheet.Charts.Add(new SheetChart(chart, where.Row1, where.Column1, where.Dx1 / 1024.0 * ColumnPoints(sheet, where.Column1),
                where.Dy1 / 256.0 * RowPoints(sheet, where.Row1), 0, 0)
            {
                To = (where.Row2, where.Column2, where.Dx2 / 1024.0 * ColumnPoints(sheet, where.Column2), where.Dy2 / 256.0 * RowPoints(sheet, where.Row2)),
            });
        }
        return sheet;
    }

    /// <summary>
    /// An OfficeArt client anchor (OfficeArtClientAnchorSheet: the cells under the corners and offsets in 1/1024 of the
    /// column's width and 1/256 of the row's height), corners in order.
    /// </summary>
    private readonly record struct Anchor(int Column1, int Dx1, int Row1, int Dy1, int Column2, int Dx2, int Row2, int Dy2);

    /// <summary>
    /// The last client anchor (0xF010) of the OfficeArt records in an MSODRAWING record: containers (version 0xF) are
    /// entered, the other records skipped; null when there is none.
    /// </summary>
    private static Anchor? ClientAnchor(byte[] data)
    {
        Anchor? found = null;
        var at = 0L;
        while (at + 8 <= data.Length)
        {
            var i = (int)at;
            var version = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i)) & 0x0F;
            var type = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i + 2));
            var length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i + 4));
            if (version == 0x0F)
            {
                at += 8;
                continue;
            }
            if (type == 0xF010 && length >= 18 && i + 8 + 18 <= data.Length)
            {
                int U(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i + 8 + offset));
                int S(int offset) => Math.Clamp((int)BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(i + 8 + offset)), 0, 1024);
                found = new Anchor(Math.Min(U(2), MaxColumn), S(4), Math.Min(U(6), SheetPagination.MaxRow), S(8),
                    Math.Min(U(10), MaxColumn), S(12), Math.Min(U(14), SheetPagination.MaxRow), S(16));
            }
            at += 8 + (long)length;
        }
        return found;
    }

    /// <summary>
    /// A column's width in points, as Excel 97–2003 lays it out with its default font (Arial 10: digits 7 pixels wide),
    /// for anchors; the sheet printer measures the page itself.
    /// </summary>
    private static double ColumnPoints(Sheet sheet, int column)
    {
        double characters;
        if (sheet.Columns.TryGetValue(column, out var own))
            characters = own.Hidden ? 0 : own.Width;
        else
            characters = sheet.DefaultColumnWidth ?? (sheet.BaseColumnWidth is { } width ? width + 5 / 7.0 : 8.43);
        return Math.Truncate((256 * Math.Clamp(characters, 0, 255) + Math.Truncate(128 / 7.0)) / 256 * 7) * 0.75;
    }

    private static double RowPoints(Sheet sheet, int row) =>
        sheet.Rows.TryGetValue(row, out var own) ? own.Hidden ? 0 : own.Points > 0 ? own.Points : sheet.DefaultRowHeight ?? 12.75 : sheet.DefaultRowHeight ?? 12.75;

    /// <summary>The workbook's charts: what they refer to in the globals, and the points they draw together.</summary>
    private sealed class ChartRecords(BiffChartBook book)
    {
        private readonly ChartBudget _budget = new();

        /// <summary>The chart of a chart substream, counted; null when BiffChartReader doesn't draw it.</summary>
        public Chart? Read(List<(int Id, byte[] Data)> records)
        {
            if (BiffChartReader.Read(records, book).Chart is not { } chart)
                return null;
            _budget.Charge(chart);
            return chart;
        }
    }

    private static double? Inches(byte[] data, int at) =>
        at + 8 <= data.Length && BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(at)) is var value && double.IsFinite(value) ? value : null;

    /// <summary>A cell format from a BIFF8 XF record.</summary>
    private static CellStyle Style(byte[] xf, FontRecord? font, Func<int, string?> color)
    {
        var alignment = xf[6];
        var horizontal = alignment & 0x07;
        var vertical = (alignment >> 4) & 0x07;
        var borders = BinaryPrimitives.ReadUInt32LittleEndian(xf.AsSpan(10));
        var more = BinaryPrimitives.ReadUInt32LittleEndian(xf.AsSpan(14));
        var fill = BinaryPrimitives.ReadUInt16LittleEndian(xf.AsSpan(18));
        CellBorder Border(uint style, uint index) => style == 0 ? CellBorder.None : new CellBorder(BorderStyles[Math.Min(style, 13)], color((int)index));
        var pattern = (int)(more >> 26) & 0x3F;
        var foreground = color(fill & 0x7F);
        var background = color((fill >> 7) & 0x7F);
        var diagonal = Border((more >> 21) & 0x0F, (more >> 14) & 0x7F);
        var diagonalFlags = borders >> 30;
        return new CellStyle
        {
            Bold = font?.Bold ?? false,
            Italic = font?.Italic ?? false,
            Strike = font?.Strike ?? false,
            Underline = font is { Underline: not 0 },
            DoubleUnderline = font is { Underline: 2 or 0x22 },
            Script = font?.Script switch { 1 => 1, 2 => -1, _ => 0 },
            FontSize = font?.Size is > 0 and <= 409 ? font.Size : null,
            FontName = font?.Name is { Length: > 0 } name ? name : null,
            Color = font is null ? null : color(font.Color),
            Fill = pattern switch
            {
                0 or 17 or 18 => null, // none and the gray125 / gray0625 patterns
                1 => foreground ?? background,
                _ => foreground ?? "#000000",
            },
            FillPattern = pattern is >= 2 and <= 16 ? PatternNames[pattern] : null,
            PatternBackground = pattern is >= 2 and <= 16 ? background : null,
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
            VerticalAlign = vertical switch
            {
                0 or 3 or 4 => CellVerticalAlign.Top,
                1 => CellVerticalAlign.Center,
                _ => CellVerticalAlign.Bottom,
            },
            VerticalJustify = vertical is 3 or 4,
            Wrap = (alignment & 0x08) != 0,
            Rotation = XlsxReader.Rotation(xf[7]),
            Indent = xf[8] & 0x0F,
            Shrink = (xf[8] & 0x10) != 0,
            Left = Border(borders & 0x0F, (borders >> 16) & 0x7F),
            Right = Border((borders >> 4) & 0x0F, (borders >> 23) & 0x7F),
            Top = Border((borders >> 8) & 0x0F, more & 0x7F),
            Bottom = Border((borders >> 12) & 0x0F, (more >> 7) & 0x7F),
            DiagonalDown = (diagonalFlags & 1) != 0 ? diagonal : CellBorder.None,
            DiagonalUp = (diagonalFlags & 2) != 0 ? diagonal : CellBorder.None,
        };
    }

    private static readonly string[] BorderStyles =
    [
        "none", "thin", "medium", "dashed", "dotted", "thick", "double", "hair", "mediumDashed", "dashDot", "mediumDashDot",
        "dashDotDot", "mediumDashDotDot", "slantDashDot",
    ];

    private static readonly string?[] PatternNames =
    [
        null, null, "mediumGray", "darkGray", "lightGray", "darkHorizontal", "darkVertical", "darkDown", "darkUp", "darkGrid",
        "darkTrellis", "lightHorizontal", "lightVertical", "lightDown", "lightUp", "lightGrid", "lightTrellis",
    ];

    /// <summary>The areas of a print area or print titles formula: area and cell references (3D), joined by unions.</summary>
    private static List<CellRange> Areas(byte[] formula)
    {
        var areas = new List<CellRange>();
        var i = 0;
        while (i < formula.Length)
        {
            var token = formula[i];
            switch (token)
            {
                case 0x3B or 0x5B or 0x7B when i + 11 <= formula.Length: // ptgArea3d
                    {
                        var top = BinaryPrimitives.ReadUInt16LittleEndian(formula.AsSpan(i + 3));
                        var bottom = BinaryPrimitives.ReadUInt16LittleEndian(formula.AsSpan(i + 5));
                        var left = BinaryPrimitives.ReadUInt16LittleEndian(formula.AsSpan(i + 7)) & 0x3FFF;
                        var right = BinaryPrimitives.ReadUInt16LittleEndian(formula.AsSpan(i + 9)) & 0x3FFF;
                        // BIFF8 sheets are 65,536 × 256: a reference to all of them means whole columns or rows.
                        areas.Add(new CellRange(top, left, bottom >= 0xFFFF ? SheetPagination.MaxRow : bottom, right >= 0xFF ? MaxColumn : right));
                        i += 11;
                        break;
                    }
                case 0x3A or 0x5A or 0x7A when i + 7 <= formula.Length: // ptgRef3d
                    {
                        var row = BinaryPrimitives.ReadUInt16LittleEndian(formula.AsSpan(i + 3));
                        var column = BinaryPrimitives.ReadUInt16LittleEndian(formula.AsSpan(i + 5)) & 0x3FFF;
                        areas.Add(new CellRange(row, column, row, column));
                        i += 7;
                        break;
                    }
                case 0x29 or 0x49 or 0x69: // ptgMemFunc: the references follow
                    i += 3;
                    break;
                case 0x10: // ptgUnion
                    i++;
                    break;
                default:
                    return areas;
            }
        }
        return areas;
    }

    /// <summary>A ShortXLUnicodeString (1-byte length, flags, characters) at <paramref name="at"/>.</summary>
    private static string ShortString(byte[] data, int at)
    {
        if (at + 2 > data.Length)
            return "";
        var length = data[at];
        var wide = (data[at + 1] & 0x01) != 0;
        var bytes = Math.Min(length * (wide ? 2 : 1), data.Length - at - 2);
        return wide ? Encoding.Unicode.GetString(data, at + 2, bytes & ~1) : Encoding.Latin1.GetString(data, at + 2, bytes);
    }

    /// <summary>An XLUnicodeString (2-byte length, flags, characters) at <paramref name="at"/>.</summary>
    private static string LongString(byte[] data, int at)
    {
        var length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at));
        var wide = (data[at + 2] & 0x01) != 0;
        var bytes = Math.Min(length * (wide ? 2 : 1), data.Length - at - 3);
        return wide ? Encoding.Unicode.GetString(data, at + 3, bytes & ~1) : Encoding.Latin1.GetString(data, at + 3, bytes);
    }

    /// <summary>The records of one substream: from its BOF up to its EOF; substreams inside it (embedded charts) are skipped.</summary>
    private static IEnumerable<(int Id, byte[] Data)> Records(byte[] stream, int offset) => RecordsAt(stream, offset).Select(r => (r.Id, r.Data));

    /// <summary>
    /// The records of one substream with where each starts: its BOF first, up to its EOF. A substream inside it (an
    /// embedded chart's, after its OBJ record) gives its BOF only, and the records up to its own EOF are skipped.
    /// </summary>
    private static IEnumerable<(int Id, byte[] Data, int Offset)> RecordsAt(byte[] stream, int offset)
    {
        if (offset < 0 || offset + 4 > stream.Length || BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(offset)) != 0x0809)
            yield break;
        var position = offset;
        var depth = 0;
        while (position + 4 <= stream.Length)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(position));
            var length = BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(position + 2));
            if (position + 4 + length > stream.Length)
                yield break;
            if (id == 0x0809 && position != offset)
            {
                if (depth == 0)
                    yield return (id, stream.AsSpan(position + 4, length).ToArray(), position);
                depth++;
            }
            else if (id == 0x000A)
            {
                if (depth == 0)
                    yield break;
                depth--;
            }
            else if (depth == 0)
            {
                yield return (id, stream.AsSpan(position + 4, length).ToArray(), position);
            }
            position += 4 + length;
        }
    }

    /// <summary>All records of the substream whose BOF is at <paramref name="offset"/>, through its EOF (substreams inside it included).</summary>
    private static List<(int Id, byte[] Data)> Substream(byte[] stream, int offset)
    {
        var records = new List<(int, byte[])>();
        if (offset < 0 || offset + 4 > stream.Length || BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(offset)) != 0x0809)
            return records;
        var position = offset;
        var depth = 0;
        while (position + 4 <= stream.Length)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(position));
            var length = BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(position + 2));
            if (position + 4 + length > stream.Length)
                break;
            records.Add((id, stream.AsSpan(position + 4, length).ToArray()));
            position += 4 + length;
            if (id == 0x0809)
                depth++;
            else if (id == 0x000A && --depth == 0)
                break;
        }
        return records;
    }

    // ───────────────────────── Compound file ─────────────────────────

    private const uint LastSector = 0xFFFFFFFA; // sector numbers at and above are markers (end of chain, free, ...)

    /// <summary>The "Workbook" (Excel 97+) or "Book" (Excel 5/95) stream of a compound file; null if there is none.</summary>
    private static byte[]? WorkbookStream(byte[] file)
    {
        if (file.Length < 512 || BinaryPrimitives.ReadUInt64LittleEndian(file) != 0xE11AB1A1E011CFD0)
            return null;
        var sectorSize = 1 << BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(30));
        var miniSectorSize = 1 << BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(32));
        if (sectorSize is not (512 or 4096) || miniSectorSize > sectorSize)
            return null;
        var sectorCount = (file.Length + sectorSize - 1) / sectorSize;
        ArraySegment<byte> Sector(uint index)
        {
            var start = (int)Math.Min((long)(index + 1) * sectorSize, file.Length);
            return new ArraySegment<byte>(file, start, Math.Min(sectorSize, file.Length - start));
        }

        // The FAT is listed by the header (109 sectors) and, in big files, a chain of DIFAT sectors.
        var fatSectors = new List<uint>();
        for (var i = 0; i < 109; i++)
            fatSectors.Add(BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(76 + i * 4)));
        for (var difat = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(68)); difat < LastSector && fatSectors.Count < sectorCount * 2;)
        {
            var sector = Sector(difat);
            if (sector.Count < sectorSize)
                break;
            for (var i = 0; i + 4 < sector.Count; i += 4)
                fatSectors.Add(BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(i)));
            difat = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(sector.Count - 4));
        }
        var fat = new List<uint>();
        foreach (var sector in fatSectors.Where(s => s < LastSector))
        {
            var data = Sector(sector);
            for (var i = 0; i + 4 <= data.Count; i += 4)
                fat.Add(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i)));
        }

        // A stream's sectors, each pointing to the next one in the table; the step count stops broken, circular chains.
        byte[] Chain(uint start, List<uint> table, Func<uint, ArraySegment<byte>> read)
        {
            var bytes = new List<byte>();
            for (uint s = start, steps = 0; s < LastSector && s < table.Count && steps <= table.Count; s = table[(int)s], steps++)
                bytes.AddRange(read(s));
            return [.. bytes];
        }

        var directory = Chain(BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(48)), fat, Sector);
        (uint Start, long Size)? workbook = null;
        for (var i = 0; i + 128 <= directory.Length && workbook is null; i += 128)
        {
            var entry = directory.AsSpan(i, 128);
            var nameLength = Math.Clamp((int)BinaryPrimitives.ReadUInt16LittleEndian(entry[64..]) - 2, 0, 62);
            var name = Encoding.Unicode.GetString(entry[..nameLength]);
            if (entry[66] == 2 && (name.Equals("Workbook", StringComparison.OrdinalIgnoreCase) || name.Equals("Book", StringComparison.OrdinalIgnoreCase)))
                workbook = (BinaryPrimitives.ReadUInt32LittleEndian(entry[116..]), BinaryPrimitives.ReadUInt32LittleEndian(entry[120..]));
        }
        if (workbook is not { } stream || directory.Length < 128)
            return null;

        byte[] bytes;
        if (stream.Size < BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(56)))
        {
            // Small streams live in the mini stream (held by the root entry), addressed through the mini FAT.
            var miniStream = Chain(BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(116)), fat, Sector);
            var miniFatBytes = Chain(BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(60)), fat, Sector);
            var miniFat = new List<uint>();
            for (var i = 0; i + 4 <= miniFatBytes.Length; i += 4)
                miniFat.Add(BinaryPrimitives.ReadUInt32LittleEndian(miniFatBytes.AsSpan(i)));
            bytes = Chain(stream.Start, miniFat, s =>
                (long)(s + 1) * miniSectorSize <= miniStream.Length ? new ArraySegment<byte>(miniStream, (int)s * miniSectorSize, miniSectorSize) : []);
        }
        else
        {
            bytes = Chain(stream.Start, fat, Sector);
        }
        return bytes.Length >= stream.Size ? bytes[..(int)stream.Size] : null;
    }
}
