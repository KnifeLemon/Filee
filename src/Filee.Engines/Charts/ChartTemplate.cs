// Excel chart templates (.crtx, #1354) drawn on one page by the drawing engine (DrawingFormats): a chart template is
// an OPC package whose main part is a DrawingML chart part (c:chartSpace), with the chart's theme colours and fonts
// in a theme override part next to it. It stores how a chart looks; the values are whatever the chart held when it was
// saved, often none. Series without cached values get sample values (and categories), and a plot without series gets
// sample series, the way Excel's template preview shows a template: four categories, three series (one for a pie or
// doughnut). The chart is drawn by ChartDrawer on a page of Excel's default chart size (5 × 3 inches), 3D plots in 3D;
// Office 2016 templates (chartex: waterfall, treemap, ...) are drawn when they hold values, and refused with
// error.chart_template_unsupported when ChartReader doesn't read them (no values, region maps).
//
// Hostile input: the package is read within DocumentLimits (part and package sizes, inflate ratio, XML depth and
// elements); ChartReader bounds series, points and texts, the samples add at most four points to a series it already
// bounds, and the chart is charged to a ChartBudget.

using System.Globalization;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Drawings;
using Filee.Engines.Office.Ooxml;
using Filee.Engines.Rendering;
using Filee.Engines.Slides;
using SkiaSharp;

namespace Filee.Engines.Charts;

/// <summary>A chart template opened for drawing: one page with its chart.</summary>
internal sealed class ChartTemplate : IFixedPages
{
    /// <summary>Shown when the template's chart is of a kind ChartReader doesn't draw.</summary>
    public const string UnsupportedKey = "error.chart_template_unsupported";

    /// <summary>Excel's default chart size: 5 × 3 inches, in points.</summary>
    internal const float Width = 360, Height = 216;

    private static readonly XNamespace C = ChartReader.C;
    private static readonly XNamespace ContentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string ChartEx = "http://schemas.microsoft.com/office/drawing/2014/chartex";

    /// <summary>The plots given sample series when they have none.</summary>
    private static readonly HashSet<string> Plots = new(StringComparer.Ordinal)
    {
        "barChart", "bar3DChart", "lineChart", "line3DChart", "areaChart", "area3DChart", "pieChart", "pie3DChart", "doughnutChart",
        "ofPieChart", "scatterChart", "bubbleChart", "radarChart", "stockChart", "surfaceChart", "surface3DChart",
    };

    /// <summary>Plots of one series of slices.</summary>
    private static readonly HashSet<string> Round = new(StringComparer.Ordinal) { "pieChart", "pie3DChart", "doughnutChart", "ofPieChart" };

    /// <summary>Office's sample values of a new chart, one row per series.</summary>
    private static readonly double[][] SampleValues = [[4.3, 2.5, 3.5, 4.5], [2.4, 4.4, 1.8, 2.8], [2, 2, 3, 5]];

    /// <summary>Office's sample values of a new pie chart.</summary>
    private static readonly double[] SampleSlices = [8.2, 3.2, 1.4, 1.2];

    private readonly ChartFonts _fonts = new();

    private ChartTemplate(Chart chart) => Chart = chart;

    public Chart Chart { get; }

    public int Count => 1;

    public string? Title => null;

    public IReadOnlyList<LeftOutPart> LeftOut => [];

    /// <summary>Opens chart template <paramref name="path"/>.</summary>
    /// <exception cref="InvalidDataException">The file isn't a chart template.</exception>
    /// <exception cref="ConversionRefusedException">The chart is of a kind Filee doesn't draw, or too large.</exception>
    public static ChartTemplate Open(string path, CancellationToken cancellationToken = default)
    {
        using var package = new OpcPackage(path, DocumentLimits.Default);
        cancellationToken.ThrowIfCancellationRequested();
        var chart = Read(package);
        new ChartBudget().Charge(chart);
        return new ChartTemplate(chart);
    }

    /// <summary>The template's chart, with samples where it holds no values.</summary>
    internal static Chart Read(OpcPackage package)
    {
        var part = ChartPart(package) ?? throw new InvalidDataException("The file is not a chart template (no chart part).");
        var root = package.Part(part)?.Root ?? throw new InvalidDataException("The file is not a chart template (no chart part).");
        if (root.Name.NamespaceName == ChartEx)
        {
            var themeEx = Theme(package, part);
            return ChartReader.Read(root, DrawingColors.FromTheme(themeEx), ChartReader.ThemeFont(themeEx)).Chart is { } drawn && drawn.PointCount > 0
                ? drawn
                : throw new ConversionRefusedException(UnsupportedKey, "chartex");
        }
        if (root.Name != C + "chartSpace")
            throw new InvalidDataException("The file is not a chart template (its main part isn't a chart).");
        var theme = Theme(package, part);
        var copy = new XElement(root);
        WithSamples(copy);
        return ChartReader.Read(copy, DrawingColors.FromTheme(theme), ChartReader.ThemeFont(theme)).Chart
            ?? throw new ConversionRefusedException(UnsupportedKey);
    }

    /// <summary>
    /// The chart part: the target of the package's chart template (or main document) relationship, else the part the
    /// content types call a chart, else Excel's own name for it.
    /// </summary>
    private static string? ChartPart(OpcPackage package)
    {
        foreach (var relationship in package.Relationships("").Values)
        {
            if (!relationship.External && (relationship.Type.EndsWith("/chartTemplate", StringComparison.Ordinal)
                || relationship.Type.EndsWith("/officeDocument", StringComparison.Ordinal)))
                return relationship.Target;
        }
        var types = package.Part("[Content_Types].xml")?.Root;
        foreach (var entry in types?.Elements(ContentTypes + "Override") ?? [])
        {
            var type = (string?)entry.Attribute("ContentType") ?? "";
            if (type.EndsWith(".chart+xml", StringComparison.Ordinal) || type.EndsWith(".chartTemplate+xml", StringComparison.Ordinal)
                || type.EndsWith(".chartex+xml", StringComparison.Ordinal))
                return ((string?)entry.Attribute("PartName") ?? "").TrimStart('/');
        }
        return package.Part("chart/chart.xml") is not null ? "chart/chart.xml" : null;
    }

    /// <summary>The chart's theme (its theme override, or a theme of the package), or null for Office's.</summary>
    private static XElement? Theme(OpcPackage package, string part)
    {
        var target = package.TargetOfType(part, "/themeOverride") ?? package.TargetOfType(part, "/theme") ?? package.TargetOfType("", "/theme");
        return target is null ? null : package.Part(target)?.Root;
    }

    /// <summary>Fills the plots of chart part <paramref name="chartSpace"/> with sample values where they have none.</summary>
    internal static void WithSamples(XElement chartSpace)
    {
        if (chartSpace.Element(C + "chart")?.Element(C + "plotArea") is not { } plotArea)
            return;
        foreach (var plot in plotArea.Elements().ToList())
        {
            if (plot.Name.Namespace != C)
                continue;
            if (!Plots.Contains(plot.Name.LocalName))
                continue;
            var kind = plot.Name.LocalName;
            var series = plot.Elements(C + "ser").ToList();
            if (series.Count == 0)
            {
                var count = Round.Contains(kind) ? 1 : SampleValues.Length;
                for (var i = 0; i < count; i++)
                {
                    var ser = new XElement(C + "ser", Value("idx", i), Value("order", i));
                    // Series come after the plot's own settings (c:barDir, c:grouping, c:varyColors, c:scatterStyle).
                    if (plot.Elements().LastOrDefault(e => e.Name.LocalName is "barDir" or "grouping" or "varyColors" or "scatterStyle" or "radarStyle" or "ofPieType" or "wireframe") is { } before)
                        before.AddAfterSelf(ser);
                    else
                        plot.AddFirst(ser);
                    series.Add(ser);
                }
            }
            for (var i = 0; i < series.Count; i++)
                Sample(series[i], kind, i);
        }
    }

    private static XElement Value(string name, int value) => new(C + name, new XAttribute("val", value.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Sample name, categories and values for a series that holds none.</summary>
    private static void Sample(XElement ser, string kind, int position)
    {
        var scatter = kind is "scatterChart" or "bubbleChart";
        var values = ser.Element(C + (scatter ? "yVal" : "val"));
        if (values?.Descendants(C + "pt").Any() == true)
            return;
        var row = Round.Contains(kind) ? SampleSlices : SampleValues[position % SampleValues.Length];
        if (ser.Element(C + "tx") is null)
        {
            var name = new XElement(C + "tx", new XElement(C + "v", $"Series {position + 1}"));
            if (ser.Element(C + "order") is { } order)
                order.AddAfterSelf(name);
            else
                ser.AddFirst(name);
        }
        values?.Remove();
        var labels = ser.Element(C + (scatter ? "xVal" : "cat"));
        if (labels?.Descendants(C + "pt").Any() != true)
        {
            labels?.Remove();
            ser.Add(scatter
                ? new XElement(C + "xVal", Literal("numLit", [.. row.Select((_, i) => (i + 1).ToString(CultureInfo.InvariantCulture))]))
                : new XElement(C + "cat", Literal("strLit", [.. row.Select((_, i) => $"Category {i + 1}")])));
        }
        ser.Add(new XElement(C + (scatter ? "yVal" : "val"), Literal("numLit", [.. row.Select(v => v.ToString(CultureInfo.InvariantCulture))])));
        if (kind == "bubbleChart" && ser.Element(C + "bubbleSize")?.Descendants(C + "pt").Any() != true)
        {
            ser.Element(C + "bubbleSize")?.Remove();
            ser.Add(new XElement(C + "bubbleSize", Literal("numLit", [.. row.Select((_, i) => (i + 1 + position).ToString(CultureInfo.InvariantCulture))])));
        }
    }

    private static XElement Literal(string name, string[] points) =>
        new(C + name, Value("ptCount", points.Length),
            points.Select((text, i) => new XElement(C + "pt", new XAttribute("idx", i.ToString(CultureInfo.InvariantCulture)), new XElement(C + "v", text))));

    public PageContent Page(int index, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(index, 0);
        cancellationToken.ThrowIfCancellationRequested();
        var page = new PageContent(Width, Height);
        ChartDrawer.Draw(page, Chart, new SKRect(0, 0, Width, Height), _fonts);
        return page;
    }

    public void Dispose() => _fonts.Dispose();
}
