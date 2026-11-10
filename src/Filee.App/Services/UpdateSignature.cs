// Signatures over a release's SHA256SUMS files. The checksum file only shows that a download arrived intact: it sits on
// the same release as the installer, so whoever could replace one could replace the other. Each checksum file is
// therefore signed with Filee's update key (ECDSA P-256 over SHA-256) on the maintainer's PC
// (build/sign-checksums.ps1, run by build/sign-release.ps1), and the signature is attached to the release next to it
// as <name>.sig. Official builds carry the public half (FileeUpdatePublicKey in Directory.Build.props), so the updater
// can tell a checksum file Filee wrote from any other, on every platform.

using System.Security.Cryptography;

namespace Filee.App.Services;

internal static class UpdateSignature
{
    /// <summary>The signature of "Filee-2.4.0-SHA256SUMS.txt" is "Filee-2.4.0-SHA256SUMS.txt.sig".</summary>
    public const string Extension = ".sig";

    /// <summary>
    /// True when <paramref name="signature"/> (base64 of the 64-byte r‖s form) is a signature of
    /// <paramref name="data"/> by <paramref name="publicKey"/> (base64 SubjectPublicKeyInfo of a P-256 key). A key or
    /// signature that can't be read counts as a wrong one.
    /// </summary>
    public static bool Verify(byte[] data, string signature, string publicKey)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey.Trim()), out _);
            if (key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
                return false;
            var bytes = Convert.FromBase64String(signature.Trim());
            return bytes.Length == 64
                   && key.VerifyData(data, bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }
}

/// <summary>
/// The update was downloaded but is not Filee's: the checksum file is not signed with the update key, or the installer
/// is not signed by Filee's publisher. The app says so instead of "couldn't download".
/// </summary>
internal sealed class UpdateVerificationException(string message) : IOException(message);
