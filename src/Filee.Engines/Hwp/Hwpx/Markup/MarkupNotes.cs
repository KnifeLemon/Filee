// Notes that several references share. The markup readers read a footnote's definition once and give every reference
// to it the same blocks, so the model stays small; the writers, though, write the note out again at every reference,
// and a note may refer to other notes (or to itself). Check measures the document as the writers will write it, every
// note at every reference, in time linear in the model: a block or note is measured once and its size reused. A note
// reference that closes a loop (a note whose text leads back to a paragraph it is written from) is taken out where the
// loop closes, and a document whose written-out notes pass the limits (paragraphs, objects, nesting, twice
// MaxPartBytes of text) is refused before any writer starts.

using Filee.Engines.Documents;

namespace Filee.Engines.Hwp.Hwpx.Markup;

/// <summary>Measures and bounds a markup document with its shared notes written out at every reference.</summary>
internal sealed class MarkupNotes
{
    private readonly DocumentLimits _limits;
    private readonly CancellationToken _cancellationToken;
    private readonly Dictionary<object, Size> _sizes = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<HNote> _open = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<HBlock> _openBlocks = new(ReferenceEqualityComparer.Instance);

    /// <summary>The note references the walk is inside, innermost last.</summary>
    private readonly Stack<(List<HInline> Inlines, HNote Note)> _path = new();
    private readonly List<(List<HInline> Inlines, HNote Note)> _cuts = [];
    private readonly long _maxCharacters;

    private MarkupNotes(DocumentLimits limits, CancellationToken cancellationToken)
    {
        _limits = limits;
        _cancellationToken = cancellationToken;
        _maxCharacters = 2 * limits.MaxPartBytes;
    }

    /// <summary>What blocks hold when they are written out: characters (text and link targets), paragraphs, objects, and the nesting below them.</summary>
    private readonly record struct Size(long Characters, long Paragraphs, long Objects, int Depth);

    /// <summary>
    /// Takes out the note references that would put a note inside itself (where the loop closes), then refuses <paramref name="blocks"/> when,
    /// written out with every note at every reference, they pass <paramref name="limits"/>.
    /// </summary>
    public static void Check(List<HBlock> blocks, DocumentLimits limits, CancellationToken cancellationToken = default)
    {
        var notes = new MarkupNotes(limits, cancellationToken);
        notes.Blocks(blocks, 0);
        foreach (var (inlines, note) in notes._cuts)
            inlines.RemoveAll(i => ReferenceEquals(i, note));
    }

    private Size Blocks(List<HBlock> blocks, int depth)
    {
        var total = default(Size);
        foreach (var block in blocks)
            total = Add(total, Block(block, depth));
        return total;
    }

    private Size Block(HBlock block, int depth)
    {
        if (_sizes.TryGetValue(block, out var known))
        {
            Nesting(depth + known.Depth);
            return known;
        }
        if (_openBlocks.Contains(block))
        {
            // The block is being measured further out: the note just entered leads back to it.
            if (_path.Count > 0)
                _cuts.Add(_path.Peek());
            return default;
        }
        _cancellationToken.ThrowIfCancellationRequested();
        _openBlocks.Add(block);
        Size size;
        switch (block)
        {
            case HParagraph paragraph:
                size = Add(new Size(0, 1, 0, 0), Inlines(paragraph.Inlines, depth));
                break;
            case HTable table:
                {
                    // A cell is a level of its own.
                    Nesting(depth + 1);
                    var inside = Blocks(table.Caption, depth + 1);
                    foreach (var row in table.Rows)
                        foreach (var cell in row.Cells)
                            inside = Add(inside, Blocks(cell.Blocks, depth + 1));
                    size = Add(new Size(0, 0, 1, 0), inside with { Depth = inside.Depth + 1 });
                    break;
                }
            default:
                size = new Size(0, 1, 0, 0);
                break;
        }
        _openBlocks.Remove(block);
        _sizes[block] = size;
        return size;
    }

    private Size Inlines(List<HInline> inlines, int depth)
    {
        var total = default(Size);
        foreach (var inline in inlines)
        {
            var size = inline switch
            {
                HText text => new Size(text.Text.Length, 0, 0, 0),
                HLink link => Add(new Size(link.Target.Length, 0, 0, 0), Inlines(link.Content, depth)),
                HNote note => Note(note, inlines, depth),
                HTextBox box => Nested(box, box.Blocks, depth, new Size(0, 0, 1, 0)),
                HObject => new Size(0, 0, 1, 0),
                _ => new Size(1, 0, 0, 0),
            };
            total = Add(total, size);
        }
        return total;
    }

    private Size Note(HNote note, List<HInline> holder, int depth)
    {
        if (_open.Contains(note))
        {
            // The note is being measured further out: this reference would put it inside itself.
            _cuts.Add((holder, note));
            return default;
        }
        _path.Push((holder, note));
        var size = Nested(note, note.Blocks, depth, default);
        _path.Pop();
        return size;
    }

    /// <summary>A note or text box: its blocks one level down, measured once.</summary>
    private Size Nested(HInline owner, List<HBlock> blocks, int depth, Size own)
    {
        if (_sizes.TryGetValue(owner, out var known))
        {
            Nesting(depth + known.Depth);
            return known;
        }
        Nesting(depth + 1);
        var note = owner as HNote;
        if (note is not null)
            _open.Add(note);
        var inside = Blocks(blocks, depth + 1);
        if (note is not null)
            _open.Remove(note);
        var size = Add(own, inside with { Depth = inside.Depth + 1 });
        _sizes[owner] = size;
        return size;
    }

    private void Nesting(int depth)
    {
        if (depth > _limits.MaxNestingDepth)
            throw DocumentLimits.Exceeded($"Tables, text boxes, notes or groups are nested more than {_limits.MaxNestingDepth} levels deep.");
    }

    /// <summary>The sum of two sizes; refused as soon as it passes a limit, so the counts never overflow.</summary>
    private Size Add(Size a, Size b)
    {
        var sum = new Size(a.Characters + b.Characters, a.Paragraphs + b.Paragraphs, a.Objects + b.Objects, Math.Max(a.Depth, b.Depth));
        if (sum.Paragraphs > _limits.MaxParagraphs)
            throw DocumentLimits.Exceeded($"The document has more than {DocumentLimits.Count(_limits.MaxParagraphs)} paragraphs with its notes written out at every reference.");
        if (sum.Objects > _limits.MaxObjects)
            throw DocumentLimits.Exceeded($"The document has more than {DocumentLimits.Count(_limits.MaxObjects)} tables, pictures and drawing objects with its notes written out at every reference.");
        if (sum.Characters > _maxCharacters)
            throw DocumentLimits.Exceeded($"The document's text is larger than {DocumentLimits.Megabytes(_maxCharacters)} with its notes written out at every reference.");
        return sum;
    }
}
