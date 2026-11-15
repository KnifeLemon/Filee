// Paint.NET's .pdn: merged as Paint.NET shows it for PNG, JPG and the other picture targets, and kept as layers (names,
// opacity, blend mode, visibility) for PSD, which Photoshop, GIMP and Krita open. The other way, pictures become a .pdn
// of one layer and a PSD a .pdn with its layers. The test files are built by PdnBuilder.

using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using ImageMagick;

namespace Filee.Engines.Tests;

public class PaintNetTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const int Width = 16, Height = 8;

    /// <summary>
    /// An orange background (R 200, G 100, B 50), a light blue layer over its left half in Multiply at half opacity and a
    /// hidden red layer, saved as <paramref name="name"/>.
    /// </summary>
    private string Pdn(string name = "drawing.pdn")
    {
        var half = PdnBuilder.Fill(Width, Height, 255, 200, 100, 255);
        for (var y = 0; y < Height; y++)
            Array.Clear(half, (y * Width + Width / 2) * 4, Width / 2 * 4);
        byte[] bytes = PdnBuilder.Build(Width, Height,
        [
            new("Arrière-plan", PdnBuilder.Fill(Width, Height, 50, 100, 200, 255)),
            new("Calque 2", half, Opacity: 128, BlendMode: 1),
            new("Hidden", PdnBuilder.Fill(Width, Height, 0, 0, 255, 255), Visible: false),
        ]);
        var path = Path.Combine(fx.NewFolder(), name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string Errors(ConversionJob job) =>
        string.Join("; ", job.Files.Select(f => $"{f.ErrorKey}: {f.ErrorDetail}"));

    private static void AssertColour(IMagickImage<byte> image, int x, int y, int r, int g, int b)
    {
        using var pixels = image.GetPixels();
        var colour = pixels.GetPixel(x, y)!.ToColor()!;
        Assert.InRange(colour.R, r - 1, r + 1);
        Assert.InRange(colour.G, g - 1, g + 1);
        Assert.InRange(colour.B, b - 1, b + 1);
        Assert.Equal(255, colour.A);
    }

    [Fact]
    public async Task A_pdn_converts_to_png_as_paint_net_shows_it()
    {
        var pdn = Pdn();
        Assert.Equal("pdn", FormatRegistry.Detect(pdn)!.Id);

        var job = await fx.ConvertAsync([pdn], new Preset { TargetFormat = "png" });
        Assert.True(job.State == JobState.Completed, Errors(job));
        var output = Assert.Single(job.Outputs);
        Assert.Equal("drawing.png", Path.GetFileName(output));
        using var png = new MagickImage(output);
        Assert.Equal((16u, 8u), (png.Width, png.Height));
        // Multiply at half opacity: halfway between the background and background × layer (200 × 100 / 255 = 78, …).
        AssertColour(png, 2, 4, 139, 89, 50);
        // The background alone on the right; the hidden red layer is not drawn.
        AssertColour(png, 12, 4, 200, 100, 50);
    }

    [Theory]
    [InlineData("jpg")]
    [InlineData("webp")]
    [InlineData("pdf")]
    public async Task A_pdn_reaches_other_picture_targets(string target)
    {
        var job = await fx.ConvertAsync([Pdn()], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, Errors(job));
        Assert.EndsWith("." + target, Assert.Single(job.Outputs));
    }

    [Fact]
    public async Task A_pdn_as_psd_keeps_its_layers()
    {
        var job = await fx.ConvertAsync([Pdn()], new Preset { TargetFormat = "psd" });
        Assert.True(job.State == JobState.Completed, Errors(job));
        var output = Assert.Single(job.Outputs);

        // The merged picture first, then one layer per Paint.NET layer, bottom first.
        using var psd = new MagickImageCollection(output);
        Assert.Equal(4, psd.Count);
        AssertColour(psd[0], 2, 4, 139, 89, 50);
        Assert.Equal("Calque 2", psd[2].Label);
        Assert.Equal(CompositeOperator.Multiply, psd[2].Compose);
        Assert.Equal("Hidden", psd[3].Label);
        Assert.Equal(CompositeOperator.No, psd[3].Compose); // hidden

        // PSD's own name field holds ASCII; the Unicode name follows it, which Photoshop, GIMP and Krita show.
        var bytes = File.ReadAllBytes(output);
        Assert.True(bytes.AsSpan().IndexOf("\u000cArriere-plan"u8) > 0);
        Assert.True(bytes.AsSpan().IndexOf("8BIMluni"u8) > 0);
        Assert.True(bytes.AsSpan().IndexOf(Encoding.BigEndianUnicode.GetBytes("Arrière-plan")) > 0);

        // The layer keeps its own colour; its opacity is the layer's, not merged into the picture.
        using var pixels = psd[2].GetPixels();
        var layerColour = pixels.GetPixel(2, 4)!.ToColor()!;
        Assert.Equal((100, 200, 255), (layerColour.R, layerColour.G, layerColour.B));
        Assert.Equal("128", psd[2].GetArtifact("psd:layer.opacity"));
    }

    [Fact]
    public async Task A_pdn_resized_to_psd_is_one_merged_picture()
    {
        var preset = new Preset { TargetFormat = "psd" };
        preset.Image.Resize = ResizeMode.Percent;
        preset.Image.ResizePercent = 50;
        var job = await fx.ConvertAsync([Pdn()], preset);
        Assert.True(job.State == JobState.Completed, Errors(job));

        using var psd = new MagickImageCollection(Assert.Single(job.Outputs));
        Assert.Equal((8u, 4u), (psd[0].Width, psd[0].Height));
        Assert.True(psd.Count <= 2); // the picture, and at most ImageMagick's one layer of it
    }

    [Fact]
    public async Task A_damaged_pdn_says_so()
    {
        var pdn = Pdn();
        var bytes = File.ReadAllBytes(pdn);
        File.WriteAllBytes(pdn, bytes[..(bytes.Length / 2)]);

        var job = await fx.ConvertAsync([pdn], new Preset { TargetFormat = "png" });
        var file = Assert.Single(job.Files);
        Assert.Equal(FileState.Failed, file.State);
    }

    [Fact]
    public async Task A_file_that_is_not_a_pdn_says_so()
    {
        var path = Path.Combine(fx.NewFolder(), "fake.pdn");
        File.WriteAllText(path, "not a Paint.NET picture");

        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = "png" });
        var file = Assert.Single(job.Files);
        Assert.Equal(FileState.Failed, file.State);
        Assert.Contains("Paint.NET", file.ErrorDetail);
    }

    [Fact]
    public async Task A_png_becomes_a_pdn_of_one_layer()
    {
        var folder = fx.NewFolder();
        var png = Path.Combine(folder, "photo.png");
        using (var picture = new MagickImage(MagickColors.Transparent, 40, 20))
        {
            picture.Draw(new ImageMagick.Drawing.Drawables().FillColor(MagickColors.Red).Rectangle(0, 0, 19, 19));
            picture.Write(png);
        }

        var job = await fx.ConvertAsync([png], new Preset { TargetFormat = "pdn" });
        Assert.True(job.State == JobState.Completed, Errors(job));
        var pdn = Assert.Single(job.Outputs);
        Assert.Equal("photo.pdn", Path.GetFileName(pdn));
        Assert.Equal("PDN3"u8.ToArray(), File.ReadAllBytes(pdn)[..4]);

        var (width, height, layers) = Magick.PaintNet.Open(pdn);
        var layer = Assert.Single(layers.ToList());
        Assert.Equal((40, 20), (width, height));
        Assert.Equal(("Background", true, (byte)255), (layer.Name, layer.Visible, layer.Opacity));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, layer.Bgra[..4]);                                // red
        Assert.Equal(0, layer.Bgra[(10 * 40 + 30) * 4 + 3]);                                        // transparent
    }

    [Fact]
    public async Task A_pdn_through_psd_and_back_keeps_its_layers()
    {
        var psdJob = await fx.ConvertAsync([Pdn()], new Preset { TargetFormat = "psd" });
        Assert.True(psdJob.State == JobState.Completed, Errors(psdJob));
        var job = await fx.ConvertAsync([Assert.Single(psdJob.Outputs)], new Preset { TargetFormat = "pdn" });
        Assert.True(job.State == JobState.Completed, Errors(job));

        var (_, _, read) = Magick.PaintNet.Open(Assert.Single(job.Outputs));
        var layers = read.ToList();
        Assert.Equal(
            [("Arrière-plan", true, 255, Magick.PaintNet.BlendMode.Normal),
             ("Calque 2", true, 128, Magick.PaintNet.BlendMode.Multiply),
             ("Hidden", false, 255, Magick.PaintNet.BlendMode.Normal)],
            layers.Select(l => (l.Name, l.Visible, (int)l.Opacity, l.Blend)));
        // The layer's own pixels, without its opacity baked in: opaque light blue left, transparent right.
        var pixel = (4 * Width + 2) * 4;
        Assert.Equal(new byte[] { 255, 200, 100, 255 }, layers[1].Bgra[pixel..(pixel + 4)]);
        Assert.Equal(0, layers[1].Bgra[(4 * Width + 12) * 4 + 3]);

        // And it still converts to the same picture.
        var png = await fx.ConvertAsync([Assert.Single(job.Outputs)], new Preset { TargetFormat = "png" });
        using var merged = new MagickImage(Assert.Single(png.Outputs));
        AssertColour(merged, 2, 4, 139, 89, 50);
    }

    [Fact]
    public async Task Two_pdn_merged_make_one_pdn()
    {
        // "Merge images" keeps the first file's format: a .pdn, written by Filee, not ImageMagick.
        var first = Pdn("first.pdn");
        var second = Pdn("second.pdn");
        var preset = Core.Presets.BuiltInData.CreatePresets().Single(p => p.Id == "merge-images").Clone();

        var job = await fx.ConvertAsync([first, second], preset);
        Assert.True(job.State == JobState.Completed, Errors(job));
        var merged = Assert.Single(job.Outputs);
        Assert.EndsWith(".pdn", merged);
        var (width, height, layers) = Magick.PaintNet.Open(merged);
        Assert.Single(layers.ToList());
        Assert.True(width * height >= 2 * Width * Height);
    }

    [Fact]
    public void A_pdn_claiming_more_pixels_than_it_holds_is_refused_before_anything_is_allocated()
    {
        var folder = fx.NewFolder();
        // A 16 × 8 file whose header and serialized document claim 20,000 × 20,000 with 3 layers.
        var huge = Path.Combine(folder, "huge.pdn");
        File.WriteAllBytes(huge, PdnBuilder.Build(20_000, 20_000,
            [new("A", []), new("B", []), new("C", [])], pixelsWritten: false));
        var ex = Assert.Throws<InvalidDataException>(() => Magick.PaintNet.Open(huge));
        Assert.Contains("too large", ex.Message);

        // Within the budget but far more than a few hundred bytes can unpack to.
        var claimed = Path.Combine(folder, "claimed.pdn");
        File.WriteAllBytes(claimed, PdnBuilder.Build(4_000, 4_000, [new("A", [])], pixelsWritten: false));
        Assert.Throws<InvalidDataException>(() => Magick.PaintNet.Open(claimed));
    }

    [Fact]
    public void A_large_layer_in_tiny_chunks_is_refused()
    {
        // 256 × 256 (256 KB) in 16-byte chunks: 16,384 chunks, damage rather than a Paint.NET file.
        var path = Path.Combine(fx.NewFolder(), "chunks.pdn");
        File.WriteAllBytes(path, PdnBuilder.Build(256, 256, [new("A", PdnBuilder.Fill(256, 256, 1, 2, 3, 255))], chunkSize: 16));
        var (_, _, layers) = Magick.PaintNet.Open(path);
        Assert.Throws<InvalidDataException>(() => layers.ToList());
    }

    [Fact]
    public async Task A_picture_wider_than_paint_net_opens_is_scaled_to_fit()
    {
        var png = Path.Combine(fx.NewFolder(), "strip.png");
        using (var strip = new MagickImage(MagickColors.Orange, 70_000, 2))
            strip.Write(png);

        var job = await fx.ConvertAsync([png], new Preset { TargetFormat = "pdn" });
        Assert.True(job.State == JobState.Completed, Errors(job));
        var (width, _, _) = Magick.PaintNet.Open(Assert.Single(job.Outputs));
        Assert.True(width <= Magick.PaintNetWriter.MaxSide);
    }

    [Fact]
    public async Task A_cut_off_psd_gives_no_layer_names_instead_of_failing()
    {
        var psdJob = await fx.ConvertAsync([Pdn()], new Preset { TargetFormat = "psd" });
        var psd = Assert.Single(psdJob.Outputs);
        // Cut inside the last layer record's Unicode name: no names (PSD → PDN then keeps ImageMagick's), no exception.
        var bytes = File.ReadAllBytes(psd);
        var luni = bytes.AsSpan().LastIndexOf("8BIMluni"u8);
        using (var file = File.OpenRead(psd))
            Assert.NotNull(Magick.PsdLayerRecords.Read(file, psb: false));
        File.WriteAllBytes(psd, bytes[..(luni + 10)]);
        using (var file = File.OpenRead(psd))
            Assert.Null(Magick.PsdLayerRecords.Read(file, psb: false));
    }
}
