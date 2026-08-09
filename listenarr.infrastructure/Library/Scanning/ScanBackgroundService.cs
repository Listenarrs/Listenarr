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

using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Scanning
{
    public class ScanBackgroundService(
        IScanQueueService queue,
        IScanJobProcessor processor,
        MoveScanHandoffRecoveryService moveHandoffRecoveryService,
        IWorkerCycleRunner cycleRunner,
        ILibraryFilesystemReadiness filesystemReadiness,
        ILogger<ScanBackgroundService> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation("ScanBackgroundService waiting for library filesystem initialization");
            await filesystemReadiness.WaitUntilReadyAsync(stoppingToken);
            logger.LogInformation("ScanBackgroundService started");

            logger.LogInformation("ScanBackgroundService awaiting jobs from queue");
            using var recoveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var recoveryTask = cycleRunner.RunPeriodicAsync(
                "move.scan.handoff.recovery",
                initialDelay: null,
                intervalProvider: static () => TimeSpan.FromSeconds(30),
                runCycle: moveHandoffRecoveryService.RecoverAsync,
                recoveryCancellation.Token);
            try
            {
                await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
                {
                    logger.LogDebug("Dequeued scan job {JobId} from channel", job.Id);
                    try
                    {
                        await processor.ProcessJobAsync(job, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (OperationCanceledException exception)
                    {
                        TryMarkJobFailed(job, exception);
                        logger.LogWarning(
                            exception,
                            "Scan job {JobId} was canceled unexpectedly; continuing with later jobs",
                            job.Id);
                    }
                    catch (Exception exception) when (WorkerExceptionClassifier.IsNonFatal(exception))
                    {
                        TryMarkJobFailed(job, exception);
                        logger.LogError(
                            exception,
                            "Unhandled error processing scan job {JobId}; continuing with later jobs",
                            job.Id);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("ScanBackgroundService cancellation requested");
            }
            catch (OperationCanceledException ex)
            {
                logger.LogWarning(ex, "ScanBackgroundService job stream canceled unexpectedly");
            }
            catch (Exception ex) when (WorkerExceptionClassifier.IsNonFatal(ex))
            {
                logger.LogError(ex, "Unhandled error reading the ScanBackgroundService job stream");
            }
            finally
            {
                await recoveryCancellation.CancelAsync();
                try
                {
                    await recoveryTask;
                }
                catch (OperationCanceledException) when (recoveryCancellation.IsCancellationRequested)
                {
                    logger.LogDebug("Move scan handoff recovery stopped");
                }
            }
        }

        private void TryMarkJobFailed(ScanJob job, Exception processingException)
        {
            try
            {
                if (queue.TryGetJob(job.Id, out var current)
                    && current != null
                    && (string.Equals(current.Status, "Completed", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(current.Status, "Failed", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(current.Status, "Superseded", StringComparison.OrdinalIgnoreCase)))
                {
                    logger.LogDebug(
                        "Preserved authoritative terminal status {Status} for scan job {JobId} after a later processor exception",
                        current.Status,
                        job.Id);
                    return;
                }

                queue.UpdateJobStatus(job.Id, "Failed", processingException.Message);
            }
            catch (Exception statusException) when (WorkerExceptionClassifier.IsNonFatal(statusException))
            {
                logger.LogWarning(
                    statusException,
                    "Failed to update scan job {JobId} after its processor failed; continuing with later jobs",
                    job.Id);
            }
        }

    }
}
