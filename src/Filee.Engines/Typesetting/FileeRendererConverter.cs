// Filee's own document renderer as an engine (id "layout", "Filee renderer"): HWPX, HWP (through the HWPX package its
// records become, Hwp/Binary) and the documents Filee reads into its document model (Word, ODT, RTF, text, Markdown,
// HTML, e-books, and Pandoc's formats when Pandoc is there) → PDF in one step, read, laid out and written in-process.
// A preview the user turns on in Settings → Engines; off, the engine is unavailable and every route stays as it was.
// Spreadsheets, presentations, PDF and OFD keep their own routes.
//
// Its edges are Checked: before a file is converted, FirstUnsupported looks through it for what the layout engine
// doesn't draw yet (OLE objects and charts, vertical text, form controls, SmartArt), and such a file takes the route
// it would take without the engine (JobQueue). An HWP is looked through as the HWPX package it becomes. Pictures of
// the pages go on from the PDF through PDFium, which applies the image options.

using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Filee.Core.Conversion;
using Filee.Engines.Hwp.Binary;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Infrastructure;
using Filee.Engines.Office;

namespace Filee.Engines.Typesetting;

/// <summary>Lays documents out with Filee's layout engine and writes them as PDF.</summary>
public sealed partial class FileeRendererConverter : IConverter, ICapabilityCheck
{
    /// <summary>
    /// Below rhwp's HWPX → PDF (10) even with the most an engine left out of the priority list adds (9), so it wins
    /// wherever it is on.
    /// </summary>
    private const int Cost = 1;

    /// <summary>Package parts read for the check at most, against a file built to make the check slow.</summary>
    private const long MaxScannedBytes = 64 * 1024 * 1024;

    /// <summary>Built-in readers of documents (not of sheets, slides, PDF or OFD, which have routes of their own).</summary>
    private static readonly string[] Documents =
    [
        "hwpx", "hwp", "docx", "docm", "dotx", "dotm", "odt", "rtf", "txt", "md", "html", "mhtml",
        "epub", "kepub", "mobi", "azw3", "azw", "prc", "fb2", "htmlz", "txtz", "ipynb", "opml", "textile", "mediawiki", "rst", "org", "dbk",
    ];

    private static readonly HashSet<string> WordFormats = new(StringComparer.OrdinalIgnoreCase) { "docx", "docm", "dotx", "dotm" };

    private bool? _pandoc;

    public string Id => "layout";
    public string DisplayName => "Filee renderer";
    public int MaxParallelism => 0;

    /// <summary>None while the preview is off, so no planner, however it was made, routes through it.</summary>
    public IReadOnlyList<ConversionEdge> Edges => !EngineEnvironment.FileeRenderer ? [] :
        Documents.Where(DocumentReaders.BuiltInFormats.Contains)
            .Concat((_pandoc ??= PandocConverter.Locate() is not null) ? DocumentReaders.PandocFormats : [])
            .Select(from => new ConversionEdge(from, "pdf", Cost, Checked: true))
            .ToList();

    public EngineStatus GetStatus()
    {
        _pandoc = PandocConverter.Locate() is not null;
        return EngineEnvironment.FileeRenderer
            ? EngineStatus.Available(version: "preview")
            : EngineStatus.Unavailable("engine.reason.preview_off");
    }

    /// <summary>
    /// The first thing in the file the layout engine doesn't draw yet, from the parts of an HWPX, Word or ODT package;
    /// other formats hold nothing it can't draw.
    /// </summary>
    public string? FirstUnsupported(string sourcePath, string sourceFormat)
    {
        if (sourceFormat == "hwp")
            return HwpUnsupported(sourcePath);
        var markers = sourceFormat == "hwpx" ? HwpxMarkers : sourceFormat == "odt" ? OdtMarkers : WordFormats.Contains(sourceFormat) ? WordMarkers : null;
        if (markers is null)
            return null;
        using var zip = ZipFile.OpenRead(sourcePath);
        long scanned = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            if (!name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || (scanned += entry.Length) > MaxScannedBytes)
                continue;
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            var xml = reader.ReadToEnd();
            foreach (var (pattern, feature) in markers)
            {
                if (pattern.IsMatch(xml))
                    return feature;
            }
        }
        return null;
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
