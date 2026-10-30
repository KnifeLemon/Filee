// 한글's own line breaks as the yardstick. An HWPX that 한글 saved stores where each line of a paragraph starts and
// how wide 한글 made the line (hp:linesegarray). For every paragraph of plain text in the HWPX files of
// FILEE_RENDER_CORPUS, Filee's layout engine lays the paragraph out at that width, with the paragraph and character
// shapes of the file, and its line starts are compared with 한글's.
//
// Left out: paragraphs with anything but text (objects, equations, tabs, fields, line breaks), because 한글 counts each
// of those as eight characters in its positions and draws objects at their own widths, and paragraphs whose lines
// differ in width (text wrapped around an object). Fonts that aren't installed are replaced, so their paragraphs can
// break elsewhere. Like the corpus compare, it reports and never fails on the numbers.

using System.IO.Compression;
using System.Xml.Linq;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Typesetting;

namespace Filee.Engines.Tests.RenderQuality;

[Trait("Category", "RenderCorpus")]
public class HangulLineBreakTests
{
    private static readonly XNamespace Hp = "http://www.hancom.co.kr/hwpml/2011/paragraph";

    [Fact]
    public void Line_breaks_agree_with_the_ones_hangul_stored()
    {
        var corpus = Environment.GetEnvironmentVariable("FILEE_RENDER_CORPUS");
        Assert.SkipWhen(string.IsNullOrEmpty(corpus), "FILEE_RENDER_CORPUS is not set (see build/render-compare.ps1)");
        var files = Directory.EnumerateFiles(corpus, "*.hwpx", SearchOption.AllDirectories).Order().ToList();
        Assert.SkipWhen(files.Count == 0, $"No HWPX in {corpus}");
        var output = TestContext.Current.TestOutputHelper;

        using var shaper = new Shaper();
        var layouter = new ParagraphLayouter(new FontResolver(), shaper);
        int paragraphs = 0, exact = 0, lines = 0, agreed = 0, shown = 0;
        foreach (var file in files)
        {
            int fileParagraphs = 0, fileExact = 0;
            foreach (var (paragraph, width, stored) in Paragraphs(file))
            {
                var box = layouter.Layout(paragraph, width);
                var ours = box.Lines.Skip(1).Select(l => l.Start).ToList();
                var same = ours.SequenceEqual(stored);
                fileParagraphs++;
                fileExact += same ? 1 : 0;
                lines += Math.Max(ours.Count, stored.Count) + 1;
                agreed += ours.Intersect(stored).Count() + 1;
                if (!same && shown++ < 30)
                {
                    var text = string.Concat(paragraph.Inlines.OfType<HText>().Select(t => t.Text));
                    output?.WriteLine($"  {Path.GetFileName(file)}: 한글 [{string.Join(", ", stored)}], Filee [{string.Join(", ", ours)}] at {width:0.##} pt");
                    output?.WriteLine($"    {text[..Math.Min(120, text.Length)]}");
                }
            }
            paragraphs += fileParagraphs;
            exact += fileExact;
            output?.WriteLine($"{Path.GetFileName(file)}: {fileExact} of {fileParagraphs} paragraphs break where 한글 broke them");
        }
        output?.WriteLine($"All: {exact} of {paragraphs} paragraphs ({(paragraphs == 0 ? 1 : (double)exact / paragraphs):P1}), " +
                          $"{agreed} of {lines} line starts ({(lines == 0 ? 1 : (double)agreed / lines):P1})");
    }

    /// <summary>
    /// The paragraphs of plain text of an HWPX, each with the shapes of the file, the width 한글 laid it out at and the
    /// text positions where its second and later lines start.
    /// </summary>
    internal static IEnumerable<(HParagraph Paragraph, float Width, List<int> Starts)> Paragraphs(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var header = zip.GetEntry("Contents/header.xml");
        if (header is null)
            yield break;
        XElement head;
        using (var stream = header.Open())
            head = XDocument.Load(stream).Root!;
        var shapes = HwpxReader.ForHeader(head);

        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("Contents/section", StringComparison.Ordinal)).OrderBy(e => e.FullName))
        {
            XDocument section;
            using (var stream = entry.Open())
                section = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
            foreach (var p in section.Descendants(Hp + "p"))
            {
                var segments = p.Element(Hp + "linesegarray")?.Elements(Hp + "lineseg").ToList();
                var runs = p.Elements(Hp + "run").ToList();
                if (segments is not { Count: > 0 } || runs.SelectMany(r => r.Elements()).Any(e => e.Name != Hp + "t" || e.HasElements))
                    continue;
                var paragraph = new HParagraph { Format = shapes.ParagraphShape((string?)p.Attribute("paraPrIDRef") ?? "0") };
                foreach (var run in runs)
                {
                    var format = shapes.CharacterShape((string?)run.Attribute("charPrIDRef") ?? "0");
                    foreach (var t in run.Elements(Hp + "t"))
                        paragraph.Inlines.Add(new HText(t.Value, format));
                }
                var length = paragraph.Inlines.Sum(i => ((HText)i).Text.Length);
                if (length == 0)
                    continue;

                // 한글 stores every line from the paragraph's left margin to its right one, indents inside: lines that
                // start or end elsewhere go around an object.
                int Int(XElement e, string name) => (int?)e.Attribute(name) ?? 0;
                var (position, size) = (Int(segments[0], "horzpos"), Int(segments[0], "horzsize"));
                if (segments.Any(s => Int(s, "horzpos") != position || Int(s, "horzsize") != size))
                    continue;
                var starts = segments.Skip(1).Select(s => Int(s, "textpos")).ToList();
                if (starts.Any(s => s <= 0 || s >= length))
                    continue;
                yield return (paragraph, (position + size + (paragraph.Format.Right ?? 0)) / 100f, starts);
            }
        }
    }
}
