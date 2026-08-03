using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Yagu.Services.Index;

/// <summary>
/// An <see cref="IIndexCandidateSource"/> backed by the out-of-process <see cref="IndexWorkerClient"/>: it
/// encodes the planned trigram query to RPN, asks the worker to verify + query the generation's
/// <c>content.bin</c>, and returns the candidate content-id set (plan §3.3). The result is byte-for-byte
/// identical to the in-process <see cref="TrigramPostingIndex.EvaluateSet"/> (proven by
/// <c>ContentIndexRustParityTests</c>), so routing through the worker never changes search results — it only
/// moves the untrusted-index parsing into an isolated process where a native fault cannot crash the app.
/// <para>
/// This runs once per search at barrier B0 (already off the UI thread), never per discovered path, so the
/// single async→sync bridge here is not on the per-file hot path. Every failure path returns <c>false</c> so
/// the accelerator falls back to the in-process evaluation.
/// </para>
/// </summary>
internal sealed class IndexWorkerQuerySource : IIndexCandidateSource
{
    private readonly IndexWorkerClient _client;
    private readonly TimeSpan _timeout;

    public IndexWorkerQuerySource(IndexWorkerClient client, TimeSpan? timeout = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _timeout = timeout is { } t && t > TimeSpan.Zero ? t : TimeSpan.FromSeconds(30);
    }

    public bool TryEvaluate(string generationDir, TrigramExpression query, out IReadOnlySet<int> candidateContentIds)
    {
        candidateContentIds = System.Collections.Immutable.ImmutableHashSet<int>.Empty;
        if (string.IsNullOrEmpty(generationDir) || query is null)
        {
            return false;
        }

        try
        {
            string contentBin = Path.Combine(generationDir, ContentIndexGenerationSerializer.ContentFile);
            if (!File.Exists(contentBin))
            {
                return false;
            }

            byte[] rpn = TrigramQueryRpn.Encode(query);

            using var cts = new CancellationTokenSource(_timeout);
            // One-shot bridge at B0 (off the UI thread); the per-path hot loop stays fully synchronous.
            IndexWorkerQueryResult result = _client
                .QueryContentBinAsync(contentBin, rpn, cts.Token)
                .GetAwaiter()
                .GetResult();

            if (!result.Success)
            {
                return false;
            }

            var set = new HashSet<int>(result.Candidates.Length);
            foreach (int id in result.Candidates)
            {
                set.Add(id);
            }

            candidateContentIds = set;
            return true;
        }
        catch
        {
            // Any failure (worker missing/crashed, bad checksum, timeout, …) → in-process fallback.
            return false;
        }
    }
}
