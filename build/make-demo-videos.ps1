<#
.SYNOPSIS
  Renders Filee's feature demo videos from the real donut UI (headless Avalonia), frame by frame.

.DESCRIPTION
  Runs the DemoVideoTests "test" in tests/Filee.App.Tests, which only renders when FILEE_DEMO_VIDEOS is set. Each
  clip is drawn at 60 fps over a made-up desktop and encoded with FFmpeg:

    <clip>-square.mp4, <clip>-vertical.mp4   1080x1080 and 1080x1920 H.264 for social video
    <clip>.webp, <clip>.gif                  820 px wide (<= 1.5 MB / <= 3 MB) for README, site and slides
    <clip>-640.webp                          640 px wide (<= 400 KB) for the "What's new" slides

  The dark theme adds "-dark" after the clip's name. Nothing is written to the repository or to %APPDATA% (the tests
  use a throw-away data folder). Run build/fetch-fonts.ps1 first so the text uses Filee's own fonts.

.PARAMETER Output
  Folder for the files (created if missing).

.PARAMETER FFmpeg
  ffmpeg executable. Default: engines/ffmpeg/bin (build/fetch-engines.ps1), else the PATH.

.PARAMETER Clips
  Only these clips (e.g. expand). Default: all.

.PARAMETER Stills
  Also save PNG frames at these times in seconds (into <Output>/stills), e.g. 1.5,3.

.PARAMETER Quick
  Stills only, no video: for checking a change to a clip.

.EXAMPLE
  pwsh build/make-demo-videos.ps1 -Output $env:TEMP\filee-demo
.EXAMPLE
  pwsh build/make-demo-videos.ps1 -Output $env:TEMP\filee-demo -Clips expand -Stills 1,2.5,4 -Quick
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Output,
    [string]$FFmpeg,
    [string[]]$Clips = @(),
    [ValidateSet('square', 'vertical', 'wide')] [string[]]$Formats = @(),
    [ValidateSet('light', 'dark')] [string[]]$Themes = @(),
    [double[]]$Stills = @(),
    [switch]$Quick
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$out = [IO.Path]::GetFullPath($Output)
$env:FILEE_DEMO_VIDEOS = $out
$env:FILEE_FFMPEG = $FFmpeg
$env:FILEE_DEMO_CLIPS = $Clips -join ','
$env:FILEE_DEMO_FORMATS = $Formats -join ','
$env:FILEE_DEMO_THEMES = $Themes -join ','
$env:FILEE_DEMO_STILLS = ($Stills | ForEach-Object { $_.ToString([Globalization.CultureInfo]::InvariantCulture) }) -join ','
$env:FILEE_DEMO_QUICK = if ($Quick) { '1' } else { '' }

try {
    dotnet test (Join-Path $root 'tests/Filee.App.Tests') -c Release --filter 'FullyQualifiedName~DemoVideoTests' --logger 'console;verbosity=normal'
    if ($LASTEXITCODE -ne 0) { throw "Rendering failed ($LASTEXITCODE)." }
}
finally {
    'FILEE_DEMO_VIDEOS', 'FILEE_FFMPEG', 'FILEE_DEMO_CLIPS', 'FILEE_DEMO_FORMATS', 'FILEE_DEMO_THEMES', 'FILEE_DEMO_STILLS', 'FILEE_DEMO_QUICK' |
        ForEach-Object { Remove-Item "Env:$_" -ErrorAction SilentlyContinue }
}

Get-ChildItem $out -File -ErrorAction SilentlyContinue | Where-Object Extension -in '.mp4', '.webp', '.gif' |
    Sort-Object Name | Format-Table Name, @{ n = 'KB'; e = { [math]::Round($_.Length / 1KB) } } -AutoSize
