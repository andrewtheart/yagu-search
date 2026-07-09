//! Phase 6: alternative scan path built on ripgrep's library crates
//! (`grep-searcher`, `grep-regex`, `grep-matcher`).
//!
//! Public surface:
//!
//! * [`GrepSession`] — long-lived holder of a `RegexMatcher` for reuse across
//!   many files. **This is the only path that should be used from the FFI
//!   streaming/packed loops** — per-call construction is ~5x slower on small
//!   files (measured in the Phase 6 grep spike; the standalone benchmark was
//!   removed as a settled experiment — see git history / `PLANS/RIPGREP_PHASE6_SPIKE.md`).
//! * [`scan_bytes_grep`] — convenience wrapper that builds a one-shot session.
//!
//! Feature-gated behind `grep_crates`.

use std::collections::VecDeque;

use grep_matcher::{Match, Matcher};
use grep_regex::{RegexMatcher, RegexMatcherBuilder};
use grep_searcher::{
    BinaryDetection, Searcher, SearcherBuilder, Sink, SinkContext, SinkContextKind, SinkError,
    SinkMatch,
};

use crate::scan::{
    copy_context_line_for_record, copy_match_line_for_record, MatchRecord, ScanError, ScanOptions,
};

/// Reusable scan session built on `grep-searcher` / `grep-regex`. Construct
/// once per pattern, then call [`GrepSession::scan`] for each file.
pub struct GrepSession {
    matcher: RegexMatcher,
}

impl GrepSession {
    /// Build a session for `pattern` honouring the same case-sensitivity /
    /// literal-vs-regex semantics as [`crate::scan::build_matcher`].
    pub fn new(pattern: &str, options: &ScanOptions) -> Result<Self, ScanError> {
        let final_pattern = if options.use_regex {
            pattern.to_owned()
        } else {
            regex::escape(pattern)
        };

        let matcher = RegexMatcherBuilder::new()
            .case_insensitive(!options.case_sensitive)
            .case_smart(false)
            .line_terminator(Some(b'\n'))
            .build(&final_pattern)
            .map_err(|e| ScanError::InvalidRegex(e.to_string()))?;

        Ok(Self { matcher })
    }

    /// Scan a byte buffer using this session's compiled matcher. Cancellation
    /// is polled at every sink event; `emit` is called once per match with
    /// fully-populated context.
    pub fn scan<C, E>(
        &self,
        bytes: &[u8],
        options: &ScanOptions,
        should_cancel: C,
        emit: E,
    ) -> Result<usize, ScanError>
    where
        C: FnMut() -> bool,
        E: FnMut(MatchRecord) -> bool,
    {
        let mut searcher = SearcherBuilder::new()
            .line_number(true)
            .binary_detection(if options.skip_binary {
                BinaryDetection::quit(0)
            } else {
                BinaryDetection::none()
            })
            .before_context(options.context_before)
            .after_context(options.context_after)
            .build();

        let mut sink = QgSink {
            matcher: &self.matcher,
            emit,
            should_cancel,
            emitted: 0,
            max_results: options.max_results,
            context_before_cap: options.context_before,
            context_after_cap: options.context_after,
            before: VecDeque::with_capacity(options.context_before),
            pending: VecDeque::new(),
            binary_skipped: false,
            cancelled: false,
        };

        searcher
            .search_slice(&self.matcher, bytes, &mut sink)
            .map_err(|e| ScanError::Io(std::io::Error::other(e.to_string())))?;

        if sink.binary_skipped {
            return Err(ScanError::BinarySkipped);
        }
        if sink.cancelled {
            return Ok(sink.emitted);
        }

        // Flush pending after-context records that never reached their quota
        // (matches near EOF).
        let QgSink {
            mut emit,
            mut pending,
            mut emitted,
            max_results,
            ..
        } = sink;
        while let Some((rec, _)) = pending.pop_front() {
            emitted += 1;
            let keep = emit(rec);
            if !keep {
                break;
            }
            if max_results != 0 && emitted >= max_results {
                break;
            }
        }
        Ok(emitted)
    }
}

/// One-shot convenience wrapper. Do **not** call this in a tight per-file
/// loop — use [`GrepSession`] for reuse instead.
pub fn scan_bytes_grep<F>(
    bytes: &[u8],
    pattern: &str,
    options: &ScanOptions,
    emit: F,
) -> Result<usize, ScanError>
where
    F: FnMut(MatchRecord) -> bool,
{
    let session = GrepSession::new(pattern, options)?;
    session.scan(bytes, options, || false, emit)
}

/// Whole-buffer cross-line (multiline) scan built on ripgrep's `grep-searcher`
/// (the ALTERNATE Phase 2 engine, `multiline_engine == 1`, plan §5). It scans
/// the SAME LF-normalized buffer and feeds every match's absolute byte-span
/// through the SHARED [`crate::scan_multiline::map_match_to_record`], so it emits
/// records byte-identical to the default hand-rolled engine — the A/B
/// correctness oracle. Yagu's own `looks_binary` classifies text/binary (not
/// grep's `BinaryDetection`), and context is computed from the line table (not
/// grep's context callbacks), so the two engines can never diverge.
pub fn scan_multiline_grep<C, E>(
    bytes: &[u8],
    pattern: &str,
    options: &ScanOptions,
    mut should_cancel: C,
    emit: E,
) -> Result<usize, ScanError>
where
    C: FnMut() -> bool,
    E: FnMut(crate::scan_multiline::MultilineMatchRecord) -> bool,
{
    // Binary sniff on RAW bytes with Yagu's classifier (parity with the default
    // engine), then scan the LF-normalized buffer.
    if options.skip_binary && crate::scan::looks_binary(bytes) {
        return Err(ScanError::BinarySkipped);
    }
    if should_cancel() {
        return Ok(0);
    }

    let lf = crate::scan_multiline::normalize_to_lf(bytes);
    let hay: &[u8] = lf.as_ref();
    let line_starts = crate::scan_multiline::build_line_starts(hay);

    let final_pattern = if options.use_regex {
        pattern.to_owned()
    } else {
        regex::escape(pattern)
    };
    // NOTE: no `.line_terminator(..)` on the matcher — that would forbid matching
    // across `\n` (it is what the single-line GrepSession sets). Multiline needs
    // the matcher free to span line breaks.
    let matcher = RegexMatcherBuilder::new()
        .case_insensitive(!options.case_sensitive)
        .case_smart(false)
        .multi_line(true)
        .dot_matches_new_line(options.multi_line_dotall)
        .build(&final_pattern)
        .map_err(|e| ScanError::InvalidRegex(e.to_string()))?;

    let mut searcher = SearcherBuilder::new()
        .multi_line(true)
        .line_number(false) // line numbers come from the shared line table
        .binary_detection(BinaryDetection::none())
        .build();

    let mut sink = MlSpanSink {
        matcher: &matcher,
        hay,
        line_starts: &line_starts,
        options,
        start_cur: crate::scan_multiline::LineColCursor::new(),
        end_cur: crate::scan_multiline::LineColCursor::new(),
        emit,
        should_cancel,
        emitted: 0,
        max_results: options.max_results,
    };

    searcher
        .search_slice(&matcher, hay, &mut sink)
        .map_err(|e| ScanError::Io(std::io::Error::other(e.to_string())))?;

    Ok(sink.emitted)
}

/// Span sink for [`scan_multiline_grep`]. Each `SinkMatch` block is re-scanned
/// with the matcher (grep-searcher gives block bounds, not per-match spans in
/// multiline mode); each match's absolute byte-span is mapped by the shared
/// [`crate::scan_multiline::map_match_to_record`]. Zero-width matches are dropped
/// to match the default engine.
struct MlSpanSink<'a, C, E>
where
    C: FnMut() -> bool,
    E: FnMut(crate::scan_multiline::MultilineMatchRecord) -> bool,
{
    matcher: &'a RegexMatcher,
    hay: &'a [u8],
    line_starts: &'a [usize],
    options: &'a ScanOptions,
    start_cur: crate::scan_multiline::LineColCursor,
    end_cur: crate::scan_multiline::LineColCursor,
    emit: E,
    should_cancel: C,
    emitted: usize,
    max_results: usize,
}

impl<C, E> Sink for MlSpanSink<'_, C, E>
where
    C: FnMut() -> bool,
    E: FnMut(crate::scan_multiline::MultilineMatchRecord) -> bool,
{
    type Error = SinkAbort;

    fn matched(
        &mut self,
        _searcher: &Searcher,
        sink_match: &SinkMatch<'_>,
    ) -> Result<bool, Self::Error> {
        if (self.should_cancel)() {
            return Ok(false);
        }

        let block = sink_match.bytes();
        let block_abs = sink_match.absolute_byte_offset() as usize;

        // Collect the block's match ranges first (the matcher borrow must end
        // before we take the mutable self borrows in map_match_to_record).
        let mut ranges: Vec<Match> = Vec::new();
        self.matcher
            .find_iter(block, |mat| {
                ranges.push(mat);
                true
            })
            .map_err(|_| SinkAbort)?;

        for mat in ranges {
            if mat.start() == mat.end() {
                continue; // drop zero-width, matching the default engine
            }
            let abs_start = block_abs + mat.start();
            let abs_end = block_abs + mat.end();
            let rec = crate::scan_multiline::map_match_to_record(
                self.hay,
                self.line_starts,
                abs_start,
                abs_end,
                &mut self.start_cur,
                &mut self.end_cur,
                self.options,
            );
            self.emitted += 1;
            if !(self.emit)(rec) {
                return Ok(false);
            }
            if self.max_results != 0 && self.emitted >= self.max_results {
                return Ok(false);
            }
        }
        Ok(true)
    }
}

/// Adapter between ripgrep's event-based searcher and Yagu's match-record
/// shape. It keeps before-context in a ring, delays records that still need
/// after-context, and emits only when a match is complete.
struct QgSink<'m, C, E>
where
    C: FnMut() -> bool,
    E: FnMut(MatchRecord) -> bool,
{
    matcher: &'m RegexMatcher,
    emit: E,
    should_cancel: C,
    emitted: usize,
    max_results: usize,
    context_before_cap: usize,
    context_after_cap: usize,
    before: VecDeque<Vec<u8>>,
    pending: VecDeque<(MatchRecord, usize)>,
    binary_skipped: bool,
    cancelled: bool,
}

#[derive(Debug)]
struct SinkAbort;
impl std::fmt::Display for SinkAbort {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("sink aborted")
    }
}
impl std::error::Error for SinkAbort {}
impl SinkError for SinkAbort {
    fn error_message<T: std::fmt::Display>(_message: T) -> Self {
        SinkAbort
    }
}

impl<C, E> QgSink<'_, C, E>
where
    C: FnMut() -> bool,
    E: FnMut(MatchRecord) -> bool,
{
    fn fill_after_context(&mut self, line: &[u8]) -> Result<bool, SinkAbort> {
        if self.pending.is_empty() {
            return Ok(true);
        }
        let prepared = if self.context_after_cap > 0 {
            Some(copy_context_line_for_record(line))
        } else {
            None
        };

        for entry in self.pending.iter_mut() {
            if entry.1 > 0 {
                if let Some(p) = prepared.as_ref() {
                    entry.0.context_after.push(p.clone());
                }
                entry.1 -= 1;
            }
        }

        while let Some(front) = self.pending.front() {
            if front.1 != 0 {
                break;
            }
            let (rec, _) = self.pending.pop_front().unwrap();
            self.emitted += 1;
            let keep = (self.emit)(rec);
            if !keep {
                self.cancelled = true;
                return Ok(false);
            }
            if self.max_results != 0 && self.emitted >= self.max_results {
                return Ok(false);
            }
        }
        Ok(true)
    }

    fn push_before_context(&mut self, line: &[u8]) {
        if self.context_before_cap == 0 {
            return;
        }
        if self.before.len() == self.context_before_cap {
            self.before.pop_front();
        }
        self.before.push_back(copy_context_line_for_record(line));
    }
}

impl<C, E> Sink for QgSink<'_, C, E>
where
    C: FnMut() -> bool,
    E: FnMut(MatchRecord) -> bool,
{
    type Error = SinkAbort;

    fn matched(
        &mut self,
        _searcher: &Searcher,
        sink_match: &SinkMatch<'_>,
    ) -> Result<bool, Self::Error> {
        if (self.should_cancel)() {
            self.cancelled = true;
            return Ok(false);
        }

        let line_bytes = sink_match.bytes();
        let trimmed_len = trimmed_line_len(line_bytes);
        let line_view = &line_bytes[..trimmed_len];
        let line_number = sink_match.line_number().unwrap_or(0);

        if !self.fill_after_context(line_view)? {
            return Ok(false);
        }

        // Per-line UTF-16 column cursor: this sink resolves source columns
        // eagerly for every match, so it MUST advance one cursor per line in
        // O(line) — NOT call `utf16_col` per match (the O(line^2) regression;
        // see `crate::scan::Utf16ColCursor`).
        let mut col_cursor = crate::scan::Utf16ColCursor::new();
        let mut search_from = 0usize;
        while search_from <= trimmed_len {
            let region = &line_view[search_from..];
            let m: Option<Match> = self.matcher.find(region).map_err(|_| SinkAbort)?;
            let Some(m) = m else { break };

            let abs_start = search_from + m.start();
            let abs_end = search_from + m.end();
            let match_len = abs_end - abs_start;

            if match_len == 0 {
                search_from = abs_start + 1;
                continue;
            }

            let (line_truncated, display_start) =
                copy_match_line_for_record(line_view, abs_start, match_len);

            let rec = MatchRecord {
                line_number,
                match_start: display_start,
                source_match_start: col_cursor.col_at(line_view, abs_start),
                match_len: match_len as u32,
                line: line_truncated,
                context_before: self.before.iter().cloned().collect(),
                context_after: Vec::with_capacity(self.context_after_cap),
            };

            if self.context_after_cap == 0 {
                self.emitted += 1;
                let keep = (self.emit)(rec);
                if !keep {
                    self.cancelled = true;
                    return Ok(false);
                }
                if self.max_results != 0 && self.emitted >= self.max_results {
                    return Ok(false);
                }
            } else {
                self.pending.push_back((rec, self.context_after_cap));
            }

            search_from = abs_end;
        }

        // The matched line itself acts as before-context for *later* matches.
        self.push_before_context(line_view);
        Ok(true)
    }

    fn context(
        &mut self,
        _searcher: &Searcher,
        sink_ctx: &SinkContext<'_>,
    ) -> Result<bool, Self::Error> {
        if (self.should_cancel)() {
            self.cancelled = true;
            return Ok(false);
        }

        let line_bytes = sink_ctx.bytes();
        let trimmed_len = trimmed_line_len(line_bytes);
        let line_view = &line_bytes[..trimmed_len];

        match sink_ctx.kind() {
            SinkContextKind::Before => {
                self.push_before_context(line_view);
            }
            SinkContextKind::After => {
                if !self.fill_after_context(line_view)? {
                    return Ok(false);
                }
            }
            SinkContextKind::Other => {}
        }
        Ok(true)
    }

    fn binary_data(
        &mut self,
        _searcher: &Searcher,
        _binary_byte_offset: u64,
    ) -> Result<bool, Self::Error> {
        self.binary_skipped = true;
        Ok(false)
    }
}

fn trimmed_line_len(line: &[u8]) -> usize {
    let mut n = line.len();
    while n > 0 && (line[n - 1] == b'\n' || line[n - 1] == b'\r') {
        n -= 1;
    }
    n
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::scan::scan_bytes_ex;

    fn opts() -> ScanOptions {
        ScanOptions {
            case_sensitive: false,
            use_regex: false,
            context_before: 0,
            context_after: 0,
            max_results: 0,
            skip_binary: false,
            ascii_case_only: false,
            metadata_only: false,
            multi_line: false,
            multi_line_dotall: false,
            multiline_engine: 0,
            max_matches_per_line: 0,
        }
    }

    type ParityRow = (u64, u32, u32, Vec<u8>, Vec<Vec<u8>>, Vec<Vec<u8>>);

    fn collect(records: &mut Vec<ParityRow>) -> impl FnMut(MatchRecord) -> bool + '_ {
        move |r: MatchRecord| {
            records.push((
                r.line_number,
                r.match_start,
                r.match_len,
                r.line,
                r.context_before,
                r.context_after,
            ));
            true
        }
    }

    fn parity(bytes: &[u8], pattern: &str, options: &ScanOptions) {
        let mut a = Vec::new();
        let _ = scan_bytes_ex(bytes, pattern, options, || false, collect(&mut a)).unwrap();
        let mut b = Vec::new();
        let _ = scan_bytes_grep(bytes, pattern, options, collect(&mut b)).unwrap();
        assert_eq!(a, b);
    }

    #[test]
    fn parity_simple_literal_case_insensitive() {
        let bytes = b"foo bar Foo\nfoo FOO foo\nno match here\nlast Foo\n";
        parity(bytes, "foo", &opts());
    }

    #[test]
    fn parity_case_sensitive_literal() {
        let bytes = b"foo Foo FOO\nFoo foo\n";
        let mut o = opts();
        o.case_sensitive = true;
        parity(bytes, "Foo", &o);
    }

    #[test]
    fn parity_regex() {
        let bytes = b"err 42\nwarn 7\nerr 100\ninfo\n";
        let mut o = opts();
        o.use_regex = true;
        o.case_sensitive = true;
        parity(bytes, r"err \d+", &o);
    }

    #[test]
    fn parity_max_results_cap() {
        let bytes = b"a\na\na\na\na\n";
        let mut o = opts();
        o.case_sensitive = true;
        o.max_results = 3;

        let mut a = Vec::new();
        let _ = scan_bytes_ex(bytes, "a", &o, || false, collect(&mut a)).unwrap();
        let mut b = Vec::new();
        let _ = scan_bytes_grep(bytes, "a", &o, collect(&mut b)).unwrap();

        assert_eq!(a.len(), 3);
        assert_eq!(a, b);
    }

    #[test]
    fn parity_multiple_matches_per_line() {
        let bytes = b"foo foo foo bar foo\n";
        let mut o = opts();
        o.case_sensitive = true;

        let mut a = Vec::new();
        let _ = scan_bytes_ex(bytes, "foo", &o, || false, collect(&mut a)).unwrap();
        let mut b = Vec::new();
        let _ = scan_bytes_grep(bytes, "foo", &o, collect(&mut b)).unwrap();

        assert_eq!(a.len(), 4);
        assert_eq!(a, b);
    }

    #[test]
    fn parity_with_before_context() {
        let bytes = b"line1\nline2\nline3 hit\nline4\nline5 hit\nline6\n";
        let mut o = opts();
        o.case_sensitive = true;
        o.context_before = 2;
        parity(bytes, "hit", &o);
    }

    #[test]
    fn parity_with_after_context() {
        let bytes = b"line1\nline2 hit\nline3\nline4\nline5 hit\nline6\nline7\n";
        let mut o = opts();
        o.case_sensitive = true;
        o.context_after = 2;
        parity(bytes, "hit", &o);
    }

    #[test]
    fn parity_with_both_contexts() {
        let bytes = b"a\nb\nc hit\nd\ne\nf\ng hit\nh\ni\n";
        let mut o = opts();
        o.case_sensitive = true;
        o.context_before = 1;
        o.context_after = 1;
        parity(bytes, "hit", &o);
    }

    #[test]
    fn parity_context_at_eof() {
        let bytes = b"a\nb\nc\nhit\n";
        let mut o = opts();
        o.case_sensitive = true;
        o.context_after = 5;
        parity(bytes, "hit", &o);
    }

    #[test]
    fn cancellation_polled() {
        let bytes = b"hit\nhit\nhit\nhit\nhit\nhit\nhit\nhit\nhit\nhit\n";
        let mut o = opts();
        o.case_sensitive = true;

        let session = GrepSession::new("hit", &o).unwrap();
        let count = std::cell::Cell::new(0usize);
        let _ = session
            .scan(
                bytes,
                &o,
                || count.get() >= 3,
                |_| {
                    count.set(count.get() + 1);
                    true
                },
            )
            .unwrap();
        let final_count = count.get();
        assert!(final_count <= 5, "cancellation observed (got {final_count})");
    }

    #[test]
    fn session_reuse_across_files() {
        let mut o = opts();
        o.case_sensitive = true;
        let session = GrepSession::new("hit", &o).unwrap();

        let buffers: [&[u8]; 3] = [b"hit\n", b"miss\nhit\n", b"hit hit\n"];
        let mut total = 0usize;
        for b in buffers {
            let _ = session
                .scan(b, &o, || false, |_| {
                    total += 1;
                    true
                })
                .unwrap();
        }
        assert_eq!(total, 4);
    }
}

/// A/B correctness oracle (plan §5): the grep-searcher multiline engine
/// (`scan_multiline_grep`) MUST emit records byte-identical to the default
/// hand-rolled engine (`scan_multiline`) — they scan the same LF buffer and
/// share the span mapper, so any divergence is a bug.
#[cfg(test)]
mod ml_ab_tests {
    use crate::scan::ScanOptions;
    use crate::scan_grep::scan_multiline_grep;
    use crate::scan_multiline::{scan_multiline, MultilineMatchRecord};

    fn opts(dotall: bool) -> ScanOptions {
        ScanOptions {
            case_sensitive: true,
            use_regex: true,
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

    fn hand_rolled(bytes: &[u8], pattern: &str, o: &ScanOptions) -> Vec<MultilineMatchRecord> {
        let mut recs = Vec::new();
        scan_multiline(bytes, pattern, o, || false, |r| {
            recs.push(r);
            true
        })
        .unwrap();
        recs
    }

    fn grep(bytes: &[u8], pattern: &str, o: &ScanOptions) -> Vec<MultilineMatchRecord> {
        let mut recs = Vec::new();
        scan_multiline_grep(bytes, pattern, o, || false, |r| {
            recs.push(r);
            true
        })
        .unwrap();
        recs
    }

    #[test]
    fn ab_oracle_engines_emit_identical_records() {
        // (bytes, pattern, dotall)
        let cases: &[(&[u8], &str, bool)] = &[
            (b"xx foo\nbar yy\nzz\n", "foo.bar", true),          // cross-line dotall
            (b"foo\r\nbar\r\n", "foo$", false),                  // CRLF, $ excludes '\r'
            (b"foo\r\nbar\r\n", "foo\\nbar", false),             // CRLF cross-line span
            (b"end\nmiddle\nend\n", "end$", false),              // multiline $ anchor (2 hits)
            (b"A1\nB A2\nB tail\n", "A[\\s\\S]*?B", false),      // lazy cross-line (2 hits)
            (b"foo foo foo\n", "foo", false),                    // multi-match same line
            ("cafe\u{0301} foo\nbar\n".as_bytes(), "foo.bar", true), // non-ASCII start line
            (b"no match here\n", "zzz", false),                  // no match
            (b"line1\nSTART x\ny END\nafter\n", "START[\\s\\S]*?END", true), // span + tail
        ];
        for (bytes, pattern, dotall) in cases {
            let o = opts(*dotall);
            assert_eq!(
                hand_rolled(bytes, pattern, &o),
                grep(bytes, pattern, &o),
                "engines diverge on pattern {pattern:?}"
            );
        }
    }

    #[test]
    fn ab_oracle_with_context_agrees() {
        let bytes = b"before1\nbefore2\nSTARTa\naEND\nafter1\nafter2\n";
        let mut o = opts(true);
        o.context_before = 2;
        o.context_after = 2;
        assert_eq!(
            hand_rolled(bytes, "START[\\s\\S]*?END", &o),
            grep(bytes, "START[\\s\\S]*?END", &o),
        );
    }

    #[test]
    fn ab_oracle_max_results_agrees() {
        let bytes = b"m\nm\nm\nm\nm\n";
        let mut o = opts(false);
        o.max_results = 3;
        let a = hand_rolled(bytes, "m", &o);
        let b = grep(bytes, "m", &o);
        assert_eq!(a.len(), 3);
        assert_eq!(a, b);
    }
}

