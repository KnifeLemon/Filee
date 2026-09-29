// A finished conversion job as shown on the Home page ("Recent conversions").

using Filee.Core.Conversion;

namespace Filee.Core.History;

/// <summary>Persisted summary of a finished job.</summary>
public sealed class HistoryEntry
{
    public string JobId { get; set; } = "";
    public DateTime FinishedAt { get; set; }
    public string PresetId { get; set; } = "";
    public string PresetName { get; set; } = "";
    public JobState State { get; set; }
    public List<string> Sources { get; set; } = [];
    public List<string> Outputs { get; set; } = [];

    /// <summary>"source: error" lines for failed files.</summary>
    public List<string> Errors { get; set; } = [];

    public static HistoryEntry From(ConversionJob job) => new()
    {
        JobId = job.Id,
        FinishedAt = DateTime.Now,
        PresetId = job.Preset.Id,
        PresetName = job.PresetDisplayName,
        State = job.State,
        Sources = [.. job.Sources],
        Outputs = [.. job.Outputs],
        Errors = job.Files
            .Where(f => f.State == FileState.Failed)
            .Select(f => $"{Path.GetFileName(f.SourcePath)}: {f.ErrorKey} {f.ErrorDetail}".Trim())
            .ToList(),
    };
}
