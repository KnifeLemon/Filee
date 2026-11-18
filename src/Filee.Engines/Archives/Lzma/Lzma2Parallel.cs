// LZMA2 decoding on several threads. A chunk that resets the dictionary (0x01, or an LZMA chunk with control 0xE0 and
// up) starts data that needs nothing from before it: the state, the properties and the window all start over. So the
// chunks from one such reset to the next (a segment) can be decoded side by side, each by its own Lzma2Reader, and
// handed out in order. 7-Zip writes a reset at every block when it compresses with several threads, xz and Filee's
// own writer start every block with one.
//
// - 7z: the packed stream of an LZMA2 coder lies in the archive, so the chunk headers are read first (they say how
//   long every chunk is) to find the segments; a stream with a single segment, or anything the headers don't explain,
//   is decoded the usual way, which also gives the usual errors.
// - .xz: every block is its own LZMA2 data, and a block header that names both sizes lets the reader take the block
//   whole and go on to the next (XzReader).
// What a segment decodes to is held in memory until it is read, so the threads and the size of a segment are
// bounded: at most one segment per processor, all of them in an eighth of the memory, none past 512 MiB. A segment
// that fails on its thread is decoded again, from its start, on the reader's thread as it is read: so damaged data
// gives the very bytes and the very error, at the same place, as the one-thread decoder.

using System.Runtime.ExceptionServices;

namespace Filee.Engines.Archives.Lzma;

/// <summary>Decodes LZMA2 segments on worker threads.</summary>
internal static class Lzma2Parallel
{
    /// <summary>The largest segment decoded on its own thread.</summary>
    public const int MaxSegment = 512 << 20;

    /// <summary>Segments smaller than this are joined with the next (fewer, larger tasks).</summary>
    private const int MinSegment = 4 << 20;

    /// <summary>Tests turn the threads off (false) or on for any data (true).</summary>
    internal static readonly AsyncLocal<bool?> TestMode = new();

    /// <summary>True when decoding on several threads is allowed at all.</summary>
    public static bool Enabled => TestMode.Value ?? Environment.ProcessorCount > 1;

    /// <summary>True when tests ask for threads even where they would not pay off.</summary>
    public static bool Forced => TestMode.Value == true;

    /// <summary>7z coders decoded on several threads so far (for tests).</summary>
    internal static int Opened;

    /// <summary>Memory the decoded segments of one stream may hold at once: an eighth of what the machine has.</summary>
    public static long Budget
    {
        get
        {
            try
            {
                var total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                return total > 0 ? total / 8 : 256L << 20;
            }
            catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
            {
                return 256L << 20;
            }
        }
    }

    /// <summary>
    /// Segments that fit in the memory budget at once, at most one per processor (the one being read counts too), for
    /// segments of up to <paramref name="unpacked"/> bytes from <paramref name="packed"/> bytes.
    /// </summary>
    public static int Workers(long unpacked, long packed, uint dictionary)
    {
        var each = unpacked + packed + Math.Min(dictionary, unpacked);
        var byMemory = Budget / Math.Max(1, each) - 1;
        var workers = (int)Math.Min(Environment.ProcessorCount, byMemory);
        return Forced ? Math.Max(2, workers) : workers;
    }

    /// <summary>
    /// Opens the LZMA2 data of a 7z coder in <paramref name="packed"/> for decoding on several threads; null when it
    /// has fewer than two segments or doesn't read as expected (the caller then decodes it the usual way).
    /// </summary>
    public static Stream? TryOpen(PackedWindow packed, uint dictionary, long size, CancellationToken cancellationToken)
    {
        if (!Enabled || size <= 0)
            return null;
        var segments = Scan(packed, size);
        if (segments is null || segments.Count < 2)
            return null;
        long largest = 0, largestPacked = 0;
        foreach (var segment in segments)
        {
            largest = Math.Max(largest, segment.Unpacked);
            largestPacked = Math.Max(largestPacked, segment.Length);
        }
        var workers = Math.Min(Workers(largest, largestPacked, dictionary), segments.Count);
        if (workers < 2)
            return null;
        Interlocked.Increment(ref Opened);
        return new SegmentStream(packed, segments, dictionary, workers, cancellationToken);
    }

    /// <summary>One run of chunks from a dictionary reset, at <see cref="Offset"/> in the packed stream.</summary>
    internal readonly record struct Segment(long Offset, int Length, int Unpacked);

    /// <summary>
    /// The segments of the LZMA2 data, from its chunk headers; null when the data does not end with the end byte
    /// within the packed stream, doesn't start with a dictionary reset, has an unknown chunk type, a segment past
    /// <see cref="MaxSegment"/>, or doesn't unpack to <paramref name="size"/> bytes.
    /// </summary>
    private static List<Segment>? Scan(PackedWindow packed, long size)
    {
        var segments = new List<Segment>();
        var minSegment = Forced ? 1 : MinSegment;
        Span<byte> header = stackalloc byte[6];
        long at = 0;
        long start = 0;
        long unpacked = 0;
        long total = 0;
        var first = true;
        while (true)
        {
            if (at >= packed.Length || packed.ReadAt(at, header[..1]) < 1)
                return null;
            var control = header[0];
            if (control == 0)
                break;
            var reset = control == 1 || control >= 0xE0;
            if (first && !reset)
                return null;
            if (reset && !first && unpacked >= minSegment)
            {
                segments.Add(new Segment(start, (int)(at - start), (int)unpacked));
                start = at;
                unpacked = 0;
            }
            first = false;
            int chunkUnpacked, chunkLength;
            if (control < 0x80)
            {
                if (control > 2 || packed.ReadAt(at + 1, header[..2]) < 2)
                    return null;
                chunkUnpacked = (header[0] << 8 | header[1]) + 1;
                chunkLength = 3 + chunkUnpacked;
            }
            else
            {
                if (packed.ReadAt(at + 1, header[..4]) < 4)
                    return null;
                chunkUnpacked = ((control & 0x1F) << 16 | header[0] << 8 | header[1]) + 1;
                chunkLength = 5 + (header[2] << 8 | header[3]) + 1 + (control >= 0xC0 ? 1 : 0);
            }
            unpacked += chunkUnpacked;
            total += chunkUnpacked;
            at += chunkLength;
            if (unpacked > MaxSegment || at - start > MaxSegment || total > size)
                return null;
        }
        if (total != size || unpacked == 0)
            return null;
        segments.Add(new Segment(start, (int)(at - start), (int)unpacked));
        return segments;
    }

    /// <summary>What a worker decoded: the output, how much of it is good, and the error that stopped it, if any.</summary>
    internal sealed class Decoded(byte[] output)
    {
        public byte[] Output { get; } = output;
        public int Count { get; set; }
        public ExceptionDispatchInfo? Error { get; set; }
    }

    /// <summary>
    /// Decodes one segment: <paramref name="length"/> bytes of chunks followed by the end byte (the caller puts it at
    /// <c>packed[length]</c>), expected to give <paramref name="unpacked"/> bytes.
    /// </summary>
    public static Decoded DecodeSegment(byte[] packed, int length, uint dictionary, int unpacked, CancellationToken cancellationToken)
    {
        var result = new Decoded(GC.AllocateUninitializedArray<byte>(unpacked));
        try
        {
            var reader = new Lzma2Reader(new ByteInput(new MemoryStream(packed, 0, length + 1, writable: false)), dictionary, unpacked,
                cancellationToken);
            var output = result.Output;
            while (result.Count < unpacked)
            {
                var read = reader.Read(output.AsSpan(result.Count));
                if (read == 0)
                    throw new InvalidDataException("the LZMA2 chunk is damaged");
                result.Count += read;
            }
        }
        catch (Exception ex)
        {
            result.Error = ExceptionDispatchInfo.Capture(ex);
        }
        return result;
    }

    /// <summary>The segments of a 7z LZMA2 coder, decoded ahead on worker threads and read in order.</summary>
    private sealed class SegmentStream : Stream
    {
        private readonly PackedWindow _packed;
        private readonly List<Segment> _segments;
        private readonly uint _dictionary;
        private readonly int _workers;
        private readonly CancellationTokenSource _cancel;
        private readonly Queue<(Segment Segment, Task<Decoded> Work)> _ahead = new();
        private int _next;
        private Decoded? _current;
        private int _position;
        private Lzma2Reader? _replay; // a failed segment, decoded again here

        public SegmentStream(PackedWindow packed, List<Segment> segments, uint dictionary, int workers, CancellationToken cancellationToken)
        {
            _packed = packed;
            _segments = segments;
            _dictionary = dictionary;
            _workers = workers;
            _cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty)
                return 0;
            while (true)
            {
                if (_replay is not null)
                {
                    var read = _replay.Read(buffer);
                    if (read > 0)
                        return read;
                    _replay = null;
                    continue;
                }
                if (_current is { } current)
                {
                    if (_position < current.Count)
                    {
                        var count = Math.Min(buffer.Length, current.Count - _position);
                        current.Output.AsSpan(_position, count).CopyTo(buffer);
                        _position += count;
                        return count;
                    }
                    _current = null;
                }
                _cancel.Token.ThrowIfCancellationRequested();
                Start();
                if (!_ahead.TryDequeue(out var next))
                    return 0;
                var decoded = next.Work.GetAwaiter().GetResult();
                if (decoded.Error is { } error)
                {
                    if (error.SourceException is OperationCanceledException)
                        error.Throw();
                    _replay = Open(next.Segment, _cancel.Token);
                    continue;
                }
                _current = decoded;
                _position = 0;
            }
        }

        /// <summary>Reads the packed bytes of the next segments (on this thread: the archive stream is shared) and starts their decoders.</summary>
        private void Start()
        {
            while (_ahead.Count < _workers && _next < _segments.Count)
            {
                var segment = _segments[_next++];
                var packed = ReadPacked(segment);
                var dictionary = _dictionary;
                var token = _cancel.Token;
                _ahead.Enqueue((segment, Task.Run(() => DecodeSegment(packed, segment.Length, dictionary, segment.Unpacked, token), CancellationToken.None)));
            }
        }

        /// <summary>A segment's chunks and the end byte after them, so its reader ends cleanly.</summary>
        private byte[] ReadPacked(Segment segment)
        {
            var packed = GC.AllocateUninitializedArray<byte>(segment.Length + 1);
            if (_packed.ReadAt(segment.Offset, packed.AsSpan(0, segment.Length)) < segment.Length)
                throw new InvalidDataException("the LZMA2 data ends too early");
            packed[segment.Length] = 0x00;
            return packed;
        }

        /// <summary>A reader of one segment, on this thread.</summary>
        private Lzma2Reader Open(Segment segment, CancellationToken cancellationToken) =>
            new(new ByteInput(new MemoryStream(ReadPacked(segment), writable: false)), _dictionary, segment.Unpacked, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _cancel.Cancel();
                _cancel.Dispose();
            }
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>A packed stream that lies at a known place in a seekable stream others read too (a 7z pack stream).</summary>
internal sealed class PackedWindow(Stream shared, long offset, long length)
{
    public long Length => length;

    /// <summary>Reads up to <paramref name="target"/>'s length at <paramref name="at"/> within the window; returns the bytes read.</summary>
    public int ReadAt(long at, Span<byte> target)
    {
        if (at < 0 || at >= length)
            return 0;
        if (target.Length > length - at)
            target = target[..(int)(length - at)];
        shared.Position = offset + at;
        return shared.ReadAtLeast(target, target.Length, throwOnEndOfStream: false);
    }
}
