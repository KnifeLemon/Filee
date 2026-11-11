// Lightweight PDF page organiser opened from the PDF donut's Edit group. Pages stay as PDF objects: saving copies
// them in the chosen order and only changes /Rotate where requested. Several pages can be selected at once (#860):
// moving, rotating, deleting, extracting, inserting and replacing all act on the whole selection.

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services;
using Filee.Core.Conversion;
using Filee.Core.History;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Engines.Pdf;
using Microsoft.Extensions.Logging;

namespace Filee.App.ViewModels;

public sealed partial class PdfPageItem(PdfPreviewSource previewSource, int pageIndex, ILocalizer loc) : ObservableObject, IDisposable
{
    public PdfPreviewSource PreviewSource { get; } = previewSource;
    public string Path => PreviewSource.Path;
    public int PageIndex { get; } = pageIndex;
    public int PageNumber => PageIndex + 1;
    public string PageText => loc.Format("pdf_editor.page", PageIndex + 1);
    public string SourceName => System.IO.Path.GetFileName(Path);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RotationText))]
    [NotifyPropertyChangedFor(nameof(PreviewRotation))]
    private int _rotation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    private bool _isThumbnailLoading;

    /// <summary>The page is among the ones being dragged to another place in the strip.</summary>
    [ObservableProperty]
    private bool _isMoving;

    public string RotationText
    {
        get
        {
            var signed = Rotation > 180 ? Rotation - 360 : Rotation;
            return signed == 0 ? "" : loc.Format("pdf_editor.rotation", signed);
        }
    }

    public double PreviewRotation => Rotation > 180 ? Rotation - 360 : Rotation;
    public bool HasThumbnail => Thumbnail is not null;
    public PdfPageReference Reference => new(Path, PageIndex, Rotation);

    internal void SetThumbnail(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var previous = Thumbnail;
        Thumbnail = new Bitmap(stream);
        previous?.Dispose();
    }

    public void Dispose()
    {
        Thumbnail?.Dispose();
        Thumbnail = null;
    }
}

public sealed partial class PdfPageEditorViewModel : ObservableObject, IDisposable
{
    private readonly Preset _preset;
    private readonly ILocalizer _loc;
    private readonly UserDataStore _store;
    private readonly ILogger? _log;
    private readonly string _primarySource;
    private readonly IReadOnlyList<string> _initialFiles;
    private readonly Dictionary<string, PdfPreviewSource> _previewSources = new(FileSystemPaths.Comparer);
    private readonly SemaphoreSlim _thumbnailGate = new(1, 1);
    private Task? _initialLoad;
    private int _previewRequest;
    private bool _closed;

    /// <summary>
    /// Set while the selection is changed in code: the list box also rewrites <see cref="SelectedPages"/> when pages
    /// move or go, and those passing changes must not pick a new preview page.
    /// </summary>
    private bool _syncingSelection;

    /// <summary>Where the pages of the PDFs being added go.</summary>
    private enum Placement { End, BeforeSelection, AfterSelection, ReplaceSelection }

    public PdfPageEditorViewModel(IReadOnlyList<string> files, Preset preset, ILocalizer loc, UserDataStore store,
        ILogger? log = null)
    {
        _preset = preset;
        _loc = loc;
        _store = store;
        _log = log;
        _primarySource = files.First();
        _initialFiles = files;
        SelectedPages.CollectionChanged += OnSelectedPagesChanged;
    }

    public ObservableCollection<PdfPageItem> Pages { get; } = [];

    /// <summary>
    /// The pages selected in the strip (click, Ctrl + click, Shift + click), bound to the list box's selected items.
    /// They are in the order they were picked; <see cref="SelectionInOrder"/> gives them in page order.
    /// </summary>
    public ObservableCollection<PdfPageItem> SelectedPages { get; } = [];

    /// <summary>
    /// The page shown large: the one picked last. Setting it to a page outside the selection selects that page alone.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedPreviewRotation))]
    private PdfPageItem? _selectedPage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedPreview))]
    private Bitmap? _selectedPreview;

    [ObservableProperty]
    private bool _isPreviewLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(RotateLeftCommand))]
    [NotifyCanExecuteChangedFor(nameof(RotateRightCommand))]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _status = "";

    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);
    public bool CanSave => !IsBusy && Pages.Count > 0;
    public bool HasSelectedPreview => SelectedPreview is not null;
    public double SelectedPreviewRotation => SelectedPage?.PreviewRotation ?? 0;

    /// <summary>Pages are selected and nothing is loading or saving: Insert, Replace and Extract can act on them.</summary>
    public bool HasSelection => !IsBusy && SelectedPages.Count > 0;

    partial void OnSelectedPageChanged(PdfPageItem? value)
    {
        _ = LoadSelectedPreviewAsync(value);
        // Picked in code rather than in the list: that page becomes the whole selection.
        if (!_syncingSelection && (value is null ? SelectedPages.Count > 0 : !SelectedPages.Contains(value)))
            Select(value is null ? [] : [value], value);
    }

    private void OnSelectedPagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_syncingSelection)
        {
            _syncingSelection = true;
            try
            {
                // A page added to the selection is the one the user just picked; it shows large.
                if (e.NewItems is { Count: > 0 } added)
                    SelectedPage = (PdfPageItem?)added[added.Count - 1];
                else if (SelectedPage is not null && !SelectedPages.Contains(SelectedPage))
                    SelectedPage = SelectedPages.LastOrDefault();
            }
            finally
            {
                _syncingSelection = false;
            }
        }
        NotifyCollectionChanged();
    }

    /// <summary>Makes <paramref name="pages"/> the selection, with <paramref name="focus"/> shown large.</summary>
    private void Select(IReadOnlyList<PdfPageItem> pages, PdfPageItem? focus)
    {
        var syncing = _syncingSelection;
        _syncingSelection = true;
        try
        {
            if (!SelectedPages.SequenceEqual(pages))
            {
                SelectedPages.Clear();
                foreach (var page in pages)
                    SelectedPages.Add(page);
            }
            SelectedPage = focus;
        }
        finally
        {
            _syncingSelection = syncing;
        }
        NotifyCollectionChanged();
    }

    /// <summary>A plain click on one page of a larger selection: that page alone stays selected.</summary>
    public void SelectOnly(PdfPageItem page)
    {
        if (Pages.Contains(page))
            Select([page], page);
    }

    /// <summary>The selected pages in the order they stand in the strip.</summary>
    public IReadOnlyList<PdfPageItem> SelectionInOrder() => SelectedIndexes().Select(index => Pages[index]).ToList();

    /// <summary>The positions of the selected pages, lowest first.</summary>
    private List<int> SelectedIndexes()
    {
        if (SelectedPages.Count == 0)
            return [];
        var selected = SelectedPages.ToHashSet();
        var indexes = new List<int>(selected.Count);
        for (var index = 0; index < Pages.Count; index++)
            if (selected.Contains(Pages[index]))
                indexes.Add(index);
        return indexes;
    }

    /// <summary>Opens the PDFs and renders the page strip after the window is visible.</summary>
    public async Task LoadThumbnailsAsync()
    {
        await EnsureInitialFilesAsync();
        await LoadThumbnailsAsync(Pages.ToList());
    }

    private async Task LoadThumbnailsAsync(IReadOnlyList<PdfPageItem> items)
    {
        await _thumbnailGate.WaitAsync();
        try
        {
            foreach (var item in items.Where(item => item.Thumbnail is null))
            {
                if (_closed || !Pages.Contains(item))
                    return;
                item.IsThumbnailLoading = true;
                try
                {
                    var preview = await Task.Run(() => item.PreviewSource.Preview(item.PageIndex, 260));
                    if (!_closed && Pages.Contains(item))
                        item.SetThumbnail(preview.Png);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _log?.LogDebug(ex, "Could not render thumbnail for page {Page} of {Path}", item.PageNumber, item.Path);
                }
                finally
                {
                    item.IsThumbnailLoading = false;
                }
            }
        }
        finally
        {
            _thumbnailGate.Release();
        }
    }

    private async Task LoadSelectedPreviewAsync(PdfPageItem? item)
    {
        var request = Interlocked.Increment(ref _previewRequest);
        IsPreviewLoading = item is not null;
        SelectedPreview?.Dispose();
        SelectedPreview = null;
        if (item is null)
            return;
        try
        {
            var preview = await Task.Run(() => item.PreviewSource.Preview(item.PageIndex, 1400));
            using var stream = new MemoryStream(preview.Png);
            var bitmap = new Bitmap(stream);
            if (_closed || request != _previewRequest || !ReferenceEquals(item, SelectedPage))
            {
                bitmap.Dispose();
                return;
            }
            SelectedPreview = bitmap;
            if (item.Thumbnail is null)
                item.SetThumbnail(preview.Png);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogDebug(ex, "Could not render preview for page {Page} of {Path}", item.PageNumber, item.Path);
        }
        finally
        {
            if (request == _previewRequest)
                IsPreviewLoading = false;
        }
    }

    /// <summary>Adds the pages of the PDFs at the end.</summary>
    public Task AddFilesAsync(IReadOnlyList<string> paths) => AddFilesAsync(paths, Placement.End);

    /// <summary>
    /// Inserts the pages of the PDFs before the first selected page or after the last one, and selects them. With no
    /// page selected they go at the end.
    /// </summary>
    public Task InsertFilesAsync(IReadOnlyList<string> paths, bool after) =>
        AddFilesAsync(paths, after ? Placement.AfterSelection : Placement.BeforeSelection);

    /// <summary>
    /// Puts the pages of the PDFs where the first selected page is and removes the selected pages, in one step. Nothing
    /// is removed when none of the PDFs could be opened.
    /// </summary>
    public Task ReplaceSelectedAsync(IReadOnlyList<string> paths) => AddFilesAsync(paths, Placement.ReplaceSelection);

    private async Task AddFilesAsync(IReadOnlyList<string> paths, Placement placement)
    {
        await EnsureInitialFilesAsync();
        if (paths.Count == 0 || IsBusy || _closed)
            return;
        var added = await AddFilesCoreAsync(paths, placement);
        _ = LoadThumbnailsAsync(added);
    }

    private Task EnsureInitialFilesAsync() => _initialLoad ??= AddFilesCoreAsync(_initialFiles, Placement.End);

    private async Task<IReadOnlyList<PdfPageItem>> AddFilesCoreAsync(IReadOnlyList<string> paths, Placement placement)
    {
        if (paths.Count == 0 || _closed)
            return [];
        IsBusy = true;
        Status = _loc["pdf_editor.loading"];
        var added = new List<PdfPageItem>();
        try
        {
            var missing = paths.Distinct(FileSystemPaths.Comparer)
                .Where(path => !_previewSources.ContainsKey(path)).ToList();
            var opened = await Task.Run(() => missing.Select(path =>
            {
                try { return (Path: path, Source: PdfPreviewSource.Open(path), Error: (Exception?)null); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { return (Path: path, Source: (PdfPreviewSource?)null, Error: ex); }
            }).ToList());
            var failed = false;
            foreach (var (path, source, error) in opened)
            {
                if (error is not null)
                {
                    failed = true;
                    _log?.LogError(error, "Could not add {Path} to the PDF editor", path);
                    continue;
                }
                _previewSources[path] = source!;
            }
            // Measured once the files are open: the selection may have changed while they were read.
            List<int> selected = placement == Placement.End ? [] : SelectedIndexes();
            List<PdfPageItem> replaced = placement == Placement.ReplaceSelection ? selected.Select(index => Pages[index]).ToList() : [];
            var at = selected.Count == 0 ? Pages.Count
                : placement == Placement.AfterSelection ? selected[^1] + 1
                : selected[0];
            _syncingSelection = true;
            try
            {
                foreach (var path in paths)
                {
                    if (!_previewSources.TryGetValue(path, out var source))
                        continue;
                    for (var page = 0; page < source.PageCount; page++)
                    {
                        var item = new PdfPageItem(source, page, _loc);
                        Pages.Insert(at++, item);
                        added.Add(item);
                    }
                }
                if (added.Count > 0)
                    foreach (var page in replaced)
                    {
                        Pages.Remove(page);
                        page.Dispose();
                    }
            }
            finally
            {
                _syncingSelection = false;
            }
            if (placement != Placement.End && added.Count > 0)
                Select(added, added[0]);
            else
                SelectedPage ??= Pages.FirstOrDefault();
            Status = failed ? _loc["error.conversion_failed"] : "";
            NotifyCollectionChanged();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogError(ex, "Could not add pages to the PDF editor");
            Status = _loc["error.conversion_failed"];
        }
        finally
        {
            IsBusy = false;
        }
        return added;
    }

    // Up while some selected page has an unselected one above it; the pages already at the top stay there.
    private bool CanMoveUp() => !IsBusy && SelectedIndexes() is { Count: > 0 } selected && selected[^1] >= selected.Count;
    private bool CanMoveDown() => !IsBusy && SelectedIndexes() is { Count: > 0 } selected && selected[0] < Pages.Count - selected.Count;
    private bool CanChangePage() => !IsBusy && SelectedPages.Count > 0;
    private bool CanDelete() => CanChangePage() && SelectedPages.Count < Pages.Count;

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => Move(-1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => Move(1);

    /// <summary>
    /// Moves every selected page one place. A selected page never passes another selected one, so a block of pages
    /// moves together and pages already against the edge stay where they are.
    /// </summary>
    private void Move(int direction)
    {
        var selected = SelectedPages.ToHashSet();
        if (selected.Count == 0)
            return;
        var order = Pages.ToList();
        var indexes = Enumerable.Range(0, order.Count);
        foreach (var index in direction < 0 ? indexes : indexes.Reverse())
        {
            var target = index + direction;
            if (selected.Contains(order[index]) && target >= 0 && target < order.Count && !selected.Contains(order[target]))
                (order[index], order[target]) = (order[target], order[index]);
        }
        Reorder(order);
    }

    /// <summary>
    /// Moves <paramref name="pages"/> (a dragged selection) so they stand together, in their current order, before the
    /// page now at <paramref name="insertAt"/>; <see cref="Pages"/>.Count puts them at the end. They stay selected.
    /// </summary>
    public void MovePages(IEnumerable<PdfPageItem> pages, int insertAt)
    {
        var moving = pages.ToHashSet();
        if (IsBusy || moving.Count == 0)
            return;
        insertAt = Math.Clamp(insertAt, 0, Pages.Count);
        var rest = Pages.Where(page => !moving.Contains(page)).ToList();
        var block = Pages.Where(moving.Contains).ToList();
        // The drop position counts the dragged pages; without them, it is one place higher for each one above it.
        var target = insertAt - Pages.Take(insertAt).Count(moving.Contains);
        Reorder([.. rest.Take(target), .. block, .. rest.Skip(target)]);
        Select(block, SelectedPage is { } focus && moving.Contains(focus) ? focus : block.FirstOrDefault());
    }

    /// <summary>Puts the pages in <paramref name="order"/> by moving them, so thumbnails and the selection are kept.</summary>
    private void Reorder(IReadOnlyList<PdfPageItem> order)
    {
        if (order.Count != Pages.Count || order.SequenceEqual(Pages))
            return;
        var selected = SelectedPages.ToList();
        var focus = SelectedPage;
        _syncingSelection = true;
        try
        {
            for (var index = 0; index < order.Count; index++)
                if (!ReferenceEquals(Pages[index], order[index]))
                    Pages.Move(Pages.IndexOf(order[index]), index);
        }
        finally
        {
            _syncingSelection = false;
        }
        // The list box may drop a moved page from its selection; it is selected again here.
        Select(selected, focus);
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        var selected = SelectedIndexes();
        if (selected.Count == 0 || selected.Count >= Pages.Count)
            return;
        _syncingSelection = true;
        try
        {
            foreach (var index in Enumerable.Reverse(selected))
            {
                var page = Pages[index];
                Pages.RemoveAt(index);
                page.Dispose();
            }
        }
        finally
        {
            _syncingSelection = false;
        }
        // The page that took the place of the first deleted one.
        var next = Pages[Math.Min(selected[0], Pages.Count - 1)];
        Select([next], next);
    }

    [RelayCommand(CanExecute = nameof(CanChangePage))]
    private void RotateLeft() => Rotate(-90);

    [RelayCommand(CanExecute = nameof(CanChangePage))]
    private void RotateRight() => Rotate(90);

    private void Rotate(int degrees)
    {
        foreach (var page in SelectedPages)
            page.Rotation = ((page.Rotation + degrees) % 360 + 360) % 360;
        OnPropertyChanged(nameof(SelectedPreviewRotation));
    }

    public Task<bool> SaveAsync() => WriteAsync(Pages, _preset, closeWhenDone: true);

    /// <summary>Saves the selected pages, in page order, as one new PDF; the editor stays open.</summary>
    public Task<bool> ExtractSelectedAsync()
    {
        var selected = SelectionInOrder();
        if (selected.Count == 0)
            return Task.FromResult(false);
        var extract = _preset.Clone();
        extract.Id = "pdf-extract-page";
        extract.NameKey = "preset.pdf_extract";
        extract.Output.FileNamePattern = "{name}_extracted";
        return WriteAsync(selected, extract, closeWhenDone: false);
    }

    private async Task<bool> WriteAsync(IEnumerable<PdfPageItem> items, Preset preset, bool closeWhenDone)
    {
        if (IsBusy)
            return false;
        var pages = items.Select(item => item.Reference).ToList();
        if (pages.Count == 0)
            return false;
        var applied = _store.Settings.WithDefaultOutput(preset);
        var tokens = new OutputPathResolver.Tokens(_primarySource, _loc.DisplayName(applied), 1, DateTime.Now);
        var output = OutputPathResolver.Resolve(applied.Output, tokens, "pdf", File.Exists);
        if (output is null)
        {
            Status = _loc["toast.skipped"];
            return false;
        }

        IsBusy = true;
        Status = _loc["pdf_editor.saving"];
        try
        {
            var folder = System.IO.Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);
            await Task.Run(() => PdfPageTools.Save(pages, output));
            PreserveDates(output, applied);
            _store.AddHistory(new HistoryEntry
            {
                JobId = Guid.NewGuid().ToString("N")[..10],
                FinishedAt = DateTime.Now,
                PresetId = preset.Id,
                PresetName = _loc.DisplayName(preset),
                State = JobState.Completed,
                Sources = pages.Select(p => p.Path).Distinct(FileSystemPaths.Comparer).ToList(),
                Outputs = [output],
            });
            Status = _loc.Format("pdf_editor.saved", System.IO.Path.GetFileName(output));
            return closeWhenDone;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogError(ex, "Could not save edited PDF");
            Status = _loc["error.conversion_failed"];
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void PreserveDates(string output, Preset preset)
    {
        if (!preset.Output.KeepDates && !_store.Settings.KeepFileDates)
            return;
        try
        {
            File.SetCreationTimeUtc(output, File.GetCreationTimeUtc(_primarySource));
            File.SetLastWriteTimeUtc(output, File.GetLastWriteTimeUtc(_primarySource));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.LogDebug(ex, "Could not preserve file timestamps for {Path}", output);
        }
    }

    private void NotifyCollectionChanged()
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(HasSelection));
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        RotateLeftCommand.NotifyCanExecuteChanged();
        RotateRightCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_closed)
            return;
        _closed = true;
        Interlocked.Increment(ref _previewRequest);
        SelectedPreview?.Dispose();
        SelectedPreview = null;
        foreach (var page in Pages)
            page.Dispose();
    }
}
