using Listenarr.Application.Common.Exceptions;
using Listenarr.Domain.Common;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    private sealed record ExternalRecoveryConflict(
        string Code,
        string PublicMessage,
        string Detail);

    private async Task<HashSet<int>> FindMetadataRecoveryAudiobookIdsAsync(
        ListenArrDbContext db,
        Guid relocationId,
        CancellationToken cancellationToken)
    {
        var relocation = await db.RootFolderRelocations
            .AsNoTracking()
            .AsSplitQuery()
            .Include(candidate => candidate.SkippedItems)
            .Include(candidate => candidate.OwnershipPathMigrations)
                .ThenInclude(migration => migration.Ownership)
            .SingleAsync(candidate => candidate.Id == relocationId, cancellationToken);
        var audiobookIds = relocation.SkippedItems
            .Select(item => item.AudiobookId)
            .Concat(relocation.OwnershipPathMigrations
                .Where(migration => migration.Ownership.AudiobookId != null)
                .Select(migration => migration.Ownership.AudiobookId!.Value))
            .ToHashSet();

        FileSystemPathSemantics sourceSemantics;
        var firstOwnershipMigration = relocation.OwnershipPathMigrations.FirstOrDefault();
        if (firstOwnershipMigration != null)
        {
            sourceSemantics = new FileSystemPathSemantics(
                firstOwnershipMigration.SourcePathSyntax,
                firstOwnershipMigration.SourceCaseSensitivity);
        }
        else if (!TryResolvePersistedRelocationSourceSemantics(
            relocation,
            out sourceSemantics,
            out _))
        {
            return audiobookIds;
        }

        var audiobookRows = await db.Audiobooks
            .AsNoTracking()
            .Where(audiobook => audiobook.BasePath != null)
            .Select(audiobook => new
            {
                Audiobook = audiobook,
                StoredBasePath = EF.Property<string>(
                    audiobook,
                    nameof(Audiobook.BasePath))!
            })
            .ToListAsync(cancellationToken);
        var candidates = audiobookRows
            .Select(row => new AudiobookPathCandidate(
                row.Audiobook,
                row.StoredBasePath))
            .ToList();
        var allowContextualAmbiguousSourceSyntax =
            !FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                relocation.SourcePath,
                out _)
            && relocation.SourcePath.StartsWith("//", StringComparison.Ordinal)
            && FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                relocation.SourcePath,
                sourceSemantics.Syntax,
                out _);
        var (affected, invalid) = DiscoverAffectedAudiobooks(
            candidates,
            relocation.SourcePath,
            sourceSemantics,
            detectAmbiguousCaseMatches: false,
            allowContextualAmbiguousSourceSyntax);

        audiobookIds.UnionWith(affected
            .Concat(invalid)
            .Select(candidate => candidate.Audiobook.Id));
        return audiobookIds;
    }

    private async Task EnsureMetadataRecoveryHasNoExternalOwnerAsync(
        ListenArrDbContext db,
        Guid relocationId,
        CancellationToken cancellationToken)
    {
        var audiobookIds = await FindMetadataRecoveryAudiobookIdsAsync(
            db,
            relocationId,
            cancellationToken);
        var conflict = await FindExternalRecoveryConflictAsync(
            db,
            audiobookIds,
            cancellationToken);
        if (conflict != null)
        {
            throw new ApplicationConflictException(
                conflict.Code,
                conflict.PublicMessage);
        }
    }

    private static async Task<ExternalRecoveryConflict?>
        FindExternalRecoveryConflictAsync(
            ListenArrDbContext db,
            IReadOnlySet<int> audiobookIds,
            CancellationToken cancellationToken)
    {
        if (audiobookIds.Count == 0)
        {
            return null;
        }

        var renameOwnerId = await db.FileMutationJournals
            .AsNoTracking()
            .Where(journal => journal.AudiobookId != null
                && audiobookIds.Contains(journal.AudiobookId.Value)
                && journal.AudiobookFileId != null
                && journal.State != FileMutationJournalState.OwnerMetadataReconciled)
            .Select(journal => journal.AudiobookId)
            .FirstOrDefaultAsync(cancellationToken);
        if (renameOwnerId.HasValue)
        {
            return new ExternalRecoveryConflict(
                "rename_recovery_pending",
                "An interrupted file organize operation still owns an audiobook under this root. Complete restart recovery before changing the root folder path.",
                $"File rename recovery owns audiobook {renameOwnerId.Value} while this root-folder relocation is being prepared or retried.");
        }

        var deletionOwnerId = await db.AudiobookDeletionIntents
            .AsNoTracking()
            .Where(intent => audiobookIds.Contains(intent.AudiobookId)
                && intent.State != AudiobookDeletionIntentState.Completed)
            .Select(intent => (int?)intent.AudiobookId)
            .FirstOrDefaultAsync(cancellationToken);
        if (deletionOwnerId.HasValue)
        {
            return new ExternalRecoveryConflict(
                "delete_recovery_pending",
                "An audiobook deletion still owns an audiobook under this root. Complete or repair that deletion before changing the root folder path.",
                $"Audiobook deletion recovery owns audiobook {deletionOwnerId.Value} while this root-folder relocation is being prepared or retried.");
        }

        return null;
    }
}
