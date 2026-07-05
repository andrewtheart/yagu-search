//! yagu-core: native scan engine for Yagu.
//!
//! Two surfaces:
//!   * `scan` module — pure-Rust, unit-testable scanner.
//!   * `ffi` module — C ABI exposed via the cdylib for P/Invoke from .NET.

pub mod ffi;
pub mod scan;

#[cfg(feature = "grep_crates")]
pub mod scan_grep;

// Optional: route Rust-side allocations through mimalloc. In a cdylib this
// only redirects allocations made by this crate's Rust code (the .NET host
// keeps using its own CRT allocator), which is exactly what we want — we get
// faster allocator behaviour for our hot `Vec<u8>` line/context buffers
// without imposing a global allocator change on the host process.
#[cfg(feature = "mimalloc")]
#[global_allocator]
static GLOBAL: mimalloc::MiMalloc = mimalloc::MiMalloc;
