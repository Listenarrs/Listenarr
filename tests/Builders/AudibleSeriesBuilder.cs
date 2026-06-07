using Listenarr.Application.Metadata;

namespace Listenarr.Tests.Builders
{
    public class AudibleSeriesBuilder
    {
        private readonly AudibleSeries _series = new();

        public AudibleSeriesBuilder WithAsin(string? value)
        {
            _series.Asin = value;
            return this;
        }

        public AudibleSeriesBuilder WithName(string value)
        {
            _series.Name = value;
            return this;
        }

        public AudibleSeriesBuilder WithPosition(string? value)
        {
            _series.Position = value;
            return this;
        }

        public AudibleSeries Build()
        {
            return _series;
        }
    }
}
