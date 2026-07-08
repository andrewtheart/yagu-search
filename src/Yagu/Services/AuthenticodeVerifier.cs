using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Yagu.Services;

/// <summary>
/// Verifies that a file carries a valid, trusted Authenticode signature (and, optionally, that it was
/// signed by an expected publisher) before it is executed. Yagu downloads the Everything Search
/// installer over the network and then runs it elevated; without this check a compromised mirror, a
/// hijacked DNS entry, or a TLS man-in-the-middle able to present a trusted certificate could deliver
/// a tampered installer that Yagu would run with administrator rights. Requiring a valid signature
/// from the expected publisher closes that software-integrity gap (OWASP A08:2021).
/// </summary>
internal static class AuthenticodeVerifier
{
    // WINTRUST_ACTION_GENERIC_VERIFY_V2 — the standard Authenticode file-verification policy.
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_WHOLECHAIN = 1;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_REVOCATION_CHECK_CHAIN = 0x00000040;
    private const int ERROR_SUCCESS = 0;

    // The host executable's Authenticode signer subject, computed once per process. Null means the
    // host is not validly signed (a local dev/test build) or its subject could not be read — in which
    // case worker signature enforcement is skipped so unsigned dev builds keep working.
    private static readonly Lazy<string?> HostSignerSubject = new(ReadHostSignerSubject);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public nint pcwszFilePath;
        public nint hFile;
        public nint pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public nint pPolicyCallbackData;
        public nint pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public nint pFile;
        public uint dwStateAction;
        public nint hWVTStateData;
        public nint pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public nint pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int WinVerifyTrust(nint hwnd, ref Guid pgActionID, nint pWVTData);

    /// <summary>
    /// Returns true only when <paramref name="filePath"/> has a valid Authenticode signature that
    /// chains to a trusted root, is not revoked, and — when <paramref name="expectedPublisher"/> is
    /// supplied — was signed by a certificate whose subject contains that publisher string. Any
    /// failure (unsigned, tampered, untrusted, revoked, wrong publisher, or an error during
    /// verification) returns false so callers fail safe and refuse to execute the file.
    /// </summary>
    public static bool IsTrustedPublisher(string filePath, string? expectedPublisher, out string failureReason)
    {
        failureReason = string.Empty;

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            failureReason = "file not found";
            return false;
        }

        if (!IsSignatureTrusted(filePath, out failureReason))
            return false;

        if (!string.IsNullOrEmpty(expectedPublisher))
        {
            string? subject = TryGetSignerSubject(filePath);
            if (subject is null)
            {
                failureReason = "signer certificate could not be read";
                return false;
            }

            if (subject.IndexOf(expectedPublisher, StringComparison.OrdinalIgnoreCase) < 0)
            {
                failureReason = $"unexpected publisher '{subject}'";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns true when it is safe to launch <paramref name="workerPath"/> as a child of the current
    /// process. If the host executable is not itself Authenticode-signed — the normal case for local
    /// dev/test builds — enforcement is skipped and the worker is allowed (there is no publisher
    /// identity to bind to). In a signed, shipped build the worker MUST carry a valid Authenticode
    /// signature from the SAME publisher as the host; otherwise it is rejected, so a planted or
    /// tampered worker (delivered via a <c>YAGU_*_WORKER</c> path override or a writable install
    /// directory) cannot run inside the signed app's process tree (OWASP A08:2021 software integrity).
    /// </summary>
    public static bool IsWorkerTrustedForHost(string workerPath, out string failureReason)
    {
        failureReason = string.Empty;

        string? hostSubject = HostSignerSubject.Value;
        if (string.IsNullOrEmpty(hostSubject))
        {
            // Host is unsigned (local dev/test build) — no publisher identity to enforce, allow it.
            return true;
        }

        if (!IsTrustedPublisher(workerPath, null, out failureReason))
            return false;

        string? workerSubject = TryGetSignerSubject(workerPath);
        if (string.IsNullOrEmpty(workerSubject))
        {
            failureReason = "worker signer certificate could not be read";
            return false;
        }

        if (!string.Equals(hostSubject, workerSubject, StringComparison.OrdinalIgnoreCase))
        {
            failureReason = $"worker publisher '{workerSubject}' does not match host publisher '{hostSubject}'";
            return false;
        }

        return true;
    }

    private static string? ReadHostSignerSubject()
    {
        string? hostPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(hostPath) || !IsTrustedPublisher(hostPath, null, out _))
            return null;
        return TryGetSignerSubject(hostPath);
    }

    private static bool IsSignatureTrusted(string filePath, out string failureReason)
    {
        failureReason = string.Empty;
        nint filePathPtr = Marshal.StringToHGlobalUni(filePath);
        nint fileInfoPtr = nint.Zero;
        nint dataPtr = nint.Zero;
        Guid action = GenericVerifyV2;
        try
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = filePathPtr,
                hFile = nint.Zero,
                pgKnownSubject = nint.Zero,
            };
            fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_WHOLECHAIN,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = fileInfoPtr,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_REVOCATION_CHECK_CHAIN,
            };
            dataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
            Marshal.StructureToPtr(data, dataPtr, false);

            int result = WinVerifyTrust(nint.Zero, ref action, dataPtr);

            // Release the state data allocated by the verify call regardless of outcome.
            WINTRUST_DATA closeData = Marshal.PtrToStructure<WINTRUST_DATA>(dataPtr);
            closeData.dwStateAction = WTD_STATEACTION_CLOSE;
            Marshal.StructureToPtr(closeData, dataPtr, false);
            _ = WinVerifyTrust(nint.Zero, ref action, dataPtr);

            if (result != ERROR_SUCCESS)
            {
                failureReason = $"signature not trusted (0x{result:X8})";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            failureReason = $"verification error: {ex.Message}";
            LogService.Instance.Warning("Authenticode", $"WinVerifyTrust failed for {filePath}", ex);
            return false;
        }
        finally
        {
            if (dataPtr != nint.Zero) Marshal.FreeHGlobal(dataPtr);
            if (fileInfoPtr != nint.Zero) Marshal.FreeHGlobal(fileInfoPtr);
            if (filePathPtr != nint.Zero) Marshal.FreeHGlobal(filePathPtr);
        }
    }

    private static string? TryGetSignerSubject(string filePath)
    {
        try
        {
            // CreateFromSignedFile is the only framework API that returns the Authenticode signer of a
            // PE file. SYSLIB0057 deprecates the raw-bytes loading path, but there is no modern
            // replacement for extracting a PE signer, and the actual trust decision has already been
            // made by WinVerifyTrust above — this only reads the subject for the publisher check.
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
#pragma warning restore SYSLIB0057
            return cert.Subject;
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("Authenticode", $"Could not read signer certificate for {filePath}", ex);
            return null;
        }
    }
}
