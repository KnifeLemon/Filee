// Files a few bytes long that ask for gigabytes or hours: repeated spaces in a spreadsheet cell, table spans over
// billions of cells, a run of a hundred thousand spaces, folders nested tens of thousands deep, a MOBI dictionary of
// phrases made of phrases, copies of packages that unpack without end, and pictures that are not pictures. Each must
// give a bounded result or a reasoned refusal, quickly. Every input is built here.

using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using Filee.Core.Conversion;
using Filee.Engines.Archives;
using Filee.Engines.Ebooks;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Infrastructure;
using Filee.Engines.Magick;
using Filee.Engines.Office.Sheets;
using ImageMagick;

namespace Filee.Engines.Tests;

public class ConversionBudgetTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string TooLarge = "error.document_too_large";

    // ───────────────────────── ODS ─────────────────────────

    private string Ods(string rows)
    {
        var path = Path.Combine(fx.NewFolder(), "a.ods");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("mimetype", CompressionLevel.NoCompression).Open()))
            writer.Write("application/vnd.oasis.opendocument.spreadsheet");
        using (var writer = new StreamWriter(zip.CreateEntry("content.xml").Open()))
        {
            writer.Write("<office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" " +
                         "xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\">" +
                         "<office:body><office:spreadsheet><table:table table:name=\"S\">" + rows +
                         "</table:table></office:spreadsheet></office:body></office:document-content>");
        }
        return path;
    }

    [Fact]
    public void Ods_repeated_spaces_are_cut_at_a_thousand()
    {
        var book = OdsReader.Read(Ods("<table:table-row><table:table-cell office:value-type=\"string\">" +
                                      "<text:p>a<text:s text:c=\"1000000000\"/>b<text:s text:c=\"3\"/>c</text:p></table:table-cell></table:table-row>"));

        Assert.Equal("a" + new string(' ', 1024) + "b   c", book.Sheets[0].Get(0, 0)!.Text);
    }

    [Fact]
    public void An_ods_cell_repeated_over_the_whole_sheet_is_refused()
    {
        var path = Ods("<table:table-row table:number-rows-repeated=\"1048576\"><table:table-cell table:number-columns-repeated=\"16384\" " +
                       "office:value-type=\"string\"><text:p>x</text:p></table:table-cell></table:table-row>");

        Assert.Equal(TooLarge, Assert.Throws<ConversionRefusedException>(() => OdsReader.Read(path)).ErrorKey);
    }

    // ───────────────────────── Tables ─────────────────────────

    private static HCell Cell(string text, int rowSpan = 1, int colSpan = 1, int column = -1)
    {
        var cell = new HCell { RowSpan = rowSpan, ColSpan = colSpan, Column = column };
        var paragraph = new HParagraph();
        paragraph.Inlines.Add(new HText(text, default));
        cell.Blocks.Add(paragraph);
        return cell;
    }

    private static HDocument WithTable(HTable table)
    {
        var section = new HSection();
        section.Blocks.Add(table);
        var document = new HDocument();
        document.Sections.Add(section);
        return document;
    }

    [Fact]
    public void Spans_past_the_table_are_cut_to_it()
    {
        var table = new HTable();
        var row = new HRow();
        row.Cells.Add(Cell("wide", rowSpan: 100_000, colSpan: int.MaxValue));
        table.Rows.Add(row);

        var (placed, columns) = HwpxWriter.PlaceCells(table);

        Assert.Equal(1, row.Cells[0].RowSpan);
        Assert.Equal(HwpxWriter.MaxTableColumns, row.Cells[0].ColSpan);
        Assert.Equal(HwpxWriter.MaxTableColumns, columns);
        Assert.Equal(0, Assert.Single(placed[0]).Column);
    }

    [Fact]
    public void A_cell_past_the_widest_table_is_refused()
    {
        var table = new HTable();
        var row = new HRow();
        row.Cells.Add(Cell("far", column: 2_000_000_000));
        table.Rows.Add(row);

        Assert.Equal(TooLarge, Assert.Throws<ConversionRefusedException>(() => HwpxWriter.PlaceCells(table)).ErrorKey);
    }

    [Fact]
    public void A_table_of_spans_over_billions_of_cells_is_refused_by_the_writers()
    {
        var table = new HTable();
        for (var r = 0; r < 1000; r++)
        {
            var row = new HRow();
            row.Cells.Add(Cell("x", rowSpan: 100_000, colSpan: 16_384));
            table.Rows.Add(row);
        }
        var document = WithTable(table);
        var folder = fx.NewFolder();

        Assert.Equal(TooLarge, Assert.Throws<ConversionRefusedException>(() => HwpxWriter.Write(document, Path.Combine(folder, "a.hwpx"))).ErrorKey);
        Assert.Equal(TooLarge, Assert.Throws<ConversionRefusedException>(() => MarkdownWriter.Write(document, _ => null)).ErrorKey);
    }

    [Fact]
    public void Ordinary_spans_place_as_before()
    {
        var table = new HTable();
        var first = new HRow();
        first.Cells.Add(Cell("a", rowSpan: 2));
        first.Cells.Add(Cell("b", colSpan: 2));
        var second = new HRow();
        second.Cells.Add(Cell("c"));
        second.Cells.Add(Cell("d"));
        table.Rows.Add(first);
        table.Rows.Add(second);

        var (placed, columns) = HwpxWriter.PlaceCells(table);

        Assert.Equal(3, columns);
        Assert.Equal(new[] { 0, 1 }, placed[0].Select(p => p.Column));
        Assert.Equal(new[] { 1, 2 }, placed[1].Select(p => p.Column));
        Assert.Contains("| a | b |", MarkdownWriter.Write(WithTable(table), _ => null));
    }

    // ───────────────────────── HWPX text ─────────────────────────

    [Fact]
    public void A_run_of_a_hundred_thousand_spaces_reads_in_one_piece()
    {
        const int Spaces = 300_000;
        var paragraph = new HParagraph();
        paragraph.Inlines.Add(new HText("MARK", default));
        var section = new HSection();
        section.Blocks.Add(paragraph);
        var document = new HDocument();
        document.Sections.Add(section);
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "a.hwpx");
        HwpxWriter.Write(document, path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("Contents/section0.xml")!;
            string xml;
            using (var reader = new StreamReader(entry.Open()))
                xml = reader.ReadToEnd();
            Assert.Contains("MARK</hp:t>", xml);
            xml = xml.Replace("MARK</hp:t>", "MARK" + string.Concat(Enumerable.Repeat("<hp:nbSpace/>", Spaces)) + "END</hp:t>", StringComparison.Ordinal);
            entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("Contents/section0.xml").Open());
            writer.Write(xml);
        }

        var read = HwpxReader.Read(path, Path.Combine(folder, "media"));

        var text = string.Concat(read.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HText>().Select(t => t.Text));
        Assert.Equal("MARK" + new string('\u00A0', Spaces) + "END", text); // hp:nbSpace is a no-break space
    }

    // ───────────────────────── Archives ─────────────────────────

    private static string Deep(int folders) => string.Concat(Enumerable.Repeat("a/", folders)) + "f.txt";

    [Fact]
    public void Entries_nested_in_more_than_512_folders_are_refused()
    {
        var root = fx.NewFolder();

        Assert.Throws<InvalidDataException>(() => ArchiveSafety.ResolveEntryPath(root, Deep(16_000), "deep.alz"));
        Assert.Throws<InvalidDataException>(() => ArchiveSafety.CheckListing([new SevenZipEntry(Deep(16_000), false, 1, false, null, null)], "deep.tar", root));
        Assert.Throws<InvalidDataException>(() => ArchiveSafety.CheckListing([new SevenZipEntry("f.txt", false, 1, false, null, Deep(600))], "deep.tar", root));
        Assert.False(ArchiveSafety.IsTooDeep(Deep(ArchiveSafety.MaxPathDepth - 1)));
        Assert.True(ArchiveSafety.IsTooDeep(Deep(ArchiveSafety.MaxPathDepth)));
        Assert.False(ArchiveSafety.IsTooDeep("./a//b/./c.txt"));
    }

    // ───────────────────────── MOBI HUFF/CDIC ─────────────────────────

    /// <summary>A HUFF table of 8-bit codes: byte b stands for phrase 255 − b.</summary>
    private static byte[] Huff()
    {
        var huff = new byte[24 + 256 * 4 + 32 * 8];
        "HUFF"u8.CopyTo(huff);
        BinaryPrimitives.WriteUInt32BigEndian(huff.AsSpan(4), 24);
        BinaryPrimitives.WriteUInt32BigEndian(huff.AsSpan(8), 24);
        BinaryPrimitives.WriteUInt32BigEndian(huff.AsSpan(12), 24 + 256 * 4);
        for (var i = 0; i < 256; i++)
            BinaryPrimitives.WriteUInt32BigEndian(huff.AsSpan(24 + i * 4), (255u << 8) | 0x80 | 8);
        return huff;
    }

    /// <summary>A CDIC record of the phrases; a phrase is stored as it is (true) or HUFF-coded itself.</summary>
    private static byte[] Cdic(params (byte[] Data, bool Literal)[] phrases)
    {
        using var data = new MemoryStream();
        var offsets = new List<int>();
        foreach (var (bytes, literal) in phrases)
        {
            offsets.Add(phrases.Length * 2 + (int)data.Length);
            data.Write([(byte)((bytes.Length >> 8) | (literal ? 0x80 : 0)), (byte)bytes.Length]);
            data.Write(bytes);
        }
        var cdic = new byte[16 + phrases.Length * 2 + data.Length];
        "CDIC"u8.CopyTo(cdic);
        BinaryPrimitives.WriteUInt32BigEndian(cdic.AsSpan(4), 16);
        BinaryPrimitives.WriteUInt32BigEndian(cdic.AsSpan(8), (uint)phrases.Length);
        BinaryPrimitives.WriteUInt32BigEndian(cdic.AsSpan(12), 8);
        for (var i = 0; i < offsets.Count; i++)
            BinaryPrimitives.WriteUInt16BigEndian(cdic.AsSpan(16 + i * 2), (ushort)offsets[i]);
        data.ToArray().CopyTo(cdic, 16 + phrases.Length * 2);
        return cdic;
    }

    private static byte[] Codes(int phrase, int count) => Enumerable.Repeat((byte)(255 - phrase), count).ToArray();

    [Fact]
    public void Huff_cdic_text_unpacks_as_written()
    {
        var huff = new HuffCdic(Huff(), [Cdic(("AB"u8.ToArray(), true), (Codes(0, 3), false))], 1024);

        Assert.Equal("ABABABAB", Encoding.ASCII.GetString(huff.Decompress([.. Codes(1, 1), .. Codes(0, 1)])));
    }

    [Fact]
    public void Huff_cdic_phrases_of_phrases_stop_at_the_text_limit()
    {
        // Each level multiplies by 4000: the record stands for 4000^4 × 2 bytes, some 500 TB.
        var huff = new HuffCdic(Huff(), [Cdic(("AB"u8.ToArray(), true), (Codes(0, 4000), false), (Codes(1, 4000), false), (Codes(2, 4000), false))],
            16 * 1024 * 1024);

        Assert.Equal(TooLarge, Assert.Throws<ConversionRefusedException>(() => huff.Decompress(Codes(3, 4000))).ErrorKey);
    }

    // ───────────────────────── Copies of packages ─────────────────────────

    private string Zip(params (string Name, int Bytes)[] entries)
    {
        var path = Path.Combine(fx.NewFolder(), "a.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(new byte[bytes]);
        }
        return path;
    }

    [Fact]
    public void Package_copies_are_counted_against_the_unpack_limit()
    {
        using var zip = ZipFile.OpenRead(Zip(("a", 60_000), ("b", 60_000)));
        var limit = new UnpackLimit(100_000);

        limit.Copy(zip.Entries[0], Stream.Null);
        Assert.Equal(TooLarge, Assert.Throws<ConversionRefusedException>(() => limit.Copy(zip.Entries[1], Stream.Null)).ErrorKey);
    }

    [Fact]
    public void A_part_read_whole_has_its_own_limit()
    {
        using var zip = ZipFile.OpenRead(Zip(("chapter.xhtml", 500_000)));
        var limit = new UnpackLimit(10_000_000);

        Assert.Equal(500_000, limit.Read(zip.Entries[0], 500_000).Length);
        Assert.Equal(TooLarge, Assert.Throws<ConversionRefusedException>(() => limit.Read(zip.Entries[0], 499_999)).ErrorKey);
    }

    // ───────────────────────── ImageMagick ─────────────────────────

    [Fact]
    public void Every_format_imagemagick_converts_for_filee_is_allowed()
    {
        // Metafiles go through GDI+ and ImageMagick has their coders on Windows only, as Filee offers them.
        var formats = ImageEncoder.Readable.Concat(ImageEncoder.Writable)
            .Concat(OperatingSystem.IsWindows() ? ImageEncoder.Metafiles : [])
            .Where(f => f is not ("fits" or "ani")) // read by Filee itself
            .Distinct();
        foreach (var format in formats)
        {
            var magick = format switch
            {
                "raw" => MagickFormat.Dng,
                "xcf" => MagickFormat.Xcf,
                "emf" => MagickFormat.Emf,
                "wmf" => MagickFormat.Wmf,
                _ => ImageEncoder.ToMagickFormat(format),
            };
            var module = MagickFormatInfo.Create(magick)!.ModuleFormat.ToString().ToUpperInvariant();
            Assert.True(MagickSecurity.Modules.Contains(module), $"{format}: module {module} is not allowed");
        }
    }

    [Fact]
    public void Imagemagick_refuses_what_filee_does_not_convert()
    {
        var postScript = "%!PS-Adobe-3.0\n%%BoundingBox: 0 0 10 10\nshowpage\n"u8.ToArray();
        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"><rect width=\"10\" height=\"10\" fill=\"red\"/></svg>"u8.ToArray();

        Assert.Throws<MagickPolicyErrorException>(() => new MagickImage(postScript));
        Assert.Throws<MagickPolicyErrorException>(() => new MagickImage("hello"u8.ToArray(), MagickFormat.Txt));
        Assert.Throws<MagickPolicyErrorException>(() => new MagickImage(svg, MagickFormat.Msvg));
        Assert.NotEmpty(MagickNET.SupportedFormats); // Magick.NET looks up formats of its own when it lists them
        using (var red = new MagickImage(MagickColors.Red, 2, 2))
        {
            var pdf = red.ToByteArray(MagickFormat.Pdf); // written: PDF is only read through Ghostscript
            Assert.Throws<MagickPolicyErrorException>(() => new MagickImage(pdf));
        }
        Assert.ThrowsAny<MagickException>(() => new MagickImage(MagickColors.Red, MagickSecurity.MaxSide + 1, 1));

        // SVG pictures of HTML documents are still rasterized (by librsvg).
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "a.svg");
        File.WriteAllBytes(path, svg);
        var png = HtmlReader.UsableImage(path, Path.Combine(folder, "media"));
        Assert.NotNull(png);
        using var image = new MagickImage(png);
        Assert.Equal(MagickFormat.Png, image.Format);
    }
}

/// <summary>ImageMagick reads its policy once: it is set before any test of this assembly can start ImageMagick.</summary>
internal static class MagickPolicyFirst
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification = "Tests that use ImageMagick directly may run before any engine code.")]
    internal static void Initialize() => MagickSecurity.Initialize();
}
