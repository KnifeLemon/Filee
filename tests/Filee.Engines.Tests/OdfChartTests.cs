// OpenDocument charts (Charts/OdfChartReader): an embedded chart object read into the chart model from its own table,
// and drawn where it appears: in ODT documents (OdtReader, the renderer and its check, the left-out notice) and on ODP
// slides (OdfDrawReader, the slide renderer). Charts are built in code (OdfChartBuilder); pictures are checked by how
// much of each series' colour they hold, not by where the fonts put the text.

using System.Diagnostics;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Charts;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Odt;
using Filee.Engines.Infrastructure;
using Filee.Engines.Odf.Draw;
using Filee.Engines.Rendering;
using Filee.Engines.Slides;
using Filee.Engines.Typesetting;
using SkiaSharp;

namespace Filee.Engines.Tests;

public sealed class OdfChartTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly SKColor Red = new(0xC0, 0x00, 0x00), Green = new(0x00, 0xB0, 0x50), Blue = new(0x00, 0x70, 0xC0);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static ChartRead Read(string content) => OdfChartReader.Read(XDocument.Parse(content).Root);

    private static Chart Drawn(string content) => Read(content).Chart ?? throw new InvalidOperationException("The chart isn't drawn.");

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

    private static SKBitmap Picture(Chart chart, float width = 480, float height = 300)
    {
        var page = new PageContent(width, height);
        using (var fonts = new ChartFonts())
            ChartDrawer.Draw(page, chart, new SKRect(0, 0, width, height), fonts);
        return page.Render(72);
    }

    // ───────────────────────── Reading ─────────────────────────

    [Fact]
    public void A_column_chart_is_read_from_its_own_table()
    {
        var chart = Drawn(OdfChartBuilder.Columns());

        var plot = Assert.Single(chart.Plots);
        Assert.Equal((ChartKind.Bar, ChartGrouping.Standard, false), (plot.Kind, plot.Grouping, plot.Horizontal));
        Assert.Equal(["North", "South", "West"], plot.Series.Select(s => s.Name));
        Assert.Equal([40.0, 55, 70, 62], plot.Series[0].Values.Select(v => v!.Value));
        Assert.Equal(["Q1", "Q2", "Q3", "Q4"], plot.Series[0].Categories!);
        Assert.Equal(Red, plot.Series[0].Style.Fill);
        Assert.Equal(["Sales by quarter"], chart.Title!.Lines);
        Assert.Equal(16, chart.Title.Style.Size, 1);
        Assert.Equal("b", chart.Legend!.Position);
        Assert.Equal([false, true], chart.Axes.Select(a => a.IsValue));
        Assert.Equal(["Units"], chart.Axes[1].Title!.Lines);
        Assert.NotNull(chart.Axes[1].MajorGridlines);

        using var bitmap = Picture(chart);
        Assert.True(Count(bitmap, Red) > 500 && Count(bitmap, Green) > 500 && Count(bitmap, Blue) > 500);
    }

    [Fact]
    public void Stacked_horizontal_bars_and_a_line_on_the_secondary_axis_are_read()
    {
        var chart = Drawn(OdfChartBuilder.Columns(plotProps: "chart:stacked=\"true\" chart:vertical=\"true\""));
        var plot = Assert.Single(chart.Plots);
        Assert.Equal((ChartGrouping.Stacked, true), (plot.Grouping, plot.Horizontal));

        var combo = Drawn(OdfChartBuilder.Columns(seriesExtra: [("", ""), ("", ""), ("chart:class=\"chart:line\" chart:attached-axis=\"secondary-y\"", "")]));
        Assert.Equal([ChartKind.Bar, ChartKind.Line], combo.Plots.Select(p => p.Kind));
        Assert.Equal(["West"], combo.Plots[1].Series.Select(s => s.Name));
        Assert.Equal([3u, 4u], combo.Plots[1].AxisIds);
        Assert.Equal(4, combo.Axes.Count);
        Assert.Equal("r", combo.Axes[3].Position);
    }

    [Fact]
    public void A_pie_takes_its_point_colours_and_a_ring_is_a_doughnut()
    {
        var points = "<chart:data-point chart:style-name=\"pRed\"/><chart:data-point chart:repeated=\"2\" chart:style-name=\"pGreen\"/>";
        var pie = Drawn(OdfChartBuilder.Columns("chart:circle", series: 1, seriesExtra: [("", points)]));
        var series = Assert.Single(Assert.Single(pie.Plots).Series);
        Assert.Equal(ChartKind.Pie, pie.Plots[0].Kind);
        Assert.Equal(Red, series.Points[0].Fill);
        Assert.Equal(Green, series.Points[1].Fill);
        Assert.Equal(Green, series.Points[2].Fill);
        Assert.False(series.Points.ContainsKey(3));
        Assert.Null(series.Style.Fill); // the other slices take the palette
        Assert.Equal(25, series.Explosion[0]);
        Assert.Empty(pie.Axes);

        Assert.Equal(ChartKind.Doughnut, Assert.Single(Drawn(OdfChartBuilder.Columns("chart:ring", series: 1)).Plots).Kind);
    }

    [Fact]
    public void A_scatter_takes_its_x_values_from_the_domain()
    {
        var chart = Drawn(OdfChartBuilder.Columns("chart:scatter", series: 1, plotProps: "chart:lines=\"false\" chart:symbol-type=\"automatic\"",
            seriesExtra: [("", "<chart:domain table:cell-range-address=\"local-table.$C$2:.$C$5\"/>")]));
        var plot = Assert.Single(chart.Plots);
        Assert.Equal(ChartKind.Scatter, plot.Kind);
        Assert.False(plot.Lines);
        Assert.True(plot.Markers);
        Assert.Equal([30.0, 45, 35, 50], plot.Series[0].XValues!.Select(v => v!.Value));
        Assert.True(chart.Axes[0].IsValue);
    }

    [Fact]
    public void A_range_into_a_sheet_falls_back_to_the_series_column_of_the_chart_table()
    {
        var chart = Drawn(OdfChartBuilder.Columns().Replace("local-table.$", "Sheet1.$", StringComparison.Ordinal));
        var plot = Assert.Single(chart.Plots);
        Assert.Equal(["North", "South", "West"], plot.Series.Select(s => s.Name));
        Assert.Equal([20.0, 25, 40, 30], plot.Series[2].Values.Select(v => v!.Value));
    }

    [Theory]
    [InlineData("chart:radar")]
    [InlineData("chart:stock")]
    [InlineData("chart:bubble")]
    [InlineData("chart:gantt")]
    public void Other_chart_classes_and_3D_charts_are_named_instead(string kind)
    {
        Assert.Equal(new ChartRead(null, "charts"), Read(OdfChartBuilder.Columns(kind)));
        Assert.Equal(new ChartRead(null, "charts"), Read(OdfChartBuilder.Columns(plotProps: "chart:three-dimensional=\"true\"")));
        Assert.False(OdfChartReader.IsChart(XDocument.Parse($"<office:document-content {OdfChartBuilder.Namespaces}><office:body><office:text/></office:body></office:document-content>").Root));
    }

    // ───────────────────────── ODT ─────────────────────────

    private string Document(string? chart, bool picture = false)
    {
        var builder = new OdtBuilder
        {
            Body = "<text:p>Before the chart.</text:p><text:p><draw:frame text:anchor-type=\"as-char\" svg:width=\"15cm\" svg:height=\"8cm\">" +
                   "<draw:object xlink:href=\"./Object 1\"/>" + (picture ? "<draw:image xlink:href=\"./ObjectReplacements/Object 1\"/>" : "") +
                   "</draw:frame></text:p>",
        };
        if (chart is not null)
            builder.Parts["Object 1/content.xml"] = Encoding.UTF8.GetBytes(chart);
        if (picture)
            builder.Parts["ObjectReplacements/Object 1"] = DocumentFuzzTests.TinyPng(4, 3);
        return builder.Save(Path.Combine(fx.NewFolder(), "chart.odt"));
    }

    [Fact]
    public void An_odt_chart_is_drawn_by_the_renderer_and_a_picture_of_it_goes_to_the_writers()
    {
        var path = Document(OdfChartBuilder.Columns(), picture: true);
        Assert.Null(new FileeRendererConverter().FirstUnsupported(path, "odt"));

        var document = OdtReader.Read(path, Path.Combine(fx.NewFolder(), "media"));
        Assert.True(document.LeftOut.IsEmpty);
        var image = document.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HImage>().Single();
        Assert.NotNull(image.Chart);
        using (var png = SKBitmap.Decode(image.Path))
            Assert.True(Count(png, Red) > 300);

        using var page = SKBitmap.Decode(FileeRenderer.ToImages(document, 72, cancellationToken: Token).First());
        Assert.True(Count(page, Red) > 300 && Count(page, Blue) > 300);
    }

    [Fact]
    public async Task An_odt_chart_Filee_doesnt_draw_is_counted_as_a_chart()
    {
        var radar = OdfChartBuilder.Columns("chart:radar");
        Assert.Equal("OLE objects and charts", new FileeRendererConverter().FirstUnsupported(Document(radar, picture: true), "odt"));
        Assert.Contains(OdtReader.Read(Document(radar, picture: true), fx.NewFolder()).LeftOut.Kinds, k => k.Kind == HLeftOutKind.ChartPictured);
        Assert.Contains(OdtReader.Read(Document(radar), fx.NewFolder()).LeftOut.Kinds, k => k.Kind == HLeftOutKind.Chart);

        var folder = fx.NewFolder();
        LeftOutNotice? notice = null;
        await new FileeRendererConverter(() => false).ConvertAsync(
            new ConversionStep(Document(radar, picture: true), "odt", "pdf", new Preset(), new FolderOutput(folder), folder, LeftOut: n => notice = n), null, Token);
        Assert.Equal(new LeftOutPart("notice.renderer_left_out.charts_pictured", 1), Assert.Single(notice!.Parts));

        // An object that isn't a chart (or isn't there) is still an embedded object.
        Assert.Contains(OdtReader.Read(Document(null, picture: true), fx.NewFolder()).LeftOut.Kinds, k => k.Kind == HLeftOutKind.ObjectPictured);
    }

    // ───────────────────────── ODP ─────────────────────────

    [Fact]
    public void An_odp_chart_is_drawn_on_its_slide()
    {
        var builder = new OdpBuilder
        {
            Pages = OdpBuilder.Page("<draw:frame svg:x=\"2cm\" svg:y=\"2cm\" svg:width=\"20cm\" svg:height=\"12cm\"><draw:object xlink:href=\"./Object 1\"/>" +
                                    "<draw:image xlink:href=\"./ObjectReplacements/Object 1\"/></draw:frame>"),
        };
        builder.Parts["Object 1/content.xml"] = Encoding.UTF8.GetBytes(OdfChartBuilder.Columns());
        builder.Parts["ObjectReplacements/Object 1"] = DocumentFuzzTests.TinyPng(4, 3);
        var path = builder.Save(Path.Combine(fx.NewFolder(), "deck.odp"));

        Assert.Null(OdfDrawReader.FirstUnsupported(path, Token));
        var deck = OdfDrawReader.Read(path, cancellationToken: Token);
        var shape = Assert.Single(deck.Slides[0].Elements.OfType<SlideShape>(), s => s.Chart is not null);
        Assert.True(shape.IsPicture);
        using var bitmap = Assert.Single(SlideOutput.ToBitmaps(deck, 72, cancellationToken: Token));
        Assert.True(Count(bitmap, Red) > 500 && Count(bitmap, Blue) > 500);
    }

    // ───────────────────────── ODS ─────────────────────────

    /// <summary>A spreadsheet with "Sales" in A1 and a chart object in a frame of B2 reaching G16 (Calc's anchoring), with its replacement picture.</summary>
    private string Spreadsheet(string? chart) => SpreadsheetWithChart(fx.NewFolder(), chart);

    /// <inheritdoc cref="Spreadsheet"/>
    internal static string SpreadsheetWithChart(string folder, string? chart)
    {
        const string Draw = "xmlns:draw=\"urn:oasis:names:tc:opendocument:xmlns:drawing:1.0\" xmlns:svg=\"urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0\" xmlns:xlink=\"http://www.w3.org/1999/xlink\"";
        var frame = $"<draw:frame {Draw} table:end-cell-address=\"Sheet1.G16\" table:end-x=\"0.5cm\" table:end-y=\"0.2cm\" svg:x=\"0.1cm\" svg:y=\"0.1cm\" svg:width=\"12cm\" svg:height=\"7cm\">" +
                    "<draw:object xlink:href=\"./Object 1\"/><draw:image xlink:href=\"./ObjectReplacements/Object 1\"/></draw:frame>";
        var tables = "<table:table table:name=\"Sheet1\"><table:table-row><table:table-cell office:value-type=\"string\"><text:p>Sales</text:p></table:table-cell></table:table-row>" +
                     $"<table:table-row><table:table-cell/><table:table-cell>{frame}</table:table-cell></table:table-row></table:table>";
        var path = OdsBuilder.Save(Path.Combine(folder, "book.ods"), "", tables);
        if (chart is not null)
        {
            using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Update);
            using var writer = new StreamWriter(zip.CreateEntry("Object 1/content.xml").Open(), new UTF8Encoding(false));
            writer.Write(chart);
        }
        return path;
    }

    [Fact]
    public void An_ods_chart_is_printed_by_the_sheet_printer()
    {
        var path = Spreadsheet(OdfChartBuilder.Columns());
        Assert.Null(Office.Sheets.SheetCapabilities.FirstUnsupported(path, "ods"));

        var book = Office.Sheets.OdsReader.Read(path);
        var chart = Assert.IsType<Office.Sheets.SheetChart>(Assert.Single(book.Sheets[0].Drawings));
        Assert.Equal((1, 1), (chart.Row, chart.Column));
        Assert.Equal((15, 6), (chart.To!.Value.Row, chart.To.Value.Column));
        using var printed = Office.Sheets.SheetPrinter.Print(book, new Office.Sheets.SheetPrintSource("book.ods", "C:\\"), DocumentLimits.Default, DateTime.Now, null, Token);
        using var bitmap = printed.Pages[0].Render(72);
        Assert.True(Count(bitmap, Red) > 300 && Count(bitmap, Blue) > 300);

        // A chart Filee doesn't draw, or an object that isn't one, is still named.
        Assert.Equal("charts", Office.Sheets.SheetCapabilities.FirstUnsupported(Spreadsheet(OdfChartBuilder.Columns("chart:radar")), "ods"));
        Assert.Empty(Office.Sheets.OdsReader.Read(Spreadsheet(OdfChartBuilder.Columns("chart:radar"))).Sheets[0].Drawings);
        Assert.Equal("embedded objects", Office.Sheets.SheetCapabilities.FirstUnsupported(Spreadsheet(null), "ods"));
        Assert.Empty(Office.Sheets.OdsReader.Read(Spreadsheet("<broken")).Sheets[0].Drawings);
    }

    // ───────────────────────── Hostile input ─────────────────────────

    [Fact]
    public void Tables_series_and_points_beyond_the_limits_are_refused_in_time()
    {
        var clock = Stopwatch.StartNew();
        var cells = OdfChartBuilder.Columns().Replace("<table:table-row>", "<table:table-row table:number-rows-repeated=\"999999999\">", StringComparison.Ordinal)
            .Replace("<table:table-cell><text:p/>", "<table:table-cell table:number-columns-repeated=\"64\"><text:p/>", StringComparison.Ordinal);
        Assert.Equal(DocumentLimits.ErrorKey, Assert.Throws<ConversionRefusedException>(() => Read(cells)).ErrorKey);

        var series = OdfChartBuilder.Columns(series: ChartReader.MaxSeries + 1);
        Assert.Equal(DocumentLimits.ErrorKey, Assert.Throws<ConversionRefusedException>(() => Read(series)).ErrorKey);

        var points = OdfChartBuilder.Columns().Replace("local-table.$B$2:.$B$5", "local-table.$B$1:.$ZZZ$1048576", StringComparison.Ordinal);
        Assert.Equal(DocumentLimits.ErrorKey, Assert.Throws<ConversionRefusedException>(() => Read(points)).ErrorKey);

        // Style chains that loop end.
        var loop = OdfChartBuilder.Columns().Replace("<style:style style:name=\"ch1\" style:family=\"chart\">",
            "<style:style style:name=\"ch1\" style:family=\"chart\" style:parent-style-name=\"ch1\">", StringComparison.Ordinal);
        Assert.NotNull(Read(loop).Chart);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"Took {clock.Elapsed}.");
    }

    [Fact]
    [Trait("Category", "Fuzz")]
    public void OdfChartReader_reads_or_refuses_every_mutated_chart_object() =>
        new FuzzHarness(fx, 40, 20261120).Data("odf-chart", "xml",
            [.. new[] { OdfChartBuilder.Columns(), OdfChartBuilder.Columns("chart:circle", series: 1), OdfChartBuilder.Columns(plotProps: "chart:percentage=\"true\" chart:vertical=\"true\"") }
                .Select(Encoding.UTF8.GetBytes)],
            FuzzHarness.Xml(["table:number-rows-repeated=\"99999999\"", "table:number-columns-repeated=\"1048576\"", "chart:repeated=\"2147483647\"",
                "office:value=\"1e309\"", "office:value=\"NaN\"", "chart:class=\"chart:line\"", "chart:attached-axis=\"secondary-y\"",
                "chart:logarithmic=\"true\"", "chart:minimum=\"5\" chart:maximum=\"-5\"", "chart:interval-major=\"1e-300\"", "style:rotation-angle=\"1e9\"",
                "fo:font-size=\"40000pt\"", "local-table.$A$1:.$ZZ$99", "Sheet1.$B$2:.$B$5", "chart:pie-offset=\"1e9\""]),
            bytes =>
            {
                XDocument xml;
                try
                {
                    xml = BoundedXml.Bytes(bytes, "content.xml", LoadOptions.None);
                }
                catch (XmlException ex)
                {
                    throw new InvalidDataException(ex.Message, ex);
                }
                if (OdfChartReader.Read(xml.Root).Chart is { } chart)
                    Picture(chart, 240, 160).Dispose();
            });

    private sealed class FolderOutput(string folder) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => Path.Combine(folder, $"out{suffix}.{extension}");
    }
}

/// <summary>OpenDocument chart objects (an embedded object's content.xml) written in code, the way LibreOffice saves them.</summary>
internal static class OdfChartBuilder
{
    public const string Namespaces =
        "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\" " +
        "xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" " +
        "xmlns:draw=\"urn:oasis:names:tc:opendocument:xmlns:drawing:1.0\" xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\" " +
        "xmlns:svg=\"urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0\" xmlns:chart=\"urn:oasis:names:tc:opendocument:xmlns:chart:1.0\" " +
        "xmlns:xlink=\"http://www.w3.org/1999/xlink\"";

    private static readonly string[] Names = ["North", "South", "West"];
    private static readonly string[] Colors = ["#c00000", "#00b050", "#0070c0"];
    private static readonly double[][] Values = [[40, 55, 70, 62], [30, 45, 35, 50], [20, 25, 40, 30]];

    /// <summary>
    /// A chart of class <paramref name="kind"/> with <paramref name="series"/> series (red, green and blue, then
    /// repeated) of four quarters in its own table, a title, a legend at the bottom and two axes with titles.
    /// </summary>
    /// <param name="seriesExtra">Per series: attributes and child elements added to its chart:series.</param>
    public static string Columns(string kind = "chart:bar", int series = 3, string plotProps = "", (string Attributes, string Children)[]? seriesExtra = null)
    {
        static string Column(int i) => i < 26 ? ((char)('A' + i)).ToString() : $"{(char)('A' + i / 26 - 1)}{(char)('A' + i % 26)}";
        var styles = new StringBuilder();
        styles.Append("<style:style style:name=\"ch1\" style:family=\"chart\"><style:graphic-properties draw:fill-color=\"#ffffff\" draw:stroke=\"none\"/></style:style>");
        styles.Append("<style:style style:name=\"ch2\" style:family=\"chart\"><style:text-properties fo:font-size=\"16pt\" fo:font-weight=\"bold\"/></style:style>");
        styles.Append($"<style:style style:name=\"ch3\" style:family=\"chart\"><style:chart-properties {plotProps}/></style:style>");
        styles.Append("<style:style style:name=\"ch4\" style:family=\"chart\"><style:chart-properties chart:display-label=\"true\" chart:tick-marks-major-outer=\"true\"/></style:style>");
        styles.Append("<style:style style:name=\"grid\" style:family=\"chart\"><style:graphic-properties svg:stroke-color=\"#b3b3b3\"/></style:style>");
        styles.Append("<style:style style:name=\"pRed\" style:family=\"chart\"><style:chart-properties chart:pie-offset=\"25\"/><style:graphic-properties draw:fill-color=\"#c00000\"/></style:style>");
        styles.Append("<style:style style:name=\"pGreen\" style:family=\"chart\"><style:graphic-properties draw:fill-color=\"#00b050\"/></style:style>");
        for (var i = 0; i < series; i++)
            styles.Append($"<style:style style:name=\"s{i}\" style:family=\"chart\"><style:graphic-properties draw:fill-color=\"{Colors[i % 3]}\" svg:stroke-color=\"{Colors[i % 3]}\"/></style:style>");

        var plot = new StringBuilder();
        plot.Append("<chart:axis chart:dimension=\"x\" chart:name=\"primary-x\" chart:style-name=\"ch4\"><chart:title><text:p>Quarter</text:p></chart:title>");
        plot.Append("<chart:categories table:cell-range-address=\"local-table.$A$2:.$A$5\"/></chart:axis>");
        plot.Append("<chart:axis chart:dimension=\"y\" chart:name=\"primary-y\" chart:style-name=\"ch4\"><chart:title><text:p>Units</text:p></chart:title>");
        plot.Append("<chart:grid chart:style-name=\"grid\" chart:class=\"major\"/></chart:axis>");
        if (seriesExtra?.Any(s => s.Attributes.Contains("secondary-y", StringComparison.Ordinal)) == true)
            plot.Append("<chart:axis chart:dimension=\"y\" chart:name=\"secondary-y\" chart:style-name=\"ch4\"/>");
        for (var i = 0; i < series; i++)
        {
            var (attributes, children) = seriesExtra is not null && i < seriesExtra.Length ? seriesExtra[i] : ("", "");
            var column = Column(Math.Min(i, 2) + 1);
            plot.Append($"<chart:series chart:style-name=\"s{i}\" chart:values-cell-range-address=\"local-table.${column}$2:.${column}$5\" " +
                        $"chart:label-cell-address=\"local-table.${column}$1\" {attributes}>{children}<chart:data-point chart:repeated=\"4\"/></chart:series>");
        }
        plot.Append("<chart:wall/>");

        var table = new StringBuilder("<table:table table:name=\"local-table\"><table:table-header-columns><table:table-column/></table:table-header-columns>");
        table.Append("<table:table-columns><table:table-column table:number-columns-repeated=\"3\"/></table:table-columns>");
        table.Append("<table:table-header-rows><table:table-row><table:table-cell><text:p/></table:table-cell>");
        foreach (var name in Names)
            table.Append($"<table:table-cell office:value-type=\"string\"><text:p>{name}</text:p></table:table-cell>");
        table.Append("</table:table-row></table:table-header-rows><table:table-rows>");
        for (var row = 0; row < 4; row++)
        {
            table.Append($"<table:table-row><table:table-cell office:value-type=\"string\"><text:p>Q{row + 1}</text:p></table:table-cell>");
            foreach (var values in Values)
                table.Append($"<table:table-cell office:value-type=\"float\" office:value=\"{values[row]}\"><text:p>{values[row]}</text:p></table:table-cell>");
            table.Append("</table:table-row>");
        }
        table.Append("</table:table-rows></table:table>");

        return $"<office:document-content {Namespaces} office:version=\"1.3\"><office:automatic-styles>{styles}</office:automatic-styles>" +
               $"<office:body><office:chart><chart:chart svg:width=\"16cm\" svg:height=\"9cm\" chart:class=\"{kind}\" chart:style-name=\"ch1\">" +
               "<chart:title chart:style-name=\"ch2\"><text:p>Sales by quarter</text:p></chart:title><chart:legend chart:legend-position=\"bottom\"/>" +
               $"<chart:plot-area chart:style-name=\"ch3\" chart:data-source-has-labels=\"both\">{plot}</chart:plot-area>{table}" +
               "</chart:chart></office:chart></office:body></office:document-content>";
    }
}
