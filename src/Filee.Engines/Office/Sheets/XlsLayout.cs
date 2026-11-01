// What ExcelDataReader doesn't expose of an XLS file, read from the BIFF records themselves: column widths (COLINFO)
// and row heights with their "custom" flag (ROW), the cell formats (FONT, XF and PALETTE in the workbook globals and
// each cell's XF index, blank cells included) and printing (SETUP, the margin records, HCENTER / VCENTER,
// PRINTGRIDLINES / PRINTHEADERS, HEADER / FOOTER, page breaks, WSBOOL's fit to page, the Print_Area and Print_Titles
// names, DEFCOLWIDTH / STANDARDWIDTH / DEFAULTROWHEIGHT, WINDOW2's right-to-left flag).
// Fonts, formats, headers and names are read from BIFF8 (Excel 97–2003); older files get widths, heights and the page
// setup. Encrypted workbooks and pre-Excel 5 files return null: the reader then falls back to ExcelDataReader's values.

using System.Buffers.Binary;
using System.Text;

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
    }

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
                    }
                    position++;
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

        foreach (var offset in offsets)
            book.Sheets.Add(ReadSheet(stream, offset, biff8));
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

    private static Sheet ReadSheet(byte[] stream, int offset, bool biff8)
    {
        var sheet = new Sheet();
        int? paper = null, scale = null, firstPage = null, fitWidth = null, fitHeight = null;
        bool portrait = true, overThenDown = false, setupValid = false, usePage = false, fitToPage = false;
        double? left = null, right = null, top = null, bottom = null, header = null, footer = null;
        bool hCenter = false, vCenter = false, grid = false, headings = false;
        string? headerText = null, footerText = null;
        foreach (var (id, data) in Records(stream, offset))
        {
            switch (id)
            {
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
                    break;
            }
        }

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
        return sheet;
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

    /// <summary>The records of one substream: from its BOF up to its EOF (or the next BOF).</summary>
    private static IEnumerable<(int Id, byte[] Data)> Records(byte[] stream, int offset)
    {
        if (offset < 0 || offset + 4 > stream.Length || BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(offset)) != 0x0809)
            yield break;
        var position = offset;
        var first = true;
        while (position + 4 <= stream.Length)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(position));
            var length = BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(position + 2));
            if (id == 0x000A || id == 0x0809 && !first || position + 4 + length > stream.Length)
                yield break;
            yield return (id, stream.AsSpan(position + 4, length).ToArray());
            position += 4 + length;
            first = false;
        }
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
