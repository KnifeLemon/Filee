using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines;
using Filee.Engines.Infrastructure;
using Filee.Engines.Pdf;

namespace Filee.Engines.Tests;

[Collection("EngineEnvironment")]
public sealed class IndraConverterTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("filee-indra-").FullName;

    public void Dispose()
    {
        EngineEnvironment.OwnCopies = new Dictionary<string, string>();
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    [Fact]
    public void Uses_Indras_Markdown_auto_mode_arguments() =>
        Assert.Equal(["input.pdf", "--out", "work", "--format", "md", "--mode", "auto"],
            IndraConverter.Arguments("input.pdf", "work"));

    [Fact]
    public void Is_registered_before_the_built_in_PDF_text_reader()
    {
        var engines = EngineRegistry.CreateAll(new EngineEnvironment(_folder));
        var ids = engines.Select(x => x.Id).ToList();
        Assert.True(ids.IndexOf("indra") < ids.IndexOf("pdf-text"));
    }

    [Fact]
    public void Is_an_optional_local_package_not_an_empty_completed_download()
    {
        var package = Assert.Single(EngineDownloads.Packages, x => x.Id == "indra");
        using var http = new HttpClient();
        var installer = new EngineInstaller(http, _folder, new Dictionary<string, EngineComponent>());

        Assert.Empty(package.Components);
        Assert.False(EngineDownloads.CanDownload(package));
        Assert.False(installer.IsInstalled(package));
        Assert.False(installer.IsUpToDate(package));
    }

    [Fact]
    public async Task Takes_Indras_Markdown_output()
    {
        if (OperatingSystem.IsWindows())
            return; // The test double is a POSIX shell command; production lookup is covered on every platform above.

        var input = Path.Combine(_folder, "scan.pdf");
        var output = Path.Combine(_folder, "recognised.md");
        var work = Path.Combine(_folder, "work");
        var executable = Path.Combine(_folder, "indra-ocr");
        await File.WriteAllTextAsync(input, "%PDF-fake", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(executable, """
            #!/bin/sh
            input="$1"
            shift
            output=""
            while [ "$#" -gt 0 ]; do
              if [ "$1" = "--out" ]; then output="$2"; shift 2; continue; fi
              shift
            done
            mkdir -p "$output/nested"
            printf '%s\n' '# Recognised scan' '' '- plain text, list and table structure' > "$output/nested/scan.md"
            """, TestContext.Current.CancellationToken);
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        EngineEnvironment.OwnCopies = new Dictionary<string, string> { ["indra"] = executable };

        var converter = new IndraConverter();
        Assert.True(converter.GetStatus().IsAvailable);
        var catalog = new ConverterCatalog([converter, new PdfTextConverter()]);
        Assert.Equal("indra", Assert.Single(catalog.CreatePlanner().Plan("pdf", "md")!.Steps).Converter.Id);
        var written = await converter.ConvertAsync(
            new ConversionStep(input, "pdf", "md", new Preset(), new FixedPath(output), work), null, TestContext.Current.CancellationToken);

        Assert.Equal(output, written.Single());
        Assert.Equal("# Recognised scan\n\n- plain text, list and table structure\n", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
    }

    private sealed class FixedPath(string path) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => path;
    }
}
