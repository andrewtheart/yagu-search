namespace Yagu.Services.Index;

public sealed partial class ContentIndexIncrementalRefresher
{
    public IncrementalUpdateOutcome Refresh(
        string scopeId,
        AppSettings settings,
        DateTimeOffset builtUtc,
        Action<int, string>? progress = null)
        => Refresh(
            scopeId,
            IndexBuildOperationFactory.CreateMaintenanceSettings(settings),
            builtUtc,
            progress);
}
