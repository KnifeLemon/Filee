<#
.SYNOPSIS
  Signs a release's SHA256SUMS files with Filee's update key, so copies built with FileeUpdatePublicKey accept them.

.DESCRIPTION
  The checksum file sits on the same release as the installer: someone who can replace one can replace the other, and
  the SHA-256 check passes. A signature with a key kept outside GitHub can't be replaced with them. UpdateService
  checks it when Directory.Build.props has FileeUpdatePublicKey (Linux and macOS have no Authenticode).

  Writes <file>.sig next to each file: the ECDSA P-256 / SHA-256 signature of the file's exact bytes, base64 (DER).
  The private key (PKCS#8 PEM) comes from -KeyFile, or the PEM text in $env:FILEE_UPDATE_SIGNING_KEY (a CI secret).
  Signs only if possible: without a key it warns and writes nothing, unless -RequireSignature.

  -NewKey creates a key pair once: the private key goes to -KeyFile (keep it outside the repository, in a password
  manager or CI secret), the public key is printed for FileeUpdatePublicKey.

.EXAMPLE
  pwsh build/sign-checksums.ps1 -NewKey -KeyFile ~/filee-update-key.pem
  pwsh build/sign-checksums.ps1 -Path Releases/Filee-2.1.0-SHA256SUMS.txt -KeyFile ~/filee-update-key.pem
#>
[CmdletBinding(DefaultParameterSetName = 'Sign')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Sign')]
    [string[]]$Path,
    [string]$KeyFile = $env:FILEE_UPDATE_SIGNING_KEY_FILE,
    [Parameter(ParameterSetName = 'Sign')]
    [switch]$RequireSignature,
    [Parameter(Mandatory, ParameterSetName = 'NewKey')]
    [switch]$NewKey
)

$ErrorActionPreference = 'Stop'

if ($NewKey) {
    if (-not $KeyFile) { throw 'Pass -KeyFile: where the private key is saved.' }
    if (Test-Path $KeyFile) { throw "$KeyFile exists already; refusing to overwrite a key." }
    $key = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    try {
        Set-Content -Path $KeyFile -Value $key.ExportPkcs8PrivateKeyPem() -NoNewline -Encoding ascii
        Write-Host "private key: $KeyFile (keep it secret, outside the repository)"
        Write-Host 'public key for Directory.Build.props <FileeUpdatePublicKey>:'
        Write-Output ([Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo()))
    }
    finally {
        $key.Dispose()
    }
    return
}

$pem = if ($KeyFile) { Get-Content -Raw $KeyFile } else { $env:FILEE_UPDATE_SIGNING_KEY }
if (-not $pem) {
    if ($RequireSignature) { throw 'No update signing key: pass -KeyFile or set FILEE_UPDATE_SIGNING_KEY.' }
    Write-Warning 'No update signing key (-KeyFile or FILEE_UPDATE_SIGNING_KEY): checksum files stay unsigned.'
    return
}

$key = [System.Security.Cryptography.ECDsa]::Create()
try {
    $key.ImportFromPem($pem)
    if ($key.KeySize -ne 256) { throw "The update key must be ECDSA P-256, not $($key.KeySize) bits." }
    foreach ($file in $Path) {
        $bytes = [System.IO.File]::ReadAllBytes((Resolve-Path $file).Path)
        $signature = $key.SignData($bytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence)
        Set-Content -Path "$file.sig" -Value ([Convert]::ToBase64String($signature)) -NoNewline -Encoding ascii
        Write-Host "signed $(Split-Path $file -Leaf) -> $(Split-Path "$file.sig" -Leaf)"
    }
}
finally {
    $key.Dispose()
}
