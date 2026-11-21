// Fonts for the PDF/A tests: Fonts/FileeTest-TrueType.ttf (a small Noto Sans subset, see Fonts/OFL.txt) as an embedded
// PDF font with the widths it really has, and as the only "installed" font of a font collection, so font embedding is
// tested the same on every computer.

using System.Text;
using Filee.Engines.Fonts;
using Filee.Engines.Rendering;
using Filee.Engines.Typesetting;

namespace Filee.Engines.Tests;

internal static class PdfATestFonts
{
    /// <summary>The family name the test collection lists the test font under.</summary>
    public const string Family = "Filee Sans";

    private static readonly Lazy<byte[]> Bytes = new(() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", "FileeTest-TrueType.ttf")));

    private static readonly Lazy<SfntFont> Sfnt = new(() => SfntFont.Read(Bytes.Value));

    /// <summary>The advance of <paramref name="c"/> in 1/1000 em, 0 when the font lacks it.</summary>
    public static double Advance(char c)
    {
        var map = CmapTable.Read(Sfnt.Value.Require("cmap"), out _);
        if (!map.TryGetValue(c, out var glyph))
            return 0;
        var hmtx = Sfnt.Value.Require("hmtx");
        var metrics = FontTables.NumberOfMetrics(Sfnt.Value.Require("hhea"));
        var advance = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(hmtx.AsSpan(Math.Min(glyph, metrics - 1) * 4));
        return Math.Round(advance * 1000.0 / FontTables.UnitsPerEm(Sfnt.Value.Require("head")), 3);
    }

    /// <summary>A Widths array for codes <paramref name="first"/> … <paramref name="last"/> (WinAnsi codes below 128 are the characters), plus <paramref name="extra"/>.</summary>
    public static string Widths(int first, int last, double extra = 0) =>
        "[" + string.Join(' ', Enumerable.Range(first, last - first + 1).Select(c => (Advance((char)c) + (Advance((char)c) > 0 ? extra : 0))
            .ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]";

    /// <summary>The test font as an embedded non-symbolic TrueType font of WinAnsi codes 32–126.</summary>
    public static string TrueTypeFont(int descriptor, double extraWidth = 0, string encoding = "/Encoding /WinAnsiEncoding") =>
        $"<< /Type /Font /Subtype /TrueType /BaseFont /FileeTest-TrueType /FirstChar 32 /LastChar 126 /Widths {Widths(32, 126, extraWidth)} " +
        $"{encoding} /FontDescriptor {descriptor} 0 R >>";

    public static string Descriptor(string name, int program, int flags = 32) =>
        $"<< /Type /FontDescriptor /FontName /{name} /Flags {flags} /FontBBox [-200 -250 1100 1000] /ItalicAngle 0 /Ascent 1069 " +
        $"/Descent -293 /CapHeight 714 /StemV 80 /FontFile2 {program} 0 R >>";

    /// <summary>The font program as a stream object.</summary>
    public static string Program() => RawPdf.Stream($"/Length1 {Bytes.Value.Length}", Encoding.Latin1.GetString(Bytes.Value));

    /// <summary>The test font under <see cref="Family"/>, as the one installed font.</summary>
    public static IFontCollection Sans { get; } = new Collection(true);

    /// <summary>No fonts installed.</summary>
    public static IFontCollection None { get; } = new Collection(false);

    /// <summary>The test font also under these names (as if it were Helvetica: its widths are not Helvetica's).</summary>
    public static IFontCollection As(params string[] families) => new Collection(true, families);

    private sealed class Collection(bool sans, string[]? others = null) : IFontCollection
    {
        private readonly Lazy<FontFace> _face = new(() =>
        {
            var font = PageFont.FromData(Bytes.Value);
            var map = CmapTable.Read(font.Sfnt.Require("cmap"), out _);
            return new FontFace(PdfATestFonts.Family, 400, false, map.ContainsKey, () => font);
        });

        public IReadOnlyList<FontFace> Family(string name) =>
            sans && (string.Equals(name, PdfATestFonts.Family, StringComparison.OrdinalIgnoreCase) || others?.Contains(name, StringComparer.OrdinalIgnoreCase) == true)
                ? [_face.Value] : [];

        public FontFace? ForCharacter(int codePoint, FontStyle style, string? language) => null;
    }
}
