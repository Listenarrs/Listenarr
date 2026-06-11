using System.Text.RegularExpressions;
using Listenarr.Tests.Builders;

namespace Listenarr.Tests.Mocks
{
    /// <summary>
    /// Flexible metadata mock that associate hardcoded metadata with given regex
    /// </summary>
    public class MetadataServiceMock : IMetadataService
    {
        private readonly Dictionary<string, AudioMetadata> metadataRegexMapping = [];

        public Task ApplyMetadataAsync(string filePath, AudioMetadata metadata)
        {
            throw new NotImplementedException();
        }

        public Task<byte[]?> DownloadCoverArtAsync(string coverArtUrl)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Adds a metadata that should be returned when the filepath match the given regex
        /// Warning: This does not make sure that the regex are mutualy exclusive
        /// </summary>
        /// <param name="regex"></param>
        /// <param name="metadata"></param>
        public void AddMetadata(string regex, AudioMetadata metadata)
        {
            metadataRegexMapping[regex] = metadata;
        }

        public async Task<AudioMetadata?> ExtractFileMetadataAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return null;
            }

            foreach (var mapping in metadataRegexMapping)
            {
                if (Regex.IsMatch(filePath, mapping.Key, RegexOptions.IgnoreCase))
                {
                    return mapping.Value;
                }
            }

            return new AudioMetadataBuilder()
                .WithTitle("Test Audiobook")
                .WithArtist("Test Author")
                .WithDuration(TimeSpan.FromSeconds(3600))
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
