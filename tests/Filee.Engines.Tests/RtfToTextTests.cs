// RTF → TXT through LibreOffice (#1204): code copied from an IDE (a colour run per token and an italic run here and
// there) keeps its text, its indentation and its blank lines. The route itself is in LibreOfficeRouteTests.

using Filee.Core.Conversion;
using Filee.Core.Presets;

namespace Filee.Engines.Tests;

public class RtfToTextTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string IdeCode = """
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
