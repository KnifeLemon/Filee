// Optional engine downloads without the network: a fake HTTP handler serves generated archives.

using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Tests;

public sealed class EngineInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "filee-installer-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    /// <summary>Serves byte arrays by URL; a string value is a redirect target.</summary>
    private sealed class FakeServer(Dictionary<string, object> content) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            var response = content.GetValueOrDefault(url) switch
            {
                byte[] bytes => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) },
                string location => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(location) } },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
            return Task.FromResult(response);
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

    private static EngineComponent Component(string id, string kind, byte[] bytes, string? sha256 = null) =>
        new(id, "1.0", $"https://example.test/{id}.{kind}", sha256 ?? Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length, kind);

    private EngineInstaller Installer(FakeServer server, params EngineComponent[] components) =>
        new(new HttpClient(server), _root, components.ToDictionary(c => c.Id));

    [Fact]
    public async Task Zip_engines_are_verified_unpacked_without_their_top_folder_and_reported()
    {
        var archive = Zip(("tool-1.0/tool.exe", "binary"), ("tool-1.0/readme.txt", "hello"));
        var component = Component("tool", "zip", archive);
        var server = new FakeServer(new() { [component.Url] = archive });
        var installer = Installer(server, component);
        var package = new EnginePackage("tool", ["tool"], 1000, ["tool"]);
        var reports = new List<EngineInstallProgress>();

        Assert.False(installer.IsInstalled(package));
        await installer.InstallAsync(package, new SyncProgress(reports.Add), TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(_root, "tool", "tool.exe")));
        Assert.True(installer.IsInstalled(package));
        Assert.True(installer.IsUpToDate(package));
        Assert.Contains(reports, r => r.Stage == EngineInstallStage.Unpacking);
        Assert.Equal(1.0, reports.Where(r => r.Stage == EngineInstallStage.Downloading).Max(r => r.Fraction), 3);
        // Nothing is left over from downloading or unpacking.
        Assert.Equal(["tool"], Directory.GetDirectories(_root).Select(Path.GetFileName).Where(n => !n!.StartsWith(".downloads", StringComparison.Ordinal)));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, ".downloads")));

        // An installed, up-to-date package is not downloaded again.
        await installer.InstallAsync(package, null, TestContext.Current.CancellationToken);
        Assert.Single(server.Requests);

        installer.Uninstall(package);
        Assert.False(Directory.Exists(Path.Combine(_root, "tool")));
    }

    [Fact]
    public async Task A_download_with_the_wrong_checksum_is_rejected_and_nothing_is_installed()
    {
        var archive = Zip(("tool.exe", "binary"));
        var component = Component("tool", "zip", archive, sha256: new string('0', 64));
        var installer = Installer(new FakeServer(new() { [component.Url] = archive }), component);
        var package = new EnginePackage("tool", ["tool"], 1000, ["tool"]);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(package, null, TestContext.Current.CancellationToken));

        Assert.Contains("checksum", error.Message);
        Assert.False(installer.IsInstalled(package));
        Assert.False(Directory.Exists(Path.Combine(_root, "tool")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, ".downloads")));
    }

    [Fact]
    public async Task Redirects_to_plain_http_mirrors_are_followed_because_the_hash_is_checked()
    {
        var archive = Zip(("tool.exe", "binary"));
        var component = Component("tool", "zip", archive);
        var server = new FakeServer(new()
        {
            [component.Url] = "https://example.test/mirror-list",
            ["https://example.test/mirror-list"] = "http://mirror.example.test/tool.zip",
            ["http://mirror.example.test/tool.zip"] = archive,
        });
        var installer = Installer(server, component);

        await installer.InstallAsync(new EnginePackage("tool", ["tool"], 1, ["tool"]), null, TestContext.Current.CancellationToken);

        Assert.Equal(3, server.Requests.Count);
        Assert.True(File.Exists(Path.Combine(_root, "tool", "tool.exe")));
    }

    [Fact]
    public async Task LibreOffice_extensions_go_into_the_installed_LibreOffice_and_need_it()
    {
        var oxt = Zip(("description.xml", "<description/>"), ("H2Orestart.jar", "jar"));
        var extension = Component("h2orestart", "oxt", oxt);
        var server = new FakeServer(new() { [extension.Url] = oxt });
        var installer = Installer(server, extension);
        var package = new EnginePackage("filter", ["h2orestart"], 1, []);

        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(package, null, TestContext.Current.CancellationToken));

        Directory.CreateDirectory(Path.Combine(_root, "libreoffice", "share", "extensions"));
        await installer.InstallAsync(package, null, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(_root, "libreoffice", "share", "extensions", "H2Orestart", "H2Orestart.jar")));
        Assert.True(installer.IsInstalled(package));
    }

    [Fact]
    public void LibreOffice_is_trimmed_to_what_headless_conversion_needs()
    {
        var root = Path.Combine(_root, "lo");
        foreach (var folder in new[] { "help", "readmes", "program", "share/gallery", "share/extensions/dict-en", "share/extensions/dict-ko", "share/extensions/dict-fr", "share/extensions/H2Orestart" })
            Directory.CreateDirectory(Path.Combine(root, folder));
        File.WriteAllText(Path.Combine(root, "LibreOffice.msi"), "");

        EngineInstaller.TrimLibreOffice(root);

        var remaining = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Select(d => Path.GetRelativePath(root, d).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["program", "share", "share/extensions", "share/extensions/H2Orestart", "share/extensions/dict-en", "share/extensions/dict-ko"], remaining);
        Assert.Empty(Directory.GetFiles(root));
    }

    [Fact]
    public void Every_package_component_is_pinned_in_the_manifest()
    {
        foreach (var package in EngineDownloads.Packages)
        {
            foreach (var id in package.Components)
            {
                var component = EngineDownloads.Components[id];
                Assert.StartsWith("https://", component.Url);
                Assert.Matches("^[0-9A-F]{64}$", component.Sha256);
                Assert.True(component.Size > 0);
            }
            Assert.True(EngineDownloads.DownloadSize(package) > 0);
        }
        // rhwp is bundled with the installer, but its download is pinned in the same list.
        Assert.Contains("rhwp", EngineDownloads.Components.Keys);
    }

    /// <summary>IProgress that reports synchronously (Progress&lt;T&gt; would post to the thread pool).</summary>
    private sealed class SyncProgress(Action<EngineInstallProgress> report) : IProgress<EngineInstallProgress>
    {
        public void Report(EngineInstallProgress value) => report(value);
    }
}
