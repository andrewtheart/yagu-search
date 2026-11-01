using System.Text;
using System.Text.Json;
using Yagu.Models;
using Yagu.Services.Cli.MachineOutput;

namespace Yagu.Tests;

/// <summary>
/// Covers the versioned CLI machine protocol: envelope shape, sequencing, correlation, per-field
/// truncation metadata, and the bounded-page contract every machine caller relies on.
/// </summary>
public class MachineOutputWriterTests
{
    private static SearchResult Match(
        string path = @"C:\repo\file.cs",
        int line = 12,
        string text = "var x = 1;",
        IReadOnlyList<string>? before = null,
        IReadOnlyList<string>? after = null)
        => new(path, line, text, MatchStartColumn: 4, MatchLength: 3, before ?? [], after ?? []);

    private static List<JsonElement> Run(Action<MachineOutputWriter> body, MachineOutputBudget? budget = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new MachineOutputWriter(stream, "req-1", budget))
            body(writer);

        string text = Encoding.UTF8.GetString(stream.ToArray());
        return [.. text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())];
    }

    [Fact]
    public void ASingleOversizedRecord_IsShortenedRatherThanBlowingTheResponseCeiling()
    {
        // The ceiling is a promise about the WHOLE response, and one match can exceed it on its own:
        // checking the running total before serializing lets a multi-megabyte record through, because
        // by the time the total is updated the bytes are already on the wire.
        //
        // Refusing it outright is not the answer either. It is the FIRST record, so refusing means the
        // response reports no results for a search that genuinely matched, and no limit the user can
        // raise is at fault. The writer shrinks its per-field ceilings and re-composes instead.
        //
        // Built directly rather than through Create, which now scales the per-record fields down so this
        // combination cannot be reached from settings. The writer-level guard still has to hold: it is
        // the last line of defence, and Create is not the only way a budget is constructed.
        var budget = new MachineOutputBudget
        {
            MaxLineBytes = MachineOutputBudget.MaxLineBytesCeiling,
            MaxContextLineBytes = MachineOutputBudget.MaxLineBytesCeiling,
            MaxPathBytes = MachineOutputBudget.MinPathBytes,
            MaxContextLines = 0,
            MaxRecords = 500,
            MaxResponseBytes = MachineOutputBudget.MinResponseBytes,
        };

        using var stream = new MemoryStream();
        bool accepted;
        using (var writer = new MachineOutputWriter(stream, "req-1", budget))
        {
            accepted = writer.WriteMatch(Match(text: new string('x', 512 * 1024)));
            Assert.True(writer.Truncated, "the shortened record must be reported as a truncation.");
            writer.WriteSummary(CliExitCode.Success);
        }

        Assert.True(accepted, "the only match must be returned, shortened, rather than dropped.");
        Assert.True(
            stream.Length <= MachineOutputBudget.MinResponseBytes,
            $"the response was {stream.Length:N0} bytes against a {MachineOutputBudget.MinResponseBytes:N0}-byte ceiling.");

        JsonElement[] records = [.. Encoding.UTF8.GetString(stream.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())];

        // Shortening silently would be worse than dropping: the client would read a truncated line as
        // the whole line. The cut is reported per field, and the reduced ceilings are explained once.
        JsonElement match = Assert.Single(records, r => r.GetProperty("type").GetString() == "match");
        Assert.Equal(
            512 * 1024,
            match.GetProperty("data").GetProperty("truncated").GetProperty("text").GetInt32());
        Assert.Contains(records, r =>
            r.GetProperty("type").GetString() == "warning"
            && r.GetProperty("data").GetProperty("code").GetString() == "fields-shrunk");
    }

    [Fact]
    public void Create_NeverProducesABudgetInWhichNoRecordCanFit()
    {
        // Every field's range is legal on its own, so the extremes are all reachable from the Settings
        // number boxes. Clamped independently, a 1 MiB line ceiling against a 64 KiB response ceiling
        // means the first match is always refused and every search returns nothing with exit code 0.
        var budget = MachineOutputBudget.Create(
            maxLineBytes: MachineOutputBudget.MaxLineBytesCeiling,
            maxContextLineBytes: MachineOutputBudget.MaxLineBytesCeiling,
            maxPathBytes: MachineOutputBudget.MaxPathBytesCeiling,
            maxContextLines: MachineOutputBudget.MaxContextLinesCeiling,
            maxRecords: 500,
            maxResponseBytes: MachineOutputBudget.MinResponseBytes);

        using var stream = new MemoryStream();
        bool accepted;
        using (var writer = new MachineOutputWriter(stream, "req-1", budget))
            accepted = writer.WriteMatch(Match(text: new string('x', 4096)));

        Assert.True(accepted, "the worst legal settings combination must still emit a match.");
    }

    [Fact]
    public void NoteBudgetExhausted_MarksTruncated_SoACallerSideDropIsNotReportedAsCancelled()
    {
        // Confinement filtering drops matches before WriteMatch, so nothing set Truncated. The search
        // still self-cancelled once the page filled, and the summary reported `cancelled` with
        // truncated:false — indistinguishable from the user pressing Ctrl+C mid-scan.
        var budget = MachineOutputBudget.Create(
            maxLineBytes: 4096,
            maxContextLineBytes: 4096,
            maxPathBytes: MachineOutputBudget.MinPathBytes,
            maxContextLines: 0,
            maxRecords: 1,
            maxResponseBytes: MachineOutputBudget.MaxResponseBytesCeiling);

        using var stream = new MemoryStream();
        using var writer = new MachineOutputWriter(stream, "req-1", budget);

        Assert.False(writer.NoteBudgetExhausted(), "an empty page is not exhausted.");
        Assert.False(writer.Truncated, "probing an unfilled page must not claim truncation.");

        Assert.True(writer.WriteMatch(Match(text: "hit")));
        Assert.False(writer.Truncated, "the accepted record fit, so nothing was suppressed yet.");

        Assert.True(writer.NoteBudgetExhausted(), "the record ceiling was reached.");
        Assert.True(writer.Truncated, "a caller-side drop against a full page is a truncation.");
    }

    [Fact]
    public void WarningRecords_DoNotConsumeTheResponseByteCeiling()
    {
        // A scan over a drive raises one warning per unreadable root. Charging those to the payload
        // ceiling let a few hundred of them consume the whole response, after which the FIRST match was
        // refused and the scan self-cancelled: zero results, truncated: true, exit code 0.
        var budget = MachineOutputBudget.Create(
            maxLineBytes: 4096,
            maxContextLineBytes: 4096,
            maxPathBytes: MachineOutputBudget.MinPathBytes,
            maxContextLines: 0,
            maxRecords: 500,
            maxResponseBytes: MachineOutputBudget.MinResponseBytes);

        using var stream = new MemoryStream();
        bool accepted;
        using (var writer = new MachineOutputWriter(stream, "req-1", budget))
        {
            for (int i = 0; i < 200; i++)
                writer.WriteWarning("search-error", new string('w', 1024));

            accepted = writer.WriteMatch(Match(text: "hit"));
        }

        Assert.True(accepted, "warnings exhausted the payload budget before any match could be written.");
    }

    [Fact]
    public void WarningRecords_AreCappedAndTheDropIsReported()
    {
        var budget = MachineOutputBudget.Create(
            maxLineBytes: 4096,
            maxContextLineBytes: 4096,
            maxPathBytes: MachineOutputBudget.MinPathBytes,
            maxContextLines: 0,
            maxRecords: 5,
            maxResponseBytes: MachineOutputBudget.MaxResponseBytesCeiling);

        int suppressed = 0;
        List<JsonElement> records = Run(
            writer =>
            {
                for (int i = 0; i < 20; i++)
                    writer.WriteWarning("search-error", "denied");

                suppressed = writer.SuppressedWarnings;
                writer.WriteSummary(CliExitCode.Success);
            },
            budget);

        Assert.Equal(15, suppressed);
        Assert.Equal(15, records[^1].GetProperty("data").GetProperty("suppressedWarnings").GetInt32());
    }

    [Fact]
    public void RecordsStopBeingEmitted_OnceTheAccumulatedCeilingIsReached()
    {
        var budget = MachineOutputBudget.Create(
            maxLineBytes: 4096,
            maxContextLineBytes: 4096,
            maxPathBytes: MachineOutputBudget.MinPathBytes,
            maxContextLines: 0,
            maxRecords: 100_000,
            maxResponseBytes: MachineOutputBudget.MinResponseBytes);

        using var stream = new MemoryStream();
        using (var writer = new MachineOutputWriter(stream, "req-1", budget))
        {
            for (int i = 0; i < 5_000; i++)
            {
                if (!writer.WriteMatch(Match(line: i, text: new string('y', 2048))))
                    break;
            }
            writer.WriteSummary(CliExitCode.Success);
        }

        Assert.True(
            stream.Length <= MachineOutputBudget.MinResponseBytes,
            $"the response reached {stream.Length:N0} bytes against a {MachineOutputBudget.MinResponseBytes:N0}-byte ceiling.");
    }

    [Fact]
    public void ATruncatedPath_IsFlaggedSoAConsumerCanRefuseToAuthorizeIt()
    {
        // A shortened path can name a directory that IS inside an allowed root while the real path is
        // not, so a consumer must be able to tell it apart from a complete one.
        var budget = MachineOutputBudget.Create(
            maxLineBytes: 8192,
            maxContextLineBytes: 4096,
            maxPathBytes: MachineOutputBudget.MinPathBytes,
            maxContextLines: 0,
            maxRecords: 500,
            maxResponseBytes: 4L * 1024 * 1024);

        string longPath = @"C:\allowed\" + new string('d', 400) + @"\junction\secret.txt";

        var records = Run(w => w.WriteMatch(Match(path: longPath)), budget);

        JsonElement data = records[0].GetProperty("data");
        Assert.True(data.GetProperty("pathTruncated").GetBoolean());
        Assert.NotEqual(longPath, data.GetProperty("path").GetString());
    }

    [Fact]
    public void ACompletePath_CarriesNoTruncationFlag()
    {
        var records = Run(w => w.WriteMatch(Match(path: @"C:\allowed\file.cs")));

        Assert.False(records[0].GetProperty("data").TryGetProperty("pathTruncated", out _));
    }

    [Fact]
    public void EveryRecord_CarriesSchemaVersionTypeSequenceAndRequestId()
    {
        var records = Run(w =>
        {
            w.WriteMatch(Match());
            w.WriteWarning("fallback", "everything unavailable");
            w.WriteSummary(CliExitCode.Success);
        });

        Assert.Equal(3, records.Count);
        for (int i = 0; i < records.Count; i++)
        {
            Assert.Equal(MachineOutputWriter.SchemaVersion, records[i].GetProperty("schemaVersion").GetInt32());
            Assert.Equal(i + 1, records[i].GetProperty("sequence").GetInt64());
            Assert.Equal("req-1", records[i].GetProperty("requestId").GetString());
            Assert.False(string.IsNullOrEmpty(records[i].GetProperty("type").GetString()));
        }

        Assert.Equal("match", records[0].GetProperty("type").GetString());
        Assert.Equal("warning", records[1].GetProperty("type").GetString());
        Assert.Equal("summary", records[2].GetProperty("type").GetString());
    }

    [Fact]
    public void Records_AreOnePerLineSoAConsumerCanParseIncrementally()
    {
        using var stream = new MemoryStream();
        using (var writer = new MachineOutputWriter(stream, "req-1"))
        {
            // A payload containing a raw newline must be escaped inside the record, never split across
            // two lines — an NDJSON consumer reads exactly one record per line.
            writer.WriteMatch(Match(text: "first\nsecond"));
            writer.WriteMatch(Match(line: 13));
        }

        string text = Encoding.UTF8.GetString(stream.ToArray());
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.EndsWith("\n", text, StringComparison.Ordinal);
        Assert.Equal(2, lines.Length);
        Assert.Equal(
            "first\nsecond",
            JsonDocument.Parse(lines[0]).RootElement.GetProperty("data").GetProperty("text").GetString());
    }

    [Fact]
    public void WriteMatch_EmitsPositionAndText()
    {
        var records = Run(w => w.WriteMatch(Match(before: ["above"], after: ["below"])));

        var data = records[0].GetProperty("data");
        Assert.Equal(@"C:\repo\file.cs", data.GetProperty("path").GetString());
        Assert.Equal(12, data.GetProperty("line").GetInt32());
        Assert.Equal(4, data.GetProperty("column").GetInt32());
        Assert.Equal(3, data.GetProperty("length").GetInt32());
        Assert.Equal("var x = 1;", data.GetProperty("text").GetString());
        Assert.Equal("above", data.GetProperty("contextBefore")[0].GetString());
        Assert.Equal("below", data.GetProperty("contextAfter")[0].GetString());
        Assert.False(data.TryGetProperty("truncated", out _));
    }

    [Fact]
    public void WriteMatch_MultilineSpan_CarriesTheEndPosition()
    {
        var result = Match();
        result.MatchEndLineNumber = 15;
        result.MatchEndColumn = 7;

        var records = Run(w => w.WriteMatch(result));

        var data = records[0].GetProperty("data");
        Assert.Equal(15, data.GetProperty("endLine").GetInt32());
        Assert.Equal(7, data.GetProperty("endColumn").GetInt32());
    }

    [Fact]
    public void WriteMatch_SingleLineMatch_OmitsTheEndPosition()
    {
        var records = Run(w => w.WriteMatch(Match()));

        Assert.False(records[0].GetProperty("data").TryGetProperty("endLine", out _));
    }

    [Fact]
    public void WriteMatch_OversizedLine_IsTruncatedWithMetadataInsteadOfReturnedWhole()
    {
        var budget = MachineOutputBudget.Default with { MaxLineBytes = 64 };
        string huge = new('z', 10_000);

        var records = Run(w => w.WriteMatch(Match(text: huge)), budget);

        var data = records[0].GetProperty("data");
        Assert.Equal(64, Encoding.UTF8.GetByteCount(data.GetProperty("text").GetString()!));
        Assert.Equal(10_000, data.GetProperty("truncated").GetProperty("text").GetInt32());
    }

    [Fact]
    public void WriteMatch_TooManyContextLines_ReportsTheDroppedCount()
    {
        var budget = MachineOutputBudget.Default with { MaxContextLines = 2 };
        string[] before = ["a", "b", "c", "d"];

        var records = Run(w => w.WriteMatch(Match(before: before)), budget);

        var data = records[0].GetProperty("data");
        Assert.Equal(2, data.GetProperty("contextBefore").GetArrayLength());
        Assert.Equal(4, data.GetProperty("truncated").GetProperty("contextBefore.count").GetInt32());
    }

    [Fact]
    public void WriteMatch_OversizedContextLine_IsTruncatedWithItsOriginalByteCount()
    {
        var budget = MachineOutputBudget.Default with { MaxContextLineBytes = 64 };
        string contextLine = new('c', 10_000);

        var records = Run(w => w.WriteMatch(Match(before: [contextLine])), budget);

        JsonElement data = records[0].GetProperty("data");
        Assert.Equal(64, Encoding.UTF8.GetByteCount(data.GetProperty("contextBefore")[0].GetString()!));
        Assert.Equal(10_000, data.GetProperty("truncated").GetProperty("contextBefore[0]").GetInt32());
    }

    // ── Truncation the SEARCH ENGINE performed before the writer saw the line ────────────────────
    //
    // ContentSearcher trims a long match line to LineTruncationLength and marks the cut with an
    // ellipsis, so the value reaches this writer already short and the byte budget never fires.
    // Without an explicit flag a client cannot tell a complete 500-character line from a 10 MB line
    // reduced to 500 characters, which is exactly the distinction the protocol promises.

    [Theory]
    [InlineData("\u2026trimmed at the front")]
    [InlineData("trimmed at the back\u2026")]
    [InlineData("\u2026trimmed at both ends\u2026")]
    public void WriteMatch_LineAlreadyTrimmedByTheEngine_IsFlagged(string matchLine)
    {
        var records = Run(w => w.WriteMatch(Match(text: matchLine)));

        Assert.True(records[0].GetProperty("data").GetProperty("engineTruncatedText").GetBoolean());
    }

    [Theory]
    [InlineData("an ordinary complete line")]
    [InlineData("")]
    [InlineData("an ellipsis \u2026 in the middle is not a trim marker")]
    public void WriteMatch_LineTheEngineDidNotTrim_IsNotFlagged(string matchLine)
    {
        var records = Run(w => w.WriteMatch(Match(text: matchLine)));

        Assert.False(records[0].GetProperty("data").TryGetProperty("engineTruncatedText", out _));
    }

    [Fact]
    public void EngineTrimDetection_UsesTheSharedEllipsisConstant()
    {
        // Pinned to LineTruncator so a change to the marker cannot silently break the signal.
        Assert.True(MachineOutputWriter.WasTrimmedBySearchEngine(Yagu.Helpers.LineTruncator.Ellipsis + "x"));
        Assert.True(MachineOutputWriter.WasTrimmedBySearchEngine("x" + Yagu.Helpers.LineTruncator.Ellipsis));
        Assert.False(MachineOutputWriter.WasTrimmedBySearchEngine("x"));
        Assert.False(MachineOutputWriter.WasTrimmedBySearchEngine(null));
    }

    [Fact]
    public void EngineTruncationAndBudgetTruncation_AreReportedSeparately()
    {
        // Two different provenances: the engine trimmed for display, the writer trimmed for the wire.
        var budget = MachineOutputBudget.Default with { MaxLineBytes = 64 };
        string engineTrimmed = "\u2026" + new string('z', 10_000);

        var records = Run(w => w.WriteMatch(Match(text: engineTrimmed)), budget);

        var data = records[0].GetProperty("data");
        Assert.True(data.GetProperty("engineTruncatedText").GetBoolean());
        // Bytes, not characters: the ellipsis costs 3 UTF-8 bytes on top of the 10,000 'z'.
        Assert.Equal(10_003, data.GetProperty("truncated").GetProperty("text").GetInt32());
    }

    [Fact]
    public void WriteMatch_StopsAtTheRecordCeilingAndReportsTruncation()
    {
        var budget = MachineOutputBudget.Default with { MaxRecords = 2 };

        using var stream = new MemoryStream();
        using var writer = new MachineOutputWriter(stream, "req-1", budget);

        Assert.True(writer.WriteMatch(Match()));
        Assert.True(writer.WriteMatch(Match()));
        Assert.False(writer.WriteMatch(Match()));

        Assert.True(writer.Truncated);
        Assert.Equal(2, writer.PayloadRecords);
    }

    [Fact]
    public void WriteMatch_StopsAtTheResponseByteCeiling()
    {
        var budget = MachineOutputBudget.Default with { MaxResponseBytes = MachineOutputBudget.MinResponseBytes };

        using var stream = new MemoryStream();
        using var writer = new MachineOutputWriter(stream, "req-1", budget);

        int written = 0;
        while (writer.WriteMatch(Match(text: new string('q', 4000))))
            written++;

        Assert.True(writer.Truncated);
        Assert.True(written > 0, "at least one match must fit inside the minimum response budget");
        Assert.True(stream.Length < MachineOutputBudget.MinResponseBytes * 2);
    }

    [Fact]
    public void ControlRecords_AreStillEmittedAfterTheBudgetIsSpent()
    {
        var budget = MachineOutputBudget.Default with { MaxRecords = 1 };

        var records = Run(w =>
        {
            w.WriteMatch(Match());
            w.WriteMatch(Match());
            w.WriteWarning("budget", "page full");
            w.WriteSummary(CliExitCode.Success);
        }, budget);

        Assert.Equal(3, records.Count);
        Assert.Equal("summary", records[^1].GetProperty("type").GetString());
        Assert.True(records[^1].GetProperty("data").GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void WriteSummary_NeverClaimsAKnownTotal()
    {
        // Each invocation is a fresh bounded scan, so there is no stable global total to report.
        var records = Run(w => w.WriteSummary(CliExitCode.NoResults));

        var data = records[0].GetProperty("data");
        Assert.False(data.GetProperty("totalKnown").GetBoolean());
        Assert.Equal(CliExitCode.NoResults, data.GetProperty("exitCode").GetInt32());
        Assert.Equal("no-results", data.GetProperty("outcome").GetString());
    }

    [Fact]
    public void WriteError_CarriesTheCodeAndTheExitOutcome()
    {
        var records = Run(w => w.WriteError("file-not-found", @"missing C:\nope.txt", CliExitCode.InvalidInput));

        var data = records[0].GetProperty("data");
        Assert.Equal("file-not-found", data.GetProperty("code").GetString());
        Assert.Equal(CliExitCode.InvalidInput, data.GetProperty("exitCode").GetInt32());
        Assert.Equal("invalid-input", data.GetProperty("outcome").GetString());
    }

    [Fact]
    public void ProgressAndWarningRecords_DoNotConsumeThePayloadBudget()
    {
        var budget = MachineOutputBudget.Default with { MaxRecords = 1 };

        using var stream = new MemoryStream();
        using var writer = new MachineOutputWriter(stream, "req-1", budget);

        writer.WriteProgress(10, 20, 30, 50);
        writer.WriteWarning("w", "still fine");
        Assert.True(writer.WriteMatch(Match()));

        Assert.Equal(1, writer.PayloadRecords);
        Assert.False(writer.Truncated);
    }

    [Fact]
    public void NewRequestId_ProducesDistinctCorrelationIds()
    {
        Assert.NotEqual(MachineOutputWriter.NewRequestId(), MachineOutputWriter.NewRequestId());
    }

    [Fact]
    public void BlankRequestId_IsReplacedWithAGeneratedOne()
    {
        using var stream = new MemoryStream();
        using var writer = new MachineOutputWriter(stream, "   ");

        Assert.False(string.IsNullOrWhiteSpace(writer.RequestId));
    }

    [Fact]
    public void SuppliedRequestId_IsPreserved()
    {
        using var stream = new MemoryStream();
        using var writer = new MachineOutputWriter(stream, "req-42");

        Assert.Equal("req-42", writer.RequestId);
    }

    // ── Non-search payload records ──
    //
    // Every tool that is not a search — read, calc, index status, session listing — reports through
    // WriteResult, so it carries the same budget and correlation guarantees as a match.

    [Fact]
    public void WriteResult_EmitsAResultRecordCarryingTheSuppliedData()
    {
        List<JsonElement> records = Run(w => Assert.True(w.WriteResult(d =>
        {
            d.WriteString("kind", "read");
            d.WriteNumber("line", 7);
        })));

        Assert.Single(records);
        Assert.Equal(MachineProtocol.Types.Result, records[0].GetProperty("type").GetString());

        var data = records[0].GetProperty("data");
        Assert.Equal("read", data.GetProperty("kind").GetString());
        Assert.Equal(7, data.GetProperty("line").GetInt32());
    }

    [Fact]
    public void WriteResult_CountsAgainstThePayloadBudget()
    {
        using var stream = new MemoryStream();
        using var writer = new MachineOutputWriter(stream, "req-1");

        writer.WriteResult(d => d.WriteString("k", "v"));
        writer.WriteResult(d => d.WriteString("k", "v"));

        Assert.Equal(2, writer.PayloadRecords);
    }

    [Fact]
    public void WriteResult_OnceTheRecordCeilingIsReached_IsRefusedAndFlaggedAsTruncated()
    {
        var budget = MachineOutputBudget.Default with { MaxRecords = 1 };

        using var stream = new MemoryStream();
        using var writer = new MachineOutputWriter(stream, "req-1", budget);

        Assert.True(writer.WriteResult(d => d.WriteString("k", "first")));
        Assert.False(writer.WriteResult(d => d.WriteString("k", "second")));

        // A consumer must be able to tell "no more results" from "results were withheld".
        Assert.True(writer.Truncated);
        Assert.Equal(1, writer.PayloadRecords);
    }

    [Fact]
    public void WriteDiagnostic_EmitsADiagnosticRecordWithoutSpendingThePayloadBudget()
    {
        var budget = MachineOutputBudget.Default with { MaxRecords = 1 };

        using var stream = new MemoryStream();
        using var writer = new MachineOutputWriter(stream, "req-1", budget);

        writer.WriteDiagnostic(d => d.WriteString("settingsSource", "portable"));

        // An environment fact must never cost a caller one of their results.
        Assert.Equal(0, writer.PayloadRecords);
        Assert.False(writer.Truncated);
        Assert.True(writer.WriteResult(d => d.WriteString("k", "v")));
    }

    [Fact]
    public void WriteDiagnostic_IsTaggedAsADiagnosticRecord()
    {
        List<JsonElement> records = Run(w => w.WriteDiagnostic(d => d.WriteString("settingsSource", "portable")));

        Assert.Single(records);
        Assert.Equal(MachineProtocol.Types.Diagnostic, records[0].GetProperty("type").GetString());
        Assert.Equal("portable", records[0].GetProperty("data").GetProperty("settingsSource").GetString());
    }

    [Fact]
    public void Sequence_CountsEveryEmittedRecordIncludingControlRecords()
    {
        using var stream = new MemoryStream();
        using var writer = new MachineOutputWriter(stream, "req-1");

        Assert.Equal(0, writer.Sequence);

        writer.WriteResult(d => d.WriteString("k", "v"));
        Assert.Equal(1, writer.Sequence);

        writer.WriteDiagnostic(d => d.WriteString("k", "v"));
        writer.WriteProgress(1, 2, 3, null);
        Assert.Equal(3, writer.Sequence);
    }

    [Fact]
    public void WriteResult_NullWriter_IsRejected()
    {
        using var stream = new MemoryStream();
        using var writer = new MachineOutputWriter(stream, "req-1");

        Assert.Throws<ArgumentNullException>(() => writer.WriteResult(null!));
    }

    [Fact]
    public void APlanRecord_IsEmittedWithoutSpendingAnyOfThePayloadBudget()
    {
        var budget = MachineOutputBudget.Create(
            MachineOutputBudget.MinLineBytes, MachineOutputBudget.MinLineBytes, MachineOutputBudget.MinPathBytes,
            0, maxRecords: 1, MachineOutputBudget.MinResponseBytes);

        List<JsonElement> records = Run(
            w =>
            {
                w.WritePlan(p => p.WriteString("interpretation", "find TODOs in C#"));

                // The plan explains what the search was turned into, so it must not be the reason the
                // one match a caller asked for is dropped.
                Assert.True(w.WriteMatch(Match()));
            },
            budget);

        Assert.Contains(records, r => r.GetProperty("type").GetString() == "plan");
        Assert.Contains(records, r => r.GetProperty("type").GetString() == "match");
    }

    [Fact]
    public void ControlBytes_AreTrackedSeparatelyFromThePayloadTheyExplain()
    {
        using var stream = new MemoryStream();
        using var writer = new MachineOutputWriter(stream, "req-1");

        writer.WriteWarning("access-denied", "one folder could not be read");
        long controlAfterWarning = writer.ControlBytesWritten;

        Assert.True(controlAfterWarning > 0);
        Assert.Equal(0, writer.PayloadBytesWritten);

        writer.WriteMatch(Match());

        // Charging warnings to the payload ceiling is how a few hundred access-denied notices used to
        // consume the whole response and return zero matches.
        Assert.Equal(controlAfterWarning, writer.ControlBytesWritten);
        Assert.True(writer.PayloadBytesWritten > 0);
    }

    [Fact]
    public void WarningsBeyondTheCeiling_AreCountedRatherThanEmitted()
    {
        var budget = MachineOutputBudget.Create(
            MachineOutputBudget.MinLineBytes, MachineOutputBudget.MinLineBytes, MachineOutputBudget.MinPathBytes,
            0, maxRecords: 3, MachineOutputBudget.MinResponseBytes);

        int suppressed = 0;

        List<JsonElement> records = Run(
            w =>
            {
                for (int i = 0; i < 10; i++)
                    w.WriteWarning("access-denied", $"folder {i} could not be read");

                suppressed = w.SuppressedWarnings;
            },
            budget);

        // The count is the honest report: silently dropping them would let a caller believe the scan
        // covered everything it was pointed at.
        int emitted = records.Count(r => r.GetProperty("type").GetString() == "warning");
        Assert.Equal(3, emitted);
        Assert.Equal(7, suppressed);
        Assert.Equal(10, emitted + suppressed);
    }

    [Fact]
    public void AFirstMatchThatCannotBeEncodedEvenAtEveryFloor_SaysSoInsteadOfReportingNoResults()
    {
        // JSON escaping is why this arm exists: Utf8JsonWriter encodes '<' as \u003C, so a line sized
        // against RAW bytes can still be six times larger on the wire. Once every field is at its floor
        // there is nothing left to give up, and returning an empty page here would report "no matches"
        // for a search that genuinely matched, blaming no ceiling the user could raise.
        var floors = MachineOutputBudget.Create(
            MachineOutputBudget.MinLineBytes,
            MachineOutputBudget.MinLineBytes,
            MachineOutputBudget.MinPathBytes,
            MachineOutputBudget.MinContextLines,
            MachineOutputBudget.MinRecords,
            MachineOutputBudget.MinResponseBytes);
        Assert.Null(floors.Shrink());

        // Create() guarantees a floor-sized record fits, so the only way to reach exhaustion is a
        // budget that did not come from it — which the writer's constructor accepts.
        var budget = floors with { MaxResponseBytes = 512 };

        bool wrote = true;
        List<JsonElement> records = Run(
            w => wrote = w.WriteMatch(Match(
                path: @"C:\repo\" + new string('<', MachineOutputBudget.MinPathBytes - 8),
                text: new string('<', MachineOutputBudget.MinLineBytes))),
            budget);

        Assert.False(wrote);
        JsonElement warning = Assert.Single(records, r => r.GetProperty("type").GetString() == "warning");
        JsonElement data = warning.GetProperty("data");
        Assert.Equal("record-too-large", data.GetProperty("code").GetString());
        Assert.Contains("maximum response size", data.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(records, r => r.GetProperty("type").GetString() == "result");
    }

    [Fact]
    public void AFirstCallerComposedResultThatDoesNotFit_IsRefusedAudibly()
    {
        // WriteResult takes a record the caller already composed, so the writer cannot shrink it. The
        // only honest option left is to say why the page is empty.
        var budget = MachineOutputBudget.Create(
            MachineOutputBudget.MinLineBytes,
            MachineOutputBudget.MinLineBytes,
            MachineOutputBudget.MinPathBytes,
            MachineOutputBudget.MinContextLines,
            MachineOutputBudget.MinRecords,
            MachineOutputBudget.MinResponseBytes);

        bool wrote = true;
        List<JsonElement> records = Run(
            w => wrote = w.WriteResult(x => x.WriteString(
                "text", new string('x', 4 * 1024 * 1024))),
            budget);

        Assert.False(wrote);
        JsonElement warning = Assert.Single(records, r => r.GetProperty("type").GetString() == "warning");
        JsonElement data = warning.GetProperty("data");
        Assert.Equal("record-too-large", data.GetProperty("code").GetString());
        Assert.Contains("nothing could be returned", data.GetProperty("message").GetString(), StringComparison.Ordinal);
    }
}
