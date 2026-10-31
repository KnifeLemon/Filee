// HWP / HWPX → plain text, Markdown or HTML without a native library: the built-in readers (HwpReader for HWP 5.0,
// HwpxReader for HWPX) read the document model, and PlainTextWriter, MarkdownWriter and XhtmlWriter write it out.
//
// Markdown puts the pictures into a "<name>_files" folder next to it (MarkdownPictures); the HTML page embeds them as
// data: URIs, so it stays one file. "Leave out pictures" (DocumentOptions.LeaveOutPictures) leaves all of them out of
// the Markdown.

using System.Text;
using Filee.Core.Conversion;
using Filee.Engines.Ebooks;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Infrastructure;
using Filee.Engines.Text;

namespace Filee.Engines.Hwp;

/// <summary>Writes the text of HWP and HWPX documents as TXT, Markdown or HTML.</summary>
public sealed class HwpTextConverter : IConverter
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
    ];

    public EngineStatus GetStatus() => EngineStatus.Available("HWP, HWPX → TXT, Markdown, HTML", EngineVersions.BuiltIn);

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
