namespace Yagu.Services.Index;

/// <summary>App-only compatibility adapters from mutable settings to worker-safe maintenance snapshots.</summary>
public sealed partial class ContentIndexManager
{
    public bool CompactScopeIfOverSegmented(
        string rootDirectory,
        IndexIngestionPolicy policy,
        AppSettings settings,
        DateTimeOffset builtUtc)
        => CompactScopeIfOverSegmented(
            rootDirectory,
            policy,
            IndexBuildOperationFactory.CreateMaintenanceSettings(settings),
            builtUtc);
}
