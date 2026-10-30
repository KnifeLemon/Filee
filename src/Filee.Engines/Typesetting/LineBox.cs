// The output of paragraph layout: a paragraph's lines, each with its glyph runs, decorations, links and bookmarks
// placed relative to the line (x from the left edge of the column, y from the baseline, downwards). The page
// composer stacks lines and draws each at its place on a page.

using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Rendering;
using SkiaSharp;

namespace Filee.Engines.Typesetting;

/// <summary>A laid out paragraph.</summary>
/// <param name="SpaceBefore">Space above the first line, in points.</param>
/// <param name="SpaceAfter">Space below the last line, in points.</param>
internal sealed record ParagraphBox(float SpaceBefore, float SpaceAfter, IReadOnlyList<LineBox> Lines)
{
    /// <summary>The lines follow 한글's stored line layout.</summary>
    public bool StoredLines { get; init; }

    /// <summary>Why the paragraph's stored lines weren't used, or null (used, or there were none).</summary>
    public string? StoredLinesIgnored { get; init; }

    /// <summary>The list item's number or bullet the paragraph was laid out with, or null.</summary>
    public string? Marker { get; init; }

    /// <summary>The paragraph's whole height with its spacing.</summary>
    public float Height => SpaceBefore + Lines.Sum(l => l.Height) + SpaceAfter;
}

/// <summary>
/// Glyphs placed on a line: <see cref="Positions"/> are relative to the line's origin (column edge, baseline);
/// <see cref="Clusters"/> are offsets into <see cref="Text"/> as in <see cref="GlyphRun"/>.
/// </summary>
internal sealed record PlacedRun(PageFont Font, float Size, ushort[] Glyphs, SKPoint[] Positions, string Text, int[] Clusters)
{
    public SKColor Color { get; init; } = SKColors.Black;

    public float ScaleX { get; init; } = 1;

    public bool Embolden { get; init; }

    public bool Slant { get; init; }

    public GlyphRun At(float x, float y) => new(Font, Size, Glyphs, [.. Positions.Select(p => new SKPoint(p.X + x, p.Y + y))], Text, Clusters)
    {
        Color = Color,
        ScaleX = ScaleX,
        Embolden = Embolden,
        SkewX = Slant ? GlyphRun.SyntheticSlant : 0,
    };
}

internal enum DecorationKind
{
    /// <summary>A filled rectangle behind the text (shade, highlight).</summary>
    Fill,

    /// <summary>A line (underline, strike-through).</summary>
    Line,

    /// <summary>A dotted line (tab leader).</summary>
    Dotted,

    /// <summary>A dashed line (tab leader).</summary>
    Dashed,
}

/// <summary>
/// A rectangle or line on a line, relative to the line's origin: <c>Top</c> and <c>Bottom</c> bound a fill, a line runs
/// at <c>Top</c> with thickness <c>Bottom - Top</c>.
/// </summary>
internal readonly record struct Decoration(DecorationKind Kind, float Left, float Right, float Top, float Bottom, SKColor Color);

/// <summary>A link area on a line: from <c>Left</c> to <c>Right</c>, the line's height.</summary>
internal readonly record struct LineLink(float Left, float Right, string Target);

/// <summary>
/// An object laid out like a character (a picture, shape or text box): <c>Area</c> relative to the line's origin;
/// <c>Stack</c> lays out a text box's content.
/// </summary>
internal sealed record LineObject(SKRect Area, HInline Object, Func<IReadOnlyList<HBlock>, float, List<Piece>> Stack);

/// <summary>One line of a paragraph.</summary>
internal sealed class LineBox
{
    /// <summary>First character of the line in the paragraph's text.</summary>
    public required int Start { get; init; }

    /// <summary>End of the line's characters (its trailing spaces and line break included).</summary>
    public required int End { get; init; }

    /// <summary>Distance from this line's top to the next line's top, in points (the line pitch).</summary>
    public required float Height { get; init; }

    /// <summary>Distance from the line's top to its baseline.</summary>
    public required float Baseline { get; init; }

    /// <summary>Height of the text itself (ascent and descent).</summary>
    public required float TextHeight { get; init; }

    /// <summary>The line spacing below the text: the part of <see cref="Height"/> after it.</summary>
    public float Below { get; init; }

    /// <summary>Left edge of the line's content from the column edge.</summary>
    public required float Left { get; init; }

    /// <summary>Width of the line's content without its trailing spaces.</summary>
    public required float Width { get; init; }

    public IReadOnlyList<PlacedRun> Runs { get; init; } = [];

    public IReadOnlyList<Decoration> Decorations { get; init; } = [];

    public IReadOnlyList<LineLink> Links { get; init; } = [];

    public IReadOnlyList<(float X, string Name)> Bookmarks { get; init; } = [];

    public IReadOnlyList<LineObject> Objects { get; init; } = [];

    /// <summary>The notes whose references are on the line, in order.</summary>
    public IReadOnlyList<HNote> Notes { get; init; } = [];

    /// <summary>Where 한글 put the line's top on its page (from the body's top), for a line on its stored layout.</summary>
    public float? StoredTop { get; init; }

    /// <summary>한글 started a page with this line (a line on its stored layout).</summary>
    public bool StoredPageStart { get; init; }

    /// <summary>한글 started a column with this line (a line on its stored layout).</summary>
    public bool StoredColumnStart { get; init; }

    /// <summary>Draws the line with its top-left corner (column edge, line top) at (<paramref name="x"/>, <paramref name="top"/>).</summary>
    public void Draw(PageContent page, float x, float top)
    {
        var baseline = top + Baseline;
        var fills = Decorations.Where(d => d.Kind == DecorationKind.Fill).ToList();
        if (fills.Count > 0)
            page.Draw(canvas => DrawDecorations(canvas, fills, x, baseline));
        foreach (var item in Objects)
        {
            var area = item.Area;
            area.Offset(x, baseline);
            FloatingObjects.Draw(page, item.Object, area, item.Stack);
        }
        foreach (var run in Runs)
            page.DrawGlyphs(run.At(x, baseline));
        var lines = Decorations.Where(d => d.Kind != DecorationKind.Fill).ToList();
        if (lines.Count > 0)
            page.Draw(canvas => DrawDecorations(canvas, lines, x, baseline));
        foreach (var link in Links)
        {
            var area = new SKRect(x + link.Left, top, x + link.Right, top + Height);
            if (link.Target.StartsWith('#'))
                page.LinkToDestination(area, link.Target[1..]);
            else
                page.LinkToUrl(area, link.Target);
        }
        foreach (var (position, name) in Bookmarks)
            page.Destination(new SKPoint(x + position, top), name);
    }

    private static void DrawDecorations(SKCanvas canvas, List<Decoration> decorations, float x, float y)
    {
        using var paint = new SKPaint { IsAntialias = true };
        foreach (var d in decorations)
        {
            paint.Color = d.Color;
            if (d.Kind == DecorationKind.Fill)
            {
                paint.Style = SKPaintStyle.Fill;
                canvas.DrawRect(new SKRect(x + d.Left, y + d.Top, x + d.Right, y + d.Bottom), paint);
                continue;
            }
            var thickness = Math.Max(0.25f, d.Bottom - d.Top);
            float[]? dashes = d.Kind switch
            {
                DecorationKind.Dotted => [thickness, thickness * 2],
                DecorationKind.Dashed => [thickness * 4, thickness * 2],
                _ => null,
            };
            using var effect = dashes is null ? null : SKPathEffect.CreateDash(dashes, 0);
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = thickness;
            paint.PathEffect = effect;
            var lineY = y + d.Top + thickness / 2;
            canvas.DrawLine(x + d.Left, lineY, x + d.Right, lineY, paint);
            paint.PathEffect = null;
        }
    }
}
