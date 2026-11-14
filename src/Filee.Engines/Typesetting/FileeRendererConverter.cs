// Filee's own document renderer as an engine (id "layout", "Filee renderer"): HWPX, HWP (through the HWPX package its
// records become, Hwp/Binary) and the documents Filee reads into its document model (Word, ODT, RTF, text, Markdown,
// HTML, e-books, CSV / TSV tables, OFD, and Pandoc's formats when Pandoc is there) → PDF in one step, read, laid out and
// written in-process. The default document → PDF engine; turned off in Settings → Engines, the engine is unavailable and
// those files go through the HWPX writer and rhwp again.
// Slide decks (SlideFormats: PPTX, PPTM, POTX, POTM, PPSX) → PDF, PNG and JPG go through Filee's slide renderer
// (Slides/): a page or a picture per slide. Spreadsheets (sheet print), other presentations and PDF keep their own routes.
//
// Settings → Engines → "When LibreOffice is installed" still holds: with LibreOffice there and chosen for Word documents,
// presentations or spreadsheets, the engine leaves those files to it (its edge would win on cost otherwise).
//
// Its edges are Checked: before a file is converted, FirstUnsupported looks through it for what the layout engine
// doesn't draw yet (OLE objects and charts, vertical text, form controls, SmartArt), and such a file takes the route
// it would take without the engine (JobQueue). An HWP is looked through as the HWPX package it becomes; a deck by
// SlideCapabilities (charts, effects, ...). Pictures of document pages go on from the PDF through PDFium, which applies
// the image options; pictures of slides are drawn directly, with the image options applied here.

using System.Text;
using System.Text.RegularExpressions;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Binary;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Doc;
using Filee.Engines.Infrastructure;
using Filee.Engines.Magick;
using Filee.Engines.Office;
using Filee.Engines.Slides;
using ImageMagick;

namespace Filee.Engines.Typesetting;

/// <summary>Lays documents out with Filee's layout engine and writes them as PDF.</summary>
public sealed partial class FileeRendererConverter : IConverter, ICapabilityCheck, IImageTransformer
{
    /// <summary>
    /// Below rhwp's HWPX → PDF (10) even with the most an engine left out of the priority list adds (9), so it wins
    /// wherever it is on.
    /// </summary>
    private const int Cost = 1;

    /// <summary>Package parts read for the check at most, against a file built to make the check slow.</summary>
    private const long MaxScannedBytes = 64 * 1024 * 1024;

    /// <summary>
    /// Built-in readers of documents (not of workbooks, which sheet print takes, slides, or PDF). CSV and TSV have no
    /// page setup for sheet print and are laid out as one table each; OFD as the pages its reader rebuilds.
    /// </summary>
    private static readonly string[] Documents =
    [
        "hwpx", "hwp", "docx", "docm", "dotx", "dotm", "doc", "dot", "wps", "odt", "rtf", "txt", "md", "html", "mhtml",
        "epub", "kepub", "mobi", "azw3", "azw", "prc", "fb2", "htmlz", "txtz", "chm", "pdb", "pml", "tcr", "rb", "snb", "oeb",
        "ipynb", "opml", "textile", "mediawiki", "rst", "org", "dbk", "csv", "tsv", "ofd",
    ];

    /// <summary>
    /// Word processor files left to LibreOffice when Settings → Engines chooses it for Word documents: the ones its chosen
    /// edge reads (10 + 9, which loses to this engine's 1 + 9). The variants (DOCM, DOTX, DOTM, DOT, WPS) stay: LibreOffice
    /// takes them only at its fallback cost, so without this engine they would go to the HWPX writer and rhwp instead.
    /// </summary>
    private static readonly string[] WordProcessor = ["docx", "doc", "odt", "rtf"];

    /// <summary>
    /// Tables left to LibreOffice when Settings → Engines chooses it for spreadsheets, as XLSX goes to it. Not TSV, which
    /// LibreOffice doesn't take (it would go to the HWPX writer and rhwp).
    /// </summary>
    private static readonly string[] Tables = ["csv"];

    /// <summary>
    /// Decks → PDF and pictures. Cost 0: with the priority list's penalty it ties LibreOffice's chosen slides edge (0)
    /// at the bottom of the list, so the decks are left to LibreOffice while it is chosen for presentations.
    /// </summary>
    private static string[] Slides => SlideFormats.Decks;

    /// <summary>Pixels of one slide picture at most; a larger picture is drawn at a lower resolution.</summary>
    private const long MaxSlidePixels = 200_000_000;

    private static readonly HashSet<string> WordFormats = new(StringComparer.OrdinalIgnoreCase) { "docx", "docm", "dotx", "dotm" };

    private readonly Func<bool> _findLibreOffice;
    private bool? _pandoc;
    private bool? _libreOffice;

    /// <param name="libreOfficeInstalled">Whether LibreOffice is there (tests stand one in); <see cref="LibreOfficeConverter.Installed"/> by default.</param>
    public FileeRendererConverter(Func<bool>? libreOfficeInstalled = null) =>
        _findLibreOffice = libreOfficeInstalled ?? LibreOfficeConverter.Installed;

    public string Id => "layout";
    public string DisplayName => "Filee renderer";
    public int MaxParallelism => 0;

    /// <summary>
    /// None while it is turned off, so no planner, however it was made, routes through it. Without the formats the user
    /// chose LibreOffice for, while LibreOffice is there.
    /// </summary>
    public IReadOnlyList<ConversionEdge> Edges
    {
        get
        {
            if (!EngineEnvironment.FileeRenderer)
                return [];
            var libreOffice = _libreOffice ??= _findLibreOffice();
            var leftOut = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (libreOffice && EngineEnvironment.LibreOfficeForDocuments)
                leftOut.UnionWith(WordProcessor);
            if (libreOffice && EngineEnvironment.LibreOfficeForSheets)
                leftOut.UnionWith(Tables);
            if (libreOffice && EngineEnvironment.LibreOfficeForSlides)
                leftOut.UnionWith(Slides);
            return Documents.Where(DocumentReaders.BuiltInFormats.Contains)
                .Concat((_pandoc ??= PandocConverter.Locate() is not null) ? DocumentReaders.PandocFormats : [])
                .Where(from => !leftOut.Contains(from))
                .Select(from => new ConversionEdge(from, "pdf", Cost, Checked: true))
                .Concat(Slides.Where(from => !leftOut.Contains(from))
                    .SelectMany(from => SlideFormats.Targets.Select(to => new ConversionEdge(from, to, 0, Checked: true))))
                .ToList();
        }
    }

    /// <summary>Pictures of slides are drawn here, with the image options (turn, mirror, size) applied.</summary>
    public bool Transforms(string from, string to) => Slides.Contains(from) && to != "pdf";

    public EngineStatus GetStatus()
    {
        _pandoc = PandocConverter.Locate() is not null;
        _libreOffice = _findLibreOffice();
        return EngineEnvironment.FileeRenderer
            ? EngineStatus.Available("HWP, HWPX, Word, ODT, RTF, OFD, e-books, PPTX → PDF", EngineVersions.BuiltIn)
            : EngineStatus.Unavailable("engine.reason.preview_off");
    }

    /// <summary>
    /// The first thing in the file the layout engine doesn't draw yet, from the parts of an HWPX, Word or ODT package;
    /// other formats hold nothing it can't draw.
    /// </summary>
    public string? FirstUnsupported(string sourcePath, string sourceFormat)
    {
        if (Slides.Contains(sourceFormat))
            return SlideCapabilities.FirstUnsupported(sourcePath);
        if (sourceFormat == "hwp")
            return HwpUnsupported(sourcePath);
        if (DocumentReaders.Checked(sourceFormat))
            return DocReader.Unsupported(sourcePath) ?? DocUnsupported(sourcePath);
        var markers = sourceFormat == "hwpx" ? HwpxMarkers : sourceFormat == "odt" ? OdtMarkers : WordFormats.Contains(sourceFormat) ? WordMarkers : null;
        if (markers is null)
            return null;
        // Parts are read through the document limits (counted as they unpack, whatever their headers claim); a package
        // that passes one is left to the reader, which refuses it with its reason.
        try
        {
            using var package = DocumentPackage.Open(sourcePath);
            long scanned = 0;
            foreach (var entry in package.Entries)
            {
                var name = entry.FullName;
                if (!name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || (scanned += entry.Length) > MaxScannedBytes
                    || package.BytesRead > MaxScannedBytes)
                    continue;
                using var reader = new StreamReader(package.Open(entry), Encoding.UTF8);
                var xml = reader.ReadToEnd();
                foreach (var (pattern, feature) in markers)
                {
                    if (pattern.IsMatch(xml))
                        return feature;
                }
            }
            return null;
        }
        catch (ConversionRefusedException)
        {
            return null;
        }
    }

    /// <summary>
    /// An HWP looked through as the HWPX package its records become. A file the HWP reader refuses (password, damage)
    /// passes: the conversion then stops with the reader's message.
    /// </summary>
    private string? HwpUnsupported(string sourcePath)
    {
        var hwpx = Path.Combine(Path.GetTempPath(), $"filee-check-{Guid.NewGuid():N}.hwpx");
        try
        {
            HwpReader.ToHwpx(sourcePath, hwpx);
            return FirstUnsupported(hwpx, "hwpx");
        }
        catch (Exception ex) when (ex is InvalidDataException or ConversionRefusedException)
        {
            return null;
        }
        finally
        {
            File.Delete(hwpx);
        }
    }

    /// <summary>A Word 97–2003 document looked through as the DOCX package it becomes (as an HWP is, above).</summary>
    private string? DocUnsupported(string sourcePath)
    {
        var docx = Path.Combine(Path.GetTempPath(), $"filee-check-{Guid.NewGuid():N}.docx");
        try
        {
            DocReader.ToDocx(sourcePath, docx);
            return FirstUnsupported(docx, "docx");
        }
        catch (Exception ex) when (ex is InvalidDataException or ConversionRefusedException)
        {
            return null;
        }
        finally
        {
            File.Delete(docx);
        }
    }

    private static readonly (Regex Pattern, string Feature)[] HwpxMarkers =
    [
        (HwpxOle(), "OLE objects and charts"),
        (HwpxVertical(), "vertical text"),
        (HwpxFormControl(), "form controls"),
        (HwpxVideo(), "videos"),
    ];

    private static readonly (Regex Pattern, string Feature)[] WordMarkers =
    [
        (WordOle(), "OLE objects and charts"),
        (WordVertical(), "vertical text"),
        (WordSmartArt(), "SmartArt"),
    ];

    private static readonly (Regex Pattern, string Feature)[] OdtMarkers =
    [
        (OdtObject(), "OLE objects and charts"),
        (OdtVertical(), "vertical text"),
        (OdtFormControl(), "form controls"),
    ];

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (DocumentFonts.Missing(step.OriginalSourcePath ?? step.InputPath) is [_, ..] missing)
            step.MissingFonts?.Invoke(missing);
        if (Slides.Contains(step.From))
            return await ConvertSlidesAsync(step, progress, cancellationToken);
        HDocument document;
        StyleResolver? styles = null;
        if (step.From == "hwpx")
        {
            var media = Path.Combine(step.WorkDirectory, $"media-{Guid.NewGuid():N}");
            document = await Task.Run(() => HwpxReader.ReadForLayout(step.InputPath, media), cancellationToken);
        }
        else if (step.From == "hwp")
        {
            var media = Path.Combine(step.WorkDirectory, $"media-{Guid.NewGuid():N}");
            document = await Task.Run(() => HwpReader.ReadForLayout(step.InputPath, media), cancellationToken);
        }
        else
        {
            document = await DocumentReaders.ReadAsync(step.InputPath, step.From, step.WorkDirectory, cancellationToken);
            styles = StyleResolver.FromTemplate();
        }
        progress?.Report(0.2);

        // Reading takes the first fifth; pages written the rest.
        var pages = progress is null ? null : new Progress<double>(p => progress.Report(0.2 + 0.8 * p));
        var pdf = await Task.Run(() => FileeRenderer.ToPdf(document, cancellationToken, styles: styles, progress: pages), cancellationToken);
        var target = step.Output.Allocate("pdf");
        if (target is null)
            return [];
        await File.WriteAllBytesAsync(target, pdf, cancellationToken);
        progress?.Report(1);
        return [target];
    }

    /// <summary>A deck → one PDF, or a picture per slide (the PDF options' page range and resolution, then the image options).</summary>
    private static async Task<IReadOnlyList<string>> ConvertSlidesAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var deck = await Task.Run(() => PptxSlideReader.Read(step.InputPath, cancellationToken: cancellationToken), cancellationToken);
        progress?.Report(0.2);
        var pages = progress is null ? null : new Progress<double>(p => progress.Report(0.2 + 0.8 * p));
        if (step.To == "pdf")
        {
            var pdf = await Task.Run(() => SlideOutput.ToPdf(deck, pages, cancellationToken), cancellationToken);
            if (step.Output.Allocate("pdf") is not { } target)
                return [];
            await File.WriteAllBytesAsync(target, pdf, cancellationToken);
            progress?.Report(1);
            return [target];
        }
        return await Task.Run<IReadOnlyList<string>>(() =>
        {
            var slides = PageRange.Parse(step.Preset.Pdf.PageRange, deck.Slides.Count);
            var dpi = (float)Math.Clamp(step.Preset.Pdf.RenderDpi, 36, 1200);
            // A large slide at a high resolution would take gigabytes: the resolution comes down to fit.
            var inches = deck.Width / 72.0 * (deck.Height / 72.0);
            dpi = (float)Math.Min(dpi, Math.Sqrt(MaxSlidePixels / Math.Max(inches, 1e-6)));
            var (_, extension) = ImageEncoder.OutputFormat(step.To, step.Preset.Image);
            var written = new List<string>();
            var done = 0;
            foreach (var bitmap in SlideOutput.ToBitmaps(deck, dpi, slides, cancellationToken))
            {
                var number = slides[done] + 1;
                using (bitmap)
                using (var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
                using (var image = new MagickImage(data.ToArray()))
                {
                    image.Density = new Density(dpi, dpi, DensityUnit.PixelsPerInch);
                    ImageEncoder.ApplyOptions(image, step.Preset.Image, step.To);
                    if (step.Output.Allocate(extension, slides.Count > 1 ? $"_p{number}" : null) is { } path)
                    {
                        ImageEncoder.WriteImage(image, path, step.To);
                        written.Add(path);
                    }
                }
                done++;
                progress?.Report(0.2 + 0.8 * done / slides.Count);
            }
            return written;
        }, cancellationToken);
    }

    [GeneratedRegex(@"<\w+:(?:ole|chart)\b")]
    private static partial Regex HwpxOle();

    [GeneratedRegex(@"textDirection=""VERT")]
    private static partial Regex HwpxVertical();

    [GeneratedRegex(@"<\w+:(?:btn|checkBtn|radioBtn|comboBox|listBox|edit|scrollBar)\b")]
    private static partial Regex HwpxFormControl();

    [GeneratedRegex(@"<\w+:video\b")]
    private static partial Regex HwpxVideo();

    [GeneratedRegex(@"<o:OLEObject\b|<c:chart\b")]
    private static partial Regex WordOle();

    [GeneratedRegex(@"<w:textDirection w:val=""(?:tbRl|btLr|tbRlV|tbLrV)""")]
    private static partial Regex WordVertical();

    [GeneratedRegex(@"<dgm:relIds\b")]
    private static partial Regex WordSmartArt();

    [GeneratedRegex(@"<draw:object(?:-ole)?\b")]
    private static partial Regex OdtObject();

    [GeneratedRegex(@"writing-mode=""tb-")]
    private static partial Regex OdtVertical();

    [GeneratedRegex(@"<form:\w")]
    private static partial Regex OdtFormControl();
}
