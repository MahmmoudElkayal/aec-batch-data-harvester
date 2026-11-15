# test-failure-paths.ps1 - prove the pipeline fails loudly when it should
#
# Phase 1 of docs/refactor-plan.md. The defects being guarded against all shared one shape:
# the pipeline reported success when it had not succeeded.
#
# Three modes, each expected to be detected by RunPipeline.bat rather than by a human
# reading a log:
#   1. a DWG that cannot be opened  -> no status file, FAIL, exit 2
#   2. an output CSV that cannot be written -> .fail + stack trace, exit 2
#   3. a clean run afterwards -> .ok for every drawing, exit 0
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-failure-paths.ps1
#
# Exit codes: 0 = every mode behaved, 1 = at least one did not.
# The script mutates Input_DWGs (a temporary truncated copy) and Output, then restores them.

[CmdletBinding()]
param(
    [string] $Root
)

$ErrorActionPreference = 'Stop'

if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }

$inputDir  = Join-Path $Root 'Input_DWGs'
$outputDir = Join-Path $Root 'Output'
$statusDir = Join-Path $outputDir '_status'
$csvPath   = Join-Path $outputDir 'Extracted_BOM.csv'
$bat       = Join-Path $Root 'RunPipeline.bat'

$script:failures = 0

function Check([bool] $condition, [string] $what) {
    if ($condition) {
        Write-Host "    ok    $what" -ForegroundColor Green
    } else {
        Write-Host "    FAIL  $what" -ForegroundColor Red
        $script:failures++
    }
}

function Invoke-Pipeline {
    # The batch writes real errors to stderr, and with ErrorActionPreference = Stop a
    # native command's stderr becomes a terminating error. That is the very thing under
    # test here, so capture it instead of trusting it not to throw.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'

    try {
        $output = & cmd /c "`"$bat`" /q" 2>&1 | Out-String
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }

    return [pscustomobject]@{ Text = $output; Code = $code }
}

function Show-StatusFiles {
    if (-not (Test-Path $statusDir)) {
        Write-Host '    (no _status folder)'
        return
    }
    Get-ChildItem $statusDir | Sort-Object Name | ForEach-Object { Write-Host "    $($_.Name)" }
}

if (-not (Test-Path $bat)) { Write-Host "[FAIL] RunPipeline.bat not found at $bat"; exit 1 }
if (-not (Test-Path $inputDir)) { Write-Host "[FAIL] no Input_DWGs at $inputDir"; exit 1 }

# A real drawing is needed as the source of a deliberately unreadable one.
$source = Get-ChildItem $inputDir -Filter *.dwg -File | Select-Object -First 1
if (-not $source) { Write-Host "[FAIL] Input_DWGs contains no DWG to copy"; exit 1 }

# ---------------------------------------------------------------------------
# 1. A DWG that cannot be opened. Core Console never reaches the command, so
#    nothing writes a status file. Silence must not look like success.
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host '[1] truncated DWG - cannot be opened'

$broken = Join-Path $inputDir 'ZZ Truncated.dwg'
$bytes = [System.IO.File]::ReadAllBytes($source.FullName)
[System.IO.File]::WriteAllBytes($broken, $bytes[0..4095])
Write-Host ("    wrote {0} bytes from '{1}'" -f 4096, $source.Name)

try {
    $run = Invoke-Pipeline
    $tail = $run.Text -split "`r?`n"

    Check ($run.Code -eq 2) "exit code is 2 (was $($run.Code))"
    Check (($tail | Where-Object { $_ -match 'no status file' }).Count -gt 0) `
        'the batch says the drawing produced no status file'
    Check (-not (Test-Path (Join-Path $statusDir 'ZZ Truncated.ok'))) `
        'no .ok file was written for the unreadable drawing'
    Check (-not (Test-Path (Join-Path $statusDir 'ZZ Truncated.fail'))) `
        'no .fail file either - nothing ran, which is the point'
}
finally {
    Remove-Item $broken -Force -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------------------
# 2. Output that cannot be written. Extraction succeeds, writing does not, so
#    every drawing must report failure with the exception text preserved.
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host '[2] locked output CSV - extraction cannot write'

$lock = $null
try {
    $lock = [System.IO.File]::Open($csvPath, 'OpenOrCreate', 'ReadWrite', 'None')

    $run = Invoke-Pipeline
    $tail = $run.Text -split "`r?`n"

    Check ($run.Code -eq 2) "exit code is 2 (was $($run.Code))"
    Check (($tail | Where-Object { $_ -match 'reported failure' }).Count -gt 0) `
        'the batch reports failures rather than silence'

    $fails = @(Get-ChildItem $statusDir -Filter *.fail -ErrorAction SilentlyContinue)
    $oks = @(Get-ChildItem $statusDir -Filter *.ok -ErrorAction SilentlyContinue)

    Check ($fails.Count -gt 0) 'a .fail file exists for the failed drawings'
    Check ($oks.Count -eq 0) 'no stale .ok file survives next to a .fail'

    if ($fails.Count -gt 0) {
        $errorFile = [System.IO.Path]::ChangeExtension($fails[0].FullName, '.error.txt')
        $hasDetail = (Test-Path $errorFile) -and ((Get-Item $errorFile).Length -gt 0)

        Check $hasDetail 'the full exception text is kept in .error.txt'
        if ($hasDetail) {
            $firstLine = (Get-Content $errorFile -TotalCount 1)
            Check ($firstLine -match 'Exception|Error') "the detail looks like an exception: $firstLine"
        }

        $json = Get-Content $fails[0].FullName -Raw
        Check ($json -match '"ok":false') 'the .fail payload is valid one-line JSON with ok:false'
        Check ($json -notmatch '[\r\n]') 'the .fail payload is a single line'
    }
}
finally {
    if ($lock) { $lock.Dispose() }
}

# ---------------------------------------------------------------------------
# 3. A clean run must recover completely: no leftover verdicts from mode 2.
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host '[3] clean run - recovery'

$run = Invoke-Pipeline
$tail = $run.Text -split "`r?`n"

Check ($run.Code -eq 0) "exit code is 0 (was $($run.Code))"

$drawings = @(Get-ChildItem $inputDir -Filter *.dwg -File)
$oks = @(Get-ChildItem $statusDir -Filter *.ok -ErrorAction SilentlyContinue)
$fails = @(Get-ChildItem $statusDir -Filter *.fail -ErrorAction SilentlyContinue)

Check ($oks.Count -eq $drawings.Count) "every drawing has a .ok ($($oks.Count) of $($drawings.Count))"
Check ($fails.Count -eq 0) 'no .fail file remains from the previous run'

$staleErrors = @(Get-ChildItem $statusDir -Filter *.error.txt -ErrorAction SilentlyContinue)
Check ($staleErrors.Count -eq 0) 'no stale .error.txt remains'

if ($oks.Count -gt 0) {
    $rowsFile = [System.IO.Path]::ChangeExtension($oks[0].FullName, '.rows')
    $rows = if (Test-Path $rowsFile) { (Get-Content $rowsFile -Raw).Trim() } else { '' }
    Check ($rows -match '^\d+$') "the row count file is digits only ('$rows')"
}

Write-Host ''
Show-StatusFiles

Write-Host ''
if ($script:failures -gt 0) {
    Write-Host "[FAIL] $script:failures check(s) failed." -ForegroundColor Red
    exit 1
}

Write-Host '[OK] Every failure mode was detected, and the clean run recovered.' -ForegroundColor Green
exit 0
