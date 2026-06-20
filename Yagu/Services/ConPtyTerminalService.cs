using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Yagu.Services;

/// <summary>
/// Manages the embedded command-shell session used by the terminal pane.
/// </summary>
internal sealed class ConPtyTerminalService : IDisposable
{
    private Process? _process;
    private StreamWriter? _input;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private CancellationTokenSource? _cts;
    private bool _loggedFirstOutput;
    private bool _loggedFirstInput;
    private bool _disposed;
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public int ProcessId { get; private set; }

    public event Action<string>? OutputReceived;

    public event Action<int>? ProcessExited;

    public void Start(int cols = 120, int rows = 30, string? workingDirectory = null)
    {
        if (_process is not null) return;

        string shellPath = ResolveCommandShellExecutable();
        string resolvedWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? AppContext.BaseDirectory : workingDirectory;
        var startInfo = new ProcessStartInfo
        {
            FileName = shellPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = resolvedWorkingDirectory,
        };
        // Render the cmd.exe prompt with one extra space after the '>' (default is "$P$G").
        startInfo.EnvironmentVariables["PROMPT"] = "$P$G ";
        startInfo.ArgumentList.Add("/Q");
        startInfo.ArgumentList.Add("/K");

        LogService.Instance.Info("Terminal", $"Launching redirected shell '{shellPath}' with cwd='{resolvedWorkingDirectory}'");

        try
        {
            _cts = new CancellationTokenSource();
            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.Exited += OnProcessExited;

            if (!_process.Start())
                throw new InvalidOperationException($"CreateProcess failed for '{shellPath}'.");

            ProcessId = _process.Id;
            _input = _process.StandardInput;
            _input.AutoFlush = true;
            _stdoutTask = Task.Run(() => ReadRedirectedOutput(_process.StandardOutput, _cts.Token));
            _stderrTask = Task.Run(() => ReadRedirectedOutput(_process.StandardError, _cts.Token));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Dispose();
            throw;
        }
    }

    public void Resize(int cols, int rows)
    {
        if (_process is null)
            return;
    }

    public void WriteInput(string text)
    {
        WriteInput(text, echoInput: true);
    }

    public void WriteInput(string text, bool echoInput)
    {
        if (_input is null)
        {
            LogService.Instance.Warning("Terminal", "Ignored terminal input because the shell input stream is not ready.");
            return;
        }

        try
        {
            if (!_loggedFirstInput && text.Length > 0)
            {
                _loggedFirstInput = true;
                LogService.Instance.Info("Terminal", $"First terminal input written: chars={text.Length}");
            }

            string echo = echoInput ? BuildLocalEcho(text) : string.Empty;
            if (echoInput && echo.Length > 0)
                OutputReceived?.Invoke(echo);

            // The redirected cmd.exe reads its stdin pipe line-by-line and only
            // executes a command once it sees a newline. xterm sends a lone '\r'
            // for Enter, which never terminates the line, so normalize every line
            // ending to '\r\n' before writing to the shell.
            string shellText = NormalizeShellLineEndings(text);
            _input.Write(shellText);
            _input.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            LogService.Instance.Warning("Terminal", "Failed to write terminal input", ex);
        }
    }

    public void CancelCurrentCommand()
    {
        if (_process is null)
            return;

        try
        {
            if (_process.HasExited)
                return;
        }
        catch
        {
            return;
        }

        bool killedDescendant = TryKillDescendantProcesses(_process.Id);
        if (killedDescendant)
            return;

        try
        {
            _input?.Write("\u0003\r");
            _input?.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            LogService.Instance.Warning("Terminal", "Failed to send terminal cancellation input", ex);
        }
    }

    private static bool TryKillDescendantProcesses(int rootProcessId)
    {
        var descendants = GetDescendantProcessIds(rootProcessId);
        if (descendants.Count == 0)
            return false;

        bool attemptedKill = false;
        foreach (int processId in descendants.OrderByDescending(id => id))
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                if (process.HasExited)
                    continue;

                attemptedKill = true;
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
                LogService.Instance.Verbose("Terminal", $"Terminal cancellation skipped process {processId}", ex);
            }
        }

        return attemptedKill;
    }

    private static List<int> GetDescendantProcessIds(int rootProcessId)
    {
        var childrenByParent = new Dictionary<int, List<int>>();
        foreach ((int ProcessId, int ParentProcessId) process in EnumerateProcessParents())
        {
            if (!childrenByParent.TryGetValue(process.ParentProcessId, out List<int>? children))
            {
                children = [];
                childrenByParent[process.ParentProcessId] = children;
            }

            children.Add(process.ProcessId);
        }

        var descendants = new List<int>();
        var stack = new Stack<int>();
        if (childrenByParent.TryGetValue(rootProcessId, out List<int>? directChildren))
        {
            foreach (int child in directChildren)
                stack.Push(child);
        }

        while (stack.Count > 0)
        {
            int processId = stack.Pop();
            descendants.Add(processId);
            if (!childrenByParent.TryGetValue(processId, out List<int>? children))
                continue;

            foreach (int child in children)
                stack.Push(child);
        }

        return descendants;
    }

    private static IEnumerable<(int ProcessId, int ParentProcessId)> EnumerateProcessParents()
    {
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == InvalidHandleValue)
            yield break;

        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry))
                yield break;

            do
            {
                yield return ((int)entry.th32ProcessID, (int)entry.th32ParentProcessID);
            }
            while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private void ReadRedirectedOutput(StreamReader reader, CancellationToken ct)
    {
        char[] buffer = new char[4096];

        while (!ct.IsCancellationRequested)
        {
            int charsRead;
            try
            {
                charsRead = reader.Read(buffer, 0, buffer.Length);
            }
            catch
            {
                break;
            }

            if (charsRead <= 0)
                break;

            string output = new(buffer, 0, charsRead);
            if (!_loggedFirstOutput)
            {
                _loggedFirstOutput = true;
                LogService.Instance.Info("Terminal", $"First shell output received: chars={charsRead}");
            }

            OutputReceived?.Invoke(output);
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        int exitCode = 0;
        try
        {
            if (_process is not null)
                exitCode = _process.ExitCode;
        }
        catch
        {
        }

        ProcessExited?.Invoke(exitCode);
    }

    private static string NormalizeShellLineEndings(string text)
    {
        if (text.IndexOf('\r') < 0 && text.IndexOf('\n') < 0)
            return text;

        return text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
    }

    private static string BuildLocalEcho(string text)
    {
        var echo = new StringBuilder(text.Length + 8);

        foreach (char c in text)
        {
            switch (c)
            {
                case '\r':
                    echo.Append("\r\n");
                    break;
                case '\n':
                    break;
                case '\b':
                case '\u007f':
                    echo.Append("\b \b");
                    break;
                case '\t':
                    break;
                case '\u0003':
                case '\u001b':
                    break;
                default:
                    if (!char.IsControl(c))
                        echo.Append(c);
                    break;
            }
        }

        return echo.ToString();
    }

    private static string ResolveCommandShellExecutable()
    {
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string[] candidates =
        [
            Path.Combine(system, "cmd.exe"),
            FindExecutableOnPath("cmd.exe"),
        ];

        foreach (string candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return candidate;
        }

        return "cmd.exe";
    }

    private static string FindExecutableOnPath(string executableName)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.Combine(directory, executableName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
            }
        }

        return string.Empty;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();

        try
        {
            if (_process is { HasExited: false })
            {
                // Forcibly terminate any command currently running under the shell
                // (e.g. a long-running build, ping -t, or a hung process) so that a
                // terminal reset or app shutdown does not leave it running. The
                // graceful "exit" below is swallowed by a foreground child, so the
                // shell would otherwise keep the command alive until the timeout.
                TryKillDescendantProcesses(_process.Id);

                _input?.Write("exit\r");
                _input?.Flush();
                if (!_process.WaitForExit(500))
                    _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }

        _input?.Dispose();
        _process?.Dispose();
        _cts?.Dispose();
    }
}
