// The Burrows-Wheeler transform of a bzip2 block (Archives/Bzip2Writer). The block is first turned to its smallest
// rotation, a Lyndon word or a power of one (the root): a Lyndon word's rotations sort exactly as its suffixes do, so
// the root's suffix array gives the order of the block's rotations, each of the root's rows standing for as many equal
// rows of the block as the root repeats. The suffix array comes from suffix array induced sorting (SA-IS, Nong, Zhang
// and Chan 2009), linear in the block size whatever the data, so long repeats or hostile input cannot make it slow.
// Equal rows (a periodic block) may come in any order among themselves; the transform is the same.

using System.Buffers;

namespace Filee.Engines.Archives;

/// <summary>Sorts the rotations of a block and writes its Burrows-Wheeler transform.</summary>
internal static class Bzip2Sort
{
    /// <summary>
    /// Writes the last column of the sorted rotations of <paramref name="block"/> to <paramref name="output"/> and returns
    /// the row of the unrotated block (bzip2's origin pointer).
    /// </summary>
    public static int Transform(ReadOnlySpan<byte> block, Span<byte> output, CancellationToken cancellationToken)
    {
        var n = block.Length;
        if (n <= 1)
        {
            block.CopyTo(output);
            return 0;
        }
        var shift = SmallestRotation(block);
        var word = ArrayPool<byte>.Shared.Rent(n);
        var sa = ArrayPool<int>.Shared.Rent(n);
        try
        {
            block[shift..].CopyTo(word);
            block[..shift].CopyTo(word.AsSpan(n - shift));
            var root = RootLength(word.AsSpan(0, n), sa);
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
                output.Slice(row, repeats).Fill(word[at == 0 ? root - 1 : at - 1]);
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

    /// <summary>Where the lexicographically smallest rotation of <paramref name="text"/> starts (two candidates race).</summary>
    internal static int SmallestRotation(ReadOnlySpan<byte> text)
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
        return Math.Min(i, j);
    }

    /// <summary>
    /// The length of the shortest piece <paramref name="text"/> is a whole number of copies of (its length when it is
    /// none), from the prefix function, kept in <paramref name="scratch"/>.
    /// </summary>
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
    }

    private readonly struct ByteText(byte[] text) : IText
    {
        public int this[int index] => text[index];
    }

    private readonly struct IntText(int[] text) : IText
    {
        public int this[int index] => text[index];
    }

    /// <summary>
    /// The suffix array of the first <paramref name="n"/> characters of <paramref name="text"/> (values below
    /// <paramref name="alphabet"/>), with an implied end character smaller than all others, into <paramref name="sa"/>.
    /// </summary>
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
        cancellationToken.ThrowIfCancellationRequested();

        // S-type: the suffix is smaller than the one after it. The last character is L-type (the end is smaller).
        var stype = ArrayPool<bool>.Shared.Rent(n);
        var starts = ArrayPool<int>.Shared.Rent(alphabet);
        var ends = ArrayPool<int>.Shared.Rent(alphabet);
        var fill = ArrayPool<int>.Shared.Rent(alphabet);
        // Leftmost S-type positions (LMS) are never next to each other, so position / 2 indexes them.
        var lmsIndex = ArrayPool<int>.Shared.Rent(n / 2 + 1);
        var lms = ArrayPool<int>.Shared.Rent(n / 2 + 1);
        int[]? sorted = null;
        int[]? names = null;
        int[]? reducedSa = null;
        try
        {
            stype[n - 1] = false;
            for (var i = n - 2; i >= 0; i--)
            {
                var a = text[i];
                var b = text[i + 1];
                stype[i] = a < b || a == b && stype[i + 1];
            }

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

            var m = 0;
            for (var i = 1; i < n; i++)
            {
                if (stype[i] && !stype[i - 1])
                {
                    lmsIndex[i >> 1] = m;
                    lms[m++] = i;
                }
            }

            // First pass: the LMS positions in text order; inducing sorts them by their LMS substrings.
            Induce(text, n, alphabet, stype, starts, ends, fill, lms.AsSpan(0, m), sa);
            if (m == 0)
                return;
            cancellationToken.ThrowIfCancellationRequested();

            sorted = ArrayPool<int>.Shared.Rent(m);
            var k = 0;
            for (var i = 0; i < n; i++)
            {
                var p = sa[i];
                if (p > 0 && stype[p] && !stype[p - 1])
                    sorted[k++] = p;
            }

            // Name the LMS substrings (from one LMS position to the next, both included) by their sorted order.
            names = ArrayPool<int>.Shared.Rent(m);
            var name = 0;
            names[lmsIndex[sorted[0] >> 1]] = 0;
            for (var i = 1; i < m; i++)
            {
                if (!SameLmsSubstring(text, n, lms, m, lmsIndex, sorted[i - 1], sorted[i]))
                    name++;
                names[lmsIndex[sorted[i] >> 1]] = name;
            }

            if (name + 1 < m)
            {
                // Some substrings repeat: sort the string of names (in text order) to order the LMS suffixes.
                reducedSa = ArrayPool<int>.Shared.Rent(m);
                SuffixArray(new IntText(names), m, name + 1, reducedSa, cancellationToken);
                for (var i = 0; i < m; i++)
                    sorted[i] = lms[reducedSa[i]];
            }
            else
            {
                for (var i = 0; i < m; i++)
                    sorted[names[i]] = lms[i];
            }

            Induce(text, n, alphabet, stype, starts, ends, fill, sorted.AsSpan(0, m), sa);
        }
        finally
        {
            if (reducedSa is not null) ArrayPool<int>.Shared.Return(reducedSa);
            if (names is not null) ArrayPool<int>.Shared.Return(names);
            if (sorted is not null) ArrayPool<int>.Shared.Return(sorted);
            ArrayPool<int>.Shared.Return(lms);
            ArrayPool<int>.Shared.Return(lmsIndex);
            ArrayPool<int>.Shared.Return(fill);
            ArrayPool<int>.Shared.Return(ends);
            ArrayPool<int>.Shared.Return(starts);
            ArrayPool<bool>.Shared.Return(stype);
        }
    }

    /// <summary>
    /// Induced sorting: the LMS positions go to the ends of their buckets in the given order, the L-type suffixes are
    /// placed from them left to right, then the S-type ones right to left.
    /// </summary>
    private static void Induce<T>(T text, int n, int alphabet, bool[] stype, int[] starts, int[] ends, int[] fill,
        ReadOnlySpan<int> lms, int[] sa)
        where T : struct, IText
    {
        Array.Fill(sa, -1, 0, n);
        Array.Copy(ends, fill, alphabet);
        for (var i = lms.Length - 1; i >= 0; i--)
        {
            var p = lms[i];
            sa[--fill[text[p]]] = p;
        }

        Array.Copy(starts, fill, alphabet);
        // The suffix before the end: the last character, L-type.
        sa[fill[text[n - 1]]++] = n - 1;
        for (var i = 0; i < n; i++)
        {
            var p = sa[i] - 1;
            if (p >= 0 && !stype[p])
                sa[fill[text[p]]++] = p;
        }

        Array.Copy(ends, fill, alphabet);
        for (var i = n - 1; i >= 0; i--)
        {
            var p = sa[i] - 1;
            if (p >= 0 && stype[p])
                sa[--fill[text[p]]] = p;
        }
    }

    /// <summary>Whether the LMS substrings at <paramref name="a"/> and <paramref name="b"/> are equal.</summary>
    private static bool SameLmsSubstring<T>(T text, int n, int[] lms, int m, int[] lmsIndex, int a, int b)
        where T : struct, IText
    {
        var nextA = lmsIndex[a >> 1] + 1;
        var nextB = lmsIndex[b >> 1] + 1;
        var endA = nextA < m ? lms[nextA] : n;
        var endB = nextB < m ? lms[nextB] : n;
        // The substring that runs into the end is unlike any other.
        if (endA == n || endB == n || endA - a != endB - b)
            return false;
        for (; a <= endA; a++, b++)
        {
            if (text[a] != text[b])
                return false;
        }
        return true;
    }
}
