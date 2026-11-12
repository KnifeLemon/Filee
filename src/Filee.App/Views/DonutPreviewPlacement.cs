// Where the donut on screen of Settings → Donut toolbar goes (ToolbarPage, DonutPreviewWindow): never over the main
// window when the screens allow it. In screen pixels throughout; each screen's own scaling sizes the donut there (macOS
// reports points: scaling 1).
//
// In order: where it was last dragged, against the main window, if all of it is on a screen there and clear of the
// window; beside the window, right then left; on another screen with room for it; else the window moves over to make
// room, narrower down to its minimum width if it must; else, on a screen too small for both, over the edge of the
// window's column not in use, above the window (Over: the donut is owned by it). Beside the window, the donut's edge
// facing it stays put as a slider grows it; grown off its screen or onto the window, it is placed again.

using Avalonia;

namespace Filee.App.Views;

/// <summary>A screen's working area, in pixels, and how many pixels a device-independent pixel takes there.</summary>
internal readonly record struct PreviewScreen(PixelRect Area, double Scaling);

/// <summary>
/// Which edge of the donut stays put as it grows: the one facing the main window, so it grows away from it.
/// </summary>
internal enum PreviewAnchor
{
    /// <summary>Its centre (on another screen, or where it was dragged).</summary>
    Centre,

    /// <summary>Its left edge (the donut is right of the window).</summary>
    Left,

    /// <summary>Its right edge (the donut is left of the window).</summary>
    Right,
}

/// <summary>
/// Where the donut goes: <see cref="Point"/> is its centre, or the middle of its anchored edge (<see cref="Anchor"/>).
/// <see cref="MovedWindow"/> is where the main window moves to first, if it must; <see cref="Over"/> that the donut can
/// only go over the window, above it.
/// </summary>
internal sealed record PreviewPlace(PixelPoint Point, PreviewAnchor Anchor, PixelRect? MovedWindow = null, bool Over = false)
{
    /// <summary>The donut's top-left corner for a donut <paramref name="side"/> pixels across.</summary>
    public PixelPoint TopLeftFor(int side) => Anchor switch
    {
        PreviewAnchor.Left => new PixelPoint(Point.X, Point.Y - side / 2),
        PreviewAnchor.Right => new PixelPoint(Point.X - side, Point.Y - side / 2),
        _ => new PixelPoint(Point.X - side / 2, Point.Y - side / 2),
    };

    /// <summary>
    /// Whether a donut <paramref name="side"/> pixels across, at <paramref name="place"/>, still fits: all of it on one
    /// of <paramref name="screens"/> and, unless it was put over it, clear of <paramref name="window"/>.
    /// </summary>
    public bool Fits(int side, PixelRect window, IEnumerable<PreviewScreen> screens) =>
        BoxFor(side) is var box && screens.Any(s => s.Area.Contains(box)) && (Over || !box.Intersects(window));

    /// <summary>The square the donut takes, <paramref name="side"/> pixels across.</summary>
    public PixelRect BoxFor(int side) => new(TopLeftFor(side), new PixelSize(side, side));
}

internal static class DonutPreviewPlacement
{
    /// <summary>Room between the main window and the donut beside it, in pixels.</summary>
    internal const int Gap = 24;

    /// <summary>A size in device-independent pixels on <paramref name="screen"/>, in its pixels.</summary>
    internal static int Pixels(double size, PreviewScreen screen) => (int)Math.Ceiling(size * screen.Scaling);

    /// <summary>
    /// Where the donut goes. <paramref name="window"/>: the main window's frame; <paramref name="main"/>: the index of
    /// its screen in <paramref name="screens"/>; <paramref name="size"/>: the donut's side in device-independent
    /// pixels; <paramref name="dragged"/>: where it was last dragged, its centre against the window's top-left corner;
    /// <paramref name="minWidth"/>: the window's narrowest frame in pixels, or null when it can't be moved (maximized,
    /// full screen, or the user is moving a slider); <paramref name="overRight"/>: which edge of the window the donut
    /// may go over when nothing else is left.
    /// </summary>
    public static PreviewPlace Place(PixelRect window, IReadOnlyList<PreviewScreen> screens, int main, double size,
        PixelPoint? dragged, int? minWidth, bool overRight)
    {
        if (screens.Count == 0)
            return new PreviewPlace(new PixelPoint(window.Right + Gap, window.Center.Y), PreviewAnchor.Left);
        var home = screens[Math.Clamp(main, 0, screens.Count - 1)];
        var side = Pixels(size, home);

        if (dragged is { } offset)
        {
            var centre = new PixelPoint(window.X + offset.X, window.Y + offset.Y);
            foreach (var screen in screens)
            {
                var box = new PreviewPlace(centre, PreviewAnchor.Centre).BoxFor(Pixels(size, screen));
                if (screen.Area.Contains(centre) && screen.Area.Contains(box) && !box.Intersects(window))
                    return new PreviewPlace(centre, PreviewAnchor.Centre);
            }
        }
        if (Beside(home.Area, window, side) is { } beside)
            return beside;
        for (var i = 0; i < screens.Count; i++)
        {
            if (screens[i] == home)
                continue;
            var there = Pixels(size, screens[i]);
            var middle = new PreviewPlace(screens[i].Area.Center, PreviewAnchor.Centre);
            // Not under a window that reaches onto that screen too.
            if (screens[i].Area.Width >= there && screens[i].Area.Height >= there && !middle.BoxFor(there).Intersects(window))
                return middle;
        }
        if (minWidth is { } min)
        {
            // As wide as it is first; only then narrower, just enough, down to its minimum.
            foreach (var width in new[] { window.Width, Math.Max(min, home.Area.Width - Gap - side) }.Distinct())
            {
                if (width > window.Width || SlideFor(home.Area, window.Width == width ? window : window.WithWidth(width), side) is not { } x)
                    continue;
                var moved = new PixelRect(x, window.Y, width, window.Height);
                if (Beside(home.Area, moved, side) is { } made)
                    return made with { MovedWindow = moved };
            }
        }
        var y = MiddleY(home.Area, window, side);
        return overRight
            ? new PreviewPlace(new PixelPoint(home.Area.Right, y), PreviewAnchor.Right, Over: true)
            : new PreviewPlace(new PixelPoint(home.Area.X, y), PreviewAnchor.Left, Over: true);
    }

    /// <summary>
    /// Where <paramref name="window"/> slides to (its left edge) so the donut fits beside it on
    /// <paramref name="area"/>: the shorter way, left to leave room on the right or right to leave room on the left.
    /// Null if both don't fit.
    /// </summary>
    internal static int? SlideFor(PixelRect area, PixelRect window, int side)
    {
        if (window.Width + Gap + side > area.Width)
            return null;
        var left = area.Right - Gap - side - window.Width;
        var right = area.X + side + Gap;
        return Math.Abs(left - window.X) <= Math.Abs(right - window.X) ? left : right;
    }

    /// <summary>
    /// The donut beside <paramref name="window"/>, right then left, where <paramref name="area"/> has room; or null.
    /// </summary>
    internal static PreviewPlace? Beside(PixelRect area, PixelRect window, int side)
    {
        var y = MiddleY(area, window, side);
        if (window.Right + Gap + side <= area.Right)
            return new PreviewPlace(new PixelPoint(window.Right + Gap, y), PreviewAnchor.Left);
        if (window.X - Gap - side >= area.X)
            return new PreviewPlace(new PixelPoint(window.X - Gap, y), PreviewAnchor.Right);
        return null;
    }

    // Level with the window's middle, all of it on the screen.
    private static int MiddleY(PixelRect area, PixelRect window, int side) =>
        Math.Clamp(window.Y + window.Height / 2, area.Y + side / 2, Math.Max(area.Y + side / 2, area.Bottom - side / 2));
}
