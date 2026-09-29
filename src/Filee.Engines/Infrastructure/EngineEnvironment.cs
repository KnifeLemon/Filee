// Shared context for engines: where bundled tools live and which custom paths the user configured.

namespace Filee.Engines.Infrastructure;

/// <summary>Environment information passed to every engine.</summary>
public sealed class EngineEnvironment
{
    /// <param name="customPath">Returns a user-configured executable path for an engine id, or null.</param>
    /// <param name="dataDirectory">Filee's writable data directory (profiles, caches).</param>
    public EngineEnvironment(Func<string, string?> customPath, string dataDirectory)
    {
        CustomPath = customPath;
        DataDirectory = dataDirectory;
    }

    /// <summary>User override for an engine executable (engine id → path).</summary>
    public Func<string, string?> CustomPath { get; }

    /// <summary>Writable directory for engine state (e.g. LibreOffice profiles).</summary>
    public string DataDirectory { get; }

    /// <summary>
    /// Per-user folder for engines downloaded on demand (see <see cref="EngineInstaller"/>):
    /// %LOCALAPPDATA%\Filee\engines. It is outside the app folder, so app updates keep it, and inside Filee's
    /// install root, so uninstalling removes it.
    /// </summary>
    public static string DownloadRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Filee", "engines");

    /// <summary>
    /// Finds an engine directory (<c>engines/&lt;name&gt;</c>): next to the executable (bundled with the installer),
    /// in <see cref="DownloadRoot"/> (downloaded later), then up the directory tree so a developer running from
    /// <c>bin/Debug</c> can use engines fetched into the repository root.
    /// </summary>
    public static string? FindBundled(string name)
    {
        var downloaded = Path.Combine(DownloadRoot, name);
        var local = Path.Combine(AppContext.BaseDirectory, "engines", name);
        if (Directory.Exists(local))
            return local;
        if (Directory.Exists(downloaded))
            return downloaded;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; dir is not null && depth < 8; depth++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "engines", name);
            if (Directory.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>Searches the PATH for an executable.</summary>
    public static string? FindOnPath(string executable)
    {
        var names = OperatingSystem.IsWindows() && !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? new[] { executable + ".exe", executable }
            : [executable];

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            foreach (var name in names)
            {
                try
                {
                    var full = Path.Combine(dir.Trim('"'), name);
                    if (File.Exists(full))
                        return full;
                }
                catch (ArgumentException) { /* malformed PATH entry */ }
            }
        }
        return null;
    }

    /// <summary>Returns the first path that exists as a file.</summary>
    public static string? FirstExisting(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && File.Exists(c));
}
