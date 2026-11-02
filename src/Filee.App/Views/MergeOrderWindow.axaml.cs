// The order of a merge: the cards can be dragged to another place with the mouse, and the Delete key takes the selected
// file out of the merge. The chosen order is the one the files merge in.

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
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
        // The list has the keys from the start: the arrows move the selection without a click first.
        Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            // After the window is active: activating it would otherwise take the focus back.
            Activate();
            FileOrder.Focus();
        }, DispatcherPriority.Background);
        // Handled too: the list selects a card on press, and the drag still has to start.
        FileOrder.AddHandler(PointerPressedEvent, OnCardPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        FileOrder.AddHandler(PointerMovedEvent, OnCardMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        FileOrder.AddHandler(PointerReleasedEvent, OnCardReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        // Tunnelled: the arrows reach the window before the list's own keys, and keep working after Alt+Up / Alt+Down.
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        FileOrder.FlowDirection = IsRightToLeft ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;
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
    /// The keys of the list, while the list itself has the focus: the arrows move the selection between the cards (mirrored
    /// in a right-to-left language), Alt+Up / Alt+Down move the selected file, and Delete takes it out of the merge.
    /// </summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MergeOrderViewModel vm || !IsListKey(e.Source))
            return;
        if (e.Key is Key.Up or Key.Down && e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            (e.Key == Key.Up ? vm.MoveUpCommand : vm.MoveDownCommand).Execute(null);
        else if (e.Key is Key.Left or Key.Right)
            vm.SelectNeighbour(e.Key == Key.Right ^ IsRightToLeft ? 1 : -1);
        else if (e.Key == Key.Delete)
            vm.RemoveSelected();
        else
            return;
        FocusSelected(vm);
        e.Handled = true;
    }

    /// <summary>
    /// Whether the keys go to the list: when the list has the focus, or a button of the bottom bar does (the focus can
    /// reach them from the list). The radio buttons and the colour picker keep their own arrows.
    /// </summary>
    private bool IsListKey(object? source) =>
        IsWithin(source, FileOrder) || ReferenceEquals(source, MoveUp) || ReferenceEquals(source, MoveDown)
        || ReferenceEquals(source, CancelMerge) || ReferenceEquals(source, StartMerge);

    /// <summary>Gives the list the focus back on the selected card, so the keys keep working after a move or a removal.</summary>
    private void FocusSelected(MergeOrderViewModel vm)
    {
        if (vm.SelectedFile is { } selected)
            FileOrder.ContainerFromIndex(vm.Files.IndexOf(selected))?.Focus();
    }

    /// <summary>Whether the language is written right to left: the cards are laid out, and read, from the right.</summary>
    private static bool IsRightToLeft => CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft;

    private static bool IsWithin(object? source, Visual container) =>
        source is Visual visual && (ReferenceEquals(visual, container) || visual.GetVisualAncestors().Contains(container));

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
