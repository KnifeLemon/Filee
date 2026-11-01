// Writes PostScript text: tokens separated by spaces with lines kept under 255 characters (the DSC limit), numbers
// without exponents, strings with every byte outside printable ASCII escaped, hex and ASCII85 data. Only 7-bit ASCII
// is written, so the files survive any channel, and no line of data can start with "%%" (a DSC comment).

using System.Globalization;
using System.Text;

namespace Filee.Engines.Pdf.PostScript;

/// <summary>A PostScript text writer over a stream.</summary>
internal sealed class PsWriter(Stream output) : IDisposable
{
    private const int LineWidth = 200;
    private const string HexDigits = "0123456789abcdef";
    private readonly Stream _output = output;
    private int _column;

    /// <summary>Bytes written so far.</summary>
    public long Position => _output.Position;

    public Stream BaseStream => _output;

    /// <summary>Starts a new line unless at the start of one.</summary>
    public PsWriter NewLine()
    {
        if (_column > 0)
            Write("\n");
        return this;
    }

    /// <summary>A whole line (a DSC comment or a line of the prolog).</summary>
    public PsWriter Line(string line)
    {
        NewLine();
        Write(line);
        Write("\n");
        return this;
    }

    /// <summary>A token, after a space or on a new line when the current one is long.</summary>
    public PsWriter Token(string token)
    {
        if (_column > 0)
        {
            if (_column + 1 + token.Length > LineWidth)
                Write("\n");
            else
                Write(" ");
        }
        Write(token);
        return this;
    }

    public PsWriter Number(double value) => Token(Format(value));

    public PsWriter Numbers(ReadOnlySpan<double> values)
    {
        foreach (var value in values)
            Number(value);
        return this;
    }

    /// <summary>An array of numbers, <c>[a b c]</c>.</summary>
    public PsWriter Array(ReadOnlySpan<double> values)
    {
        Token("[");
        Numbers(values);
        return Token("]");
    }

    /// <summary>A literal name. Only names Filee makes up are written (letters, digits, '.', '-', '_').</summary>
    public PsWriter Name(string name)
    {
        if (!IsSafeName(name))
            throw new ArgumentException($"'{name}' isn't a name Filee writes.", nameof(name));
        return Token("/" + name);
    }

    /// <summary>True for names made of letters, digits, '.', '-' and '_' only, at most 127 characters.</summary>
    public static bool IsSafeName(string name) =>
        name.Length is > 0 and <= 127 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    /// <summary>A string literal: printable ASCII as it is, parentheses and backslashes escaped, other bytes as octal.</summary>
    public PsWriter String(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 0 && Printable(bytes) < bytes.Length / 2)
            return Hex(bytes);
        var text = new StringBuilder(bytes.Length + 2);
        text.Append('(');
        var lineStart = 0;
        foreach (var b in bytes)
        {
            if (text.Length - lineStart > LineWidth)
            {
                text.Append("\\\n"); // a backslash before the line end is no part of the string
                lineStart = text.Length;
            }
            switch (b)
            {
                case (byte)'(' or (byte)')' or (byte)'\\':
                    text.Append('\\').Append((char)b);
                    break;
                case >= 32 and < 127:
                    text.Append((char)b);
                    break;
                default:
                    text.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
                    break;
            }
        }
        text.Append(')');
        return Token(text.ToString());
    }

    private static int Printable(ReadOnlySpan<byte> bytes)
    {
        var count = 0;
        foreach (var b in bytes)
        {
            if (b is >= 32 and < 127)
                count++;
        }
        return count;
    }

    /// <summary>A hex string, <c>&lt;0a1b…&gt;</c>, wrapped over lines.</summary>
    public PsWriter Hex(ReadOnlySpan<byte> bytes)
    {
        if (_column > 0)
            Write(" ");
        Write("<");
        Span<char> pair = stackalloc char[2];
        foreach (var b in bytes)
        {
            if (_column >= LineWidth)
                Write("\n");
            pair[0] = HexDigits[b >> 4];
            pair[1] = HexDigits[b & 15];
            Write(pair);
        }
        Write(">");
        return this;
    }

    /// <summary>
    /// ASCII85 data ending in <c>~&gt;</c> on lines of their own: the data an image or a font reads from the file.
    /// When <paramref name="literal"/> is true it is written as a string literal (<c>&lt;~…~&gt;</c>).
    /// </summary>
    public PsWriter Ascii85(ReadOnlySpan<byte> bytes, bool literal = false)
    {
        if (literal)
        {
            if (_column > 0)
                Write(" ");
            Write("<~");
        }
        else
        {
            NewLine();
        }
        Span<char> group = stackalloc char[5];
        for (var i = 0; i < bytes.Length; i += 4)
        {
            var n = Math.Min(4, bytes.Length - i);
            uint value = 0;
            for (var k = 0; k < 4; k++)
                value = value << 8 | (k < n ? bytes[i + k] : 0u);
            if (value == 0 && n == 4)
            {
                Chars("z");
                continue;
            }
            for (var k = 4; k >= 0; k--)
            {
                group[k] = (char)('!' + value % 85);
                value /= 85;
            }
            Chars(group[..(n + 1)]);
        }
        if (_column + 2 > LineWidth)
            Write("\n");
        Write("~>");
        if (!literal)
            Write("\n");
        return this;

        void Chars(ReadOnlySpan<char> chars)
        {
            // A line of data never starts with '%': it would read as a comment.
            if (_column + chars.Length > LineWidth || (_column == 0 && chars[0] == '%'))
                Write(_column == 0 ? " " : "\n ");
            Write(chars);
        }
    }

    /// <summary>Text written as it is (prolog code, which Filee wrote).</summary>
    public PsWriter Raw(string text)
    {
        Write(text);
        return this;
    }

    /// <summary>Bytes written as they are (data whose length the reader knows).</summary>
    public PsWriter RawBytes(ReadOnlySpan<byte> bytes)
    {
        _output.Write(bytes);
        _column = bytes.Length > 0 && bytes[^1] == '\n' ? 0 : _column + bytes.Length;
        return this;
    }

    public static string Format(double value)
    {
        if (!double.IsFinite(value))
            return "0";
        value = Math.Clamp(value, -1e9, 1e9);
        var rounded = Math.Round(value, Math.Abs(value) >= 1000 ? 3 : 5);
        if (rounded == 0)
            return "0";
        return rounded.ToString("0.#####", CultureInfo.InvariantCulture);
    }

    private void Write(ReadOnlySpan<char> text)
    {
        Span<byte> buffer = stackalloc byte[256];
        while (text.Length > 0)
        {
            var n = Math.Min(text.Length, buffer.Length);
            for (var i = 0; i < n; i++)
            {
                var c = text[i];
                buffer[i] = c < 128 ? (byte)c : (byte)'?';
                _column = c == '\n' ? 0 : _column + 1;
            }
            _output.Write(buffer[..n]);
            text = text[n..];
        }
    }

    public void Dispose() => _output.Dispose();
}
