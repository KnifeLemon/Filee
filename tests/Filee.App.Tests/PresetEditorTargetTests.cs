// The preset editor's target list offers only formats Filee can write: read-only FON (#177) is a source, never a target.

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Localization;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class PresetEditorTargetTests
{
    [AvaloniaFact]
    public void Preset_editor_target_box_excludes_fon_and_shows_ttf()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var editor = new PresetEditorViewModel(store.FindPreset("to-ttf")!, AppHost.Get<ILocalizer>());
        var window = new PresetEditorWindow { DataContext = editor, Height = 900 };
        window.Show();
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        try
        {
            var box = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "TargetBox");
            var targets = box.Items.OfType<Choice<string>>().Select(c => c.Value).ToList();
            Assert.NotEmpty(targets);
            Assert.DoesNotContain("fon", targets);
            Assert.Single(targets, t => t == "ttf");
            Assert.Equal("ttf", ((Choice<string>)box.SelectedItem!).Value);
        }
        finally
        {
            window.Close();
        }
    }
}
