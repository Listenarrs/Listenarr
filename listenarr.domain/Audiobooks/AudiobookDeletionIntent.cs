using System.ComponentModel.DataAnnotations;

namespace Listenarr.Domain.Audiobooks;

public enum AudiobookDeletionIntentState
{
    Planned,
    FilesystemCleanupCompleted,
    Completed,
    NeedsAttention
}

/// <summary>
/// Durable intent for a delete-with-files workflow. The audiobook row remains
/// authoritative until filesystem cleanup reaches its persisted completion phase.
/// </summary>
public sealed class AudiobookDeletionIntent
{
    [Key]
    public Guid Id { get; set; }
    public int AudiobookId { get; set; }
    public bool DeleteFolder { get; set; }
    public AudiobookDeletionIntentState State { get; set; } =
        AudiobookDeletionIntentState.Planned;
    [MaxLength(2048)]
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
