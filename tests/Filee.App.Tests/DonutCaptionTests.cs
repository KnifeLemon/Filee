// The output format under the slices' names: switched off under Donut toolbar → Appearance, the names stand alone in
// their slices, a group drops "Group of N" and keeps its chevron, and both page previews and the donut when it opens
// follow it.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.App.Views;
using Filee.App.Views.Pages;
using Filee.Core.Localization;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class DonutCaptionTests
{
    private static byte[] Render(DonutItem[] items, bool captions, bool reserveSubRing = false)
    {
        TestServices.EnsureInitialized();
        Motion.Configure(true, () => false);
        var donut = new DonutMenu { Items = items, CenterTitle = "Images", ShowCaptions = captions, ReserveSubRing = reserveSubRing };
        var window = new Window { Content = donut, SizeToContent = SizeToContent.WidthAndHeight };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var size = new PixelSize((int)donut.Bounds.Width, (int)donut.Bounds.Height);
            using var image = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
            image.Render(donut);
            var pixels = new byte[size.Width * size.Height * 4];
            var pin = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
            try { image.CopyPixels(new PixelRect(size), pin.AddrOfPinnedObject(), pixels.Length, size.Width * 4); }
            finally { pin.Free(); }
            return pixels;
        }
        finally
        {
            window.Close();
        }
    }

    private static DonutItem[] Slices(params DonutItem[] extra) =>
        [.. new[] { "PNG", "JPG", "WEBP", "PDF" }.Select(label => new DonutItem { Id = label, Label = label, Caption = label }), .. extra];

    [AvaloniaFact]
    public void Switched_off_the_format_under_a_name_is_not_drawn()
    {
        var items = Slices(new DonutItem { Id = "trace", Label = "Trace", Caption = "SVG" });
        Assert.NotEqual(Render(items, captions: true), Render(items, captions: false));
    }

    [AvaloniaFact]
    public void Switched_off_a_slice_is_drawn_as_if_it_had_no_caption()
    {
        // A preset that makes a format, and one that keeps the dropped file's own. Pixel for pixel: the line is gone
        // and the name stands in the middle of its slice, where a name without a caption does.
        DonutItem[] captioned =
        [
            new() { Id = "half", Label = "Half size", Caption = "JPG" }, new() { Id = "same", Label = "Rotate", Caption = "PNG" },
            new() { Id = "trace", Label = "Trace", Caption = "SVG" },
        ];
        DonutItem[] bare = [.. captioned.Select(item => new DonutItem { Id = item.Id, Label = item.Label })];

        Assert.NotEqual(Render(captioned, captions: true), Render(captioned, captions: false));
        Assert.Equal(Render(bare, captions: true), Render(captioned, captions: false));
    }

    [AvaloniaFact]
    public void Switched_off_a_group_loses_its_caption_and_keeps_its_chevron()
    {
        // Captions only on slices named after their format (hidden anyway) and on the group, the fifth slice.
        var items = Slices(new DonutItem { Id = "rotate", Label = "Rotate", Caption = "Group of 3", Children = [] });
        var on = Render(items, captions: true);
        var off = Render(items, captions: false);
        Assert.NotEqual(on, off); // "Group of 3" is gone

        // The chevron's tip, 7 px in from the outer edge in the middle of the group's slice, is the same either way,
        // and isn't there without a group, on a donut of the same size.
        var side = (int)Math.Ceiling(DonutMenu.SideFor(150, subRing: true));
        var tip = DonutGeometry.PointAt(new Point(side / 2.0, side / 2.0), 150 - 7, DonutGeometry.MidAngle(4, 5));
        var at = ((int)tip.Y * side + (int)tip.X) * 4;
        Assert.Equal(on[at..(at + 4)], off[at..(at + 4)]);
        Assert.NotEqual(on[at..(at + 4)], Render(Slices(new DonutItem { Id = "rotate", Label = "Rotate" }), captions: false, reserveSubRing: true)[at..(at + 4)]);
    }

    [AvaloniaFact]
    public void A_folder_slice_keeps_its_move_or_copy()
    {
        // Two slices named after one folder: without this line they look the same, and one takes the files away.
        DonutItem[] items =
        [
            new() { Id = "move", Label = "Projects", Caption = "Move to folder", KeepsCaption = true },
            new() { Id = "copy", Label = "Projects", Caption = "Copy to folder", KeepsCaption = true },
        ];

        Assert.Equal(Render(items, captions: true), Render(items, captions: false));
    }

    [AvaloniaFact]
    public void Two_slices_of_one_name_keep_what_tells_them_apart()
    {
        DonutItem[] items =
        [
            new() { Id = "rotate-left", Label = "Rotate left", Caption = "PNG" }, new() { Id = "pdf-rotate-left", Label = "Rotate left", Caption = "PDF" },
        ];

        Assert.Equal(Render(items, captions: true), Render(items, captions: false));
    }

    [AvaloniaFact]
    public void Switching_it_repaints_a_donut_already_on_screen()
    {
        TestServices.EnsureInitialized();
        Motion.Configure(true, () => false);
        var donut = new DonutMenu { Items = Slices(new DonutItem { Id = "half", Label = "Half size", Caption = "JPG" }), CenterTitle = "Images" };
        var window = new Window { Content = donut, SizeToContent = SizeToContent.WidthAndHeight };
        window.Show();
        RenderTests.Pump();
        try
        {
            var before = Frame(window);

            donut.ShowCaptions = false;
            RenderTests.Pump();

            Assert.NotEqual(before, Frame(window));
        }
        finally
        {
            window.Close();
        }

        static byte[] Frame(Window window)
        {
            using var frame = window.CaptureRenderedFrame()!;
            using var png = new MemoryStream();
            frame.Save(png, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            return png.ToArray();
        }
    }

    [AvaloniaFact]
    public void The_appearance_switch_saves_the_setting_and_every_donut_shows_it()
    {
        TestServices.EnsureInitialized();
        var store = AppHost.Get<UserDataStore>();
        var loc = AppHost.Get<ILocalizer>();
        var theme = AppHost.Get<ThemeService>();
        var saved = store.Settings.Donut.ShowCaptions;
        try
        {
            store.Settings.Donut.ShowCaptions = true;
            var vm = new MainWindowViewModel(AppHost.Services, loc, AppHost.Get<UpdateService>(), AppHost.Get<UserDataStore>());
            vm.Navigate("toolbar");
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            RenderTests.Pump();

            var page = window.GetVisualDescendants().OfType<ToolbarPage>().Single();
            var toggle = page.FindControl<ToggleSwitch>("CaptionsToggle")!;
            var preview = page.FindControl<DonutMenu>("Donut")!;
            Assert.True(toggle.IsChecked);
            Assert.True(preview.ShowCaptions);
            // Labelled in the page's language, for sighted users and screen readers.
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(),
                t => t.Text == loc["toolbar.captions"] && t.IsEffectivelyVisible);
            Assert.Equal(loc["toolbar.captions"], Avalonia.Automation.AutomationProperties.GetName(toggle));

            toggle.IsChecked = false;
            RenderTests.Pump();
            Assert.False(store.Settings.Donut.ShowCaptions);
            Assert.False(preview.ShowCaptions);
            toggle.BringIntoView(); // the Appearance card is below the preview
            RenderTests.Pump();
            RenderTests.Save(window, "page-toolbar-captions-off.png");

            // The Theme page's preview follows it too.
            vm.Navigate("theme");
            RenderTests.Pump();
            Assert.False(Assert.IsType<ThemePageViewModel>(vm.CurrentPage).DonutCaptions);
            Assert.False(window.GetVisualDescendants().OfType<ThemePage>().Single().FindControl<DonutMenu>("PreviewDonut")!.ShowCaptions);
            window.Close();

            // The donut takes it the next time it opens: the real window, in the dark theme, for the screenshot.
            theme.Apply(new ThemeSettings { Mode = ThemeMode.Dark }, "en");
            Motion.Configure(true, () => false); // no flip under way in the screenshot
            var radial = AppHost.Get<RadialViewModel>();
            radial.Load([@"C:\docs\report.pdf"], clickMode: true);
            radial.FitToThisDonut();
            Assert.False(radial.ShowCaptions);
            var donutWindow = new RadialWindow { DataContext = radial };
            donutWindow.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.False(donutWindow.DonutControl.ShowCaptions);
            // The back face has the Edit group: no "Group of 6", the chevron marks it.
            Assert.True(donutWindow.DonutControl.HandleKey(Avalonia.Input.Key.Tab));
            Assert.True(radial.Items[0].IsGroup);
            RenderTests.Pump();
            RenderTests.Save(donutWindow, "donut-captions-off-dark.png");
            donutWindow.Close();

            store.Settings.Donut.ShowCaptions = true;
            radial.ResetForDrag();
            Assert.True(radial.ShowCaptions);
        }
        finally
        {
            store.Settings.Donut.ShowCaptions = saved;
            store.SaveSettings();
            theme.Apply(new ThemeSettings(), "en");
        }
    }
}
