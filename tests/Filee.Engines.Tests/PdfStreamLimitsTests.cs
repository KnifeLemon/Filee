// What PDFsharp unpacks from a PDF's streams, bounded (Pdf/PdfStreamLimits): deflate bombs built in code (a fixed
// Huffman block of "repeat the last 258 bytes", so a few megabytes of it stand for gigabytes, deflated once more into
// a small file) in an object stream, the cross-reference stream PDFsharp unpacks before it looks at it, and page
// content; an LZW bomb; many content streams that only pass the limit together; a predictor row of hundreds of
// megabytes for a few bytes. Each must be refused with error.document_too_complex in well under a second or two, while
// Filee's decoders unpack ordinary data exactly as PDFsharp's own do.

using System.Globalization;
using System.IO.Compression;
using System.Text;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Pdf;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Filters;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Tests;

public class PdfStreamLimitsTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const long Megabyte = 1024 * 1024;

    // ───────────────────────── Builders ─────────────────────────

    /// <summary>Raw deflate data for <paramref name="bytes"/> zero bytes (rounded up to 258), 13 bits per 258 bytes.</summary>
    internal static byte[] DeflatedZeros(long bytes)
    {
        var output = new MemoryStream();
        int buffer = 0, count = 0;
        void Bits(int value, int length) // LSB first
        {
            buffer |= value << count;
            count += length;
            while (count >= 8)
            {
                output.WriteByte((byte)buffer);
                buffer >>= 8;
                count -= 8;
            }
        }
        void Code(int code, int length) // Huffman codes go most significant bit first
        {
            for (var i = length - 1; i >= 0; i--)
                Bits((code >> i) & 1, 1);
        }
        Bits(1, 1); // last block
        Bits(1, 2); // fixed Huffman codes
        Code(0x30, 8); // literal 0
        for (long done = 1; done < bytes; done += 258)
        {
            Code(0xC5, 8); // length 258
            Code(0, 5); // distance 1
        }
        Code(0, 7); // end of block
        if (count > 0)
            output.WriteByte((byte)buffer);
        return output.ToArray();
    }

    /// <summary>zlib data as PDF streams hold it.</summary>
    internal static byte[] Zlib(byte[] data)
    {
        var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(data);
        return output.ToArray();
    }

    /// <summary>A zlib stream of <paramref name="bytes"/> zeros, deflated a second time: [/FlateDecode /FlateDecode].</summary>
    private static byte[] TwiceDeflatedZeros(long bytes) => Zlib([0x78, 0x9C, .. DeflatedZeros(bytes)]);

    private static byte[] Latin1(string text) => Encoding.Latin1.GetBytes(text);

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A PDF 1.5 file found through a cross-reference stream: page 3, object 4 in object stream 5 (whose data and
    /// dictionary entries are given), and content streams 6… for the page. <paramref name="xrefData"/> replaces the
    /// cross-reference stream's rows (with <paramref name="xrefEntries"/> for its dictionary), to make it the bomb.
    /// </summary>
    internal static byte[] Pdf(byte[]? objectStream = null, string objectStreamEntries = "", IReadOnlyList<(byte[] Data, string Entries)>? contents = null,
        byte[]? xrefData = null, string xrefEntries = "")
    {
        contents ??= [];
        if (objectStream is null)
        {
            objectStream = Zlib(Latin1("4 0 <</A 1>>"));
            objectStreamEntries = "/Filter/FlateDecode";
        }
        var output = new MemoryStream();
        void Write(string text) => output.Write(Latin1(text));
        var count = 6 + contents.Count;
        var offsets = new long[count + 1];
        Write("%PDF-1.5\n");
        offsets[1] = output.Position;
        Write("1 0 obj\n<</Type/Catalog/Pages 2 0 R>>\nendobj\n");
        offsets[2] = output.Position;
        Write("2 0 obj\n<</Type/Pages/Kids[3 0 R]/Count 1>>\nendobj\n");
        offsets[3] = output.Position;
        var refs = string.Join(" ", Enumerable.Range(0, contents.Count).Select(i => $"{7 + i} 0 R"));
        Write($"3 0 obj\n<</Type/Page/Parent 2 0 R/MediaBox[0 0 100 100]/X 4 0 R{(contents.Count > 0 ? $"/Contents[{refs}]" : "")}>>\nendobj\n");
        offsets[5] = output.Position;
        Write($"5 0 obj\n<</Type/ObjStm/N 1/First 4{objectStreamEntries}/Length {N(objectStream.Length)}>>\nstream\n");
        output.Write(objectStream);
        Write("\nendstream\nendobj\n");
        for (var i = 0; i < contents.Count; i++)
        {
            offsets[7 + i] = output.Position;
            Write($"{7 + i} 0 obj\n<<{contents[i].Entries}/Length {N(contents[i].Data.Length)}>>\nstream\n");
            output.Write(contents[i].Data);
            Write("\nendstream\nendobj\n");
        }

        offsets[6] = output.Position;
        var rows = new MemoryStream();
        void Row(int type, long field2, int field3)
        {
            rows.WriteByte((byte)type);
            rows.Write([(byte)(field2 >> 24), (byte)(field2 >> 16), (byte)(field2 >> 8), (byte)field2, (byte)(field3 >> 8), (byte)field3]);
        }
        Row(0, 0, 65535);
        for (var n = 1; n <= count; n++)
        {
            if (n == 4)
                Row(2, 5, 0);
            else
                Row(1, offsets[n], 0);
        }
        var table = xrefData ?? rows.ToArray();
        var entries = xrefData is null ? "/W[1 4 2]" : xrefEntries;
        Write($"6 0 obj\n<</Type/XRef/Size {count + 1}{entries}/Root 1 0 R/Length {N(table.Length)}>>\nstream\n");
        output.Write(table);
        Write($"\nendstream\nendobj\nstartxref\n{N(offsets[6])}\n%%EOF\n");
        return output.ToArray();
    }

    private string Save(byte[] pdf)
    {
        var path = Path.Combine(fx.NewFolder(), "streams.pdf");
        File.WriteAllBytes(path, pdf);
        return path;
    }

    private static ConversionRefusedException RefusedTooLarge(Action action, int seconds = 60) => Refused(action, seconds, DocumentLimits.TooLargeKey);

    private static ConversionRefusedException Refused(Action action, int seconds = 60, string key = DocumentLimits.ErrorKey)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var refusal = Assert.Throws<ConversionRefusedException>(action);
        Assert.Equal(key, refusal.ErrorKey);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(seconds), $"Took {watch.Elapsed}");
        return refusal;
    }

    /// <summary>LZW codes as PDFsharp reads them (9 to 12 bits, wider one code early, 256 clears the table).</summary>
    private static byte[] LzwCodes(IEnumerable<int> codes)
    {
        var output = new MemoryStream();
        long buffer = 0;
        int count = 0, width = 9, next = 258;
        var first = false; // PDFsharp adds a string after the very first code too
        void Put(int code)
        {
            buffer = (buffer << width | (uint)code) & 0xFFFFFF;
            count += width;
            while (count >= 8)
            {
                output.WriteByte((byte)(buffer >> (count - 8)));
                count -= 8;
            }
        }
        foreach (var code in codes)
        {
            Put(code);
            if (code == 256)
            {
                (width, next, first) = (9, 258, true);
                continue;
            }
            if (!first)
            {
                next++;
                width = next switch { 511 => 10, 1023 => 11, 2047 => 12, _ => width };
            }
            first = false;
        }
        Put(257); // end of data
        if (count > 0)
            output.WriteByte((byte)(buffer << (8 - count)));
        return output.ToArray();
    }

    /// <summary>Bytes as one LZW code each, the table cleared before it fills up (PDFsharp fails on a full table).</summary>
    private static IEnumerable<int> LiteralLzw(byte[] bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            if (i % 3_000 == 0)
                yield return 256;
            yield return bytes[i];
        }
    }

    /// <summary>
    /// Codes whose strings grow by a byte each (each code is the one about to be added), then the longest one again
    /// and again, <paramref name="tables"/> times over: PDFsharp writes some 22 MB per table for 12 kB of codes.
    /// </summary>
    private static IEnumerable<int> GrowingLzw(int tables)
    {
        for (var t = 0; t < tables; t++)
        {
            yield return 256;
            yield return 65;
            for (var code = 258; code < 4096; code++)
                yield return code;
            for (var i = 0; i < 4000; i++)
                yield return 4095;
        }
    }

    // ───────────────────────── Refused ─────────────────────────

    [Fact]
    public void PDFsharp_unpacks_through_Filee_s_decoders()
    {
        PdfStreamLimits.Install();
        Assert.True(PdfStreamLimits.IsInstalled);
    }

    [Fact]
    public void An_object_stream_that_unpacks_to_gigabytes_is_refused_when_the_file_is_opened()
    {
        var pdf = Pdf(TwiceDeflatedZeros(4L * 1024 * Megabyte), "/Filter[/FlateDecode/FlateDecode]");
        Assert.True(pdf.Length < 400_000, $"{pdf.Length} bytes");
        var path = Save(pdf);
        var ct = TestContext.Current.CancellationToken;
        var refusal = RefusedTooLarge(() => PdfSharpConverter.Open(path, PdfDocumentOpenMode.Import, ct).Dispose());
        Assert.Contains("256 MB", refusal.Message, StringComparison.Ordinal);
        // PDFsharp's own open, without the check before it.
        RefusedTooLarge(() => PdfStreamLimits.Open(() => PdfReader.Open(path, PdfDocumentOpenMode.Import), ct).Dispose());
        RefusedTooLarge(() => PdfObjectNesting.Open(pdf, PdfDocumentOpenMode.Modify, ct).Dispose());
    }

    [Fact]
    public void A_cross_reference_stream_that_unpacks_hugely_is_refused_before_PDFsharp_reads_its_dictionary()
    {
        // PDFsharp unpacks the stream "startxref" points at before it reads /W.
        var pdf = Pdf(xrefData: TwiceDeflatedZeros(1024 * Megabyte), xrefEntries: "/Filter[/FlateDecode/FlateDecode]");
        var path = Save(pdf);
        var ct = TestContext.Current.CancellationToken;
        RefusedTooLarge(() => PdfStreamLimits.Open(() => PdfReader.Open(path, PdfDocumentOpenMode.Import), ct).Dispose());
        RefusedTooLarge(() => PdfSharpConverter.Open(path, PdfDocumentOpenMode.Import, ct).Dispose());
    }

    [Fact]
    public void Page_content_that_unpacks_hugely_is_refused_when_the_file_is_opened_and_when_it_is_read()
    {
        var bomb = TwiceDeflatedZeros(1024 * Megabyte);
        var path = Save(Pdf(contents: [(bomb, "/Filter[/FlateDecode/FlateDecode]")]));
        var ct = TestContext.Current.CancellationToken;
        RefusedTooLarge(() => PdfSharpConverter.Open(path, PdfDocumentOpenMode.Modify, ct).Dispose());

        // Outside an open's budget each stream still has its limit (a page read later, UnfilteredValue).
        PdfStreamLimits.Install();
        using var document = new PdfDocument();
        var stream = new PdfDictionary(document);
        stream.CreateStream(bomb);
        stream.Elements["/Filter"] = new PdfArray(document, new PdfName("/FlateDecode"), new PdfName("/FlateDecode"));
        RefusedTooLarge(() => _ = stream.Stream.UnfilteredValue);
    }

    [Fact]
    public void Content_streams_that_pass_the_limit_only_together_are_refused()
    {
        var part = Zlib(new byte[900 * 1024]);
        var contents = Enumerable.Range(0, 6).Select(_ => (part, "/Filter/FlateDecode")).ToList();
        var pdf = Pdf(contents: contents);
        var ct = TestContext.Current.CancellationToken;
        var refusal = RefusedTooLarge(() => PdfStreamLimits.Open(() => PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import), ct,
            perStream: Megabyte, total: 4 * Megabyte).Dispose());
        Assert.Contains("together", refusal.Message, StringComparison.Ordinal);
        // Each alone, and all of them under Filee's limits, open.
        PdfStreamLimits.Open(() => PdfReader.Open(new MemoryStream(Pdf(contents: contents.Take(1).ToList())), PdfDocumentOpenMode.Import), ct,
            perStream: Megabyte, total: 4 * Megabyte).Dispose();
        using (var document = PdfObjectNesting.Open(pdf, PdfDocumentOpenMode.Import, ct))
            Assert.Equal(1, document.PageCount);
    }

    [Fact]
    public void An_LZW_bomb_is_refused_and_LZW_is_measured_as_PDFsharp_unpacks_it()
    {
        // One table: measured exactly as PDFsharp's decoder writes it.
        var one = LzwCodes(GrowingLzw(1));
        Assert.Equal(new LzwDecode().Decode(one, (FilterParms?)null).Length, PdfStreamLimits.LzwSize(one, long.MaxValue));
        foreach (var text in new[] { "", "A", "TOBEORNOTTOBEORTOBEORNOT", new string('x', 10_000) })
        {
            var codes = LzwCodes(LiteralLzw(Encoding.ASCII.GetBytes(text)));
            Assert.Equal(new LzwDecode().Decode(codes, (FilterParms?)null).Length, PdfStreamLimits.LzwSize(codes, long.MaxValue));
        }

        var bomb = LzwCodes(GrowingLzw(16)); // some 360 MB from 190 kB
        Assert.Null(PdfStreamLimits.LzwSize(bomb, 256 * Megabyte));
        var path = Save(Pdf(contents: [(bomb, "/Filter/LZWDecode")]));
        RefusedTooLarge(() => PdfSharpConverter.Open(path, PdfDocumentOpenMode.Import, TestContext.Current.CancellationToken).Dispose());
    }

    [Fact]
    public void A_predictor_row_longer_than_the_data_fails_without_allocating_it()
    {
        // PDFsharp allocates two rows of /Columns bytes before it finds no whole row in the data.
        PdfStreamLimits.Install();
        using var document = new PdfDocument();
        var parameters = new PdfDictionary(document);
        parameters.Elements.SetInteger("/Predictor", 12);
        parameters.Elements.SetInteger("/Columns", 200_000_000);
        var data = Zlib([2, 1, 2, 3]);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<ArgumentException>(() => Filtering.FlateDecode.Decode(data, new FilterParms(parameters)));
        Assert.Empty(Filtering.FlateDecode.Decode(Zlib([]), new FilterParms(parameters)));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < Megabyte);
        // PDFsharp fails on it too, and reads whole rows as before.
        parameters.Elements.SetInteger("/Columns", 100);
        Assert.Throws<ArgumentException>(() => new FlateDecode().Decode(data, new FilterParms(parameters)));
        parameters.Elements.SetInteger("/Columns", 3);
        Assert.Equal(new FlateDecode().Decode(data, new FilterParms(parameters)), Filtering.FlateDecode.Decode(data, new FilterParms(parameters)));
    }

    // ───────────────────────── Read ─────────────────────────

    [Fact]
    public void Filee_s_decoders_unpack_as_PDFsharp_s_own()
    {
        PdfStreamLimits.Install();
        var random = new Random(26);
        var text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 2_000).Select(i => $"{i} 0 0 {i % 7} re f\n")));
        var noise = new byte[100_000];
        random.NextBytes(noise);
        using var document = new PdfDocument();
        var up = new PdfDictionary(document);
        up.Elements.SetInteger("/Predictor", 12);
        up.Elements.SetInteger("/Columns", 4);
        up.Elements.SetInteger("/Colors", 1);
        // Rows of a PNG "up" predictor: a type byte and 4 bytes each.
        var rows = Enumerable.Range(0, 300).SelectMany(r => new byte[] { 2, (byte)r, 1, 0, 3 }).ToArray();
        foreach (var data in new[] { Zlib(text), Zlib(noise), Zlib([]), [0x78, 0x9C, .. DeflatedZeros(100_000)], [1], [] })
            Assert.Equal(new FlateDecode().Decode(data, (FilterParms?)null), Filtering.FlateDecode.Decode(data, (FilterParms?)null));
        Assert.Equal(new FlateDecode().Decode(Zlib(rows), new FilterParms(up)), Filtering.FlateDecode.Decode(Zlib(rows), new FilterParms(up)));
        var ascii85 = new Ascii85Decode().Encode(noise);
        Assert.Equal(new Ascii85Decode().Decode(ascii85, (FilterParms?)null), Filtering.Ascii85Decode.Decode(ascii85, (FilterParms?)null));
        var lzw = LzwCodes(LiteralLzw(text));
        Assert.Equal(new LzwDecode().Decode(lzw, (FilterParms?)null), Filtering.LzwDecode.Decode(lzw, (FilterParms?)null));
    }

    [Fact]
    public void Ordinary_files_open_with_their_streams_unpacked()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Zlib(Encoding.ASCII.GetBytes("0 0 1 rg 10 10 80 80 re f\n"));
        var pdf = Pdf(contents: [(content, "/Filter/FlateDecode"), (Encoding.ASCII.GetBytes("1 0 0 rg 0 0 5 5 re f\n"), "")]);
        using (var document = PdfObjectNesting.Open(pdf, PdfDocumentOpenMode.Import, ct))
            Assert.Equal(1, document.PageCount);
        using (var document = PdfSharpConverter.Open(Save(pdf), PdfDocumentOpenMode.Modify, ct))
            Assert.Contains("re f", Encoding.ASCII.GetString(document.Pages[0].Contents.CreateSingleContent().Stream.UnfilteredValue), StringComparison.Ordinal);
    }

    [Fact]
    public void A_cancelled_open_stops_while_it_unpacks()
    {
        var pdf = Pdf(contents: [(Zlib(new byte[4 * 1024 * 1024]), "/Filter/FlateDecode")]);
        Assert.ThrowsAny<OperationCanceledException>(() =>
            PdfStreamLimits.Open(() => PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import), new CancellationToken(canceled: true)).Dispose());
    }
}
