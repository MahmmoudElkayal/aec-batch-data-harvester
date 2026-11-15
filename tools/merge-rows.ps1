# merge-rows.ps1 - the merged CSVs from the per-drawing files
#
# Phase 3 of docs/refactor-plan.md, extended in Phase 5 for the attribute file.
# Every drawing writes two standalone files:
#
#   Output\_rows\<key>.csv              one row per entity
#   Output\_rows\<key>.attributes.csv   one row per block attribute
#
# and this pass concatenates each family in key order into
#
#   Output\Extracted_BOM.csv
#   Output\Extracted_Attributes.csv
#
# Only published .csv files are read. A .tmp means the drawing was still being written
# when something died, so it is not data yet and the merge must not publish it.
#
# Only drawings that reported .ok are merged. A row file without its verdict is the
# signature of a process killed between publishing rows and publishing the verdict;
# merging it would publish a drawing the batch considers failed.
#
# Deterministic: sorted keys, header from the first file only, CRLF, UTF-8 without BOM
# unless AEC_HARVEST_UTF8_BOM=1, which names the same opt-in the plugin uses.
# Running it twice on the same _rows folder produces identical bytes.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\merge-rows.ps1 -Root .
#
# Exit codes: 0 = merged (or nothing to merge), 1 = failed.

[CmdletBinding()]
param(
    [string] $Root
)

$ErrorActionPreference = 'Stop'

if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }

$rowsDir = Join-Path $Root 'Output\_rows'
$statusDir = Join-Path $Root 'Output\_status'
$entityTarget = Join-Path $Root 'Output\Extracted_BOM.csv'
$attributeTarget = Join-Path $Root 'Output\Extracted_Attributes.csv'

if (-not (Test-Path $rowsDir)) {
    Write-Host "[WARN] No _rows folder at $rowsDir - nothing to merge."
    exit 0
}

# BOM is opt-in for the same reason it is opt-in in the engine: Windows Excel reads
# BOM-less UTF-8 as the local ANSI code page, but parsers do not want a BOM in the
# first field. The merge must agree with the plugin, or the first header field of the
# merged file would differ from the one in every per-drawing file.
$emitBom = ($env:AEC_HARVEST_UTF8_BOM -eq '1')
$utf8 = New-Object System.Text.UTF8Encoding($emitBom)

# ---------------------------------------------------------------- file families
function Get-Key([System.IO.FileInfo] $file) {
    # The attribute suffix must be stripped before the extension, or the .ok for
    # "Plan-1a2b3c4d.attributes.csv" would be looked up as "Plan-1a2b3c4d.attributes.ok".
    $suffix = '.attributes.csv'
    if ($file.Name.EndsWith($suffix, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $file.Name.Substring(0, $file.Name.Length - $suffix.Length)
    }
    return [System.IO.Path]::GetFileNameWithoutExtension($file.Name)
}

function Get-Reported([System.IO.FileInfo] $file) {
    return Test-Path (Join-Path $statusDir ((Get-Key $file) + '.ok'))
}

$allCsv = @(Get-ChildItem $rowsDir -Filter *.csv -File | Sort-Object Name)

$entityFiles = @($allCsv |
    Where-Object { $_.Name -notlike '*.attributes.csv' } |
    Where-Object { Get-Reported $_ })

$attributeFiles = @($allCsv |
    Where-Object { $_.Name -like '*.attributes.csv' } |
    Where-Object { Get-Reported $_ })

$skipped = $allCsv.Count - $entityFiles.Count - $attributeFiles.Count
if ($skipped -gt 0) {
    Write-Host ("[WARN] {0} row file(s) have no .ok and were not merged." -f $skipped)
}

if ($entityFiles.Count -eq 0) {
    Write-Host '[WARN] No completed drawings to merge.'
    exit 0
}

# ---------------------------------------------------------------- merge one family
function Merge-Family([System.IO.FileInfo[]] $files, [string] $target, [string] $label) {
    $writer = New-Object System.IO.StreamWriter($target, $false, $utf8)
    $rows = 0
    $header = $null
    $mismatched = 0

    try {
        foreach ($file in $files) {
            $lines = [System.IO.File]::ReadAllLines($file.FullName)
            if ($lines.Count -eq 0) { continue }

            if ($null -eq $header) {
                $header = $lines[0]
                $writer.WriteLine($header)
            }
            elseif ($lines[0] -ne $header) {
                # Two files with different headers would merge into nonsense. Say so loudly.
                Write-Host ("[FAIL] {0} header differs in {1}" -f $label, $file.Name)
                $mismatched++
                continue
            }

            for ($i = 1; $i -lt $lines.Count; $i++) {
                if ($lines[$i].Length -eq 0) { continue }
                $writer.WriteLine($lines[$i])
                $rows++
            }
        }
    }
    finally {
        $writer.Dispose()
    }

    Write-Host ("{0,-9}: {1} file(s), {2} row(s) -> {3}" -f $label, $files.Count, $rows, $target)
    return $mismatched
}

$mismatched = Merge-Family $entityFiles $entityTarget 'Merged'

if ($attributeFiles.Count -eq 0) {
    # A drawing with no attributes still writes a header-only file, so this only happens
    # for output produced before Phase 5. Do not leave the old merged file looking current.
    Write-Host '[WARN] No attribute files to merge.'
}
else {
    $mismatched += Merge-Family $attributeFiles $attributeTarget 'Attrs'
}

if ($mismatched -gt 0) {
    Write-Host "[FAIL] $mismatched file(s) had a different header."
    exit 1
}

exit 0
