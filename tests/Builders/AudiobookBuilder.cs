using Listenarr.Domain.Models;

namespace Listenarr.Tests.Builders
{
    public class AudiobookBuilder
    {
        private readonly Audiobook _audiobook = new();

        public AudiobookBuilder()
        {
            _audiobook.Id = 1;
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

        public AudiobookBuilder PublishedOn(DateOnly value)
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