namespace Yagu.Services.Index;

internal enum IndexingCloseTrigger
{
    UserExit,
    WindowsSessionEnding,
    AppUpdate,
}

internal sealed record IndexingCloseWarningContent(
    string Title,
    string Message,
    string KeepOpenButtonText,
    string ExitButtonText);

internal static class IndexingCloseWarning
{
    internal static IndexingCloseWarningContent Build(
        IndexingCloseTrigger trigger,
        bool isIncremental,
        string? activeFolder)
    {
        string target = string.IsNullOrWhiteSpace(activeFolder)
            ? "the active folder"
            : $"“{activeFolder.Trim()}”";
        string operation = isIncremental
            ? $"Yagu is incrementally updating the content index for {target}. Closing now leaves this update incomplete. "
                + "The next update will replay from the last committed checkpoint; if journal continuity can no longer "
                + "be proven, a complete rebuild will be required."
            : $"Yagu is building a complete content index for {target}. Closing now leaves this build incomplete. "
                + "Its partial workspace will be discarded, and a complete build must start again later.";
        string safety = " The previous complete index, if one exists, remains unchanged.";

        return trigger switch
        {
            IndexingCloseTrigger.WindowsSessionEnding => new IndexingCloseWarningContent(
                "Windows requested shutdown during indexing",
                "Windows requested a restart, shutdown, or sign-out. Yagu stopped that request so you can decide safely. "
                    + operation + safety
                    + " If you exit anyway, retry the Windows operation after Yagu closes.",
                "Keep Yagu open",
                "Exit Yagu anyway"),
            IndexingCloseTrigger.AppUpdate => new IndexingCloseWarningContent(
                "Indexing is still in progress",
                operation + safety
                    + " The downloaded update will not start unless you choose to exit Yagu.",
                "Keep indexing",
                "Install and exit anyway"),
            _ => new IndexingCloseWarningContent(
                "Indexing is still in progress",
                operation + safety,
                "Keep Yagu open",
                "Exit anyway"),
        };
    }
}
