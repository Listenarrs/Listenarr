using Listenarr.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence;

internal sealed partial class VerifiedFileRenameRecoveryService
{
    private async Task<bool> ValidateCommittedBatchAsync(
        ListenArrDbContext db,
        VerifiedFileRenameJournal journal,
        CancellationToken cancellationToken)
    {
        var batch = await db.VerifiedFileRenameJournals
            .AsNoTracking()
            .Where(candidate => candidate.BatchId == journal.BatchId)
            .ToListAsync(cancellationToken);
        if (batch.Count == 0
            || batch.Count != journal.ExpectedBatchMemberCount
            || batch.Any(candidate =>
                candidate.ProtocolVersion != VerifiedFileRenameProtocol.Current
                || candidate.AudiobookId != journal.AudiobookId
                || candidate.ExpectedBatchMemberCount != batch.Count
                || !string.Equals(
                    candidate.ExpectedBatchManifestSha256,
                    journal.ExpectedBatchManifestSha256,
                    StringComparison.OrdinalIgnoreCase)
                || candidate.State is VerifiedFileRenameState.Planned
                    or VerifiedFileRenameState.TargetVerified
                    or VerifiedFileRenameState.RolledBack
                    or VerifiedFileRenameState.NeedsAttention))
        {
            return false;
        }

        var manifest = VerifiedFileRenameBatchManifest.Create(
            batch.Select(candidate => new VerifiedFileRenameBatchMember(
                candidate.AudiobookFileId,
                candidate.SourcePath,
                candidate.DestinationPath)));
        return manifest.ExpectedMemberCount == batch.Count
            && string.Equals(
                manifest.ManifestSha256,
                journal.ExpectedBatchManifestSha256,
                StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool?> OwnerPointsToAsync(
        ListenArrDbContext db,
        VerifiedFileRenameJournal journal,
        string expectedPath,
        CancellationToken cancellationToken)
    {
        var audiobook = await db.Audiobooks
            .AsNoTracking()
            .Include(candidate => candidate.Files)
            .SingleOrDefaultAsync(
                candidate => candidate.Id == journal.AudiobookId,
                cancellationToken);
        if (audiobook == null)
        {
            return false;
        }

        if (journal.AudiobookFileId == 0)
        {
            if (string.IsNullOrWhiteSpace(audiobook.FilePath))
            {
                return false;
            }

            try
            {
                var currentIdentity = await identityResolver.ResolveAsync(
                    audiobook,
                    audiobook.FilePath,
                    cancellationToken);
                var legacyExpectedIdentity = await identityResolver.ResolveAsync(
                    audiobook,
                    expectedPath,
                    cancellationToken);
                if (currentIdentity.State == PathIdentityState.Unavailable
                    || legacyExpectedIdentity.State == PathIdentityState.Unavailable)
                {
                    return null;
                }
                if (currentIdentity.State != PathIdentityState.Valid
                    || legacyExpectedIdentity.State != PathIdentityState.Valid
                    || string.IsNullOrWhiteSpace(currentIdentity.OwnershipKey)
                    || string.IsNullOrWhiteSpace(legacyExpectedIdentity.OwnershipKey))
                {
                    return false;
                }

                return string.Equals(
                    currentIdentity.OwnershipKey,
                    legacyExpectedIdentity.OwnershipKey,
                    StringComparison.Ordinal);
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException
                    or InvalidOperationException or NotSupportedException)
            {
                return null;
            }
        }

        var trackedFile = audiobook.Files?.SingleOrDefault(
            file => file.Id == journal.AudiobookFileId);
        if (trackedFile == null
            || string.IsNullOrWhiteSpace(trackedFile.PathOwnershipKey))
        {
            return false;
        }

        AudiobookFilePathIdentity expectedIdentity;
        try
        {
            expectedIdentity = await identityResolver.ResolveAsync(
                audiobook,
                expectedPath,
                cancellationToken);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
        if (expectedIdentity.State == PathIdentityState.Unavailable)
        {
            return null;
        }
        if (expectedIdentity.State != PathIdentityState.Valid
            || string.IsNullOrWhiteSpace(expectedIdentity.OwnershipKey))
        {
            return false;
        }

        return string.Equals(
            trackedFile.PathOwnershipKey,
            expectedIdentity.OwnershipKey,
            StringComparison.Ordinal);
    }
}
