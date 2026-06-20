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
        ILogger<ScanBackgroundService> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation("ScanBackgroundService started");

            if (queue is not ScanQueueService scanQueue)
            {
                logger.LogWarning("ScanBackgroundService cannot read jobs because the configured queue is {QueueType}", queue.GetType().Name);
                return;
            }

            logger.LogInformation("ScanBackgroundService awaiting jobs from queue");
            try
            {
                await foreach (var job in scanQueue.Reader.ReadAllAsync(stoppingToken))
                {
                    logger.LogDebug("Dequeued scan job {JobId} from channel", job.Id);
                    await processor.ProcessJobAsync(job, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("ScanBackgroundService cancellation requested");
            }
            catch (OperationCanceledException ex)
            {
                logger.LogWarning(ex, "ScanBackgroundService job stream canceled/timed out unexpectedly; continuing");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex, "Unhandled error in ScanBackgroundService loop");
            }
        }
    }
}
