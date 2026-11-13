// The OpenType MATH table of a math font (Cambria Math, STIX Two Math, Latin Modern Math ...), as far as MathLayout
// draws with it: the constants for the math axis and radicals, and the vertical glyph constructions that make
// brackets, braces and radical signs as tall as what they hold. A construction is a list of ever taller variants of
// the glyph and, for heights past the largest, an assembly of parts (ends, middle, extenders repeated as often as
// needed) that overlap at their connectors.
//
// Format: OpenType 1.9, "MATH - The Mathematical Typesetting Table" (MathConstants, MathVariants, Coverage). Fonts
// can come from documents, so every offset is checked; a table that doesn't read counts as none.

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Filee.Engines.Rendering;

namespace Filee.Engines.Typesetting;

/// <summary>One glyph of a stretched construction, <paramref name="Bottom"/> font units above the construction's bottom.</summary>
internal readonly record struct StretchedGlyph(ushort Glyph, float Bottom);

internal sealed class MathTable
{
    /// <summary>Glyphs an assembly may take: far above any delimiter a page holds, and a bound for odd fonts.</summary>
    private const int MaxAssemblyGlyphs = 300;

    private static readonly ConditionalWeakTable<PageFont, Holder> Tables = [];

    private readonly Dictionary<ushort, Construction> _vertical;

    private MathTable(int unitsPerEm, Dictionary<ushort, Construction> vertical)
    {
        UnitsPerEm = unitsPerEm;
        _vertical = vertical;
    }

    public int UnitsPerEm { get; }

    /// <summary>Height of the math axis above the baseline (font units).</summary>
    public int AxisHeight { get; private init; }

    public int RadicalVerticalGap { get; private init; }
    public int RadicalDisplayStyleVerticalGap { get; private init; }
    public int RadicalRuleThickness { get; private init; }
    public int RadicalExtraAscender { get; private init; }
    public int RadicalKernBeforeDegree { get; private init; }
    public int RadicalKernAfterDegree { get; private init; }
    public int RadicalDegreeBottomRaisePercent { get; private init; }

    /// <summary>Overlap two parts of an assembly need at least (font units).</summary>
    public int MinConnectorOverlap { get; private init; }

    private sealed record Holder(MathTable? Table);

    private readonly record struct Part(ushort Glyph, int StartConnector, int EndConnector, int FullAdvance, bool Extender);

    private sealed record Construction(IReadOnlyList<(ushort Glyph, int Advance)> Variants, IReadOnlyList<Part> Assembly);

    /// <summary>The MATH table of <paramref name="font"/>; null when it has none or it doesn't read.</summary>
    public static MathTable? Of(PageFont font) => Tables.GetValue(font, static f => new Holder(Read(f))).Table;

    private static MathTable? Read(PageFont font)
    {
        try
        {
            var sfnt = font.Sfnt;
            if (sfnt["MATH"] is not { } math || sfnt["head"] is not { Length: >= 20 } head)
                return null;
            var unitsPerEm = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(18));
            return unitsPerEm == 0 ? null : Parse(math, unitsPerEm);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IndexOutOfRangeException or OverflowException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Reads a MATH table; throws <see cref="InvalidDataException"/> when an offset points outside it.</summary>
    internal static MathTable? Parse(byte[] data, int unitsPerEm)
    {
        var reader = new Reader(data);
        if (reader.U16(0) != 1)
            return null;
        int constants = reader.U16(4), variants = reader.U16(8);
        if (constants == 0 || variants == 0)
            return null;

        int Value(int offset) => reader.I16(constants + offset);
        var vertical = new Dictionary<ushort, Construction>();
        var coverage = Coverage(reader, variants + reader.U16(variants + 2));
        var count = reader.U16(variants + 6);
        foreach (var (glyph, index) in coverage)
        {
            if (index >= count)
                continue;
            var construction = variants + reader.U16(variants + 10 + index * 2);
            vertical[glyph] = ReadConstruction(reader, construction);
        }
        return new MathTable(unitsPerEm, vertical)
        {
            AxisHeight = Value(12),
            RadicalVerticalGap = Value(188),
            RadicalDisplayStyleVerticalGap = Value(192),
            RadicalRuleThickness = Value(196),
            RadicalExtraAscender = Value(200),
            RadicalKernBeforeDegree = Value(204),
            RadicalKernAfterDegree = Value(208),
            RadicalDegreeBottomRaisePercent = Value(212),
            MinConnectorOverlap = reader.U16(variants),
        };
    }

    private static Construction ReadConstruction(Reader reader, int offset)
    {
        var assemblyOffset = reader.U16(offset);
        var count = reader.U16(offset + 2);
        var variants = new List<(ushort, int)>(count);
        for (var i = 0; i < count; i++)
            variants.Add(((ushort)reader.U16(offset + 4 + i * 4), reader.U16(offset + 6 + i * 4)));
        var parts = new List<Part>();
        if (assemblyOffset != 0)
        {
            var assembly = offset + assemblyOffset;
            var partCount = reader.U16(assembly + 4);
            for (var i = 0; i < partCount; i++)
            {
                var p = assembly + 6 + i * 10;
                parts.Add(new Part((ushort)reader.U16(p), reader.U16(p + 2), reader.U16(p + 4), reader.U16(p + 6), (reader.U16(p + 8) & 1) != 0));
            }
        }
        return new Construction(variants, parts);
    }

    /// <summary>Glyph → coverage index of a Coverage table (format 1: glyphs, format 2: ranges).</summary>
    private static List<(ushort Glyph, int Index)> Coverage(Reader reader, int offset)
    {
        var result = new List<(ushort, int)>();
        var format = reader.U16(offset);
        var count = reader.U16(offset + 2);
        if (format == 1)
        {
            for (var i = 0; i < count; i++)
                result.Add(((ushort)reader.U16(offset + 4 + i * 2), i));
        }
        else if (format == 2)
        {
            for (var i = 0; i < count; i++)
            {
                var r = offset + 4 + i * 6;
                int start = reader.U16(r), end = reader.U16(r + 2), index = reader.U16(r + 4);
                // Ranges may not overlap, so a font covers each of its 65,536 glyphs at most once.
                for (var g = start; g <= end && result.Count <= 0xFFFF; g++)
                    result.Add(((ushort)g, index + g - start));
            }
        }
        return result;
    }

    /// <summary>Whether <paramref name="glyph"/> has taller variants or an assembly.</summary>
    public bool Stretches(ushort glyph) => _vertical.ContainsKey(glyph);

    /// <summary>
    /// <paramref name="glyph"/> made at least <paramref name="height"/> font units tall: itself when it is, else the
    /// first variant that is, else the assembly of its parts (bottom to top), else its largest variant.
    /// </summary>
    /// <param name="inkHeight">The height of a glyph's outline in font units (its variants are measured by advance).</param>
    public IReadOnlyList<StretchedGlyph> Stretch(ushort glyph, float height, Func<ushort, float> inkHeight)
    {
        if (!_vertical.TryGetValue(glyph, out var construction))
            return [new StretchedGlyph(glyph, 0)];
        if (inkHeight(glyph) >= height)
            return [new StretchedGlyph(glyph, 0)];
        foreach (var (variant, advance) in construction.Variants)
        {
            if (advance >= height)
                return [new StretchedGlyph(variant, 0)];
        }
        if (Assemble(construction.Assembly, height) is { } assembled)
            return assembled;
        var largest = construction.Variants.Count > 0 ? construction.Variants[^1].Glyph : glyph;
        return [new StretchedGlyph(largest, 0)];
    }

    /// <summary>
    /// The parts with as many extenders as reach <paramref name="height"/> at the least overlap, then the overlaps
    /// grown evenly (up to what the connectors allow) to end at the height. Null when the parts can't be assembled.
    /// </summary>
    private List<StretchedGlyph>? Assemble(IReadOnlyList<Part> parts, float height)
    {
        if (parts.Count == 0 || parts.Any(p => p.FullAdvance <= 0))
            return null;
        var minOverlap = MinConnectorOverlap;
        List<Part> chosen = [];
        for (var repeats = 0; ; repeats++)
        {
            chosen = [.. parts.SelectMany(p => Enumerable.Repeat(p, p.Extender ? repeats : 1))];
            if (chosen.Count == 0)
                continue;
            var longest = chosen.Sum(p => (float)p.FullAdvance) - (chosen.Count - 1) * minOverlap;
            if (longest >= height || chosen.Count >= MaxAssemblyGlyphs || !parts.Any(p => p.Extender && p.FullAdvance > minOverlap))
                break;
        }
        if (chosen.Count > MaxAssemblyGlyphs)
            chosen = chosen[..MaxAssemblyGlyphs];

        var joins = chosen.Count - 1;
        float overlap = minOverlap;
        if (joins > 0)
        {
            var allowed = Enumerable.Range(0, joins).Min(i => Math.Min(chosen[i].EndConnector, chosen[i + 1].StartConnector));
            var wanted = (chosen.Sum(p => (float)p.FullAdvance) - height) / joins;
            overlap = Math.Max(minOverlap, Math.Min(wanted, Math.Max(allowed, minOverlap)));
        }
        var result = new List<StretchedGlyph>(chosen.Count);
        float bottom = 0;
        foreach (var part in chosen)
        {
            result.Add(new StretchedGlyph(part.Glyph, bottom));
            bottom += part.FullAdvance - overlap;
        }
        return result;
    }

    /// <summary>Big-endian reads that throw <see cref="InvalidDataException"/> outside the table.</summary>
    private readonly struct Reader(byte[] data)
    {
        public int U16(int offset) =>
            offset >= 0 && offset + 2 <= data.Length ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset)) : throw new InvalidDataException("MATH table offset out of range.");

        public int I16(int offset) => (short)U16(offset);
    }
}
