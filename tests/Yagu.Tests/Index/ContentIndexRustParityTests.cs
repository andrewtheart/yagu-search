using System.Runtime.InteropServices;
using System.Text;
using Yagu.Models;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Golden byte-parity tests between the Rust <c>index</c> module (via the additive
/// <c>qg_index_extract_trigrams</c> FFI in <c>yagu_core.dll</c>) and the managed reference
/// <see cref="ContentRepresentation"/> (plan §3.1/§3.2, §9). For a corpus of raw byte inputs the Rust
/// verdict and sorted trigram set must match the managed reference exactly — the worker's index must be
/// bit-identical to the reference it is validated against.
///
/// The DLL is loaded explicitly from the app's build output (it is not copied into the test bin), so
/// these tests <b>self-gate</b>: when the native DLL hasn't been built (e.g. a fresh CI checkout) they
/// skip instead of failing, matching the repo's other native-DLL tests.
/// </summary>
public sealed class ContentIndexRustParityTests
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ExtractDelegate(IntPtr data, nuint len, out QgTrigramResult result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeDelegate(ref QgTrigramResult result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint AbiDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EvaluateDelegate(
        IntPtr docTrigrams, nuint docTrigramsLen,
        IntPtr docOffsets, nuint docOffsetsLen,
        IntPtr queryRpn, nuint queryRpnLen,
        out QgCandidateResult result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeCandidatesDelegate(ref QgCandidateResult result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int QueryContentBinDelegate(
        IntPtr contentBin, nuint contentBinLen,
        IntPtr queryRpn, nuint queryRpnLen,
        out QgCandidateResult result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int QueryPostingsV3Delegate(
        IntPtr v3Bytes, nuint v3BytesLen,
        IntPtr queryRpn, nuint queryRpnLen,
        out QgCandidateResult result);

    [StructLayout(LayoutKind.Sequential)]
    private struct QgTrigramResult
    {
        public int Verdict;
        public IntPtr Trigrams;
        public nuint TrigramCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QgCandidateResult
    {
        public IntPtr Candidates;
        public nuint Count;
    }

    private sealed class NativeFns
    {
        public required ExtractDelegate Extract { get; init; }
        public required FreeDelegate Free { get; init; }
        public required AbiDelegate Abi { get; init; }
        public required EvaluateDelegate Evaluate { get; init; }
        public required FreeCandidatesDelegate FreeCandidates { get; init; }
        public required QueryContentBinDelegate QueryContentBin { get; init; }
        public required QueryPostingsV3Delegate QueryPostingsV3 { get; init; }
    }
    private static readonly NativeFns? Native = TryLoadNative();

    private static NativeFns? TryLoadNative()
    {
        try
        {
            string? dll = FindNativeDll();
            if (dll is null || !NativeLibrary.TryLoad(dll, out IntPtr handle))
                return null;
            return new NativeFns
            {
                Extract = Marshal.GetDelegateForFunctionPointer<ExtractDelegate>(NativeLibrary.GetExport(handle, "qg_index_extract_trigrams")),
                Free = Marshal.GetDelegateForFunctionPointer<FreeDelegate>(NativeLibrary.GetExport(handle, "qg_index_free_trigrams")),
                Abi = Marshal.GetDelegateForFunctionPointer<AbiDelegate>(NativeLibrary.GetExport(handle, "qg_index_abi_version")),
                Evaluate = Marshal.GetDelegateForFunctionPointer<EvaluateDelegate>(NativeLibrary.GetExport(handle, "qg_index_evaluate")),
                FreeCandidates = Marshal.GetDelegateForFunctionPointer<FreeCandidatesDelegate>(NativeLibrary.GetExport(handle, "qg_index_free_candidates")),
                QueryContentBin = Marshal.GetDelegateForFunctionPointer<QueryContentBinDelegate>(NativeLibrary.GetExport(handle, "qg_index_query_content_bin")),
                QueryPostingsV3 = Marshal.GetDelegateForFunctionPointer<QueryPostingsV3Delegate>(NativeLibrary.GetExport(handle, "qg_index_query_postings_v3")),
            };
        }
        catch
        {
            // Old DLL without the index exports, or load failure → skip (native parity unavailable).
            return null;
        }
    }

    private static string? FindNativeDll()
    {
        string root = FindRepoRoot();
        string appBin = Path.Combine(root, "src", "Yagu", "bin");
        if (!Directory.Exists(appBin))
            return null;
        // Prefer the DLL from the same configuration the tests are running under.
        string config = typeof(ContentIndexRustParityTests).Assembly.Location
            .Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            ? "Release" : "Debug";
        string configBin = Path.Combine(appBin, config);
        string? preferred = Directory.Exists(configBin)
            ? Directory.EnumerateFiles(configBin, "yagu_core.dll", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        return preferred ?? Directory.EnumerateFiles(appBin, "yagu_core.dll", SearchOption.AllDirectories).FirstOrDefault();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    private static (int Verdict, uint[] Trigrams) RustExtract(byte[] input)
    {
        NativeFns native = Native!;
        var gch = GCHandle.Alloc(input, GCHandleType.Pinned);
        try
        {
            IntPtr ptr = input.Length == 0 ? IntPtr.Zero : gch.AddrOfPinnedObject();
            int rc = native.Extract(ptr, (nuint)input.Length, out QgTrigramResult result);
            Assert.Equal(0, rc);
            try
            {
                int count = checked((int)result.TrigramCount);
                var trigrams = new uint[count];
                if (count > 0)
                {
                    var asInts = new int[count];
                    Marshal.Copy(result.Trigrams, asInts, 0, count);
                    for (int i = 0; i < count; i++)
                        trigrams[i] = unchecked((uint)asInts[i]);
                }
                return (result.Verdict, trigrams);
            }
            finally
            {
                native.Free(ref result);
            }
        }
        finally
        {
            gch.Free();
        }
    }

    // Evaluates a query (as RPN bytes) over CSR-laid-out documents via the Rust FFI, returning the
    // candidate document-id set.
    private static HashSet<int> RustEvaluate(uint[] docTrigramsFlat, nuint[] docOffsets, byte[] queryRpn)
    {
        NativeFns native = Native!;
        var trigramsGch = GCHandle.Alloc(docTrigramsFlat, GCHandleType.Pinned);
        var offsetsGch = GCHandle.Alloc(docOffsets, GCHandleType.Pinned);
        var rpnGch = GCHandle.Alloc(queryRpn, GCHandleType.Pinned);
        try
        {
            IntPtr trigramsPtr = docTrigramsFlat.Length == 0 ? IntPtr.Zero : trigramsGch.AddrOfPinnedObject();
            IntPtr offsetsPtr = offsetsGch.AddrOfPinnedObject();
            IntPtr rpnPtr = queryRpn.Length == 0 ? IntPtr.Zero : rpnGch.AddrOfPinnedObject();

            int rc = native.Evaluate(
                trigramsPtr, (nuint)docTrigramsFlat.Length,
                offsetsPtr, (nuint)docOffsets.Length,
                rpnPtr, (nuint)queryRpn.Length,
                out QgCandidateResult result);
            Assert.Equal(0, rc);
            return DrainCandidates(native, ref result);
        }
        finally
        {
            trigramsGch.Free();
            offsetsGch.Free();
            rpnGch.Free();
        }
    }

    // Verifies + queries a serialized content.bin via the Rust FFI, returning the candidate id set.
    private static HashSet<int> RustQueryContentBin(byte[] contentBin, byte[] queryRpn)
    {
        NativeFns native = Native!;
        var binGch = GCHandle.Alloc(contentBin, GCHandleType.Pinned);
        var rpnGch = GCHandle.Alloc(queryRpn, GCHandleType.Pinned);
        try
        {
            IntPtr binPtr = contentBin.Length == 0 ? IntPtr.Zero : binGch.AddrOfPinnedObject();
            IntPtr rpnPtr = queryRpn.Length == 0 ? IntPtr.Zero : rpnGch.AddrOfPinnedObject();
            int rc = native.QueryContentBin(binPtr, (nuint)contentBin.Length, rpnPtr, (nuint)queryRpn.Length, out QgCandidateResult result);
            Assert.Equal(0, rc);
            return DrainCandidates(native, ref result);
        }
        finally
        {
            binGch.Free();
            rpnGch.Free();
        }
    }

    // Verifies + queries a serialized query-postings.v3 (format-v3) via the Rust FFI, returning the id set.
    private static HashSet<int> RustQueryPostingsV3(byte[] v3Bytes, byte[] queryRpn)
    {
        NativeFns native = Native!;
        var binGch = GCHandle.Alloc(v3Bytes, GCHandleType.Pinned);
        var rpnGch = GCHandle.Alloc(queryRpn, GCHandleType.Pinned);
        try
        {
            IntPtr binPtr = v3Bytes.Length == 0 ? IntPtr.Zero : binGch.AddrOfPinnedObject();
            IntPtr rpnPtr = queryRpn.Length == 0 ? IntPtr.Zero : rpnGch.AddrOfPinnedObject();
            int rc = native.QueryPostingsV3(binPtr, (nuint)v3Bytes.Length, rpnPtr, (nuint)queryRpn.Length, out QgCandidateResult result);
            Assert.Equal(0, rc);
            return DrainCandidates(native, ref result);
        }
        finally
        {
            binGch.Free();
            rpnGch.Free();
        }
    }

    private static HashSet<int> DrainCandidates(NativeFns native, ref QgCandidateResult result)
    {
        try
        {
            int count = checked((int)result.Count);
            var ids = new HashSet<int>(count);
            if (count > 0)
            {
                var buffer = new int[count];
                Marshal.Copy(result.Candidates, buffer, 0, count);
                foreach (int id in buffer)
                    ids.Add(id);
            }
            return ids;
        }
        finally
        {
            native.FreeCandidates(ref result);
        }
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static IEnumerable<byte[]> Corpus()
    {
        yield return Array.Empty<byte>();
        yield return Utf8("a");
        yield return Utf8("ab");
        yield return Utf8("abc");
        yield return Utf8("foofoo");
        yield return Utf8("the planner produces trigram queries");
        yield return Utf8("a\r\nb\r\nc");        // CRLF
        yield return Utf8("a\rb\rc");            // bare CR
        yield return Utf8("a\nb\nc");            // LF
        yield return Utf8("\n\n\n");            // three linefeeds → 0A0A0A
        yield return Utf8("café résumé naïve"); // multi-byte UTF-8
        yield return Utf8("mixed\r\nline\nendings\rhere");
        yield return Utf8("void Main() { Console.WriteLine(\"hi\"); }\n// comment\n");
        yield return new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a', (byte)'b', (byte)'c' }; // UTF-8 BOM
        yield return new byte[] { 0xFF, 0xFE, (byte)'a', (byte)'b' };                  // UTF-16 LE BOM
        yield return new byte[] { 0xFE, 0xFF, (byte)'a', (byte)'b' };                  // UTF-16 BE BOM
        yield return new byte[] { 0x00, 0x00, 0xFE, 0xFF, (byte)'a' };                 // UTF-32 BE BOM
        yield return new byte[] { 0xFF, 0xFE, 0x00, 0x00, (byte)'a' };                 // UTF-32 LE BOM
        yield return new byte[] { (byte)'a', (byte)'b', 0x00, (byte)'c' };             // embedded NUL → binary
        yield return new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A };                // PNG magic
        yield return new byte[] { 0x4D, 0x5A, 0x90, 0x00 };                            // MZ magic
        yield return new byte[] { (byte)'a', 0xC3, 0x28 };                             // invalid continuation
        yield return new byte[] { (byte)'a', 0xC0, 0x80 };                             // overlong
        yield return new byte[] { (byte)'a', 0xED, 0xA0, 0x80 };                       // surrogate half
        // A 600-byte control-byte blob → binary via the control-ratio heuristic.
        yield return Enumerable.Repeat((byte)0x01, 600).ToArray();
    }

    [Fact]
    public void RustTrigramExtraction_MatchesManagedReference_AcrossCorpus()
    {
        if (Native is null)
            return; // native DLL not built → skip (parity is validated on the dev box)

        foreach (byte[] input in Corpus())
        {
            ContentRepresentationVerdict managedVerdict = ContentRepresentation.Classify(input, out var managedTrigrams);
            uint[] managed = managedTrigrams.Select(t => t.Value).ToArray();

            (int rustVerdict, uint[] rustTrigrams) = RustExtract(input);

            Assert.Equal((int)managedVerdict, rustVerdict);
            Assert.Equal(managed, rustTrigrams);
        }
    }

    [Fact]
    public void RustAndManaged_AgreeOnRandomizedInputs()
    {
        if (Native is null)
            return;

        var rng = new Random(20260720);
        for (int iter = 0; iter < 300; iter++)
        {
            int len = rng.Next(0, 400);
            var input = new byte[len];
            rng.NextBytes(input);

            ContentRepresentationVerdict managedVerdict = ContentRepresentation.Classify(input, out var managedTrigrams);
            uint[] managed = managedTrigrams.Select(t => t.Value).ToArray();

            (int rustVerdict, uint[] rustTrigrams) = RustExtract(input);

            Assert.Equal((int)managedVerdict, rustVerdict);
            Assert.Equal(managed, rustTrigrams);
        }
    }

    [Fact]
    public void RustIndexAbiVersion_IsExpected()
    {
        if (Native is null)
            return;
        Assert.Equal(1u, Native.Abi());
    }

    // Builds a random TrigramExpression over a small trigram alphabet (with All/None simplification).
    private static TrigramExpression RandomQuery(Random rng, Trigram[] alphabet, int depth)
    {
        if (depth <= 0 || rng.Next(3) == 0)
        {
            int leaf = rng.Next(12);
            if (leaf == 0) return TrigramExpression.All;
            if (leaf == 1) return TrigramExpression.None;
            return TrigramExpression.OfTrigram(alphabet[rng.Next(alphabet.Length)]);
        }
        TrigramExpression left = RandomQuery(rng, alphabet, depth - 1);
        TrigramExpression right = RandomQuery(rng, alphabet, depth - 1);
        return rng.Next(2) == 0
            ? TrigramExpression.And(left, right)
            : TrigramExpression.Or(left, right);
    }

    [Fact]
    public void RustPostingEvaluation_MatchesManagedReference_AcrossRandomizedQueries()
    {
        if (Native is null)
            return;

        var rng = new Random(20260720);
        // A small trigram alphabet keeps postings dense enough that AND/OR are non-trivially exercised.
        Trigram[] alphabet = Enumerable.Range(0, 12)
            .Select(_ => new Trigram((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256)))
            .Distinct()
            .ToArray();

        for (int iter = 0; iter < 200; iter++)
        {
            int docCount = rng.Next(0, 25);
            var docs = new List<IReadOnlyCollection<Trigram>>(docCount);
            var flat = new List<uint>();
            var offsets = new List<nuint> { 0 };
            for (int d = 0; d < docCount; d++)
            {
                var set = new HashSet<Trigram>();
                int size = rng.Next(0, alphabet.Length + 1);
                for (int k = 0; k < size; k++)
                    set.Add(alphabet[rng.Next(alphabet.Length)]);
                docs.Add(set);
                foreach (uint value in set.Select(t => t.Value).OrderBy(v => v))
                    flat.Add(value);
                offsets.Add((nuint)flat.Count);
            }

            TrigramExpression query = RandomQuery(rng, alphabet, depth: 3);
            byte[] rpn = TrigramQueryRpn.Encode(query);

            var managed = new HashSet<int>(TrigramPostingIndex.Build(docs).EvaluateSet(query));
            HashSet<int> rust = RustEvaluate(flat.ToArray(), offsets.ToArray(), rpn);

            Assert.True(managed.SetEquals(rust),
                $"iter={iter} docCount={docCount} query mismatch: managed=[{string.Join(",", managed.OrderBy(x => x))}] rust=[{string.Join(",", rust.OrderBy(x => x))}]");
        }
    }

    [Fact]
    public void RustContentBinQuery_MatchesManagedReference_ForRealGeneration()
    {
        if (Native is null)
            return;

        string sandbox = Path.Combine(Path.GetTempPath(), "yagu-rust-contentbin", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        try
        {
            // Build a real generation from documents with a shared vocabulary (so postings overlap),
            // then serialize it with the managed reference serializer.
            var policy = new IndexIngestionPolicy(0, null, null, includeHiddenFiles: true, followReparsePoints: false, 0);
            var builder = new ContentIndexGenerationBuilder(policy);
            builder.AddDocument(@"C:\r\a.txt", Encoding.UTF8.GetBytes("the quick brown fox jumps over the lazy dog"));
            builder.AddDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("the lazy dog sleeps while the quick fox runs"));
            builder.AddDocument(@"C:\r\c.txt", Encoding.UTF8.GetBytes("brown foxes and quick rabbits everywhere"));
            builder.AddDocument(@"C:\r\d.txt", Encoding.UTF8.GetBytes("nothing whatsoever in common with the others here"));
            ContentIndexGeneration gen = builder.Build("scope", "vol", @"C:\r", new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);

            ContentIndexGenerationSerializer.Write(sandbox, gen);
            byte[] contentBin = File.ReadAllBytes(Path.Combine(sandbox, ContentIndexGenerationSerializer.ContentFile));

            var postingIndex = TrigramPostingIndex.Build(gen.Documents);

            foreach (string term in new[] { "quick", "lazy", "brown", "fox", "the", "quick fox", "zzzzz", "dog" })
            {
                var options = new SearchOptions
                {
                    Directory = @"C:\r",
                    Query = term,
                    CaseSensitive = true,
                    ExactMatch = false,
                    UseContentIndex = true,
                };
                var pattern = EffectiveSearchPattern.Resolve(options);
                if (TrigramQueryPlanner.Plan(pattern) is not TrigramPlan.Eligible eligible)
                    continue;

                byte[] rpn = TrigramQueryRpn.Encode(eligible.Query);
                var managed = new HashSet<int>(postingIndex.EvaluateSet(eligible.Query));
                HashSet<int> rust = RustQueryContentBin(contentBin, rpn);

                Assert.True(managed.SetEquals(rust),
                    $"term '{term}': managed=[{string.Join(",", managed.OrderBy(x => x))}] rust=[{string.Join(",", rust.OrderBy(x => x))}]");
            }
        }
        finally
        {
            try { Directory.Delete(sandbox, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void RustV3PostingsQuery_MatchesManagedReference_ForRealGeneration()
    {
        if (Native is null)
            return;

        string sandbox = Path.Combine(Path.GetTempPath(), "yagu-rust-v3", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        try
        {
            var policy = new IndexIngestionPolicy(0, null, null, includeHiddenFiles: true, followReparsePoints: false, 0);
            var builder = new ContentIndexGenerationBuilder(policy);
            builder.AddDocument(@"C:\r\a.txt", Encoding.UTF8.GetBytes("the quick brown fox jumps over the lazy dog"));
            builder.AddDocument(@"C:\r\b.txt", Encoding.UTF8.GetBytes("the lazy dog sleeps while the quick fox runs"));
            builder.AddDocument(@"C:\r\c.txt", Encoding.UTF8.GetBytes("brown foxes and quick rabbits everywhere"));
            builder.AddDocument(@"C:\r\d.txt", Encoding.UTF8.GetBytes("nothing whatsoever in common with the others here"));
            ContentIndexGeneration gen = builder.Build("scope", "vol", @"C:\r", new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);

            // Write the format-v3 query structures and read the postings sidecar's raw bytes.
            ContentIndexV3Format.Write(sandbox, gen);
            byte[] v3Postings = File.ReadAllBytes(Path.Combine(sandbox, ContentIndexV3Format.PostingsFile));

            // The managed reference reader parses the same file; the Rust FFI must agree on every query.
            using ContentIndexV3Reader managedReader = ContentIndexV3Format.TryOpen(sandbox)!;

            foreach (string term in new[] { "quick", "lazy", "brown", "fox", "the", "quick fox", "zzzzz", "dog" })
            {
                var options = new SearchOptions
                {
                    Directory = @"C:\r",
                    Query = term,
                    CaseSensitive = true,
                    ExactMatch = false,
                    UseContentIndex = true,
                };
                if (TrigramQueryPlanner.Plan(EffectiveSearchPattern.Resolve(options)) is not TrigramPlan.Eligible eligible)
                    continue;

                byte[] rpn = TrigramQueryRpn.Encode(eligible.Query);
                var managed = new HashSet<int>(managedReader.EvaluateSet(eligible.Query));
                HashSet<int> rust = RustQueryPostingsV3(v3Postings, rpn);

                Assert.True(managed.SetEquals(rust),
                    $"term '{term}': managed=[{string.Join(",", managed.OrderBy(x => x))}] rust=[{string.Join(",", rust.OrderBy(x => x))}]");
            }
        }
        finally
        {
            try { Directory.Delete(sandbox, recursive: true); } catch { /* best effort */ }
        }
    }
}
