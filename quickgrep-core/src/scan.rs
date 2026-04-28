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
        Ok(Box::new(LiteralMatcher::new(pattern.as_bytes(), true)))
    } else {
        Ok(Box::new(LiteralMatcher::new(pattern.as_bytes(), false)))
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

/// Like `scan_bytes` but accepts a pre-compiled matcher for reuse across files.
pub fn scan_bytes_with_matcher(
    bytes: &[u8],
    matcher: &dyn LineMatcher,
    options: &ScanOptions,
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

fn copy_context_line_for_record(line: &[u8]) -> Vec<u8> {
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

fn copy_match_line_for_record(line: &[u8], match_start: usize, match_len: usize) -> (Vec<u8>, u32) {
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

fn snap_to_char_boundary_end(line: &[u8], mut index: usize) -> usize {
    while index > 0 && index < line.len() && (line[index] & 0xC0) == 0x80 {
        index -= 1;
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
    finder_owned: Option<memmem::Finder<'static>>,
    needle_lower: Vec<u8>,
    case_sensitive: bool,
    needle_len: usize,
}

impl LiteralMatcher {
    fn new(needle: &[u8], case_sensitive: bool) -> Self {
        if case_sensitive {
            Self {
                finder_owned: Some(memmem::Finder::new(needle).into_owned()),
                needle_lower: Vec::new(),
                case_sensitive: true,
                needle_len: needle.len(),
            }
        } else {
            Self {
                finder_owned: None,
                needle_lower: needle.to_ascii_lowercase(),
                case_sensitive: false,
                needle_len: needle.len(),
            }
        }
    }
}

impl LineMatcher for LiteralMatcher {
    fn find(&self, hay: &[u8]) -> Option<(usize, usize)> {
        if self.case_sensitive {
            self.finder_owned
                .as_ref()
                .unwrap()
                .find(hay)
                .map(|i| (i, self.needle_len))
        } else {
            find_ascii_case_insensitive(hay, &self.needle_lower).map(|i| (i, self.needle_len))
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
        let mut count = 0;
        scan_bytes(bytes, "foobar", &o, |_| {
            count += 1;
            true
        })
        .unwrap();
        assert_eq!(count, 0);
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
}
