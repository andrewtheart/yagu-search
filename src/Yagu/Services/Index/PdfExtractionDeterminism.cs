using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;
using Yagu.Services.Pdf;

namespace Yagu.Services.Index;

/// <summary>
/// The determinism verdict for the bundled <c>pdftotext.exe</c> extractor on this machine (plan §7 Phase 4
/// safety gate). PDF-text nonmember <em>pruning</em> is only safe when the extractor is repeatable: a source
/// built with no matching trigrams must re-extract to the same text (still no match) at query time. If the
/// tool is not proven repeatable, its namespace must never prune — every PDF is live-extracted.
/// </summary>
public enum PdfDeterminismVerdict
{
    /// <summary>Every sampled PDF extracted to byte-identical text on a repeat run — pruning is safe.</summary>
    Deterministic,

    /// <summary>Repeatability could not be proven (no successful sample, or a repeat differed) — never prune.</summary>
    NotProven,
}

/// <summary>
/// The plan-mandated "determinism repeatability proof" that MUST pass before a PDF-text extended-source
/// namespace may prune nonmembers (plan §7 Phase 4). It re-extracts a small sample of the scope's PDFs and
/// requires each to reproduce byte-identical text. This is a property of the <em>tool</em> (a deterministic
/// command-line extractor with no randomness), so a bounded sample is a sound proof; a single mismatch — or
/// the inability to reproduce any successful extraction — fails closed to <see cref="PdfDeterminismVerdict.NotProven"/>.
/// It only ever compares extractions of the SAME unchanged file, so it can never be fooled by a file that
/// legitimately changed between runs.
/// </summary>
public static class PdfExtractionDeterminism
{
    /// <summary>Default number of distinct PDFs to reproduce (bounded so the proof is cheap).</summary>
    public const int DefaultMaxSamples = 3;

    /// <summary>
    /// Probes repeatability by extracting up to <paramref name="maxSamples"/> of <paramref name="candidatePaths"/>
    /// twice via <paramref name="extract"/> and comparing the text ordinally. Returns
    /// <see cref="PdfDeterminismVerdict.Deterministic"/> only when at least one sample was successfully
    /// extracted twice to identical text and no sampled repeat differed; otherwise
    /// <see cref="PdfDeterminismVerdict.NotProven"/>. A candidate whose FIRST extraction fails is skipped
    /// (nothing to reproduce); a candidate whose first succeeds but repeat fails or differs fails the proof.
    /// </summary>
    public static async Task<PdfDeterminismVerdict> ProbeAsync(
        IReadOnlyList<string> candidatePaths,
        Func<string, CancellationToken, Task<PdfTextResult>> extract,
        int maxSamples = DefaultMaxSamples,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidatePaths);
        ArgumentNullException.ThrowIfNull(extract);
        if (maxSamples <= 0)
            return PdfDeterminismVerdict.NotProven;

        int reproduced = 0;
        foreach (string path in candidatePaths)
        {
            if (reproduced >= maxSamples)
                break;
            cancellationToken.ThrowIfCancellationRequested();

            PdfTextResult first;
            try { first = await extract(path, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                YaguLog.For("ContentIndex").LogWarning(ex, "PDF determinism probe: the first extraction threw → treating the source as not proven (PDF-text pruning stays off).");
                return PdfDeterminismVerdict.NotProven;
            }
            if (!first.Success)
                continue; // an extraction that fails once has nothing to reproduce — skip, don't fail

            PdfTextResult second;
            try { second = await extract(path, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                YaguLog.For("ContentIndex").LogWarning(ex, "PDF determinism probe: the reproduction extraction threw → treating the source as not proven (PDF-text pruning stays off).");
                return PdfDeterminismVerdict.NotProven;
            }

            // A source we could extract once but not reproduce identically is NOT deterministic → fail closed.
            if (!second.Success || !string.Equals(first.Text, second.Text, StringComparison.Ordinal))
                return PdfDeterminismVerdict.NotProven;

            reproduced++;
        }

        return reproduced > 0 ? PdfDeterminismVerdict.Deterministic : PdfDeterminismVerdict.NotProven;
    }
}
