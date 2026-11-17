// This process's memory as Linux counts it, from /proc/self/smaps_rollup (issue #957). Process.PrivateMemorySize64 won't
// do here: it reports VmData, which includes address space never touched. Parsing is pure, so it can be tested anywhere.

namespace Filee.Platform.Linux;

/// <summary>
/// Resident pages; those no other process maps (what smem calls USS); and those not backed by a file (managed heap,
/// malloc, stacks, code the JIT made).
/// </summary>
public readonly record struct ProcessMemoryUsage(long ResidentBytes, long OwnBytes, long AnonymousBytes)
{
    private const string RollupFile = "/proc/self/smaps_rollup";

    /// <summary>This process's memory right now; null when the kernel doesn't offer it (before Linux 4.14) or off Linux.</summary>
    public static ProcessMemoryUsage? Current()
    {
        if (!OperatingSystem.IsLinux())
            return null;
        try
        {
            return Parse(File.ReadAllText(RollupFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Reads the "Rss:   1234 kB" lines of a smaps_rollup file; null when it has no Rss.</summary>
    internal static ProcessMemoryUsage? Parse(string rollup)
    {
        long? resident = null;
        long privateClean = 0, privateDirty = 0, anonymous = 0;
        foreach (var line in rollup.Split('\n', StringSplitOptions.TrimEntries))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            var parts = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || parts[1] != "kB" || !long.TryParse(parts[0], out var kilobytes))
                continue;
            var bytes = kilobytes * 1024;
            switch (line[..colon])
            {
                case "Rss": resident = bytes; break;
                case "Private_Clean": privateClean = bytes; break;
                case "Private_Dirty": privateDirty = bytes; break;
                case "Anonymous": anonymous = bytes; break;
            }
        }
        return resident is { } rss ? new(rss, privateClean + privateDirty, anonymous) : null;
    }
}
