using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Filee.Core.Conversion;

namespace Filee.Engines.Fonts;

/// <summary>Turns a validated monochrome FNT strike into an unhinted TrueType outline font.</summary>
internal static class FonOutlineBuilder
{
    private readonly record struct Corner(int X, int Y);
    private readonly record struct Edge(Corner From, Corner To);

    /// <summary>Scales the strike's pixel height into TrueType font units.</summary>
    internal static int Scale(int height) => height <= 1024 ? 1024 / height : 1;

    public static SfntFont Build(FonStrike strike, int resourceCount = 1, int sameHeightOrdinal = 1) =>
        Build(strike, CancellationToken.None, resourceCount, sameHeightOrdinal);

    /// <summary>
    /// Builds a validated strike, observing <paramref name="cancellationToken"/> for each glyph and raster row,
    /// and at bounded intervals during edge expansion, grouping, contour walking and point compaction.
    /// </summary>
    public static SfntFont Build(FonStrike strike, CancellationToken cancellationToken,
        int resourceCount = 1, int sameHeightOrdinal = 1)
    {
        if (resourceCount < 1 || sameHeightOrdinal < 1 || strike.Glyphs.Count < 1)
            throw new ConversionRefusedException("error.fon_damaged");
        if (strike.Glyphs.Count > 65534)
            throw new ConversionRefusedException("error.fon_too_large", "65535");
        var height = strike.PixelHeight;
        var scale = Scale(height);
        var upem = checked(height * scale);
        if (upem is < 16 or > 16384)
            throw new ConversionRefusedException("error.fon_too_large", "16384");
        var ascent = Signed(strike.Ascent * scale);
        var descent = Signed(-(height - strike.Ascent) * scale);
        var lineGap = Signed(strike.ExternalLeading * scale);

        var glyphs = new List<TrueTypeGlyph?> { null };
        var widths = new List<int> { 0 };
        var cmap = new SortedDictionary<int, int>();
        var outlines = new Dictionary<(long Offset, int Width), TrueTypeGlyph?>();
        var encoding = FonReader.CharSetEncoding(strike.CharSet);
        foreach (var source in strike.Glyphs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var advance = checked(source.Width * scale);
            if (advance > ushort.MaxValue)
                throw new ConversionRefusedException("error.fon_too_large", "65535");
            var key = (source.BitmapOffset, source.Width);
            if (!outlines.TryGetValue(key, out var outline))
            {
                outline = Outline(source, strike.Ascent, scale, cancellationToken);
                outlines.Add(key, outline);
            }
            glyphs.Add(outline);
            widths.Add(advance);
            try
            {
                var text = encoding.GetString([source.ByteValue]);
                if (text.EnumerateRunes().Count() != 1)
                    continue;
                var scalar = Rune.GetRuneAt(text, 0);
                // Windows best-fit tables decode undefined bytes of CP1253/1255/1257/874 to Private Use code points.
                if (scalar.Value > ushort.MaxValue ||
                    Rune.GetUnicodeCategory(scalar) is UnicodeCategory.Control or UnicodeCategory.PrivateUse ||
                    !encoding.GetBytes(text).AsSpan().SequenceEqual([source.ByteValue]))
                    continue;
                cmap.TryAdd(scalar.Value, glyphs.Count - 1);
            }
            catch (DecoderFallbackException) { }
            catch (EncoderFallbackException) { }
        }
        glyphs[0] = glyphs[strike.DefaultChar + 1];
        widths[0] = widths[strike.DefaultChar + 1];
        var serialized = new Dictionary<TrueTypeGlyph, byte[]>(ReferenceEqualityComparer.Instance);
        var (glyf, loca, longLoca) = GlyfTable.Build(
            [.. glyphs.Select(g =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (g is null)
                    return [];
                if (!serialized.TryGetValue(g, out var bytes))
                    serialized.Add(g, bytes = g.Serialize());
                return bytes;
            })], longFormat: false, alignment: 2);
        cancellationToken.ThrowIfCancellationRequested();
        var leftBearings = glyphs.Select(g => g?.XMin ?? (short)0).ToArray();
        var covered = glyphs.Where(g => g is not null).Cast<TrueTypeGlyph>().ToArray();
        var xMin = covered.Length == 0 ? 0 : covered.Min(g => g.XMin);
        var xMax = covered.Length == 0 ? 0 : covered.Max(g => g.XMax);
        var yMin = covered.Length == 0 ? 0 : covered.Min(g => g.YMin);
        var yMax = covered.Length == 0 ? 0 : covered.Max(g => g.YMax);
        var minRight = covered.Length == 0 ? 0 : glyphs.Select((g, i) => (g, i))
            .Where(p => p.g is not null).Min(p => widths[p.i] - p.g!.XMax);
        var maxPoints = covered.Length == 0 ? 0 : covered.Max(g => g.Points.Length);
        var maxContours = covered.Length == 0 ? 0 : covered.Max(g => g.NumberOfContours);

        var weight = strike.Weight;
        var bold = weight >= 700;
        var baseName = resourceCount == 1 ? strike.Face :
            $"{strike.Face} {height}px{(sameHeightOrdinal > 1 ? $"-{sameHeightOrdinal}" : "")}";
        var family = weight is 400 or 700 ? baseName : $"{baseName} Weight{weight}";
        var nameBold = weight == 700;
        var style = (nameBold, strike.Italic) switch
        {
            (true, true) => "Bold Italic",
            (true, false) => "Bold",
            (false, true) => "Italic",
            _ => "Regular"
        };
        var full = $"{family} {style}";
        var psName = PostScriptName(full);
        var font = new SfntFont(SfntFont.TrueTypeFlavor);
        font.Tables["glyf"] = glyf;
        font.Tables["loca"] = loca;
        font.Tables["hmtx"] = Metrics(widths, leftBearings);
        font.Tables["head"] = Head(upem, xMin, yMin, xMax, yMax, longLoca,
            (bold ? 1 : 0) | (strike.Italic ? 2 : 0));
        font.Tables["hhea"] = Hhea(ascent, descent, lineGap, widths.Max(),
            covered.Length == 0 ? 0 : covered.Min(g => g.XMin), minRight,
            covered.Length == 0 ? 0 : covered.Max(g => g.XMax), glyphs.Count);
        font.Tables["maxp"] = Maxp(glyphs.Count, maxPoints, maxContours);
        font.Tables["OS/2"] = Os2(strike, widths, cmap, encoding.CodePage, weight, bold,
            ascent, descent, lineGap, yMin, yMax);
        font.Tables["name"] = Name(family, style, full, psName);
        font.Tables["cmap"] = Cmap(cmap);
        var post = new byte[32];
        Put32(post, 0, 0x00030000);
        Put32(post, 12, strike.Glyphs.Select(g => g.Width).Distinct().Count() == 1 ? 1u : 0u);
        font.Tables["post"] = post;
        return font;
    }

    private static TrueTypeGlyph? Outline(FonGlyph source, int ascent, int scale, CancellationToken cancellationToken)
    {
        var height = source.Pixels.GetLength(0);
        if (source.Pixels.GetLength(1) != source.Width)
            throw new ConversionRefusedException("error.fon_damaged");
        if (source.Width == 0)
            return null;
        if (CountPoints(source.Pixels, source.Width, ushort.MaxValue, cancellationToken) > ushort.MaxValue)
            throw new ConversionRefusedException("error.fon_too_large", "65535");
        // Extend identical horizontal runs into maximal rectangles, then cancel each shared unit edge.
        var active = new Dictionary<(int, int), (int Start, int End)>();
        var rectangles = new List<(int X0, int X1, int Y0, int Y1)>();
        for (var y = 0; y <= height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var runs = new HashSet<(int, int)>();
            if (y < height)
                for (var x = 0; x < source.Width;)
                {
                    if (!source.Pixels[y, x]) { x++; continue; }
                    var start = x;
                    while (x < source.Width && source.Pixels[y, x]) x++;
                    runs.Add((start, x));
                }
            foreach (var (run, interval) in active.ToArray())
                if (!runs.Contains(run))
                {
                    rectangles.Add((run.Item1, run.Item2, interval.Start, y));
                    active.Remove(run);
                }
            foreach (var run in runs)
                if (!active.ContainsKey(run))
                    active[run] = (y, y);
        }
        if (rectangles.Count == 0)
            return null;
        var edges = new HashSet<Edge>();
        var order = new List<Edge>();
        void Add(Corner from, Corner to)
        {
            if (!edges.Remove(new Edge(to, from)))
            {
                var edge = new Edge(from, to);
                edges.Add(edge);
                order.Add(edge);
            }
        }
        var work = 0;
        foreach (var (x0, x1, y0, y1) in rectangles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = x0; x < x1; x++)
            {
                if ((++work & 4095) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                Add(new(x, y0), new(x + 1, y0));
                Add(new(x + 1, y1), new(x, y1));
            }
            for (var y = y0; y < y1; y++)
            {
                if ((++work & 4095) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                Add(new(x1, y), new(x1, y + 1));
                Add(new(x0, y + 1), new(x0, y));
            }
        }
        var outgoing = new Dictionary<Corner, List<Edge>>();
        foreach (var edge in edges)
        {
            if ((++work & 4095) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            if (!outgoing.TryGetValue(edge.From, out var choices))
                outgoing.Add(edge.From, choices = []);
            choices.Add(edge);
        }
        var points = new List<GlyphPoint>();
        var ends = new List<ushort>();
        var cursor = 0;
        while (edges.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (!edges.Contains(order[cursor]))
                cursor++;
            var start = order[cursor++];
            var edge = start;
            var loop = new List<Corner>();
            do
            {
                if ((++work & 4095) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                if (!edges.Remove(edge))
                    throw new ConversionRefusedException("error.fon_damaged");
                loop.Add(edge.From);
                if (edge.To == start.From)
                    break;
                if (!outgoing.TryGetValue(edge.To, out var choices))
                    throw new ConversionRefusedException("error.fon_damaged");
                var dx = edge.To.X - edge.From.X;
                var dy = edge.To.Y - edge.From.Y;
                edge = choices.Where(edges.Contains).OrderByDescending(e =>
                    dx * (e.To.Y - e.From.Y) - dy * (e.To.X - e.From.X)).FirstOrDefault();
                if (edge == default)
                    throw new ConversionRefusedException("error.fon_damaged");
            } while (true);
            for (var i = 0; i < loop.Count; i++)
            {
                if ((++work & 4095) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var prev = loop[(i + loop.Count - 1) % loop.Count];
                var curr = loop[i];
                var next = loop[(i + 1) % loop.Count];
                if ((curr.X - prev.X) * (next.Y - curr.Y) ==
                    (curr.Y - prev.Y) * (next.X - curr.X))
                    continue;
                var x = checked(curr.X * scale);
                var y = checked((ascent - curr.Y) * scale);
                points.Add(new GlyphPoint(Signed(x), Signed(y), true));
            }
            if (points.Count > ushort.MaxValue || ends.Count == short.MaxValue)
                throw new ConversionRefusedException("error.fon_too_large", points.Count > ushort.MaxValue ? "65535" : "32767");
            ends.Add((ushort)(points.Count - 1));
        }
        var result = new TrueTypeGlyph
        {
            NumberOfContours = (short)ends.Count,
            Points = [.. points],
            EndPoints = [.. ends]
        };
        (result.XMin, result.YMin, result.XMax, result.YMax) = result.ComputeBounds();
        return result;
    }

    internal static int CountPoints(bool[,] pixels, int width, int limit, CancellationToken cancellationToken)
    {
        var height = pixels.GetLength(0);
        var points = 0;
        for (var y = 0; y <= height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x <= width; x++)
            {
                var a = x > 0 && y > 0 && pixels[y - 1, x - 1];
                var b = x < width && y > 0 && pixels[y - 1, x];
                var c = x > 0 && y < height && pixels[y, x - 1];
                var d = x < width && y < height && pixels[y, x];
                var on = (a ? 1 : 0) + (b ? 1 : 0) + (c ? 1 : 0) + (d ? 1 : 0);
                if (on is 1 or 3)
                    points++;
                else if (on == 2 && ((a && d) || (b && c)))
                    points += 2;
                if (points > limit)
                    return points;
            }
        }
        return points;
    }

    private static byte[] Head(int upem, int xMin, int yMin, int xMax, int yMax, bool longLoca, int style)
    {
        var b = new byte[54];
        Put32(b, 0, 0x00010000); Put32(b, 4, 0x00010000);
        Put32(b, 12, 0x5F0F3CF5); Put16(b, 16, 3); Put16(b, 18, upem);
        Put16(b, 36, xMin); Put16(b, 38, yMin); Put16(b, 40, xMax); Put16(b, 42, yMax);
        Put16(b, 44, style); Put16(b, 46, 8); Put16(b, 48, 2); Put16(b, 50, longLoca ? 1 : 0);
        return b;
    }

    private static byte[] Hhea(int asc, int desc, int gap, int maxAdvance,
        int minLsb, int minRsb, int extent, int count)
    {
        var b = new byte[36];
        Put32(b, 0, 0x00010000);
        Put16(b, 4, asc); Put16(b, 6, desc); Put16(b, 8, gap);
        Put16(b, 10, maxAdvance); Put16(b, 12, Signed(minLsb));
        Put16(b, 14, Signed(minRsb)); Put16(b, 16, Signed(extent));
        Put16(b, 18, 1); Put16(b, 34, count);
        return b;
    }

    private static byte[] Maxp(int count, int points, int contours)
    {
        var b = new byte[32];
        Put32(b, 0, 0x00010000);
        Put16(b, 4, count); Put16(b, 6, points); Put16(b, 8, contours);
        Put16(b, 14, 1); // maxZones: no twilight zone
        return b;
    }

    private static byte[] Metrics(List<int> widths, short[] bearings)
    {
        var b = new byte[widths.Count * 4];
        for (var i = 0; i < widths.Count; i++)
        {
            Put16(b, i * 4, widths[i]);
            Put16(b, i * 4 + 2, bearings[i]);
        }
        return b;
    }

    private static byte[] Os2(FonStrike strike, List<int> widths, SortedDictionary<int, int> cmap,
        int cp, int weight, bool bold, int asc, int desc, int gap, int yMin, int yMax)
    {
        var b = new byte[86];
        Put16(b, 0, 1);
        Put16(b, 2, Signed((int)Math.Round(widths.Skip(1).Average())));
        Put16(b, 4, weight); Put16(b, 6, 5);
        // The PANOSE field is left zero (no invented classification).
        foreach (var scalar in cmap.Keys)
        {
            var bit = scalar switch
            {
                <= 0x007F => 0,
                <= 0x00FF => 1,
                <= 0x017F => 2,
                <= 0x024F => 3,
                <= 0x02AF => 4,
                <= 0x02FF => 5,
                >= 0x0300 and <= 0x036F => 6,
                >= 0x0370 and <= 0x03FF => 7,
                >= 0x0400 and <= 0x052F => 9,
                >= 0x0590 and <= 0x05FF => 11,
                >= 0x0600 and <= 0x06FF => 13,
                >= 0x0E00 and <= 0x0E7F => 24,
                >= 0x1E00 and <= 0x1EFF => 29,
                >= 0x2000 and <= 0x206F => 31,
                >= 0x20A0 and <= 0x20CF => 33,
                >= 0x2100 and <= 0x214F => 35,
                _ => -1
            };
            if (bit >= 0)
                Put32(b, 42 + bit / 32 * 4, Get32(b, 42 + bit / 32 * 4) | (1u << (bit % 32)));
        }
        b[58] = (byte)'F'; b[59] = (byte)'I'; b[60] = (byte)'L'; b[61] = (byte)'E';
        Put16(b, 62, (strike.Italic ? 1 : 0) | (bold ? 0x20 : 0) |
            (!strike.Italic && !bold ? 0x40 : 0));
        Put16(b, 64, cmap.Count == 0 ? 0 : cmap.Keys.First());
        Put16(b, 66, cmap.Count == 0 ? 0 : cmap.Keys.Last());
        Put16(b, 68, asc); Put16(b, 70, desc); Put16(b, 72, gap);
        Put16(b, 74, Math.Max(0, Math.Max(asc, yMax)));
        Put16(b, 76, Math.Max(0, -Math.Min(desc, yMin)));
        var cpBit = cp == 874 ? 16 : cp switch
        {
            1252 => 0,
            1250 => 1,
            1251 => 2,
            1253 => 3,
            1254 => 4,
            1255 => 5,
            1256 => 6,
            1257 => 7,
            1258 => 8,
            _ => throw new ConversionRefusedException("error.fon_damaged")
        };
        Put32(b, 78, 1u << cpBit);
        return b;
    }

    private static byte[] Name(string family, string style, string full, string psName)
    {
        var names = new SortedDictionary<int, string>
        {
            [1] = family,
            [2] = style,
            [3] = psName,
            [4] = full,
            [5] = "Version 1.0",
            [6] = psName
        };
        var records = (from platform in new[] { 0, 3 }
                       from pair in names
                       select (Platform: platform, Encoding: platform == 0 ? 3 : 1,
                           Language: platform == 0 ? 0 : 0x409, Id: pair.Key,
                           Bytes: Encoding.BigEndianUnicode.GetBytes(pair.Value))).ToArray();
        var w = new BigEndianWriter();
        w.U16(0); w.U16(records.Length); w.U16(6 + records.Length * 12);
        var offset = 0;
        foreach (var record in records)
        {
            if (record.Bytes.Length > ushort.MaxValue || offset > ushort.MaxValue - record.Bytes.Length)
                throw new ConversionRefusedException("error.fon_too_large", "65535");
            w.U16(record.Platform); w.U16(record.Encoding); w.U16(record.Language);
            w.U16(record.Id); w.U16(record.Bytes.Length); w.U16(offset);
            offset += record.Bytes.Length;
        }
        foreach (var record in records) w.Bytes(record.Bytes);
        return w.ToArray();
    }

    private static string PostScriptName(string full)
    {
        var bytes = Encoding.UTF8.GetBytes(full);
        var tokens = bytes.Select(b =>
            b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or
                >= (byte)'0' and <= (byte)'9' or (byte)'-'
                ? ((char)b).ToString() : $"_{b:X2}").ToArray();
        var name = string.Concat(tokens);
        if (name.Length <= 63) return name;
        var prefix = new StringBuilder();
        foreach (var token in tokens)
        {
            if (prefix.Length + token.Length > 50) break;
            prefix.Append(token);
        }
        return $"{prefix}-{Convert.ToHexString(SHA256.HashData(bytes).AsSpan(0, 6))}";
    }

    private static byte[] Cmap(SortedDictionary<int, int> map)
    {
        var count = map.Count + 1;
        var length = 16 + 8 * count;
        var sub = new byte[length];
        Put16(sub, 0, 4); Put16(sub, 2, length);
        Put16(sub, 6, count * 2);
        var pow = 1;
        var selector = 0;
        while (pow * 2 <= count) { pow *= 2; selector++; }
        Put16(sub, 8, pow * 2); Put16(sub, 10, selector); Put16(sub, 12, count * 2 - pow * 2);
        var i = 0;
        foreach (var (scalar, gid) in map)
        {
            Put16(sub, 14 + i * 2, scalar);
            Put16(sub, 16 + count * 2 + i * 2, scalar);
            Put16(sub, 16 + count * 4 + i * 2, (gid - scalar) & 0xffff);
            i++;
        }
        Put16(sub, 14 + i * 2, 0xffff);
        Put16(sub, 16 + count * 2 + i * 2, 0xffff);
        Put16(sub, 16 + count * 4 + i * 2, 1);
        var w = new BigEndianWriter();
        w.U16(0); w.U16(2);
        w.U16(0); w.U16(3); w.U32(20);
        w.U16(3); w.U16(1); w.U32(20);
        w.Bytes(sub);
        return w.ToArray();
    }

    private static short Signed(int value) =>
        value is >= short.MinValue and <= short.MaxValue
            ? (short)value : throw new ConversionRefusedException("error.fon_too_large", "32767");

    private static void Put16(byte[] b, int offset, int value) =>
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(offset), checked((ushort)(value & 0xffff)));

    private static void Put32(byte[] b, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(offset), value);

    private static uint Get32(byte[] b, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(offset));
}
