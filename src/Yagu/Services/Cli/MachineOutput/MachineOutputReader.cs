using System.Text.Json;

namespace Yagu.Services.Cli.MachineOutput;

/// <summary>One decoded record from the CLI machine protocol.</summary>
internal readonly record struct MachineRecord(
    int SchemaVersion,
    string Type,
    long Sequence,
    string RequestId,
    JsonElement Data)
{
    /// <summary>True when <see cref="Data"/> carries a payload object.</summary>
    public bool HasData => Data.ValueKind == JsonValueKind.Object;

    public string? GetString(string property) =>
        HasData && Data.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Reads an integral field. Returns null when the property is absent, is not a number, or is a
    /// fractional number — <see cref="JsonElement.GetInt64"/> throws on the latter, and a protocol
    /// decoder must never throw on well-formed-but-unexpected input.
    /// </summary>
    public long? GetInt64(string property) =>
        HasData && Data.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out long parsed)
                ? parsed
                : null;

    /// <summary>Reads a possibly fractional numeric field, such as an elapsed-seconds value.</summary>
    public double? GetDouble(string property) =>
        HasData && Data.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out double parsed)
                ? parsed
                : null;

    public bool? GetBoolean(string property) =>
        HasData && Data.TryGetProperty(property, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
}

/// <summary>
/// Decodes the NDJSON produced by <see cref="MachineOutputWriter"/>.
/// </summary>
/// <remarks>
/// The reader is deliberately strict: a line that is not a JSON object, is missing an envelope field,
/// or declares a schema version this build does not understand is rejected rather than partially
/// interpreted. A bridge that guessed at a malformed line would surface fabricated results.
/// </remarks>
internal static class MachineOutputReader
{
    /// <summary>The highest <c>schemaVersion</c> this build can decode.</summary>
    public const int MaxSupportedSchemaVersion = MachineProtocol.SchemaVersion;

    /// <summary>
    /// Parses one NDJSON line. Blank lines yield false with a null <paramref name="error"/>, so a caller
    /// can skip them without treating them as corruption.
    /// </summary>
    public static bool TryParse(string? line, out MachineRecord record, out string? error)
    {
        record = default;
        error = null;

        if (string.IsNullOrWhiteSpace(line))
            return false;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            error = $"malformed protocol line: {ex.Message}";
            return false;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "protocol line is not a JSON object.";
                return false;
            }

            if (!root.TryGetProperty("schemaVersion", out var schemaElement)
                || schemaElement.ValueKind != JsonValueKind.Number)
            {
                error = "protocol line is missing schemaVersion.";
                return false;
            }

            // TryGetInt32, not GetInt32: a fractional or out-of-range number passes the ValueKind guard
            // and then throws, out of a decoder whose whole contract is that it never throws on
            // well-formed-but-unexpected input.
            if (!schemaElement.TryGetInt32(out int schemaVersion))
            {
                error = "protocol line has a non-integer schemaVersion.";
                return false;
            }

            if (schemaVersion <= 0 || schemaVersion > MaxSupportedSchemaVersion)
            {
                error = $"unsupported protocol schemaVersion {schemaVersion}; this build understands up to {MaxSupportedSchemaVersion}.";
                return false;
            }

            if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
            {
                error = "protocol line is missing type.";
                return false;
            }

            if (!root.TryGetProperty("sequence", out var sequenceElement)
                || sequenceElement.ValueKind != JsonValueKind.Number)
            {
                error = "protocol line is missing sequence.";
                return false;
            }

            if (!sequenceElement.TryGetInt64(out long sequence))
            {
                error = "protocol line has a non-integer sequence.";
                return false;
            }

            string requestId = root.TryGetProperty("requestId", out var requestElement)
                && requestElement.ValueKind == JsonValueKind.String
                    ? requestElement.GetString()!
                    : string.Empty;

            // Clone: the JsonDocument is disposed on the way out of this scope.
            JsonElement data = root.TryGetProperty("data", out var dataElement)
                ? dataElement.Clone()
                : default;

            record = new MachineRecord(
                schemaVersion,
                typeElement.GetString()!,
                sequence,
                requestId,
                data);
            return true;
        }
    }
}
