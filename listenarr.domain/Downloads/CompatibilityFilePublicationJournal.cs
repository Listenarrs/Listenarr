using System.ComponentModel.DataAnnotations;

namespace Listenarr.Domain.Downloads;

public static class CompatibilityFilePublicationProtocol
{
    public const int Current = 1;
}

public enum CompatibilityFilePublicationState
{
    Planned,
    TargetVerified,
    RegistrationCommitted,
    Completed,
    NeedsAttention
}

public enum CompatibilitySourceDisposition
{
    Retained = 0,
    Unchanged = 1
}

/// <summary>
/// Non-destructive recovery state for publication on storage that cannot expose
/// durable object generations. This journal never authorizes deletion or overwrite.
/// </summary>
public sealed class CompatibilityFilePublicationJournal
{
    [Key]
    public Guid OperationId { get; set; }
    public int ProtocolVersion { get; set; } =
        CompatibilityFilePublicationProtocol.Current;
    public FileAction RequestedAction { get; set; }
    public FileAction EffectiveAction { get; set; } = FileAction.Copy;
    public CompatibilitySourceDisposition SourceDisposition { get; set; } =
        CompatibilitySourceDisposition.Retained;
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
    [MaxLength(2048)]
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
