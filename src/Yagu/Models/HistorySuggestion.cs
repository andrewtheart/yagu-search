using System;
using System.Globalization;

namespace Yagu.Models;

/// <summary>
/// A single autocomplete-history row shown in the search-query and directory dropdowns: the entry
/// text plus an optional "added/last-used" timestamp rendered on the trailing edge of the row.
/// Entries recorded before timestamps were tracked have a null <see cref="Timestamp"/> and render no
/// date. <see cref="ToString"/> returns <see cref="Value"/> so the AutoSuggestBox text round-trips.
/// </summary>
public sealed class HistorySuggestion
{
    public HistorySuggestion(string value, DateTimeOffset? timestamp)
    {
        Value = value;
        Timestamp = timestamp;
        TimestampDisplay = FormatTimestamp(timestamp);
    }

    /// <summary>The autocomplete entry text (search query or directory path).</summary>
    public string Value { get; }

    /// <summary>When the entry was last added to the history, or null for legacy/untimed entries.</summary>
    public DateTimeOffset? Timestamp { get; }

    /// <summary>Display string for the timestamp (e.g. "6/23/2026 @ 1:30pm"), or empty when untimed.</summary>
    public string TimestampDisplay { get; }

    public override string ToString() => Value;

    /// <summary>Formats a timestamp like "6/23/2026 @ 1:30pm" in local time, or "" when null.</summary>
    public static string FormatTimestamp(DateTimeOffset? timestamp)
    {
        if (timestamp is not { } value)
            return string.Empty;

        var local = value.ToLocalTime();
        string date = local.ToString("M/d/yyyy", CultureInfo.InvariantCulture);
        string time = local.ToString("h:mmtt", CultureInfo.InvariantCulture).ToLowerInvariant();
        return $"{date} @ {time}";
    }
}
