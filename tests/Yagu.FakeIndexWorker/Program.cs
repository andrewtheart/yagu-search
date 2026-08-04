using System.Diagnostics;
using System.Text.Json;
using System.Globalization;

if (args.Length == 1 && args[0] == "--ocr-process-tree-child")
{
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}

if (args.Length == 2 && args[0] == "--ocr-process-tree-parent")
{
    var childStartInfo = new ProcessStartInfo
    {
        FileName = Environment.ProcessPath,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    childStartInfo.ArgumentList.Add("--ocr-process-tree-child");
    using Process child = Process.Start(childStartInfo)
        ?? throw new InvalidOperationException("Could not start OCR process-tree test child.");
    File.WriteAllText(args[1], child.Id.ToString(CultureInfo.InvariantCulture));
    Send("{\"type\":\"ready\",\"mode\":\"ocr-process-tree-parent\"}");
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}

string scenario = File.Exists(Environment.ProcessPath + ".scenario")
    ? File.ReadAllText(Environment.ProcessPath + ".scenario").Trim()
    : "normal";

static void Send(string json)
{
    Console.Out.WriteLine(json);
    Console.Out.Flush();
}

if (scenario == "initError")
{
    Send("{\"type\":\"error\",\"error\":\"fake init error\"}");
    return;
}
if (scenario == "initErrorNoText")
{
    Send("{\"type\":\"error\"}");
    return;
}
if (scenario == "hangBeforeReady")
{
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}

int version = scenario == "mismatch" ? 4 : 3;
Send($"{{\"type\":\"ready\",\"controlProtocolVersion\":{version},\"epoch\":7}}");
if (scenario == "mismatch")
    return;
if (scenario == "exitAfterReady")
    return;

if (scenario == "stderr")
{
    Console.Error.WriteLine("[CRT] critical line");
    Console.Error.WriteLine("[WRN] warning line");
    Console.Error.WriteLine("[INF] info line");
    Console.Error.WriteLine("debug line");
}

string? line;
while ((line = Console.In.ReadLine()) is not null)
{
    using JsonDocument doc = JsonDocument.Parse(line);
    JsonElement root = doc.RootElement;
    string op = root.GetProperty("op").GetString() ?? "";
    int id = root.TryGetProperty("id", out JsonElement idElement) ? idElement.GetInt32() : 0;
    if (op == "shutdown" && scenario != "ignoreShutdown")
        return;
    if (op == "cancelBuild")
    {
        if (scenario != "ignoreCancel")
            Send($"{{\"type\":\"result\",\"id\":{id},\"ok\":false,\"outcomeKind\":\"cancelled\",\"error\":\"cancelled\"}}");
        continue;
    }

    File.WriteAllText(Environment.ProcessPath + ".request", id.ToString(CultureInfo.InvariantCulture));

    if (op is "openQueryScope" or "classifyPaths" or "reconcileB1" or "closeQueryScope" or "cancelSession")
    {
        HandleQuery(op, id, root, scenario);
        continue;
    }

    switch (scenario)
    {
        case "malformed":
            Send("this is not json");
            return;
        case "nullMessage":
            Send("null");
            return;
        case "progressBeforeAccepted":
            Send($"{{\"type\":\"progress\",\"id\":{id},\"percent\":10}}");
            return;
        case "duplicateAccepted":
            Send($"{{\"type\":\"accepted\",\"id\":{id},\"ok\":true}}");
            Send($"{{\"type\":\"accepted\",\"id\":{id},\"ok\":true}}");
            return;
        case "unknownMessage":
            Send($"{{\"type\":\"mystery\",\"id\":{id}}}");
            return;
        case "acceptOnly":
        case "ignoreCancel":
            Send($"{{\"type\":\"accepted\",\"id\":{id},\"ok\":true}}");
            Send($"{{\"type\":\"progress\",\"id\":{id},\"percent\":1}}");
            break;
        case "acceptThenExit":
            Send($"{{\"type\":\"accepted\",\"id\":{id},\"ok\":true}}");
            return;
        case "silent":
            break;
        case "closeInput":
            Send($"{{\"type\":\"accepted\",\"id\":{id},\"ok\":true}}");
            Send($"{{\"type\":\"progress\",\"id\":{id},\"percent\":1}}");
            Console.In.Close();
            Thread.Sleep(2000);
            break;
        case "ignoreShutdown":
            goto default;
        case "blankNormal":
            Send("");
            goto default;
        case "lateUnknown":
            Send($"{{\"type\":\"progress\",\"id\":{id + 100},\"percent\":1}}");
            goto default;
        case "duplicateTerminal":
            Send($"{{\"type\":\"accepted\",\"id\":{id},\"ok\":true}}");
            Send($"{{\"type\":\"result\",\"id\":{id},\"ok\":true,\"outcomeKind\":\"ok\"}}");
            Send($"{{\"type\":\"result\",\"id\":{id},\"ok\":true,\"outcomeKind\":\"ok\"}}");
            break;
        case "pdfNormal":
            Send($"{{\"type\":\"accepted\",\"id\":{id},\"ok\":true}}");
            Send($"{{\"type\":\"progress\",\"id\":{id},\"percent\":95,\"progressStage\":\"pdf\"}}");
            Send($"{{\"type\":\"result\",\"id\":{id},\"ok\":true,\"outcomeKind\":\"ok\",\"scopeId\":\"scope\",\"summary\":\"ok\"}}");
            break;
        case "postBuildCatchUpNormal":
            Send($"{{\"type\":\"accepted\",\"id\":{id},\"ok\":true}}");
            Send($"{{\"type\":\"progress\",\"id\":{id},\"percent\":99,\"progressStage\":\"postBuildCatchUp\"}}");
            Send($"{{\"type\":\"result\",\"id\":{id},\"ok\":true,\"outcomeKind\":\"ok\",\"scopeId\":\"scope\",\"summary\":\"ok\",\"postBuildCatchUpChecked\":true,\"postBuildCatchUpThresholdChanges\":30000,\"postBuildCatchUpOutcome\":\"SegmentAppended\",\"postBuildCatchUpJournalChangeCount\":30001,\"postBuildCatchUpChangeCountComplete\":true,\"postBuildCatchUpThresholdExceeded\":true}}");
            break;
        case "postBuildCatchUpInvalid":
            Send($"{{\"type\":\"accepted\",\"id\":{id},\"ok\":true}}");
            Send($"{{\"type\":\"result\",\"id\":{id},\"ok\":true,\"outcomeKind\":\"ok\",\"scopeId\":\"scope\",\"postBuildCatchUpChecked\":true,\"postBuildCatchUpOutcome\":\"unexpected\"}}");
            break;
        case "buildNullFields":
            Send($"{{\"type\":\"accepted\",\"id\":{id},\"ok\":true}}");
            Send($"{{\"type\":\"result\",\"id\":{id},\"ok\":true,\"outcomeKind\":\"ok\"}}");
            break;
        case "queryNormal":
            Send($"{{\"type\":\"result\",\"id\":{id},\"ok\":true,\"verdict\":0,\"trigramsBase64\":\"\",\"candidatesBase64\":\"\"}}");
            break;
        case "queryMalformed":
            Send("not json");
            break;
        case "queryUnknown":
            Send($"{{\"type\":\"mystery\",\"id\":{id}}}");
            break;
        case "queryRejectNoError":
            Send($"{{\"type\":\"result\",\"id\":{id},\"ok\":false}}");
            break;
        case "rejectBusy":
            Send($"{{\"type\":\"result\",\"id\":{id},\"ok\":false,\"outcomeKind\":\"busy\",\"error\":\"busy\"}}");
            break;
        default:
            Send($"{{\"type\":\"accepted\",\"id\":{id},\"ok\":true}}");
            Send($"{{\"type\":\"progress\",\"id\":{id},\"percent\":50,\"progressRoot\":\"C:\\\\root\",\"progressStage\":\"rawBuild\"}}");
            Send($"{{\"type\":\"result\",\"id\":{id},\"ok\":true,\"outcomeKind\":\"ok\",\"scopeId\":\"scope\",\"summary\":\"ok\"}}");
            break;
    }
}

// Mapped query-session ops (plan §5.2) — used by the Stage-3 fault-injection tests. Every reply carries the
// fake worker epoch (7) and, for classify, echoes the request's sessionId/batchSeq so the host's reply gate
// can validate them. The scenario name selects the fault to inject.
static void HandleQuery(string op, int id, JsonElement root, string scenario)
{
    const int fakeEpoch = 7;

    if (scenario == "queryReject")
    {
        Send(CamelJson(new { type = "result", id, ok = false, epoch = fakeEpoch, error = "rejected" }));
        return;
    }

    if (op == "openQueryScope")
    {
        if (scenario == "queryOpenMissingResult")
        {
            Send(CamelJson(new { type = "result", id, ok = true, epoch = fakeEpoch }));
            return;
        }
        if (scenario == "queryOpenNullResult")
        {
            Send(CamelJson(new { type = "result", id, ok = true, epoch = fakeEpoch, queryResultJson = "null" }));
            return;
        }

        bool accelerable = scenario != "queryOpenNotReady";
        object? diagnostics = accelerable
            ? new
            {
                layerCount = 3,
                pathRecordCount = 120L,
                tombstoneRecordCount = 5L,
                distinctRouteHashCount = 100,
                candidatesEvaluatedInWorker = true,
                mapOpenMs = 1.25,
                candidateEvaluationMs = 2.5,
                routingIndexMs = 3.75,
                workerOpenMs = 8.0,
            }
            : null;
        string open = CamelJson(new
        {
            accelerable,
            candidateCount = 0,
            bypassReason = accelerable ? null : "fake not-ready",
            diagnostics,
        });
        Send(CamelJson(new { type = "result", id, ok = true, epoch = fakeEpoch, queryResultJson = open }));
        return;
    }

    if (op is "closeQueryScope" or "cancelSession")
    {
        Send(CamelJson(new { type = "result", id, ok = true, epoch = fakeEpoch }));
        return;
    }

    if (op == "reconcileB1")
    {
        HandleReconcile(id, root, scenario);
        return;
    }

    // classifyPaths: read the framing fields back out of the request payload.
    int sessionId = 0;
    long batchSeq = 0;
    int pathCount = 0;
    if (root.TryGetProperty("queryJson", out JsonElement queryJson) && queryJson.GetString() is { Length: > 0 } payload)
    {
        using JsonDocument qd = JsonDocument.Parse(payload);
        JsonElement q = qd.RootElement;
        sessionId = q.TryGetProperty("sessionId", out JsonElement s) ? s.GetInt32() : 0;
        batchSeq = q.TryGetProperty("batchSeq", out JsonElement b) ? b.GetInt64() : 0;
        if (q.TryGetProperty("pathsBase64", out JsonElement p) && p.GetString() is { Length: > 0 } pathsB64)
            pathCount = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(pathsB64)).Split('\n').Length;
    }

    switch (scenario)
    {
        case "classifyMalformedResult":
            Send(CamelJson(new { type = "result", id, ok = true, epoch = fakeEpoch, queryResultJson = "{" }));
            return;
        case "classifyNullResult":
            Send(CamelJson(new { type = "result", id, ok = true, epoch = fakeEpoch, queryResultJson = "null" }));
            return;
        case "classifyCrash":
            Environment.Exit(0);
            return;
        case "classifyMalformed":
            Send("this is not json at all");
            return;
        case "classifyBusy":
            Send(CamelJson(new { type = "result", id, ok = false, epoch = fakeEpoch, error = "busy" }));
            return;
        case "classifyWrongBatch":
            SendClassify(id, fakeEpoch, sessionId, batchSeq + 1, pathCount); // late/stale sequence → host drops
            return;
        case "classifyWrongEpoch":
            SendClassify(id, fakeEpoch + 1, sessionId, batchSeq, pathCount); // restarted-worker epoch → host drops
            return;
        case "classifyDuplicate":
            SendClassify(id, fakeEpoch, sessionId, batchSeq, pathCount);
            SendClassify(id, fakeEpoch, sessionId, batchSeq, pathCount); // second must be dropped, not double-applied
            return;
        case "classifyHang":
            Thread.Sleep(5000); // host abandons via its deadline before this replies
            break;
    }

    SendClassify(id, fakeEpoch, sessionId, batchSeq, pathCount);
}

// Emits a well-formed classifyPaths reply: all-Nonmember verdicts (the max would-prune set for spool tests).
static void SendClassify(int id, int epoch, int sessionId, long batchSeq, int pathCount)
{
    var verdicts = new byte[pathCount];
    for (int i = 0; i < pathCount; i++)
        verdicts[i] = 3; // Verdicts.Nonmember
    string inner = CamelJson(new { sessionId, batchSeq, verdictsBase64 = pathCount == 0 ? "" : Convert.ToBase64String(verdicts) });
    Send(CamelJson(new { type = "result", id, ok = true, epoch, queryResultJson = inner }));
}

// reconcileB1 (plan §5.5) fault injection: a crash / malformed / mis-routed reply must degrade the host to a
// spool replay (ReconcileB1Async → Fail). The happy fake reconcile returns an empty rescue set (the real
// bundled worker is used to prove the actual rescue-set correctness).
static void HandleReconcile(int id, JsonElement root, string scenario)
{
    const int fakeEpoch = 7;
    int sessionId = 0;
    bool certain = true;
    if (root.TryGetProperty("queryJson", out JsonElement queryJson) && queryJson.GetString() is { Length: > 0 } payload)
    {
        using JsonDocument qd = JsonDocument.Parse(payload);
        JsonElement q = qd.RootElement;
        sessionId = q.TryGetProperty("sessionId", out JsonElement s) ? s.GetInt32() : 0;
        certain = !q.TryGetProperty("certain", out JsonElement c) || c.GetBoolean();
    }

    switch (scenario)
    {
        case "reconcileReject":
            Send(CamelJson(new { type = "result", id, ok = false, epoch = fakeEpoch, error = "rejected" }));
            return;
        case "reconcileNullResult":
            Send(CamelJson(new { type = "result", id, ok = true, epoch = fakeEpoch, queryResultJson = "null" }));
            return;
        case "reconcileCrash":
            Environment.Exit(0);
            return;
        case "reconcileMalformed":
            Send("still not json at all");
            return;
        case "reconcileWrongSession":
            SendReconcile(id, fakeEpoch, sessionId + 1, certain); // wrong session → host drops → Fail
            return;
        case "reconcileWrongEpoch":
            SendReconcile(id, fakeEpoch + 1, sessionId, certain); // restarted-worker epoch → host drops → Fail
            return;
    }

    SendReconcile(id, fakeEpoch, sessionId, certain);
}

// Emits a well-formed reconcileB1 reply with no rescue paths, echoing the request's certainty.
static void SendReconcile(int id, int epoch, int sessionId, bool certain)
{
    string inner = CamelJson(new { sessionId, rescuePathsBase64 = "", pruningCertain = certain });
    Send(CamelJson(new { type = "result", id, ok = true, epoch, queryResultJson = inner }));
}

static string CamelJson(object value) => JsonSerializer.Serialize(value, new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
});
