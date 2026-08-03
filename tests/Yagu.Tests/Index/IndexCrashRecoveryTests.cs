using System.Diagnostics;
using System.Reflection;
using System.Text;
using Yagu.Services.Index;

namespace Yagu.Tests.Index;

/// <summary>
/// Hard-process-termination matrix for every named durable index mutation boundary. Each case launches the
/// Debug index worker, waits until the selected boundary has been durably logged, kills that exact process,
/// inspects storage before recovery, then acquires the writer lease twice to prove automatic recovery and
/// idempotence. The acceptable state is always one complete old state or one complete new state—never a
/// partial layer set. These are real filesystem/process tests, not exception-injection tests.
/// </summary>
public sealed class IndexCrashRecoveryTests : IDisposable
{
    private enum RawState { None, Old, New }
    private enum PdfState { Missing, Old, New, Disabled }

    private static readonly (string Point, bool Committed)[] FullBuildCases =
    [
        (IndexMutationFaults.ChecksummedBodyWritten, false),
        (IndexMutationFaults.ChecksummedDigestWritten, false),
        (IndexMutationFaults.ChecksummedFlushed, false),
        (IndexMutationFaults.BaseWritten, false),
        (IndexMutationFaults.BaseValidated, false),
        (IndexMutationFaults.BaseMarked, false),
        (IndexMutationFaults.BasePromoted, false),
        (IndexMutationFaults.PointerTempFlushed, false),
        (IndexMutationFaults.PointerPublished, false),
        (IndexMutationFaults.BasePointerPublished, false),
        (IndexMutationFaults.BaseMarkerCleared, false),
        (IndexMutationFaults.BaseCleanupFinished, false),
        (IndexMutationFaults.BuildBeforeImport, false),
        (IndexMutationFaults.ImportBaseMarked, false),
        (IndexMutationFaults.ImportBaseMoved, false),
        (IndexMutationFaults.ImportBeforePointer, false),
        (IndexMutationFaults.ImportPointerPublished, true),
        (IndexMutationFaults.ImportMarkersCleared, true),
        (IndexMutationFaults.ImportCleanupFinished, true),
        (IndexMutationFaults.BuildCommitted, true),
        (IndexMutationFaults.BuildWorkspaceDeleted, true),
    ];

    private static readonly (string Point, bool Committed)[] V3Cases =
    [
        (IndexMutationFaults.V3HeaderWritten, false),
        (IndexMutationFaults.V3BodyWritten, false),
        (IndexMutationFaults.V3FileClosed, false),
        (IndexMutationFaults.V3Published, false),
    ];

    private static readonly (string Point, bool Committed)[] SegmentCases =
    [
        (IndexMutationFaults.SegmentWritten, false),
        (IndexMutationFaults.SegmentValidated, false),
        (IndexMutationFaults.SegmentMarked, false),
        (IndexMutationFaults.SegmentPromoted, false),
        (IndexMutationFaults.PointerTempFlushed, false),
        (IndexMutationFaults.PointerPublished, true),
        (IndexMutationFaults.SegmentPointerPublished, true),
        (IndexMutationFaults.SegmentMarkerCleared, true),
        (IndexMutationFaults.SegmentCleanupFinished, true),
    ];

    private static readonly (string Point, bool Committed)[] CompactCases =
    [
        (IndexMutationFaults.BaseWritten, false),
        (IndexMutationFaults.BaseValidated, false),
        (IndexMutationFaults.BaseMarked, false),
        (IndexMutationFaults.BasePromoted, false),
        (IndexMutationFaults.PointerTempFlushed, false),
        (IndexMutationFaults.PointerPublished, true),
        (IndexMutationFaults.BasePointerPublished, true),
        (IndexMutationFaults.BaseMarkerCleared, true),
        (IndexMutationFaults.BaseCleanupFinished, true),
    ];

    private static readonly (string Point, bool Committed)[] CoalesceCases =
    [
        (IndexMutationFaults.CoalesceWritten, false),
        (IndexMutationFaults.CoalesceValidated, false),
        (IndexMutationFaults.CoalesceMarked, false),
        (IndexMutationFaults.CoalescePromoted, false),
        (IndexMutationFaults.CoalescePointerPublished, true),
        (IndexMutationFaults.CoalesceMarkerCleared, true),
        (IndexMutationFaults.CoalesceCleanupFinished, true),
    ];

    private static readonly (string Point, bool Committed)[] ReanchorCases =
    [
        (IndexMutationFaults.ChecksummedBodyWritten, false),
        (IndexMutationFaults.ChecksummedDigestWritten, false),
        (IndexMutationFaults.ChecksummedFlushed, false),
        (IndexMutationFaults.ReanchorManifestReplaced, true),
        (IndexMutationFaults.PointerTempFlushed, true),
        (IndexMutationFaults.PointerPublished, true),
        (IndexMutationFaults.ReanchorPointerPublished, true),
    ];

    private static readonly (string Point, bool RawCommitted, PdfState PdfAfterRecovery)[] PdfReplaceCases =
    [
        (IndexMutationFaults.PdfReplacementMarked, false, PdfState.Old),
        (IndexMutationFaults.PdfBackupMoved, false, PdfState.Old),
        (IndexMutationFaults.PdfReplacementInstalled, false, PdfState.New),
        (IndexMutationFaults.BuildBeforeImport, false, PdfState.New),
        (IndexMutationFaults.ImportBeforePointer, false, PdfState.New),
        (IndexMutationFaults.ImportPointerPublished, true, PdfState.New),
        (IndexMutationFaults.BuildCommitted, true, PdfState.New),
        (IndexMutationFaults.PdfEnabled, true, PdfState.New),
        (IndexMutationFaults.PdfBackupDeleted, true, PdfState.New),
    ];

    private static readonly (string Point, bool RawCommitted)[] PdfDeleteCases =
    [
        (IndexMutationFaults.PdfDisabled, false),
        (IndexMutationFaults.ExtendedBackupMoved, false),
        (IndexMutationFaults.ExtendedBackupDeleted, false),
        (IndexMutationFaults.BuildBeforeImport, false),
        (IndexMutationFaults.ImportBeforePointer, false),
        (IndexMutationFaults.ImportPointerPublished, true),
        (IndexMutationFaults.BuildCommitted, true),
        (IndexMutationFaults.PdfBackupDeleted, true),
    ];

    private static readonly (string Point, bool NewNamespace)[] ExtendedPublishCases =
    [
        (IndexMutationFaults.ExtendedValidated, false),
        (IndexMutationFaults.ExtendedBackupMoved, false),
        (IndexMutationFaults.ExtendedInstalled, true),
        (IndexMutationFaults.ExtendedEnabled, true),
        (IndexMutationFaults.ExtendedBackupDeleted, true),
    ];

    private static readonly string[] RecoveryCases =
    [
        IndexMutationFaults.RetentionStarted,
        IndexMutationFaults.RecoveryBuildWorkspaceDeleted,
        IndexMutationFaults.RecoveryScopeReconciled,
        IndexMutationFaults.RecoveryPdfRestored,
        IndexMutationFaults.RecoveryPdfBackupDeleted,
        IndexMutationFaults.RecoveryCompleted,
    ];

    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "yagu-index-hard-crash", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _indexRoot;
    private readonly FixedContentIndexPathProvider _paths;
    private readonly string _scopeId;

    public IndexCrashRecoveryTests()
    {
        _root = Path.Combine(_sandbox, "root");
        _indexRoot = Path.Combine(_sandbox, "index");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_indexRoot);
        _paths = new FixedContentIndexPathProvider(_indexRoot);
        _scopeId = ContentIndexManager.ScopeIdForRoot(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    public static IEnumerable<object[]> FullBuildData() => Cases(FullBuildCases);
    public static IEnumerable<object[]> V3Data() => Cases(V3Cases);
    public static IEnumerable<object[]> SegmentData() => Cases(SegmentCases);
    public static IEnumerable<object[]> CompactData() => Cases(CompactCases);
    public static IEnumerable<object[]> CoalesceData() => Cases(CoalesceCases);
    public static IEnumerable<object[]> ReanchorData() => Cases(ReanchorCases);
    public static IEnumerable<object[]> PdfReplaceData() => PdfReplaceCases.Select(static item =>
        new object[] { item.Point, item.RawCommitted, (int)item.PdfAfterRecovery });
    public static IEnumerable<object[]> PdfDeleteData() => Cases(PdfDeleteCases);
    public static IEnumerable<object[]> ExtendedPublishData() => Cases(ExtendedPublishCases);
    public static IEnumerable<object[]> RecoveryData() => RecoveryCases.Select(static point => new object[] { point });

    [Theory]
    [MemberData(nameof(FullBuildData))]
    public async Task InitialBuild_HardCrashAtEveryBoundary_ExposesNoneOrCompleteNewState(
        string point,
        bool committed)
    {
        PrepareNewCorpus(4);

        await CrashAsync("full-build", point);

        AssertRawState(committed ? RawState.New : RawState.None);
        RecoverTwice();
        AssertRawState(committed ? RawState.New : RawState.None);
        AssertNoCrashResidue();
    }

    [Theory]
    [MemberData(nameof(FullBuildData))]
    public async Task Rebuild_HardCrashAtEveryBoundary_PreservesCompleteOldOrNewState(
        string point,
        bool committed)
    {
        SeedOldRawIndex();
        PrepareNewCorpus(4);

        await CrashAsync("full-build", point);

        AssertRawState(committed ? RawState.New : RawState.Old);
        RecoverTwice();
        AssertRawState(committed ? RawState.New : RawState.Old);
        AssertNoCrashResidue();
    }

    [Theory]
    [MemberData(nameof(V3Data))]
    public async Task V3Write_HardCrashAtEveryBoundary_NeverPublishesAPartialGeneration(
        string point,
        bool committed)
    {
        PrepareNewCorpus(4);

        await CrashAsync("full-build-v3", point);

        Assert.False(committed);
        AssertRawState(RawState.None);
        RecoverTwice();
        AssertRawState(RawState.None);
        AssertNoCrashResidue();
    }

    [Theory]
    [InlineData(nameof(IndexMutationFaults.ImportSegmentMarked))]
    [InlineData(nameof(IndexMutationFaults.ImportSegmentMoved))]
    public async Task PagedInitialBuild_CrashDuringSegmentImport_ExposesNoPartialLayerSet(string pointField)
    {
        PrepareNewCorpus(300);
        string point = Constant(pointField);

        await CrashAsync("full-build", point);

        AssertRawState(RawState.None);
        RecoverTwice();
        AssertRawState(RawState.None);
        AssertNoCrashResidue();
    }

    [Theory]
    [MemberData(nameof(SegmentData))]
    public async Task IncrementalAppend_HardCrashAtEveryBoundary_PreservesBaseOrCompleteDelta(
        string point,
        bool committed)
    {
        SeedOldRawIndex();

        await CrashAsync("segment-append", point);

        Assert.Equal(committed ? 1 : 0, Store().ActiveSegmentCount());
        Assert.Equal(committed, LayeredContains(Path.Combine(_root, "delta.txt")));
        RecoverTwice();
        Assert.Equal(committed ? 1 : 0, Store().ActiveSegmentCount());
        Assert.Equal(committed, LayeredContains(Path.Combine(_root, "delta.txt")));
        AssertNoCrashResidue();
    }

    [Theory]
    [MemberData(nameof(CompactData))]
    public async Task Compaction_HardCrashAtEveryBoundary_PreservesLayeredOrCompleteBase(
        string point,
        bool committed)
    {
        SeedOldRawIndex();
        SeedSegments(1);

        await CrashAsync("compact", point);

        Assert.Equal(committed ? 0 : 1, Store().ActiveSegmentCount());
        Assert.True(LayeredContains(Path.Combine(_root, "segment-00.txt")));
        RecoverTwice();
        Assert.Equal(committed ? 0 : 1, Store().ActiveSegmentCount());
        Assert.True(LayeredContains(Path.Combine(_root, "segment-00.txt")));
        AssertNoCrashResidue();
    }

    [Theory]
    [MemberData(nameof(CoalesceData))]
    public async Task Coalescing_HardCrashAtEveryBoundary_PreservesOriginalRunOrCompleteReplacement(
        string point,
        bool committed)
    {
        SeedOldRawIndex();
        SeedSegments(8);

        await CrashAsync("coalesce", point);

        Assert.Equal(committed ? 1 : 9, Store().ActiveSegmentCount());
        Assert.True(LayeredContains(Path.Combine(_root, "coalesce-new.txt")));
        RecoverTwice();
        Assert.Equal(committed ? 1 : 9, Store().ActiveSegmentCount());
        Assert.True(LayeredContains(Path.Combine(_root, "coalesce-new.txt")));
        AssertNoCrashResidue();
    }

    [Theory]
    [MemberData(nameof(ReanchorData))]
    public async Task Reanchor_HardCrashAtEveryBoundary_LeavesOldOrNewValidManifest(
        string point,
        bool committed)
    {
        SeedOldRawIndex();

        await CrashAsync("reanchor", point);

        Assert.Equal(committed ? 9_000 : 100, Store().TryReadCurrentIncrementalManifest()!.FreshnessCheckpoint.NextUsn);
        RecoverTwice();
        Assert.Equal(committed ? 9_000 : 100, Store().TryReadCurrentIncrementalManifest()!.FreshnessCheckpoint.NextUsn);
        AssertNoCrashResidue();
    }

    [Theory]
    [MemberData(nameof(PdfReplaceData))]
    public async Task RebuildWithPdfReplacement_HardCrashRecoversCompleteNamespace(
        string point,
        bool rawCommitted,
        int expectedPdf)
    {
        SeedOldRawIndex();
        SeedPdf("old-engine", "old pdf content");
        PrepareNewCorpus(4);

        await CrashAsync("transaction-replace", point);

        AssertRawState(rawCommitted ? RawState.New : RawState.Old);
        AssertPdfIsNeverPartial();
        RecoverTwice();
        AssertRawState(rawCommitted ? RawState.New : RawState.Old);
        AssertPdfState((PdfState)expectedPdf);
        AssertNoCrashResidue();
    }

    [Theory]
    [MemberData(nameof(PdfDeleteData))]
    public async Task RebuildWithPdfDelete_HardCrashNeverResurrectsUnsafeNamespace(
        string point,
        bool rawCommitted)
    {
        SeedOldRawIndex();
        SeedPdf("old-engine", "old unsafe pdf content");
        PrepareNewCorpus(4);

        await CrashAsync("transaction-delete", point);

        AssertRawState(rawCommitted ? RawState.New : RawState.Old);
        AssertPdfState(PdfState.Disabled);
        RecoverTwice();
        AssertRawState(rawCommitted ? RawState.New : RawState.Old);
        AssertPdfState(PdfState.Disabled);
        AssertNoCrashResidue();
    }

    [Theory]
    [MemberData(nameof(ExtendedPublishData))]
    public async Task StandaloneExtendedPublish_HardCrashRestoresOldOrCompleteNewNamespace(
        string point,
        bool newNamespace)
    {
        SeedOldRawIndex();
        SeedPdf("old-engine", "old pdf content");

        await CrashAsync("extended-publish", point);

        AssertPdfIsNeverPartial();
        RecoverTwice();
        AssertPdfState(newNamespace ? PdfState.New : PdfState.Old);
        AssertNoCrashResidue();
    }

    [Theory]
    [MemberData(nameof(RecoveryData))]
    public async Task RecoveryItself_HardCrashAtEveryBoundary_IsIdempotent(string point)
    {
        PrepareRecoveryResidue(point);

        await CrashAsync("recover", point);
        RecoverTwice();
        AssertNoCrashResidue();
        if (point == IndexMutationFaults.RecoveryPdfRestored)
            Assert.True(File.Exists(Path.Combine(PdfStore().NamespaceDirectory(SpecialSourceKind.PdfText), "old.txt")));
    }

    [Fact]
    public void FaultPointInventory_EveryNamedBoundaryHasAHardCrashCase()
    {
        var covered = new HashSet<string>(StringComparer.Ordinal);
        AddPoints(covered, FullBuildCases);
        AddPoints(covered, V3Cases);
        AddPoints(covered, SegmentCases);
        AddPoints(covered, CompactCases);
        AddPoints(covered, CoalesceCases);
        AddPoints(covered, ReanchorCases);
        foreach (var item in PdfReplaceCases) covered.Add(item.Point);
        foreach (var item in PdfDeleteCases) covered.Add(item.Point);
        foreach (var item in ExtendedPublishCases) covered.Add(item.Point);
        covered.UnionWith(RecoveryCases);
        covered.Add(IndexMutationFaults.ImportSegmentMarked);
        covered.Add(IndexMutationFaults.ImportSegmentMoved);

        FieldInfo[] fields = typeof(IndexMutationFaults)
            .GetFields(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(static field => field.IsLiteral && field.FieldType == typeof(string))
            .ToArray();
        string[] declared = fields
            .Select(static field => (string)field.GetRawConstantValue()!)
            .OrderBy(static point => point, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(declared, covered.OrderBy(static point => point, StringComparer.Ordinal));

        string indexSourceDirectory = Path.Combine(FindRepoRoot(), "src", "Yagu", "Services", "Index");
        string productionSource = string.Join(
            '\n',
            Directory.EnumerateFiles(indexSourceDirectory, "*.cs")
                .Where(static path => !path.EndsWith("IndexMutationFaults.cs", StringComparison.OrdinalIgnoreCase))
                .Select(File.ReadAllText));
        foreach (FieldInfo field in fields)
            Assert.Contains("IndexMutationFaults." + field.Name, productionSource, StringComparison.Ordinal);
    }

    private static IEnumerable<object[]> Cases(IEnumerable<(string Point, bool Value)> cases) =>
        cases.Select(static item => new object[] { item.Point, item.Value });

    private static void AddPoints(HashSet<string> destination, IEnumerable<(string Point, bool Value)> cases)
    {
        foreach ((string point, _) in cases)
            destination.Add(point);
    }

    private static string Constant(string fieldName) => (string)(typeof(IndexMutationFaults)
        .GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic)
        ?.GetRawConstantValue() ?? throw new InvalidOperationException($"Unknown fault point '{fieldName}'."));

    private async Task CrashAsync(string scenario, string point, int occurrence = 1)
    {
        string worker = FindCrashWorker();
        string log = Path.Combine(_sandbox, "crash-point.log");
        File.Delete(log);
        var start = new ProcessStartInfo(worker)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("--index-crash-harness");
        start.ArgumentList.Add(scenario);
        start.ArgumentList.Add(_indexRoot);
        start.ArgumentList.Add(_root);
        start.Environment["YAGU_INDEX_CRASH_POINT"] = point;
        start.Environment["YAGU_INDEX_CRASH_OCCURRENCE"] = occurrence.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["YAGU_INDEX_CRASH_LOG"] = log;
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start crash worker.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try
        {
            var timeout = Stopwatch.StartNew();
            while (!File.Exists(log) && !process.HasExited && timeout.Elapsed < TimeSpan.FromSeconds(20))
                await Task.Delay(10);
            if (!File.Exists(log))
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                Assert.Fail($"Crash worker did not reach '{point}' ({scenario}); exit={process.ExitCode}; stdout={await stdout}; stderr={await stderr}");
            }

            Assert.False(process.HasExited, "The crash harness must block at the boundary until the parent kills it.");
            process.Kill(entireProcessTree: true); // exact child PID only; no broad process termination
            await process.WaitForExitAsync();
            Assert.Equal(point, File.ReadAllText(log).Trim());
            _ = await stdout;
            _ = await stderr;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private void SeedOldRawIndex()
    {
        ClearCorpus();
        string old = Path.Combine(_root, "old.txt");
        File.WriteAllText(old, "old complete state");
        var builder = new ContentIndexGenerationBuilder(OpenPolicy(), identityProvider: TestIdentity);
        builder.AddDocument(old, File.ReadAllBytes(old));
        Store().Publish(builder.Build(
            _scopeId,
            Path.GetPathRoot(_root) ?? string.Empty,
            IndexScopeIdentity.NormalizePath(_root),
            new UsnCheckpoint(1, 100),
            DateTimeOffset.UtcNow));
    }

    private void PrepareNewCorpus(int count)
    {
        ClearCorpus();
        for (int i = 0; i < count; i++)
            File.WriteAllText(Path.Combine(_root, $"new-{i:D4}.txt"), $"new complete state {i:D4}");
    }

    private void SeedSegments(int count)
    {
        var store = Store(retained: 4);
        for (int i = 0; i < count; i++)
        {
            string path = Path.Combine(_root, $"segment-{i:D2}.txt");
            File.WriteAllText(path, $"segment content {i:D2}");
            var builder = new ContentIndexDeltaSegmentBuilder(OpenPolicy(), identityProvider: TestIdentity);
            builder.AddChangedDocument(path, File.ReadAllBytes(path));
            store.PublishSegment(builder.Build(
                _scopeId,
                Path.GetPathRoot(_root) ?? string.Empty,
                IndexScopeIdentity.NormalizePath(_root),
                new UsnCheckpoint(1, 200 + i),
                DateTimeOffset.UtcNow));
        }
    }

    private void SeedPdf(string engineId, string text)
    {
        var fingerprint = new ExtractorFingerprint(
            SpecialSourceKind.PdfText,
            engineId,
            "1",
            "cpu",
            [new ExtractorFileHash("exe", engineId)],
            [new KeyValuePair<string, string>("mode", "test")]);
        var builder = new ExtendedSourceNamespaceBuilder(SpecialSourceKind.PdfText, fingerprint);
        builder.AddSource(
            IndexScopeIdentity.NormalizePath(Path.Combine(_root, "document.pdf")),
            new ExtractionOutcome.Success(text),
            new UsnFileIdentity(70, 0));
        Assert.True(PdfStore().Publish(builder.Build(
            IndexScopeIdentity.NormalizePath(_root),
            new UsnCheckpoint(1, 100))));
    }

    private void PrepareRecoveryResidue(string point)
    {
        if (point == IndexMutationFaults.RecoveryBuildWorkspaceDeleted)
        {
            Directory.CreateDirectory(Path.Combine(_indexRoot, ".build-abandoned"));
            return;
        }
        string scope = _paths.GetScopeDirectory(_scopeId);
        Directory.CreateDirectory(scope);
        if (point is IndexMutationFaults.RetentionStarted or IndexMutationFaults.RecoveryScopeReconciled)
        {
            Directory.CreateDirectory(Path.Combine(scope, "generations", ".gen-000001.tmp"));
            return;
        }
        string live = PdfStore().NamespaceDirectory(SpecialSourceKind.PdfText);
        string extended = Path.GetDirectoryName(live)!;
        Directory.CreateDirectory(extended);
        string backup = Path.Combine(extended, ExtendedSourceStore.BackupPrefix + "pdf-old");
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "old.txt"), "old backup");
        if (point == IndexMutationFaults.RecoveryPdfBackupDeleted)
        {
            Directory.CreateDirectory(live);
            File.WriteAllText(Path.Combine(live, "live.txt"), "live namespace");
        }
    }

    private void RecoverTwice()
    {
        using (IndexMutationContext.Acquire(_paths)) { }
        using (IndexMutationContext.Acquire(_paths)) { }
    }

    private void AssertRawState(RawState expected)
    {
        ContentIndexStore.LayeredIndexHandle? handle = Store(retained: 4).TryOpenLayered();
        if (expected == RawState.None)
        {
            Assert.Null(handle);
            return;
        }
        Assert.NotNull(handle);
        bool old = Contains(handle!, Path.Combine(_root, "old.txt"));
        bool @new = Contains(handle, Path.Combine(_root, "new-0000.txt"));
        Assert.Equal(expected == RawState.Old, old);
        Assert.Equal(expected == RawState.New, @new);
        Assert.False(old && @new, "A staged rebuild must never expose a mixed old/new raw state.");
    }

    private void AssertPdfIsNeverPartial()
    {
        ExtendedSourceNamespace? ns = PdfStore().TryLoad(SpecialSourceKind.PdfText);
        if (ns is not null)
            Assert.Contains(ns.Fingerprint.EngineId, new[] { "old-engine", "crash-harness" });
    }

    private void AssertPdfState(PdfState expected)
    {
        ExtendedSourceStore store = PdfStore();
        ExtendedSourceNamespace? ns = store.TryLoad(SpecialSourceKind.PdfText);
        bool disabled = File.Exists(store.DisabledMarkerPath(SpecialSourceKind.PdfText));
        switch (expected)
        {
            case PdfState.Missing:
                Assert.Null(ns);
                Assert.False(disabled);
                break;
            case PdfState.Old:
                Assert.Equal("old-engine", ns!.Fingerprint.EngineId);
                Assert.False(disabled);
                break;
            case PdfState.New:
                Assert.Equal("crash-harness", ns!.Fingerprint.EngineId);
                Assert.False(disabled);
                break;
            case PdfState.Disabled:
                Assert.Null(ns);
                Assert.True(disabled);
                break;
        }
    }

    private bool LayeredContains(string path)
    {
        ContentIndexStore.LayeredIndexHandle? handle = Store(retained: 4).TryOpenLayered();
        return handle is not null && Contains(handle, path);
    }

    private static bool Contains(ContentIndexStore.LayeredIndexHandle handle, string path)
    {
        string normalized = IndexScopeIdentity.NormalizePath(path);
        for (int i = handle.Segments.Count - 1; i >= 0; i--)
        {
            ContentIndexDeltaSegment segment = handle.Segments[i];
            if (segment.IsRemoved(normalized))
                return false;
            if (segment.Added.TryGetAlias(normalized, out _, out _))
                return true;
        }
        return handle.Base.TryGetAlias(normalized, out _, out _);
    }

    private void AssertNoCrashResidue()
    {
        if (!Directory.Exists(_indexRoot))
            return;
        string[] badDirectories = Directory.EnumerateDirectories(_indexRoot, "*", SearchOption.AllDirectories)
            .Where(static path =>
            {
                string name = Path.GetFileName(path);
                return name.StartsWith(".build-", StringComparison.OrdinalIgnoreCase)
                    || (name.StartsWith('.') && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    || name.StartsWith(ExtendedSourceStore.PublishTempPrefix, StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith(ExtendedSourceStore.BackupPrefix, StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith(".pdf-backup-", StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();
        Assert.Empty(badDirectories);
        string[] badFiles = Directory.EnumerateFiles(_indexRoot, "*", SearchOption.AllDirectories)
            .Where(static path =>
            {
                string name = Path.GetFileName(path);
                return name == ContentIndexStore.ImportMarkerFile
                    || name == ExtendedSourceStore.ReplacementReadyMarkerFile
                    || name.EndsWith(".reanchor.tmp", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".v3.tmp", StringComparison.OrdinalIgnoreCase)
                    || name is "current.a.tmp" or "current.b.tmp"
                    || name.EndsWith(ExtendedSourceStore.DisabledMarkerSuffix + ExtendedSourceStore.DisabledMarkerTempSuffix,
                        StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();
        Assert.Empty(badFiles);
    }

    private void ClearCorpus()
    {
        foreach (string path in Directory.EnumerateFiles(_root, "*", SearchOption.TopDirectoryOnly))
            File.Delete(path);
    }

    private ContentIndexStore Store(int retained = 2) => new(_paths, _scopeId, retained);
    private ExtendedSourceStore PdfStore() => new(_paths, _scopeId);
    private static IndexIngestionPolicy OpenPolicy() => new(0, null, null, true, false, 0);

    private static FileIdentity? TestIdentity(string path)
    {
        ulong hash = 1469598103934665603UL;
        foreach (byte value in Encoding.UTF8.GetBytes(IndexScopeIdentity.NormalizePath(path)))
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }
        return new FileIdentity(0x55, new UsnFileIdentity(hash, 0));
    }

    private static string FindCrashWorker()
    {
        string repo = FindRepoRoot();
        string candidate = Path.Combine(repo, "src", "Yagu.IndexWorker", "bin", "Debug", "net10.0", "Yagu.IndexWorker.exe");
        return File.Exists(candidate)
            ? candidate
            : throw new FileNotFoundException("The Debug index crash worker was not built.", candidate);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yagu.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the Yagu repository root.");
    }
}
