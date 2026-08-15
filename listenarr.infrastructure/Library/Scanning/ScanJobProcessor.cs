/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Scanning
{
    public partial class ScanJobProcessor : IScanJobProcessor
    {
        private async Task ProcessJobCoreAsync(
            ScanJob job,
            Action<Func<CancellationToken, Task>> registerPostCompletionEffects,
            CancellationToken stoppingToken)
        {
            using var logScope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["JobId"] = job.Id,
                ["AudiobookId"] = job.AudiobookId,
                ["CorrelationId"] = job.CorrelationId ?? job.Id.ToString("N")
            });
            _metrics.Increment("worker.scan.job.started");
            stoppingToken.ThrowIfCancellationRequested();
            try
            {
                _logger.LogInformation(
                    "Processing scan job {JobId} for audiobook {AudiobookId}",
                    job.Id,
                    job.AudiobookId);
                await _moveQueueService.EnsureFilesystemMutationAllowedAsync(
                    job.AudiobookId,
                    stoppingToken);
                await BroadcastProcessingAsync(job);
                try
                {
                    _queue.UpdateJobStatus(job.Id, "Processing");
                }
                catch (Exception exception) when (WorkerExceptionClassifier.IsNonFatal(exception))
                {
                    _logger.LogDebug(
                        exception,
                        "Unable to publish processing status for scan job {JobId}",
                        job.Id);
                }

                using var scope = _scopeFactory.CreateScope();
                var audiobookRepository = scope.ServiceProvider
                    .GetRequiredService<IAudiobookRepository>();
                var historyRepository = scope.ServiceProvider
                    .GetRequiredService<IHistoryRepository>();
                var audiobook = await audiobookRepository.GetForScanAsync(
                    job.AudiobookId,
                    stoppingToken);
                if (audiobook == null)
                {
                    _logger.LogWarning(
                        "Audiobook {Id} not found for scan job {JobId}",
                        job.AudiobookId,
                        job.Id);
                    await RecordMoveScanFailureAsync(
                        historyRepository,
                        job,
                        audiobook: null,
                        "Audiobook not found",
                        stoppingToken);
                    _metrics.Increment("worker.scan.job.skipped");
                    return;
                }

                var commandResolution = await ResolveScanCommandAsync(
                    scope.ServiceProvider,
                    job,
                    audiobook,
                    historyRepository,
                    stoppingToken);
                if (commandResolution.Command == null)
                {
                    if (commandResolution.BroadcastFailure)
                    {
                        registerPostCompletionEffects(token => BroadcastFailedScanAsync(
                            job,
                            commandResolution.TerminalStatus ?? "Failed",
                            commandResolution.Error,
                            token));
                    }

                    _metrics.Increment(commandResolution.Metric);
                    return;
                }

                var scanRoot = commandResolution.Command.ScanRoot;
                if (!await ValidateScanRootSafetyAsync(
                        scanRoot,
                        job,
                        audiobook,
                        historyRepository,
                        stoppingToken))
                {
                    return;
                }

                var scanService = scope.ServiceProvider
                    .GetRequiredService<IAudiobookScanService>();
                var result = await scanService.ScanAsync(
                    commandResolution.Command,
                    stoppingToken);
                if (result.RemovedFiles.Count > 0)
                {
                    var removedFiles = result.RemovedFiles
                        .Select(file => (object)new
                        {
                            id = file.Id,
                            path = file.Path
                        })
                        .ToList();
                    registerPostCompletionEffects(token => BroadcastFilesRemovedAsync(
                        result.Audiobook.Id,
                        removedFiles,
                        token));
                }

                var terminalDecision = await CommitTerminalDecisionAsync(
                    job,
                    commitToken => RecordScanCompletionAsync(
                        historyRepository,
                        job,
                        result.Audiobook,
                        result.AttributedFiles.Count,
                        result.CreatedCount,
                        scanRoot,
                        commitToken),
                    stoppingToken);
                if (!string.Equals(
                        terminalDecision.Status,
                        "Completed",
                        StringComparison.OrdinalIgnoreCase))
                {
                    _metrics.Increment("worker.scan.job.skipped");
                    return;
                }

                registerPostCompletionEffects(token =>
                    RunSuccessfulPostCompletionEffectsAsync(
                        job,
                        result.Audiobook,
                        result.AttributedFiles.Count,
                        result.CreatedCount,
                        token));
                _metrics.Increment("worker.scan.job.completed");
            }
            catch (Exception exception) when (WorkerExceptionClassifier.IsNonFatal(exception))
            {
                await HandleUnexpectedScanFailureAsync(
                    job,
                    exception,
                    registerPostCompletionEffects,
                    stoppingToken);
            }
        }

        private async Task<ResolvedScanCommand> ResolveScanCommandAsync(
            IServiceProvider services,
            ScanJob job,
            Audiobook audiobook,
            IHistoryRepository historyRepository,
            CancellationToken cancellationToken)
        {
            var moveOwned = job.MoveScanHandoffId.HasValue;
            var usedBasePath =
                job.AuthorizationMode == ScanAuthorizationMode.ResolveCurrentAudiobookPath
                && !string.IsNullOrWhiteSpace(audiobook.BasePath);
            var authorizationContractError = ValidateAuthorizationContract(job, moveOwned);
            if (authorizationContractError != null)
            {
                return await RejectScanAsync(
                    historyRepository,
                    job,
                    audiobook,
                    authorizationContractError,
                    cancellationToken);
            }

            var requestedScanPath =
                job.AuthorizationMode == ScanAuthorizationMode.ResolveCurrentAudiobookPath
                    ? audiobook.BasePath
                    : job.Path;
            if (!string.IsNullOrWhiteSpace(requestedScanPath)
                && FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    requestedScanPath,
                    out var hostRequestedScanPath,
                    out _)
                && !Directory.Exists(hostRequestedScanPath))
            {
                var missingError = usedBasePath
                    ? "BasePath unavailable"
                    : "Scan path not found";
                var missing = await RejectScanAsync(
                    historyRepository,
                    job,
                    audiobook,
                    missingError,
                    cancellationToken);
                return missing with
                {
                    BroadcastFailure = usedBasePath,
                    Metric = usedBasePath
                        ? "worker.scan.job.failed"
                        : "worker.scan.job.skipped"
                };
            }

            var authorizationService = services
                .GetRequiredService<IScanPathAuthorizationService>();
            var authorization =
                job.AuthorizationMode == ScanAuthorizationMode.ResolveCurrentAudiobookPath
                    ? await authorizationService.ResolveDefaultAsync(
                        audiobook.BasePath,
                        cancellationToken)
                    : await authorizationService.AuthorizeAsync(
                        job.Path!,
                        cancellationToken);
            if (!authorization.IsAuthorized)
            {
                var rejection = await RejectScanAsync(
                    historyRepository,
                    job,
                    audiobook,
                    authorization.Error ?? "Scan path authorization failed.",
                    cancellationToken);
                return rejection with
                {
                    BroadcastFailure = usedBasePath,
                    Metric = usedBasePath
                        ? "worker.scan.job.failed"
                        : "worker.scan.job.skipped"
                };
            }

            var scanRoot = authorization.Path!;
            if (!Directory.Exists(scanRoot))
            {
                var missingError = usedBasePath
                    ? "BasePath unavailable"
                    : "Scan path not found";
                var missing = await RejectScanAsync(
                    historyRepository,
                    job,
                    audiobook,
                    missingError,
                    cancellationToken);
                return missing with
                {
                    BroadcastFailure = usedBasePath,
                    Metric = usedBasePath
                        ? "worker.scan.job.failed"
                        : "worker.scan.job.skipped"
                };
            }

            var identity = authorization.Identity!.Value;
            var physicalIdentity = authorization.PhysicalIdentity!.Value;
            if (job.PathIdentity.HasValue)
            {
                try
                {
                    var persistedIdentity = await ValidateScanIdentityAsync(
                        job.Path!,
                        job.PathIdentity.Value,
                        cancellationToken);
                    if (!PersistedAuthorizationMatches(
                            persistedIdentity,
                            identity))
                    {
                        throw new InvalidOperationException(
                            "The configured scan-root authority changed after the job was queued.");
                    }
                }
                catch (InvalidOperationException exception)
                {
                    return await RejectScanAsync(
                        historyRepository,
                        job,
                        audiobook,
                        exception.Message,
                        cancellationToken);
                }
            }

            if (job.PhysicalIdentity.HasValue
                && job.PhysicalIdentity.Value != physicalIdentity)
            {
                return await RejectScanAsync(
                    historyRepository,
                    job,
                    audiobook,
                    "The physical scan-root generation changed after the job was queued.",
                    cancellationToken);
            }

            if (!Directory.Exists(scanRoot))
            {
                var error = usedBasePath
                    ? "BasePath unavailable"
                    : "Scan path not found";
                var rejection = await RejectScanAsync(
                    historyRepository,
                    job,
                    audiobook,
                    error,
                    cancellationToken);
                return rejection with
                {
                    BroadcastFailure = usedBasePath,
                    Metric = usedBasePath
                        ? "worker.scan.job.failed"
                        : "worker.scan.job.skipped"
                };
            }

            if (moveOwned
                && (string.IsNullOrWhiteSpace(audiobook.BasePath)
                    || !FileSystemPathIdentity.AreEquivalent(
                        audiobook.BasePath,
                        scanRoot,
                        identity.Semantics)))
            {
                await RecordMoveScanSupersededAsync(
                    job,
                    "A newer audiobook destination superseded this move scan handoff.",
                    cancellationToken);
                return ResolvedScanCommand.Rejected(
                    "worker.scan.job.skipped",
                    terminalStatus: "Superseded");
            }

            return new ResolvedScanCommand(
                new AudiobookScanCommand(
                    audiobook.Id,
                    scanRoot,
                    identity,
                    physicalIdentity,
                    MoveOwned: moveOwned,
                    AllowReconciliation: true,
                    IsAuthoritativeScope: job.AuthorizationMode switch
                    {
                        ScanAuthorizationMode.ResolveCurrentAudiobookPath =>
                            job.IsAuthoritativeScope,
                        ScanAuthorizationMode.PreauthorizedPath =>
                            job.IsAuthoritativeScope,
                        ScanAuthorizationMode.MoveHandoff =>
                            moveOwned && job.IsAuthoritativeScope,
                        _ => false
                    },
                    Source: "LibraryScan",
                    CorrelationId: job.CorrelationId ?? job.Id.ToString("N")),
                "worker.scan.job.completed");
        }

        private static string? ValidateAuthorizationContract(
            ScanJob job,
            bool moveOwned)
        {
            return job.AuthorizationMode switch
            {
                ScanAuthorizationMode.ResolveCurrentAudiobookPath
                    when moveOwned
                        || !string.IsNullOrWhiteSpace(job.Path)
                        || job.PathIdentity.HasValue
                        || job.PhysicalIdentity.HasValue =>
                    "The current-path scan contains incompatible queued path authority.",
                ScanAuthorizationMode.PreauthorizedPath
                    when moveOwned
                        || string.IsNullOrWhiteSpace(job.Path)
                        || !job.PathIdentity.HasValue
                        || !job.PhysicalIdentity.HasValue =>
                    "The preauthorized scan has incomplete or incompatible queued path authority.",
                ScanAuthorizationMode.MoveHandoff
                    when !moveOwned
                        || string.IsNullOrWhiteSpace(job.Path)
                        || !job.PathIdentity.HasValue
                        || !job.PhysicalIdentity.HasValue =>
                    "The move scan handoff has incomplete or incompatible target filesystem authority.",
                ScanAuthorizationMode.ResolveCurrentAudiobookPath
                    or ScanAuthorizationMode.PreauthorizedPath
                    or ScanAuthorizationMode.MoveHandoff => null,
                _ => "The scan job has an unknown authorization mode."
            };
        }

        private static bool PersistedAuthorizationMatches(
            PathIdentitySnapshot persisted,
            PathIdentitySnapshot current) =>
            persisted.Syntax == current.Syntax
            && persisted.CaseSensitivity == current.CaseSensitivity
            && persisted.RequestedMode == current.RequestedMode
            && FileSystemPathIdentity.AreEquivalent(
                persisted.BoundaryPath,
                current.BoundaryPath,
                current.Semantics);

        private async Task<ResolvedScanCommand> RejectScanAsync(
            IHistoryRepository historyRepository,
            ScanJob job,
            Audiobook audiobook,
            string error,
            CancellationToken cancellationToken)
        {
            var decision = await RecordMoveScanFailureAsync(
                historyRepository,
                job,
                audiobook,
                error,
                cancellationToken);
            return ResolvedScanCommand.Rejected(
                "worker.scan.job.skipped",
                decision.Status,
                decision.Error ?? error);
        }

        private async Task BroadcastProcessingAsync(ScanJob job)
        {
            try
            {
                await _hubContext.Clients.All.SendAsync("ScanJobUpdate", new
                {
                    jobId = job.Id.ToString(),
                    audiobookId = job.AudiobookId,
                    status = "Processing",
                    startedAt = DateTime.UtcNow
                });
            }
            catch (Exception exception) when (WorkerExceptionClassifier.IsNonFatal(exception))
            {
                _logger.LogDebug(
                    exception,
                    "Unable to broadcast processing state for scan job {JobId}",
                    job.Id);
            }
        }

        private sealed record ResolvedScanCommand(
            AudiobookScanCommand? Command,
            string Metric,
            string? TerminalStatus = null,
            string? Error = null,
            bool BroadcastFailure = false)
        {
            public static ResolvedScanCommand Rejected(
                string metric,
                string? terminalStatus = null,
                string? error = null) =>
                new(null, metric, terminalStatus, error);
        }
    }
}
