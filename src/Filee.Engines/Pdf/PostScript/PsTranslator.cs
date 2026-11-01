// Translates PDF content streams into PostScript, operator by operator (PDF 32000 8, 9): the graphics state (q / Q,
// cm, line style, ExtGState), paths and clipping, colours (each turned into a device colour, see PdfColorSpace),
// text (every glyph placed at its PDF position with xshow, see PsFonts), pictures (PsImages), shadings and patterns
// (PostScript LanguageLevel 3 shfill and makepattern), form XObjects, optional content and marked content.
//
// PDF keeps a fill and a stroke colour, PostScript one current colour: the translator remembers which colour the
// PostScript state holds (restored with it by grestore) and sets the right one before each painting operator.
// Only Filee's own procedures and operators are written; operands are numbers, strings and names Filee made, so no
// part of a content stream reaches PostScript as code.
//
// What PostScript can't show faithfully throws PsUntranslatableException, and the page becomes a picture:
// transparency (constant alpha below 1, soft masks, blend modes other than Normal), text used as a clipping path,
// pictures PostScript can't decode, fonts PsFonts refuses, and pages beyond the limits below.

using System.Text;
using PdfSharp.Pdf;
using static Filee.Engines.Pdf.PdfContentLexer;

namespace Filee.Engines.Pdf.PostScript;

/// <summary>A 2-D affine transformation [a b c d e f] in PDF's row-vector convention.</summary>
internal readonly record struct Mat(double A, double B, double C, double D, double E, double F)
{
    public static readonly Mat Identity = new(1, 0, 0, 1, 0, 0);

    public static Mat From(ReadOnlySpan<double> m) => m.Length >= 6 ? new(m[0], m[1], m[2], m[3], m[4], m[5]) : Identity;

    /// <summary>This transformation, then <paramref name="then"/>.</summary>
    public Mat Then(Mat then) => new(
        A * then.A + B * then.C, A * then.B + B * then.D,
        C * then.A + D * then.C, C * then.B + D * then.D,
        E * then.A + F * then.C + then.E, E * then.B + F * then.D + then.F);

    public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);

    public double Determinant => A * D - B * C;

    public double[] ToArray() => [A, B, C, D, E, F];
}

/// <summary>What one PostScript file shares while its pages are translated.</summary>
internal sealed class PsContext(PsFontSet fonts, OptionalContent optionalContent, CancellationToken cancellationToken)
{
    /// <summary>Operators a page may produce (forms drawn many times count each time).</summary>
    public const int MaxOperations = 4_000_000;

    /// <summary>Forms in forms (and patterns, glyphs, annotation appearances).</summary>
    public const int MaxDepth = 24;

    public PsFontSet Fonts { get; } = fonts;

    public OptionalContent OptionalContent { get; } = optionalContent;

    public CancellationToken CancellationToken { get; } = cancellationToken;

    /// <summary>Patterns defined on the current page (they live until the page's restore).</summary>
    public Dictionary<(PdfDictionary, Mat), string> Patterns { get; } = [];

    public int Operations { get; set; }

    public void StartPage()
    {
        Patterns.Clear();
        Operations = 0;
    }
}

/// <summary>Which optional content (layers) is shown: the document's default configuration (PDF 32000 8.11.4).</summary>
internal sealed class OptionalContent
{
    private readonly HashSet<PdfDictionary> _off = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<PdfDictionary> _on = new(ReferenceEqualityComparer.Instance);
    private bool _baseOff;

    public static OptionalContent Read(PdfDictionary catalog)
    {
        var content = new OptionalContent();
        var defaults = PdfObjects.Dictionary(PdfObjects.Dictionary(catalog, "/OCProperties"), "/D");
        if (defaults is null)
            return content;
        content._baseOff = PdfObjects.Name(defaults, "/BaseState") == "OFF";
        foreach (var item in PdfObjects.Array(defaults, "/OFF")?.Elements ?? Enumerable.Empty<PdfItem>())
        {
            if (PdfObjects.Resolve(item) is PdfDictionary group)
                content._off.Add(group);
        }
        foreach (var item in PdfObjects.Array(defaults, "/ON")?.Elements ?? Enumerable.Empty<PdfItem>())
        {
            if (PdfObjects.Resolve(item) is PdfDictionary group)
                content._on.Add(group);
        }
        return content;
    }

    /// <summary>True when an optional content group or membership dictionary is shown.</summary>
    public bool Visible(PdfDictionary? item, int depth = 0)
    {
        if (item is null || depth > 8)
            return true;
        if (PdfObjects.Name(item, "/Type") == "OCMD")
        {
            var groups = PdfObjects.Get(item, "/OCGs") switch
            {
                PdfDictionary one => [one],
                PdfArray many => many.Elements.Select(PdfObjects.Resolve).OfType<PdfDictionary>().ToList(),
                _ => [],
            };
            if (groups.Count == 0)
                return true;
            var visible = groups.Select(g => Visible(g, depth + 1)).ToList();
            return PdfObjects.Name(item, "/P") switch
            {
                "AllOn" => visible.All(v => v),
                "AnyOff" => visible.Any(v => !v),
                "AllOff" => visible.All(v => !v),
                _ => visible.Any(v => v),
            };
        }
        return _baseOff ? _on.Contains(item) : !_off.Contains(item);
    }
}

/// <summary>Translates content streams into PostScript.</summary>
internal sealed class PsTranslator
{
    private const int MaxSaveDepth = 200;

    private readonly PsContext _context;
    private readonly PsWriter _w;
    private readonly bool _deferred;
    private readonly bool _uncolored;
    private readonly bool _glyph;
    private readonly int _depth;
    private readonly Mat _base;
    private readonly List<ShownGlyph> _shown = new(64);

    private State _gs = new();
    private readonly Stack<State> _saved = new();
    private PdfDictionary? _resources;
    private Mat _textMatrix = Mat.Identity;
    private Mat _lineMatrix = Mat.Identity;
    private bool _hasPoint;
    private string? _pendingClip;
    private int _hiddenDepth;
    private readonly Stack<bool> _marked = new();

    /// <param name="baseMatrix">The transformation from this content's space to the page's default space at its start.</param>
    /// <param name="deferred">The content runs later (a pattern cell, a Type 3 glyph): pictures keep their data in strings.</param>
    /// <param name="uncolored">Colour operators are ignored (an uncolored pattern, a Type 3 glyph after d1).</param>
    /// <param name="glyph">The content is a Type 3 glyph (d0 / d1 set its width).</param>
    public PsTranslator(PsContext context, PsWriter w, Mat baseMatrix, int depth, bool deferred = false, bool uncolored = false, bool glyph = false)
    {
        _context = context;
        _w = w;
        _base = baseMatrix;
        _depth = depth;
        _deferred = deferred;
        _uncolored = uncolored;
        _glyph = glyph;
        _gs.Ctm = baseMatrix;
        if (depth > PsContext.MaxDepth)
            throw new PsUntranslatableException("Forms, patterns or glyphs are nested too deeply.");
    }

    /// <summary>The graphics state the translator follows (the parts PostScript can't hold, or holds once for both colours).</summary>
    private sealed class State
    {
        public Mat Ctm = Mat.Identity;
        public Ink Fill = Ink.Black;
        public Ink Stroke = Ink.Black;

        /// <summary>The colour PostScript's current state holds (its code), or null when unknown.</summary>
        public string? PsColor;

        public PsFont? Font;
        public double FontSize;
        public double CharSpacing;
        public double WordSpacing;
        public double Scale = 100;
        public double Leading;
        public double Rise;
        public int Render;

        public State Clone() => (State)MemberwiseClone();
    }

    /// <summary>A colour: a space and components, or a pattern.</summary>
    private sealed record Ink(PdfColorSpace Space, double[] Components, PdfDictionary? Pattern = null)
    {
        public static readonly Ink Black = new(PdfColorSpace.Gray, [0]);
    }

    /// <summary>Translates <paramref name="content"/> drawn with <paramref name="resources"/>.</summary>
    public void Run(byte[] content, PdfDictionary? resources)
    {
        _resources = resources;
        var lexer = new PdfContentLexer(content);
        var operands = new List<object?>(16);
        var levels = 0;
        try
        {
            while (lexer.Next() is { } token)
            {
                if (token is not Operator op)
                {
                    if (operands.Count < 64)
                        operands.Add(token);
                    continue;
                }
                if (++_context.Operations > PsContext.MaxOperations)
                    throw new PsUntranslatableException("The page has too many operators.");
                if ((_context.Operations & 0x3FF) == 0)
                    _context.CancellationToken.ThrowIfCancellationRequested();
                if (op.Name == "BI")
                {
                    var (parameters, data) = lexer.ReadInlineImage(ImageSource.InlineLength);
                    if (_hiddenDepth == 0)
                        DrawImage(ImageSource.FromInline(parameters, data, _resources));
                }
                else
                {
                    Execute(op.Name, operands, ref levels);
                }
                operands.Clear();
            }
        }
        catch (InvalidDataException ex)
        {
            // A damaged stream: viewers draw what came before the damage, but whether they do differs, so the
            // page is drawn the way PDFium draws it.
            throw new PsUntranslatableException($"A content stream is damaged ({ex.Message}).");
        }
        while (levels-- > 0)
        {
            _w.Token("Q");
            Restore();
        }
    }

    private void Execute(string op, List<object?> o, ref int levels)
    {
        double N(int i) => i < o.Count && o[i] is double d && double.IsFinite(d) ? d : 0;
        bool Has(int count) => o.Count >= count && o.Take(count).All(v => v is double);

        switch (op)
        {
            // ── graphics state ──
            case "q":
                if (_saved.Count >= MaxSaveDepth)
                    throw new PsUntranslatableException("The graphics state is saved too deeply.");
                _saved.Push(_gs.Clone());
                levels++;
                _w.Token("q");
                break;
            case "Q":
                if (levels == 0)
                    break; // unbalanced: viewers ignore it
                levels--;
                Restore();
                _w.Token("Q");
                break;
            case "cm" when Has(6):
                {
                    var m = new Mat(N(0), N(1), N(2), N(3), N(4), N(5));
                    _gs.Ctm = m.Then(_gs.Ctm);
                    _w.Array([m.A, m.B, m.C, m.D, m.E, m.F]).Token("cm");
                    break;
                }
            case "w" when Has(1):
                _w.Number(Math.Abs(N(0))).Token("w");
                break;
            case "J" when Has(1):
                _w.Number(Math.Clamp((int)N(0), 0, 2)).Token("J");
                break;
            case "j" when Has(1):
                _w.Number(Math.Clamp((int)N(0), 0, 2)).Token("j");
                break;
            case "M" when Has(1):
                _w.Number(Math.Max(1, N(0))).Token("M");
                break;
            case "d" when o.Count >= 2 && o[0] is List<object?> dash:
                Dash(dash.Select(v => v is double d ? d : 0).ToArray(), N(1));
                break;
            case "i" when Has(1):
                _w.Number(Math.Clamp(N(0), 0.2, 100)).Token("i");
                break;
            case "ri":
                break;
            case "gs" when o.Count >= 1 && o[0] is Name name:
                GraphicsState(name.Value);
                break;

            // ── paths ──
            case "m" when Has(2):
                _w.Number(N(0)).Number(N(1)).Token("m");
                _hasPoint = true;
                break;
            case "l" when Has(2) && _hasPoint:
                _w.Number(N(0)).Number(N(1)).Token("l");
                break;
            case "c" when Has(6) && _hasPoint:
                _w.Numbers([N(0), N(1), N(2), N(3), N(4), N(5)]).Token("c");
                break;
            case "v" when Has(4) && _hasPoint:
                _w.Numbers([N(0), N(1), N(2), N(3)]).Token("v");
                break;
            case "y" when Has(4) && _hasPoint:
                _w.Numbers([N(0), N(1), N(2), N(3)]).Token("y");
                break;
            case "h" when _hasPoint:
                _w.Token("h");
                break;
            case "re" when Has(4):
                _w.Numbers([N(0), N(1), N(2), N(3)]).Token("re");
                _hasPoint = true;
                break;
            case "S":
                Paint(fill: null, stroke: true);
                break;
            case "s":
                if (_hasPoint)
                    _w.Token("h");
                Paint(fill: null, stroke: true);
                break;
            case "f" or "F":
                Paint(fill: "F", stroke: false);
                break;
            case "f*":
                Paint(fill: "F*", stroke: false);
                break;
            case "B":
                Paint(fill: "F", stroke: true);
                break;
            case "B*":
                Paint(fill: "F*", stroke: true);
                break;
            case "b":
                if (_hasPoint)
                    _w.Token("h");
                Paint(fill: "F", stroke: true);
                break;
            case "b*":
                if (_hasPoint)
                    _w.Token("h");
                Paint(fill: "F*", stroke: true);
                break;
            case "n":
                Paint(fill: null, stroke: false);
                break;
            case "W":
                _pendingClip = "W";
                break;
            case "W*":
                _pendingClip = "W*";
                break;

            // ── colour ──
            case "CS" or "cs" when o.Count >= 1 && o[0] is Name space:
                if (!_uncolored)
                    SetSpace(op == "cs", space.Value);
                break;
            case "SC" or "SCN" or "sc" or "scn":
                if (!_uncolored)
                    SetColor(op[0] == 's', o);
                break;
            case "G" or "g" when Has(1):
                if (!_uncolored)
                    SetPaint(op == "g", new Ink(PdfColorSpace.Gray, [N(0)]));
                break;
            case "RG" or "rg" when Has(3):
                if (!_uncolored)
                    SetPaint(op == "rg", new Ink(PdfColorSpace.Rgb, [N(0), N(1), N(2)]));
                break;
            case "K" or "k" when Has(4):
                if (!_uncolored)
                    SetPaint(op == "k", new Ink(PdfColorSpace.Cmyk, [N(0), N(1), N(2), N(3)]));
                break;

            // ── text ──
            case "BT":
                _textMatrix = _lineMatrix = Mat.Identity;
                break;
            case "ET":
                break;
            case "Tc" when Has(1):
                _gs.CharSpacing = N(0);
                break;
            case "Tw" when Has(1):
                _gs.WordSpacing = N(0);
                break;
            case "Tz" when Has(1):
                _gs.Scale = N(0);
                break;
            case "TL" when Has(1):
                _gs.Leading = N(0);
                break;
            case "Ts" when Has(1):
                _gs.Rise = N(0);
                break;
            case "Tr" when Has(1):
                _gs.Render = Math.Clamp((int)N(0), 0, 7);
                break;
            case "Tf" when o.Count >= 2 && o[0] is Name fontName:
                SetFont(fontName.Value, N(1));
                break;
            case "Td" when Has(2):
                _lineMatrix = new Mat(1, 0, 0, 1, N(0), N(1)).Then(_lineMatrix);
                _textMatrix = _lineMatrix;
                break;
            case "TD" when Has(2):
                _gs.Leading = -N(1);
                _lineMatrix = new Mat(1, 0, 0, 1, N(0), N(1)).Then(_lineMatrix);
                _textMatrix = _lineMatrix;
                break;
            case "Tm" when Has(6):
                _textMatrix = _lineMatrix = new Mat(N(0), N(1), N(2), N(3), N(4), N(5));
                break;
            case "T*":
                NextLine();
                break;
            case "Tj" when o.Count >= 1 && o[0] is byte[] text:
                Show([text]);
                break;
            case "'" when o.Count >= 1 && o[0] is byte[] text:
                NextLine();
                Show([text]);
                break;
            case "\"" when o.Count >= 3 && o[2] is byte[] text:
                _gs.WordSpacing = N(0);
                _gs.CharSpacing = N(1);
                NextLine();
                Show([text]);
                break;
            case "TJ" when o.Count >= 1 && o[0] is List<object?> parts:
                Show(parts);
                break;

            // ── Type 3 glyphs ──
            case "d0" when Has(2) && _glyph:
                _w.Number(N(0)).Number(N(1)).Token("setcharwidth");
                break;
            case "d1" when Has(6) && _glyph:
                _w.Numbers([N(0), N(1), N(2), N(3), N(4), N(5)]).Token("setcachedevice");
                break;

            // ── shadings, XObjects ──
            case "sh" when o.Count >= 1 && o[0] is Name shadingName && _hiddenDepth == 0:
                Shade(shadingName.Value);
                break;
            case "Do" when o.Count >= 1 && o[0] is Name xobject && _hiddenDepth == 0:
                Draw(xobject.Value);
                break;

            // ── marked and optional content ──
            case "BMC":
                _marked.Push(false);
                break;
            case "BDC":
                {
                    var hides = o.Count >= 2 && o[0] is Name { Value: "OC" } && !_context.OptionalContent.Visible(Properties(o[1]));
                    _marked.Push(hides);
                    if (hides)
                        _hiddenDepth++;
                    break;
                }
            case "EMC":
                if (_marked.Count > 0 && _marked.Pop())
                    _hiddenDepth--;
                break;
        }
    }

    private PdfDictionary? Properties(object? operand) => operand switch
    {
        Name name => PdfObjects.Dictionary(PdfObjects.Dictionary(_resources, "/Properties"), "/" + name.Value),
        _ => null,
    };

    private void Restore()
    {
        if (_saved.Count > 0)
            _gs = _saved.Pop();
    }

    private void Dash(double[] array, double phase)
    {
        if (array.Length > 64 || array.Any(v => v < 0 || !double.IsFinite(v)) || array.All(v => v == 0))
            array = [];
        _w.Array(array).Number(array.Length == 0 ? 0 : phase).Token("d");
    }

    private void GraphicsState(string name)
    {
        if (PdfObjects.Dictionary(PdfObjects.Dictionary(_resources, "/ExtGState"), "/" + name) is not { } state)
            return;
        if (PdfObjects.Number(PdfObjects.Get(state, "/LW")) is { } lw)
            _w.Number(Math.Abs(lw)).Token("w");
        if (PdfObjects.Number(PdfObjects.Get(state, "/LC")) is { } lc)
            _w.Number(Math.Clamp((int)lc, 0, 2)).Token("J");
        if (PdfObjects.Number(PdfObjects.Get(state, "/LJ")) is { } lj)
            _w.Number(Math.Clamp((int)lj, 0, 2)).Token("j");
        if (PdfObjects.Number(PdfObjects.Get(state, "/ML")) is { } ml)
            _w.Number(Math.Max(1, ml)).Token("M");
        if (PdfObjects.Array(state, "/D") is { Elements.Count: 2 } dash && PdfObjects.Numbers(dash.Elements[0]) is { } pattern)
            Dash(pattern, PdfObjects.Number(dash.Elements[1]) ?? 0);
        if (PdfObjects.Number(PdfObjects.Get(state, "/FL")) is { } flatness)
            _w.Number(Math.Clamp(flatness, 0.2, 100)).Token("i");
        if (PdfObjects.Get(state, "/SA") is PdfBoolean adjust)
            _w.Token(adjust.Value ? "true" : "false").Token("setstrokeadjust");
        if ((PdfObjects.Get(state, "/op") ?? PdfObjects.Get(state, "/OP")) is PdfBoolean overprint)
            _w.Token(overprint.Value ? "true" : "false").Token("setoverprint");
        if (PdfObjects.Array(state, "/Font") is { Elements.Count: 2 } font && PdfObjects.Resolve(font.Elements[0]) is PdfDictionary fontDict)
        {
            _gs.Font = _context.Fonts.Get(fontDict, _depth);
            _gs.FontSize = PdfObjects.Number(font.Elements[1]) ?? 0;
        }

        // Transparency PostScript can't hold.
        if (PdfObjects.Number(PdfObjects.Get(state, "/CA")) is < 0.999 || PdfObjects.Number(PdfObjects.Get(state, "/ca")) is < 0.999)
            throw new PsUntranslatableException("The page uses transparency (an alpha below 1).");
        if (PdfObjects.Get(state, "/SMask") is PdfDictionary)
            throw new PsUntranslatableException("The page uses a soft mask.");
        var blend = PdfObjects.Get(state, "/BM") switch
        {
            PdfName n => n.Value.TrimStart('/'),
            PdfArray a when a.Elements.Count > 0 => PdfObjects.Name(a.Elements[0]),
            _ => null,
        };
        if (blend is not (null or "Normal" or "Compatible"))
            throw new PsUntranslatableException($"The page uses the blend mode {blend}.");
    }

    // ───────────────────────── painting ─────────────────────────

    /// <summary>Fills (<paramref name="fill"/> F or F*), strokes, or only ends the path, applying a pending clip after.</summary>
    private void Paint(string? fill, bool stroke)
    {
        var clip = _pendingClip;
        _pendingClip = null;
        var paints = (fill is not null || stroke) && _hiddenDepth == 0;
        if (paints && clip is not null)
        {
            // Painting must not be cut by the clip that follows it: paint a copy of the path.
            _w.Token("gsave");
            var before = _gs.PsColor;
            PaintPath(fill, stroke);
            _gs.PsColor = before;
            _w.Token("grestore");
        }
        else if (paints)
        {
            PaintPath(fill, stroke);
        }
        if (clip is not null)
            _w.Token(clip);
        if (clip is not null || !paints)
            _w.Token("N");
        _hasPoint = false;
    }

    private void PaintPath(string? fill, bool stroke)
    {
        if (fill is not null && stroke)
        {
            UseColor(_gs.Fill);
            _w.Token("gsave").Token(fill).Token("grestore");
            UseColor(_gs.Stroke);
            _w.Token("S");
        }
        else if (fill is not null)
        {
            UseColor(_gs.Fill);
            _w.Token(fill);
        }
        else
        {
            UseColor(_gs.Stroke);
            _w.Token("S");
        }
    }

    /// <summary>Makes PostScript's current colour <paramref name="paint"/>.</summary>
    private void UseColor(Ink paint)
    {
        if (_uncolored)
            return;
        var code = ColorCode(paint);
        if (code == _gs.PsColor)
            return;
        _w.NewLine().Raw(code).NewLine();
        _gs.PsColor = code;
    }

    /// <summary>The PostScript that sets <paramref name="paint"/> (a pattern is defined on the page the first time).</summary>
    private string ColorCode(Ink paint)
    {
        if (paint.Space is PdfColorSpace.PatternSpace patternSpace)
        {
            if (paint.Pattern is null)
                return DeviceColor.Black.Code;
            var name = DefinePattern(paint.Pattern);
            if (PdfObjects.Number(paint.Pattern, "/PaintType", 1) == 2)
            {
                var color = (patternSpace.Underlying ?? PdfColorSpace.Gray).ToDevice(paint.Components);
                var values = string.Join(' ', Enumerable.Range(0, color.Components).Select(i => PsWriter.Format(Math.Clamp(color[i], 0, 1))));
                return $"[/Pattern /{DeviceColor.SpaceName(color.Kind)}] setcolorspace {values} {name} FileePatternU setcolor";
            }
            return $"{name} FileePattern";
        }
        return paint.Space.ToDevice(paint.Components).Code;
    }

    private void SetSpace(bool fill, string name)
    {
        var space = PdfColorSpace.Read(new Name(name), _resources);
        SetPaint(fill, new Ink(space, space.Initial));
    }

    private void SetColor(bool fill, List<object?> operands)
    {
        var current = fill ? _gs.Fill : _gs.Stroke;
        var numbers = operands.OfType<double>().ToArray();
        if (current.Space is PdfColorSpace.PatternSpace)
        {
            var pattern = operands.Count > 0 && operands[^1] is Name name
                ? PdfObjects.Dictionary(PdfObjects.Dictionary(_resources, "/Pattern"), "/" + name.Value)
                : null;
            SetPaint(fill, current with { Components = numbers, Pattern = pattern });
            return;
        }
        if (numbers.Length < current.Space.Components)
            return;
        SetPaint(fill, current with { Components = numbers[..current.Space.Components] });
    }

    private void SetPaint(bool fill, Ink paint)
    {
        if (fill)
            _gs.Fill = paint;
        else
            _gs.Stroke = paint;
    }

    // ───────────────────────── text ─────────────────────────

    private void SetFont(string name, double size)
    {
        _gs.FontSize = size;
        _gs.Font = PdfObjects.Dictionary(PdfObjects.Dictionary(_resources, "/Font"), "/" + name) is { } font
            ? _context.Fonts.Get(font, _depth)
            : null;
    }

    private void NextLine()
    {
        _lineMatrix = new Mat(1, 0, 0, 1, 0, -_gs.Leading).Then(_lineMatrix);
        _textMatrix = _lineMatrix;
    }

    /// <summary>Shows strings and TJ adjustments, glyph by glyph at their PDF positions, and moves the text matrix on.</summary>
    private void Show(List<object?> parts)
    {
        var font = _gs.Font;
        if (font is null)
            return; // no font set: viewers draw nothing
        var size = _gs.FontSize;
        var scale = _gs.Scale / 100;
        _shown.Clear();
        // Advances in text space (PDF 32000 9.4.4), and the offset before the first glyph.
        var advances = new List<double>();
        double offset = 0;
        foreach (var part in parts)
        {
            switch (part)
            {
                case byte[] text:
                    var start = _shown.Count;
                    font.Show(text, _shown);
                    for (var i = start; i < _shown.Count; i++)
                    {
                        var g = _shown[i];
                        advances.Add((g.Width * size + _gs.CharSpacing + (g.WordSpace ? _gs.WordSpacing : 0)) * scale);
                    }
                    break;
                case double adjust when double.IsFinite(adjust):
                    var move = -adjust / 1000 * size * scale;
                    if (advances.Count == 0)
                        offset += move;
                    else
                        advances[^1] += move;
                    break;
            }
        }
        var total = offset + advances.Sum();
        var render = _gs.Render;
        if (_shown.Count > 0 && render != 3 && _hiddenDepth == 0)
        {
            if (render >= 4)
                throw new PsUntranslatableException("Text is used as a clipping path.");
            var trm = new Mat(size * scale, 0, 0, size, 0, _gs.Rise).Then(_textMatrix);
            var unit = size * scale;
            if (Math.Abs(trm.Determinant) > 1e-12 && Math.Abs(unit) > 1e-9)
                DrawText(trm, offset / unit, advances.Select(a => a / unit).ToList(), font.IsType3 ? 0 : render);
        }
        _textMatrix = new Mat(1, 0, 0, 1, total, 0).Then(_textMatrix);
    }

    private void DrawText(Mat trm, double offset, List<double> displacements, int render)
    {
        var fills = render is 0 or 2;
        var strokes = render is 1 or 2;
        if (fills)
            UseColor(_gs.Fill);
        _w.NewLine().Array(trm.ToArray()).Token("TS");
        if (fills)
        {
            var x = offset;
            for (var start = 0; start < _shown.Count;)
            {
                var end = start;
                while (end < _shown.Count && _shown[end].Font == _shown[start].Font)
                    end++;
                var bytes = new byte[end - start];
                for (var i = start; i < end; i++)
                    bytes[i - start] = _shown[i].Code;
                _w.Token(_shown[start].Font).Number(x).String(bytes).Array(displacements.GetRange(start, end - start).ToArray()).Token("X");
                for (var i = start; i < end; i++)
                    x += displacements[i];
                start = end;
            }
        }
        if (strokes)
        {
            var x = offset;
            for (var i = 0; i < _shown.Count; i++)
            {
                _w.Token(_shown[i].Font).Number(x).String([_shown[i].Code]).Token("XC");
                x += displacements[i];
            }
            if (!_uncolored)
                _w.NewLine().Raw(ColorCode(_gs.Stroke)).NewLine();
            _w.Token("TK");
        }
        _w.Token("TE").NewLine();
    }

    /// <summary>A Type 3 glyph stream as PostScript, for its font's glyph procedure.</summary>
    public static string TranslateGlyph(PsContext context, PdfDictionary glyph, PdfDictionary? resources, int depth)
    {
        var data = PdfStreams.Decode(glyph, 16 * 1024 * 1024);
        // A glyph that starts with d1 draws in the colour of the text: its colour operators are ignored.
        var first = FirstOperator(data);
        var uncolored = first == "d1";
        using var buffer = new MemoryStream();
        var w = new PsWriter(buffer);
        if (first is not ("d0" or "d1"))
            w.Token("0 0 setcharwidth"); // PostScript needs the width first
        var translator = new PsTranslator(context, w, Mat.Identity, depth, deferred: true, uncolored: uncolored, glyph: true);
        translator.Run(data, resources);
        w.NewLine();
        return Encoding.ASCII.GetString(buffer.ToArray());
    }

    private static string? FirstOperator(byte[] data)
    {
        var lexer = new PdfContentLexer(data);
        try
        {
            while (lexer.Next() is { } token)
            {
                if (token is Operator op)
                    return op.Name;
            }
        }
        catch (InvalidDataException)
        {
        }
        return null;
    }

    // ───────────────────────── XObjects, pictures, shadings, patterns ─────────────────────────

    private void Draw(string name)
    {
        if (PdfObjects.Dictionary(PdfObjects.Dictionary(_resources, "/XObject"), "/" + name) is not { Stream: not null } xobject)
            return;
        if (PdfObjects.Dictionary(xobject, "/OC") is { } oc && !_context.OptionalContent.Visible(oc))
            return;
        switch (PdfObjects.Name(xobject, "/Subtype"))
        {
            case "Image":
                DrawImage(ImageSource.FromXObject(xobject, _resources));
                break;
            case "Form":
                DrawForm(xobject, Mat.Identity, clipToBox: true);
                break;
        }
    }

    private void DrawImage(ImageSource image)
    {
        if (image.IsMask)
            UseColor(_gs.Fill);
        PsImages.Draw(_w, image, _deferred);
    }

    /// <summary>Draws a form XObject (or an annotation appearance, with <paramref name="outer"/> placing it).</summary>
    public void DrawForm(PdfDictionary form, Mat outer, bool clipToBox)
    {
        var matrix = PdfObjects.Numbers(form, "/Matrix") is { Length: 6 } m ? Mat.From(m) : Mat.Identity;
        var content = PdfStreams.Decode(form, 64L * 1024 * 1024);
        var resources = PdfObjects.Dictionary(form, "/Resources") ?? _resources;
        var whole = matrix.Then(outer);
        _w.NewLine().Token("q");
        var before = _gs.PsColor;
        if (!outer.Equals(Mat.Identity))
            _w.Array(outer.ToArray()).Token("cm");
        if (!matrix.Equals(Mat.Identity))
            _w.Array(matrix.ToArray()).Token("cm");
        if (clipToBox && PdfObjects.Rectangle(PdfObjects.Get(form, "/BBox")) is { } box)
            _w.Numbers([box.X0, box.Y0, box.X1 - box.X0, box.Y1 - box.Y0]).Token("re").Token("W").Token("N");
        var inner = new PsTranslator(_context, _w, whole.Then(_gs.Ctm), _depth + 1, _deferred, _uncolored, _glyph)
        {
            _gs = _gs.Clone(),
        };
        inner._gs.Ctm = whole.Then(_gs.Ctm);
        inner._hiddenDepth = _hiddenDepth;
        inner.Run(content, resources);
        _w.NewLine().Token("Q").NewLine();
        _gs.PsColor = before;
    }

    private void Shade(string name)
    {
        if (PdfObjects.Get(PdfObjects.Dictionary(_resources, "/Shading"), "/" + name) is not PdfDictionary shading)
            return;
        _w.NewLine().Token("gsave");
        PsShadings.Write(_w, shading, _resources, withBackground: false);
        _w.Token("shfill").Token("grestore").NewLine();
    }

    /// <summary>Defines a pattern on the page (once per pattern and placement) and returns the code that makes it current.</summary>
    private string DefinePattern(PdfDictionary pattern)
    {
        if (_glyph)
            throw new PsUntranslatableException("A Type 3 glyph is painted with a pattern.");
        var matrix = (PdfObjects.Numbers(pattern, "/Matrix") is { Length: 6 } m ? Mat.From(m) : Mat.Identity).Then(_base);
        if (_context.Patterns.TryGetValue((pattern, matrix), out var known))
            return $"{known} {Matrix(matrix)}";
        var name = $"FileeP{_context.Patterns.Count + 1}";
        _context.Patterns[(pattern, matrix)] = name;
        // The definition goes into FileeDict in the middle of the page; PostScript's grestore doesn't undo it.
        _w.NewLine().Name(name).Token("<<");
        switch ((int)PdfObjects.Number(pattern, "/PatternType", 1))
        {
            case 1:
                {
                    var paintType = (int)PdfObjects.Number(pattern, "/PaintType", 1);
                    var box = PdfObjects.Rectangle(PdfObjects.Get(pattern, "/BBox")) ?? throw new InvalidDataException("A tiling pattern has no BBox.");
                    var xStep = PdfObjects.Number(pattern, "/XStep", 0);
                    var yStep = PdfObjects.Number(pattern, "/YStep", 0);
                    if (xStep == 0 || yStep == 0 || pattern.Stream is null)
                        throw new InvalidDataException("A tiling pattern has no step or no content.");
                    _w.Name("PatternType").Number(1).Name("PaintType").Number(paintType == 2 ? 2 : 1)
                        .Name("TilingType").Number(Math.Clamp((int)PdfObjects.Number(pattern, "/TilingType", 1), 1, 3))
                        .Name("BBox").Array([box.X0, box.Y0, box.X1, box.Y1]).Name("XStep").Number(xStep).Name("YStep").Number(yStep)
                        .Name("PaintProc").Token("{pop FileeDict begin").NewLine();
                    var content = PdfStreams.Decode(pattern, 64L * 1024 * 1024);
                    var cell = new PsTranslator(_context, _w, matrix, _depth + 1, deferred: true, uncolored: paintType == 2);
                    cell.Run(content, PdfObjects.Dictionary(pattern, "/Resources") ?? _resources);
                    _w.NewLine().Token("end}");
                    break;
                }
            case 2:
                {
                    if (PdfObjects.Get(pattern, "/Shading") is not PdfDictionary shading)
                        throw new InvalidDataException("A shading pattern has no shading.");
                    if (PdfObjects.Dictionary(pattern, "/ExtGState") is { } state && (PdfObjects.Number(PdfObjects.Get(state, "/ca")) is < 0.999 || PdfObjects.Get(state, "/SMask") is PdfDictionary))
                        throw new PsUntranslatableException("A shading pattern is transparent.");
                    _w.Name("PatternType").Number(2).Name("Shading");
                    PsShadings.Write(_w, shading, _resources, withBackground: true);
                    break;
                }
            default:
                throw new InvalidDataException("A pattern has an unknown type.");
        }
        _w.Token(">>").Token("def").NewLine();
        return $"{name} {Matrix(matrix)}";
    }

    private static string Matrix(Mat m) =>
        "[" + string.Join(' ', m.ToArray().Select(v => PsWriter.Format(v))) + "]";
}
