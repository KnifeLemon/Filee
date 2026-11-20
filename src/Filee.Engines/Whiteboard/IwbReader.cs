// Interactive whiteboard files (.iwb: the Interactive Whiteboard Common File Format, IEC 62874, first published by
// Becta), as the slide model, one slide per page (#1665). SMART Notebook, ActivInspire, OpenBoard and other
// whiteboard programs export it.
//
// The file is a ZIP package. content.xml holds an iwb root element (http://www.becta.org.uk/iwb) with one svg element
// in it: SVG Tiny 1.2 with SVG 1.2's pageSet of pages (written pageset by some programs), the drawing's size on the
// svg element, and pictures, videos and sounds as files of the package (images/, videos/, ...) its hrefs name.
// After the svg come iwb:element entries that add whiteboard properties to elements by id (ref): locked,
// background, invisible, ...; an element marked invisible is hidden on the board and isn't drawn.
//
// - Pages: each page of the pageSet in order; what the svg holds outside the pageSet (other than defs) is drawn on
//   every page first, as SVG 1.2 shows it. Without a pageSet the svg is one page. Every slide has the svg's size
//   (width and height, else its viewBox; pixels at 96 dpi), its viewBox mapped onto it; a page with a viewBox of its
//   own is fitted onto that size. A page's viewport-fill (else the svg's) is its background.
// - The SVG is read by SvgSlideReader; videos and sounds, and what it can't draw, are left out and counted
//   (SlideDeck.LeftOut, the renderer's left-out notice).
//
// Hostile input: the package through DocumentPackage (entries, part and package sizes, inflate ratio), XML through
// SafeXml, files only looked up by name inside the package (hrefs resolved against their SVG's folder, never above
// the package's top, no URLs or local paths), pages up to DocumentLimits.MaxPages, and SvgSlideReader's limits.

using System.Xml;
using System.Xml.Linq;
using Filee.Engines.Documents;
using Filee.Engines.Slides;
using SkiaSharp;

namespace Filee.Engines.Whiteboard;

internal static class IwbReader
{
    /// <summary>The format id (FormatRegistry).</summary>
    public const string Format = "iwb";

    /// <summary>The drawing's size when the svg names none: a 4:3 board.</summary>
    private static readonly SKSize DefaultPixels = new(1024, 768);

    /// <summary>Points of a slide side at most (PDF's 200 inches).</summary>
    private const float MaxSide = 14_400;

    public static SlideDeck Read(string path, DocumentLimits? limits = null, CancellationToken cancellationToken = default)
    {
        limits ??= DocumentLimits.Default;
        using var package = DocumentPackage.Open(path, limits,
            () => new InvalidDataException("The file is not an interactive whiteboard (IWB) file: it isn't a ZIP package. It may be damaged."));
        var files = new PackageFiles(package);
        var content = files.Find("content.xml")
                      ?? throw new InvalidDataException("The file is not an interactive whiteboard (IWB) file: it has no content.xml.");
        var reader = new SvgSlideReader(files.Read, limits, 0, null, cancellationToken);
        XElement root;
        try
        {
            root = reader.LoadXml(package.ReadBytes(content, limits.MaxPartBytes)
                                  ?? throw DocumentLimits.Exceeded($"content.xml unpacks to more than {DocumentLimits.Megabytes(limits.MaxPartBytes)}."));
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"The whiteboard's content.xml is damaged ({ex.Message}).", ex);
        }
        if (root.Name.LocalName != "iwb")
            throw new InvalidDataException("The file is not an interactive whiteboard (IWB) file: its content.xml has no iwb element.");
        var svg = root.Elements().FirstOrDefault(e => e.Name.LocalName == "svg")
                  ?? throw new InvalidDataException("The whiteboard has no drawing (no svg element in content.xml).");

        // Elements the whiteboard hides (iwb:element invisible="true").
        var hidden = root.Elements().Where(e => e.Name.LocalName == "element" && SvgSlideReader.Attr(e, "invisible") == "true")
            .Select(e => SvgSlideReader.Attr(e, "ref")).OfType<string>().ToList();
        reader = new SvgSlideReader(files.Read, limits, reader.XmlElements, hidden, cancellationToken);

        var (pixels, toPoints) = SvgSlideReader.Viewport(svg, DefaultPixels);
        var width = Math.Min(pixels.Width * SvgSlideReader.PointsPerPixel, MaxSide);
        var height = Math.Min(pixels.Height * SvgSlideReader.PointsPerPixel, MaxSide);
        var deck = new SlideDeck
        {
            Width = width,
            Height = height,
            Title = svg.Elements().FirstOrDefault(e => e.Name.LocalName == "title")?.Value.Trim() is { Length: > 0 } title ? title : null,
        };

        var document = new SvgSlideReader.SvgDocument(svg, "");
        var pageSet = svg.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("pageSet", StringComparison.OrdinalIgnoreCase));
        var pages = pageSet?.Elements().Where(e => e.Name.LocalName == "page").ToList() ?? [];
        if (pages.Count > limits.MaxPages)
            throw DocumentLimits.Exceeded($"The whiteboard has more than {DocumentLimits.Count(limits.MaxPages)} pages.");
        var common = svg.Elements().Where(e => !ReferenceEquals(e, pageSet)).ToList();
        var background = SvgSlideReader.ViewportFill(svg);
        // The svg's own properties (fill, font, ...) reach every page.
        var rootStyle = SvgStyle.Initial.Apply(svg) with { Opacity = 1, Hidden = false };
        if (pages.Count == 0)
        {
            var slide = new Slide { Background = background };
            reader.Draw(common, document, toPoints, rootStyle, slide.Elements);
            deck.Slides.Add(slide);
        }
        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slide = new Slide { Background = SvgSlideReader.ViewportFill(page) ?? background };
            reader.Draw(common, document, toPoints, rootStyle, slide.Elements);
            // A page with a viewBox of its own fills the drawing's size in place of the svg's viewBox.
            var matrix = toPoints;
            if (SvgSlideReader.ViewBox(page) is { } box)
            {
                matrix = SKMatrix.CreateScale(SvgSlideReader.PointsPerPixel, SvgSlideReader.PointsPerPixel)
                    .PreConcat(SvgSlideReader.Fit(box, new SKRect(0, 0, pixels.Width, pixels.Height), SvgSlideReader.Attr(page, "preserveAspectRatio")).Matrix);
            }
            reader.Draw(page.Elements(), document, matrix, rootStyle.Apply(page) with { Hidden = false }, slide.Elements);
            deck.Slides.Add(slide);
        }
        deck.LeftOut.AddRange(reader.LeftOut);
        return deck;
    }

    /// <summary>The package's files by name (case-insensitively, as whiteboard programs on Windows write them).</summary>
    internal sealed class PackageFiles(DocumentPackage package)
    {
        private Dictionary<string, System.IO.Compression.ZipArchiveEntry>? _byName;
        private readonly Dictionary<string, SvgSlideReader.SvgFile?> _read = new(StringComparer.OrdinalIgnoreCase);

        public System.IO.Compression.ZipArchiveEntry? Find(string name)
        {
            if (_byName is null)
            {
                _byName = new Dictionary<string, System.IO.Compression.ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in package.Entries)
                    _byName.TryAdd(entry.FullName.Replace('\\', '/').TrimStart('/'), entry);
            }
            return package.GetEntry(name) ?? _byName.GetValueOrDefault(name);
        }

        /// <summary>The file an href in an SVG of <paramref name="folder"/> names; null when it isn't in the package.</summary>
        public SvgSlideReader.SvgFile? Read(string folder, string href)
        {
            if (href.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return DataUri(href) is { } data ? new SvgSlideReader.SvgFile(data, "") : null;
            if (Resolve(folder, href) is not { } name)
                return null;
            if (_read.TryGetValue(name, out var cached))
                return cached;
            SvgSlideReader.SvgFile? file = null;
            if (Find(name) is { } entry && package.ReadBytes(entry, package.Limits.MaxPartBytes) is { } bytes)
                file = new SvgSlideReader.SvgFile(bytes, entry.FullName.Replace('\\', '/').TrimStart('/'));
            _read[name] = file;
            return file;
        }

        /// <summary>
        /// An href as a name in the package: relative to <paramref name="folder"/> ("/" from the top), "%20" decoded, a
        /// fragment dropped; null for URLs and local paths, and for a name that would leave the package.
        /// </summary>
        internal static string? Resolve(string folder, string href)
        {
            var text = href.Split('#')[0].Split('?')[0].Replace('\\', '/');
            if (text.Length == 0 || text.Contains(':', StringComparison.Ordinal))
                return null; // http:, file:, C:\ ...
            try
            {
                text = Uri.UnescapeDataString(text);
            }
            catch (UriFormatException)
            {
                return null;
            }
            var parts = new List<string>();
            if (!text.StartsWith('/') && folder.Length > 0)
                parts.AddRange(folder.Split('/', StringSplitOptions.RemoveEmptyEntries));
            foreach (var segment in text.Split('/'))
            {
                if (segment == "..")
                {
                    if (parts.Count == 0)
                        return null;
                    parts.RemoveAt(parts.Count - 1);
                }
                else if (segment is not ("." or ""))
                {
                    parts.Add(segment);
                }
            }
            return parts.Count == 0 ? null : string.Join('/', parts);
        }

        /// <summary>The bytes of a data: URI (base64 or percent-encoded); null when they can't be read.</summary>
        internal static byte[]? DataUri(string uri)
        {
            var comma = uri.IndexOf(',');
            if (comma < 0)
                return null;
            var header = uri[5..comma];
            var data = uri[(comma + 1)..];
            try
            {
                return header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
                    ? Convert.FromBase64String(data.Trim())
                    : System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(data));
            }
            catch (Exception ex) when (ex is FormatException or UriFormatException)
            {
                return null;
            }
        }
    }
}
