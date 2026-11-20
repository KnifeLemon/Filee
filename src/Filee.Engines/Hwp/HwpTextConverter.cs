// HWP / HWPX → plain text, Markdown or HTML without a native library: the built-in readers (HwpReader for HWP 5.0,
// HwpxReader for HWPX) read the document model, and PlainTextWriter, MarkdownWriter and XhtmlWriter write it out.
//
// Markdown puts the pictures into a "<name>_files" folder next to it (MarkdownPictures); the HTML page embeds them as
// data: URIs, so it stays one file. "Leave out pictures" (DocumentOptions.LeaveOutPictures) leaves all of them out of
// the Markdown.
//
// RTF → TXT is here too (#1204): RtfReader's document written a line per paragraph, as a word processor saves plain
// text. Without it the cheapest built-in route went through a PDF (the Filee renderer, then the PDF's text), which
// wraps long lines and loses the indentation and the empty lines, or through the HWPX writer. Word documents (DOCX and
// its macro-enabled and template variants, Word 97–2003 DOC, DOT and WPS) and ODT go the same way. These take the
// conversion from LibreOffice too, wherever it stands in the engine list.

using System.Text;
using Filee.Core.Conversion;
using Filee.Engines.Ebooks;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Infrastructure;
using Filee.Engines.Text;

namespace Filee.Engines.Hwp;

/// <summary>Writes the text of HWP and HWPX documents as TXT, Markdown or HTML, and of Word, ODT and RTF as TXT or Markdown.</summary>
public sealed class HwpTextConverter : IConverter, ICapabilityCheck
{
    /// <summary>The engine's id; settings that named Unhwp ("unhwp", the engine this replaced) are moved to it.</summary>
    public const string EngineId = "hwp-text";

    public string Id => EngineId;
    public string DisplayName => "HWP text (built-in)";
    public int MaxParallelism => 0;

    // The costs Unhwp's edges had, so routes through HWP text, Markdown and HTML stay as they were.
    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        new("hwp", "txt"), new("hwpx", "txt"),
        new("hwp", "md"), new("hwpx", "md"),
        new("hwp", "html", 14), new("hwpx", "html", 14),
        // RTF, Word and ODT → TXT, direct only: a TXT step in another route would lose the formatting. LibreOffice's
        // edge for them is its fallback (25), and this one wins wherever either engine stands in the engine list
        // (15 + 9 at most against 25 + 0 at least): the text is the document's own, line for line, as LibreOffice
        // writes it. It is cheaper than the renderer's PDF and its text (1 + 9 + 10 + 9 in the default order) and the
        // routes through Pandoc's Markdown (29) or the HWPX writer (32). The DOC reader checks each file (a Word 6.0 /
        // 95 file goes another way).
        .. LineFormats.Select(f => new ConversionEdge(f, "txt", LineCost, DirectOnly: true, Checked: DocumentReaders.Checked(f))),
        // RTF, Word and ODT → Markdown in one step, at the same cost: through the HWPX writer (the route before) the
        // equations came back as 한글 equation script, while the document model keeps them as math for $…$.
        .. LineFormats.Select(f => new ConversionEdge(f, "md", LineCost, DirectOnly: true, Checked: DocumentReaders.Checked(f))),
    ];

    /// <summary>
    /// Word processor documents, whose TXT is a line per paragraph (<see cref="PlainTextWriter.WriteLines"/>) as the
    /// word processor itself saves plain text.
    /// </summary>
    internal static readonly string[] LineFormats = ["rtf", "docx", "docm", "dotx", "dotm", "doc", "dot", "wps", "odt"];

    /// <summary>The cost of RTF, Word and ODT → TXT: below LibreOffice's fallback (25) with any engine order's penalty (0 to 9).</summary>
    internal const int LineCost = 15;

    public EngineStatus GetStatus() => EngineStatus.Available("HWP, HWPX → TXT, Markdown, HTML; Word, ODT, RTF → TXT, Markdown", EngineVersions.BuiltIn);

    /// <summary>A Word 6.0 / 95 or Works file with a .doc or .wps name, which the DOC reader doesn't read.</summary>
    public string? FirstUnsupported(string sourcePath, string sourceFormat) => DocumentReaders.Unsupported(sourcePath, sourceFormat);

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(0.1);
        var document = await DocumentReaders.ReadAsync(step.InputPath, step.From, step.WorkDirectory, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(0.6);

        var path = step.Output.Allocate(step.To);
        if (path is null)
            return [];
        var content = step.To switch
        {
            "txt" when LineFormats.Contains(step.From) => PlainTextWriter.WriteLines(document),
            "txt" => PlainTextWriter.Write(document),
            "md" => Markdown(document, step, path),
            "html" => Html(document, step),
            _ => throw new NotSupportedException(step.To),
        };
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: step.To == "txt"), cancellationToken);
        progress?.Report(1);
        return [path];
    }

    /// <summary>Markdown without a title block (the text starts with the document), pictures beside it.</summary>
    internal static string Markdown(HDocument document, ConversionStep step, string output)
    {
        Func<string, string?> pictures = step.Preset.Document.LeaveOutPictures
            ? _ => null
            : new MarkdownPictures(output, step.Output, step.WorkDirectory).Add;
        document.Title = null;
        return MarkdownWriter.Write(document, pictures);
    }

    /// <summary>One self-contained page: the pictures are embedded.</summary>
    internal static string Html(HDocument document, ConversionStep step)
    {
        var book = new Book(document, new BookMetadata { Title = document.Title });
        var images = EbookFiles.NewFolder(step.WorkDirectory, "hwp-html-images");
        var page = XhtmlWriter.Write(document, new XhtmlOptions { ImageSource = file => DataUri(file, images) }).Single();
        return XhtmlWriter.Page(book.DisplayTitle(step.InputPath), page.Body, book.DisplayLanguage(), null, epub: false);
    }

    private static string? DataUri(string file, string convertFolder) =>
        EbookFiles.CommonImage(file, convertFolder, gifAndSvg: true) is ({ } common, { } mediaType)
            ? $"data:{mediaType};base64,{Convert.ToBase64String(File.ReadAllBytes(common))}"
            : null;
}
