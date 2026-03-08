using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Listenarr.Domain.Models;
using Listenarr.Api.Services.Adapters;
using Microsoft.Extensions.Logging;

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
            if (client == null)
            {
                throw new ArgumentNullException(nameof(client));
            }

            var attemptedKeys = new List<string?> { client.Id, client.Type };
            foreach (var key in attemptedKeys)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                try
                {
                    return _factory.GetByIdOrType(key);
                }
                catch (InvalidOperationException)
                {
                    // Try the next key.
                    continue;
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

        public Task<string?> AddAsync(DownloadClientConfiguration client, SearchResult result, CancellationToken ct = default)
        {
            var adapter = ResolveAdapter(client);
            return adapter.AddAsync(client, result, ct);
        }

        public Task<bool> RemoveAsync(DownloadClientConfiguration client, string id, bool deleteFiles = false, CancellationToken ct = default)
        {
            var adapter = ResolveAdapter(client);
            return adapter.RemoveAsync(client, id, deleteFiles, ct);
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

        public Task<bool> MarkItemAsImportedAsync(DownloadClientConfiguration client, string downloadId, CancellationToken ct = default)
        {
            var adapter = ResolveAdapter(client);
            return adapter.MarkItemAsImportedAsync(client, downloadId, ct);
        }
    }
}
