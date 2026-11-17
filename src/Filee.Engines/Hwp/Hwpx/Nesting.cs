// How deeply a document may nest. A stack overflow cannot be caught: it ends the whole app, with every conversion and
// every watch folder, so a crafted file with thousands of tables in table cells must not be followed level by level.
// The readers open up content nested deeper than MaxDepth into plain paragraphs (the text stays, the boxes around it
// go) and walk their XML on a thread with a large stack; the writers do the same to any model handed to them, so
// every reader, Pandoc's AST and the PDF reader included, is covered.

using System.Text;
using System.Xml.Linq;

namespace Filee.Engines.Hwp.Hwpx;

internal static class Nesting
{
    /// <summary>
    /// Containers (table cells, text boxes, notes, links) kept inside each other; content deeper than this becomes
    /// plain paragraphs. Real documents nest tables a few levels at most, so this is never reached by them.
    /// </summary>
    public const int MaxDepth = 32;

    /// <summary>
    /// Stack of the thread <see cref="OnLargeStack{T}"/> runs on. The readers recurse per XML element level, which
    /// <see cref="Infrastructure.BoundedXml.MaxElementDepth"/> bounds; 16 MB holds that many levels many times over
    /// (a thread-pool thread has 1.5 MB).
    /// </summary>
    private const int StackSize = 16 * 1024 * 1024;

    /// <summary>Runs <paramref name="work"/> on a thread with a large stack and passes its result or exception on.</summary>
    public static T OnLargeStack<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }, StackSize);
        thread.Start();
        thread.Join();
        if (error is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return result;
    }

    /// <inheritdoc cref="OnLargeStack{T}"/>
    public static void OnLargeStack(Action work) => OnLargeStack(() =>
    {
        work();
        return true;
    });

    // ───────────────────────── XML ─────────────────────────

    /// <summary>True when elements inside <paramref name="root"/> nest more than <paramref name="levels"/> deep (found without recursion).</summary>
    public static bool Deeper(XElement root, int levels)
    {
        var pending = new Stack<(XElement Element, int Depth)>();
        pending.Push((root, 0));
        while (pending.TryPop(out var item))
        {
            if (item.Depth > levels)
                return true;
            foreach (var child in item.Element.Elements())
                pending.Push((child, item.Depth + 1));
        }
        return false;
    }

    /// <summary>
    /// Paragraphs of the text in XML too deeply nested to be read in full: one per <paramref name="paragraph"/>
    /// element, holding the text of the <paramref name="text"/> elements inside it (not inside a nested paragraph).
    /// Names are local names, in any namespace; mc:Fallback is left out, as it repeats its mc:Choice (a text box as
    /// DrawingML and as VML). Walks the tree without recursion; paragraphs without text are left out.
    /// </summary>
    public static List<HBlock> PlainParagraphs(XElement root, string paragraph, string text)
    {
        var paragraphs = new List<StringBuilder>();
        // The path from the root to the element being visited, each with the paragraph its text belongs to.
        var path = new Stack<(XElement Element, StringBuilder? Paragraph, bool Skipped)>();
        foreach (var element in root.DescendantsAndSelf())
        {
            while (path.Count > 0 && path.Peek().Element != element.Parent)
                path.Pop();
            var (current, skipped) = path.Count > 0 ? (path.Peek().Paragraph, path.Peek().Skipped) : (null, false);
            if (skipped || element.Name.LocalName == "Fallback")
                skipped = true;
            else if (element.Name.LocalName == paragraph)
                paragraphs.Add(current = new StringBuilder());
            else if (element.Name.LocalName == text)
            {
                if (current is null)
                    paragraphs.Add(current = new StringBuilder());
                foreach (var node in element.Nodes().OfType<XText>())
                    current.Append(node.Value);
            }
            path.Push((element, current, skipped));
        }
        return paragraphs.Where(p => p.Length > 0).Select(p =>
        {
            var block = new HParagraph();
            block.Inlines.Add(new HText(p.ToString(), default));
            return (HBlock)block;
        }).ToList();
    }

    // ───────────────────────── Model ─────────────────────────

    /// <summary>
    /// Opens up whatever <paramref name="document"/> nests deeper than <see cref="MaxDepth"/> containers into plain
    /// paragraphs, so the writers can follow the rest level by level. Documents within the limit are not changed.
    /// </summary>
    public static void Limit(HDocument document)
    {
        foreach (var section in document.Sections)
        {
            Limit(section.Blocks, 0);
            foreach (var part in section.Headers.Concat(section.Footers))
                Limit(part.Blocks, 0);
        }
    }

    /// <param name="depth">Containers around <paramref name="blocks"/>; the recursion stops at MaxDepth + 1.</param>
    private static void Limit(List<HBlock> blocks, int depth)
    {
        if (depth > MaxDepth)
        {
            if (blocks.Any(b => b is not HParagraph p || p.Inlines.Any(IsContainer)))
                Replace(blocks, Flatten(blocks));
            return;
        }
        foreach (var block in blocks)
        {
            switch (block)
            {
                case HParagraph paragraph:
                    Limit(paragraph.Inlines, depth);
                    break;
                case HTable table:
                    foreach (var cell in table.Rows.SelectMany(r => r.Cells))
                        Limit(cell.Blocks, depth + 1);
                    Limit(table.Caption, depth + 1);
                    break;
            }
        }
    }

    private static void Limit(List<HInline> inlines, int depth)
    {
        if (depth > MaxDepth)
        {
            if (inlines.Any(IsContainer))
                Replace(inlines, FlattenInlines(inlines));
            return;
        }
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case HLink link:
                    Limit(link.Content, depth + 1);
                    break;
                case HNote note:
                    Limit(note.Blocks, depth + 1);
                    break;
                case HTextBox box:
                    Limit(box.Blocks, depth + 1);
                    break;
            }
        }
    }

    private static bool IsContainer(HInline inline) => inline is HLink or HNote or HTextBox;

    private static void Replace<T>(List<T> list, List<T> content)
    {
        list.Clear();
        list.AddRange(content);
    }

    /// <summary>Continues the paragraph a note or text box interrupted, after its blocks.</summary>
    private sealed record Resume(HParagraph? Template);

    /// <summary>
    /// <paramref name="blocks"/> as paragraphs of plain inlines: tables, notes and text boxes give the paragraphs they
    /// hold (a paragraph around a note goes on after it), links their content. With a stack instead of recursion,
    /// however deep the content is.
    /// </summary>
    private static List<HBlock> Flatten(IEnumerable<HBlock> blocks)
    {
        var output = new List<HBlock>();
        HParagraph? template = null;
        HParagraph? current = null;
        var pending = new Stack<object>();
        void PushAll(IEnumerable<object> items)
        {
            foreach (var item in items.Reverse())
                pending.Push(item);
        }
        void Start(HParagraph? like)
        {
            template = like;
            current = new HParagraph { Format = like?.Format ?? default, MarkFormat = like?.MarkFormat ?? default };
            output.Add(current);
        }

        PushAll(blocks);
        while (pending.TryPop(out var item))
        {
            switch (item)
            {
                case HParagraph paragraph:
                    Start(paragraph);
                    PushAll(paragraph.Inlines);
                    break;
                case HTable table:
                    current = null;
                    PushAll(table.Rows.SelectMany(r => r.Cells).SelectMany(c => c.Blocks).Concat(table.Caption));
                    break;
                case HLink link:
                    PushAll(link.Content);
                    break;
                case HNote or HTextBox:
                    pending.Push(new Resume(template));
                    PushAll(item is HNote note ? note.Blocks : ((HTextBox)item).Blocks);
                    current = null;
                    break;
                case Resume resume:
                    Start(resume.Template);
                    break;
                case HInline inline:
                    if (current is null)
                        Start(template);
                    current!.Inlines.Add(inline);
                    break;
            }
        }
        output.RemoveAll(b => b is HParagraph { Inlines.Count: 0 });
        return output;
    }

    /// <summary>Inlines without containers: the paragraphs of notes and text boxes follow each other on new lines.</summary>
    private static List<HInline> FlattenInlines(List<HInline> inlines)
    {
        var holder = new HParagraph();
        holder.Inlines.AddRange(inlines);
        var result = new List<HInline>();
        foreach (var paragraph in Flatten([holder]).Cast<HParagraph>())
        {
            if (result.Count > 0)
                result.Add(new HLineBreak(paragraph.MarkFormat));
            result.AddRange(paragraph.Inlines);
        }
        return result;
    }
}
