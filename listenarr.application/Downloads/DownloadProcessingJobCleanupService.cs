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

using Listenarr.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Downloads
{
    /// <summary>
    /// Background service that periodically purges old completed/failed
    /// <c>DownloadProcessingJobs</c> rows. <see cref="IDownloadProcessingJobService.CleanupOldJobsAsync"/>
    /// already existed but had no caller, so the table grew unbounded on long-running
    /// instances and inflated every queue-snapshot reconciliation that queries it.
    /// Runs shortly after startup (to drain any accumulated backlog) and then daily.
    /// </summary>
    public class DownloadProcessingJobCleanupService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<DownloadProcessingJobCleanupService> _logger;

        // Terminal jobs older than this are eligible for deletion. Mirrors the existing
        // default on CleanupOldJobsAsync; kept as a constant to avoid a settings migration.
        internal const int RetentionDays = 7;

        private readonly TimeSpan _initialDelay = TimeSpan.FromMinutes(5);
        private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(24);

        public DownloadProcessingJobCleanupService(
            IServiceScopeFactory scopeFactory,
            ILogger<DownloadProcessingJobCleanupService> logger)
        {
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "DownloadProcessingJob cleanup service starting (retention {RetentionDays}d, interval {IntervalHours}h)",
                RetentionDays,
                _cleanupInterval.TotalHours);

            try
            {
                await Task.Delay(_initialDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                await RunCleanupAsync(stoppingToken);

                try
                {
                    await Task.Delay(_cleanupInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            _logger.LogInformation("DownloadProcessingJob cleanup service stopping");
        }

        /// <summary>
        /// Runs a single cleanup pass. Internal so it can be exercised directly in tests
        /// without driving the background loop.
        /// </summary>
        internal async Task RunCleanupAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var jobService = scope.ServiceProvider.GetRequiredService<IDownloadProcessingJobService>();
                await jobService.CleanupOldJobsAsync(RetentionDays);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error during DownloadProcessingJob cleanup pass");
            }
        }
    }
}
