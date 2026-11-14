// LZMA encoding: the decoder's model run the other way (LzmaModel), with a parser that chooses, at each position,
// between a literal, a short repeat (one byte from the last distance), a repeat of one of the last four distances
// and a new match from the MatchFinder.
//
// Two parsers:
// - Greedy (the fast level): take the longest match. A repeat is preferred over a slightly longer new match, since it
//   needs no distance; literal against short repeat is decided by their bit prices.
// - Price-based (normal and maximum): a shortest-path search over the next positions. Every position in a window
//   gets all its matches (one per length, the nearest distance), its four repeats, a literal and a short repeat;
//   each choice is priced in 1/16 bits with the current probabilities and the cheapest way to reach every later
//   position is kept, along with the state and distances that path leaves. The window ends where no choice reaches
//   past the position (or at its size limit, or where a match of the nice length starts), and the cheapest path to
//   its end is coded. Prices are refreshed from the probabilities every few KB.

using System.Numerics;

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

    // The price-based parser: matches of the current position, the path nodes, and price tables.
    private const int OptimalWindow = 2048;
    private const int LengthSymbols = MatchMaxLength - MatchMinLength + 1;
    private const uint LiteralBack = uint.MaxValue;
    private const int PriceRefresh = 4096;
    private readonly int[] _lengths = new int[MatchMaxLength + 1];
    private readonly uint[] _distances = new uint[MatchMaxLength + 1];
    private readonly int[] _repLengths = new int[4];
    private readonly int[] _nodePrice = new int[OptimalWindow + MatchMaxLength + 1];
    private readonly int[] _nodePrev = new int[OptimalWindow + MatchMaxLength + 1];
    private readonly int[] _nodeLength = new int[OptimalWindow + MatchMaxLength + 1];
    private readonly uint[] _nodeBack = new uint[OptimalWindow + MatchMaxLength + 1];
    private readonly int[] _nodeState = new int[OptimalWindow + MatchMaxLength + 1];
    private readonly uint[] _nodeReps = new uint[(OptimalWindow + MatchMaxLength + 1) * 4];
    private readonly int[] _path = new int[OptimalWindow + MatchMaxLength + 1];
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
    /// would pass <paramref name="packedLimit"/> bytes.
    /// </summary>
    public void Encode(RangeEncoder rc, int end, long packedLimit)
    {
        while (_mf.Position < end && rc.FlushedSize < packedLimit)
        {
            if (_settings.Optimal)
                EncodeOptimal(rc, end, packedLimit);
            else
                EncodeOne(rc, end);
        }
    }

    public override void ResetState()
    {
        base.ResetState();
        _pricedAt = -1;
    }

    /// <summary>The window moved back by <paramref name="shift"/> bytes (a slide); the cached search moves with it.</summary>
    public void Shifted(int shift)
    {
        if (_cachedAt >= 0)
            _cachedAt -= shift;
    }

    private void EncodeOne(RangeEncoder rc, int end)
    {
        var buffer = _mf.Buffer;
        var pos = _mf.Position;
        var available = Math.Min(MatchMaxLength, end - pos);

        var mainLength = _mf.Find(available, out var mainDistance);

        // The longest repeat of one of the last four distances.
        var repLength = 0;
        var repIndex = 0;
        if (available >= 2)
        {
            for (var i = 0; i < 4; i++)
            {
                var distance = Rep(i);
                if (distance >= _total)
                    continue;
                var from = pos - (int)distance - 1;
                if (buffer[from] != buffer[pos] || buffer[from + 1] != buffer[pos + 1])
                    continue;
                var length = buffer.AsSpan(pos, available).CommonPrefixLength(buffer.AsSpan(from, available));
                if (length > repLength)
                {
                    repLength = length;
                    repIndex = i;
                }
            }
        }

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

        if (mainLength >= 2)
            EmitMatch(rc, mainLength, mainDistance);
        else
            EmitLiteralOrShortRep(rc);
    }

    // ───────── Price-based parse ─────────

    private void EncodeOptimal(RangeEncoder rc, int end, long packedLimit)
    {
        var buffer = _mf.Buffer;
        var start = _mf.Position;
        if (_pricedAt < 0 || _total - _pricedAt >= PriceRefresh)
            RefreshPrices();

        // Each coded symbol takes at most ~25 bytes, so a window never overruns the chunk's packed limit.
        var window = (int)Math.Clamp((packedLimit - rc.FlushedSize) / 32, 1, OptimalWindow);
        _nodePrice[0] = 0;
        _nodeState[0] = State;
        _nodeReps[0] = Rep0;
        _nodeReps[1] = Rep1;
        _nodeReps[2] = Rep2;
        _nodeReps[3] = Rep3;
        var furthest = 0;
        int last;
        for (var i = 0; ; i++)
        {
            if (i > 0)
            {
                if (i >= furthest || i >= window)
                {
                    last = i;
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
            var state = _nodeState[i];
            var reps = _nodeReps.AsSpan(i * 4, 4);
            var bestRep = 0;
            var bestRepIndex = 0;
            for (var r = 0; r < 4; r++)
            {
                _repLengths[r] = 0;
                var distance = reps[r];
                if (available < 2 || distance >= total)
                    continue;
                var from = cur - (int)distance - 1;
                if (buffer[from] != buffer[cur] || buffer[from + 1] != buffer[cur + 1])
                    continue;
                var length = buffer.AsSpan(cur, available).CommonPrefixLength(buffer.AsSpan(from, available));
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

            var reach = i + Math.Max(1, Math.Max(longest, bestRep));
            for (; furthest < reach; furthest++)
                _nodePrice[furthest + 1] = int.MaxValue;

            var price = _nodePrice[i];
            var posState = (int)total & PbMask;
            var state2 = (state << 4) + posState;
            Relax(i + 1, price + BitPrice.Of(IsMatch[state2], 0) + LiteralPrice(cur, total, state, reps[0]), i, 1, LiteralBack);

            var matchPrice = price + BitPrice.Of(IsMatch[state2], 1);
            var repPrice = matchPrice + BitPrice.Of(IsRep[state], 1);
            if (reps[0] < total && buffer[cur - (int)reps[0] - 1] == buffer[cur])
                Relax(i + 1, repPrice + BitPrice.Of(IsRepG0[state], 0) + BitPrice.Of(IsRep0Long[state2], 0), i, 1, 0);
            for (var r = 0; r < 4; r++)
            {
                var length = _repLengths[r];
                if (length < 2)
                    continue;
                var basePrice = repPrice + RepIndexPrice(r, state, state2);
                var prices = _repLengthPrices.AsSpan(posState * LengthSymbols);
                for (var l = MatchMinLength; l <= length; l++)
                    Relax(i + l, basePrice + prices[l - MatchMinLength], i, l, (uint)r);
            }
            if (count > 0)
            {
                var basePrice = matchPrice + BitPrice.Of(IsRep[state], 0);
                var prices = _matchLengthPrices.AsSpan(posState * LengthSymbols);
                // Lengths the last distance repeats as far are cheaper as a repeat; start past them.
                var l = Math.Max(MatchMinLength, _repLengths[0] + 1);
                var k = 0;
                while (k < count && _lengths[k] < l)
                    k++;
                for (; k < count; k++)
                {
                    var distance = _distances[k];
                    var back = distance + 4;
                    // The distance is priced by length state: lengths 2, 3, 4 and 5 or more.
                    var short2 = DistancePrice(distance, 2);
                    var short3 = DistancePrice(distance, 3);
                    var short4 = DistancePrice(distance, 4);
                    var other = DistancePrice(distance, 5);
                    for (; l <= _lengths[k]; l++)
                    {
                        var candidatePrice = basePrice + prices[l - MatchMinLength] + (l switch { 2 => short2, 3 => short3, 4 => short4, _ => other });
                        var node = i + l;
                        if (candidatePrice < _nodePrice[node])
                        {
                            _nodePrice[node] = candidatePrice;
                            _nodePrev[node] = i;
                            _nodeLength[node] = l;
                            _nodeBack[node] = back;
                        }
                    }
                }
            }
        }

        // Walk the cheapest path back from the end of the window, then code it forwards.
        var steps = 0;
        for (var j = last; j > 0; j = _nodePrev[j])
            _path[steps++] = j;
        while (steps-- > 0)
        {
            var j = _path[steps];
            var length = _nodeLength[j];
            var back = _nodeBack[j];
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

    private void Relax(int node, int price, int from, int length, uint back)
    {
        if (price < _nodePrice[node])
        {
            _nodePrice[node] = price;
            _nodePrev[node] = from;
            _nodeLength[node] = length;
            _nodeBack[node] = back;
        }
    }

    /// <summary>The state and distances the cheapest path to <paramref name="node"/> leaves.</summary>
    private void FollowNode(int node)
    {
        var from = _nodePrev[node];
        var state = _nodeState[from];
        var reps = _nodeReps.AsSpan(node * 4, 4);
        _nodeReps.AsSpan(from * 4, 4).CopyTo(reps);
        var length = _nodeLength[node];
        var back = _nodeBack[node];
        if (length == 1)
        {
            state = back == LiteralBack ? AfterLiteral(state) : AfterShortRep(state);
        }
        else if (back < 4)
        {
            var distance = reps[(int)back];
            for (var r = (int)back; r > 0; r--)
                reps[r] = reps[r - 1];
            reps[0] = distance;
            state = AfterRep(state);
        }
        else
        {
            reps[3] = reps[2];
            reps[2] = reps[1];
            reps[1] = reps[0];
            reps[0] = back - 4;
            state = AfterMatch(state);
        }
        _nodeState[node] = state;
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
        for (var posState = 0; posState <= PbMask; posState++)
        {
            for (var l = 0; l < LengthSymbols; l++)
            {
                _matchLengthPrices[posState * LengthSymbols + l] = MatchLength.Price((uint)l, posState);
                _repLengthPrices[posState * LengthSymbols + l] = RepLength.Price((uint)l, posState);
            }
        }
        for (var lengthState = 0; lengthState < LengthStates; lengthState++)
        {
            var slots = PosSlot.AsSpan(lengthState << PosSlotBits, 1 << PosSlotBits);
            for (uint slot = 0; slot < 1 << PosSlotBits; slot++)
            {
                var price = BitPrice.Tree(slots, PosSlotBits, slot);
                if (slot >= EndPosModelIndex)
                    price += 16 * ((int)(slot >> 1) - 1 - AlignBits); // direct bits: one bit (16 / 16) each
                _slotPrices[(lengthState << PosSlotBits) + (int)slot] = price;
            }
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

    private uint Rep(int index) => index switch { 0 => Rep0, 1 => Rep1, 2 => Rep2, _ => Rep3 };

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

    private void EmitLiteral(RangeEncoder rc)
    {
        var buffer = _mf.Buffer;
        var pos = _mf.Position;
        var posState = (int)_total & PbMask;
        rc.EncodeBit(ref IsMatch[(State << 4) + posState], 0);
        var probs = Literal.AsSpan(LiteralBase(_total, _total > 0 ? buffer[pos - 1] : 0), 0x300);
        uint symbol = buffer[pos];
        if (State >= LiteralStates)
        {
            // Matched literal: while the bits agree with the byte at the last distance, a second set of
            // probabilities (chosen by that byte's bit) is used.
            uint matchByte = buffer[pos - (int)Rep0 - 1];
            uint m = 1;
            var same = true;
            for (var i = 7; i >= 0; i--)
            {
                var bit = (int)((symbol >> i) & 1);
                if (same)
                {
                    var matchBit = (matchByte >> i) & 1;
                    rc.EncodeBit(ref probs[(int)(((1 + matchBit) << 8) + m)], bit);
                    same = matchBit == (uint)bit;
                }
                else
                {
                    rc.EncodeBit(ref probs[(int)m], bit);
                }
                m = (m << 1) | (uint)bit;
            }
        }
        else
        {
            rc.EncodeTree(probs, 8, symbol);
        }
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
