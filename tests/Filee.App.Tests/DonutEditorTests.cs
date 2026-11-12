// Settings → Donut toolbar as an editor (#873): the sliders save the donut's look without applying every setting again;
// the donut in the page is drawn as it opens, only smaller when its column is narrower; a group clicked in the editor
// opens like a folder to edit what it fans out; and Edit Donut Size shows the donut on screen at its real size while it
// is adjusted or a group is edited, beside the window (over it only when there is no room anywhere), holding a group
// open (or an example ring) once a ring setting changes.

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
        // As WindowService does in the app: a page left open would go on reacting to every library change after the
        // test.
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
            // In the settings at once, without the theme, triggers and integration that a settings change applies again
            // (that is what made the sliders stick); written once the slider rests.
            Assert.Equal(189, store.Settings.Donut.OuterRadius);
            Assert.Equal(0, applied);
            Assert.True(DonutLookSave.IsPending);
            DonutLookSave.Flush();
            Assert.Contains("\"outerRadius\": 189", File.ReadAllText(Path.Combine(store.Directory, "settings.json")));
            Assert.Equal(0, applied);
            // The donut takes it the next time it opens.
            var donut = new RadialViewModel(store, AppHost.Get<ILocalizer>(), AppHost.Get<PresetAvailability>());
            donut.Load(["/tmp/picture.png"], clickMode: true);
            Assert.Equal(189, donut.OuterRadius);
            Assert.Equal(0.7, donut.SliceOpacity, 3);
            Assert.Equal(1.25, donut.GroupRingSize, 3);

            // The Theme page's opacity slider too; leaving the page writes what is still waiting.
            ((MainWindowViewModel)window.DataContext!).Navigate("theme");
            RenderTests.Pump();
            var theme = Assert.IsType<ThemePageViewModel>(((MainWindowViewModel)window.DataContext!).CurrentPage);
            theme.DonutOpacity = 55;
            Assert.Equal(0.55, store.Settings.Donut.Opacity, 3);
            Assert.True(DonutLookSave.IsPending);
            ((MainWindowViewModel)window.DataContext!).Navigate("general");
            Assert.False(DonutLookSave.IsPending);
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
    public void A_slider_moving_writes_the_settings_once_it_rests()
    {
        TestServices.EnsureInitialized("en");
        var dir = Directory.CreateTempSubdirectory("filee-donut-look-").FullName;
        var store = new UserDataStore(dir);
        store.Load();
        store.SaveSettings();
        var path = Path.Combine(dir, "settings.json");
        var before = File.ReadAllText(path);
        try
        {
            // Every step of a drag: the settings in memory change, the file waits.
            foreach (var radius in new[] { 160.0, 170.0, 180.0 })
            {
                store.Settings.Donut.OuterRadius = radius;
                DonutLookSave.Schedule(store);
            }
            Assert.Equal(before, File.ReadAllText(path));
            Assert.True(DonutLookSave.IsPending);
            DonutLookSave.Flush();
            Assert.Contains("\"outerRadius\": 180", File.ReadAllText(path));
            Assert.False(DonutLookSave.IsPending);
            // Nothing waiting: a flush writes nothing.
            File.WriteAllText(path, before);
            DonutLookSave.Flush();
            Assert.Equal(before, File.ReadAllText(path));

            // Left to rest, it is written on its own.
            store.Settings.Donut.OuterRadius = 190;
            DonutLookSave.Schedule(store);
            Assert.Equal(before, File.ReadAllText(path));
            RunUntil(() => !DonutLookSave.IsPending, DonutLookSave.Delay * 4);
            Assert.False(DonutLookSave.IsPending);
            Assert.Contains("\"outerRadius\": 190", File.ReadAllText(path));
        }
        finally
        {
            DonutLookSave.Flush();
            Directory.Delete(dir, recursive: true);
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
                try
                {
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
                }
                finally
                {
                    window.Close();
                }
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
            Assert.Equal(loc["group.copy_front"], vm.Message);
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
            Assert.Null(page.Preview); // closed, not only hidden
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
        var saved = (vm.OuterRadius, vm.HoleRatio);
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
            (vm.OuterRadius, vm.HoleRatio) = saved;
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

    // The donut at the default size with a group's ring (466), and at its largest: Size 220, the ring at 150 % (755).
    // Properties, not fields: DonutMenu needs the Avalonia platform, which these tests (on it too) start.
    private static double Default => DonutMenu.SideFor(150, true, 1);
    private static double Largest => DonutMenu.SideFor(220, true, DonutSettings.MaxGroupRingSize);

    private static PreviewPlace Place(PixelRect window, PreviewScreen[] screens, double? size = null, PixelPoint? dragged = null,
        int? minWidth = 860, bool overRight = true) =>
        DonutPreviewPlacement.Place(window, screens, 0, size ?? Default, dragged, minWidth, overRight);

    /// <summary>Clear of the window (or of where it moved to) and all of it on the screen.</summary>
    private static void AssertClear(PreviewPlace place, PixelRect window, PreviewScreen screen, double size)
    {
        Assert.False(place.Over);
        var box = place.BoxFor(DonutPreviewPlacement.Pixels(size, screen));
        Assert.False(box.Intersects(place.MovedWindow ?? window), $"{box} over {place.MovedWindow ?? window}");
        Assert.True(screen.Area.Contains(box), $"{box} off {screen.Area}");
    }

    [AvaloniaTheory]
    // A wide screen, the window on the left, in the middle, on the right; scaled 100 % and 200 % (a retina Windows).
    [InlineData(2560, 1400, 1.0, 100)]
    [InlineData(2560, 1400, 1.0, 740)]
    [InlineData(2560, 1400, 1.0, 1400)]
    [InlineData(5120, 2800, 2.0, 1480)]
    // Laptops with no room beside the window as it is: it moves over, narrower if it must.
    [InlineData(1440, 900, 1.0, 180)]
    [InlineData(1470, 956, 1.0, 195)]
    [InlineData(1366, 768, 1.0, 143)]
    [InlineData(1920, 1080, 1.25, 120)]
    public void The_donut_goes_beside_the_window_never_over_it_and_the_window_makes_room_when_it_must(int width, int height, double scaling, int x)
    {
        var screen = new PreviewScreen(new PixelRect(0, 25, width, height - 25), scaling);
        var window = new PixelRect(x, 100, (int)(1080 * scaling), Math.Min((int)(760 * scaling), height - 140));
        var place = Place(window, [screen], minWidth: (int)(860 * scaling));
        AssertClear(place, window, screen, Default);
        // Moved over only when there was no room beside it as it was, and kept as wide as it could be.
        var room = DonutPreviewPlacement.Beside(screen.Area, window, DonutPreviewPlacement.Pixels(Default, screen));
        Assert.Equal(room is null, place.MovedWindow is not null);
        if (place.MovedWindow is { } moved)
            Assert.InRange(moved.Width, (int)(860 * scaling), window.Width);
    }

    [AvaloniaFact]
    public void With_no_room_anywhere_the_donut_goes_over_the_column_not_in_use_and_above_the_window()
    {
        // 1280 × 720 at 150 %: too small for the window at its narrowest and the donut beside it.
        var screen = new PreviewScreen(new PixelRect(0, 0, 1920, 1080), 1.5);
        var window = new PixelRect(100, 60, 1620, 900);
        var right = Place(window, [screen], minWidth: 1290, overRight: true);
        Assert.True(right.Over);
        Assert.Equal((PreviewAnchor.Right, screen.Area.Right), (right.Anchor, right.Point.X));
        var left = Place(window, [screen], minWidth: 1290, overRight: false);
        Assert.Equal((PreviewAnchor.Left, screen.Area.X), (left.Anchor, left.Point.X));
        // Maximized or full screen (it can't move): the same, or another screen when there is one.
        Assert.True(Place(window, [screen], minWidth: null).Over);
        var other = new PreviewScreen(new PixelRect(1920, 0, 1600, 900), 1.0);
        var there = Place(window, [screen, other], minWidth: null);
        Assert.Equal((PreviewAnchor.Centre, other.Area.Center, false), (there.Anchor, there.Point, there.Over));
    }

    [AvaloniaFact]
    public void Another_screen_is_judged_at_its_own_scaling()
    {
        var home = new PreviewScreen(new PixelRect(0, 0, 1400, 900), 1.0);
        var window = new PixelRect(0, 0, 1400, 900); // fills its screen
        // Wide enough in pixels for the donut at 100 %, not at 200 %: the donut goes there only at 100 %.
        var pixels = (int)Math.Ceiling(Default * 1.5);
        var at100 = new PreviewScreen(new PixelRect(1400, 0, pixels, pixels), 1.0);
        Assert.Equal(at100.Area.Center, Place(window, [home, at100], minWidth: null).Point);
        Assert.True(Place(window, [home, at100 with { Scaling = 2.0 }], minWidth: null).Over);

        // A screen less tall than the donut (2560 × 1080 at 150 %, the donut at its largest): another screen with room
        // first; beside the window, past the screen's edges, only with none.
        var low = new PreviewScreen(new PixelRect(0, 0, 2560, 1008), 1.5);
        var tall = new PreviewScreen(new PixelRect(2560, 0, 3840, 2100), 1.5);
        var narrow = new PixelRect(100, 100, 1254, 800);
        var elsewhere = DonutPreviewPlacement.Place(narrow, [low, tall], 0, Largest, null, null, false);
        Assert.Equal((PreviewAnchor.Centre, tall.Area.Center), (elsewhere.Anchor, elsewhere.Point));
        Assert.False(DonutPreviewPlacement.Place(narrow, [low], 0, Largest, null, null, false).Over);

        // A window reaching onto the other screen, over its middle: not there, under it.
        var other = new PreviewScreen(new PixelRect(1400, 0, 1400, 900), 1.0);
        var across = new PixelRect(0, 0, 2400, 900);
        Assert.True(Place(across, [home, other], minWidth: null).Over);
    }

    [AvaloniaFact]
    public void Where_it_was_dragged_counts_only_while_all_of_it_is_on_a_screen_and_clear_of_the_window()
    {
        var screen = new PreviewScreen(new PixelRect(0, 0, 2560, 1400), 1.0);
        var window = new PixelRect(100, 100, 1080, 760);
        // Dragged below the window: there, by its centre.
        var below = Place(window, [screen], dragged: new PixelPoint(500, 1000));
        Assert.Equal((PreviewAnchor.Centre, new PixelPoint(600, 1100)), (below.Anchor, below.Point));
        // Dragged over the window, or partly off the screen: beside the window instead.
        Assert.Equal(PreviewAnchor.Left, Place(window, [screen], dragged: new PixelPoint(400, 300)).Anchor);
        Assert.Equal(PreviewAnchor.Left, Place(window, [screen], dragged: new PixelPoint(2400, 300)).Anchor);
        // The same place with the donut at its largest, now reaching the window: beside it.
        Assert.Equal(PreviewAnchor.Left, Place(window, [screen], size: Largest, dragged: new PixelPoint(500, 1000)).Anchor);
    }

    [AvaloniaFact]
    public void A_donut_beside_the_window_grows_away_from_it_and_is_placed_again_when_it_no_longer_fits()
    {
        var screen = new PreviewScreen(new PixelRect(0, 0, 2560, 1400), 1.0);
        var window = new PixelRect(100, 100, 1080, 760);
        var small = (int)Default;
        var large = (int)Math.Ceiling(Largest);
        var right = Place(window, [screen]);
        Assert.Equal(PreviewAnchor.Left, right.Anchor);
        Assert.Equal(right.BoxFor(small).X, right.BoxFor(large).X); // its edge facing the window stays
        Assert.True(right.Fits(large, window, [screen]));
        var left = Place(new PixelRect(1400, 100, 1080, 760), [screen]);
        Assert.Equal(PreviewAnchor.Right, left.Anchor);
        Assert.Equal(left.BoxFor(small).Right, left.BoxFor(large).Right);
        // Beside a window near the screen's edge, grown past the edge: it no longer fits there and is placed again.
        var tight = new PixelRect(100, 100, 1900, 760);
        var beside = Place(tight, [screen], minWidth: null);
        Assert.True(beside.Fits(small, tight, [screen]));
        Assert.False(beside.Fits(large, tight, [screen]));
        Assert.True(Place(tight, [screen], size: Largest, minWidth: null).Over);
    }

    [AvaloniaFact]
    public void A_ring_setting_holds_this_donuts_group_open_on_screen_and_the_picker_chooses_which()
    {
        var (window, page, vm) = Open();
        var store = AppHost.Get<UserDataStore>();
        var saved = (store.Settings.Donut.GroupRingSize, store.Settings.Donut.GroupRingSpread);
        var pdf = store.Profiles.Single(p => p.Id == "pdf");
        var (front, backFace) = (pdf.PresetIds.ToList(), pdf.BackPresetIds!.ToList());
        var other = store.Presets.First(p => p.IsGroup && p.Id != "pdf-edit" && p.SubPresetIds.Count > 0);
        try
        {
            // One group, PDF Edit, on the back face (whatever other groups the library puts on this donut).
            pdf.PresetIds.RemoveAll(id => store.FindPreset(id)?.IsGroup == true);
            pdf.BackPresetIds!.RemoveAll(id => id != "pdf-edit" && store.FindPreset(id)?.IsGroup == true);
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile.Id == "pdf");
            vm.AdjustLookCommand.Execute(null);
            RenderTests.Pump();
            var preview = page.Preview!;
            // Until a ring setting changes, nothing is held: resting on a group opens it as on the real donut.
            Assert.Null(preview.DonutControl.HeldGroupId);
            Assert.False(((RadialViewModel)preview.DataContext!).IsBackFace);
            Assert.False(page.FindControl<ComboBox>("GroupShownChoice")!.IsEffectivelyVisible);

            page.FindControl<Slider>("RingSizeSlider")!.Value = 125;
            RenderTests.Pump();
            // The donut's one group, on its back face: the donut on screen turns to it and holds it open.
            Assert.Equal(new ToolbarPageViewModel.RingGroupChoice("pdf-edit", vm.RingGroups[0].Name, true), vm.ShownGroup);
            Assert.True(((RadialViewModel)preview.DataContext!).IsBackFace);
            Assert.Equal("pdf-edit", preview.DonutControl.HeldGroupId);
            Assert.Equal(pdf.BackPresetIds!.IndexOf("pdf-edit"), preview.DonutControl.OpenedGroup);
            Assert.False(page.FindControl<ComboBox>("GroupShownChoice")!.IsEffectivelyVisible);
            // Restyling keeps it open.
            page.FindControl<Slider>("RingSpreadSlider")!.Value = 150;
            RenderTests.Pump();
            Assert.Equal(1.5, preview.DonutControl.GroupRingSpread, 3);
            Assert.Equal(pdf.BackPresetIds!.IndexOf("pdf-edit"), preview.DonutControl.OpenedGroup);

            // Two groups: the picker shows, front face first, and picking one turns the donut to it.
            vm.DoneAdjustingLookCommand.Execute(null);
            vm.EditingBack = false;
            vm.RemoveAt(0); // the front face is full
            Assert.True(vm.Insert(other.Id, 0));
            vm.AdjustLookCommand.Execute(null);
            page.FindControl<Slider>("RingSizeSlider")!.Value = 130;
            RenderTests.Pump();
            Assert.Equal([(other.Id, false), ("pdf-edit", true)], vm.RingGroups.Select(g => (g.Id, g.Back)));
            Assert.EndsWith(" · " + AppHost.Get<ILocalizer>()["toolbar.face_back"], vm.RingGroups[1].Name);
            var picker = page.FindControl<ComboBox>("GroupShownChoice")!;
            Assert.True(picker.IsEffectivelyVisible);
            Assert.Equal(other.Id, vm.ShownGroup!.Id); // the first, until another is picked
            Assert.Equal(other.Id, preview.DonutControl.HeldGroupId);
            Assert.Equal(0, preview.DonutControl.OpenedGroup);
            picker.SelectedIndex = 1;
            RenderTests.Pump();
            Assert.True(((RadialViewModel)preview.DataContext!).IsBackFace);
            Assert.Equal("pdf-edit", preview.DonutControl.HeldGroupId);

            // A group clicked on the donut on screen is the one held from then on.
            var model = (RadialViewModel)preview.DataContext!;
            model.ToggleFace();
            RenderTests.Pump();
            var center = preview.DonutControl.Bounds.Width / 2;
            var mid = DonutGeometry.MidAngle(0, model.Items.Count);
            var r = preview.DonutControl.OuterRadius * (1 + preview.DonutControl.HoleRatio) / 2;
            var at = preview.DonutControl.TranslatePoint(new Point(center + r * Math.Sin(mid), center - r * Math.Cos(mid)), preview)!.Value;
            preview.MouseMove(at);
            preview.MouseDown(at, MouseButton.Left);
            preview.MouseUp(at, MouseButton.Left);
            RenderTests.Pump();
            Assert.Equal(other.Id, vm.ShownGroup!.Id);
            Assert.Equal(other.Id, preview.DonutControl.HeldGroupId);

            vm.DoneAdjustingLookCommand.Execute(null);
            Assert.False(vm.ShowsRing);
            Assert.Null(vm.ShownGroup);
            vm.AdjustLookCommand.Execute(null);
            RenderTests.Pump();
            Assert.Null(page.Preview!.DonutControl.HeldGroupId); // nothing held again until a ring setting changes
        }
        finally
        {
            pdf.PresetIds.Clear();
            pdf.PresetIds.AddRange(front);
            pdf.BackPresetIds!.Clear();
            pdf.BackPresetIds.AddRange(backFace);
            store.SaveLibrary();
            (vm.GroupRingSize, vm.GroupRingSpread) = (saved.GroupRingSize * 100, saved.GroupRingSpread * 100);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Group_shown_offers_the_groups_the_donut_on_screen_fans_out_with_its_own_field_turned_off()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var before = store.Settings.TurnedOffFields.ToList();
        var allOff = FormatFields.All.Where(f => f.CanTurnOff).Select(f => f.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A donut of a field that can be turned off, and one of its own conversions that turning every field off hides.
        var (profile, member) = store.Profiles
            .Where(p => !p.IsFallback && p.Id != "folders" && p.Extensions.Count > 0)
            .Select(p => (Profile: p, Fields: RadialViewModel.PreviewFormats(p).SelectMany(FormatFields.Of).Select(f => f.Id).ToHashSet()))
            .Where(c => c.Fields.Count > 0)
            .SelectMany(c => store.Presets
                .Where(q => !q.IsGroup && !FormatFields.IsShown(q.TargetFormat, allOff)
                    && FormatRegistry.FindById(q.TargetFormat) is { } target && FormatFields.Of(target).All(f => c.Fields.Contains(f.Id)))
                .Select(q => (c.Profile, q)))
            .First();
        var group = new Preset { Name = "Test group", Kind = PresetKind.Group };
        group.SubPresetIds.Add(member.Id);
        store.Presets.Add(group);
        var back = profile.BackPresetIds?.ToList();
        profile.BackPresetIds = [.. back ?? [], group.Id];
        store.SaveLibrary();
        // Every field off: the donut still offers its own field's conversions (FORMAT-RULES rule 4), so the group has a
        // ring to show, on the donut on screen and in Group shown.
        store.Settings.TurnedOffFields = [.. allOff];
        var (window, page, vm) = Open();
        var saved = vm.GroupRingSize;
        try
        {
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile == profile);
            vm.AdjustLookCommand.Execute(null);
            RenderTests.Pump();
            Assert.Contains(vm.RingGroups, g => g.Id == group.Id && g.Back);
            vm.GroupRingSize = saved + 10;
            vm.ShownGroup = vm.RingGroups.Single(g => g.Id == group.Id);
            RenderTests.Pump();
            Assert.Equal(group.Id, page.Preview!.DonutControl.HeldGroupId);
        }
        finally
        {
            vm.GroupRingSize = saved;
            store.Settings.TurnedOffFields = before;
            profile.BackPresetIds = back;
            store.Presets.Remove(group);
            store.SaveLibrary();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_group_picked_in_group_shown_stays_picked_when_it_is_renamed_elsewhere()
    {
        var (window, page, vm) = Open();
        var store = AppHost.Get<UserDataStore>();
        var images = store.Profiles.Single(p => p.Id == "images");
        var front = images.PresetIds.ToList();
        var group = new Preset { Name = "Picked group", Kind = PresetKind.Group };
        group.SubPresetIds.Add(store.Presets.First(p => !p.IsGroup && images.PresetIds.Contains(p.Id)).Id);
        store.Presets.Add(group);
        images.PresetIds.Insert(0, group.Id);
        store.SaveLibrary();
        var saved = vm.GroupRingSize;
        (Preset Preset, string? Name)? renamed = null;
        try
        {
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile == images);
            vm.AdjustLookCommand.Execute(null);
            vm.GroupRingSize = saved + 10;
            RenderTests.Pump();
            Assert.True(vm.RingGroups.Count >= 2);
            vm.ShownGroup = vm.RingGroups.Single(g => g.Id == group.Id);
            vm.ShownGroup = vm.RingGroups.First(g => g.Id != group.Id);
            var picked = vm.ShownGroup!;
            // Renamed in another window (the presets page): still the one shown.
            var other = store.FindPreset(picked.Id)!;
            renamed = (other, other.Name);
            other.Name = "Renamed elsewhere";
            store.SaveLibrary();
            RenderTests.Pump();
            Assert.Equal((picked.Id, picked.Back), (vm.ShownGroup!.Id, vm.ShownGroup.Back));
            Assert.Contains("Renamed elsewhere", vm.ShownGroup.Name);
        }
        finally
        {
            vm.GroupRingSize = saved;
            if (renamed is { } back)
                back.Preset.Name = back.Name!;
            store.Presets.Remove(group);
            images.PresetIds.Clear();
            images.PresetIds.AddRange(front);
            store.SaveLibrary();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_donut_with_no_group_shows_an_example_ring_while_its_ring_is_adjusted()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var saved = store.Settings.Donut.GroupRingSpread;
        var images = store.Profiles.Single(p => p.Id == "images");
        var (front, back) = (images.PresetIds.ToList(), images.BackPresetIds?.ToList());
        Window? window = null;
        try
        {
            // A donut with no group on either face, before the page reads it.
            images.PresetIds.RemoveAll(id => store.FindPreset(id)?.IsGroup == true);
            images.BackPresetIds?.RemoveAll(id => store.FindPreset(id)?.IsGroup == true);
            (window, var page, var vm) = Open();
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile.Id == "images");
            vm.AdjustLookCommand.Execute(null);
            RenderTests.Pump();
            var preview = page.Preview!;
            Assert.NotEqual(RadialViewModel.ExampleGroupId, preview.DonutControl.Items![0].Id);
            Assert.False(page.FindControl<ComboBox>("GroupShownChoice")!.IsEffectivelyVisible);

            page.FindControl<Slider>("RingSpreadSlider")!.Value = 200;
            RenderTests.Pump();
            var donut = preview.DonutControl;
            Assert.Equal(RadialViewModel.ExampleGroupId, donut.Items![0].Id);
            Assert.Equal(images.PresetIds.Count, donut.Items.Count); // in place of the top slice, not added
            Assert.Equal(0, donut.OpenedGroup);
            Assert.Equal("Example group", donut.CenterTitle);
            Assert.Equal("This donut has no groups", donut.CenterSubtitle);
            // Restyling keeps it.
            page.FindControl<Slider>("RingSpreadSlider")!.Value = 250;
            RenderTests.Pump();
            Assert.Equal(0, donut.OpenedGroup);
            Assert.Equal(2.5, donut.GroupRingSpread, 3);
            // Flipped (Tab on it), and back: each face shows it, under its title.
            var model = (RadialViewModel)preview.DataContext!;
            Assert.NotEmpty(images.BackPresetIds ?? []);
            foreach (var face in new[] { true, false })
            {
                Assert.True(model.ToggleFace());
                RenderTests.Pump();
                Assert.Equal(face, model.IsBackFace);
                Assert.Equal(RadialViewModel.ExampleGroupId, donut.Items![0].Id);
                Assert.Equal(("Example group", "This donut has no groups"), (donut.CenterTitle, donut.CenterSubtitle));
            }

            vm.DoneAdjustingLookCommand.Execute(null);
            vm.AdjustLookCommand.Execute(null);
            RenderTests.Pump();
            Assert.NotEqual(RadialViewModel.ExampleGroupId, page.Preview!.DonutControl.Items![0].Id);
        }
        finally
        {
            images.PresetIds.Clear();
            images.PresetIds.AddRange(front);
            if (back is not null)
            {
                images.BackPresetIds!.Clear();
                images.BackPresetIds.AddRange(back);
            }
            store.SaveLibrary();
            store.Settings.Donut.GroupRingSpread = saved;
            store.SaveDonutLook();
            window?.Close();
        }
    }

    private static PixelRect FrameOf(Window window) => new(window.Position, PixelSize.FromSize(window.FrameSize ?? window.Bounds.Size, 1));

    private static bool IsDragging(DonutPreviewWindow preview) =>
        (bool)typeof(DonutPreviewWindow).GetField("_dragging", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(preview)!;

    private static bool IsSliding(ToolbarPage page) =>
        (bool)typeof(ToolbarPage).GetField("_sliding", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(page)!;

    /// <summary>
    /// Lets the window's slide (a quarter of a second when animated) and timers run until <paramref name="done"/>, or
    /// <paramref name="limit"/> (2 s) has passed.
    /// </summary>
    private static void RunUntil(Func<bool> done, TimeSpan? limit = null)
    {
        var until = DateTime.UtcNow + (limit ?? TimeSpan.FromSeconds(2));
        while (!done() && DateTime.UtcNow < until)
        {
            Thread.Sleep(5);
            // A frame of the dispatcher's own: only there do its timers come due (RunJobs runs what is queued).
            var frame = new Avalonia.Threading.DispatcherFrame();
            Avalonia.Threading.Dispatcher.UIThread.Post(() => frame.Continue = false, Avalonia.Threading.DispatcherPriority.SystemIdle);
            Avalonia.Threading.Dispatcher.UIThread.PushFrame(frame);
        }
        RenderTests.Pump();
    }

    /// <summary>
    /// On a screen; clear of the window, or over it only when there was no room, then above it (owned by it).
    /// </summary>
    private static void AssertNotUnder(DonutPreviewWindow preview, Window window)
    {
        Assert.Contains(window.Screens.All, screen => screen.WorkingArea.Contains(preview.Box));
        if (preview.Place is { Over: true })
            Assert.Same(window, preview.Owner);
        else
        {
            Assert.False(preview.Box.Intersects(FrameOf(window)), $"{preview.Box} under {FrameOf(window)}");
            Assert.Null(preview.Owner);
        }
    }

    [AvaloniaFact]
    public void The_window_makes_room_for_the_donut_and_goes_back_where_it_was_unless_moved_meanwhile()
    {
        // The test screen is 1920 wide; the window, 1280 wide in its middle, leaves no room beside it.
        var (window, page, vm) = Open();
        var motion = Motion.Enabled;
        Motion.Set(false); // at once (the theme set it as the window opened)
        var main = (MainWindow)window;
        var start = window.Position;
        var width = window.ClientSize.Width;
        var saved = (vm.OuterRadius, vm.GroupRingSize);
        try
        {
            vm.AdjustLookCommand.Execute(null);
            RunUntil(() => !IsSliding(page));
            var preview = page.Preview!;
            Assert.NotEqual(start, window.Position);
            Assert.Equal((start, width), (main.RestPlace!.Value.Position, main.RestPlace.Value.ClientSize.Width));
            AssertNotUnder(preview, window);
            Assert.False(preview.Place!.Over);
            // Closing now remembers where the user had it, not where it moved over to.
            Assert.Equal(start, WindowService.MainWindowPlacement.Of(main, (MainWindowViewModel)window.DataContext!, null).Position);

            // The window made wider, or moved, over it meanwhile: placed again.
            var landed = window.Position;
            var placeWidth = window.Width;
            window.Width = 1500;
            RenderTests.Pump();
            AssertNotUnder(preview, window);
            window.Width = placeWidth;
            RenderTests.Pump();
            window.Position = landed;
            RenderTests.Pump();
            Assert.False(preview.Place!.Over); // beside it again
            window.Position = new PixelPoint(preview.Box.X - 200, landed.Y);
            RenderTests.Pump();
            AssertNotUnder(preview, window);
            window.Position = landed;
            RenderTests.Pump();

            // Grown to its largest: placed again, still never under the window.
            page.FindControl<Slider>("SizeSlider")!.Value = 220;
            page.FindControl<Slider>("RingSizeSlider")!.Value = 150;
            RenderTests.Pump();
            AssertNotUnder(preview, window);

            vm.DoneAdjustingLookCommand.Execute(null);
            RunUntil(() => main.RestPlace is null);
            Assert.Equal((start, width), (window.Position, window.ClientSize.Width));
            Assert.Null(main.RestPlace);

            // Moved by the user while the donut was out: it stays where they put it.
            vm.OuterRadius = saved.OuterRadius;
            vm.GroupRingSize = saved.GroupRingSize;
            vm.AdjustLookCommand.Execute(null);
            RunUntil(() => window.Position != start && !IsSliding(page));
            window.Position = new PixelPoint(40, 60);
            vm.DoneAdjustingLookCommand.Execute(null);
            RenderTests.Pump();
            Assert.Equal(new PixelPoint(40, 60), window.Position);
            Assert.Null(main.RestPlace);

            // Done while it is still sliding over: back where it was all the same (slowed down, to be caught on its way
            // on a busy machine).
            Motion.Set(true);
            ToolbarPage.SlideTime = TimeSpan.FromSeconds(3);
            window.Position = start;
            vm.AdjustLookCommand.Execute(null);
            RunUntil(() => window.Position != start);
            Assert.NotEqual(start, window.Position); // on its way
            Assert.True(IsSliding(page));
            // Closed on its way over, or back: it opens next time where the user had it.
            Assert.Equal(start, WindowService.MainWindowPlacement.Of(main, (MainWindowViewModel)window.DataContext!, null).Position);
            vm.DoneAdjustingLookCommand.Execute(null);
            RunUntil(() => window.Position != start);
            Assert.True(IsSliding(page));
            Assert.Equal(start, WindowService.MainWindowPlacement.Of(main, (MainWindowViewModel)window.DataContext!, null).Position);
            ToolbarPage.SlideTime = TimeSpan.FromSeconds(0.25);
            RunUntil(() => main.RestPlace is null, TimeSpan.FromSeconds(6)); // the way back at the slowed pace
            Assert.Equal(start, window.Position);
            Assert.Null(main.RestPlace);
        }
        finally
        {
            ToolbarPage.SlideTime = TimeSpan.FromSeconds(0.25);
            Motion.Set(motion);
            (vm.OuterRadius, vm.GroupRingSize) = saved;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Leaving_the_page_while_the_window_made_room_puts_it_back_at_once()
    {
        var (window, page, vm) = Open();
        var motion = Motion.Enabled;
        Motion.Set(true); // animated, as in the app
        var start = window.Position;
        try
        {
            vm.AdjustLookCommand.Execute(null);
            RunUntil(() => window.Position != start);
            Assert.NotEqual(start, window.Position); // on its way over, or there
            ((MainWindowViewModel)window.DataContext!).Navigate("general");
            Assert.Equal(start, window.Position);
            Assert.Null(((MainWindow)window).RestPlace);
        }
        finally
        {
            Motion.Set(motion);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_keyboard_goes_where_the_hidden_button_was_replaced()
    {
        var (window, page, vm) = Open();
        try
        {
            IInputElement? Focused() => window.FocusManager?.GetFocusedElement();
            var editSize = page.FindControl<Button>("EditSize")!;
            editSize.Focus();
            editSize.Command!.Execute(null);
            RenderTests.Pump();
            Assert.Same(page.FindControl<Button>("LookDone"), Focused());
            page.FindControl<Button>("LookDone")!.Command!.Execute(null);
            RenderTests.Pump();
            Assert.Same(editSize, Focused());

            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile.Id == "images");
            vm.EditingBack = true;
            vm.OpenGroupAt(vm.SelectedProfile.Profile.BackPresetIds!.IndexOf("rotate"));
            RenderTests.Pump();
            var back = page.FindControl<Button>("GroupBack")!;
            Assert.Equal("Back to Images", Avalonia.Automation.AutomationProperties.GetName(back));
            back.Focus();
            back.Command!.Execute(null);
            RenderTests.Pump();
            Assert.Same(editSize, Focused());
            // A control still shown keeps the keyboard: a profile picked in the list leaves it there.
            var list = page.FindControl<TextBox>("PaletteSearchBox")!;
            list.Focus();
            vm.OpenGroupAt(vm.SelectedProfile.Profile.BackPresetIds!.IndexOf("rotate"));
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile.Id == "pdf");
            RenderTests.Pump();
            Assert.Same(list, Focused());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_search_in_the_palette_does_not_fill_the_donut_on_screen_again()
    {
        var (window, page, vm) = Open();
        double? scale = null;
        try
        {
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile.Id == "images");
            vm.EditingBack = true;
            vm.OpenGroupAt(vm.SelectedProfile.Profile.BackPresetIds!.IndexOf("rotate"));
            RenderTests.Pump();
            var items = page.Preview!.DonutControl.Items;
            vm.PaletteSearch = "png";
            vm.PaletteSearch = "pn";
            RenderTests.Pump();
            Assert.Same(items, page.Preview.DonutControl.Items);
            // Nor after a look saved with the library (Label scale): restyled, not filled.
            scale = vm.LabelScale;
            vm.LabelScale = scale.Value + 10;
            RenderTests.Pump();
            vm.PaletteSearch = "p";
            RenderTests.Pump();
            Assert.Same(items, page.Preview.DonutControl.Items);
            vm.LabelScale = scale.Value;
            scale = null;
            RenderTests.Pump();
            items = page.Preview.DonutControl.Items;
            // An edit of the group does.
            Assert.True(vm.Insert("to-png", 0));
            RenderTests.Pump();
            Assert.NotSame(items, page.Preview.DonutControl.Items);
        }
        finally
        {
            if (scale is { } back)
                vm.LabelScale = back;
            var rotate = AppHost.Get<UserDataStore>().FindPreset("rotate")!;
            rotate.SubPresetIds.Remove("to-png");
            AppHost.Get<UserDataStore>().SaveLibrary();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_mixed_profile_that_takes_anything_shows_its_slices_ready_on_the_donut_on_screen()
    {
        var (window, page, vm) = Open();
        var store = AppHost.Get<UserDataStore>();
        try
        {
            var mixed = vm.Profiles.First(p => p.Profile.IsFallback && p.Profile.Extensions.Count == 0);
            vm.SelectedProfile = mixed;
            vm.AdjustLookCommand.Execute(null);
            RenderTests.Pump();
            var items = page.Preview!.DonutControl.Items!;
            Assert.NotEmpty(items);
            Assert.All(items, item => Assert.True(item.IsEnabled, item.Id));
            Assert.Equal(AppHost.Get<ILocalizer>().DisplayName(mixed.Profile), page.Preview.DonutControl.CenterTitle);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_group_also_on_a_front_face_takes_no_copy_to_folder_from_its_back_face_either()
    {
        var (window, _, vm) = Open();
        var store = AppHost.Get<UserDataStore>();
        var loc = AppHost.Get<ILocalizer>();
        var pdf = store.Profiles.Single(p => p.Id == "pdf");
        var front = pdf.PresetIds.ToList();
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
        var made = new Preset
        {
            Name = "Copy made here",
            Kind = PresetKind.CopyToFolder,
            TargetFormat = Preset.CopyTarget,
            Output = new OutputRule { Location = OutputLocation.CustomFolder, CustomFolder = Path.GetTempPath() },
        };
        try
        {
            // Rotate is on the images donut's back face; on another donut's front face too.
            pdf.PresetIds[^1] = "rotate";
            store.SaveLibrary();
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile.Id == "images");
            vm.EditingBack = true;
            vm.OpenGroupAt(vm.SelectedProfile.Profile.BackPresetIds!.IndexOf("rotate"));
            Assert.False(vm.Insert(copy.Id, 0));
            Assert.Equal(loc["group.copy_front"], vm.Message);
            Assert.Equal(members, rotate.SubPresetIds);
            // A new Copy to folder made while it is open: not into it either, but in the palette.
            vm.Message = null;
            vm.AddFolderCopy(made);
            Assert.Equal(members, rotate.SubPresetIds);
            Assert.Equal(loc["group.copy_front"], vm.Message);
            Assert.Same(made, store.FindPreset(made.Id));
            // On no front face: it may.
            pdf.PresetIds[^1] = front[^1];
            store.SaveLibrary();
            Assert.True(vm.Insert(copy.Id, 0));
        }
        finally
        {
            store.Presets.Remove(copy);
            store.Presets.Remove(made);
            rotate.SubPresetIds.Clear();
            rotate.SubPresetIds.AddRange(members);
            pdf.PresetIds.Clear();
            pdf.PresetIds.AddRange(front);
            store.SaveLibrary();
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_window_that_cant_move_has_the_donut_over_its_start_side_clear_of_done(bool largest)
    {
        // Maximized, the window can't make room; the test screen leaves none beside its 1280 pixels.
        var (window, page, vm) = Open();
        var saved = (vm.OuterRadius, vm.GroupRingSize);
        try
        {
            if (largest)
                (vm.OuterRadius, vm.GroupRingSize) = (220, DonutSettings.MaxGroupRingSize * 100);
            window.WindowState = WindowState.Maximized;
            RenderTests.Pump();
            var start = window.Position;
            vm.AdjustLookCommand.Execute(null);
            RenderTests.Pump();
            var preview = page.Preview!;
            Assert.True(preview.Place!.Over);
            Assert.Equal(start, window.Position); // not moved
            Assert.Null(((MainWindow)window).RestPlace);
            AssertNotUnder(preview, window);
            var done = page.FindControl<Button>("LookDone")!;
            var topLeft = done.PointToScreen(new Point(0, 0));
            var doneBox = new PixelRect(topLeft, PixelSize.FromSize(done.Bounds.Size, 1));
            Assert.False(doneBox.Intersects(preview.Box), $"Done {doneBox} under the donut {preview.Box}");
        }
        finally
        {
            (vm.OuterRadius, vm.GroupRingSize) = saved;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Minimized_while_it_made_room_the_window_still_goes_back_on_done()
    {
        var (window, page, vm) = Open();
        var motion = Motion.Enabled;
        Motion.Set(false);
        var main = (MainWindow)window;
        var start = window.Position;
        try
        {
            vm.AdjustLookCommand.Execute(null);
            RenderTests.Pump();
            var landed = window.Position;
            Assert.NotEqual(start, landed);
            // As Windows does: minimized, the window is parked off every screen, and comes back where it was.
            window.WindowState = WindowState.Minimized;
            window.Position = new PixelPoint(-32000, -32000);
            RenderTests.Pump();
            Assert.False(page.Preview!.IsVisible);
            Assert.NotNull(main.RestPlace);
            window.Position = landed;
            window.WindowState = WindowState.Normal;
            RenderTests.Pump();
            vm.DoneAdjustingLookCommand.Execute(null);
            RenderTests.Pump();
            Assert.Equal(start, window.Position);
            Assert.Null(main.RestPlace);
        }
        finally
        {
            Motion.Set(motion);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_window_too_wide_for_the_donut_beside_it_is_narrowed_and_gets_its_width_back_on_done()
    {
        // 1800 of the test screen's 1920 pixels: no room beside it even moved over, so it is narrowed too.
        var (window, page, vm) = Open(1800);
        var motion = Motion.Enabled;
        Motion.Set(false);
        var main = (MainWindow)window;
        var (start, width) = (window.Position, window.ClientSize.Width);
        try
        {
            vm.AdjustLookCommand.Execute(null);
            RenderTests.Pump();
            Assert.True(window.ClientSize.Width < width - 1, $"{window.ClientSize.Width} not narrowed from {width}");
            AssertNotUnder(page.Preview!, window);
            Assert.False(page.Preview!.Place!.Over);
            // Closed now, it opens next time as wide as the user had it.
            var closing = WindowService.MainWindowPlacement.Of(main, (MainWindowViewModel)window.DataContext!, null);
            Assert.Equal(width, closing.Size!.Value.Width);
            // Maximized and restored meanwhile, as Windows reports it: the new size first, then the new state. Not the
            // user's size: Done still gives it its width back.
            var narrowed = window.ClientSize.Width;
            void SizeFirst(double to)
            {
                window.Width = to;
                window.UpdateLayout();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs(Avalonia.Threading.DispatcherPriority.Send); // the size lands
            }
            SizeFirst(1920);
            Assert.Equal(1920, window.ClientSize.Width);
            window.WindowState = WindowState.Maximized;
            RenderTests.Pump();
            SizeFirst(narrowed);
            window.WindowState = WindowState.Normal;
            RenderTests.Pump();
            closing = WindowService.MainWindowPlacement.Of(main, (MainWindowViewModel)window.DataContext!, null);
            Assert.Equal(width, closing.Size!.Value.Width);
            vm.DoneAdjustingLookCommand.Execute(null);
            RenderTests.Pump();
            Assert.Equal((start, width), (window.Position, window.ClientSize.Width));

            // Narrowed to a width a device pixel short (Windows truncates to whole pixels: up to 0.8 short at 125 %,
            // one here at 100 %), and placed where an X11 window manager reports it only later (its landing read as
            // where it was): still its own width and place back on Done, and on close meanwhile.
            vm.AdjustLookCommand.Execute(null);
            RenderTests.Pump();
            var asked = window.ClientSize.Width;
            window.Width = asked - 1;
            RenderTests.Pump();
            Assert.Equal(asked - 1, window.ClientSize.Width);
            RenderTests.Pump();
            main.Landed = start;
            closing = WindowService.MainWindowPlacement.Of(main, (MainWindowViewModel)window.DataContext!, null);
            Assert.Equal((start, width), (closing.Position!.Value, closing.Size!.Value.Width));
            vm.DoneAdjustingLookCommand.Execute(null);
            RenderTests.Pump();
            Assert.Equal((start, width), (window.Position, window.ClientSize.Width));

            // Moved by the user while the donut is put away (a profile search hides it), still narrowed: on Done it
            // goes back where they moved it, as wide as it was.
            vm.AdjustLookCommand.Execute(null);
            RenderTests.Pump();
            Assert.True(window.ClientSize.Width < width - 1);
            vm.ProfileSearch = "zzzz-no-profile";
            RenderTests.Pump();
            var theirs = new PixelPoint(start.X - 30, start.Y - 30);
            window.Position = theirs;
            RenderTests.Pump();
            vm.ProfileSearch = "";
            vm.SelectedProfile = vm.Profiles[0];
            RenderTests.Pump();
            closing = WindowService.MainWindowPlacement.Of(main, (MainWindowViewModel)window.DataContext!, null);
            Assert.Equal((theirs, width), (closing.Position!.Value, closing.Size!.Value.Width));
            vm.DoneAdjustingLookCommand.Execute(null);
            RenderTests.Pump();
            Assert.Equal((theirs, width), (window.Position, window.ClientSize.Width));
        }
        finally
        {
            Motion.Set(motion);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Another_page_while_maximized_leaves_no_old_place_behind()
    {
        var (window, page, vm) = Open();
        var motion = Motion.Enabled;
        Motion.Set(false);
        var main = (MainWindow)window;
        var start = window.Position;
        try
        {
            vm.AdjustLookCommand.Execute(null);
            RenderTests.Pump();
            Assert.NotEqual(start, window.Position);
            window.WindowState = WindowState.Maximized;
            RenderTests.Pump();
            ((MainWindowViewModel)window.DataContext!).Navigate("general");
            RenderTests.Pump();
            // Nothing is left to put it back: where it is when it is a normal window again is where closing remembers.
            Assert.Null(main.RestPlace);
            window.WindowState = WindowState.Normal;
            window.Position = new PixelPoint(60, 80);
            RenderTests.Pump();
            var closing = WindowService.MainWindowPlacement.Of(main, (MainWindowViewModel)window.DataContext!, null);
            Assert.Equal(new PixelPoint(60, 80), closing.Position);
        }
        finally
        {
            Motion.Set(motion);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_place_the_user_moves_the_window_to_meanwhile_is_kept_and_remembered_on_close()
    {
        var (window, page, vm) = Open();
        var motion = Motion.Enabled;
        Motion.Set(false);
        var main = (MainWindow)window;
        var start = window.Position;
        try
        {
            vm.AdjustLookCommand.Execute(null);
            RenderTests.Pump();
            Assert.NotEqual(start, window.Position);
            foreach (var to in new[] { new PixelPoint(800, 100), new PixelPoint(700, 120) })
            {
                window.Position = to;
                RenderTests.Pump();
                // Closed now, it opens next time where the user moved it.
                Assert.Equal(to, WindowService.MainWindowPlacement.Of(main, (MainWindowViewModel)window.DataContext!, null).Position);
            }
            vm.DoneAdjustingLookCommand.Execute(null);
            RenderTests.Pump();
            Assert.Equal(new PixelPoint(700, 120), window.Position);
            Assert.Null(main.RestPlace);
        }
        finally
        {
            Motion.Set(motion);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Moving_the_profile_in_the_list_leaves_the_donut_on_screen_and_the_window_where_they_are()
    {
        var (window, page, vm) = Open();
        var store = AppHost.Get<UserDataStore>();
        var order = store.Profiles.ToList();
        var motion = Motion.Enabled;
        Motion.Set(true); // animated, as in the app: a slide back would be under way
        var main = (MainWindow)window;
        var start = window.Position;
        try
        {
            vm.SelectedProfile = vm.Profiles[0];
            Assert.True(vm.MoveProfileDownCommand.CanExecute(null));
            var selected = vm.SelectedProfile;
            vm.AdjustLookCommand.Execute(null);
            RunUntil(() => window.Position != start && !IsSliding(page));
            var landed = window.Position;
            Assert.NotEqual(start, landed);
            var preview = page.Preview!;

            // Down and back up (the list drops its selection for a moment on the way back).
            var hidden = 0;
            preview.PropertyChanged += (_, e) =>
            {
                if (e.Property == Visual.IsVisibleProperty && !preview.IsVisible)
                    hidden++;
            };
            foreach (var move in new[] { vm.MoveProfileDownCommand, vm.MoveProfileUpCommand })
            {
                move.Execute(null);
                Assert.Equal(0, hidden); // not put away for a moment either
                RunUntil(() => false, TimeSpan.FromMilliseconds(500));
                Assert.Same(selected, vm.SelectedProfile);
                Assert.False(IsSliding(page));
                Assert.Equal(landed, window.Position);
                Assert.Same(preview, page.Preview);
                Assert.True(preview.IsVisible);
                AssertNotUnder(preview, window);
                Assert.False(preview.Place!.Over);
            }
        }
        finally
        {
            Motion.Set(motion);
            store.Profiles.Clear();
            store.Profiles.AddRange(order);
            store.SaveLibrary();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_donut_on_screen_follows_mixed_selection_and_the_name_of_a_donut_for_any_file()
    {
        var (window, page, vm) = Open();
        var store = AppHost.Get<UserDataStore>();
        var order = store.Profiles.ToList();
        var images = store.Profiles.Single(p => p.Id == "images");
        var anything = store.Profiles.First(p => RadialViewModel.PreviewFormats(p).Count == 0);
        var name = anything.Name;
        try
        {
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile == images);
            vm.AdjustLookCommand.Execute(null);
            RenderTests.Pump();
            var donut = page.Preview!.DonutControl;
            var before = donut.CenterTitle;
            // Mixed selection on: judged for two different files, as the donut opens for a mixed profile.
            vm.IsMixed = true;
            RenderTests.Pump();
            Assert.True(images.IsFallback);
            Assert.NotEqual(before, page.Preview!.DonutControl.CenterTitle);
            Assert.Equal(AppHost.Get<ILocalizer>().Format("donut.files_mixed", 2), page.Preview.DonutControl.CenterTitle);
            vm.IsMixed = false;
            RenderTests.Pump();
            Assert.Equal(before, page.Preview!.DonutControl.CenterTitle);

            // A donut for any file has its name in the centre: renamed, the centre follows.
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile == anything);
            RenderTests.Pump();
            vm.ProfileName = "Anything at all";
            RenderTests.Pump();
            Assert.Equal("Anything at all", page.Preview!.DonutControl.CenterTitle);
        }
        finally
        {
            anything.Name = name;
            images.IsFallback = false;
            store.Profiles.Clear();
            store.Profiles.AddRange(order);
            store.SaveLibrary();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_profile_search_that_hides_the_donut_edited_leaves_the_window_where_it_made_room()
    {
        var (window, page, vm) = Open();
        var motion = Motion.Enabled;
        Motion.Set(false);
        var start = window.Position;
        try
        {
            var images = vm.Profiles.Single(p => p.Profile.Id == "images");
            vm.SelectedProfile = images;
            vm.AdjustLookCommand.Execute(null);
            RenderTests.Pump();
            var landed = window.Position;
            Assert.NotEqual(start, landed);
            vm.ProfileSearch = "pdf";
            RenderTests.Pump();
            Assert.Null(vm.SelectedProfile);
            Assert.True(vm.IsAdjustingLook);
            Assert.False(page.Preview?.IsVisible ?? false);
            Assert.Equal(landed, window.Position); // not back and over again
            vm.ProfileSearch = "";
            vm.SelectedProfile = images;
            RenderTests.Pump();
            Assert.True(page.Preview!.IsVisible);
            Assert.Equal(landed, window.Position);
            vm.DoneAdjustingLookCommand.Execute(null);
            RenderTests.Pump();
            Assert.Equal(start, window.Position);

            // Moved by the user while the donut is put away, to where it must make room again: on Done it goes back
            // there, and closing meanwhile remembers there.
            vm.AdjustLookCommand.Execute(null);
            RenderTests.Pump();
            vm.ProfileSearch = "pdf";
            RenderTests.Pump();
            var theirs = new PixelPoint(start.X - 20, start.Y - 40);
            window.Position = theirs;
            RenderTests.Pump();
            vm.ProfileSearch = "";
            vm.SelectedProfile = images;
            RenderTests.Pump();
            Assert.NotEqual(theirs, window.Position); // made room again
            Assert.Equal(theirs, WindowService.MainWindowPlacement.Of((MainWindow)window, (MainWindowViewModel)window.DataContext!, null).Position);
            vm.DoneAdjustingLookCommand.Execute(null);
            RenderTests.Pump();
            Assert.Equal(theirs, window.Position);
        }
        finally
        {
            vm.ProfileSearch = "";
            Motion.Set(motion);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Closed_on_its_way_back_the_window_opens_where_the_user_had_it()
    {
        var (window, page, vm) = Open();
        var motion = Motion.Enabled;
        Motion.Set(true); // animated, as in the app
        var main = (MainWindow)window;
        var start = window.Position;
        try
        {
            vm.AdjustLookCommand.Execute(null);
            RunUntil(() => window.Position != start && !IsSliding(page));
            var landed = window.Position;
            Assert.NotEqual(start, landed);
            ToolbarPage.SlideTime = TimeSpan.FromSeconds(3); // to be caught on its way back
            vm.DoneAdjustingLookCommand.Execute(null);
            RunUntil(() => window.Position != landed);
            Assert.True(IsSliding(page));
            Assert.NotEqual(start, window.Position);
            Assert.Equal(start, WindowService.MainWindowPlacement.Of(main, (MainWindowViewModel)window.DataContext!, null).Position);

            // Edit Donut Size again on its way back, before it has gone anywhere (Done and Edit Donut Size at once): it
            // stops there, the donut beside it, never over it, and Done still takes it back to where the user had it.
            ToolbarPage.SlideTime = TimeSpan.FromSeconds(0.25);
            RunUntil(() => main.RestPlace is null, TimeSpan.FromSeconds(6));
            vm.AdjustLookCommand.Execute(null);
            RunUntil(() => window.Position != start && !IsSliding(page));
            landed = window.Position;
            ToolbarPage.SlideTime = TimeSpan.FromSeconds(3);
            vm.DoneAdjustingLookCommand.Execute(null);
            vm.AdjustLookCommand.Execute(null);
            Assert.False(IsSliding(page));
            RunUntil(() => false, TimeSpan.FromSeconds(3.5)); // as long as the slide back would have gone on
            Assert.Equal(landed, window.Position);
            var preview = page.Preview!;
            Assert.True(preview.IsVisible);
            Assert.False(preview.Place!.Over);
            AssertNotUnder(preview, window);
            ToolbarPage.SlideTime = TimeSpan.FromSeconds(0.25);
            vm.DoneAdjustingLookCommand.Execute(null);
            RunUntil(() => main.RestPlace is null);
            Assert.Equal(start, window.Position);
        }
        finally
        {
            ToolbarPage.SlideTime = TimeSpan.FromSeconds(0.25);
            Motion.Set(motion);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_size_the_user_gives_the_window_meanwhile_is_kept_on_done()
    {
        var (window, page, vm) = Open();
        var motion = Motion.Enabled;
        Motion.Set(false);
        var main = (MainWindow)window;
        var start = window.Position;
        try
        {
            vm.AdjustLookCommand.Execute(null);
            RenderTests.Pump();
            Assert.NotEqual(start, window.Position);
            window.Width = 1100;
            window.Height = 760;
            RenderTests.Pump();
            // Closed now, it opens next time where the user had it, at the size they gave it.
            var closing = WindowService.MainWindowPlacement.Of(main, (MainWindowViewModel)window.DataContext!, null);
            Assert.Equal((start, new Size(1100, 760)), (closing.Position, closing.Size!.Value));
            vm.DoneAdjustingLookCommand.Execute(null);
            RenderTests.Pump();
            Assert.Equal(start, window.Position);
            Assert.Equal(1100, window.ClientSize.Width);
        }
        finally
        {
            Motion.Set(motion);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_donut_on_screen_dragged_onto_the_window_moves_off_it_and_a_move_not_dragged_is_not_kept()
    {
        var (window, page, vm) = Open();
        var store = AppHost.Get<UserDataStore>();
        var motion = Motion.Enabled;
        Motion.Set(false);
        // The system's move returns at once (headless, it doesn't move the window): the drag is over once the button is
        // seen up, also by a leave, as on macOS and X11.
        var leave = DonutPreviewWindow.LeaveTellsTheButton;
        DonutPreviewWindow.LeaveTellsTheButton = true;
        try
        {
            vm.AdjustLookCommand.Execute(null);
            RenderTests.Pump();
            var preview = page.Preview!;
            var placed = preview.Place;

            // Moved without its middle pressed (a window manager, a DPI change): not the user's, not kept.
            preview.Position = new PixelPoint(preview.Position.X, preview.Position.Y + 40);
            RenderTests.Pump();
            Assert.Null(store.Settings.DonutPreviewPlace);
            Assert.Same(placed, preview.Place);

            // Pressed by its middle and held over the window, however long: still the user's drag, left where it is.
            var middle = new Point(preview.Bounds.Width / 2, preview.Bounds.Height / 2);
            preview.MouseDown(middle, MouseButton.Left);
            var frame = FrameOf(window);
            preview.Position = new PixelPoint(frame.X + 100, frame.Y + 100);
            preview.MouseMove(middle, RawInputModifiers.LeftMouseButton); // the button still down
            RunUntil(() => false, TimeSpan.FromSeconds(1));
            Assert.True(IsDragging(preview));
            // A leave Avalonia raises on its own after a repaint (the window moved out from under where it last saw the
            // pointer) carries no button state: the drag goes on.
            preview.RaiseEvent(new PointerEventArgs(InputElement.PointerExitedEvent, preview,
                new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true), preview, new Point(-5, -5), 0,
                PointerPointProperties.None, KeyModifiers.None));
            Assert.True(IsDragging(preview));
            Assert.True(preview.Box.Intersects(FrameOf(window)));
            preview.Position = new PixelPoint(frame.X + 140, frame.Y + 120);
            Assert.NotNull(store.Settings.DonutPreviewPlace);
            // Resized while it is dragged (moved onto a screen of other scaling, or a slider): it stays under the
            // pointer.
            var held = preview.Position;
            vm.OuterRadius += 10;
            RenderTests.Pump();
            Assert.True(IsDragging(preview));
            Assert.Equal(held, preview.Position);
            vm.OuterRadius -= 10;
            RenderTests.Pump();
            // Let go there: off it at once, and the place isn't kept.
            preview.MouseUp(middle, MouseButton.Left);
            Assert.False(IsDragging(preview));
            Assert.Null(store.Settings.DonutPreviewPlace);
            AssertNotUnder(preview, window);

            // Dragged clear of it, pausing on the way: kept where it was let go.
            var back = preview.Box;
            var clear = window.Screens.All.Any(s => s.WorkingArea.Contains(back.Translate(new PixelVector(0, 30))))
                ? back.Position + new PixelVector(0, 30) : back.Position - new PixelVector(0, 30);
            Assert.False(new PixelRect(clear, back.Size).Intersects(FrameOf(window)));
            preview.MouseDown(middle, MouseButton.Left);
            preview.Position = clear - new PixelVector(0, 10);
            RunUntil(() => false, TimeSpan.FromSeconds(1));
            preview.Position = clear;
            var kept = store.Settings.DonutPreviewPlace!;
            Assert.Equal(new PixelPoint(preview.Box.Center.X - window.Position.X, preview.Box.Center.Y - window.Position.Y),
                new PixelPoint(kept.X!.Value, kept.Y!.Value));
            // The release lost in a window manager's move: the pointer seen with the button up ends the drag all the
            // same, and a move after that isn't kept.
            preview.MouseMove(middle, RawInputModifiers.None);
            Assert.False(IsDragging(preview));
            preview.Position = clear + new PixelVector(0, 20);
            RenderTests.Pump();
            Assert.Same(kept, store.Settings.DonutPreviewPlace);
            Assert.Equal(clear, preview.Position - new PixelVector(0, 20));

            // Let go partly off the screen: placed again, all of it on a screen.
            var area = window.Screens.All[0].WorkingArea;
            preview.MouseDown(middle, MouseButton.Left);
            preview.Position = new PixelPoint(area.Right - preview.Box.Width / 2, area.Y + 40);
            preview.MouseUp(middle, MouseButton.Left);
            AssertNotUnder(preview, window);

            // Dropped onto the window with the release lost, and the pointer only seen leaving the donut, the button
            // up: the drag is over all the same, and it moves off the window.
            preview.MouseDown(middle, MouseButton.Left);
            preview.Position = new PixelPoint(frame.X + 100, frame.Y + 100);
            Assert.True(IsDragging(preview));
            var mouse = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            preview.RaiseEvent(new PointerEventArgs(InputElement.PointerExitedEvent, preview, mouse, preview, new Point(-5, -5), 0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other), KeyModifiers.None));
            Assert.False(IsDragging(preview));
            Assert.Null(store.Settings.DonutPreviewPlace);
            AssertNotUnder(preview, window);
        }
        finally
        {
            Motion.Set(motion);
            store.Settings.DonutPreviewPlace = null;
            DonutPreviewWindow.LeaveTellsTheButton = leave;
            DonutLookSave.Flush();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void On_windows_the_drag_goes_on_after_the_press_until_the_button_up_that_ends_the_systems_move()
    {
        var (window, page, vm) = Open();
        var store = AppHost.Get<UserDataStore>();
        var motion = Motion.Enabled;
        Motion.Set(false);
        var (drag, leave) = (DonutPreviewWindow.MoveDrag, DonutPreviewWindow.LeaveTellsTheButton);
        DonutPreviewWindow.LeaveTellsTheButton = false; // as on Windows
        try
        {
            vm.AdjustLookCommand.Execute(null);
            RenderTests.Pump();
            var preview = page.Preview!;
            var frame = FrameOf(window);
            var middle = new Point(preview.Bounds.Width / 2, preview.Bounds.Height / 2);
            // As Avalonia does on Windows (Win32 WindowImpl.BeginMoveDrag): the capture is given up, the system's move
            // is posted and the call returns at once; the move ends with a button-up sent to the window.
            var over = false;
            void Posted(Window moved, Action loop) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Assert.True(IsDragging((DonutPreviewWindow)moved), "the press ended it"); // returned, still the drag
                loop();
                ((DonutPreviewWindow)moved).MouseUp(default, MouseButton.Left);
                over = true;
            }, Avalonia.Threading.DispatcherPriority.Send);

            // Moved over the window, paused there, let go there.
            DonutPreviewWindow.MoveDrag = (moved, e) =>
            {
                e.Pointer.Capture(null);
                Posted(moved, () =>
                {
                    moved.Position = new PixelPoint(frame.X + 100, frame.Y + 100);
                    // A leave Windows reports during its move: built with the keyboard's modifiers only, it says the
                    // button is up when it isn't. The drag goes on.
                    moved.RaiseEvent(new PointerEventArgs(InputElement.PointerExitedEvent, moved,
                        new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true), moved, new Point(-1, -1), 0,
                        new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other), KeyModifiers.None));
                    Assert.True(IsDragging((DonutPreviewWindow)moved), "a leave during the move ended it");
                    RunUntil(() => false, TimeSpan.FromSeconds(1));
                    Assert.True(((DonutPreviewWindow)moved).Box.Intersects(FrameOf(window)), "moved off the window while still dragged");
                    moved.Position = new PixelPoint(frame.X + 150, frame.Y + 120);
                });
            };
            preview.MouseDown(middle, MouseButton.Left);
            RunUntil(() => over);
            Assert.True(over);
            // Off the window at once, and not remembered there.
            Assert.False(IsDragging(preview));
            Assert.Null(store.Settings.DonutPreviewPlace);
            AssertNotUnder(preview, window);

            // Let go clear of it: remembered there, as it was when let go; a move after that isn't.
            var back = preview.Box;
            var clear = window.Screens.All.Any(s => s.WorkingArea.Contains(back.Translate(new PixelVector(0, 30))))
                ? back.Position + new PixelVector(0, 30) : back.Position - new PixelVector(0, 30);
            over = false;
            DonutPreviewWindow.MoveDrag = (moved, e) =>
            {
                e.Pointer.Capture(null);
                Posted(moved, () =>
                {
                    moved.Position = clear - new PixelVector(0, 10);
                    RunUntil(() => false, TimeSpan.FromSeconds(1));
                    moved.Position = clear;
                });
            };
            preview.MouseDown(middle, MouseButton.Left);
            RunUntil(() => over);
            var kept = store.Settings.DonutPreviewPlace!;
            Assert.Equal(new PixelPoint(preview.Box.Center.X - window.Position.X, preview.Box.Center.Y - window.Position.Y),
                new PixelPoint(kept.X!.Value, kept.Y!.Value));
            preview.Position = clear + new PixelVector(0, 20);
            RenderTests.Pump();
            Assert.Same(kept, store.Settings.DonutPreviewPlace);
        }
        finally
        {
            (DonutPreviewWindow.MoveDrag, DonutPreviewWindow.LeaveTellsTheButton) = (drag, leave);
            Motion.Set(motion);
            store.Settings.DonutPreviewPlace = null;
            DonutLookSave.Flush();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_preset_dragged_onto_the_smaller_donut_goes_into_the_slot_under_the_pointer()
    {
        var (window, page, vm) = Open(1080);
        var profile = vm.Profiles.Single(p => p.Profile.Id == "images").Profile;
        var front = profile.PresetIds.ToList();
        try
        {
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile.Id == "images");
            vm.EditingBack = false;
            RenderTests.Pump();
            var donut = page.FindControl<DonutMenu>("Donut")!;
            var drawn = donut.TranslatePoint(new Point(donut.Bounds.Width, 0), window)!.Value.X - donut.TranslatePoint(default, window)!.Value.X;
            Assert.True(drawn < donut.Bounds.Width - 1, $"drawn {drawn} wide, laid out {donut.Bounds.Width}: not smaller");
            var chip = window.GetVisualDescendants().OfType<Border>()
                .First(b => b.DataContext is PaletteChip { CanAdd: true } c && !front.Contains(c.PresetId) && b.IsEffectivelyVisible);
            var id = ((PaletteChip)chip.DataContext!).PresetId;
            var count = vm.SliceCount;
            Assert.True(count < ToolbarProfile.MaxSlices);
            // Into every slot, near the ring's inner and outer edges: where the pointer is, not where it would be on
            // the donut at full size.
            foreach (var edge in new[] { 0.15, 0.85 })
                for (var slot = 0; slot <= count; slot++)
                {
                    var center = donut.Bounds.Width / 2;
                    var mid = DonutGeometry.MidAngle(slot, count + 1);
                    var inner = donut.OuterRadius * donut.HoleRatio;
                    var r = inner + (donut.OuterRadius - inner) * edge;
                    var to = donut.TranslatePoint(new Point(center + r * Math.Sin(mid), center - r * Math.Cos(mid)), window)!.Value;
                    chip = window.GetVisualDescendants().OfType<Border>()
                        .Single(b => b.DataContext is PaletteChip c && c.PresetId == id && b.IsEffectivelyVisible);
                    var from = chip.TranslatePoint(new Point(chip.Bounds.Width / 2, chip.Bounds.Height / 2), window)!.Value;
                    window.MouseMove(from);
                    window.MouseDown(from, MouseButton.Left);
                    for (var i = 1; i <= 10; i++)
                        window.MouseMove(from + (to - from) * (i / 10.0));
                    window.MouseUp(to, MouseButton.Left);
                    RenderTests.Pump();
                    Assert.Equal((slot, edge, id), (slot, edge, profile.PresetIds.ElementAtOrDefault(slot)));
                    Assert.Equal(count + 1, profile.PresetIds.Count);
                    vm.RemoveAt(slot);
                    RenderTests.Pump();
                }
        }
        finally
        {
            profile.PresetIds.Clear();
            profile.PresetIds.AddRange(front);
            AppHost.Get<UserDataStore>().SaveLibrary();
            window.Close();
        }
    }
}
