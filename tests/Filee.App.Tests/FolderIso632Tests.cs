// Real folder input selects the ISO donut, and the editor keeps archive compression controls hidden.

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.App.Views;
using Filee.App.Views.Pages;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.App.Tests;

public class FolderIso632Tests
{
    private static readonly string[] RefusalKeys = ["error.iso_root", "error.iso_link", "error.iso_name", "error.iso_file_size", "error.iso_changed"];

    [AvaloniaFact]
    public void Every_shipped_language_has_iso_refusal_messages()
    {
        TestServices.EnsureInitialized("en");
        var english = LocalizationService.LoadStrings("en");
        foreach (var language in LocalizationService.Languages)
        {
            var catalog = LocalizationService.LoadStrings(language.Code);
            foreach (var key in RefusalKeys)
            {
                Assert.True(catalog.TryGetValue(key, out var message), $"{language.Code}: {key}");
                Assert.False(string.IsNullOrWhiteSpace(message));
                if (language.Code != "en") Assert.NotEqual(english[key], message);
            }
        }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    [InlineData("zh-CN")]
    public void Iso_refusals_show_the_selected_language_with_separate_diagnostic_details(string language)
    {
        TestServices.EnsureInitialized(language);
        var loc = AppHost.Get<ILocalizer>();
        var catalog = LocalizationService.LoadStrings(language);
        foreach (var key in RefusalKeys)
        {
            var job = new ConversionJob(["C:/folder"], new Preset { TargetFormat = "iso" }, "ISO");
            var file = Assert.Single(job.Files);
            file.State = FileState.Failed;
            file.ErrorKey = key;
            file.ErrorDetail = "nested/setup.exe";
            var vm = new JobViewModel(job, loc, AppHost.Get<Filee.Core.Platform.IPlatformServices>(), _ => { });
            Assert.Contains(catalog[key], vm.ErrorDetails);
            Assert.Contains(file.ErrorDetail, vm.ErrorDetails);
            Assert.DoesNotContain(loc["error.conversion_failed"], vm.ErrorDetails);
        }
    }

    [AvaloniaTheory]
    [InlineData("documents", false)]
    [InlineData("documents", true)]
    [InlineData("photos.jpg", false)]
    [InlineData("photos.jpg", true)]
    public void A_deleted_folders_profile_falls_back_to_normal_profile_selection(string folderName, bool clickMode)
    {
        TestServices.EnsureInitialized("en");
        var root = Directory.CreateTempSubdirectory("filee-deleted-folder-profile-").FullName;
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, folderName)).FullName;
            var store = new UserDataStore(Path.Combine(root, "settings"));
            store.Load();
            store.Settings.TurnedOffFields.Clear();
            store.Profiles.Remove(store.Profiles.Single(p => p.Id == "folders"));
            store.Profiles.Single(p => p.Id == "mixed").PresetIds = ["to-iso", "zip-all"];
            store.Profiles.Single(p => p.Id == "images").PresetIds = ["to-jpg", "to-iso"];
            store.SaveLibrary();
            var reloaded = new UserDataStore(Path.Combine(root, "settings"));
            reloaded.Load();
            Assert.DoesNotContain(reloaded.Profiles, p => p.Id == "folders");
            var expected = Filee.Core.Profiles.ProfileSelector.Select(reloaded.Profiles, [folder]);
            Assert.NotNull(expected);
            var vm = new RadialViewModel(reloaded, AppHost.Get<ILocalizer>(), AppHost.Get<PresetAvailability>());

            vm.Load([folder], clickMode);

            Assert.NotEmpty(vm.Items);
            Assert.Equal(expected.PresetIds, vm.Items.Select(i => i.Id));
            Assert.True(Assert.Single(vm.Items, i => i.Id == "to-iso").IsEnabled);
            Assert.Equal(clickMode, vm.IsClickMode);
            Assert.Equal([folder], vm.Files);
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void Archives_offers_usable_iso_for_manual_placement_without_changing_the_default_donut()
    {
        TestServices.EnsureInitialized("en");
        var root = Directory.CreateTempSubdirectory("filee-archives-iso-").FullName;
        try
        {
            var store = new UserDataStore(root);
            store.Load();
            var archives = store.Profiles.Single(p => p.Id == "archives");
            var defaults = archives.PresetIds.ToArray();
            Assert.DoesNotContain("to-iso", defaults);
            Assert.DoesNotContain("to-iso", archives.BackPresetIds ?? []);
            using var vm = new ToolbarPageViewModel(store, AppHost.Get<ILocalizer>(), AppHost.Get<WindowService>(),
                AppHost.Get<PresetAvailability>(), AppHost.Get<ConverterCatalog>(), NullLogger<ToolbarPageViewModel>.Instance);
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile.Id == "archives");
            RenderTests.Pump();
            var chip = Assert.Single(vm.VisiblePalette, p => p.PresetId == "to-iso");
            Assert.True(chip.CanAdd);
            Assert.False(chip.IsDimmed);
            Assert.Equal(PresetUse.Usable, chip.Use);
            Assert.Null(chip.Reason);
            Assert.Equal(defaults, archives.PresetIds);
            vm.PaletteSearch = "ISO";
            vm.SelectedTab = vm.Tabs.Single(t => t.Id == PaletteTab.Archive);
            Assert.True(Assert.Single(vm.VisiblePalette, p => p.PresetId == "to-iso").CanAdd);
            Assert.True(vm.Insert("to-iso", vm.SliceCount));
            Assert.Contains("to-iso", archives.PresetIds);
            Assert.DoesNotContain(vm.VisiblePalette, p => p.PresetId == "to-iso");
            var saved = new UserDataStore(root);
            saved.Load();
            Assert.Contains("to-iso", saved.Profiles.Single(p => p.Id == "archives").PresetIds);
            var radial = new RadialViewModel(store, AppHost.Get<ILocalizer>(), AppHost.Get<PresetAvailability>());
            radial.Load([Path.Combine(root, "backup.zip")], clickMode: false);
            Assert.True(Assert.Single(radial.Items, item => item.Id == "to-iso").IsEnabled);
            vm.RemoveAt(archives.PresetIds.IndexOf("to-iso"));
            RenderTests.Pump();
            Assert.True(Assert.Single(vm.VisiblePalette, p => p.PresetId == "to-iso").CanAdd);
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile.Id == "images");
            vm.PaletteSearch = "ISO";
            RenderTests.Pump();
            Assert.False(Assert.Single(vm.VisiblePalette, p => p.PresetId == "to-iso").CanAdd);
            Assert.DoesNotContain("to-iso", BuiltInData.CreateProfiles().Single(p => p.Id == "archives").PresetIds);
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task Folder_toolbar_passes_folder_context_to_new_and_existing_preset_dialogs()
    {
        TestServices.EnsureInitialized("en");
        var root = Directory.CreateTempSubdirectory("filee-folder-editor-").FullName;
        var windows = AppHost.Get<WindowService>();
        try
        {
            var store = new UserDataStore(root);
            store.Load();
            using var vm = new ToolbarPageViewModel(store, AppHost.Get<ILocalizer>(), windows,
                AppHost.Get<PresetAvailability>(), AppHost.Get<ConverterCatalog>(), NullLogger<ToolbarPageViewModel>.Instance);
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile.Id == "folders");
            windows.ShowMain();
            foreach (var open in new Func<Task>[] { () => vm.NewPresetCommand.ExecuteAsync(null), () => vm.EditChipAsync("to-iso"), () => vm.EditAsync(0) })
            {
                var editing = open();
                Dispatcher.UIThread.RunJobs();
                var dialog = Assert.Single(windows.Main!.OwnedWindows.OfType<PresetEditorWindow>());
                var editor = Assert.IsType<PresetEditorViewModel>(dialog.DataContext);
                Assert.True(editor.FolderInput);
                Assert.Single(editor.Targets);
                dialog.Close();
                await editing.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            windows.Main?.Close();
            Dispatcher.UIThread.RunJobs();
            Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public void Folder_editor_only_offers_iso_without_compression_or_filesystem_options()
    {
        TestServices.EnsureInitialized("en");
        var loc = AppHost.Get<ILocalizer>();
        var preset = new Preset { Name = "Folder action", TargetFormat = "iso", Output = { ReplaceOriginal = true } };
        var editor = new PresetEditorViewModel(preset, loc, folderInput: true);
        Assert.Equal("iso", Assert.Single(editor.Targets).Value);
        Assert.False(editor.ShowReplaceOriginal);
        // Cancel has not changed the saved preset.
        Assert.Equal("iso", preset.TargetFormat);
        Assert.False(preset.Archive.CombineIntoOne);
        Assert.True(editor.Apply());
        Assert.False(preset.PacksIntoOneArchive);
        Assert.False(preset.Output.ReplaceOriginal);
        Assert.False(preset.Archive.CombineIntoOne);
        Assert.False(editor.ShowArchive);

        var normal = new PresetEditorViewModel(new Preset { TargetFormat = "png" }, loc);
        Assert.Contains(normal.Targets, t => t.Value == "w64");
        Assert.Contains(normal.Targets, t => t.Value == "gz");
        Assert.True(normal.ShowReplaceOriginal);
        var newFolder = new PresetEditorViewModel(new Preset { TargetFormat = "png" }, loc, folderInput: true);
        Assert.Equal("iso", newFolder.Target.Value);
        var availability = AppHost.Get<PresetAvailability>();
        foreach (var target in new[] { "zip", "7z", "tar", "tgz", "tbz2", "txz", "gz", "bz2", "xz", "w64", "eac3" })
        {
            var incompatible = new Preset { TargetFormat = target, Archive = { CombineIntoOne = true } };
            Assert.Equal(PresetUse.Unusable,
                availability.ForPalette([FormatRegistry.Get(FormatRegistry.Folder)]).Judge(incompatible).Use);
        }
    }

    [AvaloniaFact]
    public void Folder_group_editor_only_offers_folder_actions_without_removing_existing_members()
    {
        TestServices.EnsureInitialized("en");
        var root = Directory.CreateTempSubdirectory("filee-folder-group-").FullName;
        try
        {
            var store = new UserDataStore(root);
            store.Load();
            var group = new Preset { Name = "Mixed group", Kind = PresetKind.Group, SubPresetIds = ["to-png"] };
            var editor = new GroupEditorViewModel(group, store, AppHost.Get<ILocalizer>(), folderInput: true);
            Assert.Single(editor.Members);
            Assert.Contains(editor.Available, t => t.Value == "to-iso");
            Assert.DoesNotContain(editor.Available, t => t.Value == "zip-all");
            Assert.All(editor.Available, t => Assert.True(BuiltInData.AcceptsFolders(store.FindPreset(t.Value)!)));
            editor.ToAdd = editor.Available.Single(t => t.Value == "to-iso");
            editor.AddCommand.Execute(null);
            Assert.True(editor.Apply());
            Assert.Equal(new[] { "to-png", "to-iso" }, group.SubPresetIds);
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void Folder_palette_offers_only_compatible_presets_and_groups_even_when_searching_or_switching_tabs()
    {
        TestServices.EnsureInitialized("en");
        var root = Directory.CreateTempSubdirectory("filee-folder-palette-").FullName;
        var store = new UserDataStore(root);
        store.Load();
        var folders = store.Profiles.Single(p => p.Id == "folders");
        // Remove ISO so it should become available again. ZIP stays placed, but must still qualify its group.
        folders.PresetIds.Remove("to-iso");
        store.Presets.Add(new Preset
        {
            Id = "folder-group",
            Name = "Folder actions",
            Kind = PresetKind.Group,
            SubPresetIds = ["to-iso"]
        });
        store.Presets.Add(new Preset
        {
            Id = "image-group",
            Name = "Image actions",
            Kind = PresetKind.Group,
            SubPresetIds = ["to-png"]
        });
        using var vm = new ToolbarPageViewModel(store, AppHost.Get<ILocalizer>(), AppHost.Get<WindowService>(),
            AppHost.Get<PresetAvailability>(), AppHost.Get<ConverterCatalog>(), NullLogger<ToolbarPageViewModel>.Instance);
        var window = new Window { Content = new ToolbarPage { DataContext = vm }, Width = 1200, Height = 850 };
        try
        {
            window.Show();
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile.Id == "folders");
            RenderTests.Pump();
            Assert.Contains(vm.VisiblePalette, p => p.PresetId == "to-iso");
            Assert.Contains(vm.VisiblePalette, p => p.PresetId == "folder-group");
            Assert.DoesNotContain(vm.VisiblePalette, p => p.PresetId == "to-png" || p.PresetId == "image-group");
            Assert.DoesNotContain(vm.Tabs, t => t.Id == PaletteTab.Image || t.Id == PaletteTab.Document || t.Id == PaletteTab.Edit);
            RenderTests.Save(window, "folder-compatible-palette.png");

            vm.PaletteSearch = "PNG";
            RenderTests.Pump();
            Assert.Empty(vm.VisiblePalette);
            vm.PaletteSearch = "ISO";
            RenderTests.Pump();
            Assert.Contains(vm.VisiblePalette, p => p.PresetId == "to-iso");
            vm.SelectedTab = vm.Tabs.Last();
            Assert.DoesNotContain(vm.VisiblePalette, p => p.PresetId == "to-png");

            vm.PaletteSearch = "";
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile.Id == "images");
            RenderTests.Pump();
            Assert.Contains(vm.VisiblePalette, p => p.PresetId == "image-group");
        }
        finally { window.Close(); Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void Folder_input_opens_the_iso_donut_and_editor_shows_no_compression()
    {
        TestServices.EnsureInitialized("en");
        var root = Directory.CreateTempSubdirectory("filee-iso632-ui-").FullName;
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, "photos.jpg")).FullName;
            var store = new UserDataStore(Path.Combine(root, "settings"));
            store.Load();
            var vm = new RadialViewModel(store, AppHost.Get<ILocalizer>(), AppHost.Get<PresetAvailability>());
            vm.Load([folder], clickMode: false);
            Assert.Equal("Folders", AppHost.Get<ILocalizer>()[store.Profiles.Single(p => p.Id == "folders").NameKey!]);
            Assert.Single(vm.Items);
            var item = Assert.Single(vm.Items, i => i.Id == "to-iso");
            Assert.Equal("to-iso", item.Id);
            Assert.True(item.IsEnabled, item.DisabledReason);
            var preset = new Preset { TargetFormat = "iso", Archive = { CombineIntoOne = true } };
            var editor = new PresetEditorViewModel(preset, AppHost.Get<ILocalizer>());
            Assert.False(editor.ShowArchive);
            var window = new PresetEditorWindow { DataContext = editor, Height = 820 };
            try
            {
                window.Show();
                RenderTests.Pump();
                var view = window.GetVisualDescendants().OfType<PresetEditorView>().Single();
                Assert.Null(view.FindControl<Border>("IsoCard"));
                Assert.False(view.FindControl<Border>("ArchiveCard")!.IsVisible);
                var targets = view.FindControl<ComboBox>("TargetBox")!;
                Assert.Equal(1, targets.ItemCount);
                Assert.DoesNotContain(targets.Items.OfType<Choice<string>>(), t => t.Value is "w64" or "eac3" or "gz" or "bz2" or "xz");
                Assert.Null(view.FindControl<ComboBox>("IsoStandardBox"));
                RenderTests.Pump();
                RenderTests.Save(window, "folder-iso-632.png");
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(root, true); }
    }
}
