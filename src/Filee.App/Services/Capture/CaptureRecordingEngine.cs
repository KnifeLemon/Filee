using System.Diagnostics;
using System.Globalization;
using System.Text;
using Filee.Engines.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Filee.App.Services.Capture;

public sealed class CaptureRecordingEngine : IDisposable
{
    private readonly ILogger<CaptureRecordingEngine>? _log;
    private readonly List<string> _segmentFiles = [];
    private readonly object _lock = new();

    private Process? _currentProcess;
    private CaptureConfig? _activeConfig;
    private string? _sessionFolder;
    private Stopwatch _stopwatch = new();
    private TimeSpan _accumulatedTime = TimeSpan.Zero;
    private System.Timers.Timer? _timer;

    private bool _isRecording;
    private bool _isPaused;

    public bool IsRecording => _isRecording;
    public bool IsPaused => _isPaused;
    public string? LastErrorMessage { get; private set; }
    public TimeSpan ElapsedTime => _accumulatedTime + (_isRecording && !_isPaused ? _stopwatch.Elapsed : TimeSpan.Zero);

    public event Action<TimeSpan>? ElapsedTimeTicked;
    public event Action<string>? ErrorOccurred;

    public CaptureRecordingEngine(ILogger<CaptureRecordingEngine>? log = null)
    {
        _log = log;
    }

    public async Task<bool> StartRecordingAsync(CaptureConfig config)
    {
        lock (_lock)
        {
            if (_isRecording)
                return false;

            _activeConfig = config;
            _segmentFiles.Clear();
            _accumulatedTime = TimeSpan.Zero;
            _sessionFolder = Path.Combine(Path.GetTempPath(), "FileeCapture", $"Session_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_sessionFolder);
        }

        var started = await StartSegmentAsync().ConfigureAwait(false);
        if (!started)
            return false;

        lock (_lock)
        {
            _isRecording = true;
            _isPaused = false;
            _stopwatch.Restart();

            _timer?.Dispose();
            _timer = new System.Timers.Timer(500);
            _timer.Elapsed += (_, _) =>
            {
                if (_isRecording && !_isPaused)
                {
                    ElapsedTimeTicked?.Invoke(ElapsedTime);
                }
            };
            _timer.Start();
        }

        return true;
    }

    public async Task PauseAsync()
    {
        lock (_lock)
        {
            if (!_isRecording || _isPaused)
                return;

            _isPaused = true;
            _stopwatch.Stop();
            _accumulatedTime += _stopwatch.Elapsed;
            _stopwatch.Reset();
        }

        await StopCurrentProcessAsync().ConfigureAwait(false);
    }

    public async Task ResumeAsync()
    {
        lock (_lock)
        {
            if (!_isRecording || !_isPaused)
                return;
        }

        var started = await StartSegmentAsync().ConfigureAwait(false);
        if (started)
        {
            lock (_lock)
            {
                _isPaused = false;
                _stopwatch.Restart();
            }
        }
    }

    public async Task<RecordedVideoMetadata?> StopAsync()
    {
        lock (_lock)
        {
            if (!_isRecording)
                return null;

            _isRecording = false;
            _isPaused = false;
            _stopwatch.Stop();
            _accumulatedTime += _stopwatch.Elapsed;
            _timer?.Stop();
            _timer?.Dispose();
            _timer = null;
        }

        await StopCurrentProcessAsync().ConfigureAwait(false);

        string finalVideoPath;
        lock (_lock)
        {
            if (_segmentFiles.Count == 0)
                return null;

            finalVideoPath = Path.Combine(_sessionFolder!, "capture_output.mp4");
        }

        try
        {
            if (_segmentFiles.Count == 1)
            {
                File.Move(_segmentFiles[0], finalVideoPath, overwrite: true);
            }
            else
            {
                // Concatenate segments
                await ConcatenateSegmentsAsync(_segmentFiles, finalVideoPath).ConfigureAwait(false);
            }

            var duration = _accumulatedTime > TimeSpan.Zero ? _accumulatedTime : TimeSpan.FromSeconds(1);
            int width = 1920;
            int height = 1080;
            if (_activeConfig != null)
            {
                GetTargetDimensions(_activeConfig, out width, out height);
            }

            return new RecordedVideoMetadata(
                finalVideoPath,
                duration,
                width,
                height,
                _activeConfig?.Framerate ?? 30,
                _activeConfig?.EnableMicrophone ?? false);
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Failed to finalize recorded segments");
            ErrorOccurred?.Invoke($"Failed to finalize video: {ex.Message}");
            return null;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private async Task<bool> StartSegmentAsync()
    {
        var ffmpegExe = FindFfmpeg();
        if (string.IsNullOrWhiteSpace(ffmpegExe))
        {
            LastErrorMessage = "FFmpeg executable could not be found.";
            ErrorOccurred?.Invoke(LastErrorMessage);
            return false;
        }

        string segmentPath;
        CaptureConfig config;
        lock (_lock)
        {
            config = _activeConfig!;
            segmentPath = Path.Combine(_sessionFolder!, $"segment_{_segmentFiles.Count:D4}.mp4");
            _segmentFiles.Add(segmentPath);
        }

        // On Windows 10/11, try modern Windows.Graphics.Capture API (gfxcapture) first.
        // It provides smooth D3D11 frame grabbing and avoids GDI BitBlt access limitations (error 5).
        bool success = await TryLaunchRecordingProcessAsync(ffmpegExe, segmentPath, config, useGfxCapture: OperatingSystem.IsWindows()).ConfigureAwait(false);
        if (!success && OperatingSystem.IsWindows())
        {
            _log?.LogWarning("Primary gfxcapture engine failed, falling back to gdigrab");
            success = await TryLaunchRecordingProcessAsync(ffmpegExe, segmentPath, config, useGfxCapture: false).ConfigureAwait(false);
        }

        if (!success)
        {
            ErrorOccurred?.Invoke(LastErrorMessage ?? "Screen recording failed to start.");
            return false;
        }

        return true;
    }

    private async Task<bool> TryLaunchRecordingProcessAsync(string ffmpegExe, string segmentPath, CaptureConfig config, bool useGfxCapture)
    {
        var psi = BuildFfmpegProcessStartInfo(ffmpegExe, segmentPath, config, useGfxCapture);
        try
        {
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var stderrLines = new Queue<string>();
            proc.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    _log?.LogDebug("[FFmpeg Record] {Data}", e.Data);
                    lock (stderrLines)
                    {
                        if (stderrLines.Count >= 40)
                            stderrLines.Dequeue();
                        stderrLines.Enqueue(e.Data);
                    }
                }
            };

            proc.Start();
            proc.BeginErrorReadLine();

            lock (_lock)
            {
                _currentProcess = proc;
            }

            // Wait brief moment to confirm process didn't exit prematurely
            await Task.Delay(400).ConfigureAwait(false);
            if (proc.HasExited && proc.ExitCode != 0)
            {
                string errDetail;
                lock (stderrLines)
                {
                    errDetail = string.Join(Environment.NewLine, stderrLines).Trim();
                }

                LastErrorMessage = !string.IsNullOrWhiteSpace(errDetail)
                    ? errDetail
                    : $"Screen recorder exited prematurely with code {proc.ExitCode}";
                _log?.LogError("FFmpeg failed to start (useGfxCapture={UseGfx}): {Error}", useGfxCapture, LastErrorMessage);

                lock (_lock)
                {
                    _currentProcess = null;
                }
                try { proc.Dispose(); } catch { }
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            LastErrorMessage = ex.Message;
            _log?.LogError(ex, "Failed to launch FFmpeg recording process (useGfxCapture={UseGfx})", useGfxCapture);
            return false;
        }
    }

    private async Task StopCurrentProcessAsync()
    {
        Process? proc;
        lock (_lock)
        {
            proc = _currentProcess;
            _currentProcess = null;
        }

        if (proc is null)
            return;

        try
        {
            if (!proc.HasExited)
            {
                // Send 'q' to stdin for clean MP4 moov atom completion
                try
                {
                    await proc.StandardInput.WriteLineAsync("q").ConfigureAwait(false);
                    await proc.StandardInput.FlushAsync().ConfigureAwait(false);
                }
                catch
                {
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            if (!proc.HasExited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
            }
        }
        finally
        {
            proc.Dispose();
        }
    }

    internal ProcessStartInfo BuildFfmpegProcessStartInfo(string ffmpegExe, string outputPath, CaptureConfig config, bool useGfxCapture = true)
    {
        var psi = new ProcessStartInfo(ffmpegExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = Encoding.UTF8,
        };

        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-y");

        if (OperatingSystem.IsWindows())
        {
            if (useGfxCapture)
            {
                string lavfi;
                if (config.SourceType == CaptureSourceType.Window && config.TargetWindow != null && config.TargetWindow.Handle != 0)
                {
                    lavfi = $"gfxcapture=hwnd=0x{config.TargetWindow.Handle.ToInt64():X}:max_framerate={config.Framerate.ToString(CultureInfo.InvariantCulture)},hwdownload,format=bgra";
                }
                else if (config.SourceType == CaptureSourceType.CustomRegion)
                {
                    GetTargetOffsetAndSize(config, out var offsetX, out var offsetY, out var width, out var height);
                    width = Math.Max(2, (width / 2) * 2);
                    height = Math.Max(2, (height / 2) * 2);
                    lavfi = $"gfxcapture=monitor_idx=0:crop_left={offsetX}:crop_top={offsetY}:width={width}:height={height}:max_framerate={config.Framerate.ToString(CultureInfo.InvariantCulture)},hwdownload,format=bgra";
                }
                else
                {
                    int monitorIdx = config.TargetScreen != null ? Math.Max(0, config.TargetScreen.Index - 1) : 0;
                    lavfi = $"gfxcapture=monitor_idx={monitorIdx}:max_framerate={config.Framerate.ToString(CultureInfo.InvariantCulture)},hwdownload,format=bgra";
                }

                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add("lavfi");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(lavfi);
            }
            else
            {
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add("gdigrab");
                psi.ArgumentList.Add("-framerate");
                psi.ArgumentList.Add(config.Framerate.ToString(CultureInfo.InvariantCulture));

                if (config.SourceType == CaptureSourceType.FullScreen)
                {
                    // Fullscreen capture natively grabs entire desktop
                    psi.ArgumentList.Add("-i");
                    psi.ArgumentList.Add("desktop");
                }
                else
                {
                    GetTargetOffsetAndSize(config, out var offsetX, out var offsetY, out var width, out var height);

                    int screenW = GetSystemMetrics(0); // SM_CXSCREEN
                    int screenH = GetSystemMetrics(1); // SM_CYSCREEN
                    if (screenW <= 0) screenW = 1920;
                    if (screenH <= 0) screenH = 1080;

                    offsetX = Math.Clamp(offsetX, 0, Math.Max(0, screenW - 64));
                    offsetY = Math.Clamp(offsetY, 0, Math.Max(0, screenH - 64));
                    width = Math.Clamp(width, 64, screenW - offsetX);
                    height = Math.Clamp(height, 64, screenH - offsetY);

                    width = Math.Max(2, (width / 2) * 2);
                    height = Math.Max(2, (height / 2) * 2);

                    psi.ArgumentList.Add("-offset_x");
                    psi.ArgumentList.Add(offsetX.ToString(CultureInfo.InvariantCulture));
                    psi.ArgumentList.Add("-offset_y");
                    psi.ArgumentList.Add(offsetY.ToString(CultureInfo.InvariantCulture));
                    psi.ArgumentList.Add("-video_size");
                    psi.ArgumentList.Add($"{width}x{height}");
                    psi.ArgumentList.Add("-i");
                    psi.ArgumentList.Add("desktop");
                }
            }

            // Microphone audio track if enabled and valid device selected
            if (config.EnableMicrophone && !string.IsNullOrWhiteSpace(config.MicrophoneDevice) && config.MicrophoneDevice != "Default Microphone")
            {
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add("dshow");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add($"audio={config.MicrophoneDevice}");
            }

            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add("0:v");

            if (config.EnableMicrophone && !string.IsNullOrWhiteSpace(config.MicrophoneDevice) && config.MicrophoneDevice != "Default Microphone")
            {
                psi.ArgumentList.Add("-map");
                psi.ArgumentList.Add("1:a?");
            }
        }
        else
        {
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("x11grab");
            psi.ArgumentList.Add("-framerate");
            psi.ArgumentList.Add(config.Framerate.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(":0.0");
        }

        // Output scaling filter if target preset requested
        if (config.Resolution != CaptureResolutionPreset.Native)
        {
            GetTargetDimensions(config, out var targetW, out var targetH);
            psi.ArgumentList.Add("-vf");
            psi.ArgumentList.Add($"scale={targetW}:{targetH}:force_original_aspect_ratio=decrease,pad={targetW}:{targetH}:(ow-iw)/2:(oh-ih)/2");
        }

        // Codecs and encoding
        psi.ArgumentList.Add("-c:v");
        psi.ArgumentList.Add("libx264");
        psi.ArgumentList.Add("-preset");
        psi.ArgumentList.Add("ultrafast");
        psi.ArgumentList.Add("-crf");
        psi.ArgumentList.Add("23");
        psi.ArgumentList.Add("-pix_fmt");
        psi.ArgumentList.Add("yuv420p");
        psi.ArgumentList.Add("-movflags");
        psi.ArgumentList.Add("+faststart");

        if (config.EnableMicrophone && !string.IsNullOrWhiteSpace(config.MicrophoneDevice) && config.MicrophoneDevice != "Default Microphone")
        {
            psi.ArgumentList.Add("-c:a");
            psi.ArgumentList.Add("aac");
        }

        psi.ArgumentList.Add(outputPath);
        return psi;
    }

    private static void GetTargetOffsetAndSize(CaptureConfig config, out int offsetX, out int offsetY, out int width, out int height)
    {
        switch (config.SourceType)
        {
            case CaptureSourceType.Window when config.TargetWindow != null && !config.TargetWindow.IsEmpty:
                offsetX = Math.Max(0, config.TargetWindow.X);
                offsetY = Math.Max(0, config.TargetWindow.Y);
                width = config.TargetWindow.Width;
                height = config.TargetWindow.Height;
                break;

            case CaptureSourceType.CustomRegion when config.CustomRegion.Width > 0 && config.CustomRegion.Height > 0:
                offsetX = Math.Max(0, config.CustomRegion.X);
                offsetY = Math.Max(0, config.CustomRegion.Y);
                width = config.CustomRegion.Width;
                height = config.CustomRegion.Height;
                break;

            case CaptureSourceType.FullScreen when config.TargetScreen != null:
                offsetX = Math.Max(0, config.TargetScreen.X);
                offsetY = Math.Max(0, config.TargetScreen.Y);
                width = config.TargetScreen.Width;
                height = config.TargetScreen.Height;
                break;

            default:
                offsetX = 0;
                offsetY = 0;
                width = 1920;
                height = 1080;
                break;
        }
    }

    private static void GetTargetDimensions(CaptureConfig config, out int width, out int height)
    {
        switch (config.Resolution)
        {
            case CaptureResolutionPreset.Res720p:
                width = 1280;
                height = 720;
                break;
            case CaptureResolutionPreset.Res1080p:
                width = 1920;
                height = 1080;
                break;
            case CaptureResolutionPreset.Res2K:
                width = 2560;
                height = 1440;
                break;
            case CaptureResolutionPreset.Res4K:
                width = 3840;
                height = 2160;
                break;
            default:
                GetTargetOffsetAndSize(config, out _, out _, out width, out height);
                break;
        }
    }

    private async Task ConcatenateSegmentsAsync(IReadOnlyList<string> segments, string finalVideoPath)
    {
        var ffmpeg = FindFfmpeg();
        if (string.IsNullOrWhiteSpace(ffmpeg))
            throw new InvalidOperationException("FFmpeg not found for concatenation.");

        var listFilePath = Path.Combine(_sessionFolder!, "concat_list.txt");
        var sb = new StringBuilder();
        foreach (var seg in segments)
        {
            var escaped = seg.Replace("\\", "/").Replace("'", "'\\''");
            sb.AppendLine($"file '{escaped}'");
        }
        await File.WriteAllTextAsync(listFilePath, sb.ToString(), Encoding.UTF8).ConfigureAwait(false);

        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("concat");
        psi.ArgumentList.Add("-safe");
        psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(listFilePath);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("copy");
        psi.ArgumentList.Add(finalVideoPath);

        using var proc = Process.Start(psi);
        if (proc != null)
        {
            await proc.WaitForExitAsync().ConfigureAwait(false);
        }
    }

    public static string? FindFfmpeg()
    {
        // 1. Check user local appdata Filee engines
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var ownFfmpeg = Path.Combine(localAppData, "Filee", "engines", "ffmpeg", "bin", "ffmpeg.exe");
        if (File.Exists(ownFfmpeg))
            return ownFfmpeg;

        // 2. Check installed Program Files Filee engines
        var progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var installedFfmpeg = Path.Combine(progFiles, "Filee", "engines", "ffmpeg", "bin", "ffmpeg.exe");
        if (File.Exists(installedFfmpeg))
            return installedFfmpeg;

        // 3. Check AppContext.BaseDirectory engines
        var localEnginesFfmpeg = Path.Combine(AppContext.BaseDirectory, "engines", "ffmpeg", "bin", "ffmpeg.exe");
        if (File.Exists(localEnginesFfmpeg))
            return localEnginesFfmpeg;

        // 4. Filee EngineEnvironment
        var fromEnv = EngineEnvironment.OwnProgram("ffmpeg", "ffmpeg.exe")
            ?? EngineEnvironment.BundledProgram("ffmpeg", "ffmpeg.exe")
            ?? EngineEnvironment.SystemProgram("ffmpeg", "ffmpeg.exe");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
            return fromEnv;

        // 5. Check PATH directly
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            var exeName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
            foreach (var part in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(part.Trim(), exeName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return "ffmpeg";
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        if (_currentProcess != null && !_currentProcess.HasExited)
        {
            try { _currentProcess.Kill(true); } catch { }
            _currentProcess.Dispose();
        }
    }
}
