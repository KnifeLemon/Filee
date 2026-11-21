// Filee's own JPEG XR decoder (JpegXr/): pictures written in the tests by Windows' own encoder (WIC) in the formats and
// with the options XPS printers and screenshots use (lossless and lossy, every overlap mode, 4:2:0 / 4:2:2 / 4:4:4,
// spatial and frequency order, tiles, planar and interleaved alpha, grey), decoded by Filee and compared with what
// Windows' own decoder makes of the same file. The code tables are checked to be complete prefix codes, and damaged
// or oversized files to be refused quickly, on every platform.

using System.Diagnostics;
using Filee.Engines.JpegXr;
using Filee.Engines.Wic;
using ImageMagick;

namespace Filee.Engines.Tests;

public class JpegXrDecoderTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string WindowsOnly = "The test pictures are written with the Windows Imaging Component.";

    /// <summary>A picture with smooth gradients, hard edges and fine detail, so every band carries something.</summary>
    private static (byte R, byte G, byte B, byte A) Pixel(int x, int y, int width, int height)
    {
        var r = (byte)(x * 255 / Math.Max(1, width - 1));
        var g = (byte)(y * 255 / Math.Max(1, height - 1));
        var b = (byte)((x / 7 + y / 5) % 2 == 0 ? 40 : 220);
        if ((x - width / 2) * (x - width / 2) + (y - height / 2) * (y - height / 2) < width * height / 16)
            (r, g, b) = (250, 250, 30);
        var a = (byte)(x < width / 3 ? 255 : x < 2 * width / 3 ? 128 : (y * 4) % 256);
        return (r, g, b, a);
    }

    private static byte[] Pixels(int width, int height, int channels, bool premultiply = false)
    {
        var pixels = new byte[width * height * channels];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b, a) = Pixel(x, y, width, height);
                if (premultiply)
                    (r, g, b) = ((byte)(r * a / 255), (byte)(g * a / 255), (byte)(b * a / 255));
                var at = (y * width + x) * channels;
                switch (channels)
                {
                    case 1:
                        pixels[at] = (byte)((r * 3 + g * 6 + b) / 10);
                        break;
                    case 3:
                        (pixels[at], pixels[at + 1], pixels[at + 2]) = (b, g, r);
                        break;
                    default:
                        (pixels[at], pixels[at + 1], pixels[at + 2], pixels[at + 3]) = (b, g, r, a);
                        break;
                }
            }
        }
        return pixels;
    }

    /// <summary>Codec options (Quality, Overlap, Subsampling, order, tiles) apply only with UseCodecOptions.</summary>
    private const string Codec = "UseCodecOptions=true;";

    public static TheoryData<string, int, int, string> Encodings => new()
    {
        // name, width, height, options as "Name=value;…" (values: true/false, f for float, b for byte, s for ushort)
        { "lossless", 67, 45, "Lossless=true" },
        { "image-quality", 80, 48, "ImageQuality=0.8f" },
        { "low-quality", 64, 64, "ImageQuality=0.1f" },
        { "default", 33, 17, "" },
        { "overlap0", 64, 32, Codec + "Quality=20b;Subsampling=3b;Overlap=0b" },
        { "overlap1", 80, 48, Codec + "Quality=40b;Subsampling=3b;Overlap=1b" },
        { "overlap2", 70, 50, Codec + "Quality=60b;Subsampling=3b;Overlap=2b" },
        { "spatial", 80, 64, Codec + "Quality=20b;FrequencyOrder=false;Overlap=2b" },
        { "soft-tiles", 160, 96, Codec + "Quality=30b;HorizontalTileSlices=2s;VerticalTileSlices=1s;Overlap=2b" },
        { "spatial-tiles", 128, 112, Codec + "Quality=10b;HorizontalTileSlices=1s;VerticalTileSlices=2s;FrequencyOrder=false;Overlap=1b" },
        { "yuv420", 96, 64, Codec + "Quality=20b;Subsampling=1b;Overlap=1b" },
        { "yuv420-overlap2", 96, 66, Codec + "Quality=50b;Subsampling=1b;Overlap=2b" },
        { "yuv422-overlap2", 98, 48, Codec + "Quality=20b;Subsampling=2b;Overlap=2b" },
        { "yuv420-tiles", 128, 96, Codec + "Quality=20b;Subsampling=1b;Overlap=2b;HorizontalTileSlices=1s;VerticalTileSlices=1s" },
    };

    private static Dictionary<string, object> Options(string text)
    {
        var options = new Dictionary<string, object>();
        foreach (var item in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var (name, value) = (item[..item.IndexOf('=')], item[(item.IndexOf('=') + 1)..]);
            options[name] = value switch
            {
                "true" => true,
                "false" => false,
                _ when value.EndsWith('f') => float.Parse(value[..^1], System.Globalization.CultureInfo.InvariantCulture),
                _ when value.EndsWith('b') => byte.Parse(value[..^1], System.Globalization.CultureInfo.InvariantCulture),
                _ when value.EndsWith('s') => ushort.Parse(value[..^1], System.Globalization.CultureInfo.InvariantCulture),
                _ => value,
            };
        }
        return options;
    }

    /// <summary>Filee's decoding next to Windows' of the same file: the largest difference of a channel.</summary>
    private static int Difference(string path, out JpegXrImage ours) => Difference(path, out ours, out _);

    private static int Difference(string path, out JpegXrImage ours, out byte[] reference)
    {
        ours = JpegXrDecoder.Decode(File.ReadAllBytes(path), 1L << 26);
        reference = [];
        if (!OperatingSystem.IsWindows())
            return 0;
        using var theirs = WicImageReader.Read(path);
        Assert.Equal(((uint)ours.Width, (uint)ours.Height), (theirs.Width, theirs.Height));
        using var pixels = theirs.GetPixels();
        reference = pixels.ToByteArray(PixelMapping.BGRA)!;
        var max = 0;
        for (var i = 0; i < reference.Length; i++)
        {
            if (!ours.HasAlpha && i % 4 == 3)
                continue;
            max = Math.Max(max, Math.Abs(reference[i] - ours.Bgra[i]));
        }
        return max;
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public void Rgb_pictures_decode_as_windows_decodes_them(string name, int width, int height, string options)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var path = Path.Combine(fx.NewFolder(), name + ".jxr");
        var source = Pixels(width, height, 3);
        JpegXrBuilder.Write(path, width, height, JpegXrBuilder.Bgr24, source, width * 3, Options(options));
        var difference = Difference(path, out var picture, out var reference);
        Assert.Equal((width, height), (picture.Width, picture.Height));
        Assert.False(picture.HasAlpha);
        if (!name.StartsWith("yuv42", StringComparison.Ordinal))
        {
            // The decoding process is exact: every sample as Windows decodes it.
            Assert.True(difference == 0, $"{name}: off by {difference}");
            return;
        }
        // Chroma upsampling is the one step the spec leaves to the decoder (Windows' differs from its example), so
        // 4:2:x pictures are held to the source: as close to it as Windows' own decoding is.
        Assert.True(Error(picture.Bgra, source) <= Error(reference, source) + 0.5, $"{name}: {Error(picture.Bgra, source):F2} from the source, Windows {Error(reference, source):F2}");
    }

    /// <summary>The mean difference of a BGRA picture's colour channels from a BGR source.</summary>
    private static double Error(byte[] bgra, byte[] bgr)
    {
        double sum = 0;
        for (var p = 0; p < bgr.Length / 3; p++)
        {
            for (var c = 0; c < 3; c++)
                sum += Math.Abs(bgra[p * 4 + c] - bgr[p * 3 + c]);
        }
        return sum / bgr.Length;
    }

    [Theory]
    [InlineData("interleaved", "UseCodecOptions=true;InterleavedAlpha=true;Quality=20b;AlphaQuality=10b")]
    [InlineData("planar", "UseCodecOptions=true;InterleavedAlpha=false;Quality=20b;AlphaQuality=10b")]
    [InlineData("planar-lossless", "InterleavedAlpha=false;Lossless=true")]
    public void Alpha_planes_decode_interleaved_and_planar(string name, string options)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        const int Width = 72, Height = 40;
        var path = Path.Combine(fx.NewFolder(), name + ".jxr");
        JpegXrBuilder.Write(path, Width, Height, JpegXrBuilder.Bgra32, Pixels(Width, Height, 4), Width * 4, Options(options));
        var difference = Difference(path, out var picture);
        Assert.True(picture.HasAlpha);
        Assert.True(difference == 0, $"{name}: off by {difference}");
        // Interleaved: an alpha plane inside the codestream (ALPHA_IMAGE_PLANE_FLAG); planar: a codestream of its own.
        var file = File.ReadAllBytes(path);
        var flags = file[CodestreamStart(file) + 10];
        Assert.Equal(name == "interleaved", (flags & 1) != 0);
        // The middle third is half transparent.
        Assert.InRange(picture.Bgra[(20 * Width + Width / 2) * 4 + 3], 120, 136);
    }

    [Fact]
    public void Grey_and_premultiplied_pictures_decode()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var folder = fx.NewFolder();
        var grey = Path.Combine(folder, "grey.jxr");
        JpegXrBuilder.Write(grey, 50, 30, JpegXrBuilder.Gray8, Pixels(50, 30, 1), 50, Options("ImageQuality=0.9f"));
        Assert.True(Difference(grey, out var picture) <= 0);
        Assert.False(picture.HasAlpha);
        Assert.Equal(picture.Bgra[0], picture.Bgra[2]);

        var premultiplied = Path.Combine(folder, "pbgra.jxr");
        JpegXrBuilder.Write(premultiplied, 48, 32, JpegXrBuilder.Pbgra32, Pixels(48, 32, 4, premultiply: true), 48 * 4, Options("Lossless=true"));
        var result = JpegXrDecoder.Decode(File.ReadAllBytes(premultiplied), 1L << 26);
        Assert.True(result.HasAlpha);
        // Straight colour again: the opaque left third keeps its red ramp, the half-transparent middle its yellow disc.
        var (r, _, _, a) = Pixel(24, 16, 48, 32);
        var o = (16 * 48 + 24) * 4;
        Assert.InRange(result.Bgra[o + 3], a - 2, a + 2);
        Assert.InRange(result.Bgra[o + 2], r - 4, r + 4);
    }

    [Fact]
    public void An_hdr_picture_is_brought_to_srgb()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var path = Path.Combine(fx.NewFolder(), "hdr.jxr");
        // Linear 0.2158 is sRGB 128; values above 1 are clipped to white.
        JpegXrBuilder.WriteHdr(path, 32, 32, (x, _) => x < 16 ? (0.2158f, 0.2158f, 0.2158f) : (4f, 4f, 4f));
        var picture = JpegXrDecoder.Decode(File.ReadAllBytes(path), 1L << 26);
        Assert.InRange(picture.Bgra[(8 * 32 + 4) * 4], 124, 132);
        Assert.Equal(255, picture.Bgra[(8 * 32 + 24) * 4]);
    }

    [Fact]
    public void A_minimal_codestream_decodes_on_every_platform()
    {
        var picture = JpegXrDecoder.Decode(JpegXrTestFile.DcOnlyGrey(), 1 << 20);
        Assert.Equal((16, 16), (picture.Width, picture.Height));
        Assert.All(Enumerable.Range(0, 256), p => Assert.Equal(128, picture.Bgra[p * 4 + 1]));
    }

    [Fact]
    public void Code_tables_are_complete_prefix_codes()
    {
        var tables = new List<JpegXrVlc>
        {
            JpegXrTables.ValDcYuv, JpegXrTables.CbplpYuv444, JpegXrTables.CbplpYuv42x, JpegXrTables.ChrCbphp,
            JpegXrTables.NumChBlk, JpegXrTables.RefCbphp1, JpegXrTables.RunIndex, JpegXrTables.IndexB,
        };
        tables.AddRange(JpegXrTables.AbsLevelIndex);
        tables.AddRange(JpegXrTables.NumCbphp);
        tables.AddRange(JpegXrTables.NumBlkCbphpYuv);
        tables.AddRange(JpegXrTables.IndexA);
        tables.AddRange(JpegXrTables.FirstIndex);
        tables.AddRange(JpegXrTables.RunValue.OfType<JpegXrVlc>());
        foreach (var table in tables)
        {
            // Kraft: a complete prefix code's lengths sum to exactly one, and no code begins another.
            Assert.Equal(1.0, table.Codes.Sum(c => Math.Pow(2, -c.Length)), 9);
            Assert.DoesNotContain(table.Codes, a => table.Codes.Any(b => !ReferenceEquals(a, b) && b.StartsWith(a, StringComparison.Ordinal)));
        }
    }

    [Fact]
    public void Damaged_and_oversized_pictures_are_refused_quickly()
    {
        var clock = Stopwatch.StartNew();
        // Not a JPEG XR file, a directory outside the file, a codestream of nothing.
        Assert.Throws<InvalidDataException>(() => JpegXrDecoder.Decode([0x49, 0x49, 0x2A, 0, 8, 0, 0, 0], 1 << 20));
        Assert.Throws<InvalidDataException>(() => JpegXrDecoder.Decode([0x49, 0x49, 0xBC, 1, 0xFF, 0xFF, 0, 0], 1 << 20));
        var seed = Seed();
        // A codestream that claims 65536 × 65536 pixels is refused before anything is allocated.
        var huge = (byte[])seed.Clone();
        var codestream = CodestreamStart(huge);
        // SHORT_HEADER_FLAG set: width and height are the 16-bit fields after the 8 signature and 4 flag bytes.
        if ((huge[codestream + 10] & 0x80) != 0)
        {
            huge[codestream + 12] = huge[codestream + 13] = huge[codestream + 14] = huge[codestream + 15] = 0xFF;
            Assert.Throws<InvalidDataException>(() => JpegXrDecoder.Decode(huge, 1 << 24));
        }
        // Every cut of the file is read or refused, never anything else.
        for (var length = 8; length < seed.Length; length += Math.Max(1, seed.Length / 200))
        {
            try
            {
                JpegXrDecoder.Decode(seed[..length], 1 << 20);
            }
            catch (InvalidDataException)
            {
            }
            catch (NotSupportedException)
            {
            }
        }
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"took {clock.Elapsed}");
    }

    /// <summary>The first byte of the picture's codestream (its IMAGE_OFFSET).</summary>
    private static int CodestreamStart(byte[] file)
    {
        var ifd = BitConverter.ToInt32(file, 4);
        var count = BitConverter.ToUInt16(file, ifd);
        for (var i = 0; i < count; i++)
        {
            var at = ifd + 2 + i * 12;
            if (BitConverter.ToUInt16(file, at) == 0xBCC0)
                return BitConverter.ToUInt16(file, at + 2) == 3 ? BitConverter.ToUInt16(file, at + 8) : BitConverter.ToInt32(file, at + 8);
        }
        throw new InvalidDataException("no image offset");
    }

    /// <summary>
    /// A small JPEG XR file for the damage checks and the fuzz seed: written by WIC on Windows, else a file of
    /// Filee's own minimal writer (an 16 × 16 grey picture whose macroblock has only a DC value).
    /// </summary>
    internal static byte[] Seed()
    {
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(Path.GetTempPath(), $"filee-jxr-seed-{Guid.NewGuid():N}.jxr");
            try
            {
                JpegXrBuilder.Write(path, 40, 24, JpegXrBuilder.Bgra32, Pixels(40, 24, 4), 40 * 4,
                    new Dictionary<string, object> { ["ImageQuality"] = 0.8f, ["Overlap"] = (byte)2, ["InterleavedAlpha"] = true });
                return File.ReadAllBytes(path);
            }
            finally
            {
                File.Delete(path);
            }
        }
        return JpegXrTestFile.DcOnlyGrey();
    }
}
