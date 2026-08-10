namespace Listenarr.Infrastructure.Library.Moving;

internal enum SourceCleanupFaultPoint
{
    AfterMarkerlessSourceFileDeleteBeforeStateUpdate,
    AfterMarkerlessSourceFileStateUpdate
}

internal enum CopyMutationFaultPoint
{
    AfterMarkerlessFileCreationBeforeStateUpdate,
    AfterMarkerlessFileStateUpdate,
    AfterMarkerlessFileWriteBeforePublishedState,
    BeforeMarkerlessMetadataPreservation,
    AfterMarkerlessNativeRenameBeforeStateUpdate
}

internal enum TargetScaffoldPreparationFaultPoint
{
    AfterMarkerlessDirectoryCreationBeforeStateUpdate,
    AfterMarkerlessDirectoryStateUpdate
}

internal enum MoveFinalizationFaultPoint
{
    BeforeSourceAncestorDelete
}

internal enum CompletionHandoffFaultPoint
{
    BeforeHistoryPersist,
    BeforeScanEnqueue
}

internal enum FinalizedVerificationFaultPoint
{
    BeforeManifestVerification
}

internal interface IMoveFaultInjector
{
    bool AllowMarkerlessFileRename => false;
    bool ForceCrossVolumeForTest => false;

    Task AfterPublishedAsync(Guid jobId, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    void OnSourceCleanupMutation(
        Guid jobId,
        SourceCleanupFaultPoint faultPoint)
    {
    }

    void OnCopyMutation(Guid jobId, CopyMutationFaultPoint faultPoint)
    {
    }

    void OnTargetScaffoldPreparation(
        Guid jobId,
        TargetScaffoldPreparationFaultPoint faultPoint)
    {
    }

    void OnMoveFinalization(
        Guid jobId,
        MoveFinalizationFaultPoint faultPoint)
    {
    }

    void OnCompletionHandoff(
        Guid jobId,
        CompletionHandoffFaultPoint faultPoint)
    {
    }

    void OnFinalizedVerification(
        Guid jobId,
        FinalizedVerificationFaultPoint faultPoint)
    {
    }
}
