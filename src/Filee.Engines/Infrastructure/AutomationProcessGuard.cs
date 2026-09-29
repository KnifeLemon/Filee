// COM automation servers (Hwp.exe, WINWORD.EXE) sometimes hang on a hidden dialog or survive a failed Quit().
// This guard remembers which processes existed before a conversion and kills only the hidden instances that
// the conversion started, so a stuck conversion never leaves invisible Office processes behind.

using System.Diagnostics;
using System.Runtime.Versioning;

namespace Filee.Engines.Infrastructure;

[SupportedOSPlatform("windows")]
internal sealed class AutomationProcessGuard
{
    private readonly string _processName;
    private readonly HashSet<int> _existing;
    private readonly DateTime _startedAt = DateTime.Now.AddSeconds(-1);

    /// <param name="processName">Process name without ".exe", e.g. "Hwp" or "WINWORD".</param>
    public AutomationProcessGuard(string processName)
    {
        _processName = processName;
        _existing = Snapshot(processName);
    }

    /// <summary>
    /// Runs a COM conversion with a timeout. On timeout, cancellation or error, automation processes started by
    /// this conversion are killed (which also unblocks the COM call).
    /// </summary>
    public async Task RunAsync(Func<bool> work, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var task = StaThread.RunAsync(work, cancellationToken);
        try
        {
            var finished = await Task.WhenAny(task, Task.Delay(timeout, cancellationToken));
            if (finished != task)
            {
                KillLeftovers();
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException($"{_processName} did not finish within {timeout.TotalSeconds:0} s.");
            }
            await task;
        }
        catch
        {
            KillLeftovers();
            throw;
        }
        finally
        {
            // Quit() normally ends the server; give it a moment, then remove anything still hanging around.
            _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => KillLeftovers(), TaskScheduler.Default);
        }
    }

    /// <summary>Kills processes of this name that were started after the guard was created (i.e. by this conversion).</summary>
    public void KillLeftovers()
    {
        foreach (var process in Process.GetProcessesByName(_processName))
        {
            using (process)
            {
                try
                {
                    if (!_existing.Contains(process.Id) && process.StartTime >= _startedAt)
                        process.Kill();
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Already gone or not accessible.
                }
            }
        }
    }

    private static HashSet<int> Snapshot(string name)
    {
        var processes = Process.GetProcessesByName(name);
        var ids = processes.Select(p => p.Id).ToHashSet();
        foreach (var p in processes)
            p.Dispose();
        return ids;
    }
}
