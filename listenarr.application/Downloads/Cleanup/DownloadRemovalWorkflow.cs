/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Downloads.Cleanup
{
    public sealed class DownloadRemovalWorkflow(
        IConfigurationService _configurationService,
        IDownloadRepository _downloadRepository,
        IDownloadClientGateway _clientGateway,
        IDownloadQueueService _downloadQueueService,
        ILogger<DownloadRemovalWorkflow> _logger)
    {
        public async Task<bool> RemoveAsync(string downloadId, string? downloadClientId = null, bool force = false)
        {
            try
            {
                bool removedFromClient = false;
                Download? downloadRecord = null;

                // Try to find by direct ID match first
                downloadRecord = await _downloadRepository.FindAsync(downloadId);

                // If not found, try to find by client-specific ID (e.g., torrent hash)
                if (downloadRecord == null)
                {
                    var allDownloads = await _downloadRepository.GetAllAsync();
                    downloadRecord = allDownloads.FirstOrDefault(d =>
                        d.Metadata != null &&
                        ((d.Metadata.TryGetValue("ClientDownloadId", out var clientIdObj) &&
                          string.Equals(clientIdObj?.ToString(), downloadId, StringComparison.OrdinalIgnoreCase)) ||
                         (d.Metadata.TryGetValue("TorrentHash", out var hashObj) &&
                          string.Equals(hashObj?.ToString(), downloadId, StringComparison.OrdinalIgnoreCase))));
                }

                // If still not found, try enhanced title/name matching for legacy downloads
                if (downloadRecord == null && downloadClientId != null)
                {
                    var client = await _configurationService.GetDownloadClientConfigurationAsync(downloadClientId);
                    if (client != null)
                    {
                        var queue = await _downloadQueueService.GetQueueAsync();
                        var queueItem = queue.FirstOrDefault(q => q.Id == downloadId && q.DownloadClientId == downloadClientId);

                        if (queueItem != null)
                        {
                            var clientDownloads = await _downloadRepository.GetByClientAsync(downloadClientId);
                            downloadRecord = clientDownloads.FirstOrDefault(d => TitleUtils.IsMatchingTitle(d.Title, queueItem.Title));
                        }
                    }
                }

                // If force=true, skip client removal and just remove from database
                if (force)
                {
                    _logger.LogWarning("Force removal requested for {DownloadId}, skipping client removal", downloadId);
                    removedFromClient = true;
                }
                else if (downloadClientId == null)
                {
                    // Try all clients to find and remove the item
                    var downloadClients = await _configurationService.GetDownloadClientConfigurationsAsync();
                    var enabledClients = downloadClients.Where(c => c.IsEnabled).ToList();

                    foreach (var client in enabledClients)
                    {
                        removedFromClient = await RemoveFromClientAsync(client, downloadId, downloadRecord);
                        if (removedFromClient)
                        {
                            downloadClientId = client.Id; // Track which client it was removed from
                            break;
                        }
                    }
                }
                else
                {
                    // Check if the downloadClientId is a valid client configuration
                    var client = await _configurationService.GetDownloadClientConfigurationAsync(downloadClientId);
                    if (client != null && !client.IsEnabled)
                    {
                        _logger.LogInformation("Skipping removal of {DownloadId} from disabled client {ClientName}", downloadId, client.Name);
                    }
                    else if (client != null)
                    {
                        removedFromClient = await RemoveFromClientAsync(client, downloadId, downloadRecord);
                    }
                    else
                    {
                        // If client not found by ID, this might be a legacy/invalid client ID
                        // Try to find the download in the database and check if it's DDL or has a valid client
                        if (downloadRecord != null)
                        {
                            if (string.Equals(downloadRecord.DownloadClientId, DirectDownloadMetadataKeys.ClientId, StringComparison.OrdinalIgnoreCase))
                            {
                                // DDL downloads don't have an external client to remove from
                                removedFromClient = true;
                                _logger.LogInformation("Download {DownloadId} is DDL, skipping external client removal", downloadId);
                            }
                            else if (!string.IsNullOrEmpty(downloadRecord.DownloadClientId))
                            {
                                // Try with the download record's client ID
                                var recordClient = await _configurationService.GetDownloadClientConfigurationAsync(downloadRecord.DownloadClientId);
                                if (recordClient != null && !recordClient.IsEnabled)
                                {
                                    _logger.LogInformation("Skipping removal of {DownloadId} from disabled client {ClientName}", downloadId, recordClient.Name);
                                    removedFromClient = true; // Treat as success so DB record is cleaned up
                                }
                                else if (recordClient != null)
                                {
                                    removedFromClient = await RemoveFromClientAsync(recordClient, downloadId, downloadRecord);
                                    downloadClientId = recordClient.Id;
                                }
                                else
                                {
                                    // Client no longer exists, just remove from database
                                    removedFromClient = true;
                                    _logger.LogWarning("Download client {ClientId} not found for download {DownloadId}, removing from database only",
                                        downloadRecord.DownloadClientId, downloadId);
                                }
                            }
                        }
                        else
                        {
                            // Download not in database and invalid client ID provided
                            // This could be an external queue item with a bad client ID reference
                            // Try all enabled clients to find and remove it
                            _logger.LogWarning("Invalid client ID {ClientId} and download {DownloadId} not in database, trying all clients",
                                downloadClientId, downloadId);

                            var downloadClients = await _configurationService.GetDownloadClientConfigurationsAsync();
                            var enabledClients = downloadClients.Where(c => c.IsEnabled).ToList();

                            foreach (var tryClient in enabledClients)
                            {
                                removedFromClient = await RemoveFromClientAsync(tryClient, downloadId, downloadRecord);
                                if (removedFromClient)
                                {
                                    downloadClientId = tryClient.Id;
                                    _logger.LogInformation("Successfully removed {DownloadId} from client {ClientName}", downloadId, tryClient.Name);
                                    break;
                                }
                            }

                            // If still not removed but not in any queue, consider it success
                            if (!removedFromClient)
                            {
                                _logger.LogInformation("Could not remove {DownloadId} from any client, verifying it's not in any queue", downloadId);
                                var currentQueue = await _downloadQueueService.GetQueueAsync();
                                if (!currentQueue.Any(q => q.Id.Equals(downloadId, StringComparison.OrdinalIgnoreCase)))
                                {
                                    _logger.LogInformation("Download {DownloadId} not found in any queue, treating as successfully removed", downloadId);
                                    removedFromClient = true;
                                }
                            }
                        }
                    }
                }

                // If successfully removed from client (or force=true), also remove from database
                if (removedFromClient && downloadRecord != null)
                {
                    await _downloadRepository.RemoveAsync(downloadRecord.Id);
                    _logger.LogInformation("Removed download record from database: {DownloadId} (Title: {Title})",
                        downloadRecord.Id, downloadRecord.Title);
                }

                return removedFromClient;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error removing from queue: {DownloadId}", downloadId);
                return false;
            }
        }


        private async Task<bool> RemoveFromClientAsync(DownloadClientConfiguration client, string downloadId, Download? downloadRecord = null)
        {
            try
            {
                if (client == null) return false;

                // Resolve the client-specific ID (torrent hash, NZB ID, etc.) from the download record.
                // The download record's Metadata dictionary stores the mapping set during AddAsync.
                // Without this, Transmission/qBittorrent receive the Listenarr UUID which they don't recognise.
                var clientItemId = downloadId;
                if (downloadRecord?.Metadata != null)
                {
                    if ((string.Equals(client.Type, "qbittorrent", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(client.Type, "transmission", StringComparison.OrdinalIgnoreCase)) &&
                        downloadRecord.Metadata.TryGetValue("TorrentHash", out var hashObj))
                    {
                        var hash = hashObj?.ToString();
                        if (!string.IsNullOrEmpty(hash))
                        {
                            clientItemId = hash;
                            _logger.LogDebug("RemoveFromClientAsync: Using torrent hash {Hash} instead of download ID for {ClientType} removal",
                                hash, client.Type);
                        }
                    }
                    else if (downloadRecord.Metadata.TryGetValue("ClientDownloadId", out var clientIdObj))
                    {
                        var resolvedId = clientIdObj?.ToString();
                        if (!string.IsNullOrEmpty(resolvedId))
                        {
                            clientItemId = resolvedId;
                            _logger.LogDebug("RemoveFromClientAsync: Using client-specific ID {ClientId} for {ClientType} removal",
                                resolvedId, client.Type);
                        }
                    }
                }

                if (_clientGateway != null)
                {
                    try
                    {
                        var removed = await _clientGateway.RemoveAsync(client, clientItemId, false);
                        if (removed)
                        {
                            _logger.LogInformation("Successfully removed {DownloadId} from client {ClientName}", downloadId, client.Name ?? client.Id);
                            return true;
                        }

                        // If removal returned false, verify if the item is still in the client's queue
                        // If it's not in the queue, consider removal successful (item already gone)
                        _logger.LogWarning("Client reported removal failed for {DownloadId}, checking if item still exists in queue", downloadId);
                        try
                        {
                            var queue = await _clientGateway.GetQueueAsync(client);
                            var stillExists = queue.Any(q => q.Id.Equals(downloadId, StringComparison.OrdinalIgnoreCase));

                            if (!stillExists)
                            {
                                _logger.LogInformation("Item {DownloadId} no longer in {ClientName} queue, treating removal as successful", downloadId, client.Name ?? client.Id);
                                return true;
                            }

                            _logger.LogWarning("Item {DownloadId} still exists in {ClientName} queue after removal attempt", downloadId, client.Name ?? client.Id);
                            return false;
                        }
                        catch (Exception queueEx) when (queueEx is not OperationCanceledException && queueEx is not OutOfMemoryException && queueEx is not StackOverflowException)
                        {
                            _logger.LogWarning(queueEx, "Failed to verify queue status for {DownloadId} on {ClientName}, assuming removal failed", downloadId, client.Name ?? client.Id);
                            return false;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "RemoveFromClientAsync: Exception removing {DownloadId} from {Client}: {Message}",
                            LogRedaction.SanitizeText(downloadId), LogRedaction.SanitizeText(client.Name ?? client.Id), ex.Message);

                        // Check if item still exists in queue - if not, consider removal successful
                        try
                        {
                            var queue = await _clientGateway.GetQueueAsync(client);
                            var stillExists = queue.Any(q => q.Id.Equals(downloadId, StringComparison.OrdinalIgnoreCase));

                            if (!stillExists)
                            {
                                _logger.LogInformation("After exception, item {DownloadId} not found in {ClientName} queue, treating as successfully removed",
                                    downloadId, client.Name ?? client.Id);
                                return true;
                            }
                        }
                        catch (Exception queueEx) when (queueEx is not OperationCanceledException && queueEx is not OutOfMemoryException && queueEx is not StackOverflowException)
                        {
                            _logger.LogDebug(queueEx, "Failed to verify queue after exception for {DownloadId}", downloadId);
                        }

                        return false;
                    }
                }

                // Fallback conservative behavior when no gateway is available
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "RemoveFromClientAsync fallback failed for client {Client}", client.Name ?? client.Id);
                return false;
            }
        }


    }
}
