<#
.SYNOPSIS
  Downloads and prepares conversion engines for development and for the installer (which bundles rhwp and 7-Zip
  only; the app downloads the large engines on demand, see src/Filee.Engines/Infrastructure/EngineInstaller.cs).

.DESCRIPTION
  Produces this layout (next to Filee.exe in a published build, or in the repo root for development):

    engines/
      libreoffice/   LibreOffice (MPL-2.0), extracted from the official MSI via an administrative install
                     + H2Orestart (GPL-3.0) unpacked into share/extensions for HWP/HWPX import
      jre/           Eclipse Temurin JRE 21 (GPLv2 + Classpath Exception), used by H2Orestart
      rhwp/          rhwp command line (MIT), HWP/HWPX -> PDF, with the Microsoft C++ runtime DLLs next to rhwp.exe
      pandoc/        Pandoc (GPL-2.0-or-later, separate program), Markdown/HTML and MD/HTML/ODT/RTF -> HWPX
      7zip/          7-Zip console (7z.exe + 7z.dll, LGPL-2.1 + unRAR restriction, separate program), archives
      ghostscript/   Ghostscript (AGPL-3.0, separate program) from conda-forge, EPS/PS <-> PDF, with the Microsoft
                     C++ runtime DLLs (vcruntime/) copied next to gswin64c.exe
      ffmpeg/        FFmpeg (GPL-3.0 build, separate program), video and audio; bin/ffmpeg.exe + bin/ffprobe.exe
      calibre/       calibre (GPL-3.0, separate program), extracted from the official MSI via an administrative
                     install: ebook-convert for LIT, LRF, PDB, ... and MOBI / AZW3 output
      tesseract/     Tesseract OCR (Apache-2.0, separate program), UB Mannheim's Windows build unpacked from its
                     installer with 7-Zip (never run); the language data is downloaded by the app when needed

  Every download is pinned to a version and verified with SHA-256 (src/Filee.Engines/Infrastructure/engines.json,
  shared with the app). Downloads are cached in build/.cache.

  The Microsoft C++ runtime (the "vcruntime" component: MSVCP140.dll, VCRUNTIME140.dll, ...) is copied next to the
  programs built with Visual C++ that do not ship it themselves (rhwp.exe, gswin64c.exe; LibreOffice's own copy goes
  into its program folder), so they also run on PCs without the Visual C++ Redistributable. -RuntimeTo copies it into
  more folders (build-installer.ps1: next to Filee.exe, for unhwp.dll).

  Windows on ARM64 (-Runtime win-arm64): rhwp and 7-Zip are ARM64 builds, and "unhwp" is Unhwp's ARM64 library
  (unhwp\unhwp.dll; the build passes it to dotnet with -p:UnhwpNativeLibrary, see build-installer.ps1). rhwp and Unhwp
  have no ARM64 release: .github/workflows/windows-arm64-native.yml builds them from the pinned sources
  (build/build-native.py), the files are hosted on dl.filee.sh and pinned below ($arm64Pins). Both link the C runtime
  statically, so nothing gets the Microsoft C++ runtime DLLs. Every other engine is the x64 download, which Windows 11
  on ARM runs under emulation.

.PARAMETER Destination
  Target "engines" folder. Default: <repo>/engines (picked up automatically by debug builds).

.PARAMETER Only
  Restrict to some engines, e.g. -Only rhwp,pandoc (LibreOffice is large: ~356 MB download).

.PARAMETER RuntimeTo
  More folders that get the Microsoft C++ runtime DLLs, e.g. the published app folder (win-x64 only).

.PARAMETER Runtime
  win-x64 (default) or win-arm64.

.PARAMETER MissingPins
  Lists the win-arm64 downloads that are not pinned yet (one name per line) and downloads nothing.

.EXAMPLE
  pwsh build/fetch-engines.ps1
  pwsh build/fetch-engines.ps1 -Only rhwp
  pwsh build/fetch-engines.ps1 -Destination publish/win-x64/engines
  pwsh build/fetch-engines.ps1 -Runtime win-arm64 -Only rhwp,7zip,unhwp
#>
[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot '..\engines'),
    # Accepts an array (-Only rhwp,jre from PowerShell) or one comma-separated string (pwsh -File ... -Only rhwp,jre).
    [string[]]$Only,
    [string[]]$RuntimeTo,
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [switch]$MissingPins
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # Invoke-WebRequest is ~10x faster without the progress bar

# Versions, URLs and hashes come from the same list the app uses for on-demand downloads.
$manifest = Get-Content (Join-Path $PSScriptRoot '..\src\Filee.Engines\Infrastructure\engines.json') -Raw | ConvertFrom-Json
$engines = [ordered]@{}
foreach ($component in $manifest.components.PSObject.Properties) {
    $engines[$component.Name] = @{ Url = $component.Value.url; Sha256 = $component.Value.sha256 }
}

# The ARM64 builds for -Runtime win-arm64, each of the version the x64 build uses (engines.json; Unhwp:
# Directory.Packages.props). rhwp and unhwp: run the "Windows ARM64 native" workflow (Actions tab), upload the files of
# its artifact to the url below and copy sha256 from its SHA256SUMS.txt. A version update needs a new build: the
# workflow fails until build/build-native.py builds the new version, and this script until the pin is updated.
$arm64Pins = [ordered]@{
    rhwp = @{
        Version = '0.8.7'
        Url = 'https://dl.filee.sh/engines/rhwp/0.8.7/rhwp-v0.8.7-windows-aarch64.zip'
        Sha256 = 'EAC4F747115AA061208FB20E2727E67C8A8AE6AAA60D74F139A9DD6BCBA83172'
    }
    unhwp = @{
        Version = '0.13.0'
        Url = 'https://dl.filee.sh/engines/unhwp/0.13.0/unhwp-0.13.0-win-arm64.dll'
        Sha256 = '2D9760720F281E49B5D472437849E7C66704F8DF0330F378CE350D1561F16621'
    }
    # The official ARM64 installer, a 7z self-extracting archive; it is unpacked, never run.
    '7zip' = @{
        Version = '26.05'
        Url = 'https://github.com/ip7z/7zip/releases/download/26.05/7z2605-arm64.exe'
        Sha256 = '86D5790D5DD84CAB4636C3BB7FAB10D3ED7CD3E57E6119401BD2769E28165268'
    }
}

if ($Runtime -eq 'win-arm64') {
    $x64Versions = @{
        rhwp = $manifest.components.rhwp.version
        '7zip' = $manifest.components.'7zip'.version
        unhwp = ([xml](Get-Content (Join-Path $PSScriptRoot '..\Directory.Packages.props') -Raw)).Project.ItemGroup.PackageVersion |
            Where-Object { $_.Include -eq 'Unhwp' } | ForEach-Object Version
    }
    $missing = @($arm64Pins.Keys | Where-Object { $arm64Pins[$_].Sha256 -notmatch '^[0-9a-fA-F]{64}$' })
    if ($MissingPins) {
        $missing
        return
    }
    foreach ($name in $arm64Pins.Keys) {
        if ($arm64Pins[$name].Version -ne $x64Versions[$name]) {
            throw "build/fetch-engines.ps1 pins $name $($arm64Pins[$name].Version) for win-arm64, but the x64 build uses $($x64Versions[$name]). Pin the ARM64 build of $($x64Versions[$name])."
        }
    }
}
elseif ($MissingPins) {
    throw '-MissingPins is for -Runtime win-arm64.'
}
if ($RuntimeTo -and $Runtime -ne 'win-x64') {
    throw "-RuntimeTo is for win-x64: the $Runtime builds of rhwp and Unhwp link the C runtime statically."
}

$cache = Join-Path $PSScriptRoot '.cache'
New-Item -ItemType Directory -Force -Path $cache, $Destination | Out-Null
$Destination = (Resolve-Path $Destination).Path

# The download for this runtime: the ARM64 pin for -Runtime win-arm64 where there is one, else engines.json.
function Get-Engine([string]$name, [switch]$X64) {
    $info = $engines[$name]
    if ($Runtime -eq 'win-arm64' -and -not $X64 -and $arm64Pins.Contains($name)) {
        $info = $arm64Pins[$name]
        if ($info.Sha256 -notmatch '^[0-9a-fA-F]{64}$') {
            throw "$name for win-arm64 is not pinned yet. Run the ""Windows ARM64 native"" workflow (.github/workflows/windows-arm64-native.yml), host its file at $($info.Url) and set its SHA-256 in build/fetch-engines.ps1 (`$arm64Pins)."
        }
    }
    $file = Join-Path $cache ([Uri]::UnescapeDataString(($info.Url -split '/')[-1]))
    if ((Test-Path $file) -and (Get-FileHash $file -Algorithm SHA256).Hash -eq $info.Sha256) {
        Write-Host "ok   $name (cached)"
        return $file
    }
    Write-Host "get  $name  $($info.Url)"
    Invoke-WebRequest -Uri $info.Url -OutFile $file -UseBasicParsing
    $hash = (Get-FileHash $file -Algorithm SHA256).Hash
    if ($hash -ne $info.Sha256) {
        Remove-Item $file
        throw "Checksum mismatch for $name. Expected $($info.Sha256), got $hash."
    }
    return $file
}

function Reset-Folder([string]$path) {
    if (Test-Path $path) { Remove-Item $path -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $path | Out-Null
}

# Moves the content of the single top-level folder of an extracted archive up one level.
function Expand-Flat([string]$zip, [string]$target) {
    $tmp = Join-Path $cache ('x-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    Expand-Archive -Path $zip -DestinationPath $tmp
    $items = Get-ChildItem $tmp
    $root = if ($items.Count -eq 1 -and $items[0].PSIsContainer) { $items[0].FullName } else { $tmp }
    Reset-Folder $target
    Get-ChildItem $root -Force | Move-Item -Destination $target
    Remove-Item $tmp -Recurse -Force
}

# Unpacks the Windows binaries (Library/bin) of a conda-forge package: a zip holding pkg-*.tar.zst. Windows' own
# tar.exe (bsdtar with zstd, Windows 10 1803 and later) reads the tarball; the app uses a managed decompressor.
function Expand-Conda([string]$conda, [string]$target) {
    $tmp = Join-Path $cache ('c-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($conda, $tmp)
    $pkg = Get-ChildItem $tmp -Filter 'pkg-*.tar.zst' | Select-Object -First 1
    if (-not $pkg) { throw "$conda is not a conda package." }
    $files = Join-Path $tmp 'files'
    New-Item -ItemType Directory -Force -Path $files | Out-Null
    & (Join-Path $env:SystemRoot 'System32\tar.exe') -xf $pkg.FullName -C $files
    if ($LASTEXITCODE -ne 0) { throw "tar.exe could not unpack $($pkg.Name) (it needs zstd support)." }
    Reset-Folder $target
    Get-ChildItem (Join-Path $files 'Library\bin') -Force | Move-Item -Destination $target
    Remove-Item $tmp -Recurse -Force
}

# Copies the Microsoft C++ runtime DLLs (the "vcruntime" component, unpacked once per run into build/.cache) into
# $target, next to a program built with Visual C++: Windows looks for its DLLs in the program's folder first, so it
# runs on PCs without the Visual C++ Redistributable. Same as EngineInstaller does for Ghostscript.
$script:vcRuntime = $null
function Copy-VcRuntime([string]$target) {
    if (-not $script:vcRuntime) {
        $script:vcRuntime = Join-Path $cache 'vcruntime'
        Expand-Conda (Get-Engine 'vcruntime') $script:vcRuntime
    }
    Copy-Item (Join-Path $script:vcRuntime '*.dll') $target -Force
    foreach ($dll in 'msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll') {
        if (-not (Test-Path (Join-Path $target $dll))) { throw "$dll is missing in $target after copying the C++ runtime." }
    }
}

# Removes parts of LibreOffice that headless conversion never uses (~500 MB):
# help, readmes, the MSI stub copied by the admin install, the gallery, and spell-check dictionaries except
# English and Korean (their hyphenation patterns can affect page layout when exporting PDFs).
function Optimize-LibreOffice([string]$root) {
    $remove = @('help', 'readmes', 'share\gallery') | ForEach-Object { Join-Path $root $_ }
    $remove += Get-ChildItem $root -Filter '*.msi' | ForEach-Object FullName
    $remove += Get-ChildItem (Join-Path $root 'share\extensions') -Directory -Filter 'dict-*' |
        Where-Object { $_.Name -notin @('dict-en', 'dict-ko') } | ForEach-Object FullName
    foreach ($path in $remove) { Remove-Item $path -Recurse -Force -ErrorAction SilentlyContinue }
    # The MSI installs LibreOffice's C++ runtime into the system folder; the administrative install leaves it in
    # System64, where soffice.exe never looks. Same as AppLocalRuntime.CopyLibreOfficeRuntime.
    $system64 = Join-Path $root 'System64'
    if (Test-Path $system64) {
        foreach ($dll in Get-ChildItem $system64 -Filter '*.dll') {
            $target = Join-Path $root "program\$($dll.Name)"
            if (-not (Test-Path $target)) { Copy-Item $dll.FullName $target }
        }
    }
}

$selected = if ($Only) { @($Only -split ',' | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ }) } else { @($engines.Keys) }
# Unhwp's library is only downloaded for win-arm64, and only when asked for (the x64 one comes in its NuGet package).
$valid = @($engines.Keys) + @(if ($Runtime -eq 'win-arm64') { 'unhwp' })
foreach ($name in $selected) {
    if ($name -notin $valid) { throw "Unknown engine '$name' for $Runtime. Valid: $($valid -join ', ')" }
}

if ('unhwp' -in $selected) {
    $target = Join-Path $Destination 'unhwp'
    Reset-Folder $target
    Copy-Item (Get-Engine 'unhwp') (Join-Path $target 'unhwp.dll')
}

if ('rhwp' -in $selected) {
    $zip = Get-Engine 'rhwp'
    Expand-Flat $zip (Join-Path $Destination 'rhwp')
    if (-not (Get-ChildItem (Join-Path $Destination 'rhwp') -Recurse -Filter 'rhwp.exe')) { throw 'rhwp.exe not found in the archive.' }
    # Keep rhwp.exe directly in engines/rhwp.
    $exe = Get-ChildItem (Join-Path $Destination 'rhwp') -Recurse -Filter 'rhwp.exe' | Select-Object -First 1
    if ($exe.DirectoryName -ne (Join-Path $Destination 'rhwp')) {
        Get-ChildItem $exe.DirectoryName -Force | Move-Item -Destination (Join-Path $Destination 'rhwp') -Force
    }
    # rhwp.exe imports MSVCP140.dll and VCRUNTIME140.dll; without them Windows does not start it (#248). The ARM64
    # build imports neither.
    if ($Runtime -eq 'win-x64') {
        Copy-VcRuntime (Join-Path $Destination 'rhwp')
    }
}

if ('pandoc' -in $selected) {
    $zip = Get-Engine 'pandoc'
    Expand-Flat $zip (Join-Path $Destination 'pandoc')
    if (-not (Test-Path (Join-Path $Destination 'pandoc\pandoc.exe'))) { throw 'pandoc.exe not found in the archive.' }
}

if ('7zip' -in $selected) {
    # The official MSI unpacked with an administrative install (files only, no system changes). Only the console
    # program is kept: 7z.exe + 7z.dll (all formats, incl. RAR), the license and the readme; not the GUI or help.
    # win-arm64: there is no ARM64 MSI, only the installer program, a 7z self-extracting archive. Windows' tar.exe
    # can't undo its ARM64 executable filter, so the x64 7-Zip from the MSI unpacks it (under emulation on ARM64).
    $msi = Get-Engine '7zip' -X64
    $tmp = Join-Path $cache '7z-admin'
    Reset-Folder $tmp
    $proc = Start-Process msiexec.exe -ArgumentList @('/a', "`"$msi`"", '/qn', "TARGETDIR=`"$tmp`"") -Wait -PassThru
    if ($proc.ExitCode -ne 0) { throw "msiexec /a failed for 7-Zip with exit code $($proc.ExitCode)." }

    $exe = Get-ChildItem $tmp -Recurse -Filter '7z.exe' | Select-Object -First 1
    if (-not $exe) { throw '7z.exe not found after extracting the 7-Zip MSI.' }
    $target = Join-Path $Destination '7zip'
    Reset-Folder $target
    $files = @('7z.exe', '7z.dll', 'License.txt', 'readme.txt')
    if ($Runtime -eq 'win-arm64') {
        & $exe.FullName x -y "-o$target" (Get-Engine '7zip') @files | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "7-Zip could not unpack its ARM64 installer (exit code $LASTEXITCODE)." }
        foreach ($name in $files) {
            if (-not (Test-Path (Join-Path $target $name))) { throw "$name not found in the 7-Zip ARM64 installer." }
        }
        # Under x64 emulation 7z.dll stays locked for a moment after 7z.exe exits; the next run clears the rest.
        foreach ($attempt in 1..10) {
            Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
            if (-not (Test-Path $tmp)) { break }
            Start-Sleep -Seconds 1
        }
        if (Test-Path $tmp) { Write-Warning "Could not remove $tmp yet." }
    }
    else {
        foreach ($name in $files) {
            Copy-Item (Join-Path $exe.DirectoryName $name) $target
        }
        Remove-Item $tmp -Recurse -Force
    }
}

if ('ffmpeg' -in $selected) {
    # Same layout as the app's installer: the archive's single top folder is dropped, the programs stay in bin/.
    $zip = Get-Engine 'ffmpeg'
    Expand-Flat $zip (Join-Path $Destination 'ffmpeg')
    foreach ($exe in 'ffmpeg.exe', 'ffprobe.exe') {
        if (-not (Test-Path (Join-Path $Destination "ffmpeg\bin\$exe"))) { throw "$exe not found in the archive." }
    }
}

if ('jre' -in $selected) {
    $zip = Get-Engine 'jre'
    Expand-Flat $zip (Join-Path $Destination 'jre')
}

if ('libreoffice' -in $selected) {
    $msi = Get-Engine 'libreoffice'
    $tmp = Join-Path $cache 'lo-admin'
    Reset-Folder $tmp
    Write-Host 'unpack LibreOffice (administrative install, no system changes)'
    $proc = Start-Process msiexec.exe -ArgumentList @('/a', "`"$msi`"", '/qn', "TARGETDIR=`"$tmp`"") -Wait -PassThru
    if ($proc.ExitCode -ne 0) { throw "msiexec /a failed with exit code $($proc.ExitCode)." }

    $soffice = Get-ChildItem $tmp -Recurse -Filter 'soffice.exe' | Select-Object -First 1
    if (-not $soffice) { throw 'soffice.exe not found after extracting the MSI.' }
    $root = Split-Path $soffice.DirectoryName -Parent   # folder containing program/, share/, ...
    $target = Join-Path $Destination 'libreoffice'
    Reset-Folder $target
    Get-ChildItem $root -Force | Move-Item -Destination $target
    Remove-Item $tmp -Recurse -Force

    Optimize-LibreOffice $target
}

if ('h2orestart' -in $selected) {
    $oxt = Get-Engine 'h2orestart'
    $lo = Join-Path $Destination 'libreoffice'
    if (-not (Test-Path (Join-Path $lo 'program\soffice.exe'))) {
        Write-Warning 'H2Orestart skipped: LibreOffice is not in the destination folder.'
    }
    else {
        # Bundled extensions are plain folders under share/extensions; LibreOffice registers them on start-up.
        $target = Join-Path $lo 'share\extensions\H2Orestart'
        Reset-Folder $target
        $zipCopy = Join-Path $cache 'H2Orestart.zip'
        Copy-Item $oxt $zipCopy -Force
        Expand-Archive -Path $zipCopy -DestinationPath $target -Force
        Remove-Item $zipCopy
    }
}

if ('vcruntime' -in $selected -or 'ghostscript' -in $selected) {
    Expand-Conda (Get-Engine 'vcruntime') (Join-Path $Destination 'vcruntime')
}

if ('ghostscript' -in $selected) {
    $target = Join-Path $Destination 'ghostscript'
    Expand-Conda (Get-Engine 'ghostscript') $target
    if (-not (Test-Path (Join-Path $target 'gswin64c.exe'))) { throw 'gswin64c.exe not found in the Ghostscript package.' }
    # App-local Microsoft C++ runtime next to gswin64c.exe, as EngineInstaller does.
    Copy-VcRuntime $target
}

if ('tesseract' -in $selected) {
    # Trimmed like EngineInstaller.TrimTesseract: no training programs, manuals, installer files or language data.
    $setup = Get-Engine 'tesseract'
    $sevenZip = Join-Path $Destination '7zip\7z.exe'
    if (-not (Test-Path $sevenZip)) { throw 'Tesseract is unpacked with engines/7zip: use -Only 7zip,tesseract.' }
    $target = Join-Path $Destination 'tesseract'
    Reset-Folder $target
    & $sevenZip x -y "-o$target" $setup | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "7-Zip could not unpack the Tesseract installer (exit code $LASTEXITCODE)." }
    foreach ($folder in '$PLUGINSDIR', 'tessdata') { Remove-Item -LiteralPath (Join-Path $target $folder) -Recurse -Force -ErrorAction SilentlyContinue }
    Get-ChildItem $target -File | Where-Object { $_.Extension -in '.exe', '.html' -and $_.Name -ne 'tesseract.exe' } | Remove-Item -Force
    if (-not (Test-Path (Join-Path $target 'tesseract.exe'))) { throw 'tesseract.exe not found in the installer.' }
}

if ('calibre' -in $selected) {
    $msi = Get-Engine 'calibre'
    $tmp = Join-Path $cache 'calibre-admin'
    Reset-Folder $tmp
    Write-Host 'unpack calibre (administrative install, no system changes)'
    $proc = Start-Process msiexec.exe -ArgumentList @('/a', "`"$msi`"", '/qn', "TARGETDIR=`"$tmp`"") -Wait -PassThru
    if ($proc.ExitCode -ne 0) { throw "msiexec /a failed with exit code $($proc.ExitCode)." }

    $convert = Get-ChildItem $tmp -Recurse -Filter 'ebook-convert.exe' | Select-Object -First 1
    if (-not $convert) { throw 'ebook-convert.exe not found after extracting the MSI.' }
    $target = Join-Path $Destination 'calibre'
    Reset-Folder $target
    Get-ChildItem $convert.DirectoryName -Force | Move-Item -Destination $target
    Remove-Item $tmp -Recurse -Force
}

foreach ($folder in $RuntimeTo) {
    if (-not (Test-Path $folder -PathType Container)) { throw "-RuntimeTo: $folder is not a folder." }
    Copy-VcRuntime $folder
}

Write-Host "Engines ready in $Destination"
