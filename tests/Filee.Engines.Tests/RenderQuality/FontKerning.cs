// How much a font itself pulls two characters together (kerning), for the overlap check: the PDF's subset fonts carry
// no kerning tables, so the pair is shaped again with HarfBuzz (Filee's Shaper) in the installed font the PDF's font
// was subset from, found by its PostScript name. A pair whose font isn't installed, or whose letters aren't one
// character each, has no kerning to allow.

using Filee.Engines.Rendering;
using Filee.Engines.Typesetting;
using SkiaSharp;
using UglyToad.PdfPig.Content;

namespace Filee.Engines.Tests.RenderQuality;

internal sealed class FontKerning : IDisposable
{
    /// <summary>Installed faces by PostScript name, read once.</summary>
    private static readonly Lazy<Dictionary<string, SKTypeface>> Installed = new(() =>
    {
        var result = new Dictionary<string, SKTypeface>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in SKFontManager.Default.FontFamilies)
        {
            using var styles = SKFontManager.Default.GetFontStyles(family);
            for (var i = 0; i < (styles?.Count ?? 0); i++)
            {
                if (styles!.CreateTypeface(i) is { PostScriptName: { Length: > 0 } name } typeface)
                    result.TryAdd(name, typeface);
            }
        }
        return result;
    });

    private readonly Shaper _shaper = new();
    private readonly Dictionary<string, PageFont?> _fonts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The kerning of <paramref name="previous"/> followed by <paramref name="next"/> in em (positive = closer), 0 if none.</summary>
    public double Of(Letter previous, Letter next)
    {
        if (!string.Equals(previous.FontName, next.FontName, StringComparison.Ordinal) || Font(previous.FontName) is not { } font)
            return 0;
        if (previous.Value.Length == 0 || next.Value.Length == 0)
            return 0;
        var apart = _shaper.Shape(font, previous.Value).Advance + _shaper.Shape(font, next.Value).Advance;
        var together = _shaper.Shape(font, previous.Value + next.Value);
        return Math.Max(0, (double)(apart - together.Advance) / together.UnitsPerEm);
    }

    /// <summary>The installed font a PDF font ("ABCDEF+Calibri-Light") was subset from.</summary>
    private PageFont? Font(string? name)
    {
        if (name is null)
            return null;
        var postScript = name.Contains('+') ? name[(name.IndexOf('+') + 1)..] : name;
        if (!_fonts.TryGetValue(postScript, out var font))
        {
            try
            {
                font = Installed.Value.TryGetValue(postScript, out var typeface) ? PageFont.FromTypeface(typeface) : null;
            }
            catch (InvalidDataException)
            {
                font = null; // not a TrueType or OpenType font
            }
            _fonts[postScript] = font;
        }
        return font;
    }

    public void Dispose()
    {
        _shaper.Dispose();
        foreach (var font in _fonts.Values)
            font?.Dispose();
    }
}
