// The PDF page editor's window: the file pickers for Add, Insert and Replace, and dragging pages in the strip (#860).
// A drag moves every selected page to the line drawn between two pages; near the top or bottom edge the strip scrolls,
// so a page can travel further than the strip shows. Escape puts a drag back.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels;

namespace Filee.App.Views;

public partial class PdfPageEditorWindow : Window
{
    private static readonly FilePickerFileType PdfFiles = new("PDF")
    {
        Patterns = ["*.pdf"],
        MimeTypes = ["application/pdf"],
        AppleUniformTypeIdentifiers = ["com.adobe.pdf"],
    };

    /// <summary>Pixels a press may move before it counts as a drag of the pages rather than a click.</summary>
    private const double DragThreshold = 6;

    /// <summary>How close to the strip's top or bottom edge a drag scrolls it, and how far it scrolls per tick.</summary>
    private const double ScrollBand = 40;
    private const double ScrollStep = 18;

    private static readonly Cursor DragCursor = new(StandardCursorType.DragMove);

    private readonly DispatcherTimer _scrollTimer;

    /// <summary>The page under the press, and where the press began (in <see cref="PageOrder"/>).</summary>
    private (PdfPageItem Page, Point Start)? _pressed;

    /// <summary>
    /// The press was a plain click on a page of a larger selection: the list was kept from selecting it alone, so the
    /// whole selection can be dragged. A click without a drag then selects that page alone.
    /// </summary>
    private bool _keptSelection;

    /// <summary>The pages being dragged, in page order; empty when no drag is going on.</summary>
    private IReadOnlyList<PdfPageItem> _moving = [];

    private Point _pointer;

    public PdfPageEditorWindow()
    {
        InitializeComponent();
        Motion.Track(this);
        _scrollTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Input, (_, _) => ScrollWhileDragging());
        // Tunnelled: the list selects the page on its own press handler, which would break up the selection.
        PageOrder.AddHandler(PointerPressedEvent, OnPagePressing, RoutingStrategies.Tunnel);
        // Handled too: the list has selected the page by then, and the drag still has to start.
        PageOrder.AddHandler(PointerPressedEvent, OnPagePressed, RoutingStrategies.Bubble, handledEventsToo: true);
        PageOrder.AddHandler(PointerMovedEvent, OnPageMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        PageOrder.AddHandler(PointerReleasedEvent, OnPageReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        PageOrder.AddHandler(PointerCaptureLostEvent, (_, _) => EndDrag(), handledEventsToo: true);
        // Before the Cancel button, which Escape would otherwise press.
        AddHandler(KeyDownEvent, OnKeyDownWhileDragging, RoutingStrategies.Tunnel);
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is PdfPageEditorViewModel vm)
            await vm.LoadThumbnailsAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _scrollTimer.Stop();
        if (DataContext is PdfPageEditorViewModel vm)
            vm.Dispose();
        base.OnClosed(e);
    }

    public async Task PickAndAddAsync()
    {
        if (DataContext is PdfPageEditorViewModel vm)
            await vm.AddFilesAsync(await PickPdfsAsync("pdf_editor.add"));
    }

    private async Task<IReadOnlyList<string>> PickPdfsAsync(string titleKey)
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = this.FindResource(titleKey)?.ToString(),
            AllowMultiple = true,
            FileTypeFilter = [PdfFiles],
        });
        return picked.Select(file => file.TryGetLocalPath()).OfType<string>().ToList();
    }

    private async void OnAdd(object? sender, RoutedEventArgs e) => await PickAndAddAsync();

    private async void OnInsertBefore(object? sender, RoutedEventArgs e) => await InsertAsync(after: false);

    private async void OnInsertAfter(object? sender, RoutedEventArgs e) => await InsertAsync(after: true);

    private async Task InsertAsync(bool after)
    {
        if (DataContext is PdfPageEditorViewModel vm)
            await vm.InsertFilesAsync(await PickPdfsAsync(after ? "pdf_editor.insert_after" : "pdf_editor.insert_before"), after);
    }

    private async void OnReplace(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PdfPageEditorViewModel vm)
            await vm.ReplaceSelectedAsync(await PickPdfsAsync("pdf_editor.replace"));
    }

    private async void OnExtract(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PdfPageEditorViewModel vm)
            await vm.ExtractSelectedAsync();
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PdfPageEditorViewModel vm && await vm.SaveAsync())
            Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private static PdfPageItem? PageAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as PdfPageItem;

    private void OnPagePressing(object? sender, PointerPressedEventArgs e)
    {
        _keptSelection = false;
        if (DataContext is not PdfPageEditorViewModel vm || PageAt(e.Source) is not { } page
            || !e.GetCurrentPoint(PageOrder).Properties.IsLeftButtonPressed)
            return;
        // Ctrl and Shift keep their meaning (add to the selection, select a range); a plain press on a selected page
        // of several leaves the selection whole for a drag.
        if (e.KeyModifiers == KeyModifiers.None && vm.SelectedPages.Count > 1 && vm.SelectedPages.Contains(page))
        {
            _keptSelection = true;
            e.Handled = true;
            (PageOrder.ContainerFromItem(page) ?? PageOrder).Focus(NavigationMethod.Pointer);
        }
    }

    private void OnPagePressed(object? sender, PointerPressedEventArgs e)
    {
        if (PageAt(e.Source) is not { } page || !e.GetCurrentPoint(PageOrder).Properties.IsLeftButtonPressed)
            return;
        _pressed = (page, e.GetPosition(PageOrder));
        e.Pointer.Capture(PageOrder);
    }

    private void OnPageMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is not { } press || DataContext is not PdfPageEditorViewModel vm)
            return;
        _pointer = e.GetPosition(PageOrder);
        if (_moving.Count == 0)
        {
            if (Math.Abs(_pointer.X - press.Start.X) + Math.Abs(_pointer.Y - press.Start.Y) < DragThreshold)
                return;
            // A Ctrl + click that took the page out of the selection drags nothing.
            if (vm.IsBusy || !vm.SelectedPages.Contains(press.Page))
            {
                _pressed = null;
                return;
            }
            _moving = vm.SelectionInOrder();
            foreach (var page in _moving)
                page.IsMoving = true;
            PageOrder.Cursor = DragCursor;
            _scrollTimer.Start();
        }
        ShowDropLine();
    }

    private void OnPageReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressed is not { } press || DataContext is not PdfPageEditorViewModel vm)
            return;
        var moving = _moving;
        var keptSelection = _keptSelection;
        var target = moving.Count > 0 ? DropIndex(e.GetPosition(PageOrder)).Index : -1;
        EndDrag();
        e.Pointer.Capture(null);
        if (moving.Count > 0)
            vm.MovePages(moving, target);
        else if (keptSelection)
            vm.SelectOnly(press.Page);
        else if (vm.SelectedPages.Contains(press.Page))
            // The page clicked is the one shown large, also when Shift selected a range up to it.
            vm.SelectedPage = press.Page;
    }

    private void OnKeyDownWhileDragging(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _moving.Count == 0)
            return;
        EndDrag();
        e.Handled = true;
    }

    private void EndDrag()
    {
        _pressed = null;
        _keptSelection = false;
        _scrollTimer.Stop();
        foreach (var page in _moving)
            page.IsMoving = false;
        _moving = [];
        PageOrder.Cursor = null;
        DropLine.IsVisible = false;
    }

    private void ShowDropLine()
    {
        var (_, lineY) = DropIndex(_pointer);
        DropLine.Width = Math.Max(0, PageOrder.Bounds.Width - 16);
        Canvas.SetLeft(DropLine, 8);
        Canvas.SetTop(DropLine, Math.Clamp(lineY, 0, PageOrder.Bounds.Height) - DropLine.Height / 2);
        DropLine.IsVisible = true;
    }

    /// <summary>
    /// Where pages dropped at <paramref name="point"/> go: before the page whose upper half the point is over, after
    /// it on its lower half. Measured on the pages the strip has laid out, which are the ones in view.
    /// </summary>
    private (int Index, double LineY) DropIndex(Point point)
    {
        var best = (Index: PageOrder.ItemCount, LineY: 0d);
        var distance = double.MaxValue;
        foreach (var container in PageOrder.GetRealizedContainers())
        {
            var index = PageOrder.IndexFromContainer(container);
            if (index < 0 || !container.IsVisible || container.TranslatePoint(new Point(), PageOrder) is not { } origin)
                continue;
            var top = origin.Y;
            var bottom = top + container.Bounds.Height;
            var away = point.Y < top ? top - point.Y : point.Y > bottom ? point.Y - bottom : 0;
            if (away >= distance)
                continue;
            distance = away;
            best = point.Y < (top + bottom) / 2 ? (index, top) : (index + 1, bottom);
        }
        return best;
    }

    private void ScrollWhileDragging()
    {
        if (_moving.Count == 0 || PageOrder.Scroll is not ScrollViewer scroll)
            return;
        var height = PageOrder.Bounds.Height;
        var step = _pointer.Y < ScrollBand ? -ScrollStep : _pointer.Y > height - ScrollBand ? ScrollStep : 0;
        if (step == 0)
            return;
        var y = Math.Clamp(scroll.Offset.Y + step, 0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
        if (Math.Abs(y - scroll.Offset.Y) < 0.5)
            return;
        scroll.Offset = scroll.Offset.WithY(y);
        ShowDropLine();
    }
}
