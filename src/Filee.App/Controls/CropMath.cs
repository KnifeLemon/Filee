// The crop overlay's arithmetic without a window: the ratios of the aspect dial, keyboard nudges, the frame's size in
// pixels, where the size chip sits next to the frame and where the aspect dial opens. For the perspective mode it holds
// the four corners (CropQuad), which shapes they may form and how they move, the straightened output size and whether
// the corners still form a plain rectangle.

using Avalonia;

namespace Filee.App.Controls;

/// <summary>Four normalized picture corners, clockwise from the top left.</summary>
public readonly record struct CropQuad(Point TopLeft, Point TopRight, Point BottomRight, Point BottomLeft)
{
    /// <summary>The whole picture.</summary>
    public static CropQuad Full => new(new Point(0, 0), new Point(1, 0), new Point(1, 1), new Point(0, 1));

    /// <summary>A corner in clockwise order, starting at the top left.</summary>
    public Point this[int corner] => corner switch
    {
        0 => TopLeft,
        1 => TopRight,
        2 => BottomRight,
        3 => BottomLeft,
        _ => throw new ArgumentOutOfRangeException(nameof(corner)),
    };

    /// <summary>A copy with one corner replaced.</summary>
    public CropQuad With(int corner, Point p) => corner switch
    {
        0 => this with { TopLeft = p },
        1 => this with { TopRight = p },
        2 => this with { BottomRight = p },
        3 => this with { BottomLeft = p },
        _ => throw new ArgumentOutOfRangeException(nameof(corner)),
    };
}

public static class CropMath
{
    /// <summary>Minimum clockwise turn in square original pixels.</summary>
    public const double TurnEpsilon = 1e-6;

    /// <summary>Whether all corners are inside the picture and form a strictly convex clockwise quadrilateral.</summary>
    public static bool IsValidQuad(CropQuad q, int width, int height)
    {
        for (var i = 0; i < 4; i++)
        {
            var p = q[i];
            if (!(p.X >= 0 && p.X <= 1 && p.Y >= 0 && p.Y <= 1))
                return false;
            var next = q[(i + 1) % 4];
            var last = q[(i + 2) % 4];
            var cross = ((next.X - p.X) * width) * ((last.Y - next.Y) * height)
                - ((next.Y - p.Y) * height) * ((last.X - next.X) * width);
            if (!(cross > TurnEpsilon))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Moves one corner, stopping it on the picture's edge when the target lies outside, and only when the resulting
    /// quadrilateral is valid.
    /// </summary>
    public static CropQuad MoveCorner(CropQuad q, int corner, Point target, int width, int height)
    {
        var moved = q.With(corner, new Point(Math.Clamp(target.X, 0, 1), Math.Clamp(target.Y, 0, 1)));
        return IsValidQuad(moved, width, height) ? moved : q;
    }

    /// <summary>Snaps one corner to the original pixel grid and moves it by whole pixels.</summary>
    public static CropQuad NudgeCorner(CropQuad q, int corner, int dx, int dy, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return q;
        var p = q[corner];
        var target = new Point((Math.Round(p.X * width) + dx) / width, (Math.Round(p.Y * height) + dy) / height);
        return MoveCorner(q, corner, target, width, height);
    }

    /// <summary>Output size from the longer opposing sides in original pixels, at least 1 by 1.</summary>
    public static PixelSize PerspectiveSize(CropQuad q, int width, int height)
    {
        double Length(Point a, Point b) =>
            Math.Sqrt(Math.Pow((a.X - b.X) * width, 2) + Math.Pow((a.Y - b.Y) * height, 2));

        return new PixelSize(
            Math.Max(1, (int)Math.Round(Math.Max(Length(q.TopLeft, q.TopRight), Length(q.BottomLeft, q.BottomRight)))),
            Math.Max(1, (int)Math.Round(Math.Max(Length(q.TopLeft, q.BottomLeft), Length(q.TopRight, q.BottomRight)))));
    }

    /// <summary>
    /// The quadrilateral as a rectangle in original pixels when it already is one: sides along the picture's edges and
    /// every corner on a whole pixel, as the corners are until they're dragged off the grid. Saving it is then a plain
    /// crop, with no resampling.
    /// </summary>
    public static PixelRect? AxisAlignedRect(CropQuad q, int width, int height)
    {
        static int? Whole(double v) => Math.Abs(v - Math.Round(v)) < 1e-6 ? (int)Math.Round(v) : null;
        var left = Whole(q.TopLeft.X * width);
        var top = Whole(q.TopLeft.Y * height);
        var right = Whole(q.BottomRight.X * width);
        var bottom = Whole(q.BottomRight.Y * height);
        if (left is not { } l || top is not { } t || right is not { } r || bottom is not { } b
            || Whole(q.TopRight.X * width) != r || Whole(q.TopRight.Y * height) != t
            || Whole(q.BottomLeft.X * width) != l || Whole(q.BottomLeft.Y * height) != b
            || r <= l || b <= t)
            return null;
        return new PixelRect(l, t, r - l, b - t);
    }

    /// <summary>The aspect dial's choices, clockwise from the top.</summary>
    public static IReadOnlyList<string> AspectModes => Modes;

    private static readonly string[] Modes = ["free", "original", "1:1", "5:4", "4:3", "3:2", "16:9", "custom"];

    /// <summary>Where the mode sits on the dial, -1 for none.</summary>
    public static int AspectIndex(string mode) => Array.IndexOf(Modes, mode);

    /// <summary>
    /// A ratio with a portrait and a landscape form (not free, the picture's own or square). "custom" turns too: its
    /// W × H as typed, or H × W.
    /// </summary>
    public static bool CanSwap(string mode) => mode is "5:4" or "4:3" or "3:2" or "16:9" or "custom";

    /// <summary>
    /// Width / height of the mode, 0 for free. Portrait turns a swappable ratio around (4:3 → 3:4).
    /// <paramref name="custom"/> is the typed W / H of "custom" (#542).
    /// </summary>
    public static double Ratio(string mode, bool portrait, int originalWidth, int originalHeight, double custom = 0)
    {
        var ratio = mode switch
        {
            "original" => originalWidth > 0 && originalHeight > 0 ? (double)originalWidth / originalHeight : 0,
            "1:1" => 1.0,
            "5:4" => 5.0 / 4.0, // upright: 4:5, Instagram's portrait size (#542)
            "4:3" => 4.0 / 3.0,
            "3:2" => 3.0 / 2.0,
            "16:9" => 16.0 / 9.0,
            "custom" => custom,
            _ => 0,
        };
        return portrait && CanSwap(mode) && ratio > 0 ? 1 / ratio : ratio;
    }

    /// <summary>
    /// The custom ratio's numbers as small as they go ("1080 × 1350" → "4:5"), turned around in portrait. Only the ratio
    /// counts: resizing to exact pixels is a preset's job.
    /// </summary>
    public static string CustomNumbers(int width, int height, bool portrait)
    {
        var divisor = Gcd(Math.Max(1, width), Math.Max(1, height));
        var (w, h) = (Math.Max(1, width) / divisor, Math.Max(1, height) / divisor);
        return portrait ? $"{h}:{w}" : $"{w}:{h}";

        static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);
    }

    /// <summary>The numbers shown for a mode: "4:3", or "3:4" in portrait. Free, original and custom have none here.</summary>
    public static string Numbers(string mode, bool portrait)
    {
        if (!CanSwap(mode) || !portrait || mode == "custom")
            return mode;
        var parts = mode.Split(':');
        return $"{parts[1]}:{parts[0]}";
    }

    /// <summary>
    /// The largest frame of <paramref name="ratio"/> (width / height in pixels) on a picture, centred, in parts of the
    /// picture; the whole picture for 0 (free).
    /// </summary>
    public static Rect Centered(double ratio, int width, int height)
    {
        if (ratio <= 0 || width <= 0 || height <= 0)
            return new Rect(0, 0, 1, 1);
        var w = Math.Min(1, ratio * height / width);
        var h = Math.Min(1, width / (ratio * height));
        return new Rect((1 - w) / 2, (1 - h) / 2, w, h);
    }

    /// <summary>The frame in pixels of the picture (at least 1 × 1).</summary>
    public static PixelSize PixelSize(Rect normalized, int width, int height) =>
        new(Math.Max(1, (int)Math.Round(normalized.Width * width)), Math.Max(1, (int)Math.Round(normalized.Height * height)));

    /// <summary>
    /// Moves the frame by whole pixels of the picture (an arrow key: 1, with Shift 10). The frame lands on the pixel
    /// grid and stops at the picture's edges; its size stays.
    /// </summary>
    public static Rect Nudge(Rect normalized, int dx, int dy, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return normalized;
        var size = PixelSize(normalized, width, height);
        var x = Math.Clamp((int)Math.Round(normalized.X * width) + dx, 0, Math.Max(0, width - size.Width));
        var y = Math.Clamp((int)Math.Round(normalized.Y * height) + dy, 0, Math.Max(0, height - size.Height));
        return new Rect((double)x / width, (double)y / height, normalized.Width, normalized.Height);
    }

    /// <summary>How close (screen pixels) the frame's centre has to come to the picture's centre line to snap to it.</summary>
    public const double SnapDistance = 8;

    /// <summary>
    /// One side of a frame being dragged (issue #369): its start moved so its centre sits on the centre of
    /// <paramref name="min"/>…<paramref name="max"/> when it comes within <see cref="SnapDistance"/> of it. A frame as
    /// wide as the picture has nowhere to go and never counts as snapped.
    /// </summary>
    public static (double Start, bool Snapped) SnapToCentre(double start, double length, double min, double max)
    {
        var centre = (min + max) / 2;
        if (length >= max - min - 0.5 || Math.Abs(start + length / 2 - centre) > SnapDistance)
            return (start, false);
        return (centre - length / 2, true);
    }

    /// <summary>Space between the frame and the size chip.</summary>
    public const double ChipGap = 10;

    /// <summary>
    /// Top-left corner of the size chip: centred under the frame; inside its bottom edge when there's no room below
    /// <paramref name="area"/>; above the frame when the frame is too low for it as well. Never outside the area.
    /// </summary>
    public static Point SizeChipOrigin(Rect frame, Size chip, Rect area)
    {
        var x = Math.Clamp(frame.Center.X - chip.Width / 2, area.Left, Math.Max(area.Left, area.Right - chip.Width));
        double y;
        if (frame.Bottom + ChipGap + chip.Height <= area.Bottom)
            y = frame.Bottom + ChipGap;
        else if (frame.Height >= chip.Height + 3 * ChipGap)
            y = frame.Bottom - ChipGap * 1.6 - chip.Height; // clear of the handle on the bottom edge
        else
            y = frame.Top - ChipGap - chip.Height;
        return new Point(x, Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - chip.Height)));
    }

    /// <summary>Space kept between the aspect dial and the edges of the stage.</summary>
    public const double DialMargin = 8;

    /// <summary>Space between the aspect dial and the toolbar button it grows out of.</summary>
    public const double DialGap = 4;

    /// <summary>
    /// Top-left corner of the aspect dial (a square of <paramref name="side"/>) on a stage of <paramref name="stage"/>:
    /// centred on <paramref name="pointer"/> when A was pressed with the pointer over the stage, as the donut opens
    /// around the pointer; otherwise centred over its toolbar button, a little above <paramref name="buttonTop"/> (the
    /// middle of the button's top edge). Kept inside the stage.
    /// </summary>
    public static Point DialOrigin(Point? pointer, Point buttonTop, Size stage, double side)
    {
        var (x, y) = pointer is { } p
            ? (p.X - side / 2, p.Y - side / 2)
            : (buttonTop.X - side / 2, buttonTop.Y - side - DialGap);
        return new Point(
            Math.Clamp(x, DialMargin, Math.Max(DialMargin, stage.Width - side - DialMargin)),
            Math.Clamp(y, DialMargin, Math.Max(DialMargin, stage.Height - side - DialMargin)));
    }
}
