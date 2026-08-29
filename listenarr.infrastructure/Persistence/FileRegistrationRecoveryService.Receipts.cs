using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence;

public sealed partial class FileRegistrationRecoveryService
{
    private async Task AppendDurableCompletedReceiptsAsync(
        int audiobookId,
        IReadOnlyCollection<string> requestedSourcePaths,
        ICollection<FileRegistrationRecoveryReceipt> receipts,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var completedJournals = await db.FileMutationJournals
            .AsNoTracking()
            .Where(RegistrationMoveOwnerPredicate)
            .Where(journal => journal.AudiobookId == audiobookId
                && journal.State == FileMutationJournalState.Completed)
            .OrderBy(journal => journal.CreatedAt)
            .ThenBy(journal => journal.OperationId)
            .ToListAsync(cancellationToken);
        if (completedJournals.Count == 0)
        {
            return;
        }

        var trackedFiles = await db.AudiobookFiles
            .AsNoTracking()
            .Where(file => file.AudiobookId == audiobookId)
            .ToListAsync(cancellationToken);
        var includedOperationIds = receipts
            .Select(receipt => receipt.OperationId)
            .ToHashSet();
        foreach (var journal in completedJournals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (includedOperationIds.Contains(journal.OperationId)
                || !requestedSourcePaths.Any(requestedPath =>
                    RequestedSourceMatchesJournal(
                        requestedPath,
                        journal.SourcePath)))
            {
                continue;
            }

            var matchingFiles = trackedFiles
                .Where(file =>
                    RegisteredPathMatches(file, journal.DestinationPath)
                    && RegisteredGenerationMatches(
                        file,
                        journal.TargetPhysicalObjectIdentity))
                .ToList();
            if (matchingFiles.Count != 1
                || !CompletedReceiptTargetIsStillPublished(
                    journal,
                    matchingFiles[0]))
            {
                continue;
            }

            receipts.Add(new FileRegistrationRecoveryReceipt(
                journal.OperationId,
                audiobookId,
                journal.SourcePath,
                journal.DestinationPath));
            includedOperationIds.Add(journal.OperationId);
        }
    }

    private static bool CompletedReceiptTargetIsStillPublished(
        FileMutationJournal journal,
        AudiobookFile trackedFile)
    {
        try
        {
            using var lease = PinnedAudiobookFileRegistrationLease.Open(
                journal.DestinationPath,
                trackedFile.PhysicalObjectIdentity);
            if (lease.ProbeCurrentPublication()
                != RegistrationPublicationMatchOutcome.Match)
            {
                return false;
            }

            using var stream = lease.OpenMetadataReadStream();
            if (stream.Length != journal.SourceLength)
            {
                return false;
            }
            if (string.IsNullOrWhiteSpace(journal.SourceSha256))
            {
                return true;
            }

            stream.Position = 0;
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            return string.Equals(
                hash,
                journal.SourceSha256,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is
            FileNotFoundException or DirectoryNotFoundException
                or IOException or UnauthorizedAccessException
                or ArgumentException or InvalidOperationException or NotSupportedException
                or PlatformNotSupportedException or PathTooLongException
                or System.ComponentModel.Win32Exception
                or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool RequestedSourceMatchesJournal(
        string requestedPath,
        string persistedSourcePath) =>
        string.Equals(
            requestedPath,
            persistedSourcePath,
            StringComparison.Ordinal);
}
