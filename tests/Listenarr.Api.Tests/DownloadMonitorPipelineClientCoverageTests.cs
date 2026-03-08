using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Listenarr.Api.Hubs;
using Listenarr.Api.Services;
using Listenarr.Domain.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Listenarr.Api.Tests
{
    public class DownloadMonitorPipelineClientCoverageTests
    {
        private static ListenArrDbContext CreateInMemoryDb()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new ListenArrDbContext(options);
        }

        [Theory]
        [InlineData("qbittorrent")]
        [InlineData("transmission")]
        [InlineData("sabnzbd")]
        [InlineData("nzbget")]
        public async Task FinalizeDownload_QueuesImport_ForAllSupportedClientTypes(string clientType)
        {
            await using var db = CreateInMemoryDb();
            var download = new Download
            {
                Id = $"dl-{clientType}",
                Title = "Pipeline Coverage",
                Status = DownloadStatus.Downloading,
                DownloadClientId = $"client-{clientType}",
                StartedAt = DateTime.UtcNow
            };
            db.Downloads.Add(download);
            await db.SaveChangesAsync();

            var sourceDir = Path.Join(Path.GetTempPath(), "listenarr-pipeline", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sourceDir);
            var sourceFile = Path.Join(sourceDir, "Pipeline Coverage.m4b");
            await File.WriteAllTextAsync(sourceFile, "dummy");

            var outputDir = Path.Join(Path.GetTempPath(), "listenarr-pipeline-out", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outputDir);

            var settings = new ApplicationSettings
            {
                OutputPath = outputDir,
                CompletedFileAction = "Move",
                EnableMetadataProcessing = false,
                AllowedFileExtensions = new List<string> { ".m4b" }
            };

            var services = new ServiceCollection();
            services.AddSingleton(db);

            var configMock = new Mock<IConfigurationService>();
            configMock.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(settings);
            services.AddSingleton(configMock.Object);

            var downloadServiceMock = new Mock<IDownloadService>();
            services.AddSingleton(downloadServiceMock.Object);

            var queuedSource = string.Empty;
            var queueMock = new Mock<IDownloadProcessingQueueService>();
            queueMock.Setup(q => q.GetJobsForDownloadAsync(It.IsAny<string>())).ReturnsAsync(new List<DownloadProcessingJob>());
            queueMock.Setup(q => q.QueueDownloadProcessingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .Callback<string, string, string>((downloadId, sourcePath, clientId) => queuedSource = sourcePath)
                .ReturnsAsync("job-1");
            services.AddSingleton(queueMock.Object);

            var importResolverMock = new Mock<IImportItemResolutionService>();
            importResolverMock
                .Setup(r => r.ResolveImportItemAsync(It.IsAny<Download>(), It.IsAny<QueueItem>(), It.IsAny<QueueItem>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Download dl, QueueItem item, QueueItem _, CancellationToken __) =>
                {
                    item.ContentPath = sourceFile;
                    return item;
                });
            services.AddSingleton(importResolverMock.Object);

            services.AddSingleton(new Mock<IFileNamingService>().Object);
            services.AddSingleton(new Mock<IMetadataService>().Object);

            using var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var hubContextMock = new Mock<IHubContext<DownloadHub>>();
            hubContextMock.SetupGet(h => h.Clients).Returns(new Mock<IHubClients>().Object);

            var httpFactoryMock = new Mock<IHttpClientFactory>();
            using var httpClient = new System.Net.Http.HttpClient();
            httpFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

            var monitor = new DownloadMonitorService(
                scopeFactory,
                hubContextMock.Object,
                new Mock<ILogger<DownloadMonitorService>>().Object,
                httpFactoryMock.Object);

            var finalizeMethod = typeof(DownloadMonitorService).GetMethod("FinalizeDownloadAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(finalizeMethod);

            var client = new DownloadClientConfiguration
            {
                Id = download.DownloadClientId,
                Name = clientType,
                Type = clientType,
                DownloadPath = sourceDir
            };

            var finalizeTask = (Task?)finalizeMethod!.Invoke(monitor, new object[] { download, sourceDir, client, CancellationToken.None });
            if (finalizeTask != null)
            {
                await finalizeTask;
            }

            queueMock.Verify(q => q.QueueDownloadProcessingAsync(download.Id, It.IsAny<string>(), client.Id), Times.Once);
            Assert.Equal(Path.GetFullPath(sourceFile), Path.GetFullPath(queuedSource), ignoreCase: true);
        }
    }
}
