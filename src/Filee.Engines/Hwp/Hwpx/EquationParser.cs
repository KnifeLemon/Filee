// 한글's equation script (hp:equation/hp:script, "Equation Version 60") read into the equation tree (MathNode) the
// layout engine lays out: the other way from EquationScript.Script.
//
// Tokens: braces group; ^ and _ (or sup, sub) put scripts on the item before; # ends a row and & a cell of matrices,
// cases and piles; "…" is text as written; ~ is a space and ` a quarter of one; digits make a number. A run of letters
// is read the way 한글 reads it: command words at its start are split off, longest first ("rmA" → rm A, "cupB" →
// cup B, "itn" → it n, "cdotscdots" → cdots cdots), and the letters left are one variable ("rmbarAD" → rm bar AD). Words
// of symbols ("<=" → ≤, "!=" → ≠, "->" → →) and the symbol commands (LEQ, TIMES, alpha, CUP, …) become their character.
//
// Structure: over and atop take the item on each side ("{a+b} over c"), sqrt and root … of … their operand, LEFT and
// RIGHT a stretched pair of brackets (a missing or "." side is empty), matrix, pmatrix, bmatrix, dmatrix, cases, pile,
// lpile and rpile their braced rows; sums, products, integrals and unions take limits with _ and ^ or from and to; lim
// a lower limit; accents (hat, bar, vec, tilde, dot, ddot, acute, grave, check, breve, under, arch, dyad) the item after
// them; rm, it and bold switch the rest of their group. Anything left over (unknown commands, a dangling script) is
// kept as text, so nothing written is lost.

using System.Globalization;
using System.Text;

namespace Filee.Engines.Hwp.Hwpx;

internal static class EquationParser
{
    /// <summary>Symbol commands and their characters, compared ignoring case (Greek letters keep theirs, see Greek).</summary>
    private static readonly Dictionary<string, string> Symbols = new(StringComparer.OrdinalIgnoreCase)
    {
        ["times"] = "×",
        ["div"] = "÷",
        ["divide"] = "÷",
        ["pm"] = "±",
        ["plusminus"] = "±",
        ["mp"] = "∓",
        ["minusplus"] = "∓",
        ["cdot"] = "·",
        ["circ"] = "∘",
        ["bullet"] = "•",
        ["ast"] = "∗",
        ["star"] = "⋆",
        ["oplus"] = "⊕",
        ["ominus"] = "⊖",
        ["otimes"] = "⊗",
        ["odot"] = "⊙",
        ["oslash"] = "⊘",
        ["ne"] = "≠",
        ["neq"] = "≠",
        ["le"] = "≤",
        ["leq"] = "≤",
        ["ge"] = "≥",
        ["geq"] = "≥",
        ["lt"] = "<",
        ["gt"] = ">",
        ["ll"] = "≪",
        ["gg"] = "≫",
        ["lll"] = "⋘",
        ["ggg"] = "⋙",
        ["approx"] = "≈",
        ["equiv"] = "≡",
        ["cong"] = "≅",
        ["sim"] = "∼",
        ["simeq"] = "≃",
        ["propto"] = "∝",
        ["asymp"] = "≍",
        ["doteq"] = "≐",
        ["prec"] = "≺",
        ["succ"] = "≻",
        ["inf"] = "∞",
        ["infty"] = "∞",
        ["infinity"] = "∞",
        ["partial"] = "∂",
        ["nabla"] = "∇",
        ["deg"] = "°",
        ["prime"] = "′",
        ["angle"] = "∠",
        ["triangle"] = "△",
        ["bot"] = "⊥",
        ["perp"] = "⊥",
        ["in"] = "∈",
        ["notin"] = "∉",
        ["owns"] = "∋",
        ["ni"] = "∋",
        ["subset"] = "⊂",
        ["superset"] = "⊃",
        ["supset"] = "⊃",
        ["subseteq"] = "⊆",
        ["supseteq"] = "⊇",
        ["supersetEq"] = "⊇",
        ["nsubset"] = "⊄",
        ["nsupset"] = "⊅",
        ["cup"] = "∪",
        ["cap"] = "∩",
        ["smallunion"] = "∪",
        ["smallinter"] = "∩",
        ["sqcup"] = "⊔",
        ["sqcap"] = "⊓",
        ["forall"] = "∀",
        ["exist"] = "∃",
        ["exists"] = "∃",
        ["lnot"] = "¬",
        ["neg"] = "¬",
        ["wedge"] = "∧",
        ["land"] = "∧",
        ["vee"] = "∨",
        ["lor"] = "∨",
        ["xor"] = "⊻",
        ["emptyset"] = "∅",
        ["therefore"] = "∴",
        ["because"] = "∵",
        ["rarrow"] = "→",
        ["larrow"] = "←",
        ["lrarrow"] = "↔",
        ["uparrow"] = "↑",
        ["downarrow"] = "↓",
        ["udarrow"] = "↕",
        ["nearrow"] = "↗",
        ["nwarrow"] = "↖",
        ["searrow"] = "↘",
        ["swarrow"] = "↙",
        ["mapsto"] = "↦",
        ["gets"] = "←",
        ["implies"] = "⇒",
        ["iff"] = "⇔",
        ["cdots"] = "⋯",
        ["ldots"] = "…",
        ["vdots"] = "⋮",
        ["ddots"] = "⋱",
        ["dots"] = "…",
        ["ell"] = "ℓ",
        ["hbar"] = "ℏ",
        ["aleph"] = "ℵ",
        ["wp"] = "℘",
        ["dag"] = "†",
        ["ddag"] = "‡",
        ["lbrace"] = "{",
        ["rbrace"] = "}",
        ["langle"] = "⟨",
        ["rangle"] = "⟩",
        ["lfloor"] = "⌊",
        ["rfloor"] = "⌋",
        ["lceil"] = "⌈",
        ["rceil"] = "⌉",
        ["vert"] = "|",
        ["dline"] = "‖",
        ["rslant"] = "\\",
        ["lslant"] = "/",
        ["ohm"] = "Ω",
        ["mho"] = "℧",
        ["hund"] = "‰",
        ["thou"] = "‱",
        ["liter"] = "ℓ",
    };

    /// <summary>Greek letters: lowercase as written, a capital first letter gives the capital.</summary>
    private static readonly Dictionary<string, (char Small, char Capital)> Greek = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alpha"] = ('α', 'Α'),
        ["beta"] = ('β', 'Β'),
        ["gamma"] = ('γ', 'Γ'),
        ["delta"] = ('δ', 'Δ'),
        ["epsilon"] = ('ε', 'Ε'),
        ["zeta"] = ('ζ', 'Ζ'),
        ["eta"] = ('η', 'Η'),
        ["theta"] = ('θ', 'Θ'),
        ["iota"] = ('ι', 'Ι'),
        ["kappa"] = ('κ', 'Κ'),
        ["lambda"] = ('λ', 'Λ'),
        ["mu"] = ('μ', 'Μ'),
        ["nu"] = ('ν', 'Ν'),
        ["xi"] = ('ξ', 'Ξ'),
        ["omicron"] = ('ο', 'Ο'),
        ["pi"] = ('π', 'Π'),
        ["rho"] = ('ρ', 'Ρ'),
        ["sigma"] = ('σ', 'Σ'),
        ["tau"] = ('τ', 'Τ'),
        ["upsilon"] = ('υ', 'Υ'),
        ["phi"] = ('φ', 'Φ'),
        ["chi"] = ('χ', 'Χ'),
        ["psi"] = ('ψ', 'Ψ'),
        ["omega"] = ('ω', 'Ω'),
        ["vartheta"] = ('ϑ', 'ϑ'),
        ["varpi"] = ('ϖ', 'ϖ'),
        ["varsigma"] = ('ς', 'ς'),
        ["varphi"] = ('ϕ', 'ϕ'),
        ["varepsilon"] = ('ϵ', 'ϵ'),
    };

    /// <summary>Large operators: their command (as EquationScript writes it) and character.</summary>
    private static readonly Dictionary<string, (string Command, string Glyph)> BigOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sum"] = ("SUM", "∑"),
        ["prod"] = ("PROD", "∏"),
        ["coprod"] = ("COPROD", "∐"),
        ["union"] = ("BIGCUP", "⋃"),
        ["bigcup"] = ("BIGCUP", "⋃"),
        ["inter"] = ("BIGCAP", "⋂"),
        ["bigcap"] = ("BIGCAP", "⋂"),
        ["bigvee"] = ("BIGVEE", "⋁"),
        ["bigwedge"] = ("BIGWEDGE", "⋀"),
        ["bigoplus"] = ("BIGOPLUS", "⨁"),
        ["bigotimes"] = ("BIGOTIMES", "⨂"),
        ["int"] = ("INT", "∫"),
        ["integral"] = ("INT", "∫"),
        ["smallint"] = ("INT", "∫"),
        ["dint"] = ("DINT", "∬"),
        ["tint"] = ("TINT", "∭"),
        ["oint"] = ("OINT", "∮"),
        ["odint"] = ("ODINT", "∯"),
        ["otint"] = ("OTINT", "∰"),
    };

    /// <summary>Function names written upright.</summary>
    private static readonly HashSet<string> Functions = new(StringComparer.Ordinal)
    {
        "sin", "cos", "tan", "cot", "sec", "csc", "arcsin", "arccos", "arctan", "sinh", "cosh", "tanh", "coth", "log",
        "ln", "lg", "exp", "det", "dim", "ker", "hom", "arg", "gcd", "lcm", "max", "min", "mod",
    };

    private static readonly HashSet<string> Accents = new(StringComparer.OrdinalIgnoreCase)
    {
        "hat", "bar", "vec", "tilde", "dot", "ddot", "acute", "grave", "check", "breve", "under", "underline", "arch",
        "dyad", "widehat", "widetilde", "overline",
    };

    private static readonly HashSet<string> Matrices = new(StringComparer.OrdinalIgnoreCase)
    {
        "matrix", "pmatrix", "bmatrix", "dmatrix", "vmatrix",
    };

    /// <summary>Words that shape the tree (not symbols): compared ignoring case.</summary>
    private static readonly HashSet<string> Structure = new(StringComparer.OrdinalIgnoreCase)
    {
        "over", "atop", "sqrt", "root", "of", "left", "right", "sup", "sub", "from", "to", "lim", "cases", "pile", "lpile",
        "rpile", "rm", "it", "bold", "not", "color", "binom", "choose", "big", "bigg",
    };

    /// <summary>All words a run of letters is split into, longest first.</summary>
    private static readonly string[] Words = [.. Symbols.Keys.Concat(Greek.Keys).Concat(BigOperators.Keys).Concat(Functions)
        .Concat(Accents).Concat(Matrices).Concat(Structure).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(w => w.Length)];

    /// <summary>Two- and three-character symbols and the command each is.</summary>
    private static readonly (string Text, string Command)[] Combined =
    [
        ("<->", "lrarrow"), ("<=", "LEQ"), (">=", "GEQ"), ("!=", "NEQ"), ("->", "rarrow"), ("<-", "larrow"), ("==", "EQUIV"),
        ("+-", "PLUSMINUS"), ("-+", "MINUSPLUS"), ("<<", "ll"), (">>", "gg"), ("...", "LDOTS"),
    ];

    /// <summary>The tree of a 한글 equation script; an empty row for an empty script.</summary>
    public static MathNode Parse(string script)
    {
        var parser = new Reader(Tokens(script));
        var items = parser.Row(end: null);
        // Unmatched closing braces and the like: what's left is read on as further rows.
        while (!parser.AtEnd)
        {
            parser.Skip();
            items.AddRange(parser.Row(end: null));
        }
        return Single(items);
    }

    private static MathNode Single(List<MathNode> items) => items.Count == 1 ? items[0] : new MathRow(items);

    // ───────────────────────── Tokens ─────────────────────────

    private enum Kind
    {
        Word,
        Letter,
        Number,
        Symbol,
        Quoted,
        Open,
        Close,
        Super,
        Sub,
        Row,
        Cell,
        Space,
        Thin,
        Other,
    }

    private readonly record struct Token(Kind Kind, string Text);

    private static List<Token> Tokens(string script)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < script.Length)
        {
            var c = script[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            switch (c)
            {
                case '{':
                    tokens.Add(new(Kind.Open, "{"));
                    i++;
                    continue;
                case '}':
                    tokens.Add(new(Kind.Close, "}"));
                    i++;
                    continue;
                case '^':
                    tokens.Add(new(Kind.Super, "^"));
                    i++;
                    continue;
                case '_':
                    tokens.Add(new(Kind.Sub, "_"));
                    i++;
                    continue;
                case '#':
                    tokens.Add(new(Kind.Row, "#"));
                    i++;
                    continue;
                case '&':
                    tokens.Add(new(Kind.Cell, "&"));
                    i++;
                    continue;
                case '~':
                    tokens.Add(new(Kind.Space, "~"));
                    i++;
                    continue;
                case '`':
                    tokens.Add(new(Kind.Thin, "`"));
                    i++;
                    continue;
                case '"':
                    {
                        var end = script.IndexOf('"', i + 1);
                        if (end < 0)
                            end = script.Length;
                        tokens.Add(new(Kind.Quoted, script[(i + 1)..end]));
                        i = Math.Min(script.Length, end + 1);
                        continue;
                    }
            }
            if (char.IsAsciiDigit(c) || c == '.' && i + 1 < script.Length && char.IsAsciiDigit(script[i + 1]))
            {
                var start = i;
                while (i < script.Length && (char.IsAsciiDigit(script[i]) || script[i] == '.' && i + 1 < script.Length && char.IsAsciiDigit(script[i + 1])))
                    i++;
                tokens.Add(new(Kind.Number, script[start..i]));
                continue;
            }
            if (char.IsAsciiLetter(c))
            {
                var start = i;
                while (i < script.Length && char.IsAsciiLetter(script[i]))
                    i++;
                Letters(script[start..i], tokens);
                continue;
            }
            if (Combined.FirstOrDefault(s => string.CompareOrdinal(script, i, s.Text, 0, s.Text.Length) == 0) is { Text: not null } combined)
            {
                tokens.Add(new(Kind.Word, combined.Command));
                i += combined.Text.Length;
                continue;
            }
            // Other characters, a surrogate pair or combining marks with their letter kept together.
            var length = char.IsHighSurrogate(c) && i + 1 < script.Length ? 2 : 1;
            while (i + length < script.Length && CharUnicodeInfo.GetUnicodeCategory(script[i + length]) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark)
                length++;
            var text = script.Substring(i, length);
            tokens.Add(new(char.IsLetter(c) ? Kind.Letter : Kind.Symbol, text));
            i += length;
        }
        return tokens;
    }

    /// <summary>
    /// A run of letters: command words split off at its start (longest first), one after another; the letters left
    /// over are one variable ("rmbarAD" → rm bar AD).
    /// </summary>
    private static void Letters(string run, List<Token> tokens)
    {
        var i = 0;
        while (i < run.Length)
        {
            var word = Words.FirstOrDefault(w => w.Length <= run.Length - i && string.Compare(run, i, w, 0, w.Length, StringComparison.OrdinalIgnoreCase) == 0
                                                 && CaseFits(run.Substring(i, w.Length)));
            if (word is null)
            {
                tokens.Add(new(Kind.Letter, run[i..]));
                return;
            }
            tokens.Add(new(Kind.Word, run.Substring(i, word.Length)));
            i += word.Length;
        }
    }

    /// <summary>A command written in one case all through, or with a capital first (Greek capitals): "Sin" is no command.</summary>
    private static bool CaseFits(string word) =>
        word.All(char.IsLower) || word.All(char.IsUpper) || (char.IsUpper(word[0]) && word.Skip(1).All(char.IsLower) && Greek.ContainsKey(word));

    // ───────────────────────── Tree ─────────────────────────

    private sealed class Reader(List<Token> tokens)
    {
        private int _at;

        public bool AtEnd => _at >= tokens.Count;

        private Token? Peek => _at < tokens.Count ? tokens[_at] : null;

        public void Skip() => _at++;

        private bool IsWord(string word) => Peek is { Kind: Kind.Word } t && string.Equals(t.Text, word, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Items up to the end of the group (a closing brace, or <paramref name="end"/>: a row or cell separator, RIGHT,
        /// of), with over and atop joining their neighbours and rm, it and bold taking the rest.
        /// </summary>
        public List<MathNode> Row(Func<Token, bool>? end, bool separators = false)
        {
            var items = new List<MathNode>();
            while (Peek is { } token)
            {
                if (token.Kind == Kind.Close || end?.Invoke(token) == true || separators && token.Kind is Kind.Row or Kind.Cell)
                    break;
                if (token.Kind == Kind.Word && token.Text.ToLowerInvariant() is "over" or "atop")
                {
                    _at++;
                    var numerator = items.Count > 0 ? items[^1] : MathRow.Empty;
                    if (items.Count > 0)
                        items.RemoveAt(items.Count - 1);
                    var denominator = Item(end) ?? MathRow.Empty;
                    items.Add(new MathFraction(numerator, denominator, token.Text.Equals("over", StringComparison.OrdinalIgnoreCase)));
                    continue;
                }
                if (token.Kind == Kind.Word && token.Text.ToLowerInvariant() is "rm" or "it" or "bold")
                {
                    _at++;
                    items.Add(new MathStyled(token.Text.ToLowerInvariant(), Single(Row(end, separators))));
                    break;
                }
                if (Item(end) is { } item)
                    items.Add(item);
                else
                    break;
            }
            return items;
        }

        /// <summary>One item with its scripts; null at the end of the group.</summary>
        private MathNode? Item(Func<Token, bool>? end)
        {
            if (Peek is not { } token || token.Kind == Kind.Close || end?.Invoke(token) == true)
                return null;
            var primary = Primary();
            return Scripts(primary);
        }

        /// <summary>The scripts after <paramref name="node"/>: ^ / sup and _ / sub in either order.</summary>
        private MathNode Scripts(MathNode node)
        {
            MathNode? sub = null, sup = null;
            while (Peek is { } token)
            {
                if (token.Kind == Kind.Super || IsWord("sup"))
                {
                    _at++;
                    sup = Operand();
                }
                else if (token.Kind == Kind.Sub || IsWord("sub"))
                {
                    _at++;
                    sub = Operand();
                }
                else
                {
                    break;
                }
            }
            if (sub is null && sup is null)
                return node;
            return node switch
            {
                MathBigOperator big => big with { Lower = sub ?? big.Lower, Upper = sup ?? big.Upper },
                MathLimit limit when sup is null => limit with { Lower = sub ?? limit.Lower },
                _ => new MathScripts(node, sub, sup),
            };
        }

        /// <summary>The operand of a script, root or accent: a group or one item (without its own scripts).</summary>
        private MathNode Operand()
        {
            if (Peek is not { } token || token.Kind is Kind.Close or Kind.Row or Kind.Cell)
                return MathRow.Empty;
            return Primary();
        }

        private MathNode Primary()
        {
            var token = tokens[_at++];
            switch (token.Kind)
            {
                case Kind.Open:
                    {
                        var items = Row(end: null);
                        if (Peek?.Kind == Kind.Close)
                            _at++;
                        return new MathRow(items);
                    }
                case Kind.Number:
                    return new MathNumber(token.Text);
                case Kind.Letter:
                    return new MathText(token.Text);
                case Kind.Quoted:
                    return new MathQuoted(token.Text);
                case Kind.Symbol:
                    return new MathSymbol(token.Text);
                case Kind.Space:
                    return new MathSpace();
                case Kind.Thin:
                    return new MathSpace(0.25);
                case Kind.Word:
                    return Word(token.Text);
                default:
                    // A script, row or cell mark where an item should be: kept as its character.
                    return new MathSymbol(token.Text);
            }
        }

        private MathNode Word(string word)
        {
            var lower = word.ToLowerInvariant();
            if (Greek.TryGetValue(word, out var greek))
                return new MathCommand(word, (char.IsUpper(word[0]) ? greek.Capital : greek.Small).ToString());
            if (BigOperators.TryGetValue(word, out var big))
                return BigOperator(big.Command, big.Glyph);
            if (Symbols.TryGetValue(word, out var symbol))
                return new MathCommand(word, symbol);
            if (Functions.Contains(word))
                return new MathFunction(word);
            if (Accents.Contains(word))
                return new MathAccent(lower switch { "underline" => "under", "overline" => "bar", "widehat" => "hat", "widetilde" => "tilde", _ => lower }, Operand());
            if (Matrices.Contains(word))
                return new MathMatrix(lower, Cells());
            switch (lower)
            {
                case "sqrt":
                    return new MathRoot(null, Operand());
                case "root":
                    {
                        var first = Operand();
                        if (!IsWord("of"))
                            return new MathRoot(null, first);
                        _at++;
                        return new MathRoot(first, Operand());
                    }
                case "left":
                    return Brackets();
                case "lim":
                    return Limit();
                case "cases":
                    return new MathCases(Cells());
                case "pile" or "lpile" or "rpile":
                    return new MathPile(Cells().Select(r => r.Count == 1 ? r[0] : new MathRow([.. r])).ToList()) { Align = lower };
                case "not":
                    return new MathAccent("not", Operand());
                case "color":
                    // color {r g b} {content}: the colour is left out, the content kept.
                    Operand();
                    return Operand();
                case "binom" or "choose":
                    {
                        var top = Operand();
                        return new MathBrackets("(", ")", new MathFraction(top, Operand(), Bar: false));
                    }
                case "big" or "bigg":
                    return MathRow.Empty;
                default:
                    // of, from, to, right, sup, sub out of place: their words as text.
                    return new MathQuoted(word);
            }
        }

        /// <summary>A large operator with limits written with from and to (or ^ and _, see Scripts).</summary>
        private MathNode BigOperator(string command, string glyph)
        {
            MathNode? lower = null, upper = null;
            while (IsWord("from") || IsWord("to"))
            {
                var from = IsWord("from");
                _at++;
                if (from)
                    lower = Operand();
                else
                    upper = Operand();
            }
            return new MathBigOperator(command, glyph, lower, upper);
        }

        private MathNode Limit()
        {
            MathNode? lower = null;
            if (IsWord("from"))
            {
                _at++;
                lower = Operand();
            }
            return new MathLimit(lower);
        }

        /// <summary>LEFT delimiter … RIGHT delimiter: a missing RIGHT closes at the end of the group.</summary>
        private MathNode Brackets()
        {
            var left = Delimiter();
            var body = Row(t => t.Kind == Kind.Word && t.Text.Equals("right", StringComparison.OrdinalIgnoreCase));
            var right = "";
            if (IsWord("right"))
            {
                _at++;
                right = Delimiter();
            }
            return new MathBrackets(left, right, Single(body));
        }

        /// <summary>The bracket after LEFT or RIGHT: a character, a word for one (LBRACE, LANGLE, ...) or "." for none.</summary>
        private string Delimiter()
        {
            if (Peek is not { } token)
                return "";
            switch (token.Kind)
            {
                case Kind.Open:
                    _at++;
                    return "{";
                case Kind.Close:
                    _at++;
                    return "}";
                case Kind.Symbol:
                    _at++;
                    return token.Text == "." ? "" : token.Text;
                case Kind.Word when Symbols.TryGetValue(token.Text, out var glyph):
                    _at++;
                    return glyph;
                default:
                    return "";
            }
        }

        /// <summary>The braced rows of a matrix, cases or pile: # between rows, &amp; between cells.</summary>
        private List<IReadOnlyList<MathNode>> Cells()
        {
            var rows = new List<IReadOnlyList<MathNode>>();
            if (Peek?.Kind != Kind.Open)
                return [[Operand()]];
            _at++;
            var row = new List<MathNode>();
            while (true)
            {
                row.Add(Single(Row(end: null, separators: true)));
                var next = Peek;
                if (next is not { } token || token.Kind == Kind.Close)
                {
                    if (next is not null)
                        _at++;
                    rows.Add(row);
                    break;
                }
                _at++;
                if (token.Kind == Kind.Row)
                {
                    rows.Add(row);
                    row = [];
                }
            }
            return rows;
        }
    }
}
