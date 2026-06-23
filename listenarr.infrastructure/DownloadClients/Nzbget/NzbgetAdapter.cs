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
using System.Xml.Linq;
using Listenarr.Domain.Common;
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
        private readonly NzbgetHistoryReader _historyReader;
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
            _historyReader = new NzbgetHistoryReader(_xmlRpcClient);
            _downloadPollingWorkflow = new NzbgetDownloadPollingWorkflow(
                httpClientFactory,
                _historyReader,
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
            var activeIdentities = new List<ActiveQueueIdentity>();

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
                                new ActiveQueueIdentity(
                                    ReadActiveScalar(structElement, "NZBID").Trim(),
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

            await AppendQueueHistoryAsync(
                client,
                configuredCategory,
                activeIdentities,
                items,
                ct);
            return items;
        }

        private async Task AppendQueueHistoryAsync(
            DownloadClientConfiguration client,
            string? configuredCategory,
            IReadOnlyList<ActiveQueueIdentity> activeIdentities,
            List<QueueItem> items,
            CancellationToken cancellationToken)
        {
            try
            {
                var history = await _historyReader.ReadAsync(client, cancellationToken);
                AppendQueueHistory(
                    client,
                    configuredCategory,
                    activeIdentities,
                    history,
                    items,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(
                    "NZBGet history enrichment failed clientId={ClientId} surface={Surface} activeCount={ActiveCount} failureType={FailureType}",
                    LogRedaction.SanitizeText(client.Id ?? client.Name ?? client.Type),
                    nameof(GetQueueAsync),
                    items.Count,
                    ex.GetType().Name);
            }
        }

        private void AppendQueueHistory(
            DownloadClientConfiguration client,
            string? configuredCategory,
            IReadOnlyList<ActiveQueueIdentity> activeIdentities,
            IReadOnlyList<NzbgetHistoryEntry> history,
            List<QueueItem> items,
            CancellationToken cancellationToken)
        {
            var activeCanonicalIds = activeIdentities
                .Where(identity => !string.IsNullOrEmpty(identity.CanonicalNzbId))
                .Select(identity => identity.CanonicalNzbId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var processedHistoryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in history)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsQueueHistoryCandidate(
                    entry,
                    configuredCategory,
                    activeIdentities,
                    activeCanonicalIds,
                    processedHistoryIds))
                {
                    continue;
                }

                TryAppendQueueHistoryItem(client, entry, items);
            }
        }

        private static bool IsQueueHistoryCandidate(
            NzbgetHistoryEntry entry,
            string? configuredCategory,
            IReadOnlyList<ActiveQueueIdentity> activeIdentities,
            ISet<string> activeCanonicalIds,
            ISet<string> processedHistoryIds)
        {
            if (entry.Outcome == NzbgetHistoryOutcome.Ignored ||
                !DownloadClientCategoryFilter.Matches(configuredCategory, entry.Category))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(entry.CanonicalNzbId) &&
                (!processedHistoryIds.Add(entry.CanonicalNzbId) ||
                 activeCanonicalIds.Contains(entry.CanonicalNzbId)))
            {
                return false;
            }

            return !activeIdentities.Any(
                active => TitleUtils.AreTitlesSimilar(active.Title, entry.Title));
        }

        private void TryAppendQueueHistoryItem(
            DownloadClientConfiguration client,
            NzbgetHistoryEntry entry,
            ICollection<QueueItem> items)
        {
            try
            {
                items.Add(NzbgetResponseMapper.MapHistoryToQueueItem(client, entry));
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(
                    "Failed to map NZBGet queue history entry clientId={ClientId} surface={Surface} failureType={FailureType}",
                    LogRedaction.SanitizeText(client.Id ?? client.Name ?? client.Type),
                    nameof(GetQueueAsync),
                    ex.GetType().Name);
            }
        }

        private static string ReadActiveScalar(XElement structElement, string name)
        {
            return structElement.Elements("member")
                .FirstOrDefault(member => string.Equals(
                    member.Element("name")?.Value,
                    name,
                    StringComparison.Ordinal))?
                .Element("value")?
                .Elements()
                .FirstOrDefault()?
                .Value ?? string.Empty;
        }

        private sealed record ActiveQueueIdentity(
            string CanonicalNzbId,
            string Title);

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

            try
            {
                var listResult = await _xmlRpcClient.CallAsync(client, "listgroups");
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
            }

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
