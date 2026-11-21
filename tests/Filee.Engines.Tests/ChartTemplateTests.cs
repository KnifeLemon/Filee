// Excel chart templates (.crtx, Charts/ChartTemplate) through the drawing engine: the chart part of the package drawn
// on one page, sample values where the template holds none, the theme override's colours, 3D plots drawn flat, and
// what is refused. Templates are built in code (ChartTemplateBuilder); pictures are checked by how much of each
// colour they hold, not by where the fonts put the text.

using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Charts;
using Filee.Engines.Drawings;
using Filee.Engines.Office.Ooxml;
using SkiaSharp;
using UglyToad.PdfPig;
using static Filee.Engines.Tests.ChartBuilder;

namespace Filee.Engines.Tests;

public sealed class ChartTemplateTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly SKColor Red = new(0xC0, 0x00, 0x00), Accent = new(0x12, 0x9E, 0x3A);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Save(string chart, string? theme = null, string name = "template.crtx") =>
        ChartTemplateBuilder.Save(Path.Combine(fx.NewFolder(), name), chart, theme);

    private static Chart Read(string path)
    {
        using var template = ChartTemplate.Open(path, Token);
        return template.Chart;
    }

    private static SKBitmap Picture(string path)
    {
        using var template = ChartTemplate.Open(path, Token);
        return template.Page(0, Token).Render(72);
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

    /// <summary>A plot of <paramref name="plot"/> with series that only say how they look (no values), as templates often do.</summary>
    private static string Empty(string plot, int series, string axes = "") =>
        Chart(plot.Replace("{series}", string.Concat(Enumerable.Range(0, series).Select(i =>
            $"<c:ser><c:idx val=\"{i}\"/><c:order val=\"{i}\"/>{(i == 0 ? Solid("C00000") : "")}</c:ser>")), StringComparison.Ordinal), axes, legend: "b");

    [Fact]
    public void A_template_with_cached_values_draws_them()
    {
        var chart = Read(Save(Columns()));

        var plot = Assert.Single(chart.Plots);
        Assert.Equal(["North", "South", "West"], plot.Series.Select(s => s.Name));
        Assert.Equal([40.0, 55, 70, 62], plot.Series[0].Values.Select(v => v!.Value));
        using var bitmap = Picture(Save(Columns()));
        Assert.Equal((360, 216), (bitmap.Width, bitmap.Height));
        Assert.True(Count(bitmap, Red) > 500);
    }

    /// <summary><paramref name="read"/> with the app's language set to <paramref name="culture"/>.</summary>
    private static T InLanguage<T>(string culture, Func<T> read)
    {
        var language = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
            return read();
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = language;
        }
    }

    [Fact]
    public void Series_without_values_get_Office_sample_values_and_categories()
    {
        var chart = InLanguage("en-US", () => Read(Save(Empty(Bars("{series}"), 2, Axes()))));

        var plot = Assert.Single(chart.Plots);
        Assert.Equal(2, plot.Series.Count);
        Assert.Equal([4.3, 2.5, 3.5, 4.5], plot.Series[0].Values.Select(v => v!.Value));
        Assert.Equal([2.4, 4.4, 1.8, 2.8], plot.Series[1].Values.Select(v => v!.Value));
        Assert.Equal(["Category 1", "Category 2", "Category 3", "Category 4"], plot.Series[0].Categories!);
        Assert.Equal(["Series 1", "Series 2"], plot.Series.Select(s => s.Name));
        // The template's own look stays.
        Assert.Equal(Red, plot.Series[0].Style.Fill);
        using var bitmap = Picture(Save(Empty(Bars("{series}"), 2, Axes())));
        Assert.True(Count(bitmap, Red) > 300);
    }

    [Theory]
    [InlineData("ko-KR", "항목 1", "계열 2")]
    [InlineData("de-DE", "Kategorie 1", "Datenreihe 2")]
    [InlineData("zh-HK", "類別 1", "數列 2")]
    [InlineData("sv-SE", "Category 1", "Series 2")]
    public void Sample_names_are_in_the_app_language(string culture, string category, string series)
    {
        var plot = Assert.Single(InLanguage(culture, () => Read(Save(Chart(Bars(""), Axes())))).Plots);
        Assert.Equal((category, series), (plot.Series[0].Categories![0], plot.Series[1].Name));
        // Every language the app ships has the names.
        var folder = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Filee.App", "Assets", "i18n");
        foreach (var language in Directory.Exists(folder) ? Directory.GetFiles(folder, "*.json").Select(Path.GetFileNameWithoutExtension) : [])
        {
            var shipped = System.Globalization.CultureInfo.GetCultureInfo(language!);
            Assert.Contains("3", ChartWords.SampleCategory(shipped, 3), StringComparison.Ordinal);
            Assert.Contains("3", ChartWords.SampleSeries(shipped, 3), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_plot_without_series_gets_sample_series()
    {
        var columns = Assert.Single(Read(Save(Chart(Bars(""), Axes()))).Plots);
        Assert.Equal(3, columns.Series.Count);
        Assert.Equal([2.0, 2, 3, 5], columns.Series[2].Values.Select(v => v!.Value));

        var pie = Assert.Single(Read(Save(Chart(Pie("")))).Plots);
        Assert.Equal([8.2, 3.2, 1.4, 1.2], Assert.Single(pie.Series).Values.Select(v => v!.Value));

        var scatter = Assert.Single(Read(Save(Chart(Scatter(""), Axes(scatter: true)))).Plots);
        Assert.Equal([1.0, 2, 3, 4], scatter.Series[0].XValues!.Select(v => v!.Value));
    }

    [Fact]
    public void The_theme_override_colours_the_series()
    {
        var theme = "<a:themeOverride xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><a:clrScheme name=\"Mine\">" +
                    "<a:dk1><a:sysClr val=\"windowText\" lastClr=\"000000\"/></a:dk1><a:lt1><a:sysClr val=\"window\" lastClr=\"FFFFFF\"/></a:lt1>" +
                    "<a:dk2><a:srgbClr val=\"44546A\"/></a:dk2><a:lt2><a:srgbClr val=\"E7E6E6\"/></a:lt2><a:accent1><a:srgbClr val=\"129E3A\"/></a:accent1>" +
                    "<a:accent2><a:srgbClr val=\"ED7D31\"/></a:accent2><a:accent3><a:srgbClr val=\"A5A5A5\"/></a:accent3><a:accent4><a:srgbClr val=\"FFC000\"/></a:accent4>" +
                    "<a:accent5><a:srgbClr val=\"5B9BD5\"/></a:accent5><a:accent6><a:srgbClr val=\"70AD47\"/></a:accent6><a:hlink><a:srgbClr val=\"0563C1\"/></a:hlink>" +
                    "<a:folHlink><a:srgbClr val=\"954F72\"/></a:folHlink></a:clrScheme><a:fontScheme name=\"Mine\"><a:majorFont><a:latin typeface=\"Arial\"/></a:majorFont>" +
                    "<a:minorFont><a:latin typeface=\"Arial\"/></a:minorFont></a:fontScheme></a:themeOverride>";
        var path = Save(Chart(Bars(""), Axes()), theme);

        var chart = Read(path);
        Assert.Equal(Accent, chart.Palette.Color(0));
        Assert.Equal("Arial", chart.ThemeFont);
        using var bitmap = Picture(path);
        Assert.True(Count(bitmap, Accent) > 300);
    }

    [Fact]
    public void Three_d_plots_are_drawn_in_3D()
    {
        var bar3D = Chart("<c:bar3DChart><c:barDir val=\"col\"/><c:grouping val=\"clustered\"/><c:varyColors val=\"0\"/>" +
                          Series(0, "Deep", ["a", "b"], [3, 4], Solid("C00000")) + "<c:shape val=\"box\"/><c:axId val=\"1\"/><c:axId val=\"2\"/><c:axId val=\"3\"/></c:bar3DChart>",
            Axes() + "<c:serAx><c:axId val=\"3\"/><c:scaling><c:orientation val=\"minMax\"/></c:scaling><c:delete val=\"1\"/><c:axPos val=\"b\"/><c:crossAx val=\"2\"/></c:serAx>");
        var plot = Assert.Single(Read(Save(bar3D)).Plots);
        Assert.Equal(ChartKind.Bar, plot.Kind);
        Assert.True(plot.ThreeD);
        var pie = Assert.Single(Read(Save(Chart("<c:pie3DChart><c:varyColors val=\"1\"/></c:pie3DChart>"))).Plots);
        Assert.True(pie is { Kind: ChartKind.Pie, ThreeD: true });
        Assert.Equal(4, Assert.Single(pie.Series).Values.Count);
    }

    [Fact]
    public void Charts_Filee_does_not_draw_and_files_that_are_not_templates_are_refused()
    {
        Assert.Equal(ChartTemplate.UnsupportedKey, Assert.Throws<ConversionRefusedException>(() => Read(Save(Undrawn()))).ErrorKey);
        // Radar, bubble and the other kinds get samples like the rest.
        var radar = Read(Save(Chart("<c:radarChart><c:radarStyle val=\"marker\"/><c:varyColors val=\"0\"/><c:axId val=\"1\"/><c:axId val=\"2\"/></c:radarChart>", Axes())));
        Assert.Equal(3, Assert.Single(radar.Plots).Series.Count);
        var bubbles = Read(Save(Chart("<c:bubbleChart><c:varyColors val=\"0\"/><c:axId val=\"1\"/><c:axId val=\"2\"/></c:bubbleChart>", Axes(scatter: true))));
        Assert.All(Assert.Single(bubbles.Plots).Series, s => Assert.Equal(4, s.BubbleSizes?.Count));
        Assert.NotNull(Read(Save(ChartSamples.Waterfall())));

        var chartEx = Save("<cx:chartSpace xmlns:cx=\"http://schemas.microsoft.com/office/drawing/2014/chartex\"><cx:chart/></cx:chartSpace>");
        Assert.Equal(ChartTemplate.UnsupportedKey, Assert.Throws<ConversionRefusedException>(() => Read(chartEx)).ErrorKey);

        Assert.Throws<InvalidDataException>(() => Read(Save("<root/>")));
        var notZip = Path.Combine(fx.NewFolder(), "plain.crtx");
        File.WriteAllText(notZip, "not a package");
        Assert.Throws<InvalidDataException>(() => Read(notZip));
    }

    [Fact]
    public void The_chart_part_is_found_without_a_package_relationship()
    {
        var path = Path.Combine(fx.NewFolder(), "loose.crtx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("chart/chart.xml").Open(), new UTF8Encoding(false));
            writer.Write(Columns());
        }
        Assert.Equal(3, Assert.Single(Read(path).Plots).Series.Count);
    }

    // ───────────────────────── Converter ─────────────────────────

    [Fact]
    public void Crtx_is_a_format_the_drawing_engine_reads()
    {
        Assert.Equal("crtx", FormatRegistry.FindByExtension("crtx")?.Id);
        var engine = Assert.Single(fx.Converters.OfType<DrawingConverter>());
        Assert.Equal([("crtx", "pdf"), ("crtx", "svg"), ("crtx", "png")], engine.Edges.Where(e => e.From == "crtx").Select(e => (e.From, e.To)));
        Assert.All(engine.Edges.Where(e => e.From == "crtx"), e => Assert.False(e.Checked));
        Assert.Equal("drawing", fx.Catalog.CreatePlanner([]).Plan("crtx", "png")!.Steps[0].Converter.Id);
    }

    [Theory]
    [InlineData("pdf")]
    [InlineData("svg")]
    [InlineData("png")]
    public async Task A_template_is_converted_to_one_page(string target)
    {
        var folder = fx.NewFolder();
        var source = Save(Empty(Bars("{series}"), 3, Axes()));
        var written = await new DrawingConverter().ConvertAsync(
            new ConversionStep(source, "crtx", target, new Preset(), new FolderOutput(folder), folder), null, Token);
        var file = Assert.Single(written);
        Assert.True(new FileInfo(file).Length > 100);
        if (target == "pdf")
        {
            using var pdf = PdfDocument.Open(file);
            Assert.Equal(1, pdf.NumberOfPages);
            Assert.Equal(ChartTemplate.Width, pdf.GetPage(1).Width, 1);
            // The sample names are in the app's language (this machine's, here).
            Assert.Contains(ChartWords.SampleCategory(System.Globalization.CultureInfo.CurrentUICulture, 1).Split(' ')[0],
                pdf.GetPage(1).Text, StringComparison.Ordinal);
        }
    }

    // ───────────────────────── Hostile input ─────────────────────────

    [Fact]
    public void A_template_beyond_the_chart_limits_is_refused_and_many_empty_series_are_drawn_in_time()
    {
        var tooMany = Empty(Bars("{series}"), ChartReader.MaxSeries + 1, Axes());
        Assert.Equal(Documents.DocumentLimits.ErrorKey, Assert.Throws<ConversionRefusedException>(() => Read(Save(tooMany))).ErrorKey);
        var claimed = Chart(Bars("<c:ser><c:idx val=\"0\"/><c:order val=\"0\"/><c:val><c:numLit><c:ptCount val=\"99999999\"/><c:pt idx=\"0\"><c:v>1</c:v></c:pt></c:numLit></c:val></c:ser>"), Axes());
        Assert.Equal(Documents.DocumentLimits.ErrorKey, Assert.Throws<ConversionRefusedException>(() => Read(Save(claimed))).ErrorKey);

        var clock = Stopwatch.StartNew();
        using var bitmap = Picture(Save(Empty(Bars("{series}"), ChartReader.MaxSeries, Axes())));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"Took {clock.Elapsed}.");
    }

    private sealed class FolderOutput(string folder) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => Path.Combine(folder, $"out{suffix}.{extension}");
    }
}

/// <summary>Chart templates (.crtx) written in code: the chart part, its theme override and the relationships Excel writes.</summary>
internal static class ChartTemplateBuilder
{
    public static string Save(string path, string chart, string? theme = null)
    {
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/chart/chart.xml\" ContentType=\"application/vnd.ms-office.chartTemplate+xml\"/>" +
                "<Override PartName=\"/chart/theme/themeOverride1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.themeOverride+xml\"/></Types>",
            ["_rels/.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.microsoft.com/office/2006/relationships/chartTemplate\" Target=\"chart/chart.xml\"/></Relationships>",
            ["chart/chart.xml"] = chart,
        };
        if (theme is not null)
        {
            parts["chart/_rels/chart.xml.rels"] = OpcWriter.Relationships([("rId1", "themeOverride", "theme/themeOverride1.xml")]);
            parts["chart/theme/themeOverride1.xml"] = theme;
        }
        OpcWriter.Save(path, parts, new Dictionary<string, byte[]>());
        return path;
    }
}
