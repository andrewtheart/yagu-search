//! C ABI exposed to .NET via P/Invoke.
//!
//! Lifecycle:
//!   1. C# calls `qg_search_file` with a UTF-16 path, UTF-8 pattern, options,
//!      and a `cancel_flag` pointer (an atomic 32-bit int it owns).
//!   2. Rust opens the file, mmap's it (or reads to a Vec for small files),
//!      scans, and produces a single packed result buffer.
//!   3. The buffer pointer + length are returned via out-params; ownership
//!      transfers to the caller, which must release it via `qg_free_result`.

use crate::scan::{scan_bytes, MatchRecord, ScanError, ScanOptions};
use memmap2::Mmap;
use std::fs::File;
use std::os::raw::{c_int, c_uchar, c_uint, c_ulonglong, c_ushort, c_void};
use std::sync::atomic::{AtomicI32, Ordering};

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
    };

    // Open + size check + mmap (or small read).
    let file = match File::open(&path) {
        Ok(f) => f,
        Err(_) => {
            result_slot.status = STATUS_OPEN_FAILED;
            return STATUS_OPEN_FAILED;
        }
    };
    let metadata = match file.metadata() {
        Ok(m) => m,
        Err(_) => {
            result_slot.status = STATUS_OPEN_FAILED;
            return STATUS_OPEN_FAILED;
        }
    };
    let file_size = metadata.len();
    if opts_in.max_file_size != 0 && file_size > opts_in.max_file_size {
        result_slot.status = STATUS_TOO_LARGE;
        return STATUS_TOO_LARGE;
    }
    if file_size == 0 {
        // Empty file -> zero matches, success.
        return STATUS_OK;
    }

    // mmap. memmap2 is read-only by default via Mmap::map.
    let mmap = match Mmap::map(&file) {
        Ok(m) => m,
        Err(_) => {
            result_slot.status = STATUS_OPEN_FAILED;
            return STATUS_OPEN_FAILED;
        }
    };
    let bytes: &[u8] = &mmap;

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
    let mut poll_counter: u32 = 0;

    let scan_result = scan_bytes(bytes, pattern, &scan_opts, |rec| {
        poll_counter = poll_counter.wrapping_add(1);
        if poll_counter & 0xff == 0 {
            if let Some(flag) = cancel_atomic {
                if flag.load(Ordering::Relaxed) != 0 {
                    return false;
                }
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

    match scan_result {
        Ok(_) => {}
        Err(ScanError::BinarySkipped) => {
            result_slot.status = STATUS_BINARY_SKIPPED;
            return STATUS_BINARY_SKIPPED;
        }
        Err(ScanError::InvalidRegex(msg)) => {
            let mut bytes = msg.into_bytes();
            bytes.shrink_to_fit();
            let len = bytes.len();
            debug_assert_eq!(len, bytes.capacity());
            let ptr = bytes.as_mut_ptr();
            std::mem::forget(bytes);
            result_slot.error_msg = ptr;
            result_slot.error_msg_len = len;
            result_slot.status = STATUS_INVALID_REGEX;
            return STATUS_INVALID_REGEX;
        }
        // Cancelled/Io are never produced by scan_bytes on mmap'd data,
        // but handle defensively.
        Err(_) => {
            result_slot.status = STATUS_OPEN_FAILED;
            return STATUS_OPEN_FAILED;
        }
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
    2
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
    };

    let file = match File::open(&path) {
        Ok(f) => f,
        Err(_) => return set_status(STATUS_OPEN_FAILED),
    };
    let metadata = match file.metadata() {
        Ok(m) => m,
        Err(_) => return set_status(STATUS_OPEN_FAILED),
    };
    let file_size = metadata.len();
    if opts_in.max_file_size != 0 && file_size > opts_in.max_file_size {
        return set_status(STATUS_TOO_LARGE);
    }
    if file_size == 0 {
        return set_status(STATUS_OK);
    }

    let mmap = match Mmap::map(&file) {
        Ok(m) => m,
        Err(_) => return set_status(STATUS_OPEN_FAILED),
    };
    let bytes: &[u8] = &mmap;

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
    let mut poll_counter: u32 = 0;

    let scan_result = scan_bytes(bytes, pattern, &scan_opts, |rec: MatchRecord| {
        poll_counter = poll_counter.wrapping_add(1);
        if poll_counter & 0xff == 0 {
            if let Some(flag) = cancel_atomic {
                if flag.load(Ordering::Relaxed) != 0 {
                    return false;
                }
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
        Err(ScanError::BinarySkipped) => set_status(STATUS_BINARY_SKIPPED),
        Err(ScanError::InvalidRegex(msg)) => {
            if !out_error_msg.is_null() && !out_error_msg_len.is_null() {
                let mut bytes = msg.into_bytes();
                bytes.shrink_to_fit();
                let len = bytes.len();
                let ptr = bytes.as_mut_ptr();
                std::mem::forget(bytes);
                *out_error_msg = ptr;
                *out_error_msg_len = len;
            }
            set_status(STATUS_INVALID_REGEX)
        }
        Err(ScanError::Cancelled) | Err(ScanError::Io(_)) => set_status(STATUS_OPEN_FAILED),
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
    };

    let matcher = match crate::scan::build_matcher(pattern, &scan_opts) {
        Ok(m) => m,
        Err(crate::scan::ScanError::InvalidRegex(msg)) => {
            if !out_error_msg.is_null() && !out_error_msg_len.is_null() {
                let mut bytes = msg.into_bytes();
                bytes.shrink_to_fit();
                let len = bytes.len();
                let ptr = bytes.as_mut_ptr();
                std::mem::forget(bytes);
                *out_error_msg = ptr;
                *out_error_msg_len = len;
            }
            return std::ptr::null_mut();
        }
        // build_matcher only returns Ok or InvalidRegex; wildcard is defensive.
        Err(_) => return std::ptr::null_mut(),
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

    // Open + size check + mmap.
    let file = match File::open(&path) {
        Ok(f) => f,
        Err(_) => return set_status(STATUS_OPEN_FAILED),
    };
    let metadata = match file.metadata() {
        Ok(m) => m,
        Err(_) => return set_status(STATUS_OPEN_FAILED),
    };
    let file_size = metadata.len();
    if sess.max_file_size != 0 && file_size > sess.max_file_size {
        return set_status(STATUS_TOO_LARGE);
    }
    if file_size == 0 {
        return set_status(STATUS_OK);
    }

    let mmap = match Mmap::map(&file) {
        Ok(m) => m,
        Err(_) => return set_status(STATUS_OPEN_FAILED),
    };
    let bytes: &[u8] = &mmap;

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
    let mut poll_counter: u32 = 0;

    let scan_result = crate::scan::scan_bytes_with_matcher(
        bytes,
        &*sess.matcher,
        &sess.scan_opts,
        |rec: MatchRecord| {
            poll_counter = poll_counter.wrapping_add(1);
            if poll_counter & 0xff == 0 {
                if let Some(flag) = cancel_atomic {
                    if flag.load(Ordering::Relaxed) != 0 {
                        return false;
                    }
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
        Err(ScanError::BinarySkipped) => set_status(STATUS_BINARY_SKIPPED),
        // These are never produced by scan_bytes_with_matcher on mmap'd data
        // with a pre-compiled matcher, but handle defensively.
        Err(_) => set_status(STATUS_OPEN_FAILED),
    }
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
    fn abi_version_returns_2() {
        assert_eq!(qg_abi_version(), 2);
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
}
