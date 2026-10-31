// Word boundaries in Thai, Lao, Khmer and Myanmar text, which has no spaces between words (Line_Break SA): a run of such
// text splits into the dictionary's words, as few characters left outside a word as possible, then as few words as
// possible (maximal matching), then as few one-character words as possible (Burmese "မြန်မာ|ဘာသာစကား|သည်", not
// "မြန်မာဘာသာ|စ|ကားသည်"), and the line may break between two of them. Characters no word covers stay together
// with their neighbours outside words, so a name the dictionary doesn't know is not broken inside.
//
// The word lists are ICU's (build/generate-word-dictionaries.ps1, Typesetting/Dictionaries), loaded the first time
// text in their script is broken.

using System.IO.Compression;
using System.Text;

namespace Filee.Engines.Typesetting;

internal sealed class WordDictionary
{
    private static readonly Dictionary<UnicodeScript, Lazy<WordDictionary>> Dictionaries = new()
    {
        [UnicodeScript.Thai] = new(() => Load("thai")),
        [UnicodeScript.Lao] = new(() => Load("lao")),
        [UnicodeScript.Khmer] = new(() => Load("khmer")),
        [UnicodeScript.Myanmar] = new(() => Load("burmese")),
    };

    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _words;
    private readonly int _longest;

    private WordDictionary(HashSet<string> words)
    {
        _words = words.GetAlternateLookup<ReadOnlySpan<char>>();
        _longest = words.Count == 0 ? 0 : words.Max(w => w.Length);
    }

    /// <summary>The dictionary of a script, or null for a script without one.</summary>
    public static WordDictionary? For(UnicodeScript script) =>
        Dictionaries.TryGetValue(script, out var dictionary) ? dictionary.Value : null;

    /// <summary>
    /// The word boundaries inside <paramref name="text"/>[<paramref name="start"/>..<paramref name="end"/>], among the
    /// positions <paramref name="boundaries"/> allows (the starts of characters with their marks), ascending.
    /// </summary>
    public List<int> Boundaries(string text, int start, int end, Func<int, bool> boundaries)
    {
        var length = end - start;
        // For each position from the start: the fewest characters outside words, then the fewest pieces, then the fewest
        // one-character words, to reach it.
        var outside = new int[length + 1];
        var pieces = new int[length + 1];
        var singles = new int[length + 1];
        var from = new int[length + 1];
        var known = new bool[length + 1];
        Array.Fill(outside, int.MaxValue);
        outside[0] = pieces[0] = 0;
        for (var i = 0; i < length; i++)
        {
            if (outside[i] == int.MaxValue || i > 0 && !boundaries(start + i))
                continue;
            // The end of the character (with its marks) that starts here.
            var next = i + 1;
            while (next < length && !boundaries(start + next))
                next++;
            for (var j = i + 1; j <= Math.Min(length, i + _longest); j++)
            {
                if (j < length && !boundaries(start + j) || !_words.Contains(text.AsSpan(start + i, j - i)))
                    continue;
                Relax(i, j, outside[i], pieces[i] + 1, singles[i] + (j == next ? 1 : 0), word: true);
            }
            // One character outside any word.
            Relax(i, next, outside[i] + next - i, pieces[i] + 1, singles[i], word: false);
        }

        // Back from the end: a boundary between two pieces unless both are outside words.
        var result = new List<int>();
        for (var j = length; j > 0;)
        {
            var i = from[j];
            if (i > 0 && (known[j] || known[i]))
                result.Add(start + i);
            j = i;
        }
        result.Reverse();
        return result;

        void Relax(int i, int j, int cost, int count, int single, bool word)
        {
            if ((cost, count, single).CompareTo((outside[j], pieces[j], singles[j])) < 0)
                (outside[j], pieces[j], singles[j], from[j], known[j]) = (cost, count, single, i, word);
        }
    }

    private static WordDictionary Load(string name)
    {
        using var resource = typeof(WordDictionary).Assembly.GetManifestResourceStream($"Filee.Engines.Dictionaries.{name}.words.br")
                             ?? throw new InvalidOperationException($"The {name} word list is missing from the assembly.");
        using var brotli = new BrotliStream(resource, CompressionMode.Decompress);
        using var data = new MemoryStream();
        brotli.CopyTo(data);
        var bytes = data.GetBuffer().AsSpan(0, (int)data.Length);

        var words = new HashSet<string>(StringComparer.Ordinal);
        var previous = "";
        while (bytes.Length > 0)
        {
            // The count may itself be a line feed; the UTF-8 after it has none.
            var shared = bytes[0];
            var end = 1 + bytes[1..].IndexOf((byte)'\n');
            var word = string.Concat(previous.AsSpan(0, shared), Encoding.UTF8.GetString(bytes[1..end]));
            words.Add(word);
            previous = word;
            bytes = bytes[(end + 1)..];
        }
        return new WordDictionary(words);
    }
}
