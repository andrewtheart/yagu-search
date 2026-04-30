# Phase 0 Baseline — RUST_PERF_IMPROVEMENTS_FROMRIP

Captured on **2026-04-29** with the pre-work bug fixes applied (snap-end correctness,
per-line cancel polling, Unicode case-fold default for `LiteralMatcher`). Raw data
lives in [QuickGrep.Benchmarks/results/perf-baselines.jsonl](../QuickGrep.Benchmarks/results/perf-baselines.jsonl).

## Environment

| | |
|---|---|
| Machine | ANDREWAWLAPTOP |
| OS | Windows 10.0.26200 |
| .NET | 10.0.7 |
| Cores | 24 |
| Test command | `dotnet test QuickGrep.Tests -c Release` |
| Synthetic tree | 50,000 files × 200 lines × ~668 MB (default) |

All scenarios use the native (Rust) scan path with `PreferNative=true`. No fallbacks
were observed in any scenario (`fallbackReason: null` everywhere).

## Headline numbers

Whole-tree scans across the 50K-file / 668 MB synthetic tree:

| Scenario | Throughput | Files/s | Matches/s | Notes |
|---|---:|---:|---:|---|
| `LiteralSearch` | 22 MB/s | 1,736 | 138,831 | "Test", case-insensitive |
| `CaseSensitiveLiteral` | 23 MB/s | 1,818 | 145,400 | "Test", case-sensitive |
| `RegexCaseSensitive` | 25 MB/s | 1,930 | 154,388 | `\bTest\w*\b` |
| `ContentOnly` | 25 MB/s | 1,930 | 154,358 | content-mode |
| `NoContextLines` | 25 MB/s | 1,999 | 159,870 | context=0 |
| `NoMatch` | 28 MB/s | 2,192 | — | impossible literal |
| `ParallelismLow` | 24 MB/s | 1,872 | 149,703 | 1-thread content |
| `ExcludeGlobFilter` | 23 MB/s | 1,811 | 144,812 | |
| `LargeFileExclusion` | 23 MB/s | 1,779 | 142,264 | 100 KB cap |
| `ComplexRegex` | 21 MB/s | 1,624 | 323,633 | timed out at 30s |
| `HalfMillionFiles` | 22 MB/s | 1,754 | 140,350 | 500K-file tree, timed out at 30s |

Few-file / large-file scans:

| Scenario | Throughput | Files | MB scanned |
|---|---:|---:|---:|
| `MediumFiles` | **5,116 MB/s** | 10 | 551 |
| `LargeFiles` | **2,589 MB/s** | 5 | 553 |
| `VeryLargeFiles` | **3,332 MB/s** | 3 | 667 |
| `MixedFileSizes` | **21,652 MB/s** | 30 | 2,266 (mostly cached) |

Listing-only scenarios:

| Scenario | Files/s | Files |
|---|---:|---:|
| `FileListerOnly` | 8,621 | 50,000 |
| `FileNameOnly` | 8,046 | 50,000 |

## Memory & GC

Across whole-tree scans, peak working set sits around **9 GB** with the synthetic
tree fully cached. GC behavior on the literal scenarios:

- Gen0: 300–320 collections per 30s (≈ 10/s)
- Gen1: 55–73
- Gen2: 4–5
- Allocation delta per scenario: 3–35 MB

`NoContextLines` has dramatically fewer Gen0 collections (69 vs ~310) — strong
signal that `before-context` allocations on the managed side are a real cost
(addressed by Phase 2).

## Behavioral confirmation

- All 170 Rust unit tests pass.
- 593/594 .NET tests pass on the first run; the lone failure (`MaxResultsCap_Throughput`)
  was a pre-existing test design flaw — the original `maxResults: 1_000` cap fired so
  early (~14 small files / ~187 KB / 0.1s) that the MB/s metric was meaningless. The
  test was fixed by raising the cap to 100,000 (scans ≈ 1,250 files / ≈ 17 MB);
  it now passes consistently while still asserting the cap fires before the full tree.
- No native fallbacks observed in any benchmark.

## Comparators for future phases

When evaluating Phase 1/2/6 changes, regression thresholds:

| Metric | Floor (whole-tree) | Floor (large-file) |
|---|---:|---:|
| MB/s | 20 | 2,000 |
| Files/s | 1,500 | — |
| Allocation delta | < 50 MB / 30s | — |
| Gen2 collections | ≤ 5 / 30s | — |

A successful phase keeps every existing test passing and improves at least one
of the headline metrics on a focused scenario (e.g. Phase 2 → `LiteralSearch`
allocation delta drops below 5 MB; Phase 1 → `LargeFiles` MB/s holds while
peak working set drops; Phase 4 → `ComplexRegex` MB/s rises above 25).

## What is *not* yet instrumented

The plan calls for "lightweight logging around native scan mode: mmap vs
buffered, bytes read, binary skipped, matches emitted, callback stops, elapsed
time." Today the C# side only sees `nativeFallthrough` outcome plus PerfMetrics
totals. Native-internal counters (mmap-vs-buffered, bytes-read-from-disk,
binary-skipped) are deferred to Phase 1, where the buffered scan path is
introduced and a counters struct can be threaded through `qg_search_file_stream`
without an ABI bump (the `userdata` callback can carry it, or a thread-local
counter exposed via `qg_get_last_scan_stats()` after each call).

This deferral keeps Phase 0 a pure measurement step with no code changes to the
scanner — see [RUST_PERF_IMPROVEMENTS_FROMRIP.md §Phase 0](RUST_PERF_IMPROVEMENTS_FROMRIP.md#phase-0---baseline-and-instrumentation)
"No functional changes yet."
