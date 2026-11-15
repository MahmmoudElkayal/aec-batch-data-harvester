# Golden baseline

The reference output of the harvester, captured by `tools/capture-golden.ps1`.

Every phase in [`docs/refactor-plan.md`](../refactor-plan.md) is judged against these files.
They exist so that a refactor cannot silently change what the pipeline produces.

## What is here

| File | Contents |
|---|---|
| `<drawing>.csv` | The rows the pipeline produced for that drawing, header included, byte-for-byte as written |
| `summary.csv` | Drawing, row count, byte count, SHA-256, and file name for each baseline |
| `README.md` | This file |

`summary.csv` is the quick check: if a hash changed, something changed. If a hash did not
change, nothing did.

## Capturing

```powershell
# harvest, then split the merged CSV into one file per drawing
powershell -NoProfile -ExecutionPolicy Bypass -File tools\capture-golden.ps1

# compare only; write nothing
powershell -NoProfile -ExecutionPolicy Bypass -File tools\capture-golden.ps1 -Check

# re-split the existing CSV without harvesting (fast, useful while reviewing a diff)
powershell -NoProfile -ExecutionPolicy Bypass -File tools\capture-golden.ps1 -SkipRun
```

`-Check` exits `0` when the output matches, `2` when it has drifted, `1` on error. That makes
it usable as a pre-commit or CI gate.

## The rule

**A behaviour-preserving phase must reproduce these files exactly.** If it does not, the
phase has a bug, not a new baseline.

**A behaviour-changing phase ends by re-capturing them** - but only after the diff has been
read and explained in the commit message. Re-baselining is a reviewed decision, never a way
to make a failing diff go away.

The plan lists which phases may move the baseline: 1, 3 and 5. Phases 2, 4 and 6 must not.

## Format notes

- UTF-8, **no BOM**, CRLF line endings, one trailing CRLF - the bytes the pipeline writes.
- Rows keep the order the pipeline emitted them; this is not sorted data.
- Quoting is preserved raw. The splitter parses the `Drawing` field only, so an escaped
  value elsewhere in a row is never rewritten.
- The corpus is the three sample drawings in `Input_DWGs`, which is git-ignored. A machine
  without those files cannot re-capture, but can still read and diff the baseline.
