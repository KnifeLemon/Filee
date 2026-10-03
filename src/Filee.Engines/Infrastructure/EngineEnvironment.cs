// Shared context for engines: where Filee's own engine copies live and where engines keep their state.
// Engines never look for software installed on the system (Office, a system-wide LibreOffice, tools on the PATH):
// they use the copies that ship with the installer or that Filee downloaded, so every user gets the same, tested
// versions. The one exception is a copy the user points Filee to in Settings → Engines (OwnCopies), so nobody has
// to download a second FFmpeg or calibre they already have.

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

    /// <summary>Returns the first path that exists as a file.</summary>
    public static string? FirstExisting(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && File.Exists(c));
}
