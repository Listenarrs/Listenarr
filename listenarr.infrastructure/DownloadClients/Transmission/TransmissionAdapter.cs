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

namespace Listenarr.Infrastructure.DownloadClients.Transmission
{
    /// <summary>
    /// Transmission protocol facade.
    /// Client-specific RPC behavior lives in workflows so all download clients
    /// stay uniformly sliced around the IDownloadClientAdapter contract.
    /// </summary>
    public class TransmissionAdapter : IDownloadClientAdapter
    {
        public string ClientId => DownloadClientTypes.Transmission;
        public string ClientType => DownloadClientTypes.Transmission;
        public DownloadProtocol Protocol => DownloadProtocol.Torrent;

        private readonly TransmissionConnectionTester _connectionTester;
        private readonly TransmissionAddWorkflow _addWorkflow;
        private readonly TransmissionRemovalWorkflow _removalWorkflow;
        private readonly TransmissionQueueFetchWorkflow _queueFetchWorkflow;
        private readonly TransmissionItemFetchWorkflow _itemFetchWorkflow;
        private readonly TransmissionImportItemResolver _importItemResolver;

        internal TransmissionAdapter(
            TransmissionConnectionTester connectionTester,
            TransmissionAddWorkflow addWorkflow,
            TransmissionRemovalWorkflow removalWorkflow,
            TransmissionQueueFetchWorkflow queueFetchWorkflow,
            TransmissionItemFetchWorkflow itemFetchWorkflow,
            TransmissionImportItemResolver importItemResolver)
        {
            _connectionTester = connectionTester ?? throw new ArgumentNullException(nameof(connectionTester));
            _addWorkflow = addWorkflow ?? throw new ArgumentNullException(nameof(addWorkflow));
            _removalWorkflow = removalWorkflow ?? throw new ArgumentNullException(nameof(removalWorkflow));
            _queueFetchWorkflow = queueFetchWorkflow ?? throw new ArgumentNullException(nameof(queueFetchWorkflow));
            _itemFetchWorkflow = itemFetchWorkflow ?? throw new ArgumentNullException(nameof(itemFetchWorkflow));
            _importItemResolver = importItemResolver ?? throw new ArgumentNullException(nameof(importItemResolver));
        }

        internal TransmissionAdapter(IHttpClientFactory httpClientFactory, ITorrentFileDownloader torrentFileDownloader, ILogger<TransmissionAdapter> logger)
        {
            ArgumentNullException.ThrowIfNull(httpClientFactory);
            _ = torrentFileDownloader ?? throw new ArgumentNullException(nameof(torrentFileDownloader));
            ArgumentNullException.ThrowIfNull(logger);

            var rpcClient = new TransmissionRpcClient(httpClientFactory, ClientType, logger);
            _connectionTester = new TransmissionConnectionTester(rpcClient, logger);
            _addWorkflow = new TransmissionAddWorkflow(rpcClient, logger);
            _removalWorkflow = new TransmissionRemovalWorkflow(rpcClient, logger);
            _queueFetchWorkflow = new TransmissionQueueFetchWorkflow(rpcClient, logger);
            _itemFetchWorkflow = new TransmissionItemFetchWorkflow(rpcClient, logger);
            _importItemResolver = new TransmissionImportItemResolver(rpcClient, logger);
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
            // Transmission does not expose a dedicated history endpoint via RPC.
            return Task.FromResult(new List<(string Id, string Name)>());
        }

        public Task<List<DownloadClientItem>> GetItemsAsync(DownloadClientConfiguration client, CancellationToken ct = default)
            => _itemFetchWorkflow.GetItemsAsync(client, ct);

        /// <summary>
        /// Get import item from DownloadClientItem.
        /// </summary>
        public Task<DownloadClientItem> GetImportItemAsync(
            DownloadClientConfiguration client,
            DownloadClientItem item,
            DownloadClientItem? previousAttempt = null,
            CancellationToken ct = default)
            => _importItemResolver.GetImportItemAsync(client, item, ct);

        /// <summary>
        /// LEGACY: Resolves the actual import item for a completed download.
        /// Queries Transmission API for downloadDir and builds the content path.
        /// </summary>
        public Task<QueueItem> GetImportItemAsync(
            DownloadClientConfiguration client,
            Download download,
            QueueItem queueItem,
            QueueItem? previousAttempt = null,
            CancellationToken ct = default)
            => _importItemResolver.GetImportItemAsync(client, queueItem, ct);
    }
}
