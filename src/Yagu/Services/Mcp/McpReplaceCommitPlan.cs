using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Yagu.Services.Mcp;

/// <summary>One recorded edit: replace <paramref name="Length"/> characters at <paramref name="Offset"/>.</summary>
internal readonly record struct McpReplaceEdit(int Offset, int Length, string Replacement);

/// <summary>Everything needed to re-apply one file's previewed edit, and to prove the file has not moved on.</summary>
internal sealed record McpReplaceFilePlan(
    string Path,
    string EncodingWebName,
    string ContentSha256,
    long Length,
    long LastWriteUtcTicks,
    int ReplacementCount,
    IReadOnlyList<McpReplaceEdit> Edits);

/// <summary>Why a commit refused to touch a file.</summary>
internal enum McpReplaceVerifyResult
{
    Ok,
    Missing,
    ContentChanged,
    TimestampChanged,
    Unreadable,
}

/// <summary>
/// A short-lived, single-use record of exactly what a replacement preview proposed.
/// </summary>
/// <remarks>
/// <para>
/// This exists because <c>confirm: true</c> is not authorization — the same model that decided to call
/// the tool sets the flag — and because re-running the regex at write time is not the same operation the
/// user reviewed. Between preview and commit a file can change: a broad pattern re-applied to new content
/// can hit lines nobody looked at.
/// </para>
/// <para>
/// So the plan stores the <b>exact edits</b> (offset, length, replacement) plus a SHA-256 of the file's
/// bytes. Commit verifies the hash and then splices those offsets into the verified-identical content. A
/// file that changed by even one byte is refused, not rewritten.
/// </para>
/// </remarks>
internal sealed record McpReplaceCommitPlan(
    string Id,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ExpiresUtc,
    string Query,
    string Replacement,
    bool Regex,
    bool CaseSensitive,
    bool WholeWord,
    bool Multiline,
    IReadOnlyList<McpReplaceFilePlan> Files)
{
    /// <summary>How long a preview stays committable. Long enough to review, short enough to go stale.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresUtc;

    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>Where pending plans live. Per user, and outside the app directory so an update cannot clear them.</summary>
    public static string PendingDirectory => Path.Combine(McpTokenStore.DirectoryPath, "pending");

    /// <summary>A plan id is used as a file name, so it must be exactly what we generated.</summary>
    public static bool IsWellFormedId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length == 32 && id.All(Uri.IsHexDigit);

    public static string PathForId(string id) => PathForIdIn(PendingDirectory, id);

    /// <summary>The plan file for <paramref name="id"/> inside <paramref name="pendingDirectory"/>.</summary>
    internal static string PathForIdIn(string pendingDirectory, string id) =>
        Path.Combine(pendingDirectory, id + ".json");

    // ── File state ──

    /// <summary>Hash of the file's raw bytes. Content identity, independent of encoding or line endings.</summary>
    public static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Hash of bytes already in hand, so identity covers exactly the content that was used.</summary>
    public static string HashBytes(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    /// <summary>
    /// Reads the file and confirms it is byte-for-byte what the preview saw, in ONE read.
    /// </summary>
    /// <remarks>
    /// Verifying through one handle and then re-opening the file to apply the edits is a race: a writer
    /// that changes the file in between gets the previewed offsets spliced into content that was never
    /// verified. The caller must edit <paramref name="content"/>, never a fresh read of the path.
    /// </remarks>
    public static McpReplaceVerifyResult ReadVerified(McpReplaceFilePlan file, out byte[] content)
    {
        ArgumentNullException.ThrowIfNull(file);
        content = [];

        try
        {
            using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);

            long length = stream.Length;
            if (ClassifyLength(length, file.Length) is { } lengthFailure)
                return lengthFailure;

            if (File.GetLastWriteTimeUtc(stream.SafeFileHandle).Ticks != file.LastWriteUtcTicks)
                return McpReplaceVerifyResult.TimestampChanged;

            var buffer = new byte[(int)length];
            stream.ReadExactly(buffer);

            if (!string.Equals(HashBytes(buffer), file.ContentSha256, StringComparison.OrdinalIgnoreCase))
                return McpReplaceVerifyResult.ContentChanged;

            content = buffer;
            return McpReplaceVerifyResult.Ok;
        }
        catch (FileNotFoundException)
        {
            return McpReplaceVerifyResult.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return McpReplaceVerifyResult.Missing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return McpReplaceVerifyResult.Unreadable;
        }
    }

    internal static McpReplaceVerifyResult? ClassifyLength(long actual, long expected) =>
        actual != expected
            ? McpReplaceVerifyResult.ContentChanged
            : actual > int.MaxValue
                ? McpReplaceVerifyResult.Unreadable
                : null;

    /// <summary>
    /// Confirms the file is byte-for-byte what the preview saw.
    /// </summary>
    /// <remarks>
    /// The hash is the real check; length and timestamp are cheap pre-filters that also catch the case
    /// where a file was rewritten with identical content but different metadata.
    /// </remarks>
    public static McpReplaceVerifyResult Verify(McpReplaceFilePlan file)
    {
        ArgumentNullException.ThrowIfNull(file);

        try
        {
            var info = new FileInfo(file.Path);
            if (!info.Exists)
                return McpReplaceVerifyResult.Missing;

            if (info.Length != file.Length)
                return McpReplaceVerifyResult.ContentChanged;

            if (info.LastWriteTimeUtc.Ticks != file.LastWriteUtcTicks)
                return McpReplaceVerifyResult.TimestampChanged;

            return string.Equals(HashFile(file.Path), file.ContentSha256, StringComparison.OrdinalIgnoreCase)
                ? McpReplaceVerifyResult.Ok
                : McpReplaceVerifyResult.ContentChanged;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return McpReplaceVerifyResult.Unreadable;
        }
    }

    /// <summary>
    /// Applies the recorded edits to <paramref name="original"/>.
    /// </summary>
    /// <remarks>
    /// Edits are applied back-to-front so each offset still refers to the original content. Throws when an
    /// edit does not fit, which would mean the plan and the content disagree — a condition the caller must
    /// treat as a refusal, never as something to patch up.
    /// </remarks>
    public static string ApplyEdits(string original, IReadOnlyList<McpReplaceEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(edits);

        var text = new StringBuilder(original);

        foreach (McpReplaceEdit edit in edits.OrderByDescending(e => e.Offset))
        {
            if (edit.Offset < 0 || edit.Length < 0 || edit.Offset + edit.Length > text.Length)
                throw new InvalidOperationException($"edit at {edit.Offset} does not fit the file; the plan does not match the content.");

            text.Remove(edit.Offset, edit.Length);
            text.Insert(edit.Offset, edit.Replacement);
        }

        return text.ToString();
    }

    // ── Persistence ──

    /// <summary>Writes the plan and returns its id, or null when it cannot be stored.</summary>
    internal string? Save(string pendingDirectory) => SaveIn(pendingDirectory);

    /// <summary>
    /// Takes ownership of a pending plan, removing it from the store in the same step.
    /// </summary>
    /// <remarks>
    /// The plan file is renamed before it is read, so a second commit racing the first finds nothing.
    /// A plan is single-use: without this, a retry could apply the same edits to a file that has since
    /// been legitimately changed back.
    /// </remarks>
    internal static McpReplaceCommitPlan? Claim(string id, out string? failure, string pendingDirectory) =>
        ClaimIn(pendingDirectory, id, out failure);

    /// <summary>Drops the in-flight marker once the batch has finished, committed or rolled back.</summary>
    internal static void ReleaseClaim(string id, string pendingDirectory) =>
        ReleaseClaimIn(pendingDirectory, id);

    /// <summary>Deletes expired plans so an abandoned preview does not linger as a committable edit.</summary>
    internal static void PurgeExpired(DateTimeOffset now, string pendingDirectory) =>
        PurgeExpiredIn(pendingDirectory, now);

    // The directory is a parameter below so tests can drive the real claim/expiry handling without
    // writing into the live per-user pending store, which a running server may be reading.

    internal string? SaveIn(string pendingDirectory)
    {
        try
        {
            Directory.CreateDirectory(pendingDirectory);
            File.WriteAllText(PathForIdIn(pendingDirectory, Id), ToJson(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return Id;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    internal static McpReplaceCommitPlan? ClaimIn(string pendingDirectory, string id, out string? failure)
    {
        failure = null;

        if (!IsWellFormedId(id))
        {
            failure = "that is not a valid commit id.";
            return null;
        }

        string path = PathForIdIn(pendingDirectory, id);
        string claimed = path + ".inflight";

        try
        {
            // The marker outlives the claim on purpose. Files are replaced one at a time, so a kill,
            // timeout, or power loss mid-batch leaves earlier files written; finding the marker on the
            // next attempt is the only way that partial state is ever reported instead of silent.
            if (File.Exists(claimed))
            {
                failure = "a previous commit of this id was interrupted before it finished, so some files may "
                        + "already have been written. Check the .yagubak backups beside them, then preview again.";
                return null;
            }

            if (!File.Exists(path))
            {
                failure = "no pending replacement was found for that id. It may have expired, or already been committed.";
                return null;
            }

            File.Move(path, claimed, overwrite: false);
            string json = File.ReadAllText(claimed, Encoding.UTF8);

            McpReplaceCommitPlan? plan = FromJson(json);
            if (plan is null)
            {
                failure = "the pending replacement could not be read.";
                ReleaseClaimIn(pendingDirectory, id);
            }

            return plan;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            failure = "the pending replacement could not be claimed; another commit may be running.";
            return null;
        }
    }

    internal static void ReleaseClaimIn(string pendingDirectory, string id)
    {
        try
        {
            string claimed = PathForIdIn(pendingDirectory, id) + ".inflight";
            if (File.Exists(claimed))
                File.Delete(claimed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }

    internal static void PurgeExpiredIn(
        string pendingDirectory,
        DateTimeOffset now,
        Func<string, string, IEnumerable<string>>? enumerateFiles = null)
    {
        enumerateFiles ??= Directory.EnumerateFiles;

        try
        {
            if (!Directory.Exists(pendingDirectory))
                return;

            foreach (string file in enumerateFiles(pendingDirectory, "*.json"))
            {
                try
                {
                    McpReplaceCommitPlan? plan = FromJson(File.ReadAllText(file, Encoding.UTF8));
                    if (plan is null || plan.IsExpired(now))
                        File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    // A plan that cannot be read is also a plan that cannot be committed.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("id", Id);
            writer.WriteString("createdUtc", CreatedUtc);
            writer.WriteString("expiresUtc", ExpiresUtc);
            writer.WriteString("query", Query);
            writer.WriteString("replacement", Replacement);
            writer.WriteBoolean("regex", Regex);
            writer.WriteBoolean("caseSensitive", CaseSensitive);
            writer.WriteBoolean("wholeWord", WholeWord);
            writer.WriteBoolean("multiline", Multiline);

            writer.WriteStartArray("files");
            foreach (McpReplaceFilePlan file in Files)
            {
                writer.WriteStartObject();
                writer.WriteString("path", file.Path);
                writer.WriteString("encoding", file.EncodingWebName);
                writer.WriteString("sha256", file.ContentSha256);
                writer.WriteNumber("length", file.Length);
                writer.WriteNumber("lastWriteUtcTicks", file.LastWriteUtcTicks);
                writer.WriteNumber("replacements", file.ReplacementCount);

                writer.WriteStartArray("edits");
                foreach (McpReplaceEdit edit in file.Edits)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("offset", edit.Offset);
                    writer.WriteNumber("length", edit.Length);
                    writer.WriteString("replacement", edit.Replacement);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static McpReplaceCommitPlan? FromJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            var files = new List<McpReplaceFilePlan>();
            if (root.TryGetProperty("files", out JsonElement fileArray))
            {
                foreach (JsonElement file in fileArray.EnumerateArray())
                {
                    var edits = new List<McpReplaceEdit>();
                    if (file.TryGetProperty("edits", out JsonElement editArray))
                    {
                        foreach (JsonElement edit in editArray.EnumerateArray())
                        {
                            edits.Add(new McpReplaceEdit(
                                edit.GetProperty("offset").GetInt32(),
                                edit.GetProperty("length").GetInt32(),
                                edit.GetProperty("replacement").GetString() ?? string.Empty));
                        }
                    }

                    files.Add(new McpReplaceFilePlan(
                        file.GetProperty("path").GetString() ?? string.Empty,
                        file.GetProperty("encoding").GetString() ?? "utf-8",
                        file.GetProperty("sha256").GetString() ?? string.Empty,
                        file.GetProperty("length").GetInt64(),
                        file.GetProperty("lastWriteUtcTicks").GetInt64(),
                        file.GetProperty("replacements").GetInt32(),
                        edits));
                }
            }

            return new McpReplaceCommitPlan(
                root.GetProperty("id").GetString() ?? string.Empty,
                root.GetProperty("createdUtc").GetDateTimeOffset(),
                root.GetProperty("expiresUtc").GetDateTimeOffset(),
                root.TryGetProperty("query", out var q) ? q.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("replacement", out var r) ? r.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("regex", out var rx) && rx.GetBoolean(),
                root.TryGetProperty("caseSensitive", out var cs) && cs.GetBoolean(),
                root.TryGetProperty("wholeWord", out var ww) && ww.GetBoolean(),
                root.TryGetProperty("multiline", out var ml) && ml.GetBoolean(),
                files);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
