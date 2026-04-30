using Listenarr.Domain.Models;

namespace Listenarr.Tests.Builders
{
    public class AudioMetadataBuilder
    {
        private readonly AudioMetadata _audioMetadata = new();

        public AudioMetadataBuilder WithTitle(string value)
        {
            _audioMetadata.Title = value;
            return this;
        }

        public AudioMetadataBuilder WithArtist(string value)
        {
            _audioMetadata.Artist = value;
            return this;
        }

        public AudioMetadataBuilder WithDuration(TimeSpan value)
        {
            _audioMetadata.Duration = value;
            return this;
        }

        public AudioMetadataBuilder WithFormat(string value)
        {
            _audioMetadata.Format = value;
            return this;
        }

        public AudioMetadataBuilder WithAlbum(string value)
        {
            _audioMetadata.Album = value;
            return this;
        }

        public AudioMetadataBuilder WithBitRate(int value)
        {
            _audioMetadata.BitRate = value;
            return this;
        }

        public AudioMetadataBuilder WithSampleRate(int value)
        {
            _audioMetadata.SampleRate = value;
            return this;
        }

        public AudioMetadataBuilder WithChannels(int value)
        {
            _audioMetadata.Channels = value;
            return this;
        }

        public AudioMetadataBuilder WithDisc(int value)
        {
            _audioMetadata.DiscNumber = value;
            return this;
        }

        public AudioMetadataBuilder WithTrack(int value)
        {
            _audioMetadata.TrackNumber = value;
            return this;
        }

        public AudioMetadataBuilder WithYear(int value)
        {
            _audioMetadata.Year = value;
            return this;
        }

        public AudioMetadata Build()
        {
            return _audioMetadata;
        }
    }
}
