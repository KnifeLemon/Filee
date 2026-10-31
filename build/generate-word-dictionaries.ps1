<#
.SYNOPSIS
  Generates the word lists the layout engine breaks Thai, Lao, Khmer and Myanmar lines with
  (src/Filee.Engines/Typesetting/Dictionaries/*.words.br) from ICU's dictionaries.

.DESCRIPTION
  These scripts write words without spaces between them, so UAX #14 leaves their line breaks to a dictionary (Line_Break
  SA). ICU's word lists for them (icu4c/source/data/brkitr/dictionaries) are downloaded at a pinned release into
  build/.cache, verified with SHA-256, and written as sorted lists: each word as the number of UTF-16 units it shares
  with the word before it (one byte), the rest of it in UTF-8 and a line feed, the whole list compressed with Brotli.
  Typesetting/WordDictionary.cs reads them back. The generated files are committed, so building Filee needs neither
  the script nor the network.

  The data is Copyright (c) Unicode, Inc. and others, under the Unicode License v3 (see THIRD-PARTY-NOTICES.md).

.PARAMETER Output
  The folder to write. Default: src/Filee.Engines/Typesetting/Dictionaries.

.EXAMPLE
  pwsh build/generate-word-dictionaries.ps1
#>
[CmdletBinding()]
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\src\Filee.Engines\Typesetting\Dictionaries')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$Release = 'release-77-1'
# ICU dictionary => (output name, SHA-256). Update together with $Release.
$files = [ordered]@{
    'thaidict.txt'    = @('thai', '3166ABDE40C0F44AB91C28F5CE96D7D1472CB7882E1C0BDA0A72F8F69DBA4274')
    'laodict.txt'     = @('lao', '3C876934A3FA81031D2333525EAFACA6A7C9F842E3B98F18C38880420AFB5D36')
    'khmerdict.txt'   = @('khmer', '87BEE2D17CD5148AA36957EB05409EEFC124DE8AD519B81B789298EF3E60B5D9')
    'burmesedict.txt' = @('burmese', '61D8ABC3D9102B2F9BF0C9F44DB0D7AB89B18172D8CD26832E4C83174BD8673B')
}

$cache = Join-Path $PSScriptRoot ".cache\icu-$Release"
New-Item -ItemType Directory -Force -Path $cache, $Output | Out-Null

foreach ($name in $files.Keys) {
    $short, $expected = $files[$name]
    $file = Join-Path $cache $name
    if (-not ((Test-Path $file) -and (Get-FileHash $file -Algorithm SHA256).Hash -eq $expected)) {
        $url = "https://raw.githubusercontent.com/unicode-org/icu/$Release/icu4c/source/data/brkitr/dictionaries/$name"
        Write-Host "get  $url"
        Invoke-WebRequest -Uri $url -OutFile $file -UseBasicParsing
        $actual = (Get-FileHash $file -Algorithm SHA256).Hash
        if ($actual -ne $expected) {
            Remove-Item $file
            throw "Checksum mismatch for $name. Expected $expected, got $actual."
        }
    }

    $words = [System.Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    foreach ($line in [IO.File]::ReadAllLines($file, [Text.Encoding]::UTF8)) {
        $word = $line.Trim([char]0xFEFF, ' ', "`t")
        if ($word.Length -gt 0 -and -not $word.StartsWith('#')) { [void]$words.Add($word) }
    }

    $raw = [IO.MemoryStream]::new()
    $previous = ''
    foreach ($word in $words) {
        $shared = 0
        $limit = [Math]::Min([Math]::Min($previous.Length, $word.Length), 255)
        while ($shared -lt $limit -and $previous[$shared] -eq $word[$shared]) { $shared++ }
        # A surrogate pair stays whole.
        if ($shared -gt 0 -and [char]::IsHighSurrogate($word[$shared - 1])) { $shared-- }
        $raw.WriteByte([byte]$shared)
        $bytes = [Text.Encoding]::UTF8.GetBytes($word.Substring($shared))
        $raw.Write($bytes, 0, $bytes.Length)
        $raw.WriteByte(10)
        $previous = $word
    }

    $target = Join-Path $Output "$short.words.br"
    $stream = [IO.File]::Create($target)
    $brotli = [IO.Compression.BrotliStream]::new($stream, [IO.Compression.CompressionLevel]::SmallestSize)
    $raw.Position = 0
    $raw.CopyTo($brotli)
    $brotli.Dispose()
    $stream.Dispose()
    Write-Host ("{0,-8} {1,6} words, {2,7} bytes" -f $short, $words.Count, (Get-Item $target).Length)
}
