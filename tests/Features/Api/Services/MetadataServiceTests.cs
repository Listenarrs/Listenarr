using Listenarr.Application.Interfaces;
using Listenarr.Domain.Models;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Listenarr.Tests.Features.Api.Services
{
    [Trait("Name", "MetadataServiceTests")]
    [Trait("Category", "MetadataService")]
    public class MetadataServiceTests : BaseTests
    {
        private readonly string CLIENT_CONFIG_ID = "empty-1";
        private readonly string DOWNLOAD_ID = "dl-1";
        private readonly int AUDIOBOOK_ID = 1;

        [Fact]
        [Trait("Method", "FetchMetadataAsync")]
        public async Task FetchMetadataAsync()
        {
            var sourceDirectory = FileService.GetTempDirectory("FetchMetadataAsync");
            var filePath = await FileService.GetFileAsync(sourceDirectory, "03 - Seconde Fondation Isaac Asimov.withmetadata.mp3");

            var client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithId(CLIENT_CONFIG_ID)
                .Build());

            var download = new DownloadBuilder()
                .WithId(DOWNLOAD_ID)
                .WithDownloadClientConfiguration(client)
                .WithUploader("AnotherOneBiteTheDust")
                .WithProtocol(DownloadProtocol.Torrent)
                .Build();

            var audiobook = new AudiobookBuilder()
                .WithId(AUDIOBOOK_ID)
                .WithTitle("Seconde Fondation")
                .WithAuthor("Isaac Asimov")
                .WithPublishedDate(new DateOnly(1996, 6, 1))
                .WithSeries("Le Cycle de Fondation")
                .Build();

            await _audiobookRepository.AddAsync(audiobook);
            await _downloadRepository.AddAsync(download);

            var job = new DownloadProcessingJob
            {
                SourcePath = filePath
            };

            var metadataService = _provider.GetRequiredService<IMetadataService>();
            var metadata = await metadataService.FetchMetadataAsync(job, download, audiobook, default);

            Assert.NotNull(metadata);
            Assert.Equal("Le Cycle de Fondation", metadata.Series);
            Assert.Equal("Seconde Fondation", metadata.Title);
            Assert.Equal("Isaac Asimov", metadata.Artist);
            Assert.Equal("Isaac Asimov", metadata.AlbumArtist);
            Assert.Equal(1996, metadata.Year);
            Assert.Equal(3, metadata.TrackNumber);
            Assert.Equal(1, metadata.DiscNumber);
        }
    }
}
