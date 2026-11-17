// Text as glyph outlines: shaped with HarfBuzz the way Svg.Skia does it (adapted from SkiaModel.TextShaping.cs,
// Svg.Skia, MIT), so Arabic joins and Indic syllables come out right, and drawn as paths, so no font is embedded. Used
// for SVG text in PDFs (OutlinedTextPlayback), for PDF watermarks and page numbers (PdfFinishing) and for Annotate's
// labels (AnnotationPainter).

using System.Runtime.InteropServices;
using HarfBuzzSharp;
using Buffer = HarfBuzzSharp.Buffer;
using SKFont = SkiaSharp.SKFont;
using SKPath = SkiaSharp.SKPath;
using SKPoint = SkiaSharp.SKPoint;
using SKTextAlign = SkiaSharp.SKTextAlign;
using SKTypeface = SkiaSharp.SKTypeface;

namespace Filee.Engines.Vector;

/// <summary>Shapes text and appends its glyph outlines to a path; HarfBuzz fonts are cached per typeface.</summary>
internal sealed class GlyphOutlines : IDisposable
{
    /// <summary>HarfBuzz works in integer units: positions are computed at this scale and divided back.</summary>
    private const int HarfBuzzScale = 512;

    private readonly Dictionary<string, Font?> _shapers = new(StringComparer.Ordinal);
    // Entries go with their typeface: the table holds typefaces weakly, and a HarfBuzz font frees its native font when
    // it is collected (NativeObject's finalizer), so typefaces left behind by a language change aren't kept.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<SKTypeface, Shaper>? _byTypeface;

    private sealed record Shaper(Font? Font);

    /// <summary>
    /// <paramref name="perTypeface"/>: HarfBuzz fonts are kept per typeface object instead of per family and style, for
    /// a caller that holds two faces of one name (an app's own "Noto Sans" and the system's), whose glyphs differ.
    /// </summary>
    public GlyphOutlines(bool perTypeface = false)
    {
        if (perTypeface)
            _byTypeface = [];
    }

    /// <summary>Shapes <paramref name="text"/> and appends its glyph outlines, aligned at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public bool AppendShaped(SKPath path, string text, SKFont font, float x, float y, SKTextAlign align)
    {
        if (string.IsNullOrEmpty(text))
            return true;
        if (Shape(text, font) is not (var infos, var positions))
            return false;

        var scaleY = font.Size / HarfBuzzScale;
        var scaleX = scaleY * font.ScaleX;
        var width = 0f;
        foreach (var position in positions)
            width += position.XAdvance * scaleX;
        var penX = x - align switch
        {
            SKTextAlign.Center => width / 2,
            SKTextAlign.Right => width,
            _ => 0f,
        };
        var penY = y;
        for (var i = 0; i < infos.Length; i++)
        {
            AppendGlyph(path, font, (ushort)infos[i].Codepoint, penX + positions[i].XOffset * scaleX, penY - positions[i].YOffset * scaleY);
            penX += positions[i].XAdvance * scaleX;
            penY -= positions[i].YAdvance * scaleY;
        }
        return true;
    }

    /// <summary>
    /// <paramref name="text"/> shaped: its glyphs, where each is drawn from the start of the baseline, and how wide it
    /// is; null when the font's data can't be read. For drawing as text, which keeps colour glyphs (emoji) that have no
    /// outline. <paramref name="rightToLeft"/> sets the direction (a bracket is mirrored right to left), else HarfBuzz
    /// guesses it from the letters.
    /// </summary>
    public (ushort[] Glyphs, SKPoint[] Positions, float Width)? Glyphs(string text, SKFont font, bool? rightToLeft = null)
    {
        if (Shape(text, font, rightToLeft) is not (var infos, var positions))
            return null;
        var scaleY = font.Size / HarfBuzzScale;
        var scaleX = scaleY * font.ScaleX;
        var glyphs = new ushort[infos.Length];
        var points = new SKPoint[infos.Length];
        float penX = 0, penY = 0;
        for (var i = 0; i < infos.Length; i++)
        {
            glyphs[i] = (ushort)infos[i].Codepoint;
            points[i] = new SKPoint(penX + positions[i].XOffset * scaleX, penY - positions[i].YOffset * scaleY);
            penX += positions[i].XAdvance * scaleX;
            penY -= positions[i].YAdvance * scaleY;
        }
        return (glyphs, points, penX);
    }

    /// <summary>
    /// The glyphs <paramref name="text"/> is drawn with and where, or null when the font's data can't be read.
    /// </summary>
    private (GlyphInfo[] Infos, GlyphPosition[] Positions)? Shape(string text, SKFont font, bool? rightToLeft = null)
    {
        var shaper = ShaperFor(font.Typeface);
        if (shaper is null)
            return null;
        using var buffer = new Buffer();
        buffer.ClusterLevel = ClusterLevel.Characters;
        buffer.AddUtf16(text);
        buffer.GuessSegmentProperties();
        if (rightToLeft is { } rtl)
            buffer.Direction = rtl ? Direction.RightToLeft : Direction.LeftToRight;
        shaper.Shape(buffer);
        return (buffer.GlyphInfos, buffer.GlyphPositions);
    }

    public static void AppendGlyph(SKPath path, SKFont font, ushort glyph, float x, float y)
    {
        using var outline = font.GetGlyphPath(glyph);
        if (outline is { IsEmpty: false })
            path.AddPath(outline, x, y);
    }

    /// <summary>A HarfBuzz font for the typeface (cached), or null when its font data can't be read.</summary>
    private Font? ShaperFor(SKTypeface typeface)
    {
        if (_byTypeface is not null)
            return _byTypeface.GetValue(typeface, face => new Shaper(Load(face))).Font;
        var key = $"{typeface.FamilyName}|{typeface.FontWeight}|{typeface.FontWidth}|{typeface.FontSlant}";
        if (_shapers.TryGetValue(key, out var cached))
            return cached;
        var font = Load(typeface);
        _shapers[key] = font;
        return font;
    }

    /// <summary>A HarfBuzz font made from the typeface's font data, or null when it can't be read.</summary>
    private static Font? Load(SKTypeface typeface)
    {
        var asset = typeface.OpenStream(out var index);
        if (asset is null)
            return null;
        using var blob = ToBlob(asset);
        using var face = new Face(blob, index) { Index = index, UnitsPerEm = typeface.UnitsPerEm };
        var font = new Font(face);
        font.SetScale(HarfBuzzScale, HarfBuzzScale);
        font.SetFunctionsOpenType();
        return font;
    }

    /// <summary>Wraps Skia's font data for HarfBuzz without copying when Skia has it in memory.</summary>
    private static Blob ToBlob(SkiaSharp.SKStreamAsset asset)
    {
        var size = asset.Length;
        var memory = asset.GetMemoryBase();
        Blob blob;
        if (memory != IntPtr.Zero)
        {
            blob = new Blob(memory, size, MemoryMode.ReadOnly, asset.Dispose);
        }
        else
        {
            var copy = Marshal.AllocCoTaskMem(size);
            asset.Read(copy, size);
            blob = new Blob(copy, size, MemoryMode.ReadOnly, () =>
            {
                Marshal.FreeCoTaskMem(copy);
                asset.Dispose();
            });
        }
        blob.MakeImmutable();
        return blob;
    }

    public void Dispose()
    {
        foreach (var font in _shapers.Values)
            font?.Dispose();
        _shapers.Clear();
    }
}
