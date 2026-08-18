using System.ComponentModel.DataAnnotations;

namespace Listenarr.Domain.Downloads;

public static class FileMutationProtocol
{
    public const int MarkerlessDatabaseState = 1;
    public const int ParentGenerationMarkerlessDatabaseState = 2;
    public const int Current = ParentGenerationMarkerlessDatabaseState;

    public static bool IsCurrent(int version) => version == Current;
}

public static class FileMutationOwner
{
    // AudiobookFileId is an owner discriminator as well as an optional row ID:
    // null = registration publication, 0 = legacy Audiobook.FilePath,
    // positive = tracked AudiobookFile, -1 = owner-bound companion file.
    public const int CompanionFile = -1;

    public static bool IsCompanionFile(int? audiobookFileId) =>
        audiobookFileId == CompanionFile;
}

public enum FileMutationJournalState
{
    Planned,
    TargetIdentityPersisted,
    TargetVerified,
    RegistrationCommitted,
    SourceDeletionAuthorized,
    SourceDeleted,
    Completed,
    OwnerMetadataReconciled,
    NeedsAttention
}

/// <summary>
/// Durable coordination for a single final-name file mutation. Filesystem paths
/// contain only user content; recovery authority is persisted in SQLite.
/// </summary>
public sealed class FileMutationJournal
{
    [Key]
    public Guid OperationId { get; set; }
    public int ProtocolVersion { get; set; } = FileMutationProtocol.Current;
    public FileAction Action { get; set; }
    [Required, MaxLength(4096)]
    public string SourcePath { get; set; } = string.Empty;
    [Required, MaxLength(4096)]
    public string DestinationPath { get; set; } = string.Empty;
    [Required, MaxLength(512)]
    public string SourceParentDirectoryObjectIdentity { get; set; } = string.Empty;
    [Required, MaxLength(512)]
    public string DestinationParentDirectoryObjectIdentity { get; set; } = string.Empty;
    [Required, MaxLength(512)]
    public string SourcePhysicalObjectIdentity { get; set; } = string.Empty;
    [MaxLength(512)]
    public string? TargetPhysicalObjectIdentity { get; set; }
    public long SourceLength { get; set; }
    [MaxLength(64)]
    public string? SourceSha256 { get; set; }
    public FileMutationJournalState State { get; set; } =
        FileMutationJournalState.Planned;
    public int? AudiobookId { get; set; }
    public int? AudiobookFileId { get; set; }
    [MaxLength(2048)]
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
