using Filee.Core.Platform;
using Filee.Core.Presets;

namespace Filee.Core.Conversion;

public sealed partial class JobQueue
{
    /// <summary>How long the program may take when the preset leaves the timeout at zero.</summary>
    private static readonly TimeSpan DefaultProgramTimeout = TimeSpan.FromMinutes(10);

    /// <summary>How much of a program's standard error is kept as the failure detail.</summary>
    private const int MaxErrorDetail = 500;

    /// <summary>
    /// Runs the user's own program for a "Run a program" preset: once with all files (<c>{inputs}</c>), or once per
    /// file (<c>{input}</c>). The output rules pick the path handed over as <c>{output}</c>; files the program writes
    /// on its own next to it are picked up as well. Nothing runs through the route planner: the program is the step.
    /// </summary>
    private async Task RunProgramAsync(ConversionJob job)
    {
        var files = job.Files.Where(f => !Stopped(f)).ToList();
        if (files.Count == 0)
            return;

        if (string.IsNullOrWhiteSpace(job.Preset.Program.Executable))
        {
            foreach (var f in files)
            {
                f.State = FileState.Failed;
                f.ErrorKey = "error.program_not_set";
            }
            Notify(job);
            return;
        }
        if (_programRunner is null)
        {
            foreach (var f in files)
            {
                f.State = FileState.Failed;
                f.ErrorKey = "error.conversion_failed";
                f.ErrorDetail = "No program runner is registered.";
            }
            Notify(job);
            return;
        }

        if (job.Preset.Program.AllFiles && files.Count > 1)
            await RunProgramInvocationAsync(job, OrderForInputs(files));
        else
            for (var i = 0; i < files.Count; i++)
                await RunProgramInvocationAsync(job, [files[i]], i + 1);
    }

    /// <summary>
    /// All-files mode passes the paths in the order they would merge: by name, the way people count, folders
    /// together — what the Explorer shows, and the same as "filee convert --sort name" (issue #966).
    /// </summary>
    private static List<FileResult> OrderForInputs(IReadOnlyList<FileResult> files)
    {
        var byPath = files.GroupBy(f => f.SourcePath, FileSystemPaths.Comparer)
            .ToDictionary(g => g.Key, g => g.First(), FileSystemPaths.Comparer);
        var ordered = FileSorter.Sort(files.Select(f => FileStamp.Read(f.SourcePath)).ToList(), stamp => stamp,
            FileSortOrder.NameAscending, foldersTogether: true);
        return ordered.Select(stamp => byPath.TryGetValue(stamp.FilePath, out var file) ? file : null)
            .OfType<FileResult>().ToList();
    }

    /// <summary>One start of the program: for a single file, or for all of them at once.</summary>
    private async Task RunProgramInvocationAsync(ConversionJob job, IReadOnlyList<FileResult> files, int index = 1)
    {
        var preset = job.Preset;
        var first = files[0];
        var allocator = CreateFinalAllocator(job, first.SourcePath, index, oneFile: true);
        var output = allocator.Allocate(OutputExtensionFor(preset, first.SourcePath));
        if (output is null)
        {
            foreach (var f in files)
                f.State = FileState.Skipped;
            return;
        }

        foreach (var f in files)
            f.State = FileState.Running;
        Notify(job);

        // What the output folder holds before the run, so files the program writes on its own can be told apart
        // from what was there. Cheap: names only.
        var outputDir = Path.GetDirectoryName(Path.GetFullPath(output))!;
        var before = Directory.Exists(outputDir)
            ? Directory.EnumerateFiles(outputDir).ToHashSet(Platform.FileSystemPaths.Comparer)
            : new HashSet<string>(Platform.FileSystemPaths.Comparer);

        try
        {
            var arguments = ProgramTool.BuildArguments(preset.Program.Arguments,
                files.Select(f => f.SourcePath).ToList(), output);
            var timeout = preset.Program.TimeoutSeconds > 0
                ? TimeSpan.FromSeconds(preset.Program.TimeoutSeconds)
                : DefaultProgramTimeout;
            var result = await _programRunner!.RunAsync(preset.Program.Executable, arguments, timeout, job.CancellationToken);

            if (job.CancellationToken.IsCancellationRequested)
            {
                allocator.Discard();
                foreach (var f in files)
                    f.State = FileState.Cancelled;
                return;
            }
            if (result.ExitCode != 0)
            {
                allocator.Discard();
                foreach (var f in files)
                {
                    f.State = FileState.Failed;
                    f.ErrorKey = "error.program_exit_code";
                    f.ErrorDetail = Detail(result.ExitCode, result.StandardError);
                }
                return;
            }

            var outputs = CollectOutputs(output, before);
            if (outputs.Count == 0)
            {
                allocator.Discard();
                foreach (var f in files)
                {
                    f.State = FileState.Failed;
                    f.ErrorKey = "error.program_no_output";
                }
                return;
            }
            // A path handed out but not written (the program made its own files instead) is free again.
            if (!outputs.Contains(output))
                allocator.Release(outputs);

            first.Outputs.AddRange(outputs);
            foreach (var f in files)
            {
                f.State = FileState.Done;
                f.Progress = 1;
            }
        }
        catch (TimeoutException ex)
        {
            Fail(job, files, allocator, "error.program_timeout", ex.Message);
        }
        catch (OperationCanceledException)
        {
            allocator.Discard();
            foreach (var f in files)
                f.State = FileState.Cancelled;
        }
        catch (Exception ex)
        {
            // A program that could not be started at all (not there, a DLL missing) reads differently from one that
            // ran and failed; ProcessRunner reports the first as "could not start" or a refusal.
            var key = ex is InvalidOperationException or ConversionRefusedException or FileNotFoundException
                or System.ComponentModel.Win32Exception ? "error.program_not_found" : "error.conversion_failed";
            Fail(job, files, allocator, key, ex.Message);
        }
        Notify(job);
    }

    private void Fail(ConversionJob job, IReadOnlyList<FileResult> files, FinalAllocator allocator, string key, string detail)
    {
        allocator.Discard();
        foreach (var f in files)
        {
            if (f.State is FileState.Done or FileState.Skipped)
                continue;
            f.State = FileState.Failed;
            f.ErrorKey = key;
            f.ErrorDetail = detail;
        }
    }

    /// <summary>The file at <c>{output}</c> when the program wrote it, else what appeared in <c>{outputDir}</c>.</summary>
    private static List<string> CollectOutputs(string output, HashSet<string> before)
    {
        if (File.Exists(output))
            return [output];
        if (!Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(output))!))
            return [];
        var directory = Path.GetDirectoryName(Path.GetFullPath(output))!;
        return Directory.EnumerateFiles(directory)
            .Where(p => !before.Contains(p))
            .OrderBy(p => p, NaturalComparer.Instance)
            .ToList();
    }

    private static string OutputExtensionFor(Preset preset, string sourcePath) =>
        preset.TargetFormat == BuiltInData.SameAsSource
            ? Path.GetExtension(sourcePath).TrimStart('.')
            : preset.TargetFormat;

    private static string Detail(int exitCode, string standardError)
    {
        var message = $"exit code {exitCode}";
        var error = standardError?.Trim();
        if (string.IsNullOrEmpty(error))
            return message;
        error = error.Length > MaxErrorDetail ? error[^MaxErrorDetail..] : error;
        return $"{message}: {error}";
    }
}
