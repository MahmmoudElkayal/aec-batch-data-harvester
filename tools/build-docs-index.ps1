<#
    build-docs-index.ps1
    --------------------
    Downloads the official Autodesk ObjectARX 2026 (ENU) table of contents and
    generates a local, greppable index of the documentation set.

    Source: https://help.autodesk.com/view/OARX/2026/ENU/
            https://help.autodesk.com/view/OARX/2026/ENU/data/toctree.json

    Outputs (docs/autocad-2026/):
      objectarx-toc.csv              - every page, flat (searchable)
      managed-net-dev-guide.md       - Managed .NET Developer's Guide tree
      objectarx-dev-guide.md         - ObjectARX (C++) Developer's Guide tree
      managed-net-reference-index.md - .NET namespaces -> types
      namespaces.csv                 - namespace inventory
#>
[CmdletBinding()]
param(
    [string]$Lang    = 'ENU',
    [string]$Version = '2026',
    [string]$OutDir  = '',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $OutDir) { $OutDir = Join-Path $scriptDir '..\docs\autocad-2026' }

$baseUrl = "https://help.autodesk.com/view/OARX/$Version/$Lang"
$tocUrl  = "$baseUrl/data/toctree.json"

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$rawPath = Join-Path $OutDir '_toctree.raw.json'
if ($Force -or -not (Test-Path $rawPath)) {
    Write-Host "Downloading $tocUrl ..."
    Invoke-WebRequest -Uri $tocUrl -OutFile $rawPath -UseBasicParsing
}
Write-Host ("TOC size: {0:n0} bytes" -f (Get-Item $rawPath).Length)

$toc = Get-Content $rawPath -Raw | ConvertFrom-Json

# --- helpers ---------------------------------------------------------------
function Get-ViewerUrl([object]$Node) {
    if ($Node.id) { return "$baseUrl/?guid=$($Node.id)" }
    if ($Node.ln) { return "https://help.autodesk.com$($Node.ln)" }
    return $baseUrl
}

function Get-PageCount([object]$Node) {
    if (-not $Node.children) { return 0 }
    $n = 0
    foreach ($c in $Node.children) { $n += 1 + (Get-PageCount $c) }
    return $n
}

function New-TreeMarkdown {
    param(
        [object[]]$Roots,
        [string]$Header,
        [string]$Path,
        [int]$MaxDepth = 99
    )

    $script:sb = [System.Text.StringBuilder]::new()
    function Add-Node([object]$n, [int]$depth, [int]$maxDepth) {
        $pad   = '    ' * $depth
        $title = if ($n.ttl) { $n.ttl } else { '(untitled)' }
        $url   = Get-ViewerUrl $n
        $pages = if ($n.children) { Get-PageCount $n } else { 0 }
        $note  = if ($pages -gt 0 -and $depth -le 1) { "  _($pages pages)_" } else { '' }
        [void]$script:sb.AppendLine("$pad- [$title]($url)$note")
        if ($n.children -and $depth -lt $maxDepth) {
            foreach ($c in $n.children) { Add-Node $c ($depth + 1) $maxDepth }
        }
    }

    [void]$script:sb.AppendLine($Header)
    foreach ($r in $Roots) { Add-Node $r 0 $MaxDepth }
    [void]$script:sb.AppendLine()
    [void]$script:sb.AppendLine("_Generated $(Get-Date -Format 'yyyy-MM-dd HH:mm') from $tocUrl._")

    Set-Content -Path $Path -Value $script:sb.ToString() -Encoding utf8
    Write-Host ("  -> {0} ({1:n0} lines)" -f (Split-Path $Path -Leaf), $script:sb.Length)
}

# --- locate the ObjectARX book --------------------------------------------
$book = $toc.books | Where-Object { $_.id -eq 'OARX-Header_id' }
if (-not $book) { throw "ObjectARX book not found in TOC." }

$netDev  = $book.children | Where-Object { $_.ttl -like '*Managed .NET Developer*' }
$devGuide= $book.children | Where-Object { $_.ttl -like 'ObjectARX: Developer*' }
$netRef  = $book.children | Where-Object { $_.ttl -like '*Managed .NET Reference*' }

# --- 1. flat CSV of everything -------------------------------------------
Write-Host 'Building flat CSV index...'
$rows = [System.Collections.Generic.List[object]]::new()
function Add-Rows([object]$n, [int]$depth, [string]$parent, [string]$chapter) {
    $title = if ($n.ttl) { $n.ttl } else { '(untitled)' }
    $rows.Add([pscustomobject]@{
        Book   = $chapter
        Depth  = $depth
        Title  = $title
        Id     = $n.id
        Url    = (Get-ViewerUrl $n)
        Source = $n.ln
        Parent = $parent
    })
    if ($n.children) { foreach ($c in $n.children) { Add-Rows $c ($depth + 1) $title $chapter } }
}
foreach ($c in $book.children) {
    $chapterName = if ($c.ttl) { $c.ttl } else { '(untitled)' }
    Add-Rows $c 0 '' $chapterName
}
$rows | Export-Csv -Path (Join-Path $OutDir 'objectarx-toc.csv') -NoTypeInformation -Encoding utf8
Write-Host ("  -> objectarx-toc.csv ({0:n0} rows)" -f $rows.Count)

# --- 2. Managed .NET Developer's Guide -----------------------------------
if ($netDev) {
    $header = @(
        "# Managed .NET Developer's Guide (AutoCAD $Version)"
        ''
        "Source: $baseUrl/?guid=GUID-C3F3C736-40CF-44A0-9210-55F6A939B6F2"
        ''
        'Auto-generated outline of every page in the Managed .NET Developer''s Guide.'
        ''
    ) -join "`r`n"
    New-TreeMarkdown -Roots $netDev.children -Header $header -Path (Join-Path $OutDir 'managed-net-dev-guide.md') -MaxDepth 4
}

# --- 3. ObjectARX (C++) Developer's Guide --------------------------------
if ($devGuide) {
    $header = @(
        "# ObjectARX Developer's Guide (C++, AutoCAD $Version)"
        ''
        "Source: $baseUrl/?guid=GUID-265AADB3-FB89-4D34-AA9D-6ADF70FF7D4B"
        ''
        'Auto-generated outline (2 levels) of the native ObjectARX Developer''s Guide.'
        ''
    ) -join "`r`n"
    New-TreeMarkdown -Roots $devGuide.children -Header $header -Path (Join-Path $OutDir 'objectarx-dev-guide.md') -MaxDepth 2
}

# --- 4. Managed .NET reference index (namespaces -> types) ----------------
if ($netRef) {
    Write-Host 'Building Managed .NET reference index...'
    $sb2 = [System.Text.StringBuilder]::new()
    [void]$sb2.AppendLine("# Managed .NET Reference Guide - Namespaces and Types (AutoCAD $Version)")
    [void]$sb2.AppendLine()
    [void]$sb2.AppendLine("Source: $baseUrl/?guid=OARX-ManagedRefGuide-ObjectARX_Reference_Guide")
    [void]$sb2.AppendLine()
    [void]$sb2.AppendLine('Every managed namespace with its top-level types. Use `objectarx-toc.csv` for members.')
    [void]$sb2.AppendLine()
    foreach ($ns in $netRef.children) {
        if ($ns.ttl -notlike '*Namespace*') { continue }
        $types = if ($ns.children) { $ns.children.Count } else { 0 }
        [void]$sb2.AppendLine("## [$($ns.ttl)]($(Get-ViewerUrl $ns))")
        [void]$sb2.AppendLine()
        [void]$sb2.AppendLine("_$(Get-PageCount $ns) pages, $types top-level types._")
        [void]$sb2.AppendLine()
        if ($ns.children) {
            foreach ($t in $ns.children) {
                [void]$sb2.AppendLine("- [$($t.ttl)]($(Get-ViewerUrl $t))")
            }
        }
        [void]$sb2.AppendLine()
    }
    Set-Content -Path (Join-Path $OutDir 'managed-net-reference-index.md') -Value $sb2.ToString() -Encoding utf8
    Write-Host '  -> managed-net-reference-index.md'
}

# --- 5. namespace inventory ---------------------------------------------
Write-Host 'Building namespace inventory...'
$inv = [System.Collections.Generic.List[object]]::new()
if ($netRef -and $netRef.children) {
    foreach ($c in $netRef.children) {
        if ($c.ttl -like '*Namespace*') {
            $inv.Add([pscustomobject]@{
                Namespace = ($c.ttl -replace '\s+Namespace$', '')
                Pages     = (1 + (Get-PageCount $c))
                TopLevel  = if ($c.children) { $c.children.Count } else { 0 }
                Url       = (Get-ViewerUrl $c)
            })
        }
    }
}
$inv | Sort-Object Pages -Descending | Export-Csv -Path (Join-Path $OutDir 'namespaces.csv') -NoTypeInformation -Encoding utf8
Write-Host ("  -> namespaces.csv ({0} namespaces)" -f $inv.Count)

Write-Host ''
Write-Host 'Done. Outputs in:' $OutDir
