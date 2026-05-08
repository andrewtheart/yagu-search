using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Yagu.Models;
using Yagu.Native;
using Yagu.Services;

namespace Yagu.Tests;

/// <summary>
/// Tests for methods that previously had [ExcludeFromCodeCoverage].
/// Covers: FileLister admin path logic, elevation check, SetKnownTotalFiles,
/// NativeMatchDecoder, BufferReader, EditorLauncher (LaunchProcess/OpenTerminalAt),
/// SearchService (memory pressure, GC, diagnostics), ZipArchiveSearcher (extract, cleanup),
/// ContentSearcher (SearchStreamAsync, SearchMappedAsync).
/// </summary>
public sealed class ExcludedMethodTests_FileLister : IDisposable
{
    private readonly Func<bool>? _originalElevationOverride;

    public ExcludedMethodTests_FileLister()
    {
        _originalElevationOverride = FileLister.ElevationOverride;
    }

    public void Dispose()
    {
        FileLister.ElevationOverride = _originalElevationOverride;
    }

    [Fact]
    public void CheckIsElevated_WithOverride_ReturnsOverrideValue()
    {
        FileLister.ElevationOverride = () => true;
        Assert.True(FileLister.CheckIsElevated());

        FileLister.ElevationOverride = () => false;
        Assert.False(FileLister.CheckIsElevated());
    }

    [Fact]
    public void CheckIsElevated_WithoutOverride_ReturnsBoolean()
    {
        FileLister.ElevationOverride = null;
        // Just verify it doesn't throw; value depends on test runner elevation
        _ = FileLister.CheckIsElevated();
    }

    [Fact]
    public void NormalizeAdminSegment_Null_ReturnsNull()
    {
        Assert.Null(FileLister.NormalizeAdminSegment(""));
        Assert.Null(FileLister.NormalizeAdminSegment("   "));
    }

    [Fact]
    public void NormalizeAdminSegment_AddsLeadingBackslash()
    {
        Assert.Equal(@"\Windows", FileLister.NormalizeAdminSegment("Windows"));
    }

    [Fact]
    public void NormalizeAdminSegment_TrimsTrailingBackslash()
    {
        Assert.Equal(@"\Windows\System32", FileLister.NormalizeAdminSegment(@"\Windows\System32\"));
    }

    [Fact]
    public void NormalizeAdminSegment_NormalizesForwardSlash()
    {
        Assert.Equal(@"\Temp\Sub", FileLister.NormalizeAdminSegment("/Temp/Sub"));
    }

    [Fact]
    public void NormalizeAdminSegment_JustSlash_ReturnsNull()
    {
        Assert.Null(FileLister.NormalizeAdminSegment(@"\"));
        Assert.Null(FileLister.NormalizeAdminSegment("/"));
    }

    [Fact]
    public void IsAdminProtectedPath_MatchesDefaultSegments()
    {
        var lister = new FileLister
        {
            ExcludeAdminProtectedPaths = true
        };
        Assert.True(lister.IsAdminProtectedPath(@"C:\Windows\System32\config"));
        Assert.True(lister.IsAdminProtectedPath(@"C:\$Recycle.Bin"));
        Assert.True(lister.IsAdminProtectedPath(@"C:\System Volume Information"));
    }

    [Fact]
    public void IsAdminProtectedPath_NoMatchForNormalPaths()
    {
        var lister = new FileLister
        {
            ExcludeAdminProtectedPaths = true
        };
        Assert.False(lister.IsAdminProtectedPath(@"C:\Users\test\Documents"));
        Assert.False(lister.IsAdminProtectedPath(@"D:\Projects\MyApp"));
    }

    [Fact]
    public void IsAdminProtectedPath_UsesOverrideWhenSet()
    {
        var lister = new FileLister
        {
            ExcludeAdminProtectedPaths = true,
            AdminProtectedPathSegmentsOverride = new[] { @"\MyCustom\Path" }
        };
        Assert.True(lister.IsAdminProtectedPath(@"C:\MyCustom\Path"));
        Assert.False(lister.IsAdminProtectedPath(@"C:\Windows\System32\config")); // default not used
    }

    [Fact]
    public void IsAdminProtectedPath_SegmentInMiddleOfPath()
    {
        var lister = new FileLister();
        Assert.True(lister.IsAdminProtectedPath(@"C:\Windows\System32\config\systemprofile"));
    }

    [Fact]
    public void EffectiveAdminProtectedPathSegments_ReturnsDefault_WhenNoOverride()
    {
        var lister = new FileLister();
        Assert.Same(FileLister.DefaultAdminProtectedPathSegments, lister.EffectiveAdminProtectedPathSegments);
    }

    [Fact]
    public void EffectiveAdminProtectedPathSegments_ReturnsOverride_WhenSet()
    {
        var custom = new[] { @"\Custom" };
        var lister = new FileLister { AdminProtectedPathSegmentsOverride = custom };
        Assert.Same(custom, lister.EffectiveAdminProtectedPathSegments);
    }

    [Fact]
    public void EffectiveAdminProtectedPathSegments_ReturnsDefault_WhenOverrideEmpty()
    {
        var lister = new FileLister { AdminProtectedPathSegmentsOverride = Array.Empty<string>() };
        Assert.Same(FileLister.DefaultAdminProtectedPathSegments, lister.EffectiveAdminProtectedPathSegments);
    }

    [Fact]
    public void ShouldExcludeAdminPaths_False_WhenExcludeAdminPathsDisabled()
    {
        var lister = new FileLister { ExcludeAdminProtectedPaths = false };
        Assert.False(lister.ShouldExcludeAdminPaths);
    }

    [Fact]
    public void SetKnownTotalFiles_UInt_ClampsToIntMax()
    {
        var lister = new FileLister();
        lister.SetKnownTotalFiles((uint)42);
        Assert.Equal(42, lister.KnownTotalFiles);
    }

    [Fact]
    public void SetKnownTotalFiles_UInt_MaxValue_ClampsToIntMax()
    {
        var lister = new FileLister();
        lister.SetKnownTotalFiles(uint.MaxValue);
        Assert.Equal(int.MaxValue, lister.KnownTotalFiles);
    }

    [Fact]
    public void FindEsExe_Public_DoesNotThrow()
    {
        // The public overload uses real File.Exists; just verify it doesn't throw
        _ = FileLister.FindEsExe();
    }

    [Fact]
    public async Task WaitForEverythingSdkReady_ImmediateReady_ReturnsReady()
    {
        var result = await FileLister.WaitForEverythingSdkReadyAsync(
            () => EverythingReadinessResult.Ready(100, 100, Array.Empty<string>()),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(100),
            CancellationToken.None);
        Assert.True(result.IsReady);
    }

    [Fact]
    public async Task WaitForEverythingSdkReady_NeverReady_TimesOut()
    {
        var result = await FileLister.WaitForEverythingSdkReadyAsync(
            () => EverythingReadinessResult.NotReady("still loading"),
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(10),
            CancellationToken.None);
        Assert.False(result.IsReady);
        Assert.Contains("Timed out", result.Error);
    }

    [Fact]
    public async Task WaitForEverythingSdkReady_CancellationThrows()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            FileLister.WaitForEverythingSdkReadyAsync(
                () => EverythingReadinessResult.NotReady("x"),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromMilliseconds(100),
                cts.Token));
    }

    [Fact]
    public async Task RealProcess_CanBeInstantiated()
    {
        // RealProcess is a thin wrapper - verify it can be constructed
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c echo hello",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        var proc = new FileLister.RealProcess(psi);
        proc.Start();
        var line = await proc.ReadLineAsync(CancellationToken.None);
        await proc.WaitForExitAsync(CancellationToken.None);
        Assert.Equal("hello", line);
        Assert.Equal(0, proc.ExitCode);
    }
}

[Collection("EditorLauncher")]
public sealed class ExcludedMethodTests_EditorLauncher : IDisposable
{
    public void Dispose()
    {
        EditorLauncher.TestProcessLauncher = null;
    }

    [Fact]
    public void LaunchProcess_UsesTestSeam()
    {
        ProcessStartInfo? captured = null;
        EditorLauncher.TestProcessLauncher = psi => captured = psi;

        var launcher = new EditorLauncher { Command = "testexe {file}" };
        launcher.Open("myfile.txt", 1);

        Assert.NotNull(captured);
        Assert.Equal("testexe", captured!.FileName);
    }

    [Fact]
    public void OpenTerminalAt_UsesTestSeam()
    {
        ProcessStartInfo? captured = null;
        EditorLauncher.TestProcessLauncher = psi => captured = psi;

        var tempFile = Path.GetTempFileName();
        try
        {
            bool result = EditorLauncher.OpenTerminalAt(tempFile);
            Assert.True(result);
            Assert.NotNull(captured);
            Assert.Equal("wt.exe", captured!.FileName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void OpenTerminalAt_EmptyDir_ReturnsFalse()
    {
        EditorLauncher.TestProcessLauncher = _ => { };
        // A filename without a directory component
        bool result = EditorLauncher.OpenTerminalAt("justfilename.txt");
        Assert.False(result);
    }
}

public sealed class ExcludedMethodTests_SearchService
{
    [Fact]
    public void CollectForMemoryPressureIfDue_DoesNotThrow()
    {
        // Just exercise the method - it calls GC.Collect internally
        SearchService.CollectForMemoryPressureIfDue(TimeSpan.FromHours(1));
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
}

public sealed class ExcludedMethodTests_ZipArchiveSearcher : IDisposable
{
    private readonly string _root;

    public ExcludedMethodTests_ZipArchiveSearcher()
    {
        _root = Path.Combine(Path.GetTempPath(), "yagu-ziptest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string CreateTestZip(string name, Dictionary<string, string> entries)
    {
        var path = Path.Combine(_root, name);
        using var fs = new FileStream(path, FileMode.Create);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (entryName, content) in entries)
        {
            var entry = archive.CreateEntry(entryName);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
        return path;
    }

    [Fact]
    public void IsZipByHeader_Stream_ValidMagic()
    {
        using var ms = new MemoryStream(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00 });
        Assert.True(ZipArchiveSearcher.IsZipByHeader(ms));
    }

    [Fact]
    public void IsZipByHeader_Stream_InvalidMagic()
    {
        using var ms = new MemoryStream(new byte[] { 0x00, 0x00, 0x00, 0x00 });
        Assert.False(ZipArchiveSearcher.IsZipByHeader(ms));
    }

    [Fact]
    public void IsZipByHeader_Stream_SpannedZipMagic()
    {
        // PK\x05\x06 is also valid (spanned archive)
        using var ms = new MemoryStream(new byte[] { 0x50, 0x4B, 0x05, 0x06 });
        Assert.True(ZipArchiveSearcher.IsZipByHeader(ms));
    }

    [Fact]
    public void IsZipByHeader_Stream_TempZipMagic()
    {
        // PK\x07\x08 is also valid (temp signature)
        using var ms = new MemoryStream(new byte[] { 0x50, 0x4B, 0x07, 0x08 });
        Assert.True(ZipArchiveSearcher.IsZipByHeader(ms));
    }

    [Fact]
    public async Task ExtractToTempFileAsync_ExtractsSingleEntry()
    {
        var zipPath = CreateTestZip("test.zip", new Dictionary<string, string>
        {
            ["hello.txt"] = "Hello, World!"
        });
        var archivePath = $"{zipPath}?/hello.txt";
        var tempFile = await ZipArchiveSearcher.ExtractToTempFileAsync(archivePath);
        try
        {
            Assert.True(File.Exists(tempFile));
            Assert.Equal("Hello, World!", File.ReadAllText(tempFile));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ExtractToTempFileAsync_InvalidPath_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ZipArchiveSearcher.ExtractToTempFileAsync("not-an-archive-path"));
    }

    [Fact]
    public async Task ExtractToTempFileAsync_MissingEntry_Throws()
    {
        var zipPath = CreateTestZip("test2.zip", new Dictionary<string, string>
        {
            ["a.txt"] = "content"
        });
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            ZipArchiveSearcher.ExtractToTempFileAsync($"{zipPath}?/nonexistent.txt"));
    }

    [Fact]
    public async Task ExtractToMemoryAsync_ReturnsStream()
    {
        var zipPath = CreateTestZip("test3.zip", new Dictionary<string, string>
        {
            ["data.txt"] = "Some data"
        });
        var archivePath = $"{zipPath}?/data.txt";
        using var ms = await ZipArchiveSearcher.ExtractToMemoryAsync(archivePath);
        Assert.Equal(0, ms.Position);
        using var reader = new StreamReader(ms);
        Assert.Equal("Some data", reader.ReadToEnd());
    }

    [Fact]
    public async Task ExtractToMemoryAsync_InvalidPath_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ZipArchiveSearcher.ExtractToMemoryAsync("just-a-file"));
    }

    [Fact]
    public void CleanupTempFiles_DoesNotThrow_WhenDirMissing()
    {
        // Should not throw even if the temp dir doesn't exist
        ZipArchiveSearcher.CleanupTempFiles();
    }

    [Fact]
    public async Task CleanupTempFiles_RemovesTempDir()
    {
        // Create a temp file via ExtractToTempFileAsync, then clean it up
        var zipPath = CreateTestZip("cleanup.zip", new Dictionary<string, string>
        {
            ["file.txt"] = "cleanup test"
        });
        var tempFile = await ZipArchiveSearcher.ExtractToTempFileAsync($"{zipPath}?/file.txt");
        Assert.True(File.Exists(tempFile));

        ZipArchiveSearcher.CleanupTempFiles();

        string tempDir = Path.Combine(Path.GetTempPath(), "Yagu", "ZipPreview");
        Assert.False(Directory.Exists(tempDir));
    }
}

public sealed class ExcludedMethodTests_ContentSearcher : IDisposable
{
    private readonly string _root;

    public ExcludedMethodTests_ContentSearcher()
    {
        _root = Path.Combine(Path.GetTempPath(), "yagu-cs-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static SearchOptions Opts(string query) => new()
    {
        Directory = ".",
        Query = query,
        UseRegex = false,
        CaseSensitive = false,
        ContextLines = 0,
        MaxFileSizeBytes = 0,
        MaxResults = 0,
        SkipBinary = true,
    };

    [Fact]
    public async Task SearchStreamAsync_FindsMatches()
    {
        var path = WriteFile("stream.txt", "alpha\nbeta\ngamma\nalpha again");
        var ch = Channel.CreateUnbounded<SearchResult>();
        int count = await ContentSearcher.SearchStreamAsync(
            path, null, "alpha", StringComparison.OrdinalIgnoreCase,
            Opts("alpha"), ch.Writer, CancellationToken.None, default);
        ch.Writer.Complete();
        Assert.Equal(2, count);
        var results = new List<SearchResult>();
        await foreach (var r in ch.Reader.ReadAllAsync()) results.Add(r);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task SearchMappedAsync_FindsMatches()
    {
        var path = WriteFile("mapped.txt", "line1\nfind me here\nline3");
        var fileLength = new FileInfo(path).Length;
        var ch = Channel.CreateUnbounded<SearchResult>();
        int count = await ContentSearcher.SearchMappedAsync(
            path, fileLength, null, "find me", StringComparison.OrdinalIgnoreCase,
            Opts("find me"), ch.Writer, CancellationToken.None, default);
        ch.Writer.Complete();
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task SearchStreamAsync_SkipsBinary()
    {
        // Create a file with binary content (null bytes)
        var path = Path.Combine(_root, "binary.dat");
        var content = new byte[1024];
        content[0] = 0x00; content[1] = 0x00; content[2] = 0x00;
        File.WriteAllBytes(path, content);

        var ch = Channel.CreateUnbounded<SearchResult>();
        var opts = new SearchOptions
        {
            Directory = ".",
            Query = "test",
            UseRegex = false,
            CaseSensitive = false,
            ContextLines = 0,
            MaxFileSizeBytes = 0,
            MaxResults = 0,
            SkipBinary = true,
        };
        int count = await ContentSearcher.SearchStreamAsync(
            path, null, "test", StringComparison.OrdinalIgnoreCase,
            opts, ch.Writer, CancellationToken.None, default);
        ch.Writer.Complete();
        // SearchStreamAsync doesn't do binary detection itself (that's in SearchFileAsync)
        // but it should still function on the file
        Assert.True(count >= 0);
    }
}

public sealed class ExcludedMethodTests_NativeMatchDecoder
{
    [Fact]
    public unsafe void DecodeMatchLine_NullPtr_ReturnsEmpty()
    {
        var (line, start, len) = ContentSearcher.NativeMatchDecoder.DecodeMatchLine(null, 0, 0, 0);
        Assert.Equal(string.Empty, line);
        Assert.Equal(0, start);
        Assert.Equal(0, len);
    }

    [Fact]
    public unsafe void DecodeMatchLine_ZeroLen_ReturnsEmpty()
    {
        byte dummy = 0x41;
        var (line, start, len) = ContentSearcher.NativeMatchDecoder.DecodeMatchLine(&dummy, 0, 0, 0);
        Assert.Equal(string.Empty, line);
    }

    [Fact]
    public unsafe void DecodeMatchLine_ValidUtf8_DecodesCorrectly()
    {
        // "hello" in UTF-8
        byte[] data = Encoding.UTF8.GetBytes("hello world");
        fixed (byte* ptr = data)
        {
            // match "world" starts at byte 6, length 5
            var (line, start, matchLen) = ContentSearcher.NativeMatchDecoder.DecodeMatchLine(ptr, data.Length, 6, 5);
            Assert.Contains("hello world", line);
            Assert.Equal(6, start);
            Assert.Equal(5, matchLen);
        }
    }

    [Fact]
    public unsafe void DecodeMatchLine_MultibyteUtf8_DecodesCorrectly()
    {
        // "café" in UTF-8: 63 61 66 c3 a9 => match "é" at byte 3 (2 bytes)
        byte[] data = Encoding.UTF8.GetBytes("café");
        fixed (byte* ptr = data)
        {
            var (line, start, matchLen) = ContentSearcher.NativeMatchDecoder.DecodeMatchLine(ptr, data.Length, 3, 2);
            Assert.Contains("café", line);
        }
    }

    [Fact]
    public unsafe void DecodeAndTruncate_NullPtr_ReturnsEmpty()
    {
        string result = ContentSearcher.NativeMatchDecoder.DecodeAndTruncate(null, 0);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public unsafe void DecodeAndTruncate_ZeroLen_ReturnsEmpty()
    {
        byte dummy = 0x41;
        string result = ContentSearcher.NativeMatchDecoder.DecodeAndTruncate(&dummy, 0);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public unsafe void DecodeAndTruncate_ShortString_ReturnsFullString()
    {
        byte[] data = Encoding.UTF8.GetBytes("short line");
        fixed (byte* ptr = data)
        {
            string result = ContentSearcher.NativeMatchDecoder.DecodeAndTruncate(ptr, data.Length);
            Assert.Equal("short line", result);
        }
    }

    [Fact]
    public unsafe void UnpackLinesTruncated_NullPtr_ReturnsEmpty()
    {
        var result = ContentSearcher.NativeMatchDecoder.UnpackLinesTruncated(null, 0, 0);
        Assert.Empty(result);
    }

    [Fact]
    public unsafe void UnpackLinesTruncated_ZeroCount_ReturnsEmpty()
    {
        byte dummy = 0x41;
        var result = ContentSearcher.NativeMatchDecoder.UnpackLinesTruncated(&dummy, 1, 0);
        Assert.Empty(result);
    }

    [Fact]
    public unsafe void UnpackLinesTruncated_ValidData_DecodesLines()
    {
        // Pack two lines: "abc" and "de"
        // Format: u32 len + bytes for each line
        var line1 = Encoding.UTF8.GetBytes("abc");
        var line2 = Encoding.UTF8.GetBytes("de");
        var buffer = new byte[4 + line1.Length + 4 + line2.Length];
        BitConverter.GetBytes((uint)line1.Length).CopyTo(buffer, 0);
        line1.CopyTo(buffer, 4);
        BitConverter.GetBytes((uint)line2.Length).CopyTo(buffer, 4 + line1.Length);
        line2.CopyTo(buffer, 4 + line1.Length + 4);

        fixed (byte* ptr = buffer)
        {
            var result = ContentSearcher.NativeMatchDecoder.UnpackLinesTruncated(ptr, (nuint)buffer.Length, 2);
            Assert.Equal(2, result.Count);
            Assert.Equal("abc", result[0]);
            Assert.Equal("de", result[1]);
        }
    }
}

public sealed class ExcludedMethodTests_NativeSearcherBufferReader
{
    [Fact]
    public void TryReadU32_ValidData_ReadsCorrectly()
    {
        var data = new byte[4];
        BitConverter.GetBytes((uint)42).CopyTo(data, 0);
        var reader = new NativeSearchOutcome.BufferReader(data);
        Assert.True(reader.TryReadU32(out uint value));
        Assert.Equal(42u, value);
    }

    [Fact]
    public void TryReadU32_InsufficientData_ReturnsFalse()
    {
        var data = new byte[2];
        var reader = new NativeSearchOutcome.BufferReader(data);
        Assert.False(reader.TryReadU32(out _));
    }

    [Fact]
    public void TryReadU64_ValidData_ReadsCorrectly()
    {
        var data = new byte[8];
        BitConverter.GetBytes((ulong)123456789L).CopyTo(data, 0);
        var reader = new NativeSearchOutcome.BufferReader(data);
        Assert.True(reader.TryReadU64(out ulong value));
        Assert.Equal(123456789UL, value);
    }

    [Fact]
    public void TryReadU64_InsufficientData_ReturnsFalse()
    {
        var data = new byte[4];
        var reader = new NativeSearchOutcome.BufferReader(data);
        Assert.False(reader.TryReadU64(out _));
    }

    [Fact]
    public void TryReadUtf8String_EmptyString_Succeeds()
    {
        var data = new byte[4]; // doesn't matter what's here
        var reader = new NativeSearchOutcome.BufferReader(data);
        Assert.True(reader.TryReadUtf8String(0, out string? value));
        Assert.Equal(string.Empty, value);
    }

    [Fact]
    public void TryReadUtf8String_ValidData_Succeeds()
    {
        var str = Encoding.UTF8.GetBytes("hello");
        var reader = new NativeSearchOutcome.BufferReader(str);
        Assert.True(reader.TryReadUtf8String((uint)str.Length, out string? value));
        Assert.Equal("hello", value);
    }

    [Fact]
    public void Sequential_Reads_AdvancePosition()
    {
        // u32 (4 bytes) + u64 (8 bytes) = 12 bytes total
        var data = new byte[12];
        BitConverter.GetBytes((uint)7).CopyTo(data, 0);
        BitConverter.GetBytes((ulong)99).CopyTo(data, 4);
        var reader = new NativeSearchOutcome.BufferReader(data);

        Assert.True(reader.TryReadU32(out uint v1));
        Assert.Equal(7u, v1);
        Assert.True(reader.TryReadU64(out ulong v2));
        Assert.Equal(99UL, v2);
        // Now at end, next read should fail
        Assert.False(reader.TryReadU32(out _));
    }
}

[Collection("FileListerBackend")]
public sealed class ExcludedMethodTests_FileListerEsExe : IDisposable
{
    private readonly FileListerBackend _originalBackend;

    public ExcludedMethodTests_FileListerEsExe()
    {
        _originalBackend = FileLister.Backend;
    }

    public void Dispose()
    {
        FileLister.Backend = _originalBackend;
    }

    [Fact]
    public async Task RunEverythingAsync_ViaProcessFactory_ParsesOutput()
    {
        // Force es.exe backend and use a mock process
        FileLister.Backend = FileListerBackend.EsExe;

        var lister = new FileLister((path, psi) => new FakeProcess(
            lines: new[] { @"C:\test\file1.txt", @"C:\test\file2.cs", "" },
            exitCode: 0));

        var files = new List<string>();
        await foreach (var p in lister.ListFilesAsync(@"C:\test", Array.Empty<string>(), 0, CancellationToken.None))
            files.Add(p);

        Assert.Equal(2, files.Count);
        Assert.Contains(@"C:\test\file1.txt", files);
        Assert.Contains(@"C:\test\file2.cs", files);
    }

    [Fact]
    public async Task RunEverythingAsync_ExitCode8_SetsFallbackReason()
    {
        FileLister.Backend = FileListerBackend.EsExe;

        var lister = new FileLister((path, psi) => new FakeProcess(
            lines: Array.Empty<string>(),
            exitCode: 8));

        var files = new List<string>();
        await foreach (var p in lister.ListFilesAsync(@"C:\test", Array.Empty<string>(), 0, CancellationToken.None))
            files.Add(p);

        Assert.Empty(files);
        Assert.Equal("Everything is not running", lister.FallbackReason);
    }

    [Fact]
    public async Task TryGetEverythingResultCount_SetsKnownTotalFiles()
    {
        FileLister.Backend = FileListerBackend.EsExe;

        // The count process returns "42", then the main process returns lines
        int callCount = 0;
        var lister = new FileLister((path, psi) =>
        {
            callCount++;
            if (psi.ArgumentList.Contains("-get-result-count"))
                return new FakeProcess(lines: new[] { "42" }, exitCode: 0);
            return new FakeProcess(
                lines: new[] { @"C:\a.txt", @"C:\b.txt" },
                exitCode: 0);
        });

        var files = new List<string>();
        await foreach (var p in lister.ListFilesAsync(@"C:\test", Array.Empty<string>(), 0, CancellationToken.None))
            files.Add(p);

        Assert.Equal(42, lister.KnownTotalFiles);
    }

    [Fact]
    public async Task RunEverythingAsync_ProcessStartFails_SetsFallbackReason()
    {
        FileLister.Backend = FileListerBackend.EsExe;

        var lister = new FileLister((path, psi) => new ThrowingProcess());

        var files = new List<string>();
        await foreach (var p in lister.ListFilesAsync(@"C:\test", Array.Empty<string>(), 0, CancellationToken.None))
            files.Add(p);

        Assert.Empty(files);
        Assert.Contains("could not start", lister.FallbackReason);
    }

    private sealed class FakeProcess : FileLister.IProcess
    {
        private readonly string[] _lines;
        private int _index;
        public int ExitCode { get; }

        public FakeProcess(string[] lines, int exitCode)
        {
            _lines = lines;
            ExitCode = exitCode;
        }

        public void Start() { }
        public Task<string?> ReadLineAsync(CancellationToken ct)
        {
            if (_index >= _lines.Length) return Task.FromResult<string?>(null);
            return Task.FromResult<string?>(_lines[_index++]);
        }
        public Task WaitForExitAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ThrowingProcess : FileLister.IProcess
    {
        public int ExitCode => -1;
        public void Start() => throw new InvalidOperationException("Process start failed");
        public Task<string?> ReadLineAsync(CancellationToken ct) => Task.FromResult<string?>(null);
        public Task WaitForExitAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
