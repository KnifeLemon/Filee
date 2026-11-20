// The built-in writers of LaTeX, AsciiDoc, Typst and man pages (Hwp/Hwpx/Markup), which Pandoc wrote before: one
// model built in code with everything they write (headings, emphasis, nested and numbered lists, quotes, code, a table
// with merged cells, links, footnotes, pictures, equations and every special character), the text each writes for it,
// the structure that keeps the file readable (balanced braces and environments, requests, blocks), their routes, which
// now win over Pandoc's, and cancellation and limits.

using System.Text;
using System.Text.RegularExpressions;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Markup;
using Filee.Engines.Text;

namespace Filee.Engines.Tests;

public sealed partial class MacroMarkupWriterTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>Text with every character one of the four languages treats specially.</summary>
    private const string Specials = @"a\b {c} $d & #e _f %g ~h ^i <j> |k| ""l"" [m] n--o '' `p` q@r *s* t+u :: v;; w... x://y (C) -? //z";

    private static HText Text(string text, HCharFormat format = default) => new(text, format);

    private static HParagraph Paragraph(params HInline[] inlines)
    {
        var paragraph = new HParagraph();
        paragraph.Inlines.AddRange(inlines);
        return paragraph;
    }

    private static HCell Cell(string text, int rowSpan = 1, int colSpan = 1)
    {
        var cell = new HCell { RowSpan = rowSpan, ColSpan = colSpan };
        cell.Blocks.Add(Paragraph(Text(text)));
        return cell;
    }

    private static HParagraph Item(HNumbering numbering, int level, string text, bool numbered = true)
    {
        var paragraph = Paragraph(Text(text));
        paragraph.List = new HListRef(numbering, level, numbered);
        return paragraph;
    }

    private static HDocument Document(params HBlock[] blocks)
    {
        var document = new HDocument();
        var section = new HSection();
        section.Blocks.AddRange(blocks);
        document.Sections.Add(section);
        return document;
    }

    /// <summary>A document with everything the writers write.</summary>
    private static HDocument Sample()
    {
        var numbering = new HNumbering();
        numbering.Levels.Add(new HNumberingLevel("DIGIT", "•", 1, Bullet: true));
        numbering.Levels.Add(new HNumberingLevel("DIGIT", "^2.", 3, Bullet: false));
        numbering.Levels.Add(new HNumberingLevel("DIGIT", "◦", 1, Bullet: true));

        var note = new HNote(endnote: false);
        note.Blocks.Add(Paragraph(Text("The note text.")));
        var link = new HLink("https://example.com/a b#c");
        link.Content.Add(Text("the site"));
        var fraction = new MathFraction(new MathText("a"), new MathText("b"));
        var display = new HParagraph { Format = new HParaFormat(Align: HAlign.Center) };
        display.Inlines.Add(new HEquation(EquationParser.Parse("SUM _{i=1} ^{n} i"), default));

        var table = new HTable { ColumnCount = 3 };
        var header = new HRow { Header = true };
        header.Cells.AddRange([Cell("H1"), Cell("H2"), Cell("H3")]);
        var first = new HRow();
        first.Cells.AddRange([Cell("wide", colSpan: 2), Cell("tall", rowSpan: 2)]);
        var second = new HRow();
        second.Cells.AddRange([Cell("x"), Cell("y")]);
        table.Rows.AddRange([header, first, second]);

        var document = Document(
            new HParagraph { HeadingLevel = 1, Inlines = { Text("Intro_1") } },
            Paragraph(Text("Plain "), Text("bold", new(Bold: true)), Text(" "), Text("italic", new(Italic: true)), Text(" "),
                Text("under", new(Underline: true)), Text(" "), Text("gone", new(Strike: true)), Text(" "),
                Text("code()", new(Shade: HtmlReader.CodeShade)), Text(" E=mc"), Text("2", new(Superscript: true)), Text(" H"),
                Text("2", new(Subscript: true)), Text("O"), new HLineBreak(default), Text("next line")),
            Paragraph(Text(Specials)),
            Paragraph(Text("See "), link, Text(" and a note"), note, Text(".")),
            Paragraph(Text("Half is "), new HEquation(fraction, default), Text(".")),
            display,
            Item(numbering, 0, "first bullet"),
            Item(numbering, 1, "third numbered"),
            Item(numbering, 2, "deep bullet"),
            Item(numbering, 1, "fourth numbered"),
            Item(numbering, 1, "its second paragraph", numbered: false),
            Item(numbering, 0, "last bullet"),
            new HParagraph { Format = new HParaFormat(Left: MarkupBlocks.QuoteIndent), Inlines = { Text("A quoted line.") } },
            Paragraph(Text("int main() {", MarkupBlocks.Code)),
            Paragraph(Text("  return 0; // \\end{verbatim}? no", MarkupBlocks.Code)),
            Paragraph(Text("}", MarkupBlocks.Code)),
            table,
            new HParagraph { HeadingLevel = 2, Inlines = { Text("Pictures") } },
            Paragraph(new HImage("C:/pictures/photo one.png")),
            Paragraph(new HShape(HShapeKind.Line)),
            Paragraph(Text("한글 텍스트 and café 😀")));
        document.Title = "Report & Notes";
        document.Authors.Add("Kim");
        return document;
    }

    private static string? Pictures(string path) => "report_files/" + Uri.EscapeDataString(Path.GetFileName(path));

    // ───────────────────────── LaTeX ─────────────────────────

    [Fact]
    public void Latex_is_a_whole_article_with_everything_escaped()
    {
        var tex = LatexWriter.Write(Sample(), Pictures);
        Assert.StartsWith("\\documentclass{article}\n", tex, StringComparison.Ordinal);
        Assert.EndsWith("\\end{document}\n", tex, StringComparison.Ordinal);
        foreach (var expected in new[]
                 {
                     // The preamble loads what the body uses.
                     "\\usepackage{iftex}", "\\usepackage{fontspec}", "\\usepackage{xeCJK}", "\\usepackage{amsmath,amssymb}", "\\usepackage{graphicx}",
                     "\\usepackage{longtable,array,multirow}", "\\usepackage[normalem]{ulem}", "\\usepackage{hyperref}",
                     "\\title{Report \\& Notes}", "\\author{Kim}", "\\maketitle",
                     "\\section{Intro\\_1}", "\\subsection{Pictures}",
                     "\\textbf{bold}", "\\emph{italic}", "\\protect\\uline{under}", "\\protect\\sout{gone}", "\\texttt{code()}",
                     "E=mc\\textsuperscript{2}", "H\\textsubscript{2}O", "Plain", "\\\\\nnext line",
                     // Specials
                     "a\\textbackslash{}b \\{c\\} \\$d \\& \\#e \\_f \\%g \\textasciitilde{}h \\textasciicircum{}i \\textless{}j\\textgreater{}",
                     "\\textbar{}k\\textbar{} \\textquotedbl{}l\\textquotedbl{} {[}m{]} n-{}-o '{}' \\textasciigrave{}p\\textasciigrave{}",
                     "\\href{https://example.com/a%20b\\#c}{the site}", "\\protect\\footnote{The note text.}",
                     "Half is $\\frac{a}{b}$.", "\\[\n\\sum_{i=1}^{n}i\n\\]",
                     "\\begin{itemize}\n\\item first bullet\n\\begin{enumerate}\n\\item[3.] third numbered\n\\begin{itemize}\n\\item deep bullet\n\\end{itemize}\n\\item[4.] fourth numbered\n\nits second paragraph\n\\end{enumerate}\n\\item last bullet\n\\end{itemize}",
                     "\\begin{quote}\nA quoted line.\n\n\\end{quote}",
                     "\\begin{longtable}",
                     "\\multicolumn{2}{|p{\\dimexpr 0.6667\\linewidth-2\\tabcolsep-\\arrayrulewidth\\relax}|}{wide}", "\\multirow{2}{=}{tall}",
                     "\\cline{1-2}", "\\endhead", "\\includegraphics{report_files/photo one.png}", "\\noindent\\rule{\\linewidth}{0.4pt}",
                     "한글 텍스트 and café 😀",
                 })
            Assert.True(tex.Contains(expected, StringComparison.Ordinal), $"lacks {expected}:\n{tex}");
        // Code holding \end{verbatim} can't be verbatim: its lines are typewriter text.
        Assert.DoesNotContain("\\begin{verbatim}", tex, StringComparison.Ordinal);
        Assert.Contains("{\\ttfamily int\\ main()\\ \\{\\\\", tex, StringComparison.Ordinal);
        AssertBalancedLatex(tex);
    }

    [Fact]
    public void Latex_writes_code_verbatim_and_tex_math_as_its_source()
    {
        var read = MarkdownReader.Read("Text $\\alpha^2$ here.\n\n$$\\begin{align} a &= b \\end{align}$$\n\n```\nif (a < b) { c(); }\n```\n", fx.NewFolder());
        MarkupBlocks.OpenCodeBoxes(read); // as MarkupConverter does
        var tex = LatexWriter.Write(read, _ => null);
        Assert.Contains("Text $\\alpha^2$ here.", tex, StringComparison.Ordinal);
        // A display environment of its own is written as it is, not inside \[ \].
        Assert.Contains("\\begin{align} a &= b \\end{align}", tex, StringComparison.Ordinal);
        Assert.DoesNotContain("\\[\n\\begin{align}", tex, StringComparison.Ordinal);
        Assert.Contains("\\begin{verbatim}\nif (a < b) { c(); }\n\\end{verbatim}", tex, StringComparison.Ordinal);
        Assert.DoesNotContain("xeCJK", tex, StringComparison.Ordinal);
        Assert.DoesNotContain("graphicx", tex, StringComparison.Ordinal);
        AssertBalancedLatex(tex);
    }

    [Fact]
    public void Latex_lists_deeper_than_four_levels_stay_at_the_fourth()
    {
        var numbering = new HNumbering();
        numbering.Levels.Add(new HNumberingLevel("DIGIT", "•", 1, Bullet: true));
        var blocks = Enumerable.Range(0, 9).Select(level => (HBlock)Item(numbering, level, "level " + level)).ToArray();
        var tex = LatexWriter.Write(Document(blocks), _ => null);
        Assert.Equal(4, Regex.Matches(tex, @"\\begin\{itemize\}").Count);
        Assert.Contains("\\item level 8", tex, StringComparison.Ordinal);
        AssertBalancedLatex(tex);
    }

    /// <summary>
    /// Braces balance (escaped ones and verbatim aside), environments close in order, and no [ follows \\ or \item
    /// unbraced.
    /// </summary>
    private static void AssertBalancedLatex(string tex)
    {
        var environments = new Stack<string>();
        var depth = 0;
        for (var i = 0; i < tex.Length; i++)
        {
            if (string.CompareOrdinal(tex, i, "\\begin{verbatim}", 0, 16) == 0)
            {
                var end = tex.IndexOf("\\end{verbatim}", i + 16, StringComparison.Ordinal);
                Assert.True(end > 0, "verbatim never ends");
                i = end + "\\end{verbatim}".Length - 1;
                continue;
            }
            var m = Environment().Match(tex, i);
            if (m.Success && m.Index == i)
            {
                if (m.Groups[1].Value == "begin")
                    environments.Push(m.Groups[2].Value);
                else
                    Assert.Equal(environments.Pop(), m.Groups[2].Value);
            }
            switch (tex[i])
            {
                case '\\':
                    i++; // an escaped character or the first letter of a control word
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    Assert.True(depth >= 0, $"unbalanced }} at {i}: {tex[Math.Max(0, i - 40)..Math.Min(tex.Length, i + 40)]}");
                    break;
            }
        }
        Assert.Equal(0, depth);
        Assert.Empty(environments);
        Assert.DoesNotMatch(@"(\\\\|\\item)\s*\[(?!\d+\.\]|\])", tex);
    }

    [GeneratedRegex(@"\\(begin|end)\{([^}]*)\}")]
    private static partial Regex Environment();

    // ───────────────────────── AsciiDoc ─────────────────────────

    [Fact]
    public void Asciidoc_writes_everything_and_keeps_text_from_reading_as_markup()
    {
        var adoc = AsciiDocWriter.Write(Sample(), Pictures);
        Assert.StartsWith("= Report ++&++ Notes\n:stem: latexmath\n\n", adoc, StringComparison.Ordinal);
        foreach (var expected in new[]
                 {
                     "== Intro++_++1", "=== Pictures", "**bold**", "__italic__", "[.underline]##under##", "[.line-through]##gone##",
                     "``code()``", "E=mc^2^", "H~2~O", " +\nnext line",
                     // Specials in passthroughs, + and | as attributes.
                     "a++\\++b ++{++c++}++ $d ++&++ ++#++e ++_++f %g ++~++h ++^++i ++<++j++>++ {vbar}k{vbar} \"l\" ++[++m++]++ n++-++-o",
                     "q++@++r ++*++s++*++ t{plus}u ++:++: v++;++; w++.++.. x++:++//y ++(++C) -? //z",
                     "link:++https://example.com/a%20b#c++[the site]", "footnote:[The note text.]",
                     "Half is latexmath:[\\frac{a}{b}].", "[latexmath]\n++++\n\\sum_{i=1}^{n}i\n++++",
                     "* first bullet\n.. third numbered\n*** deep bullet\n.. fourth numbered\n+\nits second paragraph\n* last bullet",
                     "____\nA quoted line.\n\n____",
                     "----\nint main() {\n  return 0; // \\end{verbatim}? no\n}\n----",
                     "[cols=\"3*\",options=\"header\"]\n|===\n| H1\n| H2\n| H3\n\n2+| wide\n.2+| tall\n\n| x\n| y\n\n|===",
                     "image:report_files/photo%20one.png[]", "'''",
                 })
            Assert.True(adoc.Contains(expected, StringComparison.Ordinal), $"lacks {expected}:\n{adoc}");
        AssertNoBlockStarts(adoc);
    }

    [Fact]
    public void Asciidoc_guards_line_starts_directives_and_callouts()
    {
        var document = Document(
            Paragraph(Text("* not a list"), new HLineBreak(default), Text("include::secret.txt[]"), new HLineBreak(default), Text(". not a title"),
                new HLineBreak(default), Text("1. not an item"), new HLineBreak(default), Text("Term:: not a definition")),
            Paragraph(Text("include::../../etc/passwd[]", MarkupBlocks.Code)),
            Paragraph(Text("x = 1; <1>", MarkupBlocks.Code)));
        var adoc = AsciiDocWriter.Write(document, _ => null);
        Assert.Contains("{empty}++*++ not a list +\ninclude++:++:secret.txt++[]++ +\n{empty}. not a title +\n{empty}1. not an item +\nTerm++:++: not a definition", adoc, StringComparison.Ordinal);
        Assert.Contains("[subs=\"specialchars\"]\n----\n\\include::../../etc/passwd[]\nx = 1; <1>\n----", adoc, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex("^include::", RegexOptions.Multiline), adoc);
    }

    [Fact]
    public void Asciidoc_numbers_a_list_from_its_first_number_and_nests_tables_once()
    {
        var numbering = new HNumbering();
        var level = new HNumberingLevel("DIGIT", "^1.", 5, Bullet: false);
        numbering.Levels.Add(level);
        var inner = new HTable { ColumnCount = 1 };
        var row = new HRow();
        row.Cells.Add(Cell("inner!cell"));
        inner.Rows.Add(row);
        var outer = new HTable { ColumnCount = 1 };
        var outerRow = new HRow();
        var cell = new HCell();
        cell.Blocks.Add(inner);
        outerRow.Cells.Add(cell);
        outer.Rows.Add(outerRow);
        var adoc = AsciiDocWriter.Write(Document(Item(numbering, 0, "five"), Item(numbering, 0, "six"), outer), _ => null);
        Assert.Contains("[start=5]\n. five\n. six", adoc, StringComparison.Ordinal);
        Assert.Contains("a| [cols=\"1*\"]\n!===\n! inner\\!cell\n\n!===", adoc, StringComparison.Ordinal);
    }

    /// <summary>Every line in a paragraph that isn't markup Filee wrote starts with a letter, digit or {empty}.</summary>
    private static void AssertNoBlockStarts(string adoc)
    {
        foreach (var line in adoc.Split('\n'))
            Assert.False(Regex.IsMatch(line, @"^(include|ifdef|ifndef|ifeval|endif)::"), line);
    }

    // ───────────────────────── Typst ─────────────────────────

    [Fact]
    public void Typst_writes_everything_and_escapes_its_markup()
    {
        var typ = TypstWriter.Write(Sample(), Pictures);
        foreach (var expected in new[]
                 {
                     "#set document(title: \"Report & Notes\", author: (\"Kim\",))", "#align(center, text(size: 1.7em, weight: \"bold\")[Report & Notes])",
                     "= Intro\\_1", "== Pictures", "#strong[bold]", "#emph[italic]", "#underline[under]", "#strike[gone]", "`code()`",
                     "E=mc#super[2]", "H#sub[2]O", " \\\nnext line",
                     "a\\\\b {c} \\$d & \\#e \\_f %g \\~h ^i \\<j\\> |k| \\\"l\\\" \\[m\\] n\\--o \\'\\' \\`p\\` q\\@r \\*s\\* t+u :: v;; w\\... x\\:\\//y (C) \\-? \\//z",
                     "#link(\"https://example.com/a b#c\")[the site]", "#footnote[The note text.]",
                     "Half is $frac(a, b)$\\.", "$ ∑_(i = 1)^(n) i $",
                     "- first bullet\n  3. third numbered\n    - deep bullet\n  4. fourth numbered\n\n    its second paragraph\n- last bullet",
                     "#quote(block: true)[\nA quoted line.\n\n]",
                     "```\nint main() {\n  return 0; // \\end{verbatim}? no\n}\n```",
                     "#table(\n  columns: 3,\n  table.header(\n    [H1],\n    [H2],\n    [H3],\n  ),\n  table.cell(colspan: 2)[wide],\n  table.cell(rowspan: 2)[tall],\n  [x],\n  [y],\n)",
                     "#image(\"report_files/photo one.png\")", "#line(length: 100%)",
                 })
            Assert.True(typ.Contains(expected, StringComparison.Ordinal), $"lacks {expected}:\n{typ}");
        AssertBalancedTypst(typ);
    }

    [Fact]
    public void Typst_guards_line_starts()
    {
        var typ = TypstWriter.Write(Document(Paragraph(Text("= not a heading"), new HLineBreak(default), Text("- not a list"), new HLineBreak(default),
            Text("12. not an item"), new HLineBreak(default), Text("+ no"), new HLineBreak(default), Text("/ no: term"))), _ => null);
        Assert.Contains("\\= not a heading \\\n\\- not a list \\\n12\\. not an item \\\n\\+ no \\\n\\/ no: term", typ, StringComparison.Ordinal);
    }

    /// <summary>Square brackets and dollar signs pair up outside strings, raw text and escapes.</summary>
    private static void AssertBalancedTypst(string typ)
    {
        var depth = 0;
        var dollars = 0;
        for (var i = 0; i < typ.Length; i++)
        {
            switch (typ[i])
            {
                case '\\':
                    i++;
                    break;
                case '"':
                    for (i++; i < typ.Length && typ[i] != '"'; i++)
                        if (typ[i] == '\\')
                            i++;
                    break;
                case '`':
                    {
                        var fence = typ.Length - i >= 3 && typ.Substring(i, 3) == "```" ? "```" : "`";
                        var end = typ.IndexOf(fence, i + fence.Length, StringComparison.Ordinal);
                        Assert.True(end > 0, "raw text never ends");
                        i = end + fence.Length - 1;
                        break;
                    }
                case '[':
                    depth++;
                    break;
                case ']':
                    depth--;
                    Assert.True(depth >= 0, $"unbalanced ] at {i}");
                    break;
                case '$':
                    dollars++;
                    break;
            }
        }
        Assert.Equal(0, depth);
        Assert.Equal(0, dollars % 2);
    }

    // ───────────────────────── man ─────────────────────────

    [Fact]
    public void Man_page_writes_everything_in_ascii_requests()
    {
        var man = new ManWriter().Write(Sample());
        Assert.StartsWith(".TH \"Report & Notes\" \"1\" \"\" \"\" \"\"\n", man, StringComparison.Ordinal);
        foreach (var expected in new[]
                 {
                     ".SH \"Intro_1\"", ".SS \"Pictures\"", "\\fBbold\\fP", "\\fIitalic\\fP", "\\fIunder\\fP", "\\f(CRcode()\\fP", "\n.br\nnext line",
                     "a\\eb {c} $d & #e _f %g \\(tih \\(hai <j> |k| \\(dql\\(dq [m] n\\-\\-o \\(aq\\(aq \\(gap\\(ga q@r *s* t+u :: v;; w... x://y (C) \\-? //z",
                     "the site <https://example.com/a b#c>", "and a note[1].", ".SH NOTES\n.IP \"[1]\" 5\nThe note text.",
                     "Half is a/b.",
                     ".IP \"\\(bu\" 4\nfirst bullet\n.RS 4\n.IP \"3.\" 4\nthird numbered\n.RS 4\n.IP \"\\(bu\" 4\ndeep bullet\n.RE\n.IP \"4.\" 4\nfourth numbered\n.IP \"\" 4\nits second paragraph\n.RE\n.IP \"\\(bu\" 4\nlast bullet",
                     ".RS 4\n.PP\nA quoted line.\n.RE",
                     ".EX\nint main() {\n\\&  return 0; // \\eend{verbatim}? no\n}\n.EE",
                     ".TS\nallbox tab(@);\nlb lb lb\nl s l\nl l ^.\nT{\nH1\nT}@T{\nH2\nT}@T{\nH3\nT}\nT{\nwide\nT}@@T{\ntall\nT}\nT{\nx\nT}@T{\ny\nT}\n.TE",
                     "\\[uD55C]\\[uAE00] \\[uD14D]\\[uC2A4]\\[uD2B8] and caf\\[u00E9] \\[u1F600]",
                 })
            Assert.True(man.Contains(expected, StringComparison.Ordinal), $"lacks {expected}:\n{man}");
        Assert.DoesNotContain("photo", man, StringComparison.Ordinal); // a man page has no pictures
        Assert.All(man, c => Assert.True(c < 0x80, $"U+{(int)c:X4}"));
        // Every line starting with "." or "'" is a request Filee wrote.
        foreach (var line in man.Split('\n').Where(l => l.StartsWith('.') || l.StartsWith('\'')))
            Assert.Matches(@"^\.(TH|SH|SS|PP|br|IP|RS|RE|EX|EE|TS|TE|ce)( |$)", line);
        Assert.Equal(Regex.Matches(man, "^\\.RS", RegexOptions.Multiline).Count, Regex.Matches(man, "^\\.RE", RegexOptions.Multiline).Count);
    }

    [Fact]
    public void Man_page_text_never_reads_as_a_request()
    {
        var man = new ManWriter().Write(Document(Paragraph(Text(".hidden request"), new HLineBreak(default), Text("'also hidden"), new HLineBreak(default),
            Text("  indented"), new HLineBreak(default), Text("T} ends a cell"))));
        Assert.Contains(".PP\n\\&.hidden request\n.br\n\\(aqalso hidden\n.br\n\\&  indented\n.br\n\\&T} ends a cell", man, StringComparison.Ordinal);
    }

    // ───────────────────────── Shared ─────────────────────────

    private static IEnumerable<(string Name, Func<HDocument, string> Write)> Writers(DocumentLimits l, CancellationToken cancellation)
    {
        yield return ("tex", d => new LatexWriter(_ => null) { Limits = l, Cancellation = cancellation }.Write(d));
        yield return ("adoc", d => new AsciiDocWriter(_ => null) { Limits = l, Cancellation = cancellation }.Write(d));
        yield return ("typ", d => new TypstWriter(_ => null) { Limits = l, Cancellation = cancellation }.Write(d));
        yield return ("man", d => new ManWriter { Limits = l, Cancellation = cancellation }.Write(d));
    }

    [Fact]
    public void A_cancelled_conversion_stops_the_writers()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        foreach (var (_, write) in Writers(DocumentLimits.Default, cancelled.Token))
            Assert.ThrowsAny<OperationCanceledException>(() => write(Sample()));
    }

    [Fact]
    public void A_footnote_shared_by_many_references_is_refused_by_the_limits()
    {
        var note = new HNote(endnote: false);
        note.Blocks.Add(Paragraph(Text(new string('n', 100_000))));
        var paragraph = new HParagraph();
        for (var i = 0; i < 100; i++)
            paragraph.Inlines.AddRange([Text("ref" + i + " "), note]);
        var small = DocumentLimits.Default with { MaxPartBytes = 1024 * 1024 };
        foreach (var (name, write) in Writers(small, TestContext.Current.CancellationToken))
        {
            var refusal = Assert.Throws<ConversionRefusedException>(() => write(Document(paragraph)));
            Assert.True(refusal.ErrorKey == DocumentLimits.ErrorKey, name);
        }
    }

    [Fact]
    public void Hostile_lists_quotes_and_equations_are_written_in_time()
    {
        // List levels and quote indents far past what any language nests, and an equation 20,000 levels deep.
        var numbering = new HNumbering();
        numbering.Levels.Add(new HNumberingLevel("DIGIT", "^1.", 1, Bullet: false));
        MathNode deep = new MathText("x");
        for (var i = 0; i < 20_000; i++)
            deep = new MathRoot(null, deep);
        var blocks = new List<HBlock>
        {
            Item(numbering, 10_000, "deep item"),
            new HParagraph { Format = new HParaFormat(Left: int.MaxValue), Inlines = { Text("deep quote") } },
            Paragraph(new HEquation(deep, default, "deep equation")),
        };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var (name, write) in Writers(DocumentLimits.Default, TestContext.Current.CancellationToken))
        {
            var text = write(Document([.. blocks]));
            foreach (var expected in new[] { "deep item", "deep quote", "deep equation" })
                Assert.True(text.Contains(expected, StringComparison.Ordinal), $"{name} lacks {expected}");
        }
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"{watch.Elapsed}");
    }

    // ───────────────────────── Routes ─────────────────────────

    public static TheoryData<string> Targets() => [.. MarkupConverter.WriteOnly.Keys];

    [Theory]
    [MemberData(nameof(Targets))]
    public void Built_in_writers_win_over_pandoc_which_keeps_reading_them(string target)
    {
        foreach (var planner in new[] { fx.Catalog.CreatePlanner(["pandoc"]), MarkupTestKit.WithoutPandoc(fx).CreatePlanner() })
        {
            foreach (var source in new[] { "md", "html", "docx", "odt", "hwpx", "hwp", "rtf", "rst", "org", "txt" })
                Assert.Equal("markup", Assert.Single(planner.Plan(source, target)!.Steps).Converter.Id);
        }
        var withPandoc = fx.Catalog.CreatePlanner(["pandoc"]);
        Assert.Equal("pandoc", Assert.Single(withPandoc.Plan(target, "md")!.Steps).Converter.Id);
        Assert.Null(MarkupTestKit.WithoutPandoc(fx).CreatePlanner().Plan(target, "md"));
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task Markdown_converts_to_each_without_pandoc(string target)
    {
        var folder = fx.NewFolder();
        var source = Path.Combine(folder, "notes.md");
        await File.WriteAllTextAsync(source, "# Title_1\n\nSome *text* with $x^2$ and a [link](https://example.com).\n\n- one\n- two\n",
            new UTF8Encoding(false), TestContext.Current.CancellationToken);
        var output = await MarkupTestKit.ConvertAsync(MarkupTestKit.WithoutPandoc(fx), source, target);
        var text = await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken);
        Assert.Contains("Title", text, StringComparison.Ordinal);
        Assert.Contains("one", text, StringComparison.Ordinal);
        Assert.Contains(target switch
        {
            "tex" => "$x^2$",
            "adoc" => "latexmath:[x^2]",
            "typ" => "$x^(2)$",
            _ => "x\\(ha2",
        }, text, StringComparison.Ordinal);
        // Written through a temporary file, which is gone.
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(output)!, "*.tmp"));
    }
}
