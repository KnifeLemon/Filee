// A Visio shape's text ([MS-VSDX] Text) → the document model's paragraphs.
//
// The Text element holds the characters with empty markers among them: cp (the Character row the following characters
// take), pp (the Paragraph row of the paragraph), tp (its Tabs row) and fld (a field, with its shown text inside or in
// the Field section's Value). A line feed ends a paragraph (the last one of the text ends nothing), U+2028 and vertical
// tab break a line, a tab is a tab. The rows' cells are resolved by the reader through the shape's inheritance
// (VisioCellChain.RowCell); a paragraph with a bullet gets its bullet character before its text.

using System.Text;
using System.Xml.Linq;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Visio;

/// <summary>A Character row resolved: the run format and whether its letters are shown as capitals.</summary>
internal readonly record struct VisioRun(HCharFormat Format, bool Capitals);

/// <summary>A Paragraph row resolved: the paragraph format and its bullet text (null = none).</summary>
internal readonly record struct VisioParagraph(HParaFormat Format, string? Bullet);

internal static class VisioText
{
    /// <summary>The paragraphs of <paramref name="text"/>.</summary>
    /// <param name="run">The run format of Character row n (asked for every run: the caller keeps them).</param>
    /// <param name="paragraph">The paragraph format of Paragraph row n (asked for every paragraph).</param>
    /// <param name="field">The value of Field row n, for a field stored without its text.</param>
    public static List<HParagraph> Paragraphs(XElement text, Func<int, VisioRun> run, Func<int, VisioParagraph> paragraph, Func<int, string?> field,
        DocumentBudget budget)
    {
        var builder = new Builder(run, paragraph, budget);
        foreach (var node in text.Nodes())
        {
            switch (node)
            {
                case XText characters:
                    builder.Add(characters.Value);
                    break;
                case XElement { Name.LocalName: "cp" } marker:
                    builder.Character(Index(marker));
                    break;
                case XElement { Name.LocalName: "pp" } marker:
                    builder.Paragraph(Index(marker));
                    break;
                case XElement { Name.LocalName: "fld" } marker:
                    builder.Add(marker.Value is { Length: > 0 } shown ? shown : field(Index(marker)) ?? "");
                    break;
            }
        }
        return builder.Finish();
    }

    private static int Index(XElement marker) =>
        int.TryParse((string?)marker.Attribute("IX"), out var index) && index >= 0 ? index : 0;

    private sealed class Builder(Func<int, VisioRun> run, Func<int, VisioParagraph> paragraph, DocumentBudget budget)
    {
        private readonly List<HParagraph> _paragraphs = [];
        private readonly StringBuilder _pending = new();
        private List<HInline> _inlines = [];
        private int _character;
        private int _paragraph;
        private bool _carriageReturn;

        private VisioRun Run(int index) => run(index);

        public void Character(int index)
        {
            Flush();
            _character = index;
        }

        public void Paragraph(int index) => _paragraph = index;

        public void Add(string text)
        {
            foreach (var c in text)
            {
                if (c == '\n' && _carriageReturn)
                {
                    _carriageReturn = false;
                    continue;
                }
                _carriageReturn = c == '\r';
                switch (c)
                {
                    case '\n' or '\r':
                        End();
                        break;
                    case (char)0x2028 or '\v':
                        Flush();
                        _inlines.Add(new HLineBreak(Run(_character).Format));
                        break;
                    case '\t':
                        Flush();
                        _inlines.Add(new HTab(Run(_character).Format));
                        break;
                    default:
                        if (!char.IsControl(c) || char.IsWhiteSpace(c))
                            _pending.Append(c);
                        break;
                }
            }
        }

        private void Flush()
        {
            if (_pending.Length == 0)
                return;
            var format = Run(_character);
            var text = _pending.ToString();
            _inlines.Add(new HText(format.Capitals ? text.ToUpperInvariant() : text, format.Format));
            _pending.Clear();
        }

        /// <summary>Ends the paragraph being built.</summary>
        private void End()
        {
            Flush();
            budget.Paragraph();
            var (format, bullet) = paragraph(_paragraph);
            var mark = Run(_character).Format;
            var result = new HParagraph { Format = format, MarkFormat = _inlines.OfType<HText>().FirstOrDefault()?.Format ?? mark };
            if (bullet is not null && _inlines.Count > 0)
                result.Inlines.Add(new HText(bullet + " ", result.MarkFormat));
            result.Inlines.AddRange(_inlines);
            _paragraphs.Add(result);
            _inlines = [];
        }

        public List<HParagraph> Finish()
        {
            Flush();
            if (_inlines.Count > 0)
                End();
            return _paragraphs;
        }
    }
}
