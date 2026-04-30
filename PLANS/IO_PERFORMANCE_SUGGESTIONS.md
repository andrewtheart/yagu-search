# I/O Performance Suggestions

Based on analysis of the VS diagnostic session at
`C:\Users\andre\OneDrive\Desktop\Report20260430-0421 (2)` and a code-review of
the I/O hot paths in `QuickGrep/Services/`.

## Diag-session contents

The session is a raw VS `.diagsession` (OPC zip, extracted) containing:

- `metadata.xml` — collectors: Managed metadata cache, CountersFile, EtlFile.
- `719e02dd-…​.counters` (3.98 MB) — VS `.NET counters` (GC heap size,
  ThreadPool thread count, working set).
- `sc.user_aux.etl` (7.10 GB) — ETW trace (no extracted CSV/JSON summary
  available; would need VS or PerfView to extract per-frame data).
- `MetadataCache/` — list of managed modules loaded by the profiled process.

Notable observation from the module list: `quickgrep_core.dll` (Rust) is **not**
in the metadata cache, but `System.Text.RegularExpressions.dll` is — meaning
this profile captured a run that fell back to (or partly used) the managed
search path. That's itself a finding worth confirming.

Without VS exporting the ETW into a function-level CSV, the suggestions below
are based on code review of the I/O hot paths cross-referenced against the
perf baselines in `QuickGrep.Benchmarks/results/perf-baselines.jsonl`.

---

## Bottlenecks & fixes (priority order)

### 1. `s_nativeGate` caps native concurrency at 8 — biggest I/O ceiling

[QuickGrep/Services/ContentSearcher.cs](QuickGrep/Services/ContentSearcher.cs#L33):
`s_nativeGate = new(8, 8)`. Combined with `Parallel.ForEachAsync` capped at
`min(8, ProcessorCount)`, **effective concurrent-file I/O = 8** on any
machine. NVMe drives saturate at 16–64 outstanding reads.

**Fix:** Raise to `Math.Min(32, ProcessorCount * 2)`. The original
justification (22 GB working set) was the *managed* MMF path with held decoded
text; the native path's mmaps are already tracked + munmap'd promptly.
Validate by re-running `VeryVeryLargeFiles` and `ParallelismLow`.

### 2. Per-file FFI overhead is the real per-file tax — switch to the new batch API

`qg_session_scan_paths_parallel` exists in Rust and `ScanPathsParallel` is
already wired in [QuickGrep/Native/NativeSearcher.cs](QuickGrep/Native/NativeSearcher.cs),
but `SearchService` still calls one FFI per file inside `Parallel.ForEachAsync`.
Per-file overhead:

- `Marshal.AllocHGlobal(sizeof(int))` + `Free` for the cancel flag
- `GCHandle.Alloc` for the sink
- `Task.Run` thread-pool hop
- semaphore wait/release
- `CancellationToken.Register` allocation

For 50K-file scenarios at ~1.7K files/s, that's roughly 30 µs/file of pure
overhead — meaningful share of the budget.

**Fix:** Feed batches of ~256–1024 paths to `NativeSearcher.ScanPathsParallel`
from `SearchService`. The Rust side already does work-stealing internally; you
keep one cancel int and one GCHandle per batch instead of per file.

### 3. Managed path opens the file 2–3× per scan

[QuickGrep/Services/ContentSearcher.cs](QuickGrep/Services/ContentSearcher.cs#L88):

1. `new FileInfo(filePath)` → `GetFileAttributesEx`
2. `fi.OpenRead()` to sniff binary → close
3. `new FileStream(...)` to scan → close
4. `EncodingDetector.DetectEncoding(fs)` reads + seeks

Three `CreateFile` calls + a seek before scanning a single byte for matches.
On HDD, each `CreateFile` is ~0.2 ms.

**Fix:** Open once with `FileOptions.SequentialScan | FileOptions.Asynchronous`,
peek the first 8 KB into a stack buffer, run binary detection AND BOM/encoding
detection on that one buffer, then keep the FileStream for scanning
(StreamReader works fine after a seek to 0 or BOM offset).

### 4. `FileStream` is sync-handle — every `ReadAsync` hops the thread pool

[QuickGrep/Services/ContentSearcher.cs](QuickGrep/Services/ContentSearcher.cs#L156):
no `FileOptions.Asynchronous` → `ReadAsync` is fake-async (queues work to
ThreadPool). With `Parallel.ForEachAsync` already on TP threads, this causes
context-switch storms on small files.

**Fix:** Add `FileOptions.Asynchronous` to that constructor (and the MMF view
path) OR drop async entirely on the managed path and run `SearchLines`
synchronously inside `Task.Run` (matches the native path style).

### 5. MMF gate of 4 is too tight at the 1 MB threshold

[QuickGrep/Services/ContentSearcher.cs](QuickGrep/Services/ContentSearcher.cs#L24):
`s_mmfGate = new(4, 4)` for files ≥ 1 MB. In a tree of source files where
only a few exceed 1 MB, fine — but for log/CSV trees this is a hard 4-wide
bottleneck.

**Fix:** Either raise the threshold to 8 MB (so it only applies to genuinely
large files) or raise the gate to 16. The Rust side's mmap doesn't gate at
all — the cap was a managed-MMF address-space concern.

### 6. `EncodingDetector` re-reads the file head

`DetectEncoding(fs)` reads N bytes then seeks back to 0 → on
`FileOptions.SequentialScan` streams this defeats the readahead heuristic the
OS just set up. Combined with a separate `BinaryDetector.IsBinary` open, the
first ~8 KB of every text file is read twice or three times.

**Fix:** Single 8 KB peek shared by binary detection, BOM detection, and
encoding heuristic, then resume sequential scan from the buffer. You already
do this in Rust; mirror it in managed.

### 7. Per-file `FileInfo.Length` round-trip during enumeration

`FileLister` enumerates paths; `ContentSearcher.SearchFileWithStatsAsync` then
calls `new FileInfo(path)` again to get length and `LastWriteTime`. That's a
second `GetFileAttributesEx` per file. The Everything SDK already returned
size + mtime; the .NET enumerator can too via
`EnumerationOptions { ReturnSpecialDirectories = false }` +
`FileSystemEnumerable<>` returning `(path, length, mtime)`.

**Fix:** Have `FileLister` yield `(string Path, long Length, DateTime Mtime)`
tuples (a `record struct`) and pass them to `ContentSearcher` so it skips the
second stat. Saves ~50 µs/file.

### 8. Per-file unmanaged cancel-int allocation

Around [QuickGrep/Services/SearchService.cs](QuickGrep/Services/SearchService.cs#L309):
`Marshal.AllocHGlobal(sizeof(int))` + a `CancellationToken.Register` closure
allocation per file.

**Fix:** One pinned `int` per worker thread (via `ThreadLocal<IntPtr>`), or —
once the batch API is wired — one per batch. Saves AllocHGlobal/Register pair
on every file.

---

## Suggested order of implementation

1. **(2) Wire batch API in `SearchService`** — biggest mechanical win;
   eliminates issue 8 too.
2. **(1) Raise `s_nativeGate`** — one-line change, immediately validates with
   the `ParallelismLow` benchmark.
3. **(3) + (6) Single-peek open in managed path** — most code change; low risk.
4. **(5) Tune MMF gate / threshold** — one-line change.
5. **(7) Stat-once enumeration** — touches `IFileLister` API; defer if (2)
   lands well.
6. **(4) `FileOptions.Asynchronous`** — one-line change.

## Validation plan

For each change above:

- Run `dotnet test QuickGrep.Tests/QuickGrep.Tests.csproj -c Release` (full
  suite must stay green).
- Re-run `QuickGrep.Benchmarks` and append to
  `QuickGrep.Benchmarks/results/perf-baselines.jsonl`; compare
  `totalMatches/elapsedSeconds` (NOT `mbPerSecond` — match-callback cost
  dominates wall time when match density differs across runs).
- Watch process working set during `VeryVeryLargeFiles` to confirm raising
  `s_nativeGate` doesn't regress unmanaged memory.

## Better profiling next time

The current `.diagsession` doesn't include FileIO ETW provider data — only
.NET counters + a generic CPU sampling ETL. To get actionable per-syscall data:

- Use **PerfView** with `/KernelEvents=FileIOInit,FileIO,DiskIO` to capture
  per-file `CreateFile`/`ReadFile` latencies.
- Or use the VS **"File IO"** collector explicitly (not enabled in this
  session).
- Export the resulting trace to CSV (PerfView → Events → File → Save View
  As CSV) so it can be diffed across runs.
