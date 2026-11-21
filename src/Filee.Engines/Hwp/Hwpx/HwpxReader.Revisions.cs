// HwpxReader: tracked changes (변경 추적) and memos (메모).
//
// The header lists the changes (hh:trackChange: its type, date and author id) and their authors
// (hh:trackChangeAuthor). In the text, hp:insertBegin / hp:insertEnd and hp:deleteBegin / hp:deleteEnd mark where a
// change starts and ends, naming it by TcId. Inserted text is read as it is, marked as inserted (HCharLayout.Inserted);
// deleted text, which the printed document doesn't show, goes into an HDeletion, which the DOCX, RTF, ODT and HWPX
// writers write. A memo is a MEMO field: its text (the field's hp:subList) is a comment on the text up to the field's end,
// its author and date from the field's parameters where they are given.

using System.Xml.Linq;

namespace Filee.Engines.Hwp.Hwpx;

internal sealed partial class HwpxReader
{
    private readonly Dictionary<string, XElement> _trackChanges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _trackChangeAuthors = new(StringComparer.Ordinal);

    /// <summary>The tracked insertion text is in (hp:insertBegin … hp:insertEnd), or null.</summary>
    private HRevision? _inserted;

    /// <summary>The tracked deletion deleted text goes into, and the paragraph it is in.</summary>
    private HRevision? _deletionRevision;
    private HDeletion? _deletion;
    private HParagraph? _deletionParagraph;

    /// <summary>Remembers the header's changes and authors (called for each header element).</summary>
    private void TrackChangeHeader(XElement element, string? id)
    {
        if (id is null)
            return;
        switch (element.Name.LocalName)
        {
            case "trackChange":
                _trackChanges.TryAdd(id, element);
                break;
            case "trackChangeAuthor" when Attr(element, "name") is { Length: > 0 } name:
                _trackChangeAuthors.TryAdd(id, name);
                break;
        }
    }

    /// <summary>The change a mark (hp:insertBegin, hp:deleteBegin) names: its author and date as the header gives them.</summary>
    private HRevision Revision(XElement mark)
    {
        var change = (Attr(mark, "TcId") ?? Attr(mark, "tcId")) is { } id ? _trackChanges.GetValueOrDefault(id) : null;
        var author = (Attr(change, "authorID") ?? Attr(change, "authorId")) is { } authorId ? _trackChangeAuthors.GetValueOrDefault(authorId) : null;
        return new HRevision(author, Docx.DocxReader.Date(Attr(change, "date")));
    }

    /// <summary>The run format, marked as inserted inside a tracked insertion.</summary>
    private HCharFormat Inserted(HCharFormat format) =>
        _inserted is { } revision ? format with { Layout = (format.Layout ?? new HCharLayout()) with { Inserted = revision } } : format;

    /// <summary>Deleted text or a tab, into the deletion of the paragraph being built.</summary>
    private void AddDeleted(ParagraphBuilder builder, HInline inline)
    {
        if (_deletionRevision is not { } revision || _fields.Exists(f => f.Guide))
            return;
        if (_deletion is null || !ReferenceEquals(_deletionParagraph, builder.Current))
        {
            _deletion = new HDeletion(revision);
            _deletionParagraph = builder.Current;
            builder.Add(_deletion);
        }
        var content = _deletion.Content;
        if (inline is HText text && content.Count > 0 && content[^1] is HText last && last.Format == text.Format)
            content[^1] = new HText(last.Text + text.Text, last.Format);
        else
            content.Add(inline);
    }

    private void DeleteBegin(XElement mark)
    {
        _deleted = true;
        _deletionRevision = Revision(mark);
        _deletion = null;
    }

    private void DeleteEnd()
    {
        _deleted = false;
        _deletionRevision = null;
        _deletion = null;
        _deletionParagraph = null;
    }

    /// <summary>A memo's comment from its MEMO field: its text and, where the parameters give them, author and date.</summary>
    private HComment Memo(XElement field)
    {
        var parameters = Descendants(field, "stringParam").ToList();
        string? Parameter(params string[] names) =>
            parameters.FirstOrDefault(p => names.Contains(Attr(p, "name"), StringComparer.OrdinalIgnoreCase))?.Value.Trim() is { Length: > 0 } value ? value : null;
        var comment = new HComment
        {
            Author = Parameter("Author", "MemoAuthor"),
            Date = Docx.DocxReader.Date(Parameter("CreateDateTime", "Date", "MemoDate")),
        };
        _budget.Object();
        var (inserted, deletion) = (_inserted, _deletionRevision);
        (_inserted, _deletionRevision) = (null, null);
        try
        {
            comment.Blocks.AddRange(ReadSubList(Child(field, "subList")));
        }
        finally
        {
            (_inserted, _deletionRevision) = (inserted, deletion);
        }
        return comment;
    }
}
