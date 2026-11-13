using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Filee.App.Services.Capture;

public sealed class AndroidDeviceInfo(string serial, string model, string state)
{
    public string Serial { get; } = serial;
    public string Model { get; } = model;
    public string State { get; } = state;
    public string DisplayName => string.IsNullOrWhiteSpace(Model) ? Serial : $"{Model} ({Serial})";
}

public sealed class AndroidMirrorEngine
{
    private readonly ILogger? _logger;
    private Process? _adbProcess;
    private Process? _ffmpegProcess;
    private CancellationTokenSource? _mirrorCts;

    public bool IsActive => _mirrorCts != null && !_mirrorCts.IsCancellationRequested;

    public AndroidMirrorEngine(ILogger? logger = null)
    {
        _logger = logger;
    }

    public static string? FindAdb()
    {
        // 1. Check Android SDK default locations
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            @"D:\android_sdk\Android\platform-tools\adb.exe",
            Path.Combine(localAppData, "Android", "Sdk", "platform-tools", "adb.exe"),
            Path.Combine(localAppData, "Android", "platform-tools", "adb.exe"),
            Path.Combine(AppContext.BaseDirectory, "engines", "adb", "adb.exe"),
            Path.Combine(AppContext.BaseDirectory, "adb.exe")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        // 2. Check PATH environment variable
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            var exeName = OperatingSystem.IsWindows() ? "adb.exe" : "adb";
            foreach (var part in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = Path.Combine(part.Trim(), exeName);
                if (File.Exists(p))
                    return p;
            }
        }

        return "adb";
    }

    public async Task<IReadOnlyList<AndroidDeviceInfo>> GetConnectedDevicesAsync()
    {
        var devices = new List<AndroidDeviceInfo>();
        var adb = FindAdb();
        if (string.IsNullOrWhiteSpace(adb))
            return devices;

        try
        {
            var psi = new ProcessStartInfo(adb, ["devices", "-l"])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return devices;

            var output = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await proc.WaitForExitAsync().ConfigureAwait(false);

            var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.StartsWith("List of devices") || line.StartsWith("*"))
                    continue;

                var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    var serial = parts[0];
                    var state = parts[1];
                    var model = "";
                    var modelMatch = Regex.Match(line, @"model:(\S+)");
                    if (modelMatch.Success)
                    {
                        model = modelMatch.Groups[1].Value.Replace('_', ' ');
                    }

                    devices.Add(new AndroidDeviceInfo(serial, model, state));
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to query adb devices");
        }

        return devices;
    }

    public async Task<(bool Success, string Message)> PairWirelessDeviceAsync(string ipPort, string pairingCode)
    {
        var adb = FindAdb();
        if (string.IsNullOrWhiteSpace(adb))
            return (false, "ADB not found. Please install Android Platform Tools.");

        try
        {
            var psi = new ProcessStartInfo(adb, ["pair", ipPort, pairingCode])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return (false, "Failed to start adb pair process.");

            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync().ConfigureAwait(false);

            var stdout = await outTask;
            var stderr = await errTask;
            var fullOutput = (stdout + " " + stderr).Trim();

            if (fullOutput.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase))
            {
                return (true, fullOutput);
            }

            return (false, string.IsNullOrWhiteSpace(fullOutput) ? "Pairing failed. Check IP:port and code." : fullOutput);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<(bool Success, string Message)> ConnectWirelessDeviceAsync(string ipPort)
    {
        var adb = FindAdb();
        if (string.IsNullOrWhiteSpace(adb))
            return (false, "ADB not found.");

        try
        {
            var psi = new ProcessStartInfo(adb, ["connect", ipPort])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return (false, "Failed to start adb connect.");

            var stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await proc.WaitForExitAsync().ConfigureAwait(false);

            if (stdout.Contains("connected to", StringComparison.OrdinalIgnoreCase))
            {
                return (true, stdout.Trim());
            }

            return (false, stdout.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public void StartScreenMirror(string? serial, MobileMirrorService mirrorService)
    {
        StopScreenMirror();

        var cts = new CancellationTokenSource();
        _mirrorCts = cts;

        _ = Task.Run(async () =>
        {
            mirrorService.OnConnected();

            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var adb = FindAdb();
                    var ffmpeg = CaptureRecordingEngine.FindFfmpeg();

                    if (string.IsNullOrWhiteSpace(adb) || string.IsNullOrWhiteSpace(ffmpeg))
                    {
                        break;
                    }

                    var adbArgs = new List<string>();
                    if (!string.IsNullOrWhiteSpace(serial))
                    {
                        adbArgs.AddRange(["-s", serial]);
                    }
                    adbArgs.AddRange(["exec-out", "screenrecord", "--output-format=h264", "--size", "720x1280", "--bit-rate", "4M", "-"]);

                    var adbPsi = new ProcessStartInfo(adb, adbArgs)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    var ffmpegPsi = new ProcessStartInfo(ffmpeg, ["-hide_banner", "-loglevel", "error", "-f", "h264", "-i", "-", "-f", "image2pipe", "-vcodec", "mjpeg", "-q:v", "5", "-"])
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    using var adbProc = Process.Start(adbPsi);
                    using var ffmpegProc = Process.Start(ffmpegPsi);

                    if (adbProc == null || ffmpegProc == null)
                        break;

                    _adbProcess = adbProc;
                    _ffmpegProcess = ffmpegProc;

                    // Pipe ADB screenrecord stdout -> FFmpeg stdin
                    var pipeTask = Task.Run(async () =>
                    {
                        try
                        {
                            var pipeBuffer = new byte[64 * 1024];
                            int read;
                            while (!cts.IsCancellationRequested && (read = await adbProc.StandardOutput.BaseStream.ReadAsync(pipeBuffer, cts.Token)) > 0)
                            {
                                await ffmpegProc.StandardInput.BaseStream.WriteAsync(pipeBuffer.AsMemory(0, read), cts.Token);
                                await ffmpegProc.StandardInput.BaseStream.FlushAsync(cts.Token);
                            }
                        }
                        catch { }
                    }, cts.Token);

                    // Read MJPEG frames from FFmpeg stdout
                    var frameBuffer = new byte[256 * 1024];
                    using var ms = new MemoryStream();
                    var ffmpegOut = ffmpegProc.StandardOutput.BaseStream;
                    int bytesRead;

                    while (!cts.IsCancellationRequested && (bytesRead = await ffmpegOut.ReadAsync(frameBuffer, cts.Token)) > 0)
                    {
                        for (int i = 0; i < bytesRead; i++)
                        {
                            ms.WriteByte(frameBuffer[i]);

                            // Check for JPEG End-Of-Image marker (0xFF, 0xD9)
                            if (ms.Length >= 2)
                            {
                                ms.Position = ms.Length - 2;
                                int b1 = ms.ReadByte();
                                int b2 = ms.ReadByte();
                                if (b1 == 0xFF && b2 == 0xD9)
                                {
                                    var jpeg = ms.ToArray();
                                    mirrorService.OnFrame(jpeg);
                                    ms.SetLength(0);
                                }
                            }
                        }
                    }

                    await pipeTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Android screenrecord iteration ended");
                    await Task.Delay(1000, cts.Token);
                }
            }

            mirrorService.OnDisconnected();
        }, cts.Token);
    }

    public void StopScreenMirror()
    {
        try
        {
            _mirrorCts?.Cancel();
            _mirrorCts = null;

            if (_adbProcess is { HasExited: false })
            {
                try { _adbProcess.Kill(true); } catch { }
                _adbProcess.Dispose();
                _adbProcess = null;
            }

            if (_ffmpegProcess is { HasExited: false })
            {
                try { _ffmpegProcess.Kill(true); } catch { }
                _ffmpegProcess.Dispose();
                _ffmpegProcess = null;
            }
        }
        catch { }
    }
}
