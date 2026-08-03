using System;
using System.IO;
using System.Linq;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for <see cref="ContentIndexRecoverySpool"/> — the Stage-3 disk-backed prune-recovery backstop (plan
/// §5.3). It must record every provisionally-pruned path, replay them ALL back in order on a failure (so
/// nothing pruned is ever lost), delete its file on completion (and on dispose without completion), keep host
/// memory bounded (paths on disk, replayed lazily), and let a startup sweep clean up spools abandoned by a
/// crashed host without touching an in-flight one.
/// </summary>
public sealed class ContentIndexRecoverySpoolTests : IDisposable
{
    private readonly string _dir;

    public ContentIndexRecoverySpoolTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "yagu-spool", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Append_ReturnsSequentialOrdinals_AndCounts()
    {
        using ContentIndexRecoverySpool spool = ContentIndexRecoverySpool.Create(_dir);
        Assert.Equal(0, spool.Append(@"c:\a\one.txt"));
        Assert.Equal(1, spool.Append(@"c:\a\two.txt"));
        Assert.Equal(2, spool.Append(@"c:\a\three.txt"));
        Assert.Equal(3, spool.Count);
        Assert.True(File.Exists(spool.FilePath));
    }

    [Fact]
    public void ReplayAll_ReturnsEveryAppendedPath_InOrder()
    {
        var paths = new[] { @"c:\x\a.txt", @"c:\x\b.log", @"c:\x\deep\c.cs", @"c:\x\d.md" };
        using ContentIndexRecoverySpool spool = ContentIndexRecoverySpool.Create(_dir);
        foreach (string p in paths)
            spool.Append(p);

        Assert.Equal(paths, spool.ReplayAll().ToArray());
    }

    [Fact]
    public void ReplayAll_LargeSpool_RoundTripsEveryPath()
    {
        const int n = 20_000;
        using ContentIndexRecoverySpool spool = ContentIndexRecoverySpool.Create(_dir);
        for (int i = 0; i < n; i++)
            spool.Append($@"c:\corpus\dir{i % 128}\file{i}.txt");

        Assert.Equal(n, spool.Count);
        // Lazily streamed back (bounded memory); every path is present, in order.
        int seen = 0;
        foreach (string path in spool.ReplayAll())
        {
            Assert.Equal($@"c:\corpus\dir{seen % 128}\file{seen}.txt", path);
            seen++;
        }
        Assert.Equal(n, seen);
    }

    [Fact]
    public void ReplayAll_EmptySpool_YieldsNothing()
    {
        using ContentIndexRecoverySpool spool = ContentIndexRecoverySpool.Create(_dir);
        Assert.Empty(spool.ReplayAll());
    }

    [Fact]
    public void Complete_DeletesTheSpoolFile()
    {
        var spool = ContentIndexRecoverySpool.Create(_dir);
        spool.Append(@"c:\a\one.txt");
        string file = spool.FilePath;
        spool.Complete();
        Assert.False(File.Exists(file));
        _ = spool.ReplayAll(); // the closed writer needs no flush
        spool.Complete();
        spool.Dispose();
        spool.Dispose();
    }

    [Fact]
    public void Complete_DeleteFailure_IsBestEffort()
    {
        var spool = ContentIndexRecoverySpool.Create(_dir);
        string file = spool.FilePath;
        spool.DeleteFile = _ => throw new IOException("simulated delete failure");

        spool.Complete();

        Assert.True(File.Exists(file));
        spool.Dispose();
        File.Delete(file);
    }

    [Fact]
    public void Dispose_WithoutComplete_DeletesTheSpoolFile()
    {
        string file;
        using (var spool = ContentIndexRecoverySpool.Create(_dir))
        {
            spool.Append(@"c:\a\one.txt");
            file = spool.FilePath;
        }
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Append_AfterDispose_Throws()
    {
        var spool = ContentIndexRecoverySpool.Create(_dir);
        spool.Dispose();
        Assert.Throws<ObjectDisposedException>(() => spool.Append(@"c:\a\one.txt"));
    }

    [Fact]
    public void SweepAbandoned_DeletesOldSpools_ButKeepsRecentAndActiveOnes()
    {
        // A stale abandoned spool (back-dated well past the max age).
        string stale = Path.Combine(_dir, ContentIndexRecoverySpool.FilePrefix + "stale" + ContentIndexRecoverySpool.FileExtension);
        File.WriteAllText(stale, "c:\\old\\path.txt\n");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-48));

        // A recent spool that must be kept.
        string recent = Path.Combine(_dir, ContentIndexRecoverySpool.FilePrefix + "recent" + ContentIndexRecoverySpool.FileExtension);
        File.WriteAllText(recent, "c:\\new\\path.txt\n");

        // An active (still-open) spool must not be swept even if we ask for age 0 (its handle is locked).
        using var active = ContentIndexRecoverySpool.Create(_dir);
        active.Append(@"c:\active\path.txt");

        int deleted = ContentIndexRecoverySpool.SweepAbandoned(_dir, TimeSpan.FromHours(24));

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(active.FilePath));
    }

    [Fact]
    public void SweepAbandoned_MissingDirectory_ReturnsZero()
    {
        Assert.Equal(0, ContentIndexRecoverySpool.SweepAbandoned(Path.Combine(_dir, "nope"), TimeSpan.FromHours(1)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SweepAbandoned_BlankDirectory_ReturnsZero(string? directory)
        => Assert.Equal(0, ContentIndexRecoverySpool.SweepAbandoned(directory!, TimeSpan.FromHours(1)));

    [Fact]
    public void SweepAbandoned_EnumerationFailure_ReturnsZero()
    {
        int deleted = ContentIndexRecoverySpool.SweepAbandoned(
            _dir,
            TimeSpan.FromHours(1),
            static (_, _) => throw new IOException("simulated enumeration failure"),
            File.GetLastWriteTimeUtc,
            File.Delete);

        Assert.Equal(0, deleted);
    }

    [Fact]
    public void SweepAbandoned_DeleteFailure_LeavesFileAndContinues()
    {
        string stale = Path.Combine(_dir, ContentIndexRecoverySpool.FilePrefix + "locked" + ContentIndexRecoverySpool.FileExtension);
        File.WriteAllText(stale, "path\n");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));

        int deleted = ContentIndexRecoverySpool.SweepAbandoned(
            _dir,
            TimeSpan.FromHours(1),
            Directory.GetFiles,
            File.GetLastWriteTimeUtc,
            _ => throw new IOException("simulated delete failure"));

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(stale));
    }
}
