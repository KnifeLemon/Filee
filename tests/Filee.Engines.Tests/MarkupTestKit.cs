// What the tests of the built-in markup readers and writers (Hwp/Hwpx/Markup) share: conversions with every engine
// but Pandoc, the text of the model, and the check that a markup file reaches DOCX, HWPX, PDF, HTML and Markdown
// without Pandoc (PDF through the Filee renderer, so neither rhwp nor LibreOffice is needed either).

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Infrastructure;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Filee.Engines.Tests;

internal static class MarkupTestKit
{
    /// <summary>The engines without Pandoc, as on a PC that never downloaded it.</summary>
    public static ConverterCatalog WithoutPandoc(EngineFixture fx) =>
        new(fx.Converters.Where(c => c.Id != "pandoc")) { Priority = new AppSettings().EnginePriority };

    /// <summary>Converts <paramref name="source"/> to <paramref name="target"/> without Pandoc; the job must complete.</summary>
    public static async Task<string> ConvertAsync(ConverterCatalog catalog, string source, string target)
    {
        await using var queue = new JobQueue(catalog);
        var job = new ConversionJob([source], new Preset { TargetFormat = target }, target);
        await queue.RunAsync(job);
        Assert.True(job.State == JobState.Completed, $"{target}: " + string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job.Outputs.Single();
    }

    public static List<HParagraph> Paragraphs(HDocument document) =>
        [.. document.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>()];

    /// <summary>The text of inlines, links included, line breaks as "\n".</summary>
    public static string InlineText(IEnumerable<HInline> inlines) => string.Concat(inlines.Select(i => i switch
    {
        HText t => t.Text,
        HLink l => InlineText(l.Content),
        HTab => "\t",
        HLineBreak => "\n",
        _ => "",
    }));

    /// <summary>Every paragraph's text, tables and notes included, in reading order.</summary>
    public static List<string> Texts(HDocument document) =>
        [.. HDocumentWalker.InlineLists(document.Sections.SelectMany(s => s.Blocks)).Select(l => InlineText(l)).Where(t => t.Length > 0)];

    /// <summary>
    /// The file converts to DOCX, HWPX, HTML, Markdown and PDF without Pandoc, and each result holds
    /// <paramref name="expected"/>. PDF is laid out by the Filee renderer (the test class must be in the
    /// "EngineEnvironment" collection).
    /// </summary>
    public static async Task ConvertsEverywhereWithoutPandocAsync(EngineFixture fx, string source, params string[] expected)
    {
        var catalog = WithoutPandoc(fx);
        foreach (var target in new[] { "docx", "hwpx", "html", "md" })
        {
            var output = await ConvertAsync(catalog, source, target);
            var text = target switch
            {
                "docx" => string.Join("\n", Texts(DocxReader.Read(output, fx.NewFolder()))),
                "hwpx" => string.Join("\n", Texts(HwpxReader.Read(output, fx.NewFolder()))),
                _ => await File.ReadAllTextAsync(output),
            };
            foreach (var part in expected)
                Assert.True(text.Contains(part, StringComparison.Ordinal), $"{target} lacks \"{part}\": {text}");
        }

        var renderer = EngineEnvironment.FileeRenderer;
        EngineEnvironment.FileeRenderer = true;
        try
        {
            catalog.Refresh();
            var pdf = await ConvertAsync(catalog, source, "pdf");
            using var document = PigDocument.Open(pdf);
            // Letters and digits only: spaces and punctuation come out of a PDF's text differently with the fonts
            // each platform has.
            static string Letters(string s) => new([.. s.Where(char.IsLetterOrDigit)]);
            var text = Letters(string.Join("\n", document.GetPages().Select(p => p.Text)));
            foreach (var part in expected)
                Assert.Contains(Letters(part), text, StringComparison.Ordinal);
        }
        finally
        {
            EngineEnvironment.FileeRenderer = renderer;
            fx.Catalog.Refresh();
        }
    }
}
