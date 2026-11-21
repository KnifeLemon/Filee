// OdtReader: annotations (comments) and tracked changes.
//
// An office:annotation stands where its text starts; one with an office:name is on the text up to the
// office:annotation-end of that name. The model keeps the comment where its text ends (HComment), with an
// HCommentStart where it starts; an annotation without an end is on its point. Its author (dc:creator), date (dc:date),
// initials (meta:creator-initials, LibreOffice's loext:sender-initials) and paragraphs are read.
//
// Tracked changes are listed in text:tracked-changes, each text:changed-region an insertion, a deletion or a change of
// formatting with its office:change-info. Inserted text lies between text:change-start and text:change-end and is
// marked as inserted (HCharLayout.Inserted); a deletion's text, kept in the region, stands at its text:change mark as
// an HDeletion. Deleted paragraphs (a text:change between paragraphs) are not kept.

using System.Xml.Linq;

namespace Filee.Engines.Hwp.Hwpx.Odt;

internal sealed partial class OdtReader
{
    private static readonly XNamespace Meta = Odf.Meta;

    private enum ChangeKind
    {
        Insertion,
        Deletion,
        Other,
    }

    private readonly Dictionary<string, (ChangeKind Kind, HRevision Revision, XElement Change)> _changes = new(StringComparer.Ordinal);

    /// <summary>The insertion text is in (between its text:change-start and text:change-end), or null.</summary>
    private (string Id, HRevision Revision)? _insertion;

    /// <summary>The names of the annotations that end somewhere (office:annotation-end).</summary>
    private readonly HashSet<string> _annotationEnds = new(StringComparer.Ordinal);

    /// <summary>Annotations whose text started and whose end is still to come, by name.</summary>
    private readonly Dictionary<string, HComment> _openAnnotations = new(StringComparer.Ordinal);

    /// <summary>Inside an annotation: annotations in it are not read (ODF has none there).</summary>
    private bool _inAnnotation;

    private void LoadChanges(XElement text)
    {
        foreach (var region in text.Element(Text + "tracked-changes")?.Elements(Text + "changed-region") ?? [])
        {
            if ((Attr(region, Text + "id") ?? Attr(region, XNamespace.Xml + "id")) is not { Length: > 0 } id
                || region.Elements().FirstOrDefault(e => e.Name.Namespace == Text) is not { } change)
                continue;
            var info = change.Element(Office + "change-info");
            var author = info?.Element(Dc + "creator")?.Value.Trim();
            var revision = new HRevision(author is { Length: > 0 } ? author : null, Docx.DocxReader.Date(info?.Element(Dc + "date")?.Value));
            var kind = change.Name.LocalName switch
            {
                "insertion" => ChangeKind.Insertion,
                "deletion" => ChangeKind.Deletion,
                _ => ChangeKind.Other,
            };
            _changes.TryAdd(id, (kind, revision, change));
        }
        foreach (var end in text.Descendants(Office + "annotation-end"))
        {
            if (Attr(end, Office + "name") is { Length: > 0 } name)
                _annotationEnds.Add(name);
        }
    }

    /// <summary>The model's format of text properties, marked as inserted inside a tracked insertion.</summary>
    private HCharFormat Format(TextProps text)
    {
        var format = CharFormat(text);
        return _insertion is { Revision: var revision } ? format with { Layout = (format.Layout ?? new HCharLayout()) with { Inserted = revision } } : format;
    }

    /// <summary>A change mark (text:change-start, text:change-end, text:change) in the text.</summary>
    private void ChangeMark(XElement mark, TextProps text, List<HInline> output, TextState state)
    {
        if (Attr(mark, Text + "change-id") is not { } id || !_changes.TryGetValue(id, out var change))
            return;
        switch (mark.Name.LocalName)
        {
            case "change-start" when change.Kind == ChangeKind.Insertion:
                _insertion = (id, change.Revision);
                break;
            case "change-end" when _insertion?.Id == id:
                _insertion = null;
                break;
            case "change" when change.Kind == ChangeKind.Deletion && !_inAnnotation:
                {
                    var deletion = new HDeletion(change.Revision);
                    var outer = _insertion;
                    _insertion = null;
                    using var level = _budget.Enter();
                    try
                    {
                        foreach (var paragraph in change.Change.Elements().Where(e => e.Name == Text + "p" || e.Name == Text + "h"))
                        {
                            if (deletion.Content.Count > 0)
                                deletion.Content.Add(new HLineBreak(CharFormat(text)));
                            Inlines(paragraph, SpanProps(text, null), deletion.Content, new TextState { Space = state.Space });
                        }
                    }
                    finally
                    {
                        _insertion = outer;
                    }
                    // The deleted text's own pictures, notes and links don't stay: only its text, tabs and line breaks.
                    deletion.Content.RemoveAll(i => i is not (HText or HTab or HLineBreak) || i is HEquation);
                    if (deletion.Content.Count > 0)
                        output.Add(deletion);
                    break;
                }
        }
    }

    /// <summary>
    /// An office:annotation: a comment on the text up to its office:annotation-end (an HCommentStart here, the comment
    /// there), or on this point.
    /// </summary>
    private void Annotation(XElement annotation, List<HInline> output)
    {
        if (_inAnnotation)
            return;
        var author = annotation.Element(Dc + "creator")?.Value.Trim();
        var initials = annotation.Elements().FirstOrDefault(e => e.Name.LocalName is "creator-initials" or "sender-initials")?.Value.Trim();
        var comment = new HComment
        {
            Author = author is { Length: > 0 } ? author : null,
            Initials = initials is { Length: > 0 } ? initials : null,
            Date = Docx.DocxReader.Date(annotation.Element(Dc + "date")?.Value),
        };
        _budget.Object();
        using (_budget.Enter())
        {
            var outer = _insertion;
            _insertion = null;
            _inAnnotation = true;
            try
            {
                var sink = new Sink(comment.Blocks, body: false);
                ReadBlocks(annotation.Elements().Where(e => e.Name.Namespace == Text), sink);
                Flush(sink);
            }
            finally
            {
                _inAnnotation = false;
                _insertion = outer;
            }
        }
        if (Attr(annotation, Office + "name") is { Length: > 0 } name && _annotationEnds.Contains(name) && _openAnnotations.TryAdd(name, comment))
            output.Add(new HCommentStart(comment));
        else
            output.Add(comment);
    }

    /// <summary>An office:annotation-end: the comment its annotation started.</summary>
    private void AnnotationEnd(XElement end, List<HInline> output)
    {
        if (Attr(end, Office + "name") is { } name && _openAnnotations.Remove(name, out var comment))
            output.Add(comment);
    }
}
