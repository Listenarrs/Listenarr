using System.Net;
using System.Text;
using Listenarr.Api.Controllers;
using Listenarr.Application.Downloads;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Application.Notification;
using Listenarr.Domain.Models;
using Listenarr.Domain.Models.Configurations;
using Listenarr.Domain.Models.Enumerations;
using Listenarr.Infrastructure.Adapters;
using Listenarr.Infrastructure.Search.Providers;
using Listenarr.Tests.Builders;
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

        /// <summary>
        /// Allows to replace a given tag with a path that has been processed to be portable
        /// </summary>
        /// <param name="response"></param>
        /// <param name="pattern"></param>
        /// <param name="path">Supports path gotten through FileUtils.GetAbsolutePath for example</param>
        /// <returns></returns>
        public static string PutPathInResponse(string response, string pattern, string path)
        {
            path = path.Replace(@"\", @"\\");
            return response.Replace(pattern, path);
        }

        public static Mock<DownloadMonitorService> GetDownloadMonitorServiceMock()
        {
            var scopeFactoryMock = new Mock<IServiceScopeFactory>();
            var scopeMock = new Mock<IServiceScope>();
            var serviceProviderMock = new Mock<IServiceProvider>();

            scopeFactoryMock.Setup(x => x.CreateScope()).Returns(scopeMock.Object);
            scopeMock.Setup(x => x.ServiceProvider).Returns(serviceProviderMock.Object);

            return new Mock<DownloadMonitorService>(
                scopeFactoryMock.Object,
                new Mock<IHubContext<DownloadHub>>().Object,
                new Mock<ILogger<DownloadMonitorService>>().Object,
                new Mock<IHttpClientFactory>().Object,
                new Mock<IAppMetricsService>().Object);
        }

        public static ServiceProvider CreateServiceProvider(string outputPath = "")
        {
            return CreateServiceProvider(new Mock<IDownloadItemService>().Object, outputPath);
        }

        public static ServiceProvider CreateServiceProvider(IDownloadItemService importItemResolutionService, string outputPath = "", DownloadClientConfiguration downloadClientConfiguration = null)
        {

            var configMock = new Mock<IConfigurationService>();
            configMock.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(new ApplicationSettings
            {
                OutputPath = outputPath,
                CompletedFileAction = FileAction.Copy,
                EnableMetadataProcessing = false,
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}-{ChapterNumber:00}",
            });
            if (downloadClientConfiguration != null)
            {
                configMock.Setup(c => c.GetDownloadClientConfigurationsAsync()).ReturnsAsync([
                    downloadClientConfiguration
                ]);
                configMock.Setup(c => c.GetDownloadClientConfigurationAsync(It.IsAny<string>())).ReturnsAsync(downloadClientConfiguration);
            }

            var services = new ServiceCollectionBuilder().Build();
            services.AddSingleton(configMock.Object);
            services.AddSingleton(importItemResolutionService);

            return services.BuildServiceProvider();
        }

        public static async Task<DownloadProcessingJob> CreateDownloadProcessingJob(ServiceProvider provider, Download download, string sourcePath)
        {
            var queueService = provider.GetRequiredService<IDownloadProcessingJobService>();
            var jobId = await queueService.EnqueueAsync(download);
            var job = await queueService.GetJobAsync(jobId);

            // Set job to processing (the outer loop normally does this)
            job.Status = ProcessingJobStatus.Processing;
            job.StartedAt = DateTime.UtcNow;
            await queueService.UpdateJobAsync(job);

            return job;
        }

        public static TransmissionAdapter CreateTransmissionAdapter(ServiceProvider provider, Mock<ITorrentFileDownloader>? torrentFileDOwnloader = null)
        {
            if (torrentFileDOwnloader == null)
            {
                torrentFileDOwnloader = new Mock<ITorrentFileDownloader>();
            }

            return new TransmissionAdapter(
                provider.GetRequiredService<IHttpClientFactory>(),
                torrentFileDOwnloader.Object,
                provider.GetRequiredService<ILogger<TransmissionAdapter>>());
        }

        public static SabnzbdAdapter CreateSabnzbdAdapter(ServiceProvider provider)
        {
            return new SabnzbdAdapter(
                provider.GetRequiredService<IHttpClientFactory>(),
                Mock.Of<INzbUrlResolver>(),
                new Mock<ILogger<SabnzbdAdapter>>().Object,
                new Mock<IAppMetricsService>().Object);
        }

        public static NzbgetAdapter CreateNzbgetAdapter(ServiceProvider provider)
        {
            return new NzbgetAdapter(
                provider.GetRequiredService<IHttpClientFactory>(),
                Mock.Of<INzbUrlResolver>(),
                new Mock<ILogger<NzbgetAdapter>>().Object);
        }

        public static MyAnonamouseSearchProvider CreateMyAnonamouseSearchProvider(ServiceProvider provider)
        {
            var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient();
            return new MyAnonamouseSearchProvider(
                provider.GetRequiredService<ILogger<MyAnonamouseSearchProvider>>(),
                httpClient,
                provider.GetRequiredService<IIndexerRepository>());
        }

        public static DownloadsController CreateDownloadsController(ServiceProvider provider)
        {
            return new DownloadsController(
                provider.GetRequiredService<IDownloadRepository>(),
                provider.GetRequiredService<IDownloadService>(),
                provider.GetRequiredService<ILogger<DownloadsController>>(),
                provider.GetRequiredService<IConfigurationService>(),
                provider.GetRequiredService<IMemoryCache>());
        }

        public static IndexersController CreateIndexersController(ServiceProvider provider, HttpMessageHandler handler)
        {
            return new IndexersController(
                provider.GetRequiredService<IIndexerRepository>(),
                provider.GetRequiredService<ILogger<IndexersController>>(),
                new HttpClient(handler),
                provider.GetRequiredService<IConfigurationService>());
        }
    }
}
