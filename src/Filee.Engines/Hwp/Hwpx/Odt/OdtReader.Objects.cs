// Tables, frames (pictures and text boxes), drawing shapes and formulas of an ODT.
//
// Tables: every grid position is a table:table-cell or a table:covered-table-cell (the part of a spanning cell), so
// cells take the column of their position; repeated columns, rows and cells are cut to DocumentLimits. Frames and
// shapes anchored as characters stay in the text, the others float (HAnchor from the graphic style's position, its
// relation and wrap). Formulas (embedded Math objects) become their text.

using System.Globalization;
using System.Xml.Linq;
using Filee.Engines.Documents;
using static Filee.Engines.Hwp.Hwpx.Odt.Odf;

namespace Filee.Engines.Hwp.Hwpx.Odt;

internal sealed partial class OdtReader
{
    /// <summary>How often one table row may repeat (text tables repeat rows rarely; damaged files billions of times).</summary>
    private const int MaxRowRepeat = 1000;

    private readonly Dictionary<string, string?> _media = [];

    // ───────────────────────── Tables ─────────────────────────

    private void ReadTable(XElement element, Sink sink)
    {
        _budget.Object();
        using var level = _budget.Enter();
        var chain = Chain("table", Attr(element, Table + "style-name"), withDefault: false);
        var props = chain.Select(s => s.Element(Style + "table-properties")).ToList();
        var newPage = sink.Body && BodyBlock(Inherited(chain, Style + "master-page-name") is { Length: > 0 } master ? master : null,
            Inherited(props, Style + "page-number"), sink);

        var align = Inherited(props, Table + "align");
        var table = new HTable
        {
            // ODF tables have no borders of their own: the cells have them.
            Borders = HBorders.Empty,
            Align = align switch
            {
                "center" => HAlign.Center,
                "right" => HAlign.Right,
                _ => HAlign.Left,
            },
            Indent = align is "center" or "right" ? 0 : Math.Clamp(Length(Inherited(props, Fo + "margin-left")) ?? 0, -1_000_000, 1_000_000),
            PageBreakBefore = Inherited(props, Fo + "break-before") == "page" || newPage || _breakAfter && sink.Body,
        };
        if (sink.Body)
            _breakAfter = Inherited(props, Fo + "break-after") == "page";

        var maxColumns = _budget.Limits.MaxTableColumns;
        var widths = new List<int?>();
        var relative = new List<double?>();
        foreach (var column in TableColumns(element))
        {
            var columnProps = Find("table-column", Attr(column, Table + "style-name"))?.Element(Style + "table-column-properties");
            var width = Length(Attr(columnProps, Style + "column-width"));
            var rel = Attr(columnProps, Style + "rel-column-width") is { } text && double.TryParse(text.TrimEnd('*'), NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && r > 0 && double.IsFinite(r) ? r : (double?)null;
            var repeat = Math.Clamp(Int(Attr(column, Table + "number-columns-repeated")) ?? 1, 1, Math.Max(0, maxColumns - widths.Count));
            for (var i = 0; i < repeat; i++)
            {
                widths.Add(width is > 0 ? Math.Min(width.Value, 10_000_000) : null);
                relative.Add(rel);
            }
        }

        foreach (var (row, header) in TableRows(element, false))
        {
            var repeat = Math.Clamp(Int(Attr(row, Table + "number-rows-repeated")) ?? 1, 1, MaxRowRepeat);
            for (var i = 0; i < repeat; i++)
                table.Rows.Add(ReadRow(row, header, maxColumns));
        }

        // A cell spans no further than the last row and column.
        var columnCount = widths.Count;
        for (var r = 0; r < table.Rows.Count; r++)
        {
            foreach (var cell in table.Rows[r].Cells)
            {
                cell.RowSpan = Math.Min(cell.RowSpan, table.Rows.Count - r);
                columnCount = Math.Max(columnCount, cell.Column + cell.ColSpan);
            }
        }
        table.ColumnCount = Math.Clamp(columnCount, 1, maxColumns);
        if (widths.Count == table.ColumnCount && widths.All(w => w is not null))
        {
            table.ColumnWidths = [.. widths.Select(w => w!.Value)];
        }
        else if (relative.Count == table.ColumnCount && relative.All(r => r is not null))
        {
            var total = relative.Sum(r => r!.Value);
            table.RelativeWidths = [.. relative.Select(r => r!.Value / total)];
        }
        Add(sink, table);
    }

    /// <summary>The column definitions of a table, looking into column groups and header columns.</summary>
    private static IEnumerable<XElement> TableColumns(XElement container)
    {
        foreach (var element in container.Elements())
        {
            if (element.Name == Table + "table-column")
            {
                yield return element;
            }
            else if (element.Name == Table + "table-columns" || element.Name == Table + "table-header-columns" || element.Name == Table + "table-column-group")
            {
                foreach (var column in TableColumns(element))
                    yield return column;
            }
        }
    }

    /// <summary>The rows of a table, looking into row groups; rows in table:table-header-rows repeat on every page.</summary>
    private static IEnumerable<(XElement Row, bool Header)> TableRows(XElement container, bool header)
    {
        foreach (var element in container.Elements())
        {
            if (element.Name == Table + "table-row")
            {
                yield return (element, header);
            }
            else if (element.Name == Table + "table-header-rows" || element.Name == Table + "table-rows" || element.Name == Table + "table-row-group")
            {
                foreach (var row in TableRows(element, header || element.Name == Table + "table-header-rows"))
                    yield return row;
            }
        }
    }

    private HRow ReadRow(XElement element, bool header, int maxColumns)
    {
        var props = Chain("table-row", Attr(element, Table + "style-name"), withDefault: false).Select(s => s.Element(Style + "table-row-properties")).ToList();
        var row = new HRow
        {
            Header = header,
            Height = (Length(Inherited(props, Style + "min-row-height")) ?? Length(Inherited(props, Style + "row-height"))) is > 0 and var height ? Math.Min(height, 10_000_000) : null,
            CantSplit = Inherited(props, Fo + "keep-together") == "always",
        };
        var column = 0;
        foreach (var cell in element.Elements())
        {
            var covered = cell.Name == Table + "covered-table-cell";
            if (!covered && cell.Name != Table + "table-cell")
                continue;
            var repeat = Math.Clamp(Int(Attr(cell, Table + "number-columns-repeated")) ?? 1, 1, Math.Max(1, maxColumns - column));
            if (covered)
            {
                column = Math.Min(maxColumns, column + repeat);
                continue;
            }
            for (var i = 0; i < repeat && column < maxColumns; i++)
                row.Cells.Add(ReadCell(cell, column++, maxColumns));
        }
        return row;
    }

    private HCell ReadCell(XElement element, int column, int maxColumns)
    {
        if (++_cells > _budget.Limits.MaxParagraphs)
            throw DocumentLimits.Exceeded($"The document has more than {DocumentLimits.Count(_budget.Limits.MaxParagraphs)} table cells.");
        var props = Chain("table-cell", Attr(element, Table + "style-name")).Select(s => s.Element(Style + "table-cell-properties")).ToList();
        var fill = Inherited(props, Fo + "background-color");
        var cell = new HCell
        {
            Column = column,
            ColSpan = Math.Clamp(Int(Attr(element, Table + "number-columns-spanned")) ?? 1, 1, maxColumns - column),
            RowSpan = Math.Clamp(Int(Attr(element, Table + "number-rows-spanned")) ?? 1, 1, 1_000_000),
            Fill = fill == "transparent" ? null : Color(fill),
            Borders = Borders(props),
            CellMargin = Padding(props),
            VerticalAlign = Inherited(props, Style + "vertical-align") switch
            {
                "middle" => HVerticalAlign.Center,
                "bottom" => HVerticalAlign.Bottom,
                _ => HVerticalAlign.Top,
            },
        };
        var sink = new Sink(cell.Blocks, body: false);
        ReadBlocks(element.Elements(), sink);
        Flush(sink);
        return cell;
    }

    /// <summary>The four borders of a chain of property elements (fo:border, then each side).</summary>
    private static HBorders Borders(IEnumerable<XElement?> chain)
    {
        HBorder left = HBorder.None, right = HBorder.None, top = HBorder.None, bottom = HBorder.None;
        foreach (var props in chain)
        {
            if (Attr(props, Fo + "border") is { } all)
                left = right = top = bottom = Border(all);
            left = Attr(props, Fo + "border-left") is { } l ? Border(l) : left;
            right = Attr(props, Fo + "border-right") is { } r ? Border(r) : right;
            top = Attr(props, Fo + "border-top") is { } t ? Border(t) : top;
            bottom = Attr(props, Fo + "border-bottom") is { } b ? Border(b) : bottom;
        }
        return new HBorders(left, right, top, bottom);
    }

    /// <summary>"0.5pt solid #000000" → a border; "none" (or no width) → none.</summary>
    private static HBorder Border(string value)
    {
        var style = HBorderStyle.Solid;
        int? width = null;
        var color = "#000000";
        foreach (var token in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (token)
            {
                case "none" or "hidden":
                    return HBorder.None;
                case "solid" or "groove" or "ridge" or "inset" or "outset":
                    style = HBorderStyle.Solid;
                    break;
                case "dashed" or "dash" or "long-dash":
                    style = HBorderStyle.Dash;
                    break;
                case "dotted":
                    style = HBorderStyle.Dot;
                    break;
                case "dot-dash" or "dot-dot-dash":
                    style = HBorderStyle.DashDot;
                    break;
                case "double" or "double-thin":
                    style = HBorderStyle.Double;
                    break;
                case "thin":
                    width = 50;
                    break;
                case "medium":
                    width = 100;
                    break;
                case "thick":
                    width = 250;
                    break;
                default:
                    if (Odf.Color(token) is { } c)
                        color = c;
                    else if (Length(token) is { } length)
                        width = length;
                    break;
            }
        }
        return width is > 0 and var w ? new HBorder(style, Math.Min(w, 100_000) / HwpxUnits.PerMm, color) : HBorder.None;
    }

    /// <summary>The padding of a chain of property elements (fo:padding, then each side); 0 where none is set.</summary>
    private static HInsets Padding(IEnumerable<XElement?> chain)
    {
        int left = 0, right = 0, top = 0, bottom = 0;
        foreach (var props in chain)
        {
            if (Length(Attr(props, Fo + "padding")) is { } all)
                left = right = top = bottom = all;
            left = Length(Attr(props, Fo + "padding-left")) ?? left;
            right = Length(Attr(props, Fo + "padding-right")) ?? right;
            top = Length(Attr(props, Fo + "padding-top")) ?? top;
            bottom = Length(Attr(props, Fo + "padding-bottom")) ?? bottom;
        }
        static int Clamp(int value) => Math.Clamp(value, 0, 1_000_000);
        return new HInsets(Clamp(left), Clamp(right), Clamp(top), Clamp(bottom));
    }

    /// <summary>The outer margins of an object (fo:margin, then each side).</summary>
    private static HInsets Margins(IEnumerable<XElement?> chain)
    {
        int left = 0, right = 0, top = 0, bottom = 0;
        foreach (var props in chain)
        {
            if (Length(Attr(props, Fo + "margin")) is { } all)
                left = right = top = bottom = all;
            left = Length(Attr(props, Fo + "margin-left")) ?? left;
            right = Length(Attr(props, Fo + "margin-right")) ?? right;
            top = Length(Attr(props, Fo + "margin-top")) ?? top;
            bottom = Length(Attr(props, Fo + "margin-bottom")) ?? bottom;
        }
        static int Clamp(int value) => Math.Clamp(value, 0, 1_000_000);
        return new HInsets(Clamp(left), Clamp(right), Clamp(top), Clamp(bottom));
    }

    // ───────────────────────── Frames and shapes ─────────────────────────

    /// <summary>The objects of a drawing element (a frame, a shape, a group of them, a linked frame).</summary>
    private List<HInline> Objects(XElement element, TextProps text, string? groupAnchor)
    {
        var result = new List<HInline>();
        switch (element.Name.LocalName)
        {
            case "frame":
                if (Frame(element, text, groupAnchor) is { } frame)
                    result.Add(frame);
                break;
            case "rect" or "ellipse" or "circle" or "custom-shape" or "line":
                if (Shape(element, groupAnchor) is { } shape)
                    result.Add(shape);
                break;
            case "g":
                {
                    using var level = _budget.Enter();
                    var anchor = Attr(element, Text + "anchor-type") ?? groupAnchor;
                    foreach (var child in element.Elements())
                        result.AddRange(Objects(child, text, anchor));
                    break;
                }
            case "a":
                foreach (var child in element.Elements())
                    result.AddRange(Objects(child, text, groupAnchor));
                break;
        }
        return result;
    }

    /// <summary>The graphic properties of an object's style and its parents; with the default style for shapes.</summary>
    private List<XElement?> GraphicProps(XElement element, bool withDefault) =>
        Chain("graphic", Attr(element, Draw + "style-name"), withDefault).Select(s => s.Element(Style + "graphic-properties")).ToList();

    private HInline? Frame(XElement frame, TextProps text, string? groupAnchor)
    {
        _budget.Object();
        var anchorType = Attr(frame, Text + "anchor-type") ?? groupAnchor ?? "paragraph";
        var own = GraphicProps(frame, withDefault: false);
        var all = GraphicProps(frame, withDefault: true);
        var width = Length(Attr(frame, Svg + "width")) ?? Length(Attr(frame, Fo + "min-width"));
        var height = Length(Attr(frame, Svg + "height")) ?? Length(Attr(frame, Fo + "min-height"));
        var anchor = Anchor(anchorType, all, Length(Attr(frame, Svg + "x")) ?? 0, Length(Attr(frame, Svg + "y")) ?? 0);
        var zOrder = Int(Attr(frame, Draw + "z-index"));

        if (frame.Element(Draw + "text-box") is { } content)
        {
            var box = new HTextBox
            {
                Width = Size(width ?? Length(Attr(content, Fo + "min-width"))),
                Height = Size(height ?? Length(Attr(content, Fo + "min-height"))),
                Anchor = anchor,
                ZOrder = zOrder,
                OuterMargin = Margins(own),
                Fill = Fill(own, shape: false),
                Line = Border(Inherited(own, Fo + "border") ?? Inherited(own, Fo + "border-top") ?? "none"),
                Padding = Padding(own),
                VerticalAlign = VerticalAlign(all),
            };
            TextBoxContent(content, box.Blocks);
            return box;
        }
        foreach (var image in frame.Elements(Draw + "image"))
        {
            if (Picture(image) is { } path)
            {
                return new HImage(path)
                {
                    Width = width is > 0 ? width : null,
                    Height = height is > 0 ? height : null,
                    Anchor = anchor,
                    ZOrder = zOrder,
                    OuterMargin = Margins(own),
                    Line = Border(Inherited(own, Fo + "border") ?? Inherited(own, Fo + "border-top") ?? "none"),
                    Margin = Padding(own),
                };
            }
        }
        if (frame.Element(Draw + "object") is { } embedded && Formula(embedded) is { Length: > 0 } formula)
            return new HText(formula, CharFormat(text) with { Italic = true });
        return null;
    }

    private void TextBoxContent(XElement content, List<HBlock> blocks)
    {
        using var level = _budget.Enter();
        var sink = new Sink(blocks, body: false);
        ReadBlocks(content.Elements(), sink);
        Flush(sink);
    }

    private static int Size(int? length) => Math.Clamp(length ?? 0, 0, 10_000_000);

    private static HVerticalAlign VerticalAlign(List<XElement?> props) =>
        (Inherited(props, Draw + "textarea-vertical-align") ?? Inherited(props, Style + "vertical-align")) switch
        {
            "middle" => HVerticalAlign.Center,
            "bottom" => HVerticalAlign.Bottom,
            _ => HVerticalAlign.Top,
        };

    /// <summary>
    /// The fill of an object: draw:fill and its colour, else fo:background-color. Shapes fill solid unless told
    /// otherwise; frames only when their style says so. A gradient fills with its start colour.
    /// </summary>
    private string? Fill(List<XElement?> props, bool shape)
    {
        var fill = Inherited(props, Draw + "fill");
        if (fill == "none")
            return null;
        if (fill == "gradient")
            return Color(Attr(_gradients.GetValueOrDefault(Inherited(props, Draw + "fill-gradient-name") ?? ""), Draw + "start-color"));
        if (fill == "solid" || fill is null && shape)
            return Color(Inherited(props, Draw + "fill-color"));
        var background = Inherited(props, Fo + "background-color");
        return background == "transparent" ? null : Color(background);
    }

    /// <summary>The outline of a shape: draw:stroke, svg:stroke-width (0: a hairline, as thin as the output allows) and svg:stroke-color.</summary>
    private static HBorder Stroke(List<XElement?> props)
    {
        var stroke = Inherited(props, Draw + "stroke") ?? "solid";
        if (stroke == "none")
            return HBorder.None;
        var width = Math.Clamp(Length(Inherited(props, Svg + "stroke-width")) ?? 0, 0, 100_000);
        return new HBorder(stroke == "dash" ? HBorderStyle.Dash : HBorderStyle.Solid, width / HwpxUnits.PerMm,
            Color(Inherited(props, Svg + "stroke-color")) ?? "#000000");
    }

    /// <summary>
    /// Where a floating object is: its graphic style's position and relation (style:horizontal-pos / -rel, vertical
    /// ...) with the frame's svg:x / svg:y, and how text flows around it (style:wrap). As-character objects: null.
    /// </summary>
    private static HAnchor? Anchor(string anchorType, List<XElement?> props, int x, int y)
    {
        if (anchorType == "as-char")
            return null;
        string? Get(string name) => Inherited(props, Style + name);
        var horizontalRelation = Get("horizontal-rel") ?? (anchorType switch
        {
            "page" => "page",
            "char" => "char",
            _ => "paragraph",
        });
        var verticalRelation = Get("vertical-rel") ?? (anchorType == "page" ? "page" : "paragraph");
        var horizontalTo = horizontalRelation switch
        {
            "page" or "page-start-margin" or "page-end-margin" => "PAPER",
            "page-content" => "PAGE",
            "char" => "PARA",
            _ => "COLUMN",
        };
        var verticalTo = verticalRelation switch
        {
            "page" => "PAPER",
            "page-content" => "PAGE",
            _ => "PARA",
        };
        var (horizontalAlign, horizontalOffset) = (Get("horizontal-pos") ?? "from-left") switch
        {
            "left" => ("LEFT", 0),
            "center" => ("CENTER", 0),
            "right" => ("RIGHT", 0),
            "inside" => ("INSIDE", 0),
            "outside" => ("OUTSIDE", 0),
            _ => ("LEFT", x), // from-left, from-inside
        };
        var (verticalAlign, verticalOffset) = (Get("vertical-pos") ?? "from-top") switch
        {
            "top" => ("TOP", 0),
            "middle" => ("CENTER", 0),
            "bottom" or "below" => ("BOTTOM", 0),
            _ => ("TOP", y),
        };
        var wrap = Get("wrap") switch
        {
            "none" => "TOP_AND_BOTTOM",
            "run-through" => Get("run-through") == "background" ? "BEHIND_TEXT" : "IN_FRONT_OF_TEXT",
            _ => "SQUARE",
        };
        return new HAnchor(horizontalTo, horizontalAlign, horizontalOffset, verticalTo, verticalAlign, verticalOffset, wrap, AllowOverlap: true);
    }

    private HInline? Shape(XElement element, string? groupAnchor)
    {
        _budget.Object();
        var anchorType = Attr(element, Text + "anchor-type") ?? groupAnchor ?? "paragraph";
        var props = GraphicProps(element, withDefault: true);
        var zOrder = Int(Attr(element, Draw + "z-index"));
        var (translateX, translateY) = Translation(Attr(element, Draw + "transform"));

        if (element.Name.LocalName == "line")
        {
            var x1 = Length(Attr(element, Svg + "x1")) ?? 0;
            var y1 = Length(Attr(element, Svg + "y1")) ?? 0;
            var x2 = Length(Attr(element, Svg + "x2")) ?? 0;
            var y2 = Length(Attr(element, Svg + "y2")) ?? 0;
            return new HShape(HShapeKind.Line)
            {
                Width = Size((int)Math.Min(int.MaxValue, Math.Abs((long)x2 - x1))),
                Height = Size((int)Math.Min(int.MaxValue, Math.Abs((long)y2 - y1))),
                Anchor = Anchor(anchorType, props, Math.Min(x1, x2), Math.Min(y1, y2)),
                ZOrder = zOrder,
                Line = Stroke(props),
                FlipHorizontal = x2 < x1,
                FlipVertical = y2 < y1,
            };
        }

        HShapeKind? kind = element.Name.LocalName switch
        {
            "rect" => HShapeKind.Rectangle,
            "ellipse" or "circle" => HShapeKind.Ellipse,
            _ => Attr(element.Element(Draw + "enhanced-geometry"), Draw + "type") switch
            {
                "rectangle" or "round-rectangle" or "flowchart-process" or "flowchart-alternate-process" => HShapeKind.Rectangle,
                "ellipse" or "circle" or "flowchart-connector" => HShapeKind.Ellipse,
                _ => null,
            },
        };
        var width = Size(Length(Attr(element, Svg + "width")));
        var height = Size(Length(Attr(element, Svg + "height")));
        var anchor = Anchor(anchorType, props, Length(Attr(element, Svg + "x")) ?? translateX, Length(Attr(element, Svg + "y")) ?? translateY);
        var corner = Length(Attr(element, Draw + "corner-radius")) is > 0 and var radius && Math.Min(width, height) > 0
            ? Math.Clamp((int)Math.Round(radius * 100.0 / Math.Min(width, height)), 0, 50)
            : 0;
        var hasText = element.Elements().Any(e => e.Name == Text + "p" || e.Name == Text + "h" || e.Name == Text + "list");
        if (hasText)
        {
            var box = new HTextBox
            {
                Width = width,
                Height = height,
                Anchor = anchor,
                ZOrder = zOrder,
                OuterMargin = Margins(props),
                Shape = kind == HShapeKind.Ellipse ? HShapeKind.Ellipse : HShapeKind.Rectangle,
                CornerRatio = corner,
                Fill = kind is null ? null : Fill(props, shape: true),
                Line = kind is null ? HBorder.None : Stroke(props),
                Padding = Padding(props),
                VerticalAlign = VerticalAlign(props),
            };
            TextBoxContent(element, box.Blocks);
            return box;
        }
        if (kind is null)
            return null; // a shape of a kind the model has no outline for
        return new HShape(kind.Value)
        {
            Width = width,
            Height = height,
            Anchor = anchor,
            ZOrder = zOrder,
            OuterMargin = Margins(props),
            Fill = Fill(props, shape: true),
            Line = Stroke(props),
            CornerRatio = corner,
        };
    }

    /// <summary>The translation of a draw:transform ("rotate (0.5) translate (2cm 3cm)"), where shapes without svg:x / svg:y keep their place.</summary>
    private static (int X, int Y) Translation(string? transform)
    {
        var start = transform?.IndexOf("translate", StringComparison.Ordinal) ?? -1;
        if (start < 0)
            return (0, 0);
        var open = transform!.IndexOf('(', start);
        var close = open < 0 ? -1 : transform.IndexOf(')', open);
        if (close < 0)
            return (0, 0);
        var parts = transform[(open + 1)..close].Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        return (Length(parts.ElementAtOrDefault(0)) ?? 0, Length(parts.ElementAtOrDefault(1)) ?? 0);
    }

    // ───────────────────────── Pictures and formulas ─────────────────────────

    /// <summary>The picture of a draw:image: a part of the package (never a file outside it) or inline base64 data.</summary>
    private string? Picture(XElement image)
    {
        if (Attr(image, XLink + "href") is not { } href)
            return image.Element(Office + "binary-data") is { } data ? InlinePicture(data.Value) : null;
        if (InternalPath(href) is not { } path || _package.GetEntry(path) is not { } entry)
            return null;
        if (_media.TryGetValue(path, out var known))
            return known;
        Directory.CreateDirectory(_mediaFolder);
        string? file = Path.Combine(_mediaFolder, $"odt-media-{_media.Count + 1}{DocumentPackage.SafeExtension(path)}");
        if (!_package.ExtractPicture(entry, file) || !Usable(file))
        {
            if (File.Exists(file))
                File.Delete(file);
            file = null;
        }
        _media[path] = file;
        return file;
    }

    /// <summary>A picture stored in the XML as base64 (office:binary-data), within the part and picture limits.</summary>
    private string? InlinePicture(string base64)
    {
        if (base64.Length / 4L * 3 > _budget.Limits.MaxPartBytes)
            return null;
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64.Trim());
        }
        catch (FormatException)
        {
            return null;
        }
        Directory.CreateDirectory(_mediaFolder);
        var file = Path.Combine(_mediaFolder, $"odt-media-{_media.Count + 1}");
        _media[$"\0inline-{_media.Count}"] = file;
        File.WriteAllBytes(file, bytes);
        if (PictureSize.Read(file) is not { } size || (long)size.Width * size.Height > _budget.Limits.MaxPicturePixels)
        {
            File.Delete(file);
            return null;
        }
        return file;
    }

    /// <summary>A picture the writers can use: one whose size its header gives, or a vector format by its name.</summary>
    private static bool Usable(string file) =>
        PictureSize.Read(file) is not null || Path.GetExtension(file).ToLowerInvariant() is ".svg" or ".wmf" or ".emf" or ".tif" or ".tiff";

    /// <summary>A package path from an xlink:href ("Pictures/x.png", "./Object 1"); null for links outside the package.</summary>
    private static string? InternalPath(string href)
    {
        var path = href.Trim();
        if (path.StartsWith("./", StringComparison.Ordinal))
            path = path[2..];
        if (path.Length == 0 || path.Contains("://", StringComparison.Ordinal) || path.StartsWith('/') || path.Contains(':')
            || path.Split('/').Any(segment => segment is ".." or "."))
            return null;
        return path;
    }

    /// <summary>The text of an embedded formula: its StarMath annotation, else the MathML characters.</summary>
    private string? Formula(XElement embedded)
    {
        if (Attr(embedded, XLink + "href") is not { } href || InternalPath(href) is not { } folder)
            return null;
        var math = Load(folder.TrimEnd('/') + "/content.xml")?.Root;
        if (math?.Name != MathMl + "math")
            return null;
        var annotation = math.Descendants(MathMl + "annotation").FirstOrDefault(a => Attr(a, "encoding")?.StartsWith("StarMath", StringComparison.Ordinal) == true);
        if (annotation is not null)
            return annotation.Value.Trim();
        return string.Concat(math.Descendants().Where(e => !e.HasElements && e.Name.LocalName != "annotation").Select(e => e.Value.Trim()));
    }
}
