// Pictures and glyph outlines PdfSlideRaster draws more than once on a page: the backdrop of each blended paint draws
// the pictures and text under it again. Decoded pictures and outlines are kept for the page, up to a limit; past it they
// are made for each drawing and dropped.
//
// Glyph outlines come from the PDF's own font program (PdfPig's IFont.TryGetNormalisedPath: text space, an em of 1,
// y up). A font without one (the standard 14 fonts, which PDFs name without embedding them) is drawn with an installed
// font of the same kind found through CadFonts: Helvetica and Arial with a sans-serif one, Times with a serif one,
// Courier with a monospaced one, other letters with the CJK fallback CadFonts has. A code that stands for several
// characters (a ligature: "fi", "ffl", U+FB01) draws all of them, squeezed into the PDF's advance for the code. Type 3
// glyphs are drawn from their procedures (PdfType3Glyph), up to MaxType3Paints of their paints a page, then from
// their text like the others.
//
// Pictures PdfPig can't decode are read with Filee's own decoders where they exist (PdfOwnPicture: JBIG2, JPEG behind
// other filters).

using Filee.Engines.Cad;
using SkiaSharp;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.PdfFonts;

namespace Filee.Engines.Pdf;

/// <summary>Decoded pictures and glyph outlines of one page (see the top).</summary>
internal sealed class PdfRasterCache : IDisposable
{
    /// <summary>Pixels of the pictures kept at most.</summary>
    internal const long MaxPictureBytes = 256L * 1024 * 1024;

    /// <summary>Glyph outlines kept at most.</summary>
    internal const int MaxOutlines = 20_000;

    /// <summary>Paints of Type 3 glyph procedures drawn at most; past that their glyphs are drawn from their text.</summary>
    internal const long MaxType3Paints = 100_000;

    private readonly Dictionary<(object Image, SKColor Color), SKBitmap?> _pictures = [];
    private long _type3Paints;
    private readonly Dictionary<(IFont Font, int Code, string Unicode), SKPath?> _outlines = [];
    private long _pictureBytes;

    /// <summary>The picture's pixels, kept (not to be disposed); null when it can't be decoded.</summary>
    public SKBitmap? Decode(PdfPaintedImage image)
    {
        if (((object?)image.Own ?? image.Image) is not { } source)
            return null;
        var key = (source, image.IsMask ? image.MaskColor : SKColors.Transparent);
        if (_pictures.TryGetValue(key, out var known))
            return known;
        var bitmap = Read(image);
        if (bitmap is not null && _pictureBytes + bitmap.ByteCount > MaxPictureBytes)
        {
            bitmap.Dispose();
            return null;
        }
        _pictureBytes += bitmap?.ByteCount ?? 0;
        return _pictures[key] = bitmap;
    }

    /// <summary>Whether a Type 3 glyph's procedure is drawn, counted against <see cref="MaxType3Paints"/>.</summary>
    public bool DrawsProcedure(PdfType3Glyph glyph)
    {
        if (_type3Paints + glyph.Paints.Count > MaxType3Paints)
            return false;
        _type3Paints += glyph.Paints.Count;
        return true;
    }

    /// <summary>The glyph's outline in its text space, kept (not to be disposed); null when it has none.</summary>
    public SKPath? Outline(PdfPaintedGlyph glyph)
    {
        var key = (glyph.Font, glyph.Code, glyph.Unicode);
        if (_outlines.TryGetValue(key, out var known))
            return known;
        var outline = Shape(glyph);
        if (_outlines.Count >= MaxOutlines)
        {
            outline?.Dispose();
            return null;
        }
        return _outlines[key] = outline;
    }

    /// <summary>The picture's pixels (the caller disposes them); a stencil mask in its colour; null when it can't be decoded.</summary>
    internal static SKBitmap? Read(PdfPaintedImage image)
    {
        if (image.Own is { } own)
            return own.Decode(image.MaskColor);
        if (image.Image is not { } source || source.WidthInSamples <= 0 || source.HeightInSamples <= 0
            || (long)source.WidthInSamples * source.HeightInSamples > PdfDocumentReader.MaxGraphicsPixels)
            return null;
        if (source.IsImageMask)
            return PdfSlideRaster.Stencil(source, image.MaskColor);
        try
        {
            return source.TryGetPng(out var png) ? SKBitmap.Decode(png) : SKBitmap.Decode(source.RawMemory.ToArray());
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return null; // not decoded: left out
        }
    }

    /// <summary>The glyph's outline in its text space (the caller disposes it); null when it has none.</summary>
    internal static SKPath? Shape(PdfPaintedGlyph glyph)
    {
        IReadOnlyList<PdfSubpath>? subpaths = null;
        try
        {
            if (!glyph.Font.TryGetNormalisedPath(glyph.Code, out subpaths))
                subpaths = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // A broken font program: an installed font draws the letter.
        }
        if (subpaths is { Count: > 0 } && Path(subpaths) is { IsEmpty: false } path)
            return path;
        return Installed(glyph);
    }

    private static SKPath? Path(IReadOnlyList<PdfSubpath> subpaths)
    {
        var path = new SKPath { FillType = SKPathFillType.Winding };
        foreach (var subpath in subpaths)
        {
            var started = false;
            foreach (var command in subpath.Commands)
            {
                switch (command)
                {
                    case PdfSubpath.Move move:
                        path.MoveTo(Point(move.Location));
                        started = true;
                        break;
                    case PdfSubpath.Line line:
                        if (!started)
                            path.MoveTo(Point(line.From));
                        path.LineTo(Point(line.To));
                        started = true;
                        break;
                    case PdfSubpath.CubicBezierCurve cubic:
                        if (!started)
                            path.MoveTo(Point(cubic.StartPoint));
                        path.CubicTo(Point(cubic.FirstControlPoint), Point(cubic.SecondControlPoint), Point(cubic.EndPoint));
                        started = true;
                        break;
                    case PdfSubpath.QuadraticBezierCurve quad:
                        if (!started)
                            path.MoveTo(Point(quad.StartPoint));
                        path.QuadTo(Point(quad.ControlPoint), Point(quad.EndPoint));
                        started = true;
                        break;
                    case PdfSubpath.Close:
                        path.Close();
                        break;
                }
            }
        }
        if (path.Bounds is { Width: < 1e4f, Height: < 1e4f })
            return path;
        path.Dispose();
        return null; // absurd outlines (a damaged font): left out
    }

    private static SKPoint Point(PdfPoint point) => new((float)point.X, (float)point.Y);

    /// <summary>
    /// The glyph's characters in an installed font of the same kind, in text space (an em of 1, y up). Several (a
    /// ligature) are set side by side and squeezed or stretched into the glyph's advance.
    /// </summary>
    private static SKPath? Installed(PdfPaintedGlyph glyph)
    {
        if (string.IsNullOrEmpty(glyph.Unicode) || string.IsNullOrWhiteSpace(glyph.Unicode))
            return null;
        if (glyph.Unicode.Length > 32)
            return null; // not one glyph's text
        var name = glyph.Font.Name?.Data ?? "";
        var plus = name.IndexOf('+', StringComparison.Ordinal);
        if (plus is > 0 and <= 7)
            name = name[(plus + 1)..];
        var family = name.StartsWith("Times", StringComparison.OrdinalIgnoreCase) ? "Times New Roman"
            : name.StartsWith("Courier", StringComparison.OrdinalIgnoreCase) ? "Courier New"
            : name.StartsWith("Helvetica", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Arial", StringComparison.OrdinalIgnoreCase) ? "Arial"
            : name.Split('-', ',')[0];
        var details = glyph.Font.Details;
        var bold = details?.IsBold == true || name.Contains("Bold", StringComparison.OrdinalIgnoreCase);
        var italic = details?.IsItalic == true || name.Contains("Italic", StringComparison.OrdinalIgnoreCase) || name.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
        var primary = CadFonts.Resolve(family, bold, italic);
        var text = glyph.Unicode;
        // A ligature character the font lacks (U+FB01 "fi") is drawn as the letters it stands for.
        if (System.Text.Rune.DecodeFromUtf16(text, out var single, out var used) == System.Buffers.OperationStatus.Done && used == text.Length
            && !primary.ContainsGlyph(single.Value) && text.Normalize(System.Text.NormalizationForm.FormKC) is { Length: > 0 } letters && letters != text)
            text = letters;
        var runs = CadFonts.Layout(text, primary);
        if (runs.Count == 0)
            return null;
        var outline = new SKPath { FillType = SKPathFillType.Winding };
        var x = 0f;
        foreach (var run in runs)
        {
            using var font = new SKFont(run.Typeface, 1000) { Hinting = SKFontHinting.None, Subpixel = true };
            var glyphs = font.GetGlyphs(run.Text);
            var widths = font.GetGlyphWidths(glyphs);
            for (var i = 0; i < glyphs.Length; i++)
            {
                if (glyphs[i] != 0 && font.GetGlyphPath(glyphs[i]) is { } path)
                {
                    using (path)
                        outline.AddPath(path, x, 0);
                }
                x += widths[i];
            }
        }
        if (outline.IsEmpty)
        {
            outline.Dispose();
            return null;
        }
        // Skia's glyphs are y down at 1000 units an em; several characters fill the PDF's advance for the code.
        var squeeze = runs.Sum(r => r.Text.Length) > 1 && glyph.Advance > 0 && x > 0 ? Math.Clamp(glyph.Advance * 1000 / x, 0.5f, 1.5f) : 1;
        outline.Transform(SKMatrix.CreateScale(0.001f * squeeze, -0.001f));
        return outline;
    }

    public void Dispose()
    {
        foreach (var bitmap in _pictures.Values)
            bitmap?.Dispose();
        foreach (var outline in _outlines.Values)
            outline?.Dispose();
        _pictures.Clear();
        _outlines.Clear();
    }
}
