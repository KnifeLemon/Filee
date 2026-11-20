// The Burrows-Wheeler transform of a bzip2 block (Archives/Bzip2Writer). The block is first turned to its smallest
// rotation, a Lyndon word or a power of one (the root): a Lyndon word's rotations sort exactly as its suffixes do, so
// the root's suffix array gives the order of the block's rotations, each of the root's rows standing for as many equal
// rows of the block as the root repeats. The suffix array comes from suffix array induced sorting (SA-IS, Nong, Zhang
// and Chan 2009), linear in the block size whatever the data, so long repeats or hostile input cannot make it slow.
// Equal rows (a periodic block) may come in any order among themselves; the transform is the same.

using System.Buffers;
using System.Runtime.CompilerServices;

namespace Filee.Engines.Archives;

/// <summary>Sorts the rotations of a block and writes its Burrows-Wheeler transform.</summary>
internal static class Bzip2Sort
{
    // The sort's methods are compiled fully optimised on first use (AggressiveOptimization): each runs for a whole
    // block per call, so a job of a few dozen blocks never left their ReadyToRun code, which sorted at about two thirds
    // the speed.
    /// <summary>
    /// Writes the last column of the sorted rotations of <paramref name="block"/> to <paramref name="output"/> and returns
    /// the row of the unrotated block (bzip2's origin pointer).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static int Transform(ReadOnlySpan<byte> block, Span<byte> output, CancellationToken cancellationToken)
    {
        var n = block.Length;
        if (n <= 1)
        {
            block.CopyTo(output);
            return 0;
        }
        var shift = SmallestRotation(block, out var periodic);
        var word = ArrayPool<byte>.Shared.Rent(n);
        var sa = ArrayPool<int>.Shared.Rent(n);
        try
        {
            block[shift..].CopyTo(word);
            block[..shift].CopyTo(word.AsSpan(n - shift));
            // Only a block that equals one of its other rotations has a shorter root (and needs the prefix function).
            var root = periodic ? RootLength(word.AsSpan(0, n), sa) : n;
            var repeats = n / root;
            SuffixArray(new ByteText(word), root, 256, sa, cancellationToken);

            // The unrotated block starts at n − shift in the word, which is that place in the root.
            var start = (n - shift) % n % root;
            var origin = 0;
            var row = 0;
            for (var i = 0; i < root; i++)
            {
                var at = sa[i];
                if (at == start)
                    origin = row;
                var last = word[at == 0 ? root - 1 : at - 1];
                if (repeats == 1)
                    output[row] = last; // nearly every block: no repeating root
                else
                    output.Slice(row, repeats).Fill(last);
                row += repeats;
            }
            return origin;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(sa);
            ArrayPool<byte>.Shared.Return(word);
        }
    }

    /// <summary>
    /// Where the lexicographically smallest rotation of <paramref name="text"/> starts (two candidates race), and whether
    /// <paramref name="text"/> equals another of its rotations (is a power of a shorter piece).
    /// </summary>
    /// <remarks>
    /// A candidate is only dropped, with the k places after it, when a rotation there is larger than another, so a
    /// smallest rotation is never dropped. A periodic text has two or more: the candidates end up on two of them and
    /// match all the way round. A text with one runs out of candidates before.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static int SmallestRotation(ReadOnlySpan<byte> text, out bool periodic)
    {
        var n = text.Length;
        int i = 0, j = 1, k = 0;
        while (i < n && j < n && k < n)
        {
            var a = text[i + k < n ? i + k : i + k - n];
            var b = text[j + k < n ? j + k : j + k - n];
            if (a == b)
            {
                k++;
                continue;
            }
            // The loser's start and the k places after it can't start the smallest rotation either.
            if (a > b)
                i += k + 1;
            else
                j += k + 1;
            if (i == j)
                j++;
            k = 0;
        }
        periodic = k >= n;
        return Math.Min(i, j);
    }

    /// <summary>
    /// The length of the shortest piece <paramref name="text"/> is a whole number of copies of (its length when it is
    /// none), from the prefix function, kept in <paramref name="scratch"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int RootLength(ReadOnlySpan<byte> text, int[] scratch)
    {
        var n = text.Length;
        scratch[0] = 0;
        for (var i = 1; i < n; i++)
        {
            var k = scratch[i - 1];
            while (k > 0 && text[i] != text[k])
                k = scratch[k - 1];
            if (text[i] == text[k])
                k++;
            scratch[i] = k;
        }
        var period = n - scratch[n - 1];
        return n % period == 0 ? period : n;
    }

    /// <summary>Characters of a text by position (structs, so every level of the sort is compiled for its own type).</summary>
    private interface IText
    {
        int this[int index] { get; }

        /// <summary>Whether the <paramref name="length"/> characters at <paramref name="a"/> and <paramref name="b"/> are the same.</summary>
        bool Same(int a, int b, int length);
    }

    private readonly struct ByteText(byte[] text) : IText
    {
        public int this[int index] => text[index];

        public bool Same(int a, int b, int length) => text.AsSpan(a, length).SequenceEqual(text.AsSpan(b, length));
    }

    private readonly struct IntText(int[] text) : IText
    {
        public int this[int index] => text[index];

        public bool Same(int a, int b, int length) => text.AsSpan(a, length).SequenceEqual(text.AsSpan(b, length));
    }

    /// <summary>
    /// The suffix array of the first <paramref name="n"/> characters of <paramref name="text"/> (values below
    /// <paramref name="alphabet"/>), with an implied end character smaller than all others, into <paramref name="sa"/>.
    /// </summary>
    /// <remarks>
    /// SA-IS, written so that each step reads as little as it can:
    /// - No array of suffix types: a suffix's type follows from its first two characters and the scan that places it
    ///   (<see cref="Induce{T}"/>), and a bit per position marks the LMS ones.
    /// - LMS substrings are named by comparing their characters in one go, their lengths kept in the unused half of
    ///   the suffix array.
    /// The string of names is sorted the same way when names repeat; its alphabet is at most half the size.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void SuffixArray<T>(T text, int n, int alphabet, int[] sa, CancellationToken cancellationToken)
        where T : struct, IText
    {
        if (n == 1)
        {
            sa[0] = 0;
            return;
        }
        if (n == 2)
        {
            // "ab" or "ba": the smaller first; "aa": the shorter suffix first.
            var firstSmaller = text[0] < text[1];
            sa[0] = firstSmaller ? 0 : 1;
            sa[1] = firstSmaller ? 1 : 0;
            return;
        }
        ArgumentOutOfRangeException.ThrowIfGreaterThan(n, sa.Length);
        cancellationToken.ThrowIfCancellationRequested();

        var starts = ArrayPool<int>.Shared.Rent(alphabet);
        var ends = ArrayPool<int>.Shared.Rent(alphabet);
        var fill = ArrayPool<int>.Shared.Rent(alphabet);
        var lms = ArrayPool<int>.Shared.Rent(n / 2 + 1);
        var isLms = ArrayPool<ulong>.Shared.Rent((n >> 6) + 1);
        int[]? sorted = null;
        int[]? names = null;
        int[]? reducedSa = null;
        try
        {
            Array.Clear(starts, 0, alphabet);
            for (var i = 0; i < n; i++)
                starts[text[i]]++;
            var sum = 0;
            for (var c = 0; c < alphabet; c++)
            {
                var count = starts[c];
                starts[c] = sum;
                sum += count;
                ends[c] = sum;
            }

            // LMS positions (an S-type suffix after an L-type one; the last suffix is L-type), found right to left.
            // S-type: smaller than the suffix after it, so the first character is smaller, or equal and that one S-type.
            Array.Clear(isLms, 0, (n >> 6) + 1);
            var m = 0;
            var nextS = 0;
            var next = text[n - 1];
            for (var i = n - 2; i >= 0; i--)
            {
                var c = text[i];
                var s = c < next ? 1 : c == next ? nextS : 0;
                // Written every time and counted only for an LMS position: no branch to mispredict.
                var lmsHere = nextS & (s ^ 1);
                lms[m] = i + 1;
                m += lmsHere;
                isLms[(i + 1) >> 6] |= (ulong)lmsHere << (i + 1);
                nextS = s;
                next = c;
            }
            if (m == 0)
            {
                // No S-type suffix: the characters never rise, and the L-type ones induce from the last suffix alone.
                Array.Clear(sa, 0, n);
                Induce(text, n, sa, starts, ends, fill, alphabet);
                return;
            }
            lms.AsSpan(0, m).Reverse();

            // First pass: the LMS positions at the ends of their buckets; inducing sorts them by their LMS substrings.
            Array.Clear(sa, 0, n);
            Array.Copy(ends, fill, alphabet);
            for (var k = m - 1; k >= 0; k--)
                sa[--fill[text[lms[k]]]] = lms[k];
            Induce(text, n, sa, starts, ends, fill, alphabet);
            cancellationToken.ThrowIfCancellationRequested();

            // The LMS positions in that order, to the front.
            var found = 0;
            for (var i = 0; i < n; i++)
            {
                var p = sa[i];
                sa[found] = p;
                found += (int)(isLms[p >> 6] >> p) & 1;
            }

            // Name the LMS substrings (from one LMS position to the next, both included) in that order. Each one's
            // length, then its name, goes to sa[m + p / 2] (LMS positions are never next to each other); the last one
            // runs into the end, so it is unlike any other (length -1).
            Array.Clear(sa, m, n - m);
            for (var k = 0; k < m; k++)
                sa[m + (lms[k] >> 1)] = k + 1 < m ? lms[k + 1] - lms[k] + 1 : -1;
            var name = 0;
            int previous = -1, previousLength = 0;
            for (var i = 0; i < m; i++)
            {
                var p = sa[i];
                var length = sa[m + (p >> 1)];
                if (length < 0 || length != previousLength || !text.Same(p, previous, length))
                    name++;
                sa[m + (p >> 1)] = name; // 1 and up; 0 is a gap
                previous = p;
                previousLength = length;
            }

            sorted = ArrayPool<int>.Shared.Rent(m);
            if (name < m)
            {
                // Some substrings repeat: sort the string of names (in text order) to order the LMS suffixes.
                names = ArrayPool<int>.Shared.Rent(m);
                var k = 0;
                for (var i = m; i < n && k < m; i++)
                {
                    if (sa[i] != 0)
                        names[k++] = sa[i] - 1;
                }
                reducedSa = ArrayPool<int>.Shared.Rent(m);
                SuffixArray(new IntText(names), m, name, reducedSa, cancellationToken);
                for (var i = 0; i < m; i++)
                    sorted[i] = lms[reducedSa[i]];
            }
            else
            {
                for (var k = 0; k < m; k++)
                    sorted[sa[m + (lms[k] >> 1)] - 1] = lms[k];
            }

            // Second pass: the sorted LMS suffixes at the ends of their buckets, the rest induced from them.
            Array.Clear(sa, 0, n);
            Array.Copy(ends, fill, alphabet);
            for (var k = m - 1; k >= 0; k--)
                sa[--fill[text[sorted[k]]]] = sorted[k];
            Induce(text, n, sa, starts, ends, fill, alphabet);
        }
        finally
        {
            if (reducedSa is not null) ArrayPool<int>.Shared.Return(reducedSa);
            if (names is not null) ArrayPool<int>.Shared.Return(names);
            if (sorted is not null) ArrayPool<int>.Shared.Return(sorted);
            ArrayPool<ulong>.Shared.Return(isLms);
            ArrayPool<int>.Shared.Return(lms);
            ArrayPool<int>.Shared.Return(fill);
            ArrayPool<int>.Shared.Return(ends);
            ArrayPool<int>.Shared.Return(starts);
        }
    }

    /// <summary>
    /// Induced sorting from the LMS suffixes placed at the ends of their buckets (the rest of <paramref name="sa"/>
    /// zero): L-type suffixes left to right from the bucket starts, then S-type ones right to left from the bucket ends,
    /// which places the LMS ones again.
    /// </summary>
    /// <remarks>
    /// An entry placed as p means that the scan reaching it places p − 1; ~p (below zero) means it has nothing to place
    /// in that scan. Which it is follows from two characters: in the L scan, p − 1 is L-type when its character is not
    /// below p's (p being L-type, or LMS, whose character is below the one before it); in the S scan, p − 1 is S-type
    /// when its character is not above p's (p being S-type). The L scan turns over every entry it passes: an L-type
    /// suffix whose predecessor is L-type has nothing to place in the S scan, one whose predecessor is S-type has. The
    /// S scan leaves every entry as the plain position. 0 is both a gap and suffix 0, which never places anything.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Induce<T>(T text, int n, int[] sa, int[] starts, int[] ends, int[] fill, int alphabet)
        where T : struct, IText
    {
        Array.Copy(starts, fill, alphabet);
        // The last suffix, L-type (the end comes after it), first in its bucket.
        var last = n - 1;
        var c = text[last];
        sa[fill[c]++] = text[last - 1] >= c ? last : ~last;
        for (var i = 0; i < n; i++)
        {
            var v = sa[i];
            sa[i] = ~v;
            if (v <= 0)
                continue;
            var j = v - 1;
            c = text[j];
            sa[fill[c]++] = j > 0 && text[j - 1] >= c ? j : ~j;
        }

        Array.Copy(ends, fill, alphabet);
        for (var i = n - 1; i >= 0; i--)
        {
            var v = sa[i];
            if (v > 0)
            {
                var j = v - 1;
                c = text[j];
                sa[--fill[c]] = j > 0 && text[j - 1] <= c ? j : ~j;
            }
            else if (v < 0)
            {
                sa[i] = ~v;
            }
        }
    }
}
