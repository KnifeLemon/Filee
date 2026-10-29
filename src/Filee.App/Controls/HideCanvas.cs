// The "Pixelate / blur" window's stage, drawn as the crop overlay's: the picture on the dimmed backdrop with soft accent
// glows, and over it the frames of the parts to hide, each already showing the picture pixelated or blurred inside it
// (a copy of the whole preview covered once per choice, drawn clipped to the frames, so drawing and dragging cost
// nothing). The selected frame has the crop frame's accent outline, round corner handles and edge pills, and a size chip;
// the others a thinner, lighter accent outline that turns full under the pointer. Dragging on the picture draws a new
// frame, dragging a frame moves it (snapping to the picture's centre lines), its handles size it, the wheel grows or
// shrinks it.

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using static Filee.App.Controls.CropCanvas;

namespace Filee.App.Controls;

public sealed class HideCanvas : Control
{
    public static readonly StyledProperty<Bitmap?> SourceImageProperty =
        AvaloniaProperty.Register<HideCanvas, Bitmap?>(nameof(SourceImage));

    /// <summary>The whole preview pixelated or blurred as chosen: what the frames show.</summary>
    public static readonly StyledProperty<Bitmap?> CoverImageProperty =
        AvaloniaProperty.Register<HideCanvas, Bitmap?>(nameof(CoverImage));

    public static readonly StyledProperty<HideAreaSet?> AreasProperty =
        AvaloniaProperty.Register<HideCanvas, HideAreaSet?>(nameof(Areas));

    /// <summary>Room kept free around the picture for the floating chips and the toolbar.</summary>
    public static readonly StyledProperty<Thickness> ImagePaddingProperty =
        AvaloniaProperty.Register<HideCanvas, Thickness>(nameof(ImagePadding));

    /// <summary>The picture's size in pixels: the size chip, and arrow keys move frames by whole pixels of it.</summary>
    public static readonly StyledProperty<PixelSize> PixelSizeProperty =
        AvaloniaProperty.Register<HideCanvas, PixelSize>(nameof(PixelSize));

    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<HideCanvas, IBrush?>(nameof(AccentBrush), Brushes.MediumPurple);

    public static readonly StyledProperty<IBrush?> StageBrushProperty =
        AvaloniaProperty.Register<HideCanvas, IBrush?>(nameof(StageBrush));

    public static readonly StyledProperty<IBrush?> ChipBackgroundProperty =
        AvaloniaProperty.Register<HideCanvas, IBrush?>(nameof(ChipBackground), Brushes.White);

    public static readonly StyledProperty<IBrush?> ChipForegroundProperty =
        AvaloniaProperty.Register<HideCanvas, IBrush?>(nameof(ChipForeground), Brushes.Black);

    /// <summary>False with reduced motion: every change shows at once.</summary>
    public static readonly StyledProperty<bool> PlayAnimationsProperty =
        AvaloniaProperty.Register<HideCanvas, bool>(nameof(PlayAnimations), true);

    public Bitmap? SourceImage { get => GetValue(SourceImageProperty); set => SetValue(SourceImageProperty, value); }
    public Bitmap? CoverImage { get => GetValue(CoverImageProperty); set => SetValue(CoverImageProperty, value); }
    public HideAreaSet? Areas { get => GetValue(AreasProperty); set => SetValue(AreasProperty, value); }
    public Thickness ImagePadding { get => GetValue(ImagePaddingProperty); set => SetValue(ImagePaddingProperty, value); }
    public PixelSize PixelSize { get => GetValue(PixelSizeProperty); set => SetValue(PixelSizeProperty, value); }
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public IBrush? StageBrush { get => GetValue(StageBrushProperty); set => SetValue(StageBrushProperty, value); }
    public IBrush? ChipBackground { get => GetValue(ChipBackgroundProperty); set => SetValue(ChipBackgroundProperty, value); }
    public IBrush? ChipForeground { get => GetValue(ChipForegroundProperty); set => SetValue(ChipForegroundProperty, value); }
    public bool PlayAnimations { get => GetValue(PlayAnimationsProperty); set => SetValue(PlayAnimationsProperty, value); }

    /// <summary>The smallest frame a drag makes or a handle leaves, in screen pixels.</summary>
    private const double MinSizePx = 6;

    /// <summary>How far the pointer has to move before a press on the picture becomes a new frame (else it is a click).</summary>
    private const double DragThreshold = 4;

    private enum Drag
    {
        None,
        Pending,
        New,
        Body,
        TopLeft,
        Top,
        TopRight,
        Right,
        BottomRight,
        Bottom,
        BottomLeft,
        Left,
    }

    private Drag _drag = Drag.None;
    private int _dragIndex = -1;
    private Point _dragStartPoint;
    private Rect _dragStartFrame;   // on screen
    private bool _snappedX, _snappedY;
    private int _hoverIndex = -1;
    private Drag _hoverHandle = Drag.None;

    // ───── Motion (the crop overlay's) ─────

    private const double AppearSeconds = 0.42;  // a new picture
    private const double HandleSeconds = 0.26;
    private Tween _appear = Tween.Done(1);
    private Tween _handlePop = Tween.Done(0);
    private Drag _poppedHandle = Drag.None;
    private bool _looping;

    private Bitmap? _soft;   // a tiny copy: the frames' fill until the covered copy is ready
    private FormattedText? _chipText;
    private string? _chipTextFor;
    private IBrush? _chipTextBrush;

    private static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);
    private static readonly Cursor SizeAllCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor SizeNorthSouthCursor = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor SizeWestEastCursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor TopLeftCursor = new(StandardCursorType.TopLeftCorner);
    private static readonly Cursor TopRightCursor = new(StandardCursorType.TopRightCorner);
    private static readonly Cursor CrossCursor = new(StandardCursorType.Cross);



    static HideCanvas()
    {
        AffectsRender<HideCanvas>(SourceImageProperty, CoverImageProperty, ImagePaddingProperty, AccentBrushProperty,
            StageBrushProperty, ChipBackgroundProperty, ChipForegroundProperty, PixelSizeProperty);
        FocusableProperty.OverrideDefaultValue<HideCanvas>(false);
    }

    // The picture is never mirrored, also in a right-to-left UI.
    public HideCanvas() => FlowDirection = FlowDirection.LeftToRight;

    /// <summary>The picture's place: as large as fits inside <see cref="ImagePadding"/>, centred.</summary>
    public Rect CalculateImageBounds()
    {
        if (SourceImage is null || Bounds.Width <= 0 || Bounds.Height <= 0)
            return default;
        var area = new Rect(Bounds.Size).Deflate(ImagePadding);
        if (area.Width <= 0 || area.Height <= 0)
            area = new Rect(Bounds.Size);
        var size = SourceImage.Size;
        var scale = Math.Min(area.Width / size.Width, area.Height / size.Height);
        var w = size.Width * scale;
        var h = size.Height * scale;
        return new Rect(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
    }

    /// <summary>A frame (in parts of the picture) on screen.</summary>
    public static Rect ToScreen(Rect area, Rect picture) => new(
        picture.X + area.X * picture.Width, picture.Y + area.Y * picture.Height,
        area.Width * picture.Width, area.Height * picture.Height);

    private static Rect ToPicture(Rect screen, Rect picture) => new(
        (screen.X - picture.X) / picture.Width, (screen.Y - picture.Y) / picture.Height,
        screen.Width / picture.Width, screen.Height / picture.Height);

    /// <summary>Ends every running animation (tests capture the settled picture).</summary>
    internal void FinishAnimations()
    {
        _appear = Tween.Done(1);
        _handlePop = Tween.Done(_handlePop.To);
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == AreasProperty)
        {
            if (change.GetOldValue<HideAreaSet?>() is { } old)
                old.Changed -= OnAreasChanged;
            if (change.GetNewValue<HideAreaSet?>() is { } areas)
                areas.Changed += OnAreasChanged;
            InvalidateVisual();
        }
        else if (change.Property == SourceImageProperty)
        {
            _soft?.Dispose();
            _soft = Soften(change.GetNewValue<Bitmap?>());
            CancelDrag();
            if (change.GetNewValue<Bitmap?>() is not null && PlayAnimations)
            {
                _appear = Tween.Begin(0, 1, AppearSeconds);
                StartLoop();
            }
            else
            {
                _appear = Tween.Done(1);
            }
        }
        else if (change.Property == PlayAnimationsProperty && !change.GetNewValue<bool>())
        {
            FinishAnimations();
        }
        base.OnPropertyChanged(change);
    }

    private void OnAreasChanged() => InvalidateVisual();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _soft ??= Soften(SourceImage);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _soft?.Dispose();
        _soft = null;
    }

    /// <summary>A copy at most 24 px across: drawn as large as the picture it is a soft blur, at no cost per frame.</summary>
    private static Bitmap? Soften(Bitmap? source)
    {
        if (source is null)
            return null;
        try
        {
            var size = source.PixelSize;
            var scale = Math.Min(1, 24.0 / Math.Max(size.Width, size.Height));
            var target = new PixelSize(Math.Max(1, (int)Math.Round(size.Width * scale)), Math.Max(1, (int)Math.Round(size.Height * scale)));
            return source.CreateScaledBitmap(target, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    // ───────────────────────── Drawing ─────────────────────────

    private bool IsDark => ActualThemeVariant == ThemeVariant.Dark;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var now = CropCanvas.Now();
        var bounds = new Rect(Bounds.Size);
        CropCanvas.DrawStage(context, bounds, StageBrush, AccentBrush, IsDark);

        var picture = CalculateImageBounds();
        if (SourceImage is null || picture.Width <= 0 || picture.Height <= 0)
            return;

        // A new picture settles in: a little smaller and transparent at first.
        var appear = EaseOutCubic(_appear.Value(now));
        var scale = 0.965 + 0.035 * appear;
        var center = picture.Center;
        using var appearing = context.PushTransform(
            Matrix.CreateTranslation(-center.X, -center.Y) * Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(center.X, center.Y));
        using var fading = context.PushOpacity(appear);

        context.DrawRectangle(StageBrush ?? Brushes.Gray, null, new RoundedRect(picture), PictureShadow);
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            context.DrawImage(SourceImage, new Rect(SourceImage.Size), picture);

        if (Areas is not { } areas)
            return;

        // Inside every frame the covered picture: crisp squares for pixelate (the copy has them at the preview's size).
        var cover = CoverImage ?? _soft;
        for (var i = 0; i < areas.Count; i++)
        {
            var frame = ToScreen(areas.Areas[i], picture);
            if (cover is null || frame.Width <= 0 || frame.Height <= 0)
                continue;
            using (context.PushClip(frame))
            using (context.PushRenderOptions(new RenderOptions
            {
                BitmapInterpolationMode = CoverImage is null ? BitmapInterpolationMode.HighQuality : BitmapInterpolationMode.MediumQuality,
            }))
                context.DrawImage(cover, new Rect(cover.Size), picture);
        }

        var accent = AccentBrush ?? Brushes.MediumPurple;
        for (var i = 0; i < areas.Count; i++)
        {
            if (i == areas.Selected)
                continue;
            var frame = Snap(ToScreen(areas.Areas[i], picture));
            if (i == _hoverIndex && _drag == Drag.None)
            {
                context.DrawRectangle(null, FrameShadowPen, frame);
                context.DrawRectangle(null, new Pen(accent, 2), frame);
            }
            else
            {
                using (context.PushOpacity(0.75))
                    context.DrawRectangle(null, new Pen(accent, 1.5), frame);
            }
        }

        DrawCentreGuides(context, picture, accent);
        if (areas.SelectedArea is { } selected)
        {
            var frame = ToScreen(selected, picture);
            DrawSelected(context, frame, accent, now);
            DrawSizeChip(context, frame, selected, picture, bounds);
        }
    }

    /// <summary>A frame on whole screen pixels, so its outline stays sharp.</summary>
    private static Rect Snap(Rect frame) =>
        new(Math.Round(frame.X), Math.Round(frame.Y), Math.Max(1, Math.Round(frame.Width)), Math.Max(1, Math.Round(frame.Height)));

    private void DrawSelected(DrawingContext context, Rect frame, IBrush accent, long now)
    {
        context.DrawRectangle(null, FrameShadowPen, frame);
        context.DrawRectangle(null, new Pen(accent, 2), frame);

        var pop = EaseOutBack(_handlePop.Value(now));
        // Smaller dots on a small frame, so they don't cover it.
        var dot = Math.Clamp(Math.Min(frame.Width, frame.Height) / 4, 3.5, 6.5);
        foreach (var handle in new[] { Drag.TopLeft, Drag.TopRight, Drag.BottomRight, Drag.BottomLeft })
        {
            var grow = handle == _poppedHandle ? pop : 0;
            DrawDot(context, accent, HandlePoint(frame, handle), dot + 2.5 * grow);
        }
        foreach (var handle in new[] { Drag.Top, Drag.Right, Drag.Bottom, Drag.Left })
        {
            var horizontal = handle is Drag.Top or Drag.Bottom;
            if ((horizontal ? frame.Width : frame.Height) < 90)
                continue;
            var grow = handle == _poppedHandle ? pop : 0;
            DrawPill(context, accent, HandlePoint(frame, handle), horizontal, 22 + 8 * grow);
        }
    }

    private void DrawCentreGuides(DrawingContext context, Rect picture, IBrush accent)
    {
        if (_drag != Drag.Body || !(_snappedX || _snappedY))
            return;
        var pen = new Pen(accent, 1, DashStyle.Dash);
        var x = Math.Round(picture.Center.X) + 0.5;
        var y = Math.Round(picture.Center.Y) + 0.5;
        if (_snappedX)
            context.DrawLine(pen, new Point(x, picture.Top), new Point(x, picture.Bottom));
        if (_snappedY)
            context.DrawLine(pen, new Point(picture.Left, y), new Point(picture.Right, y));
    }

    /// <summary>How far below the picture the size chip may go (the toolbar starts further down).</summary>
    private const double ChipReach = 40;

    private void DrawSizeChip(DrawingContext context, Rect frame, Rect area, Rect picture, Rect bounds)
    {
        var pixels = PixelSize;
        if (pixels.Width <= 0 || pixels.Height <= 0)
            return;
        var size = CropMath.PixelSize(area, pixels.Width, pixels.Height);
        var text = $"{size.Width} × {size.Height} px";
        var foreground = ChipForeground ?? Brushes.Black;
        if (_chipText is null || _chipTextFor != text || !ReferenceEquals(_chipTextBrush, foreground))
        {
            var typeface = new Typeface(GetValue(TextElement.FontFamilyProperty), FontStyle.Normal, FontWeight.SemiBold);
            _chipText = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 11.5, foreground);
            _chipTextFor = text;
            _chipTextBrush = foreground;
        }
        var chip = new Size(Math.Ceiling(_chipText.Width) + 20, Math.Ceiling(_chipText.Height) + 8);
        var room = new Rect(picture.Left, picture.Top, picture.Width, picture.Height + ChipReach).Intersect(bounds.Deflate(6));
        var origin = CropMath.SizeChipOrigin(frame, chip, room);
        context.DrawRectangle(ChipBackground, null, new RoundedRect(new Rect(origin, chip), chip.Height / 2), ChipShadow);
        context.DrawText(_chipText, new Point(origin.X + 10, origin.Y + 4));
    }

    private static Point HandlePoint(Rect frame, Drag handle) => handle switch
    {
        Drag.TopLeft => frame.TopLeft,
        Drag.TopRight => frame.TopRight,
        Drag.BottomRight => frame.BottomRight,
        Drag.BottomLeft => frame.BottomLeft,
        Drag.Top => new Point(frame.Center.X, frame.Top),
        Drag.Bottom => new Point(frame.Center.X, frame.Bottom),
        Drag.Left => new Point(frame.Left, frame.Center.Y),
        Drag.Right => new Point(frame.Right, frame.Center.Y),
        _ => frame.Center,
    };

    // ───────────────────────── Motion ─────────────────────────

    private void PopHandle(Drag handle)
    {
        if (handle is Drag.None or Drag.Pending or Drag.New or Drag.Body)
            handle = Drag.None;
        if (handle == _poppedHandle)
            return;
        _poppedHandle = handle;
        _handlePop = handle == Drag.None || !PlayAnimations
            ? Tween.Done(handle == Drag.None ? 0 : 1)
            : Tween.Begin(0, 1, HandleSeconds);
        StartLoop();
    }

    private void StartLoop()
    {
        InvalidateVisual();
        if (_looping || TopLevel.GetTopLevel(this) is not { } top)
            return;
        _looping = true;
        top.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan _)
    {
        InvalidateVisual();
        var now = CropCanvas.Now();
        if ((_appear.IsRunning(now) || _handlePop.IsRunning(now)) && TopLevel.GetTopLevel(this) is { } top)
        {
            top.RequestAnimationFrame(OnFrame);
            return;
        }
        _looping = false;
    }

    // ───────────────────────── Pointer ─────────────────────────

    /// <summary>What a press at <paramref name="point"/> would drag, and which frame.</summary>
    private (Drag Drag, int Index) HitTest(Point point, Rect picture)
    {
        if (Areas is not { } areas || picture.Width <= 0 || picture.Height <= 0)
            return (Drag.None, -1);
        if (areas.SelectedArea is { } selected)
        {
            var frame = ToScreen(selected, picture);
            // A small frame keeps room in its middle for moving it.
            var tolerance = Math.Clamp(Math.Min(frame.Width, frame.Height) / 3, 5, 14);
            foreach (var corner in new[] { Drag.TopLeft, Drag.TopRight, Drag.BottomLeft, Drag.BottomRight })
            {
                if (Distance(point, HandlePoint(frame, corner)) <= tolerance)
                    return (corner, areas.Selected);
            }
            var alongX = point.X >= frame.Left && point.X <= frame.Right;
            var alongY = point.Y >= frame.Top && point.Y <= frame.Bottom;
            if (alongX && Math.Abs(point.Y - frame.Top) <= tolerance) return (Drag.Top, areas.Selected);
            if (alongX && Math.Abs(point.Y - frame.Bottom) <= tolerance) return (Drag.Bottom, areas.Selected);
            if (alongY && Math.Abs(point.X - frame.Left) <= tolerance) return (Drag.Left, areas.Selected);
            if (alongY && Math.Abs(point.X - frame.Right) <= tolerance) return (Drag.Right, areas.Selected);
        }
        var index = areas.HitTest(ToPicture(new Rect(point, new Size(0, 0)), picture).TopLeft);
        if (index >= 0)
            return (Drag.Body, index);
        return picture.Contains(point) ? (Drag.Pending, -1) : (Drag.None, -1);
    }

    private static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private static Cursor CursorFor(Drag drag) => drag switch
    {
        Drag.TopLeft or Drag.BottomRight => TopLeftCursor,
        Drag.TopRight or Drag.BottomLeft => TopRightCursor,
        Drag.Top or Drag.Bottom => SizeNorthSouthCursor,
        Drag.Left or Drag.Right => SizeWestEastCursor,
        Drag.Body => SizeAllCursor,
        Drag.Pending or Drag.New => CrossCursor,
        _ => ArrowCursor,
    };

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_drag != Drag.None)
            return;
        _hoverHandle = Drag.None;
        _hoverIndex = -1;
        PopHandle(Drag.None);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var picture = CalculateImageBounds();
        if (Areas is not { } areas || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || picture.Width <= 0)
            return;
        var point = e.GetPosition(this);
        var (drag, index) = HitTest(point, picture);
        if (drag == Drag.None)
            return;

        _drag = drag;
        _dragIndex = index;
        _dragStartPoint = point;
        _snappedX = _snappedY = false;
        if (index >= 0)
        {
            areas.Select(index);
            _dragStartFrame = ToScreen(areas.Areas[index], picture);
            areas.Begin();
        }
        e.Pointer.Capture(this);
        e.Handled = true;
        PopHandle(drag);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var picture = CalculateImageBounds();
        if (Areas is not { } areas || picture.Width <= 0 || picture.Height <= 0)
            return;
        var point = e.GetPosition(this);

        if (_drag == Drag.None)
        {
            var (hover, index) = HitTest(point, picture);
            Cursor = CursorFor(hover);
            if (index != _hoverIndex)
            {
                _hoverIndex = index;
                InvalidateVisual();
            }
            _hoverHandle = hover;
            PopHandle(hover);
            return;
        }

        var start = Clamp(_dragStartPoint, picture);
        var at = Clamp(point, picture);
        switch (_drag)
        {
            case Drag.Pending:
                if (Distance(point, _dragStartPoint) < DragThreshold)
                    return;
                areas.Begin();
                if (!areas.Add(ToPicture(FrameBetween(start, at, picture), picture)))
                {
                    areas.End();
                    _drag = Drag.None;
                    return;
                }
                _drag = Drag.New;
                _dragIndex = areas.Selected;
                break;
            case Drag.New:
                areas.Set(_dragIndex, ToPicture(FrameBetween(start, at, picture), picture));
                break;
            case Drag.Body:
                {
                    var x = SafeClamp(_dragStartFrame.X + point.X - _dragStartPoint.X, picture.Left, picture.Right - _dragStartFrame.Width);
                    var y = SafeClamp(_dragStartFrame.Y + point.Y - _dragStartPoint.Y, picture.Top, picture.Bottom - _dragStartFrame.Height);
                    (x, _snappedX) = CropMath.SnapToCentre(x, _dragStartFrame.Width, picture.Left, picture.Right);
                    (y, _snappedY) = CropMath.SnapToCentre(y, _dragStartFrame.Height, picture.Top, picture.Bottom);
                    var moved = new Rect(x, y, _dragStartFrame.Width, _dragStartFrame.Height);
                    // Kept at its size in parts of the picture: rounding on the way to the screen and back must not resize it.
                    var old = areas.Areas[_dragIndex];
                    var next = ToPicture(moved, picture);
                    areas.Set(_dragIndex, new Rect(next.X, next.Y, old.Width, old.Height));
                    break;
                }
            default:
                areas.Set(_dragIndex, ToPicture(Resize(_drag, _dragStartFrame, point - _dragStartPoint, picture), picture));
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == Drag.None)
            return;
        // A click on the picture beside every frame: nothing selected.
        if (_drag == Drag.Pending)
            Areas?.Select(-1);
        EndDrag();
        e.Pointer.Capture(null);
        e.Handled = true;
        PopHandle(_hoverHandle);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_drag != Drag.None)
        {
            EndDrag();
            PopHandle(Drag.None);
        }
    }

    private void EndDrag()
    {
        var wasDragging = _drag is not (Drag.None or Drag.Pending);
        _drag = Drag.None;
        _dragIndex = -1;
        _snappedX = _snappedY = false;
        if (wasDragging)
            Areas?.End();
        InvalidateVisual();
    }

    private void CancelDrag()
    {
        if (_drag != Drag.None)
            EndDrag();
        _hoverIndex = -1;
    }

    /// <summary>The wheel grows or shrinks the selected frame about its centre.</summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var picture = CalculateImageBounds();
        if (Areas is not { SelectedArea: { } selected } areas || picture.Width <= 0 || Math.Abs(e.Delta.Y) < 0.001 || _drag != Drag.None)
            return;
        var frame = ToScreen(selected, picture);
        var factor = e.Delta.Y > 0 ? 1.05 : 0.95;
        var w = SafeClamp(frame.Width * factor, MinSizePx, picture.Width);
        var h = SafeClamp(frame.Height * factor, MinSizePx, picture.Height);
        var x = SafeClamp(frame.Center.X - w / 2, picture.Left, picture.Right - w);
        var y = SafeClamp(frame.Center.Y - h / 2, picture.Top, picture.Bottom - h);
        areas.Set(areas.Selected, ToPicture(new Rect(x, y, w, h), picture));
        e.Handled = true;
    }

    private static Point Clamp(Point point, Rect picture) =>
        new(Math.Clamp(point.X, picture.Left, picture.Right), Math.Clamp(point.Y, picture.Top, picture.Bottom));

    private static double SafeClamp(double value, double min, double max) =>
        min > max ? Math.Clamp(value, max, min) : Math.Clamp(value, min, max);

    /// <summary>The frame between where a drag began and the pointer, at least <see cref="MinSizePx"/>, on the picture.</summary>
    private static Rect FrameBetween(Point a, Point b, Rect picture)
    {
        var w = Math.Max(MinSizePx, Math.Abs(b.X - a.X));
        var h = Math.Max(MinSizePx, Math.Abs(b.Y - a.Y));
        var x = SafeClamp(b.X < a.X ? a.X - w : a.X, picture.Left, picture.Right - w);
        var y = SafeClamp(b.Y < a.Y ? a.Y - h : a.Y, picture.Top, picture.Bottom - h);
        return new Rect(x, y, w, h);
    }

    /// <summary>A frame sized by one of its handles, its other sides staying put, within the picture.</summary>
    private static Rect Resize(Drag handle, Rect start, Vector delta, Rect picture)
    {
        double left = start.Left, top = start.Top, right = start.Right, bottom = start.Bottom;
        if (handle is Drag.TopLeft or Drag.Left or Drag.BottomLeft)
            left = SafeClamp(start.Left + delta.X, picture.Left, right - MinSizePx);
        if (handle is Drag.TopRight or Drag.Right or Drag.BottomRight)
            right = SafeClamp(start.Right + delta.X, left + MinSizePx, picture.Right);
        if (handle is Drag.TopLeft or Drag.Top or Drag.TopRight)
            top = SafeClamp(start.Top + delta.Y, picture.Top, bottom - MinSizePx);
        if (handle is Drag.BottomLeft or Drag.Bottom or Drag.BottomRight)
            bottom = SafeClamp(start.Bottom + delta.Y, top + MinSizePx, picture.Bottom);
        return new Rect(left, top, right - left, bottom - top);
    }
}
