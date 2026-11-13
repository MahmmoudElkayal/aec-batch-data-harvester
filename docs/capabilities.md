# Capability Report — AEC Batch Data Harvester

**Version:** prototype, commit `04e267f`
**Environment:** AutoCAD 2026 (release 25.1, build `25.1.179.0.0`), .NET 10, Windows x64
**Report date:** 2026-11-14

---

## 1. Summary

A managed AutoCAD plug-in plus a batch runner that extracts entity data from DWG files
without a GUI, and aggregates it into a single CSV.

It is a **working end-to-end pipeline**, not a finished product. The mechanical parts —
headless hosting, batch iteration, fault isolation, reporting — are proven. The *content*
extracted is deliberately minimal (blocks and polylines) because the target schema has not
been decided yet. Adding entity types is a small, contained change.

| Question | Answer |
|---|---|
| Can it read DWGs headlessly? | Yes, via `accoreconsole.exe` |
| Can it process many files unattended? | Yes, one process per drawing, logged per file |
| Can it damage source drawings? | No — `/readonly` plus read-only transactions |
| Is the output usable in Excel/pandas? | Yes — clean RFC 4180 CSV, UTF-8 without BOM |
| Does it extract everything? | No — two entity types today |
| Is it production-ready? | No — no timeout, no parallelism, no packaging, no tests |

---

## 2. Verified evidence

Everything below was measured, not assumed.

| Measurement | Result |
|---|---|
| Test corpus | 3 real Autodesk sample DWGs (`Assembly Sample`, `Floor Plan Sample`, `House Designer`) |
| Rows extracted | 3,278 data rows (+1 header) |
| Duplicate rows | 0 |
| Breakdown | 1,046 blocks, 2,232 polylines |
| Wall-clock per drawing | ~1–2 s |
| CSV size | 191,826 bytes (≈58.5 bytes/row) |
| Build | 0 errors, 3 warnings (all `MSB3277` assembly-version unification from AutoCAD's own DLLs) |
| Batch success rate on corpus | 3/3 |

---

## 3. Capability matrix

### 3.1 Build and deployment

| Capability | Status | Notes |
|---|---|---|
| Target AutoCAD 2026 / .NET 10 | ✅ | `net10.0-windows`, `PlatformTarget=x64` |
| Reference the AutoCAD API without an ObjectARX SDK install | ✅ | three DLLs taken from `C:\Program Files\Autodesk\AutoCAD 2026\` |
| Avoid runtime DLL conflicts | ✅ | `Copy Local = false` on all three references |
| Configurable AutoCAD location | ✅ | `AcadDir` MSBuild property |
| Load in desktop AutoCAD | ✅ | `NETLOAD` + `Extract_BOM` |
| Load in Core Console | ✅ | no `AcMgd.dll` types touched |
| Package as `.bundle` / sign | ❌ | not implemented |

### 3.2 Data access

| Capability | Status | Notes |
|---|---|---|
| Operate on the loaded drawing | ✅ | `HostApplicationServices.WorkingDatabase` |
| Read-only transaction discipline | ✅ | nothing opened `ForWrite` |
| Model space traversal | ✅ | `BlockTable` → `BlockTableRecord` → `ObjectId` |
| Paper space / layouts | ❌ | model space only |
| Nested blocks (blocks inside blocks) | ❌ | not traversed |
| Xref contents | ❌ | not traversed |
| Erased objects skipped | ✅ | `ObjectId.IsErased` |
| Corrupt/proxy object tolerated | ✅ | per-entity `try/catch`, harvest continues |

### 3.3 Extracted fields

| Entity | Fields | Status |
|---|---|---|
| `BlockReference` | block name, layer, handle, X/Y/Z insertion point | ✅ |
| `Polyline` (LWPOLYLINE) | layer, handle, length | ✅ |
| Block **attributes** (tag/value) | — | ❌ highest-value gap |
| `Polyline2d` / `Polyline3d` | — | ❌ |
| `Line`, `Arc`, `Circle`, `Ellipse`, `Spline` | — | ❌ |
| `DBText`, `MText` | — | ❌ |
| `Table` / cells | — | ❌ |
| XData, Xrecords, extension dictionaries | — | ❌ |
| Dynamic block properties | — | ❌ |
| AEC vertical objects (walls, spaces, pipes) | — | ❌ needs vertical SDK |

### 3.4 Output

| Capability | Status | Notes |
|---|---|---|
| CSV output | ✅ | header written once, append-safe |
| RFC 4180 escaping | ✅ | commas, quotes, newlines |
| UTF-8 without BOM | ✅ | opens cleanly in Excel and pandas |
| Invariant number formatting | ✅ | `0.000`, no locale-dependent decimal separators |
| Configurable output location | ✅ | `AEC_HARVEST_OUTPUT` → `AEC_HARVEST_ROOT\Output` → assembly folder |
| JSON / SQLite / Excel / DB output | ❌ | |
| Configurable column set | ❌ | schema is fixed in code |

### 3.5 Batch orchestration

| Capability | Status | Notes |
|---|---|---|
| Iterate a folder of DWGs | ✅ | `RunPipeline.bat` |
| Recurse into sub-folders | ✅ | `/r` |
| Read-only batch mode | ✅ | `/readonly` |
| Auto-detect newest Debug/Release build | ✅ | newest DLL wins |
| Regenerate the Core Console script each run | ✅ | `NETLOAD` path cannot go stale |
| Fresh CSV per run / keep appending | ✅ | default fresh, `/append` to keep |
| Rebuild before running | ✅ | `/new` |
| Continue after a failing drawing | ✅ | per-drawing process isolation |
| Summary report | ✅ | processed / succeeded / failed / CSV line count |
| Parallel workers | ❌ | serial, one process at a time |
| Per-drawing timeout | ❌ | a hung DWG stalls the queue |
| Resume an interrupted batch | ❌ | re-runs everything |
| Filename de-duplication across sub-folders | ❌ | same base name collides in `Logs` / `_status` |

### 3.6 Diagnostics

| Capability | Status | Notes |
|---|---|---|
| Per-drawing log file | ✅ | `Logs\<drawing>.log` |
| Machine-readable per-drawing status | ✅ | `Output\_status\<drawing>.ok` / `.fail` |
| Row count per drawing | ✅ | inside the status file |
| Distinguish AutoCAD error vs .NET error | ✅ | `ErrorStatus` captured separately |
| Exit code trustworthy | ❌ | Core Console's own exit code is unreliable — status files are used instead |
| Progress output during a run | ✅ | timestamped filename per drawing |

### 3.7 Documentation knowledge base

| Capability | Status |
|---|---|
| Full ObjectARX 2026 TOC indexed (45,844 pages) | ✅ |
| Managed .NET Developer's Guide outline (359 pages) | ✅ |
| C++ Developer's Guide outline | ✅ |
| 29 managed namespaces → 18,925 reference pages | ✅ |
| Regeneration script | ✅ `tools/build-docs-index.ps1` |

---

## 4. Known issues

| # | Issue | Impact | Mitigation |
|---|---|---|---|
| 1 | A trailing blank line in `.scr` repeats the last command | Duplicate output (2× rows) | Script has no trailing blank line; stdin from `NUL` |
| 2 | Core Console drops managed `Console.WriteLine` | No console feedback | Status files are the source of truth |
| 3 | `SECURELOAD` blocks `NETLOAD` outside `TRUSTEDPATHS` | Assembly won't load | `SECURELOAD 0` in script (dev); trusted path for production |
| 4 | No timeout | One hung DWG stalls the batch | Not mitigated yet |
| 5 | Serial execution | ~1–2 s per drawing, linear | Not mitigated yet |
| 6 | Output CSV is append-only across drawings | Partial run leaves partial data | Fresh mode by default |

---

## 5. Practical limits

- **Throughput:** ≈30–60 drawings/minute for small drawings; startup cost dominates. A
  1,000-drawing batch is roughly 25–35 minutes serial.
- **Memory:** one process per drawing bounds growth; a single huge drawing is limited by
  AutoCAD's own address space (x64, so not tight).
- **Licensing:** Core Console consumes an AutoCAD licence seat while running.
- **Scope:** anything that is not a block reference or LWPOLYLINE is currently invisible to
  the harvester, even though the API can read it.

---

## 6. Recommended next steps

Ordered by value per unit of effort (full detail in [roadmap.md](roadmap.md)):

1. **Block attributes** — `BlockReference.AttributeCollection` → `AttributeReference.Tag` /
   `TextString`. This is what makes the output a real BOM. Small change, large payoff.
2. **Per-drawing timeout** — a hung drawing currently stops a 1,000-file batch. Wrap the
   child process in a timeout before adding parallelism.
3. **Parallel workers** — N concurrent `accoreconsole` processes behind the timeout.
4. **Entity coverage** — `Polyline2d`/`Polyline3d`, `Line`, `Arc`, `Table`, `MText`.
5. **XML/JSON side output** — keep CSV for humans, add a structured format for tooling.
6. **Paper space, nested blocks, xrefs** — needed before the numbers can be trusted on real
   production drawings.
7. **Decide the AEC question** — if the drawings come from Architecture / MEP / Civil 3D,
   the interesting data lives in vertical objects and needs the vertical SDK.
