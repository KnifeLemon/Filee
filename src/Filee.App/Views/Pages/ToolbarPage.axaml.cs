// Drag & drop for the donut editor.
//
// Sources: a palette chip (add) or a donut slice (reorder / remove).
// Targets: a donut slice (insert at / move to that position), the empty ring or the hole (append),
//          or the palette box (remove a slice).
// A floating "ghost" chip follows the pointer, and the donut opens a gap where the drop would land, with a
// translucent slice of the dragged preset in it (DonutMenu.SetDropPreview). The Front / Back switch shows the other
// face of the profile, and the donut turns in to it as the open donut does on Tab.
// A click on a group slice opens the group (ToolbarPageViewModel.OpenGroup): the donut then shows its presets, and the
// same drags add, reorder and take them out of the group.
//
// The donut on screen: while Edit Donut Size is open, or a group is open in the editor, the donut is also shown at its
// real size next to this window (DonutPreviewWindow), filled and styled as it opens for the selected profile's files. It
// follows every change made here, and holds a group open (DonutMenu.HeldGroupId): the one open in the editor, or, once a
// ring setting changes, the one picked (Group shown, or clicked on it; the first until then), or an example ring on a
// donut with none.
// Where it goes: where it was last dragged (against this window), else beside this window, else on another screen;
// with no room anywhere, this window slides over to make room and back when the donut goes. Never over this window.
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
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.Core.Localization;
using Filee.Core.Settings;

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

    // The donut on screen (see the top of this file), made the first time it is needed.
    private DonutPreviewWindow? _preview;
    private RadialViewModel? _previewModel;

    /// <summary>The donut on screen, once made (null before).</summary>
    internal DonutPreviewWindow? Preview => _preview;

    /// <summary>Room between this window and the donut beside it.</summary>
    internal const int PreviewGap = 24;

    // What changes the donut's look (the settings and this donut's labels), and what changes what is on it.
    private static readonly HashSet<string> LookProperties =
    [
        nameof(ToolbarPageViewModel.OuterRadius), nameof(ToolbarPageViewModel.HoleRatio), nameof(ToolbarPageViewModel.SliceOpacity),
        nameof(ToolbarPageViewModel.ShowCaptions), nameof(ToolbarPageViewModel.GroupRingSize), nameof(ToolbarPageViewModel.GroupRingSpread),
        nameof(ToolbarPageViewModel.LabelScale), nameof(ToolbarPageViewModel.SliceLabels),
    ];

    private static readonly HashSet<string> ContentProperties =
    [
        nameof(ToolbarPageViewModel.IsAdjustingLook), nameof(ToolbarPageViewModel.OpenGroup), nameof(ToolbarPageViewModel.DonutItems),
        nameof(ToolbarPageViewModel.SelectedProfile), nameof(ToolbarPageViewModel.EditingBack), nameof(ToolbarPageViewModel.ShowsRing),
        nameof(ToolbarPageViewModel.ShownGroup), nameof(ToolbarPageViewModel.RingGroups),
    ];

    // This window slid over to make room for the donut: from where, to where (it slides back unless moved since).
    private (PixelPoint From, PixelPoint To)? _slid;
    private Avalonia.Threading.DispatcherTimer? _slide;

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
        // A click on a group slice opens it, as a folder: the editor then edits what it fans out.
        Donut.GroupOpenRequested += (_, index) =>
        {
            if (_drag is null)
                Vm?.OpenGroupAt(index);
        };
        DataContextChanged += (_, _) =>
        {
            if (_subscribed is not null)
                _subscribed.PropertyChanged -= OnViewModelChanged;
            _subscribed = Vm;
            if (_subscribed is not null)
                _subscribed.PropertyChanged += OnViewModelChanged;
            UpdatePreview(reload: true);
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
        if (e.PropertyName is { } name && (ContentProperties.Contains(name) || LookProperties.Contains(name)))
            UpdatePreview(reload: ContentProperties.Contains(name));
    }

    /// <summary>The donut on screen while it's wanted, refilled (<paramref name="reload"/>) or restyled; put away otherwise.</summary>
    private void UpdatePreview(bool reload)
    {
        if (Vm is not { } vm || vm.SelectedProfile?.Profile is not { } profile || !(vm.IsAdjustingLook || vm.IsEditingGroup))
        {
            HidePreview();
            SlideBack();
            return;
        }
        if (_topLevel is not Window { IsVisible: true, WindowState: not WindowState.Minimized } main)
        {
            HidePreview();
            return;
        }
        if (_preview is null || _previewModel is null)
        {
            _previewModel = new RadialViewModel(AppHost.Get<UserDataStore>(), AppHost.Get<ILocalizer>(), AppHost.Get<PresetAvailability>());
            _preview = new DonutPreviewWindow { DataContext = _previewModel };
            _preview.EscapePressed += () =>
            {
                Vm?.DoneAdjustingLookCommand.Execute(null);
                Vm?.CloseGroupCommand.Execute(null);
            };
            _preview.Moved += OnPreviewMoved;
            // A group clicked on it is the one held open from then on.
            _preview.DonutControl.GroupOpenRequested += (_, index) =>
            {
                if (_previewModel is { } model && index < model.Items.Count && model.Items[index].Id != RadialViewModel.ExampleGroupId)
                    Vm?.HoldGroup(model.Items[index].Id, model.IsBackFace);
            };
            _preview.Closed += (_, _) => (_preview, _previewModel) = (null, null);
        }
        var held = HeldGroup(vm, out var back);
        var shown = _preview.IsVisible;
        if (reload || !shown)
        {
            _previewModel.LoadPreview(profile, back);
            if (held == RadialViewModel.ExampleGroupId)
                _previewModel.ShowExampleGroup(Loc["toolbar.example_group"], Loc["toolbar.example_none"]);
        }
        else
            _previewModel.ApplyLook(profile);
        _preview.DonutControl.HeldGroupId = held;
        if (!shown)
            _preview.ShowAt(PlacePreview(main, _previewModel));
    }

    private static ILocalizer Loc => AppHost.Get<ILocalizer>();

    /// <summary>
    /// The group the donut on screen holds open, and the face it is on (<paramref name="back"/>): the one open in the
    /// editor; once a ring setting changed, the one shown (or the example); else none, on the face being edited.
    /// </summary>
    private static string? HeldGroup(ToolbarPageViewModel vm, out bool back)
    {
        back = vm.EditingBack;
        if (vm.OpenGroup is { } editing)
            return editing.Id;
        if (vm.ShowsExampleRing)
        {
            back = false;
            return RadialViewModel.ExampleGroupId;
        }
        if (vm.IsAdjustingLook && vm.ShowsRing && vm.ShownGroup is { } group)
        {
            back = group.Back;
            return group.Id;
        }
        return null;
    }

    private void HidePreview()
    {
        if (_preview is { IsVisible: true } preview)
            preview.Hide();
    }

    /// <summary>The donut was dragged: it opens there, against this window, from now on.</summary>
    private void OnPreviewMoved(PixelPoint center)
    {
        if (_topLevel is Window main)
            AppHost.Get<UserDataStore>().SaveDonutPreviewPlace(center.X - main.Position.X, center.Y - main.Position.Y);
    }

    /// <summary>
    /// Where the donut on screen goes (its centre, in screen pixels), never over this window: where it was last dragged,
    /// if all of it is on a screen there; beside this window, on whichever side its screen has room for the donut at its
    /// largest (a group open); on another screen with room for it; else this window slides over to make room (and back
    /// when the donut goes). On a screen too narrow for both, the donut goes to the side with more room.
    /// </summary>
    private PixelPoint PlacePreview(Window main, RadialViewModel donut)
    {
        var screen = main.Screens.ScreenFromWindow(main) ?? main.Screens.Primary;
        var scaling = OperatingSystem.IsMacOS() ? 1 : screen?.Scaling ?? 1;
        var side = (int)Math.Ceiling(DonutMenu.SideFor(donut.OuterRadius, true, donut.GroupRingSize) * scaling);
        var size = PixelSize.FromSize(main.FrameSize ?? main.Bounds.Size, scaling);
        var window = new PixelRect(main.Position, size);
        var area = screen?.WorkingArea ?? window;

        if (AppHost.Get<UserDataStore>().Settings.DonutPreviewPlace is { X: { } dx, Y: { } dy })
        {
            var center = new PixelPoint(window.X + dx, window.Y + dy);
            var box = new PixelRect(center.X - side / 2, center.Y - side / 2, side, side);
            if (main.Screens.All.Any(s => s.WorkingArea.Contains(box)))
                return center;
        }
        if (Beside(area, window, side) is { } beside)
            return beside;
        if (main.Screens.All.FirstOrDefault(s => !Equals(s, screen) && s.WorkingArea.Width >= side && s.WorkingArea.Height >= side)
            is { } other)
            return other.WorkingArea.Center;
        if (main.WindowState == WindowState.Normal && SlideFor(area, window, side) is { } x)
        {
            var to = new PixelPoint(x, window.Y);
            SlideMain(main, to);
            if (Beside(area, new PixelRect(to, size), side) is { } made)
                return made;
        }
        var y = Math.Clamp(window.Y + window.Height / 2, area.Y + side / 2, Math.Max(area.Y + side / 2, area.Bottom - side / 2));
        return window.Center.X > area.Center.X ? new PixelPoint(area.X + side / 2, y) : new PixelPoint(area.Right - side / 2, y);
    }

    /// <summary>
    /// Where <paramref name="window"/> slides to (its left edge) so the donut fits beside it on <paramref name="area"/>:
    /// the shorter way, left to leave room on the right or right to leave room on the left. Null if both don't fit.
    /// </summary>
    internal static int? SlideFor(PixelRect area, PixelRect window, int side)
    {
        if (window.Width + PreviewGap + side > area.Width)
            return null;
        var left = area.Right - PreviewGap - side - window.Width;
        var right = area.X + side + PreviewGap;
        return Math.Abs(left - window.X) <= Math.Abs(right - window.X) ? left : right;
    }

    /// <summary>The donut's centre beside <paramref name="window"/>, right then left, where <paramref name="area"/> has room; or null.</summary>
    internal static PixelPoint? Beside(PixelRect area, PixelRect window, int side)
    {
        var y = Math.Clamp(window.Y + window.Height / 2, area.Y + side / 2, Math.Max(area.Y + side / 2, area.Bottom - side / 2));
        if (window.Right + PreviewGap + side <= area.Right)
            return new PixelPoint(window.Right + PreviewGap + side / 2, y);
        if (window.X - PreviewGap - side >= area.X)
            return new PixelPoint(window.X - PreviewGap - side / 2, y);
        return null;
    }

    /// <summary>Slides this window to <paramref name="to"/> to make room for the donut, remembering where it was.</summary>
    private void SlideMain(Window main, PixelPoint to)
    {
        if (main.Position == to)
            return;
        _slid = (_slid?.From ?? main.Position, to);
        Slide(main, to);
    }

    /// <summary>The donut went: this window slides back to where it was, unless it was moved since it slid over.</summary>
    private void SlideBack()
    {
        if (_slid is not { } slid || _topLevel is not Window main)
            return;
        _slid = null;
        if (main.Position == slid.To)
            Slide(main, slid.From);
    }

    // Eased over a quarter of a second, or at once without animations.
    private void Slide(Window main, PixelPoint to)
    {
        _slide?.Stop();
        var from = main.Position;
        if (!Motion.Enabled)
        {
            main.Position = to;
            return;
        }
        var started = DateTime.UtcNow;
        _slide = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(16), Avalonia.Threading.DispatcherPriority.Render, (_, _) =>
        {
            var t = Math.Min(1, (DateTime.UtcNow - started).TotalSeconds / 0.25);
            var eased = 1 - Math.Pow(1 - t, 3);
            main.Position = new PixelPoint((int)Math.Round(from.X + (to.X - from.X) * eased), (int)Math.Round(from.Y + (to.Y - from.Y) * eased));
            if (t >= 1)
                _slide?.Stop();
        });
        _slide.Start();
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
            window.PropertyChanged += OnWindowPropertyChanged;
        }
        UpdatePreview(reload: true);
    }

    /// <summary>
    /// The window hidden (to the tray) or minimised puts the donut on screen away with it, and brings it back when shown.
    /// </summary>
    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.IsVisibleProperty || e.Property == Window.WindowStateProperty)
            UpdatePreview(reload: true);
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
                window.PropertyChanged -= OnWindowPropertyChanged;
            }
        }
        // Another page: the editor's modes end with the page, the donut on screen goes, and this window slides back.
        _slide?.Stop();
        SlideBack();
        _topLevel = null;
        Vm?.DoneAdjustingLookCommand.Execute(null);
        Vm?.CloseGroupCommand.Execute(null);
        _preview?.Close();
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
