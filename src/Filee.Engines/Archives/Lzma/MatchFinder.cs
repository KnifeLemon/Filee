// The encoder's match finder. Every position is entered under a hash of its next four bytes, in one of two ways:
// - Hash chain (fast level): each position links to the previous one with the same hash, and a search walks the
//   candidates newest first, up to a depth the level sets.
// - Binary tree (normal and maximum): the positions with the same hash form a search tree ordered by the bytes that
//   follow them, newest at the root. A search walks down from the root comparing the bytes at the cursor, and
//   rebuilds the tree on the way with the cursor as the new root (everything smaller to its left, larger to its
//   right). Bytes already known to match on both sides are not compared again, so the longest matches are found in
//   far fewer steps than a chain needs; inserting a position costs the same walk.
// Two chainless tables keyed by the next two and three bytes catch close short matches either way (the two-byte one
// a small hash of them with the chain, so the fast level's tables stay in the cache).
//
// The window is one array: the dictionary behind the cursor plus room ahead for new input, slid back when the room
// is used up. Positions are stored as buffer index + an offset that grows with each slide, so the tables never need
// rewriting when the window moves; only every ~1 GiB of input are they rebased (Normalize). 0 marks an empty slot.
//
// Searching ahead (binary tree, for the price-based parse, with the whole input in the buffer): threads of their own
// search every position in order, as the parse would search it or step over it (both walk the tree the same way),
// and hand the matches over in batches; the parse takes each position's list cut to the length it allows there.
// A position's tree holds only positions with the same four-byte hash, so the trees are split between the threads
// by hash: each thread walks the positions of its own hashes, in order, and touches no other tree. The last thread
// to finish a batch adds the close matches of the two- and three-byte tables (kept in order, by one thread at a
// time) and joins them with the tree's as one search would. The lists are exactly those a search in the parse would
// find, so the output is the same bytes whatever the number of threads, or with none.

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace Filee.Engines.Archives.Lzma;

internal sealed class MatchFinder
{
    private const int Hash3Bits = 16;
    private const int MinAhead = 1 << 16;

    /// <summary>The most threads <see cref="StartAhead"/> searches on.</summary>
    public const int MaxSearchThreads = Ahead.MaxThreads;

    /// <summary>Tests lower the offset at which positions are rebased (normally 1 GiB).</summary>
    internal static readonly AsyncLocal<int?> TestNormalizeAt = new();

    private readonly int _dictionary;
    private readonly int _reach; // how far back the tree walks: the dictionary less the search threads' leeway
    private readonly int _mask;
    private readonly bool _tree;
    private readonly int[] _links; // chain: one previous position per slot; tree: left and right child per slot
    private readonly int[] _head4;
    private readonly int[] _head3 = new int[1 << Hash3Bits];
    // Two-byte table: the bytes themselves with the tree (every pair its slot); a 10-bit hash of them with the chain,
    // whose small dictionary and many threads want tables that stay in the cache.
    private const int ChainHash2Bits = 10;
    private readonly int[] _head2;
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
        // A tree slot is reused a dictionary later, possibly by another search thread (they split the trees by hash
        // and may be up to Ahead.MaxLag positions apart): walks stop that far short of the dictionary, so no thread
        // reads a slot another has taken over. The same with one thread or none, so the bytes don't depend on it.
        _reach = tree ? dictionary - Math.Min(Ahead.MaxLag, dictionary >> 2) : dictionary;
        _mask = dictionary - 1;
        _tree = tree;
        _links = new int[tree ? 2L * dictionary : dictionary];
        _head2 = new int[tree ? 1 << 16 : 1 << ChainHash2Bits];
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
        return 4L * ((tree ? 2L : 1L) * dictionary + (1 << hashBits) + (1 << Hash3Bits) + (tree ? 1 << 16 : 1 << ChainHash2Bits));
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
    /// Enters <paramref name="pos"/> in the tables and lists its matches of at most <paramref name="maxLength"/>
    /// bytes, each longer than the one before; returns how many.
    /// </summary>
    public int FindAll(int pos, int maxLength, int[] lengths, uint[] distances) =>
        _ahead is { } ahead ? ahead.Take(pos, maxLength, lengths, distances)
        : _tree ? Search(pos, maxLength, lengths, distances, out _)
        : ChainSearch(pos, maxLength, lengths, distances);

    /// <summary>
    /// Starts searching every position from <see cref="Position"/> to <see cref="Filled"/> on
    /// <paramref name="threads"/> threads of their own (binary tree only); <see cref="FindAll"/> then takes their
    /// results and <see cref="SkipTo"/> does nothing. Call <see cref="StopAhead"/> when done.
    /// </summary>
    public void StartAhead(CancellationToken cancellationToken, int threads = 1)
    {
        if (!_tree)
            throw new InvalidOperationException("only the binary tree searches ahead");
        if (threads > 1 && _dictionary < 4 * Ahead.MaxLag)
            threads = 1; // the walks' leeway (a quarter of the dictionary at most) would not cover the threads' lag
        _ahead = new Ahead(this, Position, Filled, Math.Clamp(threads, 1, Ahead.MaxThreads), cancellationToken);
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

    /// <summary>The four-byte hash bucket of <paramref name="pos"/> (which has four bytes after it).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Hash4(int pos) => (int)((BinaryPrimitives.ReadUInt32LittleEndian(Buffer.AsSpan(pos)) * 2654435761u) >> _hash4Shift);

    /// <summary>
    /// The tree part of a search at <paramref name="pos"/> (which has four bytes after it): enters it in its tree
    /// and offers what the walk finds.
    /// </summary>
    private void TreeSearch(int pos, int h4, ref Found found)
    {
        var stored = pos + _offset;
        ref var slot4 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_head4), h4);
        var candidate = slot4;
        slot4 = stored;
        TreeWalk(pos, stored, candidate, ref found);
    }

    /// <summary>
    /// The two- and three-byte part of a search at <paramref name="pos"/> (which has four bytes after it): enters it
    /// in those tables and offers their candidates.
    /// </summary>
    private void NearSearch(int pos, int maxLength, ref Found found)
    {
        var buffer = Buffer;
        var value = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(pos));
        var stored = pos + _offset;
        var h2 = (int)(value & 0xFFFF);
        var h3 = (int)(((value & 0xFFFFFF) * 0x9E3779B1u) >> (32 - Hash3Bits));
        ref var slot2 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_head2), h2);
        ref var slot3 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_head3), h3);
        var candidate2 = slot2;
        slot2 = stored;
        var candidate3 = slot3;
        slot3 = stored;
        if (maxLength < 2)
            return;
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

    /// <summary>A search of the binary tree (the hash chain has <see cref="ChainSearch"/>).</summary>
    private int Search(int pos, int maxLength, int[]? lengths, uint[]? distances, out uint distance)
    {
        System.Diagnostics.Debug.Assert(_tree, "the chain searches with ChainSearch");
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
        // The hashes are below the tables' sizes by construction: 16 bits, Hash3Bits and the shift for _head4.
        ref var slot2 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_head2), h2);
        ref var slot3 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_head3), h3);
        ref var slot4 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_head4), h4);
        var candidate2 = slot2;
        slot2 = stored;
        var candidate3 = slot3;
        slot3 = stored;
        var candidate = slot4;
        slot4 = stored;

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

        distance = found.Distance;
        if (found.Best < 2)
            return 0;
        return lengths is null ? found.Best : found.Count;
    }

    /// <summary>
    /// <see cref="Search"/> for the hash chain (the fast level), on its own so its loop keeps its values in registers:
    /// the same tables, candidates and lists. A candidate is only measured when its two bytes ending at the best length
    /// so far agree with the cursor's (a match longer than the best has them), which takes one load per candidate.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int ChainSearch(int pos, int maxLength, int[] lengths, uint[] distances)
    {
        System.Diagnostics.Debug.Assert(pos >= _nextInsert, "a position is searched twice");
        SkipTo(pos);
        _nextInsert = pos + 1;
        var buffer = Buffer;
        if (pos + 4 > Filled || (uint)pos > (uint)(buffer.Length - Math.Max(4, maxLength)))
            return 0;

        ref var data = ref MemoryMarshal.GetArrayDataReference(buffer);
        ref var cursor = ref Unsafe.Add(ref data, pos);
        var value = Unsafe.ReadUnaligned<uint>(ref cursor);
        if (!BitConverter.IsLittleEndian)
            value = BinaryPrimitives.ReverseEndianness(value);
        var offset = _offset;
        var dictionary = _dictionary;
        var stored = pos + offset;
        // The hashes are below the tables' sizes by construction: 16 bits, Hash3Bits and the shift for _head4.
        ref var slot2 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_head2), ChainHash2(value));
        ref var slot3 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_head3), (int)(((value & 0xFFFFFF) * 0x9E3779B1u) >> (32 - Hash3Bits)));
        ref var slot4 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_head4), (int)((value * 2654435761u) >> _hash4Shift));
        var candidate2 = slot2;
        slot2 = stored;
        var candidate3 = slot3;
        slot3 = stored;
        var candidate = slot4;
        slot4 = stored;
        ref var links = ref MemoryMarshal.GetArrayDataReference(_links);
        var mask = _mask;
        Unsafe.Add(ref links, stored & mask) = candidate;
        // The next position is searched or entered right after this one: its table slots (mostly not in the cache)
        // are asked for now, so they arrive while this chain is walked.
        if (pos + 5 <= Filled)
            PrefetchSlots(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref cursor, 1)));

        // Candidates lie within the dictionary behind pos, so they and maxLength bytes after them fit the buffer.
        var best = 1;
        var count = 0;
        if (maxLength >= 2)
        {
            if (candidate2 > 0 && stored - candidate2 < dictionary && Unsafe.Add(ref data, candidate2 - offset) == cursor)
                Offer(Common(buffer, pos, candidate2 - offset, maxLength), stored - candidate2, ref best, ref count, lengths, distances);
            if (candidate3 > 0 && stored - candidate3 < dictionary && best < maxLength && Unsafe.Add(ref data, candidate3 - offset) == cursor)
                Offer(Common(buffer, pos, candidate3 - offset, maxLength), stored - candidate3, ref best, ref count, lengths, distances);
        }
        var stopAt = Math.Min(maxLength, _nice);
        for (var depth = _depth; candidate > 0 && depth > 0 && best < stopAt; depth--)
        {
            var gap = stored - candidate;
            if (gap <= 0 || gap >= dictionary)
                break;
            var at = candidate - offset;
            candidate = Unsafe.Add(ref links, candidate & mask);
            if (Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref data, at + best - 1)) != Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref cursor, best - 1)))
                continue;
            Offer(Common(buffer, pos, at, maxLength), gap, ref best, ref count, lengths, distances);
        }
        return best < 2 ? 0 : count;
    }

    /// <summary>
    /// A hint to load the three- and four-byte table slots of the position whose first bytes are
    /// <paramref name="value"/> (little-endian) into the cache (x86; elsewhere nothing). Nothing is read through the
    /// addresses.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void PrefetchSlots(uint value)
    {
        if (!Sse.IsSupported)
            return;
        if (!BitConverter.IsLittleEndian)
            value = BinaryPrimitives.ReverseEndianness(value);
        Sse.Prefetch0(Unsafe.AsPointer(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_head3), (int)(((value & 0xFFFFFF) * 0x9E3779B1u) >> (32 - Hash3Bits)))));
        Sse.Prefetch0(Unsafe.AsPointer(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_head4), (int)((value * 2654435761u) >> _hash4Shift))));
    }

    /// <summary>The chain's two-byte slot for the bytes in the low half of <paramref name="value"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ChainHash2(uint value) => (int)(((value & 0xFFFF) * 2654435761u) >> (32 - ChainHash2Bits));

    /// <summary><see cref="Found.Offer"/> on locals.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Offer(int length, int gap, ref int best, ref int count, int[] lengths, uint[] distances)
    {
        if (length <= best)
            return;
        best = length;
        lengths[count] = length;
        distances[count] = (uint)(gap - 1);
        count++;
    }

    /// <summary>
    /// Walks the tree of <paramref name="candidate"/> (the old root) for the position <paramref name="pos"/>, offering
    /// what matches, and makes <paramref name="pos"/> the root.
    /// </summary>
    private void TreeWalk(int pos, int stored, int candidate, ref Found found)
    {
        var buffer = Buffer;
        ref var data = ref MemoryMarshal.GetArrayDataReference(buffer);
        ref var links = ref MemoryMarshal.GetArrayDataReference(_links);
        var mask = _mask;
        var reach = _reach;
        var offset = _offset;
        var limit = Math.Min(_nice, Filled - pos);
        // Every candidate checked below lies within the dictionary behind pos, so it and the limit fit the buffer;
        // every slot is a node's (masked) or the cursor's. The checks stay, cheap next to the walk's cache misses.
        if ((uint)pos > (uint)(buffer.Length - limit) || limit < 0)
            throw new ArgumentOutOfRangeException(nameof(pos));
        ref var cursor = ref Unsafe.Add(ref data, pos);
        var leftSlot = (stored & mask) << 1; // where the next candidate smaller than pos goes
        var rightSlot = leftSlot + 1;        // where the next candidate larger than pos goes
        int leftLength = 0, rightLength = 0; // bytes known to match the bounds on either side
        for (var depth = _depth; ; depth--)
        {
            var gap = stored - candidate;
            var at = candidate - offset;
            if (candidate <= 0 || gap <= 0 || gap >= reach || depth == 0 || (uint)at > (uint)pos)
            {
                Unsafe.Add(ref links, leftSlot) = 0;
                Unsafe.Add(ref links, rightSlot) = 0;
                return;
            }
            ref var match = ref Unsafe.Add(ref data, at);
            var node = (candidate & mask) << 1;
            // Both children are read before the bytes are compared: the two loads (the node's and the candidate's
            // bytes, both mostly cache misses) then overlap instead of one waiting for the other.
            var smaller = Unsafe.Add(ref links, node);
            var larger = Unsafe.Add(ref links, node + 1);
            var length = Math.Min(leftLength, rightLength);
            // Most candidates differ at the first byte not known to match: only then is the rest compared.
            if (length < limit && Unsafe.Add(ref match, length) == Unsafe.Add(ref cursor, length))
            {
                length++;
                if (length < limit)
                    length += Common(buffer, pos + length, at + length, limit - length);
                found.Offer(length, gap);
            }
            if (length == limit)
            {
                // Equal as far as the tree looks: the candidate's subtrees become the new root's.
                Unsafe.Add(ref links, leftSlot) = smaller;
                Unsafe.Add(ref links, rightSlot) = larger;
                return;
            }
            if (Unsafe.Add(ref match, length) < Unsafe.Add(ref cursor, length))
            {
                Unsafe.Add(ref links, leftSlot) = candidate;
                leftSlot = node + 1;
                candidate = larger;
                leftLength = length;
            }
            else
            {
                Unsafe.Add(ref links, rightSlot) = candidate;
                rightSlot = node;
                candidate = smaller;
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
            if (!_tree && p + 8 < end)
                PrefetchSlots(BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(p + 4)));
            var value = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(p));
            var h4 = (int)((value * 2654435761u) >> _hash4Shift);
            var h3 = (int)(((value & 0xFFFFFF) * 0x9E3779B1u) >> (32 - Hash3Bits));
            var stored = p + _offset;
            Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_head2), _tree ? (int)(value & 0xFFFF) : ChainHash2(value)) = stored;
            Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_head3), h3) = stored;
            ref var slot4 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_head4), h4);
            var candidate = slot4;
            slot4 = stored;
            if (_tree)
                TreeWalk(p, stored, candidate, ref found);
            else
                _links[stored & _mask] = candidate;
        }
        _nextInsert = pos;
    }

    /// <summary>The search threads and the batches of match lists they hand over.</summary>
    private sealed class Ahead
    {
        /// <summary>The most tree threads one match finder starts.</summary>
        public const int MaxThreads = 4;

        private const int BatchSize = 4096;
        private const int Batches = 8;

        /// <summary>
        /// How far apart two search threads may be: a thread takes a batch only when the one eight before it was
        /// handed to the parse, after every thread finished it, so they are at most nine batches (36 Ki positions)
        /// apart.
        /// </summary>
        public const int MaxLag = 1 << 16;

        /// <summary>One thread's tree matches for the positions of a batch it owns: per position its offset in Data, or -1.</summary>
        private sealed class Part
        {
            public readonly int[] Offsets = new int[BatchSize];
            public int[] Data = new int[BatchSize * 4];
        }

        private sealed class Batch
        {
            public int First = -1; // -1: free
            public int Parts;      // tree threads done with it
            public bool Ready;
            public readonly int[] Offsets = new int[BatchSize];
            public int[] Data = new int[BatchSize * 4]; // per position: count, then length and distance per match
            public Part[] Trees = [];
        }

        private readonly MatchFinder _mf;
        private readonly int _start;
        private readonly int _end;
        private readonly int _threads;
        private readonly Batch[] _ring = new Batch[Batches];
        private readonly object _gate = new();
        private readonly CancellationTokenSource _stop;
        private readonly Task[] _producers;
        private readonly int[] _lengths = new int[LzmaModel.MatchMaxLength + 1];
        private readonly uint[] _distances = new uint[LzmaModel.MatchMaxLength + 1];
        private int _nextMerge; // the first batch (by number) not merged yet
        private int _taken;     // the first batch (by number) the parse has not handed back
        private ExceptionDispatchInfo? _error;
        private Batch? _current;

        public Ahead(MatchFinder mf, int start, int end, int threads, CancellationToken cancellationToken)
        {
            _mf = mf;
            _start = start;
            _end = end;
            _threads = threads;
            for (var i = 0; i < Batches; i++)
            {
                _ring[i] = new Batch { Trees = new Part[threads] };
                for (var k = 0; k < threads; k++)
                    _ring[i].Trees[k] = new Part();
            }
            _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _stop.Token;
            _producers = new Task[threads];
            for (var k = 0; k < threads; k++)
            {
                var part = k;
                _producers[k] = Task.Factory.StartNew(() => Produce(part, token), CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
        }

        private int BatchCount => (_end - _start + BatchSize - 1) / BatchSize;

        /// <summary>Thread <paramref name="part"/>: the tree searches of its hashes, batch by batch.</summary>
        private void Produce(int part, CancellationToken token)
        {
            try
            {
                var mf = _mf;
                var lengths = new int[LzmaModel.MatchMaxLength + 1];
                var distances = new uint[LzmaModel.MatchMaxLength + 1];
                for (var number = 0; number < BatchCount; number++)
                {
                    var batch = _ring[number % Batches];
                    lock (_gate)
                    {
                        // The slot is free once the parse handed back the batch eight before.
                        while (number - _taken >= Batches && _error is null)
                        {
                            token.ThrowIfCancellationRequested();
                            Monitor.Wait(_gate, 100);
                        }
                        if (_error is not null)
                            return;
                    }
                    var first = _start + number * BatchSize;
                    var last = Math.Min(_end, first + BatchSize);
                    var tree = batch.Trees[part];
                    var at = 0;
                    for (var p = first; p < last; p++)
                    {
                        var h4 = p + 4 <= mf.Filled ? mf.Hash4(p) : -1;
                        if (h4 < 0 || (_threads > 1 && h4 % _threads != part))
                        {
                            tree.Offsets[p - first] = -1;
                            continue;
                        }
                        var found = new Found(Math.Min(LzmaModel.MatchMaxLength, _end - p), lengths, distances);
                        mf.TreeSearch(p, h4, ref found);
                        if (at + 1 + 2 * found.Count > tree.Data.Length)
                            Array.Resize(ref tree.Data, Math.Max(tree.Data.Length * 2, at + 1 + 2 * found.Count));
                        tree.Offsets[p - first] = at;
                        var data = tree.Data;
                        data[at++] = found.Count;
                        for (var k = 0; k < found.Count; k++)
                        {
                            data[at++] = lengths[k];
                            data[at++] = (int)distances[k];
                        }
                    }
                    token.ThrowIfCancellationRequested();
                    bool merge;
                    lock (_gate)
                    {
                        if (batch.Parts == 0)
                            batch.First = first;
                        merge = ++batch.Parts == _threads;
                    }
                    if (merge)
                        MergeReady();
                }
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _error ??= ExceptionDispatchInfo.Capture(ex);
                    Monitor.PulseAll(_gate);
                }
            }
        }

        /// <summary>Joins the batches whose trees are all done, in order (one thread at a time).</summary>
        private void MergeReady()
        {
            lock (_mergeGate)
            {
                while (true)
                {
                    Batch batch;
                    lock (_gate)
                    {
                        if (_nextMerge >= BatchCount)
                            return;
                        batch = _ring[_nextMerge % Batches];
                        if (batch.Parts != _threads || batch.Ready)
                            return;
                    }
                    Merge(batch);
                    lock (_gate)
                    {
                        batch.Ready = true;
                        _nextMerge++;
                        Monitor.PulseAll(_gate);
                    }
                }
            }
        }

        private readonly object _mergeGate = new();

        /// <summary>Adds the close matches to every position of the batch and joins them with its tree's.</summary>
        private void Merge(Batch batch)
        {
            var mf = _mf;
            var buffer = mf.Buffer;
            var first = batch.First;
            var last = Math.Min(_end, first + BatchSize);
            var at = 0;
            for (var p = first; p < last; p++)
            {
                batch.Offsets[p - first] = at;
                var maxLength = Math.Min(LzmaModel.MatchMaxLength, _end - p);
                var found = new Found(maxLength, _lengths, _distances);
                var count = 0;
                if (p + 4 <= mf.Filled)
                {
                    mf.NearSearch(p, maxLength, ref found);
                    // The tree's matches longer than the close ones, as one search offers them after those.
                    var owner = _threads > 1 ? mf.Hash4(p) % _threads : 0;
                    var tree = batch.Trees[owner];
                    var from = tree.Offsets[p - first];
                    var treeData = tree.Data;
                    var treeCount = treeData[from++];
                    for (var k = 0; k < treeCount; k++, from += 2)
                        found.Offer(treeData[from], treeData[from + 1] + 1);
                    // The tree compares at most the nice length; a match that long may go on.
                    if (found.Best >= mf._nice && found.Best < maxLength)
                    {
                        var longer = Common(buffer, p, p - (int)found.Distance - 1, maxLength);
                        found.Best = longer;
                        _lengths[found.Count - 1] = longer;
                    }
                    count = found.Best < 2 ? 0 : found.Count;
                }
                if (at + 1 + 2 * count > batch.Data.Length)
                    Array.Resize(ref batch.Data, Math.Max(batch.Data.Length * 2, at + 1 + 2 * count));
                var data = batch.Data;
                data[at++] = count;
                for (var k = 0; k < count; k++)
                {
                    data[at++] = _lengths[k];
                    data[at++] = (int)_distances[k];
                }
            }
        }

        /// <summary>The matches at <paramref name="pos"/> (positions come in order), cut to <paramref name="maxLength"/>.</summary>
        public int Take(int pos, int maxLength, int[] lengths, uint[] distances)
        {
            while (_current is null || pos >= _current.First + BatchSize)
            {
                lock (_gate)
                {
                    if (_current is not null)
                    {
                        // Handed back: its slot takes a later batch.
                        _current.First = -1;
                        _current.Parts = 0;
                        _current.Ready = false;
                        _current = null;
                        _taken++;
                        Monitor.PulseAll(_gate);
                    }
                    if (_taken >= BatchCount)
                        throw new InvalidOperationException("a position past the input was searched");
                    var batch = _ring[_taken % Batches];
                    while (!batch.Ready && _error is null)
                        Monitor.Wait(_gate);
                    _error?.Throw();
                    _current = batch;
                }
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
            lock (_gate)
                Monitor.PulseAll(_gate);
            try
            {
                Task.WaitAll(_producers);
            }
            catch (AggregateException)
            {
                // The producers catch everything.
            }
            _stop.Dispose();
        }
    }
}
