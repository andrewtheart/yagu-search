using Yagu.Services;

namespace Yagu.Tests;

public sealed class ExcludedMethodTests_SearchService
{
    [Fact]
    public void CollectForMemoryPressureIfDue_DoesNotThrow()
    {
        // Just exercise the method - it requests a GC internally.
        SearchService.CollectForMemoryPressureIfDue(TimeSpan.FromHours(1));
    }

    [Fact]
    public void CollectForMemoryPressureIfDue_UsesNonBlockingGc()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "Services", "SearchService.cs"));
        string method = ExtractMethodWindow(source, "CollectForMemoryPressureIfDue", window: 1400);

        Assert.Contains("GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: false);", method);
        Assert.DoesNotContain("blocking: true", method);
        Assert.DoesNotContain("WaitForPendingFinalizers", method);
    }

    [Fact]
    public void IsMemoryPressureHigh_ReturnsBoolean()
    {
        // Exercise with no cap — should not throw
        bool result = SearchService.IsMemoryPressureHigh(0, 0);
        // With a zero cap and zero pressure%, the default path returns false
        // (effectiveCap becomes long.MaxValue so WS < cap)
        Assert.False(result);
    }

    [Fact]
    public void IsMemoryPressureHigh_WithTinyCap_ReturnsTrue()
    {
        // 1 byte cap should always be exceeded
        bool result = SearchService.IsMemoryPressureHigh(1, 0);
        Assert.True(result);
    }

    [Fact]
    public void IsMemoryPressureRelieved_ReturnsBoolean()
    {
        // With no cap, pressure is always relieved
        bool result = SearchService.IsMemoryPressureRelieved(0, 0);
        Assert.True(result);
    }

    [Fact]
    public void TryGetSystemMemoryLoadPercent_ReturnsResult()
    {
        bool success = SearchService.TryGetSystemMemoryLoadPercent(out uint load);
        // On Windows, this should succeed
        Assert.True(success);
        Assert.InRange(load, 1u, 100u);
    }

    [Fact]
    public void GetMemoryDiagnostics_ReturnsNonEmptyString()
    {
        string diag = SearchService.GetMemoryDiagnostics();
        Assert.False(string.IsNullOrEmpty(diag));
        Assert.Contains("MB", diag);
    }

    [Fact]
    public void AutoProcessMemoryCap_ReturnsPositiveValue()
    {
        long cap = SearchService.AutoProcessMemoryCap();
        Assert.True(cap >= 2L * 1024 * 1024 * 1024); // at least 2 GB
    }

    private static string ExtractMethodWindow(string source, string methodName, int window)
    {
        int index = FindMethodDefinition(source, methodName);
        int end = Math.Min(source.Length, index + window);
        return source[index..end];
    }

    private static int FindMethodDefinition(string source, string methodName)
    {
        string needle = methodName + "(";
        int search = 0;
        while (true)
        {
            int index = source.IndexOf(needle, search, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Method '{methodName}' not found.");

            int lineStart = source.LastIndexOf('\n', index);
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            int lineEnd = source.IndexOf('\n', index);
            lineEnd = lineEnd < 0 ? source.Length : lineEnd;
            string line = source[lineStart..lineEnd];
            if (line.Contains("private ", StringComparison.Ordinal)
                || line.Contains("public ", StringComparison.Ordinal)
                || line.Contains("internal ", StringComparison.Ordinal)
                || line.Contains("protected ", StringComparison.Ordinal))
            {
                return lineStart;
            }

            search = index + needle.Length;
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Cannot find repo root (Yagu.sln)");
    }
}
