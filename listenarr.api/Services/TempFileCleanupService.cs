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
namespace Listenarr.Api.Services
{
    /// <summary>
    /// Background service that runs periodically to clean up old temporary download files
    /// </summary>
    public class TempFileCleanupService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<TempFileCleanupService> _logger;
        private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(6); // Run every 6 hours

        public TempFileCleanupService(IServiceScopeFactory scopeFactory, ILogger<TempFileCleanupService> logger)
        {
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Temp File Cleanup Service is starting");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation("Running temp file cleanup at {Time}", DateTime.Now);

                    // Create a scope to resolve scoped services
                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var downloadService = scope.ServiceProvider.GetRequiredService<IDownloadService>();

                        // Clean up temp files older than 24 hours
                        if (downloadService is DownloadService ds)
                        {
                            ds.CleanupOldTempFiles(24);
                        }

                        _logger.LogInformation("Temp file cleanup completed successfully");
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Temp File Cleanup Service cancellation requested");
                    break;
                }
                catch (OperationCanceledException ex)
                {
                    _logger.LogWarning(ex, "Temp file cleanup operation canceled/timed out; continuing");
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogError(ex, "Error occurred during temp file cleanup");
                }

                // Wait until next cleanup interval
                try
                {
                    await Task.Delay(_cleanupInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Temp File Cleanup Service is stopping");
                    break;
                }
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Temp File Cleanup Service is stopping");
            await base.StopAsync(cancellationToken);
        }
    }
}
