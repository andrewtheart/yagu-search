namespace Yagu.Services.Index;

/// <summary>
/// Encodes a planned <see cref="TrigramExpression"/> as a compact post-order (RPN) byte stream so it can
/// cross the managed↔native boundary to the isolated index worker (plan §3.3/§5) without reconstructing
/// the C# object graph on the Rust side. The Rust <c>qg_index_evaluate</c> reader interprets the same
/// opcodes. It is the wire form of the query; the posting semantics themselves live in
/// <see cref="TrigramPostingIndex"/> (managed reference) and the Rust <c>PostingIndex</c> (worker).
///
/// <para>Opcodes (little-endian payloads):</para>
/// <list type="bullet">
///   <item><c>0</c> — All (match anything)</item>
///   <item><c>1</c> — None (match nothing)</item>
///   <item><c>2</c> + <c>u32</c> — a required trigram (its packed 24-bit value)</item>
///   <item><c>3</c> + <c>u16</c> — And of the top <c>N</c> post-order operands</item>
///   <item><c>4</c> + <c>u16</c> — Or of the top <c>N</c> post-order operands</item>
/// </list>
/// </summary>
public static class TrigramQueryRpn
{
    public const byte OpAll = 0;
    public const byte OpNone = 1;
    public const byte OpTrigram = 2;
    public const byte OpAnd = 3;
    public const byte OpOr = 4;

    /// <summary>Encodes <paramref name="expression"/> to its post-order byte stream.</summary>
    public static byte[] Encode(TrigramExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var buffer = new List<byte>();
        EncodeNode(expression, buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Decodes a post-order RPN byte stream (produced by <see cref="Encode"/>) back into a
    /// <see cref="TrigramExpression"/>. Used by the out-of-process query worker so it can evaluate the
    /// candidate set from the wire query itself — over its memory-mapped v3 postings — rather than the host
    /// having to compute and ship candidate ids (which would require the host to hold the index). The result
    /// is <b>semantically</b> equivalent to the encoded expression (the And/Or factories flatten/dedup, so a
    /// candidate evaluation is byte-identical, even if the object graph differs). Throws
    /// <see cref="FormatException"/> on a truncated payload, an unknown opcode, or a stream that does not
    /// reduce to exactly one expression — the worker treats a decode failure as "not query-ready" (live-scan).
    /// </summary>
    public static TrigramExpression Decode(ReadOnlySpan<byte> rpn)
    {
        var stack = new Stack<TrigramExpression>();
        int i = 0;
        while (i < rpn.Length)
        {
            byte op = rpn[i++];
            switch (op)
            {
                case OpAll:
                    stack.Push(TrigramExpression.All);
                    break;
                case OpNone:
                    stack.Push(TrigramExpression.None);
                    break;
                case OpTrigram:
                    stack.Push(TrigramExpression.OfTrigram(Trigram.FromPacked(ReadU32(rpn, ref i))));
                    break;
                case OpAnd:
                case OpOr:
                {
                    int n = ReadU16(rpn, ref i);
                    if (stack.Count < n)
                        throw new FormatException($"Malformed RPN: {op} needs {n} operands but only {stack.Count} are available.");
                    // Pop N operands and restore their original (post-order) order before combining.
                    var operands = new TrigramExpression[n];
                    for (int k = n - 1; k >= 0; k--)
                        operands[k] = stack.Pop();
                    stack.Push(Combine(op == OpAnd, operands));
                    break;
                }
                default:
                    throw new FormatException($"Unknown RPN opcode {op}.");
            }
        }

        if (stack.Count != 1)
            throw new FormatException($"Malformed RPN: reduced to {stack.Count} expressions (expected exactly 1).");
        return stack.Pop();
    }

    // Folds N operands with the simplifying binary And/Or factories (flatten + dedup). Semantics-preserving,
    // so the resulting candidate evaluation matches the original expression exactly.
    private static TrigramExpression Combine(bool isAnd, TrigramExpression[] operands)
    {
        if (operands.Length == 0)
            return isAnd ? TrigramExpression.All : TrigramExpression.None;
        TrigramExpression acc = operands[0];
        for (int k = 1; k < operands.Length; k++)
            acc = isAnd ? TrigramExpression.And(acc, operands[k]) : TrigramExpression.Or(acc, operands[k]);
        return acc;
    }

    private static uint ReadU32(ReadOnlySpan<byte> rpn, ref int i)
    {
        if (i + 4 > rpn.Length)
            throw new FormatException("Malformed RPN: truncated u32 payload.");
        uint value = (uint)(rpn[i] | (rpn[i + 1] << 8) | (rpn[i + 2] << 16) | (rpn[i + 3] << 24));
        i += 4;
        return value;
    }

    private static int ReadU16(ReadOnlySpan<byte> rpn, ref int i)
    {
        if (i + 2 > rpn.Length)
            throw new FormatException("Malformed RPN: truncated u16 payload.");
        int value = rpn[i] | (rpn[i + 1] << 8);
        i += 2;
        return value;
    }

    private static void EncodeNode(TrigramExpression node, List<byte> buffer)
    {
        switch (node.Kind)
        {
            case TrigramExpression.NodeKind.All:
                buffer.Add(OpAll);
                break;
            case TrigramExpression.NodeKind.None:
                buffer.Add(OpNone);
                break;
            case TrigramExpression.NodeKind.Trigram:
                buffer.Add(OpTrigram);
                WriteU32(buffer, node.Trigram.Value);
                break;
            case TrigramExpression.NodeKind.And:
            case TrigramExpression.NodeKind.Or:
                foreach (TrigramExpression child in node.Children)
                    EncodeNode(child, buffer);
                buffer.Add(node.Kind == TrigramExpression.NodeKind.And ? OpAnd : OpOr);
                WriteU16(buffer, checked((ushort)node.Children.Count));
                break;
        }
    }

    private static void WriteU32(List<byte> buffer, uint value)
    {
        buffer.Add((byte)value);
        buffer.Add((byte)(value >> 8));
        buffer.Add((byte)(value >> 16));
        buffer.Add((byte)(value >> 24));
    }

    private static void WriteU16(List<byte> buffer, ushort value)
    {
        buffer.Add((byte)value);
        buffer.Add((byte)(value >> 8));
    }
}
