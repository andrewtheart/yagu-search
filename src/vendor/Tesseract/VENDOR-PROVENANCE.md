# VENDOR PROVENANCE — Tesseract (managed wrapper)

**This tree is a one-time snapshot of upstream, with Yagu-local edits applied on top.
It has never been refreshed from upstream since it was taken.**

| | |
|---|---|
| Upstream | <https://github.com/charlesw/tesseract> |
| Vendored release | `5.2.0` |
| **Base commit** | **`2c993543f7fa66576a8890a6c4ab053c4598aaed`** |
| Commit date | 2022-11-08 |
| Commit subject | `Merge branch 'feature/579-tesseract-5.2' into develop` |
| Tag at that commit | `5.2.0` |
| Bundled native engine | Tesseract `5.0.0`, Leptonica `1.82.0` |

## How this was determined

Every vendored file was hashed as a git blob (both LF and CRLF variants, to neutralize checkout
line-ending conversion) and compared against the blob SHAs of every commit reachable from upstream
`HEAD`. Commit `2c993543` is the unique maximum: **157 of 161 vendored files are byte-identical**
to their upstream counterparts there, and the `5.2.0` tag points at it exactly.

## Yagu-local divergences from the base commit

- `Directory.Build.props` — added by Yagu; no upstream counterpart. Keeps Yagu's repo-wide analyzer
  and warnings-as-errors settings off third-party source.
- `src/Tesseract/Tesseract.csproj`
- `src/Tesseract/x64/tesseract.exe`
- `src/Tesseract/x86/tesseract.exe`

## Deliberately not vendored

Upstream test fixtures (`src/Tesseract.Tests/tessdata/*.traineddata`), stray NUnit
`InternalTrace.*.log` files, and `src/packages/repositories.config`.

## Refresh rule

Do not overwrite this directory with a newer upstream checkout. Reconstruct the base commit above,
replay this snapshot on top of it as a local patch layer, then merge the desired upstream release.

The managed wrapper pins native DLL names and P/Invoke expectations, so the bundled native engine
must not be bumped independently of the wrapper. A native-engine update is a separate wrapper/ABI
migration requiring x64 and x86 OCR validation.
