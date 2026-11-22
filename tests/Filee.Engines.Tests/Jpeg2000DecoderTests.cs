// Filee's own JPEG 2000 decoder (Jpeg2000/): pictures OpenJPEG writes (through Magick.NET) decode as OpenJPEG reads
// them back — losslessly to the very pixels, lossy ones within a level or two — in grey, RGB and RGBA, with one or
// several resolutions, layers and every progression order; codestreams Jpeg2000TestFile builds decode losslessly in
// every code-block style, with tiles, two layers, SOP / EPH markers and packed packet headers; empty-packet
// codestreams of any layout decode to mid-grey; and hostile codestreams (pictures, tiles, layers, code-blocks,
// packets, progression changes past the limits, damaged headers, a stream cut short) are refused with the usual
// errors or decoded as far as they go, quickly.

using System.Diagnostics;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Jpeg2000;
using ImageMagick;

namespace Filee.Engines.Tests;

public class Jpeg2000DecoderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A picture with edges, gradients and noise: something for every sub-band.</summary>
    internal static byte[] Pixels(int width, int height, int channels, int seed = 7)
    {
        var random = new Random(seed);
        var pixels = new byte[width * height * channels];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                for (var c = 0; c < channels; c++)
                {
                    var value = c switch
                    {
                        0 => 127 + 120 * Math.Sin(x / 7.0) * Math.Cos(y / 11.0),
                        1 => (x * 5 + y * 3) % 256,
                        2 => (x / 8 + y / 8) % 2 == 0 ? 250 : random.Next(80),
                        _ => x * 255.0 / Math.Max(1, width - 1),
                    };
                    pixels[(y * width + x) * channels + c] = (byte)Math.Clamp(value, 0, 255);
                }
            }
        }
        return pixels;
    }

    private static bool OpenJpegWrites => MagickFormatInfo.Create(MagickFormat.Jp2) is { SupportsWriting: true, SupportsReading: true };

    /// <summary>A picture OpenJPEG wrote (through Magick.NET), and its pixels as OpenJPEG reads them back.</summary>
    private static (byte[] File, byte[] Back) OpenJpeg(int width, int height, int channels, Action<MagickImage> setup)
    {
        var map = channels switch { 1 => "R", 3 => "RGB", _ => "RGBA" };
        var pixels = Pixels(width, height, channels);
        // Magick reads one-channel pixels as grey through "R" only with a grey image; build grey through RGB.
        using var image = channels == 1
            ? new MagickImage(pixels.SelectMany(v => new[] { v, v, v }).ToArray(), new PixelReadSettings((uint)width, (uint)height, StorageType.Char, "RGB"))
            : new MagickImage(pixels, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, map));
        if (channels == 1)
            image.ColorType = ColorType.Grayscale;
        setup(image);
        var file = image.ToByteArray(MagickFormat.Jp2);
        using var back = new MagickImage(file);
        using var values = back.GetPixels();
        var read = values.ToByteArray(channels == 1 ? "R" : map) ?? throw new InvalidOperationException("Magick gave no pixels.");
        return (file, read);
    }

    private static int MaxDifference(Jpeg2000Image image, byte[] expected, int channels)
    {
        var rgba = image.Rgba();
        var most = 0;
        for (var p = 0; p < image.Width * image.Height; p++)
        {
            for (var c = 0; c < channels; c++)
                most = Math.Max(most, Math.Abs(rgba[p * 4 + c] - expected[p * channels + c]));
        }
        return most;
    }

    public static TheoryData<string> OpenJpegCases() =>
        ["grey lossless", "rgb lossless", "rgba lossless", "rgb lossy", "rgb layers", "one resolution", "lrcp", "rlcp", "rpcl", "pcrl", "cprl"];

    [Theory]
    [MemberData(nameof(OpenJpegCases))]
    public void Openjpegs_pictures_decode_as_openjpeg_reads_them(string kind)
    {
        Assert.SkipUnless(OpenJpegWrites, "Magick.NET can't write JPEG 2000 here");
        var channels = kind.StartsWith("grey", StringComparison.Ordinal) ? 1 : kind.StartsWith("rgba", StringComparison.Ordinal) ? 4 : 3;
        var lossless = kind.EndsWith("lossless", StringComparison.Ordinal);
        var (file, back) = OpenJpeg(97, 61, channels, image =>
        {
            image.Quality = lossless ? 100u : 50u;
            switch (kind)
            {
                case "rgb layers":
                    image.Settings.SetDefine(MagickFormat.Jp2, "quality", "25,35,45");
                    break;
                case "one resolution":
                    image.Settings.SetDefine(MagickFormat.Jp2, "number-resolutions", "1");
                    break;
                case "lrcp" or "rlcp" or "rpcl" or "pcrl" or "cprl":
                    image.Settings.SetDefine(MagickFormat.Jp2, "progression-order", kind.ToUpperInvariant());
                    image.Settings.SetDefine(MagickFormat.Jp2, "quality", "30,40");
                    break;
            }
        });

        var decoded = Jpeg2000Decoder.Decode(file, null, Token);

        Assert.Equal((97, 61), (decoded.Width, decoded.Height));
        Assert.Equal(channels, decoded.Components.Count);
        Assert.InRange(MaxDifference(decoded, back, channels), 0, lossless ? 0 : 2);
        if (lossless)
            Assert.Equal(0, MaxDifference(decoded, Pixels(97, 61, channels), channels));
    }

    public static TheoryData<int> Styles() =>
    [
        0,
        Jpeg2000TestFile.Bypass,
        Jpeg2000TestFile.Reset,
        Jpeg2000TestFile.TermAll,
        Jpeg2000TestFile.Causal,
        Jpeg2000TestFile.Symbols,
        Jpeg2000TestFile.Predictable,
        Jpeg2000TestFile.Bypass | Jpeg2000TestFile.TermAll,
        Jpeg2000TestFile.Bypass | Jpeg2000TestFile.Reset | Jpeg2000TestFile.TermAll | Jpeg2000TestFile.Causal | Jpeg2000TestFile.Predictable | Jpeg2000TestFile.Symbols,
    ];

    [Theory]
    [MemberData(nameof(Styles))]
    public void Every_code_block_style_decodes_losslessly(int style)
    {
        var pixels = Pixels(61, 45, 1);
        var file = Jpeg2000TestFile.Grey(61, 45, pixels, new Jpeg2000TestFile.Layout { Style = style, TileWidth = 32, TileHeight = 32 });

        var decoded = Jpeg2000Decoder.Decode(file, null, Token);

        Assert.Equal(pixels, decoded.Components[0].Samples.Select(v => (byte)v));
    }

    public static TheoryData<string> Layouts() => ["two layers", "sop and eph", "ppm", "ppt", "ppm with markers", "wide blocks"];

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Layers_markers_and_packed_headers_decode_losslessly(string kind)
    {
        var pixels = Pixels(70, 40, 1, seed: 3);
        var layout = kind switch
        {
            "two layers" => new Jpeg2000TestFile.Layout { Style = Jpeg2000TestFile.TermAll, TwoLayers = true },
            "sop and eph" => new Jpeg2000TestFile.Layout { SopEph = true, TwoLayers = true, Style = Jpeg2000TestFile.TermAll },
            "ppm" => new Jpeg2000TestFile.Layout { Ppm = true, TileWidth = 32, TileHeight = 32 },
            "ppt" => new Jpeg2000TestFile.Layout { Ppt = true, TileWidth = 32, TileHeight = 32, TwoLayers = true, Style = Jpeg2000TestFile.TermAll },
            "ppm with markers" => new Jpeg2000TestFile.Layout { Ppm = true, SopEph = true },
            _ => new Jpeg2000TestFile.Layout { TileWidth = 64, TileHeight = 16, Style = Jpeg2000TestFile.Causal },
        };
        var file = Jpeg2000TestFile.Grey(70, 40, pixels, layout);

        var decoded = Jpeg2000Decoder.Decode(file, null, Token);

        Assert.Equal(pixels, decoded.Components[0].Samples.Select(v => (byte)v));
    }

    [Fact]
    public void Codestreams_of_empty_packets_decode_to_mid_grey_in_any_layout()
    {
        Jpeg2000TestFile.Empty[] layouts =
        [
            new() { Width = 300, Height = 200, TileWidth = 64, TileHeight = 48, Levels = 3, Components = 3 },
            new() { Width = 257, Height = 129, Levels = 5, Precinct = 4, BlockWidth = 4, BlockHeight = 4, Layers = 3, Order = 2, TileWidth = 257, TileHeight = 129, PacketBytes = 4000 },
            new() { Width = 100, Height = 100, TileWidth = 100, TileHeight = 100, Levels = 2, Components = 2, Layers = 2, Order = 4, Precinct = 5, PacketBytes = 200,
                Changes = [(0, 0, 1, 3, 2, 3), (0, 0, 2, 3, 2, 1)] },
            new() { Width = 1, Height = 1, TileWidth = 1, TileHeight = 1, Levels = 4 },
        ];
        foreach (var layout in layouts)
        {
            var decoded = Jpeg2000Decoder.Decode(Jpeg2000TestFile.EmptyPackets(layout), null, Token);
            Assert.Equal(((int)layout.Width, (int)layout.Height), (decoded.Width, decoded.Height));
            Assert.All(decoded.Components, c => Assert.All(c.Samples, s => Assert.Equal(128, s))); // the DC level
        }
        // A tile with no tile-part stays 0.
        var missing = Jpeg2000Decoder.Decode(Jpeg2000TestFile.EmptyPackets(new() { Width = 100, Height = 17, TileWidth = 64, TileHeight = 64, Levels = 3, TilesWritten = 1 }), null, Token);
        Assert.Equal(128, missing.Components[0].Samples[63]);
        Assert.Equal(0, missing.Components[0].Samples[64]);
    }

    [Fact]
    public void Hostile_codestreams_are_refused_or_cut_short_quickly()
    {
        var stopwatch = Stopwatch.StartNew();
        void Refused(byte[] file, string key, Jpeg2000Limits? limits = null)
        {
            var refused = Assert.Throws<ConversionRefusedException>(() => Jpeg2000Decoder.Decode(file, limits, Token));
            Assert.Equal(key, refused.ErrorKey);
        }
        // A picture of four billion pixels, tiles of one pixel, 65,535 layers.
        Refused(Jpeg2000TestFile.EmptyPackets(new() { Width = 70_000, Height = 70_000, TileWidth = 70_000, TileHeight = 70_000 }), DocumentLimits.TooLargeKey);
        Refused(Jpeg2000TestFile.EmptyPackets(new() { Width = 300, Height = 300, TileWidth = 1, TileHeight = 1, TilesWritten = 1 }), DocumentLimits.ErrorKey);
        Refused(Jpeg2000TestFile.EmptyPackets(new() { Layers = 65_535 }), DocumentLimits.ErrorKey);
        // Code-blocks of 4 × 4 samples over a large picture, packets of tiny precincts and many layers, more samples than allowed.
        Refused(Jpeg2000TestFile.EmptyPackets(new() { Width = 4000, Height = 4000, TileWidth = 4000, TileHeight = 4000, BlockWidth = 2, BlockHeight = 2 }),
            DocumentLimits.ErrorKey, new Jpeg2000Limits { MaxCodeBlocks = 100_000 });
        Refused(Jpeg2000TestFile.EmptyPackets(new() { Width = 2000, Height = 2000, TileWidth = 2000, TileHeight = 2000, Precinct = 2, Levels = 1, BlockWidth = 2, BlockHeight = 2, Layers = 4000 }),
            DocumentLimits.ErrorKey);
        Refused(Jpeg2000TestFile.EmptyPackets(new() { Width = 3000, Height = 3000, TileWidth = 3000, TileHeight = 3000, Components = 20 }),
            DocumentLimits.TooLargeKey, new Jpeg2000Limits { MaxSamples = 100_000_000 });
        // Progression order changes that go over the same packets again and again.
        Refused(Jpeg2000TestFile.EmptyPackets(new() { Layers = 1000, PacketBytes = 1000, Changes = [.. Enumerable.Repeat((0, 0, 1000, 1, 1, 0), 900)] }),
            DocumentLimits.ErrorKey, new Jpeg2000Limits { MaxPackets = 100_000 });

        // Damage: a SIZ of impossible sizes, a code-block claiming endless missing bit-planes, a stream cut short.
        var file = Jpeg2000TestFile.Grey(40, 30, Pixels(40, 30, 1), new Jpeg2000TestFile.Layout());
        var damaged = (byte[])file.Clone();
        damaged[8] = damaged[9] = damaged[10] = damaged[11] = 0; // width 0
        Assert.Throws<InvalidDataException>(() => Jpeg2000Decoder.Decode(damaged, null, Token));
        var sod = file.AsSpan().IndexOf([(byte)0xFF, (byte)0x93]) + 2;
        var planes = (byte[])file.Clone();
        planes[sod] = 0b11000000; // included, then zero bit-plane bits of 0 only
        for (var i = sod + 1; i < planes.Length - 2; i++)
            planes[i] = 0;
        Assert.ThrowsAny<InvalidDataException>(() => Jpeg2000Decoder.Decode(planes, null, Token));
        var cut = file[..(file.Length * 2 / 3)];
        var partial = Jpeg2000Decoder.Decode(cut, null, Token);
        Assert.Equal((40, 30), (partial.Width, partial.Height));
        Assert.Throws<InvalidDataException>(() => Jpeg2000Decoder.Decode([0xFF, 0x4F, 0xFF, 0x51, 0, 41], null, Token));
        Assert.Throws<InvalidDataException>(() => Jpeg2000Decoder.Decode([1, 2, 3], null, Token));

        // CI runners are slower and busy: a wide margin over the second or so this takes.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60), stopwatch.Elapsed.ToString());
    }

    [Fact]
    public void A_jp2_file_gives_its_palette_channels_and_colour_space()
    {
        // A grey index picture with a two-entry palette (black, orange) in a JP2 file: the palette is applied.
        var indices = new byte[16 * 8];
        for (var i = 0; i < indices.Length; i++)
            indices[i] = (byte)(i % 16 < 8 ? 0 : 1);
        var codestream = Jpeg2000TestFile.Grey(16, 8, indices, new Jpeg2000TestFile.Layout());
        byte[] Box(string type, byte[] contents) =>
            [(byte)((contents.Length + 8) >> 24), (byte)((contents.Length + 8) >> 16), (byte)((contents.Length + 8) >> 8), (byte)(contents.Length + 8),
                .. System.Text.Encoding.ASCII.GetBytes(type), .. contents];
        var header = Box("jp2h", [
            .. Box("ihdr", [0, 0, 0, 8, 0, 0, 0, 16, 0, 3, 7, 7, 0, 0]),
            .. Box("colr", [1, 0, 0, 0, 0, 0, 16]),
            .. Box("pclr", [0, 2, 3, 7, 7, 7, 0, 0, 0, 255, 128, 0]),
            .. Box("cmap", [0, 0, 1, 0, 0, 0, 1, 1, 0, 0, 1, 2]),
        ]);
        byte[] jp2 = [0, 0, 0, 12, (byte)'j', (byte)'P', (byte)' ', (byte)' ', 0x0D, 0x0A, 0x87, 0x0A, .. Box("ftyp", [.. "jp2 "u8, 0, 0, 0, 0, .. "jp2 "u8]),
            .. header, .. Box("jp2c", codestream)];

        var decoded = Jpeg2000Decoder.Decode(jp2, null, Token);

        Assert.Equal(Jpeg2000ColorSpace.Srgb, decoded.ColorSpace);
        var rgba = decoded.Rgba();
        Assert.Equal([0, 0, 0, 255], rgba[..4]);
        Assert.Equal([255, 128, 0, 255], rgba[(12 * 4)..(13 * 4)]);
    }
}
