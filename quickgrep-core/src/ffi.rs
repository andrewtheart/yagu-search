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
        Err(ScanError::Cancelled) => {
            result_slot.status = STATUS_CANCELLED;
            return STATUS_CANCELLED;
        }
        Err(ScanError::Io(_)) => {
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
        Err(ScanError::Cancelled) => set_status(STATUS_CANCELLED),
        Err(ScanError::Io(_)) => set_status(STATUS_OPEN_FAILED),
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
        Err(ScanError::Cancelled) => set_status(STATUS_CANCELLED),
        Err(ScanError::Io(_)) => set_status(STATUS_OPEN_FAILED),
        Err(ScanError::InvalidRegex(_)) => {
            // Should not happen with pre-compiled matcher, but handle gracefully.
            set_status(STATUS_INVALID_REGEX)
        }
    }
}
