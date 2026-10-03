// Shared context for engines: where Filee's own engine copies live and where engines keep their state.
// Built-in engines never use software installed on the system (Microsoft Office, Hancom Office). The optional
// engines (FFmpeg, calibre, Pandoc, Ghostscript, LibreOffice) are looked up in this order: a copy the user chose in
// Settings → Engines (OwnCopies), Filee's own copy (installer or download, the tested version), then one already on
// the PC (on the PATH or where its installer puts it), so nobody has to download a second FFmpeg they already have.

using System.Collections.Concurrent;

namespace Filee.Engines.Infrastructure;

/// <summary>Environment information passed to every engine.</summary>
/// <param name="dataDirectory">Filee's writable data directory (profiles, caches).</param>
public sealed class EngineEnvironment(string dataDirectory)
{
    /// <summary>Writable directory for engine state (e.g. LibreOffice profiles).</summary>
    public string DataDirectory { get; } = dataDirectory;

    /// <summary>
    /// Per-user folder for engines downloaded on demand (see <see cref="EngineInstaller"/>):
    /// %LOCALAPPDATA%\Filee\engines. It is outside the app folder, so app updates keep it, and inside Filee's
    /// install root, so uninstalling removes it.
    /// </summary>
    public static string DownloadRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Filee", "engines");

    /// <summary>
    /// Finds an engine directory (<c>engines/&lt;name&gt;</c>): next to the executable (bundled with the installer),
    /// in <see cref="DownloadRoot"/> (downloaded later), then in the Filee repository checkout the app runs from
    /// (a folder with Filee.slnx), so a developer running from <c>bin/Debug</c> uses the engines fetched there.
    /// </summary>
    public static string? FindBundled(string name)
    {
        var local = Path.Combine(AppContext.BaseDirectory, "engines", name);
        if (Directory.Exists(local))
            return local;
        var downloaded = Path.Combine(DownloadRoot, name);
        if (Directory.Exists(downloaded))
            return downloaded;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; dir is not null && depth < 8; depth++, dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "Filee.slnx")))
                continue;
            var candidate = Path.Combine(dir.FullName, "engines", name);
            return Directory.Exists(candidate) ? candidate : null;
        }
        return null;
    }

    /// <summary>
    /// Programs the user chose for an optional engine package instead of Filee's download: package id → the program
    /// they picked (or its folder). Set by the app and the command line from the settings.
    /// </summary>
    public static IReadOnlyDictionary<string, string> OwnCopies { get; set; } = new Dictionary<string, string>();

    /// <summary>Programs that must be in the folder of an own copy, per package (Windows names).</summary>
    public static IReadOnlyDictionary<string, string[]> OwnCopyPrograms { get; } = new Dictionary<string, string[]>
    {
        ["libreoffice"] = ["soffice.exe"],
        ["pandoc"] = ["pandoc.exe"],
        ["ghostscript"] = ["gswin64c.exe"],
        ["ffmpeg"] = ["ffmpeg.exe", "ffprobe.exe"],
        ["calibre"] = ["ebook-convert.exe"],
    };

    /// <summary>The folder of the user's own copy of package <paramref name="id"/>, when it has every program.</summary>
    public static string? OwnCopyFolder(string id) =>
        OwnCopies.TryGetValue(id, out var path) ? FolderWithPrograms(id, path) : null;

    /// <summary>A program of the user's own copy of package <paramref name="id"/>, or null.</summary>
    public static string? OwnProgram(string id, string program) =>
        OwnCopyFolder(id) is { } folder ? Path.Combine(folder, program) : null;

    /// <summary>
    /// The folder with all programs of package <paramref name="id"/>, given a program the user picked or a folder:
    /// that folder itself, or its bin\ or program\ sub folder (FFmpeg builds, LibreOffice). Null when they are missing.
    /// </summary>
    public static string? FolderWithPrograms(string id, string path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path) || !OwnCopyPrograms.TryGetValue(id, out var programs))
            return null;
        var start = File.Exists(path) ? Path.GetDirectoryName(path) : Directory.Exists(path) ? path : null;
        if (start is null)
            return null;
        foreach (var folder in new[] { start, Path.Combine(start, "bin"), Path.Combine(start, "program") })
            if (programs.All(p => File.Exists(Path.Combine(folder, p))))
                return folder;
        return null;
    }

    /// <summary>True when package <paramref name="id"/> runs from Filee's own copy (not the user's or the PC's).</summary>
    public static bool UsesFileesCopy(string id) => OwnCopyFolder(id) is null && FindBundled(id) is not null;

    /// <summary>Searching the PC for engines; tests turn it off so the machine's tools don't change results.</summary>
    public static bool SearchSystem { get; set; } = true;

    private static readonly ConcurrentDictionary<string, string?> SystemCopies = new();

    /// <summary>
    /// The folder of a copy of package <paramref name="id"/> already on the PC: on the PATH (also the user's and the
    /// machine's PATH as saved, so a tool installed after Filee started is found, e.g. Scoop's shims) or where its
    /// installer puts it (LibreOffice, calibre, Ghostscript under Program Files). Cached until
    /// <see cref="ForgetSystemCopies"/>.
    /// </summary>
    public static string? SystemCopyFolder(string id) =>
        SearchSystem && OperatingSystem.IsWindows() ? SystemCopies.GetOrAdd(id, FindOnSystem) : null;

    /// <summary>A program of the copy of package <paramref name="id"/> on the PC, or null.</summary>
    public static string? SystemProgram(string id, string program) =>
        SystemCopyFolder(id) is { } folder ? Path.Combine(folder, program) : null;

    /// <summary>Searches the PC again (Settings → Engines → Check again, settings changes).</summary>
    public static void ForgetSystemCopies() => SystemCopies.Clear();

    private static string? FindOnSystem(string id) =>
        SystemFolders(id).Select(folder => FolderWithPrograms(id, folder)).FirstOrDefault(found => found is not null);

    private static IEnumerable<string> SystemFolders(string id)
    {
        var path = string.Join(';',
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
        var folders = path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => Environment.ExpandEnvironmentVariables(entry.Trim('"')))
            .ToList();
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
                     .Select(Environment.GetFolderPath).Where(r => r.Length > 0))
        {
            switch (id)
            {
                case "libreoffice":
                    folders.Add(Path.Combine(root, "LibreOffice", "program"));
                    break;
                case "calibre":
                    folders.Add(Path.Combine(root, "Calibre2"));
                    break;
                case "ghostscript":
                    // C:\Program Files\gs\gs10.04.0\bin, newest first
                    try
                    {
                        var gs = Path.Combine(root, "gs");
                        if (Directory.Exists(gs))
                            folders.AddRange(Directory.EnumerateDirectories(gs).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                                .Select(d => Path.Combine(d, "bin")));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                    break;
            }
        }
        // Never Filee's own folders: those are its own copy, found before.
        return folders.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(f => !f.StartsWith(DownloadRoot, StringComparison.OrdinalIgnoreCase)
                        && !f.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Returns the first path that exists as a file.</summary>
    public static string? FirstExisting(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && File.Exists(c));
}
