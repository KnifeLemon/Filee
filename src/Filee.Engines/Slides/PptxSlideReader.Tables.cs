// Tables of PPTX slides (a:tbl in a graphic frame): the grid, merged cells, each cell's fill, borders, margins and
// text, with the table's style applied first. A style comes from ppt/tableStyles.xml; PowerPoint's default style
// ("Medium Style 2 – Accent 1"), which files often name without writing it out, is built in. The style's parts apply
// in PowerPoint's order: the whole table, banded rows and columns, the first and last column, the first and last row;
// the cell's own properties win over all of them.

using System.Xml.Linq;

namespace Filee.Engines.Slides;

internal sealed partial class PptxSlideReader
{
    private const string DefaultTableStyle = "{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}";

    private SlideTable? Table(SlideContext context, string part, XElement table, XElement xfrm)
    {
        using var level = _budget.Enter();
        var result = new SlideTable();
        Place(result, xfrm);
        var columns = table.Element(A + "tblGrid")?.Elements(A + "gridCol").Take(_package.Limits.MaxTableColumns).Select(c => Math.Max(0, Points(Number(c.Attribute("w"))))).ToArray() ?? [];
        if (columns.Length == 0)
            return null;
        result.Columns = columns;

        var properties = table.Element(A + "tblPr");
        var styleId = (string?)properties?.Element(A + "tableStyleId") ?? (string?)_tableStyles?.Attribute("def");
        var style = properties?.Element(A + "tableStyle")
                    ?? _tableStyles?.Elements(A + "tblStyle").FirstOrDefault(s => (string?)s.Attribute("styleId") == styleId)
                    ?? (styleId == DefaultTableStyle || properties?.Element(A + "tableStyleId") is not null ? BuiltInStyle : null);
        bool On(string name) => Flag(properties?.Attribute(name));
        var (firstRow, lastRow, firstColumn, lastColumn, bandRows, bandColumns) =
            (On("firstRow"), On("lastRow"), On("firstCol"), On("lastCol"), On("bandRow"), On("bandCol"));

        var rows = table.Elements(A + "tr").ToList();
        for (var r = 0; r < rows.Count; r++)
        {
            var row = new SlideTableRow { Height = Math.Max(0, Points(Number(rows[r].Attribute("h")))) };
            var cells = rows[r].Elements(A + "tc").Take(columns.Length).ToList();
            for (var c = 0; c < cells.Count; c++)
            {
                _budget.Object();
                var tc = cells[c];
                // The style parts that apply to this cell, weakest first.
                var parts = new List<XElement>();
                void Use(string name)
                {
                    if (style?.Element(A + name) is { } found)
                        parts.Add(found);
                }
                Use("wholeTbl");
                var bodyRow = r - (firstRow ? 1 : 0);
                var bodyColumn = c - (firstColumn ? 1 : 0);
                if (bandRows && bodyRow >= 0)
                    Use(bodyRow % 2 == 0 ? "band1H" : "band2H");
                if (bandColumns && bodyColumn >= 0)
                    Use(bodyColumn % 2 == 0 ? "band1V" : "band2V");
                if (firstColumn && c == 0)
                    Use("firstCol");
                if (lastColumn && c == cells.Count - 1)
                    Use("lastCol");
                if (lastRow && r == rows.Count - 1)
                    Use("lastRow");
                if (firstRow && r == 0)
                    Use("firstRow");
                row.Cells.Add(Cell(context, part, tc, parts, r, c, rows.Count, cells.Count));
            }
            result.Rows.Add(row);
        }
        return result;
    }

    private SlideTableCell Cell(SlideContext context, string part, XElement tc, List<XElement> styleParts, int row, int column, int rows, int columns)
    {
        var cell = new SlideTableCell
        {
            ColumnSpan = Math.Clamp((int)Number(tc.Attribute("gridSpan"), 1), 1, columns),
            RowSpan = Math.Clamp((int)Number(tc.Attribute("rowSpan"), 1), 1, rows),
            Covered = Flag(tc.Attribute("hMerge")) || Flag(tc.Attribute("vMerge")),
        };
        var properties = tc.Element(A + "tcPr");

        // Fill: the cell's own, else the strongest style part's.
        var fill = Fill(context, part, properties, null, null, out var specified);
        if (!specified)
        {
            foreach (var style in Enumerable.Reverse(styleParts))
            {
                var tcStyle = style.Element(A + "tcStyle");
                if (tcStyle?.Element(A + "fill") is { } styleFill)
                {
                    fill = Fill(context, part, styleFill, null, null, out specified);
                    if (specified)
                        break;
                }
                if (tcStyle?.Element(A + "fillRef") is { } reference)
                {
                    fill = ThemeFill(context, part, reference);
                    break;
                }
            }
        }
        cell.Fill = fill;

        // Borders: the cell's own line, else the style's (its outer border on the table's edges, inside lines between).
        SlideLine? Border(string own, string outer, string inside, bool edge)
        {
            if (properties?.Element(A + own) is { } line)
                return Line(context, part, [line], null);
            foreach (var style in Enumerable.Reverse(styleParts))
            {
                var borders = style.Element(A + "tcStyle")?.Element(A + "tcBdr");
                var side = borders?.Element(A + (edge ? outer : inside)) ?? (edge ? null : borders?.Element(A + outer));
                if (side is null)
                    continue;
                if (side.Element(A + "ln") is { } ln)
                    return Line(context, part, [ln], null);
                if (side.Element(A + "lnRef") is { } reference)
                    return Line(context, part, [], reference);
            }
            return null;
        }
        cell.Left = Border("lnL", "left", "insideV", column == 0);
        cell.Right = Border("lnR", "right", "insideV", column + cell.ColumnSpan >= columns);
        cell.Top = Border("lnT", "top", "insideH", row == 0);
        cell.Bottom = Border("lnB", "bottom", "insideH", row + cell.RowSpan >= rows);
        cell.DiagonalDown = Border("lnTlToBr", "tl2br", "tl2br", true);
        cell.DiagonalUp = Border("lnBlToTr", "tr2bl", "tr2bl", true);

        // Text: the style's text properties (bold, italic, colour, font) under the cell's own.
        var textStyle = Enumerable.Reverse(styleParts).Select(s => s.Element(A + "tcTxStyle")).OfType<XElement>().ToList();
        var defaults = new XElement(A + "defRPr");
        if (textStyle.Select(t => (string?)t.Attribute("b")).FirstOrDefault(v => v is not null) is "on")
            defaults.SetAttributeValue("b", "1");
        if (textStyle.Select(t => (string?)t.Attribute("i")).FirstOrDefault(v => v is not null) is "on")
            defaults.SetAttributeValue("i", "1");
        if (textStyle.Select(t => t.Elements().FirstOrDefault(e => e.Name.LocalName is "srgbClr" or "schemeClr" or "sysClr" or "prstClr" or "scrgbClr" or "hslClr"))
                .FirstOrDefault(e => e is not null) is { } colorElement)
            defaults.Add(new XElement(A + "solidFill", colorElement));
        var fontRef = textStyle.Select(t => t.Element(A + "fontRef")).FirstOrDefault(e => e is not null);

        var body = tc.Element(A + "txBody");
        var styled = new XElement(A + "lstStyle");
        for (var level = 1; level <= 9; level++)
            styled.Add(new XElement(A + $"lvl{level}pPr", new XElement(defaults)));
        var bodies = new List<XElement>();
        if (body is not null)
            bodies.Add(body);
        // The style's text properties sit under the cell's own list style: a synthetic body holding them.
        bodies.Add(new XElement(A + "txBody", styled));
        var frame = body is null ? null : TableText(context, part, body, bodies, fontRef);
        cell.Text = frame ?? new SlideTextFrame();
        cell.Text.InsetLeft = Points(Number(properties?.Attribute("marL"), 91440));
        cell.Text.InsetRight = Points(Number(properties?.Attribute("marR"), 91440));
        cell.Text.InsetTop = Points(Number(properties?.Attribute("marT"), 45720));
        cell.Text.InsetBottom = Points(Number(properties?.Attribute("marB"), 45720));
        cell.Text.Anchor = (string?)properties?.Attribute("anchor") switch
        {
            "ctr" => TextAnchor.Middle,
            "b" => TextAnchor.Bottom,
            _ => TextAnchor.Top,
        };
        cell.Text.Direction = (string?)properties?.Attribute("vert") switch
        {
            "vert" or "eaVert" or "wordArtVert" or "mongolianVert" => TextDirection.Down,
            "vert270" => TextDirection.Up,
            _ => TextDirection.Horizontal,
        };
        return cell;
    }

    private SlideTextFrame? TableText(SlideContext context, string part, XElement body, List<XElement> bodies, XElement? fontRef)
    {
        if (!body.Elements(A + "p").Any(p => p.Elements().Any(e => e.Name.LocalName is "r" or "fld" or "br")))
            return null;
        var frame = new SlideTextFrame();
        var counters = new int[10];
        var numbered = new bool[10];
        var first = true;
        foreach (var p in body.Elements(A + "p"))
        {
            _budget.Paragraph();
            frame.Paragraphs.Add(Paragraph(context, part, p, null, bodies, fontRef, 1, 0, counters, numbered, first));
            first = false;
        }
        return frame;
    }

    /// <summary>
    /// PowerPoint's default table style, Medium Style 2 – Accent 1: white lines, the header row and first and last
    /// column in accent 1 with bold white text, body rows in tints of accent 1, banded.
    /// </summary>
    private static readonly XElement BuiltInStyle = BuildDefaultStyle();

    private static XElement BuildDefaultStyle()
    {
        XElement Color(string scheme, int? tint = null) =>
            new(A + "schemeClr", new XAttribute("val", scheme), tint is { } t ? new XElement(A + "tint", new XAttribute("val", t)) : null);
        XElement Line(int width) =>
            new(A + "ln", new XAttribute("w", width), new XElement(A + "solidFill", Color("lt1")));
        XElement Borders(params (string Side, int Width)[] sides) =>
            new(A + "tcBdr", sides.Select(s => new XElement(A + s.Side, Line(s.Width))));
        XElement Part(string name, XElement? text, XElement? fill, XElement? borders) =>
            new(A + name, text, new XElement(A + "tcStyle", borders, fill is null ? null : new XElement(A + "fill", new XElement(A + "solidFill", fill))));
        XElement BoldWhite() => new(A + "tcTxStyle", new XAttribute("b", "on"), Color("lt1"));
        return new XElement(A + "tblStyle",
            Part("wholeTbl", new XElement(A + "tcTxStyle", Color("dk1")), Color("accent1", 20000),
                Borders(("left", 12700), ("right", 12700), ("top", 12700), ("bottom", 12700), ("insideH", 12700), ("insideV", 12700))),
            Part("band1H", null, Color("accent1", 40000), null),
            Part("band1V", null, Color("accent1", 40000), null),
            Part("lastCol", BoldWhite(), Color("accent1"), null),
            Part("firstCol", BoldWhite(), Color("accent1"), null),
            Part("lastRow", BoldWhite(), Color("accent1"), Borders(("top", 38100))),
            Part("firstRow", BoldWhite(), Color("accent1"), Borders(("bottom", 38100))));
    }

}
