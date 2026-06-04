using Listenarr.Domain.Models;

namespace Listenarr.Tests.Builders
{
    public class SearchResultBuilder
    {
        private SearchResult _searchResult = new();

        public SearchResultBuilder()
        {
            _searchResult.Id = Guid.NewGuid().ToString();
        }

        public SearchResultBuilder WithTorrentUrl(string value)
        {
            _searchResult.TorrentUrl = value;
            return this;
        }

        public SearchResultBuilder WithMagnetUrl(string value)
        {
            _searchResult.MagnetLink = value;
            return this;
        }

        public SearchResultBuilder WithTorrentData(byte[] value)
        {
            _searchResult.TorrentFileContent = value;
            return this;
        }

        public SearchResult Build()
        {
            return _searchResult;
        }
    }
}
