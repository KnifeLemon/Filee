// Book → TCR (the text compression of Psion e-book readers), from the format description TcrReader follows: the
// "!!8-Bit!!" signature, 256 dictionary entries (a length byte and up to 255 bytes each), then one byte per entry
// for the whole text. The text is the book's plain text (PlainTextWriter, CR LF line ends) in Windows-1252, or UTF-8
// when it holds characters Windows-1252 lacks.
//
// The dictionary: every byte value the text uses gets an entry of its own (so any text can be written), and the
// codes left over are filled by pair merging on a sample of the text (up to 512 KB in slices across the book): the
// most frequent pair of neighbouring entries becomes a new entry, until all 256 codes are taken or no pair repeats.
// The text is then written with the longest entry that matches at each position.

using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Ebooks;

internal static class TcrWriter
{
    private static readonly byte[] Signature = "!!8-Bit!!"u8.ToArray();

    /// <summary>Bytes of the text the dictionary is learnt from, in <see cref="SampleSlices"/> slices across it.</summary>
    private const int SampleBytes = 512 * 1024;

    private const int SampleSlices = 16;
    private const int MaxEntryLength = 255;

    public static void Write(Book book, string outputPath, CancellationToken cancellationToken = default)
    {
        var (bytes, _) = LegacyWriting.Encode(LegacyWriting.PlainText(book.Document, "\r\n"));
        LegacyWriting.CheckText(bytes.Length, "TCR");
        var file = Compress(bytes, cancellationToken);
        WriterTempFile.Write(outputPath, stream => stream.Write(file));
    }

    /// <summary>The TCR file of <paramref name="text"/>: signature, dictionary, codes.</summary>
    internal static byte[] Compress(ReadOnlySpan<byte> text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = Dictionary(text, cancellationToken);
        using var output = new MemoryStream(text.Length / 2 + 1024);
        output.Write(Signature);
        for (var i = 0; i < 256; i++)
        {
            var entry = i < entries.Count ? entries[i] : [];
            output.WriteByte((byte)entry.Length);
            output.Write(entry);
        }

        // The entries starting with each byte, longest first: the first that matches is the longest match.
        var byFirst = new List<int>[256];
        for (var code = 0; code < entries.Count; code++)
            (byFirst[entries[code][0]] ??= []).Add(code);
        foreach (var list in byFirst)
            list?.Sort((a, b) => entries[b].Length.CompareTo(entries[a].Length));

        var codes = new byte[Math.Min(text.Length, 1 << 16)];
        var count = 0;
        for (var i = 0; i < text.Length;)
        {
            if (count == codes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Write(codes, 0, count);
                count = 0;
            }
            foreach (var code in byFirst[text[i]]!)
            {
                var entry = entries[code];
                if (text[i..].StartsWith(entry))
                {
                    codes[count++] = (byte)code;
                    i += entry.Length;
                    break;
                }
            }
        }
        output.Write(codes, 0, count);
        return output.ToArray();
    }

    /// <summary>
    /// The dictionary: an entry for every byte value of <paramref name="text"/>, then entries made by merging the most
    /// frequent neighbouring pair of a sample, as long as codes are left and a pair occurs at least three times.
    /// </summary>
    internal static List<byte[]> Dictionary(ReadOnlySpan<byte> text, CancellationToken cancellationToken)
    {
        var present = new bool[256];
        foreach (var b in text)
            present[b] = true;
        // Symbols 0-255 are bytes, 256 and up merged entries; -1 separates the slices of the sample.
        var symbols = new List<byte[]>();
        var entries = new List<byte[]>();
        for (var b = 0; b < 256; b++)
        {
            symbols.Add([(byte)b]);
            if (present[b])
                entries.Add([(byte)b]);
        }
        if (entries.Count >= 256 || text.Length < 4)
            return entries;

        var sample = Sample(text);
        var length = sample.Length;
        var counts = new int[512 * 512];
        while (entries.Count < 256 && symbols.Count < 512)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Array.Clear(counts);
            int best = -1, bestCount = 2;
            for (var i = 0; i + 1 < length; i++)
            {
                var (a, b) = (sample[i], sample[i + 1]);
                if (a < 0 || b < 0)
                    continue;
                var key = a * 512 + b;
                var n = ++counts[key];
                // A run "aaa" counts twice but merges once; good enough to rank the pairs.
                if (n > bestCount && symbols[a].Length + symbols[b].Length <= MaxEntryLength)
                    (best, bestCount) = (key, n);
            }
            if (best < 0)
                break;
            var (left, right) = (best / 512, best % 512);
            var merged = new byte[symbols[left].Length + symbols[right].Length];
            symbols[left].CopyTo(merged, 0);
            symbols[right].CopyTo(merged, symbols[left].Length);
            var symbol = symbols.Count;
            symbols.Add(merged);
            entries.Add(merged);

            var write = 0;
            for (var read = 0; read < length; read++)
            {
                if (read + 1 < length && sample[read] == left && sample[read + 1] == right)
                {
                    sample[write++] = symbol;
                    read++;
                }
                else
                {
                    sample[write++] = sample[read];
                }
            }
            length = write;
        }
        return entries;
    }

    /// <summary>Up to <see cref="SampleBytes"/> of the text as symbols, in slices from across it separated by -1.</summary>
    private static int[] Sample(ReadOnlySpan<byte> text)
    {
        if (text.Length <= SampleBytes)
        {
            var whole = new int[text.Length];
            for (var i = 0; i < text.Length; i++)
                whole[i] = text[i];
            return whole;
        }
        var slice = SampleBytes / SampleSlices;
        var sample = new int[SampleSlices * (slice + 1)];
        var at = 0;
        for (var s = 0; s < SampleSlices; s++)
        {
            var start = (int)((long)(text.Length - slice) * s / (SampleSlices - 1));
            foreach (var b in text.Slice(start, slice))
                sample[at++] = b;
            sample[at++] = -1;
        }
        return sample;
    }
}
