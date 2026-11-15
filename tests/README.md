# Tests

Two layers, both runnable offline. Neither needs a NuGet feed.

| Folder | What it is | Needs AutoCAD |
|---|---|---|
| `golden/` | The pipeline's output frozen as a baseline; every phase is diffed against it | Yes, to re-capture |
| `Harvest.Checks/` | A console program that checks the pure logic and the CSV/JSON format | No |

---

## `golden/` - behaviour

The reference output of the real pipeline, one CSV per drawing. See
[golden/README.md](golden/README.md) for what the files are and when they may change.

```powershell
# harvest, then split the merged CSV into one file per drawing
powershell -NoProfile -ExecutionPolicy Bypass -File tools\capture-golden.ps1

# compare only, write nothing; exit 2 means the output drifted
powershell -NoProfile -ExecutionPolicy Bypass -File tools\capture-golden.ps1 -Check
```

A behaviour-preserving change must leave these files byte-identical. A change that is
*supposed* to alter the output ends by re-capturing them, after the diff has been read.

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
expected and what happened.

What it covers:

| Area | Checks |
|---|---|
| Model space guard | an unresolved id throws and names the drawing; a resolved one does not throw |
| Status JSON | parses; success carries rows/skipped/ms/units/unitCode; failure omits the units; a hostile drawing name survives; the payload is one line |
| CSV escaping | plain values unquoted, comma/quote/newline quoted, quotes doubled, empty stays empty |
| Numbers | invariant culture, three decimals, null renders empty |
| File names | illegal characters replaced, blank input falls back to `drawing` |
| Schema | header names unique and non-blank; a row's field count equals the header's; values land in the right columns |

The last one is the point of the whole folder: a column added to the header but not to the
row (or the reverse) is a silent schema break that no drawing will complain about.

### Proving the runner can fail

```powershell
tests\Harvest.Checks\bin\Debug\net10.0-windows\Harvest.Checks.exe --self-test
```

Runs one deliberately failing check and confirms it is reported. Without this, "16 passed"
could equally mean the checks never ran.
