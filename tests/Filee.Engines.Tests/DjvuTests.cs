// Filee's own DjVu reader (Djvu/) and its converter: every piece checked against an encoder written in code
// (DjvuBuilder) — the ZP-coder and BZZ round trips, JB2 masks (direct shapes, copies, refinements, non-mark data, a
// shared dictionary, comments and a reset) against the shapes drawn by the test, IW44 pictures against their source
// pixels, hidden text as PDF words, the CCITT G4 codec (its code tables, round trips, Smmr masks), bookmarks and
// hyperlinks — then whole documents (one page, bundled and indirect) to PDF, PNG and TIFF, the PDF's JBIG2, G4 and
// Flate masks drawn back by PDFtoImage, its links and outline, and hostile files refused quickly through the limits.

using System.Diagnostics;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Djvu;
using Filee.Engines.Documents;
using Filee.Engines.Ocr;
using ImageMagick;
using SkiaSharp;
using UglyToad.PdfPig;
using static Filee.Engines.Tests.DjvuBuilder;

#pragma warning disable CA1416 // PDFtoImage draws the PDF back on every platform the tests run on

namespace Filee.Engines.Tests;

public sealed class DjvuTests(EngineFixture fx) : IClassFixture<EngineFixture>, IDisposable
{
    private readonly string _folder = fx.NewFolder();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Save(byte[] file, string name = "test.djvu")
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, file);
        return path;
    }

    private async Task<IReadOnlyList<string>> Convert(string source, string target, Preset? preset = null, Action<LeftOutNotice>? leftOut = null)
    {
        var output = Path.Combine(_folder, "out-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(output);
        return await new DjvuConverter().ConvertAsync(
            new ConversionStep(source, "djvu", target, preset ?? new Preset(), new FolderOutput(output), _folder, LeftOut: leftOut), null, Token);
    }

    // ---- shapes and masks used by several tests ----

    private static readonly Jb2Shape Letter = Jb2Writer.Shape(
        "..####..",
        ".##..##.",
        "##....##",
        "##....##",
        "########",
        "##....##",
        "##....##",
        "##....##");

    private static readonly Jb2Shape Bar = Jb2Writer.Shape(
        "##########",
        "##########",
        "..........",
        "##########");

    /// <summary>Letter with its top-left pixel set: refines Letter.</summary>
    private static readonly Jb2Shape LetterMark = Jb2Writer.Shape(
        "#.####..",
        ".##..##.",
        "##....##",
        "##....##",
        "########",
        "##....##",
        "##....##",
        "##.#..##");

    /// <summary>The page drawn from (shape, left, bottom) placements, top row first.</summary>
    private static bool[,] Expected(int width, int height, params (Jb2Shape Shape, int Left, int Bottom)[] blits)
    {
        var page = new bool[height, width];
        foreach (var (shape, left, bottom) in blits)
        {
            for (var y = 0; y < shape.Height; y++)
            {
                for (var x = 0; x < shape.Width; x++)
                {
                    var px = left + x;
                    var py = height - 1 - (bottom + y);
                    if (shape[x, y] != 0 && px >= 0 && px < width && py >= 0 && py < height)
                        page[py, px] = true;
                }
            }
        }
        return page;
    }

    private static bool[,] Mask(DjvuPage page)
    {
        var bits = DjvuRenderer.MaskBits(page, cancellationToken: Token);
        var stride = (page.Width + 7) / 8;
        var result = new bool[page.Height, page.Width];
        for (var y = 0; y < page.Height; y++)
        {
            for (var x = 0; x < page.Width; x++)
                result[y, x] = (bits[y * stride + (x >> 3)] & (0x80 >> (x & 7))) != 0;
        }
        return result;
    }

    /// <summary>A 120 × 60 mask: letters on two lines, a copy, a refinement, an image-only shape, non-mark data.</summary>
    private static (byte[] Data, bool[,] Expected) TextMask()
    {
        var writer = new Jb2Writer().Start(120, 60)
            .New(Letter, 5, 40)
            .New(Bar, 20, 44)
            .Copy(0, 35, 40)
            .Comment("Filee test")
            .Refine(0, LetterMark, 50, 39)
            .New(Bar, 70, 45, type: 3)
            .Reset()
            .Copy(0, 5, 10) // a new line
            .Copy(1, 20, 12)
            .NonMark(Bar, 100, 30);
        var expected = Expected(120, 60,
            (Letter, 5, 40), (Bar, 20, 44), (Letter, 35, 40), (LetterMark, 50, 39), (Bar, 70, 45),
            (Letter, 5, 10), (Bar, 20, 12), (Bar, 100, 27));
        return (writer.End(), expected);
    }

    private static byte[] BilevelPage(int dpi = 100, int flags = 1, params byte[][] extra)
    {
        var (mask, _) = TextMask();
        return Form("DJVU", [Info(120, 60, dpi, flags), Chunk("Sjbz", mask), .. extra]);
    }

    /// <summary>Hidden text: one line "Hello DjVu" with two words over the first letters, and a line of Cyrillic.</summary>
    private static byte[] HelloText()
    {
        const string text = "Hello DjVu\nПривет\n";
        var hello = new TextZone(6, 5, 38, 25, 12, 0, 6);
        var djvu = new TextZone(6, 35, 38, 30, 12, 6, 5);
        var line1 = new TextZone(5, 5, 38, 60, 12, 0, 11, hello, djvu);
        var line2 = new TextZone(5, 5, 8, 50, 12, 11, System.Text.Encoding.UTF8.GetByteCount("Привет\n"));
        var paragraph = new TextZone(4, 5, 8, 60, 42, 0, System.Text.Encoding.UTF8.GetByteCount(text), line1, line2);
        var page = new TextZone(1, 0, 0, 120, 60, 0, System.Text.Encoding.UTF8.GetByteCount(text), paragraph);
        return DjvuBuilder.Text(text, page);
    }

    // ---- the pieces ----

    [Fact]
    public void Zp_coder_round_trips_adaptive_raw_and_iw44_bits()
    {
        var random = new Random(346);
        var steps = Enumerable.Range(0, 20_000).Select(_ => (Kind: random.Next(3), Context: random.Next(8), Bit: random.Next(10) < 8 ? 0 : 1)).ToList();
        var encoder = new ZpEncoder();
        var contexts = new byte[8];
        foreach (var (kind, context, bit) in steps)
        {
            if (kind == 0)
                encoder.Encode(bit, ref contexts[context]);
            else if (kind == 1)
                encoder.EncodeRaw(bit);
            else
                encoder.EncodeIw(bit);
        }
        var data = encoder.Finish();
        var decoder = new ZpDecoder(data);
        Array.Clear(contexts);
        var decoded = steps.Select(s => s.Kind switch
        {
            0 => decoder.Decode(ref contexts[s.Context]),
            1 => decoder.DecodeRaw(),
            _ => decoder.DecodeIw(),
        }).ToList();
        Assert.Equal(steps.Select(s => s.Bit), decoded);
        Assert.True(data.Length < 20_000 / 8, "biased bits compress");
    }

    [Theory]
    [InlineData(0, 1 << 20)]
    [InlineData(1, 1 << 20)]
    [InlineData(5_000, 1 << 20)]
    [InlineData(5_000, 1_000)] // several blocks: the contexts carry on
    public void Bzz_round_trips(int length, int blockSize)
    {
        var random = new Random(length);
        var words = new[] { "Filee ", "DjVu ", "scanned ", "book ", "책 ", "книга " };
        var text = new System.Text.StringBuilder();
        while (text.Length < length)
            text.Append(words[random.Next(words.Length)]);
        var data = System.Text.Encoding.UTF8.GetBytes(text.ToString())[..Math.Min(length, System.Text.Encoding.UTF8.GetByteCount(text.ToString()))];
        var packed = BzzEncoder.Encode(data, blockSize);
        Assert.Equal(data, BzzDecoder.Decode(packed, 1 << 20, Token));
        var noise = new byte[length];
        random.NextBytes(noise);
        Assert.Equal(noise, BzzDecoder.Decode(BzzEncoder.Encode(noise, blockSize), 1 << 20, Token));
    }

    [Fact]
    public void Bzz_refuses_to_unpack_past_its_limit()
    {
        // Bytes of two values: they pack well and sort quickly.
        var random = new Random(346);
        var packed = BzzEncoder.Encode([.. Enumerable.Range(0, 50_000).Select(_ => (byte)random.Next(2))]);
        Assert.True(packed.Length < 12_000);
        var refused = Assert.Throws<ConversionRefusedException>(() => BzzDecoder.Decode(packed, 10_000, Token));
        Assert.Equal(DocumentLimits.TooLargeKey, refused.ErrorKey);
    }

    [Fact]
    public void Jb2_mask_draws_new_shapes_copies_refinements_and_non_mark_data()
    {
        var (data, expected) = TextMask();
        var image = Jb2Decoder.DecodeImage(data, null, DjvuLimits.Default, Token);
        Assert.Equal(120, image.Width);
        Assert.Equal(60, image.Height);
        Assert.Equal(8, image.Blits.Count);
        var page = new DjvuPage { Width = 120, Height = 60, Dpi = 300, Mask = image };
        Assert.Equal(expected, Mask(page));
    }

    [Fact]
    public void Jb2_mask_builds_on_a_shared_dictionary()
    {
        var dictionaryWriter = new Jb2Writer().Start(0, 0).New(Letter, type: 2).New(Bar, type: 2).Refine(0, LetterMark, type: 5);
        var dictionaryData = dictionaryWriter.End();
        var dictionary = Jb2Decoder.DecodeDictionary(dictionaryData, null, DjvuLimits.Default, Token);
        Assert.Equal(3, dictionary.Shapes.Count);

        var image = new Jb2Writer().RequireDictionary(3, dictionaryWriter.Library).Start(60, 30)
            .Copy(2, 2, 15).Copy(1, 15, 17).Copy(0, 30, 15).New(Bar, 45, 2).End();
        var mask = Jb2Decoder.DecodeImage(image, dictionary, DjvuLimits.Default, Token);
        var page = new DjvuPage { Width = 60, Height = 30, Dpi = 300, Mask = mask };
        Assert.Equal(Expected(60, 30, (LetterMark, 2, 15), (Bar, 15, 17), (Letter, 30, 15), (Bar, 45, 2)), Mask(page));

        // Without the dictionary it asks for, the mask is damage, not a crash.
        Assert.Throws<InvalidDataException>(() => Jb2Decoder.DecodeImage(image, null, DjvuLimits.Default, Token));
    }

    /// <summary>A grey test picture: a gradient with a dark block near the top left and a light stripe.</summary>
    private static byte[] GreySource(int width, int height)
    {
        var grey = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = 40 + 180 * x / width;
                if (x is >= 8 and < 30 && y is >= 5 and < 20)
                    value = 20;
                if (y is >= 40 and < 44)
                    value = 235;
                grey[y * width + x] = (byte)value;
            }
        }
        return grey;
    }

    // The pictures are 128 wide: a row of 4 to 7 samples at some scale doesn't come back exactly (the lifting filters
    // of the encoder and decoder treat such short rows differently), which a real decoder shares.
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Iw44_grey_picture_comes_back_close_to_its_source_the_right_way_up(int chunks)
    {
        const int width = 128, height = 70;
        var grey = GreySource(width, height);
        var y = grey.Select(g => (sbyte)(127 - g)).ToArray();
        var decoder = new Iw44Decoder(DjvuLimits.Default);
        foreach (var chunk in Iw44Encoder.Encode(width, height, y, slices: 240 / chunks, chunks: chunks))
            decoder.Decode(chunk, Token);
        var pixmap = decoder.ToPixmap(Token);
        Assert.Equal(width, pixmap.Width);
        Assert.Equal(height, pixmap.Height);
        var errors = Enumerable.Range(0, width * height).Select(i => Math.Abs(pixmap.Rgb[3 * i] - grey[i])).ToList();
        Assert.True(errors.Average() < 2.0, $"mean error {errors.Average()}");
        Assert.True(errors.Max() < 24, $"max error {errors.Max()}");
        Assert.True(pixmap.Rgb[3 * (10 * width + 15)] < 40, "the dark block is at the top");
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 10)]
    public void Iw44_colour_picture_comes_back_through_the_ycbcr_transform(bool halfChroma, int chromaDelay)
    {
        const int width = 128, height = 48;
        var y = new sbyte[width * height];
        var cb = new sbyte[width * height];
        var cr = new sbyte[width * height];
        for (var row = 0; row < height; row++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = row * width + x;
                y[i] = (sbyte)(-60 + x);
                cb[i] = (sbyte)(row < height / 2 ? 30 : -20);
                cr[i] = (sbyte)(x < width / 2 ? -25 : 35);
            }
        }
        var decoder = new Iw44Decoder(DjvuLimits.Default);
        foreach (var chunk in Iw44Encoder.Encode(width, height, y, cb, cr, slices: 240, halfChroma: halfChroma, chromaDelay: chromaDelay))
            decoder.Decode(chunk, Token);
        var pixmap = decoder.ToPixmap(Token);
        var total = 0.0;
        for (var i = 0; i < width * height; i++)
        {
            int b = cb[i], r = cr[i];
            var t2 = r + (r >> 1);
            var t3 = y[i] + 128 - (b >> 2);
            int[] want = [Math.Clamp(y[i] + 128 + t2, 0, 255), Math.Clamp(t3 - (t2 >> 1), 0, 255), Math.Clamp(t3 + (b << 1), 0, 255)];
            for (var c = 0; c < 3; c++)
                total += Math.Abs(pixmap.Rgb[3 * i + c] - want[c]);
        }
        var mean = total / (3 * width * height);
        Assert.True(mean < (halfChroma ? 8 : 4), $"mean error {mean}");
    }

    [Fact]
    public void Hidden_text_reads_lines_and_words()
    {
        var chunk = HelloText();
        var data = BzzDecoder.Decode(chunk[8..(8 + ReadSize(chunk))], 1 << 20, Token);
        var lines = DjvuText.Read(data, DjvuLimits.Default);
        Assert.Equal(2, lines.Count);
        Assert.Equal(["Hello", "DjVu"], lines[0].Words.Select(w => w.Text));
        Assert.Equal(new DjvuRect(35, 38, 30, 12), lines[0].Words[1].Box);
        Assert.Equal("Привет", lines[1].Text);
        Assert.Empty(lines[1].Words);
    }

    private static int ReadSize(byte[] chunk) => (chunk[4] << 24) | (chunk[5] << 16) | (chunk[6] << 8) | chunk[7];

    // ---- documents ----

    [Fact]
    public async Task Words_that_all_have_their_lines_box_are_spread_over_the_line()
    {
        // Some files, the DjVu v3 specification among them, give every word the box of its whole line.
        const string text = "Hello DjVu\n";
        var hello = new TextZone(6, 5, 38, 60, 12, 0, 6);
        var djvu = new TextZone(6, 5, 38, 60, 12, 6, 5);
        var line = new TextZone(5, 5, 38, 60, 12, 0, 11, hello, djvu);
        var page = new TextZone(1, 0, 0, 120, 60, 0, 11, line);
        var source = Save(DjvuFile(BilevelPage(100, 1, DjvuBuilder.Text(text, page))));
        using (var document = DjvuDocument.Open(source, cancellationToken: Token))
        {
            var words = Assert.Single(DjvuPdfWriter.Lines(document.ReadPage(0, Token))).Words;
            Assert.Equal(["Hello", "DjVu"], words.Select(w => w.Text));
            Assert.True(words[0].Left + words[0].Width <= words[1].Left, "the words don't overlap");
        }
        var pdfPath = Assert.Single(await Convert(source, "pdf"));
        using var pdf = PdfDocument.Open(pdfPath);
        var pdfWords = Assert.Single(pdf.GetPages()).GetWords().Select(w => w.Text).ToList();
        Assert.Equal(["Hello", "DjVu"], pdfWords);
    }

    [Fact]
    public void Words_with_boxes_of_their_own_keep_them()
    {
        var source = Save(DjvuFile(BilevelPage(100, 1, HelloText())));
        using var document = DjvuDocument.Open(source, cancellationToken: Token);
        var words = DjvuPdfWriter.Lines(document.ReadPage(0, Token))[0].Words;
        Assert.Equal(5 / 120.0, words[0].Left, 6);
        Assert.Equal(35 / 120.0, words[1].Left, 6);
    }

    [Fact]
    public async Task A_bilevel_page_becomes_a_pdf_page_of_its_size_with_a_mask_and_searchable_text()
    {
        var source = Save(DjvuFile(BilevelPage(100, 1, HelloText())));
        var pdfPath = Assert.Single(await Convert(source, "pdf"));
        using (var pdf = PdfDocument.Open(pdfPath))
        {
            var page = Assert.Single(pdf.GetPages());
            Assert.Equal(120 * 72 / 100.0, page.Width, 1);
            Assert.Equal(60 * 72 / 100.0, page.Height, 1);
            var image = Assert.Single(page.GetImages());
            Assert.True(image.IsImageMask);
            var words = page.GetWords().Select(w => w.Text).ToList();
            Assert.Contains("Hello", words);
            Assert.Contains("DjVu", words);
            Assert.Contains("Привет", words);
        }
        // Drawn back, black where the mask is.
        using var bitmap = PDFtoImage.Conversion.ToImage(File.ReadAllBytes(pdfPath), 0, null, new PDFtoImage.RenderOptions(Dpi: 100));
        var (_, expected) = TextMask();
        var agree = 0;
        for (var y = 0; y < 60; y++)
        {
            for (var x = 0; x < 120; x++)
            {
                if ((bitmap.GetPixel(x, y).Red < 128) == expected[y, x])
                    agree++;
            }
        }
        Assert.True(agree > 0.97 * 120 * 60, $"{agree} pixels agree");
    }

    [Fact]
    public async Task A_bilevel_page_becomes_a_picture_at_its_resolution()
    {
        var source = Save(DjvuFile(BilevelPage(100)));
        var png = Assert.Single(await Convert(source, "png"));
        using var image = new MagickImage(png);
        Assert.Equal(120u, image.Width);
        Assert.Equal(60u, image.Height);
        Assert.Equal(100, image.Density.ChangeUnits(DensityUnit.PixelsPerInch).X, 0);
        var (_, expected) = TextMask();
        using var pixels = image.GetPixels();
        for (var y = 0; y < 60; y++)
        {
            for (var x = 0; x < 120; x++)
                Assert.Equal(expected[y, x], pixels.GetPixel(x, y).GetChannel(0) < 128);
        }
        Assert.Single(await Convert(source, "jpg"));
    }

    /// <summary>
    /// A bundled document: a shared dictionary (DJVI) the first page includes, a compound page (background, two
    /// palette colours, text) and a photo page turned a quarter counter-clockwise.
    /// </summary>
    private static byte[] Book()
    {
        var dictionaryWriter = new Jb2Writer().Start(0, 0).New(Letter, type: 2).New(Bar, type: 2);
        var shared = Form("DJVI", Chunk("Djbz", dictionaryWriter.End()));
        var page1Mask = new Jb2Writer().RequireDictionary(2, dictionaryWriter.Library).Start(120, 60)
            .Copy(0, 5, 40).Copy(1, 20, 44).Copy(0, 35, 40).End();
        var page1 = Form("DJVU", Info(120, 60, 100), Include("dict0001.djbz"), Chunk("Sjbz", page1Mask));

        var bgWidth = 40;
        var bgHeight = 20; // a third of 120 × 60
        var bgY = new sbyte[bgWidth * bgHeight];
        var bgCb = new sbyte[bgWidth * bgHeight];
        var bgCr = new sbyte[bgWidth * bgHeight];
        Array.Fill(bgY, (sbyte)90); // light
        Array.Fill(bgCr, (sbyte)-20);
        var background = Iw44Encoder.Encode(bgWidth, bgHeight, bgY, bgCb, bgCr, slices: 100).Select(c => Chunk("BG44", c)).ToArray();
        var page2Mask = new Jb2Writer().Start(120, 60).New(Letter, 5, 40).New(Bar, 20, 44).Copy(0, 35, 40).End();
        var palette = Palette([(200, 0, 0), (0, 0, 180)], [0, 1, 0]);
        var page2 = Form("DJVU", [Info(120, 60, 100), Chunk("Sjbz", page2Mask), palette, .. background, HelloText()]);

        var photoY = GreySource(80, 50).Select(g => (sbyte)(127 - g)).ToArray();
        var photo = Iw44Encoder.Encode(80, 50, photoY, slices: 100).Select(c => Chunk("BG44", c)).ToArray();
        var page3 = Form("DJVU", [Info(80, 50, 50, flags: 6), .. photo]);

        return Bundle(("dict0001.djbz", shared, false), ("p0001.djvu", page1, true), ("p0002.djvu", page2, true), ("p0003.djvu", page3, true));
    }

    [Fact]
    public void A_bundled_document_reads_its_pages_with_the_shared_dictionary()
    {
        var source = Save(Book());
        using var document = DjvuDocument.Open(source, cancellationToken: Token);
        Assert.Equal(3, document.Count);
        var page1 = document.ReadPage(0, Token);
        Assert.Equal(Expected(120, 60, (Letter, 5, 40), (Bar, 20, 44), (Letter, 35, 40)), Mask(page1));

        var page2 = document.ReadPage(1, Token);
        var rgb = DjvuRenderer.Compose(page2, Token);
        (byte R, byte G, byte B) At(int x, int y) => (rgb[(y * 120 + x) * 3], rgb[(y * 120 + x) * 3 + 1], rgb[(y * 120 + x) * 3 + 2]);
        // The first letter red, the bar blue, the background light (Y 90 is light: 218 on the red channel minus chroma).
        Assert.Equal((200, 0, 0), At(5 + 3, 60 - 1 - (40 + 3)));
        Assert.Equal((0, 0, 180), At(25, 60 - 1 - (44 + 3)));
        var paper = At(100, 5);
        Assert.True(paper.R > 150 && paper.G > 150, $"background {paper}");

        var page3 = document.ReadPage(2, Token);
        Assert.Equal(1, page3.Rotation);
        Assert.Null(page3.Mask);
        Assert.Equal(0, document.LeftOut);
    }

    [Fact]
    public async Task A_bundled_document_becomes_a_pdf_with_its_layers_turns_and_text()
    {
        var source = Save(Book());
        var pdfPath = Assert.Single(await Convert(source, "pdf"));
        using var pdf = PdfDocument.Open(pdfPath);
        var pages = pdf.GetPages().ToList();
        Assert.Equal(3, pages.Count);
        Assert.Single(pages[0].GetImages(), i => i.IsImageMask);
        var images = pages[1].GetImages().ToList();
        Assert.Equal(3, images.Count); // the background and a mask per colour
        Assert.Equal(2, images.Count(i => i.IsImageMask));
        Assert.Contains("Hello", pages[1].GetWords().Select(w => w.Text));
        Assert.Equal(270, pages[2].Rotation.Value);
        Assert.Equal(80 * 72 / 50.0, pages[2].MediaBox.Bounds.Width, 1);
    }

    [Fact]
    public async Task A_bundled_document_becomes_one_tiff_or_pictures_of_the_chosen_pages()
    {
        var source = Save(Book());
        var tiff = Assert.Single(await Convert(source, "tiff"));
        using (var frames = new MagickImageCollection(tiff))
        {
            Assert.Equal(3, frames.Count);
            // The photo page turned a quarter counter-clockwise: 50 wide, 80 high.
            Assert.Equal(50u, frames[2].Width);
            Assert.Equal(80u, frames[2].Height);
        }
        var preset = new Preset();
        preset.Pdf.PageRange = "2";
        var png = Assert.Single(await Convert(source, "png", preset));
        using var image = new MagickImage(png);
        using var pixels = image.GetPixels();
        var red = pixels.GetPixel(8, 60 - 1 - 43).ToColor()!;
        Assert.True(red.R > 180 && red.G < 40 && red.B < 40, $"red letter {red}");
    }

    [Fact]
    public async Task A_page_with_a_foreground_picture_shows_it_through_the_mask()
    {
        var mask = new Jb2Writer().Start(120, 60).New(Letter, 5, 40).New(Bar, 20, 44).End();
        var fgY = new sbyte[10 * 5];
        var fgCb = new sbyte[10 * 5];
        var fgCr = new sbyte[10 * 5];
        Array.Fill(fgY, (sbyte)-60);
        Array.Fill(fgCb, (sbyte)60); // blue
        var foreground = Iw44Encoder.Encode(10, 5, fgY, fgCb, fgCr, slices: 100).Select(c => Chunk("FG44", c)).ToArray();
        var source = Save(DjvuFile(Form("DJVU", [Info(120, 60, 100), Chunk("Sjbz", mask), .. foreground])));
        using (var document = DjvuDocument.Open(source, cancellationToken: Token))
        {
            var page = document.ReadPage(0, Token);
            Assert.NotNull(page.Foreground);
            var rgb = DjvuRenderer.Compose(page, Token);
            var at = ((60 - 1 - 43) * 120 + 8) * 3;
            Assert.True(rgb[at + 2] > rgb[at] + 60, "blue letter");
            Assert.Equal(255, rgb[(5 * 120 + 100) * 3]); // white paper
        }
        using var pdf = PdfDocument.Open(Assert.Single(await Convert(source, "pdf")));
        var image = Assert.Single(pdf.GetPages().Single().GetImages());
        Assert.False(image.IsImageMask);
    }

    [Fact]
    public async Task Layers_filee_does_not_decode_are_named_in_the_result()
    {
        var source = Save(DjvuFile(BilevelPage(100, 1, Chunk("BG2k", [1, 2, 3, 4]))));
        LeftOutNotice? notice = null;
        Assert.Single(await Convert(source, "pdf", leftOut: n => notice = n));
        Assert.NotNull(notice);
        Assert.Equal(new LeftOutPart("notice.renderer_left_out.pictures", 1), Assert.Single(notice.Parts));
    }

    [Fact]
    public async Task An_indirect_document_without_its_page_file_is_refused_with_its_own_message()
    {
        var dirm = Chunk("DIRM", [0x01, 0x00, 0x01, .. BzzEncoder.Encode([0, 0, 10, 1, .. "p1.djvu"u8.ToArray(), 0])]);
        var source = Save(DjvuFile(Form("DJVM", dirm)));
        var refused = await Assert.ThrowsAsync<ConversionRefusedException>(() => Convert(source, "pdf"));
        Assert.Equal(DjvuDocument.IndirectKey, refused.ErrorKey);
    }

    // ---- CCITT G4 ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void G4_run_codes_are_a_prefix_code_for_every_run(bool black)
    {
        var codes = CcittG4.Codes(black).ToList();
        Assert.Equal(64 + 27 + 13, codes.Count);
        Assert.Equal(Enumerable.Range(0, 64).Concat(Enumerable.Range(1, 40).Select(i => i * 64)), codes.Select(c => c.Run));
        var words = codes.Select(c => System.Convert.ToString(c.Code, 2).PadLeft(c.Length, '0')).ToList();
        Assert.All(codes, c => Assert.True(c.Code < 1 << c.Length));
        for (var i = 0; i < words.Count; i++)
        {
            for (var j = 0; j < words.Count; j++)
                Assert.False(i != j && words[j].StartsWith(words[i], StringComparison.Ordinal), $"{words[i]} starts {words[j]}");
        }
        // With EOL's prefix (eight zeros) the codes fill the code space exactly.
        var kraft = codes.Sum(c => Math.Pow(2, -c.Length)) + Math.Pow(2, -8);
        Assert.Equal(1.0, kraft, 9);
    }

    /// <summary>A 1-bit picture (rows of (width + 7) / 8 bytes, a set bit black) from a pixel rule.</summary>
    private static byte[] Bits(int width, int height, Func<int, int, bool> black)
    {
        var stride = (width + 7) / 8;
        var bits = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (black(x, y))
                    bits[y * stride + (x >> 3)] |= (byte)(0x80 >> (x & 7));
            }
        }
        return bits;
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(13, 9)]
    [InlineData(64, 40)]
    [InlineData(6001, 7)] // runs past 2560 and every make-up code
    public void G4_round_trips_runs_text_noise_and_long_lines(int width, int height)
    {
        var random = new Random(width * 31 + height);
        var noise = Bits(width, height, (_, _) => random.Next(3) == 0);
        var stripes = Bits(width, height, (x, y) => (x / (1 + y)) % 2 == 0 || x == width - 1);
        var lines = Bits(width, height, (x, y) => y % 3 == 0 ? x >= 2 : x % 1700 < 64 * y + 3);
        foreach (var picture in new[] { noise, stripes, lines, new byte[noise.Length], Bits(width, height, (_, _) => true) })
        {
            var g4 = CcittG4.Encode(picture, width, height, Token);
            var back = new byte[picture.Length];
            CcittG4.Decode(g4, width, height, back, 0, Token);
            Assert.Equal(picture, back);
        }
    }

    [Fact]
    public void G4_damage_fails_as_damage_and_cut_short_data_leaves_white_rows()
    {
        var width = 100;
        var picture = Bits(width, 50, (x, y) => (x + y) % 7 < 3);
        var g4 = CcittG4.Encode(picture, width, 50, Token);
        var half = new byte[picture.Length];
        CcittG4.Decode(g4.AsSpan(0, g4.Length / 2), width, 50, half, 0, Token);
        Assert.Equal(picture.AsSpan(0, 13 * 5), half.AsSpan(0, 13 * 5));
        Assert.All(half[^13..], b => Assert.Equal(0, b));
        var random = new Random(4);
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 200; i++)
        {
            var junk = new byte[random.Next(1, 400)];
            random.NextBytes(junk);
            try
            {
                CcittG4.Decode(junk, 3000, 2000, new byte[375 * 2000], 0, Token);
            }
            catch (InvalidDataException)
            {
            }
        }
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
    }

    /// <summary>
    /// A wide page in two palette colours: letters and bars copied along it (some across the page's edges), a rule
    /// across the page and a long block (runs past 2560 pixels).
    /// </summary>
    private static byte[] WidePage()
    {
        var writer = new Jb2Writer().Start(3000, 60).New(Letter, 5, 40).New(Bar, 20, 44).Copy(0, 2995, 40).Copy(0, -3, 55).Copy(1, 1000, -2)
            .New(Jb2Writer.Shape(new string('#', 2900)), 50, 20)
            .New(Jb2Writer.Shape([.. Enumerable.Repeat(new string('#', 2700), 6)]), 100, 5);
        for (var i = 0; i < 40; i++)
            writer.Copy(i % 2, 200 + i * 60, 30 + i % 3);
        var colors = Enumerable.Range(0, 47).Select(b => b % 3 == 0 ? 1 : 0).ToArray();
        return DjvuFile(Form("DJVU", Info(3000, 60, 100), Chunk("Sjbz", writer.End()), Palette([(0, 0, 0), (200, 0, 0)], colors)));
    }

    [Theory]
    [InlineData(1, "JBIG2Decode")]
    [InlineData(2, "CCITTFaxDecode")]
    [InlineData(4, "FlateDecode")]
    public void Each_mask_coding_draws_back_as_the_page(int coding, string filter)
    {
        using var document = DjvuDocument.Open(Save(WidePage()), cancellationToken: Token);
        var page = document.ReadPage(0, Token);
        using var writer = new OcrPdfWriter();
        DjvuPdfWriter.AddPage(writer, page, (MaskCodings)coding, Token);
        var output = new MemoryStream();
        writer.Document.Save(output);
        var bytes = output.ToArray();
        using (var pdf = PdfDocument.Open(bytes))
        {
            var images = pdf.GetPages().Single().GetImages().ToList();
            Assert.Equal(2, images.Count); // a mask per colour
            Assert.All(images, image => Assert.Contains(filter, image.ImageDictionary.ToString()));
        }
        // PDFtoImage (pdfium's own decoders) draws it as the page looks.
        var rgb = DjvuRenderer.Compose(page, Token);
        using var bitmap = PDFtoImage.Conversion.ToImage(bytes, 0, null, new PDFtoImage.RenderOptions(Dpi: 100));
        Assert.Equal((3000, 60), (bitmap.Width, bitmap.Height));
        var differ = 0;
        for (var y = 0; y < 60; y++)
        {
            for (var x = 0; x < 3000; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                var at = (y * 3000 + x) * 3;
                if (Math.Abs(pixel.Red - rgb[at]) > 64 || Math.Abs(pixel.Green - rgb[at + 1]) > 64 || Math.Abs(pixel.Blue - rgb[at + 2]) > 64)
                    differ++;
            }
        }
        Assert.True(differ < 3000 * 60 / 200, $"{differ} pixels differ");
    }

    [Fact]
    public async Task Text_masks_become_jbig2_symbols_much_smaller_than_g4()
    {
        // A page of 600 copies of two shapes: the symbols are coded once.
        var writer = new Jb2Writer().Start(1200, 600).New(Letter, 5, 5).New(Bar, 20, 5);
        for (var i = 0; i < 600; i++)
            writer.Copy(i % 2, 40 + i % 30 * 38, 10 + i / 30 * 29);
        var source = Save(DjvuFile(Form("DJVU", Info(1200, 600, 300), Chunk("Sjbz", writer.End()))));
        var pdfPath = Assert.Single(await Convert(source, "pdf"));
        using var pdf = PdfDocument.Open(pdfPath);
        var image = Assert.Single(pdf.GetPages().Single().GetImages());
        Assert.Contains("JBIG2Decode", image.ImageDictionary.ToString());
        using var document = DjvuDocument.Open(source, cancellationToken: Token);
        var page = document.ReadPage(0, Token);
        var g4 = CcittG4.Encode(DjvuRenderer.MaskBits(page, cancellationToken: Token), 1200, 600, Token);
        Assert.True(image.RawBytes.Length * 3 < g4.Length, $"JBIG2 {image.RawBytes.Length} bytes, G4 {g4.Length}");
    }

    [Fact]
    public async Task A_dithered_mask_stays_flate_when_g4_and_jbig2_would_be_larger()
    {
        // Noise, each row the one above moved by a byte: G4 codes every change and JBIG2's few neighbours don't see
        // the move, while Flate finds each row in the one before.
        var random = new Random(8);
        var noise = Enumerable.Range(0, 512).Select(_ => random.Next(2) == 0).ToArray();
        var rows = Enumerable.Range(0, 64).Select(y => string.Concat(Enumerable.Range(0, 512).Select(x => noise[(x + 8 * y) % 512] ? '#' : '.'))).ToArray();
        var mask = new Jb2Writer().Start(512, 64).New(Jb2Writer.Shape(rows), 0, 0).End();
        var source = Save(DjvuFile(Form("DJVU", Info(512, 64, 100), Chunk("Sjbz", mask))));
        using var pdf = PdfDocument.Open(Assert.Single(await Convert(source, "pdf")));
        var image = Assert.Single(pdf.GetPages().Single().GetImages());
        Assert.True(image.IsImageMask);
        Assert.Contains("FlateDecode", image.ImageDictionary.ToString());
    }

    // ---- Smmr masks ----

    [Theory]
    [InlineData(0, false)]
    [InlineData(7, false)]
    [InlineData(0, true)]
    [InlineData(16, true)]
    public void An_smmr_mask_reads_as_the_page_mask(int rowsPerStripe, bool inverted)
    {
        var (_, expected) = TextMask();
        var bits = Bits(120, 60, (x, y) => expected[y, x]);
        var source = Save(DjvuFile(Form("DJVU", Info(120, 60, 100), Smmr(bits, 120, 60, rowsPerStripe, inverted))));
        using var document = DjvuDocument.Open(source, cancellationToken: Token);
        var page = document.ReadPage(0, Token);
        Assert.Equal(expected, Mask(page));
        Assert.Equal(0, page.LeftOut);
    }

    [Fact]
    public void Damaged_smmr_masks_fail_as_damage()
    {
        byte[] Page(byte[] smmr) => DjvuFile(Form("DJVU", Info(120, 60, 100), Chunk("Smmr", smmr)));
        foreach (var smmr in new byte[][]
        {
            [(byte)'M', (byte)'M', (byte)'X', 0, 0, 120, 0, 60],
            [(byte)'M', (byte)'M', (byte)'R', 0, 0, 120, 0, 60, 0x00, 0x00, 0x00, 0x01, 0xFF], // an extension code
            [(byte)'M', (byte)'M', (byte)'R', 2, 0, 120, 0, 60, 0, 10, 0x7F, 0xFF, 0xFF, 0xFF], // a stripe past the end
        })
        {
            using var document = DjvuDocument.Open(Save(Page(smmr), "damaged.djvu"), cancellationToken: Token);
            Assert.Throws<InvalidDataException>(() => document.ReadPage(0, Token));
        }
    }

    // ---- bookmarks and links ----

    /// <summary>
    /// Three pages: the first with links to page 3 (by number), the next page ("#+1"), page 2 by id, a web address, a
    /// polygon to the first page, a script, a local file and a line (none of the last three links); a shared
    /// file with a link every page that includes it gets; bookmarks nested two deep, one to a web address and one to a
    /// file next to the document (a title only).
    /// </summary>
    private static byte[] LinkedBook(bool compressed = true)
    {
        const string links = """
            (maparea "#3" "to page three" (rect 10 10 20 10) (xor))
            (maparea "#+1" "" (rect 40 10 20 10))
            (maparea (url "#p2" "_self") "" (oval 70 10 20 10))
            (maparea "https://example.com/a b?c=1" "web" (rect 10 30 30 10) (border #ff0000))
            (maparea "#1" "" (poly 50 30 60 45 70 30))
            (maparea "javascript:alert(1)" "" (rect 80 30 10 10))
            (maparea "file:///etc/passwd" "" (rect 95 30 10 10))
            (maparea "#2" "" (line 0 0 50 50))
            (zoom page) (background #FFFFFF)
            """;
        var shared = Form("DJVI", AnnotationChunk("(maparea \"http://shared.example/\" \"\" (text 0 50 120 10))", compressed));
        byte[] PageForm(params byte[][] extra) => Form("DJVU", [Info(120, 60, 100), .. extra]);
        var outline = Outline(
            new Bookmark("Chapter one", "#1", new Bookmark("Section", "#p2", new Bookmark("Deeper", "#3"))),
            new Bookmark("Web", "https://example.org/"),
            new Bookmark("Next volume", "volume2.djvu"),
            new Bookmark("Script", "javascript:alert(1)"));
        return Bundle([outline],
            ("shared.djvi", shared, false),
            ("p1", PageForm(Include("shared.djvi"), AnnotationChunk(links, compressed)), true),
            ("p2", PageForm(), true),
            ("p3", PageForm(Include("shared.djvi")), true));
    }

    [Fact]
    public void Links_and_bookmarks_are_read_with_their_targets()
    {
        using var document = DjvuDocument.Open(Save(LinkedBook()), cancellationToken: Token);
        var links = document.ReadPage(0, Token).Links;
        Assert.Equal([2, 1, 1, -1, 0, -1], links.Select(l => l.Page));
        Assert.Equal("https://example.com/a b?c=1", links[3].Url);
        Assert.Equal("http://shared.example/", links[5].Url);
        Assert.Equal(new DjvuLink(50, 30, 20, 15, "#1", 0), links[4]);
        Assert.Single(document.ReadPage(2, Token).Links);
        Assert.Empty(document.ReadPage(1, Token).Links);

        Assert.Equal(["Chapter one", "Web", "Next volume", "Script"], document.Bookmarks.Select(b => b.Title));
        Assert.Equal("Deeper", document.Bookmarks[0].Children[0].Children[0].Title);
        Assert.Equal(1, document.PageOf("#p2"));
        Assert.Equal(-1, document.PageOf("#4"));
        Assert.Equal(-1, document.PageOf("#-1", 0));
        Assert.Equal(-1, document.PageOf("#nowhere"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Links_become_pdf_link_annotations_and_bookmarks_its_outline(bool compressed)
    {
        var pdfPath = Assert.Single(await Convert(Save(LinkedBook(compressed)), "pdf"));
        using (var pdf = PdfDocument.Open(pdfPath))
        {
            var pages = pdf.GetPages().ToList();
            var links = pages[0].GetAnnotations().ToList();
            Assert.Equal(6, links.Count);
            Assert.All(links, a => Assert.Equal(UglyToad.PdfPig.Annotations.AnnotationType.Link, a.Type));
            var text = string.Join("\n", links.Select(a => a.AnnotationDictionary.ToString()));
            Assert.Contains("https://example.com/a%20b?c=1", text);
            Assert.Contains("http://shared.example/", text);
            Assert.DoesNotContain("javascript", text);
            Assert.DoesNotContain("passwd", text);
            // The first link (page pixels 10, 10, 20 × 10 at 100 dpi) at 72 / 100 points per pixel.
            var first = links[0].Rectangle;
            Assert.Equal(7.2, first.Left, 2);
            Assert.Equal(7.2, first.Bottom, 2);
            Assert.Equal(21.6, first.Right, 2);
            Assert.Equal(14.4, first.Top, 2);
            Assert.Single(pages[2].GetAnnotations());

            Assert.True(pdf.TryGetBookmarks(out var outline));
            var chapter = Assert.IsType<UglyToad.PdfPig.Outline.DocumentBookmarkNode>(outline.Roots[0]);
            Assert.Equal(("Chapter one", 1), (chapter.Title, chapter.PageNumber));
            var section = Assert.IsType<UglyToad.PdfPig.Outline.DocumentBookmarkNode>(Assert.Single(chapter.Children));
            Assert.Equal(2, section.PageNumber);
            Assert.Equal(3, Assert.IsType<UglyToad.PdfPig.Outline.DocumentBookmarkNode>(Assert.Single(section.Children)).PageNumber);
            Assert.Equal("https://example.org/", Assert.IsType<UglyToad.PdfPig.Outline.UriBookmarkNode>(outline.Roots[1]).Uri);
        }
        // The bookmarks that lead nowhere Filee links to keep only their titles.
        var raw = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(pdfPath));
        Assert.Contains(Utf16Hex("Next volume"), raw);
        Assert.Contains(Utf16Hex("Script"), raw);
        Assert.DoesNotContain("javascript", raw);
        Assert.DoesNotContain("volume2", raw);
    }

    private static string Utf16Hex(string text) => string.Concat(System.Text.Encoding.BigEndianUnicode.GetBytes(text).Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("JavaScript:alert(1)", false)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    [InlineData("vbscript:x", false)]
    [InlineData("data:text/html,x", false)]
    [InlineData("other.djvu", false)]
    [InlineData("\\\\server\\share", false)]
    [InlineData("https://example.com/", true)]
    [InlineData("http://example.com/x?y=1#z", true)]
    [InlineData("mailto:someone@example.com", true)]
    [InlineData("ftp://example.com/file", true)]
    public void Only_web_addresses_are_links_out_of_the_document(string url, bool allowed) =>
        Assert.Equal(allowed, DjvuAnnotations.IsWebAddress(url));

    [Fact]
    public void Damaged_annotations_leave_the_page_without_links()
    {
        var limits = DjvuLimits.Default;
        Assert.Empty(DjvuAnnotations.ReadLinks("(maparea \"#1\" \"\" (rect 1 1 5 5)", limits));
        Assert.Empty(DjvuAnnotations.ReadLinks(new string('(', 100_000) + new string(')', 100_000), limits));
        Assert.Empty(DjvuAnnotations.ReadLinks("(maparea \"#1\" \"\" (rect 1 1 x 5))) (maparea \"#1", limits));
        Assert.Equal("a\"b\nc\u0001", Assert.Single(Assert.Single(DjvuAnnotations.Parse("(\"a\\\"b\\nc\\001\")", limits))));
        // One good link after damage still counts when the brackets balance.
        Assert.Single(DjvuAnnotations.ReadLinks("(junk (more)) (maparea \"#1\" \"\" (rect 1 1 5 5))", limits));
    }

    // ---- indirect documents ----

    /// <summary>Writes an indirect document's index and files into a new folder; the index path.</summary>
    private string SaveIndirect((byte[] Index, (string Id, byte[] File)[] Files) document, params string[] skip)
    {
        var folder = Path.Combine(_folder, "indirect-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        foreach (var (id, file) in document.Files)
        {
            if (!skip.Contains(id))
                File.WriteAllBytes(Path.Combine(folder, id), file);
        }
        var index = Path.Combine(folder, "index.djvu");
        File.WriteAllBytes(index, document.Index);
        return index;
    }

    private static (byte[] Index, (string Id, byte[] File)[] Files) IndirectBook(string secondId = "p0002.djvu")
    {
        var dictionaryWriter = new Jb2Writer().Start(0, 0).New(Letter, type: 2).New(Bar, type: 2);
        var shared = Form("DJVI", Chunk("Djbz", dictionaryWriter.End()));
        var mask = new Jb2Writer().RequireDictionary(2, dictionaryWriter.Library).Start(120, 60).Copy(0, 5, 40).Copy(1, 20, 44).End();
        var page1 = Form("DJVU", Info(120, 60, 100), Include("dict.djbz"), Chunk("Sjbz", mask), AnnotationChunk("(maparea \"#2\" \"\" (rect 5 40 8 8))"));
        var page2 = Form("DJVU", Info(120, 60, 100), Chunk("Sjbz", new Jb2Writer().Start(120, 60).New(Bar, 50, 30).End()));
        var thumbnails = Form("THUM", Chunk("TH44", [0, 1]));
        return Indirect([Outline(new Bookmark("Second", "#" + secondId))],
            ("dict.djbz", shared, 0), ("p0001.djvu", page1, 1), (secondId, page2, 1), ("thumbs.djvu", thumbnails, 2));
    }

    [Fact]
    public async Task An_indirect_document_reads_its_page_files_next_to_the_index()
    {
        // Thumbnails may be missing; pages and included files may not.
        var index = SaveIndirect(IndirectBook(), "thumbs.djvu");
        using (var document = DjvuDocument.Open(index, cancellationToken: Token))
        {
            Assert.Equal(2, document.Count);
            Assert.Equal(Expected(120, 60, (Letter, 5, 40), (Bar, 20, 44)), Mask(document.ReadPage(0, Token)));
            Assert.Equal(Expected(120, 60, (Bar, 50, 30)), Mask(document.ReadPage(1, Token)));
            Assert.Equal(1, Assert.Single(document.ReadPage(0, Token).Links).Page);
            Assert.Equal(1, document.PageOf(document.Bookmarks[0].Url));
        }
        using var pdf = PdfDocument.Open(Assert.Single(await Convert(index, "pdf")));
        Assert.Equal(2, pdf.NumberOfPages);
        Assert.Single(pdf.GetPage(1).GetAnnotations());

        foreach (var missing in new[] { "p0001.djvu", "dict.djbz" })
        {
            var refused = await Assert.ThrowsAsync<ConversionRefusedException>(() => Convert(SaveIndirect(IndirectBook(), missing), "pdf"));
            Assert.Equal(DjvuDocument.IndirectKey, refused.ErrorKey);
        }
    }

    [Theory]
    [InlineData("../outside.djvu")]
    [InlineData("sub/../../outside.djvu")]
    [InlineData("..\\outside.djvu")]
    [InlineData("C:/outside.djvu")]
    [InlineData("/outside.djvu")]
    [InlineData("//server/share/outside.djvu")]
    [InlineData("NUL")]
    [InlineData("p.djvu:stream")]
    public void An_indirect_document_reads_no_file_outside_its_folder(string id)
    {
        // The page exists where the name leads (outside the index file's folder) and is still refused.
        var book = IndirectBook(id);
        var index = SaveIndirect(book, id);
        var page = book.Files.First(f => f.Id == id).File;
        var outside = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(index))!, "outside.djvu");
        File.WriteAllBytes(outside, page);
        var refused = Assert.Throws<ConversionRefusedException>(() => DjvuDocument.Open(index, cancellationToken: Token).Dispose());
        Assert.Equal(DjvuDocument.IndirectKey, refused.ErrorKey);
    }

    [Fact]
    public void An_indirect_document_follows_no_link_out_of_its_folder()
    {
        var book = IndirectBook("linked.djvu");
        var index = SaveIndirect(book, "linked.djvu");
        var elsewhere = Path.Combine(_folder, "elsewhere-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(elsewhere);
        var target = Path.Combine(elsewhere, "linked.djvu");
        File.WriteAllBytes(target, book.Files.First(f => f.Id == "linked.djvu").File);
        try
        {
            File.CreateSymbolicLink(Path.Combine(Path.GetDirectoryName(index)!, "linked.djvu"), target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This machine doesn't let the test make a symbolic link.");
        }
        var refused = Assert.Throws<ConversionRefusedException>(() => DjvuDocument.Open(index, cancellationToken: Token).Dispose());
        Assert.Equal(DjvuDocument.IndirectKey, refused.ErrorKey);
    }

    [Fact]
    public void DjVu_is_a_readable_document_format_with_a_route_to_pdf_and_pictures()
    {
        var format = FormatRegistry.Detect("x.djv");
        Assert.NotNull(format);
        Assert.Equal("djvu", format.Id);
        Assert.Equal(FormatCategory.Document, format.Category);
        var converter = Assert.Single(fx.Converters.OfType<DjvuConverter>());
        Assert.Equal(["pdf", "png", "jpg", "tiff"], converter.Edges.Select(e => e.To));
    }

    // ---- hostile files ----

    [Fact]
    public void Hostile_pages_are_refused_quickly()
    {
        var watch = Stopwatch.StartNew();
        // A page claiming 65,535 × 65,535 pixels.
        AssertTooLarge(DjvuFile(Form("DJVU", Info(65535, 65535))), limits: null);
        // A shape claiming 65,535 × 65,535 pixels.
        var huge = new Jb2Writer().Start(100, 100).Record(1).Size(65535, 65535).End();
        AssertTooLarge(DjvuFile(Form("DJVU", Info(100, 100), Chunk("Sjbz", huge))), limits: null);
        // An IW44 picture claiming 65,535 × 65,535 pixels.
        byte[] bigPicture = [0, 1, 1, 2, 0xFF, 0xFF, 0xFF, 0xFF, 0x80, 0, 0, 0];
        AssertTooLarge(DjvuFile(Form("DJVU", Info(100, 100), Chunk("BG44", bigPicture))), limits: null);
        // More shapes, number cells and text zones than the (lowered) limits allow.
        var shapes = new Jb2Writer().Start(200, 200).New(Letter, 0, 0);
        for (var i = 0; i < 50; i++)
            shapes.Copy(0, (i * 7) % 190, (i * 3) % 190);
        AssertRefused(DjvuFile(Form("DJVU", Info(200, 200), Chunk("Sjbz", shapes.End()))), DjvuLimits.Default with { MaxBlits = 20 });
        AssertRefused(DjvuFile(Form("DJVU", Info(200, 200), Chunk("Sjbz", new Jb2Writer().Start(200, 200).New(Letter, 0, 0).New(Bar, 20, 0).End()))),
            DjvuLimits.Default with { MaxNumberCells = 40 });
        var zones = Enumerable.Range(0, 30).Select(i => new TextZone(6, i * 3, 0, 3, 10, 0, 1)).ToArray();
        var text = DjvuBuilder.Text("x", new TextZone(1, 0, 0, 120, 60, 0, 1, new TextZone(5, 0, 0, 120, 10, 0, 1, zones)));
        var zoneData = BzzDecoder.Decode(text[8..(8 + ReadSize(text))], 1 << 20, Token);
        Assert.Throws<ConversionRefusedException>(() => DjvuText.Read(zoneData, DjvuLimits.Default with { MaxTextZones = 10 }));
        // Annotations with more parts than the (lowered) limit, and an Smmr mask claiming 65,535 × 65,535 pixels.
        var annotations = string.Concat(Enumerable.Repeat("(maparea \"#1\" \"\" (rect 1 1 5 5)) ", 20));
        AssertRefused(DjvuFile(Form("DJVU", Info(100, 100), AnnotationChunk(annotations))), DjvuLimits.Default with { MaxAnnotationNodes = 50 });
        AssertTooLarge(DjvuFile(Form("DJVU", Info(100, 100), Chunk("Smmr", [(byte)'M', (byte)'M', (byte)'R', 0, 0xFF, 0xFF, 0xFF, 0xFF]))), limits: null);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
    }

    private void AssertRefused(byte[] file, DjvuLimits? limits, string key = DocumentLimits.ErrorKey)
    {
        var source = Save(file, "hostile.djvu");
        using var document = DjvuDocument.Open(source, limits, Token);
        var refused = Assert.Throws<ConversionRefusedException>(() => document.ReadPage(0, Token));
        Assert.Equal(key, refused.ErrorKey);
    }

    private void AssertTooLarge(byte[] file, DjvuLimits? limits) => AssertRefused(file, limits, DocumentLimits.TooLargeKey);

    [Fact]
    public void Damaged_and_looping_documents_fail_as_damage()
    {
        // Two included files including each other, and a page asking for a dictionary neither has.
        var a = Form("DJVI", Include("b"));
        var b = Form("DJVI", Include("a"));
        var mask = new Jb2Writer().RequireDictionary(2, [Letter, Bar]).Start(50, 50).Copy(0, 1, 1).End();
        var page = Form("DJVU", Info(50, 50), Include("a"), Chunk("Sjbz", mask));
        var source = Save(Bundle(("a", a, false), ("b", b, false), ("p", page, true)));
        using (var document = DjvuDocument.Open(source, cancellationToken: Token))
            Assert.Throws<InvalidDataException>(() => document.ReadPage(0, Token));

        // A bundle cut short in its last page, and a directory that claims more parts than it holds.
        var book = Book();
        var cut = Save(book[..(book.Length - 40)], "cut.djvu");
        try
        {
            using var document = DjvuDocument.Open(cut, cancellationToken: Token);
            for (var i = 0; i < document.Count; i++)
                _ = document.ReadPage(i, Token);
        }
        catch (InvalidDataException)
        {
        }
        var lying = Chunk("DIRM", [0x81, 0x01, 0x00, .. new byte[16]]);
        Assert.Throws<InvalidDataException>(() => DjvuDocument.Open(Save(DjvuFile(Form("DJVM", lying)), "lying.djvu"), cancellationToken: Token).Dispose());
        Assert.Throws<InvalidDataException>(() => DjvuDocument.Open(Save("not a djvu file"u8.ToArray(), "text.djvu"), cancellationToken: Token).Dispose());
    }

    private sealed class FolderOutput(string folder) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => Path.Combine(folder, $"out{suffix}.{extension}");
    }
}
