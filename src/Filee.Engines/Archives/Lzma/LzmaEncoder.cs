// LZMA encoding: the decoder's model run the other way (LzmaModel), with a parser that chooses, at each position,
// between a literal, a short repeat (one byte from the last distance), a repeat of one of the last four distances
// and a new match from the MatchFinder.
//
// Two parsers:
// - Lazy (the fast level): take the longest match, unless the next position starts a better one (longer, or as long
//   and nearer), in which case a literal goes first. A repeat is preferred over a slightly longer new match, since
//   it needs no distance, and a match one shorter is preferred when it is far nearer; literal against short repeat
//   is decided by their bit prices.
// - Price-based (normal and maximum): a shortest-path search over the next positions. Every position in a window
//   gets all its matches (one per length, the nearest distance), its four repeats, a literal and a short repeat;
//   each choice is priced in 1/16 bits with the current probabilities and the cheapest way to reach every later
//   position is kept, along with the state and distances that path leaves. The window ends where no choice reaches
//   past the position or where a match of the nice length starts, and the cheapest path to its end is coded. At its
//   size limit it ends instead at the node (of those its choices reach) that is cheapest for the bytes it covers, so
//   a match across the limit isn't cut short. Prices are refreshed from the probabilities every few KB.
//   (Pricing a match, the literal that ends it and the same distance again as one choice was tried: 0.05 % smaller
//   x86 code, 0.02 % larger text, 3 to 7 % slower; left out.)

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Filee.Engines.Archives.Lzma;

/// <summary>How hard the encoder looks for matches.</summary>
/// <param name="Depth">Candidates a match search looks at.</param>
/// <param name="Nice">A match this long is taken at once.</param>
/// <param name="Optimal">The price-based parse instead of the greedy one.</param>
/// <param name="Tree">A binary-tree match finder instead of a hash chain.</param>
internal readonly record struct LzmaEncoderSettings(int Depth, int Nice, bool Optimal, bool Tree);

internal sealed class LzmaEncoder : LzmaModel
{
    private readonly MatchFinder _mf;
    private readonly LzmaEncoderSettings _settings;
    private long _total; // bytes encoded since the dictionary reset: the position LZMA's contexts use

    // A position already searched by the price-based parse, whose long match is coded by the next call.
    private int _cachedAt = -1;
    private int _cachedLength;
    private uint _cachedDistance;

    // The lazy parse's look at the next position: its matches, kept for the next call when a literal went first.
    private int _nextAt = -1;
    private int _nextCount;
    private readonly int[] _nextLengths = new int[MatchMaxLength + 1];
    private readonly uint[] _nextDistances = new uint[MatchMaxLength + 1];

    // The price-based parser: matches of the current position, the path nodes, and price tables.
    private const int OptimalWindow = 2048;
    private const int Nodes = OptimalWindow + MatchMaxLength + 1; // a choice reaches up to 273 past the window
    private const int LengthSymbols = MatchMaxLength - MatchMinLength + 1;
    private const uint LiteralBack = uint.MaxValue;
    private const int PriceRefresh = 4096;

    // The most a parse window's symbols can take per position they cover: every probability stays at least 31/2048,
    // so a decision costs at most 6.05 bits, and the worst is a two-byte match at the longest distance (16 decisions
    // and 26 direct bits, 15.3 bytes); a literal takes at most 6.8 bytes. Rounded up. The callers' limits leave a
    // margin below what they can hold (LZMA2: 64 bytes under a chunk's 64 KiB).
    private const int WorstBytes = 8;
    private readonly int[] _lengths = new int[MatchMaxLength + 1];
    private readonly uint[] _distances = new uint[MatchMaxLength + 1];
    private readonly int[] _repLengths = new int[4];
    private readonly Node[] _nodes = new Node[Nodes];
    private readonly NodeState[] _nodeStates = new NodeState[Nodes];
    private readonly int[] _path = new int[Nodes];
    private readonly int[] _matchLengthPrices = new int[MaxPosStates * LengthSymbols];
    private readonly int[] _repLengthPrices = new int[MaxPosStates * LengthSymbols];
    private readonly int[] _slotPrices = new int[LengthStates << PosSlotBits];
    private readonly int[] _distancePrices = new int[LengthStates * FullDistances];
    private readonly int[] _alignPrices = new int[1 << AlignBits];
    private long _pricedAt = -1;

    public LzmaEncoder(MatchFinder mf, LzmaProperties properties, LzmaEncoderSettings settings)
    {
        _mf = mf;
        _settings = settings;
        SetProperties(properties);
        ResetState();
    }

    /// <summary>
    /// Codes symbols from the cursor until <paramref name="end"/> (no match crosses it) or until the coder's output
    /// would pass <paramref name="packedLimit"/> bytes. With <paramref name="wholeWindows"/>, the price-based parse
    /// stops instead where a window of its full size might no longer fit (see <see cref="WorstBytes"/>): ending an
    /// LZMA2 chunk early costs a few bytes, a short window costs more.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Encode(RangeEncoder rc, int end, long packedLimit, bool wholeWindows = false)
    {
        while (_mf.Position < end && rc.FlushedSize < packedLimit)
        {
            if (_settings.Optimal)
            {
                if (wholeWindows && packedLimit - rc.FlushedSize < Nodes * WorstBytes)
                    return;
                EncodeOptimal(rc, end, packedLimit);
            }
            else
            {
                EncodeOne(rc, end);
            }
        }
    }

    public override void ResetState()
    {
        base.ResetState();
        _pricedAt = -1;
    }

    /// <summary>Starts over for a new block (after a dictionary reset): state, position and the cached search.</summary>
    public void Restart()
    {
        ResetState();
        _total = 0;
        _cachedAt = -1;
        _nextAt = -1;
    }

    /// <summary>The window moved back by <paramref name="shift"/> bytes (a slide); the cached search moves with it.</summary>
    public void Shifted(int shift)
    {
        if (_cachedAt >= 0)
            _cachedAt -= shift;
        if (_nextAt >= 0)
            _nextAt -= shift;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EncodeOne(RangeEncoder rc, int end)
    {
        var buffer = _mf.Buffer;
        var pos = _mf.Position;
        var available = Math.Min(MatchMaxLength, end - pos);

        // This position's matches (searched by the last call when it looked ahead), each longer than the one before.
        int count;
        if (_nextAt == pos)
        {
            count = _nextCount;
            for (var k = 0; k < count; k++)
            {
                _lengths[k] = Math.Min(_nextLengths[k], available);
                _distances[k] = _nextDistances[k];
            }
        }
        else
        {
            count = _mf.FindAll(pos, available, _lengths, _distances);
        }
        _nextAt = -1;
        var mainLength = count > 0 ? _lengths[count - 1] : 0;
        var mainDistance = count > 0 ? _distances[count - 1] : 0;

        var repLength = LongestRep(buffer, pos, available, out var repIndex);
        if (repLength >= _settings.Nice)
        {
            EmitRep(rc, repIndex, repLength);
            return;
        }
        if (mainLength >= _settings.Nice)
        {
            EmitMatch(rc, mainLength, mainDistance);
            return;
        }

        // One byte shorter but far nearer is cheaper.
        while (count > 1 && _lengths[count - 2] + 1 == mainLength && FarNearer(_distances[count - 2], mainDistance))
        {
            count--;
            mainLength = _lengths[count - 1];
            mainDistance = _distances[count - 1];
        }

        // Short matches far back cost more than the literals they replace.
        if (mainLength == 2 && mainDistance >= 128 || mainLength == 3 && mainDistance >= 1 << 14)
            mainLength = 0;

        if (repLength >= 2 && (repLength + 1 >= mainLength
                               || repLength + 2 >= mainLength && mainDistance >= 1 << 9
                               || repLength + 3 >= mainLength && mainDistance >= 1 << 15))
        {
            EmitRep(rc, repIndex, repLength);
            return;
        }

        if (mainLength < 2)
        {
            EmitLiteralOrShortRep(rc);
            return;
        }

        // A look at the next position: a better match there (or a repeat nearly as long) makes this one a literal.
        if (available > 2)
        {
            var nextAvailable = Math.Min(MatchMaxLength, end - pos - 1);
            _nextCount = _mf.FindAll(pos + 1, nextAvailable, _nextLengths, _nextDistances);
            _nextAt = pos + 1;
            if (_nextCount > 0)
            {
                var nextLength = _nextLengths[_nextCount - 1];
                var nextDistance = _nextDistances[_nextCount - 1];
                if (nextLength >= mainLength && nextDistance < mainDistance
                    || nextLength == mainLength + 1 && !FarNearer(mainDistance, nextDistance)
                    || nextLength > mainLength + 1
                    || nextLength + 1 >= mainLength && mainLength >= 3 && FarNearer(nextDistance, mainDistance))
                {
                    EmitLiteral(rc);
                    return;
                }
            }
            if (RepReaches(buffer, pos + 1, nextAvailable, Math.Max(mainLength - 1, 2)))
            {
                EmitLiteral(rc);
                return;
            }
        }
        EmitMatch(rc, mainLength, mainDistance);
    }

    /// <summary>True when the distance <paramref name="near"/> is less than 1/128 of <paramref name="far"/>.</summary>
    private static bool FarNearer(uint near, uint far) => far >> 7 > near;

    /// <summary>The longest repeat of one of the last four distances at <paramref name="pos"/> (0 when none of 2 or more).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int LongestRep(byte[] buffer, int pos, int available, out int repIndex)
    {
        repIndex = 0;
        var repLength = 0;
        if (available < 2)
            return 0;
        for (var i = 0; i < 4; i++)
        {
            var distance = Rep(i);
            if (distance >= _total)
                continue;
            var from = pos - (int)distance - 1;
            if (buffer[from] != buffer[pos] || buffer[from + 1] != buffer[pos + 1])
                continue;
            var length = MatchFinder.Common(buffer, pos, from, available);
            if (length > repLength)
            {
                repLength = length;
                repIndex = i;
            }
        }
        return repLength;
    }

    /// <summary>True when one of the last four distances repeats at least <paramref name="length"/> bytes at <paramref name="pos"/> (one byte past the cursor).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool RepReaches(byte[] buffer, int pos, int available, int length)
    {
        if (available < length)
            return false;
        for (var i = 0; i < 4; i++)
        {
            var distance = Rep(i);
            if (distance > _total)
                continue;
            var from = pos - (int)distance - 1;
            if (buffer[from] == buffer[pos] && buffer[from + 1] == buffer[pos + 1]
                && MatchFinder.Common(buffer, pos, from, length) == length)
                return true;
        }
        return false;
    }

    // ───────── Price-based parse ─────────

    /// <summary>
    /// The cheapest way found to reach a position of the window: its price, the node it comes from, and the symbol
    /// (length 1 with <see cref="LiteralBack"/>: a literal, length 1 otherwise: a short repeat; back below 4: that
    /// repeat; else a match at distance back − 4). Kept together, as the relaxing loops touch all four.
    /// </summary>
    private struct Node
    {
        public int Price;
        public int Prev;
        public int Length;
        public uint Back;
    }

    /// <summary>The state and the four distances the cheapest path to a node leaves.</summary>
    private struct NodeState
    {
        public int State;
        public uint Rep0, Rep1, Rep2, Rep3;
    }

    private void EncodeOptimal(RangeEncoder rc, int end, long packedLimit)
    {
        var buffer = _mf.Buffer;
        var start = _mf.Position;
        if (_pricedAt < 0 || _total - _pricedAt >= PriceRefresh)
            RefreshPrices();

        // A window codes at most WorstBytes per position it covers (a byte more at the very end), so it stays within
        // the packed limit: with room for all the nodes, a full window that may end past its size limit; with less,
        // as many positions as fit.
        var room = (packedLimit - rc.FlushedSize) / WorstBytes;
        var overrun = room >= Nodes;
        var window = (int)Math.Clamp(room, 1, OptimalWindow);
        // Every node a choice reaches lies below OptimalWindow + MatchMaxLength + 1 = Nodes: choices start before the
        // window's end (i < window <= OptimalWindow) and none is longer than MatchMaxLength. The loops below index
        // the nodes through references, without checks.
        ref var nodes = ref MemoryMarshal.GetArrayDataReference(_nodes);
        ref var states = ref MemoryMarshal.GetArrayDataReference(_nodeStates);
        nodes.Price = 0;
        states = new NodeState { State = State, Rep0 = Rep0, Rep1 = Rep1, Rep2 = Rep2, Rep3 = Rep3 };
        var furthest = 0;
        int last;
        for (var i = 0; ; i++)
        {
            if (i > 0)
            {
                if (i >= furthest || i >= window)
                {
                    last = i < furthest && overrun ? BestEnd(i, furthest) : i;
                    break;
                }
                FollowNode(i);
            }
            var cur = start + i;
            var available = Math.Min(MatchMaxLength, end - cur);
            int count;
            if (_cachedAt == cur)
            {
                _lengths[0] = Math.Min(_cachedLength, available);
                _distances[0] = _cachedDistance;
                count = _lengths[0] >= 2 ? 1 : 0;
                _cachedAt = -1;
            }
            else
            {
                count = _mf.FindAll(cur, available, _lengths, _distances);
            }
            var longest = count > 0 ? _lengths[count - 1] : 0;

            var total = _total + i;
            ref var at = ref Unsafe.Add(ref states, i);
            var state = at.State;
            var bestRep = 0;
            var bestRepIndex = 0;
            for (var r = 0; r < 4; r++)
            {
                _repLengths[r] = 0;
                var distance = r switch { 0 => at.Rep0, 1 => at.Rep1, 2 => at.Rep2, _ => at.Rep3 };
                if (available < 2 || distance >= total)
                    continue;
                var from = cur - (int)distance - 1;
                if (buffer[from] != buffer[cur] || buffer[from + 1] != buffer[cur + 1])
                    continue;
                var length = MatchFinder.Common(buffer, cur, from, available);
                _repLengths[r] = length;
                if (length > bestRep)
                {
                    bestRep = length;
                    bestRepIndex = r;
                }
            }

            if (bestRep >= _settings.Nice || longest >= _settings.Nice)
            {
                if (i == 0)
                {
                    if (bestRep >= _settings.Nice)
                        EmitRep(rc, bestRepIndex, bestRep);
                    else
                        EmitMatch(rc, longest, _distances[count - 1]);
                    return;
                }
                // Code the path up to here; the long match is taken by the next call (the position is searched).
                _cachedAt = cur;
                _cachedLength = longest;
                _cachedDistance = count > 0 ? _distances[count - 1] : 0;
                last = i;
                break;
            }

            var rep0 = at.Rep0;
            var shortRep = rep0 < total && buffer[cur - (int)rep0 - 1] == buffer[cur];
            if (i == 0 && count == 0 && bestRep < 2 && !shortRep)
            {
                EmitLiteral(rc); // nothing but a literal reaches past the cursor
                return;
            }

            var reach = i + Math.Max(1, Math.Max(longest, bestRep));
            for (; furthest < reach; furthest++)
                Unsafe.Add(ref nodes, furthest + 1).Price = int.MaxValue;

            ref var here = ref Unsafe.Add(ref nodes, i);
            var price = here.Price;
            var posState = (int)total & PbMask;
            var state2 = (state << 4) + posState;
            // The literal's own price is only worked out when the bit before it leaves it a chance (prices are never
            // negative).
            var literalPrice = price + BitPrice.Of(IsMatch[state2], 0);
            if (literalPrice < Unsafe.Add(ref here, 1).Price)
                Relax(ref Unsafe.Add(ref here, 1), literalPrice + LiteralPrice(cur, total, state, rep0), i, 1, LiteralBack);

            var matchPrice = price + BitPrice.Of(IsMatch[state2], 1);
            var repPrice = matchPrice + BitPrice.Of(IsRep[state], 1);
            if (shortRep)
                Relax(ref Unsafe.Add(ref here, 1), repPrice + BitPrice.Of(IsRepG0[state], 0) + BitPrice.Of(IsRep0Long[state2], 0), i, 1, 0);
            for (var r = 0; r < 4; r++)
            {
                var length = _repLengths[r];
                if (length < 2)
                    continue;
                var basePrice = repPrice + RepIndexPrice(r, state, state2);
                // Indexed by length (2 … 273, below LengthSymbols + MatchMinLength).
                ref var prices = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_repLengthPrices), posState * LengthSymbols - MatchMinLength);
                for (var l = MatchMinLength; l <= length; l++)
                    Relax(ref Unsafe.Add(ref here, l), basePrice + Unsafe.Add(ref prices, l), i, l, (uint)r);
            }
            if (count > 0)
            {
                var basePrice = matchPrice + BitPrice.Of(IsRep[state], 0);
                ref var prices = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_matchLengthPrices), posState * LengthSymbols - MatchMinLength);
                // Lengths the last distance repeats as far are cheaper as a repeat; start past them.
                var l = Math.Max(MatchMinLength, _repLengths[0] + 1);
                var k = 0;
                while (k < count && _lengths[k] < l)
                    k++;
                for (; k < count; k++)
                {
                    var distance = _distances[k];
                    var back = distance + 4;
                    var upTo = _lengths[k];
                    // The distance is priced by length state: lengths 2, 3, 4 and 5 or more.
                    for (; l <= upTo && l <= 4; l++)
                        Relax(ref Unsafe.Add(ref here, l), basePrice + Unsafe.Add(ref prices, l) + DistancePrice(distance, l), i, l, back);
                    if (l > upTo)
                        continue;
                    var withDistance = basePrice + DistancePrice(distance, 5);
                    for (; l <= upTo; l++)
                    {
                        ref var node = ref Unsafe.Add(ref here, l);
                        var candidatePrice = withDistance + Unsafe.Add(ref prices, l);
                        if (candidatePrice < node.Price)
                        {
                            node.Price = candidatePrice;
                            node.Prev = i;
                            node.Length = l;
                            node.Back = back;
                        }
                    }
                }
            }
        }

        // Walk the cheapest path back from the end of the window, then code it forwards.
        var steps = 0;
        for (var j = last; j > 0; j = _nodes[j].Prev)
            _path[steps++] = j;
        while (steps-- > 0)
        {
            var node = _nodes[_path[steps]];
            var length = node.Length;
            var back = node.Back;
            if (length == 1)
            {
                if (back == LiteralBack)
                    EmitLiteral(rc);
                else
                    EmitShortRep(rc);
            }
            else if (back < 4)
            {
                EmitRep(rc, (int)back, length);
            }
            else
            {
                EmitMatch(rc, length, back - 4);
            }
        }
    }

    /// <summary>
    /// Where a window cut at <paramref name="cut"/> (choices from before it reach on to <paramref name="furthest"/>)
    /// had best end: at the node whose price, less what the bytes after the cut cost at the window's average so far,
    /// is lowest. A long match the cut would break ends past it that way, instead of being shortened.
    /// </summary>
    private int BestEnd(int cut, int furthest)
    {
        var perByte = (long)_nodes[cut].Price / cut;
        var best = cut;
        long bestScore = _nodes[cut].Price;
        for (var j = cut + 1; j <= furthest; j++)
        {
            var nodePrice = _nodes[j].Price;
            var score = nodePrice - perByte * (j - cut);
            if (nodePrice != int.MaxValue && score < bestScore)
            {
                best = j;
                bestScore = score;
            }
        }
        return best;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Relax(ref Node node, int price, int from, int length, uint back)
    {
        if (price < node.Price)
        {
            node.Price = price;
            node.Prev = from;
            node.Length = length;
            node.Back = back;
        }
    }

    /// <summary>The state and distances the cheapest path to <paramref name="index"/> leaves.</summary>
    private void FollowNode(int index)
    {
        var node = _nodes[index];
        var s = _nodeStates[node.Prev];
        if (node.Length == 1)
        {
            s.State = node.Back == LiteralBack ? AfterLiteral(s.State) : AfterShortRep(s.State);
        }
        else if (node.Back < 4)
        {
            switch (node.Back)
            {
                case 1:
                    (s.Rep0, s.Rep1) = (s.Rep1, s.Rep0);
                    break;
                case 2:
                    (s.Rep0, s.Rep1, s.Rep2) = (s.Rep2, s.Rep0, s.Rep1);
                    break;
                case 3:
                    (s.Rep0, s.Rep1, s.Rep2, s.Rep3) = (s.Rep3, s.Rep0, s.Rep1, s.Rep2);
                    break;
            }
            s.State = AfterRep(s.State);
        }
        else
        {
            s.Rep3 = s.Rep2;
            s.Rep2 = s.Rep1;
            s.Rep1 = s.Rep0;
            s.Rep0 = node.Back - 4;
            s.State = AfterMatch(s.State);
        }
        _nodeStates[index] = s;
    }

    private int RepIndexPrice(int index, int state, int state2)
    {
        if (index == 0)
            return BitPrice.Of(IsRepG0[state], 0) + BitPrice.Of(IsRep0Long[state2], 1);
        var price = BitPrice.Of(IsRepG0[state], 1);
        if (index == 1)
            return price + BitPrice.Of(IsRepG1[state], 0);
        return price + BitPrice.Of(IsRepG1[state], 1) + BitPrice.Of(IsRepG2[state], index - 2);
    }

    private int DistancePrice(uint distance, int length)
    {
        var lengthState = Math.Min(length - MatchMinLength, LengthStates - 1);
        return distance < FullDistances
            ? _distancePrices[lengthState * FullDistances + (int)distance]
            : _slotPrices[(lengthState << PosSlotBits) + (int)Slot(distance)] + _alignPrices[distance & ((1 << AlignBits) - 1)];
    }

    /// <summary>Recomputes the length and distance price tables from the current probabilities.</summary>
    private void RefreshPrices()
    {
        _pricedAt = _total;
        MatchLength.AllPrices(PbMask + 1, LengthSymbols, _matchLengthPrices);
        RepLength.AllPrices(PbMask + 1, LengthSymbols, _repLengthPrices);
        for (var lengthState = 0; lengthState < LengthStates; lengthState++)
        {
            var slotPrices = _slotPrices.AsSpan(lengthState << PosSlotBits, 1 << PosSlotBits);
            BitPrice.AllOfTree(PosSlot.AsSpan(lengthState << PosSlotBits, 1 << PosSlotBits), PosSlotBits, slotPrices);
            for (var slot = EndPosModelIndex; slot < 1 << PosSlotBits; slot++)
                slotPrices[slot] += 16 * ((slot >> 1) - 1 - AlignBits); // direct bits: one bit (16 / 16) each
            for (uint distance = 0; distance < FullDistances; distance++)
            {
                var slot = Slot(distance);
                var price = _slotPrices[(lengthState << PosSlotBits) + (int)slot];
                if (slot >= StartPosModelIndex)
                {
                    var footerBits = (int)(slot >> 1) - 1;
                    var baseDistance = (2 | (slot & 1)) << footerBits;
                    price += BitPrice.ReverseTree(PosSpecial.AsSpan((int)(baseDistance - slot)), footerBits, distance - baseDistance);
                }
                _distancePrices[lengthState * FullDistances + (int)distance] = price;
            }
        }
        for (uint i = 0; i < 1 << AlignBits; i++)
            _alignPrices[i] = BitPrice.ReverseTree(Align, AlignBits, i);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint Rep(int index) => index switch { 0 => Rep0, 1 => Rep1, 2 => Rep2, _ => Rep3 };

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void EmitLiteralOrShortRep(RangeEncoder rc)
    {
        var buffer = _mf.Buffer;
        var pos = _mf.Position;
        if (Rep0 < _total && buffer[pos - (int)Rep0 - 1] == buffer[pos])
        {
            var posState = (int)_total & PbMask;
            var state2 = (State << 4) + posState;
            var shortRep = BitPrice.Of(IsMatch[state2], 1) + BitPrice.Of(IsRep[State], 1)
                           + BitPrice.Of(IsRepG0[State], 0) + BitPrice.Of(IsRep0Long[state2], 0);
            var literal = BitPrice.Of(IsMatch[state2], 0) + LiteralPrice(pos, _total, State, Rep0);
            if (shortRep < literal)
            {
                EmitShortRep(rc);
                return;
            }
        }
        EmitLiteral(rc);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void EmitShortRep(RangeEncoder rc)
    {
        var state2 = (State << 4) + ((int)_total & PbMask);
        rc.EncodeBit(ref IsMatch[state2], 1);
        rc.EncodeBit(ref IsRep[State], 1);
        rc.EncodeBit(ref IsRepG0[State], 0);
        rc.EncodeBit(ref IsRep0Long[state2], 0);
        State = AfterShortRep(State);
        Advance(1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EmitLiteral(RangeEncoder rc)
    {
        var buffer = _mf.Buffer;
        var pos = _mf.Position;
        var posState = (int)_total & PbMask;
        rc.EncodeBit(ref IsMatch[(State << 4) + posState], 0);
        ref var probs = ref Literal.AsSpan(LiteralBase(_total, _total > 0 ? buffer[pos - 1] : 0), 0x300)[0];
        uint symbol = buffer[pos];
        if (State >= LiteralStates)
            rc.EncodeMatchedLiteral(ref probs, symbol, buffer[pos - (int)Rep0 - 1]);
        else
            rc.EncodeLiteral(ref probs, symbol);
        State = AfterLiteral(State);
        Advance(1);
    }

    private int LiteralPrice(int pos, long total, int state, uint rep0)
    {
        var buffer = _mf.Buffer;
        var probs = Literal.AsSpan(LiteralBase(total, total > 0 ? buffer[pos - 1] : 0), 0x300);
        uint symbol = buffer[pos];
        if (state < LiteralStates)
            return BitPrice.Tree(probs, 8, symbol);
        uint matchByte = buffer[pos - (int)rep0 - 1];
        var price = 0;
        uint m = 1;
        var same = true;
        for (var i = 7; i >= 0; i--)
        {
            var bit = (int)((symbol >> i) & 1);
            if (same)
            {
                var matchBit = (matchByte >> i) & 1;
                price += BitPrice.Of(probs[(int)(((1 + matchBit) << 8) + m)], bit);
                same = matchBit == (uint)bit;
            }
            else
            {
                price += BitPrice.Of(probs[(int)m], bit);
            }
            m = (m << 1) | (uint)bit;
        }
        return price;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void EmitMatch(RangeEncoder rc, int length, uint distance)
    {
        var posState = (int)_total & PbMask;
        rc.EncodeBit(ref IsMatch[(State << 4) + posState], 1);
        rc.EncodeBit(ref IsRep[State], 0);
        var length0 = (uint)(length - MatchMinLength);
        MatchLength.Encode(rc, length0, posState);
        EncodeDistance(rc, distance, length0);
        Rep3 = Rep2;
        Rep2 = Rep1;
        Rep1 = Rep0;
        Rep0 = distance;
        State = AfterMatch(State);
        Advance(length);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void EmitRep(RangeEncoder rc, int index, int length)
    {
        var posState = (int)_total & PbMask;
        var state2 = (State << 4) + posState;
        rc.EncodeBit(ref IsMatch[state2], 1);
        rc.EncodeBit(ref IsRep[State], 1);
        if (index == 0)
        {
            rc.EncodeBit(ref IsRepG0[State], 0);
            rc.EncodeBit(ref IsRep0Long[state2], 1);
        }
        else
        {
            rc.EncodeBit(ref IsRepG0[State], 1);
            uint distance;
            if (index == 1)
            {
                rc.EncodeBit(ref IsRepG1[State], 0);
                distance = Rep1;
            }
            else
            {
                rc.EncodeBit(ref IsRepG1[State], 1);
                rc.EncodeBit(ref IsRepG2[State], index - 2);
                if (index == 3)
                {
                    distance = Rep3;
                    Rep3 = Rep2;
                }
                else
                {
                    distance = Rep2;
                }
                Rep2 = Rep1;
            }
            Rep1 = Rep0;
            Rep0 = distance;
        }
        RepLength.Encode(rc, (uint)(length - MatchMinLength), posState);
        State = AfterRep(State);
        Advance(length);
    }

    private void EncodeDistance(RangeEncoder rc, uint distance, uint length0)
    {
        var lengthState = (int)Math.Min(length0, LengthStates - 1);
        var slot = Slot(distance);
        rc.EncodeTree(PosSlot.AsSpan(lengthState << PosSlotBits, 1 << PosSlotBits), PosSlotBits, slot);
        if (slot < StartPosModelIndex)
            return;
        var footerBits = (int)(slot >> 1) - 1;
        var baseDistance = (2 | (slot & 1)) << footerBits;
        var reduced = distance - baseDistance;
        if (slot < EndPosModelIndex)
        {
            rc.EncodeReverseTree(PosSpecial.AsSpan((int)(baseDistance - slot)), footerBits, reduced);
            return;
        }
        rc.EncodeDirectBits(reduced >> AlignBits, footerBits - AlignBits);
        rc.EncodeReverseTree(Align, AlignBits, reduced & ((1 << AlignBits) - 1));
    }

    /// <summary>The distance slot: the position of the top bit and the bit below it.</summary>
    private static uint Slot(uint distance)
    {
        if (distance < 4)
            return distance;
        var top = 31 - BitOperations.LeadingZeroCount(distance);
        return (uint)(top << 1) | ((distance >> (top - 1)) & 1);
    }

    private void Advance(int length)
    {
        _total += length;
        _mf.Position += length;
        _mf.SkipTo(_mf.Position);
    }
}
