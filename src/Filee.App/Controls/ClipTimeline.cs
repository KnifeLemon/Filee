// The clip window's timeline: a strip of small pictures of the video, the part outside the clip dimmed, and the clip
// framed in the accent colour between two handles. Dragging a handle moves that end, dragging inside the clip moves
// all of it, and pressing elsewhere on the strip brings the nearer handle there. Time always runs left to right, also
// in right-to-left languages.

using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Filee.App.ViewModels;

namespace Filee.App.Controls;

public sealed class ClipTimeline : Control
{
    public static readonly StyledProperty<double> DurationProperty =
        AvaloniaProperty.Register<ClipTimeline, double>(nameof(Duration));

    public static readonly StyledProperty<double> StartProperty =
        AvaloniaProperty.Register<ClipTimeline, double>(nameof(Start));

    public static readonly StyledProperty<double> EndProperty =
        AvaloniaProperty.Register<ClipTimeline, double>(nameof(End));

    public static readonly StyledProperty<ClipHandle> ActiveHandleProperty =
        AvaloniaProperty.Register<ClipTimeline, ClipHandle>(nameof(ActiveHandle));

    public static readonly StyledProperty<IReadOnlyList<Bitmap?>?> ThumbnailsProperty =
        AvaloniaProperty.Register<ClipTimeline, IReadOnlyList<Bitmap?>?>(nameof(Thumbnails));

    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<ClipTimeline, IBrush?>(nameof(AccentBrush), Brushes.MediumPurple);

    /// <summary>The colour on the handles' grips (on the accent).</summary>
    public static readonly StyledProperty<IBrush?> OnAccentBrushProperty =
        AvaloniaProperty.Register<ClipTimeline, IBrush?>(nameof(OnAccentBrush), Brushes.White);

    /// <summary>Behind the pictures, until they are there.</summary>
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<ClipTimeline, IBrush?>(nameof(TrackBrush), Brushes.LightGray);

    /// <summary>Laid over the video outside the clip, mostly opaque (the colour of the card the timeline is on).</summary>
    public static readonly StyledProperty<IBrush?> StageBrushProperty =
        AvaloniaProperty.Register<ClipTimeline, IBrush?>(nameof(StageBrush), Brushes.White);

    /// <summary>How much of <see cref="StageBrush"/> covers the video outside the clip.</summary>
    private const double DimOpacity = 0.68;

    public double Duration
    {
        get => GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public double Start
    {
        get => GetValue(StartProperty);
        set => SetValue(StartProperty, value);
    }

    public double End
    {
        get => GetValue(EndProperty);
        set => SetValue(EndProperty, value);
    }

    public ClipHandle ActiveHandle
    {
        get => GetValue(ActiveHandleProperty);
        set => SetValue(ActiveHandleProperty, value);
    }

    public IReadOnlyList<Bitmap?>? Thumbnails
    {
        get => GetValue(ThumbnailsProperty);
        set => SetValue(ThumbnailsProperty, value);
    }

    public IBrush? AccentBrush
    {
        get => GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public IBrush? OnAccentBrush
    {
        get => GetValue(OnAccentBrushProperty);
        set => SetValue(OnAccentBrushProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush? StageBrush
    {
        get => GetValue(StageBrushProperty);
        set => SetValue(StageBrushProperty, value);
    }

    /// <summary>A handle was dragged (or the strip pressed beside the clip): the end it moves and where to.</summary>
    public event Action<ClipHandle, double>? HandleMoved;

    /// <summary>The clip was dragged by its middle: where it starts now.</summary>
    public event Action<double>? ClipMoved;

    /// <summary>Width of a handle, outside the clip on either side.</summary>
    internal const double HandleWidth = 14;

    private const double StripRadius = 9;
    private const double FrameThickness = 3;

    /// <summary>How far beside a handle a press still takes it (thin clips, touch).</summary>
    private const double HandleSlop = 8;

    private enum Drag { None, Start, End, Clip }

    private Drag _drag;
    private double _grabOffset; // seconds between the pointer and what it drags (the handle, or the clip's start)
    private INotifyCollectionChanged? _watched;

    static ClipTimeline()
    {
        AffectsRender<ClipTimeline>(DurationProperty, StartProperty, EndProperty, ActiveHandleProperty, ThumbnailsProperty,
            AccentBrushProperty, OnAccentBrushProperty, TrackBrushProperty, StageBrushProperty);
        FocusableProperty.OverrideDefaultValue<ClipTimeline>(false);
    }

    public ClipTimeline()
    {
        FlowDirection = FlowDirection.LeftToRight;
        ClipToBounds = false;
    }

    /// <summary>The strip of pictures: the whole control but room for the handles at its sides.</summary>
    internal Rect Strip => new(HandleWidth, 0, Math.Max(1, Bounds.Width - 2 * HandleWidth), Bounds.Height);

    /// <summary>The x of <paramref name="seconds"/> on the strip.</summary>
    internal double XOf(double seconds) => Strip.X + (Duration > 0 ? Math.Clamp(seconds / Duration, 0, 1) : 0) * Strip.Width;

    /// <summary>The time at <paramref name="x"/> on the strip.</summary>
    internal double TimeAt(double x) => Duration > 0 ? Math.Clamp((x - Strip.X) / Strip.Width, 0, 1) * Duration : 0;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ThumbnailsProperty)
        {
            if (_watched is not null)
                _watched.CollectionChanged -= OnThumbnailsChanged;
            _watched = change.GetNewValue<IReadOnlyList<Bitmap?>?>() as INotifyCollectionChanged;
            if (_watched is not null)
                _watched.CollectionChanged += OnThumbnailsChanged;
        }
    }

    private void OnThumbnailsChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    // ───────────────────────── Drawing ─────────────────────────

    public override void Render(DrawingContext context)
    {
        var strip = Strip;
        if (strip.Width <= 2 || strip.Height <= 2)
            return;
        var accent = AccentBrush ?? Brushes.MediumPurple;

        using (context.PushClip(new RoundedRect(strip, StripRadius)))
        {
            context.FillRectangle(TrackBrush ?? Brushes.LightGray, strip);
            DrawPictures(context, strip);
            if (Duration > 0)
            {
                var dim = StageBrush ?? Brushes.White;
                var x0 = XOf(Start);
                var x1 = XOf(End);
                using (context.PushOpacity(DimOpacity))
                {
                    if (x0 > strip.Left)
                        context.FillRectangle(dim, new Rect(strip.Left, strip.Top, x0 - strip.Left, strip.Height));
                    if (x1 < strip.Right)
                        context.FillRectangle(dim, new Rect(x1, strip.Top, strip.Right - x1, strip.Height));
                }
            }
        }

        if (Duration <= 0)
            return;
        DrawClipFrame(context, accent);
    }

    /// <summary>Each picture fills its part of the strip (cut to fit, from the middle).</summary>
    private void DrawPictures(DrawingContext context, Rect strip)
    {
        if (Thumbnails is not { Count: > 0 } pictures)
            return;
        var cell = strip.Width / pictures.Count;
        for (var i = 0; i < pictures.Count; i++)
        {
            if (pictures[i] is not { } bitmap)
                continue;
            var target = new Rect(strip.X + i * cell, strip.Y, cell + 0.5, strip.Height); // overlap: no seams
            var size = bitmap.Size;
            if (size.Width <= 0 || size.Height <= 0)
                continue;
            var scale = Math.Max(target.Width / size.Width, target.Height / size.Height);
            var w = target.Width / scale;
            var h = target.Height / scale;
            var source = new Rect((size.Width - w) / 2, (size.Height - h) / 2, w, h);
            context.DrawImage(bitmap, source, target);
        }
    }

    private void DrawClipFrame(DrawingContext context, IBrush accent)
    {
        var top = 0.0;
        var bottom = Bounds.Height;
        var x0 = XOf(Start);
        var x1 = XOf(End);

        // Top and bottom of the frame, between the handles.
        context.FillRectangle(accent, new Rect(x0, top, Math.Max(0, x1 - x0), FrameThickness));
        context.FillRectangle(accent, new Rect(x0, bottom - FrameThickness, Math.Max(0, x1 - x0), FrameThickness));

        DrawHandle(context, accent, new Rect(x0 - HandleWidth, top, HandleWidth, bottom - top), left: true,
            ActiveHandle == ClipHandle.Start, _drag == Drag.Start);
        DrawHandle(context, accent, new Rect(x1, top, HandleWidth, bottom - top), left: false,
            ActiveHandle == ClipHandle.End, _drag == Drag.End);
    }

    private void DrawHandle(DrawingContext context, IBrush accent, Rect rect, bool left, bool active, bool dragging)
    {
        // Rounded on the outside, square where it meets the clip.
        var r = StripRadius;
        var corners = left
            ? new RoundedRect(rect, new CornerRadius(r, 0, 0, r))
            : new RoundedRect(rect, new CornerRadius(0, r, r, 0));
        if (active)
        {
            // A soft glow round the handle the arrow keys move and the large picture shows.
            var glow = new Pen(accent, dragging ? 7 : 5) { LineJoin = PenLineJoin.Round };
            using (context.PushOpacity(0.28))
                context.DrawRectangle(null, glow, corners.Rect, r, r);
        }
        using (context.PushOpacity(active ? 1 : 0.78))
            context.DrawRectangle(accent, null, corners);

        // Two short grip lines in the middle.
        var grip = new Pen(OnAccentBrush ?? Brushes.White, 1.6, lineCap: PenLineCap.Round);
        var cx = rect.Center.X;
        var cy = rect.Center.Y;
        var half = Math.Min(8, rect.Height / 5);
        context.DrawLine(grip, new Point(cx - 2, cy - half), new Point(cx - 2, cy + half));
        context.DrawLine(grip, new Point(cx + 2, cy - half), new Point(cx + 2, cy + half));
    }

    // ───────────────────────── Dragging ─────────────────────────

    /// <summary>What a press at <paramref name="x"/> takes: a handle, the clip, or (beside the clip) the nearer handle.</summary>
    private Drag HitTest(double x)
    {
        var x0 = XOf(Start);
        var x1 = XOf(End);
        var onStart = x >= x0 - HandleWidth - HandleSlop && x <= x0 + HandleSlop;
        var onEnd = x >= x1 - HandleSlop && x <= x1 + HandleWidth + HandleSlop;
        if (onStart && onEnd)
            return Math.Abs(x - (x0 - HandleWidth / 2)) <= Math.Abs(x - (x1 + HandleWidth / 2)) ? Drag.Start : Drag.End;
        if (onStart)
            return Drag.Start;
        if (onEnd)
            return Drag.End;
        if (x > x0 && x < x1)
            return Drag.Clip;
        return x <= x0 ? Drag.Start : Drag.End;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Duration <= 0 || !IsEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var x = e.GetPosition(this).X;
        _drag = HitTest(x);
        var time = TimeAt(x);
        switch (_drag)
        {
            case Drag.Start:
                // On the handle: it keeps where it was grabbed. Beside the clip: the handle jumps there.
                _grabOffset = x <= XOf(Start) + HandleSlop && x >= XOf(Start) - HandleWidth - HandleSlop ? Start - time : 0;
                HandleMoved?.Invoke(ClipHandle.Start, time + _grabOffset);
                break;
            case Drag.End:
                _grabOffset = x >= XOf(End) - HandleSlop && x <= XOf(End) + HandleWidth + HandleSlop ? End - time : 0;
                HandleMoved?.Invoke(ClipHandle.End, time + _grabOffset);
                break;
            case Drag.Clip:
                _grabOffset = Start - time;
                break;
        }
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var x = e.GetPosition(this).X;
        if (_drag == Drag.None)
        {
            Cursor = Duration <= 0 ? Cursor.Default
                : HitTest(x) == Drag.Clip ? new Cursor(StandardCursorType.SizeAll)
                : new Cursor(StandardCursorType.SizeWestEast);
            return;
        }
        var time = TimeAt(x) + _grabOffset;
        if (_drag == Drag.Clip)
            ClipMoved?.Invoke(time);
        else
            HandleMoved?.Invoke(_drag == Drag.Start ? ClipHandle.Start : ClipHandle.End, time);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        EndDrag(e.Pointer);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _drag = Drag.None;
        InvalidateVisual();
    }

    private void EndDrag(IPointer pointer)
    {
        if (_drag == Drag.None)
            return;
        _drag = Drag.None;
        pointer.Capture(null);
        InvalidateVisual();
    }
}
