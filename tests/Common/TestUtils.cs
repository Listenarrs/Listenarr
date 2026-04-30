using System.Reflection;
using Asp.Versioning.ApiExplorer;
using Listenarr.Api.Services;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Listenarr.Tests.Common
{
    public abstract class TestUtils
    {
        /// <summary>
        /// Resolves the versioned API base path (e.g. "/api/v1") from the test server's service provider.
        /// </summary>
        public static string ResolveApiBasePath(IServiceProvider services)
        {
            using var scope = services.CreateScope();
            var provider = scope.ServiceProvider.GetService<IApiVersionDescriptionProvider>();
            var groupName = provider?.ApiVersionDescriptions.FirstOrDefault(d => !d.IsDeprecated)?.GroupName
                ?? provider?.ApiVersionDescriptions.FirstOrDefault()?.GroupName;

            return string.IsNullOrWhiteSpace(groupName) ? "/api/v1" : $"/api/{groupName}";
        }

        public static DownloadProcessingBackgroundService GetDownloadProcessingBackgroundService()
        {
            return new DownloadProcessingBackgroundService(
                new Mock<IServiceScopeFactory>().Object,
                new Mock<ILogger<DownloadProcessingBackgroundService>>().Object,
                new Mock<IAppMetricsService>().Object);
        }

        public static async Task<DownloadProcessingJob?> ProcessJobAsync(DownloadProcessingBackgroundService downloadProcessingBackgroundService, ListenArrDbContext db, Download download, QueueItem item, DownloadClientConfiguration client)
        {
            using var memoryCache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(db);
            services.AddSingleton<IMemoryCache>(memoryCache);
            services.AddSingleton<IConfigurationService>(new Mock<IConfigurationService>().Object);
            services.AddSingleton<IDownloadService>(new Mock<IDownloadService>().Object);
            services.AddScoped<IRemotePathMappingService, RemotePathMappingService>();
            services.AddScoped<IDownloadProcessingQueueService, DownloadProcessingQueueService>();

            var provider = services.BuildServiceProvider();
            var queueService = provider.GetRequiredService<IDownloadProcessingQueueService>();

            // Enqueue the job pointing to the source file
            var jobId = await queueService.QueueDownloadProcessingAsync(download.Id, item.ContentPath, client.Id);
            var job = await queueService.GetJobAsync(jobId);

            using var scope = provider.CreateScope();
            var method = typeof(DownloadProcessingBackgroundService).GetMethod("ProcessJobAsync", BindingFlags.NonPublic | BindingFlags.Instance);

            // Invoke and await the returned Task
            var task = (Task)method!.Invoke(downloadProcessingBackgroundService, [job!, scope, CancellationToken.None])!;
            await task;

            job = await queueService.GetJobAsync(jobId);
            return job;
        }
    }
}
