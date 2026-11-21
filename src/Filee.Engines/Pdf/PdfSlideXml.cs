// The shape tree of an editable slide (PDF → PPTX, PdfDocumentReader.Slides) in PresentationML / DrawingML
// (ECMA-376 Part 1, 19.3 and 20.1): text boxes with their paragraphs and runs, pictures, rectangles, lines and tables,
// back to front as the page lists them. PictureDeckWriter puts the slide into the package and stores the pictures and
// links it asks for.
//
// - Text boxes have no insets and no fill, so the text sits where the PDF has it; they grow with their text when it
//   is edited (spAutoFit). Runs carry their font (for Latin, East Asian and complex scripts alike), size, bold,
//   italic, underline, colour and link; paragraphs their alignment, indents, tab stops and line distance (as a share
//   of single spacing, 1.2 em), with no space before or after and no bullet (a bullet is a character of the text).
// - Shapes have no style (p:style), so the theme adds no outline or effect: a rectangle has exactly its fill and line.
//   Fills and lines carry the PDF's transparency (a:alpha); gradients are a:gradFill (lin at an angle, or a circle
//   path from its focus); outlines of their own are a:custGeom in the shape's box (EMU).
// - Turned items have xfrm rot (60000ths of a degree, clockwise), mirrored pictures flipH / flipV; a picture's crop is
//   its a:srcRect, its transparency a:alphaModFix, a clipping path other than a rectangle its custom geometry.
// - Tables have no table style either; every cell writes its borders, fill, padding and vertical alignment, and the
//   cells a merged cell covers are marked hMerge / vMerge.
// - A page smaller or larger than the slide is scaled and centred like a picture slide (PictureDeckWriter.Fit), its
//   font sizes and line widths with it.

using System.Globalization;
using System.Text;
using Filee.Engines.Hwp.Hwpx;
using SkiaSharp;
using static Filee.Engines.Hwp.Hwpx.Pptx.PresentationPackage;

namespace Filee.Engines.Pdf;

internal static class PdfSlideXml
{
    private const string TableUri = "http://schemas.openxmlformats.org/drawingml/2006/table";

    /// <summary>Single line spacing in DrawingML's sense, in ems (PowerPoint's 100 % for most fonts).</summary>
    internal const double SingleSpacing = 1.2;

    /// <summary>Where a page goes on the slide: its top-left corner (EMU) and EMU per point.</summary>
    internal readonly record struct Placement(long X, long Y, double Scale)
    {
        public long Length(double points) => (long)Math.Round(points * Scale);
        public long Left(double points) => X + Length(points);
        public long Top(double points) => Y + Length(points);

        /// <summary>A font size or other 1/100 pt value scaled with the page.</summary>
        public int Hundredths(int value) => (int)Math.Round(value * Scale / EmuPerPoint);
    }

    /// <summary>
    /// The slide's shape tree content (after <see cref="TreeStart"/>) for <paramref name="page"/>;
    /// <paramref name="picture"/> stores a picture file and gives its relationship id, <paramref name="link"/> the same
    /// for a web link.
    /// </summary>
    public static string Shapes(PdfSlidePage page, Placement placement, Func<string, string> picture, Func<string, string> link)
    {
        var sb = new StringBuilder();
        var id = 1;
        foreach (var item in page.Items)
        {
            id++;
            switch (item)
            {
                case PdfSlideText text:
                    TextBox(sb, text, id, placement, link);
                    break;
                case PdfSlidePicture image:
                    Picture(sb, image, id, placement, picture(image.Path));
                    break;
                case PdfSlideShape { Kind: PdfShapeKind.Line } line:
                    Line(sb, line, id, placement);
                    break;
                case PdfSlideShape shape:
                    Rectangle(sb, shape, id, placement);
                    break;
                case PdfSlideTable table:
                    Table(sb, table, id, placement, link);
                    break;
            }
        }
        return sb.ToString();
    }

    private static string Transform(SKRect box, Placement placement, bool flipHorizontal = false, bool flipVertical = false, float rotation = 0) =>
        $"<a:xfrm{Rotation(rotation)}{(flipHorizontal ? " flipH=\"1\"" : "")}{(flipVertical ? " flipV=\"1\"" : "")}>" +
        $"<a:off x=\"{N(placement.Left(box.Left))}\" y=\"{N(placement.Top(box.Top))}\"/>" +
        $"<a:ext cx=\"{N(Math.Max(0, placement.Length(box.Width)))}\" cy=\"{N(Math.Max(0, placement.Length(box.Height)))}\"/></a:xfrm>";

    private static string Rotation(float degrees)
    {
        var value = (long)Math.Round((degrees % 360 + 360) % 360 * 60000) % 21600000;
        return value == 0 ? "" : $" rot=\"{N(value)}\"";
    }

    private const string Rect = "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>";

    private static string Solid(string color, double alpha = 1) => $"<a:solidFill>{Color(Hex(color), alpha)}</a:solidFill>";

    /// <summary>An sRGB colour with its alpha (1 = opaque, no a:alpha).</summary>
    private static string Color(string hex, double alpha)
    {
        var value = (long)Math.Round(Math.Clamp(double.IsFinite(alpha) ? alpha : 1, 0, 1) * 100000);
        return value >= 100000 ? $"<a:srgbClr val=\"{hex}\"/>" : $"<a:srgbClr val=\"{hex}\"><a:alpha val=\"{N(value)}\"/></a:srgbClr>";
    }

    private static long Share(double value) => (long)Math.Round(Math.Clamp(value, 0, 1) * 100000);

    private static string Gradient(PdfSlideGradient gradient)
    {
        var sb = new StringBuilder("<a:gradFill rotWithShape=\"1\"><a:gsLst>");
        foreach (var (position, color) in gradient.Stops)
            sb.Append($"<a:gs pos=\"{N(Share(position))}\">").Append(Color($"{color.Red:X2}{color.Green:X2}{color.Blue:X2}", color.Alpha / 255.0)).Append("</a:gs>");
        sb.Append("</a:gsLst>");
        if (gradient.Radial)
        {
            var (x, y) = (gradient.Focus.X, gradient.Focus.Y);
            sb.Append($"<a:path path=\"circle\"><a:fillToRect l=\"{N(Share(x))}\" t=\"{N(Share(y))}\" r=\"{N(Share(1 - x))}\" b=\"{N(Share(1 - y))}\"/></a:path>");
        }
        else
        {
            var angle = (long)Math.Round((gradient.Angle % 360 + 360) % 360 * 60000) % 21600000;
            sb.Append($"<a:lin ang=\"{N(angle)}\" scaled=\"0\"/>");
        }
        return sb.Append("</a:gradFill>").ToString();
    }

    /// <summary>A custom outline in a box of <paramref name="box"/>'s size, or a rectangle without one.</summary>
    private static string Geometry(PdfGeometry? geometry, SKRect box, Placement placement)
    {
        if (geometry is null)
            return Rect;
        var (width, height) = (Math.Max(1, placement.Length(box.Width)), Math.Max(1, placement.Length(box.Height)));
        var sb = new StringBuilder("<a:custGeom><a:avLst/><a:gdLst/><a:ahLst/><a:cxnLst/><a:rect l=\"0\" t=\"0\" r=\"r\" b=\"b\"/><a:pathLst>")
            .Append($"<a:path w=\"{N(width)}\" h=\"{N(height)}\">");
        string Point(SKPoint p) => $"<a:pt x=\"{N(placement.Length(p.X))}\" y=\"{N(placement.Length(p.Y))}\"/>";
        foreach (var (verb, points) in geometry.Commands)
        {
            switch (verb)
            {
                case SKPathVerb.Move:
                    sb.Append("<a:moveTo>").Append(Point(points[0])).Append("</a:moveTo>");
                    break;
                case SKPathVerb.Line:
                    sb.Append("<a:lnTo>").Append(Point(points[0])).Append("</a:lnTo>");
                    break;
                case SKPathVerb.Quad:
                    sb.Append("<a:quadBezTo>").Append(Point(points[0])).Append(Point(points[1])).Append("</a:quadBezTo>");
                    break;
                case SKPathVerb.Cubic:
                    sb.Append("<a:cubicBezTo>").Append(Point(points[0])).Append(Point(points[1])).Append(Point(points[2])).Append("</a:cubicBezTo>");
                    break;
                case SKPathVerb.Close:
                    sb.Append("<a:close/>");
                    break;
            }
        }
        return sb.Append("</a:path></a:pathLst></a:custGeom>").ToString();
    }

    /// <summary>"#1a2B3c" → "1A2B3C"; anything else black.</summary>
    internal static string Hex(string color)
    {
        var hex = color.TrimStart('#');
        return hex.Length == 6 && hex.All(Uri.IsHexDigit) ? hex.ToUpperInvariant() : "000000";
    }

    // ───────────────────────── Shapes ─────────────────────────

    private static void TextBox(StringBuilder sb, PdfSlideText text, int id, Placement placement, Func<string, string> link)
    {
        sb.Append($"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"TextBox {id}\"/><p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr>")
            .Append("<p:spPr>").Append(Transform(text.Bounds, placement, rotation: text.Rotation)).Append(Rect).Append("<a:noFill/></p:spPr>")
            .Append($"<p:txBody><a:bodyPr wrap=\"{(text.Wrap ? "square" : "none")}\" lIns=\"0\" tIns=\"0\" rIns=\"0\" bIns=\"0\" rtlCol=\"0\" anchor=\"t\">")
            .Append("<a:spAutoFit/></a:bodyPr><a:lstStyle/>");
        Paragraph(sb, text.Paragraph, placement, link);
        sb.Append("</p:txBody></p:sp>");
    }

    private static void Picture(StringBuilder sb, PdfSlidePicture picture, int id, Placement placement, string relationship)
    {
        sb.Append($"<p:pic><p:nvPicPr><p:cNvPr id=\"{id}\" name=\"Picture {id}\"/>")
            .Append("<p:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></p:cNvPicPr><p:nvPr/></p:nvPicPr>")
            .Append($"<p:blipFill><a:blip r:embed=\"{relationship}\"");
        var alpha = Share(picture.Alpha);
        sb.Append(alpha < 100000 ? $"><a:alphaModFix amt=\"{N(alpha)}\"/></a:blip>" : "/>");
        if (picture.Crop is { IsEmpty: false } crop)
            sb.Append($"<a:srcRect l=\"{N(Share(crop.Left))}\" t=\"{N(Share(crop.Top))}\" r=\"{N(Share(crop.Right))}\" b=\"{N(Share(crop.Bottom))}\"/>");
        sb.Append("<a:stretch><a:fillRect/></a:stretch></p:blipFill>")
            .Append("<p:spPr>").Append(Transform(picture.Bounds, placement, picture.FlipHorizontal, picture.FlipVertical, picture.Rotation))
            .Append(Geometry(picture.Geometry, picture.Bounds, placement)).Append("</p:spPr></p:pic>");
    }

    private static void Rectangle(StringBuilder sb, PdfSlideShape shape, int id, Placement placement) =>
        sb.Append($"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{(shape.Kind == PdfShapeKind.Custom ? "Freeform" : "Rectangle")} {id}\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>")
            .Append("<p:spPr>").Append(Transform(shape.Bounds, placement, rotation: shape.Rotation))
            .Append(Geometry(shape.Kind == PdfShapeKind.Custom ? shape.Geometry : null, shape.Bounds, placement))
            .Append(shape.Gradient is { } gradient ? Gradient(gradient) : shape.Fill is { } fill ? Solid(fill, shape.FillAlpha) : "<a:noFill/>")
            .Append(Outline(shape, placement)).Append("</p:spPr></p:sp>");

    private static void Line(StringBuilder sb, PdfSlideShape line, int id, Placement placement) =>
        sb.Append($"<p:cxnSp><p:nvCxnSpPr><p:cNvPr id=\"{id}\" name=\"Line {id}\"/><p:cNvCxnSpPr/><p:nvPr/></p:nvCxnSpPr>")
            .Append("<p:spPr>").Append(Transform(line.Bounds, placement, line.FlipHorizontal, line.FlipVertical))
            .Append("<a:prstGeom prst=\"line\"><a:avLst/></a:prstGeom>").Append(Outline(line, placement)).Append("</p:spPr></p:cxnSp>");

    private static string Outline(PdfSlideShape shape, Placement placement)
    {
        if (shape.Line is not { } color)
            return "<a:ln><a:noFill/></a:ln>";
        // A width of 0 is the thinnest line a device shows; a quarter point is PowerPoint's thinnest.
        var width = Math.Max(3175, placement.Length(shape.LineWidth));
        return $"<a:ln w=\"{N(width)}\">{Solid(color, shape.LineAlpha)}{(shape.Dashed ? "<a:prstDash val=\"dash\"/>" : "")}</a:ln>";
    }

    // ───────────────────────── Tables ─────────────────────────

    private static void Table(StringBuilder sb, PdfSlideTable slideTable, int id, Placement placement, Func<string, string> link)
    {
        var table = slideTable.Table;
        var columns = slideTable.Columns.Length;
        var rows = slideTable.Rows.Length;
        // Which cell starts at or covers each place of the grid.
        var owners = new (HCell Cell, int Row, int Column)?[rows, columns];
        for (var r = 0; r < Math.Min(rows, table.Rows.Count); r++)
        {
            foreach (var cell in table.Rows[r].Cells)
            {
                var c = cell.Column;
                if (c < 0 || c >= columns || owners[r, c] is not null)
                    continue;
                for (var dr = 0; dr < Math.Max(1, cell.RowSpan) && r + dr < rows; dr++)
                {
                    for (var dc = 0; dc < Math.Max(1, cell.ColSpan) && c + dc < columns; dc++)
                        owners[r + dr, c + dc] ??= (cell, r, c);
                }
            }
        }

        var width = slideTable.Columns.Sum();
        var height = slideTable.Rows.Sum();
        sb.Append($"<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"{id}\" name=\"Table {id}\"/>")
            .Append("<p:cNvGraphicFramePr><a:graphicFrameLocks noGrp=\"1\"/></p:cNvGraphicFramePr><p:nvPr/></p:nvGraphicFramePr>")
            .Append($"<p:xfrm><a:off x=\"{N(placement.Left(slideTable.Bounds.Left))}\" y=\"{N(placement.Top(slideTable.Bounds.Top))}\"/>")
            .Append($"<a:ext cx=\"{N(placement.Length(width))}\" cy=\"{N(placement.Length(height))}\"/></p:xfrm>")
            .Append($"<a:graphic><a:graphicData uri=\"{TableUri}\"><a:tbl><a:tblPr/><a:tblGrid>");
        foreach (var column in slideTable.Columns)
            sb.Append($"<a:gridCol w=\"{N(Math.Max(1, placement.Length(column)))}\"/>");
        sb.Append("</a:tblGrid>");

        var margin = table.CellMargin ?? new HInsets(100, 100, 100, 100);
        for (var r = 0; r < rows; r++)
        {
            sb.Append($"<a:tr h=\"{N(Math.Max(1, placement.Length(slideTable.Rows[r])))}\">");
            for (var c = 0; c < columns; c++)
            {
                if (owners[r, c] is not { } owner)
                {
                    sb.Append("<a:tc><a:txBody><a:bodyPr/><a:lstStyle/><a:p/></a:txBody><a:tcPr/></a:tc>");
                    continue;
                }
                if (owner.Row != r || owner.Column != c)
                {
                    sb.Append("<a:tc").Append(owner.Column != c ? " hMerge=\"1\"" : "").Append(owner.Row != r ? " vMerge=\"1\"" : "")
                        .Append("><a:txBody><a:bodyPr/><a:lstStyle/><a:p/></a:txBody><a:tcPr/></a:tc>");
                    continue;
                }
                var cell = owner.Cell;
                var rowSpan = Math.Min(Math.Max(1, cell.RowSpan), rows - r);
                var colSpan = Math.Min(Math.Max(1, cell.ColSpan), columns - c);
                sb.Append("<a:tc").Append(colSpan > 1 ? $" gridSpan=\"{colSpan}\"" : "").Append(rowSpan > 1 ? $" rowSpan=\"{rowSpan}\"" : "")
                    .Append("><a:txBody><a:bodyPr/><a:lstStyle/>");
                var paragraphs = cell.Blocks.OfType<HParagraph>().ToList();
                if (paragraphs.Count == 0)
                    sb.Append("<a:p/>");
                foreach (var paragraph in paragraphs)
                    Paragraph(sb, paragraph, placement, link);
                var insets = cell.CellMargin ?? margin;
                var anchor = cell.VerticalAlign switch { HVerticalAlign.Center => "ctr", HVerticalAlign.Bottom => "b", _ => "t" };
                sb.Append($"</a:txBody><a:tcPr marL=\"{N(Hwp(insets.Left, placement))}\" marR=\"{N(Hwp(insets.Right, placement))}\" ")
                    .Append($"marT=\"{N(Hwp(insets.Top, placement))}\" marB=\"{N(Hwp(insets.Bottom, placement))}\" anchor=\"{anchor}\">");
                var borders = cell.Borders ?? table.Borders ?? HBorders.Empty;
                sb.Append(Border("lnL", borders.Left, placement)).Append(Border("lnR", borders.Right, placement))
                    .Append(Border("lnT", borders.Top, placement)).Append(Border("lnB", borders.Bottom, placement))
                    .Append(cell.Fill is { } fill ? Solid(fill) : "<a:noFill/>")
                    .Append("</a:tcPr></a:tc>");
            }
            sb.Append("</a:tr>");
        }
        sb.Append("</a:tbl></a:graphicData></a:graphic></p:graphicFrame>");
    }

    /// <summary>A length in HWPUNIT (1/100 pt) in EMU on the slide.</summary>
    private static long Hwp(int value, Placement placement) => Math.Max(0, placement.Length(value / 100.0));

    private static string Border(string name, HBorder border, Placement placement)
    {
        if (border.Style == HBorderStyle.None)
            return $"<a:{name} w=\"0\"><a:noFill/></a:{name}>";
        var width = Math.Max(3175, placement.Length(border.WidthMm / 25.4 * 72));
        var compound = border.Style == HBorderStyle.Double ? "dbl" : "sng";
        var dash = border.Style switch { HBorderStyle.Dash => "dash", HBorderStyle.Dot => "sysDot", HBorderStyle.DashDot => "dashDot", _ => "solid" };
        return $"<a:{name} w=\"{N(width)}\" cmpd=\"{compound}\">{Solid(border.Color)}<a:prstDash val=\"{dash}\"/></a:{name}>";
    }

    // ───────────────────────── Text ─────────────────────────

    private static void Paragraph(StringBuilder sb, HParagraph paragraph, Placement placement, Func<string, string> link)
    {
        var format = paragraph.Format;
        var size = paragraph.MarkFormat.Size ?? paragraph.Inlines.OfType<HText>().Select(t => t.Format.Size).FirstOrDefault(s => s is not null) ?? 1800;
        var align = format.Align switch
        {
            HAlign.Center => "ctr",
            HAlign.Right => "r",
            HAlign.Justify => "just",
            HAlign.Distribute => "dist",
            _ => "l",
        };
        sb.Append($"<a:p><a:pPr algn=\"{align}\"");
        if (format.Left is > 0 and var left)
            sb.Append($" marL=\"{N(Hwp(left, placement))}\"");
        if (format.FirstLine is { } first && first != 0)
            sb.Append($" indent=\"{N(Math.Sign(first) * Hwp(Math.Abs(first), placement))}\"");
        if (format.RightToLeft == true)
            sb.Append(" rtl=\"1\"");
        sb.Append('>');
        // The PDF's distance between baselines as a share of single spacing.
        if (format.LineSpacing is { Kind: HLineSpacingKind.AtLeast or HLineSpacingKind.Fixed, Value: var pitch } && size > 0)
        {
            var share = Math.Clamp(pitch / (size * SingleSpacing), 0.5, 3);
            if (Math.Abs(share - 1) > 0.03)
                sb.Append($"<a:lnSpc><a:spcPct val=\"{N((long)Math.Round(share * 100000))}\"/></a:lnSpc>");
        }
        sb.Append("<a:spcBef><a:spcPts val=\"0\"/></a:spcBef><a:spcAft><a:spcPts val=\"0\"/></a:spcAft><a:buNone/>");
        if (format.Tabs is { Count: > 0 } tabs)
        {
            sb.Append("<a:tabLst>");
            foreach (var tab in tabs.Take(32))
            {
                var kind = tab.Kind switch { HTabKind.Right => "r", HTabKind.Center => "ctr", HTabKind.Decimal => "dec", _ => "l" };
                sb.Append($"<a:tab pos=\"{N(Hwp(tab.Position, placement))}\" algn=\"{kind}\"/>");
            }
            sb.Append("</a:tabLst>");
        }
        sb.Append("</a:pPr>");
        Runs(sb, paragraph.Inlines, placement, link, null);
        sb.Append("<a:endParaRPr").Append(Size(size, placement)).Append(" dirty=\"0\"/></a:p>");
    }

    private static void Runs(StringBuilder sb, IEnumerable<HInline> inlines, Placement placement, Func<string, string> link, string? linkId)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case HText text when text.Text.Length > 0:
                    sb.Append("<a:r>").Append(Properties(text.Format, placement, linkId)).Append($"<a:t>{Escape(text.Text)}</a:t></a:r>");
                    break;
                case HTab tab:
                    sb.Append("<a:r>").Append(Properties(tab.Format, placement, linkId)).Append("<a:t>\t</a:t></a:r>");
                    break;
                case HLineBreak lineBreak:
                    sb.Append("<a:br>").Append(Properties(lineBreak.Format, placement, null)).Append("</a:br>");
                    break;
                case HLink hyperlink:
                    var web = Uri.TryCreate(hyperlink.Target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto";
                    Runs(sb, hyperlink.Content, placement, link, web ? link(hyperlink.Target) : null);
                    break;
            }
        }
    }

    private static string Size(int? size, Placement placement) =>
        size is { } value ? $" sz=\"{Math.Clamp(placement.Hundredths(value), 100, 400000).ToString(CultureInfo.InvariantCulture)}\"" : "";

    private static string Properties(HCharFormat format, Placement placement, string? linkId)
    {
        var sb = new StringBuilder("<a:rPr lang=\"en-US\"").Append(Size(format.Size, placement));
        if (format.Bold == true)
            sb.Append(" b=\"1\"");
        if (format.Italic == true)
            sb.Append(" i=\"1\"");
        if (format.Underline == true)
            sb.Append(" u=\"sng\"");
        if (format.Strike == true)
            sb.Append(" strike=\"sngStrike\"");
        if (format.Superscript == true)
            sb.Append(" baseline=\"30000\"");
        else if (format.Subscript == true)
            sb.Append(" baseline=\"-25000\"");
        sb.Append(" dirty=\"0\">");
        if (format.Color is { } color)
            sb.Append(Solid(color));
        if (format.Font is { } font)
        {
            var name = Escape(font);
            sb.Append($"<a:latin typeface=\"{name}\"/><a:ea typeface=\"{Escape(format.EastAsianFont ?? font)}\"/><a:cs typeface=\"{name}\"/>");
        }
        if (linkId is not null)
            sb.Append($"<a:hlinkClick r:id=\"{linkId}\"/>");
        return sb.Append("</a:rPr>").ToString();
    }
}
