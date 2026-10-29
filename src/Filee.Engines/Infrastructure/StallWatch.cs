// Tells a program that is still working from one that hangs: every line it writes is a sign of life, and one that
// writes nothing for longer than the limit is treated as stuck. Engines that report their progress all the time
// (FFmpeg, calibre) may then run as long as a big file needs instead of up to a fixed time.

using System.Diagnostics;

namespace Filee.Engines.Infrastructure;

/// <summary>Watches a running program's output for a stall.</summary>
/// <param name="limit">How long the program may stay silent before it counts as stuck.</param>
internal sealed class StallWatch(TimeSpan limit)
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    private long _lastActivity = Stopwatch.GetTimestamp();

    public TimeSpan Limit { get; } = limit;

    /// <summary>Records a sign of life (a line of output); called from the output reader threads.</summary>
    public void Touch() => Volatile.Write(ref _lastActivity, Stopwatch.GetTimestamp());

    /// <summary>True once the program has written nothing for longer than <see cref="Limit"/>.</summary>
    public bool IsStalled => Stopwatch.GetElapsedTime(Volatile.Read(ref _lastActivity)) > Limit;

    /// <summary>
    /// Waits until <paramref name="exited"/> completes, checking every <paramref name="interval"/> (a second when
    /// null) whether the program stalled first.
    /// </summary>
    /// <returns>True when it exited; false when it stalled and still runs (the caller kills it).</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<bool> WaitAsync(Task exited, CancellationToken cancellationToken, TimeSpan? interval = null)
    {
        while (!exited.IsCompleted)
        {
            await Task.WhenAny(exited, Task.Delay(interval ?? DefaultInterval, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            if (!exited.IsCompleted && IsStalled)
                return false;
        }
        await exited;
        return true;
    }
}
