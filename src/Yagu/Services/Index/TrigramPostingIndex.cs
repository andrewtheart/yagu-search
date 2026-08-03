namespace Yagu.Services.Index;

/// <summary>
/// An in-memory trigram posting-list index (plan §3.1) — the managed reference implementation of the
/// "index lookup → candidate content-id set" step. It maps each <see cref="Trigram"/> to the sorted
/// list of document ids that contain it, then evaluates a monotone <see cref="TrigramExpression"/>
/// into a <em>candidate superset</em> of document ids (rare posting lists intersected first, per §5).
/// <para>
/// This is deliberately architecture-neutral: v1 production ultimately builds and queries postings in
/// the isolated Rust <c>Yagu.IndexWorker</c> (plan §3.3), but the same posting semantics are validated
/// here and used as the differential oracle. It never serves content — retained candidates are always
/// re-verified live by <see cref="ContentSearcher"/>.
/// </para>
/// </summary>
public sealed class TrigramPostingIndex
{
    private readonly Dictionary<Trigram, int[]> _postings;
    private readonly int[] _allDocs;

    private TrigramPostingIndex(Dictionary<Trigram, int[]> postings, int documentCount)
    {
        _postings = postings;
        DocumentCount = documentCount;
        _allDocs = new int[documentCount];
        for (int i = 0; i < documentCount; i++)
            _allDocs[i] = i;
    }

    /// <summary>Number of indexed documents. Document ids are the contiguous range <c>[0, DocumentCount)</c>.</summary>
    public int DocumentCount { get; }

    /// <summary>Number of distinct trigrams with a non-empty posting list.</summary>
    public int TrigramCount => _postings.Count;

    /// <summary>
    /// Builds an index from documents in order; the document id is the position in
    /// <paramref name="documents"/>. Each element is that document's distinct trigram set (as produced
    /// by <see cref="ContentRepresentation.Classify"/>).
    /// </summary>
    public static TrigramPostingIndex Build(IReadOnlyList<IReadOnlyCollection<Trigram>> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        var builders = new Dictionary<Trigram, List<int>>();
        for (int docId = 0; docId < documents.Count; docId++)
        {
            var trigrams = documents[docId];
            if (trigrams is null)
                continue;
            foreach (Trigram t in trigrams)
            {
                if (!builders.TryGetValue(t, out var list))
                {
                    list = new List<int>();
                    builders[t] = list;
                }
                // Documents are visited in ascending id order, so posting lists stay sorted without
                // an explicit sort. Guard against a caller passing a trigram set with duplicates.
                if (list.Count == 0 || list[^1] != docId)
                    list.Add(docId);
            }
        }

        var postings = new Dictionary<Trigram, int[]>(builders.Count);
        foreach (var (trigram, list) in builders)
            postings[trigram] = list.ToArray();

        return new TrigramPostingIndex(postings, documents.Count);
    }

    /// <summary>
    /// Builds the posting index by streaming a serialized <c>content.bin</c> body directly into posting
    /// lists, WITHOUT first materializing the per-document trigram collections. This is the QUERY-mode load
    /// path (the documents themselves are only needed for compaction/serialization): it caps the
    /// deserialize's transient allocation at one document's trigrams instead of the whole corpus, which for
    /// a large layered index (base + many segments) avoids churning multiple GB of short-lived garbage every
    /// time the index is opened. The body layout matches
    /// <c>ContentIndexGenerationSerializer.SerializeContent</c>: int32 <c>docCount</c>, then per document
    /// [int32 <c>trigramCount</c>, uint32×N] (little-endian). Throws <see cref="System.IO.InvalidDataException"/>
    /// on a malformed/truncated body; produces byte-identical postings to <see cref="Build"/> for the same data.
    /// </summary>
    public static TrigramPostingIndex BuildFromContentBody(
        ReadOnlySpan<byte> body,
        out int documentCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int offset = 0;
        int docCount = ReadInt32(body, ref offset);
        if (docCount < 0)
            throw new System.IO.InvalidDataException("Negative document count.");

        var builders = new Dictionary<Trigram, List<int>>();
        for (int docId = 0; docId < docCount; docId++)
        {
            if ((docId & 0xFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            int trigramCount = ReadInt32(body, ref offset);
            if (trigramCount < 0)
                throw new System.IO.InvalidDataException("Negative trigram count.");
            for (int j = 0; j < trigramCount; j++)
            {
                if ((j & 0xFFF) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                Trigram t = Trigram.FromPacked(ReadUInt32(body, ref offset));
                if (!builders.TryGetValue(t, out var list))
                {
                    list = new List<int>();
                    builders[t] = list;
                }
                // Documents visited in ascending id order → posting lists stay sorted; guard duplicates.
                if (list.Count == 0 || list[^1] != docId)
                    list.Add(docId);
            }
        }

        var postings = new Dictionary<Trigram, int[]>(builders.Count);
        int postingNumber = 0;
        foreach (var (trigram, list) in builders)
        {
            if ((postingNumber++ & 0x3FF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            postings[trigram] = list.ToArray();
        }

        documentCount = docCount;
        return new TrigramPostingIndex(postings, docCount);
    }

    private static int ReadInt32(ReadOnlySpan<byte> body, ref int offset)
    {
        if (offset + 4 > body.Length)
            throw new System.IO.InvalidDataException("Truncated content body.");
        int value = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(body.Slice(offset, 4));
        offset += 4;
        return value;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> body, ref int offset)
    {
        if (offset + 4 > body.Length)
            throw new System.IO.InvalidDataException("Truncated content body.");
        uint value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(offset, 4));
        offset += 4;
        return value;
    }

    /// <summary>The sorted posting list (document ids) for a trigram, or empty if none contain it.</summary>
    public IReadOnlyList<int> GetPosting(Trigram trigram)
        => _postings.TryGetValue(trigram, out var arr) ? arr : Array.Empty<int>();

    /// <summary>Document frequency of a trigram (posting-list length), used to order AND intersections.</summary>
    public int DocumentFrequency(Trigram trigram)
        => _postings.TryGetValue(trigram, out var arr) ? arr.Length : 0;

    /// <summary>
    /// Evaluates a planned trigram query into the sorted candidate document-id list. The result is a
    /// superset of the documents that can match the original search query; each must still be verified
    /// live. <see cref="TrigramExpression.NodeKind.All"/> yields every document (no constraint) and
    /// <see cref="TrigramExpression.NodeKind.None"/> yields none.
    /// </summary>
    public IReadOnlyList<int> Evaluate(TrigramExpression query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Kind == TrigramExpression.NodeKind.All)
            return _allDocs;
        if (query.Kind == TrigramExpression.NodeKind.None)
            return Array.Empty<int>();
        if (query.Kind == TrigramExpression.NodeKind.Trigram)
            return GetPosting(query.Trigram);
        return query.Kind == TrigramExpression.NodeKind.And
            ? EvaluateAnd(query.Children)
            : EvaluateOr(query.Children);
    }

    /// <summary>Evaluates a query into a candidate document-id set (order-independent membership).</summary>
    public IReadOnlySet<int> EvaluateSet(TrigramExpression query)
        => new HashSet<int>(Evaluate(query));

    private IReadOnlyList<int> EvaluateAnd(IReadOnlyList<TrigramExpression> children)
    {
        // Intersect the rarest posting lists first so a single common trigram can't blow up the
        // working set (plan §5 / §11 "very common trigrams").
        var lists = new List<IReadOnlyList<int>>(children.Count);
        foreach (var child in children)
        {
            IReadOnlyList<int> list = Evaluate(child);
            if (list.Count == 0)
                return Array.Empty<int>(); // an empty conjunct makes the whole AND empty
            lists.Add(list);
        }
        lists.Sort((a, b) => a.Count.CompareTo(b.Count));

        IReadOnlyList<int> accumulator = lists[0];
        for (int i = 1; i < lists.Count && accumulator.Count > 0; i++)
            accumulator = Intersect(accumulator, lists[i]);
        return accumulator;
    }

    private IReadOnlyList<int> EvaluateOr(IReadOnlyList<TrigramExpression> children)
    {
        IReadOnlyList<int> accumulator = Array.Empty<int>();
        foreach (var child in children)
            accumulator = Union(accumulator, Evaluate(child));
        return accumulator;
    }

    internal static List<int> Intersect(IReadOnlyList<int> a, IReadOnlyList<int> b)
    {
        var result = new List<int>(Math.Min(a.Count, b.Count));
        int i = 0, j = 0;
        while (i < a.Count && j < b.Count)
        {
            int x = a[i], y = b[j];
            if (x == y) { result.Add(x); i++; j++; }
            else if (x < y) i++;
            else j++;
        }
        return result;
    }

    internal static IReadOnlyList<int> Union(IReadOnlyList<int> a, IReadOnlyList<int> b)
    {
        if (a.Count == 0) return b;
        if (b.Count == 0) return a;
        var result = new List<int>(a.Count + b.Count);
        int i = 0, j = 0;
        while (i < a.Count && j < b.Count)
        {
            int x = a[i], y = b[j];
            if (x == y) { result.Add(x); i++; j++; }
            else if (x < y) { result.Add(x); i++; }
            else { result.Add(y); j++; }
        }
        while (i < a.Count) result.Add(a[i++]);
        while (j < b.Count) result.Add(b[j++]);
        return result;
    }
}
