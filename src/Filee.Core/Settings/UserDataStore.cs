// Loads and saves settings.json, presets.json, profiles.json and history.json.
// Writes are atomic (temp file + replace) so a crash never leaves a half-written file.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Filee.Core.History;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.Core.Settings;

/// <summary>
/// In-memory copy of all user data plus persistence. The app keeps exactly one instance.
/// </summary>
public sealed class UserDataStore
{
    private const int MaxHistory = 200;
    private readonly ILogger _log;
    private readonly object _saveLock = new();

    public UserDataStore(string directory, ILogger<UserDataStore>? log = null)
    {
        Directory = directory;
        _log = log ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>Default data directory: %APPDATA%\Filee on Windows, ~/Library/Application Support/Filee on macOS.</summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Filee");

    public string Directory { get; }

    public AppSettings Settings { get; private set; } = new();
    public List<Preset> Presets { get; private set; } = BuiltInData.CreatePresets();
    public List<ToolbarProfile> Profiles { get; private set; } = BuiltInData.CreateProfiles();
    public List<HistoryEntry> History { get; private set; } = [];

    /// <summary>Raised after settings were saved.</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>Raised after presets or profiles were saved.</summary>
    public event EventHandler? LibraryChanged;

    /// <summary>Raised after a history entry was added.</summary>
    public event EventHandler? HistoryChanged;

    private string SettingsPath => Path.Combine(Directory, "settings.json");
    private string PresetsPath => Path.Combine(Directory, "presets.json");
    private string ProfilesPath => Path.Combine(Directory, "profiles.json");
    private string HistoryPath => Path.Combine(Directory, "history.json");

    /// <summary>Loads everything from disk, falling back to defaults for missing or corrupt files.</summary>
    public void Load()
    {
        System.IO.Directory.CreateDirectory(Directory);
        Settings = Read(SettingsPath, FileeJsonContext.Default.AppSettings) ?? new AppSettings();
        Presets = Read(PresetsPath, FileeJsonContext.Default.ListPreset) ?? BuiltInData.CreatePresets();
        Profiles = Read(ProfilesPath, FileeJsonContext.Default.ListToolbarProfile) ?? BuiltInData.CreateProfiles();
        History = Read(HistoryPath, FileeJsonContext.Default.ListHistoryEntry) ?? [];

        var schema = Settings.SchemaVersion;
        SettingsMigrations.Apply(Settings);
        var libraryChanged = File.Exists(ProfilesPath) && SettingsMigrations.ApplyToLibrary(schema, Presets, Profiles);
        EnsureConsistency();

        // Persist upgrades right away so they run once, even if the user never changes a setting.
        if (schema < AppSettings.CurrentSchemaVersion && File.Exists(SettingsPath))
            Write(SettingsPath, Settings, FileeJsonContext.Default.AppSettings);
        if (libraryChanged)
        {
            Write(PresetsPath, Presets, FileeJsonContext.Default.ListPreset);
            Write(ProfilesPath, Profiles, FileeJsonContext.Default.ListToolbarProfile);
        }
    }

    public void SaveSettings()
    {
        Write(SettingsPath, Settings, FileeJsonContext.Default.AppSettings);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SaveLibrary()
    {
        EnsureConsistency();
        Write(PresetsPath, Presets, FileeJsonContext.Default.ListPreset);
        Write(ProfilesPath, Profiles, FileeJsonContext.Default.ListToolbarProfile);
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void AddHistory(HistoryEntry entry)
    {
        lock (_saveLock)
        {
            History.Insert(0, entry);
            if (History.Count > MaxHistory)
                History.RemoveRange(MaxHistory, History.Count - MaxHistory);
        }
        Write(HistoryPath, History, FileeJsonContext.Default.ListHistoryEntry);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearHistory()
    {
        lock (_saveLock)
            History.Clear();
        Write(HistoryPath, History, FileeJsonContext.Default.ListHistoryEntry);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Replaces presets and profiles with factory defaults.</summary>
    public void ResetLibrary()
    {
        Presets = BuiltInData.CreatePresets();
        Profiles = BuiltInData.CreateProfiles();
        SaveLibrary();
    }

    public Preset? FindPreset(string id) => Presets.FirstOrDefault(p => p.Id == id);

    /// <summary>Exports presets and profiles to a shareable JSON file.</summary>
    public void Export(string path) =>
        Write(path, new PresetBundle { Presets = Presets, Profiles = Profiles }, FileeJsonContext.Default.PresetBundle);

    /// <summary>
    /// Imports presets and profiles from a bundle. Items with an existing id are replaced, new ones added.
    /// </summary>
    /// <returns>Number of imported presets + profiles.</returns>
    public int Import(string path)
    {
        using var stream = File.OpenRead(path);
        var bundle = JsonSerializer.Deserialize(stream, FileeJsonContext.Default.PresetBundle)
                     ?? throw new InvalidDataException("Empty preset bundle.");

        foreach (var preset in bundle.Presets)
        {
            Presets.RemoveAll(p => p.Id == preset.Id);
            Presets.Add(preset);
        }
        foreach (var profile in bundle.Profiles)
        {
            Profiles.RemoveAll(p => p.Id == profile.Id);
            profile.IsFallback = false; // keep our own fallback
            Profiles.Add(profile);
        }
        SaveLibrary();
        return bundle.Presets.Count + bundle.Profiles.Count;
    }

    /// <summary>Removes dangling preset references and guarantees exactly one fallback profile.</summary>
    private void EnsureConsistency()
    {
        var ids = Presets.Select(p => p.Id).ToHashSet();
        foreach (var profile in Profiles)
        {
            profile.PresetIds = profile.PresetIds.Where(ids.Contains).Distinct().Take(ToolbarProfile.MaxSlices).ToList();
        }

        var fallbacks = Profiles.Where(p => p.IsFallback).ToList();
        if (fallbacks.Count == 0)
        {
            var mixed = BuiltInData.CreateProfiles().First(p => p.IsFallback);
            mixed.PresetIds = mixed.PresetIds.Where(ids.Contains).ToList();
            Profiles.Add(mixed);
        }
        foreach (var extra in fallbacks.Skip(1))
            extra.IsFallback = false;
    }

    private T? Read<T>(string path, JsonTypeInfo<T> type) where T : class
    {
        if (!File.Exists(path))
            return null;
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, type);
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            // Keep the broken file for inspection and continue with defaults.
            _log.LogWarning(ex, "Could not read {Path}; using defaults", path);
            try
            {
                File.Copy(path, path + ".broken", overwrite: true);
            }
            catch (IOException) { }
            return null;
        }
    }

    private void Write<T>(string path, T value, JsonTypeInfo<T> type)
    {
        lock (_saveLock)
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            using (var stream = File.Create(temp))
                JsonSerializer.Serialize(stream, value, type);
            File.Move(temp, path, overwrite: true);
        }
    }
}

/// <summary>Upgrades settings written by older versions. Add one step per schema bump.</summary>
internal static class SettingsMigrations
{
    public static void Apply(AppSettings settings)
    {
        // v2: engines "pandoc-hwpx" (HWPX without Hancom Office, placed before "hancom" so HWPX saving needs
        //     no approval dialog) and "pandoc" (Markdown) were added.
        if (settings.SchemaVersion < 2)
        {
            InsertBefore(settings.EnginePriority, "pandoc-hwpx", "hancom");
            InsertBefore(settings.EnginePriority, "pandoc", "libreoffice");
        }

        // v3: the Hancom Office engine was removed (rhwp converts HWP <-> HWPX without the approval dialog of 한글)
        //     and "pandoc-hwpx" became "hwpx-writer" (DOCX is read without Pandoc).
        if (settings.SchemaVersion < 3)
        {
            settings.EnginePriority.Remove("hancom");
            var index = settings.EnginePriority.IndexOf("pandoc-hwpx");
            if (index >= 0)
                settings.EnginePriority[index] = "hwpx-writer";
        }

        // v4: built-in "markdown" engine (Markdig, no Pandoc needed); the "text" toolbar profile is added by
        //     ApplyToLibrary.
        if (settings.SchemaVersion < 4)
            InsertBefore(settings.EnginePriority, "markdown", "hwpx-writer");

        // v5: Microsoft Word automation was removed (Filee no longer uses software installed on the system) and the
        //     built-in "spreadsheet" engine (XLSX <-> CSV) was added; profiles are split by ApplyToLibrary.
        if (settings.SchemaVersion < 5)
        {
            settings.EnginePriority.Remove("word");
            InsertBefore(settings.EnginePriority, "spreadsheet", "hwpx-writer");
        }

        if (settings.Triggers.Count == 0)
            settings.Triggers = TriggerGesture.Defaults();

        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
    }

    /// <summary>Upgrades presets and profiles written with settings schema <paramref name="from"/>.</summary>
    /// <returns>True when something was added.</returns>
    public static bool ApplyToLibrary(int from, List<Preset> presets, List<ToolbarProfile> profiles)
    {
        var changed = false;
        // v4: Markdown / TXT / HTML files get their own donut instead of the "mixed files" fallback. Skipped when
        //     the user already routes Markdown through a profile of their own.
        if (from < 4 && !profiles.Any(p => p.Id == "text" || p.Extensions.Contains("md", StringComparer.OrdinalIgnoreCase)))
        {
            var text = BuiltInData.TextProfile();
            var ids = presets.Select(p => p.Id).ToHashSet();
            text.PresetIds = text.PresetIds.Where(ids.Contains).ToList();
            var fallback = profiles.FindIndex(p => p.IsFallback);
            profiles.Insert(fallback < 0 ? profiles.Count : fallback, text);
            changed = true;
        }

        // v5: spreadsheets and presentations get their own donuts (with XLSX / CSV targets) instead of sharing the
        //     documents donut, and the PDF donut drops targets only Word could produce (PDF → DOCX / HWPX / TXT).
        if (from < 5)
        {
            foreach (var preset in BuiltInData.SpreadsheetPresets().Where(p => presets.All(existing => existing.Id != p.Id)))
            {
                presets.Add(preset);
                changed = true;
            }

            var office = profiles.FirstOrDefault(p => p.Id == "office");
            var insertAt = office is null ? profiles.FindIndex(p => p.IsFallback) : profiles.IndexOf(office) + 1;
            var ids = presets.Select(p => p.Id).ToHashSet();
            foreach (var split in new[] { BuiltInData.SpreadsheetProfile(), BuiltInData.PresentationProfile() })
            {
                if (profiles.Any(p => p.Id == split.Id))
                    continue;
                split.PresetIds = split.PresetIds.Where(ids.Contains).ToList();
                // The new donut takes over these extensions from the built-in documents donut, but not from
                // profiles the user made.
                changed |= office?.Extensions.RemoveAll(split.Extensions.Contains) > 0;
                if (profiles.Any(p => p.Extensions.Intersect(split.Extensions, StringComparer.OrdinalIgnoreCase).Any()))
                    continue;
                insertAt = insertAt < 0 ? profiles.Count : insertAt;
                profiles.Insert(insertAt++, split);
                changed = true;
            }

            if (profiles.FirstOrDefault(p => p.Id == "pdf") is { } pdf)
                changed |= pdf.PresetIds.RemoveAll(id => id is "to-docx" or "to-hwpx" or "to-txt") > 0;
        }
        return changed;
    }

    private static void InsertBefore(List<string> list, string id, string before)
    {
        if (list.Contains(id))
            return;
        var index = list.IndexOf(before);
        if (index < 0)
            list.Add(id);
        else
            list.Insert(index, id);
    }
}
