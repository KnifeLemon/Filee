// The order of a merge into one PDF (issue #268), one video or one image (#645): the files start sorted by name the way
// people count ("page 2" before "page 10"), and the user moves them before the merge starts. Moving a row changes only
// this copy of the selection. Pictures merged into one image also get their layout here: one under another or side by
// side, the same width or their own sizes, the gap and the background.

using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.ViewModels.Pages;
using Filee.Core.Formats;
using Filee.Core.Platform;
using Filee.Core.Presets;
using Filee.Core.Web;

namespace Filee.App.ViewModels;

/// <summary>
/// One file of the merge: its name, the folder it is in (none for a file from a browser, not saved yet), and its picture
/// (the first page of a PDF, or the picture itself), loaded in the background so the window opens at once.
/// </summary>
public sealed partial class MergeSource(string path, Action<MergeSource> remove) : ObservableObject, IDisposable
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => WebDrop.IsPlaceholder(Path) ? "" : System.IO.Path.GetDirectoryName(Path) ?? "";

    /// <summary>The format, or null when Filee doesn't know it (the file then shows no picture).</summary>
    public FileFormat? Format { get; } = FormatRegistry.Detect(path);

    /// <summary>The extension ("PDF", "PNG"), shown when the file has no picture.</summary>
    public string FormatBadge => Format is null ? "" : Format.PrimaryExtension.TrimStart('.').ToUpperInvariant();

    /// <summary>The place in the merge, from 1: the order the files merge in.</summary>
    [ObservableProperty]
    private int _position;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasThumbnail))]
    private Bitmap? _thumbnail;

    public bool HasThumbnail => Thumbnail is not null;

    /// <summary>The card's X: takes the file out of the merge.</summary>
    [RelayCommand]
    private void Remove() => remove(this);

    /// <summary>The window closed: a picture that is still being made is dropped when it arrives.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>Disposes the picture of a PDF (pictures of images are cached and shared, so they stay).</summary>
    public void Dispose()
    {
        IsDisposed = true;
        if (Format?.Category == FormatCategory.Pdf)
            Thumbnail?.Dispose();
        Thumbnail = null;
    }
}

public sealed partial class MergeOrderViewModel : ObservableObject, IDisposable
{
    /// <param name="files">The files to merge.</param>
    /// <param name="images">
    /// "Merge images" (#645): the layout the pictures are merged with last time, which the window also offers to change.
    /// Null for other merges.
    /// </param>
    /// <param name="keepOrder">The files are already in the order to merge them in (the Home page's queue): not sorted by name.</param>
    public MergeOrderViewModel(IReadOnlyList<string> files, ImageOptions? images = null, bool keepOrder = false)
    {
        IEnumerable<string> ordered = keepOrder
            ? files
            : files
                .OrderBy(file => System.IO.Path.GetFileName(file), NaturalComparer.Instance)
                .ThenBy(file => file, NaturalComparer.Instance);
        Files = new(ordered.Select(file => new MergeSource(file, RemoveFile)));
        Renumber();
        foreach (var source in Files)
            _ = LoadThumbnailAsync(source);
        SelectedFile = Files.FirstOrDefault();
        if (images is null)
            return;
        MergesImages = true;
        _sideBySide = images.JoinDirection == ImageJoinDirection.Horizontal;
        _sameSize = images.JoinFit == ImageJoinFit.SameSize;
        _gap = images.JoinSpacing;
        _transparent = string.Equals(images.JoinBackground, ImageOptions.TransparentJoin, StringComparison.OrdinalIgnoreCase);
        _backgroundColor = !_transparent && Color.TryParse(images.JoinBackground, out var colour) ? Color.FromRgb(colour.R, colour.G, colour.B) : Colors.White;
        _backgroundHex = Hex(_backgroundColor);
    }

    public ObservableCollection<MergeSource> Files { get; }

    /// <summary>The files are pictures that become one image: the window shows the layout too.</summary>
    public bool MergesImages { get; }

    /// <summary>Side by side (the first on the left); otherwise one under another (the first at the top).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TopToBottom))]
    private bool _sideBySide;

    public bool TopToBottom
    {
        get => !SideBySide;
        set => SideBySide = !value;
    }

    /// <summary>Pictures of different sizes scaled to the same width (or height side by side); otherwise kept, centred.</summary>
    [ObservableProperty]
    private bool _sameSize = true;

    /// <summary>Pixels between two pictures.</summary>
    [ObservableProperty]
    private int _gap;

    [ObservableProperty]
    private Color _backgroundColor = Colors.White;

    /// <summary><see cref="BackgroundColor"/> as <c>#RRGGBB</c>, to type one (as on the Theme page).</summary>
    [ObservableProperty]
    private string _backgroundHex = "#FFFFFF";

    partial void OnBackgroundColorChanged(Color value)
    {
        var hex = Hex(value);
        if (!string.Equals(BackgroundHex, hex, StringComparison.OrdinalIgnoreCase))
            BackgroundHex = hex;
    }

    partial void OnBackgroundHexChanged(string value)
    {
        if (Color.TryParse(value.Trim(), out var colour) && Color.FromRgb(colour.R, colour.G, colour.B) is var opaque && opaque != BackgroundColor)
            BackgroundColor = opaque;
    }

    private static string Hex(Color colour) => $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}";

    /// <summary>A see-through background instead of <see cref="BackgroundColor"/> (in a format that holds one).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PicksColor))]
    private bool _transparent;

    public bool PicksColor => !Transparent;

    /// <summary>Writes the layout chosen here into <paramref name="image"/>.</summary>
    public void ApplyTo(ImageOptions image)
    {
        image.JoinDirection = SideBySide ? ImageJoinDirection.Horizontal : ImageJoinDirection.Vertical;
        image.JoinFit = SameSize ? ImageJoinFit.SameSize : ImageJoinFit.Original;
        image.JoinGap = Math.Clamp(Gap, 0, ImageOptions.MaxJoinGap);
        image.JoinBackground = Transparent ? ImageOptions.TransparentJoin : Hex(BackgroundColor);
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    private MergeSource? _selectedFile;

    /// <summary>The paths in the order they are merged.</summary>
    public IReadOnlyList<string> OrderedFiles => Files.Select(file => file.Path).ToArray();

    private bool CanMoveUp() => SelectedFile is not null && Files.IndexOf(SelectedFile) > 0;
    private bool CanMoveDown() => SelectedFile is not null && Files.IndexOf(SelectedFile) is >= 0 and var index && index < Files.Count - 1;

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => Move(-1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => Move(1);

    /// <summary>The picture of a file, made off the UI thread; a file without one shows its extension.</summary>
    private static async Task LoadThumbnailAsync(MergeSource source)
    {
        if (source.Format is not { } format)
            return;
        Bitmap? bitmap = null;
        try
        {
            bitmap = await Task.Run(() => StagedFileViewModel.MakeThumbnail(source.Path, format));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No picture: the file shows its extension instead.
        }
        if (source.IsDisposed)
        {
            if (format.Category == FormatCategory.Pdf)
                bitmap?.Dispose();
            return;
        }
        source.Thumbnail = bitmap;
    }

    /// <summary>The window closed: the pictures are released.</summary>
    public void Dispose()
    {
        foreach (var file in Files)
            file.Dispose();
    }

    /// <summary>A card dragged to another place: the file moves there, and it stays selected (the merge follows the order).</summary>
    internal void MoveFile(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= Files.Count || to >= Files.Count)
            return;
        Files.Move(from, to);
        Renumber();
        SelectedFile = Files[to];
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The left and right arrows: the selection moves to the neighbour, and stays on a card after a move.</summary>
    internal void SelectNeighbour(int delta)
    {
        if (Files.Count == 0)
            return;
        var index = SelectedFile is null ? -1 : Files.IndexOf(SelectedFile);
        SelectedFile = Files[Math.Clamp(index < 0 ? 0 : index + delta, 0, Files.Count - 1)];
    }

    /// <summary>The Delete key: the selected file leaves the merge.</summary>
    internal void RemoveSelected()
    {
        if (SelectedFile is { } selected)
            RemoveFile(selected);
    }

    /// <summary>The card's X: the file leaves the merge, and the selection moves to a neighbour.</summary>
    internal void RemoveFile(MergeSource file)
    {
        var index = Files.IndexOf(file);
        if (index < 0)
            return;
        Files.RemoveAt(index);
        file.Dispose();
        if (ReferenceEquals(SelectedFile, file))
            SelectedFile = Files.Count == 0 ? null : Files[Math.Min(index, Files.Count - 1)];
        Renumber();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    private void Renumber()
    {
        for (var i = 0; i < Files.Count; i++)
            Files[i].Position = i + 1;
    }

    private void Move(int direction)
    {
        if (SelectedFile is not { } selected)
            return;
        var index = Files.IndexOf(selected);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= Files.Count)
            return;
        Files.Move(index, target);
        Renumber();
        // The list box can drop its selection while it moves the row.
        SelectedFile = selected;
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }
}
