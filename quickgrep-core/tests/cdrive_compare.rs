//! Real-world empirical comparison: walk C:\ and search every readable file
//! for the word "test" with both scanners. Stops after a 5-minute wall clock.
//!
//! Run with:
//!   cargo test --release --features grep_crates --test cdrive_compare \
//!       -- --nocapture --ignored
//!
//! The test reads each file's bytes ONCE, then runs both scanners on the same
//! in-memory buffer back-to-back. Order is alternated per file to neutralize
//! CPU/L2 warm-cache effects. Only the scan time itself is measured (I/O is
//! excluded from the comparison and reported separately).

#![cfg(feature = "grep_crates")]

use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};

use quickgrep_core::scan::{build_matcher, scan_bytes_with_matcher_ex, MatchRecord, ScanOptions};
use quickgrep_core::scan_grep::GrepSession;
use walkdir::WalkDir;

const PATTERN: &str = "test";
const ROOT: &str = r"C:\";
const MAX_WALL_CLOCK: Duration = Duration::from_secs(300); // 5 minutes
const MAX_FILE_BYTES: u64 = 10 * 1024 * 1024; // 10 MB per-file cap

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

fn fmt_bytes(n: u64) -> String {
    const UNITS: [&str; 5] = ["B", "KB", "MB", "GB", "TB"];
    let mut v = n as f64;
    let mut u = 0;
    while v >= 1024.0 && u < UNITS.len() - 1 {
        v /= 1024.0;
        u += 1;
    }
    format!("{:.2} {}", v, UNITS[u])
}

#[test]
#[ignore]
fn cdrive_search_test_word_5min() {
    let opts = opts();

    // Production-style reuse: build matcher / session ONCE and reuse across files.
    let in_tree_matcher = build_matcher(PATTERN, &opts).expect("in-tree build");
    let grep_session = GrepSession::new(PATTERN, &opts).expect("grep session build");

    let mut intree_files = 0u64;
    let mut intree_bytes = 0u64;
    let mut intree_matches = 0u64;
    let mut intree_scan_ns = 0u128;

    let mut grep_files = 0u64;
    let mut grep_bytes = 0u64;
    let mut grep_matches = 0u64;
    let mut grep_scan_ns = 0u128;

    let mut io_ns = 0u128;
    let mut walk_count = 0u64;
    let mut skipped_oversize = 0u64;
    let mut skipped_io_err = 0u64;
    let mut skipped_binary = 0u64;
    let mut parity_disagreements = 0u64;
    let mut first_disagreement: Option<(String, u64, u64)> = None;

    let cancel = AtomicBool::new(false);
    let cancel_check_intree = || cancel.load(Ordering::Relaxed);
    let cancel_check_grep = || cancel.load(Ordering::Relaxed);

    let started = Instant::now();
    let mut alternate = false;

    println!(
        "Scanning {} for the literal {:?} (case-insensitive). Budget: {:?}.",
        ROOT, PATTERN, MAX_WALL_CLOCK
    );

    let walker = WalkDir::new(ROOT)
        .follow_links(false)
        .same_file_system(false)
        .into_iter()
        .filter_entry(|e| {
            // Skip reparse points / junctions on Windows to avoid loops.
            match e.metadata() {
                Ok(md) => !md.file_type().is_symlink(),
                Err(_) => false,
            }
        });

    for entry in walker {
        if started.elapsed() >= MAX_WALL_CLOCK {
            break;
        }
        walk_count += 1;
        if walk_count.is_multiple_of(50_000) {
            println!(
                "  ...walked {walk_count}, elapsed {:.0}s, in-tree {} / grep {} files",
                started.elapsed().as_secs_f64(),
                intree_files,
                grep_files
            );
        }

        let entry = match entry {
            Ok(e) => e,
            Err(_) => continue, // permission denied etc.
        };
        if !entry.file_type().is_file() {
            continue;
        }

        let path = entry.path();
        let md = match entry.metadata() {
            Ok(m) => m,
            Err(_) => continue,
        };
        let size = md.len();
        if size == 0 {
            continue;
        }
        if size > MAX_FILE_BYTES {
            skipped_oversize += 1;
            continue;
        }

        // Read the file once; share bytes between both scanners.
        let io_start = Instant::now();
        let bytes = match std::fs::read(path) {
            Ok(b) => b,
            Err(_) => {
                skipped_io_err += 1;
                continue;
            }
        };
        io_ns += io_start.elapsed().as_nanos();

        // Alternate which engine runs first to neutralize warm-cache bias.
        let intree_first = alternate;
        alternate = !alternate;

        let mut intree_count = 0u64;
        let mut grep_count = 0u64;
        let mut intree_skipped = false;
        let mut grep_skipped = false;

        let run_intree = |bytes: &[u8],
                          out_count: &mut u64,
                          out_skipped: &mut bool,
                          ns: &mut u128| {
            let t = Instant::now();
            let mut local = 0u64;
            let res = scan_bytes_with_matcher_ex(
                bytes,
                &*in_tree_matcher,
                &opts,
                cancel_check_intree,
                |_: MatchRecord| {
                    local += 1;
                    true
                },
            );
            *ns += t.elapsed().as_nanos();
            match res {
                Ok(_) => *out_count = local,
                Err(quickgrep_core::scan::ScanError::BinarySkipped) => *out_skipped = true,
                Err(_) => {}
            }
        };

        let run_grep = |bytes: &[u8],
                        out_count: &mut u64,
                        out_skipped: &mut bool,
                        ns: &mut u128| {
            let t = Instant::now();
            let mut local = 0u64;
            let res = grep_session.scan(bytes, &opts, cancel_check_grep, |_| {
                local += 1;
                true
            });
            *ns += t.elapsed().as_nanos();
            match res {
                Ok(_) => *out_count = local,
                Err(quickgrep_core::scan::ScanError::BinarySkipped) => *out_skipped = true,
                Err(_) => {}
            }
        };

        if intree_first {
            run_intree(&bytes, &mut intree_count, &mut intree_skipped, &mut intree_scan_ns);
            run_grep(&bytes, &mut grep_count, &mut grep_skipped, &mut grep_scan_ns);
        } else {
            run_grep(&bytes, &mut grep_count, &mut grep_skipped, &mut grep_scan_ns);
            run_intree(&bytes, &mut intree_count, &mut intree_skipped, &mut intree_scan_ns);
        }

        if intree_skipped && grep_skipped {
            skipped_binary += 1;
        }
        if !intree_skipped {
            intree_files += 1;
            intree_bytes += size;
            intree_matches += intree_count;
        }
        if !grep_skipped {
            grep_files += 1;
            grep_bytes += size;
            grep_matches += grep_count;
        }

        // Note: in-tree and grep-searcher have slightly different binary-skip
        // heuristics, so disagreements where one skipped & the other didn't
        // are expected and not counted as parity violations. Only count if
        // both processed the file.
        if !intree_skipped && !grep_skipped && intree_count != grep_count {
            parity_disagreements += 1;
            if first_disagreement.is_none() {
                first_disagreement =
                    Some((path.display().to_string(), intree_count, grep_count));
            }
        }

        // Cancellation budget check at file boundary too (cheap, deterministic).
        if started.elapsed() >= MAX_WALL_CLOCK {
            cancel.store(true, Ordering::Relaxed);
            break;
        }
    }

    let elapsed = started.elapsed();
    let elapsed_s = elapsed.as_secs_f64();
    let intree_s = (intree_scan_ns as f64) / 1e9;
    let grep_s = (grep_scan_ns as f64) / 1e9;
    let io_s = (io_ns as f64) / 1e9;

    let mbs = |bytes: u64, s: f64| -> f64 {
        if s <= 0.0 { 0.0 } else { (bytes as f64 / 1_048_576.0) / s }
    };
    let fps = |files: u64, s: f64| -> f64 { if s <= 0.0 { 0.0 } else { files as f64 / s } };

    println!("\n================ C:\\ Search Comparison ================");
    println!("root:                  {}", ROOT);
    println!("pattern:               {:?}  (case-insensitive literal)", PATTERN);
    println!("budget:                {:?}", MAX_WALL_CLOCK);
    println!("wall clock:            {:.2}s", elapsed_s);
    println!("walk_dir entries:      {walk_count}");
    println!("skipped (oversize):    {skipped_oversize}  (>{}/file)", fmt_bytes(MAX_FILE_BYTES));
    println!("skipped (io error):    {skipped_io_err}");
    println!("binary-skipped (both): {skipped_binary}");
    println!("file I/O time:         {:.2}s  ({} read)", io_s, fmt_bytes(intree_bytes.max(grep_bytes)));
    println!();
    println!("--- in-tree scanner ---");
    println!("  files scanned:       {}", intree_files);
    println!("  bytes scanned:       {}", fmt_bytes(intree_bytes));
    println!("  matches found:       {}", intree_matches);
    println!("  scan-only CPU time:  {:.2}s", intree_s);
    println!("  scan throughput:     {:.1} MB/s   {:.0} files/s", mbs(intree_bytes, intree_s), fps(intree_files, intree_s));
    println!();
    println!("--- grep-searcher (Phase 6) ---");
    println!("  files scanned:       {}", grep_files);
    println!("  bytes scanned:       {}", fmt_bytes(grep_bytes));
    println!("  matches found:       {}", grep_matches);
    println!("  scan-only CPU time:  {:.2}s", grep_s);
    println!("  scan throughput:     {:.1} MB/s   {:.0} files/s", mbs(grep_bytes, grep_s), fps(grep_files, grep_s));
    println!();
    println!("--- speedup (grep / in-tree, scan time only) ---");
    if intree_s > 0.0 {
        println!("  MB/s     ratio:      {:.2}x", mbs(grep_bytes, grep_s) / mbs(intree_bytes, intree_s));
        println!("  files/s  ratio:      {:.2}x", fps(grep_files, grep_s) / fps(intree_files, intree_s));
        println!("  CPU time ratio:      {:.2}x  (in-tree {:.2}s vs grep {:.2}s)",
            intree_s / grep_s.max(1e-9), intree_s, grep_s);
    }
    println!();
    println!("--- parity ---");
    println!("  disagreements:       {parity_disagreements}");
    if let Some((p, a, b)) = first_disagreement.as_ref() {
        println!("  first disagreement:  {p}  in-tree={a}  grep={b}");
    }
    println!("=========================================================\n");

    // Sanity: both engines should have processed a substantial number of files
    // within the 5-minute budget.
    assert!(
        intree_files > 100 && grep_files > 100,
        "expected to scan at least 100 files in 5 minutes (intree={intree_files}, grep={grep_files})"
    );
}
