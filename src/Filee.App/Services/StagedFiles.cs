// The files waiting on the Home page to be converted together, in the order they are converted or merged in. It lives as
// long as the app, so the list survives a change of page or language. The list empties only when the conversion it was
// sent to completes; a conversion that fails keeps the list and names the files that failed.

using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Filee.App.ViewModels.Pages;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Platform;

namespace Filee.App.Services;

public sealed partial class StagedFiles : ObservableObject, IDisposable
{
    private readonly FileQueue _queue = new();
    private readonly JobQueue _jobs;
    private readonly ILocalizer _loc;

    /// <summary>The cards sent to the donut, until the conversion the donut starts takes them.</summary>
    private List<StagedFileViewModel> _sent = [];

    /// <summary>The cards of each conversion started from the donut, by job: each leaves the list only with its own job.</summary>
    private readonly Dictionary<string, List<StagedFileViewModel>> _startedJobs = [];

    public StagedFiles(JobQueue jobs, ILocalizer loc)
    {
        _jobs = jobs;
        _loc = loc;
        _jobs.JobFinished += OnJobFinished;
    }

    /// <summary>The cards, in the order the files are converted or merged in.</summary>
    public ObservableCollection<StagedFileViewModel> Cards { get; } = [];

    /// <summary>The files' paths in the order they are converted.</summary>
    public IReadOnlyList<string> Paths => _queue.Paths.ToArray();

    /// <summary>Why the last conversion of the list failed, naming its files; empty when there is nothing to report.</summary>
    [ObservableProperty]
    private string _error = "";

    /// <summary>Adds the files to the end of the list.</summary>
    public void Add(IEnumerable<string> paths)
    {
        _queue.Add(paths);
        Sync();
    }

    /// <summary>Moves a file, from the user dragging its card or the keyboard.</summary>
    public void Move(int from, int to)
    {
        _queue.Move(from, to);
        Cards.Move(from, to);
        Renumber();
    }

    /// <summary>Takes a card's file out of the list.</summary>
    public void Remove(StagedFileViewModel card)
    {
        _queue.Remove(card.Path);
        Sync();
    }

    /// <summary>Empties the list.</summary>
    public void Clear()
    {
        _queue.Clear();
        foreach (var card in Cards)
            card.Dispose();
        Cards.Clear();
        Error = "";
    }

    /// <summary>The cards were sent to the donut: they leave the list when the conversion the donut starts completes.</summary>
    public void MarkSent(IReadOnlyList<StagedFileViewModel> cards)
    {
        _sent = cards.ToList();
        Error = "";
    }

    /// <summary>
    /// A conversion started from the donut takes the cards sent to it. Cards sent while another conversion runs wait in the
    /// list for their own conversion; the running one still clears only its own cards.
    /// </summary>
    public void ConversionStarted(ConversionJob job)
    {
        if (_sent.Count == 0)
            return;
        _startedJobs[job.Id] = _sent;
        _sent = [];
    }

    /// <summary>The place of each card follows the list.</summary>
    private void Renumber()
    {
        for (var i = 0; i < Cards.Count; i++)
        {
            Cards[i].Position = i + 1;
            Cards[i].SetSize = Cards.Count;
        }
    }

    /// <summary>Brings the cards in line with the list: cards of files that left it go, new files get a card at the end.</summary>
    private void Sync()
    {
        foreach (var card in Cards.Where(card => !_queue.Paths.Contains(card.Path, FileSystemPaths.Comparer)).ToList())
        {
            Cards.Remove(card);
            _sent.Remove(card);
            foreach (var started in _startedJobs.Values)
                started.Remove(card);
            card.Dispose();
        }
        foreach (var path in _queue.Paths)
        {
            if (Cards.Any(card => FileSystemPaths.Comparer.Equals(card.Path, path)))
                continue;
            var card = new StagedFileViewModel(path, FormatRegistry.Detect(path), Remove, _loc);
            Cards.Add(card);
            _ = card.LoadPreviewAsync();
        }
        Renumber();
    }

    private void OnJobFinished(object? sender, ConversionJob job) => Dispatcher.UIThread.Post(() => Finished(job));

    private void Finished(ConversionJob job)
    {
        if (!_startedJobs.Remove(job.Id, out var sent))
            return;
        if (job.State == JobState.Cancelled)
            return;
        if (job.State == JobState.Completed)
        {
            foreach (var card in sent.Where(card => Cards.Contains(card)))
                Remove(card);
            return;
        }
        var failed = job.Files.Where(file => file.State == FileState.Failed).Select(file => Path.GetFileName(file.SourcePath));
        Error = _loc.Format("home.queue_error", string.Join(", ", failed));
    }

    public void Dispose()
    {
        _jobs.JobFinished -= OnJobFinished;
    }
}
