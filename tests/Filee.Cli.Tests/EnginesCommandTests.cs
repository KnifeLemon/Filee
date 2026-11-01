// "filee engines" without the network: the installer gets a temporary engines folder and a fake server whose
// downloads stand in for engines.json.

using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Filee.Core.Settings;
using Filee.Engines.Infrastructure;

namespace Filee.Cli.Tests;

[Collection("Cli")] // EngineEnvironment.OwnCopies and SearchSystem are process-wide, as in CliTests
public sealed class EnginesCommandTests : IDisposable
{
    private readonly string _settings = Directory.CreateTempSubdirectory("filee-engines-settings-").FullName;
    private readonly string _root = Directory.CreateTempSubdirectory("filee-engines-root-").FullName;
    private readonly bool _searchSystem = EngineEnvironment.SearchSystem;
    private readonly FakeServer _server = new();
    private readonly Dictionary<string, EngineComponent> _components = [];
    private readonly byte[] _pandoc;

    public EnginesCommandTests()
    {
        // A pandoc on this PC (PATH) must not change what the tests see.
        EngineEnvironment.SearchSystem = false;
        _pandoc = Zip(($"pandoc-1.0/bin/{EngineEnvironment.OwnCopyPrograms["pandoc"][0]}", "program"));
        var component = new EngineComponent("pandoc", "1.0", "https://example.test/pandoc.zip",
            Convert.ToHexString(SHA256.HashData(_pandoc)), _pandoc.Length, "zip");
        _components[component.Id] = component;
        _server.Files[component.Url] = _pandoc;
    }

    public void Dispose()
    {
        EngineEnvironment.SearchSystem = _searchSystem;
        EngineEnvironment.OwnCopies = new Dictionary<string, string>();
        foreach (var dir in new[] { _settings, _root })
            try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private sealed class FakeServer : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = [];
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            return Task.FromResult(Files.TryGetValue(url, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static byte[] Zip(params (string Path, string Text)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, text) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(text);
            }
        }
        return buffer.ToArray();
    }

    private EngineInstaller Installer() => new(new HttpClient(_server), _root, _components);

    private async Task<(int Code, string Output, string Errors)> Run(params string[] args)
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        var code = await Cli.RunAsync(args, output, errors, TestContext.Current.CancellationToken, () => CliHost.Create(_settings, Installer()));
        return (code, output.ToString(), errors.ToString());
    }

    private static JsonElement Engine(string json, string id) =>
        JsonDocument.Parse(json).RootElement.GetProperty("engines").EnumerateArray().Single(e => e.GetProperty("id").GetString() == id);

    private static string[] Names(JsonElement element) => element.EnumerateObject().Select(p => p.Name).ToArray();

    [Fact]
    public async Task The_list_shows_every_package_with_what_Filee_can_download_here()
    {
        var (code, output, _) = await Run("engines", "--json");
        Assert.Equal(0, code);
        var root = JsonDocument.Parse(output).RootElement;
        Assert.Equal(EngineDownloads.RuntimeIdentifier, root.GetProperty("platform").GetString());
        Assert.Equal(EngineDownloads.Packages.Select(p => p.Id), root.GetProperty("engines").EnumerateArray().Select(e => e.GetProperty("id").GetString()));

        var pandoc = Engine(output, "pandoc");
        Assert.Equal(["id", "status", "version", "downloadable", "downloadSize", "installedSize", "converters"], Names(pandoc));
        Assert.Equal("notinstalled", pandoc.GetProperty("status").GetString());
        Assert.Equal("1.0", pandoc.GetProperty("version").GetString());
        Assert.Equal(_pandoc.Length, pandoc.GetProperty("downloadSize").GetInt64());
        Assert.Equal(EngineDownloads.Packages.Single(p => p.Id == "pandoc").InstalledSize, pandoc.GetProperty("installedSize").GetInt64());
        Assert.Equal(["pandoc"], pandoc.GetProperty("converters").EnumerateArray().Select(c => c.GetString()));

        // No download of it in this list: offered with the reason instead of a version and sizes.
        var libreOffice = Engine(output, "libreoffice");
        Assert.Equal(["id", "status", "downloadable", "unavailableReason", "converters"], Names(libreOffice));
        Assert.False(libreOffice.GetProperty("downloadable").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(libreOffice.GetProperty("unavailableReason").GetString()));

        (code, output, _) = await Run("engines");
        Assert.Equal(0, code);
        Assert.Contains(output.Split('\n'), l => l.StartsWith("pandoc ", StringComparison.Ordinal) && l.Contains("not installed") && l.Contains("1.0"));
        Assert.Contains("filee engines install <id>", output);
    }

    [Fact]
    public async Task Install_downloads_once_reports_progress_and_skips_what_is_installed()
    {
        var (code, output, errors) = await Run("engines", "install", "pandoc", "--json");
        Assert.Equal(0, code);
        var result = JsonDocument.Parse(output).RootElement;
        Assert.Equal(["id", "state", "version"], Names(result));
        Assert.Equal("installed", result.GetProperty("state").GetString());
        Assert.Equal("1.0", result.GetProperty("version").GetString());
        Assert.True(File.Exists(Path.Combine(_root, "pandoc", "bin", EngineEnvironment.OwnCopyPrograms["pandoc"][0])));

        // Progress: the installer's reports, one JSON object per line on stderr.
        var reports = errors.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        Assert.Contains(reports, r => r.GetProperty("stage").GetString() == "downloading");
        Assert.Contains(reports, r => r.GetProperty("stage").GetString() == "unpacking");
        Assert.All(reports, r => Assert.Equal(["id", "stage", "component", "fraction", "bytesDone", "bytesTotal"], Names(r)));

        Assert.Equal("installed", Engine((await Run("engines", "--json")).Output, "pandoc").GetProperty("status").GetString());
        Assert.False(Engine((await Run("engines", "--json")).Output, "pandoc").GetProperty("outdated").GetBoolean());

        (code, output, errors) = await Run("engines", "install", "pandoc", "--json");
        Assert.Equal(0, code);
        result = JsonDocument.Parse(output).RootElement;
        Assert.Equal(["id", "state", "version", "reason"], Names(result));
        Assert.Equal("skipped", result.GetProperty("state").GetString());
        Assert.Empty(errors);
        Assert.Single(_server.Requests);

        (code, output, _) = await Run("engines", "install", "pandoc");
        Assert.Equal(0, code);
        Assert.Equal("pandoc 1.0 is already installed.", output.Trim());
    }

    [Fact]
    public async Task Install_without_json_prints_the_result_and_progress_lines()
    {
        var (code, output, errors) = await Run("engines", "install", "pandoc");
        Assert.Equal(0, code);
        Assert.Equal("Installed pandoc 1.0.", output.Trim());
        Assert.Contains("Downloading pandoc 1.0:", errors);
        Assert.Contains("Unpacking pandoc…", errors);

        Assert.Equal(0, (await Run("engines", "remove", "pandoc")).Code);
        (code, output, errors) = await Run("engines", "install", "pandoc", "--quiet");
        Assert.Equal(0, code);
        Assert.Empty(output);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task A_package_another_Filee_is_installing_fails_without_a_download()
    {
        var locks = Directory.CreateDirectory(Path.Combine(_root, ".downloads", "locks")).FullName;
        using (new FileStream(Path.Combine(locks, "pandoc.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var (code, output, errors) = await Run("engines", "install", "pandoc", "--json");
            Assert.Equal(1, code);
            var result = JsonDocument.Parse(output).RootElement;
            Assert.Equal(["id", "state", "error"], Names(result));
            Assert.Equal("failed", result.GetProperty("state").GetString());
            Assert.Equal("pandoc is being installed by another Filee. Try again when it has finished.", result.GetProperty("error").GetString());
            Assert.Empty(errors);

            (code, output, errors) = await Run("engines", "install", "pandoc");
            Assert.Equal(1, code);
            Assert.Empty(output);
            Assert.Equal("filee: pandoc is being installed by another Filee. Try again when it has finished.", errors.Trim());
        }
        Assert.Empty(_server.Requests);
        Assert.Equal(0, (await Run("engines", "install", "pandoc")).Code);
    }

    [Fact]
    public async Task Remove_takes_Filees_download_and_nothing_else()
    {
        Assert.Equal(0, (await Run("engines", "install", "pandoc")).Code);
        var (code, output, _) = await Run("engines", "remove", "pandoc", "--json");
        Assert.Equal(0, code);
        Assert.Equal("removed", JsonDocument.Parse(output).RootElement.GetProperty("state").GetString());
        Assert.False(Directory.Exists(Path.Combine(_root, "pandoc")));

        (code, output, _) = await Run("engines", "remove", "pandoc", "--json");
        Assert.Equal(0, code);
        var result = JsonDocument.Parse(output).RootElement;
        Assert.Equal(["id", "state", "reason"], Names(result));
        Assert.Equal("skipped", result.GetProperty("state").GetString());
        Assert.Equal("pandoc is not installed.", result.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task The_users_own_copy_is_listed_and_neither_replaced_nor_removed()
    {
        var own = Directory.CreateTempSubdirectory("filee-own-pandoc-").FullName;
        try
        {
            var program = Path.Combine(own, EngineEnvironment.OwnCopyPrograms["pandoc"][0]);
            File.WriteAllText(program, "my pandoc");
            var store = new UserDataStore(_settings);
            store.Load();
            store.Settings.EngineOwnCopies["pandoc"] = own;
            store.SaveSettings();

            var (code, output, _) = await Run("engines", "--json");
            Assert.Equal(0, code);
            var pandoc = Engine(output, "pandoc");
            Assert.Equal(["id", "status", "fromSystem", "folder", "version", "downloadable", "downloadSize", "installedSize", "converters"], Names(pandoc));
            Assert.Equal("owncopy", pandoc.GetProperty("status").GetString());
            Assert.False(pandoc.GetProperty("fromSystem").GetBoolean());
            Assert.Equal(own, pandoc.GetProperty("folder").GetString());
            var row = (await Run("engines")).Output.Split('\n').Single(l => l.StartsWith("pandoc ", StringComparison.Ordinal));
            Assert.Contains("own copy", row);
            Assert.DoesNotContain("1.0", row);

            (code, output, _) = await Run("engines", "install", "pandoc", "--json");
            Assert.Equal(0, code);
            Assert.Equal("skipped", JsonDocument.Parse(output).RootElement.GetProperty("state").GetString());
            Assert.Empty(_server.Requests);

            (code, output, _) = await Run("engines", "remove", "pandoc", "--json");
            Assert.Equal(0, code);
            var result = JsonDocument.Parse(output).RootElement;
            Assert.Equal("skipped", result.GetProperty("state").GetString());
            Assert.Contains("your own copy", result.GetProperty("reason").GetString());
            Assert.Equal("my pandoc", File.ReadAllText(program));
        }
        finally
        {
            Directory.Delete(own, true);
        }
    }

    [Fact]
    public async Task A_package_without_a_download_here_fails_without_the_network()
    {
        var (code, output, errors) = await Run("engines", "install", "libreoffice", "--json");
        Assert.Equal(1, code);
        var result = JsonDocument.Parse(output).RootElement;
        Assert.Equal("failed", result.GetProperty("state").GetString());
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("error").GetString()));
        Assert.Empty(errors);
        Assert.Equal(1, (await Run("engines", "install", "libreoffice")).Code);
        Assert.Empty(_server.Requests);
    }

    [Theory]
    [InlineData("engines", "install", "nonesuch")]
    [InlineData("engines", "install")]
    [InlineData("engines", "install", "pandoc", "ffmpeg")]
    [InlineData("engines", "update", "pandoc")]
    public async Task Wrong_engine_arguments_are_usage_errors(params string[] args)
    {
        var (code, output, errors) = await Run(args);
        Assert.Equal(2, code);
        Assert.Empty(output);
        Assert.StartsWith("filee: ", errors);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task Engines_go_to_the_apps_engines_folder_which_follows_FILEE_DATA_DIR()
    {
        await using var host = CliHost.Create(_settings);
        Assert.Equal(EngineEnvironment.DownloadRoot, host.Engines.Root);
        Assert.Same(EngineDownloads.Components, host.Engines.Components);
    }
}
