<#
.SYNOPSIS
  Copies every pinned engine download to Filee's mirror (Cloudflare R2 behind https://dl.filee.sh/engines/).

.DESCRIPTION
  Download sites drop old versions (LibreOffice's "stable" folder lost 26.2.6 when 26.2.7 came out, #1474), and a
  released Filee can't change the URLs it pinned. When a pinned URL fails, the app (EngineInstaller) and
  build/fetch-engines.ps1 take the file from https://dl.filee.sh/engines/<sha256>/<file name> instead, and check the
  same SHA-256. This script fills that folder: for every download in engines.json (every runtime) it checks whether the
  mirror already serves the file at its size, and if not downloads it from its URL, checks the SHA-256, uploads it with
  Wrangler and checks that it is served as is.

  Run it after changing a pin in engines.json (before the release that ships the pin). Files are kept under their hash,
  so old pins stay for the Filee versions that still use them.

  Needs Node.js with Wrangler signed in to Filee's Cloudflare account (npx wrangler login, once).

.PARAMETER Only
  Component ids to mirror (default: all).

.PARAMETER WhatIf
  Lists what is missing on the mirror without downloading or uploading anything.
#>
[CmdletBinding()]
param(
    [string[]]$Only = @(),
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
$bucket = 'filee-downloads'
$root = 'https://dl.filee.sh/engines/'
$wrangler = 'wrangler@4.143.1'

$json = Get-Content (Join-Path $PSScriptRoot '..\src\Filee.Engines\Infrastructure\engines.json') -Raw | ConvertFrom-Json
$downloads = [ordered]@{}
function Add-Components($components, [string]$runtime) {
    foreach ($property in $components.PSObject.Properties) {
        if ($Only.Count -gt 0 -and $property.Name -notin $Only) { continue }
        $info = $property.Value
        $hash = $info.sha256.ToLowerInvariant()
        if (-not $downloads.Contains($hash)) {
            $name = [Uri]::UnescapeDataString(($info.url -split '/')[-1])
            $downloads[$hash] = [pscustomobject]@{ Id = $property.Name; Runtime = $runtime; Url = $info.url; Size = [long]$info.size; Name = $name }
        }
    }
}
Add-Components $json.components 'win-x64'
foreach ($platform in $json.platforms.PSObject.Properties) {
    Add-Components $platform.Value.components $platform.Name
}

$cache = Join-Path $PSScriptRoot '.cache\mirror'
New-Item -ItemType Directory -Force -Path $cache | Out-Null
$failed = @()

foreach ($entry in $downloads.GetEnumerator()) {
    $hash = $entry.Key
    $info = $entry.Value
    $key = "engines/$hash/$($info.Name)"
    $mirror = $root + $hash + '/' + [Uri]::EscapeDataString($info.Name)

    $served = $null
    $response = Invoke-WebRequest $mirror -Method Head -MaximumRedirection 0 -SkipHttpErrorCheck
    if ($response.StatusCode -eq 200) { $served = [long]($response.Headers['Content-Length'] | Select-Object -First 1) }
    if ($served -eq $info.Size) {
        Write-Host "ok   $($info.Id) ($($info.Runtime)) $($info.Name)"
        continue
    }
    if ($WhatIf) {
        Write-Host "miss $($info.Id) ($($info.Runtime)) $($info.Name), $([math]::Round($info.Size / 1MB)) MB"
        continue
    }

    try {
        $file = Join-Path $cache $info.Name
        if (-not ((Test-Path $file) -and (Get-FileHash $file -Algorithm SHA256).Hash -eq $hash.ToUpperInvariant())) {
            Write-Host "get  $($info.Id) ($($info.Runtime)) $($info.Url)"
            Invoke-WebRequest -Uri $info.Url -OutFile $file -UseBasicParsing -UserAgent 'Filee-build' -MaximumRetryCount 5 -RetryIntervalSec 20
            $actual = (Get-FileHash $file -Algorithm SHA256).Hash
            if ($actual -ne $hash.ToUpperInvariant()) {
                Remove-Item $file
                throw "checksum mismatch: expected $hash, got $actual"
            }
        }
        Write-Host "put  $key"
        npx --yes $wrangler r2 object put "$bucket/$key" --file $file --remote --content-type application/octet-stream --cache-control 'public, max-age=31536000, immutable'
        if ($LASTEXITCODE -ne 0) { throw "wrangler r2 object put exited with $LASTEXITCODE" }
        $check = Invoke-WebRequest $mirror -Method Head -MaximumRedirection 0 -SkipHttpErrorCheck
        $length = [long]($check.Headers['Content-Length'] | Select-Object -First 1)
        if ($check.StatusCode -ne 200 -or $length -ne $info.Size) {
            throw "$mirror answers $($check.StatusCode) with $length bytes, not $($info.Size)"
        }
        Write-Host "ok   $($info.Id) ($($info.Runtime)) mirrored"
        Remove-Item $file
    }
    catch {
        Write-Warning "$($info.Id) ($($info.Runtime)) $($info.Name): $($_.Exception.Message)"
        $failed += $info
    }
}

if ($failed.Count -gt 0) {
    $names = ($failed | ForEach-Object { "$($_.Id) ($($_.Runtime))" }) -join ', '
    throw "Not mirrored: $names"
}
