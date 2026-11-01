// Textile without Pandoc (Hwp/Hwpx/Markup/TextileReader, TextileWriter): a page written here the way Redmine and
// Textpattern users write it reads into the model, round-trips through the writer (text that looks like markup
// included), converts to DOCX / HWPX / PDF / HTML / Markdown with Pandoc absent, and hostile pages are read in time
// or refused.

using System.Diagnostics;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Markup;
using static Filee.Engines.Tests.MarkupTestKit;

namespace Filee.Engines.Tests;

[Collection("EngineEnvironment")] // the PDF check turns the Filee renderer on, which is process-wide
public class TextileTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    internal const string Page = """
        h1. Release notes

        The build is *fast*, _clean_ and -slow- +new+. H ~2~ O and x ^2^ with @code()@.
        See "the site":https://filee.sh/ and ["the guide":https://filee.sh/guide?a=1].
        A second line[1].

        * first
        ** nested
        *# numbered inside
        * second

        # one
        # two

        bq. A quoted remark.

        bc. dotnet build
        dotnet test

        |_. Name |_. Size |
        | Seoul | 9.7 |
        |\2. Both columns |

        h2. Details &amp; more

        fn1. The note's text.
        """;

    private HDocument Read(string text, DocumentLimits? limits = null) => TextileReader.Read(text, fx.NewFolder(), limits ?? DocumentLimits.Default);

    [Fact]
    public void A_page_becomes_the_model()
    {
        var document = Read(Page);
        var paragraphs = Paragraphs(document);

        Assert.Equal([(1, "Release notes"), (2, "Details & more")], paragraphs.Where(p => p.HeadingLevel > 0).Select(p => (p.HeadingLevel, InlineText(p.Inlines))));
        var runs = paragraphs[1].Inlines.OfType<HText>().ToList();
        Assert.True(runs.Single(r => r.Text == "fast").Format.Bold);
        Assert.True(runs.Single(r => r.Text == "clean").Format.Italic);
        Assert.True(runs.Single(r => r.Text == "slow").Format.Strike);
        Assert.True(runs.Single(r => r.Text == "new").Format.Underline);
        Assert.True(runs.Single(r => r.Text == "2" && r.Format.Subscript is true).Format.Subscript);
        Assert.Equal(HtmlReader.CodeShade, runs.Single(r => r.Text == "code()").Format.Shade);
        Assert.Equal(["https://filee.sh/", "https://filee.sh/guide?a=1"], paragraphs[1].Inlines.OfType<HLink>().Select(l => l.Target));
        // A line break inside a paragraph is one; the footnote reference has its text.
        Assert.Equal(2, paragraphs[1].Inlines.OfType<HLineBreak>().Count());
        Assert.Equal("The note's text.", InlineText(((HParagraph)paragraphs[1].Inlines.OfType<HNote>().Single().Blocks.Single()).Inlines));

        var items = paragraphs.Where(p => p.List is not null).ToList();
        Assert.Equal([("first", 0, true), ("nested", 1, true), ("numbered inside", 1, false), ("second", 0, true), ("one", 0, false), ("two", 0, false)],
            items.Select(p => (InlineText(p.Inlines), p.List!.Level, p.List.Numbering.Levels[p.List.Level].Bullet)));
        Assert.Equal(MarkupBlocks.QuoteIndent, paragraphs.Single(p => InlineText(p.Inlines) == "A quoted remark.").Format.Left);
        Assert.Equal(["dotnet build", "dotnet test"], paragraphs.Where(MarkupBlocks.IsCode).Select(p => InlineText(p.Inlines)));

        var table = document.Sections[0].Blocks.OfType<HTable>().Single();
        Assert.True(table.Rows[0].Header);
        Assert.Equal(2, table.ColumnCount);
        Assert.Equal(2, table.Rows[2].Cells.Single().ColSpan);
        Assert.Equal("Seoul", InlineText(table.Rows[1].Cells[0].Blocks.OfType<HParagraph>().Single().Inlines));
    }

    [Fact]
    public void A_page_round_trips_through_the_writer()
    {
        var first = Read(Page);
        var written = TextileWriter.Write(first, _ => null);
        var second = Read(written);

        Assert.Equal(MarkupShape.Of(first), MarkupShape.Of(second));
        Assert.Equal(written, TextileWriter.Write(second, _ => null));
    }

    [Fact]
    public void Text_that_looks_like_markup_stays_text()
    {
        var document = new HDocument();
        var section = new HSection();
        foreach (var text in new[]
                 {
                     "a *star* and _under_ score, 2 * 3 - 1 + x ^ y ~ z", "h1. not a heading", "* not a list", "# nor this",
                     "|not|a|table|", "\"quoted\":not-a-link and [1] and !bang! and @at@ and ==eq== and <tag> & amp",
                     "snake_case_name, well-known, C++ and 10%",
                 })
        {
            var paragraph = new HParagraph();
            paragraph.Inlines.Add(new HText(text, default));
            section.Blocks.Add(paragraph);
        }
        document.Sections.Add(section);

        var back = Read(TextileWriter.Write(document, _ => null));

        Assert.Equal(MarkupShape.Of(document), MarkupShape.Of(back));
    }

    [Fact]
    public async Task Pages_convert_without_pandoc()
    {
        var source = Path.Combine(fx.NewFolder(), "notes.textile");
        await File.WriteAllTextAsync(source, Page, TestContext.Current.CancellationToken);

        await ConvertsEverywhereWithoutPandocAsync(fx, source, "Release notes", "dotnet build", "Seoul");
    }

    [Fact]
    public void Hostile_pages_are_read_in_time_or_refused()
    {
        var watch = Stopwatch.StartNew();
        // A million openers without a closer, emphasis nested beyond the limit, a list nested 10,000 deep.
        Read(string.Concat(Enumerable.Repeat("*a _b -c ", 300_000)));
        Read(string.Concat(Enumerable.Repeat("*_-+^~", 5_000)) + "x" + string.Concat(Enumerable.Repeat("~^+-_*", 5_000)));
        // Lines that almost match a block signature or row attributes (a fuzz finding: 73 dashes before a table row
        // took minutes when attributes were runs inside a repeated group).
        Read(new string('-', 5_000) + "| Seoul | 9.7 |\n\ntable" + new string('<', 5_000) + "x\n\np" + string.Concat(Enumerable.Repeat("()", 5_000)) + "x\n\n!"
             + new string('<', 5_000) + "\n\n|" + new string('~', 5_000) + "x. a|");
        var list = Read(new string('*', 10_000) + " deep\n" + new string('#', 64) + " deep too");
        Assert.All(Paragraphs(list).Where(p => p.List is not null), p => Assert.True(p.List!.Level <= 8));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"{watch.Elapsed}");

        // A table wider than the column limit is cut; rows past the paragraph limit are refused.
        var wide = Read("|" + string.Concat(Enumerable.Repeat("x|", 20_000)));
        Assert.Equal(DocumentLimits.Default.MaxTableColumns, wide.Sections[0].Blocks.OfType<HTable>().Single().Rows[0].Cells.Count);
        var limits = DocumentLimits.Default with { MaxParagraphs = 100 };
        Assert.Throws<ConversionRefusedException>(() => Read(string.Concat(Enumerable.Repeat("|a|b|\n", 100)), limits));
        Assert.Throws<ConversionRefusedException>(() => Read(string.Concat(Enumerable.Repeat("p. x\n\n", 200)), limits));
    }
}
