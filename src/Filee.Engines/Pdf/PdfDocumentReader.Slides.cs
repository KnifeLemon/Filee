// PDF pages as editable slides (PDF → PPTX, PdfSlidesConverter): each page is read like PDF → DOCX (ReadPage: words →
// lines → paragraphs with their fonts, sizes, colours, bold / italic and links; ruled tables; pictures), but every
// paragraph keeps its place on the page as a text box of its own instead of joining the flow of a document.
//
// - Text: one box per paragraph, as wide as its lines (with a little room for a wider font), its first baseline where
//   the PDF has it. A paragraph of one line doesn't wrap, so a substitute font never breaks it in two; longer ones wrap
//   at the box's width, so edited text flows. Alignment, indents, tabs and the line distance come from the PDF.
// - Ruled tables become real tables (the cells' text, borders, shading and sizes as for DOCX); pictures in their
//   cells stay pictures on top of them.
// - Pictures are picture shapes at their place, also one as large as the page (a slide's background picture or a
//   scan): the slides keep everything that is seen, nothing is a "scan under an OCR layer" here.
// - Vector graphics: a filled or stroked rectangle becomes a rectangle shape, a straight line a line shape, so they
//   can be moved and recoloured; a rectangle filling the page is the slide's background colour. Everything else
//   (curves, polygons, more than MaxShapes simple shapes on one page) is drawn into one transparent picture of the
//   page's remaining graphics (Skia, from PdfPig's paths), in the order the PDF paints them, line widths and dashes
//   scaled by the transformation they were drawn under.
//   Rules and shading of a ruled table are the table's own borders and fills, so they are not repeated as shapes.
// - Drawing order on the slide: graphics and pictures, larger ones behind smaller ones (a background picture, then a
//   coloured bar, then the logo on it), tables, then the text on top.
//
// Not rebuilt (left for later): text that runs sideways or turned (PdfPig's word extractor keeps horizontal text only;
// a page with nothing but such text is rendered as a picture), pattern and shading fills (gradients), transparency,
// pictures drawn turned or as stencil masks, clipping of drawings (a clipped drawing is drawn whole, within the page),
// form fields and annotations.

using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;
using UglyToad.PdfPig.Graphics;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.Graphics.Core;
using UglyToad.PdfPig.Graphics.Operations;
using UglyToad.PdfPig.Graphics.Operations.PathPainting;
using UglyToad.PdfPig.Graphics.Operations.SpecialGraphicsState;

namespace Filee.Engines.Pdf;

/// <summary>A PDF page as the content of one editable slide: its size (points) and what goes on it, back to front.</summary>
/// <param name="Background">"#RRGGBB" of a rectangle filling the page; null = white.</param>
internal sealed record PdfSlidePage(double Width, double Height, string? Background, IReadOnlyList<PdfSlideItem> Items);

/// <summary>Something on a slide in its box: points from the page's top-left corner, y downwards.</summary>
internal abstract record PdfSlideItem(SKRect Bounds);

/// <summary>A text box holding one paragraph; <paramref name="Wrap"/> = its lines wrap at the box's width.</summary>
internal sealed record PdfSlideText(SKRect Bounds, HParagraph Paragraph, bool Wrap) : PdfSlideItem(Bounds);

/// <summary>A picture (a JPEG or PNG file in the media folder) stretched over its box.</summary>
internal sealed record PdfSlidePicture(SKRect Bounds, string Path) : PdfSlideItem(Bounds);

/// <summary>A ruled table: the model table, its column widths and row heights in points.</summary>
internal sealed record PdfSlideTable(SKRect Bounds, HTable Table, float[] Columns, float[] Rows) : PdfSlideItem(Bounds);

internal enum PdfShapeKind
{
    Rectangle,

    /// <summary>A straight line across its box, from the top-left corner unless flipped.</summary>
    Line,
}

/// <summary>A rectangle or a line; colours "#RRGGBB", null = none; the line width in points.</summary>
internal sealed record PdfSlideShape(SKRect Bounds, PdfShapeKind Kind, string? Fill, string? Line, float LineWidth, bool Dashed,
    bool FlipHorizontal = false, bool FlipVertical = false) : PdfSlideItem(Bounds);

internal sealed partial class PdfDocumentReader
{
    /// <summary>Simple shapes one slide gets at most; a page with more (a chart of bars) has them drawn into its picture.</summary>
    internal const int MaxShapes = 1000;

    /// <summary>Line segments of one page's graphics drawn at most; a page with more is drawn up to there.</summary>
    internal const int MaxGraphicsSegments = 500_000;

    /// <summary>A page's graphics picture has at most this many pixels (the resolution drops for a larger page).</summary>
    internal const double MaxGraphicsPixels = 25_000_000;

    /// <summary>How far above its baseline a line's box starts, in ems (about a Latin font's ascender).</summary>
    private const double SlideAscent = 0.92;

    /// <summary>How far below the last baseline a text box ends, in ems.</summary>
    private const double SlideDescent = 0.3;

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
            // curve. What a clip hides of a drawing is drawn (within the page) for now.
            pdf = PdfDocument.Open(path, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
        }
        catch (PdfDocumentEncryptedException)
        {
            throw PasswordProtected();
        }

        using (pdf)
        {
            var pages = pagesOf(pdf.NumberOfPages);
            if (pages.Count > budget.Limits.MaxPages)
                throw DocumentLimits.TooLarge($"The PDF has more than {DocumentLimits.Count(budget.Limits.MaxPages)} pages to put on slides.");
            foreach (var index in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Page page;
                try
                {
                    page = pdf.GetPage(index + 1);
                }
                catch (PdfDocumentEncryptedException)
                {
                    throw PasswordProtected();
                }
                slide(reader.SlidePage(page, dpi, budget, cancellationToken));
            }
        }
    }

    private PdfSlidePage SlidePage(Page page, int dpi, DocumentBudget budget, CancellationToken cancellationToken)
    {
        var content = ReadPage(page);
        var (width, height) = (content.Width, content.Height);
        var items = new List<PdfSlideItem>();
        if (content.Scanned)
        {
            // Text that runs sideways only: the rendered page keeps it.
            if (content.ScanPath is { } scan)
            {
                budget.Object();
                items.Add(new PdfSlidePicture(new SKRect(0, 0, (float)width, (float)height), scan));
            }
            return new PdfSlidePage(width, height, null, items);
        }

        var ordered = ReadingOrder(content.Items);
        var texts = ordered.Where(i => !i.IsPicture && !i.IsTable).ToList();
        var lineRatio = TypicalLineRatio(texts);
        var tables = ordered.Where(i => i.IsTable).ToList();

        // Back to front: pictures and graphics by size, then tables, then text.
        var graphics = SlideGraphics(page, width, height, tables.Select(t => t.Table!.Bounds).ToList(), dpi, budget, cancellationToken);
        var behind = new List<PdfSlideItem>(graphics.Items);
        foreach (var picture in ordered.Where(i => i.IsPicture).Concat(tables.SelectMany(t => t.Table!.Cells.SelectMany(c => c.Pictures))))
        {
            budget.Object();
            behind.Add(new PdfSlidePicture(Box(picture.PictureBounds!.Value, height), picture.PicturePath!));
        }
        items.AddRange(behind.OrderByDescending(i => i.Bounds.Width * i.Bounds.Height));
        foreach (var table in tables)
        {
            budget.Object();
            items.Add(SlideTable(table, height, lineRatio, budget));
        }
        foreach (var text in texts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            budget.Object();
            budget.Paragraph();
            items.Add(TextBox(text, height, lineRatio));
        }
        return new PdfSlidePage(width, height, graphics.Background, items);
    }

    /// <summary>A PDF box (y upwards) on a page <paramref name="height"/> points high as a slide box (y downwards).</summary>
    private static SKRect Box(PdfRectangle box, double height) =>
        new((float)box.Left, (float)(height - box.Top), (float)box.Right, (float)(height - box.Bottom));

    // ───────────────────────── Text ─────────────────────────

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

    // ───────────────────────── Graphics ─────────────────────────

    /// <summary>
    /// The page's vector graphics: rectangles and straight lines as shapes, the rest drawn into one picture, and the
    /// colour of a first rectangle that fills the page. Straight-edged paths inside a ruled table are its rules and
    /// shading, which the table has itself.
    /// </summary>
    private (string? Background, List<PdfSlideItem> Items) SlideGraphics(Page page, double width, double height,
        List<PdfRectangle> tables, int dpi, DocumentBudget budget, CancellationToken cancellationToken)
    {
        var paths = page.Paths;
        var scales = StrokeScales(page.Operations, paths);
        double LineWidth(PdfPath path) => path.LineWidth * (scales?.GetValueOrDefault(path, 1) ?? 1);
        string? background = null;
        var shapes = new List<PdfSlideShape>();
        var drawn = new List<PdfPath>();
        var kept = new List<PdfPath>();
        var first = true;
        foreach (var path in paths)
        {
            if (path.IsClipping || (!path.IsFilled && !path.IsStroked) || path.Count == 0)
                continue;
            var fill = path.IsFilled ? PaintColor(path.FillColor) : null;
            var line = path.IsStroked ? PaintColor(path.StrokeColor) : null;
            if (fill is null && line is null)
                continue; // pattern and shading fills are not rebuilt
            var straight = path.All(s => s.Commands.All(c => c is not PdfSubpath.BezierCurve));
            var bounds = path.GetBoundingRectangle();
            if (straight && bounds is { } extent && tables.Any(t => Within(extent, t, 3 + LineWidth(path))))
                continue;

            var simple = straight ? SimpleShapes(path, fill, line, LineWidth(path), height) : null;
            // Outlined in its own colour at most (PdfPig's writer and some drivers fill and stroke a background).
            if (first && simple is [{ Kind: PdfShapeKind.Rectangle, Fill: { } color } only] && (only.Line is null || only.Line == color)
                && only.Bounds.Width >= 0.98 * width && only.Bounds.Height >= 0.98 * height)
            {
                background = color;
                first = false;
                continue;
            }
            first = false;
            kept.Add(path);
            if (simple is null)
                drawn.Add(path);
            else
                shapes.AddRange(simple);
        }

        var items = new List<PdfSlideItem>();
        if (shapes.Count > MaxShapes)
        {
            // A chart of bars or a pattern of lines: one picture, not a thousand shapes to click through.
            drawn = kept;
            shapes.Clear();
        }
        foreach (var shape in shapes)
        {
            budget.Object();
            items.Add(shape);
        }
        if (drawn.Count > 0 && DrawGraphics(drawn, LineWidth, width, height, dpi, cancellationToken) is { } picture)
        {
            budget.Object();
            items.Add(picture);
        }
        return (background, items);
    }

    /// <summary>"#RRGGBB" of a fill or stroke colour; null for pattern and shading colours.</summary>
    private static string? PaintColor(IColor? color) => color is PatternColor ? null : Color(color) ?? "#000000";

    private static bool Within(PdfRectangle box, PdfRectangle area, double tolerance) =>
        box.Left >= area.Left - tolerance && box.Right <= area.Right + tolerance && box.Bottom >= area.Bottom - tolerance && box.Top <= area.Top + tolerance;

    /// <summary>
    /// The shapes a path of straight segments is made of when every subpath is an upright rectangle or (stroked) a
    /// single straight line; null for anything else (a polygon, a polyline).
    /// </summary>
    private static List<PdfSlideShape>? SimpleShapes(PdfPath path, string? fill, string? line, double lineWidth, double height)
    {
        var width = (float)Math.Max(0, lineWidth);
        var dashed = path.LineDashPattern is { } dash && !IsSolid(dash);
        var shapes = new List<PdfSlideShape>();
        foreach (var subpath in path)
        {
            var points = new List<PdfPoint>();
            var closed = false;
            foreach (var command in subpath.Commands)
            {
                switch (command)
                {
                    case PdfSubpath.Move move:
                        if (points.Count > 0)
                            return null;
                        points.Add(move.Location);
                        break;
                    case PdfSubpath.Line segment:
                        if (points.Count == 0)
                            points.Add(segment.From);
                        points.Add(segment.To);
                        break;
                    case PdfSubpath.Close:
                        closed = true;
                        break;
                    default:
                        return null;
                }
            }
            if (points.Count > 1 && Same(points[0], points[^1]))
            {
                points.RemoveAt(points.Count - 1);
                closed = true;
            }
            if (points.Count < 2)
                continue; // a lone point paints nothing
            if (points.Count == 2 && !closed || points.Count == 2 && fill is null)
            {
                if (line is null)
                    continue; // a filled line has no area
                var (a, b) = (points[0], points[1]);
                var box = new SKRect((float)Math.Min(a.X, b.X), (float)(height - Math.Max(a.Y, b.Y)), (float)Math.Max(a.X, b.X), (float)(height - Math.Min(a.Y, b.Y)));
                // Slide y runs down: a line rising to the right goes from the bottom-left corner (flipped vertically).
                var rising = (b.X - a.X) * (b.Y - a.Y) > 0;
                shapes.Add(new PdfSlideShape(box, PdfShapeKind.Line, null, line, width, dashed, FlipVertical: rising));
                continue;
            }
            if (points.Count != 4 || !Upright(points))
                return null;
            var left = points.Min(p => p.X);
            var right = points.Max(p => p.X);
            var bottom = points.Min(p => p.Y);
            var top = points.Max(p => p.Y);
            var rectangle = new SKRect((float)left, (float)(height - top), (float)right, (float)(height - bottom));
            // An open rectangle that is only stroked misses its last side: not a rectangle shape.
            if (!closed && fill is null)
                return null;
            shapes.Add(new PdfSlideShape(rectangle, PdfShapeKind.Rectangle, fill, line, width, dashed));
        }
        return shapes;
    }

    private static bool Same(PdfPoint a, PdfPoint b) => Math.Abs(a.X - b.X) < 0.01 && Math.Abs(a.Y - b.Y) < 0.01;

    /// <summary>Four corners joined by horizontal and vertical sides.</summary>
    private static bool Upright(List<PdfPoint> corners)
    {
        for (var i = 0; i < 4; i++)
        {
            var (a, b) = (corners[i], corners[(i + 1) % 4]);
            if (Math.Abs(a.X - b.X) > 0.01 && Math.Abs(a.Y - b.Y) > 0.01)
                return false;
        }
        return true;
    }

    private static bool IsSolid(LineDashPattern dash) => dash.Array.Count == 0 || dash.Array.All(d => d <= 0);

    /// <summary>
    /// <paramref name="paths"/> drawn in their order into a transparent PNG over the part of the page they cover, at
    /// <paramref name="dpi"/> (less for a large area); null when they cover nothing.
    /// </summary>
    private PdfSlidePicture? DrawGraphics(List<PdfPath> paths, Func<PdfPath, double> lineWidth, double width, double height, int dpi,
        CancellationToken cancellationToken)
    {
        // The outlines first: their extent includes the curves (PdfPig's bounding box of a path is that of its end
        // points without clipping).
        var outlines = new List<(PdfPath Path, SKPath Outline)>();
        try
        {
            var segments = 0;
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Outline(path, height, ref segments) is not { } outline)
                    break;
                outlines.Add((path, outline));
            }
            var page = new SKRect(0, 0, (float)width, (float)height);
            var area = SKRect.Empty;
            foreach (var (path, outline) in outlines)
            {
                var rect = outline.TightBounds;
                var half = (float)Math.Max(0.5, lineWidth(path) / 2);
                rect.Inflate(half, half);
                area = area.IsEmpty ? rect : SKRect.Union(area, rect);
            }
            if (!area.IntersectsWith(page))
                return null;
            area.Intersect(page);
            area = SKRect.Create(MathF.Floor(area.Left), MathF.Floor(area.Top), MathF.Ceiling(area.Right) - MathF.Floor(area.Left),
                MathF.Ceiling(area.Bottom) - MathF.Floor(area.Top));
            if (area.Width < 1 || area.Height < 1)
                return null;
            return Drawn(outlines, lineWidth, area, dpi, cancellationToken);
        }
        finally
        {
            foreach (var (_, outline) in outlines)
                outline.Dispose();
        }
    }

    /// <summary>The outlines filled and stroked into a transparent PNG of <paramref name="area"/> (points).</summary>
    private PdfSlidePicture Drawn(List<(PdfPath Path, SKPath Outline)> outlines, Func<PdfPath, double> lineWidth, SKRect area, int dpi,
        CancellationToken cancellationToken)
    {
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
            foreach (var (path, outline) in outlines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (path.IsFilled && PaintColor(path.FillColor) is { } fill)
                {
                    using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = SKColor.Parse(fill) };
                    canvas.DrawPath(outline, paint);
                }
                if (path.IsStroked && PaintColor(path.StrokeColor) is { } stroke)
                {
                    using var paint = new SKPaint
                    {
                        IsAntialias = true,
                        Style = SKPaintStyle.Stroke,
                        Color = SKColor.Parse(stroke),
                        // A width of 0 is the thinnest line the device can show: one pixel.
                        StrokeWidth = (float)Math.Max(lineWidth(path), 1 / scale),
                        StrokeCap = path.LineCapStyle switch { LineCapStyle.Round => SKStrokeCap.Round, LineCapStyle.ProjectingSquare => SKStrokeCap.Square, _ => SKStrokeCap.Butt },
                        StrokeJoin = path.LineJoinStyle switch { LineJoinStyle.Round => SKStrokeJoin.Round, LineJoinStyle.Bevel => SKStrokeJoin.Bevel, _ => SKStrokeJoin.Miter },
                    };
                    using var dash = Dash(path.LineDashPattern, path.LineWidth > 0 ? lineWidth(path) / path.LineWidth : 1);
                    paint.PathEffect = dash;
                    canvas.DrawPath(outline, paint);
                }
            }
        }

        var file = NewMediaPath("png");
        using (var image = SKImage.FromBitmap(bitmap))
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
        using (var output = File.Create(file))
            data.SaveTo(output);
        return new PdfSlidePicture(area, file);
    }

    /// <summary>
    /// The path in slide coordinates; null once the page's segments pass <see cref="MaxGraphicsSegments"/> (the rest of
    /// a pathological drawing is left out rather than drawn for minutes).
    /// </summary>
    private static SKPath? Outline(PdfPath path, double height, ref int segments)
    {
        var outline = new SKPath { FillType = path.FillingRule == FillingRule.EvenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding };
        SKPoint P(PdfPoint point) => new((float)point.X, (float)(height - point.Y));
        foreach (var subpath in path)
        {
            var open = false;
            foreach (var command in subpath.Commands)
            {
                if (++segments > MaxGraphicsSegments)
                {
                    outline.Dispose();
                    return null;
                }
                switch (command)
                {
                    case PdfSubpath.Move move:
                        outline.MoveTo(P(move.Location));
                        open = true;
                        break;
                    case PdfSubpath.Line line:
                        if (!open)
                            outline.MoveTo(P(line.From));
                        outline.LineTo(P(line.To));
                        open = true;
                        break;
                    case PdfSubpath.CubicBezierCurve curve:
                        if (!open)
                            outline.MoveTo(P(curve.StartPoint));
                        outline.CubicTo(P(curve.FirstControlPoint), P(curve.SecondControlPoint), P(curve.EndPoint));
                        open = true;
                        break;
                    case PdfSubpath.QuadraticBezierCurve curve:
                        if (!open)
                            outline.MoveTo(P(curve.StartPoint));
                        outline.QuadTo(P(curve.ControlPoint), P(curve.EndPoint));
                        open = true;
                        break;
                    case PdfSubpath.Close:
                        outline.Close();
                        open = false;
                        break;
                }
            }
        }
        return outline;
    }

    /// <summary>A dash pattern in page units: the PDF's lengths times the <paramref name="scale"/> they were drawn at.</summary>
    private static SKPathEffect? Dash(LineDashPattern? pattern, double scale)
    {
        if (pattern is not { } dash || IsSolid(dash))
            return null;
        var intervals = dash.Array.Take(16).Select(d => (float)Math.Max(0.1, d * scale)).ToList();
        if (intervals.Count % 2 == 1)
            intervals.AddRange(intervals); // "[3] 0 d": 3 on, 3 off
        return SKPathEffect.CreateDash([.. intervals], (float)(dash.Phase * scale));
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
}
