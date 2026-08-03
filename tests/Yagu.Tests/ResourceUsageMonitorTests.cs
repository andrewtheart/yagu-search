using System.Diagnostics;
using Yagu.Services;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Unit tests for <see cref="ResourceUsageMonitor"/> — the formatting and storage sizing behind the
/// status-bar Temp, Index, and RAM indicators. The platform-specific process probing is exercised through
/// seams; here we pin backend selection, command construction, number crunching, and metadata reads.
/// </summary>
public class ResourceUsageMonitorTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1, "1 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1.0 MB")]
    [InlineData(1073741824, "1.0 GB")]
    [InlineData(68719476736, "64.0 GB")]
    public void FormatBytes_UsesBinaryUnits(long bytes, string expected)
        => Assert.Equal(expected, ResourceUsageMonitor.FormatBytes(bytes));

    [Fact]
    public void FormatBytes_NegativeClampsToZero()
        => Assert.Equal("0 B", ResourceUsageMonitor.FormatBytes(-5));

    [Fact]
    public void FormatTempStatus_PrefixesLabel()
        => Assert.Equal("Temp: 1.0 GB", ResourceUsageMonitor.FormatTempStatus(1073741824));

    [Fact]
    public void FormatIndexStatus_PrefixesLabel()
        => Assert.Equal("Index: 1.0 GB", ResourceUsageMonitor.FormatIndexStatus(1073741824));

    [Theory]
    [InlineData(@"C:\IndexData", @"file:C:\IndexData\")]
    [InlineData(@"C:\Index Data", "file:\"C:\\Index Data\\\"")]
    public void BuildEverythingIndexStorageQuery_ScopesFilesToDirectoryTree(string root, string expected)
        => Assert.Equal(expected, ResourceUsageMonitor.BuildEverythingIndexStorageQuery(root));

    [Fact]
    public void TrySumIndexStorageWithEverything_RequestsOnlySizesAndSumsAllResults()
    {
        var sdk = new FakeIndexStorageEverythingOps(100, 50, 25);

        bool success = ResourceUsageMonitor.TrySumIndexStorageWithEverything(
            @"C:\Index Data",
            sdk,
            CancellationToken.None,
            out long total);

        Assert.True(success);
        Assert.Equal(175, total);
        Assert.Equal("file:\"C:\\Index Data\\\"", sdk.Search);
        Assert.Equal(uint.MaxValue, sdk.Max);
        Assert.Equal(Yagu.Native.EverythingSdk.EVERYTHING_REQUEST_SIZE, sdk.RequestFlags);
        Assert.True(sdk.QueryWait);
        Assert.True(sdk.ResetCount >= 2);
    }

    [Fact]
    public void TrySumIndexStorageWithEverything_NoResultsRequestsFallback()
    {
        var sdk = new FakeIndexStorageEverythingOps();

        bool success = ResourceUsageMonitor.TrySumIndexStorageWithEverything(
            @"C:\IndexData",
            sdk,
            CancellationToken.None,
            out long total);

        Assert.False(success);
        Assert.Equal(0, total);
    }

    [Fact]
    public void TrySumIndexStorageWithEverything_DbNotLoadedRequestsFallback()
    {
        var sdk = new FakeIndexStorageEverythingOps(dbLoaded: false);

        bool success = ResourceUsageMonitor.TrySumIndexStorageWithEverything(
            @"C:\IndexData",
            sdk,
            CancellationToken.None,
            out long total);

        Assert.False(success);
        Assert.Equal(0, total);
    }

    [Fact]
    public void TrySumIndexStorageWithEverything_QueryFailureRequestsFallback()
    {
        var sdk = new FakeIndexStorageEverythingOps(sizes: new[] { 10L }, queryResult: false);

        bool success = ResourceUsageMonitor.TrySumIndexStorageWithEverything(
            @"C:\IndexData",
            sdk,
            CancellationToken.None,
            out long total);

        Assert.False(success);
        Assert.Equal(0, total);
    }

    [Fact]
    public void TrySumIndexStorageWithEverything_IncompleteTotalRequestsFallback()
    {
        var sdk = new FakeIndexStorageEverythingOps(sizes: new[] { 10L }, totalResults: 2);

        bool success = ResourceUsageMonitor.TrySumIndexStorageWithEverything(
            @"C:\IndexData",
            sdk,
            CancellationToken.None,
            out long total);

        Assert.False(success);
        Assert.Equal(0, total);
    }

    [Fact]
    public void TrySumIndexStorageWithEverything_ResultSizeFailureRequestsFallback()
    {
        var sdk = new FakeIndexStorageEverythingOps(sizes: new[] { 10L }, failResultSizeAt: 0);

        bool success = ResourceUsageMonitor.TrySumIndexStorageWithEverything(
            @"C:\IndexData",
            sdk,
            CancellationToken.None,
            out long total);

        Assert.False(success);
        Assert.Equal(0, total);
    }

    [Fact]
    public void TrySumIndexStorageWithEverything_NegativeResultSizeRequestsFallback()
    {
        var sdk = new FakeIndexStorageEverythingOps(sizes: new[] { 10L }, negativeResultSizeAt: 0);

        bool success = ResourceUsageMonitor.TrySumIndexStorageWithEverything(
            @"C:\IndexData",
            sdk,
            CancellationToken.None,
            out long total);

        Assert.False(success);
        Assert.Equal(0, total);
    }

    [Fact]
    public void TrySumIndexStorageWithEverything_CancellationIsPropagated()
    {
        var sdk = new FakeIndexStorageEverythingOps(sizes: new[] { 1L, 2L, 3L });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            ResourceUsageMonitor.TrySumIndexStorageWithEverything(
                @"C:\IndexData",
                sdk,
                cts.Token,
                out _));
    }

    [Fact]
    public void TrySumIndexStorageWithEverything_NativeExceptionFallsBackAndStillResets()
    {
        var sdk = new FakeIndexStorageEverythingOps(throwNativeOnQuery: true, throwNativeOnReset: true);

        bool success = ResourceUsageMonitor.TrySumIndexStorageWithEverything(
            @"C:\IndexData",
            sdk,
            CancellationToken.None,
            out long total);

        Assert.False(success);
        Assert.Equal(0, total);
        Assert.True(sdk.ResetCount >= 1);
    }

    [Fact]
    public void BuildEsTotalSizeStartInfo_RequestsRawUndelimitedTotalOnly()
    {
        ProcessStartInfo startInfo = ResourceUsageMonitor.BuildEsTotalSizeStartInfo(
            @"C:\tools\es.exe",
            "file:\"C:\\Index Data\\\"");

        Assert.Equal(@"C:\tools\es.exe", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal(
            new[] { "-get-total-size", "-size-format", "1", "-no-digit-grouping", "file:\"C:\\Index Data\\\"" },
            startInfo.ArgumentList);
    }

    [Fact]
    public void MeasureTotalIndexStorageBytes_ForcedEsExeUsesProcessBackendNotSdk()
    {
        string dir = Directory.CreateTempSubdirectory("yagu-index-es-test-").FullName;
        try
        {
            var sdk = new FakeIndexStorageEverythingOps(999);
            var esExe = new FakeIndexStorageEsExeOps(321);

            IndexStorageSizeMeasurement result = ResourceUsageMonitor.MeasureTotalIndexStorageBytes(
                dir,
                FileListerBackend.EsExe,
                sdk,
                esExe,
                CancellationToken.None);

            Assert.Equal(321, result.Bytes);
            Assert.Equal(IndexStorageSizeSource.EsExe, result.Source);
            Assert.Equal(0, sdk.DatabaseLoadedChecks);
            Assert.Equal(1, esExe.MeasurementCalls);
            Assert.Equal(ResourceUsageMonitor.BuildEverythingIndexStorageQuery(dir), esExe.SearchQuery);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void MeasureTotalIndexStorageBytes_ForcedSdkUsesSdkNotProcessBackend()
    {
        string dir = Directory.CreateTempSubdirectory("yagu-index-sdk-test-").FullName;
        try
        {
            var sdk = new FakeIndexStorageEverythingOps(456);
            var esExe = new FakeIndexStorageEsExeOps(999);

            IndexStorageSizeMeasurement result = ResourceUsageMonitor.MeasureTotalIndexStorageBytes(
                dir,
                FileListerBackend.EverythingSdk,
                sdk,
                esExe,
                CancellationToken.None);

            Assert.Equal(456, result.Bytes);
            Assert.Equal(IndexStorageSizeSource.Everything, result.Source);
            Assert.Equal(1, sdk.DatabaseLoadedChecks);
            Assert.Equal(0, esExe.MeasurementCalls);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void MeasureTotalIndexStorageBytes_AutoFallsFromSdkToEsExe()
    {
        string dir = Directory.CreateTempSubdirectory("yagu-index-auto-test-").FullName;
        try
        {
            var sdk = new FakeIndexStorageEverythingOps();
            var esExe = new FakeIndexStorageEsExeOps(654);

            IndexStorageSizeMeasurement result = ResourceUsageMonitor.MeasureTotalIndexStorageBytes(
                dir,
                FileListerBackend.Auto,
                sdk,
                esExe,
                CancellationToken.None);

            Assert.Equal(654, result.Bytes);
            Assert.Equal(IndexStorageSizeSource.EsExe, result.Source);
            Assert.Equal(1, sdk.DatabaseLoadedChecks);
            Assert.Equal(1, esExe.MeasurementCalls);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void MeasureTotalIndexStorageBytes_EmptyRootReturnsZeroAndSkipsBackends()
    {
        var sdk = new FakeIndexStorageEverythingOps(999);
        var esExe = new FakeIndexStorageEsExeOps(888);

        IndexStorageSizeMeasurement result = ResourceUsageMonitor.MeasureTotalIndexStorageBytes(
            "  ",
            FileListerBackend.Auto,
            sdk,
            esExe,
            _ => throw new InvalidOperationException("directoryExists should not run"),
            (_, _) => throw new InvalidOperationException("managed fallback should not run"),
            CancellationToken.None);

        Assert.Equal(0, result.Bytes);
        Assert.Equal(IndexStorageSizeSource.FileSystem, result.Source);
        Assert.True(result.Complete);
        Assert.Equal(0, sdk.DatabaseLoadedChecks);
        Assert.Equal(0, esExe.MeasurementCalls);
    }

    [Fact]
    public void MeasureTotalIndexStorageBytes_ForcedEsExeFallsBackToManagedWhenUnavailable()
    {
        var sdk = new FakeIndexStorageEverythingOps(999);
        var esExe = new FakeIndexStorageEsExeOps(0);
        int managedCalls = 0;

        IndexStorageSizeMeasurement result = ResourceUsageMonitor.MeasureTotalIndexStorageBytes(
            @"C:\Index",
            FileListerBackend.EsExe,
            sdk,
            esExe,
            _ => true,
            (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                managedCalls++;
                return new IndexStorageSizeMeasurement(42, IndexStorageSizeSource.FileSystem, Complete: false);
            },
            CancellationToken.None);

        Assert.Equal(42, result.Bytes);
        Assert.Equal(IndexStorageSizeSource.FileSystem, result.Source);
        Assert.False(result.Complete);
        Assert.Equal(1, esExe.MeasurementCalls);
        Assert.Equal(1, managedCalls);
    }

    [Fact]
    public void MeasureTotalIndexStorageBytes_ManagedFallbackIsCancellationAware()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            ResourceUsageMonitor.MeasureTotalIndexStorageBytes(
                @"C:\Index",
                FileListerBackend.Managed,
                new FakeIndexStorageEverythingOps(111),
                new FakeIndexStorageEsExeOps(222),
                _ => true,
                (_, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return new IndexStorageSizeMeasurement(0, IndexStorageSizeSource.FileSystem, Complete: true);
                },
                cts.Token));
    }

    [Fact]
    public void MeasureTotalIndexStorageBytes_NullSeamsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ResourceUsageMonitor.MeasureTotalIndexStorageBytes(
                @"C:\Index",
                FileListerBackend.Managed,
                new FakeIndexStorageEverythingOps(1),
                new FakeIndexStorageEsExeOps(1),
                null!,
                (_, _) => new IndexStorageSizeMeasurement(0, IndexStorageSizeSource.FileSystem, Complete: true),
                CancellationToken.None));

        Assert.Throws<ArgumentNullException>(() =>
            ResourceUsageMonitor.MeasureTotalIndexStorageBytes(
                @"C:\Index",
                FileListerBackend.Managed,
                new FakeIndexStorageEverythingOps(1),
                new FakeIndexStorageEsExeOps(1),
                _ => true,
                null!,
                CancellationToken.None));
    }

    [Fact]
    public void MeasureTotalIndexStorageBytes_ManagedSkipsEverythingBackends()
    {
        string dir = Directory.CreateTempSubdirectory("yagu-index-managed-test-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "index.bin"), new byte[77]);
            var sdk = new FakeIndexStorageEverythingOps(999);
            var esExe = new FakeIndexStorageEsExeOps(888);

            IndexStorageSizeMeasurement result = ResourceUsageMonitor.MeasureTotalIndexStorageBytes(
                dir,
                FileListerBackend.Managed,
                sdk,
                esExe,
                CancellationToken.None);

            Assert.Equal(77, result.Bytes);
            Assert.Equal(IndexStorageSizeSource.FileSystem, result.Source);
            Assert.Equal(0, sdk.DatabaseLoadedChecks);
            Assert.Equal(0, esExe.MeasurementCalls);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SumIndexStorageWithFileSystem_CountsNestedFiles()
    {
        string dir = Path.Combine(Path.GetTempPath(), "yagu-index-size-test-" + Guid.NewGuid().ToString("N"));
        string nested = Path.Combine(dir, "scope", "generation");
        Directory.CreateDirectory(nested);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "pointer"), new byte[10]);
            File.WriteAllBytes(Path.Combine(nested, "postings"), new byte[90]);

            IndexStorageSizeMeasurement result =
                ResourceUsageMonitor.SumIndexStorageWithFileSystem(dir, CancellationToken.None);

            Assert.Equal(100, result.Bytes);
            Assert.Equal(IndexStorageSizeSource.FileSystem, result.Source);
            Assert.True(result.Complete);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void BuildIndexTooltip_ExplainsScopeSourceAndRefreshCadence()
    {
        string root = @"C:\Index Data";
        var measurement = new IndexStorageSizeMeasurement(
            1073741824,
            IndexStorageSizeSource.Everything,
            Complete: true);

        string tip = ResourceUsageMonitor.BuildIndexTooltip(measurement, root);

        Assert.Contains("all Yagu content-index data (1.0 GB)", tip);
        Assert.Contains("every indexed folder", tip);
        Assert.Contains("Everything SDK", tip);
        Assert.Contains("at most once per minute", tip);
        Assert.Contains(root, tip);
    }

    [Fact]
    public void BuildIndexTooltip_IncompleteMeasurementIncludesRetryNotice()
    {
        var measurement = new IndexStorageSizeMeasurement(128, IndexStorageSizeSource.FileSystem, Complete: false);

        string tip = ResourceUsageMonitor.BuildIndexTooltip(measurement, @"C:\Index");

        Assert.Contains("background filesystem metadata scan", tip);
        Assert.Contains("could not be counted", tip);
    }

    [Fact]
    public void BuildIndexTooltip_EsExeSourceIsReported()
    {
        string tip = ResourceUsageMonitor.BuildIndexTooltip(
            new IndexStorageSizeMeasurement(256, IndexStorageSizeSource.EsExe, Complete: true),
            @"C:\Index");

        Assert.Contains("Measured through es.exe", tip);
    }

    [Fact]
    public void FormatRamStatus_IncludesUsedTotalAndPercent()
    {
        // 2.4 GB of 64 GB ≈ 3.7% (the byte cast truncates the fractional 0.6 byte).
        long used = (long)(2.4 * 1024 * 1024 * 1024);
        long total = 68719476736; // 64 GiB
        string label = ResourceUsageMonitor.FormatRamStatus(used, total);
        Assert.StartsWith("RAM: ", label);
        Assert.Contains("2.4 GB / 64.0 GB", label);
        Assert.Contains("(3.7%)", label);
    }

    [Fact]
    public void FormatRamStatus_UnknownTotalOmitsSuffix()
    {
        string label = ResourceUsageMonitor.FormatRamStatus(1073741824, 0);
        Assert.Equal("RAM: 1.0 GB", label);
        Assert.DoesNotContain("/", label);
        Assert.DoesNotContain("%", label);
    }

    [Theory]
    [InlineData(0, 0, 0.0)]
    [InlineData(50, 100, 50.0)]
    [InlineData(200, 100, 100.0)] // clamped
    [InlineData(10, 0, 0.0)]      // unknown total
    public void RamPercent_ClampsAndGuardsZeroTotal(long used, long total, double expected)
        => Assert.Equal(expected, ResourceUsageMonitor.RamPercent(used, total), 3);

    [Fact]
    public void TempResultSearchPatternForProcess_MatchesResultStoreNaming()
        => Assert.Equal("yagu-results-p1234-*.tmp", ResourceUsageMonitor.TempResultSearchPatternForProcess(1234));

    [Fact]
    public void SumProcessTempResultBytes_CountsOnlyThisProcessTempFiles()
    {
        string dir = Path.Combine(Path.GetTempPath(), "yagu-resmon-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            int pid = 4242;
            // Two files for this process (100 + 50 bytes).
            File.WriteAllBytes(Path.Combine(dir, $"yagu-results-p{pid}-{Guid.NewGuid():N}.tmp"), new byte[100]);
            File.WriteAllBytes(Path.Combine(dir, $"yagu-results-p{pid}-{Guid.NewGuid():N}.tmp"), new byte[50]);
            // A different process's file — must NOT be counted.
            File.WriteAllBytes(Path.Combine(dir, $"yagu-results-p9999-{Guid.NewGuid():N}.tmp"), new byte[999]);
            // An unrelated file — must NOT be counted.
            File.WriteAllBytes(Path.Combine(dir, "unrelated.txt"), new byte[7]);

            long total = ResourceUsageMonitor.SumProcessTempResultBytes(dir, pid);
            Assert.Equal(150, total);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SumProcessTempResultBytes_MissingDirectoryReturnsZero()
    {
        string missing = Path.Combine(Path.GetTempPath(), "yagu-resmon-missing-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(0, ResourceUsageMonitor.SumProcessTempResultBytes(missing, 1));
    }

    [Fact]
    public void SumProcessTempResultBytes_SkipsFileLengthFailures()
    {
        long total = ResourceUsageMonitor.SumProcessTempResultBytes(
            @"C:\Temp",
            7,
            _ => true,
            (_, _) => new[] { "ok", "gone" },
            path => path == "ok" ? 12 : throw new IOException("vanished"));

        Assert.Equal(12, total);
    }

    [Fact]
    public void SumProcessTempResultBytes_DirectoryProbeFailureReturnsPartialTotal()
    {
        long total = ResourceUsageMonitor.SumProcessTempResultBytes(
            @"C:\Temp",
            7,
            _ => throw new UnauthorizedAccessException("denied"),
            (_, _) => throw new InvalidOperationException("should not enumerate"),
            _ => throw new InvalidOperationException("should not read lengths"));

        Assert.Equal(0, total);
    }

    [Fact]
    public void SumProcessTempResultBytes_NullDirectoryFallsBackToSystemTempPath()
    {
        string? observedDirectory = null;

        _ = ResourceUsageMonitor.SumProcessTempResultBytes(
            null,
            11,
            dir =>
            {
                observedDirectory = dir;
                return false;
            },
            (_, _) => Array.Empty<string>(),
            _ => 0);

        Assert.Equal(Path.GetTempPath(), observedDirectory);
    }

    [Fact]
    public void BuildTempTooltip_MentionsUsageAndLocation()
    {
        string dir = Path.Combine(Path.GetTempPath(), "yagu-tip");
        string tip = ResourceUsageMonitor.BuildTempTooltip(1073741824, dir);
        Assert.Contains("1.0 GB", tip);
        Assert.Contains(dir, tip);
        Assert.Contains("evicted search results", tip);
    }

    [Fact]
    public void BuildTempTooltip_NullDirectoryFallsBackToSystemTempPath()
    {
        string tip = ResourceUsageMonitor.BuildTempTooltip(1, null);
        Assert.Contains(Path.GetTempPath(), tip);
    }

    [Fact]
    public void BuildRamTooltip_ListsBreakdownAndTotal()
    {
        var breakdown = new List<(string Name, long Bytes)>
        {
            ("Yagu", 1073741824),               // 1 GB
            ("Yagu.IndexWorker", 536870912),    // 0.5 GB
        };
        long used = 1610612736;                 // 1.5 GB
        long total = 68719476736;               // 64 GiB
        string tip = ResourceUsageMonitor.BuildRamTooltip(breakdown, used, total);
        Assert.Contains("Yagu: 1.0 GB", tip);
        Assert.Contains("Yagu.IndexWorker: 512.0 MB", tip);
        Assert.Contains("Total: 1.5 GB of 64.0 GB", tip);
    }

    [Fact]
    public void BuildRamTooltip_UnknownTotalOmitsPercentSuffix()
    {
        string tip = ResourceUsageMonitor.BuildRamTooltip(
            new List<(string Name, long Bytes)> { ("Yagu", 100) },
            usedBytes: 100,
            totalBytes: 0);

        Assert.Contains("Total: 100 B", tip);
        Assert.DoesNotContain("of", tip);
    }

    [Fact]
    public void GetTotalPhysicalMemoryBytes_PrefersInstalledMemory()
    {
        long total = ResourceUsageMonitor.GetTotalPhysicalMemoryBytes(
            () => (true, 1024),
            () => (true, 2048UL));

        Assert.Equal(1024L * 1024L, total);
    }

    [Fact]
    public void GetTotalPhysicalMemoryBytes_FallsBackWhenInstalledUnavailable()
    {
        long total = ResourceUsageMonitor.GetTotalPhysicalMemoryBytes(
            () => (false, 0),
            () => (true, 4096UL));

        Assert.Equal(4096L, total);
    }

    [Fact]
    public void GetTotalPhysicalMemoryBytes_FallsBackWhenInstalledProbeThrows()
    {
        long total = ResourceUsageMonitor.GetTotalPhysicalMemoryBytes(
            () => throw new InvalidOperationException("probe failed"),
            () => (true, 8192UL));

        Assert.Equal(8192L, total);
    }

    [Fact]
    public void GetTotalPhysicalMemoryBytes_ReturnsZeroWhenBothProbesFail()
    {
        long total = ResourceUsageMonitor.GetTotalPhysicalMemoryBytes(
            () => (false, 0),
            () => (false, 0));

        Assert.Equal(0, total);
    }

    [Fact]
    public void GetTotalPhysicalMemoryBytes_NullSeamsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ResourceUsageMonitor.GetTotalPhysicalMemoryBytes(
                null!,
                () => (true, 1UL)));

        Assert.Throws<ArgumentNullException>(() =>
            ResourceUsageMonitor.GetTotalPhysicalMemoryBytes(
                () => (true, 1),
                null!));
    }

    [Fact]
    public void GetTotalPhysicalMemoryBytes_FallsBackWhenSecondProbeThrows()
    {
        long total = ResourceUsageMonitor.GetTotalPhysicalMemoryBytes(
            () => (false, 0),
            () => throw new InvalidOperationException("no probe"));

        Assert.Equal(0, total);
    }

    [Fact]
    public void GetTotalPhysicalMemoryBytes_SystemProbePathIsCallable()
    {
        long total = ResourceUsageMonitor.GetTotalPhysicalMemoryBytes();
        Assert.True(total >= 0);
    }

    [Fact]
    public void TrySumIndexStorageWithEsExe_MissingExecutableReturnsFalse()
    {
        bool ok = ResourceUsageMonitor.TrySumIndexStorageWithEsExe(
            @"C:\Index",
            new FakeIndexStorageEsExeOps(100, executableFound: false),
            CancellationToken.None,
            out long total);

        Assert.False(ok);
        Assert.Equal(0, total);
    }

    [Fact]
    public void TrySumIndexStorageWithEsExe_FailedMeasurementReturnsFalse()
    {
        bool ok = ResourceUsageMonitor.TrySumIndexStorageWithEsExe(
            @"C:\Index",
            new FakeIndexStorageEsExeOps(100, executableFound: true, succeeds: false),
            CancellationToken.None,
            out long total);

        Assert.False(ok);
        Assert.Equal(0, total);
    }

    [Fact]
    public void TrySumIndexStorageWithEsExe_NonPositiveMeasurementReturnsFalse()
    {
        bool ok = ResourceUsageMonitor.TrySumIndexStorageWithEsExe(
            @"C:\Index",
            new FakeIndexStorageEsExeOps(0),
            CancellationToken.None,
            out long total);

        Assert.False(ok);
        Assert.Equal(0, total);
    }

    [Fact]
    public void TrySumIndexStorageWithEsExe_CancellationIsPropagated()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            ResourceUsageMonitor.TrySumIndexStorageWithEsExe(
                @"C:\Index",
                new FakeIndexStorageEsExeOps(10),
                cts.Token,
                out _));
    }

    [Fact]
    public void MeasureTotalIndexStorageBytes_DefaultOverload_MissingRootReturnsZero()
    {
        string missing = Path.Combine(Path.GetTempPath(), "yagu-index-missing-" + Guid.NewGuid().ToString("N"));

        IndexStorageSizeMeasurement result = ResourceUsageMonitor.MeasureTotalIndexStorageBytes(
            missing,
            FileListerBackend.Managed,
            CancellationToken.None);

        Assert.Equal(0, result.Bytes);
        Assert.True(result.Complete);
    }

    [Fact]
    public void SumIndexStorageWithFileSystem_MissingRootReturnsZeroComplete()
    {
        string missing = Path.Combine(Path.GetTempPath(), "yagu-index-missing-" + Guid.NewGuid().ToString("N"));

        IndexStorageSizeMeasurement result = ResourceUsageMonitor.SumIndexStorageWithFileSystem(
            missing,
            CancellationToken.None);

        Assert.Equal(0, result.Bytes);
        Assert.True(result.Complete);
    }

    [Fact]
    public void SumIndexStorageWithFileSystem_FileLengthFailuresMarkIncomplete()
    {
        IndexStorageSizeMeasurement result = ResourceUsageMonitor.SumIndexStorageWithFileSystem(
            @"C:\Index",
            CancellationToken.None,
            _ => true,
            _ => new[] { "ok", "bad" },
            path => path == "ok" ? 5L : throw new UnauthorizedAccessException("denied"));

        Assert.Equal(5, result.Bytes);
        Assert.False(result.Complete);
    }

    [Fact]
    public void SumIndexStorageWithFileSystem_EnumerationFailureMarksIncomplete()
    {
        IndexStorageSizeMeasurement result = ResourceUsageMonitor.SumIndexStorageWithFileSystem(
            @"C:\Index",
            CancellationToken.None,
            _ => true,
            _ => throw new IOException("scan failed"),
            _ => 1L);

        Assert.Equal(0, result.Bytes);
        Assert.False(result.Complete);
    }

    [Fact]
    public void SumIndexStorageWithFileSystem_UsesSaturatingAddForLargeTotals()
    {
        var sizes = new Dictionary<string, long>
        {
            ["first"] = long.MaxValue,
            ["second"] = 1,
        };

        IndexStorageSizeMeasurement result = ResourceUsageMonitor.SumIndexStorageWithFileSystem(
            @"C:\Index",
            CancellationToken.None,
            _ => true,
            _ => sizes.Keys,
            path => sizes[path]);

        Assert.Equal(long.MaxValue, result.Bytes);
        Assert.True(result.Complete);
    }

    [Fact]
    public void SumIndexStorageWithFileSystem_CancellationIsPropagated()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            ResourceUsageMonitor.SumIndexStorageWithFileSystem(
                @"C:\Index",
                cts.Token,
                _ => true,
                _ => new[] { "one" },
                _ => 1L));
    }

    [Fact]
    public void SumIndexStorageWithFileSystem_NullSeamsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ResourceUsageMonitor.SumIndexStorageWithFileSystem(
                @"C:\Index",
                CancellationToken.None,
                null!,
                _ => Array.Empty<string>(),
                _ => 0L));

        Assert.Throws<ArgumentNullException>(() =>
            ResourceUsageMonitor.SumIndexStorageWithFileSystem(
                @"C:\Index",
                CancellationToken.None,
                _ => true,
                null!,
                _ => 0L));

        Assert.Throws<ArgumentNullException>(() =>
            ResourceUsageMonitor.SumIndexStorageWithFileSystem(
                @"C:\Index",
                CancellationToken.None,
                _ => true,
                _ => Array.Empty<string>(),
                null!));
    }

    private sealed class FakeIndexStorageEverythingOps : IEverythingSdkOps
    {
        private readonly long[] _sizes;
        private readonly bool _dbLoaded;
        private readonly bool _queryResult;
        private readonly uint? _totalResults;
        private readonly uint? _failResultSizeAt;
        private readonly uint? _negativeResultSizeAt;
        private readonly bool _throwNativeOnQuery;
        private readonly bool _throwNativeOnReset;

        public FakeIndexStorageEverythingOps(
            params long[] sizes)
            : this(sizes: sizes, dbLoaded: true, queryResult: true, totalResults: null, failResultSizeAt: null, negativeResultSizeAt: null, throwNativeOnQuery: false, throwNativeOnReset: false)
        {
        }

        public FakeIndexStorageEverythingOps(
            long[]? sizes = null,
            bool dbLoaded = true,
            bool queryResult = true,
            uint? totalResults = null,
            uint? failResultSizeAt = null,
            uint? negativeResultSizeAt = null,
            bool throwNativeOnQuery = false,
            bool throwNativeOnReset = false)
        {
            _sizes = sizes ?? Array.Empty<long>();
            _dbLoaded = dbLoaded;
            _queryResult = queryResult;
            _totalResults = totalResults;
            _failResultSizeAt = failResultSizeAt;
            _negativeResultSizeAt = negativeResultSizeAt;
            _throwNativeOnQuery = throwNativeOnQuery;
            _throwNativeOnReset = throwNativeOnReset;
        }

        public object SyncLock { get; } = new();
        public string Search { get; private set; } = string.Empty;
        public uint Max { get; private set; }
        public uint RequestFlags { get; private set; }
        public bool QueryWait { get; private set; }
        public int ResetCount { get; private set; }
        public int DatabaseLoadedChecks { get; private set; }

        public bool IsDBLoaded()
        {
            DatabaseLoadedChecks++;
            return _dbLoaded;
        }
        public void Reset()
        {
            ResetCount++;
            if (_throwNativeOnReset)
                throw new DllNotFoundException("Everything64.dll");
        }
        public void SetSearch(string searchString) => Search = searchString;
        public void SetMatchCase(bool matchCase) { }
        public void SetMatchPath(bool matchPath) { }
        public void SetOffset(uint offset) { }
        public void SetMax(uint max) => Max = max;
        public void SetRequestFlags(uint flags) => RequestFlags = flags;
        public bool Query(bool wait)
        {
            QueryWait = wait;
            if (_throwNativeOnQuery)
                throw new DllNotFoundException("Everything64.dll");
            return _queryResult;
        }
        public uint GetNumResults() => (uint)_sizes.Length;
        public uint GetTotResults() => _totalResults ?? (uint)_sizes.Length;
        public uint GetLastError() => 0;
        public string ErrorMessage(uint error) => string.Empty;
        public bool GetResultSize(uint index, out long size)
        {
            if (_failResultSizeAt.HasValue && _failResultSizeAt.Value == index)
            {
                size = 0;
                return false;
            }

            size = (_negativeResultSizeAt.HasValue && _negativeResultSizeAt.Value == index)
                ? -1
                : _sizes[index];
            return true;
        }
        public bool GetResultDateCreated(uint index, out long fileTime)
        {
            fileTime = 0;
            return false;
        }
        public bool GetResultDateModified(uint index, out long fileTime)
        {
            fileTime = 0;
            return false;
        }
        public uint GetResultFullPathName(uint index, char[] buffer, uint capacity) => 0;
    }

    private sealed class FakeIndexStorageEsExeOps(long totalBytes, bool executableFound = true, bool succeeds = true) : IIndexStorageEsExeOps
    {
        private readonly bool _executableFound = executableFound;
        private readonly bool _succeeds = succeeds;

        public int MeasurementCalls { get; private set; }
        public string SearchQuery { get; private set; } = string.Empty;

        public string? FindExecutable() => _executableFound ? @"C:\tools\es.exe" : null;

        public bool TryGetTotalSize(
            string executable,
            string searchQuery,
            CancellationToken cancellationToken,
            out long measuredBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MeasurementCalls++;
            SearchQuery = searchQuery;
            measuredBytes = totalBytes;
            return _succeeds;
        }
    }
}
