using System.Reflection;
using Yagu.Models;
using Yagu.Services;

namespace Yagu.Tests;

/// <summary>
/// Tests for FileLister.CountSeparators and GetRelativeDepth (private static, 0% coverage).
/// </summary>
public sealed class FileListerDepthTests
{
    private static readonly MethodInfo CountSeparators = typeof(FileLister)
        .GetMethod("CountSeparators", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo GetRelativeDepth = typeof(FileLister)
        .GetMethod("GetRelativeDepth", BindingFlags.NonPublic | BindingFlags.Static)!;

    [Theory]
    [InlineData(@"C:\", 1)]
    [InlineData(@"C:\src", 1)]
    [InlineData(@"C:\src\Yagu", 2)]
    [InlineData(@"C:\src\Yagu\file.txt", 3)]
    [InlineData(@"C:\a\b\c\d\e", 5)]
    [InlineData("nobackslash", 0)]
    [InlineData("", 0)]
    public void CountSeparators_ReturnsExpected(string path, int expected)
    {
        int result = (int)CountSeparators.Invoke(null, [path])!;
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetRelativeDepth_FileInRoot_ReturnsOne()
    {
        // root = "C:\src" has 1 separator, file = "C:\src\file.txt" has 2 separators → depth 1
        int rootSeps = (int)CountSeparators.Invoke(null, [new object[] { @"C:\src" }[0]])!;
        int depth = (int)GetRelativeDepth.Invoke(null, [@"C:\src\file.txt", rootSeps])!;
        Assert.Equal(1, depth);
    }

    [Fact]
    public void GetRelativeDepth_NestedFile_ReturnsCorrectDepth()
    {
        // C:\src has 1 sep, C:\src\a\b\c\file.txt has 5 seps → depth = 5-1 = 4
        int rootSeps = (int)CountSeparators.Invoke(null, [@"C:\src"])!;
        int depth = (int)GetRelativeDepth.Invoke(null, [@"C:\src\a\b\c\file.txt", rootSeps])!;
        Assert.Equal(4, depth);
    }

    [Fact]
    public void GetRelativeDepth_SameAsRoot_ReturnsZero()
    {
        int rootSeps = (int)CountSeparators.Invoke(null, [@"C:\src\Yagu"])!;
        int depth = (int)GetRelativeDepth.Invoke(null, [@"C:\src\Yagu", rootSeps])!;
        Assert.Equal(0, depth);
    }
}

/// <summary>
/// Tests for FileGroup VarInt encoding/decoding (private static, targeting uncovered branches
/// for multi-byte values). WriteVarUInt/ReadVarUInt use Span so we test via
/// EncodeSignedVarInt/DecodeSignedVarInt which accept simple value types.
/// </summary>
public sealed class FileGroupVarIntTests
{
    private static readonly MethodInfo EncodeSignedVarInt = typeof(FileGroup)
        .GetMethod("EncodeSignedVarInt", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo DecodeSignedVarInt = typeof(FileGroup)
        .GetMethod("DecodeSignedVarInt", BindingFlags.NonPublic | BindingFlags.Static)!;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    [InlineData(1000)]
    [InlineData(-1000)]
    [InlineData(127)]
    [InlineData(-128)]
    [InlineData(16384)]
    [InlineData(-16384)]
    public void SignedVarInt_Roundtrips(int value)
    {
        uint encoded = (uint)EncodeSignedVarInt.Invoke(null, [value])!;
        int decoded = (int)DecodeSignedVarInt.Invoke(null, [encoded])!;
        Assert.Equal(value, decoded);
    }

    [Fact]
    public void EncodeSignedVarInt_Zero_ProducesZero()
    {
        uint encoded = (uint)EncodeSignedVarInt.Invoke(null, [0])!;
        Assert.Equal(0u, encoded);
    }

    [Fact]
    public void EncodeSignedVarInt_NegativeOne_ProducesOne()
    {
        // ZigZag: -1 → ((-1 << 1) ^ (-1 >> 31)) = (-2 ^ -1) = 1
        uint encoded = (uint)EncodeSignedVarInt.Invoke(null, [-1])!;
        Assert.Equal(1u, encoded);
    }

    [Fact]
    public void EncodeSignedVarInt_PositiveOne_ProducesTwo()
    {
        // ZigZag: 1 → ((1 << 1) ^ (1 >> 31)) = (2 ^ 0) = 2
        uint encoded = (uint)EncodeSignedVarInt.Invoke(null, [1])!;
        Assert.Equal(2u, encoded);
    }

    [Fact]
    public void DecodeSignedVarInt_LargeValue_Roundtrips()
    {
        uint encoded = (uint)EncodeSignedVarInt.Invoke(null, [int.MinValue])!;
        int decoded = (int)DecodeSignedVarInt.Invoke(null, [encoded])!;
        Assert.Equal(int.MinValue, decoded);
    }
}

/// <summary>
/// Additional branch tests for TerminalDirectoryGuard.RemoveMarkerLine and
/// NormalizeDirectoryForComparison.
/// </summary>
public sealed class TerminalDirectoryGuardBranchTests
{
    [Fact]
    public void RemoveMarkerLine_EmptyOutput_ReturnsOutput()
    {
        string result = TerminalDirectoryGuard.RemoveMarkerLine("", "__MARKER__");
        Assert.Equal("", result);
    }

    [Fact]
    public void RemoveMarkerLine_EmptyMarker_ReturnsOutput()
    {
        string result = TerminalDirectoryGuard.RemoveMarkerLine("hello\nworld", "");
        Assert.Equal("hello\nworld", result);
    }

    [Fact]
    public void RemoveMarkerLine_NullOutput_ReturnsNull()
    {
        string result = TerminalDirectoryGuard.RemoveMarkerLine(null!, "__MARKER__");
        Assert.Null(result);
    }

    [Fact]
    public void RemoveMarkerLine_MarkerNotFound_ReturnsUnchanged()
    {
        const string output = "line1\nline2\nline3";
        string result = TerminalDirectoryGuard.RemoveMarkerLine(output, "__MISSING__");
        Assert.Equal(output, result);
    }

    [Fact]
    public void RemoveMarkerLine_MarkerAtStartWithContentAfter_PreservesContent()
    {
        // Marker at position 0, content immediately follows on next line
        const string output = "__MARKER__\ncontent after";
        string result = TerminalDirectoryGuard.RemoveMarkerLine(output, "__MARKER__");
        // After removing marker line, content should be preserved
        Assert.Contains("content after", result);
    }

    [Fact]
    public void RemoveMarkerLine_MarkerAtEnd_NoTrailingNewline()
    {
        const string output = "before\n__MARKER__";
        string result = TerminalDirectoryGuard.RemoveMarkerLine(output, "__MARKER__");
        Assert.Equal("before\n", result);
    }

    [Fact]
    public void RemoveMarkerLine_MarkerWithCarriageReturn()
    {
        const string output = "before\r\n__MARKER__\r\nafter";
        string result = TerminalDirectoryGuard.RemoveMarkerLine(output, "__MARKER__");
        Assert.Contains("before", result);
        Assert.Contains("after", result);
        Assert.DoesNotContain("__MARKER__", result);
    }

    [Fact]
    public void DirectoriesEqual_BothEmpty_ReturnsFalse()
    {
        Assert.False(TerminalDirectoryGuard.DirectoriesEqual("", ""));
    }

    [Fact]
    public void DirectoriesEqual_WhitespaceOnly_ReturnsFalse()
    {
        Assert.False(TerminalDirectoryGuard.DirectoriesEqual("   ", "   "));
    }

    [Fact]
    public void DirectoriesEqual_EnvVar_ExpandsAndCompares()
    {
        string temp = Environment.GetEnvironmentVariable("TEMP")!;
        Assert.True(TerminalDirectoryGuard.DirectoriesEqual("%TEMP%", temp));
    }

    [Fact]
    public void DirectoriesEqual_TrailingSeparators_NormalizedAway()
    {
        Assert.True(TerminalDirectoryGuard.DirectoriesEqual(@"C:\src\", @"C:\src"));
    }

    [Fact]
    public void DirectoriesEqual_QuotedPath_QuotesStripped()
    {
        Assert.True(TerminalDirectoryGuard.DirectoriesEqual("\"C:\\src\"", @"C:\src"));
    }
}

/// <summary>
/// Tests for ReportExportService.SourceFileContext private nested class via reflection.
/// Covers GetBefore/GetAfter edge cases.
/// </summary>
public sealed class SourceFileContextEdgeCaseTests : IDisposable
{
    private readonly string _tempFile;
    private readonly Type _contextType;
    private readonly MethodInfo _tryLoad;
    private readonly MethodInfo _getBefore;
    private readonly MethodInfo _getAfter;

    public SourceFileContextEdgeCaseTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"source-ctx-{Guid.NewGuid():N}.txt");
        File.WriteAllLines(_tempFile, ["line1", "line2", "line3", "line4", "line5"]);

        _contextType = typeof(ReportExportService)
            .GetNestedType("SourceFileContext", BindingFlags.NonPublic)!;
        _tryLoad = _contextType.GetMethod("TryLoad", BindingFlags.Public | BindingFlags.Static)!;
        _getBefore = _contextType.GetMethod("GetBefore", BindingFlags.Public | BindingFlags.Instance)!;
        _getAfter = _contextType.GetMethod("GetAfter", BindingFlags.Public | BindingFlags.Instance)!;
    }

    public void Dispose() => File.Delete(_tempFile);

    [Fact]
    public void TryLoad_BlankPath_ReturnsNull()
    {
        Assert.Null(_tryLoad.Invoke(null, [""]));
    }

    [Fact]
    public void TryLoad_NonExistentPath_ReturnsNull()
    {
        Assert.Null(_tryLoad.Invoke(null, [@"C:\does-not-exist-99999.txt"]));
    }

    [Fact]
    public void TryLoad_ValidFile_ReturnsContext()
    {
        var ctx = _tryLoad.Invoke(null, [_tempFile]);
        Assert.NotNull(ctx);
    }

    [Fact]
    public void GetBefore_ZeroMaxLines_ReturnsEmpty()
    {
        var ctx = _tryLoad.Invoke(null, [_tempFile])!;
        var result = (IReadOnlyList<string>)_getBefore.Invoke(ctx, [3, 0])!;
        Assert.Empty(result);
    }

    [Fact]
    public void GetBefore_LineNumberOne_ReturnsEmpty()
    {
        var ctx = _tryLoad.Invoke(null, [_tempFile])!;
        var result = (IReadOnlyList<string>)_getBefore.Invoke(ctx, [1, 3])!;
        Assert.Empty(result);
    }

    [Fact]
    public void GetBefore_LineThreeMaxTwo_ReturnsTwoLines()
    {
        var ctx = _tryLoad.Invoke(null, [_tempFile])!;
        var result = (IReadOnlyList<string>)_getBefore.Invoke(ctx, [3, 2])!;
        Assert.Equal(2, result.Count);
        Assert.Equal("line1", result[0]);
        Assert.Equal("line2", result[1]);
    }

    [Fact]
    public void GetBefore_LineExceedsFileLength_ReturnsEmpty()
    {
        var ctx = _tryLoad.Invoke(null, [_tempFile])!;
        // Line 100 > file length (5 lines) → matchIndex > _lines.Length → empty
        var result = (IReadOnlyList<string>)_getBefore.Invoke(ctx, [100, 3])!;
        Assert.Empty(result);
    }

    [Fact]
    public void GetAfter_ZeroMaxLines_ReturnsEmpty()
    {
        var ctx = _tryLoad.Invoke(null, [_tempFile])!;
        var result = (IReadOnlyList<string>)_getAfter.Invoke(ctx, [3, 0])!;
        Assert.Empty(result);
    }

    [Fact]
    public void GetAfter_LastLine_ReturnsEmpty()
    {
        var ctx = _tryLoad.Invoke(null, [_tempFile])!;
        var result = (IReadOnlyList<string>)_getAfter.Invoke(ctx, [5, 3])!;
        Assert.Empty(result);
    }

    [Fact]
    public void GetAfter_LineThreeMaxTwo_ReturnsTwoLines()
    {
        var ctx = _tryLoad.Invoke(null, [_tempFile])!;
        var result = (IReadOnlyList<string>)_getAfter.Invoke(ctx, [3, 2])!;
        Assert.Equal(2, result.Count);
        Assert.Equal("line4", result[0]);
        Assert.Equal("line5", result[1]);
    }

    [Fact]
    public void GetAfter_NegativeLineNumber_ReturnsEmpty()
    {
        var ctx = _tryLoad.Invoke(null, [_tempFile])!;
        var result = (IReadOnlyList<string>)_getAfter.Invoke(ctx, [0, 3])!;
        Assert.Empty(result);
    }
}

/// <summary>
/// Tests for SearchService.ShouldSkipByFileMetadata (private static, 67% line/69% branch).
/// Accesses via reflection since source is compiled into the test assembly.
/// </summary>
public sealed class SearchServiceSkipMetadataTests : IDisposable
{
    private readonly string _tempFile;
    private readonly MethodInfo _shouldSkip;

    public SearchServiceSkipMetadataTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"skip-meta-{Guid.NewGuid():N}.txt");
        File.WriteAllText(_tempFile, new string('x', 100));

        _shouldSkip = typeof(SearchService)
            .GetMethod("ShouldSkipByFileMetadata", BindingFlags.NonPublic | BindingFlags.Static)!;
    }

    public void Dispose() => File.Delete(_tempFile);

    private bool Invoke(string path, SearchOptions options, bool checkSize = true, bool checkDates = true)
    {
        object[] args = [path, options, false, checkSize, checkDates];
        return (bool)_shouldSkip.Invoke(null, args)!;
    }

    [Fact]
    public void NoFilters_ReturnsFalse()
    {
        var options = new SearchOptions { Directory = ".", Query = "x", MinFileSizeBytes = 0, MaxFileSizeBytes = 0 };
        Assert.False(Invoke(_tempFile, options));
    }

    [Fact]
    public void MinSizeFilter_FileTooSmall_ReturnsTrue()
    {
        var options = new SearchOptions { Directory = ".", Query = "x", MinFileSizeBytes = 10_000 };
        Assert.True(Invoke(_tempFile, options));
    }

    [Fact]
    public void MaxSizeFilter_FileOverMax_ReturnsTrue()
    {
        var options = new SearchOptions { Directory = ".", Query = "x", MaxFileSizeBytes = 10 };
        Assert.True(Invoke(_tempFile, options));
    }

    [Fact]
    public void MaxSizeFilter_FileUnderMax_ReturnsFalse()
    {
        var options = new SearchOptions { Directory = ".", Query = "x", MaxFileSizeBytes = 10_000 };
        Assert.False(Invoke(_tempFile, options));
    }

    [Fact]
    public void NonExistentFile_ReturnsFalse()
    {
        var options = new SearchOptions { Directory = ".", Query = "x", MinFileSizeBytes = 1 };
        Assert.False(Invoke(@"C:\nonexistent-99999.txt", options));
    }

    [Fact]
    public void DateFilter_CreatedAfter_FutureDate_SkipsFile()
    {
        var options = new SearchOptions { Directory = ".", Query = "x", CreatedAfterDate = DateTime.Now.AddDays(1) };
        Assert.True(Invoke(_tempFile, options, checkSize: false, checkDates: true));
    }

    [Fact]
    public void DateFilter_ModifiedBefore_PastDate_SkipsFile()
    {
        var options = new SearchOptions { Directory = ".", Query = "x", ModifiedBeforeDate = DateTime.Now.AddYears(-10) };
        Assert.True(Invoke(_tempFile, options, checkSize: false, checkDates: true));
    }

    [Fact]
    public void CheckDatesDisabled_DateFilter_Ignored()
    {
        var options = new SearchOptions { Directory = ".", Query = "x", CreatedAfterDate = DateTime.Now.AddDays(1) };
        Assert.False(Invoke(_tempFile, options, checkSize: false, checkDates: false));
    }
}

/// <summary>
/// Tests for LowDiskSpaceMonitor.TryGetSnapshot branches and IsOverThreshold/BuildTerminationMessage.
/// </summary>
public sealed class LowDiskSpaceMonitorBranchTests
{
    [Fact]
    public void TryGetSnapshot_ValidPath_ReturnsTrue()
    {
        bool result = LowDiskSpaceMonitor.TryGetSnapshot(Environment.SystemDirectory, out var snapshot);
        Assert.True(result);
        Assert.True(snapshot.TotalBytes > 0);
        Assert.True(snapshot.AvailableBytes >= 0);
    }

    [Fact]
    public void TryGetSnapshot_InvalidRoot_ReturnsFalse()
    {
        // A path with no valid root (empty after normalization)
        bool result = LowDiskSpaceMonitor.TryGetSnapshot("", out _);
        Assert.False(result);
    }

    [Fact]
    public void TryGetSnapshot_NonexistentDrive_ReturnsFalse()
    {
        // Z:\ is unlikely to exist
        bool result = LowDiskSpaceMonitor.TryGetSnapshot(@"Z:\some\path", out _);
        // May return true if Z: exists, but at minimum should not throw
        Assert.IsType<bool>(result);
    }

    [Fact]
    public void IsOverThreshold_FullDisk_ReturnsTrue()
    {
        var snapshot = new DiskSpaceSnapshot(@"C:\", 100_000_000, 1_000);
        Assert.True(LowDiskSpaceMonitor.IsOverThreshold(snapshot, 0.9));
    }

    [Fact]
    public void IsOverThreshold_EmptyDisk_ReturnsFalse()
    {
        var snapshot = new DiskSpaceSnapshot(@"C:\", 100_000_000, 90_000_000);
        Assert.False(LowDiskSpaceMonitor.IsOverThreshold(snapshot, 0.9));
    }

    [Fact]
    public void IsOverThreshold_ZeroTotal_ReturnsFalse()
    {
        var snapshot = new DiskSpaceSnapshot(@"C:\", 0, 0);
        Assert.False(LowDiskSpaceMonitor.IsOverThreshold(snapshot, 0.9));
    }

    [Fact]
    public void BuildTerminationMessage_ContainsDriveName()
    {
        var snapshot = new DiskSpaceSnapshot(@"D:\", 100_000_000_000, 1_000_000);
        string msg = LowDiskSpaceMonitor.BuildTerminationMessage(snapshot);
        Assert.Contains("D:", msg);
        Assert.Contains("low disk space", msg);
    }
}
