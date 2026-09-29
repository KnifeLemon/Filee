// THE place where conversion engines are registered. Adding an engine = one line in CreateAll.

using Filee.Core.Conversion;
using Filee.Engines.Hwp;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Infrastructure;
using Filee.Engines.Magick;
using Filee.Engines.Office;
using Filee.Engines.Pdf;

namespace Filee.Engines;

/// <summary>Creates every built-in converter.</summary>
public static class EngineRegistry
{
    /// <summary>
    /// Instantiates all converters. Order only matters as a tie-breaker when no priority is set, so it mirrors
    /// AppSettings.EnginePriority: free, silent engines first; Word automation after them.
    /// </summary>
    public static IReadOnlyList<IConverter> CreateAll(EngineEnvironment env) =>
    [
        new MagickImageConverter(),
        new PdfSharpConverter(),
        new PdfiumConverter(),
        new RhwpConverter(env),
        new UnhwpConverter(),
        new HwpxConverter(env),
        new WordComConverter(),
        new PandocConverter(env),
        new LibreOfficeConverter(env),
    ];

    /// <summary>Finds the PDF merger among the converters.</summary>
    public static IPdfMerger? FindPdfMerger(IEnumerable<IConverter> converters) =>
        converters.OfType<IPdfMerger>().FirstOrDefault();
}
