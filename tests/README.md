# Tests

Three layers, all runnable offline. None needs a NuGet feed.

| Folder | What it is | Needs AutoCAD |
|---|---|---|
| `golden/` | The pipeline's output frozen as a baseline; every phase is diffed against it | Yes, to re-capture |
| `Harvest.Checks/` | A console program that checks the pure logic and the CSV/JSON format | No |
| `../tools/test-*.ps1` | End-to-end suites that drive the real pipeline through Core Console | Yes |

---

## `golden/` - behaviour

The reference output of the real pipeline: one entity CSV and one attribute CSV per drawing.
See [golden/README.md](golden/README.md) for what the files are and when they may change.

```powershell
# harvest, then split both merged CSVs into one file per drawing
powershell -NoProfile -ExecutionPolicy Bypass -File tools\capture-golden.ps1

# compare only, write nothing; exit 2 means the output drifted
powershell -NoProfile -ExecutionPolicy Bypass -File tools\capture-golden.ps1 -Check
```

A behaviour-preserving change must leave these files byte-identical. A change that is
*supposed* to alter the output ends by re-capturing them, after the diff has been read.
The baseline has been re-captured twice: after Phase 3 (one file per drawing) and after
Phase 5 (coverage). Phases 2, 4 and 6 moved nothing.

---

## `Harvest.Checks/` - format and policy

A plain console program, not a test framework: `dotnet restore` for xunit/mstest hangs on
this machine, and a harness that cannot run is worse than none. It references the plug-in
project and reaches its `internal` members, so the checks exercise the shipping code rather
than a copy of it.

```powershell
dotnet build AEC_Batch_Data_Haravester.slnx
tests\Harvest.Checks\bin\Debug\net10.0-windows\Harvest.Checks.exe
```

Exit code `0` = every check passed, `1` = at least one failed. Each failure prints what was
expected and what happened. 40 checks today.

What it covers:

| Area | Checks |
|---|---|
| Model space guard | an unresolved id throws and names the drawing; a resolved one does not throw |
| Status JSON | parses; success carries rows/attrs/skipped/ms/units/unitCode; failure omits the units; a hostile drawing name survives; the payload is one line |
| CSV escaping | plain values unquoted, comma/quote/newline quoted, quotes doubled, empty stays empty |
| Numbers | invariant culture, three decimals, null renders empty |
| Excel guard | prefixes only `= + - @` and tab, is off by default, and never touches a numeric column |
| Environment switches | on only for the exact value `1`; `"true"` stays off |
| Status keys | read verbatim; a missing or blank key throws instead of falling back to a file name |
| Batch manifest | `key\|path` parsing, duplicate/blank key rejection, path-component rules |
| Timeout verdict | marker selects the blamed drawing; a reported drawing is never blamed |
| Batch summary | counts and per-drawing time parse; an empty chunk does not divide by zero |
| Schema | header names unique and non-blank; a row's field count equals the header's; values land in the right columns; the attribute schema is the documented long format |

The schema checks are the point of the folder: a column added to the header but not to the
row (or the reverse) is a silent schema break that no drawing will complain about.

### Proving the runner can fail

```powershell
tests\Harvest.Checks\bin\Debug\net10.0-windows\Harvest.Checks.exe --self-test
```

Runs one deliberately failing check and confirms it is reported. Without this, "40 passed"
could equally mean the checks never ran.

---

## End-to-end suites

All of these run the real `RunPipeline.bat`, leave the tree in a clean freshly-harvested
state, and exit non-zero on the first failed assertion.

| Script | Property |
|---|---|
| `tools/test-batch-engine.ps1` | many drawings per process; golden parity; `.fail` isolation; resume re-runs nothing finished; a timed-out chunk is killed and blamed on the right drawing; recovery restores the baseline |
| `tools/test-batch-integrity.ps1` | two identical runs produce identical bytes; two `Plan.dwg` files stay separate; reserved names are refused; keys are stable when a drawing is added; a killed run publishes nothing partial; a row file without `.ok` is not merged - asserted directly for both file families |
| `tools/test-failure-paths.ps1` | a truncated DWG is one `.fail` with the full trace; an unwritable output folder fails every drawing and says why; the clean run recovers with no stale failure files |
| `tools/test-coverage.ps1` | row counts match the census per drawing and type; 219 attributes on 144 references with the probe values intact; every attribute handle resolves; paper space is proved with a synthetic fixture; every `EntityKind` has exactly one extractor and one registration |

They are intentionally sequential: they share `Output\` and `Input_DWGs\`.
