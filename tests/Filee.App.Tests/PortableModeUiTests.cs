// Portable settings clearly disable integration and retain the ordinary user interface elsewhere.

using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels.Pages;
using Filee.App.Views.Pages;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.App.Tests;

[CollectionDefinition("Portable UI environment", DisableParallelization = true)]
public sealed class PortableUiEnvironmentCollection;

[Collection("Portable UI environment")]
public sealed class PortableModeUiTests
{
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public void Portable_settings_show_the_data_location_and_disable_integration(string language)
    {
        TestServices.EnsureInitialized(language);
        var root = Directory.CreateTempSubdirectory("filee-portable-ui-").FullName;
        var previous = Environment.GetEnvironmentVariable(UserDataStore.DirectoryVariable);
        Environment.SetEnvironmentVariable(UserDataStore.DirectoryVariable, null);
        try
        {
            File.WriteAllText(Path.Combine(root, PortableMode.MarkerName), "");
            using var portable = PortableMode.Initialize(root);
            Assert.False(UpdateService.IsInstalled);
            var store = new UserDataStore(UserDataStore.DefaultDirectory);
            store.Load();
            using var vm = new GeneralPageViewModel(store, AppHost.Get<ILocalizer>(), AppHost.Get<IPlatformServices>(),
                NullLogger<GeneralPageViewModel>.Instance);
            Assert.True(vm.IsPortable);
            Assert.False(vm.StartWithSystem);
            Assert.False(vm.ContextMenu);
            Assert.False(vm.CanChangeSystemIntegration);
            Assert.False(vm.CanAddModernMenu);
            Assert.False(vm.CanRemoveModernMenu);
            Assert.False(vm.ShowModernMenu);
            Assert.Equal(Path.Combine(root, "data"), vm.PortableDataDirectory);
            var window = new Window { Content = new GeneralPage { DataContext = vm }, Width = 800, Height = 1000 };
            try
            {
                window.Show();
                RenderTests.Pump();
                var switches = window.GetVisualDescendants().OfType<ToggleSwitch>()
                    .Where(t => t.DataContext == vm && (t.Content?.ToString() == AppHost.Get<ILocalizer>()["general.start_with_system"]
                        || t.Content?.ToString() == vm.ContextMenuText)).ToArray();
                Assert.Equal(2, switches.Length);
                Assert.All(switches, t => Assert.False(t.IsEnabled));
                RenderTests.Save(window, $"general-portable-{language}.png");
            }
            finally { window.Close(); }
        }
        finally
        {
            Environment.SetEnvironmentVariable(UserDataStore.DirectoryVariable, previous);
            Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public void Every_language_has_the_portable_hint_and_formattable_startup_error()
    {
        TestServices.EnsureInitialized("en");
        foreach (var language in LocalizationService.Languages)
        {
            var catalog = LocalizationService.LoadStrings(language.Code);
            Assert.False(string.IsNullOrWhiteSpace(catalog["general.portable_mode"]));
            Assert.Contains("example-data", string.Format(catalog["startup.portable_unwritable"], "example-data"));
            Assert.Contains("old-profile", string.Format(catalog["portable.import_prompt"], "old-profile", "portable-data"));
            Assert.Contains("portable-data", string.Format(catalog["portable.import_prompt"], "old-profile", "portable-data"));
            Assert.False(string.IsNullOrWhiteSpace(catalog["portable.cleanup_prompt"]));
            Assert.False(string.IsNullOrWhiteSpace(catalog["portable.import_existing"]));
            Assert.False(string.IsNullOrWhiteSpace(catalog["portable.start_fresh"]));
            Assert.False(string.IsNullOrWhiteSpace(catalog["portable.choose_settings"]));
            Assert.False(string.IsNullOrWhiteSpace(catalog["portable.choose_engines"]));
            foreach (var key in new[] { "portable.choose_presets", "portable.choose_profiles", "portable.choose_history", "portable.profile_dependencies" })
                Assert.False(string.IsNullOrWhiteSpace(catalog[key]));
            foreach (var key in new[] { "portable.import_summary", "portable.import_next", "portable.fresh_next" })
                Assert.False(string.IsNullOrWhiteSpace(catalog[key]));
        }
    }
}
