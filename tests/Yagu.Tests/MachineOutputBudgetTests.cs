using System.Text;
using Yagu.Services.Cli.MachineOutput;

namespace Yagu.Tests;

/// <summary>
/// Covers the per-field UTF-8 ceilings that make a machine-mode response bounded. A match COUNT bounds
/// nothing — one minified line can exceed a whole sensible response — so these are the real guard.
/// </summary>
public class MachineOutputBudgetTests
{
    [Fact]
    public void TruncateUtf8_ShortValue_IsReturnedUnchanged()
    {
        string result = MachineOutputBudget.TruncateUtf8("hello", 100, out bool truncated, out int originalBytes);

        Assert.Equal("hello", result);
        Assert.False(truncated);
        Assert.Equal(5, originalBytes);
    }

    [Fact]
    public void TruncateUtf8_NullValue_IsTreatedAsEmpty()
    {
        string result = MachineOutputBudget.TruncateUtf8(null, 10, out bool truncated, out int originalBytes);

        Assert.Equal(string.Empty, result);
        Assert.False(truncated);
        Assert.Equal(0, originalBytes);
    }

    [Fact]
    public void TruncateUtf8_LongAsciiValue_IsCutToTheByteCeiling()
    {
        string value = new('x', 1000);

        string result = MachineOutputBudget.TruncateUtf8(value, 64, out bool truncated, out int originalBytes);

        Assert.True(truncated);
        Assert.Equal(1000, originalBytes);
        Assert.Equal(64, Encoding.UTF8.GetByteCount(result));
    }

    [Fact]
    public void TruncateUtf8_NeverSplitsAMultiByteCodePoint()
    {
        // Each 'é' costs two UTF-8 bytes, so an odd ceiling must stop one byte short rather than
        // emitting half a code point.
        string value = new('é', 10);

        string result = MachineOutputBudget.TruncateUtf8(value, 5, out bool truncated, out _);

        Assert.True(truncated);
        Assert.Equal(4, Encoding.UTF8.GetByteCount(result));
        Assert.Equal("éé", result);
    }

    [Fact]
    public void TruncateUtf8_NeverSplitsASurrogatePair()
    {
        // U+1F600 is a 4-byte code point stored as a surrogate pair; a 5-byte ceiling fits exactly one.
        string value = string.Concat(Enumerable.Repeat("\U0001F600", 3));

        string result = MachineOutputBudget.TruncateUtf8(value, 5, out bool truncated, out _);

        Assert.True(truncated);
        Assert.Equal("\U0001F600", result);
        Assert.False(result.Any(char.IsHighSurrogate) && !result.Any(char.IsLowSurrogate));
    }

    [Fact]
    public void TruncateUtf8_ZeroCeiling_ReturnsTheValueUnchanged()
    {
        // A zero/negative ceiling means "unbounded" so a caller that disables one field cannot
        // accidentally blank it.
        string result = MachineOutputBudget.TruncateUtf8("abc", 0, out bool truncated, out _);

        Assert.Equal("abc", result);
        Assert.False(truncated);
    }

    [Fact]
    public void Create_ClampsEveryFieldIntoItsSupportedRange()
    {
        var budget = MachineOutputBudget.Create(
            maxLineBytes: 1,
            maxContextLineBytes: int.MaxValue,
            maxPathBytes: 1,
            maxContextLines: -5,
            maxRecords: 0,
            maxResponseBytes: 1);

        Assert.Equal(MachineOutputBudget.MinLineBytes, budget.MaxLineBytes);
        Assert.Equal(MachineOutputBudget.MaxLineBytesCeiling, budget.MaxContextLineBytes);
        Assert.Equal(MachineOutputBudget.MinPathBytes, budget.MaxPathBytes);
        Assert.Equal(MachineOutputBudget.MinContextLines, budget.MaxContextLines);
        Assert.Equal(MachineOutputBudget.MinRecords, budget.MaxRecords);
        Assert.Equal(MachineOutputBudget.MinResponseBytes, budget.MaxResponseBytes);
    }

    [Fact]
    public void Default_IsWithinEverySupportedRange()
    {
        var budget = MachineOutputBudget.Default;

        Assert.InRange(budget.MaxLineBytes, MachineOutputBudget.MinLineBytes, MachineOutputBudget.MaxLineBytesCeiling);
        Assert.InRange(budget.MaxContextLineBytes, MachineOutputBudget.MinLineBytes, MachineOutputBudget.MaxLineBytesCeiling);
        Assert.InRange(budget.MaxPathBytes, MachineOutputBudget.MinPathBytes, MachineOutputBudget.MaxPathBytesCeiling);
        Assert.InRange(budget.MaxContextLines, MachineOutputBudget.MinContextLines, MachineOutputBudget.MaxContextLinesCeiling);
        Assert.InRange(budget.MaxRecords, MachineOutputBudget.MinRecords, MachineOutputBudget.MaxRecordsCeiling);
        Assert.InRange(budget.MaxResponseBytes, MachineOutputBudget.MinResponseBytes, MachineOutputBudget.MaxResponseBytesCeiling);
    }

    [Fact]
    public void TheSmallestLegalBudget_StillLeavesRoomForARecordAtEveryFieldFloor()
    {
        // Create() scales the per-record fields down until they fit half the response ceiling, and gives
        // up once every field is at its floor. That give-up arm is only safe while the floors genuinely
        // fit the smallest legal response, so assert the arithmetic rather than trusting the comment.
        var budget = MachineOutputBudget.Create(
            MachineOutputBudget.MinLineBytes,
            MachineOutputBudget.MinLineBytes,
            MachineOutputBudget.MinPathBytes,
            MachineOutputBudget.MinContextLines,
            MachineOutputBudget.MinRecords,
            MachineOutputBudget.MinResponseBytes);

        Assert.Equal(MachineOutputBudget.MinLineBytes, budget.MaxLineBytes);
        Assert.Equal(MachineOutputBudget.MinLineBytes, budget.MaxContextLineBytes);
        Assert.Equal(MachineOutputBudget.MinPathBytes, budget.MaxPathBytes);

        long largestRecord = (long)budget.MaxLineBytes + budget.MaxPathBytes
            + ((long)budget.MaxContextLineBytes * budget.MaxContextLines);
        Assert.True(
            largestRecord <= budget.MaxResponseBytes / 2,
            $"a floor-sized record ({largestRecord:N0} bytes) no longer fits half of MinResponseBytes, "
            + "so the smallest legal budget would refuse its first match.");
    }

    [Fact]
    public void Create_ScalesAnOversizedPathAfterOtherFieldsReachTheirFloor()
    {
        var budget = MachineOutputBudget.Create(
            MachineOutputBudget.MinLineBytes,
            MachineOutputBudget.MinLineBytes,
            MachineOutputBudget.MaxPathBytesCeiling,
            MachineOutputBudget.MinContextLines,
            MachineOutputBudget.MinRecords,
            MachineOutputBudget.MinResponseBytes);

        Assert.Equal(MachineOutputBudget.MinLineBytes, budget.MaxLineBytes);
        Assert.Equal(MachineOutputBudget.MinLineBytes, budget.MaxContextLineBytes);
        Assert.True(budget.MaxPathBytes < MachineOutputBudget.MaxPathBytesCeiling);
        Assert.True(
            (long)budget.MaxLineBytes + budget.MaxPathBytes <= budget.MaxResponseBytes / 2);
    }

    [Fact]
    public void Shrink_SurrendersFieldsWorstValueFirstAndEventuallyReportsNoRoomLeft()
    {
        var budget = MachineOutputBudget.Create(
            maxLineBytes: MachineOutputBudget.MinLineBytes * 4,
            maxContextLineBytes: MachineOutputBudget.MinLineBytes * 4,
            maxPathBytes: MachineOutputBudget.MinPathBytes * 4,
            maxContextLines: MachineOutputBudget.MinContextLines + 4,
            maxRecords: MachineOutputBudget.MinRecords,
            maxResponseBytes: MachineOutputBudget.MaxResponseBytesCeiling);

        // Context lines are the least valuable thing in a match record, so they go first and go whole.
        MachineOutputBudget? step = budget.Shrink();
        Assert.NotNull(step);
        Assert.Equal(MachineOutputBudget.MinContextLines, step!.MaxContextLines);
        Assert.Equal(budget.MaxLineBytes, step.MaxLineBytes);
        Assert.Equal(budget.MaxPathBytes, step.MaxPathBytes);

        // The path is surrendered last: a shortened path is the least useful of the four, because a
        // caller cannot open a file it can no longer name.
        var rungs = new List<MachineOutputBudget> { budget, step };
        while (rungs[^1].Shrink() is { } next)
        {
            rungs.Add(next);
            Assert.True(rungs.Count < 64, "the ladder must terminate rather than shrinking forever.");
        }

        MachineOutputBudget floor = rungs[^1];
        Assert.Equal(MachineOutputBudget.MinContextLines, floor.MaxContextLines);
        Assert.Equal(MachineOutputBudget.MinLineBytes, floor.MaxLineBytes);
        Assert.Equal(MachineOutputBudget.MinLineBytes, floor.MaxContextLineBytes);
        Assert.Equal(MachineOutputBudget.MinPathBytes, floor.MaxPathBytes);
        Assert.Null(floor.Shrink());

        int pathShrankAt = rungs.FindIndex(r => r.MaxPathBytes < budget.MaxPathBytes);
        int lineShrankAt = rungs.FindIndex(r => r.MaxLineBytes < budget.MaxLineBytes);
        Assert.True(lineShrankAt < pathShrankAt, "the match line must be given up before the path.");

        // Shrinking must never touch the response or record ceilings, which are the caller's contract.
        Assert.Equal(budget.MaxResponseBytes, floor.MaxResponseBytes);
        Assert.Equal(budget.MaxRecords, floor.MaxRecords);
    }
}
