using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.CsProj;
using BenchmarkDotNet.Toolchains.DotNetCli;
using System.Text;
using Yagu.Models;
using Yagu.Services;
using Yagu.Services.Index;

namespace Yagu.Benchmarks;

public static class Program
{
    public static void Main(string[] args)
    {
        var windowsToolchain = CsProjCoreToolchain.From(new NetCoreAppSettings(
            "net10.0-windows",
            runtimeFrameworkVersion: null,
            name: ".NET 10 Windows",
            customDotNetCliPath: null,
            packagesPath: null,
            customRuntimePack: null,
            aotCompilerPath: null,
            aotCompilerMode: default));
        var config = DefaultConfig.Instance.AddJob(Job.Default.WithToolchain(windowsToolchain));

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
    }
}
[MemoryDiagnoser]
public class SearchBenchmarks
{
    private string _root = string.Empty;

    [Params(100, 1000)]
    public int FileCount;

    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "qg-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var line = string.Join('\n', Enumerable.Range(0, 200).Select(i => $"line {i} foo bar baz"));
        for (int i = 0; i < FileCount; i++)
            File.WriteAllText(Path.Combine(_root, $"f{i}.txt"), line);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Benchmark]
    public async Task<int> LiteralSearch()
    {
        var svc = new SearchService();
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = "foo",
            MaxFileSizeBytes = 0,
            MaxResults = 0,
        };
        int count = 0;
        await foreach (var evt in svc.SearchAsync(opts, default))
        {
            if (evt is SearchEvent.Match) count++;
            else if (evt is SearchEvent.MatchBatch mb) count += mb.Results.Count;
        }
        return count;
    }

    [Benchmark]
    public async Task<int> RegexSearch()
    {
        var svc = new SearchService();
        var opts = new SearchOptions
        {
            Directory = _root,
            Query = @"\bfoo\w*\b",
            UseRegex = true,
            MaxFileSizeBytes = 0,
            MaxResults = 0,
        };
        int count = 0;
        await foreach (var evt in svc.SearchAsync(opts, default))
        {
            if (evt is SearchEvent.Match) count++;
            else if (evt is SearchEvent.MatchBatch mb) count += mb.Results.Count;
        }
        return count;
    }
}
/// <summary>
/// Content-index micro-benchmarks (plan §7 Phase 0): precise timings for the in-memory index build and the
/// warm posting-evaluation hot path, complementing the wall-clock gates in
/// <see cref="ContentIndexBenchmarkTests"/>. Run with <c>dotnet run -c Release -- --filter '*ContentIndex*'</c>.
/// </summary>
[MemoryDiagnoser]
public class ContentIndexBenchmarks
{
    private const string RareNeedle = "Zq9Vx7Kw";

    [Params(5_000, 20_000)]
    public int DocCount;

    private byte[][] _docs = [];
    private ContentIndexGeneration _generation = null!;
    private TrigramExpression _query = null!;
    private DirtyContentSet _emptyDirty = null!;

    [GlobalSetup]
    public void Setup()
    {
        var common = new StringBuilder(80 * 48);
        for (int i = 0; i < 80; i++)
            common.Append("the quick brown fox jumps over the lazy dog ").Append(i).Append('\n');
        byte[] commonBytes = Encoding.UTF8.GetBytes(common.ToString());
        byte[] needleBytes = Encoding.UTF8.GetBytes(common + "\n" + RareNeedle + " token line\n");

        int stride = Math.Max(50, DocCount / 200);
        _docs = new byte[DocCount][];
        for (int i = 0; i < DocCount; i++)
            _docs[i] = (i % stride == 0) ? needleBytes : commonBytes;

        _generation = BuildGeneration();
        _emptyDirty = new DirtyContentSet();

        var pattern = EffectiveSearchPattern.Resolve(new SearchOptions
        {
            Directory = @"C:\bench",
            Query = RareNeedle,
            CaseSensitive = true,
            UseContentIndex = true,
        });
        _query = ((TrigramPlan.Eligible)TrigramQueryPlanner.Plan(pattern)).Query;
    }

    private ContentIndexGeneration BuildGeneration()
    {
        var policy = new IndexIngestionPolicy(0, null, null, true, false, 0);
        var builder = new ContentIndexGenerationBuilder(policy);
        for (int i = 0; i < DocCount; i++)
            builder.AddDocument($@"C:\bench\dir{i % 64}\file{i}.txt", _docs[i]);
        return builder.Build(
            ContentIndexManager.ScopeIdForRoot(@"C:\bench"),
            "bench-vol",
            @"C:\bench",
            new UsnCheckpoint(1, 100),
            DateTimeOffset.UtcNow);
    }

    /// <summary>Full in-memory generation build (extract → posting index).</summary>
    [Benchmark]
    public long BuildGenerationInMemory() => BuildGeneration().Manifest.ContentCount;

    /// <summary>Warm posting evaluation for a selective query (the query hot path at barrier B0).</summary>
    [Benchmark]
    public int WarmPostingEvaluation()
        => ContentIndexQuerySession.Begin(_generation, _query, _emptyDirty).CandidateCount;
}
