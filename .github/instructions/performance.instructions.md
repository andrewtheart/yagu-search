---
description: "Yagu search-engine performance & hot-path regression guards. Use when: editing the search hot path, yagu-core / yagu_core.dll, Rust scanner, NativeSearcher / FFI / ABI, SearchService, FileLister, ContentSearcher, ResultStore, SearchResultCollection, FileGroup, DirectOutputSink, Everything SDK query, throughput, eviction, utf16_col, source_match_start, perf regression, benchmark."
applyTo: "yagu-core/**, Yagu/Services/SearchService.cs, Yagu/Services/FileLister.cs, Yagu/Services/ContentSearcher.cs, Yagu/Services/ResultStore.cs, Yagu/Services/DirectOutputSink.cs, Yagu/Native/**, Yagu/Models/SearchResultCollection.cs, Yagu/Models/FileGroup.cs, Yagu.Benchmarks/**"
---

# Yagu — Performance & Hot-Path Regression Guards

The search hot path is: **`yagu_core.dll` (Rust per-file matcher)** ← `NativeSearcher` (FFI) ←
`ContentSearcher` / `SearchService` ← `FileLister` (enumeration) → `ResultStore` /
`SearchResultCollection` / `FileGroup` (result model) → GUI drain, or `DirectOutputSink` (CLI).

**Managed CPU is negligible (~0.2%).** The cost is (a) the Rust scan + the per-open kernel filter
stack, and (b) the result-model memory / eviction path. Optimize those; do **not** micro-optimize
managed per-match marshalling (it is bounded by `MaxResults`).

## Do not reintroduce these regressions (each has a guard test)

1. **Per-match `utf16_col` in the Rust scan hot loop.** `source_match_start` MUST return the
   `u32::MAX` sentinel in the common (line-bytes-present) path and let C# `DecodeMatchLine` compute
   the UTF-16 column lazily. The eager path (windowed / `metadata_only` lines) MUST go through
   `scan::Utf16ColCursor` (one cursor per line → O(line), not O(line²)). **Never call
   `utf16_col(line, byte_start)` per match.** Guards: Rust unit
   `source_match_start_defers_to_managed_side_in_common_path` (always runs) +
   `ContentSearchPerformanceRegressionTests` (opt-in `YAGU_RUN_PERF_REGRESSION=1`; ASCII-vs-invalid-UTF-8
   engine-time ratio must stay ≤ 1.8).
2. **`!attrib:h` on an unnarrowed Everything sweep.** It forces a full un-indexed attribute scan
   (~42 s on C:\). Keep the `!attrib:h` term ONLY on **narrowed** queries (extension / literal-name /
   include-glob present); on the unnarrowed sweep the **native scanner** skips hidden files itself from
   the metadata it already reads (`QgOptions.skip_hidden`, zero extra syscall). Guard:
   `FileListerEsExeGateTests`.
3. **Everything SDK per-page re-query loop.** The classic SDK keeps no server-side cursor, so paging
   re-evaluates the *whole* search each page (~2 h on full C:\). Use a **single**
   `SetOffset(0)` / `SetMax(userMax)` / `Query(bWait:true)`, then read results by index. Guard:
   `FileListerCoverageTests.Sdk_NoMaxFiles_RequestsAllResultsInSingleQuery`.
4. **Result-eviction write storm.** Keeping results compact in memory (so `ResultStore` doesn't page
   to disk) is the dominant throughput lever — an eviction storm craters ~1140 MB/s → ~300 MB/s. Do
   NOT add per-match managed allocations to `SearchResultCollection` / `FileGroup`; prefer
   source-backed compact stubs + batched (multi-item) collection notifications. `ResultStore` temp
   defaults to a separate disk (`YAGU_RESULTSTORE_TEMP`), never the scanned drive.
5. **Unbuffered per-match output syscalls (CLI).** `DirectOutputSink` output is wrapped in a
   `BufferedStream` **at the `CliRunner` call site** (16× speedup) — keep the buffering at the call
   site, because sink unit tests read the `MemoryStream` mid-scan. Also: `--` separators are emitted
   only when context (`-A/-B/-C`) is on, and same-line matches fold to one emitted line (ripgrep parity).

## FFI / ABI discipline (`yagu-core` ↔ `Yagu/Native`)

- Bump `qg_abi_version` whenever the `QgOptions` / `QgSession` / `QgMatchView` layout changes (C#
  checks it; a test asserts it). A stale `yagu-core/target/<profile>/yagu_core.dll` makes an ABI change
  look broken — Yagu.Tests copies the profiling DLL.
- `MMAP_THRESHOLD_BYTES` (in `ffi.rs`) is coupled to Rust test fixtures: fixtures that must exceed it
  are sized `MMAP_THRESHOLD_BYTES + 16`. Audit those when you change the threshold.
- Native streaming cancellation must join the Rust workers before freeing C# session / cancel /
  `GCHandle` state (`FinishStreamingScanner` in cleanup; `qg_scanner_destroy` stays defensive).

## Backend pushdown & concurrency

- Push filename-only include globs to Everything (`FileLister.BuildEverythingIncludeFileNameFilter`);
  **bail to the managed post-filter** (correctness over speed) when a token has a path separator,
  quote, or wildcard-with-space.
- Push the content size ceiling as a `size:<=N` predicate (size IS indexed, ~100 ms); request
  size/date columns only when a size/date filter is active.
- Streaming IO workers = `min(64, parallelism * IoOversubscriptionMultiplier)` (SSD ×1 / HDD ×2 by
  default). Oversubscription hides per-open filter latency on cold full-drive sweeps but raises peak
  CPU/heat on warm searches — don't change the default without measuring both.
- Emit scan-completion / control signals **out-of-band** from the bounded `MatchBatch` event queue so
  the GUI timer can stop when workers finish while results are still draining.

## Measuring (before claiming a perf win)

- Canonical metric = the verbose `[Forwarder] Throughput forwarded=N @ elapsed` log line
  (`LogLevelIndex=3` / Verbose). The pressure-log scanned/matches counter is stale — ignore it.
- Profile the **native / filter stack** (ETW `WPR -start CPU`, parsed by `scripts/EtlParser`) or the
  **result-model allocation** path (`dotnet-trace` / `dotnet-gcdump`), not managed match code.
- Perf throughput benchmarks live in **Yagu.Benchmarks** (`[Trait("Category","Slow")]`); UI budget
  guards in `Yagu.Tests/UiResultPerformanceTests.cs` (env-tunable budgets, e.g. `YAGU_UI_PERF_INGEST_MS`).
  Both are skipped by the iterative `--filter "Category!=Slow"` run — run them explicitly to validate a
  perf change (see the testing instruction).
- Profile **warm with ample free RAM** so results stay in memory. Do NOT drop the standby cache first —
  it creates artificial memory pressure that trips the eviction path and gives a false throughput
  collapse.
