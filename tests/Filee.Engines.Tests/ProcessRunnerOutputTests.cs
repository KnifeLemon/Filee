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
        var started = Stopwatch.StartNew();

        var result = await ProcessRunner.RunAsync(Cmd, ["/c", script], TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken,
            outputReady: () =>
            {
                var ready = complete();
                if (ready)
                    held++;
                return ready;
            });

        Assert.Equal(0, result.ExitCode);
        Assert.True(held >= 4, $"the run ended by itself after {held} ready polls");
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(20), $"took {started.Elapsed}");
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
        // Under the 30 s the rest of the output may take: the job ended the child, so the output ended too.
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(20), $"took {started.Elapsed}");
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
        var script = Script("""
            @echo off
            >"%~dp0out\out.pdf" echo %%PDF-1.4
            ping -n 4 127.0.0.1 >nul
            exit /b 3
            """);
        var complete = LibreOfficeConverter.OutputComplete(Output);
        var polls = 0;

        var result = await ProcessRunner.RunAsync(Cmd, ["/c", script], TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken,
            outputReady: () =>
            {
                polls++;
                return complete();
            });

        Assert.Equal(3, result.ExitCode);
        Assert.True(polls >= 2, $"probed {polls} times");
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
