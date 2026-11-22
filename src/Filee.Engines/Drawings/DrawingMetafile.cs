// A slide of the slide model (SVG drawings, Visio, ODG, whiteboards, mind maps) as an EMF or a WMF (Metafiles/
// EmfWriter, WmfWriter): the shapes walked as the slide renderer walks them (SlideRenderer: each element in its box,
// turned, mirrored, groups mapping their child space), every outline mapped onto the page.
//
// - Fills: solid colours; gradients (linear at their angle, circles, ellipses, rectangles from their focus) as the
//   writers' gradients, with the frame SlidePaint gives Skia's shaders; pictures stretched into their fill rectangle
//   with their crop and opacity, clipped to the shape. The 3D-looking lighter and darker parts of presets are their
//   fill's colours lightened or darkened.
// - Lines: pens (width, dashes, caps, joins) where the element's transform keeps the line's form (no squeeze or skew)
//   and the line is one solid colour; otherwise (double lines, gradient lines, squeezed shapes) the outline the line
//   covers, filled. Arrowheads are SlideRenderer.EndMark's marks.
// - Text: laid out by the slide renderer itself (the same lines, fonts and fallbacks as the PDF), its glyph runs
//   written as text by the writers; underlines and other drawing that comes with text go as pictures.
// - What has no metafile form of its own (charts, tables, pattern fills, tiled pictures, pictures that are metafiles,
//   recoloured pictures) is drawn by the slide renderer into a picture with alpha at 288 dpi, its text kept as text.
// - The page: the slide's size; no white page behind the drawing (an SVG's background stays transparent), the slide's
//   own background when it has one.

using Filee.Engines.Metafiles;
using Filee.Engines.Rendering;
using Filee.Engines.Slides;
using SkiaSharp;

namespace Filee.Engines.Drawings;

internal sealed class DrawingMetafile
{
    /// <summary>Pixels per point of the pictures made of what has no metafile form (288 dpi).</summary>
    private const float PicturePixelsPerPoint = 4;

    /// <summary>Pixels of the long side of the coarse picture that finds where such drawing lies.</summary>
    private const int ProbeSide = 1024;

    private readonly MetafileWriter _writer;
    private readonly SlideRenderer _renderer;
    private readonly SlideDeck _deck;
    private readonly CancellationToken _cancellationToken;

    private DrawingMetafile(MetafileWriter writer, SlideRenderer renderer, SlideDeck deck, CancellationToken cancellationToken)
    {
        _writer = writer;
        _renderer = renderer;
        _deck = deck;
        _cancellationToken = cancellationToken;
    }

    private SlideImages Images => _renderer.Images;

    /// <summary>One slide of <paramref name="deck"/> as an EMF (<paramref name="wmf"/> false) or a WMF.</summary>
    public static byte[] Write(SlideDeck deck, Slide slide, bool wmf, SlideRenderer renderer, MetafileLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        MetafileWriter writer = wmf ? new WmfWriter(deck.Width, deck.Height, limits) : new EmfWriter(deck.Width, deck.Height, limits);
        var walk = new DrawingMetafile(writer, renderer, deck, cancellationToken);
        if (slide.Background is { } background)
        {
            var page = new SKRect(0, 0, deck.Width, deck.Height);
            using var path = new SKPath();
            path.AddRect(page);
            walk.Fill(path, background, page, SKMatrix.Identity, PathFill.Normal);
        }
        foreach (var element in slide.Elements)
            walk.Element(element, SKMatrix.Identity);
        return writer.ToArray();
    }

    private void Element(SlideElement element, SKMatrix parent)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (!(element.Bounds.Width >= 0 && element.Bounds.Height >= 0) || !float.IsFinite(element.Bounds.Left) || !float.IsFinite(element.Bounds.Top))
            return;
        var matrix = parent.PreConcat(SlideRenderer.Local(element));
        switch (element)
        {
            case SlideGroup group:
                var child = group.ChildBounds;
                var scaleX = child.Width > 0 ? group.Bounds.Width / child.Width : 1;
                var scaleY = child.Height > 0 ? group.Bounds.Height / child.Height : 1;
                var inner = matrix.PreConcat(SKMatrix.CreateScale(scaleX, scaleY)).PreConcat(SKMatrix.CreateTranslation(-child.Left, -child.Top));
                foreach (var item in group.Children)
                    Element(item, inner);
                break;
            case SlideShape { Chart: null } shape:
                Shape(shape, matrix);
                if (shape.Text is { IsEmpty: false })
                {
                    // Only the text: a copy without fill and line, laid out and placed by the renderer.
                    Rendered(new SlideShape
                    {
                        Bounds = shape.Bounds,
                        Rotation = shape.Rotation,
                        FlipHorizontal = shape.FlipHorizontal,
                        FlipVertical = shape.FlipVertical,
                        Text = shape.Text,
                    }, parent);
                }
                break;
            default:
                // Charts and tables: the renderer's drawing, its text kept as text.
                Rendered(element, parent);
                break;
        }
    }

    // ───────────────────────── Shapes ─────────────────────────

    private void Shape(SlideShape shape, SKMatrix matrix)
    {
        var box = new SKRect(0, 0, shape.Bounds.Width, shape.Bounds.Height);
        if (shape.Fill is { } fill)
        {
            foreach (var path in shape.Paths.Where(p => p.Fill != PathFill.None))
                Fill(path.Path, fill, box, matrix, path.Fill);
        }
        if (shape.Line is { } line)
            Stroke([.. shape.Paths.Where(p => p.Stroke).Select(p => p.Path)], line, box, matrix);
    }

    /// <summary>The veil SlideRenderer lays over the lighter and darker parts of presets; transparent for a plain fill.</summary>
    private static SKColor Veil(PathFill fill) => fill switch
    {
        PathFill.Lighten => SKColors.White.WithAlpha(102),
        PathFill.LightenLess => SKColors.White.WithAlpha(51),
        PathFill.Darken => SKColors.Black.WithAlpha(102),
        PathFill.DarkenLess => SKColors.Black.WithAlpha(51),
        _ => SKColors.Transparent,
    };

    /// <summary>A colour with the veil laid over it.</summary>
    private static SKColor Veiled(SKColor color, SKColor veil) =>
        veil.Alpha == 0 ? color : MetafileGradient.Mix(color, veil.WithAlpha(color.Alpha), veil.Alpha / 255f);

    /// <summary>Fills <paramref name="local"/> (the element's coordinates) with <paramref name="fill"/> laid over <paramref name="box"/>.</summary>
    private void Fill(SKPath local, SlideFill fill, SKRect box, SKMatrix matrix, PathFill kind)
    {
        using var path = new SKPath(local);
        path.Transform(matrix);
        if (path.IsEmpty)
            return;
        var veil = Veil(kind);
        switch (fill)
        {
            case SolidFill solid:
                _writer.Fill(path, Veiled(solid.Color, veil));
                return;
            case GradientFill { Shape: GradientShape.Rectangle } rectangle:
                RectangleGradient(path, rectangle, box, matrix, veil);
                return;
            case GradientFill gradient:
                _writer.Gradient(path, Gradient(gradient, box, matrix, veil));
                return;
            case PictureFill picture when Picture(path, picture, box, matrix):
                break;
            default:
                // Patterns, tiled pictures, pictures that are metafiles or recoloured: as SlidePaint draws them.
                Picture(canvas =>
                {
                    canvas.Concat(in matrix);
                    SlidePaint.Fill(canvas, local, fill, box, Images);
                }, path.Bounds);
                break;
        }
        if (veil.Alpha > 0)
            _writer.Fill(path, veil);
    }

    /// <summary>A gradient's frame on the page, as SlidePaint.Gradient lays the gradient over the box.</summary>
    private static MetafileGradient Gradient(GradientFill gradient, SKRect box, SKMatrix matrix, SKColor veil)
    {
        var stops = gradient.Stops.OrderBy(s => s.Position).Select(s => s with { Color = Veiled(s.Color, veil) }).ToList();
        var width = Math.Max(box.Width, 0.01f);
        var height = Math.Max(box.Height, 0.01f);
        if (gradient.Shape == GradientShape.Linear)
        {
            var angle = gradient.Angle * MathF.PI / 180;
            var (dx, dy) = (MathF.Cos(angle), MathF.Sin(angle));
            SKMatrix frame;
            if (gradient.Scaled)
            {
                var half = (MathF.Abs(dx) + MathF.Abs(dy)) / 2;
                var unit = Axis(new SKPoint(0.5f - dx * half, 0.5f - dy * half), new SKPoint(0.5f + dx * half, 0.5f + dy * half));
                frame = SKMatrix.CreateTranslation(box.Left, box.Top).PreConcat(SKMatrix.CreateScale(width, height)).PreConcat(unit);
            }
            else
            {
                var length = (MathF.Abs(width * dx) + MathF.Abs(height * dy)) / 2;
                var center = new SKPoint(box.MidX, box.MidY);
                frame = Axis(new SKPoint(center.X - dx * length, center.Y - dy * length), new SKPoint(center.X + dx * length, center.Y + dy * length));
            }
            return new MetafileGradient(MetafileGradientKind.Linear, matrix.PreConcat(frame), stops);
        }
        var focus = gradient.Focus;
        var fx = box.Left + width * (focus.Left + (1 - focus.Left - focus.Right) / 2);
        var fy = box.Top + height * (focus.Top + (1 - focus.Top - focus.Bottom) / 2);
        var rx = Math.Max(Math.Abs(fx - box.Left), Math.Abs(box.Right - fx));
        var ry = Math.Max(Math.Abs(fy - box.Top), Math.Abs(box.Bottom - fy));
        SKMatrix circle;
        if (gradient.Shape == GradientShape.Circle)
        {
            var radius = Math.Max(MathF.Sqrt(rx * rx + ry * ry), 0.01f);
            circle = SKMatrix.CreateTranslation(fx, fy).PreConcat(SKMatrix.CreateScale(radius, radius));
        }
        else
        {
            var scaleY = ry / Math.Max(rx, 0.01f);
            var radius = Math.Max(rx * 1.4142f, 0.01f);
            circle = SKMatrix.CreateTranslation(fx, fy).PreConcat(SKMatrix.CreateScale(radius, radius * scaleY));
        }
        return new MetafileGradient(MetafileGradientKind.Radial, matrix.PreConcat(circle), stops);
    }

    /// <summary>The frame whose x axis runs from <paramref name="from"/> (0) to <paramref name="to"/> (1), its y axis square to it.</summary>
    private static SKMatrix Axis(SKPoint from, SKPoint to)
    {
        var (ax, ay) = (to.X - from.X, to.Y - from.Y);
        return new SKMatrix(ax, -ay, from.X, ay, ax, from.Y, 0, 0, 1);
    }

    /// <summary>A rectangle gradient as SlidePaint draws it: the focus rectangle in the first colour, four pieces each a linear gradient.</summary>
    private void RectangleGradient(SKPath path, GradientFill gradient, SKRect box, SKMatrix matrix, SKColor veil)
    {
        var stops = gradient.Stops.OrderBy(s => s.Position).Select(s => s with { Color = Veiled(s.Color, veil) }).ToList();
        var focus = SlidePaint.Focus(gradient.Focus, box);
        _writer.Save();
        _writer.Clip(path);
        using (var middle = new SKPath())
        {
            middle.AddRect(focus);
            middle.Transform(matrix);
            _writer.Fill(middle, stops[0].Color);
        }
        var overlap = stops.All(s => s.Color.Alpha == 255) ? Math.Min(0.5f, Math.Min(box.Width, box.Height) / 40) : 0;
        foreach (var (piece, from, to) in SlidePaint.RectanglePieces(focus, box, overlap))
        {
            using (piece)
            {
                piece.Transform(matrix);
                _writer.Gradient(piece, new MetafileGradient(MetafileGradientKind.Linear, matrix.PreConcat(Axis(from, to)), stops));
            }
        }
        _writer.Restore();
    }

    /// <summary>A plain picture fill (a bitmap, perhaps translucent) clipped to <paramref name="path"/>; false for what goes as a picture of the fill.</summary>
    private bool Picture(SKPath path, PictureFill picture, SKRect box, SKMatrix matrix)
    {
        if (picture.Tile is not null || Images.Metafile(picture.Image) is not null)
            return false;
        var effects = picture.Effects;
        if (effects is not null && effects with { Alpha = 1 } != PictureEffects.None)
            return false;
        if (Images.Image(picture.Image) is not { } image)
            return true; // can't be decoded: left out, as the renderer leaves it
        var stretch = picture.Stretch;
        var target = new SKRect(box.Left + box.Width * stretch.Left, box.Top + box.Height * stretch.Top,
            box.Right - box.Width * stretch.Right, box.Bottom - box.Height * stretch.Bottom);
        var crop = picture.Crop;
        var source = new SKRect(image.Width * crop.Left, image.Height * crop.Top, image.Width * (1 - crop.Right), image.Height * (1 - crop.Bottom));
        if (source.Width <= 0 || source.Height <= 0 || target.Width <= 0 || target.Height <= 0)
            return true;
        var map = SKMatrix.CreateScale(target.Width / source.Width, target.Height / source.Height)
            .PreConcat(SKMatrix.CreateTranslation(-source.Left, -source.Top)).PostConcat(SKMatrix.CreateTranslation(target.Left, target.Top));
        using var bitmap = Faded(image, effects?.Alpha ?? 1);
        _writer.Save();
        _writer.Clip(path);
        using (var area = new SKPath())
        {
            area.AddRect(target);
            area.Transform(matrix);
            _writer.Clip(area);
        }
        _writer.Image(bitmap, matrix.PreConcat(map));
        _writer.Restore();
        return true;
    }

    /// <summary>The picture's pixels, its alpha multiplied by <paramref name="alpha"/>.</summary>
    private static SKBitmap Faded(SKImage image, float alpha)
    {
        var bitmap = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255)) };
        canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), paint);
        return bitmap;
    }

    // ───────────────────────── Lines ─────────────────────────

    private void Stroke(IReadOnlyList<SKPath> paths, SlideLine line, SKRect box, SKMatrix matrix)
    {
        if (paths.Count == 0)
            return;
        var width = line.Width > 0 ? line.Width : SlideLine.Hairline;
        // Round and square caps reach half the width past each dash's ends: the dashes are drawn that much shorter.
        var capped = line.Cap == SKStrokeCap.Butt ? 0 : width;
        var dash = line.Dash is { Length: >= 2 } pattern
            ? (pattern.Length % 2 == 0 ? pattern : [.. pattern, .. pattern]).Select((d, i) => Math.Max(0.01f, d * width + (i % 2 == 0 ? -capped : capped))).ToArray()
            : null;
        var scale = Similarity(matrix);
        var color = SlidePaint.Representative(line.Fill);
        var asPen = scale is not null && line.Compound == LineCompound.Single && line.Fill is not GradientFill;
        var (head, tail) = (line.Head, line.Tail);
        for (var i = 0; i < paths.Count; i++)
        {
            var path = paths[i];
            var first = i == 0 && head.Kind != LineEndKind.None;
            var last = i == paths.Count - 1 && tail.Kind != LineEndKind.None;
            using var shortened = first || last
                ? SlideRenderer.Shorten(path, first ? SlideRenderer.Setback(head, width) : 0, last ? SlideRenderer.Setback(tail, width) : 0)
                : null;
            var drawn = shortened ?? path;
            if (asPen)
            {
                using var page = new SKPath(drawn);
                page.Transform(matrix);
                var s = scale!.Value;
                _writer.Stroke(page, new MetafilePen(color, width * s, dash?.Select(d => d * s).ToArray(), line.Cap, line.Join, 8));
                continue;
            }
            // The outline the line covers, filled with the line's fill (a double line less its middle third).
            var pen = new MetafilePen(color, width, dash, line.Cap, line.Join, 8);
            using var outline = pen.Outline(drawn);
            var covered = outline;
            SKPath? both = null;
            if (line.Compound != LineCompound.Single)
            {
                using var middle = (pen with { Width = width / 3 }).Outline(drawn);
                both = outline.Op(middle, SKPathOp.Difference);
                covered = both ?? outline;
            }
            using (both)
                Fill(covered, line.Fill is GradientFill ? line.Fill : new SolidFill(color), box, matrix, PathFill.Normal);
        }
        if (head.Kind != LineEndKind.None)
            End(paths[0], head, width, color, matrix, atStart: true);
        if (tail.Kind != LineEndKind.None)
            End(paths[^1], tail, width, color, matrix, atStart: false);
    }

    private void End(SKPath path, LineEnd end, float width, SKColor color, SKMatrix matrix, bool atStart)
    {
        if (SlideRenderer.EndMark(path, end, width, atStart) is not var (mark, stroked))
            return;
        using (mark)
        {
            using var shape = stroked ? new MetafilePen(color, width, null, SKStrokeCap.Round, SKStrokeJoin.Round).Outline(mark) : new SKPath(mark);
            shape.Transform(matrix);
            _writer.Fill(shape, color);
        }
    }

    /// <summary>The scale of a transform that keeps shapes' form (turns, mirrors, one scale), or null for a squeeze or skew.</summary>
    private static float? Similarity(SKMatrix m)
    {
        var (a, b, c, d) = (m.ScaleX, m.SkewY, m.SkewX, m.ScaleY);
        var x = MathF.Sqrt(a * a + b * b);
        var y = MathF.Sqrt(c * c + d * d);
        if (x <= 0 || Math.Abs(x - y) > 1e-3f * Math.Max(x, y) || Math.Abs(a * c + b * d) > 1e-3f * x * y)
            return null;
        return x;
    }

    // ───────────────────────── Rendered by the slide renderer ─────────────────────────

    /// <summary>
    /// An element as the slide renderer draws it, placed through <paramref name="parent"/>: its glyph runs as text, the
    /// rest as pictures.
    /// </summary>
    private void Rendered(SlideElement element, SKMatrix parent)
    {
        var slide = new Slide();
        slide.Elements.Add(element);
        var drawn = _renderer.Render(_deck, slide, withBackground: false);
        var page = new PageContent(_deck.Width, _deck.Height);
        page.Add(drawn, parent);
        var pending = new List<DrawingItem>();
        void Flush()
        {
            if (pending.Count == 0)
                return;
            var items = pending.ToList();
            pending.Clear();
            Picture(canvas =>
            {
                foreach (var item in items)
                {
                    canvas.Save();
                    item.Drawing(canvas);
                    canvas.Restore();
                }
            }, null);
        }
        foreach (var item in page.Items)
        {
            switch (item)
            {
                case GlyphItem glyphs:
                    Flush();
                    _writer.Text(glyphs.Run);
                    break;
                case DrawingItem drawing:
                    pending.Add(drawing);
                    break;
            }
        }
        Flush();
    }

    /// <summary>
    /// What <paramref name="draw"/> draws in page coordinates as a picture with alpha over where it lies (found on a
    /// coarse picture of <paramref name="hint"/>, or of the page).
    /// </summary>
    private void Picture(Action<SKCanvas> draw, SKRect? hint)
    {
        var page = new SKRect(0, 0, _deck.Width, _deck.Height);
        var area = hint is { } h ? SKRect.Intersect(SKRect.Inflate(h, 2, 2), page) : page;
        if (area.Width <= 0 || area.Height <= 0)
            return;
        var coarse = ProbeSide / Math.Max(area.Width, area.Height);
        var (width, height) = (Math.Max(1, (int)Math.Ceiling(area.Width * coarse)), Math.Max(1, (int)Math.Ceiling(area.Height * coarse)));
        using var probe = new SKBitmap(new SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(probe))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(coarse);
            canvas.Translate(-area.Left, -area.Top);
            draw(canvas);
        }
        var alpha = probe.GetPixelSpan();
        int left = width, top = height, right = -1, bottom = -1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (alpha[y * probe.RowBytes + x] == 0)
                    continue;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }
        if (right < 0)
            return;
        var found = new SKRect(area.Left + (left - 1) / coarse, area.Top + (top - 1) / coarse, area.Left + (right + 2) / coarse, area.Top + (bottom + 2) / coarse);
        found = SKRect.Intersect(found, page);
        _writer.Rasterize(found, PicturePixelsPerPoint, draw);
    }
}
