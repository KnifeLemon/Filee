// The "Annotate" window's picture and its marks: the marks are painted by AnnotationPainter on Avalonia's own Skia
// canvas, so they look exactly as they will in the file; the selected one has its handles. A drag with a drawing tool
// draws a mark; with Select it moves a mark or, on a handle, resizes it. Text is typed in the window (TextRequested).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Filee.Core.Presets;
using Filee.Engines.Annotations;
using SkiaSharp;

namespace Filee.App.Controls;

/// <summary>What a press on the picture does.</summary>
public enum AnnotateMode
{
    Select,
    Pen,
    Arrow,
    Box,
    Highlight,
    Text,
    Number,
}

public sealed class AnnotationCanvas : Control
{
    public static readonly StyledProperty<Bitmap?> SourceImageProperty =
        AvaloniaProperty.Register<AnnotationCanvas, Bitmap?>(nameof(SourceImage));

    public static readonly StyledProperty<AnnotationSet?> MarksProperty =
        AvaloniaProperty.Register<AnnotationCanvas, AnnotationSet?>(nameof(Marks));

    public static readonly StyledProperty<AnnotateMode> ToolProperty =
        AvaloniaProperty.Register<AnnotationCanvas, AnnotateMode>(nameof(Tool));

    /// <summary>The colour new marks get, "#RRGGBB".</summary>
    public static readonly StyledProperty<string> MarkColorProperty =
        AvaloniaProperty.Register<AnnotationCanvas, string>(nameof(MarkColor), AnnotateTool.Colors[0]);

    public static readonly StyledProperty<AnnotationWidth> MarkWidthProperty =
        AvaloniaProperty.Register<AnnotationCanvas, AnnotationWidth>(nameof(MarkWidth), AnnotationWidth.Medium);

    public static readonly StyledProperty<Thickness> ImagePaddingProperty =
        AvaloniaProperty.Register<AnnotationCanvas, Thickness>(nameof(ImagePadding));

    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<AnnotationCanvas, IBrush?>(nameof(AccentBrush));

    public Bitmap? SourceImage { get => GetValue(SourceImageProperty); set => SetValue(SourceImageProperty, value); }
    public AnnotationSet? Marks { get => GetValue(MarksProperty); set => SetValue(MarksProperty, value); }
    public AnnotateMode Tool { get => GetValue(ToolProperty); set => SetValue(ToolProperty, value); }
    public string MarkColor { get => GetValue(MarkColorProperty); set => SetValue(MarkColorProperty, value); }
    public AnnotationWidth MarkWidth { get => GetValue(MarkWidthProperty); set => SetValue(MarkWidthProperty, value); }
    public Thickness ImagePadding { get => GetValue(ImagePaddingProperty); set => SetValue(ImagePaddingProperty, value); }
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }

    /// <summary>
    /// Text is to be typed: a new one at a point (in parts of the picture, index -1), or text mark <c>Index</c> again.
    /// The window shows the box to type in at <c>Screen</c>, the tag of an empty text there: the box follows the text
    /// from that size, as the tag will.
    /// </summary>
    public event EventHandler<TextRequest>? TextRequested;

    public readonly record struct TextRequest(int Index, AnnotationPoint At, Rect Screen, double FontSize);

    /// <summary>Handles are this many device-independent pixels across; a press this near a line hits it.</summary>
    private const double HandleSize = 12, HitSlop = 7;

    private enum Drag { None, Draw, Move, Handle }

    private Drag _drag;
    private int _handle;
    private Point _start;
    private List<AnnotationPoint> _startPoints = [];

    static AnnotationCanvas()
    {
        AffectsRender<AnnotationCanvas>(SourceImageProperty, ImagePaddingProperty, AccentBrushProperty);
        FocusableProperty.OverrideDefaultValue<AnnotationCanvas>(false);
    }

    // The picture is never mirrored, also in a right-to-left UI.
    public AnnotationCanvas()
    {
        FlowDirection = FlowDirection.LeftToRight;
        ClipToBounds = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarksProperty)
        {
            if (change.GetOldValue<AnnotationSet?>() is { } old)
                old.Changed -= InvalidateVisual;
            if (change.GetNewValue<AnnotationSet?>() is { } marks)
                marks.Changed += InvalidateVisual;
            InvalidateVisual();
        }
        else if (change.Property == ToolProperty)
        {
            Cursor = new Cursor(Tool == AnnotateMode.Select ? StandardCursorType.Arrow : StandardCursorType.Cross);
        }
    }

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
        return new Rect(area.X + (area.Width - size.Width * scale) / 2, area.Y + (area.Height - size.Height * scale) / 2,
            size.Width * scale, size.Height * scale);
    }

    // ───────── Drawing ─────────

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var picture = CalculateImageBounds();
        if (SourceImage is null || picture.Width <= 0)
            return;
        context.DrawRectangle(Brushes.White, null, picture, 0, 0, new BoxShadows(new BoxShadow { Blur = 24, OffsetY = 6, Color = Color.FromArgb(0x33, 0, 0, 0) }));
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            context.DrawImage(SourceImage, new Rect(SourceImage.Size), picture);
        if (Marks is not { Count: > 0 } marks)
            return;
        context.Custom(new MarksDrawing(new Rect(Bounds.Size), picture, [.. marks.Marks.Where((_, i) => i != TypingIndex).Select(m => m.Clone())]));
        if (ShowsSelection && marks.Selected != TypingIndex)
            DrawSelection(context, marks.SelectedMark!, picture);
    }

    /// <summary>
    /// The text mark being typed again (-1 for none): not drawn, the window's box stands in for it as it will be.
    /// </summary>
    internal int TypingIndex
    {
        get => _typingIndex;
        set
        {
            _typingIndex = value;
            InvalidateVisual();
        }
    }

    private int _typingIndex = -1;

    /// <summary>
    /// Whether the selected mark shows its outline or handles. A mark being drawn is the selected one (the drag extends
    /// it) but shows neither: they would only get in the way of the stroke.
    /// </summary>
    internal bool ShowsSelection => Marks?.SelectedMark is not null && _drag != Drag.Draw;

    /// <summary>The marks, painted by the file's own painter on Avalonia's Skia canvas.</summary>
    private sealed class MarksDrawing(Rect bounds, Rect picture, IReadOnlyList<Annotation> marks) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature feature)
                return;
            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            canvas.Save();
            canvas.ClipRect(new SKRect((float)picture.Left, (float)picture.Top, (float)picture.Right, (float)picture.Bottom));
            canvas.Translate((float)picture.X, (float)picture.Y);
            AnnotationPainter.Paint(canvas, marks, (float)picture.Width, (float)picture.Height);
            canvas.Restore();
        }
    }

    private void DrawSelection(DrawingContext context, Annotation mark, Rect picture)
    {
        var accent = AccentBrush ?? Brushes.MediumPurple;
        var handles = HandlesOf(mark, picture);
        if (handles.Count == 0)
        {
            // Pen, text and number: their outline, dashed, to show which is selected.
            var box = BoundsOf(mark, picture).Inflate(4);
            context.DrawRectangle(null, new Pen(accent, 1.5, new DashStyle([4, 3], 0)), box, 3, 3);
            return;
        }
        foreach (var handle in handles)
        {
            context.DrawEllipse(Brushes.White, new Pen(accent, 2), handle, HandleSize / 2, HandleSize / 2);
        }
    }

    // ───────── Geometry ─────────

    private static Point ToScreen(AnnotationPoint point, Rect picture) =>
        new(picture.X + point.X * picture.Width, picture.Y + point.Y * picture.Height);

    private static AnnotationPoint ToPicture(Point screen, Rect picture) => new()
    {
        X = Math.Clamp((screen.X - picture.X) / picture.Width, 0, 1),
        Y = Math.Clamp((screen.Y - picture.Y) / picture.Height, 0, 1),
    };

    /// <summary>
    /// A mark's handles on screen: an arrow's two ends; a box's or highlight's four corners; none for the rest.
    /// </summary>
    internal static IReadOnlyList<Point> HandlesOf(Annotation mark, Rect picture)
    {
        if (mark.Points.Count < 2)
            return [];
        var a = ToScreen(mark.Points[0], picture);
        var b = ToScreen(mark.Points[1], picture);
        return mark.Kind switch
        {
            AnnotationKind.Arrow => [a, b],
            AnnotationKind.Box or AnnotationKind.Highlight => [a, b, new Point(a.X, b.Y), new Point(b.X, a.Y)],
            _ => [],
        };
    }

    /// <summary>The screen rectangle a mark covers.</summary>
    internal static Rect BoundsOf(Annotation mark, Rect picture)
    {
        float w = (float)picture.Width, h = (float)picture.Height;
        SKRect rect;
        switch (mark.Kind)
        {
            case AnnotationKind.Text:
                rect = AnnotationPainter.TagOf(mark, w, h);
                break;
            case AnnotationKind.Number:
                rect = AnnotationPainter.CircleOf(mark, w, h);
                break;
            default:
                var xs = mark.Points.Select(p => p.X * w).ToList();
                var ys = mark.Points.Select(p => p.Y * h).ToList();
                rect = new SKRect((float)xs.Min(), (float)ys.Min(), (float)xs.Max(), (float)ys.Max());
                break;
        }
        return new Rect(picture.X + rect.Left, picture.Y + rect.Top, rect.Width, rect.Height);
    }

    /// <summary>The mark on top under <paramref name="point"/>, -1 for none; the selected one first.</summary>
    internal static int HitTest(IReadOnlyList<Annotation> marks, int selected, Point point, Rect picture)
    {
        if (selected >= 0 && selected < marks.Count && Hits(marks[selected], point, picture))
            return selected;
        for (var i = marks.Count - 1; i >= 0; i--)
        {
            if (Hits(marks[i], point, picture))
                return i;
        }
        return -1;
    }

    private static bool Hits(Annotation mark, Point point, Rect picture)
    {
        if (mark.Points.Count == 0)
            return false;
        var slop = HitSlop + AnnotationPainter.StrokeOf(mark.Width, (float)picture.Width, (float)picture.Height) / 2;
        var screen = mark.Points.Select(p => ToScreen(p, picture)).ToList();
        switch (mark.Kind)
        {
            case AnnotationKind.Pen:
            case AnnotationKind.Arrow:
                for (var i = 1; i < screen.Count; i++)
                {
                    if (Distance(point, screen[i - 1], screen[i]) <= slop)
                        return true;
                }
                return false;
            case AnnotationKind.Box when screen.Count >= 2:
                {
                    var rect = new Rect(screen[0], screen[1]).Normalize();
                    return rect.Inflate(slop).Contains(point) && !rect.Deflate(slop).Contains(point);
                }
            case AnnotationKind.Highlight when screen.Count >= 2:
                return new Rect(screen[0], screen[1]).Normalize().Contains(point);
            default:
                return BoundsOf(mark, picture).Inflate(2).Contains(point);
        }
    }

    private static double Distance(Point p, Point a, Point b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lengthSquared = dx * dx + dy * dy;
        var t = lengthSquared == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared, 0, 1);
        var (x, y) = (a.X + t * dx, a.Y + t * dy);
        return Math.Sqrt((p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y));
    }

    // ───────── Pointer ─────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var picture = CalculateImageBounds();
        if (Marks is not { } marks || picture.Width <= 0 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var point = e.GetPosition(this);
        var at = ToPicture(point, picture);
        _start = point;
        e.Handled = true;

        switch (Tool)
        {
            case AnnotateMode.Select:
                if (marks.SelectedMark is { } selected && HandleAt(selected, point, picture) is >= 0 and var handle)
                {
                    _handle = handle;
                    Begin(Drag.Handle, e, selected);
                    return;
                }
                var hit = HitTest(marks.Marks, marks.Selected, point, picture);
                marks.Select(hit);
                if (hit < 0)
                    return;
                if (e.ClickCount == 2 && marks.Marks[hit].Kind == AnnotationKind.Text)
                {
                    TypeAgain(hit);
                    return;
                }
                Begin(Drag.Move, e, marks.Marks[hit]);
                return;
            case AnnotateMode.Text:
                RequestText(-1, at, picture);
                return;
            case AnnotateMode.Number:
                var number = NewMark(AnnotationKind.Number, at);
                number.Number = marks.NextNumber;
                marks.Add(number);
                // Drawn, not picked: the next colour goes to the next mark, not to this one (Select picks a mark).
                marks.Select(-1);
                return;
            default:
                var kind = Tool switch
                {
                    AnnotateMode.Pen => AnnotationKind.Pen,
                    AnnotateMode.Arrow => AnnotationKind.Arrow,
                    AnnotateMode.Box => AnnotationKind.Box,
                    _ => AnnotationKind.Highlight,
                };
                marks.Begin();
                var mark = NewMark(kind, at);
                mark.Points.Add(at.Clone());
                if (!marks.Add(mark))
                {
                    marks.Cancel();
                    return;
                }
                _drag = Drag.Draw;
                e.Pointer.Capture(this);
                return;
        }
    }

    private Annotation NewMark(AnnotationKind kind, AnnotationPoint at) =>
        new() { Kind = kind, Color = MarkColor, Width = MarkWidth, Points = [at] };

    private void Begin(Drag drag, PointerPressedEventArgs e, Annotation mark)
    {
        Marks!.Begin();
        _drag = drag;
        _startPoints = [.. mark.Points.Select(p => p.Clone())];
        e.Pointer.Capture(this);
    }

    /// <summary>The handle of <paramref name="mark"/> under <paramref name="point"/>, -1 for none.</summary>
    private static int HandleAt(Annotation mark, Point point, Rect picture)
    {
        var handles = HandlesOf(mark, picture);
        for (var i = 0; i < handles.Count; i++)
        {
            if (Math.Abs(handles[i].X - point.X) <= HandleSize && Math.Abs(handles[i].Y - point.Y) <= HandleSize)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Asks for text mark <paramref name="index"/> to be typed again (a double-click on it with Select).
    /// </summary>
    internal void TypeAgain(int index) => RequestText(index, Marks!.Marks[index].Points[0], CalculateImageBounds());

    private void RequestText(int index, AnnotationPoint at, Rect picture)
    {
        var width = index >= 0 ? Marks!.Marks[index].Width : MarkWidth;
        var empty = new Annotation { Kind = AnnotationKind.Text, Width = width, Points = [at], Text = " " };
        TextRequested?.Invoke(this, new TextRequest(index, at, BoundsOf(empty, picture),
            AnnotationPainter.FontSizeOf(width, (float)picture.Width, (float)picture.Height)));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag == Drag.None || Marks is not { } marks || marks.Selected < 0)
            return;
        var picture = CalculateImageBounds();
        var point = e.GetPosition(this);
        var at = ToPicture(point, picture);
        var index = marks.Selected;
        switch (_drag)
        {
            case Drag.Draw:
                marks.Change(index, mark =>
                {
                    if (mark.Kind == AnnotationKind.Pen)
                    {
                        // A point each time the pointer has gone a little way: a smooth line, not thousands of points.
                        var last = ToScreen(mark.Points[^1], picture);
                        if (Math.Abs(point.X - last.X) + Math.Abs(point.Y - last.Y) >= 1.5)
                            mark.Points.Add(at);
                    }
                    else
                    {
                        mark.Points[1] = at;
                    }
                });
                break;
            case Drag.Move:
                {
                    var dx = (point.X - _start.X) / picture.Width;
                    var dy = (point.Y - _start.Y) / picture.Height;
                    // The whole mark stays on the picture.
                    dx = Math.Clamp(dx, -_startPoints.Min(p => p.X), 1 - _startPoints.Max(p => p.X));
                    dy = Math.Clamp(dy, -_startPoints.Min(p => p.Y), 1 - _startPoints.Max(p => p.Y));
                    marks.Change(index, mark =>
                    {
                        for (var i = 0; i < mark.Points.Count && i < _startPoints.Count; i++)
                            mark.Points[i] = new AnnotationPoint { X = _startPoints[i].X + dx, Y = _startPoints[i].Y + dy };
                    });
                    break;
                }
            case Drag.Handle:
                marks.Change(index, mark =>
                {
                    switch (_handle)
                    {
                        case 0:
                            mark.Points[0] = at;
                            break;
                        case 1:
                            mark.Points[1] = at;
                            break;
                        case 2: // the corner left of the first point's column, on the second's row
                            mark.Points[0] = new AnnotationPoint { X = at.X, Y = mark.Points[0].Y };
                            mark.Points[1] = new AnnotationPoint { X = mark.Points[1].X, Y = at.Y };
                            break;
                        case 3:
                            mark.Points[1] = new AnnotationPoint { X = at.X, Y = mark.Points[1].Y };
                            mark.Points[0] = new AnnotationPoint { X = mark.Points[0].X, Y = at.Y };
                            break;
                    }
                });
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == Drag.None || Marks is not { } marks)
            return;
        var drew = _drag == Drag.Draw;
        _drag = Drag.None;
        e.Pointer.Capture(null);
        // A click that drew nothing (a line of one point, a box with no size) leaves no mark.
        if (drew && marks.SelectedMark is { } mark && !IsBigEnough(mark))
            marks.Cancel();
        else
            marks.End();
        // Drawn, not picked: the next colour goes to the next mark, not to this one (Select picks a mark).
        if (drew)
            marks.Select(-1);
    }

    private static bool IsBigEnough(Annotation mark)
    {
        if (mark.Points.Count < 2)
            return false;
        var width = mark.Points.Max(p => p.X) - mark.Points.Min(p => p.X);
        var height = mark.Points.Max(p => p.Y) - mark.Points.Min(p => p.Y);
        return mark.Kind == AnnotationKind.Pen || mark.Kind == AnnotationKind.Arrow
            ? Math.Max(width, height) >= AnnotateTool.MinSize
            : width >= AnnotateTool.MinSize && height >= AnnotateTool.MinSize;
    }
}
