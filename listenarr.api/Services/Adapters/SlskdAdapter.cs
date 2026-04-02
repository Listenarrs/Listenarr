using System.Text.Json;
using Listenarr.Api.Models.Slskd;
using Listenarr.Domain.Utils;
using Listenarr.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;
using Polly;
using Polly.Extensions.Http;

namespace Listenarr.Api.Services.Adapters
{
    public class SlskdAdapter : DownloadClientAdapter, IDownloadClientAdapter
    {
        // Waiting time after file system operations in seconds
        private readonly int PROCESSING_DELAY = 3;

        private const string CLIENT_TYPE = "slskd";
        public string ClientType => CLIENT_TYPE;
        public List<DownloadProtocol> Protocols => [DownloadProtocol.Soulseek];

        public SlskdAdapter(
            IDbContextFactory<ListenArrDbContext> dbFactory,
            HttpClient httpClient,
            ILogger<SlskdAdapter> logger) : base(dbFactory, httpClient, logger)
        {
            _dbFactory = dbFactory;
        }

        public static void AddToServices(IServiceCollection services)
        {
            services.AddScoped<IDownloadClientAdapter, SlskdAdapter>();
            services.AddHttpClient<SlskdAdapter>()
                .ConfigureHttpClient(client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(30);
                })
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler()
                {
                    AutomaticDecompression = System.Net.DecompressionMethods.All,
                    UseCookies = false
                })
                .SetHandlerLifetime(TimeSpan.FromMinutes(5))
                .AddPolicyHandler(HttpPolicyExtensions
                    .HandleTransientHttpError()
                    .CircuitBreakerAsync(3, TimeSpan.FromSeconds(30)))
                .AddPolicyHandler(HttpPolicyExtensions.HandleTransientHttpError()
                    .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))));
        }

        public async Task<(bool Success, string Message)> TestConnectionAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            var request = BuildHttpRequestMessage(HttpMethod.Get, client, "/api/v0/application/version");

            try
            {
                await Call(request);
            }
            catch(DownloadClientException exception)
            {
                return (false, exception.Message);
            }

            return (true, "Connected");
        }

        public async Task<Download?> AddAsync(DownloadClientConfiguration client, IndexerSearchResult result, CancellationToken ct = default)
        {
            var transferRequest = result.Files
                .Select(file => new TransferRequestItemDto {
                    Filename = file.Filename,
                    Size = file.Size
                })
                .ToList();

            var uploader = Uri.EscapeDataString(result.Uploader);
            var request = BuildHttpRequestMessage(HttpMethod.Post, client, "/api/v0/transfers/downloads/" + uploader);
            request.Content = JsonContent.Create(transferRequest);

            HttpContent content;
            try
            {
                content = await Call(request);
            }
            catch(DownloadClientException exception)
            {
                _logger.LogDebug(exception.Message);
                return null;
            }

            var transfer = await content.ReadFromJsonAsync<TransferResultDto>();
            if (transfer == null)
            {
                _logger.LogInformation("Unable to parse Slskd transfer reply: {Content}", content);
                return null;
            }
            
            var download = new Download(result, client);
            download.Metadata["Uploader"] = result.Uploader;
            download.SetClientDownloadId(new SlskdDownloadIdDto {
                Ids = transfer.Enqueued.Select(item => item.Id).ToArray()
            });

            if (transfer.Failed.Count > 0)
            {
                // Some file failed, cancel the whole thing
                _logger.LogInformation("Unable to start transfer for some of the files in {Title}, canceling the download...", result.Title);
                var cleaned = await RemoveAsync(client, download, true, ct);
                if (!cleaned)
                {
                    _logger.LogError("Unable to clean inconsistent download, manual cleanup in Slskd might be required for {Title}", result.Title);
                }
                return null;
            }
            return download;
        }

        public async Task<bool> RemoveAsync(DownloadClientConfiguration client, Download download, bool deleteFiles = false, CancellationToken ct = default)
        {
            SlskdDownloadIdDto slskdDownloadId = download.GetClientDownloadId<SlskdDownloadIdDto>();
            if (slskdDownloadId == null)
            {
                // Assume no ID to remove
                _logger.LogWarning("Unable to get download Slskd specific download IDs, assuming files have already been removed");
                return true;
            }

            var removeIdentifiers = slskdDownloadId.Ids;
            var removedCount = 0;
            var removedGoal = removeIdentifiers.Length;
            
            var uploader = download.GetMetadata<string>("Uploader");
            if (uploader == null)
            {
                // Cannot send remove transfer requests if uploader is unknown, manual remove required
                _logger.LogError("Unable to remove download from Slskd, uploader is unknown: Manual remove will be required");
                return true;
            }

            uploader = Uri.EscapeDataString(uploader);
            
            // Cancel each transfer
            foreach(string id in removeIdentifiers)
            {
                var request = BuildHttpRequestMessage(HttpMethod.Delete, client, "/api/v0/transfers/downloads/" + uploader + "/" + id, "remove=false");
                try
                {
                    await Call(request);
                }
                catch(DownloadClientException exception)
                {
                    _logger.LogDebug("Unable to cancel download {Id} from Slskd: {Message}", id, exception.Message);
                }
            }
            
            // Wait for Slskd internal processing
            await Task.Delay(TimeSpan.FromSeconds(PROCESSING_DELAY));
            
            // Remove each transfer
            foreach(string id in removeIdentifiers)
            {
                var request = BuildHttpRequestMessage(HttpMethod.Delete, client, "/api/v0/transfers/downloads/" + uploader + "/" + id, "remove=true");

                try
                {
                    await Call(request);
                    removedCount++;
                }
                catch(DownloadClientException exception)
                {
                    _logger.LogDebug("Unable to remove download {Id} from Slskd: {Message}", id, exception.Message);
                }
            }

            return removedGoal == removedCount;
        }

        public async Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            var request = BuildHttpRequestMessage(HttpMethod.Get, client, "/api/v0/transfers/downloads");

            HttpContent content;
            try
            {
                content = await Call(request);
            }
            catch(DownloadClientException exception)
            {
                _logger.LogDebug("Unable to get Slskd download queue:" + exception.Message, exception);
                return [];
            }

            var ongoingTransfers = await content.ReadFromJsonAsync<List<OngoingTransferDto>>();
            if (ongoingTransfers == null)
            {
                _logger.LogError("Unable to parse Slskd reply: {Message}" + content.ReadAsStream());
                return [];
            }

            // Get ongoing downloads for that client
            using var db = _dbFactory.CreateDbContext();
            var downloads = await db.Downloads
                .Where(download => download.DownloadClientId == client.Id)
                .ToListAsync();
                
            var downloadBasePath = await GetConfiguredDownloadPath(client, db);

            return [.. ongoingTransfers
                .SelectMany(user => user.Directories
                    .SelectMany<OngoingTransferDirectoryDto, QueueItem>(directory => {
                        // Links the directory download to a Listenarr download
                        List<string> fileIds = directory.Files.Select(file => file.Id).ToList();
                        var matchedDownload = downloads.FirstOrDefault(download => {
                            var clientDownloadId = download.GetClientDownloadId<SlskdDownloadIdDto>();
                            return clientDownloadId != null && clientDownloadId.Ids.Intersect(fileIds).Any();
                        });
                        if (matchedDownload == null) {
                            // This directory transfer is not monitored by Listenarr
                            return [];
                        }

                        var totalSize = directory.Files.Sum(file => file.Size);
                        var totalLeft = directory.Files.Sum(file => file.BytesRemaining);
                        var progress = (double)(totalSize - totalLeft) / totalSize * 100;

                        var states = directory.Files
                            .Select(file => mapSlskdStatusToDownloadItemStatus(file.State))
                            .ToList();
                        
                        var state = states.Contains(DownloadItemStatus.Removed)  ? DownloadItemStatus.Removed
                            : states.Contains(DownloadItemStatus.Failed)         ? DownloadItemStatus.Failed
                            : states.Contains(DownloadItemStatus.Unknown)        ? DownloadItemStatus.Unknown
                            : states.Contains(DownloadItemStatus.Downloading)    ? DownloadItemStatus.Downloading
                            : states.Contains(DownloadItemStatus.Queued)         ? DownloadItemStatus.Queued
                            : DownloadItemStatus.Completed;
                        
                        // Slskd downloads directories into the configured directories/downloads + last part of the downloaded directory
                        // Given Slskd directories/downloads is set to /data/complete and we download USER3:music/books/randombook
                        // Then the content path will be /data/complete/randombook
                        // If two download have the same last directory: They are downloaded in the same directory
                        // When that happens, if there is a conflict for one of the file: The original one stays, the new one is suffixed with a timestamp
                        // Warning: In those cases, the API does not allows to detect which file comes from which transfer
                        // See https://github.com/slskd/slskd/issues/1584
                        string normalizedDirectory = directory.Directory.Replace('\\', Path.DirectorySeparatorChar);
                        string downloadPath = Path.GetFileName(normalizedDirectory.TrimEnd(Path.DirectorySeparatorChar));
                        var contentPath = Path.Join(downloadBasePath, downloadPath);
                        
                        return [new QueueItem {
                            Id = matchedDownload.Id,
                            Title = directory.Directory,
                            Size = totalSize,
                            Downloaded = totalSize - totalLeft,
                            Status = state.ToString().ToLower(),
                            Progress = progress,
                            DownloadClientId = client.Id,
                            ContentPath = contentPath
                        }];
                    })
                )
            ];
        }

        public async Task<QueueItem> GetImportItemAsync(DownloadClientConfiguration client, Download download, QueueItem queueItem, QueueItem? previousAttempt = null, CancellationToken ct = default)
        {
            List<QueueItem> queuedItems = await GetQueueAsync(client, ct);
            return queuedItems.FirstOrDefault(item => item.Id == download.Id) ?? queueItem;
        }

        public async Task<List<Download>> PollAsync(
            DownloadMonitorService service,
            DownloadClientConfiguration client,
            List<Download> downloads,
            ApplicationSettings settings,
            CancellationToken ct = default)
        {
            using var db = _dbFactory.CreateDbContext();
            var transfers = await GetQueueAsync(client, ct);
            foreach(var download in downloads)
            {
                var transfer = transfers.FirstOrDefault(transfer => transfer.Id == download.Id);
                if (transfer == null)
                {
                    // Download no longer in the download client
                    await service.HandleFailedDownloadAsync(download, client, db, settings, $"Slskd does not have the download {download.Id}", ct);
                    continue;
                }

                await service.UpdateDownloadProgressAsync(download, transfer.Progress, transfer.Size - transfer.Downloaded, "downloading", db, ct);

                if (string.Equals(transfer.Status, "completed", StringComparison.OrdinalIgnoreCase))
                {
                    // Download completed
                    await service.FinalizeDownloadAsync(download, transfer.ContentPath!, client, ct);
                }
            }

            return downloads;
        }

        // Not used by the backend: Specific to SABnzbd/NZBGet
        public Task<List<(string Id, string Name)>> GetRecentHistoryAsync(DownloadClientConfiguration client, int limit = 100, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        // Not yet used by the backend: Will replace GetQueueAsync
        public Task<List<DownloadClientItem>> GetItemsAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        // Not yet used by the backend: Will replace GetImportItemAsync
        public Task<DownloadClientItem> GetImportItemAsync(DownloadClientConfiguration client, DownloadClientItem item, DownloadClientItem? previousAttempt = null, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        private DownloadItemStatus mapSlskdStatusToDownloadItemStatus(string state)
        {
            return state.Split(',', StringSplitOptions.TrimEntries)
                .Select(part => part switch
                {
                    "InProgress" or "Initializing"          => DownloadItemStatus.Downloading,
                    "Queued" or "Requested"                 => DownloadItemStatus.Queued,
                    "Completed" or "Succeeded"              => DownloadItemStatus.Completed,
                    "Cancelled" or "Aborted"                => DownloadItemStatus.Removed,
                    "Errored" or "TimedOut" or "Rejected"   => DownloadItemStatus.Failed,
                    "None"                                  => DownloadItemStatus.Unknown,
                    _                                       => (DownloadItemStatus?)null // Ignore "Locally/Remotely"
                })
                .FirstOrDefault(s => s != null) ?? DownloadItemStatus.Unknown;
        }

        /// <summary>
        /// Retrieve the configured path for the downloads from the DownloadClientConfiguration or fetch it from Slskd API
        /// </summary>
        /// <param name="client">Download client configuration to use</param>
        /// <param name="dbContext">DB context to update the configured client once the correct path is retrieved</param>
        /// <returns>Path where the downloads are stored one completed</returns>
        /// <exception cref="DownloadClientException">Raised when there is no path configured neither in the DownloadClientConfiguraiton nor in the Slskd configuration</exception>
        private async Task<string> GetConfiguredDownloadPath(DownloadClientConfiguration client, ListenArrDbContext dbContext)
        {
            if (!string.IsNullOrWhiteSpace(client.DownloadPath))
            {
                return client.DownloadPath;
            }

            var request = BuildHttpRequestMessage(HttpMethod.Get, client, "/api/v0/options");

            HttpContent content;
            try
            {
                content = await Call(request);
                
                using var stream = await content.ReadAsStreamAsync();
                using JsonDocument doc = await JsonDocument.ParseAsync(stream);
                JsonElement root = doc.RootElement;

                if (root.TryGetProperty("directories", out JsonElement directories))
                {
                    if (directories.TryGetProperty("downloads", out JsonElement downloads))
                    {
                        var path = FileUtils.NormalizeStoredPath(downloads.GetString());
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            // Update client path to save API calls
                            client.DownloadPath = path;
                            dbContext.DownloadClientConfigurations.Update(client);
                            await dbContext.SaveChangesAsync();

                            return path;
                        }
                    }
                }
                
                throw new DownloadClientException("No configured download directory in Slskd");
            }
            catch(DownloadClientException exception)
            {
                throw new DownloadClientException("Unable to get Slskd download directory", exception);
            }
        }
    }
}

