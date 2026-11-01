// OPML outlines without Pandoc (Hwp/Hwpx/Markup/OpmlReader, OpmlWriter): outlines written here as outliners and feed
// readers save them read into headings with their notes, round-trip through the writer, convert to DOCX / HWPX / PDF
// / HTML / Markdown with Pandoc absent, and hostile XML (entities, DTDs, nesting) is refused.

using System.Text;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Markup;
using static Filee.Engines.Tests.MarkupTestKit;

namespace Filee.Engines.Tests;

[Collection("EngineEnvironment")] // the PDF check turns the Filee renderer on, which is process-wide
public class OpmlTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    internal const string Plan = """
        <?xml version="1.0" encoding="UTF-8"?>
        <opml version="2.0">
          <head><title>Project plan</title><dateCreated>Mon, 02 Nov 2026 09:00:00 GMT</dateCreated></head>
          <body>
            <outline text="Research" _note="Read the **papers** first.&#10;&#10;- one&#10;- two">
              <outline text="Interviews"><outline text="Ask &lt;b&gt;five&lt;/b&gt; people"/></outline>
            </outline>
            <outline text="Filee site" type="link" url="https://filee.sh/"/>
            <outline text="Feeds">
              <outline text="Engine news" type="rss" xmlUrl="https://example.invalid/feed.xml" htmlUrl="https://example.invalid/"/>
            </outline>
          </body>
        </opml>
        """;

    private static HDocument Read(string xml, DocumentLimits? limits = null) =>
        OpmlReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(xml)), Path.GetTempPath(), Path.GetTempPath(), limits ?? DocumentLimits.Default);

    [Fact]
    public void Outlines_become_headings_with_their_notes()
    {
        var document = Read(Plan);

        Assert.Equal("Project plan", document.Title);
        var paragraphs = Paragraphs(document);
        Assert.Equal([(1, "Research"), (2, "Interviews"), (3, "Ask five people"), (1, "Filee site"), (1, "Feeds"), (2, "Engine news")],
            paragraphs.Where(p => p.HeadingLevel > 0).Select(p => (p.HeadingLevel, InlineText(p.Inlines))));
        // The note is Markdown; the outline's HTML text keeps its bold.
        Assert.True(paragraphs.Single(p => InlineText(p.Inlines) == "Read the papers first.").Inlines.OfType<HText>().Single(t => t.Text == "papers").Format.Bold);
        Assert.Equal(2, paragraphs.Count(p => p.List is not null));
        Assert.True(paragraphs.Single(p => p.HeadingLevel == 3).Inlines.OfType<HText>().Single(t => t.Text == "five").Format.Bold);
        // Links: the outline of type link, the feed's site and its address.
        Assert.Equal("https://filee.sh/", paragraphs.Single(p => InlineText(p.Inlines) == "Filee site").Inlines.OfType<HLink>().Single().Target);
        Assert.Equal("https://example.invalid/", paragraphs.Single(p => InlineText(p.Inlines) == "Engine news").Inlines.OfType<HLink>().Single().Target);
        Assert.Equal("https://example.invalid/feed.xml", paragraphs.Single(p => InlineText(p.Inlines).StartsWith("Feed:", StringComparison.Ordinal))
            .Inlines.OfType<HLink>().Single().Target);
    }

    [Fact]
    public void An_outline_round_trips_through_the_writer()
    {
        var first = Read(Plan);
        var written = OpmlWriter.Write(first, _ => null);
        var second = Read(written);

        Assert.Equal(first.Title, second.Title);
        Assert.Equal(MarkupShape.Of(first), MarkupShape.Of(second));
        Assert.Equal(written, OpmlWriter.Write(second, _ => null));
        Assert.Contains("type=\"link\" url=\"https://filee.sh/\"", written, StringComparison.Ordinal);

        // A document whose text comes before its first heading keeps it in an outline without text.
        var document = new HDocument();
        var section = new HSection();
        var intro = new HParagraph();
        intro.Inlines.Add(new HText("Intro text", default));
        section.Blocks.Add(intro);
        document.Sections.Add(section);
        Assert.Equal(["Intro text"], Texts(Read(OpmlWriter.Write(document, _ => null))));
    }

    [Fact]
    public async Task Outlines_convert_without_pandoc()
    {
        var source = Path.Combine(fx.NewFolder(), "plan.opml");
        await File.WriteAllTextAsync(source, Plan, TestContext.Current.CancellationToken);

        await ConvertsEverywhereWithoutPandocAsync(fx, source, "Research", "Interviews", "papers");
    }

    [Fact]
    public void Hostile_xml_is_refused()
    {
        // Entities that expand a billion times, and one from outside the file: no DTD is read at all.
        var laughs = "<?xml version=\"1.0\"?><!DOCTYPE opml [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">"
                     + "<!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;\"><!ENTITY d \"&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;\">]>"
                     + "<opml version=\"2.0\"><body><outline text=\"&d;&d;&d;&d;&d;&d;&d;&d;&d;&d;\"/></body></opml>";
        Assert.Throws<InvalidDataException>(() => Read(laughs));
        Assert.Throws<InvalidDataException>(() => Read("<?xml version=\"1.0\"?><!DOCTYPE opml SYSTEM \"http://example.invalid/x.dtd\"><opml><body/></opml>"));
        Assert.Throws<InvalidDataException>(() => Read("<html><body>not an outline</body></html>"));
        Assert.Throws<InvalidDataException>(() => Read("<opml><body><outline text=\"cut"));

        // Nested a hundred thousand outlines deep: refused by the XML limits before anything is built.
        var deep = "<opml version=\"2.0\"><body>" + string.Concat(Enumerable.Repeat("<outline text=\"x\">", 100_000))
                   + string.Concat(Enumerable.Repeat("</outline>", 100_000)) + "</body></opml>";
        Assert.Throws<ConversionRefusedException>(() => Read(deep));

        // Outlines past the paragraph limit, and a note of more lines than paragraphs are left.
        var limits = DocumentLimits.Default with { MaxParagraphs = 50 };
        var many = "<opml version=\"2.0\"><body>" + string.Concat(Enumerable.Repeat("<outline text=\"x\"/>", 100)) + "</body></opml>";
        Assert.Throws<ConversionRefusedException>(() => Read(many, limits));
        var note = "<opml version=\"2.0\"><body><outline text=\"x\" _note=\"" + string.Concat(Enumerable.Repeat("a&#10;&#10;", 100)) + "\"/></body></opml>";
        Assert.Throws<ConversionRefusedException>(() => Read(note, limits));
    }
}
