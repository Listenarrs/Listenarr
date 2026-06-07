using Listenarr.Application.Interfaces;
using Listenarr.Application.Metadata;

namespace Listenarr.Tests.Builders
{
    public class SeriesCatalogFetchResultBuilder
    {
        private readonly SeriesCatalogFetchResult _result = new();

        public SeriesCatalogFetchResultBuilder WithSeries(string name, string? asin = null)
        {
            _result.Series = new SeriesLookupItemBuilder()
                .WithName(name)
                .WithAsin(asin)
                .Build();

            return this;
        }

        public SeriesCatalogFetchResultBuilder WithBook(AudibleSearchResult value)
        {
            _result.Books.Add(value);
            return this;
        }

        public SeriesCatalogFetchResult Build()
        {
            return _result;
        }
    }
}
