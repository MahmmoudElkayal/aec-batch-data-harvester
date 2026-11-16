# run-harvest-batch.ps1 - process supervision for the batch engine
#
# Phase 4 of the hardening work. The engine (HarvestBatch) harvests a chunk of
# drawings in one accoreconsole session, each opened as a side database, and isolates
# every drawing's failure itself. What it cannot do is survive its own death - an access
# violation, a stack overflow, or a drawing that never returns. That is this script's job:
#
#   * one process per chunk, not per drawing          -> the startup cost is paid once
#   * up to AEC_HARVEST_PARALLEL chunks at once        -> wall time follows cores, not just I/O
#   * a wall-clock timeout, then the tree is killed    -> a hung drawing cannot stall a batch
#   * the in-flight drawing is marked .fail            -> never .ok, never silently skipped
#   * drawings that already reported are never re-run  -> resume is cheap
#   * anything left unreported after a normal exit is marked .fail, not retried forever
#
# Concurrency is why the chunk id below exists: the engine has always written one
# _current.txt marker and one _batch.json summary per process. Two chunks writing those
# names would overwrite each other's blame marker and summary, so each child now gets
# AEC_HARVEST_CHUNK_ID and writes _current-<id>.txt / _batch-<id>.json instead. This
# supervisor writes the combined _batch.json once every chunk has finished.
#
# RunPipeline.bat remains the supervisor of the run (flags, manifest, merge, exit code);
# this is the process supervisor for the engine. Every verdict it writes is a .fail: only
# the plugin writes .ok, and only after the row file has been published.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-harvest-batch.ps1 `
#       -Root . -Manifest Output\manifest.txt -Script Scripts\HarvestBatch.scr `
#       -Console "C:\...\accoreconsole.exe" [-TimeoutSeconds 300] [-ChunkSize 50] [-Parallel 1]
#
# Exit codes: 0 = every drawing reported .ok, 2 = at least one failed, 1 = nothing could run.

[CmdletBinding()]
param(
    [string] $Root,
    [string] $Manifest,
    [string] $Script,
    [string] $Console,
    [int] $TimeoutSeconds = 300,
    [int] $ChunkSize = 50,
    [int] $Parallel = 0
)

$ErrorActionPreference = 'Stop'

if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }

$outputDir = Join-Path $Root 'Output'
$rowsDir   = Join-Path $outputDir '_rows'
$statusDir = Join-Path $outputDir '_status'
$logsDir   = Join-Path $Root 'Logs'
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

# ------------------------------------------------------------------ concurrency
# Parallel chunks are opt-in. Each Core Console instance is a full AutoCAD process, so
# the useful range is small and bounded; -Parallel wins over the environment when given.
if ($Parallel -le 0) {
    $Parallel = 1
    $configured = $env:AEC_HARVEST_PARALLEL
    if ($configured) {
        $parsed = 0
        if ([int]::TryParse($configured.Trim(), [ref]$parsed) -and $parsed -gt 0) {
            $Parallel = $parsed
        }
        else {
            Write-Host "[WARN] AEC_HARVEST_PARALLEL='$configured' is not a positive integer; using 1."
        }
    }
}
if ($Parallel -gt 16) {
    Write-Host "[WARN] AEC_HARVEST_PARALLEL=$Parallel exceeds the cap of 16; using 16."
    $Parallel = 16
}

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

# A marker or per-chunk summary left by an earlier run names a drawing this run has not
# started. The ids restart at 001, so a stale file is indistinguishable from a fresh one;
# remove them before the first chunk rather than let a timeout read someone else's blame.
Get-ChildItem $statusDir -Filter '_current*.txt' -File -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue
Get-ChildItem $statusDir -Filter '_batch-*.json' -File -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue
Remove-Item $summary -Force -ErrorAction SilentlyContinue

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

function Get-EngineStats([string] $summaryPath) {
    if (-not (Test-Path $summaryPath)) { return [pscustomobject]@{ Drawings = 0; EngineMs = 0 } }
    try {
        $data = Get-Content $summaryPath -Raw | ConvertFrom-Json
    }
    catch {
        return [pscustomobject]@{ Drawings = 0; EngineMs = 0 }
    }
    return [pscustomobject]@{ Drawings = [int]$data.drawings; EngineMs = [int]$data.engineMs }
}

# ------------------------------------------------------------------ chunk process
function Start-ChunkProcess([string] $id, [int] $number, [object[]] $chunk,
    [string] $chunkFile, [string] $log) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Console
    $psi.Arguments = '/s "' + $Script + '" /l en-US'
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true

    # Start-Process cannot set a per-process environment in Windows PowerShell 5.1, and
    # two chunks must not share one manifest, one blame marker or one summary file.
    $psi.EnvironmentVariables['AEC_HARVEST_BATCH'] = $chunkFile
    $psi.EnvironmentVariables['AEC_HARVEST_CHUNK_ID'] = $id

    # The engine resolves where to write from the environment, so a relocated -Root has to
    # cross the process boundary. RunPipeline.bat exported these for the root it owns; a
    # direct call to this script with -Root had no such export, and every drawing landed in
    # the plug-in's bin\...\Output while the supervisor waited for files that never came.
    $psi.EnvironmentVariables['AEC_HARVEST_ROOT'] = $Root
    $psi.EnvironmentVariables['AEC_HARVEST_OUTPUT'] = $outputDir

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $psi
    [void]$process.Start()

    # EOF is what tells Core Console to leave its input loop.
    $process.StandardInput.Close()

    # Copy the pipes as bytes, not decoded text: Core Console writes UTF-16LE on a
    # redirected handle, and the log on disk should stay exactly what it wrote.
    $logStream = [System.IO.File]::Create($log)
    $errorStream = [System.IO.File]::Create($log + '.err')

    return [pscustomobject]@{
        Id          = $id
        Number      = $number
        Chunk       = $chunk
        ChunkFile   = $chunkFile
        Process     = $process
        StdOut      = $process.StandardOutput.BaseStream.CopyToAsync($logStream)
        StdErr      = $process.StandardError.BaseStream.CopyToAsync($errorStream)
        LogStream   = $logStream
        ErrorStream = $errorStream
        Log         = $log
        Started     = Get-Date
        TimedOut    = $false
        KillRunner  = $null
    }
}

function Save-ChunkLog($slot) {
    # Wait for both pipes to hit EOF, then close the files. A failure here is cosmetic:
    # the status files, not the log, decide the run.
    foreach ($task in @($slot.StdOut, $slot.StdErr)) {
        try { $task.Wait(10000) | Out-Null } catch { }
    }
    foreach ($stream in @($slot.LogStream, $slot.ErrorStream)) {
        try { $stream.Dispose() } catch { }
    }
    if ((Test-Path $slot.Log) -and (Get-Item $slot.Log).Length -eq 0) {
        Remove-Item $slot.Log -Force -ErrorAction SilentlyContinue
    }
    if ((Test-Path ($slot.Log + '.err')) -and (Get-Item ($slot.Log + '.err')).Length -eq 0) {
        Remove-Item ($slot.Log + '.err') -Force -ErrorAction SilentlyContinue
    }
}

function Start-Quiet([string] $commandLine) {
    # PowerShell 5.1 turns a native command's stderr into an ErrorRecord as soon as its
    # stdout is redirected, and $ErrorActionPreference = 'Stop' makes that fatal. A child
    # cmd process that redirects its own output keeps the call silent and non-throwing.
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $env:ComSpec
    $psi.Arguments = '/d /c "' + $commandLine + '"'
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true

    try {
        return [System.Diagnostics.Process]::Start($psi)
    }
    catch {
        return $null
    }
}

function Wait-Quiet($runner) {
    if ($null -eq $runner) { return }
    try {
        $runner.WaitForExit(20000) | Out-Null
        if (-not $runner.HasExited) { $runner.Kill() }
    }
    catch {
    }
    finally {
        try { $runner.Dispose() } catch { }
    }
}

function Stop-ChunkTree($process) {
    # taskkill terminates the process and the Core Console workers it started. A child
    # that is already exiting makes taskkill report an error; that is why this call is
    # wrapped: a failed kill must not abort the supervisor before it records the verdict.
    Wait-Quiet (Start-Quiet ('taskkill /T /F /PID ' + $process.Id + ' >nul 2>&1'))
    if (-not $process.WaitForExit(5000)) {
        # taskkill can lose a race with a process that is already terminating; the .NET
        # kill is the fallback so nothing survives to publish a verdict after we decided
        # this chunk failed.
        try { $process.Kill() } catch { }
        $process.WaitForExit(5000) | Out-Null
    }
}

function Complete-Chunk($slot) {
    $chunk = $slot.Chunk
    $id = $slot.Id
    $logPath = $slot.Log

    try {
        if ($slot.TimedOut) {
            # The loop already killed the tree as soon as the deadline passed, so that
            # one chunk's cleanup cannot give another chunk time to publish a verdict
            # after it was overruled. Here we only record what that means.
            $slot.Process.WaitForExit(5000) | Out-Null
        }

        Save-ChunkLog $slot
        $wall = ((Get-Date) - $slot.Started).TotalSeconds

        if ($slot.TimedOut) {
            # Only this chunk's marker may blame a drawing: another chunk's marker names
            # a key that is not in this chunk, and reading it would be worse than no marker.
            $markerPath = Join-Path $statusDir ('_current-' + $id + '.txt')
            $inFlight = $null
            if (Test-Path $markerPath) {
                $inFlight = ([System.IO.File]::ReadAllText($markerPath)).Trim()
                Remove-Item $markerPath -Force -ErrorAction SilentlyContinue
            }

            $culprit = $null
            if ($inFlight -and ($chunk | Where-Object { $_.Key -eq $inFlight }) -and
                -not (Test-Reported $inFlight)) {
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

            # Partial files from the killed process are not data - but only this chunk's.
            # Another chunk may be publishing its own files right now.
            foreach ($entry in $chunk) {
                Remove-Item (Join-Path $rowsDir ($entry.Key + '.csv.tmp')) `
                    -Force -ErrorAction SilentlyContinue
                Remove-Item (Join-Path $rowsDir ($entry.Key + '.attributes.csv.tmp')) `
                    -Force -ErrorAction SilentlyContinue
            }
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
                     $logPath + ' and in _status\_harvest*.error.txt.')
            }
        }

        $stats = Get-EngineStats (Join-Path $statusDir ('_batch-' + $id + '.json'))
        $script:totalEngineMs += $stats.EngineMs
        $script:totalDrawingsMeasured += $stats.Drawings

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

        Write-Host ("          chunk {0} wall time {1:0.0} s" -f $id, $wall)
    }
    finally {
        try { $slot.Process.Dispose() } catch { }
        Remove-Item $slot.ChunkFile -Force -ErrorAction SilentlyContinue
    }
}

# ------------------------------------------------------------------ the loop
# Slot-based rather than a straight loop: Fill starts as many chunks as -Parallel allows,
# then the loop waits for any one of them to exit or time out and handles it. With
# -Parallel 1 this is the old sequential behaviour, one chunk at a time.
$attempted = @{}
$script:totalEngineMs = 0
$script:totalDrawingsMeasured = 0
$chunkNumber = 0
$running = New-Object System.Collections.ArrayList

try {
    while ($true) {
        while ($running.Count -lt $Parallel) {
            $pending = @($entries | Where-Object {
                (-not $attempted.ContainsKey($_.Key)) -and (-not (Test-Complete $_.Key))
            })

            if ($pending.Count -eq 0) { break }

            $chunk = @($pending | Select-Object -First $ChunkSize)
            foreach ($entry in $chunk) { $attempted[$entry.Key] = $true }

            $chunkNumber++
            $id = '{0:D3}' -f $chunkNumber
            $chunkFile = Join-Path $outputDir ('chunk-{0}.txt' -f $id)
            $lines = @($chunk | ForEach-Object { $_.Key + '|' + $_.Path })
            [System.IO.File]::WriteAllText($chunkFile, (($lines -join "`r`n") + "`r`n"), $utf8NoBom)

            Write-Host ("[chunk {0}] {1} drawing(s)" -f $chunkNumber, $chunk.Count)

            $log = Join-Path $logsDir ('batch-{0:D3}.log' -f $chunkNumber)
            $started = Start-ChunkProcess $id $chunkNumber $chunk $chunkFile $log
            [void]$running.Add($started)
        }

        if ($running.Count -eq 0) { break }

        # Deadlines first, and every expired chunk's kill is launched before any of them
        # is waited on. Handling one chunk otherwise gives a neighbour time to finish and
        # publish a verdict after the supervisor has already overruled it.
        $expiring = @($running | Where-Object {
            (-not $_.Process.HasExited) -and
            (((Get-Date) - $_.Started).TotalSeconds -ge $TimeoutSeconds)
        })
        foreach ($slot in $expiring) {
            $slot.TimedOut = $true
            $slot.KillRunner = Start-Quiet ('taskkill /T /F /PID ' + $slot.Process.Id + ' >nul 2>&1')
        }
        foreach ($slot in $expiring) {
            Wait-Quiet $slot.KillRunner
            if (-not $slot.Process.WaitForExit(5000)) {
                try { $slot.Process.Kill() } catch { }
                $slot.Process.WaitForExit(5000) | Out-Null
            }
        }

        $finished = $null
        foreach ($slot in @($running)) {
            if ($slot.Process.HasExited -or $slot.TimedOut) {
                $finished = $slot
                break
            }
        }

        if (-not $finished) {
            Start-Sleep -Milliseconds 100
            continue
        }

        [void]$running.Remove($finished)
        Complete-Chunk $finished
    }
}
finally {
    foreach ($slot in @($running)) {
        if (-not $slot.Process.HasExited) {
            Stop-ChunkTree $slot.Process
        }
        Save-ChunkLog $slot
        $slot.Process.Dispose()
        Remove-Item $slot.ChunkFile -Force -ErrorAction SilentlyContinue
    }
}

# ------------------------------------------------------------------ verdict
$okCount = @($entries | Where-Object { Test-Complete $_.Key }).Count
$failCount = @($entries | Where-Object { Test-Path (Join-Path $statusDir ($_.Key + '.fail')) }).Count
$unreported = $entries.Count - $okCount - $failCount

# The engine writes one summary per chunk (_batch-<id>.json), because concurrent chunks
# cannot share a file. This is the combined view and the one a caller should read: it is
# written after every chunk has finished, and it exists even when a chunk was killed.
$combinedPerDrawing = 0.0
if ($totalDrawingsMeasured -gt 0) {
    $combinedPerDrawing = [double]$totalEngineMs / $totalDrawingsMeasured
}
try {
    $combined = [string]::Format(
        [System.Globalization.CultureInfo]::InvariantCulture,
        '{{"drawings":{0},"ok":{1},"failed":{2},"engineMs":{3},"perDrawingMs":{4:0.0},' +
        '"parallel":{5},"chunks":{6}}}',
        $entries.Count, $okCount, $failCount, $totalEngineMs, $combinedPerDrawing,
        $Parallel, $chunkNumber)
    [System.IO.File]::WriteAllText($summary, $combined, $utf8NoBom)
}
catch {
    # A missing summary is not a failed harvest; the verdict files still decide the run.
}

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

    # A proxy is not "a class we chose not to extract": it is an object whose defining
    # application is absent, so its geometry and property data do not exist in this
    # process at all. That has a different fix, and naming it here is the difference
    # between "the harvester is incomplete" and "install the vertical".
    if ($unsupportedTotals.ContainsKey('ACAD_PROXY_ENTITY')) {
        Write-Host ("Proxies : {0} object(s) arrived as ACAD_PROXY_ENTITY." -f $unsupportedTotals['ACAD_PROXY_ENTITY'])
        Write-Host '          AEC verticals (Architecture, MEP, Civil 3D) store objects in their own'
        Write-Host '          classes. Without that vertical installed here, AutoCAD cannot read them:'
        Write-Host '          no geometry, no property sets, no schedules. Harvest the drawing where'
        Write-Host '          the vertical is installed, or export it to plain AutoCAD objects first.'
    }
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
