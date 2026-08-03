namespace Yagu.Services.Index;

/// <summary>
/// A monotone boolean expression over <see cref="Trigram"/>s — an AND/OR tree with no negation
/// (plan §4). Produced by <see cref="TrigramQueryPlanner"/> as a <em>required-superset</em> filter:
/// if <see cref="Evaluate"/> returns <c>false</c> for a document's trigram set, that document
/// cannot match the query and may be pruned; every document that truly matches evaluates to
/// <c>true</c>. <see cref="All"/> means "no constraint" (match anything) and <see cref="None"/>
/// means "matches nothing".
/// </summary>
public sealed class TrigramExpression
{
    public enum NodeKind
    {
        /// <summary>No constraint — evaluates true for every document (an ineligible/dropped factor).</summary>
        All,
        /// <summary>Unsatisfiable — evaluates false for every document.</summary>
        None,
        /// <summary>A single required trigram.</summary>
        Trigram,
        /// <summary>Conjunction: every child must hold.</summary>
        And,
        /// <summary>Disjunction: at least one child must hold.</summary>
        Or,
    }

    public NodeKind Kind { get; }

    /// <summary>The required trigram when <see cref="Kind"/> is <see cref="NodeKind.Trigram"/>.</summary>
    public Trigram Trigram { get; }

    /// <summary>Operands when <see cref="Kind"/> is <see cref="NodeKind.And"/>/<see cref="NodeKind.Or"/>.</summary>
    public IReadOnlyList<TrigramExpression> Children { get; }

    private TrigramExpression(NodeKind kind, Trigram trigram, IReadOnlyList<TrigramExpression> children)
    {
        Kind = kind;
        Trigram = trigram;
        Children = children;
    }

    /// <summary>The "match anything" sentinel (no required trigram).</summary>
    public static readonly TrigramExpression All = new(NodeKind.All, default, Array.Empty<TrigramExpression>());

    /// <summary>The "matches nothing" sentinel.</summary>
    public static readonly TrigramExpression None = new(NodeKind.None, default, Array.Empty<TrigramExpression>());

    /// <summary>A leaf requiring the given trigram to be present.</summary>
    public static TrigramExpression OfTrigram(Trigram trigram)
        => new(NodeKind.Trigram, trigram, Array.Empty<TrigramExpression>());

    /// <summary>Conjunction of two expressions, with All/None simplification and flattening.</summary>
    public static TrigramExpression And(TrigramExpression left, TrigramExpression right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.Kind == NodeKind.None || right.Kind == NodeKind.None) return None;
        if (left.Kind == NodeKind.All) return right;
        if (right.Kind == NodeKind.All) return left;

        var children = new List<TrigramExpression>();
        Flatten(NodeKind.And, left, children);
        Flatten(NodeKind.And, right, children);
        return Combine(NodeKind.And, children);
    }

    /// <summary>Disjunction of two expressions, with All/None simplification and flattening.</summary>
    public static TrigramExpression Or(TrigramExpression left, TrigramExpression right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.Kind == NodeKind.All || right.Kind == NodeKind.All) return All;
        if (left.Kind == NodeKind.None) return right;
        if (right.Kind == NodeKind.None) return left;

        var children = new List<TrigramExpression>();
        Flatten(NodeKind.Or, left, children);
        Flatten(NodeKind.Or, right, children);
        return Combine(NodeKind.Or, children);
    }

    private static void Flatten(NodeKind kind, TrigramExpression node, List<TrigramExpression> into)
    {
        if (node.Kind == kind)
            into.AddRange(node.Children);
        else
            into.Add(node);
    }

    private static TrigramExpression Combine(NodeKind kind, List<TrigramExpression> children)
    {
        // De-duplicate identical trigram leaves; keep composite children as-is.
        var seenTrigrams = new HashSet<Trigram>();
        var result = new List<TrigramExpression>(children.Count);
        foreach (var child in children)
        {
            if (child.Kind == NodeKind.Trigram)
            {
                if (seenTrigrams.Add(child.Trigram))
                    result.Add(child);
            }
            else
            {
                result.Add(child);
            }
        }

        if (result.Count == 1)
            return result[0];
        return new TrigramExpression(kind, default, result);
    }

    /// <summary>
    /// Evaluates the expression against the trigram set of a candidate document. A <c>false</c>
    /// result proves the document cannot match the original query.
    /// </summary>
    public bool Evaluate(IReadOnlySet<Trigram> present)
    {
        ArgumentNullException.ThrowIfNull(present);
        return Kind switch
        {
            NodeKind.None => false,
            NodeKind.Trigram => present.Contains(Trigram),
            NodeKind.And => EvaluateAnd(present),
            NodeKind.Or => EvaluateOr(present),
            _ => true,
        };
    }

    private bool EvaluateAnd(IReadOnlySet<Trigram> present)
    {
        foreach (var child in Children)
        {
            if (!child.Evaluate(present))
                return false;
        }
        return true;
    }

    private bool EvaluateOr(IReadOnlySet<Trigram> present)
    {
        foreach (var child in Children)
        {
            if (child.Evaluate(present))
                return true;
        }
        return false;
    }

    /// <summary>All distinct trigrams referenced anywhere in the expression (diagnostics/tests).</summary>
    public IReadOnlySet<Trigram> CollectTrigrams()
    {
        var set = new HashSet<Trigram>();
        CollectTrigrams(set);
        return set;
    }

    private void CollectTrigrams(HashSet<Trigram> into)
    {
        if (Kind == NodeKind.Trigram)
            into.Add(Trigram);
        foreach (var child in Children)
            child.CollectTrigrams(into);
    }
}
