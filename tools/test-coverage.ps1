# test-coverage.ps1 - Phase 5 acceptance
#
# Phase 5 is about coverage: the harvester must see every entity in the plan's census,
# carry block attributes in a separate long-format file, and prove paper space is scanned.
# Those are four different claims, so they get four independent checks:
#
#   1. row counts match the census exactly, per drawing and per entity type
#   2. 219 attribute rows for 144 block references, including the probe values
#   3. every attribute handle resolves in the entity file (the join actually works)
#   4. paper space is traversed - proved with a fixture, because the sample corpus only
#      has viewports there and a zero-row assertion would pass even if it were ignored
#   5. the status file tells the truth: it names the output schema, counts an unsupported
#      RAY by class, and carries a constant attribute read from the block definition
#
# A source-level check proves the plan's "one class + one registration line"
# property by matching every EntityKind to exactly one extractor and every extractor to
# the registry. It also proves the engine and the supervisor agree on the schema number,
# because a stale literal in either one turns resume into silent data corruption.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-coverage.ps1
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
$csv       = Join-Path $outputDir 'Extracted_BOM.csv'
$attrCsv   = Join-Path $outputDir 'Extracted_Attributes.csv'
$console   = 'C:\Program Files\Autodesk\AutoCAD 2026\accoreconsole.exe'

$script:failures = 0

# The supervisor refuses .ok files written under another schema, so its literal and the
# engine's constant must agree. Both are read from source rather than restated here: a
# stale copy in this script would happily certify a drift it caused.
$schemaSource = [System.IO.File]::ReadAllText((Join-Path $Root 'AEC_Batch_Data_Haravester\Core\HarvestSchema.cs'))
$SchemaVersion = [int][regex]::Match($schemaSource, 'const\s+int\s+Version\s*=\s*(\d+)').Groups[1].Value
$supervisorSource = [System.IO.File]::ReadAllText((Join-Path $Root 'tools\run-harvest-batch.ps1'))
$supervisorVersion = [int][regex]::Match($supervisorSource, '\$SchemaVersion\s*=\s*(\d+)').Groups[1].Value

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

# ------------------------------------------------------------------ the census
# Corpus census, re-checked against the drawings.
$census = @{
    'Assembly Sample.dwg|Block'    = 46
    'Assembly Sample.dwg|Polyline' = 582
    'Assembly Sample.dwg|Line'     = 1305
    'Assembly Sample.dwg|Arc'      = 187
    'Assembly Sample.dwg|Circle'   = 64
    'Assembly Sample.dwg|DBText'   = 64
    'Assembly Sample.dwg|Solid'    = 58

    'Floor Plan Sample.dwg|Block'    = 437
    'Floor Plan Sample.dwg|Polyline' = 2193
    'Floor Plan Sample.dwg|Line'     = 1737
    'Floor Plan Sample.dwg|DBText'   = 2
    'Floor Plan Sample.dwg|MText'    = 53

    'House Designer.dwg|Block' = 20
}

$censusTotal = 0
foreach ($count in $census.Values) { $censusTotal += $count }

try {
    Write-Host ''
    Write-Host '[1] row counts match the census, per drawing and per type'

    $run = Invoke-Pipeline
    Check ($run.Code -eq 0) "the clean run exits 0 (was $($run.Code))"

    $rows = @(Import-Csv $csv)
    Check ($rows.Count -eq $censusTotal) "the merge has $censusTotal rows (found $($rows.Count))"

    $actual = @{}
    foreach ($group in ($rows | Group-Object { "$($_.Drawing)|$($_.EntityType)" })) {
        $actual[$group.Name] = $group.Count
    }

    foreach ($key in ($census.Keys | Sort-Object)) {
        $want = $census[$key]
        $got = 0
        if ($actual.ContainsKey($key)) { $got = $actual[$key] }
        Check ($got -eq $want) "$key = $want (found $got)"
    }

    $unexpected = @($actual.Keys | Where-Object { -not $census.ContainsKey($_) })
    Check ($unexpected.Count -eq 0) `
        ("no unexpected drawing/type combination ({0})" -f ($unexpected -join '; '))

    $paper = @($rows | Where-Object { $_.Space -ne 'Model' })
    Check ($paper.Count -eq 0) "the sample corpus still has no paper-space rows (found $($paper.Count))"

    # ------------------------------------------------------------------ 2
    Write-Host ''
    Write-Host '[2] block attributes are complete and lossless'

    $attrs = @(Import-Csv $attrCsv)
    Check ($attrs.Count -eq 219) "219 attribute rows (found $($attrs.Count))"

    $owners = @($attrs | Group-Object Drawing, EntityHandle)
    Check ($owners.Count -eq 144) "219 attributes belong to 144 block references (found $($owners.Count))"

    foreach ($value in @('CE003902', 'CH00531', 'RIVET #511 X 3/16', 'VERT CARR BRKT. ASM')) {
        $hit = @($attrs | Where-Object { $_.Value -eq $value })
        Check ($hit.Count -ge 1) "the probe value survives: $value"
    }

    $hashTag = @($attrs | Where-Object { $_.Tag -eq '#' })
    Check ($hashTag.Count -ge 1) "a tag of '#' is carried without a column named '#' (found $($hashTag.Count))"

    # ------------------------------------------------------------------ 3
    Write-Host ''
    Write-Host '[3] every attribute handle resolves in the entity file'

    $entityKeys = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($row in $rows) { [void]$entityKeys.Add($row.Drawing + '|' + $row.Handle) }

    $unresolved = @()
    foreach ($attr in $attrs) {
        if (-not $entityKeys.Contains($attr.Drawing + '|' + $attr.EntityHandle)) {
            $unresolved += ($attr.Drawing + '|' + $attr.EntityHandle + '|' + $attr.Tag)
        }
    }
    Check ($unresolved.Count -eq 0) `
        ("no dangling attribute handle ({0} unresolved)" -f $unresolved.Count)
    if ($unresolved.Count -gt 0) {
        Write-Host ('          first: ' + $unresolved[0])
    }

    $blankTag = @($attrs | Where-Object { [string]::IsNullOrWhiteSpace($_.Tag) })
    Check ($blankTag.Count -eq 0) "every attribute has a tag (found $($blankTag.Count) blank)"

    # ------------------------------------------------------------------ 4
    Write-Host ''
    Write-Host '[4] paper space is traversed'

    $source = Get-ChildItem $inputDir -Filter *.dwg -File |
        Where-Object { $_.Name -notlike 'ZZ*' } | Select-Object -First 1
    if (-not $source) {
        Check $false 'a source drawing exists to build the paper-space fixture'
    }
    else {
        $scratch = Join-Path $env:TEMP ('aec-paper-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
        New-Item -ItemType Directory -Path $scratch -Force | Out-Null
        $previousOutput = $env:AEC_HARVEST_OUTPUT
        $previousRoot = $env:AEC_HARVEST_ROOT

        try {
            $fixture = Join-Path $scratch 'Paper Probe.dwg'
            Copy-Item $source.FullName $fixture

            # entmake with a 410 group places the entity in the named layout, independent
            # of what is visible in the editor. The 410 value is the current tab name.
            $editScript = Join-Path $scratch 'make.scr'
            # Four probe objects in one drawing:
            #   TEXT  - paper space is traversed (section 4)
            #   RAY   - an unsupported class is counted, not silently dropped (section 6)
            #   INSERT of CONSTPROBE - a constant attribute is read from the definition
            #          (section 7). Constant attributes are never copied onto the reference,
            #          so only a definition read can find this value.
            $lines = @(
                '(setvar "CTAB" "Layout1")',
                '(entmake (list (cons 0 "TEXT") (cons 8 "0") (cons 10 (list 5.0 5.0 0.0)) (cons 40 0.25) (cons 1 "PAPER-SPACE-PROBE") (cons 410 (getvar "CTAB"))))',
                '(entmake (list (cons 0 "RAY") (cons 100 "AcDbEntity") (cons 100 "AcDbRay") (cons 8 "0") (cons 10 (list 0.0 0.0 0.0)) (cons 11 (list 1.0 0.0 0.0)) (cons 410 (getvar "CTAB"))))',
                '(entmake (list (cons 0 "BLOCK") (cons 2 "CONSTPROBE") (cons 70 0) (cons 10 (list 0.0 0.0 0.0))))',
                '(entmake (list (cons 0 "ATTDEF") (cons 8 "0") (cons 10 (list 0.0 0.0 0.0)) (cons 40 0.25) (cons 1 "CONST-VALUE") (cons 2 "CTAG") (cons 3 "constant probe") (cons 70 2)))',
                '(entmake (list (cons 0 "ENDBLK")))',
                '(entmake (list (cons 0 "INSERT") (cons 8 "0") (cons 2 "CONSTPROBE") (cons 10 (list 10.0 10.0 0.0)) (cons 410 (getvar "CTAB"))))',
                '(command "_.QSAVE")'
            )
            [System.IO.File]::WriteAllText(
                $editScript, (($lines -join "`r`n") + "`r`n"),
                (New-Object System.Text.UTF8Encoding($false)))

            $stdin = Join-Path $scratch 'empty.stdin'
            [System.IO.File]::WriteAllText($stdin, '')
            $editLog = Join-Path $scratch 'edit.log'
            $editErr = Join-Path $scratch 'edit.err'

            $arguments = '/i "' + $fixture + '" /s "' + $editScript + '" /l en-US'
            $previousEap = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                Start-Process -FilePath $console -ArgumentList $arguments `
                    -RedirectStandardInput $stdin -RedirectStandardOutput $editLog `
                    -RedirectStandardError $editErr -NoNewWindow -Wait | Out-Null
            }
            finally {
                $ErrorActionPreference = $previousEap
            }

            # Harvest the fixture through the same supervisor the pipeline uses, with its
            # own output tree so the real Output\ is untouched.
            $probeOutput = Join-Path $scratch 'Output'
            New-Item -ItemType Directory -Path $probeOutput -Force | Out-Null
            $manifest = Join-Path $probeOutput 'manifest.txt'
            [System.IO.File]::WriteAllText(
                $manifest,
                "Paper Probe-11223344|$fixture`r`n",
                (New-Object System.Text.UTF8Encoding($false)))

            $env:AEC_HARVEST_OUTPUT = $probeOutput
            $env:AEC_HARVEST_ROOT = $scratch

            $probeScript = Join-Path $Root 'Scripts\HarvestBatch.scr'
            $out = & powershell -NoProfile -ExecutionPolicy Bypass -File `
                (Join-Path $Root 'tools\run-harvest-batch.ps1') `
                -Root $scratch -Manifest $manifest -Script $probeScript `
                -Console $console 2>&1 | Out-String
            $probeCode = $LASTEXITCODE

            Check ($probeCode -eq 0) "the fixture is harvested (exit was $probeCode)"

            $probeRows = Join-Path $probeOutput '_rows\Paper Probe-11223344.csv'
            $found = @()
            if (Test-Path $probeRows) {
                $found = @(Import-Csv $probeRows |
                    Where-Object { $_.Space -eq 'Paper' -and $_.Name -eq 'PAPER-SPACE-PROBE' })
            }

            Check ($found.Count -eq 1) `
                ("the paper-space text is one row with Space=Paper (found {0})" -f $found.Count)
            if ($found.Count -eq 1) {
                Check ($found[0].EntityType -eq 'DBText') `
                    ("and the extractor ran on it (type {0})" -f $found[0].EntityType)
            }

            # ---------------------------------------------- 6 and 7 status truth
            # The .ok file is the only place a reader can learn what was NOT harvested, so
            # the fixture asks the two questions a clean CSV cannot answer: did the status
            # name the schema, and did it count the RAY the registry cannot extract?
            $verdictPath = Join-Path $probeOutput '_status\Paper Probe-11223344.ok'
            $verdict = $null
            if (Test-Path $verdictPath) {
                try { $verdict = [System.IO.File]::ReadAllText($verdictPath) | ConvertFrom-Json } catch { }
            }

            $verdictSchema = $null
            if ($verdict) { $verdictSchema = $verdict.schema }
            Check ($null -ne $verdictSchema -and [int]$verdictSchema -eq $SchemaVersion) `
                ("the verdict names the schema the engine wrote ({0}, expected {1})" -f $verdictSchema, $SchemaVersion)

            $rayCount = 0
            if ($verdict -and $verdict.unsupported) { $rayCount = [int]$verdict.unsupported.RAY }
            Check ($rayCount -eq 1) ("an unsupported RAY is counted by class (found {0})" -f $rayCount)

            $probeAttrs = Join-Path $probeOutput '_rows\Paper Probe-11223344.attributes.csv'
            $constant = @()
            if (Test-Path $probeAttrs) {
                $constant = @(Import-Csv $probeAttrs |
                    Where-Object { $_.Tag -eq 'CTAG' -and $_.Value -eq 'CONST-VALUE' -and $_.IsConstant -eq '1' })
            }
            Check ($constant.Count -eq 1) `
                ("a constant attribute is read from the definition (found {0})" -f $constant.Count)

            if ($found.Count -eq 0) {
                Write-Host '          supervisor output:'
                foreach ($line in ($out -split "`r?`n")) {
                    if ($line.Trim()) { Write-Host ('            ' + $line) }
                }
            }
        }
        finally {
            $env:AEC_HARVEST_OUTPUT = $previousOutput
            $env:AEC_HARVEST_ROOT = $previousRoot
            Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    # ------------------------------------------------------------------ 5
    Write-Host ''
    Write-Host '[5] adding an entity type is one class plus one registration line'

    $kindSource = [System.IO.File]::ReadAllText(
        (Join-Path $Root 'AEC_Batch_Data_Haravester\Core\EntityKind.cs'))
    $enumBody = [regex]::Match($kindSource, 'enum\s+EntityKind\s*\{([^}]*)\}').Groups[1].Value
    $kinds = @([regex]::Matches($enumBody, '(?m)^\s*([A-Za-z][A-Za-z0-9_]*)\s*,?\s*$') |
        ForEach-Object { $_.Groups[1].Value } |
        Where-Object { $_ -ne 'EntityKind' })

    Check ($kinds.Count -gt 0) "the enum lists entity kinds (found $($kinds.Count))"

    $extractorDir = Join-Path $Root 'AEC_Batch_Data_Haravester\Extraction'
    $classes = @{}
    foreach ($file in Get-ChildItem $extractorDir -Filter *Extractor.cs -File) {
        if ($file.Name -eq 'IEntityExtractor.cs') { continue }
        $text = [System.IO.File]::ReadAllText($file.FullName)
        $class = [regex]::Match($text, 'class\s+([A-Za-z0-9_]+)\s*:\s*IEntityExtractor')
        $kind = [regex]::Match($text, 'EntityKind\.([A-Za-z0-9_]+)')
        if ($class.Success -and $kind.Success) {
            $classes[$kind.Groups[1].Value] = $class.Groups[1].Value
        }
    }

    foreach ($kind in $kinds) {
        Check ($classes.ContainsKey($kind)) "EntityKind.$kind has one extractor class"
    }

    $registry = [System.IO.File]::ReadAllText((Join-Path $extractorDir 'ExtractorRegistry.cs'))
    foreach ($class in ($classes.Values | Sort-Object)) {
        Check ($registry -match ('new\s+' + [regex]::Escape($class) + '\s*\(\s*\)')) `
            "$class is registered in CreateDefault"
    }

    Check ($classes.Count -eq $kinds.Count) `
        ("every extractor maps to its own EntityKind ($($classes.Count) of $($kinds.Count))")

    Check ($SchemaVersion -eq $supervisorVersion) `
        ("the supervisor trusts schema {0} and the engine writes {1}" -f $supervisorVersion, $SchemaVersion)
}
finally {
    Remove-Item Env:AEC_HARVEST_OUTPUT -ErrorAction SilentlyContinue
    Remove-Item Env:AEC_HARVEST_ROOT -ErrorAction SilentlyContinue
}

Write-Host ''
if ($script:failures -gt 0) {
    Write-Host "[FAIL] $script:failures check(s) failed." -ForegroundColor Red
    exit 1
}

Write-Host '[OK] Every Phase 5 property held.' -ForegroundColor Green
exit 0
