// Lua scripts, CSS style sheets, DBML database schemas and Java source files (issues #388, #368, #854, #1176) become a PDF
// of their text, never run, compiled or translated.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Pdf;

namespace Filee.Engines.Tests;

public class ScriptTextTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    [Theory]
    [InlineData("site.css", "body { color: #1d9e75; }")]
    [InlineData("init.lua", "local greeting = \"hello\" print(greeting)")]
    [InlineData("shop.dbml", "Table customers { id integer [primary key] }")]
    [InlineData("Hello.java", "class Hello { }")]
    public async Task The_code_becomes_a_pdf_of_its_text(string name, string code)
    {
        var available = fx.Converters.Where(c => fx.Catalog.StatusOf(c).IsAvailable).Select(c => c.Id).ToHashSet();
        Assert.SkipUnless(available.Contains("rhwp") || available.Contains("libreoffice"), "neither rhwp nor LibreOffice is available");
        var path = Path.Combine(fx.NewFolder(), name);
        await File.WriteAllTextAsync(path, code, TestContext.Current.CancellationToken);

        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = "pdf" });

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var text = PdfDocumentReader.PlainText(PdfDocumentReader.ReadText(Assert.Single(job.Outputs)));
        Assert.Contains(code.Split(' ')[1], text);
    }

    [Fact]
    public async Task A_dbml_schema_becomes_text_as_it_was_written()
    {
        // #854: the schema is read as text; nothing turns it into SQL.
        const string schema = """
            Table customers {
              id integer [primary key]
            }

            Ref: orders.customer_id > customers.id
            """;
        var path = Path.Combine(fx.NewFolder(), "shop.dbml");
        await File.WriteAllTextAsync(path, schema, TestContext.Current.CancellationToken);

        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = "txt" });

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var output = Assert.Single(job.Outputs);
        Assert.Equal("shop.txt", Path.GetFileName(output));
        var text = await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken);
        Assert.Equal(schema.ReplaceLineEndings(), text.ReplaceLineEndings().TrimEnd());
    }

    [Fact]
    public async Task A_java_file_becomes_text_as_it_was_written()
    {
        // #1176: the source is read as text; nothing compiles or runs it.
        const string source = """
            public class Hello {
                public static void main(String[] args) {
                    System.out.println("Hello");
                }
            }
            """;
        var path = Path.Combine(fx.NewFolder(), "Hello.java");
        await File.WriteAllTextAsync(path, source, TestContext.Current.CancellationToken);

        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = "txt" });

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var output = Assert.Single(job.Outputs);
        Assert.Equal("Hello.txt", Path.GetFileName(output));
        var text = await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken);
        Assert.Equal(source.ReplaceLineEndings(), text.ReplaceLineEndings().TrimEnd());
    }
}
