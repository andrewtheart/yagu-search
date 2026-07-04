using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace Yagu.Tests;

/// <summary>
/// Live golden-snapshot regression for the end-to-end natural-language → search-parameters pipeline.
///
/// The pipeline is deterministic (the translator runs the model with temperature 0 + a fixed random
/// seed, and the semantic layer maps its JSON deterministically), so the SAME query must always resolve
/// to the SAME Advanced-Options / search parameters. This test pins that: it runs the committed
/// <c>queries.txt</c> through the built <c>Yagu.exe --cli --semantic-batch</c> against a pinned model and
/// asserts each query's resolved fields exactly match the committed <c>expected-plans.json</c>. A drift
/// (from a prompt edit, a model swap, or a semantic-layer change) is exactly the regression this catches.
///
/// It is <c>[Trait("Category","Slow")]</c> because it needs a GPU + the local model and takes many
/// minutes — it is EXCLUDED from the default <c>--filter "Category!=Slow"</c> iterative run and runs only
/// on-demand on a dev machine. The pure semantic-layer rules are separately covered offline by
/// <see cref="SemanticPlanApplierTests"/> / <see cref="SemanticQuerySalvageTests"/>.
///
/// Re-baseline after an intentional prompt/model change with:
///   <c>$env:YAGU_UPDATE_SEMANTIC_GOLDEN=1; dotnet test --filter "FullyQualifiedName~SemanticEvalGoldenTests"</c>
/// which rewrites <c>expected-plans.json</c> from the current run instead of asserting.
/// </summary>
[Trait("Category", "Slow")]
public sealed class SemanticEvalGoldenTests
{
    private readonly ITestOutputHelper _out;
    public SemanticEvalGoldenTests(ITestOutputHelper output) => _out = output;

    /// <summary>The model the golden is pinned against (small, fast, the realistic default).</summary>
    private const string GoldenModel = "phi-4-mini";

    /// <summary>Resolved fields compared per query — the concrete search parameters. Free-text
    /// (<c>model</c>, <c>summary</c>) and order-sensitive noise are intentionally excluded.</summary>
    private static readonly string[] ComparedFields =
    {
        "directory", "pattern", "search-mode", "regex", "case-sensitive", "exact-match",
        "include", "exclude", "min-size", "max-size",
        "created-after", "created-before", "modified-after", "modified-before",
        "sort", "group", "hidden", "archives", "binary", "image-text", "error",
    };

    [Fact]
    public void SemanticPlans_MatchGolden()
    {
        string root = RepoRoot();
        string exe = FindYaguExe(root);
        string queriesFile = Path.Combine(root, "Yagu.Tests", "TestData", "SemanticEval", "queries.txt");
        string goldenFile = Path.Combine(root, "Yagu.Tests", "TestData", "SemanticEval", "expected-plans.json");
        Assert.True(File.Exists(queriesFile), $"query fixture not found: {queriesFile}");

        if (exe is null)
        {
            _out.WriteLine("Yagu.exe not built — skipping the live golden run (build Yagu first).");
            return;
        }

        string stdout = RunBatch(exe, queriesFile);
        var actual = ParseBlocks(stdout);
        _out.WriteLine($"Parsed {actual.Count} plan blocks from the batch run.");

        // Environment without the pinned model (or semantic disabled): don't fail — this is an opt-in
        // Slow test that needs a specific local model. Detect the model-unavailable signal and skip.
        if (actual.Count == 0 ||
            actual.Values.All(f => f.TryGetValue("error", out var e) &&
                (e.Contains("No compatible local model") || e.Contains("semantic search is disabled"))))
        {
            _out.WriteLine($"Model '{GoldenModel}' not available on this machine — skipping the golden assertion.");
            return;
        }

        if (string.Equals(System.Environment.GetEnvironmentVariable("YAGU_UPDATE_SEMANTIC_GOLDEN"), "1"))
        {
            WriteGolden(goldenFile, actual);
            _out.WriteLine($"REBASELINED golden ({actual.Count} queries) -> {goldenFile}");
            return;
        }

        Assert.True(File.Exists(goldenFile),
            $"golden not found: {goldenFile}. Generate it with YAGU_UPDATE_SEMANTIC_GOLDEN=1.");
        var expected = ReadGolden(goldenFile);

        var mismatches = new List<string>();
        foreach (var (query, exp) in expected)
        {
            if (!actual.TryGetValue(query, out var act))
            {
                mismatches.Add($"[{query}] MISSING from this run's output");
                continue;
            }
            foreach (var field in ComparedFields)
            {
                exp.TryGetValue(field, out var ev);
                act.TryGetValue(field, out var av);
                if (!string.Equals(ev ?? "", av ?? "", System.StringComparison.Ordinal))
                    mismatches.Add($"[{query}] {field}: expected '{ev ?? "(unset)"}' but got '{av ?? "(unset)"}'");
            }
        }

        Assert.True(mismatches.Count == 0,
            $"{mismatches.Count} golden mismatch(es) — the query→parameters mapping drifted. If this was an " +
            $"intentional prompt/model change, re-baseline with YAGU_UPDATE_SEMANTIC_GOLDEN=1.\n" +
            string.Join("\n", mismatches.Take(50)));
    }

    // ---- helpers ------------------------------------------------------------

    private static string RepoRoot()
    {
        string dir = System.AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Yagu.sln")))
            dir = Directory.GetParent(dir)?.FullName!;
        return dir ?? throw new DirectoryNotFoundException("Yagu.sln not found above the test output directory.");
    }

    private static string? FindYaguExe(string root)
    {
        const string tfm = "net10.0-windows10.0.19041.0";
        foreach (var cfg in new[] { "Debug", "Release" })
        {
            string p = Path.Combine(root, "Yagu", "bin", cfg, tfm, "Yagu.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static string RunBatch(string exe, string queriesFile)
    {
        // Allow feeding a PRE-GENERATED batch output (e.g. from the resilient run harness) instead of a
        // fresh single-shot run — used to (re)baseline the golden from a crash-resilient capture.
        var preGenerated = System.Environment.GetEnvironmentVariable("YAGU_SEMANTIC_BATCH_OUTPUT");
        if (!string.IsNullOrEmpty(preGenerated) && File.Exists(preGenerated))
            return File.ReadAllText(preGenerated);

        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = false, // let per-query progress/stack traces inherit — avoids buffer deadlock
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--cli");
        psi.ArgumentList.Add("--semantic-batch");
        psi.ArgumentList.Add(queriesFile);
        psi.ArgumentList.Add("--semantic-model");
        psi.ArgumentList.Add(GoldenModel);

        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit(90 * 60 * 1000); // generous: the full set can take many minutes on a busy GPU
        return stdout;
    }

    private static readonly Regex FieldLine = new(@"^\s{2}([a-z][a-z-]+)\s*:\s*(.*)$", RegexOptions.Compiled);

    private static Dictionary<string, Dictionary<string, string>> ParseBlocks(string stdout)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(System.StringComparer.Ordinal);
        foreach (var chunk in stdout.Split("===QUERY===").Skip(1))
        {
            var body = chunk.Split("===END===", 2)[0];
            var lines = body.Replace("\r\n", "\n").Split('\n');
            if (lines.Length == 0) continue;
            string query = lines[0].Trim();
            if (query.Length == 0) continue;
            var fields = new Dictionary<string, string>(System.StringComparer.Ordinal);
            foreach (var line in lines.Skip(1))
            {
                var m = FieldLine.Match(line);
                if (m.Success) fields[m.Groups[1].Value] = m.Groups[2].Value.Trim();
            }
            result[query] = fields; // last block wins if a query repeats
        }
        return result;
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static void WriteGolden(string path, Dictionary<string, Dictionary<string, string>> data)
    {
        // Keep only the compared fields, sorted, for a stable diff-friendly golden.
        var trimmed = new SortedDictionary<string, SortedDictionary<string, string>>(System.StringComparer.Ordinal);
        foreach (var (q, fields) in data)
        {
            var kept = new SortedDictionary<string, string>(System.StringComparer.Ordinal);
            foreach (var f in ComparedFields)
                if (fields.TryGetValue(f, out var v)) kept[f] = v;
            trimmed[q] = kept;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(trimmed, JsonOpts));
    }

    private static Dictionary<string, Dictionary<string, string>> ReadGolden(string path)
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(path))
                  ?? new Dictionary<string, Dictionary<string, string>>();
        return raw;
    }
}
