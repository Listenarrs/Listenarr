/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Sabnzbd
{
    /// <summary>
    /// SABnzbd protocol facade.
    /// Downloader-specific queue, history, add, and import behavior lives in
    /// workflows so all download clients are sliced consistently.
    /// </summary>
    public class SabnzbdAdapter : IDownloadClientAdapter
    {
        public string ClientId => DownloadClientTypes.Sabnzbd;
        public string ClientType => DownloadClientTypes.Sabnzbd;
        public DownloadProtocol Protocol => DownloadProtocol.Usenet;

        private readonly SabnzbdConnectionTester _connectionTester;
        private readonly SabnzbdAddWorkflow _addWorkflow;
        private readonly SabnzbdRemovalWorkflow _removalWorkflow;
        private readonly SabnzbdQueueFetchWorkflow _queueFetchWorkflow;
        private readonly SabnzbdHistoryFetchWorkflow _historyFetchWorkflow;
        private readonly SabnzbdItemFetchWorkflow _itemFetchWorkflow;
        private readonly SabnzbdImportItemResolver _importItemResolver;

        internal SabnzbdAdapter(
            SabnzbdConnectionTester connectionTester,
            SabnzbdAddWorkflow addWorkflow,
            SabnzbdRemovalWorkflow removalWorkflow,
            SabnzbdQueueFetchWorkflow queueFetchWorkflow,
            SabnzbdHistoryFetchWorkflow historyFetchWorkflow,
            SabnzbdItemFetchWorkflow itemFetchWorkflow,
            SabnzbdImportItemResolver importItemResolver)
        {
            _connectionTester = connectionTester ?? throw new ArgumentNullException(nameof(connectionTester));
            _addWorkflow = addWorkflow ?? throw new ArgumentNullException(nameof(addWorkflow));
            _removalWorkflow = removalWorkflow ?? throw new ArgumentNullException(nameof(removalWorkflow));
            _queueFetchWorkflow = queueFetchWorkflow ?? throw new ArgumentNullException(nameof(queueFetchWorkflow));
            _historyFetchWorkflow = historyFetchWorkflow ?? throw new ArgumentNullException(nameof(historyFetchWorkflow));
            _itemFetchWorkflow = itemFetchWorkflow ?? throw new ArgumentNullException(nameof(itemFetchWorkflow));
            _importItemResolver = importItemResolver ?? throw new ArgumentNullException(nameof(importItemResolver));
        }

        internal SabnzbdAdapter(
            IHttpClientFactory httpFactory,
            INzbUrlResolver nzbUrlResolver,
            ILogger<SabnzbdAdapter> logger)
        {
            ArgumentNullException.ThrowIfNull(httpFactory);
            _ = nzbUrlResolver ?? throw new ArgumentNullException(nameof(nzbUrlResolver));
            ArgumentNullException.ThrowIfNull(logger);

            var requestBuilder = new SabnzbdRequestBuilder();
            _connectionTester = new SabnzbdConnectionTester(httpFactory, requestBuilder, logger, ClientType);
            _addWorkflow = new SabnzbdAddWorkflow(httpFactory, requestBuilder, logger, ClientType);
            _removalWorkflow = new SabnzbdRemovalWorkflow(httpFactory, requestBuilder, logger, ClientType);
            _queueFetchWorkflow = new SabnzbdQueueFetchWorkflow(httpFactory, requestBuilder, logger, ClientType);
            _historyFetchWorkflow = new SabnzbdHistoryFetchWorkflow(httpFactory, requestBuilder, logger, ClientType);
            _itemFetchWorkflow = new SabnzbdItemFetchWorkflow(httpFactory, requestBuilder, logger, ClientType);
            _importItemResolver = new SabnzbdImportItemResolver(httpFactory, requestBuilder, logger, ClientType);
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

        public async Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, List<string> ids, CancellationToken ct = default)
        {
            var items = await _queueFetchWorkflow.GetQueueAsync(client, ids, ct);
            return FilterByIds(items, ids);
        }

        public Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, CancellationToken ct = default)
            => GetQueueAsync(client, [], ct);

        public Task<List<(string Id, string Name)>> GetRecentHistoryAsync(DownloadClientConfiguration client, int limit = 100, CancellationToken ct = default)
            => _historyFetchWorkflow.GetRecentHistoryAsync(client, limit, ct);

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
            => _importItemResolver.GetImportItemAsync(client, queueItem, ct);

        private static List<QueueItem> FilterByIds(List<QueueItem> items, List<string> ids)
        {
            if (ids.Count == 0)
            {
                return items;
            }

            var idSet = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return [.. items.Where(item => !string.IsNullOrWhiteSpace(item.Id) && idSet.Contains(item.Id))];
        }
    }
}
