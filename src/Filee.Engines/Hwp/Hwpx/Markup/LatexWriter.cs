// HDocument → LaTeX: a complete article that pdfLaTeX, XeLaTeX and LuaLaTeX take with the packages of a plain TeX
// installation (iftex, fontenc / inputenc or fontspec, amsmath, amssymb, graphicx, longtable, array, multirow, ulem,
// hyperref; only those the document needs). Text outside the Latin scripts needs XeLaTeX or LuaLaTeX and a font that
// has it; CJK text loads xeCJK or luatexja there.
//
// \section … \subparagraph headings (unnumbered, as the source numbers its own), paragraphs with \\ at line breaks,
// quote environments for quotes, itemize / enumerate nested up to LaTeX's four levels (numbers written as the items'
// labels), verbatim code (\texttt lines where verbatim can't go: in table cells, or code holding \end{verbatim}),
// longtable tables (tabular inside a cell) with p columns, \multicolumn and \multirow for merged cells and \cline
// rules that leave merged cells open, header rows repeated on every page, \href links, \includegraphics pictures
// (files in "<name>_files" next to the result, no wider than the text), \footnote notes, rules, and equations as
// $…$ / \[…\] (their TeX source, or TeX written from the equation tree). Every character LaTeX treats specially is
// escaped: \ { } $ & # _ % ~ ^, the brackets after \\ and \item, < > | " ` and the ligatures -- '' ?` !`.

using System.Globalization;
using System.Text;

namespace Filee.Engines.Hwp.Hwpx.Markup;

/// <summary>Writes the document model as LaTeX.</summary>
internal sealed class LatexWriter(Func<string, string?> pictures) : MarkupTextWriter(pictures)
{
    /// <summary>LaTeX nests lists four levels deep.</summary>
    private const int MaxListLevel = 3;

    /// <summary>Environments that are a display equation by themselves (a source written $$\begin{align}…$$).</summary>
    private static readonly string[] DisplayEnvironments = ["align", "equation", "gather", "multline", "eqnarray", "flalign", "alignat"];

    private bool _pictures;
    private bool _tables;
    private bool _lines;
    private bool _cjk;

    /// <summary>Table cells around the text being written: tables there are tabular, code is \texttt.</summary>
    private int _cell;

    public static string Write(HDocument document, Func<string, string?> pictures) => new LatexWriter(pictures).Write(document);

    protected override void Start(StringBuilder sb, string? title)
    {
        sb.Append("\\begin{document}\n\n");
        if (title is null)
            return;
        sb.Append("\\title{").Append(Escape(title.Replace('\n', ' '))).Append("}\n");
        var authors = Source?.Authors.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => Escape(a.Trim())).ToList() ?? [];
        sb.Append("\\author{").AppendJoin(" \\and ", authors).Append("}\n");
        sb.Append("\\date{").Append(Escape(Source?.Date?.Trim() ?? "")).Append("}\n\\maketitle\n\n");
    }

    protected override void End(StringBuilder sb)
    {
        sb.Append("\\end{document}\n");
        // The preamble loads what the body turned out to use.
        sb.Insert(0, Preamble());
    }

    private string Preamble()
    {
        var sb = new StringBuilder();
        sb.Append("\\documentclass{article}\n");
        sb.Append("\\usepackage{iftex}\n");
        sb.Append("\\ifPDFTeX\n  \\usepackage[T1]{fontenc}\n  \\usepackage[utf8]{inputenc}\n\\else\n  \\usepackage{fontspec}\n\\fi\n");
        if (_cjk)
            sb.Append("\\ifXeTeX\n  \\usepackage{xeCJK}\n\\fi\n\\ifLuaTeX\n  \\usepackage{luatexja}\n\\fi\n");
        sb.Append("\\usepackage{amsmath,amssymb}\n");
        if (_pictures)
        {
            // Pictures at their own size, but no wider than the text.
            sb.Append("\\usepackage{graphicx}\n");
            sb.Append("\\makeatletter\n\\def\\maxwidth{\\ifdim\\Gin@nat@width>\\linewidth\\linewidth\\else\\Gin@nat@width\\fi}\n\\makeatother\n");
            sb.Append("\\setkeys{Gin}{width=\\maxwidth,keepaspectratio}\n");
        }
        if (_tables)
            sb.Append("\\usepackage{longtable,array,multirow}\n");
        if (_lines)
            sb.Append("\\usepackage[normalem]{ulem}\n");
        sb.Append("\\usepackage{hyperref}\n");
        sb.Append("\\setcounter{secnumdepth}{-2}\n");
        sb.Append("\\setlength{\\parindent}{0pt}\n\\setlength{\\parskip}{6pt plus 2pt minus 1pt}\n\n");
        return sb.ToString();
    }

    protected override void Heading(StringBuilder sb, int level, string text)
    {
        if (text.Length == 0)
            return;
        if (_cell > 0)
        {
            sb.Append("\\textbf{").Append(text).Append("}\n\n");
            return;
        }
        var command = level switch
        {
            1 => "section",
            2 => "subsection",
            3 => "subsubsection",
            4 => "paragraph",
            _ => "subparagraph",
        };
        sb.Append('\\').Append(command).Append('{').Append(text).Append("}\n\n");
    }

    protected override void Paragraph(StringBuilder sb, string text, int quote) =>
        Quoted(sb, quote, () => sb.Append(Lines(text)).Append("\n\n"));

    private static void Quoted(StringBuilder sb, int quote, Action body)
    {
        for (var q = 0; q < quote; q++)
            sb.Append("\\begin{quote}\n");
        body();
        for (var q = 0; q < quote; q++)
            sb.Append("\\end{quote}\n");
        if (quote > 0)
            sb.Append('\n');
    }

    /// <summary>A paragraph's lines joined by \\ (an empty line holds an empty box, as \\ can't end nothing).</summary>
    private static string Lines(string text) =>
        string.Join("\\\\\n", text.Trim('\n').Split('\n').Select(line => line.Trim().Length == 0 ? "\\mbox{}" : line));

    protected override void List(StringBuilder sb, IReadOnlyList<MarkupListItem> items, int quote)
    {
        // The environment open at each level, from the outside in.
        var open = new List<string>();
        foreach (var item in items)
        {
            if (item.Continuation && open.Count > 0)
            {
                sb.Append('\n').Append(Lines(item.Text)).Append('\n');
                continue;
            }
            var level = Math.Min(item.Level, MaxListLevel);
            var environment = item.Ordered ? "enumerate" : "itemize";
            while (open.Count > level + 1 || (open.Count == level + 1 && open[^1] != environment))
            {
                sb.Append("\\end{").Append(open[^1]).Append("}\n");
                open.RemoveAt(open.Count - 1);
            }
            while (open.Count < level + 1)
            {
                var opening = open.Count == level ? environment : "itemize";
                sb.Append("\\begin{").Append(opening).Append("}\n");
                open.Add(opening);
                // A level skipped over still needs an item to hold the next one.
                if (open.Count <= level)
                    sb.Append("\\item[]\n");
            }
            sb.Append("\\item");
            if (item.Continuation)
                sb.Append("[]");
            else if (item.Ordered)
                sb.Append(CultureInfo.InvariantCulture, $"[{item.Number}.]");
            sb.Append(' ').Append(Lines(item.Text)).Append('\n');
        }
        for (var i = open.Count - 1; i >= 0; i--)
            sb.Append("\\end{").Append(open[i]).Append("}\n");
        sb.Append('\n');
    }

    protected override void Code(StringBuilder sb, IReadOnlyList<string> lines, int quote)
    {
        var expanded = lines.Select(l => l.Replace("\t", "    ", StringComparison.Ordinal)).ToList();
        Quoted(sb, quote, () =>
        {
            if (_cell == 0 && !expanded.Any(l => l.Contains("\\end{verbatim}", StringComparison.Ordinal)))
            {
                sb.Append("\\begin{verbatim}\n");
                foreach (var line in expanded)
                    sb.Append(line).Append('\n');
                sb.Append("\\end{verbatim}\n\n");
                return;
            }
            // Spaces kept, each line its own.
            sb.Append("{\\ttfamily ").AppendJoin("\\\\\n", expanded.Select(l => l.Length == 0 ? "\\mbox{}" : Escape(l).Replace(" ", "\\ ", StringComparison.Ordinal)))
              .Append("\\par}\n\n");
        });
    }

    protected override void Table(StringBuilder sb, MarkupTable table)
    {
        _tables = true;
        var columns = table.Columns;
        var owners = MarkupTableOwners.Owners(table);
        var nested = _cell > 0;
        var environment = nested ? "tabular" : "longtable";
        // Columns share the line; a merged cell is as wide as its columns with the space between them.
        string Width(int span) =>
            $"\\dimexpr {((double)span / columns).ToString("0.####", CultureInfo.InvariantCulture)}\\linewidth-2\\tabcolsep-\\arrayrulewidth\\relax";

        sb.Append("\\begin{").Append(environment).Append("}{|");
        for (var c = 0; c < columns; c++)
            sb.Append("p{").Append(Width(1)).Append("}|");
        sb.Append("}\n\\hline\n");
        var header = 0;
        while (header < table.Rows.Count && table.HeaderRows[header])
            header++;

        for (var r = 0; r < table.Rows.Count; r++)
        {
            var entries = new List<string>();
            for (var c = 0; c < columns;)
            {
                var (ownerRow, ownerColumn) = owners[r, c];
                var owner = ownerRow < 0 ? null : table.Rows[ownerRow][ownerColumn];
                var span = owner?.ColSpan ?? 1;
                var content = owner is not null && ownerRow == r ? Content(owner) : "";
                if (owner is not null && ownerRow == r && owner.RowSpan > 1 && owner.Simple)
                    content = $"\\multirow{{{owner.RowSpan}}}{{=}}{{{content}}}";
                if (span > 1)
                    content = $"\\multicolumn{{{span}}}{{{(c == 0 ? "|" : "")}p{{{Width(span)}}}|}}{{{content}}}";
                entries.Add(content);
                c += span;
            }
            sb.AppendJoin(" & ", entries).Append(" \\\\\n");
            sb.Append(Rules(table, owners, r)).Append('\n');
            if (!nested && r + 1 == header && header < table.Rows.Count)
                sb.Append("\\endhead\n");
        }
        sb.Append("\\end{").Append(environment).Append("}\n");
        if (!nested)
            sb.Append('\n');
    }

    /// <summary>\hline under a row, or \cline over the columns whose cell ends there (merged cells stay open).</summary>
    private static string Rules(MarkupTable table, (int Row, int Column)[,] owners, int row)
    {
        bool Ends(int c)
        {
            var (ownerRow, ownerColumn) = owners[row, c];
            return ownerRow < 0 || ownerRow + table.Rows[ownerRow][ownerColumn]!.RowSpan - 1 <= row;
        }

        var ends = Enumerable.Range(0, table.Columns).Select(Ends).ToArray();
        if (ends.All(e => e))
            return "\\hline";
        var sb = new StringBuilder();
        for (var c = 0; c < ends.Length; c++)
        {
            if (!ends[c])
                continue;
            var start = c;
            while (c + 1 < ends.Length && ends[c + 1])
                c++;
            sb.Append(CultureInfo.InvariantCulture, $"\\cline{{{start + 1}-{c + 1}}}");
        }
        return sb.ToString();
    }

    private string Content(MarkupCell cell)
    {
        if (cell.Simple)
            return string.Join("\\newline ", cell.Text.Split('\n').Where(l => l.Length > 0));
        var blocks = new StringBuilder();
        _cell++;
        try
        {
            Blocks(cell.Source.Blocks, blocks);
        }
        finally
        {
            _cell--;
        }
        return blocks.ToString().Trim('\n');
    }

    protected override void Rule(StringBuilder sb) => sb.Append("\\noindent\\rule{\\linewidth}{0.4pt}\n\n");

    protected override string Run(string text, HCharFormat format)
    {
        if (format.Shade == HtmlReader.CodeShade)
            return Around(text, core => $"\\texttt{{{Escape(core)}}}");
        return Around(Escape(text), core =>
        {
            if (format.Superscript is true)
                core = $"\\textsuperscript{{{core}}}";
            else if (format.Subscript is true)
                core = $"\\textsubscript{{{core}}}";
            if (format.Underline is true || format.Strike is true)
                _lines = true;
            if (format.Underline is true)
                core = $"\\protect\\uline{{{core}}}";
            if (format.Strike is true)
                core = $"\\protect\\sout{{{core}}}";
            if (format.Italic is true)
                core = $"\\emph{{{core}}}";
            if (format.Bold is true)
                core = $"\\textbf{{{core}}}";
            return core;
        });
    }

    protected override string Link(string target, string content) => $"\\href{{{Url(target)}}}{{{content}}}";

    protected override string Picture(string link)
    {
        _pictures = true;
        // The link is a URL path; LaTeX wants the file's name.
        return $"\\includegraphics{{{Uri.UnescapeDataString(link)}}}";
    }

    protected override string LineBreak() => "\n";

    protected override string NoteReference(int number) => $"\\protect\\footnote{{{Notes[number - 1]}}}";

    protected override string? Equation(HEquation equation)
    {
        if (Tex(equation) is not { } tex)
            return null;
        _cjk |= tex.Any(IsCjk);
        return equation.Display ? $"\\[ {tex} \\]" : $"${tex}$";
    }

    protected override bool DisplayEquation(StringBuilder sb, HEquation equation, int quote)
    {
        if (equation.Tex is { } source && IsDisplayEnvironment(source) && _cell == 0)
        {
            Quoted(sb, quote, () => sb.Append(source.Trim()).Append("\n\n"));
            return true;
        }
        if (Tex(equation) is not { } tex)
            return false;
        _cjk |= tex.Any(IsCjk);
        Quoted(sb, quote, () => sb.Append("\\[\n").Append(tex).Append("\n\\]\n\n"));
        return true;
    }

    /// <summary>
    /// An equation's TeX: its source, unless that is a display environment of its own (which $…$ can't hold), else
    /// written from its tree; null when neither can be had.
    /// </summary>
    private static string? Tex(HEquation equation) =>
        equation.Tex is { } source && !IsDisplayEnvironment(source) && source.Trim().Length > 0 ? source.Trim() : TexMathWriter.Write(equation.Math);

    private static bool IsDisplayEnvironment(string tex)
    {
        var trimmed = tex.TrimStart();
        return DisplayEnvironments.Any(e => trimmed.StartsWith("\\begin{" + e, StringComparison.Ordinal));
    }

    // ───────────────────────── Escaping ─────────────────────────

    /// <summary>Text with every character LaTeX treats specially written so it prints as itself.</summary>
    private string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (IsCjk(ch))
                _cjk = true;
            switch (ch)
            {
                case '\\':
                    sb.Append("\\textbackslash{}");
                    break;
                case '{' or '}' or '$' or '&' or '#' or '_' or '%':
                    sb.Append('\\').Append(ch);
                    break;
                case '~':
                    sb.Append("\\textasciitilde{}");
                    break;
                case '^':
                    sb.Append("\\textasciicircum{}");
                    break;
                case '<':
                    sb.Append("\\textless{}");
                    break;
                case '>':
                    sb.Append("\\textgreater{}");
                    break;
                case '|':
                    sb.Append("\\textbar{}");
                    break;
                case '"':
                    sb.Append("\\textquotedbl{}");
                    break;
                case '`':
                    sb.Append("\\textasciigrave{}");
                    break;
                case '[' or ']':
                    // In braces: a bracket after \\ or \item would be read as an option.
                    sb.Append('{').Append(ch).Append('}');
                    break;
                case '-' when next == '-':
                    sb.Append("-{}");
                    break;
                case '\'' when next == '\'':
                    sb.Append("'{}");
                    break;
                case '\u00A0':
                    sb.Append('~');
                    break;
                case '\u00AD':
                    sb.Append("\\-");
                    break;
                case '\t':
                    sb.Append(' ');
                    break;
                case '\n':
                    sb.Append('\n');
                    break;
                case < ' ' or '\u200B' or '\uFEFF':
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>A URL for \href: the characters an argument can't hold percent-encoded or escaped.</summary>
    private static string Url(string target)
    {
        var sb = new StringBuilder(target.Length + 8);
        foreach (var ch in target)
        {
            switch (ch)
            {
                case '%' or '#':
                    sb.Append('\\').Append(ch);
                    break;
                case '\\' or '{' or '}' or '~' or '^' or ' ' or '"' or '<' or '>' or '|' or '`':
                    sb.Append(CultureInfo.InvariantCulture, $"%{(int)ch:X2}");
                    break;
                case < ' ':
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Hangul, kana, CJK ideographs and their punctuation: text that needs xeCJK or luatexja.</summary>
    internal static bool IsCjk(char ch) =>
        ch is >= '\u1100' and <= '\u11FF' or >= '\u2E80' and <= '\u9FFF' or >= '\uA960' and <= '\uA97F' or >= '\uAC00' and <= '\uD7FF'
            or >= '\uF900' and <= '\uFAFF' or >= '\uFE30' and <= '\uFE4F' or >= '\uFF00' and <= '\uFFEF';
}
