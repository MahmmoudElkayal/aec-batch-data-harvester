# test-batch-engine.ps1 - Phase 4 acceptance
#
# The engine harvests a chunk of drawings in one Core Console process, each opened as a
# side database. Four things have to be true for that to be worth having:
#
#   1. it produces exactly the same rows as the one-process-per-drawing path
#   2. it is fast enough to matter (<= 50 ms of engine time per drawing)
#   3. a drawing that throws is one .fail and the chunk carries on
#   4. a process that is killed - crash or timeout - loses nothing that already finished,
#      and never leaves a hung drawing looking successful
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-batch-engine.ps1
#
# Exit codes: 0 = every property held, 1 = at least one did not.
# Leaves the repository in a clean, freshly harvested state.

[CmdletBinding()]
param(
    [string] $Root
)

$ErrorActionPreference = 'Stop'

if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }

$bat       = Join-Path $Root 'RunPipeline.bat'
$inputDir  = Join-Path $Root 'Input_DWGs'
$outputDir = Join-Path $Root 'Output'
$rowsDir   = Join-Path $outputDir '_rows'
$statusDir = Join-Path $outputDir '_status'
$csv       = Join-Path $outputDir 'Extracted_BOM.csv'
$summary   = Join-Path $statusDir '_batch.json'

$script:failures = 0

function Check([bool] $condition, [string] $what) {
    if ($condition) {
        Write-Host "    ok    $what" -ForegroundColor Green
    } else {
        Write-Host "    FAIL  $what" -ForegroundColor Red
        $script:failures++
    }
}

function Invoke-Pipeline([string[]] $Flags = @()) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $argLine = ($Flags -join ' ')
        $output = & cmd /c "`"$bat`" /q $argLine" 2>&1 | Out-String
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
    return [pscustomobject]@{ Text = $output; Code = $code }
}

function Get-CsvHash {
    return (Get-FileHash -Algorithm SHA256 -Path $csv).Hash
}

function Get-RowCount([string] $path) {
    return ([System.IO.File]::ReadAllLines($path)).Count - 1
}

function Remove-Broken {
    $broken = Join-Path $inputDir 'ZZ Broken.dwg'
    if (Test-Path $broken) { Remove-Item $broken -Force }
}

try {
    Remove-Broken

    # ------------------------------------------------------------------ 1
    Write-Host ''
    Write-Host '[1] the batch path produces the same rows as the per-drawing path'

    $run = Invoke-Pipeline
    Check ($run.Code -eq 0) "exit code is 0 (was $($run.Code))"

    $mergedRows = Get-RowCount $csv
    Check ($mergedRows -eq 3278) "the merged CSV has 3,278 rows (found $mergedRows)"

    $check = & powershell -NoProfile -ExecutionPolicy Bypass -File `
        (Join-Path $Root 'tools\capture-golden.ps1') -SkipRun -Check 2>&1 | Out-String
    Check ($LASTEXITCODE -eq 0) 'the rows are byte-identical to the golden baseline'

    $stats = Get-Content $summary -Raw | ConvertFrom-Json
    Check ($stats.ok -eq $stats.drawings) "every drawing reported ok ($($stats.ok)/$($stats.drawings))"
    Check ([double]$stats.perDrawingMs -le 50.0) `
        ("engine time is {0} ms/drawing, within the 50 ms budget" -f $stats.perDrawingMs)

    $baselineHash = Get-CsvHash

    # ------------------------------------------------------------------ 2
    Write-Host ''
    Write-Host '[2] one drawing throws - it fails, the rest continue'

    $source = Get-ChildItem $inputDir -Filter *.dwg -File | Select-Object -First 1
    $broken = Join-Path $inputDir 'ZZ Broken.dwg'
    $bytes = [System.IO.File]::ReadAllBytes($source.FullName)
    [System.IO.File]::WriteAllBytes($broken, $bytes[0..4095])

    $run = Invoke-Pipeline
    Check ($run.Code -eq 2) "exit code is 2 (was $($run.Code))"

    $brokenFail = @(Get-ChildItem $statusDir -Filter 'ZZ Broken-*.fail' -File -ErrorAction SilentlyContinue)
    $brokenOk = @(Get-ChildItem $statusDir -Filter 'ZZ Broken-*.ok' -File -ErrorAction SilentlyContinue)
    $brokenRows = @(Get-ChildItem $rowsDir -Filter 'ZZ Broken-*.csv' -File -ErrorAction SilentlyContinue)

    Check ($brokenFail.Count -eq 1) "the unreadable drawing wrote one .fail (found $($brokenFail.Count))"
    Check ($brokenOk.Count -eq 0) 'it did not write an .ok'
    Check ($brokenRows.Count -eq 0) 'it published no row file'

    $goodOk = @(Get-ChildItem $statusDir -Filter *.ok -File -ErrorAction SilentlyContinue)
    Check ($goodOk.Count -eq 3) "all three good drawings still reported ok (found $($goodOk.Count))"
    Check ((Get-RowCount $csv) -eq 3278) 'the merge is unchanged - the failed drawing contributed nothing'

    Remove-Broken

    # ------------------------------------------------------------------ 3
    Write-Host ''
    Write-Host '[3] a resume does not redo finished drawings'

    $run = Invoke-Pipeline
    Check ($run.Code -eq 0) "the clean run exits 0 (was $($run.Code))"

    $rowFiles = @(Get-ChildItem $rowsDir -Filter *.csv -File | Sort-Object Name)
    Check ($rowFiles.Count -eq 3) "three row files exist (found $($rowFiles.Count))"

    $before = @{}
    foreach ($file in $rowFiles) { $before[$file.Name] = $file.LastWriteTimeUtc }

    # Simulate a process killed after finishing two of three: one drawing has no row file
    # and no verdict, the others are complete.
    $victim = $rowFiles[0]
    $victimKey = [System.IO.Path]::GetFileNameWithoutExtension($victim.Name)
    Remove-Item $victim.FullName -Force
    Remove-Item (Join-Path $statusDir ($victimKey + '.ok')) -Force
    Remove-Item (Join-Path $statusDir ($victimKey + '.rows')) -Force

    $run = Invoke-Pipeline @('/append')
    Check ($run.Code -eq 0) "the resume exits 0 (was $($run.Code))"

    $redone = 0
    foreach ($file in @(Get-ChildItem $rowsDir -Filter *.csv -File)) {
        if ($file.Name -eq $victim.Name) { continue }
        if ($before.ContainsKey($file.Name) -and $file.LastWriteTimeUtc -eq $before[$file.Name]) {
            continue
        }
        $redone++
    }

    Check ($redone -eq 0) "no finished drawing was harvested again ($redone re-run)"
    Check (Test-Path $victim.FullName) 'the missing drawing was harvested'
    Check ((Get-RowCount $csv) -eq 3278) 'the merge is back to 3,278 rows'
    Check ((Get-CsvHash) -eq $baselineHash) 'and to the same bytes'

    # ------------------------------------------------------------------ 4
    Write-Host ''
    Write-Host '[4] a drawing that never returns is killed and marked .fail'

    # A 1-second budget cannot possibly open a drawing, so every chunk is killed before it
    # can report. This exercises the mechanism - kill, blame one drawing, never .ok - and
    # a stuck drawing reaches it the same way.
    $env:AEC_HARVEST_TIMEOUT = '1'
    $env:AEC_HARVEST_CHUNK = '1'
    try {
        $run = Invoke-Pipeline
        Check ($run.Code -eq 2) "exit code is 2 (was $($run.Code))"
    }
    finally {
        Remove-Item Env:AEC_HARVEST_TIMEOUT -ErrorAction SilentlyContinue
        Remove-Item Env:AEC_HARVEST_CHUNK -ErrorAction SilentlyContinue
    }

    $timeoutFails = @(Get-ChildItem $statusDir -Filter *.fail -File -ErrorAction SilentlyContinue)
    $timeoutOks = @(Get-ChildItem $statusDir -Filter *.ok -File -ErrorAction SilentlyContinue)
    $timeoutRows = @(Get-ChildItem $rowsDir -Filter *.csv -File -ErrorAction SilentlyContinue)

    Check ($timeoutFails.Count -eq 3) "each killed chunk blamed exactly one drawing (found $($timeoutFails.Count))"
    Check ($timeoutOks.Count -eq 0) 'no killed drawing was reported as ok'
    Check ($timeoutRows.Count -eq 0) 'no partial row file survived the kill'

    if ($timeoutFails.Count -gt 0) {
        $payload = Get-Content $timeoutFails[0].FullName -Raw | ConvertFrom-Json
        Check ($payload.ok -eq $false) 'the payload says ok:false'
        Check ($payload.message -match 'timed out') "the message says it timed out: $($payload.message)"

        $detail = [System.IO.Path]::ChangeExtension($timeoutFails[0].FullName, '.error.txt')
        Check (Test-Path $detail) 'the reason is kept in .error.txt'
    }

    # ------------------------------------------------------------------ 5
    Write-Host ''
    Write-Host '[5] recovery after the kills'

    $run = Invoke-Pipeline
    Check ($run.Code -eq 0) "exit code is 0 (was $($run.Code))"
    Check ((Get-RowCount $csv) -eq 3278) 'the merge is back to 3,278 rows'
    Check ((Get-CsvHash) -eq $baselineHash) 'the bytes are back to the baseline'

    $stats = Get-Content $summary -Raw | ConvertFrom-Json
    Check ([double]$stats.perDrawingMs -le 50.0) `
        ("the recovered run is still {0} ms/drawing" -f $stats.perDrawingMs)
}
finally {
    Remove-Broken
    Remove-Item Env:AEC_HARVEST_TIMEOUT -ErrorAction SilentlyContinue
    Remove-Item Env:AEC_HARVEST_CHUNK -ErrorAction SilentlyContinue
}

Write-Host ''
if ($script:failures -gt 0) {
    Write-Host "[FAIL] $script:failures check(s) failed." -ForegroundColor Red
    exit 1
}

Write-Host '[OK] Every Phase 4 property held.' -ForegroundColor Green
exit 0
