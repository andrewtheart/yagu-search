using System.IO;
using Yagu.Services;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Pins the opt-in, aggregate-only index telemetry (plan §6.4 / §11.4). <c>IndexTelemetry</c> references
/// <c>TelemetryService</c> (a whole HTTP/AppInfo chain not compiled into the test host), so the privacy and
/// offline-guard invariants are source-pinned instead of run: the share-gate must AND the index opt-in with
/// the global send-gate (so the index opt-in never phones home on its own), every report must early-return
/// when the gate is closed, and no reporting method may ever pass a root/path/query/content value.
/// </summary>
public sealed class IndexTelemetrySourcePinTests
{
    private static string Source() =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Yagu", "Services", "Index", "IndexTelemetry.cs"));

    [Fact]
    public void ShareGate_RequiresBothOptInAndGlobalSendGate()
    {
        string src = Source();
        // The index opt-in is necessary but NOT sufficient — it is ANDed with the global send-gate, which
        // itself already requires TelemetryConfig.IsConfigured (offline-by-default) and non-headless.
        Assert.Contains("settings.ShareAggregateIndexTelemetry && TelemetryGate.ShouldSendTelemetry", src);
    }

    [Fact]
    public void EveryReport_EarlyReturnsWhenGateClosed()
    {
        string src = Source();
        // Both report methods (ReportRefresh + ReportQueryOutcome) must bail before touching the telemetry
        // sink when sharing is off.
        Assert.Equal(2, CountOccurrences(src, "if (!ShouldShare(settings))"));
    }

    [Fact]
    public void Reports_CarryOnlyAggregateCountsAndTimings_NeverPathData()
    {
        string src = Source();

        // Allowed aggregate dimensions only.
        Assert.Contains("\"content_index_refresh\"", src);
        Assert.Contains("\"content_index_query\"", src);
        Assert.Contains("[\"segmentsAppended\"]", src);
        Assert.Contains("[\"compactions\"]", src);
        Assert.Contains("[\"indexUsed\"]", src);

        // Forbidden dimensions: no telemetry key may carry a root/path/query/content/trigram/identity VALUE.
        // (The aggregate COUNT keys rootsBuilt/rootsSkipped/rootsFailed are fine — they are integers, not
        // paths — so the guard targets the leak-prone full key names, not the substring "root".)
        foreach (string bannedKey in new[]
                 {
                     "[\"path\"", "[\"rootPath\"", "[\"fullPath\"", "[\"query\"", "[\"content\"",
                     "[\"trigram\"", "[\"trigrams\"", "[\"identity\"", "[\"fileName\"", "[\"directory\"",
                 })
        {
            Assert.DoesNotContain(bannedKey, src);
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
