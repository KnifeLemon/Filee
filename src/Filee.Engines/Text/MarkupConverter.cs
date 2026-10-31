// The lightweight markup languages Filee reads and writes itself (Hwp/Hwpx/Markup), without Pandoc: anything a
// built-in reader reads (DocumentReaders) → each of them, and each of them → Markdown, HTML and plain text. Their
// readers are in DocumentReaders, so the HWPX, DOCX and ODT writers and the Filee renderer take them too (→ HWPX, DOCX,
// ODT, PDF). The edges cost 8 like the DOCX writer's, below Pandoc's (10, and 11 into a markup language), so the
// built-in route wins; Pandoc keeps what only it does (LaTeX, Typst, AsciiDoc, man pages, slides, RTF and EPUB out).
// Pictures of a text result go into a "<name>_files" folder next to it (MarkdownPictures), a notebook embeds its own.

using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Engines.Ebooks;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Markup;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Text;

/// <summary>What a markup writer gets besides the document.</summary>
/// <param name="Pictures">Maps a picture file to the link written into the result, or null to leave it out.</param>
/// <param name="WorkFolder">Scratch folder of the job (pictures converted to a common format).</param>
/// <param name="LeaveOutPictures">The preset leaves pictures out.</param>
internal sealed record MarkupWriteContext(Func<string, string?> Pictures, string WorkFolder, bool LeaveOutPictures);

/// <summary>Reads and writes the markup languages Filee owns.</summary>
public sealed class MarkupConverter : IConverter
{
    /// <summary>Below Pandoc's direct edges (10 out of a markup language, 11 into one), like the DOCX writer's.</summary>
    private const int Cost = 8;

    /// <summary>The writers: format id → the text of the file.</summary>
    internal static readonly IReadOnlyDictionary<string, Func<HDocument, MarkupWriteContext, string>> Writers =
        new Dictionary<string, Func<HDocument, MarkupWriteContext, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["ipynb"] = (document, context) => NotebookWriter.Write(document, context.WorkFolder, !context.LeaveOutPictures),
        };

    /// <summary>Formats each markup language is also written as (the readers' other outputs are the writers' edges).</summary>
    private static readonly string[] TextTargets = ["md", "html", "txt"];

    public string Id => "markup";
    public string DisplayName => "Markup languages (built-in)";
    public int MaxParallelism => 0;

    /// <summary>
    /// Into a markup language only as the whole conversion (DirectOnly): a notebook or a wiki page in the middle of
    /// another route (a picture → PDF → notebook → Pandoc's RTF) would lose what the language can't hold.
    /// </summary>
    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        .. from to in Writers.Keys
           from source in DocumentReaders.BuiltInFormats
           where source != to
           select new ConversionEdge(source, to, source == "pdf" ? Cost + DocumentReaders.PdfPenalty : Cost, DirectOnly: true),
        .. from source in Writers.Keys
           from to in TextTargets
           select new ConversionEdge(source, to, Cost),
    ];

    public EngineStatus GetStatus() =>
        EngineStatus.Available(string.Join(", ", Writers.Keys.Select(f => FormatRegistry.Get(f).DisplayName)), EngineVersions.BuiltIn);

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(0.1);
        var document = await DocumentReaders.ReadAsync(step.InputPath, step.From, step.WorkDirectory, cancellationToken,
            ocrLayer: step.Preset.Pdf.Ocr);
        progress?.Report(0.6);
        cancellationToken.ThrowIfCancellationRequested();

        var output = step.Output.Allocate(FormatRegistry.Get(step.To).PrimaryExtension);
        if (output is null)
            return [];
        await Task.Run(() => Write(document, step, output), cancellationToken);
        progress?.Report(1);
        return [output];
    }

    private static void Write(HDocument document, ConversionStep step, string output)
    {
        var leaveOut = step.Preset.Document.LeaveOutPictures;
        Func<string, string?> pictures = leaveOut ? _ => null : new MarkdownPictures(output, step.Output, step.WorkDirectory).Add;
        switch (step.To)
        {
            case "md":
                File.WriteAllText(output, MarkdownWriter.Write(document, pictures), new UTF8Encoding(false));
                break;
            case "html":
                {
                    // One self-contained page: pictures are embedded as data: URIs.
                    var book = new Book(document, new BookMetadata { Title = document.Title });
                    var images = EbookFiles.NewFolder(step.WorkDirectory, "html-images");
                    var page = XhtmlWriter.Write(document, new XhtmlOptions { ImageSource = path => DataUri(path, images) }).Single();
                    File.WriteAllText(output, XhtmlWriter.Page(book.DisplayTitle(step.InputPath), page.Body, book.DisplayLanguage(), null, epub: false),
                        new UTF8Encoding(false));
                    break;
                }
            case "txt":
                File.WriteAllText(output, PlainTextWriter.Write(document), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                break;
            default:
                File.WriteAllText(output, Writers[step.To](document, new MarkupWriteContext(pictures, step.WorkDirectory, leaveOut)),
                    new UTF8Encoding(false));
                break;
        }
    }

    private static string? DataUri(string path, string convertFolder) =>
        EbookFiles.CommonImage(path, convertFolder, gifAndSvg: true) is ({ } file, { } mediaType)
            ? $"data:{mediaType};base64,{Convert.ToBase64String(File.ReadAllBytes(file))}"
            : null;
}
