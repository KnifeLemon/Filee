// Objects in the text of an RTF file: pictures as \pict groups, text boxes and shapes as Word drawing objects (\shp).
//
// A picture keeps its PNG or JPEG bytes (\pngblip, \jpegblip), an EMF or WMF stays a metafile (\emfblip, \wmetafile8
// without the placeable header WMF files start with); any other picture becomes PNG. Its bytes are hex, 64 to a line.
// Pictures stay in the text like characters (floating ones too, where they were anchored).
//
// A text box is a shape (\shp) with its paragraphs in \shptxt, and the same text in \shprslt for programs that don't
// draw shapes (RtfReader reads it from there); shapes without text have no result.

using Filee.Engines.Documents;
using Filee.Engines.Magick;
using ImageMagick;

namespace Filee.Engines.Hwp.Hwpx;

internal sealed partial class RtfWriter
{
    // ───────────────────────── Pictures ─────────────────────────

    private void Picture(HImage image)
    {
        if (!File.Exists(image.Path) || PictureData(image.Path) is not { } picture)
            return; // missing or unreadable picture: skip rather than write a broken one

        var (width, height) = (image.Width, image.Height);
        if ((width is null || height is null) && picture.Pixels is { } pixels && pixels.Width > 0 && pixels.Height > 0)
        {
            // Pixels at 96 dpi, like Word and browsers show pictures without a physical size.
            if (width is null && height is null)
                (width, height) = ((int)(pixels.Width * HwpxUnits.PerPixel), (int)(pixels.Height * HwpxUnits.PerPixel));
            else if (width is not null)
                height = (int)(width.Value * (double)pixels.Height / pixels.Width);
            else
                width = (int)(height!.Value * (double)pixels.Width / pixels.Height);
        }
        var w = Math.Max(1, width ?? 8504);
        var h = Math.Max(1, height ?? w);
        if (image.Anchor is null && w > _width)
        {
            // Pictures in the text never overflow its width.
            h = (int)((long)h * _width / w);
            w = _width;
        }

        var o = _out;
        o.Open();
        // \picw and \pich: pixels for bitmaps, 0.01 mm for metafiles.
        var (picW, picH) = picture.Blip is "emfblip" or "wmetafile8"
            ? ((int)Math.Round(w / HwpxUnits.PerMm * 100), (int)Math.Round(h / HwpxUnits.PerMm * 100))
            : (picture.Pixels?.Width ?? Math.Max(1, (int)(w / HwpxUnits.PerPixel)), picture.Pixels?.Height ?? Math.Max(1, (int)(h / HwpxUnits.PerPixel)));
        o.Control($"\\pict\\{picture.Blip}\\picw{Number(picW)}\\pich{Number(picH)}\\picwgoal{Number(Twips(w))}\\pichgoal{Number(Twips(h))}");
        o.Line();
        var data = picture.Bytes;
        const int PerLine = 64;
        for (var i = 0; i < data.Length; i += PerLine)
        {
            _token.ThrowIfCancellationRequested();
            o.Raw(Convert.ToHexString(data, i, Math.Min(PerLine, data.Length - i)).ToLowerInvariant());
            o.Line();
        }
        o.Close();
    }

    /// <summary>A picture's bytes as RTF takes them, its blip word and its pixel size; null when it can't be written.</summary>
    private sealed record PictureBytes(byte[] Bytes, string Blip, (int Width, int Height)? Pixels);

    private readonly Dictionary<string, PictureBytes?> _pictures = new(StringComparer.OrdinalIgnoreCase);

    private PictureBytes? PictureData(string path)
    {
        if (_pictures.TryGetValue(path, out var known))
            return known;
        return _pictures[path] = ReadPicture(path);
    }

    private PictureBytes? ReadPicture(string path)
    {
        // A picture larger than a part of a document may be, or claiming more pixels than any picture has, is left
        // out before anything decodes it (RtfReader refuses such pictures too).
        if (new FileInfo(path).Length > _limits.MaxPartBytes)
            return null;
        var pixels = PictureSize.Read(path);
        if (pixels is { } size && (long)size.Width * size.Height > _limits.MaxPicturePixels)
            return null;
        var bytes = File.ReadAllBytes(path);
        if (bytes is [0x89, (byte)'P', (byte)'N', (byte)'G', ..])
            return new PictureBytes(bytes, "pngblip", pixels);
        if (bytes is [0xFF, 0xD8, 0xFF, ..])
            return new PictureBytes(bytes, "jpegblip", pixels);
        if (bytes.Length > 44 && bytes[0] == 1 && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 0 && bytes[40] == 0x20 && bytes[41] == 0x45 && bytes[42] == 0x4D && bytes[43] == 0x46)
            return new PictureBytes(bytes, "emfblip", pixels);
        if (bytes is [0xD7, 0xCD, 0xC6, 0x9A, ..] && bytes.Length > 22)
            return new PictureBytes(bytes[22..], "wmetafile8", pixels); // RTF has the metafile without its placeable header
        if (bytes is [1 or 2, 0, 9, 0, ..])
            return new PictureBytes(bytes, "wmetafile8", pixels);

        // GIF, BMP, WebP, TIFF ... → PNG, which every RTF reader shows.
        try
        {
            var info = new MagickImageInfo(path);
            if ((long)info.Width * info.Height > _limits.MaxPicturePixels)
                return null;
            using var image = new MagickImage(path);
            DdsChannelOrder.Correct(path, image);
            return new PictureBytes(image.ToByteArray(MagickFormat.Png), "pngblip", ((int)image.Width, (int)image.Height));
        }
        catch (MagickException)
        {
            return null;
        }
    }

    // ───────────────────────── Text boxes and shapes ─────────────────────────

    private void TextBox(HTextBox box, RunState state, StyleBase style)
    {
        var o = _out;
        var w = Math.Max(1, box.Width);
        var h = Math.Max(1, box.Height);
        o.Open();
        o.Control("\\shp");
        o.Open();
        o.Control("\\*\\shpinst" + ShapePlacement(box.Anchor, w, h, box.ZOrder));
        var type = box.Shape == HShapeKind.Ellipse ? 3 : box.CornerRatio > 0 ? 2 : 202;
        Property("shapeType", Number(type));
        Fill(box.Fill ?? box.Gradient?.Colors.FirstOrDefault());
        Line(box.Line);
        Property("dxTextLeft", Number(Emu(box.Padding.Left)));
        Property("dxTextRight", Number(Emu(box.Padding.Right)));
        Property("dyTextTop", Number(Emu(box.Padding.Top)));
        Property("dyTextBottom", Number(Emu(box.Padding.Bottom)));
        Property("anchorText", box.VerticalAlign switch { HVerticalAlign.Center => "1", HVerticalAlign.Bottom => "2", _ => "0" });
        if (box.CornerRatio > 0)
            Property("adjustValue", Number((int)Math.Round(Math.Clamp(box.CornerRatio, 0, 50) / 100.0 * 21600)));
        Transform(box.Transform);

        var outer = _width;
        _width = Math.Max(1000, w - box.Padding.Left - box.Padding.Right);
        o.Open();
        o.Control("\\shptxt");
        Blocks(box.Blocks.Count > 0 ? box.Blocks : [new HParagraph()], Story.TextBox, 0, BlockEnd.Story);
        o.Close();
        _width = outer;
        o.Close();

        // The same text, in the paragraph, for programs that don't draw shapes: its paragraphs as lines.
        o.Open();
        o.Control("\\shprslt");
        var first = true;
        foreach (var paragraph in box.Blocks.OfType<HParagraph>())
        {
            if (!first)
                o.Control("\\line");
            first = false;
            Inlines(paragraph.Inlines.Where(i => i is HText or HTab or HLineBreak or HLink).ToList(), state, style, Story.TextBox, 0);
        }
        o.Close();
        o.Close();
    }

    private void Shape(HShape shape)
    {
        var type = shape.Kind switch
        {
            HShapeKind.Rectangle => shape.CornerRatio > 0 ? 2 : 1,
            HShapeKind.Ellipse => 3,
            HShapeKind.Line => 20,
            _ => 0, // polygons, curves and arcs come only from models read for layout
        };
        if (type == 0)
            return;
        var o = _out;
        var w = Math.Max(1, shape.Width);
        var h = Math.Max(shape.Kind == HShapeKind.Line ? 0 : 1, shape.Height);
        o.Open();
        o.Control("\\shp");
        o.Open();
        o.Control("\\*\\shpinst" + ShapePlacement(shape.Anchor, w, h, shape.ZOrder));
        Property("shapeType", Number(type));
        if (shape.Kind == HShapeKind.Line)
            Property("fFilled", "0");
        else
            Fill(shape.Fill ?? shape.Gradient?.Colors.FirstOrDefault());
        Line(shape.Line);
        if (shape.Kind == HShapeKind.Rectangle && shape.CornerRatio > 0)
            Property("adjustValue", Number((int)Math.Round(Math.Clamp(shape.CornerRatio, 0, 50) / 100.0 * 21600)));
        if (shape.FlipHorizontal)
            Property("fFlipH", "1");
        if (shape.FlipVertical)
            Property("fFlipV", "1");
        Transform(shape.Transform);
        o.Close();
        o.Close();
    }

    /// <summary>
    /// \shpleft ... \shpbottom with what they are measured from, the wrap and the stacking order. An object in the
    /// text (no anchor) is placed at its paragraph, with text above and below it.
    /// </summary>
    private string ShapePlacement(HAnchor? anchor, int width, int height, int? zOrder)
    {
        var x = anchor is null ? 0 : Twips(anchor.HorizontalOffset);
        var y = anchor is null ? 0 : Twips(anchor.VerticalOffset);
        var horizontal = anchor?.HorizontalRelativeTo switch
        {
            "PAPER" => "\\shpbxpage",
            "PAGE" => "\\shpbxmargin",
            _ => "\\shpbxcolumn",
        };
        var vertical = anchor?.VerticalRelativeTo switch
        {
            "PAPER" => "\\shpbypage",
            "PAGE" => "\\shpbymargin",
            _ => "\\shpbypara",
        };
        var wrap = anchor?.Wrap switch
        {
            null or "TOP_AND_BOTTOM" => "\\shpwr1",
            "BEHIND_TEXT" => "\\shpwr3\\shpfblwtxt1",
            "IN_FRONT_OF_TEXT" => "\\shpwr3\\shpfblwtxt0",
            _ => "\\shpwr2\\shpwrk0",
        };
        var id = ++_shapes;
        return $"\\shpleft{Number(x)}\\shptop{Number(y)}\\shpright{Number(x + Twips(width))}\\shpbottom{Number(y + Twips(height))}" +
               $"\\shpfhdr0{horizontal}\\shpbxignore{vertical}\\shpbyignore{wrap}\\shpz{Number(Math.Max(0, zOrder ?? id))}\\shplid{Number(1024 + id)}" +
               PositionProperties(anchor);
    }

    private int _shapes;

    /// <summary>An aligned anchor (centre, right ... without an offset) as Word's posh / posv shape properties.</summary>
    private static string PositionProperties(HAnchor? anchor)
    {
        if (anchor is null)
            return "";
        var sb = new System.Text.StringBuilder();
        var posh = anchor.HorizontalOffset != 0 ? 0 : anchor.HorizontalAlign switch
        {
            "CENTER" => 2,
            "RIGHT" => 3,
            "INSIDE" => 4,
            "OUTSIDE" => 5,
            _ => 0,
        };
        if (posh > 0)
            sb.Append($"{{\\sp{{\\sn posh}}{{\\sv {Number(posh)}}}}}");
        var posv = anchor.VerticalOffset != 0 ? 0 : anchor.VerticalAlign switch
        {
            "CENTER" => 2,
            "BOTTOM" => 3,
            "INSIDE" => 4,
            "OUTSIDE" => 5,
            _ => 0,
        };
        if (posv > 0)
            sb.Append($"{{\\sp{{\\sn posv}}{{\\sv {Number(posv)}}}}}");
        return sb.ToString();
    }

    /// <summary>A shape property: {\sp{\sn name}{\sv value}}.</summary>
    private void Property(string name, string value)
    {
        _out.Control($"{{\\sp{{\\sn {name}}}{{\\sv {value}}}}}");
    }

    private void Fill(string? color)
    {
        if (Odt.Odf.Color(color) is { } fill)
        {
            Property("fFilled", "1");
            Property("fillColor", Number(Bgr(fill)));
        }
        else
        {
            Property("fFilled", "0");
        }
    }

    private void Line(HBorder line)
    {
        if (line.Style == HBorderStyle.None)
        {
            Property("fLine", "0");
            return;
        }
        Property("fLine", "1");
        Property("lineColor", Number(Bgr(Odt.Odf.Color(line.Color) ?? "#000000")));
        Property("lineWidth", Number((int)Math.Round(Math.Max(0, line.WidthMm) * 36000)));
        var dash = line.Style switch
        {
            HBorderStyle.Dash => 1,
            HBorderStyle.Dot => 2,
            HBorderStyle.DashDot => 3,
            _ => 0,
        };
        if (dash > 0)
            Property("lineDashing", Number(dash));
        if (line.Style == HBorderStyle.Double)
            Property("lineStyle", "1");
    }

    private void Transform(HTransform? transform)
    {
        if (transform is not { IsIdentity: false } t)
            return;
        if (t.Rotation != 0)
            Property("rotation", Number((int)Math.Round(t.Rotation * 65536)));
        if (t.FlipHorizontal)
            Property("fFlipH", "1");
        if (t.FlipVertical)
            Property("fFlipV", "1");
    }

    /// <summary>"#RRGGBB" as the BGR number shape properties take.</summary>
    private static int Bgr(string hex)
    {
        var value = Convert.ToInt32(hex[1..], 16);
        return (value >> 16 & 0xFF) | (value & 0xFF00) | (value & 0xFF) << 16;
    }

    /// <summary>HWPUNIT → EMU (shape properties' lengths).</summary>
    private static int Emu(int hwpUnit) => (int)Math.Min(int.MaxValue, Math.Round(hwpUnit * 127.0));
}
