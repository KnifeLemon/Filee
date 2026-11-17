using System.Text.Json.Nodes;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.App.Views;
using Filee.App.Views.Pages;
using Filee.Core.Formats;
using Filee.Core.Localization;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class FieldsPageTests
{
    [AvaloniaFact]
    public void The_page_shows_the_twelve_fields_with_the_new_install_defaults()
    {
        TestServices.EnsureInitialized();
        var store = AppHost.Get<UserDataStore>();
        var before = store.Settings.TurnedOffFields.ToList();
        var window = new Window { Width = 800, Height = 820 };
        try
        {
            store.Settings.TurnedOffFields = FormatFields.All.Where(f => f.CanTurnOff && !f.OnByDefault)
                .Select(f => f.Id).ToList();
            var loc = AppHost.Get<ILocalizer>();
            window.Content = new FieldsPage { DataContext = new FieldsPageViewModel(store, loc) };
            window.Show();
            RenderTests.Pump();

            var switches = window.GetVisualDescendants().OfType<ToggleSwitch>().ToList();
            Assert.Equal(12, switches.Count);
            Assert.Equal(FormatFields.All.Where(f => f.CanTurnOff).Select(f => loc[$"fields.name.{f.Id}"]),
                switches.Select(AutomationProperties.GetName));
            Assert.Equal(loc["fields.name.developer"], AutomationProperties.GetName(Assert.Single(switches, s => s.IsChecked == true)));
            Assert.All(switches.Where(s => AutomationProperties.GetName(s) != loc["fields.name.developer"]),
                s => Assert.False(s.IsChecked));

            // The fields that can't be turned off are named at the end, by their category names.
            var alwaysShown = FormatFields.All.Where(f => !f.CanTurnOff)
                .Select(f => loc[$"format.category.{Enum.Parse<FormatCategory>(f.Id, true)}"]).ToList();
            Assert.Equal(14, alwaysShown.Count);
            Assert.Contains(loc["format.category.Image"], alwaysShown);
            var sentence = Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(),
                t => t.Text == loc.Format("fields.always_shown", string.Join(", ", alwaysShown)));
            Assert.True(sentence.IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
            store.Settings.TurnedOffFields = before;
        }
    }

    [AvaloniaFact]
    public void A_row_lists_only_formats_its_switch_can_hide()
    {
        TestServices.EnsureInitialized();
        var store = AppHost.Get<UserDataStore>();
        var loc = AppHost.Get<ILocalizer>();
        var vm = new FieldsPageViewModel(store, loc);
        var prefix = loc.Format("fields.formats", "");
        // With every switch off, a listed format is hidden; one also in a field that can't be turned off never is.
        var allOff = FormatFields.All.Where(f => f.CanTurnOff).Select(f => f.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var overlapping = 0;
        foreach (var row in vm.Fields)
        {
            var field = FormatFields.All.Single(f => f.Id == row.Id);
            var listed = row.Formats.Length == 0 ? []
                : row.Formats[prefix.Length..].TrimEnd('\u2026').Split(", ");
            Assert.All(listed, name => Assert.False(FormatFields.IsShown(
                FormatRegistry.Known.First(f => f.Writable && f.DisplayName == name && FormatFields.Of(f).Contains(field)).Id, allOff)));
            foreach (var format in FormatRegistry.Known.Where(f => f.Writable && FormatFields.Of(f).Contains(field)
                         && FormatFields.Of(f).Any(x => !x.CanTurnOff)))
            {
                overlapping++;
                Assert.DoesNotContain(format.DisplayName, listed);
            }
        }
        // TGA (Image + Games) and DXF (Vector + CAD) were listed before: the case exists.
        Assert.True(overlapping > 0);
        Assert.DoesNotContain("TGA", vm.Fields.Single(r => r.Id == "game").Formats);
        Assert.DoesNotContain("DXF", vm.Fields.Single(r => r.Id == "cad").Formats);
    }

    [AvaloniaFact]
    public void Turning_a_field_off_on_the_page_saves_it_and_survives_a_restart()
    {
        TestServices.EnsureInitialized();
        var directory = Directory.CreateTempSubdirectory("filee-fields-").FullName;
        var window = new Window { Width = 800, Height = 820 };
        var restarted = new Window { Width = 800, Height = 820 };
        try
        {
            var store = new UserDataStore(directory);
            store.Load();
            store.Settings.TurnedOffFields = ["no-such-field"];
            store.SaveSettings();
            var saves = 0;
            store.SettingsChanged += (_, _) => saves++;
            var loc = AppHost.Get<ILocalizer>();
            window.Content = new FieldsPage { DataContext = new FieldsPageViewModel(store, loc) };
            window.Show();
            RenderTests.Pump();
            Assert.Equal(0, saves);
            var developer = window.GetVisualDescendants().OfType<ToggleSwitch>()
                .Single(s => AutomationProperties.GetName(s) == loc["fields.name.developer"]);
            Assert.True(developer.IsChecked);

            developer.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(["no-such-field", "developer"], store.Settings.TurnedOffFields);
            Assert.Equal(1, saves);
            var json = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "settings.json")))!;
            Assert.Equal(["no-such-field", "developer"], json["turnedOffFields"]!.AsArray().Select(n => n!.GetValue<string>()));

            var reloaded = new UserDataStore(directory);
            reloaded.Load();
            Assert.Equal(["no-such-field", "developer"], reloaded.Settings.TurnedOffFields);
            restarted.Content = new FieldsPage { DataContext = new FieldsPageViewModel(reloaded, loc) };
            restarted.Show();
            RenderTests.Pump();
            var switches = restarted.GetVisualDescendants().OfType<ToggleSwitch>().ToList();
            Assert.Equal(12, switches.Count);
            Assert.Equal(loc["fields.name.developer"], AutomationProperties.GetName(Assert.Single(switches, s => s.IsChecked == false)));
            Assert.All(switches.Where(s => AutomationProperties.GetName(s) != loc["fields.name.developer"]),
                s => Assert.True(s.IsChecked));
        }
        finally
        {
            restarted.Close();
            window.Close();
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public void Turning_a_field_on_keeps_ids_this_version_does_not_know()
    {
        TestServices.EnsureInitialized();
        var directory = Directory.CreateTempSubdirectory("filee-fields-").FullName;
        var window = new Window { Width = 800, Height = 820 };
        try
        {
            var store = new UserDataStore(directory);
            store.Load();
            store.Settings.TurnedOffFields = ["CAD", "no-such-field"];
            store.SaveSettings();
            var saves = 0;
            store.SettingsChanged += (_, _) => saves++;
            var loc = AppHost.Get<ILocalizer>();
            window.Content = new FieldsPage { DataContext = new FieldsPageViewModel(store, loc) };
            window.Show();
            RenderTests.Pump();
            Assert.Equal(0, saves);
            var cad = window.GetVisualDescendants().OfType<ToggleSwitch>()
                .Single(s => AutomationProperties.GetName(s) == loc["fields.name.cad"]);
            Assert.False(cad.IsChecked);

            cad.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(["no-such-field"], store.Settings.TurnedOffFields);
            Assert.Equal(1, saves);
            var json = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "settings.json")))!;
            Assert.Equal(["no-such-field"], json["turnedOffFields"]!.AsArray().Select(n => n!.GetValue<string>()));
        }
        finally
        {
            window.Close();
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ko")]
    [InlineData("ar")]
    public void The_fields_page_renders_with_the_new_install_defaults(string language)
    {
        TestServices.EnsureInitialized(language);
        var store = AppHost.Get<UserDataStore>();
        var before = store.Settings.TurnedOffFields.ToList();
        MainWindow? window = null;
        try
        {
            store.Settings.TurnedOffFields = FormatFields.All.Where(f => f.CanTurnOff && !f.OnByDefault)
                .Select(f => f.Id).ToList();
            var loc = AppHost.Get<ILocalizer>();
            using var vm = new MainWindowViewModel(AppHost.Services, loc, AppHost.Get<UpdateService>(), store);
            vm.Navigate("fields");
            window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
            window.Show();
            RenderTests.Pump();

            var switches = window.GetVisualDescendants().OfType<ToggleSwitch>().ToList();
            Assert.Equal(12, switches.Count);
            Assert.Equal(loc["fields.name.developer"], AutomationProperties.GetName(Assert.Single(switches, s => s.IsChecked == true)));
            Assert.All(switches.Where(s => AutomationProperties.GetName(s) != loc["fields.name.developer"]),
                s => Assert.False(s.IsChecked));
            RenderTests.Save(window, $"page-fields-{language}.png");
        }
        finally
        {
            window?.Close();
            store.Settings.TurnedOffFields = before;
        }
    }
}
