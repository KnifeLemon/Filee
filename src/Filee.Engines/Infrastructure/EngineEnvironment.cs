// Shared context for engines: where Filee's own engine copies live and where engines keep their state.
// Engines never use software installed on the system (Office, a system-wide LibreOffice, tools on the PATH): only
// the copies that ship with the installer or that Filee downloaded, so every user gets the same, tested versions.

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

    /// <summary>Returns the first path that exists as a file.</summary>
    public static string? FirstExisting(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && File.Exists(c));
}
