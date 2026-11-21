// RtfWriter: character styles, annotations (comments) and tracked changes.
//
// A run's character style (HCharLayout.Style) is \csN, with {\*\csN \additive name;} in the style sheet and no
// properties of its own: the run's formatting follows \csN, as Word writes it. A comment's text is marked by
// {\*\atrfstart id} and {\*\atrfend id}, its mark and text follow as {\*\atnid initials}{\*\atnauthor name}\chatn
// {\*\annotation{\*\atnref id}{\*\atndate dttm} paragraphs}; comments are only written in the body and its tables.
// Inserted runs are \revised (\revauthN of the revision table, \revdttmN), deleted text \deleted (\revauthdelN,
// \revdttmdelN). Dates are DTTM numbers (minutes, hours, day, month, years since 1900, weekday in bit fields).

namespace Filee.Engines.Hwp.Hwpx;

internal sealed partial class RtfWriter
{
    /// <summary>The first character style's number: \s0 to \s9 are Normal and the headings.</summary>
    private const int FirstCharacterStyle = 20;

    private readonly Dictionary<string, int> _characterStyles = new(StringComparer.Ordinal);

    /// <summary>The revision table's authors after its first entry, "Unknown".</summary>
    private readonly List<string> _revisionAuthors = [];
    private readonly Dictionary<string, int> _revisionAuthorIndex = new(StringComparer.Ordinal);

    private readonly Dictionary<HComment, int> _commentIds = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<HComment> _commentsStarted = new(ReferenceEqualityComparer.Instance);

    /// <summary>The annotation being written still needs its mark (\chatn) in front of its first paragraph.</summary>
    private bool _annotationMark;

    /// <summary>\csN of a run's character style, defined on first use; "" for none.</summary>
    private string CharacterStyle(HCharFormat format)
    {
        if (format.Layout?.Style is not { } name || string.IsNullOrWhiteSpace(name))
            return "";
        name = name.Replace(";", "", StringComparison.Ordinal).Replace("{", "", StringComparison.Ordinal).Replace("}", "", StringComparison.Ordinal).Trim();
        if (name.Length == 0)
            return "";
        if (!_characterStyles.TryGetValue(name, out var number))
        {
            CheckEntries(_characterStyles.Count, "styles");
            _characterStyles[name] = number = FirstCharacterStyle + _characterStyles.Count;
        }
        return $"\\cs{Number(number)}";
    }

    /// <summary>The character styles of the style sheet.</summary>
    private void CharacterStyleSheet()
    {
        var o = _out;
        foreach (var (name, number) in _characterStyles)
        {
            o.Line();
            o.Open();
            o.Control($"\\*\\cs{Number(number)}\\additive");
            o.Text(name, 1252);
            o.Raw(";");
            o.Close();
        }
    }

    /// <summary>The revision table, when runs were inserted or deleted.</summary>
    private void RevisionTable()
    {
        if (_revisionAuthorIndex.Count == 0)
            return;
        var o = _out;
        o.Open();
        o.Control("\\*\\revtbl");
        foreach (var author in _revisionAuthors.Prepend("Unknown"))
        {
            o.Open();
            o.Text(author, 1252);
            o.Raw(";");
            o.Close();
        }
        o.Close();
        o.Line();
    }

    private int RevisionAuthor(string? author)
    {
        // Entry 0 of the table stands for an author nobody named.
        if (string.IsNullOrWhiteSpace(author))
        {
            _revisionAuthorIndex.TryAdd("", 0);
            return 0;
        }
        author = author.Replace(";", ",", StringComparison.Ordinal).Trim();
        if (_revisionAuthorIndex.TryGetValue(author, out var index))
            return index;
        CheckEntries(_revisionAuthors.Count, "revision authors");
        _revisionAuthors.Add(author);
        return _revisionAuthorIndex[author] = _revisionAuthors.Count;
    }

    /// <summary>A date as a DTTM number; 0 for none or one outside 1900–2411.</summary>
    internal static int Dttm(DateTime? date)
    {
        if (date is not { } value)
            return 0;
        value = value.ToUniversalTime();
        if (value.Year is < 1900 or > 2411)
            return 0;
        return value.Minute | value.Hour << 6 | value.Day << 11 | value.Month << 16 | (value.Year - 1900) << 20 | (int)value.DayOfWeek << 29;
    }

    /// <summary>The style and tracked insertion words of a run.</summary>
    private string RunWords(HCharFormat format)
    {
        var words = CharacterStyle(format);
        if (format.Layout?.Inserted is { } inserted)
        {
            words += $"\\revised\\revauth{Number(RevisionAuthor(inserted.Author))}";
            if (Dttm(inserted.Date) is not 0 and var date)
                words += $"\\revdttm{Number(date)}";
        }
        return words;
    }

    /// <summary>Deleted text: each piece a group marked \deleted.</summary>
    private void Deletion(HDeletion deletion, RunState state, StyleBase style)
    {
        var words = $"\\deleted\\revauthdel{Number(RevisionAuthor(deletion.Revision.Author))}";
        if (Dttm(deletion.Revision.Date) is not 0 and var date)
            words += $"\\revdttmdel{Number(date)}";
        void Write(IEnumerable<HInline> inlines)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case HText { Text.Length: > 0 } text:
                        Run(text.Format, state, style, () => Text(text.Text, Resolve(text.Format, style), state.Font), words);
                        break;
                    case HTab tab:
                        Run(tab.Format, state, style, () => _out.Control("\\tab"), words);
                        break;
                    case HLineBreak lineBreak:
                        Run(lineBreak.Format, state, style, () => _out.Control("\\line"), words);
                        break;
                    case HLink link:
                        Write(link.Content);
                        break;
                }
            }
        }
        Write(deletion.Content);
    }

    // ───────────────────────── Annotations ─────────────────────────

    private int CommentId(HComment comment)
    {
        if (!_commentIds.TryGetValue(comment, out var id))
            _commentIds[comment] = id = _commentIds.Count + 1;
        return id;
    }

    private void CommentStart(HCommentStart start, Story story)
    {
        if (story != Story.Body || !_commentsStarted.Add(start.Comment))
            return;
        _out.Open();
        _out.Control("\\*\\atrfstart");
        _out.Raw(Number(CommentId(start.Comment)));
        _out.Close();
    }

    private void Comment(HComment comment, Story story)
    {
        if (story != Story.Body)
            return;
        var o = _out;
        var id = Number(CommentId(comment));
        if (_commentsStarted.Contains(comment))
        {
            o.Open();
            o.Control("\\*\\atrfend");
            o.Raw(id);
            o.Close();
        }
        o.Open();
        o.Open();
        o.Control("\\*\\atnid");
        o.Text(comment.Initials ?? Initials(comment.Author), 1252);
        o.Close();
        o.Open();
        o.Control("\\*\\atnauthor");
        o.Text(comment.Author ?? "", 1252);
        o.Close();
        o.Control("\\chatn");
        o.Open();
        o.Control("\\*\\annotation");
        o.Open();
        o.Control("\\*\\atnref");
        o.Raw(id);
        o.Close();
        if (Dttm(comment.Date) is not 0 and var date)
        {
            o.Open();
            o.Control("\\*\\atndate");
            o.Raw(Number(date));
            o.Close();
        }
        var outer = _width;
        _annotationMark = true;
        Blocks(comment.Blocks.Count > 0 ? comment.Blocks : [new HParagraph()], Story.Note, 0, BlockEnd.Story);
        _annotationMark = false;
        _width = outer;
        o.Close();
        o.Close();
    }

    /// <summary>The first letters of an author's names, as Word makes initials.</summary>
    private static string Initials(string? author) =>
        string.IsNullOrWhiteSpace(author) ? "" : string.Concat(author.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w[..1]).Take(9));
}
