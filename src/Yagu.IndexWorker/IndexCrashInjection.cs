using System.Globalization;
using System.Text;
using Yagu.Services.Index;

namespace Yagu.IndexWorker;

/// <summary>Debug-only hard-crash driver used by the persistence recovery matrix.</summary>
internal static class IndexCrashInjection
{
    internal const string PointVariable = "YAGU_INDEX_CRASH_POINT";
    internal const string OccurrenceVariable = "YAGU_INDEX_CRASH_OCCURRENCE";
    internal const string LogVariable = "YAGU_INDEX_CRASH_LOG";

    public static void InstallFromEnvironment()
    {
#if DEBUG
        string? target = Environment.GetEnvironmentVariable(PointVariable);
        if (string.IsNullOrWhiteSpace(target))
            return;

        int targetOccurrence = int.TryParse(
            Environment.GetEnvironmentVariable(OccurrenceVariable),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int parsed)
            ? Math.Max(1, parsed)
            : 1;
        string? logPath = Environment.GetEnvironmentVariable(LogVariable);
        int matchingHits = 0;
        IndexMutationFaults.OnHit = point =>
        {
            if (!string.Equals(point, target, StringComparison.Ordinal))
                return;
            if (Interlocked.Increment(ref matchingHits) != targetOccurrence)
                return;

            if (!string.IsNullOrWhiteSpace(logPath))
            {
                string? parent = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);
                byte[] bytes = Encoding.UTF8.GetBytes(point + "\n");
                using var stream = new FileStream(
                    logPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.Read,
                    4096,
                    FileOptions.WriteThrough);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            // Block without unwinding. The parent test observes the durably flushed log and kills this exact
            // worker PID, which models abrupt process death without executing catch/finally cleanup.
            using var never = new ManualResetEventSlim(false);
            never.Wait();
        };
#endif
    }
}
