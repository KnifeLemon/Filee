// SMART Notebook files (.notebook) as the slide model, one slide per page (#1665). The file is a ZIP package like an
// IWB file: each page is an SVG file of its own (page0.svg, page1.svg, ...), in the order imsmanifest.xml lists them
// (by number when it lists none), with pictures and attachments as other files of the package. The pages are read by
// SvgSlideReader as an IWB file's are; the deck takes the first page's size (the model has one size per deck).
//
// Written from the package as SMART Notebook 10 and later save it, not from a specification (SMART publishes none);
// SMART's own elements and attributes in the pages are skipped like other unknown SVG.

using System.Text.RegularExpressions;
using System.Xml;
using Filee.Engines.Documents;
using Filee.Engines.Slides;
using SkiaSharp;

namespace Filee.Engines.Whiteboard;

internal static partial class SmartNotebookReader
{
    /// <summary>The format id (FormatRegistry).</summary>
    public const string Format = "notebook";

    private static readonly SKSize DefaultPixels = new(1024, 768);
    private const float MaxSide = 14_400;

    public static SlideDeck Read(string path, DocumentLimits? limits = null, CancellationToken cancellationToken = default)
    {
        limits ??= DocumentLimits.Default;
        using var package = DocumentPackage.Open(path, limits,
            () => new InvalidDataException("The file is not a SMART Notebook file: it isn't a ZIP package. It may be damaged."));
        var files = new IwbReader.PackageFiles(package);
        var reader = new SvgSlideReader(files.Read, limits, 0, null, cancellationToken);
        var pages = PageNames(package, files, reader);
        if (pages.Count == 0)
            throw new InvalidDataException("The file is not a SMART Notebook file: it has no pages (page0.svg, ...).");
        if (pages.Count > limits.MaxPages)
            throw DocumentLimits.Exceeded($"The notebook has more than {DocumentLimits.Count(limits.MaxPages)} pages.");

        SlideDeck? deck = null;
        foreach (var name in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = files.Find(name)!;
            var bytes = package.ReadBytes(entry, limits.MaxPartBytes)
                        ?? throw DocumentLimits.Exceeded($"The page {name} unpacks to more than {DocumentLimits.Megabytes(limits.MaxPartBytes)}.");
            System.Xml.Linq.XElement svg;
            try
            {
                svg = reader.LoadXml(bytes);
            }
            catch (XmlException ex)
            {
                throw new InvalidDataException($"The page {name} of the notebook is damaged ({ex.Message}).", ex);
            }
            if (svg.Name.LocalName != "svg")
                throw new InvalidDataException($"The page {name} of the notebook is no SVG drawing.");
            var (pixels, toPoints) = SvgSlideReader.Viewport(svg, DefaultPixels);
            deck ??= new SlideDeck
            {
                Width = Math.Min(pixels.Width * SvgSlideReader.PointsPerPixel, MaxSide),
                Height = Math.Min(pixels.Height * SvgSlideReader.PointsPerPixel, MaxSide),
            };
            var slide = new Slide { Background = SvgSlideReader.ViewportFill(svg) };
            reader.Draw(svg.Elements(), new SvgSlideReader.SvgDocument(svg, SvgSlideReader.Folder(name)), toPoints,
                SvgStyle.Initial.Apply(svg) with { Opacity = 1, Hidden = false }, slide.Elements);
            deck.Slides.Add(slide);
        }
        deck!.LeftOut.AddRange(reader.LeftOut);
        return deck;
    }

    /// <summary>The pages in order: as imsmanifest.xml lists them, else every pageN.svg of the package by number.</summary>
    private static List<string> PageNames(DocumentPackage package, IwbReader.PackageFiles files, SvgSlideReader reader)
    {
        var listed = new List<string>();
        if (files.Find("imsmanifest.xml") is { } manifest && package.ReadBytes(manifest, package.Limits.MaxPartBytes) is { } bytes)
        {
            try
            {
                listed = [.. reader.LoadXml(bytes).Descendants().Where(e => e.Name.LocalName == "file")
                    .Select(e => SvgSlideReader.Attr(e, "href")).OfType<string>()
                    .Select(href => IwbReader.PackageFiles.Resolve("", href)).OfType<string>()
                    .Where(name => PageName().IsMatch(name) && files.Find(name) is not null)
                    .Distinct(StringComparer.OrdinalIgnoreCase)];
            }
            catch (XmlException)
            {
                // A damaged manifest: the pages by number.
            }
        }
        if (listed.Count > 0)
            return listed;
        return [.. package.Entries.Select(e => e.FullName.Replace('\\', '/').TrimStart('/'))
            .Where(name => PageName().IsMatch(name))
            .OrderBy(name => long.TryParse(PageName().Match(name).Groups[1].ValueSpan, out var n) ? n : long.MaxValue)
            .ThenBy(name => name, StringComparer.Ordinal)];
    }

    [GeneratedRegex(@"^page(\d{1,9})\.svg$", RegexOptions.IgnoreCase)]
    private static partial Regex PageName();
}
