// Charts drawn in 3D (bar, line, area, pie, surface: c:view3D, walls) and the plot kinds of their own: radar, bubble,
// stock (high-low lines, up and down bars), surface (contour and wireframe), bar and pie of pie, and Office 2016 charts
// (chartex: waterfall, funnel, treemap, sunburst, histogram, Pareto, box and whisker), read and drawn, and drawn where
// they appear: on printed sheets, in Word documents and on slides, the chartex ones through mc:AlternateContent with
// the fallback Office saves drawn (and named) when Filee doesn't read the chart. Charts are built in code
// (ChartBuilder, ChartSamples); pictures are checked by how much of each colour they hold, not by where text lands.

using System.Diagnostics;
using System.Xml.Linq;
using Filee.Engines.Charts;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Office;
using Filee.Engines.Office.Sheets;
using Filee.Engines.Rendering;
using Filee.Engines.Slides;
using Filee.Engines.Typesetting;
using SkiaSharp;
using static Filee.Engines.Tests.ChartBuilder;

namespace Filee.Engines.Tests;

public sealed class ChartKindTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly SKColor Red = new(0xC0, 0x00, 0x00), Green = new(0x00, 0xB0, 0x50), Blue = new(0x00, 0x70, 0xC0);

    /// <summary>Office's first three accents (the automatic colours).</summary>
    private static readonly SKColor Accent1 = new(0x44, 0x72, 0xC4), Accent2 = new(0xED, 0x7D, 0x31), Accent3 = new(0xA5, 0xA5, 0xA5);

    private static ChartRead Read(string xml) => ChartReader.Read(XDocument.Parse(xml).Root, DrawingColors.FromTheme(null));

    private static Chart Drawn(string xml) => Read(xml).Chart ?? throw new InvalidOperationException("The chart isn't drawn.");

    private static (SKBitmap Bitmap, PageContent Page) Picture(string xml, float width = 480, float height = 300)
    {
        var page = new PageContent(width, height);
        using (var fonts = new ChartFonts())
            ChartDrawer.Draw(page, Drawn(xml), new SKRect(0, 0, width, height), fonts);
        return (page.Render(72), page);
    }

    /// <summary>Pixels within a few steps of <paramref name="color"/> in a band of rows and columns (fractions of the picture).</summary>
    private static int Count(SKBitmap bitmap, SKColor color, double left = 0, double right = 1, double top = 0, double bottom = 1)
    {
        var count = 0;
        for (var x = (int)(left * bitmap.Width); x < (int)(right * bitmap.Width); x++)
        {
            for (var y = (int)(top * bitmap.Height); y < (int)(bottom * bitmap.Height); y++)
            {
                var p = bitmap.GetPixel(x, y);
                if (Math.Abs(p.Red - color.Red) <= 12 && Math.Abs(p.Green - color.Green) <= 12 && Math.Abs(p.Blue - color.Blue) <= 12)
                    count++;
            }
        }
        return count;
    }

    /// <summary>Pixels of <paramref name="color"/>'s hue in any shade (3D faces are drawn lighter and darker).</summary>
    private static int Hue(SKColor bitmapColor, SKBitmap bitmap)
    {
        bitmapColor.ToHsv(out var hue, out _, out _);
        var count = 0;
        for (var x = 0; x < bitmap.Width; x++)
        {
            for (var y = 0; y < bitmap.Height; y++)
            {
                bitmap.GetPixel(x, y).ToHsv(out var h, out var saturation, out var value);
                var apart = Math.Abs(h - hue);
                if (Math.Min(apart, 360 - apart) < 10 && saturation > 40 && value > 25)
                    count++;
            }
        }
        return count;
    }

    private static SKColor Darker(SKColor color, float factor) => new((byte)(color.Red * factor), (byte)(color.Green * factor), (byte)(color.Blue * factor));

    private static string Text(PageContent page) => string.Concat(page.Items.OfType<GlyphItem>().Select(g => g.Run.Text));

    // ───────────────────────── 3D ─────────────────────────

    [Fact]
    public void A_3D_column_chart_is_read_with_its_view_and_drawn_as_boxes()
    {
        var chart = Drawn(ChartSamples.Columns3D(view: View3D(25, 30)));
        Assert.Equal(new ChartView3D(25, 30, 100, 100, true, 30), chart.View3D);
        var plot = Assert.Single(chart.Plots);
        Assert.True(plot is { Kind: ChartKind.Bar, ThreeD: true, SeriesInDepth: false });
        Assert.True(Drawn(ChartSamples.Columns3D("standard")).Plots[0].SeriesInDepth);

        var (bitmap, _) = Picture(ChartSamples.Columns3D());
        using (bitmap)
        {
            // Each box's front face in its colour and its side face (on the right, seen from the right) darker.
            Assert.True(Count(bitmap, Red) > 400 && Count(bitmap, Green) > 300 && Count(bitmap, Blue) > 200);
            Assert.True(Count(bitmap, Darker(Red, 0.78f)) > 60);
        }
    }

    [Theory]
    [InlineData("standard", "col")]
    [InlineData("stacked", "bar")]
    [InlineData("percentStacked", "col")]
    public void Bars_in_3D_of_every_grouping_are_drawn_in_their_colours(string grouping, string direction)
    {
        var (bitmap, _) = Picture(ChartSamples.Columns3D(grouping, direction));
        using (bitmap)
            Assert.True(Count(bitmap, Red) > 300 && Count(bitmap, Green) > 150 && Count(bitmap, Blue) > 100);
    }

    [Fact]
    public void A_3D_chart_with_walls_and_a_floor_fills_them_and_turned_round_still_draws()
    {
        var walls = "<c:floor><c:thickness val=\"0\"/>" + Solid("FFE699") + "</c:floor><c:sideWall><c:thickness val=\"0\"/>" + Solid("DDEBF7") + "</c:sideWall>" +
                    "<c:backWall><c:thickness val=\"0\"/>" + Solid("DDEBF7") + "</c:backWall>";
        var xml = ChartSamples.Columns3D().Replace("<c:plotArea>", walls + "<c:plotArea>", StringComparison.Ordinal);
        var chart = Drawn(xml);
        Assert.Equal(new SKColor(0xFF, 0xE6, 0x99), chart.Floor.Fill);
        var (bitmap, _) = Picture(xml);
        using (bitmap)
        {
            Assert.True(Count(bitmap, new SKColor(0xFF, 0xE6, 0x99)) > 200);
            Assert.True(Count(bitmap, new SKColor(0xDD, 0xEB, 0xF7)) > 1000);
        }
        // Seen from the left and from below.
        foreach (var view in new[] { View3D(-20, 330), View3D(10, 200), View3D(0, 0) })
        {
            var (turned, _) = Picture(ChartSamples.Columns3D(view: view));
            using (turned)
                Assert.True(Count(turned, Red) > 300);
        }
    }

    [Fact]
    public void Lines_areas_and_surfaces_in_3D_are_drawn_one_series_behind_the_other()
    {
        foreach (var xml in new[] { ChartSamples.Lines3D(), ChartSamples.Areas3D(), ChartSamples.Areas3D("stacked") })
        {
            var (bitmap, page) = Picture(xml);
            using (bitmap)
                Assert.True(Hue(Red, bitmap) > 200 && Hue(Blue, bitmap) > 50, xml[..80]);
            Assert.Contains("Q4", Text(page));
        }
        var line = Drawn(ChartSamples.Lines3D()).Plots[0];
        Assert.True(line is { Kind: ChartKind.Line, ThreeD: true, SeriesInDepth: true });
        // The series axis names the rows along the floor.
        var (surface, surfacePage) = Picture(ChartSamples.Surface(threeD: true));
        using (surface)
            Assert.True(Count(surface, Accent2) > 300 && Count(surface, Accent3) > 300);
        Assert.Contains("Row 5", Text(surfacePage));
    }

    [Fact]
    public void A_3D_pie_is_tilted_with_its_side_below_the_slices()
    {
        var chart = Drawn(ChartSamples.Pie3D());
        Assert.True(chart.Plots[0] is { Kind: ChartKind.Pie, ThreeD: true });
        Assert.Equal(30, chart.View3D!.RotX);
        var (bitmap, _) = Picture(ChartSamples.Pie3D());
        using (bitmap)
        {
            Assert.True(Count(bitmap, Accent1) > 1000 && Count(bitmap, Accent2) > 1000);
            // The sides of the front slices, darker, and the ellipse flatter than a circle.
            Assert.True(Count(bitmap, Darker(Accent2, 0.78f)) > 200);
            var rows = Enumerable.Range(0, bitmap.Height).Count(y => Enumerable.Range(0, bitmap.Width).Any(x => Near(bitmap.GetPixel(x, y), Accent1)));
            var columns = Enumerable.Range(0, bitmap.Width).Count(x => Enumerable.Range(0, bitmap.Height).Any(y => Near(bitmap.GetPixel(x, y), Accent1)));
            Assert.True(rows < columns, $"{rows} rows, {columns} columns");
        }
    }

    private static bool Near(SKColor p, SKColor c) => Math.Abs(p.Red - c.Red) <= 12 && Math.Abs(p.Green - c.Green) <= 12 && Math.Abs(p.Blue - c.Blue) <= 12;

    // ───────────────────────── Radar, bubble, stock, surface, pie of pie ─────────────────────────

    [Theory]
    [InlineData("marker")]
    [InlineData("standard")]
    [InlineData("filled")]
    public void A_radar_chart_draws_a_spoke_per_category_and_its_series(string style)
    {
        var plot = Assert.Single(Drawn(ChartSamples.Radar(style)).Plots);
        Assert.Equal(ChartKind.Radar, plot.Kind);
        Assert.Equal(style == "filled", plot.Filled);
        Assert.Equal(style == "marker", plot.Markers);
        var (bitmap, page) = Picture(ChartSamples.Radar(style));
        using (bitmap)
            Assert.True(Count(bitmap, Accent1) > (style == "filled" ? 2000 : 150) && Count(bitmap, Accent2) > (style == "filled" ? 2000 : 150));
        var text = Text(page);
        Assert.Contains("Speed", text);
        Assert.Contains("Comfort", text);
    }

    [Fact]
    public void Bubbles_are_as_large_as_their_size_says()
    {
        var series = Drawn(ChartSamples.Bubbles()).Plots[0].Series[0];
        Assert.Equal([5.0, 20, 10, 40], series.BubbleSizes!.Select(v => v!.Value));

        // One bubble four times the size of another covers four times its area (or twice its width).
        static string Two(string extra) => Chart("<c:bubbleChart><c:varyColors val=\"0\"/>" +
            BubbleSeries(0, "Small", [1], [5], [1], Solid("C00000")) + BubbleSeries(1, "Large", [3], [5], [4], Solid("0070C0")) + extra +
            "<c:axId val=\"1\"/><c:axId val=\"2\"/></c:bubbleChart>", Axes(scatter: true), legend: "");
        var (area, _) = Picture(Two(""));
        using (area)
            Assert.InRange(Count(area, Blue) / (double)Count(area, Red), 3.4, 4.6);
        var (width, _) = Picture(Two("<c:sizeRepresents val=\"w\"/>"));
        using (width)
            Assert.InRange(Count(width, Blue) / (double)Count(width, Red), 13, 19);

        var (shaded, _) = Picture(ChartSamples.Bubbles(threeD: true));
        using (shaded)
            Assert.True(Count(shaded, new SKColor(0xAB, 0xBF, 0xE4)) > 0 && Count(shaded, Accent1) < 1000);
    }

    [Fact]
    public void A_stock_chart_draws_high_low_lines_and_up_and_down_bars()
    {
        var plot = Assert.Single(Drawn(ChartSamples.Stock()).Plots);
        Assert.NotNull(plot.HighLowLines);
        Assert.Equal(150, plot.UpDownBars!.GapWidth);
        Assert.False(plot.Markers);
        var (bitmap, _) = Picture(ChartSamples.Stock());
        using (bitmap)
        {
            // Two down days (close below open) in dark grey; the up days white with a black outline, over black lines.
            Assert.True(Count(bitmap, new SKColor(0x40, 0x40, 0x40)) > 800);
            Assert.True(Count(bitmap, SKColors.Black) > 150);
        }
        var (hlc, _) = Picture(ChartSamples.Stock(open: false));
        using (hlc)
            Assert.True(Count(hlc, SKColors.Black) > 100);
    }

    [Fact]
    public void A_surface_is_coloured_by_value_band_with_the_bands_in_the_legend()
    {
        var chart = Drawn(ChartSamples.Surface(threeD: false));
        Assert.True(chart.Plots[0] is { Kind: ChartKind.Surface, ThreeD: false });
        var (bitmap, page) = Picture(ChartSamples.Surface(threeD: false));
        using (bitmap)
        {
            foreach (var band in new[] { Accent1, Accent2, Accent3, new SKColor(0xFF, 0xC0, 0x00) })
                Assert.True(Count(bitmap, band) > 100, band.ToString());
        }
        var text = Text(page);
        Assert.Contains("0-20", text);
        Assert.Contains("80-100", text);
        Assert.Contains("Row 3", text);

        // A band's own fill (c:bandFmt) and a wireframe.
        var own = ChartSamples.Surface(threeD: false).Replace("<c:wireframe val=\"0\"/>",
            "<c:wireframe val=\"0\"/>", StringComparison.Ordinal).Replace("<c:axId val=\"1\"/><c:axId val=\"2\"/><c:axId val=\"3\"/></c:surfaceChart>",
            "<c:bandFmts><c:bandFmt><c:idx val=\"1\"/>" + Solid("C00000") + "</c:bandFmt></c:bandFmts><c:axId val=\"1\"/><c:axId val=\"2\"/><c:axId val=\"3\"/></c:surfaceChart>", StringComparison.Ordinal);
        var (banded, _) = Picture(own);
        using (banded)
            Assert.True(Count(banded, Red) > 300 && Count(banded, Accent2) < 50);
        var (wires, _) = Picture(ChartSamples.Surface(threeD: false, wireframe: true));
        using (wires)
            Assert.True(Count(wires, Accent2) is > 50 and < 3000);
    }

    [Fact]
    public void A_pie_of_pie_sends_the_split_points_to_its_second_plot()
    {
        var plot = Drawn(ChartSamples.OfPie(bar: false)).Plots[0];
        Assert.True(plot is { Kind: ChartKind.OfPie, OfPieBar: false, SplitType: "pos", SplitPosition: 3, SecondPieSize: 75 });
        Assert.NotNull(plot.SeriesLines);
        var series = plot.Series[0];
        Assert.Equal([3, 4, 5], ChartDrawer.SecondPlot(plot, series).Order());
        ChartPlot With(string type, double? at, params int[] custom) => new()
        {
            Kind = ChartKind.OfPie,
            SplitType = type,
            SplitPosition = at,
            CustomSplit = custom.ToHashSet(),
        };
        Assert.Equal([3, 4, 5], ChartDrawer.SecondPlot(With("val", 10), series).Order());
        Assert.Equal([4, 5], ChartDrawer.SecondPlot(With("percent", 5.5), series).Order());
        Assert.Equal([0, 2], ChartDrawer.SecondPlot(With("cust", null, 0, 2, 99), series).Order());

        foreach (var bar in new[] { false, true })
        {
            var (bitmap, page) = Picture(ChartSamples.OfPie(bar));
            using (bitmap)
            {
                // The points of the second plot (accents 4 to 6) on the right, the first pie's on the left.
                Assert.True(Count(bitmap, Accent1, 0, 0.55) > 2000 && Count(bitmap, Accent1, 0.6) == 0);
                Assert.True(Count(bitmap, new SKColor(0xFF, 0xC0, 0x00), 0.6) > 500 && Count(bitmap, new SKColor(0x70, 0xAD, 0x47), 0.6) > 300);
            }
            Assert.Contains("Other", Text(page));
        }
    }

    // ───────────────────────── Office 2016 charts (chartex) ─────────────────────────

    [Fact]
    public void A_waterfall_floats_each_column_from_the_running_total()
    {
        var chart = Drawn(ChartSamples.Waterfall());
        var plot = Assert.Single(chart.Plots);
        Assert.Equal(ChartKind.Waterfall, plot.Kind);
        Assert.Equal([0, 4, 6], plot.Subtotals.Order());
        Assert.Equal("Waterfall", Assert.Single(chart.Title!.Lines));
        Assert.Equal("t", chart.Legend!.Position);
        var (bitmap, page) = Picture(ChartSamples.Waterfall());
        using (bitmap)
            Assert.True(Count(bitmap, Accent1) > 500 && Count(bitmap, Accent2) > 500 && Count(bitmap, Accent3) > 1500);
        var text = Text(page);
        Assert.Contains("Increase", text);
        Assert.Contains("-15", text);
        Assert.Contains("Subtotal", text);
    }

    [Fact]
    public void A_funnel_puts_its_first_and_widest_stage_at_the_top()
    {
        var (bitmap, page) = Picture(ChartSamples.Funnel());
        using (bitmap)
            Assert.True(Count(bitmap, Accent1, top: 0, bottom: 0.4) > 3 * Count(bitmap, Accent1, top: 0.6, bottom: 1));
        Assert.Contains("Visits", Text(page));
    }

    [Theory]
    [InlineData("overlapping")]
    [InlineData("banner")]
    [InlineData("none")]
    public void A_treemap_and_a_sunburst_lay_out_the_hierarchy_by_value(string parents)
    {
        foreach (var xml in new[] { ChartSamples.Treemap(parents), ChartSamples.Sunburst() })
        {
            var chart = Drawn(xml);
            var series = Assert.Single(Assert.Single(chart.Plots).Series);
            Assert.Equal("Apples", series.Categories![0]);
            Assert.Equal("Fruit", Assert.Single(series.CategoryLevels!)[0]);
            var (bitmap, page) = Picture(xml);
            using (bitmap)
            {
                // Fruit (60), vegetables (45) and bakery (27) in the first three accents, by their share.
                var (fruit, vegetables, bakery) = (Count(bitmap, Accent1), Count(bitmap, Accent2), Count(bitmap, Accent3));
                Assert.True(fruit > vegetables && vegetables > bakery && bakery > 500, $"{fruit} {vegetables} {bakery}");
            }
            Assert.Contains("Apples", Text(page));
        }
        Assert.Equal(parents, Drawn(ChartSamples.Treemap(parents)).Plots[0].ParentLabels);
    }

    [Fact]
    public void A_histogram_counts_its_values_into_bins_as_Excel_does()
    {
        Chart Read(string binning) => Drawn(ChartSamples.Histogram(binning));
        var bins = Assert.Single(Read("<cx:binning intervalClosed=\"r\"><cx:binCount val=\"5\"/></cx:binning>").Plots).Series[0];
        Assert.Equal([6.0, 9, 3, 1, 1], bins.Values.Select(v => v!.Value));
        Assert.Equal("[12, 20]", bins.Categories![0]);
        Assert.Equal("(20, 28]", bins.Categories[1]);

        var width = Read("<cx:binning intervalClosed=\"l\"><cx:binSize val=\"10\"/></cx:binning>").Plots[0].Series[0];
        Assert.Equal(["[12, 22)", "[22, 32)", "[32, 42)", "[42, 52]"], width.Categories!);
        Assert.Equal([7.0, 9, 3, 1], width.Values.Select(v => v!.Value));

        var limits = Read("<cx:binning intervalClosed=\"r\" underflow=\"15\" overflow=\"40\"><cx:binCount val=\"5\"/></cx:binning>").Plots[0].Series[0];
        Assert.Equal("≤15", limits.Categories![0]);
        Assert.Equal(">40", limits.Categories[^1]);
        Assert.Equal(20, limits.Values.Sum(v => v!.Value));
        Assert.Equal(2, limits.Values[0]);
        Assert.Equal(2, limits.Values[^1]);

        // Automatic bins (Scott's rule) count every value.
        Assert.Equal(20, Read("<cx:binning intervalClosed=\"r\"/>").Plots[0].Series[0].Values.Sum(v => v!.Value));
    }

    [Fact]
    public void A_Pareto_chart_adds_up_its_categories_largest_first_with_the_running_share()
    {
        var chart = Drawn(ChartSamples.Pareto());
        Assert.Equal(2, chart.Plots.Count);
        var columns = chart.Plots[0].Series[0];
        Assert.Equal(["Late", "Broken", "Wrong size", "Missing"], columns.Categories!);
        Assert.Equal([27.0, 12, 5, 3], columns.Values.Select(v => v!.Value));
        var line = chart.Plots[1].Series[0];
        Assert.Equal(1, line.Values[^1]!.Value, 9);
        Assert.Equal(27 / 47.0, line.Values[0]!.Value, 9);
        var percent = chart.Axis(chart.Plots[1].AxisIds[1])!;
        Assert.True(percent is { Position: "r", Min: 0, Max: 1, Format: "0%" });
        var (bitmap, page) = Picture(ChartSamples.Pareto());
        using (bitmap)
            Assert.True(Count(bitmap, Accent1) > 2000 && Count(bitmap, Accent2) > 50);
        Assert.Contains("100%", Text(page));
    }

    [Fact]
    public void Box_and_whisker_quartiles_follow_Excel()
    {
        double[] values = [1, 3, 4, 5, 6, 7, 20];
        Assert.Equal(3, ChartDrawer.Quantile(values, 0.25, exclusive: true), 9);
        Assert.Equal(3.5, ChartDrawer.Quantile(values, 0.25, exclusive: false), 9);
        Assert.Equal(5, ChartDrawer.Quantile(values, 0.5, exclusive: true), 9);
        Assert.Equal(7, ChartDrawer.Quantile(values, 0.75, exclusive: true), 9);
        Assert.Equal(6.5, ChartDrawer.Quantile(values, 0.75, exclusive: false), 9);

        var plot = Assert.Single(Drawn(ChartSamples.BoxWhisker()).Plots);
        Assert.True(plot is { Kind: ChartKind.BoxWhisker, ExclusiveQuartiles: true, MeanLine: true, MeanMarker: true, Outliers: true, InnerPoints: false });
        var (bitmap, page) = Picture(ChartSamples.BoxWhisker());
        using (bitmap)
            Assert.True(Count(bitmap, Accent1) > 1000 && Count(bitmap, Accent2) > 1000);
        var text = Text(page);
        Assert.Contains("A", text);
        Assert.Contains("Class 2", text);
    }

    [Fact]
    public void A_region_map_and_a_broken_chartex_part_are_not_read()
    {
        Assert.Equal(new ChartRead(null, "charts"), Read(ChartSamples.RegionMap()));
        Assert.Equal(new ChartRead(null, "charts"), Read($"<cx:chartSpace xmlns:cx=\"{ChartExNamespace}\"><cx:chart/></cx:chartSpace>"));
        Assert.Equal(new ChartRead(null, "charts"), Read(ChartEx(ExData(0, [], [1]), ExSeries("waterfall", 0, "A") + ExSeries("funnel", 0, "B"))));
    }

    // ───────────────────────── Trendline legend names, label layouts, leader lines, wrapped levels ─────────────────────────

    [Fact]
    public void Trendlines_are_named_in_the_legend_and_their_label_moves_where_the_file_says()
    {
        string[] q = ["Q1", "Q2", "Q3", "Q4"];
        var series = Series(0, "North", q, [40, 55, 70, 62], Solid("C00000") + Trendline("linear", "<c:dispEq val=\"1\"/>") +
                                                              Trendline("movingAvg", "<c:period val=\"3\"/>", "00B050")) +
                     Series(1, "South", q, [30, 45, 35, 50], Solid("0070C0") + Trendline("poly", "<c:name>Curve</c:name><c:order val=\"2\"/>"));
        var (bitmap, page) = Picture(Chart(Bars(series), Axes(), "Trends"));
        bitmap.Dispose();
        var text = Text(page);
        Assert.Contains("Linear (North)", text);
        Assert.Contains("3 per. Mov. Avg. (North)", text);
        Assert.Contains("Curve", text);
        Assert.DoesNotContain("Poly.", text);

        var moved = Drawn(Chart(Bars(Series(0, "North", q, [40, 55, 70, 62], Trendline("linear",
            "<c:dispEq val=\"1\"/><c:trendlineLbl><c:layout><c:manualLayout><c:x val=\"-0.2\"/><c:y val=\"0.1\"/></c:manualLayout></c:layout></c:trendlineLbl>"))), Axes()));
        Assert.Equal((-0.2, 0.1), moved.Plots[0].Series[0].Trendlines[0].LabelOffset);
    }

    [Fact]
    public void A_moved_pie_label_is_joined_to_its_slice_by_a_leader_line()
    {
        static string Pie(bool leaders) => Chart(ChartBuilder.Pie(Series(0, "Budget", ["Rent", "Food"], [70, 30],
            "<c:dLbls><c:dLbl><c:idx val=\"1\"/><c:layout><c:manualLayout><c:x val=\"0.25\"/><c:y val=\"0.2\"/></c:manualLayout></c:layout>" +
            "<c:showLegendKey val=\"0\"/><c:showVal val=\"1\"/><c:showCatName val=\"0\"/><c:showSerName val=\"0\"/><c:showPercent val=\"0\"/><c:showBubbleSize val=\"0\"/></c:dLbl>" +
            $"<c:showLegendKey val=\"0\"/><c:showVal val=\"1\"/><c:showCatName val=\"0\"/><c:showSerName val=\"0\"/><c:showPercent val=\"0\"/><c:showBubbleSize val=\"0\"/><c:showLeaderLines val=\"{(leaders ? 1 : 0)}\"/></c:dLbls>")), legend: "");
        var labels = Drawn(Pie(true)).Plots[0].Series[0].PointLabels[1];
        Assert.Equal((0.25, 0.2), labels.Offset);
        Assert.True(labels.LeaderLines);
        var (with, _) = Picture(Pie(true));
        var (without, _) = Picture(Pie(false));
        using (with)
        using (without)
        {
            // The line is all that differs.
            var changed = 0;
            for (var x = 0; x < with.Width; x++)
            {
                for (var y = 0; y < with.Height; y++)
                    changed += with.GetPixel(x, y) != without.GetPixel(x, y) ? 1 : 0;
            }
            Assert.InRange(changed, 40, 2000);
        }
    }

    [Fact]
    public void Outer_category_labels_wrap_within_their_group()
    {
        string[] inner = ["Q1", "Q2", "Q1", "Q2"];
        var levels = MultiLevel(inner, [(0, "The first financial year"), (2, "The second financial year")]);
        var xml = Chart(Bars(Series(0, "North", [], [40, 55, 70, 62], Solid("C00000") + levels)), Axes());
        var (bitmap, page) = Picture(xml, 200, 300);
        bitmap.Dispose();
        var runs = page.Items.OfType<GlyphItem>().Select(g => g.Run.Text).ToList();
        Assert.DoesNotContain("The first financial year", runs);
        Assert.Contains("year", string.Concat(runs));
    }

    // ───────────────────────── Hostile input ─────────────────────────

    [Fact]
    public void New_kinds_with_extreme_numbers_and_many_points_are_drawn_in_time()
    {
        var many = Enumerable.Range(0, 20_000).Select(i => (double?)(i % 7 == 0 ? 1e100 : i % 5 == 0 ? -1e100 : i)).ToArray();
        var names = Enumerable.Range(0, many.Length).Select(i => $"C{i % 300}").ToArray();
        string[] charts =
        [
            Chart3D($"<c:bar3DChart><c:barDir val=\"col\"/><c:grouping val=\"standard\"/>{Series(0, "A", names, many)}{Series(1, "B", names, many)}<c:axId val=\"1\"/><c:axId val=\"2\"/><c:axId val=\"3\"/></c:bar3DChart>", Axes() + SeriesAxis()),
            Chart3D($"<c:surface3DChart>{string.Concat(Enumerable.Range(0, 20).Select(i => Series(i, $"S{i}", names[..1000], many[..1000])))}<c:axId val=\"1\"/><c:axId val=\"2\"/><c:axId val=\"3\"/></c:surface3DChart>", Axes() + SeriesAxis()),
            Chart($"<c:surfaceChart>{string.Concat(Enumerable.Range(0, 20).Select(i => Series(i, $"S{i}", names[..1000], many[..1000])))}<c:axId val=\"1\"/><c:axId val=\"2\"/><c:axId val=\"3\"/></c:surfaceChart>", Axes()),
            Chart($"<c:radarChart><c:radarStyle val=\"filled\"/>{Series(0, "A", names, many)}<c:axId val=\"1\"/><c:axId val=\"2\"/></c:radarChart>", Axes()),
            Chart($"<c:bubbleChart>{BubbleSeries(0, "A", [.. many.Select((_, i) => (double)i)], [.. many.Select(v => v!.Value)], [.. many.Select(v => v!.Value)])}<c:showNegBubbles val=\"1\"/><c:axId val=\"1\"/><c:axId val=\"2\"/></c:bubbleChart>", Axes(scatter: true)),
            Chart($"<c:stockChart>{Series(0, "O", names, many)}{Series(1, "C", names, many)}<c:hiLowLines/><c:upDownBars/><c:axId val=\"1\"/><c:axId val=\"2\"/></c:stockChart>", Axes()),
            Chart($"<c:ofPieChart><c:ofPieType val=\"bar\"/>{Series(0, "A", names, many)}<c:splitType val=\"percent\"/><c:serLines/></c:ofPieChart>"),
            Chart3D($"<c:pie3DChart>{Series(0, "A", names, many)}</c:pie3DChart>"),
            ChartEx(ExData(0, [names, [.. names.Select(n => n[..2])]], many, "size"), ExSeries("treemap", 0, "T")),
            ChartEx(ExData(0, [names, [.. names.Select(n => n[..2])]], many, "size"), ExSeries("sunburst", 0, "T")),
            ChartEx(ExData(0, [names], many), ExSeries("waterfall", 0, "W"), ExAxes()),
            ChartEx(ExData(0, [names], many), ExSeries("boxWhisker", 0, "B"), ExAxes()),
            ChartEx(ExData(0, [], many), ExSeries("clusteredColumn", 0, "H").Replace("<cx:dataId val=\"0\"/>", "<cx:dataId val=\"0\"/><cx:layoutPr><cx:binning><cx:binSize val=\"1e-300\"/></cx:binning></cx:layoutPr>", StringComparison.Ordinal), ExAxes(0)),
        ];
        foreach (var xml in charts)
        {
            var timer = Stopwatch.StartNew();
            var (bitmap, _) = Picture(xml);
            bitmap.Dispose();
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(20), $"{timer.Elapsed} for {xml[..120]}");
        }
        var bins = Drawn(charts[^1]).Plots[0].Series[0];
        Assert.InRange(bins.Values.Count, 1, ChartReader.MaxBins);
    }

    [Fact]
    public void A_chartex_cache_that_claims_more_points_than_Filee_draws_is_refused()
    {
        var xml = ChartEx(ExData(0, [], [1]).Replace("ptCount=\"1\"", $"ptCount=\"{ChartReader.MaxPoints + 1}\"", StringComparison.Ordinal), ExSeries("funnel", 0, "F"));
        Assert.Equal(DocumentLimits.ErrorKey, Assert.Throws<Core.Conversion.ConversionRefusedException>(() => Read(xml)).ErrorKey);
    }

    // ───────────────────────── Hosts: chartex through mc:AlternateContent ─────────────────────────

    private const string Xdr = "xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
                               "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\"";

    private const string ChartExUri = "http://schemas.microsoft.com/office/drawing/2014/chartex";

    /// <summary>Excel's anchor of a chartex chart: the chart as the choice, a shape saying it can't be shown as the fallback.</summary>
    private static string SheetChartEx() =>
        "<mc:AlternateContent><mc:Choice Requires=\"cx1\" xmlns:cx1=\"http://schemas.microsoft.com/office/drawing/2015/9/8/chartex\">" +
        "<xdr:twoCellAnchor><xdr:from><xdr:col>1</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>1</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from>" +
        "<xdr:to><xdr:col>8</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>19</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:to>" +
        "<xdr:graphicFrame macro=\"\"><xdr:nvGraphicFramePr><xdr:cNvPr id=\"2\" name=\"Chart 1\"/><xdr:cNvGraphicFramePr/></xdr:nvGraphicFramePr>" +
        $"<xdr:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/></xdr:xfrm><a:graphic><a:graphicData uri=\"{ChartExUri}\">" +
        $"<cx:chart xmlns:cx=\"{ChartExUri}\" r:id=\"rIdC\"/></a:graphicData></a:graphic></xdr:graphicFrame><xdr:clientData/></xdr:twoCellAnchor></mc:Choice>" +
        "<mc:Fallback><xdr:twoCellAnchor><xdr:from><xdr:col>1</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>1</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from>" +
        "<xdr:to><xdr:col>8</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>19</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:to>" +
        "<xdr:sp macro=\"\" textlink=\"\"><xdr:nvSpPr><xdr:cNvPr id=\"3\" name=\"Fallback\"/><xdr:cNvSpPr/></xdr:nvSpPr><xdr:spPr><a:prstGeom prst=\"rect\"/></xdr:spPr>" +
        "<xdr:txBody><a:bodyPr/><a:p><a:r><a:t>This chart isn't available in your version of Excel.</a:t></a:r></a:p></xdr:txBody></xdr:sp><xdr:clientData/></xdr:twoCellAnchor></mc:Fallback></mc:AlternateContent>";

    private string Workbook(string chart)
    {
        var book = new XlsxBuilder();
        book.Sheet("Data", XlsxBuilder.Row(1, book.Text("A1", "Sales")), after: "<pageSetup paperSize=\"9\" orientation=\"portrait\"/><drawing r:id=\"rIdD\"/>");
        book.SheetRelationships[1] = [("rIdD", "drawing", "../drawings/drawing1.xml")];
        book.Parts["xl/drawings/drawing1.xml"] = $"<xdr:wsDr {Xdr}>{SheetChartEx()}</xdr:wsDr>";
        book.Parts["xl/drawings/_rels/drawing1.xml.rels"] = OpcWriter.Relationships([("rIdC", "chart", "../charts/chartEx1.xml")]);
        book.Parts["xl/charts/chartEx1.xml"] = chart;
        return book.Save(Path.Combine(fx.NewFolder(), "book.xlsx"));
    }

    [Fact]
    public void A_workbook_prints_its_chartex_chart_and_names_one_it_doesnt_read()
    {
        var path = Workbook(ChartSamples.Waterfall());
        Assert.Null(SheetCapabilities.FirstUnsupported(path, "xlsx"));
        var chart = Assert.IsType<SheetChart>(Assert.Single(XlsxReader.Read(path).Sheets[0].Drawings));
        Assert.Equal(ChartKind.Waterfall, chart.Chart.Plots[0].Kind);

        // A region map: Excel's fallback shape instead, and the chart named.
        Assert.Equal("charts", SheetCapabilities.FirstUnsupported(Workbook(ChartSamples.RegionMap()), "xlsx"));
    }

    private string WordWithChartEx(string chart)
    {
        var docx = new DocxBuilder();
        var id = docx.Chart(chart);
        var picture = docx.Image(SlideDeckBuilder.TestPicture(120, 80));
        long cx = 432 * 12700, cy = 252 * 12700;
        var choice = $"<w:drawing><wp:inline><wp:extent cx=\"{cx}\" cy=\"{cy}\"/><wp:docPr id=\"3\" name=\"Chart 1\"/><a:graphic><a:graphicData uri=\"{ChartExUri}\">" +
                     $"<cx:chart xmlns:cx=\"{ChartExUri}\" r:id=\"{id}\"/></a:graphicData></a:graphic></wp:inline></w:drawing>";
        var fallback = $"<w:drawing><wp:inline><wp:extent cx=\"{cx}\" cy=\"{cy}\"/><wp:docPr id=\"4\" name=\"Chart 1\"/><a:graphic><a:graphicData uri=\"{DocxBuilder.PictureUri}\">" +
                       DocxBuilder.Picture(picture, cx, cy) + "</a:graphicData></a:graphic></wp:inline></w:drawing>";
        docx.Paragraph(DocxBuilder.P(DocxBuilder.R("Before the chart.")));
        docx.Paragraph(DocxBuilder.P($"<w:r><mc:AlternateContent><mc:Choice Requires=\"cx1\" xmlns:cx1=\"http://schemas.microsoft.com/office/drawing/2015/9/8/chartex\">{choice}</mc:Choice><mc:Fallback>{fallback}</mc:Fallback></mc:AlternateContent></w:r>"));
        return docx.Save(Path.Combine(fx.NewFolder(), "chart.docx"));
    }

    [Fact]
    public void A_Word_chartex_chart_is_drawn_and_one_Filee_doesnt_read_takes_its_fallback_picture()
    {
        var path = WordWithChartEx(ChartSamples.Treemap());
        Assert.Null(new FileeRendererConverter().FirstUnsupported(path, "docx"));
        var document = DocxReader.Read(path, Path.Combine(fx.NewFolder(), "media"));
        Assert.True(document.LeftOut.IsEmpty);
        var image = document.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HImage>().Single();
        Assert.Equal(ChartKind.Treemap, image.Chart?.Plots[0].Kind);

        var map = DocxReader.Read(WordWithChartEx(ChartSamples.RegionMap()), Path.Combine(fx.NewFolder(), "media"));
        Assert.Contains(map.LeftOut.Kinds, k => k.Kind == HLeftOutKind.ChartPictured);
        Assert.Null(map.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HImage>().Single().Chart);
    }

    private string DeckWithChartEx(string chart)
    {
        var deck = new SlideDeckBuilder();
        var picture = deck.Media(SlideDeckBuilder.TestPicture(120, 80));
        deck.Part("ppt/charts/chartEx1.xml", chart, "drawingml.chartex");
        var frame = "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"4\" name=\"Chart 3\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>" +
                    $"<p:xfrm><a:off x=\"{SlideDeckBuilder.E(100)}\" y=\"{SlideDeckBuilder.E(60)}\"/><a:ext cx=\"{SlideDeckBuilder.E(600)}\" cy=\"{SlideDeckBuilder.E(380)}\"/></p:xfrm>" +
                    $"<a:graphic><a:graphicData uri=\"{ChartExUri}\"><cx:chart xmlns:cx=\"{ChartExUri}\" r:id=\"rIdC\"/></a:graphicData></a:graphic></p:graphicFrame>";
        var shapes = $"<mc:AlternateContent><mc:Choice Requires=\"cx1\" xmlns:cx1=\"http://schemas.microsoft.com/office/drawing/2015/9/8/chartex\">{frame}</mc:Choice>" +
                     $"<mc:Fallback>{deck.Picture("rIdP", 100, 60, 600, 380)}</mc:Fallback></mc:AlternateContent>";
        deck.Slide(shapes, pictures: new Dictionary<string, string> { ["rIdP"] = picture }, relationships: [("rIdC", "chart", "../charts/chartEx1.xml")]);
        return deck.Save(Path.Combine(fx.NewFolder(), "deck.pptx"));
    }

    [Fact]
    public void A_slide_chartex_chart_is_drawn_and_one_Filee_doesnt_read_takes_its_fallback_picture()
    {
        var path = DeckWithChartEx(ChartSamples.Sunburst());
        Assert.Null(new FileeRendererConverter().FirstUnsupported(path, "pptx"));
        var deck = PptxSlideReader.Read(path, cancellationToken: TestContext.Current.CancellationToken);
        var shape = Assert.Single(deck.Slides[0].Elements.OfType<SlideShape>(), s => s.Chart is not null);
        Assert.Equal(ChartKind.Sunburst, shape.Chart!.Plots[0].Kind);

        var map = DeckWithChartEx(ChartSamples.RegionMap());
        Assert.Null(new FileeRendererConverter().FirstUnsupported(map, "pptx"));
        var pictured = PptxSlideReader.Read(map, cancellationToken: TestContext.Current.CancellationToken, leftOut: true);
        Assert.DoesNotContain(pictured.Slides[0].Elements.OfType<SlideShape>(), s => s.Chart is not null);
        Assert.NotEmpty(pictured.Slides[0].Elements);
        Assert.Contains(pictured.LeftOut, p => p.Key.EndsWith("charts_pictured", StringComparison.Ordinal));
    }
}
