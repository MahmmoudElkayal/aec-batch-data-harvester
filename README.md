# AEC Batch Data Harvester

Headless batch data extraction for AutoCAD 2026 drawings. Point it at a folder of DWGs,
get a single CSV of every block, polyline and (soon) attribute in them — with no AutoCAD
window, no dialogs and no modification of the source files.

Built as an AutoCAD 2026 managed .NET plug-in that runs both inside the desktop product
(`NETLOAD` + `Extract_BOM`) and inside **AutoCAD Core Console** (`accoreconsole.exe`), which
is what makes unattended batch processing possible.

> **Status: early prototype.** The extraction pipeline and the batch runner work end to end.
> Verified against 3 sample drawings: **3,278 rows, 0 duplicates, ~2 s per drawing.**
> The set of extracted entity types is deliberately small while the data schema is being decided.

---

## What it does

| | |
|---|---|
| **Reads** | DWG files without opening them in the AutoCAD UI |
| **Finds** | block references (name, layer, handle, insertion point) and LWPOLYLINEs (layer, handle, length) |
| **Writes** | one flat, append-safe CSV — header written once, RFC 4180 quoting, UTF-8 without BOM |
| **Never writes** | to the drawings themselves (`/readonly` + read-only transactions) |
| **Reports** | per-drawing success/failure + row count, plus a per-drawing log |

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

Results land in `Output\Extracted_BOM.csv`.

### Runner flags

```bat
RunPipeline.bat              :: harvest everything directly in Input_DWGs
RunPipeline.bat /r           :: ... including sub-folders
RunPipeline.bat /append      :: keep appending instead of resetting the CSV
RunPipeline.bat /new         :: rebuild the DLL first
RunPipeline.bat /q           :: do not pause at the end
```

The runner auto-detects the newest `Debug`/`Release` build, regenerates the Core Console
script so the `NETLOAD` path can never go stale, logs every drawing to `Logs\`, and prints a
processed / succeeded / failed summary.

### Running a single drawing by hand

```bat
"C:\Program Files\Autodesk\AutoCAD 2026\accoreconsole.exe" ^
    /i "Input_DWGs\MyDrawing.dwg" ^
    /s "Scripts\RunExtraction.scr" ^
    /readonly  < nul
```

## Output schema

`Output\Extracted_BOM.csv`

| Column | Meaning |
|---|---|
| `Drawing` | source DWG file name |
| `EntityType` | `Block` or `Polyline` |
| `Name` | block name (blank for polylines) |
| `Layer` | object layer |
| `Handle` | DWG handle, for traceability back to the drawing |
| `X` `Y` `Z` | insertion point (blocks only) |
| `Length` | polyline length (polylines only) |

```csv
Drawing,EntityType,Name,Layer,Handle,X,Y,Z,Length
Assembly Sample.dwg,Polyline,,0,A4B,,,,306.124
Assembly Sample.dwg,Block,BLURB,0,A4C,13.500,2.100,0.000,
```

Each drawing also gets `Output\_status\<drawing>.ok` or `.fail` containing the row count and
target file — this is what the runner uses for pass/fail, because Core Console's exit code is
not reliable.

## How it works

```
RunPipeline.bat
   └─ for each *.dwg in Input_DWGs
        └─ accoreconsole.exe /i <dwg> /s <generated .scr> /readonly   < nul
             └─ .scr:  SECURELOAD 0
                       NETLOAD "…\AEC_Batch_Data_Haravester.dll"
                       Extract_BOM
                           └─ Extract_BOM command
                                ├─ HostApplicationServices.WorkingDatabase
                                ├─ read-only Transaction → BlockTable → model space
                                ├─ BlockReference / Polyline  → CSV rows
                                └─ append to Output\Extracted_BOM.csv
```

One process per drawing means a corrupt or crashing DWG cannot take down the batch.

## Design notes (things that are easy to get wrong)

These were found by testing, not by reading the docs:

1. **A trailing blank line in a `.scr` repeats the last command.** A bare `Enter` at the
   `Command:` prompt re-runs the previous command, so `Extract_BOM` executed **twice** per
   drawing — exactly 2× the rows. The script must end without a blank line, and Core Console
   must be given `stdin` from `NUL` so it does not wait for input forever.
2. **Core Console swallows managed `Console.WriteLine`.** Its own banner reads
   `StdOutConsoleMode: processed-output: disabled`, and writes from .NET never reach the log.
   Hence the status files.
3. **`SECURELOAD` blocks `NETLOAD`** from any folder not in `TRUSTEDPATHS`. Set `SECURELOAD 0`
   in the script for development; for production put the DLL in a trusted path instead.
4. **`/readonly`** — batch harvesting should never be able to modify a source drawing.
5. **Never reference `AcMgd.dll`** in code that must run in Core Console, and always set
   `Copy Local = false` on `accoremgd` / `acdbmgd` / `acmgd` or AutoCAD crashes on load.

## Repository layout

```
AEC_Batch_Data_Haravester.slnx          solution
AEC_Batch_Data_Haravester/
    AEC_Batch_Data_Haravester.csproj    net10.0-windows, x64, AutoCAD refs
    BomExtractor.cs                     the Extract_BOM command
RunPipeline.bat                         batch runner (entry point)
Scripts/RunExtraction.scr               Core Console script (auto-generated)
Input_DWGs/                             drop drawings here        (git-ignored)
Output/                                 CSV + status files        (git-ignored)
Logs/                                   per-drawing logs          (git-ignored)
docs/autocad-2026/                      study of the ObjectARX 2026 documentation set
docs/capabilities.md                    measured capability report + known issues
tools/build-docs-index.ps1              regenerates the documentation indexes
```

`docs/autocad-2026/README.md` is a full map of the AutoCAD 2026 / ObjectARX documentation:
45,844 pages indexed, 29 managed namespaces → 18,925 reference pages, plus platform and
execution-host constraints.

[`docs/capabilities.md`](docs/capabilities.md) is the honest status report: what is verified,
what is missing, known issues, measured performance and a prioritised roadmap.

## Roadmap

Prioritised plan, horizons and the decisions still blocking work are in
**[docs/roadmap.md](docs/roadmap.md)**. The short version:

1. **Block attributes** — turns rows into a real BOM
2. **Per-drawing timeout** — a hung DWG currently stalls the batch
3. **Parallel workers** — serial is ~1–2 s per drawing
4. **Entity coverage** — `Polyline2d/3d`, `Line`, `Arc`, `Table`, `MText`
5. **Paper space, nested blocks, xrefs** — needed before numbers are trustworthy

## License

MIT — see [LICENSE](LICENSE).
