using System.Collections.Generic;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for <see cref="ArchiveEntryIdentity"/> (plan §7 Phase 4): identity is the ordered entry chain
/// plus exact name, ordinal among duplicates, uncompressed size, and CRC. Any of those changing yields a
/// different digest so a different archive entry is never mistaken for a fresh one, and the chain/name
/// boundary cannot be spoofed by a crafted name. Equality is by digest only.
/// </summary>
public sealed class ArchiveEntryIdentityTests
{
    private static ArchiveEntryIdentity Entry(
        IEnumerable<string>? chain = null,
        string name = "readme.txt",
        int ordinal = 0,
        long size = 1024,
        uint? crc = 0xDEADBEEF)
        => new(chain ?? ["outer.zip", "inner.zip"], name, ordinal, size, crc);

    [Fact]
    public void SameFields_ProduceEqualIdentities()
    {
        ArchiveEntryIdentity a = Entry();
        ArchiveEntryIdentity b = Entry();

        Assert.Equal(a.Digest, b.Digest);
        Assert.True(a.Matches(b));
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void NullInputs_CanonicalizeToEmpty_AndEqualsHandlesForeignTypes()
    {
        var id = new ArchiveEntryIdentity(
            entryChain: new string?[] { null, "inner.zip" }!,
            entryName: null!,
            ordinal: 0,
            uncompressedSize: -1);
        Assert.NotNull(id.Digest);
        Assert.Equal(string.Empty, id.EntryName);
        Assert.Equal(2, id.EntryChain.Count);
        Assert.Equal(string.Empty, id.EntryChain[0]); // null chain element coalesced

        // A null entryChain collection defaults to empty.
        Assert.Empty(new ArchiveEntryIdentity(null!, "a", 0, 1).EntryChain);

        // Equality/Matches reject nulls and foreign types.
        Assert.False(id.Equals(null));
        Assert.False(id.Equals("not an identity"));
        Assert.False(id.Matches(null));
    }

    [Theory]
    [InlineData("chain")]
    [InlineData("name")]
    [InlineData("ordinal")]
    [InlineData("size")]
    [InlineData("crc")]
    [InlineData("crc-null")]
    public void AnyFieldChange_ChangesDigest(string what)
    {
        ArchiveEntryIdentity baseline = Entry();
        ArchiveEntryIdentity changed = what switch
        {
            "chain" => Entry(chain: ["outer.zip", "other.zip"]),
            "name" => Entry(name: "README.TXT"),
            "ordinal" => Entry(ordinal: 1),
            "size" => Entry(size: 2048),
            "crc" => Entry(crc: 0x00000000),
            "crc-null" => Entry(crc: null),
            _ => baseline,
        };

        Assert.NotEqual(baseline.Digest, changed.Digest);
        Assert.False(baseline.Matches(changed));
    }

    [Fact]
    public void ChainNameBoundary_CannotBeSpoofed()
    {
        // ["a", "b"] name "c"  must NOT collide with  ["a"] name "b/c" nor ["a", "b", "c"] name "".
        ArchiveEntryIdentity split = new(["a", "b"], "c", 0, 10, null);
        ArchiveEntryIdentity a = new(["a"], "b/c", 0, 10, null);
        ArchiveEntryIdentity b = new(["a", "b", "c"], "", 0, 10, null);

        Assert.NotEqual(split.Digest, a.Digest);
        Assert.NotEqual(split.Digest, b.Digest);
    }

    [Fact]
    public void Matches_Null_IsFalse()
        => Assert.False(Entry().Matches(null));

    [Fact]
    public void UsableAsHashSetKey_ByDigest()
    {
        var set = new HashSet<ArchiveEntryIdentity> { Entry(), Entry() };
        Assert.Single(set);
    }

    [Fact]
    public void EmptyChain_IsHandled()
    {
        ArchiveEntryIdentity a = new([], "top.txt", 0, 5, null);
        Assert.NotEmpty(a.Digest);
        Assert.Empty(a.EntryChain);
    }
}
