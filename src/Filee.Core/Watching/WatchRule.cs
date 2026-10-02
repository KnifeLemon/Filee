// A watch folder: files that land in the folder are converted with a preset (Settings → Watch folders, or
// "filee watch" on the command line).

namespace Filee.Core.Watching;

/// <summary>What happens to a source file once it was converted.</summary>
public enum AfterConversion
{
    /// <summary>The source stays where it is.</summary>
    Keep,

    /// <summary>The source moves into <see cref="WatchRule.OriginalsFolder"/>, so the watch folder works like an inbox.</summary>
    MoveToOriginals,
}

/// <summary>One watched folder and how its files are converted.</summary>
public sealed class WatchRule
{
    /// <summary>Name of the default output folder inside the watched folder.</summary>
    public const string DefaultOutputName = "converted";

    /// <summary>Name of the folder inside the watched folder that receives converted sources (<see cref="AfterConversion.MoveToOriginals"/>).</summary>
    public const string OriginalsName = "originals";

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public bool Enabled { get; set; } = true;

    public string Folder { get; set; } = "";

    /// <summary>Preset to convert with (<see cref="Presets.Preset.Id"/>).</summary>
    public string PresetId { get; set; } = "";

    public bool IncludeSubfolders { get; set; }

    /// <summary>Where converted files go; empty means <c>converted</c> inside <see cref="Folder"/>.</summary>
    public string OutputFolder { get; set; } = "";

    public AfterConversion Originals { get; set; } = AfterConversion.Keep;

    /// <summary>The output folder actually used.</summary>
    public string ResolvedOutputFolder =>
        string.IsNullOrWhiteSpace(OutputFolder) ? Path.Combine(Folder, DefaultOutputName) : OutputFolder;

    /// <summary>Where converted sources go with <see cref="AfterConversion.MoveToOriginals"/>.</summary>
    public string OriginalsFolder => Path.Combine(Folder, OriginalsName);

    public WatchRule Clone() => (WatchRule)MemberwiseClone();
}
