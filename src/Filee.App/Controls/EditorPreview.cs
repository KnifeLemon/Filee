// The editor's preview: the clip's frame (its shape, as large as fits) with the part's picture placed as the conversion
// will place it, the pictures and texts over it. What is selected has a frame with handles: dragged inside it moves,
// its corners size it, the round handle above turns it (snapping to the quarter turns). A click on the picture selects
// the part shown, on a text or a picture that one; a right click opens its menu; a double click fills the window (or
// edits a text's words).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Filee.Core.Presets;

namespace Filee.App.Controls;

public sealed class EditorPreview : Control
{
    public static readonly StyledProperty<IEditorPreviewHost?> HostProperty =
        AvaloniaProperty.Register<EditorPreview, IEditorPreviewHost?>(nameof(Host));

    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<EditorPreview, IBrush?>(nameof(AccentBrush), Brushes.MediumPurple);

    public IEditorPreviewHost? Host { get => GetValue(HostProperty); set => SetValue(HostProperty, value); }
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }

    /// <summary>Room left around the frame.</summary>
    private const double Room = 10;

    private const double HandleSize = 9;
    private const double TurnHandleDistance = 26;

    /// <summary>Turns this close to a quarter turn snap to it, in degrees.</summary>
    private const double TurnSnap = 4;

    private static readonly Cursor SizeAll = new(StandardCursorType.SizeAll);
    private static readonly Cursor Hand = new(StandardCursorType.Hand);
    private static readonly Cursor Cross = new(StandardCursorType.Cross);

    private enum Drag { None, Move, Size, Turn }

    private IEditorPreviewHost? _hooked;
    private Bitmap? _blurOf;
    private Bitmap? _blurred;
    private Drag _drag;
    private EditorItem _dragItem;
    private Point _pressedAt;
    private EditorBox _boxAtPress;
    private (double X, double Y, double Size, double Rotation) _valueAtPress;

    static EditorPreview()
    {
        AffectsRender<EditorPreview>(HostProperty, AccentBrushProperty);
    }

    public EditorPreview()
    {
        ClipToBounds = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HostProperty)
            Hook(Host);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Hook(Host);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Hook(null);
        _blurred?.Dispose();
        _blurred = null;
        _blurOf = null;
    }

    private void Hook(IEditorPreviewHost? host)
    {
        if (ReferenceEquals(_hooked, host))
            return;
        if (_hooked is not null)
            _hooked.PreviewChanged -= InvalidateVisual;
        _hooked = host;
        if (host is not null)
            host.PreviewChanged += InvalidateVisual;
        InvalidateVisual();
    }

    /// <summary>The frame on screen: the clip's shape, as large as fits.</summary>
    internal Rect Frame
    {
        get
        {
            var (width, height) = Host?.Canvas ?? (16, 9);
            var room = Bounds.Size.Deflate(new Thickness(Room));
            if (room.Width <= 1 || room.Height <= 1 || width <= 0 || height <= 0)
                return default;
            var scale = Math.Min(room.Width / width, room.Height / height);
            var size = new Size(width * scale, height * scale);
            return new Rect((Bounds.Width - size.Width) / 2, (Bounds.Height - size.Height) / 2, size.Width, size.Height);
        }
    }

    /// <summary>The blurred copy of <paramref name="picture"/> (made once per picture).</summary>
    private Bitmap? Blurred(Bitmap picture)
    {
        if (ReferenceEquals(_blurOf, picture) && _blurred is not null)
            return _blurred;
        _blurred?.Dispose();
        _blurred = null;
        _blurOf = picture;
        try
        {
            var size = picture.PixelSize;
            _blurred = picture.CreateScaledBitmap(new PixelSize(Math.Max(2, size.Width / 24), Math.Max(2, size.Height / 24)), BitmapInterpolationMode.MediumQuality);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A picture that can't be scaled (let go of meanwhile): no blurred fill this time.
            System.Diagnostics.Debug.WriteLine(ex);
        }
        return _blurred;
    }

    // ───────────────────────── Drawing ─────────────────────────

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var frame = Frame;
        if (Host is not { } host || frame.Width <= 0)
            return;
        context.FillRectangle(Brushes.Black, frame);
        if (host.Picture is { } picture)
        {
            try
            {
                if (host.IsComposed || host.PicturePart is not { Blank: false } part)
                    context.DrawImage(picture, frame);
                else
                {
                    var (width, height) = host.PictureSize;
                    EditorDrawing.DrawPicture(context, picture, host.Fit == ClipFit.Blur ? Blurred(picture) : null, part, width, height, host.Canvas, host.Fit, frame);
                }
            }
            catch (ObjectDisposedException)
            {
                // The picture was replaced meanwhile: the next frame draws the new one.
            }
        }
        using (context.PushClip(frame))
        {
            foreach (var (_, image) in host.VisibleImages)
            {
                if (host.ImageOf(image.Path) is { } bitmap)
                    EditorDrawing.DrawImage(context, image, bitmap, frame);
            }
            foreach (var (_, text) in host.VisibleTexts)
                EditorDrawing.DrawText(context, text, frame);
        }
        if (host.Framed is { } framed && BoxOf(framed) is { } box)
            DrawHandles(context, box);
    }

    private void DrawHandles(DrawingContext context, EditorBox box)
    {
        var accent = AccentBrush ?? Brushes.MediumPurple;
        var corners = box.Corners();
        var outline = new StreamGeometry();
        using (var g = outline.Open())
        {
            g.BeginFigure(corners[0], false);
            for (var i = 1; i < 4; i++)
                g.LineTo(corners[i]);
            g.EndFigure(true);
        }
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), 3), outline);
        context.DrawGeometry(null, new Pen(accent, 1.5), outline);
        foreach (var corner in corners)
            context.DrawRectangle(Brushes.White, new Pen(accent, 1.5), new Rect(corner.X - HandleSize / 2, corner.Y - HandleSize / 2, HandleSize, HandleSize), 2, 2);
        var top = box.Turn(new Point(0, -box.Size.Height / 2));
        var turn = TurnHandle(box);
        context.DrawLine(new Pen(accent, 1.5), top, turn);
        context.DrawEllipse(Brushes.White, new Pen(accent, 1.5), turn, HandleSize / 2 + 1, HandleSize / 2 + 1);
    }

    private static Point TurnHandle(EditorBox box) => box.Turn(new Point(0, -box.Size.Height / 2 - TurnHandleDistance));

    /// <summary>Where <paramref name="item"/> is on screen.</summary>
    private EditorBox? BoxOf(EditorItem item)
    {
        if (Host is not { } host)
            return null;
        var frame = Frame;
        switch (item.Kind)
        {
            case EditorItemKind.Text:
                return host.VisibleTexts.FirstOrDefault(t => t.Index == item.Index).Text is { } text ? EditorDrawing.TextBox(text, frame) : null;
            case EditorItemKind.Image:
                return host.VisibleImages.FirstOrDefault(i => i.Index == item.Index).Image is { } image
                    ? EditorDrawing.ImageBox(image, host.ImageOf(image.Path), frame) : null;
            case EditorItemKind.Part when !host.IsComposed && host.PicturePart is { Blank: false } part && item.Index == host.PictureIndex:
                var (width, height) = host.PictureSize;
                return EditorDrawing.PictureBox(part, width, height, host.Canvas, host.Fit, frame);
            default:
                return null;
        }
    }

    /// <summary>The value a drag starts from: the item's middle (parts of the frame), size and turn.</summary>
    private (double X, double Y, double Size, double Rotation)? ValueOf(EditorItem item)
    {
        if (Host is not { } host)
            return null;
        switch (item.Kind)
        {
            case EditorItemKind.Text when host.VisibleTexts.FirstOrDefault(t => t.Index == item.Index).Text is { } text:
                return (text.X, text.Y, text.PlaySize, text.Rotation);
            case EditorItemKind.Image when host.VisibleImages.FirstOrDefault(i => i.Index == item.Index).Image is { } image:
                return (image.X, image.Y, image.PlayWidth, image.Rotation);
            case EditorItemKind.Part when host.PicturePart is { } part:
                return (part.FocusX, part.FocusY, part.PlayZoom, part.PlayRotation);
            default:
                return null;
        }
    }

    /// <summary>What is under <paramref name="point"/>: a text (topmost first), a picture over the video, else the part shown.</summary>
    private EditorItem? ItemAt(Point point)
    {
        if (Host is not { } host)
            return null;
        var frame = Frame;
        foreach (var (index, text) in host.VisibleTexts.Reverse())
        {
            if (EditorDrawing.TextBox(text, frame) is { } box && box.Contains(point, 4))
                return new EditorItem(EditorItemKind.Text, index);
        }
        foreach (var (index, image) in host.VisibleImages.Reverse())
        {
            if (EditorDrawing.ImageBox(image, host.ImageOf(image.Path), frame).Contains(point, 4))
                return new EditorItem(EditorItemKind.Image, index);
        }
        return frame.Contains(point) && host.PictureIndex >= 0 && host.PicturePart is { Blank: false } && !host.IsComposed
            ? new EditorItem(EditorItemKind.Part, host.PictureIndex)
            : null;
    }

    // ───────────────────────── Pointer ─────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Host is not { } host)
            return;
        var point = e.GetPosition(this);
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsRightButtonPressed)
        {
            var item = ItemAt(point);
            if (item is { } chosen)
                host.Select(chosen);
            OpenMenu(host.MenuFor(item, double.NaN));
            e.Handled = true;
            return;
        }
        if (!properties.IsLeftButtonPressed)
            return;
        if (e.ClickCount == 2)
        {
            if (ItemAt(point) is { Kind: EditorItemKind.Text } text)
                host.EditText(text.Index);
            else
                host.ToggleFullPreview();
            e.Handled = true;
            return;
        }
        // The handles of what is framed first, then whatever is under the pointer.
        if (host.Framed is { } framed && BoxOf(framed) is { } box && ValueOf(framed) is { } value)
        {
            var drag = Distance(point, TurnHandle(box)) <= HandleSize ? Drag.Turn
                : box.Corners().Any(c => Distance(point, c) <= HandleSize) ? Drag.Size
                : Drag.None;
            if (drag != Drag.None)
            {
                Begin(e, drag, framed, box, value, point);
                return;
            }
        }
        if (ItemAt(point) is { } under)
        {
            host.Select(under);
            if (BoxOf(under) is { } underBox && ValueOf(under) is { } underValue)
                Begin(e, Drag.Move, under, underBox, underValue, point);
            e.Handled = true;
        }
    }

    private void Begin(PointerPressedEventArgs e, Drag drag, EditorItem item, EditorBox box, (double, double, double, double) value, Point point)
    {
        _drag = drag;
        _dragItem = item;
        _boxAtPress = box;
        _valueAtPress = value;
        _pressedAt = point;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        var host = Host;
        var frame = Frame;
        if (host is null || frame.Width <= 0)
            return;
        var (x, y, size, rotation) = _valueAtPress;
        switch (_drag)
        {
            case Drag.Move:
                {
                    var nx = Math.Clamp(x + (point.X - _pressedAt.X) / frame.Width, 0, 1);
                    var ny = Math.Clamp(y + (point.Y - _pressedAt.Y) / frame.Height, 0, 1);
                    // The middle lines snap, across and down.
                    if (Math.Abs(nx - 0.5) * frame.Width < 6)
                        nx = 0.5;
                    if (Math.Abs(ny - 0.5) * frame.Height < 6)
                        ny = 0.5;
                    host.Transform(_dragItem, nx, ny, size, rotation);
                    return;
                }
            case Drag.Size:
                {
                    var from = Distance(_pressedAt, _boxAtPress.Middle);
                    var to = Distance(point, _boxAtPress.Middle);
                    if (from > 1)
                        host.Transform(_dragItem, x, y, size * to / from, rotation);
                    return;
                }
            case Drag.Turn:
                {
                    var middle = _boxAtPress.Middle;
                    var degrees = Math.Atan2(point.Y - middle.Y, point.X - middle.X) * 180 / Math.PI + 90;
                    var snapped = Math.Round(degrees / 90) * 90;
                    if (Math.Abs(degrees - snapped) < TurnSnap)
                        degrees = snapped;
                    host.Transform(_dragItem, x, y, size, ClipCanvas.Angle(degrees));
                    return;
                }
        }
        // No drag: the cursor says what a press would do.
        if (host.Framed is { } framed && BoxOf(framed) is { } box)
        {
            if (Distance(point, TurnHandle(box)) <= HandleSize)
            {
                Cursor = Cross;
                return;
            }
            if (box.Corners().Any(c => Distance(point, c) <= HandleSize))
            {
                Cursor = Hand;
                return;
            }
        }
        Cursor = ItemAt(point) is not null ? SizeAll : null;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        End(e.Pointer);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        End(null);
    }

    private void End(IPointer? pointer)
    {
        if (_drag != Drag.None)
            Host?.EndDrag();
        _drag = Drag.None;
        pointer?.Capture(null);
    }

    private void OpenMenu(IReadOnlyList<EditorMenuItem> items)
    {
        if (items.Count == 0)
            return;
        EditorMenus.Build(items).Open(this);
    }
}
