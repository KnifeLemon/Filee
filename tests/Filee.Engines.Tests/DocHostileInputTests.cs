// Word 97–2003 (DOC) files built in code that are legal in shape but hostile in size: one picture shown by thousands
// of characters, metafiles that inflate far beyond their packed size, pieces that share one stretch of text past the
// stories the FIB counts, and thousands of bookmarks, sections and styles of one name. Each must be refused through the
// document limits or read quickly, writing each picture once.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx.Doc;
using Filee.Engines.Infrastructure;
using static Filee.Engines.Tests.DocBuilder;

namespace Filee.Engines.Tests;

public class DocHostileInputTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private string Doc(DocBuilder builder) => Doc(builder.Build());

    private string Doc(byte[] bytes)
    {
        var path = Path.Combine(fx.NewFolder(), "hostile.doc");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>The DOCX Filee writes for <paramref name="doc"/>, within <paramref name="seconds"/>.</summary>
    private string ToDocx(string doc, DocumentLimits? limits = null, int seconds = 60)
    {
        var docx = Path.Combine(fx.NewFolder(), "out.docx");
        Within(seconds, () => DocReader.ToDocx(doc, docx, limits));
        return docx;
    }

    private static void Within(int seconds, Action action)
    {
        var task = Task.Run(action);
        Assert.True(task.Wait(TimeSpan.FromSeconds(seconds)), $"Still running after {seconds} s");
    }

    private static ConversionRefusedException Refused(Action action)
    {
        var refusal = Assert.ThrowsAny<ConversionRefusedException>(action);
        Assert.Equal(DocumentLimits.ErrorKey, refusal.ErrorKey);
        return refusal;
    }

    private static string Part(string docx, string name)
    {
        using var zip = ZipFile.OpenRead(docx);
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    private static int Count(string text, string what)
    {
        var count = 0;
        for (var at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + what.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>An EMF-sized block of bytes that packs well: a header-like start, then a repeated pattern.</summary>
    private static byte[] Metafile(int length, byte seed)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
            bytes[i] = (byte)(seed + i % 7);
        return bytes;
    }

    // ───────────────────────── Pictures ─────────────────────────

    [Fact]
    public void A_picture_shown_by_thousands_of_characters_is_written_once()
    {
        // One 1 MB metafile packed into a few KB, shown by 3,000 characters: 3 GB of word/media before it was cached.
        var builder = new DocBuilder();
        var picture = builder.PictureOf(EmfBlip(Metafile(1 << 20, 1)), 1440, 1440);
        builder.Paragraph(R("pictures "), new Run(new string('\u0001', 3000), picture.Chpx));
        var docx = ToDocx(Doc(builder), seconds: 30);

        using (var zip = ZipFile.OpenRead(docx))
        {
            var media = zip.Entries.Where(e => e.FullName.StartsWith("word/media/", StringComparison.Ordinal)).ToList();
            var emf = Assert.Single(media);
            Assert.Equal(1 << 20, emf.Length);
            Assert.True(emf.CompressedLength < emf.Length / 10, "Metafiles are packed in the DOCX");
        }
        Assert.Equal(3000, Count(Part(docx, "word/document.xml"), "r:embed=\"rId"));
        Assert.Equal(1, Count(Part(docx, "word/_rels/document.xml.rels"), "/image\""));
    }

    [Fact]
    public void A_metafile_that_inflates_far_beyond_its_packed_size_is_refused()
    {
        // 16 MB of one byte packs to about 16 KB: a ratio near 1,000, past the limit of 200.
        var builder = new DocBuilder();
        builder.Paragraph(R("bomb "), builder.PictureOf(EmfBlip(new byte[16 << 20]), 1440, 1440));
        var doc = Doc(builder);
        var docx = Path.Combine(fx.NewFolder(), "out.docx");
        Assert.Contains("times its packed size", Refused(() => DocReader.ToDocx(doc, docx)).Detail);
        Assert.False(File.Exists(docx), "No partial DOCX is left behind");
    }

    [Fact]
    public void What_pictures_unpack_to_counts_against_the_document_total()
    {
        // Six different 2 MB metafiles (each below the ratio floor) in a document allowed 8 MB in all.
        var builder = new DocBuilder();
        var runs = Enumerable.Range(0, 6).Select(i => builder.PictureOf(EmfBlip(Metafile(2 << 20, (byte)(i * 11))), 1440, 1440)).ToArray();
        builder.Paragraph(runs);
        var doc = Doc(builder);
        var limits = DocumentLimits.Default with { MaxPackageBytes = 8L << 20 };
        Assert.Contains("unpacks to more than 8 MB", Refused(() => DocReader.ToDocx(doc, Path.Combine(fx.NewFolder(), "out.docx"), limits)).Detail);
        // Within the default limits the same document is written, each picture once.
        using var zip = ZipFile.OpenRead(ToDocx(doc));
        Assert.Equal(6, zip.Entries.Count(e => e.FullName.StartsWith("word/media/", StringComparison.Ordinal)));
    }

    // ───────────────────────── Piece table ─────────────────────────

    /// <summary>
    /// A DOC whose main text is <paramref name="text"/>, with <paramref name="extra"/> more pieces of 4,096 characters
    /// after the stories the FIB counts, all sharing one stretch of Arabic text appended to the WordDocument stream.
    /// </summary>
    private string SharedPieces(string text, int extra)
    {
        var built = new DocBuilder();
        built.Paragraph(R(text));
        var locations = built.Locations;
        var bytes = built.Build();
        byte[] word, table;
        var others = new List<(string, byte[])>();
        using (var cfb = Documents.CompoundFile.Open(new MemoryStream(bytes)))
        {
            word = cfb.Read("WordDocument")!;
            table = cfb.Read("1Table")!;
        }
        const int Piece = 4096;
        var shared = word.Length;
        word = [.. word, .. Encoding.Unicode.GetBytes(string.Concat(Enumerable.Repeat("مرحبا ", Piece / 6 + 1))[..Piece])];

        // The old piece table's PLC, then the extra pieces.
        var (fcClx, lcbClx) = locations[33];
        var plc = table.AsSpan(fcClx + 5, lcbClx - 5);
        var count = (plc.Length - 4) / 12;
        var cps = new List<int>();
        var pcds = new List<byte[]>();
        for (var i = 0; i <= count; i++)
            cps.Add(BinaryPrimitives.ReadInt32LittleEndian(plc[(i * 4)..]));
        for (var i = 0; i < count; i++)
            pcds.Add(plc.Slice((count + 1) * 4 + i * 8, 8).ToArray());
        for (var i = 0; i < extra; i++)
        {
            cps.Add(cps[^1] + Piece);
            pcds.Add(HwpBuilder.Bytes((ushort)0, shared, (ushort)0));
        }
        var newPlc = (byte[])[.. cps.SelectMany(BitConverter.GetBytes), .. pcds.SelectMany(p => p)];
        var clx = (byte[])[0x02, .. BitConverter.GetBytes(newPlc.Length), .. newPlc];
        var at = table.Length;
        table = [.. table, .. clx];
        BinaryPrimitives.WriteInt32LittleEndian(word.AsSpan(154 + 33 * 8), at);
        BinaryPrimitives.WriteInt32LittleEndian(word.AsSpan(158 + 33 * 8), clx.Length);
        return Doc(HwpBuilder.Cfb([("WordDocument", word), ("1Table", table)]));
    }

    [Fact]
    public void The_script_scan_reads_only_the_stories_the_fib_counts()
    {
        // 20,000 pieces of 4,096 characters past the main text, all on one stretch of Arabic: 80 million characters
        // the scan built into one string before; they hold no story, so neither the scan nor the conversion sees them.
        var shared = SharedPieces("Plain English text", 20_000);
        string? script = "unset";
        Within(30, () => script = ComplexScripts.FindInFile(shared, "doc"));
        Assert.Null(script);
        var docx = ToDocx(shared, seconds: 30);
        Assert.DoesNotContain("مرحبا", Part(docx, "word/document.xml"));

        // Arabic in the main text is still found.
        Assert.Equal("Arabic", ComplexScripts.FindInFile(SharedPieces("Report: مرحبا بالعالم", 0), "doc"));
        // And in the other stories the FIB counts (a footnote here).
        var note = new DocBuilder();
        note.Paragraph(R("Plain text"), note.Footnote("مرحبا بالعالم"));
        Assert.Equal("Arabic", ComplexScripts.FindInFile(Doc(note), "doc"));
    }

    // ───────────────────────── Bookmarks, sections and styles ─────────────────────────

    [Fact]
    public void Thousands_of_bookmarks_sections_and_styles_of_one_name_are_read_quickly()
    {
        var builder = new DocBuilder();
        for (var i = 0; i < 3000; i++)
            builder.Bookmark($"mark{i}", 1).Paragraph(R("x"));
        for (var i = 0; i < 2000; i++)
            builder.EndSection([], R("s"));
        for (var i = 0; i < 2000; i++)
            builder.Styles.Add(new Style("Same", 0xFFE, 1, 0, [], []));
        var docx = ToDocx(Doc(builder), seconds: 60);

        var document = Part(docx, "word/document.xml");
        Assert.Equal(3000, Count(document, "<w:bookmarkStart "));
        Assert.Contains("w:name=\"mark2999\"", document);
        // 2,000 section ends; what follows the last is empty and adds no section.
        Assert.Equal(2000, Count(document, "<w:sectPr"));
        var styles = Part(docx, "word/styles.xml");
        Assert.Contains("w:styleId=\"Same\"", styles);
        Assert.Contains("w:styleId=\"Same2000\"", styles);
        Assert.DoesNotContain("w:styleId=\"Same2001\"", styles);
    }
}
