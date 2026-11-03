// "Run a program" in the job queue: once per file or once with all files, the output path handed over as {output},
// and how a failing, silent or too-slow program marks the files (the error keys the toast shows).

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class JobQueueRunProgramTests
{
    /// <summary>A preset that runs a program next to the source, handing over {input} and {output}.</summary>
    private static Preset ProgramPreset(string arguments = "{input} -o {output}", bool allFiles = false, string targetFormat = "txt") =>
        new()
        {
            Kind = PresetKind.RunProgram,
            TargetFormat = targetFormat,
            Program = new ProgramOptions { Executable = "C:\\Tools\\convert.exe", Arguments = arguments, AllFiles = allFiles },
        };

    private static async Task<ConversionJob> Run(JobQueue queue, IReadOnlyList<string> sources, Preset preset)
    {
        var job = new ConversionJob(sources, preset, "Test");
        await queue.RunAsync(job);
        return job;
    }

    /// <summary>A program runner that records what it was asked to run and does what the test wants.</summary>
    private sealed class FakeProgramRunner : IProgramRunner
    {
        public List<(string Executable, IReadOnlyList<string> Arguments)> Runs { get; } = [];

        /// <summary>What to do per run: write the file a run would make (default: the argument after "-o").</summary>
        public Func<string, IReadOnlyList<string>, Task>? OnRun { get; set; }

        public int ExitCode { get; set; }

        public async Task<ProgramRunResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Runs.Add((executable, arguments));
            if (OnRun is { } run)
                await run(executable, arguments);
            return new ProgramRunResult(ExitCode, "");
        }

        /// <summary>Writes the file named after the argument following "-o".</summary>
        public static Task WriteDashO(string _, IReadOnlyList<string> arguments)
        {
            var output = arguments[arguments.ToList().IndexOf("-o") + 1];
            File.WriteAllText(output, "out");
            return Task.CompletedTask;
        }
    }

    private static JobQueue Queue(FakeProgramRunner runner) =>
        new(new ConverterCatalog([]), programRunner: runner);

    // ───────────────────────── Per file ─────────────────────────

    [Fact]
    public async Task Runs_once_per_file_and_collects_each_output()
    {
        using var dir = new TempDir();
        var a = dir.File("a.jpg");
        var b = dir.File("b.jpg");
        var runner = new FakeProgramRunner { OnRun = FakeProgramRunner.WriteDashO };
        await using var queue = Queue(runner);

        var job = await Run(queue, [a, b], ProgramPreset());

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(2, runner.Runs.Count);
        Assert.Equal(["C:\\Tools\\convert.exe", "C:\\Tools\\convert.exe"], runner.Runs.Select(r => r.Executable));
        Assert.Equal([a, "-o"], runner.Runs[0].Arguments.Take(2));
        Assert.Equal([b, "-o"], runner.Runs[1].Arguments.Take(2));
        Assert.All(job.Files, f => Assert.Equal(FileState.Done, f.State));
        Assert.Equal(2, job.Outputs.Count());
        Assert.True(File.Exists(Path.Combine(dir.Path, "a.txt")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "b.txt")));
    }

    [Fact]
    public async Task A_program_making_its_own_files_still_has_them_collected()
    {
        using var dir = new TempDir();
        var a = dir.File("a.jpg");
        var runner = new FakeProgramRunner
        {
            // Ignores {output} and writes something else into the output folder.
            OnRun = (_, arguments) =>
            {
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(arguments[^1]))!, "made-by-program.txt"), "x");
                return Task.CompletedTask;
            },
        };
        await using var queue = Queue(runner);

        var job = await Run(queue, [a], ProgramPreset(arguments: "{input}"));

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal([Path.Combine(dir.Path, "made-by-program.txt")], job.Outputs);
    }

    // ───────────────────────── All files ─────────────────────────

    [Fact]
    public async Task All_files_mode_runs_once_with_every_path_as_its_own_argument()
    {
        using var dir = new TempDir();
        var a = dir.File("a.jpg");
        var b = dir.File("my file.jpg"); // a path with a space stays one argument
        var runner = new FakeProgramRunner { OnRun = FakeProgramRunner.WriteDashO };
        await using var queue = Queue(runner);

        var job = await Run(queue, [a, b], ProgramPreset(arguments: "{inputs} -o {output}", allFiles: true));

        Assert.Equal(JobState.Completed, job.State);
        var run = Assert.Single(runner.Runs);
        Assert.Equal([a, b, "-o"], run.Arguments.Take(3));
        Assert.All(job.Files, f => Assert.Equal(FileState.Done, f.State));
    }

    [Fact]
    public async Task A_single_file_runs_once_even_in_all_files_mode()
    {
        using var dir = new TempDir();
        var runner = new FakeProgramRunner { OnRun = FakeProgramRunner.WriteDashO };
        await using var queue = Queue(runner);

        var job = await Run(queue, [dir.File("a.jpg")], ProgramPreset(allFiles: true));

        Assert.Equal(JobState.Completed, job.State);
        Assert.Single(runner.Runs);
    }

    // ───────────────────────── Failure ─────────────────────────

    [Fact]
    public async Task A_non_zero_exit_code_fails_the_files()
    {
        using var dir = new TempDir();
        var runner = new FakeProgramRunner { ExitCode = 3, OnRun = FakeProgramRunner.WriteDashO };
        await using var queue = Queue(runner);

        var job = await Run(queue, [dir.File("a.jpg")], ProgramPreset());

        var file = Assert.Single(job.Files);
        Assert.Equal(FileState.Failed, file.State);
        Assert.Equal("error.program_exit_code", file.ErrorKey);
        Assert.Contains("exit code 3", file.ErrorDetail);
    }

    [Fact]
    public async Task A_program_that_writes_nothing_fails_the_files()
    {
        using var dir = new TempDir();
        var runner = new FakeProgramRunner { OnRun = null }; // runs fine, makes no file
        await using var queue = Queue(runner);

        var job = await Run(queue, [dir.File("a.jpg")], ProgramPreset());

        var file = Assert.Single(job.Files);
        Assert.Equal(FileState.Failed, file.State);
        Assert.Equal("error.program_no_output", file.ErrorKey);
    }

    [Fact]
    public async Task A_too_slow_program_fails_the_files_with_the_timeout_error()
    {
        using var dir = new TempDir();
        var runner = new FakeProgramRunner { OnRun = (_, _) => throw new TimeoutException("killed after 30 s") };
        await using var queue = Queue(runner);

        var job = await Run(queue, [dir.File("a.jpg")], ProgramPreset());

        var file = Assert.Single(job.Files);
        Assert.Equal(FileState.Failed, file.State);
        Assert.Equal("error.program_timeout", file.ErrorKey);
    }

    [Fact]
    public async Task A_program_that_cannot_start_at_all_fails_the_files()
    {
        using var dir = new TempDir();
        var runner = new FakeProgramRunner { OnRun = (_, _) => throw new System.ComponentModel.Win32Exception(2) };
        await using var queue = Queue(runner);

        var job = await Run(queue, [dir.File("a.jpg")], ProgramPreset());

        var file = Assert.Single(job.Files);
        Assert.Equal(FileState.Failed, file.State);
        Assert.Equal("error.program_not_found", file.ErrorKey);
    }

    [Fact]
    public async Task Without_a_program_every_file_fails()
    {
        using var dir = new TempDir();
        await using var queue = new JobQueue(new ConverterCatalog([]), programRunner: new FakeProgramRunner());
        var preset = ProgramPreset();
        preset.Program.Executable = "";

        var job = await Run(queue, [dir.File("a.jpg")], preset);

        var file = Assert.Single(job.Files);
        Assert.Equal(FileState.Failed, file.State);
        Assert.Equal("error.program_not_set", file.ErrorKey);
    }

    // ───────────────────────── The preset on disk ─────────────────────────

    [Fact]
    public void A_run_program_preset_survives_a_round_trip_through_presets_json()
    {
        var preset = new Preset
        {
            Id = "run-x",
            Name = "My tool",
            Kind = PresetKind.RunProgram,
            TargetFormat = "txt",
            Program = new ProgramOptions
            {
                Executable = "C:\\Tools\\convert.exe",
                Arguments = "{input} -o {output}",
                AllFiles = true,
                TimeoutSeconds = 45,
            },
        };

        var json = System.Text.Json.JsonSerializer.Serialize(new List<Preset> { preset }, FileeJsonContext.Default.ListPreset);
        Assert.Contains("\"kind\": \"RunProgram\"", json);
        Assert.Contains("\"executable\": \"C:\\\\Tools\\\\convert.exe\"", json);
        var read = System.Text.Json.JsonSerializer.Deserialize(json, FileeJsonContext.Default.ListPreset)!.Single();

        Assert.Equal(PresetKind.RunProgram, read.Kind);
        Assert.True(read.IsRunProgram);
        Assert.Equal("txt", read.TargetFormat);
        Assert.Equal("C:\\Tools\\convert.exe", read.Program.Executable);
        Assert.Equal("{input} -o {output}", read.Program.Arguments);
        Assert.True(read.Program.AllFiles);
        Assert.Equal(45, read.Program.TimeoutSeconds);
    }

    [Fact]
    public void An_unknown_preset_kind_is_read_as_a_group_and_never_runs_as_a_conversion()
    {
        var json = """[{"id":"future","kind":"Teleport","targetFormat":"png"}]""";
        var read = System.Text.Json.JsonSerializer.Deserialize(json, FileeJsonContext.Default.ListPreset)!.Single();

        Assert.Equal(PresetKind.Group, read.Kind);
        Assert.True(read.IsGroup);
    }
}
