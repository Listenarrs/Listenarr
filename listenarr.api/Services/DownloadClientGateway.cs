using Listenarr.Api.Services.Adapters;
using Microsoft.AspNetCore.Authorization.Infrastructure;

namespace Listenarr.Api.Services
{
    public class DownloadClientGateway : IDownloadClientGateway
    {
        private readonly IDownloadClientAdapterFactory _factory;
        private readonly ILogger<DownloadClientGateway> _logger;

        public DownloadClientGateway(IDownloadClientAdapterFactory factory, ILogger<DownloadClientGateway> logger)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        private IDownloadClientAdapter ResolveAdapter(DownloadClientConfiguration client)
        {
            ArgumentNullException.ThrowIfNull(client);

            if (!string.IsNullOrWhiteSpace(client.Type))
            {
                try
                {
                    return _factory.GetByType(client.Type);
                }
                catch (InvalidOperationException)
                {
                }
            }

            var descriptor = !string.IsNullOrWhiteSpace(client.Name)
                ? $"{client.Name} ({client.Type ?? "unknown"})"
                : client.Type ?? client.Id ?? "unknown";

            var message = $"No download client adapter registered for {LogRedaction.SanitizeText(descriptor)}.";
            _logger.LogError(message);
            throw new InvalidOperationException(message);
        }

        public Task<(bool Success, string Message)> TestConnectionAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            var adapter = ResolveAdapter(client);
            return adapter.TestConnectionAsync(client, ct);
        }

        public Task<Download?> AddAsync(DownloadClientConfiguration client, IndexerSearchResult result, CancellationToken ct = default)
        {
            var adapter = ResolveAdapter(client);
            return adapter.AddAsync(client, result, ct);
        }

        public Task<bool> RemoveAsync(DownloadClientConfiguration client, Download download, bool deleteFiles = false, CancellationToken ct = default)
        {
            var adapter = ResolveAdapter(client);
            return adapter.RemoveAsync(client, download, deleteFiles, ct);
        }

        public Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            var adapter = ResolveAdapter(client);
            return adapter.GetQueueAsync(client, ct);
        }

        public Task<List<(string Id, string Name)>> GetRecentHistoryAsync(DownloadClientConfiguration client, int limit = 100, CancellationToken ct = default)
        {
            var adapter = ResolveAdapter(client);
            return adapter.GetRecentHistoryAsync(client, limit, ct);
        }

        public Task<bool> MarkItemAsImportedAsync(DownloadClientConfiguration client, Download download, CancellationToken ct = default)
        {
            var adapter = ResolveAdapter(client);
            return adapter.MarkItemAsImportedAsync(client, download, ct);
        }

        public Task<QueueItem> GetImportItemAsync(DownloadClientConfiguration client, Download download, QueueItem queueItem, QueueItem? previousAttempt = null, CancellationToken ct = default)
        {
            var adapter = ResolveAdapter(client);

            _logger.LogDebug(
                "Resolving import item for download {DownloadId} using {ClientType} adapter",
                download.Id,
                client.Type);

            return adapter.GetImportItemAsync(
                client,
                download,
                queueItem,
                previousAttempt,
                ct);
        }
    }
}
