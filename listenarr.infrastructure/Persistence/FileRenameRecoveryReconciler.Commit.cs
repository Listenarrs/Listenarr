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
        string expectedPhysicalObjectIdentity,
        CancellationToken cancellationToken)
    {
        PinnedAudiobookFileRegistrationLease? lease;
        try
        {
            lease = PinnedAudiobookFileRegistrationLease.Open(
                protectedPath,
                expectedPhysicalObjectIdentity);
        }
        catch (FileNotFoundException)
        {
            return GenerationMatchOutcome.Mismatch;
        }
        catch (DirectoryNotFoundException)
        {
            return GenerationMatchOutcome.Mismatch;
        }
        catch (System.ComponentModel.Win32Exception exception) when (
            OperatingSystem.IsWindows()
                ? exception.NativeErrorCode is 2 or 3
                : exception.NativeErrorCode == 2)
        {
            return GenerationMatchOutcome.Mismatch;
        }
        catch (Exception exception) when (IsTransientRecoveryFilesystemException(exception))
        {
            return GenerationMatchOutcome.Unavailable;
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException
                or NotSupportedException or PathTooLongException)
        {
            return GenerationMatchOutcome.Mismatch;
        }

        using (lease)
        {
            var publicationMatch = lease.ProbeCurrentPublication();
            if (publicationMatch != RegistrationPublicationMatchOutcome.Match)
            {
                return ToGenerationMatchOutcome(publicationMatch);
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

            publicationMatch = lease.ProbeCurrentPublication();
            if (publicationMatch != RegistrationPublicationMatchOutcome.Match)
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                return ToGenerationMatchOutcome(publicationMatch);
            }

            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            return GenerationMatchOutcome.Match;
        }
    }

    private static GenerationMatchOutcome ToGenerationMatchOutcome(
        RegistrationPublicationMatchOutcome outcome) =>
        outcome switch
        {
            RegistrationPublicationMatchOutcome.Match => GenerationMatchOutcome.Match,
            RegistrationPublicationMatchOutcome.Unavailable => GenerationMatchOutcome.Unavailable,
            _ => GenerationMatchOutcome.Mismatch
        };
}
