// Home: drop zone (handled in the view) and recent conversions.

using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.History;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Settings;

namespace Filee.App.ViewModels.Pages;

public sealed partial class HistoryItemViewModel : ObservableObject
{
    private readonly HistoryEntry _entry;
    private readonly ILocalizer _loc;
    private readonly IPlatformServices _platform;
    private readonly ConversionService? _conversions;
    private readonly UserDataStore? _store;

    /// <summary>The first output still on disk (the one the buttons open), or null.</summary>
    private readonly string? _output;

    public HistoryItemViewModel(HistoryEntry entry, ILocalizer loc, IPlatformServices platform, ConversionService? conversions = null,
        UserDataStore? store = null)
    {
        _entry = entry;
        _loc = loc;
        _platform = platform;
        _conversions = conversions;
        _store = store;
        Title = NameOf(entry, loc);
        Subtitle = entry.Sources.Count == 1 ? Path.GetFileName(entry.Sources[0]) : loc.Format("home.files", entry.Sources.Count);
        Time = entry.FinishedAt.Date == DateTime.Today ? entry.FinishedAt.ToString("t") : entry.FinishedAt.ToString("d");
        Failures = entry.Failures.Count > 0
            ? entry.Failures.Select(f => new FailureItem(Path.GetFileName(f.Source), FailureText.Reason(loc, f.ErrorKey, f.ErrorDetail))).ToList()
            : entry.Errors.Select(line => FailureText.FromLegacy(loc, line)).ToList();
        HasErrors = Failures.Count > 0 || entry.State == JobState.Failed;
        FailureSummary = loc.Format("home.failed_files", Failures.Count);
        _output = entry.Outputs.FirstOrDefault(ConversionService.Exists);
        HasOutputs = _output is not null;
        // Only a file of a format Filee knows is opened (never a program a changed history could point to); an
        // output folder ("Extract") opens in the file manager.
        CanOpen = _output is not null && (FormatRegistry.Detect(_output) is not null || Directory.Exists(_output));
        CanOpenWith = CanOpen && platform.SupportsOpenWith && !Directory.Exists(_output!);

        // The card shows the result: its picture when it is one, else its format. A job without results shows a source.
        var shown = _output ?? entry.Outputs.FirstOrDefault() ?? entry.Sources.FirstOrDefault();
        var extension = shown is null ? "" : Path.GetExtension(shown).TrimStart('.').ToUpperInvariant();
        FormatBadge = extension.Length > 4 ? extension[..4] : extension;
        var picture = _output ?? entry.Sources.FirstOrDefault(s => HistoryThumbnails.CanShow(s) && ConversionService.Exists(s));
        ThumbnailSource = picture is not null && HistoryThumbnails.CanShow(picture) ? picture : null;
        // Only entries that remember their preset (Filee 1.8 and later) and still have a failed file to convert.
        CanRetry = conversions is not null && entry.Preset is not null && entry.Failures.Any(f => ConversionService.Exists(f.Source));
        CanUndo = store is not null && UndoLeft();
        UndoneText = entry.Undone ? loc[entry.OriginalsInTrash ? "home.undone_original" : "home.undone"] : "";
    }

    /// <summary>The preset's name in the language Filee is in now when it is a built-in one, else as it was saved.</summary>
    internal static string NameOf(HistoryEntry entry, ILocalizer loc) =>
        entry.PresetNameKey is { } key && loc[key] is var name && name != key ? name : entry.PresetName;

    /// <summary>
    /// Undo (#553) is offered while the results not moved yet are all as the job left them, so a file changed since is
    /// never moved; after an Undo that stopped half-way, for the ones it didn't get to.
    /// </summary>
    private bool UndoLeft() =>
        !_entry.Undone && _entry.Undoable.Where(o => !o.Trashed).ToList() is { Count: > 0 } left && left.All(o => o.Unchanged());

    public string Title { get; }
    public string Subtitle { get; }
    public string Time { get; }
    public bool HasErrors { get; }
    public bool HasOutputs { get; }

    /// <summary>"Open file": the output is a file of a format Filee knows, or an output folder.</summary>
    public bool CanOpen { get; }

    /// <summary>"Open with": an output file, on a system with an "Open with" dialog.</summary>
    public bool CanOpenWith { get; }

    /// <summary>The picture the card shows, loaded in the background (<see cref="HomePageViewModel"/>); null until then.</summary>
    internal string? ThumbnailSource { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasThumbnail))] private Bitmap? _thumbnail;

    public bool HasThumbnail => Thumbnail is not null;

    /// <summary>The result's extension ("PNG"), shown when there is no picture; empty when it has none.</summary>
    public string FormatBadge { get; }

    public bool HasFormatBadge => FormatBadge.Length > 0;

    /// <summary>The files that failed and why.</summary>
    public IReadOnlyList<FailureItem> Failures { get; }

    public bool HasFailures => Failures.Count > 0;

    /// <summary>"2 files failed": the button that shows the list.</summary>
    public string FailureSummary { get; }

    public bool CanRetry { get; }

    [ObservableProperty] private bool _showFailures;

    [RelayCommand]
    private void ToggleFailures() => ShowFailures = !ShowFailures;

    /// <summary>Converts the failed files again the way the job ran them; progress shows in the toast.</summary>
    [RelayCommand]
    private void Retry()
    {
        if (_conversions is not null && _entry.Preset is not null)
            _conversions.Retry(_entry.Failures.Select(f => f.Source), _entry.Preset, _entry.WatchRuleId);
    }

    [ObservableProperty] private bool _canUndo;

    /// <summary>"Undone: the results are in the Recycle Bin / Trash", or empty.</summary>
    public string UndoneText { get; }

    public bool IsUndone => UndoneText.Length > 0;

    /// <summary>Why a result couldn't be moved to the Recycle Bin / Trash, or empty.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasUndoError))] private string _undoError = "";

    public bool HasUndoError => UndoError.Length > 0;

    /// <summary>
    /// Moves the job's results to the Recycle Bin / Trash, off the UI thread (Windows' Recycle Bin takes a moment per
    /// file); the button is disabled meanwhile. Every result is checked first, so a changed one stops it with nothing
    /// moved. Each result moved is saved as such, so an Undo the Trash stops half-way can be tried again for the rest.
    /// "Replace the original" sent the originals there already: they stay, and the card says so.
    /// </summary>
    [RelayCommand]
    private async Task UndoAsync()
    {
        if (!CanUndo || _store is null)
            return;
        UndoError = "";
        var left = _entry.Undoable.Where(o => !o.Trashed).ToList();
        if (left.FirstOrDefault(o => !o.Unchanged()) is { } changed)
        {
            Refused(changed, new TrashRefusal("trash.changed"));
            return;
        }
        var refused = await Task.Run(() =>
        {
            foreach (var output in left)
            {
                if (_platform.MoveToTrash(output.Path) is { } refusal)
                    return (output, refusal);
                output.Trashed = true;
            }
            return default((HistoryOutput, TrashRefusal)?);
        });
        if (refused is { } stop)
        {
            _store.SaveHistory(notify: false); // the moved ones stay moved; this card says why it stopped
            Refused(stop.Item1, stop.Item2);
            return;
        }
        _entry.Undone = true;
        _store.SaveHistory();
    }

    private void Refused(HistoryOutput output, TrashRefusal refusal)
    {
        UndoError = _loc.Format("home.undo_failed", Path.GetFileName(output.Path), _loc[refusal.ReasonKey]);
        CanUndo = UndoLeft();
    }

    [RelayCommand]
    private void OpenFolder()
    {
        // An output may be a folder ("Extract"): revealing it opens the extracted files.
        if (_output is not null && ConversionService.Exists(_output))
            _platform.RevealInFileManager(_output);
    }

    [RelayCommand]
    private void OpenFile()
    {
        if (CanOpen && ConversionService.Exists(_output!))
            _platform.OpenFile(_output!);
    }

    [RelayCommand]
    private void OpenWith()
    {
        if (CanOpenWith && File.Exists(_output))
            _platform.OpenWith(_output!);
    }
}

public sealed partial class HomePageViewModel : ObservableObject, IDisposable
{
    private readonly UserDataStore _store;
    private readonly ILocalizer _loc;
    private readonly IPlatformServices _platform;
    private readonly Services.Triggers.TriggerService? _triggers;
    private readonly ConversionService? _conversions;
    private readonly ElevationService? _elevation;
    private readonly FileQueue _queue = new();

    /// <summary>The queued files sent to the donut, until their conversion is in the history (<see cref="FinishSentQueue"/>).</summary>
    private List<string>? _sentToDonut;

    public HomePageViewModel(UserDataStore store, ILocalizer loc, IPlatformServices platform, Services.Triggers.TriggerService? triggers = null,
        ConversionService? conversions = null, ElevationService? elevation = null)
    {
        _store = store;
        _loc = loc;
        _platform = platform;
        _triggers = triggers;
        _conversions = conversions;
        _elevation = elevation;
        if (triggers is not null)
            triggers.StatusChanged += OnTriggerStatusChanged;
        if (elevation is not null)
            elevation.PropertyChanged += OnElevationChanged;
        var drag = store.Settings.Triggers.FirstOrDefault(t => t.Enabled && t.Kind == TriggerKind.Drag);
        DropHint = loc.Format(OperatingSystem.IsMacOS() ? "home.drop_hint_macos" : OperatingSystem.IsLinux() ? "home.drop_hint_linux" : "home.drop_hint", drag is null ? "—" : GestureText.Modifiers(drag.Modifiers));
        store.HistoryChanged += OnHistoryChanged;
        Queue.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasQueue));
        Reload();
    }

    private void OnHistoryChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        FinishSentQueue();
        Reload();
    });

    public void Dispose()
    {
        _thumbnails?.Cancel();
        _store.HistoryChanged -= OnHistoryChanged;
        foreach (var card in Queue)
            card.Dispose();
        if (_triggers is not null)
            _triggers.StatusChanged -= OnTriggerStatusChanged;
        if (_elevation is not null)
            _elevation.PropertyChanged -= OnElevationChanged;
    }

    private void OnTriggerStatusChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(NeedsAccessibility));

    private void OnElevationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        OnPropertyChanged(nameof(ElevationRestartFailed));

    /// <summary>
    /// Windows: Filee runs as administrator while Explorer doesn't, so drops, the Explorer menu and the shortcut can't
    /// reach it (the warning above the drop zone).
    /// </summary>
    public bool IsElevated => _elevation?.ShellCannotReachFilee == true;

    /// <summary>The restart button didn't work: the warning says what to do instead.</summary>
    public bool ElevationRestartFailed => _elevation?.RestartFailed == true;

    [RelayCommand]
    private void RestartWithoutAdministratorRights() => _elevation?.RestartWithoutAdministratorRights();

    /// <summary>macOS: gestures wait for the Accessibility permission (the reminder above the drop zone).</summary>
    public bool NeedsAccessibility => _triggers?.NeedsPermission == true;

    [RelayCommand]
    private void OpenAccessibility()
    {
        if (OperatingSystem.IsMacOS() && _platform is Filee.Platform.MacOS.MacOSPlatformServices mac)
            mac.OpenAccessibilitySettings();
    }

    public string DropHint { get; }

    /// <summary>
    /// The files waiting to be converted together, in the order they are converted or merged in. Files are added by
    /// drops and the file picker (<see cref="AddToQueue"/>).
    /// </summary>
    public ObservableCollection<StagedFileViewModel> Queue { get; } = [];

    public bool HasQueue => Queue.Count > 0;

    /// <summary>The queued files' paths in the order they are converted.</summary>
    public IReadOnlyList<string> QueuedPaths => _queue.Paths.ToArray();

    /// <summary>Adds dropped or picked files to the end of the queue.</summary>
    public void AddToQueue(IEnumerable<string> paths)
    {
        _queue.Add(paths);
        SyncQueue();
    }

    /// <summary>Moves a queued file, from the user dragging its card.</summary>
    public void MoveQueued(int from, int to)
    {
        _queue.Move(from, to);
        Queue.Move(from, to);
        Renumber();
    }

    /// <summary>The card's X, or the Delete key: takes its file out of the queue.</summary>
    internal void RemoveFromQueue(StagedFileViewModel card)
    {
        _queue.Remove(card.Path);
        SyncQueue();
    }

    /// <summary>A click on a card selects it (only that one); the Delete key takes the selected file out of the queue.</summary>
    public void SelectQueued(int index)
    {
        for (var i = 0; i < Queue.Count; i++)
            Queue[i].IsSelected = i == index;
    }

    /// <summary>Alt+Up / Alt+Down: the selected card moves one place earlier or later, and stays selected.</summary>
    public void MoveSelectedQueued(int delta)
    {
        var index = Queue.ToList().FindIndex(card => card.IsSelected);
        if (index < 0 || index + delta < 0 || index + delta >= Queue.Count)
            return;
        MoveQueued(index, index + delta);
    }

    /// <summary>The left and right arrows: the selection moves to the neighbouring card.</summary>
    public void SelectNeighbour(int delta)
    {
        if (Queue.Count == 0)
            return;
        var index = Queue.ToList().FindIndex(card => card.IsSelected);
        SelectQueued(Math.Clamp(index < 0 ? 0 : index + delta, 0, Queue.Count - 1));
    }

    /// <summary>The Delete key: takes the selected file out of the queue, if one is selected.</summary>
    public void RemoveSelectedQueued()
    {
        if (Queue.FirstOrDefault(card => card.IsSelected) is { } selected)
            RemoveFromQueue(selected);
    }

    /// <summary>
    /// The queue's files went to the donut. The queue empties once Filee has converted them, not before: a cancelled
    /// donut keeps the files.
    /// </summary>
    public void MarkSentToDonut() => _sentToDonut = [.. _queue.Paths];

    /// <summary>
    /// Empties the queue when a finished conversion (not a cancelled one) of exactly the files sent to the donut is in
    /// the history. Such an entry lists all of them as its sources.
    /// </summary>
    private void FinishSentQueue()
    {
        if (_sentToDonut is not { Count: > 0 } sent)
            return;
        var finished = _store.History.Any(entry => entry.State != JobState.Cancelled
                                                   && sent.All(path => entry.Sources.Contains(path, FileSystemPaths.Comparer)));
        if (!finished)
            return;
        _sentToDonut = null;
        ClearQueue();
    }

    /// <summary>Empties the queue: the "Clear list" button, and once its files were converted.</summary>
    [RelayCommand]
    public void ClearQueue()
    {
        _queue.Clear();
        foreach (var card in Queue)
            card.Dispose();
        Queue.Clear();
    }

    /// <summary>Brings the cards in line with the queue: cards of files that left it go, new files get a card at the end.</summary>
    private void SyncQueue()
    {
        foreach (var card in Queue.Where(card => !_queue.Paths.Contains(card.Path, FileSystemPaths.Comparer)).ToList())
        {
            Queue.Remove(card);
            card.Dispose();
        }
        foreach (var path in _queue.Paths)
        {
            if (Queue.Any(card => FileSystemPaths.Comparer.Equals(card.Path, path)))
                continue;
            var format = FormatRegistry.Detect(path);
            var card = new StagedFileViewModel(path, format, RemoveFromQueue);
            Queue.Add(card);
            _ = LoadThumbnailAsync(card, format);
        }
        Renumber();
    }

    private void Renumber()
    {
        for (var i = 0; i < Queue.Count; i++)
            Queue[i].Position = i + 1;
    }

    /// <summary>The picture of a card, made off the UI thread; a card that left the queue meanwhile gets none.</summary>
    private async Task LoadThumbnailAsync(StagedFileViewModel card, FileFormat? format)
    {
        Bitmap? bitmap = null;
        try
        {
            bitmap = await Task.Run(() => StagedFileViewModel.MakeThumbnail(card.Path, format));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No picture: the card shows its extension instead.
        }
        if (Queue.Contains(card))
            card.Thumbnail = bitmap;
        else if (format?.Category == FormatCategory.Pdf)
            bitmap?.Dispose();
    }

    /// <summary>How many conversions the page lists: the newest, or the newest that match the search.</summary>
    internal const int Shown = 40;

    /// <summary>The conversions listed (each looks for its outputs on disk, so only <see cref="Shown"/> of them).</summary>
    public ObservableCollection<HistoryItemViewModel> History { get; } = [];

    /// <summary>
    /// What was typed in the search box: the list shows the conversions whose preset or a source file name contains
    /// it, from the whole history (<see cref="AppSettings.HistoryLimit"/>), not just the ones listed without it.
    /// </summary>
    [ObservableProperty] private string _search = "";

    partial void OnSearchChanged(string value) => Reload();

    /// <summary>No history at all: no search box, and the hint that converted files show up here.</summary>
    [ObservableProperty] private bool _isEmpty;

    /// <summary>A search that matches nothing.</summary>
    [ObservableProperty] private bool _noMatches;

    private void Reload()
    {
        var query = Search.Trim();
        // The entries are picked first, then built: building one looks for its outputs on disk.
        var entries = _store.History
            .Where(entry => query.Length == 0 || Matches(entry, query, _loc))
            .Take(Shown)
            .ToList();
        History.Clear();
        foreach (var entry in entries)
            History.Add(new HistoryItemViewModel(entry, _loc, _platform, _conversions, _store));
        IsEmpty = _store.History.Count == 0;
        NoMatches = !IsEmpty && History.Count == 0;
        LoadThumbnails();
    }

    private CancellationTokenSource? _thumbnails;

    /// <summary>The task loading the thumbnails of the conversions listed (tests wait for it).</summary>
    internal Task ThumbnailsLoaded { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Shows the thumbnails made before at once, then reads the pictures one after another off the UI thread (a
    /// changed file gets a new one). A new list (a search, a new conversion) stops the old one.
    /// </summary>
    private void LoadThumbnails()
    {
        _thumbnails?.Cancel();
        var items = History.Where(item => item.ThumbnailSource is not null).ToList();
        foreach (var item in items)
            item.Thumbnail = HistoryThumbnails.Cached(item.ThumbnailSource!);
        if (items.Count == 0)
        {
            ThumbnailsLoaded = Task.CompletedTask;
            return;
        }

        var cancel = _thumbnails = new CancellationTokenSource();
        var token = cancel.Token;
        ThumbnailsLoaded = Task.Run(() =>
        {
            foreach (var item in items)
            {
                if (token.IsCancellationRequested)
                    return;
                var bitmap = HistoryThumbnails.Load(item.ThumbnailSource!);
                Dispatcher.UIThread.Post(() =>
                {
                    if (!token.IsCancellationRequested)
                        item.Thumbnail = bitmap;
                });
            }
        }, token);
    }

    private static bool Matches(HistoryEntry entry, string query, ILocalizer loc)
    {
        return Contains(HistoryItemViewModel.NameOf(entry, loc)) || Contains(entry.PresetName) || entry.Sources.Any(source => Contains(Path.GetFileName(source)));

        bool Contains(string? text) => text?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true;
    }

    [RelayCommand]
    private void ClearHistory()
    {
        Search = "";
        _store.ClearHistory();
        Reload();
    }
}
