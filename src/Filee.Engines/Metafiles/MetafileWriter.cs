// Filee's own EMF and WMF writers: what is drawn in page points (y downwards, the page model's coordinates) becomes
// metafile records. EmfWriter writes [MS-EMF], WmfWriter a placeable [MS-WMF]; both are written from Microsoft's
// specifications. Drawings/DrawingMetafile walks the slide model into these calls.
//
// What each call becomes (the formats differ in what they can hold):
// - Paths (lines, Béziers, closed figures): EMF path brackets with curves kept; WMF has no curves or paths, so its
//   outlines are flattened into polygons.
// - Fills: solid brushes. Gradients: an axis-aligned linear one as an EMF GradientFill rectangle (the colours run
//   between the stops), everything else as bands of solid colour, each one drawn over the last (no seams where a
//   renderer smooths edges): strips across a linear gradient, discs from the outside in for a radial one.
// - Opacity: GDI brushes, pens and text have no alpha. EMF keeps it through AlphaBlend: a translucent fill is one pixel
//   of its colour stretched over its bounds at its opacity and clipped to its outline, a translucent gradient or
//   picture a bitmap with per-pixel alpha. WMF has no alpha at all: a translucent colour is mixed with white (what it
//   shows on a white page), a picture with transparent parts keeps its opaque ones through a mask (two blits).
// - No EMF+: EMF+ ([MS-EMFPLUS]) would add alpha and smoothing, but only as a second copy of every drawing ("EMF+
//   dual"), and readers without GDI+ (LibreOffice's import, older Office for Mac) play the EMF records anyway; plain
//   EMF with AlphaBlend covers what the drawings need.
// - Text: runs whose glyphs stand one to one for their characters, left to right (Latin, CJK, Hangul syllables,
//   Cyrillic, ...), stay text: EMF's ExtTextOutW with the font's face name, size, weight, italic and colour and each
//   character's advance, so the receiving app keeps the layout even with another font; WMF's ExtTextOut does the same
//   in one ANSI code page (the font's character set: Western, Korean, Japanese, Chinese, Cyrillic, ...). Text that needs
//   shaping (Arabic, Hebrew, Indic scripts, combining marks), translucent text in EMF, and in WMF text no code page
//   holds or that is squeezed, skewed or clipped, is written as the outlines of its glyphs, as SVG output does.
// - Pictures: EMF StretchDIBits (opaque) or AlphaBlend; WMF StretchDIB. A turned picture is resampled upright first
//   (GDI blits only stretch).
//
// Limits: a writer keeps to the limits the metafile reader keeps (MetafileLimits), so what Filee writes it can also
// read: bitmaps are drawn smaller than a bitmap's pixel limit, and a drawing that needs more records, file bytes or
// objects than the reader takes is refused with the reader's refusal (error.document_too_large or
// error.document_too_complex).

using System.Globalization;
using Filee.Engines.Documents;
using Filee.Engines.Rendering;
using Filee.Engines.Slides;
using SkiaSharp;

namespace Filee.Engines.Metafiles;

/// <summary>A line as GDI draws one: a colour, a width and dashes in points, caps, joins and the miter limit.</summary>
/// <param name="Dash">Dash and gap lengths in points; null = solid.</param>
internal sealed record MetafilePen(SKColor Color, float Width, float[]? Dash = null, SKStrokeCap Cap = SKStrokeCap.Butt,
    SKStrokeJoin Join = SKStrokeJoin.Miter, float Miter = 4)
{
    /// <summary>The outline the line covers along <paramref name="path"/> (for formats or cases a pen can't draw).</summary>
    public SKPath Outline(SKPath path)
    {
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(Width, 0.01f),
            StrokeCap = Cap,
            StrokeJoin = Join,
            StrokeMiter = Miter,
        };
        using var dash = Dash is { Length: >= 2 } ? SKPathEffect.CreateDash(Dash.Length % 2 == 0 ? Dash : [.. Dash, .. Dash], 0) : null;
        paint.PathEffect = dash;
        return paint.GetFillPath(path) ?? new SKPath();
    }
}

internal enum MetafileGradientKind
{
    /// <summary>The colour runs along the frame's x axis, 0 to 1.</summary>
    Linear,

    /// <summary>The colour runs outwards from the frame's origin, the last stop on its unit circle.</summary>
    Radial,
}

/// <summary>A gradient fill: stops along the frame's axis or radius, the frame mapping its own space onto the page.</summary>
internal sealed record MetafileGradient(MetafileGradientKind Kind, SKMatrix Frame, IReadOnlyList<GradientStop> Stops)
{
    /// <summary>The colour at <paramref name="t"/>, clamped to the first and last stops.</summary>
    public SKColor At(float t)
    {
        var stops = Stops;
        if (stops.Count == 0)
            return SKColors.Transparent;
        if (t <= stops[0].Position)
            return stops[0].Color;
        for (var i = 1; i < stops.Count; i++)
        {
            if (t <= stops[i].Position)
            {
                var (a, b) = (stops[i - 1], stops[i]);
                var span = b.Position - a.Position;
                return span <= 0 ? b.Color : Mix(a.Color, b.Color, (t - a.Position) / span);
            }
        }
        return stops[^1].Color;
    }

    public bool Opaque => Stops.All(s => s.Color.Alpha == 255);

    /// <summary>The gradient as a Skia shader in page coordinates (for gradients drawn as bitmaps).</summary>
    public SKShader Shader()
    {
        var colors = Stops.Select(s => s.Color).ToArray();
        var positions = Stops.Select(s => s.Position).ToArray();
        return Kind == MetafileGradientKind.Linear
            ? SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(1, 0), colors, positions, SKShaderTileMode.Clamp, Frame)
            : SKShader.CreateRadialGradient(new SKPoint(0, 0), 1, colors, positions, SKShaderTileMode.Clamp, Frame);
    }

    internal static SKColor Mix(SKColor a, SKColor b, float f)
    {
        f = Math.Clamp(f, 0, 1);
        static byte Lerp(byte x, byte y, float f) => (byte)Math.Round(x + (y - x) * f);
        return new SKColor(Lerp(a.Red, b.Red, f), Lerp(a.Green, b.Green, f), Lerp(a.Blue, b.Blue, f), Lerp(a.Alpha, b.Alpha, f));
    }
}

/// <summary>The font a text run is written in.</summary>
internal sealed record MetafileFace(string Name, int Weight, bool Italic);

/// <summary>Characters of a run at one baseline: the first one's origin (the run's frame) and each one's advance.</summary>
internal sealed record MetafileTextSegment(SKPoint Origin, string Text, float[] Advances);

/// <summary>The drawing calls both writers take, and what they share.</summary>
internal abstract class MetafileWriter
{
    /// <summary>Pixels per point of bitmaps the writer makes itself (gradients drawn as bitmaps, turned pictures).</summary>
    protected const float BitmapPixelsPerPoint = 3;

    /// <summary>Pixels of one such bitmap at most.</summary>
    protected const long MaxMadePixels = 16_000_000;

    private long _records;
    private long _bytes;

    protected MetafileWriter(float width, float height, MetafileLimits? limits)
    {
        if (!(width > 0 && height > 0 && float.IsFinite(width) && float.IsFinite(height)))
            throw new ArgumentException("A metafile needs a positive size.");
        Width = width;
        Height = height;
        Limits = limits ?? MetafileLimits.Default;
    }

    /// <summary>The picture's width in points.</summary>
    public float Width { get; }

    /// <summary>The picture's height in points.</summary>
    public float Height { get; }

    protected MetafileLimits Limits { get; }

    /// <summary>Records written so far, header and end included once finished.</summary>
    public long Records => _records;

    public abstract void Save();

    public abstract void Restore();

    /// <summary>Only what lies inside <paramref name="path"/> is drawn until the matching <see cref="Restore"/>.</summary>
    public abstract void Clip(SKPath path);

    public abstract void Fill(SKPath path, SKColor color);

    public abstract void Stroke(SKPath path, MetafilePen pen);

    /// <summary>Fills <paramref name="path"/> with a gradient: bands of solid colour unless a writer has better.</summary>
    public virtual void Gradient(SKPath path, MetafileGradient gradient) => Bands(path, gradient, c => c);

    /// <summary>The file's bytes; nothing is drawn afterwards.</summary>
    public abstract byte[] ToArray();

    /// <summary>A picture whose pixel (0, 0)–(width, height) <paramref name="matrix"/> maps onto the page.</summary>
    public void Image(SKBitmap bitmap, SKMatrix matrix)
    {
        if (bitmap.Width <= 0 || bitmap.Height <= 0)
            return;
        var dest = matrix.MapRect(new SKRect(0, 0, bitmap.Width, bitmap.Height));
        if (dest.Width <= 0 || dest.Height <= 0 || !float.IsFinite(dest.Left) || !float.IsFinite(dest.Right) || !float.IsFinite(dest.Top) || !float.IsFinite(dest.Bottom))
            return;
        var upright = Math.Abs(matrix.SkewX) < 1e-6f && Math.Abs(matrix.SkewY) < 1e-6f;
        var pixels = (long)bitmap.Width * bitmap.Height;
        if (upright && pixels <= Limits.MaxBitmapPixels)
        {
            using var copy = Bgra(bitmap, matrix.ScaleX < 0, matrix.ScaleY < 0);
            Picture(copy, dest);
            return;
        }
        // Turned (or too large): drawn upright at the picture's own density, within the pixel limits.
        var density = MathF.Sqrt(Math.Abs(matrix.ScaleX * matrix.ScaleY - matrix.SkewX * matrix.SkewY));
        var scale = density > 0 ? 1 / density : BitmapPixelsPerPoint;
        scale = (float)Math.Min(scale, Math.Sqrt(Math.Min(MaxMadePixels, Limits.MaxBitmapPixels) / Math.Max(1e-6, dest.Width * dest.Height)));
        Rasterize(dest, scale, canvas =>
        {
            canvas.Concat(in matrix);
            using var image = SKImage.FromBitmap(bitmap);
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        });
    }

    /// <summary>An upright picture (unpremultiplied BGRA) stretched over <paramref name="dest"/> (points).</summary>
    protected abstract void Picture(SKBitmap bitmap, SKRect dest);

    /// <summary>
    /// Draws <paramref name="draw"/> (page coordinates) into a bitmap over <paramref name="area"/> at
    /// <paramref name="pixelsPerPoint"/> and writes it as a picture.
    /// </summary>
    public void Rasterize(SKRect area, float pixelsPerPoint, Action<SKCanvas> draw)
    {
        area = SKRect.Intersect(area, new SKRect(-Width, -Height, 2 * Width, 2 * Height));
        if (area.Width <= 0 || area.Height <= 0)
            return;
        var limit = Math.Min(MaxMadePixels, Limits.MaxBitmapPixels);
        pixelsPerPoint = (float)Math.Min(pixelsPerPoint, Math.Sqrt(limit / Math.Max(1e-6, area.Width * area.Height)));
        var width = Math.Max(1, (int)Math.Ceiling(area.Width * pixelsPerPoint));
        var height = Math.Max(1, (int)Math.Ceiling(area.Height * pixelsPerPoint));
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(width / area.Width, height / area.Height);
            canvas.Translate(-area.Left, -area.Top);
            draw(canvas);
        }
        using var copy = Bgra(bitmap, false, false);
        Picture(copy, area);
    }

    /// <summary>A copy as unpremultiplied BGRA, mirrored as asked.</summary>
    protected static SKBitmap Bgra(SKBitmap source, bool flipX, bool flipY)
    {
        var info = new SKImageInfo(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var copy = new SKBitmap(info);
        using (var pixmap = source.PeekPixels())
        {
            if (pixmap is null || !pixmap.ReadPixels(info, copy.GetPixels(), copy.RowBytes))
            {
                using var image = SKImage.FromBitmap(source);
                image.ReadPixels(info, copy.GetPixels(), copy.RowBytes);
            }
        }
        if (!flipX && !flipY)
            return copy;
        var flipped = new SKBitmap(info);
        var from = copy.GetPixelSpan();
        unsafe
        {
            var to = new Span<byte>((void*)flipped.GetPixels(), flipped.RowBytes * flipped.Height);
            for (var y = 0; y < info.Height; y++)
            {
                var row = flipY ? info.Height - 1 - y : y;
                for (var x = 0; x < info.Width; x++)
                {
                    var column = flipX ? info.Width - 1 - x : x;
                    from.Slice(row * copy.RowBytes + column * 4, 4).CopyTo(to[(y * flipped.RowBytes + x * 4)..]);
                }
            }
        }
        copy.Dispose();
        return flipped;
    }

    /// <summary>True when some pixel of an unpremultiplied BGRA bitmap isn't opaque.</summary>
    protected static bool HasAlpha(SKBitmap bitmap)
    {
        var pixels = bitmap.GetPixelSpan();
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                if (pixels[y * bitmap.RowBytes + x * 4 + 3] != 255)
                    return true;
        return false;
    }

    // ───────────────────────── Gradients as bands ─────────────────────────

    /// <summary>Bands of a gradient per stop interval at most (fewer where the colours are close).</summary>
    private const int MaxBandsPerStop = 96;

    /// <summary>Bands of one gradient at most.</summary>
    private const int MaxBands = 512;

    /// <summary>
    /// Fills <paramref name="path"/> with the gradient as bands of solid colour (<paramref name="color"/> turns each band's
    /// colour into what is written), each drawn over the last: strips across a linear gradient from its start, discs of
    /// a radial one from the outside in.
    /// </summary>
    protected void Bands(SKPath path, MetafileGradient gradient, Func<SKColor, SKColor> color)
    {
        if (gradient.Stops.Count == 0 || path.IsEmpty)
            return;
        if (!gradient.Frame.TryInvert(out var inverse))
        {
            Fill(path, color(gradient.At(0.5f)));
            return;
        }
        var bounds = inverse.MapRect(path.Bounds);
        if (!(float.IsFinite(bounds.Left) && float.IsFinite(bounds.Right) && float.IsFinite(bounds.Top) && float.IsFinite(bounds.Bottom)))
        {
            Fill(path, color(gradient.At(0.5f)));
            return;
        }
        Save();
        Clip(path);
        if (gradient.Kind == MetafileGradientKind.Linear)
        {
            var (from, to) = (bounds.Left, bounds.Right);
            var breaks = Breaks(gradient, from, to);
            var vertical = (bounds.Top - bounds.Height * 0.01f - 1, bounds.Bottom + bounds.Height * 0.01f + 1);
            // The path's fill is the first band (and what lies before the gradient's start).
            Fill(path, color(gradient.At((breaks[0] + breaks[1]) / 2)));
            for (var i = 1; i + 1 < breaks.Count; i++)
            {
                using var strip = new SKPath();
                strip.AddRect(new SKRect(breaks[i], vertical.Item1, to + 1, vertical.Item2));
                strip.Transform(gradient.Frame);
                Fill(strip, color(gradient.At((breaks[i] + breaks[i + 1]) / 2)));
            }
        }
        else
        {
            var reach = new[] { new SKPoint(bounds.Left, bounds.Top), new SKPoint(bounds.Right, bounds.Top), new SKPoint(bounds.Left, bounds.Bottom), new SKPoint(bounds.Right, bounds.Bottom) }
                .Max(p => p.Length);
            var outer = Math.Min(reach, 1);
            var breaks = Breaks(gradient, 0, outer);
            // Beyond the last stop (or the reach of the path) the colour is the outermost band's.
            Fill(path, color(gradient.At(reach > 1 ? 1 : (breaks[^2] + breaks[^1]) / 2)));
            for (var i = breaks.Count - 1; i > 0; i--)
            {
                if (i == breaks.Count - 1 && reach <= 1)
                    continue; // the path's fill is the outermost band
                using var disc = new SKPath();
                disc.AddCircle(0, 0, breaks[i]);
                disc.Transform(gradient.Frame);
                Fill(disc, color(gradient.At((breaks[i - 1] + breaks[i]) / 2)));
            }
        }
        Restore();
    }

    /// <summary>Where bands of the gradient start and end between <paramref name="from"/> and <paramref name="to"/>, in order.</summary>
    private static List<float> Breaks(MetafileGradient gradient, float from, float to)
    {
        var breaks = new List<float> { from };
        if (to <= from)
        {
            breaks.Add(from + 1e-3f);
            return breaks;
        }
        var stops = gradient.Stops;
        var budget = MaxBands;
        for (var i = 0; i + 1 < stops.Count && budget > 0; i++)
        {
            var (a, b) = (Math.Max(stops[i].Position, from), Math.Min(stops[i + 1].Position, to));
            if (b <= a)
                continue;
            var (ca, cb) = (gradient.At(a), gradient.At(b));
            var delta = Math.Max(Math.Max(Math.Abs(ca.Red - cb.Red), Math.Abs(ca.Green - cb.Green)), Math.Max(Math.Abs(ca.Blue - cb.Blue), Math.Abs(ca.Alpha - cb.Alpha)));
            var steps = Math.Clamp((int)Math.Ceiling(delta / 3.0), 1, Math.Min(MaxBandsPerStop, budget));
            budget -= steps;
            if (a > breaks[^1])
                breaks.Add(a);
            for (var s = 1; s <= steps; s++)
            {
                var t = a + (b - a) * s / steps;
                if (t > breaks[^1])
                    breaks.Add(t);
            }
        }
        if (to > breaks[^1])
            breaks.Add(to);
        if (breaks.Count == 1)
            breaks.Add(to);
        return breaks;
    }

    // ───────────────────────── Text ─────────────────────────

    /// <summary>A glyph run: as text where the format can hold it (<see cref="TryText"/>), else as its glyphs' outlines.</summary>
    public void Text(GlyphRun run)
    {
        if (run.Glyphs.Length == 0)
            return;
        var clipped = run.Clip is { } clip;
        if (clipped)
        {
            Save();
            using var area = new SKPath();
            area.AddRect(run.Clip!.Value);
            if (run.ClipTransform is { } frame)
                area.Transform(frame);
            Clip(area);
        }
        if (Segments(run) is not { } segments || !TryText(run, Face(run), segments))
        {
            using var outlines = Outlines(run);
            if (!outlines.IsEmpty)
                Fill(outlines, run.Color);
        }
        if (clipped)
            Restore();
    }

    /// <summary>Writes the run as text; false when the format can't hold it (it is drawn as outlines then).</summary>
    protected abstract bool TryText(GlyphRun run, MetafileFace face, IReadOnlyList<MetafileTextSegment> segments);

    /// <summary>The face name, weight and slant GDI is asked for: the run's font, with the bold and italic it fakes.</summary>
    internal static MetafileFace Face(GlyphRun run)
    {
        var typeface = run.Font.Typeface;
        var weight = run.Embolden ? 700 : Math.Clamp(typeface.FontWeight, 100, 900);
        return new MetafileFace(typeface.FamilyName, weight, run.SkewX != 0 || typeface.FontSlant != SKFontStyleSlant.Upright);
    }

    /// <summary>The font a run is drawn with, as PageContent draws it.</summary>
    internal static SKFont Font(GlyphRun run) => new(run.Font.Typeface, run.Size)
    {
        ScaleX = run.ScaleX,
        SkewX = run.SkewX,
        Embolden = run.Embolden,
        Subpixel = true,
        Hinting = SKFontHinting.None,
        LinearMetrics = true,
    };

    /// <summary>The run's glyphs as one outline in page coordinates.</summary>
    internal static SKPath Outlines(GlyphRun run)
    {
        using var font = Font(run);
        var outlines = new SKPath();
        for (var i = 0; i < run.Glyphs.Length; i++)
        {
            using var glyph = font.GetGlyphPath(run.Glyphs[i]);
            if (glyph is not null && !glyph.IsEmpty)
                outlines.AddPath(glyph, run.Positions[i].X, run.Positions[i].Y);
        }
        if (run.Transform is { } transform)
            outlines.Transform(transform);
        return outlines;
    }

    /// <summary>
    /// The run's characters by baseline, each with its advance (a glyph standing for several characters shares its
    /// advance out), or null when the glyphs don't stand one to one for the characters in order: right-to-left text,
    /// shaped scripts, combining marks.
    /// </summary>
    internal static List<MetafileTextSegment>? Segments(GlyphRun run)
    {
        var (glyphs, positions, clusters, text) = (run.Glyphs, run.Positions, run.Clusters, run.Text);
        var count = glyphs.Length;
        if (count == 0 || !(run.ScaleX > 0))
            return null;
        for (var i = 1; i < count; i++)
            if (clusters[i] <= clusters[i - 1])
                return null;
        using var font = Font(run);
        var widths = font.GetGlyphWidths(glyphs);
        var segments = new List<MetafileTextSegment>();
        var chars = new System.Text.StringBuilder();
        var advances = new List<float>();
        var origin = positions[0];
        for (var i = 0; i < count; i++)
        {
            var start = clusters[i];
            var end = i + 1 < count ? clusters[i + 1] : text.Length;
            var cluster = text.AsSpan(start, Math.Max(0, end - start));
            if (i + 1 == count)
                cluster = cluster.Length > 1 ? cluster[..1].ToString() + cluster[1..].TrimEnd().ToString() : cluster;
            if (cluster.IsEmpty)
                return null;
            foreach (var c in cluster)
                if (NeedsShaping(c))
                    return null;
            var sameLine = i + 1 < count && Math.Abs(positions[i + 1].Y - positions[i].Y) < 1e-3f && positions[i + 1].X >= positions[i].X;
            var advance = sameLine ? positions[i + 1].X - positions[i].X : widths[i];
            // A cluster of several characters shares its glyph's advance out, a surrogate pair counting once.
            var units = 0;
            for (var k = 0; k < cluster.Length; k++)
                if (!char.IsLowSurrogate(cluster[k]))
                    units++;
            foreach (var c in cluster)
            {
                chars.Append(c);
                advances.Add(char.IsLowSurrogate(c) ? 0 : advance / Math.Max(1, units));
            }
            if (!sameLine)
            {
                segments.Add(new MetafileTextSegment(origin, chars.ToString(), [.. advances]));
                chars.Clear();
                advances.Clear();
                if (i + 1 < count)
                    origin = positions[i + 1];
            }
        }
        return segments;
    }

    /// <summary>Characters a renderer has to shape, which GDI draws one by one only as the font's plain glyphs.</summary>
    private static bool NeedsShaping(char c)
    {
        if (c < 0x20 || c is >= '֐' and <= 'ࣿ' or >= 'ऀ' and <= '෿' or >= '฀' and <= '࿿'
                or >= 'က' and <= '႟' or >= 'ᄀ' and <= 'ᇿ' or >= 'ក' and <= '᢯'
                or >= 'ꥠ' and <= '꥿' or >= 'ힰ' and <= '퟿' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿'
                or '‌' or '‍')
            return true;
        return char.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
    }

    // ───────────────────────── Limits ─────────────────────────

    /// <summary>Counts a record of <paramref name="bytes"/> against the limits.</summary>
    protected void Count(long bytes)
    {
        _records++;
        _bytes += bytes;
        if (_records > Limits.MaxRecords)
            throw MetafileLimits.Exceeded($"The drawing needs more than {DocumentLimits.Count(Limits.MaxRecords)} metafile records.");
        if (_bytes > Limits.MaxFileBytes)
            throw MetafileLimits.TooLarge($"The metafile would be larger than {DocumentLimits.Megabytes(Limits.MaxFileBytes)}.");
    }

    /// <summary>A colour as a COLORREF (0x00BBGGRR).</summary>
    protected static uint ColorRef(SKColor color) => color.Red | (uint)color.Green << 8 | (uint)color.Blue << 16;

    /// <summary>A translucent colour mixed with white (what it shows on a white page), opaque.</summary>
    protected static SKColor OnWhite(SKColor color) =>
        color.Alpha == 255 ? color : MetafileGradient.Mix(SKColors.White, color.WithAlpha(255), color.Alpha / 255f);
}
