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

namespace Listenarr.Infrastructure.Downloads.Processing
{
    /// <summary>
    /// Schedules retention cleanup for terminal download-processing jobs.
    /// This is intentionally separate from <see cref="DownloadProcessingJobProcessor" />:
    /// import execution owns file movement and finalization, while this worker only prunes
    /// old Completed/Failed job rows so the processing table does not grow unbounded.
    /// </summary>
    public sealed class DownloadProcessingJobCleanupService(
        IDownloadProcessingJobCleanupProcessor processor,
        IWorkerCycleRunner cycleRunner,
        ILogger<DownloadProcessingJobCleanupService> logger) : BackgroundService
    {
        private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan CleanupInterval = TimeSpan.FromDays(1);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation("Download processing job cleanup worker started");

            await cycleRunner.RunPeriodicAsync(
                nameof(DownloadProcessingJobCleanupService),
                InitialDelay,
                () => CleanupInterval,
                processor.RunCycleAsync,
                stoppingToken);

            logger.LogInformation("Download processing job cleanup worker stopped");
        }
    }

    public sealed class DownloadProcessingJobCleanupProcessor(
        IServiceScopeFactory scopeFactory,
        ILogger<DownloadProcessingJobCleanupProcessor> logger) : IDownloadProcessingJobCleanupProcessor
    {
        private const int RetentionDays = 7;

        /// <summary>
        /// Runs one cleanup cycle. The processor resolves the scoped application service per cycle
        /// so the hosted service remains a scheduling adapter and the application service owns the
        /// retention policy. Broad exceptions are not caught here because <see cref="IWorkerCycleRunner" />
        /// owns non-fatal failure logging/metrics and will retry on the next interval.
        /// </summary>
        public async Task RunCycleAsync(CancellationToken cancellationToken = default)
        {
            using var scope = scopeFactory.CreateScope();
            var processingJobService = scope.ServiceProvider.GetRequiredService<IDownloadProcessingJobService>();

            logger.LogInformation(
                "Cleaning up terminal download processing jobs older than {RetentionDays} days",
                RetentionDays);

            await processingJobService.CleanupOldJobsAsync(RetentionDays, cancellationToken);
        }
    }
}
