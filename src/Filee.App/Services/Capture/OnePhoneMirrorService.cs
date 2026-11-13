using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Filee.App.Services.Capture;

/// <summary>
/// Manages the 1PhoneMirror native mobile screen mirroring receiver (MSEndpointMgr/1PhoneMirror).
/// Automatically downloads and validates the release binary via SHA-256 signature when missing.
/// </summary>
public sealed class OnePhoneMirrorService
{
    public const string DownloadUrl = "https://github.com/MSEndpointMgr/1PhoneMirror/releases/download/v0.6.2/1PhoneMirror-0.6.2.msi";
    public const string ExpectedSha256 = "5962A5FDD00B1D1BE52691559EABEC06002F8AF2B688FF05EB535C394BFF2E00";
    public const long ExpectedSize = 21016576;

    private readonly ILogger<OnePhoneMirrorService>? _logger;
    private Process? _activeProcess;

    public OnePhoneMirrorService(ILogger<OnePhoneMirrorService>? logger = null)
    {
        _logger = logger;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SwRestore = 9;

    /// <summary>
    /// Searches for an existing 1PhoneMirror.exe executable on the machine.
    /// </summary>
    public static string? Find1PhoneMirrorExe()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        string[] candidates =
        [
            Path.Combine(localAppData, "Filee", "engines", "1phonemirror", "1PhoneMirror.exe"),
            Path.Combine(localAppData, "Filee", "engines", "1phonemirror", "PFiles64", "1PhoneMirror", "1PhoneMirror.exe"),
            Path.Combine(programFiles, "1PhoneMirror", "1PhoneMirror.exe"),
            Path.Combine(programFilesX86, "1PhoneMirror", "1PhoneMirror.exe"),
            Path.Combine(localAppData, "Programs", "1PhoneMirror", "1PhoneMirror.exe"),
            Path.Combine(appData, "1PhoneMirror", "1PhoneMirror.exe"),
            @"C:\Program Files\1PhoneMirror\1PhoneMirror.exe"
        ];

        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir, "1PhoneMirror.exe");
                if (File.Exists(p)) return p;
            }
            catch { }
        }

        return null;
    }

    public static bool BinaryExists => !string.IsNullOrEmpty(Find1PhoneMirrorExe());

    /// <summary>
    /// Downloads 1PhoneMirror MSI from the official GitHub repository, validates its SHA-256 signature,
    /// and extracts it cleanly into the Filee engines directory without requiring elevation.
    /// </summary>
    public async Task<string> DownloadAndInstallAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var targetEngineDir = Path.Combine(localAppData, "Filee", "engines", "1phonemirror");
        var targetExe = Path.Combine(targetEngineDir, "1PhoneMirror.exe");

        if (File.Exists(targetExe))
            return targetExe;

        var tempMsi = Path.Combine(Path.GetTempPath(), $"1PhoneMirror-0.6.2-{Guid.NewGuid():N}.msi");
        var stagingDir = Path.Combine(Path.GetTempPath(), $"1PhoneExtract_{Guid.NewGuid():N}");

        try
        {
            progress?.Report("Downloading 1Phone Mirror from GitHub...");
            _logger?.LogInformation("Downloading 1PhoneMirror from {Url}", DownloadUrl);

            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromMinutes(5);
                using var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                using var fileStream = new FileStream(tempMsi, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                await response.Content.CopyToAsync(fileStream, ct).ConfigureAwait(false);
            }

            progress?.Report("Validating SHA-256 signature...");
            _logger?.LogInformation("Validating SHA-256 hash for {File}", tempMsi);

            using (var sha256 = SHA256.Create())
            using (var stream = File.OpenRead(tempMsi))
            {
                var hashBytes = await sha256.ComputeHashAsync(stream, ct).ConfigureAwait(false);
                var computedHash = Convert.ToHexString(hashBytes);

                if (!string.Equals(computedHash, ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"1PhoneMirror SHA-256 signature validation failed. Expected: {ExpectedSha256}, Got: {computedHash}");
                }
            }

            _logger?.LogInformation("SHA-256 signature verified successfully");
            progress?.Report("Extracting 1Phone Mirror files...");

            Directory.CreateDirectory(stagingDir);
            var psi = new ProcessStartInfo("msiexec.exe", $"/a \"{tempMsi}\" /qn TARGETDIR=\"{stagingDir}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var proc = Process.Start(psi))
            {
                if (proc == null)
                    throw new InvalidOperationException("Failed to launch msiexec for 1PhoneMirror extraction.");

                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                if (proc.ExitCode != 0)
                {
                    throw new InvalidOperationException($"msiexec extraction failed with exit code {proc.ExitCode}");
                }
            }

            // Locate 1PhoneMirror folder inside extracted structure (usually under PFiles64\1PhoneMirror)
            var extractedExes = Directory.GetFiles(stagingDir, "1PhoneMirror.exe", SearchOption.AllDirectories);
            if (extractedExes.Length == 0)
            {
                throw new FileNotFoundException("1PhoneMirror.exe not found in extracted package.");
            }

            var sourceDir = Path.GetDirectoryName(extractedExes[0])!;
            Directory.CreateDirectory(targetEngineDir);

            // Copy all extracted files and subdirectories to targetEngineDir
            CopyDirectory(sourceDir, targetEngineDir);

            _logger?.LogInformation("1PhoneMirror installed successfully into {Dir}", targetEngineDir);
            return targetExe;
        }
        finally
        {
            try { if (File.Exists(tempMsi)) File.Delete(tempMsi); } catch { }
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); } catch { }
        }
    }

    /// <summary>
    /// Launches 1PhoneMirror. If already running, brings its window to the foreground.
    /// If not installed, automatically downloads and validates it before launching.
    /// </summary>
    public async Task<bool> LaunchOrFocusAsync(Action<string>? statusCallback = null, CancellationToken ct = default)
    {
        // 1. If already running, focus the existing window
        var running = Process.GetProcessesByName("1PhoneMirror");
        if (running.Length > 0)
        {
            foreach (var p in running)
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero)
                    {
                        ShowWindow(p.MainWindowHandle, SwRestore);
                        SetForegroundWindow(p.MainWindowHandle);
                        statusCallback?.Invoke("1Phone Mirror is active");
                        return true;
                    }
                }
                catch { }
            }
            statusCallback?.Invoke("1Phone Mirror is running");
            return true;
        }

        // 2. Find binary or download & validate via SHA-256
        var exe = Find1PhoneMirrorExe();
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            statusCallback?.Invoke("Downloading 1Phone Mirror...");
            var progress = new Progress<string>(msg => statusCallback?.Invoke(msg));
            exe = await DownloadAndInstallAsync(progress, ct).ConfigureAwait(false);
        }

        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            throw new FileNotFoundException("1PhoneMirror.exe could not be found or downloaded.");
        }

        // 3. Launch 1PhoneMirror process
        statusCallback?.Invoke("Opening 1Phone Mobile Mirror...");
        _logger?.LogInformation("Launching 1PhoneMirror from {Path}", exe);

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            UseShellExecute = true
        };

        var proc = Process.Start(psi);
        _activeProcess = proc;
        return proc != null;
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var dest = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, dest, true);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var destSub = Path.Combine(targetDir, Path.GetFileName(subDir));
            Directory.CreateDirectory(destSub);
            CopyDirectory(subDir, destSub);
        }
    }
}
