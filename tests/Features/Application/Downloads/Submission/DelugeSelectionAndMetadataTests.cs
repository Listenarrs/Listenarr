using Listenarr.Application.Downloads.Common;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Downloads.Submission
{
    public class DelugeSelectionAndMetadataTests : BaseTests
    {
        [Fact]
        public async Task GetAppropriateDownloadClientAsync_SelectsDeluge_WhenDelugeIsOnlyEnabledTorrentClient()
        {
            await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfiguration
            {
                Id = "deluge-client-1",
                Name = "Deluge Client",
                Type = "deluge",
                IsEnabled = true
            });

            var selector = _provider.GetRequiredService<DownloadClientSelector>();
            var clientId = await selector.GetAppropriateDownloadClientAsync(isTorrent: true);

            Assert.Equal("deluge-client-1", clientId);
        }

        [Fact]
        public void ApplyClientSpecificId_SetsTorrentHash_WhenClientTypeIsDeluge()
        {
            var download = new Download();
            var delugeClient = new DownloadClientConfiguration
            {
                Id = "deluge-1",
                Name = "Deluge",
                Type = "deluge"
            };

            DownloadClientMetadataUpdater.ApplyClientSpecificId(download, delugeClient, "HASH1234567890");

            Assert.NotNull(download.Metadata);
            Assert.Equal("HASH1234567890", download.Metadata["ClientDownloadId"]);
            Assert.Equal("HASH1234567890", download.Metadata["TorrentHash"]);
        }
    }
}
