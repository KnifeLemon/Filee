// One file of the Home page's list: its number, its picture (the first page of a PDF, a picture itself) and its format.

using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Microsoft.Extensions.Logging;

namespace Filee.App.ViewModels.Pages;

public sealed partial class StagedFileViewModel : ObservableObject, IDisposable
{
    private readonly Action<StagedFileViewModel> _remove;
    private readonly ILocalizer _loc;
    private readonly FileFormat? _format;

    /// <param name="format">The file's format, or null when Filee doesn't know it (the card then shows no picture).</param>
    public StagedFileViewModel(string path, FileFormat? format, Action<StagedFileViewModel> remove, ILocalizer loc)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        FormatBadge = System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        _format = format;
        _remove = remove;
        _loc = loc;
        Oversized = FilePreviews.IsTooLarge(path, format);
        if (Oversized)
            OversizedText = _loc.Format("home.queue_too_large", Math.Round(new FileInfo(path).Length / 1048576.0));
    }

    /// <summary>The card the user selected: the Delete key takes it out of the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    private bool _isSelected;

    public string Path { get; }

    /// <summary>The file name, shown under its picture.</summary>
    public string Name { get; }

    /// <summary>The extension ("PDF", "PNG"), shown when the file has no picture.</summary>
    public string FormatBadge { get; }

    /// <summary>The place in the list, from 1: the order the files are converted or merged in.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    private int _position;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasThumbnail))]
    private Bitmap? _thumbnail;

    public bool HasThumbnail => Thumbnail is not null;

    /// <summary>A PDF too large for its picture: the card says so where the picture goes.</summary>
    public bool Oversized { get; }

    /// <summary>"Too large for a preview (60 MB)", when <see cref="Oversized"/>.</summary>
    public string OversizedText { get; } = "";

    /// <summary>Stops the picture still being drawn when the card goes.</summary>
    public CancellationTokenSource Cancellation { get; } = new();

    /// <summary>What a screen reader says for the card: its place, its name, and whether it is selected.</summary>
    public string AccessibleName =>
        $"{Position}. {Name}" + (IsSelected ? ", " + _loc["home.queue_selected"] : "");

    /// <summary>What the card's X says for a screen reader: which file it takes out.</summary>
    public string RemoveName => _loc.Format("home.queue_remove_file", Name);

    /// <summary>Draws the picture, in turn with the others; a card that goes meanwhile gets none.</summary>
    public async Task LoadPreviewAsync()
    {
        if (Oversized)
            return;
        try
        {
            var bitmap = await FilePreviews.RenderAsync(Path, _format, Cancellation.Token);
            if (Cancellation.IsCancellationRequested)
            {
                if (FilePreviews.Owns(_format))
                    bitmap?.Dispose();
                return;
            }
            Thumbnail = bitmap;
        }
        catch (OperationCanceledException)
        {
            // The card went: nothing to show.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No picture: the card shows its extension instead. The log says why, for a file that should have one.
            AppHost.Get<ILogger<StagedFileViewModel>>().LogWarning(ex, "No picture for {File}", Path);
        }
    }

    [RelayCommand]
    private void Remove() => _remove(this);

    /// <summary>Cancels a picture still being drawn and disposes the one drawn (a PDF's; images are cached and shared).</summary>
    public void Dispose()
    {
        Cancellation.Cancel();
        if (FilePreviews.Owns(_format))
            Thumbnail?.Dispose();
        Thumbnail = null;
    }
}
