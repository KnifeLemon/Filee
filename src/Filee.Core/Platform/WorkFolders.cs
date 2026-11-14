// Where Filee keeps the files it works with: every job's folder (JobQueue) and the files dragged from a browser
// (WebDrop). On Windows that is %TEMP%\Filee, and %TEMP% is the user's own. On Linux the temp folder is /tmp, which
// every user shares: someone else could make /tmp/Filee first and then read or swap the files a conversion leaves in
// it. So on Linux and macOS the work files go into the user's cache folder, which only they can open.

using System.Runtime.Versioning;

namespace Filee.Core.Platform;

public static class WorkFolders
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private static readonly Lazy<string> RootFolder = new(FindRoot);

    /// <summary>
    /// %TEMP%\Filee on Windows; on Linux $XDG_CACHE_HOME/filee (~/.cache/filee), on macOS ~/Library/Caches/Filee,
    /// made with mode 700 the first time it is asked for.
    /// </summary>
    public static string Root => RootFolder.Value;

    private static string FindRoot()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Path.GetTempPath(), "Filee");

        var root = UnixRoot(Environment.GetEnvironmentVariable("XDG_CACHE_HOME"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), OperatingSystem.IsMacOS());
        if (root is not null)
        {
            try
            {
                MakePrivate(root);
                return root;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A home that can't be written in (or a folder of someone else's): see below.
            }
        }
        // A folder for this run only, which mkdtemp makes under a name nobody can guess, with mode 700. Its leftovers
        // aren't swept at the next start; the system empties its temp folder.
        try
        {
            return Directory.CreateTempSubdirectory("Filee-").FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nowhere to write: the jobs fail when they make their folders, with the reason.
            return root ?? Path.Combine(Path.GetTempPath(), "Filee-" + Guid.NewGuid().ToString("N"));
        }
    }

    /// <summary>The work root on Linux or macOS, or null without a home folder.</summary>
    internal static string? UnixRoot(string? cacheHome, string? home, bool macOS)
    {
        // The XDG spec ignores a relative XDG_CACHE_HOME.
        if (!macOS && Path.IsPathFullyQualified(cacheHome ?? ""))
            return Path.Combine(cacheHome!, "filee");
        if (!Path.IsPathFullyQualified(home ?? ""))
            return null;
        return macOS ? Path.Combine(home!, "Library", "Caches", "Filee") : Path.Combine(home!, ".cache", "filee");
    }

    /// <summary>
    /// Makes <paramref name="folder"/> (and the folders above it that are missing) with mode 700, and takes the rights
    /// of others away from a folder that was there already. That fails for a folder another user owns, which is then
    /// not used.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    internal static void MakePrivate(string folder)
    {
        Directory.CreateDirectory(folder, OwnerOnly);
        if ((File.GetUnixFileMode(folder) & ~OwnerOnly) != 0)
            File.SetUnixFileMode(folder, OwnerOnly);
    }
}
