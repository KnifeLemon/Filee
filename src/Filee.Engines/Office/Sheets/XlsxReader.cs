// XLSX (SpreadsheetML) → Workbook, read directly (no Excel, no LibreOffice): cell values formatted the way Excel
// shows them (number formats through ExcelNumberFormat, MIT), rich text, fonts, fills (solid, patterns, gradients),
// borders (diagonals too), alignment (wrap, shrink, rotation, indent, justify, fill, centre across), merged cells,
// column widths, row heights (custom and the autofit Excel saved), hidden rows / columns / sheets, frozen header
// rows, and what printing needs: the page setup, margins, print options, headers and footers (with their
// pictures), print areas, print titles, manual page breaks, conditional formats that colour cells and pictures.
// Charts, shapes and comments are not read. The package is read within DocumentLimits.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Filee.Engines.Documents;
using Filee.Engines.Office.Ooxml;

namespace Filee.Engines.Office.Sheets;

internal sealed class XlsxReader
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Xdr = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace V = "urn:schemas-microsoft-com:vml";
    private static readonly XNamespace O = "urn:schemas-microsoft-com:office:office";

    /// <summary>Days between the 1900 and 1904 date systems (1 January 1904 is serial 1462 in the 1900 system).</summary>
    private const double Date1904Offset = 1462;

    /// <summary>Points per EMU (DrawingML positions).</summary>
    private const double PointsPerEmu = 1 / 12700.0;

    /// <summary>Column entries (&lt;col min max&gt; expanded) one workbook may set, against files that repeat whole-sheet ranges.</summary>
    private const int MaxColumnEntries = 4_000_000;

    /// <summary>Bytes of one picture read at most.</summary>
    private const long MaxPictureBytes = 64L * 1024 * 1024;

    private readonly OpcPackage _package;
    private readonly List<(string Text, IReadOnlyList<CellRun>? Runs)> _sharedStrings = [];
    private readonly List<(CellStyle Style, string Format)> _cellFormats = [];
    private readonly List<DifferentialStyle> _dxfs = [];
    private OoxmlColors _colors = OoxmlColors.FromTheme(null);
    private NumberFormatter _formatter = new();
    private bool _date1904;
    private int _columnEntries;

    private XlsxReader(OpcPackage package) => _package = package;

    /// <summary>Reads every visible sheet of <paramref name="path"/>.</summary>
    public static Workbook Read(string path)
    {
        using var package = new OpcPackage(path, DocumentLimits.Default);
        return new XlsxReader(package).ReadWorkbook();
    }

    private Workbook ReadWorkbook()
    {
        var workbookPath = _package.MainPartPath ?? "xl/workbook.xml";
        var workbook = _package.Part(workbookPath)?.Root ?? throw new InvalidDataException("The file is not an Excel workbook (no xl/workbook.xml).");
        _date1904 = (string?)workbook.Element(S + "workbookPr")?.Attribute("date1904") is "1" or "true";
        _formatter = new NumberFormatter(_date1904);

        _colors = OoxmlColors.FromTheme(_package.TargetOfType(workbookPath, "/theme") is { } theme ? _package.Part(theme) : null);
        if (_package.TargetOfType(workbookPath, "/sharedStrings") is { } shared)
            ReadSharedStrings(_package.Part(shared));
        var result = new Workbook();
        if (_package.TargetOfType(workbookPath, "/styles") is { } styles)
            ReadStyles(_package.Part(styles), result);

        // Print titles and print areas are defined names per sheet, by the sheet's position (hidden sheets count).
        var titles = new Dictionary<int, string>();
        var areas = new Dictionary<int, string>();
        foreach (var name in workbook.Element(S + "definedNames")?.Elements(S + "definedName") ?? [])
        {
            if (!int.TryParse((string?)name.Attribute("localSheetId"), out var id))
                continue;
            switch ((string?)name.Attribute("name"))
            {
                case "_xlnm.Print_Titles":
                    titles[id] = name.Value;
                    break;
                case "_xlnm.Print_Area":
                    areas[id] = name.Value;
                    break;
            }
        }

        var index = -1;
        foreach (var sheet in workbook.Element(S + "sheets")?.Elements(S + "sheet") ?? [])
        {
            index++;
            if ((string?)sheet.Attribute("state") is "hidden" or "veryHidden")
                continue;
            var target = _package.Target(workbookPath, (string?)sheet.Attribute(R + "id"));
            if (target is null || _package.Part(target)?.Root is not { } xml || xml.Name != S + "worksheet")
                continue; // chart sheets and dialog sheets have no cells
            var read = ReadSheet((string?)sheet.Attribute("name") ?? "Sheet", xml, target);
            if (titles.TryGetValue(index, out var title))
            {
                read.TitleRows = PrintTitleRows(title);
                read.TitleColumns = PrintTitleColumns(title);
            }
            if (areas.TryGetValue(index, out var area))
                read.PrintArea.AddRange(References(area));
            result.Sheets.Add(read);
        }
        return result;
    }

    /// <summary>
    /// The 0-based rows of a print titles reference: <c>'Bill of quantities'!$3:$4</c>, also next to repeated columns
    /// (<c>Sheet1!$A:$B,Sheet1!$1:$2</c>); null when it repeats columns only.
    /// </summary>
    internal static (int First, int Last)? PrintTitleRows(string reference)
    {
        foreach (var part in Parts(reference))
        {
            var match = PrintTitleRowsPattern.Match(part);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var first) && int.TryParse(match.Groups[2].Value, out var last)
                && first >= 1 && last >= first)
                return (first - 1, last - 1);
        }
        return null;
    }

    /// <summary>The 0-based columns of a print titles reference (<c>Sheet1!$A:$B</c>); null when it repeats rows only.</summary>
    internal static (int First, int Last)? PrintTitleColumns(string reference)
    {
        foreach (var part in Parts(reference))
        {
            var match = ColumnsPattern.Match(part);
            if (match.Success)
            {
                var first = ColumnIndex(match.Groups[1].Value);
                var last = ColumnIndex(match.Groups[2].Value);
                if (first >= 0 && last >= first)
                    return (first, last);
            }
        }
        return null;
    }

    /// <summary>
    /// The ranges of a print area: <c>Sheet1!$A$1:$D$20,Sheet1!$F$1:$G$5</c>, whole columns (<c>$A:$C</c>) and
    /// whole rows (<c>$1:$5</c>).
    /// </summary>
    internal static List<CellRange> References(string reference)
    {
        var ranges = new List<CellRange>();
        foreach (var part in Parts(reference))
        {
            if (ColumnsPattern.Match(part) is { Success: true } columns)
            {
                var (first, last) = (ColumnIndex(columns.Groups[1].Value), ColumnIndex(columns.Groups[2].Value));
                if (first >= 0 && last >= first)
                    ranges.Add(new CellRange(0, first, SheetPagination.MaxRow, Math.Min(last, SheetPagination.MaxColumn)));
            }
            else if (PrintTitleRowsPattern.Match(part) is { Success: true } rows
                     && int.TryParse(rows.Groups[1].Value, out var top) && int.TryParse(rows.Groups[2].Value, out var bottom) && top >= 1 && bottom >= top)
            {
                ranges.Add(new CellRange(top - 1, 0, Math.Min(bottom - 1, SheetPagination.MaxRow), SheetPagination.MaxColumn));
            }
            else if (CellRange.Parse(part) is { } range && range.Bottom <= SheetPagination.MaxRow && range.Right <= SheetPagination.MaxColumn)
            {
                ranges.Add(range);
            }
        }
        return ranges;
    }

    /// <summary>The references of a defined name's value without their sheet names.</summary>
    private static IEnumerable<string> Parts(string reference)
    {
        // A sheet name in quotes may hold commas: split outside quotes only.
        var start = 0;
        var quoted = false;
        for (var i = 0; i <= reference.Length; i++)
        {
            if (i < reference.Length && reference[i] == '\'')
                quoted = !quoted;
            if (i == reference.Length || reference[i] == ',' && !quoted)
            {
                var part = reference[start..i];
                yield return part[(part.LastIndexOf('!') + 1)..].Trim();
                start = i + 1;
            }
        }
    }

    private static int ColumnIndex(string letters)
    {
        var column = 0;
        foreach (var ch in letters.Replace("$", "", StringComparison.Ordinal))
        {
            if (!char.IsAsciiLetter(ch) || column > SheetPagination.MaxColumn)
                return -1;
            column = column * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        }
        return column - 1;
    }

    private static readonly Regex PrintTitleRowsPattern = new(@"^\$?(\d+):\$?(\d+)$", RegexOptions.CultureInvariant);
    private static readonly Regex ColumnsPattern = new(@"^\$?([A-Za-z]{1,3}):\$?([A-Za-z]{1,3})$", RegexOptions.CultureInvariant);

    private void ReadSharedStrings(XDocument? xml)
    {
        foreach (var item in xml?.Root?.Elements(S + "si") ?? [])
            _sharedStrings.Add(RichText(item));
    }

    /// <summary>Text of a shared or inline string: plain &lt;t&gt; or rich text runs (with their fonts); phonetic hints skipped.</summary>
    private (string Text, IReadOnlyList<CellRun>? Runs) RichText(XElement item)
    {
        if (item.Element(S + "t") is { } plain)
            return (plain.Value, null);
        var sb = new StringBuilder();
        var runs = new List<CellRun>();
        var styled = false;
        foreach (var run in item.Elements(S + "r"))
        {
            var text = run.Element(S + "t")?.Value ?? "";
            sb.Append(text);
            if (run.Element(S + "rPr") is not { } font)
            {
                runs.Add(new CellRun(text));
                continue;
            }
            styled = true;
            runs.Add(new CellRun(text)
            {
                Bold = font.Element(S + "b") is { } b ? On(b) : null,
                Italic = font.Element(S + "i") is { } i ? On(i) : null,
                Underline = font.Element(S + "u") is { } u ? (string?)u.Attribute("val") != "none" : null,
                Strike = font.Element(S + "strike") is { } st ? On(st) : null,
                FontSize = Number(font.Element(S + "sz")?.Attribute("val")),
                Color = _colors.Spreadsheet(font.Element(S + "color")),
                FontName = (string?)font.Element(S + "rFont")?.Attribute("val"),
                Script = (string?)font.Element(S + "vertAlign")?.Attribute("val") switch { "superscript" => 1, "subscript" => -1, "baseline" => 0, _ => null },
            });
        }
        return (sb.ToString(), styled ? runs : null);
    }

    // ───────────────────────── Styles ─────────────────────────

    private sealed record FontInfo(bool Bold, bool Italic, bool Underline, bool DoubleUnderline, bool Strike, double? Size, string? Color, string? Name, int Script);

    private void ReadStyles(XDocument? xml, Workbook book)
    {
        var root = xml?.Root;
        if (root is null)
            return;
        var formats = root.Element(S + "numFmts")?.Elements(S + "numFmt")
            .Where(f => int.TryParse((string?)f.Attribute("numFmtId"), out _))
            .GroupBy(f => int.Parse((string)f.Attribute("numFmtId")!, CultureInfo.InvariantCulture))
            .ToDictionary(g => g.Key, g => (string?)g.First().Attribute("formatCode") ?? "General")
            ?? [];

        var fonts = (root.Element(S + "fonts")?.Elements(S + "font") ?? []).Select(Font).ToList();
        var fills = (root.Element(S + "fills")?.Elements(S + "fill") ?? []).Select(Fill).ToList();
        var borders = (root.Element(S + "borders")?.Elements(S + "border") ?? []).Select(Borders).ToList();

        // The Normal style's font: Excel measures column widths in its digits.
        var normal = root.Element(S + "cellStyleXfs")?.Elements(S + "xf").FirstOrDefault();
        var normalFont = normal is not null && Index(normal, "fontId") < fonts.Count ? fonts[Index(normal, "fontId")] : fonts.FirstOrDefault();
        if (normalFont?.Name is { Length: > 0 } fontName)
            book.DefaultFontName = fontName;
        if (normalFont?.Size is > 0 and <= 409 && normalFont.Size is { } fontSize)
            book.DefaultFontSize = fontSize;

        foreach (var xf in root.Element(S + "cellXfs")?.Elements(S + "xf") ?? [])
        {
            var font = Index(xf, "fontId") is var fi && fi < fonts.Count ? fonts[fi] : null;
            var fill = Index(xf, "fillId") is var fl && fl < fills.Count ? fills[fl] : default;
            var border = Index(xf, "borderId") is var bi && bi < borders.Count ? borders[bi] : NoBorders;
            var alignment = xf.Element(S + "alignment");
            var horizontal = (string?)alignment?.Attribute("horizontal");
            var vertical = (string?)alignment?.Attribute("vertical");
            var rotation = (int)(Number(alignment?.Attribute("textRotation")) ?? 0);
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
                Align = horizontal switch
                {
                    "left" => CellAlign.Left,
                    "center" or "centerContinuous" => CellAlign.Center,
                    "right" => CellAlign.Right,
                    _ => CellAlign.General,
                },
                SpecialAlign = horizontal switch
                {
                    "fill" => CellSpecialAlign.Fill,
                    "justify" => CellSpecialAlign.Justify,
                    "distributed" => CellSpecialAlign.Distributed,
                    "centerContinuous" => CellSpecialAlign.CenterAcross,
                    _ => CellSpecialAlign.None,
                },
                VerticalAlign = vertical switch
                {
                    "top" or "justify" or "distributed" => CellVerticalAlign.Top,
                    "center" => CellVerticalAlign.Center,
                    _ => CellVerticalAlign.Bottom,
                },
                VerticalJustify = vertical is "justify" or "distributed",
                Wrap = On(alignment?.Attribute("wrapText")),
                Shrink = On(alignment?.Attribute("shrinkToFit")),
                Indent = Math.Clamp((int)(Number(alignment?.Attribute("indent")) ?? 0), 0, 250),
                Rotation = Rotation(rotation),
                Left = border.Left,
                Right = border.Right,
                Top = border.Top,
                Bottom = border.Bottom,
                DiagonalUp = border.Up,
                DiagonalDown = border.Down,
            };
            var formatId = Index(xf, "numFmtId");
            _cellFormats.Add((style, formats.GetValueOrDefault(formatId) ?? NumberFormatter.BuiltInFormats.GetValueOrDefault(formatId, "General")));
        }

        foreach (var dxf in root.Element(S + "dxfs")?.Elements(S + "dxf") ?? [])
        {
            var font = dxf.Element(S + "font");
            var pattern = dxf.Element(S + "fill")?.Element(S + "patternFill");
            var sides = dxf.Element(S + "border") is { } b ? Borders(b) : (Left: CellBorder.None, Right: CellBorder.None, Top: CellBorder.None, Bottom: CellBorder.None, Up: CellBorder.None, Down: CellBorder.None);
            _dxfs.Add(new DifferentialStyle
            {
                // A differential fill's colour is its bgColor (fgColor only for patterns).
                Fill = pattern is null ? null : _colors.Spreadsheet(pattern.Element(S + "bgColor")) ?? _colors.Spreadsheet(pattern.Element(S + "fgColor")),
                Color = _colors.Spreadsheet(font?.Element(S + "color")),
                Bold = font?.Element(S + "b") is { } bold ? On(bold) : null,
                Italic = font?.Element(S + "i") is { } italic ? On(italic) : null,
                Underline = font?.Element(S + "u") is { } u ? (string?)u.Attribute("val") != "none" : null,
                Strike = font?.Element(S + "strike") is { } strike ? On(strike) : null,
                Left = sides.Left.IsVisible ? sides.Left : null,
                Right = sides.Right.IsVisible ? sides.Right : null,
                Top = sides.Top.IsVisible ? sides.Top : null,
                Bottom = sides.Bottom.IsVisible ? sides.Bottom : null,
            });
        }
    }

    /// <summary>Excel's textRotation: 1–90 counter-clockwise, 91–180 clockwise (90 − value), 255 stacked.</summary>
    internal static int Rotation(int value) => value switch
    {
        255 => CellStyle.Stacked,
        > 0 and <= 90 => value,
        > 90 and <= 180 => 90 - value,
        _ => 0,
    };

    private static readonly (CellBorder Left, CellBorder Right, CellBorder Top, CellBorder Bottom, CellBorder Up, CellBorder Down) NoBorders =
        (CellBorder.None, CellBorder.None, CellBorder.None, CellBorder.None, CellBorder.None, CellBorder.None);

    private FontInfo Font(XElement f)
    {
        var underline = f.Element(S + "u") is { } u ? (string?)u.Attribute("val") ?? "single" : "none";
        return new FontInfo(
            On(f.Element(S + "b")),
            On(f.Element(S + "i")),
            underline != "none",
            underline is "double" or "doubleAccounting",
            On(f.Element(S + "strike")),
            Number(f.Element(S + "sz")?.Attribute("val")),
            _colors.Spreadsheet(f.Element(S + "color")),
            (string?)f.Element(S + "name")?.Attribute("val"),
            (string?)f.Element(S + "vertAlign")?.Attribute("val") switch { "superscript" => 1, "subscript" => -1, _ => 0 });
    }

    private (string? Color, string? Pattern, string? Background, CellGradient? Gradient) Fill(XElement f)
    {
        if (f.Element(S + "gradientFill") is { } gradient)
        {
            var stops = gradient.Elements(S + "stop")
                .Select(s => (Position: Number(s.Attribute("position")) ?? 0, Color: _colors.Spreadsheet(s.Element(S + "color"))))
                .Where(s => s.Color is not null).Select(s => (s.Position, s.Color!)).Take(16).ToList();
            if (stops.Count == 0)
                return default;
            return (stops[0].Item2, null, null, stops.Count > 1 ? new CellGradient(Number(gradient.Attribute("degree")) ?? 0, stops) : null);
        }
        var pattern = f.Element(S + "patternFill");
        var type = (string?)pattern?.Attribute("patternType");
        if (type is null or "none" or "gray125")
            return default;
        var foreground = _colors.Spreadsheet(pattern!.Element(S + "fgColor"));
        var background = _colors.Spreadsheet(pattern.Element(S + "bgColor"));
        return type == "solid"
            ? (foreground ?? background, null, null, null)
            : (foreground ?? "#000000", type, background, null);
    }

    private (CellBorder Left, CellBorder Right, CellBorder Top, CellBorder Bottom, CellBorder Up, CellBorder Down) Borders(XElement b)
    {
        CellBorder Side(XElement? side) => side is null || (string?)side.Attribute("style") is not { } style
            ? CellBorder.None
            : new CellBorder(style, _colors.Spreadsheet(side.Element(S + "color")));
        var diagonal = Side(b.Element(S + "diagonal"));
        return (Side(b.Element(S + "left") ?? b.Element(S + "start")),
            Side(b.Element(S + "right") ?? b.Element(S + "end")),
            Side(b.Element(S + "top")),
            Side(b.Element(S + "bottom")),
            On(b.Attribute("diagonalUp")) ? diagonal : CellBorder.None,
            On(b.Attribute("diagonalDown")) ? diagonal : CellBorder.None);
    }

    private static int Index(XElement element, string attribute) =>
        int.TryParse((string?)element.Attribute(attribute), out var value) && value >= 0 ? value : 0;

    private static double? Number(XAttribute? attribute) =>
        double.TryParse((string?)attribute, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;

    /// <summary>A boolean element or attribute: present without val, or val="1"/"true".</summary>
    private static bool On(XObject? node) => node switch
    {
        XElement element => (string?)element.Attribute("val") is null or "1" or "true",
        XAttribute attribute => attribute.Value is "1" or "true",
        _ => false,
    };

    // ───────────────────────── Sheets ─────────────────────────

    private Worksheet ReadSheet(string name, XElement xml, string partPath)
    {
        var sheet = new Worksheet(name);
        var format = xml.Element(S + "sheetFormatPr");
        if (Number(format?.Attribute("defaultColWidth")) is { } defaultWidth and >= 0 and <= 255)
        {
            sheet.DefaultColumnWidth = defaultWidth;
            sheet.HasDefaultColumnWidth = true;
        }
        if (Number(format?.Attribute("baseColWidth")) is { } baseWidth and >= 0 and <= 255)
            sheet.BaseColumnWidth = (int)baseWidth;
        if (Number(format?.Attribute("defaultRowHeight")) is { } defaultHeight and > 0 and <= 409.5)
            sheet.DefaultRowHeight = defaultHeight;

        var view = xml.Element(S + "sheetViews")?.Elements(S + "sheetView").FirstOrDefault();
        sheet.RightToLeft = On(view?.Attribute("rightToLeft"));
        var pane = view?.Element(S + "pane");
        if ((string?)pane?.Attribute("state") is "frozen" or "frozenSplit" && Number(pane.Attribute("ySplit")) is { } frozen and >= 0 and <= SheetPagination.MaxRow)
            sheet.FrozenRows = (int)frozen;

        foreach (var col in xml.Element(S + "cols")?.Elements(S + "col") ?? [])
        {
            var min = Index(col, "min");
            var max = Math.Min(Index(col, "max"), SheetPagination.MaxColumn + 1);
            var width = Number(col.Attribute("width"));
            var hidden = On(col.Attribute("hidden")) || width == 0;
            // Most files end with one entry for the rest of the sheet; its width only matters as the default.
            if (min >= 1 && max >= min)
            {
                if ((_columnEntries += max - min + 1) > MaxColumnEntries)
                    throw DocumentLimits.Exceeded("The workbook sets more than 4,000,000 column widths.");
            }
            for (var c = min; c <= max && c > 0; c++)
            {
                if (width is { } w)
                    sheet.ColumnWidths[c - 1] = w;
                if (hidden)
                    sheet.HiddenColumns.Add(c - 1);
            }
        }

        var nextRow = 0;
        foreach (var row in xml.Element(S + "sheetData")?.Elements(S + "row") ?? [])
        {
            var r = int.TryParse((string?)row.Attribute("r"), out var number) ? number - 1 : nextRow;
            if (r < 0 || r > SheetPagination.MaxRow)
                continue;
            nextRow = r + 1;
            if (On(row.Attribute("hidden")))
                sheet.HiddenRows.Add(r);
            if (Number(row.Attribute("ht")) is { } height and >= 0 and <= 409.5)
            {
                if (On(row.Attribute("customHeight")))
                    sheet.RowHeights[r] = height;
                else
                    sheet.AutoRowHeights[r] = height;
            }

            var nextColumn = 0;
            foreach (var cell in row.Elements(S + "c"))
            {
                var c = CellRange.TryParseCell((string?)cell.Attribute("r") ?? "", out _, out var column) ? column : nextColumn;
                if (c < 0 || c > SheetPagination.MaxColumn)
                    continue;
                nextColumn = c + 1;
                var (style, numberFormat) = Index(cell, "s") is var s && s < _cellFormats.Count ? _cellFormats[s] : (CellStyle.Default, "General");
                var value = Value(cell, style, numberFormat);
                if (value.HasContent)
                    sheet.Set(r, c, value);
            }
        }

        foreach (var merge in xml.Element(S + "mergeCells")?.Elements(S + "mergeCell") ?? [])
        {
            if (CellRange.Parse((string?)merge.Attribute("ref")) is { } range && (range.Bottom > range.Top || range.Right > range.Left)
                && range.Bottom <= SheetPagination.MaxRow && range.Right <= SheetPagination.MaxColumn)
                sheet.Merges.Add(range);
        }

        // <brk id="20"/>: the break is above 0-based row 20 (left of column 20), which starts the next page.
        foreach (var brk in xml.Element(S + "rowBreaks")?.Elements(S + "brk") ?? [])
        {
            if (int.TryParse((string?)brk.Attribute("id"), out var breakRow) && breakRow > 0 && breakRow <= SheetPagination.MaxRow && (string?)brk.Attribute("man") != "0")
                sheet.RowBreaks.Add(breakRow);
        }
        foreach (var brk in xml.Element(S + "colBreaks")?.Elements(S + "brk") ?? [])
        {
            if (int.TryParse((string?)brk.Attribute("id"), out var breakColumn) && breakColumn > 0 && breakColumn <= SheetPagination.MaxColumn && (string?)brk.Attribute("man") != "0")
                sheet.ColumnBreaks.Add(breakColumn);
        }

        sheet.PageSetup = PageSetup(xml, partPath);
        foreach (var formatting in xml.Elements(S + "conditionalFormatting"))
            ConditionalFormats(formatting, sheet);
        if (_package.Target(partPath, (string?)xml.Element(S + "drawing")?.Attribute(R + "id")) is { } drawing)
            Pictures(drawing, sheet);
        return sheet;
    }

    // ───────────────────────── Page setup ─────────────────────────

    private SheetPageSetup PageSetup(XElement xml, string partPath)
    {
        var page = xml.Element(S + "pageSetup");
        var setup = page is null ? SheetPageSetup.ForRegion() : new SheetPageSetup { PaperWidth = Sheets.Paper.Letter.Width, PaperHeight = Sheets.Paper.Letter.Height };
        if (page is not null)
        {
            if (Number(page.Attribute("paperSize")) is { } code && Sheets.Paper.FromCode((int)code) is { } paper)
                setup = setup with { PaperWidth = paper.Width, PaperHeight = paper.Height };
            if (Sheets.Paper.Length((string?)page.Attribute("paperWidth")) is { } paperWidth && Sheets.Paper.Length((string?)page.Attribute("paperHeight")) is { } paperHeight)
                setup = setup with { PaperWidth = paperWidth, PaperHeight = paperHeight };
            setup = setup with
            {
                Landscape = (string?)page.Attribute("orientation") == "landscape",
                Scale = Number(page.Attribute("scale")) is { } scale and >= 10 and <= 400 ? (int)scale : 100,
                FitWidth = Number(page.Attribute("fitToWidth")) is { } across and >= 0 and <= 32767 ? (int)across : 1,
                FitHeight = Number(page.Attribute("fitToHeight")) is { } down and >= 0 and <= 32767 ? (int)down : 1,
                OverThenDown = (string?)page.Attribute("pageOrder") == "overThenDown",
                FirstPageNumber = On(page.Attribute("useFirstPageNumber")) && Number(page.Attribute("firstPageNumber")) is { } first and >= -32768 and <= 32767 ? (int)first : null,
            };
        }
        setup = setup with { FitToPage = On(xml.Element(S + "sheetPr")?.Element(S + "pageSetUpPr")?.Attribute("fitToPage")) };
        if (xml.Element(S + "pageMargins") is { } margins)
        {
            double Margin(string name, double fallback) => Number(margins.Attribute(name)) is { } inches and >= 0 and <= 50 ? inches * 72 : fallback;
            setup = setup with
            {
                LeftMargin = Margin("left", setup.LeftMargin),
                RightMargin = Margin("right", setup.RightMargin),
                TopMargin = Margin("top", setup.TopMargin),
                BottomMargin = Margin("bottom", setup.BottomMargin),
                HeaderMargin = Margin("header", setup.HeaderMargin),
                FooterMargin = Margin("footer", setup.FooterMargin),
            };
        }
        if (xml.Element(S + "printOptions") is { } options)
        {
            setup = setup with
            {
                PrintGridLines = On(options.Attribute("gridLines")),
                PrintHeadings = On(options.Attribute("headings")),
                CenterHorizontally = On(options.Attribute("horizontalCentered")),
                CenterVertically = On(options.Attribute("verticalCentered")),
            };
        }
        if (xml.Element(S + "headerFooter") is { } hf)
        {
            string? Text(string element) => hf.Element(S + element)?.Value;
            setup = setup with
            {
                HeaderFooter = new SheetHeaderFooter
                {
                    OddHeader = Text("oddHeader"),
                    OddFooter = Text("oddFooter"),
                    EvenHeader = Text("evenHeader"),
                    EvenFooter = Text("evenFooter"),
                    FirstHeader = Text("firstHeader"),
                    FirstFooter = Text("firstFooter"),
                    DifferentOddEven = On(hf.Attribute("differentOddEven")),
                    DifferentFirst = On(hf.Attribute("differentFirst")),
                    ScaleWithDocument = hf.Attribute("scaleWithDoc") is not { } scaleWith || On(scaleWith),
                    AlignWithMargins = hf.Attribute("alignWithMargins") is not { } align || On(align),
                    Pictures = _package.Target(partPath, (string?)xml.Element(S + "legacyDrawingHF")?.Attribute(R + "id")) is { } vml ? HeaderPictures(vml) : [],
                },
            };
        }
        return setup;
    }

    /// <summary>The pictures of headers and footers: VML shapes named LH, CH, RH, LF, CF, RF (+ FIRST / EVEN).</summary>
    private Dictionary<string, (byte[], double, double)> HeaderPictures(string vmlPath)
    {
        var pictures = new Dictionary<string, (byte[], double, double)>(StringComparer.OrdinalIgnoreCase);
        XDocument? vml;
        try
        {
            vml = _package.Part(vmlPath);
        }
        catch (InvalidDataException)
        {
            return pictures; // VML that isn't well-formed XML: the header keeps its text
        }
        foreach (var shape in vml?.Root?.Descendants(V + "shape") ?? [])
        {
            var id = (string?)shape.Attribute("id");
            var relation = (string?)shape.Element(V + "imagedata")?.Attribute(O + "relid") ?? (string?)shape.Element(V + "imagedata")?.Attribute(R + "id");
            if (id is null || _package.Target(vmlPath, relation) is not { } image || _package.Bytes(image, MaxPictureBytes) is not { } data)
                continue;
            var style = ((string?)shape.Attribute("style") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split(':', 2)).Where(p => p.Length == 2).GroupBy(p => p[0].Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First()[1].Trim(), StringComparer.OrdinalIgnoreCase);
            pictures[id] = (data, Css(style.GetValueOrDefault("width")) ?? 0, Css(style.GetValueOrDefault("height")) ?? 0);
        }
        return pictures;
    }

    /// <summary>A VML style length ("100pt", "1.5in", "40px") in points.</summary>
    private static double? Css(string? length)
    {
        if (length is null)
            return null;
        var points = OdsReader.Points(length);
        return points is > 0 and < 10_000 ? points : null;
    }

    // ───────────────────────── Conditional formats ─────────────────────────

    private void ConditionalFormats(XElement formatting, Worksheet sheet)
    {
        var ranges = ((string?)formatting.Attribute("sqref") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(CellRange.Parse).OfType<CellRange>().ToList();
        if (ranges.Count == 0)
            return;
        foreach (var rule in formatting.Elements(S + "cfRule"))
        {
            var type = (string?)rule.Attribute("type");
            if (type is null or "expression" or "dataBar" or "iconSet" or "timePeriod")
                continue;
            var style = Index(rule, "dxfId") is var dxf && dxf < _dxfs.Count && rule.Attribute("dxfId") is not null ? _dxfs[dxf] : null;
            var scale = new List<ScalePoint>();
            if (rule.Element(S + "colorScale") is { } colorScale)
            {
                var points = colorScale.Elements(S + "cfvo").ToList();
                var colors = colorScale.Elements(S + "color").Select(c => _colors.Spreadsheet(c) ?? "#FFFFFF").ToList();
                for (var i = 0; i < Math.Min(points.Count, colors.Count); i++)
                    scale.Add(new ScalePoint((string?)points[i].Attribute("type") ?? "num", Number(points[i].Attribute("val")) ?? 0, colors[i]));
            }
            sheet.ConditionalFormats.Add(new ConditionalFormat(ranges, type, (int)(Number(rule.Attribute("priority")) ?? int.MaxValue))
            {
                Operator = (string?)rule.Attribute("operator"),
                Formulas = [.. rule.Elements(S + "formula").Select(f => f.Value).Take(2)],
                Text = (string?)rule.Attribute("text"),
                Rank = (int)(Number(rule.Attribute("rank")) ?? 10),
                Percent = On(rule.Attribute("percent")),
                Bottom = On(rule.Attribute("bottom")),
                AboveAverage = rule.Attribute("aboveAverage") is not { } above || On(above),
                EqualAverage = On(rule.Attribute("equalAverage")),
                StdDev = (int)Math.Clamp(Number(rule.Attribute("stdDev")) ?? 0, 0, 3),
                StopIfTrue = On(rule.Attribute("stopIfTrue")),
                Style = style,
                Scale = scale,
            });
        }
    }

    // ───────────────────────── Pictures ─────────────────────────

    private void Pictures(string drawingPath, Worksheet sheet)
    {
        if (_package.Part(drawingPath)?.Root is not { } drawing)
            return;
        foreach (var anchor in drawing.Elements().Where(e => e.Name.Namespace == Xdr))
        {
            var embed = anchor.Descendants(Xdr + "pic").Select(p => (string?)p.Element(Xdr + "blipFill")?.Element(A + "blip")?.Attribute(R + "embed")).FirstOrDefault(e => e is not null);
            if (_package.Target(drawingPath, embed) is not { } image || _package.Bytes(image, MaxPictureBytes) is not { } data)
                continue;
            static (int Row, int Column, double X, double Y)? Marker(XElement? marker) => marker is null ? null : (
                (int)Math.Clamp(Long(marker.Element(Xdr + "row")), 0, SheetPagination.MaxRow),
                (int)Math.Clamp(Long(marker.Element(Xdr + "col")), 0, SheetPagination.MaxColumn),
                Long(marker.Element(Xdr + "colOff")) * PointsPerEmu,
                Long(marker.Element(Xdr + "rowOff")) * PointsPerEmu);
            var extent = anchor.Element(Xdr + "ext");
            var (width, height) = (Long(extent?.Attribute("cx")) * PointsPerEmu, Long(extent?.Attribute("cy")) * PointsPerEmu);
            SheetPicture? picture = anchor.Name.LocalName switch
            {
                "twoCellAnchor" when Marker(anchor.Element(Xdr + "from")) is { } from && Marker(anchor.Element(Xdr + "to")) is { } to =>
                    new SheetPicture(data, from.Row, from.Column, from.X, from.Y, 0, 0) { To = (to.Row, to.Column, to.X, to.Y) },
                "oneCellAnchor" when Marker(anchor.Element(Xdr + "from")) is { } from => new SheetPicture(data, from.Row, from.Column, from.X, from.Y, width, height),
                "absoluteAnchor" => new SheetPicture(data, 0, 0, Long(anchor.Element(Xdr + "pos")?.Attribute("x")) * PointsPerEmu,
                    Long(anchor.Element(Xdr + "pos")?.Attribute("y")) * PointsPerEmu, width, height),
                _ => null,
            };
            if (picture is not null)
                sheet.Pictures.Add(picture);
        }
    }

    private static long Long(XObject? node)
    {
        var text = node switch { XElement e => e.Value, XAttribute a => a.Value, _ => null };
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? Math.Clamp(value, 0, 50_000_000_000) : 0;
    }

    // ───────────────────────── Cells ─────────────────────────

    /// <summary>
    /// The displayed text of a cell, and for numbers (dates included) and booleans the value behind it. Dates in a
    /// 1904-based workbook are moved to the 1900 date system, so the value means the same in every workbook.
    /// </summary>
    private SheetCell Value(XElement cell, CellStyle style, string format)
    {
        var type = (string?)cell.Attribute("t") ?? "n";
        var raw = cell.Element(S + "v")?.Value;
        switch (type)
        {
            case "s":
                if (int.TryParse(raw, out var index) && index >= 0 && index < _sharedStrings.Count)
                    return new SheetCell(_sharedStrings[index].Text, style) { Runs = _sharedStrings[index].Runs };
                return new SheetCell("", style);
            case "inlineStr":
                if (cell.Element(S + "is") is { } inline)
                {
                    var (text, runs) = RichText(inline);
                    return new SheetCell(text, style) { Runs = runs };
                }
                return new SheetCell(raw ?? "", style);
            case "str":
                return new SheetCell(raw ?? "", style);
            case "e":
                return new SheetCell(raw ?? "", style) { IsError = true };
            case "b":
                var on = raw is "1" or "true";
                return new SheetCell(on ? "TRUE" : "FALSE", style) { Value = on ? 1 : 0, IsBoolean = true };
            case "d":
                return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    ? Number(NumberFormatter.ToSerial(date) - (_date1904 ? Date1904Offset : 0), style, format)
                    : new SheetCell(raw ?? "", style);
            default:
                return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
                    ? Number(number, style, format)
                    : new SheetCell(raw ?? "", style);
        }
    }

    /// <param name="value">The stored number, in the workbook's own date system.</param>
    private SheetCell Number(double value, CellStyle style, string format) => new(_formatter.Format(value, format), style, IsNumber: true)
    {
        Value = _date1904 && _formatter.IsDate(format) ? value + Date1904Offset : value,
        NumberFormat = NumberFormatter.IsGeneral(format) ? null : format,
    };
}
