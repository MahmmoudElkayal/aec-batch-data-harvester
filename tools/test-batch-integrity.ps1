# test-batch-integrity.ps1 - Phase 3 acceptance
#
# Per-drawing files, temp-then-rename, supervisor-owned keys, and a merge. The properties
# under test are the ones that make a batch resumable and re-runnable:
#
#   1. the same batch run twice produces byte-identical output
#   2. two drawings with the same file name in different folders stay separate
#   3. a drawing name that is a reserved device name (CON) is safe
#   4. a missing AEC_HARVEST_KEY fails the drawing instead of inventing a name
#   5. adding a drawing does not change the keys of drawings already processed
#   6. killing the run mid-flight never publishes a partial drawing
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-batch-integrity.ps1
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
$manifest  = Join-Path $outputDir 'manifest.txt'
$scriptFile = Join-Path $Root 'Scripts\RunExtraction.scr'
$console   = 'C:\Program Files\Autodesk\AutoCAD 2026\accoreconsole.exe'

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
    # Native stderr is a terminating error under Stop, and a failing batch writes to it.
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
    if (-not (Test-Path $csv)) { return '' }
    return (Get-FileHash -Algorithm SHA256 -Path $csv).Hash
}

function Get-RowFileCount([string] $file) {
    return ([System.IO.File]::ReadAllLines($file)).Count - 1
}

function Get-KeyCount([string] $filter) {
    if (-not (Test-Path $statusDir)) { return 0 }
    return @(Get-ChildItem $statusDir -Filter $filter -File).Count
}

# Put the tree back the way a user would expect to find it.
function Restore-CleanState {
    foreach ($path in @(
        (Join-Path $inputDir 'TmpA'),
        (Join-Path $inputDir 'TmpB'))) {
        if (Test-Path $path) { Remove-Item $path -Recurse -Force }
    }
    $added = Join-Path $inputDir 'ZZ Added.dwg'
    if (Test-Path $added) { Remove-Item $added -Force }
}

try {
    Restore-CleanState

    # ------------------------------------------------------------------ 1
    Write-Host ''
    Write-Host '[1] the same batch twice produces identical bytes'

    $run = Invoke-Pipeline
    Check ($run.Code -eq 0) "first run exits 0 (was $($run.Code))"
    $hashA = Get-CsvHash
    $rowsA = (Get-Item $csv).Length
    $baseRows = ([System.IO.File]::ReadAllLines($csv)).Count - 1

    $run = Invoke-Pipeline
    Check ($run.Code -eq 0) "second run exits 0 (was $($run.Code))"
    $hashB = Get-CsvHash

    Check ($hashA -ne '') 'the merged CSV exists'
    Check ($hashA -eq $hashB) "the merged CSV is byte-identical ($rowsA bytes)"

    # ------------------------------------------------------------------ 2
    Write-Host ''
    Write-Host '[2] two folders, one file name'

    $source = Get-ChildItem $inputDir -Filter *.dwg -File | Select-Object -First 1
    $sourceRows = @([System.IO.File]::ReadAllLines($csv) |
        Where-Object { $_.StartsWith($source.Name + ',') }).Count
    Check ($sourceRows -gt 0) "the source drawing has rows to duplicate ($sourceRows)"
    foreach ($folder in @('TmpA', 'TmpB')) {
        $target = Join-Path $inputDir $folder
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        Copy-Item $source.FullName (Join-Path $target 'Plan.dwg')
    }

    $run = Invoke-Pipeline @('/r')
    Check ($run.Code -eq 0) "the recursive run exits 0 (was $($run.Code))"

    $planKeys = @()
    if (Test-Path $rowsDir) {
        $planKeys = @(Get-ChildItem $rowsDir -Filter 'Plan-*.csv' -File)
    }
    Check ($planKeys.Count -eq 2) "both Plan.dwg drawings have their own row file (found $($planKeys.Count))"
    Check ((@($planKeys | Select-Object -ExpandProperty Name -Unique)).Count -eq 2) 'their keys differ'

    $okForPlan = @(Get-ChildItem $statusDir -Filter 'Plan-*.ok' -File -ErrorAction SilentlyContinue)
    Check ($okForPlan.Count -eq 2) "both wrote a status file (found $($okForPlan.Count))"

    $mergedRows = ([System.IO.File]::ReadAllLines($csv)).Count - 1
    $expectedRows = $baseRows + (2 * $sourceRows)
    Check ($mergedRows -eq $expectedRows) "the merge contains both drawings' rows ($mergedRows, expected $expectedRows)"

    Remove-Item (Join-Path $inputDir 'TmpA') -Recurse -Force
    Remove-Item (Join-Path $inputDir 'TmpB') -Recurse -Force

    # ------------------------------------------------------------------ 3
    Write-Host ''
    Write-Host '[3] reserved device names'

    $keyRun = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Root 'tools\build-manifest.ps1') -SelfTest 2>&1 | Out-String
    Check ($LASTEXITCODE -eq 0) 'the key rules hold, including CON/NUL/COM1'

    # ------------------------------------------------------------------ 4
    Write-Host ''
    Write-Host '[4] a missing AEC_HARVEST_KEY fails the drawing'

    $scratch = Join-Path $env:TEMP ('aec-nokey-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $scratch -Force | Out-Null

    $previousOutput = $env:AEC_HARVEST_OUTPUT
    $previousKey = $env:AEC_HARVEST_KEY
    try {
        $env:AEC_HARVEST_OUTPUT = $scratch
        Remove-Item Env:AEC_HARVEST_KEY -ErrorAction SilentlyContinue

        $log = Join-Path $scratch 'run.log'
        $err = Join-Path $scratch 'run.err'
        $emptyInput = Join-Path $scratch 'empty.stdin'
        Set-Content -Path $emptyInput -Value '' -Encoding ASCII

        # The path must be quoted: a drawing name with a space, unquoted, makes Core
        # Console report "cannot find the specified drawing file" and exit before the
        # command ever runs - which would make "no status file" pass for the wrong reason.
        $arguments = '/i "' + $source.FullName + '" /s "' + $scriptFile + '" /l en-US /readonly'

        $process = Start-Process -FilePath $console `
            -ArgumentList $arguments `
            -RedirectStandardInput $emptyInput -RedirectStandardOutput $log -RedirectStandardError $err `
            -NoNewWindow -Wait -PassThru

        # Proof the command actually ran: an unquoted /i makes Core Console exit before
        # NETLOAD, which would make "no status file" pass for the wrong reason. The
        # diagnostic below is written by the plugin, so it can only exist if the command
        # ran; the stack frame in it says which command.
        $statusDirScratch = Join-Path $scratch '_status'
        $okFiles = @()
        $failFiles = @()
        if (Test-Path $statusDirScratch) {
            $okFiles = @(Get-ChildItem $statusDirScratch -Filter *.ok -File -ErrorAction SilentlyContinue)
            $failFiles = @(Get-ChildItem $statusDirScratch -Filter *.fail -File -ErrorAction SilentlyContinue)
        }

        Check ($okFiles.Count -eq 0) 'no .ok was written without a key'
        Check ($failFiles.Count -eq 0) 'no status file was invented either - absence is how this fails'

        # Core Console discards managed stdout and stderr (measured: 0 bytes on both for
        # exactly this run), so the reason must survive in a file.
        $diagnostic = Join-Path $statusDirScratch '_harvest.error.txt'
        Check (Test-Path $diagnostic) 'the reason is kept in _status\_harvest.error.txt'

        if (Test-Path $diagnostic) {
            $text = [System.IO.File]::ReadAllText($diagnostic)
            Check ([bool] ($text -match 'HarvestRunner')) 'the command itself ran (the reason carries its managed stack)'
            Check ([bool] ($text -match 'AEC_HARVEST_KEY')) "the reason names the variable: $($text.Split("`n")[0])"
        }
    }
    finally {
        $env:AEC_HARVEST_OUTPUT = $previousOutput
        $env:AEC_HARVEST_KEY = $previousKey
        Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue
    }

    # ------------------------------------------------------------------ 5
    Write-Host ''
    Write-Host '[5] adding a drawing does not renumber existing keys'

    # Snapshot from a freshly built manifest: the one left by step 2 still lists the
    # temporary folders that were just deleted, and comparing against it would test
    # nothing but the test's own bookkeeping.
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Root 'tools\build-manifest.ps1') -Root $Root -Destination $manifest | Out-Null
    Check ($LASTEXITCODE -eq 0) 'the manifest rebuilt before the snapshot'

    $before = @{}
    foreach ($line in [System.IO.File]::ReadAllLines($manifest)) {
        $parts = $line -split '\|', 2
        $before[$parts[1]] = $parts[0]
    }

    Copy-Item $source.FullName (Join-Path $inputDir 'ZZ Added.dwg')
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Root 'tools\build-manifest.ps1') -Root $Root -Destination $manifest | Out-Null
    Check ($LASTEXITCODE -eq 0) 'the manifest rebuilt'

    $after = @{}
    foreach ($line in [System.IO.File]::ReadAllLines($manifest)) {
        $parts = $line -split '\|', 2
        $after[$parts[1]] = $parts[0]
    }

    $changed = 0
    foreach ($path in $before.Keys) {
        if ($after[$path] -ne $before[$path]) { $changed++ }
    }
    Check ($changed -eq 0) "every existing key is unchanged ($changed changed)"
    Check ($after.Count -eq $before.Count + 1) 'exactly one key was added'

    Remove-Item (Join-Path $inputDir 'ZZ Added.dwg') -Force

    # ------------------------------------------------------------------ 6
    Write-Host ''
    Write-Host '[6] killed mid-run: nothing partial is published'

    $process = Start-Process -FilePath 'cmd.exe' `
        -ArgumentList @('/c', "`"$bat`" /q") -PassThru -WindowStyle Hidden
    Start-Sleep -Milliseconds 3200
    & taskkill /T /F /PID $process.Id 2>&1 | Out-Null
    Start-Sleep -Milliseconds 500

    $published = @(Get-ChildItem $rowsDir -Filter *.csv -File -ErrorAction SilentlyContinue)
    $tempFiles = @(Get-ChildItem $rowsDir -Filter *.tmp -File -ErrorAction SilentlyContinue)

    $orphans = 0
    $mismatched = 0
    foreach ($file in $published) {
        $key = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)
        $status = Join-Path $statusDir ($key + '.ok')

        if (-not (Test-Path $status)) { $orphans++; continue }

        $reported = (Get-Content $status -Raw | ConvertFrom-Json).rows
        if ((Get-RowFileCount $file.FullName) -ne [int]$reported) { $mismatched++ }
    }

    Check ($orphans -eq 0) "no published row file lacks its .ok (orphans: $orphans)"
    Check ($mismatched -eq 0) "every published row file matches its reported row count (mismatched: $mismatched)"
    Write-Host ("    (temporary files left by the kill: {0})" -f $tempFiles.Count)

    # ------------------------------------------------------------------ 7
    Write-Host ''
    Write-Host '[7] recovery and row parity with the golden baseline'

    $run = Invoke-Pipeline
    Check ($run.Code -eq 0) "the recovery run exits 0 (was $($run.Code))"
    Check ((Get-CsvHash) -eq $hashA) 'the merged CSV is back to the expected bytes'

    $check = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Root 'tools\capture-golden.ps1') -SkipRun -Check 2>&1 | Out-String
    Check ($LASTEXITCODE -eq 0) 'per-drawing rows are byte-identical to the golden baseline'
}
finally {
    Restore-CleanState
}

Write-Host ''
if ($script:failures -gt 0) {
    Write-Host "[FAIL] $script:failures check(s) failed." -ForegroundColor Red
    exit 1
}

Write-Host '[OK] Every Phase 3 property held.' -ForegroundColor Green
exit 0
