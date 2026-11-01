// Org mode without Pandoc (Hwp/Hwpx/Markup/OrgReader, OrgWriter): a file written here the way Emacs users keep notes
// (title, headlines with TODO keywords and tags, drawers, lists with check boxes, blocks, tables, footnotes) reads into
// the model, round-trips through the writer, converts to DOCX / HWPX / PDF / HTML / Markdown with Pandoc absent, and
// hostile files are read in time or refused.

using System.Diagnostics;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Markup;
using static Filee.Engines.Tests.MarkupTestKit;

namespace Filee.Engines.Tests;

[Collection("EngineEnvironment")] // the PDF check turns the Filee renderer on, which is process-wide
public class OrgTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    internal const string Notes = """
        #+TITLE: Weekly notes
        #+AUTHOR: Someone

        * TODO Plan the release                                        :work:
          SCHEDULED: <2026-11-02 Mon>
          :PROPERTIES:
          :ID: 1234
          :END:
        Ship *today*, /test/ it, _mark_ it, +drop+ the rest; run ~filee --help~ or =make=.
        See [[https://filee.sh/][the site]] and https://example.invalid/page; H_{2}O and x^{2}.
        A note follows[fn:1].

        - first
          - nested
        - [X] done item
        - term :: its meaning

        1. one
        2. two

        #+BEGIN_SRC python
        print("hi")
        #+END_SRC

        #+BEGIN_QUOTE
        A quoted line.
        #+END_QUOTE

        : fixed width

        | Name  | Size |
        |-------+------|
        | Seoul |  9.7 |

        -----

        ** Sub heading
        Line one\\
        line two
        # a comment

        [fn:1] The note's text.
        """;

    private HDocument Read(string text, DocumentLimits? limits = null) => OrgReader.Read(text, fx.NewFolder(), limits ?? DocumentLimits.Default);

    [Fact]
    public void A_file_becomes_the_model()
    {
        var document = Read(Notes);
        var paragraphs = Paragraphs(document);

        Assert.Equal("Weekly notes", document.Title);
        Assert.Equal([(1, "TODO Plan the release"), (2, "Sub heading")], paragraphs.Where(p => p.HeadingLevel > 0).Select(p => (p.HeadingLevel, InlineText(p.Inlines))));
        var body = paragraphs[1];
        Assert.Equal("Ship today, test it, mark it, drop the rest; run filee --help or make. See the site and https://example.invalid/page; H2O and x2. "
                     + "A note follows.", InlineText(body.Inlines));
        var runs = body.Inlines.OfType<HText>().ToList();
        Assert.True(runs.Single(r => r.Text == "today").Format.Bold);
        Assert.True(runs.Single(r => r.Text == "test").Format.Italic);
        Assert.True(runs.Single(r => r.Text == "mark").Format.Underline);
        Assert.True(runs.Single(r => r.Text == "drop").Format.Strike);
        Assert.Equal(HtmlReader.CodeShade, runs.Single(r => r.Text == "filee --help").Format.Shade);
        Assert.True(runs.Single(r => r.Text == "2" && r.Format.Subscript is true).Format.Subscript);
        Assert.Equal(["https://filee.sh/", "https://example.invalid/page"], body.Inlines.OfType<HLink>().Select(l => l.Target));
        Assert.Equal("The note's text.", InlineText(((HParagraph)body.Inlines.OfType<HNote>().Single().Blocks.Single()).Inlines));

        Assert.Equal([("first", 0, true), ("nested", 1, true), ("☑ done item", 0, true), ("term: its meaning", 0, true), ("one", 0, false), ("two", 0, false)],
            paragraphs.Where(p => p.List is not null).Select(p => (InlineText(p.Inlines), p.List!.Level, p.List.Numbering.Levels[p.List.Level].Bullet)));
        Assert.Equal(["print(\"hi\")", "fixed width"], paragraphs.Where(MarkupBlocks.IsCode).Select(p => InlineText(p.Inlines)));
        Assert.Equal(MarkupBlocks.QuoteIndent, paragraphs.Single(p => InlineText(p.Inlines) == "A quoted line.").Format.Left);
        var table = document.Sections[0].Blocks.OfType<HTable>().Single();
        Assert.True(table.Rows[0].Header);
        Assert.False(table.Rows[1].Header);
        Assert.Equal(["Seoul", "9.7"], table.Rows[1].Cells.Select(c => InlineText(((HParagraph)c.Blocks.Single()).Inlines)));
        Assert.Contains(paragraphs, p => p.Inlines.Any(i => i is HShape { Kind: HShapeKind.Line }));
        Assert.Equal("Line one\nline two", InlineText(paragraphs.Single(p => InlineText(p.Inlines).StartsWith("Line one", StringComparison.Ordinal)).Inlines));
        // Drawers, planning lines, keywords and comments are not text.
        Assert.DoesNotContain(Texts(document), t => t.Contains("SCHEDULED", StringComparison.Ordinal) || t.Contains("1234", StringComparison.Ordinal)
                                                     || t.Contains("Someone", StringComparison.Ordinal) || t.Contains("comment", StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_round_trips_through_the_writer()
    {
        var first = Read(Notes);
        var written = OrgWriter.Write(first, _ => null);
        var second = Read(written);

        Assert.Equal(first.Title, second.Title);
        Assert.Equal(MarkupShape.Of(first), MarkupShape.Of(second));
        Assert.Equal(written, OrgWriter.Write(second, _ => null));
    }

    [Fact]
    public void Text_that_looks_like_markup_stays_text()
    {
        var document = new HDocument();
        var section = new HSection();
        foreach (var text in new[]
                 {
                     "a *star*, /slash/ and /path/to, _under_ +plus+ =eq= ~tilde~ and [[not a link]]", "* not a headline", "- not a list",
                     "| not a table |", "#+TITLE: not a keyword", ": not fixed", "-----", "[fn:1] not a note", "x^{2} and y_{3} and \\\\ end",
                     "https://example.invalid/x",
                 })
        {
            var paragraph = new HParagraph();
            paragraph.Inlines.Add(new HText(text, default));
            section.Blocks.Add(paragraph);
        }
        document.Sections.Add(section);

        var back = Read(OrgWriter.Write(document, _ => null));

        Assert.Equal(MarkupShape.Of(document), MarkupShape.Of(back));
    }

    [Fact]
    public async Task Files_convert_without_pandoc()
    {
        var source = Path.Combine(fx.NewFolder(), "notes.org");
        await File.WriteAllTextAsync(source, Notes, TestContext.Current.CancellationToken);

        await ConvertsEverywhereWithoutPandocAsync(fx, source, "Plan the release", "print(\"hi\")", "Seoul");
    }

    [Fact]
    public void Hostile_files_are_read_in_time_or_refused()
    {
        // Quote blocks and lists nested past the nesting limit.
        var quotes = string.Concat(Enumerable.Repeat("#+BEGIN_QUOTE\n", 40)) + "x\n" + string.Concat(Enumerable.Repeat("#+END_QUOTE\n", 40));
        Assert.Throws<ConversionRefusedException>(() => Read(quotes));
        var lists = string.Concat(Enumerable.Range(0, 40).Select(n => new string(' ', n * 2) + "- item\n"));
        Assert.Throws<ConversionRefusedException>(() => Read(lists));

        var watch = Stopwatch.StartNew();
        Read(string.Concat(Enumerable.Repeat(" *a /b _c +d =e ~f [[g ", 200_000)));
        Read(new string('*', 100_000) + " deep headline");
        var wide = Read("|" + string.Concat(Enumerable.Repeat("x|", 20_000)));
        Assert.Equal(DocumentLimits.Default.MaxTableColumns, wide.Sections[0].Blocks.OfType<HTable>().Single().Rows[0].Cells.Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"{watch.Elapsed}");

        var limits = DocumentLimits.Default with { MaxParagraphs = 100 };
        Assert.Throws<ConversionRefusedException>(() => Read(string.Concat(Enumerable.Repeat("| a | b |\n", 100)), limits));
    }
}
