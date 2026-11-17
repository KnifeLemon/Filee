// G-code (3D printers and CNC machines) → SVG, PDF and pictures, without a slicer: the toolpath is read
// (GcodeToolpath) and drawn (GcodeDrawing) with SkiaSharp. For a picture, the preset can ask for the preview the slicer
// saved in the file instead (GcodeThumbnail).

using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Filee.Engines.Magick;
using Filee.Engines.Vector;
using ImageMagick;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace Filee.Engines.Gcode;

/// <summary>G-code → SVG, PDF, PNG, JPG, WEBP, TIFF, BMP, GIF, ICO, AVIF, …</summary>
/// <remarks>
/// The toolpath is drawn from above or in an isometric view (<see cref="GcodeOptions.View"/>); with
/// <see cref="GcodeView.Thumbnail"/>, pictures are the slicer's own preview when the file has one. PDF is direct (not
/// through a picture), so it stays vector; DXF and DWG come from the SVG through the vector and CAD engines.
/// </remarks>
public sealed class GcodeConverter(ILogger? logger = null) : IConverter, IImageTransformer
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public string Id => "gcode";
    public string DisplayName => "G-code (built-in)";

    /// <summary>A large print's toolpath takes a few hundred MB while it is drawn, so only a few run at once.</summary>
    public int MaxParallelism => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    /// <summary>Only when drawing to a picture (not to SVG or PDF).</summary>
    public bool Transforms(string from, string to) => ImageEncoder.Writable.Contains(to);

    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        new("gcode", "svg"),
        new("gcode", "pdf"),
        .. ImageEncoder.Writable.Select(target => ImageEncoder.Edge("gcode", target)),
    ];

    public EngineStatus GetStatus() => EngineStatus.Available(version: EngineVersions.BuiltIn);

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            var view = step.Preset.Gcode.View;
            if (view == GcodeView.Thumbnail && ImageEncoder.Writable.Contains(step.To))
            {
                if (Thumbnail(step, cancellationToken) is { } written)
                    return written;
                step.Notice?.Invoke("notice.gcode_no_thumbnail");
            }
            progress?.Report(0.05);
            var toolpath = GcodeToolpath.Read(step.InputPath, cancellationToken);
            _logger.LogInformation("{File}: {Kind} toolpath, {Lines} line(s), {Points} point(s), {Layers} layer(s)", step.InputPath,
                toolpath.IsPrinter ? "3D printer" : "CNC", toolpath.Lines.Count, toolpath.Lines.Sum(l => l.Points.Count), toolpath.Layers);
            progress?.Report(0.5);
            var scene = GcodeDrawing.Build(toolpath, view);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0.7);
            var result = step.To switch
            {
                "svg" => WriteSvg(step, scene),
                "pdf" => WritePdf(step, scene),
                _ => WritePicture(step, scene),
            };
            progress?.Report(1);
            return result;
        }, cancellationToken);

    private static IReadOnlyList<string> WriteSvg(ConversionStep step, GcodeDrawing.Scene scene)
    {
        if (step.Output.Allocate("svg") is not { } output)
            return [];
        using (var writer = new StreamWriter(output, false, new UTF8Encoding(false)))
            GcodeDrawing.WriteSvg(scene, writer);
        return [output];
    }

    private static IReadOnlyList<string> WritePdf(ConversionStep step, GcodeDrawing.Scene scene)
    {
        if (step.Output.Allocate("pdf") is not { } output)
            return [];
        GcodeDrawing.WritePdf(scene, output, FormatRegistry.NameWithoutExtension(step.InputPath), step.Preset.Document.PdfA);
        return [output];
    }

    /// <summary>
    /// The drawing as a picture, rendered at the size it ends up at as the vector engine renders an SVG (the preset's
    /// render DPI, resize or custom size; see <see cref="SvgRenderer.RasterScale"/>).
    /// </summary>
    private static IReadOnlyList<string> WritePicture(ConversionStep step, GcodeDrawing.Scene scene)
    {
        const float pixelsPerMm = SvgRenderer.CssDpi / 25.4f;
        var bounds = new SKRect(0, 0, scene.Width * pixelsPerMm, scene.Height * pixelsPerMm);
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(bounds);
        canvas.Scale(pixelsPerMm);
        GcodeDrawing.Draw(scene, canvas);
        using var picture = recorder.EndRecording();
        var scale = SvgRenderer.RasterScale(bounds, step.Preset.Image, step.Preset.Pdf);
        using var image = SvgRenderer.Rasterize(picture, bounds, scale);
        // Rendered at its final size: the preset's resize must not scale the bitmap again (as VectorConverter.ToRaster).
        var options = step.Preset.Image.Clone();
        options.Resize = ResizeMode.None;
        options.MinShortEdge = null;
        ImageEncoder.ApplyOptions(image, options, step.To);
        return ImageEncoder.Write([image], step.To, step.Output, options);
    }

    /// <summary>The slicer's preview as the picture, or null when the file has none that reads.</summary>
    private IReadOnlyList<string>? Thumbnail(ConversionStep step, CancellationToken ct)
    {
        GcodeThumbnail.Image? thumbnail;
        using (var stream = GcodeToolpath.Open(step.InputPath))
            thumbnail = GcodeThumbnail.Largest(stream, ct);
        if (thumbnail is null)
            return null;
        MagickImage image;
        try
        {
            // The header first: a thumbnail that says it is small may still unpack to a huge picture.
            var settings = new MagickReadSettings { Format = ImageEncoder.ToMagickFormat(thumbnail.Format) };
            var info = new MagickImageInfo(thumbnail.Data, settings);
            if (info.Width is 0 or > GcodeThumbnail.MaxSide || info.Height is 0 or > GcodeThumbnail.MaxSide)
            {
                _logger.LogWarning("{File}: the {Format} thumbnail is {Width}×{Height}, past {Max} pixels", step.InputPath, thumbnail.Format, info.Width, info.Height, GcodeThumbnail.MaxSide);
                return null;
            }
            image = new MagickImage(thumbnail.Data, settings);
        }
        catch (MagickException ex)
        {
            _logger.LogWarning("{File}: the {Format} thumbnail doesn't read: {Message}", step.InputPath, thumbnail.Format, ex.Message);
            return null;
        }
        using (image)
        {
            _logger.LogInformation("{File}: the slicer's {Width}×{Height} {Format} thumbnail", step.InputPath, image.Width, image.Height, thumbnail.Format);
            ImageEncoder.ApplyOptions(image, step.Preset.Image, step.To);
            return ImageEncoder.Write([image], step.To, step.Output, step.Preset.Image);
        }
    }
}
