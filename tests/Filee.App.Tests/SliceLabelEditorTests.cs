// The slice-label fields of the preset and group editors: they show the preset's icon, short name and Show setting,
// save them, clear them, and the donut shows the result.

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class SliceLabelEditorTests
{
    private static ILocalizer Loc()
    {
        TestServices.EnsureInitialized("en");
        return AppHost.Get<ILocalizer>();
    }

    [AvaloniaFact]
    public void The_preset_editor_shows_saves_and_clears_the_slice_label_fields()
    {
        var loc = Loc();
        var preset = BuiltInData.CreatePresets().Single(p => p.Id == "remove-background");
        var vm = new PresetEditorViewModel(preset, loc);
        var labels = vm.Labels;
        Assert.Null(labels.Icon);
        Assert.Equal("Default", labels.IconText);
        Assert.NotNull(labels.IconData); // its default icon
        Assert.Equal("", labels.ShortName);
        Assert.Equal(loc.DonutName(preset), labels.ShortNamePlaceholder);
        Assert.Null(labels.Show.Value);
        Assert.Equal(["Use the donut's setting", "Auto", "Icon & text", "Text only", "Icon only"], labels.ShowChoices.Select(c => c.Label));

        // Picked in the picker, typed, chosen; saved.
        labels.IconPicker.PickCommand.Execute(labels.IconPicker.Sections[0].Icons[0]);
        labels.ShortName = "  No bg ";
        labels.Show = labels.ShowChoices.Single(c => c.Value == LabelShow.IconOnly);
        Assert.True(vm.Apply());
        Assert.Equal(labels.IconPicker.Sections[0].Icons[0].Key, preset.Icon);
        Assert.Equal("No bg", preset.ShortName);
        Assert.Equal(LabelShow.IconOnly, preset.Show);

        // Reopened: the saved values; then None, a blank short name and the donut's setting clear them.
        vm = new PresetEditorViewModel(preset, loc);
        Assert.Equal("No bg", vm.Labels.ShortName);
        Assert.Equal(LabelShow.IconOnly, vm.Labels.Show.Value);
        vm.Labels.IconPicker.PickNoneCommand.Execute(null);
        Assert.Equal("None", vm.Labels.IconText);
        Assert.Null(vm.Labels.IconData);
        vm.Labels.ShortName = " ";
        vm.Labels.Show = vm.Labels.ShowChoices[0];
        Assert.True(vm.Apply());
        Assert.Equal(Preset.NoIcon, preset.Icon);
        Assert.Null(preset.ShortName);
        Assert.Null(preset.Show);

        // Default again.
        vm = new PresetEditorViewModel(preset, loc);
        vm.Labels.IconPicker.PickDefaultCommand.Execute(null);
        Assert.True(vm.Apply());
        Assert.Null(preset.Icon);
    }

    [AvaloniaFact]
    public void The_short_name_placeholder_follows_the_name_being_typed()
    {
        var loc = Loc();
        var vm = new PresetEditorViewModel(new Preset { Name = "My PNG", TargetFormat = "png" }, loc);
        Assert.Equal("My PNG", vm.Labels.ShortNamePlaceholder);
        vm.Name = "Small PNG";
        Assert.Equal("Small PNG", vm.Labels.ShortNamePlaceholder);
    }

    [AvaloniaFact]
    public void The_group_editor_saves_the_slice_label_fields_and_the_donut_shows_them()
    {
        var loc = Loc();
        var store = AppHost.Get<UserDataStore>();
        var rotate = store.FindPreset("rotate")!;
        var before = (rotate.Icon, rotate.ShortName, rotate.Show, rotate.Direction);
        try
        {
            var vm = new GroupEditorViewModel(rotate, store, loc);
            Assert.Null(vm.Labels.Direction.Value);
            vm.Labels.Direction = vm.Labels.DirectionChoices.Single(c => c.Value == LabelDirection.AlongRing);
            Assert.Equal("Default", vm.Labels.IconText);
            vm.Labels.IconPicker.PickCommand.Execute(vm.Labels.IconPicker.Sections[0].Icons[0]);
            vm.Labels.ShortName = "Turn";
            vm.Labels.Show = vm.Labels.ShowChoices.Single(c => c.Value == LabelShow.TextOnly);
            Assert.True(vm.Apply());

            var radial = AppHost.Get<RadialViewModel>();
            radial.Load([@"C:\photos\a.jpg"], clickMode: true, backFace: true);
            var item = radial.Items.Single(i => i.Id == "rotate");
            Assert.Equal("Turn", item.Label);
            Assert.Equal("Rotate", item.FullName);
            Assert.Equal(rotate.Icon, item.IconKey);
            Assert.Equal(LabelShow.TextOnly, item.Show);
            Assert.Equal(LabelDirection.AlongRing, rotate.Direction);
            Assert.Equal(LabelDirection.AlongRing, item.Direction);
        }
        finally
        {
            (rotate.Icon, rotate.ShortName, rotate.Show, rotate.Direction) = before;
        }
    }

    [AvaloniaFact]
    public void Both_editors_show_the_fields()
    {
        var loc = Loc();
        var store = AppHost.Get<UserDataStore>();
        var preset = new PresetEditorView { DataContext = new PresetEditorViewModel(BuiltInData.CreatePresets()[0], loc) };
        var group = new GroupEditorWindow { DataContext = new GroupEditorViewModel(store.FindPreset("rotate")!.Clone(), store, loc) };
        var window = new Window { Content = preset };
        window.Show();
        group.Show();
        foreach (var root in new Control[] { window, group })
        {
            var fields = root.GetVisualDescendants().OfType<SliceLabelFieldsView>().Single();
            Assert.IsType<SliceLabelFields>(fields.DataContext);
            Assert.Equal("Default", fields.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Default").Text);
        }
        window.Close();
        group.Close();
    }
}
