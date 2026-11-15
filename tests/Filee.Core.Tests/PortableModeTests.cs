// Marker detection, writable storage and portable settings without touching the real user profile.

using Filee.Core.Settings;

namespace Filee.Core.Tests;

[CollectionDefinition("Portable environment", DisableParallelization = true)]
public sealed class PortableEnvironmentCollection;

[Collection("Portable environment")]
public sealed class PortableModeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("filee-portable-tests-").FullName;
    private readonly string? _data = Environment.GetEnvironmentVariable(UserDataStore.DirectoryVariable);
    private readonly string? _cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");

    public PortableModeTests() => Environment.SetEnvironmentVariable(UserDataStore.DirectoryVariable, null);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(UserDataStore.DirectoryVariable, _data);
        Environment.SetEnvironmentVariable("XDG_CACHE_HOME", _cache);
        Directory.Delete(_root, true);
    }

    private string Mark(string? directory = null)
    {
        directory ??= _root;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, PortableMode.MarkerName), "");
        return directory;
    }

    [Fact]
    public void An_unmarked_copy_keeps_its_existing_behavior()
    {
        Assert.Null(PortableMode.Resolve(_root, null));
        Assert.Null(PortableMode.Initialize(_root));
        Assert.False(PortableMode.IsEnabled);
        Assert.Null(Environment.GetEnvironmentVariable(UserDataStore.DirectoryVariable));
        Assert.False(Directory.Exists(Path.Combine(_root, "data")));
    }

    [Fact]
    public void A_marker_selects_data_beside_the_application_and_cleans_its_probe()
    {
        Mark();
        using var portable = PortableMode.Initialize(_root);
        Assert.True(PortableMode.IsEnabled);
        Assert.Equal(Path.Combine(_root, "data"), UserDataStore.DefaultDirectory);
        Assert.Empty(Directory.GetFiles(UserDataStore.DefaultDirectory));
    }

    [Fact]
    public void An_explicit_data_override_still_wins()
    {
        Mark();
        var chosen = Path.Combine(_root, "chosen");
        Environment.SetEnvironmentVariable(UserDataStore.DirectoryVariable, chosen);
        using var portable = PortableMode.Initialize(_root);
        Assert.Equal(chosen, PortableMode.DataDirectory);
        Assert.False(Directory.Exists(Path.Combine(_root, "data")));
    }

    [Fact]
    public void A_mac_bundle_uses_the_marker_and_data_outside_its_signed_contents()
    {
        var executable = Directory.CreateDirectory(Path.Combine(_root, "Filee.app", "Contents", "MacOS")).FullName;
        Assert.Null(PortableMode.Resolve(executable, null));
        Mark();
        Assert.Equal(Path.Combine(_root, "data"), PortableMode.Resolve(executable, null));
    }

    [Fact]
    public void A_directory_named_like_the_marker_does_not_enable_portable_mode()
    {
        Directory.CreateDirectory(Path.Combine(_root, PortableMode.MarkerName));
        Assert.Null(PortableMode.Resolve(_root, null));
    }

    [Fact]
    public void An_unwritable_data_path_fails_without_creating_a_fallback()
    {
        Mark();
        var data = Path.Combine(_root, "data");
        File.WriteAllText(data, "blocks directory creation");
        var error = Assert.Throws<PortableStorageException>(() => PortableMode.Initialize(_root));
        Assert.Equal(data, error.DataDirectory);
        Assert.IsAssignableFrom<IOException>(error.InnerException);
        Assert.False(PortableMode.IsEnabled);
        Assert.Null(Environment.GetEnvironmentVariable(UserDataStore.DirectoryVariable));
        Assert.Equal("blocks directory creation", File.ReadAllText(data));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Fresh_and_existing_settings_cannot_enable_system_integration(bool existing)
    {
        Mark();
        using var portable = PortableMode.Initialize(_root);
        if (existing)
            File.WriteAllText(Path.Combine(UserDataStore.DefaultDirectory, "settings.json"),
                "{\"startWithSystem\":true,\"contextMenuEnabled\":true}");
        var store = new UserDataStore(UserDataStore.DefaultDirectory);
        store.Load();
        Assert.False(store.Settings.StartWithSystem);
        Assert.False(store.Settings.ContextMenuEnabled);
        store.Settings.StartWithSystem = store.Settings.ContextMenuEnabled = true;
        store.SaveSettings();
        var reloaded = new UserDataStore(UserDataStore.DefaultDirectory);
        reloaded.Load();
        Assert.False(reloaded.Settings.StartWithSystem);
        Assert.False(reloaded.Settings.ContextMenuEnabled);
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void A_read_only_unix_directory_reports_the_selected_path()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix directory permissions.");
        var data = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        var mode = File.GetUnixFileMode(data);
        try
        {
            File.SetUnixFileMode(data, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var error = Assert.Throws<PortableStorageException>(() => PortableMode.Validate(data));
            Assert.Equal(data, error.DataDirectory);
        }
        finally { File.SetUnixFileMode(data, mode); }
    }

    [Fact]
    public void A_moved_copy_keeps_its_settings_in_the_moved_data_folder()
    {
        var first = Mark(Path.Combine(_root, "first"));
        using (PortableMode.Initialize(first))
        {
            var store = new UserDataStore(UserDataStore.DefaultDirectory);
            store.Load();
            store.Settings.Language = "de";
            store.SaveSettings();
        }
        var moved = Path.Combine(_root, "moved");
        Directory.Move(first, moved);
        using (PortableMode.Initialize(moved))
        {
            var store = new UserDataStore(UserDataStore.DefaultDirectory);
            store.Load();
            Assert.Equal("de", store.Settings.Language);
            Assert.Equal(Path.Combine(moved, "data"), store.Directory);
        }
        Assert.False(Directory.Exists(first));
    }

    [Fact]
    public void Portable_mode_preserves_the_existing_temp_cache_and_profile_environment()
    {
        Mark();
        string[] keys = ["TEMP", "TMP", "TMPDIR", "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "XDG_CACHE_HOME", "XDG_CONFIG_HOME", "XDG_DATA_HOME"];
        var before = keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        var temporary = Path.GetTempPath();
        using (PortableMode.Initialize(_root))
        {
            Assert.Equal(temporary, Path.GetTempPath());
            foreach (var key in keys)
                Assert.Equal(before[key], Environment.GetEnvironmentVariable(key));
        }
        Assert.False(PortableMode.IsEnabled);
        Assert.Equal(_cache, Environment.GetEnvironmentVariable("XDG_CACHE_HOME"));
        Assert.False(Directory.Exists(Path.Combine(_root, "data", "temp")));
    }
}
