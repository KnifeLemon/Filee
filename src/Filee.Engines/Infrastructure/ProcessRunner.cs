// Runs command line engines (LibreOffice, rhwp, ...) with a timeout, cancellation and captured output. Engines that
// report their progress while they work (calibre) can be stopped when they go silent instead (see StallWatch).

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Filee.Core.Conversion;
using Microsoft.Win32.SafeHandles;

namespace Filee.Engines.Infrastructure;

/// <summary>Result of an external process.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>
    /// It went over the memory limit it was given (<see cref="ProcessRunner.RunWithOutputAsync"/>): it was killed, or
    /// on Windows was refused memory, and what it printed may stop part way.
    /// </summary>
    public bool StoppedForMemory { get; init; }
}

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
    /// <param name="outputReady">
    /// Polled while the program runs (not together with <paramref name="stallTimeout"/>); once it has returned true on
    /// several polls in a row the program is killed and treated as finished (exit code 0). On Windows, a Job Object also
    /// ends the programs it started, even after it exited itself.
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
        using var job = outputReady is not null && OperatingSystem.IsWindows() ? ProcessJob.TryCreate() : null;
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var watch = stallTimeout is { } limit ? new StallWatch(limit) : null;
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { watch?.Touch(); lock (stdout) stdout.AppendLine(e.Data); onOutput?.Invoke(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { watch?.Touch(); lock (stderr) stderr.AppendLine(e.Data); onOutput?.Invoke(e.Data); } };

        if (!process.Start())
            throw new InvalidOperationException($"Could not start {executable}.");
        // Joined right after the start, so the job holds the programs it starts later. One it started in that first
        // instant would escape the job (very unlikely with soffice, which loads for a while before it starts
        // soffice.bin). Without a job, TryKill still ends the process tree.
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
            var exited = WaitForExitOnlyAsync(process, timeoutCts.Token);
            if (watch is null && outputReady is not null && await WatchOutputAsync(outputReady, exited, timeoutCts.Token))
            {
                TryKill(process, job);
                finishedEarly = true;
            }
            else if (watch is null)
                await exited;
            else if (!await watch.WaitAsync(exited, timeoutCts.Token))
            {
                TryKill(process, job);
                throw new ConversionRefusedException(StalledErrorKey, $"{Path.GetFileName(executable)}, {Describe(watch.Limit)}");
            }
        }
        catch (OperationCanceledException)
        {
            TryKill(process, job);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(executable)} did not finish within {timeout.TotalSeconds:0} s.");
        }

        if (finishedEarly)
        {
            // Not waiting for the end of the output: a child the job could not end may still hold the inherited pipes.
            process.WaitForExit(3000);
            lock (stdout)
                lock (stderr)
                    return new ProcessResult(0, stdout.ToString(), stderr.ToString());
        }

        // The program exited; the ones it started that still run (and may hold the output) end with the job.
        job?.Terminate();
        // Make sure the async output readers have drained.
        await DrainAsync(process, executable, stderr, cancellationToken);
        AppLocalRuntime.ThrowIfMissing(executable, process.ExitCode);
        lock (stdout)
            lock (stderr)
                return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>
    /// Starts <paramref name="executable"/> like <see cref="RunAsync"/>, but hands its standard output to
    /// <paramref name="readOutput"/> as bytes while it runs instead of collecting text (7-Zip's <c>-so</c>). When
    /// <paramref name="readOutput"/> throws (it has read more than it takes), the program is killed and the exception
    /// passed on. The result's standard output is empty.
    /// </summary>
    /// <exception cref="TimeoutException">The program did not exit within <paramref name="timeout"/>; it was killed.</exception>
    /// <param name="workingDirectory">Where it runs; by default the folder it is in.</param>
    /// <param name="memoryLimit">
    /// The most memory the program may use, in bytes, roughly. On Windows, the limit of a Job Object it and the
    /// programs it starts are in: what goes over fails to allocate. Elsewhere, or without a job, it is killed once its
    /// memory goes over (<see cref="MemoryOf"/>), which is looked at ten times a second, as nothing else limits memory
    /// on both Linux and macOS: it can get ahead by what it allocates in that time, and that doesn't count a program it
    /// started and didn't become.
    /// </param>
    public static async Task<ProcessResult> RunWithOutputAsync(
        string executable,
        IEnumerable<string> arguments,
        Action<Stream, CancellationToken> readOutput,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? workingDirectory = null,
        long? memoryLimit = null)
    {
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(executable) ?? "",
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        using var job = memoryLimit is { } jobLimit && OperatingSystem.IsWindows() ? ProcessJob.TryCreate(jobLimit) : null;
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };
        if (!process.Start())
            throw new InvalidOperationException($"Could not start {executable}.");
        var held = job is not null && job.TryAssign(process);
        if (job is not null && !held)
            job.Dispose();
        Lower(process);
        process.StandardInput.Close();
        process.BeginErrorReadLine();

        var killedForMemory = false;
        using var memoryWatch = memoryLimit is { } limit && !held
            ? new Timer(_ =>
            {
                if (KillAbove(process, limit))
                    Volatile.Write(ref killedForMemory, true);
            }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(100)) : null;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        // A read waiting for output doesn't see the token; ending the program ends the read.
        using var stop = timeoutCts.Token.Register(() => TryKill(process, job));
        var reading = Task.Run(() => readOutput(process.StandardOutput.BaseStream, timeoutCts.Token), CancellationToken.None);
        try
        {
            // Not left to the read to end: a program it started can hold the output after it has gone.
            await reading.WaitAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
            // Stopping it ends its output, and it may have gone by now: that isn't a program that finished.
            timeoutCts.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            TryKill(process, job);
            await WaitUntilGoneAsync(process);
            Observe(reading);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(executable)} did not finish within {timeout.TotalSeconds:0} s.");
        }
        catch
        {
            TryKill(process, job);
            await WaitUntilGoneAsync(process);
            Observe(reading);
            throw;
        }

        // Looking at a process can get in the way of reading how it ended.
        memoryWatch?.Dispose();
        AppLocalRuntime.ThrowIfMissing(executable, process.ExitCode);
        var stoppedForMemory = Volatile.Read(ref killedForMemory) || (held && job!.WentOverMemoryLimit());
        lock (stderr)
            return new ProcessResult(process.ExitCode, "", stderr.ToString()) { StoppedForMemory = stoppedForMemory };
    }

    /// <summary>
    /// A read given up on can still fail, once its program is stopped: looked at, so it isn't reported as an error
    /// nobody saw.
    /// </summary>
    private static void Observe(Task reading) =>
        _ = reading.ContinueWith(t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// <summary>
    /// True once <paramref name="ready"/> has held on 4 polls in a row (2 s); false when the program exits or the wait is
    /// cancelled first.
    /// </summary>
    private static async Task<bool> WatchOutputAsync(Func<bool> ready, Task exited, CancellationToken cancellationToken)
    {
        var streak = 0;
        while (streak < 4)
        {
            await Task.WhenAny(exited, Task.Delay(500, cancellationToken));
            if (exited.IsCompleted || cancellationToken.IsCancellationRequested)
                return false;

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
        }
        return true;
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

    /// <summary>Killing a program only starts its end, and a caller may be about to delete its folder.</summary>
    private static async Task WaitUntilGoneAsync(Process process)
    {
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await process.WaitForExitAsync(wait.Token);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Kills <paramref name="process"/> when it holds more than <paramref name="memoryLimit"/>; true when so.
    /// </summary>
    private static bool KillAbove(Process process, long memoryLimit)
    {
        try
        {
            if (MemoryOf(process) <= memoryLimit)
                return false;
            TryKill(process, null);
            return true;
        }
        // It has exited, the run is over, or another tick is looking too: anything that gets out of a timer ends Filee.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>
    /// The memory <paramref name="process"/> holds, as the system counts it against it: on macOS its footprint, which
    /// takes in what the system compressed when memory ran short (a program's resident memory leaves that out, so a
    /// program holding gigabytes of zeros could look small), on Linux its resident memory and what went to swap.
    /// </summary>
    internal static long MemoryOf(Process process)
    {
        process.Refresh();
        var resident = process.WorkingSet64;
        if (OperatingSystem.IsMacOS() && proc_pid_rusage(process.Id, RusageInfoV0, out var usage) == 0)
            return Math.Max(resident, (long)usage.PhysFootprint);
        if (OperatingSystem.IsLinux() && LinuxMemoryOf(process.Id) is { } linux)
            return Math.Max(resident, linux);
        return resident;
    }

    /// <summary>VmRSS and VmSwap of /proc/<paramref name="pid"/>/status, in bytes; null when it can't be read.</summary>
    private static long? LinuxMemoryOf(int pid)
    {
        try
        {
            long? total = null;
            foreach (var line in File.ReadLines($"/proc/{pid}/status"))
            {
                if (!line.StartsWith("VmRSS:", StringComparison.Ordinal) && !line.StartsWith("VmSwap:", StringComparison.Ordinal))
                    continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && long.TryParse(parts[1], out var kilobytes))
                    total = (total ?? 0) + kilobytes * 1024;
            }
            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // it has exited
        }
    }

    private const int RusageInfoV0 = 0;

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern int proc_pid_rusage(int pid, int flavor, out RusageInfo info);

    /// <summary>rusage_info_v0 of &lt;sys/resource.h&gt;.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RusageInfo
    {
        public Guid Uuid;
        public ulong UserTime;
        public ulong SystemTime;
        public ulong PkgIdleWakeups;
        public ulong InterruptWakeups;
        public ulong PageIns;
        public ulong WiredSize;
        public ulong ResidentSize;
        public ulong PhysFootprint;
        public ulong ProcessStartAbsoluteTime;
        public ulong ProcessExitAbsoluteTime;
    }

    private static void TryKill(Process process, ProcessJob? job)
    {
        // The job first: the program may already have exited while a child it started still runs.
        job?.Terminate();
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        catch (NotSupportedException) { }
        catch (AggregateException) { } // a part of the process tree that couldn't be killed
    }

    /// <summary>
    /// Tracks a process and its children on Windows. Closing the Job Object kills all processes
    /// still associated with it.
    /// </summary>
    private sealed class ProcessJob : IDisposable
    {
        private const uint JobObjectLimitKillOnJobClose = 0x2000;
        private const uint JobObjectLimitJobMemory = 0x200;
        private const int JobObjectExtendedLimitInformationClass = 9;
        private const int JobObjectAssociateCompletionPortInformationClass = 7;
        private const uint JobObjectMsgJobMemoryLimit = 10;

        private readonly SafeJobHandle _handle;
        private readonly SafeJobHandle? _port;

        private ProcessJob(SafeJobHandle handle, SafeJobHandle? port)
        {
            _handle = handle;
            _port = port;
        }

        /// <param name="memoryLimit">The most memory the programs in the job may use together, in bytes.</param>
        public static ProcessJob? TryCreate(long? memoryLimit = null)
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
                    LimitFlags = JobObjectLimitKillOnJobClose | (memoryLimit is null ? 0 : JobObjectLimitJobMemory)
                },
                JobMemoryLimit = (UIntPtr)(ulong)(memoryLimit ?? 0),
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

            // A program refused memory by the job may only print that it ran out: the job says so on its port.
            SafeJobHandle? port = null;
            if (memoryLimit is not null)
            {
                port = CreateIoCompletionPort(new IntPtr(-1), IntPtr.Zero, UIntPtr.Zero, 1);
                var association = new JobObjectAssociateCompletionPort { CompletionKey = IntPtr.Zero, CompletionPort = port.DangerousGetHandle() };
                if (port.IsInvalid || !SetInformationJobObject(handle, JobObjectAssociateCompletionPortInformationClass, ref association,
                        (uint)Marshal.SizeOf<JobObjectAssociateCompletionPort>()))
                {
                    port.Dispose();
                    handle.Dispose();
                    return null;
                }
            }

            return new ProcessJob(handle, port);
        }

        /// <summary>A program in the job was refused memory over the job's limit (it may have ended since).</summary>
        public bool WentOverMemoryLimit()
        {
            if (_port is null || _port.IsClosed || _port.IsInvalid)
                return false;
            var over = false;
            try
            {
                // The messages that came so far, without waiting for more.
                while (GetQueuedCompletionStatus(_port, out var message, out _, out _, 0))
                    over |= message == JobObjectMsgJobMemoryLimit;
            }
            catch (ObjectDisposedException)
            {
            }
            return over;
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
            _port?.Dispose();
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
        private static extern bool SetInformationJobObject(
            SafeJobHandle job,
            int informationClass,
            ref JobObjectAssociateCompletionPort information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeJobHandle CreateIoCompletionPort(IntPtr fileHandle, IntPtr existingPort, UIntPtr key, uint threads);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetQueuedCompletionStatus(SafeJobHandle port, out uint bytes, out UIntPtr key, out IntPtr overlapped,
            uint milliseconds);

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
        private struct JobObjectAssociateCompletionPort
        {
            public IntPtr CompletionKey;
            public IntPtr CompletionPort;
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
