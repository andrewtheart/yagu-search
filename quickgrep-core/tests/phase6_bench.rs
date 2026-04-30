//! Phase 6 micro-benchmark comparing the in-tree scanner against the
//! grep-searcher / grep-regex spike on representative workloads.
//!
//! Run with:
//!   cargo test --release --features grep_crates --test phase6_bench -- --nocapture --ignored
//!
//! Marked `#[ignore]` so it does not run as part of the regular test suite.

#![cfg(feature = "grep_crates")]

use std::time::Instant;

use quickgrep_core::scan::{scan_bytes_ex, MatchRecord, ScanOptions};
use quickgrep_core::scan_grep::scan_bytes_grep;

fn opts(case_sensitive: bool, regex: bool) -> ScanOptions {
    ScanOptions {
        case_sensitive,
        use_regex: regex,
        context_before: 0,
        context_after: 0,
        max_results: 0,
        skip_binary: false,
        ascii_case_only: false,
    }
}

/// Build a synthetic source-code-ish corpus: many short lines, sparse hits.
fn build_corpus(target_bytes: usize) -> Vec<u8> {
    let template: &[u8] =
        b"fn process_request(req: &Request) -> Result<Response, Error> {\n\
          \x20\x20\x20\x20let parsed = parse_json(req.body())?;\n\
          \x20\x20\x20\x20let user = lookup_user(parsed.user_id)?;\n\
          \x20\x20\x20\x20let response = build_response(user);\n\
          \x20\x20\x20\x20log_info(&format!(\"served {}\", user.name));\n\
          \x20\x20\x20\x20Ok(response)\n\
          }\n\
          \n\
          // Some commentary about the function above with a needle: HOTPATH\n\
          // and another non-matching comment line for filler text.\n\
          struct Cache { entries: Vec<Entry>, capacity: usize }\n\
          impl Cache { fn get(&self, k: &str) -> Option<&Entry> { None } }\n";
    let mut buf = Vec::with_capacity(target_bytes + template.len());
    while buf.len() < target_bytes {
        buf.extend_from_slice(template);
    }
    buf
}

fn run<F>(label: &str, bytes: &[u8], iters: u32, mut f: F) -> (u32, f64)
where
    F: FnMut(&[u8]) -> usize,
{
    // Warm-up
    let warm = f(bytes);
    let total_bytes = (bytes.len() as u64) * (iters as u64);
    let start = Instant::now();
    let mut total_matches = 0u32;
    for _ in 0..iters {
        total_matches = f(bytes) as u32;
    }
    let elapsed = start.elapsed().as_secs_f64();
    let mb_per_s = (total_bytes as f64 / 1_048_576.0) / elapsed;
    println!(
        "  {label:<28} matches={total_matches:>6} warm={warm:>6} {mb_per_s:>8.1} MB/s ({elapsed:>5.2}s)",
    );
    (total_matches, mb_per_s)
}

#[test]
#[ignore]
fn bench_phase6_literal_sparse() {
    let bytes = build_corpus(64 * 1024 * 1024); // 64 MB
    println!("\n=== Literal sparse (case-sensitive, 64 MB synthetic, 16 iters) ===");
    let o = opts(true, false);
    let iters = 16;

    let (m_a, mbs_a) = run("in-tree scan_bytes_ex", &bytes, iters, |b| {
        let mut n = 0usize;
        let _ = scan_bytes_ex(b, "HOTPATH", &o, || false, |_: MatchRecord| {
            n += 1;
            true
        });
        n
    });

    let (m_b, mbs_b) = run("grep-searcher spike", &bytes, iters, |b| {
        let mut n = 0usize;
        let _ = scan_bytes_grep(b, "HOTPATH", &o, |_| {
            n += 1;
            true
        });
        n
    });

    assert_eq!(m_a, m_b, "match counts must match");
    println!("  >> grep-searcher / in-tree = {:.2}x", mbs_b / mbs_a);
}

#[test]
#[ignore]
fn bench_phase6_literal_no_match() {
    let bytes = build_corpus(64 * 1024 * 1024);
    println!("\n=== Literal NO MATCH (case-sensitive, 64 MB, 16 iters) ===");
    let o = opts(true, false);
    let iters = 16;

    let (_m_a, mbs_a) = run("in-tree scan_bytes_ex", &bytes, iters, |b| {
        let mut n = 0usize;
        let _ = scan_bytes_ex(b, "ZZZNOTHERE", &o, || false, |_| {
            n += 1;
            true
        });
        n
    });

    let (_m_b, mbs_b) = run("grep-searcher spike", &bytes, iters, |b| {
        let mut n = 0usize;
        let _ = scan_bytes_grep(b, "ZZZNOTHERE", &o, |_| {
            n += 1;
            true
        });
        n
    });

    println!("  >> grep-searcher / in-tree = {:.2}x", mbs_b / mbs_a);
}

#[test]
#[ignore]
fn bench_phase6_literal_case_insensitive() {
    let bytes = build_corpus(64 * 1024 * 1024);
    println!("\n=== Literal case-insensitive (64 MB, 16 iters) ===");
    let o = opts(false, false);
    let iters = 16;

    let (m_a, mbs_a) = run("in-tree scan_bytes_ex", &bytes, iters, |b| {
        let mut n = 0usize;
        let _ = scan_bytes_ex(b, "hotpath", &o, || false, |_| {
            n += 1;
            true
        });
        n
    });

    let (m_b, mbs_b) = run("grep-searcher spike", &bytes, iters, |b| {
        let mut n = 0usize;
        let _ = scan_bytes_grep(b, "hotpath", &o, |_| {
            n += 1;
            true
        });
        n
    });

    assert_eq!(m_a, m_b, "match counts must match (CI literal)");
    println!("  >> grep-searcher / in-tree = {:.2}x", mbs_b / mbs_a);
}

#[test]
#[ignore]
fn bench_phase6_regex() {
    let bytes = build_corpus(64 * 1024 * 1024);
    println!("\n=== Regex \\bHOT\\w+ (64 MB, 16 iters) ===");
    let o = opts(true, true);
    let iters = 16;

    let (m_a, mbs_a) = run("in-tree scan_bytes_ex", &bytes, iters, |b| {
        let mut n = 0usize;
        let _ = scan_bytes_ex(b, r"\bHOT\w+", &o, || false, |_| {
            n += 1;
            true
        });
        n
    });

    let (m_b, mbs_b) = run("grep-searcher spike", &bytes, iters, |b| {
        let mut n = 0usize;
        let _ = scan_bytes_grep(b, r"\bHOT\w+", &o, |_| {
            n += 1;
            true
        });
        n
    });

    assert_eq!(m_a, m_b, "match counts must match (regex)");
    println!("  >> grep-searcher / in-tree = {:.2}x", mbs_b / mbs_a);
}

#[test]
#[ignore]
fn bench_phase6_many_small_files() {
    // Simulates the "millions of files" workload: many small buffers, each
    // scanned independently. Per-call setup cost dominates here.
    let small = build_corpus(8 * 1024); // 8 KB / "file"
    let n_files = 100_000;
    println!("\n=== Many small buffers ({} x 8 KB) ===", n_files);
    let o = opts(true, false);

    let total_bytes = (small.len() as u64) * (n_files as u64);

    let start = Instant::now();
    let mut hits = 0u64;
    for _ in 0..n_files {
        let _ = scan_bytes_ex(&small, "HOTPATH", &o, || false, |_| {
            hits += 1;
            true
        });
    }
    let dt = start.elapsed().as_secs_f64();
    let mbs_a = (total_bytes as f64 / 1_048_576.0) / dt;
    let fps_a = n_files as f64 / dt;
    println!("  in-tree:        hits={hits} {mbs_a:>8.1} MB/s {fps_a:>8.0} files/s ({dt:.2}s)");

    let start = Instant::now();
    let mut hits = 0u64;
    for _ in 0..n_files {
        let _ = scan_bytes_grep(&small, "HOTPATH", &o, |_| {
            hits += 1;
            true
        });
    }
    let dt = start.elapsed().as_secs_f64();
    let mbs_b = (total_bytes as f64 / 1_048_576.0) / dt;
    let fps_b = n_files as f64 / dt;
    println!("  grep-searcher (per-call build):  hits={hits} {mbs_b:>8.1} MB/s {fps_b:>8.0} files/s ({dt:.2}s)");

    // Production wiring would reuse the Searcher + Matcher across files.
    use grep_regex::RegexMatcherBuilder;
    use grep_searcher::{BinaryDetection, SearcherBuilder, Sink, SinkError, SinkMatch};
    let matcher = RegexMatcherBuilder::new()
        .case_insensitive(false)
        .line_terminator(Some(b'\n'))
        .build(&regex::escape("HOTPATH"))
        .unwrap();
    let mut searcher = SearcherBuilder::new()
        .line_number(true)
        .binary_detection(BinaryDetection::none())
        .build();

    struct CountingSink<'a>(&'a mut u64);
    #[derive(Debug)]
    struct Abort;
    impl std::fmt::Display for Abort { fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result { f.write_str("a") } }
    impl std::error::Error for Abort {}
    impl SinkError for Abort { fn error_message<T: std::fmt::Display>(_m: T) -> Self { Abort } }
    impl Sink for CountingSink<'_> {
        type Error = Abort;
        fn matched(&mut self, _: &grep_searcher::Searcher, _: &SinkMatch<'_>) -> Result<bool, Self::Error> {
            *self.0 += 1;
            Ok(true)
        }
    }

    let start = Instant::now();
    let mut hits_c = 0u64;
    for _ in 0..n_files {
        let mut sink = CountingSink(&mut hits_c);
        let _ = searcher.search_slice(&matcher, &small, &mut sink);
    }
    let dt = start.elapsed().as_secs_f64();
    let mbs_c = (total_bytes as f64 / 1_048_576.0) / dt;
    let fps_c = n_files as f64 / dt;
    println!("  grep-searcher (REUSED):          hits={hits_c} {mbs_c:>8.1} MB/s {fps_c:>8.0} files/s ({dt:.2}s)");
    println!(
        "  >> per-call: {:.2}x throughput   reused: {:.2}x throughput   reused files/s {:.2}x",
        mbs_b / mbs_a,
        mbs_c / mbs_a,
        fps_c / fps_a
    );
}
