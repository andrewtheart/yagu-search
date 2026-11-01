using System.Diagnostics;
using System.Globalization;
using System.Text;

// A stand-in for Yagu.exe in machine mode. YaguCliBridge always prepends
// --cli --output json --non-interactive --settings-source global --request-id <id>, so the scenario is
// taken from a --fake-scenario argument the test appends after those.
//
// stdout carries protocol frames only, exactly as the real CLI does, so a BOM or a stray Console.Write
// here would fail the same tests it would fail in production.
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

string scenario = ValueAfter("--fake-scenario")
    ?? Environment.GetEnvironmentVariable("YAGU_FAKE_SCENARIO")
    ?? "normal";
string requestId = ValueAfter("--request-id") ?? "unknown";

switch (scenario)
{
    case "normal":
        Emit("match", requestId, 1, """{"path":"C:\\repo\\a.cs","line":3,"text":"hit"}""");
        Emit("summary", requestId, 2, """{"returnedRecords":1,"truncated":false,"exitCode":0}""");
        return 0;

    case "echo-args":
        // Reports the argument vector back as protocol records, so a test can prove arguments survive
        // spaces, quotes and trailing backslashes without being re-split by a shell.
        for (int i = 0; i < args.Length; i++)
            Emit("result", requestId, i + 1, $"{{\"index\":{i},\"value\":{Quote(args[i])}}}");
        Emit("summary", requestId, args.Length + 1, """{"returnedRecords":0,"truncated":false,"exitCode":0}""");
        return 0;

    case "no-summary":
        // Exits without closing its protocol stream, which must be reported as a crash whatever the
        // exit code says.
        Emit("match", requestId, 1, """{"path":"C:\\repo\\a.cs","line":1,"text":"partial"}""");
        return 0;

    case "malformed":
        Console.Out.WriteLine("{ this is not json");
        Console.Out.Flush();
        Emit("summary", requestId, 1, """{"returnedRecords":0,"truncated":false,"exitCode":0}""");
        return 0;

    case "nonzero-exit":
        Emit("error", requestId, 1, """{"code":"invalid-input","message":"bad","exitCode":2}""");
        Emit("summary", requestId, 2, """{"returnedRecords":0,"truncated":false,"exitCode":2}""");
        return 2;

    case "hang":
        // Never exits. The bridge must kill it on timeout rather than waiting for it.
        Emit("match", requestId, 1, """{"path":"C:\\repo\\a.cs","line":1,"text":"before the hang"}""");
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;

    case "orphan-holds-stdout":
    {
        // The case that made an unbounded stdout drain hang for ever: a grandchild inherits the stdout
        // handle and outlives its parent, so the pipe never reaches EOF even though the child exited.
        var psi = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--fake-scenario");
        psi.ArgumentList.Add("sleep-forever");
        Process.Start(psi);

        Emit("match", requestId, 1, """{"path":"C:\\repo\\a.cs","line":1,"text":"hit"}""");
        Emit("summary", requestId, 2, """{"returnedRecords":1,"truncated":false,"exitCode":0}""");
        return 0;
    }

    case "sleep-forever":
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;

    case "read-stdin":
        // Proves the child gets an already-closed stdin. If the bridge left its own stdin inherited,
        // this would block instead of reading null.
        Emit("result", requestId, 1, $$"""{"stdinLine":{{Quote(Console.In.ReadLine() ?? "<eof>")}}}""");
        Emit("summary", requestId, 2, """{"returnedRecords":1,"truncated":false,"exitCode":0}""");
        return 0;

    case "noisy-stderr":
        for (int i = 0; i < 2000; i++)
            Console.Error.WriteLine($"diagnostic line {i.ToString(CultureInfo.InvariantCulture)} {new string('x', 200)}");
        Emit("match", requestId, 1, """{"path":"C:\\repo\\a.cs","line":1,"text":"hit"}""");
        Emit("summary", requestId, 2, """{"returnedRecords":1,"truncated":false,"exitCode":0}""");
        return 0;

    case "unicode":
        Emit("match", requestId, 1, """{"path":"C:\\repo\\\u00e9\u4e2d\ud83d\ude00.cs","line":1,"text":"caf\u00e9\t\u4e2d\ud83d\ude00"}""");
        Emit("summary", requestId, 2, """{"returnedRecords":1,"truncated":false,"exitCode":0}""");
        return 0;

    case "blank-lines":
        // Padding between records. A reader that treated these as protocol errors would report a broken
        // stream for a run that is perfectly fine.
        Console.Out.WriteLine();
        Emit("match", requestId, 1, """{"path":"C:\\repo\\a.cs","line":1,"text":"hit"}""");
        Console.Out.WriteLine();
        Console.Out.WriteLine();
        Emit("summary", requestId, 2, """{"returnedRecords":1,"truncated":false,"exitCode":0}""");
        Console.Out.WriteLine();
        return 0;

    case "emit":
    {
        // The MCP tool classes are driven through this: a test writes the exact protocol frames the
        // real CLI would produce for the case it wants to reach, and the tool renders them for real.
        // Scripting it through the environment rather than the argument vector matters, because the
        // tool under test OWNS its arguments — a test cannot append a scenario flag to them.
        string? framesFile = Environment.GetEnvironmentVariable("YAGU_FAKE_FRAMES");
        if (framesFile is { Length: > 0 } && File.Exists(framesFile))
        {
            foreach (string frame in File.ReadAllLines(framesFile))
                Console.Out.WriteLine(frame.Replace("<REQUESTID>", requestId, StringComparison.Ordinal));
            Console.Out.Flush();
        }

        if (Environment.GetEnvironmentVariable("YAGU_FAKE_STDERR") is { Length: > 0 } diagnostic)
            Console.Error.WriteLine(diagnostic);

        return int.TryParse(
            Environment.GetEnvironmentVariable("YAGU_FAKE_EXIT"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int exitCode) ? exitCode : 0;
    }

    default:
        Console.Error.WriteLine($"unknown fake scenario '{scenario}'.");
        return 64;
}

string? ValueAfter(string flag)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }
    return null;
}

static string Quote(string value) => System.Text.Json.JsonSerializer.Serialize(value);

static void Emit(string type, string requestId, long sequence, string data)
{
    Console.Out.WriteLine(
        $$"""{"schemaVersion":1,"type":"{{type}}","sequence":{{sequence}},"requestId":{{Quote(requestId)}},"data":{{data}}}""");
    Console.Out.Flush();
}
