// SVG and SVGZ files as the slide model, one slide of the drawing's size, for ODG output (DrawingConverter.Odg): the
// shapes, text and pictures SvgSlideReader reads (written for the SVG of whiteboard files) stay editable in the drawing.
// SVG → PDF and pictures stay with the vector engine (Svg.Skia), which draws more of SVG.
//
// - Size: the svg's width and height (else its viewBox; CSS pixels at 96 dpi), at most 200 inches a side, its viewBox
//   mapped onto it; viewport-fill is the page's background.
// - Pictures only from data: URIs: a standalone SVG names files beside it or on the web, and Filee reads neither.
// - The check (FirstUnsupported) names the first thing SvgSlideReader doesn't draw (filters, masks, clip paths,
//   markers, patterns, CSS style sheets, text on a path, linked pictures), so such a file keeps LibreOffice Draw's route.
//
// Hostile input: the file (and an SVGZ unpacked) at most DocumentLimits.MaxPartBytes, XML through SafeXml, and
// SvgSlideReader's limits; the check reads the XML with no DTD and stops at the document limits' element count.

using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using Filee.Engines.Documents;
using Filee.Engines.Infrastructure;
using Filee.Engines.Slides;
using Filee.Engines.Vector;
using Filee.Engines.Whiteboard;
using SkiaSharp;

namespace Filee.Engines.Drawings;

internal static class SvgDrawingReader
{
    /// <summary>The drawing's size when the svg names none (CSS's default for a replaced element).</summary>
    private static readonly SKSize DefaultPixels = new(300, 150);

    /// <summary>Points of a page side at most (PDF's 200 inches).</summary>
    private const float MaxSide = 14_400;

    /// <summary>Elements SvgSlideReader doesn't draw, with the name the check gives them.</summary>
    private static readonly Dictionary<string, string> NotDrawn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["filter"] = "SVG filters",
        ["mask"] = "SVG masks",
        ["clipPath"] = "SVG clip paths",
        ["marker"] = "SVG markers",
        ["pattern"] = "SVG patterns",
        ["style"] = "CSS style sheets",
        ["textPath"] = "text on a path",
    };

    public static SlideDeck Read(string path, DocumentLimits? limits = null, CancellationToken cancellationToken = default)
    {
        limits ??= DocumentLimits.Default;
        var bytes = Load(path, limits);
        var reader = new SvgSlideReader(DataOnly, limits, 0, null, cancellationToken);
        XElement svg;
        try
        {
            svg = reader.LoadXml(bytes);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"The SVG file is damaged ({ex.Message}).", ex);
        }
        if (svg.Name.LocalName != "svg")
            throw new InvalidDataException("The file is not an SVG drawing (its root element is not svg).");

        var (pixels, toPoints) = SvgSlideReader.Viewport(svg, DefaultPixels);
        var deck = new SlideDeck
        {
            Width = Math.Min(pixels.Width * SvgSlideReader.PointsPerPixel, MaxSide),
            Height = Math.Min(pixels.Height * SvgSlideReader.PointsPerPixel, MaxSide),
            Title = svg.Elements().FirstOrDefault(e => e.Name.LocalName == "title")?.Value.Trim() is { Length: > 0 } title ? title : null,
        };
        var slide = new Slide { Background = SvgSlideReader.ViewportFill(svg) };
        var style = SvgStyle.Initial.Apply(svg) with { Opacity = 1, Hidden = false };
        reader.Draw(svg.Elements(), new SvgSlideReader.SvgDocument(svg, ""), toPoints, style, slide.Elements);
        deck.Slides.Add(slide);
        deck.LeftOut.AddRange(reader.LeftOut);
        return deck;
    }

    /// <summary>
    /// The first thing in the SVG the reader doesn't draw, or null; null too for a file that can't be read (the
    /// conversion then stops with the reader's reason).
    /// </summary>
    public static string? FirstUnsupported(string path)
    {
        try
        {
            var limits = DocumentLimits.Default;
            using var stream = new MemoryStream(Load(path, limits), writable: false);
            using var xml = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true });
            var elements = 0;
            while (xml.Read())
            {
                if (xml.NodeType != XmlNodeType.Element)
                    continue;
                if (++elements > limits.MaxXmlElements)
                    return null;
                if (NotDrawn.TryGetValue(xml.LocalName, out var name))
                    return name;
                if (xml.LocalName == "image" && (xml.GetAttribute("href", "http://www.w3.org/1999/xlink") ?? xml.GetAttribute("href")) is { } href
                    && !href.TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    return "linked pictures";
            }
            return null;
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException or IOException or UnauthorizedAccessException
                                       or Core.Conversion.ConversionRefusedException)
        {
            return null;
        }
    }

    /// <summary>The SVG's bytes, an SVGZ unpacked, at most a package part's size.</summary>
    private static byte[] Load(string path, DocumentLimits limits)
    {
        var length = new FileInfo(path).Length;
        if (length > limits.MaxPartBytes)
            throw DocumentLimits.TooLarge($"The SVG file is larger than {DocumentLimits.Megabytes(limits.MaxPartBytes)}.");
        var bytes = File.ReadAllBytes(path);
        if (!SvgRenderer.IsGzip(bytes))
            return bytes;
        using var gzip = new GZipStream(new MemoryStream(bytes, writable: false), CompressionMode.Decompress);
        using var limited = new LimitedStream(gzip, limits.MaxPartBytes,
            () => DocumentLimits.TooLarge($"The SVGZ file unpacks to more than {DocumentLimits.Megabytes(limits.MaxPartBytes)}."));
        using var copy = new MemoryStream();
        limited.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>Pictures from data: URIs only.</summary>
    private static SvgSlideReader.SvgFile? DataOnly(string folder, string href) =>
        href.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && IwbReader.PackageFiles.DataUri(href) is { } data
            ? new SvgSlideReader.SvgFile(data, "")
            : null;
}
