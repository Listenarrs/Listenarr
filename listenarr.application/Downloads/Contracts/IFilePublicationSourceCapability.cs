namespace Listenarr.Application.Downloads.Contracts;

public enum FilePublicationSourceCapabilityFailureKind
{
    None = 0,
    Missing = 1,
    Unavailable = 2,
    Unsupported = 3
}

/// <summary>
/// Exact source evidence used to derive and later revalidate a durable file-publication
/// operation. Physical generation alone is insufficient because a downloader may rewrite
/// bytes in place without replacing the filesystem object.
/// </summary>
public readonly record struct FilePublicationSourceProof(
    string PhysicalObjectIdentity,
    long Length,
    string Sha256)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(PhysicalObjectIdentity);
        if (Length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Length));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(Sha256);
        if (Sha256.Length != 64 || !Sha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException(
                "The source publication SHA-256 must contain exactly 64 hexadecimal characters.",
                nameof(Sha256));
        }
    }
}

public readonly record struct FilePublicationSourceCapabilityResult(
    bool IsSupported,
    string? Reason = null,
    FilePublicationSourceCapabilityFailureKind FailureKind =
        FilePublicationSourceCapabilityFailureKind.None,
    FilePublicationSourceProof? SourceProof = null)
{
    public string? PhysicalObjectIdentity => SourceProof?.PhysicalObjectIdentity;

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
