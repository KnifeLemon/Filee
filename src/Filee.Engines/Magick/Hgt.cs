// NASA SRTM / DEM elevation tiles (.hgt, #1793): headerless square grids of big-endian signed 16-bit elevations in
// metres, row-major with the north row first, -32768 marking void (no-data) cells. Side length comes from the file
// size (2 × n × n bytes: 1201 for SRTM-3, 3601 for SRTM-1, or any square in 2..10001).
// - TIFF (Filee's TiffWriter): single-channel signed 16-bit (SampleFormat = 2) preserving the exact elevations and
//   -32768 voids (GDAL_NODATA tag 42113), plus WGS 84 GeoTIFF placement (tags 33550, 33922, 34735, PixelIsPoint) when
//   the file name parses as a south-west tile corner such as N37W122.hgt or S12E045.hgt.
// - PNG and every other picture format: linear grayscale stretch from min..max valid elevation to 0..255, voids black.

using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using ImageMagick;

namespace Filee.Engines.Magick;

/// <summary>An elevation grid decoded from an SRTM <c>.hgt</c> tile.</summary>
internal sealed record HgtTile(int Side, short[] Samples, short Min, short Max, bool HasValid);

/// <summary>South-west corner of a 1° × 1° SRTM tile, in signed integer WGS 84 degrees.</summary>
internal readonly record struct HgtCorner(int Latitude, int Longitude);

/// <summary>Reads SRTM <c>.hgt</c> elevation tiles into signed 16-bit GeoTIFFs or stretched grayscale previews.</summary>
internal static partial class Hgt
{
    /// <summary>Sentinel elevation marking missing data (voids) in SRTM tiles.</summary>
    public const short Void = short.MinValue;

    /// <summary>Smallest accepted grid side length (2 × 2 samples).</summary>
    public const int MinSide = 2;

    /// <summary>Largest accepted grid side length (10001 × 10001 samples, ~200 MB).</summary>
    public const int MaxSide = 10_001;

    /// <summary>GeoTIFF tag storing pixel spacing (scaleX, scaleY, scaleZ) in model units (degrees).</summary>
    public const ushort ModelPixelScaleTag = 33550;

    /// <summary>GeoTIFF tag mapping raster coordinates (I, J, K) to model coordinates (X, Y, Z).</summary>
    public const ushort ModelTiepointTag = 33922;

    /// <summary>GeoTIFF tag storing the projection/coordinate-system GeoKeys.</summary>
    public const ushort GeoKeyDirectoryTag = 34735;

    /// <summary>GDAL ASCII tag declaring the no-data sentinel value.</summary>
    public const ushort GdalNoDataTag = 42113;

    [GeneratedRegex(@"^([NS])(\d{2})([EW])(\d{3})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TileNamePattern();

    /// <summary>
    /// Parses a standard SRTM tile file name (e.g. <c>N37W122.hgt</c>, <c>s12e045.hgt</c>) into its south-west corner
    /// in signed degrees, or returns <c>null</c> when the name does not encode a valid WGS 84 1° × 1° tile.
    /// </summary>
    public static HgtCorner? TryParseCorner(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(stem))
            return null;
        var match = TileNamePattern().Match(stem);
        if (!match.Success)
            return null;

        var latDeg = int.Parse(match.Groups[2].ValueSpan, CultureInfo.InvariantCulture);
        var lonDeg = int.Parse(match.Groups[4].ValueSpan, CultureInfo.InvariantCulture);
        var lat = char.ToUpperInvariant(match.Groups[1].ValueSpan[0]) == 'S' ? -latDeg : latDeg;
        var lon = char.ToUpperInvariant(match.Groups[3].ValueSpan[0]) == 'W' ? -lonDeg : lonDeg;

        // A 1° × 1° tile spans [lat, lat + 1] × [lon, lon + 1], so lat must lie in [-90, 89] and lon in [-180, 179].
        if (lat is < -90 or > 89 || lon is < -180 or > 179)
            return null;
        return new HgtCorner(lat, lon);
    }

    /// <summary>Reads and validates a headerless SRTM <c>.hgt</c> file.</summary>
    /// <exception cref="ConversionRefusedException">The file is empty, not a square 16-bit grid, or outside the supported size range.</exception>
    public static HgtTile Read(string path, CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var length = stream.Length;
        if (length == 0)
            throw new ConversionRefusedException("error.unsupported_source", "Empty SRTM .hgt file");
        if ((length & 1) != 0)
            throw new ConversionRefusedException("error.unsupported_source", $"SRTM .hgt file size ({length} bytes) is not a whole number of 16-bit samples");

        var count = length / 2;
        var sideLong = (long)Math.Round(Math.Sqrt(count));
        if (sideLong * sideLong != count)
            throw new ConversionRefusedException("error.unsupported_source", $"SRTM .hgt file size ({length} bytes) is not a square grid");
        if (sideLong < MinSide || sideLong > MaxSide)
            throw new ConversionRefusedException("error.unsupported_source", $"SRTM .hgt grid {sideLong}×{sideLong} is outside the supported range ({MinSide}..{MaxSide})");

        var side = (int)sideLong;
        var samples = new short[side * side];
        var row = new byte[side * 2];
        var min = short.MaxValue;
        var max = short.MinValue;
        var hasValid = false;

        try
        {
            for (var y = 0; y < side; y++)
            {
                if ((y & 63) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                stream.ReadExactly(row);
                var offset = y * side;
                for (var x = 0; x < side; x++)
                {
                    var elevation = BinaryPrimitives.ReadInt16BigEndian(row.AsSpan(x * 2, 2));
                    samples[offset + x] = elevation;
                    if (elevation != Void)
                    {
                        hasValid = true;
                        if (elevation < min)
                            min = elevation;
                        if (elevation > max)
                            max = elevation;
                    }
                }
            }
        }
        catch (EndOfStreamException)
        {
            throw new ConversionRefusedException("error.unsupported_source", "SRTM .hgt file ended before the full grid was read");
        }

        return new HgtTile(side, samples, hasValid ? min : (short)0, hasValid ? max : (short)0, hasValid);
    }

    /// <summary>
    /// Converts an SRTM <c>.hgt</c> tile to a single-channel signed 16-bit TIFF with <c>GDAL_NODATA = "-32768"</c>,
    /// adding WGS 84 GeoTIFF placement tags when the file name encodes a tile corner and the grid is not rotated or
    /// mirrored. Writes to a temporary file beside the output and moves it into place so a failure never harms an
    /// existing file.
    /// </summary>
    public static IReadOnlyList<string> WriteTiff(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var tile = Read(step.InputPath, cancellationToken);
        progress?.Report(0.4);

        var (samples, width, height) = Turn(tile.Samples, tile.Side, tile.Side, step.Preset.Image.Rotation, step.Preset.Image.Flip);
        cancellationToken.ThrowIfCancellationRequested();

        var extraTags = new List<TiffTag>
        {
            TiffTag.Ascii(GdalNoDataTag, "-32768"),
        };

        // GeoTIFF tiepoint + pixel scale describe a north-up grid; omit them if the user rotated or flipped the tile.
        if (!step.Preset.Image.TurnsOrMirrors && TryParseCorner(step.OriginalSourcePath ?? step.InputPath) is { } corner)
        {
            // Neighbouring SRTM tiles share their edge row/column, so n samples span 1° with spacing 1 / (n - 1)°
            // and sample centres on whole-degree lines (PixelIsPoint). Raster (0, 0) is the north-west corner.
            var spacing = 1.0 / (tile.Side - 1);
            extraTags.Add(TiffTag.Doubles(ModelPixelScaleTag, spacing, spacing, 0.0));
            extraTags.Add(TiffTag.Doubles(ModelTiepointTag, 0.0, 0.0, 0.0, corner.Longitude, corner.Latitude + 1, 0.0));
            extraTags.Add(TiffTag.UInt16s(GeoKeyDirectoryTag,
                1, 1, 0, 3,       // GeoTIFF directory v1.1.0, 3 keys
                1024, 0, 1, 2,    // GTModelTypeGeoKey = ModelTypeGeographic (2)
                1025, 0, 1, 2,    // GTRasterTypeGeoKey = RasterPixelIsPoint (2)
                2048, 0, 1, 4326  // GeographicTypeGeoKey = GCS_WGS_84 (4326)
            ));
        }

        var output = step.Output.Allocate("tiff");
        if (output is null)
            return [];

        var directory = Path.GetDirectoryName(Path.GetFullPath(output))!;
        var temp = Path.Combine(directory, $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.tmp");
        try
        {
            TiffWriter.Write(temp, samples, width, height, step.Preset.Image.TiffCompression,
                step.Preset.Image.Dpi ?? 72, extraTags, cancellationToken);
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

    /// <summary>
    /// Renders an SRTM <c>.hgt</c> tile as an 8-bit grayscale picture: valid elevations stretched linearly from min..max
    /// to 0..255 (mid-grey when flat), void cells (-32768) black.
    /// </summary>
    public static MagickImage ToMagick(string path, CancellationToken cancellationToken = default)
    {
        var tile = Read(path, cancellationToken);
        var pixels = tile.Samples.Length;
        var rgb = new byte[pixels * 3];
        var range = tile.Max - tile.Min;
        for (var i = 0; i < pixels; i++)
        {
            if ((i & 65535) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var elevation = tile.Samples[i];
            byte shade;
            if (elevation == Void || !tile.HasValid)
                shade = 0;
            else if (range > 0)
                shade = (byte)Math.Clamp(Math.Round((elevation - tile.Min) * 255.0 / range), 0, 255);
            else
                shade = 128;

            var p = i * 3;
            rgb[p] = shade;
            rgb[p + 1] = shade;
            rgb[p + 2] = shade;
        }

        var image = new MagickImage();
        image.ReadPixels(rgb, new PixelReadSettings((uint)tile.Side, (uint)tile.Side, StorageType.Char, PixelMapping.RGB));
        image.ColorType = ColorType.Grayscale;
        return image;
    }

    /// <summary>Turns, then mirrors, one signed 16-bit grid (matching <see cref="FitsRendering.Turn"/>).</summary>
    private static (short[] Samples, int Width, int Height) Turn(short[] samples, int width, int height,
        ImageRotation rotation, ImageFlip flip)
    {
        if (rotation == ImageRotation.None && flip == ImageFlip.None)
            return (samples, width, height);
        var (w, h) = rotation is ImageRotation.Right90 or ImageRotation.Left90 ? (height, width) : (width, height);
        var result = new short[samples.Length];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var fx = flip == ImageFlip.Horizontal ? w - 1 - x : x;
                var fy = flip == ImageFlip.Vertical ? h - 1 - y : y;
                var (sx, sy) = rotation switch
                {
                    ImageRotation.Right90 => (fy, height - 1 - fx),
                    ImageRotation.Left90 => (width - 1 - fy, fx),
                    ImageRotation.Rotate180 => (width - 1 - fx, height - 1 - fy),
                    _ => (fx, fy),
                };
                result[y * w + x] = samples[sy * width + sx];
            }
        }
        return (result, w, h);
    }
}
