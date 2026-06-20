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

namespace Listenarr.Infrastructure.Library.Monitoring
{
    public class AuthorMonitoringBackgroundService(
        ILogger<AuthorMonitoringBackgroundService> logger,
        IAuthorMonitoringProcessor processor,
        IWorkerCycleRunner cycleRunner) : BackgroundService
    {
        private static readonly TimeSpan SyncInterval = TimeSpan.FromDays(1);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation(
                "AuthorMonitoringBackgroundService started. Monitored authors will be checked every {Hours} hours",
                SyncInterval.TotalHours);

            await cycleRunner.RunPeriodicAsync(
                nameof(AuthorMonitoringBackgroundService),
                initialDelay: TimeSpan.FromMinutes(10),
                intervalProvider: () => SyncInterval,
                runCycle: processor.RunCycleAsync,
                stoppingToken);

            logger.LogInformation("AuthorMonitoringBackgroundService stopped");
        }
    }

    public class AuthorMonitoringProcessor(
        ILogger<AuthorMonitoringProcessor> logger,
        IServiceScopeFactory serviceScopeFactory) : IAuthorMonitoringProcessor
    {

        public async Task RunCycleAsync(CancellationToken cancellationToken)
        {
            using var scope = serviceScopeFactory.CreateScope();
            var monitoringService = scope.ServiceProvider.GetRequiredService<IAuthorMonitoringService>();
            var syncedCount = await monitoringService.SyncDueAuthorsAsync(cancellationToken);
            logger.LogInformation(
                "AuthorMonitoringBackgroundService completed sync cycle. Synced {Count} monitored author(s)",
                syncedCount);
        }
    }
}
