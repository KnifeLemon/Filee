// Gerber PCB layers: the reader (formats, apertures, macros, arcs, regions, polarity, blocks), the SVG it draws, the
// layer colours, the limits a damaged or crafted file runs into, and conversions through the vector engine. The test
// layers are written here command by command, as PCB programs write them.

using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Vector;
using Filee.Engines.Vector.Gerber;
using ImageMagick;

namespace Filee.Engines.Tests;

public class GerberTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>A layer in millimetres with 6 decimals, the commands between the header and M02.</summary>
    private static string Layer(params string[] commands) =>
        "%FSLAX46Y46*%\n%MOMM*%\n" + string.Join('\n', commands) + "\nM02*\n";

    private static GerberImage Read(string gerber) => GerberReader.Read(Encoding.ASCII.GetBytes(gerber), TestContext.Current.CancellationToken);

    private static string Svg(string gerber, string name = "layer.gbr") =>
        GerberSvg.ToSvg(Encoding.ASCII.GetBytes(gerber), name, ct: TestContext.Current.CancellationToken);

    private static GerberBounds Bounds(GerberImage image) => image.Objects.Aggregate(GerberBounds.Empty, (b, o) => b.Union(o.Bounds));

    private static void AssertBounds(GerberBounds actual, double minX, double minY, double maxX, double maxY)
    {
        Assert.Equal(minX, actual.MinX, 3);
        Assert.Equal(minY, actual.MinY, 3);
        Assert.Equal(maxX, actual.MaxX, 3);
        Assert.Equal(maxY, actual.MaxY, 3);
    }

    /// <summary>The colour at (x, y) millimetres from the drawing's top left, rendered by the vector engine at 20 px/mm.</summary>
    private static IMagickColor<byte> PixelAt(string svg, double x, double y)
    {
        var path = Path.Combine(Path.GetTempPath(), $"filee-gerber-{Guid.NewGuid():N}.svg");
        File.WriteAllText(path, svg);
        try
        {
            using var drawing = SvgRenderer.Load(path);
            using var image = SvgRenderer.Rasterize(drawing, (float)(20 / (SvgRenderer.CssDpi / 25.4)));
            return image.GetPixels().GetPixel((int)(x * 20), (int)(y * 20)).ToColor()!;
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ───────────────────────── Coordinates, units, apertures ─────────────────────────

    [Fact]
    public void A_flash_is_placed_in_millimetres_from_an_inch_file()
    {
        var image = Read("%FSLAX24Y24*%\n%MOIN*%\n%ADD10C,0.1*%\nD10*\nX10000Y5000D03*\nM02*");
        var flash = Assert.IsType<GerberFlash>(Assert.Single(image.Objects));
        AssertBounds(flash.Bounds, 25.4 - 1.27, 12.7 - 1.27, 25.4 + 1.27, 12.7 + 1.27);
    }

    [Fact]
    public void Trailing_zero_omission_and_incremental_coordinates_are_read()
    {
        // FSTIA: "15" is 1.5 with two integer digits, then each move adds to the last point.
        var image = Read("%FSTIX23Y23*%\n%MOMM*%\n%ADD10C,0.2*%\nD10*\nX15Y15D02*\nX15D01*\nM02*");
        var stroke = Assert.IsType<GerberStroke>(Assert.Single(image.Objects));
        AssertBounds(stroke.Bounds, 15 - 0.1, 15 - 0.1, 30 + 0.1, 15 + 0.1);
    }

    [Fact]
    public void Decimal_points_in_coordinates_are_taken_as_written()
    {
        var image = Read(Layer("%ADD10C,1*%", "D10*", "X1.5Y-2.25D03*"));
        AssertBounds(Bounds(image), 1, -2.75, 2, -1.75);
    }

    [Fact]
    public void Standard_apertures_with_holes_flash_with_the_hole_open()
    {
        var svg = Svg(Layer("%ADD10C,4X2*%", "%ADD11R,4X2X1*%", "%ADD12O,4X2*%", "%ADD13P,4X6X30X1*%", "%ADD14C,4X2X1*%",
            "D10*", "X0Y0D03*", "D11*", "X10000000Y0D03*", "D12*", "X20000000Y0D03*", "D13*", "X30000000Y0D03*", "D14*", "X40000000Y0D03*"));
        Assert.Contains("id=\"a12\" x=\"-2\" y=\"-1\" width=\"4\" height=\"2\" rx=\"1\"", svg);
        // The first pad's centre is 4 mm from the drawing's left and top edges (its 2 mm radius and the 2 mm margin).
        var copper = PixelAt(svg, 4 - 1.5, 4);
        Assert.NotEqual(PixelAt(svg, 0.5, 0.5), copper);
        Assert.Equal(PixelAt(svg, 0.5, 0.5), PixelAt(svg, 4, 4)); // the circle's round hole
        Assert.Equal(PixelAt(svg, 0.5, 0.5), PixelAt(svg, 44, 4)); // a pre-2016 rectangular hole
    }

    [Fact]
    public void A_polygon_aperture_turns_its_first_vertex_by_the_rotation()
    {
        var image = Read(Layer("%ADD10P,2X4X45*%", "D10*", "X0Y0D03*"));
        // A square on its side at 45°: corners at ±0.707 on both axes.
        AssertBounds(Bounds(image), -0.7071, -0.7071, 0.7071, 0.7071);
    }

    [Fact]
    public void A_rectangle_aperture_sweeps_a_hull_and_a_zero_length_draw_is_a_dot()
    {
        var image = Read(Layer("%ADD10R,2X1*%", "%ADD11C,1*%", "D10*", "X0Y0D02*", "X10000000Y0D01*", "D11*", "X0Y5000000D02*", "X0Y5000000D01*"));
        var swept = Assert.IsType<GerberRegion>(image.Objects[0]);
        AssertBounds(swept.Bounds, -1, -0.5, 11, 0.5);
        var dot = Assert.IsType<GerberRegion>(image.Objects[1]);
        AssertBounds(dot.Bounds, -0.5, 4.5, 0.5, 5.5);
    }

    [Fact]
    public void Consecutive_draws_with_one_aperture_share_a_path()
    {
        var image = Read(Layer("%ADD10C,0.25*%", "D10*", "X0Y0D02*", "X1000000Y0D01*", "X1000000Y1000000D01*", "X5000000Y5000000D02*", "X6000000Y5000000D01*"));
        var stroke = Assert.IsType<GerberStroke>(Assert.Single(image.Objects));
        Assert.Equal(2, stroke.Contours.Count);
        Assert.Equal(3, stroke.SegmentCount);
    }

    // ───────────────────────── Arcs and regions ─────────────────────────

    [Fact]
    public void A_multi_quadrant_arc_that_ends_where_it_starts_is_a_full_circle()
    {
        var image = Read(Layer("%ADD10C,0.5*%", "D10*", "G75*", "X5000000Y0D02*", "G03*", "X5000000Y0I-5000000J0D01*"));
        AssertBounds(Bounds(image), -5.25, -5.25, 5.25, 5.25);
        var svg = Svg(Layer("%ADD10C,0.5*%", "D10*", "G75*", "X5000000Y0D02*", "G03*", "X5000000Y0I-5000000J0D01*"));
        Assert.Contains("A5 5 0 0 1 -5 0A5 5 0 0 1 5 0", svg); // two halves
    }

    [Fact]
    public void A_clockwise_arc_bulges_to_its_side()
    {
        // From (-5, 0) to (5, 0) about (0, 0), clockwise in Y-up coordinates: over the top.
        var image = Read(Layer("%ADD10C,0.2*%", "D10*", "G75*", "X-5000000Y0D02*", "G02*", "X5000000Y0I5000000J0D01*"));
        AssertBounds(Bounds(image), -5.1, -0.1, 5.1, 5.1);
    }

    [Fact]
    public void A_single_quadrant_arc_picks_the_centre_that_turns_at_most_90_degrees()
    {
        // Unsigned offsets: of the four candidate centres, only (0, -4) gives a clockwise turn of at most 90° from
        // (-2.5, 0) to (2.5, 0) (in tenths of an inch), so the arc bulges up over the chord.
        var image = Read("%FSLAX34Y34*%\n%MOIN*%\n%ADD10C,0.01*%\nD10*\nG74*\nX-2500Y0D02*\nG02*\nX2500Y0I2500J4000D01*\nM02*");
        var b = Bounds(image);
        var radius = Math.Sqrt(2.5 * 2.5 + 4 * 4) * 25.4 / 10;
        Assert.Equal(-4 * 2.54 + radius + 0.127, b.MaxY, 2); // the top of the arc, above the chord
        Assert.True(b.MinY > -0.2);
    }

    [Fact]
    public void A_region_with_arcs_is_filled_and_its_contours_point_one_way()
    {
        // A square drawn clockwise with a round end, and a second contour inside it drawn the same way.
        var image = Read(Layer("%ADD10C,0.1*%", "G75*", "G36*", "X0Y0D02*", "G01*", "X0Y10000000D01*", "X10000000Y10000000D01*", "X10000000Y0D01*",
            "G02*", "X0Y0I-5000000J0D01*", "X20000000Y0D02*", "G01*", "X20000000Y1000000D01*", "X21000000Y1000000D01*", "X21000000Y0D01*", "X20000000Y0D01*", "G37*"));
        var region = Assert.IsType<GerberRegion>(Assert.Single(image.Objects));
        Assert.Equal(2, region.Contours.Count);
        Assert.All(region.Contours, c => Assert.True(c.SignedArea() > 0));
        AssertBounds(region.Bounds, 0, -5, 21, 10);
    }

    // ───────────────────────── Polarity ─────────────────────────

    [Fact]
    public void A_clear_object_cuts_a_real_hole_without_a_mask()
    {
        var gerber = Layer("%ADD10R,10X10*%", "%ADD11C,4*%", "D10*", "X0Y0D03*", "%LPC*%", "D11*", "X0Y0D03*");
        var svg = Svg(gerber);
        Assert.DoesNotContain("<mask", svg);
        Assert.DoesNotContain(GerberSvg.Background + "\" stroke", svg); // not painted over in the background colour
        var background = PixelAt(svg, 0.5, 0.5);
        Assert.Equal(background, PixelAt(svg, 7, 7)); // the centre: 2 mm margin + 5 mm
        Assert.NotEqual(background, PixelAt(svg, 3, 3));
    }

    [Fact]
    public void Only_objects_drawn_before_a_clear_object_lose_their_part_under_it()
    {
        var gerber = Layer("%ADD10R,10X10*%", "%ADD11C,4*%", "%ADD12C,1*%", "D10*", "X0Y0D03*", "%LPC*%", "D11*", "X0Y0D03*", "%LPD*%", "D12*", "X0Y0D03*");
        var svg = Svg(gerber);
        Assert.NotEqual(PixelAt(svg, 0.5, 0.5), PixelAt(svg, 7, 7)); // the small dot drawn afterwards
        Assert.Equal(PixelAt(svg, 0.5, 0.5), PixelAt(svg, 7, 8.5));
    }

    [Fact]
    public void A_clear_object_that_is_drawn_over_again_cuts_nothing()
    {
        // The second square covers the hole exactly: no cut-out at all, so no seam along its edge.
        var svg = Svg(Layer("%ADD10R,10X10*%", "%ADD11R,4X4*%", "D10*", "X0Y0D03*", "%LPC*%", "D11*", "X0Y0D03*", "%LPD*%", "X0Y0D03*"));
        Assert.Equal(2, svg.Split("<use ").Length - 1);
        Assert.DoesNotContain("<path", svg);
    }

    [Fact]
    public void A_negative_image_is_dark_where_nothing_is_drawn()
    {
        var svg = Svg("%FSLAX46Y46*%\n%MOMM*%\n%IPNEG*%\n%ADD10C,1*%\n%ADD11C,4*%\nD11*\nX0Y0D03*\nX10000000Y0D03*\nD10*\nX5000000Y0D03*\nM02*");
        var background = PixelAt(svg, 0.5, 0.5);
        Assert.Equal(background, PixelAt(svg, 4, 4)); // first pad: now a hole
        Assert.Equal(background, PixelAt(svg, 9, 4)); // the dot between them is a hole too
        Assert.NotEqual(background, PixelAt(svg, 7.5, 4)); // between them: dark
    }

    // ───────────────────────── Macros ─────────────────────────

    [Fact]
    public void A_macro_evaluates_variables_and_expressions_and_rotates_about_its_origin()
    {
        // $3 = $1 x 2 = 4: a centre line 4 × $2 = 1, moved right by $1 = 2 and turned 90° about the origin.
        var image = Read(Layer("%AMBAR*$3=$1x2*21,1,$3,$2,$1,0,90*%", "%ADD10BAR,2X1*%", "D10*", "X0Y0D03*"));
        AssertBounds(Bounds(image), -0.5, 0, 0.5, 4);
    }

    [Fact]
    public void Exposure_off_in_a_macro_opens_a_hole_in_its_own_shape()
    {
        var gerber = Layer("%AMRING*1,1,6,0,0*1,0,2,0,0*%", "%ADD10RING*%", "D10*", "X0Y0D03*");
        var svg = Svg(gerber);
        var background = PixelAt(svg, 0.5, 0.5);
        Assert.Equal(background, PixelAt(svg, 5, 5)); // the hole in the middle
        Assert.NotEqual(background, PixelAt(svg, 5, 3)); // the ring
    }

    [Fact]
    public void Outline_polygon_moire_and_thermal_primitives_are_drawn()
    {
        var image = Read(Layer(
            "%AMSHAPES*4,1,3,0,0,1,0,0,1,0,0,0*5,1,6,10,0,2,0*6,20,0,4,0.5,0.5,2,0.1,5,0*7,30,0,4,2,0.5,0*%",
            "%ADD10SHAPES*%", "D10*", "X0Y0D03*"));
        // The thermal's right edge is cut by its 0.5 mm gap on the X axis.
        AssertBounds(Bounds(image), 0, -2.5, 30 + Math.Sqrt(4 - 0.0625), 2.5);
    }

    [Theory]
    [InlineData("%AMBAD*21,1,(1,1,0,0,0*%")]
    [InlineData("%AMBAD*99,1,1*%")]
    [InlineData("%AMBAD*4,1,5,0,0,1,1*%")]
    public void A_malformed_macro_fails_with_a_reason(string macro)
    {
        var e = Assert.Throws<InvalidDataException>(() => Read(Layer(macro, "%ADD10BAD*%", "D10*", "X0Y0D03*")));
        Assert.Contains("macro BAD", e.Message);
    }

    [Fact]
    public void Deeply_nested_macro_expressions_are_refused()
    {
        var expression = new string('(', 200) + "1" + new string(')', 200);
        Assert.Throws<InvalidDataException>(() => Read(Layer($"%AMDEEP*1,1,{expression},0,0*%", "%ADD10DEEP*%", "D10*", "X0Y0D03*")));
    }

    // ───────────────────────── Placement, blocks, step and repeat ─────────────────────────

    [Fact]
    public void Mirroring_rotation_and_scaling_apply_to_flashed_apertures()
    {
        var image = Read(Layer("%ADD10R,4X2*%", "%LMX*%", "%LR90*%", "%LS0.5*%", "D10*", "X0Y0D03*"));
        AssertBounds(Bounds(image), -0.5, -1, 0.5, 1);
        Assert.Contains("translate(0 0) scale(0.5) rotate(90) scale(-1 1)", Svg(Layer("%ADD10R,4X2*%", "%LMX*%", "%LR90*%", "%LS0.5*%", "D10*", "X0Y0D03*")));
    }

    [Fact]
    public void Scaling_widens_draws_too()
    {
        var image = Read(Layer("%ADD10C,1*%", "%LS2*%", "D10*", "X0Y0D02*", "X10000000Y0D01*"));
        Assert.Equal(2, Assert.IsType<GerberStroke>(Assert.Single(image.Objects)).Width);
    }

    [Fact]
    public void Step_and_repeat_places_the_block_once_per_copy_by_reference()
    {
        var gerber = Layer("%ADD10C,1*%", "%SRX3Y2I10J5*%", "D10*", "X0Y0D03*", "%SR*%");
        var image = Read(gerber);
        Assert.Equal(6, image.Objects.Count);
        AssertBounds(Bounds(image), -0.5, -0.5, 20.5, 5.5);
        var svg = Svg(gerber);
        Assert.Equal(6, svg.Split("xlink:href=\"#b0r0\"").Length - 1);
    }

    [Fact]
    public void Nested_aperture_blocks_are_flashed_with_their_polarity()
    {
        var gerber = Layer("%ADD10C,1*%", "%ADD11C,0.4*%", "%ABD20*%", "D10*", "X0Y0D03*", "%LPC*%", "D11*", "X0Y0D03*", "%LPD*%", "%AB*%",
            "%ABD21*%", "D20*", "X0Y0D03*", "X3000000Y0D03*", "%AB*%", "D21*", "X10000000Y0D03*");
        var image = Read(gerber);
        AssertBounds(Bounds(image), 9.5, -0.5, 13.5, 0.5);
        var svg = Svg(gerber);
        Assert.Equal(PixelAt(svg, 0.5, 0.5), PixelAt(svg, 2.5, 2.5)); // the hole of the first copy
        Assert.NotEqual(PixelAt(svg, 0.5, 0.5), PixelAt(svg, 2.5 - 0.4, 2.5));
    }

    // ───────────────────────── Layer colours ─────────────────────────

    [Theory]
    [InlineData("Copper,L1,Top", "a.gbr", "#C83434")]
    [InlineData("Copper,L4,Bot", "a.gbr", "#4D7FC4")]
    [InlineData("Copper,L2,Inr", "a.gbr", "#7FC87F")]
    [InlineData("Legend,Top", "a.gbr", "#F2EDA1")]
    [InlineData("Soldermask,Bot", "a.gbr", "#02FFEE")]
    [InlineData("Profile,NP", "a.gbr", "#D0D2CD")]
    [InlineData(null, "board-B_Cu.gbr", "#4D7FC4")]
    [InlineData(null, "board-F.SilkS.gto", "#F2EDA1")]
    [InlineData(null, "board-In2_Cu.gbr", "#CE7D2C")]
    [InlineData(null, "R-2R-Dessus.gtl", "#C83434")]
    [InlineData(null, "R-2R-Sérigr_Dessous.gbo", "#E8B2A7")]
    [InlineData(null, "board.GTS", "#D864FF")]
    [InlineData(null, "artwork.gbr", "#D0D2CD")]
    public void The_layer_colour_follows_its_file_function_or_name(string? function, string name, string color)
    {
        var (role, inner) = GerberSvg.RoleOf(function, name);
        Assert.Equal(color, GerberSvg.Color(role, inner));
    }

    [Fact]
    public void The_file_function_is_read_from_attributes_and_from_kicad_comments()
    {
        Assert.Equal("Copper,L1,Top", Read("%TF.FileFunction,Copper,L1,Top*%\n" + Layer("%ADD10C,1*%", "D10*", "X0Y0D03*")).FileFunction);
        Assert.Equal("Legend,Bot", Read("G04 #@! TF.FileFunction,Legend,Bot*\n" + Layer("%ADD10C,1*%", "D10*", "X0Y0D03*")).FileFunction);
        Assert.Contains("fill=\"#4D7FC4\"", Svg("%TF.FileFunction,Copper,L2,Bot*%\n" + Layer("%ADD10C,1*%", "D10*", "X0Y0D03*")));
    }

    // ───────────────────────── Damaged and crafted files ─────────────────────────

    [Fact]
    public void A_gimp_brush_with_the_same_extension_is_refused_with_a_reason()
    {
        var brush = new byte[64];
        brush[3] = 28;
        "GIMP"u8.CopyTo(brush.AsSpan(20));
        var e = Assert.Throws<InvalidDataException>(() => GerberReader.Read(brush, TestContext.Current.CancellationToken));
        Assert.Contains("GIMP", e.Message);
    }

    [Theory]
    [InlineData("hello world", "isn't a Gerber file")]
    [InlineData("%FSLAX46Y46*%\n%MOMM*%\nM02*", "no drawing")]
    [InlineData("%FSLAX46Y46*%\n%MOMM*%\nD10*\nX0Y0D03*", "RS-274-D")]
    [InlineData("%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,1*%\nD11*", "D11")]
    [InlineData("%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,1*%\nD10*\nX999999999999999Y0D03*", "out of range")]
    [InlineData("%FSLAX46Y46*%\n%MOMM*%\n%ADD10Q,1*%", "undefined aperture macro")]
    [InlineData("%FSLAX46Y46*%\n%MOMM*%\n%ADD10P,1X40*%", "vertices")]
    [InlineData("%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,1*%\n%SRX1000Y1000I1J1*%", "copies")]
    [InlineData("%FSLAX46Y46*%\n%MOMM*%\n%AB*%", "never opened")]
    [InlineData("%FSLAX46Y46*%\n%MOMM*%\n%ADD99999999999C,1*%", "out of range")]
    [InlineData("%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,1*%\n%SRX99999999999Y1I1J1*%", "out of range")]
    [InlineData("%FSLAX46Y46*%\n%MOMM*%\n%AMBIG*4,1,2147483647,0,0*%\n%ADD10BIG*%", "too many points")]
    [InlineData("%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,1*%\n%LPC*%\nD10*\nX0Y0D03*", "only clear")]
    public void A_damaged_file_fails_with_a_reason(string gerber, string reason)
    {
        var e = Assert.Throws<InvalidDataException>(() => Svg(gerber));
        Assert.Contains(reason, e.Message);
    }

    [Fact]
    public void Blocks_nested_too_deep_are_refused()
    {
        var gerber = "%FSLAX46Y46*%\n%MOMM*%\n" + string.Concat(Enumerable.Range(10, 12).Select(i => $"%ABD{i}*%\n"));
        Assert.Contains("deep", Assert.Throws<InvalidDataException>(() => Read(gerber)).Message);
    }

    [Fact]
    public void A_chain_of_top_level_blocks_each_flashing_the_last_is_refused_by_depth()
    {
        // Two codes alternate: each new block flashes the previous one, so the reference depth grows by one every
        // block though nothing is lexically nested. Without a depth on the block, this would recurse without end.
        var gerber = new StringBuilder("%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,1*%\n%ABD11*%\nD10*\nX0Y0D03*\n%AB*%\n");
        var codes = new[] { 12, 11 };
        for (var i = 0; i < 20; i++)
            gerber.Append($"%ABD{codes[i % 2]}*%\nD{codes[(i + 1) % 2]}*\nX0Y0D03*\n%AB*%\n");
        Assert.Contains("deep", Assert.Throws<InvalidDataException>(() => Read(gerber.ToString())).Message);
    }

    [Fact]
    public void Empty_opened_blocks_count_against_the_aperture_budget()
    {
        // 100 001 empty aperture blocks: no objects, but each is a definition.
        var gerber = "%FSLAX46Y46*%\n%MOMM*%\n" + string.Concat(Enumerable.Range(0, 100_001).Select(i => $"%ABD{10 + i % 2}*%\n%AB*%\n"));
        Assert.Contains("apertures and blocks", Assert.Throws<InvalidDataException>(() => Read(gerber)).Message);
    }

    [Theory]
    [InlineData("%ADD10C,1e308*%", "size out of range")]
    [InlineData("%AMBIG*1,1,99999999,0,0*%\n%ADD10BIG*%", "out of range")]
    [InlineData("%ADD10C,1*%\n%LS2000*%\nD10*\nX0Y0D03*", "aperture scale")]
    public void Oversized_aperture_and_macro_and_scale_numbers_are_refused(string body, string reason)
    {
        var e = Assert.Throws<InvalidDataException>(() => Read("%FSLAX46Y46*%\n%MOMM*%\n" + body + "\nM02*"));
        Assert.Contains(reason, e.Message);
    }

    [Fact]
    public void A_drawing_pushed_far_apart_by_step_and_repeat_is_refused_by_its_extent()
    {
        // Two flashes 20 km apart (the steps are in range, their total isn't a board).
        var gerber = "%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,1*%\n%SRX3Y1I6000J0*%\nD10*\nX0Y0D03*\n%SR*%\nM02*";
        Assert.Contains("larger than", Assert.Throws<InvalidDataException>(() => Svg(gerber)).Message);
    }

    [Fact]
    public void A_byte_order_mark_before_the_first_command_is_skipped()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.ASCII.GetBytes(Layer("%ADD10C,1*%", "D10*", "X0Y0D03*"))).ToArray();
        var image = GerberReader.Read(bytes, TestContext.Current.CancellationToken);
        Assert.Single(image.Objects);
    }

    [Fact]
    public void The_dxf_route_leaves_out_the_board_background()
    {
        var gerber = Layer("%ADD10C,1*%", "D10*", "X0Y0D03*");
        Assert.Contains(GerberSvg.Background, Svg(gerber));
        Assert.DoesNotContain("fill=\"" + GerberSvg.Background + "\"",
            GerberSvg.ToSvg(Encoding.ASCII.GetBytes(gerber), "layer.gbr", background: false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Blocks_flashed_inside_blocks_are_refused_before_they_multiply()
    {
        // Eight levels, each flashing the one below 100 times: 800 objects in the file, 100^8 dots to draw.
        var gerber = new StringBuilder("%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,1*%\n%ABD11*%\nD10*\nX0Y0D03*\n%AB*%\n");
        for (var level = 12; level < 19; level++)
        {
            gerber.Append($"%ABD{level}*%\nD{level - 1}*\n");
            for (var i = 0; i < 100; i++)
                gerber.Append($"X{i}000000Y0D03*\n");
            gerber.Append("%AB*%\n");
        }
        Assert.Contains("once its blocks", Assert.Throws<InvalidDataException>(() => Read(gerber.ToString())).Message);
    }

    [Fact]
    public void A_panel_of_a_large_board_is_not_refused_for_its_copies()
    {
        // 100 000 tracks stepped 20 × 10 times: 20 million segments drawn, 100 000 written.
        var gerber = new StringBuilder("%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,0.2*%\n%SRX20Y10I60J60*%\nD10*\n");
        for (var i = 0; i < 100_000; i++)
            gerber.Append($"X{i % 500 * 100000}Y{i / 500 * 250000}D02*\nX{i % 500 * 100000 + 50000}Y{i / 500 * 250000}D01*\n");
        gerber.Append("%SR*%\nM02*\n");
        Assert.Equal(200, Read(gerber.ToString()).Objects.Count);
    }

    [Fact]
    public void Aperture_macros_have_a_budget_over_all_apertures()
    {
        // Empty circles: quick to evaluate, but each counts.
        var macro = "%AMMANY*" + string.Concat(Enumerable.Repeat("1,1,0,0,0*", 10_000)) + "%\n";
        var definitions = string.Concat(Enumerable.Range(10, 60).Select(i => $"%ADD{i}MANY*%\n"));
        Assert.Contains("primitives in all", Assert.Throws<InvalidDataException>(() => Read("%FSLAX46Y46*%\n%MOMM*%\n" + macro + definitions)).Message);
    }

    [Fact]
    public void A_file_cut_off_keeps_what_was_read()
    {
        var gerber = Layer("%ADD10C,1*%", "D10*", "X0Y0D03*", "X5000000Y0D03*")[..^6] + "%ADD11C,";
        Assert.Equal(2, Read(gerber).Objects.Count);
    }

    // ───────────────────────── Conversions ─────────────────────────

    [Fact]
    public async Task A_layer_becomes_an_svg_a_png_and_a_vector_pdf()
    {
        var path = Path.Combine(fx.NewFolder(), "board-F_Cu.gbr");
        await File.WriteAllTextAsync(path, "%TF.FileFunction,Copper,L1,Top*%\n" + Layer("%ADD10R,10X10*%", "%ADD11C,4*%", "D10*", "X0Y0D03*", "%LPC*%", "D11*", "X0Y0D03*"),
            TestContext.Current.CancellationToken);
        Assert.Equal("gerber", FormatRegistry.Detect(path)?.Id);

        var svgJob = await fx.ConvertAsync([path], new Preset { TargetFormat = "svg" });
        Assert.True(svgJob.State == JobState.Completed, string.Join("; ", svgJob.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var svg = await File.ReadAllTextAsync(svgJob.Outputs.Single(), TestContext.Current.CancellationToken);
        Assert.Contains("width=\"14mm\"", svg); // 10 mm pad and 2 mm on each side
        Assert.Contains("fill=\"#C83434\"", svg);

        var pngJob = await fx.ConvertAsync([path], new Preset { TargetFormat = "png" });
        Assert.True(pngJob.State == JobState.Completed, string.Join("; ", pngJob.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        using (var png = new MagickImage(pngJob.Outputs.Single()))
            Assert.Equal(png.GetPixels().GetPixel(1, 1).ToColor(), png.GetPixels().GetPixel((int)png.Width / 2, (int)png.Height / 2).ToColor());

        var pdfJob = await fx.ConvertAsync([path], new Preset { TargetFormat = "pdf" });
        Assert.True(pdfJob.State == JobState.Completed, string.Join("; ", pdfJob.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var pdf = await File.ReadAllBytesAsync(pdfJob.Outputs.Single(), TestContext.Current.CancellationToken);
        Assert.Equal(-1, pdf.AsSpan().IndexOf("/Subtype /Image"u8)); // the hole is geometry, not a rasterised mask
    }

    [Fact]
    public async Task A_board_outline_becomes_dxf_geometry()
    {
        var path = Path.Combine(fx.NewFolder(), "board-Edge_Cuts.gm1");
        await File.WriteAllTextAsync(path, Layer("%ADD10C,0.1*%", "D10*", "X0Y0D02*", "X50000000Y0D01*", "X50000000Y30000000D01*", "X0Y30000000D01*", "X0Y0D01*"),
            TestContext.Current.CancellationToken);

        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = "dxf" });

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        Assert.Contains("LWPOLYLINE", await File.ReadAllTextAsync(job.Outputs.Single(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_gimp_brush_named_gbr_fails_the_job_with_a_reason()
    {
        var path = Path.Combine(fx.NewFolder(), "brush.gbr");
        await File.WriteAllBytesAsync(path, [0, 0, 0, 28, 0, 0, 0, 2, .. "GIMP"u8], TestContext.Current.CancellationToken);

        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = "png" });

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("GIMP", job.Files[0].ErrorDetail);
    }
}
