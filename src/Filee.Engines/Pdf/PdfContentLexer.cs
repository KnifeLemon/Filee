// Tokens of PDF content streams (pages, forms, patterns, Type 3 glyphs, annotation appearances), shared by
// PdfFontUses (which codes each font draws) and the PostScript writer. Strings are kept as their bytes: PDFsharp's
// ContentReader turns hex strings that start with FE FF into UTF-16 text and so loses the codes of a two-byte font.

using System.Text;

namespace Filee.Engines.Pdf;

/// <summary>
/// Content stream tokens: numbers (as double), names, strings (as their bytes), arrays (as lists), dictionaries (as
/// <see cref="Dictionary{TKey, TValue}"/> by name without the slash) and operators. Damaged syntax, and arrays or
/// dictionaries nested more than <see cref="MaxDepth"/> deep, throw <see cref="InvalidDataException"/>.
/// </summary>
internal sealed class PdfContentLexer(byte[] data)
{
    /// <summary>A name (<c>/F1</c>) without its slash, <c>#xx</c> escapes decoded.</summary>
    public sealed record Name(string Value);

    /// <summary>An operator, or a keyword such as <c>true</c>, <c>false</c> and <c>null</c>.</summary>
    public sealed record Operator(string Name);

    /// <summary>Arrays and dictionaries in each other, at most (real content nests two or three).</summary>
    public const int MaxDepth = 32;

    private int _at;

    public object? Next()
    {
        while (true)
        {
            SkipSpace();
            if (_at >= data.Length)
                return null;
            var token = Token(0);
            if (token is not ArrayEnd and not DictEnd)
                return token;
        }
    }

    private sealed record ArrayEnd;

    private sealed record DictEnd;

    private object? Token(int depth)
    {
        if (depth > MaxDepth)
            throw new InvalidDataException("Arrays or dictionaries in the content stream are nested too deeply.");
        SkipSpace();
        if (_at >= data.Length)
            throw new InvalidDataException("The content stream ends inside an object.");
        var c = data[_at];
        switch (c)
        {
            case (byte)'(':
                return Literal();
            case (byte)'<' when _at + 1 < data.Length && data[_at + 1] == '<':
                _at += 2;
                var dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
                while (Token(depth + 1) is var key and not DictEnd)
                {
                    var value = Token(depth + 1);
                    if (key is not Name name || value is DictEnd or ArrayEnd)
                        throw new InvalidDataException("A dictionary in the content stream is damaged.");
                    dictionary[name.Value] = value;
                }
                return dictionary;
            case (byte)'<':
                return Hex();
            case (byte)'>' when _at + 1 < data.Length && data[_at + 1] == '>':
                _at += 2;
                return new DictEnd();
            case (byte)'[':
                _at++;
                var list = new List<object?>();
                while (Token(depth + 1) is var item and not ArrayEnd)
                    list.Add(item);
                return list;
            case (byte)']':
                _at++;
                return new ArrayEnd();
            case (byte)'/':
                _at++;
                return new Name(Regular());
            case (byte)'{' or (byte)'}' or (byte)'>' or (byte)')':
                _at++;
                return new Operator(((char)c).ToString());
        }
        var word = Regular();
        if (word.Length == 0)
            throw new InvalidDataException("The content stream has a stray character.");
        return word[0] is >= '0' and <= '9' or '+' or '-' or '.' ? double.TryParse(word, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0 : new Operator(word);
    }

    private string Regular()
    {
        var text = new StringBuilder();
        while (_at < data.Length && !IsSpace(data[_at]) && !IsDelimiter(data[_at]))
        {
            if (data[_at] == '#' && _at + 2 < data.Length && Uri.IsHexDigit((char)data[_at + 1]) && Uri.IsHexDigit((char)data[_at + 2]))
            {
                text.Append((char)Convert.ToByte(Encoding.ASCII.GetString(data, _at + 1, 2), 16));
                _at += 3;
                continue;
            }
            text.Append((char)data[_at++]);
        }
        return text.ToString();
    }

    private byte[] Literal()
    {
        _at++; // (
        var bytes = new List<byte>();
        var depth = 1;
        while (_at < data.Length)
        {
            var c = data[_at++];
            if (c == '\\' && _at < data.Length)
            {
                var e = data[_at++];
                switch (e)
                {
                    case (byte)'n': bytes.Add(10); break;
                    case (byte)'r': bytes.Add(13); break;
                    case (byte)'t': bytes.Add(9); break;
                    case (byte)'b': bytes.Add(8); break;
                    case (byte)'f': bytes.Add(12); break;
                    case 13:
                        if (_at < data.Length && data[_at] == 10)
                            _at++;
                        break;
                    case 10:
                        break;
                    case >= (byte)'0' and <= (byte)'7':
                        var value = e - '0';
                        for (var i = 0; i < 2 && _at < data.Length && data[_at] is >= (byte)'0' and <= (byte)'7'; i++)
                            value = value * 8 + (data[_at++] - '0');
                        bytes.Add((byte)value);
                        break;
                    default:
                        bytes.Add(e);
                        break;
                }
                continue;
            }
            if (c == '(')
                depth++;
            else if (c == ')' && --depth == 0)
                return [.. bytes];
            bytes.Add(c);
        }
        throw new InvalidDataException("A string in the content stream isn't closed.");
    }

    private byte[] Hex()
    {
        _at++; // <
        var bytes = new List<byte>();
        var high = -1;
        while (_at < data.Length && data[_at] != '>')
        {
            var c = (char)data[_at++];
            if (IsSpace((byte)c))
                continue;
            if (!Uri.IsHexDigit(c))
                throw new InvalidDataException("A hex string in the content stream has a stray character.");
            var digit = Convert.ToInt32(c.ToString(), 16);
            if (high < 0)
            {
                high = digit;
            }
            else
            {
                bytes.Add((byte)(high << 4 | digit));
                high = -1;
            }
        }
        if (_at >= data.Length)
            throw new InvalidDataException("A hex string in the content stream isn't closed.");
        _at++; // >
        if (high >= 0)
            bytes.Add((byte)(high << 4));
        return [.. bytes];
    }

    /// <summary>
    /// Reads an inline image after "BI": its parameters up to "ID" (keys as they are written, abbreviated or not) and
    /// its data up to "EI". When the parameters tell how long the data is (<paramref name="unfilteredLength"/>, an
    /// image without a filter) that many bytes are taken; otherwise the data ends at the first "EI" between white
    /// space, the way readers find it.
    /// </summary>
    public (Dictionary<string, object?> Parameters, byte[] Data) ReadInlineImage(Func<Dictionary<string, object?>, int?> unfilteredLength)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        while (true)
        {
            var key = Next();
            if (key is Operator { Name: "ID" })
                break;
            if (key is not Name name)
                throw new InvalidDataException("An inline image has a parameter that isn't a name.");
            var value = Next();
            if (value is Operator { Name: "ID" } || (value is null && _at >= data.Length))
                throw new InvalidDataException("An inline image has no data.");
            parameters[name.Value] = value;
        }
        _at++; // the single white-space character after ID
        if (_at > data.Length)
            throw new InvalidDataException("An inline image has no data.");
        if (unfilteredLength(parameters) is { } length && length >= 0 && length <= data.Length - _at)
        {
            var after = _at + length;
            while (after < data.Length && IsSpace(data[after]))
                after++;
            if (after + 1 < data.Length && data[after] == 'E' && data[after + 1] == 'I')
            {
                var exact = data.AsSpan(_at, length).ToArray();
                _at = after + 2;
                return (parameters, exact);
            }
        }
        for (var i = _at; i + 1 < data.Length; i++)
        {
            if (data[i] == 'E' && data[i + 1] == 'I' && i > _at && IsSpace(data[i - 1]) && (i + 2 == data.Length || IsSpace(data[i + 2])))
            {
                var found = data.AsSpan(_at, i - 1 - _at).ToArray(); // the white space before EI is syntax
                _at = i + 2;
                return (parameters, found);
            }
        }
        throw new InvalidDataException("An inline image has no end.");
    }

    /// <summary>Skips an inline image's parameters, "ID", its data and "EI".</summary>
    public void SkipInlineImage()
    {
        while (Next() is var token && token is not Operator { Name: "ID" })
        {
            if (token is null)
                throw new InvalidDataException("An inline image has no data.");
        }
        _at++; // the single white-space character after ID
        // The data ends at "EI" between white space (or the end): the usual way readers find it.
        for (var i = _at; i + 1 < data.Length; i++)
        {
            if (data[i] == 'E' && data[i + 1] == 'I' && IsSpace(data[i - 1]) && (i + 2 == data.Length || IsSpace(data[i + 2])))
            {
                _at = i + 2;
                return;
            }
        }
        throw new InvalidDataException("An inline image has no end.");
    }

    private void SkipSpace()
    {
        while (_at < data.Length)
        {
            if (data[_at] == '%')
            {
                while (_at < data.Length && data[_at] is not 10 and not 13)
                    _at++;
            }
            else if (IsSpace(data[_at]))
            {
                _at++;
            }
            else
            {
                return;
            }
        }
    }

    private static bool IsSpace(byte c) => c is 0 or 9 or 10 or 12 or 13 or 32;

    private static bool IsDelimiter(byte c) => c is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';
}
