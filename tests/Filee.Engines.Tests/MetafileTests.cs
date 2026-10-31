// Filee's own EMF / WMF renderer (Metafiles/), on every platform: metafiles written record by record in code
// (MetafileBuilders) are drawn and their pixels checked, converted to PNG and to a vector PDF through the engines, drawn
// on a layout engine page, and hostile files (cut off, oversized, looping counts, wrong object numbers, past the limits)
// stop with a clear error. MetafileGdiTests compares the renderer with GDI+ on Windows.

using System.Diagnostics;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Metafiles;
using Filee.Engines.Rendering;
using Filee.Engines.Typesetting;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using SkiaSharp;

namespace Filee.Engines.Tests;

public sealed class MetafileTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const uint Red = 0x0000FF, Green = 0x00FF00, Blue = 0xFF0000; // COLORREF: 0x00BBGGRR

    /// <summary>192 × 96 px: a red square at the left, a green circle at the right, no outlines.</summary>
    private static byte[] Shapes() => new EmfBuilder(192, 96)
        .Select(EmfBuilder.NullPen)
        .Brush(1, Red).Select(1).Rectangle(8, 8, 88, 88)
        .Brush(2, Green).Select(2).Ellipse(104, 8, 184, 88)
        .ToArray();

    private static SKColor Pixel(SKBitmap bitmap, int x, int y) => bitmap.GetPixel(x, y);

    private static (SKBitmap Bitmap, float Dpi) Render(byte[] data, float dpi) =>
        Metafile.Render(data, dpi, cancellationToken: TestContext.Current.CancellationToken);

    private static MetafileDrawing Draw(byte[] data, float pixelsPerPoint = 0, MetafileLimits? limits = null) =>
        Metafile.Draw(data, pixelsPerPoint, limits, TestContext.Current.CancellationToken);

    [Fact]
    public void Emf_shapes_are_drawn_where_their_records_put_them()
    {
        var (bitmap, dpi) = Render(Shapes(), 96);
        using (bitmap)
        {
            Assert.Equal(96, dpi, 0.01);
            Assert.Equal((192, 96), (bitmap.Width, bitmap.Height));
            Assert.Equal(SKColors.Red, Pixel(bitmap, 48, 48));
            Assert.Equal(SKColors.Lime, Pixel(bitmap, 144, 48));
            Assert.Equal(0, Pixel(bitmap, 4, 4).Alpha); // outside the shapes: transparent
            Assert.Equal(0, Pixel(bitmap, 108, 12).Alpha); // the circle's corner
        }
    }

    [Fact]
    public void The_picture_is_rendered_at_the_requested_dpi()
    {
        var (bitmap, dpi) = Render(Shapes(), 300);
        using (bitmap)
        {
            Assert.Equal(300, dpi, 0.01);
            Assert.Equal((600, 300), (bitmap.Width, bitmap.Height)); // 2 × 1 inch
            Assert.Equal(SKColors.Red, Pixel(bitmap, 150, 150));
        }
    }

    [Fact]
    public void Map_modes_and_world_transforms_place_the_drawing()
    {
        // MM_HIMETRIC (0.01 mm, y up) with the window origin moved: a blue rectangle 10 mm from the left, 5 mm down.
        var emf = new EmfBuilder(192, 96)
            .Select(EmfBuilder.NullPen)
            .Add(17, 3) // SETMAPMODE MM_HIMETRIC
            .Brush(1, Blue).Select(1)
            .Rectangle(1000, -500, 2000, -1500)
            // World transform: scale by 2 around the origin, then a green square of 5 px at (70, 10) lands at (140, 20).
            .Add(17, 1) // MM_TEXT
            .Add(35, BitConverter.SingleToInt32Bits(2), 0, 0, BitConverter.SingleToInt32Bits(2), 0, 0)
            .Brush(2, Green).Select(2)
            .Rectangle(70, 10, 85, 25)
            .ToArray();
        var (bitmap, _) = Render(emf, 96);
        using (bitmap)
        {
            // 10 mm = 37.8 px, 5 mm = 18.9 px.
            Assert.Equal(SKColors.Blue, Pixel(bitmap, 56, 37));
            Assert.Equal(0, Pixel(bitmap, 30, 37).Alpha);
            Assert.Equal(SKColors.Lime, Pixel(bitmap, 155, 35));
            Assert.Equal(0, Pixel(bitmap, 75, 15).Alpha);
        }
    }

    [Fact]
    public void Clipping_paths_and_bitmaps_are_drawn()
    {
        var emf = new EmfBuilder(192, 96)
            .Select(EmfBuilder.NullPen)
            // Clip to the left half, then fill everything red: only the left half is red.
            .Add(33) // SAVEDC
            .Add(30, 0, 0, 96, 96) // INTERSECTCLIPRECT
            .Brush(1, Red).Select(1).Rectangle(0, 0, 192, 48)
            .Add(34, -1) // RESTOREDC
                         // A path filled with the brush: a triangle in the lower right.
            .Add(59) // BEGINPATH
            .Add(27, 120, 90) // MOVETOEX
            .Add(54, 180, 90) // LINETO
            .Add(54, 180, 60)
            .Add(61) // CLOSEFIGURE
            .Add(60) // ENDPATH
            .Brush(2, Blue).Select(2)
            .Add(62, 0, 0, 0, 0) // FILLPATH
                                 // A 2 × 2 bitmap stretched to 32 × 32 at (10, 56): red, green / blue, white.
            .StretchDib(10, 56, 32, 32, new uint[,] { { 0xFF0000, 0x00FF00 }, { 0x0000FF, 0xFFFFFF } })
            .ToArray();
        var (bitmap, _) = Render(emf, 96);
        using (bitmap)
        {
            File.WriteAllBytes(Path.Combine(Environment.GetEnvironmentVariable("FILEE_DEBUG_OUT") ?? Path.GetTempPath(), "clip.png"), bitmap.Encode(SKEncodedImageFormat.Png, 100).ToArray());
            Assert.Equal(SKColors.Red, Pixel(bitmap, 48, 24));
            Assert.Equal(0, Pixel(bitmap, 144, 24).Alpha);
            Assert.Equal(SKColors.Blue, Pixel(bitmap, 170, 85));
            Assert.Equal(0, Pixel(bitmap, 130, 65).Alpha);
            Assert.Equal(SKColors.Red, Pixel(bitmap, 16, 62));
            Assert.Equal(SKColors.Lime, Pixel(bitmap, 36, 62));
            Assert.Equal(SKColors.Blue, Pixel(bitmap, 16, 82));
            Assert.Equal(SKColors.White, Pixel(bitmap, 36, 82));
        }
    }

    [Fact]
    public void Text_is_drawn_with_an_installed_font_at_the_given_advances()
    {
        Assert.SkipWhen(new FontResolver().Resolve(null, FontStyle.Regular) is null, "No font is installed.");
        int[] dx = [40, 40, 40];
        var emf = new EmfBuilder(192, 96)
            .Add(18, 1) // SETBKMODE TRANSPARENT
            .Add(22, 24) // SETTEXTALIGN TA_BASELINE
            .Font(1, 40, "Arial").Select(1)
            .Text(10, 70, "III", dx)
            .ToArray();
        var (bitmap, _) = Render(emf, 96);
        using (bitmap)
        {
            // Three strokes 40 px apart: ink in each of the three columns, none after the last one.
            var columns = Enumerable.Range(0, 3).Select(i => Ink(bitmap, new SKRectI(10 + i * 40, 30, 10 + i * 40 + 30, 72))).ToArray();
            Assert.All(columns, ink => Assert.True(ink > 20, $"ink per column: {string.Join(", ", columns)}"));
            Assert.Equal(0, Ink(bitmap, new SKRectI(140, 0, 192, 96)));
        }
    }

    [Fact]
    public void Wmf_with_a_placeable_header_maps_its_window_onto_the_box()
    {
        // 1 × 0.5 inch at 1440 units per inch; the records draw in a window of 100 × 50 units.
        var wmf = new WmfBuilder(1440, 720, unitsPerInch: 1440)
            .Add(0x0103, 8) // SETMAPMODE MM_ANISOTROPIC
            .Add(0x020B, 0, 0) // SETWINDOWORG
            .Add(0x020C, 50, 100) // SETWINDOWEXT (y, x)
            .Pen(5, 0, 0).Select(0) // PS_NULL
            .Brush(Blue).Select(1)
            .Rectangle(0, 0, 50, 50)
            .Brush(Red).Select(2)
            .Polygon((60, 10), (90, 10), (90, 40))
            .ToArray();
        var (bitmap, _) = Render(wmf, 96);
        using (bitmap)
        {
            Assert.Equal((96, 48), (bitmap.Width, bitmap.Height));
            Assert.Equal(SKColors.Blue, Pixel(bitmap, 24, 24));
            Assert.Equal(SKColors.Red, Pixel(bitmap, 84, 12));
            Assert.Equal(0, Pixel(bitmap, 64, 36).Alpha);
        }
    }

    [Fact]
    public void Emf_plus_dual_draws_its_emf_records_and_emf_plus_only_is_refused()
    {
        var dual = new EmfBuilder(96, 96).EmfPlusHeader(dual: true).Select(EmfBuilder.NullPen).Brush(1, Red).Select(1).Rectangle(0, 0, 96, 96).ToArray();
        var only = new EmfBuilder(96, 96).EmfPlusHeader(dual: false).ToArray();

        var (bitmap, _) = Render(dual, 96);
        using (bitmap)
            Assert.Equal(SKColors.Red, Pixel(bitmap, 48, 48));
        var error = Assert.Throws<NotSupportedException>(() => Draw(only));
        Assert.Contains("EMF+", error.Message);
    }

    [Theory]
    [InlineData("emf")]
    [InlineData("wmf")]
    public async Task Metafiles_convert_to_png_and_vector_pdf_on_every_platform(string format)
    {
        var dir = fx.NewFolder();
        var path = Path.Combine(dir, "clip." + format);
        File.WriteAllBytes(path, format == "emf"
            ? Shapes()
            : new WmfBuilder(192, 96).Pen(5, 0, 0).Select(0).Brush(Red).Select(1).Rectangle(8, 8, 88, 88).ToArray());

        var png = await fx.ConvertAsync([path], new Preset { TargetFormat = "png", Pdf = { RenderDpi = 192 } });
        var pdf = await fx.ConvertAsync([path], new Preset { TargetFormat = "pdf" });

        Assert.True(png.State == JobState.Completed, string.Join("; ", png.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        Assert.True(pdf.State == JobState.Completed, string.Join("; ", pdf.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        using (var image = SKBitmap.Decode(png.Outputs.Single()))
        {
            Assert.Equal((384, 192), (image.Width, image.Height)); // 2 × 1 inch at 192 dpi
            Assert.Equal(SKColors.Red, image.GetPixel(96, 96));
        }
        Assert.Equal("vector", fx.Catalog.CreatePlanner().Plan(format, "pdf")!.Steps.Single().Converter.Id);
        using var document = PdfReader.Open(pdf.Outputs.Single(), PdfDocumentOpenMode.Import);
        Assert.Equal(144, document.Pages[0].Width.Point, 0.6);
        Assert.Equal(72, document.Pages[0].Height.Point, 0.6);
        AssertVector(document.Pages[0]);
    }

    [Fact]
    public void Layout_engine_pages_draw_metafile_pictures_as_vector_drawing()
    {
        var path = Path.Combine(fx.NewFolder(), "clip.emf");
        File.WriteAllBytes(path, Shapes());
        var page = new PageContent(200, 100);
        FloatingObjects.Draw(page, new HImage(path), new SKRect(0, 0, 200, 100), (_, _) => []);

        using (var picture = page.Render(72))
        {
            Assert.Equal(SKColors.Red, picture.GetPixel(50, 50));
            Assert.Equal(SKColors.Lime, picture.GetPixel(150, 50));
        }
        using var stream = new MemoryStream();
        PdfPageWriter.Write(stream, [page], cancellationToken: TestContext.Current.CancellationToken);
        stream.Position = 0;
        using var document = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
        AssertVector(document.Pages[0]);
        // The picture's natural size is its frame: 192 × 96 px at 96 dpi = 144 × 72 pt.
        Assert.Equal((144f, 72f), ParagraphText.PictureSize(new HImage(path)));
    }

    /// <summary>The page draws with paths and holds no image.</summary>
    private static void AssertVector(PdfPage page)
    {
        var images = page.Resources.Elements.GetDictionary("/XObject")?.Elements.Values
            .Select(v => (v as PdfReference)?.Value as PdfDictionary)
            .Count(x => x?.Elements.GetName("/Subtype") == "/Image") ?? 0;
        Assert.Equal(0, images);
        var content = System.Text.Encoding.ASCII.GetString(page.Contents.CreateSingleContent().Stream.UnfilteredValue);
        Assert.Matches(@"\s(re|c|l)\s", content);
        Assert.Matches(@"\s(f|f\*)\s", content);
    }

    private static int Ink(SKBitmap bitmap, SKRectI area)
    {
        var count = 0;
        for (var y = area.Top; y < Math.Min(area.Bottom, bitmap.Height); y++)
        {
            for (var x = area.Left; x < Math.Min(area.Right, bitmap.Width); x++)
            {
                if (bitmap.GetPixel(x, y).Alpha > 128)
                    count++;
            }
        }
        return count;
    }

    // ───────────────────────── Hostile files ─────────────────────────

    [Fact]
    public void A_file_cut_off_inside_a_record_is_damaged()
    {
        var emf = Shapes();
        var error = Assert.Throws<InvalidDataException>(() => Draw(emf[..(emf.Length - 30)]));
        Assert.Contains("damaged", error.Message);
    }

    [Theory]
    [InlineData(0)] // a record of no size would never move on
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public void Record_sizes_outside_the_file_are_damaged(int size)
    {
        var emf = new EmfBuilder(96, 96).Raw(43, new byte[16], size).ToArray();
        var error = Assert.Throws<InvalidDataException>(() => Draw(emf));
        Assert.Contains("record 1", error.Message);
    }

    [Fact]
    public void Counts_larger_than_their_record_are_damaged()
    {
        // A polygon claiming two billion points in a 40-byte record.
        var emf = new EmfBuilder(96, 96).Add(3, 0, 0, 10, 10, int.MaxValue, 1, 1).ToArray();
        Assert.Throws<InvalidDataException>(() => Draw(emf));
        // A WMF polyline claiming 30,000 points.
        var wmf = new WmfBuilder(96, 96).Add(0x0325, 30_000, 1, 1).ToArray();
        Assert.Throws<InvalidDataException>(() => Draw(wmf));
    }

    [Theory]
    [InlineData(37, 1000)] // EMR_SELECTOBJECT of an entry past the table
    [InlineData(39, 0)] // EMR_CREATEBRUSHINDIRECT into the reserved entry 0
    [InlineData(39, 70_000)]
    public void Object_numbers_outside_the_table_are_damaged(int type, int index)
    {
        var emf = new EmfBuilder(96, 96, handles: 4).Add(type, index, 0, 0, 0).ToArray();
        var error = Assert.Throws<InvalidDataException>(() => Draw(emf));
        Assert.Contains("object", error.Message);
        var wmf = new WmfBuilder(96, 96, objects: 2).Add(0x012D, 500).ToArray();
        Assert.Throws<InvalidDataException>(() => Draw(wmf));
    }

    [Fact]
    public void Limits_refuse_files_past_them()
    {
        var many = new EmfBuilder(96, 96);
        for (var i = 0; i < 50; i++)
            many.Add(33); // SAVEDC
        var emf = many.ToArray();
        Assert.Throws<ConversionRefusedException>(() => Draw(emf, limits: MetafileLimits.Default with { MaxRecords = 20 }));
        Assert.Throws<ConversionRefusedException>(() => Draw(emf, limits: MetafileLimits.Default with { MaxSavedStates = 10 }));

        // A bitmap header claiming 100,000 × 100,000 pixels in a few bytes: refused before anything is allocated.
        var info = EmfBuilder.Dib(100_000, 100_000, 24);
        var payload = new byte[72 + info.Length]; // the record from byte 8: offsets in it are 8 more
        BitConverter.GetBytes(80).CopyTo(payload, 40); // offBmiSrc
        BitConverter.GetBytes(info.Length).CopyTo(payload, 44); // cbBmiSrc
        BitConverter.GetBytes(80 + info.Length).CopyTo(payload, 48); // offBitsSrc
        BitConverter.GetBytes(0x00CC0020).CopyTo(payload, 60); // SRCCOPY
        info.CopyTo(payload, 72);
        var huge = new EmfBuilder(96, 96).Raw(81, payload).ToArray();
        var watch = Stopwatch.StartNew();
        Assert.Throws<ConversionRefusedException>(() => Draw(huge));
        Assert.True(watch.ElapsedMilliseconds < 2000);
    }

    [Fact]
    public void Wmf_records_shorter_than_a_record_are_damaged()
    {
        var wmf = new WmfBuilder(96, 96).Raw(0x041B, new byte[8], words: 2).ToArray();
        Assert.Throws<InvalidDataException>(() => Draw(wmf));
        Assert.Throws<InvalidDataException>(() => Draw([1, 2, 3, 4]));
    }

    [Fact]
    public void Random_damage_stops_with_a_clean_error()
    {
        var sources = new[]
        {
            Shapes(),
            new WmfBuilder(192, 96).Pen(1, 3, Red).Select(0).Brush(Green).Select(1).Rectangle(8, 8, 88, 88).Polygon((1, 1), (50, 9), (9, 50)).ToArray(),
        };
        var random = new Random(4321);
        var watch = Stopwatch.StartNew();
        foreach (var source in sources)
        {
            for (var round = 0; round < 300; round++)
            {
                var data = (byte[])source.Clone();
                for (var i = 0; i < 1 + random.Next(6); i++)
                    data[random.Next(data.Length)] = (byte)random.Next(256);
                try
                {
                    // Recorded, and drawn when the damage left it a sensible size (a damaged frame can claim metres).
                    using var drawing = Draw(data, 1);
                    if (drawing.Width * drawing.Height < 1_000_000)
                        Metafile.Rasterize(drawing, 1).Dispose();
                }
                catch (Exception ex) when (ex is InvalidDataException or ConversionRefusedException or NotSupportedException)
                {
                    // a clean refusal
                }
            }
        }
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"600 damaged files took {watch.Elapsed}");
    }
}
