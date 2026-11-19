// A chart drawn into a picture, for what takes pictures rather than drawings: the document writers (a Word chart
// becomes a picture in HWPX, ODT, EPUB, ...) and the ODP writer.

using Filee.Engines.Rendering;
using SkiaSharp;

namespace Filee.Engines.Charts;

internal static class ChartPicture
{
    /// <summary>Pixels of the longer side at most.</summary>
    private const int MaxSide = 4000;

    /// <summary>The chart drawn <paramref name="width"/> × <paramref name="height"/> points at <paramref name="dpi"/>, as PNG; null for an empty box.</summary>
    public static byte[]? Png(Chart chart, float width, float height, float dpi = 192)
    {
        if (!(width >= 1 && height >= 1) || !float.IsFinite(width) || !float.IsFinite(height))
            return null;
        dpi = Math.Min(dpi, MaxSide * 72f / Math.Max(width, height));
        var page = new PageContent(width, height);
        using (var fonts = new ChartFonts())
            ChartDrawer.Draw(page, chart, new SKRect(0, 0, width, height), fonts);
        using var bitmap = page.Render(dpi);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray();
    }
}
