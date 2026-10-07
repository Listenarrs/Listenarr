using System.Security.Cryptography;
using Listenarr.Domain.Audiobooks.Enumerations;
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
            .Where(RegistrationPublicationOwnerPredicate)
            .Where(journal => journal.Action == FileAction.Move
                && journal.AudiobookId == audiobookId
                && (journal.State == FileMutationJournalState.Completed
                    || journal.State
                        == FileMutationJournalState.CompletedSourceRetained))
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
                    RegisteredPathMatches(file, journal.DestinationPath))
                .ToList();
            if (matchingFiles.Count != 1
                || !CompletedReceiptTargetIsStillPublished(journal))
            {
                continue;
            }

            var sourceRetained = journal.State
                == FileMutationJournalState.CompletedSourceRetained;
            receipts.Add(new FileRegistrationRecoveryReceipt(
                journal.OperationId,
                audiobookId,
                journal.SourcePath,
                journal.DestinationPath,
                SourceRetained: sourceRetained,
                SourceLength: sourceRetained ? journal.SourceLength : null,
                SourceSha256: sourceRetained ? journal.SourceSha256 : null));
            includedOperationIds.Add(journal.OperationId);
        }
    }

    private static bool CompletedReceiptTargetIsStillPublished(
        FileMutationJournal journal)
    {
        try
        {
            var parentPath = Path.GetDirectoryName(journal.DestinationPath);
            var fileName = Path.GetFileName(journal.DestinationPath);
            if (string.IsNullOrWhiteSpace(parentPath)
                || string.IsNullOrWhiteSpace(fileName)
                || string.IsNullOrWhiteSpace(journal.SourceSha256))
            {
                return false;
            }

            using var parent =
                PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                    parentPath,
                    createMissing: false);
            using var file = parent.TryOpenExistingFile(
                fileName,
                requireDeleteAccess: false);
            if (file == null
                || !parent.VisiblePathMatches()
                || !file.VisiblePathMatches()
                || !file.IsRegularFile())
            {
                return false;
            }

            using var stream = file.OpenReadStream(
                bufferSize: 81920,
                asynchronous: false);
            if (stream.Length != journal.SourceLength)
            {
                return false;
            }

            stream.Position = 0;
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            return file.VisiblePathMatches()
                && parent.VisiblePathMatches()
                && string.Equals(
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
