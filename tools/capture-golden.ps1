# capture-golden.ps1 - freeze the harvester's current output as the reference baseline
#
# Phase 0 of docs/refactor-plan.md. Every later phase is judged by diffing its output
# against these files, so this script must be boring and deterministic:
#   * it runs the real pipeline (no shortcuts, no fake data)
#   * it splits the merged CSV by the Drawing column, preserving row order
#   * it writes bytes, not re-serialised objects, so quoting is never rewritten
#   * it writes a checksum per drawing, so a diff check is one comparison
#
# The baseline is deliberately re-captured after each behaviour-changing phase
# (ground rule 3 in the plan). Re-capturing is a reviewed decision, not a convenience.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\capture-golden.ps1
#   ... -SkipRun      split the existing Output\Extracted_BOM.csv without harvesting
#   ... -Check        compare current output against the baseline; do not overwrite
#
# Exit codes: 0 = captured (or identical, with -Check), 1 = failed, 2 = -Check found drift

[CmdletBinding()]
param(
    [string] $Root,
    [string] $GoldenDir,
    [switch] $SkipRun,
    [switch] $Check
)

$ErrorActionPreference = 'Stop'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

# $MyInvocation is not populated yet when parameter defaults are evaluated, so the
# project root is resolved here, from the script's own folder.
if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }
if (-not $GoldenDir) { $GoldenDir = Join-Path $Root 'tests\golden' }

$csvPath = Join-Path $Root 'Output\Extracted_BOM.csv'
$bat     = Join-Path $Root 'RunPipeline.bat'

function Fail([string] $message) {
    Write-Host "[FAIL] $message" -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------- 1. harvest
if (-not $SkipRun) {
    if (-not (Test-Path $bat)) { Fail "RunPipeline.bat not found at $bat" }

    Write-Host 'Running the pipeline (fresh output) ...'
    # /q keeps it non-interactive. Fresh is the default, so the CSV is rebuilt, not appended.
    & cmd /c "`"$bat`" /q"
    if ($LASTEXITCODE -ne 0) { Fail "RunPipeline.bat exited $LASTEXITCODE" }
}

if (-not (Test-Path $csvPath)) { Fail "No CSV at $csvPath - run without -SkipRun first." }

# ------------------------------------------------- 2. split by Drawing column
# Read raw lines so quoting and number formatting survive byte-for-byte.
$lines = [System.IO.File]::ReadAllLines($csvPath)
if ($lines.Count -lt 2) { Fail "CSV has $($lines.Count) line(s); expected a header plus rows." }

$header = $lines[0]

function Get-FirstField([string] $line) {
    # RFC 4180: the Drawing field is quoted only when it needs to be.
    if ($line.Length -gt 0 -and $line[0] -eq '"') {
        $sb = New-Object System.Text.StringBuilder
        $i = 1
        while ($i -lt $line.Length) {
            if ($line[$i] -eq '"') {
                if ($i + 1 -lt $line.Length -and $line[$i + 1] -eq '"') {
                    [void]$sb.Append('"')
                    $i += 2
                    continue
                }
                return $sb.ToString()
            }
            [void]$sb.Append($line[$i])
            $i++
        }
        return $sb.ToString()   # unterminated quote; take what we have
    }

    $comma = $line.IndexOf(',')
    if ($comma -lt 0) { return $line }
    return $line.Substring(0, $comma)
}

# Group in encounter order, which keeps each drawing's rows byte-identical to the source.
$order  = New-Object System.Collections.ArrayList
$groups = @{}
$total  = 0

for ($i = 1; $i -lt $lines.Count; $i++) {
    $line = $lines[$i]
    if ([string]::IsNullOrWhiteSpace($line)) { continue }

    $drawing = Get-FirstField $line
    if (-not $groups.ContainsKey($drawing)) {
        $groups[$drawing] = New-Object System.Collections.ArrayList
        [void]$order.Add($drawing)
    }
    [void]$groups[$drawing].Add($line)
    $total++
}

if ($order.Count -eq 0) { Fail 'CSV contains a header but no data rows.' }

# ----------------------------------------------------------- 3. write files
if (-not (Test-Path $GoldenDir)) { New-Item -ItemType Directory -Path $GoldenDir -Force | Out-Null }

function Get-GoldenName([string] $drawing) {
    $stem = [System.IO.Path]::GetFileNameWithoutExtension($drawing)
    if ([string]::IsNullOrWhiteSpace($stem)) { $stem = 'drawing' }

    $invalid = [System.IO.Path]::GetInvalidFileNameChars()
    $sb = New-Object System.Text.StringBuilder
    foreach ($ch in $stem.ToCharArray()) {
        if ([System.Array]::IndexOf($invalid, $ch) -ge 0) { [void]$sb.Append('_') }
        else { [void]$sb.Append($ch) }
    }
    return $sb.ToString().Trim()
}

$summary = New-Object System.Collections.ArrayList
[void]$summary.Add('"Drawing","Rows","Bytes","Sha256","File"')

$changed = 0
foreach ($drawing in $order) {
    $rows = $groups[$drawing]
    $name = Get-GoldenName $drawing
    $file = Join-Path $GoldenDir ($name + '.csv')

    # CRLF everywhere, with a trailing CRLF: the format the pipeline writes, and the
    # only one that diffs cleanly across tools that care about line endings.
    $text = $header + "`r`n" + (($rows -join "`r`n")) + "`r`n"
    $bytes = $utf8NoBom.GetBytes($text)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    $hash = [System.BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '').ToLowerInvariant()
    $sha.Dispose()

    $exists = Test-Path $file
    $same = $false
    if ($exists) {
        $old = [System.IO.File]::ReadAllBytes($file)
        if ($old.Length -eq $bytes.Length) {
            $same = $true
            for ($k = 0; $k -lt $bytes.Length; $k++) {
                if ($old[$k] -ne $bytes[$k]) { $same = $false; break }
            }
        }
    }

    if (-not $same) { $changed++ }

    if (-not $Check) {
        [System.IO.File]::WriteAllBytes($file, $bytes)
    }

    [void]$summary.Add(('"{0}",{1},{2},"{3}","{4}"' -f $drawing, $rows.Count, $bytes.Length, $hash, ($name + '.csv')))

    $state = if ($same) { 'unchanged' } elseif ($exists) { 'CHANGED' } else { 'new' }
    Write-Host ('  {0,-26} {1,6} rows  {2}' -f $name, $rows.Count, $state)
}

$summaryText = ($summary -join "`r`n") + "`r`n"
$summaryFile = Join-Path $GoldenDir 'summary.csv'

if (-not $Check) { [System.IO.File]::WriteAllText($summaryFile, $summaryText, $utf8NoBom) }

Write-Host ''
Write-Host ('Drawings : {0}' -f $order.Count)
Write-Host ('Rows     : {0}' -f $total)
Write-Host ('Baseline : {0}' -f $GoldenDir)

if ($Check) {
    if ($changed -gt 0) {
        Write-Host ("[DRIFT] {0} of {1} drawing(s) differ from the baseline." -f $changed, $order.Count) -ForegroundColor Yellow
        Write-Host '        If this phase was meant to change behaviour, review the diff, then re-capture.'
        exit 2
    }
    Write-Host '[OK] Output matches the baseline exactly.' -ForegroundColor Green
    exit 0
}

if ($changed -gt 0) {
    Write-Host ("[INFO] {0} file(s) written or updated. Review the diff before committing." -f $changed) -ForegroundColor Yellow
} else {
    Write-Host '[OK] Baseline already current - capture is reproducible.' -ForegroundColor Green
}

exit 0
