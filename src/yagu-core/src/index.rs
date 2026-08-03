//! Content-index trigram extraction (plan §3.1/§3.2) — the Rust half of the persistent content index.
//!
//! This mirrors the managed reference (`ContentRepresentation.Classify` + `BinaryDetector` + `Trigram`)
//! byte-for-byte so the out-of-process worker and the managed reference produce **identical** trigram
//! sets under the golden-parity tests. v1 admits only well-formed BOM-less UTF-8; CRLF and bare CR are
//! normalized to LF; distinct 3-byte trigrams are extracted from the normalized bytes and returned
//! sorted ascending. Any unsupported representation (BOM, invalid UTF-8), binary content, or empty/short
//! input is handled conservatively — a non-`Indexed` verdict means "live-scan this file", never a wrong
//! result.
//!
//! The FFI here is **additive and independent** of the search FFI: it carries its own
//! `qg_index_abi_version` and never touches the `QgOptions`/`QgSession`/`QgMatchView` layout that
//! `qg_abi_version` gates, so adding it can never disturb the native search path.

use std::collections::{BTreeSet, HashMap, HashSet};
use std::os::raw::{c_int, c_uint};

/// Classification verdict. The discriminants match the managed `ContentRepresentationVerdict` enum
/// order (`Indexed = 0`, `Binary = 1`, `NotBomlessUtf8 = 2`) so the FFI can return the raw value.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Verdict {
    Indexed = 0,
    Binary = 1,
    NotBomlessUtf8 = 2,
}

/// Bytes sniffed by the binary detector (mirrors managed `BinaryDetector.SampleBytes`).
const BINARY_SNIFF_BYTES: usize = 8192;

/// Classifies `content` and, when it is admissible BOM-less UTF-8, returns its sorted distinct trigram
/// set. Each trigram is packed as `(b0 << 16) | (b1 << 8) | b2` in the low 24 bits of a `u32`, matching
/// the managed `Trigram`. A non-`Indexed` verdict returns an empty vector. A valid file of canonical
/// length 0–2 yields the `Indexed` verdict with an empty set (plan §5.1 #7).
pub fn extract_trigrams(content: &[u8]) -> (Verdict, Vec<u32>) {
    let payload = if starts_with_utf8_bom(content) {
        &content[3..]
    } else if starts_with_bom(content) {
        return (Verdict::NotBomlessUtf8, Vec::new());
    } else {
        content
    };

    // 2. Shared 8 KB binary sniff — magic numbers, NUL bytes, control-byte ratio.
    let sniff = if payload.len() > BINARY_SNIFF_BYTES {
        &payload[..BINARY_SNIFF_BYTES]
    } else {
        payload
    };
    if is_binary(sniff) {
        return (Verdict::Binary, Vec::new());
    }

    // 3. Validate strict UTF-8 while normalizing CRLF / bare CR to LF.
    let mut normalized = Vec::with_capacity(payload.len());
    if !validate_and_normalize(payload, &mut normalized) {
        return (Verdict::NotBomlessUtf8, Vec::new());
    }

    (Verdict::Indexed, extract_trigram_set(&normalized))
}

/// True if the span begins with a UTF-16 (LE/BE) or UTF-32 (LE/BE) byte-order mark.
fn starts_with_bom(c: &[u8]) -> bool {
    const BOMS: &[&[u8]] = &[
        b"\x00\x00\xFE\xFF",
        b"\xFF\xFE\x00\x00",
        b"\xFF\xFE",
        b"\xFE\xFF",
    ];
    BOMS.iter().any(|bom| c.starts_with(bom))
}

fn starts_with_utf8_bom(c: &[u8]) -> bool {
    c.starts_with(b"\xEF\xBB\xBF")
}

/// Mirrors managed `BinaryDetector.IsBinary(ReadOnlySpan<byte>)`: magic numbers → embedded NUL →
/// control-byte ratio over samples of at least 512 bytes.
fn is_binary(s: &[u8]) -> bool {
    if s.is_empty() {
        return false;
    }
    if has_binary_magic(s) {
        return true;
    }
    if s.contains(&0) {
        return true;
    }
    if s.len() >= 512 {
        let mut suspicious = 0usize;
        for &b in s {
            if b == 0x09 || b == 0x0A || b == 0x0D {
                continue; // tab, LF, CR
            }
            if b >= 0x20 && b < 0x7F {
                continue; // printable ASCII
            }
            if b >= 0x80 {
                continue; // possibly UTF-8 — don't penalize
            }
            suspicious += 1; // 0x01-0x08, 0x0B, 0x0C, 0x0E-0x1F, 0x7F
        }
        if suspicious * 100 / s.len() > 5 {
            return true;
        }
    }
    false
}

/// Mirrors managed `BinaryDetector.HasBinaryMagic`.
fn has_binary_magic(s: &[u8]) -> bool {
    if s.len() < 4 {
        return false;
    }
    const PREFIXES: &[(usize, &[u8])] = &[
        (4, b"\x1F\x8B"),
        (4, b"PK\x03"),
        (4, b"PK\x05"),
        (4, b"PK\x07"),
        (4, b"\x89PNG"),
        (4, b"\xFF\xD8\xFF"),
        (4, b"%PDF"),
        (4, b"\x7FELF"),
        (4, b"MZ"),
        (6, b"7z\xBC\xAF\x27\x1C"),
        (4, b"\x28\xB5\x2F\xFD"),
        (4, b"\xCE\xFA\xED\xFE"),
        (4, b"\xCF\xFA\xED\xFE"),
        (4, b"\xCA\xFE\xBA\xBE"),
        (6, b"SQLite"),
        (4, b"BZh"),
        (6, b"\xFD7zXZ\x00"),
        (7, b"Rar!\x1A\x07"),
    ];
    PREFIXES
        .iter()
        .any(|&(minimum_length, prefix)| s.len() >= minimum_length && s.starts_with(prefix))
}

/// Validates strict RFC-3629 UTF-8 (rejecting overlong encodings, surrogate halves, out-of-range code
/// points, and truncated sequences) while appending canonical bytes to `dst` with CRLF and bare CR
/// normalized to LF. Returns false on the first decoder error. Mirrors managed
/// `ContentRepresentation.ValidateAndNormalize`.
fn validate_and_normalize(src: &[u8], dst: &mut Vec<u8>) -> bool {
    let n = src.len();
    let mut i = 0usize;
    while i < n {
        let b = src[i];

        if b == b'\r' {
            dst.push(b'\n');
            // Collapse CRLF into a single LF; a bare CR also becomes LF.
            i += if i + 1 < n && src[i + 1] == b'\n' { 2 } else { 1 };
            continue;
        }

        if b < 0x80 {
            dst.push(b);
            i += 1;
            continue;
        }

        let (extra, min, mut cp): (usize, u32, u32) = if (b & 0xE0) == 0xC0 {
            (1, 0x80, (b & 0x1F) as u32)
        } else if (b & 0xF0) == 0xE0 {
            (2, 0x800, (b & 0x0F) as u32)
        } else if (b & 0xF8) == 0xF0 {
            (3, 0x10000, (b & 0x07) as u32)
        } else {
            return false; // invalid lead byte (continuation byte, 0xF8+, etc.)
        };

        if i + extra >= n {
            return false; // truncated multi-byte sequence
        }

        for k in 1..=extra {
            let c = src[i + k];
            if (c & 0xC0) != 0x80 {
                return false; // bad continuation byte
            }
            cp = (cp << 6) | (c & 0x3F) as u32;
        }

        if cp < min {
            return false; // overlong encoding
        }
        if cp > 0x10FFFF {
            return false; // out of range
        }
        if (0xD800..=0xDFFF).contains(&cp) {
            return false; // UTF-16 surrogate half
        }

        for k in 0..=extra {
            dst.push(src[i + k]);
        }
        i += extra + 1;
    }
    true
}

/// Extracts the sorted, de-duplicated 3-byte trigram set from canonical bytes (mirrors managed
/// `ContentRepresentation.ExtractTrigrams`). Each trigram is packed into the low 24 bits of a `u32`.
fn extract_trigram_set(bytes: &[u8]) -> Vec<u32> {
    if bytes.len() < 3 {
        return Vec::new();
    }
    let mut set: HashSet<u32> = HashSet::new();
    let mut i = 0usize;
    while i + 2 < bytes.len() {
        let t = ((bytes[i] as u32) << 16) | ((bytes[i + 1] as u32) << 8) | (bytes[i + 2] as u32);
        set.insert(t);
        i += 1;
    }
    let mut list: Vec<u32> = set.into_iter().collect();
    list.sort_unstable();
    list
}

// ─────────────────────────────────────────────────────────────────────────────
//  FFI — additive, independent of the search ABI (its own version probe).
// ─────────────────────────────────────────────────────────────────────────────

/// A trigram-extraction result handed to .NET. `verdict` is the raw `Verdict` discriminant; `trigrams`
/// owns `trigram_count` packed `u32` trigrams (null/0 for a non-`Indexed` or empty result). The caller
/// must release it with `qg_index_free_trigrams`.
#[repr(C)]
pub struct QgTrigramResult {
    pub verdict: c_int,
    pub trigrams: *mut u32,
    pub trigram_count: usize,
}

/// Version probe for the **index** FFI surface, independent of `qg_abi_version` (the search ABI). The
/// managed index binding calls this to confirm the loaded `yagu_core.dll` carries the index functions.
#[no_mangle]
pub extern "C" fn qg_index_abi_version() -> c_uint {
    1
}

/// Classifies `data[..data_len]` and, when it is admissible BOM-less UTF-8, writes its sorted distinct
/// trigram set into `*out`. Returns 0 on success, -1 when `out` is null. A null/empty input is treated
/// as empty content (an `Indexed` verdict with no trigrams).
///
/// # Safety
/// `data` must point to at least `data_len` readable bytes (or be null when `data_len` is 0). `out` must
/// point to a writable `QgTrigramResult`. The result must be released with `qg_index_free_trigrams`.
#[no_mangle]
pub unsafe extern "C" fn qg_index_extract_trigrams(
    data: *const u8,
    data_len: usize,
    out: *mut QgTrigramResult,
) -> c_int {
    if out.is_null() {
        return -1;
    }

    let content: &[u8] = if data.is_null() || data_len == 0 {
        &[]
    } else {
        std::slice::from_raw_parts(data, data_len)
    };

    let (verdict, mut trigrams) = extract_trigrams(content);
    trigrams.shrink_to_fit(); // cap == len so the caller's free can rebuild the Vec exactly
    let count = trigrams.len();
    let ptr = if count == 0 {
        std::ptr::null_mut()
    } else {
        trigrams.as_mut_ptr()
    };
    std::mem::forget(trigrams); // ownership transfers to the caller

    let r = &mut *out;
    r.verdict = verdict as c_int;
    r.trigrams = ptr;
    r.trigram_count = count;
    0
}

/// Releases a `QgTrigramResult` populated by `qg_index_extract_trigrams`.
///
/// # Safety
/// `out` must point to a `QgTrigramResult` previously populated by `qg_index_extract_trigrams`, and must
/// not be freed twice.
#[no_mangle]
pub unsafe extern "C" fn qg_index_free_trigrams(out: *mut QgTrigramResult) {
    if out.is_null() {
        return;
    }
    let r = &mut *out;
    if !r.trigrams.is_null() {
        // shrink_to_fit made cap == len, so len == count.
        let _ = Vec::from_raw_parts(r.trigrams, r.trigram_count, r.trigram_count);
        r.trigrams = std::ptr::null_mut();
        r.trigram_count = 0;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Posting index + query evaluation (plan §3.1/§5) — mirrors managed
//  `TrigramPostingIndex.Build`/`EvaluateSet`. Produces a candidate document-id
//  *superset*; each candidate is still re-verified live. The query crosses the
//  boundary as a compact post-order (RPN) byte stream (see the managed
//  `TrigramQueryRpn` encoder), so the worker never needs to reconstruct the C#
//  expression object graph.
// ─────────────────────────────────────────────────────────────────────────────

/// An in-memory trigram posting index: each packed-`u32` trigram maps to the ascending list of
/// document ids that contain it. Document ids are the contiguous range `[0, doc_count)`.
pub struct PostingIndex {
    postings: HashMap<u32, Vec<i32>>,
    doc_count: i32,
}

impl PostingIndex {
    /// Builds the index from `doc_count` documents laid out CSR-style: `offsets[d]..offsets[d + 1]`
    /// is document `d`'s (sorted, distinct) trigram slice within `trigrams`. Mirrors managed
    /// `TrigramPostingIndex.Build` (documents visited in ascending id order → posting lists stay sorted;
    /// duplicate trigrams within a document are guarded).
    pub fn build_from_csr(trigrams: &[u32], offsets: &[usize], doc_count: usize) -> Self {
        let mut postings: HashMap<u32, Vec<i32>> = HashMap::new();
        for doc_id in 0..doc_count {
            let (start, end) = (offsets[doc_id], offsets[doc_id + 1]);
            for &t in &trigrams[start..end] {
                let list = postings.entry(t).or_default();
                let id = doc_id as i32;
                if list.last() != Some(&id) {
                    list.push(id);
                }
            }
        }
        PostingIndex {
            postings,
            doc_count: doc_count as i32,
        }
    }

    fn posting(&self, trigram: u32) -> Vec<i32> {
        self.postings.get(&trigram).cloned().unwrap_or_default()
    }

    fn all_docs(&self) -> Vec<i32> {
        (0..self.doc_count).collect()
    }

    /// Evaluates a post-order (RPN) query byte stream into the sorted candidate document-id list, or
    /// `None` on a malformed stream. Opcodes: `0`=All, `1`=None, `2`=Trigram(+u32 LE), `3`=And(+u16 LE
    /// child count), `4`=Or(+u16 LE child count). Rare-first intersection and order-independent union
    /// yield the same candidate *set* as the managed reference.
    pub fn evaluate_rpn(&self, rpn: &[u8]) -> Option<Vec<i32>> {
        let mut stack: Vec<Vec<i32>> = Vec::new();
        let mut i = 0usize;
        while i < rpn.len() {
            let op = rpn[i];
            i += 1;
            match op {
                0 => stack.push(self.all_docs()),
                1 => stack.push(Vec::new()),
                2 => {
                    if i + 4 > rpn.len() {
                        return None;
                    }
                    let t = u32::from_le_bytes([rpn[i], rpn[i + 1], rpn[i + 2], rpn[i + 3]]);
                    i += 4;
                    stack.push(self.posting(t));
                }
                3 | 4 => {
                    if i + 2 > rpn.len() {
                        return None;
                    }
                    let count = u16::from_le_bytes([rpn[i], rpn[i + 1]]) as usize;
                    i += 2;
                    if stack.len() < count {
                        return None;
                    }
                    let operands = stack.split_off(stack.len() - count);
                    let result = if op == 3 {
                        intersect_all(operands)
                    } else {
                        union_all(operands)
                    };
                    stack.push(result);
                }
                _ => return None,
            }
        }
        if stack.len() == 1 {
            stack.pop()
        } else {
            None
        }
    }
}

/// Intersects sorted candidate lists (rare-first, matching managed `EvaluateAnd`). An empty operand
/// makes the whole conjunction empty.
fn intersect_all(mut lists: Vec<Vec<i32>>) -> Vec<i32> {
    if lists.is_empty() {
        return Vec::new();
    }
    if lists.iter().any(|l| l.is_empty()) {
        return Vec::new();
    }
    lists.sort_by_key(|l| l.len());
    let mut acc = lists[0].clone();
    for l in &lists[1..] {
        if acc.is_empty() {
            break;
        }
        acc = intersect_two(&acc, l);
    }
    acc
}

fn intersect_two(a: &[i32], b: &[i32]) -> Vec<i32> {
    let mut r = Vec::with_capacity(a.len().min(b.len()));
    let (mut i, mut j) = (0usize, 0usize);
    while i < a.len() && j < b.len() {
        match a[i].cmp(&b[j]) {
            std::cmp::Ordering::Equal => {
                r.push(a[i]);
                i += 1;
                j += 1;
            }
            std::cmp::Ordering::Less => i += 1,
            std::cmp::Ordering::Greater => j += 1,
        }
    }
    r
}

/// Unions sorted candidate lists (order-independent, matching managed `EvaluateOr` as a set).
fn union_all(lists: Vec<Vec<i32>>) -> Vec<i32> {
    let mut set: BTreeSet<i32> = BTreeSet::new();
    for l in lists {
        set.extend(l);
    }
    set.into_iter().collect()
}

/// A candidate-document-id result handed to .NET. `candidates` owns `count` ascending `i32` ids
/// (null/0 when empty). The caller must release it with `qg_index_free_candidates`.
#[repr(C)]
pub struct QgCandidateResult {
    pub candidates: *mut i32,
    pub count: usize,
}

/// Builds a posting index from CSR-laid-out per-document trigrams and evaluates the RPN `query_rpn`
/// into the sorted candidate set written to `*out`. Returns 0 on success, or a negative error code
/// (`-1` null out, `-2` bad offsets, `-3` offsets out of range/non-monotonic, `-4` malformed query).
///
/// # Safety
/// `doc_trigrams`/`doc_offsets`/`query_rpn` must point to at least their stated lengths of readable
/// data (or be null when the length is 0). `out` must point to a writable `QgCandidateResult`, released
/// with `qg_index_free_candidates`.
#[no_mangle]
pub unsafe extern "C" fn qg_index_evaluate(
    doc_trigrams: *const u32,
    doc_trigrams_len: usize,
    doc_offsets: *const usize,
    doc_offsets_len: usize,
    query_rpn: *const u8,
    query_rpn_len: usize,
    out: *mut QgCandidateResult,
) -> c_int {
    if out.is_null() {
        return -1;
    }
    if doc_offsets.is_null() || doc_offsets_len == 0 {
        return -2;
    }

    let offsets = std::slice::from_raw_parts(doc_offsets, doc_offsets_len);
    let doc_count = doc_offsets_len - 1;
    let trigrams: &[u32] = if doc_trigrams.is_null() || doc_trigrams_len == 0 {
        &[]
    } else {
        std::slice::from_raw_parts(doc_trigrams, doc_trigrams_len)
    };
    let rpn: &[u8] = if query_rpn.is_null() || query_rpn_len == 0 {
        &[]
    } else {
        std::slice::from_raw_parts(query_rpn, query_rpn_len)
    };

    // Offsets must start at 0-ish, be monotonic, and stay within the trigram buffer.
    for w in offsets.windows(2) {
        if w[0] > w[1] || w[1] > trigrams.len() {
            return -3;
        }
    }

    let index = PostingIndex::build_from_csr(trigrams, offsets, doc_count);
    let mut candidates = match index.evaluate_rpn(rpn) {
        Some(c) => c,
        None => return -4,
    };
    candidates.shrink_to_fit();
    let count = candidates.len();
    let ptr = if count == 0 {
        std::ptr::null_mut()
    } else {
        candidates.as_mut_ptr()
    };
    std::mem::forget(candidates);

    let r = &mut *out;
    r.candidates = ptr;
    r.count = count;
    0
}

/// Releases a `QgCandidateResult` populated by `qg_index_evaluate`.
///
/// # Safety
/// `out` must point to a `QgCandidateResult` previously populated by `qg_index_evaluate`, not freed twice.
#[no_mangle]
pub unsafe extern "C" fn qg_index_free_candidates(out: *mut QgCandidateResult) {
    if out.is_null() {
        return;
    }
    let r = &mut *out;
    if !r.candidates.is_null() {
        let _ = Vec::from_raw_parts(r.candidates, r.count, r.count);
        r.candidates = std::ptr::null_mut();
        r.count = 0;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  On-disk `content.bin` reader (plan §3.4) — verifies + parses the managed
//  reference format so the worker can query a generation the managed side wrote.
//  Layout: <body> || SHA-256(body). Body: LE i32 doc_count, then per document
//  { LE i32 trigram_count, LE u32 * trigram_count }.
// ─────────────────────────────────────────────────────────────────────────────

const CONTENT_DIGEST_BYTES: usize = 32; // SHA-256

/// Verifies the trailing SHA-256 of a `content.bin` file and parses it into per-document trigram
/// slices. Returns `None` on a bad digest, truncation, or a malformed body (the caller then live-scans).
pub fn parse_content_bin(file_bytes: &[u8]) -> Option<Vec<Vec<u32>>> {
    use sha2::{Digest, Sha256};

    if file_bytes.len() < CONTENT_DIGEST_BYTES {
        return None;
    }
    let split = file_bytes.len() - CONTENT_DIGEST_BYTES;
    let (body, stored) = file_bytes.split_at(split);
    let computed = Sha256::digest(body);
    // Constant-time-ish compare is unnecessary for an integrity check, but keep it simple + total.
    if computed.as_slice() != stored {
        return None;
    }

    let mut cursor = 0usize;
    let doc_count = read_i32_le(body, &mut cursor)?;
    if doc_count < 0 {
        return None;
    }
    let doc_count = doc_count as usize;
    let mut docs: Vec<Vec<u32>> = Vec::with_capacity(doc_count);
    for _ in 0..doc_count {
        let n = read_i32_le(body, &mut cursor)?;
        if n < 0 {
            return None;
        }
        let n = n as usize;
        let mut trigrams = Vec::with_capacity(n);
        for _ in 0..n {
            trigrams.push(read_u32_le(body, &mut cursor)?);
        }
        docs.push(trigrams);
    }
    // Trailing garbage in the body → malformed.
    if cursor != body.len() {
        return None;
    }
    Some(docs)
}

fn read_i32_le(buf: &[u8], cursor: &mut usize) -> Option<i32> {
    if *cursor + 4 > buf.len() {
        return None;
    }
    let v = i32::from_le_bytes([buf[*cursor], buf[*cursor + 1], buf[*cursor + 2], buf[*cursor + 3]]);
    *cursor += 4;
    Some(v)
}

fn read_u32_le(buf: &[u8], cursor: &mut usize) -> Option<u32> {
    if *cursor + 4 > buf.len() {
        return None;
    }
    let v = u32::from_le_bytes([buf[*cursor], buf[*cursor + 1], buf[*cursor + 2], buf[*cursor + 3]]);
    *cursor += 4;
    Some(v)
}

/// Verifies + parses a `content.bin` file, builds its posting index, and evaluates the RPN `query_rpn`
/// into the sorted candidate set written to `*out` — the worker's "load a managed-written generation
/// and answer a query" path. Returns 0 on success, or a negative error (`-1` null out, `-5` bad
/// checksum/malformed content, `-4` malformed query).
///
/// # Safety
/// `content_bin`/`query_rpn` must point to at least their stated lengths of readable bytes (or be null
/// when the length is 0). `out` must point to a writable `QgCandidateResult`, released with
/// `qg_index_free_candidates`.
#[no_mangle]
pub unsafe extern "C" fn qg_index_query_content_bin(
    content_bin: *const u8,
    content_bin_len: usize,
    query_rpn: *const u8,
    query_rpn_len: usize,
    out: *mut QgCandidateResult,
) -> c_int {
    if out.is_null() {
        return -1;
    }
    let file_bytes: &[u8] = if content_bin.is_null() || content_bin_len == 0 {
        &[]
    } else {
        std::slice::from_raw_parts(content_bin, content_bin_len)
    };
    let rpn: &[u8] = if query_rpn.is_null() || query_rpn_len == 0 {
        &[]
    } else {
        std::slice::from_raw_parts(query_rpn, query_rpn_len)
    };

    let docs = match parse_content_bin(file_bytes) {
        Some(d) => d,
        None => return -5,
    };

    // Build the posting index directly from the parsed per-document trigram vecs.
    let mut postings: HashMap<u32, Vec<i32>> = HashMap::new();
    for (doc_id, trigrams) in docs.iter().enumerate() {
        let id = doc_id as i32;
        for &t in trigrams {
            let list = postings.entry(t).or_default();
            if list.last() != Some(&id) {
                list.push(id);
            }
        }
    }
    // Postings must be sorted for the intersection/union; docs are visited in ascending id order, so
    // they already are, but within a single document a caller could pass unsorted trigrams — the guard
    // above only dedups adjacent ids, so re-sort defensively.
    for list in postings.values_mut() {
        list.sort_unstable();
        list.dedup();
    }
    let index = PostingIndex {
        postings,
        doc_count: docs.len() as i32,
    };

    let mut candidates = match index.evaluate_rpn(rpn) {
        Some(c) => c,
        None => return -4,
    };
    candidates.shrink_to_fit();
    let count = candidates.len();
    let ptr = if count == 0 {
        std::ptr::null_mut()
    } else {
        candidates.as_mut_ptr()
    };
    std::mem::forget(candidates);

    let r = &mut *out;
    r.candidates = ptr;
    r.count = count;
    0
}

// ─────────────────────────────────────────────────────────────────────────────
//  format-v3 query-postings reader (plan §5.1) — mirrors the managed
//  `ContentIndexV3Reader`/`ContentIndexV3BlockFile`. Verifies the block-framed
//  `query-postings.v3` file (FNV-1a-64 header + per-block hashes) and parses its
//  inverted postings into a `PostingIndex`, so the worker can map + query the
//  query-ready format the managed build writes. Byte-identical candidate sets to
//  the managed reference (proven by the C# `ContentIndexRustParityTests`).
// ─────────────────────────────────────────────────────────────────────────────

const V3_FILE_MAGIC: u32 = 0x3356_5159; // 'Y','Q','V','3' packed, matches the managed FileMagic
const V3_SECTION_POSTINGS: u16 = 1;
const V3_FORMAT_VERSION: u16 = 1;
const V3_BLOCK_SIZE: usize = 64 * 1024;

/// FNV-1a (64-bit), matching the managed `V3Fnv` (block/header integrity + path hashing).
fn fnv1a_64(data: &[u8]) -> u64 {
    let mut h: u64 = 0xcbf2_9ce4_8422_2325;
    for &b in data {
        h ^= b as u64;
        h = h.wrapping_mul(0x0000_0100_0000_01b3);
    }
    h
}

fn v3_u16(buf: &[u8], off: usize) -> Option<u16> {
    buf.get(off..off + 2).map(|s| u16::from_le_bytes([s[0], s[1]]))
}
fn v3_u32(buf: &[u8], off: usize) -> Option<u32> {
    buf.get(off..off + 4)
        .map(|s| u32::from_le_bytes([s[0], s[1], s[2], s[3]]))
}
fn v3_u64(buf: &[u8], off: usize) -> Option<u64> {
    buf.get(off..off + 8)
        .map(|s| u64::from_le_bytes([s[0], s[1], s[2], s[3], s[4], s[5], s[6], s[7]]))
}

fn v3_u64_validated(buf: &[u8], off: usize) -> u64 {
    u64::from_le_bytes([
        buf[off],
        buf[off + 1],
        buf[off + 2],
        buf[off + 3],
        buf[off + 4],
        buf[off + 5],
        buf[off + 6],
        buf[off + 7],
    ])
}

/// Verifies + parses a block-framed `query-postings.v3` file into a `PostingIndex`. Returns `None` on a
/// bad magic/section/version/block-size, an inconsistent block count/length, a failed header or block
/// integrity check, or a malformed postings body (the caller then live-scans).
pub fn parse_v3_postings(file_bytes: &[u8]) -> Option<PostingIndex> {
    // Header: magic u32 | section u16 | version u16 | blockSize u32 | blockCount u32 | bodyLength u64.
    if v3_u32(file_bytes, 0)? != V3_FILE_MAGIC {
        return None;
    }
    if v3_u16(file_bytes, 4)? != V3_SECTION_POSTINGS || v3_u16(file_bytes, 6)? != V3_FORMAT_VERSION {
        return None;
    }
    if v3_u32(file_bytes, 8)? as usize != V3_BLOCK_SIZE {
        return None;
    }
    let block_count = v3_u32(file_bytes, 12)? as usize;
    let body_length = usize::try_from(v3_u64(file_bytes, 16)?).unwrap_or(usize::MAX);
    let expected_blocks = body_length.div_ceil(V3_BLOCK_SIZE);
    if block_count != expected_blocks {
        return None;
    }
    let header_hash_offset = block_count.saturating_mul(8).saturating_add(24);
    let body_start = header_hash_offset.saturating_add(8);
    let body = match file_bytes.get(body_start..) {
        Some(body) => body,
        None => return None,
    };
    if body.len() != body_length {
        return None;
    }
    // Header integrity (covers magic..block-hash-table).
    if fnv1a_64(&file_bytes[0..header_hash_offset]) != v3_u64_validated(file_bytes, header_hash_offset) {
        return None;
    }
    // Every block's integrity (the whole postings body is read, so verify all blocks up front).
    for b in 0..block_count {
        let start = b * V3_BLOCK_SIZE;
        let len = V3_BLOCK_SIZE.min(body_length - start);
        if fnv1a_64(&body[start..start + len]) != v3_u64_validated(file_bytes, 24 + b * 8) {
            return None;
        }
    }

    // Postings body: u32 trigramCount T, u32 docCount N, then directory[T]{u32 trigram, u32 count,
    // u64 offset-into-body}, then the postings region (sorted u32 content-ids).
    let trigram_count = v3_u32(body, 0)? as usize;
    let doc_count = v3_u32(body, 4)? as i32;
    if doc_count < 0 {
        return None;
    }
    const V3_POSTINGS_HEADER: usize = 16;
    const V3_DIR_ENTRY: usize = 16;
    let mut postings: HashMap<u32, Vec<i32>> = HashMap::with_capacity(trigram_count);
    for t in 0..trigram_count {
        let e = V3_POSTINGS_HEADER.saturating_add(t.saturating_mul(V3_DIR_ENTRY));
        let trigram = v3_u32(body, e)?;
        let count = v3_u32(body, e + 4)? as usize;
        let offset = v3_u64(body, e + 8)?;
        let end = offset.saturating_add((count as u64) * 4);
        if end > body.len() as u64 {
            return None;
        }
        let offset = offset as usize;
        let end = end as usize;
        let mut list = Vec::with_capacity(count);
        for encoded in body[offset..end].chunks_exact(4) {
            list.push(u32::from_le_bytes([encoded[0], encoded[1], encoded[2], encoded[3]]) as i32);
        }
        postings.insert(trigram, list);
    }
    Some(PostingIndex {
        postings,
        doc_count,
    })
}

/// Verifies + parses a `query-postings.v3` file, then evaluates the RPN `query_rpn` into the sorted
/// candidate set written to `*out` — the worker's "map a query-ready generation and answer a query"
/// path. Returns 0 on success, or a negative error (`-1` null out, `-6` bad/corrupt v3 postings,
/// `-4` malformed query).
///
/// # Safety
/// `v3_bytes`/`query_rpn` must point to at least their stated lengths of readable bytes (or be null
/// when the length is 0). `out` must point to a writable `QgCandidateResult`, released with
/// `qg_index_free_candidates`.
#[no_mangle]
pub unsafe extern "C" fn qg_index_query_postings_v3(
    v3_bytes: *const u8,
    v3_bytes_len: usize,
    query_rpn: *const u8,
    query_rpn_len: usize,
    out: *mut QgCandidateResult,
) -> c_int {
    if out.is_null() {
        return -1;
    }
    let file_bytes: &[u8] = if v3_bytes.is_null() || v3_bytes_len == 0 {
        &[]
    } else {
        std::slice::from_raw_parts(v3_bytes, v3_bytes_len)
    };
    let rpn: &[u8] = if query_rpn.is_null() || query_rpn_len == 0 {
        &[]
    } else {
        std::slice::from_raw_parts(query_rpn, query_rpn_len)
    };

    let index = match parse_v3_postings(file_bytes) {
        Some(i) => i,
        None => return -6,
    };
    let mut candidates = match index.evaluate_rpn(rpn) {
        Some(c) => c,
        None => return -4,
    };
    candidates.shrink_to_fit();
    let count = candidates.len();
    let ptr = if count == 0 {
        std::ptr::null_mut()
    } else {
        candidates.as_mut_ptr()
    };
    std::mem::forget(candidates);

    let r = &mut *out;
    r.candidates = ptr;
    r.count = count;
    0
}

#[cfg(test)]
mod tests {
    use super::*;

    fn trigrams_of(s: &[u8]) -> (Verdict, Vec<u32>) {
        extract_trigrams(s)
    }

    fn pack(a: u8, b: u8, c: u8) -> u32 {
        ((a as u32) << 16) | ((b as u32) << 8) | (c as u32)
    }

    // ─── format-v3 query-postings reader helpers + tests ───

    /// Builds the postings body (mirrors managed `ContentIndexV3Format.BuildPostingsBody`) from per-doc
    /// trigram lists (assumed sorted+distinct within a doc).
    fn build_v3_postings_body(docs: &[Vec<u32>]) -> Vec<u8> {
        use std::collections::BTreeMap;
        let mut postings: BTreeMap<u32, Vec<u32>> = BTreeMap::new();
        for (doc_id, trigrams) in docs.iter().enumerate() {
            for &t in trigrams {
                let list = postings.entry(t).or_default();
                if list.last() != Some(&(doc_id as u32)) {
                    list.push(doc_id as u32);
                }
            }
        }
        let trigram_count = postings.len();
        let header = 16usize;
        let dir_entry = 16usize;
        let region_start = header + trigram_count * dir_entry;
        let total_ints: usize = postings.values().map(|v| v.len()).sum();
        let body_len = region_start + total_ints * 4;
        let mut body = vec![0u8; body_len];
        body[0..4].copy_from_slice(&(trigram_count as u32).to_le_bytes());
        body[4..8].copy_from_slice(&(docs.len() as u32).to_le_bytes());
        let mut dir = header;
        let mut cursor = region_start;
        for (trigram, list) in &postings {
            body[dir..dir + 4].copy_from_slice(&trigram.to_le_bytes());
            body[dir + 4..dir + 8].copy_from_slice(&(list.len() as u32).to_le_bytes());
            body[dir + 8..dir + 16].copy_from_slice(&(cursor as u64).to_le_bytes());
            dir += dir_entry;
            for &doc in list {
                body[cursor..cursor + 4].copy_from_slice(&doc.to_le_bytes());
                cursor += 4;
            }
        }
        body
    }

    /// Wraps a body in the block-framed file layout (mirrors managed `ContentIndexV3BlockFile.Write`).
    fn frame_v3(section: u16, version: u16, body: &[u8]) -> Vec<u8> {
        let block_count = if body.is_empty() {
            0
        } else {
            (body.len() + V3_BLOCK_SIZE - 1) / V3_BLOCK_SIZE
        };
        let header_hash_offset = 24 + block_count * 8;
        let mut file = vec![0u8; header_hash_offset + 8 + body.len()];
        file[0..4].copy_from_slice(&V3_FILE_MAGIC.to_le_bytes());
        file[4..6].copy_from_slice(&section.to_le_bytes());
        file[6..8].copy_from_slice(&version.to_le_bytes());
        file[8..12].copy_from_slice(&(V3_BLOCK_SIZE as u32).to_le_bytes());
        file[12..16].copy_from_slice(&(block_count as u32).to_le_bytes());
        file[16..24].copy_from_slice(&(body.len() as u64).to_le_bytes());
        for b in 0..block_count {
            let start = b * V3_BLOCK_SIZE;
            let len = V3_BLOCK_SIZE.min(body.len() - start);
            let h = fnv1a_64(&body[start..start + len]);
            file[24 + b * 8..24 + b * 8 + 8].copy_from_slice(&h.to_le_bytes());
        }
        let header_hash = fnv1a_64(&file[0..header_hash_offset]);
        file[header_hash_offset..header_hash_offset + 8].copy_from_slice(&header_hash.to_le_bytes());
        file[header_hash_offset + 8..].copy_from_slice(body);
        file
    }

    fn rpn_trigram(t: u32) -> Vec<u8> {
        let mut rpn = vec![2u8];
        rpn.extend_from_slice(&t.to_le_bytes());
        rpn
    }

    #[test]
    fn v3_postings_parse_and_query_matches_expected() {
        // doc 0 → {10, 20}, doc 1 → {20, 30}
        let body = build_v3_postings_body(&[vec![10, 20, 20], vec![20, 30]]);
        let file = frame_v3(V3_SECTION_POSTINGS, V3_FORMAT_VERSION, &body);

        let index = parse_v3_postings(&file).expect("valid v3 postings");
        assert_eq!(index.evaluate_rpn(&rpn_trigram(20)).unwrap(), vec![0, 1]);
        assert_eq!(index.evaluate_rpn(&rpn_trigram(10)).unwrap(), vec![0]);
        assert_eq!(index.evaluate_rpn(&rpn_trigram(30)).unwrap(), vec![1]);
        assert!(index.evaluate_rpn(&rpn_trigram(99)).unwrap().is_empty());
    }

    #[test]
    fn v3_postings_rejects_corruption_and_bad_headers() {
        let body = build_v3_postings_body(&[vec![10, 20], vec![20, 30]]);
        let file = frame_v3(V3_SECTION_POSTINGS, V3_FORMAT_VERSION, &body);
        let body_start = 24 + 1 * 8 + 8; // one block → header hash at 32, body at 40

        // Corrupt a body byte → block integrity fails.
        let mut bad_body = file.clone();
        bad_body[body_start] ^= 0xFF;
        assert!(parse_v3_postings(&bad_body).is_none());

        // Corrupt a byte inside the block-hash table (covered by the header hash).
        let mut bad_header = file.clone();
        bad_header[25] ^= 0xFF;
        assert!(parse_v3_postings(&bad_header).is_none());

        // Wrong section / version / magic are rejected.
        assert!(parse_v3_postings(&frame_v3(2, V3_FORMAT_VERSION, &body)).is_none());
        assert!(parse_v3_postings(&frame_v3(V3_SECTION_POSTINGS, 99, &body)).is_none());
        let mut bad_magic = file.clone();
        bad_magic[0] ^= 0xFF;
        assert!(parse_v3_postings(&bad_magic).is_none());

        let mut bad_block_size = file.clone();
        bad_block_size[8..12].copy_from_slice(&1u32.to_le_bytes());
        assert!(parse_v3_postings(&bad_block_size).is_none());

        let mut bad_block_count = file.clone();
        bad_block_count[12..16].copy_from_slice(&0u32.to_le_bytes());
        assert!(parse_v3_postings(&bad_block_count).is_none());

        let mut bad_file_length = file.clone();
        bad_file_length.push(0);
        assert!(parse_v3_postings(&bad_file_length).is_none());

        for truncated_len in [4usize, 6, 8, 12, 16, 24] {
            assert!(parse_v3_postings(&file[..truncated_len]).is_none());
        }

        assert!(parse_v3_postings(&frame_v3(V3_SECTION_POSTINGS, V3_FORMAT_VERSION, &[])).is_none());
        assert!(parse_v3_postings(&[]).is_none());
    }

    #[test]
    fn v3_postings_rejects_malformed_body() {
        let mut negative_doc_count = build_v3_postings_body(&[vec![10]]);
        negative_doc_count[4..8].copy_from_slice(&u32::MAX.to_le_bytes());
        let file = frame_v3(V3_SECTION_POSTINGS, V3_FORMAT_VERSION, &negative_doc_count);
        assert!(parse_v3_postings(&file).is_none());

        let mut posting_past_end = build_v3_postings_body(&[vec![10]]);
        let invalid_offset = posting_past_end.len() as u64;
        posting_past_end[24..32].copy_from_slice(&invalid_offset.to_le_bytes());
        let file = frame_v3(V3_SECTION_POSTINGS, V3_FORMAT_VERSION, &posting_past_end);
        assert!(parse_v3_postings(&file).is_none());

        for body_len in [4usize, 16, 20, 24] {
            let mut truncated_directory = vec![0u8; body_len];
            truncated_directory[0..4].copy_from_slice(&1u32.to_le_bytes());
            let file = frame_v3(V3_SECTION_POSTINGS, V3_FORMAT_VERSION, &truncated_directory);
            assert!(parse_v3_postings(&file).is_none());
        }
    }

    #[test]
    fn v3_postings_ffi_roundtrips() {
        let body = build_v3_postings_body(&[vec![10, 20], vec![20, 30]]);
        let file = frame_v3(V3_SECTION_POSTINGS, V3_FORMAT_VERSION, &body);
        let rpn = rpn_trigram(20);
        let mut out = QgCandidateResult {
            candidates: std::ptr::null_mut(),
            count: 0,
        };
        unsafe {
            let rc = qg_index_query_postings_v3(file.as_ptr(), file.len(), rpn.as_ptr(), rpn.len(), &mut out);
            assert_eq!(rc, 0);
            let ids = std::slice::from_raw_parts(out.candidates, out.count).to_vec();
            assert_eq!(ids, vec![0, 1]);
            qg_index_free_candidates(&mut out);
        }
        // A corrupt file returns the -6 error code.
        let mut bad = file.clone();
        bad[24 + 1 * 8 + 8] ^= 0xFF;
        let mut out2 = QgCandidateResult {
            candidates: std::ptr::null_mut(),
            count: 0,
        };
        unsafe {
            assert_eq!(
                qg_index_query_postings_v3(bad.as_ptr(), bad.len(), rpn.as_ptr(), rpn.len(), &mut out2),
                -6
            );

            assert_eq!(
                qg_index_query_postings_v3(file.as_ptr(), file.len(), rpn.as_ptr(), rpn.len(), std::ptr::null_mut()),
                -1
            );
            assert_eq!(
                qg_index_query_postings_v3(std::ptr::null(), 0, rpn.as_ptr(), rpn.len(), &mut out2),
                -6
            );
            assert_eq!(qg_index_query_postings_v3(file.as_ptr(), 0, rpn.as_ptr(), rpn.len(), &mut out2), -6);
            assert_eq!(
                qg_index_query_postings_v3(file.as_ptr(), file.len(), std::ptr::null(), 0, &mut out2),
                -4
            );
            assert_eq!(qg_index_query_postings_v3(file.as_ptr(), file.len(), rpn.as_ptr(), 0, &mut out2), -4);

            let missing_rpn = rpn_trigram(99);
            let mut empty = QgCandidateResult {
                candidates: std::ptr::null_mut(),
                count: usize::MAX,
            };
            assert_eq!(
                qg_index_query_postings_v3(
                    file.as_ptr(),
                    file.len(),
                    missing_rpn.as_ptr(),
                    missing_rpn.len(),
                    &mut empty,
                ),
                0
            );
            assert!(empty.candidates.is_null());
            assert_eq!(empty.count, 0);
            qg_index_free_candidates(&mut empty);
        }
    }

    #[test]
    fn empty_and_short_inputs_are_indexed_with_no_trigrams() {
        for input in [&b""[..], &b"a"[..], &b"ab"[..]] {
            let (v, t) = trigrams_of(input);
            assert_eq!(v, Verdict::Indexed);
            assert!(t.is_empty());
        }
    }

    #[test]
    fn ascii_trigrams_are_sorted_and_distinct() {
        let (v, t) = trigrams_of(b"foofoo");
        assert_eq!(v, Verdict::Indexed);
        // windows: foo, oof, ofo, foo -> {foo, oof, ofo}
        let mut expected = vec![pack(b'f', b'o', b'o'), pack(b'o', b'o', b'f'), pack(b'o', b'f', b'o')];
        expected.sort_unstable();
        assert_eq!(t, expected);
    }

    #[test]
    fn crlf_and_bare_cr_normalize_to_lf() {
        // "a\r\nb" and "a\rb" and "a\nb" must all yield the same trigram (a, \n, b).
        let expected = vec![pack(b'a', b'\n', b'b')];
        assert_eq!(trigrams_of(b"a\r\nb").1, expected);
        assert_eq!(trigrams_of(b"a\rb").1, expected);
        assert_eq!(trigrams_of(b"a\nb").1, expected);
        assert_eq!(trigrams_of(b"ab\r").1, vec![pack(b'a', b'b', b'\n')]);
    }

    #[test]
    fn three_linefeeds_yield_lf_trigram() {
        // Three canonical LF bytes DO produce a trigram (0A0A0A).
        assert_eq!(trigrams_of(b"\n\n\n").1, vec![pack(b'\n', b'\n', b'\n')]);
    }

    #[test]
    fn utf8_multibyte_is_indexed() {
        // "é" = C3 A9; "aéb" = 61 C3 A9 62 -> windows (61 C3 A9), (C3 A9 62).
        let (v, t) = trigrams_of("aéb".as_bytes());
        assert_eq!(v, Verdict::Indexed);
        let mut expected = vec![pack(0x61, 0xC3, 0xA9), pack(0xC3, 0xA9, 0x62)];
        expected.sort_unstable();
        assert_eq!(t, expected);
    }

    #[test]
    fn boms_are_rejected() {
        assert_eq!(trigrams_of(&[0xEF, 0xBB, 0xBF, b'a', b'b', b'c']),
            (Verdict::Indexed, vec![0x616263])); // UTF-8 BOM is stripped
        assert_eq!(trigrams_of(&[0xFF, 0xFE, b'a', b'b']).0, Verdict::NotBomlessUtf8); // UTF-16 LE
        assert_eq!(trigrams_of(&[0xFE, 0xFF, b'a', b'b']).0, Verdict::NotBomlessUtf8); // UTF-16 BE
        assert_eq!(trigrams_of(&[0x00, 0x00, 0xFE, 0xFF, b'a']).0, Verdict::NotBomlessUtf8); // UTF-32 BE
        assert_eq!(trigrams_of(&[0xFF, 0xFE, 0x00, 0x00, b'a']).0, Verdict::NotBomlessUtf8); // UTF-32 LE
    }

    #[test]
    fn nul_byte_and_magic_are_binary() {
        assert_eq!(trigrams_of(b"abc\0def").0, Verdict::Binary); // embedded NUL
        assert_eq!(trigrams_of(&[0x89, 0x50, 0x4E, 0x47, 0x0D]).0, Verdict::Binary); // PNG magic
        assert_eq!(trigrams_of(&[0x4D, 0x5A, 0x90, 0x00]).0, Verdict::Binary); // MZ magic
    }

    #[test]
    fn invalid_utf8_is_rejected() {
        assert_eq!(trigrams_of(&[b'a', 0xC3, 0x28]).0, Verdict::NotBomlessUtf8); // bad continuation
        assert_eq!(trigrams_of(&[b'a', 0xC0, 0x80]).0, Verdict::NotBomlessUtf8); // overlong
        assert_eq!(trigrams_of(&[b'a', 0xE0, 0x80]).0, Verdict::NotBomlessUtf8); // truncated
        assert_eq!(trigrams_of(&[b'a', 0xED, 0xA0, 0x80]).0, Verdict::NotBomlessUtf8); // surrogate half
    }

    #[test]
    fn control_byte_ratio_flags_binary() {
        // 512+ bytes dominated by control bytes (0x01) → binary heuristic.
        let mut v = vec![0x01u8; 600];
        // Avoid a NUL so we exercise the ratio path, not the NUL path.
        for b in v.iter_mut() {
            *b = 0x01;
        }
        assert_eq!(extract_trigrams(&v).0, Verdict::Binary);
    }

    #[test]
    fn ffi_roundtrip_matches_pure_function() {
        let input = b"hello\r\nworld";
        let (verdict, expected) = extract_trigrams(input);
        let mut result = QgTrigramResult {
            verdict: 99,
            trigrams: std::ptr::null_mut(),
            trigram_count: 0,
        };
        unsafe {
            let rc = qg_index_extract_trigrams(input.as_ptr(), input.len(), &mut result);
            assert_eq!(rc, 0);
            assert_eq!(result.verdict, verdict as c_int);
            assert_eq!(result.trigram_count, expected.len());
            let slice = std::slice::from_raw_parts(result.trigrams, result.trigram_count);
            assert_eq!(slice, expected.as_slice());
            qg_index_free_trigrams(&mut result);
            assert!(result.trigrams.is_null());
            assert_eq!(result.trigram_count, 0);
        }
    }

    #[test]
    fn ffi_null_out_returns_error() {
        unsafe {
            assert_eq!(qg_index_extract_trigrams(b"abc".as_ptr(), 3, std::ptr::null_mut()), -1);
        }
    }

    #[test]
    fn ffi_null_input_is_empty_indexed() {
        let mut result = QgTrigramResult {
            verdict: 99,
            trigrams: std::ptr::null_mut(),
            trigram_count: 0,
        };
        unsafe {
            let rc = qg_index_extract_trigrams(std::ptr::null(), 0, &mut result);
            assert_eq!(rc, 0);
            assert_eq!(result.verdict, Verdict::Indexed as c_int);
            assert_eq!(result.trigram_count, 0);
            assert!(result.trigrams.is_null());
            qg_index_free_trigrams(&mut result);
        }
    }

    #[test]
    fn index_abi_version_is_one() {
        assert_eq!(qg_index_abi_version(), 1);
    }

    // ── Posting index + RPN evaluation ──

    fn tri(a: u8, b: u8, c: u8) -> u32 {
        ((a as u32) << 16) | ((b as u32) << 8) | (c as u32)
    }

    // Builds a PostingIndex from per-doc trigram vecs.
    fn build(docs: &[Vec<u32>]) -> PostingIndex {
        let mut flat = Vec::new();
        let mut offsets = vec![0usize];
        for d in docs {
            let mut sorted = d.clone();
            sorted.sort_unstable();
            sorted.dedup();
            flat.extend_from_slice(&sorted);
            offsets.push(flat.len());
        }
        PostingIndex::build_from_csr(&flat, &offsets, docs.len())
    }

    #[test]
    fn posting_all_and_none() {
        let idx = build(&[vec![tri(b'a', b'b', b'c')], vec![tri(b'x', b'y', b'z')]]);
        assert_eq!(idx.evaluate_rpn(&[0]).unwrap(), vec![0, 1]); // All
        assert_eq!(idx.evaluate_rpn(&[1]).unwrap(), Vec::<i32>::new()); // None
    }

    #[test]
    fn posting_single_trigram() {
        let abc = tri(b'a', b'b', b'c');
        let idx = build(&[vec![abc], vec![tri(b'q', b'q', b'q')], vec![abc]]);
        let mut rpn = vec![2u8];
        rpn.extend_from_slice(&abc.to_le_bytes());
        assert_eq!(idx.evaluate_rpn(&rpn).unwrap(), vec![0, 2]);
    }

    #[test]
    fn posting_duplicate_trigrams_are_deduplicated() {
        let abc = tri(b'a', b'b', b'c');
        let idx = PostingIndex::build_from_csr(&[abc, abc], &[0, 2], 1);
        assert_eq!(idx.posting(abc), vec![0]);
    }

    #[test]
    fn posting_and_intersects() {
        let x = tri(b'x', b'x', b'x');
        let y = tri(b'y', b'y', b'y');
        // doc0={x,y}, doc1={x}, doc2={y}
        let idx = build(&[vec![x, y], vec![x], vec![y]]);
        // AND(x, y) → doc0 only
        let mut rpn = vec![2u8];
        rpn.extend_from_slice(&x.to_le_bytes());
        rpn.push(2);
        rpn.extend_from_slice(&y.to_le_bytes());
        rpn.push(3); // And
        rpn.extend_from_slice(&2u16.to_le_bytes());
        assert_eq!(idx.evaluate_rpn(&rpn).unwrap(), vec![0]);
    }

    #[test]
    fn posting_or_unions() {
        let x = tri(b'x', b'x', b'x');
        let y = tri(b'y', b'y', b'y');
        let idx = build(&[vec![x], vec![y], vec![tri(b'z', b'z', b'z')]]);
        // OR(x, y) → {0, 1} sorted
        let mut rpn = vec![2u8];
        rpn.extend_from_slice(&x.to_le_bytes());
        rpn.push(2);
        rpn.extend_from_slice(&y.to_le_bytes());
        rpn.push(4); // Or
        rpn.extend_from_slice(&2u16.to_le_bytes());
        assert_eq!(idx.evaluate_rpn(&rpn).unwrap(), vec![0, 1]);
    }

    #[test]
    fn posting_and_with_empty_conjunct_is_empty() {
        let x = tri(b'x', b'x', b'x');
        let missing = tri(b'z', b'z', b'z');
        let idx = build(&[vec![x], vec![x]]);
        let mut rpn = vec![2u8];
        rpn.extend_from_slice(&x.to_le_bytes());
        rpn.push(2);
        rpn.extend_from_slice(&missing.to_le_bytes()); // empty posting
        rpn.push(3);
        rpn.extend_from_slice(&2u16.to_le_bytes());
        assert_eq!(idx.evaluate_rpn(&rpn).unwrap(), Vec::<i32>::new());
    }

    #[test]
    fn posting_malformed_rpn_returns_none() {
        let idx = build(&[vec![tri(b'a', b'b', b'c')]]);
        assert!(idx.evaluate_rpn(&[2, 0x00]).is_none()); // truncated trigram
        assert!(idx.evaluate_rpn(&[3, 0x02]).is_none()); // truncated count
        assert!(idx.evaluate_rpn(&[3, 0x05, 0x00]).is_none()); // count exceeds stack
        assert!(idx.evaluate_rpn(&[99]).is_none()); // bad opcode
        assert!(idx.evaluate_rpn(&[]).is_none()); // empty
    }

    #[test]
    fn ffi_evaluate_roundtrip() {
        let x = tri(b'x', b'x', b'x');
        let y = tri(b'y', b'y', b'y');
        // doc0={x,y}, doc1={x}
        let flat: Vec<u32> = vec![x.min(y), x.max(y), x];
        let offsets: Vec<usize> = vec![0, 2, 3];
        let mut rpn = vec![2u8];
        rpn.extend_from_slice(&x.to_le_bytes());
        rpn.push(2);
        rpn.extend_from_slice(&y.to_le_bytes());
        rpn.push(3);
        rpn.extend_from_slice(&2u16.to_le_bytes());

        let mut result = QgCandidateResult {
            candidates: std::ptr::null_mut(),
            count: 0,
        };
        unsafe {
            let rc = qg_index_evaluate(
                flat.as_ptr(),
                flat.len(),
                offsets.as_ptr(),
                offsets.len(),
                rpn.as_ptr(),
                rpn.len(),
                &mut result,
            );
            assert_eq!(rc, 0);
            assert_eq!(result.count, 1);
            let slice = std::slice::from_raw_parts(result.candidates, result.count);
            assert_eq!(slice, &[0]);
            qg_index_free_candidates(&mut result);
            assert!(result.candidates.is_null());
        }
    }

    // ── content.bin parse + query ──

    fn make_content_bin(docs: &[Vec<u32>]) -> Vec<u8> {
        use sha2::{Digest, Sha256};
        let mut body = Vec::new();
        body.extend_from_slice(&(docs.len() as i32).to_le_bytes());
        for d in docs {
            body.extend_from_slice(&(d.len() as i32).to_le_bytes());
            for &t in d {
                body.extend_from_slice(&t.to_le_bytes());
            }
        }
        let digest = Sha256::digest(&body);
        body.extend_from_slice(&digest);
        body
    }

    #[test]
    fn content_bin_parse_roundtrip() {
        let x = tri(b'x', b'x', b'x');
        let y = tri(b'y', b'y', b'y');
        let docs = vec![vec![x, y], vec![x], vec![]];
        let bin = make_content_bin(&docs);
        assert_eq!(parse_content_bin(&bin).unwrap(), docs);
    }

    #[test]
    fn content_bin_bad_checksum_or_truncation_rejected() {
        let docs = vec![vec![tri(b'a', b'b', b'c')]];
        // Corrupt digest.
        let mut bad_digest = make_content_bin(&docs);
        let n = bad_digest.len();
        bad_digest[n - 1] ^= 0xFF;
        assert!(parse_content_bin(&bad_digest).is_none());
        // Corrupt body (digest no longer matches).
        let mut bad_body = make_content_bin(&docs);
        bad_body[0] ^= 0xFF;
        assert!(parse_content_bin(&bad_body).is_none());
        // Truncated below the digest length.
        assert!(parse_content_bin(&[0u8; 10]).is_none());
    }

    #[test]
    fn ffi_query_content_bin() {
        let x = tri(b'x', b'x', b'x');
        let y = tri(b'y', b'y', b'y');
        let docs = vec![vec![x.min(y), x.max(y), x.min(y)], vec![x]];
        let bin = make_content_bin(&docs);
        // AND(x, y) → doc 0 only.
        let mut rpn = vec![2u8];
        rpn.extend_from_slice(&x.to_le_bytes());
        rpn.push(2);
        rpn.extend_from_slice(&y.to_le_bytes());
        rpn.push(3);
        rpn.extend_from_slice(&2u16.to_le_bytes());

        let mut result = QgCandidateResult {
            candidates: std::ptr::null_mut(),
            count: 0,
        };
        unsafe {
            let rc = qg_index_query_content_bin(bin.as_ptr(), bin.len(), rpn.as_ptr(), rpn.len(), &mut result);
            assert_eq!(rc, 0);
            let slice = std::slice::from_raw_parts(result.candidates, result.count);
            assert_eq!(slice, &[0]);
            qg_index_free_candidates(&mut result);
        }

        // A corrupt checksum bypasses acceleration with error -5.
        let mut bad = make_content_bin(&docs);
        let n = bad.len();
        bad[n - 1] ^= 0xFF;
        let mut r2 = QgCandidateResult {
            candidates: std::ptr::null_mut(),
            count: 0,
        };
        unsafe {
            assert_eq!(
                qg_index_query_content_bin(bad.as_ptr(), bad.len(), rpn.as_ptr(), rpn.len(), &mut r2),
                -5
            );
        }
    }

    // ── binary-magic table: every signature classifies as Binary (mirrors managed BinaryDetector) ──

    #[test]
    fn every_binary_magic_signature_is_detected() {
        // Each is a real file-format magic header; SQLite is deliberately all-printable-ASCII so only the
        // magic path (not the NUL / control-ratio heuristics) can catch it.
        let signatures: &[&[u8]] = &[
            &[0x1F, 0x8B, 0x08, 0x00],                         // Gzip
            &[0x50, 0x4B, 0x03, 0x04],                         // ZIP (PK\x03\x04)
            &[0x50, 0x4B, 0x05, 0x06],                         // ZIP (empty archive)
            &[0x50, 0x4B, 0x07, 0x08],                         // ZIP (spanned)
            &[0x89, 0x50, 0x4E, 0x47],                         // PNG
            &[0xFF, 0xD8, 0xFF, 0xE0],                         // JPEG
            &[0x25, 0x50, 0x44, 0x46],                         // PDF "%PDF"
            &[0x7F, 0x45, 0x4C, 0x46],                         // ELF
            &[0x4D, 0x5A, 0x90, 0x00],                         // PE/DOS "MZ"
            &[0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C],             // 7z
            &[0x28, 0xB5, 0x2F, 0xFD],                         // Zstandard
            &[0xCE, 0xFA, 0xED, 0xFE],                         // Mach-O 32-bit LE
            &[0xCF, 0xFA, 0xED, 0xFE],                         // Mach-O 64-bit LE
            &[0xCA, 0xFE, 0xBA, 0xBE],                         // Mach-O fat / Java class
            &[0x53, 0x51, 0x4C, 0x69, 0x74, 0x65],             // SQLite "SQLite"
            &[0x42, 0x5A, 0x68, 0x39],                         // Bzip2 "BZh9"
            &[0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00],             // XZ
            &[0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00],       // RAR
        ];
        for sig in signatures {
            assert!(has_binary_magic(sig), "magic not detected: {sig:02X?}");
            assert_eq!(extract_trigrams(sig).0, Verdict::Binary, "not Binary: {sig:02X?}");
        }
        // Too-short input can never match a magic (guards the len < 4 early return).
        assert!(!has_binary_magic(&[0x50, 0x4B, 0x03]));
        assert!(!has_binary_magic(b"Rar!\x1A\x07"));
    }

    // ── UTF-8 validator edge branches (4-byte, truncated, out-of-range) ──

    #[test]
    fn utf8_four_byte_sequence_is_valid_and_indexed() {
        // U+1F600 GRINNING FACE = F0 9F 98 80 (a 4-byte sequence; exercises the extra==3 lead branch).
        let (v, t) = extract_trigrams("a\u{1F600}b".as_bytes());
        assert_eq!(v, Verdict::Indexed);
        assert!(!t.is_empty());
    }

    #[test]
    fn utf8_truncated_multibyte_is_rejected() {
        // 0xF0 announces a 4-byte sequence but only two bytes follow → truncated.
        assert_eq!(extract_trigrams(&[b'a', 0xF0, 0x9F]).0, Verdict::NotBomlessUtf8);
    }

    #[test]
    fn utf8_out_of_range_codepoint_is_rejected() {
        // F7 BF BF BF decodes to 0x1FFFFF, which is > U+10FFFF → out of range.
        assert_eq!(extract_trigrams(&[0xF7, 0xBF, 0xBF, 0xBF]).0, Verdict::NotBomlessUtf8);
    }

    // ── RPN evaluation: All/None opcodes + every malformed-stream branch ──

    #[test]
    fn rpn_all_and_none_opcodes() {
        // 3 docs, doc 0 has trigram x, doc 1 has y, doc 2 empty.
        let x = pack(b'x', b'x', b'x');
        let y = pack(b'y', b'y', b'y');
        let trigrams = vec![x, y];
        let offsets = vec![0usize, 1, 2, 2];
        let index = PostingIndex::build_from_csr(&trigrams, &offsets, 3);

        assert_eq!(index.evaluate_rpn(&[0]).unwrap(), vec![0, 1, 2]); // All
        assert_eq!(index.evaluate_rpn(&[1]).unwrap(), Vec::<i32>::new()); // None
    }

    #[test]
    fn rpn_malformed_streams_return_none() {
        let index = PostingIndex::build_from_csr(&[], &[0usize, 0], 1);
        assert!(index.evaluate_rpn(&[2, 0x00]).is_none()); // Trigram opcode with a truncated u32
        assert!(index.evaluate_rpn(&[3, 0x00]).is_none()); // And opcode with a truncated u16 count
        assert!(index.evaluate_rpn(&[3, 0x02, 0x00]).is_none()); // And wants 2 operands, stack empty
        assert!(index.evaluate_rpn(&[0xFF]).is_none()); // unknown opcode
        assert!(index.evaluate_rpn(&[0, 0]).is_none()); // two results left on the stack
        assert!(index.evaluate_rpn(&[]).is_none()); // empty stream → nothing on the stack
    }

    #[test]
    fn rpn_union_and_intersection_edges() {
        let x = pack(b'x', b'x', b'x');
        let y = pack(b'y', b'y', b'y');
        let trigrams = vec![x, y];
        let offsets = vec![0usize, 1, 2]; // doc 0 → x, doc 1 → y
        let index = PostingIndex::build_from_csr(&trigrams, &offsets, 2);

        // OR(x, y) → both docs.
        let mut or = vec![2u8];
        or.extend_from_slice(&x.to_le_bytes());
        or.push(2);
        or.extend_from_slice(&y.to_le_bytes());
        or.push(4);
        or.extend_from_slice(&2u16.to_le_bytes());
        assert_eq!(index.evaluate_rpn(&or).unwrap(), vec![0, 1]);

        // AND(x, y) → empty (no doc has both) — exercises the empty-operand short-circuit.
        let mut and = vec![2u8];
        and.extend_from_slice(&x.to_le_bytes());
        and.push(2);
        and.extend_from_slice(&y.to_le_bytes());
        and.push(3);
        and.extend_from_slice(&2u16.to_le_bytes());
        assert_eq!(index.evaluate_rpn(&and).unwrap(), Vec::<i32>::new());
    }

    // ── FFI wrappers: success paths, null-pointer guards, and every error code ──

    #[test]
    fn ffi_abi_version_is_one() {
        assert_eq!(qg_index_abi_version(), 1);
    }

    #[test]
    fn ffi_extract_trigrams_success_and_guards() {
        let data = b"the quick brown fox";
        let mut out = QgTrigramResult {
            verdict: -1,
            trigrams: std::ptr::null_mut(),
            trigram_count: 0,
        };
        unsafe {
            let rc = qg_index_extract_trigrams(data.as_ptr(), data.len(), &mut out);
            assert_eq!(rc, 0);
            assert_eq!(out.verdict, Verdict::Indexed as c_int);
            assert!(out.trigram_count > 0);
            let (_, expected) = extract_trigrams(data);
            let slice = std::slice::from_raw_parts(out.trigrams, out.trigram_count);
            assert_eq!(slice, expected.as_slice());
            qg_index_free_trigrams(&mut out);
            assert!(out.trigrams.is_null());
            // Freeing again / freeing a null result must be a no-op (null-guard branch).
            qg_index_free_trigrams(&mut out);
            qg_index_free_trigrams(std::ptr::null_mut());

            // Null out → -1.
            assert_eq!(qg_index_extract_trigrams(data.as_ptr(), data.len(), std::ptr::null_mut()), -1);

            // Null/empty input → an empty Indexed result (no trigrams, no allocation).
            let mut empty = QgTrigramResult {
                verdict: -1,
                trigrams: std::ptr::null_mut(),
                trigram_count: 0,
            };
            assert_eq!(qg_index_extract_trigrams(std::ptr::null(), 0, &mut empty), 0);
            assert_eq!(empty.verdict, Verdict::Indexed as c_int);
            assert_eq!(empty.trigram_count, 0);
            qg_index_free_trigrams(&mut empty);

            assert_eq!(qg_index_extract_trigrams(data.as_ptr(), 0, &mut empty), 0);
            assert_eq!(empty.trigram_count, 0);
        }
    }

    #[test]
    fn ffi_evaluate_success_and_error_codes() {
        let x = pack(b'x', b'x', b'x');
        let y = pack(b'y', b'y', b'y');
        let trigrams = vec![x, y];
        let offsets = vec![0usize, 1, 2]; // doc 0 → x, doc 1 → y

        // AND(x, y) → empty candidate set (success, count 0, null candidates pointer).
        let mut rpn = vec![2u8];
        rpn.extend_from_slice(&x.to_le_bytes());
        rpn.push(2);
        rpn.extend_from_slice(&y.to_le_bytes());
        rpn.push(3);
        rpn.extend_from_slice(&2u16.to_le_bytes());

        let mut out = QgCandidateResult {
            candidates: std::ptr::null_mut(),
            count: 0,
        };
        unsafe {
            let rc = qg_index_evaluate(
                trigrams.as_ptr(),
                trigrams.len(),
                offsets.as_ptr(),
                offsets.len(),
                rpn.as_ptr(),
                rpn.len(),
                &mut out,
            );
            assert_eq!(rc, 0);
            assert_eq!(out.count, 0);
            qg_index_free_candidates(&mut out);
            qg_index_free_candidates(std::ptr::null_mut()); // null-guard branch

            // Null out → -1.
            assert_eq!(
                qg_index_evaluate(trigrams.as_ptr(), trigrams.len(), offsets.as_ptr(), offsets.len(), rpn.as_ptr(), rpn.len(), std::ptr::null_mut()),
                -1
            );
            // Null / empty offsets → -2.
            assert_eq!(
                qg_index_evaluate(trigrams.as_ptr(), trigrams.len(), std::ptr::null(), 0, rpn.as_ptr(), rpn.len(), &mut out),
                -2
            );
            assert_eq!(
                qg_index_evaluate(trigrams.as_ptr(), trigrams.len(), offsets.as_ptr(), 0, rpn.as_ptr(), rpn.len(), &mut out),
                -2
            );
            let empty_offsets = [0usize, 0];
            assert_eq!(
                qg_index_evaluate(trigrams.as_ptr(), 0, empty_offsets.as_ptr(), empty_offsets.len(), rpn.as_ptr(), 0, &mut out),
                -4
            );
            // Non-monotonic offsets (out of range) → -3.
            let bad_offsets = vec![0usize, 5, 1];
            assert_eq!(
                qg_index_evaluate(trigrams.as_ptr(), trigrams.len(), bad_offsets.as_ptr(), bad_offsets.len(), rpn.as_ptr(), rpn.len(), &mut out),
                -3
            );
            let non_monotonic_offsets = vec![0usize, 1, 0];
            assert_eq!(
                qg_index_evaluate(trigrams.as_ptr(), trigrams.len(), non_monotonic_offsets.as_ptr(), non_monotonic_offsets.len(), rpn.as_ptr(), rpn.len(), &mut out),
                -3
            );
            // Malformed query → -4.
            let bad_rpn = [0xFFu8];
            assert_eq!(
                qg_index_evaluate(trigrams.as_ptr(), trigrams.len(), offsets.as_ptr(), offsets.len(), bad_rpn.as_ptr(), bad_rpn.len(), &mut out),
                -4
            );
        }
    }

    #[test]
    fn ffi_evaluate_returns_nonempty_candidates() {
        // A single-trigram query returns a non-empty candidate list (exercises the count>0 alloc path).
        let x = pack(b'x', b'x', b'x');
        let trigrams = vec![x];
        let offsets = vec![0usize, 1]; // doc 0 → x
        let mut rpn = vec![2u8];
        rpn.extend_from_slice(&x.to_le_bytes());

        let mut out = QgCandidateResult {
            candidates: std::ptr::null_mut(),
            count: 0,
        };
        unsafe {
            let rc = qg_index_evaluate(trigrams.as_ptr(), trigrams.len(), offsets.as_ptr(), offsets.len(), rpn.as_ptr(), rpn.len(), &mut out);
            assert_eq!(rc, 0);
            let slice = std::slice::from_raw_parts(out.candidates, out.count);
            assert_eq!(slice, &[0]);
            qg_index_free_candidates(&mut out);
        }
    }

    #[test]
    fn ffi_query_content_bin_null_out_is_error() {
        unsafe {
            assert_eq!(qg_index_query_content_bin(std::ptr::null(), 0, std::ptr::null(), 0, std::ptr::null_mut()), -1);
        }
    }

    // ── remaining branch coverage: sniff truncation, control-ratio classes, validator, RPN/intersect edges ──

    #[test]
    fn large_input_sniffs_only_the_first_8kb() {
        // > BINARY_SNIFF_BYTES exercises the &content[..BINARY_SNIFF_BYTES] truncation branch.
        let big = vec![b'a'; BINARY_SNIFF_BYTES + 808];
        assert_eq!(extract_trigrams(&big).0, Verdict::Indexed);
    }

    #[test]
    fn control_ratio_below_threshold_is_not_binary() {
        // A ≥512-byte buffer mixing printable ASCII (0x20-0x7F), high bytes (≥0x80, valid UTF-8), and a
        // few control bytes whose ratio stays ≤5% → is_binary returns false via the ratio-fell-through path.
        let mut buf = vec![b'a'; 580]; // printable ASCII → the (0x20..0x7F) continue
        for _ in 0..8 {
            buf.extend_from_slice("é".as_bytes()); // 0xC3 0xA9 → the b >= 0x80 continue
        }
        // Form feeds + a DEL (0x7F, the range's excluded upper bound) → suspicious++, but the ratio stays ≤5%.
        buf.extend_from_slice(&[0x0C, 0x0C, 0x0C, 0x7F]);
        assert!(!is_binary(&buf));
        assert_eq!(extract_trigrams(&buf).0, Verdict::Indexed);

        for whitespace in [b'\t', b'\n', b'\r'] {
            assert!(!is_binary(&vec![whitespace; 512]));
        }
    }

    #[test]
    fn utf8_invalid_lead_byte_rejected() {
        // 0x80 is a standalone continuation byte — an invalid lead → the else-return-false branch.
        assert_eq!(extract_trigrams(&[b'a', 0x80]).0, Verdict::NotBomlessUtf8);
    }

    #[test]
    fn rpn_and_with_zero_operands_is_empty() {
        // And with a child count of 0 → intersect_all([]) → the empty-lists early return.
        let index = PostingIndex::build_from_csr(&[], &[0usize, 0], 1);
        let mut rpn = vec![3u8];
        rpn.extend_from_slice(&0u16.to_le_bytes());
        assert_eq!(index.evaluate_rpn(&rpn).unwrap(), Vec::<i32>::new());
    }

    #[test]
    fn rpn_three_way_and_empties_accumulator_midway() {
        // AND(p, q, r) where p={0}, q={1}, r={0}: after p∩q is empty, the third operand hits the
        // acc.is_empty() break.
        let p = pack(b'a', b'a', b'a');
        let q = pack(b'b', b'b', b'b');
        let r = pack(b'c', b'c', b'c');
        let trigrams = vec![p, r, q]; // doc0 → [p, r] (sorted), doc1 → [q]
        let offsets = vec![0usize, 2, 3];
        let index = PostingIndex::build_from_csr(&trigrams, &offsets, 2);
        let mut rpn = vec![2u8];
        rpn.extend_from_slice(&p.to_le_bytes());
        rpn.push(2);
        rpn.extend_from_slice(&q.to_le_bytes());
        rpn.push(2);
        rpn.extend_from_slice(&r.to_le_bytes());
        rpn.push(3);
        rpn.extend_from_slice(&3u16.to_le_bytes());
        assert_eq!(index.evaluate_rpn(&rpn).unwrap(), Vec::<i32>::new());
    }

    #[test]
    fn rpn_intersection_hits_greater_branch() {
        // AND(t1, t2) with postings t1={1,2}, t2={0,2}: the merge steps past b's smaller 0 (Greater arm).
        let t2 = pack(b'x', b'x', b'x');
        let t1 = pack(b'y', b'y', b'y'); // x < y so t2 < t1
        let trigrams = vec![t2, t1, t2, t1]; // doc0 → [t2], doc1 → [t1], doc2 → [t2, t1]
        let offsets = vec![0usize, 1, 2, 4];
        let index = PostingIndex::build_from_csr(&trigrams, &offsets, 3);
        let mut rpn = vec![2u8];
        rpn.extend_from_slice(&t1.to_le_bytes());
        rpn.push(2);
        rpn.extend_from_slice(&t2.to_le_bytes());
        rpn.push(3);
        rpn.extend_from_slice(&2u16.to_le_bytes());
        assert_eq!(index.evaluate_rpn(&rpn).unwrap(), vec![2]);
        assert!(intersect_two(&[1, 2], &[0]).is_empty());
    }

    #[test]
    fn ffi_evaluate_null_trigrams_and_null_rpn_slices() {
        // Null trigram / rpn pointers with 0 length must map to empty slices, not a segfault.
        let offsets = vec![0usize, 0]; // 1 empty document
        let mut out = QgCandidateResult { candidates: std::ptr::null_mut(), count: 0 };
        unsafe {
            // Null trigrams + None-opcode query → success with an empty result.
            let none_rpn = [1u8];
            let rc = qg_index_evaluate(std::ptr::null(), 0, offsets.as_ptr(), offsets.len(), none_rpn.as_ptr(), none_rpn.len(), &mut out);
            assert_eq!(rc, 0);
            assert_eq!(out.count, 0);
            qg_index_free_candidates(&mut out);

            // Null rpn (empty stream) → nothing on the stack → malformed (-4).
            assert_eq!(
                qg_index_evaluate(std::ptr::null(), 0, offsets.as_ptr(), offsets.len(), std::ptr::null(), 0, &mut out),
                -4
            );
        }
    }

    #[test]
    fn content_bin_malformed_bodies_are_rejected() {
        fn checksummed(body: Vec<u8>) -> Vec<u8> {
            use sha2::{Digest, Sha256};
            let digest = Sha256::digest(&body);
            let mut out = body;
            out.extend_from_slice(&digest);
            out
        }

        // Negative doc_count.
        assert!(parse_content_bin(&checksummed((-1i32).to_le_bytes().to_vec())).is_none());
        // Negative trigram count for a document.
        let mut neg_n = 1i32.to_le_bytes().to_vec();
        neg_n.extend_from_slice(&(-1i32).to_le_bytes());
        assert!(parse_content_bin(&checksummed(neg_n)).is_none());
        // Missing trigram count for the declared document.
        assert!(parse_content_bin(&checksummed(1i32.to_le_bytes().to_vec())).is_none());
        // Truncated doc_count (fewer than 4 body bytes).
        assert!(parse_content_bin(&checksummed(vec![0x00, 0x00])).is_none());
        // Truncated trigram (doc_count=1, n=1, but only 2 of the 4 u32 bytes present).
        let mut trunc = 1i32.to_le_bytes().to_vec();
        trunc.extend_from_slice(&1i32.to_le_bytes());
        trunc.extend_from_slice(&[0xAB, 0xCD]);
        assert!(parse_content_bin(&checksummed(trunc)).is_none());
        // Trailing garbage after a well-formed (empty) document set.
        let mut trailing = 0i32.to_le_bytes().to_vec();
        trailing.extend_from_slice(&[0xFF, 0xFF]);
        assert!(parse_content_bin(&checksummed(trailing)).is_none());
    }

    #[test]
    fn ffi_query_content_bin_empty_slice_branches_and_empty_result() {
        let x = tri(b'x', b'x', b'x');
        let y = tri(b'y', b'y', b'y');
        // doc 0 → x only, doc 1 → y only. AND(x, y) selects nothing → empty candidate result.
        let docs = vec![vec![x], vec![y]];
        let bin = make_content_bin(&docs);

        let mut rpn = vec![2u8];
        rpn.extend_from_slice(&x.to_le_bytes());
        rpn.push(2);
        rpn.extend_from_slice(&y.to_le_bytes());
        rpn.push(3);
        rpn.extend_from_slice(&2u16.to_le_bytes());

        let mut out = QgCandidateResult { candidates: std::ptr::null_mut(), count: 0 };
        unsafe {
            // Success with an empty candidate set → the count==0 null-pointer branch.
            let rc = qg_index_query_content_bin(bin.as_ptr(), bin.len(), rpn.as_ptr(), rpn.len(), &mut out);
            assert_eq!(rc, 0);
            assert_eq!(out.count, 0);
            assert!(out.candidates.is_null());
            qg_index_free_candidates(&mut out);

            // Null content_bin (empty slice) → parse fails → -5.
            assert_eq!(
                qg_index_query_content_bin(std::ptr::null(), 0, rpn.as_ptr(), rpn.len(), &mut out),
                -5
            );
            assert_eq!(qg_index_query_content_bin(bin.as_ptr(), 0, rpn.as_ptr(), rpn.len(), &mut out), -5);
            // Null query_rpn (empty slice) over a valid content.bin → malformed query → -4.
            assert_eq!(
                qg_index_query_content_bin(bin.as_ptr(), bin.len(), std::ptr::null(), 0, &mut out),
                -4
            );
            assert_eq!(qg_index_query_content_bin(bin.as_ptr(), bin.len(), rpn.as_ptr(), 0, &mut out), -4);
        }
    }
}
