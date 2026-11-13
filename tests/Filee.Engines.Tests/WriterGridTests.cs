// The document writers' tables and output files under hostile models: spans clamped to the table, covered positions
// tracked per column (a span costs its columns, not its rows × columns), tables of more than MaxTableCells positions
// refused, ODT gaps written as repeated cells, a paragraph running over thousands of slides measured once, a token
// that cancels a long write, and temporary files of their own next to the output. Every model is built in code.

using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Hwp.Hwpx.Odt;
using Filee.Engines.Hwp.Hwpx.Pptx;

namespace Filee.Engines.Tests;

public class WriterGridTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static HDocument Document(params HBlock[] blocks)
    {
        var document = new HDocument();
        var section = new HSection();
        section.Blocks.AddRange(blocks);
        document.Sections.Add(section);
        return document;
    }

    private static HParagraph Text(string text)
    {
        var paragraph = new HParagraph();
        paragraph.Inlines.Add(new HText(text, default));
        return paragraph;
    }

    private static HCell Cell(string text, int column = -1, int rowSpan = 1, int colSpan = 1)
    {
        var cell = new HCell { Column = column, RowSpan = rowSpan, ColSpan = colSpan };
        cell.Blocks.Add(Text(text));
        return cell;
    }

    /// <summary>A table of <paramref name="rows"/> rows, each with one cell.</summary>
    private static HTable Rows(int rows)
    {
        var table = new HTable { ColumnCount = 1 };
        for (var r = 0; r < rows; r++)
        {
            var row = new HRow();
            row.Cells.Add(Cell($"r{r}"));
            table.Rows.Add(row);
        }
        return table;
    }

    private static string CellText(HCell cell) =>
        string.Concat(cell.Blocks.OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HText>().Select(t => t.Text));

    private static string ContentXml(string odt)
    {
        using var zip = ZipFile.OpenRead(odt);
        using var reader = new StreamReader(zip.GetEntry("content.xml")!.Open());
        return reader.ReadToEnd();
    }

    private static void NoTemporaryFiles(string folder) =>
        Assert.Empty(Directory.GetFiles(folder, "*.tmp").Where(f => !f.EndsWith("out.odt.tmp", StringComparison.Ordinal) && !f.EndsWith("out.pptx.tmp", StringComparison.Ordinal)));

    // ───────────────────────── Spans ─────────────────────────

    [Fact]
    public void Spans_past_the_table_are_clamped_to_its_rows_and_columns()
    {
        var table = Rows(3);
        table.Rows[0].Cells.Insert(0, Cell("huge", rowSpan: int.MaxValue, colSpan: int.MaxValue));
        table.ColumnCount = int.MaxValue;

        var (placed, columns) = HwpxWriter.PlaceCells(table);

        var huge = table.Rows[0].Cells[0];
        Assert.Equal((3, DocumentLimits.Default.MaxTableColumns), (huge.RowSpan, huge.ColSpan));
        Assert.Equal(0, placed[0][0].Column);
        // The other cells go past the span, which holds every column of the rows below too.
        Assert.Equal(DocumentLimits.Default.MaxTableColumns, placed[0][1].Column);
        Assert.Equal(DocumentLimits.Default.MaxTableColumns, placed[2][0].Column);
        Assert.Equal(DocumentLimits.Default.MaxTableColumns + 1, columns);
    }

    [Fact]
    public void A_tall_wide_span_costs_its_columns_and_the_odt_grid_repeats_its_covered_cells()
    {
        // 20,000 rows under one cell spanning all of them and 400 columns: 8 million covered positions, which a set
        // of positions would hold one by one.
        const int rows = 20_000, wide = 400;
        var table = Rows(rows);
        table.Rows[0].Cells.Insert(0, Cell("tall", rowSpan: rows, colSpan: wide));
        var folder = fx.NewFolder();
        var odt = Path.Combine(folder, "out.odt");

        OdtWriter.Write(Document(table), odt);

        var content = ContentXml(odt);
        Assert.Contains($"table:number-rows-spanned=\"{rows}\"", content);
        Assert.Contains($"<table:covered-table-cell table:number-columns-repeated=\"{wide - 1}\"/>", content);
        Assert.Contains($"<table:covered-table-cell table:number-columns-repeated=\"{wide}\"/>", content);
        Assert.True(content.Length < 20_000_000, $"content.xml has {content.Length:N0} characters");

        var back = OdtReader.Read(odt, Path.Combine(folder, "media")).Sections[0].Blocks.OfType<HTable>().Single();
        Assert.Equal(rows, back.Rows.Count);
        Assert.Equal((rows, wide), (back.Rows[0].Cells[0].RowSpan, back.Rows[0].Cells[0].ColSpan));
        Assert.Equal(wide, back.Rows[^1].Cells.Single().Column);
        Assert.Equal("r19999", CellText(back.Rows[^1].Cells.Single()));
    }

    [Fact]
    public void Gaps_in_a_ragged_odt_row_are_one_repeated_cell()
    {
        var table = new HTable { ColumnCount = 1 };
        var first = new HRow();
        first.Cells.Add(Cell("a"));
        first.Cells.Add(Cell("far", column: 5000));
        var second = new HRow();
        second.Cells.Add(Cell("b"));
        table.Rows.Add(first);
        table.Rows.Add(second);
        var folder = fx.NewFolder();
        var odt = Path.Combine(folder, "out.odt");

        OdtWriter.Write(Document(table), odt);

        var content = ContentXml(odt);
        Assert.Contains("<table:table-cell table:number-columns-repeated=\"4999\"/>", content);
        Assert.Contains("<table:table-cell table:number-columns-repeated=\"5000\"/>", content);
        var back = OdtReader.Read(odt, Path.Combine(folder, "media")).Sections[0].Blocks.OfType<HTable>().Single();
        Assert.Equal(5000, back.Rows[0].Cells.Single(c => CellText(c) == "far").Column);
        Assert.Equal(5001, back.Rows[1].Cells.Count);
    }

    [Fact]
    public void Cells_sent_back_to_column_zero_are_placed_without_searching_the_row_again()
    {
        // 100,000 cells in one row, each with an explicit column 0: each goes to the next free column.
        const int cells = 100_000;
        var table = new HTable { ColumnCount = 1 };
        var row = new HRow();
        for (var i = 0; i < cells; i++)
            row.Cells.Add(new HCell { Column = 0 });
        table.Rows.Add(row);

        var watch = Stopwatch.StartNew();
        var (placed, columns) = HwpxWriter.PlaceCells(table);

        Assert.Equal(cells, columns);
        Assert.Equal(Enumerable.Range(0, cells), placed[0].Select(p => p.Column));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    // ───────────────────────── Table size ─────────────────────────

    [Fact]
    public void A_span_of_every_column_on_thousands_of_rows_is_refused_by_every_grid_writer()
    {
        // As a DOCX with gridSpan="16384" on its first row and a thousand empty rows below it would be read.
        HDocument Wide()
        {
            var table = Rows(1_000);
            table.Rows[0].Cells[0].ColSpan = DocumentLimits.Default.MaxTableColumns;
            return Document(table);
        }
        var folder = fx.NewFolder();

        foreach (var write in new Action<HDocument, string>[]
                 {
                     (d, p) => OdtWriter.Write(d, p),
                     (d, p) => HwpxWriter.Write(d, p),
                     (d, p) => DocxWriter.Write(d, p),
                 })
        {
            var output = Path.Combine(folder, $"wide-{Guid.NewGuid():N}");
            var error = Assert.Throws<ConversionRefusedException>(() => write(Wide(), output));
            Assert.Equal(DocumentLimits.ErrorKey, error.ErrorKey);
            Assert.Contains("cells", error.Detail);
            Assert.False(File.Exists(output));
        }
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public void Cells_running_over_each_other_are_refused_once_they_cover_too_many_positions()
    {
        // 16,384 cells, each starting one column further left and spanning to the last column.
        var table = new HTable { ColumnCount = 1 };
        var row = new HRow();
        for (var column = DocumentLimits.Default.MaxTableColumns - 1; column >= 0; column--)
            row.Cells.Add(new HCell { Column = column, ColSpan = int.MaxValue });
        table.Rows.Add(row);

        var error = Assert.Throws<ConversionRefusedException>(() => HwpxWriter.PlaceCells(table));
        Assert.Equal(DocumentLimits.ErrorKey, error.ErrorKey);
    }

    [Fact]
    public void A_pptx_table_grid_past_the_limit_is_refused()
    {
        // The slide grid keeps 75 columns; 140,000 rows of them pass the limit.
        var table = Rows(140_000);
        table.Rows[0].Cells[0].ColSpan = PptxWriter.MaxColumns;
        var output = Path.Combine(fx.NewFolder(), "wide.pptx");

        var error = Assert.Throws<ConversionRefusedException>(() => PptxWriter.Write(Document(table), output));
        Assert.Equal(DocumentLimits.ErrorKey, error.ErrorKey);
        Assert.False(File.Exists(output));
    }

    // ───────────────────────── Long text on slides ─────────────────────────

    private static string Words(int characters)
    {
        var sb = new StringBuilder(characters + 16);
        for (var i = 0; sb.Length < characters; i++)
            sb.Append("word").Append(i % 1000).Append(' ');
        return sb.ToString(0, characters).TrimEnd();
    }

    [Fact]
    public void A_paragraph_over_hundreds_of_slides_keeps_all_its_words_in_order()
    {
        var text = Words(200_000);
        var output = Path.Combine(fx.NewFolder(), "long.pptx");

        PptxWriter.Write(Document(Text(text)), output);

        var slides = PptxWriterTests.Read(output);
        Assert.True(slides.Count > 100, $"{slides.Count} slides");
        var written = string.Join(" ", slides.SelectMany(s => s.Body).Select(p => p.Text));
        Assert.Equal(text.Split(' '), written.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        // Only the first part has the paragraph's own spacing; the parts after it continue the item.
        Assert.All(slides.Skip(1), s => Assert.Equal("none", s.Body.Single().Bullet));
    }

    [Fact]
    public void A_paragraph_too_long_for_the_slide_limit_is_refused_without_measuring_it_per_slide()
    {
        // About 40,000 slides of text: refused at 20,000 slides. Measured again for every slide it would take hours.
        var text = Words(16_000_000);
        var output = Path.Combine(fx.NewFolder(), "huge.pptx");

        var watch = Stopwatch.StartNew();
        var error = Assert.Throws<ConversionRefusedException>(() => PptxWriter.Write(Document(Text(text)), output));

        Assert.Equal(DocumentLimits.ErrorKey, error.ErrorKey);
        Assert.False(File.Exists(output));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"took {watch.Elapsed}");
    }

    [Fact]
    public void A_table_over_many_slides_repeats_its_header_and_keeps_every_row()
    {
        const int rows = 3_000;
        var table = Rows(rows);
        table.Rows[0].Header = true;
        var output = Path.Combine(fx.NewFolder(), "rows.pptx");

        PptxWriter.Write(Document(table), output);

        var slides = PptxWriterTests.Read(output);
        Assert.True(slides.Count > 100, $"{slides.Count} slides");
        var texts = slides.Select(s => s.Xml.InnerText).ToList();
        Assert.All(texts, t => Assert.StartsWith("r0", t));
        var body = string.Concat(texts.Select(t => t[2..]));
        Assert.Equal(string.Concat(Enumerable.Range(1, rows - 1).Select(r => $"r{r}")), body);
    }

    // ───────────────────────── Cancellation and output files ─────────────────────────

    [Fact]
    public void A_cancelled_write_stops_and_leaves_no_file()
    {
        var folder = fx.NewFolder();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var document = Document(Rows(10));

        Assert.ThrowsAny<OperationCanceledException>(() => OdtWriter.Write(document, Path.Combine(folder, "out.odt"), cancelled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => PptxWriter.Write(document, Path.Combine(folder, "out.pptx"), cancelled.Token));

        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public void A_file_named_like_the_old_temporary_file_is_left_alone()
    {
        var folder = fx.NewFolder();
        foreach (var name in new[] { "out.odt", "out.pptx" })
            File.WriteAllText(Path.Combine(folder, name + ".tmp"), "keep me");
        var document = Document(Text("hello"), Rows(2));

        OdtWriter.Write(document, Path.Combine(folder, "out.odt"));
        PptxWriter.Write(document, Path.Combine(folder, "out.pptx"));

        Assert.Equal("keep me", File.ReadAllText(Path.Combine(folder, "out.odt.tmp")));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(folder, "out.pptx.tmp")));
        Assert.True(new FileInfo(Path.Combine(folder, "out.odt")).Length > 0);
        PptxWriterTests.Valid(Path.Combine(folder, "out.pptx"));
        NoTemporaryFiles(folder);
    }

    [Fact]
    public void A_failed_write_removes_its_temporary_file()
    {
        var folder = fx.NewFolder();
        var output = Path.Combine(folder, "out.odt");
        var error = Assert.Throws<ConversionRefusedException>(() =>
            WriterTempFile.Write(output, stream =>
            {
                stream.Write(new byte[1000]);
                throw DocumentLimits.Exceeded("test");
            }));
        Assert.Equal(DocumentLimits.ErrorKey, error.ErrorKey);
        Assert.Empty(Directory.GetFiles(folder));
    }

    // ───────────────────────── Nesting ─────────────────────────

    [Fact]
    public void The_odt_and_pptx_writers_open_up_a_model_nested_thousands_deep()
    {
        const int levels = 5_000;
        var tables = new List<HBlock>();
        var blocks = tables;
        for (var i = 0; i < levels; i++)
        {
            var table = new HTable { ColumnCount = 1 };
            var row = new HRow();
            var cell = new HCell { Column = 0 };
            row.Cells.Add(cell);
            table.Rows.Add(row);
            blocks.Add(table);
            blocks = cell.Blocks;
        }
        blocks.Add(Text("deepest"));
        var folder = fx.NewFolder();

        OdtWriter.Write(Document([.. tables]), Path.Combine(folder, "deep.odt"));
        PptxWriter.Write(Document([.. tables]), Path.Combine(folder, "deep.pptx"));

        Assert.Contains("deepest", ContentXml(Path.Combine(folder, "deep.odt")));
        Assert.Contains("deepest", string.Concat(PptxWriterTests.Read(Path.Combine(folder, "deep.pptx")).Select(s => s.Xml.InnerText)));
    }
}
