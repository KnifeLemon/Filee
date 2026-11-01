using System.Net.Http;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Engines.Ebooks;
using Filee.Engines.Hwp;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Filee.Engines.Translation;

/// <summary>
/// Digital document and e-book translator preserving layout, formatting, tags, images, and tables.
/// </summary>
public sealed class TranslatorConverter : IConverter
{
    private static readonly string[] WritableFormats =
    [
        "docx", "hwpx", "epub", "kepub", "fb2", "htmlz", "txtz", "md", "txt", "html"
    ];

    private static readonly string[] KindleFormats =
    [
        "mobi", "azw3", "azw", "prc"
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
    public int MaxParallelism => 1;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
        WritableFormats.SelectMany(from =>
            WritableFormats.Select(to => new ConversionEdge(from, to, from == to ? 10 : 12, Translate: true)))
        .Concat(KindleFormats.Select(from => new ConversionEdge(from, "epub", 12, Translate: true)))
        .ToList();

    private static readonly HttpClient SharedStatusClient = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromMilliseconds(200)
    })
    {
        Timeout = TimeSpan.FromMilliseconds(250)
    };

    private static volatile EngineStatus _cachedStatus = EngineStatus.Unavailable("engine.reason.download_translator", detail: "http://127.0.0.1:11434");
    private static int _checkInProgress;
    private static long _lastCheckedTicks;

    public static event Action? StatusChanged;

    public EngineStatus GetStatus()
    {
        if (_translationService is not null)
            return EngineStatus.Available("embedded", "Test Translator");

        var last = Interlocked.Read(ref _lastCheckedTicks);
        if (last == 0)
        {
            PerformCheck();
            return _cachedStatus;
        }

        var now = Environment.TickCount64;
        if (now - last > 3000)
        {
            TriggerBackgroundCheck();
        }

        return _cachedStatus;
    }

    private static void PerformCheck()
    {
        try
        {
            Interlocked.Exchange(ref _lastCheckedTicks, Environment.TickCount64);
            var isAvailable = CheckOllamaStatus();
            var newStatus = isAvailable
                ? EngineStatus.Available("http://127.0.0.1:11434", "Ollama (Local Offline)")
                : EngineStatus.Unavailable("engine.reason.download_translator", detail: "http://127.0.0.1:11434");

            var oldStatus = _cachedStatus;
            _cachedStatus = newStatus;
            if (oldStatus.IsAvailable != newStatus.IsAvailable)
                StatusChanged?.Invoke();
        }
        catch
        {
            _cachedStatus = EngineStatus.Unavailable("engine.reason.download_translator", detail: "http://127.0.0.1:11434");
        }
    }

    private static bool CheckOllamaStatus()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            using var res = SharedStatusClient.GetAsync("http://127.0.0.1:11434/api/tags", cts.Token).GetAwaiter().GetResult();
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static void TriggerBackgroundCheck()
    {
        if (Interlocked.CompareExchange(ref _checkInProgress, 1, 0) == 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    Interlocked.Exchange(ref _lastCheckedTicks, Environment.TickCount64);
                    var isAvailable = await CheckOllamaStatusAsync();
                    var newStatus = isAvailable
                        ? EngineStatus.Available("http://127.0.0.1:11434", "Ollama (Local Offline)")
                        : EngineStatus.Unavailable("engine.reason.download_translator", detail: "http://127.0.0.1:11434");

                    var oldStatus = _cachedStatus;
                    _cachedStatus = newStatus;
                    if (oldStatus.IsAvailable != newStatus.IsAvailable)
                        StatusChanged?.Invoke();
                }
                catch
                {
                    _cachedStatus = EngineStatus.Unavailable("engine.reason.download_translator", detail: "http://127.0.0.1:11434");
                }
                finally
                {
                    Interlocked.Exchange(ref _checkInProgress, 0);
                }
            });
        }
    }

    private static async Task<bool> CheckOllamaStatusAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            using var res = await SharedStatusClient.GetAsync("http://127.0.0.1:11434/api/tags", cts.Token);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var isAvailable = _translationService is not null || await TranslationService.IsLocalOllamaAvailableAsync(cancellationToken);
        if (!isAvailable)
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

        // 3. Write translated model to requested output format
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
            case "txt":
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
            case "hwpx":
                {
                    var document = HwpxReader.Read(input, media);
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
            case "txt":
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
            default:
                throw new NotSupportedException($"Writing to format {step.To} is not supported.");
        }
    }

    private static string? DataUri(string path, string convertFolder) =>
        EbookFiles.CommonImage(path, convertFolder, gifAndSvg: true) is ({ } file, { } mediaType)
            ? $"data:{mediaType};base64,{System.Convert.ToBase64String(File.ReadAllBytes(file))}"
            : null;
}
