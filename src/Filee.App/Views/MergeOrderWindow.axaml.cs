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
            FocusSelected();
        }, DispatcherPriority.Background);
        // Tunnelled: the arrows and Alt+Up / Alt+Down are taken before the list's own navigation, which would move the
        // focus to a button at the edge of the row (and drop it when a card is moved).
        AddHandler(KeyDownEvent, OnKeyDownInList, RoutingStrategies.Tunnel);
        // Handled too: the list selects a card on press, and the drag still has to start.
        FileOrder.AddHandler(PointerPressedEvent, OnCardPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        FileOrder.AddHandler(PointerMovedEvent, OnCardMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        FileOrder.AddHandler(PointerReleasedEvent, OnCardReleased, RoutingStrategies.Bubble, handledEventsToo: true);
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

    /// <summary>
    /// The keys of the list. The arrows and Delete act only while the list itself has the focus, so they never move a
    /// selection while a button has it; Alt+Up / Alt+Down reorder from anywhere in the window. Text and choices keep their keys.
    /// </summary>
    private void OnKeyDownInList(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MergeOrderViewModel vm || IsEditing(e.Source))
            return;
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var inList = IsWithin(e.Source, FileOrder);
        var plain = e.KeyModifiers == KeyModifiers.None && inList;
        switch (e.Key)
        {
            case Key.Left when plain:
                vm.SelectLeftCommand.Execute(null);
                break;
            case Key.Right when plain:
                vm.SelectRightCommand.Execute(null);
                break;
            case Key.Up when plain:
                vm.SelectNeighbour(-ColumnCount());
                break;
            case Key.Down when plain:
                vm.SelectNeighbour(ColumnCount());
                break;
            case Key.Delete when plain:
                vm.DeleteSelectedCommand.Execute(null);
                break;
            case Key.Up when alt && vm.MoveUpCommand.CanExecute(null):
                vm.MoveUpCommand.Execute(null);
                break;
            case Key.Down when alt && vm.MoveDownCommand.CanExecute(null):
                vm.MoveDownCommand.Execute(null);
                break;
            default:
                return;
        }
        e.Handled = true;
        // The focus stays on the list's selected card; a key pressed on a button keeps the focus where it is.
        if (inList)
            FocusSelected();
    }

    private static bool IsWithin(object? source, Visual container) =>
        source is Visual visual && (ReferenceEquals(visual, container) || visual.GetVisualAncestors().Contains(container));

    /// <summary>Focus goes to the selected card, once the list has laid it out (a moved card is new to the list).</summary>
    private void FocusSelected() => Dispatcher.UIThread.Post(() =>
    {
        if (DataContext is not MergeOrderViewModel vm || vm.SelectedFile is not { } selected)
        {
            FileOrder.Focus();
            return;
        }
        if (FileOrder.ContainerFromIndex(vm.Files.IndexOf(selected)) is Control card)
            card.Focus();
    }, DispatcherPriority.Background);

    /// <summary>Whether the focus is in a box or a choice that keeps its own arrows (a text box, a number, a colour, a radio button, a sort list).</summary>
    private static bool IsEditing(object? source) =>
        source is Visual visual && (visual is TextBox or NumericUpDown or ColorPicker or RadioButton or ComboBox
                                    || visual.GetVisualAncestors().Any(ancestor => ancestor is TextBox or NumericUpDown or ColorPicker or RadioButton or ComboBox));

    /// <summary>How many cards are in a row: the cards from the first one that sit on its line.</summary>
    private int ColumnCount()
    {
        if (CardBounds(0) is not { } first)
            return 1;
        var columns = 0;
        for (var i = 0; i < CardCount() && CardBounds(i) is { } bounds && Math.Abs(bounds.Top - first.Top) < 1; i++)
            columns++;
        return Math.Max(1, columns);
    }

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

    /// <summary>Whether the language is written right to left: the cards are laid out, and read, from the right.</summary>
    private static bool IsRightToLeft => CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft;

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
