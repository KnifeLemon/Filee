# Puts the ImageMagick library built for macOS 13 (.github/workflows/magick-native-macos.yml) into the given folders,
# as Magick.Native-Q8-<arch>.dll.dylib next to Magick.NET.
#
# Magick.NET's own macOS library needs a newer macOS than Filee supports (#600), and Magick.NET 15 has none for Intel
# Macs at all. Its loader still picks Magick.Native-Q8-x64.dll in every x64 process, whatever the operating system,
# and .NET finds that as Magick.Native-Q8-x64.dll.dylib in the app's folder. So the macOS packages
# (build/build-portable.ps1) get this library in place of Magick.NET's own (Apple silicon) or added (Intel), and the
# tests on Intel Macs get it next to their builds.
#
# Each Magick.NET update needs a new build of the workflow, hosted on dl.filee.sh and pinned below.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('osx-x64', 'osx-arm64')][string]$Runtime,
    [Parameter(Mandatory)][string[]]$Destination
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Magick.Native 2026.1010.1943 (Magick.NET 15.0.0) built for macOS 13.
$pins = @{
    'osx-arm64' = @{
        url = ''
        sha256 = ''
    }
    'osx-x64' = @{
        url = ''
        sha256 = ''
    }
}

$pin = $pins[$Runtime]
if ($pin.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or -not $pin.url) {
    throw "No Magick.Native for macOS 13 is pinned for $Runtime. Run .github/workflows/magick-native-macos.yml, " +
        'host its dylibs on dl.filee.sh and pin them in build/fetch-magick-native.ps1.'
}
$cache = Join-Path $PSScriptRoot '.cache/portable-downloads'
$cached = Join-Path $cache "$($pin.sha256).dylib"
if (-not (Test-Path -LiteralPath $cached)) {
    New-Item -ItemType Directory -Force -Path $cache | Out-Null
    $download = "$cached.$([Guid]::NewGuid().ToString('N')).partial"
    Invoke-WebRequest -Uri $pin.url -OutFile $download
    if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $pin.sha256) {
        Remove-Item -LiteralPath $download
        throw "Checksum mismatch for Magick.Native ($Runtime)."
    }
    Move-Item -LiteralPath $download -Destination $cached
}
if ((Get-FileHash -LiteralPath $cached -Algorithm SHA256).Hash -ne $pin.sha256) {
    throw "Cached Magick.Native checksum mismatch: $cached"
}
$name = "Magick.Native-Q8-$($Runtime.Substring(4)).dll.dylib"
foreach ($folder in $Destination) {
    if (-not (Test-Path -LiteralPath $folder -PathType Container)) { throw "Not a folder: $folder" }
    Copy-Item -LiteralPath $cached -Destination (Join-Path $folder $name) -Force
    Write-Host "Magick.Native for macOS 13 from $($pin.url) -> $folder"
}
