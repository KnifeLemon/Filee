// Runs conversion jobs in the background: plans routes, limits engine concurrency,
// allocates output paths and reports progress. UI code only enqueues and listens to events.

using System.Collections.Concurrent;
using System.Threading.Channels;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.Core.Conversion;

/// <summary>Background queue that executes <see cref="ConversionJob"/>s one after another.</summary>
public sealed class JobQueue : IAsyncDisposable
{
    private readonly ConverterCatalog _catalog;
    private readonly IPdfMerger? _pdfMerger;
    private readonly IFileCombiner? _combiner;
    private readonly ILogger _log;
    private readonly Channel<ConversionJob> _channel = Channel.CreateUnbounded<ConversionJob>();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _engineGates = new();
    private readonly HashSet<string> _reservedOutputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Task _worker;

    /// <param name="catalog">Converters and their priority.</param>
    /// <param name="pdfMerger">Used by "Merge into one PDF" presets.</param>
    /// <param name="log">Optional logger.</param>
    /// <param name="combiner">Used by "Compress into one archive" presets (<see cref="ArchiveOptions.CombineIntoOne"/>).</param>
    public JobQueue(ConverterCatalog catalog, IPdfMerger? pdfMerger = null, ILogger<JobQueue>? log = null, IFileCombiner? combiner = null)
    {
        _catalog = catalog;
        _pdfMerger = pdfMerger;
        _combiner = combiner;
        _log = log ?? (ILogger)NullLogger.Instance;
        _worker = Task.Run(WorkLoopAsync);
    }

    /// <summary>Raised (on a background thread) whenever a job's state or progress changes.</summary>
    public event EventHandler<ConversionJob>? JobUpdated;

    /// <summary>Raised (on a background thread) once when a job reaches a final state.</summary>
    public event EventHandler<ConversionJob>? JobFinished;

    /// <summary>Adds a job to the queue and returns it immediately.</summary>
    public ConversionJob Enqueue(IReadOnlyList<string> sources, Preset preset, string presetDisplayName)
    {
        var job = new ConversionJob(sources, preset, presetDisplayName);
        _channel.Writer.TryWrite(job);
        JobUpdated?.Invoke(this, job);
        return job;
    }

    /// <summary>Runs a job to completion on the calling task. Used by tests and by <see cref="Enqueue"/>'s worker.</summary>
    public async Task RunAsync(ConversionJob job)
    {
        job.State = JobState.Running;
        Notify(job);

        var workDir = Path.Combine(Path.GetTempPath(), "Filee", job.Id);
        Directory.CreateDirectory(workDir);
        try
        {
            var planner = _catalog.CreatePlanner();
            var merge = job.Preset.Pdf.MergeIntoSingle
                        && job.Preset.TargetFormat == "pdf"
                        && job.Files.Count > 1
                        && _pdfMerger is not null;
            // Any files, one of them or many, archives or not: they go into the archive as they are.
            var combine = job.Preset.Archive.CombineIntoOne
                          && _combiner is not null
                          && FormatRegistry.FindById(job.Preset.TargetFormat) is { Category: FormatCategory.Archive } archive
                          && archive.Id != FormatRegistry.Folder;

            if (merge)
                await RunMergedAsync(job, planner, workDir);
            else if (combine)
                await RunCombinedAsync(job, workDir);
            else
                await Parallel.ForEachAsync(
                    job.Files.Select((f, i) => (File: f, Index: i + 1)),
                    new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = CancellationToken.None },
                    async (item, _) => await RunFileAsync(job, item.File, item.Index, planner, workDir, finalOutput: true));

            job.State = job.CancellationToken.IsCancellationRequested ? JobState.Cancelled
                : job.Files.All(f => f.State is FileState.Done or FileState.Skipped) ? JobState.Completed
                : job.Files.Any(f => f.State == FileState.Done) ? JobState.CompletedWithErrors
                : JobState.Failed;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Job {Job} failed", job.Id);
            job.State = JobState.Failed;
        }
        finally
        {
            lock (_reservedOutputs)
                foreach (var output in job.Outputs)
                    _reservedOutputs.Remove(output);
            TryDelete(workDir);
        }

        Notify(job);
        JobFinished?.Invoke(this, job);
    }

    /// <summary>Converts every file to PDF in the work directory, then merges them into one output.</summary>
    private async Task RunMergedAsync(ConversionJob job, RoutePlanner planner, string workDir)
    {
        var parts = new string?[job.Files.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, job.Files.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            async (i, _) =>
            {
                var file = job.Files[i];
                if (FormatRegistry.Detect(file.SourcePath)?.Id == "pdf")
                {
                    parts[i] = file.SourcePath; // already a PDF, merge as-is
                    return;
                }
                var produced = await RunFileAsync(job, file, i + 1, planner, workDir, finalOutput: false);
                parts[i] = produced.FirstOrDefault();
            });

        var inputs = parts.OfType<string>().ToList();
        if (inputs.Count == 0 || job.CancellationToken.IsCancellationRequested)
            return;

        var first = job.Files[0];
        var allocator = CreateFinalAllocator(job, first.SourcePath, 1);
        var output = allocator.Allocate("pdf");
        if (output is null)
        {
            foreach (var f in job.Files) f.State = FileState.Skipped;
            return;
        }

        await _pdfMerger!.MergeAsync(inputs, output, job.CancellationToken);
        first.Outputs.Add(output);
        foreach (var f in job.Files.Where(f => f.State != FileState.Failed))
        {
            f.State = FileState.Done;
            f.Progress = 1;
        }
        Notify(job);
    }

    /// <summary>Packs every source file, unchanged, into one archive named after the first file.</summary>
    private async Task RunCombinedAsync(ConversionJob job, string workDir)
    {
        var format = FormatRegistry.Get(job.Preset.TargetFormat);
        var inputs = new List<FileResult>();
        foreach (var file in job.Files)
        {
            // Folders (from the command line; drops deliver files) are packed with their content.
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

        var output = CreateFinalAllocator(job, inputs[0].SourcePath, 1).Allocate(format.PrimaryExtension);
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
            await _combiner!.CombineAsync(inputs.Select(f => f.SourcePath).ToList(), output, format.Id, job.Preset,
                workDir, progress, job.CancellationToken);
            inputs[0].Outputs.Add(output);
            foreach (var f in inputs)
            {
                f.State = FileState.Done;
                f.Progress = 1;
            }
        }
        catch (OperationCanceledException)
        {
            foreach (var f in inputs)
                f.State = FileState.Cancelled;
        }
        catch (Exception ex)
        {
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

    /// <summary>Gives the outputs the creation and modification dates of the source (<see cref="OutputRule.KeepDates"/>).</summary>
    private void CopyDates(string source, IEnumerable<string> outputs)
    {
        try
        {
            var created = File.GetCreationTimeUtc(source);
            var modified = File.GetLastWriteTimeUtc(source);
            foreach (var output in outputs)
            {
                if (File.Exists(output))
                {
                    File.SetCreationTimeUtc(output, created);
                    File.SetLastWriteTimeUtc(output, modified);
                }
                else if (Directory.Exists(output))
                {
                    Directory.SetCreationTimeUtc(output, created);
                    Directory.SetLastWriteTimeUtc(output, modified);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The conversion itself worked: keep the files, only the dates are new.
            _log.LogWarning(ex, "Could not copy the dates of {Source}", source);
        }
    }

    /// <summary>
    /// Converts one source file along its planned route.
    /// With <paramref name="finalOutput"/> false, results stay in the work directory (used for merging).
    /// </summary>
    private async Task<IReadOnlyList<string>> RunFileAsync(
        ConversionJob job, FileResult file, int index, RoutePlanner planner, string workDir, bool finalOutput)
    {
        var ct = job.CancellationToken;
        var source = FormatRegistry.Detect(file.SourcePath);
        if (source is null)
            return Fail(file, "error.unsupported_source", "." + FormatRegistry.ExtensionOf(file.SourcePath));

        var target = !finalOutput ? "pdf"
            : job.Preset.TargetFormat == BuiltInData.SameAsSource ? source.Id
            : job.Preset.TargetFormat;

        var route = planner.Plan(source.Id, target);
        if (route is null)
            return Fail(file, "error.no_route", $"{source.Id} → {target}");

        file.State = FileState.Running;
        Notify(job);

        try
        {
            IReadOnlyList<string> inputs = [file.SourcePath];
            for (var s = 0; s < route.Steps.Count; s++)
            {
                var step = route.Steps[s];
                var isLast = s == route.Steps.Count - 1;
                IOutputAllocator allocator = isLast && finalOutput
                    ? CreateFinalAllocator(job, file.SourcePath, index)
                    : new TempAllocator(workDir, FormatRegistry.NameWithoutExtension(file.SourcePath));

                var outputs = new List<string>();
                foreach (var input in inputs)
                {
                    ct.ThrowIfCancellationRequested();
                    var stepIndex = s;
                    var progress = new Progress<double>(p =>
                    {
                        // Reports arrive on the thread pool, possibly out of order: progress only moves forward.
                        var value = (stepIndex + Math.Clamp(p, 0, 1)) / route.Steps.Count;
                        if (value <= file.Progress)
                            return;
                        file.Progress = value;
                        Notify(job);
                    });

                    var gate = GateFor(step.Converter);
                    await gate.WaitAsync(ct);
                    try
                    {
                        var stepInfo = new ConversionStep(input, step.From, step.To, job.Preset, allocator, workDir);
                        outputs.AddRange(await step.Converter.ConvertAsync(stepInfo, progress, ct));
                    }
                    finally
                    {
                        gate.Release();
                    }
                }
                inputs = outputs;
            }

            if (finalOutput)
            {
                file.Outputs.AddRange(inputs);
                if (job.Preset.Output.KeepDates)
                    CopyDates(file.SourcePath, inputs);
            }
            file.State = inputs.Count == 0 && finalOutput ? FileState.Skipped : FileState.Done;
            file.Progress = 1;
            Notify(job);
            return inputs;
        }
        catch (OperationCanceledException)
        {
            file.State = FileState.Cancelled;
            Notify(job);
            return [];
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Converting {File} failed", file.SourcePath);
            return Fail(file, "error.conversion_failed", ex.Message);
        }

        IReadOnlyList<string> Fail(FileResult f, string key, string? detail)
        {
            f.State = FileState.Failed;
            f.ErrorKey = key;
            f.ErrorDetail = detail;
            f.Progress = 1;
            Notify(job);
            return [];
        }
    }

    private SemaphoreSlim GateFor(IConverter converter) =>
        _engineGates.GetOrAdd(converter.Id, _ =>
        {
            var max = converter.MaxParallelism <= 0 ? Environment.ProcessorCount : converter.MaxParallelism;
            return new SemaphoreSlim(max, max);
        });

    private FinalAllocator CreateFinalAllocator(ConversionJob job, string sourcePath, int index) =>
        new(this, job.Preset.Output, new OutputPathResolver.Tokens(sourcePath, job.PresetDisplayName, index, job.CreatedAt));

    private void Notify(ConversionJob job)
    {
        try
        {
            JobUpdated?.Invoke(this, job);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "JobUpdated handler threw");
        }
    }

    private async Task WorkLoopAsync()
    {
        await foreach (var job in _channel.Reader.ReadAllAsync())
        {
            if (job.CancellationToken.IsCancellationRequested)
            {
                job.State = JobState.Cancelled;
                Notify(job);
                JobFinished?.Invoke(this, job);
                continue;
            }
            await RunAsync(job);
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { /* a viewer may still hold a temp file; the OS cleans temp eventually */ }
        catch (UnauthorizedAccessException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _worker;
    }

    /// <summary>Allocates final output paths according to the preset's <see cref="OutputRule"/>.</summary>
    private sealed class FinalAllocator(JobQueue queue, OutputRule rule, OutputPathResolver.Tokens tokens) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null)
        {
            // Reserve under a lock so two parallel files never pick the same name.
            lock (queue._reservedOutputs)
            {
                var path = OutputPathResolver.Resolve(rule, tokens, extension,
                    p => File.Exists(p) || Directory.Exists(p) || queue._reservedOutputs.Contains(p), suffix);
                if (path is null)
                    return null;

                queue._reservedOutputs.Add(path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                // An empty extension asks for a folder (archive extraction): the converter creates it.
                return path;
            }
        }
    }

    /// <summary>Allocates unique paths inside the job's work directory for intermediate files.</summary>
    private sealed class TempAllocator(string workDir, string baseName) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null)
        {
            var dir = Path.Combine(workDir, Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var ext = extension.TrimStart('.');
            return Path.Combine(dir, ext.Length == 0 ? $"{baseName}{suffix}" : $"{baseName}{suffix}.{ext}");
        }
    }
}
