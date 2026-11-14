// Styles of the ODP writer: automatic styles (one per distinct set of properties, in content.xml for the pages and in
// styles.xml for the masters), the named drawing styles they refer to in office:styles (gradients, transparencies,
// hatches, fill images, line dashes, line end markers, each once), and the pictures of the package.
//
// - Fills: solid colours with draw:opacity; gradients as draw:gradient (linear, axial, radial, rectangular from the
//   model's stops: the outer two colours and a border, more stops also as LibreOffice's loext:gradient-stop), uneven
//   transparency as draw:opacity; picture fills as draw:fill-image, stretched or tiled; patterns of lines as draw:hatch,
//   dotted ones (pct5 … pct90 and the like) as the colour they blend to.
// - Lines: width (0 is a hairline), colour, transparency, dashes as draw:stroke-dash in multiples of the width, caps,
//   joins, and arrowheads as draw:marker by kind and width.
// - Pictures: PNG, JPEG, GIF, BMP, TIFF, EMF and WMF as they are, others as PNG; a crop, a fill rectangle, colour effects
//   or a mirror are drawn into a PNG once (SlidePaint, as the renderer draws them), so every reader shows the same.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Filee.Engines.Documents;
using Filee.Engines.Metafiles;
using Filee.Engines.Slides;
using SkiaSharp;

namespace Filee.Engines.Odf.Draw;

internal sealed partial class OdpWriter
{
    /// <summary>The largest side of a picture drawn out for a crop, effect or mirror, in pixels.</summary>
    private const int MaxDrawnSide = 4096;

    private readonly NamedStyles _named = new();
    private readonly Media _media = new();
    private readonly SlideImages _images = new();
    private readonly Dictionary<(byte[] Data, SKRect Crop, SKRect Stretch, PictureEffects? Effects, bool Flip), (string Path, bool Flipped)?> _pictures = [];

    // ───────────────────────── Style sets ─────────────────────────

    /// <summary>The automatic styles of one part: one per distinct family, attributes and properties.</summary>
    private sealed class StyleSet(string prefix)
    {
        private readonly StringBuilder _xml = new();
        private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

        public string Xml => _xml.ToString();

        /// <param name="kind">Name prefix: gr (graphic), P, T, L (list), dp (page), co, ro, ce (table), PM (page layout).</param>
        public string Add(string family, string kind, string attributes, string properties)
        {
            var key = family + "\n" + attributes + "\n" + properties;
            if (_names.TryGetValue(key, out var name))
                return name;
            var number = _counts.GetValueOrDefault(kind) + 1;
            _counts[kind] = number;
            name = prefix + kind + number.ToString(CultureInfo.InvariantCulture);
            if (family == "page-layout")
                _xml.Append($"<style:page-layout style:name=\"{name}\"{attributes}>{properties}</style:page-layout>");
            else if (family == "list")
                _xml.Append($"<text:list-style style:name=\"{name}\"{attributes}>{properties}</text:list-style>");
            else
                _xml.Append($"<style:style style:name=\"{name}\" style:family=\"{family}\"{attributes}>{properties}</style:style>");
            return _names[key] = name;
        }
    }

    /// <summary>Named drawing styles of office:styles, one per distinct definition.</summary>
    private sealed class NamedStyles
    {
        private readonly StringBuilder _xml = new();
        private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

        public string Xml => _xml.ToString();

        /// <summary>The name of a definition (draw:gradient, draw:hatch, ...) with these attributes and content.</summary>
        /// <param name="display">The name shown in Impress ("Gradient 1"); the style name is it with spaces as "_20_".</param>
        public string Add(string element, string display, string attributes, string content = "", string? fixedName = null)
        {
            var key = element + "\n" + attributes + "\n" + content;
            if (_names.TryGetValue(key, out var name))
                return name;
            string shown;
            if (fixedName is not null && !_names.ContainsValue(fixedName.Replace(" ", "_20_", StringComparison.Ordinal)))
            {
                shown = fixedName;
            }
            else
            {
                var number = _counts.GetValueOrDefault(display) + 1;
                _counts[display] = number;
                shown = $"{display} {number.ToString(CultureInfo.InvariantCulture)}";
            }
            name = shown.Replace(" ", "_20_", StringComparison.Ordinal);
            _xml.Append($"<draw:{element} draw:name=\"{name}\" draw:display-name=\"{shown}\"{attributes}");
            _xml.Append(content.Length == 0 ? "/>" : $">{content}</draw:{element}>");
            return _names[key] = name;
        }
    }

    /// <summary>The name of the graphic style with these properties (and child elements) in the part being written.</summary>
    private string Graphic(string properties, string children = "") =>
        _styles.Add("graphic", "gr", " style:parent-style-name=\"standard\"",
            children.Length == 0 ? $"<style:graphic-properties{properties}/>" : $"<style:graphic-properties{properties}>{children}</style:graphic-properties>");

    // ───────────────────────── Fills ─────────────────────────

    /// <summary>Fill attributes of graphic properties; <paramref name="width"/> × <paramref name="height"/> is the box (for picture tiles).</summary>
    private string Fill(SlideFill? fill, float width, float height)
    {
        switch (fill)
        {
            case null:
                return " draw:fill=\"none\"";
            case SolidFill solid:
                return $" draw:fill=\"solid\" draw:fill-color=\"{Hex(solid.Color)}\"{Opacity(solid.Color.Alpha)}";
            case GradientFill gradient when gradient.Stops.Count > 0:
                return Gradient(gradient);
            case PictureFill picture:
                return Bitmap(picture) ?? " draw:fill=\"none\"";
            case PatternFill pattern:
                return Hatch(pattern);
            default:
                return $" draw:fill=\"solid\" draw:fill-color=\"{Hex(SlidePaint.Representative(fill))}\"";
        }
    }

    /// <summary>A page's background (drawing-page properties): white when it has none, so a master's doesn't show instead.</summary>
    private string PageFill(SlideFill? fill) =>
        " draw:background-size=\"full\"" + (fill is null ? " draw:fill=\"solid\" draw:fill-color=\"#ffffff\"" : Fill(fill, 1, 1));

    private static string Opacity(byte alpha) => alpha >= 255 ? "" : $" draw:opacity=\"{Percent(alpha / 255.0)}\"";

    private static string Hex(SKColor color) => $"#{color.Red:x2}{color.Green:x2}{color.Blue:x2}";

    private string Gradient(GradientFill gradient)
    {
        var stops = gradient.Stops.OrderBy(s => s.Position).ToList();
        var first = stops[0];
        var last = stops[^1];
        string style;
        SKColor start, end;
        double border = 0;
        var angle = 0.0;
        var center = "";
        var extra = false;
        if (gradient.Shape == GradientShape.Linear)
        {
            // ODF: at angle 0 the start colour is at the top, the angle turning the axis counter-clockwise; the model's
            // angle is clockwise from left to right.
            angle = 90 - gradient.Angle;
            var middle = stops.FirstOrDefault(s => Math.Abs(s.Position - 0.5f) < 0.01f);
            if (stops.Count >= 3 && Same(first.Color, last.Color) && middle.Color != default && !Same(middle.Color, first.Color))
            {
                // The end colour along the middle, the start colour at both sides.
                style = "axial";
                (start, end) = (first.Color, middle.Color);
                var inner = stops.Where(s => s.Position < 0.5f && Same(s.Color, first.Color)).Select(s => s.Position).DefaultIfEmpty(0).Max();
                border = Math.Clamp(inner * 2, 0, 1);
            }
            else
            {
                style = "linear";
                (start, end) = (first.Color, last.Color);
                if (stops.Count > 1 && Same(stops[1].Color, first.Color))
                    border = stops[1].Position;
                extra = stops.Count > (border > 0 ? 3 : 2);
            }
        }
        else
        {
            // Path gradients: the start colour outside, the end colour at the focus.
            style = gradient.Shape == GradientShape.Circle ? "radial" : "rectangular";
            (start, end) = (last.Color, first.Color);
            if (stops.Count > 1 && Same(stops[^2].Color, last.Color))
                border = 1 - stops[^2].Position;
            var focus = gradient.Focus;
            var cx = focus.Left + (1 - focus.Left - focus.Right) / 2;
            var cy = focus.Top + (1 - focus.Top - focus.Bottom) / 2;
            center = $" draw:cx=\"{Percent(Math.Clamp(cx, 0, 1))}\" draw:cy=\"{Percent(Math.Clamp(cy, 0, 1))}\"";
            extra = stops.Count > (border > 0 ? 3 : 2);
        }
        // With its unit: LibreOffice reads a bare number as degrees in files it didn't write (tenths in its own).
        angle = (angle % 360 + 360) % 360;
        var degrees = (Math.Round(angle * 10) % 3600 / 10).ToString("0.#", CultureInfo.InvariantCulture) + "deg";
        var attributes = $" draw:style=\"{style}\"{center} draw:start-color=\"{Hex(start)}\" draw:end-color=\"{Hex(end)}\"" +
                         $" draw:start-intensity=\"100%\" draw:end-intensity=\"100%\" draw:angle=\"{degrees}\" draw:border=\"{Percent(border)}\"";
        var content = new StringBuilder();
        if (extra)
        {
            // LibreOffice's multi-colour gradients: offsets from the start colour (outside, for path gradients).
            var ordered = gradient.Shape == GradientShape.Linear ? stops.Select(s => (Offset: (double)s.Position, s.Color)) : stops.Select(s => (Offset: 1.0 - s.Position, s.Color)).Reverse();
            foreach (var (offset, color) in ordered)
                content.Append($"<loext:gradient-stop svg:offset=\"{Math.Clamp(offset, 0, 1).ToString("0.####", CultureInfo.InvariantCulture)}\" loext:color-type=\"rgb\" loext:color-value=\"{Hex(color)}\"/>");
        }
        var name = _named.Add("gradient", "Gradient", attributes, content.ToString());
        var result = $" draw:fill=\"gradient\" draw:fill-gradient-name=\"{name}\" draw:fill-color=\"{Hex(start)}\"";

        // Transparency: one opacity for all, or a transparency gradient from the start's to the end's.
        var alphas = stops.Select(s => s.Color.Alpha).Distinct().ToList();
        if (alphas is [var alpha])
            return result + Opacity(alpha);
        var opacity = _named.Add("opacity", "Transparency", $" draw:style=\"{style}\"{center} draw:start=\"{Percent(start.Alpha / 255.0)}\" draw:end=\"{Percent(end.Alpha / 255.0)}\"" +
                                                             $" draw:angle=\"{degrees}\" draw:border=\"{Percent(border)}\"");
        return result + $" draw:opacity-name=\"{opacity}\"";
    }

    private static bool Same(SKColor a, SKColor b) => a == b;

    /// <summary>A two-colour pattern: lines as a hatch over the background, dots and other textures as the colour they blend to.</summary>
    private string Hatch(PatternFill pattern)
    {
        var preset = pattern.Preset;
        var crossed = preset.Contains("Cross", StringComparison.OrdinalIgnoreCase) || preset.Contains("Grid", StringComparison.OrdinalIgnoreCase)
                      || preset.Contains("Check", StringComparison.OrdinalIgnoreCase) || preset.Contains("Dmnd", StringComparison.OrdinalIgnoreCase)
                      || preset is "plaid" or "trellis" or "weave";
        // Degrees, written with their unit like the gradient's angle.
        int? rotation = preset.Contains("UpDiag", StringComparison.OrdinalIgnoreCase) ? 45
            : preset.Contains("DnDiag", StringComparison.OrdinalIgnoreCase) ? 135
            : preset.Contains("Horz", StringComparison.OrdinalIgnoreCase) ? 0
            : preset.Contains("Vert", StringComparison.OrdinalIgnoreCase) ? 90
            : preset.Contains("diag", StringComparison.OrdinalIgnoreCase) || preset is "trellis" or "solidDmnd" or "openDmnd" or "dotDmnd" ? 45
            : crossed ? 0
            : null;
        if (rotation is null)
        {
            // pct5 … pct90: that share of the foreground; other textures half and half.
            var share = preset.StartsWith("pct", StringComparison.Ordinal) && int.TryParse(preset.AsSpan(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)
                ? Math.Clamp(p, 0, 100) / 100f
                : 0.5f;
            var (f, b) = (pattern.Foreground, pattern.Background);
            var blend = new SKColor((byte)Math.Round(f.Red * share + b.Red * (1 - share)), (byte)Math.Round(f.Green * share + b.Green * (1 - share)),
                (byte)Math.Round(f.Blue * share + b.Blue * (1 - share)), (byte)Math.Round(f.Alpha * share + b.Alpha * (1 - share)));
            return $" draw:fill=\"solid\" draw:fill-color=\"{Hex(blend)}\"{Opacity(blend.Alpha)}";
        }
        var distance = preset.StartsWith("nar", StringComparison.Ordinal) || preset.StartsWith("dk", StringComparison.Ordinal) ? 2
            : preset.StartsWith("wd", StringComparison.Ordinal) || preset.StartsWith("lg", StringComparison.Ordinal) ? 6
            : 4;
        var name = _named.Add("hatch", "Hatch", $" draw:style=\"{(crossed ? "double" : "single")}\" draw:color=\"{Hex(pattern.Foreground)}\" draw:distance=\"{Pt(distance)}\" draw:rotation=\"{N(rotation.Value)}deg\"");
        var solid = pattern.Background.Alpha > 0;
        return $" draw:fill=\"hatch\" draw:fill-hatch-name=\"{name}\" draw:fill-hatch-solid=\"{(solid ? "true" : "false")}\"" +
               (solid ? $" draw:fill-color=\"{Hex(pattern.Background)}\"" : "");
    }

    /// <summary>A picture fill as a fill image, stretched or tiled at the picture's size.</summary>
    private string? Bitmap(PictureFill picture)
    {
        var tile = picture.Tile;
        var drawn = tile is null ? Picture(picture, flip: false) : Picture(picture with { Crop = SKRect.Empty, Stretch = SKRect.Empty }, flip: false);
        if (drawn is not { } media)
            return null;
        var name = _named.Add("fill-image", "Image", $" xlink:href=\"{OdfXml.Escape(media.Path)}\" xlink:type=\"simple\" xlink:show=\"embed\" xlink:actuate=\"onLoad\"");
        if (tile is null)
            return $" draw:fill=\"bitmap\" draw:fill-image-name=\"{name}\" style:repeat=\"stretch\"";
        var size = _images.Size(picture.Image) ?? new SKSize(72, 72);
        var point = tile.Align switch
        {
            "t" => "top",
            "tr" => "top-right",
            "l" => "left",
            "ctr" => "center",
            "r" => "right",
            "bl" => "bottom-left",
            "b" => "bottom",
            "br" => "bottom-right",
            _ => "top-left",
        };
        return $" draw:fill=\"bitmap\" draw:fill-image-name=\"{name}\" style:repeat=\"repeat\"" +
               $" draw:fill-image-width=\"{Pt(Math.Max(0.1, size.Width * Math.Abs(tile.ScaleX)))}\" draw:fill-image-height=\"{Pt(Math.Max(0.1, size.Height * Math.Abs(tile.ScaleY)))}\"" +
               $" draw:fill-image-ref-point=\"{point}\"";
    }

    // ───────────────────────── Lines ─────────────────────────

    /// <summary>Line attributes of graphic properties.</summary>
    private string Stroke(SlideLine? line)
    {
        if (line is null)
            return " draw:stroke=\"none\"";
        var color = line.Fill is SolidFill solid ? solid.Color : SlidePaint.Representative(line.Fill);
        var width = float.IsFinite(line.Width) ? Math.Clamp(line.Width, 0, 1000) : 0;
        var sb = new StringBuilder();
        if (line.Dash is { Length: > 0 } dash && dash.All(float.IsFinite))
            sb.Append($" draw:stroke=\"dash\" draw:stroke-dash=\"{Dash(dash, line.Cap == SKStrokeCap.Round)}\"");
        else
            sb.Append(" draw:stroke=\"solid\"");
        sb.Append($" svg:stroke-width=\"{Pt(width)}\" svg:stroke-color=\"{Hex(color)}\"");
        if (color.Alpha < 255)
            sb.Append($" svg:stroke-opacity=\"{Percent(color.Alpha / 255.0)}\"");
        sb.Append(line.Join switch
        {
            SKStrokeJoin.Miter => " draw:stroke-linejoin=\"miter\"",
            SKStrokeJoin.Bevel => " draw:stroke-linejoin=\"bevel\"",
            _ => " draw:stroke-linejoin=\"round\"",
        });
        sb.Append(line.Cap switch
        {
            SKStrokeCap.Round => " svg:stroke-linecap=\"round\"",
            SKStrokeCap.Square => " svg:stroke-linecap=\"square\"",
            _ => " svg:stroke-linecap=\"butt\"",
        });
        foreach (var (end, side) in new[] { (line.Head, "start"), (line.Tail, "end") })
        {
            if (end.Kind == LineEndKind.None)
                continue;
            var multiple = float.IsFinite(end.Width) ? Math.Clamp(end.Width, 1, 20) : 3;
            sb.Append($" draw:marker-{side}=\"{Marker(end.Kind)}\" draw:marker-{side}-width=\"{Pt(multiple * Math.Max(width, 1))}\" draw:marker-{side}-center=\"false\"");
        }
        return sb.ToString();
    }

    /// <summary>A dash pattern (dash and gap lengths as multiples of the width) as a draw:stroke-dash.</summary>
    private string Dash(float[] dash, bool round)
    {
        static string Multiple(float value) => Percent(Math.Clamp(value, 0.01, 100));
        var first = dash[0];
        var gap = dash.Length > 1 ? dash[1] : first;
        var attributes = new StringBuilder($" draw:style=\"{(round ? "round" : "rect")}\"");
        // Two kinds of dash at most: the first, and a second one of another length after it.
        var second = dash.Length >= 4 && Math.Abs(dash[2] - first) > 0.01f ? dash[2] : (float?)null;
        attributes.Append($" draw:dots1=\"1\" draw:dots1-length=\"{Multiple(first)}\"");
        if (second is { } other)
            attributes.Append($" draw:dots2=\"1\" draw:dots2-length=\"{Multiple(other)}\"");
        attributes.Append($" draw:distance=\"{Multiple(gap)}\"");
        return _named.Add("stroke-dash", "Dash", attributes.ToString());
    }

    /// <summary>
    /// A line end marker, pointing up from its base at the bottom as ODF draws them; its name says its kind (Impress's
    /// names: Arrow, Arrow concave, Circle, Square 45, Line Arrow), which is how readers tell them apart.
    /// </summary>
    private string Marker(LineEndKind kind)
    {
        var (name, viewBox, d) = kind switch
        {
            LineEndKind.Stealth => ("Arrow concave", "0 0 20 30", "M10 0 L20 30 L10 22 L0 30 Z"),
            LineEndKind.Diamond => ("Square 45", "0 0 20 20", "M10 0 L20 10 L10 20 L0 10 Z"),
            LineEndKind.Oval => ("Circle", "0 0 20 20", "M10 0 C15.5 0 20 4.5 20 10 C20 15.5 15.5 20 10 20 C4.5 20 0 15.5 0 10 C0 4.5 4.5 0 10 0 Z"),
            LineEndKind.Arrow => ("Line Arrow", "0 0 20 30", "M10 0 L20 27 L17 30 L10 11 L3 30 L0 27 Z"),
            _ => ("Arrow", "0 0 20 30", "M10 0 L20 30 L0 30 Z"),
        };
        return _named.Add("marker", name, $" svg:viewBox=\"{viewBox}\" svg:d=\"{d}\"", fixedName: name);
    }

    // ───────────────────────── Text frames ─────────────────────────

    /// <summary>The text frame settings of a shape's graphic properties: insets, anchoring, wrapping, direction, columns.</summary>
    private static string TextFrame(SlideTextFrame? frame)
    {
        if (frame is null)
            return " draw:auto-grow-height=\"false\" draw:auto-grow-width=\"false\"";
        var sb = new StringBuilder(" draw:auto-grow-height=\"false\" draw:auto-grow-width=\"false\"")
            .Append($" fo:padding-left=\"{Pt(frame.InsetLeft)}\" fo:padding-right=\"{Pt(frame.InsetRight)}\" fo:padding-top=\"{Pt(frame.InsetTop)}\" fo:padding-bottom=\"{Pt(frame.InsetBottom)}\"")
            .Append($" draw:textarea-vertical-align=\"{Anchor(frame.Anchor)}\" draw:textarea-horizontal-align=\"{(frame.AnchorCenter ? "center" : "justify")}\"")
            .Append($" fo:wrap-option=\"{(frame.Wrap ? "wrap" : "no-wrap")}\"");
        if (frame.Direction == TextDirection.Down)
            sb.Append(" style:writing-mode=\"tb-rl\"");
        return sb.ToString();
    }

    /// <summary>The text columns of a frame (style:columns inside the graphic properties); empty for one column.</summary>
    private static string Columns(SlideTextFrame? frame) =>
        frame is { Columns: > 1 } ? $"<style:columns fo:column-count=\"{N(Math.Clamp(frame.Columns, 1, 16))}\" fo:column-gap=\"{Pt(Math.Max(0, frame.ColumnGap))}\"/>" : "";

    // ───────────────────────── Pictures ─────────────────────────

    /// <summary>
    /// The package path of a picture fill's picture: as it is, or drawn out when it is cropped, placed in part of its box,
    /// recoloured or (<paramref name="flip"/>) to be mirrored top to bottom. Null when it can't be stored.
    /// </summary>
    private (string Path, bool Flipped)? Picture(PictureFill fill, bool flip)
    {
        var plain = fill.Crop.IsEmpty && fill.Stretch.IsEmpty && (fill.Effects is null || fill.Effects.IsNone);
        var key = (fill.Image.Data, plain ? SKRect.Empty : fill.Crop, plain ? SKRect.Empty : fill.Stretch, plain ? null : fill.Effects, flip);
        if (_pictures.TryGetValue(key, out var known))
            return known;
        (string, bool)? result = null;
        if (!plain || flip)
        {
            if (DrawOut(fill, flip) is { } png)
                result = (_media.Add(png, _limits), flip);
        }
        if (result is null && _media.Store(fill.Image.Data, _limits) is { } path)
            result = (path, false);
        _pictures[key] = result;
        return result;
    }

    /// <summary>The picture fill drawn into a PNG of its own resolution; null for metafiles and pictures Skia can't decode.</summary>
    private byte[]? DrawOut(PictureFill fill, bool flip)
    {
        if (_images.Image(fill.Image) is not { } image)
            return null;
        static float Share(float a, float b) => Math.Clamp(1 - (float.IsFinite(a) ? a : 0) - (float.IsFinite(b) ? b : 0), 0.01f, 100);
        // The used part keeps its pixels: the box is as many pixels as that part needs over the share of it it fills.
        var width = image.Width * Share(fill.Crop.Left, fill.Crop.Right) / Share(fill.Stretch.Left, fill.Stretch.Right);
        var height = image.Height * Share(fill.Crop.Top, fill.Crop.Bottom) / Share(fill.Stretch.Top, fill.Stretch.Bottom);
        var scale = Math.Min(1, MaxDrawnSide / Math.Max(width, height));
        var w = Math.Clamp((int)Math.Round(width * scale), 1, MaxDrawnSide);
        var h = Math.Clamp((int)Math.Round(height * scale), 1, MaxDrawnSide);
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (surface is null)
            return null;
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        if (flip)
            canvas.Scale(1, -1, 0, h / 2f);
        SlidePaint.Picture(canvas, fill, new SKRect(0, 0, w, h), _images);
        canvas.Flush();
        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray();
    }

    /// <summary>The pictures of the package, each stored once under Pictures/.</summary>
    private sealed class Media
    {
        private readonly Dictionary<string, string> _paths = new(StringComparer.Ordinal);
        private readonly Dictionary<byte[], string?> _stored = new(ReferenceEqualityComparer.Instance);
        private readonly List<OdfPart> _parts = [];
        private long _bytes;

        public IReadOnlyList<OdfPart> Parts => _parts;

        /// <summary>The path of a picture's bytes as they are, or as PNG when ODF readers may not show its format; null when it can't be read.</summary>
        public string? Store(byte[] data, DocumentLimits limits)
        {
            if (_stored.TryGetValue(data, out var known))
                return known;
            string? path = null;
            if (Kind(data) is { } kind)
            {
                path = Add(data, limits, kind.Extension, kind.MediaType);
            }
            else
            {
                // WEBP and other formats Skia reads: PNG.
                using var bitmap = SKBitmap.Decode(data);
                if (bitmap is not null && (long)bitmap.Width * bitmap.Height <= limits.MaxPicturePixels)
                {
                    using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                    if (encoded is not null)
                        path = Add(encoded.ToArray(), limits);
                }
            }
            _stored[data] = path;
            return path;
        }

        /// <summary>The path of a PNG.</summary>
        public string Add(byte[] png, DocumentLimits limits) => Add(png, limits, "png", "image/png");

        private string Add(byte[] data, DocumentLimits limits, string extension, string mediaType)
        {
            var hash = Convert.ToHexString(SHA256.HashData(data));
            if (_paths.TryGetValue(hash, out var known))
                return known;
            _bytes += data.Length;
            if (_bytes > limits.MaxPackageBytes)
                throw DocumentLimits.Exceeded($"The presentation's pictures are larger than {DocumentLimits.Megabytes(limits.MaxPackageBytes)} together.");
            var path = $"Pictures/image{(_parts.Count + 1).ToString(CultureInfo.InvariantCulture)}.{extension}";
            _parts.Add(new OdfPart(path, mediaType, Bytes: data));
            return _paths[hash] = path;
        }

        /// <summary>The formats stored as they are, by their signature.</summary>
        private static (string Extension, string MediaType)? Kind(byte[] data)
        {
            var span = data.AsSpan();
            if (span.Length < 8)
                return null;
            if (span[0] == 0x89 && span[1] == (byte)'P' && span[2] == (byte)'N' && span[3] == (byte)'G')
                return ("png", "image/png");
            if (span[0] == 0xFF && span[1] == 0xD8 && span[2] == 0xFF)
                return ("jpg", "image/jpeg");
            if (span[0] == (byte)'G' && span[1] == (byte)'I' && span[2] == (byte)'F')
                return ("gif", "image/gif");
            if (span[0] == (byte)'B' && span[1] == (byte)'M')
                return ("bmp", "image/bmp");
            if ((span[0] == (byte)'I' && span[1] == (byte)'I' && span[2] == 42 && span[3] == 0) || (span[0] == (byte)'M' && span[1] == (byte)'M' && span[2] == 0 && span[3] == 42))
                return ("tif", "image/tiff");
            return Metafile.Detect(span[..Math.Min(span.Length, 64)]) switch
            {
                MetafileFormat.Emf => ("emf", "image/x-emf"),
                MetafileFormat.Wmf => ("wmf", "image/x-wmf"),
                _ => null,
            };
        }
    }
}
