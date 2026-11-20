// DrawingML charts (Charts/): the chart part read into the model (ChartReader), drawn as vectors with text as glyph
// runs (ChartDrawer), and drawn where they appear: on printed sheets (XlsxDrawing, SheetCapabilities), in Word
// documents (DocxReader, FloatingObjects, the renderer's check) and on slides (PptxSlideReader, SlideRenderer,
// SlideCapabilities), with trendlines, error bars, point labels, multi-level categories, rich titles, gradient and
// pattern fills and the chart style's colours. Charts are built in code (ChartBuilder); pictures are checked by how
// much of each series' colour they hold, not by where the fonts put the text, so Linux fonts don't move the goal posts.

using System.Diagnostics;
using System.Xml.Linq;
using Filee.Core.Conversion;
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

public sealed class ChartTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly SKColor Red = new(0xC0, 0x00, 0x00), Green = new(0x00, 0xB0, 0x50), Blue = new(0x00, 0x70, 0xC0);

    private static ChartRead Read(string xml) => ChartReader.Read(XDocument.Parse(xml).Root, DrawingColors.FromTheme(null));

    private static Chart Drawn(string xml) => Read(xml).Chart ?? throw new InvalidOperationException("The chart isn't drawn.");

    /// <summary>The chart drawn alone on a page of its size, and the page.</summary>
    private static (SKBitmap Bitmap, PageContent Page) Picture(string xml, float width = 480, float height = 300)
    {
        var page = new PageContent(width, height);
        using (var fonts = new ChartFonts())
            ChartDrawer.Draw(page, Drawn(xml), new SKRect(0, 0, width, height), fonts);
        return (page.Render(72), page);
    }

    /// <summary>Pixels within a few steps of <paramref name="color"/>, in columns <paramref name="from"/> to <paramref name="to"/> (fractions of the width).</summary>
    private static int Count(SKBitmap bitmap, SKColor color, double from = 0, double to = 1)
    {
        var count = 0;
        for (var x = (int)(from * bitmap.Width); x < (int)(to * bitmap.Width); x++)
        {
            for (var y = 0; y < bitmap.Height; y++)
            {
                var p = bitmap.GetPixel(x, y);
                if (Math.Abs(p.Red - color.Red) <= 12 && Math.Abs(p.Green - color.Green) <= 12 && Math.Abs(p.Blue - color.Blue) <= 12)
                    count++;
            }
        }
        return count;
    }

    private static string Text(PageContent page) =>
        string.Concat(page.Items.OfType<GlyphItem>().Select(g => g.Run.Text));

    // ───────────────────────── Reading ─────────────────────────

    [Fact]
    public void A_column_chart_is_read_from_its_cached_values()
    {
        var chart = Drawn(Columns());

        var plot = Assert.Single(chart.Plots);
        Assert.Equal((ChartKind.Bar, ChartGrouping.Standard, false), (plot.Kind, plot.Grouping, plot.Horizontal));
        Assert.Equal(["North", "South", "West"], plot.Series.Select(s => s.Name));
        Assert.Equal([40.0, 55, 70, 62], plot.Series[0].Values.Select(v => v!.Value));
        Assert.Equal(["Q1", "Q2", "Q3", "Q4"], plot.Series[0].Categories!);
        Assert.Equal(Red, plot.Series[0].Style.Fill);
        Assert.Equal(["Sales by quarter"], chart.Title!.Lines);
        Assert.Equal("r", chart.Legend!.Position);
        Assert.Equal([false, true], chart.Axes.Select(a => a.IsValue));
        Assert.Equal(["Units"], chart.Axes[1].Title!.Lines);
    }

    [Fact]
    public void Series_without_a_colour_take_the_theme_accents_in_Office_order()
    {
        var series = string.Concat(Enumerable.Range(0, 8).Select(i => Series(i, $"S{i}", ["a"], [i + 1])));
        var chart = Drawn(Chart(Bars(series), Axes()));

        Assert.Equal(new SKColor(0x44, 0x72, 0xC4), chart.Palette.Color(0));
        Assert.Equal(new SKColor(0xED, 0x7D, 0x31), chart.Palette.Color(1));
        Assert.Equal(new SKColor(0x70, 0xAD, 0x47), chart.Palette.Color(5));
        // The seventh is the first accent darker (60 % of its luminance).
        chart.Palette.Color(6).ToHsl(out _, out _, out var dark);
        chart.Palette.Color(0).ToHsl(out _, out _, out var light);
        Assert.InRange(dark / light, 0.55, 0.65);
    }

    [Theory]
    [InlineData("bar3DChart")]
    [InlineData("radarChart")]
    [InlineData("stockChart")]
    [InlineData("surfaceChart")]
    public void Other_chart_types_are_named_instead(string kind)
    {
        var xml = Chart($"<c:{kind}>{Series(0, "S", ["a"], [1])}<c:axId val=\"1\"/><c:axId val=\"2\"/></c:{kind}>", Axes());
        Assert.Equal(new ChartRead(null, "charts"), Read(xml));
    }

    [Fact]
    public void A_single_series_takes_its_name_as_title_until_the_title_is_deleted()
    {
        var xml = $"<c:chartSpace {Namespaces}><c:chart><c:plotArea>{Pie(Series(0, "Budget", ["a", "b"], [1, 2]))}</c:plotArea></c:chart></c:chartSpace>";
        Assert.Equal(["Budget"], Drawn(xml).Title!.Lines);
        Assert.Null(Drawn(xml.Replace("<c:chart>", "<c:chart><c:autoTitleDeleted val=\"1\"/>", StringComparison.Ordinal)).Title);
    }

    // ───────────────────────── Drawing ─────────────────────────

    [Fact]
    public void Columns_are_drawn_in_their_colours_as_high_as_their_values()
    {
        var (bitmap, page) = Picture(Columns());
        using (bitmap)
        {
            Assert.True(Count(bitmap, Red) > 500 && Count(bitmap, Green) > 500 && Count(bitmap, Blue) > 500);
            // North's Q3 (70) stands higher than its Q1 (40): more red in the third quarter of the plot than the first.
            var q1 = Count(bitmap, Red, 0.05, 0.3);
            var q3 = Count(bitmap, Red, 0.45, 0.7);
            Assert.InRange(q3 / (double)q1, 1.4, 2.1);
        }
        // Title, axis labels, axis titles and the legend are text in the page, not pixels.
        var text = Text(page);
        foreach (var expected in new[] { "Sales by quarter", "Q1", "Q4", "North", "West", "Units", "Quarter", "40" })
            Assert.Contains(expected, text);
    }

    private const string RedLine = "<c:spPr><a:ln w=\"50800\"><a:solidFill><a:srgbClr val=\"C00000\"/></a:solidFill></a:ln></c:spPr>";

    [Fact]
    public void Stacked_and_horizontal_bars_and_lines_areas_and_scatter_plots_are_drawn()
    {
        string[] q = ["Q1", "Q2", "Q3", "Q4"];
        foreach (var xml in new[]
        {
            Columns("stacked"), Columns("percentStacked"), Columns(direction: "bar"),
            Chart(Lines(Series(0, "A", q, [1, 3, null, 2], RedLine)), Axes()),
            Chart(Areas(Series(0, "A", q, [1, 3, 2, 2], Solid("C00000")), "stacked"), Axes()),
            Chart(Scatter(ScatterSeries(0, "A", [1, 2, 4], [3, 1, 2], RedLine)), Axes(scatter: true)),
        })
        {
            var (bitmap, _) = Picture(xml);
            using (bitmap)
                Assert.True(Count(bitmap, Red) > 100, xml[xml.IndexOf("<c:plotArea>", StringComparison.Ordinal)..][..60]);
        }
    }

    [Fact]
    public void Pie_slices_take_their_share_of_the_circle()
    {
        var (bitmap, page) = Picture(Slices("<c:dLbls><c:showVal val=\"0\"/><c:showPercent val=\"1\"/></c:dLbls>"));
        using (bitmap)
        {
            var shares = new[] { new SKColor(0xFF, 0, 0), new SKColor(0, 0xC0, 0), new SKColor(0, 0, 0xFF), new SKColor(0xFF, 0xC0, 0) }
                .Select(c => (double)Count(bitmap, c)).ToList();
            var total = shares.Sum();
            // 40 %, 30 %, 20 %, 10 % of the pie (labels and the legend's keys take a little).
            Assert.InRange(shares[0] / total, 0.36, 0.44);
            Assert.InRange(shares[3] / total, 0.07, 0.13);
        }
        var text = Text(page);
        Assert.Contains("40%", text);
        Assert.Contains("Travel", text);
    }

    [Fact]
    public void A_picture_of_a_chart_is_a_png()
    {
        var png = ChartPicture.Png(Drawn(Columns()), 240, 150, 96);
        using var bitmap = SKBitmap.Decode(png);
        Assert.Equal((320, 200), (bitmap.Width, bitmap.Height));
    }

    // ───────────────────────── Legend, trendlines, error bars, labels, levels, titles, fills ─────────────────────────

    /// <summary>The first glyph run whose text starts with <paramref name="text"/>.</summary>
    private static GlyphRun Glyphs(PageContent page, string text) =>
        page.Items.OfType<GlyphItem>().Select(g => g.Run).First(r => r.Text.StartsWith(text, StringComparison.Ordinal));

    /// <summary>Pixels of a kind (a hue more than an exact colour, as lines are antialiased).</summary>
    private static int Pixels(SKBitmap bitmap, Func<SKColor, bool> kind)
    {
        var n = 0;
        for (var x = 0; x < bitmap.Width; x++)
        {
            for (var y = 0; y < bitmap.Height; y++)
                n += kind(bitmap.GetPixel(x, y)) ? 1 : 0;
        }
        return n;
    }

    private static bool Magenta(SKColor p) => p.Red > 150 && p.Blue > 150 && p.Green < 110;

    private static bool Purple(SKColor p) => p.Red is > 70 and < 190 && p.Green < 110 && p.Blue > 110 && p.Blue > p.Red;

    /// <summary>The rightmost column left of <paramref name="x"/> with a pixel near <paramref name="color"/> within a few rows of <paramref name="y"/>.</summary>
    private static int RightmostBefore(SKBitmap bitmap, SKColor color, float x, float y)
    {
        for (var column = (int)x - 1; column >= 0; column--)
        {
            for (var row = Math.Max(0, (int)y - 6); row < Math.Min(bitmap.Height, (int)y + 2); row++)
            {
                var p = bitmap.GetPixel(column, row);
                if (Math.Abs(p.Red - color.Red) <= 30 && Math.Abs(p.Green - color.Green) <= 30 && Math.Abs(p.Blue - color.Blue) <= 30)
                    return column;
            }
        }
        return -1;
    }

    [Theory]
    [InlineData("b")]
    [InlineData("r")]
    public void A_bar_entry_text_follows_its_square_in_a_combination_legend(string position)
    {
        var (bitmap, page) = Picture(Combo(position));
        using (bitmap)
        {
            // Each entry's text starts a small gap after its own key: the bar's square, the line's stroke.
            foreach (var (name, color) in new[] { ("North", Red), ("South", Green), ("Target", Blue) })
            {
                var run = Glyphs(page, name);
                var start = run.Positions[0].X;
                var key = RightmostBefore(bitmap, color, start, run.Positions[0].Y);
                Assert.InRange(start - key, 1, 8);
            }
        }
    }

    [Fact]
    public void Trendlines_are_read_with_their_extrapolation_intercept_and_label()
    {
        string[] q = ["Q1", "Q2", "Q3", "Q4"];
        var xml = Chart(Bars(Series(0, "North", q, [40, 55, 70, 62], Solid("C00000") +
            Trendline("poly", "<c:order val=\"3\"/><c:forward val=\"1.5\"/><c:backward val=\"0.5\"/><c:intercept val=\"10\"/><c:dispRSqr val=\"1\"/><c:dispEq val=\"1\"/>") +
            Trendline("movingAvg", "<c:period val=\"3\"/>") + Trendline("exp") + Trendline("log") + Trendline("power") + Trendline("linear"))), Axes());
        var trends = Drawn(xml).Plots[0].Series[0].Trendlines;

        Assert.Equal([TrendKind.Polynomial, TrendKind.MovingAverage, TrendKind.Exponential, TrendKind.Logarithmic, TrendKind.Power, TrendKind.Linear], trends.Select(t => t.Kind));
        Assert.Equal((3, 1.5, 0.5, 10.0, true, true), (trends[0].Order, trends[0].Forward, trends[0].Backward, trends[0].Intercept, trends[0].ShowEquation, trends[0].ShowRSquared));
        Assert.Equal(3, trends[1].Period);
        Assert.Equal(new SKColor(0x70, 0x30, 0xA0), trends[0].Line!.Color);
    }

    [Fact]
    public void Trendlines_fit_lines_curves_and_moving_averages_as_Office_does()
    {
        static (double X, double Y)[] Points(Func<double, double> f) => [.. Enumerable.Range(1, 6).Select(i => ((double)i, f(i)))];
        static IReadOnlyList<double> Fit(TrendKind kind, Func<double, double> f, int order = 2, double? intercept = null) =>
            ChartTrend.Fit(new ChartTrendline(kind) { Order = order, Intercept = intercept }, Points(f))!.Coefficients;

        Assert.Equal([2.0, 1.0], Fit(TrendKind.Linear, x => 2 * x + 1).Select(c => Math.Round(c, 9)));
        Assert.Equal([0.0, 0.0, 1.0], Fit(TrendKind.Polynomial, x => x * x).Select(c => Math.Round(c, 9)));
        Assert.Equal([3.0, 0.5], Fit(TrendKind.Exponential, x => 3 * Math.Exp(0.5 * x)).Select(c => Math.Round(c, 9)));
        Assert.Equal([2.0, 1.0], Fit(TrendKind.Logarithmic, x => 2 * Math.Log(x) + 1).Select(c => Math.Round(c, 9)));
        Assert.Equal([2.0, 1.5], Fit(TrendKind.Power, x => 2 * Math.Pow(x, 1.5)).Select(c => Math.Round(c, 9)));
        // A forced intercept: the line through (0, 0) closest to y = x + 1.
        var forced = Fit(TrendKind.Linear, x => x + 1, intercept: 0);
        Assert.Equal(0, forced[1]);
        Assert.InRange(forced[0], 1.2, 1.3);

        var noisy = ChartTrend.Fit(new ChartTrendline(TrendKind.Linear), [(1, 1), (2, 3), (3, 2), (4, 5)])!;
        Assert.InRange(noisy.RSquared, 0.6, 0.7);
        Assert.Equal(1, ChartTrend.Fit(new ChartTrendline(TrendKind.Linear), Points(x => 3 * x))!.RSquared, 9);
        Assert.Equal([2.0, 3, 4], ChartTrend.MovingAverage([(1, 1), (2, 2), (3, 3), (4, 4), (5, 5)], 3).Select(p => p.Y));
        // Too few or unusable points: no fit.
        Assert.Null(ChartTrend.Fit(new ChartTrendline(TrendKind.Polynomial) { Order = 6 }, Points(x => x)[..3]));
        Assert.Null(ChartTrend.Fit(new ChartTrendline(TrendKind.Exponential), [(1, -1), (2, -2)]));

        // The label: "y = 2x + 1" and "R² = 1", the powers raised.
        var fit = ChartTrend.Fit(new ChartTrendline(TrendKind.Linear), Points(x => 2 * x + 1))!;
        var label = ChartTrend.Label(new ChartTrendline(TrendKind.Linear) { ShowEquation = true, ShowRSquared = true }, fit, ChartTextStyle.Default, ChartTrend.Number);
        Assert.Equal(["y = 2x + 1", "R2 = 1"], label.Select(line => string.Concat(line.Select(r => r.Text))));
        Assert.True(label[1][1].Raise > 0);
        var square = ChartTrend.Label(new ChartTrendline(TrendKind.Polynomial) { ShowEquation = true },
            ChartTrend.Fit(new ChartTrendline(TrendKind.Polynomial), Points(x => x * x - 3))!, ChartTextStyle.Default, ChartTrend.Number);
        Assert.Equal("y = x2 - 3", string.Concat(square[0].Select(r => r.Text)));
        Assert.Equal("1.2346E+7", ChartTrend.Number(12_345_678));
        Assert.Equal("0.3333", ChartTrend.Number(1 / 3.0));
    }

    [Fact]
    public void A_trendline_and_its_equation_are_drawn_over_the_series()
    {
        string[] q = ["Q1", "Q2", "Q3", "Q4"];
        var trend = Trendline("linear", "<c:forward val=\"1\"/><c:dispRSqr val=\"1\"/><c:dispEq val=\"1\"/>");
        var (bitmap, page) = Picture(Chart(Bars(Series(0, "North", q, [40, 50, 60, 70], Solid("C00000") + trend)), Axes()));
        using (bitmap)
            Assert.True(Pixels(bitmap, Purple) > 60);
        var text = Text(page);
        Assert.Contains("y = 10x + 30", text);
        Assert.Contains("R", text);
    }

    [Fact]
    public void Error_bars_are_read_and_drawn_at_each_point()
    {
        string[] q = ["Q1", "Q2", "Q3", "Q4"];
        var custom = ErrorBars("cust", extra: Literal("plus", 1, 2, 3, 4) + Literal("minus", 5), rgb: "FF00FF");
        var xml = Chart(Bars(Series(0, "North", q, [40, 55, 70, 62], Solid("C00000") + custom) +
                             Series(1, "South", q, [30, 45, 35, 50], Solid("00B050") + ErrorBars("stdDev", 2, type: "plus", extra: "<c:noEndCap val=\"1\"/>", rgb: "FF00FF"))), Axes());
        var series = Drawn(xml).Plots[0].Series;

        var bars = Assert.Single(series[0].ErrorBars);
        Assert.Equal((ErrorAmount.Custom, true, true), (bars.Amount, bars.Plus, bars.Minus));
        Assert.Equal([1.0, 2, 3, 4], bars.PlusValues!.Select(v => v!.Value));
        Assert.Equal([5.0], bars.MinusValues!.Select(v => v!.Value));
        var deviation = Assert.Single(series[1].ErrorBars);
        Assert.Equal((ErrorAmount.StandardDeviation, 2.0, true, false), (deviation.Amount, deviation.Value, deviation.Plus, deviation.Minus));

        var (bitmap, _) = Picture(xml);
        using (bitmap)
            Assert.True(Pixels(bitmap, Magenta) > 40);
        var (plain, _) = Picture(Columns());
        using (plain)
            Assert.Equal(0, Pixels(plain, Magenta));

        // Excel's statistics: the standard deviation, and its standard error √(Σy² / (n(n − 1))).
        var (mean, sd, se) = ChartTrend.Statistics([1, 2, 3, 4]);
        Assert.Equal((2.5, 1.291, 1.581), (mean, Math.Round(sd, 3), Math.Round(se, 3)));
        Assert.Equal(0.645, Math.Round(ChartTrend.Statistics([1, 2, 3, 4], office: false).Error, 3));
    }

    [Fact]
    public void Scatter_error_bars_run_along_x_or_y()
    {
        var xml = Chart(Scatter(ScatterSeries(0, "A", [1, 2, 4], [3, 1, 2], ErrorBars("fixedVal", 0.5, "x", rgb: "FF00FF") + ErrorBars("percentage", 10, rgb: "00FFFF"))), Axes(scatter: true));
        var bars = Drawn(xml).Plots[0].Series[0].ErrorBars;
        Assert.Equal([true, false], bars.Select(b => b.AlongX));
        var (bitmap, _) = Picture(xml);
        using (bitmap)
            Assert.True(Pixels(bitmap, Magenta) > 20 && Pixels(bitmap, p => p.Green > 150 && p.Blue > 150 && p.Red < 110) > 10);
    }

    [Fact]
    public void A_point_label_of_its_own_overrides_the_series_labels()
    {
        string[] q = ["Q1", "Q2", "Q3", "Q4"];
        var labels = "<c:dLbls><c:dLbl><c:idx val=\"2\"/><c:tx><c:rich><a:bodyPr/><a:p><a:r><a:rPr b=\"1\"/><a:t>Best</a:t></a:r><a:r><a:rPr/><a:t> quarter</a:t></a:r></a:p></c:rich></c:tx>" +
                     "<c:dLblPos val=\"outEnd\"/><c:showVal val=\"1\"/></c:dLbl><c:dLbl><c:idx val=\"0\"/><c:delete val=\"1\"/></c:dLbl>" +
                     "<c:dLbl><c:idx val=\"3\"/><c:numFmt formatCode=\"0.0\" sourceLinked=\"0\"/><c:showVal val=\"1\"/><c:showCatName val=\"1\"/><c:separator>; </c:separator></c:dLbl>" +
                     "<c:showVal val=\"1\"/></c:dLbls>";
        var xml = Chart(Bars(Series(0, "North", q, [41, 57, 73, 63], Solid("C00000") + labels)), Axes());
        var series = Drawn(xml).Plots[0].Series[0];

        Assert.False(series.PointLabels[0].Any);
        Assert.Equal(["Best", " quarter"], series.PointLabels[2].Text![0].Select(r => r.Text));
        Assert.True(series.PointLabels[2].Text![0][0].Style.Bold);
        Assert.Equal(("0.0", "; ", true, true), (series.PointLabels[3].Format, series.PointLabels[3].Separator, series.PointLabels[3].Value, series.PointLabels[3].Category));

        var (bitmap, page) = Picture(xml);
        bitmap.Dispose();
        var text = Text(page);
        Assert.Contains("57", text);
        Assert.Contains("Best quarter", text);
        Assert.Contains("Q4; 63.0", text);
        Assert.DoesNotContain("41", text);
        Assert.DoesNotContain("73", text);
    }

    [Fact]
    public void Multi_level_categories_draw_their_outer_level_beyond_the_inner_one()
    {
        var levels = MultiLevel(["Q1", "Q2", "Q3", "Q1", "Q2", "Q3"], [(0, "2025"), (3, "2026")]);
        foreach (var direction in new[] { "col", "bar" })
        {
            var xml = Chart(Bars(Series(0, "North", [], [40, 55, 70, 62, 48, 75], Solid("C00000") + levels), direction), Axes(horizontal: direction == "bar"));
            var series = Drawn(xml).Plots[0].Series[0];
            Assert.Equal(["Q1", "Q2", "Q3", "Q1", "Q2", "Q3"], series.Categories!);
            Assert.Equal(["2025", null, null, "2026", null, null], Assert.Single(series.CategoryLevels!));

            var (bitmap, page) = Picture(xml);
            bitmap.Dispose();
            var inner = Glyphs(page, "Q1").Positions[0];
            var outer = Glyphs(page, "2025").Positions[0];
            if (direction == "col")
                Assert.True(outer.Y > inner.Y + 5, $"{outer} {inner}");
            else
                Assert.True(outer.X < inner.X - 5, $"{outer} {inner}");
            Assert.Contains("2026", Text(page));
        }
    }

    [Fact]
    public void A_rich_title_keeps_the_look_of_each_run()
    {
        var xml = Chart(Bars(Series(0, "North", ["a", "b"], [1, 2])), Axes(), "x").Replace(Title("x"), RichTitle("Area ", "in km", "2"), StringComparison.Ordinal);
        var title = Drawn(xml).Title!;

        Assert.Equal(["Area in km2"], title.Lines);
        var runs = Assert.Single(title.Runs!);
        Assert.Equal([true, false, false], runs.Select(r => r.Style.Bold));
        Assert.Equal(Red, runs[0].Style.Color);
        Assert.Equal(14, runs[1].Style.Size);
        Assert.True(runs[2].Raise > 0 && runs[2].Style.Size < 14);

        var (bitmap, page) = Picture(xml);
        bitmap.Dispose();
        var first = Glyphs(page, "Area");
        var plain = Glyphs(page, "in km");
        var raised = page.Items.OfType<GlyphItem>().Select(g => g.Run).First(r => r.Text == "2" && r.Positions[0].X > plain.Positions[0].X);
        Assert.Equal(Red, first.Color);
        Assert.True(raised.Positions[0].Y < plain.Positions[0].Y - 1);
        Assert.True(plain.Positions[0].X > first.Positions[0].X);

        // A title all in one style keeps no runs.
        Assert.Null(Drawn(Columns()).Title!.Runs);
    }

    [Fact]
    public void Gradient_and_pattern_fills_are_drawn_as_such()
    {
        string[] q = ["Q1", "Q2", "Q3", "Q4"];
        var xml = Chart(Bars(Series(0, "Gradient", q, [40, 55, 70, 62], Gradient("FF0000", "0000FF")) + Series(1, "Hatch", q, [30, 45, 35, 50], Pattern("dkUpDiag", "00B050")) +
                             Series(2, "Dots", q, [20, 25, 40, 30], Pattern("pct50", "000000"))), Axes());
        var series = Drawn(xml).Plots[0].Series;

        Assert.Equal([new SKColor(0xFF, 0, 0), new SKColor(0, 0, 0xFF)], series[0].Style.Gradient!.Stops.Select(s => s.Color));
        Assert.Equal(90, series[0].Style.Gradient!.Angle);
        Assert.Equal(new SKColor(0xFF, 0, 0), series[0].Style.Fill);
        Assert.Equal((45.0, 1, (SKColor?)SKColors.White), (series[1].Style.Hatch!.Angle, series[1].Style.Hatch.Crossings, series[1].Style.Hatch.Background));
        Assert.Equal(0.5f, series[2].Style.Hatch!.Blend);

        static bool Greenish(SKColor p) => p.Green > p.Red + 40 && p.Green > p.Blue + 20;
        var (bitmap, _) = Picture(xml);
        var (solid, _) = Picture(xml.Replace(Pattern("dkUpDiag", "00B050"), Solid("00B050"), StringComparison.Ordinal));
        using (bitmap)
        using (solid)
        {
            // The gradient runs from red to blue through purples; the hatch leaves white between green lines; the dots mix to grey.
            Assert.True(Pixels(bitmap, p => p.Red > 200 && p.Blue < 80 && p.Green < 60) > 30);
            Assert.True(Pixels(bitmap, p => p.Blue > 200 && p.Red < 80 && p.Green < 60) > 30);
            Assert.True(Pixels(bitmap, p => p.Red is > 90 and < 170 && p.Blue is > 90 and < 170 && p.Green < 40) > 100);
            var hatched = Pixels(bitmap, Greenish);
            Assert.InRange(hatched / (double)Pixels(solid, Greenish), 0.1, 0.9);
            Assert.True(Count(bitmap, new SKColor(0x80, 0x80, 0x80)) > 2000);
        }
    }

    [Theory]
    [InlineData("<c:style val=\"3\"/>")]
    [InlineData("<mc:AlternateContent xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\"><mc:Choice Requires=\"c14\" xmlns:c14=\"http://schemas.microsoft.com/office/drawing/2007/8/2/chart\"><c14:style val=\"103\"/></mc:Choice><mc:Fallback><c:style val=\"3\"/></mc:Fallback></mc:AlternateContent>")]
    public void The_chart_style_picks_shades_of_one_accent(string style)
    {
        string[] q = ["Q1", "Q2"];
        var plain = Series(0, "A", q, [4, 5]) + Series(1, "B", q, [3, 4]) + Series(2, "C", q, [2, 3]);
        var xml = Chart(Bars(plain), Axes()).Replace("<c:roundedCorners val=\"0\"/>", "<c:roundedCorners val=\"0\"/>" + style, StringComparison.Ordinal);
        var palette = Drawn(xml).Palette;

        // All three are the first accent (blue), from darker to lighter.
        var lightness = new List<float>();
        for (var i = 0; i < 3; i++)
        {
            palette.Color(i).ToHsl(out var h, out _, out var l);
            Assert.InRange(h, 210, 225);
            lightness.Add(l);
        }
        Assert.True(lightness[0] < lightness[1] && lightness[1] < lightness[2]);
        // Style 2 (Office's default) is the accents in turn.
        Assert.Equal(new SKColor(0xED, 0x7D, 0x31), Drawn(xml.Replace("val=\"3\"", "val=\"2\"", StringComparison.Ordinal).Replace("val=\"103\"", "val=\"102\"", StringComparison.Ordinal)).Palette.Color(1));
    }

    [Fact]
    public void Trendlines_error_bars_and_labels_with_extreme_numbers_are_drawn_in_time()
    {
        var count = 20_000;
        var values = Enumerable.Range(0, count).Select(i => (double?)(i % 2 == 0 ? 1e100 : -1e100)).ToArray();
        var extras = string.Concat(Enumerable.Range(0, 12).Select(_ => Trendline("poly", "<c:order val=\"6\"/><c:forward val=\"1e100\"/><c:dispEq val=\"1\"/><c:dispRSqr val=\"1\"/>"))) +
                     Trendline("movingAvg", "<c:period val=\"999999\"/>") + Trendline("exp", "<c:intercept val=\"-5\"/>") + Trendline("power") +
                     ErrorBars("stdDev", 1e100) + ErrorBars("cust", extra: Literal("plus", 1e100) + Literal("minus", -1e100));
        var labels = "<c:dLbls>" + string.Concat(Enumerable.Range(0, 3000).Select(i => $"<c:dLbl><c:idx val=\"{i}\"/><c:showVal val=\"1\"/></c:dLbl>")) + "<c:showVal val=\"1\"/></c:dLbls>";
        var xml = Chart(Lines(Series(0, "S", [], values, extras + labels)), Axes());
        var watch = Stopwatch.StartNew();

        var chart = Drawn(xml);
        Assert.Equal(ChartReader.MaxTrendlines, chart.Plots[0].Series[0].Trendlines.Count);
        var (bitmap, _) = Picture(xml);
        bitmap.Dispose();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"took {watch.Elapsed}");
    }

    // ───────────────────────── Hostile input ─────────────────────────

    [Fact]
    public void A_cache_that_claims_more_points_than_Filee_draws_is_refused()
    {
        var claimed = Columns().Replace("<c:ptCount val=\"4\"/>", "<c:ptCount val=\"2000000000\"/>", StringComparison.Ordinal);
        Assert.Equal(DocumentLimits.ErrorKey, Assert.Throws<ConversionRefusedException>(() => Read(claimed)).ErrorKey);

        var far = Columns().Replace("<c:pt idx=\"3\">", "<c:pt idx=\"99999999\">", StringComparison.Ordinal);
        Assert.Equal(DocumentLimits.ErrorKey, Assert.Throws<ConversionRefusedException>(() => Read(far)).ErrorKey);

        var series = string.Concat(Enumerable.Range(0, ChartReader.MaxSeries + 1).Select(i => $"<c:ser><c:idx val=\"{i}\"/></c:ser>"));
        Assert.Equal(DocumentLimits.ErrorKey, Assert.Throws<ConversionRefusedException>(() => Read(Chart(Bars(series), Axes()))).ErrorKey);
    }

    [Fact]
    public void Huge_values_long_texts_and_many_categories_are_drawn_in_time()
    {
        var categories = Enumerable.Range(0, 50_000).Select(i => new string('w', 200) + i).ToArray();
        var values = Enumerable.Range(0, 50_000).Select(i => (double?)(i % 3 == 0 ? 1e308 : i % 3 == 1 ? -1e308 : double.Epsilon)).ToArray();
        var labels = "<c:dLbls><c:showVal val=\"1\"/><c:showCatName val=\"1\"/></c:dLbls>";
        var xml = Chart(Lines(Series(0, new string('t', 100_000), categories, values, labels)), Axes(valueExtra: "<c:majorUnit val=\"1e-300\"/>"),
            title: new string('x', 100_000));
        var watch = Stopwatch.StartNew();

        var chart = Drawn(xml);
        Assert.Equal(ChartReader.MaxText, chart.Title!.Lines[0].Length);
        var (bitmap, _) = Picture(xml);
        bitmap.Dispose();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"took {watch.Elapsed}");
    }

    [Fact]
    public void A_chart_placed_over_and_over_is_refused_by_the_workbook_budget()
    {
        var points = (int)(ChartBudget.MaxPoints / 20);
        var big = Chart(Lines(Series(0, "S", [], [.. Enumerable.Range(0, points).Select(i => (double?)i)])), Axes());
        var anchors = string.Concat(Enumerable.Repeat(SheetAnchor(), 21));
        var path = Save(Workbook(anchors, big));

        Assert.Equal(DocumentLimits.ErrorKey, Assert.Throws<ConversionRefusedException>(() => XlsxReader.Read(path)).ErrorKey);
    }

    // ───────────────────────── Sheets ─────────────────────────

    private const string Xdr = "xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";

    /// <summary>A chart from B2 to I20, as Excel anchors one.</summary>
    private static string SheetAnchor() =>
        "<xdr:twoCellAnchor><xdr:from><xdr:col>1</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>1</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from>" +
        "<xdr:to><xdr:col>8</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>19</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:to>" +
        "<xdr:graphicFrame macro=\"\"><xdr:nvGraphicFramePr><xdr:cNvPr id=\"2\" name=\"Chart 1\"/><xdr:cNvGraphicFramePr/></xdr:nvGraphicFramePr>" +
        $"<xdr:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/></xdr:xfrm><a:graphic><a:graphicData uri=\"{GraphicUri}\">" +
        $"<c:chart xmlns:c=\"{GraphicUri}\" r:id=\"rIdC\"/></a:graphicData></a:graphic></xdr:graphicFrame><xdr:clientData/></xdr:twoCellAnchor>";

    private static XlsxBuilder Workbook(string anchors, string chart)
    {
        var book = new XlsxBuilder();
        book.Sheet("Data", XlsxBuilder.Row(1, book.Text("A1", "Sales")), after: "<pageSetup paperSize=\"9\" orientation=\"portrait\"/><drawing r:id=\"rIdD\"/>");
        book.SheetRelationships[1] = [("rIdD", "drawing", "../drawings/drawing1.xml")];
        book.Parts["xl/drawings/drawing1.xml"] = $"<xdr:wsDr {Xdr}>{anchors}</xdr:wsDr>";
        book.Parts["xl/drawings/_rels/drawing1.xml.rels"] = OpcWriter.Relationships([("rIdC", "chart", "../charts/chart1.xml")]);
        book.Parts["xl/charts/chart1.xml"] = chart;
        return book;
    }

    private string Save(XlsxBuilder book) => book.Save(Path.Combine(fx.NewFolder(), "book.xlsx"));

    [Fact]
    public void A_workbook_with_a_chart_stays_on_the_sheet_printer_and_prints_it()
    {
        var path = Save(Workbook(SheetAnchor(), Columns()));
        Assert.Null(SheetCapabilities.FirstUnsupported(path, "xlsx"));

        var book = XlsxReader.Read(path);
        Assert.IsType<SheetChart>(Assert.Single(book.Sheets[0].Drawings));
        using var printed = SheetPrinter.Print(book, new SheetPrintSource("book.xlsx", "C:\\"), DocumentLimits.Default, DateTime.Now, null, TestContext.Current.CancellationToken);
        using var page = printed.Pages[0].Render(72);
        Assert.True(Count(page, Red) > 300 && Count(page, Blue) > 300);
        Assert.Contains("Sales by quarter", Text(printed.Pages[0]));
    }

    [Fact]
    public void A_workbook_with_a_3D_chart_is_still_named()
    {
        var xml = Chart($"<c:bar3DChart><c:barDir val=\"col\"/>{Series(0, "S", ["a"], [1])}<c:axId val=\"1\"/><c:axId val=\"2\"/></c:bar3DChart>", Axes());
        Assert.Equal("charts", SheetCapabilities.FirstUnsupported(Save(Workbook(SheetAnchor(), xml)), "xlsx"));
    }

    /// <summary>
    /// A chart sheet "Chart1" (landscape, a footer with the page number) holding <paramref name="chart"/> on an absolute
    /// anchor, before (or after) a worksheet "Data".
    /// </summary>
    internal static XlsxBuilder ChartSheetBook(string chart, bool chartFirst = true, bool hidden = false, string over = "")
    {
        var book = new XlsxBuilder();
        book.Sheet(chartFirst ? "Chart1" : "Data", chartFirst ? "" : XlsxBuilder.Row(1, book.Text("A1", "Sales")), hidden: hidden && chartFirst);
        book.Sheet(chartFirst ? "Data" : "Chart1", chartFirst ? XlsxBuilder.Row(1, book.Text("A1", "Sales")) : "", hidden: hidden && !chartFirst);
        var (chartId, dataId) = chartFirst ? ("rId1", "rId2") : ("rId2", "rId1");
        book.Parts["xl/_rels/workbook.xml.rels"] = OpcWriter.Relationships([
            (chartId, "chartsheet", "chartsheets/sheet1.xml"), (dataId, "worksheet", $"worksheets/sheet{(chartFirst ? 2 : 1)}.xml"),
            ("rIdS", "sharedStrings", "sharedStrings.xml"), ("rIdT", "styles", "styles.xml")]);
        book.Parts["xl/chartsheets/sheet1.xml"] =
            $"<chartsheet {XlsxBuilder.Namespaces}><sheetPr/><sheetViews><sheetView zoomToFit=\"1\" workbookViewId=\"0\"/></sheetViews>" +
            "<pageMargins left=\"0.7\" right=\"0.7\" top=\"0.75\" bottom=\"0.75\" header=\"0.3\" footer=\"0.3\"/><pageSetup paperSize=\"9\" orientation=\"landscape\"/>" +
            "<headerFooter><oddFooter>&amp;CPage &amp;P</oddFooter></headerFooter><drawing r:id=\"rIdD\"/></chartsheet>";
        book.Parts["xl/chartsheets/_rels/sheet1.xml.rels"] = OpcWriter.Relationships([("rIdD", "drawing", "../drawings/drawing1.xml")]);
        book.Parts["xl/drawings/drawing1.xml"] =
            $"<xdr:wsDr {Xdr}><xdr:absoluteAnchor><xdr:pos x=\"0\" y=\"0\"/><xdr:ext cx=\"9309100\" cy=\"6073775\"/>" +
            "<xdr:graphicFrame macro=\"\"><xdr:nvGraphicFramePr><xdr:cNvPr id=\"2\" name=\"Chart 1\"/><xdr:cNvGraphicFramePr/></xdr:nvGraphicFramePr>" +
            $"<xdr:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/></xdr:xfrm><a:graphic><a:graphicData uri=\"{GraphicUri}\">" +
            $"<c:chart xmlns:c=\"{GraphicUri}\" r:id=\"rIdC\"/></a:graphicData></a:graphic></xdr:graphicFrame><xdr:clientData/></xdr:absoluteAnchor>{over}</xdr:wsDr>";
        book.Parts["xl/drawings/_rels/drawing1.xml.rels"] = OpcWriter.Relationships([("rIdC", "chart", "../charts/chart1.xml")]);
        book.Parts["xl/charts/chart1.xml"] = chart;
        return book;
    }

    [Fact]
    public void A_chart_sheet_prints_as_one_page_with_its_chart_filling_the_margins()
    {
        var path = Save(ChartSheetBook(Columns()));
        Assert.Null(SheetCapabilities.FirstUnsupported(path, "xlsx"));

        var book = XlsxReader.Read(path);
        // The writers to other spreadsheet formats don't see it.
        Assert.Equal(["Data"], book.Sheets.Select(s => s.Name));
        Assert.Equal(["Chart1", "Data"], book.Printed().Select(s => s.Name));
        using var printed = SheetPrinter.Print(book, new SheetPrintSource("book.xlsx", "C:\\"), DocumentLimits.Default, DateTime.Now, null, TestContext.Current.CancellationToken);
        Assert.Equal(2, printed.Pages.Count);
        var page = printed.Pages[0];
        Assert.True(page.Width > page.Height); // landscape A4
        Assert.Equal(841.9, page.Width, 0);
        using (var bitmap = page.Render(72))
        {
            Assert.True(Count(bitmap, Red) > 2000 && Count(bitmap, Blue) > 2000);
            // Nothing is drawn inside the left margin (0.7 in), the chart starts there.
            Assert.Equal(0, Count(bitmap, Red, 0, 40 / 842.0));
        }
        Assert.Contains("Sales by quarter", Text(page));
        Assert.Contains("Page 1", Text(page));
        Assert.Contains("Sales", Text(printed.Pages[1]));
        Assert.Equal(["Chart1", "Data"], printed.Outline.Select(o => o.Title));

        // After the worksheet, and hidden.
        Assert.Equal(["Data", "Chart1"], XlsxReader.Read(Save(ChartSheetBook(Columns(), chartFirst: false))).Printed().Select(s => s.Name));
        Assert.Equal(["Data"], XlsxReader.Read(Save(ChartSheetBook(Columns(), hidden: true))).Printed().Select(s => s.Name));
    }

    [Fact]
    public void A_chart_sheet_whose_chart_is_not_drawn_is_named()
    {
        var radar = Chart($"<c:radarChart>{Series(0, "S", ["a"], [1])}<c:axId val=\"1\"/><c:axId val=\"2\"/></c:radarChart>", Axes());
        var path = Save(ChartSheetBook(radar));
        Assert.Equal("charts", SheetCapabilities.FirstUnsupported(path, "xlsx"));
        Assert.Empty(XlsxReader.Read(path).ChartSheets);

        // A shape placed over the chart isn't printed with it.
        var shape = "<xdr:absoluteAnchor><xdr:pos x=\"0\" y=\"0\"/><xdr:ext cx=\"1270000\" cy=\"1270000\"/><xdr:sp><xdr:nvSpPr><xdr:cNvPr id=\"3\" name=\"Box\"/><xdr:cNvSpPr/></xdr:nvSpPr>" +
                    "<xdr:spPr><a:prstGeom prst=\"rect\"/><a:solidFill><a:srgbClr val=\"FF0000\"/></a:solidFill></xdr:spPr></xdr:sp><xdr:clientData/></xdr:absoluteAnchor>";
        Assert.Equal("shapes", SheetCapabilities.FirstUnsupported(Save(ChartSheetBook(Columns(), over: shape)), "xlsx"));
    }

    [Fact]
    public void Chart_sheets_count_against_the_workbook_chart_budget()
    {
        var points = Series(0, "S", [], [.. Enumerable.Range(0, 20).Select(i => (double?)i)]);
        var big = Chart(Lines(points), Axes()).Replace("<c:ptCount val=\"20\"/>", $"<c:ptCount val=\"{ChartReader.MaxPoints}\"/>", StringComparison.Ordinal);
        // One chart sheet of a full column of points is drawn; eleven of them pass the budget of ten.
        var book = ChartSheetBook(big);
        var sheets = string.Concat(Enumerable.Range(0, 11).Select(i => $"<sheet name=\"C{i}\" sheetId=\"{i + 10}\" r:id=\"rIdC{i}\"/>"));
        book.Parts["xl/workbook.xml"] = $"<workbook {XlsxBuilder.Namespaces}><sheets>{sheets}</sheets></workbook>";
        book.Parts["xl/_rels/workbook.xml.rels"] = OpcWriter.Relationships([.. Enumerable.Range(0, 11).Select(i => ($"rIdC{i}", "chartsheet", "chartsheets/sheet1.xml")),
            ("rIdS", "sharedStrings", "sharedStrings.xml"), ("rIdT", "styles", "styles.xml")]);
        var path = Save(book);
        Assert.Equal(DocumentLimits.ErrorKey, Assert.Throws<ConversionRefusedException>(() => XlsxReader.Read(path)).ErrorKey);
    }

    // ───────────────────────── Word ─────────────────────────

    private string WordWithChart(string chart)
    {
        var docx = new DocxBuilder();
        var id = docx.Chart(chart);
        docx.Paragraph(DocxBuilder.P(DocxBuilder.R("Before the chart.")));
        docx.Paragraph(DocxBuilder.P(InlineChart(id, 432 * 12700, 252 * 12700)));
        return docx.Save(Path.Combine(fx.NewFolder(), "chart.docx"));
    }

    [Fact]
    public void A_Word_chart_is_drawn_by_the_renderer_and_a_picture_of_it_goes_to_the_writers()
    {
        var path = WordWithChart(Columns());
        Assert.Null(new FileeRendererConverter().FirstUnsupported(path, "docx"));

        var document = DocxReader.Read(path, Path.Combine(fx.NewFolder(), "media"));
        Assert.True(document.LeftOut.IsEmpty);
        var picture = document.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HImage>().Single();
        Assert.NotNull(picture.Chart);
        using (var png = SKBitmap.Decode(picture.Path))
            Assert.True(Count(png, Red) > 300);

        using var page = SKBitmap.Decode(FileeRenderer.ToImages(document, 72, cancellationToken: TestContext.Current.CancellationToken).First());
        Assert.True(Count(page, Red) > 300 && Count(page, Green) > 300);
    }

    [Fact]
    public void A_Word_chart_Filee_doesnt_draw_is_named_and_left_out()
    {
        var xml = Chart($"<c:radarChart>{Series(0, "S", ["a"], [1])}<c:axId val=\"1\"/><c:axId val=\"2\"/></c:radarChart>", Axes());
        var path = WordWithChart(xml);
        Assert.Equal("OLE objects and charts", new FileeRendererConverter().FirstUnsupported(path, "docx"));

        var document = DocxReader.Read(path, Path.Combine(fx.NewFolder(), "media"));
        Assert.Contains(document.LeftOut.Kinds, k => k.Kind == HLeftOutKind.Chart);
    }

    // ───────────────────────── HWPX ─────────────────────────

    /// <summary>The 한글-style package with its hp:chart (an OLE object in hp:default) given a size and <paramref name="chart"/> as its part.</summary>
    private string HwpxWithChart(string? chart) => HwpxChartPackage(fx.NewFolder(), chart);

    /// <inheritdoc cref="HwpxWithChart"/>
    internal static string HwpxChartPackage(string folder, string? chart)
    {
        var path = Path.Combine(folder, "chart.hwpx");
        File.Copy(HwpxReaderTests.HangulStylePackage(folder), path);
        using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Update);
        var entry = zip.GetEntry("Contents/section0.xml")!;
        string xml;
        using (var reader = new StreamReader(entry.Open()))
            xml = reader.ReadToEnd();
        entry.Delete();
        const string Bare = "<hp:chart id=\"50\" chartIDRef=\"Chart/chart1.xml\"/>";
        Assert.Contains(Bare, xml, StringComparison.Ordinal);
        xml = xml.Replace(Bare, "<hp:chart id=\"50\" zOrder=\"3\" textWrap=\"TOP_AND_BOTTOM\" chartIDRef=\"Chart/chart1.xml\"><hp:sz width=\"36000\" widthRelTo=\"ABSOLUTE\" height=\"21000\" heightRelTo=\"ABSOLUTE\" protect=\"0\"/>" +
            "<hp:pos treatAsChar=\"1\" affectLSpacing=\"0\" flowWithText=\"1\" allowOverlap=\"0\" holdAnchorAndSO=\"0\" vertRelTo=\"PARA\" horzRelTo=\"COLUMN\" vertAlign=\"TOP\" horzAlign=\"LEFT\" vertOffset=\"0\" horzOffset=\"0\"/></hp:chart>", StringComparison.Ordinal);
        using (var writer = new StreamWriter(zip.CreateEntry("Contents/section0.xml").Open(), new System.Text.UTF8Encoding(false)))
            writer.Write(xml);
        if (chart is not null)
        {
            using var writer = new StreamWriter(zip.CreateEntry("Chart/chart1.xml").Open(), new System.Text.UTF8Encoding(false));
            writer.Write(chart);
        }
        return path;
    }

    [Fact]
    public void An_hwpx_chart_is_drawn_from_its_ooxml_part_instead_of_its_ole_fallback()
    {
        var path = HwpxWithChart(Columns());
        Assert.NotEqual("OLE objects and charts", new FileeRendererConverter().FirstUnsupported(path, "hwpx"));

        var document = HwpxReader.ReadForLayout(path, Path.Combine(fx.NewFolder(), "media"));
        Assert.DoesNotContain(document.LeftOut.Kinds, k => k.Kind is HLeftOutKind.Chart or HLeftOutKind.ObjectLeftOut or HLeftOutKind.ObjectPictured);
        var picture = document.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HImage>().Single(i => i.Chart is not null);
        Assert.Equal((36000, 21000), (picture.Width, picture.Height));
        using (var png = SKBitmap.Decode(picture.Path))
            Assert.True(Count(png, Red) > 300);
        // The writers' reading gets the picture too.
        Assert.Contains(HwpxReader.Read(path, Path.Combine(fx.NewFolder(), "media")).Sections.SelectMany(s => s.Blocks).OfType<HParagraph>()
            .SelectMany(p => p.Inlines).OfType<HImage>(), i => i.Chart is not null);
    }

    [Fact]
    public void An_hwpx_chart_Filee_doesnt_draw_falls_back_to_its_ole_object()
    {
        var radar = Chart($"<c:radarChart>{Series(0, "S", ["a"], [1])}<c:axId val=\"1\"/><c:axId val=\"2\"/></c:radarChart>", Axes());
        foreach (var path in new[] { HwpxWithChart(radar), HwpxWithChart(null), HwpxWithChart("<broken") })
        {
            Assert.Equal("OLE objects and charts", new FileeRendererConverter().FirstUnsupported(path, "hwpx"));
            var document = HwpxReader.ReadForLayout(path, Path.Combine(fx.NewFolder(), "media"));
            Assert.Contains(document.LeftOut.Kinds, k => k.Kind is HLeftOutKind.ObjectLeftOut or HLeftOutKind.ObjectPictured);
            Assert.DoesNotContain(document.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HImage>(), i => i.Chart is not null);
        }
    }

    // ───────────────────────── Slides ─────────────────────────

    private string DeckWithChart(string chart)
    {
        var deck = new SlideDeckBuilder();
        deck.Part("ppt/charts/chart1.xml", chart, "drawingml.chart");
        var frame = "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"4\" name=\"Chart 3\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>" +
                    $"<p:xfrm><a:off x=\"{SlideDeckBuilder.E(100)}\" y=\"{SlideDeckBuilder.E(60)}\"/><a:ext cx=\"{SlideDeckBuilder.E(600)}\" cy=\"{SlideDeckBuilder.E(380)}\"/></p:xfrm>" +
                    $"<a:graphic><a:graphicData uri=\"{GraphicUri}\"><c:chart xmlns:c=\"{GraphicUri}\" r:id=\"rIdC\"/></a:graphicData></a:graphic></p:graphicFrame>";
        deck.Slide(frame, relationships: [("rIdC", "chart", "../charts/chart1.xml")]);
        return deck.Save(Path.Combine(fx.NewFolder(), "deck.pptx"));
    }

    [Fact]
    public void A_slide_chart_is_drawn_and_its_picture_made_for_what_takes_pictures()
    {
        var path = DeckWithChart(Columns());
        Assert.Null(new FileeRendererConverter().FirstUnsupported(path, "pptx"));

        var deck = PptxSlideReader.Read(path, cancellationToken: TestContext.Current.CancellationToken);
        var shape = Assert.Single(deck.Slides[0].Elements.OfType<SlideShape>(), s => s.Chart is not null);
        Assert.NotNull(shape.Chart);
        using (var bitmap = Assert.Single(SlideOutput.ToBitmaps(deck, 72, cancellationToken: TestContext.Current.CancellationToken)))
            Assert.True(Count(bitmap, Red) > 500 && Count(bitmap, Blue) > 500);
        // The ODP writer and the deck's text take the picture.
        using var picture = SKBitmap.Decode(Assert.IsType<PictureFill>(shape.Fill).Image.Data);
        Assert.True(Count(picture, Red) > 500);
    }

    [Fact]
    public void A_slide_chart_Filee_doesnt_draw_is_named()
    {
        var xml = Chart($"<c:pie3DChart>{Series(0, "S", ["a"], [1])}</c:pie3DChart>");
        Assert.Equal("charts", new FileeRendererConverter().FirstUnsupported(DeckWithChart(xml), "pptx"));
    }

    // ───────────────────────── Text ─────────────────────────

    [Fact]
    public void Chart_text_in_other_scripts_is_drawn_as_text()
    {
        var xml = Chart(Bars(Series(0, "매출", ["一月", "二月"], [1, 2])), Axes(), "분기별 매출");
        var (bitmap, page) = Picture(xml);
        bitmap.Dispose();
        var text = Text(page);
        Assert.Contains("분기별 매출", text);
        Assert.Contains("一月", text);
    }
}
