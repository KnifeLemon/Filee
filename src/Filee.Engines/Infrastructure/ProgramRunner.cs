// Runs the user's own program for a "Run a program" preset (#966), through ProcessRunner: started directly
// (no shell, one ArgumentList entry per argument), the whole process tree killed on timeout or cancellation.

using Filee.Core.Conversion;

namespace Filee.Engines.Infrastructure;

/// <summary>Starts a program the user picked, waits for it and reports how it ended.</summary>
public sealed class ProgramRunner : IProgramRunner
{
    public async Task<ProgramRunResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(executable, arguments, timeout, cancellationToken);
        return new ProgramRunResult(result.ExitCode, result.StandardError);
    }
}
