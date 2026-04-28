using QuickGrep.Native;
using QuickGrep.Models;

namespace QuickGrep.Tests;

public class NativeSearchOutcomeTests
{
    [Fact]
    public void Unavailable_HasCorrectKind()
    {
        var outcome = NativeSearchOutcome.Unavailable;
        Assert.Equal(NativeSearchOutcome.OutcomeKind.Unavailable, outcome.Kind);
        Assert.Empty(outcome.Results);
        Assert.Null(outcome.Reason);
    }

    [Fact]
    public void Unavailable_IsSingleton()
    {
        Assert.Same(NativeSearchOutcome.Unavailable, NativeSearchOutcome.Unavailable);
    }

    [Fact]
    public void Skipped_HasCorrectKindAndReason()
    {
        var outcome = NativeSearchOutcome.Skipped("binary");
        Assert.Equal(NativeSearchOutcome.OutcomeKind.Skipped, outcome.Kind);
        Assert.Equal("binary", outcome.Reason);
        Assert.Empty(outcome.Results);
    }

    [Fact]
    public void Error_HasCorrectKindAndReason()
    {
        var outcome = NativeSearchOutcome.Error("invalid regex");
        Assert.Equal(NativeSearchOutcome.OutcomeKind.Error, outcome.Kind);
        Assert.Equal("invalid regex", outcome.Reason);
        Assert.Empty(outcome.Results);
    }

    [Fact]
    public void Cancelled_HasCorrectKind()
    {
        var outcome = NativeSearchOutcome.Cancelled();
        Assert.Equal(NativeSearchOutcome.OutcomeKind.Cancelled, outcome.Kind);
        Assert.Null(outcome.Reason);
        Assert.Empty(outcome.Results);
    }
}

// ─── NativeSearchOutcome: coverage gaps ─────────────────────────────────

public class NativeSearchOutcomeCoverageTests
{
    [Fact]
    public void Unavailable_HasEmptyResults()
    {
        var outcome = NativeSearchOutcome.Unavailable;
        Assert.Empty(outcome.Results);
    }

    [Fact]
    public void Unavailable_IsSingleton()
    {
        var a = NativeSearchOutcome.Unavailable;
        var b = NativeSearchOutcome.Unavailable;
        Assert.Same(a, b);
        Assert.Equal(NativeSearchOutcome.OutcomeKind.Unavailable, a.Kind);
    }

    [Fact]
    public void Skipped_HasCorrectKindAndReason()
    {
        var outcome = NativeSearchOutcome.Skipped("too many files");
        Assert.Equal(NativeSearchOutcome.OutcomeKind.Skipped, outcome.Kind);
        Assert.Equal("too many files", outcome.Reason);
    }

    [Fact]
    public void Error_HasCorrectReason()
    {
        var outcome = NativeSearchOutcome.Error("bad regex");
        Assert.Equal(NativeSearchOutcome.OutcomeKind.Error, outcome.Kind);
        Assert.Equal("bad regex", outcome.Reason);
        Assert.Empty(outcome.Results);
    }

    [Fact]
    public void Cancelled_HasNullReason()
    {
        var outcome = NativeSearchOutcome.Cancelled();
        Assert.Null(outcome.Reason);
    }
}
