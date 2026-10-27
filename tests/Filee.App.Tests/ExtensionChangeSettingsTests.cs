using Filee.App.ViewModels.Pages;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Filee.App.Tests;

public sealed class ExtensionChangeSettingsTests
{
    private sealed class MockLocalizer : ILocalizer
    {
        public string Language => "en";
        public event EventHandler? LanguageChanged { add { } remove { } }
        public string this[string key] => key;
        public string Format(string key, params object[] args) => string.Format(key, args);
    }

    [Fact]
    public void Settings_defaults_and_toggle_behavior()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "FileeSettingsTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new UserDataStore(tempDir, NullLogger<UserDataStore>.Instance);
            Assert.False(store.Settings.ConvertOnExtensionChange);
            Assert.True(store.Settings.ConfirmExtensionChange);
            Assert.Equal(ExtensionChangeScope.WatchFolders, store.Settings.ExtensionChangeScope);

            var loc = new MockLocalizer();
            var vm = new GeneralPageViewModel(store, loc, new NullPlatformServices(), NullLogger<GeneralPageViewModel>.Instance);

            Assert.False(vm.ConvertOnExtensionChange);
            Assert.True(vm.ConfirmExtensionChange);
            Assert.True(vm.ScopeWatchFolders);
            Assert.False(vm.ScopeEntireSystem);

            // Toggle ConvertOnExtensionChange
            vm.ConvertOnExtensionChange = true;
            Assert.True(store.Settings.ConvertOnExtensionChange);

            // Toggle ConfirmExtensionChange
            vm.ConfirmExtensionChange = false;
            Assert.False(store.Settings.ConfirmExtensionChange);

            // Toggle Scope to EntireSystem
            vm.ScopeEntireSystem = true;
            Assert.True(vm.ScopeEntireSystem);
            Assert.False(vm.ScopeWatchFolders);
            Assert.Equal(ExtensionChangeScope.EntireSystem, store.Settings.ExtensionChangeScope);

            // Toggle Scope back to WatchFolders
            vm.ScopeWatchFolders = true;
            Assert.True(vm.ScopeWatchFolders);
            Assert.False(vm.ScopeEntireSystem);
            Assert.Equal(ExtensionChangeScope.WatchFolders, store.Settings.ExtensionChangeScope);

            // React to external change (e.g. from dialog or external setting reload)
            store.Settings.ConfirmExtensionChange = true;
            store.Settings.ExtensionChangeScope = ExtensionChangeScope.EntireSystem;
            store.SaveSettings();

            Assert.True(vm.ConfirmExtensionChange);
            Assert.True(vm.ScopeEntireSystem);
            Assert.False(vm.ScopeWatchFolders);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
