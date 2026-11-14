// A deck in the slide model as a document (HDocument) for the document writers (DOCX, ODT, HWPX, Markdown, text …):
// one section per slide, the size of the slide, holding the text frames' paragraphs and the pictures in reading order
// (top to bottom, then left to right; a group's elements where the group puts them), and table cells row by row.
// Formatting is the paragraphs' own; places, shapes and backgrounds are left to the slide renderer's pictures.

using Filee.Engines.Hwp.Hwpx;
using SkiaSharp;

namespace Filee.Engines.Slides;

internal static class SlideDeckText
{
    /// <summary>Space around the text on each page (HWPUNIT, 1 cm).</summary>
    private const int Margin = 2835;

    /// <summary>The document of <paramref name="deck"/>; pictures are written into <paramref name="mediaFolder"/>.</summary>
    public static HDocument Build(SlideDeck deck, string mediaFolder)
    {
        var document = new HDocument { Title = deck.Title };
        var width = Hwp(deck.Width);
        var height = Hwp(deck.Height);
        var page = new HPage(width, height, Margin, Margin, Margin, Margin, 0, 0, 0);
        var pictures = new Dictionary<byte[], string?>(ReferenceEqualityComparer.Instance);
        foreach (var slide in deck.Slides)
        {
            var section = new HSection { Page = page };
            var placed = new List<(SKRect Box, SlideElement Element)>();
            Flatten(slide.Elements, SKMatrix.Identity, placed);
            foreach (var (box, element) in placed.OrderBy(p => p.Box.Top).ThenBy(p => p.Box.Left))
            {
                switch (element)
                {
                    case SlideShape { IsPicture: true, Fill: PictureFill picture }:
                        if (Picture(picture.Image.Data, mediaFolder, pictures) is { } file)
                        {
                            var paragraph = new HParagraph();
                            paragraph.Inlines.Add(new HImage(file) { Width = Hwp(box.Width), Height = Hwp(box.Height) });
                            section.Blocks.Add(paragraph);
                        }
                        break;
                    case SlideShape { Text: { IsEmpty: false } text }:
                        section.Blocks.AddRange(text.Paragraphs);
                        break;
                    case SlideTable table:
                        foreach (var cell in table.Rows.SelectMany(r => r.Cells).Where(c => !c.Covered && !c.Text.IsEmpty))
                            section.Blocks.AddRange(cell.Text.Paragraphs);
                        break;
                }
            }
            if (section.Blocks.Count == 0)
                section.Blocks.Add(new HParagraph());
            document.Sections.Add(section);
        }
        if (document.Sections.Count == 0)
            document.Sections.Add(new HSection { Page = page, Blocks = { new HParagraph() } });
        return document;
    }

    /// <summary>Points → HWPUNIT (1/100 pt).</summary>
    private static int Hwp(float points) => (int)Math.Clamp(Math.Round(points * 100), 1, 10_000_000);

    /// <summary>The elements with their boxes on the slide, groups opened (their child space mapped onto their box).</summary>
    private static void Flatten(IEnumerable<SlideElement> elements, SKMatrix matrix, List<(SKRect, SlideElement)> output)
    {
        foreach (var element in elements)
        {
            var box = matrix.MapRect(element.Bounds);
            if (element is SlideGroup group)
            {
                var child = group.ChildBounds;
                var inner = SKMatrix.CreateTranslation(-child.Left, -child.Top)
                    .PostConcat(SKMatrix.CreateScale(child.Width > 0 ? group.Bounds.Width / child.Width : 1, child.Height > 0 ? group.Bounds.Height / child.Height : 1))
                    .PostConcat(SKMatrix.CreateTranslation(group.Bounds.Left, group.Bounds.Top))
                    .PostConcat(matrix);
                Flatten(group.Children, inner, output);
                continue;
            }
            output.Add((box, element));
        }
    }

    /// <summary>Writes a picture once into the media folder; null for a kind the writers don't take.</summary>
    private static string? Picture(byte[] data, string mediaFolder, Dictionary<byte[], string?> written)
    {
        if (written.TryGetValue(data, out var known))
            return known;
        var extension = data switch
        {
            [0x89, (byte)'P', (byte)'N', (byte)'G', ..] => ".png",
            [0xFF, 0xD8, ..] => ".jpg",
            [(byte)'G', (byte)'I', (byte)'F', ..] => ".gif",
            [(byte)'B', (byte)'M', ..] => ".bmp",
            [0xD7, 0xCD, 0xC6, 0x9A, ..] or [0x01, 0x00, 0x09, 0x00, ..] => ".wmf",
            { Length: > 44 } when data[40] == (byte)' ' && data[41] == (byte)'E' && data[42] == (byte)'M' && data[43] == (byte)'F' => ".emf",
            _ => null,
        };
        string? file = null;
        if (extension is not null)
        {
            Directory.CreateDirectory(mediaFolder);
            file = Path.Combine(mediaFolder, $"slide-media-{written.Count + 1}{extension}");
            File.WriteAllBytes(file, data);
        }
        written[data] = file;
        return file;
    }
}
