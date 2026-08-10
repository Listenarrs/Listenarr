namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    internal Func<string, string, Task>? AfterFileMoveEndpointsResolvedForTestAsync
    {
        get;
        init;
    }
    internal Func<Task>? BeforePinnedHardlinkCreationForTestAsync { get; init; }
    internal Func<Task>?
        AfterMarkerlessRegistrationTargetCreatedBeforeStateForTestAsync
    {
        get;
        init;
    }
    internal Func<Task>? AfterMarkerlessRegistrationTargetStateForTestAsync
    {
        get;
        init;
    }
    internal Func<Task>?
        AfterMarkerlessRegistrationTargetWrittenBeforeVerifiedStateForTestAsync
    {
        get;
        init;
    }
    internal Func<Task>? AfterMarkerlessRenameJournalPlannedForTestAsync
    {
        get;
        init;
    }
    internal Func<Task>?
        AfterMarkerlessRenamePublishedBeforeTargetStateForTestAsync
    {
        get;
        init;
    }
    internal Func<Task>? AfterMarkerlessRenameTargetStateForTestAsync
    {
        get;
        init;
    }
    internal Func<Task>? AfterMarkerlessMoveJournalPlannedForTestAsync
    {
        get;
        init;
    }
    internal Func<Task>?
        AfterMarkerlessMovePublishedBeforeTargetStateForTestAsync
    {
        get;
        init;
    }
    internal Func<Task>? AfterMarkerlessMoveTargetCreatedBeforeStateForTestAsync
    {
        get;
        init;
    }
    internal Func<Task>? AfterMarkerlessMoveTargetStateForTestAsync
    {
        get;
        init;
    }
    internal Func<Task>?
        AfterMarkerlessMoveTargetWrittenBeforeVerifiedStateForTestAsync
    {
        get;
        init;
    }
    internal Func<Task>? AfterMarkerlessMoveSourceDeletedBeforeStateForTestAsync
    {
        get;
        init;
    }
    internal bool DisableNativeFileRenameForTest { get; init; }
    internal bool ForceCrossVolumeForTest { get; init; }
    internal Action<string>? BeforeFileMoveDurabilityBarrierForTest { get; init; }
    internal string? FileMoveLockDirectoryForTest { get; init; }
}
