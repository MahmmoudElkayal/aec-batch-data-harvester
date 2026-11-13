# Batch Data Harvester — API notes and open design questions

Working notes derived from the AutoCAD 2026 ObjectARX documentation
(see `README.md` in this folder for the full map).

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

## 4. Open questions (need answers before coding)

1. **Which AutoCAD?** Vanilla AutoCAD 2026.1.2+, or an AEC vertical (Architecture/MEP/Civil 3D)?
   If AEC, we need the vertical SDK + Object Enablers — they are *not* in ObjectARX 2026 docs.
2. **What data exactly?** Blocks + attributes only, or also tables, xdata/xrecords, layers, layouts,
   dynamic block properties, AEC property sets?
3. **Where does it run?** Desktop AutoCAD command, `accoreconsole` on a server, or standalone
   (RealDWG)?
4. **Output shape?** One flat table per drawing, a normalized relational schema, or JSON per drawing?
5. **Scale/parallelism?** How many DWGs, and is parallel `accoreconsole` allowed (license/CPU)?
6. **Read-only guarantee?** Should the tool ever modify source DWGs? (`/readonly` + side DB says no.)
7. **Do we ship a `.dbx`** so other hosts (DWG TrueView, Revit-era tools) can read our custom data?
8. **Do we need to evaluate DXE first** rather than build a bespoke extractor?
