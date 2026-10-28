namespace Listenarr.Tests.Builders
{
    public class AudibleBookResponseBuilder
    {
        private readonly AudibleBookResponse _audibleBookResponse = new()
        {
            Asin = "B0TESTASIN",
            Title = "Test Audiobook",
            Authors = new List<AudibleAuthor> { new() { Asin = "A0TEST", Name = "Test Author", Region = "us" } },
            Narrators = new List<AudibleNarrator> { new() { Name = "Test Narrator" } },
            Genres = new List<AudibleGenre> { new() { Asin = "G0TEST", Name = "Fiction", Type = "Fiction" } },
            Series = new List<AudibleSeries> { new() { Asin = "S0TEST", Name = "Test Series", Position = "1" } },
            Region = "us",
            Language = "english",
            BookFormat = "unabridged",
            ImageUrl = "http://example.com/cover.jpg"
        };

        public AudibleBookResponseBuilder WithAsin(string value)
        {
            _audibleBookResponse.Asin = value;
            return this;
        }

        public AudibleBookResponseBuilder WithTitle(string value)
        {
            _audibleBookResponse.Title = value;
            return this;
        }

        public AudibleBookResponseBuilder WithRegion(string value)
        {
            _audibleBookResponse.Region = value;
            return this;
        }

        public AudibleBookResponseBuilder WithAuthor(string name, string? asin = null, string? region = null)
        {
            _audibleBookResponse.Authors = new List<AudibleAuthor> { new() { Asin = asin, Name = name, Region = region } };
            return this;
        }

        public AudibleBookResponseBuilder WithNarrator(string name)
        {
            _audibleBookResponse.Narrators = new List<AudibleNarrator> { new() { Name = name } };
            return this;
        }

        public AudibleBookResponseBuilder WithGenre(string asin, string name, string type)
        {
            _audibleBookResponse.Genres = new List<AudibleGenre> { new() { Asin = asin, Name = name, Type = type } };
            return this;
        }

        public AudibleBookResponseBuilder WithSeries(string asin, string name, string position)
        {
            _audibleBookResponse.Series = new List<AudibleSeries> { new() { Asin = asin, Name = name, Position = position } };
            return this;
        }

        public AudibleBookResponseBuilder WithLengthMinutes(int value)
        {
            _audibleBookResponse.LengthMinutes = value;
            return this;
        }

        public AudibleBookResponseBuilder WithReleaseDate(string value)
        {
            _audibleBookResponse.ReleaseDate = value;
            return this;
        }

        public AudibleBookResponseBuilder WithExplicit(bool value)
        {
            _audibleBookResponse.Explicit = value;
            return this;
        }

        public AudibleBookResponse Build()
        {
            return _audibleBookResponse;
        }
    }
}
