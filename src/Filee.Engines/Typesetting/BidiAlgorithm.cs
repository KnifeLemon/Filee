// The Unicode Bidirectional Algorithm (UAX #9, Unicode 16) for one paragraph: the paragraph's direction (P2, P3), the
// explicit embeddings, overrides and isolates (X1–X8), isolating run sequences (X9, X10), weak types (W1–W7), paired
// brackets (N0), neutrals (N1, N2), implicit levels (I1, I2) and the reset of separators and trailing white space to the
// paragraph's level (L1). Reordering by level (L2) is per line, in ParagraphLayouter; mirrored glyphs (L4) come from
// HarfBuzz, which mirrors the characters of a right-to-left run as it shapes it.
//
// The characters X9 removes (embedding and override controls, boundary neutrals) take the level of the character before
// them, so they stay in that character's run; they have no glyphs of their own.

namespace Filee.Engines.Typesetting;

internal static class BidiAlgorithm
{
    /// <summary>Deepest explicit embedding level (BD2).</summary>
    private const int MaxDepth = 125;

    /// <summary>Most bracket pairs N0 looks for at once (BD16).</summary>
    private const int MaxBrackets = 63;

    /// <summary>
    /// The direction of a paragraph that doesn't set one: that of its first strong character outside isolates (P2, P3),
    /// left to right without one.
    /// </summary>
    public static bool IsRightToLeft(string text)
    {
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var codePoint = UnicodeData.CodePointAt(text, i);
            if (codePoint > 0xFFFF)
                i++;
            switch (UnicodeData.GetBidiClass(codePoint))
            {
                case BidiClass.L when depth == 0:
                    return false;
                case BidiClass.R or BidiClass.AL when depth == 0:
                    return true;
                case BidiClass.RLI or BidiClass.LRI or BidiClass.FSI:
                    depth++;
                    break;
                case BidiClass.PDI when depth > 0:
                    depth--;
                    break;
                case BidiClass.B:
                    return false;
            }
        }
        return false;
    }

    /// <summary>
    /// The embedding level of each UTF-16 index of a paragraph (odd: right to left; a surrogate pair's two halves alike).
    /// </summary>
    /// <param name="rightToLeft">The paragraph's direction; null takes it from the text (P2, P3).</param>
    public static int[] Levels(string text, bool? rightToLeft = null)
    {
        // Text without anything right to left or explicit in it, in a left-to-right paragraph, is all at level 0 (most
        // paragraphs).
        if (rightToLeft != true && LeftToRightOnly(text))
            return new int[text.Length];
        var (codePoints, starts) = Decode(text);
        var original = codePoints.Select(UnicodeData.GetBidiClass).ToArray();
        var paragraph = (rightToLeft ?? FirstStrong(original, 0, original.Length) == BidiClass.R) ? 1 : 0;
        var levels = Resolve(codePoints, original, paragraph);

        var result = new int[text.Length];
        for (var i = 0; i < codePoints.Length; i++)
        {
            var end = i + 1 < starts.Length ? starts[i + 1] : text.Length;
            for (var u = starts[i]; u < end; u++)
                result[u] = levels[i];
        }
        return result;
    }

    /// <summary>The levels of a paragraph's code points, by their Bidi_Class, at the paragraph's level (0 or 1).</summary>
    internal static int[] Resolve(int[] codePoints, BidiClass[] original, int paragraph)
    {
        var n = original.Length;
        var types = (BidiClass[])original.Clone();
        var levels = new int[n];
        var matching = MatchingPdi(original);

        Explicit(original, types, levels, matching, paragraph);

        // X9: the controls and boundary neutrals are left out of the runs.
        var kept = new List<int>(n);
        for (var i = 0; i < n; i++)
        {
            if (!Removed(original[i]))
                kept.Add(i);
        }
        foreach (var sequence in Sequences(kept, levels, original, matching, paragraph))
            ResolveSequence(sequence, codePoints, original, types, levels);

        // X9's removed characters: the level of the character before them.
        for (var i = 0; i < n; i++)
        {
            if (Removed(original[i]))
                levels[i] = i > 0 ? levels[i - 1] : paragraph;
        }

        // L1: separators, and the white space and isolate controls before them or at the paragraph's end.
        var trailing = true;
        for (var i = n - 1; i >= 0; i--)
        {
            var type = original[i];
            if (type is BidiClass.S or BidiClass.B)
            {
                levels[i] = paragraph;
                trailing = true;
            }
            else if (trailing && (type is BidiClass.WS or BidiClass.FSI or BidiClass.LRI or BidiClass.RLI or BidiClass.PDI || Removed(type)))
            {
                levels[i] = paragraph;
            }
            else
            {
                trailing = false;
            }
        }
        return levels;
    }

    // ───────────────────────── Explicit levels (X1–X8) ─────────────────────────

    private readonly record struct Status(int Level, BidiClass Override, bool Isolate);

    private static void Explicit(BidiClass[] original, BidiClass[] types, int[] levels, int[] matching, int paragraph)
    {
        var stack = new Stack<Status>();
        stack.Push(new Status(paragraph, BidiClass.ON, false));
        int overflowIsolates = 0, overflowEmbeddings = 0, validIsolates = 0;
        for (var i = 0; i < original.Length; i++)
        {
            var top = stack.Peek();
            var type = original[i];
            switch (type)
            {
                case BidiClass.RLE or BidiClass.LRE or BidiClass.RLO or BidiClass.LRO:
                    {
                        // X2–X5.
                        var odd = type is BidiClass.RLE or BidiClass.RLO;
                        var level = odd ? (top.Level + 1) | 1 : (top.Level + 2) & ~1;
                        levels[i] = top.Level;
                        if (level <= MaxDepth && overflowIsolates == 0 && overflowEmbeddings == 0)
                        {
                            var direction = type switch { BidiClass.RLO => BidiClass.R, BidiClass.LRO => BidiClass.L, _ => BidiClass.ON };
                            stack.Push(new Status(level, direction, false));
                        }
                        else if (overflowIsolates == 0)
                        {
                            overflowEmbeddings++;
                        }
                        break;
                    }
                case BidiClass.RLI or BidiClass.LRI or BidiClass.FSI:
                    {
                        // X5a–X5c.
                        levels[i] = top.Level;
                        if (top.Override != BidiClass.ON)
                            types[i] = top.Override;
                        var odd = type == BidiClass.RLI
                                  || type == BidiClass.FSI && FirstStrong(original, i + 1, matching[i] >= 0 ? matching[i] : original.Length) == BidiClass.R;
                        var level = odd ? (top.Level + 1) | 1 : (top.Level + 2) & ~1;
                        if (level <= MaxDepth && overflowIsolates == 0 && overflowEmbeddings == 0)
                        {
                            validIsolates++;
                            stack.Push(new Status(level, BidiClass.ON, true));
                        }
                        else
                        {
                            overflowIsolates++;
                        }
                        break;
                    }
                case BidiClass.PDI:
                    {
                        // X6a.
                        if (overflowIsolates > 0)
                        {
                            overflowIsolates--;
                        }
                        else if (validIsolates > 0)
                        {
                            overflowEmbeddings = 0;
                            while (!stack.Peek().Isolate)
                                stack.Pop();
                            stack.Pop();
                            validIsolates--;
                        }
                        top = stack.Peek();
                        levels[i] = top.Level;
                        if (top.Override != BidiClass.ON)
                            types[i] = top.Override;
                        break;
                    }
                case BidiClass.PDF:
                    // X7.
                    levels[i] = top.Level;
                    if (overflowIsolates > 0)
                    {
                    }
                    else if (overflowEmbeddings > 0)
                    {
                        overflowEmbeddings--;
                    }
                    else if (!top.Isolate && stack.Count >= 2)
                    {
                        stack.Pop();
                    }
                    break;
                case BidiClass.B:
                    // X8: a paragraph separator ends every embedding, override and isolate.
                    levels[i] = paragraph;
                    stack.Clear();
                    stack.Push(new Status(paragraph, BidiClass.ON, false));
                    (overflowIsolates, overflowEmbeddings, validIsolates) = (0, 0, 0);
                    break;
                case BidiClass.BN:
                    levels[i] = top.Level;
                    break;
                default:
                    // X6.
                    levels[i] = top.Level;
                    if (top.Override != BidiClass.ON)
                        types[i] = top.Override;
                    break;
            }
        }
    }

    /// <summary>The index of the PDI that closes each isolate initiator (BD9), -1 for none and for other characters.</summary>
    private static int[] MatchingPdi(BidiClass[] types)
    {
        var matching = new int[types.Length];
        Array.Fill(matching, -1);
        var open = new Stack<int>();
        for (var i = 0; i < types.Length; i++)
        {
            switch (types[i])
            {
                case BidiClass.RLI or BidiClass.LRI or BidiClass.FSI:
                    open.Push(i);
                    break;
                case BidiClass.PDI when open.Count > 0:
                    matching[open.Pop()] = i;
                    break;
                case BidiClass.B:
                    open.Clear();
                    break;
            }
        }
        return matching;
    }

    /// <summary>
    /// The first strong type from <paramref name="start"/> to before <paramref name="end"/>, outside isolates and before a
    /// paragraph separator: L, R (AL counts as R), or ON for none.
    /// </summary>
    private static BidiClass FirstStrong(BidiClass[] types, int start, int end)
    {
        var depth = 0;
        for (var i = start; i < end; i++)
        {
            switch (types[i])
            {
                case BidiClass.L when depth == 0:
                    return BidiClass.L;
                case BidiClass.R or BidiClass.AL when depth == 0:
                    return BidiClass.R;
                case BidiClass.RLI or BidiClass.LRI or BidiClass.FSI:
                    depth++;
                    break;
                case BidiClass.PDI when depth > 0:
                    depth--;
                    break;
                case BidiClass.B:
                    return BidiClass.ON;
            }
        }
        return BidiClass.ON;
    }

    private static bool LeftToRightOnly(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            // Below U+0590 only the explicit controls aren't left to right or neutral, and they are above it too.
            if (text[i] < 0x0590)
                continue;
            var codePoint = UnicodeData.CodePointAt(text, i);
            if (codePoint > 0xFFFF)
                i++;
            if (UnicodeData.GetBidiClass(codePoint) is BidiClass.R or BidiClass.AL or BidiClass.AN || Explicit(UnicodeData.GetBidiClass(codePoint)))
                return false;
        }
        return true;
    }

    private static bool Explicit(BidiClass type) =>
        type is BidiClass.LRE or BidiClass.LRO or BidiClass.RLE or BidiClass.RLO or BidiClass.PDF or BidiClass.LRI or BidiClass.RLI
            or BidiClass.FSI or BidiClass.PDI;

    private static bool Removed(BidiClass type) =>
        type is BidiClass.RLE or BidiClass.LRE or BidiClass.RLO or BidiClass.LRO or BidiClass.PDF or BidiClass.BN;

    // ───────────────────────── Isolating run sequences (X10) ─────────────────────────

    /// <summary>An isolating run sequence: its characters in order, its level, and the types before and after it.</summary>
    private sealed record Sequence(List<int> Indices, int Level, BidiClass Sos, BidiClass Eos);

    private static List<Sequence> Sequences(List<int> kept, int[] levels, BidiClass[] original, int[] matching, int paragraph)
    {
        // Level runs (BD7) over the characters X9 keeps.
        var runs = new List<List<int>>();
        foreach (var index in kept)
        {
            if (runs.Count == 0 || levels[runs[^1][^1]] != levels[index])
                runs.Add([]);
            runs[^1].Add(index);
        }
        var runOf = new Dictionary<int, int>();
        for (var r = 0; r < runs.Count; r++)
            runOf[runs[r][0]] = r;
        var position = new Dictionary<int, int>(kept.Count);
        for (var k = 0; k < kept.Count; k++)
            position[kept[k]] = k;

        // Runs that continue the sequence of an isolate initiator ending an earlier run.
        var continuing = new HashSet<int>();
        var sequences = new List<Sequence>();
        for (var r = 0; r < runs.Count; r++)
        {
            if (continuing.Contains(r))
                continue;
            var indices = new List<int>(runs[r]);
            while (original[indices[^1]] is BidiClass.RLI or BidiClass.LRI or BidiClass.FSI
                   && matching[indices[^1]] is var pdi and >= 0 && runOf.TryGetValue(pdi, out var next))
            {
                continuing.Add(next);
                indices.AddRange(runs[next]);
            }

            var level = levels[indices[0]];
            var first = position[indices[0]];
            var before = first > 0 ? levels[kept[first - 1]] : paragraph;
            var last = position[indices[^1]];
            var after = last + 1 < kept.Count && original[indices[^1]] is not (BidiClass.RLI or BidiClass.LRI or BidiClass.FSI)
                ? levels[kept[last + 1]]
                : paragraph;
            sequences.Add(new Sequence(indices, level, Direction(Math.Max(level, before)), Direction(Math.Max(level, after))));
        }
        return sequences;
    }

    private static BidiClass Direction(int level) => (level & 1) == 1 ? BidiClass.R : BidiClass.L;

    // ───────────────────────── Weak, neutral and implicit (W1–I2) ─────────────────────────

    private static void ResolveSequence(Sequence sequence, int[] codePoints, BidiClass[] original, BidiClass[] all, int[] levels)
    {
        var indices = sequence.Indices;
        var n = indices.Count;
        var types = new BidiClass[n];
        for (var k = 0; k < n; k++)
            types[k] = all[indices[k]];

        // W1: a non-spacing mark takes the type before it (ON after an isolate initiator or PDI).
        for (var k = 0; k < n; k++)
        {
            if (types[k] != BidiClass.NSM)
                continue;
            types[k] = k == 0 ? sequence.Sos
                : types[k - 1] is BidiClass.RLI or BidiClass.LRI or BidiClass.FSI or BidiClass.PDI ? BidiClass.ON
                : types[k - 1];
        }
        // W2, W3: European numbers after Arabic letters are Arabic numbers; Arabic letters are R.
        var strong = sequence.Sos;
        for (var k = 0; k < n; k++)
        {
            switch (types[k])
            {
                case BidiClass.L or BidiClass.R or BidiClass.AL:
                    strong = types[k];
                    break;
                case BidiClass.EN when strong == BidiClass.AL:
                    types[k] = BidiClass.AN;
                    break;
            }
        }
        for (var k = 0; k < n; k++)
        {
            if (types[k] == BidiClass.AL)
                types[k] = BidiClass.R;
        }
        // W4: one separator between two numbers of a kind joins them.
        for (var k = 1; k + 1 < n; k++)
        {
            if (types[k] == BidiClass.ES && types[k - 1] == BidiClass.EN && types[k + 1] == BidiClass.EN)
                types[k] = BidiClass.EN;
            else if (types[k] == BidiClass.CS && types[k - 1] == types[k + 1] && types[k - 1] is BidiClass.EN or BidiClass.AN)
                types[k] = types[k - 1];
        }
        // W5: terminators next to European numbers are European numbers.
        for (var k = 0; k < n; k++)
        {
            if (types[k] != BidiClass.ET)
                continue;
            var end = k;
            while (end < n && types[end] == BidiClass.ET)
                end++;
            if (k > 0 && types[k - 1] == BidiClass.EN || end < n && types[end] == BidiClass.EN)
                Array.Fill(types, BidiClass.EN, k, end - k);
            k = end - 1;
        }
        // W6: other separators and terminators are neutral.
        for (var k = 0; k < n; k++)
        {
            if (types[k] is BidiClass.ES or BidiClass.ET or BidiClass.CS)
                types[k] = BidiClass.ON;
        }
        // W7: European numbers after left-to-right text are L.
        strong = sequence.Sos;
        for (var k = 0; k < n; k++)
        {
            if (types[k] is BidiClass.L or BidiClass.R)
                strong = types[k];
            else if (types[k] == BidiClass.EN && strong == BidiClass.L)
                types[k] = BidiClass.L;
        }

        var embedding = Direction(sequence.Level);
        Brackets(sequence, codePoints, original, types, embedding);

        // N1, N2: neutrals between two of one direction take it, the others the embedding direction.
        for (var k = 0; k < n; k++)
        {
            if (!Neutral(types[k]))
                continue;
            var end = k;
            while (end < n && Neutral(types[end]))
                end++;
            var before = k > 0 ? StrongOf(types[k - 1]) : sequence.Sos;
            var after = end < n ? StrongOf(types[end]) : sequence.Eos;
            Array.Fill(types, before == after ? before : embedding, k, end - k);
            k = end - 1;
        }

        // I1, I2.
        for (var k = 0; k < n; k++)
        {
            var level = sequence.Level;
            levels[indices[k]] = (level & 1) == 0
                ? types[k] switch { BidiClass.R => level + 1, BidiClass.AN or BidiClass.EN => level + 2, _ => level }
                : types[k] is BidiClass.L or BidiClass.EN or BidiClass.AN ? level + 1 : level;
        }
    }

    private static bool Neutral(BidiClass type) =>
        type is BidiClass.B or BidiClass.S or BidiClass.WS or BidiClass.ON or BidiClass.RLI or BidiClass.LRI or BidiClass.FSI or BidiClass.PDI;

    /// <summary>A resolved type as the neutral rules see it: numbers count as R.</summary>
    private static BidiClass StrongOf(BidiClass type) => type is BidiClass.L ? BidiClass.L : BidiClass.R;

    /// <summary>N0: paired brackets take the direction of what they hold, or of their context.</summary>
    private static void Brackets(Sequence sequence, int[] codePoints, BidiClass[] original, BidiClass[] types, BidiClass embedding)
    {
        var indices = sequence.Indices;
        var pairs = new List<(int Open, int Close)>();
        var open = new List<(int Pair, int Position)>();
        for (var k = 0; k < indices.Count; k++)
        {
            if (types[k] != BidiClass.ON || !UnicodeData.PairedBracket(codePoints[indices[k]], out var pair, out var opens))
                continue;
            if (opens)
            {
                if (open.Count == MaxBrackets)
                    break;
                open.Add((Canonical(pair), k));
                continue;
            }
            var code = Canonical(codePoints[indices[k]]);
            for (var s = open.Count - 1; s >= 0; s--)
            {
                if (open[s].Pair != code)
                    continue;
                pairs.Add((open[s].Position, k));
                open.RemoveRange(s, open.Count - s);
                break;
            }
        }
        pairs.Sort((a, b) => a.Open.CompareTo(b.Open));

        foreach (var (first, last) in pairs)
        {
            var inside = BidiClass.ON;
            for (var k = first + 1; k < last; k++)
            {
                if (types[k] is BidiClass.ON or BidiClass.B or BidiClass.S or BidiClass.WS or BidiClass.RLI or BidiClass.LRI or BidiClass.FSI or BidiClass.PDI or BidiClass.NSM)
                    continue;
                var type = StrongOf(types[k]);
                if (type == embedding)
                {
                    inside = type;
                    break;
                }
                inside = type;
            }
            if (inside == BidiClass.ON)
                continue;
            var direction = embedding;
            if (inside != embedding)
            {
                // Only the opposite direction inside: the brackets take it if the text before them has it too.
                var context = sequence.Sos;
                for (var k = first - 1; k >= 0; k--)
                {
                    if (types[k] is BidiClass.L or BidiClass.R or BidiClass.EN or BidiClass.AN)
                    {
                        context = StrongOf(types[k]);
                        break;
                    }
                }
                direction = context == inside ? inside : embedding;
            }
            types[first] = types[last] = direction;
            // Non-spacing marks after a bracket take its new type.
            foreach (var bracket in new[] { first, last })
            {
                for (var k = bracket + 1; k < indices.Count && original[indices[k]] == BidiClass.NSM; k++)
                    types[k] = direction;
            }
        }
    }

    /// <summary>Brackets that are canonically equivalent pair with each other (U+2329/U+232A and U+3008/U+3009).</summary>
    private static int Canonical(int codePoint) => codePoint switch { 0x2329 => 0x3008, 0x232A => 0x3009, _ => codePoint };

    private static (int[] CodePoints, int[] Starts) Decode(string text)
    {
        var codePoints = new List<int>(text.Length);
        var starts = new List<int>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            starts.Add(i);
            var codePoint = UnicodeData.CodePointAt(text, i);
            codePoints.Add(codePoint);
            if (codePoint > 0xFFFF)
                i++;
        }
        return ([.. codePoints], [.. starts]);
    }
}
