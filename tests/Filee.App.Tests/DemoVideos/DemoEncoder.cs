// Turns rendered frames into the demo files with FFmpeg: H.264 MP4s for social video, and from a lossless master
// the animated WebP and GIF for pages and slides, each made as good as fits its size budget.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;

namespace Filee.App.Tests.DemoVideos;

/// <summary>Feeds raw BGRA frames to an FFmpeg process that encodes them.</summary>
internal sealed class FrameSink : IDisposable
{
    private readonly Process _process;
    private readonly Stream _input;
    private readonly StringBuilder _log = new();
    private readonly PixelSize _size;
    private readonly byte[] _buffer;
    private bool _closed;

    public FrameSink(string ffmpeg, PixelSize size, int fps, IEnumerable<string> output)
    {
        _size = size;
        _buffer = new byte[size.Width * size.Height * 4];
        _process = DemoEncoder.Start(ffmpeg,
            ["-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{size.Width}x{size.Height}", "-framerate", fps.ToString(CultureInfo.InvariantCulture), "-i", "-", .. output],
            _log, redirectInput: true);
        _input = _process.StandardInput.BaseStream;
    }

    public void Write(RenderTargetBitmap frame)
    {
        if (frame.PixelSize != _size)
            throw new InvalidOperationException($"Frame is {frame.PixelSize}, expected {_size}.");
        var handle = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
        try
        {
            frame.CopyPixels(new PixelRect(_size), handle.AddrOfPinnedObject(), _buffer.Length, _size.Width * 4);
        }
        finally
        {
            handle.Free();
        }
        _input.Write(_buffer);
    }

    /// <summary>Ends the stream and waits for FFmpeg to finish the file.</summary>
    public void Finish()
    {
        if (_closed)
            return;
        _closed = true;
        _input.Close();
        _process.WaitForExit();
        if (_process.ExitCode != 0)
            throw new InvalidOperationException($"FFmpeg failed ({_process.ExitCode}):\n{_log}");
    }

    public void Dispose()
    {
        try
        {
            Finish();
        }
        finally
        {
            _process.Dispose();
        }
    }
}

internal static class DemoEncoder
{
    public const int Fps = 60;

    /// <summary>H.264 for social video: BT.709 colours in yuv420p, high quality, no audio, playable while it loads.</summary>
    public static string[] Mp4(string path) =>
    [
        "-vf", "scale=out_color_matrix=bt709:out_range=tv,format=yuv420p",
        "-c:v", "libx264", "-preset", "slow", "-crf", "18", "-profile:v", "high", "-tune", "animation",
        "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv",
        "-movflags", "+faststart", "-an", "-y", path,
    ];

    /// <summary>Lossless master the animated images are made from.</summary>
    public static string[] Master(string path) => ["-c:v", "ffv1", "-level", "3", "-pix_fmt", "bgr0", "-an", "-y", path];

    /// <summary>
    /// Animated WebP <paramref name="width"/> pixels wide from <paramref name="master"/>, looping: the best quality, then
    /// fewer frames per second, that stays within <paramref name="maxBytes"/>. Returns the size written.
    /// </summary>
    public static long WebP(string ffmpeg, string master, string path, int width, long maxBytes)
    {
        foreach (var fps in new[] { 30, 25, 20 })
            foreach (var quality in new[] { 90, 82, 75, 68, 60, 52, 45 })
            {
                Run(ffmpeg, ["-i", master, "-vf", $"fps={fps},scale={width}:-2:flags=lanczos", "-c:v", "libwebp_anim", "-lossless", "0",
                    "-q:v", quality.ToString(CultureInfo.InvariantCulture), "-compression_level", "6", "-preset", "picture", "-loop", "0", "-an", "-y", path]);
                var size = new FileInfo(path).Length;
                if (size <= maxBytes)
                    return size;
            }
        throw new InvalidOperationException($"{Path.GetFileName(path)} doesn't fit in {maxBytes / 1024} KB.");
    }

    /// <summary>
    /// Looping GIF <paramref name="width"/> pixels wide with one palette for the whole clip (ordered dithering, which
    /// doesn't shimmer from frame to frame), only changed rectangles stored: the smoothest that fits <paramref name="maxBytes"/>.
    /// </summary>
    public static long Gif(string ffmpeg, string master, string path, int width, long maxBytes)
    {
        foreach (var (fps, colors) in new[] { (25, 192), (20, 192), (20, 128), (15, 128), (15, 96), (12, 64) })
        {
            Run(ffmpeg, ["-i", master, "-filter_complex",
                $"fps={fps},scale={width}:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors={colors}:stats_mode=full[p];" +
                "[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle",
                "-loop", "0", "-an", "-y", path]);
            var size = new FileInfo(path).Length;
            if (size <= maxBytes)
                return size;
        }
        throw new InvalidOperationException($"{Path.GetFileName(path)} doesn't fit in {maxBytes / 1024} KB.");
    }

    public static void Run(string ffmpeg, IEnumerable<string> arguments)
    {
        var log = new StringBuilder();
        using var process = Start(ffmpeg, arguments, log, redirectInput: false);
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"FFmpeg failed ({process.ExitCode}):\n{log}");
    }

    internal static Process Start(string ffmpeg, IEnumerable<string> arguments, StringBuilder log, bool redirectInput)
    {
        var info = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardInput = redirectInput,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in (string[])["-hide_banner", "-loglevel", "error", .. arguments])
            info.ArgumentList.Add(argument);
        var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {ffmpeg}.");
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                lock (log)
                    log.AppendLine(e.Data);
        };
        process.OutputDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        return process;
    }

    /// <summary>
    /// FFmpeg to use: FILEE_FFMPEG, else the one fetch-engines.ps1 put in the repository's engines folder, else the PATH.
    /// </summary>
    public static string? Find(string repository)
    {
        var configured = Environment.GetEnvironmentVariable("FILEE_FFMPEG");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var bundled = Path.Combine(repository, "engines", "ffmpeg", "bin", exe);
        if (File.Exists(bundled))
            return bundled;
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, exe))
            .FirstOrDefault(File.Exists);
    }
}
