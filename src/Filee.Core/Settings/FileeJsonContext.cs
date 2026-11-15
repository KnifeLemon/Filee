// System.Text.Json source generation for everything Filee persists. Reflection-free and trim-safe.

using System.Text.Json.Serialization;
using Filee.Core.History;
using Filee.Core.Presets;
using Filee.Core.Profiles;

namespace Filee.Core.Settings;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(ThemeStyle))]
[JsonSerializable(typeof(List<Preset>))]
[JsonSerializable(typeof(TranslateOptions))]
[JsonSerializable(typeof(List<ToolbarProfile>))]
[JsonSerializable(typeof(PresetBundle))]
[JsonSerializable(typeof(List<HistoryEntry>))]
[JsonSerializable(typeof(CustomTheme))]
internal sealed partial class FileeJsonContext : JsonSerializerContext;

/// <summary>File format used to import / export presets and profiles.</summary>
public sealed class PresetBundle
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// The settings schema (<see cref="AppSettings.CurrentSchemaVersion"/>) of the Filee that wrote the bundle; null in
    /// bundles from before it was written. Tells a later Filee which migrations a bundle's presets still need, and the
    /// preset gallery's check that a bundle doesn't come from a newer Filee than the one checking it.
    /// </summary>
    public int? SettingsSchema { get; set; }

    /// <summary>
    /// The app language the bundle was made in ("ko"), written by "Export for the gallery": the language its names are
    /// in, which the gallery's submission takes as the preset's main language. Null in other exports.
    /// </summary>
    public string? Language { get; set; }
    public List<Preset> Presets { get; set; } = [];
    public List<ToolbarProfile> Profiles { get; set; } = [];
}
