using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Yagu.IndexWorker;

/// <summary>
/// Managed binding to the native index engine exported by <c>yagu_core.dll</c> (the Rust <c>index</c>
/// module). This is the ONLY place in Yagu that P/Invokes the index FFI — it lives in the isolated worker
/// process so a read-time / native access violation is contained here and never faults the main app.
/// <para>
/// The worker's own directory (<c>&lt;app&gt;\index-worker\</c>) does not contain <c>yagu_core.dll</c>; the
/// DLL ships beside <c>Yagu.exe</c> one level up. <see cref="Install"/> registers a resolver that probes the
/// worker directory first and then the parent app directory, so the same self-contained worker binary loads
/// the engine regardless of which folder the DLL is staged into.
/// </para>
/// </summary>
internal static class NativeIndexEngine
{
    private const string Library = "yagu_core";

    [StructLayout(LayoutKind.Sequential)]
    private struct QgTrigramResult
    {
        public int Verdict;
        public IntPtr Trigrams;   // u32*
        public nuint TrigramCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QgCandidateResult
    {
        public IntPtr Candidates; // i32*
        public nuint Count;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint qg_index_abi_version();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int qg_index_extract_trigrams(IntPtr data, nuint len, out QgTrigramResult result);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void qg_index_free_trigrams(ref QgTrigramResult result);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int qg_index_query_content_bin(
        IntPtr contentBin, nuint contentLen,
        IntPtr queryRpn, nuint rpnLen,
        out QgCandidateResult result);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void qg_index_free_candidates(ref QgCandidateResult result);

    private static bool _resolverInstalled;

    /// <summary>Registers the <c>yagu_core.dll</c> probing resolver (idempotent).</summary>
    public static void Install()
    {
        if (_resolverInstalled)
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(typeof(NativeIndexEngine).Assembly, Resolve);
        _resolverInstalled = true;
    }

    private static IntPtr Resolve(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, Library, StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        foreach (string candidate in CandidatePaths())
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out IntPtr handle))
            {
                return handle;
            }
        }

        // Fall back to the default OS search (PATH etc.).
        return NativeLibrary.TryLoad(Library, assembly, searchPath, out IntPtr fallback) ? fallback : IntPtr.Zero;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        yield return Path.Combine(baseDir, "yagu_core.dll");

        string? parent = Path.GetDirectoryName(baseDir);
        if (!string.IsNullOrEmpty(parent))
        {
            yield return Path.Combine(parent, "yagu_core.dll");
        }
    }

    /// <summary>Returns the native index-FFI ABI version, or throws <see cref="DllNotFoundException"/> /
    /// <see cref="EntryPointNotFoundException"/> when the engine cannot be loaded.</summary>
    public static uint AbiVersion() => qg_index_abi_version();

    /// <summary>Classifies <paramref name="data"/> and returns its verdict and sorted-distinct trigram set.</summary>
    public static (int Verdict, uint[] Trigrams) ExtractTrigrams(ReadOnlySpan<byte> data)
    {
        unsafe
        {
            fixed (byte* ptr = data)
            {
                int rc = qg_index_extract_trigrams((IntPtr)ptr, (nuint)data.Length, out QgTrigramResult result);
                if (rc != 0)
                {
                    throw new InvalidOperationException($"qg_index_extract_trigrams failed (rc={rc}).");
                }

                try
                {
                    int count = checked((int)result.TrigramCount);
                    uint[] trigrams = new uint[count];
                    if (count > 0)
                    {
                        var span = new ReadOnlySpan<uint>((void*)result.Trigrams, count);
                        span.CopyTo(trigrams);
                    }

                    return (result.Verdict, trigrams);
                }
                finally
                {
                    qg_index_free_trigrams(ref result);
                }
            }
        }
    }

    /// <summary>Verifies + queries a serialized <c>content.bin</c> with RPN <paramref name="queryRpn"/> and
    /// returns the candidate document-id set. Throws on a bad checksum / malformed content or query.</summary>
    public static int[] QueryContentBin(ReadOnlySpan<byte> contentBin, ReadOnlySpan<byte> queryRpn)
    {
        unsafe
        {
            fixed (byte* binPtr = contentBin)
            fixed (byte* rpnPtr = queryRpn)
            {
                int rc = qg_index_query_content_bin(
                    (IntPtr)binPtr, (nuint)contentBin.Length,
                    (IntPtr)rpnPtr, (nuint)queryRpn.Length,
                    out QgCandidateResult result);
                if (rc != 0)
                {
                    throw new InvalidOperationException($"qg_index_query_content_bin failed (rc={rc}).");
                }

                try
                {
                    int count = checked((int)result.Count);
                    int[] candidates = new int[count];
                    if (count > 0)
                    {
                        var span = new ReadOnlySpan<int>((void*)result.Candidates, count);
                        span.CopyTo(candidates);
                    }

                    return candidates;
                }
                finally
                {
                    qg_index_free_candidates(ref result);
                }
            }
        }
    }
}
