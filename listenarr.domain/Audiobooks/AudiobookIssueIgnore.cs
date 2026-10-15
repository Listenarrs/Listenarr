using System.ComponentModel.DataAnnotations;

namespace Listenarr.Domain.Audiobooks;

/// <summary>
/// A book the user keeps out of the library issues (an incomplete Audible identity they accept).
/// Kept apart from the Audiobooks table; removed with the book.
/// </summary>
public sealed class AudiobookIssueIgnore
{
    [Key]
    public int AudiobookId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
