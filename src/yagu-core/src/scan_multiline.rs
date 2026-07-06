//! Whole-buffer cross-line (multiline) scanner — the Phase 2 native engine.
//!
//! This is a **sibling** of the per-line scanner in [`crate::scan`]: the
//! single-line hot loop (`scan_bytes_with_matcher*`) is left completely
//! untouched. Multiline is selected once per file by `ScanOptions::multi_line`
//! (plan §0), so the default search path keeps its exact performance budget.
//!
//! The engine (default, hand-rolled `regex::bytes`):
//!   1. **LF-normalize** the raw file bytes into an owned buffer — `\r\n` and
//!      lone `\r` -> `\n` — the byte-level mirror of the managed
//!      `MultilineTextShadow`, so native and managed scan **byte-identical**
//!      haystacks (plan §8) and parity is exact by construction.
//!   2. Drive `regex::bytes::Regex::find_iter` **once** over the whole LF
//!      buffer with `multi_line(true)` (and `dot_matches_new_line` for dotall).
//!   3. Map each match's byte span -> (start line/col .. end line/col) using a
//!      line-start table plus per-line UTF-16 column cursors. Columns cross the
//!      FFI boundary as UTF-16 code units (the .NET column space); the end
//!      column MUST be precomputed here because the emitted record ships only
//!      the START (display) line's bytes (plan §3).
//!
//! Landmines honored (see `/memories/repo/yagu-profiling.md` + handoff §4):
//!   * NEVER `utf16_col(line, off)` per match — column work goes through
//!     [`crate::scan::Utf16ColCursor`] (O(line) per line, not O(line^2)).
//!   * Zero-width matches are DROPPED (`m.start() == m.end()`), matching the
//!     line path — do not inherit ripgrep's emit-empty rule.
//!   * Cancellation is polled before the scan and after every yielded match;
//!     the size cap (enforced by the FFI caller) is the true worst-case bound
//!     for a huge no-match buffer (plan §10).

use crate::scan::{
    copy_context_line_for_record, copy_match_line_for_record, looks_binary, needs_eager_source_col,
    ScanError, ScanOptions, Utf16ColCursor,
};
use memchr::{memchr, memchr_iter};
use regex::bytes::{Regex, RegexBuilder};
use std::borrow::Cow;

/// Owned cross-line match record — the multiline sibling of
/// [`crate::scan::MatchRecord`]. Adds the true span end (`end_line`/`end_col`)
/// on top of the single-line display fields. The display `line` is the START
/// line (windowed like the single-line path); consumers render the start line
/// plus a "(+N lines)" marker (full-span rendering is Phase 3).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MultilineMatchRecord {
    /// 1-based START line number in the LF-normalized file.
    pub line_number: u64,
    /// Match start BYTE offset within the (possibly windowed) display `line`.
    pub match_start: u32,
    /// Full-start-line UTF-16 column of the match start, or `u32::MAX` when the
    /// managed side can compute it lazily from the emitted line bytes (mirrors
    /// the single-line `source_match_start` sentinel discipline).
    pub source_match_start: u32,
    /// First-line-visible match length in SOURCE bytes (the portion of the span
    /// on the start line). Keeps single-line rendering/eviction from overrunning
    /// the display line; consumers needing the true span read `end_*`.
    pub match_len: u32,
    /// 1-based END line number (== `line_number` for a single-line match found
    /// during a multiline search; the C# reader nulls the span in that case).
    pub end_line: u64,
    /// UTF-16 column of the match end (exclusive) on the END line. Precomputed
    /// here because the record does not ship the end line's bytes (plan §3).
    pub end_col: u32,
    /// Emitted display (start) line bytes; very long lines are windowed around
    /// the match exactly like the single-line path.
    pub line: Vec<u8>,
    /// Context lines before the START line, oldest first.
    pub context_before: Vec<Vec<u8>>,
    /// Context lines after the END line, in file order (span-aware — numbered
    /// from the end line, not the start line; plan §7).
    pub context_after: Vec<Vec<u8>>,
}

/// LF-normalize `bytes`: `\r\n` and lone `\r` -> `\n`. Returns a borrowed `Cow`
/// when there is no `\r` (identity — no allocation), else an owned normalized
/// buffer. Byte-level mirror of the managed `MultilineTextShadow.Build` so both
/// engines scan identical haystacks (plan §8). Native multiline is SEARCH-only
/// and reads display lines/columns straight from the LF buffer, so the sparse
/// original-offset map the managed side keeps (for REPLACE splicing) is not
/// needed here.
pub(crate) fn normalize_to_lf(bytes: &[u8]) -> Cow<'_, [u8]> {
    if memchr(b'\r', bytes).is_none() {
        return Cow::Borrowed(bytes);
    }
    let mut out = Vec::with_capacity(bytes.len());
    let mut i = 0;
    while i < bytes.len() {
        let b = bytes[i];
        if b == b'\r' {
            // "\r\n" -> "\n" (drop the '\r' and consume the paired '\n');
            // lone "\r" -> "\n" (length-preserving substitution).
            out.push(b'\n');
            if bytes.get(i + 1) == Some(&b'\n') {
                i += 1;
            }
        } else {
            out.push(b);
        }
        i += 1;
    }
    Cow::Owned(out)
}

/// Build the whole-buffer multiline `regex::bytes::Regex`. A literal pattern is
/// escaped and run through the regex engine — there is no literal multiline
/// fast path (a bare literal must still be able to span `\n`, plan §11).
/// Lookaround/backreferences are rejected by the `regex` crate ->
/// `InvalidRegex` -> managed whole-file fallback (plan §6).
pub fn build_multiline_regex(pattern: &str, options: &ScanOptions) -> Result<Regex, ScanError> {
    let effective: Cow<'_, str> = if options.use_regex {
        Cow::Borrowed(pattern)
    } else {
        Cow::Owned(regex::escape(pattern))
    };
    RegexBuilder::new(&effective)
        .case_insensitive(!options.case_sensitive)
        .multi_line(true)
        .dot_matches_new_line(options.multi_line_dotall)
        .build()
        .map_err(|e| ScanError::InvalidRegex(e.to_string()))
}

/// Scan an in-memory byte buffer for cross-line matches with a pre-built
/// multiline regex, calling `emit` for every match. `should_cancel` is polled
/// before the scan and after each yielded match. Returns the number of emitted
/// records (early-stops on cancel, `emit` returning `false`, or `max_results`).
pub fn scan_multiline_bytes(
    bytes: &[u8],
    re: &Regex,
    options: &ScanOptions,
    mut should_cancel: impl FnMut() -> bool,
    mut emit: impl FnMut(MultilineMatchRecord) -> bool,
) -> Result<usize, ScanError> {
    // Binary sniff on the RAW bytes (before normalization), matching the line
    // path and the managed multiline sniff (plan §5/§6).
    if options.skip_binary && looks_binary(bytes) {
        return Err(ScanError::BinarySkipped);
    }
    if should_cancel() {
        return Ok(0);
    }

    let lf = normalize_to_lf(bytes);
    let hay: &[u8] = lf.as_ref();
    let line_starts = build_line_starts(hay);

    let mut emitted = 0usize;
    // Two independent, line-keyed UTF-16 column cursors: matches sharing a start
    // (or end) line decode that line's prefix once total, never per-match.
    let mut start_cur = LineColCursor::new();
    let mut end_cur = LineColCursor::new();

    for m in re.find_iter(hay) {
        if should_cancel() {
            return Ok(emitted);
        }
        let (mstart, mend) = (m.start(), m.end());
        if mstart == mend {
            continue; // drop zero-width, matching the line path
        }

        let rec = map_match_to_record(
            hay,
            &line_starts,
            mstart,
            mend,
            &mut start_cur,
            &mut end_cur,
            options,
        );

        if !emit(rec) {
            return Ok(emitted);
        }
        emitted += 1;
        if options.max_results != 0 && emitted >= options.max_results {
            return Ok(emitted);
        }
    }

    Ok(emitted)
}

/// Map one match byte-span `[mstart, mend)` over the LF buffer `hay` into a
/// [`MultilineMatchRecord`], using a precomputed line-start table and two
/// line-keyed UTF-16 column cursors. Shared by BOTH multiline engines (the
/// hand-rolled `regex::bytes` scan and the grep-searcher scan) so they emit
/// byte-identical records — the basis of the A/B correctness oracle (plan §5).
/// `mstart`/`mend` MUST be non-decreasing across calls that reuse the cursors.
pub(crate) fn map_match_to_record(
    hay: &[u8],
    line_starts: &[usize],
    mstart: usize,
    mend: usize,
    start_cur: &mut LineColCursor,
    end_cur: &mut LineColCursor,
    options: &ScanOptions,
) -> MultilineMatchRecord {
    let start_line_idx = line_index_of(line_starts, mstart);
    let end_line_idx = line_index_of(line_starts, mend);

    let start_line_begin = line_starts[start_line_idx];
    let start_line_end = line_content_end(line_starts, hay, start_line_idx);
    let start_line_bytes = &hay[start_line_begin..start_line_end];

    let start_col_byte = mstart - start_line_begin;
    let first_line_visible_end = mend.min(start_line_end);
    let first_line_visible_len = first_line_visible_end - mstart;

    let (line_vec, display_start) =
        copy_match_line_for_record(start_line_bytes, start_col_byte, first_line_visible_len);

    let source_match_start = if needs_eager_source_col(start_line_bytes, options) {
        start_cur.col_at(start_line_idx, start_line_bytes, start_col_byte)
    } else {
        u32::MAX
    };

    let end_line_begin = line_starts[end_line_idx];
    let end_line_end = line_content_end(line_starts, hay, end_line_idx);
    let end_line_bytes = &hay[end_line_begin..end_line_end];
    let end_col_byte = (mend - end_line_begin).min(end_line_bytes.len());
    let end_col = end_cur.col_at(end_line_idx, end_line_bytes, end_col_byte);

    let context_before =
        build_context_before(hay, line_starts, start_line_idx, options.context_before);
    let context_after = build_context_after(hay, line_starts, end_line_idx, options.context_after);

    MultilineMatchRecord {
        line_number: (start_line_idx + 1) as u64,
        match_start: display_start,
        source_match_start,
        match_len: first_line_visible_len as u32,
        end_line: (end_line_idx + 1) as u64,
        end_col,
        line: line_vec,
        context_before,
        context_after,
    }
}

/// Convenience: build the matcher and scan in one call (used by tests and the
/// non-session FFI path). Session/streaming paths build the matcher once and
/// call [`scan_multiline_bytes`] directly.
pub fn scan_multiline(
    bytes: &[u8],
    pattern: &str,
    options: &ScanOptions,
    should_cancel: impl FnMut() -> bool,
    emit: impl FnMut(MultilineMatchRecord) -> bool,
) -> Result<usize, ScanError> {
    let re = build_multiline_regex(pattern, options)?;
    scan_multiline_bytes(bytes, &re, options, should_cancel, emit)
}

/// Line-start table over the LF buffer (mirror of the managed
/// `ContentSearcher.BuildLineStarts`): `[0]` plus one entry just past every
/// `\n`. Line `i` (0-based) occupies `hay[line_starts[i] .. line_end(i)]` with
/// the `\n` excluded. A trailing `\n` yields a phantom empty final line — the
/// same shape the managed side produces, so line numbers match exactly.
pub(crate) fn build_line_starts(hay: &[u8]) -> Vec<usize> {
    let mut starts = Vec::with_capacity(hay.len() / 40 + 1);
    starts.push(0);
    for p in memchr_iter(b'\n', hay) {
        starts.push(p + 1);
    }
    starts
}

/// 0-based index of the line containing `offset` (greatest start `<= offset`).
/// `line_starts[0] == 0` guarantees a result for any `offset >= 0`.
fn line_index_of(line_starts: &[usize], offset: usize) -> usize {
    match line_starts.binary_search(&offset) {
        Ok(i) => i,
        // Insertion point `i >= 1` (starts[0]==0 <= offset), so `i - 1` is the
        // greatest start strictly less than `offset`.
        Err(i) => i - 1,
    }
}

/// Exclusive end (content end, `\n` excluded) of line `line_idx`.
fn line_content_end(line_starts: &[usize], hay: &[u8], line_idx: usize) -> usize {
    if line_idx + 1 < line_starts.len() {
        line_starts[line_idx + 1] - 1
    } else {
        hay.len()
    }
}

fn line_text<'a>(hay: &'a [u8], line_starts: &[usize], line_idx: usize) -> &'a [u8] {
    let begin = line_starts[line_idx];
    let end = line_content_end(line_starts, hay, line_idx);
    &hay[begin..end.max(begin)]
}

fn build_context_before(
    hay: &[u8],
    line_starts: &[usize],
    start_line_idx: usize,
    context_lines: usize,
) -> Vec<Vec<u8>> {
    if context_lines == 0 {
        return Vec::new();
    }
    let from = start_line_idx.saturating_sub(context_lines);
    if from >= start_line_idx {
        return Vec::new();
    }
    let mut out = Vec::with_capacity(start_line_idx - from);
    for li in from..start_line_idx {
        out.push(copy_context_line_for_record(line_text(hay, line_starts, li)));
    }
    out
}

fn build_context_after(
    hay: &[u8],
    line_starts: &[usize],
    end_line_idx: usize,
    context_lines: usize,
) -> Vec<Vec<u8>> {
    if context_lines == 0 {
        return Vec::new();
    }
    let last_line_idx = line_starts.len() - 1;
    let to = (end_line_idx + context_lines).min(last_line_idx);
    if to <= end_line_idx {
        return Vec::new();
    }
    let mut out = Vec::with_capacity(to - end_line_idx);
    for li in (end_line_idx + 1)..=to {
        out.push(copy_context_line_for_record(line_text(hay, line_starts, li)));
    }
    out
}

/// A line-keyed wrapper around [`Utf16ColCursor`]: resets the cursor when the
/// tracked line changes so multiple matches sharing a line decode that line's
/// prefix once total (O(line)), never per-match `utf16_col` (O(line^2)).
pub(crate) struct LineColCursor {
    line: usize,
    cursor: Utf16ColCursor,
}

impl LineColCursor {
    pub(crate) fn new() -> Self {
        Self {
            line: usize::MAX,
            cursor: Utf16ColCursor::new(),
        }
    }

    pub(crate) fn col_at(&mut self, line_idx: usize, line_bytes: &[u8], byte_off: usize) -> u32 {
        if self.line != line_idx {
            self.line = line_idx;
            self.cursor = Utf16ColCursor::new();
        }
        self.cursor.col_at(line_bytes, byte_off)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn opts(use_regex: bool, dotall: bool) -> ScanOptions {
        ScanOptions {
            case_sensitive: true,
            use_regex,
            context_before: 0,
            context_after: 0,
            max_results: 0,
            skip_binary: false,
            ascii_case_only: false,
            metadata_only: false,
            multi_line: true,
            multi_line_dotall: dotall,
            multiline_engine: 0,
        }
    }

    /// Collect all records for a pattern over `bytes`.
    fn run(bytes: &[u8], pattern: &str, o: &ScanOptions) -> Vec<MultilineMatchRecord> {
        let mut recs = Vec::new();
        let n = scan_multiline(bytes, pattern, o, || false, |r| {
            recs.push(r);
            true
        })
        .expect("scan ok");
        assert_eq!(n, recs.len());
        recs
    }

    #[test]
    fn normalize_lf_identity_when_no_cr() {
        let b = b"line1\nline2\n";
        assert!(matches!(normalize_to_lf(b), Cow::Borrowed(_)));
    }

    #[test]
    fn normalize_crlf_and_lone_cr() {
        let b = b"a\r\nb\rc\n";
        let lf = normalize_to_lf(b);
        assert_eq!(lf.as_ref(), b"a\nb\nc\n");
    }

    #[test]
    fn cross_line_match_reports_start_and_end() {
        // "foo" on line 1, "bar" on line 2; a dotall pattern spans the newline.
        let bytes = b"xx foo\nbar yy\nzz\n";
        let recs = run(bytes, "foo.bar", &opts(true, true));
        assert_eq!(recs.len(), 1);
        let r = &recs[0];
        assert_eq!(r.line_number, 1);
        assert_eq!(r.end_line, 2);
        // start col of "foo" is byte 3 on line 1 ("xx foo"); the full-line UTF-16
        // column is deferred to the managed side on this short ASCII line.
        assert_eq!(r.match_start, 3);
        assert_eq!(r.source_match_start, u32::MAX);
        // end col: match "foo\nbar" ends after "bar" -> col 3 on line 2.
        assert_eq!(r.end_col, 3);
        // first-line-visible length = "foo" = 3 bytes.
        assert_eq!(r.match_len, 3);
        // display line is the START line only.
        assert_eq!(r.line, b"xx foo");
    }

    #[test]
    fn dotall_off_dot_does_not_cross_newline() {
        let bytes = b"foo\nbar\n";
        // Without dotall, "foo.bar" cannot match across the newline.
        assert!(run(bytes, "foo.bar", &opts(true, false)).is_empty());
        // With dotall it matches.
        assert_eq!(run(bytes, "foo.bar", &opts(true, true)).len(), 1);
    }

    #[test]
    fn dollar_anchors_at_internal_line_boundary() {
        // multi_line(true): `$` binds at each internal '\n'.
        let bytes = b"end\nmiddle\nend\n";
        let recs = run(bytes, "end$", &opts(true, false));
        assert_eq!(recs.len(), 2);
        assert_eq!(recs[0].line_number, 1);
        assert_eq!(recs[1].line_number, 3);
    }

    #[test]
    fn crlf_dollar_excludes_trailing_cr() {
        // On a CRLF file, `foo$` matches "foo" with the '\r' NOT in the span,
        // because we scan the LF-normalized buffer.
        let bytes = b"foo\r\nbar\r\n";
        let recs = run(bytes, "foo$", &opts(true, false));
        assert_eq!(recs.len(), 1);
        assert_eq!(recs[0].match_len, 3); // "foo", no '\r'
        assert_eq!(recs[0].line, b"foo");
    }

    #[test]
    fn crlf_cross_line_span_excludes_cr() {
        // Cross-line span on CRLF: "foo\nbar" over the normalized buffer.
        let bytes = b"foo\r\nbar\r\n";
        let recs = run(bytes, "foo\\nbar", &opts(true, false));
        assert_eq!(recs.len(), 1);
        let r = &recs[0];
        assert_eq!(r.line_number, 1);
        assert_eq!(r.end_line, 2);
        assert_eq!(r.end_col, 3);
    }

    #[test]
    fn zero_width_matches_dropped() {
        // `^` is zero-width at every line start; must emit nothing (not loop).
        let bytes = b"a\nb\nc\n";
        assert!(run(bytes, "^", &opts(true, false)).is_empty());
    }

    #[test]
    fn literal_is_escaped_not_treated_as_regex() {
        // A literal with regex metacharacters matches literally.
        let bytes = b"a.b a+b\n";
        let recs = run(bytes, "a.b", &opts(false, false));
        assert_eq!(recs.len(), 1);
        assert_eq!(recs[0].match_start, 0); // matches "a.b" at col 0, not "a+b"
        assert_eq!(recs[0].match_len, 3);
    }

    #[test]
    fn context_after_numbered_from_end_line() {
        // Span covers lines 1..2; after-context (1 line) must be line 3.
        let bytes = b"foo\nbar\nAFTER\nzzz\n";
        let mut o = opts(true, true);
        o.context_before = 0;
        o.context_after = 1;
        let recs = run(bytes, "foo.bar", &o);
        assert_eq!(recs.len(), 1);
        assert_eq!(recs[0].context_after, vec![b"AFTER".to_vec()]);
    }

    #[test]
    fn context_before_from_start_line() {
        let bytes = b"BEFORE\nfoo\nbar\n";
        let mut o = opts(true, true);
        o.context_before = 1;
        o.context_after = 0;
        let recs = run(bytes, "foo.bar", &o);
        assert_eq!(recs.len(), 1);
        assert_eq!(recs[0].context_before, vec![b"BEFORE".to_vec()]);
    }

    #[test]
    fn source_match_start_deferred_in_common_path() {
        // Short ASCII start line -> common path -> sentinel (managed resolves it).
        let bytes = b"hello world\nfoo\n";
        let recs = run(bytes, "world", &opts(true, false));
        assert_eq!(recs.len(), 1);
        assert_eq!(recs[0].source_match_start, u32::MAX);
    }

    #[test]
    fn end_col_is_utf16_on_nonascii_end_line() {
        // End line has an astral char (💩 = 2 UTF-16 units) before the match end.
        // Line 2 = "💩bar": end of "bar" is UTF-16 col 2 (💩) + 3 (bar) = 5.
        let bytes = "foo\n💩bar\n".as_bytes();
        let recs = run(bytes, "foo.\u{1F4A9}bar", &opts(true, true));
        assert_eq!(recs.len(), 1);
        assert_eq!(recs[0].end_line, 2);
        assert_eq!(recs[0].end_col, 5);
    }

    #[test]
    fn cancellation_between_matches_stops_early() {
        let bytes = b"m\nm\nm\nm\n";
        let mut count = 0;
        let mut emitted = Vec::new();
        // Cancel after the first match is yielded.
        let n = scan_multiline_bytes(
            bytes,
            &build_multiline_regex("m", &opts(true, false)).unwrap(),
            &opts(true, false),
            || {
                count += 1;
                count > 2 // false for the pre-scan poll and first match, then cancel
            },
            |r| {
                emitted.push(r);
                true
            },
        )
        .unwrap();
        assert!(n < 4, "cancellation should stop before all four matches");
    }

    #[test]
    fn max_results_caps_emission() {
        let bytes = b"m\nm\nm\nm\n";
        let mut o = opts(true, false);
        o.max_results = 2;
        assert_eq!(run(bytes, "m", &o).len(), 2);
    }

    #[test]
    fn emit_returning_false_stops_early() {
        let bytes = b"m\nm\nm\n";
        let mut seen = 0;
        let n = scan_multiline(bytes, "m", &opts(true, false), || false, |_| {
            seen += 1;
            seen < 2 // stop after the second emit
        })
        .unwrap();
        assert_eq!(n, 1); // returns count of fully-accepted emits before the stop
        assert_eq!(seen, 2);
    }

    #[test]
    fn binary_buffer_skipped_when_skip_binary() {
        let mut o = opts(true, false);
        o.skip_binary = true;
        let bytes = b"foo\x00\x00\x00bar\n";
        let err = scan_multiline(bytes, "foo", &o, || false, |_| true).unwrap_err();
        assert!(matches!(err, ScanError::BinarySkipped));
    }

    #[test]
    fn lookaround_pattern_is_invalid_regex() {
        let o = opts(true, false);
        let err = build_multiline_regex("(?<=foo)bar", &o).unwrap_err();
        assert!(matches!(err, ScanError::InvalidRegex(_)));
    }

    #[test]
    fn single_line_match_reports_equal_start_end_line() {
        // A single-line hit during a multiline search: end_line == line_number.
        let bytes = b"hello foo world\n";
        let recs = run(bytes, "foo", &opts(true, false));
        assert_eq!(recs.len(), 1);
        assert_eq!(recs[0].line_number, 1);
        assert_eq!(recs[0].end_line, 1);
    }

    #[test]
    fn multiple_spans_on_same_start_line_both_emit() {
        // Two cross-line spans that both START on line 1.
        let bytes = b"aX bX\nY\n";
        // Pattern: letter then 'X' then newline then 'Y' won't fit twice; use a
        // simpler case: two matches "X\n?"... instead verify two single-line
        // occurrences on one line are both emitted (co-line spans, plan §4).
        let recs = run(bytes, "X", &opts(true, false));
        assert_eq!(recs.len(), 2);
        assert_eq!(recs[0].line_number, 1);
        assert_eq!(recs[1].line_number, 1);
        assert_eq!(recs[0].source_match_start, u32::MAX); // deferred
    }
}
