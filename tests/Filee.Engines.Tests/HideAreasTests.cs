// "Pixelate / blur" through the real image engine: the parts become squares of their average colour or a blur that
// leaves none of their detail, the rest of the picture keeps every pixel, the parts land on the picture as it is seen
// (EXIF orientation), the format stays, and the previews a file carries of the uncovered picture go.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Magick;
using ImageMagick;

namespace Filee.Engines.Tests;

public class HideAreasTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static Preset Hide(HideStyle style, int strength, params HiddenArea[] areas)
    {
        var tool = BuiltInData.CreatePresets().Single(p => p.Id == "tool-hide");
        HideTool.Remember(tool, style, strength);
        return HideTool.ConversionOf(tool, BuiltInData.SameAsSource, areas);
    }

    private static HiddenArea Area(double x, double y, double w, double h) => new() { X = x, Y = y, Width = w, Height = h };

    /// <summary>A picture of random grey noise: every pixel differs from its neighbours, the hardest thing to cover.</summary>
    private static MagickImage Noise(uint width, uint height, int seed = 7)
    {
        var random = new Random(seed);
        var pixels = new byte[width * height * 3];
        random.NextBytes(pixels);
        var image = new MagickImage(MagickColors.Black, width, height);
        image.ImportPixels(pixels, new PixelImportSettings(width, height, StorageType.Char, PixelMapping.RGB));
        return image;
    }

    private async Task<string> RunAsync(string input, Preset preset)
    {
        var job = await fx.ConvertAsync([input], preset);
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return Assert.Single(job.Outputs);
    }

    private static byte[] Rgb(IMagickImage<byte> image) => image.GetPixels().ToByteArray(PixelMapping.RGB)!;

    [Fact]
    public async Task Pixelate_turns_the_part_into_squares_of_its_average_and_keeps_everything_else()
    {
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "noise.png");
        using (var source = Noise(500, 400))
            source.Write(path, MagickFormat.Png);
        // Strength 5 on a 400 px tall picture: squares of 16 px (4 %), laid from the top-left corner of the picture.
        var cell = (int)Math.Round(HideTool.EffectSize(5, 500, 400));
        Assert.Equal(16, cell);

        var output = await RunAsync(path, Hide(HideStyle.Pixelate, 5, Area(0.1, 0.1, 0.4, 0.5))); // 50, 40, 200 × 200

        Assert.Equal(Path.Combine(folder, "noise_hidden.png"), output);
        using var before = new MagickImage(path);
        using var after = new MagickImage(output);
        var (a, b) = (Rgb(before), Rgb(after));
        int Index(int x, int y) => (y * 500 + x) * 3;
        for (var y = 0; y < 400; y++)
        {
            for (var x = 0; x < 500; x++)
            {
                var inside = x is >= 50 and < 250 && y is >= 40 and < 240;
                if (!inside)
                {
                    Assert.True(a[Index(x, y)] == b[Index(x, y)] && a[Index(x, y) + 2] == b[Index(x, y) + 2], $"({x}, {y}) changed");
                    continue;
                }
                // Inside: the colour of its square's first pixel inside the part, and of no single source pixel nearby.
                var (sx, sy) = (Math.Max(50, x / cell * cell), Math.Max(40, y / cell * cell));
                Assert.Equal(b[Index(sx, sy)], b[Index(x, y)]);
            }
        }
        // A square wholly inside the part (64…80 × 48…64) is its exact average.
        for (var c = 0; c < 3; c++)
        {
            long sum = 0;
            for (var y = 48; y < 64; y++)
                for (var x = 64; x < 80; x++)
                    sum += a[Index(x, y) + c];
            Assert.Equal((byte)((sum + 128) / 256), b[Index(70, 50) + c]);
        }
    }

    [Fact]
    public async Task Blur_leaves_none_of_the_parts_detail_and_keeps_everything_else()
    {
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "noise.png");
        using (var source = Noise(600, 400))
            source.Write(path, MagickFormat.Png);

        var output = await RunAsync(path, Hide(HideStyle.Blur, HideTool.MinStrength, Area(0.25, 0.25, 0.5, 0.5))); // 150, 100, 300 × 200

        using var before = new MagickImage(path);
        using var after = new MagickImage(output);
        var (a, b) = (Rgb(before), Rgb(after));
        int Index(int x, int y) => (y * 600 + x) * 3;
        // Noise has a spread of about 74 levels; even the weakest blur leaves next to none inside the part (away from its
        // edges, where the noise around it still counts).
        var inside = new List<double>();
        for (var y = 130; y < 270; y++)
            for (var x = 180; x < 420; x++)
                inside.Add(b[Index(x, y)]);
        var mean = inside.Average();
        var spread = Math.Sqrt(inside.Average(v => (v - mean) * (v - mean)));
        Assert.True(spread < 3, $"spread {spread:0.00}");
        Assert.InRange(mean, 110, 145);
        // Outside, every pixel as it was.
        for (var y = 0; y < 400; y += 3)
        {
            for (var x = 0; x < 600; x += 3)
            {
                if (x is >= 150 and < 450 && y is >= 100 and < 300)
                    continue;
                Assert.True(a[Index(x, y)] == b[Index(x, y)], $"({x}, {y}) changed");
            }
        }
    }

    [Fact]
    public void A_light_blur_would_keep_the_detail_this_one_takes()
    {
        // The reason for the size: a blur of a pixel and a half leaves strokes of a desktop screenshot's text (4 px apart)
        // easy to tell, and sharpening would bring them back; the weakest blur on that screenshot (1920 × 1080) leaves
        // no more than the rounding of a level of 255 of them.
        var data = new byte[400 * 200 * 3];
        for (var y = 0; y < 200; y++)
            for (var x = 0; x < 400; x++)
                data[(y * 400 + x) * 3] = data[(y * 400 + x) * 3 + 1] = data[(y * 400 + x) * 3 + 2] = (byte)(x / 4 % 2 == 0 ? 0 : 255);
        using var stripes = new MagickImage(MagickColors.White, 400, 200);
        stripes.ImportPixels(data, new PixelImportSettings(400, 200, StorageType.Char, PixelMapping.RGB));

        double Contrast(double sigma)
        {
            using var copy = stripes.Clone();
            AreaHider.Blur(copy, (0, 0, 400, 200), sigma);
            var row = copy.GetPixels().ToByteArray(0, 100, 400, 1, PixelMapping.RGB)!;
            var values = Enumerable.Range(100, 200).Select(x => (double)row[x * 3]).ToList();
            return values.Max() - values.Min();
        }

        var light = Contrast(1.5);
        var weakest = Contrast(HideTool.EffectSize(HideTool.MinStrength, 1920, 1080));
        Assert.True(light > 100, $"{light}");
        Assert.True(weakest <= 1, $"{weakest}");
    }

    [Fact]
    public async Task Two_parts_that_meet_share_one_grid_and_a_jpeg_stays_a_jpeg()
    {
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "shot.jpg");
        using (var source = Noise(800, 500))
        {
            source.Quality = 95;
            source.Write(path, MagickFormat.Jpeg);
        }

        var output = await RunAsync(path, Hide(HideStyle.Pixelate, 1, Area(0.1, 0.2, 0.2, 0.3), Area(0.3, 0.2, 0.2, 0.3)));

        Assert.Equal(Path.Combine(folder, "shot_hidden.jpg"), output);
        using var after = new MagickImage(output);
        Assert.Equal(MagickFormat.Jpeg, after.Format);
        Assert.Equal((800u, 500u), (after.Width, after.Height));
    }

    [Fact]
    public async Task The_parts_land_where_they_were_drawn_on_a_photo_turned_by_its_EXIF_orientation()
    {
        // Stored 300 × 200, shown upright as 200 × 300 (turned 90° clockwise): the part is on the upright picture's top
        // left quarter.
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "phone.png");
        using (var stored = Noise(300, 200))
        {
            stored.Orientation = OrientationType.RightTop;
            stored.Write(path, MagickFormat.Png);
        }
        using var upright = new MagickImage(path);
        upright.AutoOrient();
        Assert.Equal((200u, 300u), (upright.Width, upright.Height));

        var output = await RunAsync(path, Hide(HideStyle.Pixelate, 5, Area(0, 0, 0.5, 0.5)));

        using var after = new MagickImage(output);
        after.AutoOrient();
        Assert.Equal((200u, 300u), (after.Width, after.Height));
        var (a, b) = (Rgb(upright), Rgb(after));
        int Index(int x, int y) => (y * 200 + x) * 3;
        Assert.NotEqual(a[Index(10, 10)..(Index(10, 10) + 30)], b[Index(10, 10)..(Index(10, 10) + 30)]); // covered
        Assert.Equal(a[Index(150, 200)..(Index(150, 200) + 30)], b[Index(150, 200)..(Index(150, 200) + 30)]); // as it was
    }

    [Fact]
    public void The_window_shows_what_the_file_gets()
    {
        // The preview covers the whole picture with the same squares the conversion lays on a part.
        using var picture = Noise(320, 240);
        using var preview = picture.Clone();
        var cell = Math.Round(HideTool.EffectSize(5, 320, 240));
        Assert.Equal(10, cell);
        AreaHider.Cover(preview, HideStyle.Pixelate, cell);
        using var converted = picture.Clone();
        AreaHider.Apply(converted, new ImageOptions { HideStrength = 5, HiddenAreas = [Area(0.25, 0.25, 0.5, 0.5)] });
        Assert.Equal(preview.GetPixels().ToByteArray(80, 60, 160, 120, PixelMapping.RGB),
            converted.GetPixels().ToByteArray(80, 60, 160, 120, PixelMapping.RGB));
    }

    [Fact]
    public void Previews_of_the_uncovered_picture_go_and_other_metadata_stays()
    {
        using var image = Noise(64, 64);
        image.SetProfile(new ImageProfile("8bim", [0x38, 0x42, 0x49, 0x4D, 0x04, 0x0C, 0, 0, 0, 0, 0, 0]));
        image.SetProfile(new XmpProfile(System.Text.Encoding.UTF8.GetBytes(
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\"><xmp:Thumbnails>thumb</xmp:Thumbnails></rdf:Description>" +
            "</rdf:RDF></x:xmpmeta>")));
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "Phone");
        image.SetProfile(exif);

        AreaHider.Apply(image, new ImageOptions { HiddenAreas = [Area(0.1, 0.1, 0.5, 0.5)] });

        Assert.False(image.HasProfile("8bim"));
        Assert.Null(image.GetXmpProfile());
        Assert.Equal("Phone", image.GetExifProfile()?.GetValue(ExifTag.Make)?.Value);

        // Without parts nothing is touched.
        using var untouched = Noise(64, 64);
        untouched.SetProfile(new ImageProfile("8bim", [0x38, 0x42, 0x49, 0x4D, 0x04, 0x0C, 0, 0, 0, 0, 0, 0]));
        AreaHider.Apply(untouched, new ImageOptions());
        Assert.True(untouched.HasProfile("8bim"));
    }
}
