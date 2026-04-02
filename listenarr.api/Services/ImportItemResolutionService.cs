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
        private readonly IDownloadClientGateway _clientGateway;
        private readonly ILogger<ImportItemResolutionService> _logger;

        public ImportItemResolutionService(
            IConfigurationService configurationService,
            IDownloadClientGateway clientGateway,
            ILogger<ImportItemResolutionService> logger)
        {
            _configurationService = configurationService;
            _clientGateway = clientGateway;
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

            return await _clientGateway.GetImportItemAsync(
                client,
                download,
                queueItem,
                previousAttempt,
                ct);
        }
    }
}
