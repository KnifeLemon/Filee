// A later launch hands its arguments to the running Filee, also while that one is still starting.

using Filee.App.Services;

namespace Filee.App.Tests;

public class SingleInstanceTests
{
    [Fact]
    public async Task Arguments_reach_a_first_instance_that_starts_listening_late()
    {
        // The installer's --quit (or a second launch) came 8 s after Filee started, on a computer where Filee took 12 s
        // to start listening: one 3-second try gave up and the request was lost.
        var name = "Filee-test-" + Guid.NewGuid().ToString("N")[..8];
        using var first = SingleInstance.Acquire(name);
        Assert.True(first.IsFirst);
        var received = new TaskCompletionSource<string[]>();
        first.ArgumentsReceived += args => received.TrySetResult(args);

        var forward = Task.Run(() =>
        {
            using var second = SingleInstance.Acquire(name);
            Assert.False(second.IsFirst);
            return second.ForwardToFirst(["--quit"], TimeSpan.FromSeconds(15));
        }, TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken); // longer than the old single try
        first.StartListening();

        Assert.True(await forward.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
        Assert.Equal(["--quit"], await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_burst_of_later_launches_loses_nothing()
    {
        // Explorer starts one Filee per selected file (issue #767): they all queue for the one pipe at the same time.
        var name = "Filee-test-" + Guid.NewGuid().ToString("N")[..8];
        using var first = SingleInstance.Acquire(name);
        var received = new System.Collections.Concurrent.ConcurrentBag<string>();
        first.ArgumentsReceived += args => received.Add(args[^1]);
        first.StartListening();

        var results = await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Factory.StartNew(() =>
        {
            using var later = SingleInstance.Acquire(name);
            return later.Forward(["--convert", $"{i}.png"], TimeSpan.FromSeconds(30));
        }, TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default)));

        Assert.All(results, result => Assert.Equal(ForwardResult.Delivered, result));
        // Delivered means written: the first instance reads the last ones a moment later. One that never comes fails here.
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (received.Count < 200 && waited.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.Equal(200, received.Distinct().Count());
    }

    [Theory]
    [InlineData("out of file handles")]
    [InlineData("a peer of another user")]
    [InlineData("a peer that went away")]
    public async Task An_error_while_waiting_keeps_the_launches_queued_and_keeps_listening(string error)
    {
        // Launches behind the error have heard Delivered already: on macOS and Linux they wait on the listening socket,
        // which has to stay open for them. The thrown error stands in for one that takes no launch along.
        var name = "Filee-test-" + Guid.NewGuid().ToString("N")[..8];
        using var first = SingleInstance.Acquire(name);
        var received = new System.Collections.Concurrent.ConcurrentBag<string>();
        first.ArgumentsReceived += args => received.Add(args[^1]);
        var fail = new TaskCompletionSource();
        var failed = 0;
        var wait = first.WaitForLaunch;
        first.WaitForLaunch = async (server, token) =>
        {
            if (Interlocked.Exchange(ref failed, 1) == 0)
            {
                await fail.Task.WaitAsync(token);
                throw error switch
                {
                    "out of file handles" => new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.TooManyOpenSockets),
                    "a peer of another user" => new UnauthorizedAccessException(),
                    _ => new IOException(),
                };
            }
            await wait(server, token);
        };
        first.StartListening();

        // Windows has no socket to queue on: a launch connects to a pipe, and waiting there doesn't fail.
        string[] queued = OperatingSystem.IsWindows() ? [] : ["1.png", "2.png", "3.png"];
        foreach (var launch in queued)
            Assert.Equal(ForwardResult.Delivered, await Forward(name, launch));
        fail.SetResult();
        Assert.Equal(ForwardResult.Delivered, await Forward(name, "later.png"));

        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (received.Count < queued.Length + 1 && waited.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.Equal([.. queued, "later.png"], received.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_launch_that_never_sends_its_arguments_does_not_hold_up_the_next()
    {
        var name = "Filee-test-" + Guid.NewGuid().ToString("N")[..8];
        using var first = SingleInstance.Acquire(name);
        var arrived = new TaskCompletionSource<string>();
        first.ArgumentsReceived += args => arrived.TrySetResult(args[^1]);
        first.ReadTimeout = TimeSpan.FromMilliseconds(200);
        first.StartListening();

        using var stalled = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.Out,
            System.IO.Pipes.PipeOptions.CurrentUserOnly);
        await stalled.ConnectAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        _ = Forward(name, "later.png"); // on Windows it returns when it's read
        Assert.Equal("later.png", await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_handler_that_throws_does_not_end_listening(bool cancellation)
    {
        var name = "Filee-test-" + Guid.NewGuid().ToString("N")[..8];
        using var first = SingleInstance.Acquire(name);
        var received = new System.Collections.Concurrent.ConcurrentBag<string>();
        var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        first.ListeningFailed += failures.Add;
        first.ArgumentsReceived += args =>
        {
            received.Add(args[^1]);
            if (args[^1] == "throws.png")
                throw cancellation ? new OperationCanceledException() : new InvalidOperationException();
        };
        first.StartListening();

        Assert.Equal(ForwardResult.Delivered, await Forward(name, "throws.png"));
        Assert.Equal(ForwardResult.Delivered, await Forward(name, "later.png"));

        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (received.Count < 2 && waited.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.Contains("later.png", received);
        Assert.Single(failures);
    }

    [Fact]
    public void A_filee_that_starts_after_the_last_one_closed_gets_the_launches()
    {
        // Not async: the mutex belongs to the thread that took it, and a restart releases it there.
        var name = "Filee-test-" + Guid.NewGuid().ToString("N")[..8];
        for (var run = 0; run < 2; run++)
        {
            var arrived = new ManualResetEventSlim();
            using var first = SingleInstance.Acquire(name);
            Assert.True(first.IsFirst);
            first.ArgumentsReceived += _ => arrived.Set();
            first.StartListening();

            // The last one's listener closes a moment after it, and takes a launch that reached it along.
            var waited = System.Diagnostics.Stopwatch.StartNew();
            do
            {
                var launch = new Thread(() =>
                {
                    using var later = SingleInstance.Acquire(name);
                    later.Forward(["--convert", "a.png"], TimeSpan.FromSeconds(1));
                });
                launch.Start();
                launch.Join();
            } while (!arrived.Wait(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken)
                && waited.Elapsed < TimeSpan.FromSeconds(10));
            Assert.True(arrived.IsSet);
        }
    }

    [Fact]
    public void A_filee_that_closed_takes_no_more_launches()
    {
        // Not async: the mutex belongs to the thread that took it. A socket left open would keep saying Delivered.
        var name = "Filee-test-" + Guid.NewGuid().ToString("N")[..8];
        using var first = SingleInstance.Acquire(name);
        var arrived = new ManualResetEventSlim();
        first.ArgumentsReceived += _ => arrived.Set();
        first.StartListening();
        var launch = new Thread(() =>
        {
            using var later = SingleInstance.Acquire(name);
            later.Forward(["--convert", "a.png"], TimeSpan.FromSeconds(15));
        });
        launch.Start();
        launch.Join();
        Assert.True(arrived.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        first.Dispose();

        var result = ForwardResult.Delivered;
        var afterwards = new Thread(() =>
        {
            using var later = SingleInstance.Acquire(name);
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (result == ForwardResult.Delivered && waited.Elapsed < TimeSpan.FromSeconds(10))
            {
                result = later.Forward(["--convert", "b.png"], TimeSpan.FromMilliseconds(200));
                // Quick tries would fill the socket's queue of 128 on macOS, which also ends in NoAnswer.
                Thread.Sleep(100);
            }
        });
        afterwards.Start();
        afterwards.Join();
        Assert.Equal(ForwardResult.NoAnswer, result);
    }

    [Fact]
    public void A_copy_with_its_own_data_folder_never_reaches_the_users_filee()
    {
        // The name without a data folder must stay the same: a new version forwards to an older one that runs.
        var usual = "Filee-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("someone"u8.ToArray()))[..12];
        Assert.Equal(usual, SingleInstance.NameFor("someone", null));
        var test = SingleInstance.NameFor("someone", Path.Combine(Path.GetTempPath(), "filee-a"));
        Assert.NotEqual(usual, test);
        Assert.NotEqual(test, SingleInstance.NameFor("someone", Path.Combine(Path.GetTempPath(), "filee-b")));
        Assert.Equal(test, SingleInstance.NameFor("someone", Path.Combine(Path.GetTempPath(), "filee-a")));
    }

    [Fact]
    public async Task Forwarding_gives_up_when_nobody_listens()
    {
        var name = "Filee-test-" + Guid.NewGuid().ToString("N")[..8];
        using var first = SingleInstance.Acquire(name);

        var delivered = await Task.Run(() =>
        {
            using var instance = SingleInstance.Acquire(name);
            return instance.ForwardToFirst(["--quit"], TimeSpan.FromSeconds(1));
        }, TestContext.Current.CancellationToken);

        Assert.False(delivered);
    }

    [Fact]
    public async Task Forwarding_tells_that_nobody_answered()
    {
        var name = "Filee-test-" + Guid.NewGuid().ToString("N")[..8];
        using var first = SingleInstance.Acquire(name);

        var result = await Task.Run(() =>
        {
            using var instance = SingleInstance.Acquire(name);
            return instance.Forward(["--convert", "a.txt"], TimeSpan.FromSeconds(1));
        }, TestContext.Current.CancellationToken);

        Assert.Equal(ForwardResult.NoAnswer, result);
    }

    [Fact]
    public void Releasing_twice_is_harmless()
    {
        // A restart without administrator rights releases the lock early; Program releases it again at exit.
        var name = "Filee-test-" + Guid.NewGuid().ToString("N")[..8];
        var first = SingleInstance.Acquire(name);
        first.Dispose();
        first.Dispose();

        using var next = SingleInstance.Acquire(name);
        Assert.True(next.IsFirst);
    }

    private static Task<ForwardResult> Forward(string name, string file) => Task.Run(() =>
    {
        using var later = SingleInstance.Acquire(name);
        return later.Forward(["--convert", file], TimeSpan.FromSeconds(15));
    }, TestContext.Current.CancellationToken);
}

/// <summary>Windows: a running Filee with other rights (as administrator) refuses a normal launch.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class SingleInstanceAccessTests
{
    [Fact]
    public async Task A_pipe_that_refuses_this_process_is_given_up_on_quickly()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows pipe security.");
        var name = "Filee-test-" + Guid.NewGuid().ToString("N")[..8];
        using var first = SingleInstance.Acquire(name);
        // Only SYSTEM may write: what a pipe of an elevated Filee looks like to a normal process.
        var security = new System.IO.Pipes.PipeSecurity();
        security.AddAccessRule(new System.IO.Pipes.PipeAccessRule(
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
            System.IO.Pipes.PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        await using var server = System.IO.Pipes.NamedPipeServerStreamAcl.Create(name, System.IO.Pipes.PipeDirection.In, 1,
            System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous, 0, 0, security);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var result = await Task.Run(() =>
        {
            using var second = SingleInstance.Acquire(name);
            return second.Forward(["--convert", "a.txt"], TimeSpan.FromSeconds(30));
        }, TestContext.Current.CancellationToken);

        Assert.Equal(ForwardResult.AccessDenied, result);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    [Fact]
    public void The_integrity_level_of_this_process_can_be_read()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows tokens.");
        var state = Filee.Platform.Windows.Elevation.Probe();
        Assert.True(state.Process >= Filee.Core.Platform.IntegrityLevel.Medium, state.ToString());
        // Explorer may not run (CI): then its level is unknown and nothing is reported.
        if (state.Shell == Filee.Core.Platform.IntegrityLevel.Unknown)
            Assert.False(state.ShellCannotReachFilee);
    }
}
