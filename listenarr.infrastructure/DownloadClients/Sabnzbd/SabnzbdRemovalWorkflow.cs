/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Sabnzbd
{
    internal sealed class SabnzbdRemovalWorkflow
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly SabnzbdRequestBuilder _requestBuilder;
        private readonly ILogger _logger;
        private readonly string _clientType;

        public SabnzbdRemovalWorkflow(
            IHttpClientFactory httpFactory,
            SabnzbdRequestBuilder requestBuilder,
            ILogger logger,
            string clientType)
        {
            _httpFactory = httpFactory;
            _requestBuilder = requestBuilder;
            _logger = logger;
            _clientType = clientType;
        }

        public async Task<bool> RemoveAsync(DownloadClientConfiguration client, string id, bool deleteFiles = false, CancellationToken ct = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            try
            {
                var requestContext = _requestBuilder.CreateContext(client);
                if (!requestContext.HasApiKey)
                {
                    _logger.LogWarning("SABnzbd API key not configured for {ClientName}", client.Name);
                    return false;
                }

                var http = _httpFactory.CreateClient(_clientType);
                bool removedFromQueue = false;
                bool removedFromHistory = false;

                // Try to remove from queue first (for active downloads)
                var queueRemoveUrl = _requestBuilder.BuildUrl(requestContext, new Dictionary<string, string>
                {
                    ["mode"] = "queue",
                    ["name"] = "delete",
                    ["value"] = id,
                    ["output"] = "json"
                });
                if (deleteFiles)
                    queueRemoveUrl += "&del_files=1";

                try
                {
                    var queueResponse = await http.GetAsync(queueRemoveUrl, ct);
                    if (queueResponse.IsSuccessStatusCode)
                    {
                        var queueContent = await queueResponse.Content.ReadAsStringAsync(ct);
                        var queueDoc = JsonDocument.Parse(queueContent);
                        if (queueDoc.RootElement.TryGetProperty("status", out var queueStatus))
                        {
                            removedFromQueue = queueStatus.GetBoolean();
                        }
                    }
                }
                catch (Exception queueEx) when (queueEx is not OperationCanceledException && queueEx is not OutOfMemoryException && queueEx is not StackOverflowException)
                {
                    _logger.LogDebug(queueEx, "Could not remove {DownloadId} from SABnzbd queue (may not be in queue)", id);
                }

                // Try to remove from history (for completed downloads)
                var historyRemoveUrl = _requestBuilder.BuildUrl(requestContext, new Dictionary<string, string>
                {
                    ["mode"] = "history",
                    ["name"] = "delete",
                    ["value"] = id,
                    ["output"] = "json"
                });
                if (deleteFiles)
                    historyRemoveUrl += "&del_files=1";

                try
                {
                    var historyResponse = await http.GetAsync(historyRemoveUrl, ct);
                    if (historyResponse.IsSuccessStatusCode)
                    {
                        var historyContent = await historyResponse.Content.ReadAsStringAsync(ct);
                        var historyDoc = JsonDocument.Parse(historyContent);
                        if (historyDoc.RootElement.TryGetProperty("status", out var historyStatus))
                        {
                            removedFromHistory = historyStatus.GetBoolean();
                        }
                    }
                }
                catch (Exception historyEx) when (historyEx is not OperationCanceledException && historyEx is not OutOfMemoryException && historyEx is not StackOverflowException)
                {
                    _logger.LogDebug(historyEx, "Could not remove {DownloadId} from SABnzbd history (may not be in history)", id);
                }

                var success = removedFromQueue || removedFromHistory;
                if (success)
                {
                    _logger.LogInformation("Removed {DownloadId} from SABnzbd (queue: {Queue}, history: {History}, deleteFiles: {DeleteFiles})",
                        LogRedaction.SanitizeText(id), removedFromQueue, removedFromHistory, deleteFiles);
                }
                else
                {
                    _logger.LogWarning("Failed to remove {DownloadId} from SABnzbd (not found in queue or history)", LogRedaction.SanitizeText(id));
                }

                return success;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error removing from SABnzbd: {DownloadId}", LogRedaction.SanitizeText(id));
                return false;
            }
        }
    }
}
