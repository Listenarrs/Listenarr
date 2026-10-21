/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Infrastructure.Torrents;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Deluge
{
    /// <summary>
    /// Deluge JSON-RPC protocol facade.
    /// Keep client-specific workflows behind this adapter so every supported
    /// download client exposes the same thin IDownloadClientAdapter surface.
    /// </summary>
    public class DelugeAdapter : IDownloadClientAdapter
    {
        public string ClientId => DownloadClientTypes.Deluge;
        public string ClientType => DownloadClientTypes.Deluge;
        public DownloadProtocol Protocol => DownloadProtocol.Torrent;

        private readonly DelugeRpcClient _rpcClient;
        private readonly DelugeConnectionTester _connectionTester;
        private readonly DelugeAddWorkflow _addWorkflow;
        private readonly DelugeRemovalWorkflow _removalWorkflow;
        private readonly DelugeQueueFetchWorkflow _queueFetchWorkflow;
        private readonly DelugeItemFetchWorkflow _itemFetchWorkflow;
        private readonly DelugeImportItemResolver _importItemResolver;
        private readonly ILogger<DelugeAdapter> _logger;

        internal DelugeAdapter(
            DelugeRpcClient rpcClient,
            DelugeConnectionTester connectionTester,
            DelugeAddWorkflow addWorkflow,
            DelugeRemovalWorkflow removalWorkflow,
            DelugeQueueFetchWorkflow queueFetchWorkflow,
            DelugeItemFetchWorkflow itemFetchWorkflow,
            DelugeImportItemResolver importItemResolver,
            ILogger<DelugeAdapter> logger)
        {
            _rpcClient = rpcClient ?? throw new ArgumentNullException(nameof(rpcClient));
            _connectionTester = connectionTester ?? throw new ArgumentNullException(nameof(connectionTester));
            _addWorkflow = addWorkflow ?? throw new ArgumentNullException(nameof(addWorkflow));
            _removalWorkflow = removalWorkflow ?? throw new ArgumentNullException(nameof(removalWorkflow));
            _queueFetchWorkflow = queueFetchWorkflow ?? throw new ArgumentNullException(nameof(queueFetchWorkflow));
            _itemFetchWorkflow = itemFetchWorkflow ?? throw new ArgumentNullException(nameof(itemFetchWorkflow));
            _importItemResolver = importItemResolver ?? throw new ArgumentNullException(nameof(importItemResolver));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public DelugeAdapter(
            IHttpClientFactory httpClientFactory,
            ITorrentFileDownloader torrentFileDownloader,
            ILogger<DelugeAdapter> logger)
        {
            ArgumentNullException.ThrowIfNull(httpClientFactory);
            _ = torrentFileDownloader ?? throw new ArgumentNullException(nameof(torrentFileDownloader));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _rpcClient = new DelugeRpcClient(httpClientFactory, ClientType, logger);
            _connectionTester = new DelugeConnectionTester(_rpcClient, logger);
            _addWorkflow = new DelugeAddWorkflow(_rpcClient, logger);
            _removalWorkflow = new DelugeRemovalWorkflow(_rpcClient, logger);
            _queueFetchWorkflow = new DelugeQueueFetchWorkflow(_rpcClient, logger);
            _itemFetchWorkflow = new DelugeItemFetchWorkflow(_rpcClient, logger);
            _importItemResolver = new DelugeImportItemResolver(_rpcClient, logger);
        }

        public Task<(bool Success, string Message)> TestConnectionAsync(DownloadClientConfiguration client, CancellationToken ct = default)
            => _connectionTester.TestConnectionAsync(client, ct);

        public Task<DownloadClientSubmissionResult> AddAsync(
            DownloadClientConfiguration client,
            PreparedDownloadSubmission submission,
            CancellationToken ct = default)
            => _addWorkflow.AddAsync(client, submission, ct);

        public Task<bool> RemoveAsync(DownloadClientConfiguration client, string id, bool deleteFiles = false, CancellationToken ct = default)
            => _removalWorkflow.RemoveAsync(client, id, deleteFiles, ct);

        public Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, List<string> ids, CancellationToken ct = default)
            => _queueFetchWorkflow.GetQueueAsync(client, ids, ct);

        public Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, CancellationToken ct = default)
            => GetQueueAsync(client, [], ct);

        public Task<List<(string Id, string Name)>> GetRecentHistoryAsync(DownloadClientConfiguration client, int limit = 100, CancellationToken ct = default)
        {
            // Deluge does not expose a dedicated history endpoint via RPC.
            return Task.FromResult(new List<(string Id, string Name)>());
        }

        public Task<List<DownloadClientItem>> GetItemsAsync(DownloadClientConfiguration client, CancellationToken ct = default)
            => _itemFetchWorkflow.GetItemsAsync(client, ct);

        public Task<DownloadClientItem> GetImportItemAsync(
            DownloadClientConfiguration client,
            DownloadClientItem item,
            DownloadClientItem? previousAttempt = null,
            CancellationToken ct = default)
            => _importItemResolver.GetImportItemAsync(client, item, ct);

        public Task<QueueItem> GetImportItemAsync(
            DownloadClientConfiguration client,
            Download download,
            QueueItem queueItem,
            QueueItem? previousAttempt = null,
            CancellationToken ct = default)
            => _importItemResolver.GetImportItemAsync(client, download, queueItem, ct);

        public async Task<bool> MarkItemAsImportedAsync(
            DownloadClientConfiguration client,
            string downloadId,
            CancellationToken ct = default)
        {
            if (client == null || string.IsNullOrEmpty(downloadId))
            {
                return false;
            }

            var postImportCategory = client.Settings?.TryGetValue("postImportCategory", out var categoryObj) is true
                ? categoryObj?.ToString()
                : null;
            if (string.IsNullOrEmpty(postImportCategory))
            {
                _logger.LogDebug("No postImportCategory configured for Deluge client {ClientId}, skipping MarkItemAsImported", client.Id);
                return true;
            }

            try
            {
                await _rpcClient.InvokeAsync(client, "label.set_torrent", [downloadId, postImportCategory], ct);
                _logger.LogInformation("Marked torrent {Hash} as imported (category: {Category}) in Deluge", downloadId, postImportCategory);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Error marking torrent {Hash} as imported in Deluge", downloadId);
                return false;
            }
        }
    }
}
