namespace Yagu.Services.Index;

/// <summary>
/// The typed result of running an extended-source extractor (archive / PDF-text / OCR), borrowed from
/// DocFetcher's indexing outcomes (plan §7 Phase 4). It is a closed set — never a bare string or bool —
/// because only one specific case (<see cref="DeterministicUnsupported"/> from a fingerprinted
/// deterministic extractor) may ever persist a negative exclusion proof. Determinism is <b>never</b>
/// inferred from empty output, an error string, an exit code, a timeout, an exception, or OOM.
/// </summary>
public abstract record ExtractionOutcome
{
    private ExtractionOutcome() { }

    /// <summary>
    /// The extractor produced text (possibly empty) — a positive, persistable result whose trigrams
    /// may be stored as postings for candidate selection.
    /// </summary>
    public sealed record Success(string Text) : ExtractionOutcome;

    /// <summary>
    /// A fingerprinted <em>deterministic</em> extractor proved this source cannot be extracted (e.g. an
    /// image-only PDF for a text extractor). The <b>only</b> outcome that may persist a negative proof.
    /// </summary>
    public sealed record DeterministicUnsupported(string Reason) : ExtractionOutcome;

    /// <summary>
    /// A non-deterministic or environmental failure (timeout, OOM, I/O, provider error, error string).
    /// Retried on the next search; never a persistable negative.
    /// </summary>
    public sealed record TransientFailure(string Reason) : ExtractionOutcome;

    /// <summary>The extraction was cancelled. Never a persistable result of any kind.</summary>
    public sealed record Cancelled : ExtractionOutcome;
}
