<#
.SYNOPSIS
  Signs a release's Windows files and adds them to its draft on GitHub: run on a PC signed in to SimplySign Desktop
  once the release workflow of the tag has finished.

.DESCRIPTION
  The release workflow tests and builds the tag, keeps the unsigned Windows builds (artifacts windows-unsigned and
  windows-arm64-unsigned: the publish folder and the Setup program of each) and drafts the release with the Linux and
  macOS packages. This script:

  1. downloads those builds, exactly as they were tested;
  2. checks out the tag in a worktree and runs its build-installer.ps1 with -CertificateThumbprint for each, which
     signs the unsigned programs and the Setup program, packs Setup.exe, signs it, zips the portable copy and writes
     the SHA-256 sums (x64: Filee-<version>-win-Setup.exe, -win-Portable.zip and Filee-<version>-SHA256SUMS.txt;
     ARM64: Filee-<version>-win-arm64-Setup.exe, -win-arm64-Portable.zip and -win-arm64-SHA256SUMS.txt);
  3. installs the signed Setup into a sandbox (nothing on the PC is touched) and checks that Filee.exe and
     uninstall.exe come out signed, then uninstalls it. An x64 PC can't run the ARM64 Setup: there it checks the
     signatures of the ARM64 Setup.exe and of the signed files that went into it instead (the release workflow ran
     the ARM64 Setup program in a sandbox on Windows on ARM, around a stub app);
  4. signs every SHA256SUMS file of the release (both Windows ones, and the Linux and macOS ones from the draft) with
     Filee's update key (build/sign-checksums.ps1, -UpdateKey), which writes a .sig file next to each;
  5. uploads the Windows files and the .sig files to the draft;
  6. copies the Windows files and their checksum signatures to the download mirror, Cloudflare R2 behind
     https://dl.filee.sh/<version>/, and checks that each one is served there as is (no redirect, the same size). The
     Microsoft Store takes the Setup programs from that address, because GitHub's download links redirect;
  7. with -Publish, publishes the release.

  A tag whose release workflow keeps no ARM64 build (2.4.0 and earlier) gets the x64 files only.

  When the tag's Directory.Build.props carries an update key (FileeUpdatePublicKey), its builds refuse an update
  without signed checksums: the script then stops early without -UpdateKey, checks that the private key belongs to
  that public key, and doesn't publish while a SHA256SUMS file on the draft has no .sig next to it.

  Needs gh (signed in), git, the .NET SDK, and Node.js with Wrangler signed in to Filee's Cloudflare account
  (npx wrangler login, once) unless -NoMirror. Output: build/.cache/release-<version>/out.

.EXAMPLE
  pwsh build/sign-release.ps1 -Version 2.1.0 -CertificateThumbprint <sha1> -UpdateKey D:\keys\filee-update.pem
  pwsh build/sign-release.ps1 -Version 2.1.0 -Publish
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,
    # Defaults to $env:FILEE_SIGNING_CERT (see sign-windows.ps1).
    [string]$CertificateThumbprint = $env:FILEE_SIGNING_CERT,
    # The private update key (build/new-update-key.ps1). Defaults to $env:FILEE_UPDATE_KEY (the path, not the key).
    [string]$UpdateKey = $env:FILEE_UPDATE_KEY,
    [switch]$Publish,
    # Skips the copy to the download mirror (dl.filee.sh).
    [switch]$NoMirror
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Windows releases are signed on Windows.' }
if (-not $CertificateThumbprint) { throw 'Pass -CertificateThumbprint or set FILEE_SIGNING_CERT.' }
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Version = $Version.TrimStart('v')
$tag = "v$Version"

function Invoke-Checked([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed with exit code $LASTEXITCODE" }
}

# The draft and the run that made it.
$release = gh release view $tag --json isDraft | ConvertFrom-Json
if (-not $release.isDraft) { throw "$tag is already published." }
$run = gh run list --workflow release.yml --branch $tag --limit 1 --json databaseId,status,conclusion | ConvertFrom-Json | Select-Object -First 1
if (-not $run) { throw "No release workflow run for $tag." }
if ($run.status -ne 'completed' -or $run.conclusion -ne 'success') { throw "The release run $($run.databaseId) is $($run.status) ($($run.conclusion))." }

# The update key the tag's builds carry: their updater refuses checksum files that aren't signed with it.
Invoke-Checked 'git fetch' { git -C $root fetch --quiet origin "refs/tags/${tag}:refs/tags/$tag" }
$props = [xml](git -C $root show "${tag}:Directory.Build.props" | Out-String)
$publicKey = @($props.Project.PropertyGroup | ForEach-Object { $_.FileeUpdatePublicKey } | Where-Object { $_ -is [string] -and $_.Trim() }) |
    Select-Object -First 1
if ($publicKey -and -not $UpdateKey) {
    throw "$tag carries an update key (FileeUpdatePublicKey): pass -UpdateKey <private key file> or set FILEE_UPDATE_KEY."
}
if ($UpdateKey -and -not (Test-Path -LiteralPath $UpdateKey -PathType Leaf)) { throw "No private update key at $UpdateKey." }

$work = Join-Path $PSScriptRoot ".cache\release-$Version"
$source = Join-Path $work 'source'
# git can't delete paths longer than 260 characters, and the worktree has some once make-setup.cs has built its
# projects in it: PowerShell deletes what is left, and git forgets the worktree.
function Remove-Source {
    if (Test-Path -LiteralPath $source) {
        git -C $root worktree remove --force $source
        if (Test-Path -LiteralPath $source) { Remove-Item -LiteralPath $source -Recurse -Force }
    }
    git -C $root worktree prune
}
Remove-Source
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $work | Out-Null

# The release workflow keeps an ARM64 build after 2.4.0 (#236); older tags have only the x64 one.
$arm64 = (git -C $root show "${tag}:.github/workflows/release.yml" | Out-String) -match 'windows-arm64-unsigned'
Write-Host "download the unsigned Windows build$(if ($arm64) { 's (x64, ARM64)' }) of run $($run.databaseId)"
Invoke-Checked 'gh run download' { gh run download $run.databaseId --name windows-unsigned --dir (Join-Path $work 'unsigned') }
if ($arm64) {
    Invoke-Checked 'gh run download' { gh run download $run.databaseId --name windows-arm64-unsigned --dir (Join-Path $work 'unsigned-arm64') }
}

Invoke-Checked 'git worktree add' { git -C $root worktree add --detach $source $tag }
try {
    $out = Join-Path $work 'out'
    & (Join-Path $source 'build\build-installer.ps1') -Version $Version -Portable -OutputDir $out `
        -PublishDir (Join-Path $work 'unsigned\app') -SetupProgram (Join-Path $work 'unsigned\Filee.Setup.exe') `
        -CertificateThumbprint $CertificateThumbprint
    if ($arm64) {
        & (Join-Path $source 'build\build-installer.ps1') -Version $Version -Portable -OutputDir $out -Runtime win-arm64 `
            -PublishDir (Join-Path $work 'unsigned-arm64\app') -SetupProgram (Join-Path $work 'unsigned-arm64\Filee.Setup.exe') `
            -CertificateThumbprint $CertificateThumbprint
    }
}
finally {
    Remove-Source
}

$setup = Join-Path $out "Filee-$Version-win-Setup.exe"
$files = @($setup, (Join-Path $out "Filee-$Version-win-Portable.zip"), (Join-Path $out "Filee-$Version-SHA256SUMS.txt"))
$setupArm64 = Join-Path $out "Filee-$Version-win-arm64-Setup.exe"
if ($arm64) {
    $files += @($setupArm64, (Join-Path $out "Filee-$Version-win-arm64-Portable.zip"), (Join-Path $out "Filee-$Version-win-arm64-SHA256SUMS.txt"))
}
foreach ($file in $files) { if (-not (Test-Path $file)) { throw "$file was not written." } }

# The signed Setup still finds its payload, and what it installs is signed, the uninstaller too.
$sandbox = Join-Path $work 'sandbox'
function Invoke-Setup([string]$exe) {
    $process = Start-Process -FilePath $exe -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', "/SANDBOX=`"$sandbox`"") -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "$(Split-Path $exe -Leaf) failed in the sandbox with exit code $($process.ExitCode) (logs: $sandbox\Temp)" }
}
# Programs Setup installs that must come out signed: the app's own, and rhwp, which comes unsigned.
$programs = 'Filee.exe', 'filee-cli.exe', 'engines\rhwp\rhwp.exe'
function Assert-Signed([string]$folder, [string[]]$names, [string]$where) {
    foreach ($file in $names) {
        $signature = Get-AuthenticodeSignature (Join-Path $folder $file)
        if ($signature.Status -ne 'Valid') { throw "$file is not signed $where ($($signature.Status))." }
    }
}
function Test-Setup([string]$exe) {
    Write-Host "install the signed $(Split-Path $exe -Leaf) into a sandbox"
    Invoke-Setup $exe
    $installed = Join-Path $sandbox 'Program Files\Filee'
    Assert-Signed $installed ($programs + 'uninstall.exe') 'after installing'
    Invoke-Setup (Join-Path $installed 'uninstall.exe')
    Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}
Test-Setup $setup
if ($arm64 -and [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') {
    Test-Setup $setupArm64
}
elseif ($arm64) {
    # What its payload was packed from, signed in place by build-installer.ps1, and the Setup program, which Setup
    # writes out as uninstall.exe. The release workflow ran the ARM64 Setup program in a sandbox (around a stub app).
    Write-Host 'check the signatures of the ARM64 build (an x64 PC cannot run its Setup)'
    Assert-Signed (Join-Path $work 'unsigned-arm64\app') $programs 'in the ARM64 build'
    Assert-Signed (Join-Path $work 'unsigned-arm64') 'Filee.Setup.exe' 'in the ARM64 build'
    Assert-Signed $out (Split-Path $setupArm64 -Leaf) 'after packing'
}

# Every SHA256SUMS file of the release, signed in one go (one passphrase): the Linux and macOS ones the workflow put
# on the draft, and the new Windows ones.
$signatures = @()
if ($UpdateKey) {
    $sums = Join-Path $work 'sums'
    New-Item -ItemType Directory -Force -Path $sums | Out-Null
    Invoke-Checked 'gh release download' { gh release download $tag --pattern '*SHA256SUMS.txt' --dir $sums --clobber }
    Copy-Item @($files | Where-Object { $_ -like '*SHA256SUMS.txt' }) $sums -Force
    $signatures = @(& (Join-Path $PSScriptRoot 'sign-checksums.ps1') -Folder $sums -PrivateKeyPath $UpdateKey -PublicKey $publicKey)
}

Write-Host "upload to the draft of $tag"
Invoke-Checked 'gh release upload' { gh release upload $tag @files @signatures --clobber }

# The download mirror: R2 bucket filee-downloads, served at dl.filee.sh. Wrangler is pinned like the engines.
if (-not $NoMirror) {
    $bucket = 'filee-downloads'
    $mirror = "https://dl.filee.sh/$Version"
    $types = @{ '.exe' = 'application/vnd.microsoft.portable-executable'; '.zip' = 'application/zip'; '.txt' = 'text/plain; charset=utf-8'
        '.sig' = 'text/plain; charset=utf-8' }
    $windowsSignatures = @($files | Where-Object { $_ -like '*SHA256SUMS.txt' } | ForEach-Object { "$(Split-Path $_ -Leaf).sig" })
    $mirrored = $files + @($signatures | Where-Object { (Split-Path $_ -Leaf) -in $windowsSignatures })
    foreach ($file in $mirrored) {
        $name = Split-Path $file -Leaf
        Write-Host "copy $name to $mirror"
        Invoke-Checked 'wrangler r2 object put' {
            npx --yes wrangler@4.143.1 r2 object put "$bucket/$Version/$name" --file $file --remote `
                --content-type $types[[IO.Path]::GetExtension($name)]
        }
        # One byte is enough: Content-Range says the whole size (a HEAD of a text file has no Content-Length there).
        $response = Invoke-WebRequest "$mirror/$name" -Headers @{ Range = 'bytes=0-0' } -MaximumRedirection 0 -SkipHttpErrorCheck
        $served = "$($response.Headers['Content-Range'])" -replace '^.*/'
        if ($response.StatusCode -ne 206 -or $served -ne "$((Get-Item $file).Length)") {
            throw "$mirror/$name answers $($response.StatusCode) with $served bytes, not the $((Get-Item $file).Length) uploaded."
        }
    }
}
# A build with an update key installs nothing whose checksum file isn't signed: don't publish one without.
if ($publicKey) {
    $assets = @((gh release view $tag --json assets | ConvertFrom-Json).assets.name)
    $unsigned = @($assets | Where-Object { $_ -like '*SHA256SUMS.txt' -and "$_.sig" -notin $assets })
    if ($unsigned.Count) { throw "Not signed on the draft of ${tag}: $($unsigned -join ', '). Run again with -UpdateKey." }
}
if ($Publish) {
    Invoke-Checked 'gh release edit' { gh release edit $tag --draft=false --latest }
    Write-Host "published $tag"
}
else {
    Write-Host "$tag is still a draft: check it on GitHub, then publish it there or run again with -Publish."
}
