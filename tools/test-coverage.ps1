# test-coverage.ps1 - Phase 5 acceptance
#
# Phase 5 is about coverage: the harvester must see every entity in the plan's census,
# carry block attributes in a separate long-format file, and prove paper space is scanned.
# Those are different claims, so they get independent checks:
#
#   1. row counts match the census exactly, per drawing and per entity type
#   2. 219 attribute rows for 144 block references, including the probe values
#   3. every attribute handle resolves in the entity file (the join actually works)
#   4. paper space, nested blocks, xrefs and area - proved with a fixture, because the
#      sample corpus has none of them and a zero-row assertion would pass even if the
#      code ignored them
#   5. the status file tells the truth: it names the output schema, counts an unsupported
#      3DFACE by class, and carries a constant attribute read from the block definition
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
    Write-Host '[4] paper space, nesting, xrefs and area are traversed'

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

            # A second, deliberately tiny drawing to attach as an xref: one 5-unit line
            # at the origin. Erasing the copied sample's top-level entities keeps the xref
            # cheap; its block definitions are irrelevant to this assertion.
            $xrefSource = Join-Path $scratch 'xref-source.dwg'
            Copy-Item $source.FullName $xrefSource

            $runCore = {
                param([string] $dwgPath, [string] $scriptPath, [string] $logPath)
                $stdin = Join-Path $scratch 'empty.stdin'
                [System.IO.File]::WriteAllText($stdin, '')
                $arguments = '/i "' + $dwgPath + '" /s "' + $scriptPath + '" /l en-US'
                Start-Process -FilePath $console -ArgumentList $arguments `
                    -RedirectStandardInput $stdin -RedirectStandardOutput $logPath `
                    -RedirectStandardError ($logPath + '.err') -NoNewWindow -Wait | Out-Null
            }

            $xrefScript = Join-Path $scratch 'make-xref.scr'
            $xrefLines = @(
                '(setvar "CTAB" "Model")',
                '(command "_.ERASE" "_ALL" "")',
                '(setvar "CTAB" "Layout1")',
                '(command "_.ERASE" "_ALL" "")',
                '(setvar "CTAB" "Model")',
                '(entmake (list (cons 0 "LINE") (cons 100 "AcDbEntity") (cons 100 "AcDbLine") (cons 8 "0") (cons 10 (list 0.0 0.0 0.0)) (cons 11 (list 5.0 0.0 0.0))))',
                '(command "_.QSAVE")'
            )
            [System.IO.File]::WriteAllText(
                $xrefScript, (($xrefLines -join "`r`n") + "`r`n"),
                (New-Object System.Text.UTF8Encoding($false)))

            # entmake with a 410 group places the entity in the named layout, independent
            # of what is visible in the editor. The 410 value is the current tab name.
            $editScript = Join-Path $scratch 'make.scr'
            # Ten probe objects in one drawing:
            #   TEXT    - paper space is traversed (section 4)
            #   3DFACE  - an unsupported class is counted, not silently dropped (section 6).
            #             It is deliberately not a Curve: the Curve fallback now supports
            #             rays, xlines, ellipses, splines and 2D/3D polylines.
            #   INSERT of CONSTPROBE - a constant attribute is read from the definition
            #             (section 6). Constant attributes are never copied onto the
            #             reference, so only a definition read can find this value.
            #   INSERT of NESTEDPROBE - block geometry is traversed only when nested
            #             harvesting is on, and its line/circle arrive with the insertion
            #             transform applied and a Container path (section 4b).
            #   XREF of xref-source - the block row says IsXref=1 immediately; the external
            #             line becomes visible only when nested harvesting resolves the xref
            #             (section 4c).
            #   ELLIPSE, closed LWPOLYLINE, HATCH on the AREAPROBE layer - area takeoff,
            #             which the Curve fallback and the hatch extractor supply (section 4d).
            $lines = @(
                '(setvar "CTAB" "Model")',
                '(entmake (list (cons 0 "BLOCK") (cons 2 "NESTEDPROBE") (cons 70 0) (cons 10 (list 0.0 0.0 0.0))))',
                '(entmake (list (cons 0 "LINE") (cons 100 "AcDbEntity") (cons 100 "AcDbLine") (cons 8 "0") (cons 10 (list 0.0 0.0 0.0)) (cons 11 (list 10.0 0.0 0.0))))',
                '(entmake (list (cons 0 "CIRCLE") (cons 100 "AcDbEntity") (cons 100 "AcDbCircle") (cons 8 "0") (cons 10 (list 5.0 5.0 0.0)) (cons 40 2.0)))',
                '(entmake (list (cons 0 "ENDBLK")))',
                '(entmake (list (cons 0 "INSERT") (cons 8 "0") (cons 2 "NESTEDPROBE") (cons 10 (list 100.0 100.0 0.0)) (cons 41 2.0) (cons 42 2.0) (cons 43 2.0)))',
                ('(command "_.-XREF" "_A" "' + ($xrefSource -replace '\\', '/') + '" (list 200.0 200.0 0.0) 1.0 1.0 0.0)'),
                '(command "_.-LAYER" "_M" "AREAPROBE" "")',
                '(setvar "CLAYER" "AREAPROBE")',
                '(entmake (list (cons 0 "ELLIPSE") (cons 100 "AcDbEntity") (cons 100 "AcDbEllipse") (cons 8 "AREAPROBE") (cons 10 (list 300.0 100.0 0.0)) (cons 11 (list 10.0 0.0 0.0)) (cons 40 0.5) (cons 41 0.0) (cons 42 (* 2.0 pi))))',
                '(entmake (list (cons 0 "LWPOLYLINE") (cons 100 "AcDbEntity") (cons 100 "AcDbPolyline") (cons 8 "AREAPROBE") (cons 90 4) (cons 70 1) (cons 10 (list 300.0 300.0)) (cons 10 (list 310.0 300.0)) (cons 10 (list 310.0 310.0)) (cons 10 (list 300.0 310.0))))',
                '(setq areaprobe (entlast))',
                '(command "_.-HATCH" "_S" areaprobe "" "")',
                '(setvar "CTAB" "Layout1")',
                '(entmake (list (cons 0 "TEXT") (cons 8 "0") (cons 10 (list 5.0 5.0 0.0)) (cons 40 0.25) (cons 1 "PAPER-SPACE-PROBE") (cons 410 (getvar "CTAB"))))',
                '(entmake (list (cons 0 "3DFACE") (cons 100 "AcDbEntity") (cons 100 "AcDbFace") (cons 8 "0") (cons 10 (list 0.0 0.0 0.0)) (cons 11 (list 1.0 0.0 0.0)) (cons 12 (list 1.0 1.0 0.0)) (cons 13 (list 0.0 1.0 0.0)) (cons 410 (getvar "CTAB"))))',
                '(entmake (list (cons 0 "BLOCK") (cons 2 "CONSTPROBE") (cons 70 0) (cons 10 (list 0.0 0.0 0.0))))',
                '(entmake (list (cons 0 "ATTDEF") (cons 8 "0") (cons 10 (list 0.0 0.0 0.0)) (cons 40 0.25) (cons 1 "CONST-VALUE") (cons 2 "CTAG") (cons 3 "constant probe") (cons 70 2)))',
                '(entmake (list (cons 0 "ENDBLK")))',
                '(entmake (list (cons 0 "INSERT") (cons 8 "0") (cons 2 "CONSTPROBE") (cons 10 (list 10.0 10.0 0.0)) (cons 410 (getvar "CTAB"))))',
                '(command "_.QSAVE")'
            )
            [System.IO.File]::WriteAllText(
                $editScript, (($lines -join "`r`n") + "`r`n"),
                (New-Object System.Text.UTF8Encoding($false)))

            $editLog = Join-Path $scratch 'edit.log'

            $previousEap = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                & $runCore $xrefSource $xrefScript (Join-Path $scratch 'xref.log')
                & $runCore $fixture $editScript $editLog
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
            # name the schema, and did it count the 3DFACE the registry cannot extract?
            $verdictPath = Join-Path $probeOutput '_status\Paper Probe-11223344.ok'
            $verdict = $null
            if (Test-Path $verdictPath) {
                try { $verdict = [System.IO.File]::ReadAllText($verdictPath) | ConvertFrom-Json } catch { }
            }

            $verdictSchema = $null
            if ($verdict) { $verdictSchema = $verdict.schema }
            Check ($null -ne $verdictSchema -and [int]$verdictSchema -eq $SchemaVersion) `
                ("the verdict names the schema the engine wrote ({0}, expected {1})" -f $verdictSchema, $SchemaVersion)

            $faceCount = 0
            if ($verdict -and $verdict.unsupported) { $faceCount = [int]$verdict.unsupported.'3DFACE' }
            Check ($faceCount -eq 1) ("an unsupported 3DFACE is counted by class (found {0})" -f $faceCount)

            $probeAttrs = Join-Path $probeOutput '_rows\Paper Probe-11223344.attributes.csv'
            $constant = @()
            if (Test-Path $probeAttrs) {
                $constant = @(Import-Csv $probeAttrs |
                    Where-Object { $_.Tag -eq 'CTAG' -and $_.Value -eq 'CONST-VALUE' -and $_.IsConstant -eq '1' })
            }
            Check ($constant.Count -eq 1) `
                ("a constant attribute is read from the definition (found {0})" -f $constant.Count)

            # ------------------------------------------------ 4b nested traversal
            # The default run must not grow rows because a block contains geometry:
            # recursion is opt-in and off unless AEC_HARVEST_NESTED is exactly "1".
            $fixtureRows = @(Import-Csv $probeRows)
            $nestedParent = @($fixtureRows |
                Where-Object { $_.EntityType -eq 'Block' -and $_.Name -eq 'NESTEDPROBE' })
            Check ($nestedParent.Count -eq 1) `
                ("the nested probe block is placed once (found {0})" -f $nestedParent.Count)

            $unexpectedNested = @($fixtureRows | Where-Object { $_.Container -like 'NESTEDPROBE@*' })
            Check ($unexpectedNested.Count -eq 0) `
                ("nested rows stay out of a default run (found {0})" -f $unexpectedNested.Count)

            # ---------------------------------------------- 4c xref marking
            # Attaching an xref creates a block reference whose definition is external.
            # The row can say so without opening the other file, and its children must
            # stay out of a default run just like local block geometry.
            $xrefBlock = @($fixtureRows |
                Where-Object { $_.EntityType -eq 'Block' -and $_.Name -eq 'xref-source' })
            Check ($xrefBlock.Count -eq 1) `
                ("the xref is one block row (found {0})" -f $xrefBlock.Count)
            if ($xrefBlock.Count -eq 1) {
                Check ($xrefBlock[0].IsXref -eq '1') `
                    ("the xref row is marked IsXref=1 (found {0})" -f $xrefBlock[0].IsXref)
                Check ($xrefBlock[0].Depth -eq '0') `
                    ("the xref attachment itself is depth 0 (found {0})" -f $xrefBlock[0].Depth)
                Check ($xrefBlock[0].X -eq '200.000' -and $xrefBlock[0].Y -eq '200.000') `
                    ("the xref insertion point is reported ({0},{1})" -f $xrefBlock[0].X, $xrefBlock[0].Y)
            }

            $xrefDefaultChildren = @($fixtureRows | Where-Object { $_.Container -like 'xref-source@*' })
            Check ($xrefDefaultChildren.Count -eq 0) `
                ("xref contents stay out of a default run (found {0})" -f $xrefDefaultChildren.Count)

            # The second harvest sets the opt-in switch and must report the block's line
            # and circle, transformed into drawing coordinates and labelled with the
            # placement that contains them. The default results are already loaded above,
            # so the same output tree is cleared and re-harvested in place.
            Remove-Item (Join-Path $probeOutput '_rows') -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item (Join-Path $probeOutput '_status') -Recurse -Force -ErrorAction SilentlyContinue

            $env:AEC_HARVEST_NESTED = '1'
            try {
                $nestedOut = & powershell -NoProfile -ExecutionPolicy Bypass -File `
                    (Join-Path $Root 'tools\run-harvest-batch.ps1') `
                    -Root $scratch -Manifest $manifest -Script $probeScript `
                    -Console $console 2>&1 | Out-String
                $nestedCode = $LASTEXITCODE
            }
            finally {
                Remove-Item Env:AEC_HARVEST_NESTED -ErrorAction SilentlyContinue
            }

            Check ($nestedCode -eq 0) "the nested fixture is harvested (exit was $nestedCode)"

            $nestedRows = @()
            if (Test-Path $probeRows) { $nestedRows = @(Import-Csv $probeRows) }

            $expectedContainer = ''
            if ($nestedParent.Count -eq 1) {
                $expectedContainer = 'NESTEDPROBE@' + $nestedParent[0].Handle
            }

            $nestedLines = @()
            $nestedCircles = @()
            if ($expectedContainer) {
                $nestedLines = @($nestedRows | Where-Object {
                    $_.EntityType -eq 'Line' -and $_.Container -eq $expectedContainer -and $_.Depth -eq '1' })
                $nestedCircles = @($nestedRows | Where-Object {
                    $_.EntityType -eq 'Circle' -and $_.Container -eq $expectedContainer -and $_.Depth -eq '1' })
            }

            Check ($nestedLines.Count -eq 1) `
                ("the block's line is harvested once (found {0})" -f $nestedLines.Count)
            Check ($nestedCircles.Count -eq 1) `
                ("the block's circle is harvested once (found {0})" -f $nestedCircles.Count)

            if ($nestedLines.Count -eq 1) {
                $nestedLine = $nestedLines[0]
                Check ($nestedLine.X -eq '100.000' -and $nestedLine.Y -eq '100.000') `
                    ("the insertion transform moved the line to 100,100 ({0},{1})" -f $nestedLine.X, $nestedLine.Y)
                Check ($nestedLine.Length -eq '20.000') `
                    ("the block scale of 2 doubled the line length ({0})" -f $nestedLine.Length)
                Check ($nestedLine.IsXref -eq '0') `
                    ("a local block is not marked as an xref (found {0})" -f $nestedLine.IsXref)
            }

            if ($nestedCircles.Count -eq 1) {
                $nestedCircle = $nestedCircles[0]
                Check ($nestedCircle.X -eq '110.000' -and $nestedCircle.Y -eq '110.000') `
                    ("the circle centre is transformed ({0},{1})" -f $nestedCircle.X, $nestedCircle.Y)
                $circumference = 0.0
                [void][double]::TryParse(
                    $nestedCircle.Length,
                    [System.Globalization.NumberStyles]::Float,
                    [System.Globalization.CultureInfo]::InvariantCulture,
                    [ref]$circumference)
                Check ([Math]::Abs($circumference - 25.133) -lt 0.002) `
                    ("the circle circumference is scaled ({0})" -f $nestedCircle.Length)
            }

            # ---------------------------------------------- 4c xref traversal
            # The external drawing's line is not in this database until the xrefs are
            # resolved, which only the nested run does. Once resolved it must arrive with
            # the xref's insertion transform, the xref in its Container, and IsXref=1.
            $xrefContainer = ''
            if ($xrefBlock.Count -eq 1) {
                $xrefContainer = 'xref-source@' + $xrefBlock[0].Handle
            }

            $xrefNestedLines = @()
            if ($xrefContainer) {
                $xrefNestedLines = @($nestedRows | Where-Object {
                    $_.EntityType -eq 'Line' -and $_.Container -eq $xrefContainer -and $_.Depth -eq '1' })
            }

            Check ($xrefNestedLines.Count -eq 1) `
                ("the xref's line is harvested once when resolved (found {0})" -f $xrefNestedLines.Count)
            if ($xrefNestedLines.Count -eq 1) {
                $xrefLine = $xrefNestedLines[0]
                Check ($xrefLine.IsXref -eq '1') `
                    ("xref children are marked IsXref=1 (found {0})" -f $xrefLine.IsXref)
                Check ($xrefLine.X -eq '200.000' -and $xrefLine.Y -eq '200.000') `
                    ("the xref insertion transform moved the line ({0},{1})" -f $xrefLine.X, $xrefLine.Y)
                Check ($xrefLine.Length -eq '5.000') `
                    ("the xref line keeps its own length ({0})" -f $xrefLine.Length)
            }

            # ---------------------------------------------- 4d area takeoff
            # The fallback curve extractor has to produce a real number, not just a row:
            # a full ellipse with semi-axes 10 and 5 has area pi*10*5 = 157.0796.
            $areaRows = @($fixtureRows | Where-Object { $_.Layer -eq 'AREAPROBE' })
            $areaEllipse = @($areaRows | Where-Object { $_.EntityType -eq 'Ellipse' })
            $areaSquare = @($areaRows | Where-Object { $_.EntityType -eq 'Polyline' })
            $areaHatch = @($areaRows | Where-Object { $_.EntityType -eq 'Hatch' })

            Check ($areaEllipse.Count -eq 1) `
                ("the fallback curve extractor reports the ellipse (found {0})" -f $areaEllipse.Count)
            Check ($areaSquare.Count -eq 1) `
                ("the closed polyline is one row (found {0})" -f $areaSquare.Count)
            Check ($areaHatch.Count -eq 1) `
                ("the hatch is one row (found {0})" -f $areaHatch.Count)

            if ($areaEllipse.Count -eq 1) {
                $ellipseArea = 0.0
                [void][double]::TryParse(
                    $areaEllipse[0].Area,
                    [System.Globalization.NumberStyles]::Float,
                    [System.Globalization.CultureInfo]::InvariantCulture,
                    [ref]$ellipseArea)
                Check ([Math]::Abs($ellipseArea - 157.080) -lt 0.002) `
                    ("the ellipse area is pi*10*5 ({0})" -f $areaEllipse[0].Area)
            }

            if ($areaSquare.Count -eq 1) {
                Check ($areaSquare[0].Area -eq '100.000') `
                    ("a closed 10x10 polyline has area 100 ({0})" -f $areaSquare[0].Area)
                Check ($areaSquare[0].Length -eq '40.000') `
                    ("and its perimeter is still 40 ({0})" -f $areaSquare[0].Length)
            }

            if ($areaHatch.Count -eq 1) {
                Check ($areaHatch[0].Area -eq '100.000') `
                    ("the hatch over the 10x10 square has area 100 ({0})" -f $areaHatch[0].Area)
                Check ($areaHatch[0].Length -eq '') `
                    ("a hatch has no length ({0})" -f $areaHatch[0].Length)
            }

            if ($found.Count -eq 0 -or $nestedLines.Count -eq 0 -or $xrefNestedLines.Count -eq 0 `
                -or $areaHatch.Count -eq 0) {
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
