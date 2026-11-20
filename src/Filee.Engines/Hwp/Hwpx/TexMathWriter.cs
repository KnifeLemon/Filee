// The equation tree (MathNode) → TeX math, for the writers whose formats hold TeX: LaTeX ($…$ and \[…\]), Markdown
// ($…$, which MarkdownReader reads back through LatexMath) and AsciiDoc (latexmath). Equations read from TeX keep their
// source instead; this is for Word math (OMML), 한글 equations and MathML, which come as a tree.
//
// What it writes stays within amsmath and amssymb: symbols by the command LatexMath reads them from (≤ → \leq), large
// operators with limits, \frac / \genfrac, \sqrt[n], scripts (primes as '), \lim, function names (\sin, else
// \operatorname), matrix / pmatrix / bmatrix / vmatrix, cases, aligned / gathered / array for piles, \left … \right,
// accents (wide ones over several characters), \mathrm \mathit \mathbf \boldsymbol, the letters of \mathbb \mathcal
// \mathfrak, spaces, \not, and \text{…} for
// words and characters without a command. Greek capitals that look Latin are Latin letters, as TeX has no command for
// them. A character with no command at all is written as itself inside \text, which XeLaTeX and LuaLaTeX set.
//
// The tree's depth is bounded by its readers (EquationParser, LatexMath, OmmlReader: 200 at most); a tree deeper than
// MaxDepth is not written (null), and the caller keeps the equation's text.

using System.Globalization;
using System.Text;

namespace Filee.Engines.Hwp.Hwpx;

internal static class TexMathWriter
{
    /// <summary>Levels of a tree written; the walk recurses per level.</summary>
    public const int MaxDepth = 256;

    /// <summary>Function names LaTeX has a command for.</summary>
    private static readonly HashSet<string> Functions = new(StringComparer.Ordinal)
    {
        "sin", "cos", "tan", "cot", "sec", "csc", "arcsin", "arccos", "arctan", "sinh", "cosh", "tanh", "coth", "log",
        "ln", "lg", "exp", "det", "dim", "ker", "hom", "arg", "gcd", "max", "min", "sup", "inf", "limsup", "liminf",
        "Pr", "deg",
    };

    /// <summary>Large operators by their character.</summary>
    private static readonly Dictionary<string, string> BigOperators = new(StringComparer.Ordinal)
    {
        ["∑"] = "sum",
        ["∏"] = "prod",
        ["∐"] = "coprod",
        ["⋃"] = "bigcup",
        ["⋂"] = "bigcap",
        ["⋁"] = "bigvee",
        ["⋀"] = "bigwedge",
        ["⨁"] = "bigoplus",
        ["⨂"] = "bigotimes",
        ["⨀"] = "bigodot",
        ["⨄"] = "biguplus",
        ["∫"] = "int",
        ["∬"] = "iint",
        ["∭"] = "iiint",
        ["∮"] = "oint",
    };

    /// <summary>Characters whose TeX command LatexMath doesn't take, or whose command needs another package.</summary>
    private static readonly Dictionary<string, string> Extra = new(StringComparer.Ordinal)
    {
        ["⋅"] = "\\cdot",
        ["∙"] = "\\bullet",
        ["−"] = "-",
        ["′"] = "\\prime",
        ["°"] = "^{\\circ}",
        ["≔"] = ":=",
        ["≕"] = "=:",
        ["≢"] = "\\not\\equiv",
        ["ο"] = "o",
        ["∌"] = "\\not\\ni",
        ["⊄"] = "\\not\\subset",
        ["⊅"] = "\\not\\supset",
        ["≉"] = "\\not\\approx",
        ["⟨"] = "\\langle",
        ["⟩"] = "\\rangle",
        ["〈"] = "\\langle",
        ["〉"] = "\\rangle",
    };

    /// <summary>Greek capitals TeX writes as the Latin letter they look like.</summary>
    private const string GreekLookingLatin = "ΑAΒBΕEΖZΗHΙIΚKΜMΝNΟOΡPΤTΧX";

    /// <summary>The TeX of <paramref name="node"/> (without $ delimiters), or null when it nests deeper than <see cref="MaxDepth"/>.</summary>
    public static string? Write(MathNode node)
    {
        if (EquationScript.Deeper(node, MaxDepth))
            return null;
        var writer = new Writer();
        writer.Node(node, aligned: false);
        return writer.ToString().Trim();
    }

    private sealed class Writer
    {
        private readonly StringBuilder _sb = new();

        /// <summary>The last thing written is a control word: a letter after it needs a space.</summary>
        private bool _word;

        public override string ToString() => _sb.ToString();

        private void Word(string name)
        {
            _sb.Append('\\').Append(name);
            _word = true;
        }

        private void Raw(string text)
        {
            if (text.Length == 0)
                return;
            if (_word && char.IsAsciiLetter(text[0]))
                _sb.Append(' ');
            _sb.Append(text);
            _word = text.Length > 1 && text[0] == '\\' && text.Skip(1).All(char.IsAsciiLetter);
        }

        private void Group(MathNode node, bool aligned = false)
        {
            Raw("{");
            Node(node, aligned);
            Raw("}");
        }

        /// <param name="aligned">Inside an aligned pile, where an alignment mark is an &amp;.</param>
        public void Node(MathNode node, bool aligned)
        {
            switch (node)
            {
                case MathRow row:
                    foreach (var item in row.Items)
                        Node(item, aligned);
                    break;
                case MathText text:
                    Characters(text.Text);
                    break;
                case MathNumber number:
                    Characters(number.Digits);
                    break;
                case MathSymbol symbol:
                    Characters(symbol.Text);
                    break;
                case MathCommand command:
                    Characters(command.Glyph);
                    break;
                case MathQuoted quoted:
                    Text(quoted.Text);
                    break;
                case MathFunction function:
                    if (Functions.Contains(function.Name))
                        Word(function.Name);
                    else if (function.Name == "mod")
                        Word("bmod");
                    else
                    {
                        Word("operatorname");
                        Raw("{");
                        Text(function.Name, plain: true);
                        Raw("}");
                    }
                    break;
                case MathFraction fraction:
                    if (fraction.Bar)
                        Word("frac");
                    else
                        Raw("\\genfrac{}{}{0pt}{}");
                    Group(fraction.Numerator);
                    Group(fraction.Denominator);
                    break;
                case MathRoot root:
                    Word("sqrt");
                    if (root.Index is not null)
                    {
                        Raw("[");
                        Node(root.Index, aligned: false);
                        Raw("]");
                    }
                    Group(root.Body);
                    break;
                case MathScripts scripts:
                    Base(scripts.Base);
                    Scripts(scripts.Sub, scripts.Sup);
                    break;
                case MathBigOperator big:
                    if (BigOperators.TryGetValue(big.Glyph, out var name))
                        Word(name);
                    else
                    {
                        Raw("\\mathop{");
                        Text(big.Glyph);
                        Raw("}");
                    }
                    Scripts(big.Lower, big.Upper);
                    break;
                case MathLimit limit:
                    Word("lim");
                    Scripts(limit.Lower, null);
                    break;
                case MathMatrix matrix:
                    Environment(matrix.Command switch
                    {
                        "pmatrix" => "pmatrix",
                        "bmatrix" => "bmatrix",
                        "dmatrix" or "vmatrix" => "vmatrix",
                        _ => "matrix",
                    }, null, matrix.Rows);
                    break;
                case MathCases cases:
                    Environment("cases", null, cases.Rows);
                    break;
                case MathPile pile:
                    Pile(pile);
                    break;
                case MathBrackets { Left: "(", Right: ")", Body: MathFraction { Bar: false } binomial }:
                    Word("binom");
                    Group(binomial.Numerator);
                    Group(binomial.Denominator);
                    break;
                case MathBrackets brackets:
                    Word("left");
                    Delimiter(brackets.Left);
                    Node(brackets.Body, aligned);
                    Word("right");
                    Delimiter(brackets.Right);
                    break;
                case MathAccent accent:
                    Accent(accent);
                    break;
                case MathStyled styled:
                    Styled(styled);
                    break;
                case MathSpace space:
                    Space(space.Width);
                    break;
                case MathAlign:
                    if (aligned)
                        Raw("&");
                    break;
            }
        }

        /// <summary>The base of scripts: braced unless it is one item TeX takes as it is.</summary>
        private void Base(MathNode node)
        {
            var single = node is MathRow { Items.Count: 1 } row ? row.Items[0] : node;
            if (single is MathText { Text.Length: 1 } or MathNumber or MathSymbol { Text.Length: 1 } or MathCommand or MathFunction
                or MathBrackets or MathAccent or MathStyled or MathRoot or MathFraction or MathLimit)
                Node(single, aligned: false);
            else
                Group(node);
        }

        private void Scripts(MathNode? sub, MathNode? sup)
        {
            // f', f'': a superscript of primes only.
            if (sup is not null && Primes(sup) is var primes and > 0)
            {
                Raw(new string('\'', primes));
                sup = null;
            }
            if (sub is not null)
            {
                Raw("_");
                Group(sub);
            }
            if (sup is not null)
            {
                Raw("^");
                Group(sup);
            }
        }

        private static int Primes(MathNode node)
        {
            var items = node is MathRow row ? row.Items : [node];
            return items.Count > 0 && items.All(i => i is MathCommand { Glyph: "′" } or MathSymbol { Text: "′" or "'" }) ? items.Count : 0;
        }

        private void Environment(string name, string? columns, IReadOnlyList<IReadOnlyList<MathNode>> rows)
        {
            Raw("\\begin{" + name + "}");
            if (columns is not null)
                Raw("{" + columns + "}");
            for (var r = 0; r < rows.Count; r++)
            {
                if (r > 0)
                    Raw(" \\\\ ");
                for (var c = 0; c < rows[r].Count; c++)
                {
                    if (c > 0)
                        Raw(" & ");
                    Node(rows[r][c], aligned: false);
                }
            }
            Raw("\\end{" + name + "}");
        }

        /// <summary>Lines with alignment marks as aligned (the marks as &amp;); others gathered, or as an array to the left or right.</summary>
        private void Pile(MathPile pile)
        {
            var marks = pile.Rows.Any(r => r is MathRow row && row.Items.Any(i => i is MathAlign));
            var (name, columns) = marks ? ("aligned", (string?)null) : pile.Align switch
            {
                "lpile" => ("array", "l"),
                "rpile" => ("array", "r"),
                _ => ("gathered", null),
            };
            Raw("\\begin{" + name + "}");
            if (columns is not null)
                Raw("{" + columns + "}");
            for (var r = 0; r < pile.Rows.Count; r++)
            {
                if (r > 0)
                    Raw(" \\\\ ");
                Node(pile.Rows[r], aligned: marks);
            }
            Raw("\\end{" + name + "}");
        }

        private void Delimiter(string bracket)
        {
            switch (bracket)
            {
                case "":
                    Raw(".");
                    break;
                case "(" or ")" or "[" or "]" or "|" or "/":
                    Raw(bracket);
                    break;
                case "{" or "}":
                    Raw("\\" + bracket);
                    break;
                case "‖":
                    Raw("\\|");
                    break;
                case "⟨" or "〈":
                    Word("langle");
                    break;
                case "⟩" or "〉":
                    Word("rangle");
                    break;
                case "⌈":
                    Word("lceil");
                    break;
                case "⌉":
                    Word("rceil");
                    break;
                case "⌊":
                    Word("lfloor");
                    break;
                case "⌋":
                    Word("rfloor");
                    break;
                default:
                    // Not a delimiter TeX stretches: no bracket, and the character as it is.
                    Raw(".");
                    Characters(bracket);
                    break;
            }
        }

        private void Accent(MathAccent accent)
        {
            var body = accent.Body is MathRow { Items.Count: 1 } row ? row.Items[0] : accent.Body;
            var single = body is MathText { Text.Length: 1 } or MathCommand;
            string? command = accent.Command switch
            {
                "hat" => single ? "hat" : "widehat",
                "tilde" => single ? "tilde" : "widetilde",
                "check" or "acute" or "grave" or "dot" or "ddot" or "breve" => accent.Command,
                "bar" => single ? "bar" : "overline",
                "OVERLINE" or "overline" => "overline",
                "under" or "UNDERLINE" or "underline" => "underline",
                "vec" => single ? "vec" : "overrightarrow",
                "dyad" => "overleftrightarrow",
                _ => null,
            };
            if (command is not null)
            {
                Word(command);
                Group(accent.Body);
                return;
            }
            switch (accent.Command)
            {
                case "arch":
                    Raw("\\overset{\\frown}");
                    Group(accent.Body);
                    break;
                case "not":
                    Word("not");
                    if (body is MathSymbol or MathCommand)
                        Node(body, aligned: false);
                    else
                        Group(accent.Body);
                    break;
                default:
                    Node(accent.Body, aligned: false);
                    break;
            }
        }

        private void Styled(MathStyled styled)
        {
            switch (styled.Command)
            {
                case "rm":
                    Word("mathrm");
                    Group(styled.Body);
                    break;
                case "it":
                    Word("mathit");
                    Group(styled.Body);
                    break;
                case "bold" when styled.Body is MathStyled { Command: "rm" } upright:
                    Word("mathbf");
                    Group(upright.Body);
                    break;
                case "bold":
                    Word("boldsymbol");
                    Group(styled.Body);
                    break;
                default:
                    Node(styled.Body, aligned: false);
                    break;
            }
        }

        /// <summary>A space of <paramref name="width"/> (1 is half the font size, see MathLayout).</summary>
        private void Space(double width)
        {
            var known = new (double Width, string Tex)[]
            {
                (1 / 3.0, "\\,"), (4 / 9.0, "\\:"), (5 / 9.0, "\\;"), (2 / 3.0, "\\ "), (1, "\\enspace"), (2, "\\quad"), (4, "\\qquad"),
            };
            foreach (var (w, tex) in known)
            {
                if (Math.Abs(w - width) < 0.01)
                {
                    Raw(tex);
                    return;
                }
            }
            if (width <= 0)
                return;
            Raw(string.Create(CultureInfo.InvariantCulture, $"\\hspace{{{width * 0.5:0.###}em}}"));
        }

        /// <summary>
        /// Characters of a variable, number or symbol: ASCII letters and digits as they are, TeX's specials escaped,
        /// symbols by their command, letters without one (CJK) in \text.
        /// </summary>
        private void Characters(string text)
        {
            var pending = new StringBuilder();
            void Flush()
            {
                if (pending.Length > 0)
                    Text(pending.ToString());
                pending.Clear();
            }

            foreach (var rune in text.EnumerateRunes())
            {
                var s = rune.ToString();
                var c = rune.IsBmp ? (char)rune.Value : '\0';
                string? tex = c switch
                {
                    _ when char.IsAsciiLetterOrDigit(c) => s,
                    '+' or '-' or '=' or '<' or '>' or '(' or ')' or '[' or ']' or ',' or '.' or '/' or '|' or '!' or '*' or ':' or ';' or '?' or '\'' or '@' => s,
                    '{' or '}' or '#' or '$' or '%' or '&' or '_' => "\\" + s,
                    '\\' => "\\backslash",
                    '~' => "\\sim",
                    '^' => "\\hat{}",
                    '"' => "\\text{\"}",
                    ' ' => "\\ ",
                    _ when GreekLookingLatin.IndexOf(c) is var at and >= 0 && at % 2 == 0 => GreekLookingLatin[at + 1].ToString(),
                    _ when Extra.TryGetValue(s, out var extra) => extra,
                    _ when LatexMath.SymbolCommand(s) is { } command => "\\" + command,
                    _ when LatexMath.AlphabetLetter(s) is { } letter => $"\\{letter.Command}{{{letter.Letter}}}",
                    _ => null,
                };
                if (tex is null)
                {
                    pending.Append(s);
                    continue;
                }
                Flush();
                if (tex.StartsWith('\\') && tex.Skip(1).All(char.IsAsciiLetter))
                    Word(tex[1..]);
                else
                    Raw(tex);
            }
            Flush();
        }

        /// <summary>Text (\text{…}) with the characters TeX's text mode treats specially escaped.</summary>
        private void Text(string text, bool plain = false)
        {
            if (text.Length == 0)
                return;
            var escaped = new StringBuilder(text.Length + 8);
            foreach (var c in text)
            {
                escaped.Append(c switch
                {
                    '\\' => "\\textbackslash{}",
                    '{' or '}' or '#' or '$' or '%' or '&' or '_' => "\\" + c,
                    '~' => "\\textasciitilde{}",
                    '^' => "\\textasciicircum{}",
                    _ => c.ToString(),
                });
            }
            if (plain)
                Raw(escaped.ToString());
            else
                Raw("\\text{" + escaped + "}");
        }
    }
}
