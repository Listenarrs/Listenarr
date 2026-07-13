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
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Persistence
{
    /// <summary>
    /// Runs once at startup to idempotently normalize legacy JSON-backed TEXT columns
    /// so that collection properties are stored as JSON arrays (not primitive roots).
    /// This is safe to run repeatedly and will not modify already-correct rows.
    /// </summary>
    public class StartupDbNormalizer : IHostedService
    {
        private readonly IServiceProvider _provider;
        private readonly ILogger<StartupDbNormalizer> _logger;

        public StartupDbNormalizer(IServiceProvider provider, ILogger<StartupDbNormalizer> logger)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            using var scope = _provider.CreateScope();

            // Each pass is guarded independently: a failure normalizing legacy columns must not
            // prevent seeding the default quality profile, which fresh installs rely on to search at all.
            await NormalizeJsonColumnsAsync(scope, cancellationToken);
            await SeedDefaultQualityProfileAsync(scope, cancellationToken);
        }

        private async Task NormalizeJsonColumnsAsync(IServiceScope scope, CancellationToken cancellationToken)
        {
            try
            {
                var audiobookRepository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
                await audiobookRepository.NormalizeJsonColumnsAsync(cancellationToken);
                _logger.LogInformation("StartupDbNormalizer: normalization pass complete.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Debug.WriteLine("Suppressed non-fatal exception in catch block.");
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogWarning(ex, "StartupDbNormalizer: operation canceled/timed out; skipping normalization pass");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "StartupDbNormalizer: unexpected error while running normalization");
            }
        }

        private async Task SeedDefaultQualityProfileAsync(IServiceScope scope, CancellationToken cancellationToken)
        {
            try
            {
                var qualityProfileRepository = scope.ServiceProvider.GetRequiredService<IQualityProfileRepository>();
                var seededDefaultProfile = await qualityProfileRepository.SeedDefaultProfileIfMissingAsync(cancellationToken);
                if (seededDefaultProfile)
                {
                    _logger.LogInformation("StartupDbNormalizer: seeded default quality profile (none existed).");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Debug.WriteLine("Suppressed non-fatal exception in catch block.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "StartupDbNormalizer: unexpected error while seeding default quality profile");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
