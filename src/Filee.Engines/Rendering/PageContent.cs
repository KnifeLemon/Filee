// The drawing of one page as the layout engine hands it to output: shaped glyph runs with the text they stand for,
// other drawing (paths, pictures) as Skia calls, links and link targets. PdfPageWriter turns pages into a PDF and
// PageContent.Draw into pixels, both from the same record.
//
// Coordinates are PDF points from the page's top-left corner, y downwards (Skia's convention).

using SkiaSharp;

namespace Filee.Engines.Rendering;

/// <summary>
/// A shaped run of glyphs in one font and size. <see cref="Clusters"/> gives, per glyph, the UTF-16 index in
/// <see cref="Text"/> where the glyph's cluster starts, as HarfBuzz reports it (in visual order, so descending for
/// right-to-left text). Glyph positions are baseline origins in page coordinates.
/// </summary>
internal sealed record GlyphRun(PageFont Font, float Size, ushort[] Glyphs, SKPoint[] Positions, string Text, int[] Clusters)
{
    /// <summary>Fill colour of the glyphs.</summary>
    public SKColor Color { get; init; } = SKColors.Black;

    /// <summary>Horizontal scale of the glyphs (1 = as designed; 한글's 장평).</summary>
    public float ScaleX { get; init; } = 1;

    /// <summary>Bold the font lacks, drawn by thickening the outlines.</summary>
    public bool Embolden { get; init; }

    /// <summary>Horizontal skew of the glyphs (italic the font lacks: <see cref="SyntheticSlant"/>), 0 = upright.</summary>
    public float SkewX { get; init; }

    /// <summary>The skew of synthetic italic, as Skia and browsers slant (about 12°).</summary>
    public const float SyntheticSlant = -0.2f;

    /// <summary>
    /// Maps the run's positions onto the page (rotated or mirrored text: a slide's turned shape, vertical text); null =
    /// the positions are page coordinates.
    /// </summary>
    public SKMatrix? Transform { get; init; }

    /// <summary>Only what lies inside this rectangle of the page (before <see cref="Transform"/>) is drawn; null = all.</summary>
    public SKRect? Clip { get; init; }

    /// <summary>Throws when the arrays don't describe the same glyphs or a cluster points outside the text.</summary>
    public void Validate()
    {
        if (Positions.Length != Glyphs.Length || Clusters.Length != Glyphs.Length)
            throw new ArgumentException("A glyph run needs one position and one cluster per glyph.");
        if (Clusters.Any(c => c < 0 || c > Text.Length))
            throw new ArgumentException("A glyph run's cluster points outside its text.");
        if (Size <= 0 || !float.IsFinite(Size))
            throw new ArgumentException("A glyph run needs a positive font size.");
    }
}

/// <summary>An outline (bookmark) entry: a heading's title and where it is.</summary>
/// <param name="Level">Nesting level, 0 for top-level entries.</param>
/// <param name="Top">Distance of the target from the top of the page, in points.</param>
internal sealed record OutlineEntry(string Title, int PageIndex, float Top, int Level);

/// <summary>Everything drawn on one page, in drawing order.</summary>
internal sealed class PageContent(float width, float height)
{
    private readonly List<PageItem> _items = [];

    /// <summary>How many items lie under everything else (<see cref="Add(PageContent, bool)"/> with under).</summary>
    private int _under;

    /// <summary>Page width in points.</summary>
    public float Width { get; } = width;

    /// <summary>Page height in points.</summary>
    public float Height { get; } = height;

    public IReadOnlyList<PageItem> Items => _items;

    /// <summary>Draws a glyph run.</summary>
    public void DrawGlyphs(GlyphRun run)
    {
        run.Validate();
        _items.Add(new GlyphItem(run));
    }

    /// <summary>Draws something other than text (paths, fills, pictures) with Skia.</summary>
    public void Draw(Action<SKCanvas> drawing) => _items.Add(new DrawingItem(drawing));

    /// <summary>
    /// Adds what <paramref name="layer"/> holds: over everything so far, or <paramref name="under"/> it (over what was
    /// added under before).
    /// </summary>
    public void Add(PageContent layer, bool under)
    {
        if (!under)
        {
            _items.AddRange(layer._items);
            return;
        }
        _items.InsertRange(_under, layer._items);
        _under += layer._items.Count;
    }

    /// <summary>
    /// Adds what <paramref name="layer"/> holds over everything so far, drawn through <paramref name="transform"/>
    /// (a turned or mirrored shape and its text). Link areas take the bounds of their mapped rectangle.
    /// </summary>
    public void Add(PageContent layer, SKMatrix transform)
    {
        if (transform.IsIdentity)
        {
            _items.AddRange(layer._items);
            return;
        }
        foreach (var item in layer._items)
        {
            _items.Add(item switch
            {
                GlyphItem glyphs => new GlyphItem(glyphs.Run with { Transform = glyphs.Run.Transform is { } inner ? transform.PreConcat(inner) : transform }),
                DrawingItem drawing => new DrawingItem(canvas =>
                {
                    canvas.Concat(in transform);
                    drawing.Drawing(canvas);
                }),
                UrlLinkItem link => new UrlLinkItem(transform.MapRect(link.Area), link.Url),
                DestinationLinkItem link => new DestinationLinkItem(transform.MapRect(link.Area), link.Name),
                DestinationItem destination => new DestinationItem(transform.MapPoint(destination.Point), destination.Name),
                _ => item,
            });
        }
    }

    /// <summary>Makes <paramref name="area"/> a link to <paramref name="url"/>.</summary>
    public void LinkToUrl(SKRect area, string url) => _items.Add(new UrlLinkItem(area, url));

    /// <summary>Makes <paramref name="area"/> a link to the named destination <paramref name="name"/>.</summary>
    public void LinkToDestination(SKRect area, string name) => _items.Add(new DestinationLinkItem(area, name));

    /// <summary>Defines the named destination <paramref name="name"/> at <paramref name="point"/>.</summary>
    public void Destination(SKPoint point, string name) => _items.Add(new DestinationItem(point, name));

    /// <summary>The page as a picture at <paramref name="dpi"/>, on white (unhinted text, as PDF viewers draw it).</summary>
    public SKBitmap Render(float dpi)
    {
        var scale = dpi / 72f;
        var bitmap = new SKBitmap(Math.Max(1, (int)Math.Round(Width * scale)), Math.Max(1, (int)Math.Round(Height * scale)),
            SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        canvas.Scale(scale);
        Draw(canvas);
        return bitmap;
    }

    /// <summary>
    /// Draws the page onto <paramref name="canvas"/>, glyph runs with the typeface <paramref name="typeface"/> gives for
    /// their font (PDF: the subset; pictures: the whole font).
    /// </summary>
    public void Draw(SKCanvas canvas, Func<PageFont, SKTypeface>? typeface = null)
    {
        typeface ??= f => f.Typeface;
        foreach (var item in _items)
        {
            switch (item)
            {
                case GlyphItem glyphs:
                    DrawRun(canvas, glyphs.Run, typeface(glyphs.Run.Font));
                    break;
                case DrawingItem drawing:
                    canvas.Save();
                    drawing.Drawing(canvas);
                    canvas.Restore();
                    break;
                case UrlLinkItem link:
                    canvas.DrawUrlAnnotation(link.Area, link.Url)?.Dispose();
                    break;
                case DestinationLinkItem link:
                    canvas.DrawLinkDestinationAnnotation(link.Area, link.Name)?.Dispose();
                    break;
                case DestinationItem destination:
                    canvas.DrawNamedDestinationAnnotation(destination.Point, destination.Name)?.Dispose();
                    break;
            }
        }
    }

    /// <summary>
    /// Draws a run as one text blob that carries the run's text and clusters, so Skia's PDF backend writes a ToUnicode
    /// map for one-to-one glyphs and /ActualText for the others (ligatures, conjuncts, reordered vowel signs).
    /// Right-to-left runs are drawn in logical order (their glyphs reversed, each at its own position), so readers that
    /// take text in content order get it the way it was written.
    /// </summary>
    private static void DrawRun(SKCanvas canvas, GlyphRun run, SKTypeface typeface)
    {
        if (run.Glyphs.Length == 0)
            return;
        var (glyphs, positions, clusters) = LogicalOrder(run);
        var (utf8, offsets) = Utf8Clusters(run.Text, clusters);

        // Unhinted outlines at subpixel positions: what PDF viewers draw, so pictures of a page match its PDF.
        using var font = new SKFont(typeface, run.Size)
        {
            ScaleX = run.ScaleX,
            SkewX = run.SkewX,
            Embolden = run.Embolden,
            Subpixel = true,
            Edging = SKFontEdging.Antialias,
            Hinting = SKFontHinting.None,
            LinearMetrics = true,
        };
        using var builder = new SKTextBlobBuilder();
        var buffer = builder.AllocatePositionedTextRun(font, glyphs.Length, utf8.Length, null);
        buffer.SetGlyphs(glyphs);
        buffer.SetPositions(positions);
        buffer.SetText(utf8);
        buffer.SetClusters(offsets);
        using var blob = builder.Build();
        if (blob is null)
            return;
        using var paint = new SKPaint { Color = run.Color, IsAntialias = true };
        if (run.Transform is null && run.Clip is null)
        {
            canvas.DrawText(blob, 0, 0, paint);
            return;
        }
        canvas.Save();
        if (run.Clip is { } clip)
            canvas.ClipRect(clip);
        if (run.Transform is { } transform)
            canvas.Concat(in transform);
        canvas.DrawText(blob, 0, 0, paint);
        canvas.Restore();
    }

    /// <summary>
    /// The run's arrays in logical order: reversed when the clusters descend (right-to-left). Clusters that are still
    /// out of order (a shaper that reorders across clusters) become one cluster spanning the run.
    /// </summary>
    internal static (ushort[] Glyphs, SKPoint[] Positions, int[] Clusters) LogicalOrder(GlyphRun run)
    {
        var glyphs = run.Glyphs;
        var positions = run.Positions;
        var clusters = run.Clusters;
        if (clusters.Length > 1 && clusters[0] > clusters[^1])
        {
            glyphs = [.. glyphs.Reverse()];
            positions = [.. positions.Reverse()];
            clusters = [.. clusters.Reverse()];
        }
        for (var i = 1; i < clusters.Length; i++)
        {
            if (clusters[i] < clusters[i - 1])
            {
                clusters = new int[clusters.Length];
                break;
            }
        }
        return (glyphs, positions, clusters);
    }

    /// <summary>The text as UTF-8 and each glyph's cluster as a byte offset into it (what Skia's text blobs take).</summary>
    internal static (byte[] Utf8, uint[] Offsets) Utf8Clusters(string text, int[] clusters)
    {
        var byteOffsets = new int[text.Length + 1];
        var position = 0;
        for (var i = 0; i < text.Length; i++)
        {
            byteOffsets[i] = position;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                byteOffsets[++i] = position;
                position += 4;
            }
            else
            {
                position += text[i] < 0x80 ? 1 : text[i] < 0x800 ? 2 : 3;
            }
        }
        byteOffsets[text.Length] = position;
        var utf8 = System.Text.Encoding.UTF8.GetBytes(text);
        return (utf8, [.. clusters.Select(c => (uint)byteOffsets[c])]);
    }
}

/// <summary>One drawing step of a page.</summary>
internal abstract record PageItem;

internal sealed record GlyphItem(GlyphRun Run) : PageItem;

internal sealed record DrawingItem(Action<SKCanvas> Drawing) : PageItem;

internal sealed record UrlLinkItem(SKRect Area, string Url) : PageItem;

internal sealed record DestinationLinkItem(SKRect Area, string Name) : PageItem;

internal sealed record DestinationItem(SKPoint Point, string Name) : PageItem;
