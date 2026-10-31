// Filee's own EMF / WMF renderer, the same on every platform: EmfPlayer and WmfPlayer read the records, GdiPlayer
// draws them on an SKCanvas, recorded into an SKPicture. Pictures of the picture are rendered from it at any size, and
// PDF pages and the layout engine's pages replay it as vector drawing (paths, text as outlines, bitmaps as images).
//
// Used by the image engine (EMF / WMF → PNG, JPG, …, Magick/ImageReader), the vector engine (EMF / WMF → PDF,
// Vector/VectorConverter) and the layout engine for pictures in documents (Typesetting/FloatingObjects, TableLayout).
// Limits for hostile files are in MetafileLimits.

using SkiaSharp;

namespace Filee.Engines.Metafiles;

internal enum MetafileFormat
{
    Emf,
    Wmf,
}

/// <summary>A metafile recorded as a Skia picture.</summary>
internal sealed class MetafileDrawing(SKPicture picture, SKSize size, float pointsPerUnit) : IDisposable
{
    /// <summary>The drawing in device units, the picture frame's top-left corner at (0, 0).</summary>
    public SKPicture Picture { get; } = picture;

    /// <summary>The picture frame in device units.</summary>
    public SKSize Size { get; } = size;

    /// <summary>Points per device unit.</summary>
    public float PointsPerUnit { get; } = pointsPerUnit;

    /// <summary>Width in points.</summary>
    public float Width => Size.Width * PointsPerUnit;

    /// <summary>Height in points.</summary>
    public float Height => Size.Height * PointsPerUnit;

    /// <summary>
    /// Draws the picture stretched into <paramref name="area"/>, clipped to it. <paramref name="crop"/> cuts fractions
    /// of the picture away on each side first.
    /// </summary>
    public void Draw(SKCanvas canvas, SKRect area, (double Left, double Top, double Right, double Bottom) crop = default)
    {
        var source = new SKRect((float)(crop.Left * Size.Width), (float)(crop.Top * Size.Height),
            (float)((1 - crop.Right) * Size.Width), (float)((1 - crop.Bottom) * Size.Height));
        if (source.Width <= 0 || source.Height <= 0 || area.Width <= 0 || area.Height <= 0)
            return;
        canvas.Save();
        canvas.ClipRect(area);
        canvas.Translate(area.Left, area.Top);
        canvas.Scale(area.Width / source.Width, area.Height / source.Height);
        canvas.Translate(-source.Left, -source.Top);
        canvas.DrawPicture(Picture);
        canvas.Restore();
    }

    public void Dispose() => Picture.Dispose();
}

/// <summary>Reads, draws and renders EMF and WMF files.</summary>
internal static class Metafile
{
    /// <summary>Upper bounds for rendered bitmaps, so a huge picture doesn't exhaust memory (as for SVG).</summary>
    private const int MaximumLongEdge = 16384;
    private const long MaximumPixels = 100_000_000;

    /// <summary>The format the bytes start like; null when they are neither EMF nor WMF.</summary>
    public static MetafileFormat? Detect(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 44 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(head) == 1
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(head[40..]) == EmfHeader.Signature)
            return MetafileFormat.Emf;
        if (head.Length >= 4 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(head) == WmfHeader.PlaceableKey)
            return MetafileFormat.Wmf;
        return WmfHeader.IsMetaHeader(head, 0) ? MetafileFormat.Wmf : null;
    }

    /// <summary>True when the file at <paramref name="path"/> is an EMF or WMF.</summary>
    public static bool IsMetafile(string path)
    {
        try
        {
            Span<byte> head = stackalloc byte[44];
            using var stream = File.OpenRead(path);
            var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            return Detect(head[..read]) is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The picture's size in points from its header; null when the header can't be read.</summary>
    public static (float Width, float Height)? Size(ReadOnlySpan<byte> data)
    {
        try
        {
            var (picture, pointsPerUnit) = Frame(data);
            return (picture.Width * pointsPerUnit, picture.Height * pointsPerUnit);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static (SKRect Picture, float PointsPerUnit) Frame(ReadOnlySpan<byte> data)
    {
        switch (Detect(data))
        {
            case MetafileFormat.Emf:
                var emf = EmfHeader.Read(data);
                return (emf.Picture, emf.PointsPerUnit);
            case MetafileFormat.Wmf:
                var wmf = WmfHeader.Read(data);
                return (wmf.Picture, wmf.PointsPerUnit);
            default:
                throw new InvalidDataException("The file is neither an EMF nor a WMF metafile.");
        }
    }

    /// <summary>Reads and draws the metafile at <paramref name="path"/>.</summary>
    /// <inheritdoc cref="Draw(byte[], float, MetafileLimits?, CancellationToken)"/>
    public static MetafileDrawing Load(string path, float pixelsPerPoint = 0, MetafileLimits? limits = null, CancellationToken cancellationToken = default)
    {
        limits ??= MetafileLimits.Default;
        var length = new FileInfo(path).Length;
        if (length > limits.MaxFileBytes)
            throw MetafileLimits.Exceeded($"The metafile is {Documents.DocumentLimits.Megabytes(length)}, more than {Documents.DocumentLimits.Megabytes(limits.MaxFileBytes)}.");
        return Draw(File.ReadAllBytes(path), pixelsPerPoint, limits, cancellationToken);
    }

    /// <summary>
    /// The metafile at <paramref name="path"/> for vector output, or null when it can't be drawn (damaged, EMF+ only, past
    /// a limit): a picture in a document is left out then, like a picture of any format that can't be read.
    /// </summary>
    public static MetafileDrawing? TryLoad(string path)
    {
        try
        {
            return Load(path);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException
                                       or Core.Conversion.ConversionRefusedException)
        {
            return null;
        }
    }

    /// <summary>Draws a metafile into a Skia picture.</summary>
    /// <param name="pixelsPerPoint">
    /// Output pixels per point when the picture is for a bitmap at that scale (lines then cover the pixels GDI's do);
    /// 0 for vector output (PDF, the layout engine's pages).
    /// </param>
    /// <exception cref="InvalidDataException">The file is not a metafile or is damaged.</exception>
    /// <exception cref="NotSupportedException">The file is EMF+ only (no EMF records to draw).</exception>
    /// <exception cref="Core.Conversion.ConversionRefusedException">The file passes one of <paramref name="limits"/>.</exception>
    public static MetafileDrawing Draw(byte[] data, float pixelsPerPoint = 0, MetafileLimits? limits = null, CancellationToken cancellationToken = default)
    {
        limits ??= MetafileLimits.Default;
        if (data.LongLength > limits.MaxFileBytes)
            throw MetafileLimits.Exceeded($"The metafile is {Documents.DocumentLimits.Megabytes(data.LongLength)}, more than {Documents.DocumentLimits.Megabytes(limits.MaxFileBytes)}.");
        var format = Detect(data) ?? throw new InvalidDataException("The file is neither an EMF nor a WMF metafile.");
        var (frame, pointsPerUnit) = Frame(data);
        if (!(frame.Width > 0 && frame.Height > 0 && float.IsFinite(frame.Width) && float.IsFinite(frame.Height)
              && frame.Width * pointsPerUnit < 1e6 && frame.Height * pointsPerUnit < 1e6))
            throw new InvalidDataException($"The {format.ToString().ToUpperInvariant()} file has no usable size (its frame is {frame.Width} × {frame.Height} units).");

        var pixel = pixelsPerPoint > 0 ? 1 / (pixelsPerPoint * pointsPerUnit) : 0;
        var budget = new MetafileBudget(limits);
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(new SKRect(0, 0, frame.Width, frame.Height));
        canvas.Translate(-frame.Left, -frame.Top);
        using (var text = new MetafileText())
        {
            if (format == MetafileFormat.Emf)
            {
                var header = EmfHeader.Read(data);
                using var gdi = new GdiPlayer(canvas, pixel, header.UnitsPerMm, frame, budget, text);
                EmfPlayer.Play(data, header, gdi, limits, cancellationToken);
            }
            else
            {
                var header = WmfHeader.Read(data);
                using var gdi = new GdiPlayer(canvas, pixel, header.UnitsPerMm, frame, budget, text);
                WmfPlayer.Play(data, header, gdi, limits, cancellationToken);
            }
        }
        var picture = recorder.EndRecording();
        return new MetafileDrawing(picture, new SKSize(frame.Width, frame.Height), pointsPerUnit);
    }

    /// <summary>
    /// Renders the metafile at <paramref name="path"/> on a transparent bitmap at <paramref name="dpi"/> (lowered when the
    /// bitmap would pass the size limits); the dpi used comes back with it.
    /// </summary>
    public static (SKBitmap Bitmap, float Dpi) Render(string path, float dpi, MetafileLimits? limits = null, CancellationToken cancellationToken = default)
    {
        limits ??= MetafileLimits.Default;
        var length = new FileInfo(path).Length;
        if (length > limits.MaxFileBytes)
            throw MetafileLimits.Exceeded($"The metafile is {Documents.DocumentLimits.Megabytes(length)}, more than {Documents.DocumentLimits.Megabytes(limits.MaxFileBytes)}.");
        return Render(File.ReadAllBytes(path), dpi, limits, cancellationToken);
    }

    /// <inheritdoc cref="Render(string, float, MetafileLimits?, CancellationToken)"/>
    public static (SKBitmap Bitmap, float Dpi) Render(byte[] data, float dpi, MetafileLimits? limits = null, CancellationToken cancellationToken = default)
    {
        var size = Size(data) ?? throw new InvalidDataException("The file is neither an EMF nor a WMF metafile.");
        var scale = dpi / 72;
        var longEdge = Math.Max(size.Width, size.Height);
        if (longEdge > 0)
            scale = Math.Min(scale, Math.Min(MaximumLongEdge / longEdge, MathF.Sqrt(MaximumPixels / Math.Max(1e-6f, size.Width * size.Height))));
        using var drawing = Draw(data, scale, limits, cancellationToken);
        return (Rasterize(drawing, scale), scale * 72);
    }

    /// <summary>The drawing on a transparent bitmap, <paramref name="pixelsPerPoint"/> pixels per point (premultiplied RGBA).</summary>
    public static SKBitmap Rasterize(MetafileDrawing drawing, float pixelsPerPoint)
    {
        var width = Math.Max(1, (int)Math.Round(drawing.Width * pixelsPerPoint));
        var height = Math.Max(1, (int)Math.Round(drawing.Height * pixelsPerPoint));
        return Rasterize(drawing, width, height);
    }

    /// <summary>The drawing stretched onto a transparent bitmap of <paramref name="width"/> × <paramref name="height"/> pixels.</summary>
    public static SKBitmap Rasterize(MetafileDrawing drawing, int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        drawing.Draw(canvas, new SKRect(0, 0, width, height));
        return bitmap;
    }

    /// <summary>Writes a one-page PDF the size of the picture, its drawing kept as vector graphics.</summary>
    public static void WritePdf(MetafileDrawing drawing, string output, string title, bool pdfA)
    {
        var metadata = SKDocumentPdfMetadata.Default;
        metadata.Title = title;
        metadata.Creator = "Filee";
        metadata.Producer = "Filee (Skia PDF)";
        metadata.Creation = DateTime.Now;
        metadata.Modified = DateTime.Now;
        metadata.RasterDpi = 300;
        metadata.EncodingQuality = 95;
        metadata.PdfA = pdfA;
        using (var stream = File.Create(output))
        using (var document = SKDocument.CreatePdf(stream, metadata))
        {
            var canvas = document.BeginPage(drawing.Width, drawing.Height);
            drawing.Draw(canvas, new SKRect(0, 0, drawing.Width, drawing.Height));
            document.EndPage();
            document.Close();
        }
        if (new FileInfo(output).Length == 0)
            throw new InvalidOperationException("Skia could not write the PDF.");
    }
}
