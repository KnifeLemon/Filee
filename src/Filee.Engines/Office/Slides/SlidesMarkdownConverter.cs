// PowerPoint presentations → Markdown, one section per slide (SlidesMarkdown), which reveal.js shows as a deck and any
// Markdown editor reads as a document. Without it, PPTX → Markdown went through HWPX and lost the slides, titles and
// lists (issue #1702).

using System.Text;
using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;
using Filee.Engines.Text;

namespace Filee.Engines.Office.Slides;

/// <summary>Writes the slides of a PowerPoint file as Markdown; needs no external program.</summary>
public sealed class SlidesMarkdownConverter : IConverter
{
    /// <summary>PowerPoint's formats: the presentation, macro-enabled ones, templates and slide shows.</summary>
    private static readonly string[] Sources = ["pptx", "pptm", "potx", "potm", "ppsx"];

    public string Id => "slides-md";
    public string DisplayName => "Slides to Markdown (built-in)";
    public int MaxParallelism => 0;

    public IReadOnlyList<ConversionEdge> Edges { get; } = [.. Sources.Select(source => new ConversionEdge(source, "md"))];

    public EngineStatus GetStatus() => EngineStatus.Available(version: EngineVersions.BuiltIn);

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            var output = step.Output.Allocate("md");
            if (output is null)
                return [];
            Func<string, string?> pictures = step.Preset.Document.LeaveOutPictures
                ? _ => null
                : new MarkdownPictures(output, step.Output, step.WorkDirectory).Add;
            var markdown = SlidesMarkdown.Write(step.InputPath, pictures, Path.Combine(step.WorkDirectory, "slides-media"), cancellationToken);
            File.WriteAllText(output, markdown, new UTF8Encoding(false));
            progress?.Report(1);
            return [output];
        }, cancellationToken);
}
