# AutoCAD 2026 API Knowledge Base (ObjectARX / Managed .NET)

Local study map of the official documentation set:

**<https://help.autodesk.com/view/OARX/2026/ENU/>**

Everything below was extracted from the live docs (November 2026). Use this file as the
entry point, then use the generated indexes for detail.

---

## 1. Generated indexes in this folder

| File | What it is | Size |
|---|---|---|
| `objectarx-toc.csv` | **Every page** of the ObjectARX book, flat: `Book, Depth, Title, Id, Url, Source, Parent` (45,844 rows) | ~18 MB |
| `managed-net-dev-guide.md` | Full outline of the Managed .NET Developer's Guide, with links | 50 KB |
| `objectarx-dev-guide.md` | Outline (2 levels) of the native C++ Developer's Guide | 35 KB |
| `managed-net-reference-index.md` | All 29 managed namespaces → top-level types (18,925 pages of API surface) | 277 KB |
| `namespaces.csv` | Namespace inventory with page/type counts | 4 KB |

`objectarx-toc.csv` and `_toctree.raw.json` are build artifacts and are git-ignored.
Regenerate with:

```powershell
powershell -ExecutionPolicy Bypass -File tools\build-docs-index.ps1 -Force
```

Useful searches:

```powershell
# every page mentioning "DataExtraction"
Import-Csv docs\autocad-2026\objectarx-toc.csv | ? Title -match 'DataExtract'

# every managed member of a class
Import-Csv docs\autocad-2026\objectarx-toc.csv |
  ? { $_.Parent -like '*DxDrawingDataExtractor*' } | Select Title, Url

# full-text search inside the reference index
Select-String -Path docs\autocad-2026\managed-net-reference-index.md -Pattern 'SideDatabase|ReadDwgFile'
```

## 2. How the help site is built (so we can scrape it reliably)

The site is a JavaScript shell; content is *not* in the HTML of the viewer URL.

| Thing | URL |
|---|---|
| Table of contents (whole site, 13.5 MB JSON) | `https://help.autodesk.com/view/OARX/2026/ENU/data/toctree.json` |
| Viewer page (human link) | `https://help.autodesk.com/view/OARX/2026/ENU/?guid=<id>` |
| Raw page HTML | `https://help.autodesk.com/cloudhelp/2026/ENU/<book>/files/<id>.htm` |

Book folder prefixes (the `<book>` part above):

| Prefix | Book |
|---|---|
| `OARX-DevGuide-Managed` | Managed .NET Developer's Guide |
| `OARX-ManagedRefGuide` | Managed .NET Reference Guide |
| `OARX-DevGuide` | ObjectARX (C++) Developer's Guide |
| `OARX-RefGuide` | ObjectARX (C++) Reference Guide |
| `OARX-IOP` | Interoperability Guide |
| `OARX-Migration` | Migration Guide |
| `AutoCAD-Core-Console` | AutoCAD Core Console |
| `AutoCAD-Customization`, `AutoCAD-DXF`, `AutoCAD-AutoLISP`, `AutoCAD-ActiveX`, `AutoCAD-JavaScript` | the non-.NET books in the same TOC |

Two `.htm`/`.html` conventions exist: GUID-based pages end in `.htm`; named pages
(`OARX-ManagedRefGuide-What_s_New`) end in `.html`. `toctree.json`'s `ln` field is authoritative.

## 3. Map of the documentation set

The ObjectARX book contains 45,844 pages in 7 sub-books:

| Sub-book | Pages | What it covers |
|---|---|---|
| ObjectARX: Developer's Guide | 884 | Native C++ concepts: database, entities, containers, transactions, deep clone, MDI, reactors, dynamic blocks, graphics/geometry/BREP, COM |
| ObjectARX: Reference Guide | 25,530 | Every native class/function (AcDb*, AcRx*, AcGe*, AcGi*, AcBr*, aced*) |
| ObjectARX: Managed .NET Developer's Guide | 359 | **The .NET API guide — our primary read** |
| ObjectARX: Managed .NET Reference Guide | 18,925 | Every managed namespace/class/member |
| ObjectARX: Interoperability Guide | 64 | Naming rules, ObjectARX vs ObjectDBX module rules, install/packaging |
| ObjectARX: Migration Guide | 73 | Release compatibility and porting notes |
| ObjectARX: Release Notes | 9 | What's new, fiberless operation, tips |

Managed .NET Developer's Guide section map (page counts):

- About .NET and the AutoCAD .NET API (13)
- Getting Started with Microsoft Visual Studio (31) — projects, references, target framework, build/load
- Basics of the AutoCAD .NET API (21) — object hierarchy, collections, commands, in/out of process
- Control the AutoCAD Environment (36) — windows, create/open/save/close drawings, locking, sysvars, input, command line, UI
- Create and Edit AutoCAD Entities (118) — open/close, transactions, create objects, selection sets, edit, layers/colors/linetypes, layer states, text
- Dimensions and Tolerances (29)
- Work in Three-Dimensional Space (14)
- Advanced Drawing and Organizational Techniques (34) — raster images, **blocks and attributes**, external references, **extended data**
- Define Layouts and Plot (27) — layouts, viewports, plot styles, plotting, publish
- Use Events (9)
- Develop Applications (11) — error handling, distribution, security
- Customize User Interface (CUI) Managed API (12)
- ResultBuffer Data Type (1), VBA/VB → C#/VB.NET comparison (1)

## 4. Platform facts that constrain the project

**Target framework (critical):**

| AutoCAD | Release no. | .NET |
|---|---|---|
| 2026 Update 1.2 and later | 25.1 | **.NET 10.0** |
| 2026 → 2026 Update 1.1 | 25.1 | .NET 8.0 |
| 2025 | 25.0 | .NET 8.0 |
| 2024 and earlier | 24.3 and lower | .NET Framework 4.8 / 4.x |

Our `AEC_Batch_Data_ Haravester.csproj` already targets `net10.0`, so it requires
AutoCAD 2026.1.2+ unless we multi-target `net8.0;net10.0`.
Source: [Exercise: Set the Target Framework and OS](https://help.autodesk.com/view/OARX/2026/ENU/?guid=GUID-E4C5C858-787F-4D35-B7FC-1F2CBA3D4966),
[.NET 8 to .NET 10 Project Migration](https://help.autodesk.com/view/OARX/2026/ENU/?guid=GUID-8564A04F-527D-4BB3-8B31-B90D6E24AF08)

**Required references** (from the ObjectARX SDK `inc` folder, or the AutoCAD install folder),
all with **Copy Local = No**:

- `AcCoreMgd.dll` — core services, `Application`, `DocumentManager`, `Editor`
- `AcDbMgd.dll` — database/entities (`DatabaseServices`, `Geometry`, `Colors`, `DataExtraction`)
- `AcMgd.dll` — UI/graphics side; **must not be used in Core Console or ObjectDBX/CRX**
- Optional: `AdWindows.dll` (ribbon, palettes), `AcCui.dll` (CUI), `acdbmgdbrep.dll` (BRep)

Source: [Exercise: Reference the AutoCAD .NET API Files](https://help.autodesk.com/view/OARX/2026/ENU/?guid=GUID-2363CE7C-AC2B-4CAC-AE5D-F77B386132D7)

**Execution model:** the Managed .NET API is **in-process only**. Out-of-process automation
must go through COM (`AutoCAD.Application.25.1`) and push commands into the running AutoCAD.
Source: [Out-of-Process Versus In-Process](https://help.autodesk.com/view/OARX/2026/ENU/?guid=GUID-C8C65D7A-EC3A-42D8-BF02-4B13C2EA1A4B)

**Security:** `SECURELOAD` restricts loading to trusted locations (`TRUSTEDPATHS`) — applies to
`NETLOAD`, `accoreconsole` scripts, and CRX modules.

## 5. Execution hosts — the key architectural decision

| Host | Process | UI APIs | COM/ActiveX | Best for |
|---|---|---|---|---|
| AutoCAD desktop (in-process .NET) | `acad.exe` | yes | yes | interactive commands, dialogs, ribbon |
| **AutoCAD Core Console** (`accoreconsole.exe`) | `accoreconsole.exe` | **no** (`AcMgd.dll` forbidden, no WPF/MFC dialogs) | **no** | **batch processing, no user interaction** |
| ObjectDBX / Object Enabler (`.dbx`) | host's process | no | no | read/write custom objects in any RealDWG host |
| RealDWG SDK (separately licensed) | own process | n/a | n/a | standalone DWG read/write without AutoCAD |
| Out-of-process COM | `acad.exe` driven from your exe | yes | yes | orchestrating a visible AutoCAD |

Core Console is documented as "a version of AutoCAD without a graphical user interface … allows
a developer to batch process multiple drawings and automate repetitive tasks", supports the full
API range except UI, and **supports scripts, .NET assemblies and ObjectARX/CRX**.

```text
AcCoreConsole.exe [/i <input dwg>] /s <script> [/product <product>] [/l <language>]
                  [/isolate <userid> <userDataFolder>] [/readonly] [/p[rofile] <profile>]
                  [/loadmodule <module>]
```

Limitations to design around: dialogs/palettes unavailable, several commands unsupported
(`-LAYER` instead of `LAYER`, `CHPROP` instead of `PROPERTIES`, no `NEW`, `CLOSE`, `PAN`, `CUI`…),
no ActiveX, and no `AcMgd.dll` reference. `accoreconsole.exe /i a.dwg /s b.scr` style invocation is
per-drawing, so a batch runner must loop over files (one process per drawing is the safe pattern).
Source: [AutoCAD Core Console](https://help.autodesk.com/view/OARX/2026/ENU/?guid=AUTOCAD_CORE_CONSOLE)

ObjectDBX/RealDWG: ObjectDBX is the subset of ObjectARX used to define custom DB objects/entities;
`.dbx` modules load in any RealDWG host (AutoCAD, DWG TrueView, Inventor, third-party). RealDWG SDK is
**licensed separately** and lets you read/write DWG/DXF **without AutoCAD**.
Source: [Overview of ObjectDBX and Object Enablers](https://help.autodesk.com/view/OARX/2026/ENU/?guid=GUID-FF60A11B-1169-483C-9A65-85203B3A1440)

## 6. Object model essentials (.NET)

```text
Application
 ├─ Application.DocumentManager  (DocumentCollection)
 │   └─ MdiActiveDocument (Document)
 │       ├─ Database  ── TransactionManager ── Transaction ── GetObject(ObjectId, OpenMode)
 │       │      ├─ BlockTable        → BlockTableRecord (model space, paper space, block definitions)
 │       │      ├─ LayerTable, TextStyleTable, DimStyleTable, LinetypeTable, ...
 │       │      ├─ NamedObjectsDictionary → dictionaries, extension dictionaries, xrecords
 │       │      └─ LayoutDictionary
 │       └─ Editor (command line, prompts, selection)
 └─ DocumentManager events, Application events
```

- **Transactions**: `db.TransactionManager.StartTransaction()` → `GetObject(id, OpenMode.ForRead|ForWrite)`
  → `Commit()` → `Dispose()` (or `using`). Uncommitted work rolls back. `StartOpenCloseTransaction()`
  wraps `Open`/`Close` and is recommended in utility/support functions and most event handlers.
  ([Use Transactions](https://help.autodesk.com/view/OARX/2026/ENU/?guid=GUID-50FD6118-B2D1-4313-A7D6-830794DFDEFA))
- **ObjectIds** are database-scoped; never persist them, never pass them between databases.
- **Opening drawings**: `DocumentCollection.Open`/`Add`, or load a file into a separate `Database`
  with `ReadDwgFile(path, FileShare.ReadWrite, false, "")` for side-database work.
- **Document-independent databases**: `Database` is normally associated with a document (undo).
  Call `DisableUndoRecording(true)` on a side database to detach it; document-scoped system variables
  then fall back to defaults, and no document lock is needed.
  ([Document-Independent Databases](https://help.autodesk.com/view/OARX/2026/ENU/?guid=GUID-FEA5A128-DE9D-4AFF-BAC7-BD0D2A313BF5))
- **Blocks/attributes**: `BlockTableRecord` holds `AttributeDefinition`s; a `BlockReference` exposes
  `AttributeCollection` of `AttributeReference`s → read `Tag`, `TextString`, `IsConstant`, and for
  multline attributes `IsMTextAttribute` + `MTextAttribute`.
  ([Extract Attribute Information](https://help.autodesk.com/view/OARX/2026/ENU/?guid=GUID-BA69D85A-2AED-43C2-B5B7-73022B5F28F8))
- **Custom data**: `XData` (`ResultBuffer`), `Xrecord`s in the named-object dictionary, and
  `ExtensionDictionary` per object — the three standard places third-party data hides.
- **Deep clone / wblock clone**: `Database.WblockCloneObjects` and the `BeginWblock*` / `WblockEnded`
  events are the supported way to move objects between databases (needed if we merge/extract content).

## 7. The native "data extraction" engine (DXE) — closest thing to this project

`Autodesk.AutoCAD.DataExtraction` (146 pages, 7 public types) is the API behind the `DATAEXTRACTION`
command. It is designed to **collect properties/attributes from a list of drawings in one pass**:

| Type | Role |
|---|---|
| `DxDrawingDataExtractor` | The engine: `DiscoverTypesAndProperties()`, `ExtractData()`, `ExtractPublishDataForOneObject()`, `Settings`, `DiscoveredTypesAndProperties`, `ExtractedData`, `OnProgress`, `OnError` |
| `DxDrawingDataExtractorSettings` | `DrawingList`, `ExtractFlags`, `SelectedObjects`, `SetSelectedTypesAndProperties()`, JSON round-trip (`FromJsonNode`/`ToJsonNode`) |
| `DxFileList` | `AddFile`, `AddFolder`, `AddXrefFile`, `Files`, `Folders`, `XrefFiles`, enumerator |
| `DxFolder` | Folder recursion (`IncludeSubFolders`, `LiveFolder`) |
| `DxFileReference` | File identity, `LastWriteTime`, `Resolve()` |
| `DxExtractionSettings` | `.dxe` template file handling (`Filename`, `CopyFrom`, `AdjustFileReferencesPerNewDxeFileName`) |
| `AdoOutput` | Export the extracted `DataSet` (ADO/DB) to file |

Implication: before writing a bespoke harvester we should evaluate DXE — it already does
drawing list + property discovery + extraction + progress/error callbacks + ADO export.
Start at [Autodesk.AutoCAD.DataExtraction](https://help.autodesk.com/view/OARX/2026/ENU/?guid=OARX-ManagedRefGuide-Autodesk_AutoCAD_DataExtraction).

## 8. Namespace inventory (managed)

| Namespace | Pages | Use |
|---|---|---|
| `Autodesk.AutoCAD.DatabaseServices` | 7,532 | Database, entities, symbol tables, transactions (the core) |
| `Autodesk.AutoCAD.Geometry` | 2,002 | Point3d/Vector3d/Matrix3d, curves, tolerances |
| `Autodesk.AutoCAD.EditorInput` | 856 | prompts, selection sets, keywords, jigs |
| `Autodesk.AutoCAD.GraphicsInterface` | 947 | drawables, transient graphics, custom entity display |
| `Autodesk.Windows` (+ Palettes, Ribbon, Themes, Forms) | ~3,000 | WPF/ribbon/palette UI (desktop only) |
| `Autodesk.AutoCAD.ApplicationServices` (+ `.Core`) | 530 | Application, DocumentManager, Document, DocumentLock |
| `Autodesk.AutoCAD.Runtime` | 476 | `CommandMethod`, `IExtensionApplication`, RX classes, `Interop` |
| `Autodesk.AutoCAD.PlottingServices` / `Publishing` | 516 | plot, publish DSD |
| `Autodesk.AutoCAD.DataExtraction` | 146 | DATAEXTRACTION engine |
| `Autodesk.AutoCAD.DatabaseServices.Filters` | 81 | layer filters |
| `Autodesk.AutoCAD.ComponentModel`, `Colors`, `LayerManager`, `Customization` | 1,557 | supporting |
| `Autodesk.AutoCAD.BoundaryRepresentation` | 290 | BRep/solid topology |

Full list: `namespaces.csv`, details: `managed-net-reference-index.md`.

## 9. What's new in the 2026 managed API (highlights)

- `Cell.BlockTableRecordId`, `CellContent.BlockTableRecordId`, `CellRange.GetStyleOverrides`,
  `CellReference.Row` — table/cell edits
- `Database.BeginDeepCloning` event; `WblockNoticeEventArgs.From` — cloning/extraction hooks
- `Autodesk.AutoCAD.Ribbon` — Center Layer control availability (`EnableCenterLayerProperty`)

Source: [Managed .NET — What's New](https://help.autodesk.com/view/OARX/2026/ENU/?guid=OARX-ManagedRefGuide-What_s_New)

## 10. Gaps in this documentation set

1. **AEC verticals are not here.** AutoCAD Architecture / MEP / Civil 3D / Plant objects
   (AecDb* classes, `AecBase.dbx`, `AeccXUiLand*`, etc.) have their own SDKs and Object Enablers.
   ObjectARX 2026 only covers vanilla AutoCAD plus how to load enablers. If "AEC data" means
   walls, doors, spaces, pipes, or Civil 3D objects, we need the vertical SDK and its .NET API docs.
2. **RealDWG** (headless DWG I/O without AutoCAD) is a separate license and separate docs.
3. **ObjectDBX** in .NET: the managed API is usable inside a `.dbx`/CRX but the docs describe the
   constraints in the C++ Interoperability Guide, not as a managed recipe.
4. The docs give patterns but no official "batch harvester" sample; the closest artifacts are the
   DXE namespace, the Core Console page, and the ObjectARX SDK `samples` folder (installed with the SDK).

## 11. Next reading order (recommended)

1. Getting Started → *Exercise: Create a New Project / Reference the API Files / Set Target Framework / Build and Load*
2. Basics → *Understand the AutoCAD Object Hierarchy*, *Out-of-Process Versus In-Process*
3. Create and Edit Entities → *Open and Close Objects*, *Work With ObjectIds*, *Use Transactions*
4. Advanced → *Use Blocks and Attributes*, *Assign and Retrieve Extended Data*
5. Core Console page (batch execution + `/readonly` + trusted locations)
6. Reference: `DatabaseServices.Database`, `BlockTableRecord`, `AttributeReference`, `DataExtraction.*`
7. Interoperability Guide → *ObjectARX versus ObjectDBX Modules* (only if we ship a `.dbx`)
