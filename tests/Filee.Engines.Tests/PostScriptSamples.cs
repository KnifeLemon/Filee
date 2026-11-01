// The PDFs the PostScript writer is tested on, built in code (PostScriptPdfBuilder, and one written by Skia's PDF backend),
// one feature group each, so a failing comparison points at what broke.

using System.Text;
using Filee.Engines.Fonts;
using Filee.Engines.Fonts.Cff;
using ImageMagick;
using PdfSharp.Pdf;
using SkiaSharp;
using static Filee.Engines.Tests.PostScriptPdfBuilder;

namespace Filee.Engines.Tests;

internal static class PostScriptSamples
{
    /// <summary>Every sample by name (PDFs built in code).</summary>
    public static readonly string[] Names =
    [
        "vector", "text-standard", "text-truetype", "text-type1", "text-cff", "text-type3", "images", "shadings",
        "patterns", "rotated", "annotations", "optional-content", "transparency", "skia",
    ];

    public static string Make(string name, string folder) => name switch
    {
        "vector" => Vector(folder),
        "text-standard" => TextStandard(folder),
        "text-truetype" => TextTrueType(folder),
        "text-type1" => TextType1(folder),
        "text-cff" => TextCff(folder),
        "text-type3" => TextType3(folder),
        "images" => Images(folder),
        "shadings" => Shadings(folder),
        "patterns" => Patterns(folder),
        "rotated" => Rotated(folder),
        "annotations" => Annotations(folder),
        "optional-content" => OptionalContent(folder),
        "transparency" => Transparency(folder),
        "skia" => Skia(folder),
        _ => throw new ArgumentException(name),
    };

    private static byte[] FontBytes(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", name));

    /// <summary>Paths: fills in gray, RGB and CMYK, strokes with dashes, caps and joins, curves, even-odd, clipping, a form drawn twice.</summary>
    public static string Vector(string folder)
    {
        using var b = new PostScriptPdfBuilder();
        var form = b.Stream("0.9 0.4 0.1 rg 0 0 m 80 0 l 40 70 l h f 0 0 1 RG 3 w 0 0 80 70 re S",
            ("Type", Name("XObject")), ("Subtype", Name("Form")), ("BBox", b.Numbers(0, 0, 60, 70)),
            ("Matrix", b.Numbers(1, 0, 0, 1, 0, 0)));
        var content = new StringBuilder();
        content.Append("0.2 g 40 700 120 60 re f\n");
        content.Append("0.1 0.6 0.3 rg 180 700 120 60 re f\n");
        content.Append("0 0.8 0.8 0 k 320 700 120 60 re f\n");
        content.Append("1 0 0 RG 6 w 1 J 1 j 40 650 m 200 600 l 360 650 l S\n");
        content.Append("0 0 0 RG 2 w 0 J 0 j [12 6] 0 d 40 580 m 460 580 l S [] 0 d\n");
        content.Append("2 J 10 w 40 540 m 200 540 l S 0 J\n");
        content.Append("0.5 0 0.5 rg 1 w 60 420 m 60 520 160 520 160 420 c 160 380 120 360 y 100 340 60 380 v h B\n");
        content.Append("0 0 1 rg 300 380 m 420 520 l 300 520 l 420 380 l h f*\n");
        content.Append("q 40 200 200 120 re W n 0.8 0.2 0.2 rg 0 0 m 300 300 l 0 300 l h f 0 0.5 0 rg 100 150 m 280 360 l 100 360 l h f Q\n");
        content.Append("q 1 0 0 1 300 200 cm /Fm1 Do 0.5 0 0 0.5 100 0 cm /Fm1 Do Q\n");
        content.Append("q 0.7 0.7 0.2 rg 1 0 0 RG 4 w 300 80 150 80 re b Q\n");
        content.Append("Q Q 0.3 g 40 60 100 40 re f\n"); // unbalanced Q is ignored
        b.Page(500, 800, content.ToString(), b.Resources(("XObject", "Fm1", form)));
        return b.Save(folder, "vector.pdf");
    }

    /// <summary>The standard fonts (not embedded) with character and word spacing, scaling, rise, kerning, stroking and rotation.</summary>
    public static string TextStandard(string folder)
    {
        using var b = new PostScriptPdfBuilder();
        PdfDictionary Std(string baseFont) => b.Object(("Type", Name("Font")), ("Subtype", Name("Type1")), ("BaseFont", Name(baseFont)),
            ("Encoding", Name("WinAnsiEncoding")));
        var resources = b.Resources(("Font", "H", Std("Helvetica")), ("Font", "T", Std("Times-Bold")), ("Font", "C", Std("Courier")));
        var content = new StringBuilder();
        content.Append("BT /H 24 Tf 50 740 Td (Filee PostScript writer) Tj ET\n");
        content.Append("BT /T 18 Tf 50 700 Td 2 Tc (Character spacing) Tj 0 Tc 0 -26 Td 8 Tw (Word spacing here) Tj 0 Tw ET\n");
        content.Append("BT /C 16 Tf 50 620 Td 150 Tz (Wide Courier) Tj 100 Tz 0 -24 Td 60 Tz (Narrow Courier text) Tj 100 Tz ET\n");
        content.Append("BT /H 20 Tf 50 550 Td [(W) 120 (A) 80 (V) -200 (E) 300 (S)] TJ 5 Ts (up) Tj -5 Ts (down) Tj 0 Ts ET\n");
        content.Append("BT /H 36 Tf 1 0 0 RG 1 w 1 Tr 50 480 Td (Stroked) Tj 0 0 1 rg 2 Tr 200 0 Td (Both) Tj 0 Tr ET\n");
        content.Append("BT /T 20 Tf 0.866 0.5 -0.5 0.866 300 300 Tm (Turned 30 degrees) Tj ET\n");
        content.Append("BT /H 14 Tf 16 TL 50 380 Td (First line) Tj T* (Second line) Tj (Third line) ' 4 1 (Fourth line) \" ET\n");
        content.Append("BT /H 14 Tf 3 Tr 50 250 Td (Invisible OCR text) Tj 0 Tr ET\n");
        content.Append("BT /H 14 Tf 0.2 0.6 0.2 rg 50 200 Td (\\(parentheses\\) and \\\\ backslash \\351t\\351) Tj ET\n");
        b.Page(612, 792, content.ToString(), resources);
        return b.Save(folder, "text-standard.pdf");
    }

    /// <summary>
    /// TrueType as a Type 0 font and as a simple font (turned, stroked, spaced), and a CID-keyed CFF font (FontFile3
    /// /CIDFontType0C) as a Type 0 font.
    /// </summary>
    public static string TextTrueType(string folder)
    {
        using var b = new PostScriptPdfBuilder();
        var ttfBytes = FontBytes("FileeTest-TrueType.ttf");
        var ttf = SfntFont.Read(ttfBytes);
        var ttfMap = CmapTable.Read(ttf.Require("cmap"), out _);
        var otf = SfntFont.Read(FontBytes("FileeTest-CID.otf"));
        var otfMap = CmapTable.Read(otf.Require("cmap"), out _);
        var cff = otf.Require("CFF ");
        var charset = CffFont.Parse(cff).Charset!;

        PdfDictionary Type0(string cidType, string fileKey, PdfDictionary program, bool identityMap)
        {
            var descriptor = b.Descriptor("FileeTest", 4, fileKey, program);
            var cidFont = b.Object(("Type", Name("Font")), ("Subtype", Name(cidType)), ("BaseFont", Name("FileeTest")),
                ("CIDSystemInfo", b.Dictionary(("Registry", new PdfString("Adobe")), ("Ordering", new PdfString("Identity")), ("Supplement", Number(0)))),
                ("FontDescriptor", descriptor.Reference!), ("DW", Number(1000)));
            if (identityMap)
                cidFont.Elements["/CIDToGIDMap"] = Name("Identity");
            return b.Object(("Type", Name("Font")), ("Subtype", Name("Type0")), ("BaseFont", Name("FileeTest")), ("Encoding", Name("Identity-H")),
                ("DescendantFonts", b.Array(cidFont.Reference!)));
        }

        var trueTypeFile = b.Stream(ttfBytes, false, ("Length1", Number(ttfBytes.Length)));
        var f1 = Type0("CIDFontType2", "FontFile2", trueTypeFile, identityMap: true);
        var f2 = Type0("CIDFontType0", "FontFile3", b.Stream(cff, false, ("Subtype", Name("CIDFontType0C"))), identityMap: false);
        var simpleDescriptor = b.Descriptor("FileeTest", 32, "FontFile2", trueTypeFile);
        var advance = new Dictionary<int, double>();
        var hmtx = ttf.Require("hmtx");
        var units = FontTables.UnitsPerEm(ttf.Require("head"));
        double Width(int c) => ttfMap.TryGetValue(c, out var g) ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(hmtx.AsSpan(4 * g)) * 1000.0 / units : 500;
        var f3 = b.SimpleFont("TrueType", "FileeTest", Name("WinAnsiEncoding"), simpleDescriptor, c => Width(c));

        string Hex(IEnumerable<int> codes) => string.Concat(codes.Select(c => c.ToString("X4")));
        var gids = Hex("Filee Hamburgefonstiv".Select(c => ttfMap[c]));
        var cids = Hex("한글 변환".Select(c => (int)charset[otfMap[c]]));
        var content = new StringBuilder();
        content.Append($"BT /F1 36 Tf 60 700 Td <{gids}> Tj ET\n");
        content.Append($"BT /F2 40 Tf 60 620 Td <{cids}> Tj ET\n");
        content.Append("BT /F3 30 Tf 60 540 Td (Filee \\301\\341\\351\\305\\347\\321\\374 0123) Tj ET\n");
        content.Append("BT /F3 24 Tf 3 Tc 60 490 Td (Hamburgefonstiv) Tj 0 Tc 8 Tw 0 -30 Td (Filee is fun) Tj 0 Tw ET\n");
        content.Append("BT /F3 40 Tf 0.6 0 0 RG 1.2 w 1 Tr 60 360 Td (Hamburg) Tj 0 0 0.6 rg 2 Tr 220 0 Td (fonts) Tj 0 Tr ET\n");
        content.Append("BT /F3 28 Tf 0.866 0.5 -0.5 0.866 330 200 Tm [(Filee) -500 (turned)] TJ ET\n");
        content.Append($"BT /F1 20 Tf 0 1 -1 0 560 300 Tm 150 Tz <{gids}> Tj ET\n");
        b.Page(612, 792, content.ToString(), b.Resources(("Font", "F1", f1), ("Font", "F2", f2), ("Font", "F3", f3)));
        return b.Save(folder, "text-truetype.pdf");
    }

    /// <summary>A simple font with an embedded Type 1 program (subroutines, flex, seac).</summary>
    public static string TextType1(string folder)
    {
        using var b = new PostScriptPdfBuilder();
        var program = b.Type1File(Type1Program("FileeTestType1"));
        var descriptor = b.Descriptor("FileeTestType1", 32, "FontFile", program);
        var encoding = b.Dictionary(("Differences", b.Array(Number(65), Name("A"), Name("square"), Name("bowl"), Name("flexed"),
            Name("Aacute"), Name("acute"))));
        var font = b.SimpleFont("Type1", "FileeTestType1", encoding, descriptor, c => c switch
        {
            65 or 69 => 620,
            66 => 600,
            67 => 640,
            68 => 700,
            70 => 400,
            _ => 300,
        });
        var content = "BT /F1 60 Tf 40 600 Td (ABCDEF) Tj ET\nBT /F1 30 Tf 0.8 0 0 rg 40 450 Td (A B C D E F) Tj ET\n"
            + "BT /F1 90 Tf 0 0 0.7 rg 40 250 Td (EDCBA) Tj ET\n";
        b.Page(612, 792, content, b.Resources(("Font", "F1", font)));
        return b.Save(folder, "text-type1.pdf");
    }

    /// <summary>A simple font with a bare CFF program (FontFile3 /Type1C): seac, flex and subroutines.</summary>
    public static string TextCff(string folder)
    {
        using var b = new PostScriptPdfBuilder();
        var otf = SfntFont.Read(FontBytes("FileeTest-NameKeyed.otf"));
        var cff = otf.Require("CFF ");
        var program = b.Stream(cff, false, ("Subtype", Name("Type1C")));
        var descriptor = b.Descriptor("FileeTestNameKeyed", 4, "FontFile3", program);
        var encoding = b.Dictionary(("Type", Name("Encoding")), ("Differences",
            b.Array(Number(65), Name("A"), Name("a"), Name("e"), Name("acute"), Name("Aacute"), Name("flexbar"), Name("box"))));
        var widths = new Dictionary<int, double> { [65] = 640, [66] = 560, [67] = 560, [68] = 400, [69] = 640, [70] = 700, [71] = 600 };
        var font = b.SimpleFont("Type1", "FileeTestNameKeyed", encoding, descriptor, c => widths.GetValueOrDefault(c, 250));
        var content = "BT /F1 64 Tf 40 600 Td (ABCDEFG) Tj ET\nBT /F1 32 Tf 0 0.4 0.8 rg 40 450 Td (GFEDCBA ABC) Tj ET\n";
        b.Page(612, 792, content, b.Resources(("Font", "F1", font)));
        return b.Save(folder, "text-cff.pdf");
    }

    /// <summary>A Type 3 font: a coloured glyph (d0), an uncoloured one (d1) and a bitmap glyph (an inline image mask).</summary>
    public static string TextType3(string folder)
    {
        using var b = new PostScriptPdfBuilder();
        var colored = b.Stream("1000 0 d0 1 0 0 rg 100 0 800 800 re f 0 0 1 rg 300 200 400 400 re f");
        var plain = b.Stream("1000 0 0 0 1000 1000 d1 500 0 m 1000 1000 l 0 1000 l h f");
        var bitmap = b.Stream("1000 0 0 0 1000 1000 d1 q 1000 0 0 1000 0 0 cm BI /IM true /W 8 /H 8 /BPC 1 ID \x18\x3C\x7E\xFF\xFF\x7E\x3C\x18 EI Q");
        var procs = b.Dictionary(("red", b.Ref(colored)), ("tri", b.Ref(plain)), ("dot", b.Ref(bitmap)));
        var encoding = b.Dictionary(("Type", Name("Encoding")), ("Differences", b.Array(Number(97), Name("red"), Name("tri"), Name("dot"))));
        var font = b.Object(("Type", Name("Font")), ("Subtype", Name("Type3")), ("FontBBox", b.Numbers(0, 0, 1000, 1000)),
            ("FontMatrix", b.Numbers(0.001, 0, 0, 0.001, 0, 0)), ("CharProcs", procs), ("Encoding", encoding),
            ("FirstChar", Number(97)), ("LastChar", Number(99)), ("Widths", b.Numbers(1100, 1100, 1100)),
            ("Resources", b.Dictionary()));
        var content = "BT /T3 48 Tf 50 650 Td (abcabc) Tj ET\nBT 0 0.6 0 rg /T3 24 Tf 50 550 Td (bbccaa) Tj ET\n";
        b.Page(612, 792, content, b.Resources(("Font", "T3", font)));
        return b.Save(folder, "text-type3.pdf");
    }

    /// <summary>Pixels of a test picture: a gradient with a few shapes.</summary>
    public static byte[] Pixels(int width, int height, int components)
    {
        var data = new byte[width * height * components];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = (y * width + x) * components;
                var inCircle = (x - width / 2.0) * (x - width / 2.0) + (y - height / 2.0) * (y - height / 2.0) < width * height / 10.0;
                var r = (byte)(x * 255 / width);
                var g = (byte)(y * 255 / height);
                var bl = (byte)(inCircle ? 230 : 40);
                if (components == 1)
                {
                    data[at] = (byte)((r + g + bl) / 3);
                }
                else
                {
                    data[at] = r;
                    data[at + 1] = g;
                    data[at + 2] = bl;
                }
            }
        }
        return data;
    }

    /// <summary>Pictures: Flate RGB, gray with a PNG predictor, Indexed, JPEG, a stencil mask, a colour key and a binary soft mask, inline.</summary>
    public static string Images(string folder)
    {
        using var b = new PostScriptPdfBuilder();
        var rgb = b.Stream(Pixels(64, 48, 3), false, ("Type", Name("XObject")), ("Subtype", Name("Image")), ("Width", Number(64)),
            ("Height", Number(48)), ("ColorSpace", Name("DeviceRGB")), ("BitsPerComponent", Number(8)));

        // gray with PNG "Up" prediction
        var grayPixels = Pixels(40, 40, 1);
        var predicted = new List<byte>();
        for (var y = 0; y < 40; y++)
        {
            predicted.Add(2);
            for (var x = 0; x < 40; x++)
                predicted.Add((byte)(grayPixels[y * 40 + x] - (y > 0 ? grayPixels[(y - 1) * 40 + x] : 0)));
        }
        var gray = b.Stream([.. predicted], false, ("Type", Name("XObject")), ("Subtype", Name("Image")), ("Width", Number(40)),
            ("Height", Number(40)), ("ColorSpace", Name("DeviceGray")), ("BitsPerComponent", Number(8)),
            ("DecodeParms", b.Dictionary(("Predictor", Number(12)), ("Colors", Number(1)), ("Columns", Number(40)))));

        // 4-bit Indexed
        var palette = new byte[16 * 3];
        for (var i = 0; i < 16; i++)
            (palette[3 * i], palette[3 * i + 1], palette[3 * i + 2]) = ((byte)(i * 16), (byte)(255 - i * 16), (byte)(i % 2 * 200));
        var indices = new byte[16 * 16 / 2];
        for (var i = 0; i < indices.Length; i++)
            indices[i] = (byte)((i % 16) << 4 | (15 - i % 16));
        var indexedSpace = b.Array(Name("Indexed"), Name("DeviceRGB"), Number(15), new PdfString(Encoding.Latin1.GetString(palette), PdfStringEncoding.RawEncoding));
        var indexed = b.Stream(indices, false, ("Type", Name("XObject")), ("Subtype", Name("Image")), ("Width", Number(16)),
            ("Height", Number(16)), ("ColorSpace", indexedSpace), ("BitsPerComponent", Number(4)));

        // JPEG
        using var magick = new MagickImage(Pixels(80, 60, 3), new PixelReadSettings(80, 60, StorageType.Char, PixelMapping.RGB));
        magick.Quality = 92;
        var jpeg = b.Stream(magick.ToByteArray(MagickFormat.Jpeg), true, ("Type", Name("XObject")), ("Subtype", Name("Image")), ("Width", Number(80)),
            ("Height", Number(60)), ("ColorSpace", Name("DeviceRGB")), ("BitsPerComponent", Number(8)), ("Filter", Name("DCTDecode")));

        // a stencil mask: a ring
        var ring = new byte[32 * 32 / 8];
        for (var y = 0; y < 32; y++)
        {
            for (var x = 0; x < 32; x++)
            {
                var d = Math.Sqrt((x - 15.5) * (x - 15.5) + (y - 15.5) * (y - 15.5));
                if (d is > 8 and < 14)
                    ring[y * 4 + x / 8] |= (byte)(0x80 >> (x % 8)); // 1 = not painted with the default Decode
            }
        }
        var stencil = b.Stream(ring, false, ("Type", Name("XObject")), ("Subtype", Name("Image")), ("Width", Number(32)),
            ("Height", Number(32)), ("ImageMask", new PdfBoolean(true)));

        // colour key: blue-ish pixels left out
        var keyed = b.Stream(Pixels(64, 48, 3), false, ("Type", Name("XObject")), ("Subtype", Name("Image")), ("Width", Number(64)),
            ("Height", Number(48)), ("ColorSpace", Name("DeviceRGB")), ("BitsPerComponent", Number(8)),
            ("Mask", b.Numbers(0, 255, 0, 255, 200, 255)));

        // a binary soft mask (0 or 255 only)
        var alpha = new byte[64 * 48];
        for (var i = 0; i < alpha.Length; i++)
            alpha[i] = (byte)((i % 64 / 8 + i / 64 / 8) % 2 == 0 ? 255 : 0);
        var smask = b.Stream(alpha, false, ("Type", Name("XObject")), ("Subtype", Name("Image")), ("Width", Number(64)),
            ("Height", Number(48)), ("ColorSpace", Name("DeviceGray")), ("BitsPerComponent", Number(8)));
        var soft = b.Stream(Pixels(64, 48, 3), false, ("Type", Name("XObject")), ("Subtype", Name("Image")), ("Width", Number(64)),
            ("Height", Number(48)), ("ColorSpace", Name("DeviceRGB")), ("BitsPerComponent", Number(8)), ("SMask", smask.Reference!));

        var resources = b.Resources(("XObject", "Rgb", rgb), ("XObject", "Gray", gray), ("XObject", "Idx", indexed), ("XObject", "Jpg", jpeg),
            ("XObject", "Ring", stencil), ("XObject", "Key", keyed), ("XObject", "Soft", soft));
        var inline = new StringBuilder("q 160 0 0 120 400 80 cm BI /W 4 /H 3 /CS /RGB /BPC 8 ID ");
        var inlinePixels = Pixels(4, 3, 3);
        var content = new StringBuilder();
        content.Append("q 200 150 0 0 0 cm 0 0 0 0 0 0 cm Q\n"); // a degenerate matrix draws nothing
        content.Append("q 200 150 0 0 40 600 cm /Rgb Do Q\n");
        content.Append("q 150 150 0 0 300 600 cm /Gray Do Q\n");
        content.Append("q 120 120 0 0 40 420 cm /Idx Do Q\n");
        content.Append("q 200 150 0 0 200 400 cm /Jpg Do Q\n");
        content.Append("q 0.8 0.1 0.1 rg 120 120 0 0 440 420 cm /Ring Do Q\n");
        content.Append("0.9 0.9 0.2 rg 40 220 200 150 re f q 200 150 0 0 40 220 cm /Key Do Q\n");
        content.Append("0.2 0.2 0.9 rg 300 220 200 150 re f q 200 150 0 0 300 220 cm /Soft Do Q\n");
        var bytes = new List<byte>(Encoding.Latin1.GetBytes(content.ToString() + inline));
        bytes.AddRange(inlinePixels);
        bytes.AddRange(" EI Q\n"u8.ToArray());
        var page = b.Document.AddPage();
        page.Elements["/MediaBox"] = b.Numbers(0, 0, 612, 792);
        page.Elements["/Resources"] = resources;
        page.Elements["/Contents"] = b.Stream([.. bytes]).Reference!;
        return b.Save(folder, "images.pdf");
    }

    /// <summary>Axial, radial, function-based and mesh shadings, a shading pattern, a Separation colour with a calculator tint.</summary>
    public static string Shadings(string folder)
    {
        using var b = new PostScriptPdfBuilder();
        var exponential = b.Dictionary(("FunctionType", Number(2)), ("Domain", b.Numbers(0, 1)), ("C0", b.Numbers(1, 0.2, 0)),
            ("C1", b.Numbers(0, 0.3, 1)), ("N", Number(1)));
        var axial = b.Object(("ShadingType", Number(2)), ("ColorSpace", Name("DeviceRGB")), ("Coords", b.Numbers(40, 0, 260, 0)),
            ("Function", exponential), ("Extend", b.Array(new PdfBoolean(true), new PdfBoolean(true))));
        var stitching = b.Dictionary(("FunctionType", Number(3)), ("Domain", b.Numbers(0, 1)), ("Bounds", b.Numbers(0.5)),
            ("Encode", b.Numbers(0, 1, 0, 1)), ("Functions", b.Array(
                b.Dictionary(("FunctionType", Number(2)), ("Domain", b.Numbers(0, 1)), ("C0", b.Numbers(1, 1, 0)), ("C1", b.Numbers(1, 0, 0)), ("N", Number(1))),
                b.Dictionary(("FunctionType", Number(2)), ("Domain", b.Numbers(0, 1)), ("C0", b.Numbers(1, 0, 0)), ("C1", b.Numbers(0.2, 0, 0.4)), ("N", Number(2))))));
        var radial = b.Object(("ShadingType", Number(3)), ("ColorSpace", Name("DeviceRGB")), ("Coords", b.Numbers(400, 640, 10, 420, 660, 110)),
            ("Function", stitching), ("Extend", b.Array(new PdfBoolean(false), new PdfBoolean(true))));
        var calculator = b.Stream("{ 2 copy mul 3 1 roll add 2 div add 2 div }", ("FunctionType", Number(4)), ("Domain", b.Numbers(0, 1, 0, 1)),
            ("Range", b.Numbers(0, 1)));
        var functionBased = b.Object(("ShadingType", Number(1)), ("ColorSpace", Name("DeviceGray")), ("Domain", b.Numbers(0, 1, 0, 1)),
            ("Matrix", b.Numbers(200, 0, 0, 120, 40, 360)), ("Function", calculator));
        var tint = b.Stream("{ dup 0.8 mul 1 exch sub exch dup 0.1 mul 1 exch sub exch 0.9 mul 1 exch sub }", ("FunctionType", Number(4)), ("Domain", b.Numbers(0, 1)),
            ("Range", b.Numbers(0, 1, 0, 1, 0, 1)));
        var separation = b.Array(Name("Separation"), Name("FileeGreen"), Name("DeviceRGB"), tint.Reference!);
        var sepShading = b.Object(("ShadingType", Number(2)), ("ColorSpace", separation), ("Coords", b.Numbers(300, 0, 560, 0)),
            ("Function", b.Dictionary(("FunctionType", Number(2)), ("Domain", b.Numbers(0, 1)), ("C0", b.Numbers(0)), ("C1", b.Numbers(1)), ("N", Number(1)))));
        // free-form triangles: flag, x, y (16-bit over 0..612 / 0..792), r g b
        var mesh = new List<byte>();
        void Vertex(int flag, double x, double y, byte r, byte g, byte bl)
        {
            mesh.Add((byte)flag);
            var xv = (int)(x / 612 * 65535);
            var yv = (int)(y / 792 * 65535);
            mesh.AddRange([(byte)(xv >> 8), (byte)xv, (byte)(yv >> 8), (byte)yv, r, g, bl]);
        }
        Vertex(0, 60, 60, 255, 0, 0);
        Vertex(0, 260, 60, 0, 255, 0);
        Vertex(0, 160, 230, 0, 0, 255);
        Vertex(1, 300, 230, 255, 255, 0);
        var free = b.Stream([.. mesh], false, ("ShadingType", Number(4)), ("ColorSpace", Name("DeviceRGB")), ("BitsPerCoordinate", Number(16)),
            ("BitsPerComponent", Number(8)), ("BitsPerFlag", Number(8)), ("Decode", b.Numbers(0, 612, 0, 792, 0, 1, 0, 1, 0, 1)));
        var shadingPattern = b.Object(("PatternType", Number(2)), ("Shading", axial.Reference!), ("Matrix", b.Numbers(1, 0, 0, 1, 300, 0)));
        var resources = b.Resources(("Shading", "Ax", axial), ("Shading", "Rad", radial), ("Shading", "Fn", functionBased),
            ("Shading", "Sep", sepShading), ("Shading", "Mesh", free), ("Pattern", "P1", shadingPattern));
        resources.Elements["/ColorSpace"] = b.Dictionary(("Green", separation));
        var content = new StringBuilder();
        content.Append("q 40 660 220 100 re W n /Ax sh Q\n");
        content.Append("q 300 540 280 240 re W n /Rad sh Q\n");
        content.Append("q 40 360 200 120 re W n /Fn sh Q\n");
        content.Append("q 300 380 260 120 re W n /Sep sh Q\n");
        content.Append("/Green cs 0.7 scn 300 300 120 60 re f 0.3 scn 440 300 120 60 re f\n");
        content.Append("/Pattern cs /P1 scn 300 120 260 140 re f\n");
        content.Append("/Mesh sh\n");
        b.Page(612, 792, content.ToString(), resources);
        return b.Save(folder, "shadings.pdf");
    }

    /// <summary>A coloured tiling pattern and an uncoloured one painted in two colours.</summary>
    public static string Patterns(string folder)
    {
        using var b = new PostScriptPdfBuilder();
        var colored = b.Stream("1 0 0 rg 0 0 10 10 re f 0 0 1 rg 10 10 10 10 re f 0 0.6 0 RG 1 w 0 20 m 20 0 l S",
            ("Type", Name("Pattern")), ("PatternType", Number(1)), ("PaintType", Number(1)), ("TilingType", Number(1)),
            ("BBox", b.Numbers(0, 0, 20, 20)), ("XStep", Number(20)), ("YStep", Number(20)), ("Resources", b.Dictionary()),
            ("Matrix", b.Numbers(1, 0, 0, 1, 5, 5)));
        var uncolored = b.Stream("1 0 0 rg 0 6 m 8 14 l 16 6 l h f 0 0 m 12 0 l 12 4 l 0 4 l h f",
            ("Type", Name("Pattern")), ("PatternType", Number(1)), ("PaintType", Number(2)), ("TilingType", Number(2)),
            ("BBox", b.Numbers(0, 0, 16, 16)), ("XStep", Number(16)), ("YStep", Number(16)), ("Resources", b.Dictionary()),
            ("Matrix", b.Numbers(0.866, 0.5, -0.5, 0.866, 0, 0)));
        var resources = b.Resources(("Pattern", "C", colored), ("Pattern", "U", uncolored));
        resources.Elements["/ColorSpace"] = b.Dictionary(("PU", b.Array(Name("Pattern"), Name("DeviceRGB"))));
        var content = "/Pattern cs /C scn 40 500 240 240 re f\n/PU cs 0.1 0.5 0.9 /U scn 320 500 240 240 re f\n"
            + "0.9 0.5 0.1 /U scn 40 200 520 240 re f\n/Pattern CS /C SCN 20 w 80 100 m 520 100 l S\n";
        b.Page(612, 792, content, resources);
        return b.Save(folder, "patterns.pdf");
    }

    /// <summary>A page turned by /Rotate 90 whose crop box is smaller than its media box.</summary>
    public static string Rotated(string folder)
    {
        using var b = new PostScriptPdfBuilder();
        var font = b.Object(("Type", Name("Font")), ("Subtype", Name("Type1")), ("BaseFont", Name("Helvetica-Bold")));
        var content = "0.1 0.3 0.8 rg 0 0 612 792 re f 1 g 60 60 480 660 re f 0.8 0 0 rg 100 600 200 100 re f "
            + "BT 0 g /F1 40 Tf 100 400 Td (Turned page) Tj ET 0 0.6 0 RG 8 w 100 150 m 500 300 l S";
        var page = b.Page(612, 792, content, b.Resources(("Font", "F1", font)));
        page.Elements["/Rotate"] = Number(90);
        page.Elements["/CropBox"] = b.Numbers(30, 40, 580, 760);
        var second = b.Page(400, 300, "0.9 0.2 0.2 rg 20 20 200 100 re f BT 0 g /F1 24 Tf 30 200 Td (Upside down) Tj ET", b.Resources(("Font", "F1", font)));
        second.Elements["/Rotate"] = Number(180);
        return b.Save(folder, "rotated.pdf");
    }

    /// <summary>Annotations with appearance streams: one that prints, one hidden, one without the print flag.</summary>
    public static string Annotations(string folder)
    {
        using var b = new PostScriptPdfBuilder();
        PdfDictionary Appearance(string content, double w, double h) => b.Stream(content, ("Type", Name("XObject")), ("Subtype", Name("Form")),
            ("BBox", b.Numbers(0, 0, w, h)));
        var stamp = Appearance("0.9 0.1 0.1 RG 6 w 3 3 194 94 re S 0.9 0.1 0.1 rg 30 30 140 40 re f", 200, 100);
        var hidden = Appearance("0 0 1 rg 0 0 100 100 re f", 100, 100);
        var annotations = b.Array(
            b.Ref(b.Object(("Type", Name("Annot")), ("Subtype", Name("Stamp")), ("Rect", b.Numbers(100, 500, 400, 650)), ("F", Number(4)),
                ("AP", b.Dictionary(("N", stamp.Reference!))))),
            b.Ref(b.Object(("Type", Name("Annot")), ("Subtype", Name("Square")), ("Rect", b.Numbers(100, 200, 200, 300)), ("F", Number(6)),
                ("AP", b.Dictionary(("N", hidden.Reference!))))));
        var page = b.Page(612, 792, "0.5 g 50 50 512 40 re f");
        page.Elements["/Annots"] = annotations;
        return b.Save(folder, "annotations.pdf");
    }

    /// <summary>Content in a layer that is off by default, as marked content and as a form XObject.</summary>
    public static string OptionalContent(string folder)
    {
        using var b = new PostScriptPdfBuilder();
        var on = b.Object(("Type", Name("OCG")), ("Name", new PdfString("Shown")));
        var off = b.Object(("Type", Name("OCG")), ("Name", new PdfString("Hidden")));
        b.Document.Internals.Catalog.Elements["/OCProperties"] = b.Dictionary(("OCGs", b.Array(on.Reference!, off.Reference!)),
            ("D", b.Dictionary(("OFF", b.Array(off.Reference!)))));
        var form = b.Stream("0 0 1 rg 0 0 100 100 re f", ("Type", Name("XObject")), ("Subtype", Name("Form")), ("BBox", b.Numbers(0, 0, 100, 100)),
            ("OC", off.Reference!));
        var resources = b.Resources(("XObject", "Hid", form), ("Properties", "On", on), ("Properties", "Off", off));
        var content = "/OC /On BDC 0 0.6 0 rg 50 600 200 100 re f EMC /OC /Off BDC 1 0 0 rg 300 600 200 100 re f EMC "
            + "q 1 0 0 1 300 300 cm /Hid Do Q 0.3 g 50 300 200 100 re f";
        b.Page(612, 792, content, resources);
        return b.Save(folder, "optional-content.pdf");
    }

    /// <summary>
    /// A PDF written by Skia (Chrome's PDF backend, which Filee's SVG and metafile engines use): subset TrueType and CFF
    /// fonts as Type 0 fonts, gradients as shading patterns, a picture, clipping and dashed strokes; two pages.
    /// </summary>
    public static string Skia(string folder)
    {
        var path = Path.Combine(folder, "skia.pdf");
        using (var stream = File.Create(path))
        using (var document = SKDocument.CreatePdf(stream))
        using (var trueType = SKTypeface.FromFile(Path.Combine(AppContext.BaseDirectory, "Fonts", "FileeTest-TrueType.ttf")))
        using (var cff = SKTypeface.FromFile(Path.Combine(AppContext.BaseDirectory, "Fonts", "FileeTest-CID.otf")))
        {
            using (var canvas = document.BeginPage(595, 842))
            {
                using var gradient = SKShader.CreateLinearGradient(new SKPoint(40, 40), new SKPoint(300, 200),
                    [new SKColor(0x7F, 0x77, 0xDD), new SKColor(0x1D, 0x9E, 0x75)], SKShaderTileMode.Clamp);
                using var paint = new SKPaint { Shader = gradient, IsAntialias = true };
                canvas.DrawRoundRect(new SKRect(40, 40, 300, 200), 16, 16, paint);
                using var radial = SKShader.CreateRadialGradient(new SKPoint(430, 120), 80, [SKColors.Yellow, SKColors.Red], SKShaderTileMode.Clamp);
                using var circle = new SKPaint { Shader = radial, IsAntialias = true };
                canvas.DrawCircle(430, 120, 80, circle);
                using var stroke = new SKPaint
                {
                    Color = new SKColor(0x22, 0x22, 0x22),
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 4,
                    IsAntialias = true,
                    PathEffect = SKPathEffect.CreateDash([18, 8], 0)
                };
                using var curve = new SKPath();
                curve.MoveTo(60, 300);
                curve.CubicTo(160, 220, 300, 380, 520, 260);
                canvas.DrawPath(curve, stroke);
                using var font = new SKFont(trueType, 36);
                using var text = new SKPaint { Color = SKColors.Black, IsAntialias = true };
                canvas.DrawText("Filee Hamburgefonstiv 0123", 50, 420, font, text);
                using var korean = new SKFont(cff, 40);
                canvas.DrawText("한글 변환 Hamburg", 50, 490, korean, text);
                canvas.Save();
                canvas.ClipRect(new SKRect(50, 540, 300, 640));
                using var bitmap = new SKBitmap(64, 48);
                var pixels = Pixels(64, 48, 3);
                for (var y = 0; y < 48; y++)
                    for (var x = 0; x < 64; x++)
                        bitmap.SetPixel(x, y, new SKColor(pixels[(y * 64 + x) * 3], pixels[(y * 64 + x) * 3 + 1], pixels[(y * 64 + x) * 3 + 2]));
                canvas.DrawBitmap(bitmap, new SKRect(30, 520, 330, 745));
                canvas.Restore();
                canvas.DrawText("fine image", 340, 600, new SKFont(trueType, 20), text);
                document.EndPage();
            }
            using (var canvas = document.BeginPage(842, 595))
            {
                using var text = new SKPaint { Color = new SKColor(0x10, 0x40, 0x90), IsAntialias = true };
                canvas.RotateDegrees(-20, 421, 300);
                canvas.DrawText("Filee 0123 Hamburgefonstiv", 120, 300, new SKFont(trueType, 48), text);
                document.EndPage();
            }
            document.Close();
        }
        return path;
    }

    /// <summary>A page with constant alpha (drawn as a picture) next to an opaque one (translated).</summary>
    public static string Transparency(string folder)
    {
        using var b = new PostScriptPdfBuilder();
        var state = b.Object(("Type", Name("ExtGState")), ("ca", Number(0.5)));
        b.Page(612, 792, "1 0 0 rg 100 400 300 300 re f /A gs 0 0 1 rg 250 250 300 300 re f",
            b.Resources(("ExtGState", "A", state)));
        b.Page(612, 792, "0 0.5 0 rg 100 100 400 400 re f");
        return b.Save(folder, "transparency.pdf");
    }
}
