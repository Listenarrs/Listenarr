// csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Listenarr.Api.Services;

namespace Listenarr.Api.Extensions
{
    /// <summary>
    /// Registers hosted/background services and their supporting singletons/queues.
    /// Extracted from Program.cs so startup focuses on wiring modules, and hosted-worker
    /// surface is discoverable/testable and easy to disable in tests.
    /// </summary>
    public static class HostedServiceRegistrationExtensions
    {
        public static IServiceCollection AddListenarrHostedServices(this IServiceCollection services, IConfiguration config)
        {
            // Scan queue: enqueue folder scans to be processed in the background
            services.AddSingleton<IScanQueueService, ScanQueueService>();
            // Background worker to consume scan jobs and persist audiobook files
            services.AddHostedService<ScanBackgroundService>();

            // Download processing channel (in-memory publish/subscribe to wake consumers)
            services.AddSingleton<DownloadProcessingChannel>();
            services.AddHostedService<DownloadProcessingChannelConsumer>();

            // Move queue: enqueue safe move operations when an audiobook BasePath changes
            services.AddSingleton<IMoveQueueService, MoveQueueService>();
            // Background worker to consume move jobs and perform safe filesystem move
            services.AddHostedService<MoveBackgroundService>();

            // Register background service for daily cache cleanup
            services.AddHostedService<ImageCacheCleanupService>();

            // Register background service for temp file cleanup
            services.AddHostedService<TempFileCleanupService>();

            // Register background service for download monitoring and real-time updates
            services.AddHostedService<DownloadMonitorService>();

            // Register background service for completed download handling (import pipeline)
            // Implements CompletedDownloadService pattern for stability window validation
            services.AddHostedService<CompletedDownloadHandlingService>();

            // Register background service for queue monitoring (external clients) and real-time updates
            services.AddHostedService<QueueMonitorService>();

            // Register background service for automatic audiobook searching
            services.AddHostedService<AutomaticSearchService>();

            // Background installer for ffprobe - run in background so startup isn't blocked
            services.AddHostedService<FfmpegInstallBackgroundService>();

            // Background service to rescan files missing metadata
            services.AddHostedService<MetadataRescanService>();

            // Register background service for download processing queue
            services.AddHostedService<DownloadProcessingBackgroundService>();

            return services;
        }
    }
}
