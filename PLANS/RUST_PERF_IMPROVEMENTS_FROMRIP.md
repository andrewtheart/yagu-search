# Rust Performance Improvements From Ripgrep

This plan turns the ripgrep comparison into implementation work for QuickGrep's native Rust engine. The goal is not to copy ripgrep code directly, but to adopt the same proven performance principles: buffered scanning for recursive search, borrowed match data on the hot path, line-oriented output, candidate-line acceleration, early binary exits, and possible reuse of ripgrep's public library crates.

## Current Baseline

QuickGrep already has several important pieces in place:

- Streaming FFI callbacks exist in [quickgrep-core/src/ffi.rs](../quickgrep-core/src/ffi.rs).
- Native sessions compile the matcher once per worker in [QuickGrep/Services/SearchService.cs](../QuickGrep/Services/SearchService.cs).
- The Rust scanner has binary magic checks and broad unit coverage in [quickgrep-core/src/scan.rs](../quickgrep-core/src/scan.rs).
- C# native scanning is concurrency-gated in [QuickGrep/Services/ContentSearcher.cs](../QuickGrep/Services/ContentSearcher.cs) to control mmap pressure.

Pre-existing bugs to fix before or during this work:

- `snap_to_char_boundary_end` in [quickgrep-core/src/scan.rs](../quickgrep-core/src/scan.rs) retreats backward instead of forward. It uses the same logic as `snap_to_char_boundary_start`, but as the exclusive upper bound of a slice it should either advance past the multi-byte sequence or retreat to before it started. This can silently drop characters near truncation boundaries and will compound with buffer-boundary logic in Phase 1.
- The before-context ring buffer in `scan_bytes_with_matcher` calls `copy_context_line_for_record` on **every non-matching line** when `context_before > 0`. This is a significant hidden allocation source on the hot path that is separate from the `MatchRecord` ownership problem.
- Case-insensitive `LiteralMatcher` uses `to_ascii_lowercase()`, so non-ASCII case folding (e.g. "Über" vs "über") silently fails. If Phase 4 or Phase 6 introduces Unicode-aware regex matching, there will be a parity gap with the literal path.
- The `MatchRecord` constructor calls `before.iter().cloned().collect()` on **every emitted match**, which clones the entire ring buffer for each match. This is a separate hot-path allocation source from the per-line ring-buffer copy and the owned `MatchRecord` line bytes. Phase 2 must address all three.
- Cancellation polling at `poll_counter & 0xff == 0` exists in **three** places in [quickgrep-core/src/ffi.rs](../quickgrep-core/src/ffi.rs) (the buffered packed path and two streaming paths). All three share the same "never polled on no-match files" gap, and the Phase 1 fix must cover all of them.
- UTF-16/UTF-16LE files (with or without BOM) currently trip `looks_binary` because of embedded NUL bytes and are silently skipped by the native path when `skip_binary = true`. The managed fallback in [QuickGrep/Helpers/EncodingDetector.cs](../QuickGrep/Helpers/EncodingDetector.cs) handles them. This is a real native/managed parity gap that Phase 5 should call out explicitly.

Remaining gaps compared with ripgrep:

- The native path memory-maps every non-empty file.
- `MatchRecord` owns copied line/context bytes before the FFI callback.
- Multiple matches on one line duplicate the same line and context data.
- Regex search runs the main regex line-by-line without candidate-line prefiltering.
- Binary skip happens after file open/mmap setup instead of through an early probe and rolling-buffer detection.
- Cancellation is only polled every 256 **matches** (`poll_counter & 0xff`). For no-match or sparse-match workloads, the scanner processes the entire file without checking the cancel flag. Ripgrep checks cancellation based on bytes processed.
- We are reimplementing a searcher that ripgrep already exposes as library crates.

## Success Metrics

Use these metrics to decide whether each change should ship:

- Lower peak working set during recursive content searches, especially on large trees.
- Higher no-match throughput for literal and regex searches.
- Higher high-match-density throughput with lower allocation rate.
- Faster first-result latency on large files.
- No behavioral regressions in native/managed parity tests.
- No increase in skipped text files unless intentionally caused by binary policy changes.

## Validation Commands

Run these after each phase where applicable:

```powershell
cargo test --manifest-path quickgrep-core/Cargo.toml
dotnet test QuickGrep.Tests/QuickGrep.Tests.csproj -c Release
```

For local performance runs:

```powershell
$env:QUICKGREP_PERF_DIRECTORY="D:\agentRansackAlternative"
$env:QUICKGREP_PERF_DURATION_SECONDS="30"
dotnet test QuickGrep.Tests/QuickGrep.Tests.csproj -c Release --filter PerformanceBenchmarkTests
```

## Phase 0 - Baseline And Instrumentation

Before changing the implementation, capture a repeatable baseline.

Steps:

1. Run the Rust unit tests and .NET tests to confirm a clean behavioral baseline.
2. Run representative benchmark scenarios from [QuickGrep.Tests/PerformanceBenchmarkTests.cs](../QuickGrep.Tests/PerformanceBenchmarkTests.cs): no-match literal, regex no-match, high-match-density, medium files, and very large files.
3. Record current throughput, peak working set, result count, bytes scanned, and native fallthroughs.
4. Add or extend lightweight logging around native scan mode: mmap vs buffered, bytes read, binary skipped, matches emitted, callback stops, and elapsed time.

Deliverables:

- A JSONL benchmark row for each scenario.
- A short baseline note in [PERFORMANCE_IMPROVEMENTS.md](PERFORMANCE_IMPROVEMENTS.md) or a new benchmark note.
- No functional changes yet.

Exit criteria:

- Baseline measurements are reproducible enough to compare future phases.
- Existing tests pass.

## Phase 1 - Ripgrep-Style File Reading Policy

Ripgrep generally avoids mmap during recursive search and uses a rolling line buffer. QuickGrep should stop mmap'ing every file by default.

Prerequisites:

- Fix `snap_to_char_boundary_end` before starting this phase. Buffer boundary logic will inherit and amplify this bug.

Steps:

1. Add a buffered file scanning path in [quickgrep-core/src/ffi.rs](../quickgrep-core/src/ffi.rs).
2. Keep mmap available, but gate it behind a policy similar to ripgrep:
   - Use buffered scanning for normal recursive searches.
   - Use mmap only when explicitly requested or when a future heuristic proves it is faster.
   - Keep empty-file and max-file-size behavior unchanged.
3. Add a Rust scanner entry point that accepts a `Read` source or a reusable rolling byte buffer.
4. Start with a 64 KB buffer size, matching ripgrep's default rolling-buffer scale.
5. Ensure long lines can grow within a configured limit. Define the policy for lines exceeding the limit: **fall back to mmap for that specific file**, restarting the scan from the beginning, rather than truncating (which would break match accuracy) or returning an error. **Important:** if matches were already emitted from earlier chunks before the over-length line, those matches must either be deduplicated or — simpler — the buffered scan must buffer emitted matches in memory until end-of-file and only flush them on success, so a fallback restart does not produce duplicate FFI callbacks. Document this trade-off; matches will not stream incrementally for the affected files.
6. Maintain an accurate line counter across chunk reads. Partial lines at chunk boundaries must be joined before counting. Use a line-tracking struct that carries the residual partial line and current line number between reads. Note: `bstr::ByteSlice::lines()` strips both `\n` and `\r\n` and returns slices without terminators; the buffered iterator must replicate this behavior at chunk seams, not just at the final newline.
7. Handle CRLF sequences that span chunk boundaries: when a chunk ends with `\r` and the next begins with `\n`, they must be treated as a single line terminator. Do not leave a trailing `\r` on the previous line.
8. Design the buffer lifecycle to support borrowed views from the start. Phase 2 will introduce borrowed match data; if the buffer is freed or overwritten before the callback returns, borrowed views become unsound. Pin buffer contents until the per-match callback has returned.
9. Add cancellation checks that are independent of match rate. Per-chunk polling is the minimum bar; per-line polling is also acceptable because the cost is a single relaxed atomic load. The fix must be applied to **all three** `poll_counter & 0xff` sites in [quickgrep-core/src/ffi.rs](../quickgrep-core/src/ffi.rs) (the buffered packed path and the two streaming paths), not just one.
10. Plumb the chosen scan mode through FFI status/logging so the C# side can observe whether buffered or mmap mode was used.
11. Revisit the native concurrency gate in [QuickGrep/Services/ContentSearcher.cs](../QuickGrep/Services/ContentSearcher.cs) after measurement; buffered search may allow a higher safe parallelism than mmap search.

Tests:

- Add Rust tests for buffered scanning across chunk boundaries.
- Add tests for matches split near buffer edges.
- Add tests for long lines larger than 64 KB.
- Add tests for CRLF sequences split across chunk boundaries.
- Add tests for accurate line numbering across multiple chunks.
- Add tests for cancellation between chunks on a no-match file.
- Add tests verifying the fallback-to-mmap policy when a line exceeds the configured buffer limit.
- Keep existing mmap tests for backward compatibility.

Expected payoff:

- Lower unmanaged memory pressure.
- Fewer working-set spikes on recursive scans.
- Better behavior on large directory trees with many medium/large files.
- Responsive cancellation even on no-match workloads.

Risks:

- Buffer boundary bugs can affect line numbers, context lines, and match offsets.
- Very long lines need careful memory limits to avoid reintroducing unbounded growth.
- CRLF handling across boundaries is easy to get wrong; test thoroughly.

## Phase 2 - Borrowed Streaming Instead Of Owned `MatchRecord`

The current scanner emits an owned `MatchRecord`. Ripgrep keeps the hot path borrowed and lets sinks decide what to copy.

**Ordering note:** If Phase 1 (buffered scanning) has not landed yet, borrowed views are straightforward because mmap keeps the entire file in memory for the scan's duration. If Phase 1 has landed, the rolling buffer must pin its contents until the callback returns (see Phase 1 step 8). Consider implementing Phase 2 before Phase 1 if buffer pinning proves too complex.

Steps:

1. Introduce a borrowed match view inside [quickgrep-core/src/scan.rs](../quickgrep-core/src/scan.rs), for example:
   - line number
   - match start
   - match length
   - borrowed line bytes
   - borrowed before-context lines
   - borrowed after-context lines
2. Add a new scanning function that calls a closure with the borrowed view.
3. Preserve the existing owned `MatchRecord` API temporarily for tests and compatibility.
4. Update streaming FFI in [quickgrep-core/src/ffi.rs](../quickgrep-core/src/ffi.rs) to consume the borrowed view directly.
5. Remove repeated `Vec<u8>` allocations for match lines and context lines from the streaming path.
6. Address **all three** allocation sources in the current per-match path, not just the owned `MatchRecord`:
   - `copy_context_line_for_record(line)` is called on every non-matching line when `context_before > 0` to fill the ring buffer.
   - `copy_context_line_for_record(line)` is called on every non-matching line for **every pending after-context entry** while `pending` is non-empty.
   - `before.iter().cloned().collect()` clones the entire ring buffer **per emitted match**, multiplying the cost on dense-match lines.
   Convert the ring buffer and the pending-after slots to borrow from the backing store (mmap or pinned rolling buffer).
7. Keep truncation behavior, but defer copying until the FFI view is built or until C# decodes the string.
8. Add counters for copied bytes so benchmarks can verify allocation reduction.

Tests:

- Add parity tests between owned and borrowed scanner entry points.
- Verify long-line truncation still keeps the match visible.
- Verify context before/after is identical to the current behavior.
- Verify callback early-stop still returns the expected count/status.
- Verify that the before-context ring buffer no longer allocates on non-matching lines (use allocation counters or `#[global_allocator]` in test).

Expected payoff:

- Lower Rust allocation rate.
- Lower CPU spent copying non-final data.
- Faster dense-match scans.
- Elimination of per-line allocation for the before-context ring buffer.

Risks:

- Borrowed context lifetimes are more delicate, especially when after-context delays emission.
- The FFI callback must never retain Rust pointers after returning.
- If Phase 1's rolling buffer has landed, borrowed views into it require pinning guarantees. Test that the buffer is not advanced or freed while a callback is in flight.

## Phase 3 - Line-Level Emission With Match Spans

Ripgrep reports matching lines and lets the printer/highlighter deal with match ranges. QuickGrep currently emits a full result per match, duplicating line and context data when one line contains many matches.

Steps:

1. Add a line-level scan event that contains:
   - line number
   - borrowed line bytes
   - all non-overlapping match spans for that line
   - before-context lines
   - after-context lines
2. Extend the FFI surface in [quickgrep-core/src/ffi.rs](../quickgrep-core/src/ffi.rs) to expose packed match spans for a line. Be specific about which surface changes:
   - The streaming path (`QgMatchView` + per-match callback) is the primary hot path used by [QuickGrep/Native/NativeSearcher.cs](../QuickGrep/Native/NativeSearcher.cs) and is where the new line-with-spans layout should land. Either evolve the existing struct (breaking change) or add a parallel `QgLineView` callback and let the C# side opt in.
   - The packed-buffer path (`qg_search_file` + `QgResult.buffer`) also needs a matching format change if it is still in use; otherwise mark it deprecated.
3. **Bump `qg_abi_version()` to 3** (currently 2). Decide and document whether v3 covers (a) only the streaming surface, (b) only the packed buffer, or (c) both. Add a dual-format deserialization path in [QuickGrep/Native/NativeSearcher.cs](../QuickGrep/Native/NativeSearcher.cs) only for the surface(s) that actually changed; do not add transition code for surfaces that did not.
4. Add a C# representation in [QuickGrep/Native/NativeSearcher.cs](../QuickGrep/Native/NativeSearcher.cs) that can read multiple spans from one native callback.
5. Decide UI model behavior:
   - Preferred: keep one `SearchResult` row per match for compatibility, but decode/copy the line/context once and fan out spans in managed code.
   - Future: introduce a line-level result model so the UI can show all matches on one row.
6. Update `StreamingSink` in [QuickGrep/Native/NativeSearcher.cs](../QuickGrep/Native/NativeSearcher.cs) to avoid decoding the same UTF-8 line repeatedly for multiple spans.
7. Keep per-file and global result caps consistent. A line with 10 spans counts as 10 matches if the UI still exposes match-level rows.

Tests:

- Add native tests for multiple matches on one line.
- Add cap tests where one line has more spans than the remaining result budget.
- Add parity tests against the managed path in [QuickGrep.Tests/NativeParityTests.cs](../QuickGrep.Tests/NativeParityTests.cs).

Expected payoff:

- Major reduction in duplicate line/context copying.
- Better high-match-density throughput.
- Lower channel pressure between native scanning and managed result production.

Risks:

- Caps and cancellation must remain match-based, not callback-based.
- UI assumptions may expect one native callback per match.
- The packed wire format change requires a careful ABI transition; the C# side must detect the version and parse accordingly.

## Phase 4 - Regex Candidate-Line Acceleration

Ripgrep extracts required inner literals from regexes when possible, searches for candidate lines cheaply, then verifies only those lines with the full regex.

**Important context before starting this phase:** the Rust `regex` crate (≥ 1.9) already builds Aho-Corasick / Teddy-based prefilters internally via `regex-automata` and uses them inside `Regex::find` / `find_iter`. Naively bolting our own `aho-corasick` prefilter on top of `Regex::is_match` per line is likely to do the same work twice and may be slower than just calling `Regex::find_iter` over a multi-line buffer. The genuine ripgrep wins are (1) **line-anchored** regex compilation that prevents matches from crossing line terminators so the engine can prune aggressively, and (2) the broader `grep-regex` infrastructure that lifts inner-literal extraction *out of* the regex engine into a separate pass. Step 4 below should be evaluated against this baseline.

Steps:

1. Set the Rust regex matcher up as line-oriented by default for normal single-line searches. Specifically: compile patterns under a no-newline-match constraint (e.g. via `RegexBuilder::dot_matches_new_line(false)` plus syntax checks for explicit `\n`/`\r`/`(?s)`) and run them across the entire buffer with `find_iter`, instead of calling `Regex::find` per line as today.
2. Add a matcher method equivalent to `find_candidate_line`:
   - returns no candidate when no match is possible
   - returns a candidate line offset when a cheaper prefilter hits
   - falls back to full regex shortest-match behavior when no prefilter is available
3. For literal-friendly regexes, extract required literals using `regex-syntax::hir::literal::Extractor` or evaluate reuse of `grep-regex` in Phase 6. Note that `regex-syntax` is already a transitive dependency via `regex`.
4. **Only if** measurement shows the regex crate's built-in prefilter is insufficient: use `memmem` for single-literal prefilters and `aho-corasick` for multi-literal alternation (e.g. `error|warning|critical`) as a separate fast-path that runs *before* the full regex. Add `aho-corasick` as a dependency in [quickgrep-core/Cargo.toml](../quickgrep-core/Cargo.toml). Benchmark against the regex-only baseline; ship the prefilter only if it is meaningfully faster on at least one of the regex benchmark scenarios.
5. Preserve exact behavior for anchors, case-insensitive matching, CRLF, and patterns that can match across line terminators.
6. Disable the optimization when the regex can match newlines or when the line terminator guarantee cannot be made.
7. Measure no-match regex and complex-regex workloads before broadening the optimization.

Tests:

- Regexes with clear literals: `\w+foo\w+`, `error|warning`, `prefix.*suffix`.
- Regexes with anchors: `^foo`, `foo$`, `(?m)^$`.
- Regexes that should not use candidate mode because they can cross line boundaries.
- Case-insensitive literal extraction.

Expected payoff:

- Faster no-match and sparse-match regex scans.
- Less time spent in the full regex engine.
- Closer behavior to ripgrep's line search fast path.

Risks:

- Incorrect literal extraction can create false negatives, which is unacceptable.
- Regex semantics around anchors and line terminators require careful test coverage.

## Phase 5 - Earlier And Continuous Binary Detection

QuickGrep has improved binary heuristics, but the native path can still pay file setup cost before deciding to skip. Ripgrep's buffered path detects binary while filling buffers and quits early.

Steps:

1. Add a pre-read binary probe before mmap or full scan setup in [quickgrep-core/src/ffi.rs](../quickgrep-core/src/ffi.rs). Avoid opening the file twice: read the first 8 KB chunk into a reusable buffer, run the probe, and if the file is not skipped either (a) keep the file handle and continue with buffered scanning from the residual position, or (b) drop the buffer and mmap. On Windows, prefer `std::os::windows::fs::FileExt::seek_read` for the probe to avoid mutating the file pointer.
2. Reuse the existing magic-number and control-byte heuristic from [quickgrep-core/src/scan.rs](../quickgrep-core/src/scan.rs).
3. For buffered scanning, check each newly read chunk for binary data before searching it.
4. Keep the existing policy distinction clear:
   - `skip_binary = true`: stop and return binary skipped.
   - `skip_binary = false`: search bytes as today.
5. Consider matching ripgrep's NUL-byte quit behavior for chunk scanning while retaining QuickGrep's stronger magic-number probe at the start.
6. Mirror behavior with managed binary detection in [QuickGrep/Helpers/BinaryDetector.cs](../QuickGrep/Helpers/BinaryDetector.cs) so native and fallback paths agree. Pay special attention to UTF-16/UTF-16LE: the current `looks_binary` heuristic skips them because of embedded NUL bytes, which silently diverges from the managed fallback. Either (a) detect a UTF-16 BOM in the magic-number probe and route to the managed path, (b) detect UTF-16 patterns and decode in Rust, or (c) document the divergence explicitly. Pick one; do not leave it implicit.
7. Add diagnostics that separate binary skipped by magic, NUL, and control-byte ratio.

Tests:

- Magic-number files: gzip, zip, PNG, PDF, SQLite, 7z, zstd.
- Text files with UTF-8 non-ASCII bytes.
- Text files with late NUL bytes across buffered chunks.
- `skip_binary = false` still scans binary-looking content.

Expected payoff:

- Avoids expensive work on files that will be skipped.
- Better memory safety on binary blobs with few line terminators.
- More consistent native/managed skip behavior.

Risks:

- False positives skip searchable files.
- Continuous chunk detection must not misclassify valid UTF-8 or UTF-16 text that the managed fallback might decode.

## Phase 6 - Evaluate Reusing Ripgrep Library Crates

Instead of reimplementing more of ripgrep, evaluate using its public crates directly behind QuickGrep's FFI.

Candidate crates:

- `grep-searcher` for buffered line search, mmap policy, binary handling, context, and sinks.
- `grep-regex` for line-oriented regex matching and candidate-line optimization.
- `grep-matcher` for match abstractions and line terminator contracts.

Steps:

1. Create a small experimental branch or feature flag in [quickgrep-core/Cargo.toml](../quickgrep-core/Cargo.toml) for `grep-searcher` and `grep-regex`.
2. Build a custom sink that adapts ripgrep's borrowed `SinkMatch` and `SinkContext` events to QuickGrep's FFI callback.
3. Implement only the subset QuickGrep needs first:
   - literal search
   - regex search
   - case sensitivity
   - context before/after
   - max matches
   - binary skip
4. Compare behavior and performance against the current native scanner.
5. Decide whether to:
   - replace the current scanner with ripgrep crates,
   - use ripgrep crates only for regex/candidate acceleration,
   - or keep the current scanner and port only selected architecture ideas.
6. Review dependency size, build impact, API stability, and license compatibility. **Important:** the `grep-searcher`, `grep-regex`, and `grep-matcher` crates are workspace-internal to the ripgrep project. While published on crates.io, they historically have breaking changes between ripgrep releases and do not follow strict semver. Pin exact versions (`=x.y.z`) in [quickgrep-core/Cargo.toml](../quickgrep-core/Cargo.toml) and plan for manual review on every update. Confirm the licenses on the specific versions pinned (ripgrep itself is dual MIT / Unlicense, but verify the `grep-*` crates' `Cargo.toml` metadata at the pinned version) and ensure compatibility with QuickGrep's distribution license. The unrelated ripgrep crates `grep-cli`, `grep-printer`, and `ignore` are out of scope: they bring CLI/printer/walker concerns that QuickGrep already owns in the C# layer.
7. If adopted, keep QuickGrep-specific FFI, truncation, and C# UI behavior as our boundary layer.

Tests:

- Full native parity suite.
- Performance benchmarks against current native scanner.
- Edge cases for context, cancellation, max results, binary skip, and long lines.

Expected payoff:

- Fastest route to ripgrep-like internals.
- Less custom scanner code to maintain.
- Candidate-line acceleration and rolling-buffer semantics come from battle-tested crates.

Risks:

- Dependency integration may require adapting QuickGrep's exact match-per-result semantics.
- Library APIs may not expose every detail needed for the current FFI shape.
- Build size and compile time may increase.

## Recommended Implementation Order

0. **Pre-work:** Fix `snap_to_char_boundary_end` bug in scan.rs. This is a correctness issue that will compound with later phases.
1. Phase 0: baseline measurements.
2. Phase 6 spike: decide whether `grep-searcher` can replace enough custom code to be worth it.
3. If Phase 6 succeeds, implement the crate-backed search path behind a feature flag and compare it against current native scanning.
4. If Phase 6 does not replace the scanner:
   a. Phase 2 borrowed streaming first (simpler while mmap still backs everything — no buffer lifetime concerns).
   b. Phase 1 buffered scanning (design buffer lifecycle to support borrowed views from step 4a).
   c. If Phase 2 has not landed before Phase 1, add buffer pinning guarantees in Phase 1.
5. Phase 3 line-level emission (depends on Phase 2's borrowed view infrastructure).
6. Phase 5 early and continuous binary detection.
7. Phase 4 regex candidate-line acceleration, unless `grep-regex` adoption already provides it.

Rationale:

- The crate-reuse spike should happen early because it may eliminate most custom work in Phases 1, 2, 4, and part of 5.
- Phase 2 is simpler to implement while mmap still guarantees stable memory. Implementing it before Phase 1 avoids complex buffer-pinning lifetime issues.
- Buffered scanning should land before raising native parallelism.
- Borrowed streaming and line-level emission should land before optimizing dense match scenarios further.
- Candidate-line regex acceleration has the highest semantic risk, so it should either come from `grep-regex` or be implemented after the scanner boundaries are stable.

## Rollback Strategy

- Keep the current native scanner entry points while new paths are feature-gated.
- Maintain managed fallback behavior in [QuickGrep/Services/ContentSearcher.cs](../QuickGrep/Services/ContentSearcher.cs).
- Add runtime logging for selected native engine mode so regressions can be triaged quickly.
- Use native/managed parity tests as the release gate for each phase.

## Decisions

The following design questions have been resolved. They are binding for implementation.

- **UI result model:** Keep the current behavior — one `SearchResult` row per match. Phase 3's optimization is purely internal: decode the line/context once and fan out N rows that share the decoded string. The C# `SearchResult` shape, row count, and result caps must be unchanged.
- **mmap configurability:** mmap remains user-configurable for explicit single-file searches. Phase 1's "buffered by default" policy applies only to recursive directory searches; single-file API entry points must continue to honor an explicit mmap request.
- **Binary detection:** Do **not** change or enhance the existing binary-detection heuristic in [quickgrep-core/src/scan.rs](../quickgrep-core/src/scan.rs) (`looks_binary`, `has_binary_magic`) or in [QuickGrep/Helpers/BinaryDetector.cs](../QuickGrep/Helpers/BinaryDetector.cs). Phase 5 is therefore restricted to **moving the existing probe earlier** (pre-mmap) and **running it incrementally on buffered chunks**, using the unchanged classification logic. Phase 5 step 7 (separate diagnostic counters by reason) is dropped. The "stronger binary detection" framing in Phase 5 risks/expected payoff sections should be removed.
- **UTF-16 / legacy encodings in Rust:** Decode UTF-16 (LE/BE, with or without BOM) natively in the Rust path. The native scanner must detect a UTF-16 BOM in the pre-read probe, transcode to UTF-8 (or scan UTF-16 directly with appropriate matchers), and emit matches with line/column positions that round-trip back to source byte offsets. The managed fallback in [QuickGrep/Helpers/EncodingDetector.cs](../QuickGrep/Helpers/EncodingDetector.cs) is no longer the only path for UTF-16. Phase 5 step 6 is now: "Detect UTF-16 BOM in the magic-number probe and route to a native UTF-16 scan path (new work in Phase 1 or a follow-up sub-phase). Do not classify BOMed UTF-16 as binary."
- **Byte offsets in match output:** Native search must report **byte offset into the source file** in addition to the displayed character column for every match. Add a `match_byte_offset: u64` field to `QgMatchView` and the borrowed match view introduced in Phase 2. The byte offset is the offset *before* any UTF-16→UTF-8 transcoding (i.e. into the original file bytes), so editor integrations can `Seek` to it. This requires an ABI bump and must be coordinated with the Phase 3 ABI change rather than landing as a separate v3→v4 bump.
- **Case-insensitive `LiteralMatcher`:** Support **both** ASCII and Unicode case folding. Default to Unicode case folding for parity with the regex path; expose an `ascii_case_only` option on `ScanOptions` for callers that want the cheaper ASCII-only path (e.g. when the pattern is provably pure ASCII). Use `regex` with the literal pattern escaped + `(?i)` for the Unicode-folding path, or use a Unicode-aware fold from `bstr`. Add tests covering "Über" / "über", "ß" / "SS", and Turkish dotted/dotless I edge cases (document the chosen behavior for the Turkish I).
- **Long-line buffer overflow policy:** Fall back to mmap for the affected file. The fallback must restart the scan from the beginning of that file. To avoid duplicate FFI callbacks for matches already emitted in earlier chunks, the buffered path must buffer emitted matches in memory and flush them only when the file completes successfully (i.e. matches do not stream incrementally for files that hit this path). This is acceptable because the case is rare; document it in the Phase 1 expected-behavior section.
- **ABI v3 surface scope:** v3 covers **both** the streaming surface (`qg_search_file_streaming` + `QgMatchView`) and the packed-buffer surface (`qg_search_file` + `QgResult.buffer`). Both are confirmed in active use by [QuickGrep/Native/NativeSearcher.cs](../QuickGrep/Native/NativeSearcher.cs) (`SearchFile` calls `QgSearchFile`; `SearchFileStream` calls `QgSearchFileStream`). Updating both is required because:
  1. Both paths benefit identically from line-level dedup (decode line once, emit N spans), so excluding either leaves performance on the table for whichever path it serves.
  2. The byte-offset addition (`match_byte_offset`) is mandatory for editor integration on every match regardless of surface, which forces an ABI bump on both anyway.
  3. The C# row-count contract is preserved on both surfaces by fanning spans into N `SearchResult` rows in managed code, so the UI sees identical behavior either way.
  Concretely: bump `qg_abi_version()` to 3; in the streaming path, change `QgMatchView` to a `QgLineView` that carries packed `(span_start, span_len)` pairs plus the new byte offset of the line start (per-span byte offset is `line_byte_offset + span_start`); in the packed-buffer path, change the per-line record to `[line_number u64][line_byte_offset u64][line_len u32][line bytes][span_count u32][(start u32, len u32) × span_count][before_count u32][...][after_count u32][...]` instead of the current per-match record. [QuickGrep/Native/NativeSearcher.cs](../QuickGrep/Native/NativeSearcher.cs) detects v3 via `qg_abi_version()` at load time and selects the appropriate parser; v2 parsing is retained only for graceful failure during deployment, then removed once v3 ships.

## Open Questions

- None outstanding.