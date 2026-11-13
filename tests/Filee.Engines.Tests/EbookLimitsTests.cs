// Hostile e-books against the readers' limits, each built in code and refused in well under a second: PML whose open
// codes are repeated at every paragraph, Rocket eBook files of empty chunks or listed many times over, SNB text that
// passes the inflate ratio long before its last block, and MOBI HUFF/CDIC phrases made of phrases.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Ebooks;
using static Filee.Engines.Tests.ArchiveBuilders;

namespace Filee.Engines.Tests;

public class EbookLimitsTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void TooComplex(Action read) =>
        Assert.Equal(DocumentLimits.ErrorKey, Assert.Throws<ConversionRefusedException>(read).ErrorKey);

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(fx.NewFolder(), name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // ───────── PML ─────────

    /// <summary>Every inline code PML has, opened once.</summary>
    private const string AllOpen = @"\i\u\o\B\l\k\Sp\Sb";

    [Fact]
    public void Pml_whose_open_codes_repeat_at_every_paragraph_stops_at_the_limit()
    {
        var limits = DocumentLimits.Default with { MaxPartBytes = 1 << 20 };
        // 200 KB of "a\n" with eight codes open: some 15 MB of HTML.
        TooComplex(() => PmlReader.ToHtml(AllOpen + string.Concat(Enumerable.Repeat("a\n", 100_000)), _ => null, Ct, limits));
        // One line that closes the code opened first (and opens the seven after it again), then opens it last: some 30
        // times its size.
        TooComplex(() => PmlReader.ToHtml(AllOpen + string.Concat(Enumerable.Repeat(@"\i\i\u\u\o\o\B\B\l\l\k\k\Sp\Sp\Sb\Sb", 10_000)), _ => null, Ct, limits));
        // Notes are pages of their own and count as well.
        TooComplex(() => PmlReader.ToHtml(string.Concat(Enumerable.Repeat("<footnote id=\"n\">" + AllOpen + "a\na\na\n</footnote>", 5_000)), _ => null, Ct, limits));

        var html = PmlReader.ToHtml(AllOpen + "a\nb\n", _ => null, Ct, limits);
        Assert.Contains("<i><u><s><b>", html, StringComparison.Ordinal);
    }

    // ───────── Rocket eBook ─────────

    /// <summary>An RB whose table of contents lists <paramref name="count"/> HTML files, all of them the bytes <paramref name="raw"/>.</summary>
    private static byte[] Rocket(int count, byte[] raw, int flags)
    {
        const int TocOffset = 0x128;
        var header = new byte[TocOffset];
        new byte[] { 0xB0, 0x0C, 0xB0, 0x0C, 0x02, 0x00, (byte)'N', (byte)'U', (byte)'V', (byte)'O' }.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x18), TocOffset);
        var dataOffset = TocOffset + 4 + count * 44;
        using var output = new MemoryStream();
        output.Write(header);
        output.Write(BitConverter.GetBytes(count));
        for (var i = 0; i < count; i++)
        {
            var name = new byte[32];
            Encoding.ASCII.GetBytes($"page{i}.html").CopyTo(name, 0);
            output.Write(name);
            output.Write(BitConverter.GetBytes(raw.Length));
            output.Write(BitConverter.GetBytes(dataOffset));
            output.Write(BitConverter.GetBytes(flags));
        }
        output.Write(raw);
        return output.ToArray();
    }

    /// <summary>A compressed RB file: <paramref name="chunks"/> chunks for <paramref name="size"/> bytes, all empty but the first.</summary>
    private static byte[] RocketChunks(int chunks, int size)
    {
        using var first = new MemoryStream();
        using (var zlib = new ZLibStream(first, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write("<html><body><p>x</p></body></html>"u8);
        var raw = new byte[8 + chunks * 4 + first.Length];
        BinaryPrimitives.WriteInt32LittleEndian(raw, chunks);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(4), size);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(8), (int)first.Length);
        first.ToArray().CopyTo(raw, 8 + chunks * 4);
        return raw;
    }

    [Fact]
    public void Rocket_ebook_with_more_chunks_than_its_size_needs_is_damaged()
    {
        // A million chunk entries for one 4 KB file: the reader stops at the count, not after a million inflaters.
        var rb = Write("chunks.rb", Rocket(1, RocketChunks(1_000_000, 4096), flags: 1));
        Assert.Throws<InvalidDataException>(() => RocketBookReader.ReadBook(rb, fx.NewFolder(), Ct));
    }

    [Fact]
    public void Rocket_ebook_listing_one_file_many_times_is_refused_by_the_sizes_it_states()
    {
        // Twenty entries of one compressed file that claims 60 MB: 1.2 GB stated, past the package limit before any
        // of the (empty) chunks is inflated.
        var rb = Write("listed.rb", Rocket(20, RocketChunks(2, 60 * 1024 * 1024 - 4096), flags: 1));
        TooComplex(() => RocketBookReader.ReadBook(rb, fx.NewFolder(), Ct));
    }

    // ───────── SNB ─────────

    /// <summary>
    /// An SNB container without files whose text stream is <paramref name="blocks"/> bzip2 blocks of zeros and then
    /// one damaged block.
    /// </summary>
    private static byte[] SnbOfZeros(int blocks)
    {
        static byte[] Zlib(byte[] data)
        {
            using var output = new MemoryStream();
            using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(data);
            return output.ToArray();
        }
        static byte[] Be32(int value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            return bytes;
        }
        var table = Zlib([]);
        var zeros = Bzip2(new byte[0x8000]);
        using var file = new MemoryStream();
        var header = new byte[44];
        "SNBP000B"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(28), table.Length);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(36), (blocks + 1) * 0x8000);
        file.Write(header);
        file.Write(table);
        var offsets = new MemoryStream();
        for (var i = 0; i < blocks; i++)
        {
            offsets.Write(Be32((int)file.Length));
            file.Write(zeros);
        }
        offsets.Write(Be32((int)file.Length));
        file.Write("BZh9 not a bzip2 block"u8);
        var tail = Zlib(offsets.ToArray());
        var tailOffset = (int)file.Length;
        file.Write(tail);
        file.Write(Be32(tail.Length));
        file.Write(Be32(tailOffset));
        file.Write("SNBP000B"u8);
        return file.ToArray();
    }

    [Fact]
    public void Snb_text_past_the_ratio_is_refused_before_the_last_block()
    {
        var snb = SnbOfZeros(100);
        // With the ratio checked only at the end, the damaged last block would fail first.
        var limits = DocumentLimits.Default with { InflateRatioFloor = 256 * 1024, MaxInflateRatio = 10 };
        TooComplex(() => SnbReader.Unpack(snb, limits, Ct));
        Assert.Throws<InvalidDataException>(() => SnbReader.Unpack(snb, DocumentLimits.Default, Ct));
    }

    // ───────── MOBI HUFF/CDIC ─────────

    /// <summary>A HUFF record in which every byte is one 8-bit code: byte b selects phrase 255 - b.</summary>
    private static byte[] ByteHuff()
    {
        var huff = new byte[24 + 256 * 4 + 32 * 8];
        "HUFF"u8.CopyTo(huff);
        BinaryPrimitives.WriteUInt32BigEndian(huff.AsSpan(4), 24);
        BinaryPrimitives.WriteUInt32BigEndian(huff.AsSpan(8), 24);
        BinaryPrimitives.WriteUInt32BigEndian(huff.AsSpan(12), 24 + 256 * 4);
        for (var i = 0; i < 256; i++)
            BinaryPrimitives.WriteUInt32BigEndian(huff.AsSpan(24 + i * 4), 255u << 8 | 0x80 | 8); // max code 255, terminal, 8 bits
        return huff;
    }

    /// <summary>A CDIC record of <paramref name="phrases"/>; an unpacked phrase is stored as it is.</summary>
    private static byte[] Cdic(IReadOnlyList<(byte[] Data, bool Unpacked)> phrases)
    {
        using var output = new MemoryStream();
        output.Write("CDIC"u8);
        output.Write([0, 0, 0, 16]);
        var number = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, phrases.Count);
        output.Write(number);
        BinaryPrimitives.WriteInt32BigEndian(number, 8);
        output.Write(number);
        var offset = phrases.Count * 2;
        foreach (var (data, _) in phrases)
        {
            output.Write([(byte)(offset >> 8), (byte)offset]);
            offset += 2 + data.Length;
        }
        foreach (var (data, unpacked) in phrases)
        {
            var header = data.Length | (unpacked ? 0x8000 : 0);
            output.Write([(byte)(header >> 8), (byte)header]);
            output.Write(data);
        }
        return output.ToArray();
    }

    [Fact]
    public void Huff_cdic_phrases_made_of_phrases_stop_at_the_room_left_for_the_text()
    {
        // Phrase 0 is 1,000 bytes; phrase k is phrase k-1 a thousand times: phrase 3 would be 10^12 bytes.
        var phrases = new List<(byte[], bool)> { (Enumerable.Repeat((byte)'a', 1000).ToArray(), true) };
        for (var k = 1; k <= 3; k++)
            phrases.Add((Enumerable.Repeat((byte)(255 - (k - 1)), 1000).ToArray(), false));
        var huff = new HuffCdic(ByteHuff(), [Cdic(phrases)]);

        Assert.Equal(1000, huff.Decompress([255], 1 << 20).Length);
        Assert.Equal(1_000_000, huff.Decompress([254], 1 << 20).Length);
        TooComplex(() => huff.Decompress([252], 1 << 20));
        TooComplex(() => huff.Decompress([254, 254], 1 << 20)); // two phrases of 1 MB in a record with room for one
    }

    [Fact]
    public void Mobi_text_far_beyond_the_size_of_the_file_is_refused()
    {
        MobiReader.CheckTextRatio(DocumentLimits.Default.InflateRatioFloor, 1); // at the floor, any ratio is fine
        MobiReader.CheckTextRatio(50L << 20, 1 << 20);
        TooComplex(() => MobiReader.CheckTextRatio(50L << 20, 100_000));
    }
}
