// Which way a donut slice's name runs: whichever reads best (Automatic), or one the preset asks for.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Filee.Core.Presets;

/// <summary>Which way a donut slice's name runs (<see cref="Preset.Direction"/>).</summary>
public enum LabelDirection
{
    /// <summary>
    /// Whichever reads best for the slice's shape: straight, or along its longer side (out from the centre on a deep
    /// slice, bent along the ring on a wide one).
    /// </summary>
    Auto,

    /// <summary>Level, as on a page.</summary>
    Straight,

    /// <summary>From the centre outward on the right half, inward on the left half.</summary>
    OutFromCentre,

    /// <summary>Bent along the ring, never upside down.</summary>
    AlongRing,
}

/// <summary>
/// Reads a <see cref="LabelDirection"/> by name. A value this version doesn't know (a newer Filee's) reads as unset, so
/// the slice picks its own direction instead of the preset file failing to read.
/// </summary>
public sealed class LabelDirectionJsonConverter : JsonConverter<LabelDirection?>
{
    public override bool HandleNull => true;

    public override LabelDirection? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            reader.Skip();
            return null;
        }
        if (reader.TokenType != JsonTokenType.String)
            return null;
        var text = reader.GetString();
        return Enum.GetNames<LabelDirection>().FirstOrDefault(n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase)) is { } name
            ? Enum.Parse<LabelDirection>(name)
            : null;
    }

    public override void Write(Utf8JsonWriter writer, LabelDirection? value, JsonSerializerOptions options)
    {
        if (value is { } direction)
            writer.WriteStringValue(direction.ToString());
        else
            writer.WriteNullValue();
    }
}
