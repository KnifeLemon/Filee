namespace Filee.App.Services;

/// <summary>The label in the middle of the tick progress dial: the source format and the preset, "MP4 → WEBP".</summary>
internal static class ProgressCaptions
{
    /// <summary>
    /// "FORMAT → PRESET" in capitals. The format is left out when the sources are of different kinds, or are folders or
    /// have no extension; the preset alone is shown then. Null when there is no preset name.
    /// </summary>
    public static string? For(IReadOnlyList<string> sources, string? presetName)
    {
        var preset = presetName?.Trim();
        if (string.IsNullOrEmpty(preset))
            return null;

        var kinds = sources
            .Select(path => Path.GetExtension(path).TrimStart('.'))
            .Where(extension => extension.Length > 0)
            .Select(extension => extension.ToUpperInvariant())
            .Distinct()
            .ToList();
        return kinds.Count == 1 ? $"{kinds[0]} → {preset.ToUpperInvariant()}" : preset.ToUpperInvariant();
    }
}
