using System.ComponentModel.DataAnnotations;

namespace Listenarr.Domain.Audiobooks;

public enum MoveScanHandoffStatus
{
    Pending,
    Claimed,
    Succeeded,
    Failed,
    Superseded
}

public sealed class MoveScanHandoff
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MoveJobId { get; set; }
    public MoveJob MoveJob { get; set; } = null!;
    public int AudiobookId { get; set; }
    [Required, MaxLength(2000)]
    public string TargetPath { get; set; } = string.Empty;
    public MoveScanHandoffStatus Status { get; set; } = MoveScanHandoffStatus.Pending;
    public int AttemptGeneration { get; set; }
    [MaxLength(200)]
    public string? LeaseOwner { get; set; }
    public int LeaseGeneration { get; set; }
    public DateTime? LeaseExpiresAt { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public Guid? ActiveScanJobId { get; set; }
    [MaxLength(4000)]
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum MoveCreatedDirectoryState
{
    Planned,
    Created,
    Retained,
    Removed
}

public sealed class MoveJobCreatedDirectory
{
    public long Id { get; set; }
    public Guid MoveJobId { get; set; }
    public MoveJob MoveJob { get; set; } = null!;
    [Required, MaxLength(2000)]
    public string Path { get; set; } = string.Empty;
    public MoveCreatedDirectoryState State { get; set; } = MoveCreatedDirectoryState.Planned;
    [MaxLength(512)]
    public string? DirectoryObjectIdentity { get; set; }
}
