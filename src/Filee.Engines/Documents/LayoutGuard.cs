// Guards for the layout loop, so a document can't keep the layout engine busy forever: every paragraph checks the
// cancellation token and the time budget, every page counts against MaxPages, and pages in a row that receive nothing
// mean the layout is stuck. An object larger than every page is clipped to the area it goes into and placed, so it
// can't push itself from page to page without end.

using System.Diagnostics;

namespace Filee.Engines.Documents;

/// <summary>Checks a layout run against <see cref="DocumentLimits"/>.</summary>
internal sealed class LayoutGuard
{
    private readonly DocumentLimits _limits;
    private readonly CancellationToken _cancellationToken;
    private readonly Func<TimeSpan> _elapsed;
    private int _pages;
    private int _emptyInARow;

    /// <param name="elapsed">Time since the layout started (tests pass their own clock).</param>
    public LayoutGuard(DocumentLimits limits, CancellationToken cancellationToken, Func<TimeSpan>? elapsed = null)
    {
        _limits = limits;
        _cancellationToken = cancellationToken;
        if (elapsed is null)
        {
            var start = Stopwatch.GetTimestamp();
            elapsed = () => Stopwatch.GetElapsedTime(start);
        }
        _elapsed = elapsed;
    }

    /// <summary>Pages finished so far.</summary>
    public int Pages => _pages;

    /// <summary>Called before each paragraph is laid out: throws when the job was cancelled or the time is up.</summary>
    /// <exception cref="OperationCanceledException">The job was cancelled.</exception>
    public void Paragraph()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (_elapsed() > _limits.LayoutTimeBudget)
            throw DocumentLimits.Exceeded($"Laying out the document took longer than {Describe(_limits.LayoutTimeBudget)}.");
    }

    private static string Describe(TimeSpan time) => time.TotalMinutes >= 1
        ? time.TotalMinutes == 1 ? "1 minute" : $"{time.TotalMinutes:0} minutes"
        : time.TotalSeconds == 1 ? "1 second" : $"{time.TotalSeconds:0} seconds";

    /// <summary>Called when a page is finished; <paramref name="empty"/> when nothing was placed on it.</summary>
    public void PageFinished(bool empty)
    {
        if (++_pages > _limits.MaxPages)
            throw DocumentLimits.TooLarge($"The document has more than {DocumentLimits.Count(_limits.MaxPages)} pages.");
        _emptyInARow = empty ? _emptyInARow + 1 : 0;
        if (_emptyInARow > _limits.MaxConsecutiveEmptyPages)
            throw DocumentLimits.Exceeded($"The layout made more than {_limits.MaxConsecutiveEmptyPages} empty pages in a row.");
    }

    /// <summary>
    /// The size an object is placed with in an area of <paramref name="areaWidth"/> × <paramref name="areaHeight"/>
    /// (the body of an empty page): its own size when it fits, else clipped to the area. <c>Clipped</c> tells the
    /// layout to draw only that part rather than move the object to the next page again. Sizes that are not finite
    /// numbers or negative count as 0.
    /// </summary>
    public static (double Width, double Height, bool Clipped) Fit(double width, double height, double areaWidth, double areaHeight)
    {
        static double Clean(double value) => double.IsFinite(value) && value > 0 ? value : 0;
        (width, height, areaWidth, areaHeight) = (Clean(width), Clean(height), Clean(areaWidth), Clean(areaHeight));
        var clipped = width > areaWidth || height > areaHeight;
        return (Math.Min(width, areaWidth), Math.Min(height, areaHeight), clipped);
    }
}
