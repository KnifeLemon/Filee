// PDF colour spaces (PDF 32000 8.6) as the PostScript writer needs them: every colour becomes a device colour (gray,
// RGB or CMYK) that PostScript sets with setgray, setrgbcolor or setcmykcolor. ICC-based spaces are drawn in the
// device space of their number of components, as PDF viewers do without colour management; CalGray and CalRGB as
// gray and RGB; Lab is converted to sRGB; Indexed looks its colours up; Separation and DeviceN run their tint
// transform (a PDF function) into the alternate space. Pictures keep device and Indexed spaces in PostScript, and a
// one-component Separation picture becomes an Indexed one whose table holds the converted tints.

using PdfSharp.Pdf;

namespace Filee.Engines.Pdf.PostScript;

/// <summary>The device spaces PostScript colours are set in.</summary>
internal enum DeviceKind
{
    Gray,
    Rgb,
    Cmyk,
}

/// <summary>A colour in a device space; unused components are 0.</summary>
internal readonly record struct DeviceColor(DeviceKind Kind, double C0, double C1 = 0, double C2 = 0, double C3 = 0)
{
    public static DeviceColor Black => new(DeviceKind.Gray, 0);

    public int Components => Kind switch { DeviceKind.Gray => 1, DeviceKind.Rgb => 3, _ => 4 };

    public double this[int i] => i switch { 0 => C0, 1 => C1, 2 => C2, _ => C3 };

    /// <summary>The PostScript that sets the colour.</summary>
    public string Code
    {
        get
        {
            static string F(double v) => PsWriter.Format(Math.Clamp(v, 0, 1));
            return Kind switch
            {
                DeviceKind.Gray => $"{F(C0)} setgray",
                DeviceKind.Rgb => $"{F(C0)} {F(C1)} {F(C2)} setrgbcolor",
                _ => $"{F(C0)} {F(C1)} {F(C2)} {F(C3)} setcmykcolor",
            };
        }
    }

    /// <summary>The colour as RGB (CMYK and gray converted the simple way PDF viewers do).</summary>
    public DeviceColor ToRgb() => Kind switch
    {
        DeviceKind.Gray => new(DeviceKind.Rgb, C0, C0, C0),
        DeviceKind.Cmyk => new(DeviceKind.Rgb, (1 - C0) * (1 - C3), (1 - C1) * (1 - C3), (1 - C2) * (1 - C3)),
        _ => this,
    };

    public static string SpaceName(DeviceKind kind) => kind switch
    {
        DeviceKind.Gray => "DeviceGray",
        DeviceKind.Rgb => "DeviceRGB",
        _ => "DeviceCMYK",
    };
}

/// <summary>A colour space that turns its components into device colours.</summary>
internal abstract class PdfColorSpace
{
    private const int MaxDepth = 6;

    public abstract int Components { get; }

    /// <summary>The device space a picture in this space is drawn in as it is, or null when it must be converted.</summary>
    public virtual DeviceKind? Device => null;

    /// <summary>The initial colour (PDF 32000 table 73: black, or tint 1 for Separation and DeviceN).</summary>
    public virtual double[] Initial => new double[Components];

    /// <summary>The default Decode range of component <paramref name="i"/> of a picture with <paramref name="bits"/> per component.</summary>
    public virtual (double Low, double High) DecodeRange(int i, int bits) => (0, 1);

    public abstract DeviceColor ToDevice(ReadOnlySpan<double> components);

    public static readonly PdfColorSpace Gray = new DeviceSpace(DeviceKind.Gray);
    public static readonly PdfColorSpace Rgb = new DeviceSpace(DeviceKind.Rgb);
    public static readonly PdfColorSpace Cmyk = new DeviceSpace(DeviceKind.Cmyk);

    /// <summary>
    /// Reads a colour space: a name (DeviceRGB, G, …, or a name of the resources' ColorSpace dictionary) or an array.
    /// </summary>
    /// <exception cref="InvalidDataException">The space is damaged or one Filee doesn't know.</exception>
    public static PdfColorSpace Read(object? item, PdfDictionary? resources, int depth = 0)
    {
        if (depth > MaxDepth)
            throw new InvalidDataException("Colour spaces are nested too deeply.");
        switch (item)
        {
            case PdfContentLexer.Name name:
                return Named(name.Value, resources, depth);
            case string s:
                return Named(s, resources, depth);
            case PdfItem pdf:
                var resolved = PdfObjects.Resolve(pdf);
                if (resolved is PdfName n)
                    return Named(n.Value.TrimStart('/'), resources, depth);
                if (resolved is PdfArray array)
                    return FromArray(array, resources, depth);
                break;
            case List<object?> inlineArray:
                return FromInlineArray(inlineArray, resources, depth);
        }
        throw new InvalidDataException("A colour space is missing or damaged.");
    }

    private static PdfColorSpace Named(string name, PdfDictionary? resources, int depth)
    {
        switch (name)
        {
            case "DeviceGray" or "G" or "CalGray":
                return Gray;
            case "DeviceRGB" or "RGB" or "CalRGB":
                return Rgb;
            case "DeviceCMYK" or "CMYK":
                return Cmyk;
            case "Pattern":
                return new PatternSpace(null);
        }
        if (PdfObjects.Dictionary(resources, "/ColorSpace") is { } spaces && PdfObjects.Get(spaces, "/" + name) is { } item)
            return Read(item, resources: null, depth + 1);
        throw new InvalidDataException($"The colour space '{name}' isn't in the resources.");
    }

    private static PdfColorSpace FromArray(PdfArray array, PdfDictionary? resources, int depth)
    {
        if (array.Elements.Count == 0)
            throw new InvalidDataException("A colour space array is empty.");
        var family = PdfObjects.Name(array.Elements[0]);
        PdfItem? At(int i) => i < array.Elements.Count ? PdfObjects.Resolve(array.Elements[i]) : null;
        switch (family)
        {
            case "DeviceGray" or "CalGray" or "G":
                return Gray;
            case "DeviceRGB" or "CalRGB" or "RGB":
                return Rgb;
            case "DeviceCMYK" or "CMYK":
                return Cmyk;
            case "ICCBased":
                {
                    var stream = At(1) as PdfDictionary;
                    var n = (int)PdfObjects.Number(stream, "/N", 0);
                    if (n == 0 && PdfObjects.Get(stream, "/Alternate") is { } alternate)
                        return Read(alternate, resources, depth + 1);
                    return n switch
                    {
                        1 => Gray,
                        3 => Rgb,
                        4 => Cmyk,
                        _ => throw new InvalidDataException($"An ICC profile has {n} components."),
                    };
                }
            case "Lab":
                return Lab.Read(At(1) as PdfDictionary);
            case "Indexed" or "I":
                {
                    var baseSpace = Read(At(1), resources, depth + 1);
                    var hival = (int)(PdfObjects.Number(At(2)) ?? -1);
                    var table = PdfObjects.StringOrStream(At(3), 4 * 256 + 16);
                    return Indexed.Create(baseSpace, hival, table);
                }
            case "Separation":
                {
                    var name = PdfObjects.Name(At(1));
                    var alternate = Read(At(2), resources, depth + 1);
                    return new Tinted(name is "None" ? null : [name ?? ""], alternate, name is "All" or "None" ? null : PdfFunction.Parse(At(3)), name is "None");
                }
            case "DeviceN":
                {
                    var names = (At(1) as PdfArray)?.Elements.Select(e => PdfObjects.Name(e) ?? "").ToArray()
                        ?? throw new InvalidDataException("A DeviceN space has no colorants.");
                    if (names.Length is 0 or > 32)
                        throw new InvalidDataException("A DeviceN space has an unusual number of colorants.");
                    var alternate = Read(At(2), resources, depth + 1);
                    var none = names.All(n => n == "None");
                    return new Tinted(names, alternate, PdfFunction.Parse(At(3)), none);
                }
            case "Pattern":
                return new PatternSpace(array.Elements.Count > 1 ? Read(At(1), resources, depth + 1) : null);
        }
        throw new InvalidDataException($"The colour space '{family}' isn't supported.");
    }

    /// <summary>An inline image's colour space array, e.g. <c>[/I /RGB 3 &lt;…&gt;]</c> or a name of the resources.</summary>
    private static PdfColorSpace FromInlineArray(List<object?> array, PdfDictionary? resources, int depth)
    {
        if (array.Count >= 4 && array[0] is PdfContentLexer.Name { Value: "I" or "Indexed" })
        {
            var baseSpace = Read(array[1], resources, depth + 1);
            var hival = array[2] is double h ? (int)h : -1;
            var table = array[3] switch
            {
                byte[] bytes => bytes,
                PdfContentLexer.Name name when PdfObjects.Dictionary(resources, "/ColorSpace") is null => throw new InvalidDataException($"'{name.Value}' isn't a lookup table."),
                _ => null,
            };
            return Indexed.Create(baseSpace, hival, table);
        }
        throw new InvalidDataException("An inline image has a colour space Filee can't read.");
    }

    private sealed class DeviceSpace(DeviceKind kind) : PdfColorSpace
    {
        public override int Components => kind switch { DeviceKind.Gray => 1, DeviceKind.Rgb => 3, _ => 4 };

        public override DeviceKind? Device => kind;

        public override double[] Initial => kind == DeviceKind.Cmyk ? [0, 0, 0, 1] : new double[Components];

        public override DeviceColor ToDevice(ReadOnlySpan<double> c) => kind switch
        {
            DeviceKind.Gray => new(kind, At(c, 0)),
            DeviceKind.Rgb => new(kind, At(c, 0), At(c, 1), At(c, 2)),
            _ => new(kind, At(c, 0), At(c, 1), At(c, 2), At(c, 3)),
        };
    }

    private static double At(ReadOnlySpan<double> c, int i) => i < c.Length ? Math.Clamp(c[i], 0, 1) : 0;

    /// <summary>CIE L*a*b* with its white point, converted to sRGB.</summary>
    private sealed class Lab : PdfColorSpace
    {
        private double _xw = 0.9505, _yw = 1, _zw = 1.089;
        private double[] _range = [-100, 100, -100, 100];

        public override int Components => 3;

        public override (double Low, double High) DecodeRange(int i, int bits) =>
            i == 0 ? (0, 100) : (_range[2 * (i - 1)], _range[2 * (i - 1) + 1]);

        public static Lab Read(PdfDictionary? dictionary)
        {
            var white = PdfObjects.Numbers(dictionary, "/WhitePoint") ?? [0.9505, 1, 1.089];
            var range = PdfObjects.Numbers(dictionary, "/Range") ?? [-100, 100, -100, 100];
            if (white.Length < 3 || range.Length < 4)
                throw new InvalidDataException("A Lab colour space is damaged.");
            return new Lab { _xw = white[0], _yw = white[1], _zw = white[2], _range = range };
        }

        public override DeviceColor ToDevice(ReadOnlySpan<double> c)
        {
            double l = c.Length > 0 ? c[0] : 0, a = c.Length > 1 ? c[1] : 0, b = c.Length > 2 ? c[2] : 0;
            var fy = (l + 16) / 116;
            var fx = fy + a / 500;
            var fz = fy - b / 200;
            static double G(double t) => t >= 6.0 / 29 ? t * t * t : 108.0 / 841 * (t - 4.0 / 29);
            double x = _xw * G(fx), y = _yw * G(fy), z = _zw * G(fz);
            var r = 3.2406 * x - 1.5372 * y - 0.4986 * z;
            var g = -0.9689 * x + 1.8758 * y + 0.0415 * z;
            var bl = 0.0557 * x - 0.2040 * y + 1.0570 * z;
            static double Gamma(double v) => v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(Math.Max(v, 0), 1 / 2.4) - 0.055;
            return new(DeviceKind.Rgb, Math.Clamp(Gamma(r), 0, 1), Math.Clamp(Gamma(g), 0, 1), Math.Clamp(Gamma(bl), 0, 1));
        }
    }

    /// <summary>A palette of up to 256 colours of a base space.</summary>
    public sealed class Indexed : PdfColorSpace
    {
        private Indexed(PdfColorSpace baseSpace, int hival, byte[] table)
        {
            Base = baseSpace;
            High = hival;
            Table = table;
        }

        public PdfColorSpace Base { get; }

        public int High { get; }

        /// <summary>(High + 1) × base components bytes.</summary>
        public byte[] Table { get; }

        public override int Components => 1;

        public override (double Low, double High) DecodeRange(int i, int bits) => (0, (1 << bits) - 1);

        public static Indexed Create(PdfColorSpace baseSpace, int hival, byte[]? table)
        {
            if (hival is < 0 or > 255 || table is null || baseSpace is Indexed or PatternSpace)
                throw new InvalidDataException("An Indexed colour space is damaged.");
            var length = (hival + 1) * baseSpace.Components;
            if (table.Length < length)
                table = [.. table, .. new byte[length - table.Length]];
            return new Indexed(baseSpace, hival, table[..length]);
        }

        public override DeviceColor ToDevice(ReadOnlySpan<double> c)
        {
            var index = Math.Clamp((int)Math.Round(c.Length > 0 ? c[0] : 0), 0, High);
            return Base.ToDevice(Entry(index));
        }

        /// <summary>The base-space components of entry <paramref name="index"/>.</summary>
        public double[] Entry(int index)
        {
            var n = Base.Components;
            var components = new double[n];
            for (var j = 0; j < n; j++)
            {
                var (low, high) = Base.DecodeRange(j, 8);
                components[j] = low + Table[index * n + j] / 255.0 * (high - low);
            }
            return components;
        }
    }

    /// <summary>Separation and DeviceN: tints through a function into an alternate space.</summary>
    public sealed class Tinted(string[]? names, PdfColorSpace alternate, PdfFunction? tint, bool none) : PdfColorSpace
    {
        public override int Components => names?.Length ?? 1;

        public override double[] Initial => [.. Enumerable.Repeat(1.0, Components)];

        /// <summary>The colorant None: nothing is painted.</summary>
        public bool PaintsNothing => none;

        public override DeviceColor ToDevice(ReadOnlySpan<double> c)
        {
            if (tint is null)
                return new(DeviceKind.Gray, 1 - At(c, 0)); // the colorant All: every plate, gray on a composite page
            Span<double> output = stackalloc double[Math.Max(alternate.Components, tint.Outputs)];
            tint.Evaluate(c, output);
            return alternate.ToDevice(output[..alternate.Components]);
        }
    }

    /// <summary>The Pattern space; uncolored patterns take their colour in <see cref="Underlying"/>.</summary>
    public sealed class PatternSpace(PdfColorSpace? underlying) : PdfColorSpace
    {
        public PdfColorSpace? Underlying { get; } = underlying;

        public override int Components => Underlying?.Components ?? 0;

        public override DeviceColor ToDevice(ReadOnlySpan<double> c) => Underlying?.ToDevice(c) ?? DeviceColor.Black;
    }
}
