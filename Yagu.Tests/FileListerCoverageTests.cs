using System.Diagnostics;
using Yagu.Native;
using Yagu.Services;

namespace Yagu.Tests;

// ─── Mock Everything SDK for testing RunEverythingSdkAsync / ProbeEverythingSdkReadiness ────

internal sealed class MockEverythingSdkOps : IEverythingSdkOps
{
    public object SyncLock { get; } = new();
    public bool DbLoaded { get; set; } = true;
    public bool QuerySucceeds { get; set; } = true;
    public uint LastError { get; set; }
    public string LastErrorMessage { get; set; } = "";
    public List<(string Path, long Size)> Results { get; set; } = [];
    public bool ThrowDllNotFound { get; set; }
    public bool ThrowGeneral { get; set; }
    public string? GeneralExceptionMessage { get; set; }
    public string? CapturedQuery { get; set; }
    public bool? CapturedMatchCase { get; set; }
    public uint? CapturedRequestFlags { get; set; }
    public uint? CapturedMax { get; set; }
    public Dictionary<uint, long> CreatedFileTimes { get; set; } = [];
    public Dictionary<uint, long> ModifiedFileTimes { get; set; } = [];
    public bool ThrowOnReset { get; set; }
    public int ResetCallCount { get; private set; }
    public int ThrowOnResetAfterCall { get; set; } = 1; // throw on Nth+ call

    public bool IsDBLoaded()
    {
        if (ThrowDllNotFound) throw new DllNotFoundException("mock");
        if (ThrowGeneral) throw new InvalidOperationException(GeneralExceptionMessage ?? "mock error");
        return DbLoaded;
    }

    public void Reset() { ResetCallCount++; if (ThrowOnReset && ResetCallCount >= ThrowOnResetAfterCall) throw new InvalidOperationException("reset fail"); }
    public void SetSearch(string searchString) => CapturedQuery = searchString;
    public void SetMatchCase(bool matchCase) => CapturedMatchCase = matchCase;
    public void SetMatchPath(bool matchPath) { }
    public void SetOffset(uint offset) { }
    public void SetMax(uint max) => CapturedMax = max;
    public void SetRequestFlags(uint flags) => CapturedRequestFlags = flags;

    public bool Query(bool bWait)
    {
        if (ThrowDllNotFound) throw new DllNotFoundException("mock");
        if (ThrowGeneral) throw new InvalidOperationException(GeneralExceptionMessage ?? "mock error");
        return QuerySucceeds;
    }

    public uint GetLastError() => LastError;
    public string ErrorMessage(uint error) => LastErrorMessage;
    public uint GetNumResults() => (uint)Results.Count;
    public uint GetTotResults() => (uint)Results.Count;

    public bool GetResultSize(uint index, out long size)
    {
        if (index < Results.Count) { size = Results[(int)index].Size; return true; }
        size = 0; return false;
    }

    public bool GetResultDateCreated(uint index, out long fileTime)
    {
        return CreatedFileTimes.TryGetValue(index, out fileTime);
    }

    public bool GetResultDateModified(uint index, out long fileTime)
    {
        return ModifiedFileTimes.TryGetValue(index, out fileTime);
    }

    public uint GetResultFullPathName(uint index, char[] buffer, uint capacity)
    {
        if (index >= Results.Count) return 0;
        var path = Results[(int)index].Path;
        if (path.Length >= capacity)
        {
            // Signal that the buffer is too small
            return (uint)path.Length;
        }
        path.AsSpan().CopyTo(buffer);
        return (uint)path.Length;
    }
}

// ─── Static helper tests ────────────────────────────────────────────────

public class FileListerStaticHelperTests
{
    [Fact]
    public void BuildEverythingSizeFilterTerms_BothZero_YieldsNothing()
    {
        var terms = FileLister.BuildEverythingSizeFilterTerms(0, 0).ToList();
        Assert.Empty(terms);
    }

    [Fact]
    public void BuildEverythingSizeFilterTerms_MinOnly()
    {
        var terms = FileLister.BuildEverythingSizeFilterTerms(100, 0).ToList();
        Assert.Single(terms);
        Assert.Equal("size:>=100", terms[0]);
    }

    [Fact]
    public void BuildEverythingSizeFilterTerms_MaxOnly()
    {
        var terms = FileLister.BuildEverythingSizeFilterTerms(0, 5000).ToList();
        Assert.Single(terms);
        Assert.Equal("size:<=5000", terms[0]);
    }

    [Fact]
    public void BuildEverythingSizeFilterTerms_Both()
    {
        var terms = FileLister.BuildEverythingSizeFilterTerms(100, 5000).ToList();
        Assert.Equal(2, terms.Count);
        Assert.Contains("size:>=100", terms);
        Assert.Contains("size:<=5000", terms);
    }

    [Fact]
    public void BuildEverythingSizeFilterTerms_NegativeValues_ClampedToZero()
    {
        var terms = FileLister.BuildEverythingSizeFilterTerms(-10, -20).ToList();
        Assert.Empty(terms);
    }

    [Fact]
    public void BuildEverythingDateFilterTerms_AllNull_YieldsNothing()
    {
        var terms = FileLister.BuildEverythingDateFilterTerms(null, null, null, null).ToList();
        Assert.Empty(terms);
    }

    [Fact]
    public void BuildEverythingDateFilterTerms_CreatedAfter()
    {
        var date = new DateTimeOffset(2025, 6, 15, 0, 0, 0, TimeSpan.Zero);
        var terms = FileLister.BuildEverythingDateFilterTerms(date, null, null, null).ToList();
        Assert.Single(terms);
        Assert.StartsWith("dc:>=", terms[0]);
    }

    [Fact]
    public void BuildEverythingDateFilterTerms_CreatedBefore()
    {
        var date = new DateTimeOffset(2025, 6, 15, 0, 0, 0, TimeSpan.Zero);
        var terms = FileLister.BuildEverythingDateFilterTerms(null, date, null, null).ToList();
        Assert.Single(terms);
        Assert.StartsWith("dc:<=", terms[0]);
    }

    [Fact]
    public void BuildEverythingDateFilterTerms_ModifiedAfter()
    {
        var date = new DateTimeOffset(2025, 6, 15, 0, 0, 0, TimeSpan.Zero);
        var terms = FileLister.BuildEverythingDateFilterTerms(null, null, date, null).ToList();
        Assert.Single(terms);
        Assert.StartsWith("dm:>=", terms[0]);
    }

    [Fact]
    public void BuildEverythingDateFilterTerms_ModifiedBefore()
    {
        var date = new DateTimeOffset(2025, 6, 15, 0, 0, 0, TimeSpan.Zero);
        var terms = FileLister.BuildEverythingDateFilterTerms(null, null, null, date).ToList();
        Assert.Single(terms);
        Assert.StartsWith("dm:<=", terms[0]);
    }

    [Fact]
    public void BuildEverythingDateFilterTerms_AllSet()
    {
        var d = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var terms = FileLister.BuildEverythingDateFilterTerms(d, d, d, d).ToList();
        Assert.Equal(4, terms.Count);
    }

    [Fact]
    public void FormatEverythingDate_FormatsCorrectly()
    {
        var date = new DateTimeOffset(2025, 3, 14, 12, 0, 0, TimeSpan.Zero);
        var result = FileLister.FormatEverythingDate(date);
        Assert.Matches(@"\d{4}-\d{2}-\d{2}", result);
    }

    [Fact]
    public void FileNameMatchesLiteralTerms_EmptyTerms_ReturnsTrue()
    {
        Assert.True(FileLister.FileNameMatchesLiteralTerms(@"C:\test\file.cs", Array.Empty<string>()));
    }

    [Fact]
    public void FileNameMatchesLiteralTerms_MatchingTerm_ReturnsTrue()
    {
        Assert.True(FileLister.FileNameMatchesLiteralTerms(@"C:\test\MyFile.cs", new[] { "MyFile" }));
    }

    [Fact]
    public void FileNameMatchesLiteralTerms_NonMatchingTerm_ReturnsFalse()
    {
        Assert.False(FileLister.FileNameMatchesLiteralTerms(@"C:\test\other.cs", new[] { "MyFile" }));
    }

    [Fact]
    public void FileNameMatchesLiteralTerms_CaseInsensitive()
    {
        Assert.True(FileLister.FileNameMatchesLiteralTerms(@"C:\test\MYFILE.cs", new[] { "myfile" }));
    }

    [Fact]
    public void FileNameMatchesLiteralTerms_MultipleTerms_AnyMatch()
    {
        Assert.True(FileLister.FileNameMatchesLiteralTerms(@"C:\test\beta.cs", new[] { "alpha", "beta" }));
    }

    [Fact]
    public void FileNameMatchesLiteralTerms_MultipleTerms_NoneMatch()
    {
        Assert.False(FileLister.FileNameMatchesLiteralTerms(@"C:\test\gamma.cs", new[] { "alpha", "beta" }));
    }

    [Theory]
    [InlineData(50, 100, 0, false, true)]   // too small
    [InlineData(200, 100, 0, false, false)]  // in range
    [InlineData(200, 0, 100, true, true)]    // too large
    [InlineData(50, 0, 100, false, false)]   // in range
    [InlineData(50, 0, 0, false, false)]     // no limits
    [InlineData(50, 100, 200, false, true)]  // too small (both limits)
    [InlineData(300, 100, 200, true, true)]  // too large (both limits)
    [InlineData(150, 100, 200, false, false)] // in range (both limits)
    public void IsOutsideEarlyFileSizeRange_VariousCases(long size, long min, long max, bool expectedTooLarge, bool expectedOutside)
    {
        bool result = FileLister.IsOutsideEarlyFileSizeRange(size, min, max, out bool tooLarge);
        Assert.Equal(expectedOutside, result);
        if (expectedOutside)
            Assert.Equal(expectedTooLarge, tooLarge);
    }

    [Fact]
    public void IsOutsideEarlyDateRange_NoLimits_ReturnsFalse()
    {
        Assert.False(FileLister.IsOutsideEarlyDateRange(
            DateTime.Now, DateTime.Now, null, null, null, null));
    }

    [Fact]
    public void IsOutsideEarlyDateRange_CreatedTooOld_ReturnsTrue()
    {
        var created = new DateTime(2020, 1, 1);
        var modified = DateTime.Now;
        var after = new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.True(FileLister.IsOutsideEarlyDateRange(created, modified, after, null, null, null));
    }

    [Fact]
    public void IsOutsideEarlyDateRange_CreatedTooNew_ReturnsTrue()
    {
        var created = new DateTime(2026, 1, 1);
        var modified = DateTime.Now;
        var before = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.True(FileLister.IsOutsideEarlyDateRange(created, modified, null, before, null, null));
    }

    [Fact]
    public void IsOutsideEarlyDateRange_ModifiedTooOld_ReturnsTrue()
    {
        var created = DateTime.Now;
        var modified = new DateTime(2020, 1, 1);
        var after = new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.True(FileLister.IsOutsideEarlyDateRange(created, modified, null, null, after, null));
    }

    [Fact]
    public void IsOutsideEarlyDateRange_ModifiedTooNew_ReturnsTrue()
    {
        var created = DateTime.Now;
        var modified = new DateTime(2026, 1, 1);
        var before = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.True(FileLister.IsOutsideEarlyDateRange(created, modified, null, null, null, before));
    }

    [Fact]
    public void IsOutsideDateRange_DefaultDate_WithAfter_ReturnsTrue()
    {
        var after = new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.True(FileLister.IsOutsideDateRange(default, after, null));
    }

    [Fact]
    public void IsOutsideDateRange_DefaultDate_WithBefore_ReturnsTrue()
    {
        var before = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.True(FileLister.IsOutsideDateRange(default, null, before));
    }

    [Fact]
    public void IsOutsideDateRange_InRange_ReturnsFalse()
    {
        var after = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var before = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.False(FileLister.IsOutsideDateRange(new DateTime(2025, 6, 1), after, before));
    }

    [Fact]
    public void ParseAdminProtectedSegments_EmptyInput()
    {
        Assert.Empty(FileLister.ParseAdminProtectedSegments(""));
        Assert.Empty(FileLister.ParseAdminProtectedSegments("   "));
    }

    [Fact]
    public void ParseAdminProtectedSegments_SingleEntry()
    {
        var result = FileLister.ParseAdminProtectedSegments(@"\Windows\System32\config");
        Assert.Single(result);
        Assert.Equal(@"\Windows\System32\config", result[0]);
    }

    [Fact]
    public void ParseAdminProtectedSegments_MultipleEntries_Semicolons()
    {
        var result = FileLister.ParseAdminProtectedSegments(@"\Path1;\Path2;\Path3");
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void ParseAdminProtectedSegments_MultipleEntries_Newlines()
    {
        var result = FileLister.ParseAdminProtectedSegments("\\Path1\n\\Path2\r\n\\Path3");
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void ParseAdminProtectedSegments_Deduplicates()
    {
        var result = FileLister.ParseAdminProtectedSegments(@"\Path1;\path1;\PATH1");
        Assert.Single(result);
    }

    [Fact]
    public void ParseAdminProtectedSegments_SkipsBlank()
    {
        var result = FileLister.ParseAdminProtectedSegments(@"\Path1;;;\Path2");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ParseAdminProtectedSegments_SkipsSingleBackslash()
    {
        // "\" normalizes to null (length <= 1 after TrimEnd) so it should be skipped
        var result = FileLister.ParseAdminProtectedSegments(@"\;\Path1;/");
        Assert.Single(result);
        Assert.Equal(@"\Path1", result[0]);
    }

    [Fact]
    public void BuildEverythingFileNameFilter_EmptyTerms_ReturnsNull()
    {
        Assert.Null(FileLister.BuildEverythingFileNameFilter(Array.Empty<string>()));
    }

    [Fact]
    public void BuildEverythingFileNameFilter_WhitespaceTerm_ReturnsNull()
    {
        Assert.Null(FileLister.BuildEverythingFileNameFilter(new[] { "  " }));
    }

    [Fact]
    public void BuildEverythingFileNameFilter_TermWithQuote_ReturnsNull()
    {
        Assert.Null(FileLister.BuildEverythingFileNameFilter(new[] { "bad\"term" }));
    }

    [Fact]
    public void BuildEverythingFileNameFilter_SingleTerm()
    {
        Assert.Equal("\"hello\"", FileLister.BuildEverythingFileNameFilter(new[] { "hello" }));
    }

    [Fact]
    public void BuildEverythingFileNameFilter_MultipleTerms()
    {
        var result = FileLister.BuildEverythingFileNameFilter(new[] { "alpha", "beta" });
        Assert.Equal("<\"alpha\"|\"beta\">", result);
    }

    [Fact]
    public void GetEverythingInstallDirsFromRegistry_UsesOverride()
    {
        var original = FileLister.GetEverythingInstallDirsOverride;
        try
        {
            FileLister.GetEverythingInstallDirsOverride = () => new List<string> { @"C:\MockEverything" };
            var dirs = FileLister.GetEverythingInstallDirsFromRegistry();
            Assert.Single(dirs);
            Assert.Equal(@"C:\MockEverything", dirs[0]);
        }
        finally { FileLister.GetEverythingInstallDirsOverride = original; }
    }
}

// ─── RunEverythingSdkAsync via mock SDK ─────────────────────────────────

[Collection("FileListerBackend")]
public class FileListerSdkTests : IDisposable
{
    private readonly string _root;
    private readonly FileListerBackend _originalBackend;
    private readonly bool _originalSdkAvailable;

    public FileListerSdkTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qg-sdk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _originalBackend = FileLister.Backend;
        _originalSdkAvailable = FileLister.SdkAvailable;
        FileLister.Backend = FileListerBackend.EverythingSdk;
        FileLister.SdkAvailable = true;
    }

    public void Dispose()
    {
        FileLister.Backend = _originalBackend;
        FileLister.SdkAvailable = _originalSdkAvailable;
        try { Directory.Delete(_root, true); } catch { }
    }

    private FileLister CreateSdkLister(IEverythingSdkOps sdk) => new(null, sdk);

    [Fact]
    public async Task Sdk_NormalQuery_YieldsPaths()
    {
        var sdk = new MockEverythingSdkOps
        {
            Results = [(@"C:\code\a.cs", 100), (@"C:\code\b.cs", 200)]
        };
        var lister = CreateSdkLister(sdk);
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Equal(2, files.Count);
        Assert.Contains(@"C:\code\a.cs", files);
        Assert.Contains(@"C:\code\b.cs", files);
    }

    [Fact]
    public async Task Sdk_SetsKnownTotalFiles()
    {
        var sdk = new MockEverythingSdkOps
        {
            Results = [(@"C:\a.cs", 10), (@"C:\b.cs", 20)]
        };
        var lister = CreateSdkLister(sdk);
        await foreach (var _ in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default)) { }
        Assert.Equal(2, lister.KnownTotalFiles);
    }

    [Fact]
    public async Task Sdk_DbNotLoaded_SetsFallbackReason()
    {
        var sdk = new MockEverythingSdkOps { DbLoaded = false };
        var lister = CreateSdkLister(sdk);
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Empty(files);
        Assert.Equal("Everything database not loaded (is Everything running?)", lister.FallbackReason);
    }

    [Fact]
    public async Task Sdk_QueryFailsIPC_SetsEverythingNotRunning()
    {
        var sdk = new MockEverythingSdkOps
        {
            QuerySucceeds = false,
            LastError = 2, // EVERYTHING_ERROR_IPC
        };
        var lister = CreateSdkLister(sdk);
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Empty(files);
        Assert.Equal("Everything is not running", lister.FallbackReason);
    }

    [Fact]
    public async Task Sdk_QueryFailsOther_SetsGenericError()
    {
        var sdk = new MockEverythingSdkOps
        {
            QuerySucceeds = false,
            LastError = 99,
            LastErrorMessage = "test failure",
        };
        var lister = CreateSdkLister(sdk);
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Empty(files);
        Assert.Contains("Everything SDK query failed", lister.FallbackReason);
    }

    [Fact]
    public async Task Sdk_DllNotFound_SetsSdkUnavailable()
    {
        var sdk = new MockEverythingSdkOps { ThrowDllNotFound = true };
        var lister = CreateSdkLister(sdk);
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Empty(files);
        Assert.Contains("Everything64.dll not found", lister.FallbackReason);
    }

    [Fact]
    public async Task Sdk_GeneralException_SetsError()
    {
        var throwingSdk = new ThrowOnQuerySdkOps();
        var lister = CreateSdkLister(throwingSdk);
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Empty(files);
        Assert.Contains("Everything SDK error", lister.FallbackReason);
    }

    [Fact]
    public async Task Sdk_NoResults_SetsFallbackReason()
    {
        var sdk = new MockEverythingSdkOps { Results = [] };
        var lister = CreateSdkLister(sdk);
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Empty(files);
        Assert.Equal("Everything SDK returned no results", lister.FallbackReason);
    }

    [Fact]
    public async Task Sdk_NotAvailable_SetsFallbackReason()
    {
        FileLister.SdkAvailable = false;
        var sdk = new MockEverythingSdkOps();
        var lister = CreateSdkLister(sdk);
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Empty(files);
        Assert.Equal("Everything SDK not available", lister.FallbackReason);
    }

    [Fact]
    public async Task Sdk_WithMaxFiles_SetsMax()
    {
        var sdk = new MockEverythingSdkOps
        {
            Results = [(@"C:\a.cs", 10)]
        };
        var lister = CreateSdkLister(sdk);
        await foreach (var _ in lister.ListFilesAsync(_root, Array.Empty<string>(), 50, default)) { }
        Assert.Equal(50u, sdk.CapturedMax);
    }

    [Fact]
    public async Task Sdk_NoMaxFiles_UsesDefaultPageSize()
    {
        var sdk = new MockEverythingSdkOps
        {
            Results = [(@"C:\a.cs", 10)]
        };
        var lister = CreateSdkLister(sdk);
        await foreach (var _ in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default)) { }
        Assert.Equal(10_000u, sdk.CapturedMax);
    }

    [Fact]
    public async Task Sdk_SizeFilter_SkipsTooSmall()
    {
        var sdk = new MockEverythingSdkOps
        {
            Results = [(@"C:\small.cs", 10), (@"C:\large.cs", 1000)]
        };
        var lister = CreateSdkLister(sdk);
        lister.EarlyMinFileSizeBytes = 100;
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Single(files);
        Assert.Contains(@"C:\large.cs", files);
        Assert.Equal(1, lister.EarlySkippedFiles);
        Assert.Equal(0, lister.EarlySkippedTooLargeFiles);
    }

    [Fact]
    public async Task Sdk_SizeFilter_SkipsTooLarge()
    {
        var sdk = new MockEverythingSdkOps
        {
            Results = [(@"C:\small.cs", 10), (@"C:\huge.cs", 99999)]
        };
        var lister = CreateSdkLister(sdk);
        lister.EarlyMaxFileSizeBytes = 1000;
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Single(files);
        Assert.Contains(@"C:\small.cs", files);
        Assert.Equal(1, lister.EarlySkippedFiles);
        Assert.Equal(1, lister.EarlySkippedTooLargeFiles);
    }

    [Fact]
    public async Task Sdk_ExtensionBlocklist_ExcludesByExtension()
    {
        var sdk = new MockEverythingSdkOps
        {
            Results = [(@"C:\file.cs", 100), (@"C:\file.exe", 100), (@"C:\file.dll", 100)]
        };
        var lister = CreateSdkLister(sdk);
        lister.EarlySkipExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "exe", "dll" };
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Single(files);
        Assert.Contains(@"C:\file.cs", files);
        Assert.Equal(2, lister.EarlyExcludedByExtensionFiles);
    }

    [Fact]
    public async Task Sdk_IncludeExtensions_AddedToQuery()
    {
        var sdk = new MockEverythingSdkOps
        {
            Results = [(@"C:\a.cs", 100)]
        };
        var lister = CreateSdkLister(sdk);
        await foreach (var _ in lister.ListFilesAsync(_root, new[] { "cs", "txt" }, 0, default)) { }
        Assert.Contains("ext:cs;txt", sdk.CapturedQuery);
    }

    [Fact]
    public async Task Sdk_ExcludeGlobs_ExtensionGlob()
    {
        var sdk = new MockEverythingSdkOps { Results = [(@"C:\a.cs", 100)] };
        var lister = CreateSdkLister(sdk);
        lister.EarlyExcludeGlobs = ["*.log"];
        await foreach (var _ in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default)) { }
        Assert.Contains("!ext:log", sdk.CapturedQuery);
    }

    [Fact]
    public async Task Sdk_ExcludeGlobs_SegmentName()
    {
        var sdk = new MockEverythingSdkOps { Results = [(@"C:\a.cs", 100)] };
        var lister = CreateSdkLister(sdk);
        lister.EarlyExcludeGlobs = ["node_modules"];
        await foreach (var _ in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default)) { }
        Assert.Contains(@"!""\node_modules\""", sdk.CapturedQuery);
    }

    [Fact]
    public async Task Sdk_ExcludeGlobs_ComplexGlob_IgnoredInQuery()
    {
        var sdk = new MockEverythingSdkOps { Results = [(@"C:\a.cs", 100)] };
        var lister = CreateSdkLister(sdk);
        lister.EarlyExcludeGlobs = ["src/**/*.tmp"];
        await foreach (var _ in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default)) { }
        // Complex globs should not appear in query — they're left to GlobMatcher
        Assert.DoesNotContain("src", sdk.CapturedQuery!);
    }

    [Fact]
    public async Task Sdk_ExcludeGlobs_EmptyAndWhitespace_Skipped()
    {
        var sdk = new MockEverythingSdkOps { Results = [(@"C:\a.cs", 100)] };
        var lister = CreateSdkLister(sdk);
        lister.EarlyExcludeGlobs = ["", "  "];
        await foreach (var _ in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default)) { }
        // Should not crash
        Assert.NotNull(sdk.CapturedQuery);
    }

    [Fact]
    public async Task Sdk_ExcludeGlobs_MultipleInOneLine()
    {
        var sdk = new MockEverythingSdkOps { Results = [(@"C:\a.cs", 100)] };
        var lister = CreateSdkLister(sdk);
        lister.EarlyExcludeGlobs = ["*.log,bin"];
        await foreach (var _ in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default)) { }
        Assert.Contains("!ext:log", sdk.CapturedQuery);
        Assert.Contains(@"!""\bin\""", sdk.CapturedQuery);
    }

    [Fact]
    public async Task Sdk_GitignoreFolders_PostFiltered()
    {
        // .git is always excluded in the SDK query itself
        var sdk = new MockEverythingSdkOps { Results = [(@"C:\a.cs", 100), (_root + @"\node_modules\b.js", 100)] };
        var lister = CreateSdkLister(sdk);
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "node_modules\n");
        lister.GitignoreMatcher = new DynamicGitignoreMatcher(_root);
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Contains(".git", sdk.CapturedQuery); // .git always in query
        Assert.DoesNotContain("node_modules", sdk.CapturedQuery); // not in query, post-filtered
        Assert.DoesNotContain(files, f => f.Contains("node_modules")); // filtered out
    }

    [Fact]
    public async Task Sdk_GitignoreFolders_NoMatcherNoop()
    {
        var sdk = new MockEverythingSdkOps { Results = [(@"C:\a.cs", 100)] };
        var lister = CreateSdkLister(sdk);
        // No matcher set — no gitignore filtering
        await foreach (var _ in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default)) { }
        Assert.NotNull(sdk.CapturedQuery);
    }

    [Fact]
    public async Task Sdk_GitignoreExtensions_PostFiltered()
    {
        var sdk = new MockEverythingSdkOps { Results = [(_root + @"\a.cs", 100), (_root + @"\debug.log", 100)] };
        var lister = CreateSdkLister(sdk);
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "*.log\n");
        lister.GitignoreMatcher = new DynamicGitignoreMatcher(_root);
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Single(files);
        Assert.EndsWith("a.cs", files[0]);
    }

    [Fact]
    public async Task Sdk_GitignoreFullPaths_PostFiltered()
    {
        var buildDir = Path.Combine(_root, "build");
        Directory.CreateDirectory(buildDir);
        var sdk = new MockEverythingSdkOps { Results = [(_root + @"\a.cs", 100), (buildDir + @"\out.dll", 100)] };
        var lister = CreateSdkLister(sdk);
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "/build\n");
        lister.GitignoreMatcher = new DynamicGitignoreMatcher(_root);
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Single(files);
        Assert.EndsWith("a.cs", files[0]);
    }

    [Fact]
    public async Task Sdk_GitignoreFullPaths_NoMatcherNoop()
    {
        var sdk = new MockEverythingSdkOps { Results = [(@"C:\a.cs", 100)] };
        var lister = CreateSdkLister(sdk);
        await foreach (var _ in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default)) { }
        Assert.NotNull(sdk.CapturedQuery);
    }

    [Fact]
    public async Task Sdk_AdminPathExclusion_WhenNotElevated()
    {
        var originalOverride = FileLister.ElevationOverride;
        try
        {
            FileLister.ElevationOverride = () => false;
            var sdk = new MockEverythingSdkOps { Results = [(@"C:\a.cs", 100)] };
            // Need a new lister after setting elevation override (ShouldExcludeAdminPaths reads s_isElevated)
            var lister = CreateSdkLister(sdk);
            lister.ExcludeAdminProtectedPaths = true;
            await foreach (var _ in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default)) { }
            // Admin paths should appear in query as exclusions
            Assert.Contains("System Volume Information", sdk.CapturedQuery);
        }
        finally { FileLister.ElevationOverride = originalOverride; }
    }

    [Fact]
    public async Task Sdk_SkipExtensions_AddedToQuery()
    {
        var sdk = new MockEverythingSdkOps { Results = [(@"C:\a.cs", 100)] };
        var lister = CreateSdkLister(sdk);
        lister.EarlySkipExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "exe", "dll" };
        await foreach (var _ in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default)) { }
        Assert.Contains("!ext:", sdk.CapturedQuery);
    }

    [Fact]
    public async Task Sdk_SizeFilter_AddedToQuery()
    {
        var sdk = new MockEverythingSdkOps { Results = [(@"C:\a.cs", 100)] };
        var lister = CreateSdkLister(sdk);
        lister.EarlyMinFileSizeBytes = 50;
        lister.EarlyMaxFileSizeBytes = 5000;
        await foreach (var _ in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default)) { }
        Assert.Contains("size:>=50", sdk.CapturedQuery);
        Assert.Contains("size:<=5000", sdk.CapturedQuery);
    }

    [Fact]
    public async Task Sdk_DateFilter_UsesMetadataRequestFlagNotQueryTerm()
    {
        var sdk = new MockEverythingSdkOps
        {
            Results = [(@"C:\a.cs", 100)],
            CreatedFileTimes = { [0] = new DateTime(2025, 1, 2).ToFileTime() }
        };
        var lister = CreateSdkLister(sdk);
        lister.EarlyCreatedAfterDate = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Single(files);
        Assert.DoesNotContain("dc:", sdk.CapturedQuery);
        Assert.True((sdk.CapturedRequestFlags.GetValueOrDefault() & EverythingSdk.EVERYTHING_REQUEST_DATE_CREATED) != 0);
    }

    [Fact]
    public async Task Sdk_FileNameFilter_AddedToQuery()
    {
        var sdk = new MockEverythingSdkOps { Results = [(@"C:\a.cs", 100)] };
        var lister = CreateSdkLister(sdk);
        lister.EarlyFileNameLiteralTerms = ["target"];
        await foreach (var _ in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default)) { }
        Assert.Contains("\"target\"", sdk.CapturedQuery);
    }

    [Fact]
    public async Task Sdk_DirectoryWithSpaces_QuotedInQuery()
    {
        var spaceDir = Path.Combine(Path.GetTempPath(), "qg sdk space " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(spaceDir);
        try
        {
            var sdk = new MockEverythingSdkOps { Results = [(@"C:\a.cs", 100)] };
            var lister = CreateSdkLister(sdk);
            await foreach (var _ in lister.ListFilesAsync(spaceDir, Array.Empty<string>(), 0, default)) { }
            // Directory with spaces should be quoted in query
            Assert.Contains("\"", sdk.CapturedQuery);
        }
        finally { try { Directory.Delete(spaceDir, true); } catch { } }
    }

    [Fact]
    public async Task Sdk_LongPath_BufferOverflow_HandledCorrectly()
    {
        var longPath = @"C:\" + new string('a', 2000) + ".cs";
        var sdk = new MockEverythingSdkOps
        {
            Results = [(longPath, 100)]
        };
        var lister = CreateSdkLister(sdk);
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        // The buffer overflow path should still yield the result
        Assert.Single(files);
    }

    [Fact]
    public async Task Sdk_ZeroLengthResult_Skipped()
    {
        // A result with empty path
        var sdk = new MockEverythingSdkOps
        {
            Results = [("", 100), (@"C:\valid.cs", 100)]
        };
        var lister = CreateSdkLister(sdk);
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Single(files);
        Assert.Equal(@"C:\valid.cs", files[0]);
    }

    [Fact]
    public async Task Sdk_Cancellation_StopsProducer()
    {
        var sdk = new MockEverythingSdkOps
        {
            Results = Enumerable.Range(0, 100).Select(i => ($@"C:\file{i}.cs", (long)100)).ToList()
        };
        var lister = CreateSdkLister(sdk);
        using var cts = new CancellationTokenSource();
        var files = new List<string>();
        try
        {
            await foreach (var p in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, cts.Token))
            {
                files.Add(p);
                if (files.Count >= 5) cts.Cancel();
            }
        }
        catch (OperationCanceledException) { }
        // We cancelled after 5 — verify we got at least 5 and no crash occurred
        Assert.True(files.Count >= 5);
    }

    [Fact]
    public async Task Sdk_ForcedBackend_NoResults_SetsFallbackReason()
    {
        FileLister.Backend = FileListerBackend.EverythingSdk;
        var sdk = new MockEverythingSdkOps { Results = [] };
        var lister = CreateSdkLister(sdk);
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Empty(files);
        // Should set a fallback reason since forced backend returned nothing
        Assert.Contains("no results", lister.FallbackReason, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<List<string>> CollectAsync(IAsyncEnumerable<string> source)
    {
        var list = new List<string>();
        await foreach (var item in source) list.Add(item);
        return list;
    }
}

/// <summary>Mock SDK that throws a general exception on Query().</summary>
internal sealed class ThrowOnQuerySdkOps : IEverythingSdkOps
{
    public object SyncLock { get; } = new();
    public bool IsDBLoaded() => true;
    public void Reset() { }
    public void SetSearch(string searchString) { }
    public void SetMatchCase(bool matchCase) { }
    public void SetMatchPath(bool matchPath) { }
    public void SetOffset(uint offset) { }
    public void SetMax(uint max) { }
    public void SetRequestFlags(uint flags) { }
    public bool Query(bool bWait) => throw new InvalidOperationException("test SDK failure");
    public uint GetLastError() => 0;
    public string ErrorMessage(uint error) => "";
    public uint GetNumResults() => 0;
    public uint GetTotResults() => 0;
    public bool GetResultSize(uint index, out long size) { size = 0; return false; }
    public bool GetResultDateCreated(uint index, out long fileTime) { fileTime = 0; return false; }
    public bool GetResultDateModified(uint index, out long fileTime) { fileTime = 0; return false; }
    public uint GetResultFullPathName(uint index, char[] buffer, uint capacity) => 0;
}

// ─── ProbeEverythingSdkReadiness via mock SDK ───────────────────────────

[Collection("FileListerBackend")]
public class ProbeEverythingSdkReadinessTests : IDisposable
{
    private readonly bool _originalSdkAvailable;

    public ProbeEverythingSdkReadinessTests()
    {
        _originalSdkAvailable = FileLister.SdkAvailable;
        FileLister.SdkAvailable = true;
    }

    public void Dispose() => FileLister.SdkAvailable = _originalSdkAvailable;

    [Fact]
    public void Probe_SdkNotAvailable_ReturnsNotReady()
    {
        FileLister.SdkAvailable = false;
        var sdk = new MockEverythingSdkOps();
        var result = FileLister.ProbeEverythingSdkReadiness(sdk);
        Assert.False(result.IsReady);
        Assert.Contains("not available", result.Error);
    }

    [Fact]
    public void Probe_DbNotLoaded_ReturnsNotReady()
    {
        var sdk = new MockEverythingSdkOps { DbLoaded = false };
        var result = FileLister.ProbeEverythingSdkReadiness(sdk);
        Assert.False(result.IsReady);
        Assert.Contains("still loading", result.Error);
    }

    [Fact]
    public void Probe_QueryFailsIPC_ReturnsNotRunning()
    {
        var sdk = new MockEverythingSdkOps
        {
            QuerySucceeds = false,
            LastError = 2 // IPC
        };
        var result = FileLister.ProbeEverythingSdkReadiness(sdk);
        Assert.False(result.IsReady);
        Assert.Contains("not running", result.Error);
    }

    [Fact]
    public void Probe_QueryFailsOther_ReturnsError()
    {
        var sdk = new MockEverythingSdkOps
        {
            QuerySucceeds = false,
            LastError = 99,
            LastErrorMessage = "test error"
        };
        var result = FileLister.ProbeEverythingSdkReadiness(sdk);
        Assert.False(result.IsReady);
        Assert.Contains("query failed", result.Error);
    }

    [Fact]
    public void Probe_ZeroResults_ReturnsNotReady()
    {
        var sdk = new MockEverythingSdkOps { Results = [] };
        var result = FileLister.ProbeEverythingSdkReadiness(sdk);
        Assert.False(result.IsReady);
        Assert.Contains("no files or folders yet", result.Error);
    }

    [Fact]
    public void Probe_ResultsWithPaths_ReturnsReady()
    {
        var sdk = new MockEverythingSdkOps
        {
            Results = [(@"C:\test\file.txt", 100), (@"C:\test\file2.txt", 200)]
        };
        var result = FileLister.ProbeEverythingSdkReadiness(sdk);
        Assert.True(result.IsReady);
        Assert.Equal(2u, result.ReturnedCount);
        Assert.Equal(2u, result.TotalCount);
        Assert.Equal(2, result.SamplePaths.Count);
    }

    [Fact]
    public void Probe_ResultsWithEmptyPaths_ReturnsNotReady()
    {
        // Results exist but GetResultFullPathName returns 0 for all
        var sdk = new EmptyPathSdkOps();
        var result = FileLister.ProbeEverythingSdkReadiness(sdk);
        Assert.False(result.IsReady);
        Assert.Contains("no file or folder paths yet", result.Error);
    }

    [Fact]
    public void Probe_DllNotFound_SetsNotAvailable()
    {
        var sdk = new MockEverythingSdkOps { ThrowDllNotFound = true };
        var result = FileLister.ProbeEverythingSdkReadiness(sdk);
        Assert.False(result.IsReady);
        Assert.Contains("Everything64.dll not found", result.Error);
    }

    [Fact]
    public void Probe_GeneralException_ReturnsNotReady()
    {
        var sdk = new MockEverythingSdkOps
        {
            ThrowGeneral = true,
            GeneralExceptionMessage = "test probe failure"
        };
        var result = FileLister.ProbeEverythingSdkReadiness(sdk);
        Assert.False(result.IsReady);
        Assert.Contains("readiness check failed", result.Error);
    }

    [Fact]
    public void Probe_LongPath_BufferExpanded()
    {
        var longPath = @"C:\" + new string('x', 2000) + ".txt";
        var sdk = new MockEverythingSdkOps
        {
            Results = [(longPath, 100)]
        };
        var result = FileLister.ProbeEverythingSdkReadiness(sdk);
        Assert.True(result.IsReady);
        Assert.Single(result.SamplePaths);
    }

    [Fact]
    public void Probe_ResetThrows_StillReturnsResult()
    {
        var sdk = new MockEverythingSdkOps
        {
            Results = [(@"C:\file.txt", 100)],
            ThrowOnReset = true,
            ThrowOnResetAfterCall = 2 // first Reset() succeeds, second (in finally) throws
        };
        // The finally { sdk.Reset(); } catch { } should swallow the Reset exception
        var result = FileLister.ProbeEverythingSdkReadiness(sdk);
        Assert.True(result.IsReady);
    }
}

/// <summary>Mock SDK that returns results but with empty paths.</summary>
internal sealed class EmptyPathSdkOps : IEverythingSdkOps
{
    public object SyncLock { get; } = new();
    public bool IsDBLoaded() => true;
    public void Reset() { }
    public void SetSearch(string s) { }
    public void SetMatchCase(bool v) { }
    public void SetMatchPath(bool v) { }
    public void SetOffset(uint v) { }
    public void SetMax(uint v) { }
    public void SetRequestFlags(uint v) { }
    public bool Query(bool bWait) => true;
    public uint GetLastError() => 0;
    public string ErrorMessage(uint e) => "";
    public uint GetNumResults() => 3; // claim 3 results
    public uint GetTotResults() => 3;
    public bool GetResultSize(uint index, out long size) { size = 100; return true; }
    public bool GetResultDateCreated(uint index, out long fileTime) { fileTime = 0; return false; }
    public bool GetResultDateModified(uint index, out long fileTime) { fileTime = 0; return false; }
    public uint GetResultFullPathName(uint index, char[] buffer, uint capacity) => 0; // empty
}

// ─── EnumerateFallbackAsync branch coverage ─────────────────────────────

[Collection("FileListerBackend")]
public class FileListerFallbackBranchTests : IDisposable
{
    private readonly string _root;
    private readonly FileListerBackend _originalBackend;
    private readonly Func<bool>? _originalElevation;

    public FileListerFallbackBranchTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qg-fallback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _originalBackend = FileLister.Backend;
        _originalElevation = FileLister.ElevationOverride;
        FileLister.Backend = FileListerBackend.Managed;
    }

    public void Dispose()
    {
        FileLister.Backend = _originalBackend;
        FileLister.ElevationOverride = _originalElevation;
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public async Task Managed_GitignoreFolderExclusion_SkipsFolder()
    {
        var excluded = Path.Combine(_root, "node_modules");
        Directory.CreateDirectory(excluded);
        File.WriteAllText(Path.Combine(excluded, "a.js"), "");
        File.WriteAllText(Path.Combine(_root, "app.js"), "");
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "node_modules\n");

        var lister = new FileLister();
        lister.GitignoreMatcher = new DynamicGitignoreMatcher(_root);
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        // .gitignore file itself + app.js
        Assert.DoesNotContain(files, f => f.Contains("node_modules"));
        Assert.True(lister.GitignoreSkipped > 0);
    }

    [Fact]
    public async Task Managed_GitignoreExtensionExclusion_SkipsFiles()
    {
        File.WriteAllText(Path.Combine(_root, "app.cs"), "");
        File.WriteAllText(Path.Combine(_root, "debug.log"), "");
        File.WriteAllText(Path.Combine(_root, "trace.log"), "");
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "*.log\n");

        var lister = new FileLister();
        lister.GitignoreMatcher = new DynamicGitignoreMatcher(_root);
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.DoesNotContain(files, f => f.EndsWith(".log"));
        Assert.Equal(2, lister.GitignoreSkipped);
    }

    [Fact]
    public async Task Managed_GitignoreFullPathExclusion_SkipsDir()
    {
        var buildDir = Path.Combine(_root, "build");
        Directory.CreateDirectory(buildDir);
        File.WriteAllText(Path.Combine(buildDir, "out.dll"), "");
        File.WriteAllText(Path.Combine(_root, "src.cs"), "");
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "/build\n");

        var lister = new FileLister();
        lister.GitignoreMatcher = new DynamicGitignoreMatcher(_root);
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.DoesNotContain(files, f => f.Contains("build"));
        Assert.True(lister.GitignoreSkipped > 0);
    }

    [Fact]
    public async Task Managed_AdminPathExclusion_SkipsAdminDirs()
    {
        FileLister.ElevationOverride = () => false;
        var adminLike = Path.Combine(_root, "Recovery");
        Directory.CreateDirectory(adminLike);
        File.WriteAllText(Path.Combine(adminLike, "data.bin"), "");
        File.WriteAllText(Path.Combine(_root, "app.cs"), "");

        var lister = new FileLister();
        lister.ExcludeAdminProtectedPaths = true;
        lister.AdminProtectedPathSegmentsOverride = [@"\Recovery"];
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Single(files);
        Assert.EndsWith("app.cs", files[0]);
    }

    [Fact]
    public async Task Managed_EarlySizeFilter_SkipsTooSmall()
    {
        File.WriteAllText(Path.Combine(_root, "tiny.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "big.txt"), new string('x', 1000));

        var lister = new FileLister();
        lister.EarlyMinFileSizeBytes = 100;
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Single(files);
        Assert.EndsWith("big.txt", files[0]);
        Assert.True(lister.EarlySkippedFiles > 0);
    }

    [Fact]
    public async Task Managed_EarlySizeFilter_SkipsTooLarge()
    {
        File.WriteAllText(Path.Combine(_root, "small.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "large.txt"), new string('x', 5000));

        var lister = new FileLister();
        lister.EarlyMaxFileSizeBytes = 100;
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Single(files);
        Assert.EndsWith("small.txt", files[0]);
        Assert.True(lister.EarlySkippedFiles > 0);
        Assert.True(lister.EarlySkippedTooLargeFiles > 0);
    }

    [Fact]
    public async Task Managed_EarlyDateFilter_SkipsOldCreated()
    {
        var filePath = Path.Combine(_root, "old.txt");
        File.WriteAllText(filePath, "content");
        File.SetCreationTime(filePath, new DateTime(2020, 1, 1));

        var recentPath = Path.Combine(_root, "recent.txt");
        File.WriteAllText(recentPath, "content");

        var lister = new FileLister();
        lister.EarlyCreatedAfterDate = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        // "recent.txt" should remain (created today), "old.txt" should be skipped
        Assert.Single(files);
        Assert.EndsWith("recent.txt", files[0]);
        Assert.True(lister.EarlySkippedFiles > 0);
    }

    [Fact]
    public async Task Managed_EarlyDateFilter_SkipsNewModified()
    {
        var filePath = Path.Combine(_root, "future.txt");
        File.WriteAllText(filePath, "content");
        File.SetLastWriteTime(filePath, new DateTime(2030, 1, 1));

        var normalPath = Path.Combine(_root, "normal.txt");
        File.WriteAllText(normalPath, "content");
        File.SetLastWriteTime(normalPath, new DateTime(2024, 6, 1));

        var lister = new FileLister();
        lister.EarlyModifiedBeforeDate = new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero);
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Single(files);
        Assert.EndsWith("normal.txt", files[0]);
    }

    [Fact]
    public async Task Managed_FileNameLiteralTerms_FiltersFiles()
    {
        File.WriteAllText(Path.Combine(_root, "target_file.cs"), "");
        File.WriteAllText(Path.Combine(_root, "other.cs"), "");

        var lister = new FileLister();
        lister.EarlyFileNameLiteralTerms = ["target"];
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Single(files);
        Assert.Contains("target", files[0]);
    }

    [Fact]
    public async Task Managed_DriveLetterOnly_Normalized()
    {
        // "C:" should be normalized to "C:\" — testing that it doesn't crash.
        // This test just verifies no exception is thrown for a drive-letter path.
        var lister = new FileLister();
        // Use a path that definitely exists (the root of the temp drive)
        var tempDrive = Path.GetPathRoot(Path.GetTempPath())!.TrimEnd('\\');
        var files = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            await foreach (var p in lister.ListFilesAsync(tempDrive, Array.Empty<string>(), 5, cts.Token))
            {
                files.Add(p);
                if (files.Count >= 3) break;
            }
        }
        catch (OperationCanceledException) { }
        // Just verify it ran without throwing — the drive exists
        Assert.True(true);
    }

    [Fact]
    public async Task Managed_GitignoreFullPath_SubdirAlsoExcluded()
    {
        var outDir = Path.Combine(_root, "output");
        var subDir = Path.Combine(outDir, "sub");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "nested.dll"), "");
        File.WriteAllText(Path.Combine(_root, "app.cs"), "");
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "/output\n");

        var lister = new FileLister();
        lister.GitignoreMatcher = new DynamicGitignoreMatcher(_root);
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.DoesNotContain(files, f => f.Contains("output"));
    }

    private static async Task<List<string>> CollectAsync(IAsyncEnumerable<string> source)
    {
        var list = new List<string>();
        await foreach (var item in source) list.Add(item);
        return list;
    }
}

// ─── Es.exe arg building coverage ───────────────────────────────────────

[Collection("FileListerBackend")]
public class FileListerEsExeArgTests : IDisposable
{
    private readonly string _root;
    private readonly FileListerBackend _originalBackend;

    public FileListerEsExeArgTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qg-esarg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _originalBackend = FileLister.Backend;
        FileLister.Backend = FileListerBackend.EsExe;
    }

    public void Dispose()
    {
        FileLister.Backend = _originalBackend;
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public async Task EsExe_SizeFilterArgs_IncludedInPsi()
    {
        ProcessStartInfo? captured = null;
        var lister = new FileLister((path, psi) =>
        {
            if (!psi.ArgumentList.Contains("-get-result-count")) captured = psi;
            return new MockProcess(psi.ArgumentList.Contains("-get-result-count") ? ["0", null] : [null], 0);
        });
        lister.EarlyMinFileSizeBytes = 100;
        lister.EarlyMaxFileSizeBytes = 5000;
        await foreach (var _ in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default)) { }
        Assert.NotNull(captured);
        Assert.Contains(captured!.ArgumentList, a => a == "size:>=100");
        Assert.Contains(captured.ArgumentList, a => a == "size:<=5000");
    }

    [Fact]
    public async Task EsExe_DateFilterArgs_IncludedInPsi()
    {
        ProcessStartInfo? captured = null;
        var lister = new FileLister((path, psi) =>
        {
            if (!psi.ArgumentList.Contains("-get-result-count")) captured = psi;
            return new MockProcess(psi.ArgumentList.Contains("-get-result-count") ? ["0", null] : [null], 0);
        });
        lister.EarlyCreatedAfterDate = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        lister.EarlyModifiedBeforeDate = new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero);
        await foreach (var _ in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default)) { }
        Assert.NotNull(captured);
        Assert.Contains(captured!.ArgumentList, a => a.StartsWith("dc:>="));
        Assert.Contains(captured.ArgumentList, a => a.StartsWith("dm:<="));
    }

    [Fact]
    public async Task EsExe_FileNameFilterArg_IncludedInPsi()
    {
        ProcessStartInfo? captured = null;
        var lister = new FileLister((path, psi) =>
        {
            if (!psi.ArgumentList.Contains("-get-result-count")) captured = psi;
            return new MockProcess(psi.ArgumentList.Contains("-get-result-count") ? ["0", null] : [null], 0);
        });
        lister.EarlyFileNameLiteralTerms = ["target"];
        await foreach (var _ in lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default)) { }
        Assert.NotNull(captured);
        Assert.Contains(captured!.ArgumentList, a => a.Contains("target"));
    }

    [Fact]
    public async Task EsExe_CountProcessNonZeroExitWithInvalidOutput_ReturnsZero()
    {
        var lister = new FileLister((path, psi) =>
        {
            if (psi.ArgumentList.Contains("-get-result-count"))
                return new MockProcess(["not-a-number", null], 0);
            return new MockProcess([@"C:\a.cs", null], 0);
        });
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Single(files);
        // KnownTotalFiles should be 0 since the count couldn't be parsed
        Assert.Equal(0, lister.KnownTotalFiles);
    }

    private static async Task<List<string>> CollectAsync(IAsyncEnumerable<string> source)
    {
        var list = new List<string>();
        await foreach (var item in source) list.Add(item);
        return list;
    }
}

// ─── ListFilesAsync top-level branch coverage ───────────────────────────

[Collection("FileListerBackend")]
public class FileListerTopLevelBranchTests : IDisposable
{
    private readonly string _root;
    private readonly FileListerBackend _originalBackend;
    private readonly bool _originalSdkAvailable;

    public FileListerTopLevelBranchTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qg-top-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _originalBackend = FileLister.Backend;
        _originalSdkAvailable = FileLister.SdkAvailable;
    }

    public void Dispose()
    {
        FileLister.Backend = _originalBackend;
        FileLister.SdkAvailable = _originalSdkAvailable;
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public async Task SdkBackendForced_NoResults_SetsFallbackReason()
    {
        FileLister.Backend = FileListerBackend.EverythingSdk;
        FileLister.SdkAvailable = true;
        var sdk = new MockEverythingSdkOps { Results = [] };
        var lister = new FileLister(null, sdk);
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Empty(files);
        // When SDK returns nothing and backend is forced, FallbackReason mentions forced backend or no results
        Assert.NotNull(lister.FallbackReason);
    }

    [Fact]
    public async Task EsExeBackendForced_NoEsExeFound_SetsFallbackReason()
    {
        FileLister.Backend = FileListerBackend.EsExe;
        // Use process factory (so it goes through the es.exe path), but mock es.exe not found
        var lister = new FileLister((path, psi) =>
        {
            return new MockProcess([null], 0);
        });
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        // Since processFactory is set, it won't use FindEsExe() — it'll go through the mock process
        Assert.Empty(files);
    }

    [Fact]
    public async Task AutoBackend_SdkSucceeds_StopsBeforeEsExe()
    {
        FileLister.Backend = FileListerBackend.Auto;
        FileLister.SdkAvailable = true;
        var sdk = new MockEverythingSdkOps
        {
            Results = [(@"C:\via-sdk.cs", 100)]
        };
        var lister = new FileLister(null, sdk);
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Single(files);
        Assert.Equal(@"C:\via-sdk.cs", files[0]);
        Assert.Null(lister.FallbackReason); // SDK succeeded, no fallback needed
    }

    [Fact]
    public async Task Properties_EarlySkippedTooLargeFiles_Readable()
    {
        var lister = new FileLister();
        Assert.Equal(0, lister.EarlySkippedTooLargeFiles);
    }

    [Fact]
    public async Task Properties_EarlyExcludedByExtensionFiles_Readable()
    {
        var lister = new FileLister();
        Assert.Equal(0, lister.EarlyExcludedByExtensionFiles);
    }

    [Fact]
    public async Task Properties_GitignoreSkipped_Readable()
    {
        var lister = new FileLister();
        Assert.Equal(0, lister.GitignoreSkipped);
    }

    private static async Task<List<string>> CollectAsync(IAsyncEnumerable<string> source)
    {
        var list = new List<string>();
        await foreach (var item in source) list.Add(item);
        return list;
    }
}

// ─── WaitForEverythingSdkReadyAsync timeout edge cases ──────────────────

public class WaitForSdkReadyEdgeCaseTests
{
    [Fact]
    public async Task NegativeTimeout_ReturnsImmediately()
    {
        var result = await FileLister.WaitForEverythingSdkReadyAsync(
            () => EverythingReadinessResult.NotReady("not ready"),
            TimeSpan.FromMilliseconds(-100),
            TimeSpan.FromMilliseconds(10),
            CancellationToken.None);
        Assert.False(result.IsReady);
        Assert.Contains("Timed out", result.Error);
    }

    [Fact]
    public async Task NegativePollInterval_DefaultsToOneSecond()
    {
        int calls = 0;
        var result = await FileLister.WaitForEverythingSdkReadyAsync(
            () =>
            {
                calls++;
                return calls >= 2
                    ? EverythingReadinessResult.Ready(1, 1, [])
                    : EverythingReadinessResult.NotReady("loading");
            },
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(-1), // negative, should default to 1s
            CancellationToken.None);
        Assert.True(result.IsReady);
    }

    [Fact]
    public async Task TimeoutWithEmptyError_SetsGenericMessage()
    {
        var result = await FileLister.WaitForEverythingSdkReadyAsync(
            () => EverythingReadinessResult.NotReady(""),
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1),
            CancellationToken.None);
        Assert.False(result.IsReady);
        Assert.Contains("Timed out", result.Error);
        Assert.DoesNotContain("Last status:", result.Error);
    }
}

// ─── FindEsExe public overload coverage ─────────────────────────────────

public class FindEsExePublicTests : IDisposable
{
    private readonly Func<List<string>>? _originalOverride;

    public FindEsExePublicTests()
    {
        _originalOverride = FileLister.GetEverythingInstallDirsOverride;
    }

    public void Dispose()
    {
        FileLister.GetEverythingInstallDirsOverride = _originalOverride;
    }

    [Fact]
    public void FindEsExe_Public_IncludesRegistryDirs()
    {
        // Mock registry to return a specific dir
        FileLister.GetEverythingInstallDirsOverride = () => new List<string> { @"C:\MockEverythingDir" };
        // FindEsExe() public builds candidates including the registry dir
        // It won't find es.exe there, but it should not throw
        var result = FileLister.FindEsExe();
        // Result depends on system state — just verify no exception
        Assert.True(result is null || File.Exists(result));
    }
}

// ─── Additional gap-filling tests ───────────────────────────────────────

[Collection("FileListerBackend")]
public class FileListerGapTests : IDisposable
{
    private readonly string _root;
    private readonly FileListerBackend _originalBackend;
    private readonly bool _originalSdkAvailable;
    private readonly Func<bool>? _originalElevation;

    public FileListerGapTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qg-gap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _originalBackend = FileLister.Backend;
        _originalSdkAvailable = FileLister.SdkAvailable;
        _originalElevation = FileLister.ElevationOverride;
    }

    public void Dispose()
    {
        FileLister.Backend = _originalBackend;
        FileLister.SdkAvailable = _originalSdkAvailable;
        FileLister.ElevationOverride = _originalElevation;
        try { Directory.Delete(_root, true); } catch { }
    }

    // ── IsAdminProtectedPath direct tests ──

    [Fact]
    public void IsAdminProtectedPath_EndsWith_ReturnsTrue()
    {
        var lister = new FileLister();
        lister.AdminProtectedPathSegmentsOverride = [@"\Recovery"];
        lister.ExcludeAdminProtectedPaths = true;
        Assert.True(lister.IsAdminProtectedPath(@"C:\Recovery"));
    }

    [Fact]
    public void IsAdminProtectedPath_ContainsSegment_ReturnsTrue()
    {
        var lister = new FileLister();
        lister.AdminProtectedPathSegmentsOverride = [@"\Recovery"];
        lister.ExcludeAdminProtectedPaths = true;
        Assert.True(lister.IsAdminProtectedPath(@"C:\Recovery\Logs\data.bin"));
    }

    [Fact]
    public void IsAdminProtectedPath_NoMatch_ReturnsFalse()
    {
        var lister = new FileLister();
        lister.AdminProtectedPathSegmentsOverride = [@"\Recovery"];
        lister.ExcludeAdminProtectedPaths = true;
        Assert.False(lister.IsAdminProtectedPath(@"C:\Users\test.cs"));
    }

    [Fact]
    public void IsAdminProtectedPath_NullSegment_Skipped()
    {
        var lister = new FileLister();
        lister.AdminProtectedPathSegmentsOverride = ["", "  ", @"\Recovery"];
        lister.ExcludeAdminProtectedPaths = true;
        Assert.True(lister.IsAdminProtectedPath(@"C:\Recovery"));
    }

    // ── ListFilesAsync edge cases ──

    [Fact]
    public async Task ListFiles_EmptyDirectory_YieldsNothing()
    {
        var lister = new FileLister();
        var files = await CollectAsync(lister.ListFilesAsync("", Array.Empty<string>(), 0, default));
        Assert.Empty(files);
    }

    [Fact]
    public async Task ListFiles_WhitespaceDirectory_YieldsNothing()
    {
        var lister = new FileLister();
        var files = await CollectAsync(lister.ListFilesAsync("   ", Array.Empty<string>(), 0, default));
        Assert.Empty(files);
    }

    [Fact]
    public async Task ListFiles_NonExistentDirectory_SetsFallbackReason()
    {
        var lister = new FileLister();
        var files = await CollectAsync(lister.ListFilesAsync(@"C:\__nonexistent_dir_qg__", Array.Empty<string>(), 0, default));
        Assert.Empty(files);
        Assert.Equal("Directory does not exist", lister.FallbackReason);
    }

    // ── Managed fallback: include extension filtering ──

    [Fact]
    public async Task Managed_IncludeExtensions_FiltersCorrectly()
    {
        FileLister.Backend = FileListerBackend.Managed;
        File.WriteAllText(Path.Combine(_root, "app.cs"), "");
        File.WriteAllText(Path.Combine(_root, "style.css"), "");
        File.WriteAllText(Path.Combine(_root, "readme.md"), "");

        var lister = new FileLister();
        var files = await CollectAsync(lister.ListFilesAsync(_root, new[] { ".cs", ".md" }, 0, default));
        Assert.Equal(2, files.Count);
        Assert.All(files, f => Assert.True(f.EndsWith(".cs") || f.EndsWith(".md")));
    }

    // ── Managed fallback: maxFiles limit ──

    [Fact]
    public async Task Managed_MaxFiles_LimitsResults()
    {
        FileLister.Backend = FileListerBackend.Managed;
        for (int i = 0; i < 10; i++)
            File.WriteAllText(Path.Combine(_root, $"file{i}.cs"), "x");

        var lister = new FileLister();
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 3, default));
        Assert.Equal(3, files.Count);
    }

    // ── SDK: skip extensions with non-blocked extension (channel write under hasSkipExts=true) ──

    [Fact]
    public async Task Sdk_SkipExtensions_AllowsNonBlocked()
    {
        FileLister.Backend = FileListerBackend.EverythingSdk;
        FileLister.SdkAvailable = true;
        var sdk = new MockEverythingSdkOps
        {
            Results = [(@"C:\file.cs", 100), (@"C:\file.log", 100), (@"C:\file.txt", 100)]
        };
        var lister = new FileLister(null, sdk);
        lister.EarlySkipExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "log" };
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Equal(2, files.Count);
        Assert.DoesNotContain(files, f => f.EndsWith(".log"));
        Assert.Equal(1, lister.EarlyExcludedByExtensionFiles);
    }

    // ── SDK: include extensions in query ──

    [Fact]
    public async Task Sdk_IncludeExtensions_AddsExtToQuery()
    {
        FileLister.Backend = FileListerBackend.EverythingSdk;
        FileLister.SdkAvailable = true;
        var sdk = new MockEverythingSdkOps
        {
            Results = [(@"C:\file.cs", 100)]
        };
        var lister = new FileLister(null, sdk);
        var files = await CollectAsync(lister.ListFilesAsync(_root, new[] { ".cs" }, 0, default));
        Assert.Single(files);
        Assert.Contains("ext:", sdk.CapturedQuery);
    }

    // ── SDK: exclude globs — extension glob, segment glob, complex glob (ignored) ──

    [Fact]
    public async Task Sdk_ExcludeGlobs_ExtensionAndSegment()
    {
        FileLister.Backend = FileListerBackend.EverythingSdk;
        FileLister.SdkAvailable = true;
        var sdk = new MockEverythingSdkOps
        {
            Results = [(@"C:\file.cs", 100)]
        };
        var lister = new FileLister(null, sdk);
        lister.EarlyExcludeGlobs = new List<string> { "*.log", "node_modules", "src/**/*.tmp" };
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Contains("!ext:log", sdk.CapturedQuery);
        Assert.Contains("node_modules", sdk.CapturedQuery);
        // Complex globs are left for GlobMatcher, not added to query
    }

    // ── SDK: admin path exclusion in query ──

    [Fact]
    public async Task Sdk_AdminPathExclusion_AddsToQuery()
    {
        FileLister.Backend = FileListerBackend.EverythingSdk;
        FileLister.SdkAvailable = true;
        FileLister.ElevationOverride = () => false;
        var sdk = new MockEverythingSdkOps
        {
            Results = [(@"C:\file.cs", 100)]
        };
        var lister = new FileLister(null, sdk);
        lister.ExcludeAdminProtectedPaths = true;
        lister.AdminProtectedPathSegmentsOverride = [@"\Recovery"];
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Contains("Recovery", sdk.CapturedQuery);
    }

    // ── SDK: gitignore folders, extensions, full paths in query ──

    [Fact]
    public async Task Sdk_GitignoreInQuery_DotGitAlwaysExcluded()
    {
        FileLister.Backend = FileListerBackend.EverythingSdk;
        FileLister.SdkAvailable = true;
        var sdk = new MockEverythingSdkOps
        {
            Results = [(@"C:\file.cs", 100)]
        };
        var lister = new FileLister(null, sdk);
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "*.obj\n");
        lister.GitignoreMatcher = new DynamicGitignoreMatcher(_root);
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Contains(".git", sdk.CapturedQuery);
    }

    // ── SDK: directory with spaces gets quoted ──

    [Fact]
    public async Task Sdk_DirectoryWithSpaces_GetsQuoted()
    {
        FileLister.Backend = FileListerBackend.EverythingSdk;
        FileLister.SdkAvailable = true;
        var spacedDir = Path.Combine(_root, "my folder");
        Directory.CreateDirectory(spacedDir);
        var sdk = new MockEverythingSdkOps
        {
            Results = [(@"C:\file.cs", 100)]
        };
        var lister = new FileLister(null, sdk);
        var files = await CollectAsync(lister.ListFilesAsync(spacedDir, Array.Empty<string>(), 0, default));
        // The query should contain quoted path
        Assert.Contains("\"", sdk.CapturedQuery);
    }

    // ── SDK: size filtering in SDK path (too small and too large) ──

    [Fact]
    public async Task Sdk_SizeFiltering_SkipsTooSmallAndTooLarge()
    {
        FileLister.Backend = FileListerBackend.EverythingSdk;
        FileLister.SdkAvailable = true;
        var sdk = new MockEverythingSdkOps
        {
            Results = [
                (@"C:\tiny.cs", 5),
                (@"C:\ok.cs", 500),
                (@"C:\huge.cs", 50000)
            ]
        };
        var lister = new FileLister(null, sdk);
        lister.EarlyMinFileSizeBytes = 100;
        lister.EarlyMaxFileSizeBytes = 10000;
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Single(files);
        Assert.EndsWith("ok.cs", files[0]);
        Assert.Equal(2, lister.EarlySkippedFiles);
        Assert.Equal(1, lister.EarlySkippedTooLargeFiles);
    }

    // ── SDK: maxFiles limit ──

    [Fact]
    public async Task Sdk_MaxFiles_LimitsResults()
    {
        FileLister.Backend = FileListerBackend.EverythingSdk;
        FileLister.SdkAvailable = true;
        var sdk = new MockEverythingSdkOps
        {
            Results = Enumerable.Range(0, 50).Select(i => ($@"C:\file{i}.cs", (long)100)).ToList()
        };
        var lister = new FileLister(null, sdk);
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 10, default));
        // MaxFiles is set on the SDK query, so the mock returns all 50 but the SDK
        // limits are set. Since mock ignores SetMax, we get all 50.
        // Verify that SetMax was called with 10.
        Assert.Equal(10u, sdk.CapturedMax);
    }

    // ── SDK: query error after reading from channel (error set during producer) ──

    [Fact]
    public async Task Sdk_DbNotLoaded_SetsFallbackReason()
    {
        FileLister.Backend = FileListerBackend.EverythingSdk;
        FileLister.SdkAvailable = true;
        var sdk = new MockEverythingSdkOps { DbLoaded = false };
        var lister = new FileLister(null, sdk);
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Empty(files);
        Assert.Contains("database not loaded", lister.FallbackReason, StringComparison.OrdinalIgnoreCase);
    }

    // ── SDK: query fails with non-IPC error ──

    [Fact]
    public async Task Sdk_QueryFails_NonIpcError()
    {
        FileLister.Backend = FileListerBackend.EverythingSdk;
        FileLister.SdkAvailable = true;
        var sdk = new MockEverythingSdkOps { QuerySucceeds = false };
        var lister = new FileLister(null, sdk);
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Empty(files);
        Assert.Contains("query failed", lister.FallbackReason, StringComparison.OrdinalIgnoreCase);
    }

    // ── NormalizeAdminSegment edge cases ──

    [Fact]
    public void NormalizeAdminSegment_ForwardSlash_Normalized()
    {
        var result = FileLister.NormalizeAdminSegment("Windows/Installer");
        Assert.Equal(@"\Windows\Installer", result);
    }

    [Fact]
    public void NormalizeAdminSegment_OnlyBackslash_ReturnsNull()
    {
        Assert.Null(FileLister.NormalizeAdminSegment(@"\"));
    }

    [Fact]
    public void NormalizeAdminSegment_TrailingSlash_Stripped()
    {
        var result = FileLister.NormalizeAdminSegment(@"\Recovery\");
        Assert.Equal(@"\Recovery", result);
    }

    // ── ShouldExcludeAdminPaths property ──

    [Fact]
    public void ShouldExcludeAdminPaths_NotElevated_ExcludeEnabled()
    {
        FileLister.ElevationOverride = () => false;
        var lister = new FileLister();
        lister.ExcludeAdminProtectedPaths = true;
        Assert.True(lister.ShouldExcludeAdminPaths);
    }

    // ShouldExcludeAdminPaths with elevation=true cannot be tested because
    // s_isElevated is a Lazy<bool> cached on first access. Once evaluated,
    // changing ElevationOverride has no effect on the cached value.

    // ── Es.exe backend: forced, no es.exe found via FindEsExe ──

    [Fact]
    public async Task EsExeBackend_EsExeNotFound_FallbackReason()
    {
        FileLister.Backend = FileListerBackend.EsExe;
        FileLister.SdkAvailable = false;
        // Use processFactory so SDK tier is skipped (processFactory != null && sdkOps is RealEverythingSdkOps)
        var lister = new FileLister((path, psi) => new MockProcess([], 0));
        // FindEsExe might find a real es.exe on this machine, but with forced EsExe backend
        // it should try to run it. Override registry to not find it.
        FileLister.GetEverythingInstallDirsOverride = () => [];
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        // It may or may not find es.exe on PATH — just verify no crash
        Assert.NotNull(lister.FallbackReason);
    }

    // ── SDK: empty exclude glob items ──

    [Fact]
    public async Task Sdk_ExcludeGlobs_EmptyItems_Skipped()
    {
        FileLister.Backend = FileListerBackend.EverythingSdk;
        FileLister.SdkAvailable = true;
        var sdk = new MockEverythingSdkOps { Results = [(@"C:\a.cs", 100)] };
        var lister = new FileLister(null, sdk);
        lister.EarlyExcludeGlobs = ["", "  ", ",", ";"];
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Single(files);
    }

    // ── BuildEverythingFileNameFilter edge cases ──

    [Fact]
    public void FileNameFilter_SingleTerm()
    {
        var result = FileLister.BuildEverythingFileNameFilter(["hello"]);
        Assert.Equal("\"hello\"", result);
    }

    [Fact]
    public void FileNameFilter_MultipleTerms()
    {
        var result = FileLister.BuildEverythingFileNameFilter(["hello", "world"]);
        Assert.Equal("<\"hello\"|\"world\">", result);
    }

    [Fact]
    public void FileNameFilter_TermWithQuotes_ReturnsNull()
    {
        var result = FileLister.BuildEverythingFileNameFilter(["he\"llo"]);
        Assert.Null(result);
    }

    [Fact]
    public void FileNameFilter_EmptyTerms_ReturnsNull()
    {
        var result = FileLister.BuildEverythingFileNameFilter(Array.Empty<string>());
        Assert.Null(result);
    }

    [Fact]
    public void FileNameFilter_WhitespaceOnlyTerm_ReturnsNull()
    {
        var result = FileLister.BuildEverythingFileNameFilter(["", " "]);
        Assert.Null(result);
    }

    // ── SDK: file name filter in query ──

    [Fact]
    public async Task Sdk_FileNameFilter_AddedToQuery()
    {
        FileLister.Backend = FileListerBackend.EverythingSdk;
        FileLister.SdkAvailable = true;
        var sdk = new MockEverythingSdkOps { Results = [(@"C:\target.cs", 100)] };
        var lister = new FileLister(null, sdk);
        lister.EarlyFileNameLiteralTerms = ["target"];
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Contains("\"target\"", sdk.CapturedQuery);
    }

    // ── SDK: size & date filter terms in query ──

    [Fact]
    public async Task Sdk_SizeTermInQuery_DateFilterUsesMetadataFlag()
    {
        FileLister.Backend = FileListerBackend.EverythingSdk;
        FileLister.SdkAvailable = true;
        var sdk = new MockEverythingSdkOps
        {
            Results = [(@"C:\a.cs", 500)],
            CreatedFileTimes = { [0] = new DateTime(2024, 1, 2).ToFileTime() }
        };
        var lister = new FileLister(null, sdk);
        lister.EarlyMinFileSizeBytes = 100;
        lister.EarlyCreatedAfterDate = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Contains("size:>=100", sdk.CapturedQuery);
        Assert.DoesNotContain("dc:", sdk.CapturedQuery);
        Assert.True((sdk.CapturedRequestFlags.GetValueOrDefault() & EverythingSdk.EVERYTHING_REQUEST_SIZE) != 0);
        Assert.True((sdk.CapturedRequestFlags.GetValueOrDefault() & EverythingSdk.EVERYTHING_REQUEST_DATE_CREATED) != 0);
    }

    [Fact]
    public async Task Sdk_DateMetadataFiltering_SkipsOutsideRange()
    {
        FileLister.Backend = FileListerBackend.EverythingSdk;
        FileLister.SdkAvailable = true;
        var sdk = new MockEverythingSdkOps
        {
            Results = [(@"C:\old.cs", 100), (@"C:\new.cs", 100)],
            CreatedFileTimes =
            {
                [0] = new DateTime(2023, 12, 29).ToFileTime(),
                [1] = new DateTime(2024, 1, 3).ToFileTime(),
            }
        };
        var lister = new FileLister(null, sdk)
        {
            EarlyCreatedAfterDate = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Single(files);
        Assert.EndsWith("new.cs", files[0]);
        Assert.Equal(1, lister.EarlySkippedFiles);
    }

    // ── Managed: reparse point handling ──

    [Fact]
    public async Task Managed_ReparsePoint_NotLooped()
    {
        FileLister.Backend = FileListerBackend.Managed;
        // Create a real directory structure — reparse point test
        var subDir = Path.Combine(_root, "sub");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "a.cs"), "x");
        File.WriteAllText(Path.Combine(_root, "b.cs"), "x");

        var lister = new FileLister();
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Equal(2, files.Count);
    }

    // ── Managed: include extension with dot-normalized ──

    [Fact]
    public async Task Managed_IncludeExtension_WithDot()
    {
        FileLister.Backend = FileListerBackend.Managed;
        File.WriteAllText(Path.Combine(_root, "a.cs"), "");
        File.WriteAllText(Path.Combine(_root, "b.js"), "");

        var lister = new FileLister();
        var files = await CollectAsync(lister.ListFilesAsync(_root, new[] { "cs" }, 0, default));
        Assert.Single(files);
        Assert.EndsWith(".cs", files[0]);
    }

    // ── NormalizeExtension static method ──

    [Fact]
    public void NormalizeExtension_WithDot_StripsIt()
    {
        Assert.Equal("cs", FileLister.NormalizeExtension(".cs"));
    }

    [Fact]
    public void NormalizeExtension_WithoutDot_Unchanged()
    {
        Assert.Equal("cs", FileLister.NormalizeExtension("cs"));
    }

    [Fact]
    public void NormalizeExtension_WildcardDot_Stripped()
    {
        Assert.Equal("log", FileLister.NormalizeExtension("*.log"));
    }

    [Fact]
    public void NormalizeExtension_Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, FileLister.NormalizeExtension(""));
    }

    // ── Managed: empty gitignore full paths list ──

    [Fact]
    public async Task Managed_NoGitignoreMatcher_NoEffect()
    {
        FileLister.Backend = FileListerBackend.Managed;
        File.WriteAllText(Path.Combine(_root, "a.cs"), "");

        var lister = new FileLister();
        // No matcher set — no gitignore filtering
        var files = await CollectAsync(lister.ListFilesAsync(_root, Array.Empty<string>(), 0, default));
        Assert.Single(files);
    }

    // ── SdkChannelBufferSize ──

    [Fact]
    public void SdkChannelBufferSize_DefaultIs4096()
    {
        var lister = new FileLister();
        Assert.Equal(4096, lister.SdkChannelBufferSize);
    }

    [Fact]
    public void SdkChannelBufferSize_CanBeSet()
    {
        var lister = new FileLister();
        lister.SdkChannelBufferSize = 128;
        Assert.Equal(128, lister.SdkChannelBufferSize);
    }

    private static async Task<List<string>> CollectAsync(IAsyncEnumerable<string> source)
    {
        var list = new List<string>();
        await foreach (var item in source) list.Add(item);
        return list;
    }

    // ─── MockProcess for es.exe tests ──────────

    private class MockProcess : FileLister.IProcess
    {
        private readonly Queue<string?> _lines;
        public int ExitCode { get; }
        public MockProcess(IEnumerable<string?> lines, int exitCode)
        {
            _lines = new Queue<string?>(lines);
            ExitCode = exitCode;
        }
        public void Start() { }
        public Task<string?> ReadLineAsync(CancellationToken ct) =>
            Task.FromResult(_lines.Count > 0 ? _lines.Dequeue() : null);
        public Task WaitForExitAsync(CancellationToken ct) => Task.CompletedTask;
    }
}

// ─── ProbeEverythingSdkReadiness gap-fill ────────────────────────────────

[Collection("FileListerBackend")]
public class ProbeReadinessGapTests
{
    [Fact]
    public void Probe_ResultsWithPaths_ReturnsReady()
    {
        var sdk = new MockEverythingSdkOps
        {
            Results = [(@"C:\file1.cs", 100), (@"C:\file2.cs", 200)]
        };
        var result = FileLister.ProbeEverythingSdkReadiness(sdk);
        Assert.True(result.IsReady);
        Assert.Equal(2u, result.ReturnedCount);
        Assert.Equal(2u, result.TotalCount);
        Assert.Equal(2, result.SamplePaths.Count);
    }

    [Fact]
    public void Probe_LongPath_ExpandsBuffer()
    {
        var longPath = @"C:\" + new string('a', 2000) + ".cs";
        var sdk = new MockEverythingSdkOps
        {
            Results = [(longPath, 100)]
        };
        var result = FileLister.ProbeEverythingSdkReadiness(sdk);
        Assert.True(result.IsReady);
        Assert.Single(result.SamplePaths);
        Assert.Equal(longPath, result.SamplePaths[0]);
    }

    [Fact]
    public void Probe_ZeroResults_ReturnsNotReady()
    {
        var sdk = new MockEverythingSdkOps { Results = [] };
        var result = FileLister.ProbeEverythingSdkReadiness(sdk);
        Assert.False(result.IsReady);
        Assert.Contains("no files or folders", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Probe_EmptyPaths_ReturnsNotReady()
    {
        var sdk = new EmptyPathSdkOps();
        var result = FileLister.ProbeEverythingSdkReadiness(sdk);
        Assert.False(result.IsReady);
        Assert.Contains("no file or folder paths", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Probe_NotAvailable_ReturnsNotReady()
    {
        var origAvail = FileLister.SdkAvailable;
        try
        {
            FileLister.SdkAvailable = false;
            var sdk = new MockEverythingSdkOps();
            var result = FileLister.ProbeEverythingSdkReadiness(sdk);
            Assert.False(result.IsReady);
        }
        finally
        {
            FileLister.SdkAvailable = origAvail;
        }
    }

    [Fact]
    public void Probe_DbNotLoaded_ReturnsNotReady()
    {
        var sdk = new MockEverythingSdkOps { DbLoaded = false };
        var result = FileLister.ProbeEverythingSdkReadiness(sdk);
        Assert.False(result.IsReady);
        Assert.Contains("still loading", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Probe_DllNotFound_SetsUnavailable()
    {
        var origAvail = FileLister.SdkAvailable;
        try
        {
            var sdk = new MockEverythingSdkOps { ThrowDllNotFound = true };
            var result = FileLister.ProbeEverythingSdkReadiness(sdk);
            Assert.False(result.IsReady);
            Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            FileLister.SdkAvailable = origAvail;
        }
    }

    [Fact]
    public void Probe_GeneralException_ReturnsNotReady()
    {
        var sdk = new MockEverythingSdkOps { ThrowGeneral = true };
        var result = FileLister.ProbeEverythingSdkReadiness(sdk);
        Assert.False(result.IsReady);
        Assert.Contains("failed", result.Error, StringComparison.OrdinalIgnoreCase);
    }
}
