namespace Listenarr.Tests.Builders
{
    public class AudiobookBuilder
    {
        private readonly Audiobook _audiobook = new();

        public AudiobookBuilder()
        {
            _audiobook.Id = TestEntityIdGenerator.Next();
            _audiobook.Authors = [];
            _audiobook.Genres = [];
            _audiobook.AuthorAsins = [];
        }

        public AudiobookBuilder WithId(int value)
        {
            _audiobook.Id = TestEntityIdGenerator.Explicit(value);
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

        public AudiobookBuilder WithSeriesNumber(string value)
        {
            _audiobook.SeriesNumber = value;
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

        public AudiobookBuilder WithFilePath(string value)
        {
            _audiobook.FilePath = value;
            return this;
        }

        public AudiobookBuilder WithImageUrl(string value)
        {
            _audiobook.ImageUrl = value;
            return this;
        }

        public AudiobookBuilder WithMonitored(bool value = true)
        {
            _audiobook.Monitored = value;
            return this;
        }

        public AudiobookBuilder WithDescription(string value)
        {
            _audiobook.Description = value;
            return this;
        }

        public AudiobookBuilder WithSubtitle(string value)
        {
            _audiobook.Subtitle = value;
            return this;
        }

        public AudiobookBuilder WithFileSize(long value)
        {
            _audiobook.FileSize = value;
            return this;
        }

        public AudiobookBuilder WithOpenLibraryId(string value)
        {
            _audiobook.OpenLibraryId = value;
            return this;
        }

        public AudiobookBuilder WithGenre(string value)
        {
            (_audiobook.Genres ??= []).Add(value);
            return this;
        }

        public AudiobookBuilder WithAuthorAsin(string value)
        {
            (_audiobook.AuthorAsins ??= []).Add(value);
            return this;
        }

        public AudiobookBuilder WithQualityProfile(QualityProfile value)
        {
            _audiobook.QualityProfile = value;
            return this;
        }

        public Audiobook Build()
        {
            return _audiobook;
        }
    }
}
