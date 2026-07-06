//! Phase 2 spike: is a whole-buffer **multiline** scan (matches may span line
//! breaks) fast enough — and does its sink map matches to
//! (start line/col .. end line/col) correctly — to back Yagu's native
//! multiline path? And which engine: ripgrep's `grep-searcher`, or a
//! hand-rolled `regex::bytes::find_iter` over the already-shipped `regex`
//! crate (scan.rs:6)?
//!
//! HEAD-TO-HEAD RESULT (64 MB corpus, release; see the `bench_*` fns): the two
//! agree on count AND span checksum (correctness cross-check), and are within
//! ~5% on no-match / single-line needles, but the hand-rolled whole-buffer
//! `find_iter` is ~**1.7x FASTER** on a realistic match-heavy lazy cross-line
//! pattern — because grep-searcher's per-block Sink model re-locates each span
//! within its line-block (double work) while the hand-rolled path scans once
//! and maps spans with one forward newline cursor. `grep-regex` IS the `regex`
//! crate underneath, so grep-searcher's line-orientation is pure overhead in
//! multiline mode. => Yagu's native multiline should hand-roll over `regex`.
//!
//! This answers the two open questions in `PLANS/MULTILINE_SEARCH_PLAN.md` §14:
//!   * throughput of the multiline sink vs the in-tree LINE scanner (the number
//!     we must not regress the *default* path below), and
//!   * the exact CRLF line-terminator API on the pinned `grep-searcher 0.1.16`
//!     (`.crlf(true)` vs `.line_terminator(..)`).
//!
//! NOTE (superseded): the plan since standardized on an **LF-normalization**
//! CRLF policy (`PLANS/MULTILINE_SEARCH_PLAN.md` §8) — every engine normalizes
//! `\r\n` and lone `\r` -> `\n` up front and scans LF-only bytes with a plain
//! `\n` terminator, so the PRODUCTION native path does NOT use `.crlf(true)` /
//! `LineTerminator::crlf()`. The `*_crlf` helpers and `crlf_dollar_anchor_*`
//! test below are retained only as a record of the rejected `.crlf` approach.
//! The throughput head-to-head (hand-rolled ~1.7x faster) is orthogonal to CRLF
//! handling and remains the basis for the default-engine decision.
//!
//! Correctness tests run normally (fast, small inputs). Throughput tests are
//! `#[ignore]` (large corpus) — run explicitly with:
//!   cargo test --release --features grep_crates --test multiline_spike -- --nocapture --ignored

#![cfg(feature = "grep_crates")]

use std::time::Instant;

use grep_matcher::{LineTerminator, Match, Matcher};
use grep_regex::{RegexMatcher, RegexMatcherBuilder};
use grep_searcher::{BinaryDetection, Searcher, SearcherBuilder, Sink, SinkError, SinkMatch};

use regex::bytes::{Regex as BytesRegex, RegexBuilder as BytesRegexBuilder};

use yagu_core::scan::{scan_bytes_ex, MatchRecord, ScanOptions};

// ---------------------------------------------------------------------------
// Minimal cross-line record the spike sink produces. The real Phase 2 record
// would also carry display bytes/context; here we keep it lean so the
// benchmark measures the searcher + span-location cost, not Vec churn.
// ---------------------------------------------------------------------------
#[derive(Debug, Clone, PartialEq, Eq)]
struct MlRecord {
    start_line: u64,
    start_col: u32, // 0-based BYTE column within the start line
    end_line: u64,
    end_col: u32, // 0-based BYTE column within the end line (exclusive)
    byte_len: usize,
}

#[derive(Debug)]
struct Abort;
impl std::fmt::Display for Abort {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("abort")
    }
}
impl std::error::Error for Abort {}
impl SinkError for Abort {
    fn error_message<T: std::fmt::Display>(_m: T) -> Self {
        Abort
    }
}

/// Build a matcher configured for whole-buffer multiline matching.
///
/// KEY API FINDING (documented for the plan): for the searcher's `multi_line`
/// mode the matcher must be allowed to match across `\n`, so we do **not** set
/// `.line_terminator(..)` here (that flag forbids `\n` in the pattern and is
/// what the LINE path uses). `.multi_line(true)` binds `^`/`$` at internal line
/// boundaries (ripgrep `-U`); `.dot_matches_new_line` is the dotall sub-option.
fn build_ml_matcher(pattern: &str, case_sensitive: bool, dotall: bool, crlf: bool) -> RegexMatcher {
    RegexMatcherBuilder::new()
        .case_insensitive(!case_sensitive)
        .case_smart(false)
        .multi_line(true)
        .dot_matches_new_line(dotall)
        .crlf(crlf)
        .build(pattern)
        .expect("multiline matcher builds")
}

fn build_ml_searcher() -> Searcher {
    SearcherBuilder::new()
        .multi_line(true)
        .line_number(true)
        .binary_detection(BinaryDetection::none())
        .build()
}

/// CRLF variant: the searcher's line terminator MUST equal the matcher's, or
/// `search_slice` returns `ConfigError::MismatchedLineTerminators`
/// (grep-searcher `searcher/mod.rs:805-815`). So a `.crlf(true)` matcher
/// requires `.line_terminator(LineTerminator::crlf())` here. THIS is the
/// resolution to plan §8's "crlf vs line_terminator" open question: you set
/// BOTH — `.crlf(true)` on the matcher and `LineTerminator::crlf()` on the
/// searcher.
fn build_ml_searcher_crlf() -> Searcher {
    SearcherBuilder::new()
        .multi_line(true)
        .line_number(true)
        .line_terminator(LineTerminator::crlf())
        .binary_detection(BinaryDetection::none())
        .build()
}

/// Sink that maps every match inside a multiline `SinkMatch` block to an
/// `MlRecord` with true start/end line+column. In multiline mode a `SinkMatch`
/// carries the whole block of lines the match spans; `line_number()` is the
/// block's FIRST line. We re-run the matcher over the block to recover exact
/// byte ranges (this is what ripgrep's printer does) and locate lines via
/// `memchr`, advancing a forward-only newline count so the per-block cost is
/// O(block), never O(block^2).
struct SpanSink<'m, F: FnMut(MlRecord)> {
    matcher: &'m RegexMatcher,
    emit: F,
}

impl<F: FnMut(MlRecord)> Sink for SpanSink<'_, F> {
    type Error = Abort;

    fn matched(&mut self, _s: &Searcher, m: &SinkMatch<'_>) -> Result<bool, Abort> {
        let block = m.bytes();
        let first_line = m.line_number().unwrap_or(0);

        // Forward-only line/col cursor over this block.
        // `nl_before(off)` = number of '\n' in block[..off]; `line_start(off)`
        // = index just past the last '\n' before off (0 if none).
        let mut ranges: Vec<Match> = Vec::new();
        self.matcher
            .find_iter(block, |mat| {
                ranges.push(mat);
                true
            })
            .map_err(|_| Abort)?;

        let mut newline_count: u64 = 0;
        let mut scanned: usize = 0; // block[..scanned] already counted
        let mut last_line_start: usize = 0;

        for mat in ranges {
            if mat.start() == mat.end() {
                continue; // ignore zero-width (spike scope)
            }
            // Advance the cursor to mat.start().
            advance(block, &mut scanned, &mut newline_count, &mut last_line_start, mat.start());
            let start_line = first_line + newline_count;
            let start_col = (mat.start() - last_line_start) as u32;

            // Advance to mat.end() for the end line/col.
            advance(block, &mut scanned, &mut newline_count, &mut last_line_start, mat.end());
            let end_line = first_line + newline_count;
            let end_col = (mat.end() - last_line_start) as u32;

            (self.emit)(MlRecord {
                start_line,
                start_col,
                end_line,
                end_col,
                byte_len: mat.end() - mat.start(),
            });
        }
        Ok(true)
    }
}

/// Move a forward-only (newline_count, last_line_start) cursor to `target`,
/// counting only the newline bytes in the newly-scanned segment. `scanned` is
/// the high-water byte offset already accounted for.
#[inline]
fn advance(
    block: &[u8],
    scanned: &mut usize,
    newline_count: &mut u64,
    last_line_start: &mut usize,
    target: usize,
) {
    if target <= *scanned {
        return; // matches arrive in order; nothing to do
    }
    for i in memchr::memchr_iter(b'\n', &block[*scanned..target]) {
        *newline_count += 1;
        *last_line_start = *scanned + i + 1;
    }
    *scanned = target;
}

/// Run a multiline scan, returning (match count, checksum-of-line-numbers).
fn scan_multiline(bytes: &[u8], matcher: &RegexMatcher) -> (u64, u64) {
    let mut count = 0u64;
    let mut checksum = 0u64;
    let mut searcher = build_ml_searcher();
    let mut sink = SpanSink {
        matcher,
        emit: |r: MlRecord| {
            count += 1;
            // fold span into a checksum so the optimizer can't elide the work
            checksum = checksum
                .wrapping_add(r.start_line)
                .wrapping_mul(31)
                .wrapping_add(r.end_line)
                .wrapping_add(r.end_col as u64);
        },
    };
    searcher
        .search_slice(matcher, bytes, &mut sink)
        .expect("search_slice");
    (count, checksum)
}

// ---------------------------------------------------------------------------
// HAND-ROLLED alternative: `regex::bytes::Regex::find_iter` over the WHOLE
// buffer, with the SAME span-mapping logic as SpanSink. This is the "Option B"
// engine — no grep-* crates, just the already-shipped `regex` crate the line
// path uses (scan.rs:6). We compare its throughput head-to-head with
// grep-searcher on the identical corpus/patterns so the engine decision is
// data-driven, not theoretical. On an LF corpus the two must also agree on
// count + checksum (a correctness cross-check).
// ---------------------------------------------------------------------------
fn build_ml_regex(pattern: &str, case_sensitive: bool, dotall: bool) -> BytesRegex {
    BytesRegexBuilder::new(pattern)
        .case_insensitive(!case_sensitive)
        .multi_line(true)
        .dot_matches_new_line(dotall)
        .build()
        .expect("multiline regex builds")
}

/// Whole-buffer scan with a forward-only newline cursor. `find_iter` yields
/// non-overlapping matches left-to-right, so a single O(n) cursor over the
/// buffer resolves absolute (start_line/col .. end_line/col) — never O(n^2).
fn scan_multiline_regex(bytes: &[u8], re: &BytesRegex) -> (u64, u64) {
    let mut count = 0u64;
    let mut checksum = 0u64;

    let mut newline_count: u64 = 0; // '\n' seen in bytes[..scanned]
    let mut scanned: usize = 0;
    let mut last_line_start: usize = 0;

    for mat in re.find_iter(bytes) {
        if mat.start() == mat.end() {
            continue; // ignore zero-width (spike scope, matches SpanSink)
        }
        advance(bytes, &mut scanned, &mut newline_count, &mut last_line_start, mat.start());
        let start_line = 1 + newline_count; // 1-based, like grep line_number(true)
        let _start_col = (mat.start() - last_line_start) as u32;

        advance(bytes, &mut scanned, &mut newline_count, &mut last_line_start, mat.end());
        let end_line = 1 + newline_count;
        let end_col = (mat.end() - last_line_start) as u32;

        count += 1;
        checksum = checksum
            .wrapping_add(start_line)
            .wrapping_mul(31)
            .wrapping_add(end_line)
            .wrapping_add(end_col as u64);
    }
    (count, checksum)
}

fn line_opts() -> ScanOptions {
    ScanOptions {
        case_sensitive: true,
        use_regex: true,
        context_before: 0,
        context_after: 0,
        max_results: 0,
        skip_binary: false,
        ascii_case_only: false,
        metadata_only: false,
        multi_line: false,
        multi_line_dotall: false,
        multiline_engine: 0,
    }
}

/// Synthetic source-code-ish corpus: many short lines, a repeating function so
/// a cross-line pattern (fn signature .. its `Ok(response)`) matches once per
/// block, plus a sparse single-line needle (`HOTPATH`).
fn build_corpus(target_bytes: usize) -> Vec<u8> {
    let template: &[u8] = b"fn process_request(req: &Request) -> Result<Response, Error> {\n\
          \x20\x20\x20\x20let parsed = parse_json(req.body())?;\n\
          \x20\x20\x20\x20let user = lookup_user(parsed.user_id)?;\n\
          \x20\x20\x20\x20let response = build_response(user);\n\
          \x20\x20\x20\x20log_info(&format!(\"served {}\", user.name));\n\
          \x20\x20\x20\x20Ok(response)\n\
          }\n\
          \n\
          // Some commentary about the function above with a needle: HOTPATH\n\
          // and another non-matching comment line for filler text.\n\
          struct Cache { entries: Vec<Entry>, capacity: usize }\n\
          impl Cache { fn get(&self, k: &str) -> Option<&Entry> { None } }\n";
    let mut buf = Vec::with_capacity(target_bytes + template.len());
    while buf.len() < target_bytes {
        buf.extend_from_slice(template);
    }
    buf
}

fn mb_per_s(total_bytes: u64, elapsed: f64) -> f64 {
    (total_bytes as f64 / 1_048_576.0) / elapsed
}

// ===========================================================================
// Correctness (runs in the normal suite — small, fast)
// ===========================================================================

#[test]
fn cross_line_match_reports_start_and_end_lines() {
    // Match spans line 1 ("start") through line 3 ("end").
    let bytes = b"alpha start here\nmiddle line\nend of it\ntrailing\n";
    let matcher = build_ml_matcher(r"start[\s\S]*?end", true, false, false);
    let mut recs = Vec::new();
    let mut searcher = build_ml_searcher();
    let mut sink = SpanSink {
        matcher: &matcher,
        emit: |r: MlRecord| recs.push(r),
    };
    searcher.search_slice(&matcher, bytes, &mut sink).unwrap();

    assert_eq!(recs.len(), 1, "one cross-line occurrence");
    let r = &recs[0];
    assert_eq!(r.start_line, 1, "match starts on line 1");
    assert_eq!(r.end_line, 3, "match ends on line 3");
    assert_eq!(r.start_col, 6, "col of 'start' within line 1");
    // 'end' begins at col 0 of line 3, match includes the 3 chars "end".
    assert_eq!(r.end_col, 3);
}

#[test]
fn single_line_matches_in_multiline_mode_report_same_line() {
    let bytes = b"no\nfoo here foo\nbar\n";
    let matcher = build_ml_matcher("foo", true, false, false);
    let mut recs = Vec::new();
    let mut searcher = build_ml_searcher();
    let mut sink = SpanSink {
        matcher: &matcher,
        emit: |r: MlRecord| recs.push(r),
    };
    searcher.search_slice(&matcher, bytes, &mut sink).unwrap();

    assert_eq!(recs.len(), 2);
    for r in &recs {
        assert_eq!(r.start_line, 2);
        assert_eq!(r.end_line, 2);
    }
    assert_eq!(recs[0].start_col, 0);
    assert_eq!(recs[1].start_col, 9);
}

#[test]
fn multiline_count_matches_line_mode_for_single_line_pattern() {
    // For a pattern that never spans lines, multiline mode must find exactly
    // the same number of occurrences as the in-tree line scanner.
    let bytes = build_corpus(256 * 1024);
    let o = line_opts();
    let mut line_hits = 0u64;
    scan_bytes_ex(&bytes, "HOTPATH", &o, || false, |_: MatchRecord| {
        line_hits += 1;
        true
    })
    .unwrap();

    let matcher = build_ml_matcher("HOTPATH", true, false, false);
    let (ml_hits, _) = scan_multiline(&bytes, &matcher);
    assert_eq!(line_hits, ml_hits, "single-line pattern parity");
}

// ---- CRLF API resolution (§8 open question) ----

#[test]
fn crlf_dollar_anchor_before_crlf() {
    // With .crlf(true), `$` must match at the position before \r\n, and the
    // recovered match must NOT include the trailing \r.
    let bytes = b"first line\r\nsecond\r\n";
    let matcher = build_ml_matcher(r"line$", true, false, true);
    let mut recs = Vec::new();
    let mut searcher = build_ml_searcher_crlf();
    let mut sink = SpanSink {
        matcher: &matcher,
        emit: |r: MlRecord| recs.push(r),
    };
    searcher.search_slice(&matcher, bytes, &mut sink).unwrap();

    assert_eq!(recs.len(), 1, "`line$` matches once on the CRLF file");
    assert_eq!(recs[0].start_line, 1);
    assert_eq!(recs[0].end_line, 1);
    // "line" is 4 bytes; must not have swallowed the \r.
    assert_eq!(recs[0].byte_len, 4, "match excludes the trailing CR");
}

// ===========================================================================
// Throughput (ignored — large corpus)
// ===========================================================================

#[test]
#[ignore]
fn bench_multiline_crossline_pattern() {
    let bytes = build_corpus(64 * 1024 * 1024); // 64 MB
    let iters = 8u32;
    println!("\n=== Cross-line pattern (multiline, 64 MB, {iters} iters) ===");
    let matcher = build_ml_matcher(r"fn process_request[\s\S]*?Ok\(response\)", true, false, false);

    // warm-up
    let (warm, _) = scan_multiline(&bytes, &matcher);
    let total = (bytes.len() as u64) * (iters as u64);
    let start = Instant::now();
    let mut hits = 0u64;
    for _ in 0..iters {
        let (h, _c) = scan_multiline(&bytes, &matcher);
        hits = h;
    }
    let dt = start.elapsed().as_secs_f64();
    println!(
        "  grep-searcher multiline  hits={hits:>7} warm={warm:>7} {:>8.1} MB/s ({dt:.2}s)",
        mb_per_s(total, dt)
    );
    let mbs_grep = mb_per_s(total, dt);

    // Hand-rolled regex::bytes over the same corpus/pattern.
    let re = build_ml_regex(r"fn process_request[\s\S]*?Ok\(response\)", true, false);
    let (rwarm, rwarm_cs) = scan_multiline_regex(&bytes, &re);
    let (_, gwarm_cs) = {
        // recompute grep checksum once for parity
        let m = build_ml_matcher(r"fn process_request[\s\S]*?Ok\(response\)", true, false, false);
        scan_multiline(&bytes, &m)
    };
    let start = Instant::now();
    let mut rhits = 0u64;
    for _ in 0..iters {
        let (h, _c) = scan_multiline_regex(&bytes, &re);
        rhits = h;
    }
    let dt_r = start.elapsed().as_secs_f64();
    let mbs_regex = mb_per_s(total, dt_r);
    println!("  hand-rolled regex::bytes hits={rhits:>7} warm={rwarm:>7} {mbs_regex:>8.1} MB/s ({dt_r:.2}s)");
    assert_eq!(hits, rhits, "count parity grep vs hand-rolled");
    assert_eq!(gwarm_cs, rwarm_cs, "checksum parity grep vs hand-rolled");
    println!("  >> hand-rolled / grep-searcher = {:.2}x", mbs_regex / mbs_grep);
}

#[test]
#[ignore]
fn bench_multiline_singleline_overhead_vs_line_path() {
    let bytes = build_corpus(64 * 1024 * 1024);
    let iters = 8u32;
    println!("\n=== Single-line needle: multiline mode vs in-tree LINE path (64 MB, {iters} iters) ===");
    let o = line_opts();

    // In-tree LINE scanner — the DEFAULT path we must not regress.
    let start = Instant::now();
    let mut line_hits = 0u64;
    for _ in 0..iters {
        let mut n = 0u64;
        scan_bytes_ex(&bytes, "HOTPATH", &o, || false, |_: MatchRecord| {
            n += 1;
            true
        })
        .unwrap();
        line_hits = n;
    }
    let dt_line = start.elapsed().as_secs_f64();
    let total = (bytes.len() as u64) * (iters as u64);
    let mbs_line = mb_per_s(total, dt_line);
    println!("  in-tree LINE scan        hits={line_hits:>7} {mbs_line:>8.1} MB/s ({dt_line:.2}s)");

    // Same needle, but forced through the multiline whole-buffer path.
    let matcher = build_ml_matcher("HOTPATH", true, false, false);
    let start = Instant::now();
    let mut ml_hits = 0u64;
    for _ in 0..iters {
        let (h, _c) = scan_multiline(&bytes, &matcher);
        ml_hits = h;
    }
    let dt_ml = start.elapsed().as_secs_f64();
    let mbs_ml = mb_per_s(total, dt_ml);
    println!("  grep-searcher multiline  hits={ml_hits:>7} {mbs_ml:>8.1} MB/s ({dt_ml:.2}s)");

    assert_eq!(line_hits, ml_hits, "hit-count parity");
    println!("  >> multiline / line throughput = {:.2}x", mbs_ml / mbs_line);

    // Hand-rolled regex::bytes, same single-line needle through the whole-buffer path.
    let re = build_ml_regex("HOTPATH", true, false);
    let start = Instant::now();
    let mut r_hits = 0u64;
    for _ in 0..iters {
        let (h, _c) = scan_multiline_regex(&bytes, &re);
        r_hits = h;
    }
    let dt_r = start.elapsed().as_secs_f64();
    let mbs_r = mb_per_s(total, dt_r);
    println!("  hand-rolled regex::bytes hits={r_hits:>7} {mbs_r:>8.1} MB/s ({dt_r:.2}s)");
    assert_eq!(ml_hits, r_hits, "hit-count parity grep vs hand-rolled");
    println!("  >> hand-rolled / grep-searcher = {:.2}x", mbs_r / mbs_ml);
}

#[test]
#[ignore]
fn bench_multiline_no_match() {
    let bytes = build_corpus(64 * 1024 * 1024);
    let iters = 8u32;
    println!("\n=== No-match cross-line pattern (multiline, 64 MB, {iters} iters) ===");
    let matcher = build_ml_matcher(r"ZZZ_never[\s\S]*?QQQ_here", true, false, false);
    let total = (bytes.len() as u64) * (iters as u64);
    let start = Instant::now();
    let mut hits = 0u64;
    for _ in 0..iters {
        let (h, _c) = scan_multiline(&bytes, &matcher);
        hits = h;
    }
    let dt = start.elapsed().as_secs_f64();
    println!(
        "  grep-searcher multiline  hits={hits:>7} {:>8.1} MB/s ({dt:.2}s)",
        mb_per_s(total, dt)
    );
    let mbs_grep = mb_per_s(total, dt);

    // Hand-rolled regex::bytes, same no-match cross-line pattern.
    let re = build_ml_regex(r"ZZZ_never[\s\S]*?QQQ_here", true, false);
    let start = Instant::now();
    let mut rhits = 0u64;
    for _ in 0..iters {
        let (h, _c) = scan_multiline_regex(&bytes, &re);
        rhits = h;
    }
    let dt_r = start.elapsed().as_secs_f64();
    let mbs_regex = mb_per_s(total, dt_r);
    println!("  hand-rolled regex::bytes hits={rhits:>7} {mbs_regex:>8.1} MB/s ({dt_r:.2}s)");
    assert_eq!(hits, rhits, "count parity grep vs hand-rolled");
    println!("  >> hand-rolled / grep-searcher = {:.2}x", mbs_regex / mbs_grep);
}
