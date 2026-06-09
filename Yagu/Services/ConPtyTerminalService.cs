using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Yagu.Services;

/// <summary>
/// Manages a ConPTY pseudo-console session connected to PowerShell.
/// Streams output via <see cref="OutputReceived"/> and accepts input via <see cref="WriteInput"/>.
/// </summary>
internal sealed class ConPtyTerminalService : IDisposable
{
    private nint _pseudoConsoleHandle;
    private SafeFileHandle? _pipeIn;       // We write TO this → PTY stdin
    private SafeFileHandle? _pipeOut;      // We read FROM this ← PTY stdout
    private SafeProcessHandle? _processHandle;
    private SafeFileHandle? _threadHandle;
    private nint _startupInfoEx;
    private Stream? _writer;
    private Task? _readerTask;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <summary>Fired when the PTY produces output bytes (UTF-8).</summary>
    public event Action<string>? OutputReceived;

    /// <summary>Fired when the child process exits.</summary>
    public event Action<int>? ProcessExited;

    public void Start(int cols = 120, int rows = 30, string? workingDirectory = null)
    {
        if (_pseudoConsoleHandle != 0) return;

        SafeFileHandle? inputReadSide = null;
        SafeFileHandle? outputWriteSide = null;

        try
        {
            // Create pipes: inputReadSide→PTY stdin, PTY stdout→outputWriteSide
            CreatePipe(out inputReadSide, out var inputWriteSide);
            CreatePipe(out var outputReadSide, out outputWriteSide);

            _pipeIn = inputWriteSide;
            _pipeOut = outputReadSide;

            // Create pseudo console
            var size = new COORD { X = (short)cols, Y = (short)rows };
            int hr = CreatePseudoConsole(size, inputReadSide.DangerousGetHandle(), outputWriteSide.DangerousGetHandle(), 0, out _pseudoConsoleHandle);
            if (hr != 0) throw new InvalidOperationException($"CreatePseudoConsole failed: 0x{hr:X8}");

            // Close the pipe ends that the PTY now owns
            inputReadSide.Dispose();
            inputReadSide = null;
            outputWriteSide.Dispose();
            outputWriteSide = null;

            // Spawn PowerShell attached to the pseudo console
            string shellPath = ResolvePowerShellExecutable();
            SpawnProcess(shellPath, $"\"{shellPath}\" -NoLogo -NoExit", workingDirectory);

            // Open writer stream
            _writer = new FileStream(_pipeIn, FileAccess.Write, bufferSize: 256, isAsync: false);

            // Start reader loop
            _cts = new CancellationTokenSource();
            _readerTask = Task.Run(() => ReadLoop(_cts.Token));
        }
        catch
        {
            inputReadSide?.Dispose();
            outputWriteSide?.Dispose();
            Dispose();
            throw;
        }
    }

    public void Resize(int cols, int rows)
    {
        if (_pseudoConsoleHandle == 0) return;
        var size = new COORD { X = (short)cols, Y = (short)rows };
        ResizePseudoConsole(_pseudoConsoleHandle, size);
    }

    public void WriteInput(string text)
    {
        if (_writer is null) return;
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        _writer.Write(bytes);
        _writer.Flush();
    }

    private void ReadLoop(CancellationToken ct)
    {
        using var reader = new FileStream(_pipeOut!, FileAccess.Read, bufferSize: 4096, isAsync: false);
        byte[] buffer = new byte[4096];
        var decoder = Encoding.UTF8.GetDecoder();
        char[] charBuffer = new char[4096];

        while (!ct.IsCancellationRequested)
        {
            int bytesRead;
            try { bytesRead = reader.Read(buffer, 0, buffer.Length); }
            catch { break; }

            if (bytesRead == 0) break;

            int charCount = decoder.GetChars(buffer, 0, bytesRead, charBuffer, 0);
            if (charCount > 0)
            {
                string output = new string(charBuffer, 0, charCount);
                OutputReceived?.Invoke(output);
            }
        }

        // Child exited
        int exitCode = 0;
        if (_processHandle is { IsInvalid: false, IsClosed: false })
        {
            WaitForSingleObject(_processHandle.DangerousGetHandle(), 5000);
            GetExitCodeProcess(_processHandle.DangerousGetHandle(), out exitCode);
        }
        ProcessExited?.Invoke(exitCode);
    }

    private static string ResolvePowerShellExecutable()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string[] candidates =
        [
            Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe"),
            Path.Combine(programFilesX86, "PowerShell", "7", "pwsh.exe"),
            FindExecutableOnPath("pwsh.exe"),
            Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"),
            FindExecutableOnPath("powershell.exe"),
        ];

        foreach (string candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return candidate;
        }

        return "powershell.exe";
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

    private void SpawnProcess(string applicationName, string commandLine, string? workingDirectory)
    {
        var startupInfo = new STARTUPINFOEX();
        startupInfo.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();

        // Initialize proc thread attribute list for pseudo console
        nint attrListSize = 0;
        InitializeProcThreadAttributeList(nint.Zero, 1, 0, ref attrListSize);
        startupInfo.lpAttributeList = Marshal.AllocHGlobal(attrListSize);
        _startupInfoEx = startupInfo.lpAttributeList;

        if (!InitializeProcThreadAttributeList(startupInfo.lpAttributeList, 1, 0, ref attrListSize))
            throw new InvalidOperationException("InitializeProcThreadAttributeList failed");

        if (!UpdateProcThreadAttribute(startupInfo.lpAttributeList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                _pseudoConsoleHandle, nint.Size, nint.Zero, nint.Zero))
            throw new InvalidOperationException("UpdateProcThreadAttribute failed");

        var processInfo = new PROCESS_INFORMATION();
        bool success = CreateProcessW(
            applicationName, commandLine, nint.Zero, nint.Zero, false,
            EXTENDED_STARTUPINFO_PRESENT, nint.Zero, workingDirectory, ref startupInfo, out processInfo);

        if (!success)
            throw new InvalidOperationException($"CreateProcessW failed: {Marshal.GetLastWin32Error()}");

        _processHandle = new SafeProcessHandle(processInfo.hProcess, ownsHandle: true);
        _threadHandle = new SafeFileHandle(processInfo.hThread, ownsHandle: true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();
        _writer?.Dispose();

        if (_pseudoConsoleHandle != 0)
        {
            ClosePseudoConsole(_pseudoConsoleHandle);
            _pseudoConsoleHandle = 0;
        }

        _processHandle?.Dispose();
        _threadHandle?.Dispose();
        _pipeIn?.Dispose();
        _pipeOut?.Dispose();

        if (_startupInfoEx != 0)
        {
            DeleteProcThreadAttributeList(_startupInfoEx);
            Marshal.FreeHGlobal(_startupInfoEx);
            _startupInfoEx = 0;
        }

        _cts?.Dispose();
    }

    // ── P/Invoke ──────────────────────────────────────────────────

    private static void CreatePipe(out SafeFileHandle readSide, out SafeFileHandle writeSide)
    {
        var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = true };
        if (!CreatePipe(out readSide, out writeSide, ref sa, 0))
            throw new InvalidOperationException($"CreatePipe failed: {Marshal.GetLastWin32Error()}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public nint lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public nint lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
        public int dwX, dwY, dwXSize, dwYSize;
        public int dwXCountChars, dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public nint lpReserved2;
        public nint hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    private const int EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private static readonly nint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = (nint)0x00020016;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, nint hInput, nint hOutput, uint dwFlags, out nint phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(nint hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(nint hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(nint lpAttributeList, int dwAttributeCount, int dwFlags, ref nint lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(nint lpAttributeList, uint dwFlags, nint attribute, nint lpValue, nint cbSize, nint lpPreviousValue, nint lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(nint lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string? lpApplicationName, string lpCommandLine, nint lpProcessAttributes, nint lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles, int dwCreationFlags, nint lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(nint hProcess, out int lpExitCode);
}
