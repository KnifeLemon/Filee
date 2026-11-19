// PDFs nested too deeply for PDFsharp's object parser, built in code: arrays and dictionaries a million levels deep in an
// object, the trailer, an object stream (with and without /Type, deflated or not, its length given by reference), and
// in places a reading from front to back would miss (after a string that never closes, inside another stream's data,
// with the closing brackets inside strings). Each must be refused with error.document_too_complex before PDFsharp
// parses it, on the smallest stack a conversion runs on (a regression overflows that stack and ends the test run), and
// files nested as real ones are still open.

using System.IO.Compression;
using System.Text;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Pdf;
using Filee.Engines.Vector;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Tests;

public class PdfObjectNestingTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const int Deep = 1_000_000;

    // ───────────────────────── Helpers ─────────────────────────

    private static string Nest(int levels) => new string('[', levels) + new string(']', levels);

    private static byte[] Latin1(string text) => Encoding.Latin1.GetBytes(text);

    /// <summary>
    /// A one-page PDF with <paramref name="objects"/> after the catalog, page tree and page (objects 1–3, the page's
    /// dictionary ending in <paramref name="page"/>); the cross-reference table points at the first "N 0 obj" of each
    /// of the <paramref name="count"/> objects.
    /// </summary>
    internal static byte[] Pdf(string objects, int count = 4, string trailer = "", string page = "")
    {
        var text = "%PDF-1.4\n1 0 obj\n<</Type/Catalog/Pages 2 0 R>>\nendobj\n2 0 obj\n<</Type/Pages/Kids[3 0 R]/Count 1>>\nendobj\n"
            + $"3 0 obj\n<</Type/Page/Parent 2 0 R/MediaBox[0 0 100 100]{page}>>\nendobj\n" + objects;
        var xref = new StringBuilder($"xref\n0 {count + 1}\n0000000000 65535 f \n");
        for (var n = 1; n <= count; n++)
            xref.Append($"{text.IndexOf($"\n{n} 0 obj", StringComparison.Ordinal) + 1:D10} 00000 n \n");
        return Latin1(text + xref + $"trailer\n<</Size {count + 1}/Root 1 0 R{trailer}>>\nstartxref\n{text.Length}\n%%EOF\n");
    }

    /// <summary>
    /// A PDF 1.5 file whose object 4 (<paramref name="value"/>) lives in object stream 5, found through a
    /// cross-reference stream (object 6). Object 7 holds the stream's /Length when <paramref name="indirect"/> (PDFsharp
    /// takes /N and /First only as they are).
    /// </summary>
    internal static byte[] ObjectStream(string value, bool deflate = true, bool typed = true, bool indirect = false)
    {
        var output = new MemoryStream();
        void Write(string text) => output.Write(Latin1(text));
        var offsets = new long[9];
        Write("%PDF-1.5\n");
        offsets[1] = output.Position;
        Write("1 0 obj\n<</Type/Catalog/Pages 2 0 R>>\nendobj\n");
        offsets[2] = output.Position;
        Write("2 0 obj\n<</Type/Pages/Kids[3 0 R]/Count 1>>\nendobj\n");
        offsets[3] = output.Position;
        Write("3 0 obj\n<</Type/Page/Parent 2 0 R/MediaBox[0 0 100 100]/X 4 0 R>>\nendobj\n");

        const string header = "4 0 ";
        var data = Latin1(header + value);
        if (deflate)
        {
            var packed = new MemoryStream();
            using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(data);
            data = packed.ToArray();
        }
        offsets[5] = output.Position;
        var length = indirect ? "7 0 R" : data.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Write($"5 0 obj\n<<{(typed ? "/Type/ObjStm" : "")}/N 1/First {header.Length}{(deflate ? "/Filter/FlateDecode" : "")}/Length {length}>>\nstream\n");
        output.Write(data);
        Write("\nendstream\nendobj\n");
        offsets[7] = output.Position;
        Write($"7 0 obj\n{data.Length}\nendobj\n");
        offsets[8] = output.Position;
        Write("8 0 obj\nnull\nendobj\n");

        offsets[6] = output.Position;
        var rows = new MemoryStream();
        void Row(int type, long field2, int field3)
        {
            rows.WriteByte((byte)type);
            rows.Write([(byte)(field2 >> 24), (byte)(field2 >> 16), (byte)(field2 >> 8), (byte)field2, (byte)(field3 >> 8), (byte)field3]);
        }
        Row(0, 0, 65535);
        for (var n = 1; n <= 8; n++)
        {
            if (n == 4)
                Row(2, 5, 0);
            else
                Row(1, offsets[n], 0);
        }
        var table = rows.ToArray();
        Write($"6 0 obj\n<</Type/XRef/Size 9/W[1 4 2]/Root 1 0 R/Length {table.Length}>>\nstream\n");
        output.Write(table);
        Write($"\nendstream\nendobj\nstartxref\n{offsets[6]}\n%%EOF\n");
        return output.ToArray();
    }

    /// <summary>
    /// Runs <paramref name="action"/> on a thread with a 1 MB stack (the smallest a conversion gets on Windows), or
    /// <paramref name="stack"/> bytes, and returns what it threw; fails when it takes longer than <paramref name="seconds"/>.
    /// </summary>
    private static Exception? OnSmallStack(Action action, int seconds = 60, int stack = 1024 * 1024)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }, stack);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(seconds)), $"Still running after {seconds} s");
        return error;
    }

    private static ConversionRefusedException Refused(Action action)
    {
        var refusal = Assert.IsType<ConversionRefusedException>(OnSmallStack(action));
        Assert.Equal(DocumentLimits.ErrorKey, refusal.ErrorKey);
        return refusal;
    }

    private string Save(byte[] pdf)
    {
        var path = Path.Combine(fx.NewFolder(), "nested.pdf");
        File.WriteAllBytes(path, pdf);
        return path;
    }

    public static readonly TheoryData<string> Hiding =
    [
        "object", "dictionary", "trailer", "after an unclosed string", "inside stream data", "closed in strings",
        "object stream", "plain object stream", "untyped object stream of indirect length",
    ];

    private static byte[] Hostile(string where) => where switch
    {
        "object" => Pdf($"4 0 obj\n{Nest(Deep)}\nendobj\n"),
        "dictionary" => Pdf($"4 0 obj\n{string.Concat(Enumerable.Repeat("<</A ", Deep / 10))}1{string.Concat(Enumerable.Repeat(">>", Deep / 10))}\nendobj\n"),
        "trailer" => Pdf("", count: 3, trailer: "/X " + Nest(Deep)),
        // Read from front to back, the "(" makes everything after it a string; PDFsharp jumps to object 4 all the same.
        "after an unclosed string" => Pdf($"% (\n(\n4 0 obj\n{Nest(Deep)}\nendobj\n"),
        // Object 5 is the data of stream 4, which a reading from front to back skips.
        "inside stream data" => Pdf($"4 0 obj\n<</Length {Nest(Deep).Length + 16}>>\nstream\n\n5 0 obj\n{Nest(Deep)}\nendobj\nendstream\nendobj\n", count: 5),
        // Every "]" is inside a string, so PDFsharp nests a level per "[(])".
        "closed in strings" => Pdf($"4 0 obj\n{string.Concat(Enumerable.Repeat("[(])", Deep / 4))}\nendobj\n"),
        "object stream" => ObjectStream(Nest(Deep)),
        "plain object stream" => ObjectStream(Nest(Deep), deflate: false),
        "untyped object stream of indirect length" => ObjectStream(Nest(Deep), typed: false, indirect: true),
        _ => throw new ArgumentOutOfRangeException(nameof(where)),
    };

    // ───────────────────────── Refused ─────────────────────────

    [Theory]
    [MemberData(nameof(Hiding))]
    public void Nesting_too_deep_for_PDFsharp_is_refused_before_it_parses_the_file(string where)
    {
        var path = Save(Hostile(where));
        var refusal = Refused(() => PdfSharpConverter.Open(path, PdfDocumentOpenMode.Import, TestContext.Current.CancellationToken).Dispose());
        Assert.Contains("nests", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_route_that_opens_a_PDF_with_PDFsharp_refuses_it_or_leaves_it_to_another_engine()
    {
        var path = Save(Hostile("object"));
        var ct = TestContext.Current.CancellationToken;
        Refused(() => PdfSharpConverter.OpenForChanges(path, ct).Dispose());
        Refused(() => PdfBlankPages.Remove(path));
        Refused(() => PdfObjectNesting.Open(File.ReadAllBytes(path), PdfDocumentOpenMode.Import, ct).Dispose());
        // PDF/A: the PDF goes to Ghostscript as it is.
        Assert.Null(OnSmallStack(() =>
        {
            Assert.Equal(new PdfAPreparation(null, null), PdfPrintableAnnotations.Prepare(path, fx.NewFolder(), ct));
            Assert.False(GhostscriptConverter.DeclaresPdfA(path));
        }));
    }

    [Fact]
    public void Nesting_just_past_the_limit_is_refused_and_at_the_limit_read()
    {
        var ct = TestContext.Current.CancellationToken;
        PdfObjectNesting.Check(Pdf($"4 0 obj\n{Nest(PdfObjectNesting.MaxDepth)}\nendobj\n"), ct);
        PdfObjectNesting.Check(ObjectStream(Nest(PdfObjectNesting.MaxDepth)), ct);
        Assert.Throws<ConversionRefusedException>(() => PdfObjectNesting.Check(Pdf($"4 0 obj\n{Nest(PdfObjectNesting.MaxDepth + 1)}\nendobj\n"), ct));
        Assert.Throws<ConversionRefusedException>(() => PdfObjectNesting.Check(ObjectStream(Nest(PdfObjectNesting.MaxDepth + 1)), ct));
        // About 1,900 levels already overflowed a 1 MB stack in PDFsharp.
        var path = Save(Pdf($"4 0 obj\n{Nest(2_000)}\nendobj\n"));
        Refused(() => PdfSharpConverter.Open(path, PdfDocumentOpenMode.Import, ct).Dispose());
    }

    [Fact]
    public void The_deepest_nesting_allowed_is_read_saved_and_copied_on_a_quarter_megabyte_stack()
    {
        // Secondary threads can get 512 KB on macOS: half of it leaves the conversion room for its own frames. PDFsharp
        // takes about 530 bytes per level (it overflowed a 1 MB stack near 1,900), so 256 levels need some 140 KB.
        var deep = Nest(PdfObjectNesting.MaxDepth);
        var dictionaries = string.Concat(Enumerable.Repeat("<</A ", PdfObjectNesting.MaxDepth)) + "1"
            + string.Concat(Enumerable.Repeat(">>", PdfObjectNesting.MaxDepth));
        var content = "q " + Nest(PdfContentNesting.MaxDepth) + " 0 d Q";
        byte[][] files =
        [
            Pdf($"4 0 obj\n{deep}\nendobj\n", page: "/X 4 0 R"),
            Pdf($"4 0 obj\n{dictionaries}\nendobj\n", page: "/X 4 0 R"),
            Pdf("", count: 3, trailer: "/X " + Nest(PdfObjectNesting.MaxDepth - 1)), // the trailer is a level itself
            ObjectStream(deep),
            Pdf($"4 0 obj\n<</Length {content.Length}>>\nstream\n{content}\nendstream\nendobj\n", page: "/Contents 4 0 R"),
        ];
        var paths = files.Select(Save).ToList();
        var ct = TestContext.Current.CancellationToken;
        Assert.Null(OnSmallStack(() =>
        {
            foreach (var (pdf, path) in files.Zip(paths))
            {
                using (var document = PdfObjectNesting.Open(pdf, PdfDocumentOpenMode.Modify, ct))
                    document.Save(new MemoryStream());
                using (var source = PdfSharpConverter.Open(path, PdfDocumentOpenMode.Import, ct))
                using (var copy = new PdfDocument())
                {
                    copy.AddPage(source.Pages[0]);
                    copy.Save(new MemoryStream());
                }
                // Content streams go through PDFsharp's ContentReader there.
                _ = PdfBlankPages.Remove(path);
            }
        }, stack: 256 * 1024));
    }

    [Fact]
    public void Objects_read_over_and_over_are_refused_quickly()
    {
        // 250 objects open inside each other, each read to the end of the file: hundreds of times its size.
        var text = string.Concat(Enumerable.Range(4, 250).Select(n => $"\n{n} 0 obj ["))
            + string.Concat(Enumerable.Repeat("1 ", 1_000_000)) + new string(']', 250) + "\nendobj\n";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var refusal = Assert.Throws<ConversionRefusedException>(() => PdfObjectNesting.Check(Pdf(text, count: 253), TestContext.Current.CancellationToken));
        Assert.Contains("overlap", refusal.Message, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Took {watch.Elapsed}");
    }

    [Fact]
    public void A_file_is_read_through_its_window_as_memory_is()
    {
        // The "obj" keyword of the deep object crosses the end of the first 1 MB window.
        var head = Pdf("");
        var prefix = "%PDF-1.4\n1 0 obj\n<</Type/Catalog/Pages 2 0 R>>\nendobj\n2 0 obj\n<</Type/Pages/Kids[3 0 R]/Count 1>>\nendobj\n"
            + "3 0 obj\n<</Type/Page/Parent 2 0 R/MediaBox[0 0 100 100]>>\nendobj\n";
        var padding = (1 << 20) - 2 - prefix.Length - "%\n4 0 ".Length;
        var objects = "%" + new string(' ', padding) + "\n4 0 obj\n" + Nest(10_000) + "\nendobj\n";
        var deep = Pdf(objects);
        Assert.Equal((1 << 20) - 2, Encoding.Latin1.GetString(deep).IndexOf("\n4 0 obj", StringComparison.Ordinal) + 5);
        var ct = TestContext.Current.CancellationToken;
        Assert.Throws<ConversionRefusedException>(() => PdfObjectNesting.Check(Save(deep), ct));
        Assert.Throws<ConversionRefusedException>(() => PdfObjectNesting.Check(deep, ct));
        PdfObjectNesting.Check(Save(Pdf(objects.Replace(Nest(10_000), Nest(10), StringComparison.Ordinal))), ct);
        PdfObjectNesting.Check(Save(head), ct);
    }

    [Fact]
    public void A_cancelled_check_stops()
    {
        var pdf = Pdf($"4 0 obj\n{Nest(10)}\nendobj\n");
        Assert.ThrowsAny<OperationCanceledException>(() => PdfObjectNesting.Check(pdf, new CancellationToken(canceled: true)));
    }

    // ───────────────────────── Read ─────────────────────────

    [Fact]
    public void Real_nesting_opens_from_objects_and_object_streams()
    {
        var ct = TestContext.Current.CancellationToken;
        using (var document = PdfObjectNesting.Open(Pdf($"4 0 obj\n<</A [(a (b) [) [1 2 <</B [[3]]>>]] %]]]\n/C <5B5B>>>\nendobj\n"), PdfDocumentOpenMode.Import, ct))
            Assert.Equal(1, document.PageCount);
        using (var document = PdfObjectNesting.Open(ObjectStream("<</A [[1] (]]) [2]]>>", typed: false, indirect: true), PdfDocumentOpenMode.Import, ct))
        {
            var value = Assert.IsType<PdfDictionary>(((PdfSharp.Pdf.Advanced.PdfReference)document.Pages[0].Elements["/X"]!).Value);
            Assert.Equal(3, value.Elements.GetArray("/A")!.Elements.Count);
        }
    }

    [Fact]
    public void Files_PDFsharp_saves_and_Filee_packs_into_object_streams_are_checked_object_by_object()
    {
        static byte[] Saved(int levels)
        {
            using var document = new PdfDocument();
            var page = document.AddPage();
            var outer = new PdfArray(document);
            var inner = outer;
            for (var i = 1; i < levels; i++)
            {
                var next = new PdfArray(document);
                inner.Elements.Add(next);
                inner = next;
            }
            document.Internals.AddObject(outer);
            page.Elements["/X"] = outer.Reference;
            var output = new MemoryStream();
            document.Save(output);
            return output.ToArray();
        }

        var ct = TestContext.Current.CancellationToken;
        var shallow = Saved(20);
        var packed = PdfObjectStreams.Pack(shallow);
        Assert.NotNull(packed);
        PdfObjectNesting.Check(shallow, ct);
        PdfObjectNesting.Check(packed, ct);
        using (var document = PdfObjectNesting.Open(packed, PdfDocumentOpenMode.Import, ct))
            Assert.Equal(1, document.PageCount);

        var deep = PdfObjectStreams.Pack(Saved(PdfObjectNesting.MaxDepth + 10));
        Assert.NotNull(deep);
        Assert.DoesNotContain("[[[", Encoding.Latin1.GetString(deep), StringComparison.Ordinal); // only in the deflated stream
        Assert.Throws<ConversionRefusedException>(() => PdfObjectNesting.Check(deep, ct));
    }

    [Fact]
    public void Every_PDFsharp_open_of_a_file_Filee_did_not_write_goes_through_the_check()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Filee.Engines")))
            dir = dir.Parent;
        if (dir is null)
            return; // run outside the repository
        // The checked opens, and PDFs Filee or Tesseract wrote itself.
        string[] allowed =
        [
            "Pdf/PdfObjectNesting.cs", // the checked open
            "Pdf/PdfSharpConverter.cs", // OpenTolerant checks the file first
            "Pdf/PdfSheets.cs", // XPdfForm reads the file PdfSharpConverter.Open checked, or its mended bytes
            "Rendering/PdfPageWriter.cs", // the PDF the page writer just wrote
            "Ocr/TesseractConverter.cs", // the pages and text layer Tesseract wrote
        ];
        var src = Path.Combine(dir.FullName, "src");
        var unchecked_ = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file) is var text
                && (text.Contains("PdfReader.Open(", StringComparison.Ordinal) || text.Contains("XPdfForm.From", StringComparison.Ordinal)))
            .Select(file => Path.GetRelativePath(Path.Combine(src, "Filee.Engines"), file).Replace('\\', '/'))
            .Where(file => !allowed.Contains(file))
            .ToList();
        Assert.Empty(unchecked_);
    }
}
