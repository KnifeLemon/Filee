// The Pixelate / blur window's preview of a picture stored with fewer than 8 bits a channel: a 1-bit grey or palette
// PNG (a flat screenshot, a diagram). Its pixels come out at 8 bits a channel, four bytes each, in their real colours;
// reading them as the picture's own depth gave an eighth of the bytes, and the window then read past their end.

using Filee.App.Services;
using ImageMagick;
using ImageMagick.Drawing;

namespace Filee.App.Tests;

public class HidePreviewPixelsTests
{
    private static MagickImage Saved(MagickImage image)
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("filee-depth-").FullName, "picture.png");
        image.Write(path, MagickFormat.Png);
        return new MagickImage(path);
    }

    [Fact]
    public void A_one_bit_and_a_palette_png_give_four_bytes_a_pixel_in_their_colours()
    {
        using var white = Saved(new MagickImage(MagickColors.White, 40, 30));
        using var drawing = new MagickImage(MagickColors.White, 40, 30);
        new Drawables().FillColor(new MagickColor("#3E63DD")).Rectangle(0, 0, 9, 9).Draw(drawing);
        using var palette = Saved(drawing);
        Assert.True(white.Depth < 8 || palette.Depth < 8, "the pictures should be stored with fewer than 8 bits");

        var whitePixels = HideService.PixelsOf(white);
        var palettePixels = HideService.PixelsOf(palette);

        Assert.Equal(40 * 30 * 4, whitePixels.Length);
        Assert.Equal(40 * 30 * 4, palettePixels.Length);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, whitePixels[..4]);
        // BGRA: the blue square in the top left corner, white beside it.
        Assert.Equal(new byte[] { 0xDD, 0x63, 0x3E, 255 }, palettePixels[..4]);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, palettePixels[(20 * 4)..(20 * 4 + 4)]);
    }
}
