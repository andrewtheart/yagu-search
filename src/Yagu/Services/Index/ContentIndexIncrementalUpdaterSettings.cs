namespace Yagu.Services.Index;

public sealed partial class ContentIndexIncrementalUpdater
{
    public IncrementalUpdateOutcome Apply(
        string scopeId,
        string volumeIdentity,
        string normalizedRootPath,
        IReadOnlyList<IncrementalChange> changed,
        IReadOnlyList<string> deletedPaths,
        UsnCheckpoint checkpoint,
        AppSettings settings,
        DateTimeOffset builtUtc)
        => Apply(
            scopeId,
            volumeIdentity,
            normalizedRootPath,
            changed,
            deletedPaths,
            checkpoint,
            IndexBuildOperationFactory.CreateMaintenanceSettings(settings),
            builtUtc);
}
