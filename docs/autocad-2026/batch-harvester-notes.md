# Batch Data Harvester — API notes and open design questions

Working notes derived from the AutoCAD 2026 ObjectARX documentation
(see `README.md` in this folder for the full map).

## 0. Confirmed environment (checked on this machine)

- AutoCAD 2026 – English, `ACAD-9101:409`, Release **25.1**, binaries **25.1.179.0.0**
- `acdbmgd.runtimeconfig.json` targets **net10.0** → installed build is **2026 Update 1.2+**
- `accoreconsole.exe` present in `C:\Program Files\Autodesk\AutoCAD 2026`
- Architecture Object Enablers (`AecArchXOE.dll`, `AecADTUPI_OE.dll`) ship with plain AutoCAD,
  but the **managed AEC API is not installed** (`AecBaseMgd.dll`, `AecArchMgd.dll`, `AeccDbMgd.dll` absent)
- ObjectARX SDK not installed → either install it (for `inc`, `lib`, samples, docs CHM) or reference
  `AcCoreMgd.dll` / `AcDbMgd.dll` / `AcMgd.dll` straight from the AutoCAD install folder

## 1. What the API gives us for "harvest data from many DWGs"

| Capability | API |
|---|---|
| Walk files in folders (recursive, xrefs) | `DataExtraction.DxFileList` / `DxFolder` |
| Read a DWG without opening it in the UI | `Database.ReadDwgFile(path, FileShare, false, "")` on a side `Database` |
| Iterate everything in model/paper space | `BlockTable` → `BlockTableRecord` → `ObjectId` → `GetObject` |
| Block attribute values | `BlockReference.AttributeCollection` → `AttributeReference.Tag/TextString` |
| Table/cell data | `Table`, `Cell`, `CellReference`, `CellRange` (2026 additions) |
| Per-object custom data | `DBObject.XData`, `ExtensionDictionary`, named-object `Xrecord`s |
| Named object inventory | `LayerTable`, `TextStyleTable`, `DimStyleTable`, `LinetypeTable`, layout dictionary |
| Property/type discovery for arbitrary drawings | `DxDrawingDataExtractor.DiscoverTypesAndProperties()` |
| Extraction + progress + errors | `DxDrawingDataExtractor.ExtractData()`, `OnProgress`, `OnError` |
| Export | `AdoOutput` / own writer (CSV, JSON, SQLite, Excel) |
| Run headless, many files | `accoreconsole.exe /i file.dwg /s script.scr /readonly /loadmodule x.dll` |

## 2. Candidate architectures

**Chosen: A at the process boundary, B inside each process.** `RunPipeline.bat` starts one
`accoreconsole.exe` per chunk of drawings (default 50) and loops the side-database harvester
inside it, so start-up is amortised while a crash still costs only one chunk.

**A. Core Console batch runner (recommended first)**
`harvester.exe` (orchestrator) → one `accoreconsole.exe` process per DWG → `NETLOAD`/`/loadmodule`
of `AEC_Batch_Data_Haravester.dll` → command writes a per-drawing result (JSON/CSV row) → orchestrator
aggregates + logs.
- Pros: no license gymnastics, no UI, fully automatable, crash isolation per drawing.
- Cons: one process per drawing has start-up cost (~1–3 s); no `AcMgd.dll`, no dialogs; must be in a
  trusted path; must handle corrupt/crashed drawings at process level.

**B. In-process side-database harvester**
One AutoCAD (or Core Console) session, loop over files with a fresh `Database` +
`ReadDwgFile` + `DisableUndoRecording(true)`.
- Pros: fastest per file, single process, easy xref resolution.
- Cons: a single bad DWG can take down the whole run; large drawings hold memory; must dispose
  `Database`/`Transaction` correctly; no document lock needed for independent DBs.

**C. RealDWG standalone** — no AutoCAD installed at all. Requires a separate RealDWG license and
SDK. Same object model, but AEC/custom objects need their Object Enablers.

**D. Use the DXE engine directly** — configure `DxDrawingDataExtractorSettings.DrawingList` with a
`DxFileList`, discover types/properties once, extract, and export. Least code, most "AutoCAD-like"
output (matches `DATAEXTRACTION`), but less control over schema/flattening.

## 3. Non-negotiables from the docs

- `net10.0` only runs on AutoCAD 2026.1.2+. Otherwise add `net8.0` to `TargetFrameworks`.
- `PlatformTarget=x64`, `Copy Local = No` for `AcCoreMgd/AcDbMgd/AcMgd`.
- Never reference `AcMgd.dll` in anything we intend to run in Core Console.
- Everything loaded/executed must sit under a trusted path (`TRUSTEDPATHS`) — `SECURELOAD`.
- No `Editor` prompts, dialogs or palettes in batch mode; log to file instead.
- Per-drawing failure must not abort the batch; capture `System.Exception` and
  `Autodesk.AutoCAD.Runtime.Exception` (`ErrorStatus`) separately.
- Dispose every `Transaction` and side `Database`; commit writes only when intentional.

## 4. Decisions (these were open questions before coding)

1. **Which AutoCAD?** Vanilla AutoCAD 2026 Update 1.2+ (`25.1`). The managed AEC API is not
   installed here, so vertical objects arrive as proxies and are skipped rather than half-read.
2. **What data exactly?** Every entity in the corpus census — `Block` (with attributes),
   `Polyline`, `Line`, `Arc`, `Circle`, `DBText`, `MText`, `Solid` — in model and paper space.
   Tables, xdata/xrecords, layers and dynamic-block properties are not extracted yet; dynamic
   block references do resolve their definition through `DynamicBlockTableRecord`.
3. **Where does it run?** Both: `accoreconsole.exe` for unattended batches, and `NETLOAD` +
   `Extract_BOM` inside desktop AutoCAD for a single drawing. No RealDWG.
4. **Output shape?** One flat CSV per drawing for entities plus one long-format CSV for
   attributes, merged in a deterministic order. JSON/parquet were deferred.
5. **Scale/parallelism?** Serial chunks of 50 drawings per process (300 s timeout) for now;
   parallel `accoreconsole` processes are the next scale step, not yet implemented.
6. **Read-only guarantee?** Yes, always: `/readonly`, read-only transactions on side databases,
   and no code path opens the source drawing for write.
7. **Do we ship a `.dbx`?** No. The plug-in is a managed command assembly, loaded by `NETLOAD`.
8. **DXE or bespoke extractor?** Bespoke. A registry of per-type extractors gives control over
   the flat schema, and the measured cost (39–77 ms per drawing) is well inside the batch budget.

## 5. Where the shipped code diverges from this note

- The process boundary is a **chunk**, not a single drawing: one Core Console process harvests
  up to `AEC_HARVEST_CHUNK` drawings, which cuts start-up from ~1.6 s to ~40 ms per drawing.
- Core Console exit codes proved unreliable, so pass/fail comes from per-drawing status files
  (`_status\<key>.ok|.fail`, key = `Stem-<8 hex SHA256(relative path)>`), written temp-then-rename.
- `SECURELOAD 0` is set in the generated script for development; production should instead put
  the DLL under `TRUSTEDPATHS`.
- Every verdict now carries `schema` and `tool`, and `/append` rejects a verdict written under
  another schema instead of merging two extraction rules into one CSV. The current contract is
  schema 4 (the `Area` column).
- Entity classes with no extractor are counted by DXF name (`unsupported`,
  `unsupportedTotal`) rather than skipped silently; the sample corpus reports
  `VIEWPORT=8`. Sources and the remaining work are in `harvest-api-research.md`.
- Nested block geometry and xref contents are harvested on request (`AEC_HARVEST_NESTED=1`),
  with the insertion transform applied and the ancestor chain recorded per row.
- A single curve fallback extractor (registered on `Curve`) harvests the curve types that do
  not have a named extractor, and `Area` is reported for closed curves and hatches. This is
  why the acceptance fixture's unsupported probe is a 3DFACE: a RAY is a curve and is now
  harvested.
