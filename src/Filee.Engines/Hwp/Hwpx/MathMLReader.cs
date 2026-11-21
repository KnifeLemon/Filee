// MathML (presentation markup, MathML 3 and Core) → the equation tree (MathNode), the other way from MathMLWriter: the
// formula objects of OpenDocument files (LibreOffice Math's content.xml, Filee's own) become equations again.
//
// A TeX annotation (what MathMLWriter keeps of an equation read from TeX) is read as TeX. A StarMath annotation (what
// LibreOffice and Filee's ODT writer keep as the formula's source) is kept with the formula, and read as StarMath when
// the markup shows nothing. Otherwise the elements: tokens
// (mi italic when one letter, mn, mo, mtext, ms, mspace) through OmmlReader's tokens, so symbols, large operators,
// function names and lim come out as they do from Word; mrow, mstyle (bold and upright variants), mpadded, mphantom,
// menclose, semantics (its first child); mfrac, msqrt, mroot, scripts (a large operator's are its limits), under and
// over (accents, lim's limit, stacked content), tables (matrices in their brackets, cases after a brace, aligned
// lines as MathMLWriter writes them, else piles), stretched brackets (a fence at both ends of a row, mfenced) and
// mmultiscripts' first scripts. Unknown elements keep their content.
//
// Nesting is bounded (MaxDepth): a deeper formula becomes its text in a row, as OmmlReader reads one.

using System.Globalization;
using System.Xml.Linq;
using Filee.Engines.Hwp.Hwpx.Docx;

namespace Filee.Engines.Hwp.Hwpx;

internal static class MathMLReader
{
    internal static readonly XNamespace MathMl = "http://www.w3.org/1998/Math/MathML";

    /// <summary>Element levels read as a tree; deeper formulas become their text.</summary>
    private const int MaxDepth = 200;

    private readonly record struct Style(bool Upright, bool Bold, bool Explicit = false);

    /// <summary>
    /// A formula read from MathML: its tree, its TeX source and its StarMath source when it kept them, and whether it is a
    /// display formula.
    /// </summary>
    internal sealed record Formula(MathNode Math, string? Tex, bool Display)
    {
        public string? StarMath { get; init; }
    }

    /// <summary>The formula of a &lt;math&gt; element; null when it shows nothing.</summary>
    public static Formula? Read(XElement math)
    {
        var display = string.Equals(Attr(math, "display"), "block", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(Attr(math, "mode"), "display", StringComparison.OrdinalIgnoreCase);
        var tex = math.Descendants().FirstOrDefault(e => e.Name.LocalName == "annotation"
                                                        && Attr(e, "encoding") is "application/x-tex" or "TeX" or "LaTeX")?.Value.Trim();
        var starMath = math.Descendants().FirstOrDefault(e => e.Name.LocalName == "annotation"
                                                             && Attr(e, "encoding")?.Trim() == Hwpx.StarMath.Encoding)?.Value.Trim() is { Length: > 0 } source ? source : null;
        if (tex is { Length: > 0 } && LatexMath.Parse(tex) is { } parsed && !IsEmpty(parsed))
            return new Formula(parsed, tex, display) { StarMath = starMath };

        MathNode node;
        if (Nesting.Deeper(math, MaxDepth))
        {
            var items = new List<MathNode>();
            OmmlReader.Tokens(string.Concat(math.Descendants().Where(e => !e.HasElements && !IsAnnotation(e)).Select(e => e.Value)), items, false, false);
            node = new MathRow(items);
        }
        else
        {
            node = Row(math.Elements(), default);
        }
        // Markup that shows nothing (only the annotation): the formula from its StarMath source.
        if (IsEmpty(node) && starMath is not null && Hwpx.StarMath.Read(starMath) is { } fromSource)
            node = fromSource;
        return IsEmpty(node) ? null : new Formula(node, null, display) { StarMath = starMath };
    }

    // ───────────────────────── Rows ─────────────────────────

    /// <summary>Children side by side; a fence at both ends makes stretched brackets (a table in them a matrix or cases).</summary>
    private static MathRow Row(IEnumerable<XElement> children, Style style)
    {
        var elements = children.Where(e => !IsAnnotation(e)).ToList();
        if (elements.Count >= 2 && Fence(elements[0], opening: true) is { } open)
        {
            var close = Fence(elements[^1], opening: false);
            var inner = close is null ? elements.Skip(1) : elements.Skip(1).SkipLast(1);
            var body = inner.ToList();
            if (body is [var only] && Unwrapped(only) is { } table && table.Name.LocalName == "mtable")
            {
                var command = (open, close) switch
                {
                    ("(", ")") => "pmatrix",
                    ("[", "]") => "bmatrix",
                    ("|", "|") => "dmatrix",
                    _ => null,
                };
                if (command is not null)
                    return new MathRow([new MathMatrix(command, Cells(table, style))]);
                if (open == "{" && close is null or "")
                    return new MathRow([new MathCases(Cells(table, style))]);
            }
            if (close is not null && EquationScript.IsBracket(open) && EquationScript.IsBracket(close))
                return new MathRow([new MathBrackets(open, close, Row(body, style))]);
        }
        var items = new List<MathNode>();
        foreach (var element in elements)
            Element(element, items, style);
        return new MathRow(items);
    }

    /// <summary>The element as one node: its only item, or a row of them.</summary>
    private static MathNode Node(XElement? element, Style style)
    {
        if (element is null)
            return MathRow.Empty;
        var items = new List<MathNode>();
        Element(element, items, style);
        return items.Count == 1 ? items[0] : new MathRow(items);
    }

    private static void Element(XElement element, List<MathNode> output, Style style)
    {
        var children = element.Elements().Where(e => !IsAnnotation(e)).ToList();
        XElement? Child(int index) => index < children.Count ? children[index] : null;
        switch (element.Name.LocalName)
        {
            case "mi":
                Identifier(element.Value, output, Styled(element, style));
                break;
            case "mn":
                OmmlReader.Tokens(element.Value.Trim(), output, upright: true, Styled(element, style).Bold);
                break;
            case "mo":
                Operator(element.Value.Trim(), output, Styled(element, style));
                break;
            case "mtext" or "ms":
                if (element.Value.Trim().Length > 0)
                    output.Add(new MathQuoted(element.Value.Replace(' ', ' ')));
                break;
            case "mspace":
                if (Em(Attr(element, "width")) is > 0 and var em)
                    output.Add(new MathSpace(Math.Round(em * 2, 3)));
                break;
            case "mfrac":
                {
                    var thickness = Attr(element, "linethickness")?.Trim();
                    var bar = thickness is null || !(thickness == "0" || thickness.StartsWith("0px", StringComparison.Ordinal) || thickness == "0em" || thickness == "0pt");
                    output.Add(new MathFraction(Node(Child(0), style), Node(Child(1), style), bar));
                    break;
                }
            case "msqrt":
                output.Add(new MathRoot(null, Row(children, style)));
                break;
            case "mroot":
                output.Add(new MathRoot(Node(Child(1), style), Node(Child(0), style)));
                break;
            case "msub":
                output.Add(Scripts(Node(Child(0), style), Node(Child(1), style), null));
                break;
            case "msup":
                output.Add(Scripts(Node(Child(0), style), null, Node(Child(1), style)));
                break;
            case "msubsup":
                output.Add(Scripts(Node(Child(0), style), Node(Child(1), style), Node(Child(2), style)));
                break;
            case "mmultiscripts":
                {
                    // The first scripts after the base; prescripts and further pairs are left out.
                    var sub = Child(1) is { Name.LocalName: not ("none" or "mprescripts") } s ? Node(s, style) : null;
                    var sup = Child(2) is { Name.LocalName: not ("none" or "mprescripts") } p ? Node(p, style) : null;
                    output.Add(Scripts(Node(Child(0), style), sub, sup));
                    break;
                }
            case "munder":
                output.Add(UnderOver(element, Node(Child(0), style), Node(Child(1), style), null));
                break;
            case "mover":
                output.Add(UnderOver(element, Node(Child(0), style), null, Node(Child(1), style)));
                break;
            case "munderover":
                output.Add(UnderOver(element, Node(Child(0), style), Node(Child(1), style), Node(Child(2), style)));
                break;
            case "mtable":
                output.Add(Table(element, style));
                break;
            case "mfenced":
                {
                    var open = Attr(element, "open") ?? "(";
                    var close = Attr(element, "close") ?? ")";
                    var separator = (Attr(element, "separators") ?? ",").Trim();
                    var items = new List<MathNode>();
                    for (var i = 0; i < children.Count; i++)
                    {
                        if (i > 0 && separator.Length > 0)
                            OmmlReader.Tokens(separator[Math.Min(i - 1, separator.Length - 1)].ToString(), items, true, false);
                        Element(children[i], items, style);
                    }
                    if (EquationScript.IsBracket(open) && EquationScript.IsBracket(close))
                    {
                        output.Add(new MathBrackets(open, close, new MathRow(items)));
                    }
                    else
                    {
                        OmmlReader.Tokens(open, output, true, false);
                        output.AddRange(items);
                        OmmlReader.Tokens(close, output, true, false);
                    }
                    break;
                }
            case "semantics":
                if (Child(0) is { } presentation)
                    Element(presentation, output, style);
                break;
            case "maction":
                if (Child(0) is { } shown)
                    Element(shown, output, style);
                break;
            case "mphantom" or "none" or "mprescripts" or "malignmark" or "maligngroup":
                break;
            default:
                // math, mrow, mstyle, mpadded, menclose, merror, unknown elements: their content.
                Add(output, Row(children, Styled(element, style)));
                break;
        }
    }

    /// <summary>Adds a row's items (brackets, a matrix or cases are one item of it).</summary>
    private static void Add(List<MathNode> output, MathRow row) => output.AddRange(row.Items);

    // ───────────────────────── Tokens ─────────────────────────

    /// <summary>
    /// An identifier: one letter italic unless the variant says otherwise; a longer name upright (a function name such
    /// as sin is one), as MathML sets them.
    /// </summary>
    private static void Identifier(string text, List<MathNode> output, Style style)
    {
        text = text.Trim();
        if (text.Length == 0)
            return;
        var letters = text.EnumerateRunes().Count();
        var upright = style.Explicit ? style.Upright : letters > 1;
        OmmlReader.Tokens(text, output, upright, style.Bold);
    }

    private static void Operator(string text, List<MathNode> output, Style style)
    {
        if (text.Length == 0)
            return;
        // Invisible times, function application and separator show nothing.
        text = text.Replace("⁢", "", StringComparison.Ordinal).Replace("⁡", "", StringComparison.Ordinal).Replace("⁣", "", StringComparison.Ordinal);
        if (text.Length > 0)
            OmmlReader.Tokens(text, output, upright: true, style.Bold);
    }

    /// <summary>The style an element's mathvariant (or a bold font weight) gives its content.</summary>
    private static Style Styled(XElement element, Style style)
    {
        var variant = Attr(element, "mathvariant")?.Trim();
        var weight = Attr(element, "fontweight")?.Trim();
        if (weight == "bold" || (Attr(element, "style")?.Contains("font-weight:bold", StringComparison.Ordinal) ?? false))
            style = style with { Bold = true };
        return variant switch
        {
            "normal" => style with { Upright = true, Explicit = true },
            "bold" => style with { Upright = true, Bold = true, Explicit = true },
            "italic" => style with { Upright = false, Explicit = true },
            "bold-italic" => style with { Upright = false, Bold = true, Explicit = true },
            _ => style,
        };
    }

    // ───────────────────────── Structures ─────────────────────────

    /// <summary>Scripts; a large operator as the base takes them as its limits.</summary>
    private static MathNode Scripts(MathNode baseNode, MathNode? sub, MathNode? sup)
    {
        if (baseNode is MathBigOperator { Lower: null, Upper: null } big)
            return big with { Lower = sub, Upper = sup };
        return new MathScripts(baseNode, sub, sup);
    }

    /// <summary>Under and over: an accent, lim's limit, a large operator's limits, else stacked content.</summary>
    private static MathNode UnderOver(XElement element, MathNode baseNode, MathNode? under, MathNode? over)
    {
        if (baseNode is MathBigOperator { Lower: null, Upper: null } big)
            return big with { Lower = under, Upper = over };
        if (baseNode is MathLimit { Lower: null } && under is not null && over is null)
            return new MathLimit(under);
        if (over is not null && under is null && Accent(Glyph(over), below: false) is { } accent
            && (Attr(element, "accent") == "true" || Glyph(over)!.Length == 1))
            return new MathAccent(accent, baseNode);
        if (under is not null && over is null && Accent(Glyph(under), below: true) is { } line)
            return new MathAccent(line, baseNode);
        return new MathStack(baseNode, over, under);
    }

    /// <summary>The character of a node that is one symbol, else null.</summary>
    private static string? Glyph(MathNode node) => node switch
    {
        MathSymbol symbol => symbol.Text,
        MathCommand command => command.Glyph,
        MathText text => text.Text,
        MathQuoted quoted => quoted.Text,
        MathRow { Items: [var only] } => Glyph(only),
        _ => null,
    };

    private static string? Accent(string? mark, bool below) => (mark, below) switch
    {
        ("_" or "̲" or "‾" or "¯", true) => "UNDERLINE",
        (_, true) => null,
        ("^" or "ˆ" or "̂", _) => "hat",
        ("ˇ" or "̌", _) => "check",
        ("~" or "˜" or "̃", _) => "tilde",
        ("´" or "́", _) => "acute",
        ("`" or "̀", _) => "grave",
        ("˙" or "̇", _) => "dot",
        ("¨" or "̈", _) => "ddot",
        ("˘" or "̆", _) => "breve",
        ("→" or "⃗", _) => "vec",
        ("↔" or "⃡", _) => "dyad",
        ("⏜", _) => "arch",
        ("‾" or "¯" or "̅" or "̄" or "_", _) => "OVERLINE",
        _ => null,
    };

    /// <summary>
    /// A table: aligned lines (cells right and left in turn, as MathMLWriter writes them) as a pile with alignment marks,
    /// one column as a pile, else a matrix.
    /// </summary>
    private static MathNode Table(XElement table, Style style)
    {
        var rows = Cells(table, style);
        if (rows.Count == 0)
            return MathRow.Empty;
        if (rows.All(r => r.Count == 1))
        {
            var align = Attr(table, "columnalign")?.Trim() switch
            {
                "left" => "lpile",
                "right" => "rpile",
                _ => "pile",
            };
            return new MathPile(rows.Select(r => r[0]).ToList()) { Align = align };
        }
        var cells = table.Elements().Where(IsRow).SelectMany(r => r.Elements().Where(e => e.Name.LocalName == "mtd")).ToList();
        var aligned = cells.Count > 0 && table.Elements().Where(IsRow).All(r =>
            r.Elements().Where(e => e.Name.LocalName == "mtd").Select((cell, i) => Side(cell) == (i % 2 == 0 ? "right" : "left")).All(b => b));
        if (aligned)
        {
            var lines = new List<MathNode>();
            foreach (var row in rows)
            {
                var items = new List<MathNode>();
                for (var c = 0; c < row.Count; c++)
                {
                    if (c > 0)
                        items.Add(new MathAlign());
                    items.Add(row[c]);
                }
                lines.Add(new MathRow(items));
            }
            return new MathPile(lines);
        }
        return new MathMatrix("matrix", rows);
    }

    private static string? Side(XElement cell) =>
        Attr(cell, "columnalign")?.Trim() ?? (Attr(cell, "style") is { } css && css.Contains("text-align:", StringComparison.Ordinal)
            ? css[(css.IndexOf("text-align:", StringComparison.Ordinal) + 11)..].Split(';')[0].Trim()
            : null);

    private static bool IsRow(XElement element) => element.Name.LocalName is "mtr" or "mlabeledtr";

    private static List<IReadOnlyList<MathNode>> Cells(XElement table, Style style)
    {
        var rows = new List<IReadOnlyList<MathNode>>();
        foreach (var row in table.Elements().Where(IsRow))
        {
            // A labelled row's first cell is its label.
            var cells = row.Elements().Where(e => e.Name.LocalName == "mtd").Skip(row.Name.LocalName == "mlabeledtr" ? 1 : 0);
            rows.Add(cells.Select(cell => (MathNode)Row(cell.Elements(), style)).ToList());
        }
        return rows;
    }

    /// <summary>The bracket of a fence (an mo with fence or stretchy set, a bracket character or nothing), else null.</summary>
    private static string? Fence(XElement element, bool opening)
    {
        var mo = Unwrapped(element);
        if (mo is null || mo.Name.LocalName != "mo")
            return null;
        if (Attr(mo, "stretchy") == "false")
            return null;
        if (Attr(mo, "fence") != "true" && Attr(mo, "stretchy") != "true")
            return null;
        var form = Attr(mo, "form");
        if (form is "prefix" && !opening || form is "postfix" && opening)
            return null;
        var text = mo.Value.Trim();
        return text.Length == 0 || EquationScript.IsBracket(text) ? text : null;
    }

    /// <summary>An element inside mrows of one child each.</summary>
    private static XElement? Unwrapped(XElement element)
    {
        var current = element;
        for (var i = 0; i < 8 && current.Name.LocalName == "mrow"; i++)
        {
            var inner = current.Elements().Where(e => !IsAnnotation(e)).ToList();
            if (inner.Count != 1)
                return current;
            current = inner[0];
        }
        return current;
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static bool IsAnnotation(XElement element) => element.Name.LocalName is "annotation" or "annotation-xml";

    private static string? Attr(XElement element, string name) => (string?)element.Attribute(name);

    /// <summary>A length in ems ("0.5em", "1em", "6pt" at 12 pt, the named MathML spaces), else null.</summary>
    private static double? Em(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var text = value.Trim();
        var named = text switch
        {
            "veryverythinmathspace" => 1 / 18.0,
            "verythinmathspace" => 2 / 18.0,
            "thinmathspace" => 3 / 18.0,
            "mediummathspace" => 4 / 18.0,
            "thickmathspace" => 5 / 18.0,
            "verythickmathspace" => 6 / 18.0,
            "veryverythickmathspace" => 7 / 18.0,
            _ => (double?)null,
        };
        if (named is not null)
            return named;
        var unit = text.TrimStart('-', '+', '.', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        if (!double.TryParse(text.AsSpan(0, text.Length - unit.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
            return null;
        var em = unit.Trim() switch
        {
            "em" or "" => number,
            "ex" => number * 0.43,
            "pt" => number / 12,
            "px" => number / 16,
            "mu" => number / 18,
            _ => double.NaN,
        };
        return double.IsFinite(em) ? Math.Clamp(em, 0, 50) : null;
    }

    private static bool IsEmpty(MathNode node) => node switch
    {
        MathRow row => row.Items.All(IsEmpty),
        MathAlign => true,
        _ => false,
    };
}
