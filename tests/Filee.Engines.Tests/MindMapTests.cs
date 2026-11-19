// Mind maps as outlines (Hwp/Hwpx/Markup/MindMapReader): FreeMind / Freeplane and XMind maps written here as those
// apps save them read into headings, nested lists, notes, links, bookmarks, symbols and pictures; they convert to
// DOCX / HWPX / PDF / HTML / Markdown / TXT / OPML without Pandoc, and hostile maps (entities, nesting, bombs, sizes)
// are refused quickly.

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Markup;
using Filee.Engines.Infrastructure;
using static Filee.Engines.Tests.MarkupTestKit;

namespace Filee.Engines.Tests;

[Collection("EngineEnvironment")] // the PDF check turns the Filee renderer on, which is process-wide
public class MindMapTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>A Freeplane map: icons, rich text, notes, details, attributes, a Markdown note, links, five levels.</summary>
    internal const string FreeMindMap = """
        <map version="freeplane 1.9.13">
        <!--To view this file, download free mind mapping software Freeplane from https://www.freeplane.org -->
        <node TEXT="Launch plan" ID="ID_root" LINK="https://filee.sh/" FOLDED="false">
        <icon BUILTIN="bookmark"/>
        <node TEXT="Research" POSITION="right" ID="ID_research">
        <icon BUILTIN="full-1"/>
        <richcontent TYPE="NOTE"><html><head></head><body><p>Read the <b>papers</b> first.</p></body></html></richcontent>
        <node TEXT="Interviews" ID="ID_interviews">
        <node ID="ID_five"><richcontent TYPE="NODE"><html><head/><body><p>Ask <b>five</b> people</p></body></html></richcontent>
        <node TEXT="Customers&#10;and partners" ID="ID_customers">
        <attribute NAME="owner" VALUE="Mina"/>
        <richcontent TYPE="DETAILS"><html><head/><body><p>Book the rooms</p></body></html></richcontent>
        <node TEXT="Deep detail" ID="ID_deep"/>
        </node>
        </node>
        </node>
        </node>
        <node TEXT="Build" POSITION="left" ID="ID_build">
        <icon BUILTIN="button_ok"/>
        <icon BUILTIN="idea"/>
        <richcontent TYPE="NOTE" CONTENT-TYPE="plain/markdown"><text>Use **Markdown** here</text></richcontent>
        <node TEXT="See research" LINK="#ID_research"/>
        <node TEXT="Scripted" LINK="javascript:alert(1)"/>
        <node TEXT="Missing" LINK="#ID_nowhere"/>
        </node>
        </node>
        </map>
        """;

    /// <summary>An XMind Zen / 2020+ map: two sheets, notes, labels, markers, links, a picture, detached topics.</summary>
    internal static byte[] XMindZen(bool withStub = true, bool encrypted = false)
    {
        object Topic(string id, string title, object[]? children = null, object? extra = null)
        {
            var topic = new Dictionary<string, object?> { ["id"] = id, ["class"] = "topic", ["title"] = title };
            if (children is not null)
                topic["children"] = new Dictionary<string, object> { ["attached"] = children };
            if (extra is not null)
            {
                foreach (var property in extra.GetType().GetProperties())
                    topic[property.Name] = property.GetValue(extra);
            }
            return topic;
        }

        var root = Topic("root", "Launch plan",
        [
            Topic("research", "Research", [Topic("interviews", "Interviews", [Topic("five", "Ask five people", [Topic("deep", "Deep detail")])])],
                new { notes = new { plain = new { content = "plain fallback" }, realHTML = new { content = "<div>Read the <b>papers</b> first.</div>" } },
                      markers = new[] { new { markerId = "priority-2" }, new { markerId = "smiley-smile" } }, labels = new[] { "urgent", "Q1" } }),
            Topic("build", "Build", [Topic("see", "See research", extra: new { href = "xmind:#research" })],
                new { markers = new[] { new { markerId = "task-done" } }, href = "https://filee.sh/", image = new { src = "xap:resources/pic.png" } }),
        ]);
        ((Dictionary<string, object?>)root)["children"] = new Dictionary<string, object>
        {
            ["attached"] = ((Dictionary<string, object>)((Dictionary<string, object?>)root)["children"]!)["attached"],
            ["detached"] = new[] { Topic("float", "Floating idea") },
        };
        var sheets = new object[]
        {
            new { id = "s1", @class = "sheet", title = "Sheet 1", rootTopic = root },
            new { id = "s2", @class = "sheet", title = "Second sheet", rootTopic = Topic("r2", "", [Topic("t2", "Second map topic")]) },
        };
        var parts = new Dictionary<string, byte[]>
        {
            ["content.json"] = JsonSerializer.SerializeToUtf8Bytes(sheets),
            ["metadata.json"] = "{}"u8.ToArray(),
            ["manifest.json"] = Encoding.UTF8.GetBytes(encrypted
                ? """{"file-entries":{"content.json":{"encryption-data":{"algorithm":"AES"}}}}"""
                : """{"file-entries":{"content.json":{},"metadata.json":{}}}"""),
            ["resources/pic.png"] = DocumentFuzzTests.TinyPng(6, 4),
        };
        if (withStub)
            parts["content.xml"] = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><xmap-content xmlns=\"urn:xmind:xmap:xmlns:content:2.0\"><sheet><topic><title>Warning: this file was saved by a newer XMind</title></topic></sheet></xmap-content>");
        return Zip(parts);
    }

    /// <summary>An XMind 8 map (content.xml).</summary>
    internal const string XMind8Content = """
        <?xml version="1.0" encoding="UTF-8" standalone="no"?>
        <xmap-content xmlns="urn:xmind:xmap:xmlns:content:2.0" xmlns:fo="http://www.w3.org/1999/XSL/Format" xmlns:svg="http://www.w3.org/2000/svg" xmlns:xhtml="http://www.w3.org/1999/xhtml" xmlns:xlink="http://www.w3.org/1999/xlink" modified-by="x" timestamp="1" version="2.0">
          <sheet id="s1" timestamp="1">
            <topic id="root" structure-class="org.xmind.ui.map.unbalanced" timestamp="1">
              <title>Launch plan</title>
              <children>
                <topics type="attached">
                  <topic id="research" timestamp="1" xlink:href="https://filee.sh/">
                    <title>Research</title>
                    <marker-refs><marker-ref marker-id="priority-3"/></marker-refs>
                    <labels><label>urgent</label></labels>
                    <notes><plain>Read the papers first.</plain><html><xhtml:p>Read the papers first.</xhtml:p></html></notes>
                    <children><topics type="attached">
                      <topic id="interviews"><title>Interviews</title>
                        <children><topics type="attached"><topic id="five"><title>Ask five people</title>
                          <children><topics type="attached"><topic id="deep"><title>Deep detail</title></topic></topics></children>
                        </topic></topics></children>
                      </topic>
                    </topics></children>
                  </topic>
                  <topic id="build"><title>Build</title>
                    <xhtml:img svg:height="4" svg:width="6" xhtml:src="xap:attachments/pic.png"/>
                    <children><topics type="attached"><topic id="see" xlink:href="xmind:#research"><title>See research</title></topic></topics></children>
                  </topic>
                </topics>
                <topics type="detached"><topic id="float"><title>Floating idea</title></topic></topics>
              </children>
            </topic>
            <title>Sheet 1</title>
          </sheet>
        </xmap-content>
        """;

    internal static byte[] XMind8() => Zip(new Dictionary<string, byte[]>
    {
        ["content.xml"] = Encoding.UTF8.GetBytes(XMind8Content),
        ["META-INF/manifest.xml"] = "<?xml version=\"1.0\"?><manifest xmlns=\"urn:xmind:xmap:xmlns:manifest:1.0\"><file-entry full-path=\"content.xml\" media-type=\"text/xml\"/></manifest>"u8.ToArray(),
        ["attachments/pic.png"] = DocumentFuzzTests.TinyPng(6, 4),
    });

    internal static byte[] Zip(IReadOnlyDictionary<string, byte[]> parts)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in parts)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(bytes);
            }
        }
        return buffer.ToArray();
    }

    private HDocument FreeMind(string xml, DocumentLimits? limits = null) =>
        MindMapReader.ReadFreeMind(Encoding.UTF8.GetBytes(xml), Path.GetTempPath(), fx.NewFolder(), limits ?? DocumentLimits.Default);

    private HDocument XMind(byte[] zip, DocumentLimits? limits = null) =>
        MindMapReader.ReadXMind(new MemoryStream(zip), Path.GetTempPath(), fx.NewFolder(), limits ?? DocumentLimits.Default);

    /// <summary>Each topic line: its heading level, its list level (-1 when it is no list item) and its text.</summary>
    private static List<(int Heading, int List, string Text)> Outline(HDocument document) =>
        [.. Paragraphs(document).Where(p => p.HeadingLevel > 0 || p.List is { Numbered: true })
            .Select(p => (p.HeadingLevel, p.List is { Numbered: true } item ? item.Level : -1, InlineText(p.Inlines)))];

    [Fact]
    public void A_freemind_map_becomes_headings_then_nested_lists()
    {
        var document = FreeMind(FreeMindMap);

        Assert.Equal("Launch plan", document.Title);
        Assert.Equal(
        [
            (1, -1, "★ Launch plan"), (2, -1, "① Research"), (3, -1, "Interviews"), (0, 0, "Ask five people"),
            (0, 1, "Customers\nand partners"), (0, 2, "Deep detail"), (2, -1, "✓ Build"), (3, -1, "See research"), (3, -1, "Scripted"),
            (3, -1, "Missing"),
        ], Outline(document));

        var paragraphs = Paragraphs(document);
        // The central topic's link, the rich text's bold, the HTML note and the Markdown note.
        Assert.Equal("https://filee.sh/", paragraphs[0].Inlines.OfType<HLink>().Single().Target);
        Assert.True(paragraphs.Single(p => InlineText(p.Inlines) == "Ask five people").Inlines.OfType<HText>().Single(t => t.Text == "five").Format.Bold);
        Assert.True(paragraphs.Single(p => InlineText(p.Inlines) == "Read the papers first.").Inlines.OfType<HText>().Single(t => t.Text == "papers").Format.Bold);
        Assert.True(paragraphs.Single(p => InlineText(p.Inlines) == "Use Markdown here").Inlines.OfType<HText>().Single(t => t.Text == "Markdown").Format.Bold);

        // A list item's attributes and details continue it (the same level, no bullet of their own).
        var customers = paragraphs.FindIndex(p => InlineText(p.Inlines) == "Customers\nand partners");
        Assert.Equal(["owner: Mina", "Book the rooms"], paragraphs.Skip(customers + 1).Take(2).Select(p => InlineText(p.Inlines)));
        Assert.All(paragraphs.Skip(customers + 1).Take(2), p => Assert.Equal((1, false), (p.List!.Level, p.List.Numbered)));

        // A link to another topic points at a bookmark on it; a script link and a link to no topic keep only the text.
        var see = paragraphs.Single(p => InlineText(p.Inlines) == "See research").Inlines.OfType<HLink>().Single();
        var bookmark = paragraphs.Single(p => p.HeadingLevel == 2 && InlineText(p.Inlines).EndsWith("Research", StringComparison.Ordinal))
            .Inlines.OfType<HBookmark>().Single();
        Assert.Equal("#" + bookmark.Name, see.Target);
        Assert.Empty(paragraphs.Single(p => InlineText(p.Inlines) == "Scripted").Inlines.OfType<HLink>());
        Assert.Empty(paragraphs.Single(p => InlineText(p.Inlines) == "Missing").Inlines.OfType<HLink>());
    }

    [Fact]
    public void Into_opml_every_topic_is_a_heading()
    {
        var path = Path.Combine(fx.NewFolder(), "plan.mm");
        File.WriteAllText(path, FreeMindMap);

        var document = MindMapReader.Read(path, "mm", fx.NewFolder(), headingsOnly: true);

        Assert.Equal([(4, -1, "Ask five people"), (5, -1, "Customers\nand partners"), (6, -1, "Deep detail")],
            Outline(document).Where(t => t.Heading >= 4));
        Assert.DoesNotContain(Paragraphs(document), p => p.List is { Numbered: true });
    }

    [Fact]
    public void An_xmind_map_reads_its_sheets_notes_markers_labels_links_and_pictures()
    {
        var document = XMind(XMindZen());

        Assert.Equal("Launch plan", document.Title);
        Assert.Equal(
        [
            (1, -1, "Launch plan"), (2, -1, "② Research"), (3, -1, "Interviews"), (0, 0, "Ask five people"), (0, 1, "Deep detail"),
            (2, -1, "✓ Build"), (3, -1, "See research"), (2, -1, "Floating idea"), (1, -1, "Second sheet"), (2, -1, "Second map topic"),
        ], Outline(document));

        var paragraphs = Paragraphs(document);
        // realHTML wins over the plain note; labels follow the topic.
        Assert.True(paragraphs.Single(p => InlineText(p.Inlines) == "Read the papers first.").Inlines.OfType<HText>().Single(t => t.Text == "papers").Format.Bold);
        Assert.DoesNotContain(paragraphs, p => InlineText(p.Inlines) == "plain fallback");
        Assert.Contains(paragraphs, p => InlineText(p.Inlines) == "Labels: urgent, Q1");
        // The web link, the link to another topic, the picture (unpacked) and the second sheet on a new page.
        var build = paragraphs.Single(p => InlineText(p.Inlines) == "✓ Build");
        Assert.Equal("https://filee.sh/", build.Inlines.OfType<HLink>().Single().Target);
        Assert.StartsWith("#topic-research", paragraphs.Single(p => InlineText(p.Inlines) == "See research").Inlines.OfType<HLink>().Single().Target,
            StringComparison.Ordinal);
        var picture = paragraphs[paragraphs.IndexOf(build) + 1].Inlines.OfType<HImage>().Single();
        Assert.True(File.Exists(picture.Path));
        Assert.True(paragraphs.Single(p => InlineText(p.Inlines) == "Second sheet").PageBreakBefore);
        Assert.False(paragraphs[0].PageBreakBefore);

        // Without the compatibility stub the map reads the same; content.json is what newer files hold.
        Assert.Equal(Outline(document), Outline(XMind(XMindZen(withStub: false))));
    }

    [Fact]
    public void An_xmind_8_map_reads_from_content_xml()
    {
        var document = XMind(XMind8());

        Assert.Equal(
        [
            (1, -1, "Launch plan"), (2, -1, "③ Research"), (3, -1, "Interviews"), (0, 0, "Ask five people"), (0, 1, "Deep detail"),
            (2, -1, "Build"), (3, -1, "See research"), (2, -1, "Floating idea"),
        ], Outline(document));
        var paragraphs = Paragraphs(document);
        Assert.Contains(paragraphs, p => InlineText(p.Inlines) == "Read the papers first.");
        Assert.Contains(paragraphs, p => InlineText(p.Inlines) == "Labels: urgent");
        Assert.Single(paragraphs.SelectMany(p => p.Inlines).OfType<HImage>());
        Assert.Equal("https://filee.sh/", paragraphs.Single(p => InlineText(p.Inlines) == "③ Research").Inlines.OfType<HLink>().Single().Target);
    }

    [Fact]
    public void A_password_protected_xmind_file_is_refused_with_its_reason()
    {
        var refused = Assert.Throws<ConversionRefusedException>(() => XMind(XMindZen(encrypted: true)));
        Assert.Equal(MindMapReader.PasswordKey, refused.ErrorKey);
    }

    [Fact]
    public void A_mm_file_that_is_objective_c_source_is_read_as_text()
    {
        var document = FreeMind("#import \"View.h\"\n\n@implementation View\n@end\n");
        Assert.Equal(["#import \"View.h\"", "@implementation View", "@end"], Texts(document));
    }

    [Fact]
    public async Task Mind_maps_convert_without_pandoc()
    {
        var folder = fx.NewFolder();
        var mm = Path.Combine(folder, "plan.mm");
        await File.WriteAllTextAsync(mm, FreeMindMap, TestContext.Current.CancellationToken);
        var xmind = Path.Combine(folder, "plan.xmind");
        await File.WriteAllBytesAsync(xmind, XMindZen(), TestContext.Current.CancellationToken);

        foreach (var source in new[] { mm, xmind })
        {
            await ConvertsEverywhereWithoutPandocAsync(fx, source, "Launch plan", "Research", "papers", "Deep detail");

            var catalog = WithoutPandoc(fx);
            var text = await File.ReadAllTextAsync(await ConvertAsync(catalog, source, "txt"), TestContext.Current.CancellationToken);
            Assert.Contains("Deep detail", text, StringComparison.Ordinal);
            // OPML keeps the whole tree: the deepest topic is an outline six levels down.
            var opml = await ConvertAsync(catalog, source, "opml");
            var outline = OpmlReader.Read(opml, fx.NewFolder());
            var deep = Paragraphs(outline).Single(p => InlineText(p.Inlines) == "Deep detail");
            Assert.Equal(source == mm ? 6 : 5, deep.HeadingLevel);
        }
    }

    [Fact]
    public void Mind_maps_take_the_built_in_routes()
    {
        var planner = WithoutPandoc(fx).CreatePlanner();
        foreach (var format in MindMapReader.Formats)
        {
            foreach (var (to, engine) in new[] { ("md", "markup"), ("html", "markup"), ("txt", "markup"), ("opml", "markup"),
                         ("docx", "docx-writer"), ("odt", "odt-writer"), ("hwpx", "hwpx-writer"), ("pptx", "pptx-writer") })
                Assert.Equal(engine, Assert.Single(planner.Plan(format, to)!.Steps).Converter.Id);
        }

        var renderer = EngineEnvironment.FileeRenderer;
        EngineEnvironment.FileeRenderer = true;
        try
        {
            fx.Catalog.Refresh();
            foreach (var format in MindMapReader.Formats)
                Assert.Equal("layout", Assert.Single(fx.Catalog.CreatePlanner().Plan(format, "pdf")!.Steps).Converter.Id);
        }
        finally
        {
            EngineEnvironment.FileeRenderer = renderer;
            fx.Catalog.Refresh();
        }
    }

    [Fact]
    public void Deep_maps_are_walked_without_recursion_and_lists_stop_at_nine_levels()
    {
        // 400 levels: within the XML depth limit, deeper than any stack-bound walk would like.
        const int depth = 400;
        var xml = "<map>" + string.Concat(Enumerable.Range(0, depth).Select(i => $"<node TEXT=\"n{i}\">")) + string.Concat(Enumerable.Repeat("</node>", depth)) + "</map>";
        var outline = Outline(FreeMind(xml));
        Assert.Equal(depth, outline.Count);
        Assert.Equal((0, 8, "n399"), outline[^1]);

        var json = new StringBuilder("[{\"rootTopic\":");
        for (var i = 0; i < depth; i++)
            json.Append("{\"title\":\"n").Append(i).Append("\",\"children\":{\"attached\":[");
        json.Append("{\"title\":\"leaf\"}");
        for (var i = 0; i < depth; i++)
            json.Append("]}}");
        json.Append("}]");
        Assert.Equal((0, 8, "leaf"), Outline(XMind(Zip(new Dictionary<string, byte[]> { ["content.json"] = Encoding.UTF8.GetBytes(json.ToString()) })))[^1]);
    }

    [Fact]
    public void Hostile_maps_are_refused()
    {
        // Entities that expand a billion times, and one from outside the file: no DTD is read at all.
        var laughs = "<?xml version=\"1.0\"?><!DOCTYPE map [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">"
                     + "<!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;\">]><map><node TEXT=\"&c;&c;&c;&c;\"/></map>";
        Assert.Throws<InvalidDataException>(() => FreeMind(laughs));
        Assert.Throws<InvalidDataException>(() => FreeMind("<?xml version=\"1.0\"?><!DOCTYPE map SYSTEM \"http://example.invalid/x.dtd\"><map/>"));
        Assert.Throws<InvalidDataException>(() => FreeMind("<html><body>not a map</body></html>"));
        Assert.Throws<InvalidDataException>(() => FreeMind("<map><node TEXT=\"cut"));

        // Nested a hundred thousand nodes deep: refused by the XML limits, and in content.json by the JSON depth.
        var started = DateTime.UtcNow;
        var deep = "<map>" + string.Concat(Enumerable.Repeat("<node TEXT=\"x\">", 100_000)) + string.Concat(Enumerable.Repeat("</node>", 100_000)) + "</map>";
        Assert.Throws<ConversionRefusedException>(() => FreeMind(deep));
        var json = new StringBuilder("[{\"rootTopic\":");
        for (var i = 0; i < 100_000; i++)
            json.Append("{\"children\":{\"attached\":[");
        Assert.Throws<ConversionRefusedException>(() => XMind(Zip(new Dictionary<string, byte[]> { ["content.json"] = Encoding.UTF8.GetBytes(json.ToString()) })));
        var xml = new StringBuilder("<xmap-content><sheet><topic>");
        for (var i = 0; i < 100_000; i++)
            xml.Append("<children><topics><topic>");
        Assert.Throws<ConversionRefusedException>(() => XMind(Zip(new Dictionary<string, byte[]> { ["content.xml"] = Encoding.UTF8.GetBytes(xml.ToString()) })));

        // Topics past the paragraph limit, and a note of more lines than paragraphs are left.
        var limits = DocumentLimits.Default with { MaxParagraphs = 50 };
        Assert.Throws<ConversionRefusedException>(() =>
            FreeMind("<map><node TEXT=\"r\">" + string.Concat(Enumerable.Repeat("<node TEXT=\"x\"/>", 100)) + "</node></map>", limits));
        Assert.Throws<ConversionRefusedException>(() => FreeMind(
            "<map><node TEXT=\"r\"><richcontent TYPE=\"NOTE\" CONTENT-TYPE=\"plain/\"><text>" + string.Concat(Enumerable.Repeat("a&#10;", 100))
            + "</text></richcontent></node></map>", limits));

        // A content.json past the part limit, and one that unpacks to far more than it packs (a bomb).
        var small = DocumentLimits.Default with { MaxPartBytes = 1024 * 1024 };
        var big = Encoding.UTF8.GetBytes("[" + new string(' ', 2 * 1024 * 1024) + "]");
        Assert.Throws<ConversionRefusedException>(() => XMind(Zip(new Dictionary<string, byte[]> { ["content.json"] = big }), small));
        var bomb = Encoding.UTF8.GetBytes("[" + new string(' ', 8 * 1024 * 1024) + "]");
        Assert.Throws<ConversionRefusedException>(() => XMind(Zip(new Dictionary<string, byte[]> { ["content.json"] = bomb })));

        // Not a ZIP, a ZIP without a map, a map that isn't JSON or isn't shaped like sheets.
        Assert.Throws<InvalidDataException>(() => XMind("not a zip"u8.ToArray()));
        Assert.Throws<InvalidDataException>(() => XMind(Zip(new Dictionary<string, byte[]> { ["readme.txt"] = "x"u8.ToArray() })));
        Assert.Throws<InvalidDataException>(() => XMind(Zip(new Dictionary<string, byte[]> { ["content.json"] = "[{\"rootTopic\":"u8.ToArray() })));
        Assert.Throws<InvalidDataException>(() => XMind(Zip(new Dictionary<string, byte[]> { ["content.json"] = "\"sheets\""u8.ToArray() })));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30), "the refusals took too long");
    }
}
