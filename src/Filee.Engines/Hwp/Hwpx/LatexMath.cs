// TeX math (what Markdown writes between $ … $, $$ … $$, \( … \) and \[ … \]) read into the equation tree (MathNode) that
// Word and 한글 equations use, so the layout engine typesets it, the DOCX writer stores it as Word math, the HTML and
// EPUB writers as MathML and the HWPX writer as a 한글 equation.
//
// The subset Markdown documents use: braces group; ^ and _ take one token or a group ("x^23" is x² 3); ' is a prime;
// \frac, \dfrac, \tfrac, \cfrac, \binom, \over, \choose and \atop; \sqrt with an optional index; Greek letters and the
// common symbols, relations, arrows and dots; \sum, \prod, \int, \oint, \bigcup … with their limits, \lim with what goes
// under it; function names (\sin, \log, \max, \operatorname{…}); \left … \middle … \right and \big…; the environments
// matrix, pmatrix, bmatrix, Bmatrix, vmatrix, Vmatrix, array, cases, aligned / align / split / alignat / eqnarray and
// gather / gathered / multline; top-level \\ and & as in an aligned block; accents (\hat, \bar, \vec, \tilde, \dot,
// \ddot, \overline, \underline, \widehat …); \text, \mathrm, \mathbf, \mathit, \boldsymbol, \mathbb, \mathcal,
// \mathfrak and the old switches \rm, \bf, \it; spacing (\, \: \; \quad \qquad ~ \hspace); \not; \tag. Commands for
// layout only (\displaystyle, \limits, \label, \color …) are skipped. A command this reader doesn't know is kept as its
// text with its arguments as written ("\foo{x}"), never dropped. Primes (f', f'', f^\prime) are a superscript prime
// on the item before, which MathLayout draws after it at full size.
//
// Bounds: a formula longer than MaxLength characters, or one that nests deeper than MaxDepth (groups, fractions,
// scripts, brackets, environments), is not read: Parse returns null and the caller keeps the source as text, as
// EquationParser keeps a 한글 script that nests too deep. The walks over the tree (layout, writers) recurse per level.

using System.Text;
using Filee.Engines.Hwp.Hwpx.Docx;

namespace Filee.Engines.Hwp.Hwpx;

internal static class LatexMath
{
    /// <summary>Characters of one formula read as math; longer ones stay text (a page of matrix is a few thousand).</summary>
    public const int MaxLength = 32_768;

    /// <summary>Levels of nesting read, as for 한글's equation script.</summary>
    public const int MaxDepth = EquationParser.MaxDepth;

    private enum Kind
    {
        Ordinary,
        Binary,
        Relation,
    }

    /// <summary>Symbol commands: their character and how they space.</summary>
    private static readonly Dictionary<string, (string Glyph, Kind Kind)> Symbols = Build();

    private static Dictionary<string, (string Glyph, Kind Kind)> Build()
    {
        var symbols = new Dictionary<string, (string Glyph, Kind Kind)>(StringComparer.Ordinal);
        void Add(Kind kind, string list)
        {
            foreach (var pair in list.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var split = pair.IndexOf('=', 1);
                symbols[pair[..split]] = (pair[(split + 1)..], kind);
            }
        }

        Add(Kind.Ordinary,
            "alpha=α beta=β gamma=γ delta=δ epsilon=ϵ varepsilon=ε zeta=ζ eta=η theta=θ vartheta=ϑ iota=ι kappa=κ " +
            "varkappa=ϰ lambda=λ mu=μ nu=ν xi=ξ omicron=ο pi=π varpi=ϖ rho=ρ varrho=ϱ sigma=σ varsigma=ς tau=τ " +
            "upsilon=υ phi=ϕ varphi=φ chi=χ psi=ψ omega=ω digamma=ϝ Gamma=Γ Delta=Δ Theta=Θ Lambda=Λ Xi=Ξ Pi=Π " +
            "Sigma=Σ Upsilon=Υ Phi=Φ Psi=Ψ Omega=Ω varGamma=Γ varDelta=Δ varTheta=Θ varLambda=Λ varXi=Ξ varPi=Π " +
            "varSigma=Σ varUpsilon=Υ varPhi=Φ varPsi=Ψ varOmega=Ω " +
            "infty=∞ partial=∂ nabla=∇ forall=∀ exists=∃ nexists=∄ emptyset=∅ varnothing=∅ neg=¬ lnot=¬ hbar=ℏ " +
            "hslash=ℏ ell=ℓ aleph=ℵ beth=ℶ gimel=ℷ wp=℘ Re=ℜ Im=ℑ angle=∠ measuredangle=∡ sphericalangle=∢ " +
            "triangle=△ top=⊤ bot=⊥ prime=′ backprime=‵ surd=√ checkmark=✓ degree=° imath=ı jmath=ȷ " +
            "ldots=… dots=… dotsc=… dotso=… cdots=⋯ dotsb=⋯ dotsm=⋯ dotsi=⋯ vdots=⋮ ddots=⋱ iddots=⋰ " +
            "clubsuit=♣ diamondsuit=♢ heartsuit=♡ spadesuit=♠ flat=♭ natural=♮ sharp=♯ dag=† ddag=‡ S=§ P=¶ " +
            "copyright=© pounds=£ yen=¥ euro=€ therefore=∴ because=∵ complement=∁ mho=℧ eth=ð Box=□ square=□ " +
            "blacksquare=■ lozenge=◊ blacklozenge=⧫ bigstar=★ colon=: ldotp=. cdotp=· " +
            "lbrace={ rbrace=} langle=⟨ rangle=⟩ lfloor=⌊ rfloor=⌋ lceil=⌈ rceil=⌉ vert=| lvert=| rvert=| Vert=‖ " +
            "lVert=‖ rVert=‖ lbrack=[ rbrack=] backslash=\\ ulcorner=⌜ urcorner=⌝ llcorner=⌞ lrcorner=⌟");
        Add(Kind.Binary,
            "pm=± mp=∓ times=× div=÷ cdot=· ast=∗ circ=∘ bullet=• oplus=⊕ ominus=⊖ otimes=⊗ oslash=⊘ odot=⊙ " +
            "cup=∪ cap=∩ sqcup=⊔ sqcap=⊓ vee=∨ lor=∨ wedge=∧ land=∧ setminus=∖ smallsetminus=∖ wr=≀ amalg=⨿ " +
            "diamond=⋄ bigcirc=◯ triangleleft=◁ triangleright=▷ bigtriangleup=△ bigtriangledown=▽ uplus=⊎ " +
            "dagger=† ddagger=‡ ltimes=⋉ rtimes=⋊ boxplus=⊞ boxminus=⊟ boxtimes=⊠ boxdot=⊡ divideontimes=⋇ " +
            "dotplus=∔ barwedge=⊼ veebar=⊻ curlyvee=⋎ curlywedge=⋏ centerdot=· star=⋆");
        Add(Kind.Relation,
            "leq=≤ le=≤ geq=≥ ge=≥ leqslant=⩽ geqslant=⩾ leqq=≦ geqq=≧ neq=≠ ne=≠ equiv=≡ approx=≈ approxeq=≊ " +
            "cong=≅ sim=∼ simeq=≃ thicksim=∼ thickapprox=≈ propto=∝ varpropto=∝ ll=≪ gg=≫ lll=⋘ ggg=⋙ prec=≺ succ=≻ " +
            "preceq=⪯ succeq=⪰ lesssim=≲ gtrsim=≳ lessgtr=≶ gtrless=≷ subset=⊂ supset=⊃ subseteq=⊆ supseteq=⊇ " +
            "subsetneq=⊊ supsetneq=⊋ nsubseteq=⊈ nsupseteq=⊉ sqsubset=⊏ sqsupset=⊐ sqsubseteq=⊑ sqsupseteq=⊒ " +
            "in=∈ notin=∉ ni=∋ owns=∋ mid=∣ nmid=∤ parallel=∥ nparallel=∦ perp=⊥ models=⊨ vdash=⊢ dashv=⊣ " +
            "vDash=⊨ asymp=≍ doteq=≐ doteqdot=≑ triangleq=≜ coloneqq=≔ eqqcolon=≕ bowtie=⋈ smile=⌣ frown=⌢ " +
            "nless=≮ ngtr=≯ nleq=≰ ngeq=≱ nsim=≁ ncong=≇ neqsim=≂ nequiv=≢ " +
            "to=→ rightarrow=→ gets=← leftarrow=← leftrightarrow=↔ Rightarrow=⇒ Leftarrow=⇐ Leftrightarrow=⇔ " +
            "longrightarrow=⟶ longleftarrow=⟵ longleftrightarrow=⟷ Longrightarrow=⟹ Longleftarrow=⟸ " +
            "Longleftrightarrow=⟺ implies=⟹ impliedby=⟸ iff=⟺ mapsto=↦ longmapsto=⟼ uparrow=↑ downarrow=↓ " +
            "updownarrow=↕ Uparrow=⇑ Downarrow=⇓ Updownarrow=⇕ nearrow=↗ searrow=↘ nwarrow=↖ swarrow=↙ " +
            "hookrightarrow=↪ hookleftarrow=↩ rightharpoonup=⇀ rightharpoondown=⇁ leftharpoonup=↼ " +
            "leftharpoondown=↽ rightleftharpoons=⇌ leftrightharpoons=⇋ rightrightarrows=⇉ leftleftarrows=⇇ " +
            "twoheadrightarrow=↠ rightsquigarrow=⇝ leadsto=⇝ circlearrowleft=↺ circlearrowright=↻");
        return symbols;
    }

    /// <summary>Large operators: the command EquationScript writes and their character.</summary>
    private static readonly Dictionary<string, (string Command, string Glyph)> BigOperators = new(StringComparer.Ordinal)
    {
        ["sum"] = ("SUM", "∑"),
        ["prod"] = ("PROD", "∏"),
        ["coprod"] = ("COPROD", "∐"),
        ["bigcup"] = ("BIGCUP", "⋃"),
        ["bigcap"] = ("BIGCAP", "⋂"),
        ["bigvee"] = ("BIGVEE", "⋁"),
        ["bigwedge"] = ("BIGWEDGE", "⋀"),
        ["bigoplus"] = ("BIGOPLUS", "⨁"),
        ["bigotimes"] = ("BIGOTIMES", "⨂"),
        ["int"] = ("INT", "∫"),
        ["intop"] = ("INT", "∫"),
        ["iint"] = ("DINT", "∬"),
        ["iiint"] = ("TINT", "∭"),
        ["oint"] = ("OINT", "∮"),
        ["oiint"] = ("ODINT", "∯"),
        ["oiiint"] = ("OTINT", "∰"),
    };

    /// <summary>Symbols typed as characters (≤, →, ∑): how they space, and the large operators' commands.</summary>
    private static readonly Dictionary<string, Kind> Kinds = Symbols.Values.DistinctBy(v => v.Glyph).ToDictionary(v => v.Glyph, v => v.Kind);

    private static readonly Dictionary<string, string> BigGlyphs = BigOperators.Values.DistinctBy(v => v.Glyph).ToDictionary(v => v.Glyph, v => v.Command);

    /// <summary>
    /// Symbol characters → the first command this reader takes for them, for TexMathWriter. Commands that need more
    /// than amsmath and amssymb (or don't exist in LaTeX) are left out: the writer has its own way for those.
    /// </summary>
    private static readonly Dictionary<string, string> SymbolNames = Symbols
        .Where(s => s.Key is not ("omicron" or "degree" or "iddots" or "copyright" or "euro" or "coloneqq" or "eqqcolon" or "neqsim" or "nequiv"))
        .DistinctBy(s => s.Value.Glyph).ToDictionary(s => s.Value.Glyph, s => s.Key, StringComparer.Ordinal);

    /// <summary>The TeX command (without its backslash) of a symbol character: ≤ → leq, α → alpha; null for none.</summary>
    internal static string? SymbolCommand(string glyph) => SymbolNames.GetValueOrDefault(glyph);

    /// <summary>The alphabet command and letter of a math alphabet letter (ℝ → mathbb, R; 𝓛 → mathcal, L), or null.</summary>
    internal static (string Command, char Letter)? AlphabetLetter(string glyph) =>
        Reader.AlphabetLetters.TryGetValue(glyph, out var letter) ? letter : null;

    /// <summary>Function names 한글 and rhwp know (written upright by themselves).</summary>
    private static readonly HashSet<string> Functions = new(StringComparer.Ordinal)
    {
        "sin", "cos", "tan", "cot", "sec", "csc", "arcsin", "arccos", "arctan", "sinh", "cosh", "tanh", "coth", "log",
        "ln", "lg", "exp", "det", "dim", "ker", "hom", "arg", "gcd", "lcm", "max", "min",
    };

    /// <summary>Other operator names TeX writes upright: as upright letters.</summary>
    private static readonly HashSet<string> OperatorNames = new(StringComparer.Ordinal)
    {
        "sup", "inf", "limsup", "liminf", "Pr", "deg", "sgn", "arccot", "arcsec", "arccsc", "csch", "sech", "Tr", "tr",
        "rank", "span", "id", "lb", "erf",
    };

    /// <summary>Accent commands → the decoration EquationScript and MathLayout know.</summary>
    private static readonly Dictionary<string, string> Accents = new(StringComparer.Ordinal)
    {
        ["hat"] = "hat",
        ["widehat"] = "hat",
        ["check"] = "check",
        ["widecheck"] = "check",
        ["tilde"] = "tilde",
        ["widetilde"] = "tilde",
        ["acute"] = "acute",
        ["grave"] = "grave",
        ["dot"] = "dot",
        ["ddot"] = "ddot",
        ["breve"] = "breve",
        ["bar"] = "bar",
        ["overline"] = "bar",
        ["underline"] = "under",
        ["vec"] = "vec",
        ["overrightarrow"] = "vec",
        ["overleftrightarrow"] = "dyad",
        ["overgroup"] = "arch",
        ["wideparen"] = "arch",
    };

    /// <summary>A relation after \not, as its own crossed-out character.</summary>
    private static readonly Dictionary<string, string> Negated = new(StringComparer.Ordinal)
    {
        ["="] = "≠",
        ["<"] = "≮",
        [">"] = "≯",
        ["≤"] = "≰",
        ["≥"] = "≱",
        ["∈"] = "∉",
        ["∋"] = "∌",
        ["⊂"] = "⊄",
        ["⊃"] = "⊅",
        ["⊆"] = "⊈",
        ["⊇"] = "⊉",
        ["≡"] = "≢",
        ["∼"] = "≁",
        ["≈"] = "≉",
        ["≅"] = "≇",
        ["∣"] = "∤",
        ["∥"] = "∦",
        ["∃"] = "∄",
        ["|"] = "∤",
    };

    /// <summary>Spacing commands → the width of a MathSpace (1 is half the size, see MathLayout).</summary>
    private static readonly Dictionary<string, double> Spaces = new(StringComparer.Ordinal)
    {
        [","] = 1 / 3.0,
        ["thinspace"] = 1 / 3.0,
        [":"] = 4 / 9.0,
        [">"] = 4 / 9.0,
        ["medspace"] = 4 / 9.0,
        [";"] = 5 / 9.0,
        ["thickspace"] = 5 / 9.0,
        [" "] = 2 / 3.0,
        ["enspace"] = 1,
        ["quad"] = 2,
        ["qquad"] = 4,
    };

    /// <summary>Commands that change only how TeX lays out or numbers, with how many braced arguments they take.</summary>
    private static readonly Dictionary<string, int> Skipped = new(StringComparer.Ordinal)
    {
        ["displaystyle"] = 0,
        ["textstyle"] = 0,
        ["scriptstyle"] = 0,
        ["scriptscriptstyle"] = 0,
        ["limits"] = 0,
        ["nolimits"] = 0,
        ["nonumber"] = 0,
        ["notag"] = 0,
        ["relax"] = 0,
        ["allowbreak"] = 0,
        ["nobreak"] = 0,
        ["strut"] = 0,
        ["mathstrut"] = 0,
        ["!"] = 0,
        ["negthinspace"] = 0,
        ["negmedspace"] = 0,
        ["negthickspace"] = 0,
        ["hline"] = 0,
        ["hdashline"] = 0,
        ["label"] = 1,
        ["color"] = 1,
        ["vphantom"] = 1,
    };

    /// <summary>The tree of a formula's TeX source; null when it is longer than MaxLength or nests deeper than MaxDepth.</summary>
    public static MathNode? Parse(string tex)
    {
        if (tex.Length > MaxLength)
            return null;
        try
        {
            var tree = new Reader(tex, 0).Top();
            return EquationScript.Deeper(tree, MaxDepth) ? null : tree;
        }
        catch (TooDeepException)
        {
            return null;
        }
    }

    private sealed class TooDeepException : Exception;

    [Flags]
    private enum Stops
    {
        None = 0,
        Brace = 1, // }
        Bracket = 2, // ] (an optional argument)
        Cells = 4, // & and \\
        Right = 8, // \right
        End = 16, // \end
    }

    /// <summary>What ended a row.</summary>
    private enum Stop
    {
        Eof,
        Brace,
        Bracket,
        Cell,
        Line,
        Right,
        End,
    }

    private sealed class Reader(string s, int depth)
    {
        private int _i;
        private int _depth = depth;
        private readonly List<string> _tags = [];

        // ───────────────────────── Structure ─────────────────────────

        /// <summary>The whole formula: one row, or lines (\\) and cells (&amp;) as an aligned block.</summary>
        public MathNode Top()
        {
            var lines = new List<MathNode>();
            var line = new List<MathNode>();
            var multiline = false;
            while (true)
            {
                var (items, stop) = Row(Stops.Cells);
                line.AddRange(items);
                if (stop == Stop.Cell)
                {
                    line.Add(new MathAlign());
                    multiline = true;
                    continue;
                }
                if (stop == Stop.Line)
                {
                    lines.Add(new MathRow(line));
                    line = [];
                    multiline = true;
                    SkipOptional();
                    continue;
                }
                break;
            }
            if (line.Count > 0 || lines.Count == 0)
                lines.Add(new MathRow(line));
            var tree = multiline && lines.Count > 1 ? new MathPile(lines) : multiline ? lines[0] : Single(line);
            if (_tags.Count == 0)
                return tree;
            // \tag{1}: the number after the formula.
            return new MathRow([tree, new MathSpace(4), .. _tags.Select(t => (MathNode)new MathQuoted($"({t})"))]);
        }

        private static MathNode Single(List<MathNode> items) => items.Count == 1 ? items[0] : new MathRow(items);

        private void Enter()
        {
            if (++_depth > MaxDepth)
                throw new TooDeepException();
        }

        /// <summary>Items up to one of <paramref name="stops"/> (consumed) or the end.</summary>
        private (List<MathNode> Items, Stop Stop) Row(Stops stops)
        {
            Enter();
            try
            {
                var items = new List<MathNode>();
                List<MathNode>? numerator = null;
                (string Kind, bool Bar)? infix = null;
                var stop = Stop.Eof;
                while (true)
                {
                    SkipSpace();
                    if (_i >= s.Length)
                        break;
                    var c = s[_i];
                    if (c == '}')
                    {
                        _i++;
                        if (stops.HasFlag(Stops.Brace))
                        {
                            stop = Stop.Brace;
                            break;
                        }
                        continue; // an unmatched brace: syntax only
                    }
                    if (c == ']' && stops.HasFlag(Stops.Bracket))
                    {
                        _i++;
                        stop = Stop.Bracket;
                        break;
                    }
                    if (c == '&')
                    {
                        _i++;
                        if (stops.HasFlag(Stops.Cells))
                        {
                            stop = Stop.Cell;
                            break;
                        }
                        items.Add(new MathAlign());
                        continue;
                    }
                    if (c is '^' or '_')
                    {
                        _i++;
                        Script(items, c == '^');
                        continue;
                    }
                    if (c == '\'')
                    {
                        Primes(items);
                        continue;
                    }
                    if (c == '\\' && _i + 1 < s.Length)
                    {
                        var start = _i;
                        var name = CommandName();
                        switch (name)
                        {
                            case "\\" or "newline" or "cr":
                                if (stops.HasFlag(Stops.Cells))
                                {
                                    stop = Stop.Line;
                                    goto done;
                                }
                                continue; // a line break inside a group breaks nothing
                            case "right":
                                if (stops.HasFlag(Stops.Right))
                                {
                                    stop = Stop.Right;
                                    goto done;
                                }
                                Delimiter();
                                continue;
                            case "end":
                                RawArgument();
                                if (stops.HasFlag(Stops.End))
                                {
                                    stop = Stop.End;
                                    goto done;
                                }
                                continue;
                            case "over" or "atop" or "choose" or "brace" or "brack":
                                if (infix is { } pending)
                                    items = [Infix(numerator!, items, pending)];
                                numerator = items;
                                items = [];
                                infix = (name, name == "over");
                                continue;
                            case "rm" or "bf" or "it" or "cal" or "sf" or "tt":
                                {
                                    // Old switches style the rest of the group.
                                    var (rest, restStop) = Row(stops);
                                    items.Add(new MathStyled(SwitchStyle(name), Single(rest)));
                                    stop = restStop;
                                    goto done;
                                }
                        }
                        _i = start;
                    }
                    if (Atom(argument: false) is { } atom)
                        items.Add(atom);
                }
            done:
                if (infix is { } last)
                    items = [Infix(numerator!, items, last)];
                return (items, stop);
            }
            finally
            {
                _depth--;
            }
        }

        private static string SwitchStyle(string name) => name switch
        {
            "bf" => "bold",
            "it" => "it",
            _ => "rm",
        };

        /// <summary>a \over b, a \choose b (in brackets), a \atop b.</summary>
        private static MathNode Infix(List<MathNode> numerator, List<MathNode> denominator, (string Kind, bool Bar) infix)
        {
            var fraction = new MathFraction(Single(numerator), Single(denominator), infix.Bar);
            return infix.Kind switch
            {
                "choose" => new MathBrackets("(", ")", fraction),
                "brace" => new MathBrackets("{", "}", fraction),
                "brack" => new MathBrackets("[", "]", fraction),
                _ => fraction,
            };
        }

        /// <summary>A sub- or superscript on the item before (an empty base at the start of a row).</summary>
        private void Script(List<MathNode> items, bool super)
        {
            var script = Argument();
            var baseNode = items.Count > 0 ? items[^1] : null;
            if (baseNode is not null)
                items.RemoveAt(items.Count - 1);
            items.Add(Attach(baseNode, super ? null : script, super ? script : null));
        }

        private static MathNode Attach(MathNode? baseNode, MathNode? sub, MathNode? sup)
        {
            switch (baseNode)
            {
                case MathBigOperator big when (sub is null || big.Lower is null) && (sup is null || big.Upper is null):
                    return big with { Lower = sub ?? big.Lower, Upper = sup ?? big.Upper };
                case MathLimit { Lower: null } when sub is not null && sup is null:
                    return new MathLimit(sub);
                case MathScripts scripts when (sub is null || scripts.Sub is null) && (sup is null || scripts.Sup is null):
                    return scripts with { Sub = sub ?? scripts.Sub, Sup = sup ?? scripts.Sup };
                case MathScripts { Sup: MathRow primes } scripts when sup is not null && primes.Items.All(i => i is MathCommand { Command: "prime" }):
                    // f'^2: the prime and the power side by side above.
                    return scripts with { Sup = new MathRow([.. primes.Items, sup]) };
                default:
                    return new MathScripts(baseNode ?? MathRow.Empty, sub, sup);
            }
        }

        private void Primes(List<MathNode> items)
        {
            var primes = new List<MathNode>();
            while (_i < s.Length && s[_i] == '\'')
            {
                primes.Add(new MathCommand("prime", "′"));
                _i++;
            }
            var baseNode = items.Count > 0 ? items[^1] : null;
            if (baseNode is not null)
                items.RemoveAt(items.Count - 1);
            items.Add(Attach(baseNode, null, new MathRow(primes)));
        }

        /// <summary>One argument: a group, a command with its own arguments, or one character.</summary>
        private MathNode Argument()
        {
            while (true)
            {
                SkipSpace();
                if (_i >= s.Length || s[_i] is '}' or '&' or ']')
                    return MathRow.Empty;
                if (Atom(argument: true) is { } atom)
                    return atom;
            }
        }

        private MathNode Group(Stops stop = Stops.Brace)
        {
            var (items, _) = Row(stop);
            return Single(items);
        }

        /// <summary>The item at the position: a group, a number, a letter, a symbol or a command; null for one that shows nothing.</summary>
        private MathNode? Atom(bool argument)
        {
            Enter();
            try
            {
                var c = s[_i];
                if (c == '{')
                {
                    _i++;
                    var (items, _) = Row(Stops.Brace);
                    return items.Count == 1 ? items[0] : new MathRow(items);
                }
                if (c == '\\' && _i + 1 < s.Length)
                    return Command(CommandName());
                if (char.IsAsciiDigit(c))
                {
                    var start = _i++;
                    if (!argument)
                    {
                        while (_i < s.Length && (char.IsAsciiDigit(s[_i]) || (s[_i] == '.' && _i + 1 < s.Length && char.IsAsciiDigit(s[_i + 1]))))
                            _i++;
                    }
                    return new MathNumber(s[start.._i]);
                }
                _i++;
                if (char.IsHighSurrogate(c) && _i < s.Length && char.IsLowSurrogate(s[_i]))
                    return new MathText(s.Substring(_i++ - 1, 2));
                return Character(c);
            }
            finally
            {
                _depth--;
            }
        }

        private static MathNode Character(char c)
        {
            if (char.IsAsciiLetter(c))
                return new MathText(c.ToString());
            switch (c)
            {
                case '~':
                    return new MathSpace(Spaces[" "]);
                case '#' or '"' or '^' or '_' or '`':
                    return new MathQuoted(c.ToString());
                case '−' or '–':
                    return new MathSymbol("-");
                case '+' or '-' or '=' or '<' or '>' or ',' or '.' or '!' or '|' or '/' or '*' or ':' or '(' or ')' or '[' or ']' or ';' or '\'':
                    return new MathSymbol(c.ToString());
            }
            var glyph = c.ToString();
            if (BigGlyphs.TryGetValue(glyph, out var big))
                return new MathBigOperator(big, glyph, null, null);
            return Glyph(glyph, Kinds.GetValueOrDefault(glyph));
        }

        /// <summary>A symbol: a 한글 equation command where there is one (so the HWPX writer can name it), else its character.</summary>
        private static MathNode Glyph(string glyph, Kind kind)
        {
            if (glyph.Length == 1 && OmmlReader.Command(glyph[0]) is { } command)
                return new MathCommand(command, glyph);
            if (glyph is "(" or ")" or "[" or "]" or "|" or ":" or "." or "/" or "<" or ">" or "=")
                return new MathSymbol(glyph);
            return kind == Kind.Ordinary ? new MathText(glyph) : new MathSymbol(glyph);
        }

        // ───────────────────────── Commands ─────────────────────────

        /// <summary>After a backslash: a run of letters, or the one character after it.</summary>
        private string CommandName()
        {
            _i++; // the backslash
            var start = _i;
            while (_i < s.Length && char.IsAsciiLetter(s[_i]))
                _i++;
            if (_i == start)
            {
                var length = char.IsHighSurrogate(s[_i]) && _i + 1 < s.Length ? 2 : 1;
                _i += length;
                return s.Substring(_i - length, length);
            }
            if (_i < s.Length && s[_i] == '*' && s[start.._i] is "operatorname" or "tag")
                _i++;
            return s[start.._i].TrimEnd('*');
        }

        private MathNode? Command(string name)
        {
            if (Symbols.TryGetValue(name, out var symbol))
                return Glyph(symbol.Glyph, symbol.Kind);
            if (BigOperators.TryGetValue(name, out var big))
                return new MathBigOperator(big.Command, big.Glyph, null, null);
            if (Functions.Contains(name))
                return new MathFunction(name);
            if (OperatorNames.Contains(name))
                return Upright(name);
            if (Accents.TryGetValue(name, out var accent))
                return new MathAccent(accent, Argument());
            if (Spaces.TryGetValue(name, out var width))
                return new MathSpace(width);
            if (Skipped.TryGetValue(name, out var arguments))
            {
                for (var a = 0; a < arguments; a++)
                    RawArgument();
                return null;
            }
            switch (name)
            {
                case "{" or "}":
                    return Glyph(name, Kind.Ordinary);
                case "|":
                    return new MathSymbol("‖");
                case "#" or "$" or "%" or "&" or "_":
                    return new MathQuoted(name);
                case "lim":
                    return new MathLimit(null);
                case "frac" or "dfrac" or "tfrac" or "cfrac":
                    if (name == "cfrac")
                        SkipOptional();
                    return new MathFraction(Argument(), Argument());
                case "binom" or "dbinom" or "tbinom":
                    return new MathBrackets("(", ")", new MathFraction(Argument(), Argument(), Bar: false));
                case "sqrt":
                    {
                        MathNode? index = null;
                        SkipSpace();
                        if (_i < s.Length && s[_i] == '[')
                        {
                            _i++;
                            index = Group(Stops.Bracket);
                        }
                        return new MathRoot(index, Argument());
                    }
                case "left":
                    {
                        var open = Delimiter();
                        var (items, stop) = Row(Stops.Right);
                        var close = stop == Stop.Right ? Delimiter() : "";
                        return Brackets(open, Single(items), close);
                    }
                case "middle":
                    return Glyph(Delimiter() is { Length: > 0 } middle ? middle : "|", Kind.Relation);
                case "big" or "Big" or "bigg" or "Bigg" or "bigl" or "Bigl" or "biggl" or "Biggl" or "bigr" or "Bigr" or "biggr"
                    or "Biggr" or "bigm" or "Bigm" or "biggm" or "Biggm":
                    return Delimiter() is { Length: > 0 } delimiter ? Glyph(delimiter, Kind.Ordinary) : null;
                case "begin":
                    return Environment(RawArgument());
                case "text" or "textrm" or "textnormal" or "mbox" or "hbox" or "textup":
                    return Text(RawArgument(), null);
                case "textbf":
                    return Text(RawArgument(), "bold");
                case "textit" or "emph" or "textsl":
                    return Text(RawArgument(), "it");
                case "texttt" or "textsf":
                    return Text(RawArgument(), null);
                case "mathrm" or "mathup" or "mathsf" or "mathtt":
                    return new MathStyled("rm", Argument());
                case "mathnormal" or "mathit":
                    return new MathStyled("it", Argument());
                case "mathbf" or "mathbfup":
                    return new MathStyled("bold", new MathStyled("rm", Argument()));
                case "boldsymbol" or "bm" or "pmb":
                    return new MathStyled("bold", Argument());
                case "mathbb" or "Bbb":
                    return Alphabet(RawArgument(), Alphabets.DoubleStruck);
                case "mathcal" or "mathscr":
                    return Alphabet(RawArgument(), Alphabets.Script);
                case "mathfrak":
                    return Alphabet(RawArgument(), Alphabets.Fraktur);
                case "operatorname" or "mathop":
                    {
                        var raw = RawArgument();
                        return Functions.Contains(raw) ? new MathFunction(raw)
                            : raw == "lim" ? new MathLimit(null)
                            : new MathStyled("rm", new Reader(raw, _depth).Top());
                    }
                case "bmod" or "mod":
                    return new MathRow([new MathSpace(Spaces[";"]), Upright("mod"), new MathSpace(Spaces[";"])]);
                case "pmod":
                    return new MathRow([new MathSpace(Spaces["quad"] / 2), new MathSymbol("("), Upright("mod"), new MathSpace(Spaces[","]), Argument(), new MathSymbol(")")]);
                case "not":
                    {
                        var negated = Argument();
                        var text = negated switch
                        {
                            MathSymbol plain => plain.Text,
                            MathCommand command => command.Glyph,
                            MathText letter => letter.Text,
                            _ => null,
                        };
                        return text is not null && Negated.TryGetValue(text, out var crossed) ? Glyph(crossed, Kind.Relation) : new MathAccent("not", negated);
                    }
                case "overset" or "stackrel":
                    {
                        var over = Argument();
                        return new MathScripts(Argument(), null, over);
                    }
                case "underset":
                    {
                        var under = Argument();
                        return new MathScripts(Argument(), under, null);
                    }
                case "xrightarrow" or "xleftarrow" or "xLeftarrow" or "xRightarrow" or "xleftrightarrow":
                    {
                        MathNode? below = null;
                        SkipSpace();
                        if (_i < s.Length && s[_i] == '[')
                        {
                            _i++;
                            below = Group(Stops.Bracket);
                        }
                        var arrow = name switch
                        {
                            "xleftarrow" => "←",
                            "xLeftarrow" => "⇐",
                            "xRightarrow" => "⇒",
                            "xleftrightarrow" => "↔",
                            _ => "→",
                        };
                        return new MathScripts(Glyph(arrow, Kind.Relation), below, Argument());
                    }
                case "substack":
                    {
                        var rows = new List<MathNode>();
                        SkipSpace();
                        if (_i < s.Length && s[_i] == '{')
                        {
                            _i++;
                            while (true)
                            {
                                var (items, stop) = Row(Stops.Brace | Stops.Cells);
                                rows.Add(Single(items));
                                if (stop != Stop.Line)
                                    break;
                            }
                        }
                        return rows.Count == 1 ? rows[0] : new MathPile(rows);
                    }
                case "boxed" or "fbox" or "overbrace" or "underbrace" or "mathclap" or "mathllap" or "mathrlap" or "smash"
                    or "displaylimits" or "ensuremath":
                    return name == "displaylimits" ? null : Argument();
                case "textcolor" or "colorbox":
                    RawArgument();
                    return Argument();
                case "phantom" or "hphantom":
                    Argument();
                    return new MathSpace(1);
                case "hspace" or "mspace" or "hskip" or "kern" or "mkern" or "mskip":
                    if (name is "hspace" or "mspace")
                        RawArgument();
                    else
                        Dimension();
                    return new MathSpace(1);
                case "tag":
                    _tags.Add(RawArgument());
                    return null;
            }
            // Not known: kept as its text, with the [ … ] and { … } arguments right after it as written ("\foo{x}").
            return new MathQuoted("\\" + name + LiteralArguments());
        }

        /// <summary>The optional and braced arguments at the position, as their source text (consumed).</summary>
        private string LiteralArguments()
        {
            var start = _i;
            var end = _i;
            while (true)
            {
                var at = _i;
                SkipSpace();
                // [ … ] only before a group: "\foo [0, 1]" alone is an interval, not an argument.
                if (_i < s.Length && s[_i] == '[' && s.IndexOf(']', _i) is var close and > 0
                    && s.AsSpan(close + 1).TrimStart().StartsWith("{"))
                    _i = close + 1;
                else if (_i < s.Length && s[_i] == '{')
                    RawArgument();
                else
                {
                    _i = at;
                    break;
                }
                end = _i;
            }
            _i = end;
            return s[start..end].Trim();
        }

        private static MathNode Upright(string name) => new MathStyled("rm", new MathRow([.. name.Select(c => (MathNode)new MathText(c.ToString()))]));

        private static MathNode Brackets(string open, MathNode body, string close)
        {
            if (EquationScript.IsBracket(open) && EquationScript.IsBracket(close))
                return new MathBrackets(open, close, body);
            // Brackets the tree can't stretch (arrows, slashes): characters around the content.
            var row = new List<MathNode>();
            if (open.Length > 0)
                row.Add(Glyph(open, Kind.Ordinary));
            row.Add(body);
            if (close.Length > 0)
                row.Add(Glyph(close, Kind.Ordinary));
            return new MathRow(row);
        }

        /// <summary>The delimiter after \left, \right, \middle or \big: its character, "" for "." or nothing.</summary>
        private string Delimiter()
        {
            SkipSpace();
            if (_i >= s.Length)
                return "";
            var c = s[_i];
            if (c == '\\' && _i + 1 < s.Length)
            {
                var name = CommandName();
                return name switch
                {
                    "{" or "lbrace" => "{",
                    "}" or "rbrace" => "}",
                    "|" or "Vert" or "lVert" or "rVert" => "‖",
                    "vert" or "lvert" or "rvert" => "|",
                    _ => Symbols.TryGetValue(name, out var symbol) ? symbol.Glyph : "",
                };
            }
            if (c is '{' or '}' or '&')
                return "";
            _i++;
            return c switch
            {
                '.' => "",
                '<' => "⟨",
                '>' => "⟩",
                _ => char.IsHighSurrogate(c) || char.IsLetterOrDigit(c) ? "" : c.ToString(),
            };
        }

        // ───────────────────────── Environments ─────────────────────────

        private MathNode Environment(string name)
        {
            var bare = name.TrimEnd('*');
            if (bare is "array" or "subarray" or "alignat" or "alignedat")
                RawArgument(); // the column spec or the number of columns
            else
                SkipOptional(); // [t], [c] (aligned, gathered), [r] (mathtools' matrix*)
            var rows = new List<List<MathNode>>();
            var cells = new List<MathNode>();
            while (true)
            {
                var (items, stop) = Row(Stops.Cells | Stops.End);
                cells.Add(Single(items));
                if (stop == Stop.Cell)
                    continue;
                rows.Add(cells);
                cells = [];
                if (stop != Stop.Line)
                    break;
                SkipOptional();
            }
            // A \\ before \end leaves an empty last row.
            if (rows.Count > 1 && rows[^1] is [MathRow { Items.Count: 0 }])
                rows.RemoveAt(rows.Count - 1);

            IReadOnlyList<IReadOnlyList<MathNode>> Grid() => rows.Select(r => (IReadOnlyList<MathNode>)r).ToList();
            switch (bare)
            {
                case "matrix" or "array" or "smallmatrix" or "subarray":
                    return new MathMatrix("matrix", Grid());
                case "pmatrix":
                    return new MathMatrix("pmatrix", Grid());
                case "bmatrix":
                    return new MathMatrix("bmatrix", Grid());
                case "vmatrix":
                    return new MathMatrix("dmatrix", Grid());
                case "Bmatrix":
                    return new MathBrackets("{", "}", new MathMatrix("matrix", Grid()));
                case "Vmatrix":
                    return new MathBrackets("‖", "‖", new MathMatrix("matrix", Grid()));
                case "cases" or "dcases" or "numcases":
                    return new MathCases(Grid());
                case "rcases":
                    return new MathBrackets("", "}", new MathMatrix("matrix", Grid()));
                case "aligned" or "align" or "alignat" or "alignedat" or "split" or "eqnarray" or "flalign" or "darray":
                    return Lines(rows, aligned: true);
                case "gather" or "gathered" or "multline" or "equation" or "displaymath" or "math":
                    return Lines(rows, aligned: false);
            }
            // Not known: its name kept as text around the lines.
            return new MathRow([new MathQuoted($"\\begin{{{name}}}"), Lines(rows, aligned: true), new MathQuoted($"\\end{{{name}}}")]);
        }

        /// <summary>Lines of an aligned block, the cells of each joined by alignment marks; one line by itself.</summary>
        private static MathNode Lines(List<List<MathNode>> rows, bool aligned)
        {
            var lines = rows.Select(cells =>
            {
                var items = new List<MathNode>();
                for (var c = 0; c < cells.Count; c++)
                {
                    if (c > 0 && aligned)
                        items.Add(new MathAlign());
                    if (cells[c] is MathRow row)
                        items.AddRange(row.Items);
                    else
                        items.Add(cells[c]);
                }
                return (MathNode)new MathRow(items);
            }).ToList();
            return lines.Count == 1 ? lines[0] : new MathPile(lines);
        }

        // ───────────────────────── Text ─────────────────────────

        /// <summary>\text{…}: upright text with its spaces, and $…$ inside it as math.</summary>
        private MathNode Text(string raw, string? style)
        {
            var items = new List<MathNode>();
            var text = new StringBuilder();
            void Flush()
            {
                if (text.Length > 0)
                    items.Add(new MathQuoted(text.ToString()));
                text.Clear();
            }
            for (var i = 0; i < raw.Length; i++)
            {
                var c = raw[i];
                if (c == '$')
                {
                    var end = raw.IndexOf('$', i + 1);
                    if (end > i + 1)
                    {
                        Flush();
                        var inner = new Reader(raw[(i + 1)..end], _depth + 1).Top();
                        items.Add(inner);
                        i = end;
                        continue;
                    }
                }
                if (c is '{' or '}')
                    continue;
                if (c == '~')
                {
                    text.Append(' ');
                    continue;
                }
                if (c == '\\' && i + 1 < raw.Length)
                {
                    var start = ++i;
                    while (i < raw.Length && char.IsAsciiLetter(raw[i]))
                        i++;
                    if (i == start)
                    {
                        // \%, \&, \_, \{, \  …; \\ breaks nothing in a line of text.
                        if (raw[i] != '\\')
                            text.Append(raw[i]);
                        continue;
                    }
                    var command = raw[start..i];
                    i--;
                    text.Append(command switch
                    {
                        "quad" => " ",
                        "qquad" => "  ",
                        "textbackslash" => "\\",
                        "textasciitilde" => "~",
                        "textasciicircum" => "^",
                        "textendash" => "–",
                        "textemdash" => "—",
                        "ldots" or "dots" or "textellipsis" => "…",
                        _ => "\\" + command,
                    });
                    continue;
                }
                text.Append(char.IsControl(c) ? ' ' : c);
            }
            Flush();
            var node = Single(items);
            return style is null ? node : new MathStyled(style, node);
        }

        private enum Alphabets
        {
            DoubleStruck,
            Script,
            Fraktur,
        }

        /// <summary>Each letter of the math alphabets → the command and the letter it is read from (ℝ → mathbb, R).</summary>
        public static readonly Dictionary<string, (string Command, char Letter)> AlphabetLetters = BuildAlphabetLetters();

        private static Dictionary<string, (string Command, char Letter)> BuildAlphabetLetters()
        {
            var letters = new Dictionary<string, (string Command, char Letter)>(StringComparer.Ordinal);
            foreach (var (alphabet, command) in new[] { (Alphabets.DoubleStruck, "mathbb"), (Alphabets.Script, "mathcal"), (Alphabets.Fraktur, "mathfrak") })
            {
                for (var c = 'A'; c <= 'Z'; c++)
                {
                    letters.TryAdd(Letter(c, alphabet), (command, c));
                    letters.TryAdd(Letter(char.ToLowerInvariant(c), alphabet), (command, char.ToLowerInvariant(c)));
                }
            }
            return letters;
        }

        /// <summary>\mathbb{R}, \mathcal{L}, \mathfrak{g}: the letters of the Unicode math alphabet.</summary>
        private static MathNode Alphabet(string raw, Alphabets alphabet)
        {
            var items = new List<MathNode>();
            foreach (var c in raw)
            {
                if (char.IsWhiteSpace(c) || c is '{' or '}')
                    continue;
                items.Add(char.IsAsciiLetter(c) || (char.IsAsciiDigit(c) && alphabet == Alphabets.DoubleStruck)
                    ? new MathText(Letter(c, alphabet))
                    : Character(c));
            }
            return Single(items);
        }

        private static string Letter(char c, Alphabets alphabet)
        {
            var exception = (alphabet, c) switch
            {
                (Alphabets.DoubleStruck, 'C') => 'ℂ',
                (Alphabets.DoubleStruck, 'H') => 'ℍ',
                (Alphabets.DoubleStruck, 'N') => 'ℕ',
                (Alphabets.DoubleStruck, 'P') => 'ℙ',
                (Alphabets.DoubleStruck, 'Q') => 'ℚ',
                (Alphabets.DoubleStruck, 'R') => 'ℝ',
                (Alphabets.DoubleStruck, 'Z') => 'ℤ',
                (Alphabets.Script, 'B') => 'ℬ',
                (Alphabets.Script, 'E') => 'ℰ',
                (Alphabets.Script, 'F') => 'ℱ',
                (Alphabets.Script, 'H') => 'ℋ',
                (Alphabets.Script, 'I') => 'ℐ',
                (Alphabets.Script, 'L') => 'ℒ',
                (Alphabets.Script, 'M') => 'ℳ',
                (Alphabets.Script, 'R') => 'ℛ',
                (Alphabets.Script, 'e') => 'ℯ',
                (Alphabets.Script, 'g') => 'ℊ',
                (Alphabets.Script, 'o') => 'ℴ',
                (Alphabets.Fraktur, 'C') => 'ℭ',
                (Alphabets.Fraktur, 'H') => 'ℌ',
                (Alphabets.Fraktur, 'I') => 'ℑ',
                (Alphabets.Fraktur, 'R') => 'ℜ',
                (Alphabets.Fraktur, 'Z') => 'ℨ',
                _ => '\0',
            };
            if (exception != '\0')
                return exception.ToString();
            if (char.IsAsciiDigit(c))
                return char.ConvertFromUtf32(0x1D7D8 + (c - '0'));
            var capitals = alphabet switch
            {
                Alphabets.DoubleStruck => 0x1D538,
                Alphabets.Script => 0x1D49C,
                _ => 0x1D504,
            };
            return char.ConvertFromUtf32(capitals + (char.IsAsciiLetterUpper(c) ? c - 'A' : 26 + c - 'a'));
        }

        // ───────────────────────── Characters ─────────────────────────

        /// <summary>Skips spaces and % comments.</summary>
        private void SkipSpace()
        {
            while (_i < s.Length)
            {
                if (char.IsWhiteSpace(s[_i]))
                {
                    _i++;
                }
                else if (s[_i] == '%')
                {
                    while (_i < s.Length && s[_i] != '\n')
                        _i++;
                }
                else
                {
                    break;
                }
            }
        }

        /// <summary>An optional [ … ] argument, skipped.</summary>
        private void SkipOptional()
        {
            SkipSpace();
            if (_i >= s.Length || s[_i] != '[')
                return;
            var end = s.IndexOf(']', _i);
            _i = end < 0 ? s.Length : end + 1;
        }

        /// <summary>A braced argument as its text (nested braces kept), or the next character or command.</summary>
        private string RawArgument()
        {
            SkipSpace();
            if (_i >= s.Length)
                return "";
            if (s[_i] != '{')
            {
                if (s[_i] == '\\' && _i + 1 < s.Length)
                    return "\\" + CommandName();
                return s[_i] is '}' or '&' ? "" : s[_i++].ToString();
            }
            var start = ++_i;
            var level = 1;
            while (_i < s.Length)
            {
                var c = s[_i];
                if (c == '\\')
                {
                    _i += 2;
                    continue;
                }
                if (c == '{')
                    level++;
                else if (c == '}' && --level == 0)
                    break;
                _i++;
            }
            var end = Math.Min(_i, s.Length);
            _i = Math.Min(_i + 1, s.Length);
            return s[start..end];
        }

        /// <summary>A TeX dimension after \kern or \mskip ("3mu", "-0.5em"), skipped.</summary>
        private void Dimension()
        {
            SkipSpace();
            while (_i < s.Length && (char.IsAsciiDigit(s[_i]) || s[_i] is '.' or '-' or '+'))
                _i++;
            var letters = 0;
            while (_i < s.Length && letters < 2 && char.IsAsciiLetter(s[_i]))
            {
                _i++;
                letters++;
            }
        }
    }
}
