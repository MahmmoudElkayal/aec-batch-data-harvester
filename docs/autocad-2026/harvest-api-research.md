# Gap research: what the API and its documentation actually say

Each known gap in the harvester, the authoritative source consulted, the finding, and
whether the code changed. Checked 2026-11-16 against the AutoCAD managed reference
(`OARX-ManagedRefGuide`) and independent batch-processing write-ups.

The reference pages used below are the 2022 cloud-help member pages. They document the same
managed members that 2026 ships; where a member was renamed or added, the 2026 index in
`managed-net-reference-index.md` was used to confirm the name.

| # | Gap | Verdict | Code |
|---|---|---|---|
| 1 | Unsupported entities are invisible | No API reports "what you skipped"; the registry miss is the only signal | **Changed** |
| 2 | Constant attributes are never emitted | `AttributeDefinition.Constant` confirms no `Attribute` is created on insert | **Changed** |
| 3 | MText is exported with formatting | `MText.Text` already strips formatting | Already correct |
| 4 | Nested blocks and xrefs are invisible | `BlockTransform`, `IsFromExternalReference` and `Database.ResolveXrefs` cover the walk and the external contents | **Changed** |
| 5 | Schema is thin for takeoff | `Curve.Area`, `Hatch.Area`, `Polyline2d/3d : Curve` | **Changed** |
| 6 | `/append` merges two extraction rules | No built-in contract version; we must write one | **Changed** |
| 7 | Chunks run one at a time | Multiple `accoreconsole` processes are a documented pattern | Planned |
| 8 | No CI | The checks reference the AutoCAD plug-in, so cloud CI cannot build them | **Changed** |
| 9 | Dynamic blocks are untested | No API issue; the corpus has none | Planned |
| 10 | AEC property sets | Managed AEC assemblies are not installed here | Environment |

---

## 1. Unsupported entities — count them at the registry miss

**Source:** `RXClass.DxfName` (managed reference) — "Returns the DXF name for the class."

There is no API that says "this object was not extracted", because extraction is our
decision, not AutoCAD's. The only honest place to count is the branch that already decides
not to open the object: `DrawingScanner` asks the registry, gets `null`, and used to
`continue` silently. A proxy, a hatch and a dimension all took that path, so a drawing with
4,000 unsupported objects produced the same status line as an empty one.

**Change:** `Core/UnsupportedEntities.cs` counts by `RXClass.DxfName` (falling back to the
class name, and to `(unresolved)` when even the class cannot be read). `DrawingScanner`
feeds it from both model and paper space, `HarvestResult` carries the sorted dictionary,
`StatusJson` writes it as `unsupported` plus `unsupportedTotal`, and
`run-harvest-batch.ps1` sums it across the run and prints `Skipped : VIEWPORT=8, ...`.

Measured on the sample corpus: `VIEWPORT=8` across three drawings, which was previously
invisible.

## 2. Constant attributes — read the definition, not the reference

**Source:** [`AttributeDefinition.Constant`](https://help.autodesk.com/cloudhelp/2022/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_AttributeDefinition_Constant.html)
— "if bit 2 is set, then no Attribute will be created during insertion of the block
containing the AttributeDefinition. The AttributeDefinition will be used instead."

`BlockExtractor` walked `BlockReference.AttributeCollection`, which by that rule can never
contain a constant attribute. The `IsConstant` column was therefore always `0`, and every
constant value in every drawing was lost.

**Change:** `HarvestContext.ConstantAttributes(ObjectId)` reads the definition once per
layout (a door block placed forty times is read once), filters
`AttributeDefinition.Constant`, and `BlockExtractor` appends those rows with
`IsConstant: true` against the reference's handle so the join still works.

**Proof:** `tools/test-coverage.ps1` now builds a fixture block with a constant `ATTDEF`
and inserts it; the run must contain `CTAG = CONST-VALUE, IsConstant = 1`. It does.

## 3. MText — already correct, earlier claim withdrawn

**Sources:** [`MText.Text`](https://help.autodesk.com/cloudhelp/2022/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_MText_Text.html)
— "Accesses the MText contents with formatting removed"; and the practical write-up at
[benkoshy.github.io](https://benkoshy.github.io/2017/02/22/mtext-without-formatting.html),
"just use the .Text property".

`MTextExtractor` already used `text.Text`, not `text.Contents`. The golden CSV confirms it:
MText rows contain `Rudolph\nPolanski`, not `{\fArial|b0;Rudolph\PPolanski}`. No change was
needed; the earlier gap list was wrong on this point.

## 4. Nested blocks and xrefs — implemented

**Sources:** [`BlockReference.BlockTransform`](https://help.autodesk.com/cloudhelp/2022/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_BlockReference_BlockTransform.html)
— "the resulting MCS-to-drawing-WCS transform";
[`BlockTableRecord.IsFromExternalReference`](https://help.autodesk.com/cloudhelp/2022/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_BlockTableRecord_IsFromExternalReference.html)
— "Returns true if the block table record is describing an xref drawing"; and
[`Database.ResolveXrefs`](https://help.autodesk.com/cloudhelp/2023/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_Database_ResolveXrefs__MarshalAsUnmanagedType_U1__bool__MarshalAsUnmanagedType_U1__bool.html)
— "resolves existing xrefs in the working database … available to non-AutoCAD-based host
applications."

The walk is depth-first over the definition, with:
- a per-path visited `ObjectId` set and a depth cap of 8, because a block can contain itself;
- `BlockTransform` composed down the chain (`parent * child`) so a nested insertion point is
  reported in drawing coordinates, not in the block's microspace;
- `IsFromExternalReference` recorded on the row, so an xref block is not mistaken for a
  local one;
- recursion **off by default** and on by `AEC_HARVEST_NESTED=1`, because it multiplies rows
  and should be an explicit choice.

**Two findings changed the design.**

1. `Point3d.TransformBy(M)` is the column-vector product `M × P` ("Result is the same as
   setToProduct(leftSide, *this)"), so a child's transform is `parent * child`. The reversed
   order rotates nested geometry about the wrong origin.
2. A side database opened with `ReadDwgFile` contains the xref's `BlockTableRecord` but no
   children; the record resolves only when `Database.ResolveXrefs` is called. Without it the
   xref row is marked `IsXref=1` and its contents stay invisible. The scanner resolves xrefs
   only on the nested path, and a missing file is caught: the block row survives, its
   children are simply unavailable.

**Change:** `DrawingScanner` recurses and stamps `Container`/`Depth`/`IsXref` on every row
after extraction, so no extractor has to know about nesting; `HarvestContext` carries the
composed transform and the ancestor chain; `BlockInfo` centralises definition/xref lookup;
every point extractor transforms its point, and `CurveLength` measures a line from transformed
endpoints (exact under non-uniform scale) or scales other curves by `Matrix3d.GetScale()`.
Schema 3 adds the columns, and both CSVs carry `Container`.

**Proof:** the acceptance fixture places a block containing a line and a circle at
`(100,100)` with scale 2, and attaches a five-unit xref line at `(200,200)`. A default run
reports only the two block rows; the nested run reports the line at `100,100` with length
`20.000`, the circle centre at `110,110` with circumference `25.133`, and the xref line at
`200,200` with length `5.000`, `Container=xref-source@<handle>` and `IsXref=1`. The default
corpus still produces 6,748/219 rows; the schema-3 re-capture changed only the new columns.

## 5. Schema depth — implemented as one curve fallback

**Sources:** [`Curve.Area`](https://help.autodesk.com/cloudhelp/2022/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_Curve_Area.html)
("Accesses the area of the curve"),
[`Polyline2d`](https://help.autodesk.com/cloudhelp/2022/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_Polyline2d.html)
— "Inherits Curve", and
[`Hatch.Area`](https://help.autodesk.com/cloudhelp/2022/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_Hatch_Area.html).

The registry matches by inheritance, so one `CurveExtractor` registered on `Curve` catches
`Polyline2d`, `Polyline3d`, `Ellipse`, `Spline`, `Ray`, `Xline`, `Helix` and any future curve,
while the specific extractors keep their named rows. `Curve.Area` is defined for closed planar
curves and throws for open ones, so measurement goes through one failure policy that leaves the
cell blank instead of losing the row. A ray or xline has no finite length either; it is now a
harvested row with a blank length rather than an unsupported class.

**Two details the API forced.**

1. The row's `EntityType` is the class's `RXClass.DxfName` in CamelCase, so a fallback row says
   `Ellipse` or `Spline` instead of a generic `Curve`. A reader can act on that name.
2. `Polyline.Area` is defined even for an open polyline (where it is a meaningless signed
   value), so the polyline extractor reads area only when `Closed` is true. Otherwise the cell
   stays blank: an open polyline is a route, not an enclosure.

**Change:** `CurveExtractor` (on `Curve`), `HatchExtractor`, and area in `PolylineExtractor`;
`HarvestContext.AreaScale` is the magnitude of the cross product of the transformed X and Y
unit vectors, so a nested hatch's area is exact under a non-uniform block scale where squaring
the scalar scale would over-report. `CsvFormatter` adds `Area` after `Length`; schema becomes 4
and the supervisor gate moves with it.

**Proof:** the acceptance fixture creates a full ellipse with semi-axes 10 and 5, a closed
10×10 square LWPOLYLINE, and a HATCH over that square. The harvester reports ellipse area
`157.080` (π·10·5 = 157.0796), square area `100.000` with perimeter `40.000`, hatch area
`100.000` with a blank length. The default corpus keeps 6,748/219 rows; the schema-4 re-capture
filled `Area` on 362 closed polylines and left every other byte identical.

## 6. Resume must be schema-aware — implemented

There is no AutoCAD API for "is this result still valid"; it is a contract we own. The
status file is the contract (`README`, "the status file is authoritative"), so the version
belongs there.

**Change:** `Core/HarvestSchema.cs` defines `Version = 3` and the tool version;
`StatusJson` writes `schema` and `tool` into every verdict; `run-harvest-batch.ps1` refuses
an `.ok` whose `schema` differs, so `/append` re-harvests it instead of merging it.
`tools/test-coverage.ps1` reads both constants from source and fails if they drift apart.
Schema 2 added the unsupported census and constant attributes; schema 3 added the nested
provenance columns.

**Proof:** setting one verdict back to `schema:1` and running `RunPipeline.bat /append /q`
re-harvested exactly that drawing (`ms` 11 → 46) and left the other two untouched
(`ms` 80 and 54 unchanged).

## 7. Parallel chunks — implemented, one blocker removed first

**Sources:** [Autodesk's own write-up](https://blog.autodesk.io/parallel-aggregation-using-accoreconsole/)
("parallel aggregation can be used to launch multiple instances of AccoreConsole"), and a
[measured forum comparison](https://forums.autodesk.com/t5/net/accoreconsole-is-slower-in-2025/td-p/12826145)
showing `Parallel.ForEach` over Core Console instances beating a serial loop.

The engine is 39–77 ms per drawing while process start is ~1.6 s, so the win is real. The
blocker was ours: `run-harvest-batch.ps1` used one global `_status\_current.txt` to blame a
timeout, so concurrent chunks would overwrite each other's marker and misattribute the
failure. `_batch.json` and `_harvest.error.txt` had the same collision.

**Change:** each child process gets `AEC_HARVEST_CHUNK_ID` and writes
`_current-<id>.txt`, `_batch-<id>.json` and `_harvest-<id>.error.txt`
(`HarvestOptions.ChunkIdSuffix`, sanitised to file-name-safe characters). The supervisor's
loop became a slot set: `AEC_HARVEST_PARALLEL` chunks run at once (default 1, cap 16), a
freed slot takes the next pending chunk, and the supervisor writes the combined `_batch.json`
(`drawings`, `ok`, `failed`, `engineMs`, `perDrawingMs`, `parallel`, `chunks`) after every
chunk has finished. Per-drawing files were already keyed by the drawing and needed no change,
which is why the merged CSV is byte-identical to a serial run.

**Two supervisor bugs the parallel test exposed, both fixed.** First, `taskkill` writes its
diagnostics to stderr, and under `$ErrorActionPreference = 'Stop'` a redirected native stderr
line is a terminating error: the supervisor died mid-kill and the survivor published a `.ok`
after being overruled. The kill now runs in a child `cmd` that redirects its own streams.
Second, the deadline check ran one slot at a time, so handling a kill gave a neighbour time
to finish; deadlines are now evaluated for every slot first, and every expired tree is
killed before any of them is waited on.

**Proof:** the three-drawing corpus runs in 2.8 s wall with three chunks (6.9 s serially),
the merge matches the golden baseline byte for byte, each chunk writes its own summary, and
no blame marker survives. The acceptance suite forces `timeout=0` with two chunks and gets
three `.fail`, zero `.ok` and zero row files, deterministically.

## 8. CI — implemented by splitting the AutoCAD-free half out

**Sources:** GitHub's
[.NET build-and-test guide](https://docs.github.com/en/actions/automating-builds-and-tests/building-and-testing-.net)
(`setup-dotnet` is the recommended way to pin the SDK; `windows-latest` is the runner image),
and the [hosted-runner software list](https://docs.github.com/en/actions/concepts/runners/github-hosted-runners),
which has the .NET SDK but obviously not AutoCAD.

`tests/Harvest.Checks` referenced the plug-in, which references `acdbmgd`/`acmgd` from the
AutoCAD install folder. A hosted runner does not have those, so neither the build nor the
checks could run there.

**Change:** `Core/`, `Output/` and `Reporting/` moved into
`AEC_Batch_Data_Haravester.Core.csproj`, which targets `net10.0` (not `-windows`) and has no
Autodesk or Windows-specific reference at all; the plug-in and the checks both reference it.
`ModelSpaceGuard` moved with them, because it is a pure policy that the checks exercise. The
.NET 10 TFM is deliberate: a Windows-only or Autodesk-only API that creeps in fails the core
build rather than becoming a latent dependency.

`.github/workflows/ci.yml` then builds **only** `tests/Harvest.Checks` (building the solution
would pull in the plug-in and fail on the missing AutoCAD assemblies), runs the 51 checks,
parses every `tools\*.ps1` with the PowerShell parser, and runs `build-manifest.ps1 -SelfTest`.
Those last two cover the parts a `dotnet build` cannot see.

**Proof:** locally, the Release build of the checks project emits exactly one `AEC*.dll` -
`AEC_Batch_Data_Haravester.Core.dll` - and the checks pass, the tools parse, and the key
rules hold. The solution still builds with the plug-in present, and the pipeline output is
still byte-identical to the golden baseline (6,748 rows / 219 attributes).

## 9. Dynamic-block fixture

No API problem: `DynamicBlockTableRecord` is already used for the definition name. The
corpus simply has no dynamic block, so the claim is untested. A fixture must create a block
with a parameter and insert it, then assert that `BlockDefinition` is the real name rather
than `*U…`. Planned with the nested-block work, because both need fixture-building.

## 10. AEC verticals

Property sets, spaces and AEC schedules come from `AecBaseMgd`/`AecArchMgd`, which are not
installed on this machine (confirmed in `batch-harvester-notes.md`). Until they are, AEC
objects arrive as proxies and now appear in the census as `ACAD_PROXY_ENTITY`. That is a
licensing/install decision, not a code change.

---

## Practical notes from the fixtures

- `entmake` of a `RAY` returns `nil` without the subclass markers. The working form is
  `(0 . "RAY") (100 . "AcDbEntity") (100 . "AcDbRay") (10 …) (11 …)`. The first fixture
  attempt omitted them and created nothing, which looked exactly like a census bug.
- A constant `ATTDEF` inside a `BLOCK`/`ENDBLK` pair, followed by an `INSERT`, is enough to
  exercise the constant-attribute path end to end — no BEDIT or dynamic parameters needed.
- Core Console writes its log as spaced UTF-16-ish text; read the status JSON, not the log.
