// The encoder's match finder. Every position is entered under a hash of its next four bytes, in one of two ways:
// - Hash chain (fast level): each position links to the previous one with the same hash, and a search walks the
//   candidates newest first, up to a depth the level sets.
// - Binary tree (normal and maximum): the positions with the same hash form a search tree ordered by the bytes that
//   follow them, newest at the root. A search walks down from the root comparing the bytes at the cursor, and
//   rebuilds the tree on the way with the cursor as the new root (everything smaller to its left, larger to its
//   right). Bytes already known to match on both sides are not compared again, so the longest matches are found in
//   far fewer steps than a chain needs; inserting a position costs the same walk.
// Two chainless tables keyed by the next two and three bytes catch close short matches either way.
//
// The window is one array: the dictionary behind the cursor plus room ahead for new input, slid back when the room
// is used up. Positions are stored as buffer index + an offset that grows with each slide, so the tables never need
// rewriting when the window moves; only every ~1 GiB of input are they rebased (Normalize). 0 marks an empty slot.
//
// Searching ahead (binary tree, for the price-based parse, with the whole input in the buffer): a thread of its own
// searches every position in order, as the parse would search it or step over it (both walk the tree the same way),
// and hands the matches over in batches; the parse takes each position's list cut to the length it allows there.
// The lists are exactly those a search in the parse would find, so the output is the same bytes either way.

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace Filee.Engines.Archives.Lzma;

internal sealed class MatchFinder
{
    private const int Hash3Bits = 16;
    private const int MinAhead = 1 << 16;

    /// <summary>Tests lower the offset at which positions are rebased (normally 1 GiB).</summary>
    internal static readonly AsyncLocal<int?> TestNormalizeAt = new();

    private readonly int _dictionary;
    private readonly int _mask;
    private readonly bool _tree;
    private readonly int[] _links; // chain: one previous position per slot; tree: left and right child per slot
    private readonly int[] _head4;
    private readonly int[] _head3 = new int[1 << Hash3Bits];
    private readonly int[] _head2 = new int[1 << 16];
    private readonly int _hash4Shift;
    private readonly int _depth;
    private readonly int _nice;
    private int _offset = 1;
    private int _nextInsert;
    private Ahead? _ahead;

    /// <param name="dictionary">The window, a power of two (4 KiB … 256 MiB, so stored positions stay within an int).</param>
    /// <param name="ahead">Room for input beyond the window.</param>
    /// <param name="depth">How many candidates a search looks at.</param>
    /// <param name="nice">A match this long ends the search.</param>
    /// <param name="tree">Binary tree instead of hash chain.</param>
    public MatchFinder(int dictionary, int ahead, int depth, int nice, bool tree)
    {
        if (!BitOperations.IsPow2(dictionary) || dictionary < 1 << 12 || dictionary > 1 << 28)
            throw new ArgumentOutOfRangeException(nameof(dictionary));
        _dictionary = dictionary;
        _mask = dictionary - 1;
        _tree = tree;
        _links = new int[tree ? 2L * dictionary : dictionary];
        var hashBits = Math.Clamp(BitOperations.Log2((uint)dictionary) - 1, 16, 24);
        _head4 = new int[1 << hashBits];
        _hash4Shift = 32 - hashBits;
        _depth = depth;
        _nice = Math.Clamp(nice, 8, LzmaModel.MatchMaxLength);
        Buffer = new byte[dictionary + Math.Max(ahead, MinAhead)];
    }

    /// <summary>Bytes of the hash tables and links for a dictionary (the buffer not counted).</summary>
    public static long TableBytes(int dictionary, bool tree)
    {
        var hashBits = Math.Clamp(BitOperations.Log2((uint)dictionary) - 1, 16, 24);
        return 4L * ((tree ? 2L : 1L) * dictionary + (1 << hashBits) + (1 << Hash3Bits) + (1 << 16));
    }

    /// <summary>Forgets the input and every position entered, keeping the buffer and the tables' memory.</summary>
    public void Reset()
    {
        Position = 0;
        Filled = 0;
        _nextInsert = 0;
        _offset = 1;
        Array.Clear(_links);
        Array.Clear(_head4);
        Array.Clear(_head3);
        Array.Clear(_head2);
    }

    /// <summary>Makes the buffer hold up to <paramref name="capacity"/> bytes (at least doubling it), keeping its content.</summary>
    public void Grow(int capacity)
    {
        if (Buffer.Length >= capacity)
            return;
        var buffer = Buffer;
        Array.Resize(ref buffer, (int)Math.Min(capacity, Math.Max(2L * buffer.Length, 1 << 20)));
        Buffer = buffer;
    }

    /// <summary>The window: history before <see cref="Position"/>, input not yet encoded after it.</summary>
    public byte[] Buffer { get; private set; }

    /// <summary>Index of the next byte to encode.</summary>
    public int Position { get; set; }

    /// <summary>Bytes of <see cref="Buffer"/> in use.</summary>
    public int Filled { get; private set; }

    /// <summary>Copies as much of <paramref name="data"/> as there is room for; returns the bytes taken.</summary>
    public int Append(ReadOnlySpan<byte> data)
    {
        var count = Math.Min(data.Length, Buffer.Length - Filled);
        data[..count].CopyTo(Buffer.AsSpan(Filled));
        Filled += count;
        return count;
    }

    /// <summary>Moves the window back, keeping the dictionary behind <see cref="Position"/>, to make room for input.</summary>
    /// <returns>How far the window moved.</returns>
    public int Slide()
    {
        var shift = Position - _dictionary;
        if (shift <= 0)
            return 0;
        System.Buffer.BlockCopy(Buffer, shift, Buffer, 0, Filled - shift);
        Position -= shift;
        Filled -= shift;
        _nextInsert -= shift;
        _offset += shift;
        if (_offset > (TestNormalizeAt.Value ?? 1 << 30))
            Normalize();
        return shift;
    }

    /// <summary>
    /// Rebases the stored positions by a multiple of the dictionary size (so every position keeps its slot in the
    /// chain or tree); entries outside the window are dropped.
    /// </summary>
    private void Normalize()
    {
        var delta = (_offset - 1) / _dictionary * _dictionary;
        if (delta == 0)
            return;
        static void Rebase(int[] table, int delta)
        {
            for (var i = 0; i < table.Length; i++)
                table[i] = table[i] > delta ? table[i] - delta : 0;
        }
        Rebase(_links, delta);
        Rebase(_head4, delta);
        Rebase(_head3, delta);
        Rebase(_head2, delta);
        _offset -= delta;
    }

    /// <summary>
    /// Enters <see cref="Position"/> in the tables and returns the longest match there of at most
    /// <paramref name="maxLength"/> bytes (0 when there is none of at least 2), with its distance − 1.
    /// </summary>
    public int Find(int maxLength, out uint distance) => Search(Position, maxLength, null, null, out distance);

    /// <summary>
    /// Enters <paramref name="pos"/> in the tables and lists its matches of at most <paramref name="maxLength"/>
    /// bytes, each longer than the one before; returns how many.
    /// </summary>
    public int FindAll(int pos, int maxLength, int[] lengths, uint[] distances) =>
        _ahead is { } ahead ? ahead.Take(pos, maxLength, lengths, distances) : Search(pos, maxLength, lengths, distances, out _);

    /// <summary>
    /// Starts searching every position from <see cref="Position"/> to <see cref="Filled"/> on a thread of its own
    /// (binary tree only); <see cref="FindAll"/> then takes its results and <see cref="SkipTo"/> does nothing. Call
    /// <see cref="StopAhead"/> when done.
    /// </summary>
    public void StartAhead(CancellationToken cancellationToken)
    {
        if (!_tree)
            throw new InvalidOperationException("only the binary tree searches ahead");
        _ahead = new Ahead(this, Position, Filled, cancellationToken);
    }

    /// <summary>Stops the search thread (if any) and waits for it.</summary>
    public void StopAhead()
    {
        _ahead?.Stop();
        _ahead = null;
    }

    /// <summary>The matches found by one search, longest last.</summary>
    private ref struct Found(int maxLength, int[]? lengths, uint[]? distances)
    {
        public int Best = 1;
        public uint Distance;
        public int Count;

        public void Offer(int length, int gap)
        {
            length = Math.Min(length, maxLength);
            if (length <= Best)
                return;
            Best = length;
            Distance = (uint)(gap - 1);
            if (lengths is not null)
            {
                lengths[Count] = length;
                distances![Count] = Distance;
                Count++;
            }
        }
    }

    private int Search(int pos, int maxLength, int[]? lengths, uint[]? distances, out uint distance)
    {
        distance = 0;
        System.Diagnostics.Debug.Assert(pos >= _nextInsert, "a position is searched twice");
        SkipTo(pos);
        _nextInsert = pos + 1;
        if (pos + 4 > Filled)
            return 0;

        var buffer = Buffer;
        var value = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(pos));
        var stored = pos + _offset;
        var h2 = (int)(value & 0xFFFF);
        var h3 = (int)(((value & 0xFFFFFF) * 0x9E3779B1u) >> (32 - Hash3Bits));
        var h4 = (int)((value * 2654435761u) >> _hash4Shift);
        var candidate2 = _head2[h2];
        _head2[h2] = stored;
        var candidate3 = _head3[h3];
        _head3[h3] = stored;
        var candidate = _head4[h4];
        _head4[h4] = stored;

        var found = new Found(maxLength, lengths, distances);
        if (maxLength >= 2)
        {
            foreach (var near in (ReadOnlySpan<int>)[candidate2, candidate3])
            {
                if (near > 0 && stored - near < _dictionary && found.Best < maxLength)
                {
                    var at = near - _offset;
                    if (buffer[at] == buffer[pos])
                        found.Offer(Common(buffer, pos, at, maxLength), stored - near);
                }
            }
        }

        if (_tree)
        {
            TreeWalk(pos, stored, candidate, ref found);
            // The tree compares at most the nice length; a match that long may go on.
            if (found.Best >= _nice && found.Best < maxLength)
            {
                var at = pos - (int)found.Distance - 1;
                var longer = Common(buffer, pos, at, maxLength);
                found.Best = longer;
                if (lengths is not null)
                    lengths[found.Count - 1] = longer;
            }
        }
        else
        {
            _links[stored & _mask] = candidate;
            for (var depth = _depth; candidate > 0 && depth > 0 && found.Best < maxLength && found.Best < _nice; depth--)
            {
                var gap = stored - candidate;
                if (gap <= 0 || gap >= _dictionary)
                    break;
                var at = candidate - _offset;
                candidate = _links[candidate & _mask];
                if (buffer[at + found.Best] != buffer[pos + found.Best] || buffer[at] != buffer[pos])
                    continue;
                found.Offer(Common(buffer, pos, at, maxLength), gap);
            }
        }

        distance = found.Distance;
        if (found.Best < 2)
            return 0;
        return lengths is null ? found.Best : found.Count;
    }

    /// <summary>
    /// Walks the tree of <paramref name="candidate"/> (the old root) for the position <paramref name="pos"/>, offering
    /// what matches, and makes <paramref name="pos"/> the root.
    /// </summary>
    private void TreeWalk(int pos, int stored, int candidate, ref Found found)
    {
        var buffer = Buffer;
        var links = _links;
        var limit = Math.Min(_nice, Filled - pos);
        var leftSlot = (stored & _mask) << 1; // where the next candidate smaller than pos goes
        var rightSlot = leftSlot + 1;         // where the next candidate larger than pos goes
        int leftLength = 0, rightLength = 0;  // bytes known to match the bounds on either side
        for (var depth = _depth; ; depth--)
        {
            var gap = stored - candidate;
            if (candidate <= 0 || gap <= 0 || gap >= _dictionary || depth == 0)
            {
                links[leftSlot] = 0;
                links[rightSlot] = 0;
                return;
            }
            var at = candidate - _offset;
            var node = (candidate & _mask) << 1;
            var length = Math.Min(leftLength, rightLength);
            // Most candidates differ at the first byte not known to match: only then is the rest compared.
            if (length < limit && buffer[at + length] == buffer[pos + length])
            {
                length++;
                if (length < limit)
                    length += Common(buffer, pos + length, at + length, limit - length);
                found.Offer(length, gap);
            }
            if (length == limit)
            {
                // Equal as far as the tree looks: the candidate's subtrees become the new root's.
                links[leftSlot] = links[node];
                links[rightSlot] = links[node + 1];
                return;
            }
            if (buffer[at + length] < buffer[pos + length])
            {
                links[leftSlot] = candidate;
                leftSlot = node + 1;
                candidate = links[node + 1];
                leftLength = length;
            }
            else
            {
                links[rightSlot] = candidate;
                rightSlot = node;
                candidate = links[node];
                rightLength = length;
            }
        }
    }

    /// <summary>
    /// How many of the <paramref name="limit"/> bytes at <paramref name="a"/> and <paramref name="b"/> agree, eight at
    /// a time (most compares end within the first word, where a span compare would spend more on its setup).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Common(byte[] buffer, int a, int b, int limit)
    {
        if ((uint)limit > (uint)buffer.Length || (uint)a > (uint)(buffer.Length - limit) || (uint)b > (uint)(buffer.Length - limit))
            throw new ArgumentOutOfRangeException(nameof(limit));
        ref var data = ref MemoryMarshal.GetArrayDataReference(buffer);
        var n = 0;
        for (; n + 8 <= limit; n += 8)
        {
            var x = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref data, a + n)) ^ Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref data, b + n));
            if (x != 0)
                return n + (BitConverter.IsLittleEndian ? BitOperations.TrailingZeroCount(x) : BitOperations.LeadingZeroCount(x)) / 8;
        }
        while (n < limit && Unsafe.Add(ref data, a + n) == Unsafe.Add(ref data, b + n))
            n++;
        return n;
    }

    /// <summary>Enters every position before <paramref name="pos"/> not entered yet (the inside of a match).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SkipTo(int pos)
    {
        if (_nextInsert < pos && _ahead is null)
            Insert(pos);
    }

    private void Insert(int pos)
    {
        var buffer = Buffer;
        var end = Math.Min(pos, Filled - 3);
        var found = new Found(0, null, null);
        for (var p = _nextInsert; p < end; p++)
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(p));
            var h4 = (int)((value * 2654435761u) >> _hash4Shift);
            var h3 = (int)(((value & 0xFFFFFF) * 0x9E3779B1u) >> (32 - Hash3Bits));
            var stored = p + _offset;
            _head2[(int)(value & 0xFFFF)] = stored;
            _head3[h3] = stored;
            var candidate = _head4[h4];
            _head4[h4] = stored;
            if (_tree)
                TreeWalk(p, stored, candidate, ref found);
            else
                _links[stored & _mask] = candidate;
        }
        _nextInsert = pos;
    }

    /// <summary>The search thread and the batches of match lists it hands over.</summary>
    private sealed class Ahead
    {
        private const int BatchSize = 4096;
        private const int Batches = 8;

        private sealed class Batch
        {
            public int First;
            public readonly int[] Offsets = new int[BatchSize];
            public int[] Data = new int[BatchSize * 4]; // per position: count, then length and distance per match
        }

        private readonly BlockingCollection<Batch> _free = new();
        private readonly BlockingCollection<Batch> _full = new();
        private readonly CancellationTokenSource _stop;
        private readonly Task _producer;
        private ExceptionDispatchInfo? _error;
        private Batch? _current;

        public Ahead(MatchFinder mf, int start, int end, CancellationToken cancellationToken)
        {
            for (var i = 0; i < Batches; i++)
                _free.Add(new Batch());
            _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _stop.Token;
            _producer = Task.Factory.StartNew(() => Produce(mf, start, end, token), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private void Produce(MatchFinder mf, int start, int end, CancellationToken token)
        {
            try
            {
                var lengths = new int[LzmaModel.MatchMaxLength + 1];
                var distances = new uint[LzmaModel.MatchMaxLength + 1];
                for (var first = start; first < end; first += BatchSize)
                {
                    var batch = _free.Take(token);
                    batch.First = first;
                    var at = 0;
                    var last = Math.Min(end, first + BatchSize);
                    for (var p = first; p < last; p++)
                    {
                        batch.Offsets[p - first] = at;
                        var count = mf.Search(p, Math.Min(LzmaModel.MatchMaxLength, end - p), lengths, distances, out _);
                        if (at + 1 + 2 * count > batch.Data.Length)
                            Array.Resize(ref batch.Data, Math.Max(batch.Data.Length * 2, at + 1 + 2 * count));
                        var data = batch.Data;
                        data[at++] = count;
                        for (var k = 0; k < count; k++)
                        {
                            data[at++] = lengths[k];
                            data[at++] = (int)distances[k];
                        }
                    }
                    _full.Add(batch, token);
                }
            }
            catch (Exception ex)
            {
                _error = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                _full.CompleteAdding();
            }
        }

        /// <summary>The matches at <paramref name="pos"/> (positions come in order), cut to <paramref name="maxLength"/>.</summary>
        public int Take(int pos, int maxLength, int[] lengths, uint[] distances)
        {
            while (_current is null || pos >= _current.First + BatchSize)
            {
                if (_current is not null)
                    _free.Add(_current);
                _current = null;
                if (!_full.TryTake(out var next, Timeout.Infinite))
                {
                    _error?.Throw();
                    throw new InvalidOperationException("a position past the input was searched");
                }
                _current = next;
            }
            if (maxLength < 2)
                return 0;
            var data = _current.Data;
            var at = _current.Offsets[pos - _current.First];
            var count = data[at++];
            var result = 0;
            for (var k = 0; k < count; k++, at += 2)
            {
                // The lengths grow: the first match that reaches the limit ends the list, cut to the limit.
                var length = data[at];
                lengths[result] = Math.Min(length, maxLength);
                distances[result] = (uint)data[at + 1];
                result++;
                if (length >= maxLength)
                    break;
            }
            return result;
        }

        public void Stop()
        {
            _stop.Cancel();
            try
            {
                _producer.Wait();
            }
            catch (AggregateException)
            {
                // The producer catches everything.
            }
            _stop.Dispose();
            _free.Dispose();
            _full.Dispose();
        }
    }
}
