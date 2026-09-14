using System.Text.Json;
using Yagu.Services.Cli.MachineOutput;

namespace Yagu.Tests;

/// <summary>
/// Covers the bridge-side decoder for the CLI machine protocol. The reader is deliberately strict: a
/// bridge that guessed at a malformed or future-versioned line would surface fabricated results to an
/// agent, which is worse than reporting a protocol error.
/// </summary>
public class MachineOutputReaderTests
{
    private static string Envelope(
        string type = "match",
        int schemaVersion = MachineProtocol.SchemaVersion,
        long sequence = 1,
        string requestId = "req-1",
        string data = """{"path":"C:\\a.cs","line":3}""")
        => $$"""{"schemaVersion":{{schemaVersion}},"type":"{{type}}","sequence":{{sequence}},"requestId":"{{requestId}}","data":{{data}}}""";

    [Fact]
    public void TryParse_WellFormedRecord_ExposesTheEnvelopeAndPayload()
    {
        Assert.True(MachineOutputReader.TryParse(Envelope(), out var record, out string? error));

        Assert.Null(error);
        Assert.Equal(MachineProtocol.SchemaVersion, record.SchemaVersion);
        Assert.Equal("match", record.Type);
        Assert.Equal(1, record.Sequence);
        Assert.Equal("req-1", record.RequestId);
        Assert.Equal(@"C:\a.cs", record.GetString("path"));
        Assert.Equal(3, record.GetInt64("line"));
    }

    [Fact]
    public void ParsedData_SurvivesTheSourceDocumentBeingDisposed()
    {
        // The reader clones the payload; without that, every accessor would throw once the underlying
        // JsonDocument was disposed at the end of the parse.
        MachineOutputReader.TryParse(Envelope(), out var record, out _);

        GC.Collect();
        GC.WaitForPendingFinalizers();

        Assert.Equal(@"C:\a.cs", record.GetString("path"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryParse_BlankLine_IsSkippedWithoutBeingTreatedAsCorruption(string? line)
    {
        Assert.False(MachineOutputReader.TryParse(line, out _, out string? error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{ unterminated")]
    [InlineData("[1,2,3]")]
    [InlineData("42")]
    [InlineData("\"a string\"")]
    public void TryParse_MalformedLine_ReportsAProtocolError(string line)
    {
        Assert.False(MachineOutputReader.TryParse(line, out _, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryParse_FutureSchemaVersion_IsRejectedRatherThanGuessedAt()
    {
        string line = Envelope(schemaVersion: MachineProtocol.SchemaVersion + 1);

        Assert.False(MachineOutputReader.TryParse(line, out _, out string? error));
        Assert.Contains("unsupported protocol schemaVersion", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TryParse_NonsenseSchemaVersion_IsRejected(int schemaVersion)
    {
        Assert.False(MachineOutputReader.TryParse(Envelope(schemaVersion: schemaVersion), out _, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("""{"type":"match","sequence":1,"requestId":"r"}""")]
    [InlineData("""{"schemaVersion":1,"sequence":1,"requestId":"r"}""")]
    [InlineData("""{"schemaVersion":1,"type":"match","requestId":"r"}""")]
    [InlineData("""{"schemaVersion":"1","type":"match","sequence":1}""")]
    [InlineData("""{"schemaVersion":1,"type":7,"sequence":1}""")]
    [InlineData("""{"schemaVersion":1,"type":"match","sequence":"1"}""")]
    [InlineData("""{"schemaVersion":1,"type":"match","sequence":null}""")]
    [InlineData("""{"schemaVersion":1,"type":"match","sequence":true}""")]
    [InlineData("""{"schemaVersion":1,"type":"match","sequence":[]}""")]
    [InlineData("""{"schemaVersion":1,"type":"match","sequence":{}}""")]
    public void TryParse_MissingOrMistypedEnvelopeField_IsRejected(string line)
    {
        Assert.False(MachineOutputReader.TryParse(line, out _, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    // Fractional and out-of-range numbers pass the ValueKind == Number guard, so GetInt32/GetInt64 threw
    // out of a decoder whose contract is that it never throws on well-formed-but-unexpected input. In the
    // bridge that faulted the stdout reader instead of yielding a ProtocolError outcome.
    [InlineData("""{"schemaVersion":1.5,"type":"match","sequence":1,"requestId":"r"}""")]
    [InlineData("""{"schemaVersion":99999999999,"type":"match","sequence":1,"requestId":"r"}""")]
    [InlineData("""{"schemaVersion":1e9,"type":"match","sequence":1,"requestId":"r"}""")]
    [InlineData("""{"schemaVersion":1,"type":"match","sequence":2.5,"requestId":"r"}""")]
    [InlineData("""{"schemaVersion":1,"type":"match","sequence":9e99,"requestId":"r"}""")]
    public void TryParse_HostileEnvelopeNumber_IsRejectedWithoutThrowing(string line)
    {
        bool parsed = false;
        string? error = null;

        Exception? thrown = Record.Exception(() => parsed = MachineOutputReader.TryParse(line, out _, out error));

        Assert.Null(thrown);
        Assert.False(parsed);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryParse_RecordWithoutData_IsStillValid()
    {
        string line = """{"schemaVersion":1,"type":"summary","sequence":9,"requestId":"r"}""";

        Assert.True(MachineOutputReader.TryParse(line, out var record, out _));

        Assert.Equal("summary", record.Type);
        Assert.False(record.HasData);
        Assert.Null(record.GetString("anything"));
    }

    [Fact]
    public void TryParse_MissingRequestId_DefaultsToEmptyRatherThanFailing()
    {
        string line = """{"schemaVersion":1,"type":"warning","sequence":2,"data":{"a":1}}""";

        Assert.True(MachineOutputReader.TryParse(line, out var record, out _));
        Assert.Equal(string.Empty, record.RequestId);
    }

    [Fact]
    public void Accessors_ReturnNullForAMissingOrMistypedProperty()
    {
        MachineOutputReader.TryParse(Envelope(data: """{"path":"x","line":"not-a-number","flag":"no"}"""), out var record, out _);

        Assert.Equal("x", record.GetString("path"));
        Assert.Null(record.GetInt64("line"));
        Assert.Null(record.GetBoolean("flag"));
        Assert.Null(record.GetString("absent"));
        Assert.Null(record.GetInt64("absent"));
        Assert.Null(record.GetBoolean("absent"));
    }

    [Fact]
    public void GetInt64_OutOfRangeNumber_ReturnsNullAndPreservesOtherFields()
    {
        Assert.True(MachineOutputReader.TryParse(
            Envelope(data: """{"overflow":9223372036854775808,"minimum":-9223372036854775808,"maximum":9223372036854775807}"""),
            out var record, out string? error));

        Assert.Null(error);
        Assert.Null(record.GetInt64("overflow"));
        Assert.Equal(long.MinValue, record.GetInt64("minimum"));
        Assert.Equal(long.MaxValue, record.GetInt64("maximum"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void TryParse_NonStringRequestId_DefaultsToEmpty(string requestId)
    {
        string line = $$"""{"schemaVersion":1,"type":"summary","sequence":1,"requestId":{{requestId}}}""";

        Assert.True(MachineOutputReader.TryParse(line, out var record, out string? error));
        Assert.Null(error);
        Assert.Equal(string.Empty, record.RequestId);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    [InlineData("true")]
    public void Accessors_NonObjectPayload_ReturnNull(string data)
    {
        Assert.True(MachineOutputReader.TryParse(Envelope(data: data), out var record, out string? error));
        Assert.Null(error);
        Assert.False(record.HasData);
        Assert.Null(record.GetString("field"));
        Assert.Null(record.GetInt64("field"));
        Assert.Null(record.GetDouble("field"));
        Assert.Null(record.GetBoolean("field"));
    }

    [Fact]
    public void GetInt64_ReturnsNullForAFractionalNumber_RatherThanThrowing()
    {
        // JsonElement.GetInt64 throws FormatException on a fractional number. A protocol decoder that
        // threw on well-formed-but-unexpected input would fail the whole tool call; the summary's
        // elapsedSeconds is exactly such a field.
        MachineOutputReader.TryParse(
            Envelope(type: "summary", data: """{"elapsedSeconds":0.078,"filesScanned":6}"""), out var record, out _);

        Assert.Null(record.GetInt64("elapsedSeconds"));
        Assert.Equal(0.078, record.GetDouble("elapsedSeconds"));
        Assert.Equal(6, record.GetInt64("filesScanned"));
    }

    [Fact]
    public void GetDouble_ReadsIntegralAndFractionalNumbersAlike()
    {
        MachineOutputReader.TryParse(
            Envelope(type: "summary", data: """{"whole":42,"fraction":1.5,"text":"nope"}"""), out var record, out _);

        Assert.Equal(42d, record.GetDouble("whole"));
        Assert.Equal(1.5, record.GetDouble("fraction"));
        Assert.Null(record.GetDouble("text"));
        Assert.Null(record.GetDouble("absent"));
    }

    [Fact]
    public void Accessors_ReadBooleans()
    {
        MachineOutputReader.TryParse(Envelope(type: "summary", data: """{"truncated":true,"totalKnown":false}"""), out var record, out _);

        Assert.True(record.GetBoolean("truncated"));
        Assert.False(record.GetBoolean("totalKnown"));
    }

    [Fact]
    public void WriterOutput_RoundTripsThroughTheReader()
    {
        // The two halves of the protocol are pinned against each other, so a change to one that the
        // other cannot decode fails here rather than in a live agent session.
        using var stream = new MemoryStream();
        using (var writer = new MachineOutputWriter(stream, "round-trip"))
        {
            writer.WriteMatch(new Models.SearchResult(@"C:\repo\a.cs", 12, "hit", 4, 3, [], []));
            writer.WriteWarning("fallback", "everything unavailable");
            writer.WriteSummary(CliExitCode.Success);
        }

        string[] lines = System.Text.Encoding.UTF8.GetString(stream.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var decoded = new List<MachineRecord>();
        foreach (string line in lines)
        {
            Assert.True(MachineOutputReader.TryParse(line, out var record, out string? error), error);
            decoded.Add(record);
        }

        Assert.Equal([MachineProtocol.Types.Match, MachineProtocol.Types.Warning, MachineProtocol.Types.Summary],
            decoded.Select(r => r.Type));
        Assert.Equal("round-trip", decoded[0].RequestId);
        Assert.Equal(@"C:\repo\a.cs", decoded[0].GetString("path"));
        Assert.Equal(12, decoded[0].GetInt64("line"));
        Assert.Equal(CliExitCode.Success, decoded[2].GetInt64("exitCode"));
    }

    [Fact]
    public void ReaderAndWriter_AgreeOnTheSchemaVersion()
    {
        Assert.Equal(MachineOutputWriter.SchemaVersion, MachineOutputReader.MaxSupportedSchemaVersion);
        Assert.Equal(MachineProtocol.SchemaVersion, MachineOutputWriter.SchemaVersion);
    }
}
