
namespace Listenarr.Tests.Builders
{
    public class SeriesLookupItemBuilder
    {
        private readonly SeriesLookupItem _series = new();

        public SeriesLookupItemBuilder WithAsin(string? value)
        {
            _series.Asin = value;
            return this;
        }

        public SeriesLookupItemBuilder WithName(string value)
        {
            _series.Name = value;
            return this;
        }

        public SeriesLookupItemBuilder WithRegion(string value)
        {
            _series.Region = value;
            return this;
        }

        public SeriesLookupItem Build()
        {
            return _series;
        }
    }
}
