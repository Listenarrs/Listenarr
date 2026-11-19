using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Persistence;

internal interface IVerifiedFileRenameRecoveryService
{
    Task ReconcileAsync(CancellationToken cancellationToken = default);
}

internal sealed partial class VerifiedFileRenameRecoveryService(
    IDbContextFactory<ListenArrDbContext> dbContextFactory,
    IAudiobookFilePathIdentityResolver identityResolver,
    TimeProvider timeProvider,
    ILogger<VerifiedFileRenameRecoveryService> logger)
    : IVerifiedFileRenameRecoveryService
{
    internal Action<string>? AfterContentReadForTest { get; set; }
    private enum ContentProbeOutcome
    {
        Match,
        Missing,
        Unavailable,
        Mismatch
    }

    public async Task ReconcileAsync(
        CancellationToken cancellationToken = default)
    {
        await using var readDb = await dbContextFactory.CreateDbContextAsync(
            cancellationToken);
        var operationIds = await readDb.VerifiedFileRenameJournals
            .AsNoTracking()
            .Where(journal =>
                journal.State != VerifiedFileRenameState.Completed
                && journal.State != VerifiedFileRenameState.CompletedSourceRetained
                && journal.State != VerifiedFileRenameState.RolledBack
                && journal.State != VerifiedFileRenameState.NeedsAttention)
            .OrderBy(journal => journal.CreatedAt)
            .ThenBy(journal => journal.OperationId)
            .Select(journal => journal.OperationId)
            .ToListAsync(cancellationToken);

        foreach (var operationId in operationIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ReconcileOperationAsync(operationId, cancellationToken);
        }
    }

    private async Task ReconcileOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(
            cancellationToken);
        var journal = await db.VerifiedFileRenameJournals
            .SingleOrDefaultAsync(
                candidate => candidate.OperationId == operationId,
                cancellationToken);
        if (journal == null || IsTerminal(journal.State))
        {
            return;
        }
        if (journal.ProtocolVersion != VerifiedFileRenameProtocol.Current)
        {
            MarkNeedsAttention(
                journal,
                "The interrupted verified organize journal uses an unsupported recovery protocol.");
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var roots = await ResolveJournalRootsAsync(db, journal, cancellationToken);
        if (roots == null) return;
        var sourceProbe = await ProbeContentAsync(
            journal.SourcePath,
            roots.Value.Source,
            journal.SourceLength,
            journal.SourceSha256,
            cancellationToken);
        var targetProbe = await ProbeContentAsync(
            journal.DestinationPath,
            roots.Value.Destination,
            journal.SourceLength,
            journal.SourceSha256,
            cancellationToken);
        var stagingProbe = await ProbePathExistsAsync(
            journal.StagingPath,
            roots.Value.Destination,
            cancellationToken);
        var retirementProbe = await ProbePathExistsAsync(
            journal.RetirementPath,
            roots.Value.Source,
            cancellationToken);

        if (sourceProbe == ContentProbeOutcome.Unavailable
            || targetProbe == ContentProbeOutcome.Unavailable
            || stagingProbe == ContentProbeOutcome.Unavailable
            || retirementProbe == ContentProbeOutcome.Unavailable)
        {
            logger.LogWarning(
                "Verified organize recovery {OperationId} is waiting for storage availability",
                journal.OperationId);
            return;
        }

        switch (journal.State)
        {
            case VerifiedFileRenameState.Planned:
                {
                    var ownerAtSource = await OwnerPointsToAsync(
                        db,
                        journal,
                        journal.SourcePath,
                        cancellationToken);
                    if (ownerAtSource is null)
                    {
                        return;
                    }
                    if (ownerAtSource == true
                        && sourceProbe == ContentProbeOutcome.Match
                        && targetProbe == ContentProbeOutcome.Missing
                        && stagingProbe == ContentProbeOutcome.Missing
                        && retirementProbe == ContentProbeOutcome.Missing)
                    {
                        journal.State = VerifiedFileRenameState.RolledBack;
                        journal.Error =
                            "Interrupted verified organize recovered before target publication; the original source remained authoritative.";
                    }
                    else
                    {
                        MarkNeedsAttention(
                            journal,
                            "Verified organize was interrupted before target verification. Source/target artifacts were preserved because restart recovery cannot safely infer publication intent from the surviving paths and content alone.");
                    }
                    break;
                }
            case VerifiedFileRenameState.TargetVerified:
                {
                    var ownerAtSource = await OwnerPointsToAsync(
                        db,
                        journal,
                        journal.SourcePath,
                        cancellationToken);
                    if (ownerAtSource is null)
                    {
                        return;
                    }
                    if (ownerAtSource == true
                        && sourceProbe == ContentProbeOutcome.Match
                        && targetProbe == ContentProbeOutcome.Missing
                        && stagingProbe == ContentProbeOutcome.Missing
                        && retirementProbe == ContentProbeOutcome.Missing)
                    {
                        journal.State = VerifiedFileRenameState.RolledBack;
                        journal.Error =
                            "The verified target was no longer present after restart; the still-authoritative source was retained.";
                    }
                    else
                    {
                        MarkNeedsAttention(
                            journal,
                            "A verified organize target was published before owner metadata committed. The original source and published artifacts were preserved for operator review; restart recovery will not delete either path by hash equality alone.");
                    }
                    break;
                }
            case VerifiedFileRenameState.OwnerMetadataReconciled:
                {
                    if (!await ValidateCommittedBatchAsync(
                            db,
                            journal,
                            cancellationToken))
                    {
                        MarkNeedsAttention(
                            journal,
                            "The committed verified organize batch manifest is incomplete or inconsistent.");
                        break;
                    }

                    var ownerAtDestination = await OwnerPointsToAsync(
                        db,
                        journal,
                        journal.DestinationPath,
                        cancellationToken);
                    if (ownerAtDestination is null)
                    {
                        return;
                    }
                    if (ownerAtDestination != true
                        || targetProbe != ContentProbeOutcome.Match)
                    {
                        MarkNeedsAttention(
                            journal,
                            "Verified organize owner metadata committed, but the tracked destination can no longer be proven by path identity and content.");
                        break;
                    }

                    if (retirementProbe != ContentProbeOutcome.Missing)
                    {
                        MarkNeedsAttention(
                            journal,
                            "Verified organize restart found an operation-owned retirement artifact before its quarantine state was durably recorded. The artifact was preserved for operator review.");
                        break;
                    }

                    if (sourceProbe == ContentProbeOutcome.Missing)
                    {
                        journal.State = VerifiedFileRenameState.Completed;
                        journal.Error = null;
                    }
                    else
                    {
                        journal.State = VerifiedFileRenameState.CompletedSourceRetained;
                        journal.Error =
                            "Owner metadata committed before restart; the old source was retained because restart recovery cannot recreate source-retirement authority.";
                    }
                    break;
                }
            case VerifiedFileRenameState.SourceQuarantined:
                {
                    if (!await ValidateCommittedBatchAsync(
                            db,
                            journal,
                            cancellationToken))
                    {
                        MarkNeedsAttention(
                            journal,
                            "The quarantined verified organize batch manifest is incomplete or inconsistent.");
                        break;
                    }

                    var ownerAtDestination = await OwnerPointsToAsync(
                        db,
                        journal,
                        journal.DestinationPath,
                        cancellationToken);
                    if (ownerAtDestination is null)
                    {
                        return;
                    }
                    if (ownerAtDestination != true
                        || targetProbe != ContentProbeOutcome.Match)
                    {
                        MarkNeedsAttention(
                            journal,
                            "Verified organize source quarantine was recorded, but its committed destination can no longer be proven. The retirement artifact was preserved.");
                        break;
                    }

                    if (retirementProbe != ContentProbeOutcome.Missing)
                    {
                        MarkNeedsAttention(
                            journal,
                            "Verified organize source retirement was interrupted while the exact source was in the operation-owned retirement namespace. Restart recovery preserved it for operator repair.");
                        break;
                    }

                    if (sourceProbe == ContentProbeOutcome.Missing)
                    {
                        journal.State = VerifiedFileRenameState.Completed;
                        journal.Error = null;
                    }
                    else
                    {
                        journal.State = VerifiedFileRenameState.CompletedSourceRetained;
                        journal.Error =
                            "The original source path is present after an interrupted retirement. It was treated as retained/new content and was not deleted during restart recovery.";
                    }
                    break;
                }
            case VerifiedFileRenameState.SourceDeleted:
                {
                    if (!await ValidateCommittedBatchAsync(
                            db,
                            journal,
                            cancellationToken))
                    {
                        MarkNeedsAttention(
                            journal,
                            "The source-deleted verified organize batch manifest is incomplete or inconsistent.");
                        break;
                    }

                    var ownerAtDestination = await OwnerPointsToAsync(
                        db,
                        journal,
                        journal.DestinationPath,
                        cancellationToken);
                    if (ownerAtDestination is null)
                    {
                        return;
                    }
                    if (ownerAtDestination != true
                        || targetProbe != ContentProbeOutcome.Match)
                    {
                        MarkNeedsAttention(
                            journal,
                            "Verified organize source retirement was recorded, but its committed destination can no longer be proven.");
                        break;
                    }
                    if (retirementProbe != ContentProbeOutcome.Missing)
                    {
                        MarkNeedsAttention(
                            journal,
                            "Verified organize source deletion was recorded, but an operation-owned retirement artifact is still visible. It was preserved for operator review.");
                        break;
                    }

                    if (sourceProbe == ContentProbeOutcome.Missing)
                    {
                        journal.State = VerifiedFileRenameState.Completed;
                        journal.Error = null;
                    }
                    else
                    {
                        journal.State = VerifiedFileRenameState.CompletedSourceRetained;
                        journal.Error =
                            "The old source path is present after source retirement was recorded. It was treated as a new/unowned path and was not deleted during restart recovery.";
                    }
                    break;
                }
        }

        journal.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        if (journal.State == VerifiedFileRenameState.CompletedSourceRetained)
        {
            logger.LogWarning(
                "Verified organize recovery {OperationId} completed with the old source retained: {Reason}",
                journal.OperationId,
                journal.Error);
        }
        else if (journal.State == VerifiedFileRenameState.NeedsAttention)
        {
            logger.LogWarning(
                "Verified organize recovery {OperationId} requires attention: {Reason}",
                journal.OperationId,
                journal.Error);
        }
    }

    private async Task<ContentProbeOutcome> ProbeContentAsync(
        string path,
        RootFolder? root,
        long expectedLength,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var parentPath = Path.GetDirectoryName(fullPath);
            var fileName = Path.GetFileName(fullPath);
            if (string.IsNullOrWhiteSpace(parentPath)
                || string.IsNullOrWhiteSpace(fileName))
            {
                return ContentProbeOutcome.Mismatch;
            }

            using var parent = PinnedDirectoryCreation.OpenPinnedConfiguredHierarchy(
                root, parentPath,
                createMissing: false);
            var outcome = parent.TryOpenExistingFileWithOutcome(
                fileName,
                requireDeleteAccess: false,
                out var openedEntry);
            using var entry = openedEntry;
            if (outcome == PinnedFileOpenOutcome.NotFound) return ContentProbeOutcome.Missing;
            if (outcome == PinnedFileOpenOutcome.Unavailable) return ContentProbeOutcome.Unavailable;
            if (entry == null || !entry.IsRegularFile()) return ContentProbeOutcome.Mismatch;
            var visibility = ProbeReadVisibility(parent, entry);
            if (visibility != ContentProbeOutcome.Match) return visibility;
            var matches = await entry.MatchesAsync(expectedLength, expectedSha256, cancellationToken);
            AfterContentReadForTest?.Invoke(path);
            visibility = ProbeReadVisibility(parent, entry);
            return visibility != ContentProbeOutcome.Match ? visibility
                : matches ? ContentProbeOutcome.Match : ContentProbeOutcome.Mismatch;
        }
        catch (Exception exception) when (
            FileSystemSafety.IsProvenMissingPathException(exception))
        {
            return ContentProbeOutcome.Missing;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or InvalidOperationException or NotSupportedException
                or PathTooLongException or System.Security.SecurityException
                or System.ComponentModel.Win32Exception)
        {
            return ContentProbeOutcome.Unavailable;
        }
    }

    private static async Task<ContentProbeOutcome> ProbePathExistsAsync(
        string path,
        RootFolder? root,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var fullPath = Path.GetFullPath(path);
            var parentPath = Path.GetDirectoryName(fullPath);
            var fileName = Path.GetFileName(fullPath);
            if (string.IsNullOrWhiteSpace(parentPath)
                || string.IsNullOrWhiteSpace(fileName))
            {
                return ContentProbeOutcome.Mismatch;
            }

            using var parent = PinnedDirectoryCreation.OpenPinnedConfiguredHierarchy(
                root, parentPath,
                createMissing: false);
            var outcome = parent.TryOpenExistingFileWithOutcome(
                fileName,
                requireDeleteAccess: false,
                out var openedEntry);
            using var entry = openedEntry;
            return outcome switch
            {
                PinnedFileOpenOutcome.NotFound => ContentProbeOutcome.Missing,
                PinnedFileOpenOutcome.Unavailable => ContentProbeOutcome.Unavailable,
                _ => ContentProbeOutcome.Match
            };
        }
        catch (Exception exception) when (
            FileSystemSafety.IsProvenMissingPathException(exception))
        {
            return ContentProbeOutcome.Missing;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or InvalidOperationException or NotSupportedException
                or PathTooLongException or System.Security.SecurityException
                or System.ComponentModel.Win32Exception)
        {
            return ContentProbeOutcome.Unavailable;
        }
    }

    private static bool IsTerminal(VerifiedFileRenameState state) =>
        state is VerifiedFileRenameState.Completed
            or VerifiedFileRenameState.CompletedSourceRetained
            or VerifiedFileRenameState.RolledBack
            or VerifiedFileRenameState.NeedsAttention;

    private static void MarkNeedsAttention(
        VerifiedFileRenameJournal journal,
        string reason)
    {
        journal.State = VerifiedFileRenameState.NeedsAttention;
        journal.Error = reason;
    }
}
