using System.Runtime.InteropServices;
using Filee.Setup.Installation;
using Filee.Setup.Platform;

namespace Filee.Setup.Tests;

public class UninstallerTests
{
    private static (SetupBench Bench, InstallRecord Record) Installed(SetupTasks? tasks = null)
    {
        var bench = new SetupBench();
        Assert.True(bench.Install(bench.Choices(tasks)).Succeeded);
        return (bench, InstallRecord.Read(bench.InstallFolder)!);
    }

    [Fact]
    public void Uninstalling_removes_filee_and_keeps_the_settings()
    {
        var (bench, record) = Installed(SetupTasks.Defaults with { DesktopIcon = true });
        using var _ = bench;
        var settings = bench.Temp.Write(Path.Combine(Path.GetRelativePath(bench.Temp.Path, bench.Paths.SettingsFolder), "settings.json"), "{}");
        bench.Registry.SetString(Hive.LocalMachine, SetupNames.MachineEnvironmentKey, "Path",
            @"%SystemRoot%\system32;" + Path.Combine(bench.InstallFolder, "cli") + @";C:\Tools", expandable: true);
        bench.System.ExplorerMenuSupported = true;
        bench.System.MenuRegistered = true;

        var later = new Uninstaller(bench.Context).Run(bench.InstallFolder, record, deleteSettings: false, bench.Temp.Sub("trash"));

        Assert.Empty(later);
        // Filee cleaned up after itself first (Explorer menu, startup entry, downloaded engines), once and as the
        // signed-in user: those are that user's, and an elevated process can't reach the Filee they run.
        Assert.Equal(["run-as-user Filee.exe --uninstall-cleanup"], bench.System.Calls.Where(c => c.Contains("--uninstall-cleanup", StringComparison.Ordinal)));
        Assert.Contains("unregister-menu", bench.System.Calls);
        Assert.False(Directory.Exists(bench.InstallFolder));
        Assert.False(File.Exists(Path.Combine(bench.Paths.CommonStartMenu, "Filee.lnk")));
        Assert.False(File.Exists(Path.Combine(bench.Paths.CommonDesktop, "Filee.lnk")));
        Assert.False(bench.Registry.KeyExists(Hive.LocalMachine, SetupNames.UninstallKey));
        var path = bench.Registry.Get(Hive.LocalMachine, SetupNames.MachineEnvironmentKey, "Path")!;
        Assert.Equal(@"%SystemRoot%\system32;C:\Tools", path.Text);
        Assert.True(path.Expandable);
        Assert.True(File.Exists(settings));
    }

    [Fact]
    public void The_settings_go_when_the_user_asks()
    {
        var (bench, record) = Installed();
        using var _ = bench;
        bench.Temp.Write(Path.Combine(Path.GetRelativePath(bench.Temp.Path, bench.Paths.SettingsFolder), @"logs\filee.log"));
        new Uninstaller(bench.Context).Run(bench.InstallFolder, record, deleteSettings: true, bench.Temp.Sub("trash"));
        Assert.False(Directory.Exists(bench.Paths.SettingsFolder));
        // Only Filee's folder: %APPDATA% itself stays.
        Assert.True(Directory.Exists(bench.Paths.AppData));
    }

    [Fact]
    public void Files_that_are_not_filees_stay()
    {
        var (bench, record) = Installed();
        using var _ = bench;
        var mine = Path.Combine(bench.InstallFolder, "my notes.txt");
        File.WriteAllText(mine, "mine");
        new Uninstaller(bench.Context).Run(bench.InstallFolder, record, deleteSettings: false, bench.Temp.Sub("trash"));
        Assert.True(File.Exists(mine));
        Assert.Equal([mine], Directory.EnumerateFileSystemEntries(bench.InstallFolder, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void A_dll_explorer_has_loaded_is_moved_out_and_deleted_at_the_next_restart()
    {
        var (bench, record) = Installed();
        using var _ = bench;
        // Stand-in for FileeExplorerMenu.dll loaded by Explorer: a real DLL loaded into this process, which Windows
        // won't let anyone delete (but rename).
        var dll = Path.Combine(bench.InstallFolder, "FileeExplorerMenu.dll");
        File.Copy(Path.Combine(Environment.SystemDirectory, "msimg32.dll"), dll, overwrite: true);
        var handle = NativeLibrary.Load(dll);
        try
        {
            var trash = bench.Temp.Sub("trash");
            var later = new Uninstaller(bench.Context).Run(bench.InstallFolder, record, deleteSettings: false, trash);
            var moved = Assert.Single(later);
            Assert.StartsWith(trash, moved, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"delete-at-restart {moved}", bench.System.Calls);
            // So the install folder itself is gone now, not at the next restart.
            Assert.False(Directory.Exists(bench.InstallFolder));
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    [Fact]
    public void The_waiting_uninstaller_is_released_last_before_its_file_goes()
    {
        var (bench, record) = Installed();
        using var _ = bench;
        var uninstaller = Path.Combine(bench.InstallFolder, "uninstall.exe");
        var seen = new List<string>();
        new Uninstaller(bench.Context).Run(bench.InstallFolder, record, deleteSettings: false, bench.Temp.Sub("trash"),
            releaseUninstaller: () =>
            {
                // Everything else is gone by now, the uninstall entry too; only the uninstaller is left.
                seen.AddRange(Directory.EnumerateFiles(bench.InstallFolder, "*", SearchOption.AllDirectories));
                Assert.False(bench.Registry.KeyExists(Hive.LocalMachine, SetupNames.UninstallKey));
            });
        Assert.Equal([uninstaller], seen);
        Assert.False(Directory.Exists(bench.InstallFolder));
    }

    [Fact]
    public void Programs_left_running_are_ended_but_not_the_uninstaller_that_waits()
    {
        var (bench, record) = Installed();
        using var _ = bench;
        bench.System.Running.Add(new RunningProgram(11, Path.Combine(bench.InstallFolder, "filee-cli.exe")));
        bench.System.Running.Add(new RunningProgram(12, Path.Combine(bench.InstallFolder, "uninstall.exe")));
        new Uninstaller(bench.Context).Run(bench.InstallFolder, record, deleteSettings: false, bench.Temp.Sub("trash"));
        Assert.Contains("kill 11", bench.System.Calls);
        Assert.DoesNotContain("kill 12", bench.System.Calls);
    }

    [Fact]
    public void A_cleanup_stopped_by_a_filee_that_did_not_quit_runs_again_once_it_has_been_ended()
    {
        var (bench, record) = Installed();
        using var _ = bench;
        bench.System.Running.Add(new RunningProgram(11, Path.Combine(bench.InstallFolder, "Filee.exe")));
        // What Filee.exe does when the running Filee stays: exit code 1, before it removes anything (#1079).
        bench.System.Run = (_, _) => bench.System.Running.Count > 0 ? 1 : 0;

        var uninstaller = new Uninstaller(bench.Context);
        uninstaller.Run(bench.InstallFolder, record, deleteSettings: false, bench.Temp.Sub("trash"));

        Assert.Equal(["run-as-user Filee.exe --uninstall-cleanup", "kill 11", "run-as-user Filee.exe --uninstall-cleanup"],
            bench.System.Calls.Where(c => c.Contains("--uninstall-cleanup", StringComparison.Ordinal) || c.StartsWith("kill", StringComparison.Ordinal)));
        Assert.False(uninstaller.CleanupFailed);
    }

    [Fact]
    public void A_cleanup_that_never_finishes_still_takes_the_startup_entry_and_says_so()
    {
        var (bench, record) = Installed();
        using var _ = bench;
        var command = $"\"{Path.Combine(bench.InstallFolder, "Filee.exe")}\" --background";
        bench.Registry.SetString(Hive.CurrentUser, SetupNames.RunKey, "Filee", command.ToUpperInvariant());
        bench.Registry.SetString(Hive.CurrentUser, SetupNames.RunKey, "Other", command);
        bench.System.Run = (_, _) => -1;

        var uninstaller = new Uninstaller(bench.Context);
        uninstaller.Run(bench.InstallFolder, record, deleteSettings: false, bench.Temp.Sub("trash"));

        Assert.Null(bench.Registry.Get(Hive.CurrentUser, SetupNames.RunKey, "Filee"));
        Assert.Equal(command, bench.Registry.Get(Hive.CurrentUser, SetupNames.RunKey, "Other")?.Text);
        Assert.True(uninstaller.CleanupFailed);
        Assert.False(Directory.Exists(bench.InstallFolder));
    }

    [Fact]
    public void The_startup_entry_of_a_copy_elsewhere_stays()
    {
        var (bench, record) = Installed();
        using var _ = bench;
        var portable = $"\"{Path.Combine(bench.InstallFolder + "-portable", "Filee.exe")}\" --background";
        bench.Registry.SetString(Hive.CurrentUser, SetupNames.RunKey, "Filee", portable);
        bench.System.Run = (_, _) => 1;

        new Uninstaller(bench.Context).Run(bench.InstallFolder, record, deleteSettings: false, bench.Temp.Sub("trash"));

        Assert.Equal(portable, bench.Registry.Get(Hive.CurrentUser, SetupNames.RunKey, "Filee")?.Text);
    }

    [Fact]
    public void A_filee_that_can_not_be_ended_stops_the_uninstall_before_anything_goes()
    {
        var (bench, record) = Installed();
        using var _ = bench;
        bench.System.Running.Add(new RunningProgram(11, Path.Combine(bench.InstallFolder, "Filee.exe")));
        bench.System.CantBeEnded = true;
        var files = Directory.GetFiles(bench.InstallFolder, "*", SearchOption.AllDirectories);

        var error = Assert.Throws<SetupException>(() =>
            new Uninstaller(bench.Context).Run(bench.InstallFolder, record, deleteSettings: false, bench.Temp.Sub("trash")));

        Assert.Equal("installer.uninstall.still_running", error.TextKey);
        Assert.Equal(files, Directory.GetFiles(bench.InstallFolder, "*", SearchOption.AllDirectories));
        Assert.True(bench.Registry.KeyExists(Hive.LocalMachine, SetupNames.UninstallKey));
    }

    [Fact]
    public void A_filee_out_of_sight_stops_a_machine_wide_uninstall_too()
    {
        var (bench, record) = Installed();
        using var _ = bench;
        // Another account's Filee when the rights to look at it are missing. It would run on from the trash folder (#1079).
        bench.System.OutOfSight = true;

        var error = Assert.Throws<SetupException>(() =>
            new Uninstaller(bench.Context).Run(bench.InstallFolder, record, deleteSettings: false, bench.Temp.Sub("trash")));

        Assert.Equal("installer.uninstall.still_running", error.TextKey);
        Assert.True(File.Exists(Path.Combine(bench.InstallFolder, "Filee.exe")));
        Assert.True(bench.Registry.KeyExists(Hive.LocalMachine, SetupNames.UninstallKey));
    }

    [Fact]
    public void Another_accounts_filee_does_not_stop_a_per_user_uninstall()
    {
        var (bench, record) = Installed();
        using var _ = bench;
        // Without administrator rights every other account's Filee is out of sight, and none runs from this user's folder.
        bench.System.OutOfSight = true;

        new Uninstaller(bench.Context).Run(bench.InstallFolder, record with { Scope = InstallScope.User }, deleteSettings: false, bench.Temp.Sub("trash"));

        Assert.False(Directory.Exists(bench.InstallFolder));
    }

    [Fact]
    public void A_filee_exe_something_merely_reads_is_not_a_running_filee()
    {
        var (bench, record) = Installed();
        using var _ = bench;
        // Explorer showing its icon, or Apps in Settings: mapped as an image, which refuses opening it for writing as a
        // running program does. Nothing runs it, and it can be moved away.
        var exe = Path.Combine(bench.InstallFolder, "Filee.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "msimg32.dll"), exe, overwrite: true);
        var library = NativeLibrary.Load(exe);
        try
        {
            var later = new Uninstaller(bench.Context).Run(bench.InstallFolder, record, deleteSettings: false, bench.Temp.Sub("trash"));

            Assert.Single(later);
            Assert.False(Directory.Exists(bench.InstallFolder));
            Assert.False(bench.Registry.KeyExists(Hive.LocalMachine, SetupNames.UninstallKey));
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    [Fact]
    public void Only_programs_filee_installed_are_ended()
    {
        var (bench, record) = Installed();
        using var _ = bench;
        // A folder the user chose (/DIR=D:\Apps) may run a service or another account's program, which the elevated
        // uninstaller could end. Its own programs are also found below the folder, whatever the case of the path.
        bench.System.Running.Add(new RunningProgram(11, Path.Combine(bench.InstallFolder, "engines", "rhwp", "RHWP.EXE")));
        bench.System.Running.Add(new RunningProgram(21, Path.Combine(bench.InstallFolder, "other-app.exe")));
        bench.System.Running.Add(new RunningProgram(22, Path.Combine(bench.InstallFolder, "engines", "someone-elses.exe")));
        File.WriteAllText(Path.Combine(bench.InstallFolder, "other-app.exe"), "theirs");

        new Uninstaller(bench.Context).Run(bench.InstallFolder, record, deleteSettings: false, bench.Temp.Sub("trash"));

        Assert.Equal(["kill 11"], bench.System.Calls.Where(c => c.StartsWith("kill", StringComparison.Ordinal)));
        Assert.True(File.Exists(Path.Combine(bench.InstallFolder, "other-app.exe")));
    }

    [Fact]
    public void A_program_that_is_not_filees_never_stops_the_uninstall()
    {
        var (bench, record) = Installed();
        using var _ = bench;
        bench.System.Running.Add(new RunningProgram(21, Path.Combine(bench.InstallFolder, "other-app.exe")));
        bench.System.CantBeEnded = true;

        new Uninstaller(bench.Context).Run(bench.InstallFolder, record, deleteSettings: false, bench.Temp.Sub("trash"));

        Assert.DoesNotContain("kill 21", bench.System.Calls);
        Assert.False(bench.Registry.KeyExists(Hive.LocalMachine, SetupNames.UninstallKey));
    }

    [Fact]
    public void Files_that_can_not_be_removed_keep_the_entry_and_a_second_try_finishes()
    {
        var (bench, record) = Installed();
        using var _ = bench;
        var uninstaller = Path.Combine(bench.InstallFolder, "uninstall.exe");
        var released = false;

        // Locked with no sharing at all: it can't be deleted or moved, as when security software refuses (#1079).
        using (new FileStream(Path.Combine(bench.InstallFolder, "Filee.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = Assert.Throws<SetupException>(() => new Uninstaller(bench.Context).Run(bench.InstallFolder, record,
                deleteSettings: false, bench.Temp.Sub("trash"), releaseUninstaller: () => released = true));

            Assert.Equal("installer.uninstall.files_left", error.TextKey);
            Assert.Equal([bench.InstallFolder], error.Arguments);
        }

        // Filee stays in Apps, with what it takes to uninstall it again.
        Assert.False(released);
        Assert.True(bench.Registry.KeyExists(Hive.LocalMachine, SetupNames.UninstallKey));
        Assert.True(File.Exists(uninstaller));
        Assert.NotNull(InstallRecord.Read(bench.InstallFolder));

        // The first try took Filee.exe, and ran the cleanup: not having it now is no failure to warn about. A startup
        // entry that still points here goes all the same.
        Assert.False(File.Exists(Path.Combine(bench.InstallFolder, "Filee.exe")));
        var command = $"\"{Path.Combine(bench.InstallFolder, "Filee.exe")}\" --background";
        bench.Registry.SetString(Hive.CurrentUser, SetupNames.RunKey, "Filee", command);
        var second = new Uninstaller(bench.Context);
        Assert.Empty(second.Run(bench.InstallFolder, InstallRecord.Read(bench.InstallFolder)!, deleteSettings: false, bench.Temp.Sub("trash")));
        Assert.False(second.CleanupFailed);
        Assert.Null(bench.Registry.Get(Hive.CurrentUser, SetupNames.RunKey, "Filee"));
        Assert.Single(bench.System.Calls, c => c.Contains("--uninstall-cleanup", StringComparison.Ordinal));
        Assert.False(bench.Registry.KeyExists(Hive.LocalMachine, SetupNames.UninstallKey));
        Assert.False(Directory.Exists(bench.InstallFolder));
    }

    /// <summary>Holds a file with no sharing at all, as a security scan can, and lets go after a moment.</summary>
    private static Task LockFor(string path, TimeSpan time)
    {
        var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        return Task.Delay(time).ContinueWith(_ => locked.Dispose(), TaskScheduler.Default);
    }

    [Theory]
    [InlineData("Filee.dll")]
    [InlineData("uninstall.json")]
    public async Task A_file_a_scan_holds_for_a_moment_does_not_fail_the_uninstall(string file)
    {
        var (bench, record) = Installed();
        using var _ = bench;
        var scan = LockFor(Path.Combine(bench.InstallFolder, file), TimeSpan.FromMilliseconds(700));

        var later = new Uninstaller(bench.Context).Run(bench.InstallFolder, record, deleteSettings: false, bench.Temp.Sub("trash"));
        await scan;

        Assert.Empty(later);
        Assert.DoesNotContain(bench.System.Calls, c => c.Contains("delete-at-restart", StringComparison.Ordinal));
        Assert.False(bench.Registry.KeyExists(Hive.LocalMachine, SetupNames.UninstallKey));
        Assert.False(Directory.Exists(bench.InstallFolder));
    }

    [Fact]
    public void The_wait_for_locked_files_is_one_wait_however_many_are_locked()
    {
        var (bench, record) = Installed();
        using var _ = bench;
        var names = new[] { "Filee.dll", "filee-cli.exe", "FileeExplorerMenu.dll", "FileeExplorerMenu.msix", @"cli\filee.cmd", @"engines\rhwp\rhwp.exe" };
        var locks = names.Select(f => new FileStream(Path.Combine(bench.InstallFolder, f), FileMode.Open, FileAccess.Read, FileShare.None)).ToList();
        try
        {
            var watch = global::System.Diagnostics.Stopwatch.StartNew();
            var error = Assert.Throws<SetupException>(() =>
                new Uninstaller(bench.Context).Run(bench.InstallFolder, record, deleteSettings: false, bench.Temp.Sub("trash")));
            watch.Stop();

            Assert.Equal("installer.uninstall.files_left", error.TextKey);
            // About two seconds: it did wait, and not two seconds for each of the six.
            Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(1.5), $"took {watch.Elapsed}");
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), $"took {watch.Elapsed}");
            foreach (var name in names)
                Assert.Contains($"delete-at-restart {Path.Combine(bench.InstallFolder, name)}", bench.System.Calls);
        }
        finally
        {
            locks.ForEach(l => l.Dispose());
        }
    }

    [Fact]
    public void The_list_of_files_is_never_scheduled_or_renamed_so_a_later_try_can_read_it()
    {
        var (bench, record) = Installed();
        using var _ = bench;
        var list = InstallRecord.PathIn(bench.InstallFolder);

        using (new FileStream(list, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = Assert.Throws<SetupException>(() =>
                new Uninstaller(bench.Context).Run(bench.InstallFolder, record, deleteSettings: false, bench.Temp.Sub("trash")));
            Assert.Equal("installer.uninstall.files_left", error.TextKey);
        }

        // Had it gone at the restart we ask for, uninstalling again would say Filee isn't installed, with its entry left.
        Assert.DoesNotContain(bench.System.Calls, c => c.Contains("delete-at-restart", StringComparison.Ordinal));
        Assert.True(bench.Registry.KeyExists(Hive.LocalMachine, SetupNames.UninstallKey));
        var again = InstallRecord.Read(bench.InstallFolder);
        Assert.NotNull(again);

        new Uninstaller(bench.Context).Run(bench.InstallFolder, again, deleteSettings: false, bench.Temp.Sub("trash"));
        Assert.False(bench.Registry.KeyExists(Hive.LocalMachine, SetupNames.UninstallKey));
        Assert.False(Directory.Exists(bench.InstallFolder));
    }

    /// <summary>A file in use that can't leave the folder: trash can't be created (it would be on another volume).</summary>
    private static (string Trash, nint Library) LoadedDllAndNoTrash(SetupBench bench)
    {
        var dll = Path.Combine(bench.InstallFolder, "FileeExplorerMenu.dll");
        File.Copy(Path.Combine(Environment.SystemDirectory, "msimg32.dll"), dll, overwrite: true);
        return (Path.Combine(bench.Temp.Write("not-a-folder"), "trash"), NativeLibrary.Load(dll));
    }

    [Fact]
    public void A_file_renamed_in_place_goes_at_the_next_restart_with_its_folder()
    {
        var (bench, record) = Installed();
        using var _ = bench;
        var (trash, library) = LoadedDllAndNoTrash(bench);
        try
        {
            var later = new Uninstaller(bench.Context).Run(bench.InstallFolder, record, deleteSettings: false, trash);

            var renamed = Assert.Single(Directory.GetFiles(bench.InstallFolder, "*", SearchOption.AllDirectories));
            // The file first, then the folder that holds it: otherwise the empty folder stays after the restart.
            Assert.Equal([renamed, bench.InstallFolder], later);
            Assert.True(bench.System.Calls.IndexOf($"delete-at-restart {renamed}") < bench.System.Calls.IndexOf($"delete-at-restart {bench.InstallFolder}"));
            Assert.False(bench.Registry.KeyExists(Hive.LocalMachine, SetupNames.UninstallKey));
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    [Fact]
    public void A_renamed_file_windows_will_not_delete_later_stays_and_the_user_is_told()
    {
        var (bench, record) = Installed();
        using var _ = bench;
        // A per-user install on another volume than %TEMP%: scheduling takes administrator rights, so Windows refuses.
        var (trash, library) = LoadedDllAndNoTrash(bench);
        bench.System.RestartListRefuses = true;
        try
        {
            var uninstaller = new Uninstaller(bench.Context);
            var later = uninstaller.Run(bench.InstallFolder, record with { Scope = InstallScope.User }, deleteSettings: false, trash);

            Assert.Empty(later);
            Assert.Equal(bench.InstallFolder, uninstaller.FilesLeftIn);
            Assert.EndsWith(".old", Assert.Single(Directory.GetFiles(bench.InstallFolder, "*", SearchOption.AllDirectories)), StringComparison.Ordinal);
            Assert.False(bench.Registry.KeyExists(Hive.CurrentUser, SetupNames.UninstallKey));
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    [Fact]
    public void Install_record_round_trip_and_bad_records()
    {
        using var temp = new TempFolder();
        var record = new InstallRecord("2.0.0", InstallScope.User, ["Filee.exe", @"cli\filee.cmd"]);
        File.WriteAllBytes(InstallRecord.PathIn(temp.Path), record.ToJson());
        var read = InstallRecord.Read(temp.Path)!;
        Assert.Equal(record.Version, read.Version);
        Assert.Equal(record.Scope, read.Scope);
        Assert.Equal(record.Files, read.Files);

        // A record that points outside the folder is no record (the uninstaller then refuses).
        File.WriteAllText(InstallRecord.PathIn(temp.Path), """{"version":"2.0.0","scope":"machine","files":["..\\..\\Windows\\notepad.exe"]}""");
        Assert.Null(InstallRecord.Read(temp.Path));
        File.WriteAllText(InstallRecord.PathIn(temp.Path), "not json");
        Assert.Null(InstallRecord.Read(temp.Path));
        Assert.Null(InstallRecord.Read(temp.Sub("missing")));
    }
}

public class PreviousInstallTests
{
    [Fact]
    public void An_entry_without_filee_in_its_folder_is_no_installation()
    {
        using var temp = new TempFolder();
        var registry = new MemoryRegistry();
        registry.SetString(Hive.LocalMachine, SetupNames.UninstallKey, "InstallLocation", temp.Sub("gone") + "\\");
        Assert.Null(PreviousInstall.Find(registry, InstallScope.Machine));
        Assert.Null(PreviousInstall.Find(new MemoryRegistry(), InstallScope.Machine));
    }

    [Fact]
    public void Inno_setups_app_path_is_enough_and_the_scope_is_kept_apart()
    {
        using var temp = new TempFolder();
        temp.Write(@"Filee\Filee.exe");
        temp.Write(@"Filee\unins000.exe");
        var registry = new MemoryRegistry();
        registry.SetString(Hive.LocalMachine, SetupNames.UninstallKey, "Inno Setup: App Path", temp.Sub("Filee"));
        var previous = PreviousInstall.Find(registry, InstallScope.Machine)!;
        Assert.Equal(temp.Sub("Filee"), previous.Folder);
        Assert.True(previous.ByInno);
        Assert.Null(previous.RecordedTasks);
        Assert.Contains("unins000.exe", previous.OwnedFiles());
        Assert.Null(PreviousInstall.Find(registry, InstallScope.User));
    }

    [Fact]
    public void Current_tasks_come_from_what_is_registered_now()
    {
        using var temp = new TempFolder();
        var paths = SetupPaths.Sandbox(temp.Sub("pc"));
        var folder = Path.GetDirectoryName(temp.Write(@"pc\Program Files\Filee\Filee.exe"))!;
        var registry = new MemoryRegistry();
        registry.SetString(Hive.LocalMachine, SetupNames.UninstallKey, "InstallLocation", folder);
        // 1.x recorded the desktop icon and PATH, but the user deleted the shortcut since.
        registry.SetString(Hive.LocalMachine, SetupNames.UninstallKey, SetupNames.InnoTasksValue, @"contextmenu,startup,cli,desktopicon");
        registry.SetString(Hive.LocalMachine, SetupNames.MachineEnvironmentKey, "Path", folder.ToUpperInvariant() + @"\CLI\");
        var system = new FakeSystem { ExplorerMenuSupported = true, MenuRegistered = false };

        var tasks = PreviousInstall.Find(registry, InstallScope.Machine)!.CurrentTasks(registry, system, paths);
        Assert.Equal(new SetupTasks(ContextMenu: true, ModernMenu: false, Startup: true, Cli: true, DesktopIcon: false), tasks);
    }
}

public class PathVariableTests
{
    [Theory]
    [InlineData(null, @"C:\F\cli", @"C:\F\cli")]
    [InlineData("", @"C:\F\cli", @"C:\F\cli")]
    [InlineData(@"C:\a", @"C:\F\cli", @"C:\a;C:\F\cli")]
    [InlineData(@"C:\a;", @"C:\F\cli", @"C:\a;C:\F\cli")]
    [InlineData(@"C:\a;c:\f\CLI\", @"C:\F\cli", @"C:\a;c:\f\CLI\")]
    public void Add(string? paths, string folder, string expected) => Assert.Equal(expected, PathVariable.Add(paths, folder));

    [Theory]
    [InlineData(@"C:\a;C:\F\cli;C:\b", @"C:\a;C:\b")]
    [InlineData(@"C:\F\cli\;C:\a", @"C:\a")]
    [InlineData(@"C:\a;;C:\F\CLI;C:\b;", @"C:\a;;C:\b;")]
    [InlineData(@"C:\F\cli", "")]
    [InlineData(@"C:\a;C:\F\cli2", @"C:\a;C:\F\cli2")]
    public void Remove(string paths, string expected) => Assert.Equal(expected, PathVariable.Remove(paths, @"C:\F\cli"));

    [Fact]
    public void Setting_keeps_the_kind_and_reports_changes()
    {
        var registry = new MemoryRegistry();
        registry.SetString(Hive.CurrentUser, SetupNames.UserEnvironmentKey, "Path", @"C:\a", expandable: false);
        Assert.True(PathVariable.Set(registry, InstallScope.User, @"C:\F\cli", present: true));
        Assert.False(PathVariable.Set(registry, InstallScope.User, @"C:\F\cli", present: true));
        var entry = registry.Get(Hive.CurrentUser, SetupNames.UserEnvironmentKey, "Path")!;
        Assert.Equal(@"C:\a;C:\F\cli", entry.Text);
        Assert.False(entry.Expandable);
        Assert.True(PathVariable.Set(registry, InstallScope.User, @"C:\F\cli", present: false));
        Assert.False(PathVariable.Set(registry, InstallScope.User, @"C:\F\cli", present: false));
        // Never touched the machine PATH.
        Assert.False(registry.KeyExists(Hive.LocalMachine, SetupNames.MachineEnvironmentKey));
    }

    [Fact]
    public void The_sandbox_registry_survives_a_restart_of_setup()
    {
        using var temp = new TempFolder();
        var file = temp.Sub("registry.json");
        var registry = new MemoryRegistry(file);
        registry.SetString(Hive.LocalMachine, SetupNames.UninstallKey, "DisplayName", "Filee");
        registry.SetNumber(Hive.LocalMachine, SetupNames.UninstallKey, "NoModify", 1);
        registry.SetString(Hive.LocalMachine, SetupNames.MachineEnvironmentKey, "Path", "%SystemRoot%", expandable: true);
        var again = new MemoryRegistry(file);
        Assert.Equal("Filee", again.Get(Hive.LocalMachine, SetupNames.UninstallKey, "DisplayName")?.Text);
        Assert.Equal(1, again.Get(Hive.LocalMachine, SetupNames.UninstallKey, "NoModify")?.Value);
        Assert.True(again.Get(Hive.LocalMachine, SetupNames.MachineEnvironmentKey, "Path")!.Expandable);
        Assert.True(again.KeyExists(Hive.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"));
        again.DeleteKey(Hive.LocalMachine, @"Software\Microsoft");
        Assert.False(new MemoryRegistry(file).KeyExists(Hive.LocalMachine, SetupNames.UninstallKey));
    }
}
