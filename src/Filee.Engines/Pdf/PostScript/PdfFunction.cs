// PDF functions (PDF 32000 7.10): sampled (type 0), exponential (2), stitching (3) and PostScript calculator (4)
// functions, evaluated in Filee for colour conversion (tint transforms of Separation and DeviceN colours, Indexed
// tables) and written into PostScript for shadings. PostScript has types 0, 2 and 3; a calculator function, a sampled
// one whose table is too large for a string, or one evaluated through a colour space is written as a sampled function.
// Limits: sample tables of at most MaxSamples entries, stitching nested MaxDepth deep, calculator programs of at most
// MaxProgram operators and MaxSteps steps per evaluation.

using PdfSharp.Pdf;

namespace Filee.Engines.Pdf.PostScript;

/// <summary>A function of m inputs and n outputs.</summary>
internal abstract class PdfFunction
{
    private const int MaxDepth = 8;
    private const long MaxSamples = 4L * 1024 * 1024;

    protected double[] Domain { get; init; } = [0, 1];

    protected double[]? Range { get; init; }

    public int Inputs => Domain.Length / 2;

    public abstract int Outputs { get; }

    /// <summary>Evaluates the function; inputs are clipped to the domain and outputs to the range.</summary>
    public void Evaluate(ReadOnlySpan<double> input, Span<double> output)
    {
        Span<double> x = stackalloc double[Math.Max(1, Inputs)];
        for (var i = 0; i < Inputs; i++)
            x[i] = Math.Clamp(i < input.Length ? input[i] : 0, Domain[2 * i], Domain[2 * i + 1]);
        output.Clear();
        Compute(x[..Inputs], output);
        if (Range is not null)
        {
            for (var j = 0; j < Math.Min(output.Length, Range.Length / 2); j++)
                output[j] = Math.Clamp(output[j], Range[2 * j], Range[2 * j + 1]);
        }
    }

    protected abstract void Compute(ReadOnlySpan<double> x, Span<double> y);

    /// <summary>Reads a function, or an array of single-output functions (one per output).</summary>
    /// <exception cref="InvalidDataException">The function is damaged or beyond the limits.</exception>
    public static PdfFunction Parse(PdfItem? item, int depth = 0)
    {
        if (depth > MaxDepth)
            throw new InvalidDataException("PDF functions are nested too deeply.");
        item = PdfObjects.Resolve(item);
        if (item is PdfArray array)
        {
            if (array.Elements.Count is 0 or > 32)
                throw new InvalidDataException("An array of functions is empty or too long.");
            return new Combined([.. array.Elements.Select(e => Parse(e, depth + 1))]);
        }
        if (item is not PdfDictionary dictionary)
            throw new InvalidDataException("A function is missing.");
        var domain = PdfObjects.Numbers(dictionary, "/Domain") ?? throw new InvalidDataException("A function has no domain.");
        if (domain.Length is 0 or > 16 || domain.Length % 2 != 0)
            throw new InvalidDataException("A function has an unusual domain.");
        var range = PdfObjects.Numbers(dictionary, "/Range");
        if (range is not null && (range.Length > 64 || range.Length % 2 != 0))
            throw new InvalidDataException("A function has an unusual range.");
        return (int)PdfObjects.Number(dictionary, "/FunctionType", -1) switch
        {
            0 => Sampled.Read(dictionary, domain, range ?? throw new InvalidDataException("A sampled function has no range.")),
            2 => Exponential.Read(dictionary, domain, range),
            3 => Stitching.Read(dictionary, domain, range, depth),
            4 => Calculator.Read(dictionary, domain, range ?? throw new InvalidDataException("A calculator function has no range.")),
            var type => throw new InvalidDataException($"Function type {type} doesn't exist."),
        };
    }

    /// <summary>
    /// Writes the function as a PostScript function dictionary: exponential and stitching functions as they are, the
    /// others sampled.
    /// </summary>
    public virtual void Write(PsWriter w) => WriteSampled(w, Inputs, Outputs, Domain, Evaluate);

    /// <summary>
    /// Writes <paramref name="evaluate"/> as a sampled PostScript function (type 0) over <paramref name="domain"/>, 16
    /// bits per sample: 256 samples for one input, fewer per input for more, within a 64 KB string.
    /// </summary>
    public static void WriteSampled(PsWriter w, int inputs, int outputs, double[] domain, SpanAction evaluate)
    {
        var perInput = inputs switch
        {
            1 => 256,
            2 => 48,
            3 => 12,
            _ => 5,
        };
        var total = (int)Math.Pow(perInput, inputs);
        if ((long)total * outputs * 2 > 65_000)
            throw new InvalidDataException("A function has too many outputs to be written as samples.");
        var values = new double[total * outputs];
        Span<double> x = stackalloc double[inputs];
        Span<double> y = stackalloc double[outputs];
        var index = new int[inputs];
        for (var n = 0; n < total; n++)
        {
            var rest = n;
            for (var i = 0; i < inputs; i++)
            {
                index[i] = rest % perInput;
                rest /= perInput;
                x[i] = domain[2 * i] + (domain[2 * i + 1] - domain[2 * i]) * index[i] / (perInput - 1);
            }
            y.Clear();
            evaluate(x, y);
            for (var j = 0; j < outputs; j++)
                values[n * outputs + j] = double.IsFinite(y[j]) ? y[j] : 0;
        }
        var range = new double[outputs * 2];
        for (var j = 0; j < outputs; j++)
        {
            double low = double.MaxValue, high = double.MinValue;
            for (var n = 0; n < total; n++)
            {
                low = Math.Min(low, values[n * outputs + j]);
                high = Math.Max(high, values[n * outputs + j]);
            }
            if (high - low < 1e-9)
                high = low + 1e-6;
            range[2 * j] = low;
            range[2 * j + 1] = high;
        }
        var data = new byte[total * outputs * 2];
        for (var n = 0; n < total; n++)
        {
            for (var j = 0; j < outputs; j++)
            {
                var t = (values[n * outputs + j] - range[2 * j]) / (range[2 * j + 1] - range[2 * j]);
                var v = (int)Math.Round(Math.Clamp(t, 0, 1) * 65535);
                data[(n * outputs + j) * 2] = (byte)(v >> 8);
                data[(n * outputs + j) * 2 + 1] = (byte)v;
            }
        }
        w.Token("<<").Name("FunctionType").Number(0).Name("Domain").Array(domain.AsSpan(0, inputs * 2))
            .Name("Range").Array(range).Name("Size").Array(Enumerable.Repeat((double)perInput, inputs).ToArray())
            .Name("BitsPerSample").Number(16).Name("DataSource").Ascii85(data, literal: true).Token(">>");
    }

    public delegate void SpanAction(ReadOnlySpan<double> x, Span<double> y);

    /// <summary>One function per output, for an array of functions.</summary>
    private sealed class Combined : PdfFunction
    {
        private readonly PdfFunction[] _parts;

        public Combined(PdfFunction[] parts)
        {
            _parts = parts;
            Domain = parts[0].Domain;
        }

        public override int Outputs => _parts.Sum(p => p.Outputs);

        protected override void Compute(ReadOnlySpan<double> x, Span<double> y)
        {
            var at = 0;
            Span<double> one = stackalloc double[32];
            foreach (var part in _parts)
            {
                part.Evaluate(x, one[..part.Outputs]);
                for (var j = 0; j < part.Outputs && at < y.Length; j++)
                    y[at++] = one[j];
            }
        }

        public override void Write(PsWriter w)
        {
            // PostScript shadings take an array of functions too.
            w.Token("[");
            foreach (var part in _parts)
                part.Write(w);
            w.Token("]");
        }
    }

    private sealed class Sampled : PdfFunction
    {
        private int[] _size = [];
        private double[] _encode = [];
        private double[] _decode = [];
        private double[] _samples = [];
        private int _outputs;

        public override int Outputs => _outputs;

        public static Sampled Read(PdfDictionary dictionary, double[] domain, double[] range)
        {
            var m = domain.Length / 2;
            var n = range.Length / 2;
            var size = PdfObjects.Numbers(dictionary, "/Size")?.Select(s => (int)s).ToArray();
            if (size is null || size.Length != m || size.Any(s => s < 1))
                throw new InvalidDataException("A sampled function has an unusual size.");
            var count = size.Aggregate(1L, (a, s) => a * s) * n;
            if (count > MaxSamples || n == 0)
                throw new InvalidDataException("A sampled function has too many samples.");
            var bits = (int)PdfObjects.Number(dictionary, "/BitsPerSample", 8);
            if (bits is not (1 or 2 or 4 or 8 or 12 or 16 or 24 or 32))
                throw new InvalidDataException("A sampled function has an unusual number of bits per sample.");
            var encode = PdfObjects.Numbers(dictionary, "/Encode") ?? [.. size.SelectMany(s => new double[] { 0, s - 1 })];
            var decode = PdfObjects.Numbers(dictionary, "/Decode") ?? range;
            if (encode.Length < 2 * m || decode.Length < 2 * n)
                throw new InvalidDataException("A sampled function has a short Encode or Decode array.");
            var data = dictionary.Stream is null ? [] : PdfStreams.Decode(dictionary, (count * bits + 7) / 8 + 16);
            var samples = new double[count];
            var max = (1L << bits) - 1;
            long bit = 0;
            for (long i = 0; i < count; i++, bit += bits)
            {
                long value = 0;
                for (var b = 0; b < bits; b++)
                {
                    var at = bit + b;
                    var byteIndex = at >> 3;
                    var on = byteIndex < data.Length && (data[byteIndex] >> (7 - (int)(at & 7)) & 1) != 0;
                    value = value << 1 | (on ? 1L : 0);
                }
                samples[i] = value / (double)max;
            }
            return new Sampled
            {
                Domain = domain,
                Range = range,
                _size = size,
                _encode = encode,
                _decode = decode,
                _samples = samples,
                _outputs = n,
            };
        }

        protected override void Compute(ReadOnlySpan<double> x, Span<double> y)
        {
            var m = Inputs;
            Span<double> e = stackalloc double[m];
            Span<int> low = stackalloc int[m];
            Span<double> frac = stackalloc double[m];
            for (var i = 0; i < m; i++)
            {
                var d0 = Domain[2 * i];
                var d1 = Domain[2 * i + 1];
                var t = d1 == d0 ? 0 : (x[i] - d0) / (d1 - d0);
                e[i] = Math.Clamp(_encode[2 * i] + t * (_encode[2 * i + 1] - _encode[2 * i]), 0, _size[i] - 1);
                low[i] = Math.Min((int)Math.Floor(e[i]), Math.Max(0, _size[i] - 2));
                frac[i] = _size[i] == 1 ? 0 : e[i] - low[i];
            }
            // Multilinear interpolation over the 2^m corners (m is small: at most 8 inputs).
            var corners = 1 << m;
            for (var j = 0; j < _outputs; j++)
            {
                double sum = 0;
                for (var corner = 0; corner < corners; corner++)
                {
                    double weight = 1;
                    long index = 0, stride = 1;
                    for (var i = 0; i < m; i++)
                    {
                        var up = (corner >> i & 1) == 1;
                        var position = Math.Min(low[i] + (up ? 1 : 0), _size[i] - 1);
                        weight *= up ? frac[i] : 1 - frac[i];
                        index += position * stride;
                        stride *= _size[i];
                    }
                    if (weight != 0)
                        sum += weight * _samples[index * _outputs + j];
                }
                y[j] = _decode[2 * j] + sum * (_decode[2 * j + 1] - _decode[2 * j]);
            }
        }
    }

    private sealed class Exponential : PdfFunction
    {
        private double[] _c0 = [0];
        private double[] _c1 = [1];
        private double _n;

        public override int Outputs => _c0.Length;

        public static Exponential Read(PdfDictionary dictionary, double[] domain, double[]? range)
        {
            var c0 = PdfObjects.Numbers(dictionary, "/C0") ?? [0];
            var c1 = PdfObjects.Numbers(dictionary, "/C1") ?? [1];
            if (c0.Length != c1.Length || c0.Length is 0 or > 32)
                throw new InvalidDataException("An exponential function has unusual C0 / C1 arrays.");
            return new Exponential { Domain = domain[..2], Range = range, _c0 = c0, _c1 = c1, _n = PdfObjects.Number(dictionary, "/N", 1) };
        }

        protected override void Compute(ReadOnlySpan<double> x, Span<double> y)
        {
            var p = Math.Pow(x[0], _n);
            if (!double.IsFinite(p))
                p = 0;
            for (var j = 0; j < Math.Min(y.Length, _c0.Length); j++)
                y[j] = _c0[j] + p * (_c1[j] - _c0[j]);
        }

        public override void Write(PsWriter w)
        {
            w.Token("<<").Name("FunctionType").Number(2).Name("Domain").Array(Domain).Name("C0").Array(_c0)
                .Name("C1").Array(_c1).Name("N").Number(_n);
            if (Range is not null)
                w.Name("Range").Array(Range);
            w.Token(">>");
        }
    }

    private sealed class Stitching : PdfFunction
    {
        private PdfFunction[] _functions = [];
        private double[] _bounds = [];
        private double[] _encode = [];

        public override int Outputs => _functions[0].Outputs;

        public static Stitching Read(PdfDictionary dictionary, double[] domain, double[]? range, int depth)
        {
            if (PdfObjects.Array(dictionary, "/Functions") is not { Elements.Count: > 0 and <= 256 } list)
                throw new InvalidDataException("A stitching function has no functions.");
            var functions = list.Elements.Select(f => Parse(f, depth + 1)).ToArray();
            var bounds = PdfObjects.Numbers(dictionary, "/Bounds") ?? [];
            var encode = PdfObjects.Numbers(dictionary, "/Encode") ?? [];
            if (bounds.Length != functions.Length - 1 || encode.Length != 2 * functions.Length
                || functions.Any(f => f.Outputs != functions[0].Outputs || f.Inputs != 1))
                throw new InvalidDataException("A stitching function is damaged.");
            return new Stitching { Domain = domain[..2], Range = range, _functions = functions, _bounds = bounds, _encode = encode };
        }

        protected override void Compute(ReadOnlySpan<double> x, Span<double> y)
        {
            var k = 0;
            while (k < _bounds.Length && x[0] >= _bounds[k])
                k++;
            var low = k == 0 ? Domain[0] : _bounds[k - 1];
            var high = k == _bounds.Length ? Domain[1] : _bounds[k];
            var t = high == low ? 0 : (x[0] - low) / (high - low);
            var mapped = _encode[2 * k] + t * (_encode[2 * k + 1] - _encode[2 * k]);
            _functions[k].Evaluate([mapped], y);
        }

        public override void Write(PsWriter w)
        {
            w.Token("<<").Name("FunctionType").Number(3).Name("Domain").Array(Domain).Name("Functions").Token("[");
            foreach (var function in _functions)
                function.Write(w);
            w.Token("]").Name("Bounds").Array(_bounds).Name("Encode").Array(_encode);
            if (Range is not null)
                w.Name("Range").Array(Range);
            w.Token(">>");
        }
    }

    /// <summary>A PostScript calculator function, run by Filee's own small interpreter.</summary>
    private sealed class Calculator : PdfFunction
    {
        private const int MaxProgram = 20_000;
        private const int MaxSteps = 100_000;
        private const int MaxStack = 100;

        private List<object> _program = [];

        public override int Outputs => Range!.Length / 2;

        public static Calculator Read(PdfDictionary dictionary, double[] domain, double[] range)
        {
            var code = dictionary.Stream is null ? [] : PdfStreams.Decode(dictionary, 1024 * 1024);
            var lexer = new PdfContentLexer(code);
            var count = 0;
            var tokens = new List<object>();
            while (lexer.Next() is { } token)
            {
                if (++count > MaxProgram)
                    throw new InvalidDataException("A calculator function is too long.");
                tokens.Add(token);
            }
            var at = 0;
            if (tokens.Count == 0 || tokens[0] is not PdfContentLexer.Operator { Name: "{" })
                throw new InvalidDataException("A calculator function doesn't start with '{'.");
            at++;
            var program = Block(tokens, ref at, 0);
            return new Calculator { Domain = domain, Range = range, _program = program };
        }

        /// <summary>The operators up to the matching '}'; numbers as double, nested blocks as lists.</summary>
        private static List<object> Block(List<object> tokens, ref int at, int depth)
        {
            if (depth > 64)
                throw new InvalidDataException("A calculator function is nested too deeply.");
            var block = new List<object>();
            while (at < tokens.Count)
            {
                var token = tokens[at++];
                switch (token)
                {
                    case double n:
                        block.Add(n);
                        break;
                    case PdfContentLexer.Operator { Name: "{" }:
                        block.Add(Block(tokens, ref at, depth + 1));
                        break;
                    case PdfContentLexer.Operator { Name: "}" }:
                        return block;
                    case PdfContentLexer.Operator op:
                        block.Add(op.Name);
                        break;
                    default:
                        throw new InvalidDataException("A calculator function has a token that isn't an operator.");
                }
            }
            throw new InvalidDataException("A calculator function isn't closed.");
        }

        protected override void Compute(ReadOnlySpan<double> x, Span<double> y)
        {
            var stack = new List<double>(16);
            foreach (var v in x)
                stack.Add(v);
            var steps = 0;
            Run(_program, stack, ref steps);
            var n = Outputs;
            for (var j = 0; j < n; j++)
            {
                var index = stack.Count - n + j;
                y[j] = index >= 0 ? stack[index] : 0;
            }
        }

        private static void Run(List<object> program, List<double> s, ref int steps)
        {
            for (var i = 0; i < program.Count; i++)
            {
                if (++steps > MaxSteps)
                    throw new InvalidDataException("A calculator function runs too long.");
                if (s.Count > MaxStack)
                    throw new InvalidDataException("A calculator function fills its stack.");
                switch (program[i])
                {
                    case double n:
                        s.Add(n);
                        continue;
                    case List<object> block when i + 1 < program.Count && program[i + 1] is "if":
                        if (Pop(s) != 0)
                            Run(block, s, ref steps);
                        i++;
                        continue;
                    case List<object> yes when i + 2 < program.Count && program[i + 1] is List<object> no && program[i + 2] is "ifelse":
                        Run(Pop(s) != 0 ? yes : no, s, ref steps);
                        i += 2;
                        continue;
                    case string op:
                        Operator(op, s);
                        continue;
                    default:
                        throw new InvalidDataException("A calculator function has a block without if or ifelse.");
                }
            }
        }

        private static double Pop(List<double> s)
        {
            if (s.Count == 0)
                throw new InvalidDataException("A calculator function empties its stack.");
            var v = s[^1];
            s.RemoveAt(s.Count - 1);
            return v;
        }

        private static void Operator(string op, List<double> s)
        {
            double a, b;
            switch (op)
            {
                case "add": b = Pop(s); a = Pop(s); s.Add(a + b); break;
                case "sub": b = Pop(s); a = Pop(s); s.Add(a - b); break;
                case "mul": b = Pop(s); a = Pop(s); s.Add(a * b); break;
                case "div": b = Pop(s); a = Pop(s); s.Add(b == 0 ? 0 : a / b); break;
                case "idiv": b = Pop(s); a = Pop(s); s.Add((long)b == 0 ? 0 : (long)a / (long)b); break;
                case "mod": b = Pop(s); a = Pop(s); s.Add((long)b == 0 ? 0 : (long)a % (long)b); break;
                case "neg": s.Add(-Pop(s)); break;
                case "abs": s.Add(Math.Abs(Pop(s))); break;
                case "ceiling": s.Add(Math.Ceiling(Pop(s))); break;
                case "floor": s.Add(Math.Floor(Pop(s))); break;
                case "round": s.Add(Math.Floor(Pop(s) + 0.5)); break;
                case "truncate": s.Add(Math.Truncate(Pop(s))); break;
                case "cvi": s.Add(Math.Truncate(Pop(s))); break;
                case "cvr": break;
                case "sqrt": s.Add(Math.Sqrt(Math.Max(0, Pop(s)))); break;
                case "sin": s.Add(Math.Sin(Pop(s) * Math.PI / 180)); break;
                case "cos": s.Add(Math.Cos(Pop(s) * Math.PI / 180)); break;
                case "atan":
                    b = Pop(s);
                    a = Pop(s);
                    var angle = Math.Atan2(a, b) * 180 / Math.PI;
                    s.Add(angle < 0 ? angle + 360 : angle);
                    break;
                case "exp": b = Pop(s); a = Pop(s); s.Add(Finite(Math.Pow(a, b))); break;
                case "ln": s.Add(Finite(Math.Log(Pop(s)))); break;
                case "log": s.Add(Finite(Math.Log10(Pop(s)))); break;
                case "eq": b = Pop(s); a = Pop(s); s.Add(a == b ? 1 : 0); break;
                case "ne": b = Pop(s); a = Pop(s); s.Add(a != b ? 1 : 0); break;
                case "gt": b = Pop(s); a = Pop(s); s.Add(a > b ? 1 : 0); break;
                case "ge": b = Pop(s); a = Pop(s); s.Add(a >= b ? 1 : 0); break;
                case "lt": b = Pop(s); a = Pop(s); s.Add(a < b ? 1 : 0); break;
                case "le": b = Pop(s); a = Pop(s); s.Add(a <= b ? 1 : 0); break;
                case "and": b = Pop(s); a = Pop(s); s.Add((long)a & (long)b); break;
                case "or": b = Pop(s); a = Pop(s); s.Add((long)a | (long)b); break;
                case "xor": b = Pop(s); a = Pop(s); s.Add((long)a ^ (long)b); break;
                case "not": a = Pop(s); s.Add(a == 0 ? 1 : a == 1 ? 0 : ~(long)a); break;
                case "bitshift":
                    b = Pop(s);
                    a = Pop(s);
                    var shift = (int)Math.Clamp(b, -63, 63);
                    s.Add(shift >= 0 ? (long)a << shift : (long)a >> -shift);
                    break;
                case "true": s.Add(1); break;
                case "false": s.Add(0); break;
                case "pop": Pop(s); break;
                case "dup": a = Pop(s); s.Add(a); s.Add(a); break;
                case "exch": b = Pop(s); a = Pop(s); s.Add(b); s.Add(a); break;
                case "copy":
                    {
                        var n = (int)Pop(s);
                        if (n < 0 || n > s.Count)
                            throw new InvalidDataException("A calculator function copies more than its stack holds.");
                        s.AddRange(s.GetRange(s.Count - n, n));
                        break;
                    }
                case "index":
                    {
                        var n = (int)Pop(s);
                        if (n < 0 || n >= s.Count)
                            throw new InvalidDataException("A calculator function indexes past its stack.");
                        s.Add(s[s.Count - 1 - n]);
                        break;
                    }
                case "roll":
                    {
                        var j = (int)Pop(s);
                        var n = (int)Pop(s);
                        if (n < 0 || n > s.Count)
                            throw new InvalidDataException("A calculator function rolls more than its stack holds.");
                        if (n > 0)
                        {
                            j = ((j % n) + n) % n;
                            var part = s.GetRange(s.Count - n, n);
                            for (var k = 0; k < n; k++)
                                s[s.Count - n + (k + j) % n] = part[k];
                        }
                        break;
                    }
                default:
                    throw new InvalidDataException($"'{op}' isn't an operator of calculator functions.");
            }
        }

        private static double Finite(double v) => double.IsFinite(v) ? v : 0;
    }
}
