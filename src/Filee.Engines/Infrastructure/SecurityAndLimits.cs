// Applies Settings → Security and limits to the engines and the job queue. Shared by the app (at start-up and whenever
// the settings are saved) and the command line, so both convert under the same limits.

using Filee.Core.Conversion;
using Filee.Core.Settings;
using Filee.Engines.Magick;
using Filee.Engines.Office;

namespace Filee.Engines.Infrastructure;

/// <summary>Puts the security and limit settings into effect.</summary>
public static class SecurityAndLimits
{
    private static readonly ActiveContentScanner Scanner = new();

    /// <summary>
    /// Applies <paramref name="settings"/>: ImageMagick's limits (process-wide), files at once and the step time limit of
    /// <paramref name="queue"/>, and what it does with documents that carry active content.
    /// </summary>
    public static void Apply(AppSettings settings, JobQueue queue)
    {
        var limits = settings.Limits ?? new LimitSettings();
        MagickLimits.Apply(limits.EffectiveMaxImageMegapixels, limits.EffectiveMaxImageSide,
            limits.EffectiveMaxImageMemoryMb, limits.EffectiveMaxImageDiskMb);
        queue.ParallelFiles = limits.EffectiveParallelFiles;
        queue.StepTimeout = limits.EffectiveStepTimeout;
        queue.ActiveContentScanner ??= Scanner;
        queue.ActiveContentPolicy = (settings.Security ?? new SecuritySettings()).ActiveContent;
    }
}
