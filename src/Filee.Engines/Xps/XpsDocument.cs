// Filee's own XPS and OpenXPS reader (XPS 1.0, ECMA-388 OpenXPS): the package's FixedDocumentSequence (found by the
// fixed representation relationship of either spec, else by its .fdseq name), its FixedDocuments and their pages, read
// one page at a time as they are drawn (XpsPageReader → XpsPageWriter → PageContent), so pictures of a few pages don't
// read the whole document. The drawing converter writes the pages as PDF (PdfPageWriter, fonts subset), PNG and SVG.
//
// Embedded fonts (obfuscated or not) are checked by Filee's own SFNT parser before Skia sees them; a run whose font is
// missing or damaged draws its UnicodeString with an installed font. Pictures: PNG, JPEG, GIF and BMP through Skia,
// TIFF through ImageMagick; their resolution sets their size (96 dpi when they have none). JPEG XR pictures (HD Photo,
// which Windows' XPS writer may use) and pictures that are damaged or claim more than MaxPicturePixels are left out and
// counted: the converter names them in the file's result.
//
// Not drawn: IsSideways glyphs are drawn upright, the Triangle line cap is flat, scRGB gradients interpolate in sRGB,
// ICC profiles of ContextColor and ColorConvertedBitmap are not applied, and print tickets, signatures, document
// structure and story fragments are not read.

using System.Buffers.Binary;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Drawings;
using Filee.Engines.Fonts;
using Filee.Engines.Rendering;
using Filee.Engines.Typesetting;
using ImageMagick;
using SkiaSharp;

namespace Filee.Engines.Xps;

internal sealed class XpsDocument : IFixedPages
{
    /// <summary>XPS units (1/96 inch) → points.</summary>
    public const float PointsPerUnit = 72f / 96f;

    /// <summary>Letter in 1/96 inch, for a page that gives no size.</summary>
    private static readonly SKSize DefaultPage = new(816, 1056);

    private readonly XpsPackage _package;
    private readonly List<PageReference> _pages = [];
    private readonly HashSet<string> _targets = new(StringComparer.Ordinal);
    private readonly HashSet<string> _destinations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PageFont?> _fonts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, XpsImage?> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, XElement?> _dictionaries = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IDisposable> _owned = [];
    private PageFont? _fallback;
    private bool _fallbackLooked;
    private int _picturesLeftOut;

    private sealed record PageReference(string Part, SKSize? Size);

    private XpsDocument(XpsPackage package, XpsLimits limits)
    {
        _package = package;
        Budget = new XpsBudget(limits);
    }

    public XpsBudget Budget { get; }

    public int Count => _pages.Count;

    public string? Title { get; private set; }

    /// <summary>The names links can point to (FixedDocument LinkTargets).</summary>
    public IReadOnlySet<string> Targets => _targets;

    /// <summary>Opens an XPS or OpenXPS file and reads its page list.</summary>
    public static XpsDocument Open(string path, CancellationToken cancellationToken = default, XpsLimits? limits = null)
    {
        limits ??= XpsLimits.Default;
        var package = XpsPackage.Open(path, limits.Document);
        var document = new XpsDocument(package, limits);
        try
        {
            document.ReadStructure(cancellationToken);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private void ReadStructure(CancellationToken cancellationToken)
    {
        var sequencePart = _package.RootRelationships("/fixedrepresentation").FirstOrDefault(_package.Has)
                           ?? _package.Names.FirstOrDefault(n => n.EndsWith(".fdseq", StringComparison.OrdinalIgnoreCase))
                           ?? throw new InvalidDataException("The file is not an XPS document: it has no fixed document sequence.");
        var sequence = _package.Xml(sequencePart) ?? throw new InvalidDataException("The XPS document's page list is missing.");
        IEnumerable<string> documents = sequence.Name.LocalName switch
        {
            "FixedDocumentSequence" => sequence.Elements()
                .Where(e => e.Name.LocalName == "DocumentReference")
                .Select(e => XpsPackage.Resolve(sequencePart, (string?)e.Attribute("Source")))
                .OfType<string>(),
            "FixedDocument" => [sequencePart],
            _ => throw new InvalidDataException("The file is not an XPS document: its page list is no fixed document sequence."),
        };
        foreach (var part in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_package.Xml(part) is not { Name.LocalName: "FixedDocument" } fixedDocument)
                continue;
            foreach (var content in fixedDocument.Elements().Where(e => e.Name.LocalName == "PageContent"))
            {
                if (XpsPackage.Resolve(part, (string?)content.Attribute("Source")) is not { } page)
                    continue;
                if (_pages.Count >= Budget.Limits.MaxPages)
                    throw DocumentLimits.TooLarge($"The document has more than {DocumentLimits.Count(Budget.Limits.MaxPages)} pages.");
                var width = XpsValues.Number((string?)content.Attribute("Width"));
                var height = XpsValues.Number((string?)content.Attribute("Height"));
                _pages.Add(new PageReference(page, width > 0 && height > 0 ? new SKSize(width.Value, height.Value) : null));
                foreach (var target in content.Descendants().Where(e => e.Name.LocalName == "LinkTarget"))
                {
                    if ((string?)target.Attribute("Name") is { Length: > 0 } name)
                        _targets.Add(name);
                }
            }
        }
        if (_pages.Count == 0)
            throw new InvalidDataException("The XPS document has no pages.");
        Title = ReadTitle();
    }

    private string? ReadTitle()
    {
        var core = _package.RootRelationships("/metadata/core-properties").FirstOrDefault(_package.Has);
        var title = _package.Xml(core)?.Elements().FirstOrDefault(e => e.Name.LocalName == "title")?.Value.Trim();
        return string.IsNullOrEmpty(title) ? null : title;
    }

    // ───────────────────────── Pages ─────────────────────────

    /// <summary>Page <paramref name="index"/>'s elements and its size in 1/96 inch.</summary>
    public (XpsCanvas Canvas, SKSize Size) ReadPage(int index, CancellationToken cancellationToken = default)
    {
        var reference = _pages[index];
        var page = _package.Xml(reference.Part);
        if (page is not { Name.LocalName: "FixedPage" })
            throw new InvalidDataException($"The XPS page {reference.Part} is missing or damaged.");
        var width = XpsValues.Number((string?)page.Attribute("Width"));
        var height = XpsValues.Number((string?)page.Attribute("Height"));
        var size = width > 0 && height > 0 ? new SKSize(width.Value, height.Value) : reference.Size ?? DefaultPage;
        var max = Budget.Limits.MaxPageSide / PointsPerUnit;
        size = new SKSize(Math.Min(size.Width, max), Math.Min(size.Height, max));
        return (new XpsPageReader(this, reference.Part, cancellationToken).Read(page), size);
    }

    /// <summary>Page <paramref name="index"/> drawn into the page model.</summary>
    public PageContent Page(int index, CancellationToken cancellationToken = default)
    {
        var (canvas, size) = ReadPage(index, cancellationToken);
        return XpsPageWriter.Write(canvas, size, this);
    }

    /// <summary>True the first time a link target is placed (a PDF destination is defined once).</summary>
    internal bool PlaceDestination(string name) => _targets.Contains(name) && _destinations.Add(name);

    public IReadOnlyList<LeftOutPart> LeftOut =>
        _picturesLeftOut > 0 ? [new LeftOutPart("notice.renderer_left_out.pictures", _picturesLeftOut)] : [];

    // ───────────────────────── Resources ─────────────────────────

    /// <summary>Keeps <paramref name="owned"/> until the document is disposed (the pages draw with it).</summary>
    internal T Track<T>(T owned) where T : IDisposable
    {
        _owned.Add(owned);
        return owned;
    }

    /// <summary>The root of a remote resource dictionary part.</summary>
    internal XElement? Dictionary(string part)
    {
        if (!_dictionaries.TryGetValue(part, out var dictionary))
            _dictionaries[part] = dictionary = _package.Xml(part) is { Name.LocalName: "ResourceDictionary" } root ? root : null;
        return dictionary;
    }

    /// <summary>An embedded font (face <paramref name="fragment"/> of a collection); null when it is missing or damaged.</summary>
    internal PageFont? Font(string? part, string? fragment)
    {
        if (part is null)
            return null;
        var face = int.TryParse(fragment, out var index) && index is >= 0 and < 256 ? index : 0;
        var key = $"{part}#{face}";
        if (_fonts.TryGetValue(key, out var font))
            return font;
        font = null;
        if (_package.Font(part) is { } data)
        {
            try
            {
                // Filee's own parser reads the tables before Skia or HarfBuzz do.
                SfntFont.ReadFace(data, face);
                font = PageFont.FromData(data, face);
                _ = font.Typeface;
                Track(font);
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException or OverflowException)
            {
                font?.Dispose();
                font = null;
            }
        }
        _fonts[key] = font;
        return font;
    }

    /// <summary>An installed font for runs whose own font is missing.</summary>
    internal PageFont? FallbackFont
    {
        get
        {
            if (!_fallbackLooked)
            {
                _fallbackLooked = true;
                try
                {
                    _fallback = new FontResolver().Resolve(null, FontStyle.Regular)?.Font;
                }
                catch (InvalidDataException)
                {
                    _fallback = null;
                }
            }
            return _fallback;
        }
    }

    /// <summary>A picture part; null (and counted as left out) when it can't be drawn.</summary>
    internal XpsImage? Image(string? part)
    {
        if (part is null)
            return null;
        if (_images.TryGetValue(part, out var image))
            return image;
        image = Decode(_package.Bytes(part, Budget.Limits.Document.MaxPartBytes));
        if (image is null)
            _picturesLeftOut++;
        else
            Track(image.Image);
        _images[part] = image;
        return image;
    }

    private XpsImage? Decode(byte[]? bytes)
    {
        if (bytes is null or { Length: < 8 })
            return null;
        var maxPixels = Budget.Limits.Document.MaxPicturePixels;
        try
        {
            if (bytes is [0x49, 0x49, 0x2A, 0x00, ..] or [0x4D, 0x4D, 0x00, 0x2A, ..])
            {
                var settings = new MagickReadSettings { Format = MagickFormat.Tiff };
                var info = new MagickImageInfo(bytes, settings);
                if ((long)info.Width * info.Height > maxPixels || info.Width == 0 || info.Height == 0)
                    return null;
                using var tiff = new MagickImage(bytes, settings);
                var dpi = Resolution(tiff.Density);
                var png = tiff.ToByteArray(MagickFormat.Png);
                return SKImage.FromEncodedData(png) is { } decoded ? Sized(decoded, dpi) : null;
            }
            // PNG, JPEG, GIF, BMP; anything else (JPEG XR among them) has no codec here.
            if (bytes is not ([0x89, 0x50, 0x4E, 0x47, ..] or [0xFF, 0xD8, 0xFF, ..] or [0x47, 0x49, 0x46, 0x38, ..] or [0x42, 0x4D, ..]))
                return null;
            using var data = SKData.CreateCopy(bytes);
            using (var codec = SKCodec.Create(data))
            {
                if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0 || (long)codec.Info.Width * codec.Info.Height > maxPixels)
                    return null;
            }
            return SKImage.FromEncodedData(data) is { } image ? Sized(image, PngResolution(bytes) ?? JpegResolution(bytes) ?? (96, 96)) : null;
        }
        catch (MagickException)
        {
            return null;
        }
    }

    private static XpsImage Sized(SKImage image, (double X, double Y) dpi) =>
        new(image, (float)(image.Width * 96 / dpi.X), (float)(image.Height * 96 / dpi.Y));

    /// <summary>A TIFF's resolution in dots per inch; 96 when it gives none.</summary>
    private static (double X, double Y) Resolution(Density density)
    {
        var factor = density.Units switch
        {
            DensityUnit.PixelsPerInch => 1,
            DensityUnit.PixelsPerCentimeter => 2.54,
            _ => 0,
        };
        return factor > 0 && density.X > 1 && density.Y > 1 ? (density.X * factor, density.Y * factor) : (96, 96);
    }

    /// <summary>A PNG's pHYs chunk in dots per inch; null when it has none in metres.</summary>
    internal static (double X, double Y)? PngResolution(ReadOnlySpan<byte> png)
    {
        if (png is not [0x89, 0x50, 0x4E, 0x47, ..])
            return null;
        var at = 8;
        // Chunks up to the image data, a few dozen at most.
        for (var chunks = 0; chunks < 64 && at + 12 <= png.Length; chunks++)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(png[at..]);
            var type = png.Slice(at + 4, 4);
            if (type.SequenceEqual("IDAT"u8) || length > int.MaxValue - 12 || at + 12 + length > png.Length)
                return null;
            if (type.SequenceEqual("pHYs"u8) && length >= 9 && png[at + 16] == 1)
            {
                var x = BinaryPrimitives.ReadUInt32BigEndian(png[(at + 8)..]) * 0.0254;
                var y = BinaryPrimitives.ReadUInt32BigEndian(png[(at + 12)..]) * 0.0254;
                return x >= 1 && y >= 1 ? (x, y) : null;
            }
            at += 12 + (int)length;
        }
        return null;
    }

    /// <summary>A JPEG's JFIF density in dots per inch; null when it has none in inches or centimetres.</summary>
    internal static (double X, double Y)? JpegResolution(ReadOnlySpan<byte> jpeg)
    {
        // APP0 "JFIF\0": version (2), units (1), x and y density (2 each).
        if (jpeg.Length < 18 || jpeg is not [0xFF, 0xD8, 0xFF, 0xE0, ..] || !jpeg.Slice(6, 5).SequenceEqual("JFIF\0"u8))
            return null;
        var units = jpeg[13];
        var x = BinaryPrimitives.ReadUInt16BigEndian(jpeg[14..]);
        var y = BinaryPrimitives.ReadUInt16BigEndian(jpeg[16..]);
        if (x == 0 || y == 0)
            return null;
        return units switch
        {
            1 => (x, y),
            2 => (x * 2.54, y * 2.54),
            _ => null,
        };
    }

    public void Dispose()
    {
        foreach (var owned in _owned)
            owned.Dispose();
        _owned.Clear();
        _package.Dispose();
    }
}
