// GIMP's .xcf: merged as GIMP shows it for PNG, JPG and the other picture targets (every precision and compression GIMP
// writes), and kept as layers (names, opacity, visibility, offsets, blend mode) between XCF, PSD and Paint.NET, groups
// given as their layers or merged where that would change the picture. The other way, pictures become an .xcf of one
// layer, and Paint.NET and PSD files an .xcf with their layers. The test files are built by XcfBuilder; the expected
// colours are GIMP 2.10's own.

using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using ImageMagick;

namespace Filee.Engines.Tests;

public class XcfTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const int Width = 16, Height = 8;

    /// <summary>
    /// An orange background (R 200, G 100, B 50), a light blue layer over its left half in <paramref name="mode"/> at
    /// half opacity and a hidden red layer, saved as <paramref name="name"/>.
    /// </summary>
    private string Xcf(string name = "drawing.xcf", int mode = 3, int version = 12,
        XcfBuilder.Precision precision = XcfBuilder.Precision.Gamma8,
        XcfBuilder.Compression compression = XcfBuilder.Compression.Rle)
    {
        var bytes = XcfBuilder.Build(Width, Height,
        [
            new("Hidden", Width, Height, XcfBuilder.Fill(Width, Height, 255, 0, 0), Visible: false),
            new("Calque 2", Width / 2, Height, XcfBuilder.Fill(Width / 2, Height, 100, 200, 255), Opacity: 0.5f, Mode: mode),
            new("Arrière-plan", Width, Height, XcfBuilder.Fill(Width, Height, 200, 100, 50)),
        ], version, precision, compression);
        var path = Path.Combine(fx.NewFolder(), name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>
    /// Groups: "Décor", pass-through, holds a sky and a sun drawn in Screen; "Ombres", in Multiply, holds a spot. Under
    /// them, the background.
    /// </summary>
    private string Grouped()
    {
        var bytes = XcfBuilder.Build(Width, Height,
        [
            XcfBuilder.Group("Ombres", [new("Tache", 4, 4, XcfBuilder.Fill(4, 4, 60, 60, 60), X: 10, Y: 2)], mode: 30),
            XcfBuilder.Group("Décor",
            [
                new("Soleil", 4, 4, XcfBuilder.Fill(4, 4, 250, 220, 0), X: 2, Y: 2, Mode: 4),
                new("Ciel", Width, 4, XcfBuilder.Fill(Width, 4, 40, 120, 220)),
            ], mode: 61),
            new("Fond", Width, Height, XcfBuilder.Fill(Width, Height, 200, 100, 50)),
        ]);
        var path = Path.Combine(fx.NewFolder(), "grouped.xcf");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string Errors(ConversionJob job) =>
        string.Join("; ", job.Files.Select(f => $"{f.ErrorKey}: {f.ErrorDetail}"));

    private static void AssertColour(IMagickImage<byte> image, int x, int y, int r, int g, int b, int a = 255)
    {
        using var pixels = image.GetPixels();
        var colour = pixels.GetPixel(x, y)!.ToColor()!;
        Assert.InRange(colour.R, r - 1, r + 1);
        Assert.InRange(colour.G, g - 1, g + 1);
        Assert.InRange(colour.B, b - 1, b + 1);
        Assert.InRange(colour.A, a - 1, a + 1);
    }

    private async Task<string> ConvertAsync(string input, string target)
    {
        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, Errors(job));
        return Assert.Single(job.Outputs);
    }

    [Theory]
    // Multiply (legacy) blends and mixes in perceptual RGB: halfway between the background and background × layer.
    [InlineData(3, 139, 89, 50)]
    // GIMP 2.10's Multiply works in linear light, which gives other colours.
    [InlineData(30, 155, 89, 50)]
    public async Task A_gimp_file_converts_to_png_as_gimp_shows_it(int mode, int r, int g, int b)
    {
        var xcf = Xcf(mode: mode);
        Assert.Equal("xcf", FormatRegistry.Detect(xcf)!.Id);

        var output = await ConvertAsync(xcf, "png");
        Assert.Equal("drawing.png", Path.GetFileName(output));
        using var png = new MagickImage(output);
        Assert.Equal((16u, 8u), (png.Width, png.Height));
        AssertColour(png, 2, 4, r, g, b);
        // The background alone on the right; the hidden red layer is not drawn.
        AssertColour(png, 12, 4, 200, 100, 50);
    }

    [Theory]
    // Version, precision (GIMP's numbers: 150 8-bit, 200 16-bit linear, 600 float linear), compression (0 none, 1 RLE,
    // 2 zlib). ImageMagick reads none of the zlib or high-precision files.
    [InlineData(0, 150, 1)]
    [InlineData(10, 150, 0)]
    [InlineData(11, 150, 2)]
    [InlineData(12, 200, 2)]
    [InlineData(12, 600, 1)]
    public async Task Every_version_precision_and_compression_gimp_writes_is_read(int version, int precision, int compression)
    {
        var output = await ConvertAsync(
            Xcf(version: version, precision: (XcfBuilder.Precision)precision, compression: (XcfBuilder.Compression)compression), "png");
        using var png = new MagickImage(output);
        AssertColour(png, 2, 4, 139, 89, 50);
        AssertColour(png, 12, 4, 200, 100, 50);
    }

    [Fact]
    public async Task Groups_and_layer_masks_are_merged_as_gimp_shows_them()
    {
        // A layer whose mask hides its right half, inside a Normal group at 50 %.
        var mask = new byte[Width * Height];
        for (var y = 0; y < Height; y++)
            Array.Fill(mask, (byte)255, y * Width, Width / 2);
        var path = Path.Combine(fx.NewFolder(), "masked.xcf");
        File.WriteAllBytes(path, XcfBuilder.Build(Width, Height,
        [
            XcfBuilder.Group("Group", [new("Masked", Width, Height, XcfBuilder.Fill(Width, Height, 0, 0, 255), Mask: mask)], opacity: 0.5f),
            new("Background", Width, Height, XcfBuilder.Fill(Width, Height, 255, 255, 255)),
        ]));

        using var png = new MagickImage(await ConvertAsync(path, "png"));
        // Normal in GIMP 2.10 mixes in linear light: half blue over white is lighter than 128.
        AssertColour(png, 2, 4, 188, 188, 255);
        AssertColour(png, 12, 4, 255, 255, 255);
    }

    [Theory]
    [InlineData("jpg")]
    [InlineData("webp")]
    [InlineData("pdf")]
    public async Task A_gimp_file_reaches_other_picture_targets(string target) =>
        Assert.EndsWith("." + target, await ConvertAsync(Xcf(), target));

    [Fact]
    public async Task A_gimp_file_as_psd_keeps_its_layers()
    {
        var output = await ConvertAsync(Xcf(), "psd");

        // The merged picture first, then one layer per GIMP layer, bottom first.
        using var psd = new MagickImageCollection(output);
        Assert.Equal(4, psd.Count);
        AssertColour(psd[0], 2, 4, 139, 89, 50);
        Assert.Equal("Calque 2", psd[2].Label);
        Assert.Equal(CompositeOperator.Multiply, psd[2].Compose);
        Assert.Equal("128", psd[2].GetArtifact("psd:layer.opacity"));
        Assert.Equal(CompositeOperator.No, psd[3].Compose); // hidden

        // The Unicode name, which Photoshop, GIMP and Krita show, and the layer at its place on the canvas.
        Assert.True(File.ReadAllBytes(output).AsSpan().IndexOf(Encoding.BigEndianUnicode.GetBytes("Arrière-plan")) > 0);
        using var pixels = psd[2].GetPixels();
        Assert.Equal(0, pixels.GetPixel(12, 4)!.ToColor()!.A);
    }

    [Fact]
    public async Task A_gimp_file_as_pdn_keeps_its_layers()
    {
        var (width, height, read) = Magick.PaintNet.Open(await ConvertAsync(Xcf(), "pdn"));
        var layers = read.ToList();
        Assert.Equal((Width, Height), (width, height));
        Assert.Equal(
            [("Arrière-plan", true, 255, Magick.PaintNet.BlendMode.Normal),
             ("Calque 2", true, 128, Magick.PaintNet.BlendMode.Multiply),
             ("Hidden", false, 255, Magick.PaintNet.BlendMode.Normal)],
            layers.Select(l => (l.Name, l.Visible, (int)l.Opacity, l.Blend)));
        // The layer at its place on the canvas: light blue on the left, transparent on the right.
        var pixel = (4 * Width + 2) * 4;
        Assert.Equal(new byte[] { 255, 200, 100, 255 }, layers[1].Bgra[pixel..(pixel + 4)]);
        Assert.Equal(0, layers[1].Bgra[(4 * Width + 12) * 4 + 3]);
    }

    [Fact]
    public async Task A_pass_through_group_draws_onto_what_is_under_it()
    {
        using var png = new MagickImage(await ConvertAsync(Grouped(), "png"));
        // The sun in Screen over the sky, the sky over the background, the spot multiplied in linear light.
        AssertColour(png, 3, 3, 251, 236, 220);
        AssertColour(png, 2, 4, 254, 234, 50);
        AssertColour(png, 12, 4, 45, 17, 5);
    }

    [Fact]
    public async Task Groups_become_their_layers_unless_that_changes_the_picture()
    {
        var pdn = await ConvertAsync(Grouped(), "pdn");
        var (_, _, read) = Magick.PaintNet.Open(pdn);
        // The pass-through group gives its layers; the Multiply group would draw differently ungrouped: one layer.
        Assert.Equal(
            [("Fond", Magick.PaintNet.BlendMode.Normal), ("Décor / Ciel", Magick.PaintNet.BlendMode.Normal),
             ("Décor / Soleil", Magick.PaintNet.BlendMode.Screen), ("Ombres", Magick.PaintNet.BlendMode.Multiply)],
            read.Select(l => (l.Name, l.Blend)));

        using var psd = new MagickImageCollection(await ConvertAsync(Grouped(), "psd"));
        Assert.Equal(5, psd.Count);
        Assert.Equal(CompositeOperator.Screen, psd[3].Compose);
    }

    [Fact]
    public async Task A_png_becomes_a_gimp_file_of_one_layer()
    {
        var folder = fx.NewFolder();
        var png = Path.Combine(folder, "photo.png");
        using (var picture = new MagickImage(MagickColors.Transparent, 100, 70))
        {
            picture.Draw(new ImageMagick.Drawing.Drawables().FillColor(MagickColors.Red).Rectangle(0, 0, 19, 19));
            picture.Write(png);
        }

        var xcf = await ConvertAsync(png, "xcf");
        Assert.Equal("photo.xcf", Path.GetFileName(xcf));
        Assert.Equal("gimp xcf file\0"u8.ToArray(), File.ReadAllBytes(xcf)[..14]);

        var read = Magick.Xcf.Open(xcf);
        var layer = Assert.Single(read.Layers);
        Assert.Equal((100, 70), (read.Width, read.Height));
        Assert.Equal(("Background", 100, 70, true), (layer.Name, layer.Width, layer.Height, layer.Visible));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, layer.Bgra![..4]); // red, BGRA
        Assert.Equal(0, layer.Bgra[(50 * 100 + 70) * 4 + 3]);           // transparent
    }

    [Fact]
    public async Task A_pdn_and_a_psd_become_gimp_files_with_their_layers()
    {
        foreach (var source in new[] { "pdn", "psd" })
        {
            var xcf = await ConvertAsync(await ConvertAsync(Xcf(), source), "xcf");
            var picture = Magick.Xcf.Open(xcf);
            // Each layer cut to its pixels, at its place; Multiply as GIMP's legacy mode, which mixes as Paint.NET and
            // Photoshop do.
            Assert.Equal(
                [("Arrière-plan", 0, 16, true, 255, 0), ("Calque 2", 0, 8, true, 128, 3), ("Hidden", 0, 16, false, 255, 0)],
                picture.Layers.Select(l => (l.Name, l.X, l.Width, l.Visible, (int)Math.Round(l.Opacity * 255), l.Mode.Id)));
            using var png = new MagickImage(await ConvertAsync(xcf, "png"));
            AssertColour(png, 2, 4, 139, 89, 50);
        }
    }

    [Theory]
    [InlineData("pdn")]
    [InlineData("psd")]
    public async Task A_gimp_file_through_pdn_or_psd_and_back_keeps_its_layers_pixel_for_pixel(string through)
    {
        // A layer of varied pixels, partly off the canvas, over the background.
        var pixels = new byte[20 * 6 * 4];
        new Random(7).NextBytes(pixels);
        for (var i = 3; i < pixels.Length; i += 4)
            pixels[i] = 255;
        var path = Path.Combine(fx.NewFolder(), "noise.xcf");
        File.WriteAllBytes(path, XcfBuilder.Build(Width, Height,
        [
            new("Bruit", 20, 6, pixels, X: -2, Y: 1, Mode: 3),
            new("Fond", Width, Height, XcfBuilder.Fill(Width, Height, 200, 100, 50)),
        ]));

        var back = Magick.Xcf.Open(await ConvertAsync(await ConvertAsync(path, through), "xcf"));
        var original = Magick.Xcf.Open(path);
        var layer = back.Layers[1];
        Assert.Equal(("Bruit", 0, 1, 16, 6), (layer.Name, layer.X, layer.Y, layer.Width, layer.Height));
        // The part on the canvas comes back exactly.
        for (var y = 0; y < 6; y++)
        {
            Assert.Equal(original.Layers[1].Bgra.AsSpan((y * 20 + 2) * 4, 16 * 4).ToArray(),
                layer.Bgra.AsSpan(y * 16 * 4, 16 * 4).ToArray());
        }
        Assert.Equal(original.Layers[0].Bgra, back.Layers[0].Bgra);
    }

    [Fact]
    public async Task Two_gimp_files_merged_make_one_gimp_file()
    {
        // "Merge images" keeps the first file's format: an .xcf, written by Filee, not ImageMagick.
        var preset = Core.Presets.BuiltInData.CreatePresets().Single(p => p.Id == "merge-images").Clone();
        var job = await fx.ConvertAsync([Xcf("first.xcf"), Xcf("second.xcf")], preset);
        Assert.True(job.State == JobState.Completed, Errors(job));
        var merged = Assert.Single(job.Outputs);
        Assert.EndsWith(".xcf", merged);
        var picture = Magick.Xcf.Open(merged);
        Assert.Single(picture.Layers);
        Assert.True(picture.Width * picture.Height >= 2 * Width * Height);
    }

    [Fact]
    public void A_gimp_file_claiming_more_pixels_than_it_holds_is_refused_before_anything_is_allocated()
    {
        var folder = fx.NewFolder();
        // Three 20,000 × 20,000 layers: more than Filee reads, whatever the file holds.
        var huge = Path.Combine(folder, "huge.xcf");
        File.WriteAllBytes(huge, XcfBuilder.Build(20_000, 20_000,
            [new("A", 20_000, 20_000, []), new("B", 20_000, 20_000, []), new("C", 20_000, 20_000, [])], tilesWritten: false));
        var ex = Assert.Throws<InvalidDataException>(() => Magick.Xcf.Open(huge));
        Assert.Contains("too large", ex.Message);

        // Within the budget, but a file of a few hundred bytes has no room to point at 3,969 tiles.
        var claimed = Path.Combine(folder, "claimed.xcf");
        File.WriteAllBytes(claimed, XcfBuilder.Build(4_000, 4_000, [new("A", 4_000, 4_000, [])], tilesWritten: false));
        Assert.Throws<InvalidDataException>(() => Magick.Xcf.Open(claimed));
    }

    [Fact]
    public void A_picture_wider_than_gimp_opens_is_scaled_to_fit_or_refused()
    {
        Assert.Equal(524_288, Magick.ImageEncoder.MaxSide("xcf"));
        var path = Path.Combine(fx.NewFolder(), "wide.xcf");
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Magick.XcfWriter.Write(path, 524_289, 1, [new Magick.XcfWriter.Layer("A", 0, 0, 1, 1, new byte[4])]));
        Assert.Contains("524,288", ex.Message);
    }

    [Fact]
    public async Task A_damaged_gimp_file_says_so()
    {
        var xcf = Xcf();
        var bytes = File.ReadAllBytes(xcf);
        File.WriteAllBytes(xcf, bytes[..(bytes.Length * 3 / 4)]);

        var job = await fx.ConvertAsync([xcf], new Preset { TargetFormat = "png" });
        var file = Assert.Single(job.Files);
        Assert.Equal(FileState.Failed, file.State);
        Assert.Contains("GIMP", file.ErrorDetail);
    }

    [Fact]
    public void Damaged_tiles_give_a_clear_error_not_an_exception_of_another_kind()
    {
        // Every byte of the tiles changed in turn: never anything but "damaged".
        var good = File.ReadAllBytes(Xcf(compression: XcfBuilder.Compression.Zlib));
        var path = Path.Combine(fx.NewFolder(), "flipped.xcf");
        for (var i = 14; i < good.Length; i += 7)
        {
            var bytes = (byte[])good.Clone();
            bytes[i] ^= 0xA5;
            File.WriteAllBytes(path, bytes);
            try
            {
                Magick.Xcf.Open(path);
            }
            catch (InvalidDataException)
            {
            }
        }
    }

    [Fact]
    public async Task A_file_that_is_not_a_gimp_file_says_so()
    {
        var path = Path.Combine(fx.NewFolder(), "fake.xcf");
        File.WriteAllText(path, "not a GIMP picture at all");

        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = "png" });
        var file = Assert.Single(job.Files);
        Assert.Equal(FileState.Failed, file.State);
        Assert.Contains("GIMP", file.ErrorDetail);
    }

    [Fact]
    public async Task Large_layers_cross_tiles_and_long_runs()
    {
        // 150 × 140: tiles of 64, 64 and 22 pixels; one flat half (runs past 127), one noisy half (literals).
        var pixels = XcfBuilder.Fill(150, 140, 10, 20, 30);
        var random = new Random(3);
        for (var i = 0; i < pixels.Length / 2; i++)
            pixels[i] = (byte)random.Next(256);
        for (var i = 3; i < pixels.Length; i += 4)
            pixels[i] = 255;
        var path = Path.Combine(fx.NewFolder(), "large.xcf");
        File.WriteAllBytes(path, XcfBuilder.Build(150, 140, [new("Large", 150, 140, pixels)]));

        // Read: the merged picture is the layer itself.
        using (var png = new MagickImage(await ConvertAsync(path, "png")))
        {
            using var read = png.GetPixels();
            Assert.Equal(pixels, read.ToByteArray(PixelMapping.RGBA));
        }

        // Written by Filee and read again: the same pixels.
        var picture = new MagickImage();
        picture.ReadPixels(pixels, new PixelReadSettings(150, 140, StorageType.Char, PixelMapping.RGBA));
        var source = Path.Combine(fx.NewFolder(), "large.png");
        picture.Write(source);
        var layer = Assert.Single(Magick.Xcf.Open(await ConvertAsync(source, "xcf")).Layers);
        for (var i = 0; i < pixels.Length; i += 4)
            Assert.Equal((pixels[i], pixels[i + 1], pixels[i + 2]), (layer.Bgra![i + 2], layer.Bgra[i + 1], layer.Bgra[i]));
    }

    // ───────────────────────── Hostile files ─────────────────────────

    [Fact(Timeout = 60_000)]
    public async Task Deeply_nested_groups_flatten_quickly()
    {
        // 60 Normal groups one inside the other: checking whether each keeps its look must not double per level.
        XcfBuilder.Layer layer = new("Leaf", 2, 2, XcfBuilder.Fill(2, 2, 10, 200, 30));
        for (var i = 0; i < 60; i++)
            layer = XcfBuilder.Group($"G{i}", [layer]);
        var path = Path.Combine(fx.NewFolder(), "nested.xcf");
        File.WriteAllBytes(path, XcfBuilder.Build(Width, Height,
            [layer, new("Fond", Width, Height, XcfBuilder.Fill(Width, Height, 200, 100, 50))]));

        var (_, _, read) = Magick.PaintNet.Open(await ConvertAsync(path, "pdn"));
        var names = read.Select(l => l.Name).ToList();
        Assert.Equal(2, names.Count);
        Assert.EndsWith("G1 / G0 / Leaf", names[1]);
    }

    [Fact]
    public void A_group_mask_counts_toward_the_budget()
    {
        // A group as wide as GIMP allows, with a mask: refused before the mask's 2 GB are asked for.
        var group = new XcfBuilder.Layer("G", 524_288, 4_095, [], Mask: [], Children: [new("A", 1, 1, [])]);
        var path = Path.Combine(fx.NewFolder(), "masked-group.xcf");
        File.WriteAllBytes(path, XcfBuilder.Build(64, 64, [group], tilesWritten: false));
        var ex = Assert.Throws<InvalidDataException>(() => Magick.Xcf.Open(path));
        Assert.Contains("too large", ex.Message);
    }

    [Fact]
    public void Merging_is_capped_and_groups_count()
    {
        // The largest canvas Filee reads, with 20 empty groups over it: little to read, hours to merge.
        var groups = Enumerable.Range(0, 20)
            .Select(i => new XcfBuilder.Layer($"G{i}", 16_384, 16_384, [], Children: [new("A", 1, 1, [])]))
            .ToList();
        var path = Path.Combine(fx.NewFolder(), "groups.xcf");
        File.WriteAllBytes(path, XcfBuilder.Build(16_384, 16_384, groups, tilesWritten: false));
        var ex = Assert.Throws<InvalidDataException>(() => Magick.Xcf.Open(path));
        Assert.Contains("too large", ex.Message);
    }

    [Fact]
    public void A_layer_listed_twice_is_damage()
    {
        var bytes = File.ReadAllBytes(Xcf());
        // XCF 12: the layer pointers (8 bytes each) start after the header (30 bytes) and the image properties (17 bytes).
        bytes.AsSpan(47, 8).CopyTo(bytes.AsSpan(55));
        var path = Path.Combine(fx.NewFolder(), "twice.xcf");
        File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() => Magick.Xcf.Open(path));
    }

    [Fact]
    public void Long_names_are_cut_to_a_kilobyte_on_a_whole_character()
    {
        var name = new string('\u00e9', 3_000); // two bytes each in UTF-8
        var path = Path.Combine(fx.NewFolder(), "long.xcf");
        File.WriteAllBytes(path, XcfBuilder.Build(4, 4, [new(name, 4, 4, XcfBuilder.Fill(4, 4, 1, 2, 3))]));
        Assert.Equal(new string('\u00e9', 512), Assert.Single(Magick.Xcf.Open(path).Layers).Name);
    }

    [Fact]
    public void Merging_can_be_cancelled()
    {
        var picture = Magick.Xcf.Open(Xcf());
        Assert.Throws<OperationCanceledException>(() =>
            Magick.Xcf.Merge(picture, picture.Layers, new CancellationToken(canceled: true)));
        // Through the picture reader too, which PDF, HEIC, ICNS, upscaling and tracing use.
        Assert.Throws<OperationCanceledException>(() =>
            Magick.ImageReader.Read(Xcf(), "xcf", new Preset(), new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task Too_many_layers_for_their_canvas_become_the_merged_picture()
    {
        // 70 one-pixel layers on 2,048 x 2,048: each on a canvas of its own would pass Paint.NET's pixel budget.
        var layers = Enumerable.Range(0, 70)
            .Select(i => new XcfBuilder.Layer($"L{i}", 1, 1, XcfBuilder.Fill(1, 1, 255, 0, 0), X: i, Y: i))
            .Append(new XcfBuilder.Layer("Fond", 2_048, 2_048, XcfBuilder.Fill(2_048, 2_048, 0, 0, 255)))
            .ToList();
        var path = Path.Combine(fx.NewFolder(), "many.xcf");
        File.WriteAllBytes(path, XcfBuilder.Build(2_048, 2_048, layers));

        var (_, _, read) = Magick.PaintNet.Open(await ConvertAsync(path, "pdn"));
        var layer = Assert.Single(read.ToList());
        Assert.Equal("Background", layer.Name);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, layer.Bgra[..4]); // the red dot at 0,0, merged over blue
        using var psd = new MagickImageCollection(await ConvertAsync(path, "psd"));
        Assert.Equal(2, psd.Count);
    }
}
