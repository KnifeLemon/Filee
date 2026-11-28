// GeoTIFF rasters with 16/32-bit integer or 32/64-bit floating-point bands (#155): ImageMagick's Q8 build scales
// 16-bit bands over 0..65535 (making elevations like 0..3000 m nearly black) and clips floating-point values above 1.0
// to white. This reader handles the first IFD of classic little- or big-endian GeoTIFFs (strips or tiles, chunky or
// planar, uncompressed, LZW, Deflate or PackBits, with horizontal or floating-point predictors) when at least one
// GeoTIFF tag (33550, 33922, 34264 or 34735) is present and the samples are not plain 8-bit unsigned.
// - 1 or 2 bands: band 1 stretched linearly from min..max of valid values to 0..255 grayscale (mid-grey 128 when flat).
// - 3 or more bands: bands 1, 2 and 3 stretched linearly per band as R, G, B.
// - GDAL_NODATA (tag 42113) and NaN / ±Infinity values are excluded from the stretch and drawn black.
// - Ordinary TIFFs, 8-bit unsigned GeoTIFFs, BigTIFFs and unsupported compressions/layouts return null so ImageMagick
//   reads them unchanged.

using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Filee.Core.Conversion;
using ImageMagick;

namespace Filee.Engines.Magick;

/// <summary>Reads 16/32-bit integer and 32/64-bit float GeoTIFF rasters into stretched 8-bit pictures.</summary>
internal static class GeoTiff
{
    /// <summary>Largest accepted decoded pixel count (width × height), keeping float band memory bounded.</summary>
    internal const long MaxPixels = 64L * 1024 * 1024;

    /// <summary>Largest accepted total sample count (width × height × samples), matching Hgt/Vtf memory budgets.</summary>
    internal const long MaxSamples = 128L * 1024 * 1024;

    /// <summary>Largest accepted uncompressed byte size of a single strip or tile (256 MB).</summary>
    private const long MaxChunkBytes = 256L * 1024 * 1024;

    /// <summary>Largest accepted GDAL_NODATA ASCII tag length in bytes.</summary>
    private const long MaxNoDataTagBytes = 1024;

    private const ushort ImageWidthTag = 256;
    private const ushort ImageLengthTag = 257;
    private const ushort BitsPerSampleTag = 258;
    private const ushort CompressionTag = 259;
    private const ushort PhotometricTag = 262;
    private const ushort StripOffsetsTag = 273;
    private const ushort SamplesPerPixelTag = 277;
    private const ushort RowsPerStripTag = 278;
    private const ushort StripByteCountsTag = 279;
    private const ushort PlanarConfigurationTag = 284;
    private const ushort PredictorTag = 317;
    private const ushort TileWidthTag = 322;
    private const ushort TileLengthTag = 323;
    private const ushort TileOffsetsTag = 324;
    private const ushort TileByteCountsTag = 325;
    private const ushort SampleFormatTag = 339;

    private const ushort ModelPixelScaleTag = 33550;
    private const ushort ModelTiepointTag = 33922;
    private const ushort ModelTransformationTag = 34264;
    private const ushort GeoKeyDirectoryTag = 34735;
    private const ushort GdalNoDataTag = 42113;

    private const ushort CompressionNone = 1;
    private const ushort CompressionLzw = 5;
    private const ushort CompressionDeflate = 8;
    private const ushort CompressionPackBits = 32773;
    private const ushort CompressionLegacyDeflate = 32946;

    private const ushort PhotometricPalette = 3;

    private const ushort PlanarChunky = 1;
    private const ushort PlanarSeparate = 2;

    private const ushort PredictorNone = 1;
    private const ushort PredictorHorizontal = 2;
    private const ushort PredictorFloatingPoint = 3;

    private const ushort SampleFormatUInt = 1;
    private const ushort SampleFormatInt = 2;
    private const ushort SampleFormatFloat = 3;

    private readonly record struct IfdEntry(ushort Type, uint Count, int EntryOffset);

    /// <summary>
    /// Reads the first IFD of a high-depth GeoTIFF and renders it as a linearly stretched 8-bit image, or returns
    /// <c>null</c> when the file should stay on the standard ImageMagick path.
    /// </summary>
    /// <exception cref="ConversionRefusedException">
    /// The file is a high-depth GeoTIFF that this reader handles, but its dimensions, offsets, or compressed data are
    /// corrupt or exceed the memory budget.
    /// </exception>
    public static MagickImage? TryRead(string path, CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var fileLength = stream.Length;
        if (fileLength < 8)
            return null;

        Span<byte> header = stackalloc byte[8];
        stream.ReadExactly(header);

        bool bigEndian;
        if (header[0] == (byte)'I' && header[1] == (byte)'I')
            bigEndian = false;
        else if (header[0] == (byte)'M' && header[1] == (byte)'M')
            bigEndian = true;
        else
            return null;

        // Classic TIFF uses version 42; BigTIFF (43 / 0x2B) and anything else stay on ImageMagick.
        if (ReadUInt16(header.Slice(2, 2), bigEndian) != 42)
            return null;

        long ifdOffset = ReadUInt32(header.Slice(4, 4), bigEndian);
        if (ifdOffset < 8 || ifdOffset > fileLength - 2)
            return null;

        stream.Position = ifdOffset;
        Span<byte> countBytes = stackalloc byte[2];
        stream.ReadExactly(countBytes);
        int entryCount = ReadUInt16(countBytes, bigEndian);
        var ifdByteLength = (long)entryCount * 12;
        if (entryCount == 0 || ifdByteLength > fileLength - (ifdOffset + 2))
            return null;

        var ifdBytes = new byte[ifdByteLength];
        stream.ReadExactly(ifdBytes);

        var entries = new Dictionary<ushort, IfdEntry>(entryCount);
        var hasGeoTag = false;
        for (var i = 0; i < entryCount; i++)
        {
            var offset = i * 12;
            var tag = ReadUInt16(ifdBytes.AsSpan(offset, 2), bigEndian);
            if (tag is ModelPixelScaleTag or ModelTiepointTag or ModelTransformationTag or GeoKeyDirectoryTag)
                hasGeoTag = true;
            var type = ReadUInt16(ifdBytes.AsSpan(offset + 2, 2), bigEndian);
            var count = ReadUInt32(ifdBytes.AsSpan(offset + 4, 4), bigEndian);
            entries.TryAdd(tag, new IfdEntry(type, count, offset));
        }

        if (!hasGeoTag)
            return null;

        var compression = ReadOptionalUInt16(stream, fileLength, ifdBytes, entries, CompressionTag, CompressionNone, bigEndian);
        if (compression is not (CompressionNone or CompressionLzw or CompressionDeflate or CompressionLegacyDeflate or CompressionPackBits))
            return null;

        var photometric = ReadOptionalUInt16(stream, fileLength, ifdBytes, entries, PhotometricTag, 1, bigEndian);
        if (photometric == PhotometricPalette)
            return null;

        var planarConfig = ReadOptionalUInt16(stream, fileLength, ifdBytes, entries, PlanarConfigurationTag, PlanarChunky, bigEndian);
        if (planarConfig is not (PlanarChunky or PlanarSeparate))
            return null;

        var predictor = ReadOptionalUInt16(stream, fileLength, ifdBytes, entries, PredictorTag, PredictorNone, bigEndian);
        if (predictor is not (PredictorNone or PredictorHorizontal or PredictorFloatingPoint))
            return null;

        var samplesPerPixel = (int)ReadOptionalUInt16(stream, fileLength, ifdBytes, entries, SamplesPerPixelTag, 1, bigEndian);
        if (samplesPerPixel < 1)
            throw Refuse("GeoTIFF declares zero samples per pixel");

        if (!entries.TryGetValue(BitsPerSampleTag, out var bpsEntry))
            return null;
        var bitsPerSampleArray = ReadUInt16Array(stream, fileLength, ifdBytes, bpsEntry, bigEndian, maxCount: samplesPerPixel > 1 ? Math.Max(samplesPerPixel, 64) : 64);
        if (bitsPerSampleArray.Length == 0 || (bitsPerSampleArray.Length != 1 && bitsPerSampleArray.Length < samplesPerPixel))
            throw Refuse("GeoTIFF BitsPerSample tag does not match SamplesPerPixel");

        var bitsPerSample = bitsPerSampleArray[0];
        for (var i = 1; i < Math.Min(bitsPerSampleArray.Length, samplesPerPixel); i++)
        {
            if (bitsPerSampleArray[i] != bitsPerSample)
                return null;
        }

        ushort sampleFormat = SampleFormatUInt;
        if (entries.TryGetValue(SampleFormatTag, out var sfEntry))
        {
            var sampleFormatArray = ReadUInt16Array(stream, fileLength, ifdBytes, sfEntry, bigEndian, maxCount: Math.Max(samplesPerPixel, 64));
            if (sampleFormatArray.Length == 0 || (sampleFormatArray.Length != 1 && sampleFormatArray.Length < samplesPerPixel))
                throw Refuse("GeoTIFF SampleFormat tag does not match SamplesPerPixel");
            sampleFormat = sampleFormatArray[0];
            for (var i = 1; i < Math.Min(sampleFormatArray.Length, samplesPerPixel); i++)
            {
                if (sampleFormatArray[i] != sampleFormat)
                    return null;
            }
        }

        // Ordinary 8-bit unsigned GeoTIFFs (e.g. RGB orthophotos) stay on the ImageMagick path.
        if (sampleFormat == SampleFormatUInt && bitsPerSample == 8)
            return null;

        if (!IsSupportedSampleFormat(sampleFormat, bitsPerSample))
            return null;

        if (predictor == PredictorFloatingPoint && sampleFormat != SampleFormatFloat)
            throw Refuse("GeoTIFF uses the floating-point predictor on non-floating-point samples");

        var widthLong = ReadRequiredUInt32(stream, fileLength, ifdBytes, entries, ImageWidthTag, bigEndian, "ImageWidth");
        var heightLong = ReadRequiredUInt32(stream, fileLength, ifdBytes, entries, ImageLengthTag, bigEndian, "ImageLength");
        if (widthLong <= 0 || heightLong <= 0)
            throw Refuse($"GeoTIFF has invalid dimensions ({widthLong}×{heightLong})");

        if (widthLong > MaxPixels || heightLong > MaxPixels / widthLong)
            throw Refuse($"GeoTIFF dimensions ({widthLong}×{heightLong}) exceed the supported limit ({MaxPixels:N0} pixels)");

        var pixelCount = widthLong * heightLong;
        if (samplesPerPixel > MaxSamples / pixelCount)
            throw Refuse($"GeoTIFF sample count ({widthLong}×{heightLong}×{samplesPerPixel}) exceeds the supported limit ({MaxSamples:N0} samples)");

        var width = (int)widthLong;
        var height = (int)heightLong;
        var bytesPerSample = bitsPerSample / 8;

        var hasNoData = TryReadNoData(stream, fileLength, ifdBytes, entries, sampleFormat, bitsPerSample, bigEndian, out var noDataValue);

        var isTiled = entries.ContainsKey(TileOffsetsTag) || entries.ContainsKey(TileWidthTag) || entries.ContainsKey(TileLengthTag);
        var isStriped = entries.ContainsKey(StripOffsetsTag);
        if (!isTiled && !isStriped)
            throw Refuse("GeoTIFF has neither strip nor tile data offsets");

        var outputBands = samplesPerPixel >= 3 ? 3 : 1;
        var bands = new float[outputBands][];
        for (var b = 0; b < outputBands; b++)
            bands[b] = new float[pixelCount];

        var mins = new double[outputBands];
        var maxs = new double[outputBands];
        var hasValid = new bool[outputBands];
        Array.Fill(mins, double.PositiveInfinity);
        Array.Fill(maxs, double.NegativeInfinity);

        if (isTiled)
        {
            ReadTiled(stream, fileLength, ifdBytes, entries, width, height, samplesPerPixel, outputBands,
                planarConfig, compression, predictor, sampleFormat, bitsPerSample, bytesPerSample, bigEndian,
                hasNoData, noDataValue, bands, mins, maxs, hasValid, cancellationToken);
        }
        else
        {
            ReadStriped(stream, fileLength, ifdBytes, entries, width, height, samplesPerPixel, outputBands,
                planarConfig, compression, predictor, sampleFormat, bitsPerSample, bytesPerSample, bigEndian,
                hasNoData, noDataValue, bands, mins, maxs, hasValid, cancellationToken);
        }

        return Render(width, height, outputBands, bands, mins, maxs, hasValid, cancellationToken);
    }

    private static void ReadStriped(
        FileStream stream, long fileLength, byte[] ifdBytes, Dictionary<ushort, IfdEntry> entries,
        int width, int height, int samplesPerPixel, int outputBands,
        ushort planarConfig, ushort compression, ushort predictor, ushort sampleFormat, int bitsPerSample, int bytesPerSample,
        bool bigEndian, bool hasNoData, double noDataValue,
        float[][] bands, double[] mins, double[] maxs, bool[] hasValid, CancellationToken cancellationToken)
    {
        var rowsPerStripLong = entries.ContainsKey(RowsPerStripTag)
            ? ReadRequiredUInt32(stream, fileLength, ifdBytes, entries, RowsPerStripTag, bigEndian, "RowsPerStrip")
            : (long)height;
        if (rowsPerStripLong <= 0)
            throw Refuse("GeoTIFF declares zero rows per strip");
        var rowsPerStrip = (int)Math.Min(rowsPerStripLong, height);

        var stripsPerPlane = ((long)height + rowsPerStrip - 1) / rowsPerStrip;
        var expectedStrips = planarConfig == PlanarSeparate ? stripsPerPlane * samplesPerPixel : stripsPerPlane;

        if (!entries.TryGetValue(StripOffsetsTag, out var offsetsEntry) || !entries.TryGetValue(StripByteCountsTag, out var countsEntry))
            throw Refuse("GeoTIFF is missing StripOffsets or StripByteCounts");

        var offsets = ReadUInt32Array(stream, fileLength, ifdBytes, offsetsEntry, bigEndian, expectedStrips);
        var byteCounts = ReadUInt32Array(stream, fileLength, ifdBytes, countsEntry, bigEndian, expectedStrips);
        if (offsets.Length < expectedStrips || byteCounts.Length < expectedStrips)
            throw Refuse($"GeoTIFF has fewer strips ({Math.Min(offsets.Length, byteCounts.Length)}) than expected ({expectedStrips})");

        ValidateChunkRanges(offsets, byteCounts, expectedStrips, fileLength, "strip");

        var chunkSamples = planarConfig == PlanarSeparate ? 1 : samplesPerPixel;
        var maxStripBytes = ComputeChunkBytes(width, rowsPerStrip, chunkSamples, bytesPerSample);
        var decodedBuffer = new byte[maxStripBytes];
        byte[]? compressedBuffer = null;
        byte[]? predictorScratch = predictor == PredictorFloatingPoint ? new byte[(long)width * chunkSamples * bytesPerSample] : null;

        if (planarConfig == PlanarChunky)
        {
            for (var s = 0; s < stripsPerPlane; s++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var firstRow = s * rowsPerStrip;
                var stripRows = Math.Min(rowsPerStrip, height - firstRow);
                var expectedBytes = (int)((long)width * stripRows * chunkSamples * bytesPerSample);
                var chunk = decodedBuffer.AsSpan(0, expectedBytes);

                DecodeChunk(stream, offsets[s], byteCounts[s], compression, chunk, ref compressedBuffer, cancellationToken);
                UndoPredictor(chunk, stripRows, width, chunkSamples, predictor, bytesPerSample, bigEndian, predictorScratch);

                var pixelStrideBytes = samplesPerPixel * bytesPerSample;
                for (var r = 0; r < stripRows; r++)
                {
                    if ((r & 63) == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                    var rowSpan = chunk.Slice(r * width * pixelStrideBytes, width * pixelStrideBytes);
                    var rowDstBase = (firstRow + r) * width;
                    for (var x = 0; x < width; x++)
                    {
                        var pixelSpan = rowSpan.Slice(x * pixelStrideBytes, pixelStrideBytes);
                        var dstIndex = rowDstBase + x;
                        for (var b = 0; b < outputBands; b++)
                        {
                            var value = ReadSample(pixelSpan.Slice(b * bytesPerSample, bytesPerSample), sampleFormat, bitsPerSample, bigEndian);
                            StoreSample(bands[b], dstIndex, value, hasNoData, noDataValue, ref mins[b], ref maxs[b], ref hasValid[b]);
                        }
                    }
                }
            }
        }
        else
        {
            for (var b = 0; b < outputBands; b++)
            {
                var planeStripBase = b * (int)stripsPerPlane;
                for (var s = 0; s < stripsPerPlane; s++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var stripIndex = planeStripBase + s;
                    var firstRow = s * rowsPerStrip;
                    var stripRows = Math.Min(rowsPerStrip, height - firstRow);
                    var expectedBytes = (int)((long)width * stripRows * bytesPerSample);
                    var chunk = decodedBuffer.AsSpan(0, expectedBytes);

                    DecodeChunk(stream, offsets[stripIndex], byteCounts[stripIndex], compression, chunk, ref compressedBuffer, cancellationToken);
                    UndoPredictor(chunk, stripRows, width, 1, predictor, bytesPerSample, bigEndian, predictorScratch);

                    var rowBytes = width * bytesPerSample;
                    for (var r = 0; r < stripRows; r++)
                    {
                        if ((r & 63) == 0)
                            cancellationToken.ThrowIfCancellationRequested();
                        var rowSpan = chunk.Slice(r * rowBytes, rowBytes);
                        var rowDstBase = (firstRow + r) * width;
                        for (var x = 0; x < width; x++)
                        {
                            var value = ReadSample(rowSpan.Slice(x * bytesPerSample, bytesPerSample), sampleFormat, bitsPerSample, bigEndian);
                            StoreSample(bands[b], rowDstBase + x, value, hasNoData, noDataValue, ref mins[b], ref maxs[b], ref hasValid[b]);
                        }
                    }
                }
            }
        }
    }

    private static void ReadTiled(
        FileStream stream, long fileLength, byte[] ifdBytes, Dictionary<ushort, IfdEntry> entries,
        int width, int height, int samplesPerPixel, int outputBands,
        ushort planarConfig, ushort compression, ushort predictor, ushort sampleFormat, int bitsPerSample, int bytesPerSample,
        bool bigEndian, bool hasNoData, double noDataValue,
        float[][] bands, double[] mins, double[] maxs, bool[] hasValid, CancellationToken cancellationToken)
    {
        var tileWidthLong = ReadRequiredUInt32(stream, fileLength, ifdBytes, entries, TileWidthTag, bigEndian, "TileWidth");
        var tileLengthLong = ReadRequiredUInt32(stream, fileLength, ifdBytes, entries, TileLengthTag, bigEndian, "TileLength");
        if (tileWidthLong <= 0 || tileLengthLong <= 0)
            throw Refuse($"GeoTIFF has invalid tile dimensions ({tileWidthLong}×{tileLengthLong})");

        var chunkSamples = planarConfig == PlanarSeparate ? 1 : samplesPerPixel;
        var tileBytes = ComputeChunkBytes(tileWidthLong, tileLengthLong, chunkSamples, bytesPerSample);
        var tileWidth = (int)tileWidthLong;
        var tileLength = (int)tileLengthLong;

        var tilesAcross = (width + (long)tileWidth - 1) / tileWidth;
        var tilesDown = (height + (long)tileLength - 1) / tileLength;
        if (tilesAcross > MaxSamples / tilesDown)
            throw Refuse("GeoTIFF tile grid exceeds the supported limit");
        var tilesPerPlane = tilesAcross * tilesDown;
        if (planarConfig == PlanarSeparate && samplesPerPixel > MaxSamples / tilesPerPlane)
            throw Refuse("GeoTIFF tile count exceeds the supported limit");
        var expectedTiles = planarConfig == PlanarSeparate ? tilesPerPlane * samplesPerPixel : tilesPerPlane;

        if (!entries.TryGetValue(TileOffsetsTag, out var offsetsEntry) || !entries.TryGetValue(TileByteCountsTag, out var countsEntry))
            throw Refuse("GeoTIFF is missing TileOffsets or TileByteCounts");

        var offsets = ReadUInt32Array(stream, fileLength, ifdBytes, offsetsEntry, bigEndian, expectedTiles);
        var byteCounts = ReadUInt32Array(stream, fileLength, ifdBytes, countsEntry, bigEndian, expectedTiles);
        if (offsets.Length < expectedTiles || byteCounts.Length < expectedTiles)
            throw Refuse($"GeoTIFF has fewer tiles ({Math.Min(offsets.Length, byteCounts.Length)}) than expected ({expectedTiles})");

        ValidateChunkRanges(offsets, byteCounts, expectedTiles, fileLength, "tile");

        var decodedBuffer = new byte[tileBytes];
        byte[]? compressedBuffer = null;
        byte[]? predictorScratch = predictor == PredictorFloatingPoint ? new byte[(long)tileWidth * chunkSamples * bytesPerSample] : null;

        if (planarConfig == PlanarChunky)
        {
            var pixelStrideBytes = samplesPerPixel * bytesPerSample;
            var tileRowBytes = tileWidth * pixelStrideBytes;
            for (var ty = 0; ty < tilesDown; ty++)
            {
                var dstY0 = ty * tileLength;
                var copyRows = Math.Min(tileLength, height - dstY0);
                for (var tx = 0; tx < tilesAcross; tx++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var tileIndex = (int)(ty * tilesAcross + tx);
                    var chunk = decodedBuffer.AsSpan();

                    DecodeChunk(stream, offsets[tileIndex], byteCounts[tileIndex], compression, chunk, ref compressedBuffer, cancellationToken);
                    UndoPredictor(chunk, copyRows, tileWidth, chunkSamples, predictor, bytesPerSample, bigEndian, predictorScratch);

                    var dstX0 = tx * tileWidth;
                    var copyCols = Math.Min(tileWidth, width - dstX0);
                    for (var r = 0; r < copyRows; r++)
                    {
                        var rowSpan = chunk.Slice(r * tileRowBytes, tileRowBytes);
                        var rowDstBase = (dstY0 + r) * width + dstX0;
                        for (var x = 0; x < copyCols; x++)
                        {
                            var pixelSpan = rowSpan.Slice(x * pixelStrideBytes, pixelStrideBytes);
                            var dstIndex = rowDstBase + x;
                            for (var b = 0; b < outputBands; b++)
                            {
                                var value = ReadSample(pixelSpan.Slice(b * bytesPerSample, bytesPerSample), sampleFormat, bitsPerSample, bigEndian);
                                StoreSample(bands[b], dstIndex, value, hasNoData, noDataValue, ref mins[b], ref maxs[b], ref hasValid[b]);
                            }
                        }
                    }
                }
            }
        }
        else
        {
            var tileRowBytes = tileWidth * bytesPerSample;
            for (var b = 0; b < outputBands; b++)
            {
                var planeTileBase = b * tilesPerPlane;
                for (var ty = 0; ty < tilesDown; ty++)
                {
                    var dstY0 = ty * tileLength;
                    var copyRows = Math.Min(tileLength, height - dstY0);
                    for (var tx = 0; tx < tilesAcross; tx++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var tileIndex = (int)(planeTileBase + ty * tilesAcross + tx);
                        var chunk = decodedBuffer.AsSpan();

                        DecodeChunk(stream, offsets[tileIndex], byteCounts[tileIndex], compression, chunk, ref compressedBuffer, cancellationToken);
                        UndoPredictor(chunk, copyRows, tileWidth, 1, predictor, bytesPerSample, bigEndian, predictorScratch);

                        var dstX0 = tx * tileWidth;
                        var copyCols = Math.Min(tileWidth, width - dstX0);
                        for (var r = 0; r < copyRows; r++)
                        {
                            var rowSpan = chunk.Slice(r * tileRowBytes, tileRowBytes);
                            var rowDstBase = (dstY0 + r) * width + dstX0;
                            for (var x = 0; x < copyCols; x++)
                            {
                                var value = ReadSample(rowSpan.Slice(x * bytesPerSample, bytesPerSample), sampleFormat, bitsPerSample, bigEndian);
                                StoreSample(bands[b], rowDstBase + x, value, hasNoData, noDataValue, ref mins[b], ref maxs[b], ref hasValid[b]);
                            }
                        }
                    }
                }
            }
        }
    }

    private static int ComputeChunkBytes(long chunkWidth, long chunkHeight, int chunkSamples, int bytesPerSample)
    {
        if (chunkWidth <= 0 || chunkHeight <= 0 || chunkWidth > MaxChunkBytes / chunkHeight)
            throw Refuse("GeoTIFF strip or tile size exceeds the supported memory limit");
        var pixels = chunkWidth * chunkHeight;
        long bytesPerPixel = (long)chunkSamples * bytesPerSample;
        if (bytesPerPixel <= 0 || pixels > MaxChunkBytes / bytesPerPixel)
            throw Refuse("GeoTIFF strip or tile byte size exceeds the supported memory limit");
        return (int)(pixels * bytesPerPixel);
    }

    private static void ValidateChunkRanges(uint[] offsets, uint[] byteCounts, long count, long fileLength, string kind)
    {
        for (var i = 0; i < count; i++)
        {
            long offset = offsets[i];
            long length = byteCounts[i];
            if (length > fileLength || offset > fileLength - length)
                throw Refuse($"GeoTIFF {kind} {i} (offset {offset}, length {length}) extends past the end of the file ({fileLength} bytes)");
        }
    }

    private static void DecodeChunk(
        FileStream stream, uint offset, uint byteCount, ushort compression,
        Span<byte> destination, ref byte[]? compressedBuffer, CancellationToken cancellationToken)
    {
        // Sparse GeoTIFF tile/strip (GDAL convention: offset 0, byte count 0) decodes as all zeroes.
        if (offset == 0 && byteCount == 0)
        {
            destination.Clear();
            return;
        }
        if (byteCount == 0)
            throw Refuse("GeoTIFF strip or tile has zero byte length");

        stream.Position = offset;
        if (compression == CompressionNone)
        {
            if (byteCount < destination.Length)
                throw Refuse($"GeoTIFF uncompressed strip or tile is truncated ({byteCount} of {destination.Length} bytes)");
            stream.ReadExactly(destination);
            return;
        }

        if (byteCount > MaxChunkBytes)
            throw Refuse($"GeoTIFF compressed strip or tile ({byteCount} bytes) exceeds the supported memory limit");

        if (compressedBuffer is null || compressedBuffer.Length < byteCount)
            compressedBuffer = new byte[byteCount];
        var compressed = compressedBuffer.AsSpan(0, (int)byteCount);
        stream.ReadExactly(compressed);

        switch (compression)
        {
            case CompressionLzw:
                DecodeLzw(compressed, destination, cancellationToken);
                break;
            case CompressionDeflate:
            case CompressionLegacyDeflate:
                DecodeDeflate(compressedBuffer, (int)byteCount, destination, cancellationToken);
                break;
            case CompressionPackBits:
                DecodePackBits(compressed, destination, cancellationToken);
                break;
            default:
                throw Refuse($"Unsupported GeoTIFF compression ({compression})");
        }
    }

    /// <summary>
    /// Decodes a TIFF 6.0 LZW stream (MSB-first, 9..12-bit codes, clear = 256, EOI = 257, early change).
    /// </summary>
    private static void DecodeLzw(ReadOnlySpan<byte> input, Span<byte> output, CancellationToken cancellationToken)
    {
        const int clearCode = 256;
        const int eoiCode = 257;
        const int firstCode = 258;
        const int maxCodes = 4096;

        var prefix = new short[maxCodes];
        var suffix = new byte[maxCodes];
        var stack = new byte[maxCodes];

        var inPos = 0;
        ulong bitBuf = 0;
        var bitCount = 0;

        var codeWidth = 9;
        var nextCode = firstCode;
        var oldCode = -1;
        byte firstByte = 0;
        var outPos = 0;
        var iterations = 0;

        while (outPos < output.Length)
        {
            if ((++iterations & 16383) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            while (bitCount < codeWidth)
            {
                if (inPos >= input.Length)
                    goto Done;
                bitBuf = (bitBuf << 8) | input[inPos++];
                bitCount += 8;
            }
            bitCount -= codeWidth;
            var code = (int)((bitBuf >> bitCount) & ((1UL << codeWidth) - 1));

            if (code == eoiCode)
                break;
            if (code == clearCode)
            {
                codeWidth = 9;
                nextCode = firstCode;
                oldCode = -1;
                continue;
            }

            if (oldCode == -1)
            {
                if (code >= 256)
                    throw Refuse("GeoTIFF LZW stream started with an invalid code");
                output[outPos++] = (byte)code;
                oldCode = code;
                firstByte = (byte)code;
                continue;
            }

            var sp = 0;
            var cur = code;
            if (code == nextCode)
            {
                stack[sp++] = firstByte;
                cur = oldCode;
            }
            else if (code > nextCode)
            {
                throw Refuse("GeoTIFF LZW stream contains an out-of-range code");
            }

            while (cur >= 256)
            {
                if (sp >= maxCodes)
                    throw Refuse("GeoTIFF LZW table contains a cycle");
                stack[sp++] = suffix[cur];
                cur = prefix[cur];
            }
            firstByte = (byte)cur;
            stack[sp++] = firstByte;

            var toCopy = Math.Min(sp, output.Length - outPos);
            for (var i = 0; i < toCopy; i++)
                output[outPos++] = stack[sp - 1 - i];

            if (nextCode < maxCodes)
            {
                prefix[nextCode] = (short)oldCode;
                suffix[nextCode] = firstByte;
                nextCode++;
                if (nextCode == (1 << codeWidth) - 1 && codeWidth < 12)
                    codeWidth++;
            }
            oldCode = code;
        }

    Done:
        if (outPos < output.Length)
            throw Refuse($"GeoTIFF LZW data ended early ({outPos} of {output.Length} bytes decoded)");
    }

    private static void DecodeDeflate(byte[] compressedBuffer, int length, Span<byte> destination, CancellationToken cancellationToken)
    {
        try
        {
            using var memory = new MemoryStream(compressedBuffer, 0, length, writable: false);
            using var zlib = new ZLibStream(memory, CompressionMode.Decompress);
            var total = 0;
            while (total < destination.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = zlib.Read(destination[total..]);
                if (read == 0)
                    break;
                total += read;
            }
            if (total < destination.Length)
                throw Refuse($"GeoTIFF Deflate data ended early ({total} of {destination.Length} bytes decoded)");
        }
        catch (InvalidDataException)
        {
            throw Refuse("GeoTIFF contains corrupt Deflate data");
        }
    }

    private static void DecodePackBits(ReadOnlySpan<byte> input, Span<byte> output, CancellationToken cancellationToken)
    {
        var inPos = 0;
        var outPos = 0;
        var iterations = 0;
        while (outPos < output.Length && inPos < input.Length)
        {
            if ((++iterations & 16383) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var header = unchecked((sbyte)input[inPos++]);
            if (header >= 0)
            {
                var count = header + 1;
                if (inPos > input.Length - count || outPos > output.Length - count)
                    throw Refuse("GeoTIFF PackBits literal run extends past buffer bounds");
                input.Slice(inPos, count).CopyTo(output.Slice(outPos, count));
                inPos += count;
                outPos += count;
            }
            else if (header != -128)
            {
                var count = 1 - header;
                if (inPos >= input.Length || outPos > output.Length - count)
                    throw Refuse("GeoTIFF PackBits repeat run extends past buffer bounds");
                output.Slice(outPos, count).Fill(input[inPos++]);
                outPos += count;
            }
        }
        if (outPos < output.Length)
            throw Refuse($"GeoTIFF PackBits data ended early ({outPos} of {output.Length} bytes decoded)");
    }

    private static void UndoPredictor(
        Span<byte> chunk, int rows, int rowPixels, int rowSamplesPerPixel,
        ushort predictor, int bytesPerSample, bool bigEndian, byte[]? scratch)
    {
        if (predictor == PredictorNone)
            return;

        var rowBytes = rowPixels * rowSamplesPerPixel * bytesPerSample;
        if (predictor == PredictorHorizontal)
        {
            for (var r = 0; r < rows; r++)
            {
                var row = chunk.Slice(r * rowBytes, rowBytes);
                switch (bytesPerSample)
                {
                    case 1:
                        for (var i = rowSamplesPerPixel; i < rowBytes; i++)
                            row[i] = unchecked((byte)(row[i] + row[i - rowSamplesPerPixel]));
                        break;
                    case 2:
                        var stride2 = rowSamplesPerPixel * 2;
                        for (var i = stride2; i < rowBytes; i += 2)
                        {
                            var prev = ReadUInt16(row.Slice(i - stride2, 2), bigEndian);
                            var diff = ReadUInt16(row.Slice(i, 2), bigEndian);
                            WriteUInt16(row.Slice(i, 2), unchecked((ushort)(prev + diff)), bigEndian);
                        }
                        break;
                    case 4:
                        var stride4 = rowSamplesPerPixel * 4;
                        for (var i = stride4; i < rowBytes; i += 4)
                        {
                            var prev = ReadUInt32(row.Slice(i - stride4, 4), bigEndian);
                            var diff = ReadUInt32(row.Slice(i, 4), bigEndian);
                            WriteUInt32(row.Slice(i, 4), unchecked(prev + diff), bigEndian);
                        }
                        break;
                    case 8:
                        var stride8 = rowSamplesPerPixel * 8;
                        for (var i = stride8; i < rowBytes; i += 8)
                        {
                            var prev = ReadUInt64(row.Slice(i - stride8, 8), bigEndian);
                            var diff = ReadUInt64(row.Slice(i, 8), bigEndian);
                            WriteUInt64(row.Slice(i, 8), unchecked(prev + diff), bigEndian);
                        }
                        break;
                }
            }
            return;
        }

        // Floating-point predictor (Adobe TIFF Tech Note 3): horizontal byte differences across the byte-shuffled row,
        // where byte planes are always ordered from most-significant byte (plane 0) to least-significant byte.
        var sampleCount = rowPixels * rowSamplesPerPixel;
        var tmp = scratch.AsSpan(0, rowBytes);
        for (var r = 0; r < rows; r++)
        {
            var row = chunk.Slice(r * rowBytes, rowBytes);
            for (var i = rowSamplesPerPixel; i < rowBytes; i++)
                row[i] = unchecked((byte)(row[i] + row[i - rowSamplesPerPixel]));
            row.CopyTo(tmp);
            for (var s = 0; s < sampleCount; s++)
            {
                var dst = s * bytesPerSample;
                if (bigEndian)
                {
                    for (var b = 0; b < bytesPerSample; b++)
                        row[dst + b] = tmp[b * sampleCount + s];
                }
                else
                {
                    for (var b = 0; b < bytesPerSample; b++)
                        row[dst + b] = tmp[(bytesPerSample - 1 - b) * sampleCount + s];
                }
            }
        }
    }

    private static double ReadSample(ReadOnlySpan<byte> bytes, ushort sampleFormat, int bitsPerSample, bool bigEndian) =>
        (sampleFormat, bitsPerSample) switch
        {
            (SampleFormatUInt, 16) => ReadUInt16(bytes, bigEndian),
            (SampleFormatUInt, 32) => ReadUInt32(bytes, bigEndian),
            (SampleFormatInt, 8) => unchecked((sbyte)bytes[0]),
            (SampleFormatInt, 16) => bigEndian ? BinaryPrimitives.ReadInt16BigEndian(bytes) : BinaryPrimitives.ReadInt16LittleEndian(bytes),
            (SampleFormatInt, 32) => bigEndian ? BinaryPrimitives.ReadInt32BigEndian(bytes) : BinaryPrimitives.ReadInt32LittleEndian(bytes),
            (SampleFormatFloat, 32) => bigEndian ? BinaryPrimitives.ReadSingleBigEndian(bytes) : BinaryPrimitives.ReadSingleLittleEndian(bytes),
            (SampleFormatFloat, 64) => bigEndian ? BinaryPrimitives.ReadDoubleBigEndian(bytes) : BinaryPrimitives.ReadDoubleLittleEndian(bytes),
            _ => throw Refuse($"Unsupported GeoTIFF sample format ({sampleFormat}, {bitsPerSample}-bit)"),
        };

    private static void StoreSample(float[] band, int index, double value, bool hasNoData, double noDataValue,
        ref double min, ref double max, ref bool hasValid)
    {
        if (!double.IsFinite(value) || (hasNoData && value == noDataValue))
        {
            band[index] = float.NaN;
            return;
        }
        hasValid = true;
        if (value < min)
            min = value;
        if (value > max)
            max = value;
        band[index] = (float)value;
    }

    private static MagickImage Render(
        int width, int height, int outputBands, float[][] bands,
        double[] mins, double[] maxs, bool[] hasValid,
        CancellationToken cancellationToken)
    {
        var pixels = width * height;
        var rgb = new byte[pixels * 3];

        if (outputBands == 1)
        {
            var band = bands[0];
            var min = mins[0];
            var max = maxs[0];
            var valid = hasValid[0];
            var range = max - min;
            for (var i = 0; i < pixels; i++)
            {
                if ((i & 65535) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var shade = Stretch(band[i], min, max, range, valid);
                var p = i * 3;
                rgb[p] = shade;
                rgb[p + 1] = shade;
                rgb[p + 2] = shade;
            }
        }
        else
        {
            var range0 = maxs[0] - mins[0];
            var range1 = maxs[1] - mins[1];
            var range2 = maxs[2] - mins[2];
            for (var i = 0; i < pixels; i++)
            {
                if ((i & 65535) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var p = i * 3;
                rgb[p] = Stretch(bands[0][i], mins[0], maxs[0], range0, hasValid[0]);
                rgb[p + 1] = Stretch(bands[1][i], mins[1], maxs[1], range1, hasValid[1]);
                rgb[p + 2] = Stretch(bands[2][i], mins[2], maxs[2], range2, hasValid[2]);
            }
        }

        var image = new MagickImage();
        try
        {
            image.ReadPixels(rgb, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.RGB));
            if (outputBands == 1)
                image.ColorType = ColorType.Grayscale;
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private static byte Stretch(float value, double min, double max, double range, bool hasValid)
    {
        if (!hasValid || !float.IsFinite(value))
            return 0;
        if (range > 0)
        {
            var v = (double)value;
            var scaled = double.IsInfinity(range)
                ? (v * 0.5 - min * 0.5) / (max * 0.5 - min * 0.5) * 255.0
                : (v - min) * 255.0 / range;
            return (byte)Math.Clamp(Math.Round(scaled), 0, 255);
        }
        return 128;
    }

    private static bool IsSupportedSampleFormat(ushort sampleFormat, ushort bitsPerSample) =>
        (sampleFormat, bitsPerSample) switch
        {
            (SampleFormatUInt, 16 or 32) => true,
            (SampleFormatInt, 8 or 16 or 32) => true,
            (SampleFormatFloat, 32 or 64) => true,
            _ => false,
        };

    private static bool TryReadNoData(
        FileStream stream, long fileLength, byte[] ifdBytes, Dictionary<ushort, IfdEntry> entries,
        ushort sampleFormat, int bitsPerSample, bool bigEndian, out double noDataValue)
    {
        noDataValue = 0;
        if (!entries.TryGetValue(GdalNoDataTag, out var entry) || entry.Type != 2 || entry.Count == 0)
            return false;
        if (entry.Count > MaxNoDataTagBytes)
            throw Refuse("GeoTIFF GDAL_NODATA tag is unexpectedly large");

        var raw = ReadTagBytes(stream, fileLength, ifdBytes, entry, 1, bigEndian);
        var text = Encoding.ASCII.GetString(raw).TrimEnd('\0').Trim();
        if (text.Length == 0 || text.Equals("nan", StringComparison.OrdinalIgnoreCase))
            return false;

        if (sampleFormat == SampleFormatFloat && bitsPerSample == 32)
        {
            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && float.IsFinite(f))
            {
                noDataValue = f;
                return true;
            }
            return false;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d))
        {
            noDataValue = d;
            return true;
        }
        return false;
    }

    private static ushort ReadOptionalUInt16(
        FileStream stream, long fileLength, byte[] ifdBytes, Dictionary<ushort, IfdEntry> entries,
        ushort tag, ushort defaultValue, bool bigEndian)
    {
        if (!entries.TryGetValue(tag, out var entry) || entry.Count == 0)
            return defaultValue;
        var values = ReadUInt16Array(stream, fileLength, ifdBytes, entry, bigEndian, maxCount: 1);
        return values.Length > 0 ? values[0] : defaultValue;
    }

    private static ushort[] ReadUInt16Array(
        FileStream stream, long fileLength, byte[] ifdBytes, IfdEntry entry, bool bigEndian, int maxCount)
    {
        var count = (int)Math.Min(entry.Count, (uint)maxCount);
        if (count == 0)
            return [];
        var unitSize = entry.Type switch { 3 => 2, 4 => 4, _ => 0 };
        if (unitSize == 0)
            throw Refuse($"GeoTIFF tag has unexpected type {entry.Type}");

        // Read the full tag byte span only if it fits in the file; for single-value lookups, cap safely.
        var raw = ReadTagBytes(stream, fileLength, ifdBytes, entry, unitSize, bigEndian, maxElements: maxCount);
        var result = new ushort[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = unitSize == 2
                ? ReadUInt16(raw.AsSpan(i * 2, 2), bigEndian)
                : (ushort)Math.Min(ushort.MaxValue, ReadUInt32(raw.AsSpan(i * 4, 4), bigEndian));
        }
        return result;
    }

    private static long ReadRequiredUInt32(
        FileStream stream, long fileLength, byte[] ifdBytes, Dictionary<ushort, IfdEntry> entries,
        ushort tag, bool bigEndian, string name)
    {
        if (!entries.TryGetValue(tag, out var entry) || entry.Count == 0)
            throw Refuse($"GeoTIFF is missing required tag {name} ({tag})");
        var values = ReadUInt32Array(stream, fileLength, ifdBytes, entry, bigEndian, maxCount: 1);
        return values[0];
    }

    private static uint[] ReadUInt32Array(
        FileStream stream, long fileLength, byte[] ifdBytes, IfdEntry entry, bool bigEndian, long maxCount)
    {
        if (entry.Count == 0)
            return [];
        var unitSize = entry.Type switch { 3 => 2, 4 => 4, _ => 0 };
        if (unitSize == 0)
            throw Refuse($"GeoTIFF tag has unexpected type {entry.Type}");

        var countLong = Math.Min(entry.Count, maxCount);
        var raw = ReadTagBytes(stream, fileLength, ifdBytes, entry, unitSize, bigEndian, maxElements: countLong);
        var count = (int)countLong;
        var result = new uint[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = unitSize == 2
                ? ReadUInt16(raw.AsSpan(i * 2, 2), bigEndian)
                : ReadUInt32(raw.AsSpan(i * 4, 4), bigEndian);
        }
        return result;
    }

    private static byte[] ReadTagBytes(
        FileStream stream, long fileLength, byte[] ifdBytes, IfdEntry entry, int unitSize, bool bigEndian, long? maxElements = null)
    {
        var fullByteCount = (long)entry.Count * unitSize;
        var readCount = maxElements is { } cap ? Math.Min(entry.Count, cap) : entry.Count;
        var readByteCount = readCount * unitSize;
        if (readByteCount > MaxChunkBytes)
            throw Refuse("GeoTIFF tag array exceeds the supported memory limit");

        if (fullByteCount <= 4)
            return ifdBytes.AsSpan(entry.EntryOffset + 8, (int)readByteCount).ToArray();

        long offset = ReadUInt32(ifdBytes.AsSpan(entry.EntryOffset + 8, 4), bigEndian);
        if (fullByteCount > fileLength || offset > fileLength - fullByteCount)
            throw Refuse($"GeoTIFF tag data (offset {offset}, length {fullByteCount}) extends past the end of the file");

        var buffer = new byte[(int)readByteCount];
        stream.Position = offset;
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(bytes) : BinaryPrimitives.ReadUInt16LittleEndian(bytes);

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(bytes) : BinaryPrimitives.ReadUInt32LittleEndian(bytes);

    private static ulong ReadUInt64(ReadOnlySpan<byte> bytes, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt64BigEndian(bytes) : BinaryPrimitives.ReadUInt64LittleEndian(bytes);

    private static void WriteUInt16(Span<byte> bytes, ushort value, bool bigEndian)
    {
        if (bigEndian)
            BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        else
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
    }

    private static void WriteUInt32(Span<byte> bytes, uint value, bool bigEndian)
    {
        if (bigEndian)
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        else
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
    }

    private static void WriteUInt64(Span<byte> bytes, ulong value, bool bigEndian)
    {
        if (bigEndian)
            BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        else
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
    }

    private static ConversionRefusedException Refuse(string reason) =>
        new("error.unsupported_source", reason);
}
