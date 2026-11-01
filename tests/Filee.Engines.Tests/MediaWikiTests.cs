// MediaWiki markup without Pandoc (Hwp/Hwpx/Markup/MediaWikiReader, MediaWikiWriter): a page written here the way
// Wikipedia editors write one reads into the model (templates left as written), round-trips through the writer,
// converts to DOCX / HWPX / PDF / HTML / Markdown with Pandoc absent, and hostile pages (tables and quotes nested
// without end, templates and apostrophes by the million) are read in time or refused.

using System.Diagnostics;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Markup;
using static Filee.Engines.Tests.MarkupTestKit;

namespace Filee.Engines.Tests;

[Collection("EngineEnvironment")] // the PDF check turns the Filee renderer on, which is process-wide
public class MediaWikiTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    internal const string Page = """
        {{Infobox city|name=Seoul|population={{formatnum:9700000}}}}
        '''Seoul''' is the ''capital'' of [[South Korea]]s, see [https://www.seoul.go.kr the city's site].<ref>Census, 2020.</ref>

        It has <s>no</s> <sup>many</sup> <code>parks</code>.<!-- a hidden remark -->
        __TOC__

        == History ==
        * Joseon
        ** Hanyang
        *# first king
        # numbered
        ; Term : its definition

        <blockquote>A quoted line.</blockquote>

         code line one
         code line two

        <pre>
        int x = 1 &lt; 2;
        </pre>

        {| class="wikitable"
        |+ Districts
        ! Name !! People
        |-
        | Jongno || 150,000
        |-
        | colspan="2" | Both columns
        |-
        |
        {|
        | inner
        |}
        |}

        ----
        [[Category:Cities]]
        <references />
        """;

    private HDocument Read(string text, DocumentLimits? limits = null) => MediaWikiReader.Read(text, fx.NewFolder(), limits ?? DocumentLimits.Default);

    [Fact]
    public void A_page_becomes_the_model()
    {
        var document = Read(Page);
        var paragraphs = Paragraphs(document);

        // Templates are not expanded: the call stays as written, nested ones included.
        Assert.Equal("{{Infobox city|name=Seoul|population={{formatnum:9700000}}}} Seoul is the capital of South Koreas, see the city's site.",
            InlineText(paragraphs[0].Inlines));
        var runs = paragraphs[0].Inlines.OfType<HText>().ToList();
        Assert.True(runs.Single(r => r.Text == "Seoul").Format.Bold);
        Assert.True(runs.Single(r => r.Text == "capital").Format.Italic);
        var links = paragraphs[0].Inlines.OfType<HLink>().ToList();
        Assert.Equal(["South_Korea", "https://www.seoul.go.kr"], links.Select(l => l.Target));
        Assert.Equal("South Koreas", InlineText(links[0].Content)); // the link trail
        Assert.Equal("Census, 2020.", InlineText(((HParagraph)paragraphs[0].Inlines.OfType<HNote>().Single().Blocks.Single()).Inlines));
        Assert.Equal("It has no many parks.", InlineText(paragraphs[1].Inlines));
        Assert.True(paragraphs[1].Inlines.OfType<HText>().Single(t => t.Text == "no").Format.Strike);
        Assert.Equal(HtmlReader.CodeShade, paragraphs[1].Inlines.OfType<HText>().Single(t => t.Text == "parks").Format.Shade);

        Assert.Equal((2, "History"), (paragraphs[2].HeadingLevel, InlineText(paragraphs[2].Inlines)));
        Assert.Equal([("Joseon", 0, true), ("Hanyang", 1, true), ("first king", 1, false), ("numbered", 0, false)],
            paragraphs.Where(p => p.List is not null).Select(p => (InlineText(p.Inlines), p.List!.Level, p.List.Numbering.Levels[p.List.Level].Bullet)));
        Assert.True(paragraphs.Single(p => InlineText(p.Inlines) == "Term").Inlines.OfType<HText>().Single().Format.Bold);
        Assert.Equal(MarkupBlocks.QuoteIndent, paragraphs.Single(p => InlineText(p.Inlines) == "its definition").Format.Left);
        Assert.Equal(MarkupBlocks.QuoteIndent, paragraphs.Single(p => InlineText(p.Inlines) == "A quoted line.").Format.Left);
        Assert.Equal(["code line one", "code line two", "int x = 1 < 2;"], paragraphs.Where(MarkupBlocks.IsCode).Select(p => InlineText(p.Inlines)));

        var table = document.Sections[0].Blocks.OfType<HTable>().Single();
        Assert.Equal("Districts", InlineText(((HParagraph)table.Caption.Single()).Inlines));
        Assert.True(table.Rows[0].Header);
        Assert.Equal(["Name", "People"], table.Rows[0].Cells.Select(c => InlineText(((HParagraph)c.Blocks.Single()).Inlines)));
        Assert.Equal(["Jongno", "150,000"], table.Rows[1].Cells.Select(c => InlineText(((HParagraph)c.Blocks.Single()).Inlines)));
        Assert.Equal(2, table.Rows[2].Cells.Single().ColSpan);
        Assert.Equal("inner", InlineText(((HParagraph)table.Rows[3].Cells.Single().Blocks.OfType<HTable>().Single().Rows[0].Cells[0].Blocks.Single()).Inlines));
        // The rule; categories, magic words, comments and the references list are gone.
        Assert.Contains(paragraphs, p => p.Inlines.Any(i => i is HShape { Kind: HShapeKind.Line }));
        Assert.DoesNotContain(Texts(document), t => t.Contains("Category", StringComparison.Ordinal) || t.Contains("TOC", StringComparison.Ordinal)
                                                     || t.Contains("hidden", StringComparison.Ordinal) || t.Contains("references", StringComparison.Ordinal));
    }

    [Fact]
    public void A_page_round_trips_through_the_writer()
    {
        var first = Read(Page);
        var written = MediaWikiWriter.Write(first, _ => null);
        var second = Read(written);

        Assert.Equal(MarkupShape.Of(first), MarkupShape.Of(second));
        Assert.Equal(written, MediaWikiWriter.Write(second, _ => null));
    }

    [Fact]
    public void Text_that_looks_like_markup_stays_text()
    {
        var document = new HDocument();
        var section = new HSection();
        foreach (var text in new[]
                 {
                     "it''s ''not'' italic and '''not''' bold", "[[not a link]] and [https://example.invalid no] link", "{{not a template}}",
                     "* not a list", "== not a heading ==", "a | bar, ~~~~ and __TOC__ & <b>tags</b>", " leading space",
                 })
        {
            var paragraph = new HParagraph();
            paragraph.Inlines.Add(new HText(text, default));
            section.Blocks.Add(paragraph);
        }
        document.Sections.Add(section);

        var back = Read(MediaWikiWriter.Write(document, _ => null));

        Assert.Equal(MarkupShape.Of(document), MarkupShape.Of(back));
    }

    [Fact]
    public async Task Pages_convert_without_pandoc()
    {
        var source = Path.Combine(fx.NewFolder(), "seoul.wiki");
        await File.WriteAllTextAsync(source, Page, TestContext.Current.CancellationToken);

        await ConvertsEverywhereWithoutPandocAsync(fx, source, "History", "Jongno", "{{Infobox city");
    }

    [Fact]
    public void Hostile_pages_are_read_in_time_or_refused()
    {
        // Tables nested past the nesting limit, and quotes.
        var tables = string.Concat(Enumerable.Repeat("{|\n|\n", 40)) + "x\n" + string.Concat(Enumerable.Repeat("|}\n", 40));
        Assert.Throws<ConversionRefusedException>(() => Read(tables));
        var quotes = string.Concat(Enumerable.Repeat("<blockquote>", 1000)) + "x" + string.Concat(Enumerable.Repeat("</blockquote>", 1000));
        Assert.Throws<ConversionRefusedException>(() => Read(quotes));

        var watch = Stopwatch.StartNew();
        // Templates and links opened a million times without closing, apostrophes, an unclosed comment.
        Read(string.Concat(Enumerable.Repeat("{{", 500_000)));
        Read(string.Concat(Enumerable.Repeat("[[a", 300_000)));
        Read(string.Concat(Enumerable.Repeat("''x'''", 300_000)));
        Read(string.Concat(Enumerable.Repeat("<ref>", 200_000)));
        // Lines that almost match: a heading without its end, an external link without its bracket.
        Read("=" + new string('a', 1_000_000) + "\n\n" + string.Concat(Enumerable.Repeat("[http://x " + new string(' ', 3_000), 300)));
        Assert.Equal(["before"], Texts(Read("before\n<!-- never closed\n== gone ==")));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"{watch.Elapsed}");

        var limits = DocumentLimits.Default with { MaxParagraphs = 100 };
        Assert.Throws<ConversionRefusedException>(() => Read("{|\n" + string.Concat(Enumerable.Repeat("|-\n| a || b\n", 100)) + "|}", limits));
    }
}
