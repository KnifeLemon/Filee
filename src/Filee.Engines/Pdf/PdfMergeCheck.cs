// #377: a merged PDF whose pages came out blank although their sources aren't. Every page of the merged file is drawn
// small with PDFium (fast: a few milliseconds each); only a blank one is compared with its source page. Whatever made
// it blank (resources, optional content or form fields the page import didn't carry over), the check sees the result.

using PDFtoImage;
using SkiaSharp;

#pragma warning disable CA1416 // PDFium ships for every desktop platform Filee targets.

namespace Filee.Engines.Pdf;

/// <summary>Finds pages a merge lost.</summary>
internal static class PdfMergeCheck
{
    /// <summary>Resolution of the check: enough to tell a blank page from one with text on it.</summary>
    private const int Dpi = 18;

    /// <summary>
    /// The index of the first page of <paramref name="merged"/> that is blank while its page in
    /// <paramref name="sources"/> (in order) is not, or null when there is none or the files can't be drawn.
    /// </summary>
    public static int? FindLostPage(IReadOnlyList<string> sources, string merged, CancellationToken cancellationToken)
    {
        try
        {
            var options = new RenderOptions(Dpi: Dpi, WithAnnotations: true, WithFormFill: true, BackgroundColor: SKColors.White);
            var mergedBytes = File.ReadAllBytes(merged);
            var blank = new List<int>();
            var index = 0;
            foreach (var bitmap in Conversion.ToImages(mergedBytes, null, options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (bitmap)
                {
                    if (IsBlank(bitmap))
                        blank.Add(index);
                }
                index++;
            }
            if (blank.Count == 0)
                return null;

            // Which source page each merged page came from.
            var origin = new List<(string File, int Page)>();
            foreach (var source in sources)
            {
                var count = Conversion.GetPageCount(File.ReadAllBytes(source), null);
                for (var page = 0; page < count; page++)
                    origin.Add((source, page));
            }
            foreach (var page in blank.Where(p => p < origin.Count))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var original = Conversion.ToImage(File.ReadAllBytes(origin[page].File), origin[page].Page, null, options);
                if (!IsBlank(original))
                    return page;
            }
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            // A file PDFium can't draw (encrypted, damaged): no verdict, the merge stays as it is.
            return null;
        }
    }

    /// <summary>True when every pixel is (nearly) white.</summary>
    internal static bool IsBlank(SKBitmap bitmap)
    {
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red < 245 || pixel.Green < 245 || pixel.Blue < 245)
                    return false;
            }
        }
        return true;
    }
}
