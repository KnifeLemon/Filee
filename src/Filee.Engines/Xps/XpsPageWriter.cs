// Hands a read XPS page (XpsModel) to the page model (PageContent) that the PDF writer and the picture output draw:
// a glyph run with a solid colour whose clips are all rectangles becomes a glyph item (the PDF writer subsets its
// font and maps its text), everything else a drawing item that applies the clips around it and lets XpsPainter draw
// it. A canvas that needs a layer (group opacity, an opacity mask) is drawn as one drawing item, since a layer
// can't span items. Links (FixedPage.NavigateUri) to URLs and to the document's link targets, and the targets
// themselves, become link areas and named destinations.

using Filee.Engines.Rendering;
using SkiaSharp;

namespace Filee.Engines.Xps;

internal static class XpsPageWriter
{
    /// <summary>The page <paramref name="root"/> of <paramref name="size"/> (1/96 inch) as page content in points.</summary>
    public static PageContent Write(XpsCanvas root, SKSize size, XpsDocument document)
    {
        var page = new PageContent(size.Width * XpsDocument.PointsPerUnit, size.Height * XpsDocument.PointsPerUnit);
        var toPage = SKMatrix.CreateScale(XpsDocument.PointsPerUnit, XpsDocument.PointsPerUnit);
        foreach (var child in root.Children)
            Emit(page, child, toPage, [], document);
        return page;
    }

    /// <param name="parent">The node's parent's coordinates → page points.</param>
    /// <param name="clips">Clips of the node's ancestors, in page points.</param>
    private static void Emit(PageContent page, XpsNode node, SKMatrix parent, IReadOnlyList<SKPath> clips, XpsDocument document)
    {
        Links(page, node, parent, document);
        var matrix = parent.PreConcat(node.Transform);
        switch (node)
        {
            case XpsCanvas canvas when !canvas.NeedsLayer:
                {
                    var inner = clips;
                    if (canvas.Clip is { } clip)
                        inner = [.. clips, document.Track(Mapped(clip.Fill, matrix))];
                    foreach (var child in canvas.Children)
                        Emit(page, child, matrix, inner, document);
                    return;
                }
            case XpsGlyphs glyphs when TryRun(glyphs, matrix, clips, document, out var run):
                if (run is not null)
                    page.DrawGlyphs(run);
                return;
        }
        page.Draw(canvas =>
        {
            foreach (var clip in clips)
                canvas.ClipPath(clip, SKClipOperation.Intersect, antialias: !node.Aliased);
            canvas.Concat(parent);
            XpsPainter.Paint(canvas, node);
        });
    }

    private static SKPath Mapped(SKPath path, SKMatrix matrix)
    {
        var mapped = new SKPath(path);
        mapped.Transform(matrix);
        return mapped;
    }

    /// <summary>
    /// A plain glyph run as a glyph item (<paramref name="run"/> null when nothing of it shows); false when it needs
    /// the painter (a brush other than a solid colour, a mask, a clip that isn't a rectangle).
    /// </summary>
    private static bool TryRun(XpsGlyphs glyphs, SKMatrix matrix, IReadOnlyList<SKPath> clips, XpsDocument document, out GlyphRun? run)
    {
        run = null;
        if (glyphs.Fill is not XpsSolidBrush solid || glyphs.OpacityMask is not null)
            return glyphs.Fill is null;
        SKRect? area = null;
        var own = glyphs.Clip is { } clip ? document.Track(Mapped(clip.Fill, matrix)) : null;
        foreach (var path in own is null ? clips : [.. clips, own])
        {
            if (!path.IsRect)
                return false;
            var rect = path.Bounds;
            area = area is { } before ? SKRect.Intersect(before, rect) : rect;
        }
        var color = solid.WithOpacity(glyphs.Opacity);
        if (area is { IsEmpty: true } || color.Alpha == 0)
            return true;
        run = new GlyphRun(glyphs.Font, glyphs.Size, glyphs.Glyphs, glyphs.Positions, glyphs.Text, glyphs.Clusters)
        {
            Color = color,
            Embolden = glyphs.Bold,
            SkewX = glyphs.Slant,
            Transform = matrix.PreConcat(glyphs.Frame),
            Clip = area,
        };
        return true;
    }

    // ───────────────────────── Links ─────────────────────────

    private static void Links(PageContent page, XpsNode node, SKMatrix parent, XpsDocument document)
    {
        if (node.Name is { Length: > 0 } name && document.PlaceDestination(name))
        {
            var top = Bounds(node) is { } box ? new SKPoint(box.Left, box.Top) : default;
            page.Destination(parent.MapPoint(top), name);
        }
        if (node.NavigateUri is not { Length: > 0 } uri || Bounds(node) is not { } bounds)
            return;
        var area = parent.MapRect(bounds);
        if (Uri.TryCreate(uri.Trim(), UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https" or "mailto" or "ftp")
        {
            page.LinkToUrl(area, absolute.AbsoluteUri);
            return;
        }
        var hash = uri.IndexOf('#');
        if (hash >= 0 && uri[(hash + 1)..] is { Length: > 0 } target && document.Targets.Contains(target))
            page.LinkToDestination(area, target);
    }

    /// <summary>What a node covers, in its parent's coordinates; null when it draws nothing.</summary>
    private static SKRect? Bounds(XpsNode node)
    {
        SKRect? own = node switch
        {
            XpsPath path => Union(path.Data.Fill.IsEmpty ? null : path.Data.Fill.Bounds, path.Data.Stroke.IsEmpty ? null : path.Data.Stroke.Bounds),
            XpsGlyphs glyphs when glyphs.Positions.Length > 0 => glyphs.Frame.MapRect(SKRect.Create(
                glyphs.Positions.Min(p => p.X), glyphs.Positions.Min(p => p.Y) - glyphs.Size,
                glyphs.Positions.Max(p => p.X) - glyphs.Positions.Min(p => p.X) + glyphs.Size,
                glyphs.Positions.Max(p => p.Y) - glyphs.Positions.Min(p => p.Y) + glyphs.Size * 1.25f)),
            XpsCanvas canvas => canvas.Children.Select(Bounds).Aggregate((SKRect?)null, Union),
            _ => null,
        };
        return own is { } rect ? node.Transform.MapRect(rect) : null;
    }

    private static SKRect? Union(SKRect? a, SKRect? b) =>
        a is { } first ? b is { } second ? SKRect.Union(first, second) : first : b;
}
