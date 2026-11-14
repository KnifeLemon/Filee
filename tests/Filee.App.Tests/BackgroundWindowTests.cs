// The "Add background" window: what it draws is what the file gets (any picture's colours, any look, a right-to-left
// UI), the size it tells is the file's, a swatch sets the background and is named, one look serves every picture unless
// "same look" is off, Save starts the conversion with the look and keeps it in the tool, Skip and the last picture
// close it, a picture that can't be opened or saved ends on a summary, its keys, and its texts follow the language.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Engines.Magick;
using ImageMagick;

namespace Filee.App.Tests;

public sealed class BackgroundWindowTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("filee-background-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// A small "screenshot": a light window with a title bar, a purple button and a strip of vivid colours (where a
    /// wide-gamut profile read wrong shows; on whites and greys it hardly does).
    /// </summary>
    private static MagickImage Shot(uint width = 430, uint height = 290)
    {
        var image = new MagickImage(new MagickColor("#FFFFFF"), 430, 290);
        new ImageMagick.Drawing.Drawables()
            .FillColor(new MagickColor("#F3F3F5")).Rectangle(0, 0, 430, 34)
            .FillColor(new MagickColor("#FF5F57")).Circle(18, 17, 23, 17)
            .FillColor(new MagickColor("#FEBC2E")).Circle(36, 17, 41, 17)
            .FillColor(new MagickColor("#28C840")).Circle(54, 17, 59, 17)
            .FillColor(new MagickColor("#E8E6F5")).Rectangle(22, 90, 340, 100)
            .FillColor(new MagickColor("#ECECEC")).Rectangle(22, 112, 380, 122)
            .FillColor(new MagickColor("#8E7CC3")).RoundRectangle(22, 200, 408, 264, 10, 10)
            .FillColor(new MagickColor("#E5484D")).Rectangle(22, 140, 140, 180)
            .FillColor(new MagickColor("#30A46C")).Rectangle(156, 140, 274, 180)
            .FillColor(new MagickColor("#3E63DD")).Rectangle(290, 140, 408, 180)
            .Draw(image);
        if (width != 430 || height != 290)
            image.Resize(new MagickGeometry(width, height) { IgnoreAspectRatio = true });
        return image;
    }

    private string Picture(string name)
    {
        var path = Path.Combine(_folder, name);
        using var image = Shot();
        image.Write(path, MagickFormat.Png);
        return path;
    }

    /// <summary>
    /// A picture of a kind: the screenshot as sRGB, CMYK, greyscale or Adobe RGB, a cut-out on transparency, a small
    /// one shown far larger than it is, or a thin strip.
    /// </summary>
    private string Picture(string name, string kind)
    {
        var path = Path.Combine(_folder, name);
        using var image = kind switch
        {
            "small" => Shot(60, 40),
            "thin" => Shot(900, 40),
            _ => Shot(),
        };
        switch (kind)
        {
            case "cmyk":
                image.ColorSpace = ColorSpace.CMYK;
                image.Quality = 95;
                image.Write(path, MagickFormat.Jpeg);
                return path;
            case "grey":
                image.Grayscale();
                image.ColorType = ColorType.Grayscale;
                image.Write(path, MagickFormat.Png);
                return path;
            case "adobe":
                image.SetProfile(ColorProfiles.SRGB);
                image.RenderingIntent = RenderingIntent.Relative;
                image.TransformColorSpace(ColorProfiles.SRGB, ColorProfiles.AdobeRGB1998);
                image.Write(path, MagickFormat.Png);
                return path;
            case "gif":
                // See-through round its shape, its background colour the see-through one (as GIFs are written).
                using (var sticker = new MagickImage(MagickColors.Transparent, 430, 290))
                {
                    new ImageMagick.Drawing.Drawables().FillColor(new MagickColor("#8E7CC3")).Rectangle(40, 30, 390, 260).Draw(sticker);
                    sticker.BackgroundColor = MagickColors.Transparent;
                    sticker.Write(path, MagickFormat.Gif);
                }
                return path;
            case "cutout":
                using (var shape = new MagickImage(MagickColors.Transparent, 430, 290))
                {
                    new ImageMagick.Drawing.Drawables().FillColor(new MagickColor("#8E7CC3"))
                        .Circle(215, 145, 215, 20).Draw(shape);
                    shape.Write(path, MagickFormat.Png);
                }
                return path;
            default:
                image.Write(path, MagickFormat.Png);
                return path;
        }
    }

    /// <summary>A picture to put behind: a photo-like CMYK one, or one see-through on its left half.</summary>
    private string Behind(string kind)
    {
        var path = Path.Combine(_folder, "behind-" + kind + (kind == "cmyk" ? ".jpg" : ".png"));
        using var image = new MagickImage(MagickColors.Transparent, 600, 400);
        new ImageMagick.Drawing.Drawables()
            .FillColor(new MagickColor("#3E8E5E")).Rectangle(300, 0, 599, 399)
            .FillColor(new MagickColor("#E3B341")).Circle(450, 200, 450, 80)
            .Draw(image);
        if (kind == "cmyk")
        {
            image.BackgroundColor = new MagickColor("#2B6CB0");
            image.Alpha(AlphaAction.Remove);
            image.ColorSpace = ColorSpace.CMYK;
            image.Quality = 95;
            image.Write(path, MagickFormat.Jpeg);
        }
        else
        {
            image.Write(path, MagickFormat.Png);
        }
        return path;
    }

    private static Preset Tool()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var tool = store.FindPreset(BuiltInData.BackgroundId) ?? BuiltInData.BackgroundPresets().Single();
        tool.Image.BackdropLook = null;
        return tool;
    }

    private (BackgroundViewModel Vm, List<(string Path, Preset Conversion)> Exported, Preset Tool) Create(int files = 1)
    {
        var tool = Tool();
        var exported = new List<(string, Preset)>();
        var paths = Enumerable.Range(1, files).Select(i => Picture($"shot{i}.png")).ToList();
        var vm = new BackgroundViewModel(paths, tool, AppHost.Get<ILocalizer>(), AppHost.Get<UserDataStore>(), (path, conversion) => exported.Add((path, conversion)));
        Wait(() => vm.IsPreviewCurrent && !vm.IsBusy);
        return (vm, exported, tool);
    }

    private static (BackgroundWindow Window, BackgroundViewModel Vm) OpenOn(IReadOnlyList<string> paths, Action<string, Preset> export,
        Avalonia.Media.FlowDirection direction = Avalonia.Media.FlowDirection.LeftToRight)
    {
        var tool = Tool();
        var vm = new BackgroundViewModel(paths, tool, AppHost.Get<ILocalizer>(), AppHost.Get<UserDataStore>(), export);
        var window = new BackgroundWindow { DataContext = vm, Width = 1040, Height = 720, FlowDirection = direction };
        window.Show();
        Wait(() => ((vm.IsPreviewCurrent && !vm.IsBusy) || vm.IsFinished));
        RenderTests.Pump();
        return (window, vm);
    }

    private static void Wait(Func<bool> done, int seconds = 5)
    {
        // Up to about 5 s, as the Pixelate / blur, Crop and Annotate tests allow: a slow runner can take longer than 2.
        for (var i = 0; i < seconds * 200 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.True(done());
    }

    /// <summary>
    /// A key pressed and let go, as on a keyboard: the headless window's KeyPress sends only the press, and a button
    /// pressed with Space is clicked as it is let go. A key that closed the window is let go over another one.
    /// </summary>
    private static void Key(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        if (window.IsVisible)
            window.KeyRelease(key, modifiers, PhysicalKey.None, null);
    }

    private static void ClickOn(Window window, Control control)
    {
        var at = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void The_preview_and_the_size_follow_the_look_at_once()
    {
        var (vm, _, _) = Create();
        // The default look: a gradient, 12 % padding round the 430 px wide picture.
        Assert.True(vm.IsGradient);
        Assert.Equal("534 × 394 px · PNG", vm.SizeText);
        Assert.NotEqual(vm.CanvasWidth, vm.CanvasHeight);

        // Laid out as the slider moves, with no work in the background: a hundred moves take no time at all.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 100; i++)
            vm.Padding = i % BackdropOptions.MaxPadding;
        // Far under a second even on a slow runner; a picture recomposed each time took seconds.
        Assert.True(clock.ElapsedMilliseconds < 2000, $"{clock.ElapsedMilliseconds} ms");

        vm.SetCanvasCommand.Execute(BackdropCanvas.Square);
        Assert.Equal(vm.CanvasWidth, vm.CanvasHeight);
        // The last move left 19 %: 82 px each side of the 430 px wide picture (padding is whole pixels).
        Assert.Equal(19, vm.Padding);
        Assert.Equal("594 × 594 px · PNG", vm.SizeText);
    }

    [AvaloniaFact]
    public void The_size_told_is_the_files_when_the_canvas_would_be_over_the_pixel_limit()
    {
        // A strip 20000 px long on a square with wide padding would be 36000 × 36000: the file is made smaller, and the
        // window said the size it would have been.
        var path = Path.Combine(_folder, "strip.png");
        using (var strip = new MagickImage(new MagickColor("#8E7CC3"), 20000, 100))
            strip.Write(path, MagickFormat.Png);
        var (window, vm) = OpenOn([path], (_, _) => { });
        try
        {
            vm.SetCanvasCommand.Execute(BackdropCanvas.Square);
            vm.Padding = BackdropOptions.MaxPadding;
            vm.Shadow = 0;

            using var file = new MagickImage(path);
            BackdropComposer.Apply(file, vm.Look);
            Assert.Equal($"{file.Width} × {file.Height} px · PNG", vm.SizeText);
            // Opaque all over: the comparison below is of colours, and a see-through strip would pass it.
            Assert.False(file.HasAlpha, "the file has see-through parts");
        }
        finally
        {
            window.Close();
        }
    }

    public static TheoryData<string, string, BackdropCanvas, int, int, bool> Looks => new()
    {
        // picture, background, canvas, shadow, corners, right-to-left UI
        { "screenshot", "gradient", BackdropCanvas.Auto, 45, 3, false },
        { "screenshot", "gradient", BackdropCanvas.Landscape, 100, 20, true },
        { "screenshot", "#2D3A8C", BackdropCanvas.Square, 100, 12, false },
        { "cmyk", "#2D3A8C", BackdropCanvas.Portrait, 45, 3, false },
        { "grey", "gradient", BackdropCanvas.Auto, 45, 3, false },
        { "adobe", "#E06C9F", BackdropCanvas.Auto, 0, 0, false },
        { "cutout", "#FFFFFF", BackdropCanvas.Square, 100, 0, false },
        { "small", "gradient", BackdropCanvas.Square, 100, 20, false },
        { "thin", "gradient", BackdropCanvas.Landscape, 45, 20, false },
        { "screenshot", "behind-cmyk", BackdropCanvas.Square, 45, 3, false },
        { "screenshot", "behind-alpha", BackdropCanvas.Landscape, 0, 0, true },
        { "gif", "#2D3A8C", BackdropCanvas.Square, 45, 3, false },
    };

    [AvaloniaTheory]
    [MemberData(nameof(Looks))]
    public void What_the_window_draws_is_what_the_file_gets(string picture, string background, BackdropCanvas canvas, int shadow, int corners, bool rightToLeft)
    {
        var path = Picture(picture + (picture switch { "cmyk" => ".jpg", "gif" => ".gif", _ => ".png" }), picture);
        var (window, vm) = OpenOn([path], (_, _) => { },
            rightToLeft ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight);
        try
        {
            if (background.StartsWith('#'))
            {
                vm.FillColor = Avalonia.Media.Color.Parse(background);
                vm.SetKindCommand.Execute(BackdropKind.Color);
            }
            else if (background.StartsWith("behind-", StringComparison.Ordinal))
            {
                var kind = background["behind-".Length..];
                vm.Picture = Behind(kind);
                vm.SetKindCommand.Execute(BackdropKind.Picture);
            }
            else
            {
                vm.Gradients[0].ChooseCommand.Execute(null);
            }
            vm.SetCanvasCommand.Execute(canvas);
            vm.Shadow = shadow;
            vm.Corners = corners;
            Wait(() => vm.IsPreviewCurrent);
            Assert.False(vm.PictureUnreadable);
            RenderTests.Pump();

            // The window: the preview's part of a rendered frame.
            using var frame = window.CaptureRenderedFrame()!;
            var framePath = Path.Combine(_folder, "frame.png");
            using (var stream = File.Create(framePath))
                frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            var preview = window.PreviewCanvas;
            var corner = preview.TranslatePoint(new Point(0, 0), window)!.Value;
            var far = preview.TranslatePoint(new Point(preview.Bounds.Width, preview.Bounds.Height), window)!.Value;
            var scale = frame.PixelSize.Width / window.Bounds.Width;
            using var drawn = new MagickImage(framePath);
            drawn.Crop(new MagickGeometry((int)Math.Round(Math.Min(corner.X, far.X) * scale), (int)Math.Round(Math.Min(corner.Y, far.Y) * scale),
                (uint)Math.Round(Math.Abs(far.X - corner.X) * scale), (uint)Math.Round(Math.Abs(far.Y - corner.Y) * scale)));
            drawn.ResetPage();
            drawn.Alpha(AlphaAction.Remove);

            // The file: the picture read as the conversion reads it, set on the look by the engine.
            using var file = new MagickImage(path);
            file.AutoOrient();
            BackdropComposer.Apply(file, vm.Look);
            Assert.Equal($"{file.Width} × {file.Height} px · PNG", vm.SizeText);

            // Both at the smaller size: the sharper one brought down, not the other blown up.
            var compareWidth = Math.Min(file.Width, drawn.Width);
            var compareHeight = (uint)Math.Round(compareWidth * (double)file.Height / file.Width);
            drawn.Resize(new MagickGeometry(compareWidth, compareHeight) { IgnoreAspectRatio = true });
            file.Resize(new MagickGeometry(compareWidth, compareHeight) { IgnoreAspectRatio = true });
            var (mean, worst) = Difference(file, drawn, (int)compareWidth, (int)compareHeight);
            if (mean >= 2.5 || worst >= 7)
            {
                file.Write(Path.Combine(TestServices.ScreenshotDirectory, $"background-file-{picture}-{background}.png"));
                drawn.Write(Path.Combine(TestServices.ScreenshotDirectory, $"background-window-{picture}-{background}.png"));
            }
            Assert.True(mean < 2.5, $"mean difference {mean:0.00} of 255");
            // Each part of the canvas on its own: a gradient running the other way, a shadow fallen half as far or a
            // third lighter, corners a fifth smaller or a picture in other colours differ by 9 to 255 there; the same
            // look differs by at most about 5. Over the whole canvas, the mean hardly moved.
            Assert.True(worst < 7, $"a part of the canvas differs by {worst:0.00} of 255");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The mean difference over the whole of two same-sized pictures, and over the part of them (a sixteenth of the
    /// width square, at least 8 px) where they differ most, both softened a little first: edges come out a pixel softer
    /// or sharper at any zoom (a small picture is shown smoothed many times its size), while a shadow fallen elsewhere,
    /// lighter, corners of another size or a background of other colours differ over an area.
    /// </summary>
    private static (double Mean, double Worst) Difference(MagickImage a, MagickImage b, int width, int height)
    {
        var p = a.GetPixels().ToByteArray(PixelMapping.RGB)!;
        var q = b.GetPixels().ToByteArray(PixelMapping.RGB)!;
        var mean = p.Zip(q, (x, y) => Math.Abs(x - y)).Average();
        using var softA = (MagickImage)a.Clone();
        using var softB = (MagickImage)b.Clone();
        softA.GaussianBlur(0, 1.5);
        softB.GaussianBlur(0, 1.5);
        p = softA.GetPixels().ToByteArray(PixelMapping.RGB)!;
        q = softB.GetPixels().ToByteArray(PixelMapping.RGB)!;
        var tile = Math.Max(8, width / 16);
        var worst = 0.0;
        for (var top = 0; top + tile <= height; top += tile / 2)
        {
            for (var left = 0; left + tile <= width; left += tile / 2)
            {
                long sum = 0;
                for (var y = top; y < top + tile; y++)
                {
                    for (var x = left; x < left + tile; x++)
                    {
                        var i = (y * width + x) * 3;
                        sum += Math.Abs(p[i] - q[i]) + Math.Abs(p[i + 1] - q[i + 1]) + Math.Abs(p[i + 2] - q[i + 2]);
                    }
                }
                worst = Math.Max(worst, sum / (3.0 * tile * tile));
            }
        }
        return (mean, worst);
    }

    [AvaloniaFact]
    public void Save_waits_for_a_picture_behind_that_can_be_read()
    {
        // The file came out on the gradient, though "Picture" was chosen: none chosen, or one gone or broken.
        var (window, vm) = OpenOn([Picture("one.png")], (_, _) => { });
        try
        {
            Assert.True(vm.SaveCommand.CanExecute(null));
            vm.SetKindCommand.Execute(BackdropKind.Picture);
            Assert.False(vm.HasPicture);
            Assert.False(vm.SaveCommand.CanExecute(null));

            vm.Picture = Path.Combine(_folder, "gone.png");
            Wait(() => vm.IsPreviewCurrent);
            Assert.True(vm.PictureUnreadable);
            Assert.False(vm.SaveCommand.CanExecute(null));
            Key(window, Avalonia.Input.Key.Enter);
            Assert.True(window.IsVisible);

            vm.Picture = Behind("cmyk");
            Wait(() => vm.IsPreviewCurrent);
            Assert.False(vm.PictureUnreadable);
            Assert.True(vm.SaveCommand.CanExecute(null));

            vm.Picture = Path.Combine(_folder, "gone.png");
            Wait(() => vm.IsPreviewCurrent);
            vm.SetKindCommand.Execute(BackdropKind.Gradient);
            Assert.True(vm.SaveCommand.CanExecute(null));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_picture_behind_picked_again_is_read_again()
    {
        // A picture that couldn't be read stayed so until the window closed, though fixed and picked again; one edited
        // and picked again showed as it was.
        var path = Path.Combine(_folder, "later.png");
        var (window, vm) = OpenOn([Picture("one.png")], (_, _) => { });
        try
        {
            vm.PickPicture = () => Task.FromResult<string?>(path);
            vm.ChoosePictureCommand.Execute(null);
            Wait(() => vm.IsPreviewCurrent);
            Assert.True(vm.PictureUnreadable);

            using (var picture = new MagickImage(new MagickColor("#3E8E5E"), 300, 200))
                picture.Write(path, MagickFormat.Png);
            vm.ChoosePictureCommand.Execute(null);
            // While it is read: white, not the gradient, a look that wasn't chosen.
            Assert.Equal(Avalonia.Media.Colors.White, Assert.IsType<Avalonia.Media.SolidColorBrush>(vm.BackgroundBrush).Color);
            Wait(() => vm.IsPreviewCurrent && !vm.PictureUnreadable);
            var first = ((Avalonia.Media.ImageBrush)vm.BackgroundBrush!).Source;

            using (var picture = new MagickImage(new MagickColor("#E3B341"), 600, 400))
                picture.Write(path, MagickFormat.Png);
            vm.ChoosePictureCommand.Execute(null);
            Wait(() => vm.IsPreviewCurrent && vm.BackgroundBrush is Avalonia.Media.ImageBrush { } brush && !ReferenceEquals(brush.Source, first));
            Assert.True(vm.SaveCommand.CanExecute(null));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void A_picture_behind_made_readable_without_changing_it_is_read_when_picked_again()
    {
        // Its permissions fixed (or its sync finished) leaves it as written: only picking it again reads it again.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "File modes are a Unix matter.");
        Assert.SkipWhen(Environment.UserName == "root", "Root reads a file whatever its mode (a container, say).");
        var path = Path.Combine(_folder, "locked.png");
        using (var picture = new MagickImage(new MagickColor("#3E8E5E"), 300, 200))
            picture.Write(path, MagickFormat.Png);
        var written = File.GetLastWriteTimeUtc(path);
        File.SetUnixFileMode(path, UnixFileMode.None);
        var (window, vm) = OpenOn([Picture("one.png")], (_, _) => { });
        try
        {
            vm.PickPicture = () => Task.FromResult<string?>(path);
            vm.ChoosePictureCommand.Execute(null);
            Wait(() => vm.IsPreviewCurrent);
            Assert.True(vm.PictureUnreadable);

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.Equal(written, File.GetLastWriteTimeUtc(path));
            vm.ChoosePictureCommand.Execute(null);
            Wait(() => vm.IsPreviewCurrent);
            Assert.False(vm.PictureUnreadable);
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            window.Close();
        }
    }

    /// <summary>Writes a plain picture at <paramref name="path"/> of a size its length tells apart.</summary>
    private static void Plain(string path, uint width, uint height, string color = "#3E8E5E")
    {
        using var picture = new MagickImage(new MagickColor(color), width, height);
        picture.Write(path, MagickFormat.Png);
    }

    private static Avalonia.PixelSize? ShownBehind(BackgroundViewModel vm) =>
        ((vm.BackgroundBrush as Avalonia.Media.ImageBrush)?.Source as Avalonia.Media.Imaging.Bitmap)?.PixelSize;

    [AvaloniaFact]
    public void A_picture_behind_edited_while_it_is_read_is_shown_again_before_it_is_saved()
    {
        // Edited after the reader had its old bytes, the window shows the old picture: its stamp from before the read
        // tells Save it changed, and the new one is shown before it is saved.
        var path = Path.Combine(_folder, "slow.png");
        Plain(path, 300, 200);
        using var decoded = new ManualResetEventSlim();
        using var edited = new ManualResetEventSlim();
        var first = true;
        var before = (new FileInfo(path).LastWriteTimeUtc, new FileInfo(path).Length);
        var read = BackgroundViewModel.ReadPictureBehind;
        BackgroundViewModel.ReadPictureBehind = (p, w, h) =>
        {
            var image = BackdropComposer.ReadPictureBehind(p, w, h);
            if (first)
            {
                first = false;
                decoded.Set();
                edited.Wait(TimeSpan.FromSeconds(10));
            }
            return image;
        };
        try
        {
            var conversions = new List<Preset>();
            var (window, vm) = OpenOn([Picture("one.png")], (_, c) => conversions.Add(c));
            try
            {
                vm.Picture = path;
                vm.SetKindCommand.Execute(BackdropKind.Picture);
                Assert.True(decoded.Wait(TimeSpan.FromSeconds(10)));
                Plain(path, 600, 400);
                edited.Set();
                Wait(() => vm.IsPreviewCurrent && vm.SaveCommand.CanExecute(null));
                Assert.Equal(new Avalonia.PixelSize(300, 200), ShownBehind(vm));

                vm.SaveCommand.Execute(null);
                Assert.Empty(conversions);
                Wait(() => vm.IsPreviewCurrent && vm.SaveCommand.CanExecute(null));
                Assert.Equal(new Avalonia.PixelSize(600, 400), ShownBehind(vm));

                vm.SaveCommand.Execute(null);
                var look = Assert.Single(conversions).Image.Backdrop!;
                Assert.NotEqual(before.Item2, look.PictureLength);
                Assert.Equal(new FileInfo(path).Length, look.PictureLength);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            BackgroundViewModel.ReadPictureBehind = read;
        }
    }

    [AvaloniaFact]
    public void Save_sends_the_stamp_of_the_picture_behind_as_it_was_shown()
    {
        // Changed after Save (before the conversion runs), the file then refuses it rather than getting a picture the
        // window didn't show.
        var path = Path.Combine(_folder, "b.png");
        Plain(path, 300, 200);
        var shown = (new FileInfo(path).LastWriteTimeUtc, new FileInfo(path).Length);
        var conversions = new List<Preset>();
        var (window, vm) = OpenOn([Picture("one.png")], (_, c) => conversions.Add(c));
        try
        {
            vm.Picture = path;
            vm.SetKindCommand.Execute(BackdropKind.Picture);
            Wait(() => vm.IsPreviewCurrent && vm.SaveCommand.CanExecute(null));

            vm.SaveCommand.Execute(null);

            var look = Assert.Single(conversions).Image.Backdrop!;
            Assert.Equal(shown, (look.PictureWritten!.Value, look.PictureLength!.Value));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_picture_behind_edited_since_it_was_shown_is_shown_again_rather_than_saved()
    {
        // Edited in another app between two saves, every later picture went to a conversion the file then refused,
        // and the window closed as if all had gone well.
        var path = Path.Combine(_folder, "b.png");
        Plain(path, 300, 200);
        var conversions = new List<Preset>();
        var (window, vm) = OpenOn([Picture("one.png"), Picture("two.png")], (_, c) => conversions.Add(c));
        try
        {
            vm.Picture = path;
            vm.SetKindCommand.Execute(BackdropKind.Picture);
            Wait(() => vm.IsPreviewCurrent && vm.SaveCommand.CanExecute(null));
            vm.SaveCommand.Execute(null);
            Wait(() => vm.CurrentIndex == 1 && !vm.IsBusy && vm.IsPreviewCurrent && vm.SaveCommand.CanExecute(null));
            Plain(path, 600, 400);

            vm.SaveCommand.Execute(null);
            Assert.Single(conversions);
            Wait(() => vm.IsPreviewCurrent && vm.SaveCommand.CanExecute(null));
            Assert.Equal(new Avalonia.PixelSize(600, 400), ShownBehind(vm));

            vm.SaveCommand.Execute(null);
            Assert.Equal(2, conversions.Count);
            Assert.Equal(new FileInfo(path).Length, conversions[1].Image.Backdrop!.PictureLength);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Pictures_behind_let_go_of_are_disposed_once_another_is_shown()
    {
        // Kept until the window closed: trying fifteen backgrounds held a quarter of a gigabyte.
        var path = Path.Combine(_folder, "b.png");
        Plain(path, 300, 200);
        var (window, vm) = OpenOn([Picture("one.png")], (_, _) => { });
        try
        {
            vm.PickPicture = () => Task.FromResult<string?>(path);
            vm.ChoosePictureCommand.Execute(null);
            Wait(() => vm.IsPreviewCurrent && ShownBehind(vm) is not null);
            var first = (Avalonia.Media.Imaging.Bitmap)((Avalonia.Media.ImageBrush)vm.BackgroundBrush!).Source!;
            vm.ChoosePictureCommand.Execute(null);
            Wait(() => vm.IsPreviewCurrent && ShownBehind(vm) is not null);

            Assert.Throws<ObjectDisposedException>(() => first.PixelSize);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_toast_offers_no_retry_for_a_picture_behind_changed_since_it_was_shown()
    {
        // Retried, the conversion carries the same stamp and is refused again, whatever the message says to do.
        TestServices.EnsureInitialized("en");
        var job = new Filee.Core.Conversion.ConversionJob(["C:/one.png"], BackgroundTool.ConversionOf(Tool(), new BackdropOptions()), "Add background");
        job.Files[0].State = Filee.Core.Conversion.FileState.Failed;
        job.Files[0].ErrorKey = "error.background_picture_changed";
        // Finished (its state is the job queue's to set).
        typeof(Filee.Core.Conversion.ConversionJob).GetProperty(nameof(job.State))!.SetValue(job, Filee.Core.Conversion.JobState.Failed);
        var toast = new JobViewModel(job, AppHost.Get<ILocalizer>(), AppHost.Get<Filee.Core.Platform.IPlatformServices>(), _ => { }, retry: _ => { });

        Assert.False(toast.CanRetry);
        // Another failure of the same conversion is offered as ever.
        job.Files[0].ErrorKey = "error.conversion_failed";
        Assert.True(new JobViewModel(job, AppHost.Get<ILocalizer>(), AppHost.Get<Filee.Core.Platform.IPlatformServices>(), _ => { }, retry: _ => { }).CanRetry);
    }

    [AvaloniaFact]
    public void Skip_pressed_while_a_save_starts_skips_the_next_picture_not_the_saved_one()
    {
        // Save waited on a look at the picture behind's file, and Skip in the meantime moved on: the next picture was
        // written without being shown.
        var exported = new List<string>();
        var (window, vm) = OpenOn([Picture("one.png"), Picture("two.png"), Picture("three.png")], (p, _) => exported.Add(Path.GetFileName(p)));
        try
        {
            vm.Picture = Behind("cmyk");
            vm.SetKindCommand.Execute(BackdropKind.Picture);
            Wait(() => vm.IsPreviewCurrent && vm.SaveCommand.CanExecute(null));
            vm.SaveCommand.Execute(null);
            vm.SkipCommand.Execute(null);
            Wait(() => !vm.IsBusy);
            Assert.Equal(["one.png"], exported);
            Assert.Equal(1, vm.CurrentIndex);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_picture_behind_that_cannot_be_read_is_said_once_not_on_every_slider_step()
    {
        var (window, vm) = OpenOn([Picture("one.png")], (_, _) => { });
        try
        {
            vm.Picture = Path.Combine(_folder, "gone.png");
            vm.SetKindCommand.Execute(BackdropKind.Picture);
            Wait(() => vm.IsPreviewCurrent && vm.PictureUnreadable);
            var changes = 0;
            vm.PropertyChanged += (_, e) => changes += e.PropertyName == nameof(vm.PictureNotice) ? 1 : 0;
            for (var i = 0; i < 3; i++)
                vm.Padding = 20 + i;
            Assert.Equal(0, changes);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_colour_pickers_say_their_names_where_tab_lands()
    {
        // Tab lands on the button inside a colour picker, which said "Avalonia.Controls.Panel".
        var (window, vm) = OpenOn([Picture("one.png")], (_, _) => { });
        try
        {
            var loc = AppHost.Get<ILocalizer>();
            var buttons = window.GetVisualDescendants().OfType<ColorPicker>().Where(p => p.IsEffectivelyVisible)
                .Select(p => p.GetVisualDescendants().OfType<DropDownButton>().First()).ToList();
            Assert.Equal([loc["background.from"], loc["background.to"]], buttons.Select(Avalonia.Automation.AutomationProperties.GetName));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Tab_goes_through_the_look_before_save()
    {
        // The row with Save came first in the card: Tab went to "same look", Skip and Save before any of the look.
        var (window, vm) = OpenOn([Picture("one.png"), Picture("two.png")], (_, _) => { });
        try
        {
            window.Activate();
            Key(window, Avalonia.Input.Key.Tab);
            var first = window.FocusManager?.GetFocusedElement();
            Assert.True(first is RadioButton { GroupName: "BackdropKind" }, $"first stop: {first}");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_notice_about_a_picture_behind_that_cannot_be_read_is_a_text_that_appears()
    {
        // A screen reader announces a text that changes; a fixed text only shown was never said.
        var languages = AppHost.Get<LocalizationService>();
        var (window, vm) = OpenOn([Picture("one.png")], (_, _) => { });
        try
        {
            Assert.Equal("", vm.PictureNotice);
            vm.Picture = Path.Combine(_folder, "gone.png");
            vm.SetKindCommand.Execute(BackdropKind.Picture);
            Wait(() => vm.IsPreviewCurrent);
            Assert.Equal(AppHost.Get<ILocalizer>()["background.picture_unreadable"], vm.PictureNotice);
            languages.SetLanguage("de");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(AppHost.Get<ILocalizer>()["background.picture_unreadable"], vm.PictureNotice);
            vm.SetKindCommand.Execute(BackdropKind.Gradient);
            Assert.Equal("", vm.PictureNotice);
        }
        finally
        {
            languages.SetLanguage("en");
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_look_changed_while_the_first_picture_is_read_is_kept_on_any_close()
    {
        // Closed with the title bar before the first picture showed, the look was dropped.
        var tool = Tool();
        var store = AppHost.Get<UserDataStore>();
        var vm = new BackgroundViewModel([Picture("one.png")], tool, AppHost.Get<ILocalizer>(), store, (_, _) => { });
        Assert.Null(vm.PictureImage);
        vm.Corners = 13;
        vm.Dispose();

        var fresh = new UserDataStore(store.Directory);
        fresh.Load();
        Assert.Equal(13, BackgroundTool.LookOf(fresh.FindPreset(BuiltInData.BackgroundId)!).Corners);
    }

    [AvaloniaFact]
    public void A_picture_behind_that_fails_to_read_in_any_way_is_one_that_cannot_be_read()
    {
        // Anything but an I/O error left it being read for ever: no notice, and the preview never current.
        var read = BackgroundViewModel.ReadPictureBehind;
        BackgroundViewModel.ReadPictureBehind = (_, _, _) => throw new ArgumentException("odd picture");
        try
        {
            var (window, vm) = OpenOn([Picture("one.png")], (_, _) => { });
            try
            {
                vm.Picture = Behind("alpha");
                vm.SetKindCommand.Execute(BackdropKind.Picture);
                Wait(() => vm.IsPreviewCurrent);
                Assert.True(vm.PictureUnreadable);
                Assert.False(vm.SaveCommand.CanExecute(null));
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            BackgroundViewModel.ReadPictureBehind = read;
        }
    }

    public static TheoryData<string> Languages => new(LocalizationService.Languages.Select(l => l.Code));

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public void Every_text_of_the_look_shows_whole_in_every_language(string language)
    {
        // At the window's own size, "Autom…", "Quadr…" and "Speichern & wei…" were cut off, a choice's word broke in
        // two ("Automati / sch"), and the values beside the sliders ran under the scroll bar.
        var (window, vm) = OpenOn([Picture("one.png"), Picture("two.png")], (_, _) => { });
        var languages = AppHost.Get<LocalizationService>();
        try
        {
            languages.SetLanguage(language);
            foreach (var kind in new[] { BackdropKind.Gradient, BackdropKind.Color, BackdropKind.Picture })
            {
                vm.SetKindCommand.Execute(kind);
                RenderTests.Pump();
                var card = window.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("card") && b.IsEffectivelyVisible);
                foreach (var text in card.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)))
                {
                    var what = $"{language}, {kind}: '{text.Text}'";
                    Assert.All(text.TextLayout.TextLines, line => Assert.False(line.HasCollapsed, what + " is cut off"));
                    // Its letters: a line wrapped after a space has the space past its end, which shows nothing.
                    Assert.True(text.TextLayout.Width <= text.Bounds.Width + 1, what + " is wider than its place");
                    // Inside the button or choice it is on, on one line, and inside the card.
                    var holder = text.GetVisualAncestors().OfType<Control>().FirstOrDefault(c => c is Button or RadioButton or CheckBox) ?? card;
                    var bounds = new Rect(text.Bounds.Size).TransformToAABB(text.TransformToVisual(holder)!.Value);
                    Assert.True(bounds.Left >= -1 && bounds.Right <= holder.Bounds.Width + 1, what + " runs out of " + holder.GetType().Name);
                    if (holder is (Button or RadioButton) and not CheckBox)
                        Assert.True(text.TextLayout.TextLines.Count == 1, what + " breaks onto another line");
                    // Clear of the scroll bar, which lies over what scrolls.
                    if (text.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault() is { } scroller
                        && scroller.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>()
                            .FirstOrDefault(b => b.Orientation == Avalonia.Layout.Orientation.Vertical && b.IsEffectivelyVisible) is { } bar)
                    {
                        // Where its letters are, not the whole of its box.
                        var lines = text.TextLayout.TextLines;
                        var left = lines.Min(l => l.Start);
                        var ink = new Rect(left, 0, lines.Max(l => l.Start + l.Width) - left, text.Bounds.Height);
                        var inScroller = ink.TransformToAABB(text.TransformToVisual(scroller)!.Value);
                        var barLeft = bar.TranslatePoint(new Point(0, 0), scroller)!.Value.X;
                        Assert.True(inScroller.Right <= barLeft + 0.5, what + " runs under the scroll bar");
                    }
                }
            }
            RenderTests.Save(window, $"background-look-{language}.png");
        }
        finally
        {
            languages.SetLanguage("en");
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_swatch_sets_the_background_shows_as_chosen_and_is_named()
    {
        var (vm, _, _) = Create();

        vm.Colors[2].ChooseCommand.Execute(null);

        Assert.True(vm.IsColor);
        Assert.True(vm.Colors[2].IsSelected);
        Assert.DoesNotContain(vm.Gradients, g => g.IsSelected);
        Assert.Equal(BackdropKind.Color, vm.Look.Kind);
        Assert.Equal(BackgroundViewModel.ColorPresets[2].Color, vm.Look.Color);
        Assert.Equal("Charcoal", vm.Colors[2].Name);

        vm.Gradients[4].ChooseCommand.Execute(null);
        Assert.True(vm.IsGradient);
        Assert.Equal((BackgroundViewModel.GradientPresets[4].From, BackgroundViewModel.GradientPresets[4].To), (vm.Look.From, vm.Look.To));
        Assert.Equal("Ocean", vm.Gradients[4].Name);
    }

    [AvaloniaFact]
    public void The_choices_are_radio_buttons_with_names_a_screen_reader_says()
    {
        var (window, vm) = OpenOn([Picture("one.png")], (_, _) => { });
        try
        {
            var radios = window.GetVisualDescendants().OfType<RadioButton>().Where(r => r.IsEffectivelyVisible).ToList();
            // Gradient / Colour / Picture, the six gradients, and the four canvases.
            Assert.Equal(3, radios.Count(r => r.GroupName == "BackdropKind"));
            Assert.Equal(6, radios.Count(r => r.GroupName == "BackdropGradient"));
            Assert.Equal(4, radios.Count(r => r.GroupName == "BackdropCanvas"));
            Assert.All(radios.Where(r => r.GroupName is "BackdropGradient" or "BackdropCanvas" && r.Content is not string),
                r => Assert.False(string.IsNullOrEmpty(Avalonia.Automation.AutomationProperties.GetName(r))));
            Assert.Single(radios, r => r.GroupName == "BackdropKind" && r.IsChecked == true);
            Assert.Single(radios, r => r.GroupName == "BackdropCanvas" && r.IsChecked == true);
            Assert.Equal("Sky", Avalonia.Automation.AutomationProperties.GetName(radios.Single(r => r.GroupName == "BackdropGradient" && r.IsChecked == true)));
            Assert.Equal(BackdropCanvas.Auto, vm.Canvas);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Save_starts_the_conversion_with_the_look_keeps_it_and_opens_the_next_picture()
    {
        var (vm, exported, tool) = Create(files: 2);
        Assert.Equal(AppHost.Get<ILocalizer>()["background.save_next"], vm.SaveText);
        vm.Padding = 20;
        vm.Shadow = 0;

        await vm.SaveCommand.ExecuteAsync(null);
        Wait(() => !vm.IsBusy);

        var (path, conversion) = Assert.Single(exported);
        Assert.EndsWith("shot1.png", path);
        Assert.Equal(PresetKind.Convert, conversion.Kind);
        Assert.Equal("png", conversion.TargetFormat);
        Assert.Equal("{name} With Background", conversion.Output.FileNamePattern);
        Assert.Equal((20, 0), (conversion.Image.Backdrop!.Padding, conversion.Image.Backdrop.Shadow));
        // Kept in the tool; and with one look for all, the second picture starts with it.
        Assert.Equal(20, tool.Image.BackdropLook!.Padding);
        Assert.Null(tool.Image.Backdrop);
        Assert.Equal(1, vm.CurrentIndex);
        Assert.Equal(20, vm.Padding);
        Assert.Equal(AppHost.Get<ILocalizer>()["background.save"], vm.SaveText);
    }

    [AvaloniaFact]
    public async Task With_a_look_each_the_next_picture_keeps_its_own()
    {
        var (vm, exported, _) = Create(files: 2);
        vm.SameLook = false;
        vm.Corners = 15;

        await vm.SkipCommand.ExecuteAsync(null);
        Wait(() => !vm.IsBusy);

        Assert.Empty(exported);
        Assert.Equal(1, vm.CurrentIndex);
        Assert.Equal(BackgroundTool.DefaultLook.Corners, vm.Corners);
        var closed = false;
        vm.CloseRequested += () => closed = true;
        await vm.SkipCommand.ExecuteAsync(null);
        Assert.True(closed);
    }

    [AvaloniaFact]
    public void A_picture_that_cannot_be_opened_keeps_its_notice_and_the_run_ends_on_a_summary()
    {
        // The notice was cleared as the next picture opened, and after the last the window closed without a word.
        var broken = Path.Combine(_folder, "broken.png");
        File.WriteAllBytes(broken, [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3]);
        var exported = new List<string>();
        var (window, vm) = OpenOn([broken, Picture("fine.png")], (path, _) => exported.Add(path));
        try
        {
            Assert.Equal(1, vm.CurrentIndex);
            Assert.Contains("broken.png", vm.StatusNotice);
            Key(window, Avalonia.Input.Key.Enter);
            Wait(() => vm.IsFinished);

            Assert.True(window.IsVisible);
            Assert.Equal((1, 0, 1), (vm.DoneCount, vm.SkippedCount, vm.FailedCount));
            Assert.Equal("1 of 2 failed", vm.Summary);
            Assert.Contains(vm.Problems, p => p.StartsWith("broken.png", StringComparison.Ordinal));
            Assert.Single(exported);
            RenderTests.Save(window, "background-summary.png");
            Key(window, Avalonia.Input.Key.Enter);
            Assert.False(window.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_save_that_cannot_start_keeps_the_picture()
    {
        // It went on to the next picture, and this one was neither saved nor shown again.
        var (window, vm) = OpenOn([Picture("one.png"), Picture("two.png")], (_, _) => throw new IOException("disk full"));
        try
        {
            Key(window, Avalonia.Input.Key.Enter);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0, vm.CurrentIndex);
            Assert.Contains("one.png", vm.StatusNotice);
            Assert.NotNull(vm.PictureImage);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_save_that_works_the_second_time_leaves_no_failure_behind()
    {
        var tries = 0;
        var exported = new List<string>();
        var (window, vm) = OpenOn([Picture("one.png"), Picture("two.png")], (path, _) =>
        {
            if (tries++ == 0)
                throw new IOException("busy");
            exported.Add(path);
        });
        try
        {
            Key(window, Avalonia.Input.Key.Enter);
            Dispatcher.UIThread.RunJobs();
            Key(window, Avalonia.Input.Key.Enter);
            Wait(() => vm.CurrentIndex == 1 && !vm.IsBusy);
            Assert.Equal("", vm.StatusNotice);
            Assert.Empty(vm.Problems);

            Key(window, Avalonia.Input.Key.Enter);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, exported.Count);
            Assert.False(vm.IsFinished);            // closed, with nothing to report
            Assert.False(window.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Enter_saves_and_Space_presses_nothing_after_a_button_was_clicked()
    {
        // A clicked button keeps the focus: Enter or Space pressed it again (Skip skipped the picture) instead of
        // saving or doing nothing.
        var exported = new List<string>();
        var (window, vm) = OpenOn([Picture("one.png"), Picture("two.png"), Picture("three.png")], (path, _) => exported.Add(path));
        try
        {
            var skip = window.GetVisualDescendants().OfType<Button>().Single(b => b.Command == vm.SkipCommand);
            ClickOn(window, skip);
            Wait(() => vm.CurrentIndex == 1 && !vm.IsBusy);
            // Where a click on a desktop leaves the focus (the headless window gives it to nothing).
            Assert.True(skip.Focus(NavigationMethod.Pointer));
            Assert.True(skip.IsFocused);

            Key(window, Avalonia.Input.Key.Space);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, vm.CurrentIndex);

            Key(window, Avalonia.Input.Key.Enter);
            Wait(() => !vm.IsBusy);

            Assert.EndsWith("two.png", Assert.Single(exported));
            Assert.Equal(2, vm.CurrentIndex);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_arrows_go_along_a_row_of_choices_and_Space_chooses_but_Enter_still_saves()
    {
        var exported = new List<string>();
        var (window, vm) = OpenOn([Picture("one.png")], (path, _) => exported.Add(path));
        try
        {
            var gradient = window.FindControl<RadioButton>("GradientChoice")!;
            gradient.Focus(NavigationMethod.Tab);
            Key(window, Avalonia.Input.Key.Right);
            Assert.True(vm.IsColor);
            Key(window, Avalonia.Input.Key.Right);
            Assert.True(vm.IsPicture);
            Key(window, Avalonia.Input.Key.Right);       // round to the first
            Assert.True(vm.IsGradient);
            Key(window, Avalonia.Input.Key.Left);
            Assert.True(vm.IsPicture);
            Key(window, Avalonia.Input.Key.Right);
            Assert.True(vm.IsGradient);

            // Space chooses a choice reached with the keyboard.
            var square = window.GetVisualDescendants().OfType<RadioButton>()
                .Single(r => r.GroupName == "BackdropCanvas" && Equals(r.CommandParameter, BackdropCanvas.Square));
            square.Focus(NavigationMethod.Tab);
            Key(window, Avalonia.Input.Key.Space);
            Assert.Equal(BackdropCanvas.Square, vm.Canvas);
            Assert.Empty(exported);

            Key(window, Avalonia.Input.Key.Enter);
            Wait(() => !vm.IsBusy);
            Assert.Single(exported);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Esc_closes_and_closing_with_the_title_bar_keeps_the_look_on_disk()
    {
        // The window works on the library's own tool: the look was put in it, then compared with itself, found the
        // same and never written, so it was gone the next time Filee started.
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        BackdropOptions Saved()
        {
            var fresh = new UserDataStore(store.Directory);
            fresh.Load();
            return BackgroundTool.LookOf(fresh.FindPreset(BuiltInData.BackgroundId)!);
        }
        var (window, vm) = OpenOn([Picture("one.png")], (_, _) => { });
        Assert.Same(store.FindPreset(BuiltInData.BackgroundId), Tool());
        vm.Corners = 9;
        Key(window, Avalonia.Input.Key.Escape);
        Assert.False(window.IsVisible);
        Assert.Equal(9, Saved().Corners);

        var (other, vm2) = OpenOn([Picture("two.png")], (_, _) => { });
        Assert.Equal(BackgroundTool.DefaultLook.Corners, vm2.Corners);
        vm2.Shadow = 80;
        other.Close();     // as the title bar's close or Alt+F4 does: not the window's own Close command
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(80, Saved().Shadow);
    }

    [AvaloniaFact]
    public void Its_texts_follow_a_language_change()
    {
        var (window, vm) = OpenOn([Picture("one.png"), Picture("two.png")], (_, _) => { });
        var languages = AppHost.Get<LocalizationService>();
        try
        {
            vm.Shadow = 80;
            Assert.Equal("Sunset", vm.Gradients[0].Name);
            Assert.Equal("Strong", vm.ShadowText);
            languages.SetLanguage("ko");
            Dispatcher.UIThread.RunJobs();
            var loc = AppHost.Get<ILocalizer>();
            Assert.Equal(loc["background.gradient_sunset"], vm.Gradients[0].Name);
            Assert.Equal("노을", vm.Gradients[0].Name);
            Assert.Equal(loc["background.shadow_strong"], vm.ShadowText);
            Assert.Equal(loc["background.no_picture"], vm.PictureName);
            Assert.Equal(loc["background.save_next"], window.FindControl<Button>("SaveButton")!.Content);
            Assert.Equal(loc.Format("crop.page_counter", 1, 2), vm.CounterText);
        }
        finally
        {
            languages.SetLanguage("en");
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_picker_offers_only_pictures_the_file_can_be_made_with()
    {
        // The picker offered every image (an SVG, an icon), and one the file can't read was left out without a word.
        foreach (var pattern in BackdropComposer.PictureBehindPatterns)
            Assert.True(BackdropComposer.IsPicturePath(Path.Combine(_folder, "x" + pattern[1..]), out _), pattern);
        Assert.DoesNotContain("*.svg", BackdropComposer.PictureBehindPatterns);
    }

    [AvaloniaTheory]
    [InlineData(ThemeMode.Light)]
    [InlineData(ThemeMode.Dark)]
    public void Screenshot(ThemeMode mode)
    {
        var (vm, _, _) = Create(files: 3);
        // After Create: it initializes the services, which applies the stored theme again.
        var theme = AppHost.Get<ThemeService>();
        theme.Apply(new ThemeSettings { Mode = mode }, "en");
        vm.Gradients[1].ChooseCommand.Execute(null);
        vm.Corners = 4;
        var window = new BackgroundWindow { DataContext = vm };
        theme.ApplyWindowClass(window);
        window.Show();
        RenderTests.Pump();
        Assert.Equal(mode == ThemeMode.Dark ? ThemeVariant.Dark : ThemeVariant.Light, window.ActualThemeVariant);
        RenderTests.Save(window, $"background-{mode.ToString().ToLowerInvariant()}.png");
        window.Close();
    }
}
