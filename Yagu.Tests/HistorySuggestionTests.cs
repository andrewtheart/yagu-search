using System;
using System.Globalization;
using Yagu.Models;
using Xunit;

namespace Yagu.Tests;

public sealed class HistorySuggestionTests
{
    [Fact]
    public void NullTimestamp_ProducesEmptyDisplay()
    {
        var suggestion = new HistorySuggestion("foo", null);
        Assert.Equal("foo", suggestion.Value);
        Assert.Null(suggestion.Timestamp);
        Assert.Equal(string.Empty, suggestion.TimestampDisplay);
    }

    [Fact]
    public void Timestamp_FormatsAsDateAtLowercaseTime()
    {
        // Build a fixed local time so the formatting is deterministic regardless of time zone.
        var local = new DateTime(2026, 6, 23, 13, 30, 0, DateTimeKind.Local);
        var ts = new DateTimeOffset(local);

        var suggestion = new HistorySuggestion("query", ts);

        Assert.Equal("6/23/2026 @ 1:30pm", suggestion.TimestampDisplay);
        Assert.Equal("query", suggestion.ToString());
    }

    [Fact]
    public void FormatTimestamp_MorningTime_UsesAmSuffix()
    {
        var ts = new DateTimeOffset(new DateTime(2026, 1, 5, 9, 5, 0, DateTimeKind.Local));
        Assert.Equal("1/5/2026 @ 9:05am", HistorySuggestion.FormatTimestamp(ts));
    }

    [Fact]
    public void FormatTimestamp_Null_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, HistorySuggestion.FormatTimestamp(null));
    }
}
