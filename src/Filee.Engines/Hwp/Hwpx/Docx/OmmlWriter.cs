// The equation tree (MathNode) → Word math (Office Math, m:oMath, ECMA-376 part 1 §22.1), the other way from OmmlReader:
// the DOCX writer stores equations as Word's own, editable in Word's equation editor, instead of their linear text.
//
// Letters are italic (Word's default for math), function names, numbers and upright (rm) text plain (m:sty="p"), bold
// b / bi, quoted text normal text (m:nor). Fractions, roots, scripts, large operators (m:nary, its operand the items
// after it up to the next relation, + or −), lim and function names with their argument (m:func), brackets (m:d),
// matrices (m:m in m:d), cases (an equation array in a left brace), piles and aligned lines (m:eqArr, the cells joined
// by & as Word joins them), accents (m:acc), over- and underlines (m:bar) and crossed-out content (m:borderBox).

using System.Globalization;
using System.Text;

namespace Filee.Engines.Hwp.Hwpx.Docx;

internal static class OmmlWriter
{
    /// <summary>The math font of every run, as Word writes it.</summary>
    private const string MathFont = "<w:rFonts w:ascii=\"Cambria Math\" w:hAnsi=\"Cambria Math\"/>";

    /// <summary>The math font of a DrawingML run, as PowerPoint writes it.</summary>
    private const string DrawingMathFont = "<a:latin typeface=\"Cambria Math\" panose=\"02040503050406030204\" pitchFamily=\"18\" charset=\"0\"/>";

    private static readonly HashSet<string> Relations =
    [
        "=", "<", ">", "≤", "≥", "≠", "≈", "≡", "≅", "∼", "≃", "∝", "≪", "≫", "∈", "∉", "∋", "⊂", "⊃", "⊆", "⊇", "→", "←",
        "↔", "⇒", "⇐", "⇔", "↦", "⟶", "⟹", "⟺", "⩽", "⩾", ":",
    ];

    /// <summary>How the text of a run is styled.</summary>
    /// <param name="Drawing">
    /// For DrawingML text (PowerPoint's a14:m): the attributes of each run's a:rPr (size) and its content before the
    /// font (fill); null for Word's w:rPr.
    /// </param>
    private readonly record struct Style(bool Upright, bool Bold, string RunProperties, (string Attributes, string Fill)? Drawing = null);

    /// <summary>
    /// One equation: an m:oMath, or for a display equation an m:oMathPara (a paragraph of its own, centred).
    /// <paramref name="runProperties"/> is the content of each run's w:rPr after its font (size, colour);
    /// <paramref name="align"/> places a display equation (left, right, else centred).
    /// </summary>
    public static string Write(MathNode node, bool display, string runProperties = "", HAlign? align = null) =>
        Write(node, display, new Style(false, false, runProperties), align);

    /// <summary>
    /// One equation for DrawingML text, as PowerPoint keeps it inside a14:m: the same Office Math with a:rPr run
    /// properties (<paramref name="attributes"/> such as sz="2800", <paramref name="fill"/> such as an a:solidFill)
    /// and the math font as a:latin.
    /// </summary>
    public static string WriteDrawing(MathNode node, bool display, string attributes = "", string fill = "", HAlign? align = null) =>
        Write(node, display, new Style(false, false, "", (attributes, fill)), align);

    private static string Write(MathNode node, bool display, Style style, HAlign? align)
    {
        var sb = new StringBuilder();
        if (display)
        {
            var jc = align switch
            {
                HAlign.Left => "left",
                HAlign.Right => "right",
                _ => "center",
            };
            sb.Append("<m:oMathPara><m:oMathParaPr><m:jc m:val=\"").Append(jc).Append("\"/></m:oMathParaPr>");
        }
        sb.Append("<m:oMath>");
        Node(node, sb, style);
        sb.Append("</m:oMath>");
        if (display)
            sb.Append("</m:oMathPara>");
        return sb.ToString();
    }

    private static void Node(MathNode node, StringBuilder sb, Style style)
    {
        switch (node)
        {
            case MathRow row:
                Row(row.Items, sb, style);
                break;
            case MathText text:
                Run(text.Text, sb, style, italic: !style.Upright && text.Text.Any(char.IsLetter));
                break;
            case MathNumber number:
                Run(number.Digits, sb, style, italic: false);
                break;
            case MathSymbol symbol:
                Run(symbol.Text == "-" ? "−" : symbol.Text, sb, style, italic: false);
                break;
            case MathCommand command:
                Run(command.Glyph, sb, style, italic: !style.Upright && command.Glyph.Any(char.IsLetter));
                break;
            case MathQuoted quoted:
                Run(quoted.Text, sb, style, italic: false, normal: true);
                break;
            case MathFunction function:
                Run(function.Name, sb, style, italic: false);
                break;
            case MathSpace space:
                Run(Space(space.Width), sb, style, italic: false);
                break;
            case MathAlign:
                Run("&", sb, style, italic: false);
                break;
            case MathFraction fraction:
                sb.Append("<m:f>");
                if (!fraction.Bar)
                    sb.Append("<m:fPr><m:type m:val=\"noBar\"/></m:fPr>");
                Argument("num", fraction.Numerator, sb, style);
                Argument("den", fraction.Denominator, sb, style);
                sb.Append("</m:f>");
                break;
            case MathRoot root:
                sb.Append("<m:rad>");
                if (root.Index is null)
                    sb.Append("<m:radPr><m:degHide m:val=\"1\"/></m:radPr><m:deg/>");
                else
                    Argument("deg", root.Index, sb, style);
                Argument("e", root.Body, sb, style);
                sb.Append("</m:rad>");
                break;
            case MathScripts scripts:
                {
                    var kind = (scripts.Sub, scripts.Sup) switch
                    {
                        (null, null) => null,
                        (null, _) => "sSup",
                        (_, null) => "sSub",
                        _ => "sSubSup",
                    };
                    if (kind is null)
                    {
                        Node(scripts.Base, sb, style);
                        break;
                    }
                    sb.Append("<m:").Append(kind).Append('>');
                    Argument("e", scripts.Base, sb, style);
                    if (scripts.Sub is not null)
                        Argument("sub", scripts.Sub, sb, style);
                    if (scripts.Sup is not null)
                        Argument("sup", scripts.Sup, sb, style);
                    sb.Append("</m:").Append(kind).Append('>');
                    break;
                }
            case MathStack stack:
                // Word's lower and upper limits: m:limLow inside m:limUpp when both are there.
                if (stack.Over is not null)
                {
                    sb.Append("<m:limUpp>");
                    Argument("e", stack with { Over = null }, sb, style);
                    Argument("lim", stack.Over, sb, style);
                    sb.Append("</m:limUpp>");
                }
                else if (stack.Under is not null)
                {
                    sb.Append("<m:limLow>");
                    Argument("e", stack.Base, sb, style);
                    Argument("lim", stack.Under, sb, style);
                    sb.Append("</m:limLow>");
                }
                else
                {
                    Node(stack.Base, sb, style);
                }
                break;
            case MathBigOperator big:
                Nary(big, [], sb, style);
                break;
            case MathLimit limit:
                Limit(limit, [], sb, style);
                break;
            case MathMatrix matrix:
                {
                    var (open, close) = matrix.Command switch
                    {
                        "pmatrix" => ("(", ")"),
                        "bmatrix" => ("[", "]"),
                        "dmatrix" or "vmatrix" => ("|", "|"),
                        _ => ((string?)null, (string?)null),
                    };
                    if (open is null)
                    {
                        Matrix(matrix.Rows, sb, style);
                        break;
                    }
                    DelimiterStart(open, close!, sb);
                    Matrix(matrix.Rows, sb, style);
                    sb.Append("</m:e></m:d>");
                    break;
                }
            case MathCases cases:
                DelimiterStart("{", "", sb);
                sb.Append("<m:eqArr>");
                foreach (var row in cases.Rows)
                {
                    sb.Append("<m:e>");
                    for (var c = 0; c < row.Count; c++)
                    {
                        if (c > 0)
                            Run("&", sb, style, italic: false);
                        Node(row[c], sb, style);
                    }
                    sb.Append("</m:e>");
                }
                sb.Append("</m:eqArr></m:e></m:d>");
                break;
            case MathPile pile:
                sb.Append("<m:eqArr>");
                foreach (var row in pile.Rows)
                    Argument("e", row, sb, style);
                sb.Append("</m:eqArr>");
                break;
            case MathBrackets brackets:
                DelimiterStart(brackets.Left, brackets.Right, sb);
                Node(brackets.Body, sb, style);
                sb.Append("</m:e></m:d>");
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

    /// <summary>Items side by side; a large operator, lim or a function name takes the items after it as its operand.</summary>
    private static void Row(IReadOnlyList<MathNode> items, StringBuilder sb, Style style)
    {
        for (var i = 0; i < items.Count; i++)
        {
            switch (items[i])
            {
                case MathBigOperator big:
                    {
                        var operand = Operand(items, i + 1);
                        Nary(big, operand, sb, style);
                        i += operand.Count;
                        break;
                    }
                case MathLimit limit:
                    {
                        var operand = Operand(items, i + 1);
                        Limit(limit, operand, sb, style);
                        i += operand.Count;
                        break;
                    }
                case MathFunction function when i + 1 < items.Count && !IsBreak(items[i + 1]):
                    {
                        // sin x, log (x+1): the function's argument is the next item, or a bracketed run.
                        var operand = items[i + 1] is MathSymbol { Text: "(" } ? Bracketed(items, i + 1) : [items[i + 1]];
                        sb.Append("<m:func><m:fName>");
                        Run(function.Name, sb, style, italic: false);
                        sb.Append("</m:fName><m:e>");
                        Row(operand, sb, style);
                        sb.Append("</m:e></m:func>");
                        i += operand.Count;
                        break;
                    }
                default:
                    Node(items[i], sb, style);
                    break;
            }
        }
    }

    /// <summary>A relation, + or − (or an alignment mark) ends the operand of a sum or an integral.</summary>
    private static bool IsBreak(MathNode node) => node switch
    {
        MathSymbol symbol => Relations.Contains(symbol.Text) || symbol.Text is "+" or "-" or "," or ";",
        MathCommand command => Relations.Contains(command.Glyph) || command.Glyph is "±" or "∓",
        MathAlign => true,
        _ => false,
    };

    private static List<MathNode> Operand(IReadOnlyList<MathNode> items, int start)
    {
        var operand = new List<MathNode>();
        for (var i = start; i < items.Count && !IsBreak(items[i]); i++)
        {
            operand.Add(items[i]);
            if (items[i] is MathBigOperator or MathLimit)
            {
                // A sum of a sum: the inner one takes the rest.
                operand.AddRange(Operand(items, i + 1));
                break;
            }
        }
        return operand;
    }

    /// <summary>The items from a "(" up to its matching ")" (or the end).</summary>
    private static List<MathNode> Bracketed(IReadOnlyList<MathNode> items, int start)
    {
        var operand = new List<MathNode>();
        var level = 0;
        for (var i = start; i < items.Count; i++)
        {
            operand.Add(items[i]);
            if (items[i] is MathSymbol { Text: "(" })
                level++;
            else if (items[i] is MathSymbol { Text: ")" } && --level == 0)
                break;
        }
        return operand;
    }

    private static void Nary(MathBigOperator big, List<MathNode> operand, StringBuilder sb, Style style)
    {
        sb.Append("<m:nary><m:naryPr><m:chr m:val=\"").Append(Escape(big.Glyph)).Append("\"/>");
        sb.Append("<m:limLoc m:val=\"").Append(big.Integral ? "subSup" : "undOvr").Append("\"/>");
        if (big.Lower is null)
            sb.Append("<m:subHide m:val=\"1\"/>");
        if (big.Upper is null)
            sb.Append("<m:supHide m:val=\"1\"/>");
        sb.Append("</m:naryPr>");
        Argument("sub", big.Lower ?? MathRow.Empty, sb, style);
        Argument("sup", big.Upper ?? MathRow.Empty, sb, style);
        sb.Append("<m:e>");
        Row(operand, sb, style);
        sb.Append("</m:e></m:nary>");
    }

    private static void Limit(MathLimit limit, List<MathNode> operand, StringBuilder sb, Style style)
    {
        sb.Append("<m:func><m:fName>");
        if (limit.Lower is null)
        {
            Run("lim", sb, style, italic: false);
        }
        else
        {
            sb.Append("<m:limLow><m:e>");
            Run("lim", sb, style, italic: false);
            sb.Append("</m:e>");
            Argument("lim", limit.Lower, sb, style);
            sb.Append("</m:limLow>");
        }
        sb.Append("</m:fName><m:e>");
        Row(operand, sb, style);
        sb.Append("</m:e></m:func>");
    }

    private static void Matrix(IReadOnlyList<IReadOnlyList<MathNode>> rows, StringBuilder sb, Style style)
    {
        // Word wants every row as long as the matrix is wide.
        var columns = Math.Max(1, rows.Count == 0 ? 1 : rows.Max(r => r.Count));
        sb.Append("<m:m><m:mPr><m:mcs><m:mc><m:mcPr><m:count m:val=\"").Append(columns.ToString(CultureInfo.InvariantCulture))
          .Append("\"/><m:mcJc m:val=\"center\"/></m:mcPr></m:mc></m:mcs></m:mPr>");
        foreach (var row in rows)
        {
            sb.Append("<m:mr>");
            for (var c = 0; c < columns; c++)
                Argument("e", c < row.Count ? row[c] : MathRow.Empty, sb, style);
            sb.Append("</m:mr>");
        }
        sb.Append("</m:m>");
    }

    /// <summary>The start of m:d up to its argument's &lt;m:e&gt;; "" for a side without a bracket.</summary>
    private static void DelimiterStart(string open, string close, StringBuilder sb) =>
        sb.Append("<m:d><m:dPr><m:begChr m:val=\"").Append(Escape(open)).Append("\"/><m:endChr m:val=\"").Append(Escape(close))
          .Append("\"/></m:dPr><m:e>");

    private static void Accent(MathAccent accent, StringBuilder sb, Style style)
    {
        switch (accent.Command)
        {
            case "bar" or "OVERLINE" or "overline" or "under" or "UNDERLINE" or "underline":
                {
                    var top = accent.Command is "bar" or "OVERLINE" or "overline";
                    sb.Append("<m:bar><m:barPr><m:pos m:val=\"").Append(top ? "top" : "bot").Append("\"/></m:barPr>");
                    Argument("e", accent.Body, sb, style);
                    sb.Append("</m:bar>");
                    return;
                }
            case "not":
                sb.Append("<m:borderBox><m:borderBoxPr><m:hideTop m:val=\"1\"/><m:hideBot m:val=\"1\"/><m:hideLeft m:val=\"1\"/>")
                  .Append("<m:hideRight m:val=\"1\"/><m:strikeBLTR m:val=\"1\"/></m:borderBoxPr>");
                Argument("e", accent.Body, sb, style);
                sb.Append("</m:borderBox>");
                return;
        }
        var mark = accent.Command switch
        {
            "hat" => "̂",
            "check" => "̌",
            "tilde" => "̃",
            "acute" => "́",
            "grave" => "̀",
            "dot" => "̇",
            "ddot" => "̈",
            "breve" => "̆",
            "vec" => "⃗",
            "dyad" => "⃡",
            "arch" => "̑",
            _ => null,
        };
        if (mark is null)
        {
            Node(accent.Body, sb, style);
            return;
        }
        sb.Append("<m:acc><m:accPr><m:chr m:val=\"").Append(mark).Append("\"/></m:accPr>");
        Argument("e", accent.Body, sb, style);
        sb.Append("</m:acc>");
    }

    private static void Argument(string name, MathNode node, StringBuilder sb, Style style)
    {
        if (node is MathRow { Items.Count: 0 })
        {
            sb.Append("<m:").Append(name).Append("/>");
            return;
        }
        sb.Append("<m:").Append(name).Append('>');
        Node(node, sb, style);
        sb.Append("</m:").Append(name).Append('>');
    }

    /// <summary>
    /// A math run: letters italic unless <paramref name="italic"/> is false (plain), bold where styled; normal text
    /// (m:nor) for quoted text.
    /// </summary>
    private static void Run(string text, StringBuilder sb, Style style, bool italic, bool normal = false)
    {
        var clean = Escape(text);
        if (clean.Length == 0)
            return;
        sb.Append("<m:r>");
        var sty = (italic, style.Bold) switch
        {
            (true, false) => null, // Word's default for math
            (true, true) => "bi",
            (false, true) => "b",
            _ => "p",
        };
        if (normal)
            sb.Append(style.Bold ? "<m:rPr><m:nor/><m:sty m:val=\"b\"/></m:rPr>" : "<m:rPr><m:nor/></m:rPr>");
        else if (sty is not null)
            sb.Append("<m:rPr><m:sty m:val=\"").Append(sty).Append("\"/></m:rPr>");
        if (style.Drawing is var (attributes, fill))
            sb.Append("<a:rPr lang=\"en-US\"").Append(style.Bold ? " b=\"1\"" : " b=\"0\"").Append(italic ? " i=\"1\"" : " i=\"0\"").Append(attributes).Append('>')
              .Append(fill).Append(DrawingMathFont).Append("</a:rPr>");
        else
            sb.Append("<w:rPr>").Append(MathFont).Append(style.RunProperties).Append("</w:rPr>");
        sb.Append(text.Length > 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1])) ? "<m:t xml:space=\"preserve\">" : "<m:t>");
        sb.Append(clean).Append("</m:t></m:r>");
    }

    /// <summary>
    /// A space of <paramref name="width"/> (1 is half an em, see MathSpace) as Unicode spaces Word draws in math: thin
    /// spaces below a quarter of an em, else quarter-em spaces (the ones OmmlReader reads as spacing, not as text).
    /// </summary>
    private static string Space(double width)
    {
        var em = width * 0.5;
        return em < 0.22 ? " " : new string(' ', Math.Clamp((int)Math.Round(em / 0.25), 1, 64));
    }

    /// <summary>Text for XML, without the characters XML can't hold.</summary>
    internal static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                sb.Append(c).Append(text[++i]);
                continue;
            }
            if (char.IsSurrogate(c) || (c < 0x20 && c is not ('\t' or '\n' or '\r')) || c is '￾' or '￿')
                continue;
            sb.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                _ => c.ToString(),
            });
        }
        return sb.ToString();
    }
}
