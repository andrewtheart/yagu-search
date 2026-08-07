using System;
using System.IO;
using System.Text;
using System.Threading;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for <see cref="ContentIndexManager"/> (plan §5/§6.3): crawling a real directory, classifying
/// files, publishing a queryable generation, self-exclusion of the index root, status/delete/clear, and
/// error/cancellation behavior. Runs entirely under a per-test temp sandbox (§9.2).
/// </summary>
public sealed class ContentIndexManagerTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string _corpus;
    private readonly string _indexRoot;
    private readonly IContentIndexPathProvider _paths;

    public ContentIndexManagerTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "yagu-index-mgr", Guid.NewGuid().ToString("N"));
        _corpus = Path.Combine(_sandbox, "corpus");
        _indexRoot = Path.Combine(_corpus, "_index"); // deliberately under the corpus to test self-exclusion
        Directory.CreateDirectory(_corpus);
        _paths = new DefaultContentIndexPathProvider(_indexRoot, _indexRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); } catch { /* best effort */ }
    }

    private void Write(string relative, string text)
    {
        string path = Path.Combine(_corpus, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private void WriteBytes(string relative, byte[] bytes)
    {
        string path = Path.Combine(_corpus, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static IndexIngestionPolicy Policy(long maxBytes = 0, bool includeHidden = true)
        => new(maxBytes, null, null, includeHidden, followReparsePoints: false, maxDepth: 0);

    [Theory]
    [InlineData(IndexStorageHealth.Healthy, true, "root", true, false, false)]
    [InlineData(IndexStorageHealth.SourceMissing, false, "root", true, false, false)]
    [InlineData(IndexStorageHealth.IncompatibleFormat, true, "root", false, true, true)]
    [InlineData(IndexStorageHealth.IncompatibleRepresentation, false, "root", false, true, false)]
    [InlineData(IndexStorageHealth.CorruptOrIncomplete, true, "", false, true, false)]
    public void IndexStorageStat_RepairPredicatesReflectHealthAndRootAvailability(
        IndexStorageHealth health,
        bool rootExists,
        string rootPath,
        bool readable,
        bool needsRepair,
        bool canRepair)
    {
        var stat = new IndexStorageStat(
            "scope", rootPath, 1, 1, 0, DateTimeOffset.UtcNow,
            health, rootExists, Problem: null);

        Assert.Equal(readable, stat.Readable);
        Assert.Equal(needsRepair, stat.NeedsRepair);
        Assert.Equal(canRepair, stat.CanRepair);
    }

    // ─────────────────────────── build + query ───────────────────────────

    [Fact]
    public void BuildScope_CrawlsClassifiesAndPublishesQueryableGeneration()
    {
        Write("a.txt", "the planner produces trigram queries");
        Write(@"sub\b.txt", "another file with the planner keyword");
        Write("c.txt", "nothing relevant in this one");
        WriteBytes("bin.dat", new byte[] { (byte)'x', 0, (byte)'y' }); // binary → skipped

        var manager = new ContentIndexManager(_paths);
        var result = manager.BuildScope(_corpus, Policy());

        Assert.Equal(3, result.Report.IndexedCount);
        Assert.Equal(1, result.Report.SkipCount(IndexSkipReason.Binary));
        Assert.Equal("gen-000001", result.Publish.GenerationId);

        // The published generation is queryable: "planner" matches a.txt and sub\b.txt.
        var store = new ContentIndexStore(_paths, result.ScopeId);
        var generation = store.TryOpenCurrent();
        Assert.NotNull(generation);
        var query = Assert.IsType<TrigramPlan.Eligible>(TrigramQueryPlanner.Plan(
            new EffectiveSearchPattern("planner", isRegex: false, caseSensitive: true, multiline: false, dotAll: false))).Query;
        var candidates = generation!.Postings.EvaluateSet(query);
        Assert.Equal(2, candidates.Count);
    }

    [Fact]
    public void GetActiveIndexBytesForRoot_ReportsBuiltLayersAndZeroWithoutAnIndex()
    {
        var manager = new ContentIndexManager(_paths);
        Assert.Equal(0, manager.GetActiveIndexBytesForRoot(string.Empty));
        Assert.Equal(0, manager.GetActiveIndexBytesForRoot(_corpus));

        Write("a.txt", "the planner produces trigram queries");
        manager.BuildScope(_corpus, Policy());

        Assert.True(manager.GetActiveIndexBytesForRoot(_corpus) > 0);
    }

    [Fact]
    public void BuildScope_WithV3Enabled_ProducesQueryStructuresInTheLiveGeneration()
    {
        Write("a.txt", "the planner produces trigram queries");
        Write("b.txt", "nothing relevant in this one");

        var manager = new ContentIndexManager(_paths) { ProduceV3QueryStructures = true };
        var result = manager.BuildScope(_corpus, Policy());

        // The v3 sidecars ride the staged→live transaction commit into the published generation dir.
        string scopeDir = _paths.GetScopeDirectory(result.ScopeId);
        string[] v3 = Directory.GetFiles(scopeDir, ContentIndexV3Format.PostingsFile, SearchOption.AllDirectories);
        Assert.Single(v3);
        using ContentIndexV3Reader reader = ContentIndexV3Format.TryOpen(Path.GetDirectoryName(v3[0])!)!;
        Assert.NotNull(reader);

        // The v3 postings reproduce the live generation's candidate set exactly.
        var store = new ContentIndexStore(_paths, result.ScopeId);
        var generation = store.TryOpenCurrent()!;
        var query = Assert.IsType<TrigramPlan.Eligible>(TrigramQueryPlanner.Plan(
            new EffectiveSearchPattern("planner", isRegex: false, caseSensitive: true, multiline: false, dotAll: false))).Query;
        Assert.True(generation.Postings.EvaluateSet(query).SetEquals(reader.EvaluateSet(query)));
    }

    [Fact]
    public void BuildScope_WithoutV3_WritesNoQueryStructures()
    {
        Write("a.txt", "content here");
        var manager = new ContentIndexManager(_paths); // ProduceV3QueryStructures defaults false
        var result = manager.BuildScope(_corpus, Policy());
        string scopeDir = _paths.GetScopeDirectory(result.ScopeId);
        Assert.Empty(Directory.GetFiles(scopeDir, ContentIndexV3Format.PostingsFile, SearchOption.AllDirectories));
    }

    [Fact]
    public void BuildScope_ReportsCrawlProgress()
    {
        Write("a.txt", "hello world");
        Write(@"sub\b.txt", "another file");
        Write("c.txt", "third file here");
        WriteBytes("bin.dat", new byte[] { (byte)'x', 0, (byte)'y' }); // binary is still crawled (has a size)

        long expectedBytes =
            new FileInfo(Path.Combine(_corpus, "a.txt")).Length +
            new FileInfo(Path.Combine(_corpus, "sub", "b.txt")).Length +
            new FileInfo(Path.Combine(_corpus, "c.txt")).Length +
            new FileInfo(Path.Combine(_corpus, "bin.dat")).Length;

        var reports = new List<IndexBuildProgress>();
        var manager = new ContentIndexManager(_paths);
        manager.BuildScope(_corpus, Policy(), progress: p => reports.Add(p));

        // At least the final report fires (the periodic one needs thousands of files); it reflects every
        // crawled file that had readable metadata — including the skipped binary — and their total bytes.
        Assert.NotEmpty(reports);
        IndexBuildProgress final = reports[^1];
        Assert.Equal(4, final.FilesCrawled);
        Assert.Equal(expectedBytes, final.BytesCrawled);

        for (int i = 1; i < reports.Count; i++)
        {
            Assert.True(reports[i].BytesCrawled >= reports[i - 1].BytesCrawled, "BytesCrawled must be non-decreasing.");
            Assert.True(reports[i].FilesCrawled >= reports[i - 1].FilesCrawled, "FilesCrawled must be non-decreasing.");
        }
    }

    [Fact]
    public void BuildScope_ReportsContentIoStats_AndPrefixRejectsBinaryWithoutReadingTail()
    {
        // Two indexable text files, plus a large binary whose PNG magic decides it after only the 8 KB
        // sniff. The one-open reader must NOT read the binary's ~1 MB tail: the total content bytes read
        // stays far below the binary's size, and the binary is counted as a prefix rejection.
        Write("a.txt", "the planner produces trigram queries");
        Write("b.txt", "another indexable file with words");

        var bigBinary = new byte[1024 * 1024];
        for (int i = 0; i < bigBinary.Length; i++) bigBinary[i] = 0xAB;
        bigBinary[0] = 0x89; bigBinary[1] = 0x50; bigBinary[2] = 0x4E; bigBinary[3] = 0x47; // PNG
        WriteBytes("image.png", bigBinary);

        var manager = new ContentIndexManager(_paths);
        var result = manager.BuildScope(_corpus, Policy());

        Assert.Equal(2, result.Report.IndexedCount);
        Assert.Equal(1, result.Report.SkipCount(IndexSkipReason.Binary));

        // The binary contributed at most one 8 KB sniff, not its full megabyte.
        long textBytes =
            new FileInfo(Path.Combine(_corpus, "a.txt")).Length +
            new FileInfo(Path.Combine(_corpus, "b.txt")).Length;
        Assert.Equal(1, result.IoStats.PrefixRejectedFiles);
        Assert.Equal(2, result.IoStats.FullyReadFiles);
        Assert.True(result.IoStats.ContentBytesRead < bigBinary.Length,
            $"binary tail must not be read: ContentBytesRead={result.IoStats.ContentBytesRead}");
        Assert.True(result.IoStats.ContentBytesRead <= textBytes + ContentRepresentation.BinarySniffBytes,
            $"expected ~text + one sniff, got {result.IoStats.ContentBytesRead}");
    }

    [Fact]
    public void BuildScope_ParallelReadsCommitDeterministicallyInCrawlOrder()
    {
        for (int i = 0; i < 24; i++)
            Write($"p{i:D2}.txt", $"parallel planner token {i:D2} with distinct content {new string((char)('a' + i % 20), 40)}");

        // Establish the serial oracle before the parallel build creates its self-excluded index subtree.
        var serialPaths = new DefaultContentIndexPathProvider(
            Path.Combine(_sandbox, "serial-index"), Path.Combine(_sandbox, "serial-index"));
        BuildScopeResult serialResult = new ContentIndexManager(serialPaths).BuildScope(
            _corpus, Policy(), buildMemoryBudgetMB: 256, buildParallelism: 1);

        var observingReader = new ConcurrencyObservingReader();
        var parallelManager = new ContentIndexManager(_paths, retainedGenerations: 2, contentReader: observingReader);
        BuildScopeResult parallelResult = parallelManager.BuildScope(
            _corpus, Policy(), buildMemoryBudgetMB: 256, buildParallelism: 4);

        Assert.True(observingReader.MaxConcurrency >= 2,
            $"Expected concurrent reads, observed {observingReader.MaxConcurrency}.");
        Assert.Equal(serialResult.Report.IndexedCount, parallelResult.Report.IndexedCount);

        ContentIndexGeneration serial = new ContentIndexStore(serialPaths, serialResult.ScopeId).TryOpenCurrent()!;
        ContentIndexGeneration parallel = new ContentIndexStore(_paths, parallelResult.ScopeId).TryOpenCurrent()!;
        Assert.Equal(
            serial.Aliases.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            parallel.Aliases.OrderBy(pair => pair.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void BuildScope_ParallelReadFailure_KeepsPriorIndexUnchanged()
    {
        Write("stable.txt", "stable planner content");
        BuildScopeResult prior = new ContentIndexManager(_paths).BuildScope(_corpus, Policy());
        Write("boom.txt", "new content that must never be partially published");

        var failing = new ContentIndexManager(
            _paths, retainedGenerations: 2, contentReader: new ThrowingParallelReader("boom.txt"));
        Assert.Throws<InvalidDataException>(() => failing.BuildScope(
            _corpus, Policy(), buildMemoryBudgetMB: 256, buildParallelism: 4));

        ContentIndexGeneration current = new ContentIndexStore(_paths, prior.ScopeId).TryOpenCurrent()!;
        Assert.True(current.TryGetAlias(IndexScopeIdentity.NormalizePath(Path.Combine(_corpus, "stable.txt")), out _, out _));
        Assert.False(current.TryGetAlias(IndexScopeIdentity.NormalizePath(Path.Combine(_corpus, "boom.txt")), out _, out _));
    }

    [Fact]
    public void BuildScope_ParallelCancellation_KeepsPriorIndexUnchanged()
    {
        Write("stable.txt", "stable planner content");
        BuildScopeResult prior = new ContentIndexManager(_paths).BuildScope(_corpus, Policy());
        Write("cancel.txt", "new content that must never be partially published");
        using var cts = new CancellationTokenSource();

        var canceling = new ContentIndexManager(
            _paths, retainedGenerations: 2, contentReader: new CancelingParallelReader("cancel.txt", cts));
        Assert.ThrowsAny<OperationCanceledException>(() => canceling.BuildScope(
            _corpus, Policy(), cts.Token, buildMemoryBudgetMB: 256, buildParallelism: 4));

        ContentIndexGeneration current = new ContentIndexStore(_paths, prior.ScopeId).TryOpenCurrent()!;
        Assert.True(current.TryGetAlias(IndexScopeIdentity.NormalizePath(Path.Combine(_corpus, "stable.txt")), out _, out _));
        Assert.False(current.TryGetAlias(IndexScopeIdentity.NormalizePath(Path.Combine(_corpus, "cancel.txt")), out _, out _));
    }

    private sealed class ConcurrencyObservingReader : IIndexFileContentReader
    {
        private int _active;
        private int _max;
        public int MaxConcurrency => Volatile.Read(ref _max);

        public IndexFileReadResult Read(
            string path,
            long expectedLength,
            IndexIngestionPolicy policy,
            CancellationToken cancellationToken)
        {
            int active = Interlocked.Increment(ref _active);
            int observed;
            while (active > (observed = Volatile.Read(ref _max))
                   && Interlocked.CompareExchange(ref _max, active, observed) != observed)
            {
            }
            try
            {
                // Let at least one sibling lane enter. Sequential code times out quickly and fails the
                // MaxConcurrency assertion instead of hanging the suite.
                SpinWait.SpinUntil(() => Volatile.Read(ref _max) >= 2, TimeSpan.FromSeconds(1));
                return new IndexFileContentReader().Read(path, expectedLength, policy, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class ThrowingParallelReader(string fileName) : IIndexFileContentReader
    {
        public IndexFileReadResult Read(
            string path,
            long expectedLength,
            IndexIngestionPolicy policy,
            CancellationToken cancellationToken)
        {
            if (string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("injected parallel read failure");
            return new IndexFileContentReader().Read(path, expectedLength, policy, cancellationToken);
        }
    }

    private sealed class CancelingParallelReader(string fileName, CancellationTokenSource cancellation) : IIndexFileContentReader
    {
        public IndexFileReadResult Read(
            string path,
            long expectedLength,
            IndexIngestionPolicy policy,
            CancellationToken cancellationToken)
        {
            if (string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase))
                cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return new IndexFileContentReader().Read(path, expectedLength, policy, cancellationToken);
        }
    }

    [Fact]
    public void BuildScope_FilesVanishingDuringCrawl_AreSkippedNotFatal()
    {
        // Regression for the real-world failure of indexing a live volume (e.g. C:\ with .git objects
        // being packed): a file yielded by the crawl is deleted before its metadata (FileInfo.Length /
        // .Attributes) or content (File.ReadAllBytes) is read, so those throw FileNotFoundException. A
        // single vanished file must be skipped, never abort the whole build. When the bug is present the
        // uncaught exception escapes BuildScope and this test throws; when fixed it never does.
        const int total = 1500;
        const int keep = 500; // files [0,keep) are never deleted -> guaranteed survivors
        for (int i = 0; i < total; i++)
            Write($"f{i:D5}.txt", $"file number {i} contains the planner keyword plus filler text for trigrams");

        using var startDelete = new ManualResetEventSlim(false);
        var deleter = System.Threading.Tasks.Task.Run(() =>
        {
            startDelete.Wait();
            for (int i = keep; i < total; i++)
            {
                try { File.Delete(Path.Combine(_corpus, $"f{i:D5}.txt")); }
                catch { /* already gone / momentarily locked — fine */ }
            }
        });

        var manager = new ContentIndexManager(_paths);
        startDelete.Set(); // let deletion race the crawl

        // Must NOT throw despite files vanishing mid-crawl.
        var result = manager.BuildScope(_corpus, Policy());
        deleter.Wait();

        var generation = new ContentIndexStore(_paths, result.ScopeId).TryOpenCurrent();
        Assert.NotNull(generation);
        // The guaranteed survivors were indexed; the vanished files were skipped, not fatal.
        Assert.True(generation!.Manifest.ContentCount >= 1,
            $"Expected surviving files to be indexed, got {generation.Manifest.ContentCount}.");
    }

    [Fact]
    public void BuildScope_LargeCorpus_SpillsToDiskInBatches_AndStaysFullyQueryable()
    {
        // A tiny injected build-memory budget forces the crawl to spill in small batches: the first batch
        // publishes the base and later batches become delta segments, bounding peak RAM to one batch. The
        // whole corpus must remain findable through the layered (base + segments) index — no file is lost
        // across a spill boundary.
        const int total = 700; // > 256-doc floored batch => at least 3 batches (base + >=2 segments)
        for (int i = 0; i < total; i++)
            Write($"f{i:D4}.txt", $"file number {i} contains the planner keyword and unique token tok{i:D4} here");

        var manager = new ContentIndexManager(_paths);
        var result = manager.BuildScope(_corpus, Policy(), buildMemoryBudgetMB: 1);

        var store = new ContentIndexStore(_paths, result.ScopeId);
        // It actually paged: a base plus at least two appended delta segments.
        Assert.True(store.ActiveSegmentCount() >= 2,
            $"Expected the build to spill into >=2 delta segments, got {store.ActiveSegmentCount()}.");

        // Every file remains queryable across the base + segments. "planner" appears in every doc, and each
        // per-file unique token must resolve to exactly its one file — proving no batch was dropped.
        var handle = store.TryOpenLayered();
        Assert.NotNull(handle);

        long totalIndexed = handle!.Base.Manifest.ContentCount;
        foreach (var seg in handle.Segments)
            totalIndexed += seg.Added.Manifest.ContentCount;
        Assert.Equal(total, totalIndexed);

        // Spot-check unique tokens from different batches resolve via the layered session.
        foreach (int i in new[] { 0, 300, 699 })
        {
            var query = Assert.IsType<TrigramPlan.Eligible>(TrigramQueryPlanner.Plan(
                new EffectiveSearchPattern($"tok{i:D4}", isRegex: false, caseSensitive: true, multiline: false, dotAll: false))).Query;
            var segDirties = new DirtyContentSet[handle.Segments.Count];
            for (int k = 0; k < segDirties.Length; k++) segDirties[k] = new DirtyContentSet();
            var session = LayeredContentIndexQuerySession.Begin(
                handle.Base, handle.Segments, query, new DirtyContentSet(), segDirties);
            Assert.True(session.CandidateCount >= 1, $"Unique token tok{i:D4} was not found in the paged index.");
        }
    }

    // ─────────────────── compact a fresh but over-segmented scope (memory guard) ───────────────────

    [Fact]
    public void CompactScopeIfOverSegmented_CollapsesManySegmentsIntoSingleBase()
    {
        // Force a paged build (base + >=2 delta segments) — the shape a "fresh" but fragmented drive index
        // has after many incremental refreshes. Without compaction every query loads base + N segments into
        // RAM (the source of the multi-GB working set observed on a large drive).
        const int total = 700;
        for (int i = 0; i < total; i++)
            Write($"f{i:D4}.txt", $"file number {i} contains the planner keyword and unique token tok{i:D4} here");

        var manager = new ContentIndexManager(_paths);
        manager.BuildScope(_corpus, Policy(), buildMemoryBudgetMB: 1);

        var store = new ContentIndexStore(_paths, ContentIndexManager.ScopeIdForRoot(_corpus));
        Assert.True(store.ActiveSegmentCount() >= 2, $"Setup expected >=2 segments, got {store.ActiveSegmentCount()}.");

        // A low max-segments budget makes the fresh index over-segmented → it folds into a single base.
        var settings = new Yagu.Services.AppSettings { IndexMaxDeltaSegments = 1, IndexCompactionThresholdMB = 256 };
        bool compacted = manager.CompactScopeIfOverSegmented(_corpus, Policy(), settings, DateTimeOffset.UtcNow);

        Assert.True(compacted);
        Assert.Equal(0, store.ActiveSegmentCount()); // folded into one base, no segments remain

        // The compacted base is still fully queryable: unique tokens from former segments resolve.
        var generation = store.TryOpenCurrent();
        Assert.NotNull(generation);
        foreach (int i in new[] { 0, 300, 699 })
        {
            var query = Assert.IsType<TrigramPlan.Eligible>(TrigramQueryPlanner.Plan(
                new EffectiveSearchPattern($"tok{i:D4}", isRegex: false, caseSensitive: true, multiline: false, dotAll: false))).Query;
            Assert.True(generation!.Postings.EvaluateSet(query).Count >= 1, $"tok{i:D4} lost after compaction.");
        }
    }

    [Fact]
    public void CompactScopeIfOverSegmented_NotOverSegmented_ReturnsFalse_AndLeavesIndexAsIs()
    {
        Write("a.txt", "the planner produces trigram queries");
        var manager = new ContentIndexManager(_paths);
        manager.BuildScope(_corpus, Policy()); // single base, zero segments

        var store = new ContentIndexStore(_paths, ContentIndexManager.ScopeIdForRoot(_corpus));
        Assert.Equal(0, store.ActiveSegmentCount());

        var settings = new Yagu.Services.AppSettings { IndexMaxDeltaSegments = 1, IndexCompactionThresholdMB = 256 };
        Assert.False(manager.CompactScopeIfOverSegmented(_corpus, Policy(), settings, DateTimeOffset.UtcNow));
        Assert.Equal(0, store.ActiveSegmentCount());
    }

    [Fact]
    public void CompactScopeIfOverSegmented_QuietScope_CoalescesSmallSegmentsWithoutFoldingBase()
    {
        Write("base.txt", "the planner base document");
        var manager = new ContentIndexManager(_paths);
        manager.BuildScope(_corpus, Policy());

        string scopeId = ContentIndexManager.ScopeIdForRoot(_corpus);
        var store = new ContentIndexStore(_paths, scopeId, retainedGenerations: 4);
        for (int i = 0; i < 9; i++)
        {
            var builder = new ContentIndexDeltaSegmentBuilder(Policy());
            builder.AddChangedDocument(
                Path.Combine(_corpus, $"small-{i}.txt"),
                Encoding.UTF8.GetBytes($"small planner update {i}"));
            store.PublishSegmentFast(builder.Build(
                scopeId,
                Path.GetPathRoot(_corpus)!,
                IndexScopeIdentity.NormalizePath(_corpus),
                new UsnCheckpoint(1, 200 + i),
                DateTimeOffset.UtcNow.AddSeconds(i)));
        }
        Assert.Equal(9, store.ActiveSegmentCount());

        var settings = new Yagu.Services.AppSettings
        {
            IndexMaxDeltaSegments = 8,
            IndexCompactionThresholdMB = 8192,
        };
        bool maintained = manager.CompactScopeIfOverSegmented(
            _corpus, Policy(), settings, DateTimeOffset.UtcNow);

        Assert.True(maintained);
        Assert.Equal(1, store.ActiveSegmentCount());
        Assert.Equal(1, store.TryOpenLayered()!.Segments.Count);
        Assert.NotNull(store.TryOpenCurrent()); // the original base was not folded/replaced
    }

    [Fact]
    public void CompactScopeIfOverSegmented_SkipsWhenIndexExceedsSizeCap_LeavesSegmented()
    {
        // A large-ish over-segmented index: folding it in-process would spike memory. The size cap must skip
        // the automatic compaction and leave the segments in place; a 0 cap then compacts the same index.
        const int total = 700;
        for (int i = 0; i < total; i++)
        {
            // A wide, varied body maximizes distinct trigrams PER DOC (content.bin stores each doc's set with
            // no cross-doc dedup), so the on-disk index reliably clears the 1 MB cap.
            var body = new StringBuilder();
            for (int g = 0; g < 40; g++)
                body.Append(Guid.NewGuid().ToString("N"));
            Write($"f{i:D4}.txt", body.ToString());
        }

        var manager = new ContentIndexManager(_paths);
        manager.BuildScope(_corpus, Policy(), buildMemoryBudgetMB: 1);

        var store = new ContentIndexStore(_paths, ContentIndexManager.ScopeIdForRoot(_corpus));
        int segmentsBefore = store.ActiveSegmentCount();
        Assert.True(segmentsBefore >= 2, $"Setup expected >=2 segments, got {segmentsBefore}.");
        Assert.True(store.TotalActiveIndexBytes() > 1L * 1024 * 1024, "Setup expected a >1 MB index for the cap to bite.");

        Assert.True(store.TryGetCurrentLayerDirectories(out string? baseBefore, out _));

        // 1 MB cap, index is larger → a full base fold is forbidden. Bounded small-run coalescing may
        // still reduce the segment count because it never opens the base or exceeds its independent cap.
        var capped = new IndexMaintenanceSettings { MaxDeltaSegments = 1, CompactionThresholdMB = 1, MaxAutoCompactionSizeMB = 1 };
        bool boundedMaintenance = manager.CompactScopeIfOverSegmented(_corpus, Policy(), capped, DateTimeOffset.UtcNow);
        Assert.Equal(store.ActiveSegmentCount() < segmentsBefore, boundedMaintenance);
        Assert.True(store.ActiveSegmentCount() > 0); // not folded into a new base
        Assert.True(store.TryGetCurrentLayerDirectories(out string? baseAfter, out _));
        Assert.Equal(baseBefore, baseAfter);

        // 0 = no cap → the same index DOES fold into a single base.
        var uncapped = new IndexMaintenanceSettings { MaxDeltaSegments = 1, CompactionThresholdMB = 1, MaxAutoCompactionSizeMB = 0 };
        Assert.True(manager.CompactScopeIfOverSegmented(_corpus, Policy(), uncapped, DateTimeOffset.UtcNow));
        Assert.Equal(0, store.ActiveSegmentCount());
    }

    // ─────────────────────────── disk-space stop guard (plan §11.2) ───────────────────────────

    [Fact]
    public void BuildScope_DiskAtLimit_StopsImmediately_WithNoPartialIndex()
    {
        Write("a.txt", "the planner produces trigram queries");
        Write("b.txt", "another file with the planner keyword");

        var manager = new ContentIndexManager(_paths);
        // Probe always reports 95% used; the pre-crawl check trips before anything is written.
        var ex = Assert.Throws<IndexDiskFullException>(() => manager.BuildScope(
            _corpus, Policy(), maxDiskUsagePercent: 90, diskUsedPercentProbe: _ => 95.0));

        Assert.Equal(90, ex.ThresholdPercent);
        Assert.Equal(95.0, ex.UsedPercent, 3);
        // Nothing was published — no partial index exists for the scope.
        Assert.Null(new ContentIndexStore(_paths, ContentIndexManager.ScopeIdForRoot(_corpus)).TryOpenCurrent());
    }

    [Fact]
    public void BuildScope_DiskFillsMidBuild_DiscardsStagingAndLeavesNoPartialIndex()
    {
        // 700 files with a tiny budget spill in private staging batches. The probe reports OK for the first
        // flush then "full" on the next flush. The public scope remains unchanged until a complete commit,
        // so a failed first build leaves no partial index visible.
        const int total = 700;
        for (int i = 0; i < total; i++)
            Write($"f{i:D4}.txt", $"file number {i} contains the planner keyword and unique token tok{i:D4} here");

        int calls = 0;
        double? Probe(string _) => ++calls >= 3 ? 95.0 : 50.0; // pre-crawl + 1st flush OK; 2nd flush stops it

        var manager = new ContentIndexManager(_paths);
        Assert.Throws<IndexDiskFullException>(() => manager.BuildScope(
            _corpus, Policy(), buildMemoryBudgetMB: 1, maxDiskUsagePercent: 90, diskUsedPercentProbe: Probe));

        // The private flushed base is discarded; no live pointer was flipped.
        var generation = new ContentIndexStore(_paths, ContentIndexManager.ScopeIdForRoot(_corpus)).TryOpenCurrent();
        Assert.Null(generation);
        Assert.Empty(Directory.GetDirectories(_indexRoot, ".build-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void BuildScope_FailedRebuild_PreservesThePriorCompleteIndex()
    {
        Write("old.txt", "old planner content");
        var manager = new ContentIndexManager(_paths);
        manager.BuildScope(_corpus, Policy());
        ContentIndexGeneration oldGeneration = Assert.IsType<ContentIndexGeneration>(
            new ContentIndexStore(_paths, ContentIndexManager.ScopeIdForRoot(_corpus)).TryOpenCurrent());
        Assert.Equal(1, oldGeneration.Manifest.ContentCount);

        for (int i = 0; i < 700; i++)
            Write($"new{i:D4}.txt", $"new content {i} for a staged rebuild");
        int calls = 0;
        double? Probe(string _) => ++calls >= 3 ? 95.0 : 50.0;

        Assert.Throws<IndexDiskFullException>(() => manager.BuildScope(
            _corpus, Policy(), buildMemoryBudgetMB: 1, maxDiskUsagePercent: 90, diskUsedPercentProbe: Probe));

        ContentIndexGeneration after = Assert.IsType<ContentIndexGeneration>(
            new ContentIndexStore(_paths, ContentIndexManager.ScopeIdForRoot(_corpus)).TryOpenCurrent());
        Assert.Equal(1, after.Manifest.ContentCount);
        Assert.True(after.TryGetAlias(IndexScopeIdentity.NormalizePath(Path.Combine(_corpus, "old.txt")), out _, out _));
    }

    [Fact]
    public void BuildScope_DiskUnderLimit_BuildsNormally()
    {
        Write("a.txt", "the planner produces trigram queries");
        Write("b.txt", "another file with the planner keyword");

        var manager = new ContentIndexManager(_paths);
        // 50% used is well under the 90% limit → the guard never trips.
        var result = manager.BuildScope(_corpus, Policy(), maxDiskUsagePercent: 90, diskUsedPercentProbe: _ => 50.0);
        Assert.Equal(2, result.Report.IndexedCount);
    }

    [Fact]
    public void BuildScope_DiskGuardDisabled_IgnoresProbe()
    {
        Write("a.txt", "the planner produces trigram queries");

        var manager = new ContentIndexManager(_paths);
        // maxDiskUsagePercent = 0 disables the guard entirely; the probe (even at 100%) is never consulted.
        bool probed = false;
        var result = manager.BuildScope(_corpus, Policy(), maxDiskUsagePercent: 0, diskUsedPercentProbe: _ => { probed = true; return 100.0; });
        Assert.False(probed);
        Assert.Equal(1, result.Report.IndexedCount);
    }

    [Fact]
    public void GetStorageStats_ReportsSizeDocsAndSegments_ForAPagedIndex()
    {
        const int total = 700;
        for (int i = 0; i < total; i++)
            Write($"f{i:D4}.txt", $"file number {i} contains the planner keyword and unique token tok{i:D4} here");

        var manager = new ContentIndexManager(_paths);
        manager.BuildScope(_corpus, Policy(), buildMemoryBudgetMB: 1);

        IndexStorageSummary summary = manager.GetStorageStats();

        Assert.Single(summary.Indexes);
        var idx = summary.Indexes[0];
        Assert.True(idx.Readable);
        Assert.Equal(IndexStorageHealth.Healthy, idx.Health);
        Assert.True(idx.RootExists);
        Assert.True(idx.SizeBytes > 0, "index should occupy on-disk bytes");
        Assert.Equal(total, idx.DocumentCount);        // base + all segments, counted from manifests only
        Assert.True(idx.SegmentCount >= 2, $"paged build should have >=2 segments, got {idx.SegmentCount}");
        Assert.NotNull(idx.BuiltUtc);
        Assert.NotNull(idx.CreatedUtc);
        Assert.True(idx.BuiltUtc >= idx.CreatedUtc);
        Assert.Null(idx.LastIncrementalUpdateUtc);
        Assert.Equal(IndexScopeIdentity.NormalizePath(_corpus), idx.RootPath);

        // Totals match the single index.
        Assert.Equal(idx.SizeBytes, summary.TotalSizeBytes);
        Assert.Equal(total, summary.TotalDocuments);
    }

    [Fact]
    public void GetStorageStats_LegacyRepresentation_RecoversRootAndOffersRepair()
    {
        Write("a.txt", "the planner produces trigram queries");
        var manager = new ContentIndexManager(_paths);
        BuildScopeResult built = manager.BuildScope(_corpus, Policy());
        string manifestPath = Path.Combine(
            _paths.GetScopeDirectory(built.ScopeId), "generations", built.Publish.GenerationId,
            ContentIndexGenerationSerializer.ManifestFile);
        ContentIndexGeneration current = Assert.IsType<ContentIndexGeneration>(
            new ContentIndexStore(_paths, built.ScopeId).TryOpenCurrent());
        IndexManifest legacy = current.Manifest with
        {
            ContentRepresentationVersion = ContentRepresentation.Version - 1,
        };
        ChecksummedFile.Write(manifestPath, Encoding.UTF8.GetBytes(legacy.Serialize()));

        IndexStorageStat stat = Assert.Single(manager.GetStorageStats().Indexes);

        Assert.Equal(IndexStorageHealth.IncompatibleRepresentation, stat.Health);
        Assert.Equal(IndexScopeIdentity.NormalizePath(_corpus), stat.RootPath);
        Assert.True(stat.RootExists);
        Assert.True(stat.NeedsRepair);
        Assert.True(stat.CanRepair);
        Assert.Contains("requires", stat.Problem);
    }

    [Fact]
    public void GetStorageStats_IgnoresAbandonedBuildWorkspaceNames()
    {
        Directory.CreateDirectory(Path.Combine(_indexRoot, ".build-abandoned"));
        Directory.CreateDirectory(Path.Combine(_indexRoot, "not-a-scope"));

        IndexStorageSummary summary = new ContentIndexManager(_paths).GetStorageStats();

        Assert.Empty(summary.Indexes);
    }

    [Fact]
    public void ResolveBestAvailableIndexRoot_UsesRegisteredParent_NotDuplicateExactChild()
    {
        string child = Path.Combine(_corpus, "src");
        Directory.CreateDirectory(child);
        Write("src/child.txt", "planner child content");
        var manager = new ContentIndexManager(_paths);
        manager.BuildScope(_corpus, Policy());
        manager.BuildScope(child, Policy()); // Simulate pre-policy duplicate scope already on disk.

        string selected = manager.ResolveBestAvailableIndexRoot(child, new[] { _corpus });

        Assert.Equal(IndexScopeIdentity.NormalizePath(_corpus), selected);
    }

    [Fact]
    public void ResolveBestAvailableIndexRoot_FallsBackToExactChild_WhenParentHasNoUsableIndex()
    {
        string child = Path.Combine(_corpus, "src");
        Directory.CreateDirectory(child);
        Write("src/child.txt", "planner child content");
        var manager = new ContentIndexManager(_paths);
        manager.BuildScope(child, Policy());

        string selected = manager.ResolveBestAvailableIndexRoot(child, new[] { _corpus });

        Assert.Equal(IndexScopeIdentity.NormalizePath(child), selected);
    }

    [Fact]
    public void GetStorageStats_NoIndexes_ReturnsEmptySummary()
    {
        var summary = new ContentIndexManager(_paths).GetStorageStats();
        Assert.Empty(summary.Indexes);
        Assert.Equal(0, summary.TotalSizeBytes);
        Assert.Equal(0, summary.TotalDocuments);
    }

    [Fact]
    public void BuildScope_SelfExcludesIndexRoot()
    {
        Write("real.txt", "planner content here");
        // A file physically under the index storage root must never be indexed.
        Directory.CreateDirectory(_indexRoot);
        File.WriteAllText(Path.Combine(_indexRoot, "planted.txt"), "planner content planted inside the index");

        var manager = new ContentIndexManager(_paths);
        var result = manager.BuildScope(_corpus, Policy());

        // Only the one real file was indexed (the planted file under _index is excluded).
        Assert.Equal(1, result.Report.IndexedCount);
    }

    [Fact]
    public void BuildScope_HonorsHiddenPolicy()
    {
        Write("visible.txt", "planner one");
        Write("secret.txt", "planner two");
        File.SetAttributes(Path.Combine(_corpus, "secret.txt"), FileAttributes.Hidden);

        var manager = new ContentIndexManager(_paths);
        var result = manager.BuildScope(_corpus, Policy(includeHidden: false));

        Assert.Equal(1, result.Report.IndexedCount);
        Assert.Equal(1, result.Report.SkipCount(IndexSkipReason.Hidden));
    }

    [Fact]
    public void BuildScope_OverSizeCap_SkippedWithoutReading()
    {
        Write("big.txt", new string('x', 500));
        var manager = new ContentIndexManager(_paths);
        var result = manager.BuildScope(_corpus, Policy(maxBytes: 100));
        Assert.Equal(0, result.Report.IndexedCount);
        Assert.Equal(1, result.Report.SkipCount(IndexSkipReason.OverSizeCap));
    }

    // ─────────────────────────── status / delete / clear ───────────────────────────

    [Fact]
    public void GetStatus_BeforeAndAfterBuild()
    {
        var manager = new ContentIndexManager(_paths);
        string scopeId = ContentIndexManager.ScopeIdForRoot(_corpus);
        Assert.False(manager.GetStatus(scopeId).Exists);

        Write("a.txt", "planner content");
        manager.BuildScope(_corpus, Policy());

        var status = manager.GetStatusForRoot(_corpus);
        Assert.True(status.Exists);
        Assert.NotNull(status.Manifest);
        Assert.Equal(scopeId, status.Manifest!.ScopeId);
        Assert.Contains("indexed", status.Summary);
    }

    [Fact]
    public void MetadataStatus_DoesNotReadContentButValidationDetectsCorruption()
    {
        Write("a.txt", "planner content");
        var manager = new ContentIndexManager(_paths);
        BuildScopeResult result = manager.BuildScope(_corpus, Policy());
        var store = new ContentIndexStore(_paths, result.ScopeId);
        ContentIndexGeneration generation = Assert.IsType<ContentIndexGeneration>(store.TryOpenCurrent(out string? generationDir));

        string contentPath = Path.Combine(generationDir!, ContentIndexGenerationSerializer.ContentFile);
        File.WriteAllBytes(contentPath, new byte[] { 1, 2, 3, 4 });

        IndexMetadataStatus metadata = manager.GetMetadataStatusForRoot(_corpus);
        Assert.True(metadata.Exists);
        Assert.True(metadata.MetadataReadable);
        Assert.Equal(generation.Manifest.ContentCount, metadata.DocumentCount);

        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        IndexValidationResult validation = manager.ValidateScopeUnderLease(mutation, _corpus);
        Assert.False(validation.Valid);
        Assert.Contains("corrupt", validation.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeleteScope_RemovesOneScope()
    {
        Write("a.txt", "planner content");
        var manager = new ContentIndexManager(_paths);
        var result = manager.BuildScope(_corpus, Policy());

        Assert.True(manager.DeleteScope(result.ScopeId));
        Assert.False(manager.GetStatus(result.ScopeId).Exists);
        Assert.False(manager.DeleteScope(result.ScopeId)); // already gone
    }

    [Fact]
    public void ClearAll_RemovesEveryScope()
    {
        Write("a.txt", "planner content");
        var manager = new ContentIndexManager(_paths);
        manager.BuildScope(_corpus, Policy());

        // A second scope under a different corpus root.
        string corpus2 = Path.Combine(_sandbox, "corpus2");
        Directory.CreateDirectory(corpus2);
        File.WriteAllText(Path.Combine(corpus2, "b.txt"), "planner content two");
        manager.BuildScope(corpus2, Policy());

        int removed = manager.ClearAll();
        Assert.Equal(2, removed);
        Assert.False(Directory.Exists(_indexRoot) && Directory.GetDirectories(_indexRoot).Length > 0);
    }

    [Fact]
    public void ClearAll_NoIndexRoot_ReturnsZero()
        => Assert.Equal(0, new ContentIndexManager(_paths).ClearAll());

    // ─────────────────────────── errors / cancellation ───────────────────────────

    [Fact]
    public void BuildScope_MissingRoot_Throws()
    {
        var manager = new ContentIndexManager(_paths);
        Assert.Throws<DirectoryNotFoundException>(
            () => manager.BuildScope(Path.Combine(_sandbox, "does-not-exist"), Policy()));
    }

    [Fact]
    public void BuildScope_Cancelled_Throws()
    {
        Write("a.txt", "planner content");
        Write("b.txt", "more planner content");
        var manager = new ContentIndexManager(_paths);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => manager.BuildScope(_corpus, Policy(), cts.Token));
    }

    [Fact]
    public void ReliabilityHelpers_DoNotHideOutOfMemory()
    {
        var throwingPaths = new ThrowingScopePathProvider(_indexRoot);
        var manager = new ContentIndexManager(throwingPaths);
        Assert.Throws<OutOfMemoryException>(() => manager.IsScopeStale(_corpus));

        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        Assert.Throws<OutOfMemoryException>(() => manager.TryReanchorFreshScopeUnderLease(mutation, _corpus));
        Assert.Throws<OutOfMemoryException>(() => manager.CompactScopeIfOverSegmentedUnderLease(
            mutation, _corpus, Policy(), new IndexMaintenanceSettings(), DateTimeOffset.UtcNow));
    }

    private sealed class ThrowingScopePathProvider(string indexRoot) : IContentIndexPathProvider
    {
        public string IndexRoot { get; } = indexRoot;
        public string GetScopeDirectory(string scopeId) => throw new OutOfMemoryException("scope allocation failed");
    }
}
