using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class EfFileMutationJournalStore
{
    private static void ValidateClaim(FileMutationJournalClaim claim)
    {
        if (claim.OperationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A file-mutation operation ID must not be empty.",
                nameof(claim));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(claim.SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(claim.DestinationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(
            claim.SourcePhysicalObjectIdentity);
        if (claim.SourceLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(claim));
        }
        if (claim.SourceSha256 is { Length: > 0 })
        {
            ValidateSha256(claim.SourceSha256, nameof(claim));
        }
        if (claim.AudiobookId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(claim));
        }
        if (claim.AudiobookFileId is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(claim));
        }
        if (claim.AudiobookFileId.HasValue && !claim.AudiobookId.HasValue)
        {
            throw new ArgumentException(
                "An audiobook-file mutation owner requires an audiobook owner.",
                nameof(claim));
        }
    }

    private async Task ValidateIdentityAsync(
        FileMutationJournal journal,
        FileMutationJournalClaim claim,
        string canonicalSource,
        string canonicalDestination,
        CancellationToken cancellationToken)
    {
        var sourcePathsMatch = await PathsMatchAsync(
            journal.SourcePath,
            canonicalSource,
            cancellationToken);
        var destinationPathsMatch = await PathsMatchAsync(
            journal.DestinationPath,
            canonicalDestination,
            cancellationToken);
        if (journal.ProtocolVersion
                != FileMutationProtocol.MarkerlessDatabaseState
            || journal.Action != claim.Action
            || !sourcePathsMatch
            || !destinationPathsMatch
            || !string.Equals(
                journal.SourcePhysicalObjectIdentity,
                claim.SourcePhysicalObjectIdentity,
                StringComparison.Ordinal)
            || journal.SourceLength != claim.SourceLength
            || !string.Equals(
                journal.SourceSha256,
                claim.SourceSha256,
                StringComparison.Ordinal)
            || journal.AudiobookId != claim.AudiobookId
            || journal.AudiobookFileId != claim.AudiobookFileId)
        {
            throw new InvalidOperationException(
                "The operation ID is already bound to another file-mutation identity.");
        }
    }

    private async Task<bool> PathsMatchAsync(
        string persistedPath,
        string requestedPath,
        CancellationToken cancellationToken)
    {
        var resolution = await _semanticsResolver.ResolveAsync(
            requestedPath,
            cancellationToken: cancellationToken);
        return resolution.State == PathIdentityState.Valid
            && FileSystemPathIdentity.AreEquivalent(
                persistedPath,
                requestedPath,
                resolution.Semantics);
    }
}
