// Windows bitmap FON (NE + FNT v2/v3) to TTF/OTF/WOFF/WOFF2/EOT (issue #177). FonBuilder creates
// synthetic samples in memory; the expected pixels below are independent of the engine's reader.

using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Fonts;
using SkiaSharp;

namespace Filee.Engines.Tests;

public class FonTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly string[] Formats = ["ttf", "otf", "woff", "woff2", "eot"];

    private static byte[] Data(string name) => FonBuilder.Sample(name);

    private static int FntStart(byte[] data, int resource = 0)
    {
        var ne = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x3c));
        var table = ne + BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(ne + 0x24));
        var entry = table + 2 + 8 + 12 + 8 + resource * 12;
        return BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(entry));
    }

    // ───────────────────────── The generator's art, independent of the reader ─────────────────────────

    /// <summary>One synthetic single-resource sample: FNT version, fixed or proportional, first character.</summary>
    public sealed record Kind(string File, bool Fixed, int First);

    private static readonly Kind[] Kinds =
    [
        new("Fon-v2-fixed.fon", true, 32),
        new("Fon-v2-proportional.fon", false, 0),
        new("Fon-v3-fixed.fon", true, 32),
        new("Fon-v3-proportional.fon", false, 32),
    ];

    private static int ArtWidth(int code, bool isFixed) => isFixed ? 8 : code == 32 ? 0 : code % 2 == 1 ? 5 : 9;

    private static bool ArtLit(int code, int x, int y, int width, int height)
    {
        if (x >= width || code == 32)
            return false;
        var rows = Math.Min(height, 7);
        return code switch
        {
            33 => x == 2 && y == 0,
            34 => x == 2 + y % Math.Max(1, width - 2),
            _ => x >= 2 && y < rows && (x == 2 || x == width - 1 || y == 0 || y == rows - 1),
        };
    }

    private static int ArtAscent(int height) => Math.Min(height, 10);

    private static int Scale(int height) => 1024 / height;

    // ───────────────────────── Samples and queue helpers ─────────────────────────

    [Theory]
    [InlineData("Fon-v2-fixed.fon", 0x200, 8)]
    [InlineData("Fon-v2-proportional.fon", 0x200, 0)]
    [InlineData("Fon-v3-fixed.fon", 0x300, 8)]
    [InlineData("Fon-v3-proportional.fon", 0x300, 0)]
    [InlineData("Fon-multi.fon", 0x200, 8)]
    [InlineData("Fon-edge.fon", 0x300, 8)]
    public void Generated_fon_samples_have_expected_resources(string name, int version, int width)
    {
        var bytes = Data(name);
        Assert.Equal("MZ", Encoding.ASCII.GetString(bytes, 0, 2));
        var strikes = FonReader.Read(bytes, TestContext.Current.CancellationToken);
        Assert.Equal(name == "Fon-multi.fon" ? 3 : name == "Fon-edge.fon" ? 2 : 1, strikes.Count);
        Assert.Equal(version, strikes[0].Version);
        Assert.Equal(width, strikes[0].PixelWidth);
        Assert.Equal("Fixed", strikes[0].Face);
    }

    [Fact]
    public void Aligned_ne_container_keeps_resource_lengths_inside_file()
    {
        var sample = FonBuilder.V2Fixed();
        var bytes = FonBuilder.Container([Resource(sample, 0)], alignmentShift: 9);
        // FONTDIR (first entry at 0x8A) and RT_FONT (0x9E) both resolve inside the file in 512-byte units.
        foreach (var entry in new[] { 0x80 + 10, 0x80 + 30 })
        {
            var offset = BitConverter.ToUInt16(bytes, entry) << 9;
            var length = BitConverter.ToUInt16(bytes, entry + 2) << 9;
            Assert.InRange(offset, 0x100, bytes.Length - length);
        }
        var strike = Assert.Single(FonReader.Read(bytes, TestContext.Current.CancellationToken));
        Assert.Equal(0x200, strike.Version);
        Assert.Equal(13, strike.PixelHeight);
    }

    /// <summary>A folder holding <paramref name="data"/> as <c>Name.fon</c>.</summary>
    private string Source(byte[] data, out string folder)
    {
        folder = fx.NewFolder();
        var path = Path.Combine(folder, "Name.fon");
        File.WriteAllBytes(path, data);
        return path;
    }

    private static Preset PresetFor(string target, ConflictPolicy conflict = ConflictPolicy.Rename) =>
        new() { TargetFormat = target, Output = { Location = OutputLocation.SameFolder, Conflict = conflict } };

    /// <summary>Every file below <paramref name="folder"/> with the SHA-256 of its bytes.</summary>
    private static Dictionary<string, string> Snapshot(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(folder, p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));

    /// <summary>SFNT bytes Skia can read on every platform (web containers are decoded by Filee first).</summary>
    private static byte[] SfntBytes(string path, string format)
    {
        var bytes = File.ReadAllBytes(path);
        return format is "ttf" or "otf" ? bytes : FontFile.Load(bytes).Write();
    }

    // ───────────────────────── Rendering, tables ─────────────────────────

    private const int Pad = 2;

    /// <summary>
    /// The glyph drawn by Skia at its original pixel size, unhinted and aliased, with the cell's top-left at
    /// (<see cref="Pad"/>, <see cref="Pad"/>); the padding shows pixels drawn outside the cell.
    /// </summary>
    private static bool[,] Render(SKTypeface face, int gid, int height, int ascent, int width)
    {
        using var font = new SKFont(face, height) { Hinting = SKFontHinting.None, Edging = SKFontEdging.Alias, Subpixel = false };
        using var path = font.GetGlyphPath((ushort)gid) ?? new SKPath();
        var (w, h) = (width + Pad * 2, height + Pad * 2);
        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Alpha8));
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { IsAntialias = false, Color = SKColors.Black })
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Translate(Pad, Pad + ascent);
            canvas.DrawPath(path, paint);
        }
        var bytes = bitmap.Bytes;
        var mask = new bool[h, w];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                mask[y, x] = bytes[y * bitmap.RowBytes + x] > 127;
        return mask;
    }

    private static bool[,] ExpectedMask(int code, int width, int height)
    {
        var mask = new bool[height + Pad * 2, width + Pad * 2];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                mask[y + Pad, x + Pad] = ArtLit(code, x, y, width, height);
        return mask;
    }

    private static void AssertMask(bool[,] expected, bool[,] actual, string what)
    {
        Assert.Equal(expected.GetLength(0), actual.GetLength(0));
        Assert.Equal(expected.GetLength(1), actual.GetLength(1));
        for (var y = 0; y < expected.GetLength(0); y++)
            for (var x = 0; x < expected.GetLength(1); x++)
                Assert.True(expected[y, x] == actual[y, x], $"{what}: pixel ({x - Pad}, {y - Pad}) should be {(expected[y, x] ? "on" : "off")}");
    }

    private static int U16(byte[] table, int offset) => BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(offset));

    private static int I16(byte[] table, int offset) => BinaryPrimitives.ReadInt16BigEndian(table.AsSpan(offset));

    private static int Advance(SfntFont font, int gid) => U16(font.Require("hmtx"), gid * 4);

    private static int Lsb(SfntFont font, int gid) => I16(font.Require("hmtx"), gid * 4 + 2);

    /// <summary>The set bits of OS/2 ulUnicodeRange1..4.</summary>
    private static int[] UnicodeRangeBits(SfntFont font) =>
        [.. Enumerable.Range(0, 128).Where(bit =>
            (BinaryPrimitives.ReadUInt32BigEndian(font.Require("OS/2").AsSpan(42 + bit / 32 * 4)) & (1u << (bit % 32))) != 0)];

    /// <summary>The Windows Unicode BMP (3, 1) format 4 subtable as code point → glyph id.</summary>
    private static Dictionary<int, int> Cmap(SfntFont font)
    {
        var t = font.Require("cmap");
        var result = new Dictionary<int, int>();
        for (var i = 0; i < U16(t, 2); i++)
        {
            if (U16(t, 4 + i * 8) != 3 || U16(t, 6 + i * 8) != 1)
                continue;
            var at = (int)BinaryPrimitives.ReadUInt32BigEndian(t.AsSpan(8 + i * 8));
            Assert.Equal(4, U16(t, at));
            var segX2 = U16(t, at + 6);
            for (var s = 0; s < segX2 / 2; s++)
            {
                var end = U16(t, at + 14 + s * 2);
                var start = U16(t, at + 16 + segX2 + s * 2);
                var delta = U16(t, at + 16 + segX2 * 2 + s * 2);
                var rangeAt = at + 16 + segX2 * 3 + s * 2;
                var range = U16(t, rangeAt);
                for (var c = start; c <= end && c != 0xFFFF; c++)
                {
                    var gid = range == 0 ? (c + delta) & 0xFFFF : U16(t, rangeAt + range + (c - start) * 2);
                    if (range != 0 && gid != 0)
                        gid = (gid + delta) & 0xFFFF;
                    if (gid != 0)
                        result[c] = gid;
                }
            }
            return result;
        }
        throw new Xunit.Sdk.XunitException("no Windows Unicode BMP cmap");
    }

    private static string? Name(SfntFont font, int id)
    {
        var table = font.Require("name");
        var count = U16(table, 2);
        var strings = U16(table, 4);
        for (var i = 0; i < count; i++)
        {
            var offset = 6 + i * 12;
            if (U16(table, offset) != 3 || U16(table, offset + 6) != id)
                continue;
            return Encoding.BigEndianUnicode.GetString(table, strings + U16(table, offset + 10), U16(table, offset + 8));
        }
        return null;
    }

    // ───────────────────────── C1: every version and width, every target ─────────────────────────

    public static TheoryData<int, string> KindsAndFormats()
    {
        var data = new TheoryData<int, string>();
        for (var k = 0; k < Kinds.Length; k++)
            foreach (var format in Formats)
                data.Add(k, format);
        return data;
    }

    [Theory]
    [MemberData(nameof(KindsAndFormats))]
    public async Task Fon_v2_v3_each_strike_opens_in_all_five_formats(int kindIndex, string format)
    {
        var kind = Kinds[kindIndex];
        var source = Source(Data(kind.File), out var folder);
        var job = await fx.ConvertAsync([source], PresetFor(format));

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var output = Assert.Single(job.Outputs);
        Assert.Equal(Path.Combine(folder, "Name." + format), output);
        var bytes = File.ReadAllBytes(output);
        var signature = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        switch (format)
        {
            case "ttf" or "otf": Assert.Equal(0x00010000u, signature); break;
            case "woff": Assert.Equal(0x774F4646u, signature); break;
            case "woff2": Assert.Equal(0x774F4632u, signature); break;
            case "eot": Assert.Equal(bytes.Length, (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes)); break;
        }

        var font = FontFile.Load(bytes, TestContext.Current.CancellationToken);
        Assert.Equal(SfntFont.TrueTypeFlavor, font.Flavor);
        foreach (var tag in new[] { "head", "hhea", "maxp", "OS/2", "name", "cmap", "post", "glyf", "loca", "hmtx" })
            Assert.NotNull(font[tag]);
        Assert.Equal(1, U16(font.Require("maxp"), 14)); // maxZones
        Assert.Equal(2, I16(font.Require("head"), 48)); // fontDirectionHint
        var codes = Enumerable.Range(kind.First, 127 - kind.First + 1).ToArray();
        Assert.Equal(codes.Length + 1, font.GlyphCount);

        using var face = SKTypeface.FromStream(new MemoryStream(SfntBytes(output, format)));
        Assert.NotNull(face);
        Assert.Equal(codes.Length + 1, face.GlyphCount);
        var s = Scale(13);
        for (var i = 0; i < codes.Length; i++)
        {
            var width = ArtWidth(codes[i], kind.Fixed);
            Assert.Equal(width * s, Advance(font, i + 1));
            AssertMask(ExpectedMask(codes[i], width, 13), Render(face, i + 1, 13, ArtAscent(13), width), $"{kind.File} {format} code {codes[i]}");
        }
    }

    // ───────────────────────── C2: pixel masks ─────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Every_bitmap_mask_matches_unhinted_integer_raster(int kindIndex)
    {
        var kind = Kinds[kindIndex];
        var font = FonOutlineBuilder.Build(Assert.Single(FonReader.Read(Data(kind.File), TestContext.Current.CancellationToken)), TestContext.Current.CancellationToken);
        var glyphs = GlyfTable.ReadAll(font);
        using var face = SKTypeface.FromStream(new MemoryStream(font.Write()));
        var codes = Enumerable.Range(kind.First, 127 - kind.First + 1).ToArray();
        // Single pixel (33), diagonal (34), a box with a hole (35+), blank space (32), and control glyphs (0..31, 127).
        Assert.Contains(33, codes);
        Assert.Contains(34, codes);
        for (var i = 0; i < codes.Length; i++)
        {
            var width = ArtWidth(codes[i], kind.Fixed);
            if (glyphs[i + 1] is { } glyph)
                Assert.All(glyph.Points, p => Assert.True(p.OnCurve, $"code {codes[i]} has an off-curve point"));
            AssertMask(ExpectedMask(codes[i], width, 13), Render(face, i + 1, 13, ArtAscent(13), width), $"{kind.File} code {codes[i]}");
        }
        // The first lit column is x=2: two blank columns on the left survive.
        var box = glyphs[35 - kind.First + 1]!;
        Assert.Equal(2 * Scale(13), box.XMin);
        Assert.Equal(2, box.NumberOfContours); // the outline and its hole
    }

    // ───────────────────────── C3: cmap ─────────────────────────

    /// <summary>CP1252 0x80..0x9F; 0 marks the five undefined bytes.</summary>
    private static readonly int[] Cp1252High =
    [
        0x20AC, 0, 0x201A, 0x0192, 0x201E, 0x2026, 0x2020, 0x2021, 0x02C6, 0x2030, 0x0160, 0x2039, 0x0152, 0, 0x017D, 0,
        0, 0x2018, 0x2019, 0x201C, 0x201D, 0x2022, 0x2013, 0x2014, 0x02DC, 0x2122, 0x0161, 0x203A, 0x0153, 0, 0x017E, 0x0178,
    ];

    [Fact]
    public void Cmap_excludes_controls_and_undefined_bytes_without_losing_glyphs()
    {
        // 0x00..0x7F from the generated sample: controls 0x00..0x1F and DEL 0x7F keep glyphs but no cmap entry.
        var strike = Assert.Single(FonReader.Read(Data("Fon-v2-proportional.fon"), TestContext.Current.CancellationToken));
        var font = FonOutlineBuilder.Build(strike, TestContext.Current.CancellationToken);
        var glyphs = GlyfTable.ReadAll(font);
        Assert.Equal(128 + 1, font.GlyphCount);
        var expected = Enumerable.Range(0x20, 0x7F - 0x20).ToDictionary(c => c, c => c + 1);
        Assert.Equal(expected.OrderBy(p => p.Key), Cmap(font).OrderBy(p => p.Key));
        foreach (var code in Enumerable.Range(0, 0x20).Append(0x7F))
        {
            Assert.NotNull(glyphs[code + 1]);
            Assert.Equal(ArtWidth(code, false) * Scale(13), Advance(font, code + 1));
        }

        // 0x7E..0xA0: the CP1252 high half, its five undefined bytes, and NBSP.
        var pixels = new bool[13, 8];
        pixels[0, 2] = true;
        var high = strike with
        {
            Glyphs = [.. Enumerable.Range(0x7E, 0xA0 - 0x7E + 1).Select(b => new FonGlyph((byte)b, 8, pixels, 0))],
            DefaultChar = 0,
        };
        font = FonOutlineBuilder.Build(high, TestContext.Current.CancellationToken);
        glyphs = GlyfTable.ReadAll(font);
        Assert.Equal(high.Glyphs.Count + 1, font.GlyphCount);
        var map = new Dictionary<int, int> { [0x7E] = 1, [0xA0] = high.Glyphs.Count };
        for (var b = 0x80; b <= 0x9F; b++)
            if (Cp1252High[b - 0x80] != 0)
                map[Cp1252High[b - 0x80]] = b - 0x7E + 1;
        Assert.Equal(map.OrderBy(p => p.Key), Cmap(font).OrderBy(p => p.Key));
        Assert.Equal(map.Count, Cmap(font).Values.Distinct().Count());
        Assert.All(Enumerable.Range(1, high.Glyphs.Count), gid =>
        {
            Assert.NotNull(glyphs[gid]);
            Assert.Equal(8 * Scale(13), Advance(font, gid));
        });
        Assert.DoesNotContain(0xFFFD, Cmap(font).Keys);
        // Basic Latin, Latin-1, Latin Extended-A/B, spacing modifiers, punctuation, currency and letterlike symbols.
        Assert.Equal([0, 1, 2, 3, 5, 31, 33, 35], UnicodeRangeBits(font));
    }

    [Fact]
    public void Supported_charsets_are_locale_independent()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var charsets = new Dictionary<byte, int>
        {
            [0] = 1252,
            [238] = 1250,
            [204] = 1251,
            [161] = 1253,
            [162] = 1254,
            [177] = 1255,
            [178] = 1256,
            [186] = 1257,
            [163] = 1258,
            [222] = 874,
        };
        var strike = Assert.Single(FonReader.Read(Data("Fon-v2-fixed.fon"), TestContext.Current.CancellationToken));
        var pixels = new bool[13, 8];
        pixels[1, 3] = true;
        var glyphs = Enumerable.Range(0xA1, 0xFE - 0xA1 + 1).Select(b => new FonGlyph((byte)b, 8, pixels, 0)).ToList();
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (var locale in new[] { "tr-TR", "ja-JP", "ru-RU" })
            {
                CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(locale);
                foreach (var (charset, codePage) in charsets)
                {
                    var encoding = Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                    var expected = new Dictionary<int, int>();
                    for (var i = 0; i < glyphs.Count; i++)
                    {
                        string text;
                        try { text = encoding.GetString([glyphs[i].ByteValue]); }
                        catch (DecoderFallbackException) { continue; }
                        if (text.Length != 1 || char.IsControl(text[0]) || char.IsSurrogate(text[0]) ||
                            encoding.GetBytes(text).Single() != glyphs[i].ByteValue ||
                            UndefinedHigh.GetValueOrDefault(charset, []).Contains(glyphs[i].ByteValue))
                            continue;
                        expected.TryAdd(text[0], i + 1);
                    }
                    var cmap = Cmap(FonOutlineBuilder.Build(strike with { CharSet = charset, Glyphs = glyphs, DefaultChar = 0 }, TestContext.Current.CancellationToken));
                    Assert.Equal(expected.OrderBy(p => p.Key), cmap.OrderBy(p => p.Key));
                }
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = culture;
        }
        // Spot checks that the tables really differ: 0xC0 is À, А (Cyrillic) or ภ (Thai) depending on the charset.
        Assert.Equal(0xC0 - 0xA1 + 1, Cmap(FonOutlineBuilder.Build(strike with { CharSet = 0, Glyphs = glyphs, DefaultChar = 0 }, TestContext.Current.CancellationToken))[0x00C0]);
        Assert.Equal(0xC0 - 0xA1 + 1, Cmap(FonOutlineBuilder.Build(strike with { CharSet = 204, Glyphs = glyphs, DefaultChar = 0 }, TestContext.Current.CancellationToken))[0x0410]);
        Assert.Equal(0xC0 - 0xA1 + 1, Cmap(FonOutlineBuilder.Build(strike with { CharSet = 222, Glyphs = glyphs, DefaultChar = 0 }, TestContext.Current.CancellationToken))[0x0E20]);
        // ulUnicodeRange follows the mapped blocks of 0xA1..0xFE in each code page.
        Assert.Equal([1, 2, 5], UnicodeRangeBits(FonOutlineBuilder.Build(strike with { CharSet = 238, Glyphs = glyphs, DefaultChar = 0 }, TestContext.Current.CancellationToken)));
        Assert.Equal([1, 2, 3, 6, 33], UnicodeRangeBits(FonOutlineBuilder.Build(strike with { CharSet = 163, Glyphs = glyphs, DefaultChar = 0 }, TestContext.Current.CancellationToken)));
        Assert.Equal([24], UnicodeRangeBits(FonOutlineBuilder.Build(strike with { CharSet = 222, Glyphs = glyphs, DefaultChar = 0 }, TestContext.Current.CancellationToken)));
    }

    /// <summary>Bytes the Unicode MICSFT tables mark UNDEFINED that .NET decodes to Private Use code points, by charset.</summary>
    private static readonly Dictionary<byte, int[]> UndefinedHigh = new()
    {
        [161] = [0xAA, 0xD2, 0xFF], // CP1253
        [177] = [0xD9, 0xDA, 0xDB, 0xDC, 0xDD, 0xDE, 0xDF, 0xFB, 0xFC, 0xFF], // CP1255
        [186] = [0xA1, 0xA5], // CP1257
        [222] = [0xDB, 0xDC, 0xDD, 0xDE, 0xFC, 0xFD, 0xFE, 0xFF], // CP874
    };

    [Theory]
    [InlineData(161)]
    [InlineData(177)]
    [InlineData(186)]
    [InlineData(222)]
    public void Undefined_bytes_decoded_to_private_use_keep_glyphs_but_no_cmap(int charset)
    {
        var strike = Assert.Single(FonReader.Read(Data("Fon-v2-fixed.fon"), TestContext.Current.CancellationToken));
        var pixels = new bool[13, 8];
        pixels[1, 3] = true;
        var glyphs = Enumerable.Range(0x80, 0x80).Select(b => new FonGlyph((byte)b, 8, pixels, 0)).ToList();
        var font = FonOutlineBuilder.Build(strike with { CharSet = (byte)charset, Glyphs = glyphs, DefaultChar = 0 }, TestContext.Current.CancellationToken);
        var cmap = Cmap(font);
        var outlines = GlyfTable.ReadAll(font);
        foreach (var b in UndefinedHigh[(byte)charset])
        {
            var gid = b - 0x80 + 1;
            Assert.DoesNotContain(gid, cmap.Values);
            Assert.NotNull(outlines[gid]);
            Assert.Equal(8 * Scale(13), Advance(font, gid));
        }
        Assert.DoesNotContain(cmap.Keys, c => c is >= 0xE000 and <= 0xF8FF);
        Assert.Equal(cmap.Keys.Max(), U16(font.Require("OS/2"), 66)); // usLastCharIndex
        Assert.True(U16(font.Require("OS/2"), 66) < 0xE000);
        Assert.DoesNotContain(60, UnicodeRangeBits(font)); // Private Use Area
    }

    // ───────────────────────── C4: metrics ─────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Metrics_preserve_ascent_baseline_proportional_and_space(int kindIndex)
    {
        var kind = Kinds[kindIndex];
        var job = await fx.ConvertAsync([Source(Data(kind.File), out _)], PresetFor("ttf"));
        var output = Assert.Single(job.Outputs);
        var font = FontFile.Load(File.ReadAllBytes(output), TestContext.Current.CancellationToken);
        var upem = U16(font.Require("head"), 18);
        var s = upem / 13;
        Assert.Equal(13 * s, upem);
        var (hhea, os2) = (font.Require("hhea"), font.Require("OS/2"));
        Assert.Equal((10 * s, -3 * s, 2 * s), (I16(hhea, 4), I16(hhea, 6), I16(hhea, 8)));
        Assert.Equal((10 * s, -3 * s, 2 * s), (I16(os2, 68), I16(os2, 70), I16(os2, 72)));

        var glyphs = GlyfTable.ReadAll(font);
        using var face = SKTypeface.FromFile(output);
        using var skFont = new SKFont(face, 13) { Hinting = SKFontHinting.None, Subpixel = true };
        Assert.Equal(-10f, skFont.Metrics.Ascent, 3);
        Assert.Equal(3f, skFont.Metrics.Descent, 3);
        var codes = Enumerable.Range(kind.First, 127 - kind.First + 1).ToArray();
        var widths = new float[codes.Length + 1];
        skFont.GetGlyphWidths([.. Enumerable.Range(0, codes.Length + 1).Select(g => (ushort)g)], widths, []);
        for (var i = 0; i < codes.Length; i++)
        {
            var width = ArtWidth(codes[i], kind.Fixed);
            Assert.Equal(width * s, Advance(font, i + 1));
            Assert.Equal(width, widths[i + 1], 3);
            if (codes[i] == 32)
            {
                Assert.Null(glyphs[i + 1]);
                Assert.Equal(0, Lsb(font, i + 1));
                continue;
            }
            var firstLit = Enumerable.Range(0, width).First(x => Enumerable.Range(0, 13).Any(y => ArtLit(codes[i], x, y, width, 13)));
            Assert.Equal(2, firstLit);
            Assert.Equal(firstLit * s, glyphs[i + 1]!.XMin);
            Assert.Equal(glyphs[i + 1]!.XMin, Lsb(font, i + 1)); // pp1 = xMin - lsb = 0
            var mask = Render(face, i + 1, 13, 10, width);
            var renderedFirst = Enumerable.Range(0, width + Pad * 2).First(x => Enumerable.Range(0, 13 + Pad * 2).Any(y => mask[y, x])) - Pad;
            Assert.Equal(2, renderedFirst);
        }
        // Proportional widths stay different; the fixed sample's blank space keeps its 8 pixels.
        Assert.Equal(kind.Fixed ? 1 : 3, codes.Select(c => ArtWidth(c, kind.Fixed)).Distinct().Count());
        Assert.Equal(ArtWidth(32, kind.Fixed) * s, Advance(font, 32 - kind.First + 1));
    }

    [Fact]
    public void Hhea_extrema_and_caret_match_contoured_glyphs()
    {
        var kind = Kinds[1]; // proportional, 0x00..0x7F, default char 0
        var font = FonOutlineBuilder.Build(Assert.Single(FonReader.Read(Data(kind.File), TestContext.Current.CancellationToken)), TestContext.Current.CancellationToken);
        var s = Scale(13);
        var contoured = new List<(int Advance, int Lsb, int XMin, int XMax)>();
        var advances = new List<int>();
        foreach (var code in new[] { kind.First }.Concat(Enumerable.Range(kind.First, 128))) // .notdef copies code 0
        {
            var width = ArtWidth(code, false);
            advances.Add(width * s);
            var lit = Enumerable.Range(0, width).Where(x => Enumerable.Range(0, 13).Any(y => ArtLit(code, x, y, width, 13))).ToList();
            if (lit.Count > 0)
                contoured.Add((width * s, lit.Min() * s, lit.Min() * s, (lit.Max() + 1) * s));
        }
        var hhea = font.Require("hhea");
        Assert.Equal(advances.Max(), U16(hhea, 10));
        Assert.Equal(contoured.Min(g => g.Lsb), I16(hhea, 12));
        Assert.Equal(contoured.Min(g => g.Advance - (g.Lsb + g.XMax - g.XMin)), I16(hhea, 14));
        Assert.Equal(contoured.Max(g => g.Lsb + g.XMax - g.XMin), I16(hhea, 16));
        Assert.Equal((1, 0, 0), (I16(hhea, 18), I16(hhea, 20), I16(hhea, 22)));
        Assert.Equal([0, 0, 0, 0, 0], new[] { I16(hhea, 24), I16(hhea, 26), I16(hhea, 28), I16(hhea, 30), I16(hhea, 32) });
        Assert.Equal(font.GlyphCount, U16(hhea, 34));
    }

    // ───────────────────────── C5: names ─────────────────────────

    private static string PsEscape(string full) => string.Concat(Encoding.UTF8.GetBytes(full).Select(b =>
        char.IsAsciiLetterOrDigit((char)b) || b == '-' ? ((char)b).ToString() : $"_{b:X2}"));

    [Fact]
    public void Names_and_style_bits_match_each_resource()
    {
        var single = Assert.Single(FonReader.Read(Data("Fon-v2-fixed.fon"), TestContext.Current.CancellationToken));
        void Check(FonStrike strike, string family, string style, int weight, int fsSelection, int macStyle, int count = 1, int ordinal = 1)
        {
            var font = FonOutlineBuilder.Build(strike, TestContext.Current.CancellationToken, count, ordinal);
            var full = $"{family} {style}";
            Assert.Equal(family, Name(font, 1));
            Assert.Equal(style, Name(font, 2));
            Assert.Equal(full, Name(font, 4));
            var ps = PsEscape(full);
            Assert.Equal(ps.Length <= 63 ? ps : Name(font, 6), Name(font, 6));
            Assert.Null(Name(font, 16));
            Assert.Null(Name(font, 17));
            Assert.Equal(weight, U16(font.Require("OS/2"), 4));
            Assert.Equal(fsSelection, U16(font.Require("OS/2"), 62));
            Assert.Equal(macStyle, U16(font.Require("head"), 44));
        }

        Check(single, "Fixed", "Regular", 400, 0x40, 0);
        Assert.Equal("Fixed_20Regular", Name(FonOutlineBuilder.Build(single, TestContext.Current.CancellationToken), 6));
        Check(single with { Weight = 700, Italic = true }, "Fixed", "Bold Italic", 700, 0x21, 3);
        Check(single with { Weight = 700 }, "Fixed", "Bold", 700, 0x20, 1);
        Check(single with { Italic = true }, "Fixed", "Italic", 400, 0x01, 2);
        var unweighted = Data("Fon-v2-fixed.fon");
        BinaryPrimitives.WriteUInt16LittleEndian(unweighted.AsSpan(FntStart(unweighted) + 0x53), 0);
        Check(Assert.Single(FonReader.Read(unweighted, TestContext.Current.CancellationToken)), "Fixed", "Regular", 400, 0x40, 0);
        Check(single with { Weight = 500 }, "Fixed Weight500", "Regular", 500, 0x40, 0);
        Check(single, "Fixed 13px", "Regular", 400, 0x40, 0, count: 3);
        Check(single, "Fixed 13px-2", "Regular", 400, 0x40, 0, count: 3, ordinal: 2);
        Assert.Equal("Fixed_2013px-2_20Regular", Name(FonOutlineBuilder.Build(single, TestContext.Current.CancellationToken, 3, 2), 6));

        // A long non-ASCII face: nameID 6 is cut at a token boundary (at most 50) and gets a hash of the full name.
        var face = string.Concat(Enumerable.Repeat("Ä", 40));
        var font = FonOutlineBuilder.Build(single with { Face = face }, TestContext.Current.CancellationToken);
        var full = $"{face} Regular";
        var ps = Name(font, 6)!;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)).AsSpan(0, 6));
        Assert.Equal(PsEscape(full)[..48] + "-" + hash, ps);
        Assert.True(ps.Length <= 63);
        Assert.Equal(face, Name(font, 1));
    }

    [Theory]
    [InlineData("ttf")]
    [InlineData("otf")]
    [InlineData("woff")]
    [InlineData("woff2")]
    [InlineData("eot")]
    public async Task Multi_resource_outputs_have_distinct_name_ids(string format)
    {
        var source = Source(Data("Fon-multi.fon"), out var folder);
        var job = await fx.ConvertAsync([source], PresetFor(format));
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var expected = new[]
        {
            ("Name-13px", "Fixed 13px", "Fixed_2013px_20Regular"),
            ("Name-13px-2", "Fixed 13px-2", "Fixed_2013px-2_20Regular"),
            ("Name-14px", "Fixed 14px", "Fixed_2014px_20Regular"),
        };
        Assert.Equal(expected.Select(e => Path.Combine(folder, $"{e.Item1}.{format}")), job.Outputs);
        var names = new List<(string?, string?, string?)>();
        foreach (var (file, family, ps) in expected)
        {
            var font = FontFile.Load(File.ReadAllBytes(Path.Combine(folder, $"{file}.{format}")), TestContext.Current.CancellationToken);
            Assert.Equal(family, Name(font, 1));
            Assert.Equal("Regular", Name(font, 2));
            Assert.Equal(family + " Regular", Name(font, 4));
            Assert.Equal(ps, Name(font, 6));
            Assert.Null(Name(font, 16));
            Assert.Null(Name(font, 17));
            names.Add((Name(font, 1), Name(font, 4), Name(font, 6)));
        }
        Assert.Equal(3, names.Select(n => n.Item1).Distinct().Count());
        Assert.Equal(3, names.Select(n => n.Item2).Distinct().Count());
        Assert.Equal(3, names.Select(n => n.Item3).Distinct().Count());
    }

    [Fact]
    public async Task Multi_resource_same_height_styles_have_style_names_and_ordinals()
    {
        var source = Source(FonBuilder.MixedStyles(), out var folder);
        var job = await fx.ConvertAsync([source], PresetFor("ttf"));
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var expected = new[]
        {
            ("Name-13px.ttf", "Name 13px", "Regular"),
            ("Name-13px-Bold.ttf", "Name 13px", "Bold"),
            ("Name-13px-Italic.ttf", "Name 13px", "Italic"),
            ("Name-13px-BoldItalic.ttf", "Name 13px", "Bold Italic"),
            ("Name-13px-Weight600.ttf", "Name 13px Weight600", "Regular"),
            ("Name-13px-Weight600Italic.ttf", "Name 13px Weight600", "Italic"),
            ("Name-13px-2.ttf", "Name 13px-2", "Regular"),
            ("Name-13px-Bold-2.ttf", "Name 13px-2", "Bold"),
            ("Name-14px.ttf", "Name 14px", "Regular"),
        };
        Assert.Equal(expected.Select(e => Path.Combine(folder, e.Item1)), job.Outputs);
        foreach (var (file, family, style) in expected)
        {
            var font = FontFile.Load(File.ReadAllBytes(Path.Combine(folder, file)), TestContext.Current.CancellationToken);
            Assert.Equal(family, Name(font, 1));
            Assert.Equal(style, Name(font, 2));
        }
    }

    /// <summary>Hands out <c>Name{suffix}.{ext}</c> paths in a folder and records each request.</summary>
    private sealed class RecordingAllocator(string folder) : IOutputAllocator
    {
        public List<string?> Suffixes { get; } = [];

        public string? Allocate(string extension, string? suffix = null)
        {
            Suffixes.Add(suffix);
            return Path.Combine(folder, $"Name{suffix}.{extension}");
        }
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    [Fact]
    public async Task Same_height_resources_remain_separate()
    {
        var data = Data("Fon-multi.fon");
        var second = FntStart(data, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(second + 0x53), 700);
        data[second + 0x50] = 1;
        var source = Source(data, out var folder);
        var allocator = new RecordingAllocator(folder);
        var outputs = await new FontConverter().ConvertAsync(
            new ConversionStep(source, "fon", "ttf", PresetFor("ttf"), allocator, fx.NewFolder()), null, TestContext.Current.CancellationToken);

        Assert.Equal(["-13px", "-13px-BoldItalic", "-14px"], allocator.Suffixes);
        Assert.Equal(3, outputs.Count);
        var fonts = outputs.Select(o => FontFile.Load(File.ReadAllBytes(o))).ToArray();
        Assert.Equal(["Fixed 13px", "Fixed 13px", "Fixed 14px"], fonts.Select(f => Name(f, 1)));
        Assert.Equal(["Regular", "Bold Italic", "Regular"], fonts.Select(f => Name(f, 2)));
        Assert.Equal(3, outputs.Select(o => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(o)))).Distinct().Count());
        Assert.Equal(fonts[0].Require("glyf"), fonts[1].Require("glyf")); // same pixels, still two fonts
    }

    // ───────────────────────── C6/C8: queue post-processing ─────────────────────────

    [Fact]
    public async Task Single_strike_subfolder_only_follows_queue_postprocessing()
    {
        Preset Subfolder(ConflictPolicy conflict) => new()
        {
            TargetFormat = "ttf",
            Output = { Location = OutputLocation.Subfolder, SubfolderOnlyForSeveralFiles = true, Conflict = conflict },
        };

        // No conflict: the one file goes next to the source, without a size suffix, and no subfolder is left.
        var source = Source(Data("Fon-v2-fixed.fon"), out var folder);
        var job = await fx.ConvertAsync([source], Subfolder(ConflictPolicy.Rename));
        Assert.Equal([Path.Combine(folder, "Name.ttf")], job.Outputs);
        Assert.False(Directory.Exists(Path.Combine(folder, "converted")));

        // Skip: the existing file next to the source stays, the result stays in the subfolder.
        source = Source(Data("Fon-v2-fixed.fon"), out folder);
        var existing = Path.Combine(folder, "Name.ttf");
        File.WriteAllText(existing, "old");
        job = await fx.ConvertAsync([source], Subfolder(ConflictPolicy.Skip));
        Assert.Equal(FileState.Done, job.Files[0].State);
        Assert.Equal([Path.Combine(folder, "converted", "Name.ttf")], job.Outputs);
        Assert.Equal("old", File.ReadAllText(existing));

        // Overwrite: the result replaces it.
        source = Source(Data("Fon-v2-fixed.fon"), out folder);
        existing = Path.Combine(folder, "Name.ttf");
        File.WriteAllText(existing, "old");
        job = await fx.ConvertAsync([source], Subfolder(ConflictPolicy.Overwrite));
        Assert.Equal([existing], job.Outputs);
        Assert.Equal(0x00010000u, BinaryPrimitives.ReadUInt32BigEndian(File.ReadAllBytes(existing)));
        Assert.False(Directory.Exists(Path.Combine(folder, "converted")));

        // The move fails (a folder holds the name): the queue keeps the result where it was written.
        source = Source(Data("Fon-v2-fixed.fon"), out folder);
        Directory.CreateDirectory(Path.Combine(folder, "Name.ttf"));
        job = await fx.ConvertAsync([source], Subfolder(ConflictPolicy.Overwrite));
        Assert.Equal([Path.Combine(folder, "converted", "Name.ttf")], job.Outputs);
        Assert.True(Directory.Exists(Path.Combine(folder, "Name.ttf")));
    }

    // ───────────────────────── C8: nothing written before every output is built ─────────────────────────

    private static byte[] Resource(byte[] fon, int index)
    {
        var ne = (int)BinaryPrimitives.ReadUInt32LittleEndian(fon.AsSpan(0x3c));
        var table = ne + BinaryPrimitives.ReadUInt16LittleEndian(fon.AsSpan(ne + 0x24));
        var entry = table + 2 + 8 + 12 + 8 + index * 12;
        var start = BinaryPrimitives.ReadUInt16LittleEndian(fon.AsSpan(entry));
        return fon[start..(start + BinaryPrimitives.ReadUInt16LittleEndian(fon.AsSpan(entry + 2)))];
    }

    /// <summary>MZ/NE with a FONTDIR and the given RT_FONT resources.</summary>
    private static byte[] Container(params byte[][] resources)
    {
        const int ne = 0x40, table = ne + 0x40;
        var payload = table + 2 + 8 + 12 + 8 + 12 * resources.Length + 2;
        var data = new byte[payload + 1 + resources.Sum(r => r.Length)];
        data[0] = (byte)'M';
        data[1] = (byte)'Z';
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x3c), ne);
        data[ne] = (byte)'N';
        data[ne + 1] = (byte)'E';
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(ne + 0x24), table - ne);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(ne + 0x26), (ushort)(payload - ne));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(table + 2), 0x8007);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(table + 4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(table + 10), (ushort)payload);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(table + 12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(table + 22), 0x8008);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(table + 24), (ushort)resources.Length);
        var at = payload + 1;
        for (var i = 0; i < resources.Length; i++)
        {
            var entry = table + 30 + i * 12;
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(entry), (ushort)at);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(entry + 2), (ushort)resources[i].Length);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(entry + 6), (ushort)(0x8001 + i));
            resources[i].CopyTo(data, at);
            at += resources[i].Length;
        }
        return data;
    }

    /// <summary>A valid FNT v2: 1 pixel high, one glyph 33 pixels wide whose last column (x=32) is lit.</summary>
    private static byte[] WideOnePixelFnt()
    {
        const int table = 0x76, face = table + 2 * 4, bits = face + 6;
        var data = new byte[bits + 5];
        BinaryPrimitives.WriteUInt16LittleEndian(data, 0x0200);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(2), (uint)data.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x4a), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x53), 400);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x58), 1);
        data[0x5f] = data[0x60] = 32;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x69), face);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x71), bits);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(table), 33);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(table + 2), bits);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(table + 6), (ushort)data.Length);
        "Fixed\0"u8.CopyTo(data.AsSpan(face));
        data[bits + 4] = 0x80;
        return data;
    }

    /// <summary>Every glyph aliases one blank bitmap; the decoded pixel count is not bounded by FNT size.</summary>
    private static byte[] AliasedFnt(int width, int height, int glyphCount = 128)
    {
        const int table = 0x76;
        var face = table + (glyphCount + 1) * 4;
        var bits = face + 6;
        var data = new byte[bits + ((width + 7) / 8) * height];
        BinaryPrimitives.WriteUInt16LittleEndian(data, 0x0200);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(2), (uint)data.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x4a), (ushort)height);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x53), 400);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x58), (ushort)height);
        data[0x60] = (byte)(glyphCount - 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x69), (uint)face);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x71), (uint)bits);
        for (var i = 0; i < glyphCount; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(table + i * 4), (ushort)width);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(table + i * 4 + 2), (ushort)bits);
        }
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(table + glyphCount * 4 + 2), (ushort)data.Length);
        "Fixed\0"u8.CopyTo(data.AsSpan(face));
        return data;
    }

    private static void AssertRefused(Action action, string key, string? detail = null)
    {
        var error = Assert.Throws<ConversionRefusedException>(action);
        Assert.Equal(key, error.ErrorKey);
        Assert.Equal(detail, error.Detail);
        Assert.True(error.Detail is null || Regex.IsMatch(error.Detail,
            @"^(PE|FNT 0x[0-9A-F]{4}|dfFlags 0x[0-9A-F]{8}|charset \d+|[0-9][0-9,]*)$"));
    }

    [Fact]
    public void Refusal_details_are_language_neutral()
    {
        var data = Data("Fon-v2-fixed.fon");
        var offset = FntStart(data);
        var cases = new List<(byte[] Data, string Key, string? Detail)>();
        data[offset + 0x42] = 1;
        cases.Add((data, "error.fon_vector", null));
        data = Data("Fon-v2-fixed.fon");
        data[FntStart(data) + 0x55] = 128;
        cases.Add((data, "error.fon_unsupported", "charset 128"));
        cases.Add((FonBuilder.Container([AliasedFnt(64, 1, 1)]), "error.fon_too_large", "65535"));
        cases.Add(([], "error.fon_damaged", null));

        foreach (var (sample, key, detail) in cases)
            AssertRefused(() => FonReader.Read(sample, TestContext.Current.CancellationToken), key, detail);
    }

    /// <summary>
    /// Runs <paramref name="data"/> as Name.fon under each conflict policy, with every name it would write already
    /// taken, and checks that nothing changed and nothing was reported.
    /// </summary>
    private async Task AssertNothingWrittenAsync(byte[] data, string[] taken, FileState state,
        string? errorKey = null, string? detail = null)
    {
        foreach (var policy in Enum.GetValues<ConflictPolicy>())
        {
            var source = Source(data, out var folder);
            for (var i = 0; i < taken.Length; i++)
                File.WriteAllText(Path.Combine(folder, taken[i]), $"old {i}");
            var before = Snapshot(folder);

            var job = await fx.ConvertAsync([source], PresetFor("ttf", policy));

            var file = Assert.Single(job.Files);
            Assert.Equal(state, file.State);
            if (errorKey is not null)
            {
                Assert.Equal(errorKey, file.ErrorKey);
                Assert.Equal(detail, file.ErrorDetail);
            }
            Assert.Empty(job.Outputs);
            Assert.Equal(before.OrderBy(p => p.Key), Snapshot(folder).OrderBy(p => p.Key));
        }
    }

    [Fact]
    public async Task Fon_prewrite_parser_failure_preserves_outputs_under_each_conflict_policy()
    {
        // The second RT_FONT's first bitmap points outside the resource: the reader fails before any output is built.
        var data = Data("Fon-multi.fon");
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(FntStart(data, 1) + 0x94 + 2), 0x7fffffff);
        AssertRefused(() => FonReader.Read(data, TestContext.Current.CancellationToken), "error.fon_damaged");

        await AssertNothingWrittenAsync(data, ["Name-13px.ttf", "Name-13px-2.ttf", "Name-14px.ttf"], FileState.Failed, "error.fon_damaged");
    }

    [Fact]
    public async Task Fon_prewrite_builder_failure_preserves_outputs_under_each_conflict_policy()
    {
        var first = Resource(Data("Fon-multi.fon"), 0);
        var data = Container(first, WideOnePixelFnt());
        var strikes = FonReader.Read(data, TestContext.Current.CancellationToken);
        Assert.Equal([13, 1], strikes.Select(s => s.PixelHeight));
        // The first resource builds completely on its own; the second passes the reader but not the builder.
        Assert.NotEmpty(FontFile.Save(FonOutlineBuilder.Build(strikes[0], TestContext.Current.CancellationToken, 2), "ttf", TestContext.Current.CancellationToken));
        AssertRefused(() => FonOutlineBuilder.Build(strikes[1], TestContext.Current.CancellationToken, 2), "error.fon_too_large", "32767");

        await AssertNothingWrittenAsync(data, ["Name-13px.ttf", "Name-1px.ttf"], FileState.Failed, "error.fon_too_large", "32767");
    }

    [Fact]
    public async Task Fon_prewrite_cancellation_leaves_no_outputs()
    {
        var source = Source(Data("Fon-multi.fon"), out var folder);
        File.WriteAllText(Path.Combine(folder, "Name-13px.ttf"), "old");
        var before = Snapshot(folder);
        var allocator = new RecordingAllocator(folder);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var reports = new List<double>();
        // Synchronous: the report right after the first output was built cancels before anything else happens.
        var progress = new SyncProgress(value =>
        {
            reports.Add(value);
            if (value == 0.85)
                cts.Cancel();
        });

        var conversion = new FontConverter().ConvertAsync(
            new ConversionStep(source, "fon", "ttf", PresetFor("ttf"), allocator, fx.NewFolder()), progress, cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => conversion.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));
        Assert.Contains(0.85, reports);
        Assert.Empty(allocator.Suffixes);
        Assert.Equal(before.OrderBy(p => p.Key), Snapshot(folder).OrderBy(p => p.Key));
    }

    [Fact]
    public async Task Aliased_glyphs_over_filewide_pixel_limit_fail_without_outputs()
    {
        var data = Container(AliasedFnt(16, 1024), AliasedFnt(17, 1024));
        AssertRefused(() => FonReader.Read(data, TestContext.Current.CancellationToken),
            "error.fon_too_large", FonReader.MaxGlyphPixels.ToString("N0", CultureInfo.InvariantCulture));

        await AssertNothingWrittenAsync(data, ["Name-1024px.ttf", "Name-1024px-2.ttf"], FileState.Failed,
            "error.fon_too_large", FonReader.MaxGlyphPixels.ToString("N0", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Glyph_advance_above_65535_is_rejected_before_decoding()
    {
        var data = FonBuilder.Container([AliasedFnt(64, 1, 1)]);

        AssertRefused(() => FonReader.Read(data, TestContext.Current.CancellationToken), "error.fon_too_large", "65535");
    }

    [Fact]
    public void Blank_glyph_with_advance_above_signed_coordinate_limit_converts()
    {
        var resource = AliasedFnt(33, 1, 2);
        BinaryPrimitives.WriteUInt16LittleEndian(resource.AsSpan(0x76 + 4), 0);
        var data = FonBuilder.Container([resource]);
        var strike = Assert.Single(FonReader.Read(data, TestContext.Current.CancellationToken));
        var font = FontFile.Load(FontFile.Save(
            FonOutlineBuilder.Build(strike, TestContext.Current.CancellationToken), "ttf", TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal(33 * 1024, Advance(font, 1));
        Assert.Null(GlyfTable.ReadAll(font)[1]);
    }

    [Fact]
    public void Shared_large_bitmap_is_refused_with_less_than_one_megabyte_allocated()
    {
        var data = FonBuilder.Container([AliasedFnt(4096, 1024, 224)], alignmentShift: 9);
        Assert.Single(FonReader.Read(FonBuilder.V2Fixed(), TestContext.Current.CancellationToken));

        var before = GC.GetAllocatedBytesForCurrentThread();
        AssertRefused(() => FonReader.Read(data, TestContext.Current.CancellationToken),
            "error.fon_too_large", FonReader.MaxGlyphPixels.ToString("N0", CultureInfo.InvariantCulture));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 1_000_000, $"Reader allocated {allocated:N0} bytes.");
    }

    [Fact]
    public void Checkerboard_glyph_over_point_limit_is_refused_before_tracing()
    {
        var pixels = new bool[2048, 2048];
        for (var y = 0; y < 2048; y++)
            for (var x = 0; x < 2048; x++)
                pixels[y, x] = ((x + y) & 1) == 0;
        var strike = Assert.Single(FonReader.Read(Data("Fon-v2-fixed.fon"), TestContext.Current.CancellationToken)) with
        {
            PixelHeight = 2048,
            Ascent = 2048,
            Glyphs = [new FonGlyph(32, 2048, pixels, 0)],
            DefaultChar = 0
        };

        var before = GC.GetAllocatedBytesForCurrentThread();
        AssertRefused(() => FonOutlineBuilder.Build(strike, TestContext.Current.CancellationToken), "error.fon_too_large", "65535");
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 1_000_000, $"Builder allocated {allocated:N0} bytes.");
    }

    [Fact]
    public void Glyphs_sharing_a_bitmap_build_one_outline()
    {
        var pixels = new bool[64, 64];
        for (var y = 0; y < 64; y++)
            for (var x = 0; x < 64; x++)
                pixels[y, x] = ((x + y) & 1) == 0;
        var strike = Assert.Single(FonReader.Read(Data("Fon-v2-fixed.fon"), TestContext.Current.CancellationToken)) with
        {
            PixelHeight = 64,
            Ascent = 64,
            Glyphs = [new FonGlyph(32, 64, pixels, 100)],
            DefaultChar = 0
        };
        var before = GC.GetAllocatedBytesForCurrentThread();
        var single = FonOutlineBuilder.Build(strike, TestContext.Current.CancellationToken);
        var singleAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        strike = strike with
        {
            Glyphs = [.. Enumerable.Range(32, 64).Select(code => new FonGlyph((byte)code, 64, pixels, 100))]
        };
        before = GC.GetAllocatedBytesForCurrentThread();
        var shared = FonOutlineBuilder.Build(strike, TestContext.Current.CancellationToken);
        var sharedAllocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(sharedAllocated < singleAllocated * 4,
            $"Shared build allocated {sharedAllocated:N0} bytes versus {singleAllocated:N0} for one glyph.");
        var glyphs = GlyfTable.ReadAll(shared);
        var expected = GlyfTable.ReadAll(single)[1]!.Serialize();
        Assert.All(glyphs.Skip(1), glyph => Assert.Equal(expected, glyph!.Serialize()));
    }

    [Fact]
    public void Glyphs_with_one_offset_but_different_widths_keep_their_own_outlines()
    {
        var narrow = new bool[8, 8];
        var wide = new bool[8, 12];
        narrow[0, 7] = true;
        wide[0, 11] = true;
        var strike = Assert.Single(FonReader.Read(Data("Fon-v2-fixed.fon"), TestContext.Current.CancellationToken)) with
        {
            PixelHeight = 8,
            Ascent = 8,
            DefaultChar = 0,
            Glyphs = [new FonGlyph(32, 8, narrow, 100), new FonGlyph(33, 12, wide, 100)]
        };
        var glyphs = GlyfTable.ReadAll(FonOutlineBuilder.Build(strike, TestContext.Current.CancellationToken));

        Assert.Equal(8 * 128, glyphs[1]!.XMax);
        Assert.Equal(12 * 128, glyphs[2]!.XMax);
    }

    [Fact]
    public void Point_count_before_tracing_matches_the_outline()
    {
        var template = Assert.Single(FonReader.Read(Data("Fon-v2-fixed.fon"), TestContext.Current.CancellationToken));
        var random = new Random(561);
        foreach (var (height, width, threshold) in new[] { (8, 8, 2), (13, 17, 4), (32, 32, 7), (32, 32, 10) })
        {
            var pixels = new bool[height, width];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    pixels[y, x] = random.Next(12) < threshold;
            AssertPoints(pixels);
        }
        var checkerboard = new bool[32, 32];
        for (var y = 0; y < 32; y++)
            for (var x = 0; x < 32; x++)
                checkerboard[y, x] = ((x + y) & 1) == 0;
        AssertPoints(checkerboard);
        AssertPoints(new bool[8, 8]);

        void AssertPoints(bool[,] pixels)
        {
            var height = pixels.GetLength(0);
            var width = pixels.GetLength(1);
            var strike = template with
            {
                PixelHeight = height,
                Ascent = height,
                DefaultChar = 0,
                Glyphs = [new FonGlyph(32, width, pixels, 0)]
            };
            var counted = FonOutlineBuilder.CountPoints(pixels, width, 65535, TestContext.Current.CancellationToken);
            var glyph = GlyfTable.ReadAll(FonOutlineBuilder.Build(strike, TestContext.Current.CancellationToken))[1];
            Assert.Equal(counted, glyph?.Points.Length ?? 0);
            if (counted == 0)
                Assert.Null(glyph);
        }
    }

    [Fact]
    public void Aliased_glyphs_at_filewide_pixel_limit_are_accepted()
    {
        var data = Container(AliasedFnt(16, 1024), AliasedFnt(16, 1024));
        var strikes = FonReader.Read(data, TestContext.Current.CancellationToken);
        Assert.Equal(FonReader.MaxGlyphPixels, strikes.Sum(s => s.Glyphs.Sum(g => (long)g.Width * s.PixelHeight)));
        Assert.All(strikes, s => Assert.All(s.Glyphs, g => Assert.False(g.Pixels[0, 0])));
    }

    [Fact]
    public void Zero_width_glyph_rows_count_toward_filewide_budget()
    {
        var atLimit = Container(AliasedFnt(0, 16384, 256));
        var strikes = FonReader.Read(atLimit, TestContext.Current.CancellationToken);
        var strike = Assert.Single(strikes);
        Assert.Equal(256, strike.Glyphs.Count);
        Assert.Equal(FonReader.MaxGlyphPixels, strike.Glyphs.Sum(g => (long)Math.Max(g.Width, 1) * strike.PixelHeight));

        var overLimit = Container(AliasedFnt(0, 16384, 256), AliasedFnt(0, 1, 1));
        AssertRefused(() => FonReader.Read(overLimit, TestContext.Current.CancellationToken),
            "error.fon_too_large", FonReader.MaxGlyphPixels.ToString("N0", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Already_cancelled_token_stops_reader_on_valid_fon()
    {
        var data = Data("Fon-v2-fixed.fon");
        Assert.ThrowsAny<OperationCanceledException>(() => FonReader.Read(data, new CancellationToken(true)));
    }

    [Fact]
    public void Already_cancelled_token_stops_builder_on_legal_strike()
    {
        var strike = Assert.Single(FonReader.Read(Data("Fon-v2-fixed.fon"), TestContext.Current.CancellationToken));
        Assert.ThrowsAny<OperationCanceledException>(() => FonOutlineBuilder.Build(strike, new CancellationToken(true)));
    }

    /// <summary>Cancels its token when the first full pass over the glyphs ends, i.e. after every outline is traced.</summary>
    private sealed class CancelAfterFirstPass(IReadOnlyList<FonGlyph> glyphs, CancellationTokenSource cts) : IReadOnlyList<FonGlyph>
    {
        public FonGlyph this[int index] => glyphs[index];
        public int Count => glyphs.Count;
        public IEnumerator<FonGlyph> GetEnumerator()
        {
            foreach (var glyph in glyphs)
                yield return glyph;
            cts.Cancel();
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void Cancellation_after_tracing_stops_builder_before_serialization()
    {
        var strike = Assert.Single(FonReader.Read(Data("Fon-v2-fixed.fon"), TestContext.Current.CancellationToken));
        using var cts = new CancellationTokenSource();
        var watched = strike with { Glyphs = new CancelAfterFirstPass(strike.Glyphs, cts) };

        Assert.ThrowsAny<OperationCanceledException>(() => FonOutlineBuilder.Build(watched, cts.Token));
        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public void Pixel_height_above_units_per_em_limit_fails_in_reader()
    {
        var data = Container(AliasedFnt(0, 16385, 1));
        AssertRefused(() => FonReader.Read(data, TestContext.Current.CancellationToken), "error.fon_too_large", "16384");
    }

    [Fact]
    public async Task Cancelled_large_legal_strike_stops_builder_and_preserves_outputs()
    {
        var data = Container(AliasedFnt(128, 128, 256));
        var source = Source(data, out var folder);
        File.WriteAllText(Path.Combine(folder, "Name.ttf"), "old");
        var before = Snapshot(folder);
        var allocator = new RecordingAllocator(folder);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var reports = new List<double>();
        var progress = new SyncProgress(value =>
        {
            reports.Add(value);
            if (value == 0.1)
                cts.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FontConverter().ConvertAsync(
            new ConversionStep(source, "fon", "ttf", PresetFor("ttf"), allocator, fx.NewFolder()), progress, cts.Token));
        Assert.Contains(0.1, reports);
        Assert.Empty(allocator.Suffixes);
        Assert.Equal(before.OrderBy(p => p.Key), Snapshot(folder).OrderBy(p => p.Key));
    }

    [Fact]
    public void Later_rt_font_overlapping_nonadjacent_resource_is_rejected()
    {
        var resource = AliasedFnt(8, 8, 1);
        var data = Container(resource, resource, resource);
        var ne = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x3c));
        var table = ne + BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(ne + 0x24));
        var thirdEntry = table + 30 + 2 * 12;
        var thirdStart = FntStart(data) + 1;
        Assert.True(thirdStart + 1 <= FntStart(data, 1));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(thirdEntry), (ushort)thirdStart);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(thirdEntry + 2), 1);

        AssertRefused(() => FonReader.Read(data, TestContext.Current.CancellationToken), "error.fon_damaged");
    }

    [Fact]
    public async Task Queue_precancel_preserves_existing_outputs()
    {
        foreach (var policy in Enum.GetValues<ConflictPolicy>())
        {
            var source = Source(Data("Fon-multi.fon"), out var folder);
            File.WriteAllText(Path.Combine(folder, "Name-13px.ttf"), "old");
            var before = Snapshot(folder);
            await using var queue = new JobQueue(fx.Catalog);
            var job = new ConversionJob([source], PresetFor("ttf", policy), "test");
            job.Cancel();

            await queue.RunAsync(job);

            Assert.Equal(JobState.Cancelled, job.State);
            Assert.Equal(FileState.Cancelled, Assert.Single(job.Files).State);
            Assert.Empty(job.Outputs);
            Assert.Equal(before.OrderBy(p => p.Key), Snapshot(folder).OrderBy(p => p.Key));
        }
    }

    // ───────────────────────── C9: extremes ─────────────────────────

    [Fact]
    public async Task Extreme_heights_preserve_masks_and_overflows_fail()
    {
        var strikes = FonReader.Read(Data("Fon-edge.fon"), TestContext.Current.CancellationToken);
        Assert.Equal([1, 256], strikes.Select(s => s.PixelHeight));
        foreach (var strike in strikes)
        {
            var height = strike.PixelHeight;
            var font = FonOutlineBuilder.Build(strike, TestContext.Current.CancellationToken);
            Assert.Equal(1024, U16(font.Require("head"), 18));
            using var face = SKTypeface.FromStream(new MemoryStream(font.Write()));
            for (var code = 32; code <= 34; code++)
                AssertMask(ExpectedMask(code, 8, height), Render(face, code - 32 + 1, height, ArtAscent(height), 8), $"{height}px code {code}");
        }

        // x=32 at 1024 units per pixel is 32768: past signed 16-bit, an explicit error, never a wrapped coordinate.
        var pixels = new bool[1, 33];
        pixels[0, 32] = true;
        var overflowing = strikes[0] with { Glyphs = [new FonGlyph(32, 33, pixels, 0)], DefaultChar = 0 };
        AssertRefused(() => FonOutlineBuilder.Build(overflowing, TestContext.Current.CancellationToken), "error.fon_too_large", "32767");
        // 31 pixels wide, x=30 lit: every coordinate and advance ends at 31 * 1024 = 31744 and fits.
        pixels = new bool[1, 31];
        pixels[0, 30] = true;
        var fits = FonOutlineBuilder.Build(strikes[0] with { Glyphs = [new FonGlyph(32, 31, pixels, 0)], DefaultChar = 0 }, TestContext.Current.CancellationToken);
        Assert.Equal(31 * 1024, GlyfTable.ReadAll(fits)[1]!.XMax);

        // A bitmap that ends exactly at the end of the resource is read; one byte further is a clear error.
        var data = Data("Fon-v2-fixed.fon");
        var fnt = FntStart(data);
        var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(fnt + 2));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(fnt + 0x76 + 2), (ushort)(size - 13));
        Assert.Single(FonReader.Read(data, TestContext.Current.CancellationToken));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(fnt + 0x76 + 2), (ushort)(size - 12));
        AssertRefused(() => FonReader.Read(data, TestContext.Current.CancellationToken), "error.fon_damaged");

        // Through the queue: an overflowing resource writes nothing.
        var source = Source(Container(WideOnePixelFnt()), out var folder);
        var job = await fx.ConvertAsync([source], PresetFor("ttf"));
        var failed = Assert.Single(job.Files);
        Assert.Equal(FileState.Failed, failed.State);
        Assert.Equal("error.fon_too_large", failed.ErrorKey);
        Assert.Equal("32767", failed.ErrorDetail);
        Assert.Equal(["Name.fon"], Directory.EnumerateFiles(folder).Select(Path.GetFileName));
    }

    // ───────────────────────── C10/C12: routes ─────────────────────────

    [Fact]
    public void Fon_edges_only_leave_existing_font_routes_intact()
    {
        var converter = fx.Converters.OfType<FontConverter>().Single();
        Assert.Equal(Formats.Select(t => ("fon", t)).Order(), converter.Edges.Where(e => e.From == "fon").Select(e => (e.From, e.To)).Order());
        Assert.DoesNotContain(fx.Converters.SelectMany(c => c.Edges), e => e.To == "fon");
        foreach (var from in Formats)
            foreach (var to in Formats.Where(t => t != from))
                Assert.Contains(new ConversionEdge(from, to), converter.Edges);
        Assert.Equal(20 + 5, converter.Edges.Count);

        var planner = fx.Catalog.CreatePlanner();
        foreach (var target in Formats)
        {
            Assert.Equal("font", Assert.Single(planner.Plan("fon", target)!.Steps).Converter.Id);
            Assert.Null(planner.Plan(target, "fon"));
        }
    }

    // ───────────────────────── Edge cases (problem definition, section 6) ─────────────────────────

    [Fact]
    public void Zero_pix_width_uses_each_entry_width()
    {
        var strike = Assert.Single(FonReader.Read(Data("Fon-v3-proportional.fon"), TestContext.Current.CancellationToken));
        Assert.Equal(0, strike.PixelWidth);
        Assert.Equal(0, strike.Glyphs[0].Width);
        Assert.Equal(5, strike.Glyphs[1].Width);
        Assert.Equal(9, strike.Glyphs[2].Width);
        Assert.Equal((13, 5), (strike.Glyphs[1].Pixels.GetLength(0), strike.Glyphs[1].Pixels.GetLength(1)));
        Assert.True(strike.Glyphs[1].Pixels[0, 2]);
        Assert.False(strike.Glyphs[0].Pixels.Cast<bool>().Any());
    }

    [Fact]
    public void Control_bytes_and_delete_keep_glyphs()
    {
        var strike = Assert.Single(FonReader.Read(Data("Fon-v2-proportional.fon"), TestContext.Current.CancellationToken));
        Assert.Equal(128, strike.Glyphs.Count);
        Assert.Equal((byte)0, strike.Glyphs[0].ByteValue);
        Assert.Equal((byte)31, strike.Glyphs[31].ByteValue);
        Assert.Equal((byte)127, strike.Glyphs[127].ByteValue);
        Assert.True(strike.Glyphs[127].Pixels[0, 2]);
    }

    [Theory]
    [InlineData("Fon-v2-fixed.fon", true)]
    [InlineData("Fon-v3-proportional.fon", false)]
    public void Blank_and_zero_width_glyphs_keep_metrics(string sample, bool isFixed)
    {
        var strike = Assert.Single(FonReader.Read(Data(sample), TestContext.Current.CancellationToken));
        var font = FonOutlineBuilder.Build(strike, TestContext.Current.CancellationToken);
        var glyphs = GlyfTable.ReadAll(font);
        Assert.Equal(96 + 1, font.GlyphCount);
        Assert.Null(glyphs[1]); // the blank space, 0 or 8 pixels wide
        Assert.Equal((isFixed ? 8 : 0) * Scale(13), Advance(font, 1));
        Assert.Equal(0, Lsb(font, 1));
        Assert.Equal(1, Cmap(font)[0x20]);
        for (var code = 33; code <= 127; code++)
            Assert.Equal(ArtWidth(code, isFixed) * Scale(13), Advance(font, code - 32 + 1));
    }

    [Fact]
    public void Default_and_break_are_relative_indices()
    {
        var data = Data("Fon-v2-fixed.fon");
        var fnt = FntStart(data);
        data[fnt + 0x61] = 1;
        data[fnt + 0x62] = 2;
        var strike = Assert.Single(FonReader.Read(data, TestContext.Current.CancellationToken));
        Assert.Equal((1, 2, (byte)33), (strike.DefaultChar, strike.BreakChar, strike.Glyphs[strike.DefaultChar].ByteValue));
        // .notdef is the default glyph (code 33, the single pixel); the break glyph (34) is not forced onto U+0020.
        var font = FonOutlineBuilder.Build(strike, TestContext.Current.CancellationToken);
        var glyphs = GlyfTable.ReadAll(font);
        Assert.Equal(glyphs[2]!.Serialize(), glyphs[0]!.Serialize());
        Assert.Equal(Advance(font, 2), Advance(font, 0));
        Assert.Equal(1, Cmap(font)[0x20]);
        Assert.Equal(3, Cmap(font)[0x22]);

        data[fnt + 0x61] = 96;
        AssertRefused(() => FonReader.Read(data, TestContext.Current.CancellationToken), "error.fon_damaged");
    }

    [Theory]
    [InlineData(0x04)]
    [InlineData(0x08)]
    [InlineData(0x10)]
    [InlineData(0x20)]
    [InlineData(0x40)]
    [InlineData(0x80)]
    public void V3_non_six_byte_flags_are_rejected_before_glyph_table(int flag)
    {
        var data = Data($"Fon-v3-flags-{flag:x2}.fon");
        AssertRefused(() => FonReader.Read(data, TestContext.Current.CancellationToken),
            "error.fon_unsupported", $"dfFlags 0x{flag:X8}");
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(3u)]
    [InlineData(5u)]
    public void V3_zero_or_mixed_flags_are_rejected(uint flags)
    {
        var data = Data("Fon-v3-fixed.fon");
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(FntStart(data) + 0x76), flags);
        AssertRefused(() => FonReader.Read(data, TestContext.Current.CancellationToken),
            "error.fon_unsupported", $"dfFlags 0x{flags:X8}");
    }

    [Fact]
    public void Unsupported_containers_charsets_and_resources_fail_as_a_whole()
    {
        var data = Data("Fon-multi.fon");
        var second = FntStart(data, 1);
        data[second + 0x42] = 1;
        AssertRefused(() => FonReader.Read(data, TestContext.Current.CancellationToken), "error.fon_vector");
        data[second + 0x42] = 4;
        AssertRefused(() => FonReader.Read(data, TestContext.Current.CancellationToken), "error.fon_unsupported");
        data[second + 0x42] = 0;
        foreach (var charset in new byte[] { 128, 129, 130, 134, 136, 2, 1, 255, 77, 99 }) // DBCS, SYMBOL, DEFAULT, OEM, MAC, unknown
        {
            data[second + 0x55] = charset;
            AssertRefused(() => FonReader.Read(data, TestContext.Current.CancellationToken),
                "error.fon_unsupported", $"charset {charset}");
        }
        data[second + 0x55] = 0;
        data[second] = 0;
        data[second + 1] = 1;
        AssertRefused(() => FonReader.Read(data, TestContext.Current.CancellationToken), "error.fon_unsupported", "FNT 0x0100");

        var pe = Data("Fon-v2-fixed.fon");
        pe[0x40] = (byte)'P';
        pe[0x41] = (byte)'E';
        AssertRefused(() => FonReader.Read(pe, TestContext.Current.CancellationToken), "error.fon_unsupported", "PE");
        // A bare FNT and an MZ program without fonts.
        var fixedFon = Data("Fon-v2-fixed.fon");
        AssertRefused(() => FonReader.Read(fixedFon[FntStart(fixedFon)..], TestContext.Current.CancellationToken), "error.fon_damaged");
        var program = Data("Fon-v2-fixed.fon");
        var ne = 0x40;
        var table = ne + BinaryPrimitives.ReadUInt16LittleEndian(program.AsSpan(ne + 0x24));
        BinaryPrimitives.WriteUInt16LittleEndian(program.AsSpan(table + 22), 0x8006);
        AssertRefused(() => FonReader.Read(program, TestContext.Current.CancellationToken), "error.fon_damaged");
    }

    [Fact]
    public void Truncated_ne_fnt_and_face_never_produce_outputs()
    {
        var source = Data("Fon-v2-fixed.fon");
        AssertRefused(() => FonReader.Read(source[..0x40], TestContext.Current.CancellationToken), "error.fon_damaged");
        var data = (byte[])source.Clone();
        var ne = 0x40;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(ne + 0x26), 0x7fff);
        AssertRefused(() => FonReader.Read(data, TestContext.Current.CancellationToken), "error.fon_damaged");

        data = (byte[])source.Clone();
        var table = ne + BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(ne + 0x24));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(table + 30), (ushort)table);
        AssertRefused(() => FonReader.Read(data, TestContext.Current.CancellationToken), "error.fon_damaged");

        data = (byte[])source.Clone();
        var fnt = FntStart(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(fnt + 2), 0x7fffffff);
        AssertRefused(() => FonReader.Read(data, TestContext.Current.CancellationToken), "error.fon_damaged");

        data = (byte[])source.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(fnt + 0x69), 0x7fffffff);
        AssertRefused(() => FonReader.Read(data, TestContext.Current.CancellationToken), "error.fon_damaged");

        data = (byte[])source.Clone();
        var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(fnt + 2));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(fnt + 0x69), (uint)(size - 1));
        data[fnt + size - 1] = (byte)'F';
        AssertRefused(() => FonReader.Read(data, TestContext.Current.CancellationToken), "error.fon_damaged");

        data = (byte[])source.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(fnt + 0x76 + 2), 0xffff);
        AssertRefused(() => FonReader.Read(data, TestContext.Current.CancellationToken), "error.fon_damaged");
    }

    [Fact]
    public void Builder_preserves_pixel_outlines_metrics_and_control_glyphs()
    {
        var strike = Assert.Single(FonReader.Read(Data("Fon-v2-proportional.fon"), TestContext.Current.CancellationToken));
        var font = FonOutlineBuilder.Build(strike, TestContext.Current.CancellationToken);
        Assert.Equal(strike.Glyphs.Count + 1, font.GlyphCount);
        Assert.Equal(13 * (1024 / 13), FontTables.UnitsPerEm(font.Require("head")));
        Assert.Equal(0x00010000u, font.Flavor);
        var glyphs = GlyfTable.ReadAll(font);
        var scaled = 1024 / 13;
        for (var i = 0; i < strike.Glyphs.Count; i++)
        {
            var original = strike.Glyphs[i];
            Assert.Equal(original.Width * scaled, Advance(font, i + 1));
            if (!original.Pixels.Cast<bool>().Any())
            {
                Assert.Null(glyphs[i + 1]);
                Assert.Equal(0, Lsb(font, i + 1));
                continue;
            }
            Assert.NotNull(glyphs[i + 1]);
            Assert.Equal(glyphs[i + 1]!.XMin, Lsb(font, i + 1));
        }
        Assert.Equal(glyphs[strike.DefaultChar + 1]?.Serialize(), glyphs[0]?.Serialize());
        Assert.Equal((ushort)0x40, BinaryPrimitives.ReadUInt16BigEndian(font.Require("OS/2").AsSpan(62)));
    }
}
