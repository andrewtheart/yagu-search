#if DEBUG
using System.Text;
using Yagu.Services.Index;

namespace Yagu.IndexWorker;

/// <summary>
/// Executes real persistence mutations for the hard-termination tests. This code exists only in Debug
/// worker builds and is never reachable in a shipped Release worker.
/// </summary>
internal static class IndexCrashHarness
{
    private const string Switch = "--index-crash-harness";

    public static bool IsRequested(string[] args) => args.Length > 0
        && string.Equals(args[0], Switch, StringComparison.OrdinalIgnoreCase);

    public static int Run(string[] args)
    {
        if (args.Length != 4)
            return 64;

        string scenario = args[1];
        string storage = Path.GetFullPath(args[2]);
        string root = Path.GetFullPath(args[3]);
        var paths = new FixedContentIndexPathProvider(storage);
        string scopeId = ContentIndexManager.ScopeIdForRoot(root);
        IndexIngestionPolicy policy = OpenPolicy();

        switch (scenario)
        {
            case "full-build":
                new ContentIndexManager(paths, retainedGenerations: 2)
                    .BuildScope(root, policy, buildMemoryBudgetMB: 1);
                break;

            case "full-build-v3":
                new ContentIndexManager(paths, retainedGenerations: 2)
                {
                    ProduceV3QueryStructures = true,
                }.BuildScope(root, policy, buildMemoryBudgetMB: 1);
                break;

            case "direct-base":
                new ContentIndexStore(paths, scopeId, retainedGenerations: 2)
                    .Publish(BuildGeneration(root, scopeId, policy, 500));
                break;

            case "segment-append":
                PublishSegment(paths, root, scopeId, policy, 600);
                break;

            case "compact":
                new ContentIndexManager(paths, retainedGenerations: 4).CompactScopeNow(
                    root,
                    new IndexMaintenanceSettings
                    {
                        BuildMemoryBudgetMB = 1,
                        ProduceV3QueryStructures = true,
                    },
                    DateTimeOffset.UtcNow);
                break;

            case "coalesce":
            {
                var store = new ContentIndexStore(paths, scopeId, retainedGenerations: 4);
                var updater = new ContentIndexIncrementalUpdater(store, policy);
                string path = Path.Combine(root, "coalesce-new.txt");
                File.WriteAllText(path, "coalesce newest content");
                IndexContentClassification classification = IndexIngestionClassifier.ClassifyContent(
                    Encoding.UTF8.GetBytes("coalesce newest content"), policy);
                updater.Apply(
                    scopeId,
                    Path.GetPathRoot(root) ?? string.Empty,
                    IndexScopeIdentity.NormalizePath(root),
                    [new IncrementalChange(path, classification, new FileIdentity(0x55, new UsnFileIdentity(9_999, 0)))],
                    Array.Empty<string>(),
                    new UsnCheckpoint(1, 900),
                    new IndexMaintenanceSettings
                    {
                        MaxDeltaSegments = 8,
                        CompactionThresholdMB = 8192,
                        MaxAutoCompactionSizeMB = 512,
                    },
                    DateTimeOffset.UtcNow);
                break;
            }

            case "reanchor":
                new ContentIndexStore(paths, scopeId, retainedGenerations: 2)
                    .TryReanchorBaseCheckpoint(new UsnCheckpoint(1, 9_000));
                break;

            case "transaction-replace":
                RunTransaction(paths, root, scopeId, policy, StagedPdfCommitMode.Replace);
                break;

            case "transaction-delete":
                RunTransaction(paths, root, scopeId, policy, StagedPdfCommitMode.Delete);
                break;

            case "extended-publish":
            {
                using IndexMutationContext mutation = IndexMutationContext.Acquire(paths);
                ExtendedSourceNamespace ns = BuildPdfNamespace(root, "replacement pdf content");
                new ExtendedSourceStore(paths, scopeId).PublishUnderLease(mutation, ns);
                break;
            }

            case "extended-disable":
                new ExtendedSourceStore(paths, scopeId).Delete(SpecialSourceKind.PdfText);
                break;

            case "recover":
                using (IndexMutationContext.Acquire(paths)) { }
                break;

            default:
                return 65;
        }

        // A configured fault point that was not reached is a harness/test error.
        return 66;
    }

    private static void RunTransaction(
        IContentIndexPathProvider paths,
        string root,
        string scopeId,
        IndexIngestionPolicy policy,
        StagedPdfCommitMode mode)
    {
        using IndexMutationContext mutation = IndexMutationContext.Acquire(paths);
        using var transaction = new ContentIndexBuildTransaction(paths, scopeId);
        var manager = new ContentIndexManager(transaction.Paths, retainedGenerations: 2);
        manager.BuildScopeUnderLease(mutation, root, policy, buildMemoryBudgetMB: 1);
        if (mode == StagedPdfCommitMode.Replace)
        {
            var staged = new ExtendedSourceStore(transaction.Paths, scopeId);
            if (!staged.PublishUnderLease(mutation, BuildPdfNamespace(root, "replacement pdf content")))
                throw new InvalidDataException("Could not seed the staged PDF namespace.");
        }
        transaction.Commit(mutation, retainedGenerations: 2, mode);
    }

    private static void PublishSegment(
        IContentIndexPathProvider paths,
        string root,
        string scopeId,
        IndexIngestionPolicy policy,
        long usn)
    {
        string path = Path.Combine(root, "delta.txt");
        File.WriteAllText(path, "new delta content");
        var builder = new ContentIndexDeltaSegmentBuilder(
            policy,
            identityProvider: _ => new FileIdentity(0x55, new UsnFileIdentity(8_888, 0)));
        builder.AddChangedDocument(path, Encoding.UTF8.GetBytes("new delta content"));
        var segment = builder.Build(
            scopeId,
            Path.GetPathRoot(root) ?? string.Empty,
            IndexScopeIdentity.NormalizePath(root),
            new UsnCheckpoint(1, usn),
            DateTimeOffset.UtcNow);
        new ContentIndexStore(paths, scopeId, retainedGenerations: 4).PublishSegment(segment);
    }

    private static ContentIndexGeneration BuildGeneration(
        string root,
        string scopeId,
        IndexIngestionPolicy policy,
        long usn)
    {
        ulong nextIdentity = 1_000;
        var builder = new ContentIndexGenerationBuilder(
            policy,
            identityProvider: _ => new FileIdentity(0x55, new UsnFileIdentity(nextIdentity++, 0)));
        foreach (string path in Directory.EnumerateFiles(root, "*.txt", SearchOption.TopDirectoryOnly))
            builder.AddDocument(path, File.ReadAllBytes(path));
        return builder.Build(
            scopeId,
            Path.GetPathRoot(root) ?? string.Empty,
            IndexScopeIdentity.NormalizePath(root),
            new UsnCheckpoint(1, usn),
            DateTimeOffset.UtcNow);
    }

    private static ExtendedSourceNamespace BuildPdfNamespace(string root, string text)
    {
        var fingerprint = new ExtractorFingerprint(
            SpecialSourceKind.PdfText,
            "crash-harness",
            "1",
            "cpu",
            [new ExtractorFileHash("exe", "harness")],
            [new KeyValuePair<string, string>("mode", "test")]);
        var builder = new ExtendedSourceNamespaceBuilder(SpecialSourceKind.PdfText, fingerprint);
        builder.AddSource(
            IndexScopeIdentity.NormalizePath(Path.Combine(root, "document.pdf")),
            new ExtractionOutcome.Success(text),
            new UsnFileIdentity(7_777, 0));
        return builder.Build(IndexScopeIdentity.NormalizePath(root), new UsnCheckpoint(1, 500));
    }

    private static IndexIngestionPolicy OpenPolicy() => new(
        maxFileSizeBytes: 0,
        excludedGlobs: null,
        excludedExtensions: null,
        includeHiddenFiles: true,
        followReparsePoints: false,
        maxDepth: 0);
}
#endif
