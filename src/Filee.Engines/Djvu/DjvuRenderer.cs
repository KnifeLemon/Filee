// Draws a decoded DjVu page as DjVu viewers compose it: the background scaled up to the page (it is stored at a
// fraction of the page's resolution: the reduction is the smallest whole factor that gives its size), then every
// pixel the mask covers in the foreground colour: the FG44 / FGjp picture at that point (scaled the same way), the
// palette colour of the shape that covers it, or black. A page without a background is white.
//
// The mask is also given as 1-bit rows for the PDF writer, all shapes or only those of one palette colour.

namespace Filee.Engines.Djvu;

internal static class DjvuRenderer
{
    /// <summary>The reduction of a layer of <paramref name="layerWidth"/> × <paramref name="layerHeight"/> on the page.</summary>
    public static int Reduction(int width, int height, int layerWidth, int layerHeight)
    {
        for (var red = 1; red < 16; red++)
        {
            if ((width + red - 1) / red == layerWidth && (height + red - 1) / red == layerHeight)
                return red;
        }
        return 16;
    }

    /// <summary>
    /// The page's mask as 1-bit rows, top row first, a set bit for a black pixel, each row padded to a byte;
    /// <paramref name="color"/> keeps only the shapes of that palette colour.
    /// </summary>
    public static byte[] MaskBits(DjvuPage page, int? color = null, CancellationToken cancellationToken = default)
    {
        int width = page.Width, height = page.Height;
        var stride = (width + 7) / 8;
        var bits = new byte[(long)stride * height];
        if (page.Mask is not { } mask)
            return bits;
        var blitColors = page.Palette?.BlitColors;
        for (var b = 0; b < mask.Blits.Count; b++)
        {
            if ((b & 0x3FF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            if (color is { } wanted && ColorOf(blitColors, b, mask.Blits.Count) != wanted)
                continue;
            var blit = mask.Blits[b];
            var shape = mask.Shapes[blit.Shape];
            for (var y = 0; y < shape.Height; y++)
            {
                var pageY = height - 1 - (blit.Bottom + y);
                if ((uint)pageY >= (uint)height)
                    continue;
                var row = (long)pageY * stride;
                var source = y * shape.Width;
                for (var x = 0; x < shape.Width; x++)
                {
                    if (shape.Bits[source + x] == 0)
                        continue;
                    var pageX = blit.Left + x;
                    if ((uint)pageX < (uint)width)
                        bits[row + (pageX >> 3)] |= (byte)(0x80 >> (pageX & 7));
                }
            }
        }
        return bits;
    }

    /// <summary>The palette colour of blit <paramref name="blit"/> (the first colour when the file has no indices).</summary>
    public static int ColorOf(int[]? blitColors, int blit, int blits) =>
        blitColors is not null && blitColors.Length == blits ? blitColors[blit] : 0;

    /// <summary>The page drawn as RGB bytes, three per pixel, top row first.</summary>
    public static byte[] Compose(DjvuPage page, CancellationToken cancellationToken = default)
    {
        int width = page.Width, height = page.Height;
        var rgb = new byte[(long)width * height * 3];
        if (page.Background is { } background)
            Upscale(background, rgb, width, height, cancellationToken);
        else
            Array.Fill(rgb, (byte)255);
        if (page.Mask is not { } mask)
            return rgb;

        if (page.Foreground is null && page.Palette is { Colors.Length: > 0 } palette)
        {
            // Each shape in its own colour, in the order they are placed.
            for (var b = 0; b < mask.Blits.Count; b++)
            {
                if ((b & 0x3FF) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var c = palette.Colors[ColorOf(palette.BlitColors, b, mask.Blits.Count)];
                Paint(mask, b, rgb, width, height, c.Red, c.Green, c.Blue);
            }
            return rgb;
        }

        var bits = MaskBits(page, null, cancellationToken);
        var stride = (width + 7) / 8;
        var foreground = page.Foreground;
        var red = foreground is null ? 1 : Reduction(width, height, foreground.Width, foreground.Height);
        for (var y = 0; y < height; y++)
        {
            if ((y & 0xFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var row = (long)y * stride;
            var o = (long)y * width * 3;
            var fy = foreground is null ? 0 : Math.Min(y / red, foreground.Height - 1);
            for (var x = 0; x < width; x++, o += 3)
            {
                if ((bits[row + (x >> 3)] & (0x80 >> (x & 7))) == 0)
                    continue;
                if (foreground is null)
                {
                    rgb[o] = rgb[o + 1] = rgb[o + 2] = 0;
                    continue;
                }
                var f = ((long)fy * foreground.Width + Math.Min(x / red, foreground.Width - 1)) * 3;
                rgb[o] = foreground.Rgb[f];
                rgb[o + 1] = foreground.Rgb[f + 1];
                rgb[o + 2] = foreground.Rgb[f + 2];
            }
        }
        return rgb;
    }

    private static void Paint(Jb2Image mask, int b, byte[] rgb, int width, int height, byte r, byte g, byte bl)
    {
        var blit = mask.Blits[b];
        var shape = mask.Shapes[blit.Shape];
        for (var y = 0; y < shape.Height; y++)
        {
            var pageY = height - 1 - (blit.Bottom + y);
            if ((uint)pageY >= (uint)height)
                continue;
            var source = y * shape.Width;
            for (var x = 0; x < shape.Width; x++)
            {
                var pageX = blit.Left + x;
                if (shape.Bits[source + x] == 0 || (uint)pageX >= (uint)width)
                    continue;
                var o = ((long)pageY * width + pageX) * 3;
                rgb[o] = r;
                rgb[o + 1] = g;
                rgb[o + 2] = bl;
            }
        }
    }

    /// <summary>Scales <paramref name="layer"/> up to the page, bilinear, each layer pixel centred on its block of page pixels.</summary>
    public static void Upscale(DjvuPixmap layer, byte[] rgb, int width, int height, CancellationToken cancellationToken)
    {
        var red = Reduction(width, height, layer.Width, layer.Height);
        int lw = layer.Width, lh = layer.Height;
        var source = layer.Rgb;
        var xs = new int[width];
        var xf = new int[width];
        for (var x = 0; x < width; x++)
        {
            var sx = Math.Clamp((x + 0.5) / red - 0.5, 0, lw - 1);
            xs[x] = (int)sx;
            xf[x] = (int)((sx - xs[x]) * 256);
        }
        for (var y = 0; y < height; y++)
        {
            if ((y & 0xFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var sy = Math.Clamp((y + 0.5) / red - 0.5, 0, lh - 1);
            var y0 = (int)sy;
            var y1 = Math.Min(y0 + 1, lh - 1);
            var fy = (int)((sy - y0) * 256);
            var row0 = (long)y0 * lw * 3;
            var row1 = (long)y1 * lw * 3;
            var o = (long)y * width * 3;
            for (var x = 0; x < width; x++, o += 3)
            {
                var x0 = xs[x];
                var x1 = Math.Min(x0 + 1, lw - 1);
                var fx = xf[x];
                for (var c = 0; c < 3; c++)
                {
                    int a = source[row0 + x0 * 3 + c], b = source[row0 + x1 * 3 + c];
                    int d = source[row1 + x0 * 3 + c], e = source[row1 + x1 * 3 + c];
                    var top = a * 256 + (b - a) * fx;
                    var bottom = d * 256 + (e - d) * fx;
                    rgb[o + c] = (byte)((top * 256 + (bottom - top) * fy + 32768) >> 16);
                }
            }
        }
    }
}
