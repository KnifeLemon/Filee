// "Ask every time" (#521): what the save dialog suggests, and the preset as it runs once the user has answered it.

using Filee.Core.Formats;

namespace Filee.Core.Presets;

public static class AskedLocation
{
    /// <summary>
    /// The batch makes one file, so the dialog asks for its name: one source, or several that become one (a merged
    /// PDF, a multi-page TIFF, one archive, joined videos, merged pictures). Otherwise it asks for a folder.
    /// </summary>
    public static bool MakesOneResult(Preset preset, int sources) =>
        sources == 1
        || preset.TargetFormat == "pdf" && preset.Pdf.MergeIntoSingle
        || preset.Image.MultiPageTiff && preset.TargetFormat == "tiff"
        || preset.PacksIntoOneArchive
        || preset.JoinsIntoOneMedia
        || preset.JoinsImages;

    /// <summary>
    /// The name the save dialog suggests for <paramref name="source"/>: the preset's name pattern and renames, and the
    /// extension the result gets.
    /// </summary>
    public static string SuggestedName(Preset preset, string presetName, string source)
    {
        var tokens = new OutputPathResolver.Tokens(source, presetName, 1, DateTime.Now);
        var name = OutputPathResolver.Sanitize(
            OutputPathResolver.Rename(OutputPathResolver.ExpandPattern(preset.Output.FileNamePattern, tokens), preset.Output.Renames),
            FormatRegistry.NameWithoutExtension(source));
        var extension = Extension(preset, source);
        return extension.Length == 0 ? name : $"{name}.{extension}";
    }

    /// <summary>
    /// The extension the result gets, without the dot: "zip" when it's saved as a ZIP, the source's own for "same as
    /// source", empty for a folder ("Extract"). The preset is the one that runs, with Settings › General applied.
    /// </summary>
    public static string Extension(Preset preset, string source) =>
        preset.Output.ZipResult ? "zip"
        : preset.TargetFormat == BuiltInData.SameAsSource ? FormatRegistry.ExtensionOf(source)
        : FormatRegistry.FindById(preset.TargetFormat)?.Extensions.FirstOrDefault() ?? "";

    /// <summary>
    /// One result, saved as <paramref name="chosenPath"/>: that folder and that name, without the pattern or renames
    /// on top. Only that exact path, which the dialog asked about, may replace a file there; pages or a result with
    /// another extension keep the preset's own conflict policy.
    /// </summary>
    public static Preset ForFile(Preset preset, string chosenPath)
    {
        var copy = InFolder(preset, Path.GetDirectoryName(Path.GetFullPath(chosenPath))!);
        // shortcut: a chosen name with a pattern token in it ("{date}") is expanded, there is no escape; rare enough.
        copy.Output.FileNamePattern = Path.GetFileNameWithoutExtension(chosenPath);
        copy.Output.Renames = [];
        copy.Output.ConfirmedPath = Path.GetFullPath(chosenPath);
        return copy;
    }

    /// <summary>Several results, all saved in <paramref name="folder"/> under the preset's own names.</summary>
    public static Preset ForFolder(Preset preset, string folder) => InFolder(preset, folder);

    private static Preset InFolder(Preset preset, string folder)
    {
        var copy = preset.Clone();
        copy.Output.AskEveryTime = false;
        copy.Output.Location = OutputLocation.CustomFolder;
        copy.Output.CustomFolder = folder;
        copy.Output.KeepFoldersFrom = "";
        copy.Output.SubfolderOnlyForSeveralFiles = false;
        // The place the user picked is where the result goes, not back over the original.
        copy.Output.ReplaceOriginal = false;
        return copy;
    }
}
