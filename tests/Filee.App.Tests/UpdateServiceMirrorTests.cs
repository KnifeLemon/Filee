using System.Net;
using System.Security.Cryptography;
using System.Text;
using Filee.App.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.App.Tests;

/// <summary>
/// When GitHub doesn't hand out a Windows release file, the updater takes it from dl.filee.sh (#1593), and checks it
/// exactly like GitHub's. Every key here is made for the test and thrown away.
/// </summary>
public class UpdateServiceMirrorTests
{
    private const string GitHub = "https://github.com/KnifeLemon/Filee/releases/download/v9.9.9/";
    private const string Mirror = "https://dl.filee.sh/9.9.9/";
    private const string Setup = "Filee-9.9.9-win-Setup.exe";
    private const string SumsName = "Filee-9.9.9-SHA256SUMS.txt";

    [Theory]
    [InlineData("win", "Filee-2.5.0-win-Setup.exe", "https://dl.filee.sh/2.5.0/Filee-2.5.0-win-Setup.exe")]
    [InlineData("win-arm64", "Filee-2.5.0-win-arm64-Setup.exe", "https://dl.filee.sh/2.5.0/Filee-2.5.0-win-arm64-Setup.exe")]
    [InlineData("win", "Filee-2.5.0-SHA256SUMS.txt.sig", "https://dl.filee.sh/2.5.0/Filee-2.5.0-SHA256SUMS.txt.sig")]
    [InlineData("win", "Setup.exe", null)]
    [InlineData("linux-x64", "Filee-2.5.0-linux-x64.tar.gz", null)]
    public void Mirror_has_the_windows_files_under_their_version(string target, string name, string? expected)
    {
        var updates = new UpdateService(NullLogger<UpdateService>.Instance, new Network()) { TargetPlatform = target };

        Assert.Equal(expected, updates.MirrorUrl(name));
    }

    [Fact]
    public async Task Files_github_does_not_hand_out_come_from_the_mirror()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var installer = RandomNumberGenerator.GetBytes(20_000);
        var sums = Sums(installer);
        var network = new Network { GitHubFails = true };
        network.Serve(SumsName, sums).Serve(SumsName + ".sig", Sign(key, sums)).Serve(Setup, installer);

        var path = await Updates(network, PublicKey(key), installer.Length)
            .DownloadInstallerAsync(TempFolder(), null, TestContext.Current.CancellationToken);

        Assert.Equal(installer, File.ReadAllBytes(path));
        Assert.Equal([Mirror + SumsName, Mirror + SumsName + ".sig", Mirror + Setup], network.MirrorRequests);
        // GitHub timed out once; the signature and the installer don't wait for it again.
        Assert.Equal([GitHub + SumsName], network.GitHubRequests);
    }

    [Fact]
    public async Task Installer_cut_off_on_github_comes_from_the_mirror()
    {
        var installer = RandomNumberGenerator.GetBytes(20_000);
        var network = new Network { GitHubSums = Sums(installer), GitHubInstaller = installer[..5_000] };
        network.Serve(Setup, installer);

        var path = await Updates(network, publicKey: null, installer.Length)
            .DownloadInstallerAsync(TempFolder(), null, TestContext.Current.CancellationToken);

        Assert.Equal(installer, File.ReadAllBytes(path));
        Assert.Equal([Mirror + Setup], network.MirrorRequests);
    }

    [Fact]
    public async Task Mirror_checksum_file_with_a_bad_signature_is_refused()
    {
        // The mirror's checksum file lists another installer; its signature is the one of the real file.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var real = RandomNumberGenerator.GetBytes(1_000);
        var replaced = RandomNumberGenerator.GetBytes(1_000);
        var network = new Network { GitHubFails = true };
        network.Serve(SumsName, Sums(replaced)).Serve(SumsName + ".sig", Sign(key, Sums(real))).Serve(Setup, replaced);

        await Assert.ThrowsAsync<UpdateVerificationException>(() => Updates(network, PublicKey(key), replaced.Length)
            .DownloadInstallerAsync(TempFolder(), null, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(network.Requests, url => url.EndsWith(".exe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unsigned_checksum_file_is_refused_from_the_mirror_too()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        // GitHub doesn't answer with the checksum file (404), and neither GitHub nor the mirror has its signature.
        var installer = RandomNumberGenerator.GetBytes(1_000);
        var network = new Network();
        network.Serve(SumsName, Sums(installer)).Serve(Setup, installer);

        await Assert.ThrowsAsync<UpdateVerificationException>(() => Updates(network, PublicKey(key), installer.Length)
            .DownloadInstallerAsync(TempFolder(), null, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(network.Requests, url => url.EndsWith(".exe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Mirror_installer_with_the_wrong_checksum_is_thrown_away()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var real = RandomNumberGenerator.GetBytes(1_000);
        var sums = Sums(real);
        var network = new Network { GitHubFails = true };
        network.Serve(SumsName, sums).Serve(SumsName + ".sig", Sign(key, sums)).Serve(Setup, RandomNumberGenerator.GetBytes(1_000));
        var folder = TempFolder();

        await Assert.ThrowsAsync<InvalidDataException>(() => Updates(network, PublicKey(key), real.Length)
            .DownloadInstallerAsync(folder, null, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public async Task Installer_with_the_wrong_checksum_on_github_is_not_fetched_again_from_the_mirror()
    {
        // A file that arrived and doesn't match is refused, not downloaded once more.
        var real = RandomNumberGenerator.GetBytes(1_000);
        var network = new Network { GitHubSums = Sums(real), GitHubInstaller = RandomNumberGenerator.GetBytes(1_000) };
        network.Serve(Setup, real);

        await Assert.ThrowsAsync<InvalidDataException>(() => Updates(network, publicKey: null, real.Length)
            .DownloadInstallerAsync(TempFolder(), null, TestContext.Current.CancellationToken));
        Assert.Empty(network.MirrorRequests);
    }

    [Fact]
    public async Task When_both_fail_githubs_error_is_reported()
    {
        var network = new Network { GitHubFails = true }; // and the mirror has nothing: 404

        var error = await Assert.ThrowsAsync<TaskCanceledException>(() => Updates(network, publicKey: null, 1_000)
            .DownloadInstallerAsync(TempFolder(), null, TestContext.Current.CancellationToken));
        Assert.Contains("HttpClient.Timeout", error.Message);
        Assert.Equal([Mirror + SumsName], network.MirrorRequests);
    }

    [Fact]
    public async Task Linux_files_are_not_taken_from_the_mirror()
    {
        var archive = RandomNumberGenerator.GetBytes(1_000);
        const string name = "Filee-9.9.9-linux-x64.tar.gz";
        const string sums = "Filee-9.9.9-linux-x64-SHA256SUMS.txt";
        var network = new Network { GitHubFails = true };
        network.Serve(sums, Sums(archive, name)).Serve(name, archive);
        var updates = new UpdateService(NullLogger<UpdateService>.Instance, network) { TargetPlatform = "linux-x64" };
        updates.UseRelease(new UpdateService.ReleaseFile(name, GitHub + name, archive.Length),
            new UpdateService.ReleaseFile(sums, GitHub + sums, 100));

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            updates.DownloadInstallerAsync(TempFolder(), null, TestContext.Current.CancellationToken));
        Assert.Empty(network.MirrorRequests);
    }

    [Fact]
    public async Task Cancelling_does_not_try_the_mirror()
    {
        using var cancel = new CancellationTokenSource();
        var network = new Network { OnGitHub = cancel.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Updates(network, publicKey: null, 1_000)
            .DownloadInstallerAsync(TempFolder(), null, cancel.Token));
        Assert.Empty(network.MirrorRequests);
    }

    // ───────── Helpers ─────────

    private static byte[] Sums(byte[] installer, string name = Setup) =>
        Encoding.ASCII.GetBytes($"{new string('1', 64)}  Filee-9.9.9-win-Portable.zip\n" +
                                $"{Convert.ToHexStringLower(SHA256.HashData(installer))}  {name}\n");

    private static byte[] Sign(ECDsa key, byte[] data) =>
        Encoding.ASCII.GetBytes(Convert.ToBase64String(key.SignData(data, HashAlgorithmName.SHA256)) + "\n");

    private static string PublicKey(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    private static UpdateService Updates(Network network, string? publicKey, long size)
    {
        var updates = new UpdateService(NullLogger<UpdateService>.Instance, network) { TargetPlatform = "win", UpdatePublicKey = publicKey };
        updates.UseRelease(new UpdateService.ReleaseFile(Setup, GitHub + Setup, size),
            new UpdateService.ReleaseFile(SumsName, GitHub + SumsName, 100));
        return updates;
    }

    private static string TempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "filee-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>
    /// GitHub (times out with <see cref="GitHubFails"/>, otherwise serves <see cref="GitHubSums"/> and
    /// <see cref="GitHubInstaller"/>) and the mirror (the files given to <see cref="Serve"/>, 404 for others).
    /// </summary>
    private sealed class Network : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _mirror = [];

        public bool GitHubFails { get; init; }
        public byte[]? GitHubSums { get; init; }
        public byte[]? GitHubInstaller { get; init; }
        public Action? OnGitHub { get; init; }
        public List<string> Requests { get; } = [];
        public List<string> GitHubRequests => [.. Requests.Where(u => u.StartsWith(GitHub, StringComparison.Ordinal))];
        public List<string> MirrorRequests => [.. Requests.Where(u => u.StartsWith("https://dl.filee.sh/", StringComparison.Ordinal))];

        public Network Serve(string name, byte[] content)
        {
            _mirror[name] = content;
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (Requests)
                Requests.Add(url);
            Assert.Contains("Filee", request.Headers.UserAgent.ToString()); // Cloudflare refuses an empty one
            if (url.StartsWith(GitHub, StringComparison.Ordinal))
            {
                OnGitHub?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                if (GitHubFails)
                    throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 20 seconds elapsing.");
                var content = url.EndsWith(".exe", StringComparison.Ordinal) ? GitHubInstaller
                    : url.EndsWith(".txt", StringComparison.Ordinal) ? GitHubSums
                    : null;
                return Task.FromResult(content is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
            }
            if (url.StartsWith(Mirror, StringComparison.Ordinal) && _mirror.TryGetValue(Uri.UnescapeDataString(url[Mirror.Length..]), out var file))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
