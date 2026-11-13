// PDFsharp's ContentReader parses the arrays and dictionaries of a content stream recursively, with no limit: a stream
// of a million "[" (about a kilobyte deflated) overflows the stack, even a 64 MB one, and the process dies. Callers
// that hand untrusted content to ContentReader (PdfPictureUses for Compress PDF, PdfBlankPages) scan it here first and
// treat a stream nested too deeply as one that can't be read.

using PdfSharp.Pdf;

namespace Filee.Engines.Pdf;

/// <summary>Refuses content streams whose arrays and dictionaries nest too deeply for PDFsharp's parser.</summary>
internal static class PdfContentNesting
{
    /// <summary>Arrays and dictionaries inside each other at most; content streams use two or three.</summary>
    public const int MaxDepth = 64;

    /// <summary>The page's content, all its streams together, checked.</summary>
    /// <exception cref="InvalidDataException">The content nests deeper than <see cref="MaxDepth"/>.</exception>
    public static void Check(PdfPage page)
    {
        using var content = new MemoryStream();
        switch (PdfCompressor.Resolve(page.Elements["/Contents"]))
        {
            case PdfDictionary { Stream: not null } stream:
                content.Write(stream.Stream.UnfilteredValue);
                break;
            case PdfArray parts:
                foreach (var part in parts.Elements.Select(PdfCompressor.Resolve).OfType<PdfDictionary>())
                {
                    if (part.Stream is not null)
                    {
                        content.Write(part.Stream.UnfilteredValue);
                        content.WriteByte((byte)'\n');
                    }
                }
                break;
        }
        Checked(content.GetBuffer().AsSpan(0, (int)content.Length));
    }

    /// <summary><paramref name="content"/>, once checked.</summary>
    /// <exception cref="InvalidDataException">The content nests deeper than <see cref="MaxDepth"/>.</exception>
    public static byte[] Checked(byte[] content)
    {
        Checked(content.AsSpan());
        return content;
    }

    private static void Checked(ReadOnlySpan<byte> data)
    {
        var depth = 0;
        var i = 0;
        while (i < data.Length)
        {
            var c = data[i];
            switch (c)
            {
                case (byte)'%':
                    while (i < data.Length && data[i] is not 10 and not 13)
                        i++;
                    continue;
                case (byte)'(':
                    i = AfterString(data, i);
                    continue;
                case (byte)'<' when i + 1 < data.Length && data[i + 1] == '<':
                    i += 2;
                    Enter(ref depth);
                    continue;
                case (byte)'<':
                    while (i < data.Length && data[i] != '>')
                        i++;
                    i++;
                    continue;
                case (byte)'>' when i + 1 < data.Length && data[i + 1] == '>':
                    i += 2;
                    depth = Math.Max(0, depth - 1);
                    continue;
                case (byte)'[':
                    i++;
                    Enter(ref depth);
                    continue;
                case (byte)']':
                    i++;
                    depth = Math.Max(0, depth - 1);
                    continue;
            }
            if (IsRegular(c))
            {
                var start = i;
                while (i < data.Length && IsRegular(data[i]))
                    i++;
                // An inline image's data is binary: it ends at "EI" between white space.
                if (i - start == 2 && data[start] == 'I' && data[start + 1] == 'D' && (start == 0 || data[start - 1] != '/'))
                    i = AfterInlineImage(data, i);
                continue;
            }
            i++;
        }
    }

    private static void Enter(ref int depth)
    {
        if (++depth > MaxDepth)
            throw new InvalidDataException($"The content stream nests arrays or dictionaries more than {MaxDepth} deep.");
    }

    private static int AfterString(ReadOnlySpan<byte> data, int i)
    {
        var open = 0;
        for (; i < data.Length; i++)
        {
            switch (data[i])
            {
                case (byte)'\\':
                    i++;
                    break;
                case (byte)'(':
                    open++;
                    break;
                case (byte)')' when --open == 0:
                    return i + 1;
            }
        }
        return i;
    }

    private static int AfterInlineImage(ReadOnlySpan<byte> data, int i)
    {
        for (var at = i + 1; at + 1 < data.Length; at++)
        {
            if (data[at] == 'E' && data[at + 1] == 'I' && IsSpace(data[at - 1]) && (at + 2 == data.Length || IsSpace(data[at + 2])))
                return at + 2;
        }
        return data.Length;
    }

    private static bool IsRegular(byte c) => !IsSpace(c) && c is not ((byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%');

    private static bool IsSpace(byte c) => c is 0 or 9 or 10 or 12 or 13 or 32;
}
