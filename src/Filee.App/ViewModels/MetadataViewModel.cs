// State of the "Metadata" window: one file at a time, every field it carries grouped by where it lives (GPS, IFD0,
// XMP-dc, a PDF's document info …), searchable; with ExifTool, for a kind of file it writes, each value can be edited,
// removed or added and Remove location / Remove everything clear many at once. Saving starts an ordinary conversion
// (MetadataTool) that writes only the metadata, into a copy or in the original's place, shown in the toast. Without
// ExifTool the window offers it and reads nothing: a file can make a reader inside Filee hang or fill the memory, and
// ExifTool runs apart, under limits, and stops when the window closes.

using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Engines.Metadata;
using Microsoft.Extensions.Logging;

namespace Filee.App.ViewModels;

/// <summary>How the window reads metadata; tests stand in for ExifTool.</summary>
public sealed class MetadataEngines
{
    /// <summary>
    /// ExifTool's program, looked for again each time (it may have been installed since); null without it.
    /// </summary>
    public Func<string?> Locate { get; init; } = ExifTool.Locate;

    /// <summary>The ExifTool found can't run: it is a Perl script, on a computer without Perl.</summary>
    public Func<string, bool> NeedsPerl { get; init; } = ExifTool.NeedsPerl;

    public Func<string, string, CancellationToken, Task<IReadOnlyList<MetadataField>>> Read { get; init; } = ExifTool.ReadAsync;
}

public sealed partial class MetadataViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// The most fields opened, as every row costs memory and time on the UI thread: 2,000 take about 4 s and most of a
    /// GB. Six times the richest of ExifTool's own sample files, but a TIFF of about 200 pages is over it, and doesn't
    /// open: rows laid out as they scroll into view would let this go up.
    /// </summary>
    internal const int FieldLimit = 3000;

    /// <summary>
    /// The most text the rows may show together, as all of it is laid out: mixed scripts take 10 µs a character or so.
    /// Ten times what the richest of ExifTool's own sample files prints.
    /// </summary>
    internal const int ShownTextLimit = 256 * 1024;

    /// <summary>
    /// The longest "Group:Tag" opened. A file can name a field whatever it likes, the richest of ExifTool's own sample
    /// files stops at 49 characters, and the place rules take time that grows with the square of it.
    /// </summary>
    internal const int LongestName = 256;

    /// <summary>The fields out of reach of Remove location its notice names: a file can have any number.</summary>
    private const int NamesInNotice = 12;

    /// <summary>Groups with a name of their own, in the order shown; any other comes after them, File last.</summary>
    private static readonly (string Group, string Key)[] KnownGroups =
    [
        ("GPS", "metadata.group.location"), ("IFD0", "metadata.group.camera"), ("ExifIFD", "metadata.group.exposure"),
        ("XMP-dc", "metadata.group.description"), ("IPTC", "metadata.group.keywords"), ("PDF", "metadata.group.document"),
    ];

    /// <summary>Fields Add field offers, by the group they go in.</summary>
    internal static readonly IReadOnlyDictionary<string, string[]> AddableFields = new Dictionary<string, string[]>
    {
        ["XMP-dc"] = ["Title", "Description", "Creator", "Subject", "Rights"],
        ["IPTC"] = ["Keywords", "ObjectName", "Caption-Abstract", "By-line", "CopyrightNotice"],
        ["PDF"] = ["Title", "Author", "Subject", "Keywords"],
    };

    /// <summary>
    /// The kinds of file IPTC is written into; every other one gets XMP (or, a PDF, its document info).
    /// </summary>
    private static readonly HashSet<string> IptcFormats = ["jpg", "tiff", "psd"];

    private readonly IReadOnlyList<string> _files;
    private readonly Preset _tool;
    private readonly ILocalizer _loc;
    private readonly UserDataStore _store;
    private readonly MetadataEngines _engines;
    private readonly Action<string, Preset> _export;
    private readonly ILogger? _log;
    private List<MetadataRowViewModel> _rows = [];
    private string? _formatId;
    private bool _closed;
    private readonly CancellationTokenSource _closing = new();

    /// <summary>Above this many rows search waits for a pause in the typing: each search lays them all out.</summary>
    private const int RowsArrangedAtOnce = 500;

    private int _keysTyped;

    /// <summary>How each file went: saved, skipped or failed (none yet while it is open).</summary>
    private readonly CropItemState?[] _outcomes;

    /// <summary>The file each of <see cref="Problems"/> is about.</summary>
    private readonly List<int> _problemFiles = [];

    public MetadataViewModel(IReadOnlyList<string> files, Preset tool, ILocalizer loc, UserDataStore store, MetadataEngines engines,
        Action<string, Preset> export, ILogger? log = null)
    {
        _files = files;
        _tool = tool;
        _loc = loc;
        _store = store;
        _engines = engines;
        _export = export;
        _log = log;
        _outcomes = new CropItemState?[files.Count];
        _hasMultipleFiles = files.Count > 1;
        _replaceOriginal = MetadataTool.ReplacesOriginal(tool);
        loc.LanguageChanged += OnLanguageChanged;
        _ = LoadFileAsync(0);
    }

    [ObservableProperty] private int _currentIndex;
    [ObservableProperty] private string _currentFileName = "";
    [ObservableProperty] private string _details = "";
    [ObservableProperty] private string _counterText = "";
    [ObservableProperty] private bool _hasMultipleFiles;
    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private string _statusNotice = "";
    [ObservableProperty] private bool _isPdf;

    /// <summary>
    /// What the file holds beyond the fields shown, when it does (<see cref="ExifTool.HoldsMore"/>): "" otherwise.
    /// </summary>
    [ObservableProperty] private string _embeddedNotice = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadOnly), nameof(ReadOnlyNotice))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RemoveLocationCommand), nameof(RemoveAllCommand))]
    private bool _canEdit;

    /// <summary>
    /// ExifTool is there but doesn't write this kind of file (MP3, MKV, BMP …): shown read-only all the same.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReadOnlyNotice))]
    private bool _isUnwritable;

    /// <summary>
    /// No ExifTool, or one that can't run: nothing to show until it is installed.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReadOnlyNotice), nameof(CanInstall), nameof(IsEmpty))]
    private bool _needsExifTool;

    /// <summary>ExifTool is there as a Perl script, and no Perl to run it: installing it again wouldn't help.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReadOnlyNotice), nameof(CanInstall))]
    private bool _lacksPerl;

    /// <summary>Location that Remove location leaves in the file; empty when none is known of.</summary>
    [ObservableProperty]
    private string _locationNotice = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RemoveLocationCommand), nameof(RemoveAllCommand))]
    private bool _isBusy;

    /// <summary>
    /// "Replace the original": the copy takes its place and the original goes to the Recycle Bin / Trash.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveText))]
    private bool _replaceOriginal;

    [ObservableProperty] private string _searchText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges), nameof(ChangesText))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private int _changeCount;

    /// <summary>
    /// All files are done and some could not be opened or saved: the window stays open on <see cref="Summary"/> and
    /// <see cref="Problems"/> until it is closed (as "Pixelate / blur" does).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    private bool _isFinished;

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private int _doneCount;
    [ObservableProperty] private int _skippedCount;
    [ObservableProperty] private int _failedCount;

    /// <summary>"file: reason" for each file that could not be opened or saved.</summary>
    public ObservableCollection<string> Problems { get; } = [];

    public bool IsEditing => !IsFinished;

    /// <summary>The groups shown (those with a row matching the search).</summary>
    public ObservableCollection<MetadataGroupViewModel> Groups { get; } = [];

    public bool IsEmpty => Groups.Count == 0 && !NeedsExifTool;
    public bool IsReadOnly => !CanEdit;
    public bool HasChanges => ChangeCount > 0;
    public string ChangesText => ChangeCount == 1 ? _loc["metadata.changes_one"] : _loc.Format("metadata.changes", ChangeCount);

    /// <summary>
    /// Why the fields can't be changed, while they can't: no ExifTool, or a kind of file it doesn't write.
    /// </summary>
    public string ReadOnlyNotice => CanEdit ? "" : _loc[LacksPerl ? "metadata.needs_perl" : NeedsExifTool ? "metadata.needs_exiftool" : "metadata.unwritable"];

    /// <summary>Install ExifTool is offered: it would show the fields.</summary>
    public bool CanInstall => NeedsExifTool && !LacksPerl;

    public string SaveText => _loc[ReplaceOriginal ? "metadata.save" : CurrentIndex < _files.Count - 1 ? "metadata.save_next" : "metadata.save_copy"];

    /// <summary>Remove location applies: it was pressed, and every field it takes is still struck through.</summary>
    public bool RemovesLocation => _removeLocationAsked && !_rows.Any(r => r.CanEdit && ExifTool.IsLocation(r.Field) && !r.IsRemoved);

    /// <summary>Remove everything applies: it was pressed, and every field it takes is still struck through.</summary>
    public bool RemovesAll => _removeAllAsked && !_rows.Any(r => r.CanEdit && !r.IsAdded && !ExifTool.KeptByRemoveAll(r.Field) && !r.IsRemoved);

    private bool _holdsMore;
    private IReadOnlyList<string> _outOfReach = [];
    private bool _isVideo;
    private bool _removeLocationAsked;
    private bool _removeAllAsked;

    /// <summary>Raised when the last file is done, or on close.</summary>
    public event Action? CloseRequested;

    /// <summary>Opens Settings → Engines (Install ExifTool).</summary>
    public event Action? InstallRequested;

    partial void OnSearchTextChanged(string value)
    {
        if (_rows.Count <= RowsArrangedAtOnce)
            Arrange();
        else
            _ = ArrangeAfterAPauseAsync();
    }

    private async Task ArrangeAfterAPauseAsync()
    {
        var typed = ++_keysTyped;
        await Task.Delay(300);
        if (typed == _keysTyped && !_closed)
            Arrange();
    }

    partial void OnReplaceOriginalChanged(bool value) => Remember();

    // ───────── Files ─────────

    /// <summary>
    /// Opens file <paramref name="index"/>; past the last one, closes, or shows the summary when a file could not be
    /// opened or saved.
    /// </summary>
    public async Task LoadFileAsync(int index)
    {
        if (_closed)
            return;
        if (index >= _files.Count)
        {
            Finish();
            return;
        }
        IsBusy = true;
        CurrentIndex = index;
        var path = _files[index];
        CurrentFileName = Path.GetFileName(path);
        CounterText = _loc.Format("crop.page_counter", index + 1, _files.Count);
        _formatId = FormatRegistry.Detect(path)?.Id;
        IsPdf = _formatId == "pdf";
        OnPropertyChanged(nameof(SaveText));
        _removeLocationAsked = _removeAllAsked = false;
        SearchText = "";
        // HistoryThumbnails' own, shared with Home: never disposed here.
        Thumbnail = HistoryThumbnails.CanShow(path) ? await Task.Run(() => HistoryThumbnails.Load(path)) : null;
        if (_closed)
            return;

        var exifTool = _engines.Locate();
        var lacksPerl = exifTool is not null && _engines.NeedsPerl(exifTool);
        if (lacksPerl)
            exifTool = null;
        IReadOnlyList<MetadataField> fields = [];
        try
        {
            if (exifTool is not null)
                fields = await _engines.Read(exifTool, path, _closing.Token);
            if (fields.Count > FieldLimit || fields.Any(f => f.Key.Length > LongestName)
                || fields.Sum(f => (long)Math.Min(f.Items.Sum(item => item.Length), MetadataRowViewModel.LongestShown)) > ShownTextLimit)
                throw new MetadataTooLargeException("More than the window opens.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (_closed)
                return;
            _log?.LogInformation(ex, "Could not read the metadata of {File}", CurrentFileName);
            AddProblem(index, ex is MetadataTooLargeException ? "metadata.too_much" : "metadata.unreadable");
            IsBusy = false;
            await LoadFileAsync(index + 1);
            return;
        }
        if (_closed)
            return;
        IsUnwritable = !ExifTool.WritesFile(path);
        NeedsExifTool = exifTool is null;
        LacksPerl = lacksPerl;
        _holdsMore = ExifTool.HoldsMore(fields);
        EmbeddedNotice = _holdsMore ? _loc.Format("metadata.embedded", _loc["preset.remove_metadata"]) : "";
        CanEdit = exifTool is not null && !IsUnwritable;
        _outOfReach = CanEdit ? ExifTool.LocationOutOfReach(fields) : [];
        _isVideo = CanEdit && FormatRegistry.Detect(path)?.Category == FormatCategory.Video;
        ShowLocationNotice();
        _rows = [.. fields.Where(f => !ExifTool.NotInTheFile.Contains(f.Group)).Select(f => new MetadataRowViewModel(f, LabelOf(f.Tag), _loc, CanEdit && f.Editable,
            CanEdit && !f.Editable, OnRowChanged))];
        ShowDetails();
        Arrange();
        Recount();
        RemoveLocationCommand.NotifyCanExecuteChanged();
        RemoveAllCommand.NotifyCanExecuteChanged();
        IsBusy = false;
    }

    /// <summary>What saving won't take away: a "successful" Remove location must not read as a clean file.</summary>
    private void ShowLocationNotice() => LocationNotice = string.Join("\n", new[]
    {
        _outOfReach.Count > 0 ? _loc.Format("metadata.location_kept", string.Join(", ", _outOfReach.Take(NamesInNotice).Select(LabelOf))
                                                                      + (_outOfReach.Count > NamesInNotice ? ", …" : "")) : "",
        _isVideo ? _loc["metadata.location_tracks"] : "",
    }.Where(line => line.Length > 0));

    private void ShowDetails()
    {
        var path = _files[CurrentIndex];
        var size = new FileInfo(path) is { Exists: true } info ? EnginePackageState.FormatBytes(info.Length) : "";
        Details = string.Join(" · ", new[]
        {
            Path.GetExtension(path).TrimStart('.').ToUpperInvariant(), size, Path.GetFileName(Path.GetDirectoryName(path)) ?? "",
            _loc.Format("metadata.fields", _rows.Count(r => !r.IsAdded)),
        }.Where(part => part.Length > 0));
    }

    /// <summary>"DateTimeOriginal" → "Date Time Original", "GPSLatitude" → "GPS Latitude".</summary>
    internal static string LabelOf(string tag) => Regex.Replace(tag, "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");

    /// <summary>
    /// Groups the rows (known groups first, File last), leaving out those that don't match the search.
    /// </summary>
    private void Arrange()
    {
        _keysTyped++;
        // Clearing the groups can take the focus from a box, whose row then asks for this again: done once more after.
        if (_arranging)
        {
            _arrangeAgain = true;
            return;
        }
        _arranging = true;
        try
        {
            do
            {
                _arrangeAgain = false;
                ArrangeGroups();
            }
            while (_arrangeAgain);
        }
        finally
        {
            _arranging = false;
        }
    }

    private bool _arranging;
    private bool _arrangeAgain;

    private void ArrangeGroups()
    {
        var search = SearchText.Trim();
        bool Matches(MetadataRowViewModel row) => search.Length == 0
            || row.Label.Contains(search, StringComparison.CurrentCultureIgnoreCase)
            || row.Field.Tag.Contains(search, StringComparison.OrdinalIgnoreCase)
            || row.Value.AsSpan(0, Math.Min(row.Value.Length, MetadataRowViewModel.LongestEditable))
                .Contains(search, StringComparison.CurrentCultureIgnoreCase);
        int Order(string group, string family) =>
            Array.FindIndex(KnownGroups, k => k.Group == group) is >= 0 and var known ? known
            : family == "File" ? 1000 : 100;
        var addable = CanEdit ? AddableHere() : [];

        var arranged = new List<(int Order, string Group, MetadataGroupViewModel View)>();
        foreach (var group in _rows.GroupBy(r => (r.Field.Family, r.Field.Group)))
        {
            var rows = group.Where(Matches).ToList();
            if (rows.Count == 0)
                continue;
            arranged.Add((Order(group.Key.Group, group.Key.Family), group.Key.Group, new MetadataGroupViewModel(TitleOf(group.Key.Family, group.Key.Group),
                group.Key.Group, group.Key.Family, rows,
                warning: group.Key.Group == "GPS" ? _loc["metadata.location_warning"] : null,
                addable: addable.TryGetValue(group.Key.Group, out var tags) ? [.. tags.Where(tag => group.All(r => r.Field.Tag != tag))] : [],
                add: AddField)));
        }
        // Somewhere to add a title or keywords to a file that has none yet.
        foreach (var (groupName, tags) in addable)
        {
            if (search.Length > 0 || arranged.Any(a => a.Group == groupName))
                continue;
            arranged.Add((Order(groupName, ""), groupName, new MetadataGroupViewModel(TitleOf("", groupName), groupName, "", [], null, [.. tags], AddField)));
        }
        Groups.Clear();
        foreach (var (_, _, view) in arranged.OrderBy(a => a.Order).ThenBy(a => a.Group, StringComparer.Ordinal))
            Groups.Add(view);
        OnPropertyChanged(nameof(IsEmpty));
    }

    private string TitleOf(string family, string group) =>
        KnownGroups.FirstOrDefault(k => k.Group == group).Key is { } known ? _loc[known]
        : family == "File" ? _loc["metadata.group.file"] : group;

    /// <summary>
    /// The groups Add field offers for this kind of file: a PDF's document info, IPTC where it is written, XMP.
    /// </summary>
    private Dictionary<string, string[]> AddableHere() =>
        IsPdf ? new() { ["PDF"] = AddableFields["PDF"] }
        : _formatId is { } id && IptcFormats.Contains(id) ? new() { ["XMP-dc"] = AddableFields["XMP-dc"], ["IPTC"] = AddableFields["IPTC"] }
        : new() { ["XMP-dc"] = AddableFields["XMP-dc"] };

    private void AddField(MetadataGroupViewModel group, string tag)
    {
        // A search would hide the new row, and its box with it.
        SearchText = "";
        var family = group.Raw switch { "PDF" => "PDF", "IPTC" => "IPTC", _ => "XMP" };
        var key = $"{group.Raw}:{tag}";
        var row = new MetadataRowViewModel(new MetadataField(family, group.Raw, tag, [""], ExifTool.ListFields.Contains(key), true), LabelOf(tag), _loc, true,
            false, OnRowChanged)
        {
            IsAdded = true,
        };
        _rows.Add(row);
        Arrange();
        row.IsEditing = true;
        Recount();
    }

    private void OnRowChanged(MetadataRowViewModel row)
    {
        // A new field left empty, or removed, is no field.
        if (row is { IsAdded: true, IsEditing: false } && (row.Value.Length == 0 || row.IsRemoved))
        {
            _rows.Remove(row);
            Arrange();
        }
        Recount();
    }

    private void Recount()
    {
        // Remove everything may take only what shows no row of its own to strike through.
        ChangeCount = Math.Max(_rows.Count(r => r.IsChanged || r.IsRemoved || r.IsAdded && r.Value.Length > 0), RemovesAll ? 1 : 0);
    }

    private bool CanRemoveLocation() => CanEdit && !IsBusy && _rows.Any(r => r.CanEdit && ExifTool.IsLocation(r.Field));

    /// <summary>
    /// Whatever the rows offer: what's left may be locked, and go all the same (a JPEG's comment, a PNG's text).
    /// </summary>
    private bool CanRemoveAll() => CanEdit && !IsBusy && !_holdsMore && ExifTool.RemovesEverythingFrom(_files[CurrentIndex]);

    [RelayCommand(CanExecute = nameof(CanRemoveLocation))]
    private void RemoveLocation()
    {
        _removeLocationAsked = true;
        foreach (var row in _rows.Where(r => ExifTool.IsLocation(r.Field) && r.CanEdit).ToList())
            row.MarkRemoved();
        DropRemovedAdditions();
        Recount();
    }

    [RelayCommand(CanExecute = nameof(CanRemoveAll))]
    private void RemoveAll()
    {
        _removeAllAsked = true;
        foreach (var row in _rows.Where(r => r.CanEdit && !ExifTool.KeptByRemoveAll(r.Field)).ToList())
            row.MarkRemoved();
        DropRemovedAdditions();
        Recount();
    }

    /// <summary>Fields added here and removed again are no fields: their rows go.</summary>
    private void DropRemovedAdditions()
    {
        if (_rows.RemoveAll(r => r is { IsAdded: true, IsRemoved: true }) > 0)
            Arrange();
    }

    /// <summary>What the rows say changed, for the conversion.</summary>
    internal MetadataEdits Changes()
    {
        var changes = new MetadataEdits { RemoveLocation = RemovesLocation, RemoveAll = RemovesAll };
        foreach (var row in _rows)
        {
            if (row is { IsRemoved: true, IsAdded: false })
            {
                // Taken by Remove location / Remove everything already.
                if (!(RemovesAll && !ExifTool.KeptByRemoveAll(row.Field) || RemovesLocation && ExifTool.IsLocation(row.Field)))
                    changes.Remove.Add(row.Field.Key);
            }
            else if (!row.IsRemoved && (row.IsChanged || row.IsAdded && row.Value.Length > 0))
            {
                // A value emptied is the field removed.
                if (row.Items() is { Count: > 0 } items)
                    changes.Set[row.Field.Key] = items;
                else
                    changes.Remove.Add(row.Field.Key);
            }
        }
        return changes;
    }

    // ───────── Saving ─────────

    private bool CanSave() => CanEdit && !IsBusy && HasChanges;

    /// <summary>
    /// Starts the conversion that writes the changes (shown in the toast), into a copy named by the tool's rule or in
    /// the original's place, and opens the next file or closes. When it can't be started, the file stays, to save
    /// again or skip.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        try
        {
            _export(_files[CurrentIndex], MetadataTool.ConversionOf(_tool, Changes(), ReplaceOriginal));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogError(ex, "Could not start writing the metadata of {File}", CurrentFileName);
            AddProblem(CurrentIndex, "error.conversion_failed");
            return;
        }
        _outcomes[CurrentIndex] = CropItemState.Done;
        ClearProblems(CurrentIndex);
        await LoadFileAsync(CurrentIndex + 1);
    }

    [RelayCommand]
    private async Task SkipAsync()
    {
        if (IsBusy || IsFinished)
            return;
        _outcomes[CurrentIndex] ??= CropItemState.Skipped;
        await LoadFileAsync(CurrentIndex + 1);
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    [RelayCommand]
    private void Install() => InstallRequested?.Invoke();

    /// <summary>
    /// ExifTool may have been installed since the window opened (Install ExifTool): the file is read again with it.
    /// </summary>
    public async Task RecheckEngineAsync()
    {
        // Without Perl it is read again once, to say so; what it said already, it doesn't say again each time.
        if (NeedsExifTool && !IsBusy && !IsFinished && _engines.Locate() is { } exifTool && !(LacksPerl && _engines.NeedsPerl(exifTool)))
            await LoadFileAsync(CurrentIndex);
    }

    private void AddProblem(int index, string key)
    {
        _outcomes[index] = CropItemState.Failed;
        var line = $"{Path.GetFileName(_files[index])}: {_loc[key]}";
        Problems.Add(line);
        _problemFiles.Add(index);
        StatusNotice = line;
    }

    /// <summary>A file saved after all: what went wrong with it before is no failure any more.</summary>
    private void ClearProblems(int index)
    {
        for (var i = _problemFiles.Count - 1; i >= 0; i--)
        {
            if (_problemFiles[i] != index)
                continue;
            _problemFiles.RemoveAt(i);
            Problems.RemoveAt(i);
        }
        StatusNotice = Problems.LastOrDefault() ?? "";
    }

    /// <summary>
    /// Closes after the last file, or stays open on the summary when a file could not be opened or saved.
    /// </summary>
    private void Finish()
    {
        if (Problems.Count == 0)
        {
            Close();
            return;
        }
        FailedCount = _outcomes.Count(o => o == CropItemState.Failed);
        DoneCount = _outcomes.Count(o => o == CropItemState.Done);
        SkippedCount = _outcomes.Count(o => o == CropItemState.Skipped);
        Summary = _loc.Format("toast.done_errors", FailedCount, _files.Count);
        StatusNotice = "";
        HasMultipleFiles = false;
        IsBusy = false;
        _rows = [];
        Groups.Clear();
        IsFinished = true;
    }

    /// <summary>
    /// Keeps "Replace the original" in the tool, as given to this window and as saved, and writes the library.
    /// </summary>
    private void Remember()
    {
        MetadataTool.Remember(_tool, ReplaceOriginal);
        if (_store.FindPreset(_tool.Id) is not { IsMetadataTool: true } saved)
            return;
        MetadataTool.Remember(saved, ReplaceOriginal);
        try
        {
            _store.SaveLibrary();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.LogWarning(ex, "Could not remember Replace the original");
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (CurrentIndex < _files.Count)
            CounterText = _loc.Format("crop.page_counter", CurrentIndex + 1, _files.Count);
        OnPropertyChanged(nameof(SaveText));
        OnPropertyChanged(nameof(ChangesText));
        OnPropertyChanged(nameof(ReadOnlyNotice));
        if (_holdsMore)
            EmbeddedNotice = _loc.Format("metadata.embedded", _loc["preset.remove_metadata"]);
        ShowLocationNotice();
        if (IsFinished)
        {
            Summary = _loc.Format("toast.done_errors", FailedCount, _files.Count);
            return;
        }
        // The groups made again: their titles, and every row drawn again with what a screen reader says for it.
        if (_rows.Count > 0 || Groups.Count > 0)
        {
            ShowDetails();
            Arrange();
        }
    }

    public void Dispose()
    {
        var wasOpen = !_closed;
        _closed = true;
        if (wasOpen)
        {
            _closing.Cancel();
            _closing.Dispose();
        }
        _loc.LanguageChanged -= OnLanguageChanged;
        Thumbnail = null;
    }
}

/// <summary>A group of fields: its title, where it lives, its rows, what Add field offers.</summary>
public sealed partial class MetadataGroupViewModel(string title, string raw, string family, IReadOnlyList<MetadataRowViewModel> rows, string? warning,
    IReadOnlyList<string> addable, Action<MetadataGroupViewModel, string> add)
{
    public string Title { get; } = title;

    /// <summary>The group's own name (GPS, IFD0, XMP-dc), and the family it belongs to.</summary>
    public string Raw { get; } = raw;

    /// <summary>Beside the title: the group's own name, or its family's when the title is the name itself.</summary>
    public string Origin { get; } = raw == title ? family : raw;

    public IReadOnlyList<MetadataRowViewModel> Rows { get; } = rows;
    public string? Warning { get; } = warning;
    public bool HasWarning => Warning is not null;
    public IReadOnlyList<string> Addable { get; } = addable;
    public bool CanAdd => Addable.Count > 0;

    [RelayCommand]
    private void Add(string tag) => add(this, tag);
}

/// <summary>One field as its row shows it: edited in place, removed, put back.</summary>
public sealed partial class MetadataRowViewModel : ObservableObject
{
    private readonly Action<MetadataRowViewModel> _changed;
    private readonly ILocalizer _loc;
    private string _before = "";

    /// <param name="showsLock">A lock beside the value: the file decides it, while other fields can be edited.</param>
    public MetadataRowViewModel(MetadataField field, string label, ILocalizer loc, bool canEdit, bool showsLock, Action<MetadataRowViewModel> changed)
    {
        Field = field;
        Label = label;
        _loc = loc;
        CanEdit = canEdit;
        ShowsLock = showsLock;
        _value = field.Value;
        _changed = changed;
    }

    public MetadataField Field { get; }
    public string Label { get; }
    public bool CanEdit { get; }

    /// <summary>A list (keywords, creators): edited one entry per line.</summary>
    public bool IsList => Field.IsList;

    /// <summary>A lock beside a value the file's structure decides (only when editing is possible at all).</summary>
    public bool ShowsLock { get; }

    /// <summary>
    /// Not a value to type in: a text box lays all of it out (32 KB takes most of a second, 1 MB about 16 s). It can
    /// still be selected and removed.
    /// </summary>
    private bool FitsABox => Field.Value.Length <= LongestEditable;

    /// <summary>The value opens in a box when pressed; otherwise it is text to select.</summary>
    public bool CanType => CanEdit && FitsABox;

    /// <summary>Why a value that could be edited isn't offered for it.</summary>
    public string? TooLongNote => CanEdit && !FitsABox ? _loc["metadata.too_long"] : null;

    /// <summary>A field the user added (not in the file yet).</summary>
    public bool IsAdded { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged), nameof(SpokenValue), nameof(ShownValue))]
    private string _value;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsValue))]
    private bool _isEditing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemove), nameof(CanUndo), nameof(SpokenValue))]
    private bool _isRemoved;

    public bool IsChanged => !IsAdded && Value != Field.Value;
    public bool ShowsValue => !IsEditing;
    public bool CanRemove => CanEdit && !IsRemoved;
    public bool CanUndo => !IsAdded && (IsRemoved || IsChanged);

    /// <summary>
    /// What a screen reader says for the value: the field's name and its value, its entries one after another.
    /// </summary>
    public string SpokenValue
    {
        get
        {
            var spoken = $"{Label}: {Cut(Value, LongestSpoken).Replace("\n", ", ", StringComparison.Ordinal)}{(TooLongNote is { } note ? $". {note}" : "")}";
            return IsRemoved ? _loc.Format("metadata.spoken_removed", spoken) : spoken;
        }
    }

    /// <summary>
    /// The value as the window shows it. In a right-to-left UI each line is set apart by its own first letter, or a
    /// date and "45 deg 30' N" would come out reordered around the digits; left to right there's nothing to add to what
    /// can be selected and copied, but for a value too long to lay out, which is cut.
    /// </summary>
    public string ShownValue => System.Globalization.CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft
        ? string.Join("\n", Cut(Value, LongestShown).Split('\n').Select(line => $"\u2068{line}\u2069"))
        : Cut(Value, LongestShown);

    /// <summary>
    /// Enough for the six lines a row shows: text is laid out whole, and a file can hold megabytes of it.
    /// </summary>
    private static string Cut(string value, int length) => value.Length <= length ? value
        : value[..(char.IsHighSurrogate(value[length - 1]) ? length - 1 : length)] + "…";

    /// <summary>A long AI prompt or workflow stays whole to select and copy; the file's total is limited.</summary>
    internal const int LongestShown = 32 * 1024;

    internal const int LongestEditable = LongestShown;
    private const int LongestSpoken = 1000;

    /// <summary>What a screen reader says for × ("Remove Title").</summary>
    public string RemoveName => _loc.Format("metadata.remove_named", Label);

    /// <summary>What a screen reader says for the arrow that puts it back ("Put Title back").</summary>
    public string UndoName => _loc.Format("metadata.undo_named", Label);

    /// <summary>
    /// The value as the field takes it: one item, or a list's entries, one per line, empty lines left out.
    /// </summary>
    public List<string> Items() =>
        IsList ? [.. Value.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0)]
        : Value.Length > 0 ? [Value] : [];

    partial void OnIsEditingChanged(bool value)
    {
        if (value)
            _before = Value;
        else
            _changed(this);
    }

    partial void OnValueChanged(string value)
    {
        OnPropertyChanged(nameof(CanUndo));
        // While typing too: Save is there for the first change as soon as it is one.
        _changed(this);
    }

    [RelayCommand]
    private void Edit()
    {
        if (CanType && !IsRemoved)
            IsEditing = true;
    }

    /// <summary>Enter, or the box losing focus: the value is kept.</summary>
    [RelayCommand]
    private void Commit() => IsEditing = false;

    /// <summary>Esc: the value as it was before this edit.</summary>
    [RelayCommand]
    private void CancelEdit()
    {
        Value = _before;
        IsEditing = false;
    }

    [RelayCommand]
    private void Remove() => MarkRemoved(notify: true);

    internal void MarkRemoved(bool notify = false)
    {
        if (!CanEdit)
            return;
        IsEditing = false;
        IsRemoved = true;
        if (notify)
            _changed(this);
    }

    [RelayCommand]
    private void Undo()
    {
        IsRemoved = false;
        Value = Field.Value;
        _changed(this);
    }
}

/// <summary>Small converters for the Metadata window.</summary>
public static class MetadataConverters
{
    /// <summary>"CopyrightNotice" → "Copyright Notice", for Add field's menu.</summary>
    public static Avalonia.Data.Converters.IValueConverter Label { get; } =
        new Avalonia.Data.Converters.FuncValueConverter<string, string>(tag => MetadataViewModel.LabelOf(tag ?? ""));
}
