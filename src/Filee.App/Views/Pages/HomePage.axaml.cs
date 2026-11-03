// Drop zone: files dropped on the Home page join the queue below it, and so do the files a double-click picks. The
// queue's button opens the donut (click mode) at itself for the queued files, in the order shown. Pictures, links and
// downloads dragged from a browser open the donut at the drop position, as before, or join the queue when Settings asks
// for it (saved to a temporary folder first). A card dragged to another
// place in the queue changes the order the files are converted or merged in. A click selects a card, and the Delete key
// takes the selected file out of the queue.

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels.Pages;
using Filee.Core.Settings;
using Filee.Core.Web;
using Microsoft.Extensions.Logging;

namespace Filee.App.Views.Pages;

public partial class HomePage : UserControl
{
    /// <summary>Pixels a press may move before it counts as a drag of a card rather than a click.</summary>
    private const double DragThreshold = 6;

    /// <summary>
    /// The first drag over the drop zone of this run is logged: a log without it after "drops don't work" means the
    /// drag never reached Filee (Windows refuses drops from Explorer into a Filee running as administrator).
    /// </summary>
    private static int _dragEnterLogged;

    /// <summary>The queue card under a press: its index, and where the press began (in <see cref="QueueList"/>).</summary>
    private (int Index, Point Start)? _pressed;

    /// <summary>The press has moved far enough to drag its card.</summary>
    private bool _dragging;

    public HomePage()
    {
        InitializeComponent();
        DropZone.DoubleTapped += OnDropZoneDoubleTapped;
        DragDrop.AddDragEnterHandler(DropZone, (_, e) =>
        {
            DropZone.Classes.Add("over");
            var files = DraggedFiles.Offered(e.DataTransfer);
            e.DragEffects = files ? DragDropEffects.Copy : DragDropEffects.None;
            if (Interlocked.Exchange(ref _dragEnterLogged, 1) == 0)
                AppHost.Get<ILogger<HomePage>>().LogInformation("First drag over the drop zone (files: {Files})", files);
        });
        DragDrop.AddDragOverHandler(DropZone, OfferDrop);
        DragDrop.AddDragLeaveHandler(DropZone, (_, _) => DropZone.Classes.Remove("over"));
        DragDrop.AddDropHandler(DropZone, OnDrop);
        // The queue takes drops too, so files can be added next to the ones already waiting.
        DragDrop.AddDragEnterHandler(QueueArea, OfferDrop);
        DragDrop.AddDragOverHandler(QueueArea, OfferDrop);
        DragDrop.AddDropHandler(QueueArea, OnDrop);
        QueueList.PointerPressed += OnQueuePressed;
        QueueList.PointerMoved += OnQueueMoved;
        QueueList.PointerReleased += OnQueueReleased;
        QueueList.Focusable = true;
        QueueList.FlowDirection = IsRightToLeft ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;
        // Tunnelled: the arrows and Delete reach the page before the list's own navigation, which moves the focus.
        AddHandler(KeyDownEvent, OnPageKeyDown, RoutingStrategies.Tunnel);
    }

    private HomePageViewModel? ViewModel => DataContext as HomePageViewModel;

    private static void OfferDrop(object? sender, DragEventArgs e) =>
        e.DragEffects = DraggedFiles.Offered(e.DataTransfer) ? DragDropEffects.Copy : DragDropEffects.None;

    private async void OnDropZoneDoubleTapped(object? sender, TappedEventArgs e)
    {
        e.Handled = true;
        if (TopLevel.GetTopLevel(this) is not { } top)
            return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true });

        var paths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .ToList();

        if (paths.Count == 0)
            return;

        TakeFiles(paths, DropZone.TranslatePoint(new Point(DropZone.Bounds.Width / 2, DropZone.Bounds.Height / 2), this) ?? new Point());
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        DropZone.Classes.Remove("over");
        var dragged = DraggedFiles.Read(e.DataTransfer, forDrop: true);
        if (dragged.Files.Count == 0)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        if (dragged.Web is null)
        {
            TakeFiles(dragged.Files, e.GetPosition(this));
            return;
        }
        if (AppHost.Get<UserDataStore>().Settings.CollectDroppedFiles)
        {
            _ = AddBrowserFilesAsync(dragged.Web);
            return;
        }
        var screenPoint = this.PointToScreen(e.GetPosition(this));
        AppHost.Get<RadialController>().ShowForFiles(screenPoint.X, screenPoint.Y, dragged.Files, web: dragged.Web);
    }

    /// <summary>
    /// Files dropped or picked: they join the list when the setting asks for it; otherwise the donut opens for them at the
    /// point, as before the list existed.
    /// </summary>
    private void TakeFiles(IReadOnlyList<string> paths, Point at)
    {
        if (AppHost.Get<UserDataStore>().Settings.CollectDroppedFiles)
        {
            ViewModel?.AddToQueue(paths);
            return;
        }
        var screenPoint = this.PointToScreen(at);
        AppHost.Get<RadialController>().ShowForFiles(screenPoint.X, screenPoint.Y, paths);
    }

    /// <summary>
    /// A browser drag joins the list: a list holds paths, so the files are saved first into a temporary folder of this
    /// drag. Those that can't be saved are left out and logged.
    /// </summary>
    private async Task AddBrowserFilesAsync(WebDrop web)
    {
        var saved = await AppHost.Get<ConversionService>().SaveBrowserFilesAsync(web,
            (placeholder, ex) => AppHost.Get<ILogger<HomePage>>().LogWarning(ex, "A browser file could not be saved for the queue: {Placeholder}", placeholder),
            Path.Combine(web.WorkFolder, "queue"));
        ViewModel?.AddToQueue(saved);
    }

    /// <summary>The queue's button: the donut opens at the button for the queued files, in the order they are in.</summary>
    private void OnConvertQueue(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { Queue.Count: > 0 } vm)
            return;
        var screenPoint = ConvertQueueButton.PointToScreen(new Point(ConvertQueueButton.Bounds.Width / 2, ConvertQueueButton.Bounds.Height / 2));
        AppHost.Get<RadialController>().ShowForFiles(screenPoint.X, screenPoint.Y, vm.QueuedPaths, keepOrder: true, onJob: vm.ConversionStarted);
        // The cards leave the list when that conversion completes (StagedFiles).
        vm.MarkSentToDonut();
    }

    private void OnQueuePressed(object? sender, PointerPressedEventArgs e)
    {
        var at = e.GetPosition(QueueList);
        if (!e.GetCurrentPoint(QueueList).Properties.IsLeftButtonPressed || IndexOfCardAt(at) is not { } index)
            return;
        _pressed = (index, at);
        _dragging = false;
        e.Pointer.Capture(QueueList);
    }

    private void OnQueueMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is not { } press)
            return;
        var at = e.GetPosition(QueueList);
        if (!_dragging && Math.Abs(at.X - press.Start.X) + Math.Abs(at.Y - press.Start.Y) < DragThreshold)
            return;
        _dragging = true;
        // Drawn above the other cards while it is dragged.
        if (QueueList.ContainerFromIndex(press.Index) is { } card)
        {
            card.ZIndex = 1;
            card.RenderTransform = new TranslateTransform(at.X - press.Start.X, at.Y - press.Start.Y);
        }
    }

    private void OnQueueReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressed is not { } press)
            return;
        var dragged = _dragging;
        var at = e.GetPosition(QueueList);
        _pressed = null;
        _dragging = false;
        e.Pointer.Capture(null);

        // The card goes back to its place first: the layout is what the drop position is measured on.
        if (QueueList.ContainerFromIndex(press.Index) is { } card)
        {
            card.RenderTransform = null;
            card.ZIndex = 0;
        }
        if (!dragged)
        {
            ViewModel?.SelectQueued(press.Index);
            QueueList.Focus();
            return;
        }
        var target = IndexNearest(at);
        if (target != press.Index)
            ViewModel?.MoveQueued(press.Index, target);
    }

    /// <summary>
    /// The keys of the queue. Alt+Up / Alt+Down move the selected card from anywhere on the page. The arrows and Delete act
    /// only while the queue itself has the focus, so they never move the selection while a button has it. A text box keeps
    /// its own keys.
    /// </summary>
    private void OnPageKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm || IsTextEntry(e.Source))
            return;
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var inQueue = IsWithin(e.Source, QueueList);
        if (alt && e.Key is Key.Up or Key.Down)
        {
            // Only with a card selected: with none, the keys are left to the page.
            if (!vm.Queue.Any(card => card.IsSelected))
                return;
            vm.MoveSelectedQueued(e.Key == Key.Down ? 1 : -1);
        }
        else if (!inQueue || e.KeyModifiers != KeyModifiers.None)
            return;
        else
            switch (e.Key)
            {
                case Key.Left:
                    vm.SelectLeftCommand.Execute(null);
                    break;
                case Key.Right:
                    vm.SelectRightCommand.Execute(null);
                    break;
                case Key.Up:
                    vm.SelectNeighbour(-ColumnCount());
                    break;
                case Key.Down:
                    vm.SelectNeighbour(ColumnCount());
                    break;
                case Key.Delete:
                    vm.DeleteSelectedCommand.Execute(null);
                    break;
                default:
                    return;
            }
        e.Handled = true;
        if (!alt)
            QueueList.Focus();
    }

    private static bool IsWithin(object? source, Visual container) =>
        source is Visual visual && (ReferenceEquals(visual, container) || visual.GetVisualAncestors().Contains(container));

    /// <summary>How many cards are in a row: the cards from the first one that sit on its line.</summary>
    private int ColumnCount()
    {
        if (CardBounds(0) is not { } first)
            return 1;
        var columns = 0;
        for (var i = 0; i < QueueCount() && CardBounds(i) is { } bounds && Math.Abs(bounds.Top - first.Top) < 1; i++)
            columns++;
        return Math.Max(1, columns);
    }

    internal static bool IsRightToLeft => CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft;

    private static bool IsTextEntry(object? source) =>
        source is TextBox or NumericUpDown or ColorPicker
        || source is Visual visual && visual.GetVisualAncestors().Any(ancestor => ancestor is TextBox or NumericUpDown or ColorPicker);

    /// <summary>The queue card the point is over, or null.</summary>
    private int? IndexOfCardAt(Point point)
    {
        for (var i = 0; i < QueueCount(); i++)
            if (CardBounds(i) is { } bounds && bounds.Contains(point))
                return i;
        return null;
    }

    /// <summary>The card whose centre is nearest the point: where a dragged card is dropped.</summary>
    private int IndexNearest(Point point)
    {
        var nearest = 0;
        var distance = double.MaxValue;
        for (var i = 0; i < QueueCount(); i++)
        {
            if (CardBounds(i) is not { } bounds)
                continue;
            var centre = bounds.Center;
            var d = (centre.X - point.X) * (centre.X - point.X) + (centre.Y - point.Y) * (centre.Y - point.Y);
            if (d < distance)
                (nearest, distance) = (i, d);
        }
        return nearest;
    }

    private int QueueCount() => ViewModel?.Queue.Count ?? 0;

    /// <summary>The bounds of a queue card, in <see cref="QueueList"/>'s coordinates; null before it is laid out.</summary>
    private Rect? CardBounds(int index) =>
        QueueList.ContainerFromIndex(index) is { } card && card.TranslatePoint(new Point(), QueueList) is { } origin
            ? new Rect(origin, card.Bounds.Size)
            : null;
}
