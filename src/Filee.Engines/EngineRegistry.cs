// THE place where conversion engines are registered. Adding an engine = one line in CreateAll.

using Filee.Core.Conversion;
using Filee.Engines.Ebooks;
using Filee.Engines.Hwp;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Infrastructure;
using Filee.Engines.Magick;
using Filee.Engines.Office;
using Filee.Engines.Pdf;
using Filee.Engines.Text;

namespace Filee.Engines;

/// <summary>Creates every built-in converter.</summary>
public static class EngineRegistry
{
    /// <summary>
    /// Instantiates all converters. Order only matters as a tie-breaker when no priority is set, so it mirrors
    /// AppSettings.EnginePriority: built-in engines first, the optional downloads last. Software installed on the
    /// system (Microsoft Office, a system-wide LibreOffice) is never used.
    /// </summary>
    public static IReadOnlyList<IConverter> CreateAll(EngineEnvironment env) =>
    [
        new MagickImageConverter(),
        new PdfSharpConverter(),
        new PdfiumConverter(),
        new RhwpConverter(),
        new UnhwpConverter(),
        new MarkdownConverter(),
        new SpreadsheetConverter(),
        new HwpxConverter(),
        new EbookConverter(),
        new PandocConverter(),
        new CalibreConverter(),
        new LibreOfficeConverter(env),
    ];

    /// <summary>Finds the PDF merger among the converters.</summary>
    public static IPdfMerger? FindPdfMerger(IEnumerable<IConverter> converters) =>
        converters.OfType<IPdfMerger>().FirstOrDefault();
}
