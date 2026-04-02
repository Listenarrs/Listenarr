namespace Listenarr.Api.Services.Adapters
{
    public class DownloadClientAdapterFactory : IDownloadClientAdapterFactory
    {
        private readonly Dictionary<string, List<IDownloadClientAdapter>> _byType;
        private readonly Dictionary<DownloadProtocol, List<IDownloadClientAdapter>> _byProtocol;

        public DownloadClientAdapterFactory(IEnumerable<IDownloadClientAdapter> adapters)
        {
            adapters = adapters?.ToList() ?? new List<IDownloadClientAdapter>();
            _byType = adapters
                .Where(a => !string.IsNullOrWhiteSpace(a.ClientType))
                .GroupBy(a => a.ClientType!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            
            _byProtocol = adapters
                .SelectMany(a => a.Protocols.Select(p => new { Protocol = p, Adapter = a }))
                .GroupBy(x => x.Protocol)
                .ToDictionary(
                    g => g.Key, 
                    g => g.Select(x => x.Adapter).ToList()
                );

            // Ensure one client type is always one adapter
            var duplicatedAdapters =_byType
                .Where(pair => pair.Value.Count > 1)
                .Select(pair => pair.Key);
            if (duplicatedAdapters.Count() > 0)
            {
                var duplicatedAdaptersString = string.Join(", ", duplicatedAdapters);
                throw new ArgumentException($"Multiple adapters found for the following client types: {duplicatedAdaptersString}. Each type must have only one adapter.");
            }
        }

        public IDownloadClientAdapter GetByType(string type)
        {
            if (string.IsNullOrWhiteSpace(type))
            {
                throw new InvalidOperationException("Adapter key not provided.");
            }

            if (!_byType.TryGetValue(type, out var adapters))
            {
                throw new InvalidOperationException($"No adapter of type '{type}' registered.");
            }

            if (adapters.Count > 1)
            {
                throw new InvalidOperationException($"Multiple adapters of type '{type}' registered: Each client type can only have one adapter.");
            }

            return adapters.First();
        }

        public List<IDownloadClientAdapter> GetByProtocol(DownloadProtocol protocol)
        {
            if (!_byProtocol.TryGetValue(protocol, out var adapters))
            {
                throw new InvalidOperationException($"No adapter implementing '{protocol}' registered.");
            }
            
            return adapters;
        }

        public List<string> GetClientTypeSupportingProtocol(DownloadProtocol protocol)
        {
            return [.. GetByProtocol(protocol).Select(a => a.ClientType)];
        }
    }
}
