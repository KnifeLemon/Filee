// The checks that tie an update to Filee's maker beyond the release's own checksum file: the checksum file's signature
// with the update key (FileeUpdatePublicKey) and the installer's publisher pinned in the build (FileeExpectedPublisher).

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Filee.App.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.App.Tests;

public class UpdateSigningTests
{
    private const string Base = "https://example.invalid/releases/download/v9.9.9/";
    private const string Publisher = "CN=Filee Test Publisher";
    private static readonly byte[] Installer = Encoding.ASCII.GetBytes("the installer");

    [Fact]
    public void Signature_is_accepted_in_both_encodings_and_only_for_the_signed_bytes()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var data = Encoding.ASCII.GetBytes("abc  Filee-9.9.9-win-Setup.exe\n");
        var der = Convert.ToBase64String(key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        var p1363 = Convert.ToBase64String(key.SignData(data, HashAlgorithmName.SHA256));

        Assert.True(UpdateService.VerifySignature(publicKey, data, der));
        Assert.True(UpdateService.VerifySignature(publicKey, data, p1363 + "\n")); // a trailing newline from the file
        Assert.False(UpdateService.VerifySignature(publicKey, [.. data, (byte)' '], der));
        Assert.False(UpdateService.VerifySignature(Convert.ToBase64String(other.ExportSubjectPublicKeyInfo()), data, der));
        Assert.False(UpdateService.VerifySignature(publicKey, data, "not base64!"));
        Assert.False(UpdateService.VerifySignature("not a key", data, der));
    }

    [Fact]
    public async Task Without_a_key_the_signature_is_not_asked_for()
    {
        var release = Release(sums: Sums(), signature: null);
        var updates = Updates(release, publicKey: null);

        var path = await updates.DownloadInstallerAsync(TempFolder(), null, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(path));
        Assert.DoesNotContain(release.Requested, url => url.EndsWith(".sig", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_a_key_a_valid_signature_lets_the_installer_through()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var sums = Sums();
        var updates = Updates(Release(sums, Sign(key, sums)), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));

        var path = await updates.DownloadInstallerAsync(TempFolder(), null, TestContext.Current.CancellationToken);

        Assert.Equal(Installer, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task With_a_key_a_missing_signature_is_refused()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var updates = Updates(Release(Sums(), signature: null), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            updates.DownloadInstallerAsync(TempFolder(), null, TestContext.Current.CancellationToken));

        Assert.Contains(".sig", error.Message);
    }

    [Fact]
    public async Task With_a_key_a_replaced_checksum_file_is_refused()
    {
        // Someone who can replace release files replaces the installer and its checksum line, but can't sign.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var original = Sums();
        var replaced = Encoding.ASCII.GetBytes(Convert.ToHexStringLower(SHA256.HashData(Installer)) + "  Filee-9.9.9-win-Setup.exe\nextra\n");
        var updates = Updates(Release(replaced, Sign(key, original)), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        var folder = TempFolder();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            updates.DownloadInstallerAsync(folder, null, TestContext.Current.CancellationToken));

        Assert.Empty(Directory.EnumerateFiles(folder)); // nothing was downloaded
    }

    [Fact]
    public void Unsigned_copy_requires_the_pinned_publisher()
    {
        var path = Path.Combine(TempFolder(), "Filee-9.9.9-win-Setup.exe");
        File.WriteAllText(path, "setup");
        var updates = new UpdateService(NullLogger<UpdateService>.Instance, Release(Sums(), null))
        {
            OwnProgram = "Filee.exe",
            ExpectedPublisher = Publisher,
            SignerOf = (file, _) => file == "Filee.exe" ? null : Publisher,
        };

        updates.CheckPublisher(path);

        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData("CN=Someone Else")]
    [InlineData(null)]
    public void Unsigned_copy_with_a_pinned_publisher_throws_away_other_installers(string? signer)
    {
        var path = Path.Combine(TempFolder(), "Filee-9.9.9-win-Setup.exe");
        File.WriteAllText(path, "setup");
        var updates = new UpdateService(NullLogger<UpdateService>.Instance, Release(Sums(), null))
        {
            OwnProgram = "Filee.exe",
            ExpectedPublisher = Publisher,
            SignerOf = (file, _) => file == "Filee.exe" ? null : signer,
        };

        Assert.Throws<InvalidDataException>(() => updates.CheckPublisher(path));
        Assert.False(File.Exists(path));
    }

    private static byte[] Sums() =>
        Encoding.ASCII.GetBytes(Convert.ToHexStringLower(SHA256.HashData(Installer)) + "  Filee-9.9.9-win-Setup.exe\n");

    private static string Sign(ECDsa key, byte[] data) =>
        Convert.ToBase64String(key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

    private static FakeRelease Release(byte[] sums, string? signature)
    {
        var files = new Dictionary<string, byte[]>
        {
            [Base + "Filee-9.9.9-win-Setup.exe"] = Installer,
            [Base + "Filee-9.9.9-SHA256SUMS.txt"] = sums,
        };
        if (signature is not null)
            files[Base + "Filee-9.9.9-SHA256SUMS.txt.sig"] = Encoding.ASCII.GetBytes(signature);
        return new FakeRelease(files);
    }

    private static UpdateService Updates(FakeRelease release, string? publicKey)
    {
        var updates = new UpdateService(NullLogger<UpdateService>.Instance, release) { UpdatePublicKey = publicKey };
        updates.UseRelease(new UpdateService.ReleaseFile("Filee-9.9.9-win-Setup.exe", Base + "Filee-9.9.9-win-Setup.exe", Installer.Length),
            new UpdateService.ReleaseFile("Filee-9.9.9-SHA256SUMS.txt", Base + "Filee-9.9.9-SHA256SUMS.txt", 100));
        return updates;
    }

    private static string TempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "filee-update-signing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Release files by address; anything else is 404. Records what was asked for.</summary>
    private sealed class FakeRelease(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (Requested)
                Requested.Add(url);
            return Task.FromResult(files.TryGetValue(url, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
