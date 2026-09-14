using System.Text;
using Yagu.Services.Mcp;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// The record that lets a previewed replacement be applied later.
/// </summary>
/// <remarks>
/// The property under test throughout is that a commit reproduces <b>exactly</b> what the preview showed,
/// or refuses. Re-running the pattern at write time would be a different operation than the one reviewed:
/// a broad pattern applied to content that changed in between can rewrite lines nobody looked at.
/// </remarks>
public class McpReplaceCommitPlanTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "yagu-commit-" + Guid.NewGuid().ToString("N"));

    public McpReplaceCommitPlanTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string WriteFile(string content, string name = "a.txt")
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static McpReplaceFilePlan PlanFor(string path, params McpReplaceEdit[] edits)
    {
        var info = new FileInfo(path);
        return new McpReplaceFilePlan(
            path, "utf-8", McpReplaceCommitPlan.HashFile(path),
            info.Length, info.LastWriteTimeUtc.Ticks, edits.Length, edits);
    }

    // ── Reading and verifying in one step ──

    [Fact]
    public void ReadVerified_ReturnsTheExactBytesItVerified()
    {
        // The bytes handed back are the ones the hash covered. A commit that re-opened the file to read
        // its content would be editing a snapshot nothing verified.
        string path = WriteFile("alpha beta");
        McpReplaceFilePlan plan = PlanFor(path, new McpReplaceEdit(0, 5, "ALPHA"));

        McpReplaceVerifyResult result = McpReplaceCommitPlan.ReadVerified(plan, out byte[] content);

        Assert.Equal(McpReplaceVerifyResult.Ok, result);
        Assert.Equal("alpha beta", Encoding.UTF8.GetString(content));
        Assert.Equal(plan.ContentSha256, McpReplaceCommitPlan.HashBytes(content));
    }

    [Fact]
    public void ReadVerified_RefusesAndYieldsNoContentWhenTheFileChanged()
    {
        string path = WriteFile("alpha beta");
        McpReplaceFilePlan plan = PlanFor(path, new McpReplaceEdit(0, 5, "ALPHA"));

        File.WriteAllText(path, "totally different", new UTF8Encoding(false));

        McpReplaceVerifyResult result = McpReplaceCommitPlan.ReadVerified(plan, out byte[] content);

        Assert.NotEqual(McpReplaceVerifyResult.Ok, result);
        Assert.Empty(content);
    }

    [Theory]
    [InlineData(10, 11, nameof(McpReplaceVerifyResult.ContentChanged))]
    [InlineData(2147483648L, 2147483648L, nameof(McpReplaceVerifyResult.Unreadable))]
    public void ClassifyLength_RejectsMismatchesAndFilesTooLargeToBuffer(
        long actual,
        long expected,
        string expectedResult)
    {
        // The enum is internal, so it cannot appear in a public [Theory] signature (CS0051) — name the
        // expected member instead and resolve it inside the test body.
        Assert.Equal(
            Enum.Parse<McpReplaceVerifyResult>(expectedResult),
            McpReplaceCommitPlan.ClassifyLength(actual, expected));
    }

    [Fact]
    public void ClassifyLength_AcceptsMatchingBufferableFiles()
        => Assert.Null(McpReplaceCommitPlan.ClassifyLength(1024, 1024));

    [Fact]
    public void ReadVerified_RefusesAContentChangeThatKeptTheSameLength()
    {
        // Length and timestamp are pre-filters; the hash is the check that actually holds.
        string path = WriteFile("alpha beta");
        McpReplaceFilePlan plan = PlanFor(path, new McpReplaceEdit(0, 5, "ALPHA"));

        var stamp = new FileInfo(path).LastWriteTimeUtc;
        File.WriteAllText(path, "gamma beta", new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, stamp);

        Assert.Equal(McpReplaceVerifyResult.ContentChanged, McpReplaceCommitPlan.ReadVerified(plan, out _));
    }

    [Fact]
    public void ReadVerified_ReportsAMissingFileAsMissing()
    {
        string path = WriteFile("alpha beta");
        McpReplaceFilePlan plan = PlanFor(path, new McpReplaceEdit(0, 5, "ALPHA"));

        File.Delete(path);

        Assert.Equal(McpReplaceVerifyResult.Missing, McpReplaceCommitPlan.ReadVerified(plan, out _));
    }

    [Fact]
    public void HashBytes_MatchesHashFileForTheSameContent()
    {
        string path = WriteFile("alpha beta");

        Assert.Equal(
            McpReplaceCommitPlan.HashFile(path),
            McpReplaceCommitPlan.HashBytes(File.ReadAllBytes(path)));
    }

    // ── Applying edits ──

    [Fact]
    public void ApplyEdits_ReproducesTheReviewedResult()
    {
        const string original = "alpha beta alpha";
        var edits = new List<McpReplaceEdit>
        {
            new(0, 5, "ALPHA"),
            new(11, 5, "ALPHA"),
        };

        Assert.Equal("ALPHA beta ALPHA", McpReplaceCommitPlan.ApplyEdits(original, edits));
    }

    [Fact]
    public void ApplyEdits_HandlesReplacementsOfDifferentLength()
    {
        // Applied back-to-front, so an earlier offset is still valid after a later edit changed length.
        var edits = new List<McpReplaceEdit> { new(0, 3, "x"), new(4, 3, "yyyyy") };

        Assert.Equal("x yyyyy", McpReplaceCommitPlan.ApplyEdits("foo bar", edits));
    }

    [Fact]
    public void ApplyEdits_IsOrderIndependent()
    {
        var forward = new List<McpReplaceEdit> { new(0, 3, "1"), new(4, 3, "2") };
        var reversed = new List<McpReplaceEdit> { new(4, 3, "2"), new(0, 3, "1") };

        Assert.Equal(
            McpReplaceCommitPlan.ApplyEdits("foo bar", forward),
            McpReplaceCommitPlan.ApplyEdits("foo bar", reversed));
    }

    [Fact]
    public void ApplyEdits_RefusesAnEditThatDoesNotFit()
    {
        // Means the plan and the content disagree. Never something to patch up.
        var edits = new List<McpReplaceEdit> { new(10, 5, "x") };

        Assert.Throws<InvalidOperationException>(() => McpReplaceCommitPlan.ApplyEdits("short", edits));
    }

    [Fact]
    public void ApplyEdits_PreservesEverythingOutsideTheEdits()
    {
        const string original = "line1\r\nline2\r\nline3";
        var edits = new List<McpReplaceEdit> { new(7, 5, "LINE2") };

        Assert.Equal("line1\r\nLINE2\r\nline3", McpReplaceCommitPlan.ApplyEdits(original, edits));
    }

    // ── Verification ──

    [Fact]
    public void Verify_AcceptsAnUntouchedFile()
    {
        string path = WriteFile("hello world");
        Assert.Equal(McpReplaceVerifyResult.Ok, McpReplaceCommitPlan.Verify(PlanFor(path, new McpReplaceEdit(0, 5, "HELLO"))));
    }

    [Fact]
    public void Verify_RefusesAFileWhoseContentChanged()
    {
        string path = WriteFile("hello world");
        McpReplaceFilePlan plan = PlanFor(path, new McpReplaceEdit(0, 5, "HELLO"));

        File.WriteAllText(path, "something else entirely", new UTF8Encoding(false));

        Assert.NotEqual(McpReplaceVerifyResult.Ok, McpReplaceCommitPlan.Verify(plan));
    }

    [Fact]
    public void Verify_RefusesAChangeThatKeptTheSameLength()
    {
        // The cheap length check cannot catch this; the hash must.
        string path = WriteFile("hello world");
        McpReplaceFilePlan plan = PlanFor(path, new McpReplaceEdit(0, 5, "HELLO"));

        var stamp = new FileInfo(path).LastWriteTimeUtc;
        File.WriteAllText(path, "world hello", new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, stamp);

        Assert.Equal(McpReplaceVerifyResult.ContentChanged, McpReplaceCommitPlan.Verify(plan));
    }

    [Fact]
    public void Verify_RefusesADeletedFile()
    {
        string path = WriteFile("hello world");
        McpReplaceFilePlan plan = PlanFor(path, new McpReplaceEdit(0, 5, "HELLO"));

        File.Delete(path);

        Assert.Equal(McpReplaceVerifyResult.Missing, McpReplaceCommitPlan.Verify(plan));
    }

    [Fact]
    public void Verify_RefusesAFileRewrittenWithIdenticalContent()
    {
        // Same bytes, new timestamp: something else is editing this file, so the preview is stale even
        // though the content happens to match.
        string path = WriteFile("hello world");
        McpReplaceFilePlan plan = PlanFor(path, new McpReplaceEdit(0, 5, "HELLO"));

        File.SetLastWriteTimeUtc(path, new FileInfo(path).LastWriteTimeUtc.AddMinutes(1));

        Assert.Equal(McpReplaceVerifyResult.TimestampChanged, McpReplaceCommitPlan.Verify(plan));
    }

    // ── Store ──

    private McpReplaceCommitPlan NewPlan(params McpReplaceFilePlan[] files)
    {
        var now = DateTimeOffset.UtcNow;
        return new McpReplaceCommitPlan(
            McpReplaceCommitPlan.NewId(), now, now + McpReplaceCommitPlan.Lifetime,
            "foo", "bar", Regex: false, CaseSensitive: false, WholeWord: true, Multiline: false, files);
    }

    [Fact]
    public void Plan_RoundTripsThroughJson()
    {
        string path = WriteFile("alpha beta");
        McpReplaceCommitPlan original = NewPlan(PlanFor(path, new McpReplaceEdit(0, 5, "ALPHA")));

        McpReplaceCommitPlan? restored = McpReplaceCommitPlan.FromJson(original.ToJson());

        Assert.NotNull(restored);
        Assert.Equal(original.Id, restored!.Id);
        Assert.Equal(original.Query, restored.Query);
        Assert.Equal(original.Replacement, restored.Replacement);
        Assert.Equal(original.Files[0].ContentSha256, restored.Files[0].ContentSha256);
        Assert.Equal(original.Files[0].Edits[0].Replacement, restored.Files[0].Edits[0].Replacement);
        Assert.Equal(original.Files[0].Edits[0].Offset, restored.Files[0].Edits[0].Offset);
    }

    [Fact]
    public void MalformedJson_YieldsNoPlanRatherThanAPartialOne()
    {
        Assert.Null(McpReplaceCommitPlan.FromJson("{ not json"));
        Assert.Null(McpReplaceCommitPlan.FromJson("{}"));
    }

    [Fact]
    public void Ids_AreUnguessableAndFileNameSafe()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < 64; i++)
        {
            string id = McpReplaceCommitPlan.NewId();
            Assert.True(McpReplaceCommitPlan.IsWellFormedId(id));
            Assert.Equal(32, id.Length);
            Assert.True(ids.Add(id));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("../../../etc/passwd")]
    [InlineData("00112233445566778899aabbccddeeffZZ")]
    public void MalformedIds_AreRejectedBeforeTouchingTheFileSystem(string? id)
        => Assert.False(McpReplaceCommitPlan.IsWellFormedId(id));

    [Fact]
    public void Expiry_IsHonoured()
    {
        var now = DateTimeOffset.UtcNow;
        var plan = new McpReplaceCommitPlan("a", now, now.AddMinutes(-1), "q", "r", false, false, true, false, []);

        Assert.True(plan.IsExpired(now));
    }

    // ── Persistence, claiming and expiry ──
    //
    // These drive the directory-parameterised overloads: the live pending store belongs to the user's
    // running server, and a test that claimed or purged plans there could destroy a real pending commit.

    private string PendingDir => Path.Combine(_directory, "pending");

    private McpReplaceCommitPlan NewPlan(string? id = null, DateTimeOffset? expires = null)
    {
        var now = DateTimeOffset.UtcNow;
        string path = WriteFile("alpha beta", "plan-target-" + Guid.NewGuid().ToString("N") + ".txt");
        return new McpReplaceCommitPlan(
            id ?? McpReplaceCommitPlan.NewId(), now, expires ?? now.Add(McpReplaceCommitPlan.Lifetime),
            "alpha", "ALPHA", false, false, false, false,
            [PlanFor(path, new McpReplaceEdit(0, 5, "ALPHA"))]);
    }

    [Fact]
    public void SaveIn_WritesThePlanWhereClaimLooksForIt()
    {
        McpReplaceCommitPlan plan = NewPlan();

        string? id = plan.Save(PendingDir);

        Assert.Equal(plan.Id, id);
        Assert.True(File.Exists(McpReplaceCommitPlan.PathForIdIn(PendingDir, plan.Id)));
    }

    [Fact]
    public void SaveIn_UnwritableDirectory_ReturnsNullInsteadOfThrowing()
    {
        string blocker = WriteFile("x", "blocker");

        Assert.Null(NewPlan().SaveIn(Path.Combine(blocker, "pending")));
    }

    [Fact]
    public void ClaimIn_ReturnsThePlanAndTakesItOutOfTheStore()
    {
        McpReplaceCommitPlan plan = NewPlan();
        plan.SaveIn(PendingDir);

        McpReplaceCommitPlan? claimed = McpReplaceCommitPlan.Claim(plan.Id, out string? failure, PendingDir);

        Assert.NotNull(claimed);
        Assert.Null(failure);
        Assert.Equal(plan.Id, claimed!.Id);
        Assert.False(File.Exists(McpReplaceCommitPlan.PathForIdIn(PendingDir, plan.Id)));
    }

    [Fact]
    public void ClaimIn_IsSingleUse()
    {
        McpReplaceCommitPlan plan = NewPlan();
        plan.SaveIn(PendingDir);
        McpReplaceCommitPlan.ClaimIn(PendingDir, plan.Id, out _);

        // A retry must not re-apply edits to a file that may since have been changed back.
        McpReplaceCommitPlan? second = McpReplaceCommitPlan.ClaimIn(PendingDir, plan.Id, out string? failure);

        Assert.Null(second);
        Assert.NotNull(failure);
    }

    [Fact]
    public void ClaimIn_AfterAnInterruptedCommit_ReportsPartialStateRatherThanSilentlyRetrying()
    {
        McpReplaceCommitPlan plan = NewPlan();
        plan.SaveIn(PendingDir);
        McpReplaceCommitPlan.ClaimIn(PendingDir, plan.Id, out _);

        // The in-flight marker is still there, which is exactly how a killed mid-batch commit looks.
        McpReplaceCommitPlan? retried = McpReplaceCommitPlan.ClaimIn(PendingDir, plan.Id, out string? failure);

        Assert.Null(retried);
        Assert.Contains("interrupted", failure);
        Assert.Contains(".yagubak", failure);
    }

    [Fact]
    public void ClaimIn_ReleasedClaim_LeavesNothingBehind()
    {
        McpReplaceCommitPlan plan = NewPlan();
        plan.SaveIn(PendingDir);
        McpReplaceCommitPlan.ClaimIn(PendingDir, plan.Id, out _);

        McpReplaceCommitPlan.ReleaseClaim(plan.Id, PendingDir);

        Assert.False(File.Exists(McpReplaceCommitPlan.PathForIdIn(PendingDir, plan.Id) + ".inflight"));

        // With the marker gone, the id is simply unknown again rather than reported as interrupted.
        Assert.Null(McpReplaceCommitPlan.ClaimIn(PendingDir, plan.Id, out string? failure));
        Assert.Contains("no pending replacement", failure);
    }

    [Fact]
    public void ReleaseClaimIn_WithNothingClaimed_IsANoOp()
    {
        Directory.CreateDirectory(PendingDir);

        McpReplaceCommitPlan.ReleaseClaimIn(PendingDir, McpReplaceCommitPlan.NewId());
    }

    [Fact]
    public void ClaimIn_MalformedId_IsRejectedBeforeTouchingTheStore()
    {
        Assert.Null(McpReplaceCommitPlan.ClaimIn(PendingDir, "../../evil", out string? failure));
        Assert.Contains("not a valid commit id", failure);
    }

    [Fact]
    public void ClaimIn_UnknownId_ExplainsItMayHaveExpiredOrBeenCommitted()
    {
        Directory.CreateDirectory(PendingDir);

        Assert.Null(McpReplaceCommitPlan.ClaimIn(PendingDir, McpReplaceCommitPlan.NewId(), out string? failure));
        Assert.Contains("no pending replacement", failure);
    }

    [Fact]
    public void ClaimIn_UnreadablePlan_IsDiscardedRatherThanLeftClaimable()
    {
        string id = McpReplaceCommitPlan.NewId();
        Directory.CreateDirectory(PendingDir);
        File.WriteAllText(McpReplaceCommitPlan.PathForIdIn(PendingDir, id), "{ not json");

        McpReplaceCommitPlan? claimed = McpReplaceCommitPlan.ClaimIn(PendingDir, id, out string? failure);

        Assert.Null(claimed);
        Assert.Contains("could not be read", failure);

        // The claim is released, so a corrupt plan does not masquerade as an interrupted commit forever.
        Assert.False(File.Exists(McpReplaceCommitPlan.PathForIdIn(PendingDir, id) + ".inflight"));
    }

    [Fact]
    public void PurgeExpiredIn_RemovesExpiredPlansAndKeepsLiveOnes()
    {
        var now = DateTimeOffset.UtcNow;
        McpReplaceCommitPlan live = NewPlan(expires: now.AddMinutes(10));
        McpReplaceCommitPlan stale = NewPlan(expires: now.AddMinutes(-1));
        live.SaveIn(PendingDir);
        stale.SaveIn(PendingDir);

        McpReplaceCommitPlan.PurgeExpired(now, PendingDir);

        Assert.True(File.Exists(McpReplaceCommitPlan.PathForIdIn(PendingDir, live.Id)));
        Assert.False(File.Exists(McpReplaceCommitPlan.PathForIdIn(PendingDir, stale.Id)));
    }

    [Fact]
    public void PurgeExpiredIn_RemovesPlansThatCannotBeParsed()
    {
        string id = McpReplaceCommitPlan.NewId();
        Directory.CreateDirectory(PendingDir);
        File.WriteAllText(McpReplaceCommitPlan.PathForIdIn(PendingDir, id), "{ not json");

        McpReplaceCommitPlan.PurgeExpiredIn(PendingDir, DateTimeOffset.UtcNow);

        Assert.False(File.Exists(McpReplaceCommitPlan.PathForIdIn(PendingDir, id)));
    }

    [Fact]
    public void PurgeExpiredIn_LeavesInFlightMarkersAlone()
    {
        McpReplaceCommitPlan plan = NewPlan();
        plan.SaveIn(PendingDir);
        McpReplaceCommitPlan.ClaimIn(PendingDir, plan.Id, out _);

        // Only *.json is purged: dropping the marker would erase the record of a partial commit.
        McpReplaceCommitPlan.PurgeExpiredIn(PendingDir, DateTimeOffset.UtcNow.AddYears(1));

        Assert.True(File.Exists(McpReplaceCommitPlan.PathForIdIn(PendingDir, plan.Id) + ".inflight"));
    }

    [Fact]
    public void PurgeExpiredIn_MissingDirectory_IsANoOp()
    {
        McpReplaceCommitPlan.PurgeExpiredIn(Path.Combine(_directory, "never-created"), DateTimeOffset.UtcNow);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PurgeExpiredIn_DirectoryEnumerationFailure_IsBestEffort(bool unauthorized)
    {
        Directory.CreateDirectory(PendingDir);

        McpReplaceCommitPlan.PurgeExpiredIn(
            PendingDir,
            DateTimeOffset.UtcNow,
            (_, _) => throw (unauthorized
                ? new UnauthorizedAccessException("denied")
                : (Exception)new IOException("unavailable")));
    }

    [Fact]
    public void SavedPlan_SurvivesTheRoundTripThroughTheStore()
    {
        McpReplaceCommitPlan original = NewPlan();
        original.SaveIn(PendingDir);

        McpReplaceCommitPlan? claimed = McpReplaceCommitPlan.ClaimIn(PendingDir, original.Id, out _);

        Assert.NotNull(claimed);
        Assert.Equal(original.Query, claimed!.Query);
        Assert.Equal(original.Replacement, claimed.Replacement);
        Assert.Equal(original.Files[0].Path, claimed.Files[0].Path);
        Assert.Equal(original.Files[0].ContentSha256, claimed.Files[0].ContentSha256);
        Assert.Equal(original.Files[0].Edits[0].Offset, claimed.Files[0].Edits[0].Offset);
        Assert.Equal(original.Files[0].Edits[0].Replacement, claimed.Files[0].Edits[0].Replacement);
    }

    [Fact]
    public void LivePendingDirectory_SitsUnderTheMcpStateDirectory()
    {
        // The production path must keep flowing through the same layout the overloads exercise.
        Assert.Equal(
            McpReplaceCommitPlan.PathForIdIn(McpReplaceCommitPlan.PendingDirectory, "abc"),
            McpReplaceCommitPlan.PathForId("abc"));
        Assert.StartsWith(McpTokenStore.DirectoryPath, McpReplaceCommitPlan.PendingDirectory, StringComparison.Ordinal);
    }

    // ── Verification outcomes that must never be mistaken for "unchanged" ──

    [Fact]
    public void ReadVerified_ReportsARewriteThatRestoredTheSameBytesAsATimestampChange()
    {
        string path = WriteFile("hello world");
        McpReplaceFilePlan plan = PlanFor(path, new McpReplaceEdit(0, 5, "ALPHA"));

        // Same length, same hash, different write time: something else has been through this file, so
        // the recorded offsets are no longer trustworthy even though the content matches.
        File.SetLastWriteTimeUtc(path, new FileInfo(path).LastWriteTimeUtc.AddMinutes(1));

        Assert.Equal(McpReplaceVerifyResult.TimestampChanged, McpReplaceCommitPlan.ReadVerified(plan, out byte[] content));
        Assert.Empty(content);
    }

    [Fact]
    public void ReadVerified_ReportsAVanishedDirectoryAsMissing()
    {
        string nested = Path.Combine(_directory, "gone");
        Directory.CreateDirectory(nested);
        string path = Path.Combine(nested, "a.txt");
        File.WriteAllText(path, "hello world", new UTF8Encoding(false));

        McpReplaceFilePlan plan = PlanFor(path, new McpReplaceEdit(0, 5, "ALPHA"));
        Directory.Delete(nested, recursive: true);

        // Missing, not Unreadable: the caller reports "the file no longer exists", which is actionable.
        Assert.Equal(McpReplaceVerifyResult.Missing, McpReplaceCommitPlan.ReadVerified(plan, out _));
    }

    [Fact]
    public void ReadVerified_ReportsAPathThatIsADirectoryAsUnreadable()
    {
        string path = WriteFile("hello world");
        McpReplaceFilePlan plan = PlanFor(path, new McpReplaceEdit(0, 5, "ALPHA"));

        File.Delete(path);
        Directory.CreateDirectory(path);

        // Anything the commit cannot read is refused rather than allowed through as unchanged.
        Assert.Equal(McpReplaceVerifyResult.Unreadable, McpReplaceCommitPlan.ReadVerified(plan, out _));
    }

    [Fact]
    public void Verify_ReportsADirectoryWhereTheFileWas_AsMissing()
    {
        string path = WriteFile("hello world");
        McpReplaceFilePlan plan = PlanFor(path, new McpReplaceEdit(0, 5, "ALPHA"));

        File.Delete(path);
        Directory.CreateDirectory(path);

        // FileInfo.Exists is false for a directory, so the cheap preflight calls this Missing while
        // ReadVerified — which actually opens a handle — calls it Unreadable. Both refuse, which is what
        // matters; the difference is only in the message and is pinned here so neither drifts to Ok.
        Assert.Equal(McpReplaceVerifyResult.Missing, McpReplaceCommitPlan.Verify(plan));
    }

    [Fact]
    public void Verify_ReportsAFileHeldExclusivelyByAnotherWriterAsUnreadable()
    {
        string path = WriteFile("hello world");
        McpReplaceFilePlan plan = PlanFor(path, new McpReplaceEdit(0, 5, "ALPHA"));

        // Length and timestamp still match, so only the hash read fails. A commit must not proceed on
        // metadata alone while something else has the file open for writing.
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal(McpReplaceVerifyResult.Unreadable, McpReplaceCommitPlan.Verify(plan));
    }

    // ── Claiming and purging a store that has been corrupted ──

    [Fact]
    public void ClaimIn_APlanThatCannotBeParsed_FailsAndDoesNotStayClaimed()
    {
        string id = McpReplaceCommitPlan.NewId();
        Directory.CreateDirectory(PendingDir);
        File.WriteAllText(McpReplaceCommitPlan.PathForIdIn(PendingDir, id), "{ not a plan");

        Assert.Null(McpReplaceCommitPlan.ClaimIn(PendingDir, id, out string? failure));
        Assert.Contains("could not be read", failure);

        // The claim marker has to go, or the next attempt reports an interrupted commit that never ran.
        Assert.False(File.Exists(McpReplaceCommitPlan.PathForIdIn(PendingDir, id) + ".inflight"));
    }

    [Fact]
    public void PurgeExpiredIn_DropsExpiredAndUnreadablePlansAndKeepsLiveOnes()
    {
        Directory.CreateDirectory(PendingDir);

        McpReplaceCommitPlan live = NewPlan();
        McpReplaceCommitPlan stale = NewPlan(expires: DateTimeOffset.UtcNow.AddMinutes(-1));
        live.SaveIn(PendingDir);
        stale.SaveIn(PendingDir);

        string garbageId = McpReplaceCommitPlan.NewId();
        File.WriteAllText(McpReplaceCommitPlan.PathForIdIn(PendingDir, garbageId), "{ not a plan");

        McpReplaceCommitPlan.PurgeExpiredIn(PendingDir, DateTimeOffset.UtcNow);

        Assert.True(File.Exists(McpReplaceCommitPlan.PathForIdIn(PendingDir, live.Id)));
        Assert.False(File.Exists(McpReplaceCommitPlan.PathForIdIn(PendingDir, stale.Id)));

        // A plan that cannot be read is also a plan that cannot be committed, so it is not kept forever.
        Assert.False(File.Exists(McpReplaceCommitPlan.PathForIdIn(PendingDir, garbageId)));
    }

    [Fact]
    public void PurgeExpiredIn_AStoreThatWasNeverCreated_IsNotAnError()
    {
        McpReplaceCommitPlan.PurgeExpiredIn(Path.Combine(_directory, "never-existed"), DateTimeOffset.UtcNow);
    }

    [Fact]
    public void ReleaseClaimIn_WithNoMarkerPresent_IsNotAnError()
    {
        McpReplaceCommitPlan.ReleaseClaimIn(PendingDir, McpReplaceCommitPlan.NewId());
    }

    // ── A pending store another process is holding open ──
    //
    // The store lives beside the token file and a running server may be reading it. None of these may
    // throw: an unusable store has to degrade to "cannot commit", not take the caller down with it.

    [Fact]
    public void ClaimIn_APlanFileHeldOpenByAnotherProcess_FailsWithoutThrowing()
    {
        McpReplaceCommitPlan plan = NewPlan();
        Directory.CreateDirectory(PendingDir);
        plan.SaveIn(PendingDir);

        // An exclusive handle makes the rename-before-read fail, which is exactly what a competing
        // commit looks like from here.
        using var exclusive = new FileStream(
            McpReplaceCommitPlan.PathForIdIn(PendingDir, plan.Id),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Null(McpReplaceCommitPlan.ClaimIn(PendingDir, plan.Id, out string? failure));
        Assert.Contains("another commit may be running", failure);
    }

    [Fact]
    public void ReleaseClaimIn_AMarkerThatCannotBeDeleted_IsSwallowedRatherThanThrown()
    {
        string id = McpReplaceCommitPlan.NewId();
        Directory.CreateDirectory(PendingDir);
        string marker = McpReplaceCommitPlan.PathForIdIn(PendingDir, id) + ".inflight";
        File.WriteAllText(marker, "{}");

        using var exclusive = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        // This runs in the finally of a commit that may already be reporting a different failure; an
        // exception here would replace that report with an unrelated IO error.
        McpReplaceCommitPlan.ReleaseClaimIn(PendingDir, id);
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public void PurgeExpiredIn_SkipsAPlanItCannotReadAndStillPurgesTheRest()
    {
        Directory.CreateDirectory(PendingDir);

        McpReplaceCommitPlan stale = NewPlan(expires: DateTimeOffset.UtcNow.AddMinutes(-1));
        McpReplaceCommitPlan locked = NewPlan(expires: DateTimeOffset.UtcNow.AddMinutes(-1));
        stale.SaveIn(PendingDir);
        locked.SaveIn(PendingDir);

        using var exclusive = new FileStream(
            McpReplaceCommitPlan.PathForIdIn(PendingDir, locked.Id),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        McpReplaceCommitPlan.PurgeExpiredIn(PendingDir, DateTimeOffset.UtcNow);

        // One unreadable entry must not stop the sweep, or a single stuck file keeps every expired plan
        // committable for the life of the server.
        Assert.False(File.Exists(McpReplaceCommitPlan.PathForIdIn(PendingDir, stale.Id)));
        Assert.True(File.Exists(McpReplaceCommitPlan.PathForIdIn(PendingDir, locked.Id)));
    }
}
