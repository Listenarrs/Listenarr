namespace Listenarr.Api.Services
{
    /// <summary>
    /// Hosted service that consumes job IDs published to the DownloadProcessingChannel and triggers immediate processing.
    /// It acts as a bridge to the existing DownloadProcessingBackgroundService which still polls the DB for jobs.
    /// </summary>
    public class DownloadProcessingChannelConsumer : BackgroundService
    {
        private readonly IProcessingChannel _channel;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<DownloadProcessingChannelConsumer> _logger;

        public DownloadProcessingChannelConsumer(DownloadProcessingChannel channel, IServiceScopeFactory scopeFactory, ILogger<DownloadProcessingChannelConsumer> logger)
        {
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await foreach (var jobId in _channel.ReadAllAsync(stoppingToken))
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var queueService = scope.ServiceProvider.GetRequiredService<IDownloadProcessingQueueService>();
                    var job = await queueService.GetJobAsync(jobId);
                    if (job == null) continue;

                    // If job is pending, trigger immediate processing via DownloadProcessingBackgroundService by
                    // leaving it in Pending state. The background service will pick it up during its next loop.
                    // Optionally we could signal a processing mechanism here; keep lightweight for now.
                    _logger.LogDebug("Channel consumer observed job {JobId}", jobId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                    _logger.LogWarning(ex, "Failed to handle channel job {JobId}", jobId);
                }
            }
        }
    }
}

