// Which blocks of the document go on which slide: the rules of Pandoc's PowerPoint writer, so a Markdown file gives the
// deck it gave through Pandoc.
//
//  * The slide level is the highest heading level that is followed directly by content (not by another heading or a
//    rule) somewhere in the document; without such a heading it is 6.
//  * A heading at the slide level starts a slide with that heading as its title. A heading above it gets a section
//    slide of its own (the "Section Header" layout); what follows it before the next heading goes on an untitled
//    slide. Headings below it stay on the slide as bold paragraphs.
//  * A horizontal rule ends the slide.
//  * A paragraph that starts with a picture: right after a slide's title the picture goes on that slide, else on an
//    untitled slide of its own when the slide has text already, and the slide ends after it (the rest of the
//    paragraph starts the next one).
//  * A table joins a slide that has only its title (and ends it), starts a new slide when the slide holds a picture
//    or a table already, else joins the slide.
//  * Speaker notes (HBlockRole.Notes) go into the notes of the slide they are on; footnotes are numbered and gathered
//    on a "Notes" slide at the end.
//  * The document's title, subtitle, authors and date make a title slide first.
// A slide is not made for a group that shows nothing (empty paragraphs only).

namespace Filee.Engines.Hwp.Hwpx.Pptx;

internal sealed partial class PptxWriter
{
    /// <summary>The highest slide level Pandoc picks (and the one it falls back to).</summary>
    private const int DefaultSlideLevel = 6;

    private enum SlideKind
    {
        /// <summary>The document's title slide ("Title Slide" layout).</summary>
        Title,

        /// <summary>A heading above the slide level ("Section Header" layout).</summary>
        Section,

        /// <summary>A slide with an optional title and content ("Title and Content" layout).</summary>
        Content,
    }

    /// <summary>The blocks of one slide as the document groups them, before they are fitted onto slides.</summary>
    private sealed class SlideSource(SlideKind kind)
    {
        public SlideKind Kind { get; } = kind;

        /// <summary>The heading that is the slide's title, or null.</summary>
        public HParagraph? Title { get; set; }

        public List<HBlock> Blocks { get; } = [];
        public List<HBlock> Notes { get; } = [];
    }

    /// <summary>
    /// The slide level for <paramref name="blocks"/> (Pandoc's rule): the highest level of a heading followed directly
    /// by a block that is neither a heading nor a rule, else <see cref="DefaultSlideLevel"/>.
    /// </summary>
    internal static int SlideLevel(IReadOnlyList<HBlock> blocks)
    {
        var level = DefaultSlideLevel;
        for (var i = 0; i + 1 < blocks.Count; i++)
        {
            if (Heading(blocks[i]) is { } n && n < level && Heading(blocks[i + 1]) is null && !IsRule(blocks[i + 1]))
                level = n;
        }
        return level;
    }

    /// <summary>The level of a heading block, else null.</summary>
    private static int? Heading(HBlock block) =>
        block is HParagraph { HeadingLevel: > 0 and var level, List: null } && block.Role is HBlockRole.Body ? level : null;

    private static bool IsRule(HBlock block) => block.Role == HBlockRole.Rule;

    /// <summary>A paragraph that starts with a picture (bookmarks and spaces before it don't count), with that picture.</summary>
    private static HImage? LeadingPicture(HBlock block)
    {
        if (block is not HParagraph { HeadingLevel: 0, Role: HBlockRole.Body } paragraph)
            return null;
        foreach (var inline in paragraph.Inlines)
        {
            switch (inline)
            {
                case HBookmark:
                    continue;
                case HText text when string.IsNullOrWhiteSpace(text.Text):
                    continue;
                case HImage image:
                    return image;
                default:
                    return null;
            }
        }
        return null;
    }

    /// <summary>Groups the document's blocks into slides by Pandoc's rules (see the top of this file).</summary>
    private static List<SlideSource> Group(HDocument document)
    {
        var blocks = document.Sections.SelectMany(s => s.Blocks).ToList();
        var slideLevel = SlideLevel(blocks.Where(b => b.Role != HBlockRole.Notes).ToList());
        var slides = new List<SlideSource>();
        if (!string.IsNullOrWhiteSpace(document.Title) || !string.IsNullOrWhiteSpace(document.Subtitle)
            || document.Authors.Count > 0 || !string.IsNullOrWhiteSpace(document.Date))
            slides.Add(new SlideSource(SlideKind.Title));

        SlideSource? current = null;
        void Flush()
        {
            if (current is not null && (current.Title is not null || current.Kind == SlideKind.Section || current.Blocks.Any(Shows)))
                slides.Add(current);
            else if (current is not null && current.Notes.Count > 0 && slides.Count > 0)
                slides[^1].Notes.AddRange(current.Notes); // notes of a group that makes no slide stay with the slide before
            current = null;
        }
        SlideSource Current() => current ??= new SlideSource(SlideKind.Content);
        bool OnlyTitle() => current is { Title: not null } && current.Blocks.Count == 0;
        bool HasText() => current is not null && current.Blocks.Any(b => b is HParagraph && LeadingPicture(b) is null && Shows(b));
        bool HasPictureOrTable() => current is not null && current.Blocks.Any(b => b is HTable || LeadingPicture(b) is not null);

        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            if (block.Role == HBlockRole.Notes)
            {
                Current().Notes.Add(block);
                continue;
            }
            if (IsRule(block))
            {
                Flush();
                continue;
            }
            if (Heading(block) is { } level)
            {
                if (level < slideLevel)
                {
                    Flush();
                    current = new SlideSource(SlideKind.Section) { Title = (HParagraph)block };
                    // Notes right after the heading belong to the section slide.
                    while (i + 1 < blocks.Count && blocks[i + 1].Role == HBlockRole.Notes)
                        current.Notes.Add(blocks[++i]);
                    Flush();
                }
                else if (level == slideLevel)
                {
                    Flush();
                    current = new SlideSource(SlideKind.Content) { Title = (HParagraph)block };
                }
                else
                {
                    Current().Blocks.Add(block);
                }
                continue;
            }
            if (LeadingPicture(block) is { } picture)
            {
                var paragraph = (HParagraph)block;
                var (pictureParagraph, rest) = SplitAtPicture(paragraph, picture);
                if (!OnlyTitle() && HasText())
                    Flush();
                Current().Blocks.Add(pictureParagraph);
                Flush();
                if (rest is not null)
                    blocks.Insert(i + 1, rest);
                continue;
            }
            if (block is HTable)
            {
                if (OnlyTitle())
                {
                    current!.Blocks.Add(block);
                    Flush();
                    continue;
                }
                if (HasPictureOrTable())
                    Flush();
                Current().Blocks.Add(block);
                continue;
            }
            Current().Blocks.Add(block);
        }
        Flush();
        return slides;
    }

    /// <summary>The picture of a paragraph that starts with one, and the paragraph's other content (null when none).</summary>
    private static (HParagraph Picture, HParagraph? After) SplitAtPicture(HParagraph paragraph, HImage picture)
    {
        var index = paragraph.Inlines.IndexOf(picture);
        var head = new HParagraph { Format = paragraph.Format, Role = paragraph.Role };
        head.Inlines.AddRange(paragraph.Inlines.Take(index + 1));
        var tail = paragraph.Inlines.Skip(index + 1).ToList();
        if (!tail.Any(i => i is not HText text || !string.IsNullOrWhiteSpace(text.Text)))
            return (head, null);
        var rest = new HParagraph { Format = paragraph.Format, Role = paragraph.Role };
        rest.Inlines.AddRange(tail);
        return (head, rest);
    }

    /// <summary>A block that puts something on a slide (text, a picture, a table).</summary>
    private static bool Shows(HBlock block) => block switch
    {
        HTable => true,
        HParagraph paragraph => HDocumentWalker.InlinesOf(paragraph.Inlines).Any(i =>
            i is HImage or HTextBox or HNote || i is HText text && !string.IsNullOrWhiteSpace(text.Text)),
        _ => false,
    };
}
