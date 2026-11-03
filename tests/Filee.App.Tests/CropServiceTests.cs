// Tests for CropService and the crop overlay: output resolving, conflict policies, quality, failures and skips, and the
// perspective crop (straightening, orientation, a plain rectangle saved without resampling).

using System.IO;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;
using ImageMagick;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Filee.App.Tests;

public class CropServiceTests
{
    private static readonly CropQuad TiltedCorners = new(
        new Point(120.0 / 800, 80.0 / 600), new Point(690.0 / 800, 140.0 / 600),
        new Point(650.0 / 800, 520.0 / 600), new Point(90.0 / 800, 470.0 / 600));

    private static string Tilted(string folder, string name, bool oriented = false)
    {
        var path = Path.Combine(folder, name);
        using var image = new MagickImage(MagickColors.Red, 400, 300);
        using (var green = new MagickImage(MagickColors.Lime, 200, 150))
            image.Composite(green, 200, 0, CompositeOperator.Over);
        using (var blue = new MagickImage(MagickColors.Blue, 200, 150))
            image.Composite(blue, 200, 150, CompositeOperator.Over);
        using (var yellow = new MagickImage(MagickColors.Yellow, 200, 150))
            image.Composite(yellow, 0, 150, CompositeOperator.Over);
        image.BackgroundColor = MagickColors.Gray;
        image.VirtualPixelMethod = VirtualPixelMethod.Background;
        image.Distort(new DistortSettings(DistortMethod.Perspective) { Viewport = new MagickGeometry(0, 0, 800, 600) },
            [0, 0, 120, 80, 400, 0, 690, 140, 400, 300, 650, 520, 0, 300, 90, 470]);
        image.ResetPage();
        if (oriented)
        {
            image.Rotate(-90);
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Orientation, (ushort)6);
            image.SetProfile(exif);
            image.Orientation = OrientationType.RightTop;
            image.Quality = 95;
        }
        image.Write(path);
        return path;
    }

    private static void AssertColour(MagickImage image, int x, int y, IMagickColor<byte> expected)
    {
        using var pixels = image.GetPixels();
        var actual = pixels.GetPixel(x, y).ToColor()!;
        Assert.InRange(Math.Abs(actual.R - expected.R), 0, 30);
        Assert.InRange(Math.Abs(actual.G - expected.G), 0, 30);
        Assert.InRange(Math.Abs(actual.B - expected.B), 0, 30);
    }

    private static void AssertQuadrants(MagickImage image)
    {
        Assert.Equal(573u, image.Width);
        Assert.Equal(391u, image.Height);
        foreach (var (x, y, colour) in new[]
        {
            (0.1, 0.1, MagickColors.Red), (0.9, 0.1, MagickColors.Lime),
            (0.9, 0.9, MagickColors.Blue), (0.1, 0.9, MagickColors.Yellow),
            (0.45, 0.45, MagickColors.Red), (0.55, 0.45, MagickColors.Lime),
            (0.55, 0.55, MagickColors.Blue), (0.45, 0.55, MagickColors.Yellow),
        })
            AssertColour(image, (int)(x * (image.Width - 1)), (int)(y * (image.Height - 1)), colour);
    }

    [AvaloniaFact]
    public async Task A_perspective_crop_straightens_a_tilted_picture()
    {
        TestServices.EnsureInitialized("en");
        var folder = Directory.CreateTempSubdirectory("filee-perspective-").FullName;
        try
        {
            var source = Tilted(folder, "tilted.png");
            var preset = BuiltInData.CreatePresets().Single(p => p.Id == "tool-crop");
            var result = await CropService.CropFileAsync(source, 0, 0, 800, 600, preset,
                AppHost.Get<UserDataStore>(), AppHost.Get<ILocalizer>(), corners: TiltedCorners);
            Assert.Equal(CropStatus.Success, result.Status);
            Assert.EndsWith("tilted_cropped.png", result.OutputPath);
            using var image = new MagickImage(result.OutputPath!);
            Assert.Equal(MagickFormat.Png, image.Format);
            AssertQuadrants(image);
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task A_perspective_crop_of_a_plain_rectangle_is_not_resampled()
    {
        TestServices.EnsureInitialized("en");
        var folder = Directory.CreateTempSubdirectory("filee-perspective-rectangle-").FullName;
        try
        {
            var preset = BuiltInData.CreatePresets().Single(p => p.Id == "tool-crop");
            var plain = Tilted(Directory.CreateDirectory(Path.Combine(folder, "plain")).FullName, "tilted.png");
            var quad = Tilted(Directory.CreateDirectory(Path.Combine(folder, "quad")).FullName, "tilted.png");
            var rectangle = new CropQuad(new Point(100.0 / 800, 60.0 / 600), new Point(500.0 / 800, 60.0 / 600),
                new Point(500.0 / 800, 420.0 / 600), new Point(100.0 / 800, 420.0 / 600));
            var expected = await CropService.CropFileAsync(plain, 100, 60, 400, 360, preset,
                AppHost.Get<UserDataStore>(), AppHost.Get<ILocalizer>());
            var actual = await CropService.CropFileAsync(quad, 0, 0, 800, 600, preset,
                AppHost.Get<UserDataStore>(), AppHost.Get<ILocalizer>(), corners: rectangle);
            Assert.Equal(CropStatus.Success, expected.Status);
            Assert.Equal(CropStatus.Success, actual.Status);
            using var want = new MagickImage(expected.OutputPath!);
            using var got = new MagickImage(actual.OutputPath!);
            Assert.Equal(400u, got.Width);
            Assert.Equal(360u, got.Height);
            Assert.Equal(0, got.Compare(want, ErrorMetric.Absolute));
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task A_perspective_crop_follows_the_pictures_orientation()
    {
        TestServices.EnsureInitialized("en");
        var folder = Directory.CreateTempSubdirectory("filee-perspective-orientation-").FullName;
        try
        {
            var source = Tilted(folder, "tilted.jpg", oriented: true);
            var preview = await CropService.LoadPreviewAsync(source);
            using (preview.Bitmap)
            {
                Assert.Equal(800, preview.Width);
                Assert.Equal(600, preview.Height);
            }
            using (var oriented = new MagickImage(source))
            {
                Assert.Equal(600u, oriented.Width);
                Assert.Equal(800u, oriented.Height);
                oriented.AutoOrient();
                AssertColour(oriented, 160, 120, MagickColors.Red);
            }
            var preset = BuiltInData.CreatePresets().Single(p => p.Id == "tool-crop");
            var result = await CropService.CropFileAsync(source, 0, 0, 800, 600, preset,
                AppHost.Get<UserDataStore>(), AppHost.Get<ILocalizer>(), corners: TiltedCorners);
            Assert.Equal(CropStatus.Success, result.Status);
            using var image = new MagickImage(result.OutputPath!);
            AssertQuadrants(image);
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task A_perspective_crop_keeps_quality_dates_and_the_conflict_policy()
    {
        TestServices.EnsureInitialized("en");
        var folder = Directory.CreateTempSubdirectory("filee-perspective-quality-").FullName;
        try
        {
            var store = AppHost.Get<UserDataStore>();
            var loc = AppHost.Get<ILocalizer>();
            var preset = BuiltInData.CreatePresets().Single(p => p.Id == "tool-crop").Clone();
            preset.Output.KeepDates = true;
            var jpeg = Path.Combine(folder, "photo.jpg");
            using (var image = new MagickImage("plasma:", 120, 90))
            {
                image.Quality = 75;
                image.Write(jpeg, MagickFormat.Jpeg);
            }
            File.SetLastWriteTimeUtc(jpeg, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
            var jpegResult = await CropService.CropFileAsync(jpeg, 0, 0, 120, 90, preset, store, loc, corners: CropQuad.Full);
            Assert.Equal(CropStatus.Success, jpegResult.Status);
            using (var cropped = new MagickImage(jpegResult.OutputPath!))
                Assert.InRange(cropped.Quality, 73u, 77u);
            Assert.Equal(File.GetLastWriteTimeUtc(jpeg), File.GetLastWriteTimeUtc(jpegResult.OutputPath!));

            var webp = Path.Combine(folder, "logo.webp");
            using (var image = new MagickImage("plasma:", 120, 90))
            {
                image.Settings.SetDefine(MagickFormat.WebP, "lossless", true);
                image.Write(webp, MagickFormat.WebP);
            }
            Assert.True(IsLosslessWebp(webp));
            var webpResult = await CropService.CropFileAsync(webp, 0, 0, 120, 90, preset, store, loc, corners: CropQuad.Full);
            Assert.Equal(CropStatus.Success, webpResult.Status);
            Assert.True(IsLosslessWebp(webpResult.OutputPath!));

            var existing = File.ReadAllBytes(jpegResult.OutputPath!);
            preset.Output.Conflict = ConflictPolicy.Skip;
            var skipped = await CropService.CropFileAsync(jpeg, 0, 0, 120, 90, preset, store, loc, corners: CropQuad.Full);
            Assert.Equal(CropStatus.SkippedExists, skipped.Status);
            Assert.Equal(existing, File.ReadAllBytes(jpegResult.OutputPath!));
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    private static string CreateTestImage(string folder, string name, int width = 100, int height = 80)
    {
        var path = Path.Combine(folder, name);
        using var image = new MagickImage(MagickColors.Green, (uint)width, (uint)height);
        image.Write(path, MagickFormat.Png);
        return path;
    }

    [AvaloniaFact]
    public async Task CropFileAsync_resolves_output_path_and_crops_image()
    {
        TestServices.EnsureInitialized("en");
        var folder = Directory.CreateTempSubdirectory("filee-crop-test-").FullName;
        try
        {
            var source = CreateTestImage(folder, "photo.png", 200, 100);
            var store = AppHost.Get<UserDataStore>();
            var loc = AppHost.Get<ILocalizer>();
            var preset = BuiltInData.CreatePresets().Single(p => p.Id == "tool-crop");

            var result = await CropService.CropFileAsync(source, 10, 10, 50, 40, preset, store, loc);

            Assert.Equal(CropStatus.Success, result.Status);
            Assert.NotNull(result.OutputPath);
            Assert.True(File.Exists(result.OutputPath));
            Assert.EndsWith("photo_cropped.png", result.OutputPath);

            using var cropped = new MagickImage(result.OutputPath);
            Assert.Equal((uint)50, cropped.Width);
            Assert.Equal((uint)40, cropped.Height);
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task Pdf_preview_and_crop_keep_every_page_as_a_PDF()
    {
        TestServices.EnsureInitialized("en");
        var folder = Directory.CreateTempSubdirectory("filee-pdf-crop-test-").FullName;
        try
        {
            var source = Path.Combine(folder, "pages.pdf");
            using (var document = new PdfDocument())
            {
                foreach (var rotation in new[] { 0, 90 })
                {
                    var page = document.AddPage();
                    page.Width = XUnit.FromPoint(600);
                    page.Height = XUnit.FromPoint(800);
                    page.Rotate = rotation;
                }
                document.Save(source);
            }
            var store = AppHost.Get<UserDataStore>();
            var loc = AppHost.Get<ILocalizer>();
            var preset = BuiltInData.CreatePresets().Single(p => p.Id == "tool-pdf-crop");
            var preview = await CropService.LoadPreviewAsync(source);

            var result = await CropService.CropFileAsync(source,
                preview.Width / 10, preview.Height / 5, preview.Width / 2, preview.Height * 3 / 5,
                preset, store, loc, originalWidth: preview.Width, originalHeight: preview.Height);

            Assert.Equal(CropStatus.Success, result.Status);
            Assert.EndsWith("pages_cropped.pdf", result.OutputPath);
            using var cropped = PdfReader.Open(result.OutputPath!, PdfDocumentOpenMode.Import);
            Assert.Equal(2, cropped.PageCount);
            Assert.Equal([0, 90], cropped.Pages.Cast<PdfPage>().Select(p => p.Rotate));
            Assert.InRange(cropped.Pages[0].CropBox.Width, 299, 301);
            Assert.InRange(cropped.Pages[0].CropBox.Height, 479, 481);
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task CropFileAsync_honors_conflict_policies()
    {
        TestServices.EnsureInitialized("en");
        var folder = Directory.CreateTempSubdirectory("filee-crop-conflict-").FullName;
        try
        {
            var source = CreateTestImage(folder, "sample.png", 100, 100);
            var expectedOutput = Path.Combine(folder, "sample_cropped.png");
            File.WriteAllBytes(expectedOutput, [0x89, 0x50, 0x4E, 0x47]); // Existing file

            var store = AppHost.Get<UserDataStore>();
            var loc = AppHost.Get<ILocalizer>();

            // 1. ConflictPolicy.Rename -> generates (2)
            var renamePreset = BuiltInData.CreatePresets().Single(p => p.Id == "tool-crop").Clone();
            renamePreset.Output.Conflict = ConflictPolicy.Rename;
            var renameResult = await CropService.CropFileAsync(source, 0, 0, 50, 50, renamePreset, store, loc);

            Assert.Equal(CropStatus.Success, renameResult.Status);
            Assert.NotNull(renameResult.OutputPath);
            Assert.EndsWith("sample_cropped (2).png", renameResult.OutputPath);
            Assert.True(File.Exists(renameResult.OutputPath));

            // 2. ConflictPolicy.Skip -> returns SkippedExists and null path
            var skipPreset = BuiltInData.CreatePresets().Single(p => p.Id == "tool-crop").Clone();
            skipPreset.Output.Conflict = ConflictPolicy.Skip;
            var skipResult = await CropService.CropFileAsync(source, 0, 0, 50, 50, skipPreset, store, loc);

            Assert.Equal(CropStatus.SkippedExists, skipResult.Status);
            Assert.Null(skipResult.OutputPath);
            Assert.Equal("toast.skipped", skipResult.ErrorKey);

            // 3. ConflictPolicy.Overwrite -> overwrites existing file
            var overwritePreset = BuiltInData.CreatePresets().Single(p => p.Id == "tool-crop").Clone();
            overwritePreset.Output.Conflict = ConflictPolicy.Overwrite;
            var overwriteResult = await CropService.CropFileAsync(source, 0, 0, 30, 30, overwritePreset, store, loc);

            Assert.Equal(CropStatus.Success, overwriteResult.Status);
            Assert.Equal(expectedOutput, overwriteResult.OutputPath);
            using var overwritten = new MagickImage(expectedOutput);
            Assert.Equal((uint)30, overwritten.Width);
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task CropFileAsync_keeps_the_sources_quality()
    {
        TestServices.EnsureInitialized("en");
        var folder = Directory.CreateTempSubdirectory("filee-crop-quality-").FullName;
        try
        {
            var store = AppHost.Get<UserDataStore>();
            var loc = AppHost.Get<ILocalizer>();
            var preset = BuiltInData.CreatePresets().Single(p => p.Id == "tool-crop");

            // A JPEG saved at 75 isn't saved again at a higher quality.
            var jpeg = Path.Combine(folder, "photo.jpg");
            using (var image = new MagickImage("plasma:", 120, 90))
            {
                image.Quality = 75;
                image.Write(jpeg, MagickFormat.Jpeg);
            }
            var jpegResult = await CropService.CropFileAsync(jpeg, 10, 10, 60, 40, preset, store, loc);
            Assert.Equal(CropStatus.Success, jpegResult.Status);
            using (var cropped = new MagickImage(jpegResult.OutputPath!))
                Assert.InRange(cropped.Quality, 73u, 77u);

            // A lossless WebP (VP8L) stays lossless instead of becoming lossy (VP8).
            var webp = Path.Combine(folder, "logo.webp");
            using (var image = new MagickImage("plasma:", 120, 90))
            {
                image.Settings.SetDefine(MagickFormat.WebP, "lossless", true);
                image.Write(webp, MagickFormat.WebP);
            }
            Assert.True(IsLosslessWebp(webp));
            var webpResult = await CropService.CropFileAsync(webp, 10, 10, 60, 40, preset, store, loc);
            Assert.Equal(CropStatus.Success, webpResult.Status);
            Assert.True(IsLosslessWebp(webpResult.OutputPath!));
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    /// <summary>True when the WebP's image chunk is VP8L (lossless), not VP8 (lossy).</summary>
    private static bool IsLosslessWebp(string path)
    {
        var text = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path));
        return text.Contains("VP8L", StringComparison.Ordinal) && !text.Contains("VP8 ", StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task An_only_file_that_cant_be_cropped_keeps_the_overlay_open_with_the_reason_and_no_retry()
    {
        TestServices.EnsureInitialized("en");
        var folder = Directory.CreateTempSubdirectory("filee-crop-animated-").FullName;
        try
        {
            var store = AppHost.Get<UserDataStore>();
            var loc = AppHost.Get<ILocalizer>();
            var gif = Path.Combine(folder, "spinner.gif");
            using (var frames = new MagickImageCollection())
            {
                frames.Add(new MagickImage(MagickColors.Red, 40, 40));
                frames.Add(new MagickImage(MagickColors.Blue, 40, 40));
                frames.Write(gif, MagickFormat.Gif);
            }
            var preset = BuiltInData.CreatePresets().Single(p => p.Id == "tool-crop");

            using var vm = new CropOverlayViewModel([gif], preset, loc, store);
            var closed = false;
            vm.CloseRequested += () => closed = true;
            await WaitFor(() => vm.IsFinished);

            Assert.False(closed);
            Assert.Equal(["spinner.gif: " + loc["error.unsupported_source"]], vm.Problems);
            var entry = store.History.First(h => h.Sources.Contains(gif));
            Assert.Equal(JobState.Failed, entry.State);
            var failure = Assert.Single(entry.Failures);
            Assert.Equal("error.unsupported_source", failure.ErrorKey);
            Assert.Null(failure.ErrorDetail); // the English exception text stays in the log
            Assert.Null(entry.Preset); // Home offers no Retry: ConversionService doesn't run tools

            vm.CancelCommand.Execute(null);
            Assert.True(closed);
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task A_file_skipped_because_the_output_exists_is_shown_but_isnt_a_failure()
    {
        TestServices.EnsureInitialized("en");
        var folder = Directory.CreateTempSubdirectory("filee-crop-skip-").FullName;
        try
        {
            var store = AppHost.Get<UserDataStore>();
            var loc = AppHost.Get<ILocalizer>();
            var source = CreateTestImage(folder, "scan.png");
            File.WriteAllBytes(Path.Combine(folder, "scan_cropped.png"), [0x89, 0x50, 0x4E, 0x47]);
            var preset = BuiltInData.CreatePresets().Single(p => p.Id == "tool-crop");
            preset.Output.Conflict = ConflictPolicy.Skip;

            using var vm = new CropOverlayViewModel([source], preset, loc, store);
            await WaitFor(() => !vm.IsBusy && vm.OriginalWidth > 0);
            await vm.CropAndSaveAsync();

            Assert.True(vm.IsFinished);
            Assert.Equal(["scan.png: " + loc["toast.skipped"]], vm.Problems);
            Assert.Equal(loc["toast.done"], vm.Summary);
            var entry = store.History.First(h => h.Sources.Contains(source));
            Assert.Equal(JobState.Completed, entry.State);
            Assert.Empty(entry.Failures);
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    /// <summary>An uncompressed DDS stored R, G, B, A (masks 0xFF … 0xFF000000), which ImageMagick reads with red and blue swapped.</summary>
    private static string WriteRgbaDds(string folder, string name, uint width, uint flags, byte[] pixels)
    {
        var data = new byte[128 + pixels.Length];
        var header = data.AsSpan();
        "DDS "u8.CopyTo(header);
        foreach (var (offset, value) in new (int, uint)[]
                 {
                     (4, 124), (8, 0x100F), (12, 4), (16, width), (20, width * 4), (76, 32), (80, flags), (88, 32),
                     (92, 0xFF), (96, 0xFF00), (100, 0xFF0000), (104, 0xFF000000), (108, 0x1000),
                 })
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header[offset..], value);
        pixels.CopyTo(data, 128);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static byte[] PreviewPixel(Avalonia.Media.Imaging.Bitmap bitmap, int x, int y)
    {
        var buffer = new byte[4];
        var native = System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
        try
        {
            bitmap.CopyPixels(new Avalonia.PixelRect(x, y, 1, 1), native, 4, 4);
            System.Runtime.InteropServices.Marshal.Copy(native, buffer, 0, 4);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(native);
        }
        return bitmap.Format == Avalonia.Platform.PixelFormat.Rgba8888 ? buffer : [buffer[2], buffer[1], buffer[0], buffer[3]];
    }

    /// <summary>#621: the crop tool shows and saves a DDS stored R, G, B, A in its own colours.</summary>
    [AvaloniaTheory]
    [InlineData(0x41u)]
    [InlineData(0x40u)]
    public async Task Dds_stored_rgba_is_previewed_and_cropped_in_its_own_colours(uint flags)
    {
        TestServices.EnsureInitialized("en");
        var folder = Directory.CreateTempSubdirectory("filee-dds-crop-test-").FullName;
        try
        {
            byte[] red = [255, 0, 0, 255], green = [0, 255, 0, 255], blue = [0, 0, 255, 255];
            byte[][] colours = [red, green, blue];
            // Three 4 × 4 blocks side by side, matching DXT blocks so the saved DDS keeps each colour pure.
            var pixels = Enumerable.Range(0, 4).SelectMany(_ => colours.SelectMany(c => Enumerable.Repeat(c, 4).SelectMany(p => p))).ToArray();
            var source = WriteRgbaDds(folder, "rgba.dds", 12, flags, pixels);
            var store = AppHost.Get<UserDataStore>();
            var loc = AppHost.Get<ILocalizer>();
            var preset = BuiltInData.CreatePresets().Single(p => p.Id == "tool-crop");

            var preview = await CropService.LoadPreviewAsync(source);
            using (preview.Bitmap)
            {
                for (var block = 0; block < 3; block++)
                    Assert.Equal(colours[block], PreviewPixel(preview.Bitmap, block * 4 + 1, 1));
            }

            for (var block = 0; block < 3; block++)
            {
                var result = await CropService.CropFileAsync(source, block * 4, 0, 4, 4, preset, store, loc);

                Assert.Equal(CropStatus.Success, result.Status);
                using (var cropped = new MagickImage(result.OutputPath!, new MagickReadSettings { Format = MagickFormat.Dds }))
                {
                    var rgb = cropped.GetPixels().ToByteArray(PixelMapping.RGB)!;
                    Assert.All(rgb.Chunk(3), p => Assert.All(Enumerable.Range(0, 3).Where(c => c != block),
                        c => Assert.True(p[block] - p[c] >= 128, $"block {block}: {string.Join(',', p)}")));
                }
                File.Delete(result.OutputPath!);
            }
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Assert.True(condition());
    }
}
