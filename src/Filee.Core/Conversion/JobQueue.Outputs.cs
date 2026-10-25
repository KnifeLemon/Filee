// JobQueue: output paths. Reserves names while a conversion holds them, puts results next to the source
// and replaces the original when the preset asks for it.

using System.Collections.Concurrent;
using System.Threading.Channels;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.Core.Conversion;

public sealed partial class JobQueue
{
    /// <param name="oneFile">The output is one file whatever happens (a merged PDF, a multi-page TIFF, an archive).</param>
    private FinalAllocator CreateFinalAllocator(ConversionJob job, string sourcePath, int index, bool oneFile = false) =>
        new(this, oneFile ? ForOneFile(job.Preset.Output) : job.Preset.Output,
            new OutputPathResolver.Tokens(sourcePath, job.PresetDisplayName, index, job.CreatedAt));

    /// <summary>
    /// The rule for a single output file: with <see cref="OutputRule.SubfolderOnlyForSeveralFiles"/> it is saved next to
    /// the source instead of in the subfolder.
    /// </summary>
    private static OutputRule ForOneFile(OutputRule rule)
    {
        if (!rule.SubfolderOnlyForSeveralFiles || rule.Location != OutputLocation.Subfolder)
            return rule;
        var single = rule.Clone();
        single.Location = OutputLocation.SameFolder;
        return single;
    }

    /// <summary>
    /// With <see cref="OutputRule.SubfolderOnlyForSeveralFiles"/>: moves the one file a source became out of the
    /// subfolder to the folder next to the source, keeping its name (the conflict policy applies there), and removes
    /// the subfolder if that left it empty. A file that can't be moved (the policy skips an existing file, or the file
    /// is in use) stays in the subfolder.
    /// </summary>
    private string NextToSource(ConversionJob job, FileResult file, int index, string output, FinalAllocator allocator)
    {
        var rule = ForOneFile(job.Preset.Output);
        if (ReferenceEquals(rule, job.Preset.Output) || !File.Exists(output)
            // Pandoc writes a document's pictures into "<name>_files" beside it, outside the allocator: they stay together.
            || Directory.Exists(Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "_files")))
            return output;
        // The name was already made from the pattern and renames: it is kept as it is.
        rule.FileNamePattern = Path.GetFileNameWithoutExtension(output);
        rule.Renames = [];

        lock (_reservedOutputs)
        {
            var tokens = new OutputPathResolver.Tokens(file.SourcePath, job.PresetDisplayName, index, job.CreatedAt);
            var target = OutputPathResolver.Resolve(rule, tokens, Path.GetExtension(output),
                p => !PathsEqual(p, output) && (File.Exists(p) || Directory.Exists(p) || _reservedOutputs.ContainsKey(p)));
            if (target is null || PathsEqual(target, output))
                return output;
            try
            {
                // Over an existing file only when the policy says so: one that appeared since is never replaced.
                File.Move(output, target, overwrite: rule.Conflict == ConflictPolicy.Overwrite);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Could not move {Output} next to its source", output);
                return output;
            }
            allocator.Moved(output, target);
            // Another file of this or a running job may still be written into the same subfolder: RemoveIfEmpty waits.
            RemoveIfEmpty(Path.GetDirectoryName(output)!);
            return target;
        }

        static bool PathsEqual(string a, string b) =>
            string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), Platform.FileSystemPaths.Comparison);
    }

    /// <summary>How many conversions hold an output path, and whether more than one ever did.</summary>
    private sealed class Reservation
    {
        public int Holders = 1;

        /// <summary>
        /// Handed to a second conversion too: "Overwrite" gives two files that become the same name the same path. Then
        /// neither may delete it when it fails: the other one's finished file may be there.
        /// </summary>
        public bool Shared;
    }

    /// <summary>Takes <paramref name="path"/> (under the lock); true if nobody held it.</summary>
    private bool Reserve(string path)
    {
        if (_reservedOutputs.TryGetValue(path, out var reservation))
        {
            reservation.Holders++;
            reservation.Shared = true;
            return false;
        }
        _reservedOutputs[path] = new Reservation();
        return true;
    }

    /// <summary>
    /// "Replace the original" (<see cref="OutputRule.ReplaceOriginal"/>): the finished <paramref name="output"/> takes the
    /// source's place and the source goes to the Recycle Bin / Trash. The path of the result is returned. The result is
    /// first moved next to the source under a temporary name (a copy when it was saved on another drive), so a failure
    /// there leaves the source alone; then the source goes to the Trash; then the result is renamed within the folder.
    /// Anything that stops it keeps the source, with a note saying why.
    /// </summary>
    /// <param name="copiedSource">"Only if smaller" copied the source as the result: nothing changed, so the source stays
    /// where it is and the copy goes.</param>
    private string ReplaceOriginal(ConversionJob job, FileResult file, string sourceFormat, string output, bool copiedSource)
    {
        var source = file.SourcePath;
        if (!job.Preset.Output.ReplaceOriginal || job.Fetcher is not null || !File.Exists(output)
            || FormatRegistry.Detect(output) is not { } format || format.Id != sourceFormat
            || string.Equals(Path.GetFullPath(output), Path.GetFullPath(source), Platform.FileSystemPaths.Comparison))
            return output;
        // One format can cover different kinds of file (TXT: .bat, .py, .log; PPT: .pot; AIFF: .aifc), and the result
        // takes the original's name: only the same extension, or another spelling of it (.jpeg / .jpg), replaces.
        if (!SameKindOfFile(Path.GetExtension(output), Path.GetExtension(source)))
            return output;
        if (copiedSource)
        {
            try
            {
                File.Delete(output);
                file.NoticeKey = "notice.original_unchanged";
                return source;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return output;
            }
        }
        if (_moveToTrash is null)
            return Kept(new Platform.TrashRefusal("trash.no_recycle_bin"), output);

        var staged = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!,
            $".{Path.GetFileName(source)}.{Guid.NewGuid().ToString("N")[..8]}.filee-tmp");
        try
        {
            File.Move(output, staged);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not move {Output} next to {Source}", output, source);
            return Kept(new Platform.TrashRefusal("trash.cannot_place", ex.Message), output);
        }

        if (_moveToTrash(source) is { } refusal)
        {
            // The result goes back to where it was saved.
            try
            {
                File.Move(staged, output);
                return Kept(refusal, output);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Could not move {Staged} back to {Output}", staged, output);
                return Kept(refusal, staged);
            }
        }
        try
        {
            File.Move(staged, source);
            return source;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The original is in the Trash already: the result goes back to the name it was saved under.
            _log.LogWarning(ex, "Could not rename {Staged} to {Source}", staged, source);
            file.NoticeKey = "notice.original_in_trash";
            file.NoticeReasonKey = "trash.cannot_place";
            file.NoticeDetail = ex.Message;
            try
            {
                File.Move(staged, output);
                return output;
            }
            catch (Exception again) when (again is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(again, "Could not move {Staged} back to {Output}", staged, output);
                return staged;
            }
        }

        string Kept(Platform.TrashRefusal why, string result)
        {
            _log.LogInformation("Kept the original {Source}: {Reason} {Detail}", source, why.ReasonKey, why.Detail);
            file.NoticeKey = "notice.original_kept";
            file.NoticeReasonKey = why.ReasonKey;
            file.NoticeDetail = why.Detail;
            return result;
        }
    }

    /// <summary>Extensions that only spell the same kind of file differently ("Replace the original" may swap them).</summary>
    private static readonly string[][] Spellings =
    [
        [".jpg", ".jpeg", ".jpe", ".jfif"], [".tiff", ".tif"], [".html", ".htm"], [".md", ".markdown"], [".yaml", ".yml"],
        [".mpg", ".mpeg"], [".wav", ".wave"], [".aiff", ".aif"], [".ogg", ".oga"], [".3gp", ".3gpp"], [".m2ts", ".mts"],
        [".mov", ".qt"], [".tex", ".latex"],
    ];

    private static bool SameKindOfFile(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
        || Spellings.Any(s => s.Contains(a, StringComparer.OrdinalIgnoreCase) && s.Contains(b, StringComparer.OrdinalIgnoreCase));

    /// <summary>Lets go of <paramref name="path"/> (under the lock): free once nobody holds it.</summary>
    private void Unreserve(string path)
    {
        if (_reservedOutputs.TryGetValue(path, out var reservation) && --reservation.Holders <= 0)
            _reservedOutputs.Remove(path);
    }

    private bool HasReservationIn(string folder) =>
        _reservedOutputs.Keys.Any(p => Platform.FileSystemPaths.Comparer.Equals(Path.GetDirectoryName(Path.GetFullPath(p)), folder));

    /// <summary>
    /// Removes a folder this queue made for outputs once nothing is in it and no running conversion is to write into it
    /// (under the lock). Never a folder that was there before, and never one with anything inside.
    /// </summary>
    private void RemoveIfEmpty(string folder)
    {
        folder = Path.GetFullPath(folder);
        if (!_madeFolders.Contains(folder) || HasReservationIn(folder))
            return;
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
            _madeFolders.Remove(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
