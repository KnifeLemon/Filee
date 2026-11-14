// Hostile models for the text markup writers (reStructuredText, Org, Textile, MediaWiki, DocBook, Markdown, Jupyter,
// OPML), built in code as any reader could hand them over: spans that overlap or are declared billions wide, grids
// that would be gigabytes, one footnote referenced again and again, and content nested thousands of levels deep. Each
// is written in time or refused with the usual "too complex" error.

using System.Diagnostics;
using System.Text;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Markup;

namespace Filee.Engines.Tests;

public class MarkupWriterLimitTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(20);

    /// <summary>Every text writer, each on a fresh model (the writers open up deep nesting in place).</summary>
    private IEnumerable<(string Name, Func<HDocument, string> Write)> Writers() =>
    [
        ("rst", d => RstWriter.Write(d, _ => null)),
        ("org", d => OrgWriter.Write(d, _ => null)),
        ("textile", d => TextileWriter.Write(d, _ => null)),
        ("mediawiki", d => MediaWikiWriter.Write(d, _ => null)),
        ("dbk", d => DocBookWriter.Write(d, _ => null)),
        ("md", d => MarkdownWriter.Write(d, _ => null)),
        ("ipynb", d => NotebookWriter.Write(d, fx.NewFolder(), pictures: false)),
        ("opml", d => OpmlWriter.Write(d, _ => null)),
    ];

    private static T InTime<T>(Func<T> write)
    {
        var watch = Stopwatch.StartNew();
        var result = write();
        Assert.True(watch.Elapsed < Quick, $"{watch.Elapsed}");
        return result;
    }

    private static void RefusedInTime(Action write)
    {
        var watch = Stopwatch.StartNew();
        var refusal = Assert.Throws<ConversionRefusedException>(write);
        Assert.Equal(DocumentLimits.ErrorKey, refusal.ErrorKey);
        Assert.True(watch.Elapsed < Quick, $"{watch.Elapsed}");
    }

    private static HDocument Document(params HBlock[] blocks)
    {
        var document = new HDocument();
        var section = new HSection();
        section.Blocks.AddRange(blocks);
        document.Sections.Add(section);
        return document;
    }

    private static HParagraph Paragraph(params HInline[] inlines)
    {
        var paragraph = new HParagraph();
        paragraph.Inlines.AddRange(inlines);
        return paragraph;
    }

    private static HText Text(string text) => new(text, default);

    private static HCell Cell(string text, int rowSpan = 1, int colSpan = 1)
    {
        var cell = new HCell { RowSpan = rowSpan, ColSpan = colSpan };
        cell.Blocks.Add(Paragraph(Text(text)));
        return cell;
    }

    private static HTable Table(params HCell[][] rows)
    {
        var table = new HTable();
        foreach (var cells in rows)
        {
            var row = new HRow();
            row.Cells.AddRange(cells);
            table.Rows.Add(row);
        }
        return table;
    }

    // ───────────────────────── Spans ─────────────────────────

    /// <summary>
    /// A table of <paramref name="n"/> rows whose row i ends in a cell spanning the last i + 1 columns down to the
    /// bottom: every such cell overlaps all the ones above it, about n³ / 6 positions in all.
    /// </summary>
    private static HTable Staircase(int n)
    {
        var table = new HTable { ColumnCount = n };
        for (var i = 0; i < n; i++)
        {
            var row = new HRow();
            if (n - 1 - i > 0)
                row.Cells.Add(Cell("a" + i, colSpan: n - 1 - i));
            row.Cells.Add(Cell("b" + i, rowSpan: n - i, colSpan: i + 1));
            table.Rows.Add(row);
        }
        return table;
    }

    [Fact]
    public void Overlapping_spans_are_placed_in_time()
    {
        // 700 rows: about 57 million overlapping positions, which the writers used to mark one by one.
        foreach (var (name, write) in Writers())
        {
            var text = InTime(() => write(Document(Staircase(700))));
            Assert.True(text.Contains("b699", StringComparison.Ordinal), name);
        }
    }

    [Fact]
    public void A_span_into_a_column_held_from_above_is_cut_there()
    {
        // Row 2's cell asks for two columns, but the second is still held by the cell above spanning down.
        HDocument Model() => Document(Table(
            [Cell("x"), Cell("y", rowSpan: 2)],
            [Cell("z", colSpan: 2)]));

        var wiki = MediaWikiWriter.Write(Model(), _ => null);
        Assert.DoesNotContain("colspan", wiki, StringComparison.Ordinal);
        Assert.Contains("rowspan=\"2\"", wiki, StringComparison.Ordinal);

        var textile = TextileWriter.Write(Model(), _ => null);
        Assert.DoesNotContain("\\2", textile, StringComparison.Ordinal);

        var docbook = DocBookWriter.Write(Model(), _ => null);
        Assert.DoesNotContain("namest", docbook, StringComparison.Ordinal);

        // reST draws the grid from the cells: z's border ends where y's starts.
        var rst = RstWriter.Write(Model(), _ => null);
        Assert.Contains("| z |", rst.Replace("  ", " ", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void Spans_declared_billions_wide_are_cut_to_the_table()
    {
        // Two cells of int.MaxValue columns used to overflow the sum of a row's spans.
        foreach (var (_, write) in Writers())
        {
            var text = InTime(() => write(Document(Table(
                [Cell("first", int.MaxValue, int.MaxValue), Cell("second", int.MaxValue, int.MaxValue)],
                [Cell("below")]))));
            Assert.Contains("first", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_short_row_leaves_blank_cells_in_a_grid_table()
    {
        // The first row has no cells at all; reST used to take its first cell for every gap.
        var rst = RstWriter.Write(Document(Table([], [Cell("a"), Cell("b")])), _ => null);
        Assert.Contains("| a ", rst, StringComparison.Ordinal);
        Assert.Contains("| b ", rst, StringComparison.Ordinal);
    }

    // ───────────────────────── Grid size ─────────────────────────

    [Fact]
    public void A_table_of_too_many_positions_is_refused_before_its_grid_is_made()
    {
        // 100 rows of one cell spanning 16,384 columns: 1.6 million positions from 100 cells.
        foreach (var (_, write) in Writers())
        {
            var rows = Enumerable.Range(0, 100).Select(i => new[] { Cell("r" + i, colSpan: 16_384) }).ToArray();
            RefusedInTime(() => write(Document(Table(rows))));
        }
    }

    [Fact]
    public void The_tables_of_a_document_are_counted_together()
    {
        // 300 one-cell tables each 16,384 columns wide: 4.9 million positions.
        foreach (var (_, write) in Writers())
        {
            var tables = Enumerable.Range(0, 300).Select(i => (HBlock)Table([Cell("t" + i, colSpan: 16_384)])).ToArray();
            RefusedInTime(() => write(Document(tables)));
        }
    }

    [Fact]
    public void A_long_cell_over_many_rows_is_counted_before_the_grid_table_is_drawn()
    {
        // Every line of a reST grid table is as wide as the widest cell: 100,000 rows under a 10,000-character cell
        // would be two billion characters.
        var rows = new List<HCell[]> { new[] { Cell(new string('x', 10_000)) } };
        rows.AddRange(Enumerable.Range(0, 100_000).Select(_ => new[] { Cell("y") }));
        RefusedInTime(() => RstWriter.Write(Document(Table([.. rows])), _ => null));
        // The other languages write a row per line and take it in their stride.
        Assert.Contains("| y |", InTime(() => OrgWriter.Write(Document(Table([.. rows])), _ => null)), StringComparison.Ordinal);
    }

    // ───────────────────────── Shared footnotes ─────────────────────────

    private static HDocument SharedNote(int length, int references)
    {
        var note = new HNote(endnote: false);
        note.Blocks.Add(Paragraph(Text(new string('n', length))));
        var paragraph = new HParagraph();
        for (var i = 0; i < references; i++)
            paragraph.Inlines.AddRange([Text("ref" + i + " "), note]);
        return Document(paragraph);
    }

    [Fact]
    public void A_footnote_shared_by_its_references_is_written_as_separate_notes_would_be()
    {
        var shared = SharedNote(20, 3);
        var separate = new HParagraph();
        for (var i = 0; i < 3; i++)
        {
            var note = new HNote(endnote: false);
            note.Blocks.Add(Paragraph(Text(new string('n', 20))));
            separate.Inlines.AddRange([Text("ref" + i + " "), note]);
        }
        Assert.Equal(RstWriter.Write(Document(separate), _ => null), RstWriter.Write(shared, _ => null));
        Assert.Equal(3, RstWriter.Write(SharedNote(20, 3), _ => null).Split(".. [#]").Length - 1);
    }

    [Fact]
    public void A_footnote_shared_by_thousands_of_references_is_refused_in_time()
    {
        // 1 MB of footnote referenced 1,000 times: a gigabyte of notes, refused once 256 million characters are reached
        // (the note's text is made once and counted at each reference).
        RefusedInTime(() => RstWriter.Write(SharedNote(1_000_000, 1_000), _ => null));

        // The same with small limits (4 million characters) for each language; MediaWiki writes its notes inline.
        var small = DocumentLimits.Default with { MaxPartBytes = 1024 * 1024 };
        foreach (var write in new Func<HDocument, string>[]
                 {
                     d => new RstWriter(_ => null) { Limits = small }.Write(d),
                     d => new OrgWriter(_ => null) { Limits = small }.Write(d),
                     d => new TextileWriter(_ => null) { Limits = small }.Write(d),
                     d => new MediaWikiWriter(_ => null) { Limits = small }.Write(d),
                     d => DocBookWriter.Write(d, _ => null, small),
                 })
        {
            RefusedInTime(() => write(SharedNote(100_000, 100)));
            Assert.Contains("ref9 ", write(SharedNote(100_000, 10)), StringComparison.Ordinal);
        }
    }

    // ───────────────────────── Nesting ─────────────────────────

    /// <summary>Tables in table cells, links in links and notes in notes, <paramref name="levels"/> deep each.</summary>
    private static HDocument Nested(int levels)
    {
        var document = Document();
        var blocks = document.Sections[0].Blocks;
        for (var i = 0; i < levels; i++)
        {
            var table = new HTable { ColumnCount = 1 };
            var row = new HRow();
            var cell = new HCell();
            row.Cells.Add(cell);
            table.Rows.Add(row);
            blocks.Add(table);
            blocks = cell.Blocks;
        }
        blocks.Add(Paragraph(Text("deepest cell")));
        // Links and notes in a paragraph of the body: every language writes those (not all write tables in cells).
        var paragraph = new HParagraph();
        document.Sections[0].Blocks.Add(paragraph);
        var inlines = paragraph.Inlines;
        for (var i = 0; i < levels; i++)
        {
            var link = new HLink("https://example.com/" + i);
            inlines.Add(link);
            inlines = link.Content;
        }
        inlines.Add(Text("deepest link"));

        var noted = new HParagraph();
        document.Sections[0].Blocks.Add(noted);
        var holder = noted.Inlines;
        for (var i = 0; i < levels; i++)
        {
            var note = new HNote(endnote: false);
            var inside = new HParagraph();
            note.Blocks.Add(inside);
            holder.Add(Text("n" + i + " "));
            holder.Add(note);
            holder = inside.Inlines;
        }
        holder.Add(Text("deepest note"));
        return document;
    }

    [Fact]
    public void Writers_open_up_a_model_nested_thousands_deep()
    {
        // A stack overflow can't be caught: it would end the test run (and Filee) rather than fail.
        foreach (var (name, write) in Writers())
        {
            var text = InTime(() => write(Nested(5_000)));
            Assert.True(text.Contains("deepest link", StringComparison.Ordinal), name);
            Assert.True(text.Contains("deepest note", StringComparison.Ordinal), name);
        }
    }

    [Fact]
    public void Outlines_nest_no_deeper_than_headings_do()
    {
        // 20,000 headings each a level below the one before: OPML writes outlines level by level.
        var headings = Enumerable.Range(1, 20_000).Select(level => (HBlock)new HParagraph { HeadingLevel = level }).ToArray();
        foreach (var block in headings)
            ((HParagraph)block).Inlines.Add(Text("h"));
        var opml = InTime(() => OpmlWriter.Write(Document(headings), _ => null));
        var depth = 0;
        var deepest = 0;
        foreach (var line in opml.Split('\n'))
        {
            if (line.TrimStart().StartsWith("<outline", StringComparison.Ordinal) && !line.TrimEnd().EndsWith("/>", StringComparison.Ordinal))
                deepest = Math.Max(deepest, ++depth);
            else if (line.TrimStart().StartsWith("</outline>", StringComparison.Ordinal))
                depth--;
        }
        Assert.InRange(deepest, 1, 9);
    }

    // ───────────────────────── Fuzzing ─────────────────────────

    private const string SpanSeed = """
        == Title ==
        Text<ref name="a">Note</ref> and <ref name="a" />.
        {|
        ! a !! b !! c
        |-
        | rowspan="3" colspan="2" | d || e
        |-
        | colspan="3" | f
        |-
        |
        |-
        | g || rowspan="2" | h || i
        |}
        """;

    /// <summary>
    /// MediaWiki tables with their spans mutated (repeated, widened, rows dropped) are read and then written in every
    /// language: each must be written or refused in time. FILEE_FUZZ_SECONDS runs longer with a new random seed.
    /// </summary>
    [Fact]
    [Trait("Category", "Fuzz")]
    public void Writers_write_or_refuse_every_mutated_span_table()
    {
        var writers = Writers().ToList();
        new FuzzHarness(fx, 40, 20261114) { Timeout = Quick }.Data("spans", "txt", [Encoding.UTF8.GetBytes(SpanSeed)],
            FuzzHarness.Text(["| rowspan=\"9999\" colspan=\"99999\" | x ", "| colspan=\"16384\" | w ", "| rowspan=\"2\" | t ", "||", "!!", "|-\n", "|}\n",
                              "{|\n", "<ref name=\"a\" />", "\n"]),
            bytes =>
            {
                var text = Encoding.UTF8.GetString(bytes);
                foreach (var (_, write) in writers)
                    write(MediaWikiReader.Read(text, fx.NewFolder(), DocumentLimits.Default));
            });
    }
}
