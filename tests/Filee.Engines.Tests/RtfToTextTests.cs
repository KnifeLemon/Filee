// RTF → TXT through LibreOffice and through Filee's own reader (#1204): code copied from an IDE (a colour run per token
// and an italic run here and there) keeps its text, its indentation and its blank lines. The LibreOffice route is in
// LibreOfficeRouteTests; with the Filee renderer on, in FileeRendererConverterTests.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Engines.Hwp;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Tests;

public class RtfToTextTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    internal const string IdeCode = """
        {\rtf1\ansi\ansicpg1252\cocoartf2907
        \cocoatextscaling0\cocoaplatform0{\fonttbl\f0\fmodern\fcharset0 Courier;\f1\fmodern\fcharset0 Courier-Oblique;}
        {\colortbl;\red255\green255\blue255;\red195\green122\blue88;\red175\green177\blue183;\red186\green102\blue173;
        \red73\green153\blue245;\red39\green159\blue171;\red88\green157\blue94;}
        \pard\tx560\tx1120\tx1680\tx2240\pardirnatural\partightenfactor0

        \f0\fs26 \cf2 import \cf3 java.io.BufferedReader;\
        \
        \cf2 class \cf3 FileTask \cf2 implements \cf3 Runnable \{\
            \cf2 private \cf3 String \cf4 fileName\cf3 ;\
        \
            \cf2 public void \cf5 run\cf3 () \{\
                \cf2 int \cf3 count = \cf6 0\cf3 ;\
                content.append(line).append(\cf7 "\cf2 \\n\cf7 "\cf3 );\
                \cf2 for \cf3 (String word : line.split(\cf7 "\cf2 \\\\\cf7 W+"\cf3 )) \{\
                    count++;\
                \}\
                System.
        \f1\i \cf4 out
        \f0\i0 \cf3 .println(\cf7 "Java word is found " \cf3 + count);\
            \}\
        \}\
        }
        """;

    [Fact]
    public async Task Code_copied_from_an_IDE_keeps_its_text()
    {
        Assert.SkipUnless(fx.Catalog.StatusOf(fx.Converters.Single(c => c.Id == "libreoffice")).IsAvailable, "needs LibreOffice");
        var rtf = Path.Combine(fx.NewFolder(), "FileTask.rtf");
        await File.WriteAllTextAsync(rtf, IdeCode, TestContext.Current.CancellationToken);

        var job = await fx.ConvertAsync([rtf], new Preset { TargetFormat = "txt" });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var lines = (await File.ReadAllTextAsync(job.Outputs.Single(), TestContext.Current.CancellationToken))
            .TrimStart('\uFEFF').ReplaceLineEndings("\n").Split('\n');

        Assert.DoesNotContain(lines, l => l.Contains('`'));
        Assert.Equal("import java.io.BufferedReader;", lines[0]);
        Assert.Equal("", lines[1]); // blank lines stay
        Assert.Contains("    private String fileName;", lines); // and so does the indentation
        Assert.Contains("        content.append(line).append(\"\\n\");", lines);
        Assert.Contains("        for (String word : line.split(\"\\\\W+\")) {", lines);
        Assert.Contains("        System.out.println(\"Java word is found \" + count);", lines); // the italic run
    }
}

/// <summary>RTF → TXT without LibreOffice or Pandoc: RtfReader's document, a line per paragraph.</summary>
[Collection("EngineEnvironment")] // the Filee renderer is on, as in the app, so the route through a PDF is there to lose
public sealed class RtfToBuiltInTextTests(EngineFixture fx) : IClassFixture<EngineFixture>, IDisposable
{
    // Plain code from TextEdit (Helvetica): a \tab, runs of spaces, an empty paragraph, a \line and a trailing space.
    private const string PlainCode = """
        {\rtf1\ansi\ansicpg1254\cocoartf2822{\fonttbl\f0\fswiss\fcharset0 Helvetica;}
        \pard\tx560\tx1120\pardirnatural\partightenfactor0

        \f0\fs24 \cf0 #include <stdio.h>\
        \
        int main(void) \{\
            int arr[3] = \{1, 2, 3\};\
        \tab for (int i = 0; i < 3; i++) \{\
                if (arr[i] > 1) printf("%d\\n", arr[i]);\
            \}\
            return x ** 2  # two spaces\
        \}\par
        first\line second \par
        \pard\par
        last\par
        }
        """;

    public void Dispose()
    {
        EngineEnvironment.FileeRenderer = false;
        fx.Catalog.Refresh();
    }

    private async Task<string> ConvertAsync(string rtf)
    {
        EngineEnvironment.FileeRenderer = true;
        var catalog = new ConverterCatalog(fx.Converters.Where(c => c.Id is not ("libreoffice" or "pandoc")))
        {
            Priority = new AppSettings().EnginePriority,
        };
        // Not the renderer's PDF and the PDF's text, which wrapped long lines and lost the indentation.
        Assert.Equal(HwpTextConverter.EngineId, Assert.Single(catalog.CreatePlanner().Plan("rtf", "txt")!.Steps).Converter.Id);

        var source = Path.Combine(fx.NewFolder(), "code.rtf");
        await File.WriteAllTextAsync(source, rtf, TestContext.Current.CancellationToken);
        var output = await MarkupTestKit.ConvertAsync(catalog, source, "txt");
        return (await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken)).TrimStart('﻿').ReplaceLineEndings("\n");
    }

    [Fact]
    public async Task Code_copied_from_an_ide_comes_out_line_for_line()
    {
        var text = await ConvertAsync(RtfToTextTests.IdeCode);

        Assert.Equal("""
            import java.io.BufferedReader;

            class FileTask implements Runnable {
                private String fileName;

                public void run() {
                    int count = 0;
                    content.append(line).append("\n");
                    for (String word : line.split("\\W+")) {
                        count++;
                    }
                    System.out.println("Java word is found " + count);
                }
            }

            """.ReplaceLineEndings("\n"), text);
    }

    [Fact]
    public async Task Plain_code_keeps_its_tabs_spaces_empty_lines_and_line_breaks()
    {
        var text = await ConvertAsync(PlainCode);

        string[] expected =
        [
            "#include <stdio.h>",
            "", // the empty paragraph, and no empty line between the others
            "int main(void) {",
            "    int arr[3] = {1, 2, 3};",
            "\tfor (int i = 0; i < 3; i++) {", // \tab
            "        if (arr[i] > 1) printf(\"%d\\n\", arr[i]);",
            "    }",
            "    return x ** 2  # two spaces",
            "}",
            "first", // \line
            "second ",
            "",
            "last",
            "",
        ];
        Assert.Equal(string.Join("\n", expected), text);
    }

    [Fact]
    public void Lists_and_tables_take_a_line_each()
    {
        var document = new HDocument();
        var section = new HSection();
        document.Sections.Add(section);
        var numbering = new HNumbering { Levels = { new HNumberingLevel("DIGIT", "^1.", 1, Bullet: false) } };
        section.Blocks.Add(Paragraph("Steps:"));
        var one = Paragraph("one");
        one.List = new HListRef(numbering, 0, Numbered: true);
        var two = Paragraph("two");
        two.List = new HListRef(numbering, 0, Numbered: true);
        section.Blocks.AddRange([one, two]);
        var table = new HTable();
        foreach (var (left, right) in new[] { ("a", "b"), ("c", "d") })
        {
            var row = new HRow();
            row.Cells.AddRange([Cell(left), Cell(right)]);
            table.Rows.Add(row);
        }
        section.Blocks.Add(table);
        section.Blocks.Add(Paragraph("  end"));

        Assert.Equal("Steps:\n1. one\n2. two\na\tb\nc\td\n  end\n", PlainTextWriter.WriteLines(document).ReplaceLineEndings("\n"));

        static HParagraph Paragraph(string text)
        {
            var paragraph = new HParagraph();
            paragraph.Inlines.Add(new HText(text, default));
            return paragraph;
        }

        static HCell Cell(string text)
        {
            var cell = new HCell();
            cell.Blocks.Add(Paragraph(text));
            return cell;
        }
    }
}
