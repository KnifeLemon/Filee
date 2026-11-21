// OdtWriter: comments (office:annotation) and tracked changes (text:tracked-changes), as OdtReader reads them back.
//
// A comment on a stretch of text is an office:annotation with an office:name where the stretch starts (HCommentStart)
// and an office:annotation-end of that name where it ends (HComment); a comment without a start is an annotation on
// its point. Its author (dc:creator), date (dc:date), initials (meta:creator-initials, ODF 1.3) and paragraphs go in it.
// Comments are written in the body text and its tables only (as LibreOffice keeps them); elsewhere they are left out.
//
// Each tracked insertion and deletion is a text:changed-region of text:tracked-changes, the first element of the text,
// with its office:change-info (author, date). Inserted text (HCharLayout.Inserted) lies between a text:change-start
// and a text:change-end of its region; a deletion (HDeletion) is a text:change mark where the text was, its text kept
// in the region as a paragraph.

using System.Globalization;
using System.Text;

namespace Filee.Engines.Hwp.Hwpx.Odt;

internal sealed partial class OdtWriter
{
    /// <summary>The text:changed-region elements written so far.</summary>
    private readonly StringBuilder _trackedChanges = new();
    private int _changeIds;

    /// <summary>The office:name of each comment whose start is written and whose end is still to come.</summary>
    private readonly Dictionary<HComment, string> _openComments = new(ReferenceEqualityComparer.Instance);
    private int _annotations;

    /// <summary>text:tracked-changes with the regions written; empty without any.</summary>
    private string TrackedChanges() =>
        _trackedChanges.Length == 0 ? "" : $"<text:tracked-changes>{_trackedChanges}</text:tracked-changes>";

    /// <summary>office:change-info: the author and the date (UTC, as ODF's dateTime without a zone).</summary>
    private static string ChangeInfo(HRevision revision) =>
        "<office:change-info>" +
        $"<dc:creator>{Escape(revision.Author ?? "Unknown")}</dc:creator>" +
        (revision.Date is { } date ? $"<dc:date>{Date(date)}</dc:date>" : "") +
        "</office:change-info>";

    private static string Date(DateTime date) => date.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>A new region of <paramref name="kind"/> (insertion or deletion) with <paramref name="content"/>; its id.</summary>
    private string ChangedRegion(string kind, HRevision revision, string content = "")
    {
        var id = $"ct{(++_changeIds).ToString(CultureInfo.InvariantCulture)}";
        _trackedChanges.Append($"<text:changed-region text:id=\"{id}\"><text:{kind}>{ChangeInfo(revision)}{content}</text:{kind}></text:changed-region>");
        return id;
    }

    /// <summary>Writes an inline marked as inserted between the change marks of an insertion of its own.</summary>
    private void Inserted(StringBuilder sb, HRevision revision, Action content)
    {
        var id = ChangedRegion("insertion", revision);
        sb.Append($"<text:change-start text:change-id=\"{id}\"/>");
        content();
        sb.Append($"<text:change-end text:change-id=\"{id}\"/>");
    }

    /// <summary>A tracked deletion: its text in a region, a text:change mark here.</summary>
    private void Deletion(StringBuilder sb, HDeletion deletion, TextState state)
    {
        var content = deletion.Content.Where(i => i is (HText or HTab or HLineBreak) and not HEquation).ToList();
        if (content.Count == 0)
            return;
        var text = new StringBuilder("<text:p>");
        // The deleted text without insertion marks of its own (a region holds no change marks).
        Inlines(content.Select(Uninserted), text, new TextState(state.Mark), Context.Note);
        text.Append("</text:p>");
        var id = ChangedRegion("deletion", deletion.Revision, text.ToString());
        sb.Append($"<text:change text:change-id=\"{id}\"/>");
    }

    private static HInline Uninserted(HInline inline) => inline switch
    {
        HText { Format.Layout.Inserted: not null } text => new HText(text.Text, Plain(text.Format)),
        HTab { Format.Layout.Inserted: not null } tab => new HTab(Plain(tab.Format)),
        HLineBreak { Format.Layout.Inserted: not null } lineBreak => new HLineBreak(Plain(lineBreak.Format)),
        _ => inline,
    };

    private static HCharFormat Plain(HCharFormat format) => format with { Layout = format.Layout! with { Inserted = null } };

    /// <summary>The start of a comment on a stretch of text: the annotation, named for its end.</summary>
    private void CommentStart(StringBuilder sb, HCommentStart start, Context context)
    {
        if (context != Context.Body || _openComments.ContainsKey(start.Comment))
            return;
        var name = $"__Annotation__{(++_annotations).ToString(CultureInfo.InvariantCulture)}";
        _openComments[start.Comment] = name;
        sb.Append(Annotation(start.Comment, name));
    }

    /// <summary>A comment's mark: the end of its stretch, or an annotation on this point when its start wasn't written.</summary>
    private void CommentEnd(StringBuilder sb, HComment comment, Context context)
    {
        if (context != Context.Body)
            return;
        if (_openComments.Remove(comment, out var name))
            sb.Append($"<office:annotation-end office:name=\"{name}\"/>");
        else
            sb.Append(Annotation(comment, null));
    }

    private string Annotation(HComment comment, string? name)
    {
        var sb = new StringBuilder(name is null ? "<office:annotation>" : $"<office:annotation office:name=\"{name}\">");
        if (comment.Author is { } author)
            sb.Append($"<dc:creator>{Escape(author)}</dc:creator>");
        if (comment.Date is { } date)
            sb.Append($"<dc:date>{Date(date)}</dc:date>");
        if (comment.Initials is { } initials)
            sb.Append($"<meta:creator-initials>{Escape(initials)}</meta:creator-initials>");
        // Paragraphs and lists only: a comment holds no tables, frames or notes.
        var blocks = comment.Blocks.OfType<HParagraph>().Select(Commentable).ToList<HBlock>();
        sb.Append(Nested(_width, () => Blocks(blocks.Count > 0 ? blocks : [new HParagraph()], "Standard", Context.Note)));
        return sb.Append("</office:annotation>").ToString();
    }

    /// <summary>A comment's paragraph with its text, tabs, line breaks and links (no objects, notes or comments).</summary>
    private static HParagraph Commentable(HParagraph paragraph)
    {
        var copy = new HParagraph { Format = paragraph.Format, MarkFormat = paragraph.MarkFormat, List = paragraph.List };
        copy.Inlines.AddRange(paragraph.Inlines.Where(i => i is HText or HTab or HLineBreak or HLink or HField).Select(Uninserted));
        return copy;
    }
}
