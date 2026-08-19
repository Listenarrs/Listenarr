namespace Listenarr.Infrastructure.Library.Moving;

internal enum SourceCleanupFaultPoint
{
    AfterMarkerlessSourceDeleteAuthorizedState,
    AfterMarkerlessSourceFileDeleteBeforeStateUpdate,
    AfterMarkerlessSourceFileStateUpdate
}

internal enum CopyMutationFaultPoint
{
    AfterMarkerlessFileCreationBeforeStateUpdate,
    AfterMarkerlessFileStateUpdate,
    AfterMarkerlessFileWriteBeforePublishedState,
    BeforeMarkerlessMetadataPreservation,
    BeforeMarkerlessNativeRenameMutation,
    AfterMarkerlessNativeRenameFailureBeforeObservation,
    AfterMarkerlessNativeRenameFallbackAuthorized,
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
    BeforeCompletionCommitValidation,
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
    int? MarkerlessNativeRenameErrorForTest => null;
    bool MarkerlessNativeRenamePublishesBeforeErrorForTest => false;

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
