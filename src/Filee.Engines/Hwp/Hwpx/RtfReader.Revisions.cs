// RtfReader: character styles, annotations (comments) and tracked changes.
//
// Character styles are defined in the style sheet as {\*\csN ... name;} and used as \csN in the text, followed by the
// run's own properties (as Word writes them): only the style's name is kept (HCharLayout.Style).
//
// An annotation is on the text between {\*\atrfstart id} and {\*\atrfend id}; its mark follows as
// {\*\atnid initials}{\*\atnauthor name}\chatn{\*\annotation{\*\atnref id}{\*\atndate n} paragraphs}, read into an
// HComment (with an HCommentStart where its text starts). \atndate and \revdttm are DTTM numbers: minutes, hours, day,
// month and years since 1900 in bit fields.
//
// Tracked changes: \revised text was inserted (by \revauthN of the revision table {\*\revtbl {Unknown;}{name;}}, at
// \revdttmN), \deleted text deleted (\revauthdelN, \revdttmdelN). Inserted runs keep their text, marked as inserted;
// deleted text goes into an HDeletion, which only the DOCX and RTF writers write (before, it showed as plain text).

using System.Globalization;
using System.Text;

namespace Filee.Engines.Hwp.Hwpx;

internal sealed partial class RtfReader
{
    /// <summary>The character styles of the style sheet (\*\csN), by number.</summary>
    private readonly Dictionary<int, string> _characterStyles = [];

    /// <summary>The style being defined is a character style (\*\csN).</summary>
    private bool _characterStyle;

    /// <summary>The revision table's authors (\*\revtbl): \revauthN is the Nth.</summary>
    private readonly List<string> _revisionAuthors = [];

    /// <summary>Comments by their \atrfstart / \atnref id.</summary>
    private readonly Dictionary<string, HComment> _comments = new(StringComparer.Ordinal);

    /// <summary>The initials (\atnid) and author (\atnauthor) of the annotation whose \annotation group comes next.</summary>
    private readonly StringBuilder _annotationInitials = new();
    private readonly StringBuilder _annotationAuthor = new();

    /// <summary>The deletion the text in \deleted runs goes into, while it goes on.</summary>
    private HDeletion? _deletion;

    /// <summary>Starts the destinations of this file; false for other words.</summary>
    private bool RevisionDestination(string word, int? parameter)
    {
        switch (word)
        {
            case "cs" when _g.Dest == Dest.StyleSheet:
                _styleId = parameter ?? -1;
                _characterStyle = true;
                return true;
            case "revtbl":
                _g.Dest = Dest.RevisionTable;
                _collected.Clear();
                return true;
            case "atrfstart" or "atrfend" when _g.Dest == Dest.Text:
                _g.Dest = Dest.AnnotationId;
                _collected.Clear();
                var start = word == "atrfstart";
                _g.Closer = () =>
                {
                    if (start && !_g.Char.Hidden && _collected.ToString().Trim() is { Length: > 0 and <= 64 } id)
                        AddInline(new HCommentStart(CommentById(id)));
                };
                return true;
            case "atnid" when _g.Dest == Dest.Text:
                _g.Dest = Dest.AnnotationInitials;
                _annotationInitials.Clear();
                return true;
            case "atnauthor" when _g.Dest == Dest.Text:
                _g.Dest = Dest.AnnotationAuthor;
                _annotationAuthor.Clear();
                return true;
            case "annotation" when _g.Dest == Dest.Text && !_g.Story.Annotation:
                StartAnnotation();
                return true;
            case "atnref" when _g.Story.Annotation:
                _g.Dest = Dest.AnnotationId;
                _collected.Clear();
                var story = _g.Story;
                _g.Closer = () => story.AnnotationId = _collected.ToString().Trim();
                return true;
            case "atndate" when _g.Story.Annotation:
                _g.Dest = Dest.AnnotationId;
                _collected.Clear();
                var dated = _g.Story;
                _g.Closer = () => dated.AnnotationDate = int.TryParse(_collected.ToString().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var dttm) ? Dttm(dttm) : null;
                return true;
            case "annotation" or "atnid" or "atnauthor" or "atnref" or "atndate" or "atrfstart" or "atrfend" or "atnicn" or "atnparent" or "atntime":
                _g.Dest = Dest.Skip;
                return true;
        }
        return false;
    }

    private HComment CommentById(string id)
    {
        if (!_comments.TryGetValue(id, out var comment))
        {
            _budget.Object();
            _comments[id] = comment = new HComment();
        }
        return comment;
    }

    /// <summary>The text of an annotation (\*\annotation): read like a note's, into a comment where its mark is.</summary>
    private void StartAnnotation()
    {
        var parent = _g.Sink ?? _g.Story.Inlines;
        var story = new Story { Annotation = true };
        var (initials, author) = (_annotationInitials.ToString().Trim(), _annotationAuthor.ToString().Trim());
        _annotationInitials.Clear();
        _annotationAuthor.Clear();
        _g.Story = story;
        _g.Sink = null;
        _g.Field = null;
        _g.Para = ParaProps.Default;
        _budget.Object();
        var level = _budget.Enter();
        var group = _g;
        var hidden = _g.Char.Hidden;
        _g.Closer = () =>
        {
            var inner = _g;
            _g = group;
            EndParagraph(story, force: false);
            CloseTable(story);
            _g = inner;
            level.Dispose();
            if (hidden)
                return;
            var comment = story.AnnotationId is { Length: > 0 and <= 64 } id && _comments.TryGetValue(id, out var started) && started.Blocks.Count == 0
                ? started
                : new HComment();
            comment.Author = author.Length > 0 ? author : null;
            comment.Initials = initials.Length > 0 ? initials : null;
            comment.Date = story.AnnotationDate;
            comment.Blocks.AddRange(story.Blocks);
            EndRun(parent);
            parent.Add(comment);
        };
    }

    /// <summary>A DTTM (minutes, hours, day, month, years since 1900, weekday in bit fields) in UTC; null for 0 or no date.</summary>
    internal static DateTime? Dttm(int value)
    {
        var minute = value & 0x3F;
        var hour = value >> 6 & 0x1F;
        var day = value >> 11 & 0x1F;
        var month = value >> 16 & 0x0F;
        var year = 1900 + (value >> 20 & 0x1FF);
        if (value == 0 || minute > 59 || hour > 23 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
            return null;
        return new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc);
    }

    /// <summary>A revision of the revision table's author <paramref name="author"/> at DTTM <paramref name="date"/>.</summary>
    private HRevision Revision(int author, int date) =>
        new(author > 0 && author < _revisionAuthors.Count ? _revisionAuthors[author] : null, Dttm(date));

    /// <summary>A revision table entry's text ("name;").</summary>
    private void RevisionAuthor(string text)
    {
        foreach (var c in text)
        {
            if (c != ';')
            {
                if (_collected.Length < 256)
                    _collected.Append(c);
                continue;
            }
            Definitions(_revisionAuthors.Count, "revision authors");
            _revisionAuthors.Add(_collected.ToString().Trim());
            _collected.Clear();
        }
    }

    /// <summary>
    /// The inlines text and tabs of the current run go into: the deletion it is in (one for neighbouring deleted runs of
    /// one revision), else the link's or the paragraph's.
    /// </summary>
    private List<HInline> TextSink()
    {
        var inlines = _g.Sink ?? _g.Story.Inlines;
        if (!_g.Char.Deleted)
        {
            EndDeletion();
            return inlines;
        }
        var revision = Revision(_g.Char.DeleteAuthor, _g.Char.DeleteDate);
        if (_deletion is { } open && open.Revision == revision && inlines.Count > 0 && ReferenceEquals(inlines[^1], open))
            return open.Content;
        EndDeletion();
        EndRun(inlines);
        _deletion = new HDeletion(revision);
        inlines.Add(_deletion);
        return _deletion.Content;
    }

    /// <summary>Ends the deletion text was going into.</summary>
    private void EndDeletion()
    {
        if (_deletion is null)
            return;
        EndRun(_deletion.Content);
        _deletion = null;
    }

    /// <summary>The run's character style and tracked insertion, as the model keeps them.</summary>
    private HCharLayout? RevisionLayout(CharProps c, HCharLayout? layout)
    {
        var style = c.CharStyle >= 0 ? _characterStyles.GetValueOrDefault(c.CharStyle) : null;
        if (style is null && !c.Inserted)
            return layout;
        return (layout ?? new HCharLayout()) with { Style = style, Inserted = c.Inserted ? Revision(c.InsertAuthor, c.InsertDate) : null };
    }
}
