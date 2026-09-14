using System.Diagnostics;
using System.Text;
using Yagu.Services.Cli.MachineOutput;
using Yagu.Services.Index;

namespace Yagu.McpServer;

/// <summary>How a bridged <c>Yagu.exe</c> invocation ended.</summary>
internal enum YaguCliOutcome
{
    /// <summary>The process ran to completion and its protocol stream parsed.</summary>
    Completed,

    /// <summary>The MCP client cancelled the request.</summary>
    Cancelled,

    /// <summary>The server's own time limit elapsed.</summary>
    TimedOut,

    /// <summary>The process could not be started, or died without completing its protocol stream.</summary>
    Crashed,

    /// <summary>Output was produced but did not parse as the machine protocol.</summary>
    ProtocolError,
}

/// <summary>The decoded result of one bridged invocation.</summary>
internal sealed record YaguCliResult(
    YaguCliOutcome Outcome,
    int ExitCode,
    IReadOnlyList<MachineRecord> Records,
    string? Failure,
    string StandardError)
{
    public bool Succeeded => Outcome == YaguCliOutcome.Completed && CliExitCode.IsSuccess(ExitCode);

    /// <summary>The closing summary record, when the run produced one.</summary>
    public MachineRecord? Summary =>
        Records.LastOrDefault(r => r.Type == MachineProtocol.Types.Summary) is { Type: MachineProtocol.Types.Summary } s
            ? s
            : null;
}

/// <summary>
/// Runs <c>Yagu.exe --cli --output json</c> and decodes the machine protocol.
/// </summary>
/// <remarks>
/// <para>
/// Every safety property of the MCP server that concerns child processes lives here:
/// </para>
/// <list type="bullet">
/// <item><description>
/// Arguments are passed through <see cref="ProcessStartInfo.ArgumentList"/> and never concatenated into
/// a command line, so a path containing quotes or spaces cannot alter the argument boundaries.
/// </description></item>
/// <item><description>
/// stdout and stderr are drained <b>concurrently from process start</b>. Reading one to completion
/// while the other fills its pipe buffer deadlocks.
/// </description></item>
/// <item><description>
/// The child and <b>its whole descendant tree</b> (semantic, OCR and index workers) are assigned to a
/// kill-on-close job object. Cancelling a read or abandoning <c>WaitForExitAsync</c> does not terminate
/// a child; without the job object a cancelled request would strand a worker holding VRAM or an index
/// write lock.
/// </description></item>
/// <item><description>
/// Cancellation, timeout, crash and protocol-parse failure are reported as distinct outcomes, because a
/// client can act on them differently.
/// </description></item>
/// </list>
/// </remarks>
internal sealed class YaguCliBridge : IDisposable
{
    /// <summary>
    /// How long the record reader is given after the child exits, and again after a kill.
    /// </summary>
    /// <remarks>
    /// The child's stdout handle is inherited by the workers it spawns, so the pipe does not reach EOF
    /// just because the child exited. Without a bound, one orphaned worker holds the request open for
    /// the life of the server and never releases its concurrency lease.
    /// </remarks>
    private static readonly TimeSpan ReaderDrainGrace = TimeSpan.FromSeconds(5);

    private readonly string _yaguExePath;
    private readonly BridgeConcurrency _concurrency;
    private readonly Func<ProcessStartInfo, Process?> _startProcess;
    private readonly Func<WindowsJobObject> _createJob;

    public YaguCliBridge(
        string yaguExePath,
        int maxConcurrentCalls,
        TimeSpan? modelGateTimeout = null,
        Func<ProcessStartInfo, Process?>? startProcess = null,
        Func<WindowsJobObject>? createJob = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yaguExePath);
        _yaguExePath = yaguExePath;
        _concurrency = new BridgeConcurrency(maxConcurrentCalls, modelGateTimeout: modelGateTimeout);
        _startProcess = startProcess ?? Process.Start;
        _createJob = createJob ?? WindowsJobObject.CreateKillOnClose;
    }

    /// <summary>True when model work is serialized across every Yagu MCP server on this machine.</summary>
    public bool HasMachineWideModelGate => _concurrency.HasMachineWideModelGate;

    public void Dispose() => _concurrency.Dispose();

    /// <summary>
    /// Invokes the CLI in machine mode. <paramref name="arguments"/> must NOT include <c>--cli</c>,
    /// <c>--output</c>, <c>--non-interactive</c> or <c>--request-id</c>; those are supplied here so no
    /// caller can accidentally leave the protocol or the interactivity gate off.
    /// </summary>
    public async Task<YaguCliResult> InvokeAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        BridgeWorkload workload = BridgeWorkload.Light,
        bool cancellableOnceStarted = true)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        using IDisposable lease = await _concurrency.AcquireAsync(workload, cancellationToken).ConfigureAwait(false);

        // Waiting for the lease stays cancellable either way — nothing has run yet, so abandoning there
        // is free. Once the process is started, a destructive call can opt out: cancellation kills the
        // tree, and a writer whose rollback lives in a catch block cannot roll back a killed process.
        return await RunAsync(
                arguments,
                timeout,
                cancellableOnceStarted ? cancellationToken : CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task<YaguCliResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        string requestId = MachineOutputWriterRequestId();

        var psi = new ProcessStartInfo
        {
            FileName = _yaguExePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Closed immediately after start. Under the stdio transport the server's own stdin IS the
            // client's JSON-RPC frame stream, and an unredirected child inherits that handle: one stray
            // Console.ReadLine in the CLI would eat frames and corrupt the session far from the cause.
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            // A deterministic working directory: the CLI resolves a workspace .yagu.json from the CWD,
            // and the server must never inherit whichever directory it happened to be launched in.
            WorkingDirectory = Path.GetDirectoryName(_yaguExePath) ?? Environment.CurrentDirectory,
        };

        psi.ArgumentList.Add("--cli");
        psi.ArgumentList.Add("--output");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("--non-interactive");
        psi.ArgumentList.Add("--settings-source");
        psi.ArgumentList.Add("global");
        psi.ArgumentList.Add("--request-id");
        psi.ArgumentList.Add(requestId);
        foreach (string argument in arguments)
            psi.ArgumentList.Add(argument);

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        WindowsJobObject? job = null;
        Process? process = null;

        try
        {
            try
            {
                process = _startProcess(psi);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return new YaguCliResult(
                    YaguCliOutcome.Crashed, CliExitCode.InternalFailure, [],
                    $"could not start '{_yaguExePath}': {ex.Message}", string.Empty);
            }

            if (process is null)
            {
                return new YaguCliResult(
                    YaguCliOutcome.Crashed, CliExitCode.InternalFailure, [],
                    $"could not start '{_yaguExePath}'.", string.Empty);
            }

            // Assign before any real work: if the server dies now, the whole tree goes with it.
            job = _createJob();
            if (job.IsInvalid || !job.Assign(process.Handle))
            {
                // CreateKillOnClose and Assign report failure by return value rather than throwing, so
                // without this the code could not tell containment from no containment at all.
                Console.Error.WriteLine(
                    "warning: could not assign the Yagu CLI to a job object; "
                    + "an abandoned call falls back to an explicit tree kill.");
                job.Dispose();
                job = null;
            }

            CloseStandardInput(process.StandardInput);

            var records = new List<MachineRecord>();
            var recordsGate = new object();
            string? protocolError = null;

            // Both pipes are drained from here on, concurrently, for the life of the process.
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            Task stdoutTask = ReadRecordsAsync(process.StandardOutput, records, recordsGate, e => protocolError ??= e);

            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);

                try
                {
                    await stdoutTask.WaitAsync(ReaderDrainGrace, linked.Token).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // A descendant inherited the stdout handle and outlived its parent. Killing the tree
                    // closes the write end so the reader can finish; the protocol stream is whatever
                    // arrived before then, and the missing-summary check below catches a short one.
                    KillTree(process);
                    await QuiesceAsync(stdoutTask).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                KillTree(process);

                // The reader must stop touching the list before it is handed to a caller, and before the
                // finally block disposes the StreamReader out from under it.
                await QuiesceAsync(stdoutTask).ConfigureAwait(false);

                // Drain what already arrived so a cancelled call can still explain itself, but never
                // block on a pipe whose writer is gone.
                string partialStderr = await SafeDrainAsync(stderrTask).ConfigureAwait(false);

                bool timedOut = timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
                return new YaguCliResult(
                    timedOut ? YaguCliOutcome.TimedOut : YaguCliOutcome.Cancelled,
                    CliExitCode.Cancelled,
                    Snapshot(records, recordsGate),
                    timedOut ? $"the call exceeded its {timeout.TotalSeconds:N0}s limit." : "the call was cancelled.",
                    partialStderr);
            }

            string standardError = await SafeDrainAsync(stderrTask).ConfigureAwait(false);
            int exitCode = process.ExitCode;
            List<MachineRecord> finalRecords = Snapshot(records, recordsGate);

            if (protocolError is not null)
            {
                return new YaguCliResult(
                    YaguCliOutcome.ProtocolError, exitCode, finalRecords, protocolError, standardError);
            }

            // A run that produced no summary did not finish its protocol stream, whatever it exited with.
            bool sawSummary = finalRecords.Any(r => r.Type == MachineProtocol.Types.Summary);
            if (!sawSummary)
            {
                return new YaguCliResult(
                    YaguCliOutcome.Crashed, exitCode, finalRecords,
                    $"the CLI exited with {exitCode} without completing its protocol stream.", standardError);
            }

            return new YaguCliResult(YaguCliOutcome.Completed, exitCode, finalRecords, null, standardError);
        }
        finally
        {
            job?.Dispose();
            process?.Dispose();
        }
    }

    internal static void CloseStandardInput(TextWriter stdin)
    {
        try { stdin.Close(); }
        catch (IOException) { }
    }

    internal static async Task ReadRecordsAsync(
        StreamReader stdout,
        List<MachineRecord> records,
        object recordsGate,
        Action<string> onProtocolError)
    {
        try
        {
            while (await stdout.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                    continue;

                if (MachineOutputReader.TryParse(line, out var record, out string? error))
                {
                    lock (recordsGate)
                        records.Add(record);
                }
                else if (error is not null)
                {
                    onProtocolError(error);
                }
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException or InvalidOperationException)
        {
            // The pipe was closed under us by a kill or by the caller giving up. Whatever was already
            // parsed still stands, and faulting here would surface as an unobserved task exception.
        }
    }

    /// <summary>Copies the records under the gate, so a reader still running cannot tear the copy.</summary>
    private static List<MachineRecord> Snapshot(List<MachineRecord> records, object recordsGate)
    {
        lock (recordsGate)
            return [.. records];
    }

    /// <summary>
    /// Waits, bounded, for the record reader to stop. Never rethrows: the caller has already decided what
    /// the outcome is, and a stuck reader must not turn that into a hang.
    /// </summary>
    private static async Task QuiesceAsync(Task readerTask)
    {
        try
        {
            await readerTask.WaitAsync(ReaderDrainGrace).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Still running or faulted; Snapshot takes a locked copy rather than trusting the live list.
        }
    }

    private static async Task<string> SafeDrainAsync(Task<string> readTask)
    {
        try
        {
            return await readTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    internal static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already gone, or access denied; the job object is the backstop.
        }
    }

    private static string MachineOutputWriterRequestId() => Guid.NewGuid().ToString("n");
}
