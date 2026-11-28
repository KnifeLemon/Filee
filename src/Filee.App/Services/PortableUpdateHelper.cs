// Transactional file replacement helper for Windows portable in-app updates.
// Runs as a lightweight subprocess (staging/Filee.exe --update-swap ...) without UI, Avalonia,
// or PowerShell script execution dependencies.

using System.Diagnostics;

namespace Filee.App.Services;

internal static class PortableUpdateHelper
{
    internal static Func<ProcessStartInfo, Process?> StartProcess { get; set; } = psi => Process.Start(psi);

    public static int Run(
        UpdateSwapOptions options,
        Func<ProcessStartInfo, bool>? processStarter = null,
        string? processPath = null)
    {
        var targetPid = options.TargetPid;
        var targetDir = options.TargetDir;
        var stagingDir = options.StagingDir;
        var logFile = options.LogFile;
        var markerFile = options.MarkerFile;

        if (!Directory.Exists(targetDir) || !Directory.Exists(stagingDir))
        {
            AppendLog(logFile, "Update aborted: target or staging directory does not exist");
            return 1;
        }

        var effectiveProcessPath = processPath ?? Environment.ProcessPath;
        if (!IsPathInsideDirectory(effectiveProcessPath, stagingDir))
        {
            AppendLog(logFile, $"Update aborted: helper must run from staging directory '{stagingDir}'");
            return 1;
        }

        var targetExe = Path.Combine(targetDir, "Filee.exe");
        var portableMarker = Path.Combine(targetDir, "filee.portable");
        if (!File.Exists(targetExe) || !File.Exists(portableMarker))
        {
            AppendLog(logFile, $"Update aborted: target directory '{targetDir}' is missing Filee.exe or filee.portable");
            return 1;
        }

        // 1. Wait for Filee (targetPid) to exit within 60 seconds
        var sw = Stopwatch.StartNew();
        while (UpdateService.IsProcessAlive(targetPid) && sw.Elapsed < TimeSpan.FromSeconds(60))
        {
            Thread.Sleep(100);
        }

        if (UpdateService.IsProcessAlive(targetPid))
        {
            AppendLog(logFile, $"Update aborted: Filee (PID {targetPid}) did not exit within timeout");
            return 1;
        }

        var backupDir = Path.Combine(targetDir, $".backup-{targetPid}");
        var movedFiles = new List<(string OriginalPath, string BackupPath)>();
        var movedDirs = new List<(string OriginalPath, string BackupPath)>();
        var copiedFiles = new List<string>();
        var copiedDirs = new List<string>();

        try
        {
            // 2. Move aside only top-level files and folders that the new payload also has,
            //    leaving user files (e.g. when unpacked in Downloads/Desktop) and 'data' untouched.
            if (Directory.Exists(backupDir))
            {
                Directory.Delete(backupDir, recursive: true);
            }
            Directory.CreateDirectory(backupDir);

            var stagingInfo = new DirectoryInfo(stagingDir);
            foreach (var payloadFile in stagingInfo.EnumerateFiles())
            {
                var targetFile = Path.Combine(targetDir, payloadFile.Name);
                if (File.Exists(targetFile))
                {
                    var backupPath = Path.Combine(backupDir, payloadFile.Name);
                    File.Move(targetFile, backupPath, overwrite: true);
                    movedFiles.Add((targetFile, backupPath));
                }
            }
            foreach (var payloadDir in stagingInfo.EnumerateDirectories())
            {
                if (payloadDir.Name.Equals("data", StringComparison.OrdinalIgnoreCase)
                    || payloadDir.Name.Equals(Path.GetFileName(backupDir), StringComparison.OrdinalIgnoreCase)
                    || payloadDir.Name.StartsWith(".update-", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var targetSubDir = Path.Combine(targetDir, payloadDir.Name);
                if (Directory.Exists(targetSubDir))
                {
                    var backupPath = Path.Combine(backupDir, payloadDir.Name);
                    Directory.Move(targetSubDir, backupPath);
                    movedDirs.Add((targetSubDir, backupPath));
                }
            }

            // 3. Copy new files from stagingDir to targetDir
            CopyDirectory(stagingDir, targetDir, copiedFiles, copiedDirs);

            // 4. Restart updated Filee; keep backupDir until the new version starts and cleans it up.
            var launchPsi = new ProcessStartInfo(targetExe)
            {
                UseShellExecute = true,
                ArgumentList = { "--background", "--whats-new" }
            };
            if (processStarter is not null)
            {
                processStarter(launchPsi);
            }
            else
            {
                StartProcess(launchPsi);
            }
            return 0;
        }
        catch (Exception ex)
        {
            AppendLog(logFile, $"Update failed: {ex.Message}");

            // Rollback: remove only files/folders we copied in, then restore only what we moved to backupDir
            try
            {
                foreach (var copiedFile in copiedFiles)
                {
                    try
                    {
                        if (File.Exists(copiedFile))
                            File.Delete(copiedFile);
                    }
                    catch { }
                }

                for (var i = copiedDirs.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        if (Directory.Exists(copiedDirs[i]))
                            Directory.Delete(copiedDirs[i], recursive: false);
                    }
                    catch { }
                }

                var allRestored = true;
                foreach (var (originalPath, backupPath) in movedFiles)
                {
                    try
                    {
                        if (File.Exists(backupPath))
                            File.Move(backupPath, originalPath, overwrite: true);
                    }
                    catch (Exception restoreEx)
                    {
                        allRestored = false;
                        AppendLog(logFile, $"Rollback failed to restore file '{originalPath}': {restoreEx.Message}");
                    }
                }

                foreach (var (originalPath, backupPath) in movedDirs)
                {
                    try
                    {
                        if (Directory.Exists(backupPath))
                            Directory.Move(backupPath, originalPath);
                    }
                    catch (Exception restoreEx)
                    {
                        allRestored = false;
                        AppendLog(logFile, $"Rollback failed to restore directory '{originalPath}': {restoreEx.Message}");
                    }
                }

                if (allRestored && Directory.Exists(backupDir))
                {
                    try { Directory.Delete(backupDir, recursive: true); } catch { }
                }

                var markerTmp = markerFile + ".tmp";
                File.WriteAllText(markerTmp, logFile);
                File.Move(markerTmp, markerFile, overwrite: true);

                if (File.Exists(targetExe))
                {
                    var rollbackPsi = new ProcessStartInfo(targetExe)
                    {
                        UseShellExecute = true,
                        ArgumentList = { "--background" }
                    };
                    if (processStarter is not null)
                    {
                        processStarter(rollbackPsi);
                    }
                    else
                    {
                        StartProcess(rollbackPsi);
                    }
                }
            }
            catch (Exception rollbackEx)
            {
                AppendLog(logFile, $"Rollback failed: {rollbackEx.Message}");
            }

            return 1;
        }
    }

    internal static void CopyDirectory(
        string sourceDir,
        string targetDir,
        ICollection<string>? copiedFiles = null,
        ICollection<string>? copiedDirs = null)
    {
        if (!Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
            copiedDirs?.Add(targetDir);
        }

        var sourceInfo = new DirectoryInfo(sourceDir);

        foreach (var file in sourceInfo.EnumerateFiles())
        {
            var destFile = Path.Combine(targetDir, file.Name);
            copiedFiles?.Add(destFile);
            file.CopyTo(destFile, overwrite: true);
        }

        foreach (var subDir in sourceInfo.EnumerateDirectories())
        {
            CopyDirectory(subDir.FullName, Path.Combine(targetDir, subDir.Name), copiedFiles, copiedDirs);
        }
    }

    private static bool IsPathInsideDirectory(string? filePath, string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var fullDir = Path.GetFullPath(directoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullFile = Path.GetFullPath(filePath);
        return fullFile.StartsWith(fullDir, comparison);
    }

    internal static void LogAborted(string? logFile, string message)
    {
        Console.Error.WriteLine(message);
        if (!string.IsNullOrWhiteSpace(logFile))
            AppendLog(logFile, message);
    }

    private static void AppendLog(string logFile, string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(logFile);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Best effort logging
        }
    }
}
