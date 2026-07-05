using System.Text;
using Yagu.Services.Ocr;

namespace Yagu.Tests;

public sealed class OcrTextCacheTests : IDisposable
{
    private readonly string _root;
    private readonly string _cacheDir;
    private readonly OcrTextCache _cache;

    public OcrTextCacheTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qg-ocrcache-" + Guid.NewGuid().ToString("N"));
        _cacheDir = Path.Combine(_root, "cache");
        Directory.CreateDirectory(_root);
        _cache = new OcrTextCache(_cacheDir);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private string WriteImage(string name, string content = "fake-image-bytes")
    {
        var p = Path.Combine(_root, name);
        File.WriteAllText(p, content, new UTF8Encoding(false));
        return p;
    }

    [Fact]
    public void SetThenTryGet_RoundTripsText()
    {
        string img = WriteImage("a.png");
        _cache.Set(img, "paddle", "hello\nworld");

        Assert.True(_cache.TryGet(img, "paddle", out string text));
        Assert.Equal("hello\nworld", text);
    }

    [Fact]
    public void TryGet_ReturnsFalseWhenNoEntry()
    {
        string img = WriteImage("b.png");

        Assert.False(_cache.TryGet(img, "paddle", out string text));
        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void TryGet_ReturnsFalseForMissingImageFile()
    {
        string img = Path.Combine(_root, "does-not-exist.png");

        Assert.False(_cache.TryGet(img, "paddle", out _));
    }

    [Fact]
    public void TryGet_IsKeyedByEngineId()
    {
        string img = WriteImage("c.png");
        _cache.Set(img, "paddle", "paddle-text");

        Assert.True(_cache.TryGet(img, "paddle", out _));
        Assert.False(_cache.TryGet(img, "tesseract", out _));
    }

    [Fact]
    public void TryGet_IsStaleAfterFileContentChanges()
    {
        string img = WriteImage("d.png", "original");
        _cache.Set(img, "paddle", "cached-text");
        Assert.True(_cache.TryGet(img, "paddle", out _));

        // Change size + last-write time -> the cached entry must be treated as stale.
        File.WriteAllText(img, "original-but-longer", new UTF8Encoding(false));

        Assert.False(_cache.TryGet(img, "paddle", out _));
    }

    [Fact]
    public void Set_OverwritesPreviousEntry()
    {
        string img = WriteImage("e.png");
        _cache.Set(img, "paddle", "first");
        _cache.Set(img, "paddle", "second");

        Assert.True(_cache.TryGet(img, "paddle", out string text));
        Assert.Equal("second", text);
    }

    [Fact]
    public void Set_DoesNotThrowForMissingImage()
    {
        string img = Path.Combine(_root, "ghost.png");
        var ex = Record.Exception(() => _cache.Set(img, "paddle", "text"));
        Assert.Null(ex);
        Assert.False(_cache.TryGet(img, "paddle", out _));
    }

    [Fact]
    public void GetCacheFilePath_IsDeterministicAndUnderBaseDir()
    {
        string img = WriteImage("f.png");
        string p1 = _cache.GetCacheFilePath(img, "paddle");
        string p2 = _cache.GetCacheFilePath(img, "paddle");

        Assert.Equal(p1, p2);
        Assert.StartsWith(_cacheDir, p1);
        Assert.EndsWith(".txt", p1);
    }

    [Fact]
    public void GetCacheFilePath_EmbedsCurrentProcessId()
    {
        string img = WriteImage("g.png");
        string path = _cache.GetCacheFilePath(img, "paddle");

        Assert.Contains($".p{Environment.ProcessId}.txt", Path.GetFileName(path));
    }

    [Fact]
    public void ExtractProcessId_ParsesTaggedNameAndRejectsUntagged()
    {
        Assert.Equal(1234, OcrTextCache.ExtractProcessId("ABCDEF0123456789.p1234.txt"));
        Assert.Equal(1234, OcrTextCache.ExtractProcessId("ABCDEF0123456789.p1234.txt.tmp5678"));
        Assert.Equal(-1, OcrTextCache.ExtractProcessId("ABCDEF0123456789.txt"));
    }

    [Fact]
    public void Cleanup_RemovesDeadProcessAndUntaggedFiles_KeepsCurrentProcess()
    {
        // A real entry written by this (live) process must survive cleanup so the preview can read it.
        string img = WriteImage("h.png");
        _cache.Set(img, "paddle", "kept-text");
        string current = _cache.GetCacheFilePath(img, "paddle");
        Assert.True(File.Exists(current));

        // Leftovers from a process that is definitely not running, plus a legacy untagged file.
        string deadPid = Path.Combine(_cacheDir, $"DEADBEEFDEADBEEFDEADBEEFDEADBEEF.p{int.MaxValue}.txt");
        string legacy = Path.Combine(_cacheDir, "00112233445566778899AABBCCDDEEFF.txt");
        File.WriteAllText(deadPid, "stale");
        File.WriteAllText(legacy, "stale");

        OcrTextCache.Cleanup(_cacheDir);

        Assert.False(File.Exists(deadPid));
        Assert.False(File.Exists(legacy));
        Assert.True(File.Exists(current));
        Assert.True(_cache.TryGet(img, "paddle", out string text));
        Assert.Equal("kept-text", text);
    }

    [Fact]
    public void DefaultBaseDirectory_IsUnderLocalAppDataYaguOcrCache()
    {
        string dir = OcrTextCache.DefaultBaseDirectory();

        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Yagu", "ocr-cache");
        Assert.Equal(expected, dir);
    }

    [Fact]
    public void TryGet_ReturnsFalse_WhenImagePathIsInvalid()
    {
        // An embedded NUL makes FileInfo throw; TryGet must swallow it and report a miss.
        Assert.False(_cache.TryGet("bad\0path.png", "paddle", out string text));
        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void Set_DoesNotThrow_WhenImagePathIsInvalid()
    {
        var ex = Record.Exception(() => _cache.Set("bad\0path.png", "paddle", "text"));
        Assert.Null(ex);
    }

    [Fact]
    public void Set_DoesNotThrow_WhenBaseDirectoryIsInvalid()
    {
        string img = WriteImage("invalid-basedir.png");
        var cache = new OcrTextCache("bad\0dir");

        // The image exists (so FileInfo succeeds), but creating/writing under the invalid base
        // directory fails — the advisory write must be swallowed.
        var ex = Record.Exception(() => cache.Set(img, "paddle", "text"));
        Assert.Null(ex);
    }

    [Fact]
    public void TryGet_ReturnsFalse_WhenCacheFileCannotBeRead()
    {
        string img = WriteImage("locked-read.png");
        _cache.Set(img, "paddle", "cached-text");
        string cacheFile = _cache.GetCacheFilePath(img, "paddle");
        Assert.True(File.Exists(cacheFile));

        // Hold the cache file open exclusively so the reader inside TryGet hits a sharing violation.
        using (new FileStream(cacheFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(_cache.TryGet(img, "paddle", out string text));
            Assert.Equal(string.Empty, text);
        }
    }

    [Fact]
    public void Cleanup_OnEmptyDirectory_DoesNothing()
    {
        string emptyDir = Path.Combine(_root, "empty-cache");
        Directory.CreateDirectory(emptyDir);

        var ex = Record.Exception(() => OcrTextCache.Cleanup(emptyDir));
        Assert.Null(ex);
    }

    [Fact]
    public void Cleanup_OnMissingDirectory_DoesNothing()
    {
        string missing = Path.Combine(_root, "never-created");

        var ex = Record.Exception(() => OcrTextCache.Cleanup(missing));
        Assert.Null(ex);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void Cleanup_SwallowsDeleteFailures_AndKeepsLockedFile()
    {
        // A stale file tagged with a definitely-dead pid is a deletion target. Holding it open
        // without delete-sharing makes File.Delete throw; Cleanup must log and continue, not throw.
        string deadPid = Path.Combine(_cacheDir, $"FEEDFACEFEEDFACEFEEDFACEFEEDFACE.p{int.MaxValue}.txt");
        Directory.CreateDirectory(_cacheDir);
        File.WriteAllText(deadPid, "stale");

        using (new FileStream(deadPid, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var ex = Record.Exception(() => OcrTextCache.Cleanup(_cacheDir));
            Assert.Null(ex);
            Assert.True(File.Exists(deadPid)); // delete failed, but Cleanup did not throw
        }
    }

    [Fact]
    public void GetLiveYaguProcessIds_IncludesCurrentProcessAndEnumeratesMatches()
    {
        // Enumerate against a process name that is actually running (this test host) so the live
        // enumeration loop executes; the current process id is always present as a safe floor.
        string runningName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;

        var ids = OcrTextCache.GetLiveYaguProcessIds(runningName);

        Assert.Contains(Environment.ProcessId, ids);
        Assert.NotEmpty(ids);
    }
}
