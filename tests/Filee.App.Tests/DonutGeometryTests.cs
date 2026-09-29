using Avalonia;
using Filee.App.Controls;

namespace Filee.App.Tests;

public class DonutGeometryTests
{
    private static readonly Point C = new(200, 200);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(12)]
    public void Slices_cover_the_full_circle(int count)
    {
        Assert.Equal(2 * Math.PI, DonutGeometry.Span(count) * count, 6);

        // Every direction maps to exactly one slice, and each slice's centre maps back to itself.
        for (var i = 0; i < count; i++)
            Assert.Equal(i, DonutGeometry.IndexAt(DonutGeometry.MidAngle(i, count), count));
        for (var deg = 0; deg < 360; deg++)
            Assert.InRange(DonutGeometry.IndexAt(deg * Math.PI / 180, count), 0, count - 1);
    }

    [Fact]
    public void First_slice_is_centred_at_twelve_o_clock()
    {
        // Straight up belongs to slice 0; slightly left of up still belongs to slice 0 (it straddles the top).
        Assert.Equal(0, DonutGeometry.HitTest(C, new Point(200, 80), 60, 150, 6).Index);
        Assert.Equal(0, DonutGeometry.HitTest(C, new Point(190, 80), 60, 150, 6).Index);
        // Right side (3 o'clock) with 4 slices is slice 1, bottom is 2, left is 3.
        Assert.Equal(1, DonutGeometry.HitTest(C, new Point(320, 200), 60, 150, 4).Index);
        Assert.Equal(2, DonutGeometry.HitTest(C, new Point(200, 320), 60, 150, 4).Index);
        Assert.Equal(3, DonutGeometry.HitTest(C, new Point(80, 200), 60, 150, 4).Index);
    }

    [Fact]
    public void Hole_ring_and_outside_are_classified()
    {
        Assert.Equal(DonutHitKind.Center, DonutGeometry.HitTest(C, new Point(210, 205), 60, 150, 6).Kind);
        Assert.Equal(DonutHitKind.Segment, DonutGeometry.HitTest(C, new Point(200, 100), 60, 150, 6).Kind);
        Assert.Equal(DonutHitKind.Outside, DonutGeometry.HitTest(C, new Point(200, 10), 60, 150, 6).Kind);
        Assert.Equal(DonutHitKind.Segment, DonutGeometry.HitTest(C, new Point(200, 45), 60, 150, 6, slack: 8).Kind);
        Assert.Equal(DonutHitKind.EmptyRing, DonutGeometry.HitTest(C, new Point(200, 100), 60, 150, 0).Kind);
    }
}
