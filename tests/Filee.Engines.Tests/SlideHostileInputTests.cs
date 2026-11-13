// Hostile decks against the slide reader (PptxSlideReader) and the capability check (SlideCapabilities): fallbacks
// nested to the XML depth limit, a SmartArt drawing that draws itself, custom geometry of empty paths inherited from a
// layout, a slide listed over and over, and a master repeated on more slides than the deck has elements. Every deck is
// built here and read in well under a second; each must be read or refused with error.document_too_complex.

using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Slides;
using SkiaSharp;
using static Filee.Engines.Tests.SlideDeckBuilder;

namespace Filee.Engines.Tests;

public sealed class SlideHostileInputTests : IDisposable
{
    private const string Dsp = "xmlns:dsp=\"http://schemas.microsoft.com/office/drawing/2008/diagram\"";
    private const string Dgm = "xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"filee-slides-hostile-{Guid.NewGuid():N}");

    public SlideHostileInputTests() => Directory.CreateDirectory(_folder);

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

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Save(SlideDeckBuilder deck) => deck.Save(Path.Combine(_folder, $"{Guid.NewGuid():N}.pptx"));

    private static SlideDeck Read(string path, DocumentLimits? limits = null) => PptxSlideReader.Read(path, limits, Token);

    /// <summary>Replaces or adds parts of a saved package.</summary>
    private static void Replace(string path, IReadOnlyDictionary<string, string> parts)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        foreach (var (name, xml) in parts)
        {
            zip.GetEntry(name)?.Delete();
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(xml);
        }
    }

    private static bool IsRed(SlideElement element) => element is SlideShape { Fill: SolidFill { Color: var color } } && color == SKColors.Red;

    /// <summary>Runs <paramref name="action"/> and checks it took less than <paramref name="seconds"/>.</summary>
    private static T Quickly<T>(Func<T> action, double seconds = 10)
    {
        var clock = Stopwatch.StartNew();
        var result = action();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(seconds), $"took {clock.Elapsed}");
        return result;
    }

    // ───────────────────────── Fallbacks and SmartArt ─────────────────────────

    [Fact]
    public void Fallbacks_nested_to_the_xml_depth_limit_are_read()
    {
        var deck = new SlideDeckBuilder();
        var content = deck.Shape("rect", 10, 10, 100, 50, Solid("#FF0000"), styled: false);
        // sld / cSld / spTree, then two levels per fallback: the shape ends just inside the 512 levels a part may have.
        for (var i = 0; i < 248; i++)
            content = $"<mc:AlternateContent><mc:Choice Requires=\"p14\"/><mc:Fallback>{content}</mc:Fallback></mc:AlternateContent>";
        var path = Save(deck.Slide(content));

        Assert.Null(Quickly(() => SlideCapabilities.FirstUnsupported(path)));
        var read = Quickly(() => Read(path));
        Assert.Contains(read.Slides[0].Elements, IsRed);
    }

    [Fact]
    public void Fallbacks_in_a_row_are_each_read_in_order()
    {
        var deck = new SlideDeckBuilder();
        string Fallback(string color, int depth)
        {
            var content = deck.Shape("rect", 10, 10, 100, 50, Solid(color), styled: false);
            for (var i = 0; i < depth; i++)
                content = $"<mc:AlternateContent><mc:Fallback>{content}</mc:Fallback></mc:AlternateContent>";
            return content;
        }
        // A choice without a fallback is read; a fallback is read rather than its choice.
        var choice = "<mc:AlternateContent><mc:Choice Requires=\"p14\">" + deck.Shape("rect", 0, 0, 10, 10, Solid("#00FF00"), styled: false) + "</mc:Choice></mc:AlternateContent>";
        var path = Save(deck.Slide(Fallback("#FF0000", 3) + choice + Fallback("#0000FF", 1)));

        SKColor[] expected = [SKColors.Red, new SKColor(0, 0xFF, 0), SKColors.Blue];
        // The master's bar comes first, in its theme colour.
        var colors = Read(path).Slides[0].Elements.OfType<SlideShape>().Select(s => s.Fill).OfType<SolidFill>().Select(f => f.Color).Where(expected.Contains);
        Assert.Equal(expected, colors);
    }

    /// <summary>A SmartArt frame whose stored drawing holds a frame of the same SmartArt, and so on.</summary>
    private string SelfDrawingSmartArt()
    {
        var deck = new SlideDeckBuilder();
        string Frame() =>
            $"<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"{deck.NextId()}\" name=\"Diagram\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>{Xfrm(60, 60, 420, 120, element: "p:xfrm")}" +
            $"<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\"><dgm:relIds {Dgm} r:dm=\"rIdData\"/></a:graphicData></a:graphic></p:graphicFrame>";
        var shape = deck.Shape("rect", 0, 0, 50, 50, Solid("#FF0000"), styled: false);
        var path = Save(deck.Slide(Frame(), relationships: [("rIdData", "diagramData", "../diagrams/data1.xml"), ("rIdDrawing", "diagramDrawing", "../diagrams/drawing1.xml")]));
        Replace(path, new Dictionary<string, string>
        {
            ["ppt/diagrams/data1.xml"] = $"<dgm:dataModel {Dgm} xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><dgm:extLst><a:ext uri=\"x\">" +
                                         $"<dsp:dataModelExt {Dsp} relId=\"rIdDrawing\"/></a:ext></dgm:extLst></dgm:dataModel>",
            ["ppt/diagrams/drawing1.xml"] = $"<dsp:drawing {Dsp} {Namespaces}><dsp:spTree>{shape}{Frame()}</dsp:spTree></dsp:drawing>",
            ["ppt/diagrams/_rels/drawing1.xml.rels"] = OpcWriter.Relationships([("rIdData", "diagramData", "data1.xml"), ("rIdDrawing", "diagramDrawing", "drawing1.xml")]),
        });
        return path;
    }

    [Fact]
    public void A_smartart_drawing_that_draws_itself_is_drawn_once()
    {
        var path = SelfDrawingSmartArt();
        Assert.Null(Quickly(() => SlideCapabilities.FirstUnsupported(path)));
        var read = Quickly(() => Read(path));
        var smartArt = Assert.Single(read.Slides[0].Elements.OfType<SlideGroup>());
        Assert.Contains(smartArt.Children, IsRed);
        Assert.DoesNotContain(smartArt.Children, c => c is SlideGroup);
    }

    // ───────────────────────── Geometry ─────────────────────────

    private const string TreeStart = "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>";

    /// <summary>The blank layout with a body placeholder (index 1) of custom geometry, for the slides' placeholders to inherit.</summary>
    private static string LayoutWithGeometry(string paths) =>
        $"<p:sldLayout {Namespaces}><p:cSld><p:spTree>{TreeStart}" +
        "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Body\"/><p:cNvSpPr/><p:nvPr><p:ph idx=\"1\"/></p:nvPr></p:nvSpPr>" +
        $"<p:spPr>{Xfrm(10, 10, 200, 100)}<a:custGeom><a:pathLst>{paths}</a:pathLst></a:custGeom>{Solid("#FF0000")}</p:spPr>" +
        "<p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:endParaRPr lang=\"en-US\"/></a:p></p:txBody></p:sp>" +
        "</p:spTree></p:cSld></p:sldLayout>";

    /// <summary>A deck of <paramref name="slides"/> blank slides with <paramref name="placeholders"/> body placeholders each.</summary>
    private string Placeholders(int slides, int placeholders, string layout)
    {
        var deck = new SlideDeckBuilder();
        for (var s = 0; s < slides; s++)
            deck.Slide(string.Concat(Enumerable.Range(0, placeholders).Select(_ => deck.Placeholder("body", Paragraph("text"), 1))));
        var path = Save(deck);
        Replace(path, new Dictionary<string, string> { ["ppt/slideLayouts/slideLayout3.xml"] = layout });
        return path;
    }

    [Fact]
    public void Empty_paths_of_a_layouts_geometry_are_left_out()
    {
        var paths = new StringBuilder();
        for (var i = 0; i < 200_000; i++)
            paths.Append("<a:path/>");
        paths.Append("<a:path w=\"10\" h=\"10\"><a:moveTo><a:pt x=\"0\" y=\"0\"/></a:moveTo><a:lnTo><a:pt x=\"10\" y=\"10\"/></a:lnTo></a:path>");
        var path = Placeholders(10, 5, LayoutWithGeometry(paths.ToString()));

        var read = Quickly(() => Read(path));
        var inheriting = read.Slides.SelectMany(s => s.Elements).OfType<SlideShape>().Where(IsRed).ToList();
        Assert.Equal(50, inheriting.Count);
        Assert.All(inheriting, shape => Assert.Single(shape.Paths));
    }

    [Fact]
    public void A_layouts_geometry_counts_against_the_deck_at_every_use()
    {
        // 60,000 commands drawn once per inheriting shape: the 34th use passes the deck's 2,000,000.
        var commands = new StringBuilder("<a:path w=\"10\" h=\"10\"><a:moveTo><a:pt x=\"0\" y=\"0\"/></a:moveTo>");
        for (var i = 0; i < 60_000; i++)
            commands.Append("<a:lnTo><a:pt x=\"").Append(i % 10).Append("\" y=\"5\"/></a:lnTo>");
        commands.Append("</a:path>");
        var layout = LayoutWithGeometry(commands.ToString());

        Assert.Equal(30, Read(Placeholders(1, 30, layout)).Slides[0].Elements.Count(IsRed));
        var refusal = Assert.Throws<ConversionRefusedException>(() => Quickly(() => Read(Placeholders(1, 40, layout))));
        Assert.Equal(DocumentLimits.ErrorKey, refusal.ErrorKey);
    }

    [Fact]
    public void Points_beyond_a_commands_own_are_not_read()
    {
        var points = string.Concat(Enumerable.Repeat("<a:pt x=\"5\" y=\"5\"/>", 100_000));
        var geometry = $"<a:custGeom><a:pathLst><a:path w=\"10\" h=\"10\"><a:moveTo><a:pt x=\"0\" y=\"0\"/>{points}</a:moveTo>" +
                       $"<a:cubicBezTo><a:pt x=\"10\" y=\"0\"/><a:pt x=\"10\" y=\"10\"/><a:pt x=\"0\" y=\"10\"/>{points}</a:cubicBezTo></a:path></a:pathLst></a:custGeom>";
        var deck = new SlideDeckBuilder();
        var read = Quickly(() => Read(Save(deck.Slide(deck.Shape(geometry, 0, 0, 100, 100, Solid("#FF0000"), styled: false)))));
        var shape = (SlideShape)read.Slides[0].Elements.Single(IsRed);
        Assert.Equal(4, Assert.Single(shape.Paths).Path.PointCount);
    }

    [Fact]
    public void Empty_paths_count_against_a_shapes_commands()
    {
        var definition = new GeometryDefinition();
        for (var i = 0; i < 150_000; i++)
            definition.Paths.Add(new GeometryPathDefinition(0, 0, PathFill.Normal, true, []));
        var result = ShapeGeometry.Evaluate(definition, 100, 100, null, 1);
        Assert.True(result.Paths.Count <= ShapeGeometry.MaxCommands);
        foreach (var path in result.Paths)
            path.Path.Dispose();
    }

    // ───────────────────────── Parts read again ─────────────────────────

    [Fact]
    public void A_slide_listed_over_and_over_is_read_once()
    {
        var deck = new SlideDeckBuilder();
        var path = Save(deck.Slide(deck.Shape("rect", 10, 10, 100, 50, Solid("#FF0000"), styled: false)));
        string presentation;
        using (var zip = ZipFile.OpenRead(path))
        using (var reader = new StreamReader(zip.GetEntry("ppt/presentation.xml")!.Open()))
            presentation = reader.ReadToEnd();
        var list = string.Concat(Enumerable.Range(0, 50_000).Select(i => $"<p:sldId id=\"{256 + i}\" r:id=\"rId10\"/>"));
        Replace(path, new Dictionary<string, string>
        {
            ["ppt/presentation.xml"] = System.Text.RegularExpressions.Regex.Replace(presentation, "<p:sldIdLst>.*</p:sldIdLst>", $"<p:sldIdLst>{list}</p:sldIdLst>"),
        });

        Assert.Null(Quickly(() => SlideCapabilities.FirstUnsupported(path)));
        var read = Quickly(() => Read(path));
        Assert.Single(read.Slides);
    }

    [Fact]
    public void A_layouts_shapes_repeated_on_more_slides_than_the_deck_holds_elements_are_refused()
    {
        var bare = string.Concat(Enumerable.Repeat("<p:sp/>", 300));
        var layout = $"<p:sldLayout {Namespaces}><p:cSld><p:spTree>{TreeStart}{bare}</p:spTree></p:cSld></p:sldLayout>";
        var limits = new DocumentLimits { MaxXmlElements = 10_000 };

        // 300 shapes on each of 5 slides are read; on each of 200, the slides visit 6 times the 10,000 elements allowed.
        Assert.Equal(5, Read(Placeholders(5, 0, layout), limits).Slides.Count);
        var refusal = Assert.Throws<ConversionRefusedException>(() => Quickly(() => Read(Placeholders(200, 0, layout), limits)));
        Assert.Equal(DocumentLimits.ErrorKey, refusal.ErrorKey);
    }
}
