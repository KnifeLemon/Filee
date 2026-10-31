// Jupyter notebooks without Pandoc (Hwp/Hwpx/Markup/NotebookReader, NotebookWriter): the notebooks are written here
// as JSON the way Jupyter saves them (nbformat 4, and one nbformat 3), with markdown cells and their attachments, code
// cells, streams, results, errors with terminal colours, pictures and HTML tables. They read into the model, round-trip
// through the writer, convert to DOCX / HWPX / PDF / HTML / Markdown with Pandoc absent, and hostile notebooks are cut
// or refused.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Markup;
using static Filee.Engines.Tests.MarkupTestKit;

namespace Filee.Engines.Tests;

[Collection("EngineEnvironment")] // the PDF check turns the Filee renderer on, which is process-wide
public class NotebookTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly string Png = Convert.ToBase64String(DocumentFuzzTests.TinyPng(6, 4));

    /// <summary>A notebook as Jupyter saves it: sources as lists of lines.</summary>
    internal static string Notebook(params JsonObject[] cells) => new JsonObject
    {
        ["cells"] = new JsonArray(cells),
        ["metadata"] = new JsonObject
        {
            ["kernelspec"] = new JsonObject { ["name"] = "python3", ["display_name"] = "Python 3", ["language"] = "python" },
            ["title"] = "Sales analysis",
        },
        ["nbformat"] = 4,
        ["nbformat_minor"] = 5,
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    private static JsonArray Lines(string text)
    {
        var array = new JsonArray();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            if (i < lines.Length - 1 || lines[i].Length > 0)
                array.Add(i < lines.Length - 1 ? lines[i] + "\n" : lines[i]);
        return array;
    }

    internal static JsonObject MarkdownCell(string text, JsonObject? attachments = null)
    {
        var cell = new JsonObject { ["cell_type"] = "markdown", ["metadata"] = new JsonObject(), ["source"] = Lines(text) };
        if (attachments is not null)
            cell["attachments"] = attachments;
        return cell;
    }

    internal static JsonObject CodeCell(string source, params JsonObject[] outputs) => new()
    {
        ["cell_type"] = "code",
        ["execution_count"] = 1,
        ["metadata"] = new JsonObject(),
        ["outputs"] = new JsonArray(outputs),
        ["source"] = Lines(source),
    };

    internal static JsonObject Stream(string text) => new() { ["output_type"] = "stream", ["name"] = "stdout", ["text"] = Lines(text) };

    private static JsonObject Result(JsonObject data) => new()
    {
        ["output_type"] = "execute_result",
        ["execution_count"] = 1,
        ["data"] = data,
        ["metadata"] = new JsonObject(),
    };

    private static JsonObject Display(JsonObject data) => new() { ["output_type"] = "display_data", ["data"] = data, ["metadata"] = new JsonObject() };

    /// <summary>A notebook with every kind of cell and output the reader handles.</summary>
    internal static string Analysis() => Notebook(
        MarkdownCell("# Sales analysis\n\nThe numbers are **good** and *rising*.\n\n- north\n- south\n\n![chart](attachment:chart.png)",
            new JsonObject { ["chart.png"] = new JsonObject { ["image/png"] = Png } }),
        CodeCell("import math\n\ntotal = sum([1, 2, 3])\nprint(total)", Stream("6\n")),
        CodeCell("total * 2", Result(new JsonObject { ["text/plain"] = Lines("12") })),
        CodeCell("plot()", Display(new JsonObject { ["image/png"] = Png, ["text/plain"] = Lines("<Figure size 640x480>") })),
        CodeCell("1 / 0", new JsonObject
        {
            ["output_type"] = "error",
            ["ename"] = "ZeroDivisionError",
            ["evalue"] = "division by zero",
            ["traceback"] = new JsonArray("\u001b[0;31mZeroDivisionError\u001b[0m: division by zero"),
        }),
        MarkdownCell("## Table\n\nA data frame:"),
        CodeCell("frame", Result(new JsonObject
        {
            ["text/html"] = Lines("<table><tr><th>city</th><th>sales</th></tr><tr><td>Seoul</td><td>42</td></tr></table>"),
            ["text/plain"] = Lines("   city  sales\n0  Seoul     42"),
        })),
        new JsonObject { ["cell_type"] = "raw", ["metadata"] = new JsonObject(), ["source"] = Lines("raw text") });

    private HDocument Read(string json, DocumentLimits? limits = null) =>
        NotebookReader.Read(Encoding.UTF8.GetBytes(json), fx.NewFolder(), Path.Combine(fx.NewFolder(), "media"), limits ?? DocumentLimits.Default);

    private static List<string> Code(IEnumerable<HBlock> blocks, bool outputs) =>
    [
        .. blocks.Where(b => MarkupBlocks.IsCode(b) && (((HParagraph)b).Format.Left == NotebookReader.OutputIndent) == outputs)
            .Select(b => InlineText(((HParagraph)b).Inlines)),
    ];

    [Fact]
    public void Cells_and_outputs_become_the_model()
    {
        var document = Read(Analysis());
        var blocks = document.Sections.Single().Blocks;

        Assert.Equal("Sales analysis", document.Title);
        var heading = Paragraphs(document).First();
        Assert.Equal((1, "Sales analysis"), (heading.HeadingLevel, InlineText(heading.Inlines)));
        var good = Paragraphs(document).First(p => InlineText(p.Inlines).StartsWith("The numbers", StringComparison.Ordinal));
        Assert.True(good.Inlines.OfType<HText>().Single(t => t.Text == "good").Format.Bold);
        Assert.Equal(2, Paragraphs(document).Count(p => p.List is not null));
        // The attachment and the displayed figure are pictures; the figure is an output.
        var pictures = blocks.OfType<HParagraph>().Where(p => p.Inlines.OfType<HImage>().Any()).ToList();
        Assert.Equal(2, pictures.Count);
        Assert.All(pictures, p => Assert.True(File.Exists(p.Inlines.OfType<HImage>().Single().Path)));
        Assert.Equal(NotebookReader.OutputIndent, pictures[1].Format.Left);

        // Code keeps its blank line inside one block; outputs are indented, the traceback without its colours.
        Assert.Equal(["import math", "", "total = sum([1, 2, 3])", "print(total)", "total * 2", "plot()", "1 / 0", "frame", "raw text"],
            Code(blocks, outputs: false));
        Assert.Equal(["6", "12", "ZeroDivisionError: division by zero"], Code(blocks, outputs: true));
        // The HTML result is a table rather than its plain text.
        var table = blocks.OfType<HTable>().Single();
        Assert.Equal(NotebookReader.OutputIndent, table.Indent);
        Assert.Equal("Seoul", InlineText(table.Rows[1].Cells[0].Blocks.OfType<HParagraph>().Single().Inlines));
    }

    [Fact]
    public void Version_3_notebooks_are_read()
    {
        var json = """
            {"nbformat": 3, "nbformat_minor": 0, "metadata": {"name": "Old notebook"},
             "worksheets": [{"cells": [
               {"cell_type": "heading", "level": 2, "source": ["Results"]},
               {"cell_type": "code", "input": ["x = 1\n", "x"], "language": "python",
                "outputs": [{"output_type": "pyout", "text": ["1"]}, {"output_type": "stream", "stream": "stdout", "text": ["done\n"]}]}]}]}
            """;

        var document = Read(json);

        Assert.Equal("Old notebook", document.Title);
        Assert.Equal((2, "Results"), (Paragraphs(document)[0].HeadingLevel, InlineText(Paragraphs(document)[0].Inlines)));
        Assert.Equal(["x = 1", "x"], Code(document.Sections[0].Blocks, outputs: false));
        Assert.Equal(["1", "done"], Code(document.Sections[0].Blocks, outputs: true));
    }

    [Fact]
    public void A_notebook_round_trips_through_the_writer()
    {
        var first = Read(Analysis());
        var written = NotebookWriter.Write(first, fx.NewFolder());
        var second = Read(written);

        // Cells: markdown at each heading, code with its outputs, the picture attached and the figure displayed.
        var cells = JsonNode.Parse(written)!["cells"]!.AsArray();
        Assert.Equal(["markdown", "code", "code", "code", "code", "markdown", "code", "markdown", "code"],
            cells.Select(c => (string)c!["cell_type"]!));
        Assert.NotNull(cells[0]!["attachments"]!["image1.png"]!["image/png"]);
        Assert.Equal("stream", (string)cells[1]!["outputs"]![0]!["output_type"]!);
        Assert.Equal("display_data", (string)cells[3]!["outputs"]![0]!["output_type"]!);
        Assert.Equal("Sales analysis", (string)JsonNode.Parse(written)!["metadata"]!["title"]!);

        Assert.Equal(first.Title, second.Title);
        Assert.Equal(Texts(first), Texts(second));
        Assert.Equal(Code(first.Sections[0].Blocks, false), Code(second.Sections[0].Blocks, false));
        Assert.Equal(Code(first.Sections[0].Blocks, true), Code(second.Sections[0].Blocks, true));
        Assert.Equal(2, HDocumentWalker.Images(second).Count());
        // Written again, the notebook is the same.
        Assert.Equal(written, NotebookWriter.Write(second, fx.NewFolder()));
    }

    [Fact]
    public async Task Notebooks_convert_without_pandoc()
    {
        var source = Path.Combine(fx.NewFolder(), "analysis.ipynb");
        await File.WriteAllTextAsync(source, Analysis(), TestContext.Current.CancellationToken);

        await ConvertsEverywhereWithoutPandocAsync(fx, source, "Sales analysis", "total = sum([1, 2, 3])", "Seoul");

        // And back: Markdown and DOCX become notebooks with code cells.
        var catalog = WithoutPandoc(fx);
        var markdown = Path.Combine(fx.NewFolder(), "notes.md");
        await File.WriteAllTextAsync(markdown, "# Notes\n\nSome text.\n\n```python\nprint('hi')\n```\n", TestContext.Current.CancellationToken);
        var notebook = JsonNode.Parse(await File.ReadAllTextAsync(await ConvertAsync(catalog, markdown, "ipynb"), TestContext.Current.CancellationToken))!;
        Assert.Equal(["markdown", "code"], notebook["cells"]!.AsArray().Select(c => (string)c!["cell_type"]!));
        Assert.Equal("print('hi')", (string)notebook["cells"]![1]!["source"]![0]!);
        var docx = await ConvertAsync(catalog, source, "docx");
        var again = JsonNode.Parse(await File.ReadAllTextAsync(await ConvertAsync(catalog, docx, "ipynb"), TestContext.Current.CancellationToken))!;
        Assert.Contains("Sales analysis", again.ToJsonString(), StringComparison.Ordinal);
    }

    // ───────────────────────── Hostile notebooks ─────────────────────────

    [Fact]
    public void Damaged_notebooks_are_invalid_data()
    {
        Assert.Throws<InvalidDataException>(() => Read("not json"));
        Assert.Throws<InvalidDataException>(() => Read("[1, 2, 3]"));
        Assert.Throws<InvalidDataException>(() => Read("{\"metadata\": {}}"));
        // Nested far beyond any notebook: refused by the JSON reader's depth limit, not the stack.
        Assert.Throws<InvalidDataException>(() => Read("{\"cells\": " + new string('[', 100_000) + new string(']', 100_000) + "}"));
        // Cells and outputs of the wrong shape are skipped.
        var odd = Read("{\"cells\": [1, \"x\", {\"cell_type\": \"code\", \"source\": 5, \"outputs\": {\"a\": 1}}, {\"cell_type\": \"markdown\", \"source\": [\"ok\"]}]}");
        Assert.Equal(["ok"], Texts(odd));
    }

    [Fact]
    public void Huge_outputs_are_cut()
    {
        var lines = string.Join("\n", Enumerable.Range(1, 50_000).Select(i => $"line {i}"));
        var wide = new string('x', 3 * NotebookReader.MaxOutputCharacters);
        var document = Read(Notebook(CodeCell("loop()", Stream(lines), Stream(wide))));

        var outputs = Code(document.Sections[0].Blocks, outputs: true);
        Assert.Equal(NotebookReader.MaxOutputLines + 3, outputs.Count);
        Assert.Equal("line 1", outputs[0]);
        Assert.Equal("… (48,000 more lines left out)", outputs[NotebookReader.MaxOutputLines]);
        Assert.Equal(NotebookReader.MaxOutputCharacters, outputs[^2].Length);
        Assert.Equal("… (cut at 200,000 characters)", outputs[^1]);

        // Past the limit of all outputs together, the rest is left out with one notice.
        var many = Enumerable.Range(0, 60).Select(_ => CodeCell("x", Stream(new string('y', NotebookReader.MaxOutputCharacters)))).ToArray();
        var cut = Read(Notebook(many));
        Assert.Equal(1, Texts(cut).Count(t => t.StartsWith("(Further outputs left out", StringComparison.Ordinal)));
        Assert.True(Code(cut.Sections[0].Blocks, outputs: true).Sum(t => t.Length) <= NotebookReader.MaxOutputsCharacters);
    }

    [Fact]
    public void Pictures_that_are_too_big_or_not_pictures_are_left_out()
    {
        // A PNG header claiming 100,000 × 100,000 pixels, base64 that isn't, and a picture over the byte limit.
        var claim = DocumentFuzzTests.TinyPng(1, 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(claim.AsSpan(16), 100_000);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(claim.AsSpan(20), 100_000);
        var huge = new string('A', NotebookReader.MaxPictureBytes / 3 * 4 + 4096);
        var document = Read(Notebook(CodeCell("show()",
            Display(new JsonObject { ["image/png"] = Convert.ToBase64String(claim) }),
            Display(new JsonObject { ["image/png"] = "%%% not base64 %%%", ["text/plain"] = Lines("fallback text") }),
            Display(new JsonObject { ["image/png"] = huge }))));

        Assert.Empty(HDocumentWalker.Images(document));
        Assert.Equal(["fallback text"], Code(document.Sections[0].Blocks, outputs: true));
    }

    [Fact]
    public void Cells_beyond_the_paragraph_limit_are_refused()
    {
        var limits = DocumentLimits.Default with { MaxParagraphs = 100 };
        var code = string.Join("\n", Enumerable.Range(0, 500).Select(i => $"x{i} = {i}"));

        Assert.Throws<ConversionRefusedException>(() => Read(Notebook(CodeCell(code)), limits));
        Assert.Throws<ConversionRefusedException>(() => Read(Notebook(MarkdownCell(string.Join("\n\n", Enumerable.Range(0, 500).Select(i => $"p{i}")))), limits));
        Assert.Throws<ConversionRefusedException>(() => Read(Notebook(Enumerable.Range(0, 200).Select(i => MarkdownCell($"p{i}")).ToArray()), limits));
    }
}
