// Mind maps drawn (MindMaps/MindMapDrawing): FreeMind / Freeplane and XMind maps built here as those apps save them
// are laid out on a page sized to the map, the central topic in the middle and the main topics on both sides, with the
// file's colours, shapes, folded branches, clouds, arrow links, relationships, boundaries, summaries, floating topics,
// pictures and equations; no two topics overlap. Mind maps → PDF / PNG / SVG take the drawing converter, the outline
// routes stay; a .mm of Objective-C++ source becomes pages of its text; hostile maps are refused quickly.

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Markup;
using Filee.Engines.MindMaps;
using Filee.Engines.Slides;
using SkiaSharp;
using static Filee.Engines.Tests.MarkupTestKit;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Filee.Engines.Tests;

public class MindMapDrawingTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A Freeplane map with colours, a folded branch, a cloud, an arrow link, a rich text topic and positions.</summary>
    internal const string StyledFreeMind = """
        <map version="freeplane 1.9.13">
        <node TEXT="Trip to Busan" ID="ID_root" COLOR="#000000" BACKGROUND_COLOR="#ffee99" STYLE="oval">
        <font NAME="SansSerif" SIZE="20" BOLD="true"/>
        <node TEXT="Travel" POSITION="right" ID="ID_travel" COLOR="#003399">
        <edge COLOR="#cc3300" WIDTH="3" STYLE="bezier"/>
        <cloud COLOR="#e8f0ff"/>
        <arrowlink DESTINATION="ID_food" COLOR="#336699" ENDARROW="Default" STARTARROW="None" MIDDLE_LABEL="hungry"/>
        <node TEXT="KTX from Seoul, about two and a half hours on the fast line" ID="ID_ktx"/>
        <node TEXT="Bus" ID="ID_bus"/>
        </node>
        <node TEXT="Food" POSITION="left" ID="ID_food" STYLE="bubble" BACKGROUND_COLOR="#ddffdd">
        <node TEXT="돼지국밥" ID="ID_gukbap"/>
        <node TEXT="Seafood" ID="ID_sea" FOLDED="true">
        <node TEXT="Jagalchi market" ID="ID_jagalchi"/>
        <node TEXT="Millak" ID="ID_millak"/>
        </node>
        </node>
        <node TEXT="Beaches" POSITION="right" ID="ID_beach">
        <node TEXT="Haeundae" ID="ID_haeundae"/>
        <node TEXT="Gwangalli" ID="ID_gwangalli"/>
        <node ID="ID_rich"><richcontent TYPE="NODE"><html><head/><body><p>Songjeong <b>surf</b></p></body></html></richcontent></node>
        </node>
        <node TEXT="Budget" POSITION="left" ID="ID_budget">
        <node TEXT="Hotel" ID="ID_hotel"/>
        </node>
        </node>
        </map>
        """;

    /// <summary>An XMind Zen / 2020+ map with a theme, a relationship, a boundary, a summary, a floating topic, a picture and an equation.</summary>
    internal static byte[] StyledXMind(string? structure = null)
    {
        Dictionary<string, object?> Topic(string id, string title, object[]? children = null)
        {
            var topic = new Dictionary<string, object?> { ["id"] = id, ["class"] = "topic", ["title"] = title };
            if (children is not null)
                topic["children"] = new Dictionary<string, object> { ["attached"] = children };
            return topic;
        }

        var plan = Topic("plan", "Plan", [Topic("p1", "Scope"), Topic("p2", "Schedule"), Topic("p3", "Risks")]);
        plan["boundaries"] = new[] { new { id = "b1", range = "(0,1)", title = "This week" } };
        plan["summaries"] = new[] { new { id = "s1", range = "(1,2)", topicId = "sum" } };
        ((Dictionary<string, object>)plan["children"]!)["summary"] = new[] { Topic("sum", "Needs review") };
        var build = Topic("build", "Build", [Topic("b-1", "Code"), Topic("b-2", "Test", [Topic("b-2-1", "Unit"), Topic("b-2-2", "UI")])]);
        build["style"] = new { properties = new Dictionary<string, string> { ["svg:fill"] = "#2E7D32", ["fo:color"] = "#FFFFFF", ["shape-class"] = "org.xmind.topicShape.rect" } };
        var ship = Topic("ship", "Ship", [Topic("s-1", "Release notes"), Topic("s-2", "Store")]);
        ship["branch"] = "folded";
        var math = Topic("math", "Growth");
        math["extensions"] = new[] { new { provider = "org.xmind.ui.mathJax", content = new { content = "x^2+1" } } };
        math["image"] = new { src = "xap:resources/pic.png", width = 40, height = 30 };
        var root = Topic("root", "Launch", [plan, build, ship, math, Topic("people", "People")]);
        if (structure is not null)
            root["structureClass"] = structure;
        ((Dictionary<string, object>)root["children"]!)["detached"] = new[] { new Dictionary<string, object?> { ["id"] = "float", ["title"] = "Floating idea", ["position"] = new { x = 0, y = -30 } } };
        var sheet = new Dictionary<string, object?>
        {
            ["id"] = "sheet1",
            ["class"] = "sheet",
            ["title"] = "Sheet 1",
            ["rootTopic"] = root,
            ["relationships"] = new[] { new { id = "r1", end1Id = "p3", end2Id = "b-2", title = "blocks" } },
            ["theme"] = new Dictionary<string, object>
            {
                ["centralTopic"] = new { properties = new Dictionary<string, string> { ["svg:fill"] = "#37474F", ["fo:color"] = "#FFFFFF" } },
                ["mainTopic"] = new { properties = new Dictionary<string, string> { ["shape-class"] = "org.xmind.topicShape.roundedRect" } },
                ["map"] = new { properties = new Dictionary<string, string> { ["multi-line-colors"] = "#E53935 #1E88E5 #43A047 #FB8C00 #8E24AA" } },
            },
        };
        return MindMapTests.Zip(new Dictionary<string, byte[]>
        {
            ["content.json"] = JsonSerializer.SerializeToUtf8Bytes(new[] { sheet }),
            ["manifest.json"] = """{"file-entries":{"content.json":{}}}"""u8.ToArray(),
            ["resources/pic.png"] = DocumentFuzzTests.TinyPng(8, 6),
        });
    }

    private MindMapPages FreeMind(string xml, DocumentLimits? limits = null) =>
        MindMapPages.Draw(MindMapReader.DrawFreeMind(Encoding.UTF8.GetBytes(xml), Path.GetTempPath(), fx.NewFolder(), limits ?? DocumentLimits.Default), Token);

    private MindMapPages XMind(byte[] zip, DocumentLimits? limits = null) =>
        MindMapPages.Draw(MindMapReader.DrawXMind(new MemoryStream(zip), Path.GetTempPath(), fx.NewFolder(), limits ?? DocumentLimits.Default), Token);

    /// <summary>The elements of a sheet's page, in map coordinates (inside the page's group).</summary>
    private static List<SlideElement> Elements(MindMapPages pages, int sheet = 0) =>
        [.. ((SlideGroup)pages.Decks[sheet].Slides[0].Elements.Single()).Children];

    private static string ShapeText(SlideShape shape) =>
        shape.Text is { } frame ? string.Join("\n", frame.Paragraphs.Select(p => InlineText(p.Inlines))) : "";

    /// <summary>The topics drawn: shapes whose text a topic of the map has, by that text.</summary>
    private static Dictionary<string, SlideShape> Topics(MindMapPages pages, IEnumerable<string> titles, int sheet = 0)
    {
        var wanted = titles.ToHashSet();
        return Elements(pages, sheet).OfType<SlideShape>().Where(s => wanted.Contains(ShapeText(s))).GroupBy(ShapeText)
            .ToDictionary(g => g.Key, g => g.First());
    }

    private static void AssertNoOverlap(IEnumerable<SlideShape> shapes)
    {
        var boxes = shapes.Select(s => (Text: ShapeText(s), s.Bounds)).ToList();
        for (var i = 0; i < boxes.Count; i++)
        {
            for (var j = i + 1; j < boxes.Count; j++)
            {
                var a = boxes[i].Bounds;
                var b = boxes[j].Bounds;
                Assert.False(a.IntersectsWith(b), $"\"{boxes[i].Text}\" {a} overlaps \"{boxes[j].Text}\" {b}");
            }
        }
    }

    [Fact]
    public void A_freemind_map_is_drawn_around_its_central_topic_with_its_colours()
    {
        using var pages = FreeMind(StyledFreeMind);

        Assert.Equal(1, pages.Count);
        Assert.Equal("Trip to Busan", pages.Title);
        var titles = new[] { "Trip to Busan", "Travel", "KTX from Seoul, about two and a half hours on the fast line", "Bus", "Food", "돼지국밥", "Seafood",
            "Beaches", "Haeundae", "Gwangalli", "Songjeong surf", "Budget", "Hotel" };
        var topics = Topics(pages, titles);
        Assert.Equal(titles.Order(), topics.Keys.Order());
        AssertNoOverlap(topics.Values);

        // POSITION puts Travel and Beaches right of the central topic, Food and Budget left; subtopics beyond them.
        var centre = topics["Trip to Busan"].Bounds;
        Assert.All(new[] { "Travel", "Beaches", "Bus", "Haeundae" }, t => Assert.True(topics[t].Bounds.Left > centre.Right, t));
        Assert.All(new[] { "Food", "Budget", "돼지국밥", "Hotel" }, t => Assert.True(topics[t].Bounds.Right < centre.Left, t));
        Assert.True(topics["Bus"].Bounds.Left > topics["Travel"].Bounds.Right);

        // The long topic wraps; the folded one hides its two subtopics and shows their count.
        Assert.True(topics["KTX from Seoul, about two and a half hours on the fast line"].Bounds.Height > topics["Bus"].Bounds.Height * 1.5f);
        var shapes = Elements(pages).OfType<SlideShape>().ToList();
        Assert.DoesNotContain(shapes, s => ShapeText(s) is "Jagalchi market" or "Millak");
        Assert.Contains(shapes, s => ShapeText(s) == "2" && s.Bounds.Width < 20);

        // The file's colours: the central topic's oval and fill, the bubble's fill, the edge's colour and width.
        Assert.Equal(new SKColor(0xFF, 0xEE, 0x99), ((SolidFill)topics["Trip to Busan"].Fill!).Color);
        Assert.Equal(new SKColor(0xDD, 0xFF, 0xDD), ((SolidFill)topics["Food"].Fill!).Color);
        Assert.Equal("#003399", ((HText)topics["Travel"].Text!.Paragraphs[0].Inlines[0]).Format.Color);
        Assert.Contains(shapes, s => s.Line is { Width: 3 } line && ((SolidFill)line.Fill).Color == new SKColor(0xCC, 0x33, 0x00));
        // Rich text keeps its bold.
        Assert.True(topics["Songjeong surf"].Text!.Paragraphs[0].Inlines.OfType<HText>().Single(t => t.Text == "surf").Format.Bold);

        // The cloud behind Travel's branch, and the arrow link to Food with its arrowhead and label.
        var cloud = shapes.Single(s => s.Fill is SolidFill { Color: var c } && c == new SKColor(0xE8, 0xF0, 0xFF));
        Assert.True(cloud.Bounds.Contains(topics["Bus"].Bounds) && cloud.Bounds.Contains(topics["Travel"].Bounds));
        Assert.Contains(shapes, s => s.Line is { Tail.Kind: LineEndKind.Triangle } line && ((SolidFill)line.Fill).Color == new SKColor(0x33, 0x66, 0x99));
        Assert.Contains(shapes, s => ShapeText(s) == "hungry");

        // The page is the map with a margin around it.
        var deck = pages.Decks[0];
        var bounds = ((SlideGroup)deck.Slides[0].Elements[0]).ChildBounds;
        Assert.Equal(bounds.Width, deck.Width, 0.5f);
        Assert.True(topics.Values.All(t => bounds.Contains(t.Bounds)));
    }

    [Fact]
    public void An_xmind_map_draws_its_theme_relationships_boundaries_summaries_and_floating_topics()
    {
        using var pages = XMind(StyledXMind());

        var titles = new[] { "Launch", "Plan", "Scope", "Schedule", "Risks", "Build", "Code", "Test", "Unit", "UI", "Ship", "Growth\nx^2+1", "People",
            "Needs review", "Floating idea" };
        var topics = Topics(pages, titles);
        Assert.Equal(titles.Order(), topics.Keys.Order());
        AssertNoOverlap(topics.Values);
        var shapes = Elements(pages).OfType<SlideShape>().ToList();

        // Five main topics: the first three on the right, the others on the left.
        var centre = topics["Launch"].Bounds;
        Assert.All(new[] { "Plan", "Build", "Ship" }, t => Assert.True(topics[t].Bounds.Left > centre.Right, t));
        Assert.All(new[] { "Growth\nx^2+1", "People" }, t => Assert.True(topics[t].Bounds.Right < centre.Left, t));

        // The theme's central fill, the topic's own fill and shape, the multi-line colours for the branches.
        Assert.Equal(new SKColor(0x37, 0x47, 0x4F), ((SolidFill)topics["Launch"].Fill!).Color);
        Assert.Equal(new SKColor(0x2E, 0x7D, 0x32), ((SolidFill)topics["Build"].Fill!).Color);
        Assert.Equal(4, topics["Build"].Paths[0].Path.PointCount);
        Assert.Contains(shapes, s => s.Line is { } line && line.Fill is SolidFill { Color: var c } && c == new SKColor(0xE5, 0x39, 0x35));

        // Ship is folded: its subtopics are hidden. Growth has its picture above its text and its equation.
        Assert.DoesNotContain(shapes, s => ShapeText(s) is "Release notes" or "Store");
        Assert.Single(topics["Growth\nx^2+1"].Text!.Paragraphs[0].Inlines.OfType<HEquation>());
        var picture = shapes.Single(s => s.IsPicture);
        Assert.True(topics["Growth\nx^2+1"].Bounds.Contains(picture.Bounds));
        Assert.Equal(new SKSize(40, 30), picture.Bounds.Size);

        // The boundary around Scope and Schedule with its title, the summary topic beyond Schedule and Risks, the
        // dashed relationship from Risks to Test with its title.
        var boundary = shapes.Single(s => s.Line is { Dash: not null } && s.Fill is SolidFill { Color.Alpha: < 255 });
        Assert.True(boundary.Bounds.Contains(topics["Scope"].Bounds) && boundary.Bounds.Contains(topics["Schedule"].Bounds));
        Assert.False(boundary.Bounds.IntersectsWith(topics["Risks"].Bounds));
        Assert.Contains(shapes, s => ShapeText(s) == "This week");
        var summary = topics["Needs review"].Bounds;
        Assert.True(summary.Left > Math.Max(topics["Schedule"].Bounds.Right, topics["Risks"].Bounds.Right));
        Assert.InRange(summary.MidY, topics["Schedule"].Bounds.Top, topics["Risks"].Bounds.Bottom);
        Assert.Contains(shapes, s => s.Line is { Dash: not null, Tail.Kind: LineEndKind.Triangle } && s.Fill is null);
        Assert.Contains(shapes, s => ShapeText(s) == "blocks");

        // The floating topic would cover the central topic where the file puts it: it goes below the map instead.
        Assert.True(topics["Floating idea"].Bounds.Top > topics.Values.Where(t => t != topics["Floating idea"]).Max(t => t.Bounds.Bottom));
    }

    [Fact]
    public void Logic_structures_put_every_main_topic_on_one_side()
    {
        using var right = XMind(StyledXMind("org.xmind.ui.logic.right"));
        var topics = Topics(right, ["Launch", "Plan", "People"]);
        Assert.True(topics["People"].Bounds.Left > topics["Launch"].Bounds.Right);
        Assert.True(topics["Plan"].Bounds.Left > topics["Launch"].Bounds.Right);

        using var left = XMind(StyledXMind("org.xmind.ui.logic.left"));
        topics = Topics(left, ["Launch", "Plan", "People"]);
        Assert.True(topics["Plan"].Bounds.Right < topics["Launch"].Bounds.Left);
    }

    [Fact]
    public void The_outline_maps_are_drawn_too()
    {
        // MindMapTests' maps: every visible topic drawn once and none overlapping; the XMind's two sheets two pages.
        using var freeMind = FreeMind(MindMapTests.FreeMindMap);
        AssertNoOverlap(Elements(freeMind).OfType<SlideShape>().Where(s => s.Text is not null));
        using var zen = XMind(MindMapTests.XMindZen());
        Assert.Equal(2, zen.Count);
        Assert.Contains(Elements(zen, 1).OfType<SlideShape>(), s => ShapeText(s) == "Second map topic");
        using var xmind8 = XMind(MindMapTests.XMind8());
        Assert.Contains(Elements(xmind8).OfType<SlideShape>(), s => ShapeText(s) == "③ Research");
        Assert.Single(Elements(xmind8).OfType<SlideShape>(), s => s.IsPicture);
    }

    [Fact]
    public void An_xmind_8_map_reads_its_styles_relationships_and_boundaries()
    {
        const string content = """
            <?xml version="1.0" encoding="UTF-8" standalone="no"?>
            <xmap-content xmlns="urn:xmind:xmap:xmlns:content:2.0" version="2.0">
              <sheet id="s1" theme="theme1">
                <topic id="root" structure-class="org.xmind.ui.map.unbalanced"><title>Centre</title>
                  <children><topics type="attached">
                    <topic id="a" style-id="red"><title>Alpha</title>
                      <children><topics type="attached"><topic id="a1"><title>One</title></topic><topic id="a2"><title>Two</title></topic></topics></children>
                      <boundaries><boundary id="bd" range="(0,1)"><title>Both</title></boundary></boundaries>
                    </topic>
                    <topic id="b" branch="folded"><title>Beta</title>
                      <children><topics type="attached"><topic id="b1"><title>Hidden</title></topic></topics></children>
                    </topic>
                  </topics></children>
                </topic>
                <relationships><relationship id="r" end1="a1" end2="b1"><title>see</title></relationship></relationships>
                <title>Sheet</title>
              </sheet>
            </xmap-content>
            """;
        const string styles = """
            <?xml version="1.0" encoding="UTF-8" standalone="no"?>
            <xmap-styles xmlns="urn:xmind:xmap:xmlns:style:2.0" xmlns:fo="http://www.w3.org/1999/XSL/Format" xmlns:svg="http://www.w3.org/2000/svg" version="2.0">
              <styles><style id="red" type="topic"><topic-properties svg:fill="#C62828" fo:color="#FFFFFF" shape-class="org.xmind.topicShape.ellipse"/></style></styles>
              <master-styles><style id="theme1" type="theme"><theme-properties><default-style style-family="centralTopic" style-id="central"/></theme-properties></style></master-styles>
              <automatic-styles><style id="central" type="topic"><topic-properties svg:fill="#004D40" fo:color="#FFFFFF"/></style></automatic-styles>
            </xmap-styles>
            """;
        using var pages = XMind(MindMapTests.Zip(new Dictionary<string, byte[]>
        {
            ["content.xml"] = Encoding.UTF8.GetBytes(content),
            ["styles.xml"] = Encoding.UTF8.GetBytes(styles),
        }));

        var topics = Topics(pages, ["Centre", "Alpha", "One", "Two", "Beta"]);
        Assert.Equal(5, topics.Count);
        AssertNoOverlap(topics.Values);
        Assert.Equal(new SKColor(0x00, 0x4D, 0x40), ((SolidFill)topics["Centre"].Fill!).Color);
        Assert.Equal(new SKColor(0xC6, 0x28, 0x28), ((SolidFill)topics["Alpha"].Fill!).Color);
        var shapes = Elements(pages).OfType<SlideShape>().ToList();
        Assert.DoesNotContain(shapes, s => ShapeText(s) == "Hidden");
        Assert.Contains(shapes, s => ShapeText(s) == "Both");
        // The relationship to a hidden topic ends at the folded one.
        Assert.Contains(shapes, s => ShapeText(s) == "see");
    }

    [Fact]
    public void A_large_map_gets_a_page_its_size_scaled_to_the_largest_pdf_page()
    {
        // 1,400 subtopics under two main topics: a map far taller than PDF's 200 inches.
        var xml = new StringBuilder("<map><node TEXT=\"Root\">");
        foreach (var side in new[] { "left", "right" })
        {
            xml.Append($"<node TEXT=\"{side}\" POSITION=\"{side}\">");
            for (var i = 0; i < 700; i++)
                xml.Append($"<node TEXT=\"{side} topic {i}\"/>");
            xml.Append("</node>");
        }
        xml.Append("</node></map>");
        using var pages = FreeMind(xml.ToString());

        var deck = pages.Decks[0];
        Assert.Equal(MindMapDrawing.MaxPageSide, Math.Max(deck.Width, deck.Height), 0.5f);
        var group = (SlideGroup)deck.Slides[0].Elements[0];
        Assert.True(group.ChildBounds.Height > MindMapDrawing.MaxPageSide);
        AssertNoOverlap(group.Children.OfType<SlideShape>().Where(s => ShapeText(s).Contains("topic", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Mind_maps_convert_to_pdf_png_and_svg_through_the_drawing()
    {
        var folder = fx.NewFolder();
        var mm = Path.Combine(folder, "trip.mm");
        await File.WriteAllTextAsync(mm, StyledFreeMind, Token);
        var xmind = Path.Combine(folder, "launch.xmind");
        await File.WriteAllBytesAsync(xmind, StyledXMind(), Token);
        var catalog = WithoutPandoc(fx);

        foreach (var (source, expected) in new[] { (mm, "Haeundae"), (xmind, "Schedule") })
        {
            var pdf = await ConvertAsync(catalog, source, "pdf");
            using (var document = PigDocument.Open(pdf))
            {
                var page = document.GetPage(1);
                Assert.Contains(expected, page.Text, StringComparison.Ordinal);
                Assert.True(page.Width > page.Height, "a map spread to both sides is wider than tall");
            }
            var png = await ConvertAsync(catalog, source, "png");
            using (var bitmap = SKBitmap.Decode(png))
                Assert.True(bitmap.Width > 300 && bitmap.Height > 100);
            var svg = await File.ReadAllTextAsync(await ConvertAsync(catalog, source, "svg"), Token);
            Assert.Contains("<svg", svg, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Drawn_routes_for_pdf_and_pictures_outline_routes_for_documents()
    {
        var planner = WithoutPandoc(fx).CreatePlanner();
        foreach (var format in MindMapReader.Formats)
        {
            foreach (var to in new[] { "pdf", "png", "svg" })
                Assert.Equal("drawing", Assert.Single(planner.Plan(format, to)!.Steps).Converter.Id);
            Assert.Equal("docx-writer", Assert.Single(planner.Plan(format, "docx")!.Steps).Converter.Id);
            Assert.Equal("markup", Assert.Single(planner.Plan(format, "md")!.Steps).Converter.Id);
        }
    }

    [Fact]
    public async Task A_mm_file_of_objective_c_source_becomes_pages_of_its_text()
    {
        var source = Path.Combine(fx.NewFolder(), "View.mm");
        await File.WriteAllTextAsync(source, "#import \"View.h\"\n@implementation View\n- (void)draw {}\n@end\n", Token);
        using (var pages = MindMapPages.Open(source, MindMapReader.FreeMind, Token))
        {
            Assert.Empty(pages.Decks);
            Assert.Equal(1, pages.Count);
        }
        var pdf = await ConvertAsync(WithoutPandoc(fx), source, "pdf");
        using var document = PigDocument.Open(pdf);
        Assert.Contains("implementation", document.GetPage(1).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Hostile_maps_are_refused_quickly()
    {
        var started = DateTime.UtcNow;
        // More topics than the drawing draws: refused before any is measured.
        var many = "<map><node TEXT=\"r\">" + string.Concat(Enumerable.Repeat("<node TEXT=\"x\"/>", MindMapDrawing.MaxTopics + 1)) + "</node></map>";
        var refused = Assert.Throws<ConversionRefusedException>(() => FreeMind(many));
        Assert.Equal(DocumentLimits.ErrorKey, refused.ErrorKey);
        // Most of them folded away: what is drawn is what counts.
        var folded = "<map><node TEXT=\"r\"><node TEXT=\"f\" FOLDED=\"true\">" + string.Concat(Enumerable.Repeat("<node TEXT=\"x\"/>", MindMapDrawing.MaxTopics + 1))
                     + "</node></node></map>";
        using (var pages = FreeMind(folded))
            Assert.Contains(Elements(pages).OfType<SlideShape>(), s => ShapeText(s) == "99+");

        // More relationships than are drawn, and a sheet of arrow links to topics that aren't there.
        var relationships = Enumerable.Range(0, MindMapDrawing.MaxMarks + 1).Select(i => new { id = $"r{i}", end1Id = "a", end2Id = "b" }).ToArray();
        var sheet = new { id = "s", rootTopic = new { id = "a", title = "a", children = new { attached = new[] { new { id = "b", title = "b" } } } }, relationships };
        var zip = MindMapTests.Zip(new Dictionary<string, byte[]> { ["content.json"] = JsonSerializer.SerializeToUtf8Bytes(new[] { sheet }) });
        Assert.Equal(DocumentLimits.ErrorKey, Assert.Throws<ConversionRefusedException>(() => XMind(zip)).ErrorKey);
        var links = "<map><node TEXT=\"r\" ID=\"r\">" + string.Concat(Enumerable.Repeat("<arrowlink DESTINATION=\"nowhere\"/>", 5000)) + "</node></map>";
        using (var pages = FreeMind(links))
            Assert.Single(Elements(pages).OfType<SlideShape>(), s => ShapeText(s) == "r");

        // Absurd numbers in styles and positions are ignored or kept within bounds.
        var odd = """
            <map><node TEXT="r"><font SIZE="1e30"/><edge WIDTH="-5"/><node TEXT="c" BACKGROUND_COLOR="#zzzzzz" BORDER_WIDTH="NaN"><edge WIDTH="99999"/></node></node></map>
            """;
        using (var pages = FreeMind(odd))
            Assert.True(pages.Decks[0].Width < 1000);

        // A map nested deeper than the XML limit is refused by the reader, as for the outline.
        var deep = "<map>" + string.Concat(Enumerable.Repeat("<node TEXT=\"x\">", 100_000)) + string.Concat(Enumerable.Repeat("</node>", 100_000)) + "</map>";
        Assert.Throws<ConversionRefusedException>(() => FreeMind(deep));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(60), "the refusals took too long");
    }

    [Fact]
    public void Deep_maps_are_drawn_without_recursion()
    {
        const int depth = 400;
        var xml = "<map>" + string.Concat(Enumerable.Range(0, depth).Select(i => $"<node TEXT=\"n{i}\">")) + string.Concat(Enumerable.Repeat("</node>", depth)) + "</map>";
        using var pages = FreeMind(xml);
        var shapes = Elements(pages).OfType<SlideShape>().Where(s => ShapeText(s).StartsWith('n')).ToList();
        Assert.Equal(depth, shapes.Count);
        Assert.Equal(MindMapDrawing.MaxPageSide, pages.Decks[0].Width, 0.5f);
    }
}
