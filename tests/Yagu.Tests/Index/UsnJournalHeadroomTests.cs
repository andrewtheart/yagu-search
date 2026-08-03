using Yagu.Services.Index;

namespace Yagu.Tests;

/// <summary>Unit tests for the pure USN-journal headroom decision (<see cref="UsnJournalHeadroom"/>) that
/// drives the proactive re-anchor trigger.</summary>
public sealed class UsnJournalHeadroomTests
{
    private const ulong JournalId = 0x0000_0000_0000_1000UL;

    private static UsnJournalInfo Journal(long firstUsn, long nextUsn, ulong id = JournalId)
        => new(id, firstUsn, nextUsn, LowestValidUsn: 0);

    private static UsnCheckpoint Checkpoint(long nextUsn, ulong id = JournalId) => new(id, nextUsn);

    [Fact]
    public void FreshCheckpoint_NearNextUsn_HasHighHeadroom_AndDoesNotTrigger()
    {
        // window [1000, 2000); checkpoint at 1900 → 90% survives.
        UsnHeadroomVerdict v = UsnJournalHeadroom.Evaluate(Journal(1000, 2000), Checkpoint(1900));

        Assert.False(v.ShouldRefreshSoon);
        Assert.False(v.CheckpointPurged);
        Assert.False(v.JournalIdMismatch);
        Assert.Equal(0.9, v.SurvivalFraction, precision: 6);
    }

    [Fact]
    public void CheckpointInOldestQuarter_TriggersRefresh()
    {
        // checkpoint at 1100 → 10% survives, below the default 25% threshold.
        UsnHeadroomVerdict v = UsnJournalHeadroom.Evaluate(Journal(1000, 2000), Checkpoint(1100));

        Assert.True(v.ShouldRefreshSoon);
        Assert.False(v.CheckpointPurged);
        Assert.Equal(0.1, v.SurvivalFraction, precision: 6);
    }

    [Fact]
    public void CheckpointBelowFirstUsn_IsPurged_AndTriggers()
    {
        // checkpoint at 500 is below FirstUsn (1000) → the records a search needs are gone.
        UsnHeadroomVerdict v = UsnJournalHeadroom.Evaluate(Journal(1000, 2000), Checkpoint(500));

        Assert.True(v.CheckpointPurged);
        Assert.True(v.ShouldRefreshSoon);
        Assert.False(v.JournalIdMismatch);
        Assert.Equal(0.0, v.SurvivalFraction, precision: 6);
    }

    [Fact]
    public void JournalIdMismatch_TriggersImmediately_RegardlessOfCursors()
    {
        // Journal was recreated: the checkpoint's id no longer matches the volume's current journal id.
        UsnHeadroomVerdict v = UsnJournalHeadroom.Evaluate(
            Journal(1000, 2000, id: 0xAAAA), Checkpoint(1900, id: JournalId));

        Assert.True(v.JournalIdMismatch);
        Assert.True(v.ShouldRefreshSoon);
        Assert.False(v.CheckpointPurged);
        Assert.Equal(0.0, v.SurvivalFraction, precision: 6);
    }

    [Fact]
    public void EmptyWindow_WithMatchingIdAndInRangeCheckpoint_IsHealthy()
    {
        // No records yet (FirstUsn == NextUsn): nothing has been purged, so the checkpoint is safe.
        UsnHeadroomVerdict v = UsnJournalHeadroom.Evaluate(Journal(2000, 2000), Checkpoint(2000));

        Assert.False(v.ShouldRefreshSoon);
        Assert.False(v.CheckpointPurged);
        Assert.Equal(1.0, v.SurvivalFraction, precision: 6);
    }

    [Fact]
    public void AtThreshold_DoesNotTrigger_StrictlyBelow()
    {
        // Exactly 25% survives — the trigger is strictly-below, so this must NOT fire.
        UsnHeadroomVerdict v = UsnJournalHeadroom.Evaluate(Journal(1000, 2000), Checkpoint(1250));

        Assert.Equal(0.25, v.SurvivalFraction, precision: 6);
        Assert.False(v.ShouldRefreshSoon);
    }

    [Fact]
    public void CustomThreshold_IsHonored()
    {
        // 40% survives; below a 0.5 threshold → triggers, but not the default 0.25.
        UsnJournalInfo journal = Journal(1000, 2000);
        UsnCheckpoint cp = Checkpoint(1400);

        Assert.True(UsnJournalHeadroom.Evaluate(journal, cp, refreshBelowFraction: 0.5).ShouldRefreshSoon);
        Assert.False(UsnJournalHeadroom.Evaluate(journal, cp).ShouldRefreshSoon);
    }

    [Fact]
    public void CheckpointAheadOfNextUsn_ClampsFractionToOne()
    {
        // A checkpoint beyond the journal's NextUsn (e.g. captured after the query) clamps to full headroom.
        UsnHeadroomVerdict v = UsnJournalHeadroom.Evaluate(Journal(1000, 2000), Checkpoint(3000));

        Assert.Equal(1.0, v.SurvivalFraction, precision: 6);
        Assert.False(v.ShouldRefreshSoon);
    }
}
