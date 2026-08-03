namespace Yagu.Helpers;

/// <summary>A request to point the running Yagu instance at a directory and/or query, optionally
/// running the search immediately. Delivered either in-process (tray "Quick search") or across
/// processes (a second launch, e.g. the Explorer context menu, forwarding to the single instance).</summary>
internal readonly record struct SearchRequest(string? Directory, string? Query, bool RunSearch);

/// <summary>
/// Pure line-based codec for <see cref="SearchRequest"/> so the wire payload can be unit-tested
/// without touching Win32. Format is a small, order-independent set of <c>key=value</c> lines:
/// <c>dir=</c>, <c>query=</c>, <c>run=</c>. Values are single-line (newlines are stripped) and the
/// magic header guards against unrelated WM_COPYDATA senders.
/// </summary>
internal static class SearchRequestCodec
{
    public const string Header = "yagu-search-request/1";

    public static string Encode(SearchRequest request)
    {
        // Directory and query are always single-line here (a folder path / one search term), but strip
        // any stray newline defensively so a value can never inject an extra key line on decode.
        string dir = Sanitize(request.Directory);
        string query = Sanitize(request.Query);
        return string.Join('\n',
            Header,
            "dir=" + dir,
            "query=" + query,
            "run=" + (request.RunSearch ? "1" : "0"));
    }

    public static bool TryDecode(string? payload, out SearchRequest request)
    {
        request = default;
        if (string.IsNullOrEmpty(payload)) return false;

        payload = payload.TrimEnd('\0');
        if (payload.Length == 0) return false;

        var lines = payload.Split('\n');
        if (!string.Equals(lines[0], Header, StringComparison.Ordinal))
            return false;

        string? dir = null;
        string? query = null;
        bool run = false;

        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq];
            var value = line[(eq + 1)..];
            switch (key)
            {
                case "dir": dir = NullIfEmpty(value); break;
                case "query": query = NullIfEmpty(value); break;
                case "run": run = value == "1"; break;
            }
        }

        request = new SearchRequest(dir, query, run);
        return true;
    }

    private static string Sanitize(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", " ").Replace("\n", " ");

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
