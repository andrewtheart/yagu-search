using System;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for the freshness data types (plan §3.5): USN checkpoint continuity and the monotonic
/// dirty-content set, plus the versioned <see cref="IndexManifest"/> (structural trust + JSON round-trip).
/// </summary>
public sealed class IndexFreshnessAndManifestTests
{
    // ─────────────────────────── UsnCheckpoint ───────────────────────────

    [Fact]
    public void UsnCheckpoint_ContinuesFrom_SameJournalNonDecreasing()
    {
        var start = new UsnCheckpoint(JournalId: 7, NextUsn: 100);
        Assert.True(new UsnCheckpoint(7, 100).ContinuesFrom(start)); // equal cursor is fine
        Assert.True(new UsnCheckpoint(7, 250).ContinuesFrom(start));
    }

    [Fact]
    public void UsnCheckpoint_DoesNotContinue_OnJournalChangeOrRewind()
    {
        var start = new UsnCheckpoint(7, 100);
        Assert.False(new UsnCheckpoint(8, 250).ContinuesFrom(start)); // journal recreated
        Assert.False(new UsnCheckpoint(7, 50).ContinuesFrom(start));  // cursor rewound (wrap/gap)
    }

    // ─────────────────────────── DirtyContentSet ───────────────────────────

    [Fact]
    public void DirtyContentSet_IsMonotonic()
    {
        var set = new DirtyContentSet();
        Assert.False(set.IsDirty(5));
        set.MarkDirty(5);
        set.MarkDirty(5); // idempotent
        Assert.True(set.IsDirty(5));
        Assert.Equal(1, set.Count);
    }

    [Fact]
    public void DirtyContentSet_MergeFrom_Unions()
    {
        var a = new DirtyContentSet();
        a.MarkDirty(1);
        var b = new DirtyContentSet();
        b.MarkDirty(2);
        a.MergeFrom(b);
        Assert.True(a.IsDirty(1));
        Assert.True(a.IsDirty(2));
        Assert.Equal(2, a.Count);
        // Merging never clears b's absence of 1.
        Assert.False(b.IsDirty(1));
    }

    [Fact]
    public void DirtyContentSet_Snapshot_IsIndependentCopy()
    {
        var set = new DirtyContentSet();
        set.MarkDirty(9);
        var snap = set.Snapshot();
        set.MarkDirty(10);
        Assert.Contains(9L, snap);
        Assert.DoesNotContain(10L, snap); // snapshot is a copy
    }

    // ─────────────────────────── IndexManifest ───────────────────────────

    [Fact]
    public void Manifest_Defaults_AreCurrentVersions()
    {
        var manifest = new IndexManifest();
        Assert.Equal(IndexManifest.CurrentFormatVersion, manifest.IndexFormatVersion);
        Assert.Equal(ContentRepresentation.Version, manifest.ContentRepresentationVersion);
        Assert.Equal(IndexStructuralVerdict.Trusted, manifest.EvaluateStructural());
    }

    [Fact]
    public void Manifest_IncompatibleFormat_IsUntrusted()
    {
        var manifest = new IndexManifest { IndexFormatVersion = IndexManifest.CurrentFormatVersion + 1 };
        Assert.Equal(IndexStructuralVerdict.IncompatibleFormat, manifest.EvaluateStructural());
    }

    [Fact]
    public void Manifest_IncompatibleRepresentation_IsUntrusted()
    {
        var manifest = new IndexManifest { ContentRepresentationVersion = ContentRepresentation.Version + 1 };
        Assert.Equal(IndexStructuralVerdict.IncompatibleRepresentation, manifest.EvaluateStructural());
    }

    [Fact]
    public void Manifest_SerializeDeserialize_RoundTrips()
    {
        var manifest = new IndexManifest
        {
            ScopeId = "abc123",
            VolumeIdentity = "vol-guid",
            NormalizedRootPath = @"C:\src\Yagu",
            FreshnessCheckpoint = new UsnCheckpoint(42, 9999),
            ContentCount = 1234,
            AliasCount = 1300,
            BuiltUtc = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero),
            PlannerSemanticsVersion = 3,
        };

        string json = manifest.Serialize();
        var loaded = IndexManifest.Deserialize(json);

        Assert.NotNull(loaded);
        Assert.Equal(manifest, loaded); // record value equality
        Assert.Equal(new UsnCheckpoint(42, 9999), loaded!.FreshnessCheckpoint);
    }

    [Fact]
    public void Manifest_Deserialize_InvalidOrEmpty_ReturnsNull()
    {
        Assert.Null(IndexManifest.Deserialize(""));
        Assert.Null(IndexManifest.Deserialize("   "));
        Assert.Null(IndexManifest.Deserialize("{ not valid json"));
    }
}
