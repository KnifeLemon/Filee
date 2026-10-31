<#
.SYNOPSIS
  Generates the Unicode property tables of Filee's layout engine
  (src/Filee.Engines/Typesetting/UnicodeData.Generated.cs) from the Unicode Character Database.

.DESCRIPTION
  Writes Line_Break (UAX #14), Bidi_Class (UAX #9), Script (UAX #24) and East_Asian_Width (UAX #11) as range tables
  that Typesetting/UnicodeData.cs searches, and the paired brackets of the bidirectional algorithm (BidiBrackets.txt,
  rule N0) as a sorted list. Every code point is covered: unlisted ones get the defaults the files
  declare in their "@missing" lines. Each enum lists the property's overall default first (XX, L, N, Unknown), so its
  value 0 is what code points outside the code space get.

  The version is pinned to UCD 16.0.0, the one .NET 10 uses for System.Globalization (CharUnicodeInfo, Rune,
  StringInfo), so the layout engine and .NET agree on which characters exist and where grapheme clusters end. Move
  both together: when the target framework changes its Unicode version, update $Version and the hashes below and run
  the script again.

  The UCD files are downloaded from unicode.org into build/.cache and verified with SHA-256; they are not committed.
  The generated C# file is committed, so building Filee needs neither the script nor the network.

.PARAMETER Output
  The C# file to write. Default: src/Filee.Engines/Typesetting/UnicodeData.Generated.cs.

.EXAMPLE
  pwsh build/generate-unicode-tables.ps1
#>
[CmdletBinding()]
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\src\Filee.Engines\Typesetting\UnicodeData.Generated.cs')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # Invoke-WebRequest is ~10x faster without the progress bar

$Version = '16.0.0'
# UCD file => SHA-256. Update together with $Version.
$files = [ordered]@{
    'LineBreak.txt'                  = 'E97E4259D0D20FAB150B9C7B4B28ABFAE5CD78CA97E7F4AC6ED20D685D5F4A7C'
    'extracted/DerivedBidiClass.txt' = '71ED943A49C58568D8D92E80ECC2BA2F06E62AEE9C8EBB0E6E8BD2C3ED8B180E'
    'Scripts.txt'                    = '9E88F0A677DF47311106340BE8EDE2ECDACD9C1C931831218D2BE6D5508E0039'
    'EastAsianWidth.txt'             = '43ADC76C0686A42CB370764EB8CFE2B2A45B10B855E5572A2DB4A0EECCE15D5B'
    'PropertyValueAliases.txt'       = '440FD3E5460B9BFE31DA67B6F923992E1989D31FE2ED91E091C4B8F8E2620BF9'
    'BidiBrackets.txt'               = 'B8F32554C6F658821FB0EE742D21C5B1F2086B9BF13071FED04894B022F93D67'
}

$cache = Join-Path $PSScriptRoot ".cache\ucd-$Version"
New-Item -ItemType Directory -Force -Path $cache | Out-Null

function Get-UcdFile([string]$name) {
    $expected = $files[$name]
    $file = Join-Path $cache ($name -replace '/', '_')
    if ((Test-Path $file) -and (Get-FileHash $file -Algorithm SHA256).Hash -eq $expected) {
        Write-Host "ok   $name (cached)"
        return $file
    }
    $url = "https://www.unicode.org/Public/$Version/ucd/$name"
    Write-Host "get  $url"
    Invoke-WebRequest -Uri $url -OutFile $file -UseBasicParsing
    $actual = (Get-FileHash $file -Algorithm SHA256).Hash
    if ($actual -ne $expected) {
        Remove-Item $file
        throw "Checksum mismatch for $name. Expected $expected, got $actual."
    }
    return $file
}

# ───────────────────────── Property value names ─────────────────────────

# short name => long name, per property, in the order PropertyValueAliases.txt lists them.
$aliases = @{}
foreach ($line in Get-Content (Get-UcdFile 'PropertyValueAliases.txt')) {
    if ($line -match '^\s*(lb|bc|ea|sc)\s*;\s*([^;#\s]+)\s*;\s*([^;#\s]+)') {
        if (-not $aliases.ContainsKey($Matches[1])) { $aliases[$Matches[1]] = [ordered]@{} }
        $aliases[$Matches[1]][$Matches[2]] = $Matches[3]
    }
}

<# Values of one property for every code point. The "@missing" defaults come first, in file order (later lines are
   more specific), then the data lines. $names maps every name the file uses (short or long) to a value index. #>
function Read-Property([string]$file, [hashtable]$names) {
    $values = [byte[]]::new(0x110000)
    $missing = [System.Collections.Generic.List[object]]::new()
    $data = [System.Collections.Generic.List[object]]::new()
    foreach ($line in Get-Content $file) {
        if ($line -match '^#\s*@missing:\s*([0-9A-F]+)\.\.([0-9A-F]+)\s*;\s*([^;#\s]+)') {
            $missing.Add(@([Convert]::ToInt32($Matches[1], 16), [Convert]::ToInt32($Matches[2], 16), $Matches[3]))
        }
        elseif ($line -match '^([0-9A-F]+)(?:\.\.([0-9A-F]+))?\s*;\s*([^;#\s]+)') {
            $first = [Convert]::ToInt32($Matches[1], 16)
            $last = if ($Matches[2]) { [Convert]::ToInt32($Matches[2], 16) } else { $first }
            $data.Add(@($first, $last, $Matches[3]))
        }
    }
    if ($missing.Count -eq 0) { throw "$file has no @missing line." }
    foreach ($range in @($missing) + @($data)) {
        $name = $range[2]
        if (-not $names.ContainsKey($name)) { throw "$file uses the unknown value '$name'." }
        [Array]::Fill[byte]($values, [byte]$names[$name], $range[0], $range[1] - $range[0] + 1)
    }
    return , $values
}

<# The values as ranges: (start, value) where the value changes. #>
function Get-Ranges([byte[]]$values) {
    $starts = [System.Collections.Generic.List[int]]::new()
    $result = [System.Collections.Generic.List[byte]]::new()
    $previous = -1
    for ($i = 0; $i -lt $values.Length; $i++) {
        if ($values[$i] -ne $previous) {
            $starts.Add($i)
            $result.Add($values[$i])
            $previous = $values[$i]
        }
    }
    return , @($starts, $result)
}

<# A C# enum member name from a UCD long name: "Caucasian_Albanian" => "CaucasianAlbanian". #>
function ConvertTo-Member([string]$name) {
    return (($name -split '_') | ForEach-Object { $_.Substring(0, 1).ToUpperInvariant() + $_.Substring(1) }) -join ''
}

# ───────────────────────── Writing ─────────────────────────

$out = [System.Text.StringBuilder]::new()
function Line([string]$text = '') { [void]$out.Append($text).Append("`n") }

<# One property: the enum (members in $order, as short or long names) and its range table. #>
function Write-Property([string]$enum, [string]$summary, [string]$table, [string]$file, [string]$property,
    [string[]]$order, [switch]$LongMembers) {
    $names = @{}
    $long = $aliases[$property]
    for ($i = 0; $i -lt $order.Count; $i++) {
        $short = $order[$i]
        $names[$short] = $i
        $names[$long[$short]] = $i
    }
    $values = Read-Property $file $names
    $ranges = Get-Ranges $values
    Write-Host ("{0,-16} {1,3} values, {2,5} ranges" -f $enum, $order.Count, $ranges[0].Count)

    Line "/// <summary>$summary</summary>"
    Line "internal enum $enum : byte"
    Line '{'
    for ($i = 0; $i -lt $order.Count; $i++) {
        $short = $order[$i]
        if ($LongMembers) {
            Line "    /// <summary>$short</summary>"
            Line "    $(ConvertTo-Member $long[$short]),"
        }
        else {
            Line "    /// <summary>$($long[$short] -replace '_', ' ')</summary>"
            Line "    $short,"
        }
        if ($i -lt $order.Count - 1) { Line }
    }
    Line '}'
    Line

    $script:tables += , @($table, $enum, $ranges)
}

$script:tables = @()
$lb = $aliases['lb'].Keys | Where-Object { $_ -ne 'XX' }
$bc = $aliases['bc'].Keys | Where-Object { $_ -ne 'L' }
$ea = $aliases['ea'].Keys | Where-Object { $_ -ne 'N' }
$sc = $aliases['sc'].Keys | Where-Object { $_ -notin 'Zzzz', 'Zyyy', 'Zinh' }

Line '// <auto-generated>'
Line "// Generated by build/generate-unicode-tables.ps1 from the Unicode Character Database $Version (LineBreak.txt,"
Line '// DerivedBidiClass.txt, BidiBrackets.txt, Scripts.txt, EastAsianWidth.txt and PropertyValueAliases.txt). Do not'
Line '// edit: run the script. The data is Copyright (c) Unicode, Inc., under the Unicode License v3 (see'
Line '// THIRD-PARTY-NOTICES.md).'
Line '// </auto-generated>'
Line
Line 'namespace Filee.Engines.Typesetting;'
Line

Write-Property 'LineBreakClass' 'Line_Break property values (UAX #14), by their short names.' 'LineBreak' `
(Get-UcdFile 'LineBreak.txt') 'lb' (@('XX') + $lb)
Write-Property 'BidiClass' 'Bidi_Class property values (UAX #9), by their short names.' 'Bidi' `
(Get-UcdFile 'extracted/DerivedBidiClass.txt') 'bc' (@('L') + $bc)
Write-Property 'EastAsianWidth' 'East_Asian_Width property values (UAX #11).' 'EastAsianWidth' `
(Get-UcdFile 'EastAsianWidth.txt') 'ea' (@('N') + $ea) -LongMembers
Write-Property 'UnicodeScript' 'Script property values (UAX #24); the summary is the ISO 15924 code.' 'Script' `
(Get-UcdFile 'Scripts.txt') 'sc' (@('Zzzz', 'Zyyy', 'Zinh') + $sc) -LongMembers

Line 'internal static partial class UnicodeData'
Line '{'
Line '    /// <summary>Version of the Unicode Character Database the tables were generated from.</summary>'
Line "    public const string Version = `"$Version`";"
Line
Line '    /// <summary>ISO 15924 code of each <see cref="UnicodeScript"/>, by its value.</summary>'
$codes = @(@('Zzzz', 'Zyyy', 'Zinh') + $sc | ForEach-Object { "`"$_`"" })
Line '    private static readonly string[] ScriptCodes ='
Line '    ['
for ($i = 0; $i -lt $codes.Count; $i += 14) {
    Line ('        ' + ($codes[$i..([Math]::Min($i + 13, $codes.Count - 1))] -join ', ') + ',')
}
Line '    ];'
foreach ($entry in $script:tables) {
    $table, $enum, $ranges = $entry
    $starts, $values = $ranges
    Line
    Line "    /// <summary>First code point of each range of equal <see cref=`"$enum`"/> values, ascending.</summary>"
    Line "    private static ReadOnlySpan<int> ${table}Starts =>"
    Line '    ['
    for ($i = 0; $i -lt $starts.Count; $i += 10) {
        $count = [Math]::Min(10, $starts.Count - $i)
        Line ('        ' + (($starts.GetRange($i, $count) | ForEach-Object { '0x{0:X4}' -f $_ }) -join ', ') + ',')
    }
    Line '    ];'
    Line
    Line "    /// <summary>The <see cref=`"$enum`"/> value of each range in <see cref=`"${table}Starts`"/>.</summary>"
    Line "    private static ReadOnlySpan<byte> ${table}Values =>"
    Line '    ['
    for ($i = 0; $i -lt $values.Count; $i += 24) {
        $count = [Math]::Min(24, $values.Count - $i)
        Line ('        ' + (($values.GetRange($i, $count) | ForEach-Object { "$_" }) -join ', ') + ',')
    }
    Line '    ];'
}

# Paired brackets: code point, its pair, and whether it opens.
$brackets = @(foreach ($line in Get-Content (Get-UcdFile 'BidiBrackets.txt')) {
        if ($line -match '^([0-9A-F]+)\s*;\s*([0-9A-F]+)\s*;\s*([oc])') {
            [pscustomobject]@{ Code = [Convert]::ToInt32($Matches[1], 16); Pair = [Convert]::ToInt32($Matches[2], 16); Open = $Matches[3] -eq 'o' }
        }
    }) | Sort-Object Code
Write-Host ("{0,-16} {1,3} brackets" -f 'BidiBrackets', $brackets.Count)

<# One list of the brackets' values, $perLine to a line. #>
function Write-BracketList([string]$summary, [string]$type, [string]$name, [int]$perLine, [scriptblock]$value) {
    Line
    Line "    /// <summary>$summary</summary>"
    Line "    private static ReadOnlySpan<$type> $name =>"
    Line '    ['
    for ($i = 0; $i -lt $brackets.Count; $i += $perLine) {
        Line ('        ' + (($brackets[$i..([Math]::Min($i + $perLine - 1, $brackets.Count - 1))] | ForEach-Object $value) -join ', ') + ',')
    }
    Line '    ];'
}
Write-BracketList 'Code points with a Bidi_Paired_Bracket, ascending (BidiBrackets.txt).' 'int' 'BracketCodes' 10 { '0x{0:X4}' -f $_.Code }
Write-BracketList 'The Bidi_Paired_Bracket of each code point in <see cref="BracketCodes"/>.' 'int' 'BracketPairs' 10 { '0x{0:X4}' -f $_.Pair }
Write-BracketList 'Whether each code point in <see cref="BracketCodes"/> opens (Bidi_Paired_Bracket_Type Open) or closes.' 'bool' 'BracketOpens' 12 { if ($_.Open) { 'true' } else { 'false' } }
Line '}'

$Output = [IO.Path]::GetFullPath($Output)
[IO.File]::WriteAllText($Output, $out.ToString(), [Text.UTF8Encoding]::new($false))
Write-Host "Wrote $Output"
