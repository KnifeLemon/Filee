// The crop overlay's arithmetic: ratios and their portrait forms, keyboard nudges, snapping to the centre, the frame
// in pixels and where the size chip goes, and for the perspective mode the corners' allowed shapes and moves, the
// straightened size and when the corners are still a plain rectangle.

using Avalonia;
using Filee.App.Controls;

namespace Filee.App.Tests;

public class CropMathTests
{
    [Fact]
    public void A_corner_moves_only_to_a_convex_untwisted_shape()
    {
        var square = new CropQuad(new Point(0.2, 0.2), new Point(0.8, 0.2),
            new Point(0.8, 0.8), new Point(0.2, 0.8));
        var targets = new[]
        {
            new Point(0.9, 0.7), // twisted
            new Point(0.6, 0.6), // concave
            new Point(0.5, 0.5), // three collinear points
            new Point(0.9, 0.2), // passes the neighbouring corner
        };
        static Point Rotate(Point p) => new(1 - p.Y, p.X);

        for (var corner = 0; corner < 4; corner++)
        {
            var valid = new Point(0.3, 0.3);
            foreach (var _ in Enumerable.Range(0, corner))
                valid = Rotate(valid);
            var moved = CropMath.MoveCorner(square, corner, valid, 1600, 1000);
            for (var i = 0; i < 4; i++)
                Assert.Equal(i == corner ? valid : square[i], moved[i]);

            foreach (var initial in targets)
            {
                var target = initial;
                foreach (var _ in Enumerable.Range(0, corner))
                    target = Rotate(target);
                Assert.Equal(square, CropMath.MoveCorner(square, corner, target, 1600, 1000));
            }
        }

        var passing = new CropQuad(new Point(0.2, 0.2), new Point(0.6, 0.2),
            new Point(0.6, 0.8), new Point(0.2, 0.8));
        Assert.Equal(passing, CropMath.MoveCorner(passing, 0, new Point(0.7, 0.2), 1600, 1000));
        Assert.False(CropMath.IsValidQuad(new CropQuad(new Point(0, 0), new Point(0, 1),
            new Point(1, 1), new Point(1, 0)), 1600, 1000));

        foreach (var (delta, expected) in new[] { (2.5e-9, true), (6.25e-10, false) })
        {
            var turn = 800 * delta;
            Assert.Equal(expected, turn > 1e-6);
            var thin = new CropQuad(new Point(0, 0), new Point(800 / 1600.0, 0),
                new Point(1, delta / 1000), new Point(0, 1));
            Assert.Equal(expected, CropMath.IsValidQuad(thin, 1600, 1000));
        }

        var fractional = new CropQuad(new Point(0.6, 0.2), new Point(1, 0), new Point(1, 1), new Point(0, 1));
        Assert.Equal(fractional, CropMath.MoveCorner(CropQuad.Full, 0, fractional.TopLeft, 1, 1));
    }

    [Fact]
    public void A_corner_pushed_past_the_edge_stops_on_it()
    {
        var nearEdge = new CropQuad(new Point(5 / 1600.0, 0.2), new Point(0.8, 0.2),
            new Point(0.8, 0.8), new Point(0.2, 0.8));
        Assert.Equal(nearEdge.With(0, new Point(0, 0.2)),
            CropMath.MoveCorner(nearEdge, 0, new Point(-50 / 1600.0, 0.2), 1600, 1000));
        Assert.Equal(nearEdge.With(0, new Point(0, 0)),
            CropMath.MoveCorner(nearEdge, 0, new Point(-0.3, -0.4), 1600, 1000));
        Assert.Equal(nearEdge.With(2, new Point(1, 1)),
            CropMath.MoveCorner(nearEdge, 2, new Point(1.2, 1.5), 1600, 1000));

        var threeFromEdge = nearEdge.With(0, new Point(3 / 1600.0, 0.2));
        Assert.Equal(nearEdge.With(0, new Point(0, 0.2)), CropMath.NudgeCorner(threeFromEdge, 0, -10, 0, 1600, 1000));

        // Stopped on the edge, the corner would cross the bottom side: still refused.
        var leaning = new CropQuad(new Point(0.2, 0.2), new Point(0.8, 0.2), new Point(0.8, 0.8), new Point(0, 0.8));
        Assert.Equal(leaning, CropMath.MoveCorner(leaning, 0, new Point(-1, 0.9), 1600, 1000));
    }

    [Fact]
    public void Corners_on_a_whole_pixel_rectangle_are_a_plain_crop()
    {
        Assert.Equal(new PixelRect(0, 0, 1600, 1000), CropMath.AxisAlignedRect(CropQuad.Full, 1600, 1000));
        var nudged = CropMath.NudgeCorner(CropMath.NudgeCorner(CropQuad.Full, 0, 7, 0, 1600, 1000), 3, 7, 0, 1600, 1000);
        Assert.Equal(new PixelRect(7, 0, 1593, 1000), CropMath.AxisAlignedRect(nudged, 1600, 1000));
        Assert.Null(CropMath.AxisAlignedRect(CropQuad.Full.With(0, new Point(7 / 1600.0, 0)), 1600, 1000));
        Assert.Null(CropMath.AxisAlignedRect(CropQuad.Full.With(0, new Point(0.5 / 1600, 0)).With(3, new Point(0.5 / 1600, 1)), 1600, 1000));
    }

    [Fact]
    public void Perspective_size_is_the_longer_of_each_pair_of_sides()
    {
        Assert.Equal(new PixelSize(1600, 1000), CropMath.PerspectiveSize(CropQuad.Full, 1600, 1000));
        var trapezoid = new CropQuad(new Point(0.2, 0.1), new Point(0.7, 0.2),
            new Point(0.9, 0.8), new Point(0.1, 0.9));
        double Distance(Point a, Point b) =>
            Math.Sqrt(Math.Pow((a.X - b.X) * 1600, 2) + Math.Pow((a.Y - b.Y) * 1000, 2));
        var expectedWidth = (int)Math.Round(Math.Max(Distance(trapezoid.TopLeft, trapezoid.TopRight),
            Distance(trapezoid.BottomLeft, trapezoid.BottomRight)));
        var expectedHeight = (int)Math.Round(Math.Max(Distance(trapezoid.TopLeft, trapezoid.BottomLeft),
            Distance(trapezoid.TopRight, trapezoid.BottomRight)));
        Assert.Equal(new PixelSize(expectedWidth, expectedHeight), CropMath.PerspectiveSize(trapezoid, 1600, 1000));
        var rounded = new CropQuad(new Point(0, 0), new Point(0.126, 0),
            new Point(0.126, 0.234), new Point(0, 0.234));
        Assert.Equal(new PixelSize(13, 23), CropMath.PerspectiveSize(rounded, 100, 100));
        var tiny = new CropQuad(new Point(0, 0), new Point(0.001, 0),
            new Point(0.001, 0.001), new Point(0, 0.001));
        Assert.Equal(new PixelSize(1, 1), CropMath.PerspectiveSize(tiny, 100, 100));
    }

    [Fact]
    public void Arrow_keys_move_a_corner_by_whole_pixels_and_stop_where_the_shape_would_break()
    {
        var loose = new CropQuad(new Point(200.4 / 1000, 200.4 / 1000), new Point(0.8, 0.2),
            new Point(0.8, 0.8), new Point(0.2, 0.8));
        Assert.Equal(new Point(201 / 1000.0, 190 / 1000.0),
            CropMath.NudgeCorner(loose, 0, 1, -10, 1000, 1000).TopLeft);
        for (var corner = 0; corner < 4; corner++)
        {
            var dx = corner is 0 or 3 ? 1 : -1;
            var dy = corner is 0 or 1 ? 10 : -10;
            var moved = CropMath.NudgeCorner(CropQuad.Full, corner, dx, dy, 1000, 1000);
            for (var i = 0; i < 4; i++)
            {
                var p = CropQuad.Full[i];
                var expected = i == corner ? new Point(p.X + dx / 1000.0, p.Y + dy / 1000.0) : p;
                Assert.Equal(expected.X, moved[i].X, 9);
                Assert.Equal(expected.Y, moved[i].Y, 9);
            }
            Assert.Equal(CropQuad.Full, CropMath.NudgeCorner(CropQuad.Full, corner, -dx, -dy, 1000, 1000));
        }
        var almostLine = new CropQuad(new Point(0.2, 0.799), new Point(0.8, 0.2),
            new Point(0.8, 0.8), new Point(0.2, 0.8));
        Assert.True(CropMath.IsValidQuad(almostLine, 1000, 1000));
        Assert.Equal(almostLine, CropMath.NudgeCorner(almostLine, 0, 0, 1, 1000, 1000));
        Assert.Equal(loose, CropMath.NudgeCorner(loose, 0, 1, 1, 0, 0));
    }

    [Theory]
    [InlineData("free", false, 0.0)]
    [InlineData("1:1", true, 1.0)]
    [InlineData("5:4", false, 5.0 / 4.0)]
    [InlineData("5:4", true, 4.0 / 5.0)] // Instagram's portrait size
    [InlineData("4:3", false, 4.0 / 3.0)]
    [InlineData("4:3", true, 3.0 / 4.0)]
    [InlineData("3:2", true, 2.0 / 3.0)]
    [InlineData("16:9", false, 16.0 / 9.0)]
    [InlineData("16:9", true, 9.0 / 16.0)]
    [InlineData("original", true, 1.6)] // the picture's own ratio isn't turned around
    public void Ratios_and_their_portrait_forms(string mode, bool portrait, double expected)
    {
        Assert.Equal(expected, CropMath.Ratio(mode, portrait, 1600, 1000), 6);
    }

    [Theory]
    // A 100 px wide frame over a 0…1000 px picture: centred at 500 when its start is 450.
    [InlineData(450, 450, true)]   // already centred
    [InlineData(443, 450, true)]   // 7 px off: pulled in
    [InlineData(458, 450, true)]   // 8 px off: still pulled in
    [InlineData(459, 459, false)]  // 9 px off: left alone
    [InlineData(100, 100, false)]  // far away
    public void A_moved_frame_snaps_to_the_centre_line(double start, double expected, bool snapped)
    {
        Assert.Equal((expected, snapped), CropMath.SnapToCentre(start, 100, 0, 1000));
    }

    [Fact]
    public void A_frame_as_wide_as_the_picture_never_counts_as_snapped()
    {
        Assert.Equal((0.0, false), CropMath.SnapToCentre(0, 1000, 0, 1000));
    }

    [Fact]
    public void Only_fixed_ratios_that_arent_square_swap()
    {
        Assert.Equal(["5:4", "4:3", "3:2", "16:9", "custom"], CropMath.AspectModes.Where(CropMath.CanSwap));
        Assert.Equal("4:5", CropMath.Numbers("5:4", portrait: true));
        Assert.Equal("9:16", CropMath.Numbers("16:9", portrait: true));
        Assert.Equal("16:9", CropMath.Numbers("16:9", portrait: false));
        Assert.Equal("1:1", CropMath.Numbers("1:1", portrait: true));
        Assert.Equal(0, CropMath.Ratio("original", false, 0, 0));
        Assert.Equal(4, CropMath.AspectIndex("4:3"));
        Assert.Equal(-1, CropMath.AspectIndex("21:9"));
    }

    [Fact]
    public void A_custom_ratio_is_the_typed_w_over_h_shown_as_small_as_it_goes()
    {
        // 1080 × 1350 is 4:5 (#542); the hub turns it to 5:4.
        Assert.Equal(1080.0 / 1350, CropMath.Ratio("custom", false, 1600, 1000, 1080.0 / 1350), 6);
        Assert.Equal(1350.0 / 1080, CropMath.Ratio("custom", true, 1600, 1000, 1080.0 / 1350), 6);
        Assert.Equal("4:5", CropMath.CustomNumbers(1080, 1350, portrait: false));
        Assert.Equal("5:4", CropMath.CustomNumbers(1080, 1350, portrait: true));
        Assert.Equal("7:3", CropMath.CustomNumbers(7, 3, portrait: false));
        Assert.Equal("1:1", CropMath.CustomNumbers(0, 0, portrait: false)); // an emptied box counts as 1
        Assert.Equal(0, CropMath.Ratio("custom", true, 1600, 1000)); // no ratio yet: free, never 1 / 0
        Assert.Equal(7, CropMath.AspectIndex("custom")); // the last slice
    }

    [Fact]
    public void Nudging_moves_by_whole_pixels_and_stops_at_the_edges()
    {
        var frame = new Rect(0.25, 0.25, 0.5, 0.5); // 400, 250, 800 × 500 of 1600 × 1000
        Assert.Equal(new Rect(401 / 1600.0, 250 / 1000.0, 0.5, 0.5), CropMath.Nudge(frame, 1, 0, 1600, 1000));
        Assert.Equal(new Rect(400 / 1600.0, 240 / 1000.0, 0.5, 0.5), CropMath.Nudge(frame, 0, -10, 1600, 1000));
        Assert.Equal(new Rect(0, 250 / 1000.0, 0.5, 0.5), CropMath.Nudge(frame, -1000, 0, 1600, 1000));
        Assert.Equal(new Rect(800 / 1600.0, 500 / 1000.0, 0.5, 0.5), CropMath.Nudge(frame, 5000, 5000, 1600, 1000));

        // Off the pixel grid (a frame dragged on screen), the first nudge lands on it.
        var loose = new Rect(100.4 / 1600, 0, 0.5, 0.5);
        Assert.Equal(101 / 1600.0, CropMath.Nudge(loose, 1, 0, 1600, 1000).X, 9);

        // A frame as large as the picture doesn't move; an unknown picture leaves it alone.
        Assert.Equal(new Rect(0, 0, 1, 1), CropMath.Nudge(new Rect(0, 0, 1, 1), 3, 3, 1600, 1000));
        Assert.Equal(frame, CropMath.Nudge(frame, 3, 3, 0, 0));
    }

    [Fact]
    public void The_frame_in_pixels_is_rounded_and_never_empty()
    {
        Assert.Equal(new PixelSize(800, 333), CropMath.PixelSize(new Rect(0, 0, 0.5, 0.333), 1600, 1000));
        Assert.Equal(new PixelSize(1, 1), CropMath.PixelSize(new Rect(0, 0, 0.0001, 0), 1600, 1000));
    }

    [Fact]
    public void A_carried_ratio_starts_as_the_largest_centred_frame()
    {
        // 4:5 on a landscape 1600 × 1000: the whole height, 800 wide in the middle.
        Assert.Equal(new Rect(0.25, 0, 0.5, 1), CropMath.Centered(4.0 / 5, 1600, 1000));
        // 16:9 on an upright 1000 × 1600: the whole width, 562.5 high in the middle.
        var wide = CropMath.Centered(16.0 / 9, 1000, 1600);
        Assert.Equal((0, 1), (wide.X, wide.Width));
        Assert.Equal(562.5 / 1600, wide.Height, 6);
        Assert.Equal((1 - wide.Height) / 2, wide.Y, 6);
        // The picture's own ratio, and free, fill it.
        Assert.Equal(new Rect(0, 0, 1, 1), CropMath.Centered(1.6, 1600, 1000));
        Assert.Equal(new Rect(0, 0, 1, 1), CropMath.Centered(0, 1600, 1000));
    }

    [Fact]
    public void The_size_chip_sits_under_the_frame_inside_it_or_above_it()
    {
        var area = new Rect(0, 0, 800, 600);
        var chip = new Size(100, 24);

        // Room below: centred under the frame.
        Assert.Equal(new Point(150, 310), CropMath.SizeChipOrigin(new Rect(100, 100, 200, 200), chip, area));

        // The frame reaches the bottom: inside along its bottom edge, clear of the edge's handle.
        var low = CropMath.SizeChipOrigin(new Rect(100, 300, 200, 300), chip, area);
        Assert.Equal(150, low.X);
        Assert.True(low.Y + chip.Height < 600 - CropMath.ChipGap && low.Y > 300);

        // A thin frame at the bottom: above it.
        Assert.Equal(new Point(150, 576 - CropMath.ChipGap - 24), CropMath.SizeChipOrigin(new Rect(100, 576, 200, 24), chip, area));

        // Near a side: kept inside the area.
        Assert.Equal(0, CropMath.SizeChipOrigin(new Rect(0, 100, 40, 40), chip, area).X);
        Assert.Equal(700, CropMath.SizeChipOrigin(new Rect(760, 100, 40, 40), chip, area).X);
    }

    [Fact]
    public void The_dial_opens_around_the_pointer_or_over_its_button_inside_the_stage()
    {
        var stage = new Size(1000, 700);
        const double side = 246;
        var button = new Point(400, 620);

        // With the pointer over the stage: centred on it.
        Assert.Equal(new Point(377, 177), CropMath.DialOrigin(new Point(500, 300), button, stage, side));

        // Near an edge or a corner: moved in until it fits, with a margin.
        Assert.Equal(new Point(CropMath.DialMargin, CropMath.DialMargin), CropMath.DialOrigin(new Point(20, 10), button, stage, side));
        Assert.Equal(new Point(1000 - side - CropMath.DialMargin, 700 - side - CropMath.DialMargin),
            CropMath.DialOrigin(new Point(990, 690), button, stage, side));

        // Without a pointer (the keyboard): centred over the button, just above it.
        Assert.Equal(new Point(277, 620 - side - CropMath.DialGap), CropMath.DialOrigin(null, button, stage, side));
        // A button near the side still gets the whole dial.
        Assert.Equal(CropMath.DialMargin, CropMath.DialOrigin(null, new Point(30, 620), stage, side).X);

        // A stage smaller than the dial: from its top left corner.
        Assert.Equal(new Point(CropMath.DialMargin, CropMath.DialMargin), CropMath.DialOrigin(new Point(100, 100), button, new Size(200, 200), side));
    }
}
