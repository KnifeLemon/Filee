// Filee's own EMF and WMF writers (Metafiles/EmfWriter, WmfWriter) through the drawing converter (SVG → EMF / WMF,
// Drawings/DrawingMetafile): the files are read back by Filee's own metafile renderer and checked at a few pixels and
// against the slide renderer's picture of the same drawing, and their records are checked against [MS-EMF] / [MS-WMF]
// (sizes, header, end record, object table); on Windows, GDI itself plays the EMF. Drawings are written in code.

using System.Buffers.Binary;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Drawings;
using Filee.Engines.Metafiles;
using Filee.Engines.Slides;
using Filee.Engines.Typesetting;
using SkiaSharp;

namespace Filee.Engines.Tests;

public sealed class MetafileWriterTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"filee-metafile-writer-{Guid.NewGuid():N}");

    public MetafileWriterTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A solid PNG of <paramref name="color"/>, as a data: URI.</summary>
    private static string Png(SKColor color, int width = 8, int height = 6)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return "data:image/png;base64," + Convert.ToBase64String(data.ToArray());
    }

    /// <summary>200 × 100 px: a red square, a black-to-white gradient, a blue ring, translucent green, a magenta picture and text.</summary>
    private string Drawing(string text = "Filee 그림")
    {
        var svg = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="200" height="100" viewBox="0 0 200 100">
              <defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="#000000"/><stop offset="1" stop-color="#ffffff"/></linearGradient></defs>
              <rect x="0" y="0" width="50" height="50" fill="#ff0000"/>
              <rect x="60" y="0" width="100" height="20" fill="url(#g)"/>
              <circle cx="25" cy="75" r="20" fill="none" stroke="#0000ff" stroke-width="6"/>
              <rect x="60" y="30" width="40" height="30" fill="#00ff00" fill-opacity="0.5"/>
              <image x="110" y="30" width="40" height="30" preserveAspectRatio="none" xlink:href="{Png(SKColors.Magenta)}"/>
              <text x="60" y="90" font-family="Arial" font-size="12" fill="#008000">{text}</text>
            </svg>
            """;
        var path = Path.Combine(_folder, $"drawing-{Guid.NewGuid():N}.svg");
        File.WriteAllText(path, svg);
        return path;
    }

    private async Task<byte[]> ConvertAsync(string source, string to)
    {
        var output = Path.Combine(_folder, $"out-{Guid.NewGuid():N}.{to}");
        var written = await new DrawingConverter().ConvertAsync(new ConversionStep(source, "svg", to, new Preset(), new SingleOutput(output), _folder), null, Token);
        return File.ReadAllBytes(Assert.Single(written));
    }

    private sealed class SingleOutput(string path) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => path;
    }

    /// <summary>The colour at (x, y) of a picture rendered at 96 dpi (one pixel per SVG pixel), un-premultiplied.</summary>
    private static SKColor At(SKBitmap bitmap, int x, int y) => bitmap.GetPixel(x, y);

    private static bool Near(SKColor actual, SKColor expected, int tolerance = 40) =>
        Math.Abs(actual.Red - expected.Red) <= tolerance && Math.Abs(actual.Green - expected.Green) <= tolerance
        && Math.Abs(actual.Blue - expected.Blue) <= tolerance && Math.Abs(actual.Alpha - expected.Alpha) <= tolerance;

    // ───────────────────────── Round trip ─────────────────────────

    [Theory]
    [InlineData("emf")]
    [InlineData("wmf")]
    public async Task An_svg_becomes_a_metafile_of_its_size_that_draws_the_same(string format)
    {
        var bytes = await ConvertAsync(Drawing(), format);
        Assert.Equal(format == "emf" ? MetafileFormat.Emf : MetafileFormat.Wmf, Metafile.Detect(bytes));
        // 200 × 100 CSS pixels = 150 × 75 points.
        var (width, height) = Metafile.Size(bytes)!.Value;
        Assert.InRange(width, 149.5f, 150.5f);
        Assert.InRange(height, 74.5f, 75.5f);

        var (bitmap, _) = Metafile.Render(bytes, 96, cancellationToken: Token);
        using (bitmap)
        {
            Assert.Equal((200, 100), (bitmap.Width, bitmap.Height));
            Assert.True(Near(At(bitmap, 25, 25), SKColors.Red), $"red square: {At(bitmap, 25, 25)}");
            Assert.True(At(bitmap, 63, 10).Red < 40, $"gradient start: {At(bitmap, 63, 10)}");
            Assert.True(At(bitmap, 157, 10).Red > 215, $"gradient end: {At(bitmap, 157, 10)}");
            Assert.InRange(At(bitmap, 110, 10).Red, 90, 165);
            Assert.True(Near(At(bitmap, 25, 55), SKColors.Blue), $"ring: {At(bitmap, 25, 55)}");
            Assert.Equal(0, At(bitmap, 25, 75).Alpha); // inside the ring: nothing
            Assert.True(Near(At(bitmap, 130, 45), SKColors.Magenta), $"picture: {At(bitmap, 130, 45)}");
            // Translucent green: blended in an EMF; mixed with white in a WMF, which has no alpha.
            var green = At(bitmap, 80, 45);
            Assert.True(format == "emf" ? Near(green, new SKColor(0, 255, 0, 128)) : Near(green, new SKColor(128, 255, 128)), $"translucent: {green}");
            Assert.Equal(0, At(bitmap, 190, 5).Alpha); // no page behind the drawing
        }
    }

    [Theory]
    [InlineData("emf")]
    [InlineData("wmf")]
    public void A_drawing_with_turns_gradients_dashes_and_arrowheads_matches_the_slide_renderer(string format)
    {
        var deck = new SlideDeck { Width = 300, Height = 200 };
        var slide = new Slide();
        var group = new SlideGroup { Bounds = new SKRect(20, 20, 280, 180), ChildBounds = new SKRect(0, 0, 260, 160), Rotation = 15 };
        var ellipse = new SKPath();
        ellipse.AddOval(new SKRect(0, 0, 120, 80));
        group.Children.Add(new SlideShape
        {
            Bounds = new SKRect(10, 10, 130, 90),
            Paths = [new SlidePath(ellipse)],
            Fill = new GradientFill([new GradientStop(0, SKColors.Yellow), new GradientStop(1, SKColors.DarkBlue)], GradientShape.Circle, 0, false, new SKRect(0.5f, 0.5f, 0.5f, 0.5f)),
            Line = new SlideLine(new SolidFill(SKColors.Black), 2),
        });
        var square = new SKPath();
        square.AddRect(new SKRect(0, 0, 80, 60));
        group.Children.Add(new SlideShape
        {
            Bounds = new SKRect(150, 20, 230, 80),
            Paths = [new SlidePath(square)],
            Fill = new GradientFill([new GradientStop(0, SKColors.Red), new GradientStop(1, SKColors.Green)], GradientShape.Linear, 45, false, SKRect.Empty),
        });
        var line = new SKPath();
        line.MoveTo(0, 0);
        line.LineTo(200, 0);
        group.Children.Add(new SlideShape
        {
            Bounds = new SKRect(20, 120, 220, 120),
            Paths = [new SlidePath(line, PathFill.None)],
            Line = new SlideLine(new SolidFill(SKColors.DarkRed), 4, Dash: [3, 1], Tail: new LineEnd(LineEndKind.Triangle)),
        });
        slide.Elements.Add(group);
        deck.Slides.Add(slide);

        using var shaper = new Shaper();
        using var renderer = new SlideRenderer(new FontResolver(), shaper, Token);
        var bytes = DrawingMetafile.Write(deck, slide, format == "wmf", renderer, cancellationToken: Token);
        var (ours, _) = Metafile.Render(bytes, 72, cancellationToken: Token);
        using var expected = SlideOutput.ToBitmaps(deck, 72, cancellationToken: Token).Single();
        using (ours)
        {
            Assert.Equal((expected.Width, expected.Height), (ours.Width, ours.Height));
            var different = 0;
            for (var y = 0; y < ours.Height; y++)
            {
                for (var x = 0; x < ours.Width; x++)
                {
                    var a = OnWhite(ours.GetPixel(x, y));
                    var b = expected.GetPixel(x, y);
                    if (!Near(a, b, 48))
                        different++;
                }
            }
            // Edges differ (GDI's aliased fills against Skia's smoothed ones); the shapes, colours and turns don't.
            Assert.True(different < ours.Width * ours.Height * 0.04, $"{different} of {ours.Width * ours.Height} pixels differ");
        }
    }

    private static SKColor OnWhite(SKColor c)
    {
        var a = c.Alpha / 255f;
        byte Mix(byte v) => (byte)Math.Round(v * a + 255 * (1 - a));
        return new SKColor(Mix(c.Red), Mix(c.Green), Mix(c.Blue));
    }

    [Fact]
    public async Task Windows_gdi_plays_every_record_of_the_emf()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows' GDI plays the metafile.");
        var emf = await ConvertAsync(Drawing(), "emf");
        // A 200 × 100 white 24-bit DIB section, the picture played into it record by record (false: one failed).
        var dc = Gdi.CreateCompatibleDC(IntPtr.Zero);
        var info = new byte[40];
        BinaryPrimitives.WriteInt32LittleEndian(info, 40);
        BinaryPrimitives.WriteInt32LittleEndian(info.AsSpan(4), 200);
        BinaryPrimitives.WriteInt32LittleEndian(info.AsSpan(8), -100);
        BinaryPrimitives.WriteInt16LittleEndian(info.AsSpan(12), 1);
        BinaryPrimitives.WriteInt16LittleEndian(info.AsSpan(14), 24);
        var dib = Gdi.CreateDIBSection(dc, info, 0, out var pixels, IntPtr.Zero, 0);
        var metafile = Gdi.SetEnhMetaFileBits((uint)emf.Length, emf);
        try
        {
            Gdi.SelectObject(dc, dib);
            Gdi.PatBlt(dc, 0, 0, 200, 100, 0x00FF0062); // WHITENESS
            var area = new Gdi.Rect { Right = 200, Bottom = 100 };
            Assert.True(Gdi.PlayEnhMetaFile(dc, metafile, ref area));
            SKColor Pixel(int x, int y)
            {
                var bgr = new byte[3];
                System.Runtime.InteropServices.Marshal.Copy(pixels + y * 600 + x * 3, bgr, 0, 3);
                return new SKColor(bgr[2], bgr[1], bgr[0]);
            }
            Assert.True(Near(Pixel(25, 25), SKColors.Red), $"red square: {Pixel(25, 25)}");
            Assert.True(Near(Pixel(80, 45), new SKColor(128, 255, 128)), $"translucent green on white: {Pixel(80, 45)}");
            Assert.True(Near(Pixel(130, 45), SKColors.Magenta), $"picture: {Pixel(130, 45)}");
        }
        finally
        {
            Gdi.DeleteEnhMetaFile(metafile);
            Gdi.DeleteObject(dib);
            Gdi.DeleteDC(dc);
        }
    }

    private static class Gdi
    {
        public struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr dc);

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        public static extern IntPtr CreateDIBSection(IntPtr dc, byte[] info, uint usage, out IntPtr bits, IntPtr section, uint offset);

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr dc, IntPtr item);

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr item);

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        public static extern bool PatBlt(IntPtr dc, int x, int y, int width, int height, uint rop);

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        public static extern IntPtr SetEnhMetaFileBits(uint size, byte[] data);

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        public static extern bool DeleteEnhMetaFile(IntPtr metafile);

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        public static extern bool PlayEnhMetaFile(IntPtr dc, IntPtr metafile, ref Rect area);
    }

    // ───────────────────────── Records ─────────────────────────

    /// <summary>The EMF's records: type, offset, size.</summary>
    private static List<(uint Type, int At, int Size)> EmfRecords(byte[] emf)
    {
        var records = new List<(uint, int, int)>();
        for (var at = 0; at < emf.Length;)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(emf.AsSpan(at));
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(emf.AsSpan(at + 4));
            Assert.True(size >= 8 && size % 4 == 0, $"record {type} at {at} has size {size}");
            records.Add((type, at, size));
            at += size;
            Assert.True(at <= emf.Length);
        }
        return records;
    }

    [Fact]
    public async Task An_emf_has_its_header_frame_record_sizes_end_and_object_table_right()
    {
        var emf = await ConvertAsync(Drawing(), "emf");
        var records = EmfRecords(emf);
        var header = EmfHeader.Read(emf);
        Assert.Equal(1u, records[0].Type);
        Assert.Equal(14u, records[^1].Type); // EMR_EOF
        Assert.Equal(20, records[^1].Size);
        Assert.Equal((uint)emf.Length, header.Bytes);
        Assert.Equal((uint)records.Count, header.Records);
        // The frame: 150 × 75 points in 0.01 mm (5292 × 2646), right and bottom included; bounds in 1440 dpi device units.
        Assert.Equal(new SKRectI(0, 0, 5291, 2645), header.Frame);
        Assert.Equal(new SKRectI(0, 0, 2999, 1499), header.Bounds);
        Assert.Equal(1440 / 25.4, header.UnitsPerMm.X, 6);
        Assert.Equal(1440 / 25.4, header.UnitsPerMm.Y, 6);
        // Every object made has an entry in the table; selections name made or stock objects.
        var made = records.Where(r => r.Type is 39 or 95 or 82).Select(r => BinaryPrimitives.ReadUInt32LittleEndian(emf.AsSpan(r.At + 8))).ToList();
        Assert.NotEmpty(made);
        Assert.All(made, handle => Assert.InRange(handle, 1u, (uint)header.Handles - 1));
        foreach (var select in records.Where(r => r.Type == 37))
        {
            var handle = BinaryPrimitives.ReadUInt32LittleEndian(emf.AsSpan(select.At + 8));
            Assert.True((handle & 0x80000000) != 0 || made.Contains(handle), $"selects {handle:x}");
        }
        // SaveDC and RestoreDC pair up.
        Assert.Equal(records.Count(r => r.Type == 33), records.Count(r => r.Type == 34));
        // The gradient as GradientFill, the translucent green as AlphaBlend, the picture as StretchDIBits, curves kept.
        Assert.Contains(records, r => r.Type == 118);
        Assert.Contains(records, r => r.Type == 114);
        Assert.Contains(records, r => r.Type == 81);
        Assert.Contains(records, r => r.Type is 5 or 88);
    }

    [Fact]
    public async Task A_wmf_has_its_placeable_header_sizes_end_and_object_table_right()
    {
        var wmf = await ConvertAsync(Drawing(), "wmf");
        Assert.Equal(WmfHeader.PlaceableKey, BinaryPrimitives.ReadUInt32LittleEndian(wmf));
        ushort checksum = 0;
        for (var i = 0; i < 20; i += 2)
            checksum ^= BinaryPrimitives.ReadUInt16LittleEndian(wmf.AsSpan(i));
        Assert.Equal(checksum, BinaryPrimitives.ReadUInt16LittleEndian(wmf.AsSpan(20)));
        // Box 150 × 75 points in twips.
        Assert.Equal((3000, 1500, 1440), (BinaryPrimitives.ReadInt16LittleEndian(wmf.AsSpan(10)), BinaryPrimitives.ReadInt16LittleEndian(wmf.AsSpan(12)),
            BinaryPrimitives.ReadUInt16LittleEndian(wmf.AsSpan(14))));
        var words = BinaryPrimitives.ReadUInt32LittleEndian(wmf.AsSpan(22 + 6));
        Assert.Equal((wmf.Length - 22) / 2, (int)words);
        var objects = BinaryPrimitives.ReadUInt16LittleEndian(wmf.AsSpan(22 + 10));
        var maxRecord = BinaryPrimitives.ReadUInt32LittleEndian(wmf.AsSpan(22 + 12));
        var at = 22 + 18;
        uint largest = 0;
        ushort function = 0xFFFF;
        var live = new HashSet<int>();
        var peak = 0;
        while (at < wmf.Length)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(wmf.AsSpan(at));
            function = BinaryPrimitives.ReadUInt16LittleEndian(wmf.AsSpan(at + 4));
            Assert.True(size >= 3, $"record {function:x4} at {at}");
            largest = Math.Max(largest, size);
            if (function is 0x02FA or 0x02FC or 0x02FB)
            {
                // Objects take the lowest free entry.
                var entry = Enumerable.Range(0, objects).First(i => !live.Contains(i));
                live.Add(entry);
                peak = Math.Max(peak, live.Count);
            }
            if (function == 0x01F0)
                Assert.True(live.Remove(BinaryPrimitives.ReadUInt16LittleEndian(wmf.AsSpan(at + 6))));
            if (function == 0x012D)
                Assert.Contains(BinaryPrimitives.ReadUInt16LittleEndian(wmf.AsSpan(at + 6)), live);
            at += (int)size * 2;
        }
        Assert.Equal(wmf.Length, at);
        Assert.Equal(0x0000, function); // META_EOF last
        Assert.Equal(largest, maxRecord);
        Assert.True(peak <= objects);
    }

    // ───────────────────────── Text ─────────────────────────

    [Fact]
    public async Task Text_stays_text_in_unicode_in_an_emf_and_in_a_code_page_in_a_wmf()
    {
        var emf = await ConvertAsync(Drawing("Filee 그림"), "emf");
        var texts = EmfRecords(emf).Where(r => r.Type == 84)
            .Select(r => Encoding.Unicode.GetString(emf, r.At + 76, (int)BinaryPrimitives.ReadUInt32LittleEndian(emf.AsSpan(r.At + 44)) * 2)).ToList();
        Assert.Equal("Filee 그림", string.Concat(texts).Trim());
        // 12 CSS pixels = 9 points = 180 logical units, as the em height (negative).
        var fonts = EmfRecords(emf).Where(r => r.Type == 82).ToList();
        Assert.NotEmpty(fonts);
        Assert.All(fonts, r => Assert.Equal(-180, BinaryPrimitives.ReadInt32LittleEndian(emf.AsSpan(r.At + 12))));

        // Korean fits code page 949 (HANGUL_CHARSET) in a WMF.
        var wmf = await ConvertAsync(Drawing("그림 Filee"), "wmf");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var korean = Encoding.GetEncoding(949).GetBytes("그림");
        var strings = Records(wmf, 0x0A32);
        Assert.True(strings.Any(r => r.Span.IndexOf(korean) >= 0), string.Join(" | ", strings.Select(r => Convert.ToHexString(r.Span))));
        Assert.Contains(Records(wmf, 0x02FB), r => r.Span[13] == 129);
    }

    [Fact]
    public async Task Text_a_metafile_cant_hold_as_text_is_drawn_as_outlines()
    {
        // Arabic needs shaping: no ExtTextOutW, the glyphs' outlines instead.
        var emf = await ConvertAsync(Drawing("مرحبا"), "emf");
        Assert.DoesNotContain(EmfRecords(emf), r => r.Type == 84);
        // Thai fits none of the WMF code pages tried: outlines.
        var wmf = await ConvertAsync(Drawing("ภาษาไทย"), "wmf");
        Assert.Empty(Records(wmf, 0x0A32));
    }

    /// <summary>The parameters (after size and function) of the WMF's records of <paramref name="function"/>.</summary>
    private static List<ReadOnlyMemory<byte>> Records(byte[] wmf, ushort function)
    {
        var found = new List<ReadOnlyMemory<byte>>();
        for (var at = 40; at < wmf.Length;)
        {
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(wmf.AsSpan(at)) * 2;
            if (BinaryPrimitives.ReadUInt16LittleEndian(wmf.AsSpan(at + 4)) == function)
                found.Add(wmf.AsMemory(at + 6, size - 6));
            at += size;
        }
        return found;
    }

    // ───────────────────────── Converter ─────────────────────────

    [Fact]
    public void Drawings_reach_emf_and_wmf_below_libreoffice_and_a_checked_off_svg_keeps_its_route()
    {
        var edges = new DrawingConverter().Edges.Where(e => e.To is "emf" or "wmf").ToList();
        Assert.Equal(["vsd", "odg", "otg", "fodg", "iwb", "notebook", "svg", "svgz", "mm", "xmind"], edges.Select(e => e.From).Distinct());
        Assert.All(edges, e => Assert.True(e.Cost < 25 && e.DirectOnly, $"{e.From} → {e.To}"));
        Assert.All(edges.Where(e => e.From is "svg" or "svgz" or "vsd" or "odg"), e => Assert.True(e.Checked, $"{e.From} → {e.To}"));
        Assert.False(new DrawingConverter().Transforms("svg", "emf"));

        var filtered = Path.Combine(_folder, "filter.svg");
        File.WriteAllText(filtered, """<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"><filter id="f"/><rect width="5" height="5" filter="url(#f)"/></svg>""");
        Assert.Equal("SVG filters", new DrawingConverter().FirstUnsupported(filtered, "svg"));
    }

    // ───────────────────────── Limits ─────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_drawing_past_the_metafile_limits_is_refused_quickly(bool wmf)
    {
        var deck = new SlideDeck { Width = 500, Height = 500 };
        var slide = new Slide();
        for (var i = 0; i < 20_000; i++)
        {
            var path = new SKPath();
            path.AddRect(new SKRect(0, 0, 4, 4));
            slide.Elements.Add(new SlideShape
            {
                Bounds = new SKRect(i % 120 * 4, i / 120 % 120 * 4, i % 120 * 4 + 4, i / 120 % 120 * 4 + 4),
                Paths = [new SlidePath(path)],
                Fill = new SolidFill(new SKColor((uint)(0xFF000000 | (uint)i * 2654435761u >> 8))),
            });
        }
        deck.Slides.Add(slide);
        using var shaper = new Shaper();
        using var renderer = new SlideRenderer(new FontResolver(), shaper, Token);
        var refused = Assert.Throws<ConversionRefusedException>(() =>
            DrawingMetafile.Write(deck, slide, wmf, renderer, new MetafileLimits { MaxRecords = 5_000 }, Token));
        Assert.Contains("metafile records", refused.Message);
        // A huge SVG size is cut to 200 inches a side, and the WMF's units shrink so its coordinates fit 16 bits.
        var huge = Path.Combine(_folder, "huge.svg");
        File.WriteAllText(huge, """<svg xmlns="http://www.w3.org/2000/svg" width="1e12" height="10"><rect width="1e12" height="10" fill="red"/></svg>""");
        var big = SvgDrawingReader.Read(huge, cancellationToken: Token);
        var bytes = DrawingMetafile.Write(big, big.Slides[0], wmf, renderer, cancellationToken: Token);
        var (width, _) = Metafile.Size(bytes)!.Value;
        Assert.InRange(width, 14_300, 14_400);
    }
}
