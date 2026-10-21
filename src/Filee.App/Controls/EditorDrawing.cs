// How the editor draws what goes over the video, the same in its preview and for the conversion (EditorLayers): texts in
// their style, font, see-through and turn; pictures over the video; and the part's own picture in the frame (fitted,
// cropped or on a blurred copy of itself, zoomed, moved and turned). Sizes are parts of the frame, so a small preview
// and a 4K export look alike.

using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Filee.Core.Presets;

namespace Filee.App.Controls;

/// <summary>Where something sits in the frame: its middle, its size before turning, and its turn in degrees.</summary>
public readonly record struct EditorBox(Point Middle, Size Size, double Rotation)
{
    /// <summary>The four corners, turned.</summary>
    public Point[] Corners()
    {
        var (w, h) = (Size.Width / 2, Size.Height / 2);
        return [Turn(new Point(-w, -h)), Turn(new Point(w, -h)), Turn(new Point(w, h)), Turn(new Point(-w, h))];
    }

    /// <summary>A point given from the middle, unturned, turned and moved to its place.</summary>
    public Point Turn(Point local)
    {
        var radians = Rotation * Math.PI / 180;
        var (sin, cos) = (Math.Sin(radians), Math.Cos(radians));
        return new Point(Middle.X + local.X * cos - local.Y * sin, Middle.Y + local.X * sin + local.Y * cos);
    }

    /// <summary>A point of the frame seen from the middle, unturned.</summary>
    public Point Local(Point point)
    {
        var radians = -Rotation * Math.PI / 180;
        var (dx, dy) = (point.X - Middle.X, point.Y - Middle.Y);
        var (sin, cos) = (Math.Sin(radians), Math.Cos(radians));
        return new Point(dx * cos - dy * sin, dx * sin + dy * cos);
    }

    public bool Contains(Point point, double margin = 0)
    {
        var local = Local(point);
        return Math.Abs(local.X) <= Size.Width / 2 + margin && Math.Abs(local.Y) <= Size.Height / 2 + margin;
    }
}

public static class EditorDrawing
{
    /// <summary>The fonts tried when a text names none (or one this computer doesn't have).</summary>
    private const string Fallback = "Segoe UI, Inter, Noto Sans, Helvetica Neue, Arial, sans-serif";

    private static readonly FontFamily DefaultFamily = new(Fallback);

    public static FontFamily FontOf(string? name) => string.IsNullOrWhiteSpace(name) ? DefaultFamily : new FontFamily($"{name}, {Fallback}");

    private static Matrix TurnAbout(Point middle, double degrees) =>
        Matrix.CreateTranslation(-middle.X, -middle.Y) * Matrix.CreateRotation(degrees * Math.PI / 180) * Matrix.CreateTranslation(middle.X, middle.Y);

    // ───────────────────────── Texts ─────────────────────────

    private sealed record TextLayout(FormattedText Formatted, Point Origin, Geometry Geometry, Rect Letters, Rect Box, double Size, Color Color, Color Letter);

    private static TextLayout? Layout(ClipText text, Rect frame)
    {
        if (string.IsNullOrWhiteSpace(text.Text) || frame.Width <= 0)
            return null;
        var size = Math.Max(4, text.PlaySize * frame.Height);
        var color = ClipCanvas.ParseColor(text.PlayColor) is { } c ? Color.FromRgb(c.R, c.G, c.B) : Colors.White;
        var light = 0.299 * color.R + 0.587 * color.G + 0.114 * color.B > 160;
        var letters = text.Style == ClipTextStyle.Label ? light ? Color.FromRgb(17, 17, 17) : Colors.White : color;
        var formatted = new FormattedText(text.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontOf(text.Font), FontStyle.Normal, FontWeight.Bold), size, new SolidColorBrush(letters))
        {
            TextAlignment = TextAlignment.Center,
            MaxTextWidth = Math.Max(10, frame.Width * 0.94),
            LineHeight = size * 1.12,
        };
        var origin = new Point(frame.X + text.X * frame.Width - formatted.MaxTextWidth / 2, frame.Y + text.Y * frame.Height - formatted.Height / 2);
        if (formatted.BuildGeometry(origin) is not { } geometry)
            return null;
        var bounds = geometry.Bounds;
        var box = text.Style switch
        {
            ClipTextStyle.Caption => bounds.Inflate(size * 0.35),
            ClipTextStyle.Label => bounds.Inflate(size * 0.4),
            _ => bounds.Inflate(size * 0.1),
        };
        return new TextLayout(formatted, origin, geometry, bounds, box, size, color, letters);
    }

    /// <summary>Where <paramref name="text"/> is in a frame drawn at <paramref name="frame"/>, or null when it has no words.</summary>
    public static EditorBox? TextBox(ClipText text, Rect frame) =>
        Layout(text, frame) is { } layout ? new EditorBox(layout.Box.Center, layout.Box.Size, text.Rotation) : null;

    /// <summary>Draws <paramref name="text"/> in its style, font, see-through and turn.</summary>
    public static void DrawText(DrawingContext context, ClipText text, Rect frame)
    {
        if (Layout(text, frame) is not { } layout)
            return;
        using var opacity = context.PushOpacity(text.PlayOpacity);
        using var turn = context.PushTransform(TurnAbout(layout.Box.Center, text.Rotation));
        var size = layout.Size;
        switch (text.Style)
        {
            case ClipTextStyle.Caption:
                context.DrawRectangle(new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), null, layout.Box, size * 0.18, size * 0.18);
                break;
            case ClipTextStyle.Label:
                context.DrawRectangle(new SolidColorBrush(layout.Color), null, layout.Box, size * 0.2, size * 0.2);
                break;
            default:
                // A soft shadow and an outline, so a title reads on any picture.
                if (layout.Formatted.BuildGeometry(layout.Origin + new Point(size / 30, size / 30)) is { } shadow)
                    context.DrawGeometry(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), null, shadow);
                context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), size * 0.14, lineJoin: PenLineJoin.Round), layout.Geometry);
                break;
        }
        context.DrawGeometry(new SolidColorBrush(layout.Letter), null, layout.Geometry);
    }

    // ───────────────────────── Pictures over the video ─────────────────────────

    /// <summary>Where <paramref name="image"/> (its picture <paramref name="bitmap"/>) is in a frame drawn at <paramref name="frame"/>.</summary>
    public static EditorBox ImageBox(ClipImage image, Bitmap? bitmap, Rect frame)
    {
        var width = image.PlayWidth * frame.Width;
        var shape = bitmap is { Size.Width: > 0 } b ? b.Size.Height / b.Size.Width : 1;
        return new EditorBox(new Point(frame.X + image.X * frame.Width, frame.Y + image.Y * frame.Height), new Size(width, width * shape), image.Rotation);
    }

    public static void DrawImage(DrawingContext context, ClipImage image, Bitmap bitmap, Rect frame)
    {
        var box = ImageBox(image, bitmap, frame);
        using var opacity = context.PushOpacity(image.PlayOpacity);
        using var turn = context.PushTransform(TurnAbout(box.Middle, box.Rotation));
        context.DrawImage(bitmap, new Rect(box.Middle.X - box.Size.Width / 2, box.Middle.Y - box.Size.Height / 2, box.Size.Width, box.Size.Height));
    }

    // ───────────────────────── The part's picture ─────────────────────────

    /// <summary>Where the part's picture (<paramref name="pictureWidth"/> × <paramref name="pictureHeight"/>) is in a frame drawn at <paramref name="frame"/>.</summary>
    public static EditorBox PictureBox(ClipPart part, int pictureWidth, int pictureHeight, (int Width, int Height) canvas, ClipFit fit, Rect frame)
    {
        var place = ClipCanvas.Place(pictureWidth, pictureHeight, canvas.Width, canvas.Height, fit, part.PlayZoom, part.FocusX, part.FocusY);
        var scale = canvas.Width > 0 ? frame.Width / canvas.Width : 1;
        return new EditorBox(new Point(frame.X + (place.X + place.Width / 2) * scale, frame.Y + (place.Y + place.Height / 2) * scale),
            new Size(place.Width * scale, place.Height * scale), part.PlayRotation);
    }

    /// <summary>
    /// The part's picture in the frame: on black, or on <paramref name="blurred"/> (a tiny copy, drawn large and smooth)
    /// filling the frame, then the picture moved, sized and turned.
    /// </summary>
    public static void DrawPicture(DrawingContext context, Bitmap picture, Bitmap? blurred, ClipPart part, int pictureWidth, int pictureHeight,
        (int Width, int Height) canvas, ClipFit fit, Rect frame)
    {
        using var clip = context.PushClip(frame);
        var box = PictureBox(part, pictureWidth, pictureHeight, canvas, fit, frame);
        if (fit == ClipFit.Blur && blurred is not null)
        {
            var cover = ClipCanvas.Place(box.Size.Width, box.Size.Height, frame.Width, frame.Height, ClipFit.Crop);
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                context.DrawImage(blurred, new Rect(frame.X + cover.X, frame.Y + cover.Y, cover.Width, cover.Height));
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), frame);
        }
        using var turn = context.PushTransform(TurnAbout(box.Middle, box.Rotation));
        context.DrawImage(picture, new Rect(box.Middle.X - box.Size.Width / 2, box.Middle.Y - box.Size.Height / 2, box.Size.Width, box.Size.Height));
    }
}
