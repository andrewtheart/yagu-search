using System.Security.AccessControl;
using System.Text;
using Yagu.Services;
using Yagu.Services.Cli.MachineOutput;
using Yagu.Services.Mcp;

namespace Yagu;

/// <summary>
/// <c>--replace-commit &lt;id&gt;</c> — applies the edits a previous <c>--replace --dry-run</c> recorded.
/// </summary>
/// <remarks>
/// <para>
/// This never re-runs the search pattern. It replays the exact spans the preview recorded, and only
/// after proving the file is byte-for-byte what the preview saw. A file that changed in between is
/// <b>refused</b>, because re-applying a broad pattern to new content can rewrite lines nobody reviewed.
/// </para>
/// <para>
/// Writes are off unless the user has enabled them in Settings. That setting is the human authorization
/// step; a flag set by the model that requested the edit is not.
/// </para>
/// </remarks>
internal static partial class CliRunner
{
    private sealed record CommittedFile(string Path, string? BackupPath, string WrittenSha256);

    public static int RunReplaceCommit(string commitId, CliArgs args)
    {
        AppSettings settings = LoadEffectiveSettings(args);

        if (!settings.McpAllowReplaceWrites)
        {
            WriteError("error: replacement writes are disabled. Enable them in Settings > MCP Server before committing.");
            return CompleteMachineRun(CliExitCode.InvalidInput, "replace-writes-disabled",
                "replacement writes are disabled in settings.");
        }

        McpReplaceCommitPlan? plan = McpReplaceCommitPlan.Claim(
            commitId, out string? failure, McpReplaceCommitPlan.PendingDirectory);
        if (plan is null)
        {
            WriteError($"error: {failure}");
            return CompleteMachineRun(CliExitCode.InvalidInput, "commit-not-found", failure ?? "unknown commit id.");
        }

        if (plan.IsExpired(DateTimeOffset.UtcNow))
        {
            McpReplaceCommitPlan.ReleaseClaim(commitId, McpReplaceCommitPlan.PendingDirectory);
            WriteError("error: that preview has expired. Run the preview again.");
            return CompleteMachineRun(CliExitCode.InvalidInput, "commit-expired", "the preview expired before it was committed.");
        }

        var committed = new List<CommittedFile>();
        int changed = 0;

        // PREFLIGHT. Every file is verified before ANY file is written, because refusing a stale file
        // half way through leaves the earlier files rewritten with no way for the caller to tell which
        // half landed. A batch either applies completely or changes nothing.
        var refusals = new List<string>();
        foreach (McpReplaceFilePlan file in plan.Files)
        {
            // Re-checked here rather than trusted from the preview: the plan is replayed by a separate
            // process invocation, so nothing else in this run has proved these paths are still inside the
            // caller's grant. This is the only containment on a path that rewrites files.
            if (s_confinement.IsActive && !s_confinement.Allows(file.Path))
            {
                refusals.Add($"{file.Path}: it resolves outside the allowed folders");
                continue;
            }

            McpReplaceVerifyResult verified = McpReplaceCommitPlan.ReadVerified(file, out _);
            if (verified != McpReplaceVerifyResult.Ok)
                refusals.Add($"{file.Path}: {DescribeVerifyFailure(verified)}");
        }

        if (refusals.Count > 0)
        {
            McpReplaceCommitPlan.ReleaseClaim(commitId, McpReplaceCommitPlan.PendingDirectory);

            foreach (string refusal in refusals)
            {
                WriteError($"  refused {refusal}.");
                s_machine?.WriteWarning("commit-refused", $"{refusal}.");
            }

            WriteError($"\nRefused {refusals.Count:N0} file(s); nothing was written.");
            return CompleteMachineRun(CliExitCode.InvalidInput, "commit-refused",
                $"{refusals.Count} file(s) changed since the preview, so no file was modified. Preview again.");
        }

        foreach (McpReplaceFilePlan file in plan.Files)
        {
            try
            {
                ApplyOneCommittedFile(file, committed);
                changed++;
                WriteError($"  {file.Path}: {file.ReplacementCount} replacement(s)");
                s_machine?.WriteResult(w =>
                {
                    w.WriteString("path", file.Path);
                    w.WriteNumber("replacements", file.ReplacementCount);
                    w.WriteBoolean("committed", true);
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or DecoderFallbackException)
            {
                WriteError($"  error: {file.Path}: {ex.Message}");
                s_machine?.WriteWarning("commit-failed", $"{file.Path}: {ex.Message}");

                // One failure invalidates the whole edit: a half-applied rename across a codebase is
                // worse than none, and the caller cannot tell which half landed.
                RollBackOutcome rollback = RollBack(committed);
                McpReplaceCommitPlan.ReleaseClaim(commitId, McpReplaceCommitPlan.PendingDirectory);

                // Report what is ACTUALLY on disk. Rollback restores a file only while it still holds
                // what this batch wrote, and the restore itself can fail, so claiming "every file was
                // rolled back" sends the caller away from files that are still modified.
                string rollbackDetail = rollback.Unresolved.Count == 0
                    ? $"All {rollback.RolledBack:N0} file(s) already committed in this batch were rolled back."
                    : $"{rollback.RolledBack:N0} file(s) were rolled back, but {rollback.Unresolved.Count:N0} "
                      + "could not be and are still modified: "
                      + string.Join("; ", rollback.Unresolved) + ".";

                WriteError($"\n{rollbackDetail}");
                return CompleteMachineRun(CliExitCode.PartialFailure, "commit-rolled-back",
                    $"{file.Path}: {ex.Message}. {rollbackDetail}");
            }
        }

        McpReplaceCommitPlan.ReleaseClaim(commitId, McpReplaceCommitPlan.PendingDirectory);
        WriteError($"\nCommitted {changed:N0} file(s).");

        return CompleteMachineRun(CliExitCode.Success);
    }

    private static string DescribeVerifyFailure(McpReplaceVerifyResult verified) => verified switch
    {
        McpReplaceVerifyResult.Missing => "the file no longer exists",
        McpReplaceVerifyResult.ContentChanged => "the file changed since the preview",
        McpReplaceVerifyResult.TimestampChanged => "the file was written since the preview",
        _ => "the file could not be read",
    };

    /// <summary>
    /// Backs the file up, splices in the recorded edits, and replaces it atomically in its own directory.
    /// </summary>
    /// <remarks>
    /// The temp file is written beside the target so the final move is a same-volume rename: a crash
    /// leaves either the old file or the new one, never a truncated one. The original encoding is
    /// preserved, so committing must not silently convert a UTF-16 file to UTF-8.
    /// </remarks>
    private static void ApplyOneCommittedFile(McpReplaceFilePlan file, List<CommittedFile> committed)
    {
        // Re-verified here, in the same read that produces the bytes being edited. The preflight pass
        // proved the batch was committable; this proves THIS file is still the one that was previewed at
        // the moment its content is taken, and a late change rolls the whole batch back.
        McpReplaceVerifyResult verified = McpReplaceCommitPlan.ReadVerified(file, out byte[] rawBytes);
        if (verified != McpReplaceVerifyResult.Ok)
            throw new InvalidOperationException(DescribeVerifyFailure(verified) + ".");

        Encoding encoding = ResolveCommitEncoding(file.EncodingWebName);

        string original;
        using (var buffer = new MemoryStream(rawBytes, writable: false))
        using (var reader = new StreamReader(buffer, encoding, detectEncodingFromByteOrderMarks: true))
        {
            original = reader.ReadToEnd();
            encoding = reader.CurrentEncoding;
        }

        // The plan's hash proves the bytes on disk are unchanged; it proves nothing about decode-then-
        // encode being lossless. Both fallbacks here are replacing, not throwing, so a single byte that
        // does not decode would be rewritten as U+FFFD ANYWHERE in the file, not just in an edited span,
        // and the commit would report success.
        if (!CommitReencodesExactly(rawBytes, original, encoding))
        {
            throw new InvalidOperationException(
                "its bytes do not survive a decode/encode round-trip in " + encoding.WebName
                + ", so committing would rewrite content outside the edited spans");
        }

        string updated = McpReplaceCommitPlan.ApplyEdits(original, file.Edits);

        EnsureRoomToWrite(file.Path, updated, encoding);

        string backup = file.Path + ".yagubak";
        if (File.Exists(backup))
        {
            int suffix = 2;
            while (File.Exists($"{file.Path}.yagubak-{suffix}")) suffix++;
            backup = $"{file.Path}.yagubak-{suffix}";
        }
        File.Copy(file.Path, backup, overwrite: false);
        MirrorFileSecurity(file.Path, backup);

        // Unique and created exclusively. A fixed "<target>.yagu-commit-tmp" is a name an attacker can
        // predict: an ordinary file there would be silently overwritten and then deleted by the cleanup
        // below, and a link there would carry this write to whatever it points at, outside the grant the
        // preflight pass just enforced. CreateNew fails rather than following either.
        string temp = $"{file.Path}.{Guid.NewGuid():n}.yagu-commit-tmp";
        string writtenSha256;
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, encoding))
            {
                writer.Write(updated);
            }

            // What this batch put on disk, so a rollback can tell its own write from someone else's.
            writtenSha256 = McpReplaceCommitPlan.HashFile(temp);

            // Re-checked immediately before the swap. Everything since the verified read — the free-space
            // probe, the backup copy, and this temp write — is time a competing writer can use, and
            // File.Replace is unconditional.
            if (McpReplaceCommitPlan.ReadVerified(file, out _) != McpReplaceVerifyResult.Ok)
                throw new InvalidOperationException("the file changed while the commit was being written.");

            File.Replace(temp, file.Path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
            try { if (File.Exists(backup)) File.Delete(backup); } catch (IOException) { }
            throw;
        }

        committed.Add(new CommittedFile(file.Path, backup, writtenSha256));
    }

    /// <summary>What a rollback actually achieved, so the run can report the final disk state.</summary>
    /// <param name="Unresolved">
    /// One entry per file left MODIFIED, naming where its pre-commit copy is. These are the only paths
    /// the user still has to act on.
    /// </param>
    private sealed record RollBackOutcome(int RolledBack, IReadOnlyList<string> Unresolved);

    /// <summary>
    /// Restores every file already written in this batch from its backup, unless someone else has
    /// written to it since.
    /// </summary>
    /// <remarks>
    /// A rollback undoes THIS batch, and it can only do that while the file still holds what this batch
    /// wrote. Between an early file being committed and a later one failing, an editor or build can save
    /// over it; restoring unconditionally would discard that save and then delete the only copy of it.
    /// So a changed destination is left alone and reported, with its pre-commit copy kept.
    /// </remarks>
    private static RollBackOutcome RollBack(List<CommittedFile> committed)
    {
        int rolledBack = 0;
        var unresolved = new List<string>();

        for (int i = committed.Count - 1; i >= 0; i--)
        {
            CommittedFile entry = committed[i];
            if (entry.BackupPath is null || !File.Exists(entry.BackupPath))
            {
                // Written, but with no copy to restore from: still a modified file the caller must know
                // about, and silently skipping it is what made the old report claim a clean rollback.
                unresolved.Add($"{entry.Path} (no backup available)");
                continue;
            }

            try
            {
                if (!string.Equals(McpReplaceCommitPlan.HashFile(entry.Path), entry.WrittenSha256, StringComparison.OrdinalIgnoreCase))
                {
                    WriteError($"  error: not rolling back {entry.Path}; it changed since this batch wrote it. "
                             + $"Its pre-commit copy is at {entry.BackupPath}");
                    s_machine?.WriteWarning("rollback-skipped",
                        $"{entry.Path}: changed since it was committed, so it was left as it is; pre-commit copy kept at {entry.BackupPath}");
                    unresolved.Add($"{entry.Path} (changed since it was written; pre-commit copy at {entry.BackupPath})");
                    continue;
                }

                File.Copy(entry.BackupPath, entry.Path, overwrite: true);
                File.Delete(entry.BackupPath);
                WriteError($"  rolled back {entry.Path}");
                rolledBack++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Say so loudly: the backup is the user's only copy at this point, so it is kept.
                WriteError($"  error: could not roll back {entry.Path}; its backup is at {entry.BackupPath}");
                s_machine?.WriteWarning("rollback-failed", $"{entry.Path}: backup kept at {entry.BackupPath}");
                unresolved.Add($"{entry.Path} (restore failed; pre-commit copy at {entry.BackupPath})");
            }
        }

        return new RollBackOutcome(rolledBack, unresolved);
    }

    /// <summary>
    /// Reads a file's bytes and its write time as one snapshot, for a plan that must hash exactly the
    /// content its offsets were computed from.
    /// </summary>
    private static byte[] ReadFileSnapshot(string path, out long lastWriteUtcTicks)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);

        long length = stream.Length;
        if (length > int.MaxValue)
            throw new IOException($"the file is too large to replace in one pass ({length:N0} bytes).");

        lastWriteUtcTicks = File.GetLastWriteTimeUtc(stream.SafeFileHandle).Ticks;

        var buffer = new byte[(int)length];
        stream.ReadExactly(buffer);
        return buffer;
    }

    /// <summary>Refuses before writing when the volume cannot hold the result plus its backup.</summary>
    private static void EnsureRoomToWrite(string path, string content, Encoding encoding)    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
                return;

            long required = encoding.GetByteCount(content) + new FileInfo(path).Length;
            var drive = new DriveInfo(root);
            if (drive.IsReady && drive.AvailableFreeSpace < required)
                throw new IOException($"not enough free space on {root} for the new content and its backup.");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            // An unusual path shape is not a reason to block the write; the write itself will report.
        }
    }

    private static Encoding ResolveCommitEncoding(string webName)
    {
        try
        {
            Encoding encoding = Encoding.GetEncoding(webName);
            return encoding is UTF8Encoding
                ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false)
                : encoding;
        }
        catch (ArgumentException)
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        }
    }

    /// <summary>
    /// Whether re-encoding <paramref name="decoded"/> reproduces <paramref name="rawBytes"/> exactly.
    /// </summary>
    /// <remarks>
    /// Modelled on what the commit actually writes: <see cref="File.WriteAllText(string, string, Encoding)"/>
    /// emits the preamble followed by the encoded text, so this also catches a BOM being added to or
    /// dropped from a file that was not asking for one.
    /// </remarks>
    private static bool CommitReencodesExactly(byte[] rawBytes, string decoded, Encoding encoding)
    {
        byte[] preamble = encoding.GetPreamble();

        long expectedLength = (long)preamble.Length + encoding.GetByteCount(decoded);
        if (expectedLength != rawBytes.Length)
            return false;

        byte[] expected = new byte[rawBytes.Length];
        preamble.CopyTo(expected, 0);
        encoding.GetBytes(decoded, expected.AsSpan(preamble.Length));
        return expected.AsSpan().SequenceEqual(rawBytes);
    }

    /// <summary>
    /// Gives the backup the original's effective permissions.
    /// </summary>
    /// <remarks>
    /// <see cref="File.Copy(string, string, bool)"/> creates a new file that inherits the containing
    /// directory's DACL, so a locked-down file would otherwise get a sibling readable by everyone the
    /// directory grants. Inherited rules are converted to explicit ones and further inheritance is
    /// blocked, so the backup ends up with exactly the original's rules and no more.
    /// </remarks>
    private static void MirrorFileSecurity(string source, string destination)
    {
        try
        {
            FileSecurity security = new FileInfo(source).GetAccessControl(AccessControlSections.Access);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
            new FileInfo(destination).SetAccessControl(security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
            or PrivilegeNotHeldException or PlatformNotSupportedException or ArgumentException)
        {
            // A backup whose ACL could not be tightened is still better than no backup at all.
        }
    }
}
