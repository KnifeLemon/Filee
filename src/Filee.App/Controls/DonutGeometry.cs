// Pure math for the donut: slice angles, hit testing and slice outlines. Unit tested (DonutGeometryTests).
//
// Angles are measured clockwise from 12 o'clock, in radians. Slice i is centred on i·span,
// so the first preset sits at the top and the rest follow clockwise.

using Avalonia;

namespace Filee.App.Controls;

/// <summary>What is under a point of the donut.</summary>
public enum DonutHitKind
{
    /// <summary>Outside the donut.</summary>
    Outside,
    /// <summary>The hole in the middle (cancel / close).</summary>
    Center,
    /// <summary>A slice; <see cref="DonutHit.Index"/> is valid.</summary>
    Segment,
    /// <summary>The ring of a donut without slices (drop target for the first preset).</summary>
    EmptyRing,
    /// <summary>The ✎ edit button of the hovered slice (edit mode only).</summary>
    EditButton,
    /// <summary>The progress ring shown after a drop.</summary>
    ProgressRing,
    /// <summary>The × (cancel) button on the progress ring.</summary>
    ProgressCancel,
}

public readonly record struct DonutHit(DonutHitKind Kind, int Index = -1);

/// <summary>Geometry helpers.</summary>
public static class DonutGeometry
{
    /// <summary>Angle covered by one of <paramref name="count"/> slices.</summary>
    public static double Span(int count) => count <= 0 ? 2 * Math.PI : 2 * Math.PI / count;

    /// <summary>Centre angle of slice <paramref name="index"/>.</summary>
    public static double MidAngle(int index, int count) => index * Span(count);

    /// <summary>Converts a clockwise-from-top angle and radius into a point relative to <paramref name="center"/>.</summary>
    public static Point PointAt(Point center, double radius, double angle) =>
        new(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));

    /// <summary>Clockwise-from-top angle (0..2π) of <paramref name="p"/> around <paramref name="center"/>.</summary>
    public static double AngleOf(Point center, Point p)
    {
        var a = Math.Atan2(p.X - center.X, center.Y - p.Y);
        return a < 0 ? a + 2 * Math.PI : a;
    }

    /// <summary>Index of the slice containing the given angle.</summary>
    public static int IndexAt(double angle, int count)
    {
        if (count <= 0)
            return -1;
        var span = Span(count);
        return (int)Math.Floor((angle + span / 2) / span) % count;
    }

    /// <summary>Classifies a point. <paramref name="slack"/> extends the outer radius to include popped-out slices.</summary>
    public static DonutHit HitTest(Point center, Point p, double innerRadius, double outerRadius, int count, double slack = 0)
    {
        var distance = Math.Sqrt(Math.Pow(p.X - center.X, 2) + Math.Pow(p.Y - center.Y, 2));
        if (distance < innerRadius)
            return new DonutHit(DonutHitKind.Center);
        if (distance > outerRadius + slack)
            return new DonutHit(DonutHitKind.Outside);
        if (count == 0)
            return new DonutHit(DonutHitKind.EmptyRing);
        return new DonutHit(DonutHitKind.Segment, IndexAt(AngleOf(center, p), count));
    }
}
