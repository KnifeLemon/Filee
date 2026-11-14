// Type 1 fonts (Adobe Type 1 Font Format) in two directions:
//
//  * reading an embedded font program (a PDF's FontFile: the cleartext part, the eexec-encrypted part, binary or hex,
//    with or without PFB segment headers): FontMatrix, the built-in encoding, lenIV, Subrs and CharStrings. The font
//    program is PostScript, so nothing of it is ever run or copied: its charstrings are decrypted and interpreted
//    here (subroutines, flex, hint replacement and seac accents) into outlines;
//  * writing a Type 1 font of Filee's own from outlines (of Type 1 or CFF glyphs) for the PostScript output: a fixed
//    cleartext part and an eexec part with one charstring per glyph (hsbw, rmoveto, rlineto, rrcurveto, closepath,
//    endchar), without hints, subroutines or OtherSubrs, so no code but Filee's runs when the font is used.

using System.Globalization;
using System.Text;
using Filee.Engines.Fonts.Cff;

namespace Filee.Engines.Pdf.PostScript;

/// <summary>A Type 1 font program read from a PDF.</summary>
internal sealed class Type1Program
{
    private const int MaxGlyphs = 65_535;
    private const int MaxSubrDepth = 10;
    private const int MaxOperations = 200_000;

    private readonly Dictionary<string, byte[]> _charStrings = new(StringComparer.Ordinal);
    private byte[][] _subrs = [];

    /// <summary>The font's FontMatrix (glyph space → text space), [0.001 0 0 0.001 0 0] by default.</summary>
    public double[] FontMatrix { get; private set; } = [0.001, 0, 0, 0.001, 0, 0];

    /// <summary>The built-in encoding: code → glyph name (StandardEncoding when the font says so).</summary>
    public string?[] Encoding { get; } = new string?[256];

    public bool HasGlyph(string name) => _charStrings.ContainsKey(name);

    /// <summary>Reads a font program (FontFile stream contents).</summary>
    /// <exception cref="InvalidDataException">The program is damaged.</exception>
    public static Type1Program Read(byte[] data)
    {
        data = StripPfbHeaders(data);
        var eexec = IndexOf(data, "eexec"u8);
        if (eexec < 0)
            throw new InvalidDataException("The Type 1 font has no encrypted part.");
        var program = new Type1Program();
        program.ReadCleartext(System.Text.Encoding.Latin1.GetString(data, 0, eexec));

        var at = eexec + 5;
        while (at < data.Length && data[at] is 13 or 10 or 9 or 32)
            at++;
        var encrypted = data.AsSpan(at);
        if (encrypted.Length >= 4 && IsHex(encrypted[0]) && IsHex(encrypted[1]) && IsHex(encrypted[2]) && IsHex(encrypted[3]))
            encrypted = HexDecode(encrypted);
        var plain = Decrypt(encrypted, 55665, 4);
        program.ReadPrivate(plain);
        return program;
    }

    /// <summary>The outline of glyph <paramref name="name"/> in glyph space and its advance, or null when it is missing.</summary>
    public (List<PathContour> Contours, double Width)? Outline(string name)
    {
        if (!_charStrings.TryGetValue(name, out var code))
            return null;
        var interpreter = new Interpreter(this);
        return interpreter.Run(code, seacAllowed: true);
    }

    private static byte[] StripPfbHeaders(byte[] data)
    {
        if (data.Length < 6 || data[0] != 0x80)
            return data;
        var output = new MemoryStream(data.Length);
        var at = 0;
        while (at + 6 <= data.Length && data[at] == 0x80 && data[at + 1] is 1 or 2)
        {
            var length = (int)BitConverter.ToUInt32(data, at + 2);
            at += 6;
            if (length < 0 || length > data.Length - at)
                length = data.Length - at;
            output.Write(data, at, length);
            at += length;
        }
        return output.ToArray();
    }

    private static int IndexOf(byte[] data, ReadOnlySpan<byte> word) => data.AsSpan().IndexOf(word);

    private static bool IsHex(byte b) => b is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';

    private static byte[] HexDecode(ReadOnlySpan<byte> text)
    {
        var output = new List<byte>(text.Length / 2);
        var high = -1;
        foreach (var b in text)
        {
            if (!IsHex(b))
            {
                if (b is 13 or 10 or 9 or 32)
                    continue;
                break; // the zeros and cleartomark after the encrypted part
            }
            var digit = b <= '9' ? b - '0' : (b | 0x20) - 'a' + 10;
            if (high < 0)
            {
                high = digit;
            }
            else
            {
                output.Add((byte)(high << 4 | digit));
                high = -1;
            }
        }
        return [.. output];
    }

    /// <summary>Type 1 decryption (eexec key 55665, charstring key 4330), dropping the first <paramref name="skip"/> bytes.</summary>
    internal static byte[] Decrypt(ReadOnlySpan<byte> cipher, ushort key, int skip)
    {
        var plain = new byte[Math.Max(0, cipher.Length - skip)];
        var r = key;
        for (var i = 0; i < cipher.Length; i++)
        {
            var c = cipher[i];
            var p = (byte)(c ^ (r >> 8));
            r = (ushort)((c + r) * 52845 + 22719);
            if (i >= skip)
                plain[i - skip] = p;
        }
        return plain;
    }

    /// <summary>Type 1 encryption: <paramref name="prefix"/> zero bytes first (lenIV, or 4 for eexec).</summary>
    internal static byte[] Encrypt(ReadOnlySpan<byte> plain, ushort key, int prefix)
    {
        var cipher = new byte[plain.Length + prefix];
        var r = key;
        for (var i = 0; i < cipher.Length; i++)
        {
            var p = i < prefix ? (byte)0 : plain[i - prefix];
            var c = (byte)(p ^ (r >> 8));
            r = (ushort)((c + r) * 52845 + 22719);
            cipher[i] = c;
        }
        return cipher;
    }

    private void ReadCleartext(string text)
    {
        var matrix = System.Text.RegularExpressions.Regex.Match(text, @"/FontMatrix\s*[\[{]\s*([-+0-9.eE\s]+)[\]}]");
        if (matrix.Success)
        {
            var values = matrix.Groups[1].Value.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN).ToArray();
            if (values.Length == 6 && values.All(double.IsFinite) && Math.Abs(values[0] * values[3] - values[1] * values[2]) > 1e-12)
                FontMatrix = values;
        }
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"/Encoding\s+StandardEncoding\s+def"))
        {
            for (var code = 0; code < 256; code++)
                Encoding[code] = StandardEncoding(code);
            return;
        }
        foreach (System.Text.RegularExpressions.Match entry in System.Text.RegularExpressions.Regex.Matches(text, @"dup\s+(\d+)\s*/([^\s/\[\]{}()<>%]+)\s+put"))
        {
            if (int.TryParse(entry.Groups[1].Value, out var code) && code is >= 0 and < 256)
                Encoding[code] = entry.Groups[2].Value;
        }
    }

    private static string? StandardEncoding(int code)
    {
        var sid = code < CffStandardData.StandardEncoding.Length ? CffStandardData.StandardEncoding[code] : 0;
        return sid == 0 ? null : CffStandardData.StandardStrings[sid];
    }

    private void ReadPrivate(byte[] plain)
    {
        var text = System.Text.Encoding.Latin1.GetString(plain);
        var lenIV = 4;
        var lenMatch = System.Text.RegularExpressions.Regex.Match(text, @"/lenIV\s+(-?\d+)");
        if (lenMatch.Success && int.TryParse(lenMatch.Groups[1].Value, out var l))
            lenIV = l;

        var subrsAt = text.IndexOf("/Subrs", StringComparison.Ordinal);
        var charStringsAt = text.IndexOf("/CharStrings", StringComparison.Ordinal);
        if (charStringsAt < 0)
            throw new InvalidDataException("The Type 1 font has no CharStrings.");
        if (subrsAt >= 0 && subrsAt < charStringsAt)
            _subrs = ReadSubrs(plain, subrsAt, lenIV);

        // /CharStrings n dict dup begin /name len RD <bytes> ND ... end
        var at = text.IndexOf("begin", charStringsAt, StringComparison.Ordinal);
        if (at < 0)
            throw new InvalidDataException("The Type 1 font's CharStrings are damaged.");
        at += 5;
        while (at < plain.Length)
        {
            var token = NextToken(plain, ref at);
            if (token is null or "end")
                break;
            if (!token.StartsWith('/'))
                continue;
            var name = token[1..];
            var length = NextToken(plain, ref at);
            if (!int.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 0)
                throw new InvalidDataException("A Type 1 charstring has no length.");
            NextToken(plain, ref at); // RD or -|
            at++; // the space before the data
            if (n > plain.Length - at)
                throw new InvalidDataException("A Type 1 charstring runs past the end of the font.");
            var code = plain.AsSpan(at, n);
            at += n;
            if (_charStrings.Count >= MaxGlyphs)
                throw new InvalidDataException("The Type 1 font has too many glyphs.");
            _charStrings[name] = lenIV < 0 ? code.ToArray() : Decrypt(code, 4330, lenIV);
        }
    }

    private static byte[][] ReadSubrs(byte[] plain, int at, int lenIV)
    {
        at += "/Subrs".Length;
        var countToken = NextToken(plain, ref at);
        if (!int.TryParse(countToken, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 0 || count > MaxGlyphs)
            return [];
        var subrs = new byte[count][];
        for (var i = 0; i < count; i++)
            subrs[i] = [];
        while (at < plain.Length)
        {
            var save = at;
            var token = NextToken(plain, ref at);
            if (token == "array")
                continue;
            if (token != "dup")
            {
                if (token is "ND" or "|-" or "noaccess" or "def" or "readonly")
                    continue;
                at = save;
                break;
            }
            var indexToken = NextToken(plain, ref at);
            var lengthToken = NextToken(plain, ref at);
            if (!int.TryParse(indexToken, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                || !int.TryParse(lengthToken, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 0)
                break;
            NextToken(plain, ref at); // RD or -|
            at++;
            if (n > plain.Length - at)
                throw new InvalidDataException("A Type 1 subroutine runs past the end of the font.");
            if (index >= 0 && index < count)
                subrs[index] = lenIV < 0 ? plain.AsSpan(at, n).ToArray() : Decrypt(plain.AsSpan(at, n), 4330, lenIV);
            at += n;
            NextToken(plain, ref at); // NP or |
        }
        return subrs;
    }

    /// <summary>The next white-space separated token, or null at the end.</summary>
    private static string? NextToken(byte[] data, ref int at)
    {
        while (at < data.Length && data[at] is 13 or 10 or 9 or 32)
            at++;
        if (at >= data.Length)
            return null;
        var start = at;
        if (data[at] == '/')
            at++;
        while (at < data.Length && data[at] is not (13 or 10 or 9 or 32) && (at == start || data[at] is not ((byte)'/' or (byte)'[' or (byte)'{')))
            at++;
        return System.Text.Encoding.Latin1.GetString(data, start, at - start);
    }

    /// <summary>Runs Type 1 charstrings into outlines.</summary>
    private sealed class Interpreter(Type1Program font)
    {
        private readonly List<double> _stack = new(24);
        private readonly List<double> _psStack = new(8);
        private readonly List<Vec2> _flex = [];
        private List<PathContour> _contours = [];
        private PathContour? _current;
        private Vec2 _point;
        private Vec2 _origin;
        private double _width;
        private bool _flexing;
        private bool _ended;
        private int _operations;
        private (double Asb, double Adx, double Ady, int Base, int Accent)? _seac;

        public (List<PathContour>, double) Run(byte[] code, bool seacAllowed)
        {
            Execute(code, 0);
            Close();
            if (_seac is { } seac)
            {
                if (!seacAllowed)
                    throw new InvalidDataException("A Type 1 accent is itself an accented glyph.");
                var contours = _contours;
                var baseName = StandardEncoding(seac.Base);
                var accentName = StandardEncoding(seac.Accent);
                if (baseName is not null && font._charStrings.TryGetValue(baseName, out var baseCode))
                    contours.AddRange(new Interpreter(font).Run(baseCode, false).Item1);
                if (accentName is not null && font._charStrings.TryGetValue(accentName, out var accentCode))
                {
                    var accent = new Interpreter(font) { _origin = new Vec2(seac.Adx - seac.Asb, seac.Ady) };
                    contours.AddRange(accent.Run(accentCode, false).Item1);
                }
                return (contours, _width);
            }
            return (_contours, _width);
        }

        private void Execute(byte[] code, int depth)
        {
            if (depth > MaxSubrDepth)
                throw new InvalidDataException("Type 1 subroutines are nested too deeply.");
            var i = 0;
            while (i < code.Length && !_ended)
            {
                if (++_operations > MaxOperations)
                    throw new InvalidDataException("A Type 1 glyph runs too long.");
                int v = code[i++];
                if (v >= 32)
                {
                    if (v <= 246)
                    {
                        Push(v - 139);
                    }
                    else if (v <= 250)
                    {
                        if (i >= code.Length)
                            return;
                        Push((v - 247) * 256 + code[i++] + 108);
                    }
                    else if (v <= 254)
                    {
                        if (i >= code.Length)
                            return;
                        Push(-(v - 251) * 256 - code[i++] - 108);
                    }
                    else
                    {
                        if (i + 4 > code.Length)
                            return;
                        Push(code[i] << 24 | code[i + 1] << 16 | code[i + 2] << 8 | code[i + 3]);
                        i += 4;
                    }
                    continue;
                }
                if (v == 12)
                {
                    if (i >= code.Length)
                        return;
                    Escape(code[i++], depth);
                    continue;
                }
                switch (v)
                {
                    case 1 or 3: // hstem, vstem
                        break;
                    case 4: // vmoveto
                        Move(0, Arg(0));
                        break;
                    case 5:
                        Line(Arg(0), Arg(1));
                        break;
                    case 6:
                        Line(Arg(0), 0);
                        break;
                    case 7:
                        Line(0, Arg(0));
                        break;
                    case 8:
                        Curve(Arg(0), Arg(1), Arg(2), Arg(3), Arg(4), Arg(5));
                        break;
                    case 9:
                        Close();
                        break;
                    case 10: // callsubr
                        {
                            var index = (int)Pop();
                            if (index >= 0 && index < font._subrs.Length)
                                Execute(font._subrs[index], depth + 1);
                            continue; // the operands left belong to the caller
                        }
                    case 11: // return
                        return;
                    case 13: // hsbw
                        _point = _origin + new Vec2(Arg(0), 0);
                        _width = Arg(1);
                        break;
                    case 14: // endchar
                        Close();
                        _ended = true;
                        break;
                    case 21:
                        Move(Arg(0), Arg(1));
                        break;
                    case 22:
                        Move(Arg(0), 0);
                        break;
                    case 30: // vhcurveto
                        Curve(0, Arg(0), Arg(1), Arg(2), Arg(3), 0);
                        break;
                    case 31: // hvcurveto
                        Curve(Arg(0), 0, Arg(1), Arg(2), 0, Arg(3));
                        break;
                }
                _stack.Clear();
            }
        }

        private void Escape(int op, int depth)
        {
            switch (op)
            {
                case 0 or 1 or 2: // dotsection, vstem3, hstem3
                    break;
                case 6: // seac
                    _seac = (Arg(0), Arg(1), Arg(2), (int)Arg(3) & 0xFF, (int)Arg(4) & 0xFF);
                    _ended = true;
                    break;
                case 7: // sbw
                    _point = _origin + new Vec2(Arg(0), Arg(1));
                    _width = Arg(2);
                    break;
                case 12: // div
                    {
                        var b = Pop();
                        var a = Pop();
                        Push(b == 0 ? 0 : a / b);
                        return;
                    }
                case 16: // callothersubr
                    {
                        var number = (int)Pop();
                        var n = Math.Clamp((int)Pop(), 0, _stack.Count);
                        var args = _stack.GetRange(_stack.Count - n, n);
                        _stack.RemoveRange(_stack.Count - n, n);
                        OtherSubr(number, args);
                        return;
                    }
                case 17: // pop: a value from the PostScript stack
                    Push(_psStack.Count > 0 ? PopPs() : 0);
                    return;
                case 33: // setcurrentpoint
                    _point = _origin + new Vec2(Arg(0), Arg(1));
                    break;
            }
            _stack.Clear();
            _ = depth;
        }

        /// <summary>
        /// The standard OtherSubrs: 0–2 flex (the seven points collected become two curves), 3 hint replacement (the
        /// subroutine number comes back, so the charstring calls it); others hand their arguments back.
        /// </summary>
        private void OtherSubr(int number, List<double> args)
        {
            switch (number)
            {
                case 1:
                    _flexing = true;
                    _flex.Clear();
                    break;
                case 2:
                    _flex.Add(_point);
                    break;
                case 0 when args.Count >= 3:
                    _flexing = false;
                    if (_flex.Count >= 7)
                    {
                        // _flex[0] is the reference point; 1-3 and 4-6 are the two curves.
                        EnsureContour();
                        _current!.Segments.Add(new PathSegment(true, _flex[1], _flex[2], _flex[3]));
                        _current.Segments.Add(new PathSegment(true, _flex[4], _flex[5], _flex[6]));
                        _point = _flex[6];
                    }
                    // the charstring pops the end point and sets it as the current point
                    _psStack.Add(args[2]);
                    _psStack.Add(args[1]);
                    break;
                default:
                    for (var k = args.Count - 1; k >= 0; k--)
                        _psStack.Add(args[k]);
                    break;
            }
        }

        private double PopPs()
        {
            var v = _psStack[^1];
            _psStack.RemoveAt(_psStack.Count - 1);
            return v;
        }

        private void Push(double v)
        {
            if (_stack.Count >= 24 * 4)
                throw new InvalidDataException("A Type 1 glyph overflows its stack.");
            _stack.Add(v);
        }

        private double Pop()
        {
            if (_stack.Count == 0)
                return 0;
            var v = _stack[^1];
            _stack.RemoveAt(_stack.Count - 1);
            return v;
        }

        private double Arg(int i) => i < _stack.Count ? _stack[i] : 0;

        private void Move(double dx, double dy)
        {
            _point += new Vec2(dx, dy);
            if (_flexing)
                return; // flex points are collected by OtherSubr 2
            Close();
            _current = new PathContour(_point);
        }

        private void EnsureContour()
        {
            if (_current is null)
            {
                _current = new PathContour(_point);
            }
        }

        private void Line(double dx, double dy)
        {
            EnsureContour();
            _point += new Vec2(dx, dy);
            _current!.Segments.Add(new PathSegment(false, default, default, _point));
        }

        private void Curve(double dx1, double dy1, double dx2, double dy2, double dx3, double dy3)
        {
            EnsureContour();
            var c1 = _point + new Vec2(dx1, dy1);
            var c2 = c1 + new Vec2(dx2, dy2);
            _point = c2 + new Vec2(dx3, dy3);
            _current!.Segments.Add(new PathSegment(true, c1, c2, _point));
        }

        private void Close()
        {
            if (_current is { Segments.Count: > 0 })
                _contours.Add(_current);
            _current = null;
        }
    }
}

/// <summary>Writes Filee's own Type 1 fonts from outlines.</summary>
internal static class Type1Writer
{
    /// <summary>
    /// Writes a Type 1 font resource named <paramref name="name"/>: glyph <c>/aN</c> for code N of
    /// <paramref name="glyphs"/> (outlines in a 1000-unit em, already transformed), every other code .notdef.
    /// </summary>
    public static void Write(PsWriter w, string name, IReadOnlyDictionary<int, (List<PathContour> Contours, double Width)> glyphs)
    {
        double x0 = 0, y0 = 0, x1 = 0, y1 = 0;
        var any = false;
        foreach (var (contours, _) in glyphs.Values)
        {
            foreach (var contour in contours)
            {
                foreach (var p in Points(contour))
                {
                    if (!any)
                    {
                        (x0, y0, x1, y1) = (p.X, p.Y, p.X, p.Y);
                        any = true;
                    }
                    x0 = Math.Min(x0, p.X);
                    y0 = Math.Min(y0, p.Y);
                    x1 = Math.Max(x1, p.X);
                    y1 = Math.Max(y1, p.Y);
                }
            }
        }

        w.Line($"%%BeginResource: font {name}");
        w.Line("12 dict begin");
        w.Line($"/FontName /{name} def /FontType 1 def /PaintType 0 def");
        w.Line("/FontMatrix [0.001 0 0 0.001 0 0] readonly def");
        w.Line(FormattableString.Invariant($"/FontBBox [{Math.Floor(x0)} {Math.Floor(y0)} {Math.Ceiling(x1)} {Math.Ceiling(y1)}] readonly def"));
        w.Line("/Encoding 256 array 0 1 255 {1 index exch /.notdef put} for");
        foreach (var code in glyphs.Keys.Order())
            w.Token($"dup {code} /a{code} put");
        w.Line("readonly def");
        w.Line("currentdict end");
        w.Line("currentfile eexec");

        var plain = new MemoryStream();
        void Text(string s) => plain.Write(System.Text.Encoding.ASCII.GetBytes(s));
        Text("dup /Private 8 dict dup begin\n");
        Text("/RD {string currentfile exch readstring pop} executeonly def\n");
        Text("/ND {noaccess def} executeonly def\n");
        Text("/NP {noaccess put} executeonly def\n");
        Text("/MinFeature {16 16} noaccess def\n/password 5839 def\n/BlueValues [] noaccess def\n");
        Text("/Subrs 0 array ND\n");
        Text($"2 index /CharStrings {glyphs.Count + 1} dict dup begin\n");
        void Glyph(string glyphName, byte[] charString)
        {
            var encrypted = Type1Program.Encrypt(charString, 4330, 4);
            Text($"/{glyphName} {encrypted.Length} RD ");
            plain.Write(encrypted);
            Text(" ND\n");
        }
        Glyph(".notdef", CharString([], 0));
        foreach (var (code, glyph) in glyphs.OrderBy(g => g.Key))
            Glyph($"a{code}", CharString(glyph.Contours, glyph.Width));
        Text("end\nend\nreadonly put\nnoaccess put\ndup /FontName get exch definefont pop\nmark currentfile closefile\n");

        var cipher = Type1Program.Encrypt(plain.ToArray(), 55665, 4);
        // Hex, 64 digits a line (the first four characters tell eexec that the data is hex).
        var hex = Convert.ToHexStringLower(cipher);
        for (var i = 0; i < hex.Length; i += 64)
            w.Line(hex.Substring(i, Math.Min(64, hex.Length - i)));
        for (var i = 0; i < 8; i++)
            w.Line(new string('0', 64));
        w.Line("cleartomark");
        w.Line("%%EndResource");
    }

    private static IEnumerable<Vec2> Points(PathContour contour)
    {
        yield return contour.Start;
        foreach (var s in contour.Segments)
        {
            if (s.IsCurve)
            {
                yield return s.C1;
                yield return s.C2;
            }
            yield return s.End;
        }
    }

    /// <summary>A charstring for <paramref name="contours"/> (coordinates rounded to whole units, no drift).</summary>
    internal static byte[] CharString(List<PathContour> contours, double width)
    {
        var code = new List<byte>(64);
        Number(code, 0);
        Number(code, (int)Math.Round(width));
        code.Add(13); // hsbw
        int x = 0, y = 0;
        foreach (var contour in contours)
        {
            var (sx, sy) = Round(contour.Start);
            Number(code, sx - x);
            Number(code, sy - y);
            code.Add(21); // rmoveto
            (x, y) = (sx, sy);
            foreach (var segment in contour.Segments)
            {
                if (segment.IsCurve)
                {
                    var (ax, ay) = Round(segment.C1);
                    var (bx, by) = Round(segment.C2);
                    var (cx, cy) = Round(segment.End);
                    Number(code, ax - x);
                    Number(code, ay - y);
                    Number(code, bx - ax);
                    Number(code, by - ay);
                    Number(code, cx - bx);
                    Number(code, cy - by);
                    code.Add(8); // rrcurveto
                    (x, y) = (cx, cy);
                }
                else
                {
                    var (ex, ey) = Round(segment.End);
                    if (ex == x && ey == y)
                        continue;
                    Number(code, ex - x);
                    Number(code, ey - y);
                    code.Add(5); // rlineto
                    (x, y) = (ex, ey);
                }
            }
            code.Add(9); // closepath
        }
        code.Add(14); // endchar
        return [.. code];
    }

    private static (int X, int Y) Round(Vec2 p) =>
        ((int)Math.Round(Math.Clamp(p.X, -1e6, 1e6)), (int)Math.Round(Math.Clamp(p.Y, -1e6, 1e6)));

    private static void Number(List<byte> code, int v)
    {
        switch (v)
        {
            case >= -107 and <= 107:
                code.Add((byte)(v + 139));
                break;
            case >= 108 and <= 1131:
                v -= 108;
                code.Add((byte)(247 + (v >> 8)));
                code.Add((byte)(v & 0xFF));
                break;
            case >= -1131 and <= -108:
                v = -v - 108;
                code.Add((byte)(251 + (v >> 8)));
                code.Add((byte)(v & 0xFF));
                break;
            default:
                code.Add(255);
                code.Add((byte)(v >> 24));
                code.Add((byte)(v >> 16));
                code.Add((byte)(v >> 8));
                code.Add((byte)v);
                break;
        }
    }

    /// <summary>The contours transformed by <paramref name="m"/> (a FontMatrix) into a 1000-unit em.</summary>
    public static List<PathContour> ToThousandUnits(List<PathContour> contours, double[] m)
    {
        Vec2 T(Vec2 p) => new((m[0] * p.X + m[2] * p.Y + m[4]) * 1000, (m[1] * p.X + m[3] * p.Y + m[5]) * 1000);
        var result = new List<PathContour>(contours.Count);
        foreach (var contour in contours)
        {
            var moved = new PathContour(T(contour.Start));
            foreach (var s in contour.Segments)
                moved.Segments.Add(new PathSegment(s.IsCurve, T(s.C1), T(s.C2), T(s.End)));
            result.Add(moved);
        }
        return result;
    }
}
