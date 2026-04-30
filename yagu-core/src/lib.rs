//! yagu-core: native scan engine for Yagu.
//!
//! Two surfaces:
//!   * `scan` module — pure-Rust, unit-testable scanner.
//!   * `ffi` module — C ABI exposed via the cdylib for P/Invoke from .NET.

pub mod ffi;
pub mod scan;

#[cfg(feature = "grep_crates")]
pub mod scan_grep;
