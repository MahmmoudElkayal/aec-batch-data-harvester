# Roadmap — AEC Batch Data Harvester

Future features and prioritised next steps.
Current capabilities are documented separately in [capabilities.md](capabilities.md).

**Last updated:** 2026-11-14 · prototype @ `74703fe`

---

## Guiding principles

1. **Headless first.** Anything that cannot run unattended in Core Console is a second-class
   feature.
2. **Never modify a source drawing.** `/readonly`, read-only transactions, no exceptions.
3. **Fail per drawing, never per batch.** One bad DWG must not cost the other 999.
4. **Decide the schema before expanding coverage.** More entity types with an unstable column
   layout just produces more churn.
5. **Every extracted fact must be traceable** back to file + handle.

---

## Horizons

| Horizon | Theme | Goal |
|---|---|---|
| **Now** | Make the output useful | BOM-grade rows, batch cannot hang |
| **Next** | Make it complete enough for real drawings | More entity types, paper space, nested blocks |
| **Later** | Make it a product | Parallel, structured output, packaging, tests |
| **Conditional** | Vertical support | Only if the source drawings are AEC |

---

## P0 — Now

### 1. Block attributes

**Why:** without tag/value pairs the CSV lists where a block sits, not what it *is*. This is the
single change that turns the tool into a bill-of-materials extractor.

**What:**
- `BlockReference.AttributeCollection` → `AttributeReference`
- Emit `Tag`, `TextString`, `IsConstant`, and handle multiline attributes
  (`IsMTextAttribute` / `MTextAttribute`)
- Decide the row shape: one row per attribute, or a folded `Tag=Value;…` column

**Effort:** S · **Depends on:** schema decision (see [Decisions](#decisions-required))

### 2. Per-drawing timeout

**Why:** a single malformed DWG currently stalls the whole batch indefinitely. Observed twice
during development.

**What:**
- Kill the child `accoreconsole` after N seconds (configurable, default ~120 s)
- Record `TIMEOUT` as a distinct status alongside `.ok` / `.fail`
- Continue with the next drawing

**Effort:** S · **Depends on:** nothing

### 3. Fix the data-scope decision

**Why:** everything below is guesswork until we know what the output is *for*.

**Questions:** BOM only? Full drawing inventory? Feed for a database? Which entity types matter?

**Effort:** decision, not code · **Blocks:** attributes schema, structured output format

---

## P1 — Next

### 4. Parallel workers

**Why:** serial execution is ~1–2 s per drawing; 10,000 drawings is hours.

**What:**
- Bounded worker pool (default = cores − 1)
- Safe CSV writes: either one file per drawing aggregated afterwards, or a lock around appends
- Requires the timeout from P0 first, or one hang deadlocks a worker

**Effort:** M

### 5. Entity coverage

| Entity | Fields to add | Effort |
|---|---|---|
| `Polyline2d`, `Polyline3d` | layer, handle, length | S |
| `Line` | layer, handle, length | S |
| `Arc`, `Circle`, `Ellipse` | layer, handle, radius/length | S |
| `MText`, `DBText` | layer, handle, contents, style | M |
| `Table` | layer, handle, rows, cells (2026 `Cell`/`CellRange` APIs) | M |
| `Hatch` | layer, handle, area, pattern name | M |
| `Dimension` | measurement, text override, style | M |

### 6. Paper space, nested blocks, xrefs

**Why:** production drawings put data in layouts and inside blocks; xrefs carry whole
disciplines. Model-space-only output under-reports badly.

**What:**
- Include `BlockTableRecord.PaperSpace` records, tagged by layout name
- Recurse into `BlockReference` definitions with a depth guard and a parent handle chain
- Optionally resolve xrefs (`BlockTableRecord.IsFromExternalReference`) and tag the source file
- Deduplicate / flag the same block seen at multiple nesting levels

**Effort:** M–L

### 7. Structured output

**Why:** CSV is for humans and Excel. Tooling wants types, nesting and nullability.

**What:** JSON (one document per drawing, or NDJSON one row per line) and optionally SQLite.
Keep CSV as the default so nothing breaks.

**Effort:** S–M

---

## P2 — Later

### 8. Incremental runs and resume

Skip drawings whose output is newer than their `.dwg` (mtime + size), so a 10,000-file batch
can be re-run after a crash in minutes instead of hours.

### 9. Custom data harvesting

- `XData` (`ResultBuffer`) with registered application names
- `ExtensionDictionary` and `Xrecord` traversal
- Named-object dictionary contents
- Third-party dictionaries (a common place AEC/app data hides)

### 10. Drawing-level metadata

Header variables, `Database.SummaryInfo`, units, layer/text/dimstyle inventories, layout list,
drawing properties. Cheap to produce and often the first thing anyone asks for.

### 11. Configuration file

Replace hardcoded entity types and columns with a declarative schema (JSON/TOML): which entity
types, which properties, which columns. Makes "what to extract" a config change, not a rebuild.

### 12. Packaging and distribution

- Autoloader `.bundle` (`PackageContents.xml`) so plain AutoCAD picks it up
- Digital signing of the assembly
- Documented `TRUSTEDPATHS` setup so `SECURELOAD` can stay at `1` in production

### 13. Orchestrator beyond a batch file

A small .NET CLI that owns the worker pool, timeout, resume, logging and progress — replacing
`RunPipeline.bat` while keeping it as a thin wrapper for double-click users.

### 14. Tests and CI

- Unit tests for CSV escaping, number formatting, path resolution
- Integration test: a checked-in fixture DWG that must produce an exact expected CSV
- GitHub Actions on `windows-latest` — cannot run AutoCAD, so scope CI to build + unit tests

### 15. Output destinations

Direct load into SQLite / SQL Server / PostgreSQL, or Parquet for analytics. Only worth it once
the schema is stable.

---

## Conditional — AEC verticals

If the source drawings come from AutoCAD Architecture, MEP or Civil 3D, the valuable data
(walls, spaces, property sets, pipes, alignments) is **not** in vanilla `AcDb*` classes. It
requires the vertical SDK and its managed assemblies (`AecBaseMgd.dll`, `AecArchMgd.dll`,
`AeccDbMgd.dll`), none of which ship with plain AutoCAD.

Plain AutoCAD does load the Object Enablers (`AecArchXOE.dll`), so proxy graphics render — but
structured property access needs the SDK.

**Blocked on:** the unanswered question of what produced the target DWGs.

---

## Decisions required

| # | Decision | Blocks |
|---|---|---|
| 1 | Are the DWGs from an AEC vertical (Architecture / MEP / Civil 3D)? | Property sets, whole schema |
| 2 | What is the output consumed by — Excel, a database, a downstream tool? | Output format, column design |
| 3 | Attribute row shape: one row per attribute vs folded column | Attribute implementation |
| 4 | Is the tool internal, or published? | Packaging, docs, licence, CI |
| 5 | Must it run on 2026 Update 1.1 or earlier (.NET 8)? | Multi-targeting |

---

## Out of scope (for now)

| Not doing | Reason |
|---|---|
| Editing or cleaning drawings | Harvester only; keeps `/readonly` guarantee absolute |
| RealDWG standalone (no AutoCAD installed) | Separate Autodesk licence and SDK |
| Plotting / publishing / PDF output | Different API surface (`PlottingServices`), no batch value |
| GUI | Contradicts headless-first |

---

## Milestones

| Milestone | Definition of done |
|---|---|
| **M1 — BOM-grade** | Blocks + attributes + nesting, one row per component, schema frozen |
| **M2 — Batch-safe** | Timeout + parallel + resume; 10,000 drawings unattended, per-file status |
| **M3 — Product** | Packaged `.bundle`, config-driven schema, tests, docs, tagged release |

---

## Suggested order of work

```
P0.2 timeout  ──┐
P0.1 attributes ─┼─→ P1.5 entity coverage ─→ P1.6 paper space/nested ─→ P1.7 structured output
P0.3 decision  ──┘                    │
                                      └─→ P1.4 parallel ─→ P2.8 resume ─→ P2.13 orchestrator
```

Start with **P0.2 (timeout)** even though it is less exciting than attributes: it removes a
failure mode that silently destroys long batches, and it is a prerequisite for parallelism.
