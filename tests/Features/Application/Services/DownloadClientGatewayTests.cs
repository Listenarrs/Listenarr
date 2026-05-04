using Listenarr.Application.Interfaces;
using Listenarr.Domain.Common;
using Listenarr.Domain.Models;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Listenarr.Tests.Features.Application.Services
{
    [Trait("Name", "DownloadClientGatewayTests")]
    [Trait("Category", "DownloadClientGateway")]
    public class DownloadClientGatewayTests : BaseTests
    {
        private readonly string localMapping = FileUtils.GetAbsolutePath("mnt", "wdelements", "downloads");
        private readonly string localPath = null!;

        private IDownloadClientGateway downloadClientGateway = null!;
        private DownloadClientConfiguration client = null!;

        public DownloadClientGatewayTests()
        {
            localPath = Path.Join(localMapping, "complete", "audiobooks");
        }

        public override async Task InitializeAsync()
        {
            downloadClientGateway = _provider.GetRequiredService<IDownloadClientGateway>();

            client = new DownloadClientConfigurationBuilder()
                .WithType("mock")
                .Build();

            await _remotePathMappingRepository.SaveAsync(new RemotePathMappingBuilder()
                .WithDownloadClientConfiguration(client)
                .WithRemotePath(FileUtils.GetAbsolutePath("downloads"))
                .WithLocalPath(localMapping)
                .Build());
        }

        private async Task IsValid(QueueItem item)
        {
            Assert.StartsWith(DownloadCLientAdapterMock.RemotePath, item.RemotePath);
            Assert.StartsWith(localPath, item.LocalPath);

            foreach (string path in item.SourceFiles)
            {
                Assert.StartsWith(localPath, path);
            }
        }

        [Fact]
        [Trait("Method", "GetQueueItemAsync")]
        [Trait("Scenario", "Make sure GetQueueItemAsync returns a list of items with path mapped")]
        public async Task GetQueueItemAsync()
        {
            var item = await downloadClientGateway.GetQueueItemAsync(client, new DownloadBuilder().Build(), new QueueItem());
            await IsValid(item);
        }

        [Fact]
        [Trait("Method", "GetQueueAsync")]
        [Trait("Scenario", "Make sure GetQueueAsync returns one item with path mapped")]
        public async Task GetQueueAsync()
        {

            var items = await downloadClientGateway.GetQueueAsync(client);
            Assert.NotEmpty(items);

            foreach (QueueItem item in items)
            {
                await IsValid(item);
            }
        }

        [Fact]
        [Trait("Method", "TestConnectionAsync")]
        [Trait("Scenario", "Check that the selected mock is the right one and also TestConnectionAsync")]
        public async Task TestConnectionAsync()
        {
            var (success, message) = await downloadClientGateway.TestConnectionAsync(client);
            Assert.True(success);
            Assert.Equal("mock", message);
        }

        [Fact]
        [Trait("Method", "FetchDownloadsAsync")]
        [Trait("Scenario", "Check updated downloads a path mapped correctly")]
        public async Task FetchDownloadsAsync()
        {
            var downloads = await downloadClientGateway.FetchDownloadsAsync(client, []);
            Assert.NotEmpty(downloads);
            foreach (Download download in downloads)
            {
                Assert.StartsWith(localPath, download.DownloadPath);
            }
        }
    }
}
