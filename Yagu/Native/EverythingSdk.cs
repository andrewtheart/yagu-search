using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;

namespace Yagu.Native;

/// <summary>
/// P/Invoke wrapper for the Everything SDK (Everything64.dll).
/// Communicates directly with the running Everything service via IPC,
/// avoiding the overhead of spawning es.exe.
///
/// IMPORTANT: The Everything SDK uses global state. All calls must be
/// serialized via <see cref="Lock"/>.
/// </summary>
[ExcludeFromCodeCoverage]
internal static class EverythingSdk
{
    private const string DllName = "Everything64.dll";

    /// <summary>Serialize all SDK access — the native library uses global state.</summary>
    internal static readonly object Lock = new();

    // ── Error codes ──────────────────────────────────────────────
    internal const uint EVERYTHING_OK = 0;
    internal const uint EVERYTHING_ERROR_MEMORY = 1;
    internal const uint EVERYTHING_ERROR_IPC = 2;
    internal const uint EVERYTHING_ERROR_REGISTERCLASSEX = 3;
    internal const uint EVERYTHING_ERROR_CREATEWINDOW = 4;
    internal const uint EVERYTHING_ERROR_CREATETHREAD = 5;
    internal const uint EVERYTHING_ERROR_INVALIDINDEX = 6;
    internal const uint EVERYTHING_ERROR_INVALIDCALL = 7;
    internal const uint EVERYTHING_ERROR_INVALIDREQUEST = 8;
    internal const uint EVERYTHING_ERROR_INVALIDPARAMETER = 9;

    // ── Request flags ────────────────────────────────────────────
    internal const uint EVERYTHING_REQUEST_FILE_NAME = 0x00000001;
    internal const uint EVERYTHING_REQUEST_PATH = 0x00000002;
    internal const uint EVERYTHING_REQUEST_FULL_PATH_AND_FILE_NAME = 0x00000004;
    internal const uint EVERYTHING_REQUEST_SIZE = 0x00000010;

    // ── Sort types ───────────────────────────────────────────────
    internal const uint EVERYTHING_SORT_PATH_ASCENDING = 3;

    // ── Write search state ───────────────────────────────────────
    [DllImport(DllName, EntryPoint = "Everything_SetSearchW", CharSet = CharSet.Unicode)]
    internal static extern void SetSearch(string lpSearchString);

    [DllImport(DllName, EntryPoint = "Everything_SetMatchPath")]
    internal static extern void SetMatchPath(bool bEnable);

    [DllImport(DllName, EntryPoint = "Everything_SetMatchCase")]
    internal static extern void SetMatchCase(bool bEnable);

    [DllImport(DllName, EntryPoint = "Everything_SetMax")]
    internal static extern void SetMax(uint dwMax);

    [DllImport(DllName, EntryPoint = "Everything_SetOffset")]
    internal static extern void SetOffset(uint dwOffset);

    [DllImport(DllName, EntryPoint = "Everything_SetSort")]
    internal static extern void SetSort(uint dwSort);

    [DllImport(DllName, EntryPoint = "Everything_SetRequestFlags")]
    internal static extern void SetRequestFlags(uint dwRequestFlags);

    // ── Execute query ────────────────────────────────────────────
    [DllImport(DllName, EntryPoint = "Everything_QueryW", CharSet = CharSet.Unicode)]
    internal static extern bool Query(bool bWait);

    // ── Read result state ────────────────────────────────────────
    [DllImport(DllName, EntryPoint = "Everything_GetNumResults")]
    internal static extern uint GetNumResults();

    [DllImport(DllName, EntryPoint = "Everything_GetTotResults")]
    internal static extern uint GetTotResults();

    [DllImport(DllName, EntryPoint = "Everything_GetLastError")]
    internal static extern uint GetLastError();

    [DllImport(DllName, EntryPoint = "Everything_IsFileResult")]
    internal static extern bool IsFileResult(uint nIndex);

    [DllImport(DllName, EntryPoint = "Everything_GetResultFullPathNameW", CharSet = CharSet.Unicode)]
    internal static extern uint GetResultFullPathName(uint nIndex, StringBuilder lpString, uint nMaxCount);

    [DllImport(DllName, EntryPoint = "Everything_GetResultSize")]
    internal static extern bool GetResultSize(uint nIndex, out long lpSize);

    // ── Cleanup ──────────────────────────────────────────────────
    [DllImport(DllName, EntryPoint = "Everything_Reset")]
    internal static extern void Reset();

    [DllImport(DllName, EntryPoint = "Everything_CleanUp")]
    internal static extern void CleanUp();

    // ── Utility ──────────────────────────────────────────────────
    [DllImport(DllName, EntryPoint = "Everything_IsDBLoaded")]
    internal static extern bool IsDBLoaded();

    /// <summary>
    /// Returns a human-readable description for an Everything error code.
    /// </summary>
    internal static string ErrorMessage(uint code) => code switch
    {
        EVERYTHING_OK => "OK",
        EVERYTHING_ERROR_MEMORY => "Out of memory",
        EVERYTHING_ERROR_IPC => "Everything is not running",
        EVERYTHING_ERROR_REGISTERCLASSEX => "Unable to register window class",
        EVERYTHING_ERROR_CREATEWINDOW => "Unable to create listening window",
        EVERYTHING_ERROR_CREATETHREAD => "Unable to create listening thread",
        EVERYTHING_ERROR_INVALIDINDEX => "Invalid index",
        EVERYTHING_ERROR_INVALIDCALL => "Invalid call",
        EVERYTHING_ERROR_INVALIDREQUEST => "Invalid request data",
        EVERYTHING_ERROR_INVALIDPARAMETER => "Invalid parameter",
        _ => $"Unknown error ({code})"
    };
}
