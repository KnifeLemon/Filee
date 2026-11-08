// "Metadata" runs as an ordinary conversion once its window has the changes: the tool preset becomes a conversion into
// the file's own format carrying them (Preset.Metadata), so the job queue, the toast, cancelling and the output rules
// (where the copy goes, its name, "Replace the original") work as for any other file. Only an engine that writes
// metadata and nothing else takes it (ConversionEdge.Metadata): the picture, sound or document is never re-encoded.

namespace Filee.Core.Presets;

public static class MetadataTool
{
    /// <summary>
    /// The conversion that writes <paramref name="edits"/> into a copy of the file, or in its place when
    /// <paramref name="replaceOriginal"/> (the original to the Recycle Bin / Trash,
    /// <see cref="OutputRule.ReplaceOriginal"/>), with the tool's name and output rule. The tool itself is left as it
    /// is.
    /// </summary>
    public static Preset ConversionOf(Preset tool, MetadataEdits edits, bool replaceOriginal)
    {
        var preset = tool.Clone();
        preset.Kind = PresetKind.Convert;
        preset.Tool = null;
        preset.SubPresetIds = [];
        preset.TargetFormat = BuiltInData.SameAsSource;
        preset.Metadata = edits.Clone();
        preset.Output.ReplaceOriginal = replaceOriginal;
        // A copy with only its metadata changed is about as large as the file: "Only if smaller" would keep the
        // original.
        preset.Output.OnlyIfSmaller = false;
        return preset;
    }

    /// <summary>Whether the window starts with "Replace the original" ticked: the tool's own output rule.</summary>
    public static bool ReplacesOriginal(Preset tool) => tool.Output.ReplaceOriginal;

    /// <summary>Keeps "Replace the original" in <paramref name="tool"/> for the next time.</summary>
    public static void Remember(Preset tool, bool replaceOriginal) => tool.Output.ReplaceOriginal = replaceOriginal;
}
