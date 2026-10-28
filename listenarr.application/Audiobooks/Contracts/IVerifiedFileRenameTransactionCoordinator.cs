using System.Security.Cryptography;
using System.Text;

namespace Listenarr.Application.Audiobooks.Contracts;

public readonly record struct VerifiedFileRenameBatchMember(
    int AudiobookFileId,
    string SourcePath,
    string DestinationPath);

public readonly record struct VerifiedFileRenameBatchManifest(
    int ExpectedMemberCount,
    string ManifestSha256)
{
    public static VerifiedFileRenameBatchManifest Create(
        IEnumerable<VerifiedFileRenameBatchMember> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        var normalized = members
            .Select(member => new VerifiedFileRenameBatchMember(
                member.AudiobookFileId,
                Path.GetFullPath(member.SourcePath),
                Path.GetFullPath(member.DestinationPath)))
            .Distinct()
            .OrderBy(member => member.AudiobookFileId)
            .ThenBy(member => member.SourcePath, StringComparer.Ordinal)
            .ThenBy(member => member.DestinationPath, StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length == 0)
        {
            throw new ArgumentException(
                "A verified file-rename batch requires at least one member.",
                nameof(members));
        }

        var payload = string.Join(
            '\0',
            normalized.Select(member =>
                $"{member.AudiobookFileId}\0{member.SourcePath}\0{member.DestinationPath}"));
        return new VerifiedFileRenameBatchManifest(
            normalized.Length,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))));
    }

    public void Validate()
    {
        if (ExpectedMemberCount <= 0)
        {
            throw new InvalidOperationException(
                "A verified file-rename batch must contain at least one member.");
        }
        if (ManifestSha256.Length != 64 || !ManifestSha256.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException(
                "A verified file-rename manifest must contain a SHA-256 digest.");
        }
    }
}

public sealed record VerifiedFileRenamePreparationResult(
    bool Success,
    IVerifiedFileRenameLease? Lease = null,
    string? Error = null);

public enum VerifiedFileRenameRetirementOutcome
{
    Completed,
    SourceRetained,
    NeedsAttention
}

public interface IVerifiedFileRenameLease : IAsyncDisposable
{
    Guid OperationId { get; }

    Task<bool> RollBackAsync(CancellationToken cancellationToken = default);

    Task<VerifiedFileRenameRetirementOutcome> CompleteSourceRetirementAsync(
        CancellationToken cancellationToken = default);
}

public interface IVerifiedFileRenameTransactionCoordinator
{
    Task<VerifiedFileRenamePreparationResult> PrepareAsync(
        string source,
        string destination,
        Guid operationId,
        Guid batchId,
        VerifiedFileRenameBatchManifest batchManifest,
        int audiobookId,
        int audiobookFileId,
        FilePublicationSourceProof sourceProof,
        CancellationToken cancellationToken = default);
}
