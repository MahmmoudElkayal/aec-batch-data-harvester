# merge-rows.ps1 - one CSV from the per-drawing files
#
# Phase 3 of docs/refactor-plan.md. Every drawing writes Output\_rows\<key>.csv
# (temp-then-rename), and this pass concatenates them in key order into
# Output\Extracted_BOM.csv.
#
# Only .csv files are read. A .tmp means the drawing was still being written when something
# died, so it is not data yet and the merge must not publish it.
#
# Deterministic: sorted keys, header from the first file only, CRLF, UTF-8 without BOM.
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
$target  = Join-Path $Root 'Output\Extracted_BOM.csv'

if (-not (Test-Path $rowsDir)) {
    Write-Host "[WARN] No _rows folder at $rowsDir - nothing to merge."
    exit 0
}

# Only drawings that reported success are data. A row file without its .ok is the
# signature of a process killed between publishing rows and publishing the verdict;
# merging it would publish a drawing the batch considers failed.
$files = @(Get-ChildItem $rowsDir -Filter *.csv -File | Sort-Object Name |
    Where-Object {
        Test-Path (Join-Path $statusDir (
            [System.IO.Path]::GetFileNameWithoutExtension($_.Name) + '.ok'))
    })

$skipped = @(Get-ChildItem $rowsDir -Filter *.csv -File | Sort-Object Name).Count - $files.Count
if ($skipped -gt 0) {
    Write-Host ("[WARN] {0} row file(s) have no .ok and were not merged." -f $skipped)
}

if ($files.Count -eq 0) {
    Write-Host '[WARN] No completed drawings to merge.'
    exit 0
}

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$writer = New-Object System.IO.StreamWriter($target, $false, $utf8NoBom)

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
            Write-Host ("[FAIL] Header differs in {0}" -f $file.Name)
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

if ($mismatched -gt 0) {
    Write-Host "[FAIL] $mismatched file(s) had a different header."
    exit 1
}

Write-Host ("Merged  : {0} file(s), {1} row(s) -> {2}" -f $files.Count, $rows, $target)
exit 0
