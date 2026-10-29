// "Pixelate / blur": covers parts of a picture (ImageOptions.HiddenAreas) so what was there can't be read back. Each
// part is changed on its own; the rest of the picture keeps its pixels. Also used on the window's preview, so what the
// user sees while drawing the parts is what the file gets.

using Filee.Core.Presets;
using ImageMagick;

namespace Filee.Engines.Magick;

/// <summary>Pixelates or blurs parts of a picture for good.</summary>
public static class AreaHider
{
    /// <summary>
    /// Covers <see cref="ImageOptions.HiddenAreas"/> of <paramref name="image"/> (upright, as the parts were drawn) as
    /// <paramref name="options"/> say. Nothing happens without parts.
    /// </summary>
    public static void Apply(IMagickImage<byte> image, ImageOptions options)
    {
        if (!options.HidesAreas)
            return;
        int width = (int)image.Width, height = (int)image.Height;
        var size = HideTool.EffectSize(options.HideStrength, width, height);
        foreach (var area in options.HiddenAreas!)
        {
            if (HideTool.PixelBounds(area, width, height) is not { } bounds)
                continue;
            if (options.HideStyle == HideStyle.Blur)
                Blur(image, bounds, size);
            else
                Pixelate(image, bounds, Math.Round(size));
        }
        DropPreviews(image);
    }

    /// <summary>
    /// The whole of <paramref name="image"/> covered with an effect <paramref name="size"/> pixels large (a square's side
    /// or the blur's sigma): the window's preview, of which it shows the parts inside the frames.
    /// </summary>
    public static void Cover(IMagickImage<byte> image, HideStyle style, double size)
    {
        var bounds = (0, 0, (int)image.Width, (int)image.Height);
        if (style == HideStyle.Blur)
            Blur(image, bounds, size);
        else
            Pixelate(image, bounds, size);
    }

    /// <summary>
    /// Every pixel of <paramref name="bounds"/> becomes the average colour of its square on a grid of
    /// <paramref name="cell"/>-pixel squares laid from the picture's top-left corner (<see cref="HideTool.CellOf"/>).
    /// </summary>
    /// <remarks>
    /// Averaging is many-to-one: a square's worth of pixels becomes one colour, and what made it up is gone. Squares on
    /// the edge of a part are averaged over all of their pixels, those beside the part too (which stay in view anyway):
    /// the grid stays the same wherever a part is moved, and two parts that meet look like one. How large a square has
    /// to be to make text unreadable is <see cref="HideTool.EffectSize"/>'s concern.
    /// </remarks>
    public static void Pixelate(IMagickImage<byte> image, (int X, int Y, int Width, int Height) bounds, double cell)
    {
        cell = Math.Max(1, cell);
        int width = (int)image.Width, height = (int)image.Height;
        var (x, y, w, h) = bounds;
        if (w <= 0 || h <= 0)
            return;
        // The squares the part touches, whole: their pixels beside the part count towards their colour.
        int firstColumn = HideTool.CellOf(x, cell), lastColumn = HideTool.CellOf(x + w - 1, cell);
        int firstRow = HideTool.CellOf(y, cell), lastRow = HideTool.CellOf(y + h - 1, cell);
        var left = Math.Max(0, HideTool.CellStart(firstColumn, cell));
        var right = Math.Min(width, HideTool.CellStart(lastColumn + 1, cell));
        var top = Math.Max(0, HideTool.CellStart(firstRow, cell));
        var bottom = Math.Min(height, HideTool.CellStart(lastRow + 1, cell));
        int regionWidth = right - left, regionHeight = bottom - top;
        // RGB(A) whatever the picture holds: the squares are written back as a picture of their own (see below).
        var mapping = image.HasAlpha ? PixelMapping.RGBA : PixelMapping.RGB;
        var channels = image.HasAlpha ? 4 : 3;
        int columns = lastColumn - firstColumn + 1, rows = lastRow - firstRow + 1;

        byte[] data;
        using (var pixels = image.GetPixels())
            data = pixels.ToByteArray(left, top, (uint)regionWidth, (uint)regionHeight, mapping)
                ?? throw new InvalidOperationException("The picture's pixels could not be read.");

        var columnOf = new int[regionWidth];
        for (var i = 0; i < regionWidth; i++)
            columnOf[i] = Math.Clamp(HideTool.CellOf(left + i, cell) - firstColumn, 0, columns - 1);
        var rowOf = new int[regionHeight];
        for (var j = 0; j < regionHeight; j++)
            rowOf[j] = Math.Clamp(HideTool.CellOf(top + j, cell) - firstRow, 0, rows - 1);

        var sums = new long[columns * rows * channels];
        var counts = new int[columns * rows];
        for (var j = 0; j < regionHeight; j++)
        {
            var rowBase = rowOf[j] * columns;
            var offset = j * regionWidth * channels;
            for (var i = 0; i < regionWidth; i++, offset += channels)
            {
                var square = rowBase + columnOf[i];
                counts[square]++;
                var sum = square * channels;
                for (var c = 0; c < channels; c++)
                    sums[sum + c] += data[offset + c];
            }
        }
        var averages = new byte[sums.Length];
        for (var square = 0; square < counts.Length; square++)
        {
            var count = counts[square];
            if (count == 0)
                continue;
            for (var c = 0; c < channels; c++)
                averages[square * channels + c] = (byte)((sums[square * channels + c] + count / 2) / count);
        }

        // Only the part takes the squares' colours.
        for (var j = y - top; j < y - top + h; j++)
        {
            var rowBase = rowOf[j] * columns;
            for (var i = x - left; i < x - left + w; i++)
            {
                var square = (rowBase + columnOf[i]) * channels;
                Array.Copy(averages, square, data, (j * regionWidth + i) * channels, channels);
            }
        }
        // Copied over the picture as a picture of their own: writing the area into the pixels of a picture as the image
        // engine reads it left the rest of that picture black.
        using var squares = new MagickImage();
        squares.ReadPixels(data, new PixelReadSettings((uint)regionWidth, (uint)regionHeight, StorageType.Char, mapping));
        image.Composite(squares, left, top, CompositeOperator.Copy);
    }

    /// <summary>
    /// <paramref name="bounds"/> blurred with a Gaussian of <paramref name="sigma"/> pixels, after its pixels are
    /// averaged into squares of about a third of it.
    /// </summary>
    /// <remarks>
    /// A light blur can be undone: it is a known convolution, and deconvolving it (sharpening with the inverse) brings
    /// back text whose strokes it only smeared. Here the part is first scaled down by averaging, which throws pixels
    /// away for good, and then blurred so widely (a sigma about a line of text, see <see cref="HideTool.EffectSize"/>)
    /// that the detail of a letter is far below what an 8-bit pixel can hold. Pixels beside the part are blurred with
    /// it, so its edge doesn't show a seam, but only the part is written back.
    /// </remarks>
    public static void Blur(IMagickImage<byte> image, (int X, int Y, int Width, int Height) bounds, double sigma)
    {
        sigma = Math.Max(1, sigma);
        int width = (int)image.Width, height = (int)image.Height;
        var (x, y, w, h) = bounds;
        if (w <= 0 || h <= 0)
            return;
        var margin = (int)Math.Ceiling(2 * sigma);
        var left = Math.Max(0, x - margin);
        var top = Math.Max(0, y - margin);
        var right = Math.Min(width, x + w + margin);
        var bottom = Math.Min(height, y + h + margin);
        int regionWidth = right - left, regionHeight = bottom - top;

        using var part = image.CloneArea(new MagickGeometry(left, top, (uint)regionWidth, (uint)regionHeight));
        part.ResetPage();
        var step = Math.Max(1, (int)Math.Floor(sigma / 3));
        if (step > 1)
        {
            part.Scale(new MagickGeometry((uint)Math.Max(1, (regionWidth + step - 1) / step), (uint)Math.Max(1, (regionHeight + step - 1) / step))
            {
                IgnoreAspectRatio = true,
            });
        }
        part.GaussianBlur(0, sigma / step);
        if (step > 1)
        {
            part.FilterType = FilterType.Triangle;
            part.Resize(new MagickGeometry((uint)regionWidth, (uint)regionHeight) { IgnoreAspectRatio = true });
        }
        part.Crop(new MagickGeometry(x - left, y - top, (uint)w, (uint)h));
        part.ResetPage();
        image.Composite(part, x, y, CompositeOperator.Copy);
    }

    /// <summary>
    /// Drops the small previews a file may carry of the picture as it was: EXIF's thumbnail, Photoshop's (in its 8BIM
    /// resources) and XMP's. Viewers that show them would show the hidden parts.
    /// </summary>
    internal static void DropPreviews(IMagickImage<byte> image)
    {
        if (image.GetExifProfile() is { } exif && exif.ThumbnailLength > 0)
        {
            exif.RemoveThumbnail();
            image.SetProfile(exif);
        }
        if (image.HasProfile("8bim"))
            image.RemoveProfile("8bim");
        if (image.GetXmpProfile() is { } xmp
            && System.Text.Encoding.UTF8.GetString(xmp.ToByteArray()).Contains("Thumbnails", StringComparison.Ordinal))
            image.RemoveProfile("xmp");
    }
}
