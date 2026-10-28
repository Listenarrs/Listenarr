namespace Listenarr.Tests.Builders
{
    public class AudibleSearchResultBuilder
    {
        private readonly AudibleSearchResult _result = new()
        {
            Asin = "B0TESTASIN",
            Title = "Test Audiobook",
            Authors = [],
            Series = [],
            Language = "english",
            BookFormat = "unabridged"
        };

        public AudibleSearchResultBuilder WithAsin(string value)
        {
            _result.Asin = value;
            return this;
        }

        public AudibleSearchResultBuilder WithTitle(string value)
        {
            _result.Title = value;
            return this;
        }

        public AudibleSearchResultBuilder WithAuthor(string value)
        {
            _result.Authors ??= [];
            _result.Authors.Add(new AudibleAuthorBuilder().WithName(value).Build());
            return this;
        }

        public AudibleSearchResultBuilder WithLanguage(string? value)
        {
            _result.Language = value;
            return this;
        }

        public AudibleSearchResultBuilder WithSeries(
            string name,
            string? position = null,
            string? asin = null)
        {
            _result.Series ??= [];
            _result.Series.Add(new AudibleSeriesBuilder()
                .WithName(name)
                .WithPosition(position)
                .WithAsin(asin)
                .Build());

            return this;
        }

        public AudibleSearchResult Build()
        {
            return _result;
        }
    }
}
