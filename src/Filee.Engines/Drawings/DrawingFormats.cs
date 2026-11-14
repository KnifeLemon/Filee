// The drawing formats the built-in drawing converter (DrawingConverter, engine id "drawing") reads: each one a reader
// into the slide model (one slide per page, the page's size as the slide's) and, when the reader draws only part of
// what the format can hold, a check that names the first thing it can't draw (once the converter's edges are Checked,
// see DrawingConverter, such a file keeps the route it would take without the converter, LibreOffice Draw). The
// readers add their line here: ODG / FODG (Odf/Draw), VSDX planned.

using Filee.Engines.Odf.Draw;
using Filee.Engines.Slides;

namespace Filee.Engines.Drawings;

/// <summary>A drawing format the converter reads.</summary>
/// <param name="Id">The format id (FormatRegistry).</param>
/// <param name="Read">Reads a file into pages; refuses a damaged or hostile one with InvalidDataException or the document limits' error.</param>
/// <param name="FirstUnsupported">The first thing in a file the reader can't draw, or null; null when it draws everything.</param>
internal sealed record DrawingFormat(
    string Id,
    Func<string, CancellationToken, SlideDeck> Read,
    Func<string, string?>? FirstUnsupported = null);

internal static class DrawingFormats
{
    /// <summary>The formats read, one line each.</summary>
    public static IReadOnlyList<DrawingFormat> BuiltIn { get; } =
    [
        // OpenDocument drawings, packaged and flat; charts, OLE objects and 3D scenes are left to LibreOffice Draw.
        new("odg", (path, token) => OdfDrawReader.Read(path, cancellationToken: token), path => OdfDrawReader.FirstUnsupported(path)),
        new("fodg", (path, token) => OdfDrawReader.Read(path, cancellationToken: token), path => OdfDrawReader.FirstUnsupported(path)),
    ];

    /// <summary>What every drawing format is converted to.</summary>
    public static IReadOnlyList<string> Targets { get; } = ["pdf", "svg", "png"];
}
