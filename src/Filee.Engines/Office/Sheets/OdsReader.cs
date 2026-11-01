// ODS (OpenDocument spreadsheet, LibreOffice Calc) → Workbook, read directly from content.xml, styles.xml and
// settings.xml without LibreOffice: sheets, repeated rows and columns (the huge empty repeats that end every sheet
// are skipped, not expanded), merged cells, values with the text Calc shows (the cached text:p), number formats as
// Excel codes (OdsNumberStyles), cell styles (font, colour, fill, borders, alignment, wrapping), column widths, row
// heights, hidden rows / columns / sheets and frozen rows, and for printing each sheet's page style (paper, margins,
// scale, page order, centring, grid and headings, headers and footers with their fields), print ranges, repeated
// rows and columns, manual page breaks, rotation, indent and shrink to fit. Charts, images, comments and
// conditional formats are not read.

using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Filee.Engines.Documents;

namespace Filee.Engines.Office.Sheets;

internal sealed class OdsReader
{
    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private static readonly XNamespace Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace Config = "urn:oasis:names:tc:opendocument:xmlns:config:1.0";
    private static readonly XNamespace Manifest = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";
    private static readonly XNamespace Loext = "urn:org:documentfoundation:names:experimental:office:xmlns:loext:1.0";
    private static readonly XNamespace Style = OdsNumberStyles.Style;
    private static readonly XNamespace Fo = OdsNumberStyles.Fo;
    private static readonly XNamespace Number = OdsNumberStyles.Number;

    /// <summary>Excel's sheet size; ODS sheets repeat their last empty row and column up to (at least) it.</summary>
    private const int MaxRows = 1_048_576;
    private const int MaxColumns = 16_384;

    /// <summary>
    /// Empty but formatted cells repeated more often than this are the formatted rest of a row or sheet (a coloured
    /// column, a bordered page), not content worth a table cell each.
    /// </summary>
    private const int MaxStyledRepeat = 256;

    /// <summary>Points per character of column width (7 pixels of Calibri 11 at 96 dpi), as in the XLSX writer.</summary>
    internal const double PointsPerCharacter = 5.25;

    private readonly Dictionary<(string Family, string Name), XElement> _styles = [];
    private readonly Dictionary<string, XElement> _dataStyles = [];
    private readonly Dictionary<string, (CellStyle Style, string? DataStyle)> _cellStyles = [];
    private readonly Dictionary<string, string?> _formats = [];
    private readonly Dictionary<string, int> _frozenRows = [];
    private readonly NumberFormatter _formatter = new();
    private readonly Dictionary<string, XElement> _masterPages = [];

    /// <summary>Cells the repeats of all tables expand to at most.</summary>
    private const long MaxCells = 20_000_000;

    private long _cells;
    private readonly Dictionary<string, XElement> _pageLayouts = [];
    private XElement? _defaultCellStyle;

    /// <summary>Reads every visible sheet of <paramref name="path"/>.</summary>
    public static Workbook Read(string path)
    {
        using var zip = DocumentPackage.Open(path, DocumentLimits.Default,
            () => new InvalidDataException("The file is not an OpenDocument spreadsheet (no ZIP). It may be damaged."));
        if (IsEncrypted(zip))
            throw new InvalidDataException("The spreadsheet is protected with a password (encrypted). Remove the password in LibreOffice and convert it again.");
        var content = Load(zip, "content.xml") ?? throw new InvalidDataException("The file is not an OpenDocument spreadsheet (no content.xml).");
        var spreadsheet = content.Root?.Element(Office + "body")?.Element(Office + "spreadsheet")
                          ?? throw new InvalidDataException("The file is not an OpenDocument spreadsheet (no office:spreadsheet).");

        var reader = new OdsReader();
        reader.ReadStyles(Load(zip, "styles.xml"), content);
        reader.ReadSettings(Load(zip, "settings.xml"));
        var book = new Workbook();
        if (reader._defaultCellStyle?.Element(Style + "text-properties") is { } font)
        {
            if (((string?)font.Attribute(Style + "font-name") ?? (string?)font.Attribute(Fo + "font-family")) is { Length: > 0 } name)
                book.DefaultFontName = name.Trim('\'', '"');
            if (Points((string?)font.Attribute(Fo + "font-size")) is { } size and > 0 and <= 409)
                book.DefaultFontSize = size;
        }
        foreach (var table in spreadsheet.Elements(Table + "table"))
        {
            var tableStyle = reader.Find("table", (string?)table.Attribute(Table + "style-name"));
            if ((string?)tableStyle?.Element(Style + "table-properties")?.Attribute(Table + "display") == "false")
                continue;
            var sheet = reader.ReadTable(table);
            sheet.PageSetup = reader.PageSetup((string?)tableStyle?.Attribute(Style + "master-page-name") ?? "Default");
            sheet.PrintArea.AddRange(PrintRanges((string?)table.Attribute(Table + "print-ranges")));
            book.Sheets.Add(sheet);
        }
        return book;
    }

    /// <summary>A part as XML within the document limits; a part that isn't well-formed XML is damage.</summary>
    private static XDocument? Load(DocumentPackage zip, string name)
    {
        if (zip.GetEntry(name) is not { } entry)
            return null;
        try
        {
            // Whitespace is meaningful inside paragraphs (a space between two spans).
            return zip.LoadXml(entry, preserveWhitespace: true);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"The part {name} of the spreadsheet is not well-formed XML ({ex.Message}).", ex);
        }
    }

    /// <summary>Encrypted packages keep a readable manifest that lists how each part is encrypted.</summary>
    private static bool IsEncrypted(DocumentPackage zip) =>
        Load(zip, "META-INF/manifest.xml")?.Descendants(Manifest + "encryption-data").Any() == true;

    // ───────────────────────── Styles ─────────────────────────

    private void ReadStyles(XDocument? styles, XDocument content)
    {
        var containers = new[]
        {
            styles?.Root?.Element(Office + "styles"),
            styles?.Root?.Element(Office + "automatic-styles"),
            content.Root?.Element(Office + "automatic-styles"),
        };
        foreach (var page in styles?.Root?.Element(Office + "master-styles")?.Elements(Style + "master-page") ?? [])
        {
            if ((string?)page.Attribute(Style + "name") is { } pageName)
                _masterPages[pageName] = page;
        }
        foreach (var element in containers.Where(c => c is not null).SelectMany(c => c!.Elements()))
        {
            var name = (string?)element.Attribute(Style + "name");
            if (element.Name == Style + "page-layout" && name is not null)
                _pageLayouts[name] = element;
            if (element.Name == Style + "default-style" && (string?)element.Attribute(Style + "family") == "table-cell")
                _defaultCellStyle = element;
            else if (element.Name == Style + "style" && name is not null)
                _styles[((string?)element.Attribute(Style + "family") ?? "", name)] = element;
            else if (element.Name.Namespace == Number && name is not null)
                _dataStyles[name] = element;
        }
    }

    private XElement? Find(string family, string? name) =>
        name is not null && _styles.TryGetValue((family, name), out var style) ? style : null;

    /// <summary>A cell style with its parents and the default style applied, and its data style name.</summary>
    private (CellStyle Style, string? DataStyle) CellStyleOf(string? name)
    {
        name ??= "Default";
        if (_cellStyles.TryGetValue(name, out var cached))
            return cached;

        var chain = new List<XElement>();
        for (var style = Find("table-cell", name); style is not null && chain.Count < 20; style = Find("table-cell", (string?)style.Attribute(Style + "parent-style-name")))
            chain.Insert(0, style);
        if (_defaultCellStyle is not null)
            chain.Insert(0, _defaultCellStyle);

        var props = new StyleProperties();
        string? dataStyle = null;
        foreach (var style in chain)
        {
            props.Apply(style);
            dataStyle = (string?)style.Attribute(Style + "data-style-name") ?? dataStyle;
        }
        return _cellStyles[name] = (props.ToCellStyle(), dataStyle);
    }

    /// <summary>The Excel format code of a data style (cached), null when there is none.</summary>
    private string? FormatOf(string? dataStyle)
    {
        if (dataStyle is null)
            return null;
        if (!_formats.TryGetValue(dataStyle, out var format))
        {
            _formats[dataStyle] = format = _dataStyles.TryGetValue(dataStyle, out var style)
                ? OdsNumberStyles.ToExcel(style, name => _dataStyles.GetValueOrDefault(name))
                : null;
        }
        return format;
    }

    // ───────────────────────── Page styles ─────────────────────────

    /// <summary>
    /// A master page's page layout as Excel's page setup. Calc's top and bottom margins end where the header and
    /// footer start; Excel's go to the cells, with the header margin between: the header's height (fo:min-height,
    /// which holds its spacing to the cells) is added to them, as LibreOffice does when it saves XLSX. An XLSX with
    /// a 1 in top margin and a 0.5 in header margin comes back from Calc as 1.27 cm + a 1.27 cm header.
    /// </summary>
    private SheetPageSetup PageSetup(string masterName)
    {
        if (!_masterPages.TryGetValue(masterName, out var master)
            || (string?)master.Attribute(Style + "page-layout-name") is not { } layoutName || !_pageLayouts.TryGetValue(layoutName, out var layout))
            return new SheetPageSetup();
        var page = layout.Element(Style + "page-layout-properties");
        double Length(XElement? element, string name, double fallback) => Points((string?)element?.Attribute(Fo + name)) is { } points and >= 0 and < 14_400 ? points : fallback;
        var setup = new SheetPageSetup();
        if (Points((string?)page?.Attribute(Fo + "page-width")) is { } width and >= 36 and <= 14_400 && Points((string?)page?.Attribute(Fo + "page-height")) is { } height and >= 36 and <= 14_400)
            setup = setup with { PaperWidth = width, PaperHeight = height };
        var print = ((string?)page?.Attribute(Style + "print") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var centering = (string?)page?.Attribute(Style + "table-centering");
        var scaleTo = (string?)page?.Attribute(Style + "scale-to");
        var pages = int.TryParse((string?)page?.Attribute(Style + "scale-to-pages"), out var n) && n > 0 ? n : 0;
        var across = int.TryParse((string?)(page?.Attribute(Style + "scale-to-X") ?? page?.Attribute(Loext + "scale-to-X")), out var x) && x >= 0 ? x : -1;
        var down = int.TryParse((string?)(page?.Attribute(Style + "scale-to-Y") ?? page?.Attribute(Loext + "scale-to-Y")), out var y) && y >= 0 ? y : -1;
        setup = setup with
        {
            Landscape = (string?)page?.Attribute(Style + "print-orientation") == "landscape",
            LeftMargin = Length(page, "margin-left", setup.LeftMargin),
            RightMargin = Length(page, "margin-right", setup.RightMargin),
            TopMargin = Length(page, "margin-top", setup.TopMargin),
            BottomMargin = Length(page, "margin-bottom", setup.BottomMargin),
            PrintGridLines = print.Contains("grid"),
            PrintHeadings = print.Contains("headers"),
            OverThenDown = (string?)page?.Attribute(Style + "print-page-order") == "ltr",
            CenterHorizontally = centering is "horizontal" or "both",
            CenterVertically = centering is "vertical" or "both",
            Scale = scaleTo is { Length: > 1 } percent && percent.EndsWith('%') && double.TryParse(percent[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) && scale is >= 10 and <= 400 ? (int)Math.Round(scale) : 100,
            FitToPage = pages > 0 || across >= 0 || down >= 0,
            FitWidth = across >= 0 ? across : pages > 0 ? pages : 1,
            FitHeight = down >= 0 ? down : pages > 0 ? pages : 1,
            FirstPageNumber = int.TryParse((string?)page?.Attribute(Style + "first-page-number"), out var first) && first is > 0 and < 32768 ? first : null,
        };
        // A width and height of 0 both with scale-to-pages: Calc's "fit width / height" with 0 = as many as needed.
        if (setup.FitToPage && pages > 0 && across < 0 && down < 0)
            setup = setup with { FitWidth = 0, FitHeight = pages };

        string? Region(XElement? element)
        {
            if (element is null || (string?)element.Attribute(Style + "display") == "false")
                return null;
            var left = element.Element(Style + "region-left");
            var center = element.Element(Style + "region-center");
            var right = element.Element(Style + "region-right");
            if (left is null && center is null && right is null)
                return HeaderFooterText.Code(null, HeaderCode(element), null);
            return HeaderFooterText.Code(HeaderCode(left), HeaderCode(center), HeaderCode(right));
        }
        var header = Region(master.Element(Style + "header"));
        var footer = Region(master.Element(Style + "footer"));
        var headerStyle = layout.Element(Style + "header-style")?.Element(Style + "header-footer-properties");
        var footerStyle = layout.Element(Style + "footer-style")?.Element(Style + "header-footer-properties");
        if (header is not null)
        {
            var extent = Math.Max(Length(headerStyle, "min-height", 0), Length(headerStyle, "margin-bottom", 0));
            setup = setup with { HeaderMargin = setup.TopMargin, TopMargin = setup.TopMargin + extent };
        }
        if (footer is not null)
        {
            var extent = Math.Max(Length(footerStyle, "min-height", 0), Length(footerStyle, "margin-top", 0));
            setup = setup with { FooterMargin = setup.BottomMargin, BottomMargin = setup.BottomMargin + extent };
        }
        var evenHeader = Region(master.Element(Style + "header-left"));
        var evenFooter = Region(master.Element(Style + "footer-left"));
        var firstHeader = Region(master.Element(Loext + "header-first") ?? master.Element(Style + "header-first"));
        var firstFooter = Region(master.Element(Loext + "footer-first") ?? master.Element(Style + "footer-first"));
        return setup with
        {
            HeaderFooter = new SheetHeaderFooter
            {
                OddHeader = header,
                OddFooter = footer,
                EvenHeader = evenHeader ?? header,
                EvenFooter = evenFooter ?? footer,
                FirstHeader = firstHeader ?? header,
                FirstFooter = firstFooter ?? footer,
                DifferentOddEven = evenHeader is not null || evenFooter is not null,
                DifferentFirst = firstHeader is not null || firstFooter is not null,
                ScaleWithDocument = false,
            },
        };
    }

    /// <summary>The paragraphs of a header region as Excel codes: text (ampersands doubled) and the fields.</summary>
    private static string? HeaderCode(XElement? region)
    {
        if (region is null)
            return null;
        var sb = new StringBuilder();
        var paragraphs = region.Elements(Text + "p").ToList();
        for (var i = 0; i < paragraphs.Count; i++)
        {
            if (i > 0)
                sb.Append('\n');
            HeaderInline(paragraphs[i], sb);
        }
        return sb.ToString();
    }

    private static void HeaderInline(XElement element, StringBuilder sb)
    {
        foreach (var node in element.Nodes())
        {
            if (node is XText text)
                sb.Append(text.Value.Replace("&", "&&", StringComparison.Ordinal));
            else if (node is XElement child)
            {
                var code = child.Name.LocalName switch
                {
                    "page-number" => "&P",
                    "page-count" => "&N",
                    "sheet-name" => "&A",
                    "file-name" => (string?)child.Attribute(Text + "display") == "full" ? "&Z&F" : "&F",
                    "title" => "&F",
                    "date" => "&D",
                    "time" => "&T",
                    "s" => new string(' ', Repeat(child, Text + "c")),
                    "line-break" => "\n",
                    _ => null,
                };
                if (code is not null && child.Name.Namespace == Text)
                    sb.Append(code);
                else
                    HeaderInline(child, sb);
            }
        }
    }

    /// <summary>
    /// The ranges of table:print-ranges ("Sheet1.A1:Sheet1.D20 $'My sheet'.$F$1:.$G$5"); the sheet names are left out.
    /// </summary>
    internal static List<CellRange> PrintRanges(string? value)
    {
        var ranges = new List<CellRange>();
        if (string.IsNullOrWhiteSpace(value))
            return ranges;
        foreach (var part in SplitOutsideQuotes(value, ' '))
        {
            var ends = SplitOutsideQuotes(part, ':').Select(e => e[(e.LastIndexOf('.') + 1)..].Replace("$", "", StringComparison.Ordinal)).ToList();
            if (ends.Count is 1 or 2 && CellRange.Parse(string.Join(':', ends)) is { } range && range.Bottom <= SheetPagination.MaxRow && range.Right <= SheetPagination.MaxColumn)
                ranges.Add(range);
        }
        return ranges;
    }

    private static List<string> SplitOutsideQuotes(string value, char separator)
    {
        var parts = new List<string>();
        var quoted = false;
        var start = 0;
        for (var i = 0; i <= value.Length; i++)
        {
            if (i < value.Length && value[i] == '\'')
                quoted = !quoted;
            if (i == value.Length || value[i] == separator && !quoted)
            {
                if (i > start)
                    parts.Add(value[start..i]);
                start = i + 1;
            }
        }
        return parts;
    }

    /// <summary>Formatting properties of one style; unset ones inherit from the parent style.</summary>
    private sealed class StyleProperties
    {
        private bool? _bold, _italic, _underline, _strike, _wrap, _shrink, _stacked;
        private double? _size, _rotation, _indent;
        private string? _color, _fill, _textAlign, _alignSource, _verticalAlign, _font;
        private CellBorder? _left, _right, _top, _bottom, _diagonalUp, _diagonalDown;

        public void Apply(XElement style)
        {
            if (style.Element(Style + "text-properties") is { } text)
            {
                if ((Attr(text, Fo + "font-weight") ?? Attr(text, Style + "font-weight-asian")) is { } weight)
                    _bold = weight == "bold" || int.TryParse(weight, out var w) && w >= 600;
                if ((Attr(text, Fo + "font-style") ?? Attr(text, Style + "font-style-asian")) is { } italic)
                    _italic = italic is "italic" or "oblique";
                if (Attr(text, Style + "text-underline-style") is { } underline)
                    _underline = underline != "none";
                if (Attr(text, Style + "text-line-through-style") is { } strike)
                    _strike = strike != "none";
                if (Points(Attr(text, Fo + "font-size")) is { } size)
                    _size = size;
                if (Color(Attr(text, Fo + "color")) is { } color)
                    _color = color;
                if ((Attr(text, Style + "font-name") ?? Attr(text, Fo + "font-family")) is { Length: > 0 } font)
                    _font = font.Trim('\'', '"');
            }
            if (style.Element(Style + "table-cell-properties") is { } cell)
            {
                if (Attr(cell, Fo + "background-color") is { } fill)
                    _fill = fill == "transparent" ? null : Color(fill);
                if (Attr(cell, Fo + "border") is { } all)
                    _left = _right = _top = _bottom = Border(all);
                _left = Border(Attr(cell, Fo + "border-left")) ?? _left;
                _right = Border(Attr(cell, Fo + "border-right")) ?? _right;
                _top = Border(Attr(cell, Fo + "border-top")) ?? _top;
                _bottom = Border(Attr(cell, Fo + "border-bottom")) ?? _bottom;
                _verticalAlign = Attr(cell, Style + "vertical-align") ?? _verticalAlign;
                if (Attr(cell, Fo + "wrap-option") is { } wrap)
                    _wrap = wrap == "wrap";
                _alignSource = Attr(cell, Style + "text-align-source") ?? _alignSource;
                if (Attr(cell, Style + "shrink-to-fit") is { } shrink)
                    _shrink = shrink == "true";
                if (double.TryParse(Attr(cell, Style + "rotation-angle")?.TrimEnd('d', 'e', 'g'), NumberStyles.Float, CultureInfo.InvariantCulture, out var angle))
                    _rotation = angle;
                if (Attr(cell, Style + "direction") is { } direction)
                    _stacked = direction == "ttb";
                _diagonalDown = Border(Attr(cell, Style + "diagonal-tl-br")) ?? _diagonalDown;
                _diagonalUp = Border(Attr(cell, Style + "diagonal-bl-tr")) ?? _diagonalUp;
            }
            if (Attr(style.Element(Style + "paragraph-properties"), Fo + "text-align") is { } align)
                _textAlign = align;
            if (Points(Attr(style.Element(Style + "paragraph-properties"), Fo + "margin-left")) is { } margin)
                _indent = margin;
        }

        public CellStyle ToCellStyle() => new()
        {
            Bold = _bold ?? false,
            Italic = _italic ?? false,
            Underline = _underline ?? false,
            Strike = _strike ?? false,
            FontSize = _size,
            Color = _color,
            Fill = _fill,
            // "value-type" = Calc's automatic alignment (numbers right, text left), like Excel's General.
            Align = _alignSource == "value-type" ? CellAlign.General : _textAlign switch
            {
                "start" or "left" or "justify" => CellAlign.Left,
                "center" => CellAlign.Center,
                "end" or "right" => CellAlign.Right,
                _ => CellAlign.General,
            },
            VerticalAlign = _verticalAlign switch
            {
                "top" => CellVerticalAlign.Top,
                "middle" => CellVerticalAlign.Center,
                _ => CellVerticalAlign.Bottom,
            },
            Wrap = _wrap ?? false,
            Shrink = _shrink ?? false,
            FontName = _font,
            SpecialAlign = _textAlign == "justify" ? CellSpecialAlign.Justify : CellSpecialAlign.None,
            // Excel's indent level is three spaces (about 7.5 pt of Calibri 11).
            Indent = _indent is > 0 and < 2000 ? (int)Math.Round(_indent.Value / 7.5) : 0,
            Rotation = _stacked == true ? CellStyle.Stacked : Rotation(_rotation),
            DiagonalUp = _diagonalUp ?? CellBorder.None,
            DiagonalDown = _diagonalDown ?? CellBorder.None,
            Left = _left ?? CellBorder.None,
            Right = _right ?? CellBorder.None,
            Top = _top ?? CellBorder.None,
            Bottom = _bottom ?? CellBorder.None,
        };

        /// <summary>Calc's angle (0–360, counter-clockwise) as Excel's -90 to 90.</summary>
        private static int Rotation(double? angle)
        {
            if (angle is not { } a || !double.IsFinite(a))
                return 0;
            var degrees = (int)Math.Round(((a % 360) + 360) % 360);
            return degrees switch
            {
                <= 90 => degrees,
                >= 270 => degrees - 360,
                < 180 => degrees - 180,
                _ => degrees - 180,
            };
        }

        private static string? Attr(XElement? element, XName name) => (string?)element?.Attribute(name);

        /// <summary>"0.75pt solid #000000" → the Excel border style of that weight and pattern.</summary>
        private static CellBorder? Border(string? value)
        {
            if (value is null)
                return null;
            var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var pattern = parts.FirstOrDefault(p => p is "solid" or "dotted" or "dashed" or "dot-dash" or "dot-dot-dash" or "double" or "groove" or "ridge" or "inset" or "outset" or "fine-dashed" or "dash-dot" or "dash-dot-dot" or "none" or "hidden");
            if (pattern is null or "none" or "hidden")
                return CellBorder.None;
            var width = parts.Select(Points).FirstOrDefault(p => p is not null) ?? 0.75;
            var color = parts.Select(Color).FirstOrDefault(c => c is not null);
            var style = pattern switch
            {
                "double" => "double",
                "dotted" => "dotted",
                "dashed" or "fine-dashed" => width > 1.1 ? "mediumDashed" : "dashed",
                "dot-dash" or "dash-dot" => width > 1.1 ? "mediumDashDot" : "dashDot",
                "dot-dot-dash" or "dash-dot-dot" => width > 1.1 ? "mediumDashDotDot" : "dashDotDot",
                _ => width < 0.3 ? "hair" : width <= 1.1 ? "thin" : width <= 2.1 ? "medium" : "thick",
            };
            return new CellBorder(style, color);
        }
    }

    /// <summary>"#1f4e79" → "#1F4E79"; null for anything else.</summary>
    private static string? Color(string? value) =>
        value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(char.IsAsciiHexDigit) ? value.ToUpperInvariant() : null;

    /// <summary>An ODF length ("2.258cm", "0.889in", "15pt") in points; null for percentages and garbage.</summary>
    internal static double? Points(string? length)
    {
        if (string.IsNullOrEmpty(length))
            return null;
        var unit = length.TrimStart('-', '+', '.', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        if (!double.TryParse(length.AsSpan(0, length.Length - unit.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return null;
        return unit switch
        {
            "pt" => value,
            "in" => value * 72,
            "cm" => value * 72 / 2.54,
            "mm" => value * 72 / 25.4,
            "pc" => value * 12,
            "px" => value * 0.75,
            _ => null,
        };
    }

    // ───────────────────────── Settings ─────────────────────────

    /// <summary>Frozen rows per sheet (the view settings Calc saves in settings.xml).</summary>
    private void ReadSettings(XDocument? settings)
    {
        var tables = settings?.Descendants(Config + "config-item-map-named").FirstOrDefault(m => (string?)m.Attribute(Config + "name") == "Tables");
        foreach (var entry in tables?.Elements(Config + "config-item-map-entry") ?? [])
        {
            string? Item(string name) => (string?)entry.Elements(Config + "config-item").FirstOrDefault(i => (string?)i.Attribute(Config + "name") == name);
            if (Item("VerticalSplitMode") == "2" && int.TryParse(Item("VerticalSplitPosition"), out var rows) && rows > 0 && (string?)entry.Attribute(Config + "name") is { } sheet)
                _frozenRows[sheet] = rows;
        }
    }

    // ───────────────────────── Sheets ─────────────────────────

    private sealed record Span(int Start, int Count, double? Size, bool Hidden, string? DefaultStyle);

    private sealed record CellRun(int Column, int Count, SheetCell? Cell, int ColumnSpan, int RowSpan);

    private Worksheet ReadTable(XElement table)
    {
        var name = (string?)table.Attribute(Table + "name") ?? "Sheet";
        var sheet = new Worksheet(name) { WidthsInPoints = true };
        int? firstHeaderColumn = null, lastHeaderColumn = null;

        var columns = new List<Span>();
        var c = 0;
        foreach (var (column, header) in Children(table, "table-column", "table-header-columns", "table-column-group", "table-columns"))
        {
            if (c >= MaxColumns)
                break;
            var count = Math.Min(Repeat(column, "number-columns-repeated"), MaxColumns - c);
            var columnProperties = Find("table-column", (string?)column.Attribute(Table + "style-name"))?.Element(Style + "table-column-properties");
            var width = Points((string?)columnProperties?.Attribute(Style + "column-width"));
            if ((string?)columnProperties?.Attribute(Fo + "break-before") == "page" && c > 0)
                sheet.ColumnBreaks.Add(c);
            if (header)
            {
                firstHeaderColumn ??= c;
                lastHeaderColumn = c + count - 1;
            }
            columns.Add(new Span(c, count, width / PointsPerCharacter, IsCollapsed(column), (string?)column.Attribute(Table + "default-cell-style-name")));
            c += count;
        }

        var rows = new List<Span>();
        var autoHeights = new List<(int Start, int Count, double Height)>();
        var r = 0;
        var headerRows = 0;
        int? firstHeaderRow = null, lastHeaderRow = null;
        foreach (var (row, header) in Children(table, "table-row", "table-header-rows", "table-row-group", "table-rows"))
        {
            if (r >= MaxRows)
                break;
            var count = Math.Min(Repeat(row, "number-rows-repeated"), MaxRows - r);
            var properties = Find("table-row", (string?)row.Attribute(Table + "style-name"))?.Element(Style + "table-row-properties");
            // Calc saves every row's height; only rows not sized to their content have a custom one.
            var optimal = (string?)properties?.Attribute(Style + "use-optimal-row-height") == "true";
            var height = optimal ? null : Points((string?)properties?.Attribute(Style + "row-height"));
            if (optimal && Points((string?)properties?.Attribute(Style + "row-height")) is { } measured and > 0 and <= 409.5)
                autoHeights.Add((r, count, measured));
            if ((string?)properties?.Attribute(Fo + "break-before") == "page" && r > 0)
                sheet.RowBreaks.Add(r);
            rows.Add(new Span(r, count, height, IsCollapsed(row), null));
            if (header && headerRows == r)
                headerRows += count;
            if (header)
            {
                firstHeaderRow ??= r;
                lastHeaderRow = r + count - 1;
            }

            var cells = Cells(row, columns);
            var hasText = cells.Any(run => run.Cell is { Text.Length: > 0 } || run.ColumnSpan > 1 || run.RowSpan > 1);
            var copies = hasText ? count : cells.Any(run => run.Cell is not null) && count <= MaxStyledRepeat ? count : 0;
            // Repeats multiply: a row of text repeated a million times across every column is billions of cells.
            _cells += (long)copies * cells.Sum(run => (long)run.Count);
            if (_cells > MaxCells)
                throw DocumentLimits.Exceeded($"The spreadsheet repeats its cells more than {DocumentLimits.Count(MaxCells)} times.");
            for (var copy = 0; copy < copies; copy++)
            {
                foreach (var run in cells)
                {
                    for (var i = 0; i < run.Count; i++)
                    {
                        if (run.Cell is not null)
                            sheet.Set(r + copy, run.Column + i, run.Cell);
                        if (run.ColumnSpan > 1 || run.RowSpan > 1)
                            sheet.Merges.Add(new CellRange(r + copy, run.Column + i, r + copy + run.RowSpan - 1, run.Column + i + run.ColumnSpan - 1));
                    }
                }
            }
            r += count;
        }

        // Widths, heights and hidden flags only matter where the sheet has something; the repeats reach the
        // last row and column of the sheet.
        var used = sheet.UsedRange();
        var right = Math.Max(used?.Right ?? -1, sheet.Merges.Count > 0 ? sheet.Merges.Max(m => m.Right) : -1);
        var bottom = Math.Max(used?.Bottom ?? -1, sheet.Merges.Count > 0 ? sheet.Merges.Max(m => m.Bottom) : -1);
        foreach (var span in columns)
        {
            for (var i = span.Start; i < span.Start + span.Count && i <= right; i++)
            {
                if (span.Size is { } width)
                    sheet.ColumnWidths[i] = Math.Round(width, 4);
                if (span.Hidden)
                    sheet.HiddenColumns.Add(i);
            }
            if (span.Size is { } rest && span.Start <= right + 1 && right + 1 < span.Start + span.Count)
                sheet.DefaultColumnWidth = Math.Round(rest, 4);
        }
        foreach (var span in rows)
        {
            for (var i = span.Start; i < span.Start + span.Count && i <= bottom; i++)
            {
                if (span.Size is { } height)
                    sheet.RowHeights[i] = Math.Round(height, 2);
                if (span.Hidden)
                    sheet.HiddenRows.Add(i);
            }
        }
        foreach (var (start, count, height) in autoHeights)
        {
            for (var i = start; i < start + count && i <= bottom; i++)
                sheet.AutoRowHeights[i] = Math.Round(height, 2);
        }
        sheet.FrozenRows = _frozenRows.TryGetValue(name, out var frozen) ? frozen : headerRows;
        if (firstHeaderRow is { } firstRow && lastHeaderRow is { } lastRow)
            sheet.TitleRows = (firstRow, lastRow);
        if (firstHeaderColumn is { } firstColumn && lastHeaderColumn is { } lastColumn)
            sheet.TitleColumns = (firstColumn, lastColumn);
        return sheet;
    }

    /// <summary>Rows (or columns) of a table in order, also inside groups; the flag marks header rows.</summary>
    private static IEnumerable<(XElement Element, bool Header)> Children(XElement parent, string item, string header, params string[] groups)
    {
        foreach (var element in parent.Elements())
        {
            if (element.Name == Table + item)
                yield return (element, false);
            else if (element.Name == Table + header)
            {
                foreach (var (child, _) in Children(element, item, header, groups))
                    yield return (child, true);
            }
            else if (groups.Any(g => element.Name == Table + g))
            {
                foreach (var child in Children(element, item, header, groups))
                    yield return child;
            }
        }
    }

    private static int Repeat(XElement element, string attribute) =>
        int.TryParse((string?)element.Attribute(Table + attribute), out var count) && count > 0 ? count : 1;

    private static bool IsCollapsed(XElement element) => (string?)element.Attribute(Table + "visibility") is "collapse" or "filter";

    /// <summary>The cells of one row element as runs of repeated cells; covered cells (inside a merge) stay empty.</summary>
    private List<CellRun> Cells(XElement row, List<Span> columns)
    {
        var runs = new List<CellRun>();
        var rowDefault = (string?)row.Attribute(Table + "default-cell-style-name");
        var c = 0;
        foreach (var element in row.Elements())
        {
            var covered = element.Name == Table + "covered-table-cell";
            if (!covered && element.Name != Table + "table-cell")
                continue;
            if (c >= MaxColumns)
                break;
            var count = Math.Min(Repeat(element, "number-columns-repeated"), MaxColumns - c);
            if (!covered)
            {
                var styleName = (string?)element.Attribute(Table + "style-name") ?? rowDefault ?? columns.FirstOrDefault(s => s.Start <= c && c < s.Start + s.Count)?.DefaultStyle;
                var cell = Cell(element, styleName);
                if (cell is { HasContent: false } || cell is { Text.Length: 0 } && count > MaxStyledRepeat)
                    cell = null;
                var columnSpan = Repeat(element, "number-columns-spanned");
                var rowSpan = Repeat(element, "number-rows-spanned");
                if (cell is not null || columnSpan > 1 || rowSpan > 1)
                    runs.Add(new CellRun(c, count, cell, columnSpan, rowSpan));
            }
            c += count;
        }
        return runs;
    }

    private SheetCell Cell(XElement element, string? styleName)
    {
        var (style, dataStyle) = CellStyleOf(styleName);
        var type = (string?)element.Attribute(Office + "value-type");
        double? value = null;
        switch (type)
        {
            case "float" or "percentage" or "currency":
                if (double.TryParse((string?)element.Attribute(Office + "value"), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    value = number;
                break;
            case "date":
                if (DateTimeOffset.TryParse((string?)element.Attribute(Office + "date-value"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                    value = NumberFormatter.ToSerial(date.DateTime);
                break;
            case "time":
                try
                {
                    value = XmlConvert.ToTimeSpan((string?)element.Attribute(Office + "time-value") ?? "").TotalDays;
                }
                catch (FormatException) { }
                break;
            case "boolean":
                value = (string?)element.Attribute(Office + "boolean-value") is "true" or "1" ? 1 : 0;
                break;
        }

        var text = Paragraphs(element);
        if (type == "boolean")
            return new SheetCell(text ?? (value == 1 ? "TRUE" : "FALSE"), style) { Value = value, IsBoolean = true };
        if (value is not { } v)
            return new SheetCell(text ?? (string?)element.Attribute(Office + "string-value") ?? "", style);

        var format = FormatOf(dataStyle) ?? type switch
        {
            "date" => v == Math.Floor(v) ? "yyyy-mm-dd" : "yyyy-mm-dd hh:mm:ss",
            "time" => "hh:mm:ss",
            "percentage" => "0%",
            _ => null,
        };
        format = NumberFormatter.IsGeneral(format) ? null : format;
        // text:p is what Calc showed when it saved; files written by other programs may leave it out.
        return new SheetCell(text ?? _formatter.Format(v, format), style, IsNumber: true) { Value = v, NumberFormat = format };
    }

    /// <summary>The text of a cell: its paragraphs joined by line breaks; null when it has none.</summary>
    private static string? Paragraphs(XElement cell)
    {
        var paragraphs = cell.Elements().Where(e => e.Name == Text + "p" || e.Name == Text + "h").ToList();
        if (paragraphs.Count == 0)
            return null;
        var sb = new StringBuilder();
        for (var i = 0; i < paragraphs.Count; i++)
        {
            if (i > 0)
                sb.Append('\n');
            Inline(paragraphs[i], sb);
        }
        return sb.ToString();
    }

    private static void Inline(XElement element, StringBuilder sb)
    {
        foreach (var node in element.Nodes())
        {
            if (node is XText text)
            {
                // ODF collapses white space in text; extra spaces are text:s elements.
                foreach (var ch in text.Value)
                {
                    var c = ch is '\t' or '\r' or '\n' ? ' ' : ch;
                    if (c != ' ' || sb.Length == 0 || sb[^1] != ' ')
                        sb.Append(c);
                }
            }
            else if (node is XElement child)
            {
                if (child.Name == Text + "s")
                    sb.Append(' ', Repeat(child, Text + "c"));
                else if (child.Name == Text + "tab")
                    sb.Append('\t');
                else if (child.Name == Text + "line-break")
                    sb.Append('\n');
                else if (child.Name != Office + "annotation" && child.Name != Text + "note")
                    Inline(child, sb);
            }
        }
    }

    private static int Repeat(XElement element, XName attribute) =>
        int.TryParse((string?)element.Attribute(attribute), out var count) && count > 0 ? count : 1;
}
