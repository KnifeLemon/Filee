// Mutation fuzzing of the DjVu reader (Djvu/): the seeds are a page with a JB2 mask, a palette and hidden text, a
// bundled document with a shared dictionary, a photo page (IW44 in two chunks), and a bundled document with bookmarks,
// hyperlinks and a G4 (Smmr) mask in stripes, all written in code (DjvuBuilder).
// Every input must be read or refused with InvalidDataException / ConversionRefusedException, in time and on a 1 MB
// stack; every page read is drawn and written as a PDF page. Run by FuzzHarness: a short fixed run normally,
// FILEE_FUZZ_SECONDS for a longer random one, FILEE_FUZZ_SEED to repeat, FILEE_FUZZ_OUT to keep failing inputs.

using Filee.Engines.Djvu;
using Filee.Engines.Ocr;
using static Filee.Engines.Tests.DjvuBuilder;

namespace Filee.Engines.Tests;

[Trait("Category", "Fuzz")]
public class DjvuFuzzTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>Inputs per seed in a normal test run.</summary>
    private const int QuickIterations = 60;

    private static readonly Jb2Shape Letter = Jb2Writer.Shape(".##.", "#..#", "####", "#..#");

    private static byte[] MaskPage()
    {
        var mask = new Jb2Writer().Start(64, 32).New(Letter, 2, 20).Copy(0, 8, 20).Refine(0, Jb2Writer.Shape("###.", "#..#", "####", "#..#"), 14, 20)
            .Copy(0, 2, 4).NonMark(Letter, 40, 10).End();
        var text = DjvuBuilder.Text("ab cd\n", new TextZone(1, 0, 0, 64, 32, 0, 6,
            new TextZone(5, 2, 20, 20, 4, 0, 6, new TextZone(6, 2, 20, 8, 4, 0, 3), new TextZone(6, 14, 20, 4, 4, 3, 3))));
        return DjvuFile(Page(64, 32, 150, Chunk("Sjbz", mask), Palette([(255, 0, 0), (0, 0, 255)], [0, 1, 0, 1, 0]), text));
    }

    private static byte[] BundledBook()
    {
        var dictionary = new Jb2Writer().Start(0, 0).New(Letter, type: 2);
        var shared = Form("DJVI", Chunk("Djbz", dictionary.End()));
        var page = Form("DJVU", Info(40, 20, 100), Include("shared"), Chunk("Sjbz", new Jb2Writer().RequireDictionary(1, dictionary.Library).Start(40, 20).Copy(0, 1, 1).Copy(0, 10, 1).End()));
        var y = Enumerable.Range(0, 8 * 4).Select(i => (sbyte)(i * 3 - 40)).ToArray();
        var background = Iw44Encoder.Encode(8, 4, y, new sbyte[32], new sbyte[32], slices: 40).Select(c => Chunk("BG44", c));
        var compound = Form("DJVU", [Info(40, 20, 100), Chunk("Sjbz", new Jb2Writer().Start(40, 20).New(Letter, 5, 5).End()), .. background]);
        return Bundle(("shared", shared, false), ("p1", page, true), ("p2", compound, true));
    }

    private static byte[] Photo()
    {
        var y = Enumerable.Range(0, 24 * 16).Select(i => (sbyte)((i % 24) * 8 - 90)).ToArray();
        var chunks = Iw44Encoder.Encode(24, 16, y, slices: 30, chunks: 2).Select(c => Chunk("BG44", c));
        return DjvuFile(Form("DJVU", [Info(24, 16, 75, flags: 5), .. chunks]));
    }

    private static byte[] LinkedBook()
    {
        var stride = 5;
        var bits = new byte[stride * 24];
        for (var i = 0; i < bits.Length; i++)
            bits[i] = (byte)(i * 37 % 7 == 0 ? 0xF0 : i % 3 == 0 ? 0x18 : 0);
        var smmr = Page(40, 24, 100, Smmr(bits, 40, 24, rowsPerStripe: 10), AnnotationChunk("(maparea \"#2\" \"x\" (rect 1 1 10 10)) (maparea (url \"http://example.com/\" \"\") \"\" (poly 1 1 9 9 20 1))"));
        var plain = Page(40, 24, 100, Chunk("Sjbz", new Jb2Writer().Start(40, 24).New(Letter, 5, 5).End()), AnnotationChunk("(maparea \"#-1\" \"\" (oval 2 2 8 8))", compressed: false));
        var outline = Outline(new Bookmark("One", "#1", new Bookmark("Two", "#p2")), new Bookmark("Web", "https://example.org/"));
        return Bundle([outline], ("p1", smmr, true), ("p2", plain, true));
    }

    [Fact]
    public void DjvuDocument_reads_or_refuses_every_mutated_file() =>
        new FuzzHarness(fx, QuickIterations, 20261120).Files("djvu", "djvu", [MaskPage(), BundledBook(), Photo(), LinkedBook()], FuzzHarness.Bytes, (path, _) =>
        {
            // Smaller limits than real books need keep each input quick.
            var limits = DjvuLimits.Default with { MaxPagePixels = 4_000_000, MaxIw44Pixels = 1_000_000, MaxShapePixels = 4_000_000 };
            using var document = DjvuDocument.Open(path, limits, TestContext.Current.CancellationToken);
            using var writer = new OcrPdfWriter();
            var pages = new List<PdfSharp.Pdf.PdfPage>();
            var links = new List<(int Page, IReadOnlyList<DjvuLink> Links, double Scale)>();
            for (var i = 0; i < Math.Min(document.Count, 4); i++)
            {
                var page = document.ReadPage(i, TestContext.Current.CancellationToken);
                DjvuRenderer.Compose(page, TestContext.Current.CancellationToken);
                pages.Add(DjvuPdfWriter.AddPage(writer, page, TestContext.Current.CancellationToken));
                links.Add((i, page.Links, DjvuPdfWriter.Scale(page)));
            }
            foreach (var (page, pageLinks, scale) in links)
                DjvuPdfWriter.AddLinks(writer.Document, pages[page], pageLinks, scale, pages);
            DjvuPdfWriter.AddOutline(writer.Document, pages, document.Bookmarks, url => document.PageOf(url), TestContext.Current.CancellationToken);
            if (writer.PageCount > 0)
                writer.Document.Save(new MemoryStream());
        });
}
