// Puts the unpacked files into the install folder as one step that can be undone. The new files were unpacked and
// checked in a work folder inside the install folder (same volume, so every move is a rename). Each file that is
// replaced or no longer needed is first renamed into the work folder's backup: Windows lets a program in use, or a DLL
// Explorer has loaded (FileeExplorerMenu.dll), be renamed, just not overwritten or deleted. On failure everything is
// moved back; on success the backup is deleted, and what is still in use goes at the next restart.

using Filee.Setup.Platform;

namespace Filee.Setup.Installation;

public sealed class FileSwap(string installFolder, string workFolder, ISetupSystem system, SetupLog log)
{
    /// <summary>Prefix of the work folders inside the install folder. Left-overs of an interrupted Setup are removed.</summary>
    public const string WorkFolderPrefix = "~setup-";

    private readonly List<(string Target, string? Backup)> _done = [];
    private readonly string _backup = Path.Combine(workFolder, "old");
    private int _backups;

    /// <summary>Where the new files are unpacked.</summary>
    public string Staging { get; } = Path.Combine(workFolder, "new");

    /// <summary>Moves the staged file <paramref name="relative"/> into place, keeping the file it replaces.</summary>
    public void Replace(string relative)
    {
        var target = Path.Combine(installFolder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var backup = File.Exists(target) ? MoveToBackup(target) : null;
        _done.Add((target, backup));
        File.Move(Path.Combine(Staging, relative), target);
    }

    /// <summary>Moves an old file out of the install folder (deleted on <see cref="Commit"/>).</summary>
    public void Remove(string relative)
    {
        var target = Path.Combine(installFolder, relative);
        if (!File.Exists(target))
            return;
        _done.Add((target, MoveToBackup(target)));
    }

    private string MoveToBackup(string target)
    {
        // A flat, numbered backup: no folder structure to recreate, and names can't collide.
        Directory.CreateDirectory(_backup);
        var backup = Path.Combine(_backup, $"{_backups++:D5}-{Path.GetFileName(target)}");
        File.SetAttributes(target, FileAttributes.Normal);
        File.Move(target, backup);
        return backup;
    }

    /// <summary>Puts every file back as it was. Best effort: a file that can't be moved back is logged.</summary>
    public void Rollback()
    {
        for (var i = _done.Count - 1; i >= 0; i--)
        {
            var (target, backup) = _done[i];
            try
            {
                if (backup is null)
                {
                    // A new file where there was none: take it away again (into the backup if it is in use).
                    if (File.Exists(target))
                    {
                        try
                        {
                            File.Delete(target);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            MoveToBackup(target);
                        }
                    }
                    continue;
                }
                if (File.Exists(target))
                    File.Move(target, Path.Combine(_backup, $"{_backups++:D5}-new-{Path.GetFileName(target)}"));
                File.Move(backup, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Error($"Rollback: could not restore {target}: {ex.Message}");
            }
        }
        _done.Clear();
        RemoveEmptyFolders(installFolder, keep: workFolder);
        Cleanup();
    }

    /// <summary>Deletes the replaced files and the work folder.</summary>
    public void Commit()
    {
        _done.Clear();
        RemoveEmptyFolders(installFolder, keep: workFolder);
        Cleanup();
    }

    /// <summary>Deletes the work folder; what is still in use goes at the next restart.</summary>
    public void Cleanup() => DeleteTree(workFolder, system, log);

    /// <summary>
    /// Deletes a folder tree; files still in use are deleted at the next restart. Junctions and links inside are
    /// removed themselves, never followed: Setup runs elevated, and a link could otherwise point it at any folder.
    /// </summary>
    /// <param name="userFolder">
    /// A folder the user can change (%LocalAppData%, %AppData%), not the install folder: nothing is left for the next
    /// restart, which deletes by path when the folders on the way may have been swapped for links meanwhile.
    /// </param>
    public static void DeleteTree(string folder, ISetupSystem system, SetupLog log, bool userFolder = false)
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(folder))
            return;
        if (new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            // The folder itself is a link: only the link goes. Its parent was checked by the caller, or is ours.
            log.Info($"{folder} is a link; removing only the link");
            var parent = LinkSafeDelete.RealFolder(Path.GetDirectoryName(Path.GetFullPath(folder))!);
            if (parent is null || LinkSafeDelete.Entry(folder, parent) != LinkSafeDelete.Outcome.Deleted)
                log.Warn($"Could not remove the link {folder}");
            return;
        }
        if (LinkSafeDelete.RealFolder(folder) is not { } root)
        {
            log.Warn($"Not deleting {folder}: it is reached through a junction or link");
            return;
        }
        DeleteContents(root, root, system, log, userFolder);
        // The root itself is checked against its parent, which is where it really is.
        if (LinkSafeDelete.Entry(root, Path.GetDirectoryName(root)!) == LinkSafeDelete.Outcome.Failed)
            Later(root, system, log, userFolder);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void DeleteContents(string folder, string root, ISetupSystem system, SetupLog log, bool userFolder)
    {
        IEnumerable<FileSystemInfo> entries;
        try
        {
            // Not recursive: a link found is deleted, not entered.
            entries = new DirectoryInfo(folder).EnumerateFileSystemInfos("*", ListOnly).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"Could not list {folder}: {ex.Message}");
            return;
        }
        foreach (var entry in entries)
        {
            if (entry is DirectoryInfo && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                DeleteContents(entry.FullName, root, system, log, userFolder);
            switch (LinkSafeDelete.Entry(entry.FullName, root))
            {
                case LinkSafeDelete.Outcome.Failed:
                    Later(entry.FullName, system, log, userFolder);
                    break;
                case LinkSafeDelete.Outcome.Outside:
                    log.Warn($"Not deleting {entry.FullName}: it is no longer inside {root}");
                    break;
            }
        }
    }

    private static readonly EnumerationOptions ListOnly = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
    };

    private static void Later(string path, ISetupSystem system, SetupLog log, bool userFolder)
    {
        if (userFolder)
        {
            log.Info($"In use, left in place: {path}");
            return;
        }
        log.Info($"In use, deleted at the next restart: {path}");
        system.DeleteAtRestart(path);
    }

    /// <summary>Removes empty folders below <paramref name="root"/> (not the root itself).</summary>
    public static void RemoveEmptyFolders(string root, string? keep = null)
    {
        if (!Directory.Exists(root))
            return;
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            if (keep is not null && (dir.Equals(keep, StringComparison.OrdinalIgnoreCase)
                                     || dir.StartsWith(keep + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                continue;
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
