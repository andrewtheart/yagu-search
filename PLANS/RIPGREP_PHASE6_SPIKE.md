# Phase 6 Spike — `grep-searcher` / `grep-regex` Feasibility

Status: **complete** (proof-of-concept only; not adopted yet).
Source: [quickgrep-core/src/scan_grep.rs](../quickgrep-core/src/scan_grep.rs)
Feature flag: `grep_crates` in [quickgrep-core/Cargo.toml](../quickgrep-core/Cargo.toml)

## Goal

Per [PLANS/RUST_PERF_IMPROVEMENTS_FROMRIP.md §Phase 6](RUST_PERF_IMPROVEMENTS_FROMRIP.md): decide whether to replace QuickGrep's in-tree scanner with ripgrep's library crates *before* committing to Phases 1, 2, and 3 (which would each invest in our own scanner).

## What Was Built

A minimal alternate scan path `scan_grep::scan_bytes_grep(bytes, pattern, options, emit)` that:

1. Builds a `grep_regex::RegexMatcher` honouring the same case-sensitivity / literal-vs-regex semantics as `scan::build_matcher`.
2. Drives a `grep_searcher::Searcher` (with `line_number(true)` and binary detection).
3. Routes match events through a custom `Sink` that translates `SinkMatch` → `MatchRecord`, including walking `Matcher::find` repeatedly to emit one `MatchRecord` **per match per line** (matching our exact-match-count contract).
4. Honours `max_results` by returning `Ok(false)` from the sink.

Out of scope for the spike (deferred): context (before/after), per-line cancellation polling, native UTF-16 path. These were all intentionally skipped to keep the spike small.

## Pinned Versions (all dual MIT / Unlicense — verified on crates.io)

```toml
grep-searcher = "=0.1.16"
grep-regex    = "=0.1.14"
grep-matcher  = "=0.1.8"
```

Activated only when `--features grep_crates` is passed; default builds remain unchanged.

## Results

| Metric | Outcome |
|---|---|
| Compiles cleanly with feature on | ✅ |
| Default-feature build & test suite untouched | ✅ 170/170 |
| Parity unit tests (literal CI, literal CS, regex, max-results, multi-match-per-line) | ✅ 5/5 |
| Release cdylib size | 1291 KB → 1424 KB (+133 KB, +10%) |
| New transitive deps | `regex-automata`, `encoding_rs`, `encoding_rs_io`, `log` |

## Empirical Throughput (Rust-only micro-bench, release, 24-core)

See [quickgrep-core/tests/phase6_bench.rs](../quickgrep-core/tests/phase6_bench.rs).

| Workload (64 MB, 16 iters) | in-tree | grep-searcher | speedup |
|---|---:|---:|---:|
| Literal sparse, case-sensitive | 2,954 MB/s | 5,749 MB/s | **1.95x** |
| Literal NO MATCH | 3,272 MB/s | 14,225 MB/s | **4.35x** |
| Literal case-insensitive | 1,449 MB/s | 2,966 MB/s | **2.05x** |
| Regex `\bHOT\w+` | 1,650 MB/s | 3,117 MB/s | **1.89x** |

**Many-small-files (100,000 × 8 KB) — the "millions of files" workload:**

| Mode | Throughput | Files/s |
|---|---:|---:|
| in-tree (per-call) | 3,333 MB/s | 408,744 |
| grep-searcher (per-call build, naive spike) | 2,068 MB/s | 253,581 (**0.62x**) |
| grep-searcher (`Searcher`+`Matcher` REUSED) | **15,535 MB/s** | **1,905,027 (4.66x)** |

**Critical takeaway:** grep-searcher only wins on small files when the `Searcher` and `RegexMatcher` are reused across files. The naive per-call build path is *slower* than the in-tree scanner. Production wiring **must** use a long-lived `Searcher` per-thread (the FFI "session" path is the right shape; the streaming/packed paths need to hoist construction out of the per-file loop).

## Findings

1. **API fits.** `grep-searcher`'s `Sink` model matches our match-by-match emission. We can preserve QuickGrep's "one row per match" contract by re-running `Matcher::find` over the line bytes.
2. **Free wins available if adopted.** `grep-searcher` brings auto-mmap-vs-buffered policy, UTF-16 BOM transcoding (via `encoding_rs_io`), binary detection, and rolling buffer for free — these are exactly Phases 1, 2 (partial), and 5 from the plan.
3. **Context handling not yet validated.** `grep-searcher`'s before/after context arrives as separate `SinkContext` events, not as `Vec<Vec<u8>>` attached to the match. Wiring this up will require a small ring-buffer adapter in our sink (~30–50 LOC) — manageable, but not free.
4. **Cancellation handle is coarser.** `grep-searcher` polls the sink *per match*, not per line. Long no-match scans will be slower to react to cancellation. Mitigations: (a) accept the coarser granularity (cancel on match boundaries), (b) keep our scanner for the cancellation-sensitive non-match-heavy case, or (c) wrap the input slice in a `Read` impl that aborts. Option (a) is probably fine in practice because grep-searcher already chunks the input.
5. **Build cost.** +~3s clean compile time, +133 KB release `.dll`. Acceptable.
6. **Semver risk acknowledged.** Versions pinned with `=`; manual review on every bump is part of the maintenance contract.

## Recommendation

**REJECTED. Keep the in-tree scanner.** A real-world C:\\ scan (5-minute budget) shows the in-tree scanner is **1.17x faster** than grep-searcher on actual file-system content, and that scanning is **<0.5% of total wall time** anyway — I/O dominates 350:1.

The synthetic 64 MB corpus benchmarks where grep-searcher won 2-4x do not predict real-world performance because:
- Most C:\\ files are small (<10 KB) or binary; grep-searcher's per-file setup overhead amortizes poorly.
- Binary detection bails out 75%+ of files; both scanners pay that cost identically.
- Cold-disk I/O dominates (296 / 300 s in the test run), so improving scan throughput cannot move wall-clock results.

See "Real-world C:\\ benchmark" below.

## Real-World C:\\ Benchmark (5-minute wall clock)

Test: [quickgrep-core/tests/cdrive_compare.rs](../quickgrep-core/tests/cdrive_compare.rs). Walks `C:\`, reads each file once (≤10 MB), runs both scanners on the same in-memory bytes (alternating order), with `skip_binary=true` and reused matcher/session.

| Metric | in-tree | grep-searcher | grep / in-tree |
|---|---:|---:|---:|
| Files scanned | 34,946 | 35,150 | — |
| Bytes scanned | 736.75 MB | 760.07 MB | — |
| Matches found (`test`, CI) | 125,983 | 145,124 | — |
| Scan-only CPU time | **0.70 s** | 0.84 s | 1.20x |
| Scan throughput | **1,058 MB/s** | 910 MB/s | **0.86x (slower)** |
| Files/sec (scan only) | **50,195** | 42,061 | **0.84x (slower)** |

**I/O dominates:** 296.17 s of 300 s wall time was `std::fs::read`. Walker visited 68,551 entries, processed 35,000 files (~117 files/s wall-clock).

**Where the real performance ceiling is:**
1. **I/O parallelism.** Wall-clock files/s = 117. The scan can sustain 50,000/s. The bottleneck is single-threaded sequential reads.
2. **Pre-mmap binary probe.** Most files (26,453 / 35,000 = 75%) were binary-skipped *after* full read. Reading the first 4-8 KB and probing before allocating the full read would cut I/O substantially.
3. **Extension / known-folder pre-filter.** Skipping `*.dll`, `*.exe`, `*.pdb`, `*.cab`, `*.iso`, `Windows\WinSxS`, etc. before reading.
4. **Memory mapping over full reads** for files >a few hundred KB.

None of these benefit from swapping scanners.

Rationale:

- It collapses Phases 1 (buffered scanning), 2 (borrowed views), and 5 (UTF-16 + binary probe) into a single dependency upgrade, with battle-tested code paths.
- Parity is demonstrated for the core operators that QuickGrep actually uses.
- The remaining work is well-scoped: implement context buffering in the sink, and route the FFI through `scan_grep::scan_bytes_grep` instead of `scan::scan_bytes_ex`.
- Phase 3 (ABI v3 with byte offsets and spans) is still independently valuable and complementary — `SinkMatch::absolute_byte_offset()` gives us exactly the byte offset we need.
- Phase 4 (regex prefilter) is subsumed: `grep-regex` already extracts inner literals automatically.

## Next Steps If Adopted

**This section is obsolete — see Recommendation above.** Phase 6.2 / 6.3 are not pursued. The artefacts (`scan_grep` module, feature flag, parity tests, benchmarks) remain in the tree for future reference and are gated behind `--features grep_crates` so they have zero impact on default builds.

## Spike Artefacts (kept in repo)

- [quickgrep-core/src/scan_grep.rs](../quickgrep-core/src/scan_grep.rs) — module
- [quickgrep-core/Cargo.toml](../quickgrep-core/Cargo.toml) — optional deps + `grep_crates` feature
- [quickgrep-core/src/lib.rs](../quickgrep-core/src/lib.rs) — `#[cfg(feature = "grep_crates")] pub mod scan_grep;`
