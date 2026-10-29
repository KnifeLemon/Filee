// Pictures for the "Pixelate / blur" window: the preview, read as the image engine reads the file (so the frames land on
// the picture the conversion covers), and copies of it pixelated or blurred as chosen, which the frames show.

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Magick;
using ImageMagick;

namespace Filee.App.Services;

public static class HideService
{
    /// <summary>Longest side of the preview: huge pictures are shown smaller, for a fluid window.</summary>
    public const int MaxPreviewSide = 2560;

    /// <summary>True when the window can open a <paramref name="formatId"/> file: a picture the image engine reads.</summary>
    public static bool CanOpen(string formatId) =>
        HideTool.CanHide(formatId) && ImageEncoder.Sources.Contains(formatId);

    /// <summary>
    /// Reads <paramref name="path"/> as the conversion will (the composite of a PSD, the largest size of an icon, turned
    /// as its EXIF orientation says) and makes a preview of it at most <see cref="MaxPreviewSide"/> across. The size is
    /// the picture's own. Animations and multi-page files are refused: the frames drawn on the first page would hide
    /// nothing on the others.
    /// </summary>
    public static Task<(MagickImage Preview, int Width, int Height)> LoadPreviewAsync(string path, Preset tool) =>
        Task.Run(() =>
        {
            var format = FormatRegistry.Detect(path)?.Id ?? throw new NotSupportedException("Unknown picture format.");
            if (format == "png" && Apng.IsAnimated(path))
                throw new NotSupportedException("Animated PNG files can't have parts hidden.");
            using var images = ImageReader.Read(path, format, tool);
            var frames = ImageEncoder.SelectFrames(images, format, format);
            if (frames.Count != 1)
                throw new NotSupportedException("Animated or multi-page pictures can't have parts hidden.");
            var preview = (MagickImage)frames[0].Clone();
            try
            {
                preview.AutoOrient();
                var (width, height) = ((int)preview.Width, (int)preview.Height);
                if (preview.Width > MaxPreviewSide || preview.Height > MaxPreviewSide)
                    preview.Resize(new MagickGeometry(MaxPreviewSide, MaxPreviewSide) { Greater = true });
                preview.ResetPage();
                return (preview, width, height);
            }
            catch
            {
                preview.Dispose();
                throw;
            }
        });

    /// <summary>
    /// The size of the squares (or the blur) on the preview, for a picture of <paramref name="width"/> × <paramref name="height"/>
    /// shown at <paramref name="previewWidth"/> pixels across: the conversion's, scaled. A square is a whole number of
    /// the picture's pixels, as the conversion makes it.
    /// </summary>
    public static double PreviewEffectSize(HideStyle style, int strength, int width, int height, int previewWidth)
    {
        var size = HideTool.EffectSize(strength, width, height);
        if (style == HideStyle.Pixelate)
            size = Math.Round(size);
        return size * previewWidth / Math.Max(1, width);
    }

    /// <summary>A copy of <paramref name="preview"/> covered all over: what a frame shows inside it.</summary>
    public static Bitmap Cover(MagickImage preview, HideStyle style, double size)
    {
        using var copy = preview.Clone();
        AreaHider.Cover(copy, style, size);
        return ToBitmap(copy);
    }

    /// <summary>The picture as a bitmap the window draws.</summary>
    public static Bitmap ToBitmap(IMagickImage<byte> image)
    {
        var pixels = image.ToByteArray(MagickFormat.Bgra);
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Unpremul, handle.AddrOfPinnedObject(),
                new PixelSize((int)image.Width, (int)image.Height), new Vector(96, 96), (int)image.Width * 4);
        }
        finally
        {
            handle.Free();
        }
    }
}
