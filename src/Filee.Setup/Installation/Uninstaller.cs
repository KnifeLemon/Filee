// Removes Filee:
//  1. "Filee.exe --uninstall-cleanup", as the signed-in user: Filee quits and removes what it registered for that user
//     (Explorer menu entries, the Send To shortcut, the startup entry, the Windows 11 package) and the engines it
//     downloaded;
//  2. ends the programs of uninstall.json that still run (never anything else: a folder chosen by the user may hold
//     other programs), removes the Explorer menu package if it is still there; a Filee that can't be ended stops the
//     uninstall here, before anything is deleted;
//  3. removes the shortcuts, the PATH entry, the files of uninstall.json, the install folder and the uninstall entry;
//  4. deletes the settings (%APPDATA%\Filee) only when the user asked for it: by default they stay for a reinstall.
// A file Windows keeps in use (FileeExplorerMenu.dll loaded by Explorer, the running uninstall.exe) is moved out of
// the folder into the trash folder and deleted at the next restart, so the folder itself goes now. With the trash on
// another volume it is renamed in place; without administrator rights Windows won't delete that one at the restart, so
// the user is told it stays. A file that can't even be renamed keeps the uninstall entry, uninstall.json and
// uninstall.exe, and the uninstall fails: Filee stays listed in Apps and can be uninstalled again, rather than left
// behind without a way to remove it.

using Filee.Setup.Platform;

namespace Filee.Setup.Installation;

public sealed class Uninstaller(SetupContext context)
{
    private ISetupRegistry Registry => context.Registry;
    private ISetupSystem System => context.System;
    private SetupLog Log => context.Log;

    /// <summary>Filee's cleanup also removes the Explorer menu package through PowerShell, which takes a while.</summary>
    public static TimeSpan CleanupTimeout { get; } = TimeSpan.FromSeconds(90);

    /// <summary>How long a program may still be listed after it was ended.</summary>
    private static readonly TimeSpan EndTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Renamed files Windows won't delete at the next restart; nothing runs them now.</summary>
    private readonly List<string> _kept = [];

    /// <summary>
    /// After <see cref="Run"/>: Filee's own cleanup never finished, so the signed-in user may still have its Explorer menu
    /// entries, its startup entry (when it isn't in this account's registry) and downloaded engines.
    /// </summary>
    public bool CleanupFailed { get; private set; }

    /// <summary>After <see cref="Run"/>: the install folder, when renamed files stay in it for the user to delete; else null.</summary>
    public string? FilesLeftIn { get; private set; }

    /// <param name="trash">A folder outside the install folder (same volume if possible) for files still in use.</param>
    /// <param name="releaseUninstaller">
    /// Called when only uninstall.exe and the folder are left: the uninstaller that started this one from the install
    /// folder is told it worked and ends, so its uninstall.exe can be deleted (a single-file program loads parts of
    /// itself from its file while it runs: that file must not move away under it).
    /// </param>
    /// <returns>Files and folders that are deleted at the next restart.</returns>
    /// <exception cref="SetupException">Filee keeps running or files can't be removed; the uninstall entry stays.</exception>
    public IReadOnlyList<string> Run(string folder, InstallRecord record, bool deleteSettings, string trash,
        IProgress<SetupProgress>? progress = null, Action? releaseUninstaller = null)
    {
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        Log.Info($"Uninstalling Filee {record.Version} from {folder} ({record.Scope}); delete settings: {deleteSettings}");
        var later = new List<string>();
        var stuck = new List<string>();
        progress?.Report(new SetupProgress(SetupStep.Closing, 0));

        var exe = Path.Combine(folder, SetupNames.AppExe);
        // Gone already when an earlier try ended in files that stayed: that one ran the cleanup, so nothing failed.
        var hasExe = File.Exists(exe);
        var cleaned = !hasExe || RunCleanup(exe);
        foreach (var program in ProgramsLeftIn(folder, record))
        {
            Log.Info($"Ending {program.Path} ({program.Id})");
            System.Kill(program.Id);
        }
        // From here on a Filee would run on from the trash folder, and offer updates, with nothing left to remove it.
        if (StaysRunning(folder, record))
            throw new SetupException("installer.uninstall.still_running", $"Filee could not be ended and still runs from {folder}");
        progress?.Report(new SetupProgress(SetupStep.Removing, 0.3));
        if (System.ExplorerMenuSupported)
            System.UnregisterExplorerMenu();

        // A Filee that didn't quit gives up before it cleans up. It has been ended now.
        if (!cleaned)
            cleaned = RunCleanup(exe);
        if (!cleaned)
            Log.Warn("Filee's cleanup did not finish: what it registered for the user may stay");
        if (!cleaned || !hasExe)
            RemoveStartupEntry(folder);
        CleanupFailed = !cleaned;

        var hive = PreviousInstall.HiveOf(record.Scope);
        foreach (var shortcut in new[] { context.Paths.StartMenu(record.Scope), context.Paths.Desktop(record.Scope) })
            DeleteFile(Path.Combine(shortcut, SetupNames.ShortcutName), trash, later, stuck);
        if (PathVariable.Set(Registry, record.Scope, Path.Combine(folder, SetupNames.CliFolder), present: false))
        {
            System.BroadcastEnvironmentChange();
            Log.Info("Removed the filee command from PATH");
        }

        progress?.Report(new SetupProgress(SetupStep.Removing, 0.5));
        var files = record.Files.Concat(SetupNames.InnoUninstallerFiles)
            .Where(f => !f.Equals(SetupNames.UninstallerExe, StringComparison.OrdinalIgnoreCase)
                        && !f.Equals(SetupNames.InstallRecordFile, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        for (var i = 0; i < files.Count; i++)
        {
            DeleteFile(Path.Combine(folder, files[i]), trash, later, stuck);
            progress?.Report(new SetupProgress(SetupStep.Removing, 0.5 + 0.4 * (i + 1) / files.Count));
        }
        // Left by an interrupted Setup, and FileeExplorerMenu.dll copies an update moved aside.
        if (Directory.Exists(folder))
        {
            foreach (var work in Directory.EnumerateDirectories(folder, FileSwap.WorkFolderPrefix + "*"))
                FileSwap.DeleteTree(work, System, Log);
        }

        // Uninstalling again needs the list and the entry, so these go only when nothing stayed behind.
        if (stuck.Count == 0)
            DeleteRecord(folder, stuck);
        if (stuck.Count > 0)
        {
            Log.Error($"Could not remove: {string.Join(", ", stuck)}");
            throw new SetupException("installer.uninstall.files_left", $"Could not remove {stuck.Count} files of Filee", folder);
        }
        Registry.DeleteKey(hive, SetupNames.UninstallKey);
        progress?.Report(new SetupProgress(SetupStep.Removing, 0.95));
        if (deleteSettings)
        {
            Log.Info($"Deleting the settings in {context.Paths.SettingsFolder}");
            FileSwap.DeleteTree(context.Paths.SettingsFolder, System, Log);
        }

        releaseUninstaller?.Invoke();
        DeleteFile(Path.Combine(folder, SetupNames.UninstallerExe), trash, later, stuck);
        later.AddRange(stuck);
        FileSwap.RemoveEmptyFolders(folder);
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Something that isn't Filee's is still in it (a folder the user chose), or a file is in use.
            Log.Warn($"{folder} stays: {ex.Message}");
            DeleteFolderAtRestart(folder, later);
        }
        if (_kept.Count > 0)
        {
            Log.Warn($"Stay until they are deleted by hand: {string.Join(", ", _kept)}");
            FilesLeftIn = folder;
        }
        progress?.Report(new SetupProgress(SetupStep.Removing, 1));
        Log.Info("Uninstalled");
        return later;
    }

    private bool RunCleanup(string exe)
    {
        var code = System.RunAsSignedInUser(exe, "--uninstall-cleanup", CleanupTimeout);
        Log.Info($"Filee --uninstall-cleanup: exit code {code}");
        return code == 0;
    }

    /// <summary>
    /// The programs of uninstall.json that run: anything else in the folder isn't Filee's to end. Not the uninstaller that
    /// started this one either: it waits for this one and ends by itself.
    /// </summary>
    private List<RunningProgram> ProgramsLeftIn(string folder, InstallRecord record)
    {
        var ours = record.Files
            .Where(f => !f.Equals(SetupNames.UninstallerExe, StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetFullPath(Path.Combine(folder, f)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return System.ProgramsIn(folder).Where(p => p.Id != Environment.ProcessId && ours.Contains(p.Path)).ToList();
    }

    /// <summary>
    /// Whether Filee still runs after it was ended: it can't be ended, or an elevated uninstaller may not look at another
    /// account's Filee. Without administrator rights every other account's is out of sight, but a per-user folder
    /// isn't theirs to run from. Looks at processes, not at whether Filee.exe is in use: Explorer showing its icon, or
    /// Apps in Settings, holds it just the same.
    /// </summary>
    private bool StaysRunning(string folder, InstallRecord record)
    {
        var deadline = DateTime.UtcNow + EndTimeout;
        while (true)
        {
            var left = ProgramsLeftIn(folder, record);
            var unseen = record.Scope == InstallScope.Machine && System.RunsOutOfSight(Path.GetFileNameWithoutExtension(SetupNames.AppExe));
            if (left.Count == 0 && !unseen)
                return false;
            if (DateTime.UtcNow >= deadline)
            {
                Log.Error(left.Count > 0
                    ? $"Still running: {string.Join(", ", left.Select(p => p.Path))}"
                    : "Still running: a Filee this program may not look at");
                return true;
            }
            Thread.Sleep(250);
        }
    }

    /// <summary>Filee's startup entry in this account's registry, if it starts the Filee in this folder (not a portable copy).</summary>
    private void RemoveStartupEntry(string folder)
    {
        var command = Registry.Get(Hive.CurrentUser, SetupNames.RunKey, SetupNames.AppName)?.Text;
        if (command is null || !command.Contains(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return;
        Registry.DeleteValue(Hive.CurrentUser, SetupNames.RunKey, SetupNames.AppName);
        Log.Info("Removed the startup entry");
    }

    /// <summary>
    /// Deletes uninstall.json, or leaves it as it is: never at the next restart or under another name, because uninstalling
    /// again after that restart can't do without it.
    /// </summary>
    private void DeleteRecord(string folder, List<string> stuck)
    {
        var path = InstallRecord.PathIn(folder);
        try
        {
            if (!File.Exists(path))
                return;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not delete {path}: {ex.Message}");
            stuck.Add(path);
        }
    }

    /// <summary>
    /// Deletes a file; one in use is moved into <paramref name="trash"/> (or renamed in place) and deleted at the next
    /// restart. One that can't even be renamed is <paramref name="stuck"/>: it is still there under its own name. One
    /// renamed in place that Windows won't delete at the restart (it takes administrator rights) stays too, but nothing
    /// runs it now and uninstalling again wouldn't know its new name: the user is told.
    /// </summary>
    private void DeleteFile(string path, string trash, List<string> later, List<string> stuck)
    {
        if (!File.Exists(path))
            return;
        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Info($"In use: {path} ({ex.Message})");
        }
        var aside = Path.Combine(trash, $"{Guid.NewGuid():N}-{Path.GetFileName(path)}");
        var left = false;
        try
        {
            Directory.CreateDirectory(trash);
            File.Move(path, aside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another volume: renaming works only on the same one. Stay in the folder under another name.
            left = true;
            aside = path + "." + Guid.NewGuid().ToString("N")[..8] + ".old";
            try
            {
                File.Move(path, aside);
            }
            catch (Exception again) when (again is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Could not move {path} aside: {again.Message} ({ex.Message})");
                // Windows may still delete it at the next restart; uninstalling again then finds it gone.
                System.DeleteAtRestart(path);
                stuck.Add(path);
                return;
            }
        }
        if (System.DeleteAtRestart(aside) || !left)
            later.Add(aside);
        else
            _kept.Add(aside);
    }

    /// <summary>
    /// The folder and the ones in it go after the files that are deleted at the next restart. Anything else in them
    /// (the user's own files) keeps them.
    /// </summary>
    private void DeleteFolderAtRestart(string folder, List<string> later)
    {
        if (!Directory.Exists(folder) || Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Any(f => !later.Contains(f, StringComparer.OrdinalIgnoreCase)))
            return;
        foreach (var dir in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length).Append(folder))
        {
            System.DeleteAtRestart(dir);
            later.Add(dir);
        }
    }
}
