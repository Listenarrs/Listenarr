namespace Listenarr.Application.Downloads.Contracts;

public enum FilePublicationExecutionMode
{
    Durable = 0,
    AdditiveCopyRetainSource = 1,
    Blocked = 2,
    CompatibilityCopyVerifiedCleanup = 3
}

public enum FilePublicationSourceDisposition
{
    NotApplicable = 0,
    Retained = 1,
    Retired = 2,
    Unchanged = 3
}

public sealed record FilePublicationPlan(
    FileAction RequestedAction,
    FileAction EffectiveAction,
    FilePublicationExecutionMode Mode,
    FilePublicationSourceDisposition SourceDisposition,
    string? ReasonCode = null,
    string? Message = null,
    Guid? CompatibilityBatchId = null,
    CompatibilityCleanupOwner CleanupOwner = CompatibilityCleanupOwner.None,
    int? SourceRootFolderId = null,
    int? SourcePolicyRevision = null,
    int? DestinationRootFolderId = null,
    int? DestinationPolicyRevision = null,
    int? SourceStorageContractRevision = null,
    int? DestinationStorageContractRevision = null)
{
    public bool IsAllowed => Mode != FilePublicationExecutionMode.Blocked;

    public static FilePublicationPlan Durable(FileAction action) =>
        new(
            action,
            action,
            FilePublicationExecutionMode.Durable,
            action == FileAction.Move
                ? FilePublicationSourceDisposition.Retired
                : FilePublicationSourceDisposition.Unchanged);

    public static FilePublicationPlan Additive(FileAction requestedAction) =>
        new(
            requestedAction,
            FileAction.Copy,
            FilePublicationExecutionMode.AdditiveCopyRetainSource,
            FilePublicationSourceDisposition.Retained,
            "durable_identity_unavailable",
            requestedAction == FileAction.Move
                ? "The destination was copied successfully, but the source was retained because exact source retirement cannot be proven on this storage."
                : "The file was copied using compatibility publication because durable filesystem identity is unavailable.");

    public static FilePublicationPlan VerifiedCleanup(
        Guid batchId,
        CompatibilityCleanupOwner cleanupOwner,
        int? sourceRootFolderId,
        int? sourcePolicyRevision,
        int destinationRootFolderId,
        int destinationPolicyRevision,
        int? sourceStorageContractRevision,
        int destinationStorageContractRevision) =>
        new(
            FileAction.Move,
            FileAction.Copy,
            FilePublicationExecutionMode.CompatibilityCopyVerifiedCleanup,
            FilePublicationSourceDisposition.Retained,
            "verified_cleanup_pending",
            cleanupOwner == CompatibilityCleanupOwner.DownloadClient
                ? "The destination will be verified before source cleanup is delegated to the download client."
                : "The destination will be verified before protected source cleanup begins.",
            batchId,
            cleanupOwner,
            sourceRootFolderId,
            sourcePolicyRevision,
            destinationRootFolderId,
            destinationPolicyRevision,
            sourceStorageContractRevision,
            destinationStorageContractRevision);

    public static FilePublicationPlan Blocked(
        FileAction requestedAction,
        string reasonCode,
        string message) =>
        new(
            requestedAction,
            requestedAction,
            FilePublicationExecutionMode.Blocked,
            FilePublicationSourceDisposition.Unchanged,
            reasonCode,
            message);
}

public interface IFilePublicationCapabilityResolver
{
    Task<FilePublicationPlan> ResolveAsync(
        FileAction requestedAction,
        string source,
        string destination,
        FilePublicationSourceProof sourceProof,
        CancellationToken cancellationToken = default,
        Guid? compatibilityBatchId = null,
        CompatibilityCleanupOwner cleanupOwner = CompatibilityCleanupOwner.None);
}
