using System.Threading;
using System.Threading.Tasks;
using Listenarr.Api.Services.Adapters;
using Listenarr.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Listenarr.Api.Services
{
    /// <summary>
    /// Resolves import items with accurate paths and metadata.
    /// Matches ProvideImportItemService pattern.
    /// </summary>
    public interface IImportItemResolutionService
    {
        /// <summary>
        /// Resolves the import item by querying the download client.
        /// Called just before import to get the most accurate path.
        /// </summary>
        Task<QueueItem> ResolveImportItemAsync(
            Download download,
            QueueItem queueItem,
            QueueItem? previousAttempt = null,
            CancellationToken ct = default);
    }

    public class ImportItemResolutionService : IImportItemResolutionService
    {
        private readonly IConfigurationService _configurationService;
        private readonly IDownloadClientAdapterFactory _adapterFactory;
        private readonly ILogger<ImportItemResolutionService> _logger;

        public ImportItemResolutionService(
            IConfigurationService configurationService,
            IDownloadClientAdapterFactory adapterFactory,
            ILogger<ImportItemResolutionService> logger)
        {
            _configurationService = configurationService;
            _adapterFactory = adapterFactory;
            _logger = logger;
        }

        public async Task<QueueItem> ResolveImportItemAsync(
            Download download,
            QueueItem queueItem,
            QueueItem? previousAttempt = null,
            CancellationToken ct = default)
        {
            // Get the download client configuration
            var client = await _configurationService.GetDownloadClientConfigurationAsync(download.DownloadClientId);
            if (client == null)
            {
                _logger.LogWarning(
                    "Download {DownloadId} references unknown download client {ClientId}",
                    download.Id,
                    download.DownloadClientId);
                return queueItem; // Return original if client not found
            }

            // Skip resolution for disabled clients — don't contact the download client at all
            if (!client.IsEnabled)
            {
                _logger.LogDebug(
                    "Skipping import item resolution for download {DownloadId}: download client {ClientName} ({ClientId}) is disabled",
                    download.Id,
                    client.Name,
                    client.Id);
                return queueItem;
            }

            // Get the appropriate adapter for this client type
            var adapter = _adapterFactory.GetByIdOrType(client.Type);

            // Call the adapter's GetImportItemAsync to resolve the path
            _logger.LogDebug(
                "Resolving import item for download {DownloadId} using {ClientType} adapter",
                download.Id,
                client.Type);

            return await adapter.GetImportItemAsync(
                client,
                download,
                queueItem,
                previousAttempt,
                ct);
        }
    }
}
