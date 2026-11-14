// Changing a folder's icon: the picture becomes an .ico inside the folder itself, the folder's desktop.ini points at
// it, and the shell is told when the conversion has finished.
//
// Windows reads a folder's icon from its desktop.ini ([.ShellClassInfo] IconResource=<name>,0): the .ico sits inside
// the folder, so the icon stays when the folder moves or is copied, and the record survives (the relative name is
// resolved from the copy, not from the original). Both the folder and its desktop.ini carry system attributes
// (the .ini hidden too), or Explorer would keep drawing the plain folder. Nothing opens on macOS and Linux: the
// tool's slice is greyed out there (PresetAvailability), so nothing here has a platform to speak of.

using System.Text;
using Filee.Core.Platform;

namespace Filee.Core.Presets;

/// <summary>Everything about giving folders an icon: candidates, the ring's presets, the desktop.ini record.</summary>
public static class FolderIcons
{
    /// <summary>Id of the built-in tool preset (Preset.Tool "folder-icon"; settings schema 75).</summary>
    public const string ToolId = "tool-folder-icon";

    /// <summary>
    /// Prefix of the per-picture presets the tool's ring shows ("the icon is inside the folder"), never saved in the
    /// library. The suffix is the picture's path, so "folder-icon:C:\...\logo.png" carries it through the donut.
    /// </summary>
    public const string ChildIdPrefix = "folder-icon:";

    /// <summary>The "Browse…" entry at the end of that ring; its preset is thrown away with the face.</summary>
    public const string BrowseChildId = ChildIdPrefix + "browse";

    /// <summary>Extensions of the pictures a folder may offer as its icon, .ico included.</summary>
    public static readonly string[] IconExtensions = [".ico", ".png", ".jpg", ".jpeg", ".webp", ".svg"];

    /// <summary>The picture files the folder itself holds that could be its icon, sorted.</summary>
    public static IReadOnlyList<string> IconCandidatesOf(string folder) =>
    [
        .. Directory.EnumerateFiles(folder)
            .Where(path => IconExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.CurrentCultureIgnoreCase),
    ];

    /// <summary>The folders the donut's files bring, in the order they were given, each once.</summary>
    public static IReadOnlyList<string> FoldersIn(IReadOnlyList<string> files) =>
        [.. files.Where(Directory.Exists).Distinct(FileSystemPaths.Comparer)];

    /// <summary>
    /// A preset for the ring's picture: converts it into the .ico that goes into <paramref name="folder"/>. Not saved
    /// in the library, and thrown away with the face like any other ring preset. The target is the folder itself
    /// (Preset.IsFolderIconTool's ring entry, issue #806's schema 75).
    /// </summary>
    public static Preset ChildFor(string folder, string picture) => new()
    {
        Id = ChildIdPrefix + picture,
        Name = Path.GetFileName(picture),
        TargetFormat = "ico",
        FolderIconPath = picture,
        Output =
        {
            Location = OutputLocation.CustomFolder,
            CustomFolder = folder,
            Conflict = ConflictPolicy.Overwrite,
            // The .ico of the folder carries one name, so a record survives and the next pick replaces it.
            FileNamePattern = "icon",
        },
    };

    /// <summary>The preset of the ring's "Browse…" entry: nothing is converted; its use opens the file picker.</summary>
    public static Preset BrowseChild() => new()
    {
        Id = BrowseChildId,
        NameKey = "donut.folder_icon_browse",
        TargetFormat = BuiltInData.SameAsSource,
        Kind = PresetKind.Tool,
        Tool = Preset.FolderIconTool,
    };

    /// <summary>
    /// Records the folder's icon in its desktop.ini and gives the folder the system bit it needs for the record to
    /// bite. <paramref name="reference"/> is the icon's name when it sits in the folder (resolved from there, so the
    /// record survives the folder moving) or its absolute path when it lives elsewhere — both carry ",0". The .ini is
    /// written UTF-16 (with a BOM), as Explorer's own folder desktop.inis are, and carries only what it needs, so a
    /// rewrite over the last one, or over a desktop.ini another app keeps, loses nothing else.
    /// </summary>
    public static void RecordIcon(string folder, string reference)
    {
        var text = DesktopIni(reference);
        if (!OperatingSystem.IsWindows())
        {
            // Nothing consumes a desktop.ini on macOS or Linux; the tool is greyed out there, so this is a stray call
            // rather than a record to keep: written anyway, harmless.
            File.WriteAllText(Path.Combine(folder, "desktop.ini"), text, Encoding.Unicode);
            return;
        }
        var ini = Path.Combine(folder, "desktop.ini");
        var current = File.Exists(ini) ? File.ReadAllText(ini, Encoding.Unicode) : null;
        if (current is not { } had || had != text)
        {
            // Away with the hidden and system bits for the write itself: Windows denies writes to a hidden file, and a
            // folder that already has an icon carries exactly such a desktop.ini. They come back right after.
            var attributes = File.Exists(ini) ? File.GetAttributes(ini) : FileAttributes.Normal;
            if (File.Exists(ini))
                File.SetAttributes(ini, attributes & ~(FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly));
            File.WriteAllText(ini, text, Encoding.Unicode);
            File.SetAttributes(ini, (attributes & ~FileAttributes.ReadOnly) | FileAttributes.Hidden | FileAttributes.System);
        }
        if ((File.GetAttributes(folder) & FileAttributes.System) == 0)
            File.SetAttributes(folder, File.GetAttributes(folder) | FileAttributes.System);
    }

    /// <summary>The record a folder's desktop.ini carries: an in-folder icon by name, an outside one by path, index 0.</summary>
    /// <remarks>The section header is "[.ShellClassInfo]" with the leading dot: Explorer honours only the dotted
    /// header (lab-verified on this machine, 2026-11-11 — "[ShellClassInfo]" without the dot is ignored and the
    /// folder keeps its plain icon).</remarks>
    private static string DesktopIni(string reference) => "[.ShellClassInfo]\r\nIconResource=" + reference + ",0\r\n";
}
