<#
.SYNOPSIS
  Finds the code signing certificate the signing scripts use (dot-sourced by sign-windows.ps1 and sign-release.ps1).

.DESCRIPTION
  Find-FileeSigningCertificate returns the certificate to sign with, or $null when there is none, so a build can go on
  unsigned ("sign only if possible"):
    * -Thumbprint (or $env:FILEE_SIGNING_CERT) names it; it must be in the current user's store and not expired.
    * Without one, the current user's store is searched for a valid code signing certificate with its private key
      (SimplySign Desktop puts Certum's cloud certificate there once signed in). $env:FILEE_SIGNING_SUBJECT narrows it
      down by subject when there are several.
  A named certificate that is missing or expired is an error, not "unsigned": that is a mistake to fix, not a PC
  without a certificate.
#>

function Find-FileeSigningCertificate {
    [CmdletBinding()]
    param([string]$Thumbprint)

    if (-not $IsWindows) { return $null }
    if ($Thumbprint) {
        $Thumbprint = $Thumbprint -replace '\s', ''
        $certificate = Get-Item "Cert:\CurrentUser\My\$Thumbprint" -ErrorAction SilentlyContinue
        if (-not $certificate) {
            throw "Certificate $Thumbprint is not in the current user's store. Sign in to SimplySign Desktop first."
        }
        if ($certificate.NotAfter -lt (Get-Date)) { throw "Certificate $Thumbprint expired on $($certificate.NotAfter)." }
        return $certificate
    }

    $codeSigning = '1.3.6.1.5.5.7.3.3'
    $candidates = @(Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue | Where-Object {
            $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) -and $_.NotBefore -lt (Get-Date) -and
            ($_.EnhancedKeyUsageList.ObjectId -contains $codeSigning) -and
            (-not $env:FILEE_SIGNING_SUBJECT -or $_.Subject -like "*$($env:FILEE_SIGNING_SUBJECT)*")
        })
    if ($candidates.Count -gt 1) {
        $names = ($candidates | ForEach-Object { "$($_.Subject) ($($_.Thumbprint))" }) -join '; '
        throw "Several code signing certificates: $names. Pass -CertificateThumbprint or set FILEE_SIGNING_SUBJECT."
    }
    return $candidates | Select-Object -First 1
}
