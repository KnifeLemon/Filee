// Files a document refers to (pictures, style sheets), the one rule for every reader: a reference is read only from
// inside the document's own folder and its subfolders, so a received HTML, Markdown or Pandoc document can't copy other
// pictures on the PC into the converted file. Absolute paths, ../ and file: URLs outside are left out like missing
// files. A path or file: URL that names another computer (\\host\share, file://host/, //host/, \\?\UNC\) is refused
// before anything looks at it: even File.Exists on it makes Windows connect to that computer and offer the user's
// sign-in.

using System.Text.RegularExpressions;

namespace Filee.Engines.Infrastructure;

internal static partial class DocumentFiles
{
    /// <summary>
    /// The existing local file <paramref name="reference"/> names (a relative or absolute path, or a file: URL), or null
    /// for remote and missing files, files on another computer and files outside every one of <paramref name="roots"/>.
    /// </summary>
    /// <param name="baseFolder">Folder relative references are resolved against (the document's folder).</param>
    /// <param name="roots">Folders the file must be inside; none means <paramref name="baseFolder"/>.</param>
    public static string? Find(string? reference, string baseFolder, params string[] roots)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return null;
        reference = reference.Trim();
        try
        {
            string path;
            if (reference.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(reference, UriKind.Absolute, out var uri) || !uri.IsFile || uri.IsUnc || uri.Host.Length > 0)
                    return null;
                path = uri.LocalPath;
            }
            else if (OtherScheme().IsMatch(reference))
            {
                return null; // http:, data:, mailto:, … (one letter before the colon is a drive)
            }
            else
            {
                path = Uri.UnescapeDataString(reference);
            }
            if (OnOtherComputer(path))
                return null;
            // A document on a network share may still use the pictures beside it: the roots are its own folders.
            var candidate = Path.GetFullPath(Path.Combine(baseFolder, path));
            if (!(roots.Length == 0 ? Inside(candidate, baseFolder) : roots.Any(root => Inside(candidate, root))))
                return null;
            return File.Exists(candidate) ? candidate : null;
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> starts with two slashes (\\host\share, //host/share, \\?\UNC\…, \\.\…): a
    /// network or device path. Only looks at the text.
    /// </summary>
    public static bool OnOtherComputer(string path) =>
        path.Length >= 2 && path[0] is '\\' or '/' && path[1] is '\\' or '/';

    /// <summary>True when <paramref name="path"/> is inside <paramref name="root"/> (or a subfolder). Only looks at the text.</summary>
    public static bool Inside(string path, string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return Path.GetFullPath(path).StartsWith(root, comparison);
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9+.-]+:")]
    private static partial Regex OtherScheme();
}
