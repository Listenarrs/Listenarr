/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using System.Threading.Channels;
using Listenarr.Application.Common;
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;


namespace Listenarr.Application.Audiobooks.Jobs
{
    public partial class MoveQueueService : IMoveQueueService
    {
        private readonly Channel<MoveJob> _channel = Channel.CreateUnbounded<MoveJob>();
        private bool _identityKeysReconciled;
        private readonly SemaphoreSlim _identityReconciliationGate = new(1, 1);
        private readonly object _publicationGateSync = new();
        private readonly Dictionary<Guid, PublicationGateEntry> _publicationGates = [];
        private readonly ILogger<MoveQueueService> _logger;
        private readonly IMoveQueuePersistence _persistence;
        private readonly IHubBroadcaster _hubBroadcaster;
        private readonly TimeProvider _timeProvider;
        private readonly IRootFolderRelocationService _relocationService;
        private readonly IFileSystemSemanticsResolver _semanticsResolver;
        private readonly IFilesystemMutationCoordinator _mutationCoordinator;
        private readonly IAudiobookDeletionIntentProbe? _deletionIntentProbe;
        private readonly IFileRenameRecoveryProbe? _fileRenameRecoveryProbe;

        public MoveQueueService(
            ILogger<MoveQueueService> logger,
            IMoveQueuePersistence persistence,
            IHubBroadcaster hubBroadcaster,
            TimeProvider timeProvider,
            IFileSystemSemanticsResolver semanticsResolver,
            IRootFolderRelocationService relocationService,
            IFilesystemMutationCoordinator mutationCoordinator,
            IAudiobookDeletionIntentProbe? deletionIntentProbe = null,
            IFileRenameRecoveryProbe? fileRenameRecoveryProbe = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
            _hubBroadcaster = hubBroadcaster ?? throw new ArgumentNullException(nameof(hubBroadcaster));
            _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
            _semanticsResolver = semanticsResolver ?? throw new ArgumentNullException(nameof(semanticsResolver));
            _mutationCoordinator = mutationCoordinator ?? throw new ArgumentNullException(nameof(mutationCoordinator));
            _relocationService = relocationService ?? throw new ArgumentNullException(nameof(relocationService));
            _deletionIntentProbe = deletionIntentProbe;
            _fileRenameRecoveryProbe = fileRenameRecoveryProbe;
        }

        public ChannelReader<MoveJob> Reader => _channel.Reader;

        public async Task<Guid> EnqueueMoveAsync(
            MoveEnqueueCommand command,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);
            await EnsureExternalRecoveryAllowsMutationAsync(
                command.AudiobookId,
                allowActiveDeletionIntent: false,
                cancellationToken);
            await EnsureIdentityKeysReconciledAsync(cancellationToken);

            var source = FileSystemPathIdentity.Canonicalize(
                command.SourcePath,
                command.SourceIdentity.Syntax);
            var target = FileSystemPathIdentity.Canonicalize(
                command.TargetPath,
                command.TargetIdentity.Syntax);
            command.SourceIdentity.ValidateForPath(source);
            command.TargetIdentity.ValidateForPath(target);
            if (FileSystemPathIdentity.AreEquivalentEndpoints(
                    source,
                    command.SourceIdentity,
                    target,
                    command.TargetIdentity))
            {
                throw new ArgumentException(
                    "Move source and target paths must be distinct.",
                    nameof(command));
            }

            var manifest = ValidateSourceManifest(
                source,
                command.SourceIdentity,
                target,
                command.TargetIdentity,
                command.SourceEntries);
            if (command.TargetBoundaryDirectoryObjectIdentityVersion <= 0
                || string.IsNullOrWhiteSpace(
                    command.TargetBoundaryDirectoryObjectIdentity))
            {
                throw new InvalidOperationException(
                    "A physical move requires durable target-boundary generation authorization.");
            }

            var persistedEntries = manifest.Entries.ToList();
            persistedEntries.Add(
                MoveManifestIdentity.CreateTargetBoundaryAuthorization(
                    command.TargetBoundaryDirectoryObjectIdentityVersion,
                    command.TargetBoundaryDirectoryObjectIdentity));
            var deduplicationKey = MoveManifestIdentity.CreateDeduplicationKey(
                command.AudiobookId,
                source,
                command.SourceIdentity,
                target,
                command.TargetIdentity,
                persistedEntries);

            MoveJob? jobToSchedule = null;
            var jobId = await _mutationCoordinator.ExecuteExclusiveAsync(async token =>
            {
                await ThrowIfRelocationBoundaryProtectedAsync(
                    source,
                    command.SourceIdentity,
                    target,
                    command.TargetIdentity,
                    token);
                var existingDb = await _persistence.GetActiveByKeyAsync(
                    deduplicationKey,
                    token);
                if (existingDb != null)
                {
                    jobToSchedule = existingDb;
                    _logger.LogInformation(
                        "Found active move job {JobId} for audiobook {AudiobookId} to {Path}; deduping and returning existing job id",
                        existingDb.Id,
                        command.AudiobookId,
                        LogRedaction.SanitizeFilePath(target));
                    return existingDb.Id;
                }

                var job = new MoveJob
                {
                    AudiobookId = command.AudiobookId,
                    RequestedPath = target,
                    ActiveDeduplicationKey = deduplicationKey,
                    IdentityKeyVersion = MoveManifestIdentity.Version,
                    ExecutionProtocolVersion = MoveExecutionProtocol.Current,
                    EnqueuedAt = _timeProvider.GetUtcNow().UtcDateTime,
                    Status = MoveJobStatus.Queued,
                    SourcePath = source,
                    SourceCleanupBoundary = command.SourceCleanupBoundary,
                    DeleteEmptySource = command.DeleteEmptySource,
                    RelocationId = command.RelocationId,
                    Entries = persistedEntries
                };
                job.SetSourceIdentity(command.SourceIdentity);
                job.SetTargetIdentity(command.TargetIdentity);

                var commitToken = RequestCancellationBoundary.EnterNonCancelablePhase(token);
                try
                {
                    await _persistence.AddAsync(job, commitToken);
                }
                catch (UniqueConstraintViolationException)
                {
                    existingDb = await _persistence.GetActiveByKeyAsync(
                        deduplicationKey,
                        commitToken);
                    if (existingDb != null)
                    {
                        jobToSchedule = existingDb;
                        return existingDb.Id;
                    }

                    throw;
                }

                _logger.LogInformation(
                    "Enqueueing move job {JobId} for audiobook {AudiobookId} to {Path}",
                    job.Id,
                    command.AudiobookId,
                    LogRedaction.SanitizeFilePath(target));
                jobToSchedule = job;
                return job.Id;
            }, cancellationToken);

            if (jobToSchedule != null)
            {
                await ScheduleAsync(jobToSchedule);
                await NotifyCommittedJobStateAsync(
                    jobToSchedule.Id,
                    jobToSchedule.Status,
                    jobToSchedule.Error,
                    CancellationToken.None);
            }

            return jobId;
        }

        public async Task RecoverActiveJobsAsync(CancellationToken cancellationToken = default)
        {
            await EnsureIdentityKeysReconciledAsync(cancellationToken);
            var activeJobs = await _persistence.GetActiveAsync(cancellationToken);
            await _relocationService.ReconcileActiveAsync(cancellationToken);
            var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
            var schedulableJobs = activeJobs.Where(job =>
                    job.Status == MoveJobStatus.Queued
                    || (job.Status == MoveJobStatus.RetryScheduled
                        && (job.NextAttemptAt == null || job.NextAttemptAt <= nowUtc))
                    || (job.Status == MoveJobStatus.Running
                        && (job.LeaseExpiresAt == null || job.LeaseExpiresAt <= nowUtc)))
                .ToList();
            foreach (var activeJob in schedulableJobs)
            {
                await ScheduleAsync(activeJob, cancellationToken);
            }

            if (schedulableJobs.Count > 0)
            {
                _logger.LogInformation(
                    "Recovered {Count} claimable move jobs from persistence",
                    schedulableJobs.Count);
            }
        }

        public Task<int?> TryClaimJobAsync(
            Guid jobId,
            string leaseOwner,
            CancellationToken cancellationToken = default)
        {
            var now = _timeProvider.GetUtcNow();
            return _persistence.TryClaimAsync(
                jobId,
                leaseOwner,
                now,
                now.Add(MoveTimingPolicy.OwnershipDuration),
                cancellationToken);
        }

        public Task<MoveHeartbeatOutcome> HeartbeatJobAsync(
            Guid jobId,
            string leaseOwner,
            int leaseGeneration,
            CancellationToken cancellationToken = default)
        {
            var now = _timeProvider.GetUtcNow();
            return _persistence.HeartbeatAsync(
                jobId,
                leaseOwner,
                leaseGeneration,
                now,
                now.Add(MoveTimingPolicy.OwnershipDuration),
                cancellationToken);
        }

        public async Task<IReadOnlyList<MoveJob>> GetActiveJobsAsync(CancellationToken cancellationToken = default)
        {
            return await _persistence.GetActiveAsync(cancellationToken);
        }

        public Task<MoveQueueHealthSnapshot> GetQueueHealthAsync(
            CancellationToken cancellationToken = default) =>
            _persistence.GetHealthAsync(_timeProvider.GetUtcNow(), cancellationToken);

        public async Task<MoveJob?> GetJobAsync(
            Guid id,
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await _persistence.GetByIdAsync(id, cancellationToken);
            }
            catch (Exception ex) when (WorkerExceptionClassifier.IsNonFatal(ex))
            {
                _logger.LogWarning(ex, "Failed to retrieve move job {JobId}", id);
                throw;
            }
        }

        public async Task UpdateJobStatusAsync(
            Guid id,
            string leaseOwner,
            int leaseGeneration,
            MoveJobStatus status,
            string? error = null,
            CancellationToken cancellationToken = default)
        {
            await UpdateJobStatusWithoutNotificationAsync(
                id,
                leaseOwner,
                leaseGeneration,
                status,
                error,
                cancellationToken);
            await NotifyCommittedJobStateAsync(
                id,
                status,
                error,
                cancellationToken);
        }

        public async Task UpdateJobStatusWithoutNotificationAsync(
            Guid id,
            string leaseOwner,
            int leaseGeneration,
            MoveJobStatus status,
            string? error = null,
            CancellationToken cancellationToken = default)
        {
            var updatedAt = _timeProvider.GetUtcNow();
            MoveJob? dbJob;
            try
            {
                dbJob = await _persistence.GetByIdAsync(id, cancellationToken);
                var phase = status == MoveJobStatus.Running
                    && (dbJob?.Phase ?? MoveJobPhase.None) == MoveJobPhase.None
                        ? MoveJobPhase.Planned
                        : dbJob?.Phase ?? MoveJobPhase.None;
                var failureKind = status is MoveJobStatus.Failed or MoveJobStatus.NeedsAttention
                    ? MoveFailureKind.Unknown
                    : MoveFailureKind.None;
                var updated = await PersistWithRetryAsync(
                    () => _persistence.UpdateStatusAsync(id, leaseOwner, leaseGeneration, status, phase, error, failureKind, updatedAt, cancellationToken),
                    cancellationToken);
                if (!updated)
                {
                    throw new MoveLeaseLostException(id, leaseGeneration);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to persist move job status change for {JobId}", id);
                throw;
            }
        }

        public async Task NotifyPersistedJobStateAsync(
            Guid id,
            MoveJobStatus status,
            string? error = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await _relocationService.OnMoveJobStateChangedAsync(id, cancellationToken);
            }
            catch (Exception ex) when (WorkerExceptionClassifier.IsNonFatal(ex))
            {
                _logger.LogWarning(ex, "Failed to reconcile relocation for move job {JobId}", id);
            }

            var publicationGate = AcquirePublicationGate(id);
            var enteredPublicationGate = false;
            try
            {
                await publicationGate.Gate.WaitAsync(cancellationToken);
                enteredPublicationGate = true;
                MoveJob? dbJob = null;
                try
                {
                    dbJob = await _persistence.GetByIdAsync(id, cancellationToken);
                }
                catch (Exception ex) when (WorkerExceptionClassifier.IsNonFatal(ex))
                {
                    _logger.LogWarning(ex, "Failed to reload persisted move job {JobId} for notification", id);
                }

                var currentStatus = dbJob?.Status ?? status;
                var currentError = dbJob?.Error ?? error;
                LogStatusChange(id, currentStatus, currentError);
                try
                {
                    await _hubBroadcaster.BroadcastAsync(
                        "MoveJobUpdate",
                        MoveJobPublicProjection.CreateUpdate(
                            id,
                            status,
                            error,
                            _timeProvider.GetUtcNow().UtcDateTime,
                            dbJob),
                        cancellationToken);
                }
                catch (Exception ex) when (WorkerExceptionClassifier.IsNonFatal(ex))
                {
                    _logger.LogWarning(ex, "Failed to broadcast MoveJobUpdate for job {JobId}", id);
                }
            }
            finally
            {
                if (enteredPublicationGate)
                {
                    publicationGate.Gate.Release();
                }

                ReleasePublicationGate(id, publicationGate);
            }
        }

        public async Task IncrementAttemptAsync(
            Guid id,
            string leaseOwner,
            int leaseGeneration,
            CancellationToken cancellationToken = default)
        {
            var incremented = await PersistWithRetryAsync(
                () => _persistence.TryIncrementAttemptAsync(
                    id,
                    leaseOwner,
                    leaseGeneration,
                    _timeProvider.GetUtcNow(),
                    cancellationToken),
                cancellationToken);
            if (!incremented)
            {
                throw new MoveLeaseLostException(id, leaseGeneration);
            }
        }

        private PublicationGateEntry AcquirePublicationGate(Guid id)
        {
            lock (_publicationGateSync)
            {
                if (!_publicationGates.TryGetValue(id, out var entry))
                {
                    entry = new PublicationGateEntry();
                    _publicationGates.Add(id, entry);
                }

                entry.References++;
                return entry;
            }
        }

        private void ReleasePublicationGate(Guid id, PublicationGateEntry entry)
        {
            lock (_publicationGateSync)
            {
                entry.References--;
                if (entry.References == 0
                    && _publicationGates.TryGetValue(id, out var current)
                    && ReferenceEquals(current, entry))
                {
                    _publicationGates.Remove(id);
                    entry.Gate.Dispose();
                }
            }
        }

        internal int PublicationGateCount
        {
            get
            {
                lock (_publicationGateSync)
                {
                    return _publicationGates.Count;
                }
            }
        }

        internal int GetPublicationGateReferenceCount(Guid id)
        {
            lock (_publicationGateSync)
            {
                return _publicationGates.TryGetValue(id, out var entry)
                    ? entry.References
                    : 0;
            }
        }

        private sealed class PublicationGateEntry
        {
            public SemaphoreSlim Gate { get; } = new(1, 1);
            public int References { get; set; }
        }

        private void LogStatusChange(Guid id, MoveJobStatus status, string? error)
        {
            if (status == MoveJobStatus.Failed && !string.IsNullOrWhiteSpace(error))
            {
                _logger.LogError("Move job {JobId} FAILED with error: {Error}", id, error);
            }
            else
            {
                _logger.LogInformation("Updated move job {JobId} status to {Status}", id, status);
            }
        }

    }
}
