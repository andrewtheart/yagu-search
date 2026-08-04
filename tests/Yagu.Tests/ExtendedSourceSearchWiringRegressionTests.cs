using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Source-pin regression tests for the extended-source (archive / PDF-text / OCR) pruning gate wired into
/// the live search discovery loop (plan §7 Phase 4). The wiring is fail-safe: a null gate leaves the
/// OCR/PDF enqueue path byte-for-byte the extract-everything path, the gate only prunes a proven-fresh
/// deterministic nonmember, and end-of-discovery B1 reconciliation re-extracts anything changed after B0.
/// </summary>
public sealed class ExtendedSourceSearchWiringRegressionTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string SearchServiceSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "Services", "SearchService.cs"));
    private static readonly string SearchOptionsSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "Models", "SearchOptions.cs"));
    private static readonly string GateSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "Services", "Index", "ExtendedSourceSearchGate.cs"));
    private static readonly string ManagerSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "Services", "Index", "ContentIndexManager.cs"));
    private static readonly string ViewModelSource = MainViewModelPartials.Text;
    private static readonly string CliRunnerSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "CliRunner.cs"));
    private static readonly string SettingsActionsSource = File.ReadAllText(
        Path.Combine(RepoRoot, "src", "Yagu", "UI", "Windows", "Settings", "SettingsWindow.IndexingActions.cs"));

    [Fact]
    public void SearchOptions_ExposesExtendedSourceGateFactory()
        => Assert.Contains("public Func<Services.Index.ExtendedSourceSearchGate?>? ExtendedSourceGateFactory { get; set; }", SearchOptionsSource);

    [Fact]
    public void SearchService_CreatesExtendedSourceGateOffThreadAtDiscoveryStart()
    {
        Assert.Contains("Services.Index.ExtendedSourceSearchGate? extendedSourceGate = null;", SearchServiceSource);
        Assert.Contains("extendedSourceGate = options.ExtendedSourceGateFactory?.Invoke();", SearchServiceSource);
    }

    [Fact]
    public void SearchService_GatesOcrAndPdfEnqueueThroughShouldExtract()
    {
        // Both the native and managed content paths gate the image and PDF enqueue behind the fail-safe
        // null-check so a disabled gate never changes routing.
        Assert.Contains("extendedSourceGate is null || extendedSourceGate.ShouldExtract(Services.Index.SpecialSourceKind.ImageOcr, file, out ocrPrioritized)", SearchServiceSource);
        Assert.Contains("imageOcr.TryEnqueue(file, ocrPrioritized)", SearchServiceSource);
        Assert.Contains("extendedSourceGate is null || extendedSourceGate.ShouldExtract(Services.Index.SpecialSourceKind.PdfText, file)", SearchServiceSource);
    }

    [Fact]
    public void RawIndexPruning_NeverSuppressesArchivePdfOrOcrExtraction()
    {
        Assert.Contains("RequiresAuthoritativeSpecialSourceScan(path, options)", SearchServiceSource);
        Assert.Contains("options.SearchInsideArchives && ZipArchiveSearcher.HasArchiveExtension", SearchServiceSource);
        Assert.Contains("options.SearchImageText && Ocr.ImageOcrSupport.IsImageCandidate", SearchServiceSource);
        Assert.Contains("options.SearchPdfText && Pdf.PdfTextSupport.IsPdfCandidate", SearchServiceSource);
        Assert.Contains("await WritePendingFileAsync(path).ConfigureAwait(false);", SearchServiceSource);
    }

    [Fact]
    public void SearchService_DrainsExtendedSourceRescueBeforeExtractorsComplete()
    {
        Assert.Contains("foreach (string rescue in extendedSourceGate.GetSourcesToRescan())", SearchServiceSource);
        Assert.Contains("if (imageOcr.TryEnqueue(rescue))", SearchServiceSource);
        Assert.Contains("if (pdfText.TryEnqueue(rescue))", SearchServiceSource);
    }

    [Fact]
    public void Gate_NeverPrunesAnUnknownSource()
    {
        // The critical safety guard: a source the namespace never saw at build time must always be extracted,
        // otherwise a nonmember classification would silently hide an un-indexed PDF.
        Assert.Contains("if (!ctx.Namespace.IsKnownSource(key))", GateSource);
        Assert.Contains("public bool IsKnownSource(", File.ReadAllText(
            Path.Combine(RepoRoot, "src", "Yagu", "Services", "Index", "ExtendedSourceNamespace.cs")));
    }

    [Fact]
    public void Gate_TryCreate_LoadsPdfNamespaceOnlyWhenEnabled()
    {
        Assert.Contains("public static ExtendedSourceSearchGate? TryCreate(", GateSource);
        Assert.Contains("if (options.SearchPdfText && settings.IndexBuildPdfTextExtendedSource)", GateSource);
        Assert.Contains("PdfExtractorFingerprint.TryCompute(extractor)", GateSource);
        Assert.Contains(".TryLoad(SpecialSourceKind.PdfText)", GateSource);
    }

    [Fact]
    public void Gate_TryCreate_LoadsPositiveOcrNamespaceWhenEnabled()
    {
        Assert.Contains("if (options.SearchImageText && settings.IndexBuildImageTextExtendedSource)", GateSource);
        Assert.Contains("ImageOcrExtractorFingerprint.TryCompute(", GateSource);
        Assert.Contains(".TryLoad(SpecialSourceKind.ImageOcr)", GateSource);
    }

    [Fact]
    public void Manager_BuildsPdfNamespace_AndPublishesOnlyWhenProvenRepeatable()
    {
        Assert.Contains("public async Task<PdfExtendedSourceBuildResult> BuildPdfExtendedSourceAsync(", ManagerSource);
        // Publish only when the determinism proof passed; otherwise drop any stale namespace (never prune).
        Assert.Contains("if (!population.IsPrunable)", ManagerSource);
        Assert.Contains("store.DeleteUnderLease(mutation, SpecialSourceKind.PdfText);", ManagerSource);
        Assert.Contains("if (!store.PublishUnderLease(mutation, population.Namespace))", ManagerSource);
        Assert.Contains("throw new IOException(\"The PDF-text namespace failed validation or publication.\");", ManagerSource);
    }

    [Fact]
    public void ViewModel_AttachesExtendedSourceGateFactoryWhenPdfExtendedSourceEnabled()
    {
        Assert.Contains("settings.IndexBuildPdfTextExtendedSource && rootOptions.SearchPdfText", ViewModelSource);
        Assert.Contains("settings.IndexBuildImageTextExtendedSource && rootOptions.SearchImageText", ViewModelSource);
        Assert.Contains("rootOptions.ExtendedSourceGateFactory = () =>", ViewModelSource);
        Assert.Contains("Yagu.Services.Index.ExtendedSourceSearchGate.TryCreate(", ViewModelSource);
    }

    [Fact]
    public void CliRunner_AttachesExtendedSourceGateFactory_AndBuildsPdfNamespace()
    {
        Assert.Contains("gateSettings.IndexBuildPdfTextExtendedSource && gateOptions.SearchPdfText", CliRunnerSource);
        Assert.Contains("gateSettings.IndexBuildImageTextExtendedSource && gateOptions.SearchImageText", CliRunnerSource);
        Assert.Contains("searchOptions.ExtendedSourceGateFactory = () =>", CliRunnerSource);
        Assert.Contains("string indexRoot = ResolveGateIndexRoot(extendedPathProvider);", CliRunnerSource);
        Assert.Contains("return ExtendedSourceSearchGate.TryCreate(", CliRunnerSource);
        Assert.Contains("IndexBuildOperationFactory.CreateBuild(settings, buildRoot, args.IndexRebuildRequested)", CliRunnerSource);
        Assert.Contains("result.PdfStatus", CliRunnerSource);
        Assert.Contains("result.ImageOcrStatus", CliRunnerSource);
    }

    [Fact]
    public void SettingsBuild_RunsPdfExtendedSourcePopulationWhenEnabled()
    {
        Assert.Contains("IndexBuildOperationFactory.CreateBuild(_viewModel.Settings, root, rebuild)", SettingsActionsSource);
        Assert.Contains("pdfProgress:", SettingsActionsSource);
        Assert.Contains("imageOcrProgress:", SettingsActionsSource);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repository root.");
    }
}
