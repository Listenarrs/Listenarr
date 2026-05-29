using Listenarr.Application.Downloads;
using Listenarr.Application.Interfaces;
using Listenarr.Domain.Models;
using Listenarr.Tests.Builders;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Application.Downloads
{
    [Trait("Name", "DownloadItemServiceTests")]
    [Trait("Category", "DownloadItemService")]
    public class DownloadItemServiceTests
    {
        [Fact]
        [Trait("Method", "GetImportItemAsync")]
        [Trait("Scenario", "Passes saved client content path to download client gateway")]
        public async Task GetImportItemAsync_PassesClientContentPath()
        {
            var client = new DownloadClientConfigurationBuilder().Build();
            var contentPath = "/downloads/books/Example Book";
            var download = new DownloadBuilder()
                .WithDownloadClientConfiguration(client)
                .WithTorrentHash("ABC123")
                .WithTitle("Example Book")
                .Build();
            download.Metadata["ClientContentPath"] = contentPath;

            QueueItem? capturedQueueItem = null;
            var configurationService = new Mock<IConfigurationService>();
            configurationService
                .Setup(s => s.GetDownloadClientConfigurationAsync(client.Id))
                .ReturnsAsync(client);

            var gateway = new Mock<IDownloadClientGateway>();
            gateway
                .Setup(g => g.GetQueueItemAsync(client, download, It.IsAny<QueueItem>(), It.IsAny<CancellationToken>()))
                .Callback<DownloadClientConfiguration, Download, QueueItem, CancellationToken>((configuredClient, matchedDownload, queueItem, cancellationToken) => capturedQueueItem = queueItem)
                .ReturnsAsync((DownloadClientConfiguration configuredClient, Download matchedDownload, QueueItem queueItem, CancellationToken cancellationToken) => queueItem);

            var service = new DownloadItemService(
                configurationService.Object,
                Mock.Of<ILogger<IDownloadItemService>>(),
                gateway.Object);

            var result = await service.GetImportItemAsync(download);

            Assert.NotNull(capturedQueueItem);
            Assert.Equal(contentPath, capturedQueueItem.ContentPath);
            Assert.Equal(contentPath, result.ContentPath);
        }
    }
}
