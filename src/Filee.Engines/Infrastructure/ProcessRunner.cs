// Runs command line engines (LibreOffice, rhwp, ...) with a timeout, cancellation and captured output. Engines that
// report their progress while they work (calibre) can be stopped when they go silent instead (see StallWatch).

using System.Diagnostics;
using System.Text;
using Filee.Core.Conversion;

namespace Filee.Engines.Infrastructure;

/// <summary>Result of an external process.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Helper for starting external tools safely.</summary>
public static class ProcessRunner
{
    /// <summary>The reason shown when a program stopped writing anything for the stall limit and was stopped.</summary>
    public const string StalledErrorKey = "error.engine_stalled";

    /// <summary>
    /// Starts <paramref name="executable"/> with the given arguments (no shell, arguments are escaped),
    /// waits for it to exit and kills the whole process tree on timeout or cancellation.
    /// </summary>
    /// <exception cref="ConversionRefusedException">
    /// Windows could not start the program because a DLL is missing (see <see cref="AppLocalRuntime"/>), or it stalled
    /// (<see cref="StalledErrorKey"/>).
    /// </exception>
    /// <exception cref="TimeoutException">The program did not exit within <paramref name="timeout"/>; it was killed.</exception>
    /// <param name="timeout">The longest the program may run; <see cref="Timeout.InfiniteTimeSpan"/> for no limit.</param>
    /// <param name="onOutput">Called with every line of standard output and error as it arrives (e.g. to parse progress).</param>
    /// <param name="stallTimeout">
    /// When set, the program is also killed once it has written nothing (no line on either stream) for this long: for
    /// engines that keep reporting progress, so a long run that does goes on and a hung one stops early.
    /// </param>
    public static async Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? workingDirectory = null,
        IDictionary<string, string>? environment = null,
        Action<string>? onOutput = null,
        TimeSpan? stallTimeout = null)
    {
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            // Closed right after the start: a program that asks a question (7-Zip's password prompt) reads end of
            // input instead of waiting on the console or pipe Filee itself was started with.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(executable) ?? "",
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);
        if (environment is not null)
            foreach (var (key, value) in environment)
                psi.Environment[key] = value;

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var watch = stallTimeout is { } limit ? new StallWatch(limit) : null;
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { watch?.Touch(); lock (stdout) stdout.AppendLine(e.Data); onOutput?.Invoke(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { watch?.Touch(); lock (stderr) stderr.AppendLine(e.Data); onOutput?.Invoke(e.Data); } };

        if (!process.Start())
            throw new InvalidOperationException($"Could not start {executable}.");
        Lower(process);
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var exited = WaitForExitOnlyAsync(process, timeoutCts.Token);
            if (watch is null)
                await exited;
            else if (!await watch.WaitAsync(exited, timeoutCts.Token))
            {
                TryKill(process);
                throw new ConversionRefusedException(StalledErrorKey, $"{Path.GetFileName(executable)}, {Describe(watch.Limit)}");
            }
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(executable)} did not finish within {timeout.TotalSeconds:0} s.");
        }

        // Make sure the async output readers have drained.
        await DrainAsync(process, executable, stderr, cancellationToken);
        AppLocalRuntime.ThrowIfMissing(executable, process.ExitCode);
        lock (stdout)
            lock (stderr)
                return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>"10 min", or "30 s" below a minute.</summary>
    internal static string Describe(TimeSpan limit) =>
        limit >= TimeSpan.FromMinutes(1) ? $"{limit.TotalMinutes:0} min" : $"{limit.TotalSeconds:0} s";

    /// <summary>How long the rest of the output may take once the program has exited.</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Waits for the program itself to exit. <see cref="Process.WaitForExitAsync"/> also waits for the end of its
    /// output, which a program it started can hold open long after (soffice.bin after soffice.exe): see
    /// <see cref="DrainAsync"/>.
    /// </summary>
    private static async Task WaitForExitOnlyAsync(Process process, CancellationToken cancellationToken)
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => exited.TrySetResult();
        if (process.HasExited)
            exited.TrySetResult();
        await exited.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Waits for the rest of the output after the program exited, for <see cref="DrainTimeout"/> at most, rather than
    /// block the conversion until its time limit: only the rest of the output is lost, with a note in standard error.
    /// </summary>
    private static async Task DrainAsync(Process process, string executable, StringBuilder stderr, CancellationToken cancellationToken)
    {
        using var drain = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        drain.CancelAfter(DrainTimeout);
        try
        {
            await process.WaitForExitAsync(drain.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            lock (stderr)
                stderr.AppendLine($"(output not complete: a program {Path.GetFileName(executable)} started still holds it)");
        }
    }

    /// <summary>
    /// Below normal priority, so a long conversion on every core leaves room for the desktop and Filee's own input
    /// hook (#511); on an idle machine it is as fast. Programs it starts inherit it. A process that is already gone
    /// or not ours to change stays as it is.
    /// </summary>
    internal static void Lower(Process process)
    {
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
