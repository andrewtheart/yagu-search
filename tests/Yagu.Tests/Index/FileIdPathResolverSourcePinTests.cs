using System.IO;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Source-pins the P/Invoke path resolver (plan §3.5). <see cref="Yagu.Services.Index.FileIdPathResolver"/>
/// is native interop (<c>OpenFileById</c> + <c>GetFinalPathNameByHandle</c>) that can't get runtime coverage
/// in the headless test host, so its correctness-critical constants and the FILE_ID_DESCRIPTOR layout are
/// pinned here. A wrong V2/V3 file-id type, a mis-aligned FILE_ID_DESCRIPTOR union, or a dropped null/prefix
/// check would silently resolve the wrong file (or nothing), corrupting an incremental refresh.
/// </summary>
public sealed class FileIdPathResolverSourcePinTests
{
    private static string Source() =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "Services", "Index", "FileIdPathResolver.cs"));

    [Fact]
    public void Resolver_UsesJournalCompatibleV2OrExtendedV3Id_AndFinalPathApis()
    {
        string src = Source();

        // ReFS unprivileged journal reads emit V2 64-bit reference numbers; true V3 IDs use the extended arm.
        Assert.Contains("FileIdType = 0", src);
        Assert.Contains("ExtendedFileIdType = 2", src);
        Assert.Contains("Type = identity.High == 0 ? FileIdType : ExtendedFileIdType", src);

        // Resolution is OpenFileById (against a per-volume hint handle) + GetFinalPathNameByHandle.
        Assert.Contains("OpenFileById(", src);
        Assert.Contains("GetFinalPathNameByHandleW(", src);
    }

    [Fact]
    public void FileIdDescriptor_LayoutMatchesWin32()
    {
        string src = Source();

        // dwSize + FILE_ID_TYPE + the FILE_ID_128 union (two ulongs). The ulong forces the union to the
        // Win32 8-byte-aligned offset 8, so Marshal.SizeOf == 24 and OpenFileById reads the right bytes.
        Assert.Contains("[StructLayout(LayoutKind.Sequential)]", src);
        Assert.Contains("public uint dwSize;", src);
        Assert.Contains("public int Type;", src);
        Assert.Contains("public ulong FileIdLow;", src);
        Assert.Contains("public ulong FileIdHigh;", src);
        Assert.Contains("dwSize = (uint)Marshal.SizeOf<FILE_ID_DESCRIPTOR>()", src);

        // Identity fields flow straight from the durable UsnFileIdentity (FILE_ID_128 halves).
        Assert.Contains("FileIdLow = identity.Low", src);
        Assert.Contains("FileIdHigh = identity.High", src);
    }

    [Fact]
    public void Resolver_OpensNonBlocking_And_FailsSafe()
    {
        string src = Source();

        // Non-blocking, non-elevated hint handle: read-attributes + backup semantics + full share.
        Assert.Contains("FILE_READ_ATTRIBUTES", src);
        Assert.Contains("FILE_FLAG_BACKUP_SEMANTICS", src);
        Assert.Contains("FileShare.ReadWrite | FileShare.Delete", src);

        // Fail-safe: a bad root, non-Windows, disposed instance, or invalid handle returns null (→ deletion /
        // full-rebuild fallback), never throws to the caller.
        Assert.Contains("!OperatingSystem.IsWindows()", src);
        Assert.Contains("if (hint.IsInvalid)", src);
        Assert.Contains("if (handle.IsInvalid)", src);
        Assert.Contains("return null;", src);
    }

    [Fact]
    public void GetFinalPath_TwoCallSizing_And_StripsExtendedPrefix()
    {
        string src = Source();

        // Two-call sizing pattern (first call sizes, second fills) + the '\\?\' prefix strip for a plain path.
        Assert.Contains("getFinalPathName(handle, null, 0,", src);
        Assert.Contains("new char[needed + 1]", src);
        Assert.Contains(@"ExtendedLengthPrefix = @""\\?\""", src);
        Assert.Contains("path[ExtendedLengthPrefix.Length..]", src);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
