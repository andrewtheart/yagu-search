using Yagu.Services;

namespace Yagu.Helpers;

/// <summary>
/// Counting and list bookkeeping for the saved-session picker, kept out of the WinUI dialog so the
/// summary wording and the delete-by-path rule stay directly testable.
/// </summary>
internal static class SessionPickerList
{
    internal static string BuildSummary(int count) => count == 0
        ? "No .yagu-session files found by Everything."
        : $"{count:N0} .yagu-session file{(count == 1 ? string.Empty : "s")} found";

    /// <summary>Drops every candidate at <paramref name="path"/>, matched case-insensitively because
    /// Everything can report a different casing than the deleted file used. Returns how many rows went.</summary>
    internal static int RemoveByPath(List<SessionFileCandidate> sessions, string path)
        => sessions.RemoveAll(candidate => string.Equals(candidate.Path, path, StringComparison.OrdinalIgnoreCase));
}
