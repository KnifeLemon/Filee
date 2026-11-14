// A font the page model draws glyphs from: the font file's bytes (the PDF writer subsets them) and a Skia typeface
// over the whole font (for shaping, measuring and drawing PNG pages).
//
// Skia's macOS font manager (CoreText) makes typefaces from the first font of a collection only, so a later face of
// a TTC (the Chinese system fonts) is handed over as a font file of its own; failing that, an installed font keeps
// the typeface the font manager found. The glyph numbers are the same in all three.

using Filee.Engines.Fonts;
using SkiaSharp;

namespace Filee.Engines.Rendering;

/// <summary>One face of a font file, as the layout engine uses it.</summary>
internal sealed class PageFont : IDisposable
{
    private SKTypeface? _typeface;
    private SfntFont? _sfnt;

    /// <summary>The installed font's own typeface: shared with whoever else found it, so never disposed here.</summary>
    private readonly SKTypeface? _installed;

    private PageFont(byte[] data, int faceIndex, SKTypeface? installed)
    {
        Data = data;
        FaceIndex = faceIndex;
        _installed = installed;
    }

    /// <summary>The font file (TTF, OTF or TTC).</summary>
    public byte[] Data { get; }

    /// <summary>Index of the face in a TTC collection; 0 otherwise.</summary>
    public int FaceIndex { get; }

    /// <summary>A Skia typeface over the whole font.</summary>
    public SKTypeface Typeface => _typeface ??= MakeTypeface() ?? _installed
        ?? throw new InvalidDataException("Skia can't read the font.");

    /// <summary>
    /// The typeface over <see cref="Data"/>; for a face of a collection Skia can't open (macOS), over that face
    /// written out alone. Null when Skia reads neither.
    /// </summary>
    private SKTypeface? MakeTypeface()
    {
        if (SKTypeface.FromData(SKData.CreateCopy(Data), FaceIndex) is { } typeface)
            return typeface;
        if (FaceIndex == 0)
            return null;
        try
        {
            return SKTypeface.FromData(SKData.CreateCopy(Sfnt.Write()));
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>The font's tables.</summary>
    public SfntFont Sfnt => _sfnt ??= SfntFont.ReadFace(Data, FaceIndex);

    /// <summary>Reads face <paramref name="faceIndex"/> of a font file.</summary>
    public static PageFont FromFile(string path, int faceIndex = 0) => FromData(File.ReadAllBytes(path), faceIndex);

    /// <summary>A font from the bytes of a font file.</summary>
    public static PageFont FromData(byte[] data, int faceIndex = 0)
    {
        if (!SfntFont.IsSfnt(data))
            throw new InvalidDataException("This is not a TrueType or OpenType font.");
        return new PageFont(data, faceIndex, null);
    }

    /// <summary>The font file behind a typeface Skia found (an installed font, a font manager match).</summary>
    public static PageFont FromTypeface(SKTypeface typeface)
    {
        using var stream = typeface.OpenStream(out var index) ?? throw new InvalidDataException($"Skia can't read the font data of {typeface.FamilyName}.");
        var data = new byte[stream.Length];
        if (stream.Read(data, data.Length) != data.Length)
            throw new InvalidDataException($"Skia can't read the font data of {typeface.FamilyName}.");
        if (!SfntFont.IsSfnt(data))
            throw new InvalidDataException($"{typeface.FamilyName} is not a TrueType or OpenType font.");
        return new PageFont(data, index, typeface);
    }

    public void Dispose()
    {
        if (!ReferenceEquals(_typeface, _installed))
            _typeface?.Dispose();
    }
}
