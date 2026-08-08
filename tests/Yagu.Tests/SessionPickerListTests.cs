using Yagu.Helpers;
using Yagu.Services;

namespace Yagu.Tests;

/// <summary>
/// Unit tests for <see cref="SessionPickerList"/> — the saved-session picker's summary wording and its
/// delete-by-path bookkeeping.
/// </summary>
public sealed class SessionPickerListTests
{
    private static SessionFileCandidate Candidate(string path)
        => new(path, SizeBytes: 128, ModifiedUtc: DateTimeOffset.UnixEpoch, CreatedUtc: DateTimeOffset.UnixEpoch);

    [Fact]
    public void BuildSummary_Zero_ExplainsNothingWasFound()
        => Assert.Equal("No .yagu-session files found by Everything.", SessionPickerList.BuildSummary(0));

    [Fact]
    public void BuildSummary_One_UsesTheSingular()
        => Assert.Equal("1 .yagu-session file found", SessionPickerList.BuildSummary(1));

    [Fact]
    public void BuildSummary_Two_UsesThePlural()
        => Assert.Equal("2 .yagu-session files found", SessionPickerList.BuildSummary(2));

    [Fact]
    public void BuildSummary_LargeCount_IsGroupedForReadability()
    {
        // Formatted with the same culture the dialog runs under, so the assertion is culture-robust.
        Assert.Equal($"{1234:N0} .yagu-session files found", SessionPickerList.BuildSummary(1234));
        Assert.Contains($"{1234:N0}", SessionPickerList.BuildSummary(1234), StringComparison.Ordinal);
    }

    [Fact]
    public void RemoveByPath_DropsTheMatchingRow_AndReportsOneRemoval()
    {
        List<SessionFileCandidate> sessions =
        [
            Candidate(@"C:\one.yagu-session"),
            Candidate(@"C:\two.yagu-session"),
        ];

        int removed = SessionPickerList.RemoveByPath(sessions, @"C:\one.yagu-session");

        Assert.Equal(1, removed);
        Assert.Equal([@"C:\two.yagu-session"], sessions.Select(s => s.Path));
    }

    [Fact]
    public void RemoveByPath_IgnoresCase_BecauseEverythingCanReportADifferentCasing()
    {
        List<SessionFileCandidate> sessions = [Candidate(@"C:\Saved\Run.yagu-session")];

        int removed = SessionPickerList.RemoveByPath(sessions, @"c:\saved\RUN.YAGU-SESSION");

        Assert.Equal(1, removed);
        Assert.Empty(sessions);
    }

    [Fact]
    public void RemoveByPath_UnknownPath_LeavesTheListUntouched()
    {
        List<SessionFileCandidate> sessions = [Candidate(@"C:\one.yagu-session")];

        int removed = SessionPickerList.RemoveByPath(sessions, @"C:\missing.yagu-session");

        Assert.Equal(0, removed);
        Assert.Single(sessions);
    }

    [Fact]
    public void RemoveByPath_EmptyList_IsANoOp()
    {
        List<SessionFileCandidate> sessions = [];

        Assert.Equal(0, SessionPickerList.RemoveByPath(sessions, @"C:\one.yagu-session"));
        Assert.Empty(sessions);
    }

    [Fact]
    public void RemoveByPath_DuplicatePaths_AllGo()
    {
        List<SessionFileCandidate> sessions =
        [
            Candidate(@"C:\dup.yagu-session"),
            Candidate(@"C:\DUP.yagu-session"),
            Candidate(@"C:\keep.yagu-session"),
        ];

        int removed = SessionPickerList.RemoveByPath(sessions, @"C:\dup.yagu-session");

        Assert.Equal(2, removed);
        Assert.Equal([@"C:\keep.yagu-session"], sessions.Select(s => s.Path));
    }

    [Fact]
    public void RemoveByPath_ThenSummary_ReportsTheRemainingCount()
    {
        List<SessionFileCandidate> sessions =
        [
            Candidate(@"C:\one.yagu-session"),
            Candidate(@"C:\two.yagu-session"),
        ];

        SessionPickerList.RemoveByPath(sessions, @"C:\one.yagu-session");
        Assert.Equal("1 .yagu-session file found", SessionPickerList.BuildSummary(sessions.Count));

        SessionPickerList.RemoveByPath(sessions, @"C:\two.yagu-session");
        Assert.Equal("No .yagu-session files found by Everything.", SessionPickerList.BuildSummary(sessions.Count));
    }
}
