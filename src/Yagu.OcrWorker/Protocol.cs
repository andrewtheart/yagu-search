using System.Text.Json.Serialization;

namespace Yagu.OcrWorker;

/// <summary>
/// A single OCR request sent from Yagu to the worker (one JSON object per stdin line).
/// </summary>
internal sealed class OcrRequest
{
    /// <summary>Correlation id chosen by the caller; echoed back on the matching result.</summary>
    public int Id { get; set; }

    /// <summary>Absolute path to the image file to recognize.</summary>
    public string? Path { get; set; }
}

/// <summary>
/// A single message sent from the worker to Yagu (one JSON object per stdout line).
/// <para>
/// <c>type</c> is one of:
/// <list type="bullet">
/// <item><c>ready</c>  — worker finished provisioning and is ready to accept requests.</item>
/// <item><c>error</c>  — fatal initialization error (carries <c>stage</c> + <c>message</c>).</item>
/// <item><c>result</c> — recognition result for request <c>id</c> (carries <c>ok</c> + <c>text</c>/<c>error</c>).</item>
/// </list>
/// </para>
/// </summary>
internal sealed class OcrEnvelope
{
    public string? Type { get; set; }

    // error
    public string? Stage { get; set; }
    public string? Message { get; set; }

    // result
    public int Id { get; set; }
    public bool Ok { get; set; }
    public string? Text { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Source-generated JSON context (keeps serialization trim-friendly and fast).
/// </summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(OcrRequest))]
[JsonSerializable(typeof(OcrEnvelope))]
internal sealed partial class OcrJsonContext : JsonSerializerContext
{
}
