// Places the glyphs of an XPS Glyphs element (XPS 1.0 §5.1, ECMA-388 §12.1): UnicodeString and Indices, whose entries
// are "[(chars:glyphs)][glyph][,advance[,uOffset[,vOffset]]]" separated by ";". A missing glyph number is the font's
// glyph for the character (cmap), a missing advance the font's own (hmtx); advances and offsets are hundredths of
// FontRenderingEmSize. A cluster map "(2:1)" makes the next glyphs one cluster of that many UTF-16 code units, which
// the PDF's text keeps; characters after the last entry get a glyph each. An odd BidiLevel runs right to left:
// advances go leftwards from OriginX. vOffset moves a glyph up.
//
// IsSideways (ECMA-388 §12.1.6): each glyph is turned 90° counter-clockwise with its top centre on the pen position, and
// its default advance is its vertical one: vmtx's advance height when the font has vertical metrics, else the OS/2
// typographic ascender plus descender (hhea's when OS/2 is short). The top is the glyph's yMax plus its vmtx top side
// bearing, or that ascender. Positions of a sideways run are given in the turned frame (XpsGlyphs.SidewaysTurn).
//
// Style simulations (ECMA-388 §12.1.5, M5.13 / M5.14): an emboldened glyph's own advance (hmtx, or vmtx sideways) grows
// by 2 % of the em; given advances stay. A sideways italic is skewed about its sideways origin (its top centre), not
// about its baseline: drawn with the skew XpsGlyphs.Slant gives about the baseline, each such glyph moves along the
// run by the slant times its top, so its top stays on the origin.

using System.Buffers.Binary;
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

    /// <summary>What an emboldened glyph's own advance grows by, in ems (M5.13).</summary>
    private const float BoldAdvance = 0.02f;

    public static Layout Lay(SKTypeface typeface, float size, SKPoint origin, string text, string? indices, bool rightToLeft, bool sideways = false,
        bool bold = false, bool italic = false)
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
        if (sideways)
        {
            Sideways(typeface, font, glyphsOut, widths, entries, origin, positions, bold ? BoldAdvance * size : 0, italic ? XpsGlyphs.SidewaysSlant : 0);
            return new Layout(glyphsOut, positions, [.. entries.Select(e => e.Cluster)]);
        }
        var pen = origin.X;
        for (var i = 0; i < entries.Count; i++)
        {
            var advance = entries[i].Advance ?? (i < widths.Length ? widths[i] : 0) + (bold ? BoldAdvance * size : 0);
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

    /// <summary>
    /// Sideways glyphs left to right, their baseline origins in the turned frame; <paramref name="grow"/> is added to the
    /// font's own advances (bold), <paramref name="slant"/> is the italic skew they are drawn with (0: upright).
    /// </summary>
    private static void Sideways(SKTypeface typeface, SKFont font, ushort[] glyphs, float[] widths, List<Entry> entries, SKPoint origin, SKPoint[] positions,
        float grow, float slant)
    {
        var metrics = VerticalMetrics.Of(typeface);
        var scale = font.Size / metrics.UnitsPerEm;
        var bounds = new SKRect[glyphs.Length];
        font.GetGlyphWidths(glyphs, new float[glyphs.Length], bounds);
        var pen = origin.X;
        for (var i = 0; i < entries.Count; i++)
        {
            float top, advance;
            if (metrics.Vertical(glyphs[i]) is var (height, topBearing))
            {
                // Skia's bounds are y downwards: their top is −yMax.
                top = -bounds[i].Top + topBearing * scale;
                advance = height * scale;
            }
            else
            {
                top = metrics.Ascender * scale;
                advance = (metrics.Ascender + metrics.Descender) * scale;
            }
            advance = entries[i].Advance ?? advance + grow;
            var nominal = new SKPoint(pen + entries[i].OffsetX, origin.Y - entries[i].OffsetY);
            // Turned counter-clockwise, the glyph's top centre (half its advance across, its top up) lands on the
            // nominal origin; in the turned frame (x, y) = (−y, x) of that baseline origin.
            var baseline = new SKPoint(nominal.X + top, nominal.Y + (i < widths.Length ? widths[i] : 0) / 2);
            // The skew about the baseline moves the top by −slant × top across the turned frame: moved back, the top
            // stays on the origin and the rest of the glyph leans from there (M5.14's skew of the sideways glyph).
            positions[i] = new SKPoint(-baseline.Y + slant * top, baseline.X);
            pen += advance;
        }
    }

    /// <summary>What a font says about its glyphs set sideways (vhea / vmtx, OS/2, hhea), in font units.</summary>
    private sealed record VerticalMetrics(int UnitsPerEm, float Ascender, float Descender, byte[]? Vmtx, int LongMetrics)
    {
        public static VerticalMetrics Of(SKTypeface typeface)
        {
            var head = Table(typeface, "head");
            var unitsPerEm = head is { Length: >= 20 } ? BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(18)) : 0;
            if (unitsPerEm is < 16 or > 16384)
                unitsPerEm = 1000;
            float ascender, descender;
            if (Table(typeface, "OS/2") is { Length: >= 78 } os2)
            {
                ascender = BinaryPrimitives.ReadInt16BigEndian(os2.AsSpan(68));
                descender = Math.Abs((int)BinaryPrimitives.ReadInt16BigEndian(os2.AsSpan(70)));
            }
            else if (Table(typeface, "hhea") is { Length: >= 8 } hhea)
            {
                ascender = BinaryPrimitives.ReadInt16BigEndian(hhea.AsSpan(4));
                descender = Math.Abs((int)BinaryPrimitives.ReadInt16BigEndian(hhea.AsSpan(6)));
            }
            else
            {
                (ascender, descender) = (unitsPerEm * 0.88f, unitsPerEm * 0.12f);
            }
            var vhea = Table(typeface, "vhea");
            var vmtx = Table(typeface, "vmtx");
            var longMetrics = vhea is { Length: >= 36 } ? BinaryPrimitives.ReadUInt16BigEndian(vhea.AsSpan(34)) : 0;
            if (vmtx is null || longMetrics == 0 || vmtx.Length < longMetrics * 4)
                (vmtx, longMetrics) = (null, 0);
            return new VerticalMetrics(unitsPerEm, ascender, descender, vmtx, longMetrics);
        }

        /// <summary>A glyph's advance height and top side bearing; null when the font has no vertical metrics.</summary>
        public (float Height, float TopBearing)? Vertical(ushort glyph)
        {
            if (Vmtx is not { } vmtx)
                return null;
            var metric = Math.Min(glyph, LongMetrics - 1);
            var height = BinaryPrimitives.ReadUInt16BigEndian(vmtx.AsSpan(metric * 4));
            // Glyphs past the long metrics share the last advance and have a bearing of their own after them.
            var at = glyph < LongMetrics ? glyph * 4 + 2 : LongMetrics * 4 + (glyph - LongMetrics) * 2;
            var bearing = at + 2 <= vmtx.Length ? BinaryPrimitives.ReadInt16BigEndian(vmtx.AsSpan(at)) : 0;
            return (height, bearing);
        }

        private static byte[]? Table(SKTypeface typeface, string tag)
        {
            var value = (uint)(tag[0] << 24 | tag[1] << 16 | tag[2] << 8 | tag[3]);
            return typeface.TryGetTableData(value, out var data) ? data : null;
        }
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
