// One file of the Home page's queue: its number, its picture (the first page of a PDF, a picture itself) and its format.

using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services;
using Filee.Core.Formats;
using Filee.Engines.Pdf;

namespace Filee.App.ViewModels.Pages;

public sealed partial class StagedFileViewModel : ObservableObject, IDisposable
{
    /// <summary>Width and height of the picture of a PDF's first page, in pixels (the card shows it smaller).</summary>
    private const int PdfThumbnailSize = 260;

    private readonly Action<StagedFileViewModel> _remove;
    private readonly bool _ownsThumbnail;

    /// <param name="format">The file's format, or null when Filee doesn't know it (the card then shows no picture).</param>
    public StagedFileViewModel(string path, FileFormat? format, Action<StagedFileViewModel> remove)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        FormatBadge = System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        _remove = remove;
        _ownsThumbnail = format?.Category == FormatCategory.Pdf;
    }

    /// <summary>The card the user selected: the Delete key takes it out of the queue.</summary>
    [ObservableProperty] private bool _isSelected;

    public string Path { get; }

    /// <summary>The file name, shown under its picture.</summary>
    public string Name { get; }

    /// <summary>The extension ("PDF", "PNG"), shown when the file has no picture.</summary>
    public string FormatBadge { get; }

    /// <summary>The place in the queue, from 1: the order the files are converted or merged in.</summary>
    [ObservableProperty] private int _position;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasThumbnail))]
    private Bitmap? _thumbnail;

    public bool HasThumbnail => Thumbnail is not null;

    /// <summary>
    /// Makes the picture off the UI thread: the first page of a PDF, or the picture of an image file. Null for other
    /// formats, which show their extension instead.
    /// </summary>
    internal static Bitmap? MakeThumbnail(string path, FileFormat? format)
    {
        if (format is null)
            return null;
        if (format.Category == FormatCategory.Pdf)
        {
            var preview = PdfPageTools.Preview(path, 0, PdfThumbnailSize);
            using var stream = new MemoryStream(preview.Png);
            return new Bitmap(stream);
        }
        return format.Category == FormatCategory.Image && HistoryThumbnails.CanShow(path)
            ? HistoryThumbnails.Load(path)
            : null;
    }

    [RelayCommand]
    private void Remove() => _remove(this);

    /// <summary>Disposes the picture of a PDF (pictures of images are cached and shared, so they stay).</summary>
    public void Dispose()
    {
        if (_ownsThumbnail)
            Thumbnail?.Dispose();
        Thumbnail = null;
    }
}
