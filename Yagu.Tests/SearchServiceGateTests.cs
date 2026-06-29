using Yagu.Services;

namespace Yagu.Tests;

public sealed class SearchServiceGateTests
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
    public void Discovery_ExcludesOcrCacheTextFilesSoOnlyImagePathsAreShown()
    {
        // OCR'd images must only ever appear under their image path (set by OcrTextMatcher). The
        // internal OCR text cache (%LOCALAPPDATA%\Yagu\ocr-cache\*.txt) must never surface as its own
        // result row, so discovery skips any path under that directory before glob/content/OCR routing.
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "Services", "SearchService.cs"));

        Assert.Contains("string ocrCacheDirPrefix = Ocr.OcrTextCache.DefaultBaseDirectory() + Path.DirectorySeparatorChar;", source);
        AssertContainsInOrder(source,
            "string ocrCacheDirPrefix = Ocr.OcrTextCache.DefaultBaseDirectory() + Path.DirectorySeparatorChar;",
            "if (path.StartsWith(ocrCacheDirPrefix, StringComparison.OrdinalIgnoreCase))",
            "Interlocked.Increment(ref skipOcrCache);",
            "if (!globMatcher.Matches(path))");
    }

    [Fact]
    public void StreamingScannerCancellationCleanup_FinishesBeforeDestroyingCallbacks()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "Services", "SearchService.cs"));
        string cleanup = source[source.IndexOf("bool scannerFinished = false;", StringComparison.Ordinal)..];

        Assert.Contains("bool scannerFinished = false;", cleanup);
        Assert.Contains("scannerFinished = true;", cleanup);
        AssertContainsInOrder(cleanup,
            "if (!scannerFinished)",
            "Native.NativeSearcher.FinishStreamingScanner(scanner);",
            "Native.NativeSearcher.DestroyStreamingScanner(scanner);",
            "if (sinkHandle.IsAllocated) sinkHandle.Free();");
    }

    [Fact]
    public void RoutineSearchTelemetry_LogsAtInfoLevel()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "Services", "SearchService.cs"));

        Assert.Contains("LogService.Instance.Info(\"SearchService\", $\"Pipeline channels created:", source);
        Assert.Contains("LogService.Instance.Info(\"Discovery\", $\"Progress:", source);
        Assert.Contains("LogService.Instance.Info(\"SearchService\", $\"Discovery finished:", source);
        Assert.Contains("LogService.Instance.Info(\"Workers\",", source);
        Assert.Contains("$\"Streaming: pushed=", source);
        Assert.Contains("LogService.Instance.Info(\"Forwarder\",", source);
        Assert.Contains("$\"Throughput: forwarded=", source);
        Assert.Contains("$\"Completed: forwarded=", source);
        Assert.Contains("LogService.Instance.Info(\"SearchService\", $\"Search complete:", source);

        Assert.DoesNotContain("LogService.Instance.Warning(\"Discovery\", $\"Progress:", source);
        Assert.DoesNotContain("LogService.Instance.Warning(\"SearchService\", $\"Pipeline channels created:", source);
        Assert.DoesNotContain("LogService.Instance.Warning(\"SearchService\", $\"Discovery finished:", source);
        Assert.DoesNotContain("LogService.Instance.Warning(\"SearchService\", $\"Search complete:", source);

        int backpressureIndex = source.IndexOf("$\"Backpressure:", StringComparison.Ordinal);
        Assert.True(backpressureIndex >= 0, "Forwarder backpressure log not found.");
        string backpressureWindow = source[Math.Max(0, backpressureIndex - 120)..Math.Min(source.Length, backpressureIndex + 220)];
        Assert.Contains("LogService.Instance.Warning(\"Forwarder\",", backpressureWindow);
        Assert.DoesNotContain("LogService.Instance.Info(\"Forwarder\",", backpressureWindow);

        int throughputIndex = source.IndexOf("$\"Throughput:", StringComparison.Ordinal);
        Assert.True(throughputIndex >= 0, "Forwarder throughput log not found.");
        string throughputWindow = source[Math.Max(0, throughputIndex - 120)..Math.Min(source.Length, throughputIndex + 220)];
        Assert.Contains("LogService.Instance.Info(\"Forwarder\",", throughputWindow);
        Assert.DoesNotContain("LogService.Instance.Warning(\"Forwarder\",", throughputWindow);
    }

    [Fact]
    public void SourceBackedResults_UseCoarseBatchesAndDedicatedBuffer()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "Services", "SearchService.cs"));

        Assert.Contains("private const int SourceBackedResultChannelCapacity = 65_536;", source);
        Assert.Contains("int sourceBackedCap = options.DegradedResultStore != null", source);
        Assert.Contains("new BoundedChannelOptions(sourceBackedCap)", source);
        Assert.Contains("sourceBackedResults={sourceBackedCap}", source);
        Assert.Contains("const int SourceBackedBatchSize = 16_384;", source);
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
        Assert.InRange(cap, 512L * 1024 * 1024, 768L * 1024 * 1024);
    }

    // ── Image-text (OCR) search wiring ──
    // The OCR session is set up and driven from inside the large SearchAsync orchestration
    // (Rust scan + channels + content workers). That happy path only runs against a full search
    // over real image files with a live worker, so it is pinned at the source level here.

    [Fact]
    public void ImageOcr_BypassesSkipExtensionsSoImagesReachTheOcrQueue()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "Services", "SearchService.cs"));

        Assert.Contains("bool bypassImages = options.SearchImageText && skipExts.Count > 0 && options.ImageOcrExtensions.Count > 0;", source);
        AssertContainsInOrder(source,
            "if (bypassArchives || bypassImages)",
            "if (bypassImages)",
            "foreach (var ext in options.ImageOcrExtensions)",
            "filtered.Remove(ext.TrimStart('.'));");
    }

    [Fact]
    public void ImageOcr_SessionIsCreatedOnlyWhenContentSearchAndImageTextEnabled()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "Services", "SearchService.cs"));

        AssertContainsInOrder(source,
            "if (searchContent && options.SearchImageText)",
            "ocrEngine = Ocr.OcrEngineFactory.Create(options.ImageOcrEngine, options.ImageOcrModel, options.ImageOcrMaxSide);",
            "Ocr.OcrTextCache.Cleanup();",
            "var ocrCache = new Ocr.OcrTextCache();",
            "imageOcr = new Ocr.ImageOcrSearchSession(",
            "shouldStop: () => Volatile.Read(ref truncated) != 0);");
    }

    [Fact]
    public void ImageOcr_RoutesImageCandidatesToTheQueueOnBothScanPaths()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "Services", "SearchService.cs"));

        // The session is started alongside the content workers.
        Assert.Contains("imageOcr?.Start();", source);
        // Image candidates are diverted to the OCR queue instead of the native/managed content scanner.
        Assert.Contains("else if (imageOcr != null && Ocr.ImageOcrSupport.IsImageCandidate(file, options.ImageOcrExtensions))", source);
        Assert.Contains("if (imageOcr != null && Ocr.ImageOcrSupport.IsImageCandidate(file, options.ImageOcrExtensions))", source);
        Assert.Contains("imageOcr.TryEnqueue(file);", source);
    }

    [Fact]
    public void ImageOcr_DrainsAndDisposesEngineInFinallyBeforeClosingResults()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Yagu", "Services", "SearchService.cs"));

        AssertContainsInOrder(source,
            "if (imageOcr != null)",
            "imageOcr.Complete();",
            "await imageOcr.DrainAsync().ConfigureAwait(false);",
            "if (ocrEngine is IAsyncDisposable ocrEngineAsyncDisposable)",
            "await ocrEngineAsyncDisposable.DisposeAsync().ConfigureAwait(false);",
            "else if (ocrEngine is IDisposable ocrEngineDisposable)",
            "ocrEngineDisposable.Dispose();");
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

    private static void AssertContainsInOrder(string text, params string[] expected)
    {
        int cursor = 0;
        foreach (string value in expected)
        {
            int index = text.IndexOf(value, cursor, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Expected to find '{value}' after offset {cursor}.");
            cursor = index + value.Length;
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
