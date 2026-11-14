// Builds PDFs object by object for the PostScript writer's tests: pages with hand-written content streams and the
// resources they draw (fonts of every kind, pictures, shadings, patterns, forms, annotations, optional content), so
// each feature of the translation can be compared with PDFium's rendering. Also writes a small Type 1 font program
// (cleartext part and eexec-encrypted charstrings, with subroutines, flex and a seac accent), since no Type 1 font
// is in the repository and PDFsharp writes none.

using System.Globalization;
using System.IO.Compression;
using System.Text;
using PdfSharp.Pdf;

namespace Filee.Engines.Tests;

internal sealed class PostScriptPdfBuilder : IDisposable
{
    public PdfDocument Document { get; } = new();

    public static PdfName Name(string name) => new("/" + name);

    public static PdfItem Number(double value) =>
        value == Math.Floor(value) && Math.Abs(value) < int.MaxValue ? new PdfInteger((int)value) : new PdfReal(value);

    public PdfArray Array(params PdfItem[] items) => new(Document, items);

    public PdfArray Numbers(params double[] values) => new(Document, [.. values.Select(Number)]);

    /// <summary>A direct dictionary.</summary>
    public PdfDictionary Dictionary(params (string Key, PdfItem Value)[] entries)
    {
        var dictionary = new PdfDictionary(Document);
        foreach (var (key, value) in entries)
            dictionary.Elements["/" + key] = value;
        return dictionary;
    }

    /// <summary>An indirect dictionary.</summary>
    public PdfDictionary Object(params (string Key, PdfItem Value)[] entries)
    {
        var dictionary = Dictionary(entries);
        Document.Internals.AddObject(dictionary);
        return dictionary;
    }

    /// <summary>An indirect stream, deflated unless <paramref name="raw"/>.</summary>
    public PdfDictionary Stream(byte[] data, bool raw = false, params (string Key, PdfItem Value)[] entries)
    {
        var dictionary = Dictionary(entries);
        if (raw)
        {
            dictionary.CreateStream(data);
        }
        else
        {
            dictionary.CreateStream(Deflate(data));
            dictionary.Elements["/Filter"] = Name("FlateDecode");
        }
        Document.Internals.AddObject(dictionary);
        return dictionary;
    }

    public PdfDictionary Stream(string content, params (string Key, PdfItem Value)[] entries) =>
        Stream(Encoding.Latin1.GetBytes(content), false, entries);

    public static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data);
        return output.ToArray();
    }

    /// <summary>Adds a page of <paramref name="width"/> × <paramref name="height"/> points drawing <paramref name="content"/>.</summary>
    public PdfPage Page(double width, double height, string content, PdfDictionary? resources = null)
    {
        var page = Document.AddPage();
        page.Elements["/MediaBox"] = Numbers(0, 0, width, height);
        page.Elements["/Resources"] = resources ?? Dictionary();
        page.Elements["/Contents"] = Stream(content).Reference!;
        return page;
    }

    /// <summary>Resources with the given sub-dictionaries (Font, XObject, …), each name → object.</summary>
    public PdfDictionary Resources(params (string Category, string Name, PdfDictionary Value)[] entries)
    {
        var resources = Dictionary();
        foreach (var group in entries.GroupBy(e => e.Category))
        {
            var dictionary = Dictionary();
            foreach (var (_, name, value) in group)
                dictionary.Elements["/" + name] = value.Reference is { } reference ? reference : value;
            resources.Elements["/" + group.Key] = dictionary;
        }
        return resources;
    }

    public PdfItem Ref(PdfDictionary dictionary) => dictionary.Reference ?? (PdfItem)dictionary;

    public string Save(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        Document.Save(path);
        return path;
    }

    public void Dispose() => Document.Dispose();

    public static string F(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    // ───────────────────────── fonts ─────────────────────────

    /// <summary>A font descriptor with an embedded program under <paramref name="fileKey"/>.</summary>
    public PdfDictionary Descriptor(string name, int flags, string fileKey, PdfDictionary program) =>
        Object(("Type", Name("FontDescriptor")), ("FontName", Name(name)), ("Flags", Number(flags)),
            ("FontBBox", Numbers(-200, -300, 1200, 1000)), ("ItalicAngle", Number(0)), ("Ascent", Number(900)),
            ("Descent", Number(-200)), ("CapHeight", Number(700)), ("StemV", Number(80)), (fileKey, program.Reference!));

    /// <summary>A simple font with widths for codes 32–255.</summary>
    public PdfDictionary SimpleFont(string subtype, string baseFont, PdfItem? encoding, PdfDictionary? descriptor, Func<int, double> width)
    {
        var font = Object(("Type", Name("Font")), ("Subtype", Name(subtype)), ("BaseFont", Name(baseFont)),
            ("FirstChar", Number(32)), ("LastChar", Number(255)),
            ("Widths", Numbers([.. Enumerable.Range(32, 224).Select(c => width(c))])));
        if (encoding is not null)
            font.Elements["/Encoding"] = encoding;
        if (descriptor is not null)
            font.Elements["/FontDescriptor"] = descriptor.Reference!;
        return font;
    }

    /// <summary>A Type 1 font program with glyphs /square (a subroutine), /bowl (curves), /flexed (flex), /A, /acute and /Aacute (seac).</summary>
    public static byte[] Type1Program(string fontName)
    {
        var glyphs = new Dictionary<string, string>
        {
            [".notdef"] = "0 500 hsbw endchar",
            ["space"] = "0 300 hsbw endchar",
            ["square"] = "50 600 hsbw 0 0 rmoveto 4 callsubr endchar",
            ["bowl"] = "40 640 hsbw 280 0 rmoveto 150 0 270 120 0 270 rrcurveto 0 150 -120 270 -270 0 rrcurveto "
                + "-150 0 -270 -120 0 -270 rrcurveto 0 -150 120 -270 270 0 rrcurveto closepath endchar",
            // a bar whose top edge is a flex: the reference point, then the seven points, then the end point
            ["flexed"] = "0 700 hsbw 100 0 rmoveto 500 0 rlineto 0 500 rlineto 1 callsubr "
                + "-250 0 rmoveto 2 callsubr 150 20 rmoveto 2 callsubr -100 0 rmoveto 2 callsubr -50 0 rmoveto 2 callsubr "
                + "-50 0 rmoveto 2 callsubr -100 0 rmoveto 2 callsubr -100 -20 rmoveto 2 callsubr "
                + "50 100 500 0 callsubr closepath endchar",
            ["A"] = "10 620 hsbw 0 0 rmoveto 300 700 rlineto 300 -700 rlineto -120 0 rlineto -180 450 rlineto -180 -450 rlineto closepath endchar",
            ["acute"] = "100 400 hsbw 0 780 rmoveto 120 160 rlineto 60 -20 rlineto -120 -160 rlineto closepath endchar",
            ["Aacute"] = "10 620 hsbw 100 160 40 65 194 seac",
        };
        string[] subrs =
        [
            "3 0 callothersubr pop pop setcurrentpoint return", // 0: end of flex
            "0 1 callothersubr return",                          // 1: start of flex
            "0 2 callothersubr return",                          // 2: a flex point
            "return",                                            // 3: hint replacement
            "500 0 rlineto 0 500 rlineto -500 0 rlineto closepath return", // 4: the square
        ];

        var plain = new MemoryStream();
        void Text(string s) => plain.Write(Encoding.ASCII.GetBytes(s));
        Text("dup /Private 9 dict dup begin\n/RD {string currentfile exch readstring pop} executeonly def\n");
        Text("/ND {noaccess def} executeonly def\n/NP {noaccess put} executeonly def\n");
        Text("/BlueValues [-20 0 700 720] def\n/MinFeature {16 16} def\n/password 5839 def\n/lenIV 4 def\n");
        Text($"/Subrs {subrs.Length} array\n");
        for (var i = 0; i < subrs.Length; i++)
        {
            var code = Encrypt(CharString(subrs[i]), 4330);
            Text($"dup {i} {code.Length} RD ");
            plain.Write(code);
            Text(" NP\n");
        }
        Text("ND\n");
        Text($"2 index /CharStrings {glyphs.Count} dict dup begin\n");
        foreach (var (name, program) in glyphs)
        {
            var code = Encrypt(CharString(program), 4330);
            Text($"/{name} {code.Length} RD ");
            plain.Write(code);
            Text(" ND\n");
        }
        Text("end\nend\nreadonly put\nnoaccess put\ndup /FontName get exch definefont pop\nmark currentfile closefile\n");

        var cleartext = new StringBuilder();
        cleartext.Append("%!PS-AdobeFont-1.0: ").Append(fontName).Append(" 001.000\n");
        cleartext.Append("11 dict begin\n/FontInfo 2 dict dup begin /FullName (Filee Test Type 1) readonly def end readonly def\n");
        cleartext.Append("/FontName /").Append(fontName).Append(" def\n/PaintType 0 def\n/FontType 1 def\n");
        cleartext.Append("/FontMatrix [0.001 0 0 0.001 0 0] readonly def\n/FontBBox {-10 -10 760 960} readonly def\n");
        cleartext.Append("/Encoding 256 array 0 1 255 {1 index exch /.notdef put} for\n");
        cleartext.Append("dup 32 /space put dup 65 /A put dup 66 /square put dup 67 /bowl put dup 68 /flexed put dup 69 /Aacute put dup 70 /acute put\n");
        cleartext.Append("readonly def\ncurrentdict end\ncurrentfile eexec\n");
        var encrypted = Encrypt(plain.ToArray(), 55665);
        var trailer = string.Concat(Enumerable.Repeat(new string('0', 64) + "\n", 8)) + "cleartomark\n";
        return [.. Encoding.ASCII.GetBytes(cleartext.ToString()), .. encrypted, .. Encoding.ASCII.GetBytes(trailer)];
    }

    /// <summary>The PDF FontFile stream of a Type 1 program (Length1–3 for the cleartext, binary and trailer parts).</summary>
    public PdfDictionary Type1File(byte[] program)
    {
        var eexec = program.AsSpan().IndexOf("eexec\n"u8) + 6;
        var trailer = program.AsSpan().LastIndexOf("0000000000"u8);
        trailer = program.AsSpan(0, trailer).LastIndexOf((byte)'\n') + 1;
        var stream = Stream(program, false, ("Length1", Number(eexec)), ("Length2", Number(trailer - eexec)), ("Length3", Number(program.Length - trailer)));
        return stream;
    }

    /// <summary>A Type 1 charstring from text: numbers and operator names.</summary>
    private static byte[] CharString(string program)
    {
        var code = new List<byte>();
        foreach (var token in program.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
            {
                code.AddRange(Num(value));
                continue;
            }
            code.AddRange(token switch
            {
                "hstem" => [1],
                "vstem" => [3],
                "rlineto" => [5],
                "rrcurveto" => [8],
                "closepath" => [9],
                "callsubr" => [10],
                "return" => [11],
                "hsbw" => [13],
                "endchar" => [14],
                "rmoveto" => [21],
                "seac" => [12, 6],
                "div" => [12, 12],
                "callothersubr" => [12, 16],
                "pop" => [12, 17],
                "setcurrentpoint" => [12, 33],
                _ => throw new ArgumentException($"Unknown charstring operator {token}."),
            });
        }
        return [.. code];
    }

    private static byte[] Num(int v) => v switch
    {
        >= -107 and <= 107 => [(byte)(v + 139)],
        >= 108 and <= 1131 => [(byte)(247 + ((v - 108) >> 8)), (byte)((v - 108) & 0xFF)],
        >= -1131 and <= -108 => [(byte)(251 + ((-v - 108) >> 8)), (byte)((-v - 108) & 0xFF)],
        _ => [255, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v],
    };

    private static byte[] Encrypt(byte[] plain, ushort key)
    {
        var cipher = new byte[plain.Length + 4];
        var r = key;
        for (var i = 0; i < cipher.Length; i++)
        {
            var p = i < 4 ? (byte)0 : plain[i - 4];
            var c = (byte)(p ^ (r >> 8));
            r = (ushort)((c + r) * 52845 + 22719);
            cipher[i] = c;
        }
        return cipher;
    }
}
