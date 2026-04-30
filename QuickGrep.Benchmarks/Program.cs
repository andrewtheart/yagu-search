using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.CsProj;
using BenchmarkDotNet.Toolchains.DotNetCli;
using QuickGrep.Models;
using QuickGrep.Services;

namespace QuickGrep.Benchmarks;

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
