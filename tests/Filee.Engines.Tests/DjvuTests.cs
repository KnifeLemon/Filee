// Filee's own DjVu reader (Djvu/) and its converter: every piece checked against an encoder written in code
// (DjvuBuilder) — the ZP-coder and BZZ round trips, JB2 masks (direct shapes, copies, refinements, non-mark data, a
// shared dictionary, comments and a reset) against the shapes drawn by the test, IW44 pictures against their source
// pixels, hidden text as PDF words — then whole documents (one page and bundled) to PDF, PNG and TIFF, and hostile
// files refused quickly through the limits.

using System.Diagnostics;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Djvu;
using Filee.Engines.Documents;
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
        Assert.Equal(DocumentLimits.ErrorKey, refused.ErrorKey);
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
    public async Task An_indirect_document_is_refused_with_its_own_message()
    {
        var dirm = Chunk("DIRM", [0x01, 0x00, 0x01, .. BzzEncoder.Encode([0, 0, 10, 1, .. "p1.djvu"u8.ToArray(), 0])]);
        var source = Save(DjvuFile(Form("DJVM", dirm)));
        var refused = await Assert.ThrowsAsync<ConversionRefusedException>(() => Convert(source, "pdf"));
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
        AssertRefused(DjvuFile(Form("DJVU", Info(65535, 65535))), limits: null);
        // A shape claiming 65,535 × 65,535 pixels.
        var huge = new Jb2Writer().Start(100, 100).Record(1).Size(65535, 65535).End();
        AssertRefused(DjvuFile(Form("DJVU", Info(100, 100), Chunk("Sjbz", huge))), limits: null);
        // An IW44 picture claiming 65,535 × 65,535 pixels.
        byte[] bigPicture = [0, 1, 1, 2, 0xFF, 0xFF, 0xFF, 0xFF, 0x80, 0, 0, 0];
        AssertRefused(DjvuFile(Form("DJVU", Info(100, 100), Chunk("BG44", bigPicture))), limits: null);
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
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
    }

    private void AssertRefused(byte[] file, DjvuLimits? limits)
    {
        var source = Save(file, "hostile.djvu");
        using var document = DjvuDocument.Open(source, limits, Token);
        var refused = Assert.Throws<ConversionRefusedException>(() => document.ReadPage(0, Token));
        Assert.Equal(DocumentLimits.ErrorKey, refused.ErrorKey);
    }

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
