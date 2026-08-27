using System.ComponentModel.DataAnnotations;

namespace Listenarr.Domain.Downloads;

public static class CompatibilityFilePublicationProtocol
{
    public const int RetainOnly = 1;
    public const int Current = 2;
}

public enum CompatibilityFilePublicationState
{
    Planned = 0,
    TargetVerified = 1,
    RegistrationCommitted = 2,
    Completed = 3,
    NeedsAttention = 4,
    SourceDeleteAuthorized = 5,
    SourceQuarantinePlanned = 6,
    SourceQuarantined = 7,
    SourceDeleted = 8
}

public enum CompatibilitySourceDisposition
{
    Retained = 0,
    Unchanged = 1,
    RetiredByListenarr = 2,
    DeferredToDownloadClient = 3,
    PartialNeedsAttention = 4
}

public enum CompatibilityCleanupOwner
{
    None = 0,
    Listenarr = 1,
    DownloadClient = 2
}

/// <summary>
/// Recovery state for publication on storage that cannot expose durable object
/// generations. Protocol v1 is retain-only; protocol v2 can authorize verified,
/// policy-gated source cleanup after registration commits for the entire batch.
/// </summary>
public sealed class CompatibilityFilePublicationJournal
{
    [Key]
    public Guid OperationId { get; set; }
    public Guid? BatchId { get; set; }
    public int ProtocolVersion { get; set; } =
        CompatibilityFilePublicationProtocol.Current;
    public FileAction RequestedAction { get; set; }
    public FileAction EffectiveAction { get; set; } = FileAction.Copy;
    public CompatibilitySourceDisposition SourceDisposition { get; set; } =
        CompatibilitySourceDisposition.Retained;
    public CompatibilityCleanupOwner CleanupOwner { get; set; } =
        CompatibilityCleanupOwner.None;
    public int? SourceRootFolderId { get; set; }
    public int? SourcePolicyRevision { get; set; }
    public int? SourceStorageContractRevision { get; set; }
    public int? DestinationRootFolderId { get; set; }
    public int? DestinationPolicyRevision { get; set; }
    public int? DestinationStorageContractRevision { get; set; }
    [Required, MaxLength(4096)]
    public string SourcePath { get; set; } = string.Empty;
    [Required, MaxLength(4096)]
    public string DestinationPath { get; set; } = string.Empty;
    public long SourceLength { get; set; }
    [Required, MaxLength(64)]
    public string SourceSha256 { get; set; } = string.Empty;
    public long? TargetLength { get; set; }
    [MaxLength(64)]
    public string? TargetSha256 { get; set; }
    public CompatibilityFilePublicationState State { get; set; } =
        CompatibilityFilePublicationState.Planned;
    public int? AudiobookId { get; set; }
    public bool IsCompanionFile { get; set; }
    [MaxLength(4096)]
    public string? QuarantinePath { get; set; }
    [MaxLength(2048)]
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
