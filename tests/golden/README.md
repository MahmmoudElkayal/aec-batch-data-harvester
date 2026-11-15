# Golden baseline

The reference output of the harvester, captured by `tools/capture-golden.ps1`.

Every phase in the refactor plan is judged against these files.
They exist so that a refactor cannot silently change what the pipeline produces.

## What is here

| File | Contents |
|---|---|
| `<drawing>.csv` | The entity rows the pipeline produced for that drawing, header included, byte-for-byte as written |
| `<drawing>.attributes.csv` | The attribute rows for that drawing, same rule |
| `summary.csv` | Drawing, entity row/byte/SHA-256, attribute row/byte/SHA-256, and both file names |
| `README.md` | This file |

Current baseline: 6,748 entity rows (543,500 bytes merged) and 219 attribute rows
(8,595 bytes merged) across three drawings.

`summary.csv` is the quick check: if a hash changed, something changed. If a hash did not
change, nothing did.

## Capturing

```powershell
# harvest, then split both merged CSVs into one file per drawing
powershell -NoProfile -ExecutionPolicy Bypass -File tools\capture-golden.ps1

# compare only; write nothing
powershell -NoProfile -ExecutionPolicy Bypass -File tools\capture-golden.ps1 -Check

# re-split the existing CSVs without harvesting (fast, useful while reviewing a diff)
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
The baseline has been re-captured twice: after Phase 3 (one file per drawing, supervisor-owned
keys) and after Phase 5 (line/arc/circle/text/solid extraction, attributes, paper space).

## Format notes

- UTF-8, **no BOM**, CRLF line endings, one trailing CRLF - the bytes the pipeline writes
  by default. `AEC_HARVEST_UTF8_BOM=1` changes that for consumers who need it, and the
  baseline is captured with the default.
- Rows keep the order the pipeline emitted them; this is not sorted data.
- Quoting is preserved raw. The splitter parses the `Drawing` field only, so an escaped
  value elsewhere in a row is never rewritten.
- A drawing with no attributes still has a header-only `.attributes.csv`; its absence would
  make a complete drawing look interrupted to the resume check.
- The corpus is the three sample drawings in `Input_DWGs`, which is git-ignored. A machine
  without those files cannot re-capture, but can still read and diff the baseline.
