// The order of a merge: the cards can be dragged to another place with the mouse, and the Delete key takes the selected
// file out of the merge. The chosen order is the one the files merge in.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels;

namespace Filee.App.Views;

public partial class MergeOrderWindow : Window
{
    /// <summary>Pixels a press may move before it counts as a drag of a card rather than a click.</summary>
    private const double DragThreshold = 6;

    /// <summary>The card under a press: its index, and where the press began (in <see cref="FileOrder"/>).</summary>
    private (int Index, Point Start)? _pressed;

    /// <summary>The press has moved far enough to drag its card.</summary>
    private bool _dragging;

    public MergeOrderWindow()
    {
        InitializeComponent();
        Motion.Track(this);
        UiZoom.Track(this);
        // Handled too: the list selects a card on press, and the drag still has to start.
        FileOrder.AddHandler(PointerPressedEvent, OnCardPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        FileOrder.AddHandler(PointerMovedEvent, OnCardMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        FileOrder.AddHandler(PointerReleasedEvent, OnCardReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        // Tunnelled: the arrows reach the window before the list's own keys, and keep working after Alt+Up / Alt+Down.
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    public IReadOnlyList<string>? Result { get; private set; }

    private void OnMerge(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MergeOrderViewModel vm || vm.Files.Count < 2) return;
        Result = vm.OrderedFiles;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        // A card's X takes its file out with its own click: no drag starts there.
        if (IsInButton(e.Source))
            return;
        var at = e.GetPosition(FileOrder);
        if (!e.GetCurrentPoint(FileOrder).Properties.IsLeftButtonPressed || IndexOfCardAt(at) is not { } index)
            return;
        _pressed = (index, at);
        _dragging = false;
        e.Pointer.Capture(FileOrder);
    }

    private void OnCardMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is not { } press)
            return;
        var at = e.GetPosition(FileOrder);
        if (!_dragging && Math.Abs(at.X - press.Start.X) + Math.Abs(at.Y - press.Start.Y) < DragThreshold)
            return;
        _dragging = true;
        // Drawn above the other cards while it is dragged.
        if (FileOrder.ContainerFromIndex(press.Index) is { } card)
        {
            card.ZIndex = 1;
            card.RenderTransform = new TranslateTransform(at.X - press.Start.X, at.Y - press.Start.Y);
        }
    }

    private void OnCardReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressed is not { } press)
            return;
        var dragged = _dragging;
        var at = e.GetPosition(FileOrder);
        _pressed = null;
        _dragging = false;
        e.Pointer.Capture(null);

        // The card goes back to its place first: the layout is what the drop position is measured on.
        if (FileOrder.ContainerFromIndex(press.Index) is { } card)
        {
            card.RenderTransform = null;
            card.ZIndex = 0;
        }
        if (dragged && DataContext is MergeOrderViewModel vm)
            vm.MoveFile(press.Index, IndexNearest(at));
    }

    /// <summary>
    /// The left and right arrows move the selection between the cards; the Delete key takes the selected file out of the
    /// merge, while the list has the focus. Text boxes keep their keys.
    /// </summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MergeOrderViewModel vm || IsTextEntry(e.Source))
            return;
        if (e.Key is Key.Left or Key.Right)
        {
            vm.SelectNeighbour(e.Key == Key.Right ? 1 : -1);
            if (vm.SelectedFile is { } selected)
                FileOrder.ContainerFromIndex(vm.Files.IndexOf(selected))?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && e.Source is Visual source && (ReferenceEquals(source, FileOrder) || source.GetVisualAncestors().Contains(FileOrder)))
        {
            vm.RemoveSelected();
            e.Handled = true;
        }
    }

    private static bool IsTextEntry(object? source) =>
        source is TextBox or NumericUpDown or ColorPicker
        || source is Visual visual && visual.GetVisualAncestors().Any(ancestor => ancestor is TextBox or NumericUpDown or ColorPicker);

    private static bool IsInButton(object? source) =>
        source is Visual visual && (visual is Button || visual.GetVisualAncestors().Any(ancestor => ancestor is Button));

    /// <summary>The card the point is over, or null.</summary>
    private int? IndexOfCardAt(Point point)
    {
        for (var i = 0; i < CardCount(); i++)
            if (CardBounds(i) is { } bounds && bounds.Contains(point))
                return i;
        return null;
    }

    /// <summary>The card whose centre is nearest the point: where a dragged card is dropped.</summary>
    private int IndexNearest(Point point)
    {
        var nearest = 0;
        var distance = double.MaxValue;
        for (var i = 0; i < CardCount(); i++)
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

    private int CardCount() => (DataContext as MergeOrderViewModel)?.Files.Count ?? 0;

    /// <summary>The bounds of a card, in <see cref="FileOrder"/>'s coordinates; null before it is laid out.</summary>
    private Rect? CardBounds(int index) =>
        FileOrder.ContainerFromIndex(index) is { } card && card.TranslatePoint(new Point(), FileOrder) is { } origin
            ? new Rect(origin, card.Bounds.Size)
            : null;
}
