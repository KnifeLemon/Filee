// PDF pages as editable slides (PDF → PPTX, PdfSlidesConverter): each page is read like PDF → DOCX (ReadPage: words →
// lines → paragraphs with their fonts, sizes, colours, bold / italic and links; ruled tables; pictures), but every
// paragraph keeps its place on the page as a text box of its own instead of joining the flow of a document.
//
// - Text: one box per paragraph, as wide as its lines (with a little room for a wider font), its first baseline where
//   the PDF has it. A paragraph of one line doesn't wrap, so a substitute font never breaks it in two; longer ones wrap
//   at the box's width, so edited text flows. Alignment, indents, tabs and the line distance come from the PDF.
//   Words far apart on one baseline (columns, table-like layouts, a label and its value) are boxes of their own: a
//   row's parts that the document reading joins with a tab are split again (not the tab after a list number), and so
//   is a gap much wider than the page's spaces and the line's other gaps (SlideParagraphs). Lines of a justified
//   paragraph keep their wide spaces.
// - Text that runs sideways or turned (any angle) is read the same way in its own direction and becomes text boxes
//   turned by that angle.
// - Ruled tables become real tables (the cells' text, borders, shading and sizes as for DOCX); pictures in their
//   cells stay pictures on top of them.
// - Pictures are picture shapes at their place, also one as large as the page (a slide's background picture or a
//   scan): the slides keep everything that is seen, nothing is a "scan under an OCR layer" here. A picture drawn
//   turned or mirrored is a turned or flipped picture shape; a stencil mask (an image mask) a picture of its colour.
// - Vector graphics: a filled or stroked rectangle becomes a rectangle shape, a straight line a line shape, so they
//   can be moved and recoloured, with the PDF's transparency (ca / CA); a rectangle filling the page is the slide's
//   background colour. Paths and "sh" areas filled with an axial or radial shading become shapes with a gradient fill
//   (their own outline as custom geometry), as do solid fills under a soft mask that is one shading (an alpha
//   gradient), and function-based and mesh shadings that look like a linear or radial gradient over all of the shape
//   (PdfShadingPictures). A tiling pattern whose cells repeat upright is a picture fill of its cell (a:blipFill with
//   a:tile, the cell's PNG at 96 dpi). Everything else (curves, polygons, other shadings, fills under a soft mask Filee
//   drew, more than MaxShapes shapes on one page) is drawn into transparent pictures of the page's graphics (Skia),
//   line widths and dashes as the transformation they were drawn under makes them.
// - Blend modes: Multiply and Darken over text painted before them (a highlight) put the shape or picture under that
//   text, which then shows through as it does in the PDF. The other modes, blended pictures, and Multiply / Darken over
//   anything else painted before them are drawn over their backdrop (Backdrops): the page's colour and, where earlier
//   pictures, text or shapes lie under them, those drawn again into the same picture under the blend, which then
//   covers them there (the text stays a text box underneath). A picture under a soft mask other than a constant alpha
//   gets the mask in its own alpha.
//   Rules and shading of a ruled table are the table's own borders and fills, so they are not repeated as shapes.
// - Clipping: a clipping rectangle crops the pictures and rectangles it cuts (srcRect, a smaller rectangle); another
//   clipping path is a picture's custom geometry and is intersected with a shape's outline; strokes it cuts are drawn.
// - Drawing order on the slide: the PDF's own (PdfSlideDrawing records it): text boxes, tables, shapes and pictures
//   stack as the page paints them. Drawn graphics share a picture as long as nothing painted between them overlaps
//   them.
// - Speaker notes: the contents of the page's comment annotations (sticky notes, free text, highlights and other
//   text markup), top to bottom, with their author.
//
// Not rebuilt (left for later): form fields, the appearance of annotations, text inside pattern cells, vertical CJK
// writing. Blends past MaxBackdrops a page see only their own picture's graphics.

using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Annotations;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Exceptions;
using UglyToad.PdfPig.Graphics;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.Graphics.Operations;
using UglyToad.PdfPig.Graphics.Operations.PathPainting;
using UglyToad.PdfPig.Graphics.Operations.SpecialGraphicsState;
using UglyToad.PdfPig.Tokens;

namespace Filee.Engines.Pdf;

/// <summary>A PDF page as the content of one editable slide: its size (points) and what goes on it, back to front.</summary>
/// <param name="Background">"#RRGGBB" of a rectangle filling the page; null = white.</param>
/// <param name="Notes">The slide's speaker notes (lines separated by LF); null for none.</param>
internal sealed record PdfSlidePage(double Width, double Height, string? Background, IReadOnlyList<PdfSlideItem> Items, string? Notes = null);

/// <summary>Something on a slide in its box: points from the page's top-left corner, y downwards.</summary>
internal abstract record PdfSlideItem(SKRect Bounds)
{
    /// <summary>Degrees clockwise the box is turned about its centre.</summary>
    public float Rotation { get; init; }
}

/// <summary>A text box holding one paragraph; <paramref name="Wrap"/> = its lines wrap at the box's width.</summary>
internal sealed record PdfSlideText(SKRect Bounds, HParagraph Paragraph, bool Wrap) : PdfSlideItem(Bounds);

/// <summary>A picture (a JPEG or PNG file in the media folder) stretched over its box.</summary>
internal sealed record PdfSlidePicture(SKRect Bounds, string Path) : PdfSlideItem(Bounds)
{
    public bool FlipHorizontal { get; init; }
    public bool FlipVertical { get; init; }

    /// <summary>What is cut off the picture on each side (shares of its size: left, top, right, bottom).</summary>
    public SKRect Crop { get; init; }

    public double Alpha { get; init; } = 1;

    /// <summary>The outline the picture is cut to (a clipping path); null = its box.</summary>
    public PdfGeometry? Geometry { get; init; }
}

/// <summary>A ruled table: the model table, its column widths and row heights in points.</summary>
internal sealed record PdfSlideTable(SKRect Bounds, HTable Table, float[] Columns, float[] Rows) : PdfSlideItem(Bounds);

internal enum PdfShapeKind
{
    Rectangle,

    /// <summary>A straight line across its box, from the top-left corner unless flipped.</summary>
    Line,

    /// <summary>An outline of its own (<see cref="PdfSlideShape.Geometry"/>).</summary>
    Custom,
}

/// <summary>
/// A picture fill repeating a tiling pattern's cell: the cell's PNG, where a tile starts in the shape's box (points
/// from its top-left corner), the tiles' scale (of the picture's size at 96 dpi) and the fill's alpha.
/// </summary>
internal sealed record PdfSlideTile(string Path, SKPoint Offset, float ScaleX, float ScaleY, double Alpha);

/// <summary>A rectangle, a line or an outline; colours "#RRGGBB", null = none; the line width in points.</summary>
internal sealed record PdfSlideShape(SKRect Bounds, PdfShapeKind Kind, string? Fill, string? Line, float LineWidth, bool Dashed,
    bool FlipHorizontal = false, bool FlipVertical = false) : PdfSlideItem(Bounds)
{
    public double FillAlpha { get; init; } = 1;
    public double LineAlpha { get; init; } = 1;

    /// <summary>A gradient instead of <see cref="PdfSlideShape.Fill"/>.</summary>
    public PdfSlideGradient? Gradient { get; init; }

    /// <summary>A tiled picture instead of <see cref="PdfSlideShape.Fill"/>.</summary>
    public PdfSlideTile? Tile { get; init; }

    public PdfGeometry? Geometry { get; init; }
}

/// <summary>
/// A DrawingML gradient over a shape's box: linear at <paramref name="Angle"/> (degrees clockwise), or circular from the
/// point <paramref name="Focus"/> (its left and top as shares of the box) outwards; stops at shares of the way.
/// </summary>
internal sealed record PdfSlideGradient(bool Radial, double Angle, SKPoint Focus, IReadOnlyList<(double Position, SKColor Color)> Stops);

/// <summary>An outline in its box's coordinates (points from the box's top-left corner).</summary>
internal sealed record PdfGeometry(IReadOnlyList<(SKPathVerb Verb, SKPoint[] Points)> Commands)
{
    /// <summary>Segments a custom outline has at most; a longer one is drawn instead.</summary>
    internal const int MaxCommands = 5000;

    /// <summary><paramref name="path"/> moved by <paramref name="toBox"/> into its box; null when it is longer than <see cref="MaxCommands"/>.</summary>
    public static PdfGeometry? From(SKPath path, SKMatrix toBox)
    {
        var commands = new List<(SKPathVerb, SKPoint[])>();
        using var iterator = path.CreateRawIterator();
        var points = new SKPoint[4];
        SKPathVerb verb;
        while ((verb = iterator.Next(points)) != SKPathVerb.Done)
        {
            if (commands.Count >= MaxCommands)
                return null;
            switch (verb)
            {
                case SKPathVerb.Move:
                    commands.Add((verb, [toBox.MapPoint(points[0])]));
                    break;
                case SKPathVerb.Line:
                    commands.Add((verb, [toBox.MapPoint(points[1])]));
                    break;
                case SKPathVerb.Quad:
                    commands.Add((verb, [toBox.MapPoint(points[1]), toBox.MapPoint(points[2])]));
                    break;
                case SKPathVerb.Conic:
                    var quads = SKPath.ConvertConicToQuads(points[0], points[1], points[2], iterator.ConicWeight(), 1);
                    for (var i = 1; i + 1 < quads.Length; i += 2)
                        commands.Add((SKPathVerb.Quad, [toBox.MapPoint(quads[i]), toBox.MapPoint(quads[i + 1])]));
                    break;
                case SKPathVerb.Cubic:
                    commands.Add((verb, [toBox.MapPoint(points[1]), toBox.MapPoint(points[2]), toBox.MapPoint(points[3])]));
                    break;
                case SKPathVerb.Close:
                    commands.Add((verb, []));
                    break;
            }
        }
        return commands.Count == 0 ? null : new PdfGeometry(commands);
    }
}

internal sealed partial class PdfDocumentReader
{
    /// <summary>Simple shapes one slide gets at most; a page with more (a chart of bars) has them drawn into its picture.</summary>
    internal const int MaxShapes = 1000;

    /// <summary>Line segments of one page's graphics drawn at most; a page with more is drawn up to there.</summary>
    internal const int MaxGraphicsSegments = 500_000;

    /// <summary>A page's graphics picture has at most this many pixels (the resolution drops for a larger page).</summary>
    internal const double MaxGraphicsPixels = 25_000_000;

    /// <summary>Pictures of drawn graphics one slide gets at most; graphics after that share the last one.</summary>
    internal const int MaxGraphicsPictures = 32;

    /// <summary>How far above its baseline a line's box starts, in ems (about a Latin font's ascender).</summary>
    private const double SlideAscent = 0.92;

    /// <summary>How far below the last baseline a text box ends, in ems.</summary>
    private const double SlideDescent = 0.3;

    /// <summary>Characters of speaker notes a slide gets at most.</summary>
    private const int MaxNotes = 100_000;

    private static readonly Dictionary<double, int> NoHeadings = [];

    /// <summary>
    /// Reads <paramref name="path"/> for slides, one page at a time: <paramref name="pagesOf"/> picks the pages
    /// (zero-based) from the page count, <paramref name="slide"/> gets each page as it is read. Pictures go into
    /// <paramref name="mediaFolder"/>; vector graphics are drawn at <paramref name="dpi"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The PDF is password-protected.</exception>
    public static void ReadSlides(string path, Func<int, IReadOnlyList<int>> pagesOf, string mediaFolder, int dpi, Action<PdfSlidePage> slide,
        CancellationToken cancellationToken = default)
    {
        var reader = new PdfDocumentReader(path, mediaFolder, textOnly: false) { _slides = true };
        var budget = new DocumentBudget(DocumentLimits.Default);
        PdfDocument pdf;
        try
        {
            // Without PdfPig's clipping of paths: it turns curves into a few straight segments, which shows on a large
            // curve. The paint order, clipping and transparency come from PdfSlideDrawing.
            pdf = PdfDocument.Open(path, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
        }
        catch (PdfDocumentEncryptedException)
        {
            throw PasswordProtected();
        }

        using (pdf)
        {
            pdf.AddPageFactory<PdfSlideDrawing, PdfSlideDrawingFactory>();
            var pages = pagesOf(pdf.NumberOfPages);
            if (pages.Count > budget.Limits.MaxPages)
                throw DocumentLimits.TooLarge($"The PDF has more than {DocumentLimits.Count(budget.Limits.MaxPages)} pages to put on slides.");
            foreach (var index in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Page page;
                PdfSlideDrawing drawing;
                try
                {
                    page = pdf.GetPage(index + 1);
                    drawing = pdf.GetPage<PdfSlideDrawing>(index + 1);
                }
                catch (PdfDocumentEncryptedException)
                {
                    throw PasswordProtected();
                }
                using (drawing)
                    slide(reader.SlidePage(page, drawing, dpi, budget, cancellationToken));
            }
        }
    }

    private PdfSlidePage SlidePage(Page page, PdfSlideDrawing drawing, int dpi, DocumentBudget budget, CancellationToken cancellationToken)
    {
        var content = ReadPage(page);
        _tileFiles.Clear();
        var (width, height) = (content.Width, content.Height);
        var notes = Notes(page);
        var items = new List<PdfSlideItem>();
        if (content.Scanned)
        {
            if (content.ScanPath is { } scan)
            {
                budget.Object();
                items.Add(new PdfSlidePicture(new SKRect(0, 0, (float)width, (float)height), scan));
            }
            return new PdfSlidePage(width, height, null, items, notes);
        }

        var texts = SlideParagraphs(content.Items.Where(i => !i.IsPicture && !i.IsTable).ToList());
        var ordered = ReadingOrder([.. texts, .. content.Items.Where(i => i.IsPicture || i.IsTable)]);
        texts = ordered.Where(i => !i.IsPicture && !i.IsTable).ToList();
        var lineRatio = TypicalLineRatio(texts);
        var tables = ordered.Where(i => i.IsTable).ToList();
        var glyphs = new GlyphIndex([.. drawing.Glyphs.Where(g => !g.Turned)]);

        // Everything with the place in the PDF's paint order it is put on the slide at.
        var tableBoxes = tables.Select(t => Box(t.Table!.Bounds, height)).ToList();
        var (background, entries, tableOrders) = SlideGraphics(drawing, width, height, tableBoxes, budget);
        for (var t = 0; t < tables.Count; t++)
        {
            budget.Object();
            var order = tableOrders[t] ?? tables[t].Table!.Cells.SelectMany(c => c.Words).Select(glyphs.Order).Min() ?? int.MaxValue;
            entries.Add(new Entry(order, SlideTable(tables[t], height, lineRatio, budget), null, default));
        }
        var pictures = ordered.Where(i => i.IsPicture).Concat(tables.SelectMany(t => t.Table!.Cells.SelectMany(c => c.Pictures))).ToList();
        entries.AddRange(SlidePictures(pictures, drawing, width, height, budget));
        foreach (var text in texts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            budget.Object();
            budget.Paragraph();
            entries.Add(new Entry(glyphs.Order(text) ?? int.MaxValue, TextBox(text, height, lineRatio), null, default));
        }
        entries.AddRange(TurnedTexts(page, drawing, height, lineRatio, budget, cancellationToken));
        Blended(entries);

        var backdrop = background is null ? SKColors.White : SKColor.Parse(background);
        items.AddRange(Stacked(entries.OrderBy(e => e.Order).ToList(), drawing, width, height, dpi, backdrop, budget, cancellationToken));
        return new PdfSlidePage(width, height, background, items, notes);
    }

    /// <summary>
    /// Something for the slide at its place in the paint order: a slide item, or (<paramref name="Item"/> null) a paint
    /// to draw into a picture over <paramref name="Area"/>. A shape keeps the paint it came from in <paramref name="Paint"/>.
    /// </summary>
    private sealed record Entry(int Order, PdfSlideItem? Item, PdfPaint? Paint, SKRect Area);

    /// <summary>
    /// A shape or picture painted with Multiply or Darken over text painted before it goes under the earliest such text
    /// (dark text over a highlight looks as the blend shows it), unless something else painted in between overlaps it.
    /// </summary>
    private static void Blended(List<Entry> entries)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.Paint is not { Blend: SKBlendMode.Multiply or SKBlendMode.Darken } || entry.Item is not (PdfSlideShape or PdfSlidePicture) || entry.Order < 0)
                continue;
            var area = Shown(entry.Item);
            Entry? under = null;
            foreach (var text in entries)
            {
                if (text.Item is PdfSlideText && text.Order < entry.Order && Shown(text.Item).IntersectsWith(area) && (under is null || text.Order < under.Order))
                    under = text;
            }
            if (under is null)
                continue;
            var between = entries.Any(o => !ReferenceEquals(o, entry) && o.Item is not PdfSlideText && o.Order >= under.Order && o.Order < entry.Order
                                           && !ReferenceEquals(o.Paint, entry.Paint) && (o.Item is { } item ? Shown(item) : o.Area).IntersectsWith(area));
            if (!between)
                entries[i] = entry with { Order = under.Order };
        }
    }

    /// <summary>A PDF box (y upwards) on a page <paramref name="height"/> points high as a slide box (y downwards).</summary>
    private static SKRect Box(PdfRectangle box, double height) =>
        new((float)box.Left, (float)(height - box.Top), (float)box.Right, (float)(height - box.Bottom));

    /// <summary>The upright box a turned item covers.</summary>
    private static SKRect Shown(PdfSlideItem item)
    {
        if (item.Rotation == 0)
            return item.Bounds;
        var turn = SKMatrix.CreateRotationDegrees(item.Rotation, item.Bounds.MidX, item.Bounds.MidY);
        return turn.MapRect(item.Bounds);
    }

    /// <summary>
    /// The entries in order as slide items: drawn paints gather into pictures, each one put where its first paint is.
    /// A paint joins the last picture unless something put after that picture overlaps it (it would end up under
    /// that); after <see cref="MaxGraphicsPictures"/> pictures it joins the last one anyway. Blended paints are drawn
    /// over their backdrop (see <see cref="Backdrops"/>).
    /// </summary>
    private List<PdfSlideItem> Stacked(List<Entry> entries, PdfSlideDrawing drawing, double width, double height, int dpi, SKColor page,
        DocumentBudget budget, CancellationToken cancellationToken)
    {
        Backdrops(entries, drawing, page, new SKRect(0, 0, (float)width, (float)height));

        // Too many shapes (a chart of bars): their paints are drawn instead.
        if (entries.Count(e => e.Item is PdfSlideShape) > MaxShapes)
        {
            var drawn = new HashSet<PdfPaint>(ReferenceEqualityComparer.Instance);
            entries = [.. entries.Where(e => e.Item is not PdfSlideShape || e.Paint is null || drawn.Add(e.Paint))
                .Select(e => e.Item is PdfSlideShape && e.Paint is not null ? e with { Item = null } : e)];
        }

        var slots = new List<PdfSlideItem?>();
        var groups = new List<(int Slot, List<Entry> Paints, List<SKRect> Above)>();
        foreach (var entry in entries)
        {
            if (entry.Item is { } item)
            {
                slots.Add(item);
                if (groups.Count > 0)
                    groups[^1].Above.Add(Shown(item));
                continue;
            }
            var area = entry.Area;
            if (groups.Count > 0 && (groups.Count >= MaxGraphicsPictures || !groups[^1].Above.Any(a => a.IntersectsWith(area))))
            {
                groups[^1].Paints.Add(entry);
                continue;
            }
            groups.Add((slots.Count, [entry], []));
            slots.Add(null);
        }
        using var cache = new PdfRasterCache();
        foreach (var (slot, paints, _) in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DrawGraphics(paints, width, height, dpi, cache, cancellationToken) is { } picture)
            {
                budget.Object();
                slots[slot] = picture;
            }
        }
        return [.. slots.OfType<PdfSlideItem>()];
    }

    /// <summary>Blended paints that get the page's earlier paints drawn under them, on one page at most.</summary>
    internal const int MaxBackdrops = 64;

    /// <summary>Earlier paints drawn under blended ones, on one page at most.</summary>
    internal const int MaxBackdropPaints = 50_000;

    /// <summary>
    /// What blended paints are drawn over, as the PDF blends them with what is under them:
    /// <list type="bullet">
    /// <item>A blended picture is drawn (a picture shape can't blend), unless it is a Multiply or Darken one that
    /// nothing painted before it lies under (blending with the white page changes nothing). So is a Multiply or Darken
    /// shape over something painted before it.</item>
    /// <item>A drawn blended paint that no slide item painted before it overlaps is blended with <paramref name="page"/>
    /// (the slide's background) and the graphics of its own picture.</item>
    /// <item>One over earlier pictures, text or shapes gets the page colour and everything painted before it there (its
    /// <see cref="PdfPaint.Below"/>) drawn under it inside its own picture, which then covers those items where it
    /// paints. Up to <see cref="MaxBackdrops"/> paints and <see cref="MaxBackdropPaints"/> earlier paints a page; past
    /// that a blend sees only its own picture's graphics.</item>
    /// </list>
    /// </summary>
    private static void Backdrops(List<Entry> entries, PdfSlideDrawing drawing, SKColor page, SKRect pageBox)
    {
        var converted = new HashSet<PdfPaint>(ReferenceEqualityComparer.Instance);
        var (backdrops, paints) = (0, 0);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.Paint is not { Blend: not SKBlendMode.SrcOver } blended)
                continue;
            if (entry.Item is PdfSlideShape or PdfSlidePicture)
            {
                var shown = Shown(entry.Item);
                var under = entries.Take(i).Any(o => (o.Item is { } item ? Shown(item) : o.Area).IntersectsWith(shown));
                if (blended.Blend is SKBlendMode.Multiply or SKBlendMode.Darken && !under)
                    continue;
                // A path made into several shapes is drawn once.
                if (!converted.Add(blended))
                {
                    entries.RemoveAt(i--);
                    continue;
                }
                var area = entry.Area.IsEmpty ? shown : entry.Area;
                if (blended.Clip is { } clip)
                    area.Intersect(clip.Bounds);
                area.Intersect(pageBox);
                entries[i] = entry = new Entry(entry.Order, null, blended, area);
            }
            if (entry.Item is not null)
                continue;
            var box = entry.Area;
            if (!entries.Take(i).Any(o => o.Item is { } item && Shown(item).IntersectsWith(box)))
            {
                entries[i] = entry with { Paint = blended with { Backdrop = page } };
                continue;
            }
            if (backdrops >= MaxBackdrops || paints >= MaxBackdropPaints)
                continue;
            var below = Below(drawing, entry.Order, box, pageBox, MaxBackdropPaints - paints);
            if (below is null)
                continue;
            backdrops++;
            paints += below.Count;
            entries[i] = entry with { Paint = blended with { Backdrop = page, Below = below } };
        }
    }

    /// <summary>
    /// The paints and glyphs painted before <paramref name="order"/> that reach into <paramref name="box"/>, in paint
    /// order; null when there are more than <paramref name="room"/>.
    /// </summary>
    private static List<PdfPaint>? Below(PdfSlideDrawing drawing, int order, SKRect box, SKRect page, int room)
    {
        var below = new List<PdfPaint>();
        foreach (var paint in drawing.Paints)
        {
            if (paint.Order >= order)
                break;
            if (Reach(paint, page).IntersectsWith(box))
                below.Add(paint);
        }
        foreach (var letter in drawing.Letters)
        {
            if (letter.Order >= order)
                break;
            var area = letter.Area;
            if (letter.Clip is { } clip)
                area.Intersect(clip.Bounds);
            if (area.IntersectsWith(box))
                below.Add(letter);
        }
        if (below.Count > room)
            return null;
        below.Sort((a, b) => a.Order.CompareTo(b.Order));
        return below;
    }

    /// <summary>The box a paint can paint in on the slide.</summary>
    private static SKRect Reach(PdfPaint paint, SKRect page)
    {
        var area = paint switch
        {
            PdfPaintedPath path => SKRect.Inflate(path.Outline.Bounds, path.Stroke is null ? 0 : path.LineWidth, path.Stroke is null ? 0 : path.LineWidth),
            PdfPaintedImage image => image.Matrix.MapRect(SKRect.Create(0, 0, 1, 1)),
            PdfPaintedShading shading => ShadingBox(shading) ?? PictureBox(shading.Fill) ?? page,
            PdfPaintedGlyph glyph => glyph.Area,
            _ => page,
        };
        if (paint.Clip is { } clip)
            area.Intersect(clip.Bounds);
        return area;
    }

    // ───────────────────────── Text ─────────────────────────

    /// <summary>
    /// The paragraphs of a page's text for slides: where a line has words far apart (a row's parts joined with a tab,
    /// not the one after a list number; a gap much wider than a space where the line's other gaps are not), its parts
    /// become lines of their own and the paragraph's lines are stacked into paragraphs again. A line of a justified paragraph
    /// (one that reaches the paragraph's right edge and is not its last) keeps its spaces however wide.
    /// </summary>
    private static List<Item> SlideParagraphs(List<Item> items)
    {
        if (items.Count == 0)
            return items;
        var gaps = new List<double>();
        foreach (var line in items.SelectMany(i => i.Lines))
        {
            for (var w = 1; w < line.Words.Count; w++)
            {
                if (line.Words[w].Left - line.Words[w - 1].Right is > 0 and var gap)
                    gaps.Add(gap);
            }
        }
        var space = gaps.Count > 0 ? Math.Max(0.5, Median(gaps)) : 3;

        var result = new List<Item>();
        foreach (var item in items)
        {
            var lines = new List<TextLine>();
            var changed = false;
            for (var l = 0; l < item.Lines.Count; l++)
            {
                var line = item.Lines[l];
                var justified = l < item.Lines.Count - 1 && Math.Abs(line.Right - item.Right) < 0.5 * line.Size && Math.Abs(line.Left - item.Left) < 0.5 * line.Size;
                var parts = Parts(line, space, justified);
                changed |= parts.Count > 1;
                lines.AddRange(parts);
            }
            // The parts stack into paragraphs among themselves only: a label split off its value doesn't join the
            // label of the next row.
            if (changed)
                result.AddRange(Paragraphs([.. lines.OrderBy(l => l.Row).ThenBy(l => l.Left)]));
            else
                result.Add(item);
        }
        return result;
    }

    /// <summary>A line split where its words are far apart (see <see cref="SlideParagraphs"/>).</summary>
    private static List<TextLine> Parts(TextLine line, double space, bool justified)
    {
        var words = line.Words;
        var cuts = new SortedSet<int>();
        // "1.  Text": the number and its text stay one paragraph with a hanging indent.
        var number = line.Tabs.Contains(1) && ListStart.IsMatch(words[0].Text);
        foreach (var tab in line.Tabs)
        {
            if (!(tab == 1 && number))
                cuts.Add(tab);
        }
        if (!justified && words.Count > 1)
        {
            var gaps = new double[words.Count - 1];
            for (var i = 1; i < words.Count; i++)
                gaps[i - 1] = words[i].Left - words[i - 1].Right;
            var typical = gaps.Length >= 3 ? Median(gaps.Where(g => g > 0)) : 0;
            for (var i = 1; i < words.Count; i++)
            {
                var size = Math.Min(words[i].Size, words[i - 1].Size);
                var gap = gaps[i - 1];
                if (!(i == 1 && number) && gap > Math.Max(1.2 * size, 3 * space) && (typical <= 0 || gap > 2.5 * typical))
                    cuts.Add(i);
            }
        }
        if (cuts.Count == 0)
            return [line];
        var parts = new List<TextLine>();
        var start = 0;
        foreach (var cut in cuts.Append(words.Count))
        {
            if (cut <= start)
                continue;
            var part = new TextLine(words.GetRange(start, cut - start), line.Row);
            // Tabs of the joined row that stay within the part (the one after a list number).
            part.Tabs.AddRange(line.Tabs.Where(t => t > start && t < cut).Select(t => t - start));
            parts.Add(part);
            start = cut;
        }
        return parts;
    }

    /// <summary>
    /// The paragraph of <paramref name="item"/> in a box of its own: the box starts at its leftmost line and its first
    /// baseline is where the PDF has it; indents and tabs are measured from the box, not the column.
    /// </summary>
    private static PdfSlideText TextBox(Item item, double pageHeight, double lineRatio)
    {
        // Alignment in the paragraph's column; line breaks only where a line ended early within the paragraph's own
        // width (a narrow text box beside a picture wraps by itself, not with a break after every line).
        var paragraph = TextParagraph(item, null, NoHeadings, lineRatio);
        var rightToLeft = item.RightToLeft;
        // Indents and tab stops were measured from the column's start side; the box starts at the item's.
        var shift = Hwp(rightToLeft ? item.ContainerRight - item.Right : item.Left - item.ContainerLeft);
        (item.ContainerLeft, item.ContainerRight) = (item.Left, item.Right);
        var own = TextParagraph(item, null, NoHeadings, lineRatio);
        paragraph.Inlines.Clear();
        paragraph.Inlines.AddRange(own.Inlines);
        var format = paragraph.Format;
        paragraph.Format = format with
        {
            Left = format.Left is { } start && start - shift > 0 ? start - shift : null,
            Tabs = format.Tabs is { Count: > 0 } tabs ? [.. tabs.Select(t => t with { Position = Math.Max(0, t.Position - shift) })] : null,
            Before = 0,
            // One line: nothing to space, and a taller line would only move the text down.
            LineSpacing = item.Lines.Count > 1 ? format.LineSpacing : null,
        };

        var wrap = item.Lines.Count > 1;
        var first = item.Lines[0];
        var last = item.Lines[^1];
        var top = pageHeight - (first.Baseline + SlideAscent * first.Size);
        var bottom = pageHeight - (last.Baseline - SlideDescent * last.Size);
        double left = item.Left, right = item.Right;
        if (wrap)
        {
            // Room for a font a little wider than the PDF's, on the side the text grows to.
            var room = Math.Max(2, (right - left) * 0.02);
            switch (paragraph.Format.Align)
            {
                case HAlign.Center:
                    (left, right) = (left - room / 2, right + room / 2);
                    break;
                case HAlign.Right when !rightToLeft:
                case HAlign.Left or HAlign.Justify or null when rightToLeft:
                    left -= room;
                    break;
                default:
                    right += room;
                    break;
            }
        }
        return new PdfSlideText(new SKRect((float)left, (float)top, (float)right, (float)Math.Max(bottom, top + 1)), paragraph, wrap);
    }

    /// <summary>
    /// Text that runs at an angle (sideways, upside down, slanted): the letters of each direction turned upright, read
    /// into paragraphs like the page's other text, and put back as text boxes turned by their angle.
    /// </summary>
    private List<Entry> TurnedTexts(Page page, PdfSlideDrawing drawing, double height, double lineRatio, DocumentBudget budget,
        CancellationToken cancellationToken)
    {
        var entries = new List<Entry>();
        var turned = page.Letters
            .Where(l => l.TextOrientation != TextOrientation.Horizontal && !string.IsNullOrWhiteSpace(l.Value)
                        && (l.RenderingMode != TextRenderingMode.Neither || _ocrLayer))
            .Select(l => (Letter: l, Angle: Angle(l)))
            .Where(l => l.Angle is not null)
            .GroupBy(l => l.Angle!.Value);
        foreach (var direction in turned)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var radians = direction.Key * Math.PI / 180;
            var (cos, sin) = (Math.Cos(radians), Math.Sin(radians));
            // Into the text's own frame (turned back by the angle) and out of it again.
            PdfPoint Upright(PdfPoint p) => new(p.X * cos + p.Y * sin, -p.X * sin + p.Y * cos);
            PdfPoint Page(double x, double y) => new(x * cos - y * sin, x * sin + y * cos);

            var letters = direction.Select(d => Turned(d.Letter, Upright)).ToList();
            var words = new List<TextWord>();
            foreach (var word in NearestNeighbourWordExtractor.Instance.GetWords(letters))
            {
                if (word.TextOrientation != TextOrientation.Horizontal || string.IsNullOrWhiteSpace(word.Text))
                    continue;
                var visible = word.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
                if (visible.Count == 0)
                    continue;
                var size = Median(visible.Select(LetterSize));
                var box = word.BoundingBox;
                words.Add(new TextWord(CleanText(word.Text), box.Left, box.Right, box.Top, box.Bottom, visible[0].StartBaseLine.Y, Math.Max(1, size),
                    Format(visible[0], size), null));
            }
            if (words.Count == 0)
                continue;
            var glyphs = new GlyphIndex([.. drawing.Glyphs.Where(g => g.Turned).Select(g =>
            {
                var p = Upright(new PdfPoint(g.X, g.Y));
                return g with { X = p.X, Y = p.Y };
            })]);
            foreach (var item in ReadingOrder(SlideParagraphs(TextItems(words))))
            {
                budget.Object();
                budget.Paragraph();
                // The box in the turned frame (y down from 0 there), its centre back on the page.
                var box = TextBox(item, 0, lineRatio);
                var centre = Page(box.Bounds.MidX, -box.Bounds.MidY);
                var (x, y) = ((float)centre.X, (float)(height - centre.Y));
                var bounds = new SKRect(x - box.Bounds.Width / 2, y - box.Bounds.Height / 2, x + box.Bounds.Width / 2, y + box.Bounds.Height / 2);
                var rotation = (float)((360 - direction.Key) % 360);
                entries.Add(new Entry(glyphs.Order(item) ?? int.MaxValue, box with { Bounds = bounds, Rotation = rotation }, null, default));
            }
        }
        return entries;
    }

    /// <summary>The direction a letter's baseline runs in, whole degrees anticlockwise (PDF y up); null for a letter without a width.</summary>
    private static int? Angle(Letter letter)
    {
        var (dx, dy) = (letter.EndBaseLine.X - letter.StartBaseLine.X, letter.EndBaseLine.Y - letter.StartBaseLine.Y);
        if (Math.Abs(dx) + Math.Abs(dy) < 1e-3 || !double.IsFinite(dx) || !double.IsFinite(dy))
            return null;
        var degrees = (int)Math.Round(Math.Atan2(dy, dx) * 180 / Math.PI);
        degrees = (degrees % 360 + 360) % 360;
        return degrees == 0 ? null : degrees;
    }

    /// <summary>A letter moved by <paramref name="turn"/> (its boxes as the upright boxes of their turned corners).</summary>
    private static Letter Turned(Letter letter, Func<PdfPoint, PdfPoint> turn)
    {
        PdfRectangle Upright(PdfRectangle box)
        {
            var corners = new[] { turn(box.BottomLeft), turn(box.BottomRight), turn(box.TopLeft), turn(box.TopRight) };
            return new PdfRectangle(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Max(c => c.X), corners.Max(c => c.Y));
        }

        return new Letter(letter.Value, Upright(letter.BoundingBox), Upright(letter.GlyphRectangleLoose), turn(letter.StartBaseLine), turn(letter.EndBaseLine),
            letter.Width, letter.FontSize, letter.FontDetails, letter.RenderingMode, letter.StrokeColor, letter.FillColor, letter.PointSize, letter.TextSequence);
    }

    /// <summary>Where glyphs start (PdfSlideDrawing), sorted by height, to tell when the words of a paragraph were drawn.</summary>
    private sealed class GlyphIndex(List<PdfGlyphOrder> glyphs)
    {
        private readonly PdfGlyphOrder[] _sorted = [.. glyphs.OrderBy(g => g.Y)];

        /// <summary>When the first glyph of <paramref name="word"/> was drawn; null when none is found there.</summary>
        public int? Order(TextWord word)
        {
            var tolerance = 0.3 * word.Size + 0.5;
            var (low, high) = (word.Baseline - tolerance, word.Baseline + tolerance);
            int from = 0, to = _sorted.Length;
            while (from < to)
            {
                var middle = (from + to) / 2;
                if (_sorted[middle].Y < low)
                    from = middle + 1;
                else
                    to = middle;
            }
            int? best = null;
            for (var i = from; i < _sorted.Length && _sorted[i].Y <= high; i++)
            {
                var glyph = _sorted[i];
                if (glyph.X >= word.Left - 0.5 * word.Size && glyph.X <= word.Right && (best is null || glyph.Order < best))
                    best = glyph.Order;
            }
            return best;
        }

        public int? Order(Item item) => item.Lines.SelectMany(l => l.Words).Select(Order).Min();
    }

    // ───────────────────────── Tables ─────────────────────────

    private static PdfSlideTable SlideTable(Item item, double pageHeight, double lineRatio, DocumentBudget budget)
    {
        var grid = item.Table!;
        // A slide's table stands where the PDF has it: no column to align it in.
        (item.ContainerLeft, item.ContainerRight) = (grid.Bounds.Left, grid.Bounds.Right);
        var table = TableBlock(item, lineRatio);
        foreach (var cell in table.Rows.SelectMany(r => r.Cells))
        {
            // Pictures of a cell are picture shapes over the table (SlidePage), not part of its text.
            foreach (var paragraph in cell.Blocks.OfType<HParagraph>())
            {
                paragraph.Inlines.RemoveAll(i => i is HImage);
                budget.Paragraph();
            }
        }
        var columns = grid.Columns.Zip(grid.Columns.Skip(1), (a, b) => (float)(b - a)).ToArray();
        var rows = grid.Rows.Zip(grid.Rows.Skip(1), (a, b) => (float)(a - b)).ToArray();
        return new PdfSlideTable(Box(grid.Bounds, pageHeight), table, columns, rows);
    }

    // ───────────────────────── Pictures ─────────────────────────

    /// <summary>
    /// The page's pictures where the drawing paints them: <paramref name="pictures"/> (the files ReadPage wrote) found
    /// by their box, placed with the matrix they are drawn with (turned, mirrored) and cut by their clipping path;
    /// stencil masks as pictures of their colour. Pictures the drawing doesn't show go behind everything at their box.
    /// </summary>
    private List<Entry> SlidePictures(List<Item> pictures, PdfSlideDrawing drawing, double width, double height, DocumentBudget budget)
    {
        var entries = new List<Entry>();
        var used = new bool[pictures.Count];
        var page = new SKRect(0, 0, (float)width, (float)height);
        foreach (var image in drawing.Paints.OfType<PdfPaintedImage>())
        {
            var shown = image.Matrix.MapRect(SKRect.Create(0, 0, 1, 1));
            if (image.Image is { IsImageMask: true })
            {
                if (shown.Width < 1 || shown.Height < 1 || !shown.IntersectsWith(page) || _pictures >= 200)
                    continue;
                if (Stencil(image) is { } stencil && Placed(Masked(stencil, image), image, true) is { } placed)
                {
                    budget.Object();
                    entries.Add(new Entry(image.Order, placed, image, default));
                }
                continue;
            }
            // ReadPage's box of the picture is PdfPig's (PDF coordinates, y up, within the page): the upright box of the
            // unit square under the matrix for most pictures, but for some turned ones the box of two corners or of
            // PdfPig's turned rectangle. Any of them finds the picture.
            var m = image.Matrix;
            var pdf = TransformationMatrix.FromValues(m.ScaleX, -m.SkewY, m.SkewX, -m.ScaleY, m.TransX, height - m.TransY);
            var turned = pdf.Transform(new PdfRectangle(0, 0, 1, 1));
            var (origin, corner) = (pdf.Transform(new PdfPoint(0, 0)), pdf.Transform(new PdfPoint(1, 1)));
            SKRect OnPage(double left, double bottom, double right, double top) =>
                new((float)Math.Max(0, left), (float)Math.Max(0, bottom), (float)Math.Min(width, right), (float)Math.Min(height, top));
            SKRect[] expected =
            [
                OnPage(shown.Left, height - shown.Bottom, shown.Right, height - shown.Top),
                OnPage(Math.Min(origin.X, corner.X), Math.Min(origin.Y, corner.Y), Math.Max(origin.X, corner.X), Math.Max(origin.Y, corner.Y)),
                OnPage(turned.Left, turned.Bottom, turned.Right, turned.Top),
            ];
            var match = -1;
            for (var i = 0; i < pictures.Count && match < 0; i++)
            {
                var bounds = pictures[i].PictureBounds!.Value;
                var found = new SKRect((float)bounds.Left, (float)bounds.Bottom, (float)bounds.Right, (float)bounds.Top);
                if (!used[i] && expected.Any(e => Close(found, e)))
                    match = i;
            }
            if (match < 0)
                continue;
            used[match] = true;
            var path = pictures[match].PicturePath!;
            var natural = image.Image is { } source && PictureSize(path) is { } size && size == (source.WidthInSamples, source.HeightInSamples);
            // A picture cut out of the rendered page is the part of the page it shows, upright.
            var picture = natural ? Placed(Masked(path, image), image, false) : new PdfSlidePicture(Box(pictures[match].PictureBounds!.Value, height), path);
            if (picture is not null)
            {
                budget.Object();
                entries.Add(new Entry(image.Order, picture, image, default));
            }
        }
        for (var i = 0; i < pictures.Count; i++)
        {
            if (used[i])
                continue;
            budget.Object();
            entries.Add(new Entry(-1, new PdfSlidePicture(Box(pictures[i].PictureBounds!.Value, height), pictures[i].PicturePath!), null, default));
        }
        return entries;
    }

    /// <summary>
    /// The picture <paramref name="path"/> with the soft mask it is painted under in its alpha (a PNG of its own), when
    /// that mask is a gradient or a drawn group; the path itself otherwise or when it can't be read.
    /// </summary>
    private string Masked(string path, PdfPaintedImage image)
    {
        if (image.Mask is not { IsConstant: false } mask)
            return path;
        using var source = SKBitmap.Decode(path);
        if (source is null || (long)source.Width * source.Height > MaxGraphicsPixels)
            return path;
        // The picture's pixels (y down) → its unit square (y up) → the slide; and back for the mask.
        var toSlide = new SKMatrix(1f / source.Width, 0, 0, 0, -1f / source.Height, 1, 0, 0, 1).PostConcat(image.Matrix);
        if (!toSlide.TryInvert(out var toPixels))
            return path;
        using var bitmap = new SKBitmap(new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(source, 0, 0);
            canvas.SetMatrix(toPixels);
            PdfSlideRaster.ApplyMask(canvas, mask);
        }
        var file = NewMediaPath("png");
        using (var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100))
        using (var output = File.Create(file))
            encoded.SaveTo(output);
        return file;
    }

    private static bool Close(SKRect a, SKRect b) =>
        Math.Abs(a.Left - b.Left) < 1 && Math.Abs(a.Top - b.Top) < 1 && Math.Abs(a.Right - b.Right) < 1 && Math.Abs(a.Bottom - b.Bottom) < 1;

    private static (int Width, int Height)? PictureSize(string path)
    {
        using var codec = SKCodec.Create(path);
        return codec is null ? null : (codec.Info.Width, codec.Info.Height);
    }

    /// <summary>
    /// The picture <paramref name="path"/> drawn by <paramref name="image"/>'s matrix: its size, turn and mirroring, and
    /// the clipping path as a crop (a rectangle across an upright picture) or as its outline; null when the clip hides it.
    /// </summary>
    private static PdfSlidePicture? Placed(string path, PdfPaintedImage image, bool stencil)
    {
        var m = image.Matrix;
        var topLeft = m.MapPoint(0, 1);
        var across = m.MapPoint(1, 1) - topLeft;
        var down = m.MapPoint(0, 0) - topLeft;
        var (width, height) = (across.Length, down.Length);
        if (width < 0.5f || height < 0.5f || !float.IsFinite(width) || !float.IsFinite(height))
            return null;
        var rotation = (float)(Math.Atan2(across.Y, across.X) * 180 / Math.PI);
        var mirrored = across.X * down.Y - across.Y * down.X < 0;
        var (flipH, flipV) = (false, mirrored);
        if (mirrored && Math.Abs(Math.Abs(rotation) - 180) < 0.5)
            (rotation, flipH, flipV) = (0, true, false);
        rotation = (rotation % 360 + 360) % 360;
        if (rotation < 0.05 || rotation > 359.95)
            rotation = 0;
        var centre = m.MapPoint(0.5f, 0.5f);
        var bounds = new SKRect(centre.X - width / 2, centre.Y - height / 2, centre.X + width / 2, centre.Y + height / 2);
        var picture = new PdfSlidePicture(bounds, path) { Rotation = rotation, FlipHorizontal = flipH, FlipVertical = flipV };
        // A stencil's colour carries its alpha already; a mask other than a constant alpha is in the picture (Masked).
        var alpha = (stencil ? 1 : image.Alpha) * (image.Mask is { IsConstant: true } mask ? mask.Alpha : 1);
        if (alpha < 1)
            picture = picture with { Alpha = alpha };

        using var shown = new SKPath();
        shown.AddPoly([m.MapPoint(0, 0), m.MapPoint(1, 0), m.MapPoint(1, 1), m.MapPoint(0, 1)]);
        if (image.Clip is not { } clip || !Cuts(clip, shown))
            return picture;
        if (rotation == 0 && clip.Rectangular)
        {
            var visible = SKRect.Intersect(clip.Bounds, bounds);
            if (visible.Width < 0.5f || visible.Height < 0.5f)
                return null;
            var (left, top, right, bottom) = ((visible.Left - bounds.Left) / width, (visible.Top - bounds.Top) / height,
                (bounds.Right - visible.Right) / width, (bounds.Bottom - visible.Bottom) / height);
            // The crop is of the picture itself, before it is mirrored.
            if (flipH)
                (left, right) = (right, left);
            if (flipV)
                (top, bottom) = (bottom, top);
            return picture with { Bounds = visible, Crop = new SKRect(left, top, right, bottom) };
        }
        // Other clips: the visible part of the box as the picture's outline, in the box's own (unturned) coordinates.
        var toBox = ToBox(bounds, rotation, flipH, flipV);
        using var outline = new SKPath(clip.Path);
        outline.Transform(toBox);
        using var box = new SKPath();
        box.AddRect(SKRect.Create(0, 0, width, height));
        using var part = outline.Op(box, SKPathOp.Intersect);
        if (part is null || part.IsEmpty || part.Bounds.Width < 0.5f || part.Bounds.Height < 0.5f)
            return null;
        return PdfGeometry.From(part, SKMatrix.Identity) is { } geometry ? picture with { Geometry = geometry } : picture;
    }

    /// <summary>From slide coordinates into those of a box turned and flipped as a shape's xfrm says.</summary>
    private static SKMatrix ToBox(SKRect bounds, float rotation, bool flipH, bool flipV) =>
        SKMatrix.CreateTranslation(-bounds.MidX, -bounds.MidY)
            .PostConcat(SKMatrix.CreateRotationDegrees(-rotation))
            .PostConcat(SKMatrix.CreateScale(flipH ? -1 : 1, flipV ? -1 : 1))
            .PostConcat(SKMatrix.CreateTranslation(bounds.Width / 2, bounds.Height / 2));

    /// <summary>
    /// Whether <paramref name="clip"/> hides part of <paramref name="outline"/> (grown by <paramref name="grow"/> for
    /// a stroke): it doesn't hold its box, or (a clipping path) some of the outline lies outside it. A stroke inside the
    /// box of a clipping path other than a rectangle is taken as cut.
    /// </summary>
    private static bool Cuts(PdfClip clip, SKPath outline, float grow = 0, bool stroked = false)
    {
        var box = outline.TightBounds;
        if (!Contains(clip.Bounds, SKRect.Inflate(box, grow, grow)))
            return true;
        if (clip.Rectangular)
            return false;
        if (stroked)
            return true;
        // A long outline: taken as cut (its picture is drawn through the clip, which is right either way).
        if (outline.PointCount > PdfGeometry.MaxCommands)
            return true;
        using var outside = outline.Op(clip.Path, SKPathOp.Difference);
        return outside is not null && !outside.IsEmpty && outside.TightBounds is { Width: > 0.25f, Height: > 0.25f };
    }

    private static bool Contains(SKRect outer, SKRect inner) =>
        inner.Left >= outer.Left - 0.5f && inner.Top >= outer.Top - 0.5f && inner.Right <= outer.Right + 0.5f && inner.Bottom <= outer.Bottom + 0.5f;

    /// <summary>A stencil mask as a PNG of its colour, transparent where it paints nothing; null when it can't be read.</summary>
    private string? Stencil(PdfPaintedImage image)
    {
        if (image.Image is not { } mask)
            return null;
        using var bitmap = PdfSlideRaster.Stencil(mask, image.MaskColor);
        if (bitmap is null)
            return null;
        var file = NewMediaPath("png");
        using (var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100))
        using (var output = File.Create(file))
            encoded.SaveTo(output);
        return file;
    }

    // ───────────────────────── Graphics ─────────────────────────

    /// <summary>
    /// The page's vector graphics in paint order: rectangles and straight lines as shapes, gradient fills as shapes
    /// with a gradient, the rest as paints to draw, and the colour of a first rectangle that fills the page.
    /// Straight-edged paths inside a ruled table are its rules and shading, which the table has itself: the first of
    /// them gives the table its place in the order.
    /// </summary>
    private (string? Background, List<Entry> Entries, int?[] TableOrders) SlideGraphics(PdfSlideDrawing drawing, double width, double height,
        List<SKRect> tables, DocumentBudget budget)
    {
        string? background = null;
        var entries = new List<Entry>();
        var tableOrders = new int?[tables.Count];
        var page = new SKRect(0, 0, (float)width, (float)height);
        var first = true;
        foreach (var paint in drawing.Paints)
        {
            switch (paint)
            {
                case PdfPaintedPath path:
                    {
                        if (path.Fill is null && path.Stroke is null)
                            continue; // pattern fills that can't be drawn
                        var bounds = path.Outline.TightBounds;
                        var half = path.Stroke is null ? 0 : Math.Max(0.5f, path.LineWidth / 2);
                        var area = SKRect.Inflate(bounds, half, half);
                        if (!path.Curved && tables.FindIndex(t => Within(bounds, t, 3 + path.LineWidth)) is >= 0 and var table)
                        {
                            tableOrders[table] ??= path.Order;
                            continue;
                        }
                        if (path.Clip is { } clip)
                            area.Intersect(clip.Bounds);
                        if (area.Width <= 0 || area.Height <= 0 || !area.IntersectsWith(page))
                            continue; // clipped away, or off the page
                        var cut = path.Clip is { } c && Cuts(c, path.Outline, half, path.Stroke is not null);
                        if (first)
                        {
                            first = false;
                            // Outlined in its own colour at most (PdfPig's writer and some drivers fill and stroke a background).
                            if (!cut && path.Mask is null && path.Blend == SKBlendMode.SrcOver && path.Fill is { IsSolid: true, Color.Alpha: 255 } fill && path.Outline.IsRect
                                && (path.Stroke is null || path.Stroke.Color == fill.Color)
                                && bounds.Width >= 0.98 * width && bounds.Height >= 0.98 * height)
                            {
                                background = Hex(fill.Color);
                                continue;
                            }
                        }
                        var shapes = PathShapes(path, cut ? path.Clip : null, TileFile);
                        if (shapes is null)
                        {
                            entries.Add(new Entry(path.Order, null, path, area));
                            continue;
                        }
                        foreach (var shape in shapes)
                        {
                            budget.Object();
                            entries.Add(new Entry(path.Order, shape, path, area));
                        }
                        break;
                    }
                case PdfPaintedShading shading:
                    {
                        var area = shading.Clip?.Bounds ?? ShadingBox(shading) ?? PictureBox(shading.Fill) ?? page;
                        area.Intersect(page);
                        if (area.Width <= 0 || area.Height <= 0)
                            continue;
                        first = false;
                        if (ShadingShape(shading, page) is { } shape)
                        {
                            budget.Object();
                            entries.Add(new Entry(shading.Order, shape, shading, area));
                        }
                        else
                        {
                            entries.Add(new Entry(shading.Order, null, shading, area));
                        }
                        break;
                    }
            }
        }
        return (background, entries, tableOrders);
    }

    private static string Hex(SKColor color) => $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";

    /// <summary>Tile pictures of this page already written (a pattern used by several fills is one file).</summary>
    private readonly Dictionary<SKImage, string> _tileFiles = new(ReferenceEqualityComparer.Instance);

    /// <summary>A tiling pattern cell's picture as a PNG of 96 dpi (its size at tile scale 100 % is its pixels at 96 dpi).</summary>
    private string TileFile(PdfTile tile)
    {
        if (_tileFiles.TryGetValue(tile.Image, out var known))
            return known;
        var file = NewMediaPath("png");
        using (var data = tile.Image.Encode(SKEncodedImageFormat.Png, 100))
            File.WriteAllBytes(file, WithResolution(data.ToArray(), 96));
        return _tileFiles[tile.Image] = file;
    }

    /// <summary>A PNG with a pHYs chunk of <paramref name="dpi"/> after its header, so readers take its size from it.</summary>
    internal static byte[] WithResolution(byte[] png, double dpi)
    {
        const int Header = 8 + 25; // signature, IHDR
        if (png.Length < Header || png[12] != 'I' || png[13] != 'H' || png[14] != 'D' || png[15] != 'R')
            return png;
        var perMetre = (uint)Math.Round(dpi / 0.0254);
        var chunk = new byte[4 + 4 + 9 + 4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(chunk, 9);
        "pHYs"u8.CopyTo(chunk.AsSpan(4));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8), perMetre);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(12), perMetre);
        chunk[16] = 1; // metres
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(17), System.IO.Hashing.Crc32.HashToUInt32(chunk.AsSpan(4, 13)));
        return [.. png.AsSpan(0, Header), .. chunk, .. png.AsSpan(Header)];
    }

    private static bool Within(SKRect box, SKRect area, double tolerance) =>
        box.Left >= area.Left - tolerance && box.Right <= area.Right + tolerance && box.Top >= area.Top - tolerance && box.Bottom <= area.Bottom + tolerance;

    /// <summary>What a function-based or mesh shading's picture covers on the slide.</summary>
    private static SKRect? PictureBox(PdfShadingFill fill) =>
        fill.Picture is { } picture ? picture.Space.MapRect(SKRect.Create(picture.Image.Width, picture.Image.Height)) : null;

    /// <summary>The box of a shading painted without a clipping path (its own BBox), on the slide.</summary>
    private static SKRect? ShadingBox(PdfPaintedShading shading)
    {
        if (shading.Fill.Shading.BBox is not { } box)
            return null;
        return shading.Fill.Space.MapRect(new SKRect((float)box.Left, (float)box.Bottom, (float)box.Right, (float)box.Top));
    }

    /// <summary>
    /// The shapes a path becomes, or null when it is drawn: rectangles and straight lines in solid colours (cut by a
    /// clipping rectangle when <paramref name="clip"/> is given), or the outline (intersected with the clipping path) of
    /// a fill with a shading that is a gradient, of a solid fill under a shading soft mask, or of a fill with a tiling
    /// pattern repeating upright (its cell's picture from <paramref name="tileFile"/>). Blend modes other than Multiply
    /// and Darken, and masks Filee drew, are drawn.
    /// </summary>
    private static List<PdfSlideShape>? PathShapes(PdfPaintedPath path, PdfClip? clip, Func<PdfTile, string> tileFile)
    {
        var (fill, stroke) = (path.Fill, path.Stroke);
        if (stroke is { IsSolid: false } || path.Blend is not (SKBlendMode.SrcOver or SKBlendMode.Multiply or SKBlendMode.Darken) || path.Mask?.Picture is not null)
            return null;
        var maskAlpha = path.Mask is { IsConstant: true } mask ? mask.Alpha : 1;
        if (fill is null or { IsSolid: true } && path.Mask?.Shading is null && !path.Curved)
        {
            if (clip is { Rectangular: false })
                return null;
            return SimpleShapes(path, clip?.Bounds, maskAlpha);
        }
        // A gradient: of the fill's shading, or the solid colour faded by the mask's shading.
        if (fill is null || clip is not null && stroke is not null)
            return null;
        using var outline = clip is null ? new SKPath(path.Outline) : path.Outline.Op(clip.Path, SKPathOp.Intersect);
        if (outline is null || outline.IsEmpty)
            return null;
        var bounds = outline.TightBounds;
        PdfSlideGradient? gradient = null;
        PdfSlideTile? tiled = null;
        if (fill.Shading is { } shading && path.Mask?.Shading is null)
        {
            var alpha = fill.Alpha * maskAlpha;
            gradient = Gradient(shading, bounds, c => c.WithAlpha((byte)Math.Round(alpha * 255)));
        }
        else if (fill.IsSolid && path.Mask?.Shading is { } fade)
        {
            var color = fill.Color;
            gradient = Gradient(fade, bounds, c => color.WithAlpha((byte)Math.Round(color.Alpha * PdfShadings.Luminosity(c) * path.Mask.Alpha)));
        }
        else if (fill.Tile is { } tile && path.Mask?.Shading is null)
        {
            tiled = Tiled(tile, bounds, fill.Alpha * maskAlpha, tileFile);
        }
        if (gradient is null && tiled is null)
            return null;
        var line = stroke is null ? null : Hex(stroke.Color);
        var lineAlpha = stroke is null ? 1 : stroke.Alpha * maskAlpha;
        var dashed = path.Dash is not null;
        if (outline.IsRect)
            return [new PdfSlideShape(bounds, PdfShapeKind.Rectangle, null, line, path.LineWidth, dashed) { Gradient = gradient, Tile = tiled, LineAlpha = lineAlpha }];
        if (PdfGeometry.From(outline, SKMatrix.CreateTranslation(-bounds.Left, -bounds.Top)) is not { } geometry)
            return null;
        return [new PdfSlideShape(bounds, PdfShapeKind.Custom, null, line, path.LineWidth, dashed)
            { Gradient = gradient, Tile = tiled, Geometry = geometry, LineAlpha = lineAlpha }];
    }

    /// <summary>
    /// A tiling pattern's fill of <paramref name="box"/> as a picture fill, when its cells repeat upright (scaled, not
    /// turned, skewed or mirrored); null otherwise (it is drawn).
    /// </summary>
    private static PdfSlideTile? Tiled(PdfTile tile, SKRect box, double alpha, Func<PdfTile, string> tileFile)
    {
        var m = tile.Space;
        if (m.ScaleX <= 0 || m.ScaleY <= 0 || Math.Abs(m.SkewX) > 1e-4 * m.ScaleY || Math.Abs(m.SkewY) > 1e-4 * m.ScaleX
            || !PdfShadings.Finite(m.ScaleX, m.ScaleY, m.TransX, m.TransY))
            return null;
        var (width, height) = (tile.Image.Width * m.ScaleX, tile.Image.Height * m.ScaleY);
        if (width < 0.05f || height < 0.05f)
            return null;
        // Where a tile starts, from the box's top-left corner, within one tile.
        static float Within(float value, float size) => (value % size + size) % size;
        var offset = new SKPoint(Within(m.TransX - box.Left, width), Within(m.TransY - box.Top, height));
        // A tile at 100 % is the picture's pixels at 96 dpi: 0.75 points a pixel.
        return new PdfSlideTile(tileFile(tile), offset, m.ScaleX / 0.75f, m.ScaleY / 0.75f, alpha);
    }

    /// <summary>A shading painted over its clipping path (or its box, or the page) as a shape with a gradient; null when it is drawn.</summary>
    private static PdfSlideShape? ShadingShape(PdfPaintedShading shading, SKRect page)
    {
        if (shading.Mask is { IsConstant: false } || shading.Blend is not (SKBlendMode.SrcOver or SKBlendMode.Multiply or SKBlendMode.Darken))
            return null;
        using var area = new SKPath();
        if (shading.Clip is { } clip)
            area.AddPath(clip.Path);
        else
            area.AddRect(ShadingBox(shading) ?? PictureBox(shading.Fill) ?? page);
        using var pageArea = new SKPath();
        pageArea.AddRect(page);
        using var visible = area.Op(pageArea, SKPathOp.Intersect);
        if (visible is null || visible.IsEmpty)
            return null;
        var bounds = visible.TightBounds;
        var alpha = shading.Alpha * (shading.Mask?.Alpha ?? 1);
        if (Gradient(shading.Fill, bounds, c => c.WithAlpha((byte)Math.Round(alpha * 255))) is not { } gradient)
            return null;
        if (visible.IsRect)
            return new PdfSlideShape(bounds, PdfShapeKind.Rectangle, null, null, 0, false) { Gradient = gradient };
        return PdfGeometry.From(visible, SKMatrix.CreateTranslation(-bounds.Left, -bounds.Top)) is { } geometry
            ? new PdfSlideShape(bounds, PdfShapeKind.Custom, null, null, 0, false) { Gradient = gradient, Geometry = geometry }
            : null;
    }

    /// <summary>
    /// The shapes a path of straight segments is made of when every subpath is an upright rectangle or (stroked) a
    /// single straight line, each cut by <paramref name="clip"/>; null for anything else (a polygon, a polyline).
    /// </summary>
    private static List<PdfSlideShape>? SimpleShapes(PdfPaintedPath path, SKRect? clip, double maskAlpha)
    {
        var fill = path.Fill is { } f ? Hex(f.Color) : null;
        var line = path.Stroke is { } s ? Hex(s.Color) : null;
        var fillAlpha = (path.Fill?.Alpha ?? 1) * maskAlpha;
        var lineAlpha = (path.Stroke?.Alpha ?? 1) * maskAlpha;
        var width = Math.Max(0, path.LineWidth);
        var dashed = path.Dash is not null;
        var shapes = new List<PdfSlideShape>();
        using var iterator = path.Outline.CreateRawIterator();
        var buffer = new SKPoint[4];
        var points = new List<SKPoint>();
        var closed = false;

        bool Flush()
        {
            if (points.Count > 1 && Same(points[0], points[^1]))
            {
                points.RemoveAt(points.Count - 1);
                closed = true;
            }
            if (points.Count < 2)
                return true; // a lone point paints nothing
            if (points.Count == 2 && (!closed || fill is null))
            {
                if (line is null)
                    return true; // a filled line has no area
                var (a, b) = (points[0], points[1]);
                if (clip is { } box && !ClipSegment(box, ref a, ref b))
                    return true;
                var bounds = new SKRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
                // Slide y runs down: a line rising to the right goes from the bottom-left corner (flipped vertically).
                var rising = (b.X - a.X) * (b.Y - a.Y) < 0;
                shapes.Add(new PdfSlideShape(bounds, PdfShapeKind.Line, null, line, width, dashed, FlipVertical: rising) { LineAlpha = lineAlpha });
                return true;
            }
            if (points.Count != 4 || !Upright(points))
                return false;
            // An open rectangle that is only stroked misses its last side: not a rectangle shape. A stroked one cut by a
            // clip would get the clip's edge as its outline: drawn.
            if (!closed && fill is null || clip is not null && line is not null)
                return false;
            var rectangle = new SKRect(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
            if (clip is { } area)
            {
                rectangle.Intersect(area);
                if (rectangle.Width <= 0 || rectangle.Height <= 0)
                    return true;
            }
            shapes.Add(new PdfSlideShape(rectangle, PdfShapeKind.Rectangle, fill, line, width, dashed) { FillAlpha = fillAlpha, LineAlpha = lineAlpha });
            return true;
        }

        SKPathVerb verb;
        while ((verb = iterator.Next(buffer)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move:
                    if (!Flush())
                        return null;
                    points.Clear();
                    closed = false;
                    points.Add(buffer[0]);
                    break;
                case SKPathVerb.Line:
                    if (points.Count == 0)
                        points.Add(buffer[0]);
                    points.Add(buffer[1]);
                    break;
                case SKPathVerb.Close:
                    closed = true;
                    break;
                default:
                    return null;
            }
        }
        return Flush() ? shapes : null;
    }

    /// <summary>Cuts the segment a → b to <paramref name="box"/> (Liang–Barsky); false when none of it is inside.</summary>
    private static bool ClipSegment(SKRect box, ref SKPoint a, ref SKPoint b)
    {
        double t0 = 0, t1 = 1;
        double dx = b.X - a.X, dy = b.Y - a.Y;
        foreach (var (p, q) in new[] { (-dx, a.X - box.Left), (dx, box.Right - a.X), (-dy, a.Y - box.Top), (dy, box.Bottom - a.Y) })
        {
            if (Math.Abs(p) < 1e-9)
            {
                if (q < -0.01)
                    return false;
                continue;
            }
            var r = q / p;
            if (p < 0)
                t0 = Math.Max(t0, r);
            else
                t1 = Math.Min(t1, r);
            if (t0 > t1)
                return false;
        }
        var start = a;
        a = new SKPoint((float)(start.X + t0 * dx), (float)(start.Y + t0 * dy));
        b = new SKPoint((float)(start.X + t1 * dx), (float)(start.Y + t1 * dy));
        return true;
    }

    private static bool Same(SKPoint a, SKPoint b) => Math.Abs(a.X - b.X) < 0.01 && Math.Abs(a.Y - b.Y) < 0.01;

    /// <summary>Four corners joined by horizontal and vertical sides.</summary>
    private static bool Upright(List<SKPoint> corners)
    {
        for (var i = 0; i < 4; i++)
        {
            var (a, b) = (corners[i], corners[(i + 1) % 4]);
            if (Math.Abs(a.X - b.X) > 0.01 && Math.Abs(a.Y - b.Y) > 0.01)
                return false;
        }
        return true;
    }

    /// <summary>
    /// A DrawingML gradient over <paramref name="box"/> that paints like <paramref name="fill"/> (an axial shading, or a
    /// radial one whose circles share about one centre, drawn without stretching), each colour through
    /// <paramref name="color"/>; null for other shadings and for ones it can't show (they are drawn).
    /// </summary>
    internal static PdfSlideGradient? Gradient(PdfShadingFill fill, SKRect box, Func<SKColor, SKColor> color)
    {
        if (box.Width < 0.01f || box.Height < 0.01f)
            return null;
        // A function-based or mesh shading: the gradient it looks like, where it covers the whole box.
        if (fill.Look is { } look)
            return look.Covers(box) ? LookGradient(look, box, color) : null;
        if (fill.Picture is not null)
            return null;
        var shading = fill.Shading;
        var space = fill.Space;
        var (extendStart, extendEnd) = PdfShadings.Extend(shading);
        var centre = new SKPoint(box.MidX, box.MidY);
        switch (shading)
        {
            case AxialShading { Coords: [var x0, var y0, var x1, var y1, ..] } when PdfShadings.Finite(x0, y0, x1, y1) && (x0 != x1 || y0 != y1):
                {
                    var start = space.MapPoint((float)x0, (float)y0);
                    var end = space.MapPoint((float)x1, (float)y1);
                    // The lines of one colour run across the axis in the shading's space; on the slide, where the matrix takes them.
                    var across = space.MapVector((float)-(y1 - y0), (float)(x1 - x0));
                    var normal = new SKPoint(across.Y, -across.X);
                    var length = normal.Length;
                    if (length < 1e-9f)
                        return null;
                    normal = new SKPoint(normal.X / length, normal.Y / length);
                    var distance = Dot(end - start, normal);
                    if (Math.Abs(distance) < 1e-6)
                        return null;
                    if (distance < 0)
                        (normal, distance) = (new SKPoint(-normal.X, -normal.Y), -distance);
                    var angle = Math.Atan2(normal.Y, normal.X);
                    // DrawingML's line through the box's centre, as long as the box is across that direction.
                    var half = (Math.Abs(box.Width * Math.Cos(angle)) + Math.Abs(box.Height * Math.Sin(angle))) / 2;
                    var offset = Dot(centre - start, normal);
                    var degrees = (angle * 180 / Math.PI % 360 + 360) % 360;
                    return new PdfSlideGradient(false, degrees, default,
                        Stops((offset - half) / distance, 2 * half / distance, t => color(PdfShadings.ColorAt(shading, t)), extendStart, extendEnd));
                }
            case RadialShading { Coords: [var x0, var y0, var r0, var x1, var y1, var r1, ..] } when PdfShadings.Finite(x0, y0, r0, x1, y1, r1) && r0 >= 0 && r1 >= 0:
                {
                    // Circles stay circles only under a matrix that turns and scales evenly.
                    var (u, v) = (space.MapVector(1, 0), space.MapVector(0, 1));
                    if (Math.Abs(u.Length - v.Length) > 0.02 * Math.Max(u.Length, v.Length) || Math.Abs(Dot(u, v)) > 0.02 * u.Length * v.Length)
                        return null;
                    var scale = u.Length;
                    var (inner, outer) = (r0 * scale, r1 * scale);
                    var focus = space.MapPoint((float)x1, (float)y1);
                    var other = space.MapPoint((float)x0, (float)y0);
                    if (Math.Abs(outer - inner) < 1e-6 || (other - focus).Length > 0.02 * Math.Max(inner, outer) + 0.5)
                        return null;
                    if (!box.Contains(focus))
                        return null;
                    // DrawingML's circle runs from the focus to the box's farthest corner.
                    var reach = new[] { new SKPoint(box.Left, box.Top), new SKPoint(box.Right, box.Top), new SKPoint(box.Left, box.Bottom), new SKPoint(box.Right, box.Bottom) }
                        .Max(p => (p - focus).Length);
                    return new PdfSlideGradient(true, 0, new SKPoint((focus.X - box.Left) / box.Width, (focus.Y - box.Top) / box.Height),
                        Stops(-inner / (outer - inner), reach / (outer - inner), t => color(PdfShadings.ColorAt(shading, t)), extendStart, extendEnd));
                }
            default:
                return null;
        }
    }

    /// <summary>The DrawingML gradient over <paramref name="box"/> of a shading's look (PdfShadingLook), each colour through <paramref name="color"/>.</summary>
    private static PdfSlideGradient? LookGradient(PdfShadingLook look, SKRect box, Func<SKColor, SKColor> color)
    {
        var centre = new SKPoint(box.MidX, box.MidY);
        var way = look.End - look.Start;
        var distance = way.Length;
        if (distance < 1e-3f)
            return null;
        SKColor At(double t) => color(look.At(t));
        if (look.Radial)
        {
            var focus = look.Start;
            if (!box.Contains(focus))
                return null;
            var reach = new[] { new SKPoint(box.Left, box.Top), new SKPoint(box.Right, box.Top), new SKPoint(box.Left, box.Bottom), new SKPoint(box.Right, box.Bottom) }
                .Max(p => (p - focus).Length);
            return new PdfSlideGradient(true, 0, new SKPoint((focus.X - box.Left) / box.Width, (focus.Y - box.Top) / box.Height),
                Stops(0, reach / distance, At, true, true));
        }
        var normal = new SKPoint(way.X / distance, way.Y / distance);
        var angle = Math.Atan2(normal.Y, normal.X);
        var half = (Math.Abs(box.Width * Math.Cos(angle)) + Math.Abs(box.Height * Math.Sin(angle))) / 2;
        var offset = Dot(centre - look.Start, normal);
        var degrees = (angle * 180 / Math.PI % 360 + 360) % 360;
        return new PdfSlideGradient(false, degrees, default, Stops((offset - half) / distance, 2 * half / distance, At, true, true));
    }

    private static double Dot(SKPoint a, SKPoint b) => a.X * b.X + a.Y * b.Y;

    /// <summary>
    /// Gradient stops for a shading whose parameter is <paramref name="t0"/> + <paramref name="slope"/> × s along the
    /// DrawingML gradient (s from 0 to 1): its colours sampled, transparent beyond an end that doesn't extend.
    /// </summary>
    private static List<(double Position, SKColor Color)> Stops(double t0, double slope, Func<double, SKColor> At, bool extendStart, bool extendEnd)
    {
        const int Samples = 16;
        var positions = new SortedSet<double> { 0, 1 };
        if (Math.Abs(slope) > 1e-9)
        {
            for (var k = 0; k <= Samples; k++)
            {
                var s = (k / (double)Samples - t0) / slope;
                if (s is > 0 and < 1)
                    positions.Add(s);
            }
        }
        var stops = new List<(double, SKColor)>();
        foreach (var s in positions)
        {
            var t = t0 + slope * s;
            var before = t < 0 ? (extendStart ? At(0) : At(0).WithAlpha(0)) : t > 1 ? (extendEnd ? At(1) : At(1).WithAlpha(0)) : At(t);
            // A hard edge where the shading stops without extending: the colour, then nothing (or the other way round).
            var atStart = Math.Abs(t) < 1e-6;
            var edge = atStart && !extendStart || Math.Abs(t - 1) < 1e-6 && !extendEnd;
            if (edge && s is > 0 and < 1)
            {
                var outside = atStart ? At(0).WithAlpha(0) : At(1).WithAlpha(0);
                // Outside first where the way runs into the shading.
                var rising = slope > 0 == atStart;
                stops.Add((s, rising ? outside : At(t)));
                stops.Add((s, rising ? At(t) : outside));
                continue;
            }
            stops.Add((s, before));
        }
        return stops;
    }

    /// <summary>
    /// <paramref name="paints"/> drawn in their order into a transparent PNG over the part of the page they cover, at
    /// <paramref name="dpi"/> (less for a large area); null when they cover nothing.
    /// </summary>
    private PdfSlidePicture? DrawGraphics(List<Entry> paints, double width, double height, int dpi, PdfRasterCache cache, CancellationToken cancellationToken)
    {
        var page = new SKRect(0, 0, (float)width, (float)height);
        var area = SKRect.Empty;
        foreach (var entry in paints)
            area = area.IsEmpty ? entry.Area : SKRect.Union(area, entry.Area);
        if (!area.IntersectsWith(page))
            return null;
        area.Intersect(page);
        area = SKRect.Create(MathF.Floor(area.Left), MathF.Floor(area.Top), MathF.Ceiling(area.Right) - MathF.Floor(area.Left),
            MathF.Ceiling(area.Bottom) - MathF.Floor(area.Top));
        if (area.Width < 1 || area.Height < 1)
            return null;

        var scale = dpi / 72.0;
        if (area.Width * scale * area.Height * scale > MaxGraphicsPixels)
            scale = Math.Sqrt(MaxGraphicsPixels / (area.Width * area.Height));
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(area.Width * scale));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(area.Height * scale));
        using var bitmap = new SKBitmap(new SKImageInfo(pixelWidth, pixelHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale((float)scale);
            canvas.Translate(-area.Left, -area.Top);
            PdfSlideRaster.Draw(canvas, paints.Select(p => p.Paint!), cancellationToken: cancellationToken, pictures: true, cache: cache);
        }

        var file = NewMediaPath("png");
        using (var image = SKImage.FromBitmap(bitmap))
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
        using (var output = File.Create(file))
            data.SaveTo(output);
        return new PdfSlidePicture(area, file);
    }

    /// <summary>
    /// How much the transformation each path was painted under scales its line width (PdfPig gives the width as the
    /// content stream sets it, before the transformation: under "2 0 0 2 0 0 cm" a "1 w" line is 2 points wide), by
    /// following q / Q / cm through the page's operations. Null when the painting operators don't match the paths one
    /// to one (a form XObject drew some of them, or clipping removed one): the widths are then taken as they are.
    /// </summary>
    private static Dictionary<PdfPath, double>? StrokeScales(IReadOnlyList<IGraphicsStateOperation> operations, IReadOnlyList<PdfPath> paths)
    {
        var scales = new List<double>();
        var saved = new Stack<(double A, double B, double C, double D)>();
        var (a, b, c, d) = (1.0, 0.0, 0.0, 1.0);
        foreach (var operation in operations)
        {
            switch (operation)
            {
                case Push:
                    if (saved.Count < 4096)
                        saved.Push((a, b, c, d));
                    break;
                case Pop:
                    if (saved.Count > 0)
                        (a, b, c, d) = saved.Pop();
                    break;
                case ModifyCurrentTransformationMatrix { Value.Length: >= 4 } cm:
                    var v = cm.Value;
                    (a, b, c, d) = (v[0] * a + v[1] * c, v[0] * b + v[1] * d, v[2] * a + v[3] * c, v[2] * b + v[3] * d);
                    break;
                case StrokePath or CloseAndStrokePath or FillPathEvenOddRule or FillPathNonZeroWinding or FillPathNonZeroWindingCompatibility
                    or FillPathEvenOddRuleAndStroke or FillPathNonZeroWindingAndStroke or CloseFillPathEvenOddRuleAndStroke
                    or CloseFillPathNonZeroWindingAndStroke:
                    var scale = Math.Sqrt(Math.Abs(a * d - b * c));
                    scales.Add(double.IsFinite(scale) ? Math.Clamp(scale, 1e-3, 1e3) : 1);
                    break;
            }
        }
        var painted = paths.Where(p => !p.IsClipping).ToList();
        if (painted.Count != scales.Count || scales.All(s => Math.Abs(s - 1) < 1e-6))
            return null;
        var result = new Dictionary<PdfPath, double>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < painted.Count; i++)
            result[painted[i]] = scales[i];
        return result;
    }

    // ───────────────────────── Notes ─────────────────────────

    /// <summary>
    /// The page's comments for the speaker notes: the text of its sticky notes, free text and text markup
    /// annotations, top to bottom, "Author: text"; null when there are none (or PdfPig can't read them).
    /// </summary>
    private static string? Notes(Page page)
    {
        List<Annotation> annotations;
        try
        {
            annotations = [.. page.GetAnnotations()];
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return null; // the notes are an extra: broken annotations leave them out, not the page
        }
        var lines = new List<string>();
        var length = 0;
        foreach (var annotation in annotations
                     .Where(a => a.Type is AnnotationType.Text or AnnotationType.FreeText or AnnotationType.Highlight or AnnotationType.Underline
                         or AnnotationType.StrikeOut or AnnotationType.Squiggly or AnnotationType.Caret)
                     .Where(a => !string.IsNullOrWhiteSpace(a.Content))
                     .OrderByDescending(a => a.Rectangle.Top).ThenBy(a => a.Rectangle.Left))
        {
            var text = annotation.Content!.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
            if (Author(annotation) is { } author)
                text = $"{author}: {text}";
            if (length + text.Length > MaxNotes)
                break;
            length += text.Length;
            lines.Add(text);
        }
        return lines.Count == 0 ? null : string.Join('\n', lines);
    }

    private static string? Author(Annotation annotation)
    {
        if (!annotation.AnnotationDictionary.TryGet(NameToken.Create("T"), out var token))
            return null;
        return token switch
        {
            StringToken text => NullIfBlank(text.Data),
            HexToken hex => NullIfBlank(hex.Data),
            _ => null,
        };
    }
}
