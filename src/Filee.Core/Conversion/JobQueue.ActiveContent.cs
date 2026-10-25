// JobQueue: Settings → Security and limits. Documents with active content are checked before a step through an office
// suite (asking the user, or following the setting), files of a job convert at most so many at once, and a built-in
// engine's step fails as stuck after the step time limit.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Filee.Core.Conversion;

public sealed partial class JobQueue
{
    private int _parallelFiles = Environment.ProcessorCount;

    /// <summary>Finds macros and other active content in documents; without it no document is checked.</summary>
    public IActiveContentScanner? ActiveContentScanner { get; set; }

    /// <summary>What happens to a document with active content (<see cref="SecuritySettings.ActiveContent"/>).</summary>
    public ActiveContentPolicy ActiveContentPolicy { get; set; } = ActiveContentPolicy.Ask;

    /// <summary>
    /// Asks the user about a job's documents with active content under <see cref="ActiveContentPolicy.Ask"/>. Null (the
    /// command line) and unattended jobs (<see cref="ConversionJob.Unattended"/>) convert them without asking.
    /// </summary>
    public ActiveContentPrompt? ActiveContentPrompt { get; set; }

    /// <summary>How many files of a job convert at once (<see cref="LimitSettings.EffectiveParallelFiles"/>); at least 1.</summary>
    public int ParallelFiles
    {
        get => _parallelFiles;
        set => _parallelFiles = Math.Max(1, value);
    }

    /// <summary>
    /// How long a built-in engine may spend on one step before the file fails as stuck (<see cref="LimitSettings.EffectiveStepTimeout"/>);
    /// null for no limit. Engines with a time limit of their own (<see cref="IHasOwnTimeout"/>) keep theirs.
    /// </summary>
    public TimeSpan? StepTimeout { get; set; }

    /// <summary>
    /// Checks the job's documents that would go through an office suite for active content before any of them is
    /// converted, so the user is asked once for the whole job. Under "Ask" with nobody to ask, they convert and the file's
    /// result says what they carry.
    /// </summary>
    private async Task ScreenActiveContentAsync(ConversionJob job, RoutePlanner planner, string? intermediate)
    {
        if (ActiveContentScanner is not { } scanner)
            return;
        var found = new List<(FileResult File, ActiveContentReport Report)>();
        foreach (var file in job.Files)
        {
            if (Stopped(file) || FormatRegistry.Detect(file.SourcePath) is not { } source)
                continue;
            var target = intermediate
                         ?? (job.Preset.TargetFormat == BuiltInData.SameAsSource ? source.Id : job.Preset.TargetFormat);
            if (planner.Plan(source.Id, target) is not { } route || !route.Steps.Any(s => s.Converter is IOpensDocumentsInOfficeSuite))
                continue;
            file.ActiveContentChecked = true;
            if (Scan(scanner, file.SourcePath, source.Id) is not { } report)
                continue;
            file.ActiveContent = report.Findings;
            found.Add((file, report));
        }
        if (found.Count == 0)
            return;

        _log.LogInformation("Job {Job}: {Count} document(s) with active content, policy {Policy}", job.Id, found.Count, ActiveContentPolicy);
        var decision = ActiveContentPolicy switch
        {
            ActiveContentPolicy.Convert => ActiveContentDecision.Trust,
            ActiveContentPolicy.Refuse => ActiveContentDecision.Skip,
            _ when job.Unattended || ActiveContentPrompt is null => ActiveContentDecision.Trust,
            _ => await AskAsync(job, found.Select(f => f.Report).ToList()),
        };
        foreach (var (file, report) in found)
        {
            switch (decision)
            {
                case ActiveContentDecision.Trust:
                    file.NoticeKey ??= "notice.active_content";
                    break;
                case ActiveContentDecision.Skip when ActiveContentPolicy == ActiveContentPolicy.Refuse:
                    FailFile(job, file, "error.active_content_refused", Summary(report));
                    break;
                case ActiveContentDecision.Skip:
                    file.State = FileState.Skipped;
                    file.NoticeKey = "notice.active_content_skipped";
                    file.Progress = 1;
                    Notify(job);
                    break;
            }
        }
        if (decision == ActiveContentDecision.Cancel)
            job.Cancel();
    }

    /// <summary>Asks the user; a prompt that fails leaves the documents out rather than converting them unasked.</summary>
    private async Task<ActiveContentDecision> AskAsync(ConversionJob job, IReadOnlyList<ActiveContentReport> reports)
    {
        try
        {
            return await ActiveContentPrompt!(job, reports, job.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            return ActiveContentDecision.Cancel;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Asking about active content failed for job {Job}: leaving those documents out", job.Id);
            return ActiveContentDecision.Skip;
        }
    }

    /// <summary>
    /// A file whose route goes through an office suite only now (rerouted for its scripts, say) and wasn't checked before
    /// the job ran: refused under "Never", else converted with what it carries in its result. Returns the error key, or null.
    /// </summary>
    private string? CheckActiveContentLate(FileResult file, string sourceFormat, Route route)
    {
        if (file.ActiveContentChecked || ActiveContentScanner is not { } scanner
            || !route.Steps.Any(s => s.Converter is IOpensDocumentsInOfficeSuite))
            return null;
        file.ActiveContentChecked = true;
        if (Scan(scanner, file.SourcePath, sourceFormat) is not { } report)
            return null;
        file.ActiveContent = report.Findings;
        if (ActiveContentPolicy == ActiveContentPolicy.Refuse)
            return "error.active_content_refused";
        file.NoticeKey ??= "notice.active_content";
        return null;
    }

    private ActiveContentReport? Scan(IActiveContentScanner scanner, string path, string formatId)
    {
        try
        {
            return scanner.Scan(path, formatId) is { Findings.Count: > 0 } report ? report : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // An unreadable package is the conversion's problem, which reports it with its own words.
            _log.LogInformation("Checking {File} for active content failed: {Message}", path, ex.Message);
            return null;
        }
    }

    /// <summary>"Macro: Module1, ExternalLink: http://…" for the log and the error detail (not localized).</summary>
    internal static string Summary(ActiveContentReport report) =>
        string.Join(", ", report.Findings.Take(5).Select(f => $"{f.Kind}: {f.Name}"))
        + (report.Findings.Count > 5 ? $", +{report.Findings.Count - 5}" : "");

    /// <summary>
    /// Runs one step, failing it as stuck after <see cref="StepTimeout"/> for an engine without a time limit of its own.
    /// The engine's token is cancelled then; one that doesn't check it finishes in the background, but the job goes on.
    /// </summary>
    private async Task<IReadOnlyList<string>> ConvertWithTimeoutAsync(IConverter converter, ConversionStep step,
        IProgress<double> progress, CancellationToken ct)
    {
        if (StepTimeout is not { } limit || converter is IHasOwnTimeout)
            return await converter.ConvertAsync(step, progress, ct);

        var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var task = converter.ConvertAsync(step, progress, stop.Token);
        try
        {
            return await task.WaitAsync(limit, ct);
        }
        catch (TimeoutException)
        {
            stop.Cancel();
            _log.LogWarning("{Engine} took longer than {Minutes} min on {File}: stopped", converter.DisplayName, limit.TotalMinutes, step.InputPath);
            // Observe the abandoned step so its exception isn't reported as unobserved later.
            _ = task.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            throw new ConversionRefusedException("error.conversion_timeout", $"{converter.DisplayName}, {limit.TotalMinutes:0} min");
        }
        finally
        {
            if (task.IsCompleted)
                stop.Dispose();
            else
                _ = task.ContinueWith(_ => stop.Dispose(), TaskScheduler.Default);
        }
    }
}
