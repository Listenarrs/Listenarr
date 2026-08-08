using System.ComponentModel.DataAnnotations;
using Listenarr.Domain.Common;

namespace Listenarr.Domain.Audiobooks;

public enum LibraryDirectoryOwnershipState
{
    Owned,
    Retained,
    Removing,
    Removed,
    Conflict,
    Unavailable
}

public sealed class LibraryDirectoryOwnership
{
    public long Id { get; set; }

    [Required, MaxLength(2000)]
    public string Path { get; set; } = string.Empty;

    [Required, MaxLength(4096)]
    public string CanonicalPath { get; set; } = string.Empty;

    public FileSystemPathSyntax PathSyntax { get; set; }
    public FileSystemCaseSensitivity PathCaseSensitivity { get; set; }
    public FileSystemCaseSensitivityMode PathCaseSensitivityMode { get; set; }

    [Required, MaxLength(4096)]
    public string PathIdentityBoundary { get; set; } = string.Empty;

    [Required, MaxLength(160)]
    public string PathIdentityLookupKey { get; set; } = string.Empty;

    [MaxLength(160)]
    public string? PathOwnershipKey { get; set; }

    [Required, MaxLength(64)]
    public string OwnershipToken { get; set; } = string.Empty;

    public LibraryDirectoryOwnershipState State { get; set; } = LibraryDirectoryOwnershipState.Owned;

    [Required, MaxLength(64)]
    public string CreationWorkflow { get; set; } = string.Empty;

    public Guid? CreationOperationId { get; set; }
    public int? AudiobookId { get; set; }

    public int? ManagedRootFolderId { get; set; }

    public int? DirectoryObjectIdentityVersion { get; set; }

    [MaxLength(256)]
    public string? DirectoryObjectIdentity { get; set; }

    [MaxLength(1024)]
    public string? DirectoryObjectIdentityUnavailableReason { get; set; }

    [MaxLength(1024)]
    public string? StateReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<LibraryDirectoryOwnershipPathMigration> PathMigrations { get; set; } =
        new List<LibraryDirectoryOwnershipPathMigration>();
    public PathIdentitySnapshot GetIdentity() => new(
        PathSyntax,
        PathCaseSensitivity,
        PathCaseSensitivityMode,
        PathIdentityBoundary);
}
