// Colour palettes (#1615): the documented .pal layouts (raw 768 bytes in 6 or 8 bits, JASC-PAL, RIFF PAL), GIMP's .gpl
// and Adobe's .act, read and written by PaletteConverter, and the swatch picture through SVG. Inputs are written here
// per each format's description.

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using SkiaSharp;

namespace Filee.Engines.Tests;

public class PaletteTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> ConvertOkAsync(string source, string target)
    {
        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey}: {f.ErrorDetail}")));
        return job.Outputs.Single();
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(fx.NewFolder(), name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string Write(string name, string text) => Write(name, Encoding.UTF8.GetBytes(text));

    /// <summary>256 colours, colour i = (i, 255 - i, i / 2), 8 bits per channel.</summary>
    private static byte[] Raw8()
    {
        var bytes = new byte[768];
        for (var i = 0; i < 256; i++)
        {
            bytes[i * 3] = (byte)i;
            bytes[i * 3 + 1] = (byte)(255 - i);
            bytes[i * 3 + 2] = (byte)(i / 2);
        }
        return bytes;
    }

    private static List<string> HexLines(string path) =>
        [.. File.ReadAllLines(path).Where(line => line.Length > 0)];

    [Fact]
    public async Task A_raw_vga_palette_is_scaled_from_6_bits()
    {
        var bytes = new byte[768];
        bytes[0] = 63; bytes[1] = 32; bytes[2] = 0;   // colour 0
        bytes[3] = 1; bytes[4] = 62; bytes[5] = 63;  // colour 1

        var hex = HexLines(await ConvertOkAsync(Write("game.pal", bytes), "txt"));

        Assert.Equal(256, hex.Count);
        Assert.Equal("#FF8200", hex[0]);
        Assert.Equal("#04FBFF", hex[1]);
        Assert.Equal("#000000", hex[255]);
    }

    [Fact]
    public async Task A_raw_8_bit_palette_round_trips_through_act_and_gpl()
    {
        var raw = Raw8();
        var act = await ConvertOkAsync(Write("earth.pal", raw), "act");

        var actBytes = await File.ReadAllBytesAsync(act, Ct);
        Assert.Equal(772, actBytes.Length);
        Assert.Equal(raw, actBytes[..768]);
        Assert.Equal(256, BinaryPrimitives.ReadUInt16BigEndian(actBytes.AsSpan(768)));
        Assert.Equal(0xFFFF, BinaryPrimitives.ReadUInt16BigEndian(actBytes.AsSpan(770)));

        var gpl = await ConvertOkAsync(act, "gpl");
        var lines = await File.ReadAllLinesAsync(gpl, Ct);
        Assert.Equal("GIMP Palette", lines[0]);
        Assert.Equal("Name: earth", lines[1]);
        Assert.Equal("Columns: 16", lines[2]);
        Assert.Equal(" 16 239   8\t#10EF08", lines[4 + 16]);

        var back = await ConvertOkAsync(gpl, "act");
        Assert.Equal(actBytes, await File.ReadAllBytesAsync(back, Ct));
    }

    [Fact]
    public async Task A_jasc_palette_becomes_a_hex_list()
    {
        var jasc = Write("ui.pal", "JASC-PAL\r\n0100\r\n3\r\n255 0 0\r\n0 128 255\r\n17 34 51\r\n");

        Assert.Equal(["#FF0000", "#0080FF", "#112233"], HexLines(await ConvertOkAsync(jasc, "txt")));
    }

    [Fact]
    public async Task A_jasc_palette_saved_with_a_byte_order_mark_is_read()
    {
        var jasc = Write("bom.pal", "﻿JASC-PAL\r\n0100\r\n2\r\n255 0 0\r\n0 128 255\r\n");

        Assert.Equal(["#FF0000", "#0080FF"], HexLines(await ConvertOkAsync(jasc, "txt")));
    }

    [Fact]
    public void The_hex_lists_are_never_a_step_towards_other_formats()
    {
        var edges = new Filee.Engines.Palettes.PaletteConverter().Edges;
        Assert.All(edges.Where(e => e.To is "txt" or "json"), e => Assert.True(e.DirectOnly));
        Assert.All(edges.Where(e => e.To is "svg" or "gpl" or "act"), e => Assert.False(e.DirectOnly));
    }

    [Fact]
    public async Task A_riff_palette_becomes_json()
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            w.Write("RIFF"u8);
            w.Write(4 + 8 + 4 + 2 * 4);
            w.Write("PAL "u8);
            w.Write("data"u8);
            w.Write(4 + 2 * 4);
            w.Write((ushort)0x0300);
            w.Write((ushort)2);
            w.Write([10, 20, 30, 0]);
            w.Write([200, 100, 50, 0]);
        }

        var json = await ConvertOkAsync(Write("windows.pal", ms.ToArray()), "json");

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(json, Ct));
        Assert.Equal("windows", doc.RootElement.GetProperty("name").GetString());
        var colors = doc.RootElement.GetProperty("colors").EnumerateArray().ToList();
        Assert.Equal(2, colors.Count);
        Assert.Equal("#0A141E", colors[0].GetProperty("hex").GetString());
        Assert.Equal(200, colors[1].GetProperty("r").GetInt32());
        Assert.Equal(50, colors[1].GetProperty("b").GetInt32());
    }

    [Fact]
    public async Task A_gimp_palette_keeps_its_name_and_colour_names()
    {
        var gpl = Write("brand.gpl", "GIMP Palette\nName: Brand colours\nColumns: 4\n# comment\n255 255 255\tPaper white\n  0  51 102 Deep blue\n12 12 12\n");

        var json = await ConvertOkAsync(gpl, "json");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(json, Ct));
        Assert.Equal("Brand colours", doc.RootElement.GetProperty("name").GetString());
        var colors = doc.RootElement.GetProperty("colors").EnumerateArray().ToList();
        Assert.Equal(["Paper white", "Deep blue"], colors.Take(2).Select(c => c.GetProperty("name").GetString()));
        Assert.False(colors[2].TryGetProperty("name", out _));

        // An ACT of three colours says so in its count.
        var act = await File.ReadAllBytesAsync(await ConvertOkAsync(gpl, "act"), Ct);
        Assert.Equal(3, BinaryPrimitives.ReadUInt16BigEndian(act.AsSpan(768)));
        Assert.Equal([0, 51, 102], act[3..6]);
    }

    [Fact]
    public async Task An_act_reads_only_the_colours_it_says_it_uses()
    {
        var act = new byte[772];
        act[0] = 1; act[3] = 2; act[6] = 3;
        BinaryPrimitives.WriteUInt16BigEndian(act.AsSpan(768), 2);
        BinaryPrimitives.WriteUInt16BigEndian(act.AsSpan(770), 0xFFFF);

        Assert.Equal(["#010000", "#020000"], HexLines(await ConvertOkAsync(Write("two.act", act), "txt")));
    }

    [Fact]
    public async Task The_swatch_picture_shows_every_colour_in_rows_of_16()
    {
        var png = await ConvertOkAsync(Write("earth.pal", Raw8()), "png");

        // The picture's size follows the render resolution like any SVG's; the grid is 16 by 16 squares.
        using var bitmap = SKBitmap.Decode(png);
        Assert.Equal(bitmap.Width, bitmap.Height);
        var cell = bitmap.Width / 16;
        // Colour 17 (second row, second column): (17, 238, 8).
        Assert.Equal(new SKColor(17, 238, 8), bitmap.GetPixel(cell + cell / 2, cell + cell / 2));
        Assert.Equal(new SKColor(255, 0, 127), bitmap.GetPixel(bitmap.Width - cell / 2, bitmap.Height - cell / 2));
        // A small palette is one row as wide as it needs.
        var three = await ConvertOkAsync(Write("ui.pal", "JASC-PAL\n0100\n3\n255 0 0\n0 255 0\n0 0 255\n"), "png");
        using var row = SKBitmap.Decode(three);
        Assert.InRange(row.Width, 3 * row.Height - 3, 3 * row.Height + 3); // rounded to whole pixels
        Assert.Equal(new SKColor(0, 0, 255), row.GetPixel(row.Width - row.Height / 2, row.Height / 2));
    }

    [Fact]
    public async Task A_pal_in_an_undocumented_layout_is_refused_not_guessed()
    {
        // Like the sample in #1615: a header of its own ("ZP") and two 768-byte tables, 1614 bytes in all.
        var bytes = new byte[1614];
        bytes[0] = 0x0B;
        "ZP"u8.CopyTo(bytes.AsSpan(2));
        var path = Write("EARTH.PAL", bytes);

        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = "gpl" });

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("error.unsupported_source", job.Files.Single().ErrorKey);
    }
}
