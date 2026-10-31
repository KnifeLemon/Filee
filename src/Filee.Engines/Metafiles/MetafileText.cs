// Text of a metafile (EMR_EXTTEXTOUTW and friends, META_TEXTOUT / META_EXTTEXTOUT) as glyph outlines. The LOGFONT's
// face name goes through the layout engine's font resolver (installed fonts, other names, a font of the same script
// when it is missing, fallback per character) and the text is shaped with HarfBuzz, so Korean, Arabic and Indic text
// comes out joined and reordered. Where the record gives character advances (the dx array), each character's cluster
// starts where the advances put it, as GDI places it; the shaping only decides the glyphs and their offsets inside a
// cluster.
//
// The outlines are drawn as paths: in a PDF they stay vector without embedding the fonts (SkiaSharp's PDF backend has
// no subsetter and would embed each font whole), the way SVG text is written (Vector/OutlinedTextPlayback.cs).
//
// Sizes follow GDI: a negative LOGFONT height is the em height, a positive one the cell height (usWinAscent +
// usWinDescent), and a width other than 0 scales the glyphs to that average character width (OS/2 xAvgCharWidth).

using System.Text;
using Filee.Engines.Rendering;
using Filee.Engines.Typesetting;
using SkiaSharp;

namespace Filee.Engines.Metafiles;

/// <summary>A line of text laid out in text space: x along the baseline from the pen start, y down, logical units.</summary>
/// <param name="Path">The glyph outlines.</param>
/// <param name="Width">Advance of the whole text.</param>
/// <param name="Ascent">Height of the character cell above the baseline.</param>
/// <param name="Descent">Depth of the character cell below the baseline.</param>
/// <param name="Bold">Bold the font lacks: the outlines are thickened by <paramref name="BoldWidth"/>.</param>
/// <param name="Glyphs">The glyphs the outlines are made of, at their pen positions.</param>
/// <param name="Em">The font size (em height).</param>
/// <param name="ScaleX">Horizontal scale of the glyphs.</param>
/// <param name="Skew">Slant of italic the font lacks (Skia's skew), 0 for upright.</param>
internal sealed record TextLine(SKPath Path, float Width, float Ascent, float Descent, bool Bold, float BoldWidth,
    SKRect? Underline, SKRect? StrikeOut, IReadOnlyList<TextGlyph> Glyphs, float Em, float ScaleX, float Skew) : IDisposable
{
    public void Dispose() => Path.Dispose();
}

/// <summary>A glyph of a laid-out line and its pen position in text space.</summary>
internal readonly record struct TextGlyph(PageFont Font, ushort Glyph, float X, float Y);

/// <summary>Lays out metafile text with installed fonts; one per metafile (not thread-safe).</summary>
internal sealed class MetafileText : IDisposable
{
    /// <summary>Size the glyph outlines are read at; they are scaled from there.</summary>
    private const float OutlineSize = 256;

    private static readonly Lazy<FontResolver> SharedResolver = new(() => new FontResolver(), LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly FontResolver _resolver;
    private readonly Shaper _shaper = new();
    private readonly Dictionary<GdiFont, Face?> _faces = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PageFont, SKFont> _outlineFonts = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(PageFont Font, ushort Glyph), SKPath?> _glyphs = [];

    static MetafileText() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <param name="resolver">The fonts to use; the installed ones when null.</param>
    public MetafileText(FontResolver? resolver = null) => _resolver = resolver ?? SharedResolver.Value;

    /// <summary>The font chosen for a LOGFONT.</summary>
    /// <param name="Exact">The face name itself is installed (glyph indices of the record only mean something then).</param>
    private sealed record Face(ResolvedFont Font, FontStyle Style, FontMetrics Metrics, bool Exact, int AverageWidth);

    /// <summary>Text in the font's ANSI code page as UTF-16, and how many bytes each character took.</summary>
    public static (string Text, int[] Bytes) DecodeAnsi(ReadOnlySpan<byte> bytes, GdiFont font)
    {
        var encoding = font.CharSet == GdiFont.SymbolCharSet ? Encoding.Latin1 : CodePage(font.CodePage);
        var text = encoding.GetString(bytes);
        var counts = new int[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            var length = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
            counts[i] = encoding.GetByteCount(text.AsSpan(i, length));
            if (length == 2)
                counts[++i] = 0;
        }
        return (text, counts);
    }

    private static Encoding CodePage(int codePage)
    {
        try
        {
            return Encoding.GetEncoding(codePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.Latin1;
        }
    }

    /// <summary>
    /// Lays out <paramref name="text"/> in <paramref name="font"/>; null when there is nothing to draw (no font for a
    /// symbol font or for glyph indices).
    /// </summary>
    /// <param name="glyphIndices">The text's characters are glyph indices of the font (ETO_GLYPH_INDEX).</param>
    /// <param name="dx">Advance of each character in logical units, or null to take the shaped advances.</param>
    /// <param name="dy">Vertical advance of each character (ETO_PDY), downwards in text space, or null.</param>
    /// <param name="charExtra">Space added after each character when there is no <paramref name="dx"/>.</param>
    /// <param name="pixelsPerUnit">
    /// Output pixels per logical unit for a bitmap, whose font sizes are whole pixels as GDI chooses them; 0 for vector
    /// output.
    /// </param>
    public TextLine? Layout(GdiFont font, string text, bool glyphIndices, float[]? dx, float[]? dy, float charExtra, bool rightToLeft,
        float pixelsPerUnit = 0)
    {
        if (FaceFor(font) is not { } face)
            return null;
        if ((glyphIndices || font.CharSet == GdiFont.SymbolCharSet) && !face.Exact)
            return null; // glyph numbers or symbol codes of a font that isn't there would draw the wrong signs
        var metrics = face.Metrics;
        var unitsPerEm = Math.Max(16, metrics.UnitsPerEm);
        int ascent = metrics.WinAscent > 0 ? metrics.WinAscent : metrics.Ascender;
        int descent = metrics.WinDescent > 0 ? metrics.WinDescent : -metrics.Descender;
        var em = font.Height switch
        {
            < 0 => -(float)font.Height,
            > 0 => font.Height * (float)unitsPerEm / Math.Max(1, ascent + descent),
            _ => 12f,
        };
        if (pixelsPerUnit > 0)
            em = Math.Max(1, MathF.Round(em * pixelsPerUnit)) / pixelsPerUnit;
        var scale = em / unitsPerEm;
        var scaleX = font.Width > 0 && face.AverageWidth > 0 ? font.Width / (face.AverageWidth * scale) : 1f;
        if (!float.IsFinite(scaleX) || scaleX <= 0)
            scaleX = 1;

        if (font.CharSet == GdiFont.SymbolCharSet)
            text = SymbolCodes(text, face.Font);

        var n = text.Length;
        var x = new float[n + 1];
        var y = new float[n + 1];
        var glyphs = new List<TextGlyph>();
        if (glyphIndices)
        {
            using var measure = new SKFont(face.Font.Font.Typeface, em) { LinearMetrics = true, Hinting = SKFontHinting.None };
            var ids = text.Select(c => (ushort)c).ToArray();
            var widths = measure.GetGlyphWidths(ids);
            for (var i = 0; i < n; i++)
            {
                x[i + 1] = x[i] + (dx is not null && i < dx.Length ? dx[i] : widths[i] * scaleX + charExtra);
                y[i + 1] = y[i] + (dy is not null && i < dy.Length ? dy[i] : 0);
                glyphs.Add(new TextGlyph(face.Font.Font, ids[i], x[i], y[i]));
            }
        }
        else
        {
            List<FontRun> runs = n == 0 ? [] : _resolver.Fallback(text, 0, n, face.Font, face.Style, font.Language.Length > 0 ? font.Language : null);
            var shaped = runs.Select(run => (Run: run, Shape: _shaper.Shape(run.Font.Font, text, run.Start, run.Length,
                rightToLeft ? new ShapeOptions(Direction: TextDirection.RightToLeft) : null))).ToList();

            // Advances per character: the given ones, else each cluster's shaped advance on its first character.
            var advance = new float[n];
            if (dx is not null)
            {
                for (var i = 0; i < n; i++)
                    advance[i] = i < dx.Length ? dx[i] : 0;
            }
            else
            {
                foreach (var (run, shape) in shaped)
                {
                    var runScale = em / shape.UnitsPerEm * scaleX;
                    for (var g = 0; g < shape.Glyphs.Length; g++)
                        advance[run.Start + Math.Clamp(shape.Clusters[g], 0, run.Length - 1)] += shape.XAdvances[g] * runScale;
                }
                for (var i = 0; i < n; i++)
                {
                    if (!char.IsLowSurrogate(text[i]))
                        advance[i] += charExtra;
                }
            }
            for (var i = 0; i < n; i++)
            {
                x[i + 1] = x[i] + advance[i];
                y[i + 1] = y[i] + (dy is not null && i < dy.Length ? dy[i] : 0);
            }

            foreach (var (run, shape) in shaped)
                Place(run, shape, em, scaleX, x, y, glyphs);
        }

        var path = new SKPath();
        var skew = face.Font.SyntheticItalic || (font.Italic && !face.Font.Face.Italic) ? GlyphRun.SyntheticSlant : 0;
        foreach (var (pageFont, glyph, gx, gy) in glyphs)
        {
            if (Outline(pageFont, glyph) is not { } outline)
                continue;
            var glyphScale = em / OutlineSize;
            var matrix = SKMatrix.CreateScale(glyphScale * scaleX, glyphScale);
            if (skew != 0)
                matrix = SKMatrix.CreateSkew(skew, 0).PreConcat(matrix);
            matrix = SKMatrix.CreateTranslation(gx, gy).PreConcat(matrix);
            path.AddPath(outline, in matrix);
        }

        var width = x[n];
        var cellAscent = ascent * scale;
        var cellDescent = descent * scale;
        SKRect? Line(int position, int thickness, float fallbackPosition)
        {
            var size = Math.Max(1, thickness > 0 ? thickness : unitsPerEm / 20) * scale;
            var top = -(position != 0 ? position : fallbackPosition) * scale - size / 2;
            return new SKRect(0, top, width, top + size);
        }
        return new TextLine(path, width, cellAscent, cellDescent, face.Font.SyntheticBold || (font.Weight >= 600 && face.Font.Face.Weight < 600),
            em / 32, font.Underline ? Line(metrics.UnderlinePosition, metrics.UnderlineThickness, -unitsPerEm / 10f) : null,
            font.StrikeOut ? Line(metrics.StrikeoutPosition, metrics.StrikeoutThickness, unitsPerEm / 4f) : null,
            glyphs, em, scaleX, skew);
    }

    /// <summary>
    /// Positions the glyphs of one shaped run. Left to right, each cluster starts at its first character's position;
    /// right to left (whose clusters run backwards), the run's glyphs are spread over the width its characters take.
    /// </summary>
    private static void Place(FontRun run, ShapedRun shape, float em, float scaleX, float[] x, float[] y,
        List<TextGlyph> glyphs)
    {
        var scale = em / shape.UnitsPerEm;
        if (shape.Direction == TextDirection.RightToLeft)
        {
            var span = x[run.End] - x[run.Start];
            var shapedWidth = shape.Advance * scale * scaleX;
            var factor = shapedWidth > 0 && span > 0 ? span / shapedWidth : 1;
            var pen = x[run.Start];
            for (var g = 0; g < shape.Glyphs.Length; g++)
            {
                glyphs.Add(new TextGlyph(run.Font.Font, shape.Glyphs[g], pen + shape.XOffsets[g] * scale * scaleX * factor,
                    y[run.Start] - shape.YOffsets[g] * scale));
                pen += shape.XAdvances[g] * scale * scaleX * factor;
            }
            return;
        }
        var cluster = -1;
        float inCluster = 0;
        for (var g = 0; g < shape.Glyphs.Length; g++)
        {
            var c = run.Start + Math.Clamp(shape.Clusters[g], 0, run.Length - 1);
            if (c != cluster)
            {
                cluster = c;
                inCluster = 0;
            }
            glyphs.Add(new TextGlyph(run.Font.Font, shape.Glyphs[g], x[c] + inCluster + shape.XOffsets[g] * scale * scaleX, y[c] - shape.YOffsets[g] * scale));
            inCluster += shape.XAdvances[g] * scale * scaleX;
        }
    }

    /// <summary>Symbol fonts map their signs at U+F020–U+F0FF: codes below 256 are moved there where the font has them.</summary>
    private static string SymbolCodes(string text, ResolvedFont font)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] < 0x100 && !font.Face.Has(chars[i]) && font.Face.Has(0xF000 + chars[i]))
                chars[i] = (char)(0xF000 + chars[i]);
        }
        return new string(chars);
    }

    private Face? FaceFor(GdiFont font)
    {
        if (_faces.TryGetValue(font, out var face))
            return face;
        var name = font.FaceName.TrimStart('@').Trim(); // "@Font" is the font turned for vertical text
        var style = new FontStyle(font.Weight is > 0 and <= 1000 ? font.Weight : 400, font.Italic);
        var found = name.Length > 0 ? _resolver.Find(name, style) : null;
        var resolved = found ?? _resolver.Resolve(name.Length > 0 ? name : null, style, font.Language);
        face = null;
        if (resolved is not null)
        {
            try
            {
                var metrics = _shaper.Metrics(resolved.Font);
                face = new Face(resolved, style, metrics, found is not null && string.Equals(found.Family, name, StringComparison.OrdinalIgnoreCase),
                    AverageWidth(resolved.Font));
            }
            catch (InvalidDataException)
            {
                face = null; // a font file Skia or HarfBuzz can't read: the text is left out
            }
        }
        _faces[font] = face;
        return face;
    }

    /// <summary>OS/2 xAvgCharWidth in font units, or 0.</summary>
    private static int AverageWidth(PageFont font)
    {
        try
        {
            return font.Sfnt["OS/2"] is { Length: >= 4 } os2 ? System.Buffers.Binary.BinaryPrimitives.ReadInt16BigEndian(os2.AsSpan(2)) : 0;
        }
        catch (InvalidDataException)
        {
            return 0;
        }
    }

    /// <summary>A glyph's outline at <see cref="OutlineSize"/>, baseline at y = 0, y down; null for glyphs without one.</summary>
    private SKPath? Outline(PageFont font, ushort glyph)
    {
        if (_glyphs.TryGetValue((font, glyph), out var path))
            return path;
        if (!_outlineFonts.TryGetValue(font, out var skFont))
        {
            skFont = new SKFont(font.Typeface, OutlineSize) { LinearMetrics = true, Hinting = SKFontHinting.None, Subpixel = true };
            _outlineFonts[font] = skFont;
        }
        path = skFont.GetGlyphPath(glyph);
        if (path is { IsEmpty: true })
        {
            path.Dispose();
            path = null;
        }
        _glyphs[(font, glyph)] = path;
        return path;
    }

    public void Dispose()
    {
        _shaper.Dispose();
        foreach (var font in _outlineFonts.Values)
            font.Dispose();
        foreach (var path in _glyphs.Values)
            path?.Dispose();
    }
}
