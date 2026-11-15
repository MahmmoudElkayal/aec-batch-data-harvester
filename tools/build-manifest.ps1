# build-manifest.ps1 - the one place that names a drawing's files
#
# Phase 3 of docs/refactor-plan.md. Two rules, both learned the hard way:
#
#  1. The supervisor owns the key. The plugin reads AEC_HARVEST_KEY verbatim and never
#     derives a name, so there is exactly one implementation of "which files belong to
#     this drawing" instead of one in C# and one in the BAT that agree by luck.
#
#  2. The key must be stable when the folder changes. An index into a sorted list is not:
#     adding a drawing before an existing one renumbers everything after it, and resume
#     then redoes finished work (or worse, skips unfinished work). The key is therefore
#     the readable stem plus a short hash of the relative path - unique, stable, and
#     impossible to confuse with a reserved device name because it always has a suffix.
#
# Output: <Destination>, one line per drawing, "key|full path", sorted by path.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\build-manifest.ps1 -Root . -Destination Output\manifest.txt [-Recurse]
#   ... -SelfTest      verify the key rules against synthetic names (no files needed)
#
# Exit codes: 0 = written (or self-test passed), 1 = refused (duplicate key, no input).

[CmdletBinding()]
param(
    [string] $Root,
    [string] $Destination,
    [switch] $Recurse,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }

function Get-HarvestKey([string] $fileName) {
    $stem = [System.IO.Path]::GetFileNameWithoutExtension($fileName)
    if ($null -eq $stem) { $stem = 'drawing' }

    # Keep the key readable and short enough for a filename on a deep path.
    if ($stem.Length -gt 40) { $stem = $stem.Substring(0, 40) }

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($fileName.ToLowerInvariant())
        $hash = [System.BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }

    return ($stem + '-' + $hash.Substring(0, 8))
}

# ------------------------------------------------------------------ self-test
if ($SelfTest) {
    $failures = 0

    function Assert([bool] $condition, [string] $what) {
        if ($condition) {
            Write-Host "  ok    $what"
        } else {
            Write-Host "  FAIL  $what" -ForegroundColor Red
            $script:failures++
        }
    }

    $a = Get-HarvestKey 'FolderA\Plan.dwg'
    $b = Get-HarvestKey 'FolderB\Plan.dwg'

    Assert ($a -ne $b) 'the same leaf name in two folders gets different keys'
    Assert ($a.StartsWith('Plan-')) 'the key keeps the readable stem'
    Assert ((Get-HarvestKey 'FolderA\Plan.dwg') -eq $a) 'the key is deterministic'
    Assert ($a -match '^[^\\/:*?"<>|]+$') 'the key contains no illegal filename character'

    # Reserved device names are not usable as a file name, even with an extension.
    $reserved = @('CON', 'PRN', 'AUX', 'NUL', 'COM1', 'LPT1')
    $allSafe = $true
    foreach ($name in $reserved) {
        $key = Get-HarvestKey "$name.dwg"
        if ($key -match ('^(?i)' + [regex]::Escape($name) + '$')) { $allSafe = $false }
    }
    Assert $allSafe 'reserved device names are made safe by the suffix'

    $long = Get-HarvestKey (('x' * 200) + '.dwg')
    Assert ($long.Length -le 60) 'a very long name is truncated, not passed through'

    Write-Host ''
    if ($failures -gt 0) { Write-Host "[FAIL] $failures key rule(s) broken"; exit 1 }
    Write-Host '[OK] key rules hold'
    exit 0
}

# --------------------------------------------------------------- the manifest
$inputDir = Join-Path $Root 'Input_DWGs'
$outputDir = Join-Path $Root 'Output'
if (-not $Destination) { $Destination = Join-Path $outputDir 'manifest.txt' }

if (-not (Test-Path $inputDir)) {
    Write-Host "[FAIL] Input folder missing: $inputDir"
    exit 1
}

$files = @(Get-ChildItem $inputDir -Filter *.dwg -File -Recurse:$Recurse |
    Sort-Object -Property @{ Expression = { $_.FullName.ToLowerInvariant() } })

if (-not (Test-Path $outputDir)) { New-Item -ItemType Directory -Path $outputDir -Force | Out-Null }

$lines = New-Object System.Collections.ArrayList
$seen = @{}

foreach ($file in $files) {
    # The relative path, not the full path: moving the project folder must not change
    # every key and invalidate a resume.
    $relative = $file.FullName.Substring($inputDir.Length).TrimStart('\')
    $key = Get-HarvestKey $relative

    if ($seen.ContainsKey($key)) {
        Write-Host "[FAIL] Two drawings map to the same key '$key':"
        Write-Host "         $($seen[$key])"
        Write-Host "         $($file.FullName)"
        Write-Host '       Refusing to write a manifest that would silently merge them.'
        exit 1
    }

    $seen[$key] = $file.FullName
    [void]$lines.Add($key + '|' + $file.FullName)
}

[System.IO.File]::WriteAllText($Destination, (($lines -join "`r`n") + $(if ($lines.Count -gt 0) { "`r`n" } else { '' })), $utf8NoBom)

Write-Host ("Manifest: {0} drawing(s) -> {1}" -f $lines.Count, $Destination)
exit 0
