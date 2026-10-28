using System.ComponentModel.DataAnnotations;
using Listenarr.Domain.Common;

namespace Listenarr.Domain.Audiobooks;

public enum RootFolderRelocationMode
{
    Relocate,
    MetadataOnly
}

public enum RootFolderRelocationStatus
{
    Pending,
    Running,
    NeedsAttention,
    Completed,
    Failed
}

public enum RootFolderRelocationSkipReasonCode
{
    Unknown,
    InvalidStoredPath,
    SourceSemanticsUnavailable,
    TargetPathInvalid,
    TargetIdentityCollision,
    TargetIdentityUnresolvedConflict
}

public enum TargetIdentityEnrollmentState
{
    Authorized,
    Unavailable,
    NotRequired
}

public enum RootFolderRelocationCreatedDirectoryState
{
    Planned,
    Created,
    Retained,
    Removed
}

public sealed class RootFolderRelocation
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();
    public int? RootFolderId { get; set; }
    public int? ActiveRootFolderId { get; set; }
    public RootFolder? RootFolder { get; set; }
    [Required, MaxLength(1000)]
    public string SourcePath { get; set; } = string.Empty;
    public FileSystemCaseSensitivityMode SourceCaseSensitivityMode { get; set; } = FileSystemCaseSensitivityMode.Auto;
    [Required, MaxLength(1000)]
    public string TargetPath { get; set; } = string.Empty;
    public RootFolderRelocationMode Mode { get; set; } = RootFolderRelocationMode.Relocate;
    public RootFolderRelocationStatus Status { get; set; } = RootFolderRelocationStatus.Pending;
    public bool DeleteEmptySource { get; set; } = true;
    [Required, MaxLength(200)]
    public string DesiredName { get; set; } = string.Empty;
    public bool DesiredIsDefault { get; set; }
    public FileSystemCaseSensitivityMode TargetCaseSensitivityMode { get; set; } = FileSystemCaseSensitivityMode.Auto;
    public TargetIdentityEnrollmentState TargetIdentityEnrollmentState { get; set; } =
        TargetIdentityEnrollmentState.Authorized;
    public int? TargetDirectoryObjectIdentityVersion { get; set; }
    [MaxLength(256)]
    public string? TargetDirectoryObjectIdentity { get; set; }
    [MaxLength(1024)]
    public string? TargetDirectoryObjectIdentityUnavailableReason { get; set; }
    public int TotalJobs { get; set; }
    public int CompletedJobs { get; set; }
    [MaxLength(4000)]
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public ICollection<MoveJob> MoveJobs { get; set; } = new List<MoveJob>();
    public ICollection<RootFolderRelocationSkippedItem> SkippedItems { get; set; } = new List<RootFolderRelocationSkippedItem>();
    public ICollection<LibraryDirectoryOwnershipPathMigration> OwnershipPathMigrations { get; set; } =
        new List<LibraryDirectoryOwnershipPathMigration>();
    public ICollection<RootFolderRelocationCreatedDirectory> CreatedDirectories { get; set; } =
        new List<RootFolderRelocationCreatedDirectory>();
}

public sealed class RootFolderRelocationSkippedItem
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RelocationId { get; set; }
    public int AudiobookId { get; set; }
    [Required, MaxLength(4000)]
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public RootFolderRelocation Relocation { get; set; } = null!;
}

public sealed class LibraryDirectoryOwnershipPathMigration
{
    [Key]
    public long Id { get; set; }
    public long OwnershipId { get; set; }
    public LibraryDirectoryOwnership Ownership { get; set; } = null!;
    public Guid RelocationId { get; set; }
    public RootFolderRelocation Relocation { get; set; } = null!;
    [Required, MaxLength(4096)]
    public string SourceCanonicalPath { get; set; } = string.Empty;
    public FileSystemPathSyntax SourcePathSyntax { get; set; }
    public FileSystemCaseSensitivity SourceCaseSensitivity { get; set; }
    public FileSystemCaseSensitivityMode SourceCaseSensitivityMode { get; set; }
    [Required, MaxLength(4096)]
    public string SourceIdentityBoundary { get; set; } = string.Empty;
    [Required, MaxLength(160)]
    public string SourceIdentityLookupKey { get; set; } = string.Empty;
    [Required, MaxLength(160)]
    public string SourceOwnershipKey { get; set; } = string.Empty;
    [Required, MaxLength(4096)]
    public string TargetCanonicalPath { get; set; } = string.Empty;
    public FileSystemPathSyntax TargetPathSyntax { get; set; }
    public FileSystemCaseSensitivity TargetCaseSensitivity { get; set; }
    public FileSystemCaseSensitivityMode TargetCaseSensitivityMode { get; set; }
    [Required, MaxLength(4096)]
    public string TargetIdentityBoundary { get; set; } = string.Empty;
    [Required, MaxLength(160)]
    public string TargetIdentityLookupKey { get; set; } = string.Empty;
    [Required, MaxLength(160)]
    public string TargetOwnershipKey { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class RootFolderRelocationCreatedDirectory
{
    [Key]
    public long Id { get; set; }
    public Guid RelocationId { get; set; }
    public RootFolderRelocation Relocation { get; set; } = null!;
    [Required, MaxLength(4096)]
    public string CanonicalPath { get; set; } = string.Empty;
    [Required, MaxLength(64)]
    public string OwnershipToken { get; set; } = string.Empty;
    public RootFolderRelocationCreatedDirectoryState State { get; set; } =
        RootFolderRelocationCreatedDirectoryState.Planned;
    public int? DirectoryObjectIdentityVersion { get; set; }
    [MaxLength(256)]
    public string? DirectoryObjectIdentity { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
