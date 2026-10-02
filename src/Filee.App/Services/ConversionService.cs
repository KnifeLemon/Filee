// UI-facing wrapper around JobQueue: starts jobs, keeps bindable lists for the toast window and records history.
// Jobs started from the donut are first shown by its progress ring and only appear in the toast when the ring
// hands them over (long job, failure, or a new drag).

using System.Collections.ObjectModel;
using Avalonia.Threading;
using Filee.App.ViewModels;
using Filee.Core.Conversion;
using Filee.Core.History;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.App.Services;

public sealed class ConversionService
{
    private static readonly TimeSpan KeepSuccess = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan KeepErrors = TimeSpan.FromSeconds(15);

    /// <summary>After this many fully successful conversions Filee asks once for a star or feedback.</summary>
    public const int FeedbackAfterConversions = 3;

    private readonly JobQueue _queue;
    private readonly UserDataStore _store;
    private readonly ILocalizer _loc;
    private readonly IPlatformServices _platform;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, JobViewModel> _byId = new();

    public ConversionService(JobQueue queue, UserDataStore store, ILocalizer loc, IPlatformServices platform)
    {
        _queue = queue;
        _store = store;
        _loc = loc;
        _platform = platform;
        _queue.JobUpdated += OnJobUpdated;
        _queue.JobFinished += OnJobFinished;
    }

    /// <summary>All jobs that are running or finished recently, newest first. UI thread only.</summary>
    public ObservableCollection<JobViewModel> Jobs { get; } = [];

    /// <summary>The subset shown in the toast window. UI thread only.</summary>
    public ObservableCollection<JobViewModel> ToastJobs { get; } = [];

    /// <summary>
    /// Raised once (UI thread) when the user has had a few successful conversions and the toast has gone: time to
    /// ask for a GitHub star or feedback.
    /// </summary>
    public event EventHandler? FeedbackDue;

    /// <summary>Starts converting files with a preset.</summary>
    /// <param name="files">Source files.</param>
    /// <param name="preset">Preset to apply.</param>
    /// <param name="showInToast">False when another view (the donut's progress ring) displays the job first.</param>
    /// <returns>The job's view model, or null when there was nothing to convert.</returns>
    public JobViewModel? Start(IReadOnlyList<string> files, Preset preset, bool showInToast = true)
    {
        if (files.Count == 0)
            return null;
        var job = _queue.Enqueue(files, preset, _loc.DisplayName(preset));
        var vm = new JobViewModel(job, _loc, _platform, Remove);
        _byId[job.Id] = vm;
        Jobs.Insert(0, vm);
        if (showInToast)
            ToastJobs.Insert(0, vm);
        return vm;
    }

    /// <summary>Moves a job into the toast window (no-op if it is already there or gone).</summary>
    public void ShowInToast(JobViewModel vm)
    {
        if (Jobs.Contains(vm) && !ToastJobs.Contains(vm))
            ToastJobs.Insert(0, vm);
    }

    private void OnJobUpdated(object? sender, ConversionJob job)
    {
        // Called from worker threads, possibly many times per second: coalesce into one UI update.
        if (_byId.TryGetValue(job.Id, out var vm) && vm.TryMarkPending())
            Dispatcher.UIThread.Post(vm.Refresh, DispatcherPriority.Background);
    }

    private void OnJobFinished(object? sender, ConversionJob job)
    {
        _store.AddHistory(HistoryEntry.From(job));
        Dispatcher.UIThread.Post(() =>
        {
            if (!_byId.TryGetValue(job.Id, out var vm))
                return;
            vm.Refresh();
            var keep = vm.HasErrors ? KeepErrors : KeepSuccess;
            DispatcherTimer.RunOnce(() => Remove(vm), keep);
            if (!vm.HasErrors && CountSuccess(job))
                DispatcherTimer.RunOnce(() => FeedbackDue?.Invoke(this, EventArgs.Empty), keep + TimeSpan.FromSeconds(1));
        });
    }

    /// <summary>Counts a fully successful conversion; true when the feedback card is due now (only once).</summary>
    private bool CountSuccess(ConversionJob job)
    {
        var settings = _store.Settings;
        if (settings.FeedbackPromptShown || job.Files.Count == 0 || job.Files.Any(f => f.State != FileState.Done))
            return false;
        settings.SuccessfulConversions++;
        settings.FeedbackPromptShown = settings.SuccessfulConversions >= FeedbackAfterConversions;
        _store.SaveSettings();
        return settings.FeedbackPromptShown;
    }

    private void Remove(JobViewModel vm)
    {
        Jobs.Remove(vm);
        ToastJobs.Remove(vm);
        _byId.TryRemove(vm.Job.Id, out _);
    }
}
