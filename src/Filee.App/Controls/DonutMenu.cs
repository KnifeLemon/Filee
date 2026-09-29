// The donut toolbar: a ring completely divided into equal slices, one per preset.
// Used both as the live toolbar (RadialWindow) and as the editor in Settings → Donut toolbar (IsEditMode).
//
// Drawn by hand (no child controls) so it stays fast on low-end PCs and so every slice can be hit-tested by
// its exact wedge shape. Animations are tiny springs advanced on each rendered frame; with
// Motion.Enabled == false every value jumps straight to its target.
//
// After a drop the donut turns into a progress ring: see DonutMenu.Progress.cs.

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
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
    private (int Index, Point Start, PointerPressedEventArgs Args)? _press;

    static DonutMenu()
    {
        AffectsMeasure<DonutMenu>(OuterRadiusProperty);
        AffectsRender<DonutMenu>(HoleRatioProperty, SliceOpacityProperty, CenterTitleProperty, CenterSubtitleProperty,
            IsEditModeProperty, SliceBrushProperty, SliceBorderBrushProperty, CenterBrushProperty, AccentBrushProperty,
            OnAccentBrushProperty, ForegroundProperty, SecondaryForegroundProperty, SuccessBrushProperty);
        ItemsProperty.Changed.AddClassHandler<DonutMenu>((d, _) => d.OnItemsChanged());
    }

    public DonutMenu()
    {
        ClipToBounds = false;
        Cursor = new Cursor(StandardCursorType.Hand);
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

    /// <summary>Right-click or ✎ on a slice.</summary>
    public event EventHandler<int>? SegmentContextRequested;

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

    /// <summary>Hit-tests a point in this control's coordinates.</summary>
    public DonutHit HitTest(Point p)
    {
        if (_progressMode)
            return HitTestProgress(p);
        if (IsEditMode && _hoverIndex >= 0 && _hoverIndex < Count &&
            Distance(p, EditButtonCenter(_hoverIndex)) <= 13)
            return new DonutHit(DonutHitKind.EditButton, _hoverIndex);
        return DonutGeometry.HitTest(Center, p, InnerRadius, OuterRadius, Count, PopOut);
    }

    // ───────────────────────── Layout ─────────────────────────

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = 2 * (OuterRadius + PopOut + 14);
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
        InvalidateVisual();
    }

    // ───────────────────────── Input ─────────────────────────

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_progressMode)
        {
            OnProgressPointerMoved(p);
            return;
        }

        if (_press is { } press && IsEditMode && Distance(p, press.Start) > DragStartDistance && press.Index >= 0)
        {
            _press = null;
            SegmentDragStarted?.Invoke(this, (press.Index, e));
            return;
        }

        var hit = HitTest(p);
        SetHover(hit.Kind switch
        {
            DonutHitKind.Segment or DonutHitKind.EditButton => hit.Index,
            _ => -1,
        });
        ToolTip.SetTip(this, _hoverIndex >= 0 && Items![_hoverIndex] is { IsEnabled: false } item ? item.DisabledReason : null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_progressMode)
        {
            OnProgressPointerMoved(new Point(double.MaxValue / 4, 0));
            return;
        }
        if (_press is null)
            SetHover(-1);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        var hit = HitTest(point.Position);

        if (_progressMode)
        {
            if (point.Properties.IsLeftButtonPressed)
            {
                _press = (-1, point.Position, e);
                e.Handled = true;
            }
            return;
        }

        if (point.Properties.IsRightButtonPressed)
        {
            if (hit.Kind is DonutHitKind.Segment or DonutHitKind.EditButton && (IsEditMode || AllowContextEdit))
            {
                SegmentContextRequested?.Invoke(this, hit.Index);
                e.Handled = true;
            }
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
        {
            _press = (hit.Kind == DonutHitKind.Segment ? hit.Index : -1, point.Position, e);
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_press is null)
            return;
        _press = null;

        if (_progressMode)
        {
            OnProgressPointerReleased(e.GetPosition(this));
            e.Handled = true;
            return;
        }

        var hit = HitTest(e.GetPosition(this));
        switch (hit.Kind)
        {
            case DonutHitKind.EditButton:
                SegmentContextRequested?.Invoke(this, hit.Index);
                break;
            case DonutHitKind.Segment when !IsEditMode && Items![hit.Index].IsEnabled:
                SegmentInvoked?.Invoke(this, hit.Index);
                break;
            case DonutHitKind.Center when !IsEditMode:
                CenterInvoked?.Invoke(this, EventArgs.Empty);
                break;
        }
        e.Handled = true;
    }

    private void SetHover(int index)
    {
        if (_hoverIndex == index)
            return;
        _hoverIndex = index;
        ActiveIndexChanged?.Invoke(this, ActiveIndex);
        StartAnimation();
    }

    // ───────────────────────── Animation ─────────────────────────

    /// <summary>Critically-underdamped spring: gives the soft "boing" when a slice pops out.</summary>
    private struct Spring
    {
        public double Value;
        public double Velocity;

        public bool Step(double target, double dt, double stiffness = 320, double damping = 20)
        {
            var force = stiffness * (target - Value) - damping * Velocity;
            Velocity += force * dt;
            Value += Velocity * dt;
            var settled = Math.Abs(target - Value) < 0.001 && Math.Abs(Velocity) < 0.01;
            if (settled)
            {
                Value = target;
                Velocity = 0;
            }
            return !settled;
        }
    }

    private void StartAnimation()
    {
        InvalidateVisual();
        if (_animating)
            return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
            return;
        _animating = true;
        _lastFrame = null;
        top.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan time)
    {
        var dt = _lastFrame is { } last ? Math.Clamp((time - last).TotalSeconds, 0, 0.05) : 1 / 60.0;
        _lastFrame = time;
        var active = Advance(dt);

        InvalidateVisual();
        if (active && TopLevel.GetTopLevel(this) is { } top)
            top.RequestAnimationFrame(OnFrame);
        else
            _animating = false;
    }

    /// <summary>
    /// Advances every animation by <paramref name="dt"/> seconds. Returns true while more frames are needed.
    /// Internal so tests can step time deterministically.
    /// </summary>
    internal bool Advance(double dt)
    {
        var active = false;

        if (!Motion.Enabled)
        {
            for (var i = 0; i < _hover.Length; i++)
            {
                _hover[i].Value = i == ActiveIndex ? 1 : 0;
                _hover[i].Velocity = 0;
            }
            Array.Fill(_open, _closing ? 0 : 1);
            _centerPulse = default;
            if (_closing)
                FinishClose();
            active |= StepProgress(dt); // hold timers still run without animation
        }
        else
        {
            for (var i = 0; i < _hover.Length; i++)
                active |= _hover[i].Step(i == ActiveIndex ? 1 : 0, dt);
            active |= _centerPulse.Step(0, dt, 260, 12);
            active |= StepProgress(dt);

            if (_openClock < double.MaxValue)
            {
                _openClock += dt;
                var done = true;
                for (var i = 0; i < _open.Length; i++)
                {
                    var t = Math.Clamp((_openClock - i * (_closing ? 0.01 : OpenStagger)) / (_closing ? 0.16 : OpenDuration), 0, 1);
                    _open[i] = _closing ? 1 - EaseInCubic(t) : EaseOutBack(t);
                    done &= t >= 1;
                }
                if (done)
                {
                    _openClock = double.MaxValue;
                    if (_closing)
                        FinishClose();
                }
                else
                {
                    active = true;
                }
            }
        }

        return active;
    }

    private void FinishClose()
    {
        _closing = false;
        var callback = _closeCallback;
        _closeCallback = null;
        callback?.Invoke();
    }

    private static double EaseOutBack(double t)
    {
        const double c1 = 1.55;
        const double c3 = c1 + 1;
        return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
    }

    private static double EaseInCubic(double t) => t * t * t;

    // ───────────────────────── Rendering ─────────────────────────

    public override void Render(DrawingContext context)
    {
        var center = Center;
        var r2 = OuterRadius;
        var r1 = InnerRadius;
        var n = Count;
        var typeface = new Typeface(GetValue(TextElement.FontFamilyProperty), FontStyle.Normal, FontWeight.SemiBold);
        var typefaceRegular = new Typeface(GetValue(TextElement.FontFamilyProperty));

        if (_progressMode)
        {
            RenderProgress(context, center, r1, r2, typeface, typefaceRegular);
            return;
        }

        DrawSoftShadow(context, center, r2);

        if (n == 0)
        {
            var ring = CreateRing(center, r1, r2);
            context.DrawGeometry(WithOpacity(SliceBrush, SliceOpacity), BorderPen(), ring);
            if (_dropTargetIndex >= 0)
                context.DrawGeometry(WithOpacity(AccentBrush, 0.35), null, ring);
        }

        for (var i = 0; i < n; i++)
        {
            var item = Items![i];
            var open = i < _open.Length ? _open[i] : 1;
            if (open <= 0.001)
                continue;

            var hover = i < _hover.Length ? _hover[i].Value : 0;
            var mid = DonutGeometry.MidAngle(i, n);
            var offset = DonutGeometry.PointAt(new Point(0, 0), hover * PopOut, mid);

            // Open animation: scale up from the centre with a small twist.
            var scale = 0.55 + 0.45 * open;
            var rotate = (1 - Math.Min(open, 1)) * -0.35;
            var transform =
                Matrix.CreateTranslation(-center.X, -center.Y) *
                Matrix.CreateRotation(rotate) *
                Matrix.CreateScale(scale * (1 + hover * 0.025), scale * (1 + hover * 0.025)) *
                Matrix.CreateTranslation(center.X + offset.X, center.Y + offset.Y);

            using (context.PushTransform(transform))
            using (context.PushOpacity(Math.Clamp(open, 0, 1) * (i == _dimmedIndex ? 0.3 : 1)))
            {
                var wedge = CreateWedge(center, r1, r2, i, n);
                context.DrawGeometry(WithOpacity(SliceBrush, SliceOpacity * (item.IsEnabled ? 1 : 0.55)), BorderPen(), wedge);

                var emphasis = i == _dropTargetIndex ? 1.0 : hover;
                if (emphasis > 0.01 && item.IsEnabled)
                    context.DrawGeometry(WithOpacity(AccentBrush, 0.22 + 0.6 * Math.Clamp(emphasis, 0, 1)), null, wedge);

                var hot = (i == ActiveIndex || i == _dropTargetIndex) && item.IsEnabled;
                DrawLabel(context, item, center, (r1 + r2) / 2, mid, DonutGeometry.Span(n) * (r1 + r2) / 2 - 10,
                    r2 - r1 - 16, typeface, typefaceRegular, hot);
            }
        }

        DrawCenter(context, center, r1, typeface, typefaceRegular);

        if (IsEditMode && _hoverIndex >= 0 && _hoverIndex < n && _dimmedIndex < 0)
            DrawEditButton(context, EditButtonCenter(_hoverIndex));
    }

    private void DrawSoftShadow(DrawingContext context, Point center, double r2)
    {
        // A radial gradient ring fakes a soft drop shadow without blur effects (cheap on any GPU).
        var shadowRadius = r2 + 14;
        var brush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 0),
                new GradientStop(Color.FromArgb(0, 0, 0, 0), (r2 - 18) / shadowRadius),
                new GradientStop(Color.FromArgb(46, 0, 0, 0), (r2 - 2) / shadowRadius),
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 1),
            },
        };
        var opening = _open.Length == 0 ? 1 : Math.Clamp(_open.Average(), 0, 1);
        using (context.PushOpacity(opening))
            context.DrawEllipse(brush, null, new Point(center.X, center.Y + 4), shadowRadius, shadowRadius);
    }

    private void DrawCenter(DrawingContext context, Point center, double r1, Typeface bold, Typeface regular)
    {
        var pulse = 1 + _centerPulse.Value * 0.06;
        var radius = (r1 - 6) * pulse;
        var cancelHot = _externalCenterHot;
        context.DrawEllipse(CenterBrush, BorderPen(), center, radius, radius);
        if (cancelHot)
            context.DrawEllipse(WithOpacity(DangerBrush, 0.18), null, center, radius, radius);

        var maxWidth = radius * 1.6;
        var title = MakeText(CenterTitle ?? "", bold, 14, Foreground, maxWidth, maxLines: 2);
        var subtitle = MakeText(CenterSubtitle ?? "", regular, 11.5, cancelHot ? DangerBrush : SecondaryForeground, maxWidth, maxLines: 2);
        var total = title.Height + (string.IsNullOrEmpty(CenterSubtitle) ? 0 : subtitle.Height + 2);
        var y = center.Y - total / 2;
        DrawCentered(context, title, center.X, y);
        if (!string.IsNullOrEmpty(CenterSubtitle))
            DrawCentered(context, subtitle, center.X, y + title.Height + 2);
    }

    private void DrawLabel(DrawingContext context, DonutItem item, Point center, double radius, double angle,
        double maxWidth, double maxHeight, Typeface bold, Typeface regular, bool hot)
    {
        var foreground = hot ? OnAccentBrush : item.IsEnabled ? Foreground : SecondaryForeground;
        var width = Math.Clamp(maxWidth, 30, 120);
        var label = MakeText(item.Label, bold, 13, foreground, width, maxLines: 2);
        var showCaption = !string.IsNullOrEmpty(item.Caption) &&
                          !string.Equals(item.Caption, item.Label, StringComparison.OrdinalIgnoreCase) &&
                          label.Height + 14 < maxHeight;
        var caption = showCaption ? MakeText(item.Caption!, regular, 10.5, hot ? OnAccentBrush : SecondaryForeground, width) : null;

        var total = label.Height + (caption?.Height ?? 0);
        var anchor = DonutGeometry.PointAt(center, radius, angle);
        var y = anchor.Y - total / 2;
        DrawCentered(context, label, anchor.X, y);
        if (caption is not null)
            DrawCentered(context, caption, anchor.X, y + label.Height);
    }

    private Point EditButtonCenter(int index)
    {
        var mid = DonutGeometry.MidAngle(index, Count);
        return DonutGeometry.PointAt(Center, OuterRadius - 14 + PopOut, mid);
    }

    private void DrawEditButton(DrawingContext context, Point at)
    {
        context.DrawEllipse(AccentBrush, new Pen(CenterBrush, 2), at, 12, 12);
        var bounds = PencilIcon.Bounds;
        var s = 13 / Math.Max(bounds.Width, bounds.Height);
        var matrix = Matrix.CreateTranslation(-bounds.Center.X, -bounds.Center.Y) * Matrix.CreateScale(s, s) * Matrix.CreateTranslation(at.X, at.Y);
        using (context.PushTransform(matrix))
            context.DrawGeometry(OnAccentBrush, null, PencilIcon);
    }

    /// <summary>
    /// Draws text horizontally centred on <paramref name="centerX"/>.
    /// The text is laid out with <see cref="TextAlignment.Center"/> inside a box of <see cref="FormattedText.MaxTextWidth"/>,
    /// so the box (not the measured <see cref="FormattedText.Width"/>) must be centred — otherwise short labels
    /// drift right by (box − text) / 2.
    /// </summary>
    internal static void DrawCentered(DrawingContext context, FormattedText text, double centerX, double top) =>
        context.DrawText(text, new Point(centerX - text.MaxTextWidth / 2, top));

    private Pen? BorderPen() => SliceBorderBrush is null ? null : new Pen(SliceBorderBrush, 1);

    private static FormattedText MakeText(string text, Typeface typeface, double size, IBrush? brush, double maxWidth, int maxLines = 1) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, brush)
        {
            MaxTextWidth = Math.Max(10, maxWidth),
            MaxLineCount = maxLines,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
        };

    private static IBrush? WithOpacity(IBrush? brush, double opacity) => brush switch
    {
        ISolidColorBrush solid => new SolidColorBrush(solid.Color, solid.Opacity * opacity),
        _ => brush,
    };

    private static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    /// <summary>Wedge-shaped slice with a constant-width gap to its neighbours.</summary>
    private static StreamGeometry CreateWedge(Point c, double r1, double r2, int index, int count)
    {
        if (count == 1)
            return CreateRing(c, r1, r2);

        var span = DonutGeometry.Span(count);
        var mid = DonutGeometry.MidAngle(index, count);
        var outerHalf = span / 2 - GapPixels / 2 / r2;
        var innerHalf = span / 2 - GapPixels / 2 / r1;

        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(DonutGeometry.PointAt(c, r2, mid - outerHalf), true);
        ctx.ArcTo(DonutGeometry.PointAt(c, r2, mid + outerHalf), new Size(r2, r2), 0, outerHalf * 2 > Math.PI, SweepDirection.Clockwise);
        ctx.LineTo(DonutGeometry.PointAt(c, r1, mid + innerHalf));
        ctx.ArcTo(DonutGeometry.PointAt(c, r1, mid - innerHalf), new Size(r1, r1), 0, innerHalf * 2 > Math.PI, SweepDirection.CounterClockwise);
        ctx.EndFigure(true);
        return geometry;
    }

    /// <summary>Full ring (used for a single slice and for the empty donut).</summary>
    private static StreamGeometry CreateRing(Point c, double r1, double r2)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.SetFillRule(FillRule.EvenOdd);
        foreach (var r in new[] { r2, r1 })
        {
            ctx.BeginFigure(new Point(c.X, c.Y - r), true);
            ctx.ArcTo(new Point(c.X, c.Y + r), new Size(r, r), 0, false, SweepDirection.Clockwise);
            ctx.ArcTo(new Point(c.X, c.Y - r), new Size(r, r), 0, false, SweepDirection.Clockwise);
            ctx.EndFigure(true);
        }
        return geometry;
    }
}
