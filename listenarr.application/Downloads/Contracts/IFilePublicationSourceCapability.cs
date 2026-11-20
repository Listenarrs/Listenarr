namespace Listenarr.Application.Downloads.Contracts;

public enum FilePublicationSourceCapabilityFailureKind
{
    None = 0,
    Missing = 1,
    Unavailable = 2,
    Unsupported = 3
}

/// <summary>
/// Durable content evidence for a file publication. This proof may cross
/// process and mount boundaries because it describes bytes, not kernel identity.
/// </summary>
public readonly record struct FileContentProof(
    long Length,
    string Sha256,
    string Algorithm = "SHA-256",
    int Version = 1)
{
    public void Validate()
    {
        if (Length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Length));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(Sha256);
        if (Sha256.Length != 64 || !Sha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException(
                "The file content SHA-256 must contain exactly 64 hexadecimal characters.",
                nameof(Sha256));
        }

        if (!string.Equals(Algorithm, "SHA-256", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Only SHA-256 content proofs are currently supported.",
                nameof(Algorithm));
        }

        if (Version != 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Version),
                "Unsupported file content proof version.");
        }
    }
}

/// <summary>
/// Source evidence carried into one publication attempt. ContentProof is the
/// durable authority. OperationLocalPhysicalObjectIdentity is optional and may
/// only be used to strengthen a live, pinned operation; it is not restart authority.
/// </summary>
public readonly record struct FilePublicationSourceProof
{
    public FilePublicationSourceProof(
        FileContentProof contentProof,
        string? operationLocalPhysicalObjectIdentity = null)
    {
        contentProof.Validate();
        ContentProof = contentProof;
        OperationLocalPhysicalObjectIdentity =
            string.IsNullOrWhiteSpace(operationLocalPhysicalObjectIdentity)
                ? null
                : operationLocalPhysicalObjectIdentity;
    }

    // Transitional constructor for existing callers. The authority argument is
    // intentionally ignored for durable semantics; it only controls whether the
    // supplied physical observation is retained as an operation-local diagnostic.
    public FilePublicationSourceProof(
        string physicalObjectIdentity,
        long length,
        string sha256,
        FilePublicationSourceAuthority authority =
            FilePublicationSourceAuthority.DurableObjectIdentity)
        : this(
            new FileContentProof(length, sha256),
            authority == FilePublicationSourceAuthority.DurableObjectIdentity
                ? physicalObjectIdentity
                : null)
    {
    }

    public FileContentProof ContentProof { get; }

    public string? OperationLocalPhysicalObjectIdentity { get; }

    public long Length => ContentProof.Length;

    public string Sha256 => ContentProof.Sha256;

    public string PhysicalObjectIdentity =>
        OperationLocalPhysicalObjectIdentity
        ?? $"content-only:{Sha256}";

    public bool HasDurablePhysicalObjectIdentity =>
        OperationLocalPhysicalObjectIdentity != null;

    public FilePublicationSourceAuthority Authority =>
        OperationLocalPhysicalObjectIdentity == null
            ? FilePublicationSourceAuthority.ContentOnly
            : FilePublicationSourceAuthority.DurableObjectIdentity;

    public void Validate() => ContentProof.Validate();
}

/// <summary>
/// Legacy compatibility surface while callers migrate away from using physical
/// identity as a top-level publication decision.
/// </summary>
public enum FilePublicationSourceAuthority
{
    DurableObjectIdentity = 0,
    ContentOnly = 1
}

public readonly record struct FilePublicationSourceCapabilityResult(
    bool IsSupported,
    string? Reason = null,
    FilePublicationSourceCapabilityFailureKind FailureKind =
        FilePublicationSourceCapabilityFailureKind.None,
    FilePublicationSourceProof? SourceProof = null)
{
    public string? PhysicalObjectIdentity =>
        SourceProof?.OperationLocalPhysicalObjectIdentity;

    public static FilePublicationSourceCapabilityResult SupportedForProof(
        FilePublicationSourceProof sourceProof)
    {
        sourceProof.Validate();
        return new(true, SourceProof: sourceProof);
    }

    public static FilePublicationSourceCapabilityResult Unsupported(
        string reason,
        FilePublicationSourceCapabilityFailureKind failureKind =
            FilePublicationSourceCapabilityFailureKind.Unsupported) =>
        new(false, reason, failureKind);
}

public interface IFilePublicationSourceCapability
{
    Task<FilePublicationSourceCapabilityResult> CheckAsync(
        string sourcePath,
        CancellationToken cancellationToken = default);
}
