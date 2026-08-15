/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using System.Collections.Concurrent;
using System.Threading.Channels;
using Listenarr.Application.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Jobs;

public partial class ScanQueueService : IScanQueueService
{
    private static readonly TimeSpan MoveHandoffLeaseDuration = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<Guid, ScanJob> _jobs = new();
    private readonly Channel<ScanJob> _channel = Channel.CreateUnbounded<ScanJob>();
    private readonly SemaphoreSlim _enqueueGate = new(1, 1);
    private readonly Dictionary<int, DispatchReservation> _dispatchReservations = [];
    private readonly ILogger<ScanQueueService> _logger;
    private readonly IMoveScanHandoffStore? _handoffStore;
    private readonly TimeProvider _timeProvider;

    public ScanQueueService(
        ILogger<ScanQueueService> logger,
        IMoveScanHandoffStore? handoffStore = null,
        TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _handoffStore = handoffStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<Guid> EnqueueScanAsync(ScanEnqueueCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Audiobook);

        var pathIdentity = command.PathIdentity;
        var physicalIdentity = command.PhysicalIdentity;
        switch (command.AuthorizationMode)
        {
            case ScanAuthorizationMode.ResolveCurrentAudiobookPath:
                if (!string.IsNullOrWhiteSpace(command.Path)
                    || pathIdentity.HasValue
                    || physicalIdentity.HasValue)
                {
                    throw new ArgumentException(
                        "A current-path scan cannot carry a queued path or queued path identities.",
                        nameof(command));
                }
                break;

            case ScanAuthorizationMode.PreauthorizedPath:
                if (string.IsNullOrWhiteSpace(command.Path)
                    || !pathIdentity.HasValue
                    || !physicalIdentity.HasValue
                    || !IsCompletePhysicalIdentity(physicalIdentity.Value))
                {
                    throw new InvalidOperationException(
                        "A preauthorized path scan must carry its path plus lexical and physical authorization before queue publication.");
                }

                pathIdentity.Value.ValidateForPath(command.Path);
                break;

            case ScanAuthorizationMode.MoveHandoff:
                throw new ArgumentException(
                    "Move handoff scans must be published through EnqueueMoveHandoffScanAsync.",
                    nameof(command));

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(command),
                    command.AuthorizationMode,
                    "Unknown scan authorization mode.");
        }

        var job = new ScanJob
        {
            AudiobookId = command.Audiobook.Id,
            Path = command.Path,
            PathIdentity = pathIdentity,
            PhysicalIdentity = physicalIdentity,
            CorrelationId = command.CorrelationId,
            DownloadId = command.DownloadId,
            IsAuthoritativeScope = command.IsAuthoritativeScope,
            AuthorizationMode = command.AuthorizationMode
        };
        return await EnqueueJobAsync(
                job,
                allowUncorrelatedPathDedupe: command.CorrelationId == null)
            ?? throw new InvalidOperationException(
                "A normal scan enqueue was unexpectedly deferred.");
    }

    public Task<Guid> EnqueueScanAsync(
        Audiobook audiobook,
        string? correlationId = null,
        string? downloadId = null) =>
        EnqueueScanAsync(new ScanEnqueueCommand(
            audiobook,
            Path: null,
            PathIdentity: null,
            PhysicalIdentity: null,
            correlationId,
            downloadId,
            IsAuthoritativeScope: true,
            AuthorizationMode: ScanAuthorizationMode.ResolveCurrentAudiobookPath));

    public async Task<Guid?> EnqueueMoveHandoffScanAsync(
        Audiobook audiobook,
        MoveScanHandoffClaim claim,
        ScanPathPhysicalIdentity physicalIdentity)
    {
        ArgumentNullException.ThrowIfNull(audiobook);
        ArgumentNullException.ThrowIfNull(claim);
        if (_handoffStore == null)
        {
            throw new InvalidOperationException(
                "Move scan handoff dispatch requires a durable handoff store.");
        }

        claim.TargetIdentity.ValidateForPath(claim.TargetPath);
        if (!IsCompletePhysicalIdentity(physicalIdentity))
        {
            throw new ArgumentException(
                "Move handoff physical authority is incomplete.",
                nameof(physicalIdentity));
        }

        var job = new ScanJob
        {
            AudiobookId = audiobook.Id,
            Path = claim.TargetPath,
            PathIdentity = claim.TargetIdentity,
            PhysicalIdentity = physicalIdentity,
            CorrelationId = $"move:{claim.MoveJobId:N}",
            MoveScanHandoffId = claim.HandoffId,
            MoveScanAttemptGeneration = claim.AttemptGeneration,
            IsAuthoritativeScope = true,
            AuthorizationMode = ScanAuthorizationMode.MoveHandoff,
            Status = "Queued"
        };

        DispatchReservation reservation;
        Task<Guid?>? existingReservation = null;
        await _enqueueGate.WaitAsync();
        try
        {
            var correlated = FindCorrelatedActiveJob(job);
            if (correlated != null)
            {
                return correlated.Id;
            }

            if (_dispatchReservations.TryGetValue(job.AudiobookId, out var currentReservation))
            {
                if (currentReservation.Matches(claim))
                {
                    existingReservation = currentReservation.Completion.Task;
                }
                else
                {
                    _logger.LogInformation(
                        "Deferred move scan handoff {HandoffId} because another dispatch is being published for audiobook {AudiobookId}",
                        claim.HandoffId,
                        job.AudiobookId);
                    return null;
                }
            }
            else if (_jobs.Values.Any(candidate =>
                candidate.AudiobookId == job.AudiobookId && IsActive(candidate.Status)))
            {
                _logger.LogInformation(
                    "Deferred move scan handoff {HandoffId} because another scan is active for audiobook {AudiobookId}",
                    claim.HandoffId,
                    job.AudiobookId);
                return null;
            }

            if (existingReservation == null)
            {
                reservation = new DispatchReservation(claim);
                _dispatchReservations.Add(job.AudiobookId, reservation);
            }
            else
            {
                reservation = currentReservation!;
            }
        }
        finally
        {
            _enqueueGate.Release();
        }

        if (existingReservation != null)
        {
            return await existingReservation;
        }

        var now = _timeProvider.GetUtcNow();
        try
        {
            if (!await _handoffStore.MarkDispatchedAsync(
                    claim.HandoffId,
                    claim.LeaseOwner,
                    claim.LeaseGeneration,
                    job.Id,
                    now))
            {
                await CompleteReservationAsync(
                    job.AudiobookId,
                    reservation,
                    result: null);
                return null;
            }

            await _enqueueGate.WaitAsync();
            try
            {
                if (!_dispatchReservations.TryGetValue(job.AudiobookId, out var current)
                    || !ReferenceEquals(current, reservation))
                {
                    throw new InvalidOperationException(
                        "The private move scan dispatch reservation disappeared before publication.");
                }

                _jobs[job.Id] = job;
                if (!_channel.Writer.TryWrite(job))
                {
                    _jobs.TryRemove(job.Id, out _);
                    throw new InvalidOperationException(
                        "The scan queue is no longer accepting jobs.");
                }

                _dispatchReservations.Remove(job.AudiobookId);
                reservation.Completion.TrySetResult(job.Id);
            }
            finally
            {
                _enqueueGate.Release();
            }

            _logger.LogInformation(
                "Enqueued move scan handoff {HandoffId} attempt {AttemptGeneration} as job {JobId}",
                claim.HandoffId,
                claim.AttemptGeneration,
                job.Id);
            return job.Id;
        }
        catch
        {
            await CompleteReservationAsync(
                job.AudiobookId,
                reservation,
                result: null);
            try
            {
                await _handoffStore.ReleaseClaimAsync(
                    claim.HandoffId,
                    claim.LeaseOwner,
                    claim.LeaseGeneration,
                    "Move scan channel publication failed after durable dispatch reservation.",
                    _timeProvider.GetUtcNow());
            }
            catch (Exception requeueException) when (WorkerExceptionClassifier.IsNonFatal(requeueException))
            {
                _logger.LogDebug(
                    requeueException,
                    "Unable to release move scan handoff {HandoffId} after publication failure",
                    claim.HandoffId);
            }

            throw;
        }
    }

    private async Task<Guid?> EnqueueJobAsync(
        ScanJob job,
        bool allowUncorrelatedPathDedupe)
    {
        while (true)
        {
            Task<Guid?>? pendingDispatch = null;
            await _enqueueGate.WaitAsync();
            try
            {
                if (_dispatchReservations.TryGetValue(job.AudiobookId, out var reservation))
                {
                    pendingDispatch = reservation.Completion.Task;
                }
                else
                {
                    var matchingJobs = _jobs.Values.Where(candidate =>
                        candidate.AudiobookId == job.AudiobookId
                        && PathsMatch(candidate, job));
                    var correlated = !string.IsNullOrWhiteSpace(job.CorrelationId)
                        ? matchingJobs.FirstOrDefault(candidate =>
                            string.Equals(
                                candidate.CorrelationId,
                                job.CorrelationId,
                                StringComparison.Ordinal)
                            && candidate.MoveScanHandoffId == job.MoveScanHandoffId
                            && candidate.MoveScanAttemptGeneration == job.MoveScanAttemptGeneration
                            && IsActive(candidate.Status))
                        : null;
                    if (correlated != null)
                    {
                        _logger.LogInformation(
                            "Found active correlated scan job {JobId} for audiobook {AudiobookId}",
                            correlated.Id,
                            job.AudiobookId);
                        return correlated.Id;
                    }

                    if (allowUncorrelatedPathDedupe)
                    {
                        var active = matchingJobs.FirstOrDefault(candidate => IsActive(candidate.Status));
                        if (active != null)
                        {
                            _logger.LogInformation(
                                "Found active uncorrelated scan job {JobId} for audiobook {AudiobookId}; reusing it",
                                active.Id,
                                job.AudiobookId);
                            return active.Id;
                        }
                    }
                    else if (job.MoveScanHandoffId.HasValue
                        && _jobs.Values.Any(candidate =>
                            candidate.AudiobookId == job.AudiobookId
                            && IsActive(candidate.Status)))
                    {
                        _logger.LogInformation(
                            "Deferred move scan handoff {HandoffId} because another scan is active for audiobook {AudiobookId}",
                            job.MoveScanHandoffId,
                            job.AudiobookId);
                        return null;
                    }

                    _jobs[job.Id] = job;
                    if (!_channel.Writer.TryWrite(job))
                    {
                        _jobs.TryRemove(job.Id, out _);
                        throw new InvalidOperationException(
                            "The scan queue is no longer accepting jobs.");
                    }

                    _logger.LogInformation(
                        "Enqueued scan job {JobId} for audiobook {AudiobookId}",
                        job.Id,
                        job.AudiobookId);
                    return job.Id;
                }
            }
            finally
            {
                _enqueueGate.Release();
            }

            // A normal scan waits outside the queue gate, then reevaluates the published
            // job set. A failed provisional dispatch therefore cannot return a phantom ID.
            await pendingDispatch!;
        }
    }

    private static bool IsCompletePhysicalIdentity(
        ScanPathPhysicalIdentity physicalIdentity) =>
        !string.IsNullOrWhiteSpace(physicalIdentity.BoundaryObjectIdentity)
        && !string.IsNullOrWhiteSpace(physicalIdentity.ScanRootObjectIdentity);

    public bool TryGetJob(Guid id, out ScanJob? job) => _jobs.TryGetValue(id, out job);

    public ChannelReader<ScanJob> Reader => _channel.Reader;

    public void UpdateJobStatus(
        Guid id,
        string status,
        string? error = null,
        int? found = null,
        int? created = null)
    {
        _enqueueGate.Wait();
        try
        {
            UpdateJobStatusCore(id, status, error);
        }
        finally
        {
            _enqueueGate.Release();
        }
    }

    public async Task CommitTerminalJobStatusAsync(
        Guid jobId,
        Func<Task<(string Status, string? Error)>> persistTerminalState,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(persistTerminalState);
        cancellationToken.ThrowIfCancellationRequested();
        var terminalState = await persistTerminalState();
        await _enqueueGate.WaitAsync(CancellationToken.None);
        try
        {
            UpdateJobStatusCore(jobId, terminalState.Status, terminalState.Error);
        }
        finally
        {
            _enqueueGate.Release();
        }
    }

}
