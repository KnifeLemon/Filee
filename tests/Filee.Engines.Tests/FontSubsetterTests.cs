// The glyph-ID-preserving font subsetter (Fonts/FontSubsetter.cs) that the Filee renderer's PDF output embeds fonts
// with, and the character map code it uses (Fonts/CmapTable.cs). Inputs are the small OFL test fonts in Fonts/ (see
// FontTests) and fonts changed in code.

using Filee.Engines.Fonts;
using Filee.Engines.Fonts.Cff;
using SkiaSharp;

namespace Filee.Engines.Tests;

public class FontSubsetterTests
{
    private const string TrueType = "FileeTest-TrueType.ttf";
    private const string CidKeyed = "FileeTest-CID.otf";
    private const string NameKeyed = "FileeTest-NameKeyed.otf";

    private static SfntFont Load(string name) => FontFile.Load(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", name)));

    private static SKTypeface Typeface(SfntFont font) => SKTypeface.FromData(SKData.CreateCopy(font.Write()))!;

    private static ushort[] Glyphs(SKTypeface typeface, string text)
    {
        using var font = new SKFont(typeface, 100);
        return font.GetGlyphs(text);
    }

    [Fact]
    public void TrueType_subset_keeps_used_glyphs_at_their_ids_and_empties_the_rest()
    {
        var source = Load(TrueType);
        using var original = Typeface(source);
        var used = Glyphs(original, "Hi").Select(g => (int)g).ToArray();

        var subset = FontSubsetter.Subset(source, used, TestContext.Current.CancellationToken);
        using var typeface = Typeface(subset);

        Assert.Equal(SfntFont.TrueTypeFlavor, subset.Flavor);
        Assert.Equal(used.Max() + 1, subset.GlyphCount);
        var glyf = subset.Require("glyf");
        var loca = GlyfTable.ReadLoca(subset.Require("loca"), subset.GlyphCount, FontTables.IndexToLocFormat(subset.Require("head")) != 0, glyf.Length);
        var sourceGlyf = source.Require("glyf");
        var sourceLoca = GlyfTable.ReadLoca(source.Require("loca"), source.GlyphCount, FontTables.IndexToLocFormat(source.Require("head")) != 0, sourceGlyf.Length);
        for (var gid = 0; gid < subset.GlyphCount; gid++)
        {
            if (gid == 0 || used.Contains(gid))
            {
                // The same bytes, padded to four.
                var expected = sourceGlyf.AsSpan(sourceLoca[gid].Offset, sourceLoca[gid].Length);
                var actual = glyf.AsSpan(loca[gid].Offset, loca[gid].Length);
                Assert.True(actual.StartsWith(expected) && !actual[expected.Length..].ContainsAnyExcept((byte)0), $"glyph {gid}");
            }
            else
                Assert.Equal(0, loca[gid].Length);
        }

        // Skia draws the same outlines and advances from the subset, and its cmap knows only the kept characters.
        using var a = new SKFont(original, 100);
        using var b = new SKFont(typeface, 100);
        foreach (var gid in used)
        {
            using var expected = a.GetGlyphPath((ushort)gid);
            using var actual = b.GetGlyphPath((ushort)gid);
            Assert.Equal(expected.ToSvgPathData(), actual.ToSvgPathData());
            Assert.Equal(a.GetGlyphWidths([(ushort)gid])[0], b.GetGlyphWidths([(ushort)gid])[0]);
        }
        Assert.Equal(used.Select(g => (ushort)g), Glyphs(typeface, "Hi"));
        Assert.Equal(0, Glyphs(typeface, "F")[0]);
    }

    [Fact]
    public void Composite_glyphs_bring_their_components()
    {
        var source = Load(TrueType);
        using var original = Typeface(source);
        var aacute = Glyphs(original, "Á")[0];
        var components = ComponentsOf(source, aacute);
        Assert.NotEmpty(components);

        var subset = FontSubsetter.Subset(source, [(int)aacute], TestContext.Current.CancellationToken);
        using var typeface = Typeface(subset);

        var glyf = subset.Require("glyf");
        var loca = GlyfTable.ReadLoca(subset.Require("loca"), subset.GlyphCount, FontTables.IndexToLocFormat(subset.Require("head")) != 0, glyf.Length);
        Assert.All(components, c => Assert.True(loca[c].Length > 0, $"component {c} kept"));
        using var a = new SKFont(original, 100);
        using var b = new SKFont(typeface, 100);
        using var expected = a.GetGlyphPath(aacute);
        using var actual = b.GetGlyphPath(aacute);
        Assert.Equal(expected.ToSvgPathData(), actual.ToSvgPathData());
    }

    [Fact]
    public void A_glyph_two_characters_share_maps_back_to_the_one_the_text_has()
    {
        // As CJK fonts map 文 and the Kangxi radical ⽂ to one glyph: a PDF's ToUnicode would take the lower code point.
        var source = Load(TrueType);
        var map = CmapTable.Read(source.Require("cmap"), out var symbol);
        var h = map['H'];
        map[0x2F42] = h;
        source.Tables["cmap"] = CmapTable.Build(map, symbol);

        var all = CmapTable.Read(FontSubsetter.Subset(source, [h], TestContext.Current.CancellationToken).Require("cmap"), out _);
        var text = CmapTable.Read(FontSubsetter.Subset(source, [h], TestContext.Current.CancellationToken, codePoints: new HashSet<int> { 'H' })
            .Require("cmap"), out _);

        Assert.Equal(h, all[0x2F42]);
        Assert.Equal(new Dictionary<int, int> { ['H'] = h }, text);
    }

    [Fact]
    public void Subset_keeps_only_what_a_pdf_needs()
    {
        var source = Load(TrueType);
        source.Tables["fvar"] = [0, 1, 0, 0];
        source.Tables["gvar"] = [0, 1, 0, 0];
        source.Tables["DSIG"] = [0, 0, 0, 1];

        var subset = FontSubsetter.Subset(source, [1], TestContext.Current.CancellationToken);

        Assert.Equal(
            new[] { "OS/2", "cmap", "fpgm", "gasp", "glyf", "head", "hhea", "hmtx", "loca", "maxp", "name", "post", "prep", "cvt " }.Where(t => source[t] is not null).Order(StringComparer.Ordinal),
            subset.Tables.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(0x00030000u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(subset.Require("post")));
        // The names that identify the font are kept.
        using var typeface = Typeface(subset);
        Assert.Equal("Filee Test TrueType", typeface.FamilyName);
        Assert.True(subset.Require("name").Length < source.Require("name").Length);
    }

    [Theory]
    [InlineData(CidKeyed, "한글 Filee")]
    [InlineData(NameKeyed, "Aae")]
    public void Cff_font_becomes_a_truetype_subset_with_the_same_shapes(string sample, string text)
    {
        var source = Load(sample);
        using var original = Typeface(source);
        var used = Glyphs(original, text).Distinct().ToArray();

        var subset = FontSubsetter.Subset(source, used.Select(g => (int)g), TestContext.Current.CancellationToken);
        using var typeface = Typeface(subset);

        Assert.Equal(SfntFont.TrueTypeFlavor, subset.Flavor);
        Assert.Null(subset["CFF "]);
        Assert.Equal(used.Max() + 1, subset.GlyphCount);
        using var a = new SKFont(original, 1000);
        using var b = new SKFont(typeface, 1000);
        foreach (var gid in used.Where(g => g != 0))
        {
            using var expected = a.GetGlyphPath(gid);
            using var actual = b.GetGlyphPath(gid);
            // CoreText gives no outline for the subset of a CID-keyed CFF font yet (open on the Project 4 board).
            Assert.SkipWhen(actual is null && sample == CidKeyed && OperatingSystem.IsMacOS(),
                "macOS: CoreText has no outline for the TrueType subset of a CID-keyed CFF font yet");
            Assert.True(actual is not null, $"glyph {gid}: the subset has no outline for it");
            Assert.Equal(a.GetGlyphWidths([gid])[0], b.GetGlyphWidths([gid])[0]);
            // CoreText (macOS) may give no outline for a CID-keyed CFF font read from memory. Filee never draws with
            // that font (the subset is TrueType), so only the subset's outline is required there.
            if (expected is null)
                continue;
            // Quadratic approximations of the cubic curves: the same box within a font unit or two.
            var (e, r) = (expected.Bounds, actual.Bounds);
            Assert.True(Math.Abs(e.Left - r.Left) < 3 && Math.Abs(e.Top - r.Top) < 3 && Math.Abs(e.Right - r.Right) < 3 && Math.Abs(e.Bottom - r.Bottom) < 3,
                $"glyph {gid}: {e} vs {r}");
        }
    }

    [Fact]
    public void Fonts_that_cant_be_subset_say_why()
    {
        var cff2 = Load(CidKeyed);
        cff2.Tables["CFF2"] = cff2.Require("CFF ");
        cff2.Tables.Remove("CFF ");
        var color = Load(TrueType);
        color.Tables["COLR"] = new byte[14];

        Assert.False(FontSubsetter.CanSubset(cff2, out var reason));
        Assert.Contains("CFF2", reason, StringComparison.Ordinal);
        Assert.False(FontSubsetter.CanSubset(color, out _));
        Assert.Throws<NotSupportedException>(() => FontSubsetter.Subset(cff2, [1], TestContext.Current.CancellationToken));
        Assert.True(FontSubsetter.CanSubset(Load(TrueType), out _));
    }

    [Fact]
    public void Glyph_ids_outside_the_font_are_refused()
    {
        var font = Load(TrueType);

        Assert.Throws<ArgumentOutOfRangeException>(() => FontSubsetter.Subset(font, [font.GlyphCount], TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentOutOfRangeException>(() => FontSubsetter.Subset(font, [-1], TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Cmap_round_trips_bmp_and_supplementary_characters()
    {
        var map = new Dictionary<int, int> { ['A'] = 3, ['B'] = 4, ['C'] = 5, ['가'] = 900, ['힣'] = 901, [0x1F600] = 77, [0x20000] = 78, [0x20001] = 79 };

        var table = CmapTable.Build(map);
        var back = CmapTable.Read(table, out var symbol);

        Assert.False(symbol);
        Assert.Equal(map.OrderBy(p => p.Key), back.OrderBy(p => p.Key));
        // BMP-only maps need no format 12 subtable.
        Assert.Single(CmapTable.Read(CmapTable.Build(new Dictionary<int, int> { ['A'] = 3 }), out _));
        Assert.Equal(1, System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(CmapTable.Build(new Dictionary<int, int> { ['A'] = 3 }).AsSpan(2)));
    }

    [Fact]
    public void Symbol_cmap_stays_a_symbol_cmap()
    {
        var table = CmapTable.Build(new Dictionary<int, int> { [0xF041] = 2 }, symbol: true);

        Assert.Equal(new Dictionary<int, int> { [0xF041] = 2 }, CmapTable.Read(table, out var symbol));
        Assert.True(symbol);
    }

    [Fact]
    public void Cmap_of_a_real_font_is_read_like_skia_reads_it()
    {
        var source = Load(TrueType);
        using var typeface = Typeface(source);
        using var font = new SKFont(typeface, 10);

        var map = CmapTable.Read(source.Require("cmap"), out _);

        Assert.NotEmpty(map);
        foreach (var (code, glyph) in map)
            Assert.Equal(glyph, font.GetGlyphs(char.ConvertFromUtf32(code))[0]);
    }

    [Fact]
    public void Cid_font_matrices_with_their_own_units_per_em_combine_like_rasterizers_do()
    {
        // Top DICT 1/2048 and a font dictionary 1/1000 (Pretendard and other fonts built that way): 1/2048 overall.
        Assert.Equal([1.0 / 2048, 0, 0, 1.0 / 2048, 0, 0], CffFont.Combine([0.001, 0, 0, 0.001, 0, 0], [1.0 / 2048, 0, 0, 1.0 / 2048, 0, 0]), new DoubleComparer());
        // Top DICT 1/1000 and an identity font dictionary (the usual CID layout).
        Assert.Equal([0.001, 0, 0, 0.001, 0, 0], CffFont.Combine([1, 0, 0, 1, 0, 0], [0.001, 0, 0, 0.001, 0, 0]), new DoubleComparer());
        // A slanted font dictionary keeps its slant.
        Assert.Equal([0.001, 0, 0.0002, 0.001, 0, 0], CffFont.Combine([0.001, 0, 0.0002, 0.001, 0, 0], [0.001, 0, 0, 0.001, 0, 0]), new DoubleComparer());
    }

    /// <summary>The components of a composite glyph (direct ones).</summary>
    private static List<int> ComponentsOf(SfntFont font, int gid)
    {
        var glyph = GlyfTable.ReadAll(font)[gid]!;
        Assert.True(glyph.IsComposite);
        var r = new BigEndianReader(glyph.Components);
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

    private sealed class DoubleComparer : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) < 1e-12;

        public int GetHashCode(double obj) => 0;
    }
}
