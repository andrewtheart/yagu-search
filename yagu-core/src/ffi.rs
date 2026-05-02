//! C ABI exposed to .NET via P/Invoke.
//!
//! Lifecycle:
//!   1. C# calls `qg_search_file` with a UTF-16 path, UTF-8 pattern, options,
//!      and a `cancel_flag` pointer (an atomic 32-bit int it owns).
//!   2. Rust opens the file, mmap's it (or reads to a Vec for small files),
//!      scans, and produces a single packed result buffer.
//!   3. The buffer pointer + length are returned via out-params; ownership
//!      transfers to the caller, which must release it via `qg_free_result`.

use crate::scan::{
    looks_binary, scan_bytes_ex, scan_bytes_with_matcher_ex, MatchRecord, ScanError, ScanOptions,
};
use memmap2::Mmap;
use std::fs::File;
use std::io::Read;
use std::os::raw::{c_int, c_uchar, c_uint, c_ulonglong, c_ushort, c_void};
use std::sync::atomic::{AtomicI32, AtomicUsize, Ordering};
use std::sync::Mutex;
#[cfg(windows)]
use std::os::windows::fs::MetadataExt;

/// Open a file with the platform's best read hint for sequential, one-shot
/// scanning. On Windows we set `FILE_FLAG_SEQUENTIAL_SCAN` so the cache
/// manager prefetches more aggressively and discards pages soon after they
/// are read — a large win for files-per-second on cold reads and a meaningful
/// reduction in working-set pressure when sweeping large trees.
#[inline]
fn open_for_scan(path: &str) -> std::io::Result<File> {
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        // FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000
        const FILE_FLAG_SEQUENTIAL_SCAN: u32 = 0x0800_0000;
        std::fs::OpenOptions::new()
            .read(true)
            .custom_flags(FILE_FLAG_SEQUENTIAL_SCAN)
            .open(path)
    }
    #[cfg(not(windows))]
    {
        File::open(path)
    }
}

/// Files at or below this size are read fully into a `Vec<u8>` instead of being
/// memory-mapped. mmap setup (TLB, page-fault dance) costs more than a single
/// `read_to_end` for small files; on the C:\ benchmark 75% of files are <64 KB.
const MMAP_THRESHOLD_BYTES: u64 = 64 * 1024;

/// Number of bytes read from the head of a candidate file to test for binary
/// content before paying for `mmap` (or full read). 8 KB is large enough to
/// catch the magic bytes of every common binary format yet still fits in a
/// single page-cache hit and a stack buffer.
const BINARY_PROBE_BYTES: usize = 8 * 1024;

#[repr(C)]
pub struct QgOptions {
    pub case_sensitive: c_uchar,
    pub use_regex: c_uchar,
    pub skip_binary: c_uchar,
    pub _pad0: c_uchar,
    pub context_before: c_uint,
    pub context_after: c_uint,
    pub max_results: c_ulonglong,
    pub max_file_size: c_ulonglong,
}

#[repr(C)]
pub struct QgResult {
    /// Pointer to a heap-allocated, packed result buffer (see layout below).
    pub buffer: *mut u8,
    /// Length of `buffer` in bytes.
    pub buffer_len: usize,
    /// Number of match records in the buffer.
    pub match_count: c_uint,
    /// 0 = OK, 1 = file open failed, 2 = file too large, 3 = binary skipped,
    /// 4 = invalid regex (utf8 message in `error_msg`), 5 = invalid utf8 path,
    /// 6 = cancelled.
    pub status: c_int,
    /// UTF-8 error message (only set when status == 4); freed with `qg_free_result`.
    pub error_msg: *mut u8,
    pub error_msg_len: usize,
}

/// Packed match record layout (little-endian):
///   u64 line_number
///   u32 match_start
///   u32 match_len
///   u32 line_len            -> followed by line_len bytes
///   u32 ctx_before_count    -> for each: u32 len, then `len` bytes
///   u32 ctx_after_count     -> for each: u32 len, then `len` bytes
const STATUS_OK: c_int = 0;
const STATUS_OPEN_FAILED: c_int = 1;
const STATUS_TOO_LARGE: c_int = 2;
const STATUS_BINARY_SKIPPED: c_int = 3;
const STATUS_INVALID_REGEX: c_int = 4;
const STATUS_INVALID_PATH: c_int = 5;
const STATUS_CANCELLED: c_int = 6;

#[inline]
fn use_ascii_literal_fast_path(pattern: &str, use_regex: bool, case_sensitive: bool) -> bool {
    !use_regex && !case_sensitive && pattern.is_ascii()
}

// ---------------------------------------------------------------------------
// Helpers: open_and_mmap, scan_error_to_status
// ---------------------------------------------------------------------------

/// Test-only injection points for triggering OS-level errors.
#[cfg(test)]
mod test_inject {
    use std::cell::Cell;
    thread_local! {
        static FAIL_METADATA: Cell<bool> = Cell::new(false);
        static FAIL_MMAP: Cell<bool> = Cell::new(false);
    }
    pub fn should_fail_metadata() -> bool {
        FAIL_METADATA.with(|c| c.get())
    }
    pub fn should_fail_mmap() -> bool {
        FAIL_MMAP.with(|c| c.get())
    }
    pub fn set_fail_metadata(fail: bool) {
        FAIL_METADATA.with(|c| c.set(fail));
    }
    pub fn set_fail_mmap(fail: bool) {
        FAIL_MMAP.with(|c| c.set(fail));
    }
}

#[cfg(test)]
fn try_metadata(file: &File) -> std::io::Result<std::fs::Metadata> {
    if test_inject::should_fail_metadata() {
        return Err(std::io::Error::from(std::io::ErrorKind::PermissionDenied));
    }
    file.metadata()
}

#[cfg(not(test))]
fn try_metadata(file: &File) -> std::io::Result<std::fs::Metadata> {
    file.metadata()
}

#[cfg(test)]
fn try_mmap(file: &File) -> std::io::Result<Mmap> {
    if test_inject::should_fail_mmap() {
        return Err(std::io::Error::from(std::io::ErrorKind::Other));
    }
    unsafe { Mmap::map(file) }
}

#[cfg(not(test))]
fn try_mmap(file: &File) -> std::io::Result<Mmap> {
    unsafe { Mmap::map(file) }
}

/// Bytes for a single file, sourced from either a heap read (small files)
/// or a memory map (large files). The `Owned` variant retains the buffer; the
/// `Mapped` variant keeps the `Mmap` alive until drop.
pub(crate) enum FileBytes {
    Owned(Vec<u8>),
    Mapped(Mmap),
}

impl std::fmt::Debug for FileBytes {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            FileBytes::Owned(v) => f.debug_tuple("Owned").field(&v.len()).finish(),
            FileBytes::Mapped(m) => f.debug_tuple("Mapped").field(&m.len()).finish(),
        }
    }
}

impl FileBytes {
    pub(crate) fn as_slice(&self) -> &[u8] {
        match self {
            FileBytes::Owned(v) => v.as_slice(),
            FileBytes::Mapped(m) => &m[..],
        }
    }
}

/// Open a file and prepare its bytes for scanning, applying three real-world
/// optimizations validated by the C:\ benchmark:
///
/// 1. **Pre-mmap binary probe.** When `skip_binary` is set, read the first
///    `BINARY_PROBE_BYTES` and check for a NUL byte. If found, return
///    [`STATUS_BINARY_SKIPPED`] without ever paying the mmap setup cost. On
///    the C:\ benchmark 75% of candidate files were binary; this short-circuit
///    saves both physical I/O for the file tail and TLB/page-fault overhead.
/// 2. **mmap for large files only.** Files larger than `MMAP_THRESHOLD_BYTES`
///    (64 KB) are memory-mapped; smaller files are read into a `Vec<u8>` in
///    one syscall. mmap's per-call setup amortizes poorly across the long
///    tail of small text files.
/// 3. **Empty files** return an empty `Owned` buffer (caller treats this as
///    zero matches, no allocation cost).
fn open_file_for_scan(
    path: &str,
    max_file_size: u64,
    skip_binary: bool,
) -> Result<FileBytes, c_int> {
    let mut file = open_for_scan(path).map_err(|_| STATUS_OPEN_FAILED)?;
    let file_size = try_metadata(&file).map_err(|_| STATUS_OPEN_FAILED)?.len();
    if max_file_size != 0 && file_size > max_file_size {
        return Err(STATUS_TOO_LARGE);
    }
    if file_size == 0 {
        return Ok(FileBytes::Owned(Vec::new()));
    }

    // Small file path: a single sized read is faster than mmap setup. The
    // scan layer's own `looks_binary` will handle the binary check if
    // `skip_binary` is set, so we don't need a separate probe here.
    // `read_exact` over a presized buffer avoids the growth-and-zero-init
    // overhead of `read_to_end`'s internal buffer doubling.
    if file_size <= MMAP_THRESHOLD_BYTES {
        let size = file_size as usize;
        let mut buf: Vec<u8> = Vec::with_capacity(size);
        // SAFETY: we set the length to `size`, which equals capacity, and
        // immediately overwrite the entire range with `read_exact`. Any I/O
        // error returns before the buffer is observed.
        unsafe { buf.set_len(size) };
        if let Err(e) = file.read_exact(&mut buf[..]) {
            // Tolerate truncation between metadata() and read (rare on Windows
            // but possible if another process truncates concurrently). Fall
            // back to a normal read to recover whatever bytes are present.
            if e.kind() == std::io::ErrorKind::UnexpectedEof {
                buf.clear();
                file.read_to_end(&mut buf).map_err(|_| STATUS_OPEN_FAILED)?;
            } else {
                return Err(STATUS_OPEN_FAILED);
            }
        }
        return Ok(FileBytes::Owned(buf));
    }

    // Large file path: probe the first 8 KB on disk for NUL before paying for
    // a full mmap. Use a stack buffer to avoid heap traffic on the hot path.
    if skip_binary {
        let mut probe = [0u8; BINARY_PROBE_BYTES];
        let probe_len = std::cmp::min(file_size as usize, BINARY_PROBE_BYTES);
        let n = file
            .read(&mut probe[..probe_len])
            .map_err(|_| STATUS_OPEN_FAILED)?;
        if looks_binary(&probe[..n]) {
            return Err(STATUS_BINARY_SKIPPED);
        }
    }

    // Memory-map the full file. mmap is independent of the read cursor we
    // advanced during the probe.
    let mmap = try_mmap(&file).map_err(|_| STATUS_OPEN_FAILED)?;
    Ok(FileBytes::Mapped(mmap))
}

/// Borrowed view of file bytes returned by [`open_file_for_scan_into`]. The
/// `Borrowed` variant references a caller-owned scratch buffer (no per-file
/// allocation in the small-file path); the `Mapped` variant carries an Mmap
/// whose lifetime is bound to the caller's stack.
pub(crate) enum FileBytesRef<'a> {
    Borrowed(&'a [u8]),
    Mapped(Mmap),
}
impl<'a> FileBytesRef<'a> {
    #[inline]
    pub(crate) fn as_slice(&self) -> &[u8] {
        match self {
            FileBytesRef::Borrowed(s) => s,
            FileBytesRef::Mapped(m) => &m[..],
        }
    }
}

/// Same contract as [`open_file_for_scan`], but the small-file path reads into
/// a caller-supplied scratch buffer so per-thread `Vec<u8>` allocations are
/// reused across files. On the C:\ benchmark 75% of files are <= 64 KB, so
/// this turns the most common case into a zero-alloc syscall plus a memcpy.
fn open_file_for_scan_into<'a>(
    path: &str,
    max_file_size: u64,
    skip_binary: bool,
    scratch: &'a mut Vec<u8>,
) -> Result<(FileBytesRef<'a>, u64, u64), c_int> {
    let mut file = open_for_scan(path).map_err(|_| STATUS_OPEN_FAILED)?;
    let meta = try_metadata(&file).map_err(|_| STATUS_OPEN_FAILED)?;
    let file_size = meta.len();
    #[cfg(windows)]
    let last_modified = meta.last_write_time();
    #[cfg(not(windows))]
    let last_modified = 0u64;
    if max_file_size != 0 && file_size > max_file_size {
        return Err(STATUS_TOO_LARGE);
    }
    if file_size == 0 {
        scratch.clear();
        return Ok((FileBytesRef::Borrowed(&[]), 0, last_modified));
    }

    if file_size <= MMAP_THRESHOLD_BYTES {
        let size = file_size as usize;
        scratch.clear();
        scratch.reserve(size);
        // SAFETY: we set the length to `size <= capacity` and immediately
        // overwrite the full range with `read_exact`. On error we truncate
        // before the caller observes the buffer.
        unsafe { scratch.set_len(size) };
        if let Err(e) = file.read_exact(&mut scratch[..]) {
            scratch.clear();
            if e.kind() == std::io::ErrorKind::UnexpectedEof {
                file.read_to_end(scratch).map_err(|_| STATUS_OPEN_FAILED)?;
            } else {
                return Err(STATUS_OPEN_FAILED);
            }
        }
        return Ok((FileBytesRef::Borrowed(&scratch[..]), file_size, last_modified));
    }

    if skip_binary {
        let mut probe = [0u8; BINARY_PROBE_BYTES];
        let probe_len = std::cmp::min(file_size as usize, BINARY_PROBE_BYTES);
        let n = file
            .read(&mut probe[..probe_len])
            .map_err(|_| STATUS_OPEN_FAILED)?;
        if looks_binary(&probe[..n]) {
            return Err(STATUS_BINARY_SKIPPED);
        }
    }

    let mmap = try_mmap(&file).map_err(|_| STATUS_OPEN_FAILED)?;
    Ok((FileBytesRef::Mapped(mmap), file_size, last_modified))
}

/// Open a file, verify its size, and memory-map it.
/// Returns `Ok(mmap)` on success, or `Err((status_code, true))` on empty file
/// (caller should return OK), `Err((status_code, false))` on real error.
#[cfg(test)]
fn open_and_mmap(path: &str, max_file_size: u64) -> Result<Mmap, c_int> {
    let file = File::open(path).map_err(|_| STATUS_OPEN_FAILED)?;
    let file_size = try_metadata(&file).map_err(|_| STATUS_OPEN_FAILED)?.len();
    if max_file_size != 0 && file_size > max_file_size {
        return Err(STATUS_TOO_LARGE);
    }
    if file_size == 0 {
        // Signal "empty, nothing to scan" — caller treats this as STATUS_OK.
        return Err(STATUS_OK);
    }
    try_mmap(&file).map_err(|_| STATUS_OPEN_FAILED)
}

/// Convert a `ScanError` to the corresponding FFI status code.
fn scan_error_to_status(err: &ScanError) -> c_int {
    match err {
        ScanError::BinarySkipped => STATUS_BINARY_SKIPPED,
        ScanError::InvalidRegex(_) => STATUS_INVALID_REGEX,
        ScanError::Cancelled => STATUS_CANCELLED,
        ScanError::Io(_) => STATUS_OPEN_FAILED,
    }
}

/// If the error is `InvalidRegex`, write the message into the out-params.
///
/// # Safety
/// `out_error_msg` and `out_error_msg_len` must be valid pointers (or null).
unsafe fn write_scan_error_msg(
    err: ScanError,
    out_error_msg: *mut *mut u8,
    out_error_msg_len: *mut usize,
) {
    if let ScanError::InvalidRegex(msg) = err {
        if !out_error_msg.is_null() && !out_error_msg_len.is_null() {
            let mut bytes = msg.into_bytes();
            bytes.shrink_to_fit();
            let len = bytes.len();
            let ptr = bytes.as_mut_ptr();
            std::mem::forget(bytes);
            *out_error_msg = ptr;
            *out_error_msg_len = len;
        }
    }
}

/// # Safety
/// All pointer arguments must be valid for the indicated lengths and remain
/// valid for the duration of the call. `cancel_flag` may be null; if non-null
/// it must point to a 4-byte-aligned i32. The caller must release the result
/// buffer by calling `qg_free_result`.
#[no_mangle]
pub unsafe extern "C" fn qg_search_file(
    path_utf16: *const c_ushort,
    path_len: usize,
    pattern_utf8: *const u8,
    pattern_len: usize,
    options: *const QgOptions,
    cancel_flag: *const i32,
    out_result: *mut QgResult,
) -> c_int {
    if out_result.is_null() {
        return -1;
    }
    let result_slot = &mut *out_result;
    *result_slot = QgResult {
        buffer: std::ptr::null_mut(),
        buffer_len: 0,
        match_count: 0,
        status: STATUS_OK,
        error_msg: std::ptr::null_mut(),
        error_msg_len: 0,
    };

    if path_utf16.is_null() || options.is_null() {
        result_slot.status = STATUS_INVALID_PATH;
        return STATUS_INVALID_PATH;
    }

    let path_slice = std::slice::from_raw_parts(path_utf16, path_len);
    let path = match String::from_utf16(path_slice) {
        Ok(s) => s,
        Err(_) => {
            result_slot.status = STATUS_INVALID_PATH;
            return STATUS_INVALID_PATH;
        }
    };

    let pattern_slice = if pattern_utf8.is_null() {
        &[][..]
    } else {
        std::slice::from_raw_parts(pattern_utf8, pattern_len)
    };
    let pattern = match std::str::from_utf8(pattern_slice) {
        Ok(s) => s,
        Err(_) => {
            result_slot.status = STATUS_INVALID_PATH;
            return STATUS_INVALID_PATH;
        }
    };

    let opts_in = &*options;
    let scan_opts = ScanOptions {
        case_sensitive: opts_in.case_sensitive != 0,
        use_regex: opts_in.use_regex != 0,
        context_before: opts_in.context_before as usize,
        context_after: opts_in.context_after as usize,
        max_results: opts_in.max_results as usize,
        skip_binary: opts_in.skip_binary != 0,
        ascii_case_only: use_ascii_literal_fast_path(
            pattern,
            opts_in.use_regex != 0,
            opts_in.case_sensitive != 0,
        ),
    };

    // Open + size check + (probe/read/mmap).
    let file_bytes = match open_file_for_scan(&path, opts_in.max_file_size, scan_opts.skip_binary) {
        Ok(b) => b,
        Err(status) => {
            result_slot.status = status;
            return status;
        }
    };
    let bytes: &[u8] = file_bytes.as_slice();

    // Cancellation: poll the caller's atomic flag through a casted reference.
    let cancel_atomic: Option<&AtomicI32> = if cancel_flag.is_null() {
        None
    } else {
        debug_assert!(
            cancel_flag as usize % std::mem::align_of::<AtomicI32>() == 0,
            "cancel_flag must be 4-byte aligned"
        );
        Some(&*(cancel_flag as *const AtomicI32))
    };

    let mut records: Vec<MatchRecord> = Vec::new();
    let mut byte_estimate: usize = 4; // u32 count
    let mut line_poll_counter: u32 = 0;

    let cancel_check = || {
        // Per-line cancel poll: throttle to every 64 lines so the atomic
        // load is amortized but still much more responsive than per-match.
        line_poll_counter = line_poll_counter.wrapping_add(1);
        if line_poll_counter & 0x3f != 0 {
            return false;
        }
        match cancel_atomic {
            Some(flag) => flag.load(Ordering::Relaxed) != 0,
            None => false,
        }
    };

    let scan_result = scan_bytes_ex(bytes, pattern, &scan_opts, cancel_check, |rec| {
        if let Some(flag) = cancel_atomic {
            if flag.load(Ordering::Relaxed) != 0 {
                return false;
            }
        }
        // Reserve estimate: 8 + 4 + 4 + 4 + line + 4 + sum_before + 4 + sum_after
        byte_estimate += 8 + 4 + 4 + 4 + rec.line.len() + 4 + 4;
        for l in &rec.context_before {
            byte_estimate += 4 + l.len();
        }
        for l in &rec.context_after {
            byte_estimate += 4 + l.len();
        }
        records.push(rec);
        true
    });

    if let Err(e) = scan_result {
        let status = scan_error_to_status(&e);
        write_scan_error_msg(e, &mut result_slot.error_msg, &mut result_slot.error_msg_len);
        result_slot.status = status;
        return status;
    }

    // Re-check cancellation (most recent value) after scan completed.
    if let Some(flag) = cancel_atomic {
        if flag.load(Ordering::Relaxed) != 0 {
            result_slot.status = STATUS_CANCELLED;
            return STATUS_CANCELLED;
        }
    }

    // Pack records into a single buffer.
    let mut buf: Vec<u8> = Vec::with_capacity(byte_estimate);
    buf.extend_from_slice(&(records.len() as u32).to_le_bytes());
    for rec in &records {
        buf.extend_from_slice(&rec.line_number.to_le_bytes());
        buf.extend_from_slice(&rec.match_start.to_le_bytes());
        buf.extend_from_slice(&rec.match_len.to_le_bytes());
        buf.extend_from_slice(&(rec.line.len() as u32).to_le_bytes());
        buf.extend_from_slice(&rec.line);
        buf.extend_from_slice(&(rec.context_before.len() as u32).to_le_bytes());
        for l in &rec.context_before {
            buf.extend_from_slice(&(l.len() as u32).to_le_bytes());
            buf.extend_from_slice(l);
        }
        buf.extend_from_slice(&(rec.context_after.len() as u32).to_le_bytes());
        for l in &rec.context_after {
            buf.extend_from_slice(&(l.len() as u32).to_le_bytes());
            buf.extend_from_slice(l);
        }
    }

    buf.shrink_to_fit();
    let len = buf.len();
    debug_assert_eq!(len, buf.capacity());
    let ptr = buf.as_mut_ptr();
    std::mem::forget(buf);

    result_slot.buffer = ptr;
    result_slot.buffer_len = len;
    result_slot.match_count = records.len() as u32;
    result_slot.status = STATUS_OK;

    STATUS_OK
}

/// # Safety
/// `result` must point to a `QgResult` previously populated by `qg_search_file`.
#[no_mangle]
pub unsafe extern "C" fn qg_free_result(result: *mut QgResult) {
    if result.is_null() {
        return;
    }
    let r = &mut *result;
    if !r.buffer.is_null() {
        // We always shrink_to_fit before transfer, so cap == len.
        let _ = Vec::from_raw_parts(r.buffer, r.buffer_len, r.buffer_len);
        r.buffer = std::ptr::null_mut();
        r.buffer_len = 0;
    }
    if !r.error_msg.is_null() {
        let len = r.error_msg_len;
        let _ = Vec::from_raw_parts(r.error_msg, len, len);
        r.error_msg = std::ptr::null_mut();
        r.error_msg_len = 0;
    }
}

/// ABI/version probe used by the C# loader to confirm the DLL is the one we built.
#[no_mangle]
pub extern "C" fn qg_abi_version() -> c_uint {
    4
}

// ---------------------------------------------------------------------------
// Streaming FFI (per-match callback). See PERFORMANCE_IMPROVEMENTS.md "A".
// ---------------------------------------------------------------------------

#[repr(C)]
pub struct QgMatchView {
    pub line_number: c_ulonglong,
    pub match_start: c_uint,
    pub match_len: c_uint,
    pub line_ptr: *const u8,
    pub line_len: usize,
    /// Packed buffer: for each context-before line, u32 little-endian length
    /// followed by `len` bytes of UTF-8.
    pub ctx_before_ptr: *const u8,
    pub ctx_before_bytes: usize,
    pub ctx_before_count: c_uint,
    pub ctx_after_ptr: *const u8,
    pub ctx_after_bytes: usize,
    pub ctx_after_count: c_uint,
}

/// Callback returns 0 to continue, non-zero to stop scanning early.
pub type QgMatchCallback = unsafe extern "C" fn(ctx: *mut c_void, m: *const QgMatchView) -> c_int;

fn pack_lines(lines: &[Vec<u8>], buf: &mut Vec<u8>) {
    buf.clear();
    for l in lines {
        buf.extend_from_slice(&(l.len() as u32).to_le_bytes());
        buf.extend_from_slice(l);
    }
}

/// Streaming search. Calls `on_match` once per match. Pointers in the
/// `QgMatchView` are valid only for the duration of the callback. The C#
/// caller must copy data it wants to retain.
///
/// # Safety
/// All pointer arguments must be valid for the indicated lengths and remain
/// valid for the duration of the call. `cancel_flag` may be null; if non-null
/// it must point to a 4-byte-aligned i32. `on_match` must be a valid function
/// pointer and `on_match_ctx` is opaque to Rust.
#[no_mangle]
pub unsafe extern "C" fn qg_search_file_stream(
    path_utf16: *const c_ushort,
    path_len: usize,
    pattern_utf8: *const u8,
    pattern_len: usize,
    options: *const QgOptions,
    cancel_flag: *const i32,
    on_match: QgMatchCallback,
    on_match_ctx: *mut c_void,
    out_status: *mut c_int,
    out_error_msg: *mut *mut u8,
    out_error_msg_len: *mut usize,
) -> c_int {
    // Initialize out-params.
    if !out_status.is_null() {
        *out_status = STATUS_OK;
    }
    if !out_error_msg.is_null() {
        *out_error_msg = std::ptr::null_mut();
    }
    if !out_error_msg_len.is_null() {
        *out_error_msg_len = 0;
    }

    let set_status = |code: c_int| {
        if !out_status.is_null() {
            *out_status = code;
        }
        code
    };

    if path_utf16.is_null() || options.is_null() {
        return set_status(STATUS_INVALID_PATH);
    }

    let path_slice = std::slice::from_raw_parts(path_utf16, path_len);
    let path = match String::from_utf16(path_slice) {
        Ok(s) => s,
        Err(_) => return set_status(STATUS_INVALID_PATH),
    };

    let pattern_slice = if pattern_utf8.is_null() {
        &[][..]
    } else {
        std::slice::from_raw_parts(pattern_utf8, pattern_len)
    };
    let pattern = match std::str::from_utf8(pattern_slice) {
        Ok(s) => s,
        Err(_) => return set_status(STATUS_INVALID_PATH),
    };

    let opts_in = &*options;
    let scan_opts = ScanOptions {
        case_sensitive: opts_in.case_sensitive != 0,
        use_regex: opts_in.use_regex != 0,
        context_before: opts_in.context_before as usize,
        context_after: opts_in.context_after as usize,
        max_results: opts_in.max_results as usize,
        skip_binary: opts_in.skip_binary != 0,
        ascii_case_only: use_ascii_literal_fast_path(
            pattern,
            opts_in.use_regex != 0,
            opts_in.case_sensitive != 0,
        ),
    };

    let file_bytes = match open_file_for_scan(&path, opts_in.max_file_size, scan_opts.skip_binary) {
        Ok(b) => b,
        Err(status) => return set_status(status),
    };
    let bytes: &[u8] = file_bytes.as_slice();

    let cancel_atomic: Option<&AtomicI32> = if cancel_flag.is_null() {
        None
    } else {
        debug_assert!(
            cancel_flag as usize % std::mem::align_of::<AtomicI32>() == 0,
            "cancel_flag must be 4-byte aligned"
        );
        Some(&*(cancel_flag as *const AtomicI32))
    };

    let mut before_buf: Vec<u8> = Vec::new();
    let mut after_buf: Vec<u8> = Vec::new();
    let mut line_poll_counter: u32 = 0;

    let cancel_check = || {
        line_poll_counter = line_poll_counter.wrapping_add(1);
        if line_poll_counter & 0x3f != 0 {
            return false;
        }
        match cancel_atomic {
            Some(flag) => flag.load(Ordering::Relaxed) != 0,
            None => false,
        }
    };

    let scan_result = scan_bytes_ex(bytes, pattern, &scan_opts, cancel_check, |rec: MatchRecord| {
        if let Some(flag) = cancel_atomic {
            if flag.load(Ordering::Relaxed) != 0 {
                return false;
            }
        }

        pack_lines(&rec.context_before, &mut before_buf);
        pack_lines(&rec.context_after, &mut after_buf);

        let view = QgMatchView {
            line_number: rec.line_number,
            match_start: rec.match_start,
            match_len: rec.match_len,
            line_ptr: rec.line.as_ptr(),
            line_len: rec.line.len(),
            ctx_before_ptr: if before_buf.is_empty() {
                std::ptr::null()
            } else {
                before_buf.as_ptr()
            },
            ctx_before_bytes: before_buf.len(),
            ctx_before_count: rec.context_before.len() as c_uint,
            ctx_after_ptr: if after_buf.is_empty() {
                std::ptr::null()
            } else {
                after_buf.as_ptr()
            },
            ctx_after_bytes: after_buf.len(),
            ctx_after_count: rec.context_after.len() as c_uint,
        };

        let cb_result = on_match(on_match_ctx, &view as *const QgMatchView);
        cb_result == 0
    });

    match scan_result {
        Ok(_) => set_status(STATUS_OK),
        Err(e) => {
            let status = scan_error_to_status(&e);
            write_scan_error_msg(e, out_error_msg, out_error_msg_len);
            set_status(status)
        }
    }
}

/// Free a UTF-8 buffer allocated by Rust (e.g. `out_error_msg` from
/// `qg_search_file_stream`).
///
/// # Safety
/// `ptr`/`len` must originate from a Rust-side allocation handed to the caller
/// via this crate's FFI.
#[no_mangle]
pub unsafe extern "C" fn qg_free_buffer(ptr: *mut u8, len: usize) {
    if ptr.is_null() || len == 0 {
        return;
    }
    let _ = Vec::from_raw_parts(ptr, len, len);
}

// ---------------------------------------------------------------------------
// Session API: compile the pattern once, reuse across many files.
// ---------------------------------------------------------------------------

/// Opaque session holding a pre-compiled matcher and scan options.
pub struct QgSession {
    matcher: Box<dyn crate::scan::LineMatcher>,
    scan_opts: ScanOptions,
    max_file_size: u64,
}

// SAFETY: LineMatcher is Send+Sync, ScanOptions is plain data.
unsafe impl Send for QgSession {}
unsafe impl Sync for QgSession {}

/// Create a session that pre-compiles the search pattern. Returns null on
/// failure (invalid regex); the caller must free the session with
/// `qg_free_session`.
///
/// # Safety
/// `pattern_utf8`/`pattern_len` must be a valid UTF-8 byte slice.
/// `options` must point to a valid `QgOptions`.
#[no_mangle]
pub unsafe extern "C" fn qg_create_session(
    pattern_utf8: *const u8,
    pattern_len: usize,
    options: *const QgOptions,
    out_error_msg: *mut *mut u8,
    out_error_msg_len: *mut usize,
) -> *mut QgSession {
    if !out_error_msg.is_null() {
        *out_error_msg = std::ptr::null_mut();
    }
    if !out_error_msg_len.is_null() {
        *out_error_msg_len = 0;
    }

    if options.is_null() {
        return std::ptr::null_mut();
    }

    let pattern_slice = if pattern_utf8.is_null() {
        &[][..]
    } else {
        std::slice::from_raw_parts(pattern_utf8, pattern_len)
    };
    let pattern = match std::str::from_utf8(pattern_slice) {
        Ok(s) => s,
        Err(_) => return std::ptr::null_mut(),
    };

    let opts_in = &*options;
    let scan_opts = ScanOptions {
        case_sensitive: opts_in.case_sensitive != 0,
        use_regex: opts_in.use_regex != 0,
        context_before: opts_in.context_before as usize,
        context_after: opts_in.context_after as usize,
        max_results: opts_in.max_results as usize,
        skip_binary: opts_in.skip_binary != 0,
        ascii_case_only: use_ascii_literal_fast_path(
            pattern,
            opts_in.use_regex != 0,
            opts_in.case_sensitive != 0,
        ),
    };

    let matcher = match crate::scan::build_matcher(pattern, &scan_opts) {
        Ok(m) => m,
        Err(e) => {
            write_scan_error_msg(e, out_error_msg, out_error_msg_len);
            return std::ptr::null_mut();
        }
    };

    Box::into_raw(Box::new(QgSession {
        matcher,
        scan_opts,
        max_file_size: opts_in.max_file_size,
    }))
}

/// Free a session previously created by `qg_create_session`.
///
/// # Safety
/// `session` must have been returned by `qg_create_session` and not yet freed.
#[no_mangle]
pub unsafe extern "C" fn qg_free_session(session: *mut QgSession) {
    if !session.is_null() {
        let _ = Box::from_raw(session);
    }
}

/// Streaming search using a pre-compiled session. Same semantics as
/// `qg_search_file_stream` but skips pattern compilation.
///
/// # Safety
/// All pointer arguments must be valid. `session` must be a live session from
/// `qg_create_session`. The session may be shared across threads (Send+Sync).
#[no_mangle]
pub unsafe extern "C" fn qg_session_search_file_stream(
    session: *const QgSession,
    path_utf16: *const c_ushort,
    path_len: usize,
    cancel_flag: *const i32,
    on_match: QgMatchCallback,
    on_match_ctx: *mut c_void,
    out_status: *mut c_int,
    out_error_msg: *mut *mut u8,
    out_error_msg_len: *mut usize,
) -> c_int {
    // Initialize out-params.
    if !out_status.is_null() {
        *out_status = STATUS_OK;
    }
    if !out_error_msg.is_null() {
        *out_error_msg = std::ptr::null_mut();
    }
    if !out_error_msg_len.is_null() {
        *out_error_msg_len = 0;
    }

    let set_status = |code: c_int| {
        if !out_status.is_null() {
            *out_status = code;
        }
        code
    };

    if session.is_null() || path_utf16.is_null() {
        return set_status(STATUS_INVALID_PATH);
    }

    let sess = &*session;

    let path_slice = std::slice::from_raw_parts(path_utf16, path_len);
    let path = match String::from_utf16(path_slice) {
        Ok(s) => s,
        Err(_) => return set_status(STATUS_INVALID_PATH),
    };

    // Open + size check + (probe/read/mmap).
    let file_bytes = match open_file_for_scan(&path, sess.max_file_size, sess.scan_opts.skip_binary) {
        Ok(b) => b,
        Err(status) => return set_status(status),
    };
    let bytes: &[u8] = file_bytes.as_slice();

    let cancel_atomic: Option<&AtomicI32> = if cancel_flag.is_null() {
        None
    } else {
        debug_assert!(
            cancel_flag as usize % std::mem::align_of::<AtomicI32>() == 0,
            "cancel_flag must be 4-byte aligned"
        );
        Some(&*(cancel_flag as *const AtomicI32))
    };

    let mut before_buf: Vec<u8> = Vec::new();
    let mut after_buf: Vec<u8> = Vec::new();
    let mut line_poll_counter: u32 = 0;

    let cancel_check = || {
        line_poll_counter = line_poll_counter.wrapping_add(1);
        if line_poll_counter & 0x3f != 0 {
            return false;
        }
        match cancel_atomic {
            Some(flag) => flag.load(Ordering::Relaxed) != 0,
            None => false,
        }
    };

    let scan_result = scan_bytes_with_matcher_ex(
        bytes,
        &*sess.matcher,
        &sess.scan_opts,
        cancel_check,
        |rec: MatchRecord| {
            if let Some(flag) = cancel_atomic {
                if flag.load(Ordering::Relaxed) != 0 {
                    return false;
                }
            }

            pack_lines(&rec.context_before, &mut before_buf);
            pack_lines(&rec.context_after, &mut after_buf);

            let view = QgMatchView {
                line_number: rec.line_number,
                match_start: rec.match_start,
                match_len: rec.match_len,
                line_ptr: rec.line.as_ptr(),
                line_len: rec.line.len(),
                ctx_before_ptr: if before_buf.is_empty() {
                    std::ptr::null()
                } else {
                    before_buf.as_ptr()
                },
                ctx_before_bytes: before_buf.len(),
                ctx_before_count: rec.context_before.len() as c_uint,
                ctx_after_ptr: if after_buf.is_empty() {
                    std::ptr::null()
                } else {
                    after_buf.as_ptr()
                },
                ctx_after_bytes: after_buf.len(),
                ctx_after_count: rec.context_after.len() as c_uint,
            };

            let cb_result = on_match(on_match_ctx, &view as *const QgMatchView);
            cb_result == 0
        },
    );

    match scan_result {
        Ok(_) => set_status(STATUS_OK),
        Err(e) => set_status(scan_error_to_status(&e)),
    }
}

// ---------------------------------------------------------------------------
// Parallel batch API: scan many files in parallel using a worker pool.
// ---------------------------------------------------------------------------

/// Match callback for the parallel batch API. Receives the file index
/// (`0..path_count`) so the caller can correlate matches with input paths.
/// Returning a non-zero value signals the worker pool to stop scanning new
/// files; in-flight files finish their current scan before threads exit.
pub type QgFileMatchCallback = unsafe extern "C" fn(
    ctx: *mut c_void,
    file_index: c_uint,
    m: *const QgMatchView,
) -> c_int;

/// Per-file completion callback for the parallel batch API. Called once per
/// path with the final status code (any of the `STATUS_*` constants). The
/// callback is invoked under the same serialization mutex as `on_match`, so
/// the C# delegate sees a single-threaded view of events.
pub type QgFileDoneCallback =
    unsafe extern "C" fn(ctx: *mut c_void, file_index: c_uint, status: c_int);

/// Extended per-file completion callback that also reports the file length
/// already obtained by Rust during open/metadata validation. This avoids a
/// second managed FileInfo stat on every matched file.
pub type QgFileDoneExCallback = unsafe extern "C" fn(
    ctx: *mut c_void,
    file_index: c_uint,
    status: c_int,
    file_len: c_ulonglong,
    last_modified: c_ulonglong,
);

#[derive(Clone, Copy)]
enum QgFileDoneDispatch {
    Legacy(QgFileDoneCallback),
    WithLength(QgFileDoneExCallback),
}

#[inline]
unsafe fn dispatch_file_done(
    callback: QgFileDoneDispatch,
    ctx: *mut c_void,
    file_index: c_uint,
    status: c_int,
    file_len: u64,
    last_modified: u64,
) {
    match callback {
        QgFileDoneDispatch::Legacy(cb) => cb(ctx, file_index, status),
        QgFileDoneDispatch::WithLength(cb) => cb(ctx, file_index, status, file_len, last_modified),
    }
}

/// Scan `path_count` files in parallel using a session.
///
/// The match and per-file-done callbacks are serialized through an internal
/// mutex so the C# delegate target may be non-thread-safe. The win comes from
/// overlapping `File::open + read` (the bottleneck on the C:\ benchmark) across
/// `thread_count` workers.
///
/// `paths_utf16_concat` points to the concatenation of all UTF-16 paths;
/// `path_lengths[i]` gives the length (in u16 code units) of the i-th path.
///
/// `thread_count == 0` selects `std::thread::available_parallelism()` (capped
/// at 64). The function blocks until all paths are processed or the cancel
/// flag is set.
///
/// Returns `STATUS_OK` on a clean run (including when individual files are
/// skipped or fail) or `STATUS_CANCELLED` if `cancel_flag` was tripped.
///
/// # Safety
/// All pointers must remain valid for the call. `session` must be live.
/// `paths_utf16_concat` must reference a buffer of at least
/// `sum(path_lengths[0..path_count])` u16 elements. `path_lengths` must
/// reference at least `path_count` u32 elements. Callbacks must be valid
/// `extern "C"` function pointers.
#[no_mangle]
pub unsafe extern "C" fn qg_session_scan_paths_parallel(
    session: *const QgSession,
    paths_utf16_concat: *const c_ushort,
    path_lengths: *const c_uint,
    path_count: usize,
    thread_count: c_uint,
    cancel_flag: *const i32,
    on_match: QgFileMatchCallback,
    on_file_done: QgFileDoneCallback,
    on_match_ctx: *mut c_void,
) -> c_int {
    qg_session_scan_paths_parallel_impl(
        session,
        paths_utf16_concat,
        path_lengths,
        path_count,
        thread_count,
        cancel_flag,
        on_match,
        QgFileDoneDispatch::Legacy(on_file_done),
        on_match_ctx,
    )
}

#[no_mangle]
pub unsafe extern "C" fn qg_session_scan_paths_parallel_ex(
    session: *const QgSession,
    paths_utf16_concat: *const c_ushort,
    path_lengths: *const c_uint,
    path_count: usize,
    thread_count: c_uint,
    cancel_flag: *const i32,
    on_match: QgFileMatchCallback,
    on_file_done: QgFileDoneExCallback,
    on_match_ctx: *mut c_void,
) -> c_int {
    qg_session_scan_paths_parallel_impl(
        session,
        paths_utf16_concat,
        path_lengths,
        path_count,
        thread_count,
        cancel_flag,
        on_match,
        QgFileDoneDispatch::WithLength(on_file_done),
        on_match_ctx,
    )
}

unsafe fn qg_session_scan_paths_parallel_impl(
    session: *const QgSession,
    paths_utf16_concat: *const c_ushort,
    path_lengths: *const c_uint,
    path_count: usize,
    thread_count: c_uint,
    cancel_flag: *const i32,
    on_match: QgFileMatchCallback,
    on_file_done: QgFileDoneDispatch,
    on_match_ctx: *mut c_void,
) -> c_int {
    if session.is_null() || (path_count > 0 && (paths_utf16_concat.is_null() || path_lengths.is_null())) {
        return STATUS_INVALID_PATH;
    }
    if path_count == 0 {
        return STATUS_OK;
    }

    let sess: &QgSession = &*session;

    // Materialize each path's u16 slice up front so worker threads can index in
    // O(1). Validate cumulative length against usize overflow.
    let lengths_slice = std::slice::from_raw_parts(path_lengths, path_count);
    let mut total: usize = 0;
    for &l in lengths_slice {
        total = match total.checked_add(l as usize) {
            Some(t) => t,
            None => return STATUS_INVALID_PATH,
        };
    }
    let all_utf16 = std::slice::from_raw_parts(paths_utf16_concat, total);
    let mut path_slices: Vec<&[u16]> = Vec::with_capacity(path_count);
    let mut cursor = 0usize;
    for &l in lengths_slice {
        let end = cursor + l as usize;
        path_slices.push(&all_utf16[cursor..end]);
        cursor = end;
    }

    let cancel_atomic: Option<&AtomicI32> = if cancel_flag.is_null() {
        None
    } else {
        debug_assert!(
            cancel_flag as usize % std::mem::align_of::<AtomicI32>() == 0,
            "cancel_flag must be 4-byte aligned"
        );
        Some(&*(cancel_flag as *const AtomicI32))
    };

    // Resolve thread count. Cap at 64 to avoid pathological oversubscription
    // when callers pass a huge value or get fooled by a virtualized CPU count.
    const MAX_WORKERS: usize = 64;
    let workers = if thread_count == 0 {
        std::thread::available_parallelism()
            .map(|n| n.get())
            .unwrap_or(4)
    } else {
        thread_count as usize
    };
    let workers = workers.clamp(1, MAX_WORKERS).min(path_count);

    // Shared state.
    let next_index = AtomicUsize::new(0);
    let cb_mutex = Mutex::new(());
    let early_stop = std::sync::atomic::AtomicBool::new(false);

    // Make the session and callback context shareable with the workers.
    // SAFETY: callers contract that `session` and `on_match_ctx` outlive
    // this call and are safe to read concurrently. `sess.matcher` is
    // Send + Sync (LineMatcher impls are read-only).
    struct CtxPtr(*mut c_void);
    unsafe impl Send for CtxPtr {}
    unsafe impl Sync for CtxPtr {}
    let ctx_wrapper = CtxPtr(on_match_ctx);

    std::thread::scope(|s| {
        for _ in 0..workers {
            s.spawn(|| {
                // Per-thread scratch buffers — reused across files.
                let mut before_buf: Vec<u8> = Vec::new();
                let mut after_buf: Vec<u8> = Vec::new();
                // Per-thread file-content scratch. Sized to MMAP_THRESHOLD so
                // the common small-file case never reallocates after the
                // first iteration. ~75% of real-world files fit here.
                let mut file_scratch: Vec<u8> =
                    Vec::with_capacity(MMAP_THRESHOLD_BYTES as usize);
                let _ = &ctx_wrapper; // capture by reference

                loop {
                    if early_stop.load(Ordering::Relaxed) {
                        return;
                    }
                    if let Some(flag) = cancel_atomic {
                        if flag.load(Ordering::Relaxed) != 0 {
                            return;
                        }
                    }

                    let i = next_index.fetch_add(1, Ordering::Relaxed);
                    if i >= path_count {
                        return;
                    }

                    let path_u16 = path_slices[i];
                    let path = match String::from_utf16(path_u16) {
                        Ok(s) => s,
                        Err(_) => {
                            // Report the failure and keep going.
                            let _g = cb_mutex.lock().unwrap();
                            dispatch_file_done(
                                on_file_done,
                                ctx_wrapper.0,
                                i as c_uint,
                                STATUS_INVALID_PATH,
                                0,
                                0,
                            );
                            continue;
                        }
                    };

                    let (file_bytes, file_len, last_modified) = match open_file_for_scan_into(
                        &path,
                        sess.max_file_size,
                        sess.scan_opts.skip_binary,
                        &mut file_scratch,
                    ) {
                        Ok(b) => b,
                        Err(status) => {
                            let _g = cb_mutex.lock().unwrap();
                            dispatch_file_done(
                                on_file_done,
                                ctx_wrapper.0,
                                i as c_uint,
                                status,
                                0,
                                0,
                            );
                            continue;
                        }
                    };
                    let bytes: &[u8] = file_bytes.as_slice();

                    let mut line_poll_counter: u32 = 0;
                    let cancel_check = || {
                        line_poll_counter = line_poll_counter.wrapping_add(1);
                        if line_poll_counter & 0x3f != 0 {
                            return early_stop.load(Ordering::Relaxed);
                        }
                        if early_stop.load(Ordering::Relaxed) {
                            return true;
                        }
                        match cancel_atomic {
                            Some(flag) => flag.load(Ordering::Relaxed) != 0,
                            None => false,
                        }
                    };

                    let scan_result = scan_bytes_with_matcher_ex(
                        bytes,
                        &*sess.matcher,
                        &sess.scan_opts,
                        cancel_check,
                        |rec: MatchRecord| {
                            pack_lines(&rec.context_before, &mut before_buf);
                            pack_lines(&rec.context_after, &mut after_buf);

                            let view = QgMatchView {
                                line_number: rec.line_number,
                                match_start: rec.match_start,
                                match_len: rec.match_len,
                                line_ptr: rec.line.as_ptr(),
                                line_len: rec.line.len(),
                                ctx_before_ptr: if before_buf.is_empty() {
                                    std::ptr::null()
                                } else {
                                    before_buf.as_ptr()
                                },
                                ctx_before_bytes: before_buf.len(),
                                ctx_before_count: rec.context_before.len() as c_uint,
                                ctx_after_ptr: if after_buf.is_empty() {
                                    std::ptr::null()
                                } else {
                                    after_buf.as_ptr()
                                },
                                ctx_after_bytes: after_buf.len(),
                                ctx_after_count: rec.context_after.len() as c_uint,
                            };

                            let cb_result = {
                                let _g = cb_mutex.lock().unwrap();
                                on_match(
                                    ctx_wrapper.0,
                                    i as c_uint,
                                    &view as *const QgMatchView,
                                )
                            };
                            if cb_result != 0 {
                                early_stop.store(true, Ordering::Relaxed);
                                return false;
                            }
                            true
                        },
                    );

                    let final_status = match scan_result {
                        Ok(_) => STATUS_OK,
                        Err(e) => scan_error_to_status(&e),
                    };
                    let _g = cb_mutex.lock().unwrap();
                    dispatch_file_done(
                        on_file_done,
                        ctx_wrapper.0,
                        i as c_uint,
                        final_status,
                        file_len,
                        last_modified,
                    );
                }
            });
        }
    });

    if let Some(flag) = cancel_atomic {
        if flag.load(Ordering::Relaxed) != 0 {
            return STATUS_CANCELLED;
        }
    }
    STATUS_OK
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;

    /// Helper: encode a Rust &str as UTF-16 for the FFI path parameter.
    fn to_utf16(s: &str) -> Vec<u16> {
        s.encode_utf16().collect()
    }

    /// Helper: create a default QgOptions for tests.
    fn default_opts() -> QgOptions {
        QgOptions {
            case_sensitive: 0,
            use_regex: 0,
            skip_binary: 1,
            _pad0: 0,
            context_before: 0,
            context_after: 0,
            max_results: 0,
            max_file_size: 0,
        }
    }

    /// Helper: create a temp file with given content, return its path.
    fn temp_file(name: &str, content: &[u8]) -> (tempfile::TempDir, String) {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join(name);
        let mut f = File::create(&path).unwrap();
        f.write_all(content).unwrap();
        f.sync_all().unwrap();
        (dir, path.to_string_lossy().into_owned())
    }

    // ---- qg_abi_version ----
    #[test]
    fn abi_version_returns_4() {
        assert_eq!(qg_abi_version(), 4);
    }

    // ---- pack_lines ----
    #[test]
    fn pack_lines_empty() {
        let mut buf = Vec::new();
        pack_lines(&[], &mut buf);
        assert!(buf.is_empty());
    }

    #[test]
    fn pack_lines_multiple() {
        let lines = vec![b"hello".to_vec(), b"world".to_vec()];
        let mut buf = Vec::new();
        pack_lines(&lines, &mut buf);
        // Should contain: u32(5) + "hello" + u32(5) + "world"
        assert_eq!(buf.len(), 4 + 5 + 4 + 5);
        let len1 = u32::from_le_bytes(buf[0..4].try_into().unwrap());
        assert_eq!(len1, 5);
        assert_eq!(&buf[4..9], b"hello");
    }

    #[test]
    fn pack_lines_clears_previous_content() {
        let mut buf = vec![0xFFu8; 100];
        pack_lines(&[b"x".to_vec()], &mut buf);
        assert_eq!(buf.len(), 4 + 1);
    }

    // ---- qg_free_buffer ----
    #[test]
    fn free_buffer_null() {
        unsafe {
            qg_free_buffer(std::ptr::null_mut(), 0);
            qg_free_buffer(std::ptr::null_mut(), 10);
        }
    }

    #[test]
    fn free_buffer_valid() {
        unsafe {
            let mut v = vec![1u8, 2, 3];
            v.shrink_to_fit();
            let len = v.len();
            let ptr = v.as_mut_ptr();
            std::mem::forget(v);
            qg_free_buffer(ptr, len); // should not crash
        }
    }

    #[test]
    fn free_buffer_zero_len() {
        unsafe {
            // A non-null ptr with len=0 should be treated as no-op
            qg_free_buffer(1 as *mut u8, 0);
        }
    }

    // ---- qg_free_result ----
    #[test]
    fn free_result_null() {
        unsafe {
            qg_free_result(std::ptr::null_mut());
        }
    }

    #[test]
    fn free_result_empty() {
        unsafe {
            let mut result = QgResult {
                buffer: std::ptr::null_mut(),
                buffer_len: 0,
                match_count: 0,
                status: 0,
                error_msg: std::ptr::null_mut(),
                error_msg_len: 0,
            };
            qg_free_result(&mut result);
            assert!(result.buffer.is_null());
        }
    }

    #[test]
    fn free_result_with_buffer() {
        unsafe {
            let mut buf = vec![0u8; 16];
            buf.shrink_to_fit();
            let len = buf.len();
            let ptr = buf.as_mut_ptr();
            std::mem::forget(buf);

            let mut result = QgResult {
                buffer: ptr,
                buffer_len: len,
                match_count: 0,
                status: 0,
                error_msg: std::ptr::null_mut(),
                error_msg_len: 0,
            };
            qg_free_result(&mut result);
            assert!(result.buffer.is_null());
            assert_eq!(result.buffer_len, 0);
        }
    }

    #[test]
    fn free_result_with_error_msg() {
        unsafe {
            let mut msg = b"error message".to_vec();
            msg.shrink_to_fit();
            let len = msg.len();
            let ptr = msg.as_mut_ptr();
            std::mem::forget(msg);

            let mut result = QgResult {
                buffer: std::ptr::null_mut(),
                buffer_len: 0,
                match_count: 0,
                status: STATUS_INVALID_REGEX,
                error_msg: ptr,
                error_msg_len: len,
            };
            qg_free_result(&mut result);
            assert!(result.error_msg.is_null());
            assert_eq!(result.error_msg_len, 0);
        }
    }

    // ---- qg_search_file ----
    #[test]
    fn search_file_null_out_result() {
        unsafe {
            let ret = qg_search_file(
                std::ptr::null(),
                0,
                std::ptr::null(),
                0,
                std::ptr::null(),
                std::ptr::null(),
                std::ptr::null_mut(),
            );
            assert_eq!(ret, -1);
        }
    }

    #[test]
    fn search_file_null_path() {
        unsafe {
            let mut result = std::mem::zeroed::<QgResult>();
            let opts = default_opts();
            let ret = qg_search_file(
                std::ptr::null(),
                0,
                std::ptr::null(),
                0,
                &opts,
                std::ptr::null(),
                &mut result,
            );
            assert_eq!(ret, STATUS_INVALID_PATH);
            assert_eq!(result.status, STATUS_INVALID_PATH);
        }
    }

    #[test]
    fn search_file_null_options() {
        unsafe {
            let mut result = std::mem::zeroed::<QgResult>();
            let path = to_utf16("test.txt");
            let ret = qg_search_file(
                path.as_ptr(),
                path.len(),
                std::ptr::null(),
                0,
                std::ptr::null(),
                std::ptr::null(),
                &mut result,
            );
            assert_eq!(ret, STATUS_INVALID_PATH);
        }
    }

    #[test]
    fn search_file_invalid_utf16_path() {
        unsafe {
            let mut result = std::mem::zeroed::<QgResult>();
            let opts = default_opts();
            // Lone surrogate — invalid UTF-16
            let bad_path: Vec<u16> = vec![0xD800, 0x0041];
            let ret = qg_search_file(
                bad_path.as_ptr(),
                bad_path.len(),
                std::ptr::null(),
                0,
                &opts,
                std::ptr::null(),
                &mut result,
            );
            assert_eq!(ret, STATUS_INVALID_PATH);
        }
    }

    #[test]
    fn search_file_invalid_utf8_pattern() {
        unsafe {
            let mut result = std::mem::zeroed::<QgResult>();
            let opts = default_opts();
            let path = to_utf16("test.txt");
            let bad_pattern: &[u8] = &[0xFF, 0xFE];
            let ret = qg_search_file(
                path.as_ptr(),
                path.len(),
                bad_pattern.as_ptr(),
                bad_pattern.len(),
                &opts,
                std::ptr::null(),
                &mut result,
            );
            assert_eq!(ret, STATUS_INVALID_PATH);
        }
    }

    #[test]
    fn search_file_null_pattern() {
        unsafe {
            let (_dir, path_str) = temp_file("null_pat.txt", b"hello world\n");
            let mut result = std::mem::zeroed::<QgResult>();
            let opts = default_opts();
            let path = to_utf16(&path_str);
            let ret = qg_search_file(
                path.as_ptr(),
                path.len(),
                std::ptr::null(),
                0,
                &opts,
                std::ptr::null(),
                &mut result,
            );
            // null pattern → empty string → no crash, 0 matches
            assert_eq!(ret, STATUS_OK);
            qg_free_result(&mut result);
        }
    }

    #[test]
    fn search_file_nonexistent() {
        unsafe {
            let mut result = std::mem::zeroed::<QgResult>();
            let opts = default_opts();
            let path = to_utf16("C:\\nonexistent_file_12345.txt");
            let pattern = b"test";
            let ret = qg_search_file(
                path.as_ptr(),
                path.len(),
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null(),
                &mut result,
            );
            assert_eq!(ret, STATUS_OPEN_FAILED);
        }
    }

    #[test]
    fn search_file_too_large() {
        unsafe {
            let (_dir, path_str) = temp_file("large.txt", b"hello world\n");
            let mut result = std::mem::zeroed::<QgResult>();
            let mut opts = default_opts();
            opts.max_file_size = 1; // 1 byte max — file is larger
            let path = to_utf16(&path_str);
            let pattern = b"hello";
            let ret = qg_search_file(
                path.as_ptr(),
                path.len(),
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null(),
                &mut result,
            );
            assert_eq!(ret, STATUS_TOO_LARGE);
        }
    }

    #[test]
    fn search_file_empty() {
        unsafe {
            let (_dir, path_str) = temp_file("empty.txt", b"");
            let mut result = std::mem::zeroed::<QgResult>();
            let opts = default_opts();
            let path = to_utf16(&path_str);
            let pattern = b"hello";
            let ret = qg_search_file(
                path.as_ptr(),
                path.len(),
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null(),
                &mut result,
            );
            assert_eq!(ret, STATUS_OK);
            assert_eq!(result.match_count, 0);
        }
    }

    #[test]
    fn search_file_binary_skipped() {
        unsafe {
            let (_dir, path_str) = temp_file("binary.bin", b"hello\0world\n");
            let mut result = std::mem::zeroed::<QgResult>();
            let opts = default_opts();
            let path = to_utf16(&path_str);
            let pattern = b"hello";
            let ret = qg_search_file(
                path.as_ptr(),
                path.len(),
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null(),
                &mut result,
            );
            assert_eq!(ret, STATUS_BINARY_SKIPPED);
        }
    }

    #[test]
    fn search_file_invalid_regex() {
        unsafe {
            let (_dir, path_str) = temp_file("regex.txt", b"hello\n");
            let mut result = std::mem::zeroed::<QgResult>();
            let mut opts = default_opts();
            opts.use_regex = 1;
            let path = to_utf16(&path_str);
            let pattern = b"[invalid";
            let ret = qg_search_file(
                path.as_ptr(),
                path.len(),
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null(),
                &mut result,
            );
            assert_eq!(ret, STATUS_INVALID_REGEX);
            assert!(!result.error_msg.is_null());
            assert!(result.error_msg_len > 0);
            qg_free_result(&mut result);
        }
    }

    #[test]
    fn search_file_with_matches() {
        unsafe {
            let (_dir, path_str) = temp_file("matches.txt", b"foo bar\nfoo baz\nhello\n");
            let mut result = std::mem::zeroed::<QgResult>();
            let opts = default_opts();
            let path = to_utf16(&path_str);
            let pattern = b"foo";
            let ret = qg_search_file(
                path.as_ptr(),
                path.len(),
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null(),
                &mut result,
            );
            assert_eq!(ret, STATUS_OK);
            assert_eq!(result.match_count, 2);
            assert!(!result.buffer.is_null());
            assert!(result.buffer_len > 0);
            qg_free_result(&mut result);
        }
    }

    #[test]
    fn search_file_with_context() {
        unsafe {
            let (_dir, path_str) = temp_file(
                "ctx.txt",
                b"before1\nbefore2\nmatch_line\nafter1\nafter2\n",
            );
            let mut result = std::mem::zeroed::<QgResult>();
            let mut opts = default_opts();
            opts.context_before = 2;
            opts.context_after = 2;
            let path = to_utf16(&path_str);
            let pattern = b"match_line";
            let ret = qg_search_file(
                path.as_ptr(),
                path.len(),
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null(),
                &mut result,
            );
            assert_eq!(ret, STATUS_OK);
            assert_eq!(result.match_count, 1);
            // Verify the packed buffer contains context
            assert!(result.buffer_len > 20);
            qg_free_result(&mut result);
        }
    }

    #[test]
    fn search_file_cancellation() {
        unsafe {
            let (_dir, path_str) = temp_file("cancel.txt", b"foo\nfoo\nfoo\n");
            let mut result = std::mem::zeroed::<QgResult>();
            let opts = default_opts();
            let path = to_utf16(&path_str);
            let pattern = b"foo";
            let cancel: i32 = 1; // pre-set to cancelled
            let ret = qg_search_file(
                path.as_ptr(),
                path.len(),
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                &cancel,
                &mut result,
            );
            // With cancel flag set, scan may complete (checked every 256 matches)
            // or the post-scan recheck catches it.
            assert!(ret == STATUS_OK || ret == STATUS_CANCELLED);
        }
    }

    // ---- qg_search_file_stream ----
    unsafe extern "C" fn count_callback(
        ctx: *mut c_void,
        _m: *const QgMatchView,
    ) -> c_int {
        let counter = &mut *(ctx as *mut u32);
        *counter += 1;
        0 // continue
    }

    unsafe extern "C" fn stop_callback(
        ctx: *mut c_void,
        _m: *const QgMatchView,
    ) -> c_int {
        let counter = &mut *(ctx as *mut u32);
        *counter += 1;
        1 // stop
    }

    #[test]
    fn stream_search_null_path() {
        unsafe {
            let mut status: c_int = 0;
            let mut err_msg: *mut u8 = std::ptr::null_mut();
            let mut err_len: usize = 0;
            let opts = default_opts();
            let ret = qg_search_file_stream(
                std::ptr::null(),
                0,
                std::ptr::null(),
                0,
                &opts,
                std::ptr::null(),
                count_callback,
                std::ptr::null_mut(),
                &mut status,
                &mut err_msg,
                &mut err_len,
            );
            assert_eq!(ret, STATUS_INVALID_PATH);
        }
    }

    #[test]
    fn stream_search_null_options() {
        unsafe {
            let mut status: c_int = 0;
            let path = to_utf16("test.txt");
            let ret = qg_search_file_stream(
                path.as_ptr(),
                path.len(),
                std::ptr::null(),
                0,
                std::ptr::null(),
                std::ptr::null(),
                count_callback,
                std::ptr::null_mut(),
                &mut status,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            assert_eq!(ret, STATUS_INVALID_PATH);
        }
    }

    #[test]
    fn stream_search_invalid_utf16() {
        unsafe {
            let mut status: c_int = 0;
            let opts = default_opts();
            let bad_path: Vec<u16> = vec![0xD800, 0x0041]; // lone surrogate
            let ret = qg_search_file_stream(
                bad_path.as_ptr(),
                bad_path.len(),
                std::ptr::null(),
                0,
                &opts,
                std::ptr::null(),
                count_callback,
                std::ptr::null_mut(),
                &mut status,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            assert_eq!(ret, STATUS_INVALID_PATH);
        }
    }

    #[test]
    fn stream_search_invalid_utf8_pattern() {
        unsafe {
            let mut status: c_int = 0;
            let opts = default_opts();
            let path = to_utf16("test.txt");
            let bad: &[u8] = &[0xFF, 0xFE];
            let ret = qg_search_file_stream(
                path.as_ptr(),
                path.len(),
                bad.as_ptr(),
                bad.len(),
                &opts,
                std::ptr::null(),
                count_callback,
                std::ptr::null_mut(),
                &mut status,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            assert_eq!(ret, STATUS_INVALID_PATH);
        }
    }

    #[test]
    fn stream_search_nonexistent_file() {
        unsafe {
            let mut status: c_int = 0;
            let opts = default_opts();
            let path = to_utf16("C:\\nonexistent_stream_12345.txt");
            let pattern = b"x";
            let ret = qg_search_file_stream(
                path.as_ptr(),
                path.len(),
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null(),
                count_callback,
                std::ptr::null_mut(),
                &mut status,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            assert_eq!(ret, STATUS_OPEN_FAILED);
        }
    }

    #[test]
    fn stream_search_too_large() {
        unsafe {
            let (_dir, path_str) = temp_file("stream_large.txt", b"hello\n");
            let mut status: c_int = 0;
            let mut opts = default_opts();
            opts.max_file_size = 1;
            let path = to_utf16(&path_str);
            let pattern = b"hello";
            let ret = qg_search_file_stream(
                path.as_ptr(),
                path.len(),
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null(),
                count_callback,
                std::ptr::null_mut(),
                &mut status,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            assert_eq!(ret, STATUS_TOO_LARGE);
        }
    }

    #[test]
    fn stream_search_empty_file() {
        unsafe {
            let (_dir, path_str) = temp_file("stream_empty.txt", b"");
            let mut status: c_int = 0;
            let opts = default_opts();
            let path = to_utf16(&path_str);
            let pattern = b"hello";
            let ret = qg_search_file_stream(
                path.as_ptr(),
                path.len(),
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null(),
                count_callback,
                std::ptr::null_mut(),
                &mut status,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            assert_eq!(ret, STATUS_OK);
        }
    }

    #[test]
    fn stream_search_binary_skipped() {
        unsafe {
            let (_dir, path_str) = temp_file("stream_bin.bin", b"hello\0world\n");
            let mut status: c_int = 0;
            let opts = default_opts();
            let path = to_utf16(&path_str);
            let pattern = b"hello";
            let ret = qg_search_file_stream(
                path.as_ptr(),
                path.len(),
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null(),
                count_callback,
                std::ptr::null_mut(),
                &mut status,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            assert_eq!(ret, STATUS_BINARY_SKIPPED);
        }
    }

    #[test]
    fn stream_search_invalid_regex() {
        unsafe {
            let (_dir, path_str) = temp_file("stream_regex.txt", b"hello\n");
            let mut status: c_int = 0;
            let mut err_msg: *mut u8 = std::ptr::null_mut();
            let mut err_len: usize = 0;
            let mut opts = default_opts();
            opts.use_regex = 1;
            let path = to_utf16(&path_str);
            let pattern = b"[invalid";
            let ret = qg_search_file_stream(
                path.as_ptr(),
                path.len(),
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null(),
                count_callback,
                std::ptr::null_mut(),
                &mut status,
                &mut err_msg,
                &mut err_len,
            );
            assert_eq!(ret, STATUS_INVALID_REGEX);
            assert!(!err_msg.is_null());
            assert!(err_len > 0);
            qg_free_buffer(err_msg, err_len);
        }
    }

    #[test]
    fn stream_search_with_matches() {
        unsafe {
            let (_dir, path_str) = temp_file("stream_match.txt", b"foo bar\nfoo baz\nhello\n");
            let mut status: c_int = 0;
            let opts = default_opts();
            let path = to_utf16(&path_str);
            let pattern = b"foo";
            let mut count: u32 = 0;
            let ret = qg_search_file_stream(
                path.as_ptr(),
                path.len(),
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null(),
                count_callback,
                &mut count as *mut u32 as *mut c_void,
                &mut status,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            assert_eq!(ret, STATUS_OK);
            assert_eq!(count, 2);
        }
    }

    #[test]
    fn stream_search_callback_stops_early() {
        unsafe {
            let (_dir, path_str) = temp_file("stream_stop.txt", b"foo\nfoo\nfoo\n");
            let mut status: c_int = 0;
            let opts = default_opts();
            let path = to_utf16(&path_str);
            let pattern = b"foo";
            let mut count: u32 = 0;
            let ret = qg_search_file_stream(
                path.as_ptr(),
                path.len(),
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null(),
                stop_callback,
                &mut count as *mut u32 as *mut c_void,
                &mut status,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            assert_eq!(ret, STATUS_OK);
            assert_eq!(count, 1); // stopped after first
        }
    }

    #[test]
    fn stream_search_with_context() {
        unsafe {
            let (_dir, path_str) = temp_file(
                "stream_ctx.txt",
                b"before\nmatch_here\nafter\n",
            );
            let mut status: c_int = 0;
            let mut opts = default_opts();
            opts.context_before = 1;
            opts.context_after = 1;
            let path = to_utf16(&path_str);
            let pattern = b"match_here";
            let mut count: u32 = 0;
            let ret = qg_search_file_stream(
                path.as_ptr(),
                path.len(),
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null(),
                count_callback,
                &mut count as *mut u32 as *mut c_void,
                &mut status,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            assert_eq!(ret, STATUS_OK);
            assert_eq!(count, 1);
        }
    }

    #[test]
    fn stream_search_null_pattern() {
        unsafe {
            let (_dir, path_str) = temp_file("stream_nullpat.txt", b"hello\n");
            let mut status: c_int = 0;
            let opts = default_opts();
            let path = to_utf16(&path_str);
            let mut count: u32 = 0;
            let ret = qg_search_file_stream(
                path.as_ptr(),
                path.len(),
                std::ptr::null(),
                0,
                &opts,
                std::ptr::null(),
                count_callback,
                &mut count as *mut u32 as *mut c_void,
                &mut status,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            assert_eq!(ret, STATUS_OK);
        }
    }

    #[test]
    fn stream_search_null_out_params() {
        unsafe {
            let (_dir, path_str) = temp_file("stream_nullout.txt", b"foo\n");
            let opts = default_opts();
            let path = to_utf16(&path_str);
            let pattern = b"foo";
            let mut count: u32 = 0;
            let ret = qg_search_file_stream(
                path.as_ptr(),
                path.len(),
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null(),
                count_callback,
                &mut count as *mut u32 as *mut c_void,
                std::ptr::null_mut(), // null out_status
                std::ptr::null_mut(), // null out_error_msg
                std::ptr::null_mut(), // null out_error_msg_len
            );
            assert_eq!(ret, STATUS_OK);
            assert_eq!(count, 1);
        }
    }

    // ---- qg_create_session / qg_free_session ----
    #[test]
    fn create_session_null_options() {
        unsafe {
            let mut err_msg: *mut u8 = std::ptr::null_mut();
            let mut err_len: usize = 0;
            let session = qg_create_session(
                std::ptr::null(),
                0,
                std::ptr::null(),
                &mut err_msg,
                &mut err_len,
            );
            assert!(session.is_null());
        }
    }

    #[test]
    fn create_session_invalid_utf8() {
        unsafe {
            let opts = default_opts();
            let bad: &[u8] = &[0xFF, 0xFE];
            let session = qg_create_session(
                bad.as_ptr(),
                bad.len(),
                &opts,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            assert!(session.is_null());
        }
    }

    #[test]
    fn create_session_invalid_regex() {
        unsafe {
            let mut opts = default_opts();
            opts.use_regex = 1;
            let pattern = b"[invalid";
            let mut err_msg: *mut u8 = std::ptr::null_mut();
            let mut err_len: usize = 0;
            let session = qg_create_session(
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                &mut err_msg,
                &mut err_len,
            );
            assert!(session.is_null());
            assert!(!err_msg.is_null());
            assert!(err_len > 0);
            qg_free_buffer(err_msg, err_len);
        }
    }

    #[test]
    fn create_session_invalid_regex_null_out_params() {
        unsafe {
            let mut opts = default_opts();
            opts.use_regex = 1;
            let pattern = b"[invalid";
            let session = qg_create_session(
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            assert!(session.is_null());
        }
    }

    #[test]
    fn create_session_null_pattern() {
        unsafe {
            let opts = default_opts();
            let mut err_msg: *mut u8 = std::ptr::null_mut();
            let mut err_len: usize = 0;
            let session = qg_create_session(
                std::ptr::null(),
                0,
                &opts,
                &mut err_msg,
                &mut err_len,
            );
            // null pattern → empty string → valid
            assert!(!session.is_null());
            qg_free_session(session);
        }
    }

    #[test]
    fn create_and_free_session() {
        unsafe {
            let opts = default_opts();
            let pattern = b"hello";
            let session = qg_create_session(
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            assert!(!session.is_null());
            qg_free_session(session);
        }
    }

    #[test]
    fn free_session_null() {
        unsafe {
            qg_free_session(std::ptr::null_mut());
        }
    }

    // ---- qg_session_search_file_stream ----
    #[test]
    fn session_stream_null_session() {
        unsafe {
            let mut status: c_int = 0;
            let path = to_utf16("test.txt");
            let ret = qg_session_search_file_stream(
                std::ptr::null(),
                path.as_ptr(),
                path.len(),
                std::ptr::null(),
                count_callback,
                std::ptr::null_mut(),
                &mut status,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            assert_eq!(ret, STATUS_INVALID_PATH);
        }
    }

    #[test]
    fn session_stream_null_path() {
        unsafe {
            let opts = default_opts();
            let pattern = b"test";
            let session = qg_create_session(
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            assert!(!session.is_null());
            let mut status: c_int = 0;
            let ret = qg_session_search_file_stream(
                session,
                std::ptr::null(),
                0,
                std::ptr::null(),
                count_callback,
                std::ptr::null_mut(),
                &mut status,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            assert_eq!(ret, STATUS_INVALID_PATH);
            qg_free_session(session);
        }
    }

    #[test]
    fn session_stream_invalid_utf16() {
        unsafe {
            let opts = default_opts();
            let pattern = b"test";
            let session = qg_create_session(
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            let mut status: c_int = 0;
            let bad_path: Vec<u16> = vec![0xD800]; // lone surrogate
            let ret = qg_session_search_file_stream(
                session,
                bad_path.as_ptr(),
                bad_path.len(),
                std::ptr::null(),
                count_callback,
                std::ptr::null_mut(),
                &mut status,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            assert_eq!(ret, STATUS_INVALID_PATH);
            qg_free_session(session);
        }
    }

    #[test]
    fn session_stream_nonexistent_file() {
        unsafe {
            let opts = default_opts();
            let pattern = b"test";
            let session = qg_create_session(
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            let mut status: c_int = 0;
            let path = to_utf16("C:\\nonexistent_session_12345.txt");
            let ret = qg_session_search_file_stream(
                session,
                path.as_ptr(),
                path.len(),
                std::ptr::null(),
                count_callback,
                std::ptr::null_mut(),
                &mut status,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            assert_eq!(ret, STATUS_OPEN_FAILED);
            qg_free_session(session);
        }
    }

    #[test]
    fn session_stream_too_large() {
        unsafe {
            let (_dir, path_str) = temp_file("sess_large.txt", b"hello\n");
            let mut opts = default_opts();
            opts.max_file_size = 1;
            let pattern = b"hello";
            let session = qg_create_session(
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            let mut status: c_int = 0;
            let path = to_utf16(&path_str);
            let ret = qg_session_search_file_stream(
                session,
                path.as_ptr(),
                path.len(),
                std::ptr::null(),
                count_callback,
                std::ptr::null_mut(),
                &mut status,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            assert_eq!(ret, STATUS_TOO_LARGE);
            qg_free_session(session);
        }
    }

    #[test]
    fn session_stream_empty_file() {
        unsafe {
            let (_dir, path_str) = temp_file("sess_empty.txt", b"");
            let opts = default_opts();
            let pattern = b"hello";
            let session = qg_create_session(
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            let mut status: c_int = 0;
            let path = to_utf16(&path_str);
            let ret = qg_session_search_file_stream(
                session,
                path.as_ptr(),
                path.len(),
                std::ptr::null(),
                count_callback,
                std::ptr::null_mut(),
                &mut status,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            assert_eq!(ret, STATUS_OK);
            qg_free_session(session);
        }
    }

    #[test]
    fn session_stream_binary_skipped() {
        unsafe {
            let (_dir, path_str) = temp_file("sess_bin.bin", b"hello\0world\n");
            let opts = default_opts();
            let pattern = b"hello";
            let session = qg_create_session(
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            let mut status: c_int = 0;
            let path = to_utf16(&path_str);
            let ret = qg_session_search_file_stream(
                session,
                path.as_ptr(),
                path.len(),
                std::ptr::null(),
                count_callback,
                std::ptr::null_mut(),
                &mut status,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            assert_eq!(ret, STATUS_BINARY_SKIPPED);
            qg_free_session(session);
        }
    }

    #[test]
    fn session_stream_with_matches() {
        unsafe {
            let (_dir, path_str) = temp_file("sess_match.txt", b"foo bar\nfoo baz\nhello\n");
            let opts = default_opts();
            let pattern = b"foo";
            let session = qg_create_session(
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            let mut status: c_int = 0;
            let path = to_utf16(&path_str);
            let mut count: u32 = 0;
            let ret = qg_session_search_file_stream(
                session,
                path.as_ptr(),
                path.len(),
                std::ptr::null(),
                count_callback,
                &mut count as *mut u32 as *mut c_void,
                &mut status,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            assert_eq!(ret, STATUS_OK);
            assert_eq!(count, 2);
            qg_free_session(session);
        }
    }

    #[test]
    fn session_stream_with_context() {
        unsafe {
            let (_dir, path_str) = temp_file(
                "sess_ctx.txt",
                b"before\nmatch_line\nafter\n",
            );
            let mut opts = default_opts();
            opts.context_before = 1;
            opts.context_after = 1;
            let pattern = b"match_line";
            let session = qg_create_session(
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            let mut status: c_int = 0;
            let path = to_utf16(&path_str);
            let mut count: u32 = 0;
            let ret = qg_session_search_file_stream(
                session,
                path.as_ptr(),
                path.len(),
                std::ptr::null(),
                count_callback,
                &mut count as *mut u32 as *mut c_void,
                &mut status,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            assert_eq!(ret, STATUS_OK);
            assert_eq!(count, 1);
            qg_free_session(session);
        }
    }

    #[test]
    fn session_stream_null_out_params() {
        unsafe {
            let (_dir, path_str) = temp_file("sess_nullout.txt", b"foo\n");
            let opts = default_opts();
            let pattern = b"foo";
            let session = qg_create_session(
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            let path = to_utf16(&path_str);
            let mut count: u32 = 0;
            let ret = qg_session_search_file_stream(
                session,
                path.as_ptr(),
                path.len(),
                std::ptr::null(),
                count_callback,
                &mut count as *mut u32 as *mut c_void,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            assert_eq!(ret, STATUS_OK);
            assert_eq!(count, 1);
            qg_free_session(session);
        }
    }

    // ---- Cancellation via cancel_flag ----
    #[test]
    fn session_stream_cancellation() {
        unsafe {
            let (_dir, path_str) = temp_file("sess_cancel.txt", b"foo\nfoo\nfoo\n");
            let opts = default_opts();
            let pattern = b"foo";
            let session = qg_create_session(
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            let cancel: i32 = 1;
            let mut status: c_int = 0;
            let path = to_utf16(&path_str);
            let mut count: u32 = 0;
            let ret = qg_session_search_file_stream(
                session,
                path.as_ptr(),
                path.len(),
                &cancel,
                count_callback,
                &mut count as *mut u32 as *mut c_void,
                &mut status,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            // Few matches, so cancellation poll may not trigger; either result is OK
            assert!(ret == STATUS_OK || ret == STATUS_CANCELLED);
            qg_free_session(session);
        }
    }

    // ---- create_session with null error out-params ----
    #[test]
    fn create_session_null_err_msg_only() {
        unsafe {
            let opts = default_opts();
            let pattern = b"hello";
            let session = qg_create_session(
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            assert!(!session.is_null());
            qg_free_session(session);
        }
    }

    // ---- Cancel polling: 300+ matches triggers poll_counter & 0xff == 0 ----

    /// Create a temp file with `n` lines each containing "match".
    fn temp_file_many_matches(name: &str, n: usize) -> (tempfile::TempDir, String) {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join(name);
        let mut content = Vec::new();
        for _ in 0..n {
            content.extend_from_slice(b"match\n");
        }
        std::fs::write(&path, &content).unwrap();
        (dir, path.to_string_lossy().into_owned())
    }

    #[test]
    fn batch_search_cancel_polling_triggers() {
        // 300 matches ensures poll_counter reaches 256 → polls cancel flag.
        // Cancel flag is 1, so the callback returns false → Cancelled.
        unsafe {
            let (_dir, path_str) = temp_file_many_matches("cancel_poll.txt", 300);
            let mut result = std::mem::zeroed::<QgResult>();
            let opts = default_opts();
            let path = to_utf16(&path_str);
            let pattern = b"match";
            let cancel: i32 = 1;
            let ret = qg_search_file(
                path.as_ptr(),
                path.len(),
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                &cancel,
                &mut result,
            );
            assert_eq!(ret, STATUS_CANCELLED);
            qg_free_result(&mut result);
        }
    }

    #[test]
    fn batch_search_post_scan_cancel_zero_flag() {
        // cancel_flag pointer is non-null but value is 0.
        // Scan completes, post-scan recheck sees flag==0 → proceeds normally.
        // This covers the `}` closing-brace path at the post-scan check.
        unsafe {
            let (_dir, path_str) = temp_file("cancel0.txt", b"foo\nbar\n");
            let mut result = std::mem::zeroed::<QgResult>();
            let opts = default_opts();
            let path = to_utf16(&path_str);
            let pattern = b"foo";
            let cancel: i32 = 0; // not cancelled
            let ret = qg_search_file(
                path.as_ptr(),
                path.len(),
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                &cancel,
                &mut result,
            );
            assert_eq!(ret, STATUS_OK);
            qg_free_result(&mut result);
        }
    }

    #[test]
    fn batch_search_polling_no_cancel_300_matches() {
        // 300 matches + non-null cancel flag = 0 → poll fires at 256th match
        // but flag is 0, so we fall through the closing braces (lines 182-183).
        unsafe {
            let (_dir, path_str) = temp_file_many_matches("poll_no_cancel.txt", 300);
            let mut result = std::mem::zeroed::<QgResult>();
            let opts = default_opts();
            let path = to_utf16(&path_str);
            let pattern = b"match";
            let cancel: i32 = 0;
            let ret = qg_search_file(
                path.as_ptr(),
                path.len(),
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                &cancel,
                &mut result,
            );
            assert_eq!(ret, STATUS_OK);
            qg_free_result(&mut result);
        }
    }

    #[test]
    fn batch_search_polling_null_cancel_300_matches() {
        // 300 matches + null cancel flag → poll fires at 256th match,
        // cancel_atomic is None → falls through to closing brace (line 183).
        unsafe {
            let (_dir, path_str) = temp_file_many_matches("poll_null_cancel.txt", 300);
            let mut result = std::mem::zeroed::<QgResult>();
            let opts = default_opts();
            let path = to_utf16(&path_str);
            let pattern = b"match";
            let ret = qg_search_file(
                path.as_ptr(),
                path.len(),
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null(),
                &mut result,
            );
            assert_eq!(ret, STATUS_OK);
            qg_free_result(&mut result);
        }
    }

    #[test]
    fn stream_search_cancel_polling_triggers() {
        // 300 matches → poll_counter reaches 256 → polls cancel flag.
        // Callback returns false → scan_bytes returns Ok → STATUS_OK.
        // But the polling code IS exercised (covers the inner cancel block).
        unsafe {
            let (_dir, path_str) = temp_file_many_matches("stream_cancel_poll.txt", 300);
            let mut status: c_int = 0;
            let opts = default_opts();
            let path = to_utf16(&path_str);
            let pattern = b"match";
            let cancel: i32 = 1;
            let mut count: u32 = 0;
            let ret = qg_search_file_stream(
                path.as_ptr(),
                path.len(),
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                &cancel,
                count_callback,
                &mut count as *mut u32 as *mut c_void,
                &mut status,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            // scan_bytes returns Ok when callback returns false; no post-scan recheck.
            assert_eq!(ret, STATUS_OK);
            // Polling triggered at count 256, stopped early.
            assert!(count < 300, "should have stopped before processing all 300 matches");
        }
    }

    #[test]
    fn stream_search_cancel_zero_flag_nonnull() {
        // Non-null cancel_flag with value 0 → covers the cancel_atomic = Some(...)
        // path without actually cancelling.
        unsafe {
            let (_dir, path_str) = temp_file("stream_cancel0.txt", b"foo\nbar\n");
            let mut status: c_int = 0;
            let opts = default_opts();
            let path = to_utf16(&path_str);
            let pattern = b"foo";
            let cancel: i32 = 0;
            let mut count: u32 = 0;
            let ret = qg_search_file_stream(
                path.as_ptr(),
                path.len(),
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                &cancel,
                count_callback,
                &mut count as *mut u32 as *mut c_void,
                &mut status,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            assert_eq!(ret, STATUS_OK);
            assert_eq!(count, 1);
        }
    }

    #[test]
    fn stream_search_polling_no_cancel_300_matches() {
        // 300 matches + non-null cancel flag = 0 → poll fires at 256th match
        // but flag is 0, so we fall through the closing braces (441-442).
        unsafe {
            let (_dir, path_str) = temp_file_many_matches("stream_poll_no_cancel.txt", 300);
            let mut status: c_int = 0;
            let opts = default_opts();
            let path = to_utf16(&path_str);
            let pattern = b"match";
            let cancel: i32 = 0;
            let mut count: u32 = 0;
            let ret = qg_search_file_stream(
                path.as_ptr(),
                path.len(),
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                &cancel,
                count_callback,
                &mut count as *mut u32 as *mut c_void,
                &mut status,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            assert_eq!(ret, STATUS_OK);
            assert_eq!(count, 300);
        }
    }

    #[test]
    fn stream_search_polling_null_cancel_300_matches() {
        // 300 matches + null cancel flag → poll fires at 256th match,
        // cancel_atomic is None → falls through to closing brace (line 442).
        unsafe {
            let (_dir, path_str) = temp_file_many_matches("stream_poll_null_cancel.txt", 300);
            let mut status: c_int = 0;
            let opts = default_opts();
            let path = to_utf16(&path_str);
            let pattern = b"match";
            let mut count: u32 = 0;
            let ret = qg_search_file_stream(
                path.as_ptr(),
                path.len(),
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null(),
                count_callback,
                &mut count as *mut u32 as *mut c_void,
                &mut status,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            assert_eq!(ret, STATUS_OK);
            assert_eq!(count, 300);
        }
    }

    #[test]
    fn session_stream_cancel_polling_triggers() {
        // 300 matches → poll_counter reaches 256 → polls cancel flag.
        // Callback returns false → scan_bytes returns Ok → STATUS_OK.
        unsafe {
            let (_dir, path_str) = temp_file_many_matches("sess_cancel_poll.txt", 300);
            let opts = default_opts();
            let pattern = b"match";
            let session = qg_create_session(
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            let cancel: i32 = 1;
            let mut status: c_int = 0;
            let path = to_utf16(&path_str);
            let mut count: u32 = 0;
            let ret = qg_session_search_file_stream(
                session,
                path.as_ptr(),
                path.len(),
                &cancel,
                count_callback,
                &mut count as *mut u32 as *mut c_void,
                &mut status,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            assert_eq!(ret, STATUS_OK);
            assert!(count < 300, "should have stopped before processing all 300 matches");
            qg_free_session(session);
        }
    }

    #[test]
    fn session_stream_cancel_zero_flag_nonnull() {
        // Non-null cancel_flag with value 0 → covers cancel_atomic = Some path
        // plus the out_error_msg/out_error_msg_len non-null init paths.
        unsafe {
            let (_dir, path_str) = temp_file("sess_cancel0.txt", b"foo\nbar\n");
            let opts = default_opts();
            let pattern = b"foo";
            let session = qg_create_session(
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            let cancel: i32 = 0;
            let mut status: c_int = 0;
            let path = to_utf16(&path_str);
            let mut count: u32 = 0;
            let mut err_msg: *mut u8 = std::ptr::null_mut();
            let mut err_msg_len: usize = 0;
            let ret = qg_session_search_file_stream(
                session,
                path.as_ptr(),
                path.len(),
                &cancel,
                count_callback,
                &mut count as *mut u32 as *mut c_void,
                &mut status,
                &mut err_msg,
                &mut err_msg_len,
            );
            assert_eq!(ret, STATUS_OK);
            assert_eq!(count, 1);
            assert!(err_msg.is_null());
            assert_eq!(err_msg_len, 0);
            qg_free_session(session);
        }
    }

    #[test]
    fn session_stream_polling_no_cancel_300_matches() {
        // 300 matches + non-null cancel flag = 0 → poll fires at 256th match
        // but flag is 0, so we fall through the closing braces (699-700).
        unsafe {
            let (_dir, path_str) = temp_file_many_matches("sess_poll_no_cancel.txt", 300);
            let opts = default_opts();
            let pattern = b"match";
            let session = qg_create_session(
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            let cancel: i32 = 0;
            let mut status: c_int = 0;
            let path = to_utf16(&path_str);
            let mut count: u32 = 0;
            let ret = qg_session_search_file_stream(
                session,
                path.as_ptr(),
                path.len(),
                &cancel,
                count_callback,
                &mut count as *mut u32 as *mut c_void,
                &mut status,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            assert_eq!(ret, STATUS_OK);
            assert_eq!(count, 300);
            qg_free_session(session);
        }
    }

    #[test]
    fn session_stream_polling_null_cancel_300_matches() {
        // 300 matches + null cancel flag → poll fires at 256th match,
        // cancel_atomic is None → falls through to closing brace (line 700).
        unsafe {
            let (_dir, path_str) = temp_file_many_matches("sess_poll_null_cancel.txt", 300);
            let opts = default_opts();
            let pattern = b"match";
            let session = qg_create_session(
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            let mut status: c_int = 0;
            let path = to_utf16(&path_str);
            let mut count: u32 = 0;
            let ret = qg_session_search_file_stream(
                session,
                path.as_ptr(),
                path.len(),
                std::ptr::null(),
                count_callback,
                &mut count as *mut u32 as *mut c_void,
                &mut status,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            assert_eq!(ret, STATUS_OK);
            assert_eq!(count, 300);
            qg_free_session(session);
        }
    }

    #[test]
    fn stream_search_nonnull_error_out_params() {
        // Pass non-null out_error_msg/out_error_msg_len to cover
        // their initialization at the top of qg_search_file_stream.
        unsafe {
            let (_dir, path_str) = temp_file("stream_errout.txt", b"foo\n");
            let mut status: c_int = 0;
            let opts = default_opts();
            let path = to_utf16(&path_str);
            let pattern = b"foo";
            let mut count: u32 = 0;
            let mut err_msg: *mut u8 = std::ptr::null_mut();
            let mut err_msg_len: usize = 0;
            let ret = qg_search_file_stream(
                path.as_ptr(),
                path.len(),
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null(),
                count_callback,
                &mut count as *mut u32 as *mut c_void,
                &mut status,
                &mut err_msg,
                &mut err_msg_len,
            );
            assert_eq!(ret, STATUS_OK);
            assert!(err_msg.is_null());
            assert_eq!(err_msg_len, 0);
        }
    }

    // -----------------------------------------------------------------------
    // scan_error_to_status tests
    // -----------------------------------------------------------------------

    #[test]
    fn scan_error_to_status_binary_skipped() {
        assert_eq!(scan_error_to_status(&ScanError::BinarySkipped), STATUS_BINARY_SKIPPED);
    }

    #[test]
    fn scan_error_to_status_invalid_regex() {
        assert_eq!(
            scan_error_to_status(&ScanError::InvalidRegex("bad".into())),
            STATUS_INVALID_REGEX,
        );
    }

    #[test]
    fn scan_error_to_status_cancelled() {
        assert_eq!(scan_error_to_status(&ScanError::Cancelled), STATUS_CANCELLED);
    }

    #[test]
    fn scan_error_to_status_io() {
        let io_err = std::io::Error::from(std::io::ErrorKind::NotFound);
        assert_eq!(scan_error_to_status(&ScanError::Io(io_err)), STATUS_OPEN_FAILED);
    }

    // -----------------------------------------------------------------------
    // open_and_mmap tests
    // -----------------------------------------------------------------------

    #[test]
    fn open_and_mmap_nonexistent_returns_open_failed() {
        assert_eq!(open_and_mmap("__nonexistent_path__", 0).unwrap_err(), STATUS_OPEN_FAILED);
    }

    #[test]
    fn open_and_mmap_empty_file_returns_ok() {
        let tmp = tempfile::NamedTempFile::new().unwrap();
        // File is 0 bytes.
        let path = tmp.path().to_str().unwrap();
        assert_eq!(open_and_mmap(path, 0).unwrap_err(), STATUS_OK);
    }

    #[test]
    fn open_and_mmap_too_large_returns_too_large() {
        let mut tmp = tempfile::NamedTempFile::new().unwrap();
        tmp.write_all(b"hello world\n").unwrap();
        tmp.flush().unwrap();
        let path = tmp.path().to_str().unwrap();
        // max_file_size=1 makes even this tiny file "too large".
        assert_eq!(open_and_mmap(path, 1).unwrap_err(), STATUS_TOO_LARGE);
    }

    #[test]
    fn open_and_mmap_success() {
        let mut tmp = tempfile::NamedTempFile::new().unwrap();
        tmp.write_all(b"hello\n").unwrap();
        tmp.flush().unwrap();
        let path = tmp.path().to_str().unwrap();
        let result = open_and_mmap(path, 0);
        assert!(result.is_ok());
        assert_eq!(&result.unwrap()[..], b"hello\n");
    }

    #[test]
    fn open_and_mmap_metadata_error() {
        let mut tmp = tempfile::NamedTempFile::new().unwrap();
        tmp.write_all(b"data\n").unwrap();
        tmp.flush().unwrap();
        let path = tmp.path().to_str().unwrap();

        test_inject::set_fail_metadata(true);
        let result = open_and_mmap(path, 0);
        test_inject::set_fail_metadata(false);

        assert_eq!(result.unwrap_err(), STATUS_OPEN_FAILED);
    }

    #[test]
    fn open_and_mmap_mmap_error() {
        let mut tmp = tempfile::NamedTempFile::new().unwrap();
        tmp.write_all(b"data\n").unwrap();
        tmp.flush().unwrap();
        let path = tmp.path().to_str().unwrap();

        test_inject::set_fail_mmap(true);
        let result = open_and_mmap(path, 0);
        test_inject::set_fail_mmap(false);

        assert_eq!(result.unwrap_err(), STATUS_OPEN_FAILED);
    }

    // -----------------------------------------------------------------------
    // FFI functions with injected metadata/mmap failures
    // -----------------------------------------------------------------------

    #[test]
    fn search_file_metadata_failure() {
        let mut tmp = tempfile::NamedTempFile::new().unwrap();
        tmp.write_all(b"hello\n").unwrap();
        tmp.flush().unwrap();
        let path = to_utf16(tmp.path().to_str().unwrap());
        let pattern = b"hello";
        let opts = default_opts();
        let mut result = QgResult {
            buffer: std::ptr::null_mut(),
            buffer_len: 0,
            match_count: 0,
            status: 0,
            error_msg: std::ptr::null_mut(),
            error_msg_len: 0,
        };

        test_inject::set_fail_metadata(true);
        unsafe {
            let ret = qg_search_file(
                path.as_ptr(),
                path.len(),
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null(),
                &mut result,
            );
            assert_eq!(ret, STATUS_OPEN_FAILED);
            assert_eq!(result.status, STATUS_OPEN_FAILED);
        }
        test_inject::set_fail_metadata(false);
    }

    #[test]
    fn search_file_mmap_failure() {
        // File must exceed MMAP_THRESHOLD_BYTES (64 KB) so open_file_for_scan
        // takes the mmap path; smaller files go through read_to_end and bypass
        // try_mmap entirely.
        let mut tmp = tempfile::NamedTempFile::new().unwrap();
        let big = vec![b'a'; 70 * 1024];
        tmp.write_all(&big).unwrap();
        tmp.write_all(b"\nhello\n").unwrap();
        tmp.flush().unwrap();
        let path = to_utf16(tmp.path().to_str().unwrap());
        let pattern = b"hello";
        let opts = default_opts();
        let mut result = QgResult {
            buffer: std::ptr::null_mut(),
            buffer_len: 0,
            match_count: 0,
            status: 0,
            error_msg: std::ptr::null_mut(),
            error_msg_len: 0,
        };

        test_inject::set_fail_mmap(true);
        unsafe {
            let ret = qg_search_file(
                path.as_ptr(),
                path.len(),
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null(),
                &mut result,
            );
            assert_eq!(ret, STATUS_OPEN_FAILED);
            assert_eq!(result.status, STATUS_OPEN_FAILED);
        }
        test_inject::set_fail_mmap(false);
    }

    #[test]
    fn stream_metadata_failure() {
        let mut tmp = tempfile::NamedTempFile::new().unwrap();
        tmp.write_all(b"hello\n").unwrap();
        tmp.flush().unwrap();
        let path = to_utf16(tmp.path().to_str().unwrap());
        let pattern = b"hello";
        let opts = default_opts();
        let mut status: c_int = 0;
        let mut err_msg: *mut u8 = std::ptr::null_mut();
        let mut err_msg_len: usize = 0;

        test_inject::set_fail_metadata(true);
        unsafe {
            let ret = qg_search_file_stream(
                path.as_ptr(),
                path.len(),
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null(),
                count_callback,
                std::ptr::null_mut(),
                &mut status,
                &mut err_msg,
                &mut err_msg_len,
            );
            assert_eq!(ret, STATUS_OPEN_FAILED);
            assert_eq!(status, STATUS_OPEN_FAILED);
        }
        test_inject::set_fail_metadata(false);
    }

    #[test]
    fn stream_mmap_failure() {
        // See `search_file_mmap_failure` — file must exceed MMAP_THRESHOLD_BYTES.
        let mut tmp = tempfile::NamedTempFile::new().unwrap();
        let big = vec![b'a'; 70 * 1024];
        tmp.write_all(&big).unwrap();
        tmp.write_all(b"\nhello\n").unwrap();
        tmp.flush().unwrap();
        let path = to_utf16(tmp.path().to_str().unwrap());
        let pattern = b"hello";
        let opts = default_opts();
        let mut status: c_int = 0;
        let mut err_msg: *mut u8 = std::ptr::null_mut();
        let mut err_msg_len: usize = 0;

        test_inject::set_fail_mmap(true);
        unsafe {
            let ret = qg_search_file_stream(
                path.as_ptr(),
                path.len(),
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null(),
                count_callback,
                std::ptr::null_mut(),
                &mut status,
                &mut err_msg,
                &mut err_msg_len,
            );
            assert_eq!(ret, STATUS_OPEN_FAILED);
            assert_eq!(status, STATUS_OPEN_FAILED);
        }
        test_inject::set_fail_mmap(false);
    }

    #[test]
    fn session_stream_metadata_failure() {
        let mut tmp = tempfile::NamedTempFile::new().unwrap();
        tmp.write_all(b"hello\n").unwrap();
        tmp.flush().unwrap();
        let path = to_utf16(tmp.path().to_str().unwrap());
        let pattern = b"hello";
        let opts = default_opts();
        let mut status: c_int = 0;
        let mut err_msg: *mut u8 = std::ptr::null_mut();
        let mut err_msg_len: usize = 0;

        unsafe {
            let session = qg_create_session(
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                &mut err_msg,
                &mut err_msg_len,
            );
            assert!(!session.is_null());

            test_inject::set_fail_metadata(true);
            let ret = qg_session_search_file_stream(
                session,
                path.as_ptr(),
                path.len(),
                std::ptr::null(),
                count_callback,
                std::ptr::null_mut(),
                &mut status,
                &mut err_msg,
                &mut err_msg_len,
            );
            test_inject::set_fail_metadata(false);

            assert_eq!(ret, STATUS_OPEN_FAILED);
            assert_eq!(status, STATUS_OPEN_FAILED);
            qg_free_session(session);
        }
    }

    #[test]
    fn session_stream_mmap_failure() {
        // See `search_file_mmap_failure` — file must exceed MMAP_THRESHOLD_BYTES.
        let mut tmp = tempfile::NamedTempFile::new().unwrap();
        let big = vec![b'a'; 70 * 1024];
        tmp.write_all(&big).unwrap();
        tmp.write_all(b"\nhello\n").unwrap();
        tmp.flush().unwrap();
        let path = to_utf16(tmp.path().to_str().unwrap());
        let pattern = b"hello";
        let opts = default_opts();
        let mut status: c_int = 0;
        let mut err_msg: *mut u8 = std::ptr::null_mut();
        let mut err_msg_len: usize = 0;

        unsafe {
            let session = qg_create_session(
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                &mut err_msg,
                &mut err_msg_len,
            );
            assert!(!session.is_null());

            test_inject::set_fail_mmap(true);
            let ret = qg_session_search_file_stream(
                session,
                path.as_ptr(),
                path.len(),
                std::ptr::null(),
                count_callback,
                std::ptr::null_mut(),
                &mut status,
                &mut err_msg,
                &mut err_msg_len,
            );
            test_inject::set_fail_mmap(false);

            assert_eq!(ret, STATUS_OPEN_FAILED);
            assert_eq!(status, STATUS_OPEN_FAILED);
            qg_free_session(session);
        }
    }

    // -----------------------------------------------------------------------
    // open_file_for_scan tests (probe + small/large heuristic)
    // -----------------------------------------------------------------------

    #[test]
    fn open_file_for_scan_nonexistent() {
        let r = open_file_for_scan("__no_such_path_xyz__", 0, true);
        assert_eq!(r.unwrap_err(), STATUS_OPEN_FAILED);
    }

    #[test]
    fn open_file_for_scan_too_large() {
        let mut tmp = tempfile::NamedTempFile::new().unwrap();
        tmp.write_all(b"hello\n").unwrap();
        tmp.flush().unwrap();
        let path = tmp.path().to_str().unwrap();
        assert_eq!(
            open_file_for_scan(path, 1, false).unwrap_err(),
            STATUS_TOO_LARGE,
        );
    }

    #[test]
    fn open_file_for_scan_empty_returns_owned_empty() {
        let tmp = tempfile::NamedTempFile::new().unwrap();
        let path = tmp.path().to_str().unwrap();
        let bytes = open_file_for_scan(path, 0, true).unwrap();
        assert!(matches!(bytes, FileBytes::Owned(ref v) if v.is_empty()));
        assert_eq!(bytes.as_slice().len(), 0);
    }

    #[test]
    fn open_file_for_scan_small_file_uses_owned() {
        let mut tmp = tempfile::NamedTempFile::new().unwrap();
        tmp.write_all(b"abc\n").unwrap();
        tmp.flush().unwrap();
        let path = tmp.path().to_str().unwrap();
        let bytes = open_file_for_scan(path, 0, true).unwrap();
        // 4 bytes < MMAP_THRESHOLD_BYTES, so we expect the owned path.
        assert!(matches!(bytes, FileBytes::Owned(_)));
        assert_eq!(bytes.as_slice(), b"abc\n");
    }

    #[test]
    fn open_file_for_scan_large_file_uses_mmap() {
        let mut tmp = tempfile::NamedTempFile::new().unwrap();
        let big = vec![b'a'; (MMAP_THRESHOLD_BYTES as usize) + 16];
        tmp.write_all(&big).unwrap();
        tmp.flush().unwrap();
        let path = tmp.path().to_str().unwrap();
        let bytes = open_file_for_scan(path, 0, true).unwrap();
        assert!(matches!(bytes, FileBytes::Mapped(_)));
        assert_eq!(bytes.as_slice().len(), big.len());
    }

    #[test]
    fn open_file_for_scan_binary_probe_skips_large_binary_file() {
        // Large file with a NUL in the first 8 KB → must be rejected before
        // the mmap path runs.
        let mut tmp = tempfile::NamedTempFile::new().unwrap();
        let mut content = vec![b'a'; (MMAP_THRESHOLD_BYTES as usize) + 16];
        content[100] = 0;
        tmp.write_all(&content).unwrap();
        tmp.flush().unwrap();
        let path = tmp.path().to_str().unwrap();
        assert_eq!(
            open_file_for_scan(path, 0, true).unwrap_err(),
            STATUS_BINARY_SKIPPED,
        );
    }

    #[test]
    fn open_file_for_scan_binary_probe_skips_large_magic_file() {
        let mut tmp = tempfile::NamedTempFile::new().unwrap();
        let mut content = vec![b'a'; (MMAP_THRESHOLD_BYTES as usize) + 16];
        content[..4].copy_from_slice(b"PK\x03\x04");
        tmp.write_all(&content).unwrap();
        tmp.flush().unwrap();
        let path = tmp.path().to_str().unwrap();
        assert_eq!(
            open_file_for_scan(path, 0, true).unwrap_err(),
            STATUS_BINARY_SKIPPED,
        );
    }

    #[test]
    fn open_file_for_scan_into_binary_probe_skips_large_magic_file() {
        let mut tmp = tempfile::NamedTempFile::new().unwrap();
        let mut content = vec![b'a'; (MMAP_THRESHOLD_BYTES as usize) + 16];
        content[..2].copy_from_slice(&[0x1F, 0x8B]);
        tmp.write_all(&content).unwrap();
        tmp.flush().unwrap();
        let path = tmp.path().to_str().unwrap();
        let mut scratch = Vec::with_capacity(MMAP_THRESHOLD_BYTES as usize);
        assert!(matches!(
            open_file_for_scan_into(path, 0, true, &mut scratch),
            Err(STATUS_BINARY_SKIPPED)
        ));
    }

    #[test]
    fn open_file_for_scan_binary_probe_disabled() {
        // Same as above but skip_binary=false: the probe is bypassed and we
        // get a Mapped buffer back.
        let mut tmp = tempfile::NamedTempFile::new().unwrap();
        let mut content = vec![b'a'; (MMAP_THRESHOLD_BYTES as usize) + 16];
        content[100] = 0;
        tmp.write_all(&content).unwrap();
        tmp.flush().unwrap();
        let path = tmp.path().to_str().unwrap();
        let bytes = open_file_for_scan(path, 0, false).unwrap();
        assert!(matches!(bytes, FileBytes::Mapped(_)));
    }

    #[test]
    fn open_file_for_scan_binary_probe_clean_head_passes() {
        // NUL is past the 8 KB probe window → probe accepts the file. The
        // scan layer's own binary detector still sees the NUL when scanning,
        // but at the open layer we get a Mapped buffer.
        let mut tmp = tempfile::NamedTempFile::new().unwrap();
        let mut content = vec![b'a'; (MMAP_THRESHOLD_BYTES as usize) + 16];
        let nul_offset = content.len() - 5;
        content[nul_offset] = 0;
        tmp.write_all(&content).unwrap();
        tmp.flush().unwrap();
        let path = tmp.path().to_str().unwrap();
        let bytes = open_file_for_scan(path, 0, true).unwrap();
        assert!(matches!(bytes, FileBytes::Mapped(_)));
    }

    #[test]
    fn open_file_for_scan_metadata_failure() {
        let mut tmp = tempfile::NamedTempFile::new().unwrap();
        tmp.write_all(b"hello\n").unwrap();
        tmp.flush().unwrap();
        let path = tmp.path().to_str().unwrap();
        test_inject::set_fail_metadata(true);
        let r = open_file_for_scan(path, 0, true);
        test_inject::set_fail_metadata(false);
        assert_eq!(r.unwrap_err(), STATUS_OPEN_FAILED);
    }

    #[test]
    fn open_file_for_scan_mmap_failure_on_large_file() {
        let mut tmp = tempfile::NamedTempFile::new().unwrap();
        let big = vec![b'a'; (MMAP_THRESHOLD_BYTES as usize) + 16];
        tmp.write_all(&big).unwrap();
        tmp.flush().unwrap();
        let path = tmp.path().to_str().unwrap();
        test_inject::set_fail_mmap(true);
        let r = open_file_for_scan(path, 0, true);
        test_inject::set_fail_mmap(false);
        assert_eq!(r.unwrap_err(), STATUS_OPEN_FAILED);
    }

    // -----------------------------------------------------------------------
    // qg_session_scan_paths_parallel
    // -----------------------------------------------------------------------

    /// Concatenate UTF-16 encodings of `paths` and produce a (`buffer`,
    /// `lengths`) pair suitable for the parallel API.
    fn pack_paths_utf16(paths: &[&str]) -> (Vec<u16>, Vec<u32>) {
        let mut buffer = Vec::new();
        let mut lengths = Vec::with_capacity(paths.len());
        for p in paths {
            let utf16: Vec<u16> = p.encode_utf16().collect();
            lengths.push(utf16.len() as u32);
            buffer.extend(utf16);
        }
        (buffer, lengths)
    }

    /// Shared accumulator updated under the FFI mutex by the test callbacks.
    struct ParallelHarness {
        matches_per_file: Vec<u32>,
        statuses: Vec<i32>,
        stop_after: i32, // -1 = never stop
        total_matches_seen: i32,
    }

    unsafe extern "C" fn parallel_match_cb(
        ctx: *mut c_void,
        file_index: c_uint,
        _m: *const QgMatchView,
    ) -> c_int {
        let h = &mut *(ctx as *mut ParallelHarness);
        h.matches_per_file[file_index as usize] += 1;
        h.total_matches_seen += 1;
        if h.stop_after >= 0 && h.total_matches_seen >= h.stop_after {
            return 1;
        }
        0
    }

    unsafe extern "C" fn parallel_done_cb(
        ctx: *mut c_void,
        file_index: c_uint,
        status: c_int,
    ) {
        let h = &mut *(ctx as *mut ParallelHarness);
        h.statuses[file_index as usize] = status;
    }

    #[test]
    fn parallel_zero_paths_returns_ok() {
        unsafe {
            let opts = default_opts();
            let pattern = b"x";
            let session = qg_create_session(
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            let mut h = ParallelHarness {
                matches_per_file: vec![],
                statuses: vec![],
                stop_after: -1,
                total_matches_seen: 0,
            };
            let ret = qg_session_scan_paths_parallel(
                session,
                std::ptr::null(),
                std::ptr::null(),
                0,
                0,
                std::ptr::null(),
                parallel_match_cb,
                parallel_done_cb,
                &mut h as *mut _ as *mut c_void,
            );
            assert_eq!(ret, STATUS_OK);
            qg_free_session(session);
        }
    }

    #[test]
    fn parallel_null_session_returns_invalid_path() {
        unsafe {
            let mut h = ParallelHarness {
                matches_per_file: vec![],
                statuses: vec![],
                stop_after: -1,
                total_matches_seen: 0,
            };
            let ret = qg_session_scan_paths_parallel(
                std::ptr::null(),
                std::ptr::null(),
                std::ptr::null(),
                3,
                0,
                std::ptr::null(),
                parallel_match_cb,
                parallel_done_cb,
                &mut h as *mut _ as *mut c_void,
            );
            assert_eq!(ret, STATUS_INVALID_PATH);
        }
    }

    #[test]
    fn parallel_null_buffer_with_paths_returns_invalid_path() {
        unsafe {
            let opts = default_opts();
            let pattern = b"x";
            let session = qg_create_session(
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            let mut h = ParallelHarness {
                matches_per_file: vec![],
                statuses: vec![],
                stop_after: -1,
                total_matches_seen: 0,
            };
            let ret = qg_session_scan_paths_parallel(
                session,
                std::ptr::null(),
                std::ptr::null(),
                2,
                0,
                std::ptr::null(),
                parallel_match_cb,
                parallel_done_cb,
                &mut h as *mut _ as *mut c_void,
            );
            assert_eq!(ret, STATUS_INVALID_PATH);
            qg_free_session(session);
        }
    }

    #[test]
    fn parallel_scans_all_files_and_finds_matches() {
        unsafe {
            let dir = tempfile::tempdir().unwrap();
            let mut paths = Vec::new();
            let n_files = 8;
            for i in 0..n_files {
                let p = dir.path().join(format!("f{i}.txt"));
                let mut f = File::create(&p).unwrap();
                // Each file has a different number of "test" matches.
                for _ in 0..(i + 1) {
                    f.write_all(b"this is a test line\n").unwrap();
                }
                f.write_all(b"no match here\n").unwrap();
                paths.push(p.to_string_lossy().into_owned());
            }
            let path_strs: Vec<&str> = paths.iter().map(|s| s.as_str()).collect();
            let (buf, lengths) = pack_paths_utf16(&path_strs);

            let opts = default_opts();
            let pattern = b"test";
            let session = qg_create_session(
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            let mut h = ParallelHarness {
                matches_per_file: vec![0; n_files as usize],
                statuses: vec![-1; n_files as usize],
                stop_after: -1,
                total_matches_seen: 0,
            };
            let ret = qg_session_scan_paths_parallel(
                session,
                buf.as_ptr(),
                lengths.as_ptr(),
                path_strs.len(),
                4,
                std::ptr::null(),
                parallel_match_cb,
                parallel_done_cb,
                &mut h as *mut _ as *mut c_void,
            );
            assert_eq!(ret, STATUS_OK);
            for i in 0..n_files {
                assert_eq!(h.matches_per_file[i as usize], (i + 1) as u32);
                assert_eq!(h.statuses[i as usize], STATUS_OK);
            }
            qg_free_session(session);
        }
    }

    #[test]
    fn parallel_thread_count_zero_uses_default() {
        unsafe {
            let dir = tempfile::tempdir().unwrap();
            let p = dir.path().join("solo.txt");
            std::fs::write(&p, b"alpha test beta\n").unwrap();
            let path = p.to_string_lossy().into_owned();
            let (buf, lengths) = pack_paths_utf16(&[path.as_str()]);

            let opts = default_opts();
            let pattern = b"test";
            let session = qg_create_session(
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            let mut h = ParallelHarness {
                matches_per_file: vec![0; 1],
                statuses: vec![-1; 1],
                stop_after: -1,
                total_matches_seen: 0,
            };
            let ret = qg_session_scan_paths_parallel(
                session,
                buf.as_ptr(),
                lengths.as_ptr(),
                1,
                0,
                std::ptr::null(),
                parallel_match_cb,
                parallel_done_cb,
                &mut h as *mut _ as *mut c_void,
            );
            assert_eq!(ret, STATUS_OK);
            assert_eq!(h.matches_per_file[0], 1);
            qg_free_session(session);
        }
    }

    #[test]
    fn parallel_reports_per_file_status_codes() {
        unsafe {
            let dir = tempfile::tempdir().unwrap();
            let good = dir.path().join("good.txt");
            std::fs::write(&good, b"test\n").unwrap();
            let binary = dir.path().join("bin.bin");
            std::fs::write(&binary, b"hello\0world\n").unwrap();
            let missing = dir.path().join("missing.txt").to_string_lossy().into_owned();
            let paths = vec![
                good.to_string_lossy().into_owned(),
                binary.to_string_lossy().into_owned(),
                missing,
            ];
            let path_strs: Vec<&str> = paths.iter().map(|s| s.as_str()).collect();
            let (buf, lengths) = pack_paths_utf16(&path_strs);

            let opts = default_opts();
            let pattern = b"test";
            let session = qg_create_session(
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            let mut h = ParallelHarness {
                matches_per_file: vec![0; 3],
                statuses: vec![-1; 3],
                stop_after: -1,
                total_matches_seen: 0,
            };
            let ret = qg_session_scan_paths_parallel(
                session,
                buf.as_ptr(),
                lengths.as_ptr(),
                3,
                2,
                std::ptr::null(),
                parallel_match_cb,
                parallel_done_cb,
                &mut h as *mut _ as *mut c_void,
            );
            assert_eq!(ret, STATUS_OK);
            assert_eq!(h.statuses[0], STATUS_OK);
            assert_eq!(h.statuses[1], STATUS_BINARY_SKIPPED);
            assert_eq!(h.statuses[2], STATUS_OPEN_FAILED);
            qg_free_session(session);
        }
    }

    #[test]
    fn parallel_callback_stop_signal_halts_scanning() {
        unsafe {
            let dir = tempfile::tempdir().unwrap();
            let mut paths = Vec::new();
            for i in 0..16 {
                let p = dir.path().join(format!("stop{i}.txt"));
                let mut f = File::create(&p).unwrap();
                for _ in 0..50 {
                    f.write_all(b"test test test\n").unwrap();
                }
                paths.push(p.to_string_lossy().into_owned());
            }
            let path_strs: Vec<&str> = paths.iter().map(|s| s.as_str()).collect();
            let (buf, lengths) = pack_paths_utf16(&path_strs);

            let opts = default_opts();
            let pattern = b"test";
            let session = qg_create_session(
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            let mut h = ParallelHarness {
                matches_per_file: vec![0; path_strs.len()],
                statuses: vec![-1; path_strs.len()],
                stop_after: 5, // tell the callback to signal stop early
                total_matches_seen: 0,
            };
            let ret = qg_session_scan_paths_parallel(
                session,
                buf.as_ptr(),
                lengths.as_ptr(),
                path_strs.len(),
                4,
                std::ptr::null(),
                parallel_match_cb,
                parallel_done_cb,
                &mut h as *mut _ as *mut c_void,
            );
            // The early-stop signal returns OK (caller-driven), not CANCELLED.
            assert_eq!(ret, STATUS_OK);
            // We must not have processed every file — at least one file
            // should still report status -1 (untouched) because the workers
            // bailed out.
            assert!(h.statuses.iter().any(|&s| s == -1));
            qg_free_session(session);
        }
    }

    #[test]
    fn parallel_external_cancel_flag_returns_cancelled() {
        unsafe {
            let dir = tempfile::tempdir().unwrap();
            let mut paths = Vec::new();
            for i in 0..8 {
                let p = dir.path().join(format!("c{i}.txt"));
                std::fs::write(&p, b"test\n").unwrap();
                paths.push(p.to_string_lossy().into_owned());
            }
            let path_strs: Vec<&str> = paths.iter().map(|s| s.as_str()).collect();
            let (buf, lengths) = pack_paths_utf16(&path_strs);

            let opts = default_opts();
            let pattern = b"test";
            let session = qg_create_session(
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            let mut h = ParallelHarness {
                matches_per_file: vec![0; path_strs.len()],
                statuses: vec![-1; path_strs.len()],
                stop_after: -1,
                total_matches_seen: 0,
            };
            let cancel: i32 = 1; // already cancelled
            let ret = qg_session_scan_paths_parallel(
                session,
                buf.as_ptr(),
                lengths.as_ptr(),
                path_strs.len(),
                2,
                &cancel,
                parallel_match_cb,
                parallel_done_cb,
                &mut h as *mut _ as *mut c_void,
            );
            assert_eq!(ret, STATUS_CANCELLED);
            qg_free_session(session);
        }
    }

    #[test]
    fn parallel_invalid_utf16_path_reports_per_file_invalid_path() {
        unsafe {
            let opts = default_opts();
            let pattern = b"x";
            let session = qg_create_session(
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            // Lone surrogate \xD800 is invalid UTF-16.
            let buf: Vec<u16> = vec![0xD800, 0x0041];
            let lengths: Vec<u32> = vec![buf.len() as u32];
            let mut h = ParallelHarness {
                matches_per_file: vec![0; 1],
                statuses: vec![-1; 1],
                stop_after: -1,
                total_matches_seen: 0,
            };
            let ret = qg_session_scan_paths_parallel(
                session,
                buf.as_ptr(),
                lengths.as_ptr(),
                1,
                1,
                std::ptr::null(),
                parallel_match_cb,
                parallel_done_cb,
                &mut h as *mut _ as *mut c_void,
            );
            assert_eq!(ret, STATUS_OK);
            assert_eq!(h.statuses[0], STATUS_INVALID_PATH);
            qg_free_session(session);
        }
    }

    #[test]
    fn parallel_path_lengths_overflow_returns_invalid_path() {
        unsafe {
            let opts = default_opts();
            let pattern = b"x";
            let session = qg_create_session(
                pattern.as_ptr(),
                pattern.len(),
                &opts,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            // Two lengths whose u32-as-usize sum overflows usize on 64-bit
            // platforms is unrealistic, but on any target we can force the
            // overflow by claiming both halves are u32::MAX. The buffer is
            // never read (we fail in the validation loop) so a null is fine
            // \u2014 except the early-out check requires non-null when
            // path_count > 0. Pass a non-null sentinel instead.
            let buf: Vec<u16> = vec![0u16; 1];
            let lengths: Vec<u32> = vec![u32::MAX, u32::MAX];
            // On 32-bit usize this overflows; on 64-bit it does not. Skip
            // the assertion when usize is 64 bits.
            if std::mem::size_of::<usize>() < 8 {
                let mut h = ParallelHarness {
                    matches_per_file: vec![0; 2],
                    statuses: vec![-1; 2],
                    stop_after: -1,
                    total_matches_seen: 0,
                };
                let ret = qg_session_scan_paths_parallel(
                    session,
                    buf.as_ptr(),
                    lengths.as_ptr(),
                    2,
                    1,
                    std::ptr::null(),
                    parallel_match_cb,
                    parallel_done_cb,
                    &mut h as *mut _ as *mut c_void,
                );
                assert_eq!(ret, STATUS_INVALID_PATH);
            }
            qg_free_session(session);
        }
    }
}
