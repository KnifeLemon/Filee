
// Runs command line engines (LibreOffice, rhwp, ...) with a timeout, cancellation and captured output. Engines that
// report their progress while they work (calibre) can be stopped when they go silent instead (see StallWatch).

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
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
    /// <param name="outputReady">
    /// Polled while the program runs; once it has returned true on several polls in a row the program is killed and
    /// treated as finished (exit code 0). On Windows, a Job Object also terminates child processes.
    /// </param>
    /// <param name="stallTimeout">
    /// When set, the program is also killed once it has written nothing (no line on either stream) for this long.
    /// </param>
    public static async Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? workingDirectory = null,
        IDictionary<string, string>? environment = null,
        Action<string>? onOutput = null,
        TimeSpan? stallTimeout = null,
        Func<bool>? outputReady = null)
    {
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            // Closed right after the start: a program that asks a question reads end of input
            // instead of waiting on the console or pipe Filee itself was started with.
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
        {
            foreach (var (key, value) in environment)
                psi.Environment[key] = value;
        }

        using var process = new Process { StartInfo = psi };
        using var job = outputReady is not null && OperatingSystem.IsWindows()
            ? ProcessJob.TryCreate()
            : null;

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var watch = stallTimeout is { } limit ? new StallWatch(limit) : null;

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                watch?.Touch();
                lock (stdout)
                    stdout.AppendLine(e.Data);
                onOutput?.Invoke(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                watch?.Touch();
                lock (stderr)
                    stderr.AppendLine(e.Data);
                onOutput?.Invoke(e.Data);
            }
        };

        if (!process.Start())
            throw new InvalidOperationException($"Could not start {executable}.");

        // Assign immediately after starting, so the Job Object tracks the process and its children.
        // If assignment is unavailable, retain the normal process-tree kill fallback.
        if (job is not null && !job.TryAssign(process))
            job.Dispose();

        Lower(process);
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var finishedEarly = false;

        try
        {
            var exited = process.WaitForExitAsync(timeoutCts.Token);

            if (watch is null && outputReady is not null)
            {
                var ready = WatchOutputAsync(outputReady, timeoutCts.Token);

                if (await Task.WhenAny(exited, ready) == ready && await ready)
                {
                    TryKill(process, job);
                    finishedEarly = true;
                }
                else
                {
                    await exited;
                }
            }
            else if (watch is null)
            {
                await exited;
            }
            else if (!await watch.WaitAsync(exited, timeoutCts.Token))
            {
                TryKill(process, job);
                throw new ConversionRefusedException(
                    StalledErrorKey,
                    $"{Path.GetFileName(executable)}, {Describe(watch.Limit)}");
            }
        }
        catch (OperationCanceledException)
        {
            TryKill(process, job);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException(
                $"{Path.GetFileName(executable)} did not finish within {timeout.TotalSeconds:0} s.");
        }

        if (finishedEarly)
        {
            // Do not wait for redirected streams: a child may have inherited their handles.
            process.WaitForExit(3000);

            lock (stdout)
            {
                lock (stderr)
                {
                    return new ProcessResult(0, stdout.ToString(), stderr.ToString());
                }
            }
        }

        // Make sure the async output readers have drained.
        process.WaitForExit();
        AppLocalRuntime.ThrowIfMissing(executable, process.ExitCode);

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>
    /// True once <paramref name="ready"/> has held on 4 polls in a row (2 s); false when cancelled first.
    /// </summary>
    private static async Task<bool> WatchOutputAsync(Func<bool> ready, CancellationToken cancellationToken)
    {
        var streak = 0;

        try
        {
            while (true)
            {
                await Task.Delay(500, cancellationToken);

                bool ok;
                try
                {
                    ok = ready();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    ok = false;
                }

                streak = ok ? streak + 1 : 0;

                if (streak >= 4)
                    return true;
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>"10 min", or "30 s" below a minute.</summary>
    internal static string Describe(TimeSpan limit) =>
        limit >= TimeSpan.FromMinutes(1)
            ? $"{limit.TotalMinutes:0} min"
            : $"{limit.TotalSeconds:0} s";

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
        catch (Exception ex) when (
            ex is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or NotSupportedException)
        {
        }
    }

    private static void TryKill(Process process, ProcessJob? job)
    {
        // Terminate the Job Object first: the parent may already have exited while a child is still running.
        job?.Terminate();

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    /// <summary>
    /// Tracks a process and its children on Windows. Closing the Job Object kills all processes
    /// still associated with it.
    /// </summary>
    private sealed class ProcessJob : IDisposable
    {
        private const uint JobObjectLimitKillOnJobClose = 0x2000;
        private const int JobObjectExtendedLimitInformationClass = 9;

        private readonly SafeJobHandle _handle;

        private ProcessJob(SafeJobHandle handle)
        {
            _handle = handle;
        }

        public static ProcessJob? TryCreate()
        {
            var handle = CreateJobObject(IntPtr.Zero, null);

            if (handle.IsInvalid)
            {
                handle.Dispose();
                return null;
            }

            var info = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose
                }
            };

            if (!SetInformationJobObject(
                handle,
                JobObjectExtendedLimitInformationClass,
                ref info,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            {
                handle.Dispose();
                return null;
            }

            return new ProcessJob(handle);
        }

        public bool TryAssign(Process process)
        {
            if (_handle.IsClosed || _handle.IsInvalid)
                return false;

            try
            {
                return AssignProcessToJobObject(_handle, process.SafeHandle);
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        public void Terminate()
        {
            if (_handle.IsClosed || _handle.IsInvalid)
                return;

            try
            {
                TerminateJobObject(_handle, 1);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            _handle.Dispose();
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeJobHandle CreateJobObject(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            SafeJobHandle job,
            int informationClass,
            ref JobObjectExtendedLimitInformation information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(
            SafeJobHandle job,
            SafeProcessHandle process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateJobObject(SafeJobHandle job, uint exitCode);

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public SafeJobHandle() : base(true)
            {
            }

            protected override bool ReleaseHandle() => CloseHandle(handle);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool CloseHandle(IntPtr handle);
        }
    }
}
