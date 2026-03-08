using Listenarr.Api.Services;

namespace Listenarr.Api.Services
{
    /// <summary>
    /// Background service that runs daily to clean up temporary image cache
    /// </summary>
    public class ImageCacheCleanupService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<ImageCacheCleanupService> _logger;
        private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(24); // Run daily

        public ImageCacheCleanupService(IServiceScopeFactory scopeFactory, ILogger<ImageCacheCleanupService> logger)
        {
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Image Cache Cleanup Service is starting");

            // Wait until midnight for the first cleanup
            await WaitUntilMidnight(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation("Running daily image cache cleanup at {Time}", DateTime.Now);

                    // Create a scope to resolve scoped services
                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var imageCacheService = scope.ServiceProvider.GetRequiredService<IImageCacheService>();
                        await imageCacheService.ClearTempCacheAsync();
                        _logger.LogInformation("Daily image cache cleanup completed successfully");
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Image Cache Cleanup Service cancellation requested");
                    break;
                }
                catch (OperationCanceledException ex)
                {
                    _logger.LogWarning(ex, "Image cache cleanup operation canceled/timed out; continuing");
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                    _logger.LogError(ex, "Error occurred during image cache cleanup");
                }

                // Wait 24 hours until next cleanup
                try
                {
                    await Task.Delay(_cleanupInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Image Cache Cleanup Service is stopping");
                    break;
                }
            }
        }

        private async Task WaitUntilMidnight(CancellationToken stoppingToken)
        {
            var now = DateTime.Now;
            var tomorrow = now.Date.AddDays(1);
            var timeUntilMidnight = tomorrow - now;

            if (timeUntilMidnight.TotalSeconds > 0)
            {
                _logger.LogInformation("Waiting {Hours} hours and {Minutes} minutes until midnight for first cleanup",
                    (int)timeUntilMidnight.TotalHours, timeUntilMidnight.Minutes);

                try
                {
                    await Task.Delay(timeUntilMidnight, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Image Cache Cleanup Service is stopping before first cleanup");
                }
                catch (OperationCanceledException ex)
                {
                    _logger.LogWarning(ex, "Image Cache Cleanup Service startup delay canceled/timed out");
                }
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Image Cache Cleanup Service is stopping");
            await base.StopAsync(cancellationToken);
        }
    }
}

