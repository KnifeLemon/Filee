// Drag & drop for the donut editor.
//
// Sources: a palette chip (add) or a donut slice (reorder / remove).
// Targets: a donut slice (insert at / move to that position), the empty ring or the hole (append),
//          or the palette box (remove a slice).
// A floating "ghost" chip follows the pointer, and the donut opens a gap where the drop would land, with a
// translucent slice of the dragged preset in it (DonutMenu.SetDropPreview). The Front / Back switch shows the other
// face of the profile, and the donut turns in to it as the open donut does on Tab.
//
// Chip tooltips: an available chip's tip lists the formats it takes (PresetFormatTip), as a card. Holding either Shift
// shows the whole list while it is open; Tab or the arrow keys open a chip's tip as hover does, and pressing or
// double-clicking a chip closes it.

using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Filee.App.Controls;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;

namespace Filee.App.Views.Pages;

public partial class ToolbarPage : UserControl
{
    private const double DragThreshold = 5;

    private enum Source { Chip, Slice }

    // CanAdd, IsGroup: fixed when the drag starts, so a chip that a search or a tab swaps under the pointer can't
    // change them. Pointer: the one holding the drag; a second finger or a pen on the screen is no part of it.
    private sealed record DragState(Source Source, string? PresetId, int SliceIndex, string Label, Point Start, IPointer Pointer, bool CanAdd = true, bool IsGroup = false)
    {
        public bool Active { get; set; }
    }

    private DragState? _drag;
    private ToolbarPageViewModel? _subscribed;

    // Each Shift key on its own: letting go of one while the other is held keeps the list whole.
    private bool _leftShift;
    private bool _rightShift;
    private TopLevel? _topLevel;
    // The format tips open now, by chip: hover and keyboard focus can each have one open.
    private readonly Dictionary<Control, PresetFormatTip> _openTips = [];

    private bool ShiftDown => _leftShift || _rightShift;

    public ToolbarPage()
    {
        InitializeComponent();

        // See every pointer event on the page, even while a chip / the donut has pointer capture.
        Root.AddHandler(PointerPressedEvent, OnRootPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        Root.AddHandler(PointerMovedEvent, OnRootPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        Root.AddHandler(PointerReleasedEvent, OnRootPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        // Direct: Avalonia raises this on the element that had the capture and on each of its ancestors. A palette
        // rebuild doesn't get here: the pressed chip has left the tree by then and its panel takes the capture, so the
        // drag goes on.
        Root.AddHandler(PointerCaptureLostEvent, OnRootCaptureLost, RoutingStrategies.Direct, handledEventsToo: true);
        // Before the list box, which doesn't move between the rows of its wrap panel.
        PaletteTabs.AddHandler(KeyDownEvent, OnTabsKeyDown, RoutingStrategies.Tunnel);

        Donut.SegmentDragStarted += (_, e) =>
        {
            if (Vm is null || e.Index >= Vm.DonutItems.Count || _drag is not null)
                return;
            var p = e.Args.GetPosition(Root);
            var item = Vm.DonutItems[e.Index];
            _drag = new DragState(Source.Slice, item.Id, e.Index, item.Label, p, e.Args.Pointer, IsGroup: item.IsGroup) { Active = true };
            Donut.SetDimmedIndex(e.Index);
            ShowGhost(_drag.Label, p);
        };
        Donut.SegmentContextRequested += async (_, index) =>
        {
            if (Vm is not null && _drag is null)
                await Vm.EditAsync(index);
        };
        DataContextChanged += (_, _) =>
        {
            if (_subscribed is not null)
                _subscribed.PropertyChanged -= OnViewModelChanged;
            _subscribed = Vm;
            if (_subscribed is not null)
                _subscribed.PropertyChanged += OnViewModelChanged;
        };
    }

    /// <summary>
    /// A drag belongs to the face and profile it started on, so another one ends it, as does a rebuild that moves the
    /// dragged slice. The switch showed the other face (its slices are in already): the donut turns in to it.
    /// </summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ToolbarPageViewModel.EditingBack):
                EndDrag();
                Donut.PlayFlipIn();
                break;
            case nameof(ToolbarPageViewModel.SelectedProfile):
            case nameof(ToolbarPageViewModel.DonutItems)
                when _drag is { Source: Source.Slice } drag && Vm?.DonutItems.ElementAtOrDefault(drag.SliceIndex)?.Id != drag.PresetId:
                EndDrag();
                break;
        }
    }

    private ToolbarPageViewModel? Vm => DataContext as ToolbarPageViewModel;

    // Shift is watched on the whole window: the pointer can be on a chip while the keyboard focus is elsewhere.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is null)
            return;
        _topLevel.AddHandler(KeyDownEvent, OnWindowShiftKey, RoutingStrategies.Tunnel, handledEventsToo: true);
        _topLevel.AddHandler(KeyUpEvent, OnWindowShiftKey, RoutingStrategies.Tunnel, handledEventsToo: true);
        if (_topLevel is WindowBase window)
        {
            window.Activated += OnWindowActivationChanged;
            window.Deactivated += OnWindowActivationChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_topLevel is not null)
        {
            _topLevel.RemoveHandler(KeyDownEvent, OnWindowShiftKey);
            _topLevel.RemoveHandler(KeyUpEvent, OnWindowShiftKey);
            if (_topLevel is WindowBase window)
            {
                window.Activated -= OnWindowActivationChanged;
                window.Deactivated -= OnWindowActivationChanged;
            }
        }
        _topLevel = null;
        _leftShift = _rightShift = false;
    }

    private void OnWindowShiftKey(object? sender, KeyEventArgs e)
    {
        var down = e.RoutedEvent == KeyDownEvent;
        switch (e.Key)
        {
            case Key.LeftShift:
                _leftShift = down;
                break;
            case Key.RightShift:
                _rightShift = down;
                break;
            default:
                return;
        }
        ExpandOpenTips();
    }

    /// <summary>
    /// A Shift let go in another window never reaches this one, so the keys count as up once the window loses (or,
    /// where the platform only reports that, regains) the keyboard.
    /// </summary>
    private void OnWindowActivationChanged(object? sender, EventArgs e)
    {
        _leftShift = _rightShift = false;
        ExpandOpenTips();
    }

    private void ExpandOpenTips()
    {
        foreach (var tip in _openTips.Values)
            tip.IsExpanded = ShiftDown;
    }

    /// <summary>Built when first shown; opens whole while Shift is held.</summary>
    private void OnChipTipOpening(object? sender, CancelRoutedEventArgs e)
    {
        if (sender is not Control chip || ToolTip.GetTip(chip) is not PresetFormatTip tip)
            return;
        tip.EnsureBuilt();
        tip.IsExpanded = ShiftDown;
        _openTips[chip] = tip;
    }

    /// <summary>
    /// A closed tip no longer follows Shift. Watched on IsOpen: Avalonia 12.1 raises ToolTipClosing while a tip opens and
    /// not when it closes.
    /// </summary>
    private void OnChipPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ToolTip.IsOpenProperty && e.NewValue is false && sender is Control chip)
            _openTips.Remove(chip);
    }

    /// <summary>The card look goes on the tooltip that shows a format tip (and comes off if it shows something else).</summary>
    private void OnFormatTipAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control { DataContext: PresetFormatTip } content && content.FindAncestorOfType<ToolTip>() is { } toolTip)
        {
            toolTip.Classes.Set("formats", true);
            toolTip.PointerEntered -= OnFormatTipPointerEntered;
            toolTip.PointerEntered += OnFormatTipPointerEntered;
            content.Tag = toolTip;
        }
    }

    /// <summary>
    /// The pointer on a format tip closes it. Avalonia keeps a tip open while the pointer is over it, and this one opens
    /// just below the chip, over the chips of the next rows.
    /// </summary>
    private void OnFormatTipPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is ToolTip { Content: PresetFormatTip tip } && _openTips.FirstOrDefault(p => p.Value == tip).Key is { } chip)
            ToolTip.SetIsOpen(chip, false);
    }

    private void OnFormatTipDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control { Tag: ToolTip toolTip } content && toolTip.Content is not PresetFormatTip)
        {
            toolTip.Classes.Set("formats", false);
            toolTip.PointerEntered -= OnFormatTipPointerEntered;
            content.Tag = null;
        }
    }

    /// <summary>Down / Up in the wrapped tab row: the tab on the next / previous row nearest below / above this one.</summary>
    private void OnTabsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Down or Key.Up) || e.KeyModifiers != KeyModifiers.None ||
            (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { } from)
            return;
        var down = e.Key == Key.Down;
        var tabs = PaletteTabs.GetRealizedContainers().OfType<ListBoxItem>().ToList();
        var beyond = tabs.Where(t => down ? t.Bounds.Top >= from.Bounds.Bottom - 0.5 : t.Bounds.Bottom <= from.Bounds.Top + 0.5).ToList();
        if (beyond.Count == 0)
            return;
        var row = down ? beyond.Min(t => t.Bounds.Top) : beyond.Max(t => t.Bounds.Top);
        var to = beyond.Where(t => Math.Abs(t.Bounds.Top - row) < 0.5)
            .MinBy(t => Math.Abs(t.Bounds.Center.X - from.Bounds.Center.X))!;
        PaletteTabs.SelectedIndex = PaletteTabs.IndexFromContainer(to);
        to.Focus(NavigationMethod.Directional);
        e.Handled = true;
    }

    private void OnChipPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: PaletteChip chip } border || _drag is not null)
            return;
        var point = e.GetCurrentPoint(border);
        if (point.Properties.IsRightButtonPressed)
        {
            _ = Vm?.EditChipAsync(chip.PresetId);
            e.Handled = true;
            return;
        }
        // A preset this donut can't run (shown by a search) can be edited, not added.
        if (!point.Properties.IsLeftButtonPressed || !chip.CanAdd)
            return;

        _drag = new DragState(Source.Chip, chip.PresetId, -1, chip.Label, e.GetPosition(Root), e.Pointer, chip.CanAdd, chip.IsGroup);
        e.Pointer.Capture(border);
        e.Handled = true;
    }

    /// <summary>Double-clicking a chip appends it to the donut. Its second press is a drag not started yet.</summary>
    private void OnChipDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Border { DataContext: PaletteChip { CanAdd: true } chip } && Vm is { } vm && _drag is not { Active: true })
            vm.Insert(chip.PresetId, vm.SliceCount);
    }

    /// <summary>The keyboard's double- and right-click on a chip: Enter appends it, Shift+F10 or the menu key edits it.</summary>
    private void OnChipKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Border { DataContext: PaletteChip chip } || Vm is not { } vm || _drag is not null)
            return;
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            if (chip.CanAdd)
                vm.Insert(chip.PresetId, vm.SliceCount);
            e.Handled = true;
        }
        else if (e.Key == Key.Apps || (e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift))
        {
            _ = vm.EditChipAsync(chip.PresetId);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Avalonia sends a second mouse button's press as a move, so a press here means the drag's release never came (or,
    /// from a pen, that its other button went down). It only ends the drag: it would go to what still has the capture,
    /// not to what it landed on.
    /// </summary>
    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_drag?.Pointer != e.Pointer)
            return;
        EndDrag();
        e.Handled = true;
    }

    private void OnRootCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_drag?.Pointer == e.Pointer)
            EndDrag();
    }

    private void OnRootPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_drag?.Pointer != e.Pointer)
            return;
        var p = e.GetPosition(Root);
        if (!_drag.Active)
        {
            if (Math.Abs(p.X - _drag.Start.X) < DragThreshold && Math.Abs(p.Y - _drag.Start.Y) < DragThreshold)
                return;
            _drag.Active = true;
        }

        ShowGhost(_drag.Label, p);
        var (sliceTarget, overPalette) = FindTarget(e);
        var removing = overPalette && _drag.Source == Source.Slice;
        Donut.SetDropPreview(removing ? -1 : sliceTarget, _drag.Label, _drag.Source == Source.Slice ? _drag.SliceIndex : -1, _drag.IsGroup);
        PaletteBox.Classes.Set("over", removing);
    }

    private void OnRootPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var drag = _drag;
        if (drag?.Pointer != e.Pointer)
            return;

        if (drag.Active && Vm is { } vm)
        {
            var (sliceTarget, overPalette) = FindTarget(e);
            switch (drag.Source)
            {
                case Source.Chip when drag.CanAdd && sliceTarget >= 0:
                    vm.Insert(drag.PresetId!, sliceTarget);
                    break;
                case Source.Slice when overPalette:
                    vm.RemoveAt(drag.SliceIndex);
                    break;
                case Source.Slice when sliceTarget >= 0 && sliceTarget < vm.SliceCount:
                    vm.Move(drag.SliceIndex, sliceTarget);
                    break;
            }
        }
        e.Pointer.Capture(null);
        EndDrag();
    }

    /// <summary>
    /// Where would a drop land? Returns the position in the list after the drop (SliceCount = append; -1 = not on the
    /// donut) and whether the pointer is over the palette. The slots are those of the donut with the gap open (one
    /// more slice for a new preset), so the gap doesn't move away from under the pointer.
    /// </summary>
    private (int SliceTarget, bool OverPalette) FindTarget(PointerEventArgs e)
    {
        var overPalette = new Rect(PaletteBox.Bounds.Size).Contains(e.GetPosition(PaletteBox));
        var point = e.GetPosition(Donut);
        var count = Vm?.SliceCount ?? 0;
        var adding = _drag?.Source == Source.Chip;
        var target = Donut.HitTest(point).Kind == DonutHitKind.Center
            ? adding ? count : -1 // drop in the hole = append
            : Donut.SlotAt(point, adding ? count + 1 : count);
        return (target, overPalette);
    }

    private void ShowGhost(string label, Point at)
    {
        GhostText.Text = label;
        Ghost.IsVisible = true;
        Canvas.SetLeft(Ghost, at.X + 12);
        Canvas.SetTop(Ghost, at.Y + 12);
    }

    private void EndDrag()
    {
        _drag = null;
        Ghost.IsVisible = false;
        Donut.SetDropTarget(-1);
        Donut.SetDropPreview(-1);
        Donut.SetDimmedIndex(-1);
        PaletteBox.Classes.Set("over", false);
    }
}
