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
public sealed partial class JobQueue : IAsyncDisposable
{
    private readonly ConverterCatalog _catalog;
    private readonly IPdfMerger? _pdfMerger;
    private readonly IFileCombiner? _combiner;
    private readonly ITiffMerger? _tiffMerger;
    private readonly IMediaJoiner? _mediaJoiner;
    private readonly ILogger _log;
    private readonly Channel<ConversionJob> _channel = Channel.CreateUnbounded<ConversionJob>();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _engineGates = new();
    /// <summary>
    /// Output paths handed out and not released yet, with how many conversions hold each. Also the lock for everything
    /// about output paths.
    /// </summary>
    private readonly Dictionary<string, Reservation> _reservedOutputs = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Output folders this queue made (a subfolder, a fixed folder that wasn't there): removed again when left empty.</summary>
    private readonly HashSet<string> _madeFolders = new(Platform.FileSystemPaths.Comparer);
    private readonly Task _worker;
    private readonly Func<string, Platform.TrashRefusal?>? _moveToTrash;
    private int _busy;

    /// <param name="catalog">Converters and their priority.</param>
    /// <param name="pdfMerger">Used by "Merge into one PDF" presets.</param>
    /// <param name="log">Optional logger.</param>
    /// <param name="combiner">Used by "Compress into one archive" presets (<see cref="ArchiveOptions.CombineIntoOne"/>).</param>
    /// <param name="tiffMerger">Used by multi-page TIFF presets (<see cref="ImageOptions.MultiPageTiff"/>).</param>
    /// <param name="mediaJoiner">Used by "Merge videos", "Merge audio" and "Video from images" (<see cref="Preset.JoinsIntoOneMedia"/>).</param>
    /// <param name="moveToTrash">
    /// Moves a file to the Recycle Bin / Trash for <see cref="OutputRule.ReplaceOriginal"/>: null when it worked, else
    /// why not. Without it originals are always kept.
    /// </param>
    public JobQueue(ConverterCatalog catalog, IPdfMerger? pdfMerger = null, ILogger<JobQueue>? log = null, IFileCombiner? combiner = null,
        ITiffMerger? tiffMerger = null, IMediaJoiner? mediaJoiner = null, Func<string, Platform.TrashRefusal?>? moveToTrash = null)
    {
        _moveToTrash = moveToTrash;
        _catalog = catalog;
        _pdfMerger = pdfMerger;
        _combiner = combiner;
        _tiffMerger = tiffMerger;
        _mediaJoiner = mediaJoiner;
        _log = log ?? (ILogger)NullLogger.Instance;
        _worker = Task.Run(WorkLoopAsync);
    }

    /// <summary>Raised (on a background thread) whenever a job's state or progress changes.</summary>
    public event EventHandler<ConversionJob>? JobUpdated;

    /// <summary>Raised (on a background thread) once when a job reaches a final state.</summary>
    public event EventHandler<ConversionJob>? JobFinished;

    /// <summary>True while a job waits in the queue or runs (the app frees memory only when this is false).</summary>
    public bool IsBusy => Volatile.Read(ref _busy) > 0;

    /// <summary>Adds a job to the queue and returns it immediately.</summary>
    /// <param name="fetcher">Saves sources that aren't files yet (files from a browser) when the job runs.</param>
    /// <param name="unattended">Nobody is there to answer a question (a watch folder): see <see cref="ConversionJob.Unattended"/>.</param>
    public ConversionJob Enqueue(IReadOnlyList<string> sources, Preset preset, string presetDisplayName, ISourceFetcher? fetcher = null,
        bool unattended = false)
    {
        var job = new ConversionJob(sources, preset, presetDisplayName, fetcher, unattended);
        Interlocked.Increment(ref _busy); // until the worker is done with it
        _channel.Writer.TryWrite(job);
        JobUpdated?.Invoke(this, job);
        return job;
    }

    /// <summary>Runs a job to completion on the calling task. Used by tests and by <see cref="Enqueue"/>'s worker.</summary>
    public async Task RunAsync(ConversionJob job)
    {
        Interlocked.Increment(ref _busy);
        try
        {
            await RunJobAsync(job);
        }
        finally
        {
            Interlocked.Decrement(ref _busy);
        }
    }

    private async Task RunJobAsync(ConversionJob job)
    {
        job.State = JobState.Running;
        Notify(job);

        var workDir = Path.Combine(DefaultWorkRoot, job.Id);
        Directory.CreateDirectory(workDir);
        try
        {
            if (job.Fetcher is { } fetcher)
                await FetchSourcesAsync(job, fetcher);

            var planner = _catalog.CreatePlanner();
            var merge = job.Preset.Pdf.MergeIntoSingle
                        && job.Preset.TargetFormat == "pdf"
                        && job.Files.Count > 1
                        && _pdfMerger is not null;
            // Any files, one of them or many, archives or not: they go into the archive as they are.
            var combine = job.Preset.PacksIntoOneArchive && _combiner is not null;

            // One file is enough: a 10-page PDF becomes one 10-page TIFF.
            var multiPageTiff = job.Preset.Image.MultiPageTiff && job.Preset.TargetFormat == "tiff" && _tiffMerger is not null;
            // Clips back to back, or pictures one after the other, in the order they were dropped.
            var join = job.Preset.JoinsIntoOneMedia && _mediaJoiner is not null;

            // Documents with macros or other active content: asked about once for the whole job, before any of them
            // is opened in an office suite. Joined media and archives packed as they are open no documents.
            if (!join && !combine)
                await ScreenActiveContentAsync(job, planner, merge ? "pdf" : multiPageTiff ? "tiff" : null);

            if (join)
                await RunJoinedMediaAsync(job, planner, workDir);
            else if (merge)
                await RunMergedAsync(job, planner, workDir);
            else if (multiPageTiff)
                await RunMultiPageTiffAsync(job, planner, workDir);
            else if (combine)
                await RunCombinedAsync(job, workDir);
            else
                await Parallel.ForEachAsync(
                    job.Files.Select((f, i) => (File: f, Index: i + 1)),
                    new ParallelOptions { MaxDegreeOfParallelism = ParallelFiles, CancellationToken = CancellationToken.None },
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
            {
                foreach (var output in job.Outputs)
                    Unreserve(output);
                // Folders no running job writes into any more are the user's from now on.
                _madeFolders.RemoveWhere(folder => !HasReservationIn(folder));
            }
            TryDelete(workDir);
            Release(job);
        }

        Notify(job);
        JobFinished?.Invoke(this, job);
    }

    /// <summary>
    /// A file that didn't get as far as being converted: saving it failed or was cancelled, or it was left out before the
    /// job ran (a document with active content the user didn't trust).
    /// </summary>
    private static bool Stopped(FileResult file) => file.State is FileState.Failed or FileState.Cancelled or FileState.Skipped;

    /// <summary>Lets the job's fetcher clean up (originals that aren't kept).</summary>
    private void Release(ConversionJob job)
    {
        try
        {
            job.Fetcher?.Release(job);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cleaning up after job {Job} failed", job.Id);
        }
    }

    private void FailFile(ConversionJob job, FileResult file, string key, string? detail)
    {
        file.State = FileState.Failed;
        file.ErrorKey = key;
        file.ErrorDetail = detail;
        file.Progress = 1;
        Notify(job);
    }

    /// <summary>A file that would be used as it is although the preset turns or mirrors it.</summary>
    private void FailUnturned(ConversionJob job, FileResult file, string detail)
    {
        file.State = FileState.Failed;
        file.ErrorKey = "error.cannot_turn";
        file.ErrorDetail = detail;
        file.Progress = 1;
        Notify(job);
    }

    /// <summary>A file (not a folder) with nothing in it. Combining still packs such a file as it is.</summary>
    private static bool IsEmptyFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Converts one source file along its planned route.
    /// With <paramref name="finalOutput"/> false, results stay in the work directory as <paramref name="intermediate"/>
    /// files (PDF for merging, TIFF pages for a multi-page TIFF). <paramref name="ceiling"/> is the file's progress once
    /// the route is done (a slideshow's pictures leave the rest to the video).
    /// </summary>
    private async Task<IReadOnlyList<string>> RunFileAsync(
        ConversionJob job, FileResult file, int index, RoutePlanner planner, string workDir, bool finalOutput,
        string intermediate = "pdf", double ceiling = 1)
    {
        var ct = job.CancellationToken;
        if (Stopped(file))
            return [];
        var source = FormatRegistry.Detect(file.SourcePath);
        if (source is null)
            return Fail(file, "error.unsupported_source", "." + FormatRegistry.ExtensionOf(file.SourcePath));
        // A file just made with "New" in the file manager stays 0 bytes until its app saves it: no engine can read it,
        // and their errors ("Central Directory corrupt") don't say why.
        if (IsEmptyFile(file.SourcePath))
            return Fail(file, "error.empty_file", null);

        var target = !finalOutput ? intermediate
            : job.Preset.TargetFormat == BuiltInData.SameAsSource ? source.Id
            : job.Preset.TargetFormat;

        // "Save every frame": a video becomes a folder of pictures in one step of its own; "Extract Live/Motion Photo
        // video" likewise takes a photo's video, and "Live / Motion Photo" makes one of a video. Never for an
        // intermediate file, and never on the way to another format (video → JPG is otherwise one still frame, photo →
        // MP4 a video of the still picture).
        var frames = finalOutput && job.Preset.SavesFramesOf(source.Id);
        var motionPhoto = finalOutput && job.Preset.ExtractsMotionOf(source.Id);
        var livePhoto = finalOutput && job.Preset.MakesLivePhotoOf(source.Id);
        // "Recognize text": Tesseract writes a searchable PDF first, read on into DOCX, TXT, … from there. Also for the
        // PDFs that are merged into one.
        var ocr = (finalOutput || target == "pdf") && job.Preset.RecognizesTextOf(source.Id);
        var route = planner.Plan(source.Id, target, frames, motionPhoto: motionPhoto, livePhoto: livePhoto, ocr: ocr);
        if (route is null)
            return Fail(file, "error.no_route", $"{source.Id} → {target}");

        // A step that draws text without shaping (rhwp) would break Arabic, Hebrew, Indic or Thai text: such a file
        // takes a route without it (LibreOffice), or is refused with the reason rather than written broken. One
        // scan of the file's text answers this and the widths question below.
        var scripts = route.Steps.FirstOrDefault(s => s.Unshaped)?.Converter is ITextShapingCheck check
            ? ScanScripts(check, file.SourcePath, source.Id)
            : null;
        if (scripts?.NeedsShaping is { } script)
        {
            _log.LogInformation("{File} has {Script} text, which {Engine} can't draw: planning a route without it",
                file.SourcePath, script, route.Steps.First(s => s.Unshaped).Converter.DisplayName);
            route = planner.Plan(source.Id, target, frames, shaping: true, ocr: ocr);
            if (route is null)
                return Fail(file, "error.needs_shaping", script);
        }
        // Letters the engine has no widths for — anything outside ASCII and Latin-1 but the full-width CJK it
        // estimates at an em (Polish, Turkish, Vietnamese, Greek, Cyrillic, ... — rhwp#8143) — overlap at a fixed
        // half-em: still readable, so the file is never refused, it only takes a route without the unshaped step
        // when there is one.
        else if (scripts?.WithoutWidths is { } unmeasured)
        {
            _log.LogInformation("{File} has {Script} text, which {Engine} can't measure: planning a route without it",
                file.SourcePath, unmeasured, route.Steps.First(s => s.Unshaped).Converter.DisplayName);
            route = planner.Plan(source.Id, target, frames, shaping: true) ?? route;
        }

        // Through an office suite only now (rerouted above), and not checked for macros before the job ran.
        if (CheckActiveContentLate(file, source.Id, route) is { } refusal)
            return Fail(file, refusal, null);

        // Rotate / flip: only an engine that turns images does it. Without one the file would come out unturned
        // (a PDF or a video re-encoded "as rotated"); with two in the route (EPS → PNG → JPG) it would turn twice.
        var turnAt = job.Preset.Image.TurnsOrMirrors ? route.FirstTransformStep() : -1;
        if (job.Preset.Image.TurnsOrMirrors && turnAt < 0)
            return Fail(file, "error.cannot_turn", $"{source.Id} → {target}");
        var unturned = turnAt < 0 ? job.Preset : Unturned(job.Preset);

        file.State = FileState.Running;
        Notify(job);
        // A file saved from a browser first has part of its progress already.
        var floor = file.Progress;
        ceiling = Math.Max(ceiling, floor);
        // The last step's allocator: how many paths it handed out tells a single output from several, and what a
        // failed or cancelled conversion started there is undone through it.
        FinalAllocator? final = null;

        try
        {
            IReadOnlyList<string> inputs = [file.SourcePath];
            // "Save as ZIP": the last step writes into the work folder and the files are packed into one ZIP afterwards.
            var zipResult = finalOutput && job.Preset.Output.ZipResult;
            for (var s = 0; s < route.Steps.Count; s++)
            {
                var step = route.Steps[s];
                var isLast = s == route.Steps.Count - 1;
                IOutputAllocator allocator = isLast && finalOutput && !zipResult
                    ? final = CreateFinalAllocator(job, file.SourcePath, index)
                    : new TempAllocator(workDir, FormatRegistry.NameWithoutExtension(file.SourcePath));

                var outputs = new List<string>();
                foreach (var input in inputs)
                {
                    ct.ThrowIfCancellationRequested();
                    var stepIndex = s;
                    var progress = new Progress<double>(p =>
                    {
                        // Reports arrive on the thread pool, possibly out of order: progress only moves forward.
                        var value = floor + (ceiling - floor) * (stepIndex + Math.Clamp(p, 0, 1)) / route.Steps.Count;
                        if (value <= file.Progress)
                            return;
                        file.Progress = value;
                        Notify(job);
                    });

                    var gate = GateFor(step.Converter);
                    await gate.WaitAsync(ct);
                    try
                    {
                        // Turned once, in step turnAt: every other step gets the preset without rotation and flip,
                        // also one before it whose engine applies image options but doesn't say it turns.
                        var preset = s == turnAt ? job.Preset : unturned;
                        // Playback speed changes once, in the step that writes the output: an animation passed from one
                        // engine to another would otherwise change speed in both.
                        if (preset.Image.ChangesSpeed && !(isLast && finalOutput))
                            preset = AtOriginalSpeed(preset);
                        var stepInfo = new ConversionStep(input, step.From, step.To, preset, allocator, workDir, step.FrameSequence,
                            key => file.NoticeKey = key, step.MotionPhoto, step.LivePhoto, fonts =>
                            {
                                // Replace the collection as one value: the UI can enumerate its snapshot while this
                                // worker reports another step without racing a mutable List.
                                file.MissingFonts = file.MissingFonts.Concat(fonts)
                                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                                Notify(job);
                            }, file.SourcePath);
                        outputs.AddRange(await ConvertWithTimeoutAsync(step.Converter, stepInfo, progress, ct));
                    }
                    finally
                    {
                        gate.Release();
                    }
                }
                inputs = outputs;
            }

            // Under "Save as ZIP" the output is still in the work folder: the source copied over it goes into the ZIP.
            var copiedSource = finalOutput && job.Preset.Output.OnlyIfSmaller && inputs.Count == 1 && CopyIfNotSmaller(file.SourcePath, inputs[0]);
            if (copiedSource)
                file.NoticeKey = "notice.pdf_not_smaller";
            // Watermark, page numbers, password: on every PDF this file became, from whichever engine.
            if (finalOutput && job.Preset.TargetFormat == "pdf")
                await FinishPdfsAsync(job.Preset, inputs, ct);

            if (zipResult && inputs.Count > 0)
                inputs = PackZip(job, file, index, inputs, ct);
            // One output, and nothing else handed out for it (a Markdown file's picture folder goes with it).
            else if (finalOutput && inputs.Count == 1 && final is { Handed: 1 })
                inputs = [NextToSource(job, file, index, inputs[0], final)];

            // The source's dates are read now: "Replace the original" may send it to the Trash below.
            var dates = finalOutput && job.Preset.Output.KeepDates ? DatesOf(file.SourcePath) : null;
            if (finalOutput && !zipResult && inputs.Count == 1)
                inputs = [ReplaceOriginal(job, file, source.Id, inputs[0], copiedSource)];

            // A name the engine took but wrote nothing to is free again.
            final?.Release(except: inputs);
            if (finalOutput)
            {
                file.Outputs.AddRange(inputs);
                if (dates is { } kept)
                    CopyDates(kept, inputs);
            }
            file.State = inputs.Count == 0 && finalOutput ? FileState.Skipped : FileState.Done;
            file.Progress = ceiling;
            Notify(job);
            return inputs;
        }
        catch (OperationCanceledException)
        {
            final?.Discard();
            file.State = FileState.Cancelled;
            Notify(job);
            return [];
        }
        catch (ConversionRefusedException ex)
        {
            final?.Discard();
            _log.LogInformation("Converting {File} was refused: {Reason}", file.SourcePath, ex.Message);
            return Fail(file, ex.ErrorKey, ex.Detail);
        }
        catch (Exception ex)
        {
            final?.Discard();
            _log.LogWarning(ex, "Converting {File} failed", file.SourcePath);
            return Fail(file, "error.conversion_failed", ex.Message);
        }

        static Preset Unturned(Preset preset)
        {
            var copy = preset.Clone();
            copy.Image.Rotation = ImageRotation.None;
            copy.Image.Flip = ImageFlip.None;
            return copy;
        }

        static Preset AtOriginalSpeed(Preset preset)
        {
            var copy = preset.Clone();
            copy.Image.AnimationSpeed = 1;
            return copy;
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

    /// <summary>
    /// Puts the source in place of <paramref name="output"/> when both have the same format and the output is not
    /// smaller (<see cref="OutputRule.OnlyIfSmaller"/>). True when it did.
    /// </summary>
    private static bool CopyIfNotSmaller(string source, string output)
    {
        if (!string.Equals(FormatRegistry.Detect(source)?.Id, FormatRegistry.Detect(output)?.Id, StringComparison.Ordinal)
            || !File.Exists(output) || new FileInfo(output).Length < new FileInfo(source).Length)
            return false;
        File.Copy(source, output, overwrite: true);
        return true;
    }

    /// <summary>The engine's scan; one that fails (an unreadable file) finds nothing, and the conversion reports the file.</summary>
    private TextScripts? ScanScripts(ITextShapingCheck check, string path, string format)
    {
        try
        {
            return check.ScanScripts(path, format);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not check the scripts of {File}", path);
            return null;
        }
    }

    private SemaphoreSlim GateFor(IConverter converter) =>
        _engineGates.GetOrAdd(converter.Id, _ =>
        {
            var max = converter.MaxParallelism <= 0 ? Environment.ProcessorCount : converter.MaxParallelism;
            return new SemaphoreSlim(max, max);
        });

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
            try
            {
                if (job.CancellationToken.IsCancellationRequested)
                {
                    job.State = JobState.Cancelled;
                    Release(job);
                    Notify(job);
                    JobFinished?.Invoke(this, job);
                    continue;
                }
                await RunAsync(job);
            }
            finally
            {
                Interlocked.Decrement(ref _busy);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _worker;
    }

}
