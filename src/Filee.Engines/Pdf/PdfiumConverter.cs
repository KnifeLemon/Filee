// Renders PDF pages to raster images with PDFium (via PDFtoImage, MIT / PDFium BSD-3): one image per page, one
// multi-page TIFF or GIF, or (PdfOptions.CombinePages) the pages stacked into one tall image. PDFium renders RGB: a
// document drawn in CMYK (a print file, issue #1027) is rendered by Ghostscript instead when the target holds CMYK.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Filee.Engines.Magick;
using Filee.Engines.Vector;
using ImageMagick;
using PDFtoImage;
using SkiaSharp;

// PDFtoImage marks its API for the platforms PDFium ships for (Windows, macOS, Linux, mobile).
// Filee only targets desktop platforms that are all covered.
#pragma warning disable CA1416

namespace Filee.Engines.Pdf;

/// <summary>PDF → every raster format ImageMagick writes (PNG, JPG, WEBP, TIFF, …, PSD, TGA, PPM).</summary>
public sealed class PdfiumConverter : IConverter, IImageTransformer
{
    private static readonly HashSet<string> MultiPageTargets = ["tiff", "gif", "apng", "mng"];

    /// <summary>
    /// Targets that store CMYK pictures, the ones a print shop takes. Pages drawn in CMYK (<see cref="PdfCmykContent"/>)
    /// are rendered in CMYK for them when Ghostscript is installed; a grayscale preset keeps PDFium's rendering.
    /// </summary>
    private static readonly HashSet<string> CmykTargets = ["jpg", "tiff"];

    /// <summary>Where Ghostscript is, or null; a test sets it to see a PC without it.</summary>
    internal static Func<string?> FindGhostscript { get; set; } = GhostscriptConverter.Locate;

    /// <summary>A tall image beyond this many pixels is scaled down (a 300-page PDF at 300 dpi would need gigabytes).</summary>
    internal const long MaxStackedPixels = 200_000_000;

    public string Id => "pdfium";
    public string DisplayName => "PDFium";

    /// <summary>PDFium is not thread-safe.</summary>
    public int MaxParallelism => 1;

    /// <summary>Renders pages to images, then applies the image options to them.</summary>
    public bool Transforms(string from, string to) => true;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
        ImageEncoder.Writable.Select(target => new ConversionEdge("pdf", target)).ToList();

    public EngineStatus GetStatus() => EngineStatus.Available(version: EngineVersions.Library("PDFtoImage", typeof(Conversion)));

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(async () =>
        {
            var pdf = await File.ReadAllBytesAsync(step.InputPath, cancellationToken);
            var pageCount = Conversion.GetPageCount(pdf, null);
            var pages = PageRange.Parse(step.Preset.Pdf.PageRange, pageCount);
            var dpi = Math.Clamp(step.Preset.Pdf.RenderDpi, 36, 1200);

            if (CmykTargets.Contains(step.To) && !step.Preset.Image.Grayscale
                && PdfCmykContent.Inspect(step.InputPath, pages) is { UsesCmyk: true } cmyk)
            {
                // A print file that comes out in RGB says so, with the reason: print work shouldn't change colour unseen.
                if (FindGhostscript() is not { } gs)
                {
                    step.Notice?.Invoke("notice.cmyk_needs_ghostscript");
                }
                else
                {
                    IReadOnlyList<string> files = [];
                    try
                    {
                        files = await GhostscriptConverter.RenderCmykAsync(gs, step.InputPath, pages.Count < pageCount ? pages : null, dpi,
                            cmyk.OutputProfile, step.WorkDirectory, cancellationToken, progress is null ? null : new SubProgress(progress, 0, 0.6));
                    }
                    catch (InvalidOperationException)
                    {
                        // Ghostscript fails on some files PDFium renders: those come out in RGB, as before.
                    }
                    if (files.Count == pages.Count)
                    {
                        var frames = files.Select(file => ReadCmyk(file, dpi, cmyk.OutputProfile));
                        return Encode(step, pages, dpi, frames, progress is null ? null : new SubProgress(progress, 0.6, 0.4), cancellationToken);
                    }
                    step.Notice?.Invoke("notice.cmyk_render_failed");
                }
            }

            var options = new RenderOptions(Dpi: dpi, WithAnnotations: true, WithFormFill: true, BackgroundColor: SKColors.White);
            var rendered = Conversion.ToImages(pdf, pages, null, options).Select(bitmap => ToMagick(bitmap, dpi));
            return Encode(step, pages, dpi, rendered, progress, cancellationToken);
        }, cancellationToken);

    /// <summary>Applies the image options to the rendered pages and writes them: one file per page, one multi-page file or one tall image.</summary>
    private static List<string> Encode(ConversionStep step, IReadOnlyList<int> pages, int dpi, IEnumerable<IMagickImage<byte>> rendered,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var written = new List<string>();
        var collected = new List<IMagickImage<byte>>();
        var (format, extension) = ImageEncoder.OutputFormat(step.To, step.Preset.Image);
        var stack = step.Preset.Pdf.CombinePages && pages.Count > 1 && !MultiPageTargets.Contains(step.To);

        try
        {
            var index = 0;
            foreach (var frame in rendered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pageNumber = pages[index] + 1;
                index++;

                if (stack)
                {
                    // Each page turns, then they stack: a turned long image would lie on its side instead.
                    ImageEncoder.Turn(frame, step.Preset.Image);
                    if (step.Preset.Pdf.TrimPageMargins)
                        TrimVertically(frame, dpi);
                    collected.Add(frame); // stacked after the loop; the image options apply to the tall image
                    progress?.Report((double)index / pages.Count * 0.8);
                    continue;
                }
                ImageEncoder.ApplyOptions(frame, step.Preset.Image, step.To);

                if (MultiPageTargets.Contains(step.To))
                {
                    collected.Add(frame); // one multi-page file, written after the loop
                }
                else
                {
                    using (frame)
                    {
                        var path = step.Output.Allocate(extension, pages.Count > 1 ? $"_p{pageNumber}" : null);
                        if (path is not null)
                        {
                            frame.Write(path, format);
                            written.Add(path);
                        }
                    }
                }
                progress?.Report((double)index / pages.Count);
            }

            if (stack)
            {
                var unturned = step.Preset.Image.Clone(); // the pages are turned already
                unturned.Rotation = ImageRotation.None;
                unturned.Flip = ImageFlip.None;
                var parts = step.Preset.Pdf.SplitTallImage ? Split(collected, step.To, unturned) : [collected];
                for (var part = 0; part < parts.Count; part++)
                {
                    using var tall = Stack(parts[part]);
                    ImageEncoder.ApplyOptions(tall, unturned, step.To); // also scales it to fit the format
                    if (step.Output.Allocate(extension, parts.Count > 1 ? $"_{part + 1}" : null) is { } path)
                    {
                        tall.Write(path, format);
                        written.Add(path);
                    }
                }
            }
            else if (collected.Count > 0)
            {
                written.AddRange(ImageEncoder.Write(collected, step.To, step.Output, step.Preset.Image));
            }
        }
        finally
        {
            foreach (var frame in collected)
                frame.Dispose();
        }
        return written;
    }

    /// <summary>
    /// The pages one under another, centred on a white strip as wide as the widest page. Scaled down when larger than
    /// <see cref="MaxStackedPixels"/>; <see cref="ImageEncoder.ApplyOptions"/> then fits it to what the format stores
    /// (JPEG 65,500 px, WebP 16,383 px).
    /// </summary>
    internal static IMagickImage<byte> Stack(IReadOnlyList<IMagickImage<byte>> pages)
    {
        double width = pages.Max(p => p.Width);
        double height = pages.Sum(p => (double)p.Height);
        var scale = Math.Min(1, Math.Sqrt(MaxStackedPixels / (width * height)));
        var stripWidth = (uint)Math.Max(1, Math.Floor(width * scale));
        using var strip = new MagickImageCollection();
        foreach (var page in pages)
        {
            var copy = page.Clone();
            if (scale < 1)
                copy.Resize((uint)Math.Max(1, Math.Floor(copy.Width * scale)), (uint)Math.Max(1, Math.Floor(copy.Height * scale)));
            copy.BackgroundColor = MagickColors.White;
            copy.Extent(stripWidth, copy.Height, Gravity.North);
            strip.Add(copy);
        }
        var tall = strip.AppendVertically();
        tall.Density = pages[0].Density;
        return tall;
    }

    /// <summary>
    /// Pages in runs short enough for one image of the format after the preset's resize (issue #50: a 44-page
    /// certificate at 1200 px wide is 74,673 px tall, more than a JPG holds). Runs break between pages; a single page
    /// taller than the format is a run of its own and is scaled down.
    /// </summary>
    internal static List<IReadOnlyList<IMagickImage<byte>>> Split(IReadOnlyList<IMagickImage<byte>> pages, string target, ImageOptions options)
    {
        double width = pages.Max(p => p.Width);
        double height = pages.Sum(p => (double)p.Height);
        var scale = ImageEncoder.ResizeScale(width, height, options) * Math.Min(1, Math.Sqrt(MaxStackedPixels / (width * height)));
        var limit = ImageEncoder.MaxSide(target) / scale;
        var parts = new List<IReadOnlyList<IMagickImage<byte>>>();
        var run = new List<IMagickImage<byte>>();
        double runHeight = 0;
        foreach (var page in pages)
        {
            if (run.Count > 0 && runHeight + page.Height > limit)
            {
                parts.Add(run);
                run = [];
                runHeight = 0;
            }
            run.Add(page);
            runHeight += page.Height;
        }
        parts.Add(run);
        return parts;
    }

    /// <summary>
    /// Cuts the blank rows above and below a page's content, keeping about 4 mm so text does not touch the next page.
    /// A blank page shrinks to that margin.
    /// </summary>
    internal static void TrimVertically(IMagickImage<byte> page, int dpi)
    {
        var margin = Math.Max(4, dpi / 6);
        uint top, bottom;
        using (var probe = page.Clone())
        {
            probe.ColorFuzz = new Percentage(8); // scanned pages are not pure white
            var box = probe.BoundingBox;
            if (box is null || box.Width == 0 || box.Height == 0 || box.Height >= page.Height)
            {
                if (box is not null && box.Height >= page.Height)
                    return;
                top = 0;
                bottom = (uint)Math.Min(page.Height, margin * 2);
            }
            else
            {
                top = (uint)Math.Max(0, box.Y - margin);
                bottom = (uint)Math.Min(page.Height, box.Y + box.Height + margin);
            }
        }
        page.Crop(new MagickGeometry(0, (int)top, page.Width, bottom - top));
        page.ResetPage();
    }

    /// <summary>
    /// One page Ghostscript rendered. Its TIFF carries Ghostscript's default CMYK profile, which isn't the document's:
    /// the picture carries the output intent's profile or none (untagged CMYK keeps its numbers, and the print shop
    /// applies its own).
    /// </summary>
    private static MagickImage ReadCmyk(string file, int dpi, byte[]? profile)
    {
        var image = new MagickImage(file);
        image.RemoveProfile("icc");
        if (profile is not null)
            image.SetProfile(new ColorProfile(profile));
        image.Density = new Density(dpi, dpi, DensityUnit.PixelsPerInch);
        return image;
    }

    private static MagickImage ToMagick(SKBitmap bitmap, int dpi)
    {
        using (bitmap)
        using (var data = bitmap.Encode(SKEncodedImageFormat.Png, 100))
        {
            var image = new MagickImage(data.ToArray());
            image.Density = new Density(dpi, dpi, DensityUnit.PixelsPerInch);
            return image;
        }
    }

    private sealed class SubProgress(IProgress<double> parent, double start, double length) : IProgress<double>
    {
        public void Report(double value) => parent.Report(start + length * value);
    }
}
