// HWP / HWPX → PDF, HWP → HWPX and HWPX → HWP with the rhwp command line tool (MIT, https://github.com/edwardkim/rhwp).
// Bundled under engines/rhwp. Works without Hancom Office, so there is no automation approval dialog.

using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Hwp;

/// <summary>Renders HWP and HWPX documents to PDF and converts between the two 한글 formats.</summary>
public sealed class RhwpConverter(EngineEnvironment env) : IConverter
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);
    private string? _exe;

    public string Id => "rhwp";
    public string DisplayName => "rhwp";
    public int MaxParallelism => 2;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        new("hwp", "pdf"),
        new("hwpx", "pdf"),
        // Lossless within the 한글 document model; anything → HWP goes through the HWPX writer first.
        new("hwp", "hwpx"),
        new("hwpx", "hwp"),
    ];

    public EngineStatus GetStatus()
    {
        _exe = Locate();
        return _exe is null ? EngineStatus.Unavailable("engine.reason.not_installed") : EngineStatus.Available(_exe);
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var exe = _exe ?? Locate() ?? throw new InvalidOperationException("rhwp was not found.");
        var temp = Path.Combine(step.WorkDirectory, $"rhwp-{Guid.NewGuid():N}.{step.To}");
        string[] arguments = step.To switch
        {
            "pdf" => ["export-pdf", step.InputPath, "-o", temp],
            "hwpx" => ["export-hwpx", step.InputPath, temp],
            "hwp" => ["convert", step.InputPath, temp],
            _ => throw new NotSupportedException(step.To),
        };

        progress?.Report(0.1);
        ProcessResult result;
        using (ProgressEstimate.Start(progress, TimeSpan.FromSeconds(1.5)))
            result = await ProcessRunner.RunAsync(exe, arguments, Timeout, cancellationToken);
        if (!File.Exists(temp) || new FileInfo(temp).Length == 0)
            throw new InvalidOperationException($"rhwp failed (exit {result.ExitCode}). {result.StandardError.Trim()}");

        var target = step.Output.Allocate(step.To);
        if (target is null)
            return [];
        File.Move(temp, target, overwrite: true);
        progress?.Report(1);
        return [target];
    }

    private string? Locate()
    {
        var exe = OperatingSystem.IsWindows() ? "rhwp.exe" : "rhwp";
        var bundled = EngineEnvironment.FindBundled("rhwp");
        return EngineEnvironment.FirstExisting(
            env.CustomPath(Id),
            bundled is null ? null : Path.Combine(bundled, exe),
            EngineEnvironment.FindOnPath("rhwp"));
    }
}
