using System;
using System.Collections.Generic;
using System.Text;
using Yagu.Services;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for the build-time ingestion policy and classifier (plan §3.6): metadata gating
/// (depth/hidden/cloud/reparse/size/extension/glob), content gating via the canonical representation,
/// and construction from the persisted Indexing settings.
/// </summary>
public sealed class IndexIngestionPolicyTests
{
    private static IndexIngestionPolicy Policy(
        long maxBytes = 0,
        IReadOnlyList<string>? globs = null,
        IReadOnlySet<string>? exts = null,
        bool hidden = true,
        bool reparse = false,
        int maxDepth = 0,
        bool indexBinary = false)
        => new(maxBytes, globs, exts, hidden, reparse, maxDepth, indexBinaryAsciiContent: indexBinary);

    private static IngestionFileInfo File(
        string path = @"C:\src\file.txt",
        long size = 100,
        int depth = 1,
        bool isHidden = false,
        bool isReparse = false,
        bool isCloud = false)
        => new(path, size, depth, isHidden, isReparse, isCloud);

    [Fact]
    public void ClassifyFile_AdmitsNormalFile()
        => Assert.Equal(IndexSkipReason.None, IndexIngestionClassifier.ClassifyFile(File(), Policy()));

    [Fact]
    public void ClassifyFile_OverDepth()
        => Assert.Equal(IndexSkipReason.OverDepth,
            IndexIngestionClassifier.ClassifyFile(File(depth: 10), Policy(maxDepth: 5)));

    [Fact]
    public void ClassifyFile_HiddenSkippedWhenNotIncluded()
        => Assert.Equal(IndexSkipReason.Hidden,
            IndexIngestionClassifier.ClassifyFile(File(isHidden: true), Policy(hidden: false)));

    [Fact]
    public void ClassifyFile_HiddenAdmittedWhenIncluded()
        => Assert.Equal(IndexSkipReason.None,
            IndexIngestionClassifier.ClassifyFile(File(isHidden: true), Policy(hidden: true)));

    [Fact]
    public void ClassifyFile_CloudOnlyAlwaysSkipped()
        => Assert.Equal(IndexSkipReason.CloudOnly,
            IndexIngestionClassifier.ClassifyFile(File(isCloud: true), Policy()));

    [Fact]
    public void ClassifyFile_ReparseSkippedUnlessFollowed()
    {
        Assert.Equal(IndexSkipReason.ReparsePointSkipped,
            IndexIngestionClassifier.ClassifyFile(File(isReparse: true), Policy(reparse: false)));
        Assert.Equal(IndexSkipReason.None,
            IndexIngestionClassifier.ClassifyFile(File(isReparse: true), Policy(reparse: true)));
    }

    [Fact]
    public void ClassifyFile_OverSizeCap()
        => Assert.Equal(IndexSkipReason.OverSizeCap,
            IndexIngestionClassifier.ClassifyFile(File(size: 5000), Policy(maxBytes: 1000)));

    [Fact]
    public void ClassifyFile_ExcludedByExtension()
    {
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "log" };
        Assert.Equal(IndexSkipReason.ExcludedByExtension,
            IndexIngestionClassifier.ClassifyFile(File(path: @"C:\a\b.LOG"), Policy(exts: exts)));
    }

    [Fact]
    public void ClassifyFile_ExtensionlessFile_NotExcluded_EvenWithExtensionList()
    {
        // A file with no extension must not match the excluded-extension list (the ext.Length==0 gate).
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "log" };
        Assert.Equal(IndexSkipReason.None,
            IndexIngestionClassifier.ClassifyFile(File(path: @"C:\a\Makefile"), Policy(exts: exts)));
    }

    [Fact]
    public void FromSettings_ParsesNonEmptyGlobAndExtensionLists()
    {
        var settings = new AppSettings
        {
            IndexExcludedGlobs = "**/node_modules/**, **/bin/**",
            IndexExcludedExtensions = "*.png, .jpg; obj",
        };
        var policy = IndexIngestionPolicy.FromSettings(settings);

        Assert.Equal(2, policy.ExcludedGlobs.Count);
        // Leading "*." and "." are stripped so the set holds bare extensions.
        Assert.Contains("png", policy.ExcludedExtensions);
        Assert.Contains("jpg", policy.ExcludedExtensions);
        Assert.Contains("obj", policy.ExcludedExtensions);
    }

    [Fact]
    public void FromSettings_EmptyLists_YieldEmptyPolicy()
    {
        // Blank settings exercise the whitespace short-circuit in the list/extension parsers.
        var policy = IndexIngestionPolicy.FromSettings(new AppSettings
        {
            IndexExcludedGlobs = "",
            IndexExcludedExtensions = "   ",
        });
        Assert.Empty(policy.ExcludedGlobs);
        Assert.Empty(policy.ExcludedExtensions);
    }

    [Fact]
    public void ReAdmitGlobs_OverrideAnExcludeForThatRootsPolicy()
    {
        // A per-root re-admit glob (gitignore-style negation) re-admits a path an exclude would drop.
        var withReAdmit = new IndexIngestionPolicy(
            0, new[] { "**/node_modules/**" }, null, includeHiddenFiles: true, followReparsePoints: false, 0,
            reAdmitGlobs: new[] { "**/node_modules/**" });
        Assert.False(withReAdmit.IsGloballyExcluded(@"C:\r\node_modules\x.js"));
        Assert.NotEmpty(withReAdmit.ReAdmitGlobs);

        var withoutReAdmit = new IndexIngestionPolicy(
            0, new[] { "**/node_modules/**" }, null, includeHiddenFiles: true, followReparsePoints: false, 0);
        Assert.True(withoutReAdmit.IsGloballyExcluded(@"C:\r\node_modules\x.js"));
        Assert.Empty(withoutReAdmit.ReAdmitGlobs);
    }

    [Fact]
    public void FromSettings_WithRootFilter_MergesExtraExcludesAndReAdmits()
    {
        var settings = new AppSettings { IndexExcludedGlobs = "**/bin/**" };
        var filter = new IndexedRootFilter { Path = @"C:\r", ExcludeGlobs = "**/obj/**", IncludeGlobs = "**/bin/**" };

        var policy = IndexIngestionPolicy.FromSettings(settings, filter);

        Assert.Contains("**/obj/**", policy.ExcludedGlobs);
        Assert.Contains("**/bin/**", policy.ExcludedGlobs);
        Assert.True(policy.IsGloballyExcluded(@"C:\r\obj\a.o"));    // per-root exclude added
        Assert.False(policy.IsGloballyExcluded(@"C:\r\bin\a.dll")); // global exclude re-admitted for this root
    }

    [Fact]
    public void ClassifyFile_ExcludedByGlob()
    {
        var globs = new[] { "node_modules" };
        Assert.Equal(IndexSkipReason.ExcludedByGlob,
            IndexIngestionClassifier.ClassifyFile(File(path: @"C:\app\node_modules\x.js"), Policy(globs: globs)));
        Assert.Equal(IndexSkipReason.None,
            IndexIngestionClassifier.ClassifyFile(File(path: @"C:\app\src\x.js"), Policy(globs: globs)));
    }

    [Fact]
    public void ClassifyContent_AdmitsUtf8_YieldsTrigrams()
    {
        var result = IndexIngestionClassifier.ClassifyContent(Encoding.UTF8.GetBytes("hello"), Policy());
        Assert.True(result.Admitted);
        Assert.Equal(IndexSkipReason.None, result.Reason);
        Assert.NotEmpty(result.Trigrams);
    }

    [Fact]
    public void ClassifyContent_BinaryRejected_Utf8BomStripped()
    {
        var binary = IndexIngestionClassifier.ClassifyContent(new byte[] { (byte)'a', 0, (byte)'b' }, Policy());
        Assert.Equal(IndexSkipReason.Binary, binary.Reason);
        Assert.False(binary.Admitted);

        var bom = IndexIngestionClassifier.ClassifyContent(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'x' }, Policy());
        Assert.Equal(IndexSkipReason.None, bom.Reason);
        Assert.True(bom.Admitted);
    }

    [Fact]
    public void ClassifyContent_BinaryAsciiEnabled_AdmitsPrintableRuns()
    {
        byte[] content = new byte[] { (byte)'a', (byte)'b', (byte)'c', 0, (byte)'d', (byte)'e', (byte)'f' };

        IndexContentClassification result =
            IndexIngestionClassifier.ClassifyContent(content, Policy(indexBinary: true));

        Assert.True(result.Admitted);
        Assert.Contains(new Trigram((byte)'a', (byte)'b', (byte)'c'), result.Trigrams);
        Assert.Contains(new Trigram((byte)'d', (byte)'e', (byte)'f'), result.Trigrams);
        Assert.DoesNotContain(new Trigram((byte)'b', (byte)'c', (byte)'d'), result.Trigrams);
    }

    [Fact]
    public void ClassifyContent_BinaryAsciiOverflow_RemainsUnindexed()
    {
        using var stream = new MemoryStream();
        int needed = BinaryAsciiContentRepresentation.MaxDistinctTrigramsPerFile + 1;
        for (int index = 0; index < needed; index++)
        {
            stream.WriteByte((byte)(0x20 + (index / (95 * 95)) % 95));
            stream.WriteByte((byte)(0x20 + (index / 95) % 95));
            stream.WriteByte((byte)(0x20 + index % 95));
            stream.WriteByte(0);
        }

        IndexContentClassification result =
            IndexIngestionClassifier.ClassifyContent(stream.ToArray(), Policy(indexBinary: true));

        Assert.Equal(IndexSkipReason.Binary, result.Reason);
        Assert.Empty(result.Trigrams);
    }

    [Fact]
    public void ClassifyContent_InvalidUtf8_IsUnsupportedEncoding()
    {
        IndexContentClassification result =
            IndexIngestionClassifier.ClassifyContent(new byte[] { 0xC2 }, Policy());

        Assert.Equal(IndexSkipReason.UnsupportedEncoding, result.Reason);
        Assert.Empty(result.Trigrams);
    }

    [Fact]
    public void ClassifyContent_OverSizeCap()
    {
        var big = Encoding.ASCII.GetBytes(new string('x', 100));
        var result = IndexIngestionClassifier.ClassifyContent(big, Policy(maxBytes: 10));
        Assert.Equal(IndexSkipReason.OverSizeCap, result.Reason);
    }

    [Fact]
    public void FromSettings_MapsIndexingSettings()
    {
        var settings = new AppSettings
        {
            IndexMaxFileSizeMB = 50,
            IndexExcludedGlobs = "node_modules;bin",
            IndexExcludedExtensions = "log;*.tmp;.bak",
            IndexIncludeHiddenFiles = false,
            IndexFollowReparsePoints = true,
        };
        var policy = IndexIngestionPolicy.FromSettings(settings);
        Assert.Equal(50L * 1024 * 1024, policy.MaxFileSizeBytes);
        Assert.Equal(2, policy.ExcludedGlobs.Count);
        Assert.Contains("log", policy.ExcludedExtensions);
        Assert.Contains("tmp", policy.ExcludedExtensions); // "*.tmp" → "tmp"
        Assert.Contains("bak", policy.ExcludedExtensions); // ".bak" → "bak"
        Assert.False(policy.IncludeHiddenFiles);
        Assert.True(policy.FollowReparsePoints);
    }

    [Fact]
    public void IsGloballyExcluded_EmptyGlobs_NeverExcludes()
        => Assert.False(Policy().IsGloballyExcluded(@"C:\anything\x.cs"));
}
