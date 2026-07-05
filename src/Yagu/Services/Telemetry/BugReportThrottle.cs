using System.Security.Cryptography;
using System.Text;

namespace Yagu.Services.Telemetry;

/// <summary>
/// Prevents the bug-report dialog from spamming the user when an error repeats (e.g. a crash loop or
/// a critical logged on every keystroke). A given error "signature" (source + exception type + a
/// short hash of the scrubbed message) is offered at most once per process. Pure managed and
/// thread-safe so it links into the test project.
/// </summary>
public sealed class BugReportThrottle
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private readonly int _maxDistinct;

    /// <param name="maxDistinct">Hard ceiling on how many distinct errors may be offered in one
    /// session, as a backstop against an error storm with ever-changing messages.</param>
    public BugReportThrottle(int maxDistinct = 8)
    {
        _maxDistinct = Math.Max(1, maxDistinct);
    }

    /// <summary>Computes a stable, non-identifying signature for an error. Uses the scrubbed message
    /// so paths/queries never influence (or leak through) the signature.</summary>
    public static string Signature(string source, string exceptionType, string scrubbedMessage)
    {
        string basis = (source ?? string.Empty) + "|" + (exceptionType ?? string.Empty) + "|" + (scrubbedMessage ?? string.Empty);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(basis));
        // 8 bytes of hex is plenty to distinguish error sites without being unwieldy.
        return source + ":" + exceptionType + ":" + Convert.ToHexString(hash, 0, 8);
    }

    /// <summary>Returns true the FIRST time a given signature is seen (and records it), false on
    /// every subsequent occurrence or once the per-session ceiling is reached.</summary>
    public bool ShouldOffer(string signature)
    {
        if (string.IsNullOrEmpty(signature))
            return false;

        lock (_lock)
        {
            if (_seen.Contains(signature))
                return false;
            if (_seen.Count >= _maxDistinct)
                return false;
            _seen.Add(signature);
            return true;
        }
    }

    /// <summary>Test/diagnostic helper: number of distinct errors offered so far.</summary>
    public int OfferedCount
    {
        get { lock (_lock) { return _seen.Count; } }
    }
}
