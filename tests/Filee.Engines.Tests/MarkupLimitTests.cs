// Hostile input for the markup readers (reStructuredText, Org, Textile, MediaWiki, DocBook, Jupyter), built in code:
// tables whose grid would be gigabytes, one footnote referenced thousands of times or notes that refer to each other,
// and the line patterns that used to cost quadratic time. Each is read in time or refused with the usual
// "too complex" error.

using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Markup;

namespace Filee.Engines.Tests;

public class MarkupLimitTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(20);

    private HDocument Rst(string text, DocumentLimits? limits = null) => RstReader.Read(text, fx.NewFolder(), limits ?? DocumentLimits.Default);

    private HDocument Org(string text, DocumentLimits? limits = null) => OrgReader.Read(text, fx.NewFolder(), limits ?? DocumentLimits.Default);

    private HDocument Textile(string text, DocumentLimits? limits = null) => TextileReader.Read(text, fx.NewFolder(), limits ?? DocumentLimits.Default);

    private HDocument Wiki(string text, DocumentLimits? limits = null) => MediaWikiReader.Read(text, fx.NewFolder(), limits ?? DocumentLimits.Default);

    private HDocument DocBook(string xml) => DocBookReader.Read(Encoding.UTF8.GetBytes(xml), fx.NewFolder(), DocumentLimits.Default);

    /// <summary>Runs <paramref name="read"/> and checks it ended within <see cref="Quick"/>.</summary>
    private static T InTime<T>(Func<T> read)
    {
        var watch = Stopwatch.StartNew();
        var result = read();
        Assert.True(watch.Elapsed < Quick, $"{watch.Elapsed}");
        return result;
    }

    private static void RefusedInTime(Action read)
    {
        var watch = Stopwatch.StartNew();
        var refusal = Assert.Throws<ConversionRefusedException>(read);
        Assert.Equal(DocumentLimits.ErrorKey, refusal.ErrorKey);
        Assert.True(watch.Elapsed < Quick, $"{watch.Elapsed}");
    }

    private static string Repeat(string text, int count) => new StringBuilder(text.Length * count).Insert(0, text, count).ToString();

    private static IEnumerable<HNote> Notes(HDocument document) =>
        document.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HNote>();

    // ───────────────────────── Tables ─────────────────────────

    [Fact]
    public void Rst_grid_table_is_counted_before_its_grid_is_made()
    {
        // A border of 16,384 columns over 100,000 short "+-+" lines: 1.6 billion grid cells.
        var table = "+" + Repeat("-+", 16_384) + "\n" + Repeat("+-+\n", 100_000);
        RefusedInTime(() => Rst(table));
        // One row of 16,384 cells over 100,000 lines: the lines the cells take are counted before any is cut out.
        var tall = "+" + Repeat("-+", 16_384) + "\n|" + Repeat("x|", 16_384) + "\n" + Repeat("|\n", 100_000) + "+" + Repeat("-+", 16_384) + "\n";
        RefusedInTime(() => Rst(tall));
    }

    [Fact]
    public void Rst_simple_table_is_counted_before_its_rows_are_made()
    {
        var border = Repeat("= ", 16_384) + "\n";
        RefusedInTime(() => Rst(border + Repeat("x\n", 100_000) + border));
        // Lines that go on with the row above cost the columns they reach, not every column.
        var tall = border + "a\n" + Repeat(" b\n", 200_000) + border;
        var cell = InTime(() => Rst(tall)).Sections[0].Blocks.OfType<HTable>().Single().Rows[0].Cells[0];
        Assert.StartsWith("a b b", MarkupTestKit.InlineText(((HParagraph)cell.Blocks[0]).Inlines));
    }

    [Fact]
    public void Wide_rows_are_cut_at_the_column_limit_without_splitting_the_rest()
    {
        var bars = Repeat("|", 4_000_000);
        var org = InTime(() => Org(bars + "\n")).Sections[0].Blocks.OfType<HTable>().Single();
        Assert.Equal(DocumentLimits.Default.MaxTableColumns, org.ColumnCount);
        var textile = InTime(() => Textile(bars + "\n")).Sections[0].Blocks.OfType<HTable>().Single();
        Assert.Equal(DocumentLimits.Default.MaxTableColumns, textile.ColumnCount);
        var wiki = InTime(() => Wiki("{|\n|" + bars + "\n|}\n")).Sections[0].Blocks.OfType<HTable>().Single();
        Assert.Equal(DocumentLimits.Default.MaxTableColumns, wiki.ColumnCount);
    }

    [Fact]
    public void Row_spans_end_at_the_last_row_and_ragged_tables_are_counted()
    {
        var wiki = Wiki("{|\n| rowspan=\"10000\" colspan=\"16384\" | x\n|-\n| y\n|}\n").Sections[0].Blocks.OfType<HTable>().Single();
        Assert.Equal(2, wiki.Rows[0].Cells[0].RowSpan);
        Assert.Equal(16_384, wiki.Rows[0].Cells[0].ColSpan);
        // One very wide row over many narrow ones: the grid the writers lay out is counted.
        RefusedInTime(() => Wiki("{|\n|" + Repeat("x||", 16_383) + "x\n" + Repeat("|-\n| y\n", 100_000) + "|}\n"));
        RefusedInTime(() => DocBook("<article xmlns=\"http://docbook.org/ns/docbook\"><informaltable><tgroup cols=\"16384\"><tbody>"
                                    + Repeat("<row/>", 1_000) + "</tbody></tgroup></informaltable></article>"));
    }

    // ───────────────────────── Shared notes ─────────────────────────

    [Fact]
    public void A_footnote_referenced_many_times_is_read_once_and_counted_at_every_reference()
    {
        var note = Repeat("lorem ", 120_000); // 720 KB, 144 MB at 200 references
        RefusedInTime(() => Rst("Text" + Repeat(" [1]_", 200) + "\n\n.. [1] " + note + "\n"));
        RefusedInTime(() => Org("Text" + Repeat(" [fn:1]", 200) + "\n\n[fn:1] " + note + "\n"));
        RefusedInTime(() => Textile("Text" + Repeat(" [1]", 200) + "\n\nfn1. " + note + "\n"));
        RefusedInTime(() => Wiki("<ref name=\"a\">" + note + "</ref>" + Repeat(" <ref name=\"a\" />", 200) + "\n"));

        // A few references to a short note each show it, and share the paragraphs.
        var notes = Notes(Rst("One [1]_ two [1]_ three [1]_.\n\n.. [1] The note.\n")).ToList();
        Assert.Equal(3, notes.Count);
        Assert.All(notes, n => Assert.Equal("The note.", MarkupTestKit.InlineText(((HParagraph)n.Blocks.Single()).Inlines)));
        Assert.Same(notes[0].Blocks[0], notes[2].Blocks[0]);
        // A named <ref> used again with <ref name="a" /> is the same note.
        var wiki = Notes(Wiki("One<ref name=\"a\">The note.</ref> two<ref name=\"a\" /> three<ref name=a/>.\n")).ToList();
        Assert.Equal(3, wiki.Count);
        Assert.All(wiki, n => Assert.Equal("The note.", MarkupTestKit.InlineText(((HParagraph)n.Blocks.Single()).Inlines)));
    }

    [Fact]
    public void Notes_that_refer_to_each_other_are_refused_before_they_multiply()
    {
        // Twenty notes, each referring to the next four times: 4^20 copies once written out.
        var rst = new StringBuilder("Start [1]_.\n\n");
        for (var n = 1; n <= 20; n++)
            rst.Append(".. [").Append(n).Append("] Note").Append(Repeat($" [{n + 1}]_", 4)).Append("\n\n");
        RefusedInTime(() => Rst(rst.ToString()));

        var wiki = new StringBuilder("<ref name=\"n0\">base</ref>\n\n");
        for (var n = 1; n <= 20; n++)
            wiki.Append("<ref name=\"n").Append(n).Append("\">level").Append(Repeat($"<ref name=\"n{n - 1}\" />", 4)).Append("</ref>\n\n");
        RefusedInTime(() => Wiki(wiki.ToString()));

        var org = new StringBuilder("Start [fn:1].\n\n");
        for (var n = 1; n <= 20; n++)
            org.Append("[fn:").Append(n).Append("] Note").Append(Repeat($" [fn:{n + 1}]", 4)).Append("\n\n");
        RefusedInTime(() => Org(org.ToString()));
    }

    [Fact]
    public void A_note_that_refers_to_itself_does_not_hold_itself()
    {
        foreach (var document in new[]
                 {
                     Rst("Text [1]_.\n\n.. [1] See [1]_ again.\n"),
                     Org("Text [fn:1].\n\n[fn:1] See [fn:1] again.\n"),
                     Textile("Text[1].\n\nfn1. See[1] again.\n"),
                 })
        {
            var note = Notes(document).Single();
            Assert.Contains("See", MarkupTestKit.InlineText(((HParagraph)note.Blocks.Single()).Inlines));
            // Every note reachable from the text is finite: no note is inside itself.
            Assert.DoesNotContain(note, ((HParagraph)note.Blocks.Single()).Inlines);
            Assert.DoesNotContain(((HParagraph)note.Blocks.Single()).Inlines.OfType<HNote>(), n => n.Blocks.Count > 0
                && ((HParagraph)n.Blocks[0]).Inlines.Contains(n));
        }
        // Two notes that refer to each other: the loop is cut where it closes.
        var pair = Rst("Text [1]_.\n\n.. [1] One [2]_.\n\n.. [2] Two [1]_.\n");
        var first = Notes(pair).Single();
        var second = ((HParagraph)first.Blocks.Single()).Inlines.OfType<HNote>().Single();
        Assert.Empty(((HParagraph)second.Blocks.Single()).Inlines.OfType<HNote>());
    }

    [Fact]
    public void A_substitution_counts_its_text_at_every_use()
    {
        var limits = DocumentLimits.Default with { MaxPartBytes = 1024 * 1024 };
        RefusedInTime(() => Rst(Repeat("|s| ", 200) + "\n\n.. |s| replace:: " + Repeat("x", 10_000) + "\n", limits));
        Assert.Equal("aaa bbb", MarkupTestKit.InlineText(((HParagraph)Rst("|a| |b|\n\n.. |a| replace:: aaa\n.. |b| replace:: bbb\n").Sections[0].Blocks[0]).Inlines));
    }

    // ───────────────────────── Linear scans ─────────────────────────

    [Fact]
    public void Line_patterns_that_used_to_take_quadratic_time_are_read_in_time()
    {
        // reST: target definitions, each indented line a target of its own; a staircase of them; a long line block.
        InTime(() => Rst(string.Concat(Enumerable.Range(0, 20_000).Select(i => $"  .. _t{i}: http://x/{i}\n"))));
        InTime(() => Rst(string.Concat(Enumerable.Range(0, 5_000).Select(i => new string(' ', i % 3000) + $".. _t{i}: u{i}\n"))));
        InTime(() => Rst("| a\n" + Repeat("  more\n", 200_000)));
        // A target's URL still goes on on the lines indented under it.
        var link = Rst("See `site`_.\n\n.. _site: http://example.invalid/\n   long/path\n").Sections[0].Blocks.OfType<HParagraph>().First()
            .Inlines.OfType<HLink>().Single();
        Assert.Equal("http://example.invalid/long/path", link.Target);

        // Org: drawers that are never closed, many #+TITLE lines, a headline with a long run of spaces.
        InTime(() => Org(Repeat(":A:\n\n", 200_000)));
        Assert.Equal(Repeat("x ", 99_999) + "x", InTime(() => Org(Repeat("#+TITLE: x\n", 100_000))).Title);
        var headline = InTime(() => Org("* a" + new string(' ', 200_000) + "b   \n")).Sections[0].Blocks.OfType<HParagraph>().Single();
        Assert.EndsWith(" b", MarkupTestKit.InlineText(headline.Inlines));

        // DocBook: a paragraph that starts with a long run of blank runs in changing formats.
        var blanks = InTime(() => DocBook("<article xmlns=\"http://docbook.org/ns/docbook\"><para>" + Repeat("<emphasis> </emphasis> ", 100_000)
                                          + "end</para></article>"));
        Assert.Equal("end", MarkupTestKit.InlineText(((HParagraph)blanks.Sections[0].Blocks.Single()).Inlines));
    }

    [Fact]
    public void Notebook_attachments_are_written_only_when_the_text_links_them()
    {
        var png = new JsonObject { ["image/png"] = Convert.ToBase64String(DocumentFuzzTests.TinyPng(2, 2)) };
        var attachments = new JsonObject();
        for (var n = 0; n < 5_000; n++)
            attachments[$"unused{n}.png"] = png.DeepClone();
        attachments["used.png"] = png.DeepClone();
        var json = NotebookTests.Notebook(NotebookTests.MarkdownCell(Repeat("text ", 50_000) + "\n\n![used](attachment:used.png)", attachments));
        var media = Path.Combine(fx.NewFolder(), "media");
        var document = InTime(() => NotebookReader.Read(Encoding.UTF8.GetBytes(json), fx.NewFolder(), media, DocumentLimits.Default));
        Assert.Single(HDocumentWalker.Images(document));
        Assert.Single(Directory.GetFiles(media));
    }

    [Fact]
    public void A_cancelled_read_stops()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var text = Repeat("para\n\n", 1_000);
        Assert.Throws<OperationCanceledException>(() => RstReader.Read(text, fx.NewFolder(), DocumentLimits.Default, cancelled.Token));
        Assert.Throws<OperationCanceledException>(() => OrgReader.Read(text, fx.NewFolder(), DocumentLimits.Default, cancelled.Token));
        Assert.Throws<OperationCanceledException>(() => TextileReader.Read(text, fx.NewFolder(), DocumentLimits.Default, cancelled.Token));
        Assert.Throws<OperationCanceledException>(() => MediaWikiReader.Read(text, fx.NewFolder(), DocumentLimits.Default, cancelled.Token));
        Assert.Throws<OperationCanceledException>(() => DocBookReader.Read(Encoding.UTF8.GetBytes("<article><para>x</para></article>"), fx.NewFolder(),
            DocumentLimits.Default, cancelled.Token));
    }
}
