// The pictures of files in the Home page's list and the merge order window. A PDF is read whole to draw its first page, so
// the pictures are drawn one at a time, a PDF above a size limit gets none, and a picture whose card is gone is cancelled.

using Avalonia.Media.Imaging;
using Filee.Core.Formats;
using Filee.Engines.Pdf;

namespace Filee.App.Services;

internal static class FilePreviews
{
    /// <summary>Width and height of the picture of a PDF's first page, in pixels (the cards show it smaller).</summary>
    private const int PdfSize = 260;

    /// <summary>The largest PDF whose first page is drawn: a bigger one is read whole, which costs too much memory.</summary>
    public const long MaxPdfBytes = 75L * 1024 * 1024;

    /// <summary>The longest one picture may take: a file that stalls does not hold up the others.</summary>
    private static readonly TimeSpan RenderLimit = TimeSpan.FromSeconds(20);

    /// <summary>One picture at a time: the pictures of a long list are drawn in turn, not all at once.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Whether <paramref name="path"/> is a PDF too large for a picture of its first page.</summary>
    public static bool IsTooLarge(string path, FileFormat? format)
    {
        if (format?.Category != FormatCategory.Pdf)
            return false;
        try
        {
            return new FileInfo(path).Length > MaxPdfBytes;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// The picture of <paramref name="path"/>, drawn in turn with the others. Null when the file has none (a format
    /// without a picture, or a PDF too large). A picture is cancelled with <paramref name="cancel"/>.
    /// </summary>
    public static async Task<Bitmap?> RenderAsync(string path, FileFormat? format, CancellationToken cancel)
    {
        if (format is null || IsTooLarge(path, format))
            return null;
        // The wait for a turn is not counted: the limit starts when this picture is drawn.
        await Gate.WaitAsync(cancel);
        var released = 0;
        void Release()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
                Gate.Release();
        }
        var draw = Task.Run(() => Make(path, format));
        try
        {
            return await draw.WaitAsync(RenderLimit, cancel);
        }
        catch (TimeoutException)
        {
            // Too slow: this file gets no picture, and the files after it are not kept waiting. The stuck draw goes on in
            // the background, so the next picture can take the gate now; what it draws is dropped.
            Release();
            Abandon(draw, format);
            return null;
        }
        catch (OperationCanceledException)
        {
            // The card went (or the page closed) while its picture was drawn: the picture is dropped when it is ready.
            Abandon(draw, format);
            throw;
        }
        finally
        {
            Release();
        }
    }

    /// <summary>A draw nobody waits for any more: its picture is disposed when it completes (a PDF's; images are shared).</summary>
    private static void Abandon(Task<Bitmap?> draw, FileFormat format) =>
        _ = draw.ContinueWith(task =>
        {
            if (task.Status == TaskStatus.RanToCompletion && Owns(format))
                task.Result?.Dispose();
        }, TaskScheduler.Default);

    /// <summary>Draws the picture now. Pictures of images are cached and shared; a PDF's picture belongs to its caller.</summary>
    private static Bitmap? Make(string path, FileFormat format)
    {
        if (format.Category == FormatCategory.Pdf)
        {
            var preview = PdfPageTools.Preview(path, 0, PdfSize);
            using var stream = new MemoryStream(preview.Png);
            return new Bitmap(stream);
        }
        return format.Category == FormatCategory.Image && HistoryThumbnails.CanShow(path)
            ? HistoryThumbnails.Load(path)
            : null;
    }

    /// <summary>Whether a picture made by <see cref="RenderAsync"/> is the caller's to dispose.</summary>
    public static bool Owns(FileFormat? format) => format?.Category == FormatCategory.Pdf;
}
