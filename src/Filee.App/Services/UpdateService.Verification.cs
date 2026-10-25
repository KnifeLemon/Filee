// UpdateService: downloads the installer and checks it before anything runs it: the release's checksum file must carry
// a valid signature of Filee's update key (when the build has one), the installer must match its SHA-256, and on
// Windows it must be signed by the publisher that signed this copy (or the publisher pinned in the build).

using System.ComponentModel;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Filee.Core.Settings;
using Filee.Platform.Windows;
using Microsoft.Extensions.Logging;

namespace Filee.App.Services;

public sealed partial class UpdateService
{
    /// <summary>
    /// Downloads the installer of the latest release into <paramref name="folder"/> and checks its SHA-256 against
    /// the release's checksum file. Returns the verified file; throws <see cref="InvalidDataException"/> when it does
    /// not match.
    /// </summary>
    internal async Task<string> DownloadInstallerAsync(string folder, IProgress<int>? progress,
        CancellationToken cancellationToken = default)
    {
        var installer = _installer ?? throw new InvalidOperationException("No installer in the latest release");
        var checksums = _checksums ?? throw new InvalidOperationException("No checksum file in the latest release");

        var sumsBytes = await GetBytesAsync(checksums.Url, cancellationToken);
        await VerifyChecksumSignatureAsync(checksums, sumsBytes, cancellationToken);
        var sums = System.Text.Encoding.UTF8.GetString(sumsBytes).TrimStart('﻿');
        var expected = ParseChecksum(sums, installer.Name)
                       ?? throw new InvalidDataException($"{checksums.Name} has no line for {installer.Name}");

        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, installer.Name);
        // Downloaded already (Update pressed again after a declined prompt): reuse it if it is still intact.
        if (File.Exists(target) && await Sha256Async(target, cancellationToken) == expected)
        {
            progress?.Report(100);
            return target;
        }
        foreach (var old in Directory.EnumerateFiles(folder))
            TryDelete(old);

        var partial = target + ".part";
        TryDelete(partial);
        await DownloadAsync(installer, partial, progress, cancellationToken);
        var actual = await Sha256Async(partial, cancellationToken);
        if (actual != expected)
        {
            TryDelete(partial);
            throw new InvalidDataException($"{installer.Name}: SHA-256 {actual} does not match {expected}");
        }
        File.Move(partial, target, overwrite: true);
        return target;
    }

    /// <summary>
    /// With a public key in the build (<see cref="UpdatePublicKey"/>), the checksum file must carry a valid signature
    /// (<c>&lt;checksum file&gt;.sig</c> next to it in the release, base64): the checksum file sits on the same release
    /// as the installer, so a replaced pair would pass the SHA-256 check together; the key's signature can't be
    /// replaced with them. Without a key (releases don't sign it yet) only the SHA-256 is checked, as before.
    /// </summary>
    /// <exception cref="InvalidDataException">The signature is missing or doesn't match.</exception>
    internal async Task VerifyChecksumSignatureAsync(ReleaseFile checksums, byte[] sums, CancellationToken cancellationToken)
    {
        if (UpdatePublicKey is not { } key)
        {
            _log.LogInformation("This build has no update signing key: {File} is not signature-checked", checksums.Name);
            return;
        }
        string signature;
        try
        {
            signature = await GetTextAsync(checksums.Url + ".sig", cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidDataException($"{checksums.Name}.sig, the signature of the checksum file, could not be downloaded: {ex.Message}", ex);
        }
        if (!VerifySignature(key, sums, signature))
            throw new InvalidDataException($"{checksums.Name} is not signed with Filee's update key");
    }

    /// <summary>
    /// Checks an ECDSA P-256 / SHA-256 signature (base64; DER as <c>openssl dgst -sign</c> writes it, or IEEE P1363 as
    /// .NET's <c>SignData</c> does) of <paramref name="data"/> with a base64 SubjectPublicKeyInfo key.
    /// </summary>
    internal static bool VerifySignature(string publicKey, byte[] data, string signature)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey.Trim()), out _);
            var bytes = Convert.FromBase64String(signature.Trim());
            return ecdsa.VerifyData(data, bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)
                   || ecdsa.VerifyData(data, bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// Throws <see cref="InvalidDataException"/> (and deletes the installer) unless the publisher that signed this copy
    /// of Filee signed <paramref name="installer"/> as well. The checksum file sits on the same release as the
    /// installer, so it can't tell a replaced installer from the real one; the signature can. A copy that is not
    /// signed itself (built from source) compares with the publisher pinned in the build (<see cref="ExpectedPublisher"/>);
    /// with none it keeps to the checksum.
    /// </summary>
    internal void CheckPublisher(string installer)
    {
        var publisher = (OwnProgram is null ? null : SignerOf(OwnProgram, false)) ?? ExpectedPublisher;
        if (publisher is null)
        {
            _log.LogInformation("This copy of Filee is not signed and its build pins no publisher: the installer is checked by its SHA-256 only");
            return;
        }
        var signer = SignerOf(installer, true);
        if (signer == publisher)
            return;
        // A signature that holds without the revocation check failed only that check: the certificate authority
        // could not be reached (offline, a proxy or a firewall) or the certificate was revoked.
        var reason = signer is null && SignerOf(installer, false) == publisher
            ? $"is signed by {publisher}, but the revocation check failed (the certificate authority could not be " +
              "reached, or the certificate was revoked)"
            : $"is signed by {signer ?? "no publisher Windows trusts"}, not by {publisher}";
        TryDelete(installer);
        throw new InvalidDataException($"{Path.GetFileName(installer)} {reason}");
    }

    /// <summary>Finds the hash for <paramref name="fileName"/> in a <c>sha256sum</c>-style file ("hash  name" lines).</summary>
    internal static string? ParseChecksum(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].Trim().TrimStart('*') == fileName && parts[0].Length == 64)
                return parts[0].ToLowerInvariant();
        }
        return null;
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }
}
