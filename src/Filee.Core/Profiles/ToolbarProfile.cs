// A toolbar profile decides which presets the donut shows for a given set of source files.

using Filee.Core.Formats;

namespace Filee.Core.Profiles;

/// <summary>
/// A donut toolbar layout for one family of source files (e.g. "Images" for jpg/png/...).
/// </summary>
public sealed class ToolbarProfile
{
    /// <summary>Maximum number of slices on one donut ring.</summary>
    public const int MaxSlices = 12;

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>User-defined name. When empty, <see cref="NameKey"/> is localized instead.</summary>
    public string Name { get; set; } = "";

    public string? NameKey { get; set; }

    /// <summary>Source extensions (without dot) this profile is used for.</summary>
    public List<string> Extensions { get; set; } = [];

    /// <summary>
    /// Used when dropped files match no other profile, or when they span several profiles.
    /// Exactly one profile should be the fallback.
    /// </summary>
    public bool IsFallback { get; set; }

    /// <summary>Preset ids in donut order, clockwise starting at 12 o'clock.</summary>
    public List<string> PresetIds { get; set; } = [];

    /// <summary>Returns true if the extension (with or without dot) belongs to this profile.</summary>
    public bool Matches(string extension) =>
        Extensions.Contains(extension.TrimStart('.'), StringComparer.OrdinalIgnoreCase);

    public ToolbarProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        NameKey = NameKey,
        Extensions = [.. Extensions],
        IsFallback = IsFallback,
        PresetIds = [.. PresetIds],
    };
}

/// <summary>Chooses the profile for a set of dropped files.</summary>
public static class ProfileSelector
{
    /// <summary>
    /// Picks the profile matching every file's extension. If the files match different profiles
    /// (or none), the fallback profile is returned.
    /// </summary>
    public static ToolbarProfile? Select(IReadOnlyList<ToolbarProfile> profiles, IEnumerable<string> filePaths)
    {
        var extensions = filePaths
            .Select(FormatRegistry.ExtensionOf)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (extensions.Count > 0)
        {
            var match = profiles.FirstOrDefault(p => !p.IsFallback && extensions.All(p.Matches));
            if (match is not null)
                return match;
        }

        return profiles.FirstOrDefault(p => p.IsFallback) ?? profiles.FirstOrDefault();
    }

    /// <summary>Formats of the given files that Filee recognises.</summary>
    public static IReadOnlyList<FileFormat> DetectFormats(IEnumerable<string> filePaths) =>
        filePaths.Select(FormatRegistry.Detect).OfType<FileFormat>().Distinct().ToList();
}
