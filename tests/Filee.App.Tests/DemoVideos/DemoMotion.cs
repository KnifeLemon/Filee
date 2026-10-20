// Easing and timed paths for the demo videos: where the mouse is at any moment of a clip.

using Avalonia;

namespace Filee.App.Tests.DemoVideos;

internal static class Ease
{
    public static double Clamp01(double t) => Math.Clamp(t, 0, 1);

    public static double InOutCubic(double t)
    {
        t = Clamp01(t);
        return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    }

    public static double OutCubic(double t) => 1 - Math.Pow(1 - Clamp01(t), 3);

    public static double OutBack(double t)
    {
        t = Clamp01(t);
        const double c1 = 1.7;
        const double c3 = c1 + 1;
        return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
    }

    /// <summary>0 before <paramref name="start"/>, 1 after <paramref name="start"/> + <paramref name="length"/>, linear between.</summary>
    public static double Span(double t, double start, double length) => Clamp01((t - start) / length);

    /// <summary>Fades in over <paramref name="fade"/> from <paramref name="from"/> and out again before <paramref name="to"/>.</summary>
    public static double Window(double t, double from, double to, double fade) =>
        Math.Min(Span(t, from, fade), 1 - Span(t, to - fade, fade));

    public static Point Lerp(Point a, Point b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
}

/// <summary>
/// A mouse path through timed waypoints. Between two waypoints the pointer eases in and out and bows slightly to one
/// side, as a hand moving a mouse does; equal waypoints hold it still.
/// </summary>
internal sealed class MousePath
{
    private readonly List<(double Time, Point At, double Bow)> _points = [];

    /// <summary>Be at <paramref name="at"/> at <paramref name="time"/>, arriving along a curve bowed by <paramref name="bow"/> (share of the distance).</summary>
    public MousePath At(double time, Point at, double bow = 0.08)
    {
        if (_points.Count > 0 && time < _points[^1].Time)
            throw new ArgumentException("Waypoints must be in time order.", nameof(time));
        _points.Add((time, at, bow));
        return this;
    }

    /// <summary>Stay where the last waypoint is until <paramref name="time"/>.</summary>
    public MousePath Hold(double time) => At(time, _points[^1].At, 0);

    public Point this[double time]
    {
        get
        {
            if (time <= _points[0].Time)
                return _points[0].At;
            for (var i = 1; i < _points.Count; i++)
            {
                var (t1, b, bow) = _points[i];
                if (time > t1)
                    continue;
                var (t0, a, _) = _points[i - 1];
                var u = t1 <= t0 ? 1 : Ease.InOutCubic((time - t0) / (t1 - t0));
                var straight = Ease.Lerp(a, b, u);
                var d = b - a;
                var normal = new Point(-d.Y, d.X);
                return straight + normal * (bow * Math.Sin(Math.PI * u));
            }
            return _points[^1].At;
        }
    }
}

/// <summary>Things that happen once, at a moment of the clip, while frames are stepped in order.</summary>
internal sealed class Cues
{
    private double _previous = double.NegativeInfinity;
    private double _now = double.NegativeInfinity;

    public void Advance(double now)
    {
        _previous = _now;
        _now = now;
    }

    /// <summary>True on the one frame that reaches <paramref name="time"/>.</summary>
    public bool Hit(double time) => _previous < time && time <= _now;
}
