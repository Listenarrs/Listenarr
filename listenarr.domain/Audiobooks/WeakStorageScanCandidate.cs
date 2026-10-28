using System.ComponentModel.DataAnnotations;

namespace Listenarr.Domain.Audiobooks;

public sealed class WeakStorageScanCandidate
{
    [Key]
    public Guid Id { get; set; }
    public Guid ScanToken { get; set; }
    public int AudiobookId { get; set; }
    public int AudiobookFileId { get; set; }
    [Required, MaxLength(4096)]
    public string ExpectedStoredPath { get; set; } = string.Empty;
    [Required, MaxLength(4096)]
    public string ExpectedResolvedPath { get; set; } = string.Empty;
    [MaxLength(512)]
    public string? ExpectedPhysicalObjectIdentity { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConfirmedAt { get; set; }
}
