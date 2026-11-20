// The lightweight markup languages Filee reads and writes itself (Hwp/Hwpx/Markup), without Pandoc: anything a
// built-in reader reads (DocumentReaders) → each of them, and each of them → Markdown, HTML and plain text. Their
// readers are in DocumentReaders, so the HWPX, DOCX and ODT writers and the Filee renderer take them too (→ HWPX, DOCX,
// ODT, PDF). The edges cost 8 like the DOCX writer's, below Pandoc's (10, and 11 into a markup language), so the
// built-in route wins. LaTeX, AsciiDoc, Typst and man pages are written here too (WriteOnly), from every built-in
// reader's format; reading them stays Pandoc's, as do slides, and RTF and EPUB straight from a markup language.
// Pictures of a text result go into a "<name>_files" folder next to it (MarkdownPictures), a notebook embeds its own.
// Mind maps (MindMapReader), read only, are written as Markdown, HTML and text here too; into OPML every topic is a
// heading, so the outline keeps its whole tree.

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
/// <param name="Cancellation">Stops the writer between blocks.</param>
internal sealed record MarkupWriteContext(Func<string, string?> Pictures, string WorkFolder, bool LeaveOutPictures, CancellationToken Cancellation = default);

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
            ["opml"] = (document, context) => OpmlWriter.Write(document, context.Pictures),
            ["textile"] = (document, context) => TextileWriter.Write(document, context.Pictures),
            ["mediawiki"] = (document, context) => MediaWikiWriter.Write(document, context.Pictures),
            ["rst"] = (document, context) => RstWriter.Write(document, context.Pictures),
            ["org"] = (document, context) => OrgWriter.Write(document, context.Pictures),
            ["dbk"] = (document, context) => DocBookWriter.Write(document, context.Pictures),
        };

    /// <summary>
    /// Languages written but not read here (macro and scripting languages, which Pandoc reads): format id → the text of
    /// the file. Their edges come from every built-in reader's format, like the writers'.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, Func<HDocument, MarkupWriteContext, string>> WriteOnly =
        new Dictionary<string, Func<HDocument, MarkupWriteContext, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["tex"] = (document, context) => new LatexWriter(context.Pictures) { Cancellation = context.Cancellation }.Write(document),
            ["adoc"] = (document, context) => new AsciiDocWriter(context.Pictures) { Cancellation = context.Cancellation }.Write(document),
            ["typ"] = (document, context) => new TypstWriter(context.Pictures) { Cancellation = context.Cancellation }.Write(document),
            ["man"] = (document, context) => new ManWriter { Cancellation = context.Cancellation }.Write(document),
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
        .. from to in Writers.Keys.Concat(WriteOnly.Keys)
           from source in DocumentReaders.BuiltInFormats
           where source != to
           select new ConversionEdge(source, to, source == "pdf" ? Cost + DocumentReaders.PdfPenalty : Cost, DirectOnly: true),
        .. from source in Writers.Keys.Concat(MindMapReader.Formats)
           from to in TextTargets
           select new ConversionEdge(source, to, Cost),
    ];

    public EngineStatus GetStatus() =>
        EngineStatus.Available(string.Join(", ", Writers.Keys.Concat(WriteOnly.Keys).Select(f => FormatRegistry.Get(f).DisplayName)), EngineVersions.BuiltIn);

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(0.1);
        var document = step.To == "opml" && MindMapReader.Formats.Contains(step.From, StringComparer.OrdinalIgnoreCase)
            ? await Task.Run(() => MindMapReader.Read(step.InputPath, step.From, Path.Combine(step.WorkDirectory, $"media-{Guid.NewGuid():N}"),
                headingsOnly: true), cancellationToken)
            : await DocumentReaders.ReadAsync(step.InputPath, step.From, step.WorkDirectory, cancellationToken, ocrLayer: step.Preset.Pdf.Ocr);
        progress?.Report(0.6);
        cancellationToken.ThrowIfCancellationRequested();

        var output = step.Output.Allocate(FormatRegistry.Get(step.To).PrimaryExtension);
        if (output is null)
            return [];
        await Task.Run(() => Write(document, step, output, cancellationToken), cancellationToken);
        progress?.Report(1);
        return [output];
    }

    private static void Write(HDocument document, ConversionStep step, string output, CancellationToken cancellationToken)
    {
        var leaveOut = step.Preset.Document.LeaveOutPictures;
        Func<string, string?> pictures = leaveOut ? _ => null : new MarkdownPictures(output, step.Output, step.WorkDirectory).Add;
        if (step.To is "md" || Writers.ContainsKey(step.To) || WriteOnly.ContainsKey(step.To))
            MarkupBlocks.OpenCodeBoxes(document); // Markdown's code boxes are code blocks in markup
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
                {
                    var context = new MarkupWriteContext(pictures, step.WorkDirectory, leaveOut, cancellationToken);
                    var text = WriteOnly.TryGetValue(step.To, out var writeOnly) ? writeOnly(document, context) : Writers[step.To](document, context);
                    // Through a temporary file: a failed write leaves nothing under the output's name.
                    var bytes = new UTF8Encoding(false).GetBytes(text);
                    WriterTempFile.Write(output, stream => stream.Write(bytes));
                    break;
                }
        }
    }

    private static string? DataUri(string path, string convertFolder) =>
        EbookFiles.CommonImage(path, convertFolder, gifAndSvg: true) is ({ } file, { } mediaType)
            ? $"data:{mediaType};base64,{Convert.ToBase64String(File.ReadAllBytes(file))}"
            : null;
}
