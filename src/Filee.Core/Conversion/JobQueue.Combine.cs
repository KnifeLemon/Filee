// JobQueue: jobs that turn several files into one result: merged PDF, multi-page TIFF, joined media,
// a combined archive, and PDF finishing on the result.

using System.Collections.Concurrent;
using System.Threading.Channels;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.Core.Conversion;

public sealed partial class JobQueue
{
    /// <summary>Applies the preset's PDF finishing (<see cref="PdfOptions.FinishesPdf"/>) to the PDFs among <paramref name="outputs"/>.</summary>
    private async Task FinishPdfsAsync(Preset preset, IReadOnlyList<string> outputs, CancellationToken ct)
    {
        // A retry from history whose preset (and so its password) is gone: no PDF without the password it was meant to have.
        if (preset.Pdf.PasswordLeftOut && preset.Pdf.Password.Length == 0)
            throw new ConversionRefusedException("error.pdf_password_gone");
        if (!preset.Pdf.FinishesPdf || _catalog.All.OfType<IPdfFinisher>().FirstOrDefault() is not { } finisher)
            return;
        foreach (var pdf in outputs.Where(o => FormatRegistry.Detect(o)?.Id == "pdf"))
            await finisher.FinishAsync(pdf, preset.Pdf, ct);
    }

    /// <summary>Converts every file to PDF in the work directory, then merges them into one output.</summary>
    private async Task RunMergedAsync(ConversionJob job, RoutePlanner planner, string workDir)
    {
        var parts = new string?[job.Files.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, job.Files.Count),
            new ParallelOptions { MaxDegreeOfParallelism = ParallelFiles },
            async (i, _) =>
            {
                var file = job.Files[i];
                if (Stopped(file))
                    return;
                // An empty PDF goes the long way, where it fails as empty instead of breaking the whole merge; so does
                // one whose text is recognized first.
                if (FormatRegistry.Detect(file.SourcePath)?.Id == "pdf" && !IsEmptyFile(file.SourcePath) && !job.Preset.RecognizesTextOf("pdf"))
                {
                    // Already a PDF, merged as it is: its pages can't be turned (only images going into it can).
                    if (job.Preset.Image.TurnsOrMirrors)
                        FailUnturned(job, file, "pdf → pdf");
                    else
                        parts[i] = file.SourcePath;
                    return;
                }
                var produced = await RunFileAsync(job, file, i + 1, planner, workDir, finalOutput: false);
                parts[i] = produced.FirstOrDefault();
            });

        var inputs = parts.OfType<string>().ToList();
        if (inputs.Count == 0 || job.CancellationToken.IsCancellationRequested)
            return;

        var first = job.Files[0];
        var allocator = CreateFinalAllocator(job, first.SourcePath, 1, oneFile: true);
        var output = allocator.Allocate("pdf");
        if (output is null)
        {
            foreach (var f in job.Files) f.State = FileState.Skipped;
            return;
        }

        try
        {
            await _pdfMerger!.MergeAsync(inputs, output, job.CancellationToken);
            // Once for the merged PDF, so its pages are numbered through.
            await FinishPdfsAsync(job.Preset, [output], job.CancellationToken);
        }
        catch
        {
            allocator.Discard();
            throw;
        }
        first.Outputs.Add(output);
        foreach (var f in job.Files.Where(f => f.State != FileState.Failed))
        {
            f.State = FileState.Done;
            f.Progress = 1;
        }
        Notify(job);
    }

    /// <summary>
    /// Converts every file to TIFF pages in the work directory (a TIFF is taken as it is), then writes them all as one
    /// multi-page TIFF named after the first file.
    /// </summary>
    private async Task RunMultiPageTiffAsync(ConversionJob job, RoutePlanner planner, string workDir)
    {
        var parts = new IReadOnlyList<string>[job.Files.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, job.Files.Count),
            new ParallelOptions { MaxDegreeOfParallelism = ParallelFiles },
            async (i, _) =>
            {
                var file = job.Files[i];
                if (Stopped(file))
                    return;
                // A TIFF joins as it is, unless it is to be turned: then it goes through the engine like the rest.
                parts[i] = FormatRegistry.Detect(file.SourcePath)?.Id == "tiff" && !job.Preset.Image.TurnsOrMirrors
                           && !IsEmptyFile(file.SourcePath)
                    ? [file.SourcePath]
                    : await RunFileAsync(job, file, i + 1, planner, workDir, finalOutput: false, intermediate: "tiff");
            });

        var inputs = parts.SelectMany(p => p ?? []).ToList();
        if (inputs.Count == 0 || job.CancellationToken.IsCancellationRequested)
            return;

        var first = job.Files[0];
        var allocator = CreateFinalAllocator(job, first.SourcePath, 1, oneFile: true);
        var output = allocator.Allocate("tiff");
        if (output is null)
        {
            foreach (var f in job.Files) f.State = FileState.Skipped;
            return;
        }

        try
        {
            await _tiffMerger!.MergeAsync(inputs, output, job.Preset.Image, job.CancellationToken);
        }
        catch
        {
            allocator.Discard();
            throw;
        }
        first.Outputs.Add(output);
        foreach (var f in job.Files.Where(f => f.State != FileState.Failed))
        {
            f.State = FileState.Done;
            f.Progress = 1;
        }
        Notify(job);
    }

    /// <summary>Share of a slideshow's progress that preparing its pictures takes; encoding the video fills the rest.</summary>
    private const double SlidesReadyShare = 0.3;

    /// <summary>
    /// "Merge videos" / "Merge audio": the videos or audio files go into one file as they are. "Video from images":
    /// every picture becomes PNG in the work directory first (a PNG is taken as it is), with its orientation applied by
    /// the image engines, then the pictures become one video. The output is named after the first file.
    /// </summary>
    private async Task RunJoinedMediaAsync(ConversionJob job, RoutePlanner planner, string workDir)
    {
        var slideshow = job.Preset.Media.Slideshow;
        var target = FormatRegistry.Get(job.Preset.TargetFormat);
        // Before any picture is converted: too many fail at once instead of after minutes of PNG steps. Files whose
        // download already failed don't count.
        var pictures = job.Files.Count(f => !Stopped(f));
        if (slideshow && pictures > MediaOptions.MaxSlides)
        {
            foreach (var f in job.Files.Where(f => !Stopped(f)))
                FailFile(job, f, "error.too_many_pictures", $"{pictures} > {MediaOptions.MaxSlides}");
            return;
        }
        var parts = new IReadOnlyList<string>?[job.Files.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, job.Files.Count),
            new ParallelOptions { MaxDegreeOfParallelism = ParallelFiles },
            async (i, _) =>
            {
                var file = job.Files[i];
                if (Stopped(file))
                    return;
                var source = FormatRegistry.Detect(file.SourcePath);
                if (source is null)
                {
                    FailFile(job, file, "error.unsupported_source", "." + FormatRegistry.ExtensionOf(file.SourcePath));
                    return;
                }
                if (IsEmptyFile(file.SourcePath))
                {
                    FailFile(job, file, "error.empty_file", null);
                    return;
                }
                if (slideshow)
                {
                    parts[i] = source.Id == "png" && !job.Preset.Image.TurnsOrMirrors
                        ? [file.SourcePath]
                        : await RunFileAsync(job, file, i + 1, planner, workDir, finalOutput: false, intermediate: "png",
                            ceiling: SlidesReadyShare);
                    return;
                }
                // A video's sound can join an audio file; a picture or a document joins nothing.
                var joins = source.Category == FormatCategory.Video
                    || source.Category == FormatCategory.Audio && target.Category == FormatCategory.Audio;
                if (!joins)
                {
                    FailFile(job, file, "error.no_route", $"{source.Id} → {target.Id}");
                    return;
                }
                parts[i] = [file.SourcePath];
            });

        var joined = job.Files.Where((f, i) => parts[i] is { Count: > 0 } && f.State != FileState.Failed).ToList();
        var inputs = parts.Where(p => p is not null).SelectMany(p => p!).ToList();
        if (inputs.Count == 0 || job.CancellationToken.IsCancellationRequested)
        {
            Notify(job);
            return;
        }

        var first = joined[0];
        var allocator = CreateFinalAllocator(job, first.SourcePath, 1, oneFile: true);
        var output = allocator.Allocate(target.PrimaryExtension);
        if (output is null)
        {
            foreach (var f in joined)
            {
                f.State = FileState.Skipped;
                f.Progress = 1;
            }
            Notify(job);
            return;
        }

        foreach (var f in joined)
            f.State = FileState.Running;
        Notify(job);
        var floor = joined.Min(f => f.Progress);
        var progress = new Progress<double>(p =>
        {
            // Reports arrive on the thread pool, possibly out of order: progress only moves forward.
            var value = floor + (1 - floor) * Math.Clamp(p, 0, 1);
            if (value <= first.Progress)
                return;
            foreach (var f in joined)
                f.Progress = value;
            Notify(job);
        });

        try
        {
            await _mediaJoiner!.JoinAsync(inputs, output, target.Id, job.Preset, workDir, progress, job.CancellationToken);
            first.Outputs.Add(output);
            foreach (var f in joined)
            {
                f.State = FileState.Done;
                f.Progress = 1;
            }
        }
        catch (OperationCanceledException)
        {
            allocator.Discard();
            foreach (var f in joined)
                f.State = FileState.Cancelled;
        }
        catch (ConversionRefusedException ex)
        {
            allocator.Discard();
            _log.LogInformation("Joining {Count} file(s) into {Output} was refused: {Reason}", inputs.Count, output, ex.Message);
            foreach (var f in joined)
                FailFile(job, f, ex.ErrorKey, ex.Detail);
        }
        catch (Exception ex)
        {
            allocator.Discard();
            _log.LogWarning(ex, "Joining {Count} file(s) into {Output} failed", inputs.Count, output);
            foreach (var f in joined)
                FailFile(job, f, "error.conversion_failed", ex.Message);
        }
        Notify(job);
    }

    /// <summary>Packs every source file, unchanged, into one archive named after the first file.</summary>
    private async Task RunCombinedAsync(ConversionJob job, string workDir)
    {
        var format = FormatRegistry.Get(job.Preset.TargetFormat);
        var inputs = new List<FileResult>();
        foreach (var file in job.Files.Where(f => !Stopped(f)))
        {
            // Folders are packed with their content.
            if (File.Exists(file.SourcePath) || Directory.Exists(file.SourcePath))
                inputs.Add(file);
            else
                MarkFailed(file, "The file no longer exists.");
        }
        if (inputs.Count == 0)
        {
            Notify(job);
            return;
        }

        var allocator = CreateFinalAllocator(job, inputs[0].SourcePath, 1, oneFile: true);
        var output = allocator.Allocate(format.PrimaryExtension);
        if (output is null)
        {
            foreach (var f in inputs)
            {
                f.State = FileState.Skipped;
                f.Progress = 1;
            }
            Notify(job);
            return;
        }

        foreach (var f in inputs)
            f.State = FileState.Running;
        Notify(job);
        var progress = new Progress<double>(p =>
        {
            // Reports arrive on the thread pool, possibly out of order: progress only moves forward.
            var value = Math.Clamp(p, 0, 1);
            if (value <= inputs[0].Progress)
                return;
            foreach (var f in inputs)
                f.Progress = value;
            Notify(job);
        });

        try
        {
            // A Shapefile goes into the archive with its companion files (.shx, .dbf, .prj, ...): the .shp alone is no map.
            var files = new List<string>();
            var packed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in inputs.Select(f => f.SourcePath))
            {
                foreach (var file in new[] { source }.Concat(CompanionFiles.Of(source)))
                {
                    if (packed.Add(Path.GetFullPath(file)))
                        files.Add(file);
                }
            }
            await _combiner!.CombineAsync(files, output, format.Id, job.Preset, workDir, progress, job.CancellationToken);
            inputs[0].Outputs.Add(output);
            foreach (var f in inputs)
            {
                f.State = FileState.Done;
                f.Progress = 1;
            }
        }
        catch (OperationCanceledException)
        {
            allocator.Discard();
            foreach (var f in inputs)
                f.State = FileState.Cancelled;
        }
        catch (Exception ex)
        {
            allocator.Discard();
            _log.LogWarning(ex, "Packing {Count} file(s) into {Output} failed", inputs.Count, output);
            foreach (var f in inputs)
                MarkFailed(f, ex.Message);
        }
        Notify(job);

        static void MarkFailed(FileResult f, string detail)
        {
            f.State = FileState.Failed;
            f.ErrorKey = "error.conversion_failed";
            f.ErrorDetail = detail;
            f.Progress = 1;
        }
    }

    /// <summary>
    /// Packs the files one source was converted to into one ZIP (named by the preset's output rule, so the conflict
    /// policy applies to it) and returns it; empty when the rule says to skip an existing ZIP. Files keep their names
    /// inside, a clash gets "name (2).ext", and a ZIP that can't be completed is deleted.
    /// </summary>
    private IReadOnlyList<string> PackZip(ConversionJob job, FileResult file, int index, IReadOnlyList<string> files, CancellationToken ct)
    {
        var allocator = CreateFinalAllocator(job, file.SourcePath, index, oneFile: true);
        if (allocator.Allocate("zip") is not { } zipPath)
            return [];
        try
        {
            using (var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create))
            {
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in files)
                {
                    ct.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(path);
                    for (var n = 2; !used.Add(name); n++)
                        name = $"{Path.GetFileNameWithoutExtension(path)} ({n}){Path.GetExtension(path)}";
                    System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(archive, path, name, System.IO.Compression.CompressionLevel.Optimal);
                }
            }
        }
        catch
        {
            try
            {
                File.Delete(zipPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            allocator.Discard();
            throw;
        }
        return [zipPath];
    }
}
