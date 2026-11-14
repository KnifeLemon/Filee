// Renders the demo animations of the "What's new" slides (src/Filee.App/Assets/WhatsNew/<version>/*.webp) with the
// headless app: real windows, real donut animations, frame by frame, encoded as compact animated WebP by Magick.NET.
// The files it writes are product assets; run it again when the UI they show changes. It is explicit, so a normal test
// run skips it:
//
//   dotnet build tests/Filee.App.Tests
//   tests/Filee.App.Tests/bin/Debug/net10.0/Filee.App.Tests.exe -method "Filee.App.Tests.WhatsNewMedia.*" -explicit only
// Set FILEE_WHATSNEW_LANGUAGE=pt-PT (or pt-BR) for localized recordings; shared English recordings stay unchanged.
//
// Each clip is drawn on a 768 × 432 canvas (16:9, shown at 496 × 279 in the window, sharp up to 150 % scaling) in the
// light theme. The first frame of each clip is what reduced motion shows, so every clip starts on a meaningful picture;
// a clip that marks a better one writes it into slides.json as the slide's "poster".

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.App.Views;
using Filee.App.Views.Pages;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;
using ImageMagick;
using ImageMagick.Drawing;
using static Avalonia.VisualExtensions;

namespace Filee.App.Tests;

[Collection("Motion")]
public class WhatsNewMedia
{
    internal const int CanvasWidth = 768;
    internal const int CanvasHeight = 432;
    private const double Frame = 1 / 25.0;

    /// <summary>The light theme's PageBrush: the canvas behind every clip.</summary>
    private const string Page = "#F6F5F1";

    /// <summary>Where the slides of <paramref name="version"/> live in the repository.</summary>
    internal static string Folder(string version) =>
        Path.Combine(RepositoryRoot, "src", "Filee.App", "Assets", "WhatsNew", version);

    internal static string RepositoryRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Filee.slnx")))
                dir = dir.Parent;
            return dir!.FullName;
        }
    }

    [AvaloniaFact(Explicit = true)]
    public void Generate_2_0_0()
    {
        var language = Environment.GetEnvironmentVariable("FILEE_WHATSNEW_LANGUAGE") ?? "en";
        Assert.Contains(language, AppLanguages.All.Select(l => l.Code));
        TestServices.EnsureInitialized(language);
        var folder = language == "en" ? Folder("2.0.0") : Path.Combine(Folder("2.0.0"), language);
        Directory.CreateDirectory(folder);
        var theme = AppHost.Get<ThemeService>();
        // Animations on whatever this machine prefers: every theme applied here says ReduceMotion = false, since
        // ThemeService.Apply reads the setting again.
        Motion.Set(true);
        // The recording's region, independently of the machine that generates it.
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(language == "en" ? "en-US" : language);
        try
        {
            theme.Apply(new ThemeSettings { ReduceMotion = false, Mode = ThemeMode.Light }, language);
            // browser.webp and flip.webp come from the demo-video generator (branch media/demo-videos: a scripted desktop
            // with a browser and a file manager). These headless versions are written only when asked for by name
            // (FILEE_WHATSNEW_ONLY=browser,flip).
            Write(folder, "browser.webp", BrowserDrop, onRequestOnly: true);
            Write(folder, "flip.webp", DonutFlip, onRequestOnly: true);
            Write(folder, "formats.webp", FormatDonuts);
            Write(folder, "crop.webp", Crop);
            Write(folder, "theme.webp", () => Theme("2.0.0"));
            // This slide intentionally demonstrates several languages, rather than the reader's UI language.
            if (language == "en")
                Write(folder, "languages.webp", () => Languages("2.0.0"));
            Write(folder, "more.webp", () => More("2.0.0"));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
            theme.Apply(new ThemeSettings(), "en");
            TestServices.EnsureInitialized("en");
        }
    }

    [AvaloniaFact(Explicit = true)]
    public void Generate_2_3_0()
    {
        TestServices.EnsureInitialized("en");
        var folder = Folder("2.3.0");
        var theme = AppHost.Get<ThemeService>();
        Motion.Set(true);
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        try
        {
            theme.Apply(new ThemeSettings { ReduceMotion = false, Mode = ThemeMode.Light }, "en");
            Write(folder, "annotate.webp", Annotate);
            Write(folder, "crop_video.webp", CropVideo);
            Write(folder, "sheets.webp", Sheets);
            Write(folder, "fields.webp", Fields);
            Write(folder, "translate.webp", Translate);
            Write(folder, "more.webp", AndMore);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
            theme.Apply(new ThemeSettings(), "en");
            TestServices.EnsureInitialized("en");
        }
    }

    // ───────────────────────── Scenes ─────────────────────────

    /// <summary>
    /// A picture dropped from a browser (#261): the donut for it, "Drop → JPG", then the ring with the download badge
    /// above it while the picture is saved, then the check. The badge shows what JobViewModel would write; the real
    /// drag from a browser can't be made headless.
    /// </summary>
    private static Clip BrowserDrop()
    {
        var clip = new Clip();
        // Where a browser picture stands before it is saved (BrowserDrags: placeholder paths named like the file).
        var (window, vm) = OpenDonut([Path.Combine(Path.GetTempPath(), "Filee", "web", "drop", "1", "sunset.jpg")], clickMode: false);
        var donut = window.DonutControl;
        var badge = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "DownloadBadge");
        var badgeText = (TextBlock)badge.Child!;
        var loc = AppHost.Get<ILocalizer>();
        try
        {
            // Frame 0 (reduced motion): the open donut with "Drop → JPG".
            donut.FinishAnimations();
            Highlight(window, vm, 1);
            clip.Add(Capture(window), 1400);

            donut.BeginProgress();
            badge.Transitions = null;
            const long size = 3_100_000;
            var shown = false;
            for (var t = 0.0; t < 1.6; t += Frame)
            {
                if (t >= 0.3)
                {
                    var part = Math.Min(1, (t - 0.3) / 1.2);
                    var done = (long)(size * part);
                    donut.SetProgress(part / 2); // saving is the first half of a browser file's progress
                    badgeText.Text = ViewModels.DownloadBadge.Format(loc, 2_400_000, new Filee.Core.Conversion.JobTransfer(done, size, 0, 1, done));
                    if (!shown)
                    {
                        (badge.Opacity, badge.RenderTransform, shown) = (1, null, true);
                    }
                }
                Step(donut);
                clip.Add(Capture(window), (int)(Frame * 1000));
            }
            badge.Opacity = 0;
            donut.SetProgress(1);
            Run(clip, window, 0.6);
            donut.CompleteProgress(DonutProgressOutcome.Succeeded, () => { });
            Run(clip, window, 2.5, () => donut.ShownOutcome is not null);
            // The check stays a moment before the clip starts over.
            clip.Hold(1600);
        }
        finally
        {
            window.Close();
        }
        return clip;
    }

    /// <summary>
    /// The donut's back face (#262): the images donut with "⟳ Tab back" in the hub turns over to its back face and,
    /// after a moment, to the front again.
    /// </summary>
    private static Clip DonutFlip()
    {
        var clip = new Clip();
        var (window, vm) = OpenDonut([@"C:\photos\a.jpg", @"C:\photos\b.jpg"], clickMode: true);
        var donut = window.DonutControl;
        try
        {
            donut.FinishAnimations();
            Pump(); // the back face is checked once the front is on screen
            Assert.True(vm.CanFlip);
            Settle(donut);
            // Frame 0: the front with its "⟳ Tab back" hint.
            clip.Add(Capture(window), 1600);
            for (var turn = 0; turn < 2; turn++)
            {
                Assert.True(donut.Flip());
                for (var t = 0.0; t < DonutMenu.FlipDuration + Frame; t += Frame)
                {
                    Step(donut);
                    clip.Add(Capture(window), (int)(Frame * 1000));
                }
                Settle(donut);
                clip.Add(Capture(window), turn == 0 ? 1800 : 900);
            }
        }
        finally
        {
            window.Close();
            if (vm.IsBackFace)
                vm.ToggleFace();
        }
        return clip;
    }

    /// <summary>The donut opening for one new format after another.</summary>
    private static Clip FormatDonuts()
    {
        var clip = new Clip();
        string[][] sets =
        [
            [@"C:\gis\roads.shp"],
            [@"C:\invoices\invoice.ofd"],
            [@"C:\web\article.mhtml"],
            [@"C:\photos\scan.jxr"],
            [@"C:\models\chair.fbx"],
            [@"C:\slides\template.potm"],
        ];
        foreach (var files in sets)
        {
            var (window, vm) = OpenDonut(files, clickMode: true);
            // As someone with the optional engines (LibreOffice) sees it, not greyed out for a missing engine.
            vm.Items = vm.Items.Select(Enabled).ToList();
            try
            {
                var donut = window.DonutControl;
                if (clip.Count == 0)
                {
                    // Frame 0: the first donut open, for reduced motion.
                    donut.FinishAnimations();
                    Highlight(window, vm, 0);
                    clip.Add(Capture(window), 900);
                    donut.SetExternalHighlight(-1);
                }
                donut.PlayOpenAnimation();
                Run(clip, window, 0.4);
                Highlight(window, vm, 0);
                clip.Add(Capture(window), 1100);
            }
            finally
            {
                window.Close();
            }
        }
        return clip;
    }

    /// <summary>
    /// The crop tool (#307): three pictures dragged onto the donut's Crop slice, the crop overlay opening on the first,
    /// A pressed with the pointer over the picture opening the aspect dial around the pointer, 1:1 picked, then "Crop
    /// &amp; next" opening the second picture while the strip marks the first done. Real pointer and key input; the
    /// donut and the canvas animate on the clip's clock (<see cref="CropCanvas.Clock"/>), and the transitions of the
    /// dial, its slices and the strip are played here as the window's styles time them (the render timer, which runs
    /// them in the app, keeps wall-clock time).
    /// </summary>
    private static Clip Crop()
    {
        var clip = new Clip { Quality = 72 };
        var folder = Directory.CreateTempSubdirectory("filee-whatsnew-crop-").FullName;
        var clock = System.Diagnostics.Stopwatch.GetTimestamp();
        CropCanvas.Clock = () => clock;
        // Crop on the donut's front (it ships on the back face, where Tab turns the donut): one drop in a short clip.
        var images = AppHost.Get<UserDataStore>().Profiles.Single(p => p.Id == "images");
        var (front, back) = (images.PresetIds.ToList(), images.BackPresetIds?.ToList());
        images.PresetIds[images.PresetIds.IndexOf("half-size")] = "tool-crop";
        images.BackPresetIds?.Remove("tool-crop");
        CropOverlayWindow? window = null;
        try
        {
            var files = new[] { "harbour.jpg", "meadow.jpg", "dunes.jpg" }.Select((name, i) =>
            {
                var path = Path.Combine(folder, name);
                DrawLandscape(path, i);
                return path;
            }).ToList();
            DropOnCrop(clip, files);

            // The overlay opens on the first picture, which settles in on the stepped clock.
            var preset = BuiltInData.CreatePresets().Single(p => p.Id == "tool-crop");
            var vm = new CropOverlayViewModel(files, preset, AppHost.Get<ILocalizer>(), AppHost.Get<UserDataStore>());
            window = new CropOverlayWindow
            {
                DataContext = vm,
                Width = 889, // 16:9 at the window's least height
                Height = 500,
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(Page)),
            };
            window.Show();
            WaitFor(() => vm.PreviewImage is not null && !vm.IsBusy);
            // The styles' transitions off (played below).
            window.Classes.Set("reduced-motion", true);
            Pump();

            // 20 frames a second keep the clip within the slides' budget.
            var scene = new Scene(window, clip) { FrameLength = 1 / 20.0 };
            scene.Steps.Add(dt => clock += (long)(System.Diagnostics.Stopwatch.Frequency * dt));
            PlayCropTransitions(window, vm, scene);

            var canvas = window.CropCanvas;
            var origin = (Avalonia.Vector)canvas.TranslatePoint(default, window)!.Value;
            var image = canvas.CalculateImageBounds().Translate(origin);
            Avalonia.Point Middle(Control control) =>
                control.TranslatePoint(new Avalonia.Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
            Avalonia.Point OnPicture(double x, double y) => new(image.X + image.Width * x, image.Y + image.Height * y);

            scene.Place(OnPicture(0.8, 0.75));
            scene.Hold(0.7);

            // A with the pointer over the picture: the dial opens around the pointer.
            scene.MoveTo(OnPicture(0.6, 0.45), 0.45);
            scene.Hold(0.2);
            scene.Key("A", () => window.KeyPress(Key.A, RawInputModifiers.None, PhysicalKey.A, "a"));
            Assert.True(vm.IsAspectDialOpen);
            scene.Hold(0.45);
            window.KeyRelease(Key.A, RawInputModifiers.None, PhysicalKey.A, "a");
            scene.HideKey();
            var dial = window.AspectDial;
            var square = dial.Segments.ToList().FindIndex(s => s.Mode == "1:1");
            var onSquare = dial.TranslatePoint(DonutGeometry.PointAt(new Avalonia.Point(dial.Bounds.Width / 2, dial.Bounds.Height / 2),
                dial.OuterRadius - 14, DonutGeometry.MidAngle(square, dial.Segments.Count())), window)!.Value; // beside its label
            scene.MoveTo(onSquare, 0.45);
            scene.Hold(0.35);
            // Shown with reduced motion: the dial around the pointer, on 1:1.
            clip.MarkPoster();
            scene.Click();
            Assert.Equal("1:1", vm.SelectedAspect);
            scene.Hold(1.0);

            // Crop & next: the second picture settles in, the strip marks the first one done.
            var primary = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PrimaryButton");
            scene.MoveTo(Middle(primary) + new Avalonia.Vector(-12, 2), 0.55);
            scene.Hold(0.2);
            scene.Press();
            scene.Tick();
            scene.Release();
            WaitFor(() => vm.CurrentIndex == 1 && !vm.IsBusy && vm.PreviewImage is not null);
            Assert.Equal(CropItemState.Done, vm.Queue[0].State);
            scene.Hold(0.4);
            scene.MoveTo(Middle(primary) + new Avalonia.Vector(150, -14), 0.4);
            scene.Hold(1.2);
        }
        finally
        {
            window?.Close();
            CropCanvas.Clock = System.Diagnostics.Stopwatch.GetTimestamp;
            (images.PresetIds, images.BackPresetIds) = (front, back);
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException) { }
        }
        return clip;
    }

    /// <summary>
    /// The pictures dragged with the gesture key: the donut opens around the pointer, which goes onto the Crop slice
    /// and drops there (what RadialWindow does with a drag over it, on the donut's own clock).
    /// </summary>
    private static void DropOnCrop(Clip clip, IReadOnlyList<string> files)
    {
        var (window, vm) = OpenDonut(files, clickMode: false);
        var donut = window.DonutControl;
        try
        {
            var crop = vm.Presets.ToList().FindIndex(p => p.Id == "tool-crop");
            Assert.True(crop >= 0, "Crop isn't on the donut");
            var center = new Avalonia.Point(donut.Bounds.Width / 2, donut.Bounds.Height / 2);
            var inner = donut.OuterRadius * donut.HoleRatio;
            Avalonia.Point InWindow(Avalonia.Point p) => donut.TranslatePoint(p, window)!.Value;

            // Its own frame loop would run on wall-clock time: only its clock, stepped here, moves it.
            var scene = new Scene(window, clip) { FrameLength = 1 / 20.0, RunsRenderTimer = false };
            scene.Steps.Add(dt =>
            {
                var at = window.TranslatePoint(scene.Pointer, donut)!.Value;
                var hit = donut.HitTest(at);
                donut.SetExternalHit(hit, at);
                vm.ShowHint(hit.Kind == DonutHitKind.Segment ? hit.Index : -1, -1, hit.Kind == DonutHitKind.Center);
                KeepFramesComing(donut);
                donut.Advance(dt);
                KeepFramesComing(donut);
            });
            scene.Grab(true); // the files are being dragged
            scene.Place(InWindow(center + new Avalonia.Vector(6, 8)));
            scene.ShowKey("Ctrl", down: true);
            donut.PlayOpenAnimation();
            scene.Hold(0.6);
            scene.MoveTo(InWindow(DonutGeometry.PointAt(center, (inner + donut.OuterRadius) / 2 + 6,
                DonutGeometry.MidAngle(crop, donut.Items!.Count) + 0.05)), 0.6);
            scene.Hold(0.45);
            Assert.Equal(new DonutHit(DonutHitKind.Segment, crop), donut.HitTest(window.TranslatePoint(scene.Pointer, donut)!.Value));
            scene.HideKey();
        }
        finally
        {
            window.Close();
            donut.SetExternalHighlight(-1);
        }
    }

    /// <summary>
    /// What CropOverlayWindow.axaml's transitions do, on the clip's clock: the dial grows out of its button (opacity
    /// 0.16 s, scale 0.55 → 1 in 0.34 s) and shrinks away, a slice under the pointer moves out (0.24 s), and the strip's
    /// tiles grow and shrink as the current file changes (0.32 s).
    /// </summary>
    private static void PlayCropTransitions(CropOverlayWindow window, CropOverlayViewModel vm, Scene scene)
    {
        var dial = window.AspectDial;
        dial.Transitions = null;
        var (opacity, scale) = (new Glide(0, 0.16, t => t), new Glide(0.55, 0.34, BackEaseOut));
        var pops = dial.Segments.ToDictionary(s => s, _ => new Glide(0, 0.24, BackEaseOut));
        foreach (var segment in pops.Keys)
            segment.Transitions = null;
        var tiles = new Dictionary<Border, (Glide Scale, Glide Opacity)>();
        scene.Steps.Add(dt =>
        {
            var open = vm.IsAspectDialOpen;
            dial.Opacity = opacity.Step(open ? 1 : 0, dt);
            var s = scale.Step(open ? 1 : 0.55, dt);
            dial.RenderTransform = new Avalonia.Media.ScaleTransform(s, s);
            dial.IsVisible = open || dial.Opacity > 0.001;
            foreach (var (segment, pop) in pops)
                segment.Pop = pop.Step(segment.IsPointerOver ? 1 : 0, dt);

            foreach (var tile in window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("tile")))
            {
                if (!tiles.TryGetValue(tile, out var glide))
                    tiles[tile] = glide = (new Glide(TileScale(tile), 0.32, BackEaseOut), new Glide(TileOpacity(tile), 0.2, t => t));
                var size = glide.Scale.Step(TileScale(tile), dt);
                tile.RenderTransform = new Avalonia.Media.ScaleTransform(size, size);
                tile.Opacity = glide.Opacity.Step(TileOpacity(tile), dt);
            }
        });

        static double TileScale(Border tile) => tile.Classes.Contains("current") ? 1.08 : 0.86;
        static double TileOpacity(Border tile) =>
            tile.Classes.Contains("current") ? 1 : tile.Classes.Contains("skipped") || tile.Classes.Contains("failed") ? 0.55 : 0.9;
    }

    /// <summary>
    /// The Theme page: an accent swatch clicked (the whole window takes the colour), a colour picked on the spectrum of
    /// the colour picker (#291), Softness rounding the preview donut, and the donut toolbar's opacity (#275) letting the
    /// backdrop through. Swatch and spectrum get real pointer input; the sliders are moved through the page's settings
    /// with the pointer on their thumbs.
    /// </summary>
    private static Clip Theme(string version)
    {
        // 15 frames a second at a lower quality: every change of colour redraws the whole window.
        var clip = new Clip { Quality = AppHost.Get<ILocalizer>().Language == "en" ? 72U : 70U };
        var store = AppHost.Get<UserDataStore>();
        var theme = AppHost.Get<ThemeService>();
        var (themeBefore, opacityBefore) = (store.Settings.Theme, store.Settings.Donut.Opacity);
        store.Settings.Theme = new ThemeSettings { ReduceMotion = false, Mode = ThemeMode.Light };
        store.Settings.Donut.Opacity = 1;
        theme.Apply(store.Settings.Theme, AppHost.Get<ILocalizer>().Language);
        var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>(), AppHost.Get<UserDataStore>());
        var window = new MainWindow { DataContext = vm, Width = 1080, Height = 608 };
        try
        {
            vm.Navigate("theme");
            window.Show();
            Pump();
            var page = (ThemePageViewModel)vm.CurrentPage!;
            var view = window.GetVisualDescendants().OfType<ThemePage>().Single();
            var softness = view.GetVisualDescendants().OfType<Slider>().Single(s => s.Name != "DonutOpacitySlider");
            var opacity = view.FindControl<Slider>("DonutOpacitySlider")!;

            // Scrolled, if the window is too short for it, so the opacity card shows whole.
            var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "PageScroller");
            var card = opacity.FindAncestorOfType<Border>()!;
            while (!card.Classes.Contains("card"))
                card = card.FindAncestorOfType<Border>()!;
            var cardBottom = card.TranslatePoint(new Avalonia.Point(0, card.Bounds.Height), scroller)!.Value.Y;
            scroller.Offset = new Avalonia.Vector(0, Math.Max(0, cardBottom + 18 - scroller.Bounds.Height));
            Pump();

            var scene = new Scene(window, clip, version) { FrameLength = 1 / 15.0 };
            (string?, double) applied = (store.Settings.Theme.Accent, store.Settings.Theme.CornerRadius);
            // The app applies the theme when the settings change (App.ApplySettings); here the clip does.
            scene.Steps.Add(_ =>
            {
                var now = (store.Settings.Theme.Accent, store.Settings.Theme.CornerRadius);
                if (now != applied)
                {
                    applied = now;
                    theme.Apply(store.Settings.Theme, AppHost.Get<ILocalizer>().Language);
                }
            });
            Avalonia.Point Middle(Control control) =>
                control.TranslatePoint(new Avalonia.Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
            Avalonia.Point Thumb(Slider slider) =>
                Middle(slider.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Thumb>().Single()) + new Avalonia.Vector(3, 5);

            // Frame 0: the page as it opens.
            var preview = view.GetVisualDescendants().OfType<DonutMenu>().Single();
            scene.Place(Middle(preview) + new Avalonia.Vector(150, 40));
            scene.Hold(0.9);

            // A swatch: the window takes its colour.
            var swatches = view.FindControl<ListBox>("AccentSwatches")!;
            scene.MoveTo(Middle(swatches.ContainerFromIndex(1)!) + new Avalonia.Vector(4, 6), 0.5);
            scene.Click();
            scene.Hold(0.8);

            // Any colour: a click on the spectrum under the picker.
            var picker = view.FindControl<ColorPicker>("AccentPicker")!;
            var dropDown = picker.GetVisualDescendants().OfType<Button>().First();
            scene.MoveTo(Middle(dropDown) + new Avalonia.Vector(4, 6), 0.45);
            scene.Click();
            scene.Hold(0.3);
            var spectrum = ((Control)((Flyout)dropDown.Flyout!).Content!).GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ColorSpectrum>().Single();
            scene.MoveTo(spectrum.TranslatePoint(new Avalonia.Point(spectrum.Bounds.Width * 0.6, spectrum.Bounds.Height * 0.3), window)!.Value, 0.45);
            scene.Click();
            scene.Hold(0.8);
            clip.MarkPoster();
            // A click beside it closes it.
            scene.MoveTo(Middle(preview) + new Avalonia.Vector(0, 205), 0.45);
            scene.Click();
            scene.Hold(0.2);

            // Softness: the preview donut's slices round off and sharpen.
            scene.MoveTo(Thumb(softness), 0.45);
            scene.Grab(true);
            foreach (var (to, seconds) in new[] { (28.0, 0.35), (2.0, 0.7), (20.0, 0.45) })
                Slide(scene, page.CornerRadius, to, seconds, v => page.CornerRadius = Math.Round(v), () => Thumb(softness));
            scene.Grab(false);
            scene.Hold(0.3);

            // The donut toolbar's opacity: the backdrop shows through the slices.
            scene.MoveTo(Thumb(opacity), 0.4);
            scene.Grab(true);
            Slide(scene, page.DonutOpacity, 45, 0.8, v => page.DonutOpacity = Math.Round(v), () => Thumb(opacity));
            scene.Grab(false);
            scene.Hold(1.4);
        }
        finally
        {
            window.Close();
            vm.Dispose();
            (store.Settings.Theme, store.Settings.Donut.Opacity) = (themeBefore, opacityBefore);
            theme.Apply(new ThemeSettings { ReduceMotion = false, Mode = ThemeMode.Light }, AppHost.Get<ILocalizer>().Language);
        }
        return clip;

        static void Slide(Scene scene, double from, double to, double seconds, Action<double> set, Func<Avalonia.Point> thumb)
        {
            var frames = Math.Max(1, (int)Math.Round(seconds / scene.FrameLength));
            for (var i = 1; i <= frames; i++)
            {
                set(from + (to - from) * Scene.EaseInOut(i / (double)frames));
                scene.Tick(thumb);
            }
        }
    }

    /// <summary>The Home page in one language after another.</summary>
    private static Clip Languages(string version)
    {
        var clip = new Clip();
        foreach (var language in AppLanguages.All.Select(l => l.Code))
        {
            TestServices.EnsureInitialized(language);
            AppHost.Get<ThemeService>().Apply(new ThemeSettings { ReduceMotion = false, Mode = ThemeMode.Light }, language);
            var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>(), AppHost.Get<UserDataStore>());
            vm.Navigate("home");
            var window = new MainWindow { DataContext = vm, Width = 960, Height = 540 };
            window.Show();
            Pump();
            clip.Add(Capture(window, version), 1100);
            window.Close();
            vm.Dispose();
        }
        TestServices.EnsureInitialized("en");
        return clip;
    }

    /// <summary>
    /// "And more": the history search finding an old conversion as it is typed, then "Reset to default" beside a
    /// built-in donut on the Toolbar page.
    /// </summary>
    private static Clip More(string version)
    {
        var clip = new Clip();
        var store = AppHost.Get<UserDataStore>();
        var loc = AppHost.Get<ILocalizer>();
        var report = loc.Language.StartsWith("pt-", StringComparison.Ordinal) ? "relatório" : "report";
        var work = OperatingSystem.IsWindows() ? @"C:\Work" : OperatingSystem.IsMacOS() ? "/Users/demo/Documents" : "/home/demo/Documents";
        var photos = OperatingSystem.IsWindows() ? @"C:\Photos" : OperatingSystem.IsMacOS() ? "/Users/demo/Pictures" : "/home/demo/Pictures";
        store.ClearHistory();
        store.History.AddRange(Enumerable.Range(0, store.Settings.EffectiveHistoryLimit).Select(i => new Filee.Core.History.HistoryEntry
        {
            FinishedAt = DateTime.Now.AddMinutes(-7 * i - 3),
            PresetName = i == 150 ? "PDF" : i % 3 == 0 ? "JPG" : "PNG",
            State = Filee.Core.Conversion.JobState.Completed,
            Sources = i == 150 ? [Path.Combine(work, $"{report}.docx")] : [Path.Combine(photos, $"IMG_{4000 + i:0000}.heic")],
        }));
        var profiles = store.Profiles.Select(p => p.Clone()).ToList();
        try
        {
            var vm = new MainWindowViewModel(AppHost.Services, loc, AppHost.Get<UpdateService>(), AppHost.Get<UserDataStore>());
            vm.Navigate("home");
            var window = new MainWindow { DataContext = vm, Width = 960, Height = 540 };
            window.Show();
            Pump();
            var home = (HomePageViewModel)vm.CurrentPage!;
            // Frame 0: the old report found.
            home.Search = report;
            Pump();
            clip.Add(Capture(window, version), 1400);
            home.Search = "";
            Pump();
            clip.Add(Capture(window, version), 700);
            for (var length = 1; length <= report.Length; length++)
            {
                home.Search = report[..length];
                Pump();
                clip.Add(Capture(window, version), 160);
            }
            clip.Add(Capture(window, version), 1400);

            // A tall window shows the whole profile card; the clip shows its lower part, down to "Reset to default".
            vm.Navigate("toolbar");
            window.Width = 1240;
            window.Height = 1700;
            var page = (ToolbarPageViewModel)vm.CurrentPage!;
            page.SelectedProfile = page.Profiles.Single(p => p.Profile.Id == "images");
            Pump();
            var actions = window.GetVisualDescendants().OfType<StackPanel>().Single(s => s.Name == "ProfileActions");
            var card = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ProfileCard");
            var cardAt = Avalonia.VisualExtensions.TranslatePoint(card, default, window)!.Value;
            var actionsAt = Avalonia.VisualExtensions.TranslatePoint(actions, default, window)!.Value;
            var bottom = actionsAt.Y + actions.Bounds.Height + 28;
            var region = new Avalonia.Rect(Math.Max(0, cardAt.X - 8), Math.Max(0, bottom - 540), 960, 540);
            clip.Add(Capture(window, version, region), 2200);
            window.Close();
            vm.Dispose();
        }
        finally
        {
            store.ClearHistory();
            store.Profiles.Clear();
            store.Profiles.AddRange(profiles);
        }
        return clip;
    }

    // ───────────────────────── Scenes of 2.3.0 ─────────────────────────

    /// <summary>
    /// Annotate: a web page screenshot marked up with the tools of the bar, each picked with the pointer: a
    /// highlight over the title, a box round the button, an arrow to it, a label typed in place and two numbers.
    /// </summary>
    private static Clip Annotate()
    {
        var clip = new Clip { Quality = 74 };
        var folder = Directory.CreateTempSubdirectory("filee-whatsnew-annotate-").FullName;
        var store = AppHost.Get<UserDataStore>();
        var tool = store.FindPreset(BuiltInData.AnnotateId) ?? BuiltInData.AnnotatePresets().Single();
        AnnotateTool.Remember(tool, AnnotateTool.Colors[0], AnnotationWidth.Medium);
        AnnotateWindow? window = null;
        try
        {
            var picture = Path.Combine(folder, "lisbon.png");
            DrawWebPage(picture);
            var vm = new AnnotateViewModel([picture], tool, AppHost.Get<ILocalizer>(), store, _ => BuiltInData.SameAsSource, (_, _) => { });
            window = new AnnotateWindow { DataContext = vm, Width = 1024, Height = 600 };
            window.Show();
            WaitFor(() => vm.PreviewImage is not null && !vm.IsBusy);
            Pump();

            var bounds = window.AnnotationCanvas.CalculateImageBounds();
            Avalonia.Point On(double x, double y) =>
                window.AnnotationCanvas.TranslatePoint(new Avalonia.Point(bounds.X + x * bounds.Width, bounds.Y + y * bounds.Height), window)!.Value;
            Avalonia.Point Middle(Control control) =>
                control.TranslatePoint(new Avalonia.Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
            Control Named(string name) => window.GetVisualDescendants().OfType<Control>().First(c => c.Name == name);
            Avalonia.Rect InWindow(Control control) => new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);

            // The picture and the bar under it, closer than the whole window shows them (the file name above and the
            // keys below are left out).
            var shown = new Avalonia.Rect(On(0, 0), On(1, 1)).Union(InWindow(Named("SelectTool"))).Union(InWindow(Named("SaveButton"))).Inflate(14);
            var height = Math.Max(shown.Height, shown.Width * CanvasHeight / CanvasWidth);
            var region = new Avalonia.Rect(shown.Center.X - height * CanvasWidth / CanvasHeight / 2, shown.Center.Y - height / 2,
                height * CanvasWidth / CanvasHeight, height);
            var scene = new Scene(window, clip) { FrameLength = 1 / 15.0, Region = region };
            void Pick(string name)
            {
                scene.MoveTo(Middle(Named(name)) + new Avalonia.Vector(3, 4), 0.45);
                scene.Hold(0.1);
                scene.Click();
                scene.Hold(0.15);
            }
            void Swatch(int index)
            {
                var swatch = window.GetVisualDescendants().OfType<RadioButton>().Where(b => b.Classes.Contains("swatch")).ElementAt(index);
                scene.MoveTo(Middle(swatch) + new Avalonia.Vector(2, 3), 0.35);
                scene.Click();
                scene.Hold(0.15);
            }
            void Drag((double X, double Y) from, (double X, double Y) to, double seconds)
            {
                scene.MoveTo(On(from.X, from.Y), 0.4);
                scene.Press();
                scene.MoveTo(On(to.X, to.Y), seconds);
                scene.Release();
                scene.Hold(0.25);
            }

            // Frame 0: the picture open in the window, nothing drawn yet.
            scene.Place(On(0.45, 0.42));
            scene.Hold(0.6);

            // A highlight over the title, in yellow.
            Pick("HighlightTool");
            Swatch(2);
            Drag((0.565, 0.16), (0.905, 0.225), 0.6);

            // A box round the button, in red.
            Pick("BoxTool");
            Swatch(0);
            Drag((0.558, 0.505), (0.776, 0.632), 0.6);

            // An arrow pointing at it.
            Pick("ArrowTool");
            Drag((0.375, 0.775), (0.545, 0.62), 0.5);

            // A label typed where it goes.
            Pick("TextTool");
            scene.MoveTo(On(0.2, 0.81), 0.4);
            scene.Click();
            WaitFor(() => window.Editor.IsVisible);
            scene.Hold(0.2);
            foreach (var letter in "Book this one")
            {
                window.KeyTextInput(letter.ToString());
                scene.Tick();
                if (letter == ' ')
                    scene.Tick();
            }
            scene.Hold(0.3);
            scene.Key("Enter", () => window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null));
            window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            scene.Hold(0.3);
            scene.HideKey();

            // Numbers count up: 1 on the picture, 2 on the price.
            Pick("NumberTool");
            foreach (var (x, y) in new[] { (0.07, 0.19), (0.553, 0.405) })
            {
                scene.MoveTo(On(x, y), 0.45);
                scene.Click();
                scene.Hold(0.3);
            }

            scene.MoveTo(On(0.47, 0.5), 0.5);
            scene.Hold(0.2);
            clip.MarkPoster();
            scene.Hold(2.0);
            Assert.Equal(6, vm.Marks.Count);
        }
        finally
        {
            window?.Close();
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException) { }
        }
        return clip;
    }

    /// <summary>
    /// Crop video (#830): a film with black bars, dragged onto the video donut; Tab turns it to its back face, the
    /// "Crop video" slice fans out its ring and the film is dropped on "Auto" (remove black bars); then the frame loses
    /// its bars.
    /// </summary>
    private static Clip CropVideo()
    {
        var clip = new Clip { Quality = 76 };
        var folder = Directory.CreateTempSubdirectory("filee-whatsnew-video-").FullName;
        try
        {
            // A 2.39:1 film in a 16:9 frame.
            var still = Path.Combine(folder, "still.jpg");
            DrawLandscape(still, 2);
            using var picture = new MagickImage(still);
            picture.Crop(new MagickGeometry(0, 140, 1600, 670));
            picture.ResetPage();
            picture.Resize(new MagickGeometry(544, 228) { IgnoreAspectRatio = true });
            var bars = (306 - picture.Height) / 2.0; // 544 × 306 is 16:9

            // Frame 0: the film with its bars.
            Film(clip, picture, bars, 0, 1100);

            using (var drag = new DonutDrag(clip, [@"C:\Videos\trailer.mp4"]))
            {
                drag.Open(drag.Slice(1, 0.55));
                drag.Flip();
                var group = drag.Index("crop-video");
                drag.Scene.MoveTo(drag.Slice(group, 0.6), 0.4);
                drag.Scene.MoveTo(drag.Slice(group, 0.93), 0.25);
                drag.Scene.Hold(0.3);
                drag.Scene.MoveTo(drag.Sub(group, 0) + new Avalonia.Vector(-2, 10), 0.5);
                drag.Scene.Hold(0.5);
                Assert.Equal(new DonutHit(DonutHitKind.SubSegment, group, 0), drag.Hit);
                // Shown with reduced motion: the ring of ratios, the drop on Auto.
                clip.MarkPoster();
                drag.Scene.Hold(0.6);
                drag.Drop(0.8, 0.6);
            }

            // The bars found are marked and cut away.
            Film(clip, picture, bars, 0, 300);
            for (var t = 1; t <= 6; t++)
                Film(clip, picture, bars, t / 6.0, 50);
            clip.Hold(400);
            for (var t = 1; t <= 14; t++)
                Film(clip, picture, bars * (1 - Scene.EaseInOut(t / 14.0)), 1 - t / 14.0, 50);
            clip.Hold(2000);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException) { }
        }
        return clip;

        // The frame of a video with a play bar under it: the picture with black bars of <bars> above and below, and a
        // dashed line round the picture <outline> (0 to 1) visible.
        static void Film(Clip clip, MagickImage picture, double bars, double outline, int milliseconds)
        {
            var (width, height) = ((double)picture.Width, picture.Height + 2 * bars);
            var (left, top) = ((CanvasWidth - width) / 2, (CanvasHeight - height) / 2 - 12);
            var canvas = new MagickImage(new MagickColor(Page), CanvasWidth, CanvasHeight);
            new Drawables()
                .FillColor(new MagickColor("#00000014")).RoundRectangle(left - 2, top + 3, left + width + 2, top + height + 8, 12, 12)
                .FillColor(new MagickColor("#0B0B0C")).RoundRectangle(left, top, left + width, top + height, 10, 10)
                .Draw(canvas);
            canvas.Composite(picture, (int)Math.Round(left), (int)Math.Round(top + bars), CompositeOperator.Over);
            var barTop = top + height + 22;
            var drawing = new Drawables()
                .FillColor(new MagickColor("#1F1E1D")).Polygon(new PointD(left + 4, barTop - 7), new PointD(left + 4, barTop + 7), new PointD(left + 16, barTop))
                .FillColor(new MagickColor("#DAD7CF")).RoundRectangle(left + 32, barTop - 3, left + width, barTop + 3, 3, 3)
                .FillColor(new MagickColor("#7F77DD")).RoundRectangle(left + 32, barTop - 3, left + 220, barTop + 3, 3, 3)
                .FillColor(new MagickColor("#7F77DD")).Circle(left + 220, barTop, left + 227, barTop);
            if (outline > 0)
                drawing
                    .FillColor(MagickColors.Transparent).StrokeColor(new MagickColor(255, 255, 255, (byte)Math.Round(255 * outline)))
                    .StrokeWidth(2).StrokeDashArray(9, 6)
                    .Rectangle(left + 1.5, top + bars + 1.5, left + width - 1.5, top + bars + picture.Height - 1.5);
            drawing.Draw(canvas);
            clip.Add(canvas, milliseconds);
        }
    }

    /// <summary>
    /// Pages per sheet (#737): an eight-page PDF dragged onto the PDF donut, the "Pages per sheet" slice fanning out
    /// 2, 4, 6, 8 and 9, the drop on 4; then the pages going onto their two sheets. The sheets are the PdfSheets output
    /// for that PDF, rendered; the pages fly to where it put them.
    /// </summary>
    private static Clip Sheets()
    {
        var clip = new Clip { Quality = 78 };
        var folder = Directory.CreateTempSubdirectory("filee-whatsnew-sheets-").FullName;
        // Pages per sheet is in the palette, on no donut: put on the PDF donut's front for the clip.
        var pdf = AppHost.Get<UserDataStore>().Profiles.Single(p => p.Id == "pdf");
        var front = pdf.PresetIds.ToList();
        pdf.PresetIds[pdf.PresetIds.IndexOf("to-hwp")] = "pdf-nup";
        var pages = new List<MagickImage>();
        var sheets = new List<MagickImage>();
        try
        {
            var source = Path.Combine(folder, "chapters.pdf");
            var output = Path.Combine(folder, "chapters_4.pdf");
            DrawDocument(source, folder);
            var options = BuiltInData.CreatePresets().Single(p => p.Id == "pdf-nup-4").Pdf;
            Filee.Engines.Pdf.PdfSheets.Write(source, output, options, null, CancellationToken.None);
            var input = Filee.Engines.Pdf.PdfPreviewDocument.Open(source);
            var result = Filee.Engines.Pdf.PdfPreviewDocument.Open(output);
            Assert.Equal((8, 2), (input.PageCount, result.PageCount));
            pages.AddRange(Enumerable.Range(1, 8).Select(n => new MagickImage(input.RenderPage(n, 480).Png)));
            sheets.AddRange(Enumerable.Range(1, 2).Select(n => new MagickImage(result.RenderPage(n, 720).Png)));

            // Where everything goes on the canvas: the pages in a row, the two sheets side by side, and in the sheets
            // the cells PdfSheets drew the pages in.
            var (pageWidth, pageHeight) = (84.0, 84.0 * pages[0].Height / pages[0].Width);
            var row = Enumerable.Range(0, 8).Select(k => new Avalonia.Rect(
                (CanvasWidth - (8 * pageWidth + 7 * 9)) / 2 + k * (pageWidth + 9), (CanvasHeight - pageHeight) / 2, pageWidth, pageHeight)).ToList();
            var sheetHeight = 356.0;
            var sheetWidth = sheetHeight * sheets[0].Width / sheets[0].Height;
            var sheetAt = Enumerable.Range(0, 2).Select(k => new Avalonia.Rect(
                (CanvasWidth - (2 * sheetWidth + 44)) / 2 + k * (sheetWidth + 44), (CanvasHeight - sheetHeight) / 2, sheetWidth, sheetHeight)).ToList();
            var a4 = new PdfSharp.Drawing.XSize(595.28, 841.89);
            var cells = Filee.Engines.Pdf.PdfSheets.Cells(a4, options, 4);
            var targets = Enumerable.Range(0, 8).Select(k =>
            {
                var (sheet, cell) = (sheetAt[k / 4], cells[k % 4]);
                var scale = sheet.Width / a4.Width;
                var fit = Math.Min(cell.Width / pageWidth, cell.Height / pageHeight) * scale;
                var (w, h) = (pageWidth * fit, pageHeight * fit);
                return new Avalonia.Rect(sheet.X + (cell.X + cell.Width / 2) * scale - w / 2, sheet.Y + (cell.Y + cell.Height / 2) * scale - h / 2, w, h);
            }).ToList();

            // Frame 0: the eight pages.
            clip.Add(Layout(pages, row, null, 0), 1100);

            using (var drag = new DonutDrag(clip, [@"C:\Documents\chapters.pdf"]))
            {
                var group = drag.Index("pdf-nup");
                drag.Open(drag.Slice(group, 0.45));
                drag.Scene.MoveTo(drag.Slice(group, 0.93), 0.3);
                drag.Scene.Hold(0.3);
                drag.Scene.MoveTo(drag.Sub(group, 1) + new Avalonia.Vector(0, 6), 0.5);
                drag.Scene.Hold(0.9);
                Assert.Equal(new DonutHit(DonutHitKind.SubSegment, group, 1), drag.Hit);
                drag.Drop(0.8, 0.6);
            }

            // The pages fly onto the sheets, one after another, as the sheets come in under them.
            clip.Add(Layout(pages, row, null, 0), 400);
            const int frames = 22;
            for (var f = 1; f <= frames; f++)
            {
                var places = row.Select((from, k) =>
                {
                    var u = Scene.EaseInOut(Math.Clamp((f / (double)frames * 1.6) - k * 0.075, 0, 1));
                    var to = targets[k];
                    return new Avalonia.Rect(from.X + (to.X - from.X) * u, from.Y + (to.Y - from.Y) * u,
                        from.Width + (to.Width - from.Width) * u, from.Height + (to.Height - from.Height) * u);
                }).ToList();
                clip.Add(Layout(pages, places, sheetAt, Math.Min(1, f / (frames * 0.4))), 50);
            }
            // The sheets as PdfSheets wrote them.
            var final = new MagickImage(new MagickColor(Page), CanvasWidth, CanvasHeight);
            for (var k = 0; k < 2; k++)
            {
                DrawPaper(final, sheetAt[k], 1);
                using var sheet = (MagickImage)sheets[k].Clone();
                sheet.FilterType = FilterType.Lanczos;
                sheet.Resize((uint)Math.Round(sheetAt[k].Width), (uint)Math.Round(sheetAt[k].Height));
                final.Composite(sheet, (int)Math.Round(sheetAt[k].X), (int)Math.Round(sheetAt[k].Y), CompositeOperator.Over);
            }
            clip.Add(final, 2400);
            // Shown with reduced motion: the two sheets.
            clip.MarkPoster();
        }
        finally
        {
            pdf.PresetIds = front;
            foreach (var image in pages.Concat(sheets))
                image.Dispose();
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException) { }
        }
        return clip;

        // The pages at <places>, over the sheets at <sheetAt> (<paper> 0 to 1 of the way in).
        static MagickImage Layout(List<MagickImage> pages, List<Avalonia.Rect> places, List<Avalonia.Rect>? sheetAt, double paper)
        {
            var canvas = new MagickImage(new MagickColor(Page), CanvasWidth, CanvasHeight);
            if (sheetAt is not null && paper > 0)
                foreach (var sheet in sheetAt)
                    DrawPaper(canvas, sheet, paper);
            for (var k = 0; k < pages.Count; k++)
            {
                var at = places[k];
                using var page = (MagickImage)pages[k].Clone();
                page.FilterType = FilterType.Lanczos;
                page.Resize((uint)Math.Round(at.Width), (uint)Math.Round(at.Height));
                if (sheetAt is null)
                    new Drawables().FillColor(new MagickColor("#0000001A"))
                        .RoundRectangle(at.X - 1, at.Y + 1, at.Right + 1, at.Bottom + 3, 3, 3).Draw(canvas);
                canvas.Composite(page, (int)Math.Round(at.X), (int)Math.Round(at.Y), CompositeOperator.Over);
            }
            return canvas;
        }

        // A sheet of paper with a soft shadow, <opacity> 0 to 1.
        static void DrawPaper(MagickImage canvas, Avalonia.Rect sheet, double opacity)
        {
            var alpha = (byte)Math.Round(255 * opacity);
            new Drawables()
                .FillColor(new MagickColor(0, 0, 0, (byte)Math.Round(0.08 * alpha))).RoundRectangle(sheet.X - 2, sheet.Y + 2, sheet.Right + 2, sheet.Bottom + 6, 6, 6)
                .FillColor(new MagickColor(255, 255, 255, alpha)).Rectangle(sheet.X, sheet.Y, sheet.Right, sheet.Bottom)
                .Draw(canvas);
        }
    }

    /// <summary>
    /// Settings → Fields (#806): "Design &amp; print" and then "Games" switched off on the page, and the pictures donut
    /// beside it opening again without their slices (PSD and EPS, then DDS). The page gets real pointer input; the donut
    /// is the one the next drop opens, played on the clip's clock.
    /// </summary>
    private static Clip Fields()
    {
        var clip = new Clip { Quality = 78 };
        var store = AppHost.Get<UserDataStore>();
        var loc = AppHost.Get<ILocalizer>();
        var turnedOff = store.Settings.TurnedOffFields.ToList();
        var images = store.Profiles.Single(p => p.Id == "images");
        var front = images.PresetIds.ToList();
        // Someone who works in design and games: PSD, EPS and DDS on the pictures donut, those fields on.
        Preset[] added =
        [
            new() { Id = "whatsnew-psd", Name = "PSD", TargetFormat = "psd" },
            new() { Id = "whatsnew-eps", Name = "EPS", TargetFormat = "eps" },
            new() { Id = "whatsnew-dds", Name = "DDS", TargetFormat = "dds" },
        ];
        store.Presets.AddRange(added);
        images.PresetIds = ["to-png", "to-jpg", "to-webp", "whatsnew-psd", "whatsnew-eps", "whatsnew-dds", "to-pdf", "trace-svg"];
        store.Settings.TurnedOffFields = Filee.Core.Formats.FormatFields.All
            .Where(f => f.CanTurnOff && !f.OnByDefault && f.Id is not ("design" or "game")).Select(f => f.Id).ToList();
        string[] files = [@"C:\Pictures\poster.png"];
        var page = new Window
        {
            Width = 470,
            Height = 1100,
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(Page)),
            Content = new Border { Padding = new Avalonia.Thickness(24), Child = new FieldsPage { DataContext = new FieldsPageViewModel(store, loc) } },
        };
        var (donutWindow, vm) = OpenDonut(files, clickMode: true);
        var donut = donutWindow.DonutControl;
        try
        {
            page.Show();
            Pump();
            ToolTip.SetServiceEnabled(page, false);
            Assert.Equal(8, vm.Items.Count);
            var switches = page.GetVisualDescendants().OfType<ToggleSwitch>().ToList();
            ToggleSwitch Switch(string id) => switches.Single(s => Avalonia.Automation.AutomationProperties.GetName(s) == loc[$"fields.name.{id}"]);
            Avalonia.Rect InPage(Control control) => new(control.TranslatePoint(default, page)!.Value, control.Bounds.Size);

            // The card from its top down to the Developer row.
            var card = InPage(page.GetVisualDescendants().OfType<Border>().First(x => x.Classes.Contains("card")));
            var developer = Switch("developer").FindAncestorOfType<Grid>()!.GetVisualDescendants().OfType<TextBlock>().Max(t => InPage(t).Bottom);
            var region = new Avalonia.Rect(card.X - 8, card.Y - 8, card.Width + 16, developer - card.Y + 18);
            const double donutSide = 344;
            var pageScale = Math.Min(1, Math.Min(404 / region.Width, (CanvasHeight - 12) / region.Height));
            var pageAt = new Avalonia.Point(18, (CanvasHeight - region.Height * pageScale) / 2);
            var design = InPage(Switch("design"));
            var pointer = new Avalonia.Point(design.X - 120, design.Y + 40);
            var pressed = false;

            MagickImage Frame()
            {
                var canvas = new MagickImage(new MagickColor(Page), CanvasWidth, CanvasHeight);
                // Drawn directly: a frame of the render timer would also run the donut's frame loop.
                using (var shot = Snapshot(page, direct: true))
                {
                    shot.Crop(new MagickGeometry((int)region.X, (int)region.Y, (uint)region.Width, (uint)region.Height));
                    shot.ResetPage();
                    shot.FilterType = FilterType.Lanczos;
                    shot.Resize((uint)Math.Round(region.Width * pageScale), (uint)Math.Round(region.Height * pageScale));
                    canvas.Composite(shot, (int)pageAt.X, (int)Math.Round(pageAt.Y), CompositeOperator.Over);
                }
                using (var shot = Snapshot(donutWindow))
                {
                    var scale = Math.Min(1, donutSide / shot.Width);
                    shot.FilterType = FilterType.Lanczos;
                    shot.Resize((uint)Math.Round(shot.Width * scale), (uint)Math.Round(shot.Height * scale));
                    canvas.Composite(shot, (int)(CanvasWidth - 12 - donutSide + (donutSide - shot.Width) / 2), (int)((CanvasHeight - shot.Height) / 2), CompositeOperator.Over);
                }
                DrawPointer(canvas, new PointD(pageAt.X + (pointer.X - region.X) * pageScale, pageAt.Y + (pointer.Y - region.Y) * pageScale), pressed);
                return canvas;
            }
            // The render timer plays the switch's transition, but also the donut's own frame loop on wall-clock time: it
            // runs only while the donut is at rest.
            void Tick(double seconds = 0.05, bool renderTimer = true)
            {
                for (var t = 0.0; t < seconds - 1e-9; t += 0.05)
                {
                    KeepFramesComing(donut);
                    donut.Advance(0.05);
                    KeepFramesComing(donut);
                    for (var i = 0; i < 3; i++)
                    {
                        Dispatcher.UIThread.RunJobs();
                        if (renderTimer)
                            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    }
                    clip.Add(Frame(), 50);
                }
            }
            void MoveTo(Avalonia.Point to, double seconds)
            {
                var from = pointer;
                var frames = Math.Max(1, (int)Math.Round(seconds / 0.05));
                var normal = new Avalonia.Vector(from.Y - to.Y, to.X - from.X) * 0.06;
                for (var i = 1; i <= frames; i++)
                {
                    var u = Scene.EaseInOut(i / (double)frames);
                    pointer = new Avalonia.Point(from.X + (to.X - from.X) * u, from.Y + (to.Y - from.Y) * u) + normal * Math.Sin(Math.PI * u);
                    page.MouseMove(pointer);
                    Tick();
                }
            }
            // A switch clicked off, and the donut opening again without that field's slices.
            void TurnOff(string id, int slicesLeft)
            {
                var toggle = Switch(id);
                var at = InPage(toggle);
                MoveTo(new Avalonia.Point(at.X + 14, at.Center.Y + 2), 0.6);
                Tick(0.15);
                page.MouseDown(pointer, MouseButton.Left);
                pressed = true;
                Tick();
                page.MouseUp(pointer, MouseButton.Left);
                pressed = false;
                Assert.Contains(id, store.Settings.TurnedOffFields);
                Tick(0.35);
                vm.Load(files, clickMode: true);
                Assert.Equal(slicesLeft, vm.Items.Count);
                Dispatcher.UIThread.RunJobs(); // the donut takes the new slices before it opens
                donut.PlayOpenAnimation();
                Tick(1.2, renderTimer: false);
            }

            // Frame 0: Design & print and Games on, PSD, EPS and DDS on the donut.
            page.MouseMove(pointer);
            donut.FinishAnimations();
            Settle(donut);
            clip.Add(Frame(), 1300);
            TurnOff("design", 6);
            TurnOff("game", 5);
            clip.MarkPoster();
            clip.Hold(1600);
        }
        finally
        {
            page.Close();
            donutWindow.Close();
            store.Settings.TurnedOffFields = turnedOff;
            images.PresetIds = front;
            foreach (var preset in added)
                store.Presets.Remove(preset);
        }
        return clip;
    }

    /// <summary>
    /// Captions and translation with a local AI: a video dropped on the "Caption" slice, its SRT written
    /// cue by cue, then the same cues translated with their timings kept. Whisper and Ollama can't run here, so the
    /// cues are written out; the cards use the app's own styles (card, h2, caption).
    /// </summary>
    private static Clip Translate()
    {
        var clip = new Clip { Quality = 80 };
        (string Time, string English, string Korean)[] cues =
        [
            ("00:00:01,200 --> 00:00:03,800", "Welcome back to the workshop.", "워크숍에 다시 오신 걸 환영해요."),
            ("00:00:04,100 --> 00:00:07,000", "Today we turn old photos into prints.", "오늘은 오래된 사진을 인쇄해 볼게요."),
            ("00:00:07,400 --> 00:00:10,200", "Let's start with the scanner.", "먼저 스캐너부터 시작해요."),
        ];
        var loc = AppHost.Get<ILocalizer>();
        var blocks = new List<(Control Block, bool Translated)>();
        Control Card(string name, bool translated)
        {
            var list = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(0, 12, 0, 0) };
            for (var i = 0; i < cues.Length; i++)
            {
                var block = new StackPanel { Spacing = 1 };
                block.Children.Add(new TextBlock { Classes = { "caption" }, Text = $"{i + 1}" });
                block.Children.Add(new TextBlock { Classes = { "caption" }, Text = cues[i].Time });
                block.Children.Add(new TextBlock { FontSize = 16, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Text = translated ? cues[i].Korean : cues[i].English });
                list.Children.Add(block);
                blocks.Add((block, translated));
            }
            return new Border
            {
                Classes = { "card" },
                Padding = new Avalonia.Thickness(20, 16),
                Width = 316,
                Child = new StackPanel { Children = { new TextBlock { Classes = { "h2" }, Text = name }, list } },
            };
        }
        var arrow = new StackPanel
        {
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(14, 0),
            Spacing = 6,
            Children =
            {
                new Border
                {
                    Background = (Avalonia.Media.IBrush)Avalonia.Application.Current!.FindResource("AccentBrush")!,
                    CornerRadius = new Avalonia.CornerRadius(12),
                    Padding = new Avalonia.Thickness(10, 4),
                    Child = new TextBlock { Text = loc["preset.translate"], Foreground = Avalonia.Media.Brushes.White, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                },
                new TextBlock { Text = "→", FontSize = 26, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center },
            },
        };
        var window = new Window
        {
            Width = CanvasWidth,
            Height = CanvasHeight,
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(Page)),
            Content = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Children = { Card("interview.srt", false), arrow, Card("interview_ko.srt", true) },
            },
        };
        try
        {
            window.Show();
            Pump();
            // Frame 0 (reduced motion), and the frame the clip comes back to: both files written.
            clip.Add(Capture(window), 1400);

            // As someone with Whisper installed sees it.
            using (var drag = new DonutDrag(clip, [@"C:\Videos\interview.mp4"], vm => vm.Items = vm.Items.Select(Enabled).ToList()))
            {
                var caption = drag.Index("caption-video");
                drag.Open(drag.Slice(caption, 0.6));
                drag.Scene.Hold(0.8);
                Assert.Equal(new DonutHit(DonutHitKind.Segment, caption), drag.Hit);
                drag.Drop(0.8, 0.5);
            }

            // The captions come in cue by cue, then their translation; the arrow shows between them.
            void Show(Func<int, bool, double> shown)
            {
                for (var i = 0; i < blocks.Count; i++)
                {
                    var amount = shown(i % cues.Length, blocks[i].Translated);
                    blocks[i].Block.Opacity = amount;
                    blocks[i].Block.RenderTransform = new Avalonia.Media.TranslateTransform(0, 8 * (1 - amount));
                }
                Pump();
            }
            double Ease(double t) => Scene.EaseInOut(Math.Clamp(t, 0, 1));
            arrow.Opacity = 0;
            Show((_, _) => 0);
            clip.Add(Capture(window), 300);
            for (var f = 1; f <= 24; f++)
            {
                Show((cue, translated) => translated ? 0 : Ease(f / 6.0 - cue * 1.2));
                clip.Add(Capture(window), 50);
            }
            clip.Hold(300);
            for (var f = 1; f <= 6; f++)
            {
                arrow.Opacity = f / 6.0;
                Pump();
                clip.Add(Capture(window), 50);
            }
            for (var f = 1; f <= 24; f++)
            {
                Show((cue, translated) => translated ? Ease(f / 6.0 - cue * 1.2) : 1);
                clip.Add(Capture(window), 50);
            }
            clip.Hold(2000);
        }
        finally
        {
            window.Close();
        }
        return clip;
    }

    /// <summary>
    /// "And more": Move to folder and Copy to folder slices on the back of the pictures donut, the perspective crop
    /// straightening a photographed page, the donut of a Krita file, and the Home page in Italian and in Indonesian.
    /// </summary>
    private static Clip AndMore()
    {
        var clip = new Clip { Quality = 80 };
        var store = AppHost.Get<UserDataStore>();
        var loc = AppHost.Get<ILocalizer>();
        var images = store.Profiles.Single(p => p.Id == "images");
        var back = images.BackPresetIds?.ToList();
        Preset Folder(string id, string name, string path, bool move) => new()
        {
            Id = id,
            Name = name,
            Kind = PresetKind.CopyToFolder,
            TargetFormat = Preset.CopyTarget,
            Output = new OutputRule { Location = OutputLocation.CustomFolder, CustomFolder = path, MoveInsteadOfCopy = move },
        };
        Preset[] added =
        [
            Folder("whatsnew-projects", "Projects", @"C:\Projects", move: true),
            Folder("whatsnew-references", "References", @"C:\References", move: false),
        ];
        store.Presets.AddRange(added);
        images.BackPresetIds = ["tool-crop", "rotate", "remove-background", "whatsnew-projects", "whatsnew-references", "tool-image-edit", "zip-all"];
        var folder = Directory.CreateTempSubdirectory("filee-whatsnew-more-").FullName;
        var clock = System.Diagnostics.Stopwatch.GetTimestamp();
        CropCanvas.Clock = () => clock;
        try
        {
            // Frame 0: the folders on the back face, the pointer on Projects (Move to folder).
            using (var donut = new DonutDrag(clip, [@"C:\Pictures\sketch.png"], clickMode: true, backFace: true))
            {
                donut.Donut.FinishAnimations();
                var (projects, references) = (donut.Index("whatsnew-projects"), donut.Index("whatsnew-references"));
                donut.Scene.Place(donut.Slice(projects, 0.9));
                donut.Aim();
                Settle(donut.Donut);
                donut.Scene.Tick();
                clip.Hold(1000);
                donut.Scene.MoveTo(donut.Slice(references, 0.9), 0.45);
                donut.Scene.Hold(1.2);
            }

            // Perspective crop: the corners go onto a page photographed at an angle.
            var photo = Path.Combine(folder, "notes.jpg");
            var page = DrawTiltedPage(photo);
            var preset = BuiltInData.CreatePresets().Single(p => p.Id == "tool-crop");
            var vm = new CropOverlayViewModel([photo], preset, loc, store);
            var window = new CropOverlayWindow
            {
                DataContext = vm,
                Width = 889,
                Height = 500,
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(Page)),
            };
            try
            {
                window.Show();
                WaitFor(() => vm.PreviewImage is not null && !vm.IsBusy);
                window.Classes.Set("reduced-motion", true);
                vm.IsPerspective = true;
                Pump();
                var canvas = window.CropCanvas;
                Assert.True(canvas.IsPerspective);
                clip.Add(Capture(window), 500);
                var from = canvas.NormalizedCorners;
                static Avalonia.Point Toward(Avalonia.Point a, Avalonia.Point b, double u) => new(a.X + (b.X - a.X) * u, a.Y + (b.Y - a.Y) * u);
                for (var f = 1; f <= 12; f++)
                {
                    var u = Scene.EaseInOut(f / 12.0);
                    canvas.NormalizedCorners = new CropQuad(Toward(from.TopLeft, page.TopLeft, u), Toward(from.TopRight, page.TopRight, u),
                        Toward(from.BottomRight, page.BottomRight, u), Toward(from.BottomLeft, page.BottomLeft, u));
                    clock += System.Diagnostics.Stopwatch.Frequency / 20;
                    Pump();
                    clip.Add(Capture(window), 50);
                }
                clip.Hold(1500);
            }
            finally
            {
                window.Close();
            }

            // A new format: a Krita picture.
            var (krita, kritaVm) = OpenDonut([@"C:\Art\castle.kra"], clickMode: true);
            try
            {
                krita.DonutControl.PlayOpenAnimation();
                Run(clip, krita, 0.4);
                Highlight(krita, kritaVm, 0);
                clip.Add(Capture(krita), 1400);
            }
            finally
            {
                krita.Close();
            }

            // Two new languages.
            foreach (var language in new[] { "it", "id" })
            {
                TestServices.EnsureInitialized(language);
                AppHost.Get<ThemeService>().Apply(new ThemeSettings { ReduceMotion = false, Mode = ThemeMode.Light }, language);
                var main = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>(), AppHost.Get<UserDataStore>());
                main.Navigate("home");
                var home = new MainWindow { DataContext = main, Width = 960, Height = 540 };
                home.Show();
                Pump();
                clip.Add(Capture(home, "2.3.0"), 1400);
                home.Close();
                main.Dispose();
            }
        }
        finally
        {
            CropCanvas.Clock = System.Diagnostics.Stopwatch.GetTimestamp;
            TestServices.EnsureInitialized("en");
            AppHost.Get<ThemeService>().Apply(new ThemeSettings { ReduceMotion = false, Mode = ThemeMode.Light }, "en");
            images.BackPresetIds = back;
            foreach (var preset in added)
                store.Presets.Remove(preset);
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException) { }
        }
        return clip;
    }

    /// <summary>
    /// A made-up photo of a page lying at an angle on a desk (1600 × 1000), with lines of writing that follow its tilt.
    /// Returns where the page's corners are, in parts of the photo.
    /// </summary>
    private static CropQuad DrawTiltedPage(string path)
    {
        PointD[] corners = [new(360, 150), new(1230, 235), new(1300, 900), new(270, 830)];
        PointD At(double u, double v)
        {
            var top = new PointD(corners[0].X + (corners[1].X - corners[0].X) * u, corners[0].Y + (corners[1].Y - corners[0].Y) * u);
            var bottom = new PointD(corners[3].X + (corners[2].X - corners[3].X) * u, corners[3].Y + (corners[2].Y - corners[3].Y) * u);
            return new PointD(top.X + (bottom.X - top.X) * v, top.Y + (bottom.Y - top.Y) * v);
        }
        using var image = new MagickImage("gradient:#C79A6B-#9C7148", 1600, 1000);
        var drawing = new Drawables()
            .FillColor(new MagickColor("#00000033")).Polygon(corners.Select(c => new PointD(c.X + 14, c.Y + 18)).ToArray())
            .FillColor(new MagickColor("#FBFAF6")).Polygon(corners)
            .FillColor(new MagickColor("#3E63DD")).Polygon(At(0.1, 0.09), At(0.6, 0.09), At(0.6, 0.15), At(0.1, 0.15));
        for (var line = 0; line < 11; line++)
        {
            var v = 0.24 + line * 0.065;
            var end = line % 4 == 3 ? 0.55 : 0.9;
            drawing.FillColor(new MagickColor("#B9B6AE")).Polygon(At(0.1, v), At(end, v), At(end, v + 0.022), At(0.1, v + 0.022));
        }
        drawing.Draw(image);
        image.Quality = 90;
        image.Write(path, MagickFormat.Jpeg);
        static Avalonia.Point Part(PointD p) => new(p.X / 1600, p.Y / 1000);
        return new CropQuad(Part(corners[0]), Part(corners[1]), Part(corners[2]), Part(corners[3]));
    }

    /// <summary>
    /// A made-up eight-page document (A4): each page a coloured heading with its number, a picture and lines of text.
    /// The pages are pictures (PDFsharp draws no text without fonts set up); PdfSheets places them as it would any page.
    /// </summary>
    private static void DrawDocument(string path, string folder)
    {
        var bold = Path.Combine(RepositoryRoot, "src", "Filee.App", "Assets", "Fonts", "NotoSans-Bold.ttf");
        string[] colours = ["#7F77DD", "#3E63DD", "#12A594", "#30A46C", "#F5A524", "#E5484D", "#D6409F", "#8E4EC6"];
        using var document = new PdfSharp.Pdf.PdfDocument();
        for (var n = 0; n < 8; n++)
        {
            var picture = Path.Combine(folder, $"page{n + 1}.jpg");
            using (var image = new MagickImage(MagickColors.White, 1190, 1684))
            {
                var grey = new MagickColor("#DEDDE3");
                var drawing = new Drawables()
                    .FillColor(new MagickColor(colours[n])).Rectangle(0, 0, 1190, 300)
                    .Font(bold).FontPointSize(210).FillColor(MagickColors.White).Text(90, 250, (n + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .FillColor(new MagickColor("#FFFFFFB0")).RoundRectangle(420, 120, 1000, 150, 15, 15)
                    .FillColor(new MagickColor("#FFFFFF80")).RoundRectangle(420, 180, 860, 206, 13, 13);
                for (var line = 0; line < 6; line++)
                    drawing.FillColor(grey).RoundRectangle(90, 400 + line * 52, line == 5 ? 700 : 1100, 424 + line * 52, 12, 12);
                drawing.FillColor(new MagickColor(colours[(n + 3) % 8]))
                    .RoundRectangle(90, 760, 1100, 1180, 24, 24)
                    .FillColor(new MagickColor("#FFFFFF59")).Circle(800, 900, 800, 960)
                    .FillColor(new MagickColor("#FFFFFF40")).Polygon(new PointD(90, 1180), new PointD(420, 920), new PointD(700, 1180));
                for (var line = 0; line < 6; line++)
                    drawing.FillColor(grey).RoundRectangle(90, 1260 + line * 52, line == 5 ? 560 : 1100, 1284 + line * 52, 12, 12);
                drawing.Draw(image);
                image.Quality = 90;
                image.Write(picture, MagickFormat.Jpeg);
            }
            var page = document.AddPage();
            page.Width = PdfSharp.Drawing.XUnit.FromPoint(595.28);
            page.Height = PdfSharp.Drawing.XUnit.FromPoint(841.89);
            using var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page);
            using var ximage = PdfSharp.Drawing.XImage.FromFile(picture);
            gfx.DrawImage(ximage, 0, 0, 595.28, 841.89);
        }
        document.Save(path);
    }

    /// <summary>
    /// Files dragged onto the donut with the gesture key held, as <see cref="DropOnCrop"/> does: the donut opens around
    /// the pointer, its own clock stepped with the clip's frames; Tab can turn it over, a group fans out when the pointer
    /// heads outward on it, and a drop runs the progress ring to its check mark.
    /// </summary>
    private sealed class DonutDrag : IDisposable
    {
        private readonly Avalonia.Point _center;
        private bool _tracking = true;

        public DonutDrag(Clip clip, IReadOnlyList<string> files, Action<RadialViewModel>? prepare = null, bool clickMode = false, bool backFace = false)
        {
            (Window, Vm) = OpenDonut(files, clickMode, backFace);
            prepare?.Invoke(Vm);
            Donut = Window.DonutControl;
            _center = new Avalonia.Point(Donut.Bounds.Width / 2, Donut.Bounds.Height / 2);
            Scene = new Scene(Window, clip) { FrameLength = 1 / 20.0, RunsRenderTimer = false };
            Scene.Steps.Add(dt =>
            {
                if (_tracking)
                    Aim();
                KeepFramesComing(Donut);
                Donut.Advance(dt);
                KeepFramesComing(Donut);
            });
        }

        /// <summary>Lights up what the pointer is on, and the hint for it in the hub.</summary>
        public void Aim()
        {
            var at = Window.TranslatePoint(Scene.Pointer, Donut)!.Value;
            Hit = Donut.HitTest(at);
            Donut.SetExternalHit(Hit, at);
            Vm.ShowHint(Hit.Kind is DonutHitKind.Segment or DonutHitKind.SubSegment ? Hit.Index : -1,
                Hit.Kind == DonutHitKind.SubSegment ? Hit.Sub : -1, Hit.Kind == DonutHitKind.Center);
        }

        public RadialWindow Window { get; }
        public RadialViewModel Vm { get; }
        public DonutMenu Donut { get; }
        public Scene Scene { get; }

        /// <summary>What the pointer was on at the last frame.</summary>
        public DonutHit Hit { get; private set; }

        public int Index(string presetId)
        {
            var index = Vm.Items.ToList().FindIndex(i => i.Id == presetId);
            Assert.True(index >= 0, $"{presetId} isn't on this face of the donut");
            return index;
        }

        /// <summary>The middle of slice <paramref name="index"/> (in the window), <paramref name="depth"/> from the hole (0) to the rim (1).</summary>
        public Avalonia.Point Slice(int index, double depth)
        {
            var inner = Donut.OuterRadius * Donut.HoleRatio;
            return InWindow(DonutGeometry.PointAt(_center, inner + (Donut.OuterRadius - inner) * depth, DonutGeometry.MidAngle(index, Donut.Items!.Count)));
        }

        public Avalonia.Point Sub(int group, int sub) => InWindow(Donut.SubSliceCenter(group, sub));

        private Avalonia.Point InWindow(Avalonia.Point p) => Donut.TranslatePoint(p, Window)!.Value;

        /// <summary>
        /// The donut opens around the pointer as the drag comes in with the gesture key held, and the pointer goes on to
        /// <paramref name="to"/>.
        /// </summary>
        public void Open(Avalonia.Point to)
        {
            Scene.Grab(true);
            Scene.Place(InWindow(_center + new Avalonia.Vector(2, 3)));
            Scene.ShowKey("Ctrl", down: true);
            Donut.PlayOpenAnimation();
            Scene.Tick();
            Scene.MoveTo(to, 0.35);
            Scene.Hold(0.35);
            Scene.HideKey();
        }

        /// <summary>Tab turns the donut over to its other face.</summary>
        public void Flip()
        {
            Scene.ShowKey("Tab", down: true);
            Assert.True(Donut.Flip());
            Scene.Tick();
            Scene.ShowKey("Tab", down: false);
            Scene.Hold(DonutMenu.FlipDuration);
            Scene.HideKey();
            Scene.Hold(0.3);
        }

        /// <summary>
        /// The files dropped: the slices go into the centre, the ring fills in <paramref name="seconds"/> and the check
        /// shows for <paramref name="check"/> seconds.
        /// </summary>
        public void Drop(double seconds, double check)
        {
            _tracking = false;
            Scene.Grab(false);
            Donut.BeginProgress();
            var frames = (int)Math.Round(seconds / Scene.FrameLength);
            Scene.Hold(0.25);
            for (var i = 1; i <= frames; i++)
            {
                Donut.SetProgress(Scene.EaseInOut(i / (double)frames));
                Scene.Tick();
            }
            Scene.Hold(0.3);
            Donut.CompleteProgress(DonutProgressOutcome.Succeeded, () => { });
            for (var i = 0; i < 60 && Donut.ShownOutcome is null; i++)
                Scene.Tick();
            Scene.Hold(check);
        }

        public void Dispose()
        {
            Window.Close();
            Donut.SetExternalHighlight(-1);
            if (Vm.IsBackFace)
                Vm.ToggleFace();
        }
    }

    /// <summary>
    /// A made-up web page to mark up (1280 × 720): a travel offer with a photo, a title, a price and a button, and
    /// three cards below. Generated, so no picture is kept in the repository.
    /// </summary>
    private static void DrawWebPage(string path)
    {
        var fonts = Path.Combine(RepositoryRoot, "src", "Filee.App", "Assets", "Fonts");
        var (regular, bold) = (Path.Combine(fonts, "NotoSans-Regular.ttf"), Path.Combine(fonts, "NotoSans-Bold.ttf"));
        var photoPath = Path.ChangeExtension(path, ".photo.jpg");
        DrawLandscape(photoPath, 0);
        using var image = new MagickImage(new MagickColor("#FFFFFF"), 1280, 720);
        using (var photo = new MagickImage(photoPath))
        {
            photo.Resize(new MagickGeometry(640, 360) { IgnoreAspectRatio = true });
            image.Composite(photo, 48, 92, CompositeOperator.Over);
        }
        var grey = new MagickColor("#E4E4E9");
        var drawing = new Drawables()
            .FillColor(new MagickColor("#F3F3F5")).Rectangle(0, 0, 1280, 56)
            .FillColor(new MagickColor("#3E63DD")).Circle(48, 28, 48, 41)
            .FillColor(grey).RoundRectangle(80, 20, 200, 36, 8, 8)
            .FillColor(grey).RoundRectangle(900, 18, 980, 38, 10, 10)
            .FillColor(grey).RoundRectangle(1000, 18, 1080, 38, 10, 10)
            .FillColor(new MagickColor("#1C1C1C")).RoundRectangle(1100, 14, 1232, 42, 14, 14)
            .Font(bold).FontPointSize(40).FillColor(new MagickColor("#1C1C1C")).Text(730, 150, "Three days in Lisbon")
            .FillColor(grey).RoundRectangle(730, 186, 1200, 200, 7, 7)
            .FillColor(grey).RoundRectangle(730, 214, 1160, 228, 7, 7)
            .FillColor(grey).RoundRectangle(730, 242, 1010, 256, 7, 7)
            .Font(bold).FontPointSize(34).FillColor(new MagickColor("#1C1C1C")).Text(730, 306, "€ 420")
            .Font(regular).FontPointSize(20).FillColor(new MagickColor("#6B6B73")).Text(842, 304, "per person")
            .FillColor(new MagickColor("#3E63DD")).RoundRectangle(730, 380, 976, 440, 12, 12)
            .Font(bold).FontPointSize(24).FillColor(MagickColors.White).TextAlignment(TextAlignment.Center).Text(853, 419, "Book now")
            .TextAlignment(TextAlignment.Left);
        foreach (var x in new[] { 48, 472, 896 })
            drawing
                .FillColor(new MagickColor("#F6F6F8")).RoundRectangle(x, 492, x + 336, 680, 14, 14)
                .FillColor(grey).RoundRectangle(x + 24, 528, x + 250, 544, 8, 8)
                .FillColor(grey).RoundRectangle(x + 24, 560, x + 300, 572, 6, 6)
                .FillColor(grey).RoundRectangle(x + 24, 584, x + 210, 596, 6, 6);
        drawing.Draw(image);
        image.Write(path, MagickFormat.Png);
    }

    // ───────────────────────── Helpers ─────────────────────────

    /// <summary>
    /// A window played frame by frame with a pointer: real pointer input (hover, press, drag) and a picture of the
    /// pointer on every frame. <see cref="Steps"/> run before each frame with its length, for what the clip times itself.
    /// </summary>
    private sealed class Scene(TopLevel window, Clip clip, string? version = null)
    {
        private bool _grabbed;
        private readonly Glide _key = new(0, 0.12, t => t);
        private (string Label, bool Down, bool Shown) _keyCap;

        public Avalonia.Point Pointer { get; private set; }
        public bool Pressed { get; private set; }
        public List<Action<double>> Steps { get; } = [];

        /// <summary>Seconds per frame.</summary>
        public double FrameLength { get; init; } = Frame;

        /// <summary>The part of the window the clip shows (in the window), or null for all of it.</summary>
        public Avalonia.Rect? Region { get; set; }

        /// <summary>
        /// False for a window whose animations the clip steps itself (the donut): the render timer would also run its
        /// frame loop, on wall-clock time.
        /// </summary>
        public bool RunsRenderTimer { get; init; } = true;

        /// <summary>Puts the pointer somewhere at once (before the first frame).</summary>
        public void Place(Avalonia.Point at)
        {
            // Tooltips open on a wall-clock delay: they would come and go at random in the clip.
            ToolTip.SetServiceEnabled(window, false);
            Pointer = at;
            window.MouseMove(at);
        }

        /// <summary>One frame; <paramref name="pointer"/>, if given, places the pointer once the frame is laid out.</summary>
        public void Tick(Func<Avalonia.Point>? pointer = null)
        {
            foreach (var step in Steps)
                step(FrameLength);
            // A few rounds are enough: the capture renders the frame itself.
            for (var i = 0; i < 3; i++)
            {
                Dispatcher.UIThread.RunJobs();
                if (RunsRenderTimer)
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
            if (pointer is not null)
                Pointer = pointer();
            var frame = Capture(window, version, Region, Pointer, Pressed || _grabbed);
            var shown = _key.Step(_keyCap.Shown ? 1 : 0, FrameLength);
            if (shown > 0.01)
                DrawKeyCap(frame, _keyCap.Label, shown, _keyCap.Down);
            clip.Add(frame, (int)Math.Round(FrameLength * 1000));
        }

        /// <summary>A keycap at the bottom left, as the demo videos show the keys held.</summary>
        public void ShowKey(string label, bool down) => _keyCap = (label, down, true);

        public void HideKey() => _keyCap = _keyCap with { Shown = false };

        /// <summary>A key pressed: shown down, and <paramref name="press"/> sends it.</summary>
        public void Key(string label, Action press)
        {
            ShowKey(label, down: true);
            press();
            Tick();
        }

        public void Hold(double seconds)
        {
            for (var i = (int)Math.Round(seconds / FrameLength); i > 0; i--)
                Tick();
        }

        /// <summary>Moves the pointer to <paramref name="to"/> along a slight curve, easing in and out (a drag while pressed).</summary>
        public void MoveTo(Avalonia.Point to, double seconds)
        {
            var from = Pointer;
            var normal = new Avalonia.Vector(from.Y - to.Y, to.X - from.X) * 0.06;
            var frames = Math.Max(1, (int)Math.Round(seconds / FrameLength));
            for (var i = 1; i <= frames; i++)
            {
                var u = EaseInOut(i / (double)frames);
                Pointer = new Avalonia.Point(from.X + (to.X - from.X) * u, from.Y + (to.Y - from.Y) * u) + normal * Math.Sin(Math.PI * u);
                window.MouseMove(Pointer, Pressed ? RawInputModifiers.LeftMouseButton : RawInputModifiers.None);
                Tick();
            }
        }

        public void Press()
        {
            window.MouseDown(Pointer, MouseButton.Left);
            Pressed = true;
        }

        public void Release()
        {
            window.MouseUp(Pointer, MouseButton.Left);
            Pressed = false;
        }

        /// <summary>A click, the button shown pressed for a frame.</summary>
        public void Click()
        {
            Press();
            Tick();
            Release();
        }

        /// <summary>The pointer drawn pressed without input: for a control the clip moves through its settings.</summary>
        public void Grab(bool grabbed) => _grabbed = grabbed;

        public static double EaseInOut(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    }

    /// <summary>A value easing to where the styles put it, as their transition would: a new target starts from where it is.</summary>
    private sealed class Glide(double value, double seconds, Func<double, double> easing)
    {
        private double _from = value, _to = value;
        private double _age = double.PositiveInfinity; // at rest
        private double _value = value;

        public double Step(double target, double dt)
        {
            if (target != _to)
                (_from, _to, _age) = (_value, target, 0);
            _age += dt;
            _value = _from + (_to - _from) * easing(Math.Clamp(_age / seconds, 0, 1));
            return _value;
        }
    }

    /// <summary>Avalonia's BackEaseOut (the SoftEase of Filee's styles).</summary>
    private static double BackEaseOut(double t)
    {
        var p = 1 - t;
        return 1 - p * (p * p - Math.Sin(p * Math.PI));
    }

    private static (RadialWindow Window, RadialViewModel Vm) OpenDonut(IReadOnlyList<string> files, bool clickMode, bool backFace = false)
    {
        var vm = AppHost.Get<RadialViewModel>();
        if (!clickMode)
            vm.ResetForDrag();
        vm.Load(files, clickMode, backFace);
        vm.FitToThisDonut();
        // The headless platform has no transparent windows: the page colour instead of black around the donut.
        var window = new RadialWindow { DataContext = vm, Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(Page)) };
        window.Show();
        Pump();
        return (window, vm);
    }

    private static DonutItem Enabled(DonutItem item) => new()
    {
        Id = item.Id,
        Label = item.Label,
        Caption = item.Caption,
        Children = item.Children?.Select(Enabled).ToList(),
    };

    private static void Highlight(RadialWindow window, RadialViewModel vm, int index)
    {
        window.DonutControl.SetExternalHighlight(index);
        vm.ShowHint(index, -1, centerHot: false);
        Settle(window.DonutControl);
    }

    /// <summary>Lets the donut's springs (highlight, hub) come to rest, and draws it.</summary>
    private static void Settle(DonutMenu donut)
    {
        for (var i = 0; i < 90; i++)
            donut.Advance(1 / 60.0);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>One frame: the donut's clock moves on by <see cref="Frame"/>.</summary>
    private static void Step(DonutMenu donut)
    {
        KeepFramesComing(donut);
        donut.Advance(Frame);
        Dispatcher.UIThread.RunJobs();
        KeepFramesComing(donut);
    }

    /// <summary>Runs the donut's animations <paramref name="seconds"/> (or until <paramref name="until"/>), a frame every <see cref="Frame"/>.</summary>
    private static void Run(Clip clip, RadialWindow window, double seconds, Func<bool>? until = null)
    {
        for (var t = 0.0; t < seconds && until?.Invoke() != true; t += Frame)
        {
            Step(window.DonutControl);
            clip.Add(Capture(window), (int)(Frame * 1000));
        }
    }

    private static void Pump()
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
        Assert.True(condition());
    }

    /// <summary>
    /// A rendered frame of <paramref name="window"/> centred on the page-coloured canvas, scaled down to fit. With
    /// <paramref name="version"/>, the sidebar shows that version instead of the one being built (the clips are made
    /// before the version is bumped). With <paramref name="pointer"/> (in the window), a mouse pointer is drawn there.
    /// </summary>
    private static MagickImage Capture(TopLevel window, string? version = null, Avalonia.Rect? region = null,
        Avalonia.Point? pointer = null, bool pressed = false)
    {
        if (window is RadialWindow radial)
            KeepFramesComing(radial.DonutControl);
        try
        {
            return CaptureCore(window, version, region, pointer, pressed);
        }
        finally
        {
            if (window is RadialWindow after)
                KeepFramesComing(after.DonutControl);
        }
    }

    private static readonly System.Reflection.FieldInfo LastFrameTick =
        typeof(DonutMenu).GetField("_lastTick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("DonutMenu._lastTick is gone: update WhatsNewMedia.KeepFramesComing");

    /// <summary>
    /// Taking a frame (PNG, compare) takes longer than the donut's stalled-frames watchdog waits (StalledLoopMs), which
    /// would then jump every animation to its end. The clips step the clock themselves, so the watchdog is told that
    /// frames are coming.
    /// </summary>
    private static void KeepFramesComing(DonutMenu donut) => LastFrameTick.SetValue(donut, Environment.TickCount64);

    /// <summary>
    /// The window as it is drawn now, at its own size. <paramref name="direct"/> draws it from its state, as the donut
    /// always is, without a frame of the render timer.
    /// </summary>
    private static MagickImage Snapshot(TopLevel window, bool direct = false)
    {
        using var png = new MemoryStream();
        if (direct || window is RadialWindow)
        {
            // The donut is drawn straight from its state: the clips step its clock themselves, and the render timer
            // would run its own frame loop as well.
            window.UpdateLayout();
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(
                new Avalonia.PixelSize((int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height)));
            bitmap.Render(window);
            bitmap.Save(png, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        else
        {
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame.Save(png, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        png.Position = 0;
        return new MagickImage(png);
    }

    private static MagickImage CaptureCore(TopLevel window, string? version, Avalonia.Rect? region, Avalonia.Point? pointer, bool pressed)
    {
        if (version is not null)
        {
            foreach (var text in window.GetVisualDescendants().OfType<TextBlock>())
                if (text.Text?.Contains(UpdateService.CurrentVersion, StringComparison.Ordinal) == true)
                    text.Text = text.Text.Replace(UpdateService.CurrentVersion, version, StringComparison.Ordinal);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        using var shot = Snapshot(window);
        var corner = new Avalonia.Point(0, 0);
        if (region is { } r)
        {
            var x = (int)Math.Clamp(r.X, 0, Math.Max(0, shot.Width - r.Width));
            var y = (int)Math.Clamp(r.Y, 0, Math.Max(0, shot.Height - r.Height));
            shot.Crop(new MagickGeometry(x, y, (uint)r.Width, (uint)r.Height));
            shot.ResetPage();
            corner = new Avalonia.Point(x, y);
        }
        var canvas = new MagickImage(new MagickColor(Page), CanvasWidth, CanvasHeight);
        var scale = Math.Min(1, Math.Min((double)CanvasWidth / shot.Width, (double)CanvasHeight / shot.Height));
        if (scale < 1)
        {
            shot.FilterType = FilterType.Lanczos;
            shot.Resize((uint)Math.Round(shot.Width * scale), (uint)Math.Round(shot.Height * scale));
        }
        canvas.Composite(shot, Gravity.Center, CompositeOperator.Over);
        if (pointer is { } p)
        {
            var left = (CanvasWidth - (int)shot.Width) / 2;
            var top = (CanvasHeight - (int)shot.Height) / 2;
            DrawPointer(canvas, new PointD(left + (p.X - corner.X) * scale, top + (p.Y - corner.Y) * scale), pressed);
        }
        return canvas;
    }

    /// <summary>A keyboard key at the bottom left of the canvas, <paramref name="opacity"/> 0 to 1, pressed or not.</summary>
    private static void DrawKeyCap(MagickImage canvas, string label, double opacity, bool down)
    {
        var font = Path.Combine(RepositoryRoot, "src", "Filee.App", "Assets", "Fonts", "NotoSans-Bold.ttf");
        var width = Math.Max(46, 22 + 11 * label.Length);
        var (x, y) = (40.0, CanvasHeight - 70.0 + (down ? 2 : 0));
        var alpha = (byte)Math.Round(255 * opacity);
        MagickColor Colour(string hex, double share = 1)
        {
            var c = new MagickColor(hex);
            return new MagickColor(c.R, c.G, c.B, (byte)Math.Round(alpha * share));
        }
        var drawing = new Drawables()
            .StrokeColor(Colour("#000000", 0)).FillColor(Colour("#000000", 0.14)).RoundRectangle(x, y + 4, x + width, y + 38, 9, 9)
            .FillColor(Colour("#FFFFFF")).StrokeColor(Colour(down ? "#7F77DD" : "#D6D3CB")).StrokeWidth(down ? 1.6 : 1)
            .RoundRectangle(x, y, x + width, y + 34, 9, 9);
        if (File.Exists(font))
            drawing.Font(font);
        drawing.FontPointSize(15).StrokeColor(Colour("#000000", 0)).FillColor(Colour("#1F1E1D"))
            .TextAlignment(TextAlignment.Center).Text(x + width / 2.0, y + 23, label)
            .Draw(canvas);
    }

    /// <summary>An arrow pointer with its tip at <paramref name="tip"/>, a little smaller while pressed.</summary>
    private static void DrawPointer(MagickImage canvas, PointD tip, bool pressed)
    {
        var size = pressed ? 1.06 : 1.15;
        PointD[] shape = [new(0, 0), new(0, 17.5), new(4.2, 13.6), new(7.1, 20.3), new(10.1, 19), new(7.3, 12.5), new(12.8, 12.5)];
        PointD[] At(double dx, double dy) => [.. shape.Select(q => new PointD(tip.X + q.X * size + dx, tip.Y + q.Y * size + dy))];
        new Drawables()
            .StrokeLineJoin(LineJoin.Round)
            .FillColor(new MagickColor("#00000038")).StrokeColor(new MagickColor("#0000001F")).StrokeWidth(2.4).Polygon(At(0.6, 1.6))
            .FillColor(MagickColors.White).StrokeColor(new MagickColor("#1F1E1D")).StrokeWidth(1.15).Polygon(At(0, 0))
            .Draw(canvas);
    }

    /// <summary>
    /// A made-up photo to crop (1600 × 1000): a harbour (sky, sun, sea, hills, a sail), a meadow at noon or dunes at
    /// dusk. Generated, so no picture is kept in the repository.
    /// </summary>
    private static void DrawLandscape(string path, int variant = 0)
    {
        var (sky, sun, ground, near, far) = variant switch
        {
            1 => ("gradient:#9FD3F2-#E9F6E4", "#FFF7D6", "#7DB65A", "#4E8F3C", "#A5CC7A"),
            2 => ("gradient:#F6B98A-#FCE3C4", "#FFF1D0", "#D9925A", "#B86E3E", "#E7B07A"),
            _ => ("gradient:#8EC5FC-#F8D2A6", "#FFF2C2", "#4F7CAC", "#3F6E4A", "#6C9A5B"),
        };
        using var image = new MagickImage(sky, 1600, 1000);
        var drawing = new Drawables()
            .FillColor(new MagickColor(sun)).Circle(1080, 360, 1080, 460)
            .FillColor(new MagickColor(ground)).Rectangle(0, 640, 1600, 1000)
            .FillColor(new MagickColor(far)).Polygon(new PointD(0, 700), new PointD(380, 470), new PointD(760, 690), new PointD(0, 760))
            .FillColor(new MagickColor(near)).Polygon(new PointD(600, 700), new PointD(1000, 520), new PointD(1600, 700), new PointD(1600, 760), new PointD(600, 760));
        if (variant == 0)
            drawing
                .FillColor(new MagickColor("#F2F2F2")).Polygon(new PointD(1180, 760), new PointD(1260, 600), new PointD(1260, 760))
                .FillColor(new MagickColor("#7A4E2D")).Rectangle(1150, 760, 1300, 790);
        drawing.Draw(image);
        image.Quality = 92;
        image.Write(path, MagickFormat.Jpeg);
    }

    /// <summary>
    /// Renders and writes one clip. FILEE_WHATSNEW_ONLY (names without extension, comma-separated) limits a run to some
    /// clips, e.g. <c>FILEE_WHATSNEW_ONLY=crop,theme</c>; <paramref name="onRequestOnly"/> clips (made elsewhere) are
    /// written only when named there.
    /// </summary>
    private static void Write(string folder, string name, Func<Clip> render, bool onRequestOnly = false)
    {
        var only = Environment.GetEnvironmentVariable("FILEE_WHATSNEW_ONLY");
        var named = !string.IsNullOrWhiteSpace(only)
                    && only.Split(',', StringSplitOptions.TrimEntries).Contains(Path.GetFileNameWithoutExtension(name), StringComparer.OrdinalIgnoreCase);
        if (onRequestOnly ? !named : !string.IsNullOrWhiteSpace(only) && !named)
            return;
        var language = AppHost.Get<ILocalizer>().Language;
        Console.WriteLine($"Rendering {language}: {name}");
        using (var clip = render())
        {
            var path = Path.Combine(folder, name);
            clip.WriteWebP(path);
            // The first frame next to the screenshots, for review.
            clip.SaveFirstFrame(Path.Combine(TestServices.ScreenshotDirectory, $"whatsnew-{language}-" + Path.ChangeExtension(name, ".png")));
            Assert.InRange(new FileInfo(path).Length, 1, 400 * 1024);
            if (language != "en" || clip.Poster is not null)
                SetPoster(folder, name, clip.PosterIn(path));
        }
    }

    /// <summary>Writes the frame a clip marked as its <c>poster</c> into the slide of slides.json that shows it.</summary>
    private static void SetPoster(string folder, string media, int frame)
    {
        var language = AppHost.Get<ILocalizer>().Language;
        if (language != "en")
        {
            var manifest = Path.Combine(Path.GetDirectoryName(folder)!, "slides.json");
            var root = JsonNode.Parse(File.ReadAllText(manifest))!;
            var item = root["slides"]!.AsArray().Single(s => s!["media"]!.GetValue<string>() == media)!;
            item["localizedMedia"] ??= new JsonObject();
            item["localizedMedia"]![language] = new JsonObject { ["media"] = $"{language}/{media}", ["poster"] = frame };
            File.WriteAllText(manifest, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
            return;
        }
        var path = Path.Combine(folder, "slides.json");
        var text = File.ReadAllText(path);
        var slide = Regex.Match(text, $$"""\{[^{}]*"media": "{{Regex.Escape(media)}}"[^{}]*\}""");
        Assert.True(slide.Success, $"slides.json has no slide showing {media}");
        var updated = Regex.Replace(slide.Value, """ "poster": \d+,""", "")
            .Replace($"\"media\": \"{media}\",", $"\"media\": \"{media}\", \"poster\": {frame},", StringComparison.Ordinal);
        File.WriteAllText(path, text.Replace(slide.Value, updated, StringComparison.Ordinal));
    }

    /// <summary>Frames with how long each shows; a frame identical to the one before only lengthens it.</summary>
    private sealed class Clip : IDisposable
    {
        private readonly List<(MagickImage Image, int Milliseconds)> _frames = [];

        public int Count => _frames.Count;

        /// <summary>WebP quality of the frames: lower for a long clip with much movement, to stay within the budget.</summary>
        public uint Quality { get; init; } = 82;

        /// <summary>The frame reduced motion shows instead of the first (<see cref="MarkPoster"/>).</summary>
        public int? Poster { get; private set; }

        /// <summary>The frame added last is the clip's poster.</summary>
        public void MarkPoster() => Poster = _frames.Count - 1;

        /// <summary>Shows the last frame <paramref name="milliseconds"/> longer.</summary>
        public void Hold(int milliseconds) => _frames[^1] = (_frames[^1].Image, _frames[^1].Milliseconds + milliseconds);

        public void Add(MagickImage image, int milliseconds)
        {
            if (_frames.Count > 0 && _frames[^1].Image.Compare(image, ErrorMetric.Absolute) == 0)
            {
                _frames[^1] = (_frames[^1].Image, _frames[^1].Milliseconds + milliseconds);
                image.Dispose();
                return;
            }
            _frames.Add((image, milliseconds));
        }

        public void WriteWebP(string path)
        {
            using var collection = new MagickImageCollection();
            foreach (var (image, milliseconds) in _frames)
            {
                var copy = image.Clone();
                // Delays are in 1/100 s; at least 2 so viewers don't speed the frame up.
                copy.AnimationDelay = (uint)Math.Max(2, Math.Round(milliseconds / 10.0));
                copy.AnimationTicksPerSecond = 100;
                copy.Quality = Quality;
                collection.Add(copy);
            }
            var defines = new ImageMagick.Formats.WebPWriteDefines { Method = 6, Lossless = false, ThreadLevel = true };
            if (_frames.Count > 1)
                collection[0].Settings.SetDefine(MagickFormat.WebP, "mixed", "true");
            collection.Write(path, defines);
        }

        public void SaveFirstFrame(string path) => _frames[0].Image.Write(path, MagickFormat.Png);

        /// <summary>
        /// The poster's frame in the file written to <paramref name="path"/> (0 without a poster): the encoder can merge
        /// a frame into the one before when they come out the same, which moves the frames after it.
        /// </summary>
        public int PosterIn(string path)
        {
            if (Poster is not { } poster)
                return 0;
            using var written = new MagickImageCollection(path);
            if (written.Count == _frames.Count)
                return poster;
            written.Coalesce();
            var wanted = _frames[poster].Image;
            return Enumerable.Range(0, written.Count).MinBy(i => wanted.Compare(written[i], ErrorMetric.RootMeanSquared));
        }

        public void Dispose()
        {
            foreach (var (image, _) in _frames)
                image.Dispose();
            _frames.Clear();
        }
    }
}
