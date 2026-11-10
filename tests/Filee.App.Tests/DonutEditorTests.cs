// Settings → Donut toolbar as an editor (#873): the sliders save the donut's look without applying every setting again;
// the donut in the page is drawn as it opens, only smaller when its column is narrower; and a group clicked in the
// editor opens like a folder to edit what it fans out.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.App.Views;
using Filee.App.Views.Pages;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class DonutEditorTests
{
    internal static (MainWindow Window, ToolbarPage Page, ToolbarPageViewModel Vm) Open(double width = 1280, double height = 900)
    {
        TestServices.EnsureInitialized("en");
        var main = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>(), AppHost.Get<UserDataStore>());
        main.Navigate("toolbar");
        var window = new MainWindow { DataContext = main, Width = width, Height = height };
        // As WindowService does in the app: a page left open would go on reacting to every library change after the test.
        window.Closed += (_, _) => main.Dispose();
        window.Show();
        RenderTests.Pump();
        var page = window.GetVisualDescendants().OfType<ToolbarPage>().Single();
        return (window, page, (ToolbarPageViewModel)page.DataContext!);
    }

    [AvaloniaFact]
    public void The_look_sliders_save_quietly_on_this_page_and_on_the_theme_page()
    {
        var (window, _, vm) = Open();
        var store = AppHost.Get<UserDataStore>();
        var saved = (store.Settings.Donut.OuterRadius, store.Settings.Donut.Opacity, store.Settings.Donut.GroupRingSize);
        var applied = 0;
        EventHandler count = (_, _) => applied++;
        store.SettingsChanged += count;
        try
        {
            foreach (var value in new[] { 150.4, 171.0, 188.6 })
                vm.OuterRadius = value;
            vm.SliceOpacity = 70;
            vm.GroupRingSize = 125;
            RenderTests.Pump();
            // Written as it moves (the donut reads it when it opens), without the theme, triggers and integration that
            // a settings change applies again: that is what made the sliders stick.
            Assert.Equal(189, store.Settings.Donut.OuterRadius);
            Assert.Contains("\"outerRadius\": 189", File.ReadAllText(Path.Combine(store.Directory, "settings.json")));
            Assert.Equal(0, applied);

            ((MainWindowViewModel)window.DataContext!).Navigate("theme");
            RenderTests.Pump();
            var theme = Assert.IsType<ThemePageViewModel>(((MainWindowViewModel)window.DataContext!).CurrentPage);
            theme.DonutOpacity = 55;
            Assert.Equal(0.55, store.Settings.Donut.Opacity, 3);
            Assert.Contains("\"opacity\": 0.55", File.ReadAllText(Path.Combine(store.Directory, "settings.json")));
            Assert.Equal(0, applied);
        }
        finally
        {
            store.SettingsChanged -= count;
            (store.Settings.Donut.OuterRadius, store.Settings.Donut.Opacity, store.Settings.Donut.GroupRingSize) = saved;
            store.SaveDonutLook();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_page_donut_is_the_real_one_made_smaller_only_when_its_column_is_narrower()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var saved = store.Settings.Donut.OuterRadius;
        try
        {
            foreach (var (width, radius, fits) in new[] { (1080.0, 220.0, false), (1080.0, 150.0, false), (1600.0, 150.0, true) })
            {
                store.Settings.Donut.OuterRadius = radius;
                var (window, page, _) = Open(width);
                var donut = page.FindControl<DonutMenu>("Donut")!;
                var fit = page.FindControl<Viewbox>("DonutFit")!;
                var card = page.FindControl<Border>("EditorCard")!;
                // Laid out at its real size, so names wrap and icons show as on the donut itself...
                Assert.Equal(DonutMenu.SideFor(radius, false), donut.DesiredSize.Width, 1);
                Assert.Equal(DonutMenu.SideFor(radius, false), donut.Bounds.Width, 1);
                // ...and never wider than its card: drawn smaller there, at full size once there is room.
                Assert.True(fit.Bounds.Width <= card.Bounds.Width - card.Padding.Left - card.Padding.Right + 0.5,
                    $"{width} px, radius {radius}: {fit.Bounds.Width} in a card of {card.Bounds.Width}");
                var scale = fit.Bounds.Width / donut.Bounds.Width;
                if (fits)
                    Assert.Equal(1, scale, 3);
                else
                    Assert.InRange(scale, 0.4, 0.99);
                // The column beside it is not covered: the palette starts right of the card.
                var palette = page.FindControl<StackPanel>("PaletteColumn")!;
                Assert.True(card.TranslatePoint(new Point(card.Bounds.Width, 0), page)!.Value.X
                    <= palette.TranslatePoint(default, page)!.Value.X + 0.5);
                window.Close();
            }
        }
        finally
        {
            store.Settings.Donut.OuterRadius = saved;
        }
    }

    /// <summary>Clicks slice <paramref name="index"/> of the page's donut in its middle (not on its ✎).</summary>
    private static void ClickSlice(Window window, DonutMenu donut, int index, int count)
    {
        var center = donut.Bounds.Width / 2;
        var mid = DonutGeometry.MidAngle(index, count);
        var r = donut.OuterRadius * (1 + donut.HoleRatio) / 2;
        var at = donut.TranslatePoint(new Point(center + r * Math.Sin(mid), center - r * Math.Cos(mid)), window)!.Value;
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        RenderTests.Pump();
    }

    [AvaloniaFact]
    public void A_group_clicked_in_the_editor_opens_like_a_folder()
    {
        var (window, page, vm) = Open();
        var store = AppHost.Get<UserDataStore>();
        var loc = AppHost.Get<ILocalizer>();
        var group = store.FindPreset("pdf-edit")!;
        var members = group.SubPresetIds.ToList();
        try
        {
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile.Id == "pdf");
            vm.EditingBack = true;
            RenderTests.Pump();
            var face = vm.SelectedProfile.Profile.BackPresetIds!;
            var donut = page.FindControl<DonutMenu>("Donut")!;

            // A click on the group's slice (not its ✎, which still opens its settings) opens it.
            ClickSlice(window, donut, face.IndexOf("pdf-edit"), vm.DonutItems.Count);
            Assert.Same(group, vm.OpenGroup);
            Assert.Equal(members, vm.DonutItems.Select(i => i.Id));
            Assert.Equal(loc.DisplayName(group), vm.CenterTitle);
            Assert.Equal($"{members.Count} / {Preset.MaxSubPresets}", vm.CenterSubtitle);
            Assert.False(page.FindControl<Border>("FaceSwitch")!.IsEffectivelyVisible);
            Assert.True(page.FindControl<Button>("GroupBack")!.IsEffectivelyVisible);
            Assert.Equal(loc.DisplayName(vm.SelectedProfile.Profile), vm.GroupBackText);
            Assert.True(page.FindControl<TextBlock>("GroupHint")!.IsEffectivelyVisible);

            // Never a group in a group, nor offered for one; what the group holds isn't offered either.
            Assert.DoesNotContain(vm.Palette, chip => store.FindPreset(chip.PresetId)!.IsGroup);
            Assert.DoesNotContain(vm.Palette, chip => members.Contains(chip.PresetId));
            Assert.Contains(vm.Palette, chip => chip.PresetId == "compress-pdf"); // on the face, not in the group
            // Full at six.
            Assert.Equal(Preset.MaxSubPresets, members.Count);
            Assert.False(vm.Insert("compress-pdf", 0));
            Assert.Equal(loc.Format("group.full", Preset.MaxSubPresets), vm.Message);
            // Taking one out, adding and reordering edit the group, and the face stays as it was.
            var faceBefore = face.ToList();
            vm.RemoveAt(members.Count - 1);
            Assert.Equal(members.Take(members.Count - 1), group.SubPresetIds);
            // With room, still no group in it, dragged or double-clicked.
            Assert.False(vm.Insert(store.Presets.First(p => p.IsGroup && p != group).Id, 0));
            Assert.Equal(members.Take(members.Count - 1), group.SubPresetIds);
            Assert.True(vm.Insert("compress-pdf", 0));
            Assert.Equal("compress-pdf", group.SubPresetIds[0]);
            vm.Move(0, 3);
            Assert.Equal("compress-pdf", group.SubPresetIds[3]);
            Assert.Equal(group.SubPresetIds, vm.DonutItems.Select(i => i.Id));
            Assert.Equal(faceBefore, face);

            // Back: the whole donut again.
            page.FindControl<Button>("GroupBack")!.Command!.Execute(null);
            RenderTests.Pump();
            Assert.Null(vm.OpenGroup);
            Assert.Equal(face, vm.DonutItems.Select(i => i.Id));
            Assert.True(page.FindControl<Border>("FaceSwitch")!.IsEffectivelyVisible);
            Assert.Contains(vm.Palette, chip => chip.PresetId == "rotate"); // groups are offered again
        }
        finally
        {
            group.SubPresetIds.Clear();
            group.SubPresetIds.AddRange(members);
            store.SaveLibrary();
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData("en", FlowDirection.LeftToRight)]
    [InlineData("ar", FlowDirection.RightToLeft)]
    public void The_way_back_out_of_a_group_points_back_along_the_line(string language, FlowDirection direction)
    {
        TestServices.EnsureInitialized(language);
        var main = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>(), AppHost.Get<UserDataStore>());
        main.Navigate("toolbar");
        var window = new MainWindow { DataContext = main, Width = 1280, Height = 900 };
        window.Closed += (_, _) => main.Dispose();
        window.Show();
        try
        {
            RenderTests.Pump();
            var page = window.GetVisualDescendants().OfType<ToolbarPage>().Single();
            var vm = (ToolbarPageViewModel)page.DataContext!;
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile.Id == "images");
            vm.EditingBack = true;
            vm.OpenGroupAt(vm.SelectedProfile.Profile.BackPresetIds!.IndexOf("rotate"));
            RenderTests.Pump();
            // The chevron is drawn turned (it is ChevronRight) and follows the line: left in English, right in Arabic.
            var chevron = page.FindControl<Button>("GroupBack")!.GetVisualDescendants().OfType<PathIcon>().Single();
            Assert.Equal(direction, chevron.FlowDirection);
            Assert.Equal(direction, page.FlowDirection);
        }
        finally
        {
            window.Close();
            TestServices.EnsureInitialized("en");
        }
    }

    [AvaloniaFact]
    public void An_open_group_closes_with_its_face_its_donut_or_itself_and_keeps_the_faces_rules()
    {
        var (window, _, vm) = Open();
        var store = AppHost.Get<UserDataStore>();
        var loc = AppHost.Get<ILocalizer>();
        var images = store.Profiles.Single(p => p.Id == "images");
        var (front, back) = (images.PresetIds.ToList(), images.BackPresetIds!.ToList());
        var rotate = store.FindPreset("rotate")!;
        var members = rotate.SubPresetIds.ToList();
        var copy = new Preset
        {
            Name = "Copy to test",
            Kind = PresetKind.CopyToFolder,
            TargetFormat = Preset.CopyTarget,
            Output = new OutputRule { Location = OutputLocation.CustomFolder, CustomFolder = Path.GetTempPath() },
        };
        store.Presets.Add(copy);
        try
        {
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile.Id == "images");
            vm.EditingBack = true;
            vm.OpenGroupAt(images.BackPresetIds!.IndexOf("rotate"));
            Assert.Same(rotate, vm.OpenGroup);
            // On the back face a group may hold a Copy to folder preset, as the face may.
            Assert.True(vm.Insert(copy.Id, 0));
            vm.RemoveAt(0);
            // The Front / Back switch, another donut: back to the whole donut, even with the group on both faces.
            images.PresetIds[^1] = "rotate";
            vm.EditingBack = false;
            Assert.Null(vm.OpenGroup);
            images.PresetIds[^1] = front[^1];
            vm.EditingBack = true;
            vm.OpenGroupAt(images.BackPresetIds!.IndexOf("rotate"));
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile.Id == "pdf");
            Assert.Null(vm.OpenGroup);

            // A group on the front face: a Copy to folder preset can't go in, as it can't go on the front face.
            images.BackPresetIds!.Remove("rotate");
            images.PresetIds[^1] = "rotate";
            store.SaveLibrary();
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile.Id == "images");
            vm.EditingBack = false;
            vm.OpenGroupAt(images.PresetIds.IndexOf("rotate"));
            Assert.Same(rotate, vm.OpenGroup);
            Assert.False(vm.Insert(copy.Id, 0));
            Assert.Equal(loc["presets.copy_back"], vm.Message);
            Assert.Equal(members, rotate.SubPresetIds);

            // Taken off the face (dragged off elsewhere, or the profile reset), it closes on the next rebuild.
            images.PresetIds[^1] = front[^1];
            vm.PaletteSearch = "x";
            Assert.Null(vm.OpenGroup);
        }
        finally
        {
            store.Presets.Remove(copy);
            rotate.SubPresetIds.Clear();
            rotate.SubPresetIds.AddRange(members);
            images.PresetIds.Clear();
            images.PresetIds.AddRange(front);
            images.BackPresetIds!.Clear();
            images.BackPresetIds.AddRange(back);
            store.SaveLibrary();
            window.Close();
        }
    }
}
