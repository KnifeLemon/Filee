// Screen readers announce a control by its accessible name (#524): icon-only buttons and fields whose label is a
// separate TextBlock need AutomationProperties.Name, or they are read as just "button" / "combo box".

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.App.Views;
using Filee.Core.Localization;
using Filee.Core.Settings;
using Filee.Core.Watching;

namespace Filee.App.Tests;

public class AutomationNameTests
{
    [AvaloniaTheory]
    [InlineData("home")]
    [InlineData("general")]
    [InlineData("triggers")]
    [InlineData("toolbar")]
    [InlineData("presets")]
    [InlineData("theme")]
    [InlineData("engines")]
    [InlineData("about")]
    [InlineData("watch")]
    public void Every_icon_button_and_field_on_a_page_has_a_name(string page)
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var rule = new WatchRule { Folder = @"C:\Scans\Inbox", PresetId = "to-pdf", Enabled = false };
        store.Settings.WatchFolders.Add(rule);
        try
        {
            using var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>(), AppHost.Get<UserDataStore>());
            vm.Navigate(page);
            var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
            window.Show();
            RenderTests.Pump();

            var unnamed = window.GetVisualDescendants().OfType<Control>()
                .Where(c => c.TemplatedParent is null && c.IsEffectivelyVisible && NeedsName(c)
                            && string.IsNullOrWhiteSpace(AutomationProperties.GetName(c)))
                .Select(c => $"{c.GetType().Name} {c.Name} (in {c.DataContext?.GetType().Name})")
                .ToList();
            window.Close();

            Assert.Empty(unnamed);
        }
        finally
        {
            store.Settings.WatchFolders.Remove(rule);
        }
    }

    [AvaloniaFact]
    public void A_watch_folder_switch_is_named_after_its_folder_and_shows_no_on_label()
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var rule = new WatchRule { Folder = @"C:\Scans\Inbox", PresetId = "to-pdf", Enabled = false };
        store.Settings.WatchFolders.Add(rule);
        try
        {
            using var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>(), AppHost.Get<UserDataStore>());
            vm.Navigate("watch");
            var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
            window.Show();
            RenderTests.Pump();

            var toggle = Assert.Single(window.GetVisualDescendants().OfType<ToggleSwitch>(), t => t.DataContext is WatchRuleViewModel);
            // An off rule read "On  Off": the switch's own label, then the status.
            Assert.Equal("", toggle.OffContent);
            Assert.Equal("", toggle.OnContent);
            Assert.Equal(@"C:\Scans\Inbox", AutomationProperties.GetName(toggle));
            window.Close();
        }
        finally
        {
            store.Settings.WatchFolders.Remove(rule);
        }
    }

    [AvaloniaFact]
    public void A_collapsed_navigation_names_every_item_by_its_title_for_the_tooltip_and_screen_readers()
    {
        TestServices.EnsureInitialized("en");
        using var dir = new TempNavDirectory();
        var store = new UserDataStore(dir.Path);
        store.Load();
        using var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>(), store);
        vm.IsNavCollapsed = true;
        var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
        window.Show();
        RenderTests.Pump();

        // The row that carries the tooltip is the item template's Border, one per navigation item.
        var rows = window.GetVisualDescendants().OfType<Border>().Where(b => b.DataContext is NavItem && ToolTip.GetTip(b) is not null).ToList();
        Assert.Equal(vm.Items.Count, rows.Count);
        foreach (var row in rows)
        {
            var item = (NavItem)row.DataContext!;
            Assert.False(string.IsNullOrWhiteSpace(item.Title));
            Assert.Equal(item.Title, ToolTip.GetTip(row));
            Assert.Equal(item.Title, AutomationProperties.GetName(row));
        }

        // Expanded, the title is on screen, so there is no tooltip repeating it.
        vm.IsNavCollapsed = false;
        RenderTests.Pump();
        Assert.All(vm.Items, i => Assert.Null(i.Tip));
        window.Close();
    }

    private static bool NeedsName(Control c) => c switch
    {
        ToggleSwitch t => t.Content is null,
        TextBox t => string.IsNullOrEmpty(t.PlaceholderText),
        ComboBox b => string.IsNullOrEmpty(b.PlaceholderText),
        Slider or NumericUpDown => true,
        // A button with text says what it does; one with only an icon doesn't.
        Button b => b.Content is not string && !b.GetVisualDescendants().OfType<TextBlock>().Any(t => !string.IsNullOrWhiteSpace(t.Text)),
        _ => false,
    };
}

/// <summary>A throwaway data folder, so a test that changes settings never touches the real ones.</summary>
internal sealed class TempNavDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "filee-nav-" + Guid.NewGuid().ToString("N"));

    public TempNavDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}
