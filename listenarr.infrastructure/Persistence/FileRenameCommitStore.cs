using Listenarr.Domain.Audiobooks.Enumerations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Listenarr.Infrastructure.Persistence;

/// <summary>
/// Commits tracked audiobook path changes and the terminal/owner-commit state of
/// their owner-bound rename journals through the same scoped DbContext.
/// </summary>
public sealed class FileRenameCommitStore(
    ListenArrDbContext dbContext,
    TimeProvider timeProvider) : IFileRenameCommitStore
{
    internal Action? AfterSaveBeforeTargetRevalidationForTest { get; set; }

    public async Task CommitOwnerMetadataAsync(
        int audiobookId,
        IReadOnlyCollection<Guid> operationIds,
        CancellationToken cancellationToken = default)
    {
        if (audiobookId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(audiobookId));
        }
        ArgumentNullException.ThrowIfNull(operationIds);
        var distinctIds = operationIds
            .Where(operationId => operationId != Guid.Empty)
            .Distinct()
            .ToArray();
        if (distinctIds.Length != operationIds.Count)
        {
            throw new InvalidOperationException(
                "A rename commit contains an empty or duplicate file-mutation operation ID.");
        }

        var journals = new List<FileMutationJournal>();
        var verifiedJournals = new List<VerifiedFileRenameJournal>();
        var targetLeases = new List<PinnedAudiobookFileRegistrationLease>();
        var verifiedLeases = new List<VerifiedRenameCommitLease>();
        var originalJournalState = new Dictionary<Guid, (
            FileMutationJournalState State,
            string? Error,
            DateTime UpdatedAt)>();
        var originalVerifiedState = new Dictionary<Guid, (
            VerifiedFileRenameState State,
            string? Error,
            DateTime UpdatedAt)>();
        IDbContextTransaction? ownedTransaction = null;
        try
        {
            if (dbContext.Database.IsRelational())
            {
                if (dbContext.Database.CurrentTransaction != null)
                {
                    throw new InvalidOperationException(
                        "Rename owner-metadata commit must own its database transaction so filesystem proof cannot outlive the commit boundary.");
                }

                ownedTransaction = await dbContext.Database.BeginTransactionAsync(
                    cancellationToken);
            }

            if (distinctIds.Length > 0)
            {
                journals = await dbContext.FileMutationJournals
                    .Where(journal => distinctIds.Contains(journal.OperationId))
                    .ToListAsync(cancellationToken);
                verifiedJournals = await dbContext.VerifiedFileRenameJournals
                    .Where(journal => distinctIds.Contains(journal.OperationId))
                    .ToListAsync(cancellationToken);
                foreach (var verifiedJournal in verifiedJournals)
                {
                    // The live verified-rename lease advances rollback/attention state
                    // through its own DbContext. This scoped commit context may still
                    // be tracking the journal from an earlier failed owner commit, so
                    // refresh before deciding which durable state is authoritative.
                    await dbContext.Entry(verifiedJournal).ReloadAsync(cancellationToken);
                }
                if (journals.Count + verifiedJournals.Count != distinctIds.Length
                    || journals.Select(journal => journal.OperationId)
                        .Intersect(verifiedJournals.Select(journal => journal.OperationId))
                        .Any())
                {
                    throw new InvalidOperationException(
                        "One or more owner-bound rename journals are missing or ambiguous before metadata commit.");
                }
                if (journals.Count > 0 && verifiedJournals.Count > 0)
                {
                    throw new InvalidOperationException(
                        "One organize owner commit cannot mix durable-generation and verified weak-storage rename protocols.");
                }

                if (journals.Count > 0)
                {
                    PrepareDurableRenameCommit(
                        audiobookId,
                        journals,
                        targetLeases,
                        originalJournalState);
                }
                else
                {
                    await PrepareVerifiedRenameCommitAsync(
                        audiobookId,
                        distinctIds,
                        verifiedJournals,
                        verifiedLeases,
                        originalVerifiedState,
                        cancellationToken);
                }
            }

            EnsureTargetsStillMatch(targetLeases);
            await EnsureVerifiedEntriesStillMatchAsync(
                verifiedLeases,
                cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            AfterSaveBeforeTargetRevalidationForTest?.Invoke();
            EnsureTargetsStillMatch(targetLeases);
            await EnsureVerifiedEntriesStillMatchAsync(
                verifiedLeases,
                cancellationToken);
            if (ownedTransaction != null)
            {
                await ownedTransaction.CommitAsync(cancellationToken);
            }
        }
        catch
        {
            if (ownedTransaction != null)
            {
                await ownedTransaction.RollbackAsync(CancellationToken.None);
            }
            foreach (var journal in journals)
            {
                if (!originalJournalState.TryGetValue(journal.OperationId, out var original))
                {
                    continue;
                }

                journal.State = original.State;
                journal.Error = original.Error;
                journal.UpdatedAt = original.UpdatedAt;
            }
            foreach (var journal in verifiedJournals)
            {
                if (!originalVerifiedState.TryGetValue(
                        journal.OperationId,
                        out var original))
                {
                    continue;
                }

                journal.State = original.State;
                journal.Error = original.Error;
                journal.UpdatedAt = original.UpdatedAt;
            }
            throw;
        }
        finally
        {
            if (ownedTransaction != null)
            {
                await ownedTransaction.DisposeAsync();
            }
            foreach (var targetLease in targetLeases)
            {
                targetLease.Dispose();
            }
            foreach (var verifiedLease in verifiedLeases)
            {
                verifiedLease.Dispose();
            }
        }
    }

    private void PrepareDurableRenameCommit(
        int audiobookId,
        IReadOnlyCollection<FileMutationJournal> journals,
        ICollection<PinnedAudiobookFileRegistrationLease> targetLeases,
        IDictionary<Guid, (
            FileMutationJournalState State,
            string? Error,
            DateTime UpdatedAt)> originalJournalState)
    {
        foreach (var journal in journals)
        {
            if (journal.Action != FileAction.Move
                || journal.AudiobookId != audiobookId
                || !journal.AudiobookFileId.HasValue)
            {
                throw new InvalidOperationException(
                    "A rename journal is not a move bound to the audiobook whose metadata is being committed.");
            }
            if (journal.State != FileMutationJournalState.Completed)
            {
                throw new InvalidOperationException(
                    "A rename journal has not completed its filesystem mutation before metadata commit.");
            }
            if (string.IsNullOrWhiteSpace(journal.TargetPhysicalObjectIdentity))
            {
                throw new InvalidOperationException(
                    "A completed rename journal has no persisted target physical generation.");
            }

            originalJournalState[journal.OperationId] =
                (journal.State, journal.Error, journal.UpdatedAt);
            var targetLease = PinnedAudiobookFileRegistrationLease.Open(
                journal.DestinationPath,
                journal.TargetPhysicalObjectIdentity);
            targetLeases.Add(targetLease);
            if (targetLease.ProbeCurrentPublication()
                != RegistrationPublicationMatchOutcome.Match)
            {
                throw new InvalidOperationException(
                    "A completed rename target is not currently the journaled physical generation.");
            }

            journal.State = FileMutationJournalState.OwnerMetadataReconciled;
            journal.Error = null;
            journal.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        }
    }

    private async Task PrepareVerifiedRenameCommitAsync(
        int audiobookId,
        IReadOnlyCollection<Guid> distinctIds,
        IReadOnlyList<VerifiedFileRenameJournal> journals,
        ICollection<VerifiedRenameCommitLease> leases,
        IDictionary<Guid, (
            VerifiedFileRenameState State,
            string? Error,
            DateTime UpdatedAt)> originalState,
        CancellationToken cancellationToken)
    {
        if (journals.Count == 0)
        {
            return;
        }
        if (journals.All(journal => journal.State == VerifiedFileRenameState.RolledBack))
        {
            if (journals.Any(journal =>
                    journal.ProtocolVersion != VerifiedFileRenameProtocol.Current
                    || journal.AudiobookId != audiobookId))
            {
                throw new InvalidOperationException(
                    "A rolled-back verified organize journal is not bound to the expected audiobook/protocol.");
            }
            return;
        }
        if (journals.Any(journal =>
                journal.ProtocolVersion != VerifiedFileRenameProtocol.Current
                || journal.AudiobookId != audiobookId
                || journal.AudiobookFileId < 0
                || journal.State != VerifiedFileRenameState.TargetVerified))
        {
            throw new InvalidOperationException(
                "Every verified organize journal must be target-verified and owner-bound before metadata commit.");
        }

        var batchIds = journals.Select(journal => journal.BatchId).Distinct().ToArray();
        if (batchIds.Length != 1 || batchIds[0] == Guid.Empty)
        {
            throw new InvalidOperationException(
                "A verified organize owner commit must contain exactly one sealed batch.");
        }

        var fullBatch = await dbContext.VerifiedFileRenameJournals
            .Where(journal => journal.BatchId == batchIds[0])
            .OrderBy(journal => journal.AudiobookFileId)
            .ThenBy(journal => journal.SourcePath)
            .ToListAsync(cancellationToken);
        if (fullBatch.Count != journals[0].ExpectedBatchMemberCount
            || fullBatch.Count != distinctIds.Count
            || !fullBatch.Select(journal => journal.OperationId)
                .ToHashSet()
                .SetEquals(distinctIds)
            || fullBatch.Any(journal =>
                journal.AudiobookId != audiobookId
                || journal.State != VerifiedFileRenameState.TargetVerified
                || journal.ExpectedBatchMemberCount != fullBatch.Count
                || !string.Equals(
                    journal.ExpectedBatchManifestSha256,
                    journals[0].ExpectedBatchManifestSha256,
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "The verified organize batch is incomplete or inconsistent before owner metadata commit.");
        }

        var manifest = VerifiedFileRenameBatchManifest.Create(
            fullBatch.Select(journal => new VerifiedFileRenameBatchMember(
                journal.AudiobookFileId,
                journal.SourcePath,
                journal.DestinationPath)));
        manifest.Validate();
        if (manifest.ExpectedMemberCount != fullBatch.Count
            || !string.Equals(
                manifest.ManifestSha256,
                journals[0].ExpectedBatchManifestSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The persisted verified organize batch manifest does not match its journal members.");
        }

        var rootIds = fullBatch
            .SelectMany(journal => new[]
            {
                journal.SourceRootFolderId,
                journal.DestinationRootFolderId
            })
            .Distinct()
            .ToArray();
        var roots = await dbContext.RootFolders
            .AsNoTracking()
            .Where(root => rootIds.Contains(root.Id))
            .ToDictionaryAsync(root => root.Id, cancellationToken);
        foreach (var journal in fullBatch)
        {
            if (!roots.TryGetValue(journal.SourceRootFolderId, out var sourceRoot)
                || !roots.TryGetValue(
                    journal.DestinationRootFolderId,
                    out var destinationRoot)
                || sourceRoot.StorageContractRevision
                    != journal.SourceStorageContractRevision
                || destinationRoot.StorageContractRevision
                    != journal.DestinationStorageContractRevision)
            {
                throw new InvalidOperationException(
                    "A verified organize root storage contract changed before owner metadata commit.");
            }
        }

        foreach (var journal in journals)
        {
            originalState[journal.OperationId] =
                (journal.State, journal.Error, journal.UpdatedAt);
            var lease = VerifiedRenameCommitLease.Open(journal);
            await lease.EnsureMatchesAsync(cancellationToken);
            leases.Add(lease);
            journal.State = VerifiedFileRenameState.OwnerMetadataReconciled;
            journal.Error = null;
            journal.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        }
    }

    private static void EnsureTargetsStillMatch(
        IReadOnlyCollection<PinnedAudiobookFileRegistrationLease> targetLeases)
    {
        foreach (var targetLease in targetLeases)
        {
            var match = targetLease.ProbeCurrentPublication();
            if (match == RegistrationPublicationMatchOutcome.Unavailable)
            {
                throw new IOException(
                    "A completed rename target is temporarily unavailable during owner-metadata commit.");
            }
            if (match != RegistrationPublicationMatchOutcome.Match)
            {
                throw new InvalidOperationException(
                    "A completed rename target changed before owner metadata could be committed.");
            }
        }
    }

    private static async Task EnsureVerifiedEntriesStillMatchAsync(
        IReadOnlyCollection<VerifiedRenameCommitLease> leases,
        CancellationToken cancellationToken)
    {
        foreach (var lease in leases)
        {
            await lease.EnsureMatchesAsync(cancellationToken);
        }
    }

    private sealed class VerifiedRenameCommitLease : IDisposable
    {
        private readonly PinnedDirectoryCreation.PinnedDirectoryAnchor _sourceParent;
        private readonly PinnedDirectoryCreation.PinnedDirectoryAnchor _destinationParent;
        private readonly PinnedDirectoryCreation.PinnedFileEntry _source;
        private readonly PinnedDirectoryCreation.PinnedFileEntry _target;
        private readonly long _length;
        private readonly string _sha256;
        private bool _disposed;

        private VerifiedRenameCommitLease(
            PinnedDirectoryCreation.PinnedDirectoryAnchor sourceParent,
            PinnedDirectoryCreation.PinnedDirectoryAnchor destinationParent,
            PinnedDirectoryCreation.PinnedFileEntry source,
            PinnedDirectoryCreation.PinnedFileEntry target,
            long length,
            string sha256)
        {
            _sourceParent = sourceParent;
            _destinationParent = destinationParent;
            _source = source;
            _target = target;
            _length = length;
            _sha256 = sha256;
        }

        public static VerifiedRenameCommitLease Open(
            VerifiedFileRenameJournal journal)
        {
            var sourceParentPath = Path.GetDirectoryName(journal.SourcePath)
                ?? throw new InvalidOperationException(
                    "The verified organize source has no parent directory.");
            var destinationParentPath = Path.GetDirectoryName(journal.DestinationPath)
                ?? throw new InvalidOperationException(
                    "The verified organize destination has no parent directory.");
            var sourceParent = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(
                sourceParentPath);
            PinnedDirectoryCreation.PinnedDirectoryAnchor? destinationParent = null;
            PinnedDirectoryCreation.PinnedFileEntry? source = null;
            PinnedDirectoryCreation.PinnedFileEntry? target = null;
            try
            {
                destinationParent = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(
                    destinationParentPath);
                source = sourceParent.OpenExistingFileForStableRead(
                    Path.GetFileName(journal.SourcePath));
                target = destinationParent.OpenExistingFileForStableRead(
                    Path.GetFileName(journal.DestinationPath));
                var lease = new VerifiedRenameCommitLease(
                    sourceParent,
                    destinationParent,
                    source,
                    target,
                    journal.SourceLength,
                    journal.SourceSha256);
                sourceParent = null!;
                destinationParent = null;
                source = null;
                target = null;
                return lease;
            }
            finally
            {
                target?.Dispose();
                source?.Dispose();
                destinationParent?.Dispose();
                sourceParent?.Dispose();
            }
        }

        public async Task EnsureMatchesAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_source.ProbeVisiblePathMatch()
                    != RegistrationPublicationMatchOutcome.Match
                || _target.ProbeVisiblePathMatch()
                    != RegistrationPublicationMatchOutcome.Match
                || !await _source.MatchesAsync(
                    _length,
                    _sha256,
                    cancellationToken)
                || !await _target.MatchesAsync(
                    _length,
                    _sha256,
                    cancellationToken))
            {
                throw new InvalidOperationException(
                    "A verified organize source or target changed during owner-metadata commit.");
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _target.Dispose();
            _source.Dispose();
            _destinationParent.Dispose();
            _sourceParent.Dispose();
        }
    }
}
