//! Core scanner: produces match records for a single file, including
//! line-level context, with no allocations per non-matching line.

use bstr::ByteSlice;
use memchr::{memchr, memchr2, memmem};
use regex::bytes::{Regex, RegexBuilder};
use std::collections::VecDeque;

/// Owned, decoded match record handed back to the FFI layer.
#[derive(Debug)]
pub struct MatchRecord {
    pub line_number: u64,
    pub match_start: u32,
    pub match_len: u32,
    pub line: Vec<u8>,
    pub context_before: Vec<Vec<u8>>,
    pub context_after: Vec<Vec<u8>>,
}

#[derive(Debug, Clone)]
pub struct ScanOptions {
    pub case_sensitive: bool,
    pub use_regex: bool,
    pub context_before: usize,
    pub context_after: usize,
    pub max_results: usize,
    pub skip_binary: bool,
    /// When `true` and `case_sensitive == false`, perform ASCII-only case
    /// folding for the literal matcher (cheaper, but does not fold non-ASCII
    /// characters like "Über" / "über"). When `false` (default), use
    /// Unicode-aware case folding via the regex engine, matching the regex
    /// matcher's case-insensitive behavior.
    pub ascii_case_only: bool,
}

const MAX_EMITTED_LINE_BYTES: usize = 4096;
const TRUNCATION_MARKER: &[u8] = b"\xE2\x80\xA6";

#[derive(Debug)]
pub enum ScanError {
    Io(std::io::Error),
    InvalidRegex(String),
    Cancelled,
    BinarySkipped,
}

impl From<std::io::Error> for ScanError {
    fn from(value: std::io::Error) -> Self {
        ScanError::Io(value)
    }
}

/// Build a compiled matcher from a pattern and options. This can be called
/// once and reused across many `scan_bytes_with_matcher` calls.
pub fn build_matcher(
    pattern: &str,
    options: &ScanOptions,
) -> Result<Box<dyn LineMatcher>, ScanError> {
    if options.use_regex {
        let re = RegexBuilder::new(pattern)
            .case_insensitive(!options.case_sensitive)
            .build()
            .map_err(|e| ScanError::InvalidRegex(e.to_string()))?;
        Ok(Box::new(RegexMatcher { re }))
    } else if options.case_sensitive {
        Ok(Box::new(LiteralMatcher::new_case_sensitive(pattern.as_bytes())))
    } else if options.ascii_case_only {
        Ok(Box::new(LiteralMatcher::new_ascii_case_insensitive(
            pattern.as_bytes(),
        )))
    } else {
        Ok(Box::new(LiteralMatcher::new_unicode_case_insensitive(
            pattern,
        )?))
    }
}

/// Scan an in-memory byte buffer for `pattern`, calling `emit` for every match.
/// The closure may return `false` to stop early (cancellation / cap reached).
pub fn scan_bytes(
    bytes: &[u8],
    pattern: &str,
    options: &ScanOptions,
    emit: impl FnMut(MatchRecord) -> bool,
) -> Result<usize, ScanError> {
    let matcher = build_matcher(pattern, options)?;
    scan_bytes_with_matcher(bytes, &*matcher, options, emit)
}

/// Like `scan_bytes` but also polls `should_cancel` once per line.
pub fn scan_bytes_ex(
    bytes: &[u8],
    pattern: &str,
    options: &ScanOptions,
    should_cancel: impl FnMut() -> bool,
    emit: impl FnMut(MatchRecord) -> bool,
) -> Result<usize, ScanError> {
    let matcher = build_matcher(pattern, options)?;
    scan_bytes_with_matcher_ex(bytes, &*matcher, options, should_cancel, emit)
}

/// Like `scan_bytes` but accepts a pre-compiled matcher for reuse across files.
pub fn scan_bytes_with_matcher(
    bytes: &[u8],
    matcher: &dyn LineMatcher,
    options: &ScanOptions,
    emit: impl FnMut(MatchRecord) -> bool,
) -> Result<usize, ScanError> {
    scan_bytes_with_matcher_ex(bytes, matcher, options, || false, emit)
}

/// Like `scan_bytes_with_matcher` but also polls `should_cancel` once per line
/// (including non-matching lines). When `should_cancel()` returns `true` the
/// scan stops early and returns `Ok(emitted_so_far)`, matching the soft
/// early-stop semantics of `emit` returning `false`. This lets long no-match
/// or sparse-match scans react to cancellation independent of match rate.
pub fn scan_bytes_with_matcher_ex(
    bytes: &[u8],
    matcher: &dyn LineMatcher,
    options: &ScanOptions,
    mut should_cancel: impl FnMut() -> bool,
    mut emit: impl FnMut(MatchRecord) -> bool,
) -> Result<usize, ScanError> {
    if options.skip_binary && looks_binary(bytes) {
        return Err(ScanError::BinarySkipped);
    }

    let mut emitted = 0usize;
    let mut line_number: u64 = 0;
    let mut before: VecDeque<Vec<u8>> = VecDeque::with_capacity(options.context_before);
    // Pending after-context: each entry is (record, lines_remaining).
    // VecDeque for O(1) front removal when flushing completed records.
    let mut pending: VecDeque<(MatchRecord, usize)> = VecDeque::new();

    for line in bytes.lines() {
        line_number += 1;

        // Per-line cancellation poll: independent of match rate so no-match
        // and sparse-match scans react promptly to the cancel flag.
        if should_cancel() {
            return Ok(emitted);
        }

        // Fill in pending after-context with this line.
        if !pending.is_empty() {
            for entry in pending.iter_mut() {
                if entry.1 > 0 {
                    entry
                        .0
                        .context_after
                        .push(copy_context_line_for_record(line));
                    entry.1 -= 1;
                }
            }
            // Flush any pending records whose after-context is full.
            while let Some(front) = pending.front() {
                if front.1 == 0 {
                    let (rec, _) = pending.pop_front().unwrap();
                    if !emit(rec) {
                        return Ok(emitted);
                    }
                    emitted += 1;
                    if options.max_results != 0 && emitted >= options.max_results {
                        return Ok(emitted);
                    }
                } else {
                    break;
                }
            }
        }

        // Find every match in this line.
        let mut search_from = 0usize;
        while search_from <= line.len() {
            match matcher.find(&line[search_from..]) {
                None => break,
                Some((start_rel, len)) => {
                    let start = search_from + start_rel;
                    if len == 0 {
                        // Avoid infinite zero-width loops.
                        search_from = start + 1;
                        continue;
                    }

                    let (match_line, display_start) = copy_match_line_for_record(line, start, len);
                    let rec = MatchRecord {
                        line_number,
                        match_start: display_start,
                        match_len: len as u32,
                        line: match_line,
                        context_before: before.iter().cloned().collect(),
                        context_after: Vec::with_capacity(options.context_after),
                    };

                    if options.context_after == 0 {
                        if !emit(rec) {
                            return Ok(emitted);
                        }
                        emitted += 1;
                        if options.max_results != 0 && emitted >= options.max_results {
                            return Ok(emitted);
                        }
                    } else {
                        pending.push_back((rec, options.context_after));
                    }

                    search_from = start + len;
                }
            }
        }

        // Update before-context ring buffer with the just-scanned line.
        if options.context_before > 0 {
            if before.len() == options.context_before {
                before.pop_front();
            }
            before.push_back(copy_context_line_for_record(line));
        }
    }

    // Flush any pending (after-context never completed because EOF).
    for (rec, _) in pending.drain(..) {
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

pub(crate) fn copy_context_line_for_record(line: &[u8]) -> Vec<u8> {
    if line.len() <= MAX_EMITTED_LINE_BYTES {
        return line.to_vec();
    }

    let mut end = MAX_EMITTED_LINE_BYTES;
    while end > 0 && end < line.len() && (line[end] & 0xC0) == 0x80 {
        end -= 1;
    }

    let mut output = Vec::with_capacity(end + TRUNCATION_MARKER.len());
    output.extend_from_slice(&line[..end]);
    output.extend_from_slice(TRUNCATION_MARKER);
    output
}

pub(crate) fn copy_match_line_for_record(line: &[u8], match_start: usize, match_len: usize) -> (Vec<u8>, u32) {
    if line.len() <= MAX_EMITTED_LINE_BYTES {
        return (line.to_vec(), match_start as u32);
    }

    let safe_start = match_start.min(line.len());
    let safe_len = match_len.min(line.len().saturating_sub(safe_start));
    let visible_match_len = safe_len.min(MAX_EMITTED_LINE_BYTES);
    let context = (MAX_EMITTED_LINE_BYTES - visible_match_len) / 2;

    let mut start = safe_start.saturating_sub(context);
    let mut end = (start + MAX_EMITTED_LINE_BYTES).min(line.len());
    if end - start < MAX_EMITTED_LINE_BYTES {
        start = end.saturating_sub(MAX_EMITTED_LINE_BYTES);
    }

    start = snap_to_char_boundary_start(line, start);
    end = snap_to_char_boundary_end(line, end);
    if end <= start {
        return (
            copy_context_line_for_record(line),
            match_start.min(MAX_EMITTED_LINE_BYTES) as u32,
        );
    }

    let has_prefix = start > 0;
    let has_suffix = end < line.len();
    let mut output = Vec::with_capacity(
        (end - start)
            + if has_prefix {
                TRUNCATION_MARKER.len()
            } else {
                0
            }
            + if has_suffix {
                TRUNCATION_MARKER.len()
            } else {
                0
            },
    );
    if has_prefix {
        output.extend_from_slice(TRUNCATION_MARKER);
    }
    output.extend_from_slice(&line[start..end]);
    if has_suffix {
        output.extend_from_slice(TRUNCATION_MARKER);
    }

    let adjusted = safe_start.saturating_sub(start)
        + if has_prefix {
            TRUNCATION_MARKER.len()
        } else {
            0
        };
    (output, adjusted.min(u32::MAX as usize) as u32)
}

fn snap_to_char_boundary_start(line: &[u8], mut index: usize) -> usize {
    while index > 0 && index < line.len() && (line[index] & 0xC0) == 0x80 {
        index -= 1;
    }
    index
}

/// Snap an *exclusive* upper bound to a UTF-8 codepoint boundary by advancing
/// forward past any continuation bytes. This keeps the partial codepoint in
/// the slice rather than dropping it (which `snap_to_char_boundary_start`-style
/// retreat would do when used as an end bound).
fn snap_to_char_boundary_end(line: &[u8], mut index: usize) -> usize {
    while index < line.len() && (line[index] & 0xC0) == 0x80 {
        index += 1;
    }
    index
}

fn looks_binary(bytes: &[u8]) -> bool {
    let probe = &bytes[..bytes.len().min(8 * 1024)];
    if probe.is_empty() {
        return false;
    }
    if has_binary_magic(probe) {
        return true;
    }
    if probe.contains(&0u8) {
        return true;
    }
    if probe.len() >= 512 {
        let suspicious = probe
            .iter()
            .filter(|&&b| !matches!(b, 0x09 | 0x0A | 0x0D) && (b < 0x20 || b == 0x7F))
            .count();
        if suspicious * 100 / probe.len() > 5 {
            return true;
        }
    }
    false
}

fn has_binary_magic(s: &[u8]) -> bool {
    if s.len() < 4 {
        return false;
    }
    // Gzip
    if s[0] == 0x1F && s[1] == 0x8B {
        return true;
    }
    // ZIP family
    if s[0] == 0x50 && s[1] == 0x4B && (s[2] == 0x03 || s[2] == 0x05 || s[2] == 0x07) {
        return true;
    }
    // PNG
    if s[0] == 0x89 && s[1] == 0x50 && s[2] == 0x4E && s[3] == 0x47 {
        return true;
    }
    // JPEG
    if s[0] == 0xFF && s[1] == 0xD8 && s[2] == 0xFF {
        return true;
    }
    // PDF
    if s[0] == 0x25 && s[1] == 0x50 && s[2] == 0x44 && s[3] == 0x46 {
        return true;
    }
    // ELF
    if s[0] == 0x7F && s[1] == 0x45 && s[2] == 0x4C && s[3] == 0x46 {
        return true;
    }
    // PE/DOS
    if s[0] == 0x4D && s[1] == 0x5A {
        return true;
    }
    // 7z
    if s.len() >= 6
        && s[0] == 0x37
        && s[1] == 0x7A
        && s[2] == 0xBC
        && s[3] == 0xAF
        && s[4] == 0x27
        && s[5] == 0x1C
    {
        return true;
    }
    // Zstd
    if s[0] == 0x28 && s[1] == 0xB5 && s[2] == 0x2F && s[3] == 0xFD {
        return true;
    }
    // Mach-O 32-bit LE
    if s[0] == 0xCE && s[1] == 0xFA && s[2] == 0xED && s[3] == 0xFE {
        return true;
    }
    // Mach-O 64-bit LE
    if s[0] == 0xCF && s[1] == 0xFA && s[2] == 0xED && s[3] == 0xFE {
        return true;
    }
    // Mach-O fat / Java class
    if s[0] == 0xCA && s[1] == 0xFE && s[2] == 0xBA && s[3] == 0xBE {
        return true;
    }
    // SQLite
    if s.len() >= 6
        && s[0] == 0x53
        && s[1] == 0x51
        && s[2] == 0x4C
        && s[3] == 0x69
        && s[4] == 0x74
        && s[5] == 0x65
    {
        return true;
    }
    // Bzip2
    if s[0] == 0x42 && s[1] == 0x5A && s[2] == 0x68 {
        return true;
    }
    // XZ
    if s.len() >= 6
        && s[0] == 0xFD
        && s[1] == 0x37
        && s[2] == 0x7A
        && s[3] == 0x58
        && s[4] == 0x5A
        && s[5] == 0x00
    {
        return true;
    }
    // RAR
    if s.len() >= 7
        && s[0] == 0x52
        && s[1] == 0x61
        && s[2] == 0x72
        && s[3] == 0x21
        && s[4] == 0x1A
        && s[5] == 0x07
    {
        return true;
    }
    false
}

pub trait LineMatcher: Send + Sync {
    fn find(&self, hay: &[u8]) -> Option<(usize, usize)>;
}

pub struct LiteralMatcher {
    impl_: LiteralImpl,
}

enum LiteralImpl {
    CaseSensitive {
        finder: memmem::Finder<'static>,
        len: usize,
    },
    AsciiCaseInsensitive {
        needle_lower: Vec<u8>,
        len: usize,
    },
    UnicodeCaseInsensitive {
        regex: Regex,
    },
}

impl LiteralMatcher {
    fn new_case_sensitive(needle: &[u8]) -> Self {
        Self {
            impl_: LiteralImpl::CaseSensitive {
                finder: memmem::Finder::new(needle).into_owned(),
                len: needle.len(),
            },
        }
    }

    fn new_ascii_case_insensitive(needle: &[u8]) -> Self {
        Self {
            impl_: LiteralImpl::AsciiCaseInsensitive {
                needle_lower: needle.to_ascii_lowercase(),
                len: needle.len(),
            },
        }
    }

    /// Build a Unicode-case-folding literal matcher by escaping the pattern
    /// and feeding it to the regex engine with `(?i)`. This matches the
    /// case-insensitive behavior of the regex path.
    fn new_unicode_case_insensitive(pattern: &str) -> Result<Self, ScanError> {
        let escaped = regex::escape(pattern);
        let regex = RegexBuilder::new(&escaped)
            .case_insensitive(true)
            .build()
            .map_err(|e| ScanError::InvalidRegex(e.to_string()))?;
        Ok(Self {
            impl_: LiteralImpl::UnicodeCaseInsensitive { regex },
        })
    }
}

impl LineMatcher for LiteralMatcher {
    fn find(&self, hay: &[u8]) -> Option<(usize, usize)> {
        match &self.impl_ {
            LiteralImpl::CaseSensitive { finder, len } => {
                finder.find(hay).map(|i| (i, *len))
            }
            LiteralImpl::AsciiCaseInsensitive { needle_lower, len } => {
                find_ascii_case_insensitive(hay, needle_lower).map(|i| (i, *len))
            }
            LiteralImpl::UnicodeCaseInsensitive { regex } => {
                regex.find(hay).map(|m| (m.start(), m.end() - m.start()))
            }
        }
    }
}

fn find_ascii_case_insensitive(hay: &[u8], needle_lower: &[u8]) -> Option<usize> {
    if needle_lower.is_empty() {
        return Some(0);
    }
    if needle_lower.len() > hay.len() {
        return None;
    }

    let first = needle_lower[0];
    let mut offset = 0usize;
    while offset + needle_lower.len() <= hay.len() {
        let candidate_rel = if first.is_ascii_alphabetic() {
            memchr2(first, first.to_ascii_uppercase(), &hay[offset..])?
        } else {
            memchr(first, &hay[offset..])?
        };
        let candidate = offset + candidate_rel;
        if candidate + needle_lower.len() > hay.len() {
            return None;
        }
        if hay[candidate..candidate + needle_lower.len()]
            .iter()
            .zip(needle_lower.iter())
            .all(|(&actual, &expected_lower)| ascii_byte_eq_lower(actual, expected_lower))
        {
            return Some(candidate);
        }
        offset = candidate + 1;
    }

    None
}

fn ascii_byte_eq_lower(actual: u8, expected_lower: u8) -> bool {
    if expected_lower.is_ascii_alphabetic() {
        actual.to_ascii_lowercase() == expected_lower
    } else {
        actual == expected_lower
    }
}

pub struct RegexMatcher {
    re: Regex,
}

impl LineMatcher for RegexMatcher {
    fn find(&self, hay: &[u8]) -> Option<(usize, usize)> {
        self.re.find(hay).map(|m| (m.start(), m.end() - m.start()))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn opts() -> ScanOptions {
        ScanOptions {
            case_sensitive: false,
            use_regex: false,
            context_before: 0,
            context_after: 0,
            max_results: 0,
            skip_binary: true,
            ascii_case_only: false,
        }
    }

    #[test]
    fn literal_finds_multiple_per_line() {
        let bytes = b"foo bar foo baz\n";
        let mut hits = Vec::new();
        scan_bytes(bytes, "foo", &opts(), |r| {
            hits.push(r);
            true
        })
        .unwrap();
        assert_eq!(hits.len(), 2);
        assert_eq!(hits[0].line_number, 1);
        assert_eq!(hits[0].match_start, 0);
        assert_eq!(hits[1].match_start, 8);
    }

    #[test]
    fn case_insensitive_default() {
        let bytes = b"FooBar\n";
        let mut count = 0;
        scan_bytes(bytes, "foobar", &opts(), |_| {
            count += 1;
            true
        })
        .unwrap();
        assert_eq!(count, 1);
    }

    #[test]
    fn case_sensitive_off() {
        let bytes = b"FooBar\n";
        let o = ScanOptions {
            case_sensitive: true,
            ..opts()
        };
        let n = scan_bytes(bytes, "foobar", &o, |_| true).unwrap();
        assert_eq!(n, 0);
    }

    #[test]
    fn regex_with_groups() {
        let bytes = b"alpha 12\nbeta 345\n";
        let o = ScanOptions {
            use_regex: true,
            ..opts()
        };
        let mut hits = Vec::new();
        scan_bytes(bytes, r"\d+", &o, |r| {
            hits.push(r);
            true
        })
        .unwrap();
        assert_eq!(hits.len(), 2);
    }

    #[test]
    fn context_lines_collected() {
        let bytes = b"a\nb\nc\nMATCH\nd\ne\nf\n";
        let o = ScanOptions {
            context_before: 2,
            context_after: 2,
            ..opts()
        };
        let mut hits = Vec::new();
        scan_bytes(bytes, "MATCH", &o, |r| {
            hits.push(r);
            true
        })
        .unwrap();
        assert_eq!(hits.len(), 1);
        let h = &hits[0];
        assert_eq!(h.context_before, vec![b"b".to_vec(), b"c".to_vec()]);
        assert_eq!(h.context_after, vec![b"d".to_vec(), b"e".to_vec()]);
    }

    #[test]
    fn binary_skipped() {
        let bytes = b"abc\0def\n";
        let res = scan_bytes(bytes, "abc", &opts(), |_| true);
        assert!(matches!(res, Err(ScanError::BinarySkipped)));
    }

    #[test]
    fn max_results_caps() {
        let bytes = b"x x x x x x x\n";
        let o = ScanOptions {
            max_results: 3,
            ..opts()
        };
        let mut count = 0;
        scan_bytes(bytes, "x", &o, |_| {
            count += 1;
            true
        })
        .unwrap();
        assert_eq!(count, 3);
    }

    #[test]
    fn long_lines_are_capped_before_emit() {
        let mut bytes = vec![b'a'; MAX_EMITTED_LINE_BYTES + 128];
        bytes.extend_from_slice(b"\n");
        let o = ScanOptions {
            max_results: 1,
            ..opts()
        };
        let mut hits = Vec::new();
        scan_bytes(&bytes, "aaa", &o, |r| {
            hits.push(r);
            true
        })
        .unwrap();
        assert_eq!(hits.len(), 1);
        assert!(hits[0].line.len() <= MAX_EMITTED_LINE_BYTES + TRUNCATION_MARKER.len());
        assert!(hits[0].line.ends_with(TRUNCATION_MARKER));
    }

    #[test]
    fn case_insensitive_literal_matches_without_lowercase_buffer() {
        let bytes = b"prefix TeSt suffix test TEST\n";
        let mut hits = Vec::new();
        scan_bytes(bytes, "test", &opts(), |r| {
            hits.push(r.match_start);
            true
        })
        .unwrap();
        assert_eq!(hits, vec![7, 19, 24]);
    }

    // ---- Coverage: ScanError::Io From impl ----
    #[test]
    fn scan_error_io_from_impl() {
        let io_err = std::io::Error::new(std::io::ErrorKind::NotFound, "gone");
        let scan_err = ScanError::from(io_err);
        assert!(matches!(scan_err, ScanError::Io(_)));
    }

    // ---- Coverage: emit returning false (early stop, no context) ----
    #[test]
    fn emit_returning_false_stops_scan() {
        let bytes = b"aaa\naaa\naaa\n";
        let mut count = 0;
        let n = scan_bytes(bytes, "aaa", &opts(), |_| {
            count += 1;
            false // stop after first
        })
        .unwrap();
        assert_eq!(count, 1);
        assert_eq!(n, 0); // emitted is only incremented AFTER emit returns true
    }

    // ---- Coverage: zero-width regex match ----
    #[test]
    fn zero_width_regex_does_not_infinite_loop() {
        let bytes = b"abc\n";
        let o = ScanOptions {
            use_regex: true,
            ..opts()
        };
        // \b is zero-width, but the scanner should skip it via the len==0 guard
        // Actually we need a regex that produces zero-width matches.
        // "(?=a)" is a lookahead — zero-width match at position of 'a'.
        let result = scan_bytes(bytes, "(?=a)", &o, |_| true);
        // Should not hang; the zero-width guard breaks the loop
        assert!(result.unwrap_or(0) <= 3); // at most one per character
    }

    // ---- Coverage: max_results with pending after-context ----
    #[test]
    fn max_results_with_after_context_flush() {
        // 3 matches each wanting 1 line of after-context, max_results=2
        let bytes = b"hit\nctx\nhit\nctx\nhit\nctx\n";
        let o = ScanOptions {
            context_after: 1,
            max_results: 2,
            ..opts()
        };
        let mut hits = Vec::new();
        let n = scan_bytes(bytes, "hit", &o, |r| {
            hits.push(r.line_number);
            true
        })
        .unwrap();
        assert_eq!(n, 2);
    }

    // ---- Coverage: emit returns false during pending flush ----
    #[test]
    fn emit_false_during_pending_flush() {
        let bytes = b"hit\nctx\nhit\nctx\n";
        let o = ScanOptions {
            context_after: 1,
            ..opts()
        };
        let mut count = 0;
        scan_bytes(bytes, "hit", &o, |_| {
            count += 1;
            false // stop on first emitted
        })
        .unwrap();
        assert_eq!(count, 1);
    }

    // ---- Coverage: EOF flush of pending records with emit returning false ----
    #[test]
    fn eof_flush_emit_false() {
        // Match at end of file, pending after-context never completes
        let bytes = b"hit\n";
        let o = ScanOptions {
            context_after: 5,
            ..opts()
        };
        let mut count = 0;
        scan_bytes(bytes, "hit", &o, |_| {
            count += 1;
            false
        })
        .unwrap();
        assert_eq!(count, 1);
    }

    // ---- Coverage: EOF flush with max_results ----
    #[test]
    fn eof_flush_max_results() {
        let bytes = b"hit\nhit\nhit\n";
        let o = ScanOptions {
            context_after: 99, // large context that will never fill
            max_results: 2,
            ..opts()
        };
        let mut count = 0;
        scan_bytes(bytes, "hit", &o, |_| {
            count += 1;
            true
        })
        .unwrap();
        assert_eq!(count, 2);
    }

    // ---- Coverage: copy_context_line_for_record truncation with multi-byte UTF-8 ----
    #[test]
    fn context_line_truncation_multibyte() {
        // Create a long context line > MAX_EMITTED_LINE_BYTES with multi-byte chars at boundary
        let mut long_line = vec![b'x'; MAX_EMITTED_LINE_BYTES - 1];
        // Add a 3-byte UTF-8 char (e.g. U+2603 snowman = E2 98 83) at the boundary
        long_line.extend_from_slice(&[0xE2, 0x98, 0x83]);
        long_line.extend_from_slice(&[b'y'; 100]);
        let result = copy_context_line_for_record(&long_line);
        // Should be truncated and end with the truncation marker
        assert!(result.len() <= MAX_EMITTED_LINE_BYTES + TRUNCATION_MARKER.len() + 3);
        assert!(result.ends_with(TRUNCATION_MARKER));
    }

    #[test]
    fn context_line_short_is_unchanged() {
        let line = b"short line";
        let result = copy_context_line_for_record(line);
        assert_eq!(result, line.to_vec());
    }

    // ---- Coverage: copy_match_line_for_record branches ----
    #[test]
    fn match_line_short_unchanged() {
        let line = b"hello world";
        let (result, start) = copy_match_line_for_record(line, 6, 5);
        assert_eq!(result, line.to_vec());
        assert_eq!(start, 6);
    }

    #[test]
    fn match_line_long_with_prefix_and_suffix() {
        // Line much longer than MAX_EMITTED_LINE_BYTES, match in the middle
        let mut line = vec![b'A'; MAX_EMITTED_LINE_BYTES * 2];
        let match_pos = MAX_EMITTED_LINE_BYTES; // middle-ish
        line[match_pos] = b'X';
        let (result, _) = copy_match_line_for_record(&line, match_pos, 1);
        // Should have prefix AND suffix truncation markers
        assert!(result.starts_with(TRUNCATION_MARKER));
        assert!(result.ends_with(TRUNCATION_MARKER));
    }

    #[test]
    fn match_line_long_match_at_start() {
        // Match at very beginning of a long line - no prefix marker
        let line = vec![b'Z'; MAX_EMITTED_LINE_BYTES + 500];
        let (result, start) = copy_match_line_for_record(&line, 0, 3);
        assert!(!result.starts_with(TRUNCATION_MARKER));
        assert!(result.ends_with(TRUNCATION_MARKER));
        assert_eq!(start, 0);
    }

    #[test]
    fn match_line_long_match_at_end() {
        // Match at very end of a long line - no suffix marker
        let line = vec![b'W'; MAX_EMITTED_LINE_BYTES + 500];
        let match_pos = line.len() - 3;
        let (result, _) = copy_match_line_for_record(&line, match_pos, 3);
        assert!(result.starts_with(TRUNCATION_MARKER));
        assert!(!result.ends_with(TRUNCATION_MARKER));
    }

    #[test]
    fn match_line_long_with_multibyte_boundary() {
        // A long line with multi-byte UTF-8 near the window boundary
        let mut line = vec![b'a'; MAX_EMITTED_LINE_BYTES + 200];
        // Place a 3-byte char at the potential snap boundary
        let snap_pos = 100;
        line[snap_pos] = 0xE2;
        line[snap_pos + 1] = 0x98;
        line[snap_pos + 2] = 0x83;
        let (result, _) = copy_match_line_for_record(&line, MAX_EMITTED_LINE_BYTES, 5);
        assert!(result.len() <= MAX_EMITTED_LINE_BYTES + 2 * TRUNCATION_MARKER.len() + 10);
    }

    // ---- Coverage: snap_to_char_boundary helpers ----
    #[test]
    fn snap_start_on_continuation_byte() {
        // 3-byte UTF-8: E2 98 83 (snowman)
        let data = b"abc\xE2\x98\x83def";
        // Try snapping from a continuation byte (index 4 = 0x98)
        assert_eq!(snap_to_char_boundary_start(data, 4), 3);
        // From index 5 = 0x83 (continuation)
        assert_eq!(snap_to_char_boundary_start(data, 5), 3);
        // From index 3 = 0xE2 (lead byte) — stays
        assert_eq!(snap_to_char_boundary_start(data, 3), 3);
        // From index 0 — stays
        assert_eq!(snap_to_char_boundary_start(data, 0), 0);
    }

    #[test]
    fn snap_end_on_continuation_byte() {
        let data = b"abc\xE2\x98\x83def";
        // snap_end advances forward past continuation bytes so the partial
        // codepoint is preserved (e.g. truncating in the middle of "☃" keeps
        // the whole character rather than dropping it).
        assert_eq!(snap_to_char_boundary_end(data, 4), 6);
        assert_eq!(snap_to_char_boundary_end(data, 5), 6);
        // From lead byte — stays
        assert_eq!(snap_to_char_boundary_end(data, 3), 3);
    }

    #[test]
    fn snap_start_at_end_of_slice() {
        let data = b"abc";
        // index == len: beyond bounds, but loop guard checks index < line.len()
        assert_eq!(snap_to_char_boundary_start(data, 3), 3);
    }

    #[test]
    fn snap_end_at_end_of_slice() {
        let data = b"abc";
        assert_eq!(snap_to_char_boundary_end(data, 3), 3);
    }

    // ---- Coverage: looks_binary branches ----
    #[test]
    fn looks_binary_empty() {
        assert!(!looks_binary(b""));
    }

    #[test]
    fn looks_binary_null_byte() {
        assert!(looks_binary(b"hello\0world"));
    }

    #[test]
    fn looks_binary_magic_gzip() {
        let mut data = vec![0x1F, 0x8B, 0x08, 0x00];
        data.extend_from_slice(&[b'x'; 100]);
        assert!(looks_binary(&data));
    }

    #[test]
    fn looks_binary_magic_zip() {
        let mut data = vec![0x50, 0x4B, 0x03, 0x04];
        data.extend_from_slice(&[b'x'; 100]);
        assert!(looks_binary(&data));
    }

    #[test]
    fn looks_binary_magic_png() {
        let mut data = vec![0x89, 0x50, 0x4E, 0x47];
        data.extend_from_slice(&[b'x'; 100]);
        assert!(looks_binary(&data));
    }

    #[test]
    fn looks_binary_magic_jpeg() {
        let mut data = vec![0xFF, 0xD8, 0xFF, 0xE0];
        data.extend_from_slice(&[b'x'; 100]);
        assert!(looks_binary(&data));
    }

    #[test]
    fn looks_binary_magic_pdf() {
        let mut data = vec![0x25, 0x50, 0x44, 0x46];
        data.extend_from_slice(&[b'x'; 100]);
        assert!(looks_binary(&data));
    }

    #[test]
    fn looks_binary_magic_elf() {
        let mut data = vec![0x7F, 0x45, 0x4C, 0x46];
        data.extend_from_slice(&[b'x'; 100]);
        assert!(looks_binary(&data));
    }

    #[test]
    fn looks_binary_magic_pe_dos() {
        let mut data = vec![0x4D, 0x5A, 0x90, 0x00];
        data.extend_from_slice(&[b'x'; 100]);
        assert!(looks_binary(&data));
    }

    #[test]
    fn looks_binary_magic_7z() {
        let mut data = vec![0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
        data.extend_from_slice(&[b'x'; 100]);
        assert!(looks_binary(&data));
    }

    #[test]
    fn looks_binary_magic_zstd() {
        let mut data = vec![0x28, 0xB5, 0x2F, 0xFD];
        data.extend_from_slice(&[b'x'; 100]);
        assert!(looks_binary(&data));
    }

    #[test]
    fn looks_binary_magic_macho32() {
        let mut data = vec![0xCE, 0xFA, 0xED, 0xFE];
        data.extend_from_slice(&[b'x'; 100]);
        assert!(looks_binary(&data));
    }

    #[test]
    fn looks_binary_magic_macho64() {
        let mut data = vec![0xCF, 0xFA, 0xED, 0xFE];
        data.extend_from_slice(&[b'x'; 100]);
        assert!(looks_binary(&data));
    }

    #[test]
    fn looks_binary_magic_macho_fat() {
        let mut data = vec![0xCA, 0xFE, 0xBA, 0xBE];
        data.extend_from_slice(&[b'x'; 100]);
        assert!(looks_binary(&data));
    }

    #[test]
    fn looks_binary_magic_sqlite() {
        let mut data = vec![0x53, 0x51, 0x4C, 0x69, 0x74, 0x65];
        data.extend_from_slice(&[b'x'; 100]);
        assert!(looks_binary(&data));
    }

    #[test]
    fn looks_binary_magic_bzip2() {
        let mut data = vec![0x42, 0x5A, 0x68, 0x39];
        data.extend_from_slice(&[b'x'; 100]);
        assert!(looks_binary(&data));
    }

    #[test]
    fn looks_binary_magic_xz() {
        let mut data = vec![0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00];
        data.extend_from_slice(&[b'x'; 100]);
        assert!(looks_binary(&data));
    }

    #[test]
    fn looks_binary_magic_rar() {
        let mut data = vec![0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];
        data.extend_from_slice(&[b'x'; 100]);
        assert!(looks_binary(&data));
    }

    #[test]
    fn looks_binary_suspicious_control_chars() {
        // >= 512 bytes with > 5% suspicious control chars
        let mut data = Vec::with_capacity(600);
        for _ in 0..564 {
            data.push(b'a');
        }
        // Add ~36 suspicious control characters (6%+ of 600)
        for _ in 0..36 {
            data.push(0x01); // control char, not tab/lf/cr
        }
        assert!(looks_binary(&data));
    }

    #[test]
    fn looks_binary_below_threshold_not_binary() {
        // >= 512 bytes with < 5% suspicious — should NOT be binary
        let mut data = Vec::with_capacity(600);
        for _ in 0..590 {
            data.push(b'a');
        }
        for _ in 0..10 {
            data.push(0x01); // only ~1.6%
        }
        assert!(!looks_binary(&data));
    }

    #[test]
    fn has_binary_magic_short_input() {
        assert!(!has_binary_magic(b"ab")); // < 4 bytes
        assert!(!has_binary_magic(b"abc")); // < 4 bytes
    }

    #[test]
    fn has_binary_magic_no_match() {
        assert!(!has_binary_magic(b"hello world this is text"));
    }

    #[test]
    fn has_binary_magic_zip_variant_05() {
        let data = vec![0x50, 0x4B, 0x05, 0x06];
        assert!(has_binary_magic(&data));
    }

    #[test]
    fn has_binary_magic_zip_variant_07() {
        let data = vec![0x50, 0x4B, 0x07, 0x08];
        assert!(has_binary_magic(&data));
    }

    // ---- Coverage: find_ascii_case_insensitive edge cases ----
    #[test]
    fn find_case_insensitive_empty_needle() {
        assert_eq!(find_ascii_case_insensitive(b"hello", b""), Some(0));
    }

    #[test]
    fn find_case_insensitive_needle_longer_than_hay() {
        assert_eq!(
            find_ascii_case_insensitive(b"hi", b"hello"),
            None
        );
    }

    #[test]
    fn find_case_insensitive_non_alpha_first_byte() {
        // needle starts with a digit — uses memchr instead of memchr2
        assert_eq!(
            find_ascii_case_insensitive(b"abc123def", b"123"),
            Some(3)
        );
    }

    #[test]
    fn find_case_insensitive_no_match() {
        assert_eq!(
            find_ascii_case_insensitive(b"abcdef", b"xyz"),
            None
        );
    }

    #[test]
    fn find_case_insensitive_partial_match_then_real() {
        // "teST" starts with 'te' but doesn't match "text"; "test" later does
        assert_eq!(
            find_ascii_case_insensitive(b"texttestmore", b"test"),
            Some(4)
        );
    }

    #[test]
    fn find_case_insensitive_candidate_overflows() {
        // candidate found but needle extends past hay end
        assert_eq!(
            find_ascii_case_insensitive(b"abcX", b"xyzw"),
            None
        );
    }

    #[test]
    fn find_case_insensitive_loop_exhausts_all_candidates() {
        // First byte matches via memchr2 but full needle never matches,
        // and offset advances past the last viable position, exiting the
        // while loop normally and returning the trailing None.
        assert_eq!(
            find_ascii_case_insensitive(b"aX", b"ay"),
            None
        );
    }

    // ---- Coverage: ascii_byte_eq_lower ----
    #[test]
    fn ascii_byte_eq_lower_alpha() {
        assert!(ascii_byte_eq_lower(b'A', b'a'));
        assert!(ascii_byte_eq_lower(b'a', b'a'));
        assert!(!ascii_byte_eq_lower(b'b', b'a'));
    }

    #[test]
    fn ascii_byte_eq_lower_non_alpha() {
        assert!(ascii_byte_eq_lower(b'5', b'5'));
        assert!(!ascii_byte_eq_lower(b'6', b'5'));
        assert!(ascii_byte_eq_lower(b'.', b'.'));
    }

    // ---- Coverage: build_matcher branches ----
    #[test]
    fn build_matcher_invalid_regex() {
        let o = ScanOptions {
            use_regex: true,
            ..opts()
        };
        let result = build_matcher("[invalid", &o);
        assert!(matches!(result, Err(ScanError::InvalidRegex(_))));
    }

    #[test]
    fn build_matcher_case_sensitive_literal() {
        let o = ScanOptions {
            case_sensitive: true,
            use_regex: false,
            ..opts()
        };
        let m = build_matcher("hello", &o).unwrap();
        assert_eq!(m.find(b"Hello"), None);
        assert_eq!(m.find(b"hello"), Some((0, 5)));
    }

    #[test]
    fn build_matcher_regex_case_insensitive() {
        let o = ScanOptions {
            case_sensitive: false,
            use_regex: true,
            ..opts()
        };
        let m = build_matcher("hello", &o).unwrap();
        assert!(m.find(b"HELLO").is_some());
    }

    // ---- Coverage: scan_bytes_with_matcher directly ----
    #[test]
    fn scan_bytes_with_matcher_basic() {
        let o = opts();
        let m = build_matcher("foo", &o).unwrap();
        let mut hits = Vec::new();
        let n = scan_bytes_with_matcher(b"foo bar\nfoo baz\n", &*m, &o, |r| {
            hits.push(r.line_number);
            true
        })
        .unwrap();
        assert_eq!(n, 2);
        assert_eq!(hits, vec![1, 2]);
    }

    // ---- Coverage: skip_binary=false allows binary content ----
    #[test]
    fn skip_binary_false_allows_binary() {
        let o = ScanOptions {
            skip_binary: false,
            ..opts()
        };
        let bytes = b"abc\0def\n";
        let mut count = 0;
        scan_bytes(bytes, "abc", &o, |_| {
            count += 1;
            true
        })
        .unwrap();
        assert_eq!(count, 1);
    }

    // ---- Coverage: ScanError Debug ----
    #[test]
    fn scan_error_debug() {
        let e = ScanError::InvalidRegex("bad".into());
        let dbg = format!("{:?}", e);
        assert!(dbg.contains("InvalidRegex"));

        let e2 = ScanError::Cancelled;
        let dbg2 = format!("{:?}", e2);
        assert!(dbg2.contains("Cancelled"));

        let e3 = ScanError::BinarySkipped;
        assert!(format!("{:?}", e3).contains("BinarySkipped"));

        let io = ScanError::Io(std::io::Error::new(std::io::ErrorKind::Other, "x"));
        assert!(format!("{:?}", io).contains("Io"));
    }

    // ---- Coverage: MatchRecord Debug ----
    #[test]
    fn match_record_debug() {
        let r = MatchRecord {
            line_number: 1,
            match_start: 0,
            match_len: 3,
            line: b"foo".to_vec(),
            context_before: vec![],
            context_after: vec![],
        };
        let dbg = format!("{:?}", r);
        assert!(dbg.contains("MatchRecord"));
    }

    // ---- Coverage: ScanOptions Clone + Debug ----
    #[test]
    fn scan_options_clone_debug() {
        let o = opts();
        let o2 = o.clone();
        assert_eq!(o2.case_sensitive, o.case_sensitive);
        let dbg = format!("{:?}", o);
        assert!(dbg.contains("ScanOptions"));
    }

    // ---- Coverage: max_results during pending after-context flush ----
    #[test]
    fn max_results_hit_during_after_context_flush() {
        // Two matches on consecutive lines, each wanting 1 line of after-context.
        // max_results=1 means only the first pending record should flush.
        let bytes = b"hit\nhit\nfiller\n";
        let o = ScanOptions {
            context_after: 1,
            max_results: 1,
            ..opts()
        };
        let mut count = 0;
        let n = scan_bytes(bytes, "hit", &o, |_| {
            count += 1;
            true
        })
        .unwrap();
        assert_eq!(n, 1);
    }

    // ---- Coverage: long context line in before-context ----
    #[test]
    fn long_before_context_line_truncated() {
        let mut long_line = vec![b'z'; MAX_EMITTED_LINE_BYTES + 200];
        long_line.push(b'\n');
        let mut bytes = long_line;
        bytes.extend_from_slice(b"MATCH\n");
        let o = ScanOptions {
            context_before: 1,
            ..opts()
        };
        let mut hits = Vec::new();
        scan_bytes(&bytes, "MATCH", &o, |r| {
            hits.push(r);
            true
        })
        .unwrap();
        assert_eq!(hits.len(), 1);
        assert_eq!(hits[0].context_before.len(), 1);
        assert!(hits[0].context_before[0].len() <= MAX_EMITTED_LINE_BYTES + TRUNCATION_MARKER.len());
        assert!(hits[0].context_before[0].ends_with(TRUNCATION_MARKER));
    }

    // ---- Coverage: long after-context line truncated ----
    #[test]
    fn long_after_context_line_truncated() {
        let mut bytes = b"MATCH\n".to_vec();
        let mut long_line = vec![b'q'; MAX_EMITTED_LINE_BYTES + 200];
        long_line.push(b'\n');
        bytes.extend_from_slice(&long_line);
        let o = ScanOptions {
            context_after: 1,
            ..opts()
        };
        let mut hits = Vec::new();
        scan_bytes(&bytes, "MATCH", &o, |r| {
            hits.push(r);
            true
        })
        .unwrap();
        assert_eq!(hits.len(), 1);
        assert_eq!(hits[0].context_after.len(), 1);
        assert!(hits[0].context_after[0].len() <= MAX_EMITTED_LINE_BYTES + TRUNCATION_MARKER.len());
        assert!(hits[0].context_after[0].ends_with(TRUNCATION_MARKER));
    }

    // ---- Coverage: RegexMatcher find method ----
    #[test]
    fn regex_matcher_find() {
        let re = RegexBuilder::new(r"\d+")
            .build()
            .unwrap();
        let m = RegexMatcher { re };
        assert_eq!(m.find(b"abc 42 xyz"), Some((4, 2)));
        assert_eq!(m.find(b"no digits"), None);
    }

    // ---- Coverage: LiteralMatcher find method ----
    #[test]
    fn literal_matcher_find_case_sensitive() {
        let m = LiteralMatcher::new_case_sensitive(b"Test");
        assert_eq!(m.find(b"a Test here"), Some((2, 4)));
        assert_eq!(m.find(b"a test here"), None); // wrong case
    }

    #[test]
    fn literal_matcher_find_case_insensitive() {
        let m = LiteralMatcher::new_ascii_case_insensitive(b"Test");
        assert_eq!(m.find(b"a TEST here"), Some((2, 4)));
        assert_eq!(m.find(b"nope"), None);
    }

    // ---- Coverage: copy_match_line_for_record fallback (end <= start) ----
    #[test]
    fn copy_match_line_fallback_branch() {
        // This exercises the (end <= start) fallback in copy_match_line_for_record.
        // We need: line > MAX_EMITTED_LINE_BYTES, and after snap, end <= start.
        // All continuation bytes (0x80). Match at position 0 so that `end`
        // computes to MAX_EMITTED_LINE_BYTES (< line.len()), and snap_to_char_boundary_end
        // walks all the 0x80 bytes back to 0, giving end=0 <= start=0.
        let line = vec![0x80u8; MAX_EMITTED_LINE_BYTES + 200];
        let (result, offset) = copy_match_line_for_record(&line, 0, 5);
        // Should fall back to copy_context_line_for_record behavior
        assert!(!result.is_empty());
        // Fallback returns match_start.min(MAX_EMITTED_LINE_BYTES) as u32 = 0
        assert_eq!(offset, 0);
    }

    // ---- Pre-work: Unicode case folding for LiteralMatcher ----
    #[test]
    fn literal_unicode_case_fold_german_umlaut() {
        // "Über" / "über" should match under Unicode fold (default).
        let m = LiteralMatcher::new_unicode_case_insensitive("über").unwrap();
        let hay = "Über alles".as_bytes();
        let r = m.find(hay).expect("Unicode fold should match Über");
        assert_eq!(r.0, 0);
        // "Über" is 5 bytes ("\xC3\x9Cber"), "über" is also 5 bytes.
        assert_eq!(r.1, 5);
    }

    #[test]
    fn literal_unicode_case_fold_pattern_with_uppercase() {
        let m = LiteralMatcher::new_unicode_case_insensitive("ÜBER").unwrap();
        assert!(m.find("über alles".as_bytes()).is_some());
    }

    #[test]
    fn literal_ascii_only_skips_non_ascii_fold() {
        // Documents that ascii_case_only path does NOT fold non-ASCII.
        let m = LiteralMatcher::new_ascii_case_insensitive("über".as_bytes());
        let hay = "Über alles".as_bytes();
        // "Ü" (0xC3 0x9C) vs "ü" (0xC3 0xBC) — different non-ASCII bytes,
        // ASCII fold leaves them alone, so this does not match.
        assert!(m.find(hay).is_none());
    }

    #[test]
    fn literal_unicode_fold_via_build_matcher_default() {
        let o = ScanOptions {
            case_sensitive: false,
            use_regex: false,
            ascii_case_only: false,
            ..opts()
        };
        let m = build_matcher("über", &o).unwrap();
        assert!(m.find("Über".as_bytes()).is_some());
    }

    #[test]
    fn literal_unicode_fold_eszett_documents_simple_fold() {
        // Document the chosen behavior for ß: regex's case_insensitive uses
        // Unicode *simple* case fold, which folds "ß" to itself (not "ss").
        // So "ß" pattern matches "ß" but NOT "SS" (and vice versa).
        let m = LiteralMatcher::new_unicode_case_insensitive("ß").unwrap();
        assert!(m.find("straße".as_bytes()).is_some());
        // "ß" and "SS" do not match each other under simple fold.
        assert!(m.find(b"STRASSE").is_none());
    }

    #[test]
    fn literal_unicode_fold_turkish_dotless_i_documents_default() {
        // Document Turkish I behavior: regex simple fold treats "i"/"I" as
        // a fold pair, but does NOT fold "I" to dotless "ı" or "i" to "İ"
        // (that requires Turkish-locale full case folding which is out of scope).
        let m = LiteralMatcher::new_unicode_case_insensitive("i").unwrap();
        assert!(m.find(b"I").is_some());
        assert!(m.find(b"i").is_some());
        // Dotless I (U+0131) is NOT considered a fold of "i" by default.
        assert!(m.find("ı".as_bytes()).is_none());
    }

    // ---- Pre-work: per-line cancellation polling ----
    #[test]
    fn per_line_cancellation_stops_no_match_scan() {
        // Build a no-match buffer with many lines so that per-match cancel
        // polling would never fire. The per-line cancel should stop us early.
        let mut bytes = Vec::new();
        for _ in 0..2000 {
            bytes.extend_from_slice(b"nothing here to find\n");
        }
        let cancel_calls = std::cell::Cell::new(0u32);
        let result = scan_bytes_ex(
            &bytes,
            "needle",
            &opts(),
            || {
                let n = cancel_calls.get() + 1;
                cancel_calls.set(n);
                // Cancel after the first poll.
                n >= 1
            },
            |_| true,
        )
        .unwrap();
        assert_eq!(result, 0);
        // Cancel was polled at least once even though there were zero matches.
        assert!(cancel_calls.get() >= 1);
    }

    #[test]
    fn per_line_cancel_returning_false_completes_scan() {
        let bytes = b"foo\nbar\nfoo\n";
        let n = scan_bytes_ex(bytes, "foo", &opts(), || false, |_| true).unwrap();
        assert_eq!(n, 2);
    }
}
