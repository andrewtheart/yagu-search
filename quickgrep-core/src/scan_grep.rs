//! Phase 6: alternative scan path built on ripgrep's library crates
//! (`grep-searcher`, `grep-regex`, `grep-matcher`).
//!
//! Public surface:
//!
//! * [`GrepSession`] — long-lived holder of a `RegexMatcher` for reuse across
//!   many files. **This is the only path that should be used from the FFI
//!   streaming/packed loops** — per-call construction is ~5x slower on small
//!   files (see `tests/phase6_bench.rs`).
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
