// Filee's own XPS and OpenXPS reader (XPS 1.0, ECMA-388 OpenXPS): the package's FixedDocumentSequence (found by the
// fixed representation relationship of either spec, else by its .fdseq name), its FixedDocuments and their pages, read
// one page at a time as they are drawn (XpsPageReader → XpsPageWriter → PageContent), so pictures of a few pages don't
// read the whole document. The drawing converter writes the pages as PDF (PdfPageWriter, fonts subset), PNG and SVG.
//
// Embedded fonts (obfuscated or not) are checked by Filee's own SFNT parser before Skia sees them, and the glyphs a run
// uses by the PDF writer's subsetter (CanSubset); a run whose font is missing or damaged draws its UnicodeString with an
// installed font, and the fonts replaced are named in the file's result. Pictures: PNG, JPEG, GIF and BMP through Skia,
// TIFF through ImageMagick, JPEG XR (HD Photo, which Windows' XPS writer uses) through Filee's own decoder (JpegXr/);
// their resolution sets their size (96 dpi when they have none). A picture's own ICC profile, or the one a
// ColorConvertedBitmap names, brings it into sRGB. Pictures that are damaged or claim more than MaxPicturePixels are
// left out and counted: the converter names them too. ContextColor colours go through their ICC profile
// (XpsColorProfile: grey, RGB, CMYK, N-channel and Named Color profiles), or follow ECMA-388's rules when it isn't
// usable.
//
// Not drawn: scRGB gradients interpolate in sRGB, and print tickets, signatures, document structure and story fragments
// are not read.

using System.Buffers.Binary;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Drawings;
using Filee.Engines.Fonts;
using Filee.Engines.JpegXr;
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
    private readonly Dictionary<PageFont, HashSet<int>> _subsettable = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, XpsColorProfile?> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _fontsReplaced = new(StringComparer.OrdinalIgnoreCase);
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

    public IReadOnlyList<LeftOutPart> LeftOut
    {
        get
        {
            var parts = new List<LeftOutPart>();
            if (_picturesLeftOut > 0)
                parts.Add(new LeftOutPart("notice.renderer_left_out.pictures", _picturesLeftOut));
            if (_fontsReplaced.Count > 0)
                parts.Add(new LeftOutPart("notice.renderer_left_out.fonts", _fontsReplaced.Count));
            return parts;
        }
    }

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

    /// <summary>
    /// True when the PDF writer can subset <paramref name="font"/> with <paramref name="glyphs"/>: a font whose outlines,
    /// metrics or cmap are damaged where these glyphs need them would fail the whole PDF, so such a run is drawn with
    /// the fallback font instead. Each glyph is tried once; a font the subsetter doesn't take is embedded whole.
    /// </summary>
    internal bool CanSubset(PageFont font, ushort[] glyphs)
    {
        if (!_subsettable.TryGetValue(font, out var known))
        {
            if (!FontSubsetter.CanSubset(font.Sfnt, out _))
                return true;
            _subsettable[font] = known = [];
        }
        var fresh = glyphs.Select(g => (int)g).Where(g => !known.Contains(g)).Distinct().ToList();
        if (fresh.Count == 0)
            return true;
        try
        {
            _ = FontSubsetter.Subset(font.Sfnt, fresh).Write();
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException or OverflowException or NotSupportedException)
        {
            return false;
        }
        known.UnionWith(fresh);
        return true;
    }

    /// <summary>Notes that runs of the font part <paramref name="part"/> were drawn with the fallback font.</summary>
    internal void FontReplaced(string part) => _fontsReplaced.Add(part);

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

    /// <summary>A colour of the markup written in part <paramref name="where"/>, its ContextColor profile applied.</summary>
    internal SKColor? Color(string? text, string where)
    {
        if (XpsValues.ContextColor(text) is not var (reference, values))
            return XpsValues.Color(text);
        if (Profile(XpsPackage.Resolve(where, reference)) is { } profile && profile.Channels == values.Length - 1)
            return profile.Convert(values[0], values.AsSpan(1));
        return XpsValues.ContextFallback(values);
    }

    /// <summary>An ICC profile part sampled; null when it is missing, too large or not usable.</summary>
    private XpsColorProfile? Profile(string? part)
    {
        if (part is null)
            return null;
        if (_profiles.TryGetValue(part, out var profile))
            return profile;
        profile = _profiles.Count < Budget.Limits.MaxProfiles && _package.Bytes(part, Budget.Limits.MaxProfileBytes) is { } icc
            ? XpsColorProfile.Create(icc)
            : null;
        _profiles[part] = profile;
        return profile;
    }

    /// <summary>
    /// A picture part read through the ICC profile part <paramref name="profile"/> (null: its own profile, if any);
    /// null (and counted as left out) when it can't be drawn.
    /// </summary>
    internal XpsImage? Image(string? part, string? profile = null)
    {
        if (part is null)
            return null;
        var key = profile is null ? part : $"{part}|{profile}";
        if (_images.TryGetValue(key, out var image))
            return image;
        var icc = profile is null ? null : _package.Bytes(profile, Budget.Limits.MaxProfileBytes);
        image = Decode(_package.Bytes(part, Budget.Limits.Document.MaxPartBytes), icc);
        if (image is null)
            _picturesLeftOut++;
        else
            Track(image.Image);
        _images[key] = image;
        return image;
    }

    private XpsImage? Decode(byte[]? bytes, byte[]? icc)
    {
        if (bytes is null or { Length: < 8 })
            return null;
        var maxPixels = Budget.Limits.Document.MaxPicturePixels;
        try
        {
            if (JpegXrDecoder.IsJpegXr(bytes))
                return JpegXr(bytes, icc, maxPixels);
            if (bytes is [0x49, 0x49, 0x2A, 0x00, ..] or [0x4D, 0x4D, 0x00, 0x2A, ..])
            {
                var settings = new MagickReadSettings { Format = MagickFormat.Tiff };
                var info = new MagickImageInfo(bytes, settings);
                if ((long)info.Width * info.Height > maxPixels || info.Width == 0 || info.Height == 0)
                    return null;
                using var tiff = new MagickImage(bytes, settings);
                var dpi = Resolution(tiff.Density);
                ToSrgb(tiff, icc);
                var png = tiff.ToByteArray(MagickFormat.Png);
                return SKImage.FromEncodedData(png) is { } decoded ? Sized(decoded, dpi) : null;
            }
            // PNG, JPEG, GIF, BMP; anything else has no codec here.
            if (bytes is not ([0x89, 0x50, 0x4E, 0x47, ..] or [0xFF, 0xD8, 0xFF, ..] or [0x47, 0x49, 0x46, 0x38, ..] or [0x42, 0x4D, ..]))
                return null;
            var resolution = PngResolution(bytes) ?? JpegResolution(bytes) ?? (96, 96);
            using var data = SKData.CreateCopy(bytes);
            using (var codec = SKCodec.Create(data))
            {
                if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0 || (long)codec.Info.Width * codec.Info.Height > maxPixels)
                    return null;
                if (icc is not null)
                {
                    // The profile the markup names replaces the picture's own.
                    using var picture = new MagickImage(bytes);
                    ToSrgb(picture, icc);
                    return SKImage.FromEncodedData(picture.ToByteArray(MagickFormat.Png)) is { } converted ? Sized(converted, resolution) : null;
                }
                if (codec.Info.ColorSpace is { IsSrgb: false })
                {
                    // An embedded profile other than sRGB: Skia's codec converts the pixels into sRGB.
                    var info = codec.Info.WithColorSpace(SKColorSpace.CreateSrgb()).WithColorType(SKColorType.Rgba8888);
                    using var bitmap = new SKBitmap(info);
                    var result = codec.GetPixels(info, bitmap.GetPixels());
                    if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                        return null;
                    bitmap.SetImmutable();
                    return SKImage.FromBitmap(bitmap) is { } srgb ? Sized(srgb, resolution) : null;
                }
            }
            return SKImage.FromEncodedData(data) is { } image ? Sized(image, resolution) : null;
        }
        catch (Exception ex) when (ex is MagickException or NotSupportedException or ArgumentException)
        {
            // A damaged picture, or an ICC profile ImageMagick can't read.
            return null;
        }
    }

    /// <summary>A JPEG XR picture, decoded by Filee's own decoder and read through its ICC profile.</summary>
    private static XpsImage? JpegXr(byte[] bytes, byte[]? icc, long maxPixels)
    {
        JpegXrImage picture;
        try
        {
            picture = JpegXrDecoder.Decode(bytes, maxPixels);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IndexOutOfRangeException or OverflowException)
        {
            // Damaged, larger than allowed, or of a kind the decoder doesn't read: left out and counted.
            return null;
        }
        icc ??= picture.IccProfile;
        var info = new SKImageInfo(picture.Width, picture.Height, SKColorType.Bgra8888, picture.HasAlpha ? SKAlphaType.Unpremul : SKAlphaType.Opaque);
        if (icc is not null)
        {
            using var magick = new MagickImage();
            magick.ReadPixels(picture.Bgra, new PixelReadSettings((uint)picture.Width, (uint)picture.Height, StorageType.Char, PixelMapping.BGRA));
            ToSrgb(magick, icc);
            using var pixels = magick.GetPixels();
            if (pixels.ToByteArray(PixelMapping.BGRA) is { } converted && converted.Length == picture.Bgra.Length)
                picture = picture with { Bgra = converted };
        }
        var image = SKImage.FromPixelCopy(info, picture.Bgra, picture.Width * 4);
        return image is null ? null : Sized(image, (picture.DpiX, picture.DpiY));
    }

    /// <summary>
    /// Converts <paramref name="image"/> into sRGB through <paramref name="icc"/> (replacing its own profile), or through
    /// its own profile when that isn't sRGB; a profile of another colour space than the picture's is not used.
    /// </summary>
    private static void ToSrgb(MagickImage image, byte[]? icc)
    {
        image.RenderingIntent = RenderingIntent.Relative;
        if (icc is not null)
        {
            var given = new ColorProfile(icc);
            if (!SameKind(given.ColorSpace, image.ColorSpace))
                return;
            image.RemoveProfile("icc");
            image.TransformColorSpace(given, ColorProfiles.SRGB);
            return;
        }
        if (image.GetColorProfile() is { } own && SameKind(own.ColorSpace, image.ColorSpace)
            && own.Description?.Contains("sRGB", StringComparison.OrdinalIgnoreCase) != true)
            image.TransformColorSpace(ColorProfiles.SRGB);

        static bool SameKind(ColorSpace profile, ColorSpace picture) => (profile, picture) switch
        {
            (ColorSpace.Gray or ColorSpace.LinearGray, ColorSpace.Gray or ColorSpace.LinearGray) => true,
            (ColorSpace.sRGB or ColorSpace.RGB, ColorSpace.sRGB or ColorSpace.RGB) => true,
            (ColorSpace.CMYK, ColorSpace.CMYK) => true,
            _ => false,
        };
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
