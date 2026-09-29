// Output location / file naming / conflict handling for a preset.

namespace Filee.Core.Presets;

/// <summary>Where converted files go.</summary>
public enum OutputLocation
{
    /// <summary>Next to the source file.</summary>
    SameFolder,
    /// <summary>In a sub folder (see <see cref="OutputRule.SubfolderName"/>) next to the source file.</summary>
    Subfolder,
    /// <summary>In a fixed folder (see <see cref="OutputRule.CustomFolder"/>).</summary>
    CustomFolder,
}

/// <summary>What to do when the output file already exists.</summary>
public enum ConflictPolicy
{
    /// <summary>Append " (2)", " (3)" ... to the file name.</summary>
    Rename,
    /// <summary>Replace the existing file (never the source file itself).</summary>
    Overwrite,
    /// <summary>Leave the existing file alone and skip this conversion.</summary>
    Skip,
}

/// <summary>Output placement and naming rules.</summary>
public sealed class OutputRule
{
    public OutputLocation Location { get; set; } = OutputLocation.SameFolder;

    public string SubfolderName { get; set; } = "converted";

    public string CustomFolder { get; set; } = "";

    /// <summary>
    /// File name pattern without extension. Tokens:
    /// <c>{name}</c> source name, <c>{ext}</c> source extension, <c>{preset}</c> preset name,
    /// <c>{date}</c> yyyyMMdd, <c>{time}</c> HHmmss, <c>{index}</c> 1-based position in the batch.
    /// </summary>
    public string FileNamePattern { get; set; } = "{name}";

    public ConflictPolicy Conflict { get; set; } = ConflictPolicy.Rename;

    public OutputRule Clone() => (OutputRule)MemberwiseClone();
}
