// ODF custom shapes (draw:custom-shape) as the slide model's outlines: draw:enhanced-geometry (OpenDocument 1.3 Part 3,
// 19.144 ff.) with its view box, modifiers (draw:modifiers, $n), equations (draw:equation, ?name) and the
// draw:enhanced-path commands, evaluated at the shape's size into SKPaths in the shape's box, plus the text area
// (draw:text-areas) and mirroring.
//
// - Formulas: numbers, + - * /, unary minus, parentheses, ?name (an equation), $n (a modifier), the names pi, left, top,
//   right, bottom, width, height (of the view box), logwidth, logheight (the shape in 1/100 mm), xstretch, ystretch,
//   hasfill, hasstroke, and the functions abs, sqrt, sin, cos, tan, atan, atan2, min, max and if.
// - Path commands: M (moveto, then lineto), L, C, Q, Z, N (ends a set of subpaths), F (no fill), S (no stroke),
//   A / W (arcto, counter-clockwise / clockwise, with a line to the start), B / V (the same without the line), T / U
//   (angle-ellipseto / angle-ellipse), X / Y (elliptical quadrants starting along x / y, alternating), and
//   LibreOffice's G (arc by radii, start and swing angle, as DrawingML arcTo) and H / I / J / K (darken, darken less,
//   lighten, lighten less).
// - A shape without a path (another producer may write only draw:type) takes the DrawingML preset of the same shape
//   (PresetShapes), "ooxml-…" types their preset by name with the modifiers as its adjust values; unknown types are a
//   rectangle and named by the capability check.
//
// Damage stays contained: unknown names count as 0, a division by zero gives 0, results are clamped, formulas nest at
// most MaxDepth deep, equations refer to each other at most MaxDepth deep (a loop gives 0), and the counts of equations,
// formula characters and path tokens are limited; the reader counts each evaluation's tokens against the deck.

using System.Globalization;
using System.Xml.Linq;
using Filee.Engines.Slides;
using SkiaSharp;
using OdfNames = Filee.Engines.Hwp.Hwpx.Odt.Odf;

namespace Filee.Engines.Odf.Draw;

internal sealed class OdfEnhancedGeometry
{
    /// <summary>Equations of one shape at most.</summary>
    public const int MaxEquations = 4096;

    /// <summary>Characters of one formula at most.</summary>
    public const int MaxFormulaLength = 4096;

    /// <summary>Path and text area tokens (commands and parameters) of one shape at most.</summary>
    public const int MaxTokens = ShapeGeometry.MaxCommands;

    /// <summary>Nesting of a formula, and of evaluation through equations, at most.</summary>
    public const int MaxDepth = 64;

    /// <summary>Modifiers read at most.</summary>
    private const int MaxModifiers = 256;

    private const double Limit = 1e12;

    private static readonly XNamespace Draw = OdfNames.Draw;
    private static readonly XNamespace Svg = OdfNames.Svg;

    private readonly double _viewX;
    private readonly double _viewY;
    private readonly double _viewWidth = 21600;
    private readonly double _viewHeight = 21600;
    private readonly double[] _modifiers;
    private readonly double _stretchX;
    private readonly double _stretchY;
    private readonly Node?[] _equations;
    private readonly List<Token> _path = [];
    private readonly List<Node> _textArea = [];

    private OdfEnhancedGeometry(XElement geometry)
    {
        if (Numbers((string?)geometry.Attribute(Svg + "viewBox"), 4) is [var x, var y, var w, var h] && w > 0 && h > 0)
            (_viewX, _viewY, _viewWidth, _viewHeight) = (x, y, w, h);
        _modifiers = Numbers((string?)geometry.Attribute(Draw + "modifiers"), MaxModifiers);
        _stretchX = Numbers((string?)geometry.Attribute(Draw + "path-stretchpoint-x"), 1) is [var sx] ? sx : 0;
        _stretchY = Numbers((string?)geometry.Attribute(Draw + "path-stretchpoint-y"), 1) is [var sy] ? sy : 0;
        Type = (string?)geometry.Attribute(Draw + "type");
        MirrorHorizontal = (string?)geometry.Attribute(Draw + "mirror-horizontal") == "true";
        MirrorVertical = (string?)geometry.Attribute(Draw + "mirror-vertical") == "true";
        TextRotation = Numbers((string?)geometry.Attribute(Draw + "text-rotate-angle"), 1) is [var angle] ? angle : 0;
        Extruded = (string?)geometry.Attribute(Draw + "extrusion") == "true";
        Fontwork = (string?)geometry.Attribute(Draw + "text-path") == "true";

        var equations = geometry.Elements(Draw + "equation").Take(MaxEquations).ToList();
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < equations.Count; i++)
            names.TryAdd((string?)equations[i].Attribute(Draw + "name") ?? "", i);
        _equations = new Node?[equations.Count];
        for (var i = 0; i < equations.Count; i++)
            _equations[i] = Parse((string?)equations[i].Attribute(Draw + "formula"), names);

        var tokens = 0;
        if ((string?)geometry.Attribute(Draw + "enhanced-path") is { } path)
        {
            HasPath = true;
            foreach (var token in Tokens(path))
            {
                if (++tokens > MaxTokens)
                    break;
                _path.Add(token.Command != '\0' ? token : new Token('\0', Parse(token.Text, names)));
            }
        }
        if ((string?)geometry.Attribute(Draw + "text-areas") is { } areas)
        {
            foreach (var token in Tokens(areas))
            {
                if (token.Command != '\0' || ++tokens > MaxTokens || _textArea.Count == 4)
                    break;
                _textArea.Add(Parse(token.Text, names) ?? new Constant(0));
            }
        }
        Cost = tokens + _equations.Length + 1;
    }

    /// <summary>The draw:type (a preset's name).</summary>
    public string? Type { get; }

    /// <summary>The shape has its own draw:enhanced-path.</summary>
    public bool HasPath { get; }

    public bool MirrorHorizontal { get; }
    public bool MirrorVertical { get; }

    /// <summary>Degrees the text turns, counter-clockwise (draw:text-rotate-angle).</summary>
    public double TextRotation { get; }

    /// <summary>The shape is drawn as a 3D extrusion (draw:extrusion).</summary>
    public bool Extruded { get; }

    /// <summary>The text follows the path (Fontwork, draw:text-path).</summary>
    public bool Fontwork { get; }

    /// <summary>What one evaluation costs against the deck's path budget: the tokens and equations, plus one.</summary>
    public int Cost { get; }

    public static OdfEnhancedGeometry Parse(XElement geometry) => new(geometry);

    /// <summary>
    /// The DrawingML preset a shape without a path stands for (<see cref="PresetShapes"/>), or null for a type Filee
    /// doesn't know.
    /// </summary>
    public static string? Preset(string? type)
    {
        if (type is null)
            return "rect";
        if (type.StartsWith("ooxml-", StringComparison.Ordinal))
            return PresetShapes.Known(type[6..]) ? type[6..] : null;
        var preset = type switch
        {
            "rectangle" or "non-primitive" => "rect",
            "round-rectangle" => "roundRect",
            "ellipse" or "circle" => "ellipse",
            "isosceles-triangle" => "triangle",
            "right-triangle" => "rtTriangle",
            "diamond" => "diamond",
            "parallelogram" => "parallelogram",
            "trapezoid" => "trapezoid",
            "pentagon" => "pentagon",
            "hexagon" => "hexagon",
            "octagon" => "octagon",
            "cross" => "plus",
            "star4" => "star4",
            "star5" => "star5",
            "star6" => "star6",
            "star8" => "star8",
            "star12" => "star12",
            "star24" => "star24",
            "right-arrow" => "rightArrow",
            "left-arrow" => "leftArrow",
            "up-arrow" => "upArrow",
            "down-arrow" => "downArrow",
            "left-right-arrow" => "leftRightArrow",
            "up-down-arrow" => "upDownArrow",
            "heart" => "heart",
            "smiley" => "smileyFace",
            "sun" => "sun",
            "moon" => "moon",
            "lightning" => "lightningBolt",
            "can" => "can",
            "cube" => "cube",
            "frame" => "frame",
            "block-arc" => "blockArc",
            "flowchart-process" => "flowChartProcess",
            "flowchart-alternate-process" => "flowChartAlternateProcess",
            "flowchart-decision" => "flowChartDecision",
            "flowchart-data" => "flowChartInputOutput",
            "flowchart-terminator" => "flowChartTerminator",
            "flowchart-connector" => "flowChartConnector",
            "flowchart-document" => "flowChartDocument",
            "flowchart-manual-input" => "flowChartManualInput",
            "flowchart-predefined-process" => "flowChartPredefinedProcess",
            "rectangular-callout" => "wedgeRectCallout",
            "round-rectangular-callout" => "wedgeRoundRectCallout",
            "round-callout" => "wedgeEllipseCallout",
            _ => null,
        };
        return preset is not null && PresetShapes.Known(preset) ? preset : null;
    }

    /// <summary>
    /// The outlines and text area of the shape at <paramref name="width"/> × <paramref name="height"/> points, in its
    /// box. A shape without a path takes its preset's.
    /// </summary>
    public GeometryResult Evaluate(double width, double height, bool hasFill, bool hasStroke)
    {
        if (!HasPath)
            return EvaluatePreset(width, height);
        var context = new Evaluation(this, width, height, hasFill, hasStroke);
        // Equations in order, so one that refers to the one before finds it computed (a long chain stays shallow).
        for (var i = 0; i < _equations.Length; i++)
            context.Equation(i, 0);
        var sx = width / _viewWidth;
        var sy = height / _viewHeight;
        var paths = new PathBuilder(context, sx, sy, _viewX, _viewY).Build(_path);

        SKRect? text = null;
        if (_textArea.Count == 4)
        {
            float X(double v) => Clamp((v - _viewX) * sx);
            float Y(double v) => Clamp((v - _viewY) * sy);
            text = new SKRect(X(context.Value(_textArea[0])), Y(context.Value(_textArea[1])), X(context.Value(_textArea[2])),
                Y(context.Value(_textArea[3]))).Standardized;
        }
        return new GeometryResult(paths, text);
    }

    private GeometryResult EvaluatePreset(double width, double height)
    {
        var name = Preset(Type) ?? "rect";
        var definition = PresetShapes.Find(name)!;
        Dictionary<string, string>? adjust = null;
        // LibreOffice keeps a DrawingML shape's adjust values as its modifiers, in the preset's order.
        if (Type?.StartsWith("ooxml-", StringComparison.Ordinal) == true && _modifiers.Length > 0)
        {
            adjust = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < Math.Min(_modifiers.Length, definition.Adjust.Count); i++)
                adjust[definition.Adjust[i].Name] = "val " + _modifiers[i].ToString("R", CultureInfo.InvariantCulture);
        }
        return ShapeGeometry.Evaluate(definition, width, height, adjust, 1);
    }

    private static float Clamp(double value) => (float)(double.IsFinite(value) ? Math.Clamp(value, -Limit, Limit) : 0);

    /// <summary>Up to <paramref name="max"/> numbers of a list ("0 0 21600 21600", "17520 5400").</summary>
    private static double[] Numbers(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        var result = new List<double>();
        foreach (var part in text.Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (result.Count == max)
                break;
            result.Add(double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
                ? Math.Clamp(value, -Limit, Limit)
                : 0);
        }
        return [.. result];
    }

    // ───────────────────────── Tokens ─────────────────────────

    /// <summary>A command letter, or a parameter (its text before parsing, its formula after).</summary>
    private readonly record struct Token(char Command, Node? Value, string Text = "");

    /// <summary>The commands and parameters of an enhanced path or a list of text areas.</summary>
    private static IEnumerable<Token> Tokens(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c is ' ' or ',' or '\t' or '\r' or '\n')
            {
                i++;
                continue;
            }
            var start = i;
            if (char.IsAsciiLetterUpper(c))
            {
                i++;
                yield return new Token(c, null);
                continue;
            }
            if (c is '?' or '$')
            {
                i++;
                while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_'))
                    i++;
            }
            else if (char.IsAsciiLetterLower(c))
            {
                while (i < text.Length && char.IsAsciiLetterOrDigit(text[i]))
                    i++;
            }
            else if (char.IsAsciiDigit(c) || c is '-' or '+' or '.')
            {
                i++;
                while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '.'
                                           || text[i] is 'e' or 'E' && i + 1 < text.Length && (char.IsAsciiDigit(text[i + 1]) || text[i + 1] is '-' or '+')
                                           || text[i] is '-' or '+' && text[i - 1] is 'e' or 'E'))
                    i++;
            }
            else
            {
                i++; // anything else is skipped
                continue;
            }
            yield return new Token('\0', null, text[start..i]);
        }
    }

    // ───────────────────────── Formulas ─────────────────────────

    private abstract class Node
    {
        public abstract double Value(Evaluation context, int depth);
    }

    private sealed class Constant(double value) : Node
    {
        public override double Value(Evaluation context, int depth) => value;
    }

    private sealed class EquationReference(int index) : Node
    {
        public override double Value(Evaluation context, int depth) => context.Equation(index, depth);
    }

    private sealed class Modifier(int index) : Node
    {
        public override double Value(Evaluation context, int depth) => context.Modifier(index);
    }

    private sealed class Named(string name) : Node
    {
        public override double Value(Evaluation context, int depth) => context.Named(name);
    }

    private sealed class Negate(Node operand) : Node
    {
        public override double Value(Evaluation context, int depth) => -Evaluation.Child(operand, context, depth);
    }

    private sealed class Binary(char op, Node left, Node right) : Node
    {
        public override double Value(Evaluation context, int depth)
        {
            var a = Evaluation.Child(left, context, depth);
            var b = Evaluation.Child(right, context, depth);
            return op switch
            {
                '+' => a + b,
                '-' => a - b,
                '*' => a * b,
                _ => b == 0 ? 0 : a / b,
            };
        }
    }

    private sealed class Call(string name, Node[] arguments) : Node
    {
        public override double Value(Evaluation context, int depth)
        {
            double Arg(int i) => i < arguments.Length ? Evaluation.Child(arguments[i], context, depth) : 0;
            return name switch
            {
                "abs" => Math.Abs(Arg(0)),
                "sqrt" => Arg(0) is > 0 and var x ? Math.Sqrt(x) : 0,
                "sin" => Math.Sin(Arg(0)),
                "cos" => Math.Cos(Arg(0)),
                "tan" => Math.Tan(Arg(0)),
                "atan" => Math.Atan(Arg(0)),
                "atan2" => Math.Atan2(Arg(0), Arg(1)),
                "min" => Math.Min(Arg(0), Arg(1)),
                "max" => Math.Max(Arg(0), Arg(1)),
                "if" => Arg(0) > 0 ? Arg(1) : Arg(2),
                _ => 0,
            };
        }
    }

    /// <summary>A formula as a tree; null for an empty, damaged or too deeply nested one.</summary>
    private static Node? Parse(string? formula, Dictionary<string, int> equations)
    {
        if (string.IsNullOrWhiteSpace(formula) || formula.Length > MaxFormulaLength)
            return null;
        var parser = new Parser(formula, equations);
        try
        {
            var node = parser.Expression(0);
            return parser.AtEnd ? node : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Recursive descent over a formula; FormatException for what it can't read.</summary>
    private sealed class Parser(string text, Dictionary<string, int> equations)
    {
        private int _at;

        public bool AtEnd
        {
            get
            {
                Skip();
                return _at >= text.Length;
            }
        }

        private void Skip()
        {
            while (_at < text.Length && char.IsWhiteSpace(text[_at]))
                _at++;
        }

        private char Peek()
        {
            Skip();
            return _at < text.Length ? text[_at] : '\0';
        }

        public Node Expression(int depth)
        {
            if (depth > MaxDepth)
                throw new FormatException();
            var node = Term(depth);
            while (Peek() is '+' or '-')
            {
                var op = text[_at++];
                node = new Binary(op, node, Term(depth));
            }
            return node;
        }

        private Node Term(int depth)
        {
            var node = Unary(depth);
            while (Peek() is '*' or '/')
            {
                var op = text[_at++];
                node = new Binary(op, node, Unary(depth));
            }
            return node;
        }

        private Node Unary(int depth)
        {
            if (depth > MaxDepth)
                throw new FormatException();
            switch (Peek())
            {
                case '-':
                    _at++;
                    return new Negate(Unary(depth + 1));
                case '+':
                    _at++;
                    return Unary(depth + 1);
                default:
                    return Primary(depth);
            }
        }

        private Node Primary(int depth)
        {
            var c = Peek();
            if (c == '(')
            {
                _at++;
                var inner = Expression(depth + 1);
                Expect(')');
                return inner;
            }
            if (c == '?')
            {
                var name = Word(++_at);
                return equations.TryGetValue(name, out var index) ? new EquationReference(index) : new Constant(0);
            }
            if (c == '$')
            {
                var digits = Word(++_at);
                return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var index) ? new Modifier(index) : new Constant(0);
            }
            if (char.IsAsciiDigit(c) || c == '.')
                return new Constant(Number());
            if (char.IsAsciiLetter(c))
            {
                var name = Word(_at);
                if (Peek() != '(')
                    return new Named(name);
                _at++;
                var arguments = new List<Node>();
                if (Peek() != ')')
                {
                    arguments.Add(Expression(depth + 1));
                    while (Peek() == ',')
                    {
                        _at++;
                        if (arguments.Count == 3)
                            throw new FormatException();
                        arguments.Add(Expression(depth + 1));
                    }
                }
                Expect(')');
                return new Call(name, [.. arguments]);
            }
            throw new FormatException();
        }

        private void Expect(char c)
        {
            if (Peek() != c)
                throw new FormatException();
            _at++;
        }

        private string Word(int start)
        {
            _at = start;
            while (_at < text.Length && (char.IsAsciiLetterOrDigit(text[_at]) || text[_at] == '_'))
                _at++;
            return text[start.._at];
        }

        private double Number()
        {
            var start = _at;
            while (_at < text.Length && (char.IsAsciiDigit(text[_at]) || text[_at] == '.'
                                         || text[_at] is 'e' or 'E' && _at + 1 < text.Length && (char.IsAsciiDigit(text[_at + 1]) || text[_at + 1] is '-' or '+')
                                         || text[_at] is '-' or '+' && _at > start && text[_at - 1] is 'e' or 'E'))
                _at++;
            return double.TryParse(text.AsSpan(start, _at - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
                ? Math.Clamp(value, -Limit, Limit)
                : throw new FormatException();
        }
    }

    /// <summary>The values of one evaluation at one size: equations computed once each, loops and deep chains as 0.</summary>
    private sealed class Evaluation(OdfEnhancedGeometry geometry, double width, double height, bool hasFill, bool hasStroke)
    {
        private readonly double?[] _values = new double?[geometry._equations.Length];
        private readonly bool[] _busy = new bool[geometry._equations.Length];

        /// <summary>A node's value at the nesting depth of its parent plus one; past <see cref="MaxDepth"/> × 4 it is 0.</summary>
        public static double Child(Node node, Evaluation context, int depth) => depth >= MaxDepth * 4 ? 0 : Finite(node.Value(context, depth + 1));

        public double Value(Node? node) => node is null ? 0 : Child(node, this, 0);

        public double Equation(int index, int depth)
        {
            if (_values[index] is { } known)
                return known;
            if (_busy[index] || geometry._equations[index] is not { } formula)
                return 0;
            _busy[index] = true;
            var value = Child(formula, this, depth);
            _busy[index] = false;
            _values[index] = value;
            return value;
        }

        public double Modifier(int index) => index >= 0 && index < geometry._modifiers.Length ? geometry._modifiers[index] : 0;

        public double Named(string name) => name switch
        {
            "pi" => Math.PI,
            "left" => geometry._viewX,
            "top" => geometry._viewY,
            "right" => geometry._viewX + geometry._viewWidth,
            "bottom" => geometry._viewY + geometry._viewHeight,
            "width" => geometry._viewWidth,
            "height" => geometry._viewHeight,
            "logwidth" => width / 72 * 2540,
            "logheight" => height / 72 * 2540,
            "xstretch" => geometry._stretchX,
            "ystretch" => geometry._stretchY,
            "hasfill" => hasFill ? 1 : 0,
            "hasstroke" => hasStroke ? 1 : 0,
            _ => 0,
        };

        private static double Finite(double value) => double.IsFinite(value) ? Math.Clamp(value, -Limit, Limit) : 0;
    }

    // ───────────────────────── Paths ─────────────────────────

    private sealed class PathBuilder(Evaluation context, double sx, double sy, double viewX, double viewY)
    {
        /// <summary>The Bézier handle length of a quarter ellipse, as a share of its radius.</summary>
        private const double Kappa = 0.5522847498;

        private readonly List<SlidePath> _paths = [];
        private SKPath _current = NewPath();
        private bool _noFill;
        private bool _noStroke;
        private PathFill _mode = PathFill.Normal;
        private SKPoint _point;
        private bool _open;

        private static SKPath NewPath() => new() { FillType = SKPathFillType.EvenOdd };

        public List<SlidePath> Build(List<Token> tokens)
        {
            var i = 0;
            while (i < tokens.Count)
            {
                var command = tokens[i].Command;
                // Parameters before any command continue a moveto.
                if (command == '\0')
                    command = 'M';
                else
                    i++;
                var count = 0;
                while (i + count < tokens.Count && tokens[i + count].Command == '\0')
                    count++;
                Command(command, tokens, i, count);
                i += count;
            }
            Flush();
            return _paths;
        }

        private double At(List<Token> tokens, int index) => context.Value(tokens[index].Value);

        private SKPoint Point(List<Token> tokens, int index) => new(X(At(tokens, index)), Y(At(tokens, index + 1)));

        private float X(double v) => Clamp((v - viewX) * sx);
        private float Y(double v) => Clamp((v - viewY) * sy);

        private void Command(char command, List<Token> tokens, int first, int count)
        {
            switch (command)
            {
                case 'M':
                    for (var k = 0; k + 2 <= count; k += 2)
                    {
                        var point = Point(tokens, first + k);
                        if (k == 0)
                            MoveTo(point);
                        else
                            LineTo(point);
                    }
                    break;
                case 'L':
                    for (var k = 0; k + 2 <= count; k += 2)
                        LineTo(Point(tokens, first + k));
                    break;
                case 'C':
                    for (var k = 0; k + 6 <= count; k += 6)
                    {
                        Start();
                        var end = Point(tokens, first + k + 4);
                        _current.CubicTo(Point(tokens, first + k), Point(tokens, first + k + 2), end);
                        _point = end;
                    }
                    break;
                case 'Q':
                    for (var k = 0; k + 4 <= count; k += 4)
                    {
                        Start();
                        var end = Point(tokens, first + k + 2);
                        _current.QuadTo(Point(tokens, first + k), end);
                        _point = end;
                    }
                    break;
                case 'Z':
                    if (_open)
                        _current.Close();
                    _open = false;
                    break;
                case 'N':
                    Flush();
                    break;
                case 'F':
                    _noFill = true;
                    break;
                case 'S':
                    _noStroke = true;
                    break;
                case 'H':
                    _mode = PathFill.Darken;
                    break;
                case 'I':
                    _mode = PathFill.DarkenLess;
                    break;
                case 'J':
                    _mode = PathFill.Lighten;
                    break;
                case 'K':
                    _mode = PathFill.LightenLess;
                    break;
                case 'A' or 'B' or 'V' or 'W':
                    for (var k = 0; k + 8 <= count; k += 8)
                        ArcThroughPoints(tokens, first + k, clockwise: command is 'V' or 'W', line: command is 'A' or 'W');
                    break;
                case 'T' or 'U':
                    for (var k = 0; k + 6 <= count; k += 6)
                        AngleEllipse(tokens, first + k, line: command == 'T' || k > 0);
                    break;
                case 'X' or 'Y':
                    var alongX = command == 'X';
                    for (var k = 0; k + 2 <= count; k += 2)
                    {
                        Quadrant(Point(tokens, first + k), alongX);
                        alongX = !alongX;
                    }
                    break;
                case 'G':
                    for (var k = 0; k + 4 <= count; k += 4)
                        ArcAngle(At(tokens, first + k) * sx, At(tokens, first + k + 1) * sy, At(tokens, first + k + 2), At(tokens, first + k + 3));
                    break;
            }
        }

        private void MoveTo(SKPoint point)
        {
            _current.MoveTo(point);
            _point = point;
            _open = true;
        }

        private void LineTo(SKPoint point)
        {
            if (!_open)
            {
                MoveTo(point);
                return;
            }
            _current.LineTo(point);
            _point = point;
        }

        /// <summary>A contour at the current point for a curve that follows.</summary>
        private void Start()
        {
            if (!_open)
                MoveTo(_point);
        }

        /// <summary>Ends a set of subpaths: it becomes one outline with its own fill and stroke.</summary>
        private void Flush()
        {
            if (_current.PointCount > 0)
                _paths.Add(new SlidePath(_current, _noFill ? PathFill.None : _mode, !_noStroke));
            else
                _current.Dispose();
            _current = NewPath();
            _open = false;
            _noFill = false;
            _noStroke = false;
            _mode = PathFill.Normal;
        }

        /// <summary>
        /// A / B / V / W: the ellipse inside the rectangle of the first two points, from the angle of the third point to
        /// that of the fourth, counter-clockwise or clockwise.
        /// </summary>
        private void ArcThroughPoints(List<Token> tokens, int first, bool clockwise, bool line)
        {
            var oval = new SKRect(X(At(tokens, first)), Y(At(tokens, first + 1)), X(At(tokens, first + 2)), Y(At(tokens, first + 3))).Standardized;
            var from = Point(tokens, first + 4);
            var to = Point(tokens, first + 6);
            if (oval.Width < 1e-6 || oval.Height < 1e-6)
            {
                if (line)
                    LineTo(to);
                else
                    MoveTo(to);
                return;
            }
            var start = Angle(oval, from);
            var end = Angle(oval, to);
            var sweep = clockwise ? Positive(end - start) : -Positive(start - end);
            if (Math.Abs(sweep) < 1e-9)
                sweep = clockwise ? 360 : -360;
            Arc(oval, start, sweep, moveTo: !line || !_open);
        }

        /// <summary>T / U: the ellipse around a centre with two radii, from angle t0 to t1 (degrees, counter-clockwise as seen).</summary>
        private void AngleEllipse(List<Token> tokens, int first, bool line)
        {
            var center = Point(tokens, first);
            var rx = Math.Abs(At(tokens, first + 2) * sx);
            var ry = Math.Abs(At(tokens, first + 3) * sy);
            var t0 = At(tokens, first + 4);
            var t1 = At(tokens, first + 5);
            if (rx < 1e-9 || ry < 1e-9)
                return;
            var oval = new SKRect((float)(center.X - rx), (float)(center.Y - ry), (float)(center.X + rx), (float)(center.Y + ry));
            // Counter-clockwise as seen is negative in Skia's angles (y down).
            var sweep = t1 - t0;
            if (Math.Abs(sweep) < 1e-9 || Math.Abs(sweep) >= 360)
                sweep = 360;
            Arc(oval, -t0, -sweep, moveTo: !line || !_open);
        }

        /// <summary>X / Y: a quarter ellipse from the current point to <paramref name="to"/>, leaving along x or along y.</summary>
        private void Quadrant(SKPoint to, bool alongX)
        {
            Start();
            var from = _point;
            var k = (float)Kappa;
            SKPoint c1, c2;
            if (alongX)
            {
                c1 = new SKPoint(from.X + (to.X - from.X) * k, from.Y);
                c2 = new SKPoint(to.X, to.Y - (to.Y - from.Y) * k);
            }
            else
            {
                c1 = new SKPoint(from.X, from.Y + (to.Y - from.Y) * k);
                c2 = new SKPoint(to.X - (to.X - from.X) * k, to.Y);
            }
            _current.CubicTo(c1, c2, to);
            _point = to;
        }

        /// <summary>G: an arc from the current point on an ellipse of radii wR × hR, from a start angle through a swing angle (degrees, as seen).</summary>
        private void ArcAngle(double rx, double ry, double start, double swing)
        {
            rx = Math.Abs(rx);
            ry = Math.Abs(ry);
            if (rx < 1e-9 || ry < 1e-9 || swing == 0 || !double.IsFinite(start) || !double.IsFinite(swing))
                return;
            swing = Math.Clamp(swing, -3600, 3600);
            Start();
            var t1 = Parameter(start, rx, ry);
            var t2 = Parameter(start + swing, rx, ry);
            var delta = swing > 0 ? Positive(t2 - t1) : -Positive(t1 - t2);
            if (Math.Abs(delta) < 1e-9)
                delta = swing > 0 ? 360 : -360;
            var cx = _point.X - rx * Math.Cos(t1 * Math.PI / 180);
            var cy = _point.Y - ry * Math.Sin(t1 * Math.PI / 180);
            Arc(new SKRect((float)(cx - rx), (float)(cy - ry), (float)(cx + rx), (float)(cy + ry)), t1, delta, moveTo: false);
        }

        /// <summary>Adds an arc of <paramref name="oval"/> in pieces of at most half a turn and moves the current point to its end.</summary>
        private void Arc(SKRect oval, double start, double sweep, bool moveTo)
        {
            sweep = Math.Clamp(sweep, -3600, 3600);
            var pieces = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / 180));
            var step = sweep / pieces;
            for (var i = 0; i < pieces; i++)
                _current.ArcTo(oval, (float)(start + step * i), (float)step, moveTo && i == 0);
            _open = true;
            var end = (start + sweep) * Math.PI / 180;
            _point = new SKPoint((float)(oval.MidX + oval.Width / 2 * Math.Cos(end)), (float)(oval.MidY + oval.Height / 2 * Math.Sin(end)));
        }

        /// <summary>The ellipse parameter (Skia's angle, degrees) of a point.</summary>
        private static double Angle(SKRect oval, SKPoint point) =>
            Math.Atan2((point.Y - oval.MidY) / (oval.Height / 2), (point.X - oval.MidX) / (oval.Width / 2)) * 180 / Math.PI;

        /// <summary>The ellipse parameter of the point seen at <paramref name="degrees"/>.</summary>
        private static double Parameter(double degrees, double rx, double ry)
        {
            var a = degrees * Math.PI / 180;
            return Math.Atan2(rx * Math.Sin(a), ry * Math.Cos(a)) * 180 / Math.PI;
        }

        private static double Positive(double degrees)
        {
            degrees %= 360;
            return degrees < 0 ? degrees + 360 : degrees;
        }
    }
}
