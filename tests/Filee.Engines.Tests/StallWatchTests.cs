// The stall watch that lets calibre (#651) and FFmpeg run as long as they keep reporting progress, and stops them
// once they go silent. The program is faked: its exit is a task, its output lines are Touch calls.

using System.Diagnostics;
using Filee.Core.Conversion;
using Filee.Engines.Ebooks;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Tests;

public sealed class StallWatchTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(20);

    [Fact]
    public async Task A_program_that_keeps_reporting_runs_well_past_the_stall_limit()
    {
        var token = TestContext.Current.CancellationToken;
        // A limit far above the 50 ms between lines: a busy CI runner can delay a 50 ms wait by a few hundred ms.
        var watch = new StallWatch(TimeSpan.FromSeconds(1));
        var exited = new TaskCompletionSource();
        var started = Stopwatch.StartNew();

        // "34% Running transforms on e-book..." every 50 ms for three seconds, three times the limit.
        var output = Task.Run(async () =>
        {
            while (started.Elapsed < TimeSpan.FromSeconds(3))
            {
                watch.Touch();
                await Task.Delay(50, token);
            }
            exited.SetResult();
        }, token);

        Assert.True(await watch.WaitAsync(exited.Task, token, Interval));
        Assert.True(started.Elapsed >= TimeSpan.FromSeconds(3));
        await output;
    }

    [Fact]
    public async Task A_program_that_goes_silent_is_reported_as_stalled()
    {
        var token = TestContext.Current.CancellationToken;
        var watch = new StallWatch(TimeSpan.FromMilliseconds(200));
        var exited = new TaskCompletionSource(); // never exits

        // A few lines of progress, then nothing.
        for (var i = 0; i < 3; i++)
        {
            await Task.Delay(100, token);
            watch.Touch();
        }
        var silent = Stopwatch.StartNew();

        Assert.False(await watch.WaitAsync(exited.Task, token, Interval));
        Assert.True(watch.IsStalled);
        Assert.True(silent.Elapsed >= TimeSpan.FromMilliseconds(190), $"the silence counts from the last line, stalled after {silent.Elapsed}");
    }

    [Fact]
    public async Task A_program_that_exits_before_the_limit_is_not_stalled()
    {
        var watch = new StallWatch(TimeSpan.FromMinutes(10));

        Assert.True(await watch.WaitAsync(Task.CompletedTask, TestContext.Current.CancellationToken, Interval));
        Assert.False(watch.IsStalled);
    }

    [Fact]
    public async Task Cancelling_stops_the_wait_even_while_the_program_reports()
    {
        var watch = new StallWatch(TimeSpan.FromMinutes(10));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            watch.WaitAsync(new TaskCompletionSource().Task, cancel.Token, Interval));
    }

    [Fact]
    public async Task ProcessRunner_stops_a_silent_program_with_the_stalled_reason()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows PowerShell as the silent program.");
        var started = Stopwatch.StartNew();

        var refused = await Assert.ThrowsAsync<ConversionRefusedException>(() =>
            ProcessRunner.RunAsync(PowerShell, ["-NoProfile", "-Command", "Start-Sleep -Seconds 60"], Timeout.InfiniteTimeSpan,
                TestContext.Current.CancellationToken, stallTimeout: TimeSpan.FromSeconds(2)));

        Assert.Equal(ProcessRunner.StalledErrorKey, refused.ErrorKey);
        Assert.Equal("powershell.exe, 2 s", refused.Detail);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(30), $"stopped after {started.Elapsed}");
    }

    [Fact]
    public async Task ProcessRunner_cancellation_still_wins_over_the_stall_watch()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows PowerShell as the long program.");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProcessRunner.RunAsync(PowerShell, ["-NoProfile", "-Command", "Start-Sleep -Seconds 60"], Timeout.InfiniteTimeSpan,
                cancel.Token, stallTimeout: TimeSpan.FromMinutes(10)));
    }

    [Theory]
    [InlineData(600, "10 min")]
    [InlineData(120, "2 min")]
    [InlineData(30, "30 s")]
    public void The_stall_limit_reads_in_minutes_or_seconds(int seconds, string expected) =>
        Assert.Equal(expected, ProcessRunner.Describe(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Calibre_stops_only_after_at_least_as_long_a_silence_as_its_former_fixed_limit() =>
        Assert.True(CalibreConverter.StallTimeout >= TimeSpan.FromMinutes(10));

    private static string PowerShell =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
}
