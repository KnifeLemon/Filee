// Entry point of Setup (Filee-<version>-win-Setup.exe, which carries a payload) and of the uninstaller (uninstall.exe,
// the same program without one). SetupStart tells them apart, and from a Setup.exe whose download didn't finish.
//
// Rights: the program starts without administrator rights (asInvoker). An install for all users starts itself again
// with them (one UAC prompt) and waits; the elevated copy writes what to start at the end into a file, and this
// unelevated one starts Filee as the signed-in user: Filee must not run elevated (Windows blocks drag and drop from
// Explorer into elevated windows). The uninstaller first copies itself out of the install folder and runs from there,
// so the install folder can be deleted. One for all users asks for administrator rights before that, from the install
// folder, and the elevated one makes the copy where standard users can't change it (see CopyUninstaller).

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Filee.Setup.Installation;
using Filee.Setup.Payload;
using Filee.Setup.Platform;
using Filee.Setup.ViewModels;

namespace Filee.Setup;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitInitFailed = 1;
    private const int ExitCancelled = 2;
    private const int ExitFailed = 4;

    /// <summary>Win32 ERROR_CANCELLED: the user declined the UAC prompt.</summary>
    private const int ErrorCancelled = 1223;

    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
            return ExitInitFailed;
        // Before anything loads a native library: Windows' own only from System32 (see NativeLibraries).
        NativeLibraries.Guard();
        // Never hold a folder Setup may delete (the install folder) as the current directory.
        Environment.CurrentDirectory = Environment.SystemDirectory;
        NativeMethods.QuietErrors();
        var options = SetupOptions.Parse(args);
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "uninstall.exe");
        var sandbox = options.Sandbox is { } root ? Path.GetFullPath(root) : null;
        var paths = sandbox is null ? SetupPaths.Real() : SetupPaths.Sandbox(sandbox);
        Directory.CreateDirectory(paths.Temp);

        var start = SetupStart.Of(exe, options.Uninstall);
        var payload = start.Payload;
        var uninstall = start.Mode == StartMode.Uninstall;
        var log = new SetupLog(options.LogFile ?? SetupLog.DefaultFile(paths.Temp, uninstall ? "Filee-Uninstall" : "Filee-Setup"));
        log.Info($"{(uninstall ? "Uninstaller" : "Setup")} {SetupInfo.Version}{(payload is null ? "" : $" with Filee {payload.Manifest.Version}")}: " +
                 $"{exe} {SetupOptions.Join(args)}; administrator: {NativeMethods.IsElevated()}; Windows {Environment.OSVersion.Version} " +
                 $"{RuntimeInformation.OSArchitecture}{(sandbox is null ? "" : $"; sandbox {sandbox}")}");
        foreach (var unknown in options.Unknown)
            log.Warn($"Unknown argument ignored: {unknown}");
        foreach (var ignored in options.Ignored)
            log.Info($"Accepted, changes nothing: {ignored}");
        foreach (var task in SetupTasks.UnknownNames(options.Tasks).Concat(SetupTasks.UnknownNames(options.MergeTasks)))
            log.Warn($"Unknown task ignored: {task}");

        var texts = new SetupTexts(options.Language ?? "auto");
        try
        {
            if (!IsSupportedWindows())
            {
                log.Error("Windows is too old or not 64-bit");
                Tell(options, texts["installer.error.windows"], NativeMethods.MB_ICONERROR);
                return ExitInitFailed;
            }
            if (start.Mode == StartMode.Damaged)
            {
                log.Error($"Damaged payload: {start.Problem}");
                Tell(options, texts["installer.error.damaged"], NativeMethods.MB_ICONERROR);
                return ExitInitFailed;
            }
            if (start.Mode == StartMode.Incomplete)
            {
                log.Error($"Incomplete download: {start.Problem}");
                Tell(options, texts["installer.error.incomplete"], NativeMethods.MB_ICONERROR);
                return ExitInitFailed;
            }
            return uninstall
                ? Uninstall(args, options, exe, paths, sandbox, log, texts)
                : Install(args, options, exe, payload!, paths, sandbox, log, texts);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.Error($"Setup failed: {ex}");
            Tell(options, texts.Format("installer.error.unexpected", ex.Message), NativeMethods.MB_ICONERROR);
            return ExitFailed;
        }
        finally
        {
            payload?.Dispose();
            NativeLibraries.RemoveAfterExit(log);
        }
    }

    /// <summary>Windows 10 1809 or later, 64-bit (x64, or ARM64 running x64 programs).</summary>
    private static bool IsSupportedWindows() =>
        Environment.OSVersion.Version.Build >= SetupNames.MinimumBuild
        && RuntimeInformation.OSArchitecture is Architecture.X64 or Architecture.Arm64;

    private static int Install(string[] args, SetupOptions options, string exe, SetupPayload payload, SetupPaths paths, string? sandbox,
        SetupLog log, SetupTexts texts)
    {
        var scope = options.Scope;
        if (!options.Portable && sandbox is null && scope == InstallScope.Machine && !NativeMethods.IsElevated())
        {
            payload.Dispose();
            return RunElevated(exe, args, log, launchFile: Path.Combine(Path.GetTempPath(), $"Filee-Setup-{Guid.NewGuid():N}.launch"));
        }

        var context = CreateContext(sandbox, paths, log);
        var previous = options.Portable ? null : PreviousInstall.Find(context.Registry, scope);
        if (options.Language is null && previous?.Language is { } recorded)
            texts.SetLanguage(recorded);
        var folder = previous?.Folder
                     ?? (options.Directory is { } dir ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(dir)) : null)
                     ?? (options.Portable ? Path.Combine(Path.GetDirectoryName(exe)!, "Filee") : paths.DefaultInstallFolder(scope));
        if (previous is not null && options.Directory is { } asked
            && !Path.GetFullPath(Environment.ExpandEnvironmentVariables(asked)).TrimEnd('\\').Equals(previous.Folder, StringComparison.OrdinalIgnoreCase))
            log.Warn($"/DIR={asked} ignored: Filee is installed in {previous.Folder}, which an update keeps");

        var modernMenu = context.System.ExplorerMenuSupported && scope == InstallScope.Machine && !options.Portable;
        var tasks = previous is null
            ? SetupTasks.FromCommandLine(options.Tasks, options.MergeTasks)
            : UpdateTasks(previous.CurrentTasks(context.Registry, context.System, paths), options);
        if (!modernMenu)
            tasks = tasks with { ModernMenu = false };
        var marker = payload.Manifest.EngineMarker;
        var engines = previous is null
            ? payload.Manifest.Engines.Select(e => (e, EngineInstalled(e, paths, marker))).ToList()
            : [];
        var choices = new InstallChoices(folder, scope, tasks,
            engines.Where(e => e.e.Suggested && !e.Item2).Select(e => e.e.Id).ToList(),
            texts.Language, LanguagePicked: options.Language is not null,
            NoIcons: options.NoIcons, Portable: options.Portable, ForceClose: options.ForceCloseApplications);
        var runner = new SetupRunner(context, payload, previous);

        InstallResult? result;
        int exitCode;
        LaunchRequest? launch;
        if (options.Ui == SetupUi.VerySilent)
        {
            result = runner.Install(choices, new SyncProgress<SetupProgress>(_ => { }));
            exitCode = result.Succeeded ? ExitOk : ExitFailed;
            launch = result.Launch;
        }
        else
        {
            var model = new SetupViewModel(texts, runner, payload.Manifest.Version, choices, previous, engines, modernMenu,
                payload.Manifest.TotalSize + payload.Offset, languagePicked: options.Language is not null)
            {
                LogFile = log.File,
            };
            RunWindow(model, texts, options.Ui == SetupUi.Silent, paths, log);
            result = model.Result;
            exitCode = model.ExitCode;
            launch = options.Ui == SetupUi.Silent ? result?.Launch : model.Launch;
        }

        if (result is { Succeeded: false } failed && options.IsSilent)
        {
            Tell(options, SetupMessages.Error(texts, failed.Error!) + Environment.NewLine + Environment.NewLine + log.File, NativeMethods.MB_ICONERROR);
        }
        if (result is { Succeeded: true } && options.Ui == SetupUi.Silent)
        {
            // /SILENT shows its window anyway: say what doesn't work. (The top-level menu, like before, only in the log.)
            foreach (var warning in result.Warnings.OfType<RuntimeWarning>())
                Tell(options, SetupMessages.Warning(texts, warning), NativeMethods.MB_ICONWARNING);
        }
        if (result is { Succeeded: true } && launch is not null)
        {
            if (options.NoLaunch || sandbox is not null)
                log.Info($"Not starting Filee ({(sandbox is null ? "/NOLAUNCH" : "sandbox")}): {launch.Executable} {launch.Arguments}");
            else
                StartFilee(launch, options.LaunchFile, log);
        }
        log.Info($"Exit code {exitCode}");
        return exitCode;
    }

    /// <summary>An update keeps the current state; /TASKS and /MERGETASKS may still change the PATH entry and the desktop icon.</summary>
    private static SetupTasks UpdateTasks(SetupTasks current, SetupOptions options)
    {
        if (options.Tasks is null && options.MergeTasks is null)
            return current;
        var asked = options.Tasks is null
            ? SetupTasks.FromCommandLine(current.ToInnoString(), options.MergeTasks)
            : SetupTasks.FromCommandLine(options.Tasks, options.MergeTasks);
        return current with { Cli = asked.Cli, DesktopIcon = asked.DesktopIcon };
    }

    private static bool EngineInstalled(EngineOffer engine, SetupPaths paths, string marker) =>
        marker.Length > 0 && engine.MarkerFolders.Count > 0
        && engine.MarkerFolders.All(folder => File.Exists(Path.Combine(paths.EnginesFolder, folder, marker)));

    private static int Uninstall(string[] args, SetupOptions options, string exe, SetupPaths paths, string? sandbox, SetupLog log, SetupTexts texts)
    {
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Directory ?? Path.GetDirectoryName(exe)!));
        var record = InstallRecord.Read(folder);
        if (record is null)
        {
            log.Error($"No {SetupNames.InstallRecordFile} in {folder}");
            Tell(options, texts.Format("installer.uninstall.not_installed", folder), NativeMethods.MB_ICONERROR);
            return ExitInitFailed;
        }
        var context = CreateContext(sandbox, paths, log, seeOtherAccounts: true);
        var hive = PreviousInstall.HiveOf(record.Scope);
        if (options.Language is null && context.Registry.Get(hive, SetupNames.UninstallKey, SetupNames.LanguageValue)?.Text is { } language)
            texts.SetLanguage(language);
        var needsAdmin = record.Scope == InstallScope.Machine && sandbox is null && !NativeMethods.IsElevated();

        // Phase 1: running from the install folder, which can't be deleted while this runs. Copy out of it and wait.
        var exeFolder = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(exe)!);
        if (exeFolder.Equals(folder, StringComparison.OrdinalIgnoreCase))
        {
            // Administrator rights first, for this file: only administrators can change an install for all users, so
            // the prompt is never answered for a file another program of this user has put there. The elevated one
            // makes the copy, and its copy waits for this one to end too: both run from the uninstall.exe it deletes.
            if (needsAdmin)
                return RunElevated(exe, [.. args.Where(a => !HasName(a, "/FIRSTPHASE")), $"/FIRSTPHASE={Environment.ProcessId}"], log, launchFile: null);
            var copy = CopyUninstaller(exe, paths, NativeMethods.IsElevated());
            var temp = Path.GetDirectoryName(copy)!;
            log.Info($"Continuing from {copy}");
            // Waits for the copy, or for its word that only uninstall.exe is left (it then deletes it).
            var released = Path.Combine(temp, ReleasedFile);
            int code;
            // Held open without write or delete sharing while it starts, on top of the folder's rights: nothing can
            // change the copy between here and its start.
            using (new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read))
                code = RunAndWait(copy, SecondPhaseArgs(args, folder, log.File, [Environment.ProcessId, .. options.FirstPhase]), elevated: false, log,
                    until: () => File.Exists(released));
            if (code == ExitCancelled)
                TryDeleteFolder(temp);
            return code;
        }
        if (needsAdmin)
            return RunElevated(exe, args, log, launchFile: null);

        // Phase 2: from the copy.
        var fromTemp = Path.GetFileName(exeFolder).StartsWith(CopyFolderPrefix, StringComparison.OrdinalIgnoreCase);
        // In the copy's folder, the files still in use are out of reach for standard users too until the restart.
        var trash = fromTemp ? Path.Combine(exeFolder, "trash") : Path.Combine(paths.Temp, $"{CopyFolderPrefix}{Guid.NewGuid():N}");
        var runner = new SetupRunner(context, null, null, folder, record, trash)
        {
            ReleaseFirstPhase = options.FirstPhase.Count > 0 && fromTemp ? () => ReleaseFirstPhase(exeFolder, options.FirstPhase, log) : null,
        };
        int exitCode;
        if (options.Ui == SetupUi.VerySilent)
        {
            exitCode = runner.Uninstall(deleteSettings: false, new SyncProgress<SetupProgress>(_ => { })).Error is { } error
                ? Fail(error)
                : ExitOk;
        }
        else
        {
            var model = new SetupViewModel(texts, runner, record.Version, new InstallChoices(folder, record.Scope, SetupTasks.Defaults, [], texts.Language),
                null, [], false, 0, uninstall: true)
            {
                LogFile = log.File,
                SettingsFolder = paths.SettingsFolder,
            };
            RunWindow(model, texts, options.Ui == SetupUi.Silent, paths, log);
            exitCode = model.ExitCode;
            if (exitCode == 1 && options.IsSilent)
                Tell(options, model.ErrorText, NativeMethods.MB_ICONERROR);
        }
        // This copy and what was in use go once it has ended (and at the next restart, to be sure).
        CleanUpAfterExit(fromTemp ? exeFolder : trash, context.System, log);
        log.Info($"Exit code {exitCode}");
        return exitCode;

        int Fail(Exception error)
        {
            Tell(options, SetupMessages.Error(texts, error), NativeMethods.MB_ICONERROR);
            return ExitInitFailed;
        }
    }

    /// <summary>Written by the uninstaller's copy when only uninstall.exe is left (see <see cref="Uninstaller.Run"/>).</summary>
    private const string ReleasedFile = "released";

    /// <summary>The name of the uninstaller copy's folder, followed by a new GUID: the copy knows itself by it.</summary>
    internal const string CopyFolderPrefix = "Filee-Uninstall-";

    /// <summary>
    /// The rights of an elevated uninstaller's copy folder: SYSTEM and administrators only, nothing inherited from the
    /// parent (in %SystemRoot%\Temp users may add files to what is below). The OWNER RIGHTS entry takes away what an
    /// owner may otherwise always do, change these rights, should Windows make the signed-in user the owner rather
    /// than Administrators.
    /// </summary>
    internal const string AdminOnlyFolder = "D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;OW)";

    /// <summary>
    /// Copies the uninstaller into a new folder and returns the copy. An elevated one goes to %SystemRoot%\Temp with
    /// <see cref="AdminOnlyFolder"/>: in the user's %TEMP% any program of theirs could swap the copy before it starts
    /// with administrator rights, and even a folder they can't open could be renamed away and replaced there, since
    /// %TEMP% lets them delete what is in it. Without administrator rights there is nothing to guard.
    /// </summary>
    internal static string CopyUninstaller(string exe, SetupPaths paths, bool elevated)
    {
        var parent = elevated ? paths.SystemTemp : paths.Temp;
        Directory.CreateDirectory(parent);
        var temp = Path.Combine(parent, $"{CopyFolderPrefix}{Guid.NewGuid():N}");
        NativeMethods.CreateNewFolder(temp, elevated ? AdminOnlyFolder : null);
        var copy = Path.Combine(temp, SetupNames.UninstallerExe);
        File.Copy(exe, copy);
        return copy;
    }

    /// <summary>The copy's command line. <paramref name="firstPhase"/>: the uninstallers in the install folder that wait for it.</summary>
    internal static IEnumerable<string> SecondPhaseArgs(string[] args, string folder, string? logFile, IEnumerable<int> firstPhase) =>
        args.Where(a => !HasName(a, "/DIR") && !HasName(a, "/LOG") && !HasName(a, "/UNINSTALL") && !HasName(a, "/FIRSTPHASE"))
            .Concat(["/UNINSTALL", $"/DIR={folder}", $"/LOG={logFile}", $"/FIRSTPHASE={string.Join(',', firstPhase)}"]);

    /// <summary>
    /// Tells the uninstallers in the install folder that they may end, and waits until they have: the elevated one
    /// ends on the word, and then the unelevated one that waited for it.
    /// </summary>
    private static void ReleaseFirstPhase(string temp, IReadOnlyList<int> processIds, SetupLog log)
    {
        // Looked up before the word, while they all still run and wait for it.
        var first = new List<Process>();
        foreach (var id in processIds)
        {
            try
            {
                first.Add(Process.GetProcessById(id));
            }
            catch (ArgumentException)
            {
                // Ended already.
            }
        }
        try
        {
            if (first.Count == 0)
                return;
            File.WriteAllText(Path.Combine(temp, ReleasedFile), "0");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            foreach (var process in first)
            {
                try
                {
                    var left = deadline - DateTime.UtcNow;
                    if (!process.WaitForExit(left > TimeSpan.Zero ? left : TimeSpan.Zero))
                        log.Warn($"The first uninstaller (process {process.Id}) did not end");
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    log.Warn($"Could not wait for the first uninstaller (process {process.Id}): {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"Could not release the first uninstaller: {ex.Message}");
        }
        finally
        {
            foreach (var process in first)
                process.Dispose();
        }
    }

    private static SetupContext CreateContext(string? sandbox, SetupPaths paths, SetupLog log, bool seeOtherAccounts = false)
    {
        if (sandbox is null)
            return new SetupContext(new WindowsRegistry(), new WindowsSetupSystem(log, seeOtherAccounts), paths, log);
        // The sandbox's registry is a JSON file; Filee runs with its data in the sandbox.
        return new SetupContext(new MemoryRegistry(Path.Combine(sandbox, "registry.json")),
            new SandboxSetupSystem(log, paths.SettingsFolder), paths, log, Sandbox: true);
    }

    private static void RunWindow(SetupViewModel model, SetupTexts texts, bool silent, SetupPaths paths, SetupLog log)
    {
        // Skia and HarfBuzz: from a folder of this run's own, where only administrators can change them when this one
        // is elevated.
        NativeLibraries.Unpack(paths, NativeMethods.IsElevated(), log);
        App.Model = model;
        App.Texts = texts;
        App.Silent = silent;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
    }

    /// <summary>
    /// Also used by the XAML previewer (the tests have their own, headless, builder). Software rendering: Setup's one
    /// small window doesn't need the GPU, and without ANGLE there is no av_libglesv2.dll to carry (see
    /// NativeLibraries). The redirection surface keeps the window's transparency.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.Software],
                CompositionMode = [Win32CompositionMode.RedirectionSurface],
            })
            .LogToTrace();

    /// <summary>
    /// Starts this program again with administrator rights and waits. With <paramref name="launchFile"/>, starts what
    /// the elevated copy wrote into it afterwards, as the signed-in user.
    /// </summary>
    private static int RunElevated(string exe, string[] args, SetupLog log, string? launchFile)
    {
        var childArgs = args.Where(a => !HasName(a, "/LAUNCHFILE") && !HasName(a, "/LOG"))
            .Concat(launchFile is null ? [] : [$"/LAUNCHFILE={launchFile}"])
            .Append($"/LOG={log.File}");
        log.Info("Asking for administrator rights");
        var code = RunAndWait(exe, childArgs, elevated: true, log);
        if (launchFile is not null && File.Exists(launchFile))
        {
            try
            {
                var lines = File.ReadAllLines(launchFile);
                File.Delete(launchFile);
                if (lines.Length >= 1 && Path.GetFileName(lines[0]).Equals(SetupNames.AppExe, StringComparison.OrdinalIgnoreCase) && File.Exists(lines[0]))
                    Start(new LaunchRequest(lines[0], lines.Length > 1 ? lines[1] : ""), log);
                else
                    log.Warn($"Not starting what {launchFile} names");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Warn($"Could not read {launchFile}: {ex.Message}");
            }
        }
        return code;
    }

    /// <param name="until">Stops waiting early when true (checked every 100 ms); the exit code is then 0.</param>
    private static int RunAndWait(string exe, IEnumerable<string> args, bool elevated, SetupLog log, Func<bool>? until = null)
    {
        // Only the prompt for administrator rights needs the shell. Otherwise CreateProcess starts exactly this file:
        // the shell would look up how to open it, also in the user's own part of the registry, which an elevated
        // uninstaller must not follow.
        var info = new ProcessStartInfo(exe, SetupOptions.Join(args))
        {
            UseShellExecute = elevated,
            WorkingDirectory = Environment.SystemDirectory,
        };
        if (elevated)
            info.Verb = "runas";
        try
        {
            using var child = Process.Start(info);
            if (child is null)
                return ExitInitFailed;
            while (!child.WaitForExit(100))
            {
                if (until?.Invoke() == true)
                {
                    log.Info($"{Path.GetFileName(exe)} is done with this one");
                    return ExitOk;
                }
            }
            log.Info($"{Path.GetFileName(exe)} ended with exit code {child.ExitCode}");
            return child.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            log.Info("Administrator rights declined");
            return ExitCancelled;
        }
        catch (Win32Exception ex)
        {
            log.Error($"Could not start {exe}: {ex.Message}");
            return ExitInitFailed;
        }
    }

    private static bool HasName(string arg, string name) =>
        arg.Equals(name, StringComparison.OrdinalIgnoreCase) || arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Starts Filee as the signed-in user: through the unelevated Setup that started this one (the launch file), else
    /// with Explorer's token when this Setup is elevated, else directly.
    /// </summary>
    private static void StartFilee(LaunchRequest launch, string? launchFile, SetupLog log)
    {
        if (launchFile is not null)
        {
            try
            {
                File.WriteAllLines(launchFile, [launch.Executable, launch.Arguments]);
                log.Info($"Filee starts from the unelevated Setup: {launch.Arguments}");
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Warn($"Could not write {launchFile}: {ex.Message}");
            }
        }
        if (NativeMethods.IsElevated())
        {
            if (NativeMethods.StartAsShellUser(launch.Executable, launch.Arguments, Path.GetDirectoryName(launch.Executable)!, out var error))
            {
                log.Info($"Started Filee as the signed-in user: {launch.Arguments}");
                return;
            }
            // Filee notices it runs elevated and offers to restart without administrator rights.
            log.Warn($"Could not start Filee as the signed-in user ({error}); starting it with Setup's rights");
        }
        Start(launch, log);
    }

    private static void Start(LaunchRequest launch, SetupLog log)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(launch.Executable, launch.Arguments)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(launch.Executable)!,
            });
            log.Info($"Started Filee: {launch.Arguments}");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            log.Warn($"Could not start Filee: {ex.Message}");
        }
    }

    /// <summary>Deletes <paramref name="folder"/> (the uninstaller's copy and files that were in use) once this process has ended.</summary>
    private static void CleanUpAfterExit(string folder, ISetupSystem system, SetupLog log)
    {
        if (!Directory.Exists(folder))
            return;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            system.DeleteAtRestart(file);
        system.DeleteAtRestart(folder);
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                $"/d /c ping -n 4 127.0.0.1 >nul & rd /s /q \"{folder}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.SystemDirectory,
            });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            log.Warn($"Could not clean up {folder}: {ex.Message}");
        }
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A message box, unless /SUPPRESSMSGBOXES (the log has everything anyway).</summary>
    private static void Tell(SetupOptions options, string text, uint icon)
    {
        if (options.CanShowMessages)
            NativeMethods.MessageBox(0, text, "Filee Setup", NativeMethods.MB_OK | icon | NativeMethods.MB_SETFOREGROUND);
    }
}

/// <summary>The real installer and uninstaller behind the window.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class SetupRunner(SetupContext context, SetupPayload? payload, PreviousInstall? previous,
    string? folder = null, InstallRecord? record = null, string? trash = null) : ISetupRunner
{
    public InstallResult Install(InstallChoices choices, IProgress<SetupProgress> progress) =>
        new Installer(context, payload ?? throw new InvalidOperationException("No payload.")).Run(choices, previous, progress);

    /// <summary>Lets the uninstaller in the install folder end before its file is deleted.</summary>
    public Action? ReleaseFirstPhase { get; init; }

    public UninstallResult Uninstall(bool deleteSettings, IProgress<SetupProgress> progress)
    {
        try
        {
            var uninstaller = new Uninstaller(context);
            var later = uninstaller.Run(folder!, record!, deleteSettings, trash!, progress, ReleaseFirstPhase);
            return new UninstallResult(DeletedAtRestart: later.Count, CleanupFailed: uninstaller.CleanupFailed, FilesLeftIn: uninstaller.FilesLeftIn);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            context.Log.Error($"Uninstall failed: {ex}");
            return new UninstallResult(ex);
        }
    }
}
