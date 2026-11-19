// Places the glyphs of an XPS Glyphs element (XPS 1.0 §5.1, ECMA-388 §12.1): UnicodeString and Indices, whose entries
// are "[(chars:glyphs)][glyph][,advance[,uOffset[,vOffset]]]" separated by ";". A missing glyph number is the font's
// glyph for the character (cmap), a missing advance the font's own (hmtx); advances and offsets are hundredths of
// FontRenderingEmSize. A cluster map "(2:1)" makes the next glyphs one cluster of that many UTF-16 code units, which
// the PDF's text keeps; characters after the last entry get a glyph each. An odd BidiLevel runs right to left:
// advances go leftwards from OriginX. vOffset moves a glyph up.

using System.Globalization;
using SkiaSharp;

namespace Filee.Engines.Xps;

internal static class XpsGlyphLayout
{
    /// <summary>Glyphs, their baseline origins and the index in the text where each one's cluster starts.</summary>
    public readonly record struct Layout(ushort[] Glyphs, SKPoint[] Positions, int[] Clusters);

    private struct Entry
    {
        public ushort Glyph;
        public float? Advance;
        public float OffsetX;
        public float OffsetY;
        public int Cluster;
    }

    public static Layout Lay(SKTypeface typeface, float size, SKPoint origin, string text, string? indices, bool rightToLeft)
    {
        var glyphCount = typeface.GlyphCount;
        var entries = new List<Entry>();
        var at = 0; // the code unit the next cluster starts at
        var clusterStart = 0;
        var clusterLength = 0;
        var glyphsLeft = 0;
        foreach (var raw in string.IsNullOrEmpty(indices) ? [] : indices.Split(';'))
        {
            var item = raw.Trim();
            if (item.StartsWith('(') && item.IndexOf(')') is var close and > 0)
            {
                var map = item[1..close].Split(':');
                var chars = Count(map[0]);
                var glyphs = map.Length > 1 ? Count(map[1]) : 1;
                clusterStart = at;
                clusterLength = Math.Max(1, chars);
                glyphsLeft = Math.Max(1, glyphs);
                item = item[(close + 1)..];
            }
            else if (glyphsLeft == 0)
            {
                clusterStart = at;
                clusterLength = at < text.Length && char.IsSurrogatePair(text, at) ? 2 : 1;
                glyphsLeft = 1;
            }
            var fields = item.Split(',');
            var entry = new Entry { Cluster = Math.Min(clusterStart, text.Length) };
            entry.Glyph = fields[0].Trim() is { Length: > 0 } number && ushort.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var glyph)
                ? glyph
                : Cmap(typeface, text, clusterStart);
            entry.Advance = fields.Length > 1 ? Hundredths(fields[1], size) : null;
            entry.OffsetX = fields.Length > 2 ? Hundredths(fields[2], size) ?? 0 : 0;
            entry.OffsetY = fields.Length > 3 ? Hundredths(fields[3], size) ?? 0 : 0;
            if (entry.Glyph >= glyphCount)
                entry.Glyph = 0;
            entries.Add(entry);
            if (--glyphsLeft == 0)
                at = clusterStart + clusterLength;
        }
        // Characters no entry covers: a glyph each, from the cmap.
        while (at < text.Length)
        {
            var length = char.IsSurrogatePair(text, at) ? 2 : 1;
            entries.Add(new Entry { Glyph = Cmap(typeface, text, at), Cluster = at });
            at += length;
        }

        var glyphsOut = entries.Select(e => e.Glyph).ToArray();
        using var font = new SKFont(typeface, size) { Subpixel = true, Hinting = SKFontHinting.None, LinearMetrics = true };
        var widths = font.GetGlyphWidths(glyphsOut);
        var positions = new SKPoint[entries.Count];
        var pen = origin.X;
        for (var i = 0; i < entries.Count; i++)
        {
            var advance = entries[i].Advance ?? (i < widths.Length ? widths[i] : 0);
            if (rightToLeft)
            {
                pen -= advance;
                positions[i] = new SKPoint(pen - entries[i].OffsetX, origin.Y - entries[i].OffsetY);
            }
            else
            {
                positions[i] = new SKPoint(pen + entries[i].OffsetX, origin.Y - entries[i].OffsetY);
                pen += advance;
            }
        }
        return new Layout(glyphsOut, positions, [.. entries.Select(e => e.Cluster)]);
    }

    private static int Count(string text) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? Math.Min(count, 1 << 20) : 1;

    private static float? Hundredths(string text, float size) =>
        text.Trim().Length > 0 && XpsValues.Number(text) is { } value ? value * size / 100 : null;

    /// <summary>The font's glyph for the character at <paramref name="index"/>; 0 past the text.</summary>
    private static ushort Cmap(SKTypeface typeface, string text, int index)
    {
        if (index >= text.Length)
            return 0;
        var codePoint = char.IsSurrogatePair(text, index) ? char.ConvertToUtf32(text, index) : text[index];
        return typeface.GetGlyph(codePoint);
    }
}
