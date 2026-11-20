using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Filee.App.Services;
using Filee.App.ViewModels;

namespace Filee.App.Views;

public partial class SpreadsheetPrintStudioWindow : Window
{
    /// <summary>The page each item of the strip shows (a recycled item shows another page next).</summary>
    private readonly Dictionary<Control, SpreadsheetPreviewPage> _shown = [];

    public SpreadsheetPrintStudioWindow()
    {
        InitializeComponent();
        Motion.Track(this);
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
        PageList.ContainerPrepared += OnPageShown;
        PageList.ContainerClearing += OnPageHidden;
    }

    // The strip only makes items for the pages in view (and a few beside them): those are the thumbnails rendered.
    private void OnPageShown(object? sender, ContainerPreparedEventArgs e)
    {
        // The container isn't indexed yet while it is being prepared (ItemFromContainer gives null): go by its index.
        if (DataContext is SpreadsheetPrintStudioViewModel vm && e.Index >= 0 && e.Index < vm.Pages.Count)
        {
            var page = vm.Pages[e.Index];
            if (_shown.TryGetValue(e.Container, out var before) && before != page)
                vm.ReleaseThumbnail(before);
            _shown[e.Container] = page;
            vm.RequestThumbnail(page);
        }
    }

    // An item scrolled out of the strip (or recycled for another page) lets go of its thumbnail.
    private void OnPageHidden(object? sender, ContainerClearingEventArgs e)
    {
        if (_shown.Remove(e.Container, out var page) && DataContext is SpreadsheetPrintStudioViewModel vm)
            vm.ReleaseThumbnail(page);
    }

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SpreadsheetPrintStudioViewModel vm)
            return;
        // With one PDF per sheet the chosen name is only the start of each file: Book_Sheet1.pdf, Book_Sheet2.pdf.
        var name = Path.GetFileNameWithoutExtension(vm.FileName) + (vm.OnePdfPerSheet ? ".pdf" : "_prepared.pdf");
        var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = this.TryFindResource("sheet_studio.export_pdf", out var title)
                ? title?.ToString() : "PDF",
            SuggestedFileName = name,
            DefaultExtension = "pdf",
            FileTypeChoices = [new FilePickerFileType("PDF") { Patterns = ["*.pdf"] }],
        });
        if (target?.TryGetLocalPath() is { } path)
            await vm.ExportAsync(path);
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        (DataContext as SpreadsheetPrintStudioViewModel)?.CancelExport();
        Close();
    }
}
