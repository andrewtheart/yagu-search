using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.Services.Index;

/// <summary>
/// An in-process <see cref="IIndexCandidateSource"/> that produces the candidate content-id set from a
/// generation's <b>memory-mapped format-v3 postings</b> (<see cref="ContentIndexV3Reader"/>, plan §5.1)
/// instead of the deserialized <see cref="TrigramPostingIndex"/>. Because the v3 postings are built from the
/// same generation, the set is byte-for-byte identical to <see cref="TrigramPostingIndex.EvaluateSet"/>
/// (proven by <c>ContentIndexV3FormatTests</c> / <c>ContentIndexRustParityTests</c>) — so routing through it
/// never changes search results; it only reads candidates over mapped pages (bounded resident memory) and is
/// the managed reference the out-of-process worker mirrors.
/// <para>
/// Conservative by contract: a generation that was NOT built with the v3 sidecars (un-upgraded), a missing
/// file, or any read/integrity fault returns <c>false</c> so the accelerator falls back to the in-process
/// posting evaluation. This runs once per search at barrier B0, never per discovered path.
/// </para>
/// </summary>
public sealed class V3ContentIndexCandidateSource : IIndexCandidateSource
{
    /// <summary>Shared stateless instance (the reader is opened + disposed per evaluation).</summary>
    public static readonly V3ContentIndexCandidateSource Instance = new();

    /// <inheritdoc />
    public bool TryEvaluate(string generationDir, TrigramExpression query, out IReadOnlySet<int> candidateContentIds)
    {
        candidateContentIds = ImmutableHashSet<int>.Empty;
        if (string.IsNullOrEmpty(generationDir) || query is null)
        {
            return false;
        }

        try
        {
            // TryOpen returns null when the v3 sidecars are absent (un-upgraded generation) or the header is
            // corrupt; EvaluateSet may throw InvalidDataException on a torn body block — both fall back.
            using ContentIndexV3Reader? reader = ContentIndexV3Format.TryOpen(generationDir);
            if (reader is null)
            {
                return false;
            }

            // EvaluateSet materializes a plain HashSet<int> (copied out of the mapped postings region), so
            // the set stays valid after the reader is disposed here.
            candidateContentIds = reader.EvaluateSet(query);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            YaguLog.For("ContentIndex").LogDebug(ex, "format-v3 candidate read failed for '{GenerationDir}'; falling back to in-process evaluation.", generationDir);
            candidateContentIds = ImmutableHashSet<int>.Empty;
            return false;
        }
    }
}
