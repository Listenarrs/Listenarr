namespace Listenarr.Tests.Builders
{
    public class AudiobookFileBuilder
    {
        private static int IdCounter = 0;

        private readonly AudiobookFile _audiobookFile = new();
        private Audiobook _audiobook = new AudiobookBuilder().Build();

        public AudiobookFileBuilder()
        {
            _audiobookFile.Id = ++IdCounter;
        }

        public AudiobookFileBuilder WithAudiobook(Audiobook value)
        {
            _audiobook = value;
            return this;
        }

        public AudiobookFileBuilder WithPath(string value)
        {
            _audiobookFile.Path = value;
            return this;
        }

        public AudiobookFileBuilder WithSize(long value)
        {
            _audiobookFile.Size = value;
            return this;
        }

        public AudiobookFileBuilder WithFormat(string value)
        {
            _audiobookFile.Format = value;
            return this;
        }

        public AudiobookFileBuilder WithCoded(string value)
        {
            _audiobookFile.Codec = value;
            return this;
        }

        public AudiobookFileBuilder WithBitrate(int value)
        {
            _audiobookFile.Bitrate = value;
            return this;
        }

        public AudiobookFileBuilder WithSampleRate(int value)
        {
            _audiobookFile.SampleRate = value;
            return this;
        }

        public AudiobookFile Build()
        {
            _audiobookFile.Audiobook = _audiobook;
            _audiobookFile.AudiobookId = _audiobook.Id;

            return _audiobookFile;
        }
    }
}
