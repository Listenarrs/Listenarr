using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence;

public sealed partial class FileRenameRecoveryReconciler
{
    internal Func<Guid, Task>? AfterOwnerMetadataSaveBeforeCommitForTestAsync
    {
        get;
        set;
    }

    private async Task<GenerationMatchOutcome> CommitRecoveredOwnerMetadataAsync(
        ListenArrDbContext db,
        FileMutationJournal journal,
        string protectedPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(journal.SourceSha256))
        {
            return GenerationMatchOutcome.Mismatch;
        }

        PinnedDirectoryCreation.PinnedDirectoryAnchor? parent = null;
        PinnedDirectoryCreation.PinnedFileEntry? file = null;
        try
        {
            var fullPath = Path.GetFullPath(protectedPath);
            var parentPath = Path.GetDirectoryName(fullPath);
            var fileName = Path.GetFileName(fullPath);
            if (string.IsNullOrWhiteSpace(parentPath)
                || string.IsNullOrWhiteSpace(fileName))
            {
                return GenerationMatchOutcome.Mismatch;
            }

            parent = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                parentPath,
                createMissing: false);
            var openOutcome = parent.TryOpenExistingFileWithOutcome(
                fileName,
                requireDeleteAccess: false,
                out file);
            if (openOutcome == PinnedFileOpenOutcome.NotFound)
            {
                return GenerationMatchOutcome.Missing;
            }
            if (openOutcome == PinnedFileOpenOutcome.Unavailable)
            {
                return GenerationMatchOutcome.Unavailable;
            }
            if (file == null || !file.IsRegularFile())
            {
                return GenerationMatchOutcome.Mismatch;
            }

            var publicationMatch = await ProbePinnedRecoveryContentAsync(
                parent,
                file,
                journal,
                cancellationToken);
            if (publicationMatch != GenerationMatchOutcome.Match)
            {
                return publicationMatch;
            }

            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(cancellationToken)
                : null;

            journal.State = FileMutationJournalState.OwnerMetadataReconciled;
            journal.Error = null;
            journal.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(cancellationToken);

            if (AfterOwnerMetadataSaveBeforeCommitForTestAsync != null)
            {
                await AfterOwnerMetadataSaveBeforeCommitForTestAsync(
                    journal.OperationId);
            }

            publicationMatch = await ProbePinnedRecoveryContentAsync(
                parent,
                file,
                journal,
                CancellationToken.None);
            if (publicationMatch != GenerationMatchOutcome.Match)
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                return publicationMatch;
            }

            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            return GenerationMatchOutcome.Match;
        }
        catch (Exception exception) when (
            FileSystemSafety.IsProvenMissingPathException(exception))
        {
            return GenerationMatchOutcome.Missing;
        }
        catch (Exception exception) when (IsTransientRecoveryFilesystemException(exception))
        {
            return GenerationMatchOutcome.Unavailable;
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException
                or NotSupportedException or PathTooLongException
                or System.Security.SecurityException)
        {
            return GenerationMatchOutcome.Mismatch;
        }
        finally
        {
            file?.Dispose();
            parent?.Dispose();
        }
    }

    private static async Task<GenerationMatchOutcome>
        ProbePinnedRecoveryContentAsync(
            PinnedDirectoryCreation.PinnedDirectoryAnchor parent,
            PinnedDirectoryCreation.PinnedFileEntry file,
            FileMutationJournal journal,
            CancellationToken cancellationToken)
    {
        var fileVisibility = file.ProbeVisiblePathMatch();
        var parentVisibility = parent.ProbeVisiblePathMatch();
        if (fileVisibility == RegistrationPublicationMatchOutcome.Unavailable
            || parentVisibility == RegistrationPublicationMatchOutcome.Unavailable)
        {
            return GenerationMatchOutcome.Unavailable;
        }
        if (fileVisibility != RegistrationPublicationMatchOutcome.Match
            || parentVisibility != RegistrationPublicationMatchOutcome.Match)
        {
            return GenerationMatchOutcome.Mismatch;
        }

        return await file.MatchesAsync(
                journal.SourceLength,
                journal.SourceSha256!,
                cancellationToken)
            ? GenerationMatchOutcome.Match
            : GenerationMatchOutcome.Mismatch;
    }
}
