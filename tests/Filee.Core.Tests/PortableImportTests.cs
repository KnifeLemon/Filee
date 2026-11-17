using Filee.Core.Settings;

namespace Filee.Core.Tests;

public sealed class PortableImportTests : IDisposable
{
    [Theory]
    [InlineData("settings.json")]
    [InlineData("presets.json")]
    [InlineData("profiles.json")]
    [InlineData("history.json")]
    public void Each_profile_file_can_be_imported_without_the_others(string selected)
    {
        foreach (var name in new[] { "presets.json", "profiles.json", "history.json" })
            File.WriteAllText(Path.Combine(Source, name), "[]");
        var selection = new PortableImport.Selection
        {
            Settings = selected == "settings.json",
            Presets = selected == "presets.json",
            Profiles = selected == "profiles.json",
            History = selected == "history.json",
            Engines = false,
        };
        PortableImport.Copy(Source, Engines, Destination, selection: selection);
        foreach (var name in new[] { "settings.json", "presets.json", "profiles.json", "history.json" })
            Assert.Equal(name == selected, File.Exists(Path.Combine(Destination, name)));
    }

    [Fact]
    public void History_can_be_excluded_while_importing_everything_else()
    {
        File.WriteAllText(Path.Combine(Source, "history.json"), "[]");
        File.WriteAllText(Path.Combine(Source, "presets.json"), "[]");
        File.WriteAllText(Path.Combine(Source, "profiles.json"), "[]");
        PortableImport.Copy(Source, Engines, Destination, selection: new() { History = false });
        Assert.True(File.Exists(Path.Combine(Destination, "settings.json")));
        Assert.True(File.Exists(Path.Combine(Destination, "presets.json")));
        Assert.True(File.Exists(Path.Combine(Destination, "profiles.json")));
        Assert.False(File.Exists(Path.Combine(Destination, "history.json")));
        Assert.True(File.Exists(Path.Combine(Source, "history.json")));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Only_selected_categories_are_copied(bool settings, bool engines)
    {
        Directory.CreateDirectory(Engines);
        File.WriteAllText(Path.Combine(Engines, "tool.exe"), "engine");
        Assert.Equal(6, PortableImport.EngineSize(Engines));
        PortableImport.Copy(Source, Engines, Destination, selection: new() { Settings = settings, Presets = settings, Profiles = settings, History = settings, Engines = engines });
        Assert.Equal(settings, File.Exists(Path.Combine(Destination, "settings.json")));
        Assert.Equal(engines, File.Exists(Path.Combine(Destination, "engines", "tool.exe")));
        Assert.True(File.Exists(Path.Combine(Source, "settings.json")));
        Assert.True(File.Exists(Path.Combine(Engines, "tool.exe")));
        Assert.False(PortableImport.CanOffer(Source, Destination));
    }

    [Fact]
    public void Empty_selection_does_not_change_data()
    {
        Assert.Throws<ArgumentException>(() => PortableImport.Copy(Source, Engines, Destination,
            selection: new() { Settings = false, Presets = false, Profiles = false, History = false, Engines = false }));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Destination));
    }

    private readonly string _root = Directory.CreateTempSubdirectory("filee-import-").FullName;
    private string Source => Path.Combine(_root, "profile");
    private string Destination => Path.Combine(_root, "portable");
    private string Engines => Path.Combine(_root, "downloaded");

    public PortableImportTests()
    {
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Destination);
        File.WriteAllText(Path.Combine(Source, "settings.json"), "{\"language\":\"de\",\"firstRunCompleted\":true}");
    }

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Import_copies_profile_and_engines_and_keeps_originals()
    {
        Directory.CreateDirectory(Path.Combine(Engines, "tool"));
        File.WriteAllText(Path.Combine(Engines, "tool", "program"), "engine");
        File.WriteAllText(Path.Combine(Source, "presets.json"), "[]");
        Directory.CreateDirectory(Path.Combine(Destination, "logs"));
        PortableImport.Copy(Source, Engines, Destination);
        Assert.Equal(File.ReadAllText(Path.Combine(Source, "settings.json")), File.ReadAllText(Path.Combine(Destination, "settings.json")));
        Assert.Equal("[]", File.ReadAllText(Path.Combine(Destination, "presets.json")));
        Assert.Equal("engine", File.ReadAllText(Path.Combine(Destination, "engines", "tool", "program")));
        Assert.True(File.Exists(Path.Combine(Engines, "tool", "program")));
        Assert.False(PortableImport.CanOffer(Source, Destination));
        Assert.Empty(Directory.GetDirectories(Destination, ".portable-import-*"));
        var imported = new UserDataStore(Destination);
        imported.Load();
        Assert.Equal("de", imported.Settings.Language);
        Assert.True(imported.Settings.FirstRunCompleted);
    }

    [Fact]
    public void Declining_is_remembered_without_copying_any_profile()
    {
        Assert.True(PortableImport.CanOffer(Source, Destination));
        PortableImport.Decline(Destination);
        Assert.False(PortableImport.CanOffer(Source, Destination));
        Assert.False(File.Exists(Path.Combine(Destination, "settings.json")));
    }

    [Theory]
    [InlineData("settings.json")]
    [InlineData("profiles.json")]
    [InlineData("history.json")]
    [InlineData("presets.json")]
    public void Existing_portable_data_is_never_overwritten(string name)
    {
        File.WriteAllText(Path.Combine(Destination, name), "keep");
        Assert.Throws<IOException>(() => PortableImport.Copy(Source, Engines, Destination));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(Destination, name)));
    }

    [Fact]
    public void Failed_engine_copy_leaves_destination_empty_and_retry_possible()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix symbolic links without administrator rights.");
        Directory.CreateDirectory(Engines);
        File.CreateSymbolicLink(Path.Combine(Engines, "linked"), Path.Combine(Source, "settings.json"));
        Assert.Throws<IOException>(() => PortableImport.Copy(Source, Engines, Destination));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Destination));
        Assert.True(PortableImport.CanOffer(Source, Destination));
        Assert.True(File.Exists(Path.Combine(Source, "settings.json")));
    }

    [Fact]
    public void Existing_engines_and_same_profile_are_not_import_candidates()
    {
        Assert.False(PortableImport.CanOffer(Source, Source));
        Directory.CreateDirectory(Path.Combine(Destination, "engines"));
        Assert.False(PortableImport.CanOffer(Source, Destination));
    }

    [Fact]
    public void Invalid_profile_is_rejected_before_any_destination_files_are_committed()
    {
        File.WriteAllText(Path.Combine(Source, "settings.json"), "broken json");
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => PortableImport.Copy(Source, Engines, Destination));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Destination));
    }
}
