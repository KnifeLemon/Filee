// The main window's navigation can collapse to an icon rail, and Filee remembers how the user left it.

using Avalonia.Headless.XUnit;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.Core.Localization;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class NavCollapseTests
{
    private static MainWindowViewModel Create(UserDataStore store)
    {
        TestServices.EnsureInitialized("en");
        return new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>(), store);
    }

    private static UserDataStore Open(string directory)
    {
        var store = new UserDataStore(directory);
        store.Load();
        return store;
    }

    [AvaloniaFact]
    public void Collapsing_the_navigation_saves_it_and_a_new_window_starts_collapsed()
    {
        using var dir = new TempNavDirectory();
        var store = Open(dir.Path);
        using (var vm = Create(store))
        {
            Assert.False(vm.IsNavCollapsed);
            vm.ToggleNavCommand.Execute(null);
            Assert.True(vm.IsNavCollapsed);
            Assert.All(vm.Items, i => Assert.True(i.Collapsed));
        }
        Assert.True(store.Settings.NavCollapsed);

        // A fresh store reads the file back, as the next start of Filee does.
        using var next = Create(Open(dir.Path));
        Assert.True(next.IsNavCollapsed);
        Assert.Equal(64, next.NavWidth);
        Assert.All(next.Items, i => Assert.True(i.Collapsed));

        next.ToggleNavCommand.Execute(null);
        using var again = Create(Open(dir.Path));
        Assert.False(again.IsNavCollapsed);
        Assert.Equal(230, again.NavWidth);
    }

    [AvaloniaFact]
    public void Settings_from_before_the_rail_start_expanded()
    {
        using var dir = new TempNavDirectory();
        File.WriteAllText(Path.Combine(dir.Path, "settings.json"), "{}");
        using var vm = Create(Open(dir.Path));
        Assert.False(vm.IsNavCollapsed);
    }
}
