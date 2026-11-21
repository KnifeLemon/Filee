// Comments and tracked changes of a Word document.
//
// A comment (word/comments.xml w:comment) is on the text between its w:commentRangeStart and w:commentRangeEnd; its
// mark, the run with w:commentReference, follows the range's end. The model keeps the start (HCommentStart) and the
// comment itself where the mark is (HComment), its paragraphs read like a note's. Comments in comments are not followed.
//
// Text inserted while changes were tracked (w:ins, w:moveTo) is read as it is, its runs marked with who inserted it and
// when (HCharLayout.Inserted). Deleted text (w:del, w:moveFrom, its runs' w:delText) becomes an HDeletion holding the
// text, which only the DOCX and RTF writers write: the layout and the other writers show the document with the
// deletions made, as Filee did before. Changes of formatting (w:rPrChange ...) and of paragraph marks are not kept.

using System.Globalization;
using System.Xml.Linq;

namespace Filee.Engines.Hwp.Hwpx.Docx;

internal sealed partial class DocxReader
{
    private Part? _commentsPart;
    private readonly Dictionary<string, XElement> _commentElements = [];
    private readonly Dictionary<string, HComment> _comments = [];

    /// <summary>Inside a comment's paragraphs: comments there are not followed.</summary>
    private bool _inComment;

    /// <summary>The tracked insertion (w:ins) being read, or null.</summary>
    private HRevision? _insertion;

    /// <summary>The tracked deletion (w:del) being read, or null.</summary>
    private HDeletion? _deletion;

    private void LoadComments(Part main)
    {
        _commentsPart = RelatedPart(main, "/comments");
        foreach (var comment in _commentsPart?.Xml.Root?.Elements(W + "comment") ?? [])
            _commentElements.TryAdd(Attr(comment, "id") ?? "", comment);
    }

    /// <summary>The comment with the id of <paramref name="marker"/> (a range start or reference), read the first time; null for none.</summary>
    private HComment? Comment(XElement marker)
    {
        var id = Attr(marker, "id") ?? "";
        if (_comments.TryGetValue(id, out var known))
            return known;
        if (_inComment || _commentsPart is not { } part || !_commentElements.TryGetValue(id, out var element))
            return null;
        var comment = new HComment
        {
            Author = Attr(element, "author") is { Length: > 0 } author ? author : null,
            Initials = Attr(element, "initials") is { Length: > 0 } initials ? initials : null,
            Date = Date(Attr(element, "date")),
        };
        _comments[id] = comment;
        _budget.Object();
        _inComment = true;
        var tableStyleLayer = _tableStyleLayer;
        _tableStyleLayer = null;
        var (insertion, deletion) = (_insertion, _deletion);
        (_insertion, _deletion) = (null, null);
        using (_budget.Enter())
        {
            try
            {
                WithPart(part, () =>
                {
                    ReadNestedBlocks(element, comment.Blocks);
                    return 0;
                });
            }
            finally
            {
                _inComment = false;
                _tableStyleLayer = tableStyleLayer;
                (_insertion, _deletion) = (insertion, deletion);
            }
        }
        return comment;
    }

    /// <summary>Who made a tracked change (w:ins, w:del ...) and when.</summary>
    private static HRevision Revision(XElement change) =>
        new(Attr(change, "author") is { Length: > 0 } author ? author : null, Date(Attr(change, "date")));

    /// <summary>A w:date (xsd:dateTime) in UTC; null when it is missing or not a date.</summary>
    internal static DateTime? Date(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date)
            ? DateTime.SpecifyKind(date, DateTimeKind.Utc)
            : null;

    /// <summary>The paragraph content of a tracked insertion, its runs marked as inserted.</summary>
    private void Inserted(XElement insertion, ParagraphBuilder builder, RunProps styleRun)
    {
        var outer = _insertion;
        _insertion = _deletion is null ? Revision(insertion) : null;
        try
        {
            ParagraphContent(insertion.Elements(), builder, styleRun);
        }
        finally
        {
            _insertion = outer;
        }
    }

    /// <summary>The paragraph content of a tracked deletion: its text into an <see cref="HDeletion"/>.</summary>
    private void Deleted(XElement deletion, ParagraphBuilder builder, RunProps styleRun)
    {
        if (_deletion is not null)
        {
            ParagraphContent(deletion.Elements(), builder, styleRun);
            return;
        }
        var deleted = new HDeletion(Revision(deletion));
        builder.Add(deleted);
        _deletion = deleted;
        builder.OpenSink(deleted.Content);
        try
        {
            ParagraphContent(deletion.Elements(), builder, styleRun);
        }
        finally
        {
            builder.CloseSink(deleted.Content);
            _deletion = null;
        }
    }

    /// <summary>The run format with the character style's name and the tracked insertion it is in.</summary>
    private HCharFormat WithRunStyle(HCharFormat format, XElement? rPr)
    {
        var style = Val(rPr?.Element(W + "rStyle")) is { } id ? _styles.GetValueOrDefault(id) is { Type: "character" } named ? named.Name ?? id : null : null;
        if (style is null && _insertion is null)
            return format;
        return format with { Layout = (format.Layout ?? new HCharLayout()) with { Style = style, Inserted = _insertion } };
    }

    /// <summary>
    /// Run content kept in a deletion: its text, tabs and line breaks (deleted pictures, page breaks and notes go), and
    /// field characters, so a field that ends in deleted text still ends.
    /// </summary>
    private static bool KeptInDeletion(string name) =>
        name is "t" or "delText" or "tab" or "ptab" or "cr" or "noBreakHyphen" or "sym" or "br" or "fldChar" or "instrText" or "delInstrText";
}
