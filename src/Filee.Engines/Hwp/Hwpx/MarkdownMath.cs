// TeX math in Markdown (#1652), for Markdig: where it starts and ends, and how Markdown → HTML writes it (MathML).
//
// Markdig's own math reads $ … $ and $$ … $$; this reads those and \( … \) (inline) and \[ … \] (display) too, with the
// rules Pandoc uses for dollars: an opening $ is followed by a non-space, a closing $ follows a non-space and is not
// followed by a digit ("$5 and $10" stays text), and a $ or \( escaped with a backslash is text. A code span wins over
// math: "$a `b$` c" is a code span, as backticks bind before every other inline. $$ … $$ on lines of their own is a
// math block (Markdig's MathBlockParser).
//
// The HTML renderer writes MathML with the TeX as its annotation; a formula LatexMath doesn't read stays its source.

using System.Net;
using Markdig;
using Markdig.Extensions.Mathematics;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Parsers.Inlines;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace Filee.Engines.Hwp.Hwpx;

/// <summary>Adds <see cref="TexMathInlineParser"/> in place of Markdig's inline math, and MathML to the HTML renderer.</summary>
internal sealed class TexMathExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        pipeline.InlineParsers.TryRemove<MathInlineParser>();
        if (!pipeline.InlineParsers.Contains<TexMathInlineParser>())
        {
            // Before the escape parser, which would otherwise read \( and \[ as escaped brackets.
            if (!pipeline.InlineParsers.InsertBefore<EscapeInlineParser>(new TexMathInlineParser()))
                pipeline.InlineParsers.Insert(0, new TexMathInlineParser());
        }
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        if (renderer is not HtmlRenderer html)
            return;
        html.ObjectRenderers.TryRemove<HtmlMathInlineRenderer>();
        html.ObjectRenderers.TryRemove<HtmlMathBlockRenderer>();
        if (!html.ObjectRenderers.Contains<MathMLInlineRenderer>())
            html.ObjectRenderers.Insert(0, new MathMLInlineRenderer());
        if (!html.ObjectRenderers.Contains<MathMLBlockRenderer>())
            html.ObjectRenderers.Insert(0, new MathMLBlockRenderer());
    }
}

/// <summary>$ … $, $$ … $$, \( … \) and \[ … \] as <see cref="MathInline"/> (DelimiterCount 2 for display math).</summary>
internal sealed class TexMathInlineParser : InlineParser
{
    public TexMathInlineParser() => OpeningCharacters = ['$', '\\'];

    public override bool Match(InlineProcessor processor, ref StringSlice slice)
    {
        var text = slice.Text;
        var start = slice.Start;
        var end = slice.End; // inclusive
        int contentStart, contentEnd, after; // content [contentStart, contentEnd), the text after the closing delimiter
        bool display;
        char delimiter;
        if (slice.CurrentChar == '\\')
        {
            var kind = slice.PeekChar();
            if (kind is not ('(' or '['))
                return false;
            display = kind == '[';
            var close = display ? ']' : ')';
            contentStart = start + 2;
            var i = contentStart;
            for (; i < end; i++)
            {
                if (text[i] != '\\')
                    continue;
                if (text[i + 1] == close)
                    break;
                i++; // \\, \{, \$ … inside the math
            }
            if (i >= end)
                return false;
            contentEnd = i;
            after = i + 2;
            delimiter = '\\';
        }
        else
        {
            display = start < end && text[start + 1] == '$';
            contentStart = start + (display ? 2 : 1);
            if (contentStart > end || (!display && char.IsWhiteSpace(text[contentStart])))
                return false;
            var i = contentStart;
            var found = false;
            for (; i <= end; i++)
            {
                var c = text[i];
                if (c == '\\')
                {
                    i++;
                    continue;
                }
                if (c != '$')
                    continue;
                if (display)
                {
                    if (i < end && text[i + 1] == '$')
                    {
                        found = true;
                        break;
                    }
                    continue;
                }
                if (!char.IsWhiteSpace(text[i - 1]) && (i == end || !char.IsAsciiDigit(text[i + 1])))
                {
                    found = true;
                    break;
                }
            }
            if (!found)
                return false;
            contentEnd = i;
            after = i + (display ? 2 : 1);
            delimiter = '$';
        }

        if (contentEnd <= contentStart || text.AsSpan(contentStart, contentEnd - contentStart).IsWhiteSpace())
            return false;
        if (CodeSpanCrosses(text, contentStart, contentEnd, end))
            return false;

        var math = new MathInline
        {
            Delimiter = delimiter,
            DelimiterCount = display ? 2 : 1,
            Content = new StringSlice(text, contentStart, contentEnd - 1),
            Span = new SourceSpan(processor.GetSourcePosition(start, out var line, out var column), processor.GetSourcePosition(after - 1)),
            Line = line,
            Column = column,
        };
        processor.Inline = math;
        slice.Start = after;
        return true;
    }

    /// <summary>
    /// Whether a code span starts inside the math and ends after it: a run of backticks in the content with a run as
    /// long later in the paragraph, so the backticks win.
    /// </summary>
    private static bool CodeSpanCrosses(string text, int contentStart, int contentEnd, int end)
    {
        for (var i = contentStart; i < contentEnd; i++)
        {
            if (text[i] != '`')
                continue;
            var run = 1;
            while (i + run <= end && text[i + run] == '`')
                run++;
            for (var j = i + run; j <= end; j++)
            {
                if (text[j] != '`')
                    continue;
                var other = 1;
                while (j + other <= end && text[j + other] == '`')
                    other++;
                if (other == run)
                    return j + run > contentEnd;
                j += other - 1;
            }
            i += run - 1;
        }
        return false;
    }
}

/// <summary>Inline (and $$ display) math as MathML; the source when it isn't read.</summary>
internal sealed class MathMLInlineRenderer : HtmlObjectRenderer<MathInline>
{
    protected override void Write(HtmlRenderer renderer, MathInline obj) =>
        MarkdownMath.Write(renderer, obj.Content.ToString(), obj.DelimiterCount == 2, obj.Delimiter, block: false);
}

/// <summary>A $$ … $$ math block as display MathML.</summary>
internal sealed class MathMLBlockRenderer : HtmlObjectRenderer<MathBlock>
{
    protected override void Write(HtmlRenderer renderer, MathBlock obj)
    {
        renderer.EnsureLine();
        MarkdownMath.Write(renderer, obj.Lines.ToString(), display: true, '$', block: true);
        renderer.EnsureLine();
    }
}

internal static class MarkdownMath
{
    /// <summary>The source of a formula as written: "$x$", "$$x$$", "\(x\)", "\[x\]".</summary>
    public static string Source(string tex, bool display, char delimiter) => delimiter == '\\'
        ? (display ? $"\\[{tex}\\]" : $"\\({tex}\\)")
        : (display ? $"$${tex}$$" : $"${tex}$");

    public static void Write(HtmlRenderer renderer, string tex, bool display, char delimiter, bool block)
    {
        var tree = renderer.EnableHtmlForInline ? LatexMath.Parse(tex.Trim()) : null;
        if (tree is null)
        {
            // Plain text, or math that isn't read: its source.
            if (block && renderer.EnableHtmlForBlock)
                renderer.Write("<p>");
            renderer.Write(renderer.EnableHtmlEscape ? WebUtility.HtmlEncode(Source(tex, display, delimiter)) : Source(tex, display, delimiter));
            if (block && renderer.EnableHtmlForBlock)
                renderer.Write("</p>");
            return;
        }
        renderer.Write(MathMLWriter.Write(tree, display, tex.Trim()));
    }
}
