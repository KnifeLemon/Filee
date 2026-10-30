// A font subsetter for PDF output: keeps the outlines of the glyphs a document uses and empties the others, without
// renumbering them, so glyph IDs from shaping stay valid against the subset (a "glyph-ID-preserving" subset). The
// glyph count is cut to the highest used glyph + 1, and the tables that only describe dropped glyphs shrink to almost
// nothing once a PDF writer deflates the font.
//
// What the subset keeps: glyf / loca (the used glyphs and the components of composite ones, hinting instructions
// included), head, hhea / hmtx (advances of the used glyphs), maxp, OS/2, a cmap reduced to the used glyphs (PDF
// writers build ToUnicode from it), post without glyph names, name records 0–6, and the hinting tables cvt / fpgm / prep
// / gasp (some CJK fonts build their glyphs with hinting instructions). Layout tables (GSUB, GPOS, GDEF, kern, ...)
// are dropped: the text is already shaped. Vertical metrics and embedded bitmaps are dropped too.
//
// CFF fonts are converted to TrueType outlines first (only the used glyphs, CffToTrueType), so the result is always a
// TrueType font. Variable TrueType fonts lose their variation tables: the subset is the default instance, which is
// what glyf holds. Fonts the subsetter can't handle (CFF2 outlines, colour and bitmap-only fonts) are reported by
// CanSubset.

using System.Buffers.Binary;

namespace Filee.Engines.Fonts;

/// <summary>Glyph-ID-preserving TrueType subsetting.</summary>
internal static class FontSubsetter
{
    /// <summary>Tables copied unchanged into the subset (when the font has them).</summary>
    private static readonly string[] KeptTables = ["OS/2", "cvt ", "fpgm", "prep", "gasp"];

    /// <summary>Tables whose glyphs are pictures (colour or bitmap): the subset would draw nothing for them.</summary>
    private static readonly string[] ColorTables = ["COLR", "CBDT", "sbix", "SVG "];

    /// <summary>name IDs kept: copyright, family, subfamily, unique ID, full name, version, PostScript name.</summary>
    private const int LastKeptNameId = 6;

    /// <summary>
    /// True when <see cref="Subset"/> handles the font; otherwise <paramref name="reason"/> says why (a PDF writer then
    /// draws the text another way).
    /// </summary>
    public static bool CanSubset(SfntFont font, out string? reason)
    {
        if (font["CFF2"] is not null)
            reason = "CFF2 (variable PostScript) outlines can't be subset yet.";
        else if (ColorTables.Any(font.Tables.ContainsKey))
            reason = "The font draws its glyphs as colour layers or pictures.";
        else if (font["glyf"] is null && font["CFF "] is null)
            reason = "The font has no glyph outlines.";
        else if (font["glyf"] is not null && font["loca"] is null)
            reason = "The font has glyph outlines without a 'loca' table.";
        else if (font["head"] is null || font["hhea"] is null || font["hmtx"] is null || font["maxp"] is null)
            reason = "The font lacks a required table (head, hhea, hmtx or maxp).";
        else
            reason = null;
        return reason is null;
    }

    /// <summary>
    /// A TrueType font with the outlines of <paramref name="glyphs"/> (plus .notdef and the components they use) at
    /// their original glyph IDs and every other glyph empty.
    /// </summary>
    /// <param name="codePoints">
    /// The characters drawn with the font, or null: the cmap then keeps only these, so a glyph several characters share
    /// (文 and the Kangxi radical ⽂ in CJK fonts) maps back to the character the text has, not the lowest one.
    /// </param>
    /// <exception cref="NotSupportedException">The font is one <see cref="CanSubset"/> refuses.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A glyph ID is outside the font.</exception>
    public static SfntFont Subset(SfntFont font, IEnumerable<int> glyphs, CancellationToken cancellationToken = default,
        IReadOnlySet<int>? codePoints = null)
    {
        if (!CanSubset(font, out var reason))
            throw new NotSupportedException(reason);
        var glyphCount = font.GlyphCount;
        var wanted = new HashSet<int> { 0 };
        foreach (var glyph in glyphs)
        {
            if (glyph < 0 || glyph >= glyphCount)
                throw new ArgumentOutOfRangeException(nameof(glyphs), glyph, $"The font has {glyphCount} glyphs.");
            wanted.Add(glyph);
        }

        var source = font["glyf"] is null ? CffToTrueType.Convert(font, cancellationToken, only: wanted) : font;
        cancellationToken.ThrowIfCancellationRequested();

        var glyf = source.Require("glyf");
        var head = source.Require("head");
        var longLoca = FontTables.IndexToLocFormat(head) != 0;
        var loca = GlyfTable.ReadLoca(source.Require("loca"), glyphCount, longLoca, glyf.Length);
        var used = Closure(glyf, loca, wanted);
        var count = used.Max() + 1;

        var outlines = new byte[count][];
        for (var gid = 0; gid < count; gid++)
            outlines[gid] = used.Contains(gid) ? glyf.AsSpan(loca[gid].Offset, loca[gid].Length).ToArray() : [];
        var (newGlyf, newLoca, newLongLoca) = GlyfTable.Build(outlines, longLoca);

        var subset = new SfntFont(SfntFont.TrueTypeFlavor);
        subset.Tables["glyf"] = newGlyf;
        subset.Tables["loca"] = newLoca;
        subset.Tables["head"] = FontTables.WithIndexToLocFormat(head, newLongLoca);
        subset.Tables["maxp"] = Maxp(source.Require("maxp"), count);
        (subset.Tables["hhea"], subset.Tables["hmtx"]) = HorizontalMetrics(source.Require("hhea"), source.Require("hmtx"), used, count);
        subset.Tables["post"] = Post(source["post"]);
        if (source["cmap"] is { } cmap)
        {
            var map = CmapTable.Read(cmap, out var symbol);
            subset.Tables["cmap"] = CmapTable.Build(
                map.Where(p => used.Contains(p.Value) && (codePoints is null || codePoints.Contains(p.Key))).ToDictionary(), symbol);
        }
        else
        {
            subset.Tables["cmap"] = CmapTable.Build(new Dictionary<int, int>());
        }
        if (source["name"] is { } name)
            subset.Tables["name"] = Name(name);
        foreach (var tag in KeptTables)
        {
            if (source[tag] is { } table)
                subset.Tables[tag] = table;
        }
        return subset;
    }

    /// <summary>The wanted glyphs plus every glyph their composite glyphs are built from, at any depth.</summary>
    private static HashSet<int> Closure(byte[] glyf, (int Offset, int Length)[] loca, HashSet<int> wanted)
    {
        var used = new HashSet<int>(wanted);
        var pending = new Stack<int>(wanted);
        while (pending.Count > 0)
        {
            var (offset, length) = loca[pending.Pop()];
            if (length < 10 || BinaryPrimitives.ReadInt16BigEndian(glyf.AsSpan(offset)) >= 0)
                continue;
            foreach (var component in Components(glyf, offset + 10, offset + length))
            {
                if (component < loca.Length && used.Add(component))
                    pending.Push(component);
            }
        }
        return used;
    }

    /// <summary>The glyph IDs of a composite glyph's components (its records start at <paramref name="start"/>).</summary>
    private static List<int> Components(byte[] glyf, int start, int end)
    {
        var r = new BigEndianReader(glyf, start, end);
        var result = new List<int>();
        ushort flags;
        do
        {
            flags = r.U16();
            result.Add(r.U16());
            r.Skip((flags & TrueTypeGlyph.ArgsAreWords) != 0 ? 4 : 2);
            if ((flags & TrueTypeGlyph.HasScale) != 0)
                r.Skip(2);
            else if ((flags & TrueTypeGlyph.HasXYScale) != 0)
                r.Skip(4);
            else if ((flags & TrueTypeGlyph.HasTwoByTwo) != 0)
                r.Skip(8);
        }
        while ((flags & TrueTypeGlyph.MoreComponents) != 0);
        return result;
    }

    private static byte[] Maxp(byte[] maxp, int count)
    {
        var copy = (byte[])maxp.Clone();
        BinaryPrimitives.WriteUInt16BigEndian(copy.AsSpan(4), (ushort)count);
        return copy;
    }

    /// <summary>
    /// hhea and hmtx for <paramref name="count"/> glyphs, all with a full metric: the used glyphs keep their advance
    /// and side bearing, the others get zeros (which deflate to almost nothing).
    /// </summary>
    private static (byte[] Hhea, byte[] Hmtx) HorizontalMetrics(byte[] hhea, byte[] hmtx, HashSet<int> used, int count)
    {
        var metrics = FontTables.NumberOfMetrics(hhea);
        if (metrics == 0)
            throw new InvalidDataException("The font's horizontal metrics are damaged.");
        var r = new BigEndianReader(hmtx);
        var w = new BigEndianWriter(count * 4);
        var advance = 0;
        for (var gid = 0; gid < count; gid++)
        {
            int bearing;
            if (gid < metrics)
            {
                advance = r.U16();
                bearing = r.I16();
            }
            else
            {
                bearing = r.Remaining >= 2 ? r.I16() : 0;
            }
            var keep = used.Contains(gid);
            w.U16(keep ? advance : 0);
            w.I16(keep ? bearing : 0);
        }
        var header = (byte[])hhea.Clone();
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(34), (ushort)count);
        return (header, w.ToArray());
    }

    /// <summary>post format 3 (no glyph names) with the original header (italic angle, underline, fixed pitch).</summary>
    private static byte[] Post(byte[]? post)
    {
        var result = new byte[32];
        post?.AsSpan(0, Math.Min(32, post.Length)).CopyTo(result);
        BinaryPrimitives.WriteUInt32BigEndian(result, 0x00030000);
        return result;
    }

    /// <summary>The name table with only the records that identify the font (IDs 0–6), as format 0.</summary>
    private static byte[] Name(byte[] name)
    {
        var r = new BigEndianReader(name);
        r.Skip(2); // format
        int count = r.U16();
        int storage = r.U16();
        var records = new List<(int Platform, int Encoding, int Language, int Id, byte[] Text)>();
        for (var i = 0; i < count; i++)
        {
            int platform = r.U16(), encoding = r.U16(), language = r.U16(), id = r.U16(), length = r.U16(), offset = r.U16();
            if (id > LastKeptNameId || language >= 0x8000 || storage + offset + length > name.Length)
                continue;
            records.Add((platform, encoding, language, id, name.AsSpan(storage + offset, length).ToArray()));
        }
        records.Sort((a, b) => (a.Platform, a.Encoding, a.Language, a.Id).CompareTo((b.Platform, b.Encoding, b.Language, b.Id)));

        var w = new BigEndianWriter(6 + records.Sum(x => 12 + x.Text.Length));
        w.U16(0);
        w.U16(records.Count);
        w.U16(6 + 12 * records.Count);
        var position = 0;
        foreach (var record in records)
        {
            w.U16(record.Platform);
            w.U16(record.Encoding);
            w.U16(record.Language);
            w.U16(record.Id);
            w.U16(record.Text.Length);
            w.U16(position);
            position += record.Text.Length;
        }
        foreach (var record in records)
            w.Bytes(record.Text);
        return w.ToArray();
    }
}
