// Minecraft Bedrock packs (.mcpack), add-ons (.mcaddon), worlds (.mcworld) and world templates (.mctemplate), and
// Modrinth modpacks (.mrpack), are ZIP archives under their own extensions (issues #824, #898). Minecraft and Modrinth
// look for a marker file at the top of the ZIP, so a pack is written with it there: when the files came in one folder
// that holds it (a pack zipped as its folder), that folder's content goes in instead of the folder.

namespace Filee.Engines.Archives;

internal static class GamePacks
{
    /// <summary>The pack formats, all written as ZIP.</summary>
    public static readonly IReadOnlySet<string> Formats = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "mcpack", "mcaddon", "mcworld", "mctemplate", "mrpack",
    };

    /// <summary>
    /// The file Minecraft or Modrinth looks for at the top of <paramref name="format"/>, or null for an add-on (a ZIP of
    /// several packs, with nothing of its own at the top).
    /// </summary>
    public static string? Marker(string format) => format switch
    {
        "mcpack" => "manifest.json",
        "mcworld" or "mctemplate" => "level.dat",
        "mrpack" => "modrinth.index.json",
        _ => null,
    };

    /// <summary>
    /// The folder whose content goes at the top of a <paramref name="format"/> pack: <paramref name="folder"/>, or its
    /// only subfolder when that holds the marker and <paramref name="folder"/> doesn't.
    /// </summary>
    public static string Root(string folder, string format)
    {
        if (Marker(format) is not { } marker || File.Exists(Path.Combine(folder, marker)))
            return folder;
        var inner = ArchiveFiles.ContentRoot(folder);
        return inner != folder && File.Exists(Path.Combine(inner, marker)) ? inner : folder;
    }
}
