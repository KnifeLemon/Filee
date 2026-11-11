// The size the compression window shows (#1353): about how large the files dropped on a "Compress" preset come out
// at the setting being chosen. It is an estimate, and the window says so; each kind is guessed the most honest cheap way:
//
// - Video and audio: the bitrate the encoder is given (or, for a constant-quality video, the bitrate Filee already uses
//   to give a graphics chip the same quality, HardwareVideo.BitrateKbps) times the length ffprobe reads. Sound is close;
//   a video's real size depends on its picture (a still screen recording comes out far smaller, confetti larger).
// - Pictures: a tile from the middle of the picture, at full resolution, is really encoded at the quality and the bytes
//   scaled up to the whole picture. Encoding all of it on every slider step would be too slow for a phone photo, and a
//   scaled-down copy has more detail per pixel and would guess too high.
// - PDF: the built-in compressor really runs, into a stream that only counts bytes; a level is done once. Nothing
//   cheaper can tell how much of a PDF is pictures drawn above the level's resolution. Ghostscript, when the preset
//   also tries it, is not run: it can only make the result smaller.
//
// The preset's size limit and "only keep if smaller" cap the result, as they do when it converts. Only the first files
// of each kind are read; the others count at the same ratio.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Magick;
using Filee.Engines.Media;
using Filee.Engines.Pdf;
using ImageMagick;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.Engines;

/// <summary>Estimates what the files dropped on a preset that asks for its compression come to (<see cref="AskedCompression"/>).</summary>
public sealed class CompressionEstimator : IDisposable
{
    /// <summary>Pixels of the tile a picture is estimated from (about 0.4 MP: a few milliseconds per JPEG step).</summary>
    internal const int TileSide = 640;

    /// <summary>Files of each kind read for the estimate; the others count at the same ratio.</summary>
    internal static int SampledFiles(CompressionKind kind) => kind switch
    {
        CompressionKind.Image => 12,
        CompressionKind.Pdf => 4,
        _ => 40,
    };

    private readonly Preset _preset;
    private readonly List<Entry> _entries;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CompressionEstimator(Preset preset, List<Entry> entries, ILogger logger)
    {
        _preset = preset;
        _entries = entries;
        _logger = logger;
    }

    /// <summary>The files a level is chosen for (<see cref="AskedCompression.KindOf"/>), in the order dropped.</summary>
    public int FileCount => _entries.Count;

    /// <summary>Their size now, in bytes.</summary>
    public long SourceBytes => _entries.Sum(e => e.Bytes);

    /// <summary>
    /// Reads the files of <paramref name="files"/> that <paramref name="preset"/> asks a level for: their sizes, a tile of
    /// each picture, the length and streams of each video or audio file. On a worker thread.
    /// </summary>
    public static Task<CompressionEstimator> ReadAsync(IReadOnlyList<string> files, Preset preset, ILogger? logger = null,
        CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        logger ??= NullLogger.Instance;
        var entries = new List<Entry>();
        var sampled = new Dictionary<CompressionKind, int>();
        string? ffprobe = null;
        var ffprobeLooked = false;
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (AskedCompression.KindOf(preset, path) is not { } kind)
                continue;
            var entry = new Entry(path, kind, File.Exists(path) ? new FileInfo(path).Length : 0);
            entries.Add(entry);
            if (sampled.GetValueOrDefault(kind) >= SampledFiles(kind) || entry.Bytes == 0)
                continue;
            sampled[kind] = sampled.GetValueOrDefault(kind) + 1;
            entry.Sampled = true;
            try
            {
                switch (kind)
                {
                    case CompressionKind.Image:
                        entry.Tile = ReadTile(path, preset.Image, out entry.PixelFactor);
                        break;
                    case CompressionKind.Video or CompressionKind.Audio:
                        if (!ffprobeLooked)
                            (ffprobe, ffprobeLooked) = (FfmpegConverter.Locate()?.Ffprobe, true);
                        if (ffprobe is not null)
                            entry.Media = await MediaInfo.ProbeAsync(ffprobe, path, cancellationToken);
                        break;
                }
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
            {
                // That file can't be estimated; the others still can, and the window says when none could.
                logger.LogInformation(ex, "Could not read {File} to estimate its compressed size", Path.GetFileName(path));
                entry.Sampled = false;
            }
        }
        return new CompressionEstimator(preset, entries, logger);
    }, cancellationToken);

    /// <summary>
    /// About how many bytes the files come to with <paramref name="choice"/>, or null when none of them could be
    /// estimated (no FFmpeg, a picture format ImageMagick doesn't write, a damaged file). On a worker thread; a call
    /// waits for the one before it.
    /// </summary>
    public async Task<long?> EstimateAsync(CompressionChoice choice, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => Estimate(choice, cancellationToken), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private long? Estimate(CompressionChoice choice, CancellationToken cancellationToken)
    {
        var preset = AskedCompression.Apply(_preset, choice);
        long total = 0;
        var any = false;
        foreach (var group in _entries.GroupBy(e => e.Kind))
        {
            long sourceKnown = 0, estimated = 0;
            foreach (var entry in group.Where(e => e.Sampled))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (EstimateOne(entry, preset, cancellationToken) is not { } bytes)
                    continue;
                sourceKnown += entry.Bytes;
                estimated += bytes;
            }
            if (sourceKnown == 0)
                return null; // a kind none of whose files could be read: any total would leave them out
            any = true;
            var ratio = (double)estimated / sourceKnown;
            total += estimated + (long)Math.Round(group.Where(e => !e.Sampled).Sum(e => e.Bytes) * ratio);
        }
        return any ? total : null;
    }

    private long? EstimateOne(Entry entry, Preset preset, CancellationToken cancellationToken)
    {
        try
        {
            return entry.Kind switch
            {
                CompressionKind.Image => ImageBytes(entry, preset),
                CompressionKind.Video => VideoBytes(entry, preset),
                CompressionKind.Audio => AudioBytes(entry, preset),
                _ => PdfBytes(entry, preset.Pdf.Compression, cancellationToken),
            };
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            _logger.LogInformation(ex, "Could not estimate the compressed size of {File}", Path.GetFileName(entry.Path));
            return null;
        }
    }

    /// <summary>
    /// The middle of the picture, at most <see cref="TileSide"/> square at full resolution, and how many times its pixels
    /// the whole picture has once the preset has resized it (<paramref name="pixelFactor"/>).
    /// </summary>
    internal static MagickImage ReadTile(string path, ImageOptions options, out double pixelFactor)
    {
        var image = new MagickImage(path, new MagickReadSettings { FrameIndex = 0, FrameCount = 1 });
        try
        {
            double width = image.Width, height = image.Height;
            var scale = ImageEncoder.ResizeScale(width, height, options);
            var quality = image.Quality;
            var format = image.Format;
            if (width > TileSide || height > TileSide)
            {
                var (tileWidth, tileHeight) = ((uint)Math.Min(width, TileSide), (uint)Math.Min(height, TileSide));
                image.Crop(new MagickGeometry((int)((width - tileWidth) / 2), (int)((height - tileHeight) / 2), tileWidth, tileHeight));
                image.ResetPage();
                // Crop keeps what was read, but the encoder's "never raise a JPEG's quality" reads both.
                (image.Quality, image.Format) = (quality, format);
            }
            pixelFactor = width * height * scale * scale / ((double)image.Width * image.Height);
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private long? ImageBytes(Entry entry, Preset preset)
    {
        if (entry.Tile is not { } tile)
            return null;
        var target = preset.TargetFormat == BuiltInData.SameAsSource ? FormatRegistry.Detect(entry.Path)?.Id ?? "" : preset.TargetFormat;
        var quality = preset.Image.Quality;
        if (!entry.ImageCache.TryGetValue(quality, out var encoded))
        {
            // The preset's options on the tile, without its resizing: the tile stands for the resized picture already.
            var options = preset.Image.Clone();
            (options.Resize, options.CustomSize, options.MinShortEdge) = (ResizeMode.None, null, null);
            using var copy = (MagickImage)tile.Clone();
            (copy.Quality, copy.Format) = (tile.Quality, tile.Format);
            ImageEncoder.ApplyOptions(copy, options, target);
            var tileBytes = copy.ToByteArray(ImageEncoder.OutputFormat(target, options, entry.Path).Format).LongLength;
            var tilePixels = (double)copy.Width * copy.Height;
            encoded = (long)Math.Round(tileBytes * entry.PixelFactor * (tile.Width * (double)tile.Height) / tilePixels);
            entry.ImageCache[quality] = encoded;
        }
        var limit = preset.Image.MaxSizeMb > 0 ? SizeLimit.Bytes(preset.Image.MaxSizeMb) : 0;
        return AskedCompression.Result(entry.Bytes, encoded, limit, preset.Output.OnlyIfSmaller);
    }

    private static long? VideoBytes(Entry entry, Preset preset)
    {
        var media = preset.Media;
        var target = preset.TargetFormat;
        if (entry.Media is not { Video: { } video, Duration: { TotalSeconds: > 0 } length } info
            || !MediaEncoding.VideoTargets.TryGetValue(target, out var spec))
            return null;
        long encoded;
        if (MediaEncoding.CanCopyVideo(target, video, media))
            encoded = entry.Bytes; // copied as it is: the size stays
        else
        {
            var codec = MediaCodecs.Effective(target, media.VideoCodec) is VideoCodec.Hevc or VideoCodec.Av1 ? "hevc" : "h264";
            var audio = media.RemoveAudio ? 0 : info.AudioTracks.Sum(track =>
                MediaEncoding.AudioKbps(MediaEncoding.AudioEncoder(target, spec.AudioCodec, media), media, track.Channels) ?? 0);
            encoded = AskedCompression.MediaBytes(length.TotalSeconds, HardwareVideo.BitrateKbps(codec, media.Quality, video, media.MaxHeight) + audio);
        }
        return AskedCompression.Result(entry.Bytes, encoded, SizeLimit.ForVideo(media, entry.Bytes), preset.Output.OnlyIfSmaller);
    }

    private static long? AudioBytes(Entry entry, Preset preset)
    {
        var media = preset.Media;
        if (entry.Media is not { Audio: { } audio, Duration: { TotalSeconds: > 0 } length }
            || !MediaEncoding.AudioTargets.TryGetValue(preset.TargetFormat, out var spec)
            || MediaEncoding.AudioKbps(MediaEncoding.AudioEncoder(preset.TargetFormat, spec.AudioCodec, media), media, audio.Channels) is not { } kbps)
            return null;
        return AskedCompression.Result(entry.Bytes, AskedCompression.MediaBytes(length.TotalSeconds, kbps), 0, preset.Output.OnlyIfSmaller);
    }

    private static long? PdfBytes(Entry entry, PdfCompression level, CancellationToken cancellationToken)
    {
        if (level == PdfCompression.None)
            return entry.Bytes;
        if (!entry.PdfCache.TryGetValue(level, out var compressed))
        {
            using var document = PdfSharpConverter.OpenForChanges(entry.Path, cancellationToken);
            PdfCompressor.Compress(document, level, keepMetadata: true, cancellationToken);
            using var counter = new CountingStream();
            document.Save(counter, closeStream: false);
            compressed = counter.Length;
            entry.PdfCache[level] = compressed;
        }
        // The smaller of the two is kept, as when it converts.
        return AskedCompression.Result(entry.Bytes, compressed, 0, onlyIfSmaller: true);
    }

    public void Dispose()
    {
        foreach (var entry in _entries)
            entry.Tile?.Dispose();
        _gate.Dispose();
    }

    private sealed class Entry(string path, CompressionKind kind, long bytes)
    {
        public string Path { get; } = path;
        public CompressionKind Kind { get; } = kind;
        public long Bytes { get; } = bytes;

        /// <summary>Read for the estimate; the others count at the ratio of the ones that were.</summary>
        public bool Sampled;

        public MagickImage? Tile;
        public double PixelFactor = 1;
        public MediaInfo? Media;
        public Dictionary<int, long> ImageCache { get; } = [];
        public Dictionary<PdfCompression, long> PdfCache { get; } = [];
    }

    /// <summary>A stream that only counts what is written to it: the compressed PDF is measured, never kept.</summary>
    private sealed class CountingStream : Stream
    {
        private long _length;

        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get; set; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            _ => _length + offset,
        };

        public override void SetLength(long value) => _length = value;

        public override void Write(byte[] buffer, int offset, int count)
        {
            Position += count;
            _length = Math.Max(_length, Position);
        }
    }
}
