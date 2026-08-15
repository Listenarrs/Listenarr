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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Moving;

internal partial class MoveJobProcessor(
    IMoveQueueService moveQueueService,
    IToastService toastService,
    IScanQueueService scanQueueService,
    ILogger<MoveJobProcessor> logger,
    AudiobookContentMoveService contentMoveService,
    IServiceScopeFactory scopeFactory,
    IAppMetricsService metrics,
    IFileSystemSemanticsResolver semanticsResolver,
    IMoveCleanupBoundaryResolver cleanupBoundaryResolver,
    IMoveScanHandoffStore moveScanHandoffStore,
    TimeProvider timeProvider,
    IFilesystemMutationCoordinator filesystemMutationCoordinator,
    IAudiobookOperationCoordinator audiobookOperationCoordinator,
    IAudiobookUpdatePublisher? audiobookUpdatePublisher = null) : IMoveJobProcessor, IMoveJobProcessorPhases
{
    internal Func<MoveJob, Task>? AfterSourceCleanupBeforeMetadataRewriteForTest { get; set; }

    public async Task ProcessJobAsync(MoveJob job, CancellationToken stoppingToken)
    {
        var postCommit = await ProcessDurableJobAsync(job, stoppingToken);
        if (postCommit != null)
        {
            await RunPostCompletionEffectsAsync(postCommit, stoppingToken);
        }
    }

    public async Task<MovePostCommitContext?> ProcessDurableJobAsync(
        MoveJob job,
        CancellationToken stoppingToken)
    {
        MovePostCommitContext? postCommit = null;
        void RegisterPostCommit(MovePostCommitContext context) => postCommit = context;

        if (string.IsNullOrWhiteSpace(job.LeaseOwner))
        {
            throw new MoveLeaseLostException(job.Id, job.LeaseGeneration);
        }

        await moveQueueService.UpdateJobStatusAsync(
            job.Id,
            job.LeaseOwner,
            job.LeaseGeneration,
            MoveJobStatus.Running,
            cancellationToken: stoppingToken);
        job.Status = MoveJobStatus.Running;
        job.Error = null;

        await filesystemMutationCoordinator.ExecuteExclusiveAsync(
            globalToken => audiobookOperationCoordinator.ExecuteExclusiveAsync(
                job.AudiobookId,
                token => ProcessJobCoreAsync(job, RegisterPostCommit, token),
                globalToken),
            stoppingToken);

        if (postCommit == null)
        {
            try
            {
                await moveQueueService.NotifyPersistedJobStateAsync(
                    job.Id,
                    job.Status,
                    job.Error,
                    stoppingToken);
            }
            catch (OperationCanceledException exception)
            {
                logger.LogDebug(
                    exception,
                    "Move job {JobId} state was already persisted before terminal notification cancellation",
                    job.Id);
            }
        }

        return postCommit;
    }
}
