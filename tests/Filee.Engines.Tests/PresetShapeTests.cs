// DrawingML's preset shapes (Slides/PresetShapes*.cs): every ST_ShapeType name is drawn, at any size and adjust value,
// and a few of them put their key points where the standard's guides say.

using Filee.Engines.Slides;
using SkiaSharp;

namespace Filee.Engines.Tests;

public sealed class PresetShapeTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"filee-presets-{Guid.NewGuid():N}");

    public PresetShapeTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>ECMA-376 Part 1, 20.1.10.56 ST_ShapeType: all 187 names.</summary>
    private static readonly string[] ShapeTypes =
    [
        "line", "lineInv", "triangle", "rtTriangle", "rect", "diamond", "parallelogram", "trapezoid", "nonIsoscelesTrapezoid", "pentagon",
        "hexagon", "heptagon", "octagon", "decagon", "dodecagon", "star4", "star5", "star6", "star7", "star8", "star10", "star12", "star16",
        "star24", "star32", "roundRect", "round1Rect", "round2SameRect", "round2DiagRect", "snipRoundRect", "snip1Rect", "snip2SameRect",
        "snip2DiagRect", "plaque", "ellipse", "teardrop", "homePlate", "chevron", "pieWedge", "pie", "blockArc", "donut", "noSmoking",
        "rightArrow", "leftArrow", "upArrow", "downArrow", "stripedRightArrow", "notchedRightArrow", "bentUpArrow", "leftRightArrow",
        "upDownArrow", "leftUpArrow", "leftRightUpArrow", "quadArrow", "leftArrowCallout", "rightArrowCallout", "upArrowCallout",
        "downArrowCallout", "leftRightArrowCallout", "upDownArrowCallout", "quadArrowCallout", "bentArrow", "uturnArrow", "circularArrow",
        "leftCircularArrow", "leftRightCircularArrow", "curvedRightArrow", "curvedLeftArrow", "curvedUpArrow", "curvedDownArrow",
        "swooshArrow", "cube", "can", "lightningBolt", "heart", "sun", "moon", "smileyFace", "irregularSeal1", "irregularSeal2",
        "foldedCorner", "bevel", "frame", "halfFrame", "corner", "diagStripe", "chord", "arc", "leftBracket", "rightBracket", "leftBrace",
        "rightBrace", "bracketPair", "bracePair", "straightConnector1", "bentConnector2", "bentConnector3", "bentConnector4",
        "bentConnector5", "curvedConnector2", "curvedConnector3", "curvedConnector4", "curvedConnector5", "callout1", "callout2",
        "callout3", "accentCallout1", "accentCallout2", "accentCallout3", "borderCallout1", "borderCallout2", "borderCallout3",
        "accentBorderCallout1", "accentBorderCallout2", "accentBorderCallout3", "wedgeRectCallout", "wedgeRoundRectCallout",
        "wedgeEllipseCallout", "cloudCallout", "cloud", "ribbon", "ribbon2", "ellipseRibbon", "ellipseRibbon2", "leftRightRibbon",
        "verticalScroll", "horizontalScroll", "wave", "doubleWave", "plus", "flowChartProcess", "flowChartDecision",
        "flowChartInputOutput", "flowChartPredefinedProcess", "flowChartInternalStorage", "flowChartDocument", "flowChartMultidocument",
        "flowChartTerminator", "flowChartPreparation", "flowChartManualInput", "flowChartManualOperation", "flowChartConnector",
        "flowChartPunchedCard", "flowChartPunchedTape", "flowChartSummingJunction", "flowChartOr", "flowChartCollate", "flowChartSort",
        "flowChartExtract", "flowChartMerge", "flowChartOfflineStorage", "flowChartOnlineStorage", "flowChartMagneticTape",
        "flowChartMagneticDisk", "flowChartMagneticDrum", "flowChartDisplay", "flowChartDelay", "flowChartAlternateProcess",
        "flowChartOffpageConnector", "actionButtonBlank", "actionButtonHome", "actionButtonHelp", "actionButtonInformation",
        "actionButtonForwardNext", "actionButtonBackPrevious", "actionButtonEnd", "actionButtonBeginning", "actionButtonReturn",
        "actionButtonDocument", "actionButtonSound", "actionButtonMovie", "gear6", "gear9", "funnel", "mathPlus", "mathMinus",
        "mathMultiply", "mathDivide", "mathEqual", "mathNotEqual", "cornerTabs", "squareTabs", "plaqueTabs", "chartX", "chartStar",
        "chartPlus",
    ];

    private static GeometryResult Draw(string name, double width, double height, Dictionary<string, string>? adjust = null) =>
        ShapeGeometry.Evaluate(PresetShapes.Find(name)!, width, height, adjust, 1);

    private static Dictionary<string, string> Adjust(params (string Name, double Value)[] values) =>
        values.ToDictionary(v => v.Name, v => FormattableString.Invariant($"val {v.Value}"));

    [Fact]
    public void Every_shape_type_of_the_standard_is_drawn()
    {
        Assert.Equal(187, ShapeTypes.Distinct().Count());
        Assert.All(ShapeTypes, name => Assert.True(PresetShapes.Known(name), name));
        Assert.Empty(PresetShapes.Names.Except(ShapeTypes));
    }

    [Fact]
    public void Every_preset_draws_at_any_size_and_adjust_value()
    {
        (double, double)[] sizes = [(1_270_000, 762_000), (500_000, 2_000_000), (12_700, 12_700), (1, 3), (0, 0)];
        foreach (var name in ShapeTypes)
        {
            var definition = PresetShapes.Find(name)!;
            var adjusts = new List<Dictionary<string, string>?> { null };
            foreach (var value in new[] { 0, -50_000, 30_000, 250_000, 30_000_000 })
                adjusts.Add(definition.Adjust.ToDictionary(a => a.Name, _ => FormattableString.Invariant($"val {value}")));
            foreach (var (width, height) in sizes)
            {
                foreach (var adjust in adjusts)
                {
                    var result = ShapeGeometry.Evaluate(definition, width, height, adjust, 1 / 12700.0);
                    Assert.NotEmpty(result.Paths);
                    foreach (var path in result.Paths)
                    {
                        var bounds = path.Path.Bounds;
                        Assert.True(float.IsFinite(bounds.Left) && float.IsFinite(bounds.Top) && float.IsFinite(bounds.Right) && float.IsFinite(bounds.Bottom),
                            $"{name} {width}×{height}: {bounds}");
                    }
                    Assert.True(result.TextRect is { } text && float.IsFinite(text.Width) && float.IsFinite(text.Height), name);
                }
            }
        }
    }

    [Theory]
    [InlineData("borderCallout1")]
    [InlineData("accentCallout2")]
    [InlineData("callout3")]
    public void A_callout_leader_ends_where_its_adjust_values_put_it(string name)
    {
        // adj1/adj2 are the first point's y and x, the last pair the leader's end, in 100,000ths of the shape's size.
        var definition = PresetShapes.Find(name)!;
        var adjust = definition.Adjust.ToDictionary(a => a.Name, a => a.Formula);
        var last = definition.Adjust.Count;
        adjust[$"adj{last - 1}"] = "val 130000";
        adjust[$"adj{last}"] = "val 75000";
        adjust["adj1"] = "val 40000";
        adjust["adj2"] = "val -20000";
        var result = ShapeGeometry.Evaluate(definition, 200, 100, adjust, 1);

        var leader = result.Paths[^1].Path;
        Assert.Equal(PathFill.None, result.Paths[^1].Fill);
        Assert.Equal(new SKPoint(-40, 40), leader.Points[0]);
        Assert.Equal(new SKPoint(150, 130), leader.LastPoint);
        // The box is the whole shape.
        Assert.Equal(new SKRect(0, 0, 200, 100), result.Paths[0].Path.Bounds);
        Assert.Equal(name.StartsWith("border", StringComparison.Ordinal), result.Paths[0].Stroke);
        if (name.StartsWith("accent", StringComparison.Ordinal))
            Assert.Equal(new SKRect(-40, 0, -40, 100), result.Paths[1].Path.Bounds);
    }

    [Fact]
    public void A_cloud_callout_puts_its_smallest_bubble_on_the_tail()
    {
        var result = Draw("cloudCallout", 216, 108, Adjust(("adj1", -40000), ("adj2", 90000)));
        // The tail's point: the centre moved by adj1 of the width and adj2 of the height.
        var tail = new SKPoint(108 - 0.4f * 216, 54 + 0.9f * 108);
        var bubbles = result.Paths[1].Path;
        Assert.Equal(PathFill.Normal, result.Paths[1].Fill);
        Assert.True(bubbles.Contains(tail.X, tail.Y));
        Assert.False(result.Paths[0].Path.Contains(tail.X, tail.Y));
        // The smallest bubble is 600/21,600 of the shorter side around it.
        var radius = 108 * 600 / 21600f;
        Assert.True(bubbles.Contains(tail.X + radius * 0.9f, tail.Y));
        Assert.False(bubbles.Contains(tail.X + radius * 1.1f, tail.Y));
    }

    [Fact]
    public void The_cloud_closes_on_itself_however_it_is_stretched()
    {
        foreach (var (width, height) in new[] { (432.0, 432.0), (864.0, 216.0), (100.0, 400.0) })
        {
            var outline = Draw("cloud", width, height).Paths[0].Path;
            using var iterator = outline.CreateRawIterator();
            var points = new SKPoint[4];
            SKPoint start = default, end = default;
            SKPathVerb verb;
            while ((verb = iterator.Next(points)) != SKPathVerb.Done)
            {
                if (verb == SKPathVerb.Move)
                    start = points[0];
                else if (verb == SKPathVerb.Conic || verb == SKPathVerb.Quad)
                    end = points[2];
                else if (verb == SKPathVerb.Cubic)
                    end = points[3];
                else if (verb == SKPathVerb.Line)
                    end = points[1];
            }
            // The eleventh arc ends where the first began (to well under a percent of the size).
            Assert.True(SKPoint.Distance(start, end) < 0.005 * Math.Max(width, height), $"{width}×{height}: {start} → {end}");
            var bounds = outline.TightBounds;
            Assert.InRange(bounds.Left, -0.01 * width, 0.02 * width);
            Assert.InRange(bounds.Right, 0.98 * width, 1.01 * width);
            Assert.InRange(bounds.Top, -0.01 * height, 0.02 * height);
            Assert.InRange(bounds.Bottom, 0.98 * height, 1.01 * height);
        }
    }

    [Theory]
    [InlineData("actionButtonHome")]
    [InlineData("actionButtonHelp")]
    [InlineData("actionButtonInformation")]
    [InlineData("actionButtonForwardNext")]
    [InlineData("actionButtonBackPrevious")]
    [InlineData("actionButtonEnd")]
    [InlineData("actionButtonBeginning")]
    [InlineData("actionButtonReturn")]
    [InlineData("actionButtonDocument")]
    [InlineData("actionButtonSound")]
    [InlineData("actionButtonMovie")]
    public void An_action_button_draws_its_icon_inside_the_middle_of_the_button(string name)
    {
        foreach (var (width, height) in new[] { (300.0, 200.0), (120.0, 320.0) })
        {
            var result = Draw(name, width, height);
            Assert.Equal(new SKRect(0, 0, (float)width, (float)height), result.Paths[0].Path.Bounds);
            // The icon: 3/4 of the shorter side, centred.
            var half = 0.375f * (float)Math.Min(width, height) + 0.01f;
            var icon = new SKRect((float)width / 2 - half, (float)height / 2 - half, (float)width / 2 + half, (float)height / 2 + half);
            var parts = result.Paths.Skip(1).SkipLast(1).ToList();
            Assert.Contains(parts, p => p.Fill is PathFill.Darken or PathFill.DarkenLess);
            foreach (var part in parts)
            {
                var bounds = part.Path.TightBounds;
                Assert.True(icon.Contains(bounds) && bounds.Width > 0 && bounds.Height > 0, $"{name}: {bounds} outside {icon}");
            }
        }
        Assert.Single(Draw("actionButtonBlank", 100, 100).Paths);
    }

    private static int Figures(SKPath path)
    {
        using var iterator = path.CreateRawIterator();
        var points = new SKPoint[4];
        var count = 0;
        SKPathVerb verb;
        while ((verb = iterator.Next(points)) != SKPathVerb.Done)
            count += verb == SKPathVerb.Move ? 1 : 0;
        return count;
    }

    [Fact]
    public void The_sun_has_eight_rays_around_its_disc()
    {
        var sun = Assert.Single(Draw("sun", 200, 200).Paths).Path;
        Assert.Equal(9, Figures(sun));
        // Rays point at the middles of the sides and at the corners' diagonals; the disc (adj 25,000: radius 1/4) sits apart from them.
        Assert.True(sun.Contains(199, 100) && sun.Contains(100, 1) && sun.Contains(1, 100) && sun.Contains(100, 199));
        Assert.True(sun.Contains(165, 35) && sun.Contains(35, 165));
        Assert.True(sun.Contains(100, 100) && sun.Contains(100, 70));
        Assert.False(sun.Contains(100, 45));
        // A smaller disc (adj 40,000: radius 1/10) with the rays' bases following it in.
        var small = Draw("sun", 200, 200, Adjust(("adj", 40000))).Paths[0].Path;
        Assert.False(small.Contains(100, 70));
        Assert.True(small.Contains(100, 85) && small.Contains(100, 55));
    }

    [Theory]
    [InlineData("gear6", 6)]
    [InlineData("gear9", 9)]
    public void A_gear_has_its_teeth_on_the_box(string name, int teeth)
    {
        var gear = Assert.Single(Draw(name, 300, 300).Paths).Path;
        Assert.True(gear.Contains(150, 150));
        // Walking round just inside the box's circle crosses into a tooth and out again once per tooth.
        var inside = 0;
        var previous = false;
        for (var i = 0; i < 3600; i++)
        {
            var angle = i * Math.PI / 1800;
            var now = gear.Contains((float)(150 + 148 * Math.Cos(angle)), (float)(150 + 148 * Math.Sin(angle)));
            inside += now && !previous ? 1 : 0;
            previous = now;
        }
        Assert.Equal(teeth, inside);
        // One tooth points straight up.
        Assert.True(gear.Contains(150, 3));
    }

    [Fact]
    public void Mirrored_presets_are_their_twins_turned_over()
    {
        var right = Draw("curvedRightArrow", 100, 300).Paths[0].Path;
        var left = Draw("curvedLeftArrow", 100, 300).Paths[0].Path;
        var down = Draw("curvedDownArrow", 300, 100).Paths[0].Path;
        var up = Draw("curvedUpArrow", 300, 100).Paths[0].Path;
        for (var x = 2; x < 100; x += 7)
        {
            for (var y = 2; y < 300; y += 7)
            {
                var inRight = right.Contains(x, y);
                Assert.Equal(inRight, left.Contains(100 - x, y));
                Assert.Equal(inRight, down.Contains(y, x));
                Assert.Equal(inRight, up.Contains(y, 100 - x));
            }
        }
        // The right arrow's head is on the right, at the bottom.
        Assert.True(right.Contains(90, 270));
        Assert.False(right.Contains(97, 40));
    }

    [Fact]
    public void A_block_arc_follows_its_angles_and_thickness()
    {
        // From 180° clockwise to 0°: the upper half, adj3 (25,000) of the shorter side thick.
        var arch = Draw("blockArc", 200, 200);
        var path = arch.Paths[0].Path;
        Assert.True(path.Contains(100, 10) && path.Contains(10, 98));
        Assert.False(path.Contains(100, 100) || path.Contains(100, 190) || path.Contains(100, 60));
        Assert.Equal(new SKRect(0, 0, 200, 100), arch.TextRect!.Value, new RectComparer());
        // From 90° to 180°: the lower left quarter.
        var quarter = Draw("blockArc", 200, 200, Adjust(("adj1", 5_400_000), ("adj2", 10_800_000))).Paths[0].Path;
        Assert.True(quarter.Contains(30, 160));
        Assert.False(quarter.Contains(170, 160) || quarter.Contains(30, 40));
    }

    [Fact]
    public void A_circular_arrow_ends_in_its_head_at_the_adjusted_angle()
    {
        // Defaults: from 180° clockwise over the top to 340.96°, the head's tip at 360° (the right, on the band's middle).
        var arrow = Draw("circularArrow", 200, 200).Paths[0].Path;
        Assert.True(arrow.Contains(100, 20));
        Assert.False(arrow.Contains(100, 180) || arrow.Contains(100, 100));
        var tip = arrow.TightBounds;
        Assert.InRange(tip.Right, 180, 200.01);
        // Turned to end at 90° (the bottom): the band goes on round the right, the head points left at the bottom.
        var turned = Draw("circularArrow", 200, 200, Adjust(("adj3", 5_400_000))).Paths[0].Path;
        Assert.True(turned.Contains(100, 20) && turned.Contains(180, 100) && turned.Contains(100, 180));
        Assert.False(turned.Contains(25, 110));
    }

    [Fact]
    public void The_slide_check_no_longer_names_the_new_presets()
    {
        var deck = new SlideDeckBuilder();
        var shapes = string.Concat(new[] { "cloud", "borderCallout2", "actionButtonHome", "gear9", "curvedUpArrow", "ribbon2", "sun", "chartPlus" }
            .Select((name, i) => deck.Shape(name, 20 + i * 100, 50, 90, 60)));
        deck.Slide(shapes);
        var path = deck.Save(Path.Combine(_folder, "presets.pptx"));
        Assert.Null(SlideCapabilities.FirstUnsupported(path, TestContext.Current.CancellationToken));

        // The reader hands the shape's own adjust values to the geometry.
        var callout = new SlideDeckBuilder();
        callout.Slide(callout.Shape("<a:prstGeom prst=\"borderCallout1\"><a:avLst><a:gd name=\"adj4\" fmla=\"val 50000\"/><a:gd name=\"adj3\" fmla=\"val 150000\"/></a:avLst></a:prstGeom>",
            100, 100, 200, 100));
        var slide = Assert.Single(PptxSlideReader.Read(callout.Save(Path.Combine(_folder, "callout.pptx")), cancellationToken: TestContext.Current.CancellationToken).Slides);
        var shape = slide.Elements.OfType<SlideShape>().Single(s => s.Bounds.Left == 100 && s.Bounds.Width == 200);
        var end = shape.Paths[^1].Path.LastPoint;
        Assert.Equal(100, end.X, 0.01);
        Assert.Equal(150, end.Y, 0.01);
    }

    private sealed class RectComparer : IEqualityComparer<SKRect>
    {
        public bool Equals(SKRect a, SKRect b) =>
            Math.Abs(a.Left - b.Left) < 0.6 && Math.Abs(a.Top - b.Top) < 0.6 && Math.Abs(a.Right - b.Right) < 0.6 && Math.Abs(a.Bottom - b.Bottom) < 0.6;

        public int GetHashCode(SKRect rect) => 0;
    }
}
