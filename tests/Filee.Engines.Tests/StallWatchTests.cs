// The stall watch that lets calibre (#651) and FFmpeg run as long as they keep reporting progress, and stops them
// once they go silent. The program is faked: its exit is a task, its output lines are Touch calls, and its clock is a
// fake one the test moves by hand, so a busy CI runner cannot stretch a pause into a stall.

using System.Diagnostics;
using Filee.Core.Conversion;
using Filee.Engines.Ebooks;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Tests;

public sealed class StallWatchTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// A clock that moves only when the test advances it. Its timers (Task.Delay makes one-shot ones) fire from
    /// <see cref="Advance"/>.
    /// </summary>
    private sealed class FakeTime : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<FakeTimer> _timers = [];
        private readonly SemaphoreSlim _created = new(0);
        private long _now;
        private int _timersCreated;

        public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _now));

        public int TimersCreated => Volatile.Read(ref _timersCreated);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _now);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(this, callback, state);
            timer.Change(dueTime, period);
            Interlocked.Increment(ref _timersCreated);
            _created.Release();
            return timer;
        }

        /// <summary>Completes once the next timer is created: the watch has checked and waits for its next check.</summary>
        public Task NextTimerAsync(CancellationToken cancellationToken) => _created.WaitAsync(cancellationToken);

        public void Advance(TimeSpan by)
        {
            var now = Interlocked.Add(ref _now, by.Ticks);
            List<FakeTimer> due;
            lock (_gate)
            {
                due = _timers.FindAll(t => t.Due <= now);
                _timers.RemoveAll(t => t.Due <= now);
            }
            // Outside the lock: the callback completes the delay, and the watch may create its next timer right away.
            foreach (var timer in due)
                timer.Fire();
        }

        private sealed class FakeTimer(FakeTime time, TimerCallback callback, object? state) : ITimer
        {
            public long Due { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (time._gate)
                {
                    time._timers.Remove(this);
                    if (dueTime == Timeout.InfiniteTimeSpan)
                        return true;
                    Due = time.GetTimestamp() + dueTime.Ticks;
                    time._timers.Add(this);
                }
                return true;
            }

            public void Fire() => callback(state);

            public void Dispose()
            {
                lock (time._gate)
                    time._timers.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>
    /// Moves the clock one check interval at a time, each time the watch waits for its next check, until the wait
    /// ends; <paramref name="onStep"/> runs after every step (the program writing a line, exiting, being cancelled).
    /// </summary>
    private static async Task<bool> DriveAsync(FakeTime time, Task<bool> waiting, Action<int> onStep, CancellationToken cancellationToken)
    {
        for (var step = 1; ; step++)
        {
            await Task.WhenAny(waiting, time.NextTimerAsync(cancellationToken));
            if (waiting.IsCompleted)
                return await waiting;
            if (step > 10_000)
                Assert.Fail($"the wait did not end after {time.Elapsed}");
            time.Advance(Interval);
            onStep(step);
        }
    }

    [Fact]
    public async Task A_program_that_keeps_reporting_runs_well_past_the_stall_limit()
    {
        var token = TestContext.Current.CancellationToken;
        var time = new FakeTime();
        var watch = new StallWatch(TimeSpan.FromMilliseconds(300), time);
        var exited = new TaskCompletionSource();

        // "34% Running transforms on e-book..." every 50 ms for three seconds, ten times the limit.
        var ran = await DriveAsync(time, watch.WaitAsync(exited.Task, token, Interval), step =>
        {
            if (step % 5 == 0)
                watch.Touch();
            if (time.Elapsed >= TimeSpan.FromSeconds(3))
                exited.TrySetResult();
        }, token);

        Assert.True(ran);
        Assert.True(time.Elapsed >= TimeSpan.FromSeconds(3));
        Assert.True(time.TimersCreated >= 300, $"the watch checked {time.TimersCreated} times in {time.Elapsed}");
        Assert.False(watch.IsStalled);
    }

    [Fact]
    public async Task A_program_that_goes_silent_is_reported_as_stalled()
    {
        var token = TestContext.Current.CancellationToken;
        var time = new FakeTime();
        var watch = new StallWatch(TimeSpan.FromMilliseconds(200), time);
        var exited = new TaskCompletionSource(); // never exits
        var lastLine = TimeSpan.Zero;

        // A line every 100 ms for 300 ms, then nothing.
        var ran = await DriveAsync(time, watch.WaitAsync(exited.Task, token, Interval), step =>
        {
            if (step <= 30 && step % 10 == 0)
            {
                watch.Touch();
                lastLine = time.Elapsed;
            }
        }, token);

        Assert.False(ran);
        Assert.True(watch.IsStalled);
        Assert.Equal(TimeSpan.FromMilliseconds(300), lastLine);
        var silence = time.Elapsed - lastLine;
        Assert.True(silence > watch.Limit && silence <= watch.Limit + Interval,
            $"the silence counts from the last line and ends at the first check past the limit, stalled after {silence}");
    }

    [Fact]
    public async Task A_program_that_exits_before_the_limit_is_not_stalled()
    {
        var watch = new StallWatch(TimeSpan.FromMinutes(10), new FakeTime());

        Assert.True(await watch.WaitAsync(Task.CompletedTask, TestContext.Current.CancellationToken, Interval));
        Assert.False(watch.IsStalled);
    }

    [Fact]
    public async Task Cancelling_stops_the_wait_even_while_the_program_reports()
    {
        var time = new FakeTime();
        var watch = new StallWatch(TimeSpan.FromMinutes(10), time);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DriveAsync(time, watch.WaitAsync(new TaskCompletionSource().Task, cancel.Token, Interval), step =>
            {
                watch.Touch();
                if (step == 5)
                    cancel.Cancel();
            }, TestContext.Current.CancellationToken));
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
