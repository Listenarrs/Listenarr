using Listenarr.Domain.Models;

namespace Listenarr.Tests.Builders
{
    public class AudiobookBuilder
    {
        private static int IdCounter = 0;

        private readonly Audiobook _audiobook = new();

        public AudiobookBuilder()
        {
            _audiobook.Id = ++IdCounter;
            _audiobook.Authors = [];
        }

        public AudiobookBuilder WithId(int value)
        {
            _audiobook.Id = value;
            return this;
        }

        public AudiobookBuilder WithBasePath(string value)
        {
            _audiobook.BasePath = value;
            return this;
        }

        public AudiobookBuilder WithTitle(string value)
        {
            _audiobook.Title = value;
            return this;
        }

        public AudiobookBuilder WithAuthor(string value)
        {
            _audiobook.Authors.Add(value);
            return this;
        }

        public AudiobookBuilder WithSeries(string value)
        {
            _audiobook.Series = value;
            return this;
        }

        public AudiobookBuilder WithYear(string value)
        {
            _audiobook.PublishYear = value;
            return this;
        }

        public AudiobookBuilder WithPublishedDate(DateOnly value)
        {
            _audiobook.PublishYear = value.Year.ToString();
            _audiobook.PublishedDate = value.ToString();
            return this;
        }

        public Audiobook Build()
        {
            return _audiobook;
        }
    }
}
