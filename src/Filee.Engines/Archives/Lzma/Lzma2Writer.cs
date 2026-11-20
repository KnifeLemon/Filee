// LZMA2 encoding (see Lzma2Reader for the chunk layout). The input is cut into blocks of a fixed size (one to four
// dictionaries by level, see Lzma2Settings.BlockSize) and each block is coded on its own, starting with a dictionary
// reset: blocks are coded on several threads at once, and since where a block ends depends only on the input and the
// level, the output is the same bytes whatever the number of threads. A reader can decode the blocks side by side
// too (Lzma2Parallel). Within a block, one LZMA chunk is coded at a time: up to 2 MiB of input, stopped early when
// its output nears 64 KiB. A chunk that came out no smaller than its input is written as stored chunks instead, and
// the next LZMA chunk resets the state (the decoder never saw the probabilities the discarded attempt left behind).
// A block's first chunk resets the dictionary and sets the properties; after a first stored chunk (0x01) the decoder
// needs the properties again, so the next LZMA chunk carries them (0xC0).
// With processors to spare (fewer blocks than half of them), each block's binary-tree match finder searches ahead on
// threads of its own (MatchFinder.StartAhead, the trees split between them by hash), which gives the same bytes too.

using System.Numerics;
using System.Runtime.CompilerServices;
using Filee.Core.Presets;

namespace Filee.Engines.Archives.Lzma;

/// <summary>Dictionary and search effort for one <see cref="ArchiveLevel"/>.</summary>
internal readonly record struct Lzma2Settings(int Dictionary, LzmaEncoderSettings Encoder, bool Store)
{

    /// <summary>
    /// The settings for <paramref name="level"/>, with the dictionary cut to the input when its size is known. Memory
    /// for one block's encoder is about 8 × the dictionary with the hash chain (fast: 256 KiB → ~2 MB; small, so the
    /// tables mostly stay in the processor's cache, and the lazy parse makes up the ratio) and 10–11 × with
    /// the binary tree (normal: 16 MiB → ~180 MB, maximum: 64 MiB → ~660 MB, as xz -9), plus the block itself while it
    /// is coded (<see cref="BlockSize"/>); unpacking needs only the dictionary.
    /// </summary>
    public static Lzma2Settings For(ArchiveLevel level, long expectedSize = -1)
    {
        var settings = level switch
        {
            ArchiveLevel.Store => new Lzma2Settings(1 << 12, default, Store: true),
            // Six chain candidates: against eight, about 4 % less work for 0.25 % larger output.
            ArchiveLevel.Fast => new Lzma2Settings(1 << 18, new LzmaEncoderSettings(Depth: 6, Nice: 32, Optimal: false, Tree: false), false),
            ArchiveLevel.Maximum => new Lzma2Settings(1 << 26, new LzmaEncoderSettings(Depth: 64, Nice: 64, Optimal: true, Tree: true), false),
            _ => new Lzma2Settings(1 << 24, new LzmaEncoderSettings(Depth: 32, Nice: 32, Optimal: true, Tree: true), false),
        };
        if (expectedSize >= 0)
        {
            var fit = (int)Math.Min(1 << 30, BitOperations.RoundUpToPowerOf2((ulong)Math.Max(expectedSize, 1 << 12)));
            settings = settings with { Dictionary = Math.Min(settings.Dictionary, fit) };
        }
        return settings;
    }

    /// <summary>
    /// The input coded as one block, from one dictionary reset to the next, at least 1 MiB and at most 256 MiB: one
    /// dictionary at the fast level (that is 1 MiB: many blocks for many threads), two at normal (about 0.15 % larger
    /// output), four at maximum. Matches never reach into the block before, which costs that little ratio.
    /// </summary>
    public int BlockSize => (int)Math.Clamp((long)Dictionary * (!Encoder.Optimal ? 1 : Encoder.Depth >= 64 ? 4 : 2), 1 << 20, 1 << 28);
}

internal sealed class Lzma2Writer
{
    /// <summary>Tests set the number of threads (normally from the processors, the blocks expected and memory).</summary>
    internal static readonly AsyncLocal<int?> TestThreads = new();

    /// <summary>Tests set a smaller block size, to get many blocks from little input.</summary>
    internal static readonly AsyncLocal<int?> TestBlockSize = new();

    /// <summary>Tests turn the match finder's own thread on or off (normally on with processors to spare).</summary>
    internal static readonly AsyncLocal<bool?> TestSearchAhead = new();

    /// <summary>Tests set how many threads the match finder of each block searches on (normally from the processors).</summary>
    internal static readonly AsyncLocal<int?> TestSearchThreads = new();

    private readonly Stream _output;
    private readonly Lzma2Settings _settings;
    private readonly Action<Lzma2Block>? _onBlock;
    private readonly CancellationToken _cancellationToken;
    private readonly int _blockSize;
    private readonly int _firstCapacity;
    private readonly int _threads;
    private readonly int _searchThreads; // per block: 0 for none, else the match finder's tree threads
    private readonly bool _check;
    private readonly Stack<Lzma2BlockEncoder> _free = new();
    private readonly Queue<(Lzma2BlockEncoder Encoder, Task Work)> _pending = new();
    private int _created;
    private Lzma2BlockEncoder? _current;
    private bool _finished;

    /// <param name="expectedSize">
    /// The input size if known (-1 otherwise); it sizes the first block's buffer and how many threads are worth
    /// starting, never where blocks end.
    /// </param>
    /// <param name="onBlock">
    /// Takes each coded block (in order) instead of the output: then nothing is written to <paramref name="output"/>,
    /// the end byte neither, and each block's input gets a CRC-64 (for .xz blocks).
    /// </param>
    public Lzma2Writer(Stream output, Lzma2Settings settings, long expectedSize, CancellationToken cancellationToken,
        Action<Lzma2Block>? onBlock = null)
    {
        _output = output;
        _settings = settings;
        _onBlock = onBlock;
        _check = onBlock is not null;
        _cancellationToken = cancellationToken;
        _blockSize = TestBlockSize.Value ?? settings.BlockSize;
        _firstCapacity = expectedSize >= 0
            ? (int)Math.Min(_blockSize, Math.Max(expectedSize, 1) + LzmaModel.MatchMaxLength + 4)
            : _blockSize;
        _threads = TestThreads.Value ?? Threads(settings, _blockSize, expectedSize);
        // Processors left over by the blocks go to their match finders: the trees are split between threads by hash.
        var searchAhead = settings.Encoder is { Tree: true, Optimal: true }
                          && (TestSearchAhead.Value ?? 2 * _threads <= Environment.ProcessorCount);
        _searchThreads = !searchAhead ? 0
            : TestSearchThreads.Value ?? Math.Clamp(Environment.ProcessorCount / _threads - 1, 1, MatchFinder.MaxSearchThreads);
    }

    /// <summary>The dictionary size the decoder needs (for the filter properties).</summary>
    public int Dictionary => _settings.Dictionary;

    /// <summary>Bytes written to the output so far.</summary>
    public long Written { get; private set; }

    /// <summary>
    /// Threads worth starting: one per block expected, at most one per processor, and no more block encoders than an
    /// eighth of the memory holds.
    /// </summary>
    private static int Threads(Lzma2Settings settings, int blockSize, long expectedSize)
    {
        var blocks = expectedSize < 0 ? int.MaxValue : (int)Math.Min(int.MaxValue, (expectedSize + blockSize - 1) / blockSize);
        var perBlock = Lzma2BlockEncoder.MemoryFor(settings, blockSize);
        long memory;
        try
        {
            memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            memory = 0;
        }
        var byMemory = memory > 0 ? (int)Math.Min(int.MaxValue, memory / 8 / perBlock) : 1;
        return Math.Max(1, Math.Min(Math.Min(Environment.ProcessorCount, blocks), byMemory));
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        while (!data.IsEmpty)
        {
            _current ??= Acquire();
            var room = _blockSize - _current.Filled;
            if (_current.Room == 0)
                _current.Grow(_blockSize);
            var count = _current.Append(data[..Math.Min(data.Length, room)]);
            data = data[count..];
            if (_current.Filled == _blockSize)
            {
                Dispatch(_current);
                _current = null;
            }
        }
    }

    /// <summary>Codes what is left and writes the end byte (not with a block callback).</summary>
    public void Finish()
    {
        if (_finished)
            return;
        if (_current is { Filled: > 0 })
            Dispatch(_current);
        _current = null;
        while (_pending.Count > 0)
            _free.Push(Complete(_pending.Dequeue()));
        _finished = true;
        if (_onBlock is null)
        {
            _output.WriteByte(0x00);
            Written++;
        }
    }

    /// <summary>A block encoder to fill: an idle one, a new one while there are threads to spare, or the oldest busy one once done.</summary>
    private Lzma2BlockEncoder Acquire()
    {
        if (_free.TryPop(out var idle))
        {
            idle.Reset();
            return idle;
        }
        if (_created < _threads)
        {
            _created++;
            return new Lzma2BlockEncoder(_settings, _created == 1 ? _firstCapacity : _blockSize, _check);
        }
        var done = Complete(_pending.Dequeue());
        done.Reset();
        return done;
    }

    private void Dispatch(Lzma2BlockEncoder encoder)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (_threads == 1)
        {
            encoder.Encode(_cancellationToken, _searchThreads);
            Emit(encoder);
            _free.Push(encoder);
            return;
        }
        var (token, ahead) = (_cancellationToken, _searchThreads);
        _pending.Enqueue((encoder, Task.Run(() => encoder.Encode(token, ahead), CancellationToken.None)));
    }

    /// <summary>Waits for a block, writes it out and hands its encoder back.</summary>
    private Lzma2BlockEncoder Complete((Lzma2BlockEncoder Encoder, Task Work) pending)
    {
        pending.Work.GetAwaiter().GetResult();
        Emit(pending.Encoder);
        return pending.Encoder;
    }

    private void Emit(Lzma2BlockEncoder encoder)
    {
        if (_onBlock is not null)
        {
            _onBlock(new Lzma2Block(encoder.Output, encoder.Filled, encoder.Check));
            return;
        }
        _output.Write(encoder.Output);
        Written += encoder.Output.Length;
    }
}

/// <summary>One coded LZMA2 block: its chunks (without the end byte), its input size and that input's CRC-64.</summary>
internal readonly ref struct Lzma2Block(ReadOnlySpan<byte> chunks, int unpacked, ulong check)
{
    public ReadOnlySpan<byte> Chunks { get; } = chunks;
    public int Unpacked { get; } = unpacked;
    public ulong Check { get; } = check;
}

/// <summary>Codes one LZMA2 block (all of it in its buffer) into chunks that start with a dictionary reset.</summary>
internal sealed class Lzma2BlockEncoder
{
    private const int StoredChunk = 1 << 16;

    // Stop a chunk while a worst-case symbol (about 25 bytes) still fits into the 64 KiB a chunk may hold.
    private const int PackedLimit = Lzma2Reader.MaxChunkPacked - 64;

    private readonly Lzma2Settings _settings;
    private readonly bool _check;
    private readonly MatchFinder? _mf;
    private readonly LzmaEncoder? _encoder;
    private readonly RangeEncoder? _rc;
    private byte[] _stored = []; // the input at the store level
    private int _storedCount;
    private byte[] _out;
    private int _outCount;
    private bool _first;
    private bool _needProperties;
    private bool _needStateReset;

    public Lzma2BlockEncoder(Lzma2Settings settings, int capacity, bool check)
    {
        _settings = settings;
        _check = check;
        _out = new byte[Math.Max(1 << 16, capacity / 4)];
        if (settings.Store)
        {
            _stored = new byte[capacity];
            return;
        }
        _mf = new MatchFinder(settings.Dictionary, Math.Max(0, capacity - settings.Dictionary), settings.Encoder.Depth,
            settings.Encoder.Nice, settings.Encoder.Tree);
        _encoder = new LzmaEncoder(_mf, LzmaProperties.Default, settings.Encoder);
        _rc = new RangeEncoder(Lzma2Reader.MaxChunkPacked + 1024);
    }

    /// <summary>About the memory one block encoder takes: its buffer, the match finder's tables and its output.</summary>
    public static long MemoryFor(Lzma2Settings settings, int blockSize) =>
        settings.Store ? 2L * blockSize : blockSize * 3L / 2 + MatchFinder.TableBytes(settings.Dictionary, settings.Encoder.Tree);

    /// <summary>Input bytes in the block.</summary>
    public int Filled => _mf?.Filled ?? _storedCount;

    /// <summary>Room left in the buffer.</summary>
    public int Room => _mf is not null ? _mf.Buffer.Length - _mf.Filled : _stored.Length - _storedCount;

    /// <summary>The coded chunks, after <see cref="Encode"/>.</summary>
    public ReadOnlySpan<byte> Output => _out.AsSpan(0, _outCount);

    /// <summary>CRC-64 of the input, after <see cref="Encode"/> (only when asked for).</summary>
    public ulong Check { get; private set; }

    public int Append(ReadOnlySpan<byte> data)
    {
        if (_mf is not null)
            return _mf.Append(data);
        var count = Math.Min(data.Length, _stored.Length - _storedCount);
        data[..count].CopyTo(_stored.AsSpan(_storedCount));
        _storedCount += count;
        return count;
    }

    /// <summary>Makes room for up to <paramref name="capacity"/> bytes of input (the input was larger than expected).</summary>
    public void Grow(int capacity)
    {
        if (_mf is not null)
        {
            _mf.Grow(capacity);
            return;
        }
        if (_stored.Length < capacity)
            Array.Resize(ref _stored, Math.Min(capacity, Math.Max(_stored.Length * 2, 1 << 20)));
    }

    /// <summary>Empties the block for the next one, keeping the buffers.</summary>
    public void Reset()
    {
        _storedCount = 0;
        _outCount = 0;
        if (_mf is null)
            return;
        _mf.Reset();
        _encoder!.Restart();
    }

    /// <summary>Codes the whole block.</summary>
    /// <param name="searchThreads">Lets the match finder search ahead on this many threads of its own (binary tree, large blocks; 0 for none).</param>
    public void Encode(CancellationToken cancellationToken, int searchThreads = 0)
    {
        _first = true;
        _needProperties = false;
        _needStateReset = false;
        _outCount = 0;
        if (_check)
            Check = Crc64.Compute(_mf is not null ? _mf.Buffer.AsSpan(0, _mf.Filled) : _stored.AsSpan(0, _storedCount));
        if (_mf is null)
        {
            WriteStored(_stored.AsSpan(0, _storedCount));
            return;
        }
        if (searchThreads > 0 && _settings.Encoder.Tree && _mf.Filled >= 1 << 18)
            _mf.StartAhead(cancellationToken, searchThreads);
        try
        {
            while (_mf.Position < _mf.Filled)
                EncodeChunk(Math.Min(_mf.Filled, _mf.Position + Lzma2Reader.MaxChunkUnpacked), cancellationToken);
        }
        finally
        {
            _mf.StopAhead();
        }
    }

    // Compiled fully optimised on first use, with the fast level's per-symbol code (LzmaEncoder.Encode and EncodeOne
    // and the helpers marked AggressiveInlining) inlined, as the tiered compiler ends up doing; the methods it calls
    // stay separate and are compiled the same way. A chunk then runs at full speed from the first one, where the
    // ReadyToRun and instrumented code made the first second of a fast job about a third slower, more so with many
    // threads.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void EncodeChunk(int end, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var mf = _mf!;
        var rc = _rc!;
        var start = mf.Position;
        if (_needStateReset)
            _encoder!.ResetState();
        rc.Reset();
        _encoder!.Encode(rc, end, PackedLimit);
        rc.Flush();
        var unpacked = mf.Position - start;
        var packed = rc.Output.Length;

        if (packed + 6 >= unpacked + 3 * ((unpacked + StoredChunk - 1) / StoredChunk))
        {
            // Didn't shrink: store it. The decoder's state doesn't see the attempt, so the next LZMA chunk resets it.
            WriteStored(mf.Buffer.AsSpan(start, unpacked));
            return;
        }

        int control;
        if (_first)
            control = 0xE0;
        else if (_needProperties)
            control = 0xC0;
        else if (_needStateReset)
            control = 0xA0;
        else
            control = 0x80;
        var u = unpacked - 1;
        var p = packed - 1;
        Span<byte> header = stackalloc byte[6];
        header[0] = (byte)(control | (u >> 16));
        header[1] = (byte)(u >> 8);
        header[2] = (byte)u;
        header[3] = (byte)(p >> 8);
        header[4] = (byte)p;
        var headerLength = 5;
        if (control >= 0xC0)
            header[headerLength++] = LzmaProperties.Default.ToByte();
        Put(header[..headerLength]);
        Put(rc.Output);
        _first = false;
        _needProperties = false;
        _needStateReset = false;
    }

    private void WriteStored(ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[3];
        while (!data.IsEmpty)
        {
            var count = Math.Min(data.Length, StoredChunk);
            var u = count - 1;
            header[0] = (byte)(_first ? 0x01 : 0x02);
            header[1] = (byte)(u >> 8);
            header[2] = (byte)u;
            Put(header);
            Put(data[..count]);
            if (_first)
                _needProperties = true;
            _first = false;
            _needStateReset = true;
            data = data[count..];
        }
    }

    private void Put(ReadOnlySpan<byte> data)
    {
        if (_outCount + data.Length > _out.Length)
            Array.Resize(ref _out, Math.Max(_outCount + data.Length, _out.Length * 2));
        data.CopyTo(_out.AsSpan(_outCount));
        _outCount += data.Length;
    }
}
