// Settings → Donut toolbar as an editor (#873): the sliders save the donut's look without applying every setting again;
// the donut in the page is drawn as it opens, only smaller when its column is narrower; a group clicked in the editor
// opens like a folder to edit what it fans out; and Edit Donut Size shows the donut on screen at its real size while
// it is adjusted or a group is edited, beside the window, never over it.

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
using Filee.Core.Formats;
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

    [AvaloniaFact]
    public void Edit_donut_size_shows_the_donut_on_screen_at_its_real_size_while_it_is_adjusted()
    {
        var (window, page, vm) = Open();
        var store = AppHost.Get<UserDataStore>();
        var saved = (store.Settings.Donut.OuterRadius, store.Settings.Donut.Opacity);
        var savedScale = vm.LabelScale;
        try
        {
            Assert.Null(page.Preview);
            Assert.False(page.FindControl<Border>("LookCard")!.IsVisible);
            page.FindControl<Button>("EditSize")!.Command!.Execute(null);
            RenderTests.Pump();
            Assert.False(page.FindControl<Border>("EditorCard")!.IsVisible);
            Assert.True(page.FindControl<Border>("LookCard")!.IsEffectivelyVisible);
            // The look settings and this donut's labels are all in the panel, and nowhere else.
            foreach (var name in new[] { "SizeSlider", "HoleSlider", "OpacitySlider", "RingSizeSlider", "RingSpreadSlider", "CaptionsToggle", "SliceLabelsChoice", "LabelScaleSlider" })
            {
                var control = page.FindControl<Control>(name)!;
                Assert.True(control.IsEffectivelyVisible, name);
                Assert.Contains(control.GetVisualAncestors().OfType<Border>(), b => b.Name == "LookCard");
            }
            var preview = page.Preview!;
            Assert.True(preview.IsVisible);
            // Not pinned above everything, and not taking the keyboard from the sliders.
            Assert.False(preview.Topmost);
            Assert.False(preview.ShowActivated);
            var donut = preview.DonutControl;
            Assert.False(donut.IsEditMode);
            Assert.Equal(vm.OuterRadius, donut.OuterRadius);
            // Filled as the donut opens for this profile's files.
            Assert.Equal(vm.SelectedProfile!.Profile.PresetIds.Count, donut.Items!.Count);
            Assert.Equal(AppHost.Get<ILocalizer>().Format("donut.files_same", 1,
                FormatRegistry.FindByExtension(vm.SelectedProfile.Profile.Extensions[0])!.DisplayName), donut.CenterTitle);

            // It follows every slider, at once.
            page.FindControl<Slider>("SizeSlider")!.Value = 200;
            page.FindControl<Slider>("LabelScaleSlider")!.Value = 125;
            page.FindControl<Slider>("OpacitySlider")!.Value = 60;
            RenderTests.Pump();
            Assert.Equal(200, donut.OuterRadius);
            Assert.Equal(1.25, donut.LabelScale, 3);
            Assert.Equal(0.6, donut.SliceOpacity, 3);
            // The window, sized to the donut, rounds up to a whole pixel.
            var side = DonutMenu.SideFor(200, donut.ReserveSubRing, donut.GroupRingSize);
            Assert.InRange(preview.Bounds.Width, side - 0.01, side + 1);

            page.FindControl<Button>("LookDone")!.Command!.Execute(null);
            RenderTests.Pump();
            Assert.False(preview.IsVisible);
            Assert.True(page.FindControl<Border>("EditorCard")!.IsVisible);

            // Esc on the donut on screen is Done too.
            vm.AdjustLookCommand.Execute(null);
            RenderTests.Pump();
            Assert.True(preview.IsVisible);
            preview.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            RenderTests.Pump();
            Assert.False(vm.IsAdjustingLook);
            Assert.False(preview.IsVisible);

            // Another page ends it and puts the donut away for good.
            vm.AdjustLookCommand.Execute(null);
            RenderTests.Pump();
            Assert.True(page.Preview!.IsVisible);
            ((MainWindowViewModel)window.DataContext!).Navigate("general");
            RenderTests.Pump();
            Assert.False(preview.IsVisible);
            Assert.False(vm.IsAdjustingLook);
        }
        finally
        {
            vm.LabelScale = savedScale;
            (store.Settings.Donut.OuterRadius, store.Settings.Donut.Opacity) = saved;
            store.SaveDonutLook();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Every_slider_of_the_panel_shows_its_value()
    {
        var (window, page, vm) = Open();
        var saved = vm.OuterRadius;
        try
        {
            vm.AdjustLookCommand.Execute(null);
            RenderTests.Pump();
            // The value beside each slider, in its last column: the size in pixels (the donut's radius, as it is
            // stored), the others in percent.
            string ValueOf(string slider) => ((Grid)page.FindControl<Slider>(slider)!.Parent!).Children.OfType<TextBlock>()
                .Single(t => Grid.GetColumn(t) == 2).Text ?? "";
            page.FindControl<Slider>("SizeSlider")!.Value = 171.4;
            RenderTests.Pump();
            Assert.Equal("171 px", ValueOf("SizeSlider"));
            vm.OuterRadius = 150;
            vm.HoleRatio = 42;
            RenderTests.Pump();
            Assert.Equal("150 px", ValueOf("SizeSlider"));
            Assert.Equal((0.42).ToString("P0", System.Globalization.CultureInfo.CurrentCulture), ValueOf("HoleSlider"));
            Assert.All(new[] { "OpacitySlider", "RingSizeSlider", "RingSpreadSlider", "LabelScaleSlider" },
                name => Assert.EndsWith("%", ValueOf(name)));
        }
        finally
        {
            vm.OuterRadius = saved;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_donut_on_screen_leaves_out_the_turned_off_fields_as_the_donut_does()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var before = store.Settings.TurnedOffFields.ToList();
        store.Settings.TurnedOffFields = FormatFields.All.Where(f => f.CanTurnOff).Select(f => f.Id).ToList();
        // The donut's own field stays (FORMAT-RULES rule 4); the others' formats leave its slices, as when it opens.
        HashSet<string> OffFor(ToolbarProfile p) => store.Settings.TurnedOffFields
            .Except(FormatFields.Of(FormatRegistry.FindByExtension(p.Extensions[0])!).Select(f => f.Id), StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Shown(ToolbarProfile p, string id) => store.FindPreset(id) is { } preset
            && (preset.IsGroup || FormatFields.IsShown(preset.TargetFormat, OffFor(p)));
        var profile = store.Profiles.First(p => !p.IsFallback && p.Extensions.Count > 0
            && FormatRegistry.FindByExtension(p.Extensions[0]) is not null && p.PresetIds.Any(id => !Shown(p, id)));
        var (window, page, vm) = Open();
        try
        {
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile == profile);
            vm.AdjustLookCommand.Execute(null);
            RenderTests.Pump();
            var expected = profile.PresetIds.Where(id => Shown(profile, id)).ToList();
            Assert.Equal(expected, page.Preview!.DonutControl.Items!.Select(i => i.Id).Where(expected.Contains));
            Assert.DoesNotContain(page.Preview.DonutControl.Items!, i => !Shown(profile, i.Id));
            Assert.True(page.Preview.DonutControl.Items!.Count < profile.PresetIds.Count, profile.Id);
        }
        finally
        {
            store.Settings.TurnedOffFields = before;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_group_open_in_the_editor_is_held_open_on_the_donut_on_screen_and_follows_its_edits()
    {
        var (window, page, vm) = Open();
        var store = AppHost.Get<UserDataStore>();
        var group = store.FindPreset("pdf-edit")!;
        var members = group.SubPresetIds.ToList();
        try
        {
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile.Id == "pdf");
            vm.EditingBack = true;
            var index = vm.SelectedProfile.Profile.BackPresetIds!.IndexOf("pdf-edit");
            vm.OpenGroupAt(index);
            RenderTests.Pump();
            // The donut on screen turns to the group's face and holds its ring open.
            var preview = page.Preview!;
            Assert.True(preview.IsVisible);
            Assert.True(((RadialViewModel)preview.DataContext!).IsBackFace);
            Assert.Equal("pdf-edit", preview.DonutControl.HeldGroupId);
            Assert.Equal(index, preview.DonutControl.OpenedGroup);
            // Edit Donut Size isn't offered while a group is open.
            Assert.False(page.FindControl<Button>("EditSize")!.IsEffectivelyVisible);

            vm.RemoveAt(members.Count - 1);
            Assert.True(vm.Insert("compress-pdf", 0));
            vm.Move(0, 3);
            RenderTests.Pump();
            Assert.Equal(group.SubPresetIds, preview.DonutControl.Items![index].Children!.Select(c => c.Id));
            Assert.Equal(index, preview.DonutControl.OpenedGroup);

            vm.CloseGroupCommand.Execute(null);
            RenderTests.Pump();
            Assert.False(preview.IsVisible);
        }
        finally
        {
            group.SubPresetIds.Clear();
            group.SubPresetIds.AddRange(members);
            store.SaveLibrary();
            window.Close();
        }
    }

    private static DonutItem Item(string id, bool group = false) => new()
    {
        Id = id,
        Label = id,
        Children = group ? [new DonutItem { Id = id + "-a", Label = "A" }, new DonutItem { Id = id + "-b", Label = "B" }] : null,
    };

    [AvaloniaFact]
    public void A_held_group_stays_open_and_comes_back_after_another_one_opens_over_it()
    {
        TestServices.EnsureInitialized("en");
        var donut = new DonutMenu { Items = [Item("plain"), Item("one", true), Item("two", true), Item("other")], HeldGroupId = "one" };
        var host = new Window { Content = donut, SizeToContent = SizeToContent.WidthAndHeight };
        host.Show();
        RenderTests.Pump();
        try
        {
            Assert.Equal(1, donut.OpenedGroup);
            // What folds a ring (the pointer leaving, a drop) leaves it open, and Esc goes on to the window.
            donut.CollapseGroup();
            Assert.Equal(1, donut.OpenedGroup);
            Assert.False(donut.HandleKey(Key.Escape));
            Assert.Equal(1, donut.OpenedGroup);
            // Another group opens over it while the pointer is on it, and folding that one brings it back.
            donut.OpenGroup(2);
            Assert.Equal(2, donut.OpenedGroup);
            Assert.True(donut.HandleKey(Key.Escape)); // a ring that isn't held folds on Esc as before
            Assert.Equal(1, donut.OpenedGroup);
            // Held by id: it follows its slice when the slices change, and an id not on them holds nothing.
            donut.Items = [Item("one", true), Item("plain")];
            Assert.Equal(0, donut.OpenedGroup);
            donut.HeldGroupId = "elsewhere";
            Assert.Equal(-1, donut.OpenedGroup);
            donut.HeldGroupId = "one";
            Assert.Equal(0, donut.OpenedGroup);
            donut.HeldGroupId = null;
            Assert.Equal(-1, donut.OpenedGroup);
            // The editor never holds one.
            var editor = new DonutMenu { IsEditMode = true, Items = [Item("one", true)], HeldGroupId = "one" };
            Assert.Equal(-1, editor.OpenedGroup);
        }
        finally
        {
            host.Close();
        }
    }

    [Fact]
    public void The_donut_goes_beside_the_window_and_the_window_slides_over_when_there_is_no_room()
    {
        var gap = ToolbarPage.PreviewGap;
        var area = new PixelRect(0, 25, 1728, 1050);
        const int side = 540;
        // Room on the right: there, level with the window's middle.
        Assert.Equal(new PixelPoint(80 + 1080 + gap + side / 2, 150 + 760 / 2), ToolbarPage.Beside(area, new PixelRect(80, 150, 1080, 760), side));
        // No room on the right, room on the left: the left.
        Assert.Equal(new PixelPoint(1728 - 1080 - gap - side / 2, 150 + 760 / 2), ToolbarPage.Beside(area, new PixelRect(1728 - 1080, 150, 1080, 760), side));
        // Low on the screen: still all of it on the screen.
        Assert.Equal(1075 - side / 2, ToolbarPage.Beside(area, new PixelRect(80, 700, 1080, 760), side)!.Value.Y);
        // Centred on the screen: no room either side...
        var centred = new PixelRect((1728 - 1080) / 2, 150, 1080, 760);
        Assert.Null(ToolbarPage.Beside(area, centred, side));
        // ...so the window slides the shorter way, just far enough, and the donut fits beside it.
        var x = ToolbarPage.SlideFor(area, centred, side)!.Value;
        Assert.Equal(1728 - gap - side - 1080, x);
        Assert.NotNull(ToolbarPage.Beside(area, centred.WithX(x), side));
        // A screen too narrow for both: no slide.
        Assert.Null(ToolbarPage.SlideFor(new PixelRect(0, 0, 1440, 900), centred, side));
    }
}
