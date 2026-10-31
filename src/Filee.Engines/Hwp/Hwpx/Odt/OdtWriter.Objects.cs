// Objects in the text of an ODT: pictures and text boxes as draw:frame, shapes as draw:rect / draw:ellipse /
// draw:line, each with an automatic graphic style that holds its position, wrap, fill and outline.
//
// Objects in the text stay characters (text:anchor-type="as-char"); floating ones are anchored to their paragraph, or
// to a character when the model places them relative to the paragraph's text, with the horizontal and vertical
// relation (page, page-content, paragraph) the model's anchor names.

using System.Text;
using ImageMagick;

namespace Filee.Engines.Hwp.Hwpx.Odt;

internal sealed partial class OdtWriter
{
    // ───────────────────────── Pictures ─────────────────────────

    private string Picture(HImage image)
    {
        if (!File.Exists(image.Path) || MediaTarget(image.Path) is not { } target)
            return ""; // missing or unreadable picture: skip rather than write a broken reference

        var (width, height) = (image.Width, image.Height);
        if (width is null || height is null)
        {
            try
            {
                // Pixels at 96 dpi, like Word and browsers show pictures without a physical size.
                var info = new MagickImageInfo(image.Path);
                double pxW = info.Width, pxH = info.Height;
                if (pxW > 0 && pxH > 0)
                {
                    if (width is null && height is null)
                        (width, height) = ((int)(pxW * HwpxUnits.PerPixel), (int)(pxH * HwpxUnits.PerPixel));
                    else if (width is not null)
                        height = (int)(width.Value * pxH / pxW);
                    else
                        width = (int)(height!.Value * pxW / pxH);
                }
            }
            catch (MagickException)
            {
                // Unknown size: a square placeholder size below.
            }
        }
        var w = Math.Max(1, width ?? 8504);
        var h = Math.Max(1, height ?? w);
        if (image.Anchor is null && image.Width is null && w > _width)
        {
            // Pictures of their natural size never overflow the text width.
            h = (int)((long)h * _width / w);
            w = _width;
        }

        var props = new StringBuilder(" draw:fill=\"none\" style:mirror=\"none\"")
            .Append(BorderAttribute(image.Line))
            .Append(PaddingAttributes(image.Margin));
        var content = $"<draw:image xlink:href=\"{Escape(target)}\" xlink:type=\"simple\" xlink:show=\"embed\" xlink:actuate=\"onLoad\"/>";
        return Frame("Image", image.Anchor, image.OuterMargin, image.ZOrder, props.ToString(), w, h, content);
    }

    // ───────────────────────── Text boxes and shapes ─────────────────────────

    private string TextBox(HTextBox box)
    {
        var w = Math.Max(1, box.Width);
        var h = Math.Max(1, box.Height);
        var content = Nested(Math.Max(1000, w - box.Padding.Left - box.Padding.Right), () => Blocks(box.Blocks, "Standard", Context.TextBox));
        var align = box.VerticalAlign switch
        {
            HVerticalAlign.Center => "middle",
            HVerticalAlign.Bottom => "bottom",
            _ => "top",
        };
        if (box.Shape == HShapeKind.Ellipse || box.CornerRatio > 0)
        {
            // Round outlines are shapes with paragraphs in them; their outline is a stroke.
            var shapeProps = new StringBuilder(Fill(box.Fill ?? box.Gradient?.Colors.FirstOrDefault()))
                .Append(Stroke(box.Line))
                .Append(PaddingAttributes(box.Padding))
                .Append($" draw:textarea-vertical-align=\"{align}\"");
            var element = box.Shape == HShapeKind.Ellipse ? "ellipse" : "rect";
            var corner = element == "rect" ? $" draw:corner-radius=\"{Odf.Points(Corner(box.CornerRatio, w, h))}\"" : "";
            return ShapeElement(element, "Shape", box.Anchor, box.OuterMargin, box.ZOrder, shapeProps.ToString(), w, h, corner, content);
        }
        var props = new StringBuilder(Fill(box.Fill ?? box.Gradient?.Colors.FirstOrDefault(), background: true))
            .Append(BorderAttribute(box.Line))
            .Append(PaddingAttributes(box.Padding))
            .Append($" draw:textarea-vertical-align=\"{align}\"");
        return Frame("Frame", box.Anchor, box.OuterMargin, box.ZOrder, props.ToString(), w, h, $"<draw:text-box>{content}</draw:text-box>");
    }

    private string Shape(HShape shape)
    {
        var w = Math.Max(0, shape.Width);
        var h = Math.Max(0, shape.Height);
        var props = new StringBuilder(shape.Kind == HShapeKind.Line ? " draw:fill=\"none\"" : Fill(shape.Fill ?? shape.Gradient?.Colors.FirstOrDefault()))
            .Append(Stroke(shape.Line));
        switch (shape.Kind)
        {
            case HShapeKind.Line:
                {
                    var (style, anchorType, x, y) = Placement(shape.Anchor, shape.OuterMargin, props.ToString(), "gr");
                    var x1 = x + (shape.FlipHorizontal ? w : 0);
                    var x2 = x + (shape.FlipHorizontal ? 0 : w);
                    var y1 = y + (shape.FlipVertical ? h : 0);
                    var y2 = y + (shape.FlipVertical ? 0 : h);
                    return $"<draw:line draw:style-name=\"{style}\" draw:name=\"Shape{Number(++_frames)}\" text:anchor-type=\"{anchorType}\"{ZIndex(shape.ZOrder)} " +
                           $"svg:x1=\"{Odf.Points(x1)}\" svg:y1=\"{Odf.Points(y1)}\" svg:x2=\"{Odf.Points(x2)}\" svg:y2=\"{Odf.Points(y2)}\"/>";
                }
            case HShapeKind.Rectangle or HShapeKind.Ellipse:
                {
                    var element = shape.Kind == HShapeKind.Ellipse ? "ellipse" : "rect";
                    var corner = element == "rect" && shape.CornerRatio > 0 ? $" draw:corner-radius=\"{Odf.Points(Corner(shape.CornerRatio, w, h))}\"" : "";
                    return ShapeElement(element, "Shape", shape.Anchor, shape.OuterMargin, shape.ZOrder, props.ToString(), Math.Max(1, w), Math.Max(1, h), corner, "");
                }
            default:
                return ""; // polygons, curves and arcs come only from models read for layout
        }
    }

    /// <summary>Corner radius of a rounded rectangle from the model's ratio (percent of the shorter side).</summary>
    private static int Corner(int ratio, int width, int height) => (int)Math.Round(Math.Clamp(ratio, 0, 50) / 100.0 * Math.Min(width, height));

    // ───────────────────────── Frames ─────────────────────────

    private string Frame(string kind, HAnchor? anchor, HInsets outer, int? zOrder, string graphicProps, int width, int height, string content)
    {
        var (style, anchorType, x, y) = Placement(anchor, outer, graphicProps);
        var position = anchor is null ? "" : $" svg:x=\"{Odf.Points(x)}\" svg:y=\"{Odf.Points(y)}\"";
        return $"<draw:frame draw:style-name=\"{style}\" draw:name=\"{kind}{Number(++_frames)}\" text:anchor-type=\"{anchorType}\"{position} " +
               $"svg:width=\"{Odf.Points(width)}\" svg:height=\"{Odf.Points(height)}\"{ZIndex(zOrder)}>{content}</draw:frame>";
    }

    private string ShapeElement(string element, string kind, HAnchor? anchor, HInsets outer, int? zOrder, string graphicProps, int width, int height, string extra, string content)
    {
        var (style, anchorType, x, y) = Placement(anchor, outer, graphicProps, "gr");
        var position = anchor is null ? "" : $" svg:x=\"{Odf.Points(x)}\" svg:y=\"{Odf.Points(y)}\"";
        return $"<draw:{element} draw:style-name=\"{style}\" draw:name=\"{kind}{Number(++_frames)}\" text:anchor-type=\"{anchorType}\"{position} " +
               $"svg:width=\"{Odf.Points(width)}\" svg:height=\"{Odf.Points(height)}\"{extra}{ZIndex(zOrder)}>{content}</draw:{element}>";
    }

    private static string ZIndex(int? zOrder) => zOrder is { } z ? $" draw:z-index=\"{Number(Math.Max(0, z))}\"" : "";

    /// <summary>
    /// The graphic style of an object with its position and wrap, its anchor type and its offsets. Objects in the text
    /// sit on the baseline; floating ones relate to the page, its text area or the paragraph (or its text, anchored to a
    /// character), aligned or at an offset from the left / top.
    /// </summary>
    private (string Style, string AnchorType, int X, int Y) Placement(HAnchor? anchor, HInsets outer, string graphicProps, string kind = "fr")
    {
        var props = new StringBuilder(graphicProps);
        props.Append($" fo:margin-left=\"{Odf.Points(outer.Left)}\" fo:margin-right=\"{Odf.Points(outer.Right)}\"")
             .Append($" fo:margin-top=\"{Odf.Points(outer.Top)}\" fo:margin-bottom=\"{Odf.Points(outer.Bottom)}\"");
        if (anchor is null)
        {
            props.Append(" style:vertical-pos=\"top\" style:vertical-rel=\"baseline\"");
            return (_styles.Add("graphic", kind, "", $"<style:graphic-properties{props}/>"), "as-char", 0, 0);
        }

        var anchorType = anchor.HorizontalRelativeTo == "PARA" ? "char" : "paragraph";
        var (horizontalPos, x) = anchor.HorizontalAlign switch
        {
            "CENTER" when anchor.HorizontalOffset == 0 => ("center", 0),
            "RIGHT" when anchor.HorizontalOffset == 0 => ("right", 0),
            "INSIDE" when anchor.HorizontalOffset == 0 => ("inside", 0),
            "OUTSIDE" when anchor.HorizontalOffset == 0 => ("outside", 0),
            _ => ("from-left", anchor.HorizontalOffset),
        };
        var horizontalRel = anchor.HorizontalRelativeTo switch
        {
            "PAPER" => "page",
            "PAGE" => "page-content",
            "PARA" => "char",
            _ => "paragraph",
        };
        var (verticalPos, y) = anchor.VerticalAlign switch
        {
            "CENTER" when anchor.VerticalOffset == 0 => ("middle", 0),
            "BOTTOM" or "OUTSIDE" when anchor.VerticalOffset == 0 => ("bottom", 0),
            _ => ("from-top", anchor.VerticalOffset),
        };
        var verticalRel = anchor.VerticalRelativeTo switch
        {
            "PAPER" => "page",
            "PAGE" => "page-content",
            _ => "paragraph",
        };
        var wrap = anchor.Wrap switch
        {
            "TOP_AND_BOTTOM" => " style:wrap=\"none\"",
            "BEHIND_TEXT" => " style:wrap=\"run-through\" style:run-through=\"background\"",
            "IN_FRONT_OF_TEXT" => " style:wrap=\"run-through\" style:run-through=\"foreground\"",
            _ => " style:wrap=\"parallel\" style:number-wrapped-paragraphs=\"no-limit\"",
        };
        props.Append(wrap)
             .Append($" style:horizontal-pos=\"{horizontalPos}\" style:horizontal-rel=\"{horizontalRel}\"")
             .Append($" style:vertical-pos=\"{verticalPos}\" style:vertical-rel=\"{verticalRel}\"");
        return (_styles.Add("graphic", kind, "", $"<style:graphic-properties{props}/>"), anchorType, x, y);
    }

    /// <summary>
    /// The fill of an object: solid in its colour or none. <paramref name="background"/> also gives the older
    /// fo:background-color that frames had before draw:fill.
    /// </summary>
    private static string Fill(string? color, bool background = false) => Hex(color) is { } fill
        ? $" draw:fill=\"solid\" draw:fill-color=\"{fill}\"{(background ? $" fo:background-color=\"{fill}\"" : "")}"
        : $" draw:fill=\"none\"{(background ? " fo:background-color=\"transparent\"" : "")}";

    /// <summary>The outline of a shape (draw:stroke, its width and colour).</summary>
    private static string Stroke(HBorder line)
    {
        if (line.Style == HBorderStyle.None)
            return " draw:stroke=\"none\"";
        var width = Math.Max(0, (int)Math.Round(line.WidthMm * HwpxUnits.PerMm));
        var stroke = line.Style is HBorderStyle.Dash or HBorderStyle.Dot or HBorderStyle.DashDot ? "dash" : "solid";
        return $" draw:stroke=\"{stroke}\" svg:stroke-width=\"{Odf.Points(width)}\" svg:stroke-color=\"{Hex(line.Color) ?? "#000000"}\"";
    }

    /// <summary>The border of a frame (fo:border).</summary>
    private static string BorderAttribute(HBorder line) =>
        line.Style == HBorderStyle.None ? " fo:border=\"none\"" : $" fo:border=\"{Border(line)}\"";

    private static string PaddingAttributes(HInsets padding) =>
        $" fo:padding-left=\"{Odf.Points(padding.Left)}\" fo:padding-right=\"{Odf.Points(padding.Right)}\"" +
        $" fo:padding-top=\"{Odf.Points(padding.Top)}\" fo:padding-bottom=\"{Odf.Points(padding.Bottom)}\"";
}
