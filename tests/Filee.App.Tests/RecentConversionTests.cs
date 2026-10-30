// The Home page's recent conversions: thumbnails of the results (decoded off the UI thread, upright, without holding
// the files), the buttons that open a result, and Undo (#553).

using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.App.Views;
using Filee.Core.Conversion;
using Filee.Core.History;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Settings;
using SkiaSharp;

namespace Filee.App.Tests;

public class RecentConversionTests
{
    [AvaloniaFact]
    public void A_picture_becomes_an_upright_square_and_the_file_stays_free()
    {
        var folder = Directory.CreateTempSubdirectory("filee-thumbs-").FullName;
        try
        {
            // Wide: red on the left, blue on the right. The middle square keeps a bit of both.
            var wide = Path.Combine(folder, "wide.png");
            WritePicture(wide, 300, 150, SKEncodedImageFormat.Png);
            var square = HistoryThumbnails.Load(wide);
            Assert.NotNull(square);
            Assert.Equal(new PixelSize(HistoryThumbnails.Size, HistoryThumbnails.Size), square.PixelSize);
            AssertMostly(Pixel(square, 10, 48), red: true);
            AssertMostly(Pixel(square, 86, 48), red: false);

            // Read in one go and closed: the file can be replaced and deleted right away.
            using (new FileStream(wide, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }

            // Unchanged, it comes from the cache; changed, it is read again.
            Assert.Same(square, HistoryThumbnails.Load(wide));
            Assert.Same(square, HistoryThumbnails.Cached(wide));
            WritePicture(wide, 320, 150, SKEncodedImageFormat.Png);
            File.SetLastWriteTimeUtc(wide, DateTime.UtcNow.AddMinutes(1));
            Assert.NotSame(square, HistoryThumbnails.Load(wide));
            File.Delete(wide);
            Assert.Null(HistoryThumbnails.Load(wide));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [AvaloniaFact]
    public void A_photo_turned_by_its_exif_orientation_shows_upright()
    {
        var folder = Directory.CreateTempSubdirectory("filee-thumbs-").FullName;
        try
        {
            // Stored on its side (red left, blue right) with orientation 6: shown turned a quarter clockwise, red on top.
            var photo = Path.Combine(folder, "photo.jpg");
            WritePicture(photo, 200, 100, SKEncodedImageFormat.Jpeg, exifOrientation: 6);
            var thumbnail = HistoryThumbnails.Decode(photo, HistoryThumbnails.MaxDecodedPixels);
            Assert.NotNull(thumbnail);
            AssertMostly(Pixel(thumbnail, 48, 10), red: true);
            AssertMostly(Pixel(thumbnail, 48, 86), red: false);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [AvaloniaFact]
    public void Only_a_picture_that_fits_the_budget_is_decoded()
    {
        var folder = Directory.CreateTempSubdirectory("filee-thumbs-").FullName;
        try
        {
            // A PNG decodes whole: past the budget it shows its format instead.
            var png = Path.Combine(folder, "large.png");
            WritePicture(png, 300, 150, SKEncodedImageFormat.Png);
            Assert.Null(HistoryThumbnails.Decode(png, maxPixels: 300 * 150 - 1));
            Assert.NotNull(HistoryThumbnails.Decode(png, maxPixels: 300 * 150));

            // A JPEG decodes at an eighth of its size: 1600 × 1600 fits a budget a quarter of its pixels.
            var jpeg = Path.Combine(folder, "large.jpg");
            WritePicture(jpeg, 1600, 1600, SKEncodedImageFormat.Jpeg);
            Assert.NotNull(HistoryThumbnails.Decode(jpeg, maxPixels: 1600 * 1600 / 4));

            var broken = Path.Combine(folder, "broken.png");
            File.WriteAllBytes(broken, [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
            Assert.Null(HistoryThumbnails.Load(broken));

            var brokenVideo = Path.Combine(folder, "broken.mp4");
            File.WriteAllBytes(brokenVideo, [0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70]);
            Assert.Null(HistoryThumbnails.Load(brokenVideo));

            Assert.True(HistoryThumbnails.CanShow(@"C:\a\photo.JPG"));
            Assert.True(HistoryThumbnails.CanShow(@"C:\a\clip.mp4"));
            Assert.True(HistoryThumbnails.CanShow(@"C:\a\movie.MKV"));
            Assert.True(HistoryThumbnails.CanShow(@"C:\a\sample.webm"));
            Assert.True(HistoryThumbnails.CanShow(@"C:\a\phone.MOV"));
            Assert.False(HistoryThumbnails.CanShow(@"C:\a\report.pdf"));
            Assert.False(HistoryThumbnails.CanShow(@"C:\a\folder"));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [AvaloniaFact]
    public void A_video_generates_a_thumbnail_when_ffmpeg_is_available()
    {
        var tools = Filee.Engines.Media.FfmpegConverter.Locate();
        Assert.SkipUnless(tools is not null, "FFmpeg is not installed (pwsh build/fetch-engines.ps1 -Only ffmpeg)");

        var folder = Directory.CreateTempSubdirectory("filee-videothumbs-").FullName;
        try
        {
            var video = Path.Combine(folder, "sample.mp4");
            var psi = new System.Diagnostics.ProcessStartInfo(tools.Value.Ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-nostdin");
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("lavfi");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add("testsrc=duration=2:size=320x240:rate=10");
            psi.ArgumentList.Add("-pix_fmt");
            psi.ArgumentList.Add("yuv420p");
            psi.ArgumentList.Add(video);

            using var process = System.Diagnostics.Process.Start(psi);
            Assert.NotNull(process);
            Assert.True(process.WaitForExit(20_000));
            Assert.True(process.ExitCode == 0 && File.Exists(video), "Sample video could not be created by FFmpeg.");

            var thumbnail = HistoryThumbnails.Load(video);
            Assert.NotNull(thumbnail);
            Assert.Equal(new PixelSize(HistoryThumbnails.Size, HistoryThumbnails.Size), thumbnail.PixelSize);
            Assert.Same(thumbnail, HistoryThumbnails.Cached(video));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [AvaloniaFact]
    public void A_video_shorter_than_one_second_generates_a_thumbnail()
    {
        var tools = Filee.Engines.Media.FfmpegConverter.Locate();
        Assert.SkipUnless(tools is not null, "FFmpeg is not installed (pwsh build/fetch-engines.ps1 -Only ffmpeg)");

        var folder = Directory.CreateTempSubdirectory("filee-videoshort-").FullName;
        try
        {
            var video = Path.Combine(folder, "short.mp4");
            var psi = new System.Diagnostics.ProcessStartInfo(tools.Value.Ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-nostdin");
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("lavfi");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add("testsrc=duration=0.5:size=320x240:rate=10");
            psi.ArgumentList.Add("-pix_fmt");
            psi.ArgumentList.Add("yuv420p");
            psi.ArgumentList.Add(video);

            using var process = System.Diagnostics.Process.Start(psi);
            Assert.NotNull(process);
            Assert.True(process.WaitForExit(20_000));
            Assert.True(process.ExitCode == 0 && File.Exists(video), "Short sample video could not be created by FFmpeg.");

            var thumbnail = HistoryThumbnails.Load(video);
            Assert.NotNull(thumbnail);
            Assert.Equal(new PixelSize(HistoryThumbnails.Size, HistoryThumbnails.Size), thumbnail.PixelSize);
            Assert.Same(thumbnail, HistoryThumbnails.Cached(video));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_result_opens_with_its_app_and_open_with_only_where_the_system_has_it(bool openWith)
    {
        TestServices.EnsureInitialized("en");
        var loc = AppHost.Get<ILocalizer>();
        var folder = Directory.CreateTempSubdirectory("filee-open-").FullName;
        try
        {
            var picture = Path.Combine(folder, "photo.png");
            var program = Path.Combine(folder, "setup.exe");
            var extracted = Path.Combine(folder, "archive");
            File.WriteAllBytes(picture, [0]);
            File.WriteAllBytes(program, [0]);
            Directory.CreateDirectory(extracted);
            var platform = new OpeningPlatform(openWith);

            var item = new HistoryItemViewModel(Entry([picture]), loc, platform);
            Assert.True(item.CanOpen);
            Assert.Equal(openWith, item.CanOpenWith);
            item.OpenFileCommand.Execute(null);
            item.OpenWithCommand.Execute(null);
            Assert.Equal([picture], platform.Opened);
            string[] openedWith = openWith ? [picture] : [];
            Assert.Equal(openedWith, platform.OpenedWith);

            // A history pointing at a program never starts it; an extracted folder opens, but not "with" an app.
            var changed = new HistoryItemViewModel(Entry([program]), loc, platform);
            Assert.True(changed.HasOutputs);
            Assert.False(changed.CanOpen);
            Assert.False(changed.CanOpenWith);
            changed.OpenFileCommand.Execute(null);
            changed.OpenWithCommand.Execute(null);
            var folderResult = new HistoryItemViewModel(Entry([extracted]), loc, platform);
            Assert.True(folderResult.CanOpen);
            Assert.False(folderResult.CanOpenWith);
            Assert.Equal("", folderResult.FormatBadge);
            Assert.Equal([picture], platform.Opened);

            // Gone since: nothing to open.
            File.Delete(picture);
            item.OpenFileCommand.Execute(null);
            Assert.Single(platform.Opened);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [AvaloniaFact]
    public async Task Undo_moves_the_results_to_the_trash_while_they_are_as_the_job_left_them()
    {
        TestServices.EnsureInitialized("en");
        var loc = AppHost.Get<ILocalizer>();
        var store = AppHost.Get<UserDataStore>();
        var folder = Directory.CreateTempSubdirectory("filee-undo-").FullName;
        store.ClearHistory();
        try
        {
            var a = Path.Combine(folder, "a.png");
            var b = Path.Combine(folder, "b.png");
            File.WriteAllBytes(a, [1]);
            File.WriteAllBytes(b, [2]);
            var entry = Entry([a, b]);
            entry.Undoable = [Stamp(a), Stamp(b)];
            store.AddHistory(entry);
            var platform = new OpeningPlatform(false);

            // Only the Home page (with the history to save) offers it.
            Assert.False(new HistoryItemViewModel(entry, loc, platform).CanUndo);

            // The Trash refuses the second result: the first stays moved (and saved as such), the card says why and
            // still offers Undo for the one left, even rebuilt from the saved history.
            platform.Refuse = new TrashRefusal("trash.in_use");
            platform.RefuseOnly = b;
            var item = new HistoryItemViewModel(entry, loc, platform, store: store);
            Assert.True(item.CanUndo);
            await item.UndoCommand.ExecuteAsync(null);
            Assert.Equal("b.png couldn't be moved to the Recycle Bin / Trash: the file is open in another program", item.UndoError);
            Assert.Equal([a], platform.Trashed);
            Assert.False(entry.Undone);
            Assert.True(item.CanUndo);
            var reloaded = new UserDataStore(store.Directory);
            reloaded.Load();
            Assert.Equal([true, false], Assert.Single(reloaded.History).Undoable.Select(o => o.Trashed));
            Assert.True(new HistoryItemViewModel(reloaded.History[0], loc, platform, store: store).CanUndo);

            platform.Refuse = null;
            await item.UndoCommand.ExecuteAsync(null);
            Assert.Equal([a, b], platform.Trashed); // the first isn't asked for again
            Assert.True(Assert.Single(store.History).Undone);
            var undone = new HistoryItemViewModel(store.History[0], loc, platform, store: store);
            Assert.False(undone.CanUndo);
            Assert.Equal("Undone: the results are in the Recycle Bin / Trash", undone.UndoneText);

            // "Replace the original" sent the originals there first: the card says they're there too.
            entry.OriginalsInTrash = true;
            Assert.Equal("Undone: the results are in the Recycle Bin / Trash, and so are the originals they replaced",
                new HistoryItemViewModel(entry, loc, platform, store: store).UndoneText);

            // A result written again since (an edit, another conversion over it) is never moved.
            var c = Path.Combine(folder, "c.png");
            File.WriteAllBytes(c, [3]);
            var changed = Entry([c]);
            changed.Undoable = [Stamp(c)];
            File.AppendAllText(c, "edited");
            Assert.False(new HistoryItemViewModel(changed, loc, platform, store: store).CanUndo);
            Assert.True(File.Exists(c));

            // Every result is checked before any moves: one changed while the card was open stops it with nothing moved.
            var d = Path.Combine(folder, "d.png");
            var e = Path.Combine(folder, "e.png");
            File.WriteAllBytes(d, [4]);
            File.WriteAllBytes(e, [5]);
            var pair = Entry([d, e]);
            pair.Undoable = [Stamp(d), Stamp(e)];
            var open = new HistoryItemViewModel(pair, loc, platform, store: store);
            File.AppendAllText(e, "edited");
            await open.UndoCommand.ExecuteAsync(null);
            Assert.Equal("e.png couldn't be moved to the Recycle Bin / Trash: it was changed after the conversion", open.UndoError);
            Assert.True(File.Exists(d));
            Assert.DoesNotContain(d, platform.Trashed);
            Assert.False(open.CanUndo);
        }
        finally
        {
            store.ClearHistory();
            Directory.Delete(folder, true);
        }
    }

    private static HistoryOutput Stamp(string path)
    {
        var info = new FileInfo(path);
        return new HistoryOutput { Path = path, Length = info.Length, WrittenUtc = info.LastWriteTimeUtc };
    }

    [AvaloniaTheory]
    [InlineData("en", false)]
    [InlineData("ru", false)]
    [InlineData("hi", true)]
    [InlineData("es", true)]
    public void Recent_conversions_show_their_results(string language, bool dark)
    {
        TestServices.EnsureInitialized(language);
        var store = AppHost.Get<UserDataStore>();
        var theme = AppHost.Get<ThemeService>();
        var folder = Directory.CreateTempSubdirectory("filee-home-").FullName;
        store.ClearHistory();
        try
        {
            if (dark)
                theme.Apply(new ThemeSettings { Mode = ThemeMode.Dark }, language);
            var photo = Path.Combine(folder, "IMG_0412.jpg");
            var sunset = Path.Combine(folder, "sunset.png");
            var report = Path.Combine(folder, "Annual report.pdf");
            WritePicture(photo, 400, 300, SKEncodedImageFormat.Jpeg);
            WritePicture(sunset, 300, 300, SKEncodedImageFormat.Png);
            File.WriteAllBytes(report, [0]);
            store.AddHistory(Entry([report], "PDF", [Path.Combine(folder, "Annual report.docx")]));
            store.AddHistory(Entry([sunset], "PNG", [Path.Combine(folder, "sunset.heic")]));
            var newest = Entry([photo], "JPG 70%", [Path.Combine(folder, "IMG_0412.heic")]);
            newest.Undoable = [Stamp(photo)];
            store.AddHistory(newest);

            var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>(), AppHost.Get<UserDataStore>());
            vm.Navigate("home");
            var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
            window.Show();
            var page = (HomePageViewModel)vm.CurrentPage;
            Assert.True(page.ThumbnailsLoaded.Wait(TimeSpan.FromSeconds(20)));
            RenderTests.Pump();

            Assert.Equal(["JPG 70%", "PNG", "PDF"], page.History.Select(item => item.Title));
            Assert.True(page.History[0].HasThumbnail);
            Assert.True(page.History[1].HasThumbnail);
            Assert.False(page.History[2].HasThumbnail);
            Assert.Equal("PDF", page.History[2].FormatBadge);
            Assert.All(page.History, item => Assert.Equal(OperatingSystem.IsWindows(), item.CanOpenWith));
            Assert.Equal([true, false, false], page.History.Select(item => item.CanUndo));

            // A search lists them anew: the thumbnails come from the cache at once.
            page.Search = "sunset";
            Assert.True(Assert.Single(page.History).HasThumbnail);
            page.Search = "";
            RenderTests.Pump();
            RenderTests.Save(window, $"page-home-recent-{language}{(dark ? "-dark" : "")}.png");
            window.Close();
        }
        finally
        {
            store.ClearHistory(); // also on disk: the next test's application loads the history again
            theme.Apply(new ThemeSettings(), language);
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public void A_built_in_preset_name_follows_the_language_filee_is_in_now()
    {
        // #744: converted while Filee was in Chinese, looked at in English.
        TestServices.EnsureInitialized("en");
        var loc = AppHost.Get<ILocalizer>();
        var platform = new OpeningPlatform(false);
        var builtIn = Entry([], "长图");
        builtIn.PresetNameKey = "preset.long_image";
        Assert.Equal(loc["preset.long_image"], new HistoryItemViewModel(builtIn, loc, platform).Title);
        Assert.NotEqual("长图", loc["preset.long_image"]);

        // The user's own name, an older entry and a key Filee no longer has keep the saved name.
        Assert.Equal("My PNG", new HistoryItemViewModel(Entry([], "My PNG"), loc, platform).Title);
        var gone = Entry([], "旧名");
        gone.PresetNameKey = "preset.no_such_preset";
        Assert.Equal("旧名", new HistoryItemViewModel(gone, loc, platform).Title);
    }

    private static HistoryEntry Entry(List<string> outputs, string preset = "PNG", List<string>? sources = null) => new()
    {
        FinishedAt = DateTime.Now,
        PresetName = preset,
        State = JobState.Completed,
        Sources = sources ?? [Path.Combine(Path.GetTempPath(), "source.heic")],
        Outputs = outputs,
    };

    /// <summary>Red on the left half, blue on the right; a JPEG may carry an EXIF orientation.</summary>
    private static void WritePicture(string path, int width, int height, SKEncodedImageFormat format, int exifOrientation = 0)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Blue);
            using var red = new SKPaint { Color = SKColors.Red };
            canvas.DrawRect(0, 0, width / 2f, height, red);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        var bytes = data.ToArray();
        if (exifOrientation != 0)
            bytes = WithExifOrientation(bytes, exifOrientation);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>A JPEG with an APP1 Exif segment holding only the orientation, right after its SOI marker.</summary>
    private static byte[] WithExifOrientation(byte[] jpeg, int orientation)
    {
        byte[] tiff =
        [
            (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8, // big-endian TIFF header, first IFD at 8
            0, 1, // one entry
            0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, (byte)orientation, 0, 0, // Orientation, SHORT, 1 value
            0, 0, 0, 0, // no next IFD
        ];
        var payload = new List<byte> { (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0 };
        payload.AddRange(tiff);
        var length = payload.Count + 2;
        return [.. jpeg[..2], 0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. payload, .. jpeg[2..]];
    }

    private static (byte R, byte G, byte B) Pixel(Bitmap bitmap, int x, int y)
    {
        var buffer = new byte[4];
        var native = System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
        try
        {
            bitmap.CopyPixels(new PixelRect(x, y, 1, 1), native, 4, 4);
            System.Runtime.InteropServices.Marshal.Copy(native, buffer, 0, 4);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(native);
        }
        return bitmap.Format == PixelFormat.Rgba8888 ? (buffer[0], buffer[1], buffer[2]) : (buffer[2], buffer[1], buffer[0]);
    }

    private static void AssertMostly((byte R, byte G, byte B) pixel, bool red)
    {
        if (red)
            Assert.True(pixel.R > 180 && pixel.B < 80, $"expected red, got {pixel}");
        else
            Assert.True(pixel.B > 180 && pixel.R < 80, $"expected blue, got {pixel}");
    }

    private sealed class OpeningPlatform(bool openWith) : IPlatformServices
    {
        public List<string> Opened { get; } = [];
        public List<string> OpenedWith { get; } = [];
        public List<string> Trashed { get; } = [];
        public TrashRefusal? Refuse { get; set; }

        /// <summary>Refuses only this path, when set.</summary>
        public string? RefuseOnly { get; set; }

        public TrashRefusal? MoveToTrash(string path)
        {
            if (Refuse is not null && (RefuseOnly is null || RefuseOnly == path))
                return Refuse;
            File.Delete(path);
            Trashed.Add(path);
            return null;
        }

        public bool SupportsOpenWith => openWith;
        public void OpenFile(string path) => Opened.Add(path);
        public void OpenWith(string path) => OpenedWith.Add(path);
        public bool IsFileManagerAt(int x, int y) => false;
        public string? ProcessNameAt(int x, int y) => null;
        public IReadOnlyList<string> GetFileManagerSelection() => [];
        public int SystemDragThreshold => 4;
        public bool PrefersReducedMotion => false;
        public void SetStartWithSystem(bool enabled, string executablePath) { }
        public void SetContextMenu(bool enabled, string executablePath, string label) { }
        public ModernContextMenuState GetModernContextMenuState(string executablePath) => ModernContextMenuState.Unsupported;
        public Task<ModernContextMenuResult> SetModernContextMenuAsync(bool enabled, string executablePath) => Task.FromResult(new ModernContextMenuResult(false));
        public void RevealInFileManager(string path) { }
        public void RaiseTopmost(nint windowHandle) { }
    }
}
