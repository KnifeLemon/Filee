// A program that leaves a child holding Filee's output pipes (#1011): soffice.exe exits, but soffice.bin keeps the
// inherited pipes open, so waiting for the end of the output would last until the timeout. And the output probe, for
// a program that finished its file but doesn't exit.

using System.Diagnostics;
using Filee.Engines.Infrastructure;
using Filee.Engines.Office;

namespace Filee.Engines.Tests;

public sealed class ProcessRunnerOutputTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("filee-runner-").FullName;

    /// <summary>The output folder, like LibreOffice's: the probe takes the first file in it.</summary>
    private string Output => Path.Combine(_root, "out");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task A_program_over_its_memory_limit_doesnt_get_to_finish()
    {
        // 1.5 GB, a little at a time, as a program that fills its memory does. On Windows it can't allocate the rest,
        // elsewhere it is killed: either way it never gets to the end. Elsewhere its memory is only looked at now and
        // then, later still on a busy runner, so it takes its time and holds what it has before the end: all at once it
        // could get to the end between two looks.
        var (program, arguments) = OperatingSystem.IsWindows()
            ? ("powershell.exe", new[]
            {
                "-NoProfile", "-Command",
                "$ErrorActionPreference = 'Stop'; [Console]::Out.WriteLine('started'); $held = @(); 1..150 | ForEach-Object { $block = [byte[]]::new(10MB); [Array]::Clear($block, 0, $block.Length); $held += ,$block }; 'reached the end'",
            })
            : ("/usr/bin/env", ["perl", "-e", "$| = 1; print qq(started\\n); my @held; for (1..150) { push @held, 'a' x 10_000_000; select(undef, undef, undef, 0.02) } sleep 10; print 'reached the end'; sleep 30"]);
        var timeout = TimeSpan.FromMinutes(3);
        var printed = new StringWriter();
        var clock = Stopwatch.StartNew();
        var started = TimeSpan.Zero;

        var result = await ProcessRunner.RunWithOutputAsync(program, arguments, (output, _) =>
            {
                var reader = new StreamReader(output);
                printed.WriteLine(reader.ReadLine());
                started = clock.Elapsed;
                printed.Write(reader.ReadToEnd());
            },
            timeout, TestContext.Current.CancellationToken, memoryLimit: 500L * 1024 * 1024);

        Assert.StartsWith("started", printed.ToString());
        Assert.DoesNotContain("reached the end", printed.ToString());
        // Timed from its first line, not from the start: PowerShell alone can take half a minute to start on a slow
        // runner (win-arm64). Stopped for memory, it ends in seconds, but a busy runner has taken 20 s for PowerShell to
        // give up at the limit: a minute leaves room for that, well short of the timeout, which would throw.
        var ran = clock.Elapsed - started;
        Assert.True(ran < timeout / 3, $"ran {ran} after it started");
        Assert.True(result.StoppedForMemory); // so a cut output isn't taken for a file it couldn't read
    }

    [Fact]
    public async Task A_programs_memory_is_what_it_holds()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Windows limits memory with a Job Object.");
        // Zeros, as a file made to fill the memory holds: what the system can compress when memory runs short.
        using var program = Process.Start(new ProcessStartInfo("/usr/bin/env", ["perl", "-e", "$| = 1; my $held = qq(\\0) x 400_000_000; print qq(held\\n); sleep 30"])
        {
            RedirectStandardOutput = true,
        })!;
        try
        {
            Assert.Equal("held", await program.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken));

            var memory = ProcessRunner.MemoryOf(program);

            Assert.True(memory >= 400_000_000L, $"{memory} bytes");
        }
        finally
        {
            program.Kill();
        }
    }

    [Fact]
    public async Task A_read_given_up_on_doesnt_fail_where_nobody_looks()
    {
        // Cancelled while the read waits: it fails after the run has ended, as a read stopped part way does.
        var marker = Guid.NewGuid().ToString();
        var unobserved = new List<Exception>();
        void Seen(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.Flatten().InnerExceptions.Any(ex => ex.Message == marker))
                lock (unobserved)
                    unobserved.Add(e.Exception);
        }
        var failed = new ManualResetEventSlim();
        TaskScheduler.UnobservedTaskException += Seen;
        try
        {
            await Run();
            Assert.True(failed.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            await Task.Delay(200, TestContext.Current.CancellationToken); // the read's task ends just after its throw
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            lock (unobserved)
                Assert.Empty(unobserved);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Seen;
        }

        // Not inlined: the run's tasks are left to the collector once it returns.
        async Task Run()
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var (program, arguments) = OperatingSystem.IsWindows()
                ? ("powershell.exe", new[] { "-NoProfile", "-Command", "Start-Sleep 30" })
                : ("/bin/sh", ["-c", "sleep 30"]);
            cancel.CancelAfter(TimeSpan.FromMilliseconds(500));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.RunWithOutputAsync(program, arguments,
                (_, token) =>
                {
                    token.WaitHandle.WaitOne();
                    Thread.Sleep(300); // after the run has given up on it
                    failed.Set();
                    throw new IOException(marker);
                },
                TimeSpan.FromMinutes(1), cancel.Token, memoryLimit: 1024L * 1024 * 1024));
        }
    }

    [Fact]
    public async Task A_run_cancelled_as_the_program_ends_is_cancelled_not_finished()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "A shell.");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // Cancelling once the output has ended, with the program gone: nothing is left to cancel but the result.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.RunWithOutputAsync("/bin/sh", ["-c", "exit 0"],
            (output, _) =>
            {
                output.CopyTo(Stream.Null);
                Thread.Sleep(300);
                cancel.Cancel();
            },
            TimeSpan.FromMinutes(1), cancel.Token));
    }

    [Fact]
    public async Task A_program_within_its_memory_limit_runs_as_usual()
    {
        var (program, arguments) = OperatingSystem.IsWindows()
            ? ("powershell.exe", new[] { "-NoProfile", "-Command", "'reached the end'" })
            : ("/usr/bin/env", ["perl", "-e", "print 'reached the end'"]);
        var printed = new StringWriter();

        var result = await ProcessRunner.RunWithOutputAsync(program, arguments, (output, _) => printed.Write(new StreamReader(output).ReadToEnd()),
            TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken, memoryLimit: 1024L * 1024 * 1024);

        Assert.Contains("reached the end", printed.ToString());
        Assert.False(result.StoppedForMemory);
    }

    [Fact]
    public async Task A_finished_PDF_ends_a_run_that_goes_on()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "cmd.exe and a Job Object.");
        // The script writes a complete PDF and then waits a minute, like a soffice.exe that doesn't exit.
        var script = Script("""
            @echo off
            >"%~dp0out\out.pdf" echo %%PDF-1.4
            >>"%~dp0out\out.pdf" echo %%%%EOF
            ping -n 60 127.0.0.1 >nul
            """);
        var complete = LibreOfficeConverter.OutputComplete(Output);
        var held = 0;
        var clock = Stopwatch.StartNew();
        var firstReady = TimeSpan.Zero;
        var timeout = TimeSpan.FromSeconds(30);

        var result = await ProcessRunner.RunAsync(Cmd, ["/c", script], timeout, TestContext.Current.CancellationToken,
            outputReady: () =>
            {
                var ready = complete();
                if (ready && held++ == 0)
                    firstReady = clock.Elapsed;
                return ready;
            });

        // The script waits longer than the timeout, which would throw: the probe ended the run.
        Assert.Equal(0, result.ExitCode);
        Assert.True(held >= 4, $"the run ended by itself after {held} ready polls");
        // Timed from the first poll that found the PDF complete, not from the start, which a busy runner can delay.
        var after = clock.Elapsed - firstReady;
        Assert.True(after < timeout / 2, $"ended {after} after the PDF was complete");
    }

    [Fact]
    public async Task A_child_that_holds_the_output_ends_with_the_run()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "cmd.exe and a Job Object.");
        // The child writes its process id and waits a minute with the script's output pipes, which it inherits like
        // soffice.bin; the script writes a complete PDF and exits.
        var script = Script("""
            @echo off
            start /b %SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -NonInteractive -Command "[IO.File]::WriteAllText('%~dp0child.pid', $PID); Start-Sleep -Seconds 60"
            >"%~dp0out\out.pdf" echo %%PDF-1.4
            >>"%~dp0out\out.pdf" echo %%%%EOF
            """);
        var pidFile = Path.Combine(_root, "child.pid");
        var complete = LibreOfficeConverter.OutputComplete(Output);
        var started = Stopwatch.StartNew();

        var result = await ProcessRunner.RunAsync(Cmd, ["/c", script], TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken,
            outputReady: () => File.Exists(pidFile) && complete());

        Assert.Equal(0, result.ExitCode);
        // Under the 30 s that waiting for the rest of the output alone would take: the job ended the child, so the
        // output ended too.
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(30), $"took {started.Elapsed}");
        Assert.DoesNotContain("output not complete", result.StandardError);
        if (!File.Exists(pidFile))
            return; // The script exited before the child had started; the job ended it all the same.
        var pid = int.Parse(File.ReadAllText(pidFile).Trim());
        try
        {
            using var child = Process.GetProcessById(pid);
            Assert.True(child.WaitForExit(TimeSpan.FromSeconds(5)), $"the child {pid} still runs");
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
    }

    [Fact]
    public async Task A_PDF_without_its_end_marker_does_not_end_the_run()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "cmd.exe.");
        // A steady file for longer than the probe needs, but the PDF is not complete: the script's own exit code counts.
        // The script waits until the probe has seen the file as often as a complete one would take to end the run, and
        // not for a fixed time, which a busy runner can fill with a single poll; it gives up after about 20 s. (An exit
        // /b inside the loop's brackets would end cmd /c with 0.)
        var script = Script("""
            @echo off
            >"%~dp0out\out.pdf" echo %%PDF-1.4
            for /l %%i in (1,1,20) do (
                if exist "%~dp0seen" goto end
                ping -n 2 127.0.0.1 >nul
            )
            :end
            exit /b 3
            """);
        var seen = Path.Combine(_root, "seen");
        var complete = LibreOfficeConverter.OutputComplete(Output);
        var polls = 0;

        var result = await ProcessRunner.RunAsync(Cmd, ["/c", script], TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken,
            outputReady: () =>
            {
                var ready = complete();
                // The first poll that finds the file only takes its size; four steady ones after it would end the run.
                if (File.Exists(Path.Combine(Output, "out.pdf")) && ++polls == 5)
                    File.WriteAllText(seen, "");
                return ready;
            });

        Assert.Equal(3, result.ExitCode);
        Assert.True(polls >= 5, $"probed the file {polls} times");
    }

    private static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private string Script(string text)
    {
        Directory.CreateDirectory(Output);
        var script = Path.Combine(_root, "run.cmd");
        File.WriteAllText(script, text.ReplaceLineEndings("\r\n") + "\r\n");
        return script;
    }
}
