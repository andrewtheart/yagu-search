using Yagu.Services.Pdf;

using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.Services.Index;

/// <summary>
/// The result of populating a PDF-text extended-source namespace for one scope (plan §7 Phase 4).
/// </summary>
/// <param name="Namespace">The built namespace (postings over each PDF's extracted text).</param>
/// <param name="Determinism">
/// Whether the extractor was proven repeatable on this scope's PDFs. The namespace may only be persisted for
/// pruning when this is <see cref="PdfDeterminismVerdict.Deterministic"/>.
/// </param>
/// <param name="PdfsSeen">Number of PDF files considered.</param>
/// <param name="Admitted">Number of PDFs whose text was extracted and admitted as posting documents.</param>
public sealed record PdfPopulationResult(
    ExtendedSourceNamespace Namespace,
    PdfDeterminismVerdict Determinism,
    int PdfsSeen,
    int Admitted)
{
    /// <summary>True only when the namespace is safe to persist and use for nonmember pruning.</summary>
    public bool IsPrunable => Determinism == PdfDeterminismVerdict.Deterministic;
}

/// <summary>
/// Builds a PDF-text <see cref="ExtendedSourceNamespace"/> for an index scope during a build (plan §7
/// Phase 4). For each PDF it runs the fingerprinted <see cref="PdfTextExtractor"/>, streams the
/// <em>ephemeral</em> extracted text into <see cref="ExtendedSourceNamespaceBuilder"/> (which reduces it to
/// trigrams and immediately discards the text — §6.4), and captures the source's build-time file identity for
/// USN freshness. It then runs the plan-mandated <see cref="PdfExtractionDeterminism"/> repeatability proof;
/// the caller persists the namespace for pruning ONLY when that proof passes. A PDF whose extraction fails is
/// recorded as a <see cref="ExtractionOutcome.TransientFailure"/> (never a persisted negative — determinism is
/// never inferred from a failure), so it is always live-extracted next search.
/// </summary>
public sealed class PdfExtendedSourcePopulator
{
    private readonly PdfTextExtractor _extractor;
    private readonly ExtractorFingerprint _fingerprint;
    private readonly Func<string, UsnFileIdentity?> _identityProvider;

    /// <summary>
    /// Creates a populator. <paramref name="fingerprint"/> is the current extractor fingerprint (see
    /// <see cref="PdfExtractorFingerprint.TryCompute"/>); it MUST be a <see cref="SpecialSourceKind.PdfText"/>
    /// fingerprint. <paramref name="identityProvider"/> captures each PDF's durable file identity for USN
    /// freshness (typically <c>FileIdentityReader.TryGetIdentity</c>); return <c>null</c> when unavailable.
    /// </summary>
    public PdfExtendedSourcePopulator(
        PdfTextExtractor extractor,
        ExtractorFingerprint fingerprint,
        Func<string, UsnFileIdentity?> identityProvider)
    {
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _fingerprint = fingerprint ?? throw new ArgumentNullException(nameof(fingerprint));
        if (fingerprint.Source != SpecialSourceKind.PdfText)
            throw new ArgumentException("Fingerprint must be for the PdfText source.", nameof(fingerprint));
        _identityProvider = identityProvider ?? throw new ArgumentNullException(nameof(identityProvider));
    }

    /// <summary>
    /// Extracts every PDF in <paramref name="pdfPaths"/>, builds the namespace, and runs the determinism
    /// proof over the successfully-extracted sample. <paramref name="normalizedRootPath"/> and
    /// <paramref name="freshnessCheckpoint"/> record where the sources live and the build-time USN cursor.
    /// Never throws for extraction failures (they degrade to live-extraction); honors cancellation.
    /// </summary>
    public async Task<PdfPopulationResult> PopulateAsync(
        IReadOnlyList<string> pdfPaths,
        string normalizedRootPath,
        UsnCheckpoint freshnessCheckpoint,
        int maxDeterminismSamples = PdfExtractionDeterminism.DefaultMaxSamples,
        CancellationToken cancellationToken = default,
        Action<PdfBuildProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(pdfPaths);
        ArgumentException.ThrowIfNullOrEmpty(normalizedRootPath);

        var builder = new ExtendedSourceNamespaceBuilder(SpecialSourceKind.PdfText, _fingerprint);
        var extractedOk = new List<string>();
        int admitted = 0;
        int processed = 0;

        foreach (string path in pdfPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string key = IndexScopeIdentity.NormalizePath(path);
            ExtractionOutcome outcome = await ExtractOutcomeAsync(path, cancellationToken).ConfigureAwait(false);

            UsnFileIdentity? identity = SafeIdentity(path);
            int docId = builder.AddSource(key, outcome, identity);
            if (docId >= 0)
                admitted++;
            if (outcome is ExtractionOutcome.Success)
                extractedOk.Add(path);
            progress?.Invoke(new PdfBuildProgress(++processed, pdfPaths.Count));
        }

        ExtendedSourceNamespace ns = builder.Build(normalizedRootPath, freshnessCheckpoint);

        // The determinism proof reproduces a bounded SAMPLE of the successfully-extracted PDFs. Only after it
        // passes is the namespace safe to persist for pruning (a persisted namespace => proof passed).
        PdfDeterminismVerdict verdict = await PdfExtractionDeterminism.ProbeAsync(
            extractedOk, ExtractRawAsync, maxDeterminismSamples, cancellationToken).ConfigureAwait(false);

        YaguLog.For("ContentIndex").LogInformation(
            "PDF extended-source populated for '{Root}': {PdfCount} PDF(s), {Admitted} admitted, determinism={Verdict}.", normalizedRootPath, pdfPaths.Count, admitted, verdict);

        return new PdfPopulationResult(ns, verdict, pdfPaths.Count, admitted);
    }

    private async Task<ExtractionOutcome> ExtractOutcomeAsync(string path, CancellationToken ct)
    {
        PdfTextResult result = await ExtractRawAsync(path, ct).ConfigureAwait(false);
        // Ok(text) — including a clean exit with EMPTY text (an image-only PDF with no text layer, a legitimate
        // nonmember of any text query). A failure is a TransientFailure, NEVER a persisted negative: determinism
        // is never inferred from an error/exit-code/timeout (ExtractionOutcome contract).
        return result.Success
            ? new ExtractionOutcome.Success(result.Text)
            : new ExtractionOutcome.TransientFailure(result.Error ?? "pdftotext failed");
    }

    private Task<PdfTextResult> ExtractRawAsync(string path, CancellationToken ct) => _extractor.ExtractAsync(path, ct);

    private UsnFileIdentity? SafeIdentity(string path)
    {
        try { return _identityProvider(path); }
        catch { return null; }
    }
}
