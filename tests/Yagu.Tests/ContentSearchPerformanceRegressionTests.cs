using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace Yagu.Tests;

/// <summary>
/// Performance-regression guard for the native scan hot loop, tailored to the
/// <c>source_match_start</c> / <c>utf16_col</c> regression (full-<c>C:\</c>
/// "test" went ~4x slower and stopped completing because the Rust scanner
/// computed the full-line UTF-16 column PER MATCH via <c>utf16_col</c> instead
/// of returning the sentinel and letting the managed side resolve the column
/// lazily — see <c>source_match_start</c> in <c>yagu-core/src/scan.rs</c> and
/// repo memory <c>yagu-profiling.md</c>).
///
/// HOW IT ISOLATES THE REGRESSION (and avoids false positives):
/// <c>utf16_col</c> is the ONLY per-match cost that depends on whether the
/// match's line PREFIX is ASCII — it is a cheap byte offset for ASCII prefixes
/// but a slow UTF-8 decode for non-ASCII ones. Everything else in the pipeline
/// (memchr matching, the per-match FFI line-bytes copy, DirectOutputSink's raw
/// byte writes) costs the same regardless of content. So we search two corpora
/// that are byte-for-byte the same length with the same match count, differing
/// ONLY in their inter-match filler bytes — ASCII 'a' vs invalid-UTF-8 0x80 —
/// and compare engine time:
///   • Fixed code: source_match_start returns the sentinel for both → ratio ≈ 1.
///   • Regressed code: every match on the non-ASCII corpus pays a slow prefix
///     decode → the non-ASCII corpus is markedly slower → ratio well above 1.
/// This deliberately does NOT vary line length or match density (doing so
/// measures the pre-existing O(line) per-match FFI line copy, not this bug).
///
/// Why CLI / non-color output: <c>DirectOutputSink</c> streams the raw match
/// line bytes and does NO per-match column work in non-color (redirected) mode,
/// so the native <c>source_match_start</c> is the only column cost left to
/// observe.
///
/// This launches processes and is timing-sensitive, so it is
/// <c>Category=Slow</c> AND opt-in: set <c>YAGU_RUN_PERF_REGRESSION=1</c> to run
/// it. Without that it returns immediately (treated as passing) so normal and
/// CI runs are unaffected. A complementary deterministic guard lives in
/// <c>yagu-core/src/scan.rs</c>
/// (<c>source_match_start_defers_to_managed_side_in_common_path</c>), which
/// fails the instant the sentinel contract is broken.
/// </summary>
[Trait("Category", "Slow")]
public sealed class ContentSearchPerformanceRegressionTests
{
    private const string OptInVar = "YAGU_RUN_PERF_REGRESSION";

    /// <summary>
    /// Ceiling for the non-ASCII vs ASCII engine-time ratio. With the fix the
    /// two corpora do identical work, so the ratio sits near 1.0; the per-match
    /// <c>utf16_col</c> regression makes the non-ASCII corpus several times
    /// slower. 1.8 leaves margin for timing jitter while still catching a
    /// reintroduced regression (observed ~3x+ when the bug is present).
    /// </summary>
    private const double NonAsciiRatioCeiling = 1.8;

    private readonly ITestOutputHelper _output;

    public ContentSearchPerformanceRegressionTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void NativeScan_NonAsciiDenseMatches_NotSlowerThanAsciiEquivalent()
    {
        if (!ShouldRun(out var ours)) return;

        var root = NewCorpusRoot("col");
        try
        {
            // Identical shape (8 files × 1000 lines × 16 matches/line, ~16 KB lines)
            // and identical byte length. The ONLY difference is the inter-match filler
            // bytes:
            //   • ascii corpus    → 'a' (0x61): utf16_col hits the is_ascii fast path.
            //   • nonascii corpus → 0x80      : invalid UTF-8, so a reintroduced
            //     utf16_col falls into the slow byte-by-byte resync decode for every
            //     match's (long) prefix — exactly the binary-blob case that triggered
            //     the original ~4x full-C:\ regression.
            var asciiDir = Path.Combine(root, "ascii");
            var nonAsciiDir = Path.Combine(root, "nonascii");
            GenerateCorpus(asciiDir, files: 8, linesPerFile: 1000, matchesPerLine: 16, fillerChunk: 1024, nonAscii: false);
            GenerateCorpus(nonAsciiDir, files: 8, linesPerFile: 1000, matchesPerLine: 16, fillerChunk: 1024, nonAscii: true);

            // Warm both (filesystem cache + JIT), then best-of-3 to damp jitter.
            RunCli(ours, asciiDir);
            RunCli(ours, nonAsciiDir);
            var ascii = BestOf(3, () => RunCli(ours, asciiDir));
            var nonAscii = BestOf(3, () => RunCli(ours, nonAsciiDir));

            _output.WriteLine($"ascii    : {ascii.Matches} matches, engine {ascii.Seconds:F3}s");
            _output.WriteLine($"nonascii : {nonAscii.Matches} matches, engine {nonAscii.Seconds:F3}s");

            Assert.True(ascii.Matches > 0 && nonAscii.Matches > 0,
                "Expected matches in both corpora — a zero count means the search broke, not sped up.");
            Assert.Equal(ascii.Matches, nonAscii.Matches); // identical shape ⇒ identical match count

            // Guard against measuring noise: if the ASCII baseline is too quick to
            // time reliably on this machine, treat the run as inconclusive (pass)
            // rather than asserting a jittery ratio. The deterministic Rust unit
            // guard (source_match_start_defers_to_managed_side_in_common_path) still
            // covers the invariant unconditionally.
            if (ascii.Seconds < 0.05)
            {
                _output.WriteLine(
                    $"Inconclusive (skipped): ASCII engine time {ascii.Seconds:F3}s is below the noise " +
                    "floor on this machine; the ratio is not asserted. Increase the corpus size to force it.");
                return;
            }

            double ratio = nonAscii.Seconds / ascii.Seconds;
            _output.WriteLine($"nonascii/ascii engine-time ratio = {ratio:F2} (ceiling {NonAsciiRatioCeiling})");

            Assert.True(ratio <= NonAsciiRatioCeiling,
                $"Non-ASCII content made an otherwise-identical search {ratio:F2}x slower " +
                $"(> {NonAsciiRatioCeiling}). Only the per-match full-line column resolution depends on " +
                $"ASCII-ness, so this is the source_match_start/utf16_col regression signature: the scan " +
                $"hot loop is computing the UTF-16 column per match again instead of returning the " +
                $"sentinel. See `source_match_start` in yagu-core/src/scan.rs and repo memory " +
                $"yagu-profiling.md.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ───────────────────────── Corpus ─────────────────────────

    /// <summary>
    /// Writes <paramref name="files"/> files of identical shape:
    /// <paramref name="linesPerFile"/> lines, each built as
    /// (<paramref name="fillerChunk"/> filler bytes + "test") repeated
    /// <paramref name="matchesPerLine"/> times — so the prefix before each match
    /// grows long (maximizing the per-match column cost the regression imposes).
    /// The filler byte is 'a' (ASCII, fast <c>is_ascii</c> path) when
    /// <paramref name="nonAscii"/> is false, or <c>0x80</c> (invalid UTF-8, slow
    /// resync decode path) when true. Both variants are the same byte length with
    /// the same match count, so the ONLY cost that differs is per-match column
    /// resolution. Bytes are written raw (the non-ASCII variant is not valid UTF-8
    /// by design), and searches pass <c>--binary</c> so neither corpus is skipped.
    /// </summary>
    private static void GenerateCorpus(string dir, int files, int linesPerFile, int matchesPerLine, int fillerChunk, bool nonAscii)
    {
        Directory.CreateDirectory(dir);
        byte fillerByte = nonAscii ? (byte)0x80 : (byte)'a';
        ReadOnlySpan<byte> needle = "test"u8;

        var line = new List<byte>(matchesPerLine * (fillerChunk + needle.Length) + 1);
        for (int t = 0; t < matchesPerLine; t++)
        {
            for (int i = 0; i < fillerChunk; i++)
                line.Add(fillerByte);
            foreach (var b in needle)
                line.Add(b);
        }
        line.Add((byte)'\n');
        byte[] lineBytes = line.ToArray();

        for (int f = 0; f < files; f++)
        {
            var path = Path.Combine(dir, $"corpus-{f:D2}.txt");
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            for (int i = 0; i < linesPerFile; i++)
                fs.Write(lineBytes, 0, lineBytes.Length);
        }
    }

    // ───────────────────────── Process harness ─────────────────────────

    private readonly record struct Measurement(long Matches, double EngineSeconds, double WallSeconds)
    {
        /// <summary>Engine-internal elapsed when parsed (excludes process startup); else wall-clock.</summary>
        public double Seconds => EngineSeconds > 0 ? EngineSeconds : WallSeconds;
    }

    private Measurement BestOf(int n, Func<Measurement> run)
    {
        Measurement best = default;
        double bestSeconds = double.MaxValue;
        for (int i = 0; i < n; i++)
        {
            var m = run();
            if (m.Seconds < bestSeconds)
            {
                bestSeconds = m.Seconds;
                best = m;
            }
        }
        return best;
    }

    private Measurement RunCli(string exe, string dir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // Non-color (redirected) output → DirectOutputSink streams raw bytes with
        // no per-match column work, isolating the native source_match_start cost.
        foreach (var arg in new[]
        {
            "--cli", "--directory", dir, "test",
            "--no-exact-match", "--ignore-case", "--binary",
            "--context", "0", "--threads", "4", "--max-results", "0",
        })
        {
            psi.ArgumentList.Add(arg);
        }

        var sw = Stopwatch.StartNew();
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{exe}'.");

        // Drain stdout efficiently (it can be tens of MB) so the pipe never blocks.
        var drainStdout = proc.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        string stderr = proc.StandardError.ReadToEnd();

        if (!proc.WaitForExit(120_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException($"'{exe}' did not finish within 120s on '{dir}'.");
        }
        proc.WaitForExit();
        drainStdout.Wait();
        sw.Stop();

        return new Measurement(
            ParseMatchCount(stderr),
            ParseEngineSeconds(stderr),
            sw.Elapsed.TotalSeconds);
    }

    // Completion summary (stderr): "... - <N> match(es) in <F> file(s) [<E>s]"
    private static readonly Regex MatchCountRegex =
        new(@"(\d+)\s+match\(es\)", RegexOptions.Compiled);
    private static readonly Regex EngineSecondsRegex =
        new(@"\[(\d+(?:\.\d+)?)s\]", RegexOptions.Compiled);

    private static long ParseMatchCount(string stderr)
    {
        var m = MatchCountRegex.Match(stderr);
        return m.Success && long.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : -1;
    }

    private static double ParseEngineSeconds(string stderr)
    {
        var m = EngineSecondsRegex.Match(stderr);
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s)
            ? s
            : 0.0;
    }

    // ───────────────────────── Setup / skipping ─────────────────────────

    /// <summary>
    /// Returns false (and writes a skip reason) unless the test is opted-in on
    /// Windows and our freshly-built Yagu.exe exists. On success, <paramref name="ourExe"/>
    /// is the path to our CLI binary.
    /// </summary>
    private bool ShouldRun(out string ourExe)
    {
        ourExe = string.Empty;

        if (!OperatingSystem.IsWindows())
        {
            _output.WriteLine("Skipped: performance regression test requires Windows.");
            return false;
        }

        var optIn = Environment.GetEnvironmentVariable(OptInVar);
        if (string.IsNullOrEmpty(optIn) || optIn == "0")
        {
            _output.WriteLine($"Skipped: set {OptInVar}=1 to run the performance regression test.");
            return false;
        }

        var solutionRoot = FindSolutionRoot();
        var exe = Path.Combine(
            solutionRoot, "Yagu", "bin", "Debug", "net10.0-windows10.0.19041.0", "Yagu.exe");
        if (!File.Exists(exe))
        {
            _output.WriteLine(
                $"Skipped: Yagu Debug build not found at '{exe}'. " +
                "Run 'dotnet build Yagu/Yagu.csproj -c Debug -p:RustProfile=profiling' first.");
            return false;
        }

        ourExe = exe;
        return true;
    }

    private static string NewCorpusRoot(string label)
    {
        var solutionRoot = FindSolutionRoot();
        var root = Path.Combine(
            solutionRoot, "TestResults", "PerfRegressionCorpora",
            $"{label}-{Guid.NewGuid():N}"[..(label.Length + 9)]);
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* leave for inspection */ }
    }

    private static string FindSolutionRoot()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(ContentSearchPerformanceRegressionTests).Assembly.Location)!;
        var dir = new DirectoryInfo(assemblyDir);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException($"Could not locate Yagu.sln walking up from {assemblyDir}.");
    }
}
