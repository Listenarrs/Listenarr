using Listenarr.Api.Services.Metadata;
using Listenarr.Domain.Models;
using Listenarr.Tests.Builders;

namespace Listenarr.Tests.Mocks
{
    public class MetadataServiceMock : IMetadataService
    {
        public Task ApplyMetadataAsync(string filePath, AudioMetadata metadata)
        {
            throw new NotImplementedException();
        }

        public Task<byte[]?> DownloadCoverArtAsync(string coverArtUrl)
        {
            throw new NotImplementedException();
        }

        public async Task<AudioMetadata?> ExtractFileMetadataAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return null;
            }

            return new AudioMetadataBuilder()
                .WithTitle("Test Audiobook")
                .WithArtist("Test Author")
                .WithDuration(TimeSpan.FromSeconds(3600))
                .WithFormat("m4b")
                .WithBitRate(64000)
                .WithSampleRate(44100)
                .WithChannels(2)
                .Build();
        }

        public Task<AudioMetadata> FetchMetadataAsync(DownloadProcessingJob job, Download? download, Audiobook? audiobook, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<AudioMetadata?> GetMetadataAsync(string title, string? artist = null, string? isbn = null)
        {
            throw new NotImplementedException();
        }

        public Task WriteAsinTagAsync(string filePath, string asin)
        {
            throw new NotImplementedException();
        }
    }
}
