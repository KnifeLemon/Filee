// The equation tree (MathNode) ↔ StarMath 5.0, LibreOffice Math's own language: an OpenDocument formula object keeps
// its source text as a MathML annotation (encoding "StarMath 5.0"), which LibreOffice's formula editor shows and edits.
//
// Write: every token apart (so letters never run into a command word: a variable is written one letter at a time),
// groups braced. Fractions "{a} over {b}", stacks without a bar binom, sqrt / nroot, scripts {x}_{i}^{n}, content over
// and under a base csup / csub, large operators sum / prod / int … with from {…} to {…} (other ones as their glyph
// with csub / csup), lim from {…}, functions func name, Greek letters %alpha, the operators and relations StarMath
// names (times, <=, in, toward …), stretched brackets left … right (none for a missing side), matrices matrix {a # b ##
// c # d} in their brackets, cases as a matrix after left lbrace … right none, piles stack {… # …} (aligned lines as a
// matrix of right and left cells), accents (hat / widehat …), nitalic / ital / bold, spaces ~ and `. A bracket or brace
// on its own is escaped (\(, \lbrace); characters with no StarMath name, and words, are quoted text.
//
// Read: the same language back into the tree, StarMath's precedence kept where it shows (over binds the terms beside
// it, scripts bind tighter, operators between terms stay in the row); color, size and font are passed over, unknown
// words are variables. Nesting is bounded (MaxDepth): a deeper formula is read as its words and numbers in a row.

using System.Globalization;
using System.Text;
using Filee.Engines.Hwp.Hwpx.Docx;

namespace Filee.Engines.Hwp.Hwpx;

internal static class StarMath
{
    /// <summary>The annotation encoding LibreOffice writes and reads for its source text.</summary>
    public const string Encoding = "StarMath 5.0";

    /// <inheritdoc cref="TexMathWriter.MaxDepth"/>
    public const int MaxDepth = TexMathWriter.MaxDepth;

    /// <summary>Tokens read from one formula, at most; a longer text is read as far as this.</summary>
    private const int MaxTokens = 100_000;

    // ───────────────────────── Names ─────────────────────────

    /// <summary>Operators, relations and symbols StarMath writes as words or ASCII signs, by their character.</summary>
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        ["×"] = "times",
        ["÷"] = "div",
        ["·"] = "cdot",
        ["⋅"] = "cdot",
        ["±"] = "+-",
        ["∓"] = "-+",
        ["∘"] = "circ",
        ["⊕"] = "oplus",
        ["⊖"] = "ominus",
        ["⊗"] = "otimes",
        ["⊙"] = "odot",
        ["⊘"] = "odivide",
        ["∧"] = "and",
        ["∨"] = "or",
        ["¬"] = "neg",
        ["≤"] = "<=",
        ["≥"] = ">=",
        ["≠"] = "<>",
        ["≪"] = "<<",
        ["≫"] = ">>",
        ["⩽"] = "leslant",
        ["⩾"] = "geslant",
        ["≈"] = "approx",
        ["∼"] = "sim",
        ["≃"] = "simeq",
        ["≡"] = "equiv",
        ["∝"] = "prop",
        ["∥"] = "parallel",
        ["⊥"] = "ortho",
        ["∣"] = "divides",
        ["∤"] = "ndivides",
        ["≺"] = "prec",
        ["≻"] = "succ",
        ["→"] = "toward",
        ["⇒"] = "drarrow",
        ["⇐"] = "dlarrow",
        ["⇔"] = "dlrarrow",
        ["←"] = "leftarrow",
        ["↑"] = "uparrow",
        ["↓"] = "downarrow",
        ["∈"] = "in",
        ["∉"] = "notin",
        ["∋"] = "owns",
        ["⊂"] = "subset",
        ["⊆"] = "subseteq",
        ["⊃"] = "supset",
        ["⊇"] = "supseteq",
        ["⊄"] = "nsubset",
        ["⊈"] = "nsubseteq",
        ["⊅"] = "nsupset",
        ["⊉"] = "nsupseteq",
        ["∪"] = "union",
        ["∩"] = "intersection",
        ["∖"] = "setminus",
        ["∅"] = "emptyset",
        ["ℵ"] = "aleph",
        ["ℕ"] = "setN",
        ["ℤ"] = "setZ",
        ["ℚ"] = "setQ",
        ["ℝ"] = "setR",
        ["ℂ"] = "setC",
        ["∞"] = "infinity",
        ["∂"] = "partial",
        ["∇"] = "nabla",
        ["∃"] = "exists",
        ["∄"] = "notexists",
        ["∀"] = "forall",
        ["ℏ"] = "hbar",
        ["ƛ"] = "lambdabar",
        ["ℜ"] = "Re",
        ["ℑ"] = "Im",
        ["℘"] = "wp",
        ["…"] = "dotslow",
        ["⋯"] = "dotsaxis",
        ["⋮"] = "dotsvert",
        ["⋰"] = "dotsup",
        ["⋱"] = "dotsdown",
    };

    /// <summary>The same, read back: words ignoring case, as LibreOffice reads them, plus their other spellings.</summary>
    private static readonly Dictionary<string, string> Glyphs = Reverse();

    private static Dictionary<string, string> Reverse()
    {
        var glyphs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (glyph, name) in Names)
            glyphs.TryAdd(name, glyph);
        glyphs["infty"] = "∞";
        glyphs["neq"] = "≠";
        glyphs["le"] = "≤";
        glyphs["ge"] = "≥";
        glyphs["ll"] = "≪";
        glyphs["gg"] = "≫";
        glyphs["rightarrow"] = "→";
        glyphs["dotsdiag"] = "⋰";
        glyphs["backepsilon"] = "∍";
        glyphs["def"] = "≝";
        return glyphs;
    }

    /// <summary>Greek letters by their StarMath names (%alpha; %ialpha is the italic one, read the same).</summary>
    private static readonly Dictionary<string, string> Greek = new(StringComparer.Ordinal)
    {
        ["alpha"] = "α",
        ["beta"] = "β",
        ["gamma"] = "γ",
        ["delta"] = "δ",
        ["epsilon"] = "ε",
        ["varepsilon"] = "ϵ",
        ["zeta"] = "ζ",
        ["eta"] = "η",
        ["theta"] = "θ",
        ["vartheta"] = "ϑ",
        ["iota"] = "ι",
        ["kappa"] = "κ",
        ["lambda"] = "λ",
        ["mu"] = "μ",
        ["nu"] = "ν",
        ["xi"] = "ξ",
        ["omicron"] = "ο",
        ["pi"] = "π",
        ["varpi"] = "ϖ",
        ["rho"] = "ρ",
        ["varrho"] = "ϱ",
        ["sigma"] = "σ",
        ["varsigma"] = "ς",
        ["tau"] = "τ",
        ["upsilon"] = "υ",
        ["phi"] = "φ",
        ["varphi"] = "ϕ",
        ["chi"] = "χ",
        ["psi"] = "ψ",
        ["omega"] = "ω",
        ["ALPHA"] = "Α",
        ["BETA"] = "Β",
        ["GAMMA"] = "Γ",
        ["DELTA"] = "Δ",
        ["EPSILON"] = "Ε",
        ["ZETA"] = "Ζ",
        ["ETA"] = "Η",
        ["THETA"] = "Θ",
        ["IOTA"] = "Ι",
        ["KAPPA"] = "Κ",
        ["LAMBDA"] = "Λ",
        ["MU"] = "Μ",
        ["NU"] = "Ν",
        ["XI"] = "Ξ",
        ["OMICRON"] = "Ο",
        ["PI"] = "Π",
        ["RHO"] = "Ρ",
        ["SIGMA"] = "Σ",
        ["TAU"] = "Τ",
        ["UPSILON"] = "Υ",
        ["PHI"] = "Φ",
        ["CHI"] = "Χ",
        ["PSI"] = "Ψ",
        ["OMEGA"] = "Ω",
    };

    private static readonly Dictionary<string, string> GreekNames = Greek.GroupBy(p => p.Value).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.Ordinal);

    /// <summary>Large operators StarMath names, by glyph.</summary>
    private static readonly Dictionary<string, string> Operators = new(StringComparer.Ordinal)
    {
        ["∑"] = "sum",
        ["∏"] = "prod",
        ["∐"] = "coprod",
        ["∫"] = "int",
        ["∬"] = "iint",
        ["∭"] = "iiint",
        ["∮"] = "lint",
        ["∯"] = "llint",
        ["∰"] = "lllint",
    };

    private static readonly Dictionary<string, string> OperatorGlyphs =
        Operators.ToDictionary(p => p.Value, p => p.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Function names StarMath knows by themselves (others are written after func).</summary>
    private static readonly HashSet<string> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        "sin", "cos", "tan", "cot", "sinh", "cosh", "tanh", "coth", "arcsin", "arccos", "arctan", "arccot", "arsinh", "arcosh",
        "artanh", "arcoth", "ln", "log", "exp",
    };

    /// <summary>Opening brackets after left (and on their own, paired) with the closing one each takes.</summary>
    private static readonly Dictionary<string, (string Glyph, string Close)> Openers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["("] = ("(", ")"),
        ["["] = ("[", "]"),
        ["lbrace"] = ("{", "rbrace"),
        ["langle"] = ("⟨", "rangle"),
        ["lline"] = ("|", "rline"),
        ["ldline"] = ("‖", "rdline"),
        ["lfloor"] = ("⌊", "rfloor"),
        ["lceil"] = ("⌈", "rceil"),
        ["ldbracket"] = ("⟦", "rdbracket"),
    };

    /// <summary>Closing brackets after right, by word or sign.</summary>
    private static readonly Dictionary<string, string> Closers = new(StringComparer.OrdinalIgnoreCase)
    {
        [")"] = ")",
        ["]"] = "]",
        ["rbrace"] = "}",
        ["rangle"] = "⟩",
        ["rline"] = "|",
        ["rdline"] = "‖",
        ["rfloor"] = "⌋",
        ["rceil"] = "⌉",
        ["rdbracket"] = "⟧",
    };

    /// <summary>Accents by StarMath's word, as MathAccent's command.</summary>
    private static readonly Dictionary<string, string> Accents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["acute"] = "acute",
        ["grave"] = "grave",
        ["breve"] = "breve",
        ["dot"] = "dot",
        ["ddot"] = "ddot",
        ["dddot"] = "ddot",
        ["bar"] = "bar",
        ["vec"] = "vec",
        ["widevec"] = "vec",
        ["harpoon"] = "vec",
        ["wideharpoon"] = "vec",
        ["tilde"] = "tilde",
        ["widetilde"] = "tilde",
        ["hat"] = "hat",
        ["widehat"] = "hat",
        ["check"] = "check",
        ["overline"] = "bar",
        ["underline"] = "UNDERLINE",
        ["overstrike"] = "not",
        ["circle"] = "dot",
    };

    // ───────────────────────── Writing ─────────────────────────

    /// <summary>The StarMath text of <paramref name="node"/>, or null when it nests deeper than <see cref="MaxDepth"/>.</summary>
    public static string? Write(MathNode node)
    {
        if (EquationScript.Deeper(node, MaxDepth))
            return null;
        var writer = new Writer();
        writer.Node(node);
        return writer.ToString();
    }

    private sealed class Writer
    {
        private readonly StringBuilder _sb = new();

        public override string ToString() => _sb.ToString().Trim();

        private void Token(string text)
        {
            if (_sb.Length > 0 && _sb[^1] != ' ')
                _sb.Append(' ');
            _sb.Append(text);
        }

        /// <summary>A node as one braced group.</summary>
        private void Group(MathNode? node)
        {
            Token("{");
            if (node is not null)
                Node(node);
            Token("}");
        }

        public void Node(MathNode node)
        {
            switch (node)
            {
                case MathRow row:
                    Row(row.Items);
                    break;
                case MathText text:
                    Letters(text.Text);
                    break;
                case MathNumber number:
                    if (number.Digits.Length > 0 && number.Digits.All(c => char.IsAsciiDigit(c) || c == '.') && char.IsAsciiDigit(number.Digits[0]))
                        Token(number.Digits);
                    else
                        Token(Quoted(number.Digits));
                    break;
                case MathSymbol symbol:
                    Symbol(symbol.Text);
                    break;
                case MathCommand command:
                    Symbol(command.Glyph);
                    break;
                case MathQuoted quoted:
                    if (quoted.Text.Length > 0)
                        Token(Quoted(quoted.Text));
                    break;
                case MathFunction function:
                    if (function.Name.Length > 0 && function.Name.All(char.IsAsciiLetter))
                    {
                        if (!Functions.Contains(function.Name))
                            Token("func");
                        Token(function.Name);
                    }
                    else
                    {
                        Token(Quoted(function.Name));
                    }
                    break;
                case MathFraction { Bar: true } fraction:
                    Group(fraction.Numerator);
                    Token("over");
                    Group(fraction.Denominator);
                    break;
                case MathFraction stack:
                    Token("binom");
                    Group(stack.Numerator);
                    Group(stack.Denominator);
                    break;
                case MathRoot { Index: null } root:
                    Token("sqrt");
                    Group(root.Body);
                    break;
                case MathRoot root:
                    Token("nroot");
                    Group(root.Index);
                    Group(root.Body);
                    break;
                case MathScripts scripts:
                    Group(scripts.Base);
                    if (scripts.Sub is not null)
                    {
                        Token("_");
                        Group(scripts.Sub);
                    }
                    if (scripts.Sup is not null)
                    {
                        Token("^");
                        Group(scripts.Sup);
                    }
                    break;
                case MathStack stack:
                    Group(stack.Base);
                    Stacked(stack.Under, stack.Over);
                    break;
                case MathBigOperator big:
                    if (Operators.TryGetValue(big.Glyph, out var name))
                    {
                        Token(name);
                        Limits(big.Lower, big.Upper);
                    }
                    else
                    {
                        Group(new MathQuoted(big.Glyph));
                        Stacked(big.Lower, big.Upper);
                    }
                    break;
                case MathLimit limit:
                    Token("lim");
                    Limits(limit.Lower, null);
                    break;
                case MathMatrix matrix:
                    var (open, close) = matrix.Command switch
                    {
                        "pmatrix" => ("(", ")"),
                        "bmatrix" => ("[", "]"),
                        "dmatrix" or "vmatrix" => ("lline", "rline"),
                        _ => ("", ""),
                    };
                    if (open.Length > 0)
                        Token("left " + open);
                    Matrix(matrix.Rows, aligned: false);
                    if (close.Length > 0)
                        Token("right " + close);
                    break;
                case MathCases cases:
                    Token("left lbrace");
                    Matrix(cases.Rows, aligned: false);
                    Token("right none");
                    break;
                case MathPile pile:
                    Pile(pile);
                    break;
                case MathBrackets brackets:
                    Token("left " + Bracket(brackets.Left, opening: true));
                    Group(brackets.Body);
                    Token("right " + Bracket(brackets.Right, opening: false));
                    break;
                case MathAccent accent:
                    Accent(accent);
                    break;
                case MathStyled styled:
                    switch (styled.Command)
                    {
                        case "rm":
                            Token("nitalic");
                            Group(styled.Body);
                            break;
                        case "it":
                            Token("ital");
                            Group(styled.Body);
                            break;
                        case "bold":
                            Token("bold");
                            Group(styled.Body);
                            break;
                        default:
                            Node(styled.Body);
                            break;
                    }
                    break;
                case MathSpace space:
                    // ~ a space, ` a quarter of one.
                    var quarters = (int)Math.Clamp(Math.Round(space.Width * 4), 0, 400);
                    for (var i = 0; i < quarters / 4; i++)
                        Token("~");
                    for (var i = 0; i < quarters % 4; i++)
                        Token("`");
                    break;
            }
        }

        /// <summary>The items of a row; a large operator or lim at its end gets an empty body, which StarMath wants.</summary>
        private void Row(IReadOnlyList<MathNode> items)
        {
            for (var i = 0; i < items.Count; i++)
            {
                Node(items[i]);
                if (items[i] is MathLimit || (items[i] is MathBigOperator big && Operators.ContainsKey(big.Glyph)))
                {
                    if (i + 1 == items.Count || items[i + 1] is MathAlign)
                        Token("{}");
                }
            }
        }

        private void Limits(MathNode? lower, MathNode? upper)
        {
            if (lower is not null)
            {
                Token("from");
                Group(lower);
            }
            if (upper is not null)
            {
                Token("to");
                Group(upper);
            }
        }

        private void Stacked(MathNode? under, MathNode? over)
        {
            if (over is not null)
            {
                Token("csup");
                Group(over);
            }
            if (under is not null)
            {
                Token("csub");
                Group(under);
            }
        }

        /// <summary>A matrix of cells; <paramref name="aligned"/> sets its columns right and left in turn (aligned lines).</summary>
        private void Matrix(IReadOnlyList<IReadOnlyList<MathNode>> rows, bool aligned)
        {
            var columns = Math.Max(1, rows.Count == 0 ? 1 : rows.Max(r => r.Count));
            Token("matrix");
            Token("{");
            for (var r = 0; r < rows.Count; r++)
            {
                if (r > 0)
                    Token("##");
                for (var c = 0; c < columns; c++)
                {
                    if (c > 0)
                        Token("#");
                    if (aligned)
                        Token(c % 2 == 0 ? "alignr" : "alignl");
                    Group(c < rows[r].Count ? rows[r][c] : null);
                }
            }
            if (rows.Count == 0)
                Token("{}");
            Token("}");
        }

        private void Pile(MathPile pile)
        {
            if (pile.Rows.Any(r => r is MathRow row && row.Items.Any(i => i is MathAlign)))
            {
                Matrix([.. pile.Rows.Select(Cells)], aligned: true);
                return;
            }
            var align = pile.Align switch
            {
                "lpile" => "alignl",
                "rpile" => "alignr",
                _ => null,
            };
            Token("stack");
            Token("{");
            for (var r = 0; r < pile.Rows.Count; r++)
            {
                if (r > 0)
                    Token("#");
                if (align is not null)
                    Token(align);
                Group(pile.Rows[r]);
            }
            if (pile.Rows.Count == 0)
                Token("{}");
            Token("}");
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

        private void Accent(MathAccent accent)
        {
            var body = accent.Body is MathRow { Items: [var only] } ? only : accent.Body;
            var single = body is MathText { Text.Length: 1 } or MathCommand or MathNumber { Digits.Length: 1 };
            if (accent.Command == "not" && body is MathSymbol or MathCommand)
            {
                var glyph = body is MathSymbol symbol ? symbol.Text : ((MathCommand)body).Glyph;
                var negated = glyph switch
                {
                    "=" => "<>",
                    "∈" => "notin",
                    "⊂" => "nsubset",
                    "⊆" => "nsubseteq",
                    "⊃" => "nsupset",
                    "⊇" => "nsupseteq",
                    "∃" => "notexists",
                    "∣" or "|" => "ndivides",
                    _ => null,
                };
                if (negated is not null)
                {
                    Token(negated);
                    return;
                }
            }
            var word = accent.Command switch
            {
                "hat" => single ? "hat" : "widehat",
                "tilde" => single ? "tilde" : "widetilde",
                "vec" or "dyad" => single ? "vec" : "widevec",
                "bar" => single ? "bar" : "overline",
                "OVERLINE" or "overline" or "arch" => "overline",
                "UNDERLINE" or "underline" or "under" => "underline",
                "check" or "acute" or "grave" or "breve" or "dot" or "ddot" => accent.Command,
                "not" => "overstrike",
                _ => null,
            };
            if (word is null)
            {
                Node(accent.Body);
                return;
            }
            Token(word);
            Group(accent.Body);
        }

        /// <summary>Letters one at a time (so none run into a command word), Greek ones by name, other text quoted.</summary>
        private void Letters(string text)
        {
            if (text.Length == 0)
                return;
            if (text.All(char.IsAsciiLetterOrDigit))
            {
                foreach (var letter in text)
                    Token(letter.ToString());
                return;
            }
            if (GreekNames.TryGetValue(text, out var greek))
                Token("%" + greek);
            else if (Names.TryGetValue(text, out var name))
                Token(name); // ℝ, ℵ, ℏ …
            else
                Token(Quoted(text));
        }

        /// <summary>A sign or symbol: ASCII operators as they are, named ones by name, a bracket on its own escaped.</summary>
        private void Symbol(string text)
        {
            var glyph = text == "−" ? "-" : text;
            if (glyph is "+" or "-" or "=" or "<" or ">" or "/" or "*" or "," or ".")
            {
                Token(glyph);
                return;
            }
            var escaped = glyph switch
            {
                "(" => "\\(",
                ")" => "\\)",
                "[" => "\\[",
                "]" => "\\]",
                "{" => "\\lbrace",
                "}" => "\\rbrace",
                "|" => "\\lline",
                "‖" => "\\ldline",
                "⟨" or "〈" => "\\langle",
                "⟩" or "〉" => "\\rangle",
                "⌊" => "\\lfloor",
                "⌋" => "\\rfloor",
                "⌈" => "\\lceil",
                "⌉" => "\\rceil",
                _ => null,
            };
            if (escaped is not null)
                Token(escaped);
            else if (Names.TryGetValue(glyph, out var name))
                Token(name);
            else if (GreekNames.TryGetValue(glyph, out var greek))
                Token("%" + greek);
            else if (glyph.Length > 0)
                Token(Quoted(glyph));
        }

        private static string Bracket(string glyph, bool opening) => (glyph, opening) switch
        {
            ("(", _) => "(",
            (")", _) => ")",
            ("[", _) => "[",
            ("]", _) => "]",
            ("{", _) => "lbrace",
            ("}", _) => "rbrace",
            ("|", true) => "lline",
            ("|", false) => "rline",
            ("‖", true) => "ldline",
            ("‖", false) => "rdline",
            ("⟨" or "〈", _) => "langle",
            ("⟩" or "〉", _) => "rangle",
            ("⌊", _) => "lfloor",
            ("⌋", _) => "rfloor",
            ("⌈", _) => "lceil",
            ("⌉", _) => "rceil",
            ("⟦", _) => "ldbracket",
            ("⟧", _) => "rdbracket",
            _ => "none",
        };

        private static string Quoted(string text) => "\"" + text.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    // ───────────────────────── Reading ─────────────────────────

    private enum Kind
    {
        Word,
        Number,
        Text,
        Greek,
        Escaped,
        Sign,
    }

    private readonly record struct Item(Kind Kind, string Text);

    /// <summary>The tree of a StarMath formula; null when it shows nothing.</summary>
    public static MathNode? Read(string text)
    {
        var tokens = Tokens(text);
        MathNode node;
        try
        {
            node = new Parser(tokens).Formula();
        }
        catch (TooDeepException)
        {
            // Too deep: its words, numbers and texts in a row.
            var items = new List<MathNode>();
            foreach (var token in tokens)
            {
                if (token.Kind == Kind.Text)
                    items.Add(new MathQuoted(token.Text));
                else if (token.Kind is Kind.Word or Kind.Number && !Glyphs.ContainsKey(token.Text))
                    OmmlReader.Tokens(token.Text, items, upright: token.Kind == Kind.Number, bold: false);
            }
            node = new MathRow(items);
        }
        return IsEmpty(node) ? null : node;
    }

    private static List<Item> Tokens(string text)
    {
        var tokens = new List<Item>();
        var i = 0;
        while (i < text.Length && tokens.Count < MaxTokens)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] == '"')
                        i++;
                    sb.Append(text[i]);
                    i++;
                }
                i++;
                tokens.Add(new Item(Kind.Text, sb.ToString()));
                continue;
            }
            if (c == '%')
            {
                var start = ++i;
                while (i < text.Length && char.IsAsciiLetter(text[i]))
                    i++;
                tokens.Add(new Item(Kind.Greek, text[start..i]));
                continue;
            }
            if (c == '\\' && i + 1 < text.Length)
            {
                var start = ++i;
                if (char.IsAsciiLetter(text[i]))
                {
                    while (i < text.Length && char.IsAsciiLetter(text[i]))
                        i++;
                }
                else
                {
                    i++;
                }
                tokens.Add(new Item(Kind.Escaped, text[start..i]));
                continue;
            }
            if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1])))
            {
                var start = i;
                while (i < text.Length && (char.IsAsciiDigit(text[i]) || (text[i] == '.' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1]))))
                    i++;
                tokens.Add(new Item(Kind.Number, text[start..i]));
                continue;
            }
            if (char.IsLetter(c))
            {
                var start = i;
                while (i < text.Length && char.IsLetter(text[i]))
                    i++;
                tokens.Add(new Item(Kind.Word, text[start..i]));
                continue;
            }
            // Signs of two or three characters first.
            string? sign = null;
            foreach (var candidate in new[] { "<?>", "##", "<=", ">=", "<>", "<<", ">>", "+-", "-+" })
            {
                if (string.CompareOrdinal(text, i, candidate, 0, candidate.Length) == 0)
                {
                    sign = candidate;
                    break;
                }
            }
            if (sign is null && char.IsHighSurrogate(c) && i + 1 < text.Length)
                sign = text.Substring(i, 2);
            sign ??= c.ToString();
            i += sign.Length;
            tokens.Add(new Item(Kind.Sign, sign));
        }
        return tokens;
    }

    private sealed class TooDeepException : Exception;

    private sealed class Parser(List<Item> tokens)
    {
        private int _at;
        private int _depth;

        private Item? Peek => _at < tokens.Count ? tokens[_at] : null;

        private bool At(string text) => Peek is { } token && token.Kind is Kind.Word or Kind.Sign && string.Equals(token.Text, text, StringComparison.OrdinalIgnoreCase);

        /// <summary>The formula: its lines (newline), each a row.</summary>
        public MathNode Formula()
        {
            var lines = new List<MathNode>();
            var line = new List<MathNode>();
            while (true)
            {
                line.AddRange(Row(null).Items);
                if (Peek is null)
                    break;
                // A new line, or a closing token with nothing open (passed over).
                if (At("newline"))
                {
                    lines.Add(new MathRow(line));
                    line = [];
                }
                _at++;
            }
            lines.Add(new MathRow(line));
            var shown = lines.Where(l => !IsEmpty(l)).ToList();
            return shown.Count switch
            {
                0 => MathRow.Empty,
                1 => shown[0],
                _ => new MathPile(shown) { Align = "lpile" },
            };
        }

        /// <summary>Whether the next token ends the row being read (a group, cell, line or bracket closing).</summary>
        private bool Stops(string? close) =>
            Peek is null || At("}") || At("#") || At("##") || At("right") || At("newline") || (close is not null && At(close));

        /// <summary>Terms until a closing token (<paramref name="close"/>: the bracket a paired one waits for).</summary>
        private MathRow Row(string? close)
        {
            var items = new List<MathNode>();
            while (!Stops(close))
            {
                if (At("over") || At("wideslash"))
                {
                    _at++;
                    var numerator = items.Count > 0 ? items[^1] : MathRow.Empty;
                    if (items.Count > 0)
                        items.RemoveAt(items.Count - 1);
                    items.Add(new MathFraction(numerator, Term()));
                    continue;
                }
                if (Operator() is { } op)
                {
                    items.Add(op);
                    continue;
                }
                var term = Term();
                // Paired brackets and a word of letters are items of the row, unless over takes them as a whole.
                if (term is MathRow row && _spread.Remove(row) && !At("over") && !At("wideslash"))
                    items.AddRange(row.Items);
                else if (!IsEmpty(term))
                    items.Add(term);
            }
            return new MathRow(items);
        }

        /// <summary>Rows of several items that are one term only for over: paired brackets, a word of letters.</summary>
        private readonly HashSet<MathRow> _spread = new(ReferenceEqualityComparer.Instance);

        private MathRow Spread(List<MathNode> items)
        {
            var row = new MathRow(items);
            _spread.Add(row);
            return row;
        }

        /// <summary>An operator or relation between terms (signs and their words), else null without reading it.</summary>
        private MathNode? Operator()
        {
            if (Peek is not { } token)
                return null;
            if (token.Kind == Kind.Sign && token.Text is "+" or "-" or "=" or "<" or ">" or "/" or "*" or "," or "." or ";" or ":" or "!" or "'"
                or "<=" or ">=" or "<>" or "<<" or ">>" or "+-" or "-+")
            {
                _at++;
                var glyph = token.Text switch
                {
                    "<=" => "≤",
                    ">=" => "≥",
                    "<>" => "≠",
                    "<<" => "≪",
                    ">>" => "≫",
                    "+-" => "±",
                    "-+" => "∓",
                    var other => other,
                };
                return Glyph(glyph);
            }
            if (token.Kind == Kind.Word && Glyphs.TryGetValue(token.Text, out var named) && !IsSymbolWord(token.Text))
            {
                _at++;
                return Glyph(named);
            }
            return null;
        }

        /// <summary>Named things that are terms rather than operators (they may take scripts): ∞, ∂, ∅ …</summary>
        private static bool IsSymbolWord(string word) => word.ToLowerInvariant() is "infinity" or "infty" or "partial" or "nabla" or "emptyset"
            or "aleph" or "setn" or "setz" or "setq" or "setr" or "setc" or "hbar" or "lambdabar" or "re" or "im" or "wp" or "exists"
            or "notexists" or "forall" or "dotslow" or "dotsaxis" or "dotsvert" or "dotsup" or "dotsdown" or "dotsdiag";

        private static MathNode Glyph(string glyph)
        {
            var items = new List<MathNode>();
            OmmlReader.Tokens(glyph, items, upright: true, bold: false);
            return items.Count == 1 ? items[0] : new MathRow(items);
        }

        /// <summary>A term with its scripts.</summary>
        private MathNode Term()
        {
            if (++_depth > MaxDepth)
                throw new TooDeepException();
            try
            {
                var node = Primary();
                MathNode? sub = null, sup = null, under = null, over = null;
                while (true)
                {
                    if (At("^") || At("sup") || At("rsup"))
                    {
                        _at++;
                        sup = Argument();
                    }
                    else if (At("_") || At("sub") || At("rsub"))
                    {
                        _at++;
                        sub = Argument();
                    }
                    else if (At("csup"))
                    {
                        _at++;
                        over = Argument();
                    }
                    else if (At("csub"))
                    {
                        _at++;
                        under = Argument();
                    }
                    else if (At("lsup") || At("lsub"))
                    {
                        // Scripts on the left: no place for them in the tree; kept after the base.
                        var left = At("lsup");
                        _at++;
                        var script = Argument();
                        node = new MathScripts(node, left ? null : script, left ? script : null);
                    }
                    else
                    {
                        break;
                    }
                }
                if (over is not null || under is not null)
                {
                    node = node switch
                    {
                        MathBigOperator { Lower: null, Upper: null } big => big with { Lower = under, Upper = over },
                        MathLimit { Lower: null } when under is not null && over is null => new MathLimit(under),
                        _ => new MathStack(node, over, under),
                    };
                }
                if (sub is not null || sup is not null)
                {
                    node = node is MathBigOperator { Lower: null, Upper: null } big2 ? big2 with { Lower = sub, Upper = sup } : new MathScripts(node, sub, sup);
                }
                return node;
            }
            finally
            {
                _depth--;
            }
        }

        /// <summary>A script or limit: a sign and the term after it, or a term without scripts of its own.</summary>
        private MathNode Argument()
        {
            if (++_depth > MaxDepth)
                throw new TooDeepException();
            try
            {
                if (At("+") || At("-") || At("+-") || At("-+") || At("neg"))
                {
                    var sign = Operator()!;
                    return new MathRow([sign, Argument()]);
                }
                return Primary();
            }
            finally
            {
                _depth--;
            }
        }

        private MathNode Primary()
        {
            if (Peek is not { } token || Stops(null))
                return MathRow.Empty;
            _at++;
            switch (token.Kind)
            {
                case Kind.Number:
                    return new MathNumber(token.Text);
                case Kind.Text:
                    return token.Text.Length == 0 ? MathRow.Empty : new MathQuoted(token.Text);
                case Kind.Greek:
                    {
                        var name = token.Text.Length > 1 && token.Text[0] is 'i' && Greek.ContainsKey(token.Text[1..]) ? token.Text[1..] : token.Text;
                        if (Greek.TryGetValue(name, out var letter))
                            return Glyph(letter);
                        return name.Length == 0 ? MathRow.Empty : new MathText(name);
                    }
                case Kind.Escaped:
                    {
                        var glyph = token.Text switch
                        {
                            "lbrace" => "{",
                            "rbrace" => "}",
                            "lline" or "rline" => "|",
                            "ldline" or "rdline" => "‖",
                            "langle" => "⟨",
                            "rangle" => "⟩",
                            "lfloor" => "⌊",
                            "rfloor" => "⌋",
                            "lceil" => "⌈",
                            "rceil" => "⌉",
                            "ldbracket" => "⟦",
                            "rdbracket" => "⟧",
                            var other => other,
                        };
                        return Glyph(glyph);
                    }
                case Kind.Sign:
                    return Sign(token.Text);
                default:
                    return Word(token.Text);
            }
        }

        private MathNode Sign(string sign)
        {
            switch (sign)
            {
                case "{":
                    {
                        var row = Row(null);
                        if (At("}"))
                            _at++;
                        return row;
                    }
                case "(" or "[":
                    return Paired(Openers[sign]);
                case "~":
                    return new MathSpace();
                case "`":
                    return new MathSpace(0.25);
                case "<?>":
                    return MathRow.Empty;
                case "&":
                    return Glyph("∧");
                default:
                    return Glyph(sign);
            }
        }

        /// <summary>Brackets in pairs that keep their size: the opening one, the content, the closing one.</summary>
        private MathNode Paired((string Glyph, string Close) bracket)
        {
            var row = Row(bracket.Close);
            var items = new List<MathNode> { Glyph(bracket.Glyph) };
            items.AddRange(row.Items);
            if (At(bracket.Close))
            {
                var close = Peek!.Value.Text;
                _at++;
                items.Add(Glyph(Closers.GetValueOrDefault(close, close)));
            }
            return Spread(items);
        }

        private MathNode Word(string word)
        {
            var lower = word.ToLowerInvariant();
            if (Openers.TryGetValue(lower, out var opener))
                return Paired(opener);
            if (OperatorGlyphs.TryGetValue(lower, out var operatorGlyph))
                return BigOperator(operatorGlyph);
            if (Accents.TryGetValue(lower, out var accent))
                return new MathAccent(accent, Term());
            if (Functions.Contains(lower))
                return new MathFunction(lower);
            if (Glyphs.TryGetValue(word, out var named))
                return Glyph(named);
            switch (lower)
            {
                case "left":
                    return Left();
                case "over" or "wideslash":
                    return MathRow.Empty; // with nothing before it
                case "sqrt":
                    return new MathRoot(null, Term());
                case "nroot":
                    {
                        var index = Term();
                        return new MathRoot(index, Term());
                    }
                case "abs":
                    return new MathBrackets("|", "|", Term());
                case "fact":
                    return new MathRow([Term(), new MathSymbol("!")]);
                case "binom":
                    {
                        var top = Term();
                        return new MathFraction(top, Term(), false);
                    }
                case "stack":
                    return new MathPile(Cells(rows: false).Select(r => r[0]).ToList());
                case "matrix":
                    return new MathMatrix("matrix", Cells(rows: true));
                case "lim" or "liminf" or "limsup":
                    {
                        var lower2 = Limit("from");
                        Limit("to");
                        return lower == "lim" ? new MathLimit(lower2) : new MathScripts(new MathFunction(lower == "liminf" ? "lim inf" : "lim sup"), lower2, null);
                    }
                case "oper":
                    {
                        if (Peek is not { } next)
                            return MathRow.Empty;
                        _at++;
                        return BigOperator(next.Kind == Kind.Greek && Greek.TryGetValue(next.Text, out var letter) ? letter : next.Text);
                    }
                case "func":
                    {
                        if (Peek is { Kind: Kind.Word } name)
                        {
                            _at++;
                            return new MathFunction(name.Text);
                        }
                        return MathRow.Empty;
                    }
                case "bold":
                    return new MathStyled("bold", Term());
                case "ital" or "italic":
                    return new MathStyled("it", Term());
                case "nitalic":
                    return new MathStyled("rm", Term());
                case "nbold" or "alignl" or "alignc" or "alignr" or "alignm" or "alignt" or "alignb" or "nospace":
                    return Term();
                case "phantom":
                    Term();
                    return MathRow.Empty;
                case "color":
                    if (At("rgb"))
                    {
                        _at++;
                        for (var i = 0; i < 3 && Peek is { Kind: Kind.Number }; i++)
                            _at++;
                    }
                    else if (At("hex") || At("dvip") || At("html") || At("rgba"))
                    {
                        _at++;
                        if (Peek is not null)
                            _at++;
                    }
                    else if (Peek is not null)
                    {
                        _at++;
                    }
                    return Term();
                case "size":
                    if (At("+") || At("-") || At("*") || At("/"))
                        _at++;
                    if (Peek is not null)
                        _at++;
                    return Term();
                case "font":
                    if (Peek is not null)
                        _at++;
                    return Term();
                case "from" or "to" or "csub" or "csup":
                    return MathRow.Empty;
                default:
                    {
                        var items = new List<MathNode>();
                        OmmlReader.Tokens(word, items, upright: false, bold: false);
                        return items.Count == 1 ? items[0] : Spread(items);
                    }
            }
        }

        /// <summary>A large operator with its limits (from, to); a glyph that isn't one has them over and under it.</summary>
        private MathNode BigOperator(string glyph)
        {
            var lower = Limit("from");
            var upper = Limit("to");
            var symbol = Glyph(glyph);
            if (symbol is MathBigOperator big)
                return big with { Lower = lower, Upper = upper };
            return lower is null && upper is null ? symbol : new MathStack(symbol, upper, lower);
        }

        /// <summary>The argument after from or to, when it comes next.</summary>
        private MathNode? Limit(string word)
        {
            if (!At(word))
                return null;
            _at++;
            return Argument();
        }

        /// <summary>left bracket … right bracket: stretched brackets, a matrix in them, cases after a brace.</summary>
        private MathNode Left()
        {
            var open = Bracket();
            var body = Row(null);
            var close = "";
            if (At("right"))
            {
                _at++;
                close = Bracket();
            }
            if (body.Items is [MathMatrix { Command: "matrix" } matrix])
            {
                var command = (open, close) switch
                {
                    ("(", ")") => "pmatrix",
                    ("[", "]") => "bmatrix",
                    ("|", "|") => "dmatrix",
                    _ => null,
                };
                if (command is not null)
                    return matrix with { Command = command };
                if (open == "{" && close.Length == 0)
                    return new MathCases(matrix.Rows);
            }
            if (body.Items is [MathPile pile] && open == "{" && close.Length == 0)
                return new MathCases([.. pile.Rows.Select(r => (IReadOnlyList<MathNode>)[r])]);
            return new MathBrackets(open, close, body.Items is [var only] ? only : body);
        }

        /// <summary>The bracket after left or right ("" for none).</summary>
        private string Bracket()
        {
            if (Peek is not { } token)
                return "";
            _at++;
            var text = token.Kind == Kind.Escaped ? token.Text : token.Text.ToLowerInvariant();
            if (text == "none")
                return "";
            if (Openers.TryGetValue(text, out var open))
                return open.Glyph;
            if (Closers.TryGetValue(text, out var close))
                return close;
            if (text == "mline")
                return "|";
            if (token.Kind == Kind.Sign && text.Length == 1 && EquationScript.IsBracket(text))
                return text;
            _at--; // not a bracket: read as content
            return "";
        }

        /// <summary>The cells of stack { a # b } (one each) or matrix { a # b ## c # d }.</summary>
        private List<IReadOnlyList<MathNode>> Cells(bool rows)
        {
            var result = new List<IReadOnlyList<MathNode>>();
            if (!At("{"))
            {
                result.Add([Term()]);
                return result;
            }
            _at++;
            var row = new List<MathNode>();
            while (true)
            {
                row.Add(Row(null));
                if (At("#"))
                {
                    _at++;
                    if (!rows)
                    {
                        result.Add(row);
                        row = [];
                    }
                    continue;
                }
                if (At("##"))
                {
                    _at++;
                    result.Add(row);
                    row = [];
                    continue;
                }
                if (At("}"))
                    _at++;
                else if (Peek is not null && !At("right") && !At("newline"))
                {
                    _at++; // a stray closing token inside: passed over
                    continue;
                }
                break;
            }
            result.Add(row);
            return result;
        }
    }

    private static bool IsEmpty(MathNode node) => node switch
    {
        MathRow row => row.Items.All(IsEmpty),
        MathAlign => true,
        _ => false,
    };
}
