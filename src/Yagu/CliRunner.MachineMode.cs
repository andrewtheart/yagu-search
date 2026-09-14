using System.Text;
using Yagu.Helpers;
using Yagu.Models;
using Yagu.Services;
using Yagu.Services.Ai;
using Yagu.Services.Cli;
using Yagu.Services.Cli.MachineOutput;
using Yagu.Services.Index;
using Yagu.Services.Mcp;

namespace Yagu;

/// <summary>
/// Machine mode (<c>--output json</c>) and the flags that make a <c>--cli</c> run deterministic for a
/// non-interactive caller: <c>--settings-source</c>, <c>--non-interactive</c>, <c>--request-id</c>, and
/// the bounded <c>--read</c> command.
/// </summary>
/// <remarks>
/// Machine mode is configured once, before any command dispatches, so every command — not just search —
/// shares one versioned protocol and one exit-code map. Grep mode is untouched by everything here.
/// </remarks>
internal static partial class CliRunner
{
    private static MachineOutputMode s_outputMode = MachineOutputMode.Grep;
    private static MachineOutputWriter? s_machine;
    private static MachineOutputBudget s_budget = MachineOutputBudget.Default;
    private static CliSettingsSource s_settingsSource = CliSettingsSource.Auto;
    private static bool s_nonInteractive;

    /// <summary>True once a replacement preview record was dropped by the response ceiling.</summary>
    private static bool s_replacePreviewTruncated;

    /// <summary>Outcome of a file this run wrote itself, for the closing summary.</summary>
    private sealed record MachineWriteOutcome(bool Written, string Path, string? Failure);

    /// <summary>Result of <c>--export</c>, if this run exported.</summary>
    private static MachineWriteOutcome? s_exportOutcome;

    /// <summary>Result of <c>--save-session</c>, if this run saved one.</summary>
    private static MachineWriteOutcome? s_sessionOutcome;

    /// <summary>Roots this run may write or rewrite results under. Inactive unless --confine-to is given.</summary>
    private static PathConfinement s_confinement = PathConfinement.None;

    /// <summary>True when stdout carries the NDJSON protocol rather than human text.</summary>
    private static bool MachineMode => s_outputMode == MachineOutputMode.Json;

    /// <summary>
    /// Emits a match unless <c>--confine-to</c> excludes it. Returns false only when the budget is spent,
    /// so a filtered-out match does not look like the end of the page.
    /// </summary>
    /// <remarks>
    /// A confined result is dropped silently rather than reported: naming the path in a warning would
    /// disclose the very location the confinement exists to keep out of the response.
    /// </remarks>
    private static bool WriteConfinedMatch(SearchResult result)
    {
        if (s_confinement.IsActive && !s_confinement.Allows(result.FilePath))
            return !s_machine!.NoteBudgetExhausted();

        return s_machine!.WriteMatch(result);
    }

    /// <summary>
    /// True when the run must never block on console input. Machine mode always implies this: a
    /// <c>Console.ReadLine</c> would both corrupt the protocol stream and deadlock the bridge, which
    /// holds stdin open but never answers.
    /// </summary>
    private static bool NonInteractive => s_nonInteractive || MachineMode;

    /// <summary>
    /// Validates the machine-protocol flags and, in machine mode, opens the NDJSON writer over raw
    /// stdout. Returns false with <paramref name="errorCode"/> when a flag value is unusable — the run
    /// fails as invalid-input rather than half-emitting output in the wrong shape.
    /// </summary>
    private static bool TryConfigureMachineMode(CliArgs args, out int errorCode)
    {
        errorCode = CliExitCode.InvalidInput;

        s_outputMode = MachineOutputMode.Grep;
        s_machine = null;
        s_budget = MachineOutputBudget.Default;
        s_settingsSource = CliSettingsSource.Auto;
        s_nonInteractive = args.NonInteractive;
        s_replacePreviewTruncated = false;
        s_exportOutcome = null;
        s_sessionOutcome = null;
        s_confinement = args.ConfineToRoots.Count == 0
            ? PathConfinement.None
            : new PathConfinement(args.ConfineToRoots);

        if (!string.IsNullOrWhiteSpace(args.OutputMode))
        {
            if (!MachineOutputModeParser.TryParse(args.OutputMode, out var mode))
            {
                // The output shape itself is what failed to parse, so there is no protocol the caller
                // agreed to fail into. stderr is the only honest channel here.
                WriteError($"error: unrecognised --output '{args.OutputMode}'. Expected 'grep' or 'json'.");
                return false;
            }
            s_outputMode = mode;
        }

        if (!string.IsNullOrWhiteSpace(args.SettingsSource))
        {
            if (!CliSettingsSource.TryParse(args.SettingsSource, out var source, out string? sourceError))
                return FailConfiguration("invalid-settings-source", sourceError!, args);

            s_settingsSource = source;
        }

        // The writer opens BEFORE the remaining validation. Rejecting a flag after this point used to
        // exit 2 with completely empty stdout, which a client cannot tell apart from a crashed process.
        if (MachineMode)
        {
            s_budget = ResolveMachineOutputBudget(args);
            s_machine = new MachineOutputWriter(
                new BufferedStream(Console.OpenStandardOutput(), 1 << 16),
                args.RequestId ?? MachineOutputWriter.NewRequestId(),
                s_budget);

            EmitSettingsSourceDiagnostic(args);
        }

        // Supplied-but-blank is invalid input, not "unset": every other bad value here errors out, and
        // silently reading the whole file instead of the requested range is a surprising fallback.
        if (args.ReadLines is not null && !CliLineRange.TryParse(args.ReadLines, out _, out string? rangeError))
            return FailConfiguration("invalid-line-range", rangeError!, args);

        errorCode = CliExitCode.Success;
        return true;
    }

    /// <summary>
    /// Reports a rejected flag through the protocol the caller ASKED for, opening the writer first if
    /// validation failed before it existed. Always returns false so callers can <c>return</c> it.
    /// </summary>
    private static bool FailConfiguration(string code, string message, CliArgs args)
    {
        WriteError($"error: {message}");

        if (!MachineMode)
            return false;

        s_machine ??= new MachineOutputWriter(
            new BufferedStream(Console.OpenStandardOutput(), 1 << 16),
            args.RequestId ?? MachineOutputWriter.NewRequestId(),
            s_budget);

        CompleteMachineRun(CliExitCode.InvalidInput, code, message);
        return false;
    }

    /// <summary>
    /// Reads the response ceilings the user configured on the MCP Server settings tab. They are
    /// persisted rather than hard-coded, so a caller can bound a response to what its client can
    /// actually handle. An unreadable settings file falls back to the shipped defaults — a run must not
    /// fail because a ceiling could not be read.
    /// </summary>
    private static MachineOutputBudget ResolveMachineOutputBudget(CliArgs args)
    {
        try
        {
            return ResolveSettingsService(args).Load().EffectiveMachineOutputBudget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
        {
            return MachineOutputBudget.Default;
        }
    }

    /// <summary>
    /// Reports which settings file the run actually resolved and from which working directory, so a
    /// caller can tell a workspace <c>.yagu.json</c> apart from the global one it expected.
    /// </summary>
    private static void EmitSettingsSourceDiagnostic(CliArgs args)
    {
        string? resolved = null;
        try { resolved = ResolveSettingsService(args).SettingsPath; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }

        s_machine?.WriteDiagnostic(w =>
        {
            w.WriteString("settingsSourceKind", s_settingsSource.Kind.ToString().ToLowerInvariant());
            w.WriteString("settingsPath", resolved ?? string.Empty);
            w.WriteString("workingDirectory", SafeCurrentDirectory());
            w.WriteBoolean("nonInteractive", NonInteractive);
        });
    }

    private static string SafeCurrentDirectory()
    {
        try { return Directory.GetCurrentDirectory(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return string.Empty; }
    }

    /// <summary>
    /// Finishes a machine-mode run: emits the closing <c>summary</c> record (and an <c>error</c> record
    /// first when the outcome is a real failure) and flushes. Returns <paramref name="exitCode"/> so
    /// call sites can <c>return CompleteMachineRun(code)</c>.
    /// </summary>
    private static int CompleteMachineRun(int exitCode, string? errorCode = null, string? errorMessage = null)
    {
        if (s_machine is null)
            return exitCode;

        if (errorCode is not null)
            s_machine.WriteError(errorCode, errorMessage ?? CliExitCode.Describe(exitCode), exitCode);

        s_machine.WriteSummary(exitCode);
        s_machine.Flush();
        return exitCode;
    }

    // -----------------------------------------------------------------------
    // Content-index management
    // -----------------------------------------------------------------------

    /// <summary>
    /// Runs an index-management command under the machine protocol.
    /// </summary>
    /// <remarks>
    /// The index commands print a human transcript with <c>Console.Out.WriteLine</c> and return values
    /// from <see cref="ContentIndexExitCode"/>, a numbering that deliberately differs from
    /// <see cref="CliExitCode"/> (3 = unsupported scope, 4 = cancelled, 6 = already running). Those
    /// codes are a published contract, so they are preserved verbatim as the process exit code while the
    /// machine summary additionally reports the mapped protocol outcome. Machine mode owns stdout, so
    /// the transcript is redirected to stderr and the structured records are emitted here.
    /// </remarks>
    private static int RunIndexManagementMachine(CliArgs args)
    {
        if (s_machine is null)
            return RunIndexManagement(args);

        if (args.IndexStatusRequested)
            return WriteMachineIndexStatus(args);

        if (args.IndexListRoots)
            return WriteMachineIndexRoots(args);

        var humanOut = Console.Out;
        int indexExitCode;
        try
        {
            Console.SetOut(Console.Error);
            indexExitCode = RunIndexManagement(args);
        }
        finally
        {
            Console.SetOut(humanOut);
        }

        int exitCode = MapIndexExitCode(indexExitCode);
        s_machine.WriteResult(w =>
        {
            w.WriteString("operation", DescribeIndexOperation(args));
            w.WriteString("root", args.IndexBuildPath ?? args.IndexRebuildPath ?? args.IndexDeletePath
                ?? args.IndexCompactPath ?? args.IndexValidatePath ?? args.IndexAddRootPath
                ?? args.IndexRemoveRootPath ?? string.Empty);
            w.WriteNumber("indexExitCode", indexExitCode);
        });

        s_machine.WriteSummary(exitCode, w => w.WriteNumber("indexExitCode", indexExitCode));
        s_machine.Flush();
        return indexExitCode;
    }

    /// <summary>Maps an index command's <see cref="ContentIndexExitCode"/> onto the protocol outcome.</summary>
    private static int MapIndexExitCode(int indexExitCode) => (ContentIndexExitCode)indexExitCode switch
    {
        ContentIndexExitCode.Success          => CliExitCode.Success,
        ContentIndexExitCode.InvalidArguments  => CliExitCode.InvalidInput,
        ContentIndexExitCode.UnsupportedScope  => CliExitCode.InvalidInput,
        ContentIndexExitCode.Cancelled         => CliExitCode.Cancelled,
        ContentIndexExitCode.AlreadyRunning    => CliExitCode.Busy,
        ContentIndexExitCode.BuildFailure      => CliExitCode.InternalFailure,
        _                                      => CliExitCode.InternalFailure,
    };

    private static string DescribeIndexOperation(CliArgs args)
    {
        if (args.IndexBuildRequested) return "build";
        if (args.IndexRebuildRequested) return "rebuild";
        if (args.IndexCompactRequested) return "compact";
        if (args.IndexValidateRequested) return "validate";
        if (args.IndexDeletePath is not null) return "delete";
        if (args.ClearIndexes) return "clear";
        if (args.IndexConfigRequested) return "config";
        if (args.IndexAddRootPath is not null) return "add-root";
        if (args.IndexRemoveRootPath is not null) return "remove-root";
        if (args.IndexSetRootFilterPath is not null) return "set-root-filter";
        if (args.IndexClearRootFilterPath is not null) return "clear-root-filter";
        if (args.IndexSetRootSizePath is not null) return "set-root-size";
        if (args.IndexClearRootSizePath is not null) return "clear-root-size";
        return "unknown";
    }

    /// <summary>Emits <c>--index-status</c> as a structured record rather than a human transcript.</summary>
    private static int WriteMachineIndexStatus(CliArgs args)
    {
        var settings = ResolveSettingsService(args).Load();
        string root = ResolveIndexRoot(args.IndexStatusPath);
        var pathProvider = DefaultContentIndexPathProvider.Create(settings.IndexStorageDirectory);
        var manager = new ContentIndexManager(
            pathProvider, AppSettings.NormalizeIndexRetainedGenerationCount(settings.IndexRetainedGenerationCount));

        string? coveringRoot = IndexedRootsPolicy.FindBestCoveringRoot(settings.IndexedRoots, root);
        bool coveredByBroaderRoot = coveringRoot is not null
            && !string.Equals(coveringRoot, IndexScopeIdentity.NormalizePath(root), StringComparison.OrdinalIgnoreCase);

        IndexMetadataStatus status = manager.GetMetadataStatusForRoot(root);

        s_machine!.WriteResult(w =>
        {
            w.WriteString("root", root);
            w.WriteString("scope", ContentIndexManager.ScopeIdForRoot(root));
            w.WriteBoolean("exists", status.Exists);
            w.WriteBoolean("metadataReadable", status.MetadataReadable);
            w.WriteString("health", status.Health.ToString());
            w.WriteString("healthLabel", ContentIndexUiStatus.StorageHealthLabel(status.Health));
            w.WriteBoolean("coveredByBroaderRoot", coveredByBroaderRoot);
            if (coveredByBroaderRoot)
                w.WriteString("coveringRoot", coveringRoot!);
            if (status.Problem is { } problem)
                w.WriteString("problem", problem);
            if (status.Exists && status.MetadataReadable)
            {
                w.WriteNumber("documentCount", status.DocumentCount);
                w.WriteNumber("segmentCount", status.SegmentCount);
                if (status.CreatedUtc is { } created)
                    w.WriteString("createdUtc", created.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                if (status.BuiltUtc is { } built)
                    w.WriteString("builtUtc", built.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                if (status.LastIncrementalUpdateUtc is { } updated)
                    w.WriteString("lastIncrementalUpdateUtc", updated.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            }
        });

        s_machine.WriteSummary(CliExitCode.Success, w => w.WriteNumber("indexExitCode", (int)ContentIndexExitCode.Success));
        s_machine.Flush();
        return (int)ContentIndexExitCode.Success;
    }

    /// <summary>Emits <c>--index-list-roots</c> as one structured record per registered root.</summary>
    private static int WriteMachineIndexRoots(CliArgs args)
    {
        var settings = ResolveSettingsService(args).Load();

        foreach (string indexedRoot in settings.IndexedRoots)
        {
            IndexedRootFilter? filter = IndexedRootFilterPolicy.Find(settings.IndexedRootFilters, indexedRoot);
            EffectiveIndexSizePolicy size = IndexSizeManagementPolicy.Resolve(settings, indexedRoot);

            if (!s_machine!.WriteResult(w =>
            {
                w.WriteString("root", indexedRoot);
                w.WriteBoolean("exists", Directory.Exists(indexedRoot));
                w.WriteString("includeGlobs", filter?.IncludeGlobs ?? string.Empty);
                w.WriteString("excludeGlobs", filter?.ExcludeGlobs ?? string.Empty);
                w.WriteString("sizeMode", size.Mode.ToString());
                w.WriteNumber("sizeBudgetMB", size.SizeBudgetMB);
                w.WriteNumber("autoCompactionCapMB", size.MaxAutoCompactionSizeMB);
            }))
            {
                break;
            }
        }

        s_machine!.WriteSummary(CliExitCode.Success, w =>
        {
            w.WriteNumber("indexExitCode", (int)ContentIndexExitCode.Success);
            w.WriteNumber("registeredRoots", settings.IndexedRoots.Count);
        });
        s_machine.Flush();
        return (int)ContentIndexExitCode.Success;
    }

    // -----------------------------------------------------------------------
    // Semantic plan record
    // -----------------------------------------------------------------------

    /// <summary>
    /// Emits the resolved natural-language plan as a <c>plan</c> record. No-op outside machine mode, so
    /// the human <c>--explain</c> transcript stays exactly as it was.
    /// </summary>
    private static void WriteMachineSemanticPlan(CliArgs args, ResolvedSearchPlan resolved, string? modelAlias)
    {
        if (s_machine is null)
            return;

        s_machine.WritePlan(w =>
        {
            w.WriteString("model", modelAlias ?? string.Empty);
            w.WriteString("summary", SemanticPlanApplier.BuildExplanation(resolved));
            w.WriteString("directory", args.Directory ?? string.Empty);
            w.WriteString("pattern", args.Pattern ?? string.Empty);

            if (resolved.SearchMode is { } searchMode)  w.WriteString("searchMode", searchMode.ToString());
            if (resolved.UseRegex is { } useRegex)      w.WriteBoolean("useRegex", useRegex);
            if (resolved.CaseSensitive is { } caseSensitive) w.WriteBoolean("caseSensitive", caseSensitive);
            if (resolved.ExactMatch is { } exactMatch)  w.WriteBoolean("exactMatch", exactMatch);
            if (resolved.Multiline is { } multiline)    w.WriteBoolean("multiline", multiline);
            if (resolved.MultilineDotAll is { } dotAll) w.WriteBoolean("multilineDotAll", dotAll);
            if (resolved.MinFileSizeBytes is { } minSize) w.WriteNumber("minFileSizeBytes", minSize);
            if (resolved.MaxFileSizeBytes is { } maxSize) w.WriteNumber("maxFileSizeBytes", maxSize);
            if (resolved.MaxSearchDepth is { } depth)   w.WriteNumber("maxSearchDepth", depth);
            if (resolved.ObeyGitignore is { } gitignore) w.WriteBoolean("obeyGitignore", gitignore);
            if (resolved.SearchHiddenFiles is { } hidden) w.WriteBoolean("searchHiddenFiles", hidden);
            if (resolved.SearchInsideArchives is { } archives) w.WriteBoolean("searchInsideArchives", archives);
            if (resolved.SearchImageText is { } imageText) w.WriteBoolean("searchImageText", imageText);
            if (resolved.SearchPdfText is { } pdfText)  w.WriteBoolean("searchPdfText", pdfText);

            WriteStringArray(w, "includeGlobs", resolved.IncludeGlobs);
            WriteStringArray(w, "excludeGlobs", resolved.ExcludeGlobs);
            WriteDate(w, "createdAfter", resolved.CreatedAfterDate);
            WriteDate(w, "createdBefore", resolved.CreatedBeforeDate);
            WriteDate(w, "modifiedAfter", resolved.ModifiedAfterDate);
            WriteDate(w, "modifiedBefore", resolved.ModifiedBeforeDate);
            WriteStringArray(w, "warnings", resolved.Warnings);
        });
    }

    private static void WriteStringArray(System.Text.Json.Utf8JsonWriter w, string name, IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
            return;

        w.WriteStartArray(name);
        foreach (var value in values)
            w.WriteStringValue(value);
        w.WriteEndArray();
    }

    private static void WriteDate(System.Text.Json.Utf8JsonWriter w, string name, DateTimeOffset? value)
    {
        if (value is DateTimeOffset date)
            w.WriteString(name, date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
    }

    // -----------------------------------------------------------------------
    // --read <file> --lines <first>-<last>
    // -----------------------------------------------------------------------
    /// <summary>
    /// Emits a bounded, 1-based inclusive line window from a single file. This is a real CLI command
    /// rather than a bridge-side file read so the read path inherits encoding detection, the machine
    /// envelope, the response budget and (later) the single authorization policy instead of growing a
    /// second implementation of each.
    /// </summary>
    private static int RunRead(CliArgs args)
    {
        string path = args.ReadPath ?? string.Empty;

        // An omitted --lines means the whole file; a supplied one must parse. The old `is not whitespace`
        // guard let `--lines "   "` fail the parse AND skip the error, silently reading everything.
        if (!CliLineRange.TryParse(args.ReadLines ?? "1-", out var range, out string? rangeError)
            && args.ReadLines is not null)
        {
            WriteError($"error: {rangeError}");
            return CompleteMachineRun(CliExitCode.InvalidInput, "invalid-line-range", rangeError);
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            WriteError($"error: invalid --read path: {path}");
            return CompleteMachineRun(CliExitCode.InvalidInput, "invalid-path", $"invalid --read path: {path}");
        }

        // --confine-to bounds every path this run emits, and a read is the most direct emission there is;
        // without this the flag bounded search results while --read handed back any file on the machine.
        // Checked BEFORE File.Exists so a refused path cannot be used to probe for files outside the roots.
        if (s_confinement.IsActive && !s_confinement.Allows(fullPath))
        {
            WriteError($"error: {fullPath} resolves outside the allowed folders.");
            return CompleteMachineRun(CliExitCode.InvalidInput, "path-not-allowed",
                $"{fullPath} resolves outside the allowed folders");
        }

        if (!File.Exists(fullPath))
        {
            WriteError($"error: file not found: {fullPath}");
            return CompleteMachineRun(CliExitCode.InvalidInput, "file-not-found", $"file not found: {fullPath}");
        }

        try
        {
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
            var encoding = EncodingDetector.DetectEncoding(stream);

            // The BOM-less fallback throws on invalid bytes. Downgrade it to the replacement fallback, as
            // every other reader in the app does: without this, one stray byte anywhere in the file failed
            // the whole read with an internal error and zero lines, instead of rendering that byte as
            // U+FFFD and returning the window the caller asked for.
            if (encoding is UTF8Encoding)
                encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

            using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);

            int lineNumber = 0;
            int emitted = 0;
            bool truncated = false;
            string? line;

            while ((line = reader.ReadLine()) is not null)
            {
                lineNumber++;
                if (lineNumber < range.First)
                    continue;
                if (lineNumber > range.Last)
                {
                    truncated = true;
                    break;
                }

                if (!EmitReadLine(fullPath, lineNumber, line))
                {
                    truncated = true;
                    break;
                }

                emitted++;
            }

            if (s_machine is not null)
            {
                int exitCode = emitted > 0 ? CliExitCode.Success : CliExitCode.NoResults;
                s_machine.WriteSummary(exitCode, w =>
                {
                    w.WriteString("path", fullPath);
                    w.WriteString("encoding", encoding.WebName);
                    w.WriteNumber("firstLine", range.First);
                    w.WriteNumber("lastLine", emitted > 0 ? range.First + emitted - 1 : range.First);
                    w.WriteBoolean("reachedEndOfFile", !truncated);
                });
                s_machine.Flush();
                return exitCode;
            }

            Console.Out.Flush();
            return emitted > 0 ? CliExitCode.Success : CliExitCode.NoResults;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            WriteError($"error: failed to read {fullPath}: {ex.Message}");
            return CompleteMachineRun(CliExitCode.InternalFailure, "read-failed", ex.Message);
        }
    }

    /// <summary>Writes one <c>--read</c> line. Returns false when the response budget stopped it.</summary>
    private static bool EmitReadLine(string path, int lineNumber, string text)
    {
        if (s_machine is null)
        {
            Console.Out.WriteLine(text);
            return true;
        }

        return s_machine.WriteResult(w =>
        {
            w.WriteString("path", MachineOutputBudget.TruncateUtf8(path, s_budget.MaxPathBytes, out _, out _));
            w.WriteNumber("line", lineNumber);
            string bounded = MachineOutputBudget.TruncateUtf8(
                text, s_budget.MaxLineBytes, out bool lineTruncated, out int originalBytes);
            w.WriteString("text", bounded);
            if (lineTruncated)
            {
                w.WriteStartObject("truncated");
                w.WriteNumber("text", originalBytes);
                w.WriteEndObject();
            }
        });
    }
}
