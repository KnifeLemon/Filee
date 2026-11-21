// The hidden text of a DjVu page (DjVu v3 specification, "Text Chunk": TXTa, or TXTz which is TXTa packed with BZZ):
// the page's text in UTF-8, then a tree of zones (page, column, region, paragraph, line, word, character), each with
// its box in page pixels (row 0 at the bottom) and the range of the text it covers. A zone's box is coded relative to
// the previous zone at its level or to its parent. The lines and words become the invisible text layer of the PDF. A
// zone above the line level with no smaller zones (a page whose text has only its page zone, a bare paragraph) is cut
// into its text's lines, stacked evenly down its box.

using System.Text;
using Filee.Engines.Documents;

namespace Filee.Engines.Djvu;

/// <summary>A box in page pixels, row 0 at the bottom.</summary>
internal readonly record struct DjvuRect(int Left, int Bottom, int Width, int Height);

/// <summary>A word of a text line and its box.</summary>
internal sealed record DjvuWord(string Text, DjvuRect Box);

/// <summary>A line of text: its box and words (one estimated word per run of text when the file has no word zones).</summary>
internal sealed record DjvuLine(DjvuRect Box, IReadOnlyList<DjvuWord> Words, string Text);

/// <summary>Reads TXTa data.</summary>
internal static class DjvuText
{
    private const int Line = 5, Word = 6;

    private sealed class Zone(int type, DjvuRect box, int textStart, int textLength)
    {
        public int Type { get; } = type;
        public DjvuRect Box { get; } = box;
        public int TextStart { get; } = textStart;
        public int TextLength { get; } = textLength;
        public List<Zone> Children { get; } = [];
    }

    /// <summary>The lines of a page's text; empty when it has no zones. Damaged text throws InvalidDataException.</summary>
    public static List<DjvuLine> Read(byte[] data, DjvuLimits limits)
    {
        var reader = new DjvuBytes(data);
        var size = reader.U24();
        if (size <= 0 || size > data.Length - 3)
            throw new InvalidDataException("A DjVu text chunk is damaged.");
        var text = reader.Bytes(size);
        if (reader.Remaining == 0)
            return [];
        if (reader.U8() != 1)
            throw new InvalidDataException("A DjVu text chunk has a version Filee doesn't know.");
        var root = ReadZones(reader, size, limits);
        return Lines(root, text);
    }

    /// <summary>The zone tree, read without recursion.</summary>
    private static Zone ReadZones(DjvuBytes reader, int textSize, DjvuLimits limits)
    {
        var zones = 0;
        // Each open zone with how many children it still has and its last child read.
        var stack = new Stack<(Zone Zone, int Remaining, Zone? Last)>();
        Zone? root = null;
        Zone? parent = null;
        Zone? previous = null;
        while (true)
        {
            if (++zones > limits.MaxTextZones)
                throw DocumentLimits.Exceeded($"A DjVu page's text has more than {DocumentLimits.Count(limits.MaxTextZones)} zones.");
            var type = reader.U8();
            if (type is < 1 or > 7 || (parent is not null && type <= parent.Type))
                throw new InvalidDataException("A DjVu text zone is damaged.");
            var x = reader.U16() - 0x8000;
            var y = reader.U16() - 0x8000;
            var width = reader.U16() - 0x8000;
            var height = reader.U16() - 0x8000;
            var start = reader.U16() - 0x8000;
            var length = reader.U24();
            if (previous is not null)
            {
                if (type is 1 or 4 or 5)
                {
                    x += previous.Box.Left;
                    y = previous.Box.Bottom - (y + height);
                }
                else
                {
                    x += previous.Box.Left + previous.Box.Width;
                    y += previous.Box.Bottom;
                }
                start += previous.TextStart + previous.TextLength;
            }
            else if (parent is not null)
            {
                x += parent.Box.Left;
                y = parent.Box.Bottom + parent.Box.Height - (y + height);
                start += parent.TextStart;
            }
            var children = reader.U24();
            if (start < 0 || length < 0 || (long)start + length > textSize)
                throw new InvalidDataException("A DjVu text zone points outside the text.");
            var zone = new Zone(type, new DjvuRect(x, y, width, height), start, length);
            if (parent is null)
                root = zone;
            else
                parent.Children.Add(zone);
            if (stack.Count > 0)
            {
                var top = stack.Pop();
                stack.Push((top.Zone, top.Remaining - 1, zone));
            }
            if (children > 0)
            {
                stack.Push((zone, children, null));
                parent = zone;
                previous = null;
                continue;
            }
            // Closes the zones whose children are all read.
            while (stack.Count > 0 && stack.Peek().Remaining == 0)
                stack.Pop();
            if (stack.Count == 0)
                return root!;
            (parent, _, previous) = stack.Peek();
        }
    }

    private static List<DjvuLine> Lines(Zone root, byte[] text)
    {
        var lines = new List<DjvuLine>();
        var pending = new Stack<Zone>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var zone = pending.Pop();
            if (zone.Type < Line && zone.Children.Count == 0)
            {
                lines.AddRange(Block(zone, text));
                continue;
            }
            if (zone.Type == Line || zone.Type >= Word)
            {
                var line = MakeLine(zone, text);
                if (line is not null)
                    lines.Add(line);
                continue;
            }
            for (var i = zone.Children.Count - 1; i >= 0; i--)
                pending.Push(zone.Children[i]);
        }
        return lines;
    }

    /// <summary>Separators DjVu ends lines, paragraphs, regions and columns with.</summary>
    private static readonly char[] LineBreaks = ['\n', '\r', '\v', '\u001d', '\u001f'];

    /// <summary>A zone without smaller zones as its text's lines, each an even share of its box from the top.</summary>
    private static List<DjvuLine> Block(Zone zone, byte[] text)
    {
        var lines = new List<DjvuLine>();
        var box = zone.Box;
        if (box.Width <= 0 || box.Height <= 0)
            return lines;
        var parts = Encoding.UTF8.GetString(text, zone.TextStart, zone.TextLength)
            .Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Trim('\0'))
            .Where(part => part.Length > 0)
            .ToList();
        for (var i = 0; i < parts.Count; i++)
        {
            var top = box.Bottom + box.Height - (int)((long)box.Height * i / parts.Count);
            var bottom = box.Bottom + box.Height - (int)((long)box.Height * (i + 1) / parts.Count);
            if (top > bottom)
                lines.Add(new DjvuLine(new DjvuRect(box.Left, bottom, box.Width, top - bottom), [], parts[i]));
        }
        return lines;
    }

    private static DjvuLine? MakeLine(Zone zone, byte[] text)
    {
        if (zone.Box.Width <= 0 || zone.Box.Height <= 0)
            return null;
        var whole = Text(text, zone.TextStart, zone.TextLength);
        if (whole.Length == 0)
            return null;
        var words = new List<DjvuWord>();
        if (zone.Type == Line)
        {
            foreach (var child in zone.Children)
            {
                if (child.Type != Word || child.Box.Width <= 0)
                    continue;
                var word = Text(text, child.TextStart, child.TextLength);
                if (word.Length > 0)
                    words.Add(new DjvuWord(word, child.Box));
            }
        }
        else if (zone.Type >= Word)
        {
            words.Add(new DjvuWord(whole, zone.Box));
        }
        return new DjvuLine(zone.Box, words, whole);
    }

    /// <summary>A zone's text without the separators DjVu ends zones with (space, line feed, 0x0B, 0x1D, 0x1F).</summary>
    private static string Text(byte[] text, int start, int length)
    {
        var value = Encoding.UTF8.GetString(text, start, length);
        return value.Trim().Trim('\v', '\u001d', '\u001f', '\0').Trim();
    }
}

/// <summary>Big-endian reading of a chunk's bytes; reading past the end is damage.</summary>
internal sealed class DjvuBytes(byte[] data, int offset = 0)
{
    private int _at = offset;

    public int Remaining => data.Length - _at;

    public int Position => _at;

    public int U8()
    {
        Need(1);
        return data[_at++];
    }

    public int U16()
    {
        Need(2);
        var value = (data[_at] << 8) | data[_at + 1];
        _at += 2;
        return value;
    }

    public int U24()
    {
        Need(3);
        var value = (data[_at] << 16) | (data[_at + 1] << 8) | data[_at + 2];
        _at += 3;
        return value;
    }

    public long U32()
    {
        Need(4);
        var value = ((long)data[_at] << 24) | ((long)data[_at + 1] << 16) | ((long)data[_at + 2] << 8) | data[_at + 3];
        _at += 4;
        return value;
    }

    /// <summary>A zero-terminated UTF-8 string.</summary>
    public string CString()
    {
        var end = Array.IndexOf(data, (byte)0, _at);
        if (end < 0)
            throw new InvalidDataException("A DjVu chunk ends early.");
        var value = Encoding.UTF8.GetString(data, _at, end - _at);
        _at = end + 1;
        return value;
    }

    public byte[] Bytes(int count)
    {
        Need(count);
        var value = data.AsSpan(_at, count).ToArray();
        _at += count;
        return value;
    }

    private void Need(int count)
    {
        if (count < 0 || count > data.Length - _at)
            throw new InvalidDataException("A DjVu chunk ends early.");
    }
}
