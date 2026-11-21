// Character styles, comments and tracked changes.
//
// A run's character style (HCharLayout.Style) is written as w:rStyle with a character style of that name in
// styles.xml, without properties of its own: the run's formatting is written on the run, as the model resolved it.
// Comments go into word/comments.xml, their range between w:commentRangeStart and w:commentRangeEnd and their mark in a
// run of its own after it; comments are only written in the document's own text (not in notes, headers or footers, nor
// in comments). Inserted runs are wrapped in w:ins, deleted text (HDeletion) is w:del with its runs' w:delText.

using System.Globalization;
using System.Text;

namespace Filee.Engines.Hwp.Hwpx.Docx;

internal sealed partial class DocxWriter
{
    /// <summary>Style ids of the styles styles.xml always has: a character style can't take one of them.</summary>
    private static readonly HashSet<string> BuiltInStyleIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Normal", "DefaultParagraphFont", "TableNormal", "NoList", "Heading1", "Heading2", "Heading3", "Heading4", "Heading5",
        "Heading6", "Heading7", "Heading8", "Heading9", "Hyperlink", "FootnoteText", "FootnoteReference", "EndnoteText",
        "EndnoteReference", "Header", "Footer", "TableGrid", "CommentText", "CommentReference",
    };

    /// <summary>The built-in character styles a run's style name may be, by name.</summary>
    private static readonly Dictionary<string, string> BuiltInCharacterStyles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Hyperlink"] = "Hyperlink",
        ["footnote reference"] = "FootnoteReference",
        ["endnote reference"] = "EndnoteReference",
        ["annotation reference"] = "CommentReference",
    };

    /// <summary>The character styles the runs use, by name, with their ids, in the order of first use.</summary>
    private readonly Dictionary<string, string> _characterStyles = new(StringComparer.Ordinal);
    private readonly HashSet<string> _styleIds = new(StringComparer.OrdinalIgnoreCase);

    private Part? _commentsPart;
    private readonly List<string> _commentXml = [];
    private readonly Dictionary<HComment, int> _commentIds = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<HComment> _commentsStarted = new(ReferenceEqualityComparer.Instance);

    /// <summary>Writing a comment's paragraphs: comments in them are left out.</summary>
    private bool _inComment;

    private int _nextRevisionId;

    /// <summary>The id of the character style a run is in (HCharLayout.Style), defined on first use; null for none.</summary>
    private string? CharacterStyle(HCharFormat format)
    {
        if (format.Layout?.Style is not { } name || string.IsNullOrWhiteSpace(name))
            return null;
        name = name.Trim();
        if (BuiltInCharacterStyles.TryGetValue(name, out var builtIn))
            return builtIn;
        if (_characterStyles.TryGetValue(name, out var id))
            return id;
        var letters = new string([.. name.Where(char.IsAsciiLetterOrDigit)]);
        var stem = letters.Length == 0 ? "CharacterStyle" : letters.Length > 40 ? letters[..40] : letters;
        id = stem;
        for (var n = 2; BuiltInStyleIds.Contains(id) || !_styleIds.Add(id); n++)
            id = stem + n.ToString(CultureInfo.InvariantCulture);
        _characterStyles[name] = id;
        return id;
    }

    /// <summary>The character styles the runs used, for styles.xml.</summary>
    private string CharacterStyles()
    {
        var sb = new StringBuilder();
        foreach (var (name, id) in _characterStyles)
        {
            sb.Append($"<w:style w:type=\"character\" w:customStyle=\"1\" w:styleId=\"{id}\"><w:name w:val=\"{Escape(name)}\"/>")
              .Append("<w:basedOn w:val=\"DefaultParagraphFont\"/><w:qFormat/></w:style>");
        }
        if (_commentXml.Count > 0)
        {
            sb.Append("<w:style w:type=\"paragraph\" w:styleId=\"CommentText\"><w:name w:val=\"annotation text\"/><w:basedOn w:val=\"Normal\"/><w:uiPriority w:val=\"99\"/><w:unhideWhenUsed/>")
              .Append("<w:pPr><w:spacing w:line=\"240\" w:lineRule=\"auto\"/></w:pPr><w:rPr><w:sz w:val=\"20\"/><w:szCs w:val=\"20\"/></w:rPr></w:style>");
            sb.Append("<w:style w:type=\"character\" w:styleId=\"CommentReference\"><w:name w:val=\"annotation reference\"/><w:basedOn w:val=\"DefaultParagraphFont\"/><w:uiPriority w:val=\"99\"/><w:semiHidden/><w:unhideWhenUsed/>")
              .Append("<w:rPr><w:sz w:val=\"16\"/><w:szCs w:val=\"16\"/></w:rPr></w:style>");
        }
        return sb.ToString();
    }

    // ───────────────────────── Comments ─────────────────────────

    /// <summary>Comments are written here: the document's own text, outside comments.</summary>
    private bool CommentsHere => _part == _document && !_inComment;

    /// <summary>The comment's id in comments.xml, its paragraphs written there the first time.</summary>
    private int CommentId(HComment comment)
    {
        if (_commentIds.TryGetValue(comment, out var id))
            return id;
        id = _commentXml.Count;
        _commentIds[comment] = id;
        _commentXml.Add("");
        var part = _commentsPart ??= new Part("word/comments.xml", ContentTypeBase + "comments+xml");
        var outer = (_inComment, _paragraph, _width);
        _inComment = true;
        try
        {
            const string Mark = "<w:r><w:rPr><w:rStyle w:val=\"CommentReference\"/></w:rPr><w:annotationRef/></w:r>";
            var content = WithPart(part, () => Blocks(comment.Blocks, requireParagraph: true, paragraphStyle: "CommentText", leadingRuns: Mark));
            var initials = comment.Initials is { Length: > 0 } given ? given : Initials(comment.Author);
            _commentXml[id] = $"<w:comment w:id=\"{Number(id)}\" w:author=\"{Escape(comment.Author ?? "")}\"{DateAttribute(comment.Date)}" +
                              $"{(initials is null ? "" : $" w:initials=\"{Escape(initials)}\"")}>{content}</w:comment>";
        }
        finally
        {
            (_inComment, _paragraph, _width) = outer;
        }
        return id;
    }

    /// <summary>The first letters of an author's names ("Kim Minsu" → "KM"), as Word makes initials; null for no author.</summary>
    private static string? Initials(string? author)
    {
        if (string.IsNullOrWhiteSpace(author))
            return null;
        var letters = string.Concat(author.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w[..1]));
        return letters.Length > 9 ? letters[..9] : letters;
    }

    private void CommentStart(HCommentStart start, StringBuilder sb)
    {
        if (!CommentsHere || !_commentsStarted.Add(start.Comment))
            return;
        sb.Append($"<w:commentRangeStart w:id=\"{Number(CommentId(start.Comment))}\"/>");
    }

    private void CommentMark(HComment comment, StringBuilder sb)
    {
        if (!CommentsHere)
            return;
        var id = Number(CommentId(comment));
        if (_commentsStarted.Contains(comment))
            sb.Append($"<w:commentRangeEnd w:id=\"{id}\"/>");
        sb.Append($"<w:r><w:rPr><w:rStyle w:val=\"CommentReference\"/></w:rPr><w:commentReference w:id=\"{id}\"/></w:r>");
    }

    /// <summary>word/comments.xml, when the document has comments.</summary>
    private Part? CommentsPart()
    {
        if (_commentsPart is null)
            return null;
        _commentsPart.Xml = $"<w:comments {Namespaces}>{string.Concat(_commentXml)}</w:comments>";
        return _commentsPart;
    }

    // ───────────────────────── Tracked changes ─────────────────────────

    /// <summary>w:id, w:author and w:date of a w:ins or w:del.</summary>
    private string RevisionAttributes(HRevision revision) =>
        $" w:id=\"{Number(_nextRevisionId++)}\" w:author=\"{Escape(revision.Author ?? "")}\"{DateAttribute(revision.Date)}";

    private static string DateAttribute(DateTime? date) =>
        date is { } when ? $" w:date=\"{when.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}\"" : "";

    /// <summary>Runs written by <paramref name="write"/>, in a w:ins when <paramref name="format"/> was inserted.</summary>
    private void MaybeInserted(StringBuilder sb, HCharFormat format, Action<StringBuilder> write)
    {
        if (format.Layout?.Inserted is not { } revision)
        {
            write(sb);
            return;
        }
        var runs = new StringBuilder();
        write(runs);
        if (runs.Length == 0)
            return;
        sb.Append("<w:ins").Append(RevisionAttributes(revision)).Append('>').Append(runs).Append("</w:ins>");
    }

    /// <summary>Deleted text as w:del: its text as w:delText, its tabs and line breaks.</summary>
    private void Deletion(HDeletion deletion, StringBuilder sb, RunContext context)
    {
        var runs = new StringBuilder();
        void Write(IEnumerable<HInline> inlines)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case HText { Text.Length: > 0 } text:
                        runs.Append("<w:r>").Append(RunPr(text.Format, context, CharacterStyle(text.Format)))
                            .Append("<w:delText xml:space=\"preserve\">").Append(Escape(text.Text.Replace("\r", "", StringComparison.Ordinal).Replace('\n', ' ')))
                            .Append("</w:delText></w:r>");
                        break;
                    case HTab tab:
                        runs.Append("<w:r>").Append(RunPr(tab.Format, context)).Append("<w:tab/></w:r>");
                        break;
                    case HLineBreak lineBreak:
                        runs.Append("<w:r>").Append(RunPr(lineBreak.Format, context)).Append("<w:br/></w:r>");
                        break;
                    case HLink link:
                        Write(link.Content);
                        break;
                }
            }
        }
        Write(deletion.Content);
        if (runs.Length > 0)
            sb.Append("<w:del").Append(RevisionAttributes(deletion.Revision)).Append('>').Append(runs).Append("</w:del>");
    }
}
