using System.IO;
using Filee.Core.Presets;
using Filee.Engines.Magick;
using Filee.Engines.Upscale;
using ImageMagick;
using Xunit;

namespace Filee.Engines.Tests;

public class UpscalerTests
{
    [Fact]
    public void Catalog_contains_curated_models_with_valid_metadata()
    {
        var models = UpscaleModels.All;
        Assert.NotEmpty(models);
        Assert.True(models.Count >= 9);

        foreach (var model in models)
        {
            Assert.False(string.IsNullOrWhiteSpace(model.Id));
            Assert.False(string.IsNullOrWhiteSpace(model.Name));
            Assert.True(model.Scale >= 2);
            Assert.False(string.IsNullOrWhiteSpace(model.License));
            Assert.False(string.IsNullOrWhiteSpace(model.Author));
            Assert.False(string.IsNullOrWhiteSpace(model.Description));
            Assert.True(model.ParamSize > 0);
            Assert.Equal(64, model.ParamSha256.Length);
            Assert.True(model.BinSize > 0);
            Assert.Equal(64, model.BinSha256.Length);
            Assert.EndsWith(".bin", model.BinFileName);
            Assert.EndsWith(".param", model.ParamFileName);
        }
    }

    [Fact]
    public void Catalog_contains_only_permissive_and_free_licenses()
    {
        var models = UpscaleModels.All;
        // All models must allow commercial use (no CC-BY-NC)
        Assert.All(models, m =>
        {
            Assert.Contains(m.License, new[] { "WTFPL", "BSD-3-Clause", "CC-BY-4.0" });
            Assert.DoesNotContain("NC", m.License);
        });
    }

    [Theory]
    [InlineData("png", true)]
    [InlineData("jpg", true)]
    [InlineData("jpeg", true)]
    [InlineData("webp", true)]
    [InlineData("avif", true)]
    [InlineData("heic", true)]
    [InlineData("ico", true)]
    [InlineData("bmp", true)]
    [InlineData("tiff", true)]
    [InlineData("pdf", false)]
    [InlineData("docx", false)]
    [InlineData("txt", false)]
    [InlineData("mp4", false)]
    [InlineData("xlsx", false)]
    public void CanUpscale_validates_image_formats(string format, bool expected)
    {
        Assert.Equal(expected, UpscaleTool.CanUpscale(format));
    }

    [Fact]
    public void TargetFor_handles_same_as_source_and_explicit_targets()
    {
        Assert.Equal("png", UpscaleTool.TargetFor("png", BuiltInData.SameAsSource));
        Assert.Equal("jpg", UpscaleTool.TargetFor("jpg", BuiltInData.SameAsSource));
        Assert.Equal("webp", UpscaleTool.TargetFor("png", "webp"));
        Assert.Equal("jpg", UpscaleTool.TargetFor("png", "jpg"));
    }

    [Fact]
    public void TargetFor_falls_back_to_png_for_unsupported_writable_formats()
    {
        // Unsupported writable formats fall back to PNG
        Assert.Equal("png", UpscaleTool.TargetFor("gif", BuiltInData.SameAsSource));
        Assert.Equal("png", UpscaleTool.TargetFor("psd", BuiltInData.SameAsSource));
        Assert.Equal("png", UpscaleTool.TargetFor("jxl", BuiltInData.SameAsSource));
        Assert.Equal("png", UpscaleTool.TargetFor("tga", BuiltInData.SameAsSource));
        Assert.Equal("png", UpscaleTool.TargetFor("jp2", BuiltInData.SameAsSource));

        // Supported target formats preserve their format
        Assert.Equal("png", UpscaleTool.TargetFor("png", BuiltInData.SameAsSource));
        Assert.Equal("jpg", UpscaleTool.TargetFor("jpg", BuiltInData.SameAsSource));
        Assert.Equal("webp", UpscaleTool.TargetFor("webp", BuiltInData.SameAsSource));
        Assert.Equal("avif", UpscaleTool.TargetFor("avif", BuiltInData.SameAsSource));
        Assert.Equal("heic", UpscaleTool.TargetFor("heic", BuiltInData.SameAsSource));
        Assert.Equal("bmp", UpscaleTool.TargetFor("bmp", BuiltInData.SameAsSource));
        Assert.Equal("tiff", UpscaleTool.TargetFor("tiff", BuiltInData.SameAsSource));
        Assert.Equal("ico", UpscaleTool.TargetFor("ico", BuiltInData.SameAsSource));

        // Explicitly chosen targets are respected
        Assert.Equal("webp", UpscaleTool.TargetFor("gif", "webp"));
        Assert.Equal("jpg", UpscaleTool.TargetFor("psd", "jpg"));
    }

    [Fact]
    public void AutoOrient_and_profiles_are_applied_correctly_on_portrait_image()
    {
        // Stored landscape (200x100), but marked with orientation RightTop (6) to be viewed as portrait
        var tempPath = Path.Combine(Path.GetTempPath(), $"test-orient-{Guid.NewGuid():N}.jpg");
        try
        {
            using (var img = new MagickImage(MagickColors.Blue, 200, 100))
            {
                var exif = new ExifProfile();
                exif.SetValue(ExifTag.Make, "FileePhone");
                exif.SetValue(ExifTag.Orientation, (ushort)Orientation.RightTop);
                img.SetProfile(exif);
                img.SetProfile(ColorProfiles.SRGB);
                img.Orientation = Orientation.RightTop;
                img.Write(tempPath, MagickFormat.Jpeg);
            }

            var preset = BuiltInData.UpscalePresets().Single();
            using var images = ImageReader.Read(tempPath, "jpg", preset);
            var frames = ImageEncoder.SelectFrames(images, "jpg", "png");
            Assert.NotEmpty(frames);

            var frame = frames[0];
            Assert.Equal((200u, 100u), (frame.Width, frame.Height));
            Assert.Equal(Orientation.RightTop, frame.Orientation);

            frame.AutoOrient();

            // After AutoOrient, the pixels are physically upright (100x200) and orientation tag is reset
            Assert.Equal((100u, 200u), (frame.Width, frame.Height));
            Assert.True(frame.Orientation is Orientation.TopLeft or Orientation.Undefined);

            var extractedExif = frame.GetExifProfile();
            var extractedIcc = frame.GetColorProfile();
            Assert.NotNull(extractedExif);
            Assert.Equal("FileePhone", extractedExif.GetValue(ExifTag.Make)?.Value);
            Assert.NotNull(extractedIcc);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void ConversionOf_creates_valid_upscale_preset()
    {
        var tool = BuiltInData.UpscalePresets().Single();
        var preset = UpscaleTool.ConversionOf(tool, "png", "4xLSDIRCompactC3", 4);

        Assert.Equal(PresetKind.Convert, preset.Kind);
        Assert.Null(preset.Tool);
        Assert.Equal("png", preset.TargetFormat);
        Assert.True(preset.Upscale.Enabled);
        Assert.Equal("4xLSDIRCompactC3", preset.Upscale.Model);
        Assert.Equal(4, preset.Upscale.Scale);
    }

    [Fact]
    public void UpscalerConverter_metadata_and_edges()
    {
        var converter = new UpscalerConverter();
        Assert.Equal("upscaler", converter.Id);
        Assert.Equal("Upscayl (AI image upscaler)", converter.DisplayName);
        Assert.Equal(1, converter.MaxParallelism);
        Assert.NotEmpty(converter.Edges);

        // Edges exist for common readable formats to all 8 targets
        foreach (var target in UpscaleTool.Targets)
        {
            Assert.Contains(converter.Edges, e => e.From == "png" && e.To == target && e.Upscale);
            Assert.Contains(converter.Edges, e => e.From == "jpg" && e.To == target && e.Upscale);
            Assert.Contains(converter.Edges, e => e.From == "gif" && e.To == target && e.Upscale);
            Assert.Contains(converter.Edges, e => e.From == "psd" && e.To == target && e.Upscale);
        }

        var status = converter.GetStatus();
        Assert.NotNull(status);
    }

    [Fact]
    public async Task RealUpscale_processes_image_when_binary_available()
    {
        var bin = UpscalerConverter.LocateExecutable();
        Assert.SkipUnless(bin is not null && File.Exists(bin), "upscayl-bin is not installed on this PC.");

        var installed = UpscaleModels.InstalledModels;
        Assert.SkipUnless(installed.Count > 0, "No upscale model is installed on this PC.");

        var engine = new UpscalerEngine();
        var tempIn = Path.Combine(Path.GetTempPath(), $"test-in-{Guid.NewGuid():N}.png");
        var tempOut = Path.Combine(Path.GetTempPath(), $"test-out-{Guid.NewGuid():N}.png");
        var workDir = Path.Combine(Path.GetTempPath(), $"test-work-{Guid.NewGuid():N}");

        try
        {
            using (var img = new MagickImage(MagickColors.Red, 64, 64))
            {
                img.Write(tempIn);
            }

            var progressReports = new List<double>();
            var progress = new Progress<double>(p => progressReports.Add(p));
            var tool = BuiltInData.UpscalePresets().Single();

            await engine.UpscaleAsync(
                tempIn,
                tempOut,
                "png",
                "png",
                installed[0].Id,
                2,
                workDir,
                tool,
                progress,
                TestContext.Current.CancellationToken);

            Assert.True(File.Exists(tempOut));
            using var result = new MagickImage(tempOut);
            Assert.Equal(128u, result.Width);
            Assert.Equal(128u, result.Height);
            Assert.NotEmpty(progressReports);
        }
        finally
        {
            if (File.Exists(tempIn)) File.Delete(tempIn);
            if (File.Exists(tempOut)) File.Delete(tempOut);
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }
}
