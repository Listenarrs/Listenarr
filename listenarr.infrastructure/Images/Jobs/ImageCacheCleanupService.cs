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

namespace Listenarr.Infrastructure.Images.Jobs
{
    /// <summary>
    /// Background service that runs daily to clean up temporary image cache
    /// </summary>
    public class ImageCacheCleanupService(
        ILogger<ImageCacheCleanupService> logger,
        IImageCacheCleanupProcessor processor,
        IWorkerCycleRunner cycleRunner,
        TimeProvider timeProvider) : BackgroundService
    {
        private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(24);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation("Image Cache Cleanup Service is starting");
            await cycleRunner.RunPeriodicAsync(
                nameof(ImageCacheCleanupService),
                initialDelay: GetDelayUntilMidnight(),
                intervalProvider: () => CleanupInterval,
                runCycle: processor.RunCycleAsync,
                stoppingToken);
        }

        private TimeSpan GetDelayUntilMidnight()
        {
            var now = timeProvider.GetLocalNow().DateTime;
            var tomorrow = now.Date.AddDays(1);
            var delay = tomorrow - now;
            logger.LogInformation("Waiting {Hours} hours and {Minutes} minutes until midnight for first cleanup",
                (int)delay.TotalHours, delay.Minutes);
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("Image Cache Cleanup Service is stopping");
            await base.StopAsync(cancellationToken);
        }
    }

    public class ImageCacheCleanupProcessor(
        IServiceScopeFactory scopeFactory,
        ILogger<ImageCacheCleanupProcessor> logger) : IImageCacheCleanupProcessor
    {
        public async Task RunCycleAsync(CancellationToken cancellationToken)
        {
            using var scope = scopeFactory.CreateScope();
            var imageCacheService = scope.ServiceProvider.GetRequiredService<IImageCacheService>();
            await imageCacheService.ClearTempCacheAsync();
            logger.LogInformation("Daily image cache cleanup completed successfully");
        }
    }
}
