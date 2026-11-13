// Drag & drop for the donut editor.
//
// Sources: a palette chip (add) or a donut slice (reorder / remove). Targets: a donut slice (insert at / move to that
// position), the empty ring or the hole (append), or the palette box (remove a slice). A floating "ghost" chip follows
// the pointer, and the donut opens a gap where the drop would land, with a translucent slice of the dragged preset in
// it (DonutMenu.SetDropPreview). The Front / Back switch shows the other face of the profile, and the donut turns in to
// it as the open donut does on Tab. A click on a group slice opens the group (ToolbarPageViewModel.OpenGroup): the
// donut then shows its presets, and the same drags add, reorder and take them out of the group.
//
// The donut on screen: while Edit Donut Size is open, or a group is open in the editor, the donut is also shown at its
// real size next to this window (DonutPreviewWindow), filled and styled as it opens for the selected profile's files.
// It follows every change made here, and holds a group open (DonutMenu.HeldGroupId): the one open in the editor, or,
// once a ring setting changes, the one picked (Group shown, or clicked on it; the first until then), or an example ring
// on a donut with none. Where it goes: where it was last dragged (against this window), else beside this window (on a
// screen as tall as the donut), else on another screen; with no room, this window slides over to make room and back
// when the donut goes; with none at all (maximized, or a screen too small for both), over the start side of this
// window, above it.
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
        AddHandler(GotFocusEvent, (_, e) => _lastFocused = e.Source as Control, RoutingStrategies.Bubble, handledEventsToo: true);
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
        if (e.PropertyName == nameof(ToolbarPageViewModel.ProfileName))
            RenamePreview();
        if (e.PropertyName is nameof(ToolbarPageViewModel.OpenGroup) or nameof(ToolbarPageViewModel.IsAdjustingLook))
            Avalonia.Threading.Dispatcher.UIThread.Post(KeepFocus, Avalonia.Threading.DispatcherPriority.Loaded);
        // Not while the profile moves in the list: its selection is gone for a moment, the profile the same.
        if (e.PropertyName is { } name && (ContentProperties.Contains(name) || LookProperties.Contains(name))
            && !(name == nameof(ToolbarPageViewModel.SelectedProfile) && Vm is { IsRepositioning: true }))
            UpdatePreview(reload: ContentProperties.Contains(name));
    }

    /// <summary>
    /// The control that had the keyboard was hidden with its part of the page (the way back out of a group, Edit Donut
    /// Size, Done): the one that took its place has it now, rather than nothing.
    /// </summary>
    private void KeepFocus()
    {
        // Hidden, the focused control lets go of the keyboard (nothing has it then): the one this page last gave it to.
        var focused = _topLevel?.FocusManager?.GetFocusedElement() as Control;
        if (Vm is not { } vm || (focused ?? _lastFocused) is not { IsEffectivelyVisible: false } hidden
            || !hidden.GetVisualAncestors().Contains(this))
            return;
        Control next = vm.IsAdjustingLook ? LookDone : vm.IsEditingGroup ? GroupBack : EditSize;
        next.Focus(NavigationMethod.Tab);
    }

    // The control of this page that last had the keyboard (see KeepFocus).
    private Control? _lastFocused;

    /// <summary>
    /// The donut on screen while it's wanted: refilled when what is on it changed (<paramref name="reload"/>, and only
    /// then: a search typed in the palette rebuilds the editor but changes nothing on it), else restyled; put away
    /// otherwise, and this window back where it was.
    /// </summary>
    private void UpdatePreview(bool reload)
    {
        if (Vm is not { } vm || !(vm.IsAdjustingLook || vm.IsEditingGroup))
        {
            HidePreview();
            SlideBack(animate: true);
            return;
        }
        // No profile for now (a search in the profile list hid it): the donut goes until one is picked, the window
        // stays where it made room.
        if (vm.SelectedProfile?.Profile is not { } profile)
        {
            HidePreview();
            return;
        }
        // Hidden or minimized: the donut goes with it, and comes back with it. The window stays where it slid to
        // meanwhile (a minimized window reports no position to compare), and closing it remembers where the user had
        // it.
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
            _preview.DragFinished += CheckDrop;
            // Grown off its screen or onto this window (Size or Group ring size moved up): placed again; over the
            // window for want of room, it goes back beside it once it is small enough again.
            _preview.SizeChanged += (_, _) =>
            {
                if (_preview is { IsVisible: true, IsDragging: false, Place: { } place } preview && _topLevel is Window window
                    && !_sliding && (place.Over || !place.Fits(preview.Box.Width, FrameOf(window), ScreensOf(window))))
                    PlacePreview(window, moveWindow: false);
            };
            // A group clicked on it is the one held open from then on.
            _preview.DonutControl.GroupOpenRequested += (_, index) =>
            {
                if (_previewModel is { } model && index < model.Items.Count && model.Items[index].Id != RadialViewModel.ExampleGroupId)
                    Vm?.HoldGroup(model.Items[index].Id, model.IsBackFace);
            };
            _preview.Closed += (_, _) => (_preview, _previewModel, _previewContent) = (null, null, null);
        }
        var held = HeldGroup(vm, out var back);
        var shown = _preview.IsVisible;
        var content = ContentOf(vm, profile, held, back);
        if (!shown || reload && content != _previewContent)
        {
            _previewModel.LoadPreview(profile, back);
            if (held == RadialViewModel.ExampleGroupId)
                _previewModel.ShowExampleGroup(Loc["toolbar.example_group"], Loc["toolbar.example_none"]);
            _previewContent = content;
        }
        else
        {
            _previewModel.ApplyLook(profile);
            _previewContent = content; // a look saved with the library (Label scale) isn't new content
        }
        _preview.DonutControl.HeldGroupId = held;
        if (!shown)
        {
            // Back while this window slides back (or over): it stops there, as if it had landed there, and is placed
            // from there; where it was before it moved over is still where Done takes it.
            if (_sliding)
            {
                var at = SlidingAt(main);
                StopSlide();
                SlidTo = LandedAt = at;
                _stopped = true; // not moved by the user: where X11 reports it may lag behind
            }
            PlacePreview(main, moveWindow: true);
            _stopped = false;
        }
    }

    // What the donut on screen shows, so a rebuild of the editor that changed none of it (a palette search) doesn't
    // fill it again.
    private string? _previewContent;

    private string ContentOf(ToolbarPageViewModel vm, Filee.Core.Profiles.ToolbarProfile profile, string? held, bool back) =>
        string.Join('|', profile.Id, held, back, vm.LibraryVersion, Loc.Language,
            string.Join(',', AppHost.Get<UserDataStore>().Settings.TurnedOffFields));

    private static ILocalizer Loc => AppHost.Get<ILocalizer>();

    // Renamed: a donut filled for no file at all (the mixed profile that takes anything) has its name in the centre,
    // which changes with it. Not filled again for every key typed.
    private void RenamePreview()
    {
        if (_preview is { IsVisible: true } && _previewModel is { } model && Vm is { } vm && vm.SelectedProfile?.Profile is { } profile
            && RadialViewModel.PreviewFormats(profile).Count == 0 && HeldGroup(vm, out _) != RadialViewModel.ExampleGroupId)
            model.CenterTitle = Loc.DisplayName(profile);
    }

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

    /// <summary>
    /// The donut was dragged: it opens there, against this window, from now on (written once it rests).
    /// </summary>
    private void OnPreviewMoved(PixelPoint center)
    {
        if (_topLevel is not Window main)
            return;
        var store = AppHost.Get<UserDataStore>();
        store.Settings.DonutPreviewPlace = new WindowBounds { X = center.X - main.Position.X, Y = center.Y - main.Position.Y };
        DonutLookSave.Schedule(store);
    }

    /// <summary>
    /// The drag is over: dropped onto this window, where a click would bury it, the donut goes back beside it, and the
    /// place isn't kept. Left across two screens, or grown off one meanwhile, it is placed again.
    /// </summary>
    private void CheckDrop()
    {
        if (_preview is not { IsVisible: true } preview || _topLevel is not Window main)
            return;
        var frame = FrameOf(main);
        if (preview.Box.Intersects(frame))
        {
            var store = AppHost.Get<UserDataStore>();
            store.Settings.DonutPreviewPlace = null;
            DonutLookSave.Schedule(store);
            PlacePreview(main, moveWindow: false);
        }
        else if (preview.Place is { } place && !place.Fits(preview.Box.Width, frame, ScreensOf(main)))
            PlacePreview(main, moveWindow: false);
    }

    // macOS reports window positions and screens in points, the others in device pixels.
    private static double ScalingOf(Avalonia.Platform.Screen? screen) => OperatingSystem.IsMacOS() ? 1 : screen?.Scaling ?? 1;

    private static List<PreviewScreen> ScreensOf(Window window) =>
        window.Screens.All.Select(s => new PreviewScreen(s.WorkingArea, ScalingOf(s))).ToList();

    private static PixelRect FrameOf(Window window) => new(window.Position,
        PixelSize.FromSize(window.FrameSize ?? window.Bounds.Size, ScalingOf(window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary)));

    /// <summary>
    /// Puts the donut on screen where <see cref="DonutPreviewPlacement"/> says, moving this window over first when it
    /// must (<paramref name="moveWindow"/>: only as the donut first appears, never while the user works).
    /// </summary>
    private void PlacePreview(Window main, bool moveWindow)
    {
        if (_preview is not { } preview || _previewModel is not { } donut)
            return;
        var all = main.Screens.All.ToList();
        var home = main.Screens.ScreenFromWindow(main) ?? main.Screens.Primary;
        var screens = ScreensOf(main);
        var index = Math.Max(0, home is null ? 0 : all.FindIndex(s => s.WorkingArea == home.WorkingArea));
        var scaling = ScalingOf(home);
        var frame = main.FrameSize ?? main.Bounds.Size;
        var window = new PixelRect(main.Position, PixelSize.FromSize(frame, scaling));
        var current = DonutMenu.SideFor(donut.OuterRadius, donut.ReserveSubRing, donut.GroupRingSize);
        var dragged = AppHost.Get<UserDataStore>().Settings.DonutPreviewPlace is { X: { } dx, Y: { } dy } ? new PixelPoint(dx, dy) : (PixelPoint?)null;
        // Not on native Wayland, where a window can't place itself: it would only be made narrower.
        int? minWidth = moveWindow && main.WindowState == WindowState.Normal && !Filee.Platform.Linux.WaylandSupport.NativeWaylandRequested
            ? (int)Math.Ceiling((main.MinWidth + frame.Width - main.ClientSize.Width) * scaling)
            : null;
        // With nothing else left, it goes over the navigation and the profile list, which neither Edit Donut Size nor
        // an open group needs while the donut is tried out, and which are wider than Available presets: the panel's
        // Done and its values stay clear. They are on the right in Arabic.
        var overRight = FlowDirection == Avalonia.Media.FlowDirection.RightToLeft;
        var place = DonutPreviewPlacement.Place(window, screens, index, current, dragged, minWidth, overRight);
        if (place.MovedWindow is { } moved)
            MoveMain(main, moved, scaling);
        preview.ShowAt(place, (int)Math.Ceiling(current * scaling), main);
    }

    // This window moved over to make room for the donut: where it went (it goes back unless moved since), the slide
    // under way, and the width it was made narrower to (it goes back to its own unless the user resized it since).
    // Where it actually landed (a window manager may keep it off the menu bar) is the window's own, for closing to
    // judge by too (MainWindow.Landed): null while it is on its way, over or back.
    private bool _sliding;
    private double? _narrowedTo;

    // A slide was just stopped midway, the donut back on screen.
    private bool _stopped;

    private PixelPoint? SlidTo
    {
        get => (_topLevel as MainWindow)?.SlidTo;
        set
        {
            if (_topLevel is MainWindow main)
                main.SlidTo = value;
        }
    }

    // Two widths a window was given and reports: the same to a device pixel (Windows truncates to whole pixels).
    private static bool SameWidth(Window window, double a, double b) => Math.Abs(a - b) <= 1 / window.RenderScaling + 0.01;

    private PixelPoint? LandedAt
    {
        get => (_topLevel as MainWindow)?.Landed;
        set
        {
            if (_topLevel is MainWindow main)
                main.Landed = value;
        }
    }

    /// <summary>
    /// Moves this window to <paramref name="to"/> (narrower if it must be), remembering where it was.
    /// </summary>
    private void MoveMain(Window main, PixelRect to, double scaling)
    {
        if (main is MainWindow mw)
        {
            // Moved by the user since it last moved over (while the donut was put away): there is where it goes back,
            // as wide as it was unless the user resized it too.
            if (mw.RestPlace is { } rest && !_sliding && !_stopped && (LandedAt ?? SlidTo) is not null && !mw.StillWhereItSlid)
            {
                var narrowed = _narrowedTo is { } width && SameWidth(main, main.ClientSize.Width, width);
                mw.RestPlace = (main.Position, narrowed ? rest.ClientSize.WithHeight(main.ClientSize.Height) : main.ClientSize);
                if (!narrowed)
                    _narrowedTo = null;
            }
            mw.RestPlace ??= (main.Position, main.ClientSize);
        }
        var narrower = (main.FrameSize?.Width ?? main.Bounds.Width) * scaling - to.Width;
        if (narrower > 0)
        {
            // Recorded first: resizing reports it, and must not take it for the user's.
            _narrowedTo = Math.Max(main.MinWidth, main.ClientSize.Width - narrower / scaling);
            main.Width = _narrowedTo.Value;
        }
        SlidTo = to.Position;
        LandedAt = null;
        Slide(main, to.Position, () => LandedAt = main.Position);
    }

    /// <summary>
    /// The donut went: this window goes back to where it was, and as wide, unless the user moved it since it moved
    /// over. <paramref name="animate"/> false puts it back at once (leaving the page, closing, hiding).
    /// </summary>
    private void SlideBack(bool animate)
    {
        // Maximized meanwhile: it goes back once it is a normal window again (see OnWindowPropertyChanged).
        if (_topLevel is not MainWindow { WindowState: WindowState.Normal } main || main.RestPlace is not { } rest)
            return;
        var stillThere = _sliding || main.StillWhereItSlid;
        // Its own width back, unless the user resized it since it was made narrower (or it never was).
        var narrowed = _narrowedTo is { } width && SameWidth(main, main.ClientSize.Width, width);
        SlidTo = LandedAt = null;
        _narrowedTo = null;
        if (narrowed)
            main.Width = rest.ClientSize.Width;
        if (!stillThere)
        {
            StopSlide();
            main.RestPlace = null; // moved by the user: where it is now is theirs
            return;
        }
        if (animate)
            Slide(main, rest.Position, () => main.RestPlace = null);
        else
        {
            StopSlide();
            main.Position = rest.Position;
            main.RestPlace = null;
        }
    }

    private Avalonia.Threading.DispatcherTimer? _slide;

    /// <summary>How long this window takes to slide over or back (tests make it longer, to catch it midway).</summary>
    internal static TimeSpan SlideTime { get; set; } = TimeSpan.FromSeconds(0.25);

    private void StopSlide()
    {
        _slide?.Stop();
        _sliding = false;
        _slideAt = null;
    }

    // Where the slide under way last put this window: an X11 window reports a new place only once the window manager
    // has moved it, a frame or so later.
    private PixelPoint? _slideAt;

    private PixelPoint SlidingAt(Window main) => _sliding && _slideAt is { } at ? at : main.Position;

    // Eased over a quarter of a second, or at once without animations.
    private void Slide(Window main, PixelPoint to, Action done)
    {
        var from = SlidingAt(main);
        StopSlide();
        if (!Motion.Enabled || from == to)
        {
            main.Position = to;
            done();
            return;
        }
        var started = DateTime.UtcNow;
        var duration = SlideTime.TotalSeconds;
        _sliding = true;
        _slide = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(16), Avalonia.Threading.DispatcherPriority.Render, (_, _) =>
        {
            var t = Math.Min(1, (DateTime.UtcNow - started).TotalSeconds / duration);
            var eased = 1 - Math.Pow(1 - t, 3);
            _slideAt = new PixelPoint((int)Math.Round(from.X + (to.X - from.X) * eased), (int)Math.Round(from.Y + (to.Y - from.Y) * eased));
            main.Position = _slideAt.Value;
            if (t < 1)
                return;
            StopSlide();
            done();
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
            window.PositionChanged += OnWindowPositionChanged;
        }
        UpdatePreview(reload: true);
    }

    /// <summary>
    /// The window hidden (to the tray) or minimised puts the donut on screen away with it, and brings it back when
    /// shown.
    /// </summary>
    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.IsVisibleProperty || e.Property == Window.WindowStateProperty)
            UpdatePreview(reload: true);
        if (e.Property == Window.WindowStateProperty || e.Property == TopLevel.ClientSizeProperty)
            KeepClearOfWindow();
        if (e.Property == TopLevel.ClientSizeProperty)
            NoteUserResize();
        // A normal window again after Done (it was maximized meanwhile): it goes back, once its normal size and place
        // are in (some systems report the state first).
        if (e.Property == Window.WindowStateProperty)
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_preview is not { IsVisible: true } && _topLevel is MainWindow { WindowState: WindowState.Normal, RestPlace: not null })
                    SlideBack(animate: false);
            }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private bool _userResizePosted;

    /// <summary>
    /// This window was resized while it made room: the size the user gave it is theirs, kept on Done and remembered on
    /// close (<see cref="MainWindow.RestPlace"/>). Judged once the change has settled, on a normal window: Windows
    /// reports a maximized window's new size before its new state.
    /// </summary>
    private void NoteUserResize()
    {
        if (_userResizePosted)
            return;
        _userResizePosted = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _userResizePosted = false;
            if (_sliding || _topLevel is not MainWindow { IsVisible: true, WindowState: WindowState.Normal, RestPlace: { } rest } main)
                return;
            // Its width the user's unless it is the one it was made narrower to; its height always.
            var width = rest.ClientSize.Width;
            if (!SameWidth(main, main.ClientSize.Width, _narrowedTo ?? width) && !SameWidth(main, main.ClientSize.Width, width))
            {
                _narrowedTo = null;
                width = main.ClientSize.Width;
            }
            if (new Size(width, main.ClientSize.Height) is var size && size != rest.ClientSize)
                main.RestPlace = (rest.Position, size);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// This window was resized, maximized or moved while the donut is on screen: the donut is placed again, so it stays
    /// beside the window (or where it was dragged, against it) and never ends up under it.
    /// </summary>
    private void KeepClearOfWindow()
    {
        // Once the window's new size and frame are laid out (they follow the size it was given), and once for a burst
        // of moves.
        if (_clearPosted)
            return;
        _clearPosted = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _clearPosted = false;
            if (!_sliding && _topLevel is Window { IsVisible: true, WindowState: not WindowState.Minimized } main
                && _preview is { IsVisible: true, IsDragging: false })
                PlacePreview(main, moveWindow: false);
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private bool _clearPosted;

    private void OnWindowPositionChanged(object? sender, PixelPointEventArgs e) => KeepClearOfWindow();

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
                window.PositionChanged -= OnWindowPositionChanged;
            }
        }
        // Another page: the editor's modes end with the page, the donut on screen goes, and this window goes back.
        SlideBack(animate: false);
        if (_topLevel is MainWindow { RestPlace: not null } away)
        {
            // Maximized or minimized meanwhile: nothing is left to put it back once it is a normal window again, so
            // where it moved to is where it is, and where closing remembers; made narrower, it gets its own width back
            // then all the same (MainWindow.WidthOwed).
            StopSlide();
            if (_narrowedTo is not null)
                away.WidthOwed = away.RestPlace.Value.ClientSize.Width;
            away.RestPlace = null;
            SlidTo = LandedAt = null;
            _narrowedTo = null;
        }
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
