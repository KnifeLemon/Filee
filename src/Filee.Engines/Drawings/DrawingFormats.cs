// The drawing formats the built-in drawing converter (DrawingConverter, engine id "drawing") reads: each one a reader
// into the slide model (one slide per page, the page's size as the slide's) and, when the reader draws only part of
// what the format can hold, a check that names the first thing it can't draw (once the converter's edges are Checked,
// see DrawingConverter, such a file keeps the route it would take without the converter, LibreOffice Draw). The
// readers add their line here: VSDX (Visio/), ODG / OTG / FODG (Odf/Draw), XPS / OpenXPS (Xps/), chart templates (Charts/),
// interactive whiteboards (Whiteboard/), mind maps (MindMaps/).
//
// Fixed-layout documents (XPS), chart templates and mind maps are not slides: their reader draws pages straight into the page model (IFixedPages).

using Filee.Core.Conversion;
using Filee.Engines.Charts;
using Filee.Engines.Hwp.Hwpx.Markup;
using Filee.Engines.MindMaps;
using Filee.Engines.Odf.Draw;
using Filee.Engines.Rendering;
using Filee.Engines.Slides;
using Filee.Engines.Visio;
using Filee.Engines.Whiteboard;
using Filee.Engines.Xps;

namespace Filee.Engines.Drawings;

/// <summary>A drawing format the converter reads.</summary>
/// <param name="Id">The format id (FormatRegistry).</param>
/// <param name="Read">
/// Reads a file into pages; refuses a damaged or hostile one with InvalidDataException or the document limits' error.
/// Null for a format read by <see cref="ReadPages"/>.
/// </param>
/// <param name="FirstUnsupported">The first thing in a file the reader can't draw, or null; null when it draws everything.</param>
internal sealed record DrawingFormat(
    string Id,
    Func<string, CancellationToken, SlideDeck>? Read,
    Func<string, string?>? FirstUnsupported = null)
{
    /// <summary>Opens a fixed-layout document whose pages its reader draws itself (instead of <see cref="Read"/>).</summary>
    public Func<string, CancellationToken, IFixedPages>? ReadPages { get; init; }

    /// <summary>
    /// Slides (a presentation format no other engine reads): also written as ODP by the ODP writer and as PPTX through
    /// its PDF (the PDF slides engine's editable slides), as routes of their own (<see cref="DrawingFormats.SlideTargets"/>).
    /// </summary>
    public bool Slides { get; init; }

    /// <summary>The cost of the format's edges (ConversionEdge.Cost).</summary>
    public int Cost { get; init; } = 10;
}

/// <summary>
/// A fixed-layout document opened for drawing: its pages are read and drawn when asked for, so pictures of a few pages
/// don't read the rest.
/// </summary>
internal interface IFixedPages : IDisposable
{
    int Count { get; }

    string? Title { get; }

    /// <summary>Page <paramref name="index"/> drawn into the page model.</summary>
    PageContent Page(int index, CancellationToken cancellationToken = default);

    /// <summary>What the pages drawn so far left out (pictures that can't be decoded), for the file's result.</summary>
    IReadOnlyList<LeftOutPart> LeftOut { get; }
}

internal static class DrawingFormats
{
    /// <summary>The formats read, one line each.</summary>
    public static IReadOnlyList<DrawingFormat> BuiltIn { get; } =
    [
        // Format id vsd is both .vsdx and binary .vsd: the binary one is left to LibreOffice Draw.
        new("vsd", VisioReader.Read, VisioReader.FirstUnsupported),
        // OpenDocument drawings, packaged, templates and flat; charts, OLE objects and 3D scenes are left to LibreOffice
        // Draw (a template through the ODG it becomes, OdfVariantConverter).
        new("odg", (path, token) => OdfDrawReader.Read(path, cancellationToken: token), path => OdfDrawReader.FirstUnsupported(path)),
        new("otg", (path, token) => OdfDrawReader.Read(path, cancellationToken: token), path => OdfDrawReader.FirstUnsupported(path)),
        new("fodg", (path, token) => OdfDrawReader.Read(path, cancellationToken: token), path => OdfDrawReader.FirstUnsupported(path)),
        // XPS and OpenXPS documents (.xps, .oxps): Filee's own reader draws every page.
        new("xps", null) { ReadPages = (path, token) => XpsDocument.Open(path, token) },
        // Excel chart templates (.crtx): the chart on one page, with sample values where the template has none.
        new("crtx", null) { ReadPages = (path, token) => ChartTemplate.Open(path, token) },
        // Interactive whiteboard files (.iwb, IEC 62874, #1665) and SMART Notebook files: a slide per page of their SVG.
        new(IwbReader.Format, (path, token) => IwbReader.Read(path, cancellationToken: token)) { Slides = true },
        new(SmartNotebookReader.Format, (path, token) => SmartNotebookReader.Read(path, cancellationToken: token)) { Slides = true },
        // Mind maps (.mm, .xmind) drawn as maps, a page per sheet (MindMaps/; a .mm of Objective-C++ source as its text).
        // Cost 0: below the Filee renderer's outline → PDF (1).
        new(MindMapReader.FreeMind, null) { ReadPages = (path, token) => MindMapPages.Open(path, MindMapReader.FreeMind, token), Cost = 0 },
        new(MindMapReader.XMind, null) { ReadPages = (path, token) => MindMapPages.Open(path, MindMapReader.XMind, token), Cost = 0 },
    ];

    /// <summary>What every drawing format is converted to.</summary>
    public static IReadOnlyList<string> Targets { get; } = ["pdf", "svg", "png"];

    /// <summary>What formats of <see cref="DrawingFormat.Slides"/> are also converted to.</summary>
    public static IReadOnlyList<string> SlideTargets { get; } = ["odp", "pptx"];
}
