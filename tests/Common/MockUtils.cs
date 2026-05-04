using System.Net;
using System.Text;
using Listenarr.Api.Controllers;
using Listenarr.Api.Hubs;
using Listenarr.Api.Services;
using Listenarr.Api.Services.Adapters;
using Listenarr.Api.Services.Search.Providers;
using Listenarr.Application.Repositories;
using Listenarr.Application.Services;
using Listenarr.Domain.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Listenarr.Tests.Common
{
    public abstract class MockUtils
    {
        /// <summary>
        /// Returns a 200 HTTP reply with the given content
        /// </summary>
        /// <param name="content">Content to use as a response body</param>
        /// <returns>HttpResponseMessage with status 200 and body</returns>
        public static HttpResponseMessage GetCannedResponse(string content, string mediaType = "application/json")
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, mediaType)
            };
        }

        public static async Task<DownloadProcessingJob> CreateDownloadProcessingJob(ServiceProvider provider, Download download, string sourcePath)
        {
            var queueService = provider.GetRequiredService<IDownloadProcessingQueueService>();
            var jobId = await queueService.QueueDownloadProcessingAsync(download.Id, sourcePath, null);
            var job = await queueService.GetJobAsync(jobId);

            // Set job to processing (the outer loop normally does this)
            job.Status = ProcessingJobStatus.Processing;
            job.StartedAt = DateTime.UtcNow;
            await queueService.UpdateJobAsync(job);

            return job;
        }

        public static SabnzbdAdapter CreateSabnzbdAdapter(ServiceProvider provider)
        {
            return new SabnzbdAdapter(
                provider.GetRequiredService<IHttpClientFactory>(),
                provider.GetRequiredService<IRemotePathMappingService>(),
                Mock.Of<INzbUrlResolver>(),
                new Mock<ILogger<SabnzbdAdapter>>().Object);
        }

        public static NzbgetAdapter CreateNzbgetAdapter(ServiceProvider provider)
        {
            return new NzbgetAdapter(
                provider.GetRequiredService<IHttpClientFactory>(),
                Mock.Of<INzbUrlResolver>(),
                provider.GetRequiredService<IRemotePathMappingService>(),
                new Mock<ILogger<NzbgetAdapter>>().Object);
        }

        public static CompletedDownloadProcessor CreateCompletedDownloadProcessor(ServiceProvider provider)
        {
            return new CompletedDownloadProcessor(
                provider.GetRequiredService<IDownloadRepository>(),
                provider.GetRequiredService<IFileFinalizer>(),
                provider.GetRequiredService<IConfigurationService>(),
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IImportService>(),
                provider.GetRequiredService<IArchiveExtractor>(),
                provider.GetRequiredService<IDownloadQueueService>(),
                provider.GetRequiredService<IHubContext<DownloadHub>>(),
                provider.GetRequiredService<ILogger<CompletedDownloadProcessor>>(),
                hubBroadcaster: null,
                metrics: null,
                provider.GetRequiredService<IDownloadHistoryService>());
        }

        public static MyAnonamouseSearchProvider CreateMyAnonamouseSearchProvider(ServiceProvider _provider)
        {
            var httpClientFactory = _provider.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient();
            return new MyAnonamouseSearchProvider(
                _provider.GetRequiredService<ILogger<MyAnonamouseSearchProvider>>(),
                httpClient,
                _provider.GetRequiredService<IIndexerRepository>());
        }

        public static DownloadsController CreateDownloadsController(ServiceProvider _provider)
        {
            return new DownloadsController(
                _provider.GetRequiredService<IDownloadRepository>(),
                _provider.GetRequiredService<ILogger<DownloadsController>>(),
                _provider.GetRequiredService<IConfigurationService>(),
                _provider.GetRequiredService<IMemoryCache>());
        }

        public static IndexersController CreateIndexersController(ServiceProvider _provider, HttpMessageHandler handler)
        {
            return new IndexersController(
                _provider.GetRequiredService<IIndexerRepository>(),
                _provider.GetRequiredService<ILogger<IndexersController>>(),
                new HttpClient(handler),
                _provider.GetRequiredService<IConfigurationService>());
        }
    }
}
