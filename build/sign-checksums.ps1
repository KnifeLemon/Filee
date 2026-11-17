<#
.SYNOPSIS
  Signs every SHA256SUMS file of a release folder with Filee's update key: writes <name>.sig next to each one.

.DESCRIPTION
  The signature is ECDSA P-256 over the SHA-256 of the file's bytes, as base64 of r and s (64 bytes) on one line. The
  in-app updater of an official build checks it with the public key the build carries (FileeUpdatePublicKey in
  Directory.Build.props) and refuses an update without it. build/sign-release.ps1 runs this; the key comes from
  build/new-update-key.ps1. Every signature is checked again before the script ends; with -PublicKey, also that the
  private key is the one that public key belongs to. Outputs the paths of the .sig files.

.PARAMETER PrivateKeyPath
  The private key (PEM). Defaults to $env:FILEE_UPDATE_KEY. An encrypted key asks for its passphrase.

.PARAMETER PublicKey
  The public key the build carries (base64), to check that the signatures are made with the matching key.

.EXAMPLE
  pwsh build/sign-checksums.ps1 -Folder build\.cache\release-2.4.0\sums -PrivateKeyPath D:\keys\filee-update.pem
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Folder,
    [string]$PrivateKeyPath = $env:FILEE_UPDATE_KEY,
    [string]$PublicKey
)

$ErrorActionPreference = 'Stop'
if (-not $PrivateKeyPath) { throw 'Pass -PrivateKeyPath or set FILEE_UPDATE_KEY.' }
if (-not (Test-Path -LiteralPath $PrivateKeyPath -PathType Leaf)) { throw "No private key at $PrivateKeyPath." }
$sums = @(Get-ChildItem -LiteralPath $Folder -File -Filter '*SHA256SUMS.txt')
if (-not $sums.Count) { throw "No SHA256SUMS file in $Folder." }

$key = [Security.Cryptography.ECDsa]::Create()
$verifier = [Security.Cryptography.ECDsa]::Create()
try {
    $pem = Get-Content -LiteralPath $PrivateKeyPath -Raw
    if ($pem -match 'BEGIN ENCRYPTED PRIVATE KEY') {
        $passphrase = [Net.NetworkCredential]::new('', (Read-Host "Passphrase for $PrivateKeyPath" -AsSecureString)).Password
        $key.ImportFromEncryptedPem($pem, $passphrase)
    }
    else {
        $key.ImportFromPem($pem)
    }
    if ($key.ExportParameters($false).Curve.Oid.Value -ne [Security.Cryptography.ECCurve+NamedCurves]::nistP256.Oid.Value) {
        throw "$PrivateKeyPath is not an ECDSA P-256 key."
    }
    $own = [Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo())
    if ($PublicKey -and $PublicKey.Trim() -ne $own) {
        throw "$PrivateKeyPath does not belong to the public key the build carries (FileeUpdatePublicKey)."
    }
    $verifier.ImportSubjectPublicKeyInfo([Convert]::FromBase64String($own), [ref]$null)

    foreach ($file in $sums) {
        $data = [IO.File]::ReadAllBytes($file.FullName)
        $signature = $key.SignData($data, [Security.Cryptography.HashAlgorithmName]::SHA256)
        if (-not $verifier.VerifyData($data, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256)) {
            throw "The signature of $($file.Name) does not verify."
        }
        $path = "$($file.FullName).sig"
        [IO.File]::WriteAllText($path, [Convert]::ToBase64String($signature) + "`n", [Text.Encoding]::ASCII)
        Write-Host "sign $($file.Name)"
        Write-Output $path
    }
}
finally {
    $key.Dispose()
    $verifier.Dispose()
}
