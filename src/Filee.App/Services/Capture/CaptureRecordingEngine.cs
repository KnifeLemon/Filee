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
    private WasapiLoopbackRecorder? _wasapiRecorder;
    private string? _currentRawSegmentPath;
    private string? _currentTargetSegmentPath;
    private string? _currentSysAudioPath;
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
        List<string> validSegments;
        lock (_lock)
        {
            validSegments = _segmentFiles.Where(f => File.Exists(f) && new FileInfo(f).Length > 0).ToList();
            if (validSegments.Count == 0)
                return null;

            finalVideoPath = Path.Combine(_sessionFolder!, "capture_output.mp4");
        }

        try
        {
            if (validSegments.Count == 1)
            {
                File.Move(validSegments[0], finalVideoPath, overwrite: true);
            }
            else
            {
                // Concatenate segments
                await ConcatenateSegmentsAsync(validSegments, finalVideoPath).ConfigureAwait(false);
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

        string finalSegmentPath;
        string rawSegmentPath;
        string? sysAudioPath = null;
        CaptureConfig config;
        lock (_lock)
        {
            config = _activeConfig!;
            var idx = _segmentFiles.Count;
            finalSegmentPath = Path.Combine(_sessionFolder!, $"segment_{idx:D4}.mp4");
            rawSegmentPath = Path.Combine(_sessionFolder!, $"segment_{idx:D4}_raw.mp4");
            if (config.EnableSystemAudio && WasapiLoopbackRecorder.IsSupported)
            {
                sysAudioPath = Path.Combine(_sessionFolder!, $"sysaudio_{idx:D4}.wav");
            }
            _currentRawSegmentPath = rawSegmentPath;
            _currentTargetSegmentPath = finalSegmentPath;
            _currentSysAudioPath = sysAudioPath;
            _segmentFiles.Add(finalSegmentPath);
        }

        // On Windows 10/11, try modern Windows.Graphics.Capture API (gfxcapture) first.
        // It provides smooth D3D11 frame grabbing and avoids GDI BitBlt access limitations (error 5).
        bool success = await TryLaunchRecordingProcessAsync(ffmpegExe, rawSegmentPath, config, useGfxCapture: OperatingSystem.IsWindows()).ConfigureAwait(false);
        if (!success && OperatingSystem.IsWindows())
        {
            _log?.LogWarning("Primary gfxcapture engine failed, falling back to gdigrab");
            success = await TryLaunchRecordingProcessAsync(ffmpegExe, rawSegmentPath, config, useGfxCapture: false).ConfigureAwait(false);
        }

        // If recording with microphone failed, retry with video-only so screen recording always starts smoothly
        if (!success && config.EnableMicrophone)
        {
            _log?.LogWarning("Recording with microphone failed, falling back to video-only capture");
            var fallbackConfig = CloneWithoutMicrophone(config);
            success = await TryLaunchRecordingProcessAsync(ffmpegExe, rawSegmentPath, fallbackConfig, useGfxCapture: OperatingSystem.IsWindows()).ConfigureAwait(false);
            if (!success && OperatingSystem.IsWindows())
            {
                success = await TryLaunchRecordingProcessAsync(ffmpegExe, rawSegmentPath, fallbackConfig, useGfxCapture: false).ConfigureAwait(false);
            }
        }

        if (!success)
        {
            lock (_lock)
            {
                _segmentFiles.Remove(finalSegmentPath);
                _currentRawSegmentPath = null;
                _currentTargetSegmentPath = null;
                _currentSysAudioPath = null;
            }
            ErrorOccurred?.Invoke(LastErrorMessage ?? "Screen recording failed to start.");
            return false;
        }

        // Start WASAPI system audio loopback recorder if enabled
        if (!string.IsNullOrWhiteSpace(sysAudioPath))
        {
            try
            {
                _wasapiRecorder = new WasapiLoopbackRecorder();
                _wasapiRecorder.Start(sysAudioPath);
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "Failed to start WASAPI system audio loopback recorder; continuing without system audio.");
                _wasapiRecorder = null;
            }
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
            await Task.Delay(600).ConfigureAwait(false);
            if (proc.HasExited && proc.ExitCode != 0)
            {
                string errDetail;
                lock (stderrLines)
                {
                    var nonBannerLines = stderrLines
                        .Where(l =>
                        {
                            var t = l.Trim();
                            return !t.StartsWith("ffmpeg version", StringComparison.OrdinalIgnoreCase)
                                && !t.StartsWith("built with", StringComparison.OrdinalIgnoreCase)
                                && !t.StartsWith("configuration:", StringComparison.OrdinalIgnoreCase)
                                && !t.StartsWith("--enable", StringComparison.OrdinalIgnoreCase)
                                && !t.StartsWith("--disable", StringComparison.OrdinalIgnoreCase)
                                && !t.StartsWith("libav", StringComparison.OrdinalIgnoreCase)
                                && !t.StartsWith("libsw", StringComparison.OrdinalIgnoreCase);
                        })
                        .ToList();
                    var meaningfulLines = nonBannerLines.Count > 0 ? nonBannerLines : stderrLines.ToList();
                    errDetail = string.Join(Environment.NewLine, meaningfulLines.TakeLast(8)).Trim();
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

        // Stop WASAPI system audio loopback recorder
        WasapiLoopbackRecorder? wasapi;
        lock (_lock)
        {
            wasapi = _wasapiRecorder;
            _wasapiRecorder = null;
        }

        if (wasapi != null)
        {
            try
            {
                wasapi.Stop();
                wasapi.Dispose();
            }
            catch { }
        }

        string? rawSeg;
        string? targetSeg;
        string? sysWav;
        lock (_lock)
        {
            rawSeg = _currentRawSegmentPath;
            targetSeg = _currentTargetSegmentPath;
            sysWav = _currentSysAudioPath;
            _currentRawSegmentPath = null;
            _currentTargetSegmentPath = null;
            _currentSysAudioPath = null;
        }

        if (!string.IsNullOrWhiteSpace(rawSeg) && !string.IsNullOrWhiteSpace(targetSeg))
        {
            var ffmpegExe = FindFfmpeg();
            if (!string.IsNullOrWhiteSpace(ffmpegExe))
            {
                await PostProcessSegmentAudioAsync(ffmpegExe, rawSeg, targetSeg, sysWav).ConfigureAwait(false);
            }
            else if (File.Exists(rawSeg))
            {
                File.Move(rawSeg, targetSeg, overwrite: true);
            }
        }
    }

    private async Task PostProcessSegmentAudioAsync(string ffmpegExe, string rawSegmentPath, string targetSegmentPath, string? sysAudioPath)
    {
        if (string.IsNullOrWhiteSpace(sysAudioPath) || !File.Exists(sysAudioPath) || new FileInfo(sysAudioPath).Length <= 44)
        {
            if (File.Exists(rawSegmentPath))
            {
                if (File.Exists(targetSegmentPath))
                    File.Delete(targetSegmentPath);
                File.Move(rawSegmentPath, targetSegmentPath);
            }
            return;
        }

        try
        {
            // Try amix first (if rawSegment has mic audio stream)
            bool mixed = await TryMixAudioAsync(ffmpegExe, rawSegmentPath, sysAudioPath, targetSegmentPath).ConfigureAwait(false);
            if (!mixed)
            {
                // Fallback to simple muxing if no microphone track is in rawSegment
                await MuxAudioAsync(ffmpegExe, rawSegmentPath, sysAudioPath, targetSegmentPath).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Failed to post-process system audio for segment {Segment}", targetSegmentPath);
        }
        finally
        {
            if (!File.Exists(targetSegmentPath) && File.Exists(rawSegmentPath))
            {
                try { File.Move(rawSegmentPath, targetSegmentPath, overwrite: true); } catch { }
            }
            try { if (File.Exists(rawSegmentPath)) File.Delete(rawSegmentPath); } catch { }
            try { if (!string.IsNullOrWhiteSpace(sysAudioPath) && File.Exists(sysAudioPath)) File.Delete(sysAudioPath); } catch { }
        }
    }

    private static async Task<bool> TryMixAudioAsync(string ffmpegExe, string rawSegmentPath, string sysAudioPath, string targetSegmentPath)
    {
        var psi = new ProcessStartInfo(ffmpegExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            ArgumentList =
            {
                "-y",
                "-hide_banner",
                "-i", rawSegmentPath,
                "-i", sysAudioPath,
                "-filter_complex", "[0:a][1:a]amix=inputs=2:duration=longest[aout]",
                "-map", "0:v",
                "-map", "[aout]",
                "-c:v", "copy",
                "-c:a", "aac",
                targetSegmentPath
            }
        };
        using var proc = Process.Start(psi);
        if (proc is null) return false;
        await proc.WaitForExitAsync().ConfigureAwait(false);
        return proc.ExitCode == 0 && File.Exists(targetSegmentPath) && new FileInfo(targetSegmentPath).Length > 0;
    }

    private static async Task MuxAudioAsync(string ffmpegExe, string rawSegmentPath, string sysAudioPath, string targetSegmentPath)
    {
        var psi = new ProcessStartInfo(ffmpegExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            ArgumentList =
            {
                "-y",
                "-hide_banner",
                "-i", rawSegmentPath,
                "-i", sysAudioPath,
                "-map", "0:v",
                "-map", "1:a",
                "-c:v", "copy",
                "-c:a", "aac",
                targetSegmentPath
            }
        };
        using var proc = Process.Start(psi);
        if (proc != null)
        {
            await proc.WaitForExitAsync().ConfigureAwait(false);
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

        bool hasMicrophoneInput = false;

        if (OperatingSystem.IsWindows())
        {
            if (useGfxCapture)
            {
                string lavfi;
                if (config.SourceType == CaptureSourceType.Window && config.TargetWindow != null && config.TargetWindow.Handle != 0)
                {
                    lavfi = $"gfxcapture=hwnd={(ulong)config.TargetWindow.Handle}:max_framerate={config.Framerate.ToString(CultureInfo.InvariantCulture)},hwdownload,format=bgra";
                }
                else if (config.SourceType == CaptureSourceType.CustomRegion)
                {
                    GetTargetOffsetAndSize(config, out var offsetX, out var offsetY, out var width, out var height);
                    int screenW = GetSystemMetrics(0);
                    int screenH = GetSystemMetrics(1);
                    if (screenW <= 0) screenW = 1920;
                    if (screenH <= 0) screenH = 1080;

                    offsetX = Math.Clamp(offsetX, 0, Math.Max(0, screenW - 64));
                    offsetY = Math.Clamp(offsetY, 0, Math.Max(0, screenH - 64));
                    width = Math.Clamp(width, 64, screenW - offsetX);
                    height = Math.Clamp(height, 64, screenH - offsetY);

                    int cropRight = Math.Max(0, screenW - (offsetX + width));
                    int cropBottom = Math.Max(0, screenH - (offsetY + height));
                    lavfi = $"gfxcapture=monitor_idx=0:crop_left={offsetX}:crop_top={offsetY}:crop_right={cropRight}:crop_bottom={cropBottom}:max_framerate={config.Framerate.ToString(CultureInfo.InvariantCulture)},hwdownload,format=bgra";
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

            // Microphone audio track if enabled
            if (config.EnableMicrophone)
            {
                var (micName, moniker) = ResolveMicrophoneTarget(config.MicrophoneDevice);
                if (!string.IsNullOrWhiteSpace(moniker))
                {
                    psi.ArgumentList.Add("-f");
                    psi.ArgumentList.Add("dshow");
                    psi.ArgumentList.Add("-i");
                    psi.ArgumentList.Add($"audio={moniker}");
                    hasMicrophoneInput = true;
                }
                else if (!string.IsNullOrWhiteSpace(micName))
                {
                    psi.ArgumentList.Add("-f");
                    psi.ArgumentList.Add("dshow");
                    psi.ArgumentList.Add("-i");
                    psi.ArgumentList.Add($"audio={micName}");
                    hasMicrophoneInput = true;
                }
            }

            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add("0:v");

            if (hasMicrophoneInput)
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
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add("0:v");
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

        if (hasMicrophoneInput)
        {
            psi.ArgumentList.Add("-c:a");
            psi.ArgumentList.Add("aac");
        }

        psi.ArgumentList.Add(outputPath);
        return psi;
    }

    internal static (string? deviceName, string? moniker) ResolveMicrophoneTarget(string? configuredDevice)
    {
        var devices = WindowTargetService.GetCachedAudioInputDevices();
        string? targetName = null;
        if (!string.IsNullOrWhiteSpace(configuredDevice) && !configuredDevice.Equals("Default Microphone", StringComparison.OrdinalIgnoreCase))
        {
            targetName = configuredDevice;
        }
        else
        {
            targetName = devices.FirstOrDefault(d => !d.Equals("Default Microphone", StringComparison.OrdinalIgnoreCase));
        }

        if (string.IsNullOrWhiteSpace(targetName))
            return (null, null);

        var moniker = WindowTargetService.GetAudioDeviceMoniker(targetName);
        return (targetName, moniker);
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

        // Must write UTF-8 WITHOUT BOM so FFmpeg's concat demuxer does not choke on the first line directive
        await File.WriteAllTextAsync(listFilePath, sb.ToString(), new UTF8Encoding(false)).ConfigureAwait(false);

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

        using (var proc = Process.Start(psi))
        {
            if (proc != null)
            {
                var stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await proc.WaitForExitAsync().ConfigureAwait(false);
                if (proc.ExitCode != 0)
                {
                    _log?.LogWarning("FFmpeg concat -c copy returned code {Code}: {Error}", proc.ExitCode, stderr);
                }
            }
        }

        // If -c copy produced no file or an empty file (e.g. timestamp/codec disparity), fallback to re-encoding concat
        if (!File.Exists(finalVideoPath) || new FileInfo(finalVideoPath).Length == 0)
        {
            _log?.LogInformation("Retrying segment concatenation with re-encoding fallback");
            var psiReencode = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            psiReencode.ArgumentList.Add("-y");
            psiReencode.ArgumentList.Add("-f");
            psiReencode.ArgumentList.Add("concat");
            psiReencode.ArgumentList.Add("-safe");
            psiReencode.ArgumentList.Add("0");
            psiReencode.ArgumentList.Add("-i");
            psiReencode.ArgumentList.Add(listFilePath);
            psiReencode.ArgumentList.Add("-c:v");
            psiReencode.ArgumentList.Add("libx264");
            psiReencode.ArgumentList.Add("-preset");
            psiReencode.ArgumentList.Add("ultrafast");
            psiReencode.ArgumentList.Add("-c:a");
            psiReencode.ArgumentList.Add("aac");
            psiReencode.ArgumentList.Add(finalVideoPath);

            using var procRe = Process.Start(psiReencode);
            if (procRe != null)
            {
                await procRe.WaitForExitAsync().ConfigureAwait(false);
            }
        }

        // Fail-safe: if still missing, copy the largest segment so user never loses recorded data
        if ((!File.Exists(finalVideoPath) || new FileInfo(finalVideoPath).Length == 0) && segments.Count > 0)
        {
            var largest = segments.Where(File.Exists).OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
            if (largest != null)
            {
                File.Copy(largest, finalVideoPath, overwrite: true);
            }
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

    private static CaptureConfig CloneWithoutMicrophone(CaptureConfig src) => new()
    {
        SourceType = src.SourceType,
        TargetWindow = src.TargetWindow,
        TargetScreen = src.TargetScreen,
        CustomRegion = src.CustomRegion,
        Resolution = src.Resolution,
        Framerate = src.Framerate,
        EnableMicrophone = false,
        MicrophoneDevice = null,
        EnableSystemAudio = src.EnableSystemAudio,
        HighlightCursor = src.HighlightCursor,
        SpotlightMode = src.SpotlightMode,
        SpotlightRadius = src.SpotlightRadius,
        CountdownBeforeRecording = src.CountdownBeforeRecording
    };

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        if (_wasapiRecorder != null)
        {
            try { _wasapiRecorder.Dispose(); } catch { }
            _wasapiRecorder = null;
        }
        if (_currentProcess != null && !_currentProcess.HasExited)
        {
            try { _currentProcess.Kill(true); } catch { }
            _currentProcess.Dispose();
            _currentProcess = null;
        }
    }
}
