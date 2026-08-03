using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Unit tests for <see cref="TrigramQueryRpn"/> — the post-order (RPN) wire encoding of a planned
/// trigram query sent to the index worker (plan §3.3/§5). These are pure and run without the native
/// DLL; the byte stream's semantics are additionally golden-parity-checked against the Rust reader in
/// <see cref="ContentIndexRustParityTests"/>.
/// </summary>
public sealed class TrigramQueryRpnTests
{
    [Fact]
    public void Encode_All_And_None_AreSingleOpcodes()
    {
        Assert.Equal(new byte[] { TrigramQueryRpn.OpAll }, TrigramQueryRpn.Encode(TrigramExpression.All));
        Assert.Equal(new byte[] { TrigramQueryRpn.OpNone }, TrigramQueryRpn.Encode(TrigramExpression.None));
    }

    [Fact]
    public void Encode_Trigram_IsOpcodePlusLittleEndianValue()
    {
        var t = new Trigram((byte)'f', (byte)'o', (byte)'o'); // packed 0x00_666F6F
        byte[] rpn = TrigramQueryRpn.Encode(TrigramExpression.OfTrigram(t));

        Assert.Equal(TrigramQueryRpn.OpTrigram, rpn[0]);
        uint value = (uint)(rpn[1] | (rpn[2] << 8) | (rpn[3] << 16) | (rpn[4] << 24));
        Assert.Equal(t.Value, value);
        Assert.Equal(5, rpn.Length);
    }

    [Fact]
    public void Encode_And_EmitsChildrenThenOpAndCount()
    {
        var a = TrigramExpression.OfTrigram(new Trigram(1, 2, 3));
        var b = TrigramExpression.OfTrigram(new Trigram(4, 5, 6));
        var and = TrigramExpression.And(a, b);
        Assert.Equal(TrigramExpression.NodeKind.And, and.Kind);

        byte[] rpn = TrigramQueryRpn.Encode(and);

        // [2 <a>] [2 <b>] [3 <count=2>]  → last three bytes are OpAnd + u16(2)
        Assert.Equal(TrigramQueryRpn.OpAnd, rpn[^3]);
        ushort count = (ushort)(rpn[^2] | (rpn[^1] << 8));
        Assert.Equal((ushort)and.Children.Count, count);
        Assert.Equal(TrigramQueryRpn.OpTrigram, rpn[0]);
    }

    [Fact]
    public void Encode_Or_UsesOrOpcode()
    {
        var or = TrigramExpression.Or(
            TrigramExpression.OfTrigram(new Trigram(1, 1, 1)),
            TrigramExpression.OfTrigram(new Trigram(2, 2, 2)));
        Assert.Equal(TrigramExpression.NodeKind.Or, or.Kind);

        byte[] rpn = TrigramQueryRpn.Encode(or);
        Assert.Equal(TrigramQueryRpn.OpOr, rpn[^3]);
    }

    // ── Decode (worker self-evaluates candidates from the wire query) ──

    [Fact]
    public void Decode_RoundTrips_All_None_Trigram()
    {
        Assert.Equal(TrigramExpression.NodeKind.All, TrigramQueryRpn.Decode(TrigramQueryRpn.Encode(TrigramExpression.All)).Kind);
        Assert.Equal(TrigramExpression.NodeKind.None, TrigramQueryRpn.Decode(TrigramQueryRpn.Encode(TrigramExpression.None)).Kind);

        var t = new Trigram((byte)'b', (byte)'a', (byte)'r');
        TrigramExpression decoded = TrigramQueryRpn.Decode(TrigramQueryRpn.Encode(TrigramExpression.OfTrigram(t)));
        Assert.Equal(TrigramExpression.NodeKind.Trigram, decoded.Kind);
        Assert.Equal(t.Value, decoded.Trigram.Value);
    }

    [Fact]
    public void Decode_IsSemanticallyEquivalentToTheEncodedQuery()
    {
        var t1 = new Trigram(1, 2, 3);
        var t2 = new Trigram(4, 5, 6);
        var t3 = new Trigram(7, 8, 9);
        var t4 = new Trigram(10, 11, 12);
        var queries = new[]
        {
            TrigramExpression.OfTrigram(t1),
            TrigramExpression.And(TrigramExpression.OfTrigram(t1), TrigramExpression.OfTrigram(t2)),
            TrigramExpression.Or(TrigramExpression.OfTrigram(t1), TrigramExpression.OfTrigram(t2)),
            TrigramExpression.And(TrigramExpression.OfTrigram(t1), TrigramExpression.Or(TrigramExpression.OfTrigram(t2), TrigramExpression.OfTrigram(t3))),
            TrigramExpression.Or(TrigramExpression.And(TrigramExpression.OfTrigram(t1), TrigramExpression.OfTrigram(t2)), TrigramExpression.OfTrigram(t3)),
        };
        var all = new[] { t1, t2, t3, t4 };

        foreach (TrigramExpression q in queries)
        {
            TrigramExpression decoded = TrigramQueryRpn.Decode(TrigramQueryRpn.Encode(q));
            for (int mask = 0; mask < 16; mask++)
            {
                var present = new System.Collections.Generic.HashSet<Trigram>();
                for (int b = 0; b < 4; b++)
                    if ((mask & (1 << b)) != 0)
                        present.Add(all[b]);
                Assert.Equal(q.Evaluate(present), decoded.Evaluate(present));
            }
        }
    }

    [Fact]
    public void Decode_ZeroArityAndOr_UseTheirBooleanIdentities()
    {
        TrigramExpression and = TrigramQueryRpn.Decode(new byte[] { TrigramQueryRpn.OpAnd, 0, 0 });
        TrigramExpression or = TrigramQueryRpn.Decode(new byte[] { TrigramQueryRpn.OpOr, 0, 0 });

        Assert.Same(TrigramExpression.All, and);
        Assert.Same(TrigramExpression.None, or);
    }

    [Fact]
    public void Decode_MalformedStreams_Throw()
    {
        Assert.Throws<System.FormatException>(() => TrigramQueryRpn.Decode(System.ReadOnlySpan<byte>.Empty));      // 0 operands
        Assert.Throws<System.FormatException>(() => TrigramQueryRpn.Decode(new byte[] { TrigramQueryRpn.OpTrigram, 1, 2 })); // truncated u32
        Assert.Throws<System.FormatException>(() => TrigramQueryRpn.Decode(new byte[] { TrigramQueryRpn.OpAnd, 0 }));        // truncated u16
        Assert.Throws<System.FormatException>(() => TrigramQueryRpn.Decode(new byte[] { TrigramQueryRpn.OpAnd, 2, 0 }));     // And needs 2, none available
        Assert.Throws<System.FormatException>(() => TrigramQueryRpn.Decode(new byte[] { 99 }));                              // unknown opcode
        Assert.Throws<System.FormatException>(() => TrigramQueryRpn.Decode(new byte[] { TrigramQueryRpn.OpAll, TrigramQueryRpn.OpAll })); // 2 operands remain
    }
}
