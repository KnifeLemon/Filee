// Transactional file replacement helper for Windows portable in-app updates.
// Runs as a lightweight subprocess (staging/Filee.exe --update-swap ...) without UI, Avalonia,
// or PowerShell script execution dependencies.

using System.Diagnostics;

namespace Filee.App.Services;

internal static class PortableUpdateHelper
{
    internal static Func<ProcessStartInfo, Process?> StartProcess { get; set; } = psi => Process.Start(psi);

    public static int Run(UpdateSwapOptions options, Func<ProcessStartInfo, bool>? processStarter = null)
    {
        var targetPid = options.TargetPid;
        var targetDir = options.TargetDir;
        var stagingDir = options.StagingDir;
        var logFile = options.LogFile;
        var markerFile = options.MarkerFile;

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
        var targetExe = Path.Combine(targetDir, "Filee.exe");

        try
        {
            // 2. Move existing files into backupDir on the same volume (atomic rename), skipping user 'data'
            if (Directory.Exists(backupDir))
            {
                Directory.Delete(backupDir, recursive: true);
            }
            Directory.CreateDirectory(backupDir);

            var dirInfo = new DirectoryInfo(targetDir);
            foreach (var file in dirInfo.EnumerateFiles())
            {
                File.Move(file.FullName, Path.Combine(backupDir, file.Name), overwrite: true);
            }
            foreach (var subDir in dirInfo.EnumerateDirectories())
            {
                if (subDir.Name.Equals("data", StringComparison.OrdinalIgnoreCase)
                    || subDir.Name.Equals(Path.GetFileName(backupDir), StringComparison.OrdinalIgnoreCase)
                    || subDir.Name.StartsWith(".update-", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                Directory.Move(subDir.FullName, Path.Combine(backupDir, subDir.Name));
            }

            // 3. Copy new files from stagingDir to targetDir
            CopyDirectory(stagingDir, targetDir);

            // 4. Update succeeded! Clean up backupDir
            try
            {
                Directory.Delete(backupDir, recursive: true);
            }
            catch (Exception ex)
            {
                AppendLog(logFile, $"Warning: could not delete backup directory: {ex.Message}");
            }

            // Restart updated Filee
            var launchPsi = new ProcessStartInfo(targetExe)
            {
                UseShellExecute = true,
                Arguments = "--background --whats-new"
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

            // Rollback: delete partial staging copies and restore from backupDir
            try
            {
                var dirInfo = new DirectoryInfo(targetDir);
                foreach (var file in dirInfo.EnumerateFiles())
                {
                    try { file.Delete(); } catch { }
                }
                foreach (var subDir in dirInfo.EnumerateDirectories())
                {
                    if (subDir.Name.Equals("data", StringComparison.OrdinalIgnoreCase)
                        || subDir.Name.Equals(Path.GetFileName(backupDir), StringComparison.OrdinalIgnoreCase)
                        || subDir.Name.StartsWith(".update-", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    try { subDir.Delete(recursive: true); } catch { }
                }

                if (Directory.Exists(backupDir))
                {
                    var backupInfo = new DirectoryInfo(backupDir);
                    foreach (var file in backupInfo.EnumerateFiles())
                    {
                        File.Move(file.FullName, Path.Combine(targetDir, file.Name), overwrite: true);
                    }
                    foreach (var subDir in backupInfo.EnumerateDirectories())
                    {
                        Directory.Move(subDir.FullName, Path.Combine(targetDir, subDir.Name));
                    }
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
                        Arguments = "--background"
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

    internal static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        var sourceInfo = new DirectoryInfo(sourceDir);

        foreach (var file in sourceInfo.EnumerateFiles())
        {
            file.CopyTo(Path.Combine(targetDir, file.Name), overwrite: true);
        }

        foreach (var subDir in sourceInfo.EnumerateDirectories())
        {
            CopyDirectory(subDir.FullName, Path.Combine(targetDir, subDir.Name));
        }
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
