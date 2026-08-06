namespace Listenarr.Infrastructure.Library.Moving;

internal enum RecoveryMarkerWriteFaultPoint
{
    BeforeTemporaryFileCreation,
    DuringJsonWrite,
    DuringFlush,
    AfterTemporaryFileWritten,
    BeforePublication,
    BeforeTemporaryFileDeletion
}

internal enum OwnershipMarkerKind
{
    TemporaryDirectory,
    QuarantineDirectory,
    CleanupTombstone
}

internal enum OwnershipMarkerWriteFaultPoint
{
    BeforeTemporaryFileCreation,
    DuringJsonWrite,
    DuringFlush,
    AfterTemporaryFileWritten,
    BeforePublication,
    BeforeRecoveredPublication,
    BeforeTemporaryFileDeletion
}

internal enum SourceCleanupFaultPoint
{
    BeforeSourceFileMove,
    BeforeSourceFilePublication,
    BeforeQuarantineFileDelete,
    BeforeQuarantineFileRemoval,
    BeforePinnedQuarantineDelete,
    AfterPinnedQuarantineDelete,
    BeforeEmptySourceDirectoryQuarantine,
    AfterEmptySourceDirectoryQuarantine,
    BeforeEmptySourceClaimDelete,
    BeforeEmptySourceStateDelete,
    AfterMarkerlessSourceFileDeleteBeforeStateUpdate,
    AfterMarkerlessSourceFileStateUpdate
}

internal enum CopyMutationFaultPoint
{
    BeforeCopyRootValidation,
    BeforePartialFileCreation,
    AfterChunkWritten,
    BeforePartialPublication,
    AfterMarkerlessFileCreationBeforeStateUpdate,
    AfterMarkerlessFileStateUpdate,
    AfterMarkerlessFileWriteBeforePublishedState,
    AfterMarkerlessNativeRenameBeforeStateUpdate
}

internal enum AtomicRenameFaultPoint
{
    BeforeSourceRevalidation,
    BeforeDirectoryPublication,
    AfterDirectoryMoveBeforeVerification
}

internal enum TempPublicationFaultPoint
{
    BeforeFinalValidation,
    BeforePublication
}

internal enum OwnershipCleanupFaultPoint
{
    BeforeCleanupDirectoryMove,
    BeforeOwnershipMarkerDelete,
    BeforeDirectoryDelete,
    BeforeTombstoneDelete
}

internal enum CompletedArtifactCleanupFaultPoint
{
    BeforeRecoveryMarkerDelete,
    BeforeFinalDestinationOwnershipValidation
}

internal enum TargetScaffoldPreparationFaultPoint
{
    BeforePublication,
    AfterPublication,
    AfterMarkerlessDirectoryCreationBeforeStateUpdate,
    AfterMarkerlessDirectoryStateUpdate
}

internal enum TargetScaffoldCleanupFaultPoint
{
    BeforeQuarantineRename,
    AfterQuarantineRename,
    BeforeQuarantineValidation,
    BeforeQuarantineDelete,
    DuringQuarantineDelete,
    BeforeCleanupIntentStateUpdate,
    AfterQuarantineDelete,
    BeforeRemovedStateUpdate
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
    bool AllowAtomicRename => false;
    bool AllowMarkerlessFileRename => false;

    Task AfterPublishedAsync(Guid jobId, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    void OnAtomicRename(Guid jobId, AtomicRenameFaultPoint faultPoint)
    {
    }

    void OnTempPublication(Guid jobId, TempPublicationFaultPoint faultPoint)
    {
    }

    void OnRecoveryMarkerWrite(
        Guid jobId,
        RecoveryMarkerWriteFaultPoint faultPoint)
    {
    }

    void OnOwnershipMarkerWrite(
        Guid jobId,
        OwnershipMarkerKind markerKind,
        OwnershipMarkerWriteFaultPoint faultPoint)
    {
    }

    void OnSourceCleanupMutation(
        Guid jobId,
        SourceCleanupFaultPoint faultPoint)
    {
    }

    void OnCopyMutation(Guid jobId, CopyMutationFaultPoint faultPoint)
    {
    }

    void OnOwnershipCleanup(
        Guid jobId,
        OwnershipMarkerKind markerKind,
        OwnershipCleanupFaultPoint faultPoint)
    {
    }

    void OnCompletedArtifactCleanup(
        Guid jobId,
        CompletedArtifactCleanupFaultPoint faultPoint)
    {
    }

    void OnTargetScaffoldPreparation(
        Guid jobId,
        TargetScaffoldPreparationFaultPoint faultPoint)
    {
    }

    void OnTargetScaffoldCleanup(
        Guid jobId,
        TargetScaffoldCleanupFaultPoint faultPoint)
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
