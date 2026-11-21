using System.ComponentModel.DataAnnotations;

namespace Listenarr.Domain.Downloads;

public static class VerifiedFileRenameProtocol
{
    public const int Current = 1;
}

public enum VerifiedFileRenameState
{
    Planned,
    TargetVerified,
    OwnerMetadataReconciled,
    SourceQuarantined,
    SourceDeleted,
    Completed,
    CompletedSourceRetained,
    RolledBack,
    NeedsAttention
}

/// <summary>
/// Durable owner-bound organize transaction for storage where persistent physical
/// generation identity is unavailable. Content hashes prove byte equality only;
/// destructive source retirement is permitted only while the original process still
/// holds the pinned source entry that was verified before publication.
/// </summary>
public sealed class VerifiedFileRenameJournal
{
    [Key]
    public Guid OperationId { get; set; }

    public Guid BatchId { get; set; }

    public int ProtocolVersion { get; set; } = VerifiedFileRenameProtocol.Current;

    public int AudiobookId { get; set; }

    public int AudiobookFileId { get; set; }

    public int ExpectedBatchMemberCount { get; set; }

    [Required, MaxLength(64)]
    public string ExpectedBatchManifestSha256 { get; set; } = string.Empty;

    [Required, MaxLength(4096)]
    public string SourcePath { get; set; } = string.Empty;

    [Required, MaxLength(4096)]
    public string DestinationPath { get; set; } = string.Empty;

    [Required, MaxLength(4096)]
    public string StagingPath { get; set; } = string.Empty;

    [Required, MaxLength(4096)]
    public string RetirementPath { get; set; } = string.Empty;

    public long SourceLength { get; set; }

    [Required, MaxLength(64)]
    public string SourceSha256 { get; set; } = string.Empty;

    public int SourceRootFolderId { get; set; }

    public int SourceStorageContractRevision { get; set; }

    public int DestinationRootFolderId { get; set; }

    public int DestinationStorageContractRevision { get; set; }

    public VerifiedFileRenameState State { get; set; } =
        VerifiedFileRenameState.Planned;

    [MaxLength(2048)]
    public string? Error { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
