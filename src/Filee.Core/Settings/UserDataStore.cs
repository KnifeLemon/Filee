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
    private readonly ILogger _log;
    private readonly object _saveLock = new();

    public UserDataStore(string directory, ILogger<UserDataStore>? log = null)
    {
        Directory = directory;
        _log = log ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>
    /// Environment variable that moves all of Filee's per-user data (settings, logs, downloaded engines) to another
    /// folder and gives that copy its own single instance: a development or test copy then never touches, or talks
    /// to, the Filee the user runs. A copy started with it also leaves the system integration alone.
    /// </summary>
    public const string DirectoryVariable = "FILEE_DATA_DIR";

    /// <summary>The folder <see cref="DirectoryVariable"/> points to (full path), or null when it is not set.</summary>
    public static string? OverrideDirectory => DirectoryFrom(Environment.GetEnvironmentVariable(DirectoryVariable));

    /// <summary>
    /// Default data directory: %APPDATA%\Filee on Windows, ~/Library/Application Support/Filee on macOS, or the
    /// folder in <see cref="DirectoryVariable"/>.
    /// </summary>
    public static string DefaultDirectory =>
        OverrideDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Filee");

    internal static string? DirectoryFrom(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(value.Trim()));

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

    /// <summary>
    /// Remembers the window zoom (<see cref="AppSettings.UiScale"/>) without raising <see cref="SettingsChanged"/>:
    /// the zoom is already applied, and nothing else changed that would have to be applied again.
    /// </summary>
    public void SaveUiScale(double scale)
    {
        Settings.UiScale = scale;
        Write(SettingsPath, Settings, FileeJsonContext.Default.AppSettings);
    }

    public void SaveLibrary()
    {
        EnsureConsistency();
        Write(PresetsPath, Presets, FileeJsonContext.Default.ListPreset);
        Write(ProfilesPath, Profiles, FileeJsonContext.Default.ListToolbarProfile);
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Records a finished conversion, unless the user turned the history off (<see cref="AppSettings.KeepHistory"/>).</summary>
    public void AddHistory(HistoryEntry entry)
    {
        if (!Settings.KeepHistory)
            return;
        // A lowered limit drops the oldest entries here, with the next conversion.
        var limit = Settings.EffectiveHistoryLimit;
        lock (_saveLock)
        {
            History.Insert(0, entry);
            if (History.Count > limit)
                History.RemoveRange(limit, History.Count - limit);
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

    /// <summary>
    /// Whether <see cref="ResetProfile"/> can put <paramref name="profile"/> back: only a built-in donut has a default
    /// state, and not when the reset would leave no profile for mixed selections (a built-in normal donut the user made
    /// the only mixed one).
    /// </summary>
    public bool CanResetProfile(ToolbarProfile profile) =>
        Profiles.Contains(profile)
        && BuiltInData.CreateProfiles().FirstOrDefault(p => p.Id == profile.Id) is { } builtIn
        && (builtIn.IsFallback || !profile.IsFallback || Profiles.Count(p => p.IsFallback) > 1);

    /// <summary>
    /// Puts one built-in donut back as a new library has it (name, slices of both faces and their order, extensions,
    /// normal or mixed) and leaves the other donuts alone. So no file type opens two donuts, an extension another normal donut takes
    /// now stays there. Presets are shared by every donut: the ones this donut drops stay in the library, edited ones
    /// keep their settings, and a built-in preset of its default slices that the user deleted comes back. A donut that
    /// changes kind moves to the border between the normal profiles and the mixed ones, the built-in mixed donut back
    /// to the head of the mixed ones (the catch-all).
    /// </summary>
    /// <returns>False when the profile can't be reset (<see cref="CanResetProfile"/>).</returns>
    public bool ResetProfile(ToolbarProfile profile)
    {
        if (!CanResetProfile(profile))
            return false;
        var builtIn = BuiltInData.CreateProfiles().Single(p => p.Id == profile.Id);
        RestoreBuiltInPresets([.. builtIn.PresetIds, .. builtIn.BackPresetIds ?? []]);

        // Extensions checked on a mixed-selection profile only limit that profile; they don't claim files.
        var claimed = Profiles.Where(p => p != profile && !p.IsFallback)
            .SelectMany(p => p.Extensions)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        profile.Name = "";
        profile.NameKey = builtIn.NameKey;
        profile.Extensions = builtIn.IsFallback ? builtIn.Extensions : builtIn.Extensions.Where(e => !claimed.Contains(e)).ToList();
        profile.PresetIds = builtIn.PresetIds;
        profile.BackPresetIds = builtIn.BackPresetIds ?? [];
        if (profile.IsFallback != builtIn.IsFallback)
        {
            profile.IsFallback = builtIn.IsFallback;
            Profiles.Remove(profile);
            var index = Profiles.FindIndex(p => p.IsFallback);
            Profiles.Insert(index < 0 ? Profiles.Count : index, profile);
        }
        SaveLibrary();
        return true;
    }

    /// <summary>Adds back the built-in presets among <paramref name="ids"/> that the library no longer has, and those a
    /// built-in group among them fans out, each after the built-in preset before it (as a new library orders them).</summary>
    private void RestoreBuiltInPresets(IEnumerable<string> ids)
    {
        var defaults = BuiltInData.CreatePresets();
        var members = ids.SelectMany(id => defaults.FirstOrDefault(p => p.Id == id) is { IsGroup: true } group ? group.SubPresetIds : []);
        foreach (var id in ids.Concat(members).Distinct().Where(id => FindPreset(id) is null).ToList())
        {
            var at = defaults.FindIndex(p => p.Id == id);
            if (at < 0)
                continue;
            var after = defaults.Take(at).Select(p => Presets.FindIndex(existing => existing.Id == p.Id)).LastOrDefault(i => i >= 0, -1);
            Presets.Insert(after + 1, defaults[at]);
        }
    }

    public Preset? FindPreset(string id) => Presets.FirstOrDefault(p => p.Id == id);

    /// <summary>The preset <paramref name="id"/> when it converts (a group or tool doesn't), for every place that runs one.</summary>
    public Preset? FindConversion(string id) => Presets.FirstOrDefault(p => p.Id == id && !p.IsGroup && !p.IsTool);

    /// <summary>Exports presets and profiles to a shareable JSON file, without PDF passwords.</summary>
    public void Export(string path) =>
        Write(path, new PresetBundle { Presets = [.. Presets.Select(p => p.WithoutPassword(mark: false))], Profiles = Profiles },
            FileeJsonContext.Default.PresetBundle);

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
        // A bundle from a Filee before back faces has none: a built-in donut gets its default one, as on loading.
        SettingsMigrations.AddBackFaces(Presets, bundle.Profiles);
        // Mixed-selection profiles stay mixed: several may exist, and the catch-all is the first one with nothing
        // checked, so an imported one added at the end never replaces ours.
        foreach (var profile in bundle.Profiles)
        {
            Profiles.RemoveAll(p => p.Id == profile.Id);
            Profiles.Add(profile);
        }
        SaveLibrary();
        return bundle.Presets.Count + bundle.Profiles.Count;
    }

    /// <summary>Removes dangling preset references and guarantees at least one mixed-selection profile.</summary>
    private void EnsureConsistency()
    {
        // "security": null in a file edited by hand reads as the defaults rather than failing later.
        Settings.Security ??= new SecuritySettings();
        Settings.Limits ??= new LimitSettings();
        var ids = Presets.Select(p => p.Id).ToHashSet();
        foreach (var profile in Profiles)
        {
            profile.PresetIds = profile.PresetIds.Where(ids.Contains).Distinct().Take(ToolbarProfile.MaxSlices).ToList();
            // No back face known by now (a donut the user made, read from an older file): it has none.
            profile.BackPresetIds = (profile.BackPresetIds ?? []).Where(ids.Contains).Distinct().Take(ToolbarProfile.MaxSlices).ToList();
        }
        // A Filee from before groups drops "kind" (and the presets) when it saves a group, which comes back as a
        // conversion to "group": still a group, now an empty one, rather than a conversion that can never run.
        foreach (var preset in Presets.Where(p => !p.IsGroup && p.TargetFormat == Preset.GroupTarget))
            preset.Kind = PresetKind.Group;
        // A group fans out conversions and tools (but never another group), each once, at most MaxSubPresets, and has no
        // target format an older Filee could run it with.
        var actions = Presets.Where(p => !p.IsGroup).Select(p => p.Id).ToHashSet();
        foreach (var group in Presets.Where(p => p.IsGroup))
        {
            group.SubPresetIds = group.SubPresetIds.Where(actions.Contains).Distinct().Take(Preset.MaxSubPresets).ToList();
            group.TargetFormat = Preset.GroupTarget;
        }

        // Several mixed profiles are fine (each limited to the extensions checked on it); none is not.
        if (!Profiles.Any(p => p.IsFallback))
        {
            var mixed = BuiltInData.CreateProfiles().First(p => p.IsFallback);
            mixed.PresetIds = mixed.PresetIds.Where(ids.Contains).ToList();
            mixed.BackPresetIds = (mixed.BackPresetIds ?? []).Where(ids.Contains).ToList();
            Profiles.Add(mixed);
        }
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
