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
| 5 | Schema is thin for takeoff | `Curve.Area`, `Hatch.Area`, `Polyline2d/3d : Curve` | Planned |
| 6 | `/append` merges two extraction rules | No built-in contract version; we must write one | **Changed** |
| 7 | Chunks run one at a time | Multiple `accoreconsole` processes are a documented pattern | Planned |
| 8 | No CI | The checks reference the AutoCAD plug-in, so cloud CI cannot build them | Planned |
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

## 5. Schema depth — one curve fallback, not eight extractors

**Sources:** [`Curve.Area`](https://help.autodesk.com/cloudhelp/2022/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_Curve_Area.html)
("Accesses the area of the curve"), and
[`Polyline2d`](https://help.autodesk.com/cloudhelp/2022/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_DatabaseServices_Polyline2d.html)
— "Inherits Curve".

The registry already matches by inheritance, so the cheapest correct move is a
`CurveExtractor` registered on `Curve`: it catches `Polyline2d`, `Polyline3d`, `Ellipse`,
`Spline`, `Ray`, `Xline` and any future curve with one class, while the specific extractors
keep their named rows. `Curve.Area` is defined for closed planar curves and throws for open
ones, so the extractor must catch and leave area blank.

`Hatch.Area` exists for the same reason and is the next-highest-value entity: hatches are
where area takeoff usually lives. Both need an `Area` column (schema 4), so they land together
rather than half-shipped.

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

## 7. Parallel chunks — supported, with one blocker to remove first

**Sources:** [Autodesk's own write-up](https://blog.autodesk.io/parallel-aggregation-using-accoreconsole/)
("parallel aggregation can be used to launch multiple instances of AccoreConsole"), and a
[measured forum comparison](https://forums.autodesk.com/t5/net/accoreconsole-is-slower-in-2025/td-p/12826145)
showing `Parallel.ForEach` over Core Console instances beating a serial loop.

The engine is 39–77 ms per drawing while process start is ~1.6 s, so the win is real. The
blocker is ours: `run-harvest-batch.ps1` uses one global `_status\_current.txt` to blame a
timeout, and concurrent chunks would overwrite each other's marker and misattribute the
failure.

**Plan:** per-chunk markers (`_current-<chunk>.txt`) and per-chunk log names, then a
bounded worker set (`AEC_HARVEST_PARALLEL`, default 1 so existing behaviour is unchanged).
Merge stays deterministic because each drawing writes its own files.

## 8. CI — the checks cannot build without AutoCAD

`tests/Harvest.Checks` has a `ProjectReference` to the plug-in, which references
`acdbmgd`/`acmgd` from the AutoCAD install folder. A GitHub-hosted runner does not have
them, so neither the build nor the checks can run there as laid out.

Two options, in order of value:
1. Split `Core/`, `Output/` and `Reporting/` into a dependency-free
   `AEC_Batch_Data_Haravester.Core` project. The checks already only exercise those, and
   the split makes the "no AutoCAD here" claim structural rather than a comment.
2. Until then, a workflow can run only `build-manifest.ps1 -SelfTest` and a link check,
   which is honest but thin.

Not implemented; the split is an architecture change, not a test tweak.

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
