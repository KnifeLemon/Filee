// A finished conversion job as shown on the Home page ("Recent conversions").

using Filee.Core.Conversion;
using Filee.Core.Presets;

namespace Filee.Core.History;

/// <summary>Persisted summary of a finished job.</summary>
public sealed class HistoryEntry
{
    public string JobId { get; set; } = "";
    public DateTime FinishedAt { get; set; }
    public string PresetId { get; set; } = "";
    public string PresetName { get; set; } = "";

    /// <summary>
    /// The built-in preset's name key when the job showed its translated name, so the entry follows the language Filee
    /// is in now (#744); null for the user's own names and in entries from before.
    /// </summary>
    public string? PresetNameKey { get; set; }
    public JobState State { get; set; }
    public List<string> Sources { get; set; } = [];
    public List<string> Outputs { get; set; } = [];

    /// <summary>The watch folder (<see cref="Watching.WatchRule.Id"/>) the job came from, or null.</summary>
    public string? WatchRuleId { get; set; }

    /// <summary>Files that failed, with the reason.</summary>
    public List<HistoryFailure> Failures { get; set; } = [];

    /// <summary>
    /// The preset as the job ran it (save location already decided, a watch folder's output folder included), kept
    /// when files failed: they can be converted again the same way even after the preset was changed or deleted. Its PDF
    /// password is left out (<see cref="PdfOptions.PasswordLeftOut"/>).
    /// </summary>
    public Preset? Preset { get; set; }

    /// <summary>"file: error" lines written by Filee 1.7 and earlier; read for entries without <see cref="Failures"/>.</summary>
    public List<string> Errors { get; set; } = [];

    /// <summary>
    /// The results Undo sends to the Recycle Bin / Trash, as they were when the job finished: Undo is offered only while
    /// each one is still there unchanged (#553). Empty when there is nothing to undo, and in entries from before Undo.
    /// </summary>
    public List<HistoryOutput> Undoable { get; set; } = [];

    /// <summary>"Replace the original" sent a source to the Recycle Bin / Trash; Undo says it is still there.</summary>
    public bool OriginalsInTrash { get; set; }

    /// <summary>Undo moved the results to the Recycle Bin / Trash.</summary>
    public bool Undone { get; set; }

    public static HistoryEntry From(ConversionJob job)
    {
        var failures = job.Files
            .Where(f => f.State == FileState.Failed)
            .Select(f => new HistoryFailure { Source = f.SourcePath, ErrorKey = f.ErrorKey, ErrorDetail = f.ErrorDetail })
            .ToList();
        return new HistoryEntry
        {
            JobId = job.Id,
            FinishedAt = DateTime.Now,
            PresetId = job.Preset.Id,
            PresetName = job.PresetDisplayName,
            State = job.State,
            Sources = [.. job.Sources],
            Outputs = [.. job.Outputs],
            Failures = failures,
            // history.json isn't the place for a PDF password: a retry takes it from the preset again.
            // No Retry for parts of a picture to hide: a Filee from before them would write the picture as it is.
            Preset = failures.Count > 0 && !job.Preset.Image.HidesAreas ? job.Preset.Clone().WithoutPassword(mark: true) : null,
            Undoable = UndoableOutputs(job),
            OriginalsInTrash = job.Files.Any(f => f.OriginalInTrash),
        };
    }

    /// <summary>
    /// More results than this ("Save every frame", a long PDF split into pages) get no Undo, so history.json stays
    /// small and an Undo never takes minutes.
    /// </summary>
    public const int MaxUndoable = 500;

    private static List<HistoryOutput> UndoableOutputs(ConversionJob job)
    {
        var outputs = new List<HistoryOutput>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var file in job.Files)
        {
            foreach (var output in file.Outputs)
            {
                // A source left as it was ("Only if smaller" with "Replace the original") is the user's file, not a result.
                if (!file.OriginalInTrash && string.Equals(Path.GetFullPath(output), Path.GetFullPath(file.SourcePath), Platform.FileSystemPaths.Comparison))
                    continue;
                // An output folder (Extract), or a document whose pictures Pandoc wrote into "<name>_files" beside it
                // (not one of the outputs): Undo only moves files, so the whole job gets none rather than half of it.
                if (!File.Exists(output)
                    || Directory.Exists(Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "_files")))
                    return [];
                if (!seen.Add(output))
                    continue;
                if (outputs.Count == MaxUndoable)
                    return [];
                var info = new FileInfo(output);
                outputs.Add(new HistoryOutput { Path = output, Length = info.Length, WrittenUtc = info.LastWriteTimeUtc });
            }
        }
        return outputs;
    }
}

/// <summary>A result as the job left it, so Undo never moves a file that was changed or replaced since.</summary>
public sealed class HistoryOutput
{
    public string Path { get; set; } = "";
    public long Length { get; set; }
    public DateTime WrittenUtc { get; set; }

    /// <summary>Undo moved it to the Recycle Bin / Trash, also when a later result was refused there.</summary>
    public bool Trashed { get; set; }

    /// <summary>Still there, the same size and last written at the same time.</summary>
    public bool Unchanged()
    {
        var info = new FileInfo(Path);
        return info.Exists && info.Length == Length && info.LastWriteTimeUtc == WrittenUtc;
    }
}

/// <summary>A file a job couldn't convert.</summary>
public sealed class HistoryFailure
{
    /// <summary>Full path of the source file.</summary>
    public string Source { get; set; } = "";

    /// <summary>Localization key of the reason (e.g. <c>error.no_route</c>).</summary>
    public string? ErrorKey { get; set; }

    /// <summary>Detail of the reason (an engine's message, the formats of a missing route).</summary>
    public string? ErrorDetail { get; set; }
}
