// Filee's metafile renderer against GDI+ on Windows: metafiles are recorded in code with GDI (CreateEnhMetaFile and
// the drawing functions, a WMF converted from that by GetWinMetaFileBits) and with GDI+ (System.Drawing, EMF only and
// EMF+ dual), then drawn by GDI+ and by Filee at the same size, and the two pictures must reach SSIM ≥ 0.97
// (RenderQuality/PageImage.cs; text 0.9, see TextSsim). Skipped on other platforms; MetafileTests covers them with
// hand-written records. Set FILEE_RENDER_OUT to a folder to keep both pictures of each comparison.

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Filee.Engines.Metafiles;
using Filee.Engines.Tests.RenderQuality;
using SkiaSharp;
using GdiMetafile = System.Drawing.Imaging.Metafile;

#pragma warning disable CA1416 // every test skips unless it runs on Windows

namespace Filee.Engines.Tests;

public sealed class MetafileGdiTests(ITestOutputHelper output)
{
    private const double MinimumSsim = 0.97;

    /// <summary>
    /// Text is held to less: GDI hints glyphs to the pixel grid and draws their stems darker and wider than their
    /// outlines, which Filee draws (hinted the same way only where Skia's hinting agrees).
    /// </summary>
    private const double TextSsim = 0.9;
    private const int Width = 400, Height = 300;

    /// <summary>Both renderers draw at this many times the size; each block of Factor × Factor pixels is one compared pixel.</summary>
    private const int Factor = 4;

    // ───────────────────────── GDI recordings ─────────────────────────

    [Fact]
    public void Gdi_shapes_pens_and_brushes_match_gdiplus() =>
        Compare("GDI shapes", RecordGdi(dc =>
        {
            Gdi.SetBkMode(dc, 1);
            using (Gdi.Use(dc, Gdi.CreatePen(0, 3, 0x003070C0)))
            using (Gdi.Use(dc, Gdi.CreateSolidBrush(0x0060C0F0)))
                Gdi.Rectangle(dc, 20, 20, 180, 120);
            using (Gdi.Use(dc, Gdi.CreatePen(0, 1, 0x00000000)))
            using (Gdi.Use(dc, Gdi.CreateHatchBrush(5, 0x00208020))) // HS_DIAGCROSS
                Gdi.Ellipse(dc, 200, 20, 380, 140);
            // Dashes on a straight line (GDI counts a cosmetic pen's dashes in pixel steps, which along a curve are
            // shorter than the curve's length).
            using (Gdi.Use(dc, Gdi.CreatePen(1, 1, 0x00000000))) // PS_DASH
            {
                Gdi.MoveToEx(dc, 200, 146, 0);
                Gdi.LineTo(dc, 380, 146);
            }
            using (Gdi.Use(dc, Gdi.CreatePen(0, 6, 0x000000C0)))
            using (Gdi.Use(dc, Gdi.CreateSolidBrush(0x00C08040)))
            {
                Gdi.RoundRect(dc, 20, 150, 180, 280, 40, 40);
                Gdi.Pie(dc, 200, 150, 380, 290, 380, 220, 290, 150);
                Gdi.Polygon(dc, [new(30, 290), new(100, 160), new(170, 290)], 3);
            }
            using (Gdi.Use(dc, Gdi.CreatePen(0, 2, 0x00800080)))
            {
                Gdi.MoveToEx(dc, 10, 10, 0);
                Gdi.LineTo(dc, 390, 290);
                Gdi.Arc(dc, 40, 40, 360, 260, 360, 150, 40, 150);
            }
        }));

    [Fact]
    public void Gdi_text_matches_gdiplus()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI records the metafile.");
        Compare("GDI text", RecordGdi(dc =>
        {
            Gdi.SetBkMode(dc, 1);
            Gdi.SetTextColor(dc, 0x00402000);
            Text(dc, "Arial", 36, 20, 20, "Filee metafile");
            Text(dc, "Times New Roman", 30, 20, 90, "Vector text, 1234");
            Text(dc, "Malgun Gothic", 32, 20, 160, "한글 메타파일 그림");
            Gdi.SetTextAlign(dc, 6 | 24); // TA_CENTER | TA_BASELINE
            Text(dc, "Arial", 28, 200, 270, "Centred on a baseline");
        }), minimum: TextSsim);
    }

    [Fact]
    public void Gdi_bitmaps_paths_clipping_and_transforms_match_gdiplus() =>
        Compare("GDI bitmaps, paths, clips", RecordGdi(dc =>
        {
            // A 16 × 16 colour gradient stretched to 160 × 120.
            var pixels = new byte[16 * 16 * 4];
            for (var y = 0; y < 16; y++)
            {
                for (var x = 0; x < 16; x++)
                {
                    var at = ((15 - y) * 16 + x) * 4; // bottom-up
                    pixels[at] = (byte)(x * 16);
                    pixels[at + 1] = (byte)(y * 16);
                    pixels[at + 2] = (byte)(255 - x * 8);
                }
            }
            var info = new Gdi.BitmapInfoHeader { Size = 40, Width = 16, Height = 16, Planes = 1, BitCount = 32 };
            Gdi.StretchDIBits(dc, 20, 20, 160, 120, 0, 0, 16, 16, pixels, ref info, 0, 0x00CC0020);

            // A star filled as a path, alternate fill, clipped to a rectangle.
            Gdi.SaveDC(dc);
            Gdi.IntersectClipRect(dc, 200, 20, 380, 120);
            Gdi.BeginPath(dc);
            Gdi.Polygon(dc, [new(290, 0), new(330, 140), new(210, 50), new(370, 50), new(250, 140)], 5);
            Gdi.EndPath(dc);
            using (Gdi.Use(dc, Gdi.CreateSolidBrush(0x000080FF)))
                Gdi.FillPath(dc);
            Gdi.RestoreDC(dc, -1);

            // Rotated by the world transform.
            Gdi.SetGraphicsMode(dc, 2);
            var rotate = new Gdi.XForm { M11 = 0.866f, M12 = 0.5f, M21 = -0.5f, M22 = 0.866f, Dx = 120, Dy = 150 };
            Gdi.SetWorldTransform(dc, ref rotate);
            using (Gdi.Use(dc, Gdi.CreatePen(0, 4, 0x00006000)))
            using (Gdi.Use(dc, Gdi.CreateSolidBrush(0x0080FF80)))
                Gdi.Rectangle(dc, 0, 0, 160, 80);
        }));

    [Fact]
    public void Wmf_converted_by_gdi_matches_gdiplus()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI records the metafile.");
        var emf = RecordGdi(dc =>
        {
            Gdi.SetBkMode(dc, 1);
            using (Gdi.Use(dc, Gdi.CreatePen(0, 3, 0x003070C0)))
            using (Gdi.Use(dc, Gdi.CreateSolidBrush(0x0060C0F0)))
            {
                Gdi.Rectangle(dc, 20, 20, 180, 120);
                Gdi.Ellipse(dc, 200, 20, 380, 140);
            }
            using (Gdi.Use(dc, Gdi.CreateSolidBrush(0x00C08040)))
                Gdi.Polygon(dc, [new(30, 290), new(100, 160), new(170, 290)], 3);
            Gdi.SetTextColor(dc, 0x00402000);
            Text(dc, "Arial", 36, 200, 200, "WMF text");
        });
        Compare("WMF from GDI", ToWmf(emf));
    }

    // ───────────────────────── GDI+ recordings ─────────────────────────

    [Theory]
    [InlineData(EmfType.EmfOnly)]
    [InlineData(EmfType.EmfPlusDual)]
    public void Gdiplus_drawings_match_gdiplus(EmfType type) =>
        CompareGdiPlus("GDI+ drawing", type, MinimumSsim, g =>
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillRectangle(Brushes.SteelBlue, 20, 20, 160, 100);
            using (var pen = new Pen(Color.DarkRed, 4))
                g.DrawEllipse(pen, 200, 20, 180, 120);
            using (var hatch = new HatchBrush(HatchStyle.DiagonalCross, Color.DarkGreen, Color.White))
                g.FillPie(hatch, 20, 140, 160, 140, 30, 270);
            using (var gradient = new LinearGradientBrush(new Rectangle(200, 160, 180, 120), Color.Gold, Color.Purple, 0f))
                g.FillRectangle(gradient, 200, 160, 180, 120);
            using (var pen = new Pen(Color.Black, 2) { DashStyle = DashStyle.Dash })
                g.DrawBezier(pen, 10, 290, 120, 120, 280, 300, 390, 130);
            g.TranslateTransform(300, 80);
            g.RotateTransform(20);
            g.FillPolygon(Brushes.OrangeRed, new PointF[] { new(-30, -20), new(30, -20), new(0, 30) });
        });

    [Theory]
    [InlineData(EmfType.EmfOnly)]
    [InlineData(EmfType.EmfPlusDual)]
    public void Gdiplus_text_matches_gdiplus(EmfType type) =>
        CompareGdiPlus("GDI+ text", type, TextSsim, g =>
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (var font = new Font("Arial", 28, FontStyle.Regular, GraphicsUnit.Pixel))
                g.DrawString("GDI+ text in an EMF", font, Brushes.Navy, 20, 20);
            using (var font = new Font("Malgun Gothic", 26, FontStyle.Bold, GraphicsUnit.Pixel))
                g.DrawString("한글 클립아트", font, Brushes.DarkGreen, 20, 70);
            using (var font = new Font("Times New Roman", 30, FontStyle.Italic, GraphicsUnit.Pixel))
                g.DrawString("Serif italic, 1234", font, Brushes.Black, 20, 140);
        });

    [Theory]
    [InlineData(EmfType.EmfOnly)]
    [InlineData(EmfType.EmfPlusDual)]
    public void Gdiplus_images_and_clips_match_gdiplus(EmfType type) =>
        CompareGdiPlus("GDI+ images", type, MinimumSsim, g =>
        {
            using var bitmap = new Bitmap(24, 16);
            for (var y = 0; y < 16; y++)
            {
                for (var x = 0; x < 24; x++)
                    bitmap.SetPixel(x, y, Color.FromArgb(x * 10, y * 15, 200 - x * 5));
            }
            g.DrawImage(bitmap, new Rectangle(20, 130, 240, 160));
            g.SetClip(new Rectangle(280, 130, 100, 100));
            g.FillEllipse(Brushes.Crimson, 260, 110, 140, 140);
        });

    /// <summary>
    /// Records <paramref name="draw"/> with GDI+ and compares Filee's picture of it with GDI+'s. Filee draws an EMF+
    /// dual file from its EMF records, while GDI+ draws it from its EMF+ records, which lay text out with GDI+'s own
    /// grid-fitted advances and carry the bitmap undithered (the EMF copy is dithered to 256 colours). A dual file is
    /// therefore held to what GDI+'s own pictures of the EMF-only and the dual recording reach between them, less 0.03,
    /// when that is below <paramref name="minimum"/>.
    /// </summary>
    private void CompareGdiPlus(string name, EmfType type, double minimum, Action<Graphics> draw)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI+ records the metafile.");
        var metafile = RecordGdiPlus(type, draw);
        if (type == EmfType.EmfPlusDual)
        {
            using var emf = DrawWithGdiPlus(RecordGdiPlus(EmfType.EmfOnly, draw), Width * Factor, Height * Factor);
            using var dual = DrawWithGdiPlus(metafile, Width * Factor, Height * Factor);
            var (agreement, _) = Ssim.Compare(GreyImage.From(emf, Factor), GreyImage.From(dual, Factor));
            output.WriteLine($"{name}: GDI+ draws the EMF records and the EMF+ records with SSIM {agreement:F4} between them");
            minimum = Math.Min(minimum, agreement - 0.03);
        }
        Compare($"{name} {type}", metafile, minimum);
    }

    // ───────────────────────── Comparison ─────────────────────────

    /// <summary>
    /// Draws the metafile with GDI+ and with Filee on white and checks the SSIM. GDI draws without smoothing and Filee
    /// smooths, so both draw at four times the size and each 4 × 4 block counts as one pixel of the 400 × 300 picture that
    /// is compared: what is measured is where things are and what colour, not how each renderer covers an edge pixel.
    /// The SSIM of the pictures drawn at 400 × 300 is reported too. Text-heavy pictures (GDI hints its glyphs to the
    /// pixel grid and draws them darker) are held to <paramref name="minimum"/>.
    /// </summary>
    private void Compare(string name, byte[] metafile, double minimum = MinimumSsim)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI+ draws the reference.");
        var (ssim, off) = Measure(name, metafile, Factor);
        var (direct, _) = Measure(name, metafile, 1);
        output.WriteLine($"{name}: SSIM {ssim:F4} (drawn at 400 × 300: {direct:F4}), pixels off {off:P2}");
        Assert.True(ssim >= minimum, $"{name}: SSIM {ssim:F4} < {minimum}");
    }

    private static (double Ssim, double Off) Measure(string name, byte[] metafile, int factor)
    {
        int width = Width * factor, height = Height * factor;
        using var expected = DrawWithGdiPlus(metafile, width, height);
        var size = Filee.Engines.Metafiles.Metafile.Size(metafile)!.Value;
        var pixelsPerPoint = width / size.Width;
        using var drawing = Filee.Engines.Metafiles.Metafile.Draw(metafile, pixelsPerPoint, cancellationToken: TestContext.Current.CancellationToken);
        using var actual = Filee.Engines.Metafiles.Metafile.Rasterize(drawing, width, height);
        Assert.Equal((width, height), (actual.Width, actual.Height));
        if (Environment.GetEnvironmentVariable("FILEE_RENDER_OUT") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            var file = string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : '_')) + (factor > 1 ? $"_x{factor}" : "");
            File.WriteAllBytes(Path.Combine(folder, file + ".gdiplus.png"), expected.Encode(SKEncodedImageFormat.Png, 100).ToArray());
            File.WriteAllBytes(Path.Combine(folder, file + ".filee.png"), actual.Encode(SKEncodedImageFormat.Png, 100).ToArray());
        }
        return Ssim.Compare(GreyImage.From(expected, factor), GreyImage.From(actual, factor));
    }

    private static SKBitmap DrawWithGdiPlus(byte[] metafile, int width, int height)
    {
        using var stream = new MemoryStream(metafile);
        using var picture = new GdiMetafile(stream);
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.White);
            g.DrawImage(picture, new Rectangle(0, 0, width, height));
        }
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var result = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
            var bytes = new byte[data.Stride * height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            Marshal.Copy(bytes, 0, result.GetPixels(), bytes.Length);
            return result;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    // ───────────────────────── Recording ─────────────────────────

    /// <summary>
    /// Records an EMF with GDI. The drawing is in a 400 × 300 window mapped onto a 100 × 75 mm frame, whatever the
    /// screen the metafile is recorded against.
    /// </summary>
    private static byte[] RecordGdi(Action<IntPtr> draw)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI records the metafile.");
        var frame = new Gdi.Rect { Right = 10_000, Bottom = 7_500 };
        var dc = Gdi.CreateEnhMetaFileW(IntPtr.Zero, null, ref frame, "Filee test\0\0");
        Assert.NotEqual(IntPtr.Zero, dc);
        var pixelsX = 100.0 * Gdi.GetDeviceCaps(dc, 8) / Gdi.GetDeviceCaps(dc, 4); // HORZRES / HORZSIZE
        var pixelsY = 75.0 * Gdi.GetDeviceCaps(dc, 10) / Gdi.GetDeviceCaps(dc, 6); // VERTRES / VERTSIZE
        Gdi.SetMapMode(dc, 8); // MM_ANISOTROPIC
        Gdi.SetWindowExtEx(dc, Width, Height, IntPtr.Zero);
        Gdi.SetViewportExtEx(dc, (int)Math.Round(pixelsX), (int)Math.Round(pixelsY), IntPtr.Zero);
        draw(dc);
        var handle = Gdi.CloseEnhMetaFile(dc);
        try
        {
            var size = Gdi.GetEnhMetaFileBits(handle, 0, null);
            var bytes = new byte[size];
            Gdi.GetEnhMetaFileBits(handle, size, bytes);
            return bytes;
        }
        finally
        {
            Gdi.DeleteEnhMetaFile(handle);
        }
    }

    /// <summary>A placeable WMF (1440 units per inch) of an EMF, converted by GDI.</summary>
    private static byte[] ToWmf(byte[] emf)
    {
        var handle = Gdi.SetEnhMetaFileBits((uint)emf.Length, emf);
        var screen = Gdi.GetDC(IntPtr.Zero);
        try
        {
            var size = Gdi.GetWinMetaFileBits(handle, 0, null, 8, screen);
            var bits = new byte[size];
            Gdi.GetWinMetaFileBits(handle, size, bits, 8, screen);
            var header = new byte[22];
            BitConverter.GetBytes(0x9AC6CDD7).CopyTo(header, 0);
            BitConverter.GetBytes((short)(100 / 25.4 * 1440)).CopyTo(header, 10);
            BitConverter.GetBytes((short)(75 / 25.4 * 1440)).CopyTo(header, 12);
            BitConverter.GetBytes((ushort)1440).CopyTo(header, 14);
            ushort checksum = 0;
            for (var i = 0; i < 20; i += 2)
                checksum ^= BitConverter.ToUInt16(header, i);
            BitConverter.GetBytes(checksum).CopyTo(header, 20);
            return [.. header, .. bits];
        }
        finally
        {
            _ = Gdi.ReleaseDC(IntPtr.Zero, screen);
            Gdi.DeleteEnhMetaFile(handle);
        }
    }

    /// <summary>Records an EMF with GDI+, a 400 × 300 pixel frame.</summary>
    private static byte[] RecordGdiPlus(EmfType type, Action<Graphics> draw)
    {
        using var screen = Graphics.FromHwnd(IntPtr.Zero);
        var hdc = screen.GetHdc();
        using var stream = new MemoryStream();
        try
        {
            using var picture = new GdiMetafile(stream, hdc, new RectangleF(0, 0, Width, Height), MetafileFrameUnit.Pixel, type);
            using var g = Graphics.FromImage(picture);
            g.PageUnit = GraphicsUnit.Pixel;
            draw(g);
        }
        finally
        {
            screen.ReleaseHdc(hdc);
        }
        return stream.ToArray();
    }

    private static void Text(IntPtr dc, string face, int pixels, int x, int y, string text)
    {
        var font = Gdi.CreateFontW(-pixels, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4, 0, face);
        var old = Gdi.SelectObject(dc, font);
        Gdi.ExtTextOutW(dc, x, y, 0, IntPtr.Zero, text, (uint)text.Length, IntPtr.Zero);
        Gdi.SelectObject(dc, old);
        Gdi.DeleteObject(font);
    }

    /// <summary>The GDI functions the recordings use.</summary>
    private static class Gdi
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Point(int x, int y)
        {
            public int X = x, Y = y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct XForm
        {
            public float M11, M12, M21, M22, Dx, Dy;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BitmapInfoHeader
        {
            public int Size, Width, Height;
            public short Planes, BitCount;
            public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
        }

        /// <summary>Selects <paramref name="item"/> and, when disposed, the object it replaced, then deletes it.</summary>
        public static IDisposable Use(IntPtr dc, IntPtr item) => new Selection(dc, item, SelectObject(dc, item));

        private sealed class Selection(IntPtr dc, IntPtr item, IntPtr previous) : IDisposable
        {
            public void Dispose()
            {
                SelectObject(dc, previous);
                DeleteObject(item);
            }
        }

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateEnhMetaFileW(IntPtr reference, string? path, ref Rect frame, string description);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CloseEnhMetaFile(IntPtr dc);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteEnhMetaFile(IntPtr metafile);

        [DllImport("gdi32.dll")]
        public static extern uint GetEnhMetaFileBits(IntPtr metafile, uint size, byte[]? buffer);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SetEnhMetaFileBits(uint size, byte[] data);

        [DllImport("gdi32.dll")]
        public static extern int GetWinMetaFileBits(IntPtr metafile, int size, byte[]? buffer, int mapMode, IntPtr reference);

        [DllImport("gdi32.dll")]
        public static extern int GetDeviceCaps(IntPtr dc, int index);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr window, IntPtr dc);

        [DllImport("gdi32.dll")]
        public static extern int SetMapMode(IntPtr dc, int mode);

        [DllImport("gdi32.dll")]
        public static extern bool SetWindowExtEx(IntPtr dc, int x, int y, IntPtr previous);

        [DllImport("gdi32.dll")]
        public static extern bool SetViewportExtEx(IntPtr dc, int x, int y, IntPtr previous);

        [DllImport("gdi32.dll")]
        public static extern int SetGraphicsMode(IntPtr dc, int mode);

        [DllImport("gdi32.dll")]
        public static extern bool SetWorldTransform(IntPtr dc, ref XForm transform);

        [DllImport("gdi32.dll")]
        public static extern int SetBkMode(IntPtr dc, int mode);

        [DllImport("gdi32.dll")]
        public static extern uint SetTextColor(IntPtr dc, uint color);

        [DllImport("gdi32.dll")]
        public static extern uint SetTextAlign(IntPtr dc, uint align);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreatePen(int style, int width, uint color);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateSolidBrush(uint color);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateHatchBrush(int hatch, uint color);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic,
            uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr dc, IntPtr item);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr item);

        [DllImport("gdi32.dll")]
        public static extern bool Rectangle(IntPtr dc, int left, int top, int right, int bottom);

        [DllImport("gdi32.dll")]
        public static extern bool Ellipse(IntPtr dc, int left, int top, int right, int bottom);

        [DllImport("gdi32.dll")]
        public static extern bool RoundRect(IntPtr dc, int left, int top, int right, int bottom, int width, int height);

        [DllImport("gdi32.dll")]
        public static extern bool Pie(IntPtr dc, int left, int top, int right, int bottom, int x1, int y1, int x2, int y2);

        [DllImport("gdi32.dll")]
        public static extern bool Arc(IntPtr dc, int left, int top, int right, int bottom, int x1, int y1, int x2, int y2);

        [DllImport("gdi32.dll")]
        public static extern bool Polygon(IntPtr dc, Point[] points, int count);

        [DllImport("gdi32.dll")]
        public static extern bool MoveToEx(IntPtr dc, int x, int y, IntPtr previous);

        [DllImport("gdi32.dll")]
        public static extern bool LineTo(IntPtr dc, int x, int y);

        [DllImport("gdi32.dll")]
        public static extern bool BeginPath(IntPtr dc);

        [DllImport("gdi32.dll")]
        public static extern bool EndPath(IntPtr dc);

        [DllImport("gdi32.dll")]
        public static extern bool FillPath(IntPtr dc);

        [DllImport("gdi32.dll")]
        public static extern int SaveDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        public static extern bool RestoreDC(IntPtr dc, int saved);

        [DllImport("gdi32.dll")]
        public static extern int IntersectClipRect(IntPtr dc, int left, int top, int right, int bottom);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        public static extern bool ExtTextOutW(IntPtr dc, int x, int y, uint options, IntPtr rect, string text, uint count, IntPtr dx);

        [DllImport("gdi32.dll")]
        public static extern int StretchDIBits(IntPtr dc, int x, int y, int width, int height, int sourceX, int sourceY,
            int sourceWidth, int sourceHeight, byte[] bits, ref BitmapInfoHeader info, uint usage, uint rop);
    }
}
