// The donut toolbar: a ring completely divided into equal slices, one per preset.
// Used both as the live toolbar (RadialWindow) and as the editor in Settings → Donut toolbar (IsEditMode).
//
// Drawn by hand (no child controls) so it stays fast on low-end PCs and so every slice can be hit-tested by
// its exact wedge shape. Animations are tiny springs advanced on each rendered frame; with
// Motion.Enabled == false every value jumps straight to its target.
//
// After a drop the donut turns into a progress ring: see DonutMenu.Progress.cs. Group slices, which fan out a ring
// of presets, and the keyboard: DonutMenu.Groups.cs. Tab (or the wheel) flips the donut over: DonutMenu.Flip.cs.

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Filee.App.Services;

namespace Filee.App.Controls;

/// <summary>One slice of the donut.</summary>
public sealed class DonutItem
{
    public required string Id { get; init; }
    public required string Label { get; init; }

    /// <summary>Small second line, e.g. the target format. Hidden when equal to <see cref="Label"/>.</summary>
    public string? Caption { get; init; }

    public bool IsEnabled { get; init; } = true;

    /// <summary>Tooltip shown for disabled slices.</summary>
    public string? DisabledReason { get; init; }

    /// <summary>
    /// A group slice: converts nothing itself; hovering it fans these out in a small ring outside the donut, where they
    /// are clicked or dropped on like ordinary slices.
    /// </summary>
    public IReadOnlyList<DonutItem>? Children { get; init; }

    /// <summary>A group slice (<see cref="Children"/> set), even one left with nothing to fan out.</summary>
    public bool IsGroup => Children is not null;

    /// <summary>A group with something to fan out.</summary>
    public bool CanExpand => Children is { Count: > 0 };
}

/// <summary>Donut-shaped menu control.</summary>
public sealed partial class DonutMenu : Control
{
    public static readonly StyledProperty<IReadOnlyList<DonutItem>?> ItemsProperty =
        AvaloniaProperty.Register<DonutMenu, IReadOnlyList<DonutItem>?>(nameof(Items));
    public static readonly StyledProperty<double> OuterRadiusProperty =
        AvaloniaProperty.Register<DonutMenu, double>(nameof(OuterRadius), 150);
    public static readonly StyledProperty<double> HoleRatioProperty =
        AvaloniaProperty.Register<DonutMenu, double>(nameof(HoleRatio), 0.42);
    public static readonly StyledProperty<double> SliceOpacityProperty =
        AvaloniaProperty.Register<DonutMenu, double>(nameof(SliceOpacity), 0.96);
    public static readonly StyledProperty<string?> CenterTitleProperty =
        AvaloniaProperty.Register<DonutMenu, string?>(nameof(CenterTitle));
    public static readonly StyledProperty<string?> CenterSubtitleProperty =
        AvaloniaProperty.Register<DonutMenu, string?>(nameof(CenterSubtitle));
    public static readonly StyledProperty<bool> IsEditModeProperty =
        AvaloniaProperty.Register<DonutMenu, bool>(nameof(IsEditMode));
    public static readonly StyledProperty<bool> AllowContextEditProperty =
        AvaloniaProperty.Register<DonutMenu, bool>(nameof(AllowContextEdit));
    public static readonly StyledProperty<bool> ReserveSubRingProperty =
        AvaloniaProperty.Register<DonutMenu, bool>(nameof(ReserveSubRing));

    /// <summary>The theme's softness (Styles/Controls.axaml binds SoftRadius): rounds the corners of the slices.</summary>
    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<DonutMenu, CornerRadius>(nameof(CornerRadius));

    public static readonly StyledProperty<IBrush?> SliceBrushProperty =
        AvaloniaProperty.Register<DonutMenu, IBrush?>(nameof(SliceBrush), Brushes.White);
    public static readonly StyledProperty<IBrush?> SliceBorderBrushProperty =
        AvaloniaProperty.Register<DonutMenu, IBrush?>(nameof(SliceBorderBrush));
    public static readonly StyledProperty<IBrush?> CenterBrushProperty =
        AvaloniaProperty.Register<DonutMenu, IBrush?>(nameof(CenterBrush), Brushes.White);
    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<DonutMenu, IBrush?>(nameof(AccentBrush), Brushes.MediumPurple);
    public static readonly StyledProperty<IBrush?> OnAccentBrushProperty =
        AvaloniaProperty.Register<DonutMenu, IBrush?>(nameof(OnAccentBrush), Brushes.White);
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<DonutMenu, IBrush?>(nameof(Foreground), Brushes.Black);
    public static readonly StyledProperty<IBrush?> SecondaryForegroundProperty =
        AvaloniaProperty.Register<DonutMenu, IBrush?>(nameof(SecondaryForeground), Brushes.Gray);
    public static readonly StyledProperty<IBrush?> DangerBrushProperty =
        AvaloniaProperty.Register<DonutMenu, IBrush?>(nameof(DangerBrush), Brushes.IndianRed);

    /// <summary>Gap between slices in pixels (keeps the donut "full" while separating slices).</summary>
    private const double GapPixels = 3;
    private const double PopOut = 7;
    private const double OpenDuration = 0.42;
    private const double OpenStagger = 0.028;
    private const double DragStartDistance = 5;

    // Pencil (Material "edit") path in a 24×24 box.
    private static readonly Geometry PencilIcon = Geometry.Parse(
        "M3,17.25V21h3.75L17.81,9.94l-3.75-3.75L3,17.25z M20.71,7.04c0.39-0.39,0.39-1.02,0-1.41l-2.34-2.34c-0.39-0.39-1.02-0.39-1.41,0l-1.83,1.83l3.75,3.75L20.71,7.04z");

    private Spring[] _hover = [];
    private double[] _open = [];
    private Spring _centerPulse;
    private double _openClock = double.MaxValue;
    private TimeSpan? _lastFrame;
    private bool _animating;
    private Action? _closeCallback;
    private bool _closing;

    private int _hoverIndex = -1;
    private int _externalHighlight = -1;
    private bool _externalCenterHot;
    private int _dimmedIndex = -1;
    private int _dropTargetIndex = -1;

    // Edit mode drop preview: the slices make room where a dragged preset would land and a translucent slice shows
    // it there. Every slice's centre angle and width is a spring, so the others slide aside instead of jumping.
    private Spring[] _mid = [];
    private Spring[] _span = [];
    private Spring _previewMid;
    private Spring _previewSpan;
    private int _previewSlot = -1;
    private int _previewRemoved = -1;
    private string? _previewLabel;
    private (int Index, Point Start, PointerPressedEventArgs Args)? _press;

    static DonutMenu()
    {
        AffectsMeasure<DonutMenu>(OuterRadiusProperty, ReserveSubRingProperty);
        AffectsRender<DonutMenu>(HoleRatioProperty, SliceOpacityProperty, CenterTitleProperty, CenterSubtitleProperty,
            IsEditModeProperty, SliceBrushProperty, SliceBorderBrushProperty, CenterBrushProperty, AccentBrushProperty,
            OnAccentBrushProperty, ForegroundProperty, SecondaryForegroundProperty, SuccessBrushProperty, CornerRadiusProperty,
            CenterHintProperty, CenterHintWheelProperty);
        ItemsProperty.Changed.AddClassHandler<DonutMenu>((d, _) => d.OnItemsChanged());
    }

    public DonutMenu()
    {
        // A circle reads the same either way: slices keep their clockwise order in Arabic too, their labels are
        // shaped right to left (TextDirection).
        FlowDirection = FlowDirection.LeftToRight;
        ClipToBounds = false;
        Cursor = new Cursor(StandardCursorType.Hand);
        // A tab stop, so the keyboard reaches the editor's donut (DonutMenu.Accessibility.cs).
        Focusable = true;
    }

    public IReadOnlyList<DonutItem>? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public double OuterRadius { get => GetValue(OuterRadiusProperty); set => SetValue(OuterRadiusProperty, value); }
    public double HoleRatio { get => GetValue(HoleRatioProperty); set => SetValue(HoleRatioProperty, value); }
    public double SliceOpacity { get => GetValue(SliceOpacityProperty); set => SetValue(SliceOpacityProperty, value); }
    public string? CenterTitle { get => GetValue(CenterTitleProperty); set => SetValue(CenterTitleProperty, value); }
    public string? CenterSubtitle { get => GetValue(CenterSubtitleProperty); set => SetValue(CenterSubtitleProperty, value); }

    /// <summary>Settings editor mode: shows ✎ on hover, raises drag and edit events instead of invoking slices.</summary>
    public bool IsEditMode { get => GetValue(IsEditModeProperty); set => SetValue(IsEditModeProperty, value); }

    /// <summary>Live toolbar in click mode: right-click on a slice raises <see cref="SegmentContextRequested"/>.</summary>
    public bool AllowContextEdit { get => GetValue(AllowContextEditProperty); set => SetValue(AllowContextEditProperty, value); }

    /// <summary>
    /// Leave room for a group's ring even before the slices are known (the drag window is placed before files arrive),
    /// so the window doesn't grow, and the donut move, when they do.
    /// </summary>
    public bool ReserveSubRing { get => GetValue(ReserveSubRingProperty); set => SetValue(ReserveSubRingProperty, value); }

    public CornerRadius CornerRadius { get => GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    public IBrush? SliceBrush { get => GetValue(SliceBrushProperty); set => SetValue(SliceBrushProperty, value); }
    public IBrush? SliceBorderBrush { get => GetValue(SliceBorderBrushProperty); set => SetValue(SliceBorderBrushProperty, value); }
    public IBrush? CenterBrush { get => GetValue(CenterBrushProperty); set => SetValue(CenterBrushProperty, value); }
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public IBrush? OnAccentBrush { get => GetValue(OnAccentBrushProperty); set => SetValue(OnAccentBrushProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public IBrush? SecondaryForeground { get => GetValue(SecondaryForegroundProperty); set => SetValue(SecondaryForegroundProperty, value); }
    public IBrush? DangerBrush { get => GetValue(DangerBrushProperty); set => SetValue(DangerBrushProperty, value); }

    /// <summary>True while an external drag hovers the hole (drop = cancel).</summary>
    public bool IsCenterHot => _externalCenterHot;

    /// <summary>Currently emphasised slice (pointer hover or external highlight), or -1.</summary>
    public int ActiveIndex => _externalHighlight >= 0 ? _externalHighlight : _hoverIndex;

    /// <summary>A slice was clicked (live toolbar, click mode).</summary>
    public event EventHandler<int>? SegmentInvoked;

    /// <summary>A slice of a group's ring was clicked: (group, slice in the group).</summary>
    public event EventHandler<(int Group, int Sub)>? SubSegmentInvoked;

    /// <summary>The hovered slice of a group's ring, or -1. <see cref="ActiveIndexChanged"/> is raised when it changes.</summary>
    public int ActiveSubIndex => _subHover;

    /// <summary>Right-click or ✎ on a slice.</summary>
    public event EventHandler<int>? SegmentContextRequested;

    /// <summary>Right-click on a slice of a group's ring (live toolbar, click mode): (group, slice in the group).</summary>
    public event EventHandler<(int Group, int Sub)>? SubSegmentContextRequested;

    /// <summary>The hole was clicked.</summary>
    public event EventHandler? CenterInvoked;

    /// <summary>Edit mode: the user started dragging slice <c>Index</c>.</summary>
    public event EventHandler<(int Index, PointerEventArgs Args)>? SegmentDragStarted;

    /// <summary>The emphasised slice changed (-1 = none). Useful to update the centre text.</summary>
    public event EventHandler<int>? ActiveIndexChanged;

    private int Count => Items?.Count ?? 0;
    private Point Center => new(Bounds.Width / 2, Bounds.Height / 2);
    private double InnerRadius => OuterRadius * Math.Clamp(HoleRatio, 0.2, 0.75);

    // ───────────────────────── Public API ─────────────────────────

    /// <summary>Plays the "pop out from the centre" animation.</summary>
    public void PlayOpenAnimation()
    {
        ResetProgress();
        _closing = false;
        _closeCallback = null;
        if (!Motion.Enabled)
        {
            Array.Fill(_open, 1);
            InvalidateVisual();
            return;
        }
        Array.Fill(_open, 0);
        _openClock = 0;
        StartAnimation();
    }

    /// <summary>Shrinks the donut back into the centre, then calls <paramref name="done"/>.</summary>
    public void PlayCloseAnimation(Action done)
    {
        if (IsFlipping)
            FinishFlip();
        if (!Motion.Enabled || Count == 0)
        {
            done();
            return;
        }
        _closing = true;
        _closeCallback = done;
        _openClock = 0;
        StartAnimation();
    }

    /// <summary>Short "boing" of the centre, e.g. after a drop.</summary>
    public void PulseCenter()
    {
        if (!Motion.Enabled)
            return;
        _centerPulse.Velocity = 6;
        StartAnimation();
    }

    /// <summary>Highlights a slice from outside (drag-over in the live toolbar). -1 clears.</summary>
    public void SetExternalHighlight(int index, bool centerHot = false)
    {
        if (_externalHighlight == index && _externalCenterHot == centerHot)
            return;
        _externalHighlight = index;
        _externalCenterHot = centerHot;
        ActiveIndexChanged?.Invoke(this, ActiveIndex);
        StartAnimation();
    }

    /// <summary>How far slice <paramref name="index"/> is drawn lit up right now (0 to 1), for the drag log.</summary>
    public double LitAmount(int index) => index >= 0 && index < _hover.Length ? _hover[index].Value : 0;

    /// <summary>Edit mode: the slice being dragged is drawn faded.</summary>
    public void SetDimmedIndex(int index)
    {
        _dimmedIndex = index;
        InvalidateVisual();
    }

    /// <summary>Edit mode: highlights where a dragged item would land (-1 none, Count = empty ring).</summary>
    public void SetDropTarget(int index)
    {
        if (_dropTargetIndex == index)
            return;
        _dropTargetIndex = index;
        InvalidateVisual();
    }

    /// <summary>
    /// Edit mode: opens a gap at <paramref name="slot"/> (the position in the list after the drop) and shows a
    /// translucent slice labelled <paramref name="label"/> there. <paramref name="removed"/> is the slice being moved,
    /// which leaves its place. -1 closes the gap.
    /// </summary>
    public void SetDropPreview(int slot, string? label = null, int removed = -1)
    {
        if (_previewSlot == slot && _previewRemoved == removed && _previewLabel == label)
            return;
        if (_previewSlot < 0 && slot >= 0)
        {
            // The new slice grows out of nothing where it appears.
            _previewMid = new Spring { Value = DonutGeometry.MidAngle(slot, SlotCount(removed)) };
            _previewSpan = default;
        }
        _previewSlot = slot;
        _previewRemoved = removed;
        _previewLabel = label;
        StartAnimation();
    }

    /// <summary>The slot under <paramref name="p"/> when the donut has <paramref name="slots"/> slices; -1 off the ring.</summary>
    public int SlotAt(Point p, int slots)
    {
        var hit = DonutGeometry.HitTest(Center, p, InnerRadius, OuterRadius, Math.Max(slots, 1), PopOut);
        return hit.Kind == DonutHitKind.Segment ? hit.Index : -1;
    }

    /// <summary>Slices while previewing: the items, minus the one being moved, plus the preview.</summary>
    private int SlotCount(int removed) => Count + 1 - (removed >= 0 ? 1 : 0);

    /// <summary>Where slice <paramref name="i"/> belongs: its usual place, or shifted to make room for the preview.</summary>
    private (double Mid, double Span) LayoutTarget(int i)
    {
        var n = Count;
        if (_previewSlot < 0)
            return (DonutGeometry.MidAngle(i, n), DonutGeometry.Span(n));
        if (i == _previewRemoved)
            return (i < _mid.Length ? _mid[i].Value : DonutGeometry.MidAngle(i, n), 0); // it becomes the preview
        var slot = _previewRemoved >= 0 && i > _previewRemoved ? i - 1 : i;
        if (slot >= _previewSlot)
            slot++;
        var m = SlotCount(_previewRemoved);
        return (DonutGeometry.MidAngle(slot, m), DonutGeometry.Span(m));
    }

    /// <summary>Moves the slices and the preview towards their places. Returns true while they are still moving.</summary>
    private bool StepLayout(double dt, bool snap = false)
    {
        snap |= !Motion.Enabled;
        var active = false;
        for (var i = 0; i < _mid.Length; i++)
        {
            var (mid, span) = LayoutTarget(i);
            if (snap)
            {
                _mid[i] = new Spring { Value = mid };
                _span[i] = new Spring { Value = span };
                continue;
            }
            active |= _mid[i].Step(mid, dt, 420, 36);
            active |= _span[i].Step(span, dt, 420, 36);
        }
        var m = SlotCount(_previewRemoved);
        var previewMid = _previewSlot >= 0 ? DonutGeometry.MidAngle(_previewSlot, m) : _previewMid.Value;
        var previewSpan = _previewSlot >= 0 ? DonutGeometry.Span(m) : 0;
        if (snap)
        {
            _previewMid = new Spring { Value = previewMid };
            _previewSpan = new Spring { Value = previewSpan };
            return false;
        }
        active |= _previewMid.Step(previewMid, dt, 420, 36);
        active |= _previewSpan.Step(previewSpan, dt, 420, 36);
        return active;
    }

    /// <summary>Hit-tests a point in this control's coordinates.</summary>
    public DonutHit HitTest(Point p)
    {
        if (_progressMode)
            return HitTestProgress(p);
        if (IsEditMode && _hoverIndex >= 0 && _hoverIndex < Count &&
            Distance(p, EditButtonCenter(_hoverIndex)) <= 13)
            return new DonutHit(DonutHitKind.EditButton, _hoverIndex);
        var hit = DonutGeometry.HitTest(Center, p, InnerRadius, OuterRadius, Count, PopOut);
        if (ExpandedChildren is { } children)
        {
            var (inner, outer) = SubRadii;
            var parentMid = DonutGeometry.MidAngle(_expanded, Count);
            var subSpan = SubSpanOf(children.Count);
            var sub = DonutGeometry.SubIndexAt(Center, p, inner, outer, parentMid, children.Count, subSpan);
            if (sub >= 0)
                return new DonutHit(DonutHitKind.SubSegment, _expanded, sub);
            // On the way out to the ring the pointer crosses the gap before it: there it still belongs to the group, or
            // the ring would fold away (or the hint and the drag cursor flicker) just before it is reached. A neighbour's
            // outer half stays that neighbour, so a click or a drop there converts with it; TrackGroups keeps the ring
            // open over it (OnWayToRing).
            if (hit.Kind == DonutHitKind.Outside && Distance(p, Center) < inner && InFan(p, _expanded))
                return new DonutHit(DonutHitKind.Segment, _expanded);
        }
        return hit;
    }

    // ───────────────────────── Layout ─────────────────────────

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = SideFor(OuterRadius, ReserveSubRing || (!IsEditMode && Items?.Any(i => i.IsGroup) == true));
        return new Size(side, side);
    }

    private void OnItemsChanged()
    {
        var n = Count;
        _hover = new Spring[n];
        _open = Enumerable.Repeat(1.0, n).ToArray();
        _hoverIndex = -1;
        _externalHighlight = -1;
        _dimmedIndex = -1;
        _dropTargetIndex = -1;
        // A drop changes the items into exactly what the preview showed: start there, without a jump.
        _previewSlot = -1;
        _previewRemoved = -1;
        _previewSpan = default;
        _mid = Enumerable.Range(0, n).Select(i => new Spring { Value = DonutGeometry.MidAngle(i, n) }).ToArray();
        _span = Enumerable.Range(0, n).Select(_ => new Spring { Value = DonutGeometry.Span(n) }).ToArray();
        _expanded = _shownExpanded = _subHover = -1;
        ClearPending();
        _expand = default;
        // New files (or none): a flip of the old ones is over. A flip's own swap keeps going (SwapFaces).
        if (!_swapping)
            CancelFlip();
        _lastExternalAt = null;
        // The window is reused: a keyboard hold from the last time it was open must not swallow the first moves now.
        _keyboardHold = _ringFromKeys = false;
        _keyAnchor = _lastPointer = null;
        InvalidateMeasure();
        InvalidateVisual();
    }

}
