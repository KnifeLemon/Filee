// The warnings of a shared preset (#1530): settings the gallery allows that touch the installer's files (replacing or
// moving originals, overwriting files, writing or removing metadata, a fixed AI model, edits drawn into every file). The
// check derives them from preset.json; the gallery's CI copies the codes into meta.json and index.json for the site,
// and the app derives them again from the file it installs, never trusting the index. The codes are a contract with
// the site and the gallery's repository: add new ones, never rename one.

using Filee.Core.Presets;

namespace Filee.Core.Gallery;

public static class SharedPresetWarnings
{
    public const string High = "high", Medium = "medium", Info = "info";

    /// <summary>Every warning code, with its severity and what it means, most serious first.</summary>
    public static readonly IReadOnlyList<(string Code, string Severity, string Message)> All =
    [
        ("replaces_original", High, "Replaces the original files: the result takes their place and the originals go to the Recycle Bin / Trash."),
        ("moves_original", High, "Moves the original files into a folder instead of copying them."),
        ("overwrites_existing", Medium, "Overwrites a file that already has the result's name instead of adding a number."),
        ("copies_to_folder", Medium, "Copies files into a folder; you choose the folder after installing."),
        ("edits_metadata", Medium, "Writes metadata fields into the files it converts."),
        ("fixed_edits", Medium, "Draws the same marks, hidden areas, background or clip into every file it converts."),
        ("strips_metadata", Info, "Removes metadata (such as camera details, dates or location) from the files it converts."),
        ("uses_translate_model", Info, "Translates with a particular AI model, which you may need to download in Ollama."),
    ];

    /// <summary>The severity of <paramref name="code"/>; null for a code this version doesn't know.</summary>
    public static string? SeverityOf(string code) => All.FirstOrDefault(w => w.Code == code).Severity;

    /// <summary>The warnings <paramref name="presets"/> give, each with the ids of the presets that have it.</summary>
    public static IReadOnlyList<PresetCheckWarning> Of(IEnumerable<Preset> presets)
    {
        var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var preset in presets)
            foreach (var code in Codes(preset))
            {
                if (!found.TryGetValue(code, out var ids))
                    found[code] = ids = [];
                ids.Add(preset.Id);
            }
        return [.. All.Where(w => found.ContainsKey(w.Code)).Select(w => new PresetCheckWarning(w.Code, w.Severity, w.Message, found[w.Code]))];
    }

    /// <summary>The warning codes of one preset.</summary>
    public static IEnumerable<string> Codes(Preset preset)
    {
        if (preset.IsGroup)
            yield break;
        var output = preset.Output;
        if (output.ReplaceOriginal)
            yield return "replaces_original";
        if (preset.IsCopyToFolder && output.MoveInsteadOfCopy)
            yield return "moves_original";
        if (output.Conflict == ConflictPolicy.Overwrite)
            yield return "overwrites_existing";
        if (preset.IsCopyToFolder)
            yield return "copies_to_folder";
        if (preset.Metadata is { Set.Count: > 0 })
            yield return "edits_metadata";
        if (preset.Image.HasAnnotations || preset.Image.HidesAreas || preset.Image.AddsBackdrop || preset.Media.Clip is { Parts.Count: > 0 })
            yield return "fixed_edits";
        if (!preset.Image.KeepMetadata || preset.Pdf.RemoveMetadata || preset.Media.RemoveMetadata
            || preset.Metadata is { Remove.Count: > 0 } or { RemoveLocation: true } or { RemoveAll: true })
            yield return "strips_metadata";
        if (preset.Translate.Enabled && !string.IsNullOrWhiteSpace(preset.Translate.Model))
            yield return "uses_translate_model";
    }
}
