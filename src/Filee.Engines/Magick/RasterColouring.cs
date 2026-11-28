// Colour-mapped PNG previews of single-band GeoTIFF and SRTM .hgt rasters (#155): valid samples stretch linearly
// from min..max onto a 5-stop sRGB colour ramp (Terrain for elevation, Viridis for general scalar/index values);
// flat rasters map to the ramp midpoint (t = 0.5) and no-data / NaN cells become fully transparent.

using System.Globalization;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using ImageMagick;

namespace Filee.Engines.Magick;

/// <summary>Renders a single-band GeoTIFF or SRTM <c>.hgt</c> raster to a colour-mapped RGBA PNG.</summary>
internal static class RasterColouring
{
    private readonly record struct Rgb(byte R, byte G, byte B);

    // Evenly spaced stops at t = 0, 0.25, 0.5, 0.75, 1.
    private static readonly Rgb[] TerrainStops =
    [
        new(0x00, 0x61, 0x30), // #006130
        new(0x6A, 0xAE, 0x45), // #6AAE45
        new(0xE8, 0xD5, 0x7A), // #E8D57A
        new(0x9C, 0x6B, 0x3A), // #9C6B3A
        new(0xFF, 0xFF, 0xFF), // #FFFFFF
    ];

    private static readonly Rgb[] ViridisStops =
    [
        new(0x44, 0x01, 0x54), // #440154
        new(0x3B, 0x52, 0x8B), // #3B528B
        new(0x21, 0x91, 0x8C), // #21918C
        new(0x5E, 0xC9, 0x62), // #5EC962
        new(0xFD, 0xE7, 0x25), // #FDE725
    ];

    /// <summary>
    /// Decodes a single-band GeoTIFF or SRTM <c>.hgt</c> file, maps its valid values onto the preset's colour ramp,
    /// and writes an RGBA PNG atomically via a temporary file beside the output.
    /// </summary>
    /// <exception cref="ConversionRefusedException">
    /// The input is not a supported single-band raster (wrong format, multi-band TIFF, or unsupported TIFF layout).
    /// </exception>
    public static IReadOnlyList<string> WritePng(
        ConversionStep step,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var (width, height, band, min, max, hasValid) = ReadSingleBand(step.InputPath, step.From, cancellationToken);
        progress?.Report(0.4);

        using var image = Render(width, height, band, min, max, hasValid, step.Preset.Image.RasterColours, cancellationToken);
        ImageEncoder.ApplyOptions(image, step.Preset.Image, "png");
        progress?.Report(0.8);

        var output = step.Output.Allocate("png");
        if (output is null)
            return [];

        var directory = Path.GetDirectoryName(Path.GetFullPath(output))!;
        var temp = Path.Combine(directory, $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.tmp");
        try
        {
            image.Write(temp, MagickFormat.Png32);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, output, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        progress?.Report(1);
        return [output];
    }

    private static (int Width, int Height, float[] Band, double Min, double Max, bool HasValid) ReadSingleBand(
        string path,
        string format,
        CancellationToken cancellationToken)
    {
        if (format == "hgt")
        {
            var tile = Hgt.Read(path, cancellationToken);
            var band = new float[tile.Samples.Length];
            for (var y = 0; y < tile.Side; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rowOffset = y * tile.Side;
                for (var x = 0; x < tile.Side; x++)
                {
                    var sample = tile.Samples[rowOffset + x];
                    band[rowOffset + x] = sample == Hgt.Void ? float.NaN : sample;
                }
            }
            return (tile.Side, tile.Side, band, tile.Min, tile.Max, tile.HasValid);
        }

        if (format != "tiff")
            throw new ConversionRefusedException("error.unsupported_source", format);

        var raster = GeoTiff.TryReadBands(path, acceptEightBit: true, cancellationToken, onlyPreviewBands: true);
        if (raster is null)
            throw new ConversionRefusedException("error.raster_not_geotiff");
        if (raster.SamplesPerPixel != 1 || raster.Bands.Length != 1)
        {
            var bandCount = Math.Max(raster.SamplesPerPixel, raster.Bands.Length);
            throw new ConversionRefusedException("error.raster_needs_one_band", bandCount.ToString(CultureInfo.InvariantCulture));
        }

        return (raster.Width, raster.Height, raster.Bands[0], raster.Mins[0], raster.Maxs[0], raster.HasValid[0]);
    }

    /// <summary>Renders one float band (NaN = no-data) onto an 8-bit RGBA <see cref="MagickImage"/>.</summary>
    internal static MagickImage Render(
        int width,
        int height,
        float[] band,
        double min,
        double max,
        bool hasValid,
        RasterColours ramp,
        CancellationToken cancellationToken)
    {
        var stops = ramp == RasterColours.Viridis ? ViridisStops : TerrainStops;
        var rgba = new byte[(long)width * height * 4];
        var range = max - min;
        var mid = SampleRamp(stops, 0.5);

        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowBase = y * width;
            for (var x = 0; x < width; x++)
            {
                var value = band[rowBase + x];
                if (!hasValid || !float.IsFinite(value))
                    continue; // RGBA already zeroed (fully transparent)

                Rgb colour;
                if (range > 0)
                {
                    var v = (double)value;
                    var t = double.IsInfinity(range)
                        ? (v * 0.5 - min * 0.5) / (max * 0.5 - min * 0.5)
                        : (v - min) / range;
                    colour = SampleRamp(stops, t);
                }
                else
                {
                    colour = mid;
                }

                var p = (rowBase + x) * 4;
                rgba[p] = colour.R;
                rgba[p + 1] = colour.G;
                rgba[p + 2] = colour.B;
                rgba[p + 3] = 255;
            }
        }

        var image = new MagickImage();
        try
        {
            image.ReadPixels(rgba, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.RGBA));
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private static Rgb SampleRamp(Rgb[] stops, double t)
    {
        var clamped = Math.Clamp(t, 0.0, 1.0);
        var scaled = clamped * (stops.Length - 1);
        var index = Math.Min((int)scaled, stops.Length - 2);
        var frac = scaled - index;
        var a = stops[index];
        var b = stops[index + 1];
        return new(
            (byte)Math.Clamp(Math.Round(a.R + (b.R - a.R) * frac), 0, 255),
            (byte)Math.Clamp(Math.Round(a.G + (b.G - a.G) * frac), 0, 255),
            (byte)Math.Clamp(Math.Round(a.B + (b.B - a.B) * frac), 0, 255));
    }
}
