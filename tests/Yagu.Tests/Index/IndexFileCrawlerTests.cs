using System.Collections;
using Yagu.Services.Index;

namespace Yagu.Tests.Index;

public sealed class IndexFileCrawlerTests
{
    private static IndexIngestionPolicy Policy(bool followReparse = false)
        => new(0, null, null, true, followReparse, 0);

    private static IndexCrawlEntry File(string path, long length = 10)
        => new(path, length, FileAttributes.Normal);

    private static IndexCrawlEntry Dir(string path)
        => new(path, 0, FileAttributes.Directory);

    private static IndexCrawlEntry Reparse(string path)
        => new(path, 0, FileAttributes.Directory | FileAttributes.ReparsePoint);

    [Fact]
    public void EnumerateFiles_TraversesNormalDirectories_PrunesStorage_AndIsolatesIoFailures()
    {
        string root = IndexScopeIdentity.NormalizePath(@"C:\root");
        string sub = IndexScopeIdentity.NormalizePath(@"C:\root\sub");
        string denied = IndexScopeIdentity.NormalizePath(@"C:\root\denied");
        string flaky = IndexScopeIdentity.NormalizePath(@"C:\root\flaky");
        string storage = IndexScopeIdentity.NormalizePath(@"C:\root\index-data");
        var fs = new IndexCrawlerFileSystem
        {
            EnumerateEntries = directory => directory switch
            {
                var d when d == root => new[] { File(@"C:\root\a.txt", 5), Dir(sub), Dir(denied), Dir(flaky), Dir(storage) },
                var d when d == sub => new[] { File(@"C:\root\sub\b.txt", 7) },
                var d when d == denied => throw new UnauthorizedAccessException(),
                var d when d == flaky => new ThrowAfterEnumerable(File(@"C:\root\flaky\first.txt")),
                var d when d == storage => throw new InvalidOperationException("storage must be pruned before enumeration"),
                _ => Array.Empty<IndexCrawlEntry>(),
            },
        };

        IndexCrawlEntry[] entries = IndexFileCrawler.EnumerateFiles(root, Policy(), storage, CancellationToken.None, fs).ToArray();
        string[] files = entries.Select(e => e.Path).ToArray();

        Assert.Contains(@"C:\root\a.txt", files);
        Assert.Contains(@"C:\root\sub\b.txt", files);
        Assert.Contains(@"C:\root\flaky\first.txt", files);
        Assert.Equal(3, files.Length);

        // Length/attributes ride the enumeration record — no per-file stat is done in the crawl.
        Assert.Equal(5, entries.Single(e => e.Path == @"C:\root\a.txt").Length);
        Assert.Equal(7, entries.Single(e => e.Path == @"C:\root\sub\b.txt").Length);
        Assert.All(entries, e => Assert.False(e.Attributes.HasFlag(FileAttributes.Directory)));
    }

    [Fact]
    public void EnumerateFiles_ReparseTraversalIsSameVolumeInRootAndCycleSafe()
    {
        string root = IndexScopeIdentity.NormalizePath(@"C:\root");
        string alias = IndexScopeIdentity.NormalizePath(@"C:\root\alias");
        string aliasTarget = IndexScopeIdentity.NormalizePath(@"C:\root\target");
        string outside = IndexScopeIdentity.NormalizePath(@"C:\root\outside-link");
        string otherVolume = IndexScopeIdentity.NormalizePath(@"C:\root\other-volume");
        string cycle = IndexScopeIdentity.NormalizePath(@"C:\root\cycle");
        string unresolved = IndexScopeIdentity.NormalizePath(@"C:\root\unresolved");
        string throwing = IndexScopeIdentity.NormalizePath(@"C:\root\throwing");
        string storage = IndexScopeIdentity.NormalizePath(@"C:\index");
        var reparses = new[] { alias, outside, otherVolume, cycle, unresolved, throwing };
        var fs = new IndexCrawlerFileSystem
        {
            EnumerateEntries = directory => directory switch
            {
                var d when d == root => reparses.Select(Reparse).ToArray(),
                var d when d == alias => new[] { File(@"C:\root\alias\inside.txt") },
                _ => Array.Empty<IndexCrawlEntry>(),
            },
            ResolveDirectoryTarget = path => path switch
            {
                var p when p == alias => aliasTarget,
                var p when p == outside => @"C:\outside",
                var p when p == otherVolume => @"D:\target",
                var p when p == cycle => root,
                var p when p == unresolved => null,
                _ => throw new NotSupportedException(),
            },
        };

        Assert.Empty(IndexFileCrawler.EnumerateFiles(root, Policy(followReparse: false), storage, CancellationToken.None, fs));
        string[] followed = IndexFileCrawler.EnumerateFiles(root, Policy(followReparse: true), storage, CancellationToken.None, fs)
            .Select(e => e.Path).ToArray();
        Assert.Equal(new[] { @"C:\root\alias\inside.txt" }, followed);
    }

    [Fact]
    public void EnumerateFiles_HonorsCancellationAndValidatesArguments()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => IndexFileCrawler.EnumerateFiles(
            @"C:\root", Policy(), @"C:\index", cts.Token).ToArray());
        Assert.Throws<ArgumentException>(() => IndexFileCrawler.EnumerateFiles(
            " ", Policy(), @"C:\index", CancellationToken.None).ToArray());
        Assert.Throws<ArgumentNullException>(() => IndexFileCrawler.EnumerateFiles(
            @"C:\root", null!, @"C:\index", CancellationToken.None).ToArray());
        Assert.Throws<ArgumentNullException>(() => IndexFileCrawler.EnumerateFiles(
            @"C:\root", Policy(), null!, CancellationToken.None).ToArray());
        Assert.Throws<ArgumentException>(() => IndexFileCrawler.EnumerateFiles(
            @"C:\root", Policy(), " ", CancellationToken.None).ToArray());

        // A drive-root exclusion has a trailing separator and prunes the whole root immediately.
        Assert.Empty(IndexFileCrawler.EnumerateFiles(
            @"C:\root", Policy(), @"C:\", CancellationToken.None,
            new IndexCrawlerFileSystem { EnumerateEntries = _ => throw new InvalidOperationException("must be pruned") }));
    }

    [Fact]
    public void EnumerateFiles_RealTree_YieldsLengthAndAttributesMatchingAStat()
    {
        string dir = Path.Combine(Path.GetTempPath(), "yagu-crawl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string a = Path.Combine(dir, "a.txt");
            string bDir = Path.Combine(dir, "sub");
            string b = Path.Combine(bDir, "b.txt");
            Directory.CreateDirectory(bDir);
            System.IO.File.WriteAllText(a, "hello");    // 5 bytes
            System.IO.File.WriteAllText(b, "world!!");   // 7 bytes
            string storage = Path.Combine(dir, "_never");
            Directory.CreateDirectory(storage);
            System.IO.File.WriteAllText(Path.Combine(storage, "skip.txt"), "should be pruned");

            IndexCrawlEntry[] entries = IndexFileCrawler.EnumerateFiles(
                IndexScopeIdentity.NormalizePath(dir),
                Policy(),
                IndexScopeIdentity.NormalizePath(storage),
                CancellationToken.None).ToArray();

            // Exactly the two real files (the storage subtree is pruned); metadata matches a fresh stat.
            Assert.Equal(2, entries.Length);
            foreach (IndexCrawlEntry e in entries)
            {
                var info = new FileInfo(e.Path);
                Assert.Equal(info.Length, e.Length);
                Assert.Equal(info.Attributes, e.Attributes);
                Assert.False(e.Attributes.HasFlag(FileAttributes.Directory));
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class ThrowAfterEnumerable(IndexCrawlEntry first) : IEnumerable<IndexCrawlEntry>
    {
        public IEnumerator<IndexCrawlEntry> GetEnumerator() => new Enumerator(first);
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class Enumerator(IndexCrawlEntry first) : IEnumerator<IndexCrawlEntry>
        {
            private int _state;
            public IndexCrawlEntry Current => first;
            object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                if (_state++ == 0)
                    return true;
                throw new IOException("directory vanished");
            }

            public void Reset() => throw new NotSupportedException();
            public void Dispose() { }
        }
    }

    [Fact]
    public void EnumerateFiles_VanishedDirectory_IsSkipped_AndKeepsBuildCommittable()
    {
        // A live C:\ constantly has temp/MUI locale subdirs appear and vanish during a multi-minute crawl.
        // A directory that no longer exists (DirectoryNotFound on open, or FileNotFound mid-enumeration) has
        // nothing to miss, so it is skipped WITHOUT marking the crawl incomplete — otherwise a whole-drive
        // index build would essentially never commit.
        string root = IndexScopeIdentity.NormalizePath(@"C:\root");
        string good = IndexScopeIdentity.NormalizePath(@"C:\root\good");
        string goneOnOpen = IndexScopeIdentity.NormalizePath(@"C:\root\gone-open");
        string goneMidScan = IndexScopeIdentity.NormalizePath(@"C:\root\gone-mid");
        string storage = IndexScopeIdentity.NormalizePath(@"C:\root\index-data");
        var fs = new IndexCrawlerFileSystem
        {
            EnumerateEntries = directory => directory switch
            {
                var d when d == root => new[] { File(@"C:\root\a.txt", 3), Dir(good), Dir(goneOnOpen), Dir(goneMidScan), Dir(storage) },
                var d when d == good => new[] { File(@"C:\root\good\b.txt", 4) },
                var d when d == goneOnOpen => throw new DirectoryNotFoundException("Could not find a part of the path"),
                var d when d == goneMidScan => new VanishAfterEnumerable(File(@"C:\root\gone-mid\first.txt")),
                _ => Array.Empty<IndexCrawlEntry>(),
            },
        };

        var completion = new IndexCrawlCompletion();
        string[] files = IndexFileCrawler.EnumerateFiles(root, Policy(), storage, CancellationToken.None, fs, completion)
            .Select(e => e.Path).ToArray();

        Assert.Contains(@"C:\root\a.txt", files);
        Assert.Contains(@"C:\root\good\b.txt", files);
        Assert.Contains(@"C:\root\gone-mid\first.txt", files); // the entry read before it vanished still counts
        Assert.DoesNotContain(@"C:\root\gone-open", files);
        Assert.True(completion.IsComplete, "A vanished directory must not make the crawl incomplete.");
        Assert.Null(completion.FailedDirectory);
    }

    [Fact]
    public void EnumerateFiles_GenuineIoFault_MarksCrawlIncomplete()
    {
        // A real directory I/O fault (a disconnected/failing volume, not a benign disappearance) must fail
        // the crawl closed so a partial enumeration never replaces a prior complete index.
        string root = IndexScopeIdentity.NormalizePath(@"C:\root");
        string flaky = IndexScopeIdentity.NormalizePath(@"C:\root\flaky");
        string storage = IndexScopeIdentity.NormalizePath(@"C:\root\index-data");
        var fs = new IndexCrawlerFileSystem
        {
            EnumerateEntries = directory => directory switch
            {
                var d when d == root => new[] { File(@"C:\root\a.txt"), Dir(flaky), Dir(storage) },
                var d when d == flaky => new ThrowAfterEnumerable(File(@"C:\root\flaky\first.txt")),
                _ => Array.Empty<IndexCrawlEntry>(),
            },
        };

        var completion = new IndexCrawlCompletion();
        _ = IndexFileCrawler.EnumerateFiles(root, Policy(), storage, CancellationToken.None, fs, completion).ToArray();

        Assert.False(completion.IsComplete, "A genuine directory I/O fault must mark the crawl incomplete.");
        Assert.Equal(flaky, completion.FailedDirectory);
    }

    [Fact]
    public void EnumerateFiles_AccessDeniedMidScan_IsSkipped_AndKeepsBuildCommittable()
    {
        // An access-denied surfaced mid-enumeration (not at open) is an ordinary skip, not a fault: the
        // subtree is inaccessible but the rest of the root still enumerates and the build stays committable.
        string root = IndexScopeIdentity.NormalizePath(@"C:\root");
        string denied = IndexScopeIdentity.NormalizePath(@"C:\root\denied");
        string storage = IndexScopeIdentity.NormalizePath(@"C:\root\index-data");
        var fs = new IndexCrawlerFileSystem
        {
            EnumerateEntries = directory => directory switch
            {
                var d when d == root => new[] { File(@"C:\root\a.txt"), Dir(denied), Dir(storage) },
                var d when d == denied => new ThrowOnMoveNextEnumerable(File(@"C:\root\denied\first.txt"), new UnauthorizedAccessException()),
                _ => Array.Empty<IndexCrawlEntry>(),
            },
        };

        var completion = new IndexCrawlCompletion();
        string[] files = IndexFileCrawler.EnumerateFiles(root, Policy(), storage, CancellationToken.None, fs, completion)
            .Select(e => e.Path).ToArray();

        Assert.Contains(@"C:\root\a.txt", files);
        Assert.Contains(@"C:\root\denied\first.txt", files); // the entry read before access was denied
        Assert.True(completion.IsComplete, "An access-denied subtree must not make the crawl incomplete.");
        Assert.Null(completion.FailedDirectory);
    }

    [Fact]
    public void EnumerateFiles_IoFaultOnOpen_MarksCrawlIncomplete()
    {
        // A genuine I/O fault when OPENING the enumerator (not a vanished directory) must fail the crawl
        // closed so a partial enumeration never replaces a prior complete index.
        string root = IndexScopeIdentity.NormalizePath(@"C:\root");
        string faulted = IndexScopeIdentity.NormalizePath(@"C:\root\faulted");
        string storage = IndexScopeIdentity.NormalizePath(@"C:\root\index-data");
        var fs = new IndexCrawlerFileSystem
        {
            EnumerateEntries = directory => directory switch
            {
                var d when d == root => new[] { File(@"C:\root\a.txt"), Dir(faulted), Dir(storage) },
                var d when d == faulted => throw new IOException("device I/O error opening directory"),
                _ => Array.Empty<IndexCrawlEntry>(),
            },
        };

        var completion = new IndexCrawlCompletion();
        _ = IndexFileCrawler.EnumerateFiles(root, Policy(), storage, CancellationToken.None, fs, completion).ToArray();

        Assert.False(completion.IsComplete, "A genuine directory-open I/O fault must mark the crawl incomplete.");
        Assert.Equal(faulted, completion.FailedDirectory);
    }

    private sealed class VanishAfterEnumerable(IndexCrawlEntry first) : IEnumerable<IndexCrawlEntry>
    {
        public IEnumerator<IndexCrawlEntry> GetEnumerator() => new Enumerator(first);
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class Enumerator(IndexCrawlEntry first) : IEnumerator<IndexCrawlEntry>
        {
            private int _state;
            public IndexCrawlEntry Current => first;
            object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                if (_state++ == 0)
                    return true;
                throw new FileNotFoundException("Could not find a part of the path");
            }

            public void Reset() => throw new NotSupportedException();
            public void Dispose() { }
        }
    }

    private sealed class ThrowOnMoveNextEnumerable(IndexCrawlEntry first, Exception toThrow) : IEnumerable<IndexCrawlEntry>
    {
        public IEnumerator<IndexCrawlEntry> GetEnumerator() => new Enumerator(first, toThrow);
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class Enumerator(IndexCrawlEntry first, Exception toThrow) : IEnumerator<IndexCrawlEntry>
        {
            private int _state;
            public IndexCrawlEntry Current => first;
            object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                if (_state++ == 0)
                    return true;
                throw toThrow;
            }

            public void Reset() => throw new NotSupportedException();
            public void Dispose() { }
        }
    }
}
