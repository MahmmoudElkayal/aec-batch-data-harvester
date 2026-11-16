# test-batch-engine.ps1 - Phase 4 acceptance
#
# The engine harvests a chunk of drawings in one Core Console process, each opened as a
# side database. Four things have to be true for that to be worth having:
#
#   1. it produces exactly the same rows as the one-process-per-drawing path
#   2. it is fast enough to matter (<= 100 ms of engine time per drawing)
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
    Check ($mergedRows -eq 6748) "the merged CSV has 6,748 rows (found $mergedRows)"

    $check = & powershell -NoProfile -ExecutionPolicy Bypass -File `
        (Join-Path $Root 'tools\capture-golden.ps1') -SkipRun -Check 2>&1 | Out-String
    Check ($LASTEXITCODE -eq 0) 'the rows are byte-identical to the golden baseline'

    $stats = Get-Content $summary -Raw | ConvertFrom-Json
    Check ($stats.ok -eq $stats.drawings) "every drawing reported ok ($($stats.ok)/$($stats.drawings))"
    # Phase 5 added 3,470 rows to the same corpus, so the Phase 4 budget is restated:
    # 100 ms/drawing still leaves the engine ~20x faster than a 1.6 s process start.
    Check ([double]$stats.perDrawingMs -le 100.0) `
        ("engine time is {0} ms/drawing, within the 100 ms budget" -f $stats.perDrawingMs)

    $attributeRows = (Get-RowCount (Join-Path $outputDir 'Extracted_Attributes.csv'))
    Check ($attributeRows -eq 219) "the attribute file has 219 rows (found $attributeRows)"

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
    $brokenRows = @(Get-ChildItem $rowsDir -Filter 'ZZ Broken-*' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like '*.csv' })

    Check ($brokenFail.Count -eq 1) "the unreadable drawing wrote one .fail (found $($brokenFail.Count))"
    Check ($brokenOk.Count -eq 0) 'it did not write an .ok'
    Check ($brokenRows.Count -eq 0) 'it published no entity or attribute file'

    $goodOk = @(Get-ChildItem $statusDir -Filter *.ok -File -ErrorAction SilentlyContinue)
    Check ($goodOk.Count -eq 3) "all three good drawings still reported ok (found $($goodOk.Count))"
    Check ((Get-RowCount $csv) -eq 6748) 'the merge is unchanged - the failed drawing contributed nothing'

    Remove-Broken

    # ------------------------------------------------------------------ 3
    Write-Host ''
    Write-Host '[3] a resume does not redo finished drawings'

    $run = Invoke-Pipeline
    Check ($run.Code -eq 0) "the clean run exits 0 (was $($run.Code))"

    $rowFiles = @(Get-ChildItem $rowsDir -Filter *.csv -File |
        Where-Object { $_.Name -notlike '*.attributes.csv' } |
        Sort-Object Name)
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
    foreach ($file in @(Get-ChildItem $rowsDir -Filter *.csv -File |
        Where-Object { $_.Name -notlike '*.attributes.csv' })) {
        if ($file.Name -eq $victim.Name) { continue }
        if ($before.ContainsKey($file.Name) -and $file.LastWriteTimeUtc -eq $before[$file.Name]) {
            continue
        }
        $redone++
    }

    Check ($redone -eq 0) "no finished drawing was harvested again ($redone re-run)"
    Check (Test-Path $victim.FullName) 'the missing drawing was harvested'
    Check ((Get-RowCount $csv) -eq 6748) 'the merge is back to 6,748 rows'
    Check ((Get-CsvHash) -eq $baselineHash) 'and to the same bytes'

    # ------------------------------------------------------------------ 4
    Write-Host ''
    Write-Host '[4] a drawing that never returns is killed and marked .fail'

    # A zero-second budget kills every chunk at the first poll - before Core Console can
    # open a drawing, which takes over a second even with a warm cache. This exercises the
    # mechanism (kill, blame one drawing, never .ok) deterministically; a stuck drawing
    # reaches it the same way. Two chunks at once also proves each kill reads its own
    # blame marker rather than a neighbour's.
    $env:AEC_HARVEST_TIMEOUT = '0'
    $env:AEC_HARVEST_CHUNK = '1'
    $env:AEC_HARVEST_PARALLEL = '2'
    try {
        $run = Invoke-Pipeline
        Check ($run.Code -eq 2) "exit code is 2 (was $($run.Code))"
    }
    finally {
        Remove-Item Env:AEC_HARVEST_TIMEOUT -ErrorAction SilentlyContinue
        Remove-Item Env:AEC_HARVEST_CHUNK -ErrorAction SilentlyContinue
        Remove-Item Env:AEC_HARVEST_PARALLEL -ErrorAction SilentlyContinue
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
    Check ((Get-RowCount $csv) -eq 6748) 'the merge is back to 6,748 rows'
    Check ((Get-CsvHash) -eq $baselineHash) 'the bytes are back to the baseline'

    $stats = Get-Content $summary -Raw | ConvertFrom-Json
    Check ([double]$stats.perDrawingMs -le 100.0) `
        ("the recovered run is still {0} ms/drawing" -f $stats.perDrawingMs)

    # ------------------------------------------------------------------ 6
    Write-Host ''
    Write-Host '[6] parallel chunks produce one answer'

    # Three drawings, three chunks, three processes at once: the result must be the same
    # bytes, each chunk must own its summary, and the combined summary must account for
    # all of them. This is the wiring the unit check only names.
    $env:AEC_HARVEST_PARALLEL = '3'
    $env:AEC_HARVEST_CHUNK = '1'
    try {
        $run = Invoke-Pipeline
        Check ($run.Code -eq 0) "a parallel run exits 0 (was $($run.Code))"
        Check ((Get-RowCount $csv) -eq 6748) 'it still merges 6,748 rows'
        Check ((Get-CsvHash) -eq $baselineHash) 'and writes the same bytes as a serial run'

        $chunkSummaries = @(Get-ChildItem $statusDir -Filter '_batch-*.json' -File)
        Check ($chunkSummaries.Count -eq 3) `
            "each chunk wrote its own summary (found $($chunkSummaries.Count))"

        $summed = 0
        foreach ($file in $chunkSummaries) {
            $chunk = Get-Content $file.FullName -Raw | ConvertFrom-Json
            $summed += [int]$chunk.drawings
        }
        Check ($summed -eq 3) "the chunk summaries account for every drawing (found $summed)"

        $markers = @(Get-ChildItem $statusDir -Filter '_current*.txt' -File)
        Check ($markers.Count -eq 0) "no blame marker survived the run (found $($markers.Count))"

        $stats = Get-Content $summary -Raw | ConvertFrom-Json
        Check ($stats.parallel -eq 3) "the combined summary records the concurrency ($($stats.parallel))"
        Check ($stats.chunks -eq 3) "and the chunk count ($($stats.chunks))"
        Check ($stats.ok -eq $stats.drawings) `
            "every drawing reported ok ($($stats.ok)/$($stats.drawings))"
    }
    finally {
        Remove-Item Env:AEC_HARVEST_PARALLEL -ErrorAction SilentlyContinue
        Remove-Item Env:AEC_HARVEST_CHUNK -ErrorAction SilentlyContinue
    }

    # ------------------------------------------------------------------ 7
    Write-Host ''
    Write-Host '[7] a proxy object is named as a missing vertical, not just counted'

    # No AutoCAD needed: the supervisor only aggregates verdicts that already exist. A
    # faked .ok carrying a proxy count is the cheapest way to prove the operator gets the
    # cause and the fix, not just "ACAD_PROXY_ENTITY=3" in a list of skipped classes.
    $proxyRoot = Join-Path $env:TEMP ('aec-proxy-' + [guid]::NewGuid().ToString('N'))
    try {
        $proxyRows   = Join-Path $proxyRoot 'Output\_rows'
        $proxyStatus = Join-Path $proxyRoot 'Output\_status'
        New-Item -ItemType Directory -Path $proxyRows, $proxyStatus -Force | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $proxyRows 'k.csv'), "x`r`n")
        [System.IO.File]::WriteAllText((Join-Path $proxyRows 'k.attributes.csv'), "x`r`n")
        [System.IO.File]::WriteAllText((Join-Path $proxyStatus 'k.ok'),
            '{"ok":true,"schema":4,"tool":"test","rows":1,"attributes":0,"ms":5,' +
            '"unsupported":{"ACAD_PROXY_ENTITY":3}}')
        $proxyManifest = Join-Path $proxyRoot 'manifest.txt'
        [System.IO.File]::WriteAllText($proxyManifest, "k|C:\does-not-exist.dwg`r`n")

        $console = 'C:\Program Files\Autodesk\AutoCAD 2026\accoreconsole.exe'
        if (-not (Test-Path $console)) {
            Write-Host '    skip  accoreconsole.exe not installed; the supervisor cannot start'
        }
        else {
            $proxyOut = & powershell -NoProfile -ExecutionPolicy Bypass -File `
                (Join-Path $Root 'tools\run-harvest-batch.ps1') `
                -Root $proxyRoot -Manifest $proxyManifest `
                -Script (Join-Path $Root 'Scripts\HarvestBatch.scr') -Console $console `
                -TimeoutSeconds 5 -ChunkSize 1 -Parallel 1 2>&1 | Out-String
            $proxyCode = $LASTEXITCODE

            Check ($proxyCode -eq 0) "the aggregation exits 0 (was $proxyCode)"
            Check ($proxyOut -match 'Skipped : ACAD_PROXY_ENTITY=3') 'the census still names the class'
            Check ($proxyOut -match 'Proxies : 3 object') 'the proxy count is called out separately'
            Check ($proxyOut -match 'AEC verticals') 'the cause names the missing vertical'
            Check ($proxyOut -match 'export it to plain AutoCAD objects') 'and the alternative fix'
        }
    }
    finally {
        Remove-Item $proxyRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    # ------------------------------------------------------------------ 8
    Write-Host ''
    Write-Host '[8] a relocated -Root is where the engine writes'

    # -Root relocates this script's own folders. The engine learns its output folder from the
    # environment, so the supervisor has to carry -Root across the process boundary too;
    # otherwise the work is written beside the plug-in DLL and this script reports "no status
    # file" for drawings that actually succeeded.
    $relocated = Join-Path $env:TEMP ('aec-root-' + [guid]::NewGuid().ToString('N'))
    try {
        $console = 'C:\Program Files\Autodesk\AutoCAD 2026\accoreconsole.exe'
        if (-not (Test-Path $console)) {
            Write-Host '    skip  accoreconsole.exe not installed; nothing to run'
        }
        else {
            $source = Get-ChildItem $inputDir -Filter *.dwg -File |
                Where-Object { $_.Name -notlike 'ZZ*' } | Select-Object -First 1
            if (-not $source) {
                Write-Host '    skip  no input drawing to relocate'
            }
            else {
                New-Item -ItemType Directory -Path $relocated -Force | Out-Null
                $key = 'relocated-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
                $copy = Join-Path $relocated ($key + '.dwg')
                Copy-Item $source.FullName $copy
                $relocatedManifest = Join-Path $relocated 'manifest.txt'
                [System.IO.File]::WriteAllText($relocatedManifest,
                    ($key + '|' + $copy + "`r`n"))

                $relocatedOut = & powershell -NoProfile -ExecutionPolicy Bypass -File `
                    (Join-Path $Root 'tools\run-harvest-batch.ps1') `
                    -Root $relocated -Manifest $relocatedManifest `
                    -Script (Join-Path $Root 'Scripts\HarvestBatch.scr') -Console $console `
                    -TimeoutSeconds 60 -ChunkSize 1 -Parallel 1 2>&1 | Out-String
                $relocatedCode = $LASTEXITCODE

                Check ($relocatedCode -eq 0) "a relocated run exits 0 (was $relocatedCode)"
                Check (Test-Path (Join-Path $relocated ('Output\_status\' + $key + '.ok'))) `
                    'the verdict lands under the requested root'
                Check (Test-Path (Join-Path $relocated ('Output\_rows\' + $key + '.csv'))) `
                    'and so do its rows'

                # The regression this section exists for: the engine resolving its output
                # folder from the assembly instead of from the root it was given.
                $binStatus = Join-Path $Root 'AEC_Batch_Data_Haravester\bin\Debug\net10.0-windows\Output\_status'
                $stray = Get-ChildItem $binStatus -Filter ($key + '.*') -ErrorAction SilentlyContinue
                Check (@($stray).Count -eq 0) 'nothing was written beside the plug-in DLL'
            }
        }
    }
    finally {
        Remove-Item $relocated -Recurse -Force -ErrorAction SilentlyContinue
    }
}
finally {
    Remove-Broken
    Remove-Item Env:AEC_HARVEST_TIMEOUT -ErrorAction SilentlyContinue
    Remove-Item Env:AEC_HARVEST_CHUNK -ErrorAction SilentlyContinue
    Remove-Item Env:AEC_HARVEST_PARALLEL -ErrorAction SilentlyContinue
}

Write-Host ''
if ($script:failures -gt 0) {
    Write-Host "[FAIL] $script:failures check(s) failed." -ForegroundColor Red
    exit 1
}

Write-Host '[OK] Every Phase 4 property held.' -ForegroundColor Green
exit 0
