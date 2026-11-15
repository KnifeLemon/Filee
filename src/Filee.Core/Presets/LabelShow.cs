// What a donut slice shows: its name, its icon, both, or whichever fits (Auto).

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Filee.Core.Presets;

/// <summary>
/// What a donut slice shows (<see cref="Preset.Show"/>, and a donut's default in
/// <see cref="Profiles.ToolbarProfile.LabelShow"/>).
/// </summary>
public enum LabelShow
{
    /// <summary>
    /// Whichever fits, one style for every Auto slice of a ring that has an icon: icon and name if every name fits
    /// whole, else the name if every name fits whole, else the icon. A slice without an icon shows its name.
    /// </summary>
    Auto,

    /// <summary>The icon above the name, the name shortened if it doesn't fit.</summary>
    IconAndText,

    /// <summary>The name, shortened if it doesn't fit.</summary>
    TextOnly,

    /// <summary>The icon, centred; a slice without an icon shows its name.</summary>
    IconOnly,
}

/// <summary>
/// Reads a <see cref="LabelShow"/> by name. A value this version doesn't know (a newer Filee's) reads as unset, so the
/// slice follows its donut's setting instead of the preset file failing to read.
/// </summary>
public sealed class LabelShowJsonConverter : JsonConverter<LabelShow?>
{
    public override bool HandleNull => true;

    public override LabelShow? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            reader.Skip();
            return null;
        }
        if (reader.TokenType != JsonTokenType.String)
            return null;
        var text = reader.GetString();
        return Enum.GetNames<LabelShow>().FirstOrDefault(n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase)) is { } name
            ? Enum.Parse<LabelShow>(name)
            : null;
    }

    public override void Write(Utf8JsonWriter writer, LabelShow? value, JsonSerializerOptions options)
    {
        if (value is { } show)
            writer.WriteStringValue(show.ToString());
        else
            writer.WriteNullValue();
    }
}
