// End-to-end tests of the "filee" command: real engines, a temporary settings folder, files in a temporary folder.

using System.Text.Json;
using Filee.Core.Presets;

namespace Filee.Cli.Tests;

public sealed class CliTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("filee-cli-").FullName;
    private readonly string _settings = Directory.CreateTempSubdirectory("filee-cli-settings-").FullName;

    public void Dispose()
    {
        foreach (var dir in new[] { _folder, _settings })
            try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private string Png(string name)
    {
        var path = Path.Combine(_folder, name);
        using var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.Orange, 40, 30);
        image.Write(path, ImageMagick.MagickFormat.Png);
        return path;
    }

    private async Task<(int Code, string Output, string Errors)> Run(params string[] args)
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        var code = await Cli.RunAsync(args, output, errors, TestContext.Current.CancellationToken, () => CliHost.Create(_settings));
        return (code, output.ToString(), errors.ToString());
    }

    [Fact]
    public async Task Converts_files_and_wildcards_into_an_output_folder()
    {
        Png("a.png");
        Png("b.png");
        var outFolder = Path.Combine(_folder, "out");

        var (code, output, errors) = await Run("convert", Path.Combine(_folder, "*.png"), "--to", "jpg", "-o", outFolder, "--quality", "70");

        Assert.Equal(Cli.Success, code);
        Assert.Equal("", errors);
        Assert.Contains("Converted 2 of 2", output);
        Assert.True(File.Exists(Path.Combine(outFolder, "a.jpg")));
        Assert.True(File.Exists(Path.Combine(outFolder, "b.jpg")));
    }

    [Fact]
    public async Task Keep_dates_copies_the_dates_of_the_originals()
    {
        var source = Png("old.png");
        var modified = new DateTime(2018, 5, 4, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, modified);

        var (code, _, _) = await Run("convert", source, "--to", "jpg", "--keep-dates");

        Assert.Equal(Cli.Success, code);
        Assert.Equal(modified, File.GetLastWriteTimeUtc(Path.Combine(_folder, "old.jpg")));
    }

    [Fact]
    public async Task Json_output_lists_every_file()
    {
        var source = Png("a.png");

        var (code, output, _) = await Run("convert", source, "--to", ".webp", "--json");

        Assert.Equal(Cli.Success, code);
        using var json = JsonDocument.Parse(output);
        Assert.Equal(1, json.RootElement.GetProperty("converted").GetInt32());
        var file = json.RootElement.GetProperty("files")[0];
        Assert.Equal("done", file.GetProperty("state").GetString());
        Assert.EndsWith("a.webp", file.GetProperty("outputs")[0].GetString());
    }

    [Fact]
    public async Task A_folder_converts_the_files_filee_knows()
    {
        Png("a.png");
        File.WriteAllText(Path.Combine(_folder, "notes.unknownformat"), "x");

        var (code, output, _) = await Run("convert", _folder, "--preset", BuiltInData.CreatePresets().First(p => p.TargetFormat == "jpg").Id);

        Assert.Equal(Cli.Success, code);
        Assert.Contains("Converted 1 of 1", output);
    }

    [Fact]
    public async Task A_file_that_cannot_be_converted_fails_with_exit_code_1()
    {
        var text = Path.Combine(_folder, "notes.txt");
        File.WriteAllText(text, "hello");

        var (code, output, _) = await Run("convert", text, "--to", "mp3");

        Assert.Equal(Cli.SomeFailed, code);
        Assert.Contains("fail", output);
    }

    [Theory]
    [InlineData(new[] { "convert", "a.png" }, "--to")]
    [InlineData(new[] { "convert", "a.png", "--to", "jpg", "--preset", "x" }, "--to")]
    [InlineData(new[] { "convert", "a.png", "--to", "nosuchformat" }, "unknown format")]
    [InlineData(new[] { "convert", "a.png", "--preset", "nosuchpreset" }, "no preset")]
    [InlineData(new[] { "convert", "--to", "jpg" }, "at least one file")]
    [InlineData(new[] { "convert", "a.png", "--to", "jpg", "--quality", "300" }, "--quality")]
    [InlineData(new[] { "frobnicate" }, "Unknown command")]
    public async Task Wrong_arguments_explain_themselves_with_exit_code_2(string[] args, string message)
    {
        Png("a.png");
        var withPaths = args.Select(a => a == "a.png" ? Path.Combine(_folder, a) : a).ToArray();

        var (code, _, errors) = await Run(withPaths);

        Assert.Equal(Cli.UsageError, code);
        Assert.Contains(message, errors, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_missing_input_is_reported()
    {
        var (code, _, errors) = await Run("convert", Path.Combine(_folder, "missing.png"), "--to", "jpg");

        Assert.Equal(Cli.UsageError, code);
        Assert.Contains("not found", errors);
    }

    [Fact]
    public async Task Watch_needs_an_existing_folder()
    {
        var (code, _, errors) = await Run("watch", Path.Combine(_folder, "nope"), "--to", "jpg");

        Assert.Equal(Cli.UsageError, code);
        Assert.Contains("not found", errors, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Watch_converts_files_that_arrive_until_cancelled()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var output = new StringWriter();
        var watching = Cli.RunAsync(["watch", _folder, "--to", "jpg", "--settle", "0.2"], TextWriter.Synchronized(output), TextWriter.Null,
            stop.Token, () => CliHost.Create(_settings));

        await Task.Delay(500, TestContext.Current.CancellationToken);
        Png("arrived.png");
        var converted = Path.Combine(_folder, "converted", "arrived.jpg");
        for (var i = 0; i < 100 && !File.Exists(converted); i++)
            await Task.Delay(100, TestContext.Current.CancellationToken);
        stop.Cancel();

        Assert.Equal(Cli.Success, await watching);
        Assert.True(File.Exists(converted), output.ToString());
    }

    [Fact]
    public async Task Formats_and_presets_are_listed()
    {
        var (code, output, _) = await Run("formats", "heic");
        Assert.Equal(Cli.Success, code);
        Assert.Contains("jpg", output);

        (code, output, _) = await Run("presets");
        Assert.Equal(Cli.Success, code);
        Assert.Contains("to-png", output);
    }

    [Fact]
    public async Task Help_and_version_need_no_engines()
    {
        var (code, output, _) = await Run("help", "convert");
        Assert.Equal(Cli.Success, code);
        Assert.Contains("--to", output);

        (code, output, _) = await Run("--version");
        Assert.Equal(Cli.Success, code);
        Assert.StartsWith("filee ", output);
    }
}
