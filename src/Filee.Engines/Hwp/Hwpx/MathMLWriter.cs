// The equation tree (MathNode) → MathML (MathML Core, which browsers and EPUB 3 readers draw), for the HTML and EPUB
// writers and Markdown → HTML. An equation read from TeX keeps its source as the TeX annotation (and alttext), so a page
// can be copied back to TeX and readers without MathML have the source to show.
//
// Letters are <mi> (italic), several letters each their own unless upright; numbers <mn>; operators, relations and
// brackets <mo> (unbracketed "(" and ")" don't stretch, as in TeX); quoted text <mtext>; Greek capitals upright.
// Fractions, roots, scripts, large operators (limits above and below, at the side for integrals), lim, matrices in
// their brackets, cases, piles and aligned lines (<mtable>, the cells of a line at its & marks, right and left in
// turn), stretched brackets, accents and spaces. Bold and upright follow MathStyled.

using System.Globalization;
using System.Text;
using Filee.Engines.Hwp.Hwpx.Docx;

namespace Filee.Engines.Hwp.Hwpx;

internal static class MathMLWriter
{
    private const string Namespace = "http://www.w3.org/1998/Math/MathML";

    /// <summary>Symbols that are things rather than operators: identifiers in MathML.</summary>
    private static readonly HashSet<string> Ordinary =
    [
        "∞", "∂", "∇", "∅", "ℏ", "ℓ", "ℵ", "℘", "°", "∠", "△", "⊤", "ℜ", "ℑ", "ı", "ȷ", "♠", "♣", "♡", "♢", "♭", "♮", "♯",
        "†", "‡", "§", "¶", "∡", "ℶ", "ℷ", "□", "■", "✓", "€", "£", "¥", "©",
    ];

    private readonly record struct Style(bool Upright, bool Bold);

    /// <summary>One equation as a &lt;math&gt; element; <paramref name="tex"/> (its TeX source) becomes its annotation.</summary>
    public static string Write(MathNode node, bool display, string? tex = null)
    {
        var sb = new StringBuilder();
        sb.Append("<math xmlns=\"").Append(Namespace).Append('"');
        if (display)
            sb.Append(" display=\"block\"");
        if (tex is not null)
            sb.Append(" alttext=\"").Append(OmmlWriter.Escape(tex)).Append('"');
        sb.Append('>');
        if (tex is not null)
            sb.Append("<semantics>");
        Group(node, sb, default);
        if (tex is not null)
            sb.Append("<annotation encoding=\"application/x-tex\">").Append(OmmlWriter.Escape(tex)).Append("</annotation></semantics>");
        sb.Append("</math>");
        return sb.ToString();
    }

    /// <summary>A node as one element (an &lt;mrow&gt; around several), as the children of mfrac, msub … must be.</summary>
    private static void Group(MathNode node, StringBuilder sb, Style style)
    {
        sb.Append("<mrow>");
        Node(node, sb, style);
        sb.Append("</mrow>");
    }

    private static void Node(MathNode node, StringBuilder sb, Style style)
    {
        switch (node)
        {
            case MathRow row:
                foreach (var item in row.Items)
                    Node(item, sb, style);
                break;
            case MathText text:
                Text(text.Text, sb, style);
                break;
            case MathNumber number:
                Token("mn", number.Digits, sb, style);
                break;
            case MathSymbol symbol:
                Operator(symbol.Text == "-" ? "−" : symbol.Text, sb, style);
                break;
            case MathCommand command:
                if (IsGreek(command.Glyph) || Ordinary.Contains(command.Glyph))
                    Text(command.Glyph, sb, style);
                else
                    Operator(command.Glyph, sb, style);
                break;
            case MathQuoted quoted:
                // Spaces at the ends of a token are dropped: kept as no-break spaces.
                Token("mtext", quoted.Text.Length > 0 && (quoted.Text[0] == ' ' || quoted.Text[^1] == ' ')
                    ? quoted.Text.Replace(' ', ' ') : quoted.Text, sb, style with { Upright = true });
                break;
            case MathFunction function:
                // sin x, det A: a thin space after the name, as TeX sets it.
                Token("mi", function.Name, sb, style with { Upright = true });
                sb.Append("<mspace width=\"0.1667em\"/>");
                break;
            case MathSpace space:
                sb.Append("<mspace width=\"").Append((space.Width * 0.5).ToString("0.###", CultureInfo.InvariantCulture)).Append("em\"/>");
                break;
            case MathAlign:
                break;
            case MathFraction fraction:
                sb.Append(fraction.Bar ? "<mfrac>" : "<mfrac linethickness=\"0\">");
                Group(fraction.Numerator, sb, style);
                Group(fraction.Denominator, sb, style);
                sb.Append("</mfrac>");
                break;
            case MathRoot root:
                if (root.Index is null)
                {
                    sb.Append("<msqrt>");
                    Node(root.Body, sb, style);
                    sb.Append("</msqrt>");
                }
                else
                {
                    sb.Append("<mroot>");
                    Group(root.Body, sb, style);
                    Group(root.Index, sb, style);
                    sb.Append("</mroot>");
                }
                break;
            case MathScripts scripts:
                Scripts(scripts.Base, scripts.Sub, scripts.Sup, sb, style, under: false);
                break;
            case MathStack stack:
                Scripts(stack.Base, stack.Under, stack.Over, sb, style, under: true);
                break;
            case MathBigOperator big:
                Scripts(new MathSymbol(big.Glyph), big.Lower, big.Upper, sb, style, under: !big.Integral, large: true);
                break;
            case MathLimit limit:
                if (limit.Lower is null)
                {
                    sb.Append("<mo form=\"prefix\">lim</mo>");
                }
                else
                {
                    sb.Append("<munder><mo form=\"prefix\" movablelimits=\"true\">lim</mo>");
                    Group(limit.Lower, sb, style);
                    sb.Append("</munder>");
                }
                break;
            case MathMatrix matrix:
                {
                    var (open, close) = matrix.Command switch
                    {
                        "pmatrix" => ("(", ")"),
                        "bmatrix" => ("[", "]"),
                        "dmatrix" or "vmatrix" => ("|", "|"),
                        _ => ("", ""),
                    };
                    sb.Append("<mrow>");
                    Fence(open, sb);
                    Table(matrix.Rows, sb, style, _ => "center");
                    Fence(close, sb);
                    sb.Append("</mrow>");
                    break;
                }
            case MathCases cases:
                sb.Append("<mrow>");
                Fence("{", sb);
                Table(cases.Rows, sb, style, _ => "left");
                sb.Append("</mrow>");
                break;
            case MathPile pile:
                {
                    var rows = pile.Rows.Select(Cells).ToList();
                    if (rows.Any(r => r.Count > 1))
                        Table(rows, sb, style, column => column % 2 == 0 ? "right" : "left"); // aligned: =, ≤ … at the marks
                    else
                        Table(rows, sb, style, _ => pile.Align switch { "lpile" => "left", "rpile" => "right", _ => "center" });
                    break;
                }
            case MathBrackets brackets:
                sb.Append("<mrow>");
                Fence(brackets.Left, sb);
                Node(brackets.Body, sb, style);
                Fence(brackets.Right, sb);
                sb.Append("</mrow>");
                break;
            case MathAccent accent:
                Accent(accent, sb, style);
                break;
            case MathStyled styled:
                Node(styled.Body, sb, styled.Command switch
                {
                    "rm" => style with { Upright = true },
                    "it" => style with { Upright = false },
                    "bold" => style with { Bold = true },
                    _ => style,
                });
                break;
        }
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

    private static void Table(IReadOnlyList<IReadOnlyList<MathNode>> rows, StringBuilder sb, Style style, Func<int, string> align)
    {
        sb.Append("<mtable>");
        foreach (var row in rows)
        {
            sb.Append("<mtr>");
            for (var c = 0; c < row.Count; c++)
            {
                var side = align(c);
                // columnalign for MathML 3 readers, text-align for MathML Core (browsers).
                sb.Append("<mtd columnalign=\"").Append(side).Append("\" style=\"text-align:").Append(side);
                if (side is "right" or "left" && row.Count > 1)
                    sb.Append(side == "right" ? ";padding-right:0" : ";padding-left:0");
                sb.Append("\">");
                Node(row[c], sb, style);
                sb.Append("</mtd>");
            }
            sb.Append("</mtr>");
        }
        sb.Append("</mtable>");
    }

    private static void Scripts(MathNode baseNode, MathNode? sub, MathNode? sup, StringBuilder sb, Style style, bool under, bool large = false)
    {
        void Base()
        {
            if (large && baseNode is MathSymbol symbol)
                sb.Append(under ? "<mo largeop=\"true\" movablelimits=\"true\">" : "<mo largeop=\"true\">").Append(OmmlWriter.Escape(symbol.Text)).Append("</mo>");
            else
                Group(baseNode, sb, style);
        }

        var tag = (sub, sup, under) switch
        {
            (null, null, _) => null,
            (_, null, false) => "msub",
            (null, _, false) => "msup",
            (_, _, false) => "msubsup",
            (_, null, true) => "munder",
            (null, _, true) => "mover",
            _ => "munderover",
        };
        if (tag is null)
        {
            Base();
            return;
        }
        sb.Append('<').Append(tag).Append('>');
        Base();
        if (sub is not null)
            Group(sub, sb, style);
        if (sup is not null)
            Group(sup, sb, style);
        sb.Append("</").Append(tag).Append('>');
    }

    private static void Accent(MathAccent accent, StringBuilder sb, Style style)
    {
        var (mark, below) = accent.Command switch
        {
            "hat" => ("^", false),
            "check" => ("ˇ", false),
            "tilde" => ("~", false),
            "acute" => ("´", false),
            "grave" => ("`", false),
            "dot" => ("˙", false),
            "ddot" => ("¨", false),
            "breve" => ("˘", false),
            "vec" => ("→", false),
            "dyad" => ("↔", false),
            "arch" => ("⏜", false),
            "bar" or "OVERLINE" or "overline" => ("‾", false),
            "under" or "UNDERLINE" or "underline" => ("_", true),
            _ => ((string?)null, false),
        };
        if (mark is null)
        {
            // \not: a crossed-out character where there is one, else the content.
            if (accent.Command == "not" && accent.Body is MathSymbol or MathCommand)
            {
                var glyph = accent.Body is MathSymbol symbol ? symbol.Text : ((MathCommand)accent.Body).Glyph;
                Operator(glyph + "̸", sb, style);
                return;
            }
            Node(accent.Body, sb, style);
            return;
        }
        // A line or arrow over several characters stretches across them.
        var stretch = mark is "‾" or "_" or "→" or "↔" or "⏜" ? " stretchy=\"true\"" : " stretchy=\"false\"";
        sb.Append(below ? "<munder accentunder=\"true\">" : "<mover accent=\"true\">");
        Group(accent.Body, sb, style);
        sb.Append("<mo").Append(stretch).Append('>').Append(OmmlWriter.Escape(mark)).Append("</mo>");
        sb.Append(below ? "</munder>" : "</mover>");
    }

    /// <summary>A stretched bracket ("" for none).</summary>
    private static void Fence(string bracket, StringBuilder sb)
    {
        if (bracket.Length > 0)
            sb.Append("<mo fence=\"true\" stretchy=\"true\">").Append(OmmlWriter.Escape(bracket)).Append("</mo>");
    }

    private static void Operator(string text, StringBuilder sb, Style style)
    {
        // Brackets typed as characters keep their size, as TeX keeps them.
        var fixedSize = text is "(" or ")" or "[" or "]" or "{" or "}" or "|" or "‖" or "⟨" or "⟩" or "⌊" or "⌋" or "⌈" or "⌉";
        // (and no room around them: | x | is an absolute value, not two relations)
        sb.Append(fixedSize ? "<mo stretchy=\"false\" lspace=\"0\" rspace=\"0\"" : "<mo");
        if (style.Bold)
            sb.Append(" style=\"font-weight:bold\"");
        sb.Append('>').Append(OmmlWriter.Escape(text)).Append("</mo>");
    }

    /// <summary>Letters (each its own &lt;mi&gt; when italic), CJK and other text.</summary>
    private static void Text(string text, StringBuilder sb, Style style)
    {
        if (!style.Upright && text.Length > 1 && text.All(char.IsAsciiLetter))
        {
            foreach (var letter in text)
                Token("mi", letter.ToString(), sb, style);
            return;
        }
        // Greek capitals are upright in TeX and Word.
        var upright = style.Upright || (text.Length == 1 && text[0] is >= 'Α' and <= 'Ω');
        Token("mi", text, sb, style with { Upright = upright });
    }

    private static void Token(string tag, string text, StringBuilder sb, Style style)
    {
        sb.Append('<').Append(tag);
        if (tag == "mi" && style.Upright && text.Length == 1)
            sb.Append(" mathvariant=\"").Append(style.Bold ? "bold" : "normal").Append('"');
        else if (tag == "mi" && style.Bold)
            sb.Append(" mathvariant=\"").Append(style.Upright || text.Length > 1 ? "bold" : "bold-italic").Append('"');
        if (style.Bold)
            sb.Append(" style=\"font-weight:bold\"");
        sb.Append('>').Append(OmmlWriter.Escape(text)).Append("</").Append(tag).Append('>');
    }

    private static bool IsGreek(string glyph) => glyph.Length == 1 && glyph[0] is >= 'Ͱ' and <= 'Ͽ';
}
