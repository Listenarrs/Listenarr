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
    public class SeriesMonitoringBackgroundService(
        ILogger<SeriesMonitoringBackgroundService> logger,
        ISeriesMonitoringProcessor processor,
        IWorkerCycleRunner cycleRunner) : BackgroundService
    {
        private static readonly TimeSpan SyncInterval = TimeSpan.FromDays(1);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation(
                "SeriesMonitoringBackgroundService started. Monitored series will be checked every {Hours} hours",
                SyncInterval.TotalHours);

            await cycleRunner.RunPeriodicAsync(
                nameof(SeriesMonitoringBackgroundService),
                initialDelay: TimeSpan.FromMinutes(10),
                intervalProvider: () => SyncInterval,
                runCycle: processor.RunCycleAsync,
                stoppingToken);

            logger.LogInformation("SeriesMonitoringBackgroundService stopped");
        }
    }

    public class SeriesMonitoringProcessor(
        ILogger<SeriesMonitoringProcessor> logger,
        IServiceScopeFactory serviceScopeFactory) : ISeriesMonitoringProcessor
    {

        public async Task RunCycleAsync(CancellationToken cancellationToken)
        {
            using var scope = serviceScopeFactory.CreateScope();
            var monitoringService = scope.ServiceProvider.GetRequiredService<ISeriesMonitoringService>();
            var syncedCount = await monitoringService.SyncDueSeriesAsync(cancellationToken);
            logger.LogInformation(
                "SeriesMonitoringBackgroundService completed sync cycle. Synced {Count} monitored series",
                syncedCount);
        }
    }
}
