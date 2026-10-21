// The editor's frame: its shape and size, where each part's picture sits in it (fitted with bars, cropped to fill, or
// over a blurred copy of itself), and the texts over it. The same numbers place the picture in the editor's preview
// and in FFmpeg's graph, so what the preview shows is what is written.

using System.Globalization;
using System.Text.Json.Serialization;

namespace Filee.Core.Presets;

/// <summary>How a picture of another shape than the frame fills it.</summary>
public enum ClipFit
{
    /// <summary>Whole, with black bars beside or above it.</summary>
    Bars,

    /// <summary>Cropped so it fills the frame (what is cut off chosen with the part's focus).</summary>
    Crop,

    /// <summary>Whole, over a blurred copy of itself that fills the frame.</summary>
    Blur,
}

/// <summary>The ready-made looks of a text.</summary>
public enum ClipTextStyle
{
    /// <summary>Large and bold in the middle, outlined.</summary>
    Title,

    /// <summary>A line at the bottom on a dark band, like subtitles.</summary>
    Caption,

    /// <summary>A small tag on a coloured box.</summary>
    Label,
}

/// <summary>A text over the picture for a while.</summary>
public sealed class ClipText
{
    /// <summary>The smallest and largest size, as a part of the frame's height.</summary>
    public const double MinSize = 0.02, MaxSize = 0.25;

    /// <summary>Seconds a text fades in and out.</summary>
    public const double FadeSeconds = 0.2;

    /// <summary>The colours offered for texts.</summary>
    public static IReadOnlyList<string> Colors { get; } = ["#FFFFFF", "#FFD43B", "#FF6B6B", "#4DABF7", "#69DB7C", "#111111"];

    public string Text { get; set; } = "";

    public ClipTextStyle Style { get; set; }

    /// <summary>When it appears, in seconds into the clip.</summary>
    public double Start { get; set; }

    /// <summary>How long it stays, in seconds.</summary>
    public double Length { get; set; } = 3;

    /// <summary>Where its middle is, as a part of the frame's width and height (0.5 the middle).</summary>
    public double X { get; set; } = 0.5;

    /// <summary>The same as <see cref="X"/>, down.</summary>
    public double Y { get; set; } = 0.5;

    /// <summary>How tall its letters are, as a part of the frame's height; 0 for its style's.</summary>
    public double Size { get; set; }

    /// <summary>
    /// "#RRGGBB": the letters of a title or a caption, the box of a label; null for its style's.
    /// </summary>
    public string? Color { get; set; }

    /// <summary>The font family by name; null for Filee's own.</summary>
    public string? Font { get; set; }

    /// <summary>How see-through it is: 1 solid, down to 0.05.</summary>
    public double Opacity { get; set; } = 1;

    /// <summary>Turned clockwise, in degrees.</summary>
    public double Rotation { get; set; }

    /// <summary>
    /// The text drawn by the editor as it looks, a transparent picture the size of the written frame (set for the
    /// conversion; FFmpeg lays it over the video instead of drawing the text itself).
    /// </summary>
    public string? Rendered { get; set; }

    /// <summary><see cref="Opacity"/> as written.</summary>
    [JsonIgnore]
    public double PlayOpacity => double.IsFinite(Opacity) ? Math.Clamp(Opacity, 0.05, 1) : 1;

    /// <summary><see cref="Size"/> as written.</summary>
    [JsonIgnore]
    public double PlaySize => Size > 0 && double.IsFinite(Size) ? Math.Clamp(Size, MinSize, MaxSize) : DefaultSize(Style);

    /// <summary><see cref="Color"/> as written (its style's when it isn't a colour).</summary>
    [JsonIgnore]
    public string PlayColor => ClipCanvas.ParseColor(Color) is not null ? Color!.ToUpperInvariant() : DefaultColor(Style);

    public ClipText Clone() => (ClipText)MemberwiseClone();

    public static double DefaultSize(ClipTextStyle style) => style switch
    {
        ClipTextStyle.Title => 0.085,
        ClipTextStyle.Caption => 0.05,
        _ => 0.04,
    };

    public static string DefaultColor(ClipTextStyle style) => style == ClipTextStyle.Label ? "#E8505B" : "#FFFFFF";

    /// <summary>A new text of <paramref name="style"/> where that style usually goes.</summary>
    public static ClipText Of(ClipTextStyle style, string text, double start, double length = 3) => new()
    {
        Text = text,
        Style = style,
        Start = start,
        Length = length,
        X = 0.5,
        Y = style switch
        {
            ClipTextStyle.Title => 0.42,
            ClipTextStyle.Caption => 0.86,
            _ => 0.12,
        },
    };
}

/// <summary>A picture (a logo, a photo, a sticker) over the video for a while.</summary>
public sealed class ClipImage
{
    /// <summary>The smallest and largest width, as a part of the frame's width.</summary>
    public const double MinWidth = 0.03, MaxWidth = 2;

    /// <summary>The picture's file.</summary>
    public string Path { get; set; } = "";

    /// <summary>When it appears, in seconds into the clip.</summary>
    public double Start { get; set; }

    /// <summary>How long it stays, in seconds.</summary>
    public double Length { get; set; } = 5;

    /// <summary>Where its middle is, as a part of the frame's width and height.</summary>
    public double X { get; set; } = 0.5;

    /// <summary>The same as <see cref="X"/>, down.</summary>
    public double Y { get; set; } = 0.5;

    /// <summary>How wide it is, as a part of the frame's width (its height follows its shape).</summary>
    public double Width { get; set; } = 0.3;

    /// <summary>Turned clockwise, in degrees.</summary>
    public double Rotation { get; set; }

    /// <summary>How see-through it is: 1 solid, down to 0.05.</summary>
    public double Opacity { get; set; } = 1;

    /// <summary>The picture drawn by the editor in place, the size of the written frame (set for the conversion).</summary>
    public string? Rendered { get; set; }

    [JsonIgnore]
    public double PlayWidth => double.IsFinite(Width) && Width > 0 ? Math.Clamp(Width, MinWidth, MaxWidth) : 0.3;

    [JsonIgnore]
    public double PlayOpacity => double.IsFinite(Opacity) ? Math.Clamp(Opacity, 0.05, 1) : 1;

    public ClipImage Clone() => (ClipImage)MemberwiseClone();
}

/// <summary>Where a picture sits in the frame: its size once scaled, and its top-left corner (outside the frame when it is larger).</summary>
public readonly record struct ClipPlacement(double Width, double Height, double X, double Y);

/// <summary>The editor's frame: shapes, sizes, and where a picture goes in it.</summary>
public static class ClipCanvas
{
    /// <summary>The smallest and largest shorter side of the frame.</summary>
    public const int MinResolution = 144, MaxResolution = 4320;

    /// <summary>The frame's shapes besides the first video's own.</summary>
    public static IReadOnlyList<string?> Aspects { get; } = ["16:9", "9:16", "1:1", "4:5"];

    /// <summary>The shorter sides offered (0 is the first video's).</summary>
    public static IReadOnlyList<int> Resolutions { get; } = [0, 2160, 1440, 1080, 720, 480];

    /// <summary>Width over height of <paramref name="aspect"/> ("9:16"); null when it isn't one.</summary>
    public static double? Ratio(string? aspect)
    {
        if (aspect?.Split(':') is not [var w, var h]
            || !double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
            || !double.TryParse(h, NumberStyles.Float, CultureInfo.InvariantCulture, out var height)
            || width <= 0 || height <= 0)
            return null;
        return width / height;
    }

    /// <summary>
    /// The frame's size in pixels (even): <paramref name="aspect"/> (null: the video's own), its shorter side
    /// <paramref name="resolution"/> (0: the video's shorter side), for a first video of
    /// <paramref name="videoWidth"/> × <paramref name="videoHeight"/> as shown.
    /// </summary>
    public static (int Width, int Height) Size(string? aspect, int resolution, double videoWidth, double videoHeight)
    {
        var (w, h) = (Math.Max(16, videoWidth), Math.Max(16, videoHeight));
        var ratio = Ratio(aspect) ?? w / h;
        var shorter = resolution > 0 ? resolution : Math.Min(w, h);
        if (aspect is null && resolution <= 0)
            return (Even(w), Even(h));
        return ratio >= 1 ? (Even(shorter * ratio), Even(shorter)) : (Even(shorter), Even(shorter / ratio));
    }

    /// <summary>
    /// Where a picture of <paramref name="width"/> × <paramref name="height"/> goes in a frame of
    /// <paramref name="frameWidth"/> × <paramref name="frameHeight"/>: fitted whole (bars, blur) or filling it (crop),
    /// then <paramref name="zoom"/> times as large, its middle at <paramref name="focusX"/>, <paramref name="focusY"/>
    /// of the frame (0.5 the middle). Its size and top-left corner before any turn.
    /// </summary>
    public static ClipPlacement Place(double width, double height, double frameWidth, double frameHeight, ClipFit fit, double zoom = 1,
        double focusX = 0.5, double focusY = 0.5)
    {
        if (width <= 0 || height <= 0)
            return new ClipPlacement(frameWidth, frameHeight, 0, 0);
        var fitScale = Math.Min(frameWidth / width, frameHeight / height);
        var fillScale = Math.Max(frameWidth / width, frameHeight / height);
        var scale = (fit == ClipFit.Crop ? fillScale : fitScale) * Math.Clamp(double.IsFinite(zoom) ? zoom : 1, ClipPart.MinZoom, ClipPart.MaxZoom);
        var (w, h) = (width * scale, height * scale);
        return new ClipPlacement(w, h, Middle(focusX) * frameWidth - w / 2, Middle(focusY) * frameHeight - h / 2);
    }

    private static double Middle(double focus) => double.IsFinite(focus) ? Math.Clamp(focus, 0, 1) : 0.5;

    /// <summary>
    /// The middle that puts the picture <paramref name="dx"/>, <paramref name="dy"/> pixels further (a drag on the
    /// preview) in a frame of the given size, kept inside the frame.
    /// </summary>
    public static (double X, double Y) Drag(double frameWidth, double frameHeight, double focusX, double focusY, double dx, double dy) =>
        (Math.Clamp(focusX + dx / Math.Max(1, frameWidth), 0, 1), Math.Clamp(focusY + dy / Math.Max(1, frameHeight), 0, 1));

    /// <summary>An angle in degrees as −180 (exclusive) to 180, 0 for one that makes no sense.</summary>
    public static double Angle(double degrees)
    {
        if (!double.IsFinite(degrees))
            return 0;
        var angle = Math.Round(degrees % 360, 2);
        if (angle > 180)
            angle -= 360;
        if (angle <= -180)
            angle += 360;
        return angle;
    }


    /// <summary>The red, green and blue of "#RRGGBB", or null.</summary>
    public static (byte R, byte G, byte B)? ParseColor(string? color)
    {
        if (color is not { Length: 7 } || color[0] != '#'
            || !int.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return null;
        return ((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    private static int Even(double value) => Math.Max(2, (int)Math.Round(value / 2) * 2);
}
