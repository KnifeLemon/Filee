using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Engines.Ebooks;
using Filee.Engines.Hwp;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Infrastructure;
using Filee.Engines.Pdf;
using Microsoft.Extensions.Logging;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Filee.Engines.Translation;

/// <summary>
/// Digital document and e-book translator preserving layout, formatting, tags, images, and tables.
/// </summary>
public sealed class TranslatorConverter : IConverter
{
    private static readonly string[] DocumentFormats =
    [
        "epub", "kepub", "mobi", "azw3", "azw", "prc", "fb2", "htmlz", "txtz",
        "md", "txt", "html", "docx", "pdf", "hwpx", "json", "yaml", "toml", "xml", "srt", "vtt", "csv"
    ];

    private readonly ITranslationService? _translationService;
    private readonly ILogger? _logger;

    public TranslatorConverter(ILogger<TranslatorConverter>? logger = null)
        : this(null, logger)
    {
    }

    public TranslatorConverter(ITranslationService? translationService, ILogger<TranslatorConverter>? logger = null)
    {
        _translationService = translationService;
        _logger = logger;
    }

    public string Id => "translator";
    public string DisplayName => "Translator";
    public int MaxParallelism => 2;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
        DocumentFormats.SelectMany(from =>
            DocumentFormats.Select(to => new ConversionEdge(from, to, from == to ? 10 : 12, Translate: true)))
        .ToList();

    private static readonly HttpClient SharedStatusClient = new() { Timeout = TimeSpan.FromMilliseconds(200) };
    private static (DateTime CheckedAt, bool Available) _cachedOllama;

    public EngineStatus GetStatus()
    {
        if (_translationService is not null)
            return EngineStatus.Available("embedded", "Test Translator");

        var now = DateTime.UtcNow;
        bool isAvailable;
        if (now - _cachedOllama.CheckedAt < TimeSpan.FromSeconds(3))
        {
            isAvailable = _cachedOllama.Available;
        }
        else
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
                using var res = SharedStatusClient.GetAsync("http://127.0.0.1:11434/api/tags", cts.Token).GetAwaiter().GetResult();
                isAvailable = res.IsSuccessStatusCode;
            }
            catch
            {
                isAvailable = false;
            }
            _cachedOllama = (now, isAvailable);
        }

        if (isAvailable)
            return EngineStatus.Available("http://127.0.0.1:11434", "Ollama (Local Offline)");

        if (TranslationService.LocateLocalEngine() is { } cli && File.Exists(cli))
            return EngineStatus.Available(cli, "Translator (Local)");

        return EngineStatus.Unavailable("engine.reason.download_translator");
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var status = GetStatus();
        if (!status.IsAvailable && _translationService is null)
            throw new ConversionRefusedException("error.translation_needs_engine");

        progress?.Report(0.05);
        var work = EbookFiles.NewFolder(step.WorkDirectory, "translate");
        var targetLang = step.Preset.Translate.TargetLanguage;
        var sourceLang = step.Preset.Translate.SourceLanguage;

        var service = _translationService ?? new TranslationService(step.Preset.Translate, logger: _logger);

        // 1. Read document / e-book into in-memory model
        progress?.Report(0.15);
        var (book, doc) = Read(step, work, cancellationToken);

        // 2. Translate text while preserving structural layout and inline tags
        progress?.Report(0.25);
        var transProgress = new Progress<double>(p => progress?.Report(0.25 + 0.65 * p));

        if (book is not null)
        {
            await DocumentTranslator.TranslateBookAsync(book, targetLang, sourceLang, service, transProgress, cancellationToken);
        }
        else if (doc is not null)
        {
            await DocumentTranslator.TranslateDocumentAsync(doc, targetLang, sourceLang, service, transProgress, cancellationToken);
        }

        // 3. Write out to requested target format
        progress?.Report(0.92);
        var ext = FormatRegistry.FindById(step.To)?.PrimaryExtension ?? step.To;
        var output = step.Output.Allocate(ext);
        if (output is null)
            return [];

        Write(book, doc, step, output, work, cancellationToken);
        progress?.Report(1.0);
        return [output];
    }

    private static (Book? Book, HDocument? Document) Read(ConversionStep step, string work, CancellationToken cancellationToken)
    {
        var input = step.InputPath;
        var media = Path.Combine(work, "media");
        Directory.CreateDirectory(media);

        switch (step.From)
        {
            case "epub" or "kepub":
                return (EpubReader.ReadBook(input, work, cancellationToken), null);
            case "mobi" or "azw3" or "azw" or "prc":
                return (MobiReader.ReadBook(input, work, cancellationToken), null);
            case "fb2":
                return (Fb2Reader.ReadBook(input, work), null);
            case "htmlz":
                return (ZippedText.ReadHtmlzBook(input, work, cancellationToken), null);
            case "txtz":
                return (ZippedText.ReadTxtzBook(input, work), null);
            case "html":
                {
                    var document = HtmlReader.Read(input, media);
                    return (new Book(document, new BookMetadata { Title = document.Title }), document);
                }
            case "md":
                {
                    var text = HwpxConverter.DecodeText(File.ReadAllBytes(input));
                    var document = MarkdownReader.Read(text, Path.GetDirectoryName(Path.GetFullPath(input))!);
                    return (new Book(document, new BookMetadata { Title = document.Title }), document);
                }
            case "txt" or "json" or "yaml" or "toml" or "xml" or "srt" or "vtt" or "csv":
                {
                    var text = HwpxConverter.DecodeText(File.ReadAllBytes(input));
                    var document = PlainText.ToDocument(text);
                    return (new Book(document, new BookMetadata()), document);
                }
            case "docx":
                {
                    var document = DocxReader.Read(input, media);
                    return (new Book(document, new BookMetadata { Title = document.Title }), document);
                }
            case "pdf":
                {
                    var document = PdfDocumentReader.Read(input, media);
                    return (new Book(document, new BookMetadata { Title = document.Title }), document);
                }
            default:
                throw new NotSupportedException($"Translation from format {step.From} is not supported.");
        }
    }

    private static void Write(Book? book, HDocument? doc, ConversionStep step, string output, string work, CancellationToken cancellationToken)
    {
        var document = doc ?? book?.Document ?? throw new InvalidOperationException("No document available to write.");
        var b = book ?? new Book(document, new BookMetadata { Title = document.Title });

        switch (step.To)
        {
            case "epub":
                EpubWriter.Write(b, output, step.InputPath, work);
                break;
            case "kepub":
                {
                    var tempEpub = Path.Combine(work, "temp.epub");
                    EpubWriter.Write(b, tempEpub, step.InputPath, work);
                    Kepub.FromEpub(tempEpub, output, cancellationToken);
                    break;
                }
            case "hwpx":
                HwpxWriter.Write(document, output);
                break;
            case "docx":
                DocxWriter.Write(document, output);
                break;
            case "txt" or "json" or "yaml" or "toml" or "xml" or "srt" or "vtt" or "csv":
                File.WriteAllText(output, PlainTextWriter.Write(document), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                break;
            case "html":
                {
                    var images = EbookFiles.NewFolder(work, "html-images");
                    var page = XhtmlWriter.Write(document, new XhtmlOptions { ImageSource = path => DataUri(path, images) }).Single();
                    File.WriteAllText(output, XhtmlWriter.Page(document.Title ?? "", page.Body, b.DisplayLanguage(), null, epub: false), new UTF8Encoding(false));
                    break;
                }
            case "md":
                File.WriteAllText(output, MarkdownWriter.Write(document, _ => null), new UTF8Encoding(false));
                break;
            case "fb2":
                Fb2Writer.Write(b, output, step.InputPath, work);
                break;
            case "htmlz":
                ZippedText.WriteHtmlz(b, output, step.InputPath, work);
                break;
            case "txtz":
                ZippedText.WriteTxtz(b, output, step.InputPath, work);
                break;
            case "pdf":
                WritePdf(document, output, work, cancellationToken);
                break;
            default:
                throw new NotSupportedException($"Writing to format {step.To} is not supported.");
        }
    }

    private static void WritePdf(HDocument document, string output, string work, CancellationToken cancellationToken)
    {
        // 1. Try bundled rhwp (Filee's primary engine for HWPX -> PDF)
        var rhwp = EngineEnvironment.FindBundled("rhwp") is { } bundled
            ? EngineEnvironment.FirstExisting(Path.Combine(bundled, OperatingSystem.IsWindows() ? "rhwp.exe" : "rhwp"))
            : null;

        if (rhwp is not null && File.Exists(rhwp))
        {
            try
            {
                var tempHwpx = Path.Combine(work, $"doc-{Guid.NewGuid():N}.hwpx");
                HwpxWriter.Write(document, tempHwpx);
                var tempPdf = Path.Combine(work, $"rhwp-{Guid.NewGuid():N}.pdf");
                var result = ProcessRunner.RunAsync(rhwp, ["export-pdf", tempHwpx, "-o", tempPdf, .. RhwpInput.PdfOptions()], TimeSpan.FromMinutes(3), cancellationToken).GetAwaiter().GetResult();
                if (result.ExitCode == 0 && File.Exists(tempPdf) && new FileInfo(tempPdf).Length > 0)
                {
                    File.Move(tempPdf, output, overwrite: true);
                    return;
                }
            }
            catch
            {
                // Fall back
            }
        }

        // 2. Try LibreOffice if available
        var lo = EngineEnvironment.FindBundled("libreoffice") is { } loBundled
            ? EngineEnvironment.FirstExisting(Path.Combine(loBundled, OperatingSystem.IsWindows() ? "program/soffice.exe" : "program/soffice"))
            : null;
        lo ??= EngineEnvironment.FirstExisting(OperatingSystem.IsWindows() ? @"C:\Program Files\LibreOffice\program\soffice.exe" : "/usr/bin/soffice");

        if (lo is not null && File.Exists(lo))
        {
            try
            {
                var tempDocx = Path.Combine(work, $"doc-{Guid.NewGuid():N}.docx");
                DocxWriter.Write(document, tempDocx);
                var loOutDir = Path.Combine(work, $"lo-{Guid.NewGuid():N}");
                Directory.CreateDirectory(loOutDir);
                var result = ProcessRunner.RunAsync(lo, ["--headless", "--convert-to", "pdf", "--outdir", loOutDir, tempDocx], TimeSpan.FromMinutes(3), cancellationToken).GetAwaiter().GetResult();
                var produced = Directory.GetFiles(loOutDir, "*.pdf").FirstOrDefault();
                if (produced is not null && File.Exists(produced) && new FileInfo(produced).Length > 0)
                {
                    File.Move(produced, output, overwrite: true);
                    return;
                }
            }
            catch
            {
                // Fall back
            }
        }

        // 3. Fallback using SkiaSharp to ensure a valid PDF is always produced
        WritePdfFallback(document, output);
    }

    private static void WritePdfFallback(HDocument document, string output)
    {
        using var stream = File.Create(output);
        using var doc = SkiaSharp.SKDocument.CreatePdf(stream);
        var margin = 40f;
        var pageWidth = 595f;
        var pageHeight = 842f;

        using var font = new SkiaSharp.SKFont(SkiaSharp.SKTypeface.Default, 11);
        using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Black, IsAntialias = true };

        var text = PlainTextWriter.Write(document);
        var lines = text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);

        var y = margin;
        var canvas = doc.BeginPage(pageWidth, pageHeight);

        foreach (var line in lines)
        {
            if (y > pageHeight - margin)
            {
                doc.EndPage();
                canvas = doc.BeginPage(pageWidth, pageHeight);
                y = margin;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                y += 14f;
                continue;
            }

            canvas.DrawText(line, margin, y, SkiaSharp.SKTextAlign.Left, font, paint);
            y += 14f;
        }

        doc.EndPage();
        doc.Close();
    }

    private static string? DataUri(string path, string convertFolder) =>
        EbookFiles.CommonImage(path, convertFolder, gifAndSvg: true) is ({ } file, { } mediaType)
            ? $"data:{mediaType};base64,{System.Convert.ToBase64String(File.ReadAllBytes(file))}"
            : null;
}
