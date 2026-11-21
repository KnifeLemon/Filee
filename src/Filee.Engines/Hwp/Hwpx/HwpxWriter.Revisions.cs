// HwpxWriter: memos (메모) and tracked changes (변경 추적), as HwpxReader reads them back.
//
// A comment is a MEMO field: its fieldBegin (with the author and date as "Author" and "CreateDateTime" string
// parameters and the comment's paragraphs in an hp:subList) where the text it is on starts (HCommentStart), its
// fieldEnd where it ends (HComment); a comment without a start has both at its mark. Memos are written in the body
// text, tables and text boxes, not in notes, headers, footers or other memos.
//
// The header lists each change (hh:trackChange: Insert or Delete, its date and its author's id) and the authors
// (hh:trackChangeAuthor) in hh:refList. In the text, inserted text lies between an hp:insertBegin and an hp:insertEnd
// naming its change (TcId), deleted text (HDeletion) between an hp:deleteBegin and an hp:deleteEnd. The marks go in
// the run's hp:t, like 한글's highlighter marks.
//
// The element and parameter names follow the reader (W37), which follows OWPML as read; neither has been checked on a
// file 한글 saved with memos or tracked changes.

using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Filee.Engines.Hwp.Hwpx;

internal sealed partial class HwpxWriter
{
    /// <summary>A MEMO field's fieldid: its control id '%%me' as a number, as <see cref="HyperlinkFieldId"/> is '%hlk'.</summary>
    private const string MemoFieldId = "623209829";

    /// <summary>The tracked changes written so far: type and revision, in id order (ids from 1).</summary>
    private readonly List<(string Type, HRevision Revision)> _trackChanges = [];
    private readonly List<string> _trackChangeAuthors = [];

    /// <summary>The field id of each memo whose fieldBegin is written and whose fieldEnd is still to come.</summary>
    private readonly Dictionary<HComment, int> _openMemos = new(ReferenceEqualityComparer.Instance);

    /// <summary>Memos are written here (not inside notes, headers, footers or other memos).</summary>
    private bool _memosAllowed = true;

    /// <summary>A new change of <paramref name="type"/> (Insert, Delete): its id.</summary>
    private int TrackChange(string type, HRevision revision)
    {
        _trackChanges.Add((type, revision));
        return _trackChanges.Count;
    }

    /// <summary>The change marks around inserted or deleted content: hp:insertBegin / hp:deleteBegin … End.</summary>
    private static (string Begin, string End) ChangeMarks(string kind, int id) =>
        ($"<hp:{kind}Begin Id=\"{id}\" TcId=\"{id}\" paraend=\"0\"/>", $"<hp:{kind}End Id=\"{id}\" TcId=\"{id}\" paraend=\"0\"/>");

    /// <summary><paramref name="xml"/> (text, a tab or a line break) in the marks of an insertion when it is inserted.</summary>
    private string Insertion(HCharFormat format, string xml)
    {
        if (format.Layout?.Inserted is not { } revision)
            return xml;
        var (begin, end) = ChangeMarks("insert", TrackChange("Insert", revision));
        return begin + xml + end;
    }

    /// <summary>A tracked deletion: its text, tabs and line breaks between deletion marks (not in the plain text).</summary>
    private void Deletion(HDeletion deletion, RunBuilder runs, string charPrBase, bool link)
    {
        var content = deletion.Content.Where(i => i is (HText or HTab or HLineBreak) and not HEquation).ToList();
        if (content.Count == 0)
            return;
        var (begin, end) = ChangeMarks("delete", TrackChange("Delete", deletion.Revision));
        for (var i = 0; i < content.Count; i++)
        {
            var (format, xml) = content[i] switch
            {
                HText text => (text.Format, Escape(text.Text)),
                HTab tab => (tab.Format, "<hp:tab width=\"4000\" leader=\"0\" type=\"1\"/>"),
                HLineBreak lineBreak => (lineBreak.Format, "<hp:lineBreak/>"),
                _ => (default(HCharFormat), ""),
            };
            // Each piece in its own run's text when the formats differ; the marks around all of it.
            runs.Text(CharPr(charPrBase, format, link), (i == 0 ? begin : "") + xml + (i == content.Count - 1 ? end : ""));
        }
    }

    /// <summary>The fieldBegin of a memo: its author and date, and its paragraphs.</summary>
    private string MemoBegin(HComment comment, int id)
    {
        var parameters = new StringBuilder();
        var count = 0;
        if (comment.Author is { } author)
        {
            parameters.Append($"<hp:stringParam name=\"Author\">{Escape(author)}</hp:stringParam>");
            count++;
        }
        if (comment.Date is { } date)
        {
            parameters.Append($"<hp:stringParam name=\"CreateDateTime\">{date.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}</hp:stringParam>");
            count++;
        }
        // The memo's paragraphs only (no tables, objects, notes or memos in a memo).
        var blocks = comment.Blocks.OfType<HParagraph>().Select(Memoable).ToList<HBlock>();
        var allowed = _memosAllowed;
        _memosAllowed = false;
        string text;
        try
        {
            text = SubList(blocks, "TOP");
        }
        finally
        {
            _memosAllowed = allowed;
        }
        return $"<hp:ctrl><hp:fieldBegin id=\"{id}\" type=\"MEMO\" name=\"\" editable=\"1\" dirty=\"0\" zorder=\"-1\" fieldid=\"{MemoFieldId}\">" +
               $"<hp:parameters cnt=\"{count}\" name=\"\">{parameters}</hp:parameters>{text}</hp:fieldBegin></hp:ctrl>";
    }

    private static string MemoEnd(int id) => $"<hp:ctrl><hp:fieldEnd beginIDRef=\"{id}\" fieldid=\"{MemoFieldId}\"/></hp:ctrl>";

    /// <summary>A memo's paragraph with its text, tabs, line breaks and links, without change marks.</summary>
    private static HParagraph Memoable(HParagraph paragraph)
    {
        static HCharFormat Plain(HCharFormat format) =>
            format.Layout is { Inserted: not null } layout ? format with { Layout = layout with { Inserted = null } } : format;
        var copy = new HParagraph { Format = paragraph.Format, MarkFormat = paragraph.MarkFormat, List = paragraph.List };
        foreach (var inline in paragraph.Inlines)
        {
            HInline? kept = inline switch
            {
                HEquation => null,
                HText text => new HText(text.Text, Plain(text.Format)),
                HTab tab => new HTab(Plain(tab.Format)),
                HLineBreak lineBreak => new HLineBreak(Plain(lineBreak.Format)),
                HLink or HField => inline,
                _ => null,
            };
            if (kept is not null)
                copy.Inlines.Add(kept);
        }
        return copy;
    }

    private void CommentStart(HCommentStart start, RunBuilder runs, string charPrBase)
    {
        if (!_memosAllowed || _openMemos.ContainsKey(start.Comment))
            return;
        var id = NextId();
        _openMemos[start.Comment] = id;
        runs.Control(charPrBase, MemoBegin(start.Comment, id));
    }

    private void CommentEnd(HComment comment, RunBuilder runs, string charPrBase)
    {
        if (!_memosAllowed)
            return;
        if (_openMemos.Remove(comment, out var id))
        {
            runs.Control(charPrBase, MemoEnd(id));
            return;
        }
        // A comment on a point: the field begins and ends at its mark.
        id = NextId();
        runs.Control(charPrBase, MemoBegin(comment, id) + MemoEnd(id));
    }

    /// <summary>Runs <paramref name="write"/> where memos are not written (notes, headers and footers).</summary>
    private T WithoutMemos<T>(Func<T> write)
    {
        var allowed = _memosAllowed;
        _memosAllowed = false;
        try
        {
            return write();
        }
        finally
        {
            _memosAllowed = allowed;
        }
    }

    /// <summary>hh:trackChanges and hh:trackChangeAuthors at the end of hh:refList, when changes were written.</summary>
    private void TrackChangeHeader()
    {
        if (_trackChanges.Count == 0)
            return;
        var refList = _head.Element(Hh + "refList") ?? _head;
        var changes = new XElement(Hh + "trackChanges", new XAttribute("itemCnt", _trackChanges.Count));
        for (var i = 0; i < _trackChanges.Count; i++)
        {
            var (type, revision) = _trackChanges[i];
            var change = new XElement(Hh + "trackChange", new XAttribute("type", type));
            if (revision.Date is { } date)
                change.Add(new XAttribute("date", date.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)));
            change.Add(new XAttribute("authorID", Author(revision.Author ?? "Unknown")), new XAttribute("id", i + 1));
            changes.Add(change);
        }
        var authors = new XElement(Hh + "trackChangeAuthors", new XAttribute("itemCnt", _trackChangeAuthors.Count),
            _trackChangeAuthors.Select((name, i) => new XElement(Hh + "trackChangeAuthor", new XAttribute("name", name), new XAttribute("id", i + 1))));
        refList.Add(changes, authors);

        int Author(string name)
        {
            var index = _trackChangeAuthors.IndexOf(name);
            if (index < 0)
            {
                _trackChangeAuthors.Add(name);
                index = _trackChangeAuthors.Count - 1;
            }
            return index + 1;
        }
    }
}
