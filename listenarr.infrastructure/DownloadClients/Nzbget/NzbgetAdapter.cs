/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using System.Net;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Nzbget
{
    public class NzbgetAdapter : IDownloadClientAdapter
    {
        public string ClientId => "nzbget";
        public string ClientType => "nzbget";
        public DownloadProtocol Protocol => DownloadProtocol.Usenet;

        private readonly ILogger<NzbgetAdapter> _logger;
        private readonly NzbgetXmlRpcClient _xmlRpcClient;
        private readonly NzbgetHistoryEnrichmentWorkflow _historyEnrichmentWorkflow;
        private readonly NzbgetDownloadPollingWorkflow _downloadPollingWorkflow;
        private readonly NzbgetRemovalWorkflow _removalWorkflow;
        private readonly NzbgetAddWorkflow _addWorkflow;
        private readonly NzbgetImportItemResolver _importItemResolver;

        public NzbgetAdapter(
            IHttpClientFactory httpClientFactory,
            INzbUrlResolver nzbUrlResolver,
            ILogger<NzbgetAdapter> logger)
            : this(
                httpClientFactory,
                nzbUrlResolver,
                logger,
                TimeProvider.System)
        {
        }

        internal NzbgetAdapter(
            IHttpClientFactory httpClientFactory,
            INzbUrlResolver nzbUrlResolver,
            ILogger<NzbgetAdapter> logger,
            TimeProvider timeProvider)
        {
            ArgumentNullException.ThrowIfNull(httpClientFactory);
            ArgumentNullException.ThrowIfNull(nzbUrlResolver);
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            ArgumentNullException.ThrowIfNull(timeProvider);
            _xmlRpcClient = new NzbgetXmlRpcClient(httpClientFactory, ClientType);
            var historyReader = new NzbgetHistoryReader(_xmlRpcClient);
            _historyEnrichmentWorkflow = new NzbgetHistoryEnrichmentWorkflow(
                historyReader,
                _logger);
            _downloadPollingWorkflow = new NzbgetDownloadPollingWorkflow(
                httpClientFactory,
                historyReader,
                _logger,
                timeProvider,
                ClientType);
            _removalWorkflow = new NzbgetRemovalWorkflow(_xmlRpcClient, _logger);
            _addWorkflow = new NzbgetAddWorkflow(_xmlRpcClient, _logger);
            _importItemResolver = new NzbgetImportItemResolver(_xmlRpcClient, _logger);
        }

        public async Task<(bool Success, string Message)> TestConnectionAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            if (client == null)
            {
                return (false, "NZBGet: Configuration not provided");
            }

            if (!string.IsNullOrWhiteSpace(client.Username) && string.IsNullOrWhiteSpace(client.Password))
            {
                return (false, "NZBGet: Password is required when a username is specified");
            }

            try
            {
                // Test connection via XML-RPC
                var versionResult = await _xmlRpcClient.CallAsync(client, "version");
                var version = versionResult.Element("string")?.Value ?? "unknown";

                if (string.IsNullOrWhiteSpace(version))
                {
                    return (false, "NZBGet: Unable to retrieve version");
                }

                return (true, "NZBGet: connected");
            }
            catch (HttpRequestException httpEx) when (httpEx.StatusCode == HttpStatusCode.Unauthorized || httpEx.StatusCode == HttpStatusCode.Forbidden)
            {
                _logger.LogDebug(httpEx, "NZBGet authentication failed for client {ClientId}", LogRedaction.SanitizeText(client.Id ?? client.Name ?? client.Type));
                return (false, "NZBGet: Authentication failed (check username/password)");
            }
            catch (HttpRequestException httpEx)
            {
                _logger.LogDebug(httpEx, "NZBGet network error for client {ClientId}", LogRedaction.SanitizeText(client.Id ?? client.Name ?? client.Type));
                return (false, $"NZBGet: network error ({httpEx.StatusCode?.ToString() ?? "unavailable"})");
            }
            catch (TaskCanceledException tce)
            {
                _logger.LogDebug(tce, "NZBGet test timed out for client {ClientId}", LogRedaction.SanitizeText(client.Id ?? client.Name ?? client.Type));
                return (false, "NZBGet: connection timed out");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "NZBGet test failed for client {ClientId}", LogRedaction.SanitizeText(client.Id ?? client.Name ?? client.Type));
                return (false, "NZBGet: connection failed");
            }
        }

        public async Task<DownloadClientSubmissionResult> AddAsync(
            DownloadClientConfiguration client,
            PreparedDownloadSubmission submission,
            CancellationToken ct = default)
        {
            if (submission is not PreparedUsenetSubmission usenet)
                throw new DownloadClientSubmissionException("NZBGet requires a prepared Usenet submission.");
            return await _addWorkflow.AddAsync(client, usenet, ct);
        }

        public async Task<bool> RemoveAsync(DownloadClientConfiguration client, string id, bool deleteFiles = false, CancellationToken ct = default)
        {
            return await _removalWorkflow.RemoveAsync(client, id, deleteFiles, ct);
        }

        public async Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            var items = new List<QueueItem>();
            if (client == null) return items;

            var configuredCategory = DownloadClientCategoryFilter.GetConfiguredCategory(client);
            var activeIdentities = new List<NzbgetHistoryEnrichmentWorkflow.ActiveHistoryIdentity>();

            try
            {
                var listResult = await _xmlRpcClient.CallAsync(
                    new NzbgetXmlRpcRequest
                    {
                        Client = client,
                        MethodName = "listgroups",
                        Parameters = [0]
                    },
                    ct);
                var arrayData = listResult.Element("array")?.Element("data");

                if (arrayData == null)
                {
                    return items;
                }

                foreach (var valueElement in arrayData.Elements("value"))
                {
                    try
                    {
                        var structElement = valueElement.Element("struct");
                        if (structElement != null)
                        {
                            var groupCategory = structElement.Elements("member")
                                .FirstOrDefault(m => string.Equals(m.Element("name")?.Value, "Category", StringComparison.Ordinal))?
                                .Element("value")?.Elements().FirstOrDefault()?.Value ?? string.Empty;

                            if (!DownloadClientCategoryFilter.Matches(configuredCategory, groupCategory))
                            {
                                continue;
                            }

                            var queueItem = NzbgetResponseMapper.MapGroup(client, structElement);
                            items.Add(queueItem);
                            activeIdentities.Add(
                                _historyEnrichmentWorkflow.ParseActiveIdentity(
                                    structElement,
                                    queueItem.Title));
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogDebug(ex, "Failed to map NZBGet queue item (non-fatal)");
                    }
                }
            }
            catch (HttpRequestException httpEx) when (httpEx.StatusCode == HttpStatusCode.Unauthorized || httpEx.StatusCode == HttpStatusCode.Forbidden)
            {
                _logger.LogWarning("NZBGet authentication failed for client {ClientName} — check username/password", LogRedaction.SanitizeText(client.Name ?? client.Id));
                return items;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to retrieve NZBGet queue for client {ClientName}", LogRedaction.SanitizeText(client.Name ?? client.Id));
                return items;
            }

            await _historyEnrichmentWorkflow.EnrichQueueAsync(
                client,
                configuredCategory,
                activeIdentities,
                items,
                ct);
            return items;
        }

        public async Task<List<(string Id, string Name)>> GetRecentHistoryAsync(DownloadClientConfiguration client, int limit = 100, CancellationToken ct = default)
        {
            var history = new List<(string Id, string Name)>();
            if (client == null) return history;

            try
            {
                var historyResult = await _xmlRpcClient.CallAsync(client, "history", false);
                var arrayData = historyResult.Element("array")?.Element("data");

                if (arrayData == null)
                {
                    return history;
                }

                var count = 0;
                foreach (var valueElement in arrayData.Elements("value"))
                {
                    if (count >= limit) break;

                    var structElement = valueElement.Element("struct");
                    if (structElement != null)
                    {
                        var members = structElement.Elements("member").ToDictionary(
                            m => m.Element("name")?.Value ?? string.Empty,
                            m => m.Element("value")?.Elements().FirstOrDefault()?.Value ?? string.Empty
                        );

                        var entryId = members.GetValueOrDefault("ID", string.Empty);
                        var entryName = members.GetValueOrDefault("NZBName", string.Empty);

                        if (!string.IsNullOrEmpty(entryId) && !string.IsNullOrEmpty(entryName))
                        {
                            history.Add((entryId, entryName));
                            count++;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Failed to fetch NZBGet history for client {ClientName}", LogRedaction.SanitizeText(client.Name ?? client.Id));
            }

            return history;
        }

        /// <summary>
        /// Get all downloads as standardized DownloadClientItem objects
        /// </summary>
        public async Task<List<DownloadClientItem>> GetItemsAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            var items = new List<DownloadClientItem>();
            if (client == null) return items;

            var configuredCategory = DownloadClientCategoryFilter.GetConfiguredCategory(client);
            var activeIdentities = new List<NzbgetHistoryEnrichmentWorkflow.ActiveHistoryIdentity>();

            try
            {
                var listResult = await _xmlRpcClient.CallAsync(
                    new NzbgetXmlRpcRequest
                    {
                        Client = client,
                        MethodName = "listgroups",
                        Parameters = [0]
                    },
                    ct);
                var arrayData = listResult.Element("array")?.Element("data");

                if (arrayData == null)
                {
                    return items;
                }

                foreach (var valueElement in arrayData.Elements("value"))
                {
                    try
                    {
                        var structElement = valueElement.Element("struct");
                        if (structElement != null)
                        {
                            var groupCategory = structElement.Elements("member")
                                .FirstOrDefault(m => string.Equals(m.Element("name")?.Value, "Category", StringComparison.Ordinal))?
                                .Element("value")?.Elements().FirstOrDefault()?.Value ?? string.Empty;

                            if (!DownloadClientCategoryFilter.Matches(configuredCategory, groupCategory))
                            {
                                continue;
                            }

                            var downloadClientItem = NzbgetResponseMapper.MapGroupToDownloadClientItem(client, structElement);
                            items.Add(downloadClientItem);
                            activeIdentities.Add(
                                _historyEnrichmentWorkflow.ParseActiveIdentity(
                                    structElement,
                                    downloadClientItem.Title));
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogDebug(ex, "Failed to map NZBGet queue item (non-fatal)");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to retrieve NZBGet items for client {ClientName}", LogRedaction.SanitizeText(client.Name ?? client.Id));
                return items;
            }

            await _historyEnrichmentWorkflow.EnrichItemsAsync(
                client,
                configuredCategory,
                activeIdentities,
                items,
                ct);
            return items;
        }

        /// <summary>
        /// Get import item from DownloadClientItem
        /// </summary>
        public async Task<DownloadClientItem> GetImportItemAsync(
            DownloadClientConfiguration client,
            DownloadClientItem item,
            DownloadClientItem? previousAttempt = null,
            CancellationToken ct = default)
        {
            return await _importItemResolver.GetImportItemAsync(client, item);
        }

        /// <summary>
        /// Resolves the actual import item for a completed download.
        /// Queries NZBGet history for FinalDir or DestDir.
        /// Matches NzbGet.GetImportItem pattern.
        /// </summary>
        public async Task<QueueItem> GetImportItemAsync(
            DownloadClientConfiguration client,
            Download download,
            QueueItem queueItem,
            QueueItem? previousAttempt = null,
            CancellationToken ct = default)
        {
            return await _importItemResolver.GetImportItemAsync(client, queueItem);
        }

        public async Task<List<Download>> FetchDownloadsAsync(
            DownloadClientConfiguration client,
            List<Download> downloads,
            CancellationToken cancellationToken)
        {
            return await _downloadPollingWorkflow.FetchDownloadsAsync(client, downloads, cancellationToken);
        }
    }
}
