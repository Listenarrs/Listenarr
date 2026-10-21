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

namespace Listenarr.Infrastructure.HostedServices.Search
{
    public class IndexerDomainRotationBackgroundService(
        ILogger<IndexerDomainRotationBackgroundService> logger,
        IIndexerDomainRotationProcessor processor,
        IWorkerCycleRunner cycleRunner) : BackgroundService
    {
        // ponytail: 6h interval hardcoded — promote to settings if users want to tune cadence
        private static readonly TimeSpan RotationInterval = TimeSpan.FromHours(6);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation(
                "IndexerDomainRotationBackgroundService started. Will check indexer domains every {Hours} hours",
                RotationInterval.TotalHours);

            await cycleRunner.RunPeriodicAsync(
                nameof(IndexerDomainRotationBackgroundService),
                initialDelay: TimeSpan.FromMinutes(2),
                intervalProvider: () => RotationInterval,
                runCycle: processor.RunCycleAsync,
                stoppingToken);

            logger.LogInformation("IndexerDomainRotationBackgroundService stopped");
        }
    }

    public class IndexerDomainRotationProcessor(
        ILogger<IndexerDomainRotationProcessor> logger,
        IServiceScopeFactory serviceScopeFactory) : IIndexerDomainRotationProcessor
    {
        public Task RunCycleAsync(CancellationToken ct) => RunRotationCycleAsync(ct);

        private async Task RunRotationCycleAsync(CancellationToken ct)
        {
            logger.LogInformation("Starting indexer domain rotation cycle");

            using var scope = serviceScopeFactory.CreateScope();
            var indexerRepo = scope.ServiceProvider.GetRequiredService<IIndexerRepository>();
            var rotationService = scope.ServiceProvider.GetRequiredService<IIndexerDomainRotationService>();

            var enabledIndexers = await indexerRepo.GetEnabledAsync(isAutomaticSearch: false, ct);
            var catalogIndexers = enabledIndexers
                .Where(i => IndexerMirrorCatalog.IsKnown(i.Implementation))
                .ToList();

            if (catalogIndexers.Count == 0)
            {
                logger.LogDebug("No domain-rotating indexers found; skipping cycle");
                return;
            }

            logger.LogInformation("Checking reachability for {Count} domain-rotating indexer(s)", catalogIndexers.Count);

            foreach (var indexer in catalogIndexers)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    var result = await rotationService.EnsureReachableAsync(indexer, ct);
                    if (result.Changed)
                        logger.LogInformation("Indexer '{Name}': {Reason}", indexer.Name, result.Reason);
                    else
                        logger.LogDebug("Indexer '{Name}': {Reason}", indexer.Name, result.Reason);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    logger.LogError(ex, "Error during domain rotation check for indexer '{Name}' (ID {Id})",
                        indexer.Name, indexer.Id);
                }
            }

            logger.LogInformation("Indexer domain rotation cycle complete");
        }
    }
}
