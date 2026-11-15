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
        $planKeys = @(Get-ChildItem $rowsDir -Filter 'Plan-*.csv' -File |
            Where-Object { $_.Name -notlike '*.attributes.csv' })
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
    Write-Host '[4] an unusable manifest key is refused before anything runs'

    # The key is a file name. A separator or colon in it would put status files outside
    # _status, where the supervisor will never look - so the whole manifest is refused,
    # loudly, instead of half-running under names nobody can find.
    $scratch = Join-Path $env:TEMP ('aec-badkey-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $scratch -Force | Out-Null

    try {
        $scratchOutput = Join-Path $scratch 'Output'
        New-Item -ItemType Directory -Path $scratchOutput -Force | Out-Null

        $badManifest = Join-Path $scratchOutput 'manifest.txt'
        [System.IO.File]::WriteAllText(
            $badManifest,
            "FolderA\Plan-3fa11b4c|" + $source.FullName + "`r`n")

        $output = & powershell -NoProfile -ExecutionPolicy Bypass -File `
            (Join-Path $Root 'tools\run-harvest-batch.ps1') `
            -Root $scratch -Manifest $badManifest `
            -Script (Join-Path $Root 'Scripts\HarvestBatch.scr') `
            -Console $console 2>&1 | Out-String
        $code = $LASTEXITCODE

        Check ($code -eq 1) "the supervisor refuses it (exit was $code)"
        Check ([bool] ($output -match 'not usable as a file name')) `
            'the reason names the rule, not just a failure'

        $statusFiles = @()
        $scratchStatus = Join-Path $scratchOutput '_status'
        if (Test-Path $scratchStatus) {
            $statusFiles = @(Get-ChildItem $scratchStatus -File -Recurse -ErrorAction SilentlyContinue)
        }
        Check ($statusFiles.Count -eq 0) 'no status file was written - nothing was reported as success'
    }
    finally {
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
    $started = Get-Date

    # Wait until at least one drawing has genuinely reported. Killing an idle process
    # would prove only that taskkill works; the property under test is that a drawing
    # which already finished survives, and nothing partial is published for the rest.
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        $reported = @(Get-ChildItem $statusDir -Filter *.ok -File -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTime -gt $started })
        if ($reported.Count -gt 0) { break }
        Start-Sleep -Milliseconds 100
    }
    $reportedBeforeKill = @(Get-ChildItem $statusDir -Filter *.ok -File -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -gt $started }).Count
    Write-Host ("    (drawings reported before the kill: {0})" -f $reportedBeforeKill)

    # taskkill writes to stderr, and a target that already exited is not an error worth
    # stopping for: the assertion below does not depend on the kill having succeeded.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & taskkill /T /F /PID $process.Id 2>&1 | Out-Null
        Start-Sleep -Milliseconds 500
    }
    finally {
        $ErrorActionPreference = $previous
    }

    $published = @(Get-ChildItem $rowsDir -Filter *.csv -File -ErrorAction SilentlyContinue)
    $tempFiles = @(Get-ChildItem $rowsDir -Filter *.tmp -File -ErrorAction SilentlyContinue)

    # A success verdict must have data behind it. The reverse window exists (the row file
    # is published just before the .ok), which is why the merge only takes drawings that
    # reported .ok - asserted below, not assumed.
    $okWithoutRows = 0
    foreach ($ok in @(Get-ChildItem $statusDir -Filter *.ok -File -ErrorAction SilentlyContinue)) {
        $key = [System.IO.Path]::GetFileNameWithoutExtension($ok.Name)
        if (-not (Test-Path (Join-Path $rowsDir ($key + '.csv')))) { $okWithoutRows++ }
    }
    Check ($okWithoutRows -eq 0) "no .ok lacks its row file (found $okWithoutRows)"

    # Merge what survived, and prove it contains exactly the drawings that reported.
    $expectedRows = 0
    $expectedAttrs = 0
    foreach ($ok in @(Get-ChildItem $statusDir -Filter *.ok -File -ErrorAction SilentlyContinue)) {
        $payload = Get-Content $ok.FullName -Raw | ConvertFrom-Json
        $expectedRows += [int]$payload.rows
        $expectedAttrs += [int]$payload.attrs
    }

    & powershell -NoProfile -ExecutionPolicy Bypass -File `
        (Join-Path $Root 'tools\merge-rows.ps1') -Root $Root | Out-Null

    $mergedAfterKill = 0
    if (Test-Path $csv) {
        $mergedAfterKill = @([System.IO.File]::ReadAllLines($csv)).Count - 1
    }
    Check ($mergedAfterKill -eq $expectedRows) `
        "the merge published only reported drawings ($mergedAfterKill rows, expected $expectedRows)"

    # The attribute file is gated the same way. A killed drawing that published its
    # entity rows but not its verdict must not contribute attributes either.
    $attributeCsv = Join-Path $Root 'Output\Extracted_Attributes.csv'
    $mergedAttrs = 0
    if (Test-Path $attributeCsv) {
        $mergedAttrs = @([System.IO.File]::ReadAllLines($attributeCsv)).Count - 1
    }
    Check ($mergedAttrs -eq $expectedAttrs) `
        "the attribute merge published only reported drawings ($mergedAttrs rows, expected $expectedAttrs)"

    # The real kill window - rows published, verdict not yet written - is a few
    # milliseconds wide, so a timing test cannot be relied on to land in it. Prove the
    # gate directly: hold back one drawing's verdict and merge again. Its files still
    # exist; only the .ok is missing.
    $candidates = @(Get-ChildItem $statusDir -Filter *.ok -File | ForEach-Object {
        [pscustomobject]@{
            File = $_
            Payload = (Get-Content $_.FullName -Raw | ConvertFrom-Json)
        }
    })
    $withAttributes = @($candidates | Where-Object { [int]$_.Payload.attrs -gt 0 })
    $victim = if ($withAttributes.Count -gt 0) { $withAttributes[0] } else { $candidates[0] }

    if ($victim) {
        $held = $victim.File.FullName + '.held'
        Move-Item $victim.File.FullName $held -Force

        & powershell -NoProfile -ExecutionPolicy Bypass -File `
            (Join-Path $Root 'tools\merge-rows.ps1') -Root $Root | Out-Null

        $gatedRows = @([System.IO.File]::ReadAllLines($csv)).Count - 1
        $gatedAttrs = @([System.IO.File]::ReadAllLines($attributeCsv)).Count - 1
        Check ($gatedRows -eq ($expectedRows - [int]$victim.Payload.rows)) `
            ("a row file without .ok is not merged ($gatedRows rows, expected {0})" -f
             ($expectedRows - [int]$victim.Payload.rows))
        Check ($gatedAttrs -eq ($expectedAttrs - [int]$victim.Payload.attrs)) `
            ("its attributes are not merged either ($gatedAttrs rows, expected {0})" -f
             ($expectedAttrs - [int]$victim.Payload.attrs))

        Move-Item $held $victim.File.FullName -Force
        & powershell -NoProfile -ExecutionPolicy Bypass -File `
            (Join-Path $Root 'tools\merge-rows.ps1') -Root $Root | Out-Null
        Check ((@([System.IO.File]::ReadAllLines($csv)).Count - 1) -eq $expectedRows) `
            'restoring the verdict restores the rows'
    }

    # A row file that lost the race to its .ok must not reach the merged CSV. Every merged
    # row is accounted for by a .rows file that has an .ok, so any orphan above is excluded
    # by construction - reported, not asserted twice.
    $dataWithoutOk = @(Get-ChildItem $rowsDir -Filter *.csv -File |
        Where-Object { $_.Name -notlike '*.attributes.csv' } |
        Where-Object {
            -not (Test-Path (Join-Path $statusDir (
                [System.IO.Path]::GetFileNameWithoutExtension($_.Name) + '.ok')))
        })
    Write-Host ("    (row files published but not yet reported: {0})" -f $dataWithoutOk.Count)

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
