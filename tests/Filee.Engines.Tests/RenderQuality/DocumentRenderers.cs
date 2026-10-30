// The renderers the render quality harness measures. rhwp is the reference while it is still bundled: it renders a
// document the way Filee's users get it today (RhwpInput's fixes, then rhwp export-pdf with Filee's fallback fonts).
// It is used here only as a yardstick; no conversion path is added through it.
//
// Filee's own layout engine plugs in at one place, Candidate below: a renderer takes a document (HWPX or HWP, with
// its layout model read on demand) and returns a PDF. Each layout milestone registers what it can render there, and
// the differential compare and the read-back checks pick it up without other changes.

using Filee.Engines.Hwp;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Infrastructure;
using Filee.Engines.Typesetting;

namespace Filee.Engines.Tests.RenderQuality;

/// <summary>A document to render: the file, its format and (read when first asked for) its layout model.</summary>
internal sealed class RenderInput(string path, string workDirectory)
{
    private HDocument? _document;

    public string Path { get; } = path;

    /// <summary>"hwpx" or "hwp".</summary>
    public string Format { get; } = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    /// <summary>A folder of this input's own for temporary files.</summary>
    public string WorkDirectory { get; } = workDirectory;

    /// <summary>The document read for layout (HWPX only, until there is an HWP reader).</summary>
    public HDocument Document => _document ??= Format == "hwpx"
        ? HwpxReader.ReadForLayout(Path, System.IO.Path.Combine(WorkDirectory, "media"))
        : throw new NotSupportedException("Only HWPX documents are read for layout.");
}

/// <summary>Renders a document to PDF bytes.</summary>
internal delegate Task<byte[]> RenderDocument(RenderInput input, CancellationToken cancellationToken);

/// <param name="Formats">Input formats the renderer takes ("hwpx", "hwp").</param>
internal sealed record DocumentRenderer(string Name, IReadOnlySet<string> Formats, RenderDocument Render);

internal static class DocumentRenderers
{
    /// <summary>
    /// Filee's layout engine, measured against <see cref="Reference"/>: the page composer and paragraph layout
    /// (Typesetting/FileeRenderer.cs, PageComposer.cs). Null would skip the compare.
    /// </summary>
    public static DocumentRenderer? Candidate =>
        new("filee", Hwpx, (input, ct) => Task.FromResult(FileeRenderer.ToPdf(input.Document, ct)));

    /// <summary>Why the differential compare doesn't run while <see cref="Candidate"/> is null.</summary>
    public const string NoCandidate =
        "Filee's layout engine has no renderer registered in DocumentRenderers.Candidate";

    public static readonly IReadOnlySet<string> Hwpx = new HashSet<string>(["hwpx"]);
    public static readonly IReadOnlySet<string> HwpxAndHwp = new HashSet<string>(["hwpx", "hwp"]);

    /// <summary>rhwp as Filee runs it, or null when rhwp isn't there (fetch it with build/fetch-engines.ps1 -Only rhwp).</summary>
    public static DocumentRenderer? Reference => RhwpExecutable() is { } exe ? new DocumentRenderer("rhwp", HwpxAndHwp, (input, ct) => RhwpPdfAsync(exe, input, ct)) : null;

    /// <summary>The bundled rhwp executable, or null.</summary>
    public static string? RhwpExecutable() =>
        EngineEnvironment.FindBundled("rhwp") is { } folder && Path.Combine(folder, OperatingSystem.IsWindows() ? "rhwp.exe" : "rhwp") is var exe && File.Exists(exe)
            ? exe
            : null;

    private static async Task<byte[]> RhwpPdfAsync(string exe, RenderInput input, CancellationToken cancellationToken)
    {
        // The same input Filee gives rhwp: an HWPX with RhwpInput's fixes when it needs any (an HWP as it is).
        var source = input.Format == "hwpx" ? RhwpInput.Prepare(input.Path, input.WorkDirectory) ?? input.Path : input.Path;
        var pdf = Path.Combine(input.WorkDirectory, $"rhwp-{Guid.NewGuid():N}.pdf");
        var result = await ProcessRunner.RunAsync(exe, ["export-pdf", source, "-o", pdf, .. RhwpInput.PdfOptions()],
            RhwpConverter.TimeLimit(RhwpConverter.TextSize(source)), cancellationToken);
        if (!File.Exists(pdf) || new FileInfo(pdf).Length == 0)
            throw new InvalidOperationException($"rhwp failed (exit {result.ExitCode}): {result.StandardError.Trim()}");
        return await File.ReadAllBytesAsync(pdf, cancellationToken);
    }

    /// <summary>A binary HWP made from <paramref name="hwpx"/> with <c>rhwp convert</c> (inputs for the HWP reader).</summary>
    public static async Task<string> ConvertToHwpAsync(string hwpx, CancellationToken cancellationToken)
    {
        var exe = RhwpExecutable() ?? throw new InvalidOperationException("rhwp not found");
        var hwp = Path.ChangeExtension(hwpx, ".hwp");
        var result = await ProcessRunner.RunAsync(exe, ["convert", hwpx, hwp], TimeSpan.FromMinutes(2), cancellationToken);
        if (!File.Exists(hwp) || new FileInfo(hwp).Length == 0)
            throw new InvalidOperationException($"rhwp convert failed (exit {result.ExitCode}): {result.StandardError.Trim()}");
        return hwp;
    }
}
