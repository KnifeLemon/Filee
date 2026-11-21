// Excel 97–2003 charts (BiffChartReader through XlsLayout and XlsReader): embedded charts on worksheets and chart
// sheets read into the chart model and printed by the sheet printer, with the chart groups, series formats, labels,
// trendlines, error bars, axes, 3D view and legend their records say; the capability check no longer names charts
// Filee draws; hostile chart substreams are refused or read quickly. Workbooks are built in code (XlsBuilder,
// XlsChartBuilder); pictures are checked by how much of each colour they hold.

using System.Diagnostics;
using Filee.Core.Conversion;
using Filee.Engines.Charts;
using Filee.Engines.Documents;
using Filee.Engines.Office.Sheets;
using Filee.Engines.Rendering;
using SkiaSharp;
using static Filee.Engines.Tests.XlsChartBuilder;

namespace Filee.Engines.Tests;

public sealed class XlsChartTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly string[] Quarters = ["Q1", "Q2", "Q3", "Q4"];

    private static XlsChartBuilder Columns(XlsChartGroup? group = null) => new()
    {
        Title = "Sales by quarter",
        Categories = Quarters,
        Series =
        [
            new XlsChartSeries("North", [10, 20, 30, 40]) { Format = [LineFormat(0, auto: true), AreaFormat(0xC00000)] },
            new XlsChartSeries("South", [15, 25, 20, 10]) { Format = [LineFormat(0, auto: true), AreaFormat(0x0070C0)] },
        ],
        Groups = [group ?? Column()],
    };

    /// <summary>A workbook with sheet "Data" holding <paramref name="chart"/> from B2 to H16.</summary>
    private string Embedded(XlsChartBuilder chart, bool prints = true, Action<XlsBuilder>? more = null)
    {
        var book = new XlsBuilder();
        book.Sheet("Data").Text(0, 0, "Sales").Number(0, 1, 1).After(chart.Embedded(1, 1, 15, 7, dx1: 512, dy1: 128, prints: prints));
        more?.Invoke(book);
        return book.Save(Path.Combine(fx.NewFolder(), "book.xls"));
    }

    private static Chart OnlyChart(string path) =>
        Assert.IsType<SheetChart>(Assert.Single(XlsReader.Read(path).Sheets[0].Drawings)).Chart;

    /// <summary>The chart alone on a page of its size, rendered at 72 dpi, and the page.</summary>
    private static (SKBitmap Bitmap, PageContent Page) Picture(Chart chart, float width = 480, float height = 300)
    {
        var page = new PageContent(width, height);
        using (var fonts = new ChartFonts())
            ChartDrawer.Draw(page, chart, new SKRect(0, 0, width, height), fonts);
        return (page.Render(72), page);
    }

    private static int Count(SKBitmap bitmap, SKColor color)
    {
        var count = 0;
        for (var x = 0; x < bitmap.Width; x++)
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

    private static string Words(PageContent page) => string.Concat(page.Items.OfType<GlyphItem>().Select(g => g.Run.Text));

    // ───────────────────────── Embedded charts ─────────────────────────

    [Fact]
    public void An_embedded_column_chart_is_read_and_placed_by_its_anchor()
    {
        var path = Embedded(Columns());
        var drawing = Assert.IsType<SheetChart>(Assert.Single(XlsReader.Read(path).Sheets[0].Drawings));
        Assert.Equal((1, 1), (drawing.Row, drawing.Column));
        Assert.True(drawing.OffsetX > 10 && drawing.OffsetY > 1); // half a column, half a row in
        Assert.Equal((15, 7), (drawing.To!.Value.Row, drawing.To.Value.Column));

        var chart = drawing.Chart;
        var plot = Assert.Single(chart.Plots);
        Assert.Equal((ChartKind.Bar, ChartGrouping.Standard, false), (plot.Kind, plot.Grouping, plot.Horizontal));
        Assert.Equal(["North", "South"], plot.Series.Select(s => s.Name));
        Assert.Equal(Quarters, plot.Series[0].Categories!);
        Assert.Equal([10.0, 20, 30, 40], plot.Series[0].Values.Select(v => v!.Value));
        Assert.Equal(new SKColor(0xC0, 0x00, 0x00), plot.Series[0].Style.Fill);
        Assert.Equal("Sales by quarter", Assert.Single(chart.Title!.Lines));
        Assert.Equal("r", chart.Legend!.Position);
        Assert.Equal(2, chart.Axes.Count);
        Assert.NotNull(chart.Axes.Single(a => a.IsValue).MajorGridlines);
    }

    [Fact]
    public void A_workbook_with_an_xls_chart_stays_on_the_sheet_printer_and_prints_it()
    {
        var path = Embedded(Columns());
        Assert.Null(SheetCapabilities.FirstUnsupported(path, "xls"));
        var book = XlsReader.Read(path);
        using var printed = SheetPrinter.Print(book, new SheetPrintSource("book.xls", "C:\\"), DocumentLimits.Default, DateTime.Now, null, TestContext.Current.CancellationToken);
        using var page = printed.Pages[0].Render(72);
        Assert.True(Count(page, new SKColor(0xC0, 0, 0)) > 300 && Count(page, new SKColor(0x00, 0x70, 0xC0)) > 300);
        Assert.Contains("Sales by quarter", Words(printed.Pages[0]));
        Assert.Contains("Sales", Words(printed.Pages[0]));
    }

    [Fact]
    public void A_chart_set_not_to_print_is_left_out_and_one_Filee_cant_read_is_named()
    {
        var path = Embedded(Columns(), prints: false);
        Assert.Empty(XlsReader.Read(path).Sheets[0].Drawings);
        Assert.Null(SheetCapabilities.FirstUnsupported(path, "xls"));

        // A chart without series isn't drawn: the check names it, as it named every chart before.
        var empty = new XlsChartBuilder { Categories = Quarters, Series = [] };
        var unread = Embedded(empty);
        Assert.Empty(XlsReader.Read(unread).Sheets[0].Drawings);
        Assert.Equal("charts", SheetCapabilities.FirstUnsupported(unread, "xls"));
    }

    [Fact]
    public void Automatic_colours_are_the_workbook_palettes_chart_fills_and_lines()
    {
        var chart = OnlyChart(Embedded(new XlsChartBuilder
        {
            Categories = Quarters,
            Series = [new XlsChartSeries("A", [1, 2, 3, 4]), new XlsChartSeries("B", [2, 3, 4, 5])],
        }));
        // Excel 97–2003's chart fills: palette 25 (#9999FF), 26 (#993366).
        Assert.Equal([new SKColor(0x99, 0x99, 0xFF), new SKColor(0x99, 0x33, 0x66)], chart.Plots[0].Series.Select(s => s.Style.Fill!.Value));
        var lines = OnlyChart(Embedded(new XlsChartBuilder
        {
            Categories = Quarters,
            Series = [new XlsChartSeries("A", [1, 2, 3, 4])],
            Groups = [Line()],
        }));
        // Chart lines: palette 33 (#000080), with Excel's first automatic marker.
        Assert.Equal(new SKColor(0x00, 0x00, 0x80), lines.Plots[0].Series[0].Style.Line!.Color);
        Assert.Equal("diamond", lines.Plots[0].Series[0].Marker!.Symbol);
    }

    [Fact]
    public void Bar_line_area_pie_doughnut_radar_and_surface_groups_are_read()
    {
        Assert.Equal((ChartGrouping.PercentStacked, true), Kind(Column(horizontal: true, percent: true), p => (p.Grouping, p.Horizontal)));
        Assert.Equal(ChartGrouping.Stacked, Kind(Line(stacked: true), p => p.Grouping));
        Assert.Equal(ChartKind.Area, Kind(Area(), p => p.Kind));
        Assert.Equal((ChartKind.Pie, 90.0, true), Kind(Pie(start: 90), p => (p.Kind, p.FirstSliceAngle, p.VaryColors)));
        Assert.Equal((ChartKind.Doughnut, 60), Kind(Pie(hole: 60), p => (p.Kind, p.HoleSize)));
        Assert.Equal((ChartKind.Radar, true), Kind(Radar(filled: true), p => (p.Kind, p.Filled)));
        Assert.Equal((ChartKind.Surface, true), Kind(Surface(filled: false), p => (p.Kind, p.Wireframe)));

        static T Kind<T>(XlsChartGroup group, Func<ChartPlot, T> what)
        {
            var records = new XlsChartBuilder
            {
                Categories = Quarters,
                Series = [new XlsChartSeries("A", [1, 2, 3, 4]), new XlsChartSeries("B", [2, 3, 4, 5])],
                Groups = [group],
                NoAxes = group.Type == 0x1019,
            }.Build();
            var chart = BiffChartReader.Read(records, new BiffChartBook()).Chart ?? throw new InvalidOperationException("not drawn");
            return what(Assert.Single(chart.Plots));
        }
    }

    [Fact]
    public void A_pie_keeps_its_pulled_slice_point_colour_and_percent_labels()
    {
        var chart = OnlyChart(Embedded(new XlsChartBuilder
        {
            Title = "Share",
            Categories = ["Tea", "Coffee", "Water"],
            Series =
            [
                new XlsChartSeries("Drinks", [50, 30, 20])
                {
                    Format = [AttachedLabel(0x02)],
                    PointFormats = { [1] = [LineFormat(0), AreaFormat(0x00B050), PieFormat(25)] },
                },
            ],
            Groups = [Pie()],
            NoAxes = true,
        }));
        var series = Assert.Single(chart.Plots[0].Series);
        Assert.Equal(25, series.Explosion[1]);
        Assert.Equal(new SKColor(0x00, 0xB0, 0x50), series.Points[1].Fill);
        Assert.True(series.Labels!.Percent);
        var (bitmap, page) = Picture(chart);
        using (bitmap)
            Assert.True(Count(bitmap, new SKColor(0x00, 0xB0, 0x50)) > 1000);
        Assert.Contains("50%", Words(page));
    }

    [Fact]
    public void Scatter_and_bubble_series_take_their_x_values_and_sizes()
    {
        var scatter = OnlyChart(Embedded(new XlsChartBuilder
        {
            Series = [new XlsChartSeries("Points", [1, 4, 9]) { X = [1, 2, 3], Format = [LineFormat(0, style: 5), MarkerFormat(0xFF0000, 0xFF0000, 8, 7)] }],
            Groups = [Scatter()],
            ValueAxes = true,
        }));
        var s = Assert.Single(scatter.Plots[0].Series);
        Assert.Equal([1.0, 2, 3], s.XValues!.Select(v => v!.Value));
        Assert.True(s.Style.Line!.None);
        Assert.Equal(("circle", 7f), (s.Marker!.Symbol, s.Marker.Size));
        Assert.All(scatter.Axes, a => Assert.True(a.IsValue));

        var bubble = OnlyChart(Embedded(new XlsChartBuilder
        {
            Series = [new XlsChartSeries("Bubbles", [1, 4, 9]) { X = [1, 2, 3], Bubbles = [5, 10, 20] }],
            Groups = [Scatter(bubbles: true)],
            ValueAxes = true,
        }));
        Assert.Equal(ChartKind.Bubble, bubble.Plots[0].Kind);
        Assert.Equal([5.0, 10, 20], bubble.Plots[0].Series[0].BubbleSizes!.Select(v => v!.Value));
    }

    [Fact]
    public void A_stock_chart_keeps_high_low_lines_and_up_down_bars()
    {
        var stock = new XlsChartGroup(0x1018, XlsBuilder.U16(0)) { Extra = [.. DropBar(0xFFFFFF, 100), .. DropBar(0x000000, 100), .. CrtLine(1, 0x000000)] };
        var chart = OnlyChart(Embedded(new XlsChartBuilder
        {
            Categories = ["Mon", "Tue", "Wed"],
            Series =
            [
                new XlsChartSeries("Open", [10, 12, 11]) { Format = [LineFormat(0, style: 5), MarkerFormat(0, 0, 0)] },
                new XlsChartSeries("High", [14, 15, 13]) { Format = [LineFormat(0, style: 5), MarkerFormat(0, 0, 0)] },
                new XlsChartSeries("Low", [9, 11, 10]) { Format = [LineFormat(0, style: 5), MarkerFormat(0, 0, 0)] },
                new XlsChartSeries("Close", [12, 11, 12]) { Format = [LineFormat(0, style: 5), MarkerFormat(0, 0, 0)] },
            ],
            Groups = [stock],
        }));
        var plot = Assert.Single(chart.Plots);
        Assert.NotNull(plot.HighLowLines);
        Assert.Equal(100, plot.UpDownBars!.GapWidth);
        Assert.Equal(SKColors.White, plot.UpDownBars.Up.Fill);
        Assert.All(plot.Series, s => Assert.Equal("none", s.Marker!.Symbol));
    }

    [Fact]
    public void A_column_and_line_combination_puts_the_line_on_the_secondary_axis()
    {
        var chart = OnlyChart(Embedded(new XlsChartBuilder
        {
            Categories = Quarters,
            Series = [new XlsChartSeries("Sales", [100, 200, 150, 300]), new XlsChartSeries("Margin", [0.1, 0.15, 0.12, 0.2]) { Group = 1 }],
            Groups = [Column(), Line() with { Axes = 1 }],
        }));
        Assert.Equal([ChartKind.Bar, ChartKind.Line], chart.Plots.Select(p => p.Kind));
        Assert.Equal(4, chart.Axes.Count);
        var secondary = chart.Axis(chart.Plots[1].AxisIds[1])!;
        Assert.Equal("r", secondary.Position);
    }

    [Fact]
    public void Value_axis_scaling_tick_labels_and_a_date_axis_format_are_read()
    {
        var chart = OnlyChart(Embedded(new XlsChartBuilder
        {
            NumberCategories = [45658, 45689, 45717],
            CategoryXf = XlsBuilder.Date,
            Series = [new XlsChartSeries("Visits", [10, 100, 1000])],
            ValueAxis = [ValueRange(min: 1, max: 10_000, log: true), Tick(major: 1, labels: 3, rotation: 45)],
        }));
        var value = chart.Axes.Single(a => a.IsValue);
        Assert.Equal((1.0, 10_000.0, 10.0), (value.Min!.Value, value.Max!.Value, value.LogBase!.Value));
        Assert.Equal("in", value.MajorTicks);
        Assert.Equal(-45, value.LabelRotation);
        Assert.Null(value.MajorGridlines);
        // The category cells are dates: their format comes with them.
        var categories = chart.Plots[0].Series[0].Categories!;
        Assert.Equal(3, categories.Count);
        Assert.DoesNotContain("45658", categories);
    }

    [Fact]
    public void Trendlines_error_bars_and_point_labels_are_read()
    {
        var chart = OnlyChart(Embedded(new XlsChartBuilder
        {
            Categories = Quarters,
            Series = [new XlsChartSeries("North", [10, 20, 30, 40]) { Format = [AreaFormat(0xC00000), AttachedLabel(0x01)] }],
            AuxiliarySeries =
            [
                Trendline(0, 0, order: 1, equation: true, rSquared: true, forward: 1),
                Trendline(0, 4, order: 2),
                ErrorBar(0, 3, 2, 5),
                ErrorBar(0, 4, 2, 5),
            ],
            Texts = [LinkedText(4, series: 0, point: 2, deleted: true)],
        }));
        var series = chart.Plots[0].Series[0];
        Assert.Equal([TrendKind.Linear, TrendKind.MovingAverage], series.Trendlines.Select(t => t.Kind));
        Assert.True(series.Trendlines[0].ShowEquation && series.Trendlines[0].ShowRSquared);
        Assert.Equal(1, series.Trendlines[0].Forward);
        var bars = Assert.Single(series.ErrorBars);
        Assert.Equal((ErrorAmount.Fixed, 5.0, true, true), (bars.Amount, bars.Value, bars.Plus, bars.Minus));
        Assert.True(series.Labels!.Value);
        Assert.False(series.PointLabels[2].Any);
        var (bitmap, page) = Picture(chart);
        bitmap.Dispose();
        Assert.Contains("30", Words(page));
        Assert.Contains("y = 10x", Words(page));
    }

    [Fact]
    public void A_3D_column_chart_keeps_its_view_walls_and_series_in_depth()
    {
        var chart = OnlyChart(Embedded(Columns(Column() with { Extra = [Chart3d(rotation: 30, elevation: 20, depth: 150, gap: 80)] })));
        var plot = Assert.Single(chart.Plots);
        Assert.True(plot.ThreeD && plot.SeriesInDepth);
        Assert.Equal(80, plot.GapDepth);
        Assert.Equal((20.0, 30.0, 150), (chart.View3D!.RotX, chart.View3D.RotY, chart.View3D.DepthPercent));
        Assert.Contains(chart.Axes, a => a.IsSeries);
        var (bitmap, _) = Picture(chart);
        using (bitmap)
            Assert.True(Count(bitmap, new SKColor(0xC0, 0, 0)) > 500);
    }

    [Fact]
    public void Frames_patterns_dashes_blanks_and_deleted_legend_entries_are_read()
    {
        var chart = OnlyChart(Embedded(new XlsChartBuilder
        {
            Categories = Quarters,
            Series =
            [
                new XlsChartSeries("A", [1, null, 3, 4]) { Format = [LineFormat(0x00FF00, style: 1, weight: 1), AreaFormat(0x0000FF, 0xFFFFFF, pattern: 5)] },
                new XlsChartSeries("B", [2, 3, 4, 5]),
            ],
            Groups = [Line()],
            Blanks = 2,
            PlotFrame = [LineFormat(0x808080), AreaFormat(0xFFFFCC)],
            ChartFrame = [LineFormat(0, style: 5), AreaFormat(0, pattern: 0)],
        }));
        Assert.Equal("span", chart.BlanksAs);
        Assert.Equal(new SKColor(0xFF, 0xFF, 0xCC), chart.PlotArea.Fill);
        Assert.Equal(SKColors.Transparent, chart.Area.Fill);
        var line = chart.Plots[0].Series[0].Style.Line!;
        Assert.Equal((new SKColor(0x00, 0xFF, 0x00), 2f), (line.Color!.Value, line.Width!.Value));
        Assert.NotNull(line.Dash);

        var bars = OnlyChart(Embedded(new XlsChartBuilder
        {
            Categories = Quarters,
            Series = [new XlsChartSeries("A", [1, 2, 3, 4]) { Format = [AreaFormat(0x0000FF, 0xFFFFFF, pattern: 5)] }],
        }));
        Assert.NotNull(bars.Plots[0].Series[0].Style.Hatch);
    }

    // ───────────────────────── Chart sheets ─────────────────────────

    [Fact]
    public void A_chart_sheet_prints_between_the_worksheets_around_it()
    {
        var book = new XlsBuilder();
        book.Sheet("First").Text(0, 0, "one");
        book.ChartSheet("Chart1", Columns().Build());
        book.Sheet("Second").Text(0, 0, "two");
        var path = book.Save(Path.Combine(fx.NewFolder(), "book.xls"));
        Assert.Null(SheetCapabilities.FirstUnsupported(path, "xls"));

        var read = XlsReader.Read(path);
        // The worksheets keep their own cells (the chart sheet takes no worksheet's place).
        Assert.Equal(["First", "Second"], read.Sheets.Select(s => s.Name));
        Assert.Equal("two", read.Sheets[1].Get(0, 0)!.Text);
        Assert.Equal(["First", "Chart1", "Second"], read.Printed().Select(s => s.Name));
        using var printed = SheetPrinter.Print(read, new SheetPrintSource("book.xls", "C:\\"), DocumentLimits.Default, DateTime.Now, null, TestContext.Current.CancellationToken);
        Assert.Equal(3, printed.Pages.Count);
        using var page = printed.Pages[1].Render(72);
        Assert.True(Count(page, new SKColor(0xC0, 0, 0)) > 2000);

        // Hidden, it isn't printed; unreadable, it is named.
        var hidden = new XlsBuilder();
        hidden.Sheet("Data").Text(0, 0, "x");
        hidden.ChartSheet("Chart1", Columns().Build(), hidden: true);
        Assert.Equal(["Data"], XlsReader.Read(hidden.Save(Path.Combine(fx.NewFolder(), "hidden.xls"))).Printed().Select(s => s.Name));
        var unread = new XlsBuilder();
        unread.Sheet("Data").Text(0, 0, "x");
        unread.ChartSheet("Chart1", new XlsChartBuilder { Series = [] }.Build());
        Assert.Equal("charts", SheetCapabilities.FirstUnsupported(unread.Save(Path.Combine(fx.NewFolder(), "unread.xls")), "xls"));
    }

    [Fact]
    public void Cells_and_window_settings_after_an_embedded_chart_are_still_read()
    {
        // Records after the chart's substream belong to the worksheet again (WINDOW2's right-to-left flag here).
        var book = new XlsBuilder();
        book.Sheet("Data").Text(0, 0, "Sales").After([.. Columns().Embedded(1, 1, 10, 5), (0x023E, [0x40, 0, 0, 0, 0, 0, 0, 0, 0, 0])]);
        var read = XlsReader.Read(book.Save(Path.Combine(fx.NewFolder(), "book.xls")));
        Assert.True(read.Sheets[0].RightToLeft);
        Assert.Single(read.Sheets[0].Drawings);
    }

    // ───────────────────────── Hostile input ─────────────────────────

    [Fact]
    public void A_chart_that_claims_more_points_than_Filee_draws_is_refused()
    {
        var series = Enumerable.Range(0, 20).Select(i => new XlsChartSeries($"S{i}", new double?[32_000])).ToList();
        var records = new XlsChartBuilder { Series = series }.Build();
        var refused = Assert.Throws<ConversionRefusedException>(() => BiffChartReader.Read(records, new BiffChartBook()));
        Assert.Equal(DocumentLimits.ErrorKey, refused.ErrorKey);
    }

    [Fact]
    public void Deep_nesting_short_records_and_junk_are_read_quickly()
    {
        var watch = Stopwatch.StartNew();
        // 100,000 Begins without Ends, then a chart; and every record cut short.
        var deep = new List<(int, byte[])> { (0x0809, XlsBuilder.Bof(0x20)), (0x1002, new byte[16]) };
        for (var i = 0; i < 100_000; i++)
            deep.AddRange([(0x1003, new byte[12]), (Begin, [])]);
        _ = BiffChartReader.Read(deep, new BiffChartBook());
        var shortRecords = Columns().Build().Select(r => (r.Id, r.Data.Length > 1 ? r.Data[..1] : r.Data)).ToList();
        _ = BiffChartReader.Read(shortRecords, new BiffChartBook());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), watch.Elapsed.ToString());
    }
}
