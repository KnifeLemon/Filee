using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using FakeConverter = Filee.App.Tests.PresetAvailabilityTests.FakeConverter;
using Fixture = Filee.App.Tests.PresetAvailabilityTests.Fixture;

namespace Filee.App.Tests;

[Collection("Motion")]
public class OrdinaryConversionNoticeTests
{
    internal sealed class Inputs() : FakeConverter("notice-inputs", new ConversionEdge[]
    {
        new("mp4", "png"), new("mp4", "jpg"),
        new("mp4", "png", FrameSequence: true), new("mp4", "jpg", FrameSequence: true),
        new("jpg", "mp4"), new("png", "mp4"), new("jpg", "jpg"),
        new("jpg", "mp4", MotionPhoto: true), new("heic", "mp4", MotionPhoto: true),
        new("avif", "mp4", MotionPhoto: true), new("livp", "mp4", MotionPhoto: true),
        new("p7m", "png"), new("shp", "png"),
    }), IEnvelopeOpener
    {
        public bool Opens(string format) => format == "p7m";
        public string InnerFormat(string path, string format) => throw new NotSupportedException("No content preflight.");
    }

    [AvaloniaTheory]
    [InlineData("extract-frames-png", "mp4", "jpg")]
    [InlineData("extract-frames-jpg", "mp4", "png")]
    [InlineData("extract-motion", "jpg", "png")]
    [InlineData("extract-motion", "heic", "png")]
    [InlineData("extract-motion", "avif", "png")]
    [InlineData("extract-motion", "livp", "png")]
    public void Only_runnable_inputs_without_the_named_operation_count(string id, string named, string ordinary)
    {
        using var f = new Fixture(new Inputs());
        var preset = f.Preset(id);
        OrdinaryConversion Notice(params string[] formats) =>
            f.Availability.OrdinaryConversionFor(preset, formats.Select(FormatRegistry.Get).ToList());

        Assert.Equal(OrdinaryConversion.None, Notice(named));
        Assert.Equal(OrdinaryConversion.All, Notice(ordinary));
        Assert.Equal(OrdinaryConversion.All, Notice(ordinary, ordinary));
        Assert.Equal(OrdinaryConversion.Some, Notice(named, ordinary));
        Assert.Equal(OrdinaryConversion.All, Notice(ordinary, "wav"));
        Assert.Equal(OrdinaryConversion.None, Notice(named, "wav"));
        Assert.Equal(OrdinaryConversion.None, Notice("wav"));
        Assert.Equal(OrdinaryConversion.None, Notice());
    }

    [AvaloniaFact]
    public void The_fixed_list_does_not_probe_other_presets()
    {
        using var f = new Fixture();
        var availability = new PresetAvailability(f.Catalog, f.Loc, [], () => throw new InvalidOperationException("Unexpected probe."));
        foreach (var id in new[] { "to-png", "live-photo-iphone", "motion-photo-android", "caption-video", "searchable-pdf" })
            Assert.Equal(OrdinaryConversion.None,
                availability.OrdinaryConversionFor(f.Preset(id), [Fixture.Format("png")]));
    }

    [AvaloniaFact]
    public void Current_options_missing_packages_and_opaque_envelopes_are_not_guessed()
    {
        using var f = new Fixture(new Inputs());
        var frames = new Preset { Id = "extract-frames-jpg", TargetFormat = "jpg" };
        Assert.Equal(OrdinaryConversion.All, f.Availability.OrdinaryConversionFor(frames, [Fixture.Format("mp4")]));
        var motion = new Preset { Id = "extract-motion", TargetFormat = "mp4" };
        Assert.Equal(OrdinaryConversion.All, f.Availability.OrdinaryConversionFor(motion, [Fixture.Format("jpg")]));
        var preset = f.Preset("extract-frames-png");
        Assert.Equal(OrdinaryConversion.None, f.Availability.OrdinaryConversionFor(preset, [Fixture.Format("p7m")]));
        Assert.Equal(OrdinaryConversion.Some,
            f.Availability.OrdinaryConversionFor(preset, [Fixture.Format("png"), Fixture.Format("p7m")]));

        using var missing = new Fixture();
        Assert.NotNull(missing.Availability.Check(missing.Preset("extract-motion"), [Fixture.Format("png")]));
        Assert.Equal(OrdinaryConversion.None,
            missing.Availability.OrdinaryConversionFor(missing.Preset("extract-motion"), [Fixture.Format("png")]));
    }

    private sealed class Live : IDisposable
    {
        public Live(Fixture f, bool click, params string[] files)
        {
            Motion.Configure(true, () => false);
            f.Store.Profiles.Clear();
            f.Store.Profiles.Add(new ToolbarProfile
            {
                Id = "notice-donut",
                IsFallback = true,
                PresetIds = ["extract-frames-png", "extract-motion", "test-jpg", "test-wav", "notice-group"],
                BackPresetIds = ["test-jpg", "extract-frames-jpg", "notice-group"],
            });
            f.Store.Presets.Add(new Preset
            {
                Id = "notice-group",
                Name = "Extraction",
                Kind = PresetKind.Group,
                SubPresetIds = ["extract-frames-png", "extract-motion"],
            });
            Vm = new RadialViewModel(f.Store, f.Loc, f.Availability);
            Vm.Load(files, click);
            Vm.FitToThisDonut();
            Window = new RadialWindow { DataContext = Vm };
            Donut.ActiveIndexChanged += (_, index) => Vm.ShowHint(index, Donut.ActiveSubIndex, Donut.IsCenterHot);
            Window.Show();
            Dispatcher.UIThread.RunJobs();
        }

        public RadialViewModel Vm { get; }
        public RadialWindow Window { get; }
        public DonutMenu Donut => Window.DonutControl;
        public Point Center => new(Donut.Bounds.Width / 2, Donut.Bounds.Height / 2);

        public DonutHit PointAt(int index)
        {
            var at = DonutGeometry.PointAt(Center, Donut.OuterRadius * 0.7, DonutGeometry.MidAngle(index, Donut.Items!.Count));
            if (Vm.IsClickMode)
                Window.MouseMove(Donut.TranslatePoint(at, Window)!.Value);
            else
                Donut.SetExternalHit(Donut.HitTest(at), at);
            Dispatcher.UIThread.RunJobs();
            return Donut.HitTest(at);
        }

        public void Dispose()
        {
            Window.Close();
            Motion.Configure(false, () => false);
        }
    }

    [AvaloniaTheory]
    [InlineData(true, "single")]
    [InlineData(false, "single")]
    [InlineData(true, "all")]
    [InlineData(false, "all")]
    [InlineData(true, "some")]
    [InlineData(false, "some")]
    public void The_real_donut_keeps_the_action_hint_and_the_slice_runnable(bool click, string scope)
    {
        using var f = new Fixture(new Inputs());
        string[] files = scope switch
        {
            "all" => ["first.png", "second.png"],
            "some" => ["clip.mp4", "photo.png"],
            _ => ["photo.png"],
        };
        using var live = new Live(f, click, files);
        var title = live.Donut.CenterTitle;
        var hit = live.PointAt(0);
        Assert.Equal(f.Loc["donut.ordinary_conversion." + scope], live.Donut.CenterNotice);
        Assert.Equal(f.Loc.Format(click ? "donut.click_to" : "donut.drop_to", Assert.IsType<string>(live.Donut.Items![0].FullName)),
            live.Donut.CenterSubtitle);
        Assert.Equal(title, live.Donut.CenterTitle);
        Assert.True(live.Donut.Items![0].IsEnabled);
        Assert.True(live.Donut.Converts(hit));
        Assert.Same(f.Preset("extract-frames-png"), live.Vm.PresetAt(0, -1));

        live.PointAt(2);
        Assert.Null(live.Donut.CenterNotice);
        live.PointAt(3);
        Assert.Null(live.Donut.CenterNotice);
        Assert.Equal(live.Donut.Items[3].DisabledReason, live.Donut.CenterSubtitle);
    }

    [AvaloniaFact]
    public void Groups_faces_cancellation_and_reuse_do_not_keep_an_old_notice()
    {
        using var f = new Fixture(new Inputs());
        using var live = new Live(f, click: false, "photo.png");
        live.PointAt(4);
        Assert.Null(live.Donut.CenterNotice);
        live.Donut.ExpandPendingGroup();
        var at = live.Donut.SubSliceCenter(4, 0);
        live.Donut.SetExternalHit(live.Donut.HitTest(at), at);
        Assert.Equal(f.Loc["donut.ordinary_conversion.single"], live.Donut.CenterNotice);
        Assert.True(live.Donut.Converts(live.Donut.HitTest(at)));
        Assert.Same(f.Preset("extract-frames-png"), live.Vm.PresetAt(4, 0));
        live.Donut.CollapseGroup();
        Assert.Null(live.Donut.CenterNotice);

        live.PointAt(0);
        live.Donut.SetExternalHit(new DonutHit(DonutHitKind.Center), live.Center);
        Assert.Null(live.Donut.CenterNotice);
        Assert.Equal(f.Loc["donut.cancel"], live.Donut.CenterSubtitle);
        live.PointAt(0);
        Assert.NotNull(live.Donut.CenterNotice);
        Assert.True(live.Donut.Flip());
        Assert.Null(live.Donut.CenterNotice);
        Assert.Equal("test-jpg", live.Donut.Items![0].Id);
        live.PointAt(1);
        Assert.Equal(f.Loc["donut.ordinary_conversion.single"], live.Donut.CenterNotice);
        live.PointAt(2);
        Assert.Null(live.Donut.CenterNotice);
        live.Donut.ExpandPendingGroup();
        at = live.Donut.SubSliceCenter(2, 1);
        live.Donut.SetExternalHit(live.Donut.HitTest(at), at);
        Assert.Equal(f.Loc["donut.ordinary_conversion.single"], live.Donut.CenterNotice);
        Assert.Same(f.Preset("extract-motion"), live.Vm.PresetAt(2, 1));
        live.Donut.SetExternalHit(new DonutHit(DonutHitKind.Outside), new Point(-20, -20));
        Assert.Null(live.Donut.CenterNotice);

        live.PointAt(1);
        live.Vm.Load(["clip.mp4"], clickMode: false);
        Assert.Null(live.Donut.CenterNotice);
        live.PointAt(0);
        Assert.Null(live.Donut.CenterNotice);
        live.Vm.Load(["photo.png"], clickMode: false);
        live.PointAt(0);
        Assert.NotNull(live.Donut.CenterNotice);
        live.Vm.ResetForDrag();
        Assert.Null(live.Donut.CenterNotice);
        live.Vm.Load(["photo.png"], clickMode: false);
        live.PointAt(0);
        Assert.NotNull(live.Donut.CenterNotice);
        live.Vm.LoadForConnect();
        Assert.Null(live.Donut.CenterNotice);
        Assert.True(live.Vm.IsConnectMode);
        Assert.Equal("preset.filee_connect", Assert.Single(live.Donut.Items!).Id);
    }

    [AvaloniaFact]
    public void Keyboard_hover_and_flip_use_the_real_click_window()
    {
        using var f = new Fixture(new Inputs());
        using var live = new Live(f, click: true, "photo.png");
        live.PointAt(0);
        Assert.NotNull(live.Donut.CenterNotice);
        live.Window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, live.Donut.ActiveIndex);
        Assert.Equal(f.Loc["donut.ordinary_conversion.single"], live.Donut.CenterNotice);
        live.Donut.FlipKey = "Tab";
        live.Window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(live.Vm.IsBackFace);
        Assert.Null(live.Donut.CenterNotice);
        live.PointAt(1);
        Assert.NotNull(live.Donut.CenterNotice);
        var escaped = false;
        live.Window.EscapePressed += () => escaped = true;
        live.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(escaped);
    }

    [AvaloniaFact]
    public void Unknown_files_folders_and_companions_do_not_become_ordinary_inputs()
    {
        using var f = new Fixture(new Inputs());
        using var live = new Live(f, click: false, "photo.png", "unknown.notice-unknown");
        live.PointAt(0);
        Assert.Equal(f.Loc["donut.ordinary_conversion.all"], live.Donut.CenterNotice);
        var folder = Directory.CreateTempSubdirectory("filee-notice-");
        try
        {
            live.Vm.Load(["photo.png", folder.FullName], clickMode: false);
            live.PointAt(0);
            Assert.Equal(f.Loc["donut.ordinary_conversion.all"], live.Donut.CenterNotice);
            live.Vm.Load(new[] { "shp", "dbf", "shx" }.Select(ext => Path.Combine(folder.FullName, "map." + ext)).ToList(),
                clickMode: false);
            live.PointAt(0);
            Assert.Equal(f.Loc["donut.ordinary_conversion.single"], live.Donut.CenterNotice);
        }
        finally { folder.Delete(); }
    }

    [AvaloniaTheory]
    [InlineData("en", false)]
    [InlineData("ko", true)]
    [InlineData("ru", false)]
    [InlineData("es", true)]
    [InlineData("ar", true)]
    public void The_notice_is_actually_drawn_in_the_hub(string language, bool dark)
    {
        TestServices.EnsureInitialized(language);
        var app = Avalonia.Application.Current!;
        var before = app.RequestedThemeVariant;
        var donut = new DonutMenu { Items = [new() { Id = "one", Label = "PNG" }], CenterTitle = "" };
        var window = new Window { Content = donut, SizeToContent = SizeToContent.WidthAndHeight };
        try
        {
            app.RequestedThemeVariant = dark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
            window.Show();
            RenderTests.Pump();
            var empty = HubPixels(donut);
            donut.CenterNotice = AppHost.Get<Filee.Core.Localization.ILocalizer>()["donut.ordinary_conversion.all"];
            RenderTests.Pump();
            Assert.False(empty.SequenceEqual(HubPixels(donut)));
            donut.CenterSubtitle = AppHost.Get<Filee.Core.Localization.ILocalizer>().Format("donut.drop_to", "PNG");
            RenderTests.Pump();
            var withAction = HubPixels(donut);
            donut.CenterSubtitle = null;
            RenderTests.Pump();
            Assert.False(withAction.SequenceEqual(HubPixels(donut)));
        }
        finally
        {
            window.Close();
            app.RequestedThemeVariant = before;
            TestServices.EnsureInitialized("en");
        }
    }

    private static byte[] HubPixels(DonutMenu donut)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)donut.Bounds.Width, (int)donut.Bounds.Height));
        bitmap.Render(donut);
        var radius = (int)(donut.OuterRadius * donut.HoleRatio - 8);
        var pixels = new byte[radius * radius * 16];
        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect((int)donut.Bounds.Width / 2 - radius, (int)donut.Bounds.Height / 2 - radius,
                radius * 2, radius * 2), pin.AddrOfPinnedObject(), pixels.Length, radius * 8);
        }
        finally { pin.Free(); }
        return pixels;
    }
}
