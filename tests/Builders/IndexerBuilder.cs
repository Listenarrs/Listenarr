using System.Text.Json;
using Listenarr.Domain.Models;

namespace Listenarr.Tests.Builders
{
    public class IndexerBuilder
    {
        private static int IdCounter = 0;

        private readonly Indexer _indexer = new();
        private Dictionary<string, string> _additionalSettings = [];

        public IndexerBuilder()
        {
            _indexer.Id = ++IdCounter;
            _indexer.Name = "";
            _indexer.Type = "torrent";
            _indexer.Implementation = "Custom";
            _indexer.Url = "";
            _indexer.ApiKey = "";
            _indexer.IsEnabled = true;
            _indexer.EnableInteractiveSearch = true;
            _indexer.AdditionalSettings = "{}";
        }

        public IndexerBuilder WithId(int value)
        {
            _indexer.Id = value;
            return this;
        }

        public IndexerBuilder WithName(string value)
        {
            _indexer.Name = value;
            return this;
        }

        public IndexerBuilder WithType(string value)
        {
            _indexer.Type = value;
            return this;
        }

        public IndexerBuilder WithImplementation(string value)
        {
            _indexer.Implementation = value;
            return this;
        }

        public IndexerBuilder WithUrl(string value)
        {
            _indexer.Url = value;
            return this;
        }

        public IndexerBuilder WithApiKey(string value)
        {
            _indexer.ApiKey = value;
            return this;
        }

        public IndexerBuilder WithEnabled()
        {
            _indexer.IsEnabled = true;
            return this;
        }

        public IndexerBuilder WithDisabled()
        {
            _indexer.IsEnabled = false;
            return this;
        }

        public IndexerBuilder WithInteractiveSearch()
        {
            _indexer.EnableInteractiveSearch = true;
            return this;
        }

        public IndexerBuilder WithoutInteractiveSearch()
        {
            _indexer.EnableInteractiveSearch = false;
            return this;
        }

        public IndexerBuilder WithSetting(string key, string value)
        {
            _additionalSettings[key] = value;
            return this;
        }

        public Indexer Build()
        {
            _indexer.AdditionalSettings = JsonSerializer.Serialize(_additionalSettings);

            return _indexer;
        }
    }
}
