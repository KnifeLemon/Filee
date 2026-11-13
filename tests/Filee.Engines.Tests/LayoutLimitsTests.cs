// The layout engine against hostile models (Typesetting/TableLayout.cs, ParagraphLayout.cs, FloatingObjects.cs and
// Hwp/Hwpx/PandocAstReader.cs): spans cut at the column limit and placed without a grid of every spanned cell, pictures
// held to the pixel limit by their codec, and long paragraphs found by position and checking the guard. Everything is
// built in code and finishes in a moment; the old code took minutes or gigabytes on the same input.

using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Typesetting;
using SkiaSharp;

namespace Filee.Engines.Tests;

public sealed class LayoutLimitsTests(EngineFixture fx) : IClassFixture<EngineFixture>, IDisposable
{
    private readonly Shaper _shaper = new();

    public void Dispose() => _shaper.Dispose();

    private static TableLayout Tables() => new((_, _) => throw new InvalidOperationException("the cells hold no paragraphs"), fitToWidth: false);

    // ───────────────────────── Tables ─────────────────────────

    [Fact]
    public void Cells_take_the_free_columns_left_by_spans_from_above()
    {
        var table = new HTable { ColumnCount = 3 };
        table.Rows.Add(Row(new HCell { RowSpan = 2 }, new HCell { ColSpan = 2 }));
        table.Rows.Add(Row(new HCell(), new HCell()));
        table.Rows.Add(Row(new HCell { Column = 1 }, new HCell(), new HCell()));
        table.Rows.Add(Row(new HCell { RowSpan = 50 }, new HCell { ColSpan = 3 }));

        var box = Tables().Layout(table, 400);

        // The last cell of the third row is pushed past the three columns: four of 100 pt.
        Assert.Equal(400, box.Width, 0.01);
        Assert.Equal([0f, 100f], box.Rows[0].Cells.Select(c => c.X));
        Assert.Equal([100f, 200f], box.Rows[1].Cells.Select(c => c.X));
        Assert.Equal([100f, 200f, 300f], box.Rows[2].Cells.Select(c => c.X));
        Assert.Equal([0f, 100f], box.Rows[3].Cells.Select(c => c.X));
        Assert.Equal(300, box.Rows[3].Cells[1].Width, 0.01);
        // A span past the last row ends there.
        Assert.Equal(1, box.Rows[3].Cells[0].RowSpan);
    }

    [Fact]
    public void Cells_spanning_every_column_and_row_are_cut_at_the_column_limit()
    {
        // 20,000 rows, each cell spanning all columns to the end: the old grid held RowSpan × ColSpan entries for each
        // (some 10¹⁶), and each cell after the first moved past all columns taken, to two billion columns.
        const int rows = 20_000;
        var table = new HTable();
        for (var r = 0; r < rows; r++)
            table.Rows.Add(Row(new HCell { RowSpan = int.MaxValue, ColSpan = int.MaxValue }));

        var box = Tables().Layout(table, 500);

        Assert.Equal(rows, box.Rows.Count);
        Assert.Equal(500, box.Width, 0.01);
        var cells = box.Rows.SelectMany(r => r.Cells).ToList();
        Assert.Equal(rows, cells.Count);
        Assert.All(cells, c => Assert.InRange(c.X + c.Width, 0, 500.01f));
        Assert.Equal(rows, cells[0].RowSpan);
    }

    [Fact]
    public void Pandoc_table_spans_and_columns_stay_within_the_limit()
    {
        var limit = DocumentLimits.Default.MaxTableColumns;
        JsonNode Cell(JsonNode rowSpan, JsonNode colSpan) => new JsonArray(Attr(), new JsonObject { ["t"] = "AlignDefault" }, rowSpan, colSpan, new JsonArray());
        var specs = new JsonArray();
        for (var i = 0; i < limit + 100; i++)
            specs.Add(new JsonArray(new JsonObject { ["t"] = "AlignDefault" }, new JsonObject { ["t"] = "ColWidthDefault" }));
        var body = new JsonArray(new JsonArray(Attr(), 0, new JsonArray(), new JsonArray(
            new JsonArray(Attr(), new JsonArray(Cell(1e300, int.MaxValue), Cell("x", -5))),
            new JsonArray(Attr(), new JsonArray(Cell(int.MaxValue, 2_000_000_000))))));
        var ast = new JsonObject
        {
            ["blocks"] = new JsonArray(new JsonObject
            {
                ["t"] = "Table",
                ["c"] = new JsonArray(Attr(), new JsonArray(null, new JsonArray()), specs, new JsonArray(Attr(), new JsonArray()), body,
                    new JsonArray(Attr(), new JsonArray())),
            }),
        };

        var document = PandocAstReader.Read(ast, _ => null);

        var table = Assert.IsType<HTable>(Assert.Single(document.Sections[0].Blocks));
        Assert.Equal(limit, table.ColumnCount);
        var cells = table.Rows.SelectMany(r => r.Cells).ToList();
        Assert.Equal([limit, 1, limit], cells.Select(c => c.ColSpan));
        Assert.Equal([1, 1, int.MaxValue], cells.Select(c => c.RowSpan));
        var box = Tables().Layout(table, 500);
        Assert.Equal(500, box.Width, 0.01);
        Assert.Equal(2, box.Rows.Count);
    }

    private static JsonArray Attr() => new("", new JsonArray(), new JsonArray());

    private static HRow Row(params HCell[] cells)
    {
        var row = new HRow();
        row.Cells.AddRange(cells);
        return row;
    }

    // ───────────────────────── Pictures ─────────────────────────

    [Fact]
    public void A_picture_is_decoded_only_when_its_codec_reports_no_more_pixels_than_the_limit()
    {
        var dir = fx.NewFolder();
        // An icon holding a PNG of 40,000 × 40,000 pixels: a few dozen bytes that would decode to some 6 GB. The
        // header check (PictureSize) doesn't read icons; the codec does.
        var png = Png(40_000, 40_000);
        var icon = Path.Combine(dir, "huge.ico");
        File.WriteAllBytes(icon, Icon(png));
        var plain = Path.Combine(dir, "huge.png");
        File.WriteAllBytes(plain, png);
        var small = Path.Combine(dir, "small.png");
        using (var bitmap = new SKBitmap(4, 3))
        using (var data = bitmap.Encode(SKEncodedImageFormat.Png, 100))
            File.WriteAllBytes(small, data.ToArray());

        Assert.Null(PictureSize.Read(icon));
        using (var codec = SKCodec.Create(icon))
            Assert.Equal(40_000, codec.Info.Width);
        Assert.Null(FloatingObjects.Image(icon));
        Assert.Null(FloatingObjects.Image(plain));
        Assert.Null(FloatingObjects.Image(Path.Combine(dir, "missing.png")));
        using var image = FloatingObjects.Image(small);
        Assert.NotNull(image);
        Assert.Equal(4, image.Width);
    }

    /// <summary>A PNG whose header claims <paramref name="width"/> × <paramref name="height"/>, with a token of image data.</summary>
    private static byte[] Png(int width, int height)
    {
        using var stream = new MemoryStream();
        stream.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 6; // RGBA
        Chunk(stream, "IHDR", header);
        Chunk(stream, "IDAT", [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01]);
        Chunk(stream, "IEND", []);
        return stream.ToArray();
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        stream.Write(word);
        var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        stream.Write(body);
        BinaryPrimitives.WriteUInt32BigEndian(word, Crc32(body));
        stream.Write(word);
    }

    /// <summary>The CRC-32 a PNG chunk ends with (ISO 3309, reflected 0xEDB88320).</summary>
    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    /// <summary>An ICO holding one PNG.</summary>
    private static byte[] Icon(byte[] png)
    {
        var icon = new byte[6 + 16 + png.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(icon.AsSpan(2), 1); // icon
        BinaryPrimitives.WriteUInt16LittleEndian(icon.AsSpan(4), 1); // one image
        BinaryPrimitives.WriteUInt16LittleEndian(icon.AsSpan(6 + 4), 1); // planes
        BinaryPrimitives.WriteUInt16LittleEndian(icon.AsSpan(6 + 6), 32); // bits per pixel
        BinaryPrimitives.WriteInt32LittleEndian(icon.AsSpan(6 + 8), png.Length);
        BinaryPrimitives.WriteInt32LittleEndian(icon.AsSpan(6 + 12), 22);
        png.CopyTo(icon, 22);
        return icon;
    }

    // ───────────────────────── Paragraphs ─────────────────────────

    [Fact]
    public void A_paragraph_of_many_runs_in_a_narrow_column_finds_each_lines_runs_bookmarks_and_notes_by_position()
    {
        var fonts = RepositoryFonts.Load();
        Assert.SkipUnless(fonts.Has(RepositoryFonts.Latin), "Noto Sans is not in src/Filee.App/Assets/Fonts (build/fetch-fonts.ps1)");
        var layouter = new ParagraphLayouter(new FontResolver(fonts), _shaper);
        // 20,000 one-letter runs of alternating weight, a bookmark at each and a note at every tenth, in a column
        // narrower than a letter: a line per letter, each of which used to look through every run, bookmark and note.
        const int runs = 20_000;
        var paragraph = new HParagraph();
        var notes = 0;
        for (var i = 0; i < runs; i++)
        {
            paragraph.Inlines.Add(new HBookmark("b" + i));
            paragraph.Inlines.Add(new HText("x", new HCharFormat(Size: 1000, Font: RepositoryFonts.Latin, Bold: i % 2 == 0)));
            if (i % 10 == 0)
            {
                paragraph.Inlines.Add(new HNote(endnote: false));
                notes++;
            }
        }

        var box = layouter.Layout(paragraph, 1, notes: _ => "");

        Assert.Equal(runs, box.Lines.Count);
        Assert.Equal(runs, box.Lines.Sum(l => l.Bookmarks.Count));
        Assert.Equal(notes, box.Lines.Sum(l => l.Notes.Count));
        Assert.All(box.Lines, l => Assert.Single(l.Runs));
    }

    [Fact]
    public void A_long_paragraph_checks_the_guard_as_its_lines_are_laid_out()
    {
        var fonts = RepositoryFonts.Load();
        Assert.SkipUnless(fonts.Has(RepositoryFonts.Latin), "Noto Sans is not in src/Filee.App/Assets/Fonts (build/fetch-fonts.ps1)");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var layouter = new ParagraphLayouter(new FontResolver(fonts), _shaper) { Guard = new LayoutGuard(DocumentLimits.Default, cancelled.Token) };
        var paragraph = new HParagraph();
        paragraph.Inlines.Add(new HText(new string('x', 1000), new HCharFormat(Size: 1000, Font: RepositoryFonts.Latin)));

        Assert.Throws<OperationCanceledException>(() => layouter.Layout(paragraph, 1));
        // A short paragraph lays out before the first check.
        paragraph.Inlines[0] = new HText("short", new HCharFormat(Size: 1000, Font: RepositoryFonts.Latin));
        Assert.NotEmpty(layouter.Layout(paragraph, 100).Lines);
    }
}
