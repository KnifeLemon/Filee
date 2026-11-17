using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Headless.XUnit;
using Filee.App.Services;
using Filee.Platform.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.App.Tests;

/// <summary>
/// An official build carries the public half of the update key and the publisher it expects. Every key here is made
/// for the test and thrown away.
/// </summary>
public class UpdateServiceSignatureTests
{
    private const string Base = "https://example.test/v9.9.9/";
    private const string Setup = "Filee-9.9.9-win-Setup.exe";

    // ───────── SHA256SUMS signed with the update key ─────────

    [Fact]
    public async Task Signed_checksum_file_is_accepted()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var installer = RandomNumberGenerator.GetBytes(20_000);
        var sums = Sums(installer);
        var network = new SignedRelease(installer, sums, Sign(key, sums));
        var folder = TempFolder();

        var path = await Updates(network, PublicKey(key), installer.Length)
            .DownloadInstallerAsync(folder, null, TestContext.Current.CancellationToken);

        Assert.Equal(installer, File.ReadAllBytes(path));
        Assert.Contains(network.Requests, url => url.EndsWith("SHA256SUMS.txt.sig", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Checksum_file_without_a_signature_is_refused()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var installer = RandomNumberGenerator.GetBytes(1_000);
        var network = new SignedRelease(installer, Sums(installer), signature: null);

        await Assert.ThrowsAsync<UpdateVerificationException>(() => Updates(network, PublicKey(key), installer.Length)
            .DownloadInstallerAsync(TempFolder(), null, TestContext.Current.CancellationToken));
        Assert.Equal(0, network.InstallerRequests);
    }

    [Fact]
    public async Task Checksum_file_signed_with_another_key_is_refused()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var installer = RandomNumberGenerator.GetBytes(1_000);
        var sums = Sums(installer);
        var network = new SignedRelease(installer, sums, Sign(other, sums));

        await Assert.ThrowsAsync<UpdateVerificationException>(() => Updates(network, PublicKey(key), installer.Length)
            .DownloadInstallerAsync(TempFolder(), null, TestContext.Current.CancellationToken));
        Assert.Equal(0, network.InstallerRequests);
    }

    [Fact]
    public async Task Changed_checksum_file_is_refused()
    {
        // The installer and its line were replaced together; the signature is still the one of the real file.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var real = RandomNumberGenerator.GetBytes(1_000);
        var replaced = RandomNumberGenerator.GetBytes(1_000);
        var network = new SignedRelease(replaced, Sums(replaced), Sign(key, Sums(real)));

        await Assert.ThrowsAsync<UpdateVerificationException>(() => Updates(network, PublicKey(key), replaced.Length)
            .DownloadInstallerAsync(TempFolder(), null, TestContext.Current.CancellationToken));
        Assert.Equal(0, network.InstallerRequests);
    }

    [Fact]
    public async Task Changed_installer_is_thrown_away()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var real = RandomNumberGenerator.GetBytes(1_000);
        var sums = Sums(real);
        var network = new SignedRelease(RandomNumberGenerator.GetBytes(1_000), sums, Sign(key, sums));
        var folder = TempFolder();

        await Assert.ThrowsAnyAsync<InvalidDataException>(() => Updates(network, PublicKey(key), real.Length)
            .DownloadInstallerAsync(folder, null, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public async Task Older_signed_release_under_a_new_tag_is_refused()
    {
        // A real, signed checksum file of an old release, attached to a release with a higher tag.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var installer = RandomNumberGenerator.GetBytes(1_000);
        var sums = Sums(installer, "Filee-1.0.0-win-Setup.exe");
        var network = new SignedRelease(installer, sums, Sign(key, sums));

        var error = await Assert.ThrowsAsync<UpdateVerificationException>(() =>
            Updates(network, PublicKey(key), installer.Length, "Filee-1.0.0-win-Setup.exe")
                .DownloadInstallerAsync(TempFolder(), null, TestContext.Current.CancellationToken));
        Assert.Contains("not a newer version", error.Message);
    }

    [Fact]
    public async Task Build_without_a_key_keeps_to_the_checksum()
    {
        var installer = RandomNumberGenerator.GetBytes(1_000);
        var network = new SignedRelease(installer, Sums(installer), signature: null);

        var path = await Updates(network, publicKey: null, installer.Length)
            .DownloadInstallerAsync(TempFolder(), null, TestContext.Current.CancellationToken);

        Assert.Equal(installer, File.ReadAllBytes(path));
        Assert.DoesNotContain(network.Requests, url => url.EndsWith(".sig", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Linux_update_without_a_signature_is_refused_and_reported()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var archive = RandomNumberGenerator.GetBytes(1_000);
        var name = "Filee-9.9.9-linux-x64.tar.gz";
        var network = new SignedRelease(archive, Sums(archive, name), signature: null);
        var updates = new UpdateService(NullLogger<UpdateService>.Instance, network)
        {
            InstallsItself = true,
            TargetPlatform = "linux-x64",
            UpdatePublicKey = PublicKey(key),
        };
        updates.UseRelease(new UpdateService.ReleaseFile(name, Base + name, archive.Length),
            new UpdateService.ReleaseFile("Filee-9.9.9-linux-x64-SHA256SUMS.txt", Base + "Filee-9.9.9-linux-x64-SHA256SUMS.txt", 100));
        var started = new List<ProcessStartInfo>();
        updates.StartProcess = psi =>
        {
            started.Add(psi);
            return null;
        };

        await updates.UpdateAsync();

        Assert.Equal(UpdateStep.Failed, updates.Step);
        Assert.IsType<UpdateVerificationException>(updates.LastError);
        Assert.Empty(started);
        Assert.Equal(0, network.InstallerRequests);
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("AAAA")]
    public void Unreadable_signature_or_key_is_not_valid(string garbage)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var data = "a  b\n"u8.ToArray();

        Assert.True(UpdateSignature.Verify(data, Sign(key, data), PublicKey(key)));
        Assert.False(UpdateSignature.Verify(data, garbage, PublicKey(key)));
        Assert.False(UpdateSignature.Verify(data, Sign(key, data), garbage));
    }

    [Fact]
    public void Key_on_another_curve_is_not_valid()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var data = "a  b\n"u8.ToArray();

        Assert.False(UpdateSignature.Verify(data, Sign(key, data), PublicKey(key)));
    }

    // ───────── The publisher an official build expects ─────────

    private static readonly AuthenticodeSigner Publisher = UpdateServiceTests.Publisher;

    private static UpdateService Pinned(UpdateService.PublisherPin pin, AuthenticodeSigner? own, AuthenticodeSigner? installer,
        List<string>? asked = null) =>
        new(NullLogger<UpdateService>.Instance, new SignedRelease([], [], null))
        {
            OwnProgram = "Filee.exe",
            ExpectedPublisher = pin,
            SignerOf = (path, _) =>
            {
                asked?.Add(path);
                return path == "Filee.exe" ? own : installer;
            },
        };

    private static UpdateService.PublisherPin Pin(params string[] keys) => new(Publisher.Subject, [UpdateServiceTests.Issuer], keys);

    [Fact]
    public void Unsigned_official_copy_takes_only_the_expected_publisher()
    {
        var asked = new List<string>();
        var kept = InstallerFile();
        Pinned(Pin(), own: null, Publisher, asked).CheckPublisher(kept);
        Assert.True(File.Exists(kept));
        Assert.DoesNotContain("Filee.exe", asked); // the pin, not this copy, says who the publisher is

        var refused = InstallerFile();
        Assert.Throws<UpdateVerificationException>(() =>
            Pinned(Pin(), own: null, Publisher with { Subject = "CN=Someone Else" }).CheckPublisher(refused));
        Assert.False(File.Exists(refused));
    }

    [Fact]
    public void Expected_publisher_wins_over_whoever_signed_this_copy()
    {
        var someone = Publisher with { Subject = "CN=Someone Else" };
        var path = InstallerFile();

        Assert.Throws<UpdateVerificationException>(() => Pinned(Pin(), own: someone, someone).CheckPublisher(path));
    }

    [Fact]
    public void Expected_publisher_needs_the_issuer_too()
    {
        var path = InstallerFile();

        Assert.Throws<UpdateVerificationException>(() =>
            Pinned(Pin(), own: null, Publisher with { Issuer = "CN=Some Other CA" }).CheckPublisher(path));
    }

    [Fact]
    public void Listed_keys_limit_the_certificates_taken()
    {
        var other = new string('c', 64);
        var kept = InstallerFile();
        Pinned(Pin(other, Publisher.KeySha256.ToUpperInvariant()), own: null, Publisher).CheckPublisher(kept);
        Assert.True(File.Exists(kept));

        var refused = InstallerFile();
        Assert.Throws<UpdateVerificationException>(() => Pinned(Pin(other), own: null, Publisher).CheckPublisher(refused));
    }

    [Fact]
    public void Pin_comes_from_the_build_values()
    {
        Assert.Null(UpdateService.PublisherPin.From(null, "CN=CA", null));

        var pin = UpdateService.PublisherPin.From("CN=Filee", "CN=Old CA | CN=New CA", null)!;

        Assert.Equal(["CN=Old CA", "CN=New CA"], pin.Issuers);
        Assert.Empty(pin.Keys);
        Assert.True(pin.Accepts(new AuthenticodeSigner("CN=Filee", "CN=New CA", new string('d', 64))));
    }

    [Fact]
    public void Builds_from_source_carry_no_key_and_no_expected_publisher()
    {
        // The tests run on a build without -p:FileeRelease=true, like `dotnet run`: today's checks.
        var updates = new UpdateService(NullLogger<UpdateService>.Instance, new SignedRelease([], [], null));

        Assert.Null(updates.UpdatePublicKey);
        Assert.Null(updates.ExpectedPublisher);
    }

    // ───────── Helpers ─────────

    private static byte[] Sums(byte[] installer, string name = Setup) =>
        Encoding.ASCII.GetBytes($"{new string('1', 64)}  Filee-9.9.9-win-Portable.zip\n" +
                                $"{Convert.ToHexStringLower(SHA256.HashData(installer))}  {name}\n");

    private static string Sign(ECDsa key, byte[] data) => Convert.ToBase64String(key.SignData(data, HashAlgorithmName.SHA256)) + "\n";

    private static string PublicKey(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    private static UpdateService Updates(SignedRelease network, string? publicKey, long size, string name = Setup)
    {
        var updates = new UpdateService(NullLogger<UpdateService>.Instance, network) { UpdatePublicKey = publicKey };
        updates.UseRelease(new UpdateService.ReleaseFile(name, Base + name, size),
            new UpdateService.ReleaseFile("Filee-9.9.9-SHA256SUMS.txt", Base + "Filee-9.9.9-SHA256SUMS.txt", 100));
        return updates;
    }

    private static string TempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "filee-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string InstallerFile()
    {
        var path = Path.Combine(TempFolder(), Setup);
        File.WriteAllText(path, "setup");
        return path;
    }

    /// <summary>Serves a checksum file as it is, its signature (404 without one) and the installer.</summary>
    private sealed class SignedRelease(byte[] installer, byte[] sums, string? signature) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public int InstallerRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (Requests)
                Requests.Add(url);
            if (url.EndsWith("SHA256SUMS.txt.sig", StringComparison.Ordinal))
                return Task.FromResult(signature is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(signature, Encoding.ASCII) });
            if (url.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(sums) });
            if (url.EndsWith(".exe", StringComparison.Ordinal) || url.EndsWith(".tar.gz", StringComparison.Ordinal))
            {
                InstallerRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(installer) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
