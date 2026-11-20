// Text shaping for the layout engine: HarfBuzz turns a run of text in one font into glyph IDs with advances, offsets
// and the cluster each glyph belongs to, which is what the page model's GlyphRun takes (Rendering/PageContent.cs).
// Arabic joining, Indic conjuncts and reordered vowel signs, Thai marks and Hangul jamo composition all happen here.
//
// Generalised from Vector/GlyphOutlines.cs (which shapes SVG text for outlines):
// - Results are in font units (HarfBuzz scaled to the font's units per em), so one shaping serves every size; they are
//   cached by font, features, script, direction, language and the run's text with its context.
// - A run is shaped inside its paragraph: up to five characters on either side go to HarfBuzz as context, so a word
//   split into two runs (a colour or font change) still joins (Arabic) and reorders (Indic) as one. The context is part
//   of the cache key.
// - HarfBuzz reads the font from PageFont.Data, pinned for as long as HarfBuzz's face lives: memory HarfBuzz doesn't
//   own must outlive it (Blob.FromStream let HarfBuzz read freed memory in the PDF backend spike).
// - Clusters are HarfBuzz's default (monotone graphemes): marks join their base's cluster, and the clusters run in
//   visual order (descending for right-to-left runs), as PageContent expects.
//
// Variable fonts shape at their default instance: HarfBuzzSharp has no binding for variation coordinates, and the PDF
// writer subsets the default instance too (see FontFace.Variable).

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Filee.Engines.Rendering;
using HarfBuzzSharp;
using SkiaSharp;
using Buffer = HarfBuzzSharp.Buffer;
using HbFont = HarfBuzzSharp.Font;
using HbScript = HarfBuzzSharp.Script;

namespace Filee.Engines.Typesetting;

internal enum TextDirection
{
    LeftToRight,
    RightToLeft,
}

/// <summary>An OpenType feature set for a whole run ("liga" off, "smcp" on, "salt" = 2).</summary>
internal readonly record struct FontFeature(string Tag, uint Value = 1)
{
    public override string ToString() => $"{Tag}={Value}";
}

/// <summary>How a run is shaped; null members are taken from the text.</summary>
/// <param name="Direction">The run's direction from the bidirectional algorithm; null: that of <see cref="Script"/>.</param>
/// <param name="Script">Null: the first script of the run other than Common and Inherited.</param>
/// <param name="Language">BCP 47 language ("ko", "sr", "zh-Hant"): selects language forms (OpenType locl).</param>
internal sealed record ShapeOptions(
    TextDirection? Direction = null,
    UnicodeScript? Script = null,
    string? Language = null,
    IReadOnlyList<FontFeature>? Features = null)
{
    public static readonly ShapeOptions Default = new();
}

/// <summary>
/// A shaped run in font units (multiply by size / <see cref="UnitsPerEm"/>). The arrays hold one entry per glyph in
/// visual order. <see cref="Clusters"/> are UTF-16 offsets from the start of the run. The arrays are shared with the
/// shaper's cache and must not be changed.
/// </summary>
internal sealed class ShapedRun
{
    internal ShapedRun(int unitsPerEm, TextDirection direction, int length, ushort[] glyphs, int[] clusters, int[] xAdvances,
        int[] yAdvances, int[] xOffsets, int[] yOffsets, bool[] unsafeToBreak)
    {
        UnitsPerEm = unitsPerEm;
        Direction = direction;
        Length = length;
        Glyphs = glyphs;
        Clusters = clusters;
        XAdvances = xAdvances;
        YAdvances = yAdvances;
        XOffsets = xOffsets;
        YOffsets = yOffsets;
        UnsafeToBreak = unsafeToBreak;
        Advance = xAdvances.Sum();
    }

    public int UnitsPerEm { get; }

    public TextDirection Direction { get; }

    /// <summary>Length of the shaped text in UTF-16 code units.</summary>
    public int Length { get; }

    public ushort[] Glyphs { get; }

    /// <summary>Per glyph: the UTF-16 offset from the run start where its cluster starts.</summary>
    public int[] Clusters { get; }

    public int[] XAdvances { get; }

    public int[] YAdvances { get; }

    /// <summary>Horizontal offset of each glyph from its pen position.</summary>
    public int[] XOffsets { get; }

    /// <summary>Vertical offset of each glyph from its pen position, upwards (HarfBuzz's convention).</summary>
    public int[] YOffsets { get; }

    /// <summary>
    /// Per glyph: breaking the line before this glyph's cluster changes the shaping, so the text on either side must
    /// be shaped again (HarfBuzz's UNSAFE_TO_BREAK).
    /// </summary>
    public bool[] UnsafeToBreak { get; }

    /// <summary>Total horizontal advance.</summary>
    public int Advance { get; }

    /// <summary>The run's width at <paramref name="size"/> points (or any unit the size is in).</summary>
    public float Width(float size) => Advance * size / UnitsPerEm;

    /// <summary>
    /// The run as the page model draws it, the pen starting at <paramref name="origin"/> (page coordinates, y down)
    /// and moving right; <paramref name="text"/> is the run's text.
    /// </summary>
    /// <param name="scaleX">Horizontal scale of glyphs and advances (한글 장평, Word w:w), 1 = as designed.</param>
    public GlyphRun ToGlyphRun(PageFont font, float size, SKPoint origin, string text, float scaleX = 1)
    {
        if (text.Length != Length)
            throw new ArgumentException("The text is not the text of the run.", nameof(text));
        var scale = size / UnitsPerEm;
        var positions = new SKPoint[Glyphs.Length];
        float x = origin.X, y = origin.Y;
        for (var i = 0; i < Glyphs.Length; i++)
        {
            positions[i] = new SKPoint(x + XOffsets[i] * scale * scaleX, y - YOffsets[i] * scale);
            x += XAdvances[i] * scale * scaleX;
            y -= YAdvances[i] * scale;
        }
        return new GlyphRun(font, size, Glyphs, positions, text, Clusters) { ScaleX = scaleX };
    }
}

/// <summary>Vertical and decoration metrics of a font in font units (y up: descenders are negative).</summary>
/// <param name="Ascender">The typographic ascender HarfBuzz uses (OS/2 typo with USE_TYPO_METRICS, else hhea).</param>
/// <param name="WinAscent">OS/2 usWinAscent (Word's line height on Windows comes from the Win metrics).</param>
/// <param name="WinDescent">OS/2 usWinDescent, as a positive distance below the baseline.</param>
/// <param name="EastAsian">
/// The font is for a Chinese, Japanese or Korean code page (OS/2 ulCodePageRange1 bits 17–21: 932, 936, 949, 950, 1361).
/// </param>
/// <param name="Math">The font has a MATH table (Cambria Math): its Win metrics reach far out for big operators.</param>
internal sealed record FontMetrics(
    int UnitsPerEm, int Ascender, int Descender, int LineGap, int WinAscent, int WinDescent, int XHeight, int CapHeight,
    int UnderlinePosition, int UnderlineThickness, int StrikeoutPosition, int StrikeoutThickness,
    int SuperscriptYOffset, int SuperscriptYSize, int SubscriptYOffset, int SubscriptYSize, bool EastAsian = false, bool Math = false);

/// <summary>Shapes runs with HarfBuzz and caches the results; one per layout job (not thread-safe).</summary>
internal sealed class Shaper : IDisposable
{
    /// <summary>Characters of context HarfBuzz looks at on either side of a run (HB_BUFFER_CONTEXT_LENGTH).</summary>
    private const int ContextLength = 5;

    /// <summary>Shaped runs kept; the cache starts over when it is full.</summary>
    private const int MaxCached = 20_000;

    private readonly Dictionary<PageFont, HarfBuzzFont> _fonts = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ShapeKey, ShapedRun> _cache = [];

    /// <summary>Runs shaped by HarfBuzz so far (cache misses), for tests.</summary>
    internal int Shaped { get; private set; }

    /// <summary>Shapes all of <paramref name="text"/>.</summary>
    public ShapedRun Shape(PageFont font, string text, ShapeOptions? options = null) => Shape(font, text, 0, text.Length, options);

    /// <summary>
    /// Shapes <c>text[start..start+length]</c> with the characters around it as context; clusters are counted from
    /// <paramref name="start"/>.
    /// </summary>
    public ShapedRun Shape(PageFont font, string text, int start, int length, ShapeOptions? options = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start + length, text.Length);
        options ??= ShapeOptions.Default;
        var script = options.Script ?? ScriptOf(text, start, length);
        var direction = options.Direction ?? DirectionOf(script);
        var language = options.Language ?? "";
        var features = options.Features is { Count: > 0 } list ? string.Join(",", list) : "";

        var contextStart = ContextStart(text, start);
        var contextEnd = ContextEnd(text, start + length);
        // The text's start and end (BOT / EOT) only when the run reaches them: else the context stands for the rest.
        var key = new ShapeKey(font, text[contextStart..contextEnd], start - contextStart, length, start == 0,
            start + length == text.Length, direction, script, language, features);
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var run = Run(For(font), key, options.Features);
        if (_cache.Count >= MaxCached)
            _cache.Clear();
        _cache[key] = run;
        Shaped++;
        return run;
    }

    /// <summary>The font's vertical and decoration metrics.</summary>
    public FontMetrics Metrics(PageFont font)
    {
        var hb = For(font);
        var metrics = hb.Font.OpenTypeMetrics;
        int Get(OpenTypeMetricsTag tag) => metrics.TryGetPosition(tag, out var value) ? value : 0;
        hb.Font.TryGetHorizontalFontExtents(out var extents);
        return new FontMetrics(hb.UnitsPerEm, extents.Ascender, extents.Descender, extents.LineGap,
            Get(OpenTypeMetricsTag.HorizontalClippingAscent), Get(OpenTypeMetricsTag.HorizontalClippingDescent),
            Get(OpenTypeMetricsTag.XHeight), Get(OpenTypeMetricsTag.CapHeight),
            Get(OpenTypeMetricsTag.UnderlineOffset), Get(OpenTypeMetricsTag.UnderlineSize),
            Get(OpenTypeMetricsTag.StrikeoutOffset), Get(OpenTypeMetricsTag.StrikeoutSize),
            Get(OpenTypeMetricsTag.SuperScriptEmYOffset), Get(OpenTypeMetricsTag.SuperScriptEmYSize),
            Get(OpenTypeMetricsTag.SubScriptEmYOffset), Get(OpenTypeMetricsTag.SubScriptEmYSize), hb.EastAsian, hb.Math);
    }

    private static ShapedRun Run(HarfBuzzFont hb, ShapeKey key, IReadOnlyList<FontFeature>? features)
    {
        using var buffer = new Buffer();
        buffer.AddUtf16(key.Text, key.Start, key.Length);
        buffer.Direction = key.Direction == TextDirection.RightToLeft ? Direction.RightToLeft : Direction.LeftToRight;
        buffer.Script = HbScript.TryParse(key.Script.IsoCode(), out var script) ? script : HbScript.Common;
        if (key.Language.Length > 0)
            buffer.Language = new Language(key.Language);
        var flags = BufferFlags.Default;
        if (key.BeginningOfText)
            flags |= BufferFlags.BeginningOfText;
        if (key.EndOfText)
            flags |= BufferFlags.EndOfText;
        buffer.Flags = flags;
        hb.Font.Shape(buffer, features is { Count: > 0 } ? [.. features.Select(f => new Feature(Tag.Parse(f.Tag), f.Value))] : []);

        var infos = buffer.GetGlyphInfoSpan();
        var positions = buffer.GetGlyphPositionSpan();
        var count = infos.Length;
        var glyphs = new ushort[count];
        var clusters = new int[count];
        int[] xAdvances = new int[count], yAdvances = new int[count], xOffsets = new int[count], yOffsets = new int[count];
        var unsafeToBreak = new bool[count];
        for (var i = 0; i < count; i++)
        {
            glyphs[i] = (ushort)infos[i].Codepoint;
            clusters[i] = (int)infos[i].Cluster - key.Start;
            unsafeToBreak[i] = (infos[i].GlyphFlags & GlyphFlags.UnsafeToBreak) != 0;
            xAdvances[i] = positions[i].XAdvance;
            yAdvances[i] = positions[i].YAdvance;
            xOffsets[i] = positions[i].XOffset;
            yOffsets[i] = positions[i].YOffset;
        }
        return new ShapedRun(hb.UnitsPerEm, key.Direction, key.Length, glyphs, clusters, xAdvances, yAdvances, xOffsets, yOffsets, unsafeToBreak);
    }

    /// <summary>The first script of the run other than Common and Inherited; Common when it has none.</summary>
    internal static UnicodeScript ScriptOf(string text, int start, int length)
    {
        for (var i = start; i < start + length; i++)
        {
            var codePoint = UnicodeData.CodePointAt(text, i);
            if (codePoint > 0xFFFF)
                i++;
            if (UnicodeData.GetScript(codePoint) is not (UnicodeScript.Common or UnicodeScript.Inherited) and var script)
                return script;
        }
        return UnicodeScript.Common;
    }

    /// <summary>The horizontal direction a script is written in.</summary>
    internal static TextDirection DirectionOf(UnicodeScript script) =>
        HbScript.TryParse(script.IsoCode(), out var hb) && hb.HorizontalDirection == Direction.RightToLeft
            ? TextDirection.RightToLeft
            : TextDirection.LeftToRight;

    /// <summary>Where the context before <paramref name="start"/> begins: up to five code points.</summary>
    private static int ContextStart(string text, int start)
    {
        var position = start;
        for (var n = 0; n < ContextLength && position > 0; n++)
            position -= position >= 2 && char.IsSurrogatePair(text[position - 2], text[position - 1]) ? 2 : 1;
        return position;
    }

    /// <summary>Where the context after <paramref name="end"/> ends: up to five code points.</summary>
    private static int ContextEnd(string text, int end)
    {
        var position = end;
        for (var n = 0; n < ContextLength && position < text.Length; n++)
            position += position + 1 < text.Length && char.IsSurrogatePair(text[position], text[position + 1]) ? 2 : 1;
        return position;
    }

    private HarfBuzzFont For(PageFont font)
    {
        if (!_fonts.TryGetValue(font, out var hb))
            _fonts[font] = hb = new HarfBuzzFont(font);
        return hb;
    }

    public void Dispose()
    {
        foreach (var font in _fonts.Values)
            font.Dispose();
        _fonts.Clear();
        _cache.Clear();
    }

    /// <summary>What a result needs: <c>Text</c> is the run with its context, the run is at <c>Start</c>.</summary>
    private readonly record struct ShapeKey(PageFont Font, string Text, int Start, int Length, bool BeginningOfText, bool EndOfText,
        TextDirection Direction, UnicodeScript Script, string Language, string Features)
    {
        public bool Equals(ShapeKey other) => ReferenceEquals(Font, other.Font) && Text == other.Text && Start == other.Start
            && Length == other.Length && BeginningOfText == other.BeginningOfText && EndOfText == other.EndOfText
            && Direction == other.Direction && Script == other.Script && Language == other.Language && Features == other.Features;

        public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(Font), Text, Start, Length,
            Direction, Script, Language, Features);
    }

    /// <summary>A HarfBuzz font over a PageFont's data, which stays pinned while HarfBuzz may read it.</summary>
    private sealed class HarfBuzzFont : IDisposable
    {
        /// <summary>OS/2 ulCodePageRange1 bits 17–21: code pages 932, 936, 949, 950 and 1361.</summary>
        private const uint EastAsianCodePages = 0x1Fu << 17;

        private readonly Blob _blob;
        private readonly Face _face;

        public HarfBuzzFont(PageFont font)
        {
            var data = font.Data;
            var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
            // HarfBuzz calls the release delegate when it destroys the blob, after the face and font that use it.
            _blob = new Blob(pin.AddrOfPinnedObject(), data.Length, MemoryMode.ReadOnly, () => pin.Free());
            _blob.MakeImmutable();
            _face = new Face(_blob, font.FaceIndex);
            UnitsPerEm = _face.UnitsPerEm;
            Font = new HbFont(_face);
            Font.SetFunctionsOpenType();
            Font.SetScale(UnitsPerEm, UnitsPerEm);
        }

        public HbFont Font { get; }

        public int UnitsPerEm { get; }

        /// <summary>See <see cref="FontMetrics.EastAsian"/>; read from the OS/2 table on first use.</summary>
        public bool EastAsian => _eastAsian ??= CodePages();

        /// <summary>See <see cref="FontMetrics.Math"/>.</summary>
        public bool Math => _math ??= HasTable("MATH");

        private bool? _eastAsian, _math;

        private bool HasTable(string tag)
        {
            using var table = _face.ReferenceTable(Tag.Parse(tag));
            return table.Length > 0;
        }

        private bool CodePages()
        {
            using var table = _face.ReferenceTable(Tag.Parse("OS/2"));
            var os2 = table.AsSpan();
            // ulCodePageRange1 is at offset 78 from version 1 on.
            if (os2.Length < 82 || BinaryPrimitives.ReadUInt16BigEndian(os2) < 1)
                return false;
            return (BinaryPrimitives.ReadUInt32BigEndian(os2[78..]) & EastAsianCodePages) != 0;
        }

        public void Dispose()
        {
            Font.Dispose();
            _face.Dispose();
            _blob.Dispose();
        }
    }
}
