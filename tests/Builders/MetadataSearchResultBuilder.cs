namespace Listenarr.Tests.Builders
{
    public class MetadataSearchResultBuilder
    {
        private readonly MetadataSearchResult _metadataSearchResult = new()
        {
            Id = Guid.NewGuid().ToString(),
            Asin = "B0TESTASIN",
            Title = "Test Audiobook",
            Artist = "Test Author",
            Source = "Audible",
            MetadataSource = "Audible"
        };

        public MetadataSearchResultBuilder WithAsin(string value)
        {
            _metadataSearchResult.Asin = value;
            return this;
        }

        public MetadataSearchResultBuilder WithTitle(string value)
        {
            _metadataSearchResult.Title = value;
            return this;
        }

        public MetadataSearchResultBuilder WithArtist(string value)
        {
            _metadataSearchResult.Artist = value;
            return this;
        }

        public MetadataSearchResultBuilder WithSource(string value)
        {
            _metadataSearchResult.Source = value;
            return this;
        }

        public MetadataSearchResultBuilder WithMetadataSource(string value)
        {
            _metadataSearchResult.MetadataSource = value;
            return this;
        }

        public MetadataSearchResultBuilder WithSeries(string value)
        {
            _metadataSearchResult.Series = value;
            return this;
        }

        public MetadataSearchResultBuilder WithProductUrl(string value)
        {
            _metadataSearchResult.ProductUrl = value;
            return this;
        }

        public MetadataSearchResultBuilder WithImageUrl(string value)
        {
            _metadataSearchResult.ImageUrl = value;
            return this;
        }

        public MetadataSearchResultBuilder WithEnriched()
        {
            _metadataSearchResult.IsEnriched = true;
            return this;
        }

        public MetadataSearchResult Build()
        {
            return _metadataSearchResult;
        }
    }
}
