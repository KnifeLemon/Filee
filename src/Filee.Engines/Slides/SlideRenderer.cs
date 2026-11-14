// Draws the slide model (SlideModel.cs) onto the layout engine's page record (PageContent), one page per slide the
// size of the slide, which PdfPageWriter writes as a PDF and PageContent.Render draws into pictures.
//
// - Elements: each in its box, turned about the box's centre and mirrored, groups mapping their child space onto their
//   box. Shapes: their outlines filled (lighten / darken parts of presets as a white or black veil over the fill), then
//   stroked; lines with dashes, caps, joins, double lines and arrowheads (the line shortened under a pointed head).
// - Text: the frame's paragraphs laid out by the layout engine (ParagraphLayouter: shaping, font fallback, bidi, line
//   breaking, tabs, alignment) in the text rectangle less its insets, in columns, anchored at the top, middle or bottom,
//   unwrapped when the frame doesn't wrap, turned for vertical frames. Text turns with its shape but isn't mirrored
//   (PowerPoint turns it upside down for a vertically flipped shape).
// - Tables: rows as high as their content needs at least, cells' fills, text and borders, merged cells over their span.

using Filee.Engines.Rendering;
using Filee.Engines.Typesetting;
using SkiaSharp;

namespace Filee.Engines.Slides;

internal sealed class SlideRenderer : IDisposable
{
    /// <summary>The width unwrapped text is laid out in before it is placed.</summary>
    private const float Unbounded = 100_000;

    private readonly ParagraphLayouter _layouter;
    private readonly SlideImages _images = new();
    private readonly CancellationToken _cancellationToken;

    public SlideRenderer(FontResolver fonts, Shaper shaper, CancellationToken cancellationToken = default)
    {
        _layouter = new ParagraphLayouter(fonts, shaper, storedLines: false);
        _cancellationToken = cancellationToken;
    }

    /// <summary>The pictures the pages draw; they live as long as the renderer.</summary>
    public SlideImages Images => _images;

    public void Dispose() => _images.Dispose();

    /// <summary>One page per slide.</summary>
    public List<PageContent> Render(SlideDeck deck)
    {
        var pages = new List<PageContent>(deck.Slides.Count);
        foreach (var slide in deck.Slides)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            pages.Add(Render(deck, slide));
        }
        return pages;
    }

    public PageContent Render(SlideDeck deck, Slide slide)
    {
        var page = new PageContent(deck.Width, deck.Height);
        var area = new SKRect(0, 0, deck.Width, deck.Height);
        page.Draw(canvas =>
        {
            using var white = new SKPaint { Color = SKColors.White };
            canvas.DrawRect(area, white);
            if (slide.Background is { } background)
            {
                using var path = new SKPath();
                path.AddRect(area);
                SlidePaint.Fill(canvas, path, background, area, _images);
            }
        });
        foreach (var element in slide.Elements)
            Element(page, element, SKMatrix.Identity);
        return page;
    }

    /// <summary>The element's own coordinates (its box from 0, 0) → its parent's.</summary>
    private static SKMatrix Local(SlideElement element, bool mirror = true)
    {
        var box = element.Bounds;
        var (cx, cy) = (box.Width / 2, box.Height / 2);
        var matrix = SKMatrix.CreateTranslation(box.Left + cx, box.Top + cy);
        if (element.Rotation != 0)
            matrix = matrix.PreConcat(SKMatrix.CreateRotationDegrees(element.Rotation));
        if (mirror && (element.FlipHorizontal || element.FlipVertical))
            matrix = matrix.PreConcat(SKMatrix.CreateScale(element.FlipHorizontal ? -1 : 1, element.FlipVertical ? -1 : 1));
        return matrix.PreConcat(SKMatrix.CreateTranslation(-cx, -cy));
    }

    private void Element(PageContent page, SlideElement element, SKMatrix parent)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (!(element.Bounds.Width >= 0 && element.Bounds.Height >= 0) || !float.IsFinite(element.Bounds.Left) || !float.IsFinite(element.Bounds.Top))
            return;
        var matrix = parent.PreConcat(Local(element));
        switch (element)
        {
            case SlideGroup group:
                var child = group.ChildBounds;
                var scaleX = child.Width > 0 ? group.Bounds.Width / child.Width : 1;
                var scaleY = child.Height > 0 ? group.Bounds.Height / child.Height : 1;
                var inner = matrix.PreConcat(SKMatrix.CreateScale(scaleX, scaleY)).PreConcat(SKMatrix.CreateTranslation(-child.Left, -child.Top));
                foreach (var item in group.Children)
                    Element(page, item, inner);
                break;
            case SlideShape shape:
                Shape(page, shape, matrix);
                if (shape.Text is { IsEmpty: false } text)
                {
                    // Text turns with its shape but isn't mirrored; a vertically flipped shape turns it upside down.
                    var textMatrix = parent.PreConcat(Local(shape, mirror: false));
                    if (shape.FlipVertical)
                        textMatrix = textMatrix.PreConcat(SKMatrix.CreateRotationDegrees(180, shape.Bounds.Width / 2, shape.Bounds.Height / 2));
                    Text(page, text, new SKSize(shape.Bounds.Width, shape.Bounds.Height), textMatrix);
                }
                break;
            case SlideTable table:
                Table(page, table, matrix);
                break;
        }
    }

    // ───────────────────────── Shapes ─────────────────────────

    private void Shape(PageContent page, SlideShape shape, SKMatrix matrix)
    {
        var box = new SKRect(0, 0, shape.Bounds.Width, shape.Bounds.Height);
        if (shape.Fill is null && shape.Line is null)
            return;
        page.Draw(canvas =>
        {
            canvas.Concat(in matrix);
            if (shape.Fill is { } fill)
            {
                foreach (var path in shape.Paths.Where(p => p.Fill != PathFill.None))
                {
                    SlidePaint.Fill(canvas, path.Path, fill, box, _images);
                    Veil(canvas, path);
                }
            }
            if (shape.Line is { } line)
                Stroke(canvas, shape.Paths.Where(p => p.Stroke).Select(p => p.Path).ToList(), line, box);
        });
    }

    /// <summary>The lighter or darker parts of 3D-looking presets: a white or black veil over the fill.</summary>
    private static void Veil(SKCanvas canvas, SlidePath path)
    {
        var veil = path.Fill switch
        {
            PathFill.Lighten => SKColors.White.WithAlpha(102),
            PathFill.LightenLess => SKColors.White.WithAlpha(51),
            PathFill.Darken => SKColors.Black.WithAlpha(102),
            PathFill.DarkenLess => SKColors.Black.WithAlpha(51),
            _ => SKColors.Empty,
        };
        if (veil == SKColors.Empty)
            return;
        using var paint = new SKPaint { Color = veil, IsAntialias = true, Style = SKPaintStyle.Fill };
        canvas.DrawPath(path.Path, paint);
    }

    /// <summary>Strokes <paramref name="paths"/> with <paramref name="line"/>, its arrowheads at the first path's start and the last's end.</summary>
    internal static void Stroke(SKCanvas canvas, IReadOnlyList<SKPath> paths, SlideLine line, SKRect box)
    {
        if (paths.Count == 0)
            return;
        var width = line.Width > 0 ? line.Width : SlideLine.Hairline;
        using var paint = SlidePaint.StrokePaint(line.Fill, box);
        paint.StrokeWidth = width;
        paint.StrokeCap = line.Cap;
        paint.StrokeJoin = line.Join;
        paint.StrokeMiter = 8;
        // Round and square caps reach half the width past each dash's ends: the dashes are drawn that much shorter.
        var capped = line.Cap == SKStrokeCap.Butt ? 0 : width;
        using var dash = line.Dash is { Length: >= 2 } pattern
            ? SKPathEffect.CreateDash([.. (pattern.Length % 2 == 0 ? pattern : [.. pattern, .. pattern])
                .Select((d, i) => Math.Max(0.01f, d * width + (i % 2 == 0 ? -capped : capped)))], 0)
            : null;
        paint.PathEffect = dash;
        var (head, tail) = (line.Head, line.Tail);
        for (var i = 0; i < paths.Count; i++)
        {
            var path = paths[i];
            var first = i == 0 && head.Kind != LineEndKind.None;
            var last = i == paths.Count - 1 && tail.Kind != LineEndKind.None;
            using var shortened = first || last ? Shorten(path, first ? Setback(head, width) : 0, last ? Setback(tail, width) : 0) : null;
            var drawn = shortened ?? path;
            if (line.Compound == LineCompound.Single)
            {
                canvas.DrawPath(drawn, paint);
            }
            else
            {
                // Double lines: the stroke's outline less a middle third.
                using var outer = paint.GetFillPath(drawn);
                paint.StrokeWidth = width / 3;
                using var middle = paint.GetFillPath(drawn);
                paint.StrokeWidth = width;
                using var both = outer.Op(middle, SKPathOp.Difference);
                using var fill = SlidePaint.StrokePaint(line.Fill, box);
                fill.Style = SKPaintStyle.Fill;
                if (both is not null)
                    canvas.DrawPath(both, fill);
            }
        }
        if (head.Kind != LineEndKind.None)
            End(canvas, paths[0], head, width, line, atStart: true);
        if (tail.Kind != LineEndKind.None)
            End(canvas, paths[^1], tail, width, line, atStart: false);
    }

    /// <summary>How far a line stops short of its end under an arrowhead (pointed heads cover the line's end).</summary>
    private static float Setback(LineEnd end, float width) => end.Kind switch
    {
        LineEndKind.Triangle => HeadLength(end, width) * 0.9f,
        LineEndKind.Stealth => HeadLength(end, width) * 0.6f,
        _ => 0,
    };

    private static float HeadLength(LineEnd end, float width) => end.Length * Math.Max(width, 1);

    private static float HeadWidth(LineEnd end, float width) => end.Width * Math.Max(width, 1);

    /// <summary>An open path without its first <paramref name="start"/> and last <paramref name="end"/> points of length.</summary>
    private static SKPath? Shorten(SKPath path, float start, float end)
    {
        using var measure = new SKPathMeasure(path, false);
        var length = measure.Length;
        if (length <= start + end + 0.01f || measure.NextContour())
            return null;
        using var again = new SKPathMeasure(path, false);
        var result = new SKPath();
        return again.GetSegment(start, length - end, result, true) ? result : null;
    }

    /// <summary>Draws an arrowhead or other mark at a path's start or end, pointing away from the path.</summary>
    private static void End(SKCanvas canvas, SKPath path, LineEnd end, float width, SlideLine line, bool atStart)
    {
        // The last contour holds the path's end. (A measure whose NextContour found none has no contour left, so the
        // contours are counted on a second one first.)
        var contours = 1;
        if (!atStart)
        {
            using var probe = new SKPathMeasure(path, false);
            while (probe.NextContour())
                contours++;
        }
        using var measure = new SKPathMeasure(path, false);
        for (var i = 1; i < contours; i++)
            measure.NextContour();
        var length = measure.Length;
        if (length <= 0)
            return;
        if (!measure.GetPositionAndTangent(atStart ? 0 : Math.Max(0, length - 0.001f), out var point, out var tangent))
            return;
        var direction = atStart ? new SKPoint(-tangent.X, -tangent.Y) : tangent;
        var normal = new SKPoint(-direction.Y, direction.X);
        var headLength = HeadLength(end, width);
        var halfWidth = HeadWidth(end, width) / 2;
        SKPoint At(float along, float across) =>
            new(point.X + direction.X * along + normal.X * across, point.Y + direction.Y * along + normal.Y * across);
        using var paint = new SKPaint { Color = SlidePaint.Representative(line.Fill), IsAntialias = true, Style = SKPaintStyle.Fill };
        using var mark = new SKPath();
        switch (end.Kind)
        {
            case LineEndKind.Triangle:
                mark.AddPoly([At(0, 0), At(-headLength, halfWidth), At(-headLength, -halfWidth)]);
                break;
            case LineEndKind.Stealth:
                mark.AddPoly([At(0, 0), At(-headLength, halfWidth), At(-headLength * 0.6f, 0), At(-headLength, -halfWidth)]);
                break;
            case LineEndKind.Diamond:
                mark.AddPoly([At(headLength / 2, 0), At(0, halfWidth), At(-headLength / 2, 0), At(0, -halfWidth)]);
                break;
            case LineEndKind.Oval:
                {
                    canvas.Save();
                    var angle = MathF.Atan2(direction.Y, direction.X) * 180 / MathF.PI;
                    canvas.Translate(point.X, point.Y);
                    canvas.RotateDegrees(angle);
                    canvas.DrawOval(SKPoint.Empty, new SKSize(headLength / 2, halfWidth), paint);
                    canvas.Restore();
                    return;
                }
            case LineEndKind.Arrow:
                {
                    paint.Style = SKPaintStyle.Stroke;
                    paint.StrokeWidth = width;
                    paint.StrokeCap = SKStrokeCap.Round;
                    paint.StrokeJoin = SKStrokeJoin.Round;
                    mark.MoveTo(At(-headLength, halfWidth));
                    mark.LineTo(At(0, 0));
                    mark.LineTo(At(-headLength, -halfWidth));
                    break;
                }
        }
        canvas.DrawPath(mark, paint);
    }

    // ───────────────────────── Text ─────────────────────────

    /// <summary>Lays out and draws a text frame of a box <paramref name="size"/> through <paramref name="matrix"/>.</summary>
    private void Text(PageContent page, SlideTextFrame frame, SKSize size, SKMatrix matrix)
    {
        var area = frame.Area ?? new SKRect(0, 0, size.Width, size.Height);
        var content = new SKRect(area.Left + frame.InsetLeft, area.Top + frame.InsetTop, area.Right - frame.InsetRight, area.Bottom - frame.InsetBottom);
        if (content.Width < 0)
            content = new SKRect(content.MidX, content.Top, content.MidX, content.Bottom);
        if (content.Height < 0)
            content = new SKRect(content.Left, content.MidY, content.Right, content.MidY);
        if (frame.Rotation != 0)
            matrix = matrix.PreConcat(SKMatrix.CreateRotationDegrees(frame.Rotation, area.MidX, area.MidY));

        // A vertical frame is laid out across its height and turned into place.
        var (width, height) = frame.Direction == TextDirection.Horizontal ? (content.Width, content.Height) : (content.Height, content.Width);
        var layer = new PageContent(Math.Max(1, width), Math.Max(1, height));
        Lay(layer, frame, width, height);
        var place = frame.Direction switch
        {
            TextDirection.Down => SKMatrix.CreateTranslation(content.Right, content.Top).PreConcat(SKMatrix.CreateRotationDegrees(90)),
            TextDirection.Up => SKMatrix.CreateTranslation(content.Left, content.Bottom).PreConcat(SKMatrix.CreateRotationDegrees(-90)),
            _ => SKMatrix.CreateTranslation(content.Left, content.Top),
        };
        page.Add(layer, matrix.PreConcat(place));
    }

    /// <summary>A laid-out line with where it goes in its frame.</summary>
    private readonly record struct PlacedLine(LineBox Line, float X, float Top);

    /// <summary>Lays out the frame's paragraphs in a <paramref name="width"/> × <paramref name="height"/> area and draws them.</summary>
    internal void Lay(PageContent layer, SlideTextFrame frame, float width, float height)
    {
        var columns = Math.Max(1, frame.Columns);
        var gap = columns > 1 ? Math.Max(0, frame.ColumnGap) : 0;
        var columnWidth = Math.Max(1, (width - gap * (columns - 1)) / columns);

        // Unwrapped text: each paragraph one line, placed by its alignment in the frame however wide it gets.
        var layoutWidth = columnWidth;
        var shift = 0f;
        if (!frame.Wrap)
        {
            var natural = 0f;
            foreach (var paragraph in frame.Paragraphs)
                foreach (var line in _layouter.Layout(paragraph, Unbounded).Lines)
                    natural = Math.Max(natural, line.Left + line.Width);
            if (natural > columnWidth)
            {
                layoutWidth = natural + 0.5f;
                var align = frame.Paragraphs.FirstOrDefault()?.Format.Align ?? Hwp.Hwpx.HAlign.Left;
                shift = align switch
                {
                    Hwp.Hwpx.HAlign.Center => (columnWidth - layoutWidth) / 2,
                    Hwp.Hwpx.HAlign.Right => columnWidth - layoutWidth,
                    _ => 0,
                };
            }
        }

        var lines = new List<PlacedLine>();
        var column = 0;
        var y = 0f;
        var first = true;
        foreach (var paragraph in frame.Paragraphs)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var box = _layouter.Layout(paragraph, layoutWidth);
            if (!first)
                y += box.SpaceBefore;
            first = false;
            foreach (var line in box.Lines)
            {
                // Columns: a line that would leave the frame goes to the top of the next column.
                if (columns > 1 && column < columns - 1 && y > 0 && y + line.Height > height)
                {
                    column++;
                    y = 0;
                }
                // Line spacing is taken above the text, as PowerPoint does: more than single moves the text down
                // within its line, less than single lets it reach up into the line before.
                lines.Add(new PlacedLine(line, column * (columnWidth + gap) + shift, y + line.Height - line.TextHeight));
                y += line.Height;
            }
            y += box.SpaceAfter;
        }
        if (lines.Count == 0)
            return;

        // The text's height: to the last line's bottom (its spacing is above it).
        var used = columns > 1 ? height : lines.Max(l => l.Top + l.Line.TextHeight);
        var top = frame.Anchor switch
        {
            TextAnchor.Middle => (height - used) / 2,
            TextAnchor.Bottom => height - used,
            _ => 0,
        };
        var left = 0f;
        if (frame.AnchorCenter && columns == 1)
        {
            var from = lines.Min(l => l.X + l.Line.Left);
            var to = lines.Max(l => l.X + l.Line.Left + l.Line.Width);
            left = (width - (to - from)) / 2 - from;
        }
        foreach (var (line, x, lineTop) in lines)
            line.Draw(layer, x + left, top + lineTop);
    }

    // ───────────────────────── Tables ─────────────────────────

    private void Table(PageContent page, SlideTable table, SKMatrix matrix)
    {
        var columns = table.Columns;
        var xs = new float[columns.Length + 1];
        for (var i = 0; i < columns.Length; i++)
            xs[i + 1] = xs[i] + columns[i];
        var rows = table.Rows;
        var heights = rows.Select(r => r.Height).ToArray();

        // Rows grow to their cells' text: single-row cells first, then cells spanning rows stretch their last row.
        var cells = new List<(SlideTableCell Cell, int Row, int Column)>();
        for (var r = 0; r < rows.Count; r++)
        {
            var c = 0;
            foreach (var cell in rows[r].Cells)
            {
                if (c >= columns.Length)
                    break;
                cells.Add((cell, r, c));
                c++;
            }
        }
        foreach (var span in new[] { false, true })
        {
            foreach (var (cell, r, c) in cells.Where(x => !x.Cell.Covered && x.Cell.RowSpan > 1 == span))
            {
                var width = xs[Math.Min(columns.Length, c + cell.ColumnSpan)] - xs[c];
                var needed = TextHeight(cell.Text, width);
                var last = Math.Min(rows.Count, r + cell.RowSpan) - 1;
                var have = heights.Skip(r).Take(last - r + 1).Sum();
                if (needed > have)
                    heights[last] += needed - have;
            }
        }
        var ys = new float[rows.Count + 1];
        for (var i = 0; i < rows.Count; i++)
            ys[i + 1] = ys[i] + heights[i];

        SKRect Area(SlideTableCell cell, int r, int c) =>
            new(xs[c], ys[r], xs[Math.Min(columns.Length, c + cell.ColumnSpan)], ys[Math.Min(rows.Count, r + cell.RowSpan)]);
        var visible = cells.Where(x => !x.Cell.Covered).ToList();
        page.Draw(canvas =>
        {
            canvas.Concat(in matrix);
            foreach (var (cell, r, c) in visible)
            {
                if (cell.Fill is not { } fill)
                    continue;
                var area = Area(cell, r, c);
                using var path = new SKPath();
                path.AddRect(area);
                SlidePaint.Fill(canvas, path, fill, area, _images);
            }
        });
        foreach (var (cell, r, c) in visible)
        {
            if (cell.Text.IsEmpty)
                continue;
            var area = Area(cell, r, c);
            var frame = cell.Text;
            frame.Area = new SKRect(0, 0, area.Width, area.Height);
            Text(page, frame, area.Size, matrix.PreConcat(SKMatrix.CreateTranslation(area.Left, area.Top)));
        }
        page.Draw(canvas =>
        {
            canvas.Concat(in matrix);
            foreach (var (cell, r, c) in visible)
            {
                var area = Area(cell, r, c);
                Border(canvas, cell.Top, area.Left, area.Top, area.Right, area.Top);
                Border(canvas, cell.Bottom, area.Left, area.Bottom, area.Right, area.Bottom);
                Border(canvas, cell.Left, area.Left, area.Top, area.Left, area.Bottom);
                Border(canvas, cell.Right, area.Right, area.Top, area.Right, area.Bottom);
                Border(canvas, cell.DiagonalDown, area.Left, area.Top, area.Right, area.Bottom);
                Border(canvas, cell.DiagonalUp, area.Left, area.Bottom, area.Right, area.Top);
            }
        });
    }

    private static void Border(SKCanvas canvas, SlideLine? line, float x1, float y1, float x2, float y2)
    {
        if (line is null)
            return;
        using var path = new SKPath();
        path.MoveTo(x1, y1);
        path.LineTo(x2, y2);
        Stroke(canvas, [path], line with { Head = LineEnd.None, Tail = LineEnd.None, Cap = SKStrokeCap.Square }, path.Bounds);
    }

    /// <summary>How high a cell's text is with its insets, laid out at <paramref name="width"/>.</summary>
    private float TextHeight(SlideTextFrame frame, float width)
    {
        if (frame.IsEmpty)
            return 0;
        var inner = Math.Max(1, width - frame.InsetLeft - frame.InsetRight);
        if (frame.Direction != TextDirection.Horizontal)
            return 0;
        var height = 0f;
        var first = true;
        foreach (var paragraph in frame.Paragraphs)
        {
            var box = _layouter.Layout(paragraph, inner);
            height += (first ? 0 : box.SpaceBefore) + box.Lines.Sum(l => l.Height) + box.SpaceAfter;
            first = false;
        }
        return height + frame.InsetTop + frame.InsetBottom;
    }
}
