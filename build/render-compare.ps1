<#
.SYNOPSIS
  Compares Filee's document renderers with the PDFs 한글 printed, over a private corpus outside the repository.

.DESCRIPTION
  The corpus is a folder (searched recursively) of documents next to the PDF 한글 printed of each: doc.hwpx + doc.pdf,
  or doc.hwp + doc.pdf. Nothing of it is copied into the repository. Every document is rendered by rhwp (fetch it with
  build/fetch-engines.ps1 -Only rhwp) and, once one is registered, by Filee's layout engine; each rendering is compared
  with 한글's PDF (page count, line breaks, baselines, per-page SSIM at 100 dpi) and read back (text, overlapping
  glyphs, glyphs outside the page, embedded fonts). The results go to an HTML report with the page that differs most of
  each document side by side.

  It runs RenderCorpusTests in tests/Filee.Engines.Tests (docs/RENDER-QUALITY.md).

.PARAMETER Corpus
  The corpus folder. Defaults to the FILEE_RENDER_CORPUS environment variable.

.PARAMETER Report
  The HTML report to write. Defaults to filee-render-compare-<time>.html in the temp folder.

.PARAMETER Open
  Opens the report when done.

.EXAMPLE
  pwsh build/render-compare.ps1 -Corpus D:\hwp-corpus -Open
#>
[CmdletBinding()]
param(
    [string]$Corpus = $env:FILEE_RENDER_CORPUS,
    [string]$Report = (Join-Path ([IO.Path]::GetTempPath()) "filee-render-compare-$(Get-Date -Format 'yyyyMMdd-HHmmss').html"),
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$Open
)

$ErrorActionPreference = 'Stop'
if (-not $Corpus) { throw 'Name the corpus folder with -Corpus or FILEE_RENDER_CORPUS.' }
if (-not (Test-Path $Corpus -PathType Container)) { throw "Corpus folder not found: $Corpus" }

$repository = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repository 'tests/Filee.Engines.Tests/Filee.Engines.Tests.csproj'

$env:FILEE_RENDER_CORPUS = (Resolve-Path $Corpus).Path
$env:FILEE_RENDER_REPORT = [IO.Path]::GetFullPath($Report)
try {
    dotnet test $project -c $Configuration --filter 'FullyQualifiedName~RenderQuality.RenderCorpusTests|FullyQualifiedName~RenderQuality.HangulLineBreakTests' --logger 'console;verbosity=normal'
    if ($LASTEXITCODE -ne 0) { throw "The compare failed (exit $LASTEXITCODE)." }
}
finally {
    Remove-Item Env:FILEE_RENDER_REPORT -ErrorAction SilentlyContinue
}

if (-not (Test-Path $Report)) { throw "No report was written to $Report." }
Write-Host "Report: $Report"
if ($Open) { Invoke-Item $Report }
