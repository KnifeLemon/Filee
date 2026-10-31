// RTF tokens for RtfReader: groups, control words and symbols, hex bytes, text and \bin data. The lexer works on the
// file's bytes; text is decoded by the reader, which knows the code page of the font in effect.

namespace Filee.Engines.Hwp.Hwpx;

internal sealed partial class RtfReader
{
    private enum TokenKind
    {
        End,
        Open,
        Close,

        /// <summary>A control word (<c>\b</c>, <c>\fs24</c>) with its parameter, if any.</summary>
        Word,

        /// <summary>A control symbol (<c>\~</c>, <c>\*</c>, <c>\{</c>).</summary>
        Symbol,

        /// <summary>A byte written as <c>\'hh</c>.</summary>
        Hex,

        /// <summary>Plain text bytes.</summary>
        Text,

        /// <summary>The data of <c>\binN</c>.</summary>
        Binary,
    }

    private readonly record struct Token(TokenKind Kind, string Word = "", int? Parameter = null, byte Byte = 0, int Start = 0, int Length = 0);

    /// <summary>Splits RTF bytes into tokens.</summary>
    private sealed class Lexer(byte[] data)
    {
        /// <summary>Control words are short; a longer run of letters is damage.</summary>
        private const int MaxWordLength = 32;

        private int _at;

        public byte[] Data => data;

        public Token Next()
        {
            while (_at < data.Length && data[_at] is (byte)'\r' or (byte)'\n')
                _at++;
            if (_at >= data.Length)
                return new Token(TokenKind.End);
            var c = data[_at];
            switch (c)
            {
                case (byte)'{':
                    _at++;
                    return new Token(TokenKind.Open);
                case (byte)'}':
                    _at++;
                    return new Token(TokenKind.Close);
                case (byte)'\\':
                    return Control();
            }
            var start = _at;
            while (_at < data.Length && data[_at] is not ((byte)'{' or (byte)'}' or (byte)'\\' or (byte)'\r' or (byte)'\n'))
                _at++;
            return new Token(TokenKind.Text, Start: start, Length: _at - start);
        }

        private Token Control()
        {
            _at++; // backslash
            if (_at >= data.Length)
                return new Token(TokenKind.End);
            var c = data[_at];
            if (c == '\'')
            {
                if (_at + 2 < data.Length && Hex(data[_at + 1]) is >= 0 and var high && Hex(data[_at + 2]) is >= 0 and var low)
                {
                    _at += 3;
                    return new Token(TokenKind.Hex, Byte: (byte)(high << 4 | low));
                }
                // A damaged escape: its hex digits, if any, go with it.
                _at++;
                for (var i = 0; i < 2 && _at < data.Length && Hex(data[_at]) >= 0; i++)
                    _at++;
                return new Token(TokenKind.Symbol, "'");
            }
            if (!char.IsAsciiLetter((char)c))
            {
                _at++;
                // A backslash before a line end is a paragraph mark.
                return c is (byte)'\r' or (byte)'\n' ? new Token(TokenKind.Word, "par") : new Token(TokenKind.Symbol, ((char)c).ToString());
            }

            var start = _at;
            while (_at < data.Length && char.IsAsciiLetter((char)data[_at]) && _at - start < MaxWordLength)
                _at++;
            var word = System.Text.Encoding.ASCII.GetString(data, start, _at - start);
            int? parameter = null;
            var negative = _at < data.Length && data[_at] == '-';
            var digitsAt = negative ? _at + 1 : _at;
            if (digitsAt < data.Length && char.IsAsciiDigit((char)data[digitsAt]))
            {
                _at = digitsAt;
                long value = 0;
                while (_at < data.Length && char.IsAsciiDigit((char)data[_at]))
                {
                    if (value < int.MaxValue)
                        value = value * 10 + (data[_at] - '0');
                    _at++;
                }
                parameter = (int)Math.Clamp(negative ? -value : value, int.MinValue, int.MaxValue);
            }
            if (_at < data.Length && data[_at] == ' ')
                _at++; // the delimiter belongs to the control word

            if (word == "bin" && parameter is > 0 and var length)
            {
                var available = Math.Min(length, data.Length - _at);
                var token = new Token(TokenKind.Binary, Start: _at, Length: available);
                _at += available;
                return token;
            }
            return new Token(TokenKind.Word, word, parameter);
        }

        private static int Hex(byte c) => c switch
        {
            >= (byte)'0' and <= (byte)'9' => c - '0',
            >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
            >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
            _ => -1,
        };
    }
}
