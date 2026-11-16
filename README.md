# AEC Batch Data Harvester

[![ci](https://github.com/MahmmoudElkayal/aec-batch-data-harvester/actions/workflows/ci.yml/badge.svg)](https://github.com/MahmmoudElkayal/aec-batch-data-harvester/actions/workflows/ci.yml)

Headless batch data extraction for AutoCAD 2026 drawings. Point it at a folder of DWGs and
get two CSVs: one row per entity (blocks, polylines, lines, arcs, circles, text, solids) and
one row per block attribute. No AutoCAD window, no dialogs, and no modification of the
source files.

Built as an AutoCAD 2026 managed .NET plug-in that runs both inside the desktop product
(`NETLOAD` + `Extract_BOM`) and inside **AutoCAD Core Console** (`accoreconsole.exe`), which
is what makes unattended batch processing possible.

> **Status: working end-to-end prototype.** Verified against 3 sample drawings:
> **6,748 entity rows and 219 attribute rows, 0 duplicates, 39–77 ms of engine time per
> drawing.** Every entity type in the corpus census is extracted. Nested blocks and xref
> contents are available behind `AEC_HARVEST_NESTED=1`; curves without a named extractor and
> hatches report area. AEC vertical objects require that vertical's managed assemblies; where
> they are absent they are reported as explained proxies, not silently dropped.

---

## What it does

| | |
|---|---|
| **Reads** | DWG files without opening them in the AutoCAD UI |
| **Finds** | block references (name, definition, layer, handle, insertion point, attributes), LWPOLYLINEs, closed-curve and hatch areas, lines, arcs, circles, DBText/MText, solids, ellipses/splines/2D-3D polylines through one curve fallback, in model and paper space |
| **Writes** | `Extracted_BOM.csv` (entities) and `Extracted_Attributes.csv` (long-format attributes) — RFC 4180, UTF-8 without BOM by default |
| **Never writes** | to the drawings themselves (`/readonly` + read-only transactions) |
| **Reports** | per-drawing success/failure, row and attribute counts, plus a per-drawing log |
| **Resumes** | `/append` skips drawings that already have rows *and* a verdict, and retries failures |

## Requirements

| Component | Version |
|---|---|
| AutoCAD | 2026 **Update 1.2+** (release 25.1, .NET 10). Verified on `25.1.179.0.0` |
| .NET SDK | 10.0 (to build) |
| OS | Windows x64 |
| Optional | ObjectARX 2026 SDK — not required; the three reference DLLs are taken from the AutoCAD install folder |

AutoCAD 2026 through 2026 Update 1.1 runs on .NET 8, not .NET 10. If you must support those
builds, multi-target `net8.0-windows;net10.0-windows`.

## Quick start

```bat
:: 1. build
dotnet build AEC_Batch_Data_Haravester.slnx

:: 2. drop DWGs into Input_DWGs\

:: 3. run
RunPipeline.bat
```

Results land in `Output\Extracted_BOM.csv` and `Output\Extracted_Attributes.csv`.

### Runner flags

```bat
RunPipeline.bat              :: harvest everything directly in Input_DWGs
RunPipeline.bat /r           :: ... including sub-folders
RunPipeline.bat /append      :: resume: keep finished drawings, retry failures
RunPipeline.bat /new         :: rebuild the DLL first
RunPipeline.bat /q           :: do not pause at the end
```

`/append` is a resume, not an append-to-one-file: finished per-drawing results stay, and
only drawings without a complete result are harvested again. The merged CSV is always
rebuilt from the per-drawing files, so it never contains a drawing twice.

### Environment switches

| Variable | Effect |
|---|---|
| `AEC_HARVEST_CHUNK` | drawings per Core Console process (default 50) |
| `AEC_HARVEST_TIMEOUT` | seconds per chunk before the process is killed (default 300) |
| `AEC_HARVEST_PARALLEL` | chunk processes running at once (default 1, cap 16). Each chunk is a full AutoCAD process |
| `AEC_HARVEST_EXCEL_SAFE=1` | prefix text starting with `= + - @` or a tab with `'` so Excel does not treat it as a formula. Text columns only; numeric columns are untouched. Off by default because it changes real layer names |
| `AEC_HARVEST_UTF8_BOM=1` | write a UTF-8 BOM (plugin and merge) so Windows Excel decodes non-ASCII text correctly. Off by default because parsers do not want one |

Both switches are on only for the exact value `1`; `true` is treated as off, so a typo cannot
silently rewrite output.

The runner auto-detects the newest `Debug`/`Release` build, regenerates the Core Console
script so the `NETLOAD` path can never go stale, logs every drawing to `Logs\`, and prints a
processed / succeeded / failed summary.

### Running a single drawing by hand

```bat
"C:\Program Files\Autodesk\AutoCAD 2026\accoreconsole.exe" ^
    /i "Input_DWGs\MyDrawing.dwg" ^
    /s "Scripts\HarvestBatch.scr" ^
    /readonly  < nul
```

## Output schema

### `Output\Extracted_BOM.csv` — one row per entity

| Column | Meaning |
|---|---|
| `Drawing` | source DWG file name |
| `Space` | `Model` or `Paper` |
| `EntityType` | `Block`, `Polyline`, `Line`, `Arc`, `Circle`, `DBText`, `MText`, `Solid`, `Hatch`, or a fallback curve named after its DXF type (`Ellipse`, `Spline`, `Ray`, `Xline`, `Polyline`, …) |
| `Name` | block name; for `DBText`/`MText`, the text content |
| `BlockDefinition` | resolved definition name (blocks only; dynamic blocks resolve through `DynamicBlockTableRecord`) |
| `Layer` | object layer |
| `Handle` | DWG handle, for traceability back to the drawing |
| `Container` | block path that contains the row, `Name@Handle > Name@Handle`; blank at the top level |
| `Depth` | number of block references above the row; `0` at the top level |
| `IsXref` | `1` when the row is, or is inside, an external reference |
| `X` `Y` `Z` | insertion point / centre / start point, per type, in drawing coordinates |
| `Length` | curve length (polyline, line, arc, circle, fallback curves); blank where it has no meaning |
| `Area` | enclosed area for a closed polyline, closed curve or hatch; blank for open geometry |
| `Unit` `UnitCode` | drawing `INSUNITS`, on every row |

```csv
Drawing,Space,EntityType,Name,BlockDefinition,Layer,Handle,Container,Depth,IsXref,X,Y,Z,Length,Area,Unit,UnitCode
Assembly Sample.dwg,Model,Line,,,0,A47,,0,0,90.927,60.400,0.000,89.927,,Undefined,0
Floor Plan Sample.dwg,Model,Polyline,,,E-B-MULL,3A,,0,0,,,,14.500,11.250,Inches,1
Floor Plan Sample.dwg,Model,Block,DR-36,DR-36,E-F-DOOR,6C,,0,0,2827.000,1911.000,0.000,,,Inches,1
```

`Area` is the drawing's square unit (the square of `Unit`). It is filled only where the CAD
engine defines one: a closed LWPOLYLINE, a closed curve such as a full ellipse or closed
spline, and a HATCH. An open polyline is a route, not an enclosure, so its cell stays blank
rather than becoming `0.000`.

### Curves and hatches the named extractors do not cover

One fallback extractor is registered on `Curve`, so 2D/3D polylines, ellipses, splines, rays,
xlines and helices are harvested without an extractor each. The registry walks from the
entity's class upward and stops at the first match, which is why `Line`, `Arc`, `Circle` and
LWPOLYLINE keep their specific rows. A ray or xline has no finite length and no area; those
cells stay blank while the row still records the object and its layer.

`Container`, `Depth` and `IsXref` describe where a row came from. They stay blank/`0` unless
nested traversal is on, so a default run's rows are unchanged by the column addition.

### Nested blocks and xrefs (opt-in)

Set `AEC_HARVEST_NESTED=1` to descend into block definitions. Every entity inside a
definition is reported with the insertion transform applied, so its point is a drawing
coordinate rather than a coordinate in the block's private space; lengths are scaled with the
transform as well. The ancestor chain is recorded, and the handle in it makes two placements of
the same inner definition distinguishable:

```csv
Drawing,Space,EntityType,Name,BlockDefinition,Layer,Handle,Container,Depth,IsXref,X,Y,Z,Length,Area,Unit,UnitCode
Plan.dwg,Model,Line,,,0,B21,Door@2A > Hinge@3B,2,0,101.500,99.000,0.000,4.000,,Millimeters,4
```

The switch is off by default because recursion multiplies rows on any drawing whose blocks
contain geometry, and because the same definition object is reached once per placement. A
self-referencing block cannot make the walk run forever: each path carries a visited set, and
the chain stops at eight levels.

Xrefs are marked even when traversal is off: `IsXref` comes from
`BlockTableRecord.IsFromExternalReference`, so the block row says it came from another file.
With `AEC_HARVEST_NESTED=1` the runner also calls `Database.ResolveXrefs`, because a side
database opened with `ReadDwgFile` has the xref record but none of its contents. If the xref
file is missing the resolution is skipped and the block row is still reported; the geometry is
simply unavailable. A row inside an xref carries `IsXref=1` and the xref in its `Container`.

### `Output\Extracted_Attributes.csv` — one row per block attribute

| Column | Meaning |
|---|---|
| `Drawing` | source DWG file name |
| `EntityHandle` | handle of the owning block reference — joins to `Extracted_BOM.csv` |
| `Container` | block path of the owning reference, same format as the entity file |
| `Tag` | attribute tag, verbatim (a tag of `#` occurs in the corpus) |
| `Value` | attribute text |
| `IsConstant` | `1` or `0`; constant values are read from the block definition |

```csv
Drawing,EntityHandle,Container,Tag,Value,IsConstant
Assembly Sample.dwg,12F5,,3,CE003902,0
```

Text and attribute values are written on one physical line: a raw newline is rendered as the
two characters `\n`, so the row count in the file, the status file and the supervisor agree.

Each drawing also gets `Output\_status\<key>.ok` or `.fail` containing a one-line JSON
verdict — this is what the runner uses for pass/fail, because Core Console's exit code is not
reliable. The status key is `Stem-<8 hex of SHA256(relative path)>`, so two `Plan.dwg` files
in different folders cannot collide. The verdict also carries `schema` and `tool`, and
`/append` refuses a verdict whose `schema` differs from the engine's: a drawing extracted
under older rules is re-run, never merged with new rows under a silent assumption.

### Coverage — what was *not* extracted

The CSV contains only what the registry knows how to read. The status file says what it does
not:

```json
"unsupported":{"VIEWPORT":2},"unsupportedTotal":2
```

`VIEWPORT`, `DIMENSION`, `ACAD_PROXY_ENTITY` and every other class without an extractor
are counted by DXF name, and the runner prints the corpus-wide totals as
`Skipped : VIEWPORT=8`. Without that line, a drawing full of unsupported geometry is
indistinguishable from an empty one. The sample corpus currently reports eight viewports
and nothing else.

A proxy is different from a class the harvester has not implemented: it is an object whose
defining application is missing (an AEC vertical, a third-party add-on), so its geometry and
property data do not exist in the process at all. When the census sees one, the runner adds a
separate line naming the cause and both fixes:

```
Skipped : ACAD_PROXY_ENTITY=12
Proxies : 12 object(s) arrived as ACAD_PROXY_ENTITY.
          AEC verticals (Architecture, MEP, Civil 3D) store objects in their own
          classes. Without that vertical installed here, AutoCAD cannot read them:
          no geometry, no property sets, no schedules. Harvest the drawing where
          the vertical is installed, or export it to plain AutoCAD objects first.
```

## How it works

```
RunPipeline.bat                         supervisor
   ├─ build-manifest.ps1                key|path, one line per drawing
   ├─ up to AEC_HARVEST_PARALLEL chunk processes at once
   │    └─ for each chunk of AEC_HARVEST_CHUNK drawings
   │         └─ accoreconsole.exe /i <first> /s <generated .scr> /readonly
   │              └─ HarvestBatch     (one process, many side databases)
   │                   ├─ read the chunk manifest
   │                   ├─ for each drawing: open as a side database
   │                   │    ├─ read-only Transaction
   │                   │    ├─ model space, then every paper layout
   │                   │    ├─ registry → extractor → entity rows + attribute rows
   │                   │    └─ temp-then-rename both per-drawing files
   │                   └─ write .ok only after the files exist
   ├─ kill a chunk on AEC_HARVEST_TIMEOUT; mark its in-flight drawing .fail
   ├─ combine the per-chunk _batch-<id>.json into _batch.json
   └─ merge-rows.ps1                    only drawings with .ok, sorted, byte-identical
```

Chunking exists because AutoCAD startup costs ~1.6 s while the work is ~40–80 ms per drawing:
opening side databases inside one session amortises the startup. The process boundary stays
as the isolation layer, so a crash costs at most one chunk, and the per-drawing results
already published are not lost. Parallel chunks are the same idea one level up: the three-
drawing corpus takes 6.9 s serially and 2.8 s with three chunks, because the cost is startup
and the drawings do not share state.

Two chunks cannot share the fixed-name files that describe a process. Each child is given
`AEC_HARVEST_CHUNK_ID` and writes `_status\_current-<id>.txt`, `_status\_batch-<id>.json`
and `_status\_harvest-<id>.error.txt`; the supervisor reads the marker matching the chunk it
killed and writes the combined `_batch.json`. Per-drawing outputs were already keyed by the
drawing, so they needed no change.

## Design notes (things that are easy to get wrong)

These were found by testing, not by reading the docs:

1. **A trailing blank line in a `.scr` repeats the last command.** A bare `Enter` at the
   `Command:` prompt re-runs the previous command, so the harvest command executed **twice**
   per drawing — exactly 2× the rows. The script must end without a blank line, and Core
   Console must be given `stdin` from `NUL` so it does not wait for input forever.
2. **Core Console swallows managed `Console.WriteLine`.** Its own banner reads
   `StdOutConsoleMode: processed-output: disabled`, and writes from .NET never reach the log.
   Hence the status files.
3. **`SECURELOAD` blocks `NETLOAD`** from any folder not in `TRUSTEDPATHS`. Set `SECURELOAD 0`
   in the script for development; for production put the DLL in a trusted path instead.
4. **`/readonly`** — batch harvesting should never be able to modify a source drawing.
5. **Never reference `AcMgd.dll`** in code that must run in Core Console, and always set
   `Copy Local = false` on `accoremgd` / `acdbmgd` / `acmgd` or AutoCAD crashes on load.
6. **A side-database drawing has no `Document`.** Layout enumeration goes through the
   `BlockTable` (`IsLayout`), not `LayoutManager`, which is why paper space works in a batch.
7. **One bad drawing must be one `.fail`.** The engine catches per drawing; the supervisor
   kills a hung chunk and blames the drawing named in that chunk's `_status\_current-<id>.txt`,
   then resume runs only what did not report.
8. **A constant attribute is not on the block reference.** AutoCAD creates no `Attribute`
   for it — the definition is used for every insertion (bit 2 of DXF group 70). Reading only
   `BlockReference.AttributeCollection` drops every constant value; the definition's
   `AttributeDefinition`s with `Constant = true` must be read too.
9. **`MText.Text`, not `MText.Contents`.** `Contents` carries the formatting codes; `Text`
   is documented as "the contents with formatting removed".
10. **The engine learns its output folder from the environment, not from `-Root`.** The
    supervisor computes `-Root\Output` itself, so it must also pass `AEC_HARVEST_ROOT` and
    `AEC_HARVEST_OUTPUT` into the Core Console process. Without that, the engine falls back
    to a folder beside the plug-in DLL and the supervisor waits forever for status files
    that were written somewhere else - every drawing reported as "no status file" even
    though each one had been harvested.

## Repository layout

```
AEC_Batch_Data_Haravester.slnx          solution
AEC_Batch_Data_Haravester.Core/         the AutoCAD-free half (net10.0, no Autodesk reference)
    Core/                               rows, options, manifest, timeout verdict
    Output/                             CSV schemas, escaping, temp-then-rename sinks
    Reporting/                          status JSON, per-drawing files, batch summary
AEC_Batch_Data_Haravester/              the plug-in (net10.0-windows)
    Commands/                           Extract_BOM and HarvestBatch entry points
    Extraction/                         scanner, extractors, runners (AutoCAD only)
RunPipeline.bat                         supervisor (entry point)
Scripts/HarvestBatch.scr                Core Console script (auto-generated)
Input_DWGs/                             drop drawings here        (git-ignored)
Output/                                 CSVs, _rows, _status      (git-ignored)
Logs/                                   per-chunk logs            (git-ignored)
tests/Harvest.Checks/                   dependency-free unit checks
tests/golden/                           frozen output baseline
tools/                                  manifest, batch supervisor, merge, golden/checks
.github/workflows/ci.yml                builds the core + checks on a hosted runner
docs/autocad-2026/                      study of the ObjectARX 2026 documentation set, plus
                                        harvest-api-research.md (sources behind each gap)
```

`docs/autocad-2026/README.md` is a full map of the AutoCAD 2026 / ObjectARX documentation:
45,844 pages indexed, 29 managed namespaces → 18,925 reference pages, plus platform and
execution-host constraints.

## Tests

```powershell
dotnet build AEC_Batch_Data_Haravester.slnx
tests\Harvest.Checks\bin\Debug\net10.0\Harvest.Checks.exe

powershell -File tools\test-batch-engine.ps1      # throughput, isolation, resume, timeout
powershell -File tools\test-batch-integrity.ps1   # determinism, keys, .ok merge gate
powershell -File tools\test-failure-paths.ps1     # unreadable DWG, unwritable output, recovery
powershell -File tools\test-coverage.ps1          # census, attributes, join, paper space
powershell -File tools\capture-golden.ps1 -SkipRun -Check
```

The first two commands are also what CI runs. The workflow builds only
`tests/Harvest.Checks`, never the solution, because the plug-in needs AutoCAD's managed
assemblies; the checks reference `AEC_Batch_Data_Haravester.Core` alone, so a hosted
`windows-latest` runner can prove the schema, the status contract and the CSV escaping
without AutoCAD installed. CI additionally parses every PowerShell tool and runs the
manifest key self-test.

## Roadmap

The short version:

1. **Dynamic-block fixture** — the read path is correct; a fixture needs one desktop-authored
   dynamic block (the block editor is the only way to create the parameters)
2. **AEC verticals** — property sets and spaces need `AecBaseMgd`, an install/licensing
   decision rather than code; missing verticals are now explained as proxies
3. **Structured side output** — JSON/parquet for tooling

Shipped since the first list: nested blocks and xrefs, parallel chunks, the single `Curve`
fallback and `Area`, and CI over the AutoCAD-free core.

## Branches

Merged feature branches are kept permanently. `feat/*` and `fix/*` stay on `origin` after
they are merged, as the record of how each change was built; nothing prunes them and
GitHub's "automatically delete head branches" setting is off. Do not delete them.

## License

MIT — see [LICENSE](LICENSE).
