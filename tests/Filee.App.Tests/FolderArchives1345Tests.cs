using Avalonia.Headless.XUnit;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public sealed class FolderArchives1345Tests
{
    [AvaloniaFact]
    public void Folder_wheel_and_editor_offer_zip_and_one_zip()
    {
        TestServices.EnsureInitialized("zh-TW");
        var root = Directory.CreateTempSubdirectory("filee-folder-1345-ui-").FullName;
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, "photos.jpg")).FullName;
            var store = new UserDataStore(Path.Combine(root, "settings"));
            store.Load();
            var loc = AppHost.Get<ILocalizer>();
            var radial = new RadialViewModel(store, loc, AppHost.Get<PresetAvailability>());
            radial.Load([folder], clickMode: false);
            Assert.Equal(["to-iso", "to-zip", "zip-all"], radial.Items.Select(item => item.Id));
            var archiveAvailable = new Filee.Engines.Archives.ArchiveConverter().GetStatus().IsAvailable;
            foreach (var id in new[] { "to-zip", "zip-all" })
            {
                var preset = store.Presets.Single(p => p.Id == id);
                var item = radial.Items.Single(i => i.Id == id);
                Assert.Equal(id == "zip-all" || archiveAvailable, item.IsEnabled);
                Assert.Equal([folder], RadialController.SourcesFor(preset, [folder]));
                var editor = new PresetEditorViewModel(preset, loc, folderInput: true);
                Assert.Equal("zip", editor.Target.Value);
                Assert.True(editor.ShowArchive);
                Assert.False(editor.ShowReplaceOriginal);
                Assert.Equal(id == "zip-all", editor.CombineIntoOne);
                Assert.DoesNotContain(editor.Targets, target => target.Value is "gz" or "bz2" or "xz" or "png");
                Assert.Contains(editor.Targets, target => target.Value == "7z");
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
