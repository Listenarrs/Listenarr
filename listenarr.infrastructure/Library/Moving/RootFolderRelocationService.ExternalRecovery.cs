using Listenarr.Application.Common.Exceptions;
using Listenarr.Domain.Audiobooks.Enumerations;
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
        if (conflict == null)
        {
            var relocation = await db.RootFolderRelocations
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == relocationId, cancellationToken);
            conflict = await FindRegistrationBoundaryRecoveryConflictAsync(
                relocation.SourcePath,
                relocation.SourceCaseSensitivityMode,
                relocation.TargetPath,
                relocation.TargetCaseSensitivityMode,
                cancellationToken);
        }
        if (conflict != null)
        {
            throw new ApplicationConflictException(
                conflict.Code,
                conflict.PublicMessage);
        }
    }

    private async Task<ExternalRecoveryConflict?>
        FindRegistrationBoundaryRecoveryConflictAsync(
            ListenArrDbContext db,
            Guid relocationId,
            CancellationToken cancellationToken)
    {
        var boundary = await db.RootFolderRelocations
            .AsNoTracking()
            .Where(relocation => relocation.Id == relocationId)
            .Select(relocation => new
            {
                relocation.SourcePath,
                relocation.SourceCaseSensitivityMode,
                relocation.TargetPath,
                relocation.TargetCaseSensitivityMode
            })
            .SingleAsync(cancellationToken);
        return await FindRegistrationBoundaryRecoveryConflictAsync(
            boundary.SourcePath,
            boundary.SourceCaseSensitivityMode,
            boundary.TargetPath,
            boundary.TargetCaseSensitivityMode,
            cancellationToken);
    }

    private async Task<ExternalRecoveryConflict?>
        FindRegistrationBoundaryRecoveryConflictAsync(
            string sourcePath,
            FileSystemCaseSensitivityMode sourceMode,
            string targetPath,
            FileSystemCaseSensitivityMode targetMode,
            CancellationToken cancellationToken)
    {
        if (_fileRegistrationRecoveryProbe == null)
        {
            return null;
        }

        var sourceSemantics = await ResolveRecoveryBoundarySemanticsAsync(
            sourcePath,
            sourceMode,
            cancellationToken);
        if (sourceSemantics.HasValue
            && await _fileRegistrationRecoveryProbe.HasBlockingBoundaryAsync(
                sourcePath,
                sourceSemantics.Value,
                cancellationToken))
        {
            return RegistrationBoundaryConflict(sourcePath);
        }

        var targetSemantics = await ResolveRecoveryBoundarySemanticsAsync(
            targetPath,
            targetMode,
            cancellationToken);
        if (targetSemantics.HasValue
            && await _fileRegistrationRecoveryProbe.HasBlockingBoundaryAsync(
                targetPath,
                targetSemantics.Value,
                cancellationToken))
        {
            return RegistrationBoundaryConflict(targetPath);
        }

        return null;
    }

    private async Task<FileSystemPathSemantics?> ResolveRecoveryBoundarySemanticsAsync(
        string path,
        FileSystemCaseSensitivityMode mode,
        CancellationToken cancellationToken)
    {
        if (FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                path,
                out var canonicalPath,
                out _))
        {
            try
            {
                var resolution = await semanticsResolver.ResolveAsync(
                    canonicalPath,
                    mode,
                    cancellationToken);
                if (resolution.State == PathIdentityState.Valid)
                {
                    return resolution.Semantics;
                }
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException or ArgumentException
                    or InvalidOperationException or NotSupportedException
                    or PathTooLongException or System.Security.SecurityException)
            {
                // Fall back to persisted path geometry below. This check grants no
                // filesystem authority; an insensitive comparison is deliberately
                // conservative when live Auto semantics are unavailable.
            }
        }

        if (!FileSystemPathIdentity.TryDetectAbsoluteSyntax(path, out var syntax))
        {
            return null;
        }

        var sensitivity = mode switch
        {
            FileSystemCaseSensitivityMode.Sensitive => FileSystemCaseSensitivity.Sensitive,
            FileSystemCaseSensitivityMode.Insensitive => FileSystemCaseSensitivity.Insensitive,
            _ => FileSystemCaseSensitivity.Insensitive
        };
        return new FileSystemPathSemantics(syntax, sensitivity);
    }

    private async Task<string> ValidateStartRecoveryBoundariesAsync(
        ListenArrDbContext db,
        int rootFolderId,
        RootFolder root,
        StartSourcePathSemantics sourcePathSemantics,
        string targetPath,
        FileSystemSemanticsResolution targetResolution,
        CancellationToken cancellationToken)
    {
        var sourceSemantics =
            sourcePathSemantics.MetadataSourcePathSemantics?.Semantics
            ?? sourcePathSemantics.SourceOperationSemantics;
        if (_fileRegistrationRecoveryProbe != null
            && ((sourceSemantics.HasValue
                    && await _fileRegistrationRecoveryProbe.HasBlockingBoundaryAsync(
                        root.Path,
                        sourceSemantics.Value,
                        cancellationToken))
                || await _fileRegistrationRecoveryProbe.HasBlockingBoundaryAsync(
                    targetPath,
                    targetResolution.Semantics,
                    cancellationToken)))
        {
            var conflict = RegistrationBoundaryConflict(root.Path);
            throw new RootFolderPathChangeRejectedException(
                conflict.Code,
                conflict.PublicMessage,
                conflict.Detail);
        }

        var targetIdentityKey = FileSystemPathIdentity.CreateKey(
            "root",
            targetPath,
            targetResolution.Semantics);
        await EnsureNoTargetBoundaryConflictAsync(
            db,
            rootFolderId,
            targetPath,
            targetIdentityKey,
            targetResolution.Semantics,
            cancellationToken);
        return targetIdentityKey;
    }

    private static ExternalRecoveryConflict RegistrationBoundaryConflict(string path) =>
        new(
            "registration_recovery_pending",
            "An unresolved file publication still owns a path under this root. Complete file-registration recovery before changing the root folder path.",
            $"File-registration recovery touches relocation boundary {LogRedaction.SanitizeFilePath(path)}.");

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

        var registrationOwnerId = await db.FileMutationJournals
            .AsNoTracking()
            .Where(journal => journal.AudiobookId != null
                && audiobookIds.Contains(journal.AudiobookId.Value)
                && journal.AudiobookFileId == null
                && journal.Action == FileAction.Move
                && journal.State != FileMutationJournalState.Completed)
            .Select(journal => journal.AudiobookId)
            .FirstOrDefaultAsync(cancellationToken);
        if (registrationOwnerId.HasValue)
        {
            return new ExternalRecoveryConflict(
                "registration_recovery_pending",
                "A committed file import still owns source-cleanup state for an audiobook under this root. Complete that recovery before changing the root folder path.",
                $"File-registration recovery owns audiobook {registrationOwnerId.Value} while this root-folder relocation is being prepared or retried.");
        }

        var renameOwnerId = await db.FileMutationJournals
            .AsNoTracking()
            .Where(journal => journal.AudiobookId != null
                && audiobookIds.Contains(journal.AudiobookId.Value)
                && journal.AudiobookFileId != null
                && (journal.AudiobookFileId == FileMutationOwner.CompanionFile
                    || journal.AudiobookFileId
                        == FileMutationOwner.RegistrationCompanionFile
                    ? journal.State != FileMutationJournalState.Completed
                    : journal.State != FileMutationJournalState.OwnerMetadataReconciled))
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
