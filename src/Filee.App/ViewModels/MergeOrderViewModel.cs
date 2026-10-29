// The order of a merge into one PDF (issue #268), one video or one image (#645): the files start sorted by name the way
// people count ("page 2" before "page 10"), and the user moves them before the merge starts. Moving a row changes only
// this copy of the selection. Pictures merged into one image also get their layout here: one under another or side by
// side, the same width or their own sizes, the gap and the background.

using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.Core.Platform;
using Filee.Core.Presets;
using Filee.Core.Web;

namespace Filee.App.ViewModels;

/// <summary>One file of the merge: its name, and the folder it is in (none for a file from a browser, not saved yet).</summary>
public sealed class MergeSource(string path)
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => WebDrop.IsPlaceholder(Path) ? "" : System.IO.Path.GetDirectoryName(Path) ?? "";
}

public sealed partial class MergeOrderViewModel : ObservableObject
{
    /// <param name="files">The files to merge.</param>
    /// <param name="images">
    /// "Merge images" (#645): the layout the pictures are merged with last time, which the window also offers to change.
    /// Null for other merges.
    /// </param>
    public MergeOrderViewModel(IReadOnlyList<string> files, ImageOptions? images = null)
    {
        Files = new(files
            .OrderBy(file => System.IO.Path.GetFileName(file), NaturalComparer.Instance)
            .ThenBy(file => file, NaturalComparer.Instance)
            .Select(file => new MergeSource(file)));
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

    private void Move(int direction)
    {
        if (SelectedFile is not { } selected)
            return;
        var index = Files.IndexOf(selected);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= Files.Count)
            return;
        Files.Move(index, target);
        // The list box can drop its selection while it moves the row.
        SelectedFile = selected;
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }
}
