// The equation tree (MathNode) → Typst math, for the Typst writer ($ … $). Typst's math is its own language, so even
// an equation read from TeX is written from its tree.
//
// Atoms are separated by spaces (Typst draws math spacing from the atoms, not the spaces, and a space keeps letters
// from running into one multi-letter name or shorthands like "->" from forming). Symbols are their Unicode characters,
// which Typst's math takes as they are; the characters with a meaning in its syntax are escaped (\( \, \_ …), words
// and CJK text are quoted. Fractions frac(a, b), roots sqrt / root, scripts x_(i)^(n) (attach() for a base of several
// atoms, primes as '), stacked content limits(x)_( … )^( … ) (an arrow stretch(→)_( … )^( … ), which grows with what
// it carries: Typst sets a relation's attachments over and under it), large operators with their limits, function
// names as op("…") (with limits for lim, max …), matrices mat(delim: …), cases(…), piles as lines (\) with & marks
// at the top or as a matrix inside, stretched brackets ( … ) or lr(…), abs / norm / floor / ceil, accents (hat, tilde,
// overline, arrow …), upright / italic / bold, spaces (thin, med, thick, quad, wide or #h). Cells and arguments are
// separated by the writer's own commas, so a comma or semicolon in their content is escaped.
//
// A tree deeper than MaxDepth is not written (null), as in TexMathWriter.

using System.Globalization;
using System.Text;

namespace Filee.Engines.Hwp.Hwpx;

internal static class TypstMathWriter
{
    /// <inheritdoc cref="TexMathWriter.MaxDepth"/>
    public const int MaxDepth = TexMathWriter.MaxDepth;

    /// <summary>Operator names Typst sets with their limits below in display math.</summary>
    private static readonly HashSet<string> WithLimits = new(StringComparer.Ordinal)
    {
        "lim", "liminf", "limsup", "max", "min", "sup", "inf", "det", "gcd", "lcm", "Pr",
    };

    /// <summary>The Typst math of <paramref name="node"/> (without $ delimiters), or null when it nests deeper than <see cref="MaxDepth"/>.</summary>
    public static string? Write(MathNode node)
    {
        if (EquationScript.Deeper(node, MaxDepth))
            return null;
        var writer = new Writer();
        // Lines of the whole equation are lines; a pile inside something else is a matrix.
        if (node is MathPile pile)
            writer.Lines(pile);
        else if (node is MathRow { Items: [MathPile first, ..] } row && row.Items.Skip(1).All(i => i is MathSpace or MathQuoted))
        {
            // Lines with their 	ag after them.
            writer.Lines(first);
            foreach (var item in row.Items.Skip(1))
                writer.Node(item);
        }
        else
            writer.Node(node);
        return writer.ToString().Trim();
    }

    private sealed class Writer
    {
        private readonly StringBuilder _sb = new();

        /// <summary>Calls the writer is inside: their commas and semicolons separate arguments.</summary>
        private int _arguments;

        public override string ToString() => _sb.ToString();

        /// <summary>A space before the next atom, unless it follows an opening bracket or another space.</summary>
        private void Separate()
        {
            // (an escaped "\(" is an atom, not an opening)
            if (_sb.Length == 0 || _sb[^1] is ' ' or '\n' || (_sb[^1] == '(' && (_sb.Length < 2 || _sb[^2] != '\\')))
                return;
            _sb.Append(' ');
        }

        private void Atom(string text)
        {
            Separate();
            _sb.Append(text);
        }

        /// <summary>A call: name( arguments separated by ", " ).</summary>
        private void Call(string name, params MathNode[] arguments)
        {
            Atom(name);
            _sb.Append('(');
            _arguments++;
            for (var i = 0; i < arguments.Length; i++)
            {
                if (i > 0)
                    _sb.Append(", ");
                Content(arguments[i]);
            }
            _arguments--;
            _sb.Append(')');
        }

        /// <summary>Content of an argument; empty content as "" (an argument can't be left blank).</summary>
        private void Content(MathNode node)
        {
            var start = _sb.Length;
            Node(node);
            if (_sb.ToString(start, _sb.Length - start).Trim().Length == 0)
            {
                _sb.Length = start;
                _sb.Append("\"\"");
            }
        }

        public void Lines(MathPile pile)
        {
            for (var r = 0; r < pile.Rows.Count; r++)
            {
                if (r > 0)
                    _sb.Append(" \\\n");
                Node(pile.Rows[r], aligned: true);
            }
        }

        /// <param name="aligned">At the top of the equation, where an alignment mark is an &amp;.</param>
        public void Node(MathNode node, bool aligned = false)
        {
            switch (node)
            {
                case MathRow row:
                    foreach (var item in row.Items)
                        Node(item, aligned);
                    break;
                case MathText text:
                    Letters(text.Text);
                    break;
                case MathNumber number:
                    if (number.Digits.All(c => char.IsAsciiDigit(c) || c == '.'))
                        Atom(number.Digits);
                    else
                        Symbols(number.Digits);
                    break;
                case MathSymbol symbol:
                    Symbols(symbol.Text);
                    break;
                case MathCommand command:
                    Symbols(command.Glyph);
                    break;
                case MathQuoted quoted:
                    if (quoted.Text.Length > 0)
                        Atom(Quoted(quoted.Text));
                    break;
                case MathFunction function:
                    Atom(Operator(function.Name));
                    break;
                case MathFraction { Bar: true } fraction:
                    Call("frac", fraction.Numerator, fraction.Denominator);
                    break;
                case MathFraction stack:
                    Matrix("#none", [[stack.Numerator], [stack.Denominator]]);
                    break;
                case MathRoot { Index: null } root:
                    Call("sqrt", root.Body);
                    break;
                case MathRoot root:
                    Call("root", root.Index, root.Body);
                    break;
                case MathScripts scripts:
                    Scripts(scripts.Base, scripts.Sub, scripts.Sup);
                    break;
                case MathStack stack when Stretchable(stack.Base) is { } arrow:
                    // \xrightarrow: stretch() as the base of the attachments makes the arrow as long as they are.
                    Atom("stretch");
                    _sb.Append('(').Append(arrow).Append(')');
                    Attachments(stack.Under, stack.Over);
                    break;
                case MathStack stack:
                    // limits() sets the attachments over and under the base.
                    Call("limits", stack.Base);
                    Attachments(stack.Under, stack.Over);
                    break;
                case MathBigOperator big:
                    Symbols(big.Glyph);
                    Attachments(big.Lower, big.Upper);
                    break;
                case MathLimit limit:
                    Atom("lim");
                    Attachments(limit.Lower, null);
                    break;
                case MathMatrix matrix:
                    Matrix(matrix.Command switch
                    {
                        "pmatrix" => "\"(\"",
                        "bmatrix" => "\"[\"",
                        "dmatrix" or "vmatrix" => "\"|\"",
                        _ => "#none",
                    }, matrix.Rows);
                    break;
                case MathCases cases:
                    Atom("cases");
                    _sb.Append('(');
                    _arguments++;
                    for (var r = 0; r < cases.Rows.Count; r++)
                    {
                        if (r > 0)
                            _sb.Append(", ");
                        for (var c = 0; c < cases.Rows[r].Count; c++)
                        {
                            if (c > 0)
                                _sb.Append(" &");
                            Node(cases.Rows[r][c]);
                        }
                    }
                    _arguments--;
                    _sb.Append(')');
                    break;
                case MathPile pile:
                    Matrix("#none", [.. pile.Rows.Select(Cells)]);
                    break;
                case MathBrackets { Left: "(", Right: ")", Body: MathFraction { Bar: false } binomial }:
                    Call("binom", binomial.Numerator, binomial.Denominator);
                    break;
                case MathBrackets brackets:
                    Brackets(brackets);
                    break;
                case MathAccent accent:
                    Accent(accent);
                    break;
                case MathStyled styled:
                    switch (styled.Command)
                    {
                        case "rm":
                            Call("upright", styled.Body);
                            break;
                        case "it":
                            Call("italic", styled.Body);
                            break;
                        case "bold":
                            Call("bold", styled.Body);
                            break;
                        default:
                            Node(styled.Body, aligned);
                            break;
                    }
                    break;
                case MathSpace space:
                    Space(space.Width);
                    break;
                case MathAlign:
                    if (aligned)
                        Atom("&");
                    break;
            }
        }

        private void Scripts(MathNode baseNode, MathNode? sub, MathNode? sup)
        {
            var single = baseNode is MathRow { Items.Count: 1 } row ? row.Items[0] : baseNode;
            var atom = single is MathText { Text.Length: 1 } or MathNumber or MathSymbol { Text.Length: 1 } or MathCommand or MathFunction
                or MathQuoted or MathFraction or MathRoot or MathBrackets or MathAccent or MathStyled or MathLimit or MathMatrix;
            if (atom)
            {
                Node(single);
                Attachments(sub, sup);
                return;
            }
            // A base of several atoms (or none): attach() keeps it one.
            Atom("attach");
            _sb.Append('(');
            _arguments++;
            Content(baseNode);
            if (sub is not null)
            {
                _sb.Append(", b: ");
                Content(sub);
            }
            if (sup is not null)
            {
                _sb.Append(", t: ");
                Content(sup);
            }
            _arguments--;
            _sb.Append(')');
        }

        /// <summary>_( … ) and ^( … ) right after the base (primes as ').</summary>
        private void Attachments(MathNode? sub, MathNode? sup)
        {
            if (sup is not null && Primes(sup) is var primes and > 0)
            {
                _sb.Append('\'', primes);
                sup = null;
            }
            if (sub is not null)
            {
                _sb.Append("_(");
                _arguments++;
                Content(sub);
                _arguments--;
                _sb.Append(')');
            }
            if (sup is not null)
            {
                _sb.Append("^(");
                _arguments++;
                Content(sup);
                _arguments--;
                _sb.Append(')');
            }
        }

        /// <summary>
        /// An arrow Typst's math fonts stretch sideways, the long forms (⟶ …) as their short ones (the fonts' variants
        /// are of those); null for any other base.
        /// </summary>
        private static string? Stretchable(MathNode node) => (node switch
        {
            MathCommand command => command.Glyph,
            MathSymbol symbol => symbol.Text,
            _ => null,
        }) switch
        {
            "→" or "⟶" => "→",
            "←" or "⟵" => "←",
            "↔" or "⟷" => "↔",
            "⇒" or "⟹" => "⇒",
            "⇐" or "⟸" => "⇐",
            "⇔" or "⟺" => "⇔",
            "↦" or "⟼" => "↦",
            "↪" => "↪",
            "↩" => "↩",
            "⇌" => "⇌",
            _ => null,
        };

        private static int Primes(MathNode node)
        {
            var items = node is MathRow row ? row.Items : [node];
            return items.Count > 0 && items.All(i => i is MathCommand { Glyph: "′" } or MathSymbol { Text: "′" or "'" }) ? items.Count : 0;
        }

        private void Matrix(string delimiter, IReadOnlyList<IReadOnlyList<MathNode>> rows)
        {
            Atom("mat");
            _sb.Append("(delim: ").Append(delimiter);
            _arguments++;
            for (var r = 0; r < rows.Count; r++)
            {
                _sb.Append(r == 0 ? ", " : "; ");
                for (var c = 0; c < rows[r].Count; c++)
                {
                    if (c > 0)
                        _sb.Append(", ");
                    Content(rows[r][c]);
                }
            }
            _arguments--;
            _sb.Append(')');
        }

        /// <summary>A line of a pile split at its alignment marks.</summary>
        private static IReadOnlyList<MathNode> Cells(MathNode line)
        {
            if (line is not MathRow row || !row.Items.Any(i => i is MathAlign))
                return [line];
            var cells = new List<MathNode>();
            var cell = new List<MathNode>();
            foreach (var item in row.Items)
            {
                if (item is MathAlign)
                {
                    cells.Add(new MathRow(cell));
                    cell = [];
                }
                else
                {
                    cell.Add(item);
                }
            }
            cells.Add(new MathRow(cell));
            return cells;
        }

        private void Brackets(MathBrackets brackets)
        {
            string? function = (brackets.Left, brackets.Right) switch
            {
                ("|", "|") => "abs",
                ("‖", "‖") => "norm",
                ("⌊", "⌋") => "floor",
                ("⌈", "⌉") => "ceil",
                _ => null,
            };
            if (function is not null)
            {
                Call(function, brackets.Body);
                return;
            }
            if ((brackets.Left, brackets.Right) is ("(", ")") or ("[", "]") or ("{", "}"))
            {
                // Matched brackets stretch by themselves.
                Atom(brackets.Left);
                Node(brackets.Body);
                _sb.Append(brackets.Right);
                return;
            }
            Atom("lr");
            _sb.Append('(');
            _arguments++;
            if (brackets.Left.Length > 0)
                Symbols(brackets.Left);
            Node(brackets.Body);
            if (brackets.Right.Length > 0)
                Symbols(brackets.Right);
            _arguments--;
            _sb.Append(')');
        }

        private void Accent(MathAccent accent)
        {
            var body = accent.Body is MathRow { Items.Count: 1 } row ? row.Items[0] : accent.Body;
            var single = body is MathText { Text.Length: 1 } or MathCommand;
            string? function = accent.Command switch
            {
                "hat" or "tilde" or "acute" or "grave" or "dot" or "breve" => accent.Command,
                "check" => "caron",
                "ddot" => "dot.double",
                "bar" => single ? "macron" : "overline",
                "OVERLINE" or "overline" => "overline",
                "under" or "UNDERLINE" or "underline" => "underline",
                "vec" => "arrow",
                _ => null,
            };
            if (function is not null)
            {
                Call(function, accent.Body);
                return;
            }
            switch (accent.Command)
            {
                case "dyad" or "arch":
                    Atom("accent");
                    _sb.Append('(');
                    _arguments++;
                    Content(accent.Body);
                    _arguments--;
                    _sb.Append(accent.Command == "dyad" ? ", arrow.l.r)" : ", ⌢)");
                    break;
                case "not" when body is MathSymbol { Text.Length: 1 } or MathCommand { Glyph.Length: 1 }:
                    {
                        var glyph = body is MathSymbol symbol ? symbol.Text : ((MathCommand)body).Glyph;
                        Atom(Escape(glyph) + "̸");
                        break;
                    }
                case "not":
                    Call("cancel", accent.Body);
                    break;
                default:
                    Node(accent.Body);
                    break;
            }
        }

        private void Space(double width)
        {
            var name = width switch
            {
                _ when Math.Abs(width - 1 / 3.0) < 0.01 => "thin",
                _ when Math.Abs(width - 4 / 9.0) < 0.01 => "med",
                _ when Math.Abs(width - 5 / 9.0) < 0.01 => "thick",
                _ when Math.Abs(width - 2) < 0.01 => "quad",
                _ when Math.Abs(width - 4) < 0.01 => "wide",
                _ => null,
            };
            if (name is not null)
                Atom(name);
            else if (width > 0)
                Atom(string.Create(CultureInfo.InvariantCulture, $"#h({width * 0.5:0.###}em)"));
        }

        /// <summary>
        /// A variable: Latin and Greek letters each an atom of its own (several together would be one name), other
        /// writing (CJK) as quoted text.
        /// </summary>
        private void Letters(string text)
        {
            if (text.EnumerateRunes().Any(r => Rune.IsLetter(r) && r.Value > 0x3FF))
            {
                Atom(Quoted(text));
                return;
            }
            Symbols(text);
        }

        /// <summary>Each character an atom, those with a meaning in Typst's syntax escaped.</summary>
        private void Symbols(string text)
        {
            foreach (var rune in text.EnumerateRunes())
            {
                if (Rune.IsWhiteSpace(rune))
                    continue;
                Atom(Escape(rune.ToString()));
            }
        }

        /// <summary>
        /// A character as an atom: escaped when it means something in Typst's syntax (a comma or semicolon only inside
        /// an argument, where it would separate).
        /// </summary>
        private string Escape(string character) =>
            character is "\\" or "/" or "_" or "^" or "&" or "#" or "$" or "\"" or "(" or ")" or "[" or "]" or "{" or "}" or "'" or "@"
                || (_arguments > 0 && character is "," or ";")
                ? "\\" + character
                : character;

        private static string Quoted(string text) =>
            "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

        private static string Operator(string name) =>
            name == "lim" ? "lim"
            : WithLimits.Contains(name) ? $"op({Quoted(name)}, limits: #true)"
            : $"op({Quoted(name)})";
    }
}
