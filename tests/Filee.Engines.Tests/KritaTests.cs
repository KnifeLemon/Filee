// Krita's .kra (issue #903): read as the merged picture it keeps at its top, built here as Krita lays the file out.

using System.IO.Compression;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using ImageMagick;

namespace Filee.Engines.Tests;

public class KritaTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>
    /// A .kra as Krita writes it: the mimetype first, the document, a layer and its small preview, and the merged picture
    /// (left red, right transparent). <paramref name="merged"/> false leaves the merged picture out.
    /// </summary>
    private string Kra(bool merged = true)
    {
        var path = Path.Combine(fx.NewFolder(), "sketch.kra");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(zip, "mimetype", "application/x-krita"u8.ToArray());
        Add(zip, "maindoc.xml", "<DOC><IMAGE width=\"64\" height=\"32\"/></DOC>"u8.ToArray());
        // A layer and the preview are other pictures: only the merged one is read.
        Add(zip, "sketch/layers/layer1", Png(MagickColors.Blue, 64, 32));
        Add(zip, "preview.png", Png(MagickColors.Green, 16, 8));
        if (merged)
        {
            using var picture = new MagickImage(MagickColors.Transparent, 64, 32);
            picture.Draw(new ImageMagick.Drawing.Drawables().FillColor(MagickColors.Red).Rectangle(0, 0, 31, 31));
            Add(zip, "mergedimage.png", picture.ToByteArray(MagickFormat.Png));
        }
        return path;
    }

    private static byte[] Png(IMagickColor<byte> colour, uint width, uint height)
    {
        using var image = new MagickImage(colour, width, height);
        return image.ToByteArray(MagickFormat.Png);
    }

    private static void Add(ZipArchive zip, string name, byte[] data)
    {
        using var entry = zip.CreateEntry(name).Open();
        entry.Write(data);
    }

    [Fact]
    public async Task A_kra_converts_from_its_merged_picture()
    {
        var kra = Kra();
        Assert.Equal("kra", FormatRegistry.Detect(kra)!.Id);

        var job = await fx.ConvertAsync([kra], new Preset { TargetFormat = "png" });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey}: {f.ErrorDetail}")));
        using var png = new MagickImage(Assert.Single(job.Outputs));
        Assert.Equal((64u, 32u), (png.Width, png.Height));
        using var pixels = png.GetPixels();
        Assert.Equal(MagickColors.Red, pixels.GetPixel(8, 16)!.ToColor());
        Assert.Equal(0, pixels.GetPixel(48, 16)!.ToColor()!.A); // transparent where Krita's canvas is
    }

    [Theory]
    [InlineData("jpg")]
    [InlineData("pdf")]
    public async Task A_kra_reaches_the_targets_the_issue_asked_for(string target)
    {
        var job = await fx.ConvertAsync([Kra()], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey}: {f.ErrorDetail}")));
        Assert.EndsWith("." + target, Assert.Single(job.Outputs));
    }

    [Fact]
    public async Task A_kra_without_its_merged_picture_says_so()
    {
        var job = await fx.ConvertAsync([Kra(merged: false)], new Preset { TargetFormat = "png" });
        var file = Assert.Single(job.Files);
        Assert.Equal(FileState.Failed, file.State);
        Assert.Contains("mergedimage.png", file.ErrorDetail);
    }
}
