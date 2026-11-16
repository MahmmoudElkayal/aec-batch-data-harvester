# run-harvest-batch.ps1 - process supervision for the batch engine
#
# Phase 4 of the hardening work. The engine (HarvestBatch) harvests a chunk of
# drawings in one accoreconsole session, each opened as a side database, and isolates
# every drawing's failure itself. What it cannot do is survive its own death - an access
# violation, a stack overflow, or a drawing that never returns. That is this script's job:
#
#   * one process per chunk, not per drawing          -> the startup cost is paid once
#   * a wall-clock timeout, then the tree is killed    -> a hung drawing cannot stall a batch
#   * the in-flight drawing is marked .fail            -> never .ok, never silently skipped
#   * drawings that already reported are never re-run  -> resume is cheap
#   * anything left unreported after a normal exit is marked .fail, not retried forever
#
# RunPipeline.bat remains the supervisor of the run (flags, manifest, merge, exit code);
# this is the process supervisor for the engine. Every verdict it writes is a .fail: only
# the plugin writes .ok, and only after the row file has been published.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-harvest-batch.ps1 `
#       -Root . -Manifest Output\manifest.txt -Script Scripts\HarvestBatch.scr `
#       -Console "C:\...\accoreconsole.exe" [-TimeoutSeconds 300] [-ChunkSize 50]
#
# Exit codes: 0 = every drawing reported .ok, 2 = at least one failed, 1 = nothing could run.

[CmdletBinding()]
param(
    [string] $Root,
    [string] $Manifest,
    [string] $Script,
    [string] $Console,
    [int] $TimeoutSeconds = 300,
    [int] $ChunkSize = 50
)

$ErrorActionPreference = 'Stop'

if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }

$outputDir = Join-Path $Root 'Output'
$rowsDir   = Join-Path $outputDir '_rows'
$statusDir = Join-Path $outputDir '_status'
$logsDir   = Join-Path $Root 'Logs'
$chunkFile = Join-Path $outputDir 'batch.txt'
$marker    = Join-Path $statusDir '_current.txt'
$summary   = Join-Path $statusDir '_batch.json'

# The output contract this supervisor is willing to trust, in step with
# HarvestSchema.Version (AEC_Batch_Data_Haravester\Core\HarvestSchema.cs).
# tools\test-coverage.ps1 fails if the two drift apart.
$SchemaVersion = 4

if (-not $Manifest) { $Manifest = Join-Path $outputDir 'manifest.txt' }
if (-not $Script)   { $Script   = Join-Path $Root 'Scripts\HarvestBatch.scr' }

foreach ($path in @($Manifest, $Script)) {
    if (-not (Test-Path $path)) {
        Write-Host "[FATAL] Missing: $path"
        exit 1
    }
}
if (-not $Console) {
    $Console = 'C:\Program Files\Autodesk\AutoCAD 2026\accoreconsole.exe'
}
if (-not (Test-Path $Console)) {
    Write-Host "[FATAL] accoreconsole.exe not found at $Console"
    exit 1
}

foreach ($dir in @($outputDir, $rowsDir, $statusDir, $logsDir)) {
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
}

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

# Core Console is stopped from waiting for input by EOF on stdin. RedirectStandardInput
# resolves a relative path against the child's working directory, so it must be absolute.
$stdinFile = Join-Path $env:TEMP 'aec-batch.stdin'
[System.IO.File]::WriteAllText($stdinFile, '', $utf8NoBom)

# ------------------------------------------------------------------ manifest
function Read-Entries([string] $path) {
    $entries = New-Object System.Collections.ArrayList
    foreach ($line in [System.IO.File]::ReadAllLines($path)) {
        $text = $line.Trim()
        if ($text.Length -eq 0 -or $text.StartsWith('#')) { continue }

        $parts = $text -split '\|', 2
        if ($parts.Count -ne 2 -or $parts[0].Length -eq 0) {
            throw "manifest line is not 'key|path': $line"
        }

        $key = $parts[0].Trim()

        # The key becomes <key>.csv, <key>.ok and <key>.fail. Anything that is not a
        # single path component would write outside _status, so refuse the whole manifest
        # rather than create files nobody will look for.
        if ($key -match '[\\/:*?"<>|]') {
            throw "manifest key is not usable as a file name: '$key'"
        }

        [void]$entries.Add([pscustomobject]@{ Key = $key; Path = $parts[1].Trim() })
    }
    return $entries
}

try {
    $entries = @(Read-Entries $Manifest)
}
catch {
    Write-Host "[FATAL] $($_.Exception.Message)"
    exit 1
}

if ($entries.Count -eq 0) {
    Write-Host '[WARN] The manifest is empty - nothing to harvest.'
    exit 0
}

# ------------------------------------------------------------------ verdicts
function Test-Reported([string] $key) {
    # A drawing has reported exactly when the plugin wrote .ok or .fail. A row file with
    # no verdict is not a result: the status file is the contract.
    return (Test-Path (Join-Path $statusDir ($key + '.ok'))) -or
           (Test-Path (Join-Path $statusDir ($key + '.fail')))
}

function Read-Verdict([string] $key, [string] $extension) {
    $path = Join-Path $statusDir ($key + $extension)
    if (-not (Test-Path $path)) { return $null }
    try { return ([System.IO.File]::ReadAllText($path) | ConvertFrom-Json) } catch { return $null }
}

function Test-Complete([string] $key) {
    # Both files are part of one drawing's output: entity rows, attribute rows, and the
    # verdict. A resume must not treat a drawing from before Phase 5, or a kill between
    # the two publishes, as finished.
    if (-not (Test-Path (Join-Path $rowsDir ($key + '.csv')))) { return $false }
    if (-not (Test-Path (Join-Path $rowsDir ($key + '.attributes.csv')))) { return $false }

    # The verdict must also come from this schema. An .ok written by an older engine
    # describes a different CSV, and merging it would put two extraction rules in one
    # file with nothing to say which row came from which.
    $verdict = Read-Verdict $key '.ok'
    return ($null -ne $verdict) -and ([int]$verdict.schema -eq $SchemaVersion)
}

function Write-Fail([string] $key, [string] $drawing, [string] $message, [string] $detail) {
    # Same shape as the plugin's status file, because the supervisor and any later reader
    # must not care which side wrote it.
    $payload = '{"ok":false,"schema":' + $SchemaVersion +
               ',"tool":"supervisor","drawing":"' + ($drawing -replace '\\', '\\\\' -replace '"', '\"') +
               '","rows":0,"attrs":0,"skipped":0,"unsupported":{},"unsupportedTotal":0,"ms":0,"message":"' +
               ($message -replace '\\', '\\\\' -replace '"', '\"') + '"}'

    [System.IO.File]::WriteAllText((Join-Path $statusDir ($key + '.fail')), $payload, $utf8NoBom)

    if ($detail) {
        [System.IO.File]::WriteAllText((Join-Path $statusDir ($key + '.error.txt')), $detail, $utf8NoBom)
    }

    # A .fail must never sit beside a .ok, whatever left it there.
    $ok = Join-Path $statusDir ($key + '.ok')
    if (Test-Path $ok) { Remove-Item $ok -Force }

    Write-Host "  FAIL  - $message"
}

function Get-EngineStats {
    if (-not (Test-Path $summary)) { return [pscustomobject]@{ Drawings = 0; EngineMs = 0 } }
    try {
        $data = Get-Content $summary -Raw | ConvertFrom-Json
    }
    catch {
        return [pscustomobject]@{ Drawings = 0; EngineMs = 0 }
    }
    return [pscustomobject]@{ Drawings = [int]$data.drawings; EngineMs = [int]$data.engineMs }
}

# ------------------------------------------------------------------ the loop
$attempted = @{}
$totalEngineMs = 0
$totalDrawingsMeasured = 0
$chunkNumber = 0
$failedChunks = 0

try {
    while ($true) {
        $pending = @($entries | Where-Object {
            (-not $attempted.ContainsKey($_.Key)) -and (-not (Test-Complete $_.Key))
        })

        if ($pending.Count -eq 0) { break }

        $chunk = @($pending | Select-Object -First $ChunkSize)
        foreach ($entry in $chunk) { $attempted[$entry.Key] = $true }

        $chunkNumber++
        $lines = @($chunk | ForEach-Object { $_.Key + '|' + $_.Path })
        [System.IO.File]::WriteAllText($chunkFile, (($lines -join "`r`n") + "`r`n"), $utf8NoBom)

        Write-Host ("[chunk {0}] {1} drawing(s)" -f $chunkNumber, $chunk.Count)

        $env:AEC_HARVEST_BATCH = $chunkFile
        $log = Join-Path $logsDir ('batch-{0:D3}.log' -f $chunkNumber)

        $started = Get-Date
        $process = Start-Process -FilePath $Console `
            -ArgumentList ('/s "' + $Script + '" /l en-US') `
            -RedirectStandardInput $stdinFile `
            -RedirectStandardOutput $log -RedirectStandardError ($log + '.err') `
            -NoNewWindow -PassThru

        $finished = $process.WaitForExit($TimeoutSeconds * 1000)

        if (-not $finished) {
            # Hung or crashing. Kill the tree: accoreconsole spawns workers.
            & taskkill /T /F /PID $process.Id 2>&1 | Out-Null
            $process.WaitForExit(10000) | Out-Null

            $inFlight = $null
            if (Test-Path $marker) {
                $inFlight = ([System.IO.File]::ReadAllText($marker)).Trim()
            }

            $culprit = $null
            if ($inFlight -and ($chunk | Where-Object { $_.Key -eq $inFlight }) -and -not (Test-Reported $inFlight)) {
                $culprit = $inFlight
            }
            else {
                $first = $chunk | Where-Object { -not (Test-Reported $_.Key) } | Select-Object -First 1
                if ($first) { $culprit = $first.Key }
            }

            if ($culprit) {
                $entry = $chunk | Where-Object { $_.Key -eq $culprit } | Select-Object -First 1
                $reason = "timed out after $TimeoutSeconds s (killed by the supervisor)"
                Write-Fail $culprit ([System.IO.Path]::GetFileName($entry.Path)) $reason `
                    ("No status file was written. The process was still working on this drawing " +
                     "when it was killed, so the drawing is treated as failed. Increase " +
                     "AEC_HARVEST_TIMEOUT if it is merely slow.")
            }
            else {
                # Everything in the chunk had already reported; the death cost nothing.
                Write-Host '  (the process died after finishing every drawing in the chunk)'
            }

            # Partial files from the killed process are not data.
            Get-ChildItem $rowsDir -Filter *.tmp -File -ErrorAction SilentlyContinue |
                Remove-Item -Force -ErrorAction SilentlyContinue
            Get-ChildItem $statusDir -Filter *.tmp -File -ErrorAction SilentlyContinue |
                Remove-Item -Force -ErrorAction SilentlyContinue

            $failedChunks++
        }
        else {
            # Normal exit. Anything in this chunk still without a verdict never reached the
            # command (a .NET exception in the manifest, a failed NETLOAD). Mark it now, or
            # the loop would retry it forever.
            foreach ($entry in $chunk) {
                if (Test-Reported $entry.Key) { continue }

                Write-Fail $entry.Key ([System.IO.Path]::GetFileName($entry.Path)) `
                    'the engine produced no status file for this drawing' `
                    ("The chunk process exited without reporting this drawing. The reason is in " +
                     (Join-Path $logsDir ('batch-{0:D3}.log' -f $chunkNumber)) + ' and in _status\_harvest.error.txt.')

                $failedChunks++
            }
        }

        $stats = Get-EngineStats
        $totalEngineMs += $stats.EngineMs
        $totalDrawingsMeasured += $stats.Drawings

        # Per-drawing lines, so the console still reads like the old one-per-process run.
        foreach ($entry in $chunk) {
            if (Test-Path (Join-Path $statusDir ($entry.Key + '.ok'))) {
                $rows = ''
                $rowsFile = Join-Path $statusDir ($entry.Key + '.rows')
                if (Test-Path $rowsFile) { $rows = ([System.IO.File]::ReadAllText($rowsFile)).Trim() }
                if ($rows) {
                    Write-Host ("  OK    - {0} rows  {1}" -f $rows, $entry.Key)
                } else {
                    Write-Host ("  OK    - {0}" -f $entry.Key)
                }
            }
            elseif (Test-Path (Join-Path $statusDir ($entry.Key + '.fail'))) {
                # Write-Fail already printed the reason; the key is in it.
            }
        }

        $elapsed = ((Get-Date) - $started).TotalSeconds
        Write-Host ("          chunk wall time {0:0.0} s" -f $elapsed)
    }
}
finally {
    Remove-Item Env:AEC_HARVEST_BATCH -ErrorAction SilentlyContinue
    if (Test-Path $marker) { Remove-Item $marker -Force -ErrorAction SilentlyContinue }
}

# ------------------------------------------------------------------ verdict
$okCount = @($entries | Where-Object { Test-Complete $_.Key }).Count
$failCount = @($entries | Where-Object { Test-Path (Join-Path $statusDir ($_.Key + '.fail')) }).Count
$unreported = $entries.Count - $okCount - $failCount

# ------------------------------------------------------- unsupported census
# Sums the classes the engine could not read across every completed drawing. A corpus
# that is mostly hatches, dimensions or proxies would otherwise look exactly like a
# clean corpus: same exit code, same shape of CSV, fewer rows and no explanation.
$unsupportedTotals = @{}
foreach ($entry in $entries) {
    $verdict = Read-Verdict $entry.Key '.ok'
    if ($null -eq $verdict -or $null -eq $verdict.unsupported) { continue }
    foreach ($property in $verdict.unsupported.PSObject.Properties) {
        $unsupportedTotals[$property.Name] = [int]$unsupportedTotals[$property.Name] + [int]$property.Value
    }
}

Write-Host ''
Write-Host ("Engine  : {0} drawing(s), {1} ms of engine time" -f $totalDrawingsMeasured, $totalEngineMs)
if ($totalDrawingsMeasured -gt 0) {
    $perDrawing = [double]$totalEngineMs / $totalDrawingsMeasured
    Write-Host ("Per dwg : {0:0.0} ms (engine only, excludes process start)" -f $perDrawing)
}
if ($unsupportedTotals.Count -gt 0) {
    $top = $unsupportedTotals.GetEnumerator() |
        Sort-Object -Property Value -Descending | Select-Object -First 8
    $rendered = ($top | ForEach-Object { '{0}={1}' -f $_.Key, $_.Value }) -join ', '
    Write-Host ("Skipped : {0}" -f $rendered)
}

if ($unreported -gt 0) {
    # Should be impossible: every path above writes a verdict. If it happens, say so
    # rather than let a drawing vanish from the count.
    Write-Host ("[FAIL] {0} drawing(s) have no verdict at all." -f $unreported)
    exit 2
}

# Only a .ok is success. A .fail is a failure even if the process exited 0.
if ($failCount -gt 0) {
    exit 2
}
exit 0
