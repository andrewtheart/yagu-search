using System.IO.Compression;
using System.Threading.Channels;
using Yagu.Models;
using Yagu.Services;

namespace Yagu.Tests;

public sealed class ZipArchiveSearcherTests : IDisposable
{
    private readonly string _root;

    public ZipArchiveSearcherTests()
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

    [Fact]
    public async Task SearchArchiveStreamAutoAsync_UnknownFormat_ReturnsZeroMatches()
    {
        // A stream with non-zip/non-7z magic → ArchiveKind.Unknown → 0 matches
        using var ms = new MemoryStream(new byte[] { 0xFF, 0xFE, 0x00, 0x01, 0x02, 0x03 });
        var channel = System.Threading.Channels.Channel.CreateUnbounded<SearchResult>();

        var (matchCount, entriesScanned) = await ZipArchiveSearcher.SearchArchiveStreamAutoAsync(
            ms,
            "fake.bin",
            regex: null,
            literal: "test",
            literalComparison: StringComparison.OrdinalIgnoreCase,
            new SearchOptions { Directory = @"C:\test", Query = "test" },
            channel.Writer,
            nestingDepth: 0,
            CancellationToken.None);

        Assert.Equal(0, matchCount);
        Assert.Equal(0, entriesScanned);
    }

    [Fact]
    public async Task SearchArchiveStreamAutoAsync_ValidZip_FindsMatches()
    {
        var zipPath = CreateTestZip("searchable.zip", new Dictionary<string, string>
        {
            ["hello.txt"] = "Hello World\nFoo bar\n",
            ["other.txt"] = "Nothing here",
        });
        using var fs = File.OpenRead(zipPath);
        var channel = System.Threading.Channels.Channel.CreateUnbounded<SearchResult>();

        var (matchCount, entriesScanned) = await ZipArchiveSearcher.SearchArchiveStreamAutoAsync(
            fs,
            zipPath,
            regex: null,
            literal: "Hello",
            literalComparison: StringComparison.OrdinalIgnoreCase,
            new SearchOptions { Directory = _root, Query = "Hello" },
            channel.Writer,
            nestingDepth: 0,
            CancellationToken.None);

        Assert.True(matchCount >= 1);
        Assert.True(entriesScanned >= 1);
    }

    [Fact]
    public async Task ExtractToMemoryAsync_CaseInsensitiveEntryMatch()
    {
        // Tests ArchiveEntryNameEquals case-insensitive branch
        var zipPath = CreateTestZip("case.zip", new Dictionary<string, string>
        {
            ["Dir/File.TXT"] = "content here"
        });
        // Request with different casing — exercises OrdinalIgnoreCase fallback
        var archivePath = $"{zipPath}?/dir/file.txt";
        using var ms = await ZipArchiveSearcher.ExtractToMemoryAsync(archivePath);
        using var reader = new StreamReader(ms);
        Assert.Equal("content here", reader.ReadToEnd());
    }

    [Fact]
    public void Configure_CustomValues_SetsProperties()
    {
        int origDepth = ZipArchiveSearcher.MaxNestingDepth;
        long origSize = ZipArchiveSearcher.MaxEntrySize;
        try
        {
            ZipArchiveSearcher.Configure(maxNestingDepth: 5, maxEntryMB: 64);
            Assert.Equal(5, ZipArchiveSearcher.MaxNestingDepth);
            Assert.Equal(64L * 1024 * 1024, ZipArchiveSearcher.MaxEntrySize);
        }
        finally
        {
            // Restore defaults
            ZipArchiveSearcher.Configure(maxNestingDepth: origDepth, maxEntryMB: (int)(origSize / (1024 * 1024)));
        }
    }

    [Fact]
    public void Configure_ZeroValues_UsesDefaults()
    {
        int origDepth = ZipArchiveSearcher.MaxNestingDepth;
        long origSize = ZipArchiveSearcher.MaxEntrySize;
        try
        {
            ZipArchiveSearcher.Configure(maxNestingDepth: 0, maxEntryMB: 0);
            // Should use defaults, not 0
            Assert.True(ZipArchiveSearcher.MaxNestingDepth > 0);
            Assert.True(ZipArchiveSearcher.MaxEntrySize > 0);
        }
        finally
        {
            ZipArchiveSearcher.Configure(maxNestingDepth: origDepth, maxEntryMB: (int)(origSize / (1024 * 1024)));
        }
    }
}
