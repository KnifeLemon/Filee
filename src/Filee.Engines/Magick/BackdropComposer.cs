// "Add background": a picture set on a gradient, a colour or a picture of one's own, with space round it, rounded
// corners and a soft shadow, on a canvas of the chosen shape. Every size is a part of the picture, so a look suits a
// screenshot and a photo alike. Drawn by Skia with what the window's preview draws with (Avalonia's linear gradient
// brush and drop shadow are Skia's), so the file looks like the preview.

using System.Runtime.InteropServices;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using ImageMagick;
using SkiaSharp;

namespace Filee.Engines.Magick;

public static class BackdropComposer
{
    /// <summary>
    /// The most pixels a canvas may have (as for merged pictures): a long, thin picture on a square canvas with wide
    /// padding would otherwise multiply its pixels many times. A larger canvas is made from a smaller copy of the
    /// picture.
    /// </summary>
    internal static long MaxPixels { get; set; } = ImageJoiner.MaxPixels;

    /// <summary>
    /// Pictures behind that are read: common raster formats, each read as itself (never by guessing the coder).
    /// </summary>
    private static readonly Dictionary<string, MagickFormat> PictureFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = MagickFormat.Png,
        [".jpg"] = MagickFormat.Jpeg,
        [".jpeg"] = MagickFormat.Jpeg,
        [".webp"] = MagickFormat.WebP,
        [".gif"] = MagickFormat.Gif,
        [".bmp"] = MagickFormat.Bmp,
        [".tif"] = MagickFormat.Tiff,
        [".tiff"] = MagickFormat.Tiff,
        [".heic"] = MagickFormat.Heic,
        [".heif"] = MagickFormat.Heif,
        [".avif"] = MagickFormat.Avif,
    };

    /// <summary>Where everything goes on the canvas, in pixels.</summary>
    public readonly record struct Layout(int Width, int Height, int X, int Y, int Radius);

    /// <summary>
    /// The canvas for a <paramref name="width"/> × <paramref name="height"/> picture set on <paramref name="look"/>.
    /// </summary>
    public static Layout LayoutOf(int width, int height, BackdropOptions look)
    {
        look = look.Clamped();
        var pad = (int)Math.Round(Math.Max(width, height) * look.Padding / 100.0);
        int contentWidth = width + 2 * pad, contentHeight = height + 2 * pad;
        // The canvas grows round the picture and its padding until it has the chosen shape; it never crops.
        var (canvasWidth, canvasHeight) = look.Canvas switch
        {
            BackdropCanvas.Square => (Math.Max(contentWidth, contentHeight), Math.Max(contentWidth, contentHeight)),
            BackdropCanvas.Landscape => Grow(contentWidth, contentHeight, 16, 9),
            BackdropCanvas.Portrait => Grow(contentWidth, contentHeight, 4, 5),
            _ => (contentWidth, contentHeight),
        };
        var radius = (int)Math.Round(Math.Min(width, height) * look.Corners / 100.0);
        return new Layout(canvasWidth, canvasHeight, (canvasWidth - width) / 2, (canvasHeight - height) / 2, radius);
    }

    /// <summary>
    /// The picture's size on its canvas: its own, or as much smaller as keeps the canvas within
    /// <see cref="MaxPixels"/>.
    /// </summary>
    public static (int Width, int Height) PictureSizeOn(int width, int height, BackdropOptions look)
    {
        var layout = LayoutOf(width, height, look);
        if ((long)layout.Width * layout.Height <= MaxPixels)
            return (width, height);
        var scale = Math.Sqrt(MaxPixels / ((double)layout.Width * layout.Height));
        while (true)
        {
            // Whole pixels and a canvas rounded up can still pass the limit by a row: a little smaller then.
            (int Width, int Height) size = (Math.Max(1, (int)Math.Floor(width * scale)), Math.Max(1, (int)Math.Floor(height * scale)));
            layout = LayoutOf(size.Width, size.Height, look);
            if ((long)layout.Width * layout.Height <= MaxPixels || size is (1, 1))
                return size;
            scale *= 0.995;
        }
    }

    /// <summary>The size of the file a <paramref name="width"/> × <paramref name="height"/> picture makes.</summary>
    internal static (int Width, int Height) SizeOf(int width, int height, BackdropOptions look)
    {
        var (pictureWidth, pictureHeight) = PictureSizeOn(width, height, look);
        var layout = LayoutOf(pictureWidth, pictureHeight, look);
        return (layout.Width, layout.Height);
    }

    private static (int, int) Grow(int width, int height, int ratioWidth, int ratioHeight) =>
        (long)width * ratioHeight >= (long)height * ratioWidth
            ? (width, (int)Math.Ceiling(width * (double)ratioHeight / ratioWidth))
            : ((int)Math.Ceiling(height * (double)ratioWidth / ratioHeight), height);

    /// <summary>
    /// Sets <paramref name="image"/> (upright) on <paramref name="look"/>: afterwards it is the whole canvas, opaque,
    /// in sRGB, its other metadata kept. Drawn as the window draws it (Skia's linear gradient, the picture with its
    /// corners cut round, Skia's drop shadow of what shows of it), in tiles of at most <see cref="TileSide"/> px a band
    /// at a time: Skia smooths a clip and blurs only within limits of size, and a huge canvas needs no second copy.
    /// </summary>
    public static void Apply(IMagickImage<byte> image, BackdropOptions look)
    {
        look = look.Clamped();
        ToSrgb(image);
        var (width, height) = PictureSizeOn((int)image.Width, (int)image.Height, look);
        // Too large a canvas: the picture shrinks just enough for the whole of it to fit the limit.
        if (width != image.Width || height != image.Height)
            image.Resize(new MagickGeometry((uint)width, (uint)height) { IgnoreAspectRatio = true });
        var layout = LayoutOf(width, height, look);

        // Changed since the window showed it (as the conversion's stamp of it says, before it is read and after):
        // refused, not written with a picture behind the window didn't show.
        var stamped = look.Kind == BackdropKind.Picture && (look.PictureWritten is not null || look.PictureLength is not null);
        if (stamped && !Unchanged(look.Picture, look.PictureWritten, look.PictureLength))
            throw new ConversionRefusedException("error.background_picture_changed", Path.GetFileName(look.Picture));
        var behind = look.Kind == BackdropKind.Picture ? CoverOf(look.Picture, layout) : null;
        AfterPictureBehindRead?.Invoke();
        if (stamped && behind is not null && !Unchanged(look.Picture, look.PictureWritten, look.PictureLength))
        {
            behind.Dispose();
            throw new ConversionRefusedException("error.background_picture_changed", Path.GetFileName(look.Picture));
        }
        // Gone since the window showed it: the file isn't written with a look that wasn't chosen.
        if (look.Kind == BackdropKind.Picture && behind is null)
            throw new ConversionRefusedException("error.background_picture", Path.GetFileName(look.Picture));
        using var behindImage = behind;

        // The picture's pixels, its corners cut round in them: exact and the same in every tile, where a clip's
        // smoothing depended on the canvas's size.
        var pixels = image.GetPixels().ToByteArray(PixelMapping.RGBA) ?? throw new InvalidOperationException("The picture's pixels could not be read.");
        CutCorners(pixels, width, height, Math.Min(layout.Radius, Math.Min(width, height) / 2.0));
        var shape = look.Shadow > 0 ? ShadowOf(width, height, look.Shadow) : (ShadowShape?)null;
        using var picture = Pinned.Of(pixels, width, height);
        // Its alpha only while Skia takes a copy: a second whole-picture array held to the end was memory for nothing.
        using var shadow = shape is { } s ? Shadow.Of(AlphaOf(pixels), width, height, layout, s) : null;

        // The canvas has no alpha: written as RGB, its pixels aren't copied to make room for a fourth channel. Grown
        // with an opaque colour, which RGB then covers: a see-through one (a GIF's background is its see-through
        // colour) turned the alpha back on, and the strips the canvas grew by stayed see-through.
        image.Alpha(AlphaAction.Off);
        image.BackgroundColor = MagickColors.White;
        image.Extent((uint)layout.Width, (uint)layout.Height);
        image.Alpha(AlphaAction.Off);
        var bandRows = BandRows > 0 ? BandRows : (int)Math.Clamp(BandPixels / layout.Width, 64, TileSide);
        var rgb = new byte[(long)layout.Width * Math.Min(bandRows, layout.Height) * 3];
        for (var top = 0; top < layout.Height; top += bandRows)
        {
            var rows = Math.Min(bandRows, layout.Height - top);
            for (var left = 0; left < layout.Width; left += TileSide)
            {
                var columns = Math.Min(TileSide, layout.Width - left);
                using var tile = new SKBitmap(new SKImageInfo(columns, rows, SKColorType.Rgba8888, SKAlphaType.Premul));
                using (var canvas = new SKCanvas(tile))
                {
                    canvas.Translate(-left, -top);
                    Paint(canvas, layout, look, picture.Image, behindImage, shadow, new SKRectI(left, top, left + columns, top + rows));
                }
                // Opaque everywhere: premultiplied is as it is.
                var drawn = tile.Bytes;
                for (var y = 0; y < rows; y++)
                {
                    int from = y * tile.RowBytes, to = (y * layout.Width + left) * 3;
                    for (var x = 0; x < columns; x++, from += 4, to += 3)
                    {
                        rgb[to] = drawn[from];
                        rgb[to + 1] = drawn[from + 1];
                        rgb[to + 2] = drawn[from + 2];
                    }
                }
            }
            image.ImportPixels(rgb, 0, new PixelImportSettings(0, top, (uint)layout.Width, (uint)rows, StorageType.Char, PixelMapping.RGB));
        }
        image.ResetPage();
        // The small previews a file carries (EXIF's thumbnail) still show the picture on its own.
        AreaHider.DropPreviews(image);
    }

    /// <summary>Tests: called between reading the picture behind and looking at its stamp again.</summary>
    internal static Action? AfterPictureBehindRead { get; set; }

    /// <summary>True when <paramref name="path"/>'s file was last written then and is that long still.</summary>
    private static bool Unchanged(string? path, DateTime? written, long? length)
    {
        try
        {
            var file = new FileInfo(path ?? "");
            return file.Exists && (written is null || file.LastWriteTimeUtc == written) && (length is null || file.Length == length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>The widest and tallest a tile is drawn, in pixels (tests make it small).</summary>
    internal static int TileSide { get; set; } = 2048;

    /// <summary>Rows a band holds when set (tests); else as many as make about <see cref="BandPixels"/>.</summary>
    internal static int BandRows { get; set; }

    /// <summary>A band holds about this many pixels (48 MB as RGB), however wide the canvas.</summary>
    private const int BandPixels = 16 << 20;

    /// <summary>
    /// The part <paramref name="tile"/> of the canvas as the window draws it: the background, the shadow, the picture.
    /// </summary>
    private static void Paint(SKCanvas canvas, Layout layout, BackdropOptions look, SKImage picture, Cover? behind,
        Shadow? shadow, SKRectI tile)
    {
        var bounds = SKRect.Create(layout.Width, layout.Height);
        if (look.Kind == BackdropKind.Color)
        {
            canvas.Clear(SkiaColorOf(look.Color, SKColors.White));
        }
        else if (behind is not null)
        {
            // Covering the canvas, its middle kept (as an image brush stretched to fill): the piece kept goes where it
            // is in the whole picture so placed.
            canvas.Clear(SKColors.White);
            var scale = Math.Max(layout.Width / behind.FullWidth, layout.Height / behind.FullHeight);
            double left = (layout.Width - behind.FullWidth * scale) / 2, top = (layout.Height - behind.FullHeight * scale) / 2;
            var place = SKRect.Create((float)(left + behind.Left * scale), (float)(top + behind.Top * scale),
                (float)(behind.Image.Image.Width * scale), (float)(behind.Image.Image.Height * scale));
            canvas.DrawImage(behind.Image.Image, place, new SKSamplingOptions(SKCubicResampler.Mitchell));
        }
        else
        {
            // From the top left corner to the bottom right one, its bands square to that diagonal.
            using var gradient = new SKPaint
            {
                Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(layout.Width, layout.Height),
                    [SkiaColorOf(look.From, new SKColor(0x8E, 0xC5, 0xFC)), SkiaColorOf(look.To, new SKColor(0xE0, 0xC3, 0xFC))], SKShaderTileMode.Clamp),
            };
            canvas.DrawRect(bounds, gradient);
        }
        shadow?.Draw(canvas, tile);
        canvas.DrawImage(picture, layout.X, layout.Y);
    }

    /// <summary>
    /// Cuts the corners of a <paramref name="width"/> × <paramref name="height"/> picture (RGBA, not premultiplied)
    /// round to <paramref name="radius"/>: each pixel keeps about the part of it inside the rounded rectangle (by its
    /// distance to the arc, smoothed over a pixel), the same in every tile and at any size.
    /// </summary>
    private static void CutCorners(byte[] pixels, int width, int height, double radius)
    {
        if (radius <= 0)
            return;
        var reach = (int)Math.Ceiling(radius);
        for (var y = 0; y < reach; y++)
        {
            for (var x = 0; x < reach; x++)
            {
                // From the middle of the pixel to the middle of the corner's circle.
                double dx = radius - (x + 0.5), dy = radius - (y + 0.5);
                if (dx <= 0 || dy <= 0)
                    continue;
                var inside = Math.Clamp(radius - Math.Sqrt(dx * dx + dy * dy) + 0.5, 0, 1);
                if (inside >= 1)
                    continue;
                foreach (var (cx, cy) in new[] { (x, y), (width - 1 - x, y), (x, height - 1 - y), (width - 1 - x, height - 1 - y) })
                {
                    var i = ((long)cy * width + cx) * 4 + 3;
                    pixels[i] = (byte)Math.Round(pixels[i] * inside);
                }
            }
        }
    }

    /// <summary>The alpha of RGBA pixels, one byte each: the shadow's shape.</summary>
    private static byte[] AlphaOf(byte[] pixels)
    {
        var alpha = new byte[pixels.Length / 4];
        for (var i = 0; i < alpha.Length; i++)
            alpha[i] = pixels[i * 4 + 3];
        return alpha;
    }

    /// <summary>
    /// The widest blur drawn at the picture's own size: at a sigma of 200 Skia's blur is already wider than a Gaussian
    /// (53 of 255 a sigma out, where a Gaussian is 40); at 100 it is one to within 2.
    /// </summary>
    private const double MaxSigma = 100;

    /// <summary>
    /// The picture's drop shadow (Skia's, as the window's drop shadow effect draws it), drawn a tile at a time from
    /// the part of its shape the tile's blur reaches: a filter over the whole of a strip longer than Skia's limits
    /// stopped part-way. A wider blur is drawn smaller and scaled up, so it stays Gaussian.
    /// </summary>
    private sealed class Shadow : IDisposable
    {
        private readonly SKImage _shape;
        private readonly Layout _layout;
        private readonly ShadowShape _look;
        private readonly int _scale;
        private readonly SKImage? _small;
        private readonly SKPoint _smallAt;

        private Shadow(SKImage shape, Layout layout, ShadowShape look, int scale, SKImage? small, SKPoint smallAt) =>
            (_shape, _layout, _look, _scale, _small, _smallAt) = (shape, layout, look, scale, small, smallAt);

        public static Shadow Of(byte[] alpha, int width, int height, Layout layout, ShadowShape look)
        {
            var shape = SKImage.FromPixelCopy(new SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Premul), alpha)
                        ?? throw new InvalidOperationException("No shape for the shadow.");
            var scale = (int)Math.Max(1, Math.Ceiling(look.Sigma / MaxSigma));
            if (scale == 1)
                return new Shadow(shape, layout, look, 1, null, default);
            // Drawn small: the shape, then its blurred shadow round it, once for every tile.
            try
            {
                int smallWidth = Math.Max(1, (int)Math.Ceiling(width / (double)scale)), smallHeight = Math.Max(1, (int)Math.Ceiling(height / (double)scale));
                var reach = (int)Math.Ceiling(3 * look.Sigma / scale) + 2;
                var drop = look.Drop / scale;
                var area = new SKRectI(-reach, -reach, smallWidth + reach, smallHeight + (int)Math.Ceiling(drop) + reach);
                using var surface = SKSurface.Create(new SKImageInfo(area.Width, area.Height, SKColorType.Alpha8, SKAlphaType.Premul))
                                    ?? throw new InvalidOperationException("No surface for the shadow.");
                surface.Canvas.Clear(SKColors.Transparent);
                surface.Canvas.Translate(-area.Left, -area.Top);
                using var paint = ShadowPaint(new ShadowShape(look.Sigma / scale, drop, look.Opacity));
                surface.Canvas.DrawImage(shape, SKRect.Create(smallWidth, smallHeight), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
                return new Shadow(shape, layout, look, scale, surface.Snapshot(), new SKPoint(area.Left * scale, area.Top * scale));
            }
            catch
            {
                shape.Dispose();
                throw;
            }
        }

        /// <summary>The shadow over <paramref name="tile"/> (canvas pixels), on a canvas moved to it.</summary>
        public void Draw(SKCanvas canvas, SKRectI tile)
        {
            if (_small is null)
            {
                PaintShadow(canvas, _shape, _layout.X, _layout.Y, _look, tile);
                return;
            }
            var place = SKRect.Create(_layout.X + _smallAt.X, _layout.Y + _smallAt.Y, _small.Width * _scale, _small.Height * _scale);
            using var paint = new SKPaint { Color = SKColors.Black };
            canvas.DrawImage(_small, place, new SKSamplingOptions(SKFilterMode.Linear), paint);
        }

        public void Dispose()
        {
            _shape.Dispose();
            _small?.Dispose();
        }
    }

    /// <summary>The paint that draws a picture's drop shadow alone, as Avalonia's drop shadow effect does.</summary>
    private static SKPaint ShadowPaint(ShadowShape look) => new()
    {
        ImageFilter = SKImageFilter.CreateDropShadowOnly(0, (float)look.Drop, (float)look.Sigma, (float)look.Sigma,
            new SKColor(0, 0, 0, (byte)Math.Round(look.Opacity * 255))),
    };

    /// <summary>
    /// The drop shadow of <paramref name="shape"/> (alpha, <paramref name="width"/> × <paramref name="height"/>) placed
    /// at (<paramref name="x"/>, <paramref name="y"/>), over <paramref name="tile"/> only: from the part of the shape
    /// its blur reaches there, so no Skia surface or filter is ever larger than a tile and three sigmas round it.
    /// </summary>
    internal static void PaintShadow(SKCanvas canvas, byte[] shape, int width, int height, float x, float y, ShadowShape look, SKRectI tile)
    {
        using var image = SKImage.FromPixelCopy(new SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Premul), shape)
                          ?? throw new InvalidOperationException("No shape for the shadow.");
        if (look.Sigma > MaxSigma)
        {
            // As the composer draws a wide blur: smaller, then scaled up.
            using var shadow = Shadow.Of(shape, width, height, new Layout(0, 0, (int)x, (int)y, 0), look);
            shadow.Draw(canvas, tile);
            return;
        }
        PaintShadow(canvas, image, x, y, look, tile);
    }

    private static void PaintShadow(SKCanvas canvas, SKImage shape, float x, float y, ShadowShape look, SKRectI tile)
    {
        // Beyond three sigmas a Gaussian adds nothing a byte can show.
        var reach = (int)Math.Ceiling(3 * look.Sigma) + 2;
        var part = new SKRectI(tile.Left - (int)Math.Floor(x) - reach, tile.Top - (int)Math.Floor(y + look.Drop) - reach,
            tile.Right - (int)Math.Floor(x) + reach, tile.Bottom - (int)Math.Floor(y) + reach);
        if (!part.IntersectsWith(new SKRectI(0, 0, shape.Width, shape.Height)))
            return;
        part.Intersect(new SKRectI(0, 0, shape.Width, shape.Height));
        // The whole of it is the shape itself, not a copy: disposed with the piece, the next tile drew a disposed
        // image.
        var piece = shape.Subset(part);
        if (piece is null)
            return;
        try
        {
            using var paint = ShadowPaint(look);
            canvas.DrawImage(piece, x + part.Left, y + part.Top, paint);
        }
        finally
        {
            if (!ReferenceEquals(piece, shape))
                piece.Dispose();
        }
    }

    /// <summary>
    /// The part of the picture behind that covers the canvas, as Skia's: the whole picture would be
    /// <see cref="FullWidth"/> × <see cref="FullHeight"/>, the part kept starting at (<see cref="Left"/>,
    /// <see cref="Top"/>) of it.
    /// </summary>
    internal sealed class Cover(Pinned image, double fullWidth, double fullHeight, int left, int top) : IDisposable
    {
        public Pinned Image { get; } = image;
        public double FullWidth { get; } = fullWidth;
        public double FullHeight { get; } = fullHeight;
        public int Left { get; } = left;
        public int Top { get; } = top;

        public void Dispose() => Image.Dispose();
    }

    /// <summary>Tests: the picture behind kept whole, the reference its cropped part is held to.</summary>
    internal static bool KeepWholePictureBehind { get; set; }

    /// <summary>
    /// The picture behind for <paramref name="layout"/>'s canvas, as large as covering it takes, with only the whole
    /// pixels of its middle that show and two more round them (as far as its smoothing reads): a panorama behind a
    /// square kept ten times the pixels. Cut to whole pixels, the part kept moved by up to half of one, times how much
    /// it was enlarged; it is drawn where it lies in the whole picture instead. Null when it can't be read.
    /// </summary>
    internal static Cover? CoverOf(string? path, Layout layout)
    {
        using var picture = ReadPictureBehind(path, layout.Width, layout.Height);
        if (picture is null)
            return null;
        double width = picture.Width, height = picture.Height;
        var scale = Math.Max(layout.Width / width, layout.Height / height);
        double shownWidth = layout.Width / scale, shownHeight = layout.Height / scale;
        int left = 0, top = 0, keepWidth = (int)picture.Width, keepHeight = (int)picture.Height;
        if (!KeepWholePictureBehind)
        {
            left = Math.Max(0, (int)Math.Floor((width - shownWidth) / 2) - 2);
            top = Math.Max(0, (int)Math.Floor((height - shownHeight) / 2) - 2);
            keepWidth = Math.Min((int)width, (int)Math.Ceiling((width + shownWidth) / 2) + 2) - left;
            keepHeight = Math.Min((int)height, (int)Math.Ceiling((height + shownHeight) / 2) + 2) - top;
            picture.Crop(new MagickGeometry(left, top, (uint)keepWidth, (uint)keepHeight));
            picture.ResetPage();
        }
        var pixels = picture.GetPixels().ToByteArray(PixelMapping.RGBA) ?? throw new InvalidOperationException("The picture behind could not be read.");
        return new Cover(Pinned.Of(pixels, keepWidth, keepHeight), width, height, left, top);
    }

    /// <summary>
    /// A picture as Skia's over its pixels (RGBA, not premultiplied) without a copy of them, which stay in place until
    /// Skia lets go of them (not necessarily when the image is disposed).
    /// </summary>
    internal sealed class Pinned : IDisposable
    {
        private Pinned(SKImage image) => Image = image;

        public SKImage Image { get; }

        public static Pinned Of(byte[] pixels, int width, int height)
        {
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), info.RowBytes);
            if (SKImage.FromPixels(pixmap, (_, held) => ((GCHandle)held!).Free(), handle) is not { } drawn)
            {
                handle.Free();
                throw new InvalidOperationException("The picture could not be drawn.");
            }
            return new Pinned(drawn);
        }

        public void Dispose() => Image.Dispose();
    }

    private static SKColor SkiaColorOf(string? text, SKColor fallback) =>
        SKColor.TryParse(text?.Trim(), out var color) ? color.WithAlpha(255) : fallback;

    /// <summary>
    /// Brings <paramref name="image"/> to sRGB as it is seen, as merged pictures are: through its profile when it has
    /// one that isn't sRGB (a phone's Display P3, a print CMYK, a scanner's grey), else by formula (CMYK without one,
    /// grey). The background's colours are sRGB, and laid on another picture's numbers they came out shifted, and on a
    /// greyscale picture grey. The window shows its preview through the same.
    /// </summary>
    public static void ToSrgb(IMagickImage<byte> image)
    {
        if (image.GetColorProfile() is { } profile && profile.Description?.Contains("sRGB", StringComparison.OrdinalIgnoreCase) != true)
        {
            image.RenderingIntent = RenderingIntent.Relative;
            image.TransformColorSpace(profile, ColorProfiles.SRGB);
        }
        if (image.ColorSpace != ColorSpace.sRGB)
            image.ColorSpace = ColorSpace.sRGB;
        if (image.ColorType is not (ColorType.TrueColor or ColorType.TrueColorAlpha))
            image.ColorType = image.HasAlpha ? ColorType.TrueColorAlpha : ColorType.TrueColor;
    }

    /// <summary>
    /// A picture of one's own to put behind, as it is seen: upright, in sRGB, white where it is see-through (a viewer
    /// would show its own page there; the window shows white too), and no larger than it takes to cover a
    /// <paramref name="coverWidth"/> × <paramref name="coverHeight"/> box when one is given (shrunk to fit it, a
    /// panorama behind a square was blown up eight times). Null when it isn't a picture of a common raster format, is
    /// larger than <see cref="MaxPixels"/>, or can't be read.
    /// </summary>
    public static MagickImage? ReadPictureBehind(string? path, int coverWidth = 0, int coverHeight = 0)
    {
        if (!IsPicturePath(path, out var format) || !File.Exists(path))
            return null;
        // As what its extension says, else as what its first bytes say it is (a JPEG saved as ".png"), of the same
        // kinds only.
        foreach (var kind in new[] { format, Sniff(path) }.Distinct())
        {
            if (kind == MagickFormat.Unknown)
                continue;
            MagickImage? picture = null;
            try
            {
                var settings = new MagickReadSettings { Format = kind };
                // Its size from its header first: a huge one (or one that only claims to be) is never decoded.
                var info = new MagickImageInfo(path, settings);
                if ((long)info.Width * info.Height > MaxPixels)
                    return null;
                // A JPEG decodes at a half, a quarter or an eighth of its size when that still covers the box whichever
                // way it is turned (its longer side against the picture's shorter one). Asked for the box's own size,
                // the decoder enlarged a picture whose shorter side was less, past the limit checked above.
                if (kind == MagickFormat.Jpeg && coverWidth > 0 && coverHeight > 0)
                {
                    var scale = Math.Max(coverWidth, coverHeight) / (double)Math.Min(info.Width, info.Height);
                    if (scale <= 0.5)
                        settings.SetDefine(MagickFormat.Jpeg, "size", $"{(int)Math.Ceiling(info.Width * scale)}x{(int)Math.Ceiling(info.Height * scale)}");
                }
                picture = new MagickImage(path, settings);
                picture.AutoOrient();
                ToSrgb(picture);
                picture.BackgroundColor = MagickColors.White;
                picture.Alpha(AlphaAction.Remove);
                if (coverWidth > 0 && coverHeight > 0)
                {
                    var scale = Math.Max(coverWidth / (double)picture.Width, coverHeight / (double)picture.Height);
                    if (scale < 1)
                    {
                        uint coverW = (uint)Math.Max(1, Math.Ceiling(picture.Width * scale)), coverH = (uint)Math.Max(1, Math.Ceiling(picture.Height * scale));
                        picture.Resize(new MagickGeometry(coverW, coverH) { IgnoreAspectRatio = true });
                    }
                }
                picture.ResetPage();
                return picture;
            }
            catch (MagickException)
            {
                picture?.Dispose();
            }
        }
        return null;
    }

    /// <summary>
    /// The kind of picture the file's first bytes say it is, of <see cref="PictureFormats"/>; else Unknown.
    /// </summary>
    private static MagickFormat Sniff(string path)
    {
        var head = new byte[16];
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < 12)
                return MagickFormat.Unknown;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return MagickFormat.Unknown;
        }
        bool At(int offset, string text) => System.Text.Encoding.ASCII.GetString(head, offset, text.Length) == text;
        if (head[0] == 0x89 && At(1, "PNG"))
            return MagickFormat.Png;
        if (head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
            return MagickFormat.Jpeg;
        if (At(0, "GIF8"))
            return MagickFormat.Gif;
        if (At(0, "RIFF") && At(8, "WEBP"))
            return MagickFormat.WebP;
        if (At(0, "BM"))
            return MagickFormat.Bmp;
        if ((At(0, "II") && head[2] == 42 && head[3] == 0) || (At(0, "MM") && head[2] == 0 && head[3] == 42))
            return MagickFormat.Tiff;
        if (At(4, "ftyp"))
            return At(8, "avif") || At(8, "avis") ? MagickFormat.Avif : At(8, "heic") || At(8, "heix") || At(8, "mif1") ? MagickFormat.Heic : MagickFormat.Unknown;
        return MagickFormat.Unknown;
    }

    /// <summary>
    /// The extensions of the pictures that can be put behind, for the window's file picker ("*.png" and "*.PNG": the
    /// picker on Linux matches them as written).
    /// </summary>
    public static IReadOnlyList<string> PictureBehindPatterns =>
        [.. PictureFormats.Keys.SelectMany(extension => new[] { "*" + extension, "*" + extension.ToUpperInvariant() })];

    /// <summary>
    /// True for a full path (on this computer or a network share) to a picture of a format in
    /// <see cref="PictureFormats"/>; never a Windows device path (<c>\\?\</c>, <c>\\.\</c>).
    /// </summary>
    internal static bool IsPicturePath(string? path, out MagickFormat format)
    {
        format = MagickFormat.Unknown;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || IsDevicePath(path))
            return false;
        return PictureFormats.TryGetValue(Path.GetExtension(path), out format);
    }

    /// <summary>
    /// A Windows device path, written with either slash: two slashes, "?" or ".", a slash (\\?\, \\.\, //./), or \??\.
    /// </summary>
    private static bool IsDevicePath(string path)
    {
        static bool Slash(char c) => c is '\\' or '/';
        return path.StartsWith(@"\??\", StringComparison.Ordinal)
               || (path.Length >= 4 && Slash(path[0]) && Slash(path[1]) && path[2] is '?' or '.' && Slash(path[3]));
    }

    /// <summary>How a shadow is drawn under a picture (<see cref="ShadowOf"/>).</summary>
    /// <param name="Sigma">The blur's sigma, in pixels.</param>
    /// <param name="Drop">How far down the shadow falls, in pixels.</param>
    /// <param name="Opacity">How dark it is where the picture is solid, 0–1.</param>
    public readonly record struct ShadowShape(double Sigma, double Drop, double Opacity);

    /// <summary>
    /// The shadow for <paramref name="strength"/> (0–100): wider, lower and darker the stronger it is, in parts of the
    /// picture's shorter side. The window's live preview draws the same one (an Avalonia drop shadow).
    /// </summary>
    public static ShadowShape ShadowOf(int width, int height, int strength)
    {
        var amount = Math.Clamp(strength, 0, BackdropOptions.MaxShadow) / (double)BackdropOptions.MaxShadow;
        var side = Math.Min(width, height);
        return new ShadowShape(side * (0.012 + 0.045 * amount), side * (0.006 + 0.03 * amount), 0.18 + 0.42 * amount);
    }
}
