// reStructuredText without Pandoc (Hwp/Hwpx/Markup/RstReader, RstWriter): a document written here the way Sphinx and
// docutils users write one (title, sections, lists, literal blocks, directives, grid and simple tables with spans,
// footnotes, targets, substitutions) reads into the model, round-trips through the writer, converts to DOCX / HWPX /
// PDF / HTML / Markdown with Pandoc absent, and hostile documents are read in time or refused.

using System.Diagnostics;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Markup;
using static Filee.Engines.Tests.MarkupTestKit;

namespace Filee.Engines.Tests;

[Collection("EngineEnvironment")] // the PDF check turns the Filee renderer on, which is process-wide
public class RstTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    internal const string Guide = """
        ==============
        Install guide
        ==============

        Getting started
        ===============

        Filee is **fast** and *small*; run ``filee --help``. Water is H\ :sub:`2`\ O and e = mc\ :sup:`2`.
        See `the site <https://filee.sh/>`__, the Docs_ and https://example.invalid/page.
        A note follows [#]_. |project| ships today.

        - first
        - second

          - nested

        #. one
        #. two

        Run this::

            dotnet build
            dotnet test

        .. code-block:: python

           print("hi")

        .. note::

           Back up your files.

        +--------+--------+
        | Name   | Size   |
        +========+========+
        | Seoul  | 9.7    |
        +--------+--------+
        | Both columns    |
        +-----------------+

        =====  =====
        A      B
        =====  =====
        1      2
        =====  =====

        Details
        -------

            A quoted line.

        term
            Its definition.

        ----

        Last words.

        .. [#] The footnote's text.
        .. _Docs: https://filee.sh/docs
        .. |project| replace:: Filee
        """;

    private HDocument Read(string text, DocumentLimits? limits = null) => RstReader.Read(text, fx.NewFolder(), limits ?? DocumentLimits.Default);

    [Fact]
    public void A_document_becomes_the_model()
    {
        var document = Read(Guide);
        var paragraphs = Paragraphs(document);

        Assert.Equal("Install guide", document.Title);
        Assert.Equal([(1, "Getting started"), (2, "Details")], paragraphs.Where(p => p.HeadingLevel > 0).Select(p => (p.HeadingLevel, InlineText(p.Inlines))));
        var intro = paragraphs[1];
        Assert.Equal("Filee is fast and small; run filee --help. Water is H2O and e = mc2. See the site, the Docs and https://example.invalid/page. "
                     + "A note follows . Filee ships today.", InlineText(intro.Inlines));
        var runs = intro.Inlines.OfType<HText>().ToList();
        Assert.True(runs.Single(r => r.Text == "fast").Format.Bold);
        Assert.True(runs.Single(r => r.Text == "small").Format.Italic);
        Assert.Equal(HtmlReader.CodeShade, runs.Single(r => r.Text == "filee --help").Format.Shade);
        Assert.True(runs.Single(r => r.Text == "2" && r.Format.Subscript is true).Format.Subscript);
        Assert.Equal(["https://filee.sh/", "https://filee.sh/docs", "https://example.invalid/page"], intro.Inlines.OfType<HLink>().Select(l => l.Target));
        Assert.Equal("The footnote's text.", InlineText(((HParagraph)intro.Inlines.OfType<HNote>().Single().Blocks.Single()).Inlines));

        Assert.Equal([("first", 0, true), ("second", 0, true), ("nested", 1, true), ("one", 0, false), ("two", 0, false)],
            paragraphs.Where(p => p.List is not null).Select(p => (InlineText(p.Inlines), p.List!.Level, p.List.Numbering.Levels[p.List.Level].Bullet)));
        Assert.Contains(paragraphs, p => InlineText(p.Inlines) == "Run this:");
        Assert.Equal(["dotnet build", "dotnet test", "print(\"hi\")"], paragraphs.Where(MarkupBlocks.IsCode).Select(p => InlineText(p.Inlines)));
        Assert.True(paragraphs.Single(p => InlineText(p.Inlines) == "Note").Inlines.OfType<HText>().Single().Format.Bold);
        Assert.Equal(MarkupBlocks.QuoteIndent, paragraphs.Single(p => InlineText(p.Inlines) == "Back up your files.").Format.Left);

        var tables = document.Sections[0].Blocks.OfType<HTable>().ToList();
        Assert.Equal(2, tables.Count);
        Assert.True(tables[0].Rows[0].Header);
        Assert.Equal(["Name", "Size"], tables[0].Rows[0].Cells.Select(c => InlineText(((HParagraph)c.Blocks.Single()).Inlines)));
        Assert.Equal(2, tables[0].Rows[2].Cells.Single().ColSpan);
        Assert.True(tables[1].Rows[0].Header);
        Assert.Equal(["1", "2"], tables[1].Rows[1].Cells.Select(c => InlineText(((HParagraph)c.Blocks.Single()).Inlines)));

        Assert.Equal(MarkupBlocks.QuoteIndent, paragraphs.Single(p => InlineText(p.Inlines) == "A quoted line.").Format.Left);
        Assert.True(paragraphs.Single(p => InlineText(p.Inlines) == "term").Inlines.OfType<HText>().Single().Format.Bold);
        Assert.Contains(paragraphs, p => p.Inlines.Any(i => i is HShape { Kind: HShapeKind.Line }));
        Assert.Equal("Last words.", InlineText(paragraphs[^1].Inlines));
    }

    [Fact]
    public void Row_spans_of_grid_tables_are_read()
    {
        var document = Read("""
            +------+------+
            | Tall | Top  |
            +      +------+
            |      | Low  |
            +------+------+
            """);

        var table = document.Sections[0].Blocks.OfType<HTable>().Single();
        Assert.Equal(2, table.Rows[0].Cells[0].RowSpan);
        Assert.Equal("Low", InlineText(((HParagraph)table.Rows[1].Cells.Single().Blocks.Single()).Inlines));
    }

    [Fact]
    public void A_document_round_trips_through_the_writer()
    {
        var first = Read(Guide);
        var written = RstWriter.Write(first, _ => null);
        var second = Read(written);

        Assert.Equal(first.Title, second.Title);
        Assert.Equal(MarkupShape.Of(first), MarkupShape.Of(second));
        Assert.Equal(written, RstWriter.Write(second, _ => null));

        // Spans both ways, written as a grid table.
        var spans = Read("+------+------+\n| Tall | Top  |\n+      +------+\n|      | Low  |\n+------+------+\n| Wide row    |\n+-------------+\n");
        Assert.Equal(MarkupShape.Of(spans), MarkupShape.Of(Read(RstWriter.Write(spans, _ => null))));
    }

    [Fact]
    public void Text_that_looks_like_markup_stays_text()
    {
        var document = new HDocument();
        var section = new HSection();
        foreach (var text in new[]
                 {
                     "a *star*, a `tick`, a |bar| and name_ or [1]_ and _`target`", "- not a list", "1. nor this", ".. not a comment",
                     "Ends in::", "=====", ":field: no", "back\\slash and https://example.invalid/x",
                 })
        {
            var paragraph = new HParagraph();
            paragraph.Inlines.Add(new HText(text, default));
            section.Blocks.Add(paragraph);
        }
        // Bold right next to letters.
        var tight = new HParagraph();
        tight.Inlines.Add(new HText("un", default));
        tight.Inlines.Add(new HText("break", new HCharFormat(Bold: true)));
        tight.Inlines.Add(new HText("able", default));
        section.Blocks.Add(tight);
        document.Sections.Add(section);

        var back = Read(RstWriter.Write(document, _ => null));

        Assert.Equal(MarkupShape.Of(document), MarkupShape.Of(back));
    }

    [Fact]
    public async Task Documents_convert_without_pandoc()
    {
        var source = Path.Combine(fx.NewFolder(), "guide.rst");
        await File.WriteAllTextAsync(source, Guide, TestContext.Current.CancellationToken);

        await ConvertsEverywhereWithoutPandocAsync(fx, source, "Getting started", "dotnet build", "Seoul");
    }

    [Fact]
    public void Hostile_documents_are_read_in_time_or_refused()
    {
        // Quotes and lists nested by indentation past the nesting limit.
        var quotes = string.Concat(Enumerable.Range(0, 40).Select(n => new string(' ', n * 2) + "q\n\n"));
        Assert.Throws<ConversionRefusedException>(() => Read(quotes));
        var lists = string.Concat(Enumerable.Range(0, 40).Select(n => new string(' ', n * 2) + "- item\n\n"));
        Assert.Throws<ConversionRefusedException>(() => Read(lists));

        var watch = Stopwatch.StartNew();
        Read(string.Concat(Enumerable.Repeat("*a **b ``c `d |e [f :g ", 200_000)));
        Read(string.Concat(Enumerable.Repeat("word_ ", 300_000)));
        // A grid table wider than the column limit is cut, and one of a million rows is refused.
        var wide = "+" + string.Concat(Enumerable.Repeat("-+", 20_000)) + "\n|" + string.Concat(Enumerable.Repeat("x|", 20_000)) + "\n+"
                   + string.Concat(Enumerable.Repeat("-+", 20_000)) + "\n";
        Assert.Equal(DocumentLimits.Default.MaxTableColumns, Read(wide).Sections[0].Blocks.OfType<HTable>().Single().ColumnCount);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"{watch.Elapsed}");

        var limits = DocumentLimits.Default with { MaxParagraphs = 100 };
        Assert.Throws<ConversionRefusedException>(() => Read(string.Concat(Enumerable.Repeat("para\n\n", 200)), limits));
    }
}
