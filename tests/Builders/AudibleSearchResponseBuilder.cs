namespace Listenarr.Tests.Builders
{
    public class AudibleSearchResponseBuilder
    {
        private readonly AudibleSearchResponse _audibleSearchResponse = new()
        {
            Results = new List<AudibleSearchResult>(),
            TotalResults = 0
        };

        public AudibleSearchResponseBuilder WithResult(AudibleSearchResult value)
        {
            _audibleSearchResponse.Results ??= new List<AudibleSearchResult>();
            _audibleSearchResponse.Results.Add(value);
            _audibleSearchResponse.TotalResults = _audibleSearchResponse.Results.Count;
            return this;
        }

        public AudibleSearchResponseBuilder WithTotalResults(int value)
        {
            _audibleSearchResponse.TotalResults = value;
            return this;
        }

        public AudibleSearchResponse Build()
        {
            return _audibleSearchResponse;
        }
    }
}
