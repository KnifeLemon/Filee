// Runs ffmpeg.exe and turns its machine-readable progress output into a position in seconds. Unlike ProcessRunner
// there is no overall timeout (a feature film legitimately encodes for an hour); a process that stops reporting
// for a long time is treated as hung and killed (StallWatch), and cancellation always kills the whole process tree.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Filee.Core.Platform;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Media;

/// <summary>Starts FFmpeg with live progress, a stall watchdog and cancellation.</summary>
internal static class FfmpegRunner
{
    /// <summary>
    /// FFmpeg reports its progress every half second while it works, so this long without a single line means it
    /// is stuck (e.g. on a stalled network share).
    /// </summary>
    public static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromMinutes(10);

    private const int KeptErrorLines = 40;

    /// <summary>
    /// Key of the per-frame stats line the GIF palette pass and the runs that read with <c>-copyts</c> write (seconds,
    /// see MediaEncoding).
    /// </summary>
    public const string FrameTimeKey = "frame_time";

    /// <summary>
    /// Runs ffmpeg with <paramref name="arguments"/> and waits for it to exit.
    /// </summary>
    /// <param name="onPosition">Called with the output position in seconds whenever FFmpeg reports one.</param>
    /// <param name="stallTimeout">How long FFmpeg may stay silent before it is killed; <see cref="DefaultStallTimeout"/> when null.</param>
    /// <returns>Exit code and the last lines FFmpeg wrote to stderr (its error messages).</returns>
    /// <remarks>Below normal priority, as every engine (<see cref="ProcessRunner.Lower"/>).</remarks>
    public static async Task<ProcessResult> RunAsync(
        string ffmpeg, IReadOnlyList<string> arguments, Action<double>? onPosition, CancellationToken cancellationToken,
        TimeSpan? stallTimeout = null)
    {
        // A run that reports the time of each picture (MediaEncoding.FrameProgress) writes -progress only to show it is
        // working while no picture is kept, and its out_time is wrong there.
        var byFrame = arguments.Any(a => a.StartsWith(FrameTimeKey + "=", StringComparison.Ordinal));
        var version = CopyVersions.Installed("ffmpeg", Path.GetDirectoryName(ffmpeg) ?? "");
        if (byFrame && WithoutFrameStats(arguments) is { } older && version is { Major: < 6 })
        {
            // FFmpeg before 6.0 has no -stats_enc_pre and would refuse the run: it reports its own -progress instead,
            // which falls short of the end with -copyts.
            arguments = older;
            byFrame = false;
        }
        arguments = GraphInFile(arguments, version, out var graphFile);
        try
        {
            return await RunProcessAsync(ffmpeg, arguments, onPosition, byFrame, stallTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (graphFile is not null)
                TryDelete(graphFile);
        }
    }

    private static async Task<ProcessResult> RunProcessAsync(string ffmpeg, IReadOnlyList<string> arguments, Action<double>? onPosition, bool byFrame,
        TimeSpan? stallTimeout, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(ffmpeg) ?? "",
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
        // SVT-AV1 logs its settings to stderr, where they would bury FFmpeg's error message: errors only.
        psi.Environment["SVT_LOG"] = "1";

        using var process = new Process { StartInfo = psi };
        var limit = stallTimeout ?? DefaultStallTimeout;
        var watch = new StallWatch(limit);
        var errors = new Queue<string>();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            watch.Touch();
            if (ParsePosition(e.Data, byFrame) is { } seconds)
                onPosition?.Invoke(seconds);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            watch.Touch();
            lock (errors)
            {
                errors.Enqueue(e.Data);
                if (errors.Count > KeptErrorLines)
                    errors.Dequeue();
            }
        };

        if (!process.Start())
            throw new InvalidOperationException($"Could not start {ffmpeg}.");
        ProcessRunner.Lower(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            if (!await watch.WaitAsync(process.WaitForExitAsync(cancellationToken), cancellationToken))
            {
                Kill(process);
                throw new TimeoutException($"FFmpeg stopped responding (no progress for {limit.TotalMinutes:0} minutes) and was stopped.");
            }
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }

        // Make sure the asynchronous readers have delivered the last lines.
        process.WaitForExit();
        AppLocalRuntime.ThrowIfMissing(ffmpeg, process.ExitCode);
        string stderr;
        lock (errors)
            stderr = string.Join(Environment.NewLine, errors);
        return new ProcessResult(process.ExitCode, "", stderr);
    }

    /// <summary>
    /// Runs a short query (<c>ffmpeg -version</c>, <c>-encoders</c>) synchronously. Both streams are read in the
    /// background, so the time limit holds even when the program hangs with its output open (reading it to the end
    /// first would wait for the hang).
    /// </summary>
    /// <exception cref="TimeoutException">The program did not exit within <paramref name="timeout"/>; it was killed.</exception>
    /// <exception cref="InvalidOperationException">The program could not be started.</exception>
    internal static ProcessResult RunBriefly(string executable, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.Append(e.Data).Append('\n'); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.Append(e.Data).Append('\n'); };
        if (!process.Start())
            throw new InvalidOperationException($"Could not start {executable}.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(timeout))
        {
            Kill(process);
            throw new TimeoutException($"{Path.GetFileName(executable)} did not finish within {timeout.TotalSeconds:0} s.");
        }
        // Make sure the asynchronous readers have delivered the last lines.
        process.WaitForExit();
        lock (stdout)
            lock (stderr)
                return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>
    /// The position in seconds from one line of <c>-progress</c> output (<c>out_time_us=2000000</c>) or of the frame
    /// stats of the palette pass and <see cref="MediaEncoding.FrameProgress"/> (<c>frame_time=2.5</c>); null for every
    /// other line, and for <c>-progress</c> lines when <paramref name="byFrame"/> is set.
    /// </summary>
    internal static double? ParsePosition(string line, bool byFrame = false)
    {
        var separator = line.IndexOf('=');
        if (separator <= 0)
            return null;
        var key = line.AsSpan(0, separator);
        var value = line.AsSpan(separator + 1).Trim();
        if (key.SequenceEqual("out_time_us"))
            return !byFrame && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds) && microseconds >= 0
                ? microseconds / 1_000_000.0
                : null; // "N/A" before the first frame
        if (key.SequenceEqual(FrameTimeKey))
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0 && double.IsFinite(seconds)
                ? seconds
                : null;
        return null;
    }

    /// <summary>
    /// A filter graph longer than this goes into a file: Windows limits a whole command line to 32,767 characters, and
    /// every part of an edit, every clip of a merge adds a chain to the graph.
    /// </summary>
    internal const int MaxInlineGraph = 8000;

    /// <summary>
    /// <paramref name="arguments"/> with a long <c>-filter_complex</c> in a file (<paramref name="file"/>, deleted by
    /// the caller after the run): <c>-/filter_complex</c> from FFmpeg 7.0 (and an unknown one),
    /// <c>-filter_complex_script</c> before, which has no <c>-/</c>. A <c>-/filter_complex</c> already given (Merge
    /// videos) becomes the older option there too.
    /// </summary>
    internal static IReadOnlyList<string> GraphInFile(IReadOnlyList<string> arguments, Version? version, out string? file)
    {
        file = null;
        var older = version is { Major: < 7 };
        List<string> changed = [.. arguments];
        for (var i = 0; i + 1 < changed.Count; i++)
        {
            if (changed[i] == "-/filter_complex" && older)
                changed[i] = "-filter_complex_script";
            else if (changed[i] == "-filter_complex" && changed[i + 1].Length > MaxInlineGraph && file is null)
            {
                var folder = Path.Combine(WorkFolders.Root, "graphs");
                Directory.CreateDirectory(folder);
                file = Path.Combine(folder, $"graph-{Guid.NewGuid():N}.txt");
                File.WriteAllText(file, changed[i + 1]);
                (changed[i], changed[i + 1]) = (older ? "-filter_complex_script" : "-/filter_complex", file);
            }
        }
        return changed;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// <paramref name="arguments"/> without the picture times of <see cref="MediaEncoding.FrameProgress"/>; null when
    /// they hold none.
    /// </summary>
    internal static List<string>? WithoutFrameStats(IReadOnlyList<string> arguments)
    {
        List<string> kept = [];
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] is "-stats_enc_pre:v:0" or "-stats_enc_pre_fmt:v:0" && i + 1 < arguments.Count)
                i++;
            else
                kept.Add(arguments[i]);
        }
        return kept.Count < arguments.Count ? kept : null;
    }

    /// <summary>A short, readable version of FFmpeg's error output: its last distinct lines, without log prefixes.</summary>
    internal static string Summarize(string stderr)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(StripPrefix)
            .Where(l => l.Length > 0)
            .Distinct()
            .TakeLast(3);
        return string.Join(" ", lines);
    }

    /// <summary>"[aost#0:1/wmav2 @ 0000012a] too many channels" → "too many channels".</summary>
    private static string StripPrefix(string line)
    {
        while (line.StartsWith('[') && line.IndexOf("] ", StringComparison.Ordinal) is var end and > 0)
            line = line[(end + 2)..].TrimStart();
        return line;
    }

    /// <summary>How long a killed FFmpeg may take to exit (and let go of its output) before the caller moves on.</summary>
    private static readonly TimeSpan ExitAfterKill = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Kills FFmpeg and waits until it has exited: Kill only asks Windows to end it, and a process that still runs
    /// holds its output open, so the half-written file or folder of frames could not be deleted yet.
    /// </summary>
    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.WaitForExit(ExitAfterKill);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
