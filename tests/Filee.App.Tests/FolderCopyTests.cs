using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.App.Views;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Core.Web;

namespace Filee.App.Tests;

[Collection("Motion")]
public class FolderCopyTests
{
    private static Preset Copy(string name, string folder) => new()
    {
        Name = name,
        Kind = PresetKind.CopyToFolder,
        TargetFormat = Preset.CopyTarget,
        Output = new OutputRule { Location = OutputLocation.CustomFolder, CustomFolder = folder }
    };

    [AvaloniaFact]
    public async Task Cancelling_ask_creates_no_job_and_releases_the_browser_drop()
    {
        TestServices.EnsureInitialized("en");
        var service = AppHost.Get<ConversionService>();
        var count = service.Jobs.Count;
        var owner = new Owner();
        using var drop = new WebDrop([WebDropItem.FromStream("a.png", _ => throw new Exception("Must not download"), owner: owner)], WebDrop.DefaultWorkRoot);
        var preset = Copy("Ask", ""); preset.Output.AskEveryTime = true;
        var result = await service.AskThenStartAsync(drop.Placeholders, preset, drop, (_, one) =>
        {
            Assert.True(one);
            return Task.FromResult<Preset?>(null);
        });
        Assert.Null(result); Assert.Equal(count, service.Jobs.Count);
        Assert.True(owner.Disposed); Assert.False(Directory.Exists(drop.WorkFolder));
    }

    private sealed class Owner : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    [AvaloniaFact]
    public void Copy_is_usable_for_unknown_types_folders_and_every_profile()
    {
        TestServices.EnsureInitialized("en");
        var availability = AppHost.Get<PresetAvailability>();
        var copy = Copy("Projects", "C:\\Projects");
        Assert.Null(availability.Check(copy, []));
        foreach (var format in FormatRegistry.Known)
            Assert.Null(availability.Check(copy, [format]));
        Assert.True(copy.IsTool);
        Assert.False(copy.IsCropTool);
        Assert.DoesNotContain(BuiltInData.CreatePresets(), p => p.IsCopyToFolder);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("zh-CN")]
    public void Editor_reuses_destination_choices_and_only_allows_numbered_conflicts(string language)
    {
        TestServices.EnsureInitialized(language);
        var loc = AppHost.Get<ILocalizer>();
        var preset = Copy(language == "en" ? "Project icons" : "项目图标", "C:\\Projects\\Icons");
        var editor = new PresetEditorViewModel(preset, loc);
        Assert.Equal(2, editor.Locations.Count);
        Assert.Equal(ConflictPolicy.Rename, Assert.Single(editor.Conflicts).Value);
        Assert.False(editor.CanChangeTarget); Assert.False(editor.ShowImage); Assert.False(editor.ShowConversionOutput);
        var window = new PresetEditorWindow { DataContext = editor };
        try
        {
            window.Show(); RenderTests.Pump();
            RenderTests.Save(window, $"copy-folder-editor-{language}.png");
            editor.Location = editor.Locations.Single(l => l.Value is null);
            Assert.True(editor.Apply()); Assert.True(preset.Output.AskEveryTime);
            Assert.Equal(PresetKind.CopyToFolder, preset.Kind);
            editor.Location = editor.Locations.Single(l => l.Value == OutputLocation.CustomFolder);
            editor.CustomFolder = "";
            Assert.False(editor.Apply()); Assert.NotNull(editor.DestinationError);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("zh-CN")]
    public void Folder_presets_fan_out_on_the_back_face_and_reject_front_placement(string language)
    {
        TestServices.EnsureInitialized(language);
        var store = AppHost.Get<UserDataStore>();
        var loc = AppHost.Get<ILocalizer>();
        var copies = new[] { Copy("Projects", "C:\\Projects"), Copy("References", "C:\\References"), Copy("Icons", "C:\\Icons") };
        var group = new Preset { Kind = PresetKind.Group, TargetFormat = Preset.GroupTarget, Name = "Folders", SubPresetIds = copies.Select(p => p.Id).ToList() };
        var profile = store.Profiles.Single(p => p.Id == "images");
        var before = profile.BackPresetIds;
        var motion = Motion.Enabled;
        Window? window = null;
        try
        {
            foreach (var p in copies.Append(group)) store.Presets.Add(p);
            profile.BackPresetIds = ["tool-crop", group.Id, "tool-image-edit"];
            var toolbar = new ToolbarPageViewModel(store, loc, AppHost.Get<WindowService>(), AppHost.Get<PresetAvailability>(), AppHost.Get<Filee.Core.Conversion.ConverterCatalog>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<ToolbarPageViewModel>.Instance);
            toolbar.EditingBack = false;
            Assert.False(toolbar.Insert(copies[0].Id, 0));
            Assert.False(toolbar.Insert(group.Id, 0));
            var radial = new RadialViewModel(store, loc, AppHost.Get<PresetAvailability>());
            radial.Load(["picture.png"], clickMode: true, backFace: true);
            var groupIndex = radial.Items.ToList().FindIndex(i => i.Id == group.Id);
            Assert.True(groupIndex >= 0);
            Assert.Equal(3, radial.Items[groupIndex].Children!.Count);
            Assert.All(radial.Items[groupIndex].Children!, child => Assert.True(child.IsEnabled));
            Motion.Set(false);
            var donut = new DonutMenu { Items = radial.Items, ReserveSubRing = true, CenterTitle = "Folders", CenterSubtitle = "Copy to folder", OuterRadius = 160 };
            window = new Window { Width = 560, Height = 560, Content = donut };
            window.Show(); RenderTests.Pump();
            var center = new Point(donut.Bounds.Width / 2, donut.Bounds.Height / 2);
            var at = DonutGeometry.PointAt(center, donut.OuterRadius * 0.7, DonutGeometry.MidAngle(groupIndex, radial.Items.Count));
            donut.SetExternalHit(donut.HitTest(at), at); donut.ExpandPendingGroup();
            RenderTests.Pump();
            Assert.Equal(DonutHitKind.SubSegment, donut.HitTest(donut.SubSliceCenter(groupIndex, 0)).Kind);
            RenderTests.Save(window, $"copy-folder-group-{language}.png");
        }
        finally
        {
            window?.Close(); Motion.Set(motion); profile.BackPresetIds = before;
            foreach (var p in copies.Append(group)) store.Presets.Remove(p);
        }
    }

    [AvaloniaFact]
    public void Move_action_changes_the_hint_saves_with_the_preset_and_names_the_slice()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var loc = AppHost.Get<ILocalizer>();
        var preset = Copy("Projects", "C:\\Projects");
        var editor = new PresetEditorViewModel(preset, loc);
        Assert.False(editor.MoveInsteadOfCopy);
        Assert.Equal(loc["presets.copy_hint"], editor.CopyHint);
        var window = new PresetEditorWindow { DataContext = editor };
        try
        {
            window.Show(); RenderTests.Pump();
            // Chosen as the user does, in the Action box.
            Assert.Equal([loc["presets.copy_to_folder"], loc["presets.move_to_folder"]], editor.CopyActions.Select(c => c.Label));
            Assert.Equal(loc["presets.copy_to_folder"], editor.CopyAction.Label);
            editor.CopyAction = editor.CopyActions[1];
            RenderTests.Pump();
            Assert.True(editor.MoveInsteadOfCopy);
            var boxes = window.GetVisualDescendants().OfType<ComboBox>().ToList();
            Assert.True(boxes.Single(b => b.Name == "CopyActionBox").IsEffectivelyVisible);
            Assert.False(boxes.Single(b => b.Name == "TargetBox").IsEffectivelyVisible);
            Assert.Equal(loc["presets.move_hint"], editor.CopyHint);
            RenderTests.Save(window, "copy-folder-move-editor-en.png");
            Assert.True(editor.Apply());
            Assert.True(preset.Output.MoveInsteadOfCopy);
            Assert.True(new PresetEditorViewModel(preset, loc).MoveInsteadOfCopy);
        }
        finally { window.Close(); }

        // A conversion preset never saves it, even when its editor's value is set.
        var convert = new Preset { Name = "PNG", TargetFormat = "png" };
        var other = new PresetEditorViewModel(convert, loc) { MoveInsteadOfCopy = true };
        Assert.True(other.Apply());
        Assert.False(convert.Output.MoveInsteadOfCopy);

        var copy = Copy("References", "C:\\References");
        var group = new Preset { Kind = PresetKind.Group, TargetFormat = Preset.GroupTarget, Name = "Folders", SubPresetIds = [preset.Id, copy.Id] };
        var profile = store.Profiles.Single(p => p.Id == "images");
        var before = profile.BackPresetIds;
        var motion = Motion.Enabled;
        Window? donutWindow = null;
        try
        {
            foreach (var p in new[] { preset, copy, group }) store.Presets.Add(p);
            profile.BackPresetIds = [preset.Id, copy.Id];
            var radial = new RadialViewModel(store, loc, AppHost.Get<PresetAvailability>());
            radial.Load(["picture.png"], clickMode: true, backFace: true);
            Assert.Equal(loc["presets.move_to_folder"], radial.Items.Single(i => i.Id == preset.Id).Caption);
            Assert.Equal(loc["presets.copy_to_folder"], radial.Items.Single(i => i.Id == copy.Id).Caption);
            Motion.Set(false);
            var donut = new DonutMenu { Items = radial.Items, CenterTitle = "picture.png", OuterRadius = 160 };
            donutWindow = new Window { Width = 420, Height = 420, Content = donut };
            donutWindow.Show(); RenderTests.Pump();
            RenderTests.Save(donutWindow, "copy-folder-move-donut-en.png");
        }
        finally
        {
            donutWindow?.Close(); Motion.Set(motion);
            profile.BackPresetIds = before;
            foreach (var p in new[] { preset, copy, group }) store.Presets.Remove(p);
        }
    }
}
