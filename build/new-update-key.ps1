<#
.SYNOPSIS
  Makes Filee's update key: the ECDSA P-256 key pair that signs every release's SHA256SUMS files. Run it once.

.DESCRIPTION
  Writes the private key to -PrivateKeyPath (PKCS#8 PEM, encrypted with a passphrase typed twice) and prints the
  public key. Put the public key into Directory.Build.props as FileeUpdatePublicKey: official builds then refuse an
  update whose checksum file is not signed with this key, on every platform. build/sign-release.ps1 signs each release
  with the private key (build/sign-checksums.ps1).

  The private key must stay outside the repository, so a path inside a Git working tree is refused. Keep a backup of it
  (and of the passphrase) somewhere only you can reach: without it, copies that carry the public key can't be updated
  in the app any more, only by downloading a build with a new key from filee.sh.

.PARAMETER NoPassphrase
  Writes the private key unencrypted. Only for a key kept on encrypted storage that nobody else can read.

.EXAMPLE
  pwsh build/new-update-key.ps1 -PrivateKeyPath D:\keys\filee-update.pem
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PrivateKeyPath,
    [switch]$NoPassphrase
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$target = [IO.Path]::GetFullPath($PrivateKeyPath, (Get-Location).Path)

# Not in this repository, and not in any other Git working tree (one commit and the key is public).
$relative = [IO.Path]::GetRelativePath($root, $target)
if (-not ([IO.Path]::IsPathRooted($relative) -or $relative -eq '..' -or $relative.StartsWith("..$([IO.Path]::DirectorySeparatorChar)"))) {
    throw "$target is inside the repository. Write the private key somewhere outside it."
}
$folder = Split-Path $target
$existing = $folder
while ($existing -and -not (Test-Path -LiteralPath $existing -PathType Container)) { $existing = Split-Path $existing }
if ($existing -and (Get-Command git -ErrorAction SilentlyContinue)) {
    $inside = git -C $existing rev-parse --is-inside-work-tree 2>$null
    if ($LASTEXITCODE -eq 0 -and $inside -eq 'true') { throw "$target is inside a Git working tree. Write the private key somewhere outside one." }
}
if (Test-Path -LiteralPath $target) { throw "$target already exists. Pick another path; this script never overwrites a key." }

$key = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
try {
    if ($NoPassphrase) {
        $pem = $key.ExportPkcs8PrivateKeyPem()
    }
    else {
        $first = [Net.NetworkCredential]::new('', (Read-Host 'Passphrase for the private key' -AsSecureString)).Password
        $again = [Net.NetworkCredential]::new('', (Read-Host 'The same passphrase again' -AsSecureString)).Password
        if (-not $first) { throw 'The passphrase is empty (pass -NoPassphrase for an unencrypted key).' }
        if ($first -cne $again) { throw 'The two passphrases differ.' }
        $pbe = [Security.Cryptography.PbeParameters]::new([Security.Cryptography.PbeEncryptionAlgorithm]::Aes256Cbc,
            [Security.Cryptography.HashAlgorithmName]::SHA256, 600000)
        $pem = $key.ExportEncryptedPkcs8PrivateKeyPem($first, $pbe)
    }
    $publicKey = [Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo())
}
finally {
    $key.Dispose()
}

New-Item -ItemType Directory -Force -Path $folder | Out-Null
# CreateNew: never replaces a file that appeared in the meantime.
$stream = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $bytes = [Text.Encoding]::ASCII.GetBytes($pem + "`n")
    $stream.Write($bytes, 0, $bytes.Length)
}
finally {
    $stream.Dispose()
}

Write-Host "private key: $target (keep it, and a backup, outside the repository)"
Write-Host ''
Write-Host 'public key, for <FileeUpdatePublicKey> in Directory.Build.props:'
Write-Output $publicKey
