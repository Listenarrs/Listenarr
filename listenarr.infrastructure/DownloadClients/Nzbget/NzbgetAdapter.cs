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

namespace Listenarr.Infrastructure.DownloadClients.Nzbget
{
    public class NzbgetAdapter : IDownloadClientAdapter
    {
        public string ClientId => DownloadClientTypes.Nzbget;
        public string ClientType => DownloadClientTypes.Nzbget;
        public DownloadProtocol Protocol => DownloadProtocol.Usenet;

        private readonly NzbgetConnectionTester _connectionTester;
        private readonly NzbgetAddWorkflow _addWorkflow;
        private readonly NzbgetRemovalWorkflow _removalWorkflow;
        private readonly NzbgetQueueFetchWorkflow _queueFetchWorkflow;
        private readonly NzbgetHistoryFetchWorkflow _historyFetchWorkflow;
        private readonly NzbgetItemFetchWorkflow _itemFetchWorkflow;
        private readonly NzbgetImportItemResolver _importItemResolver;

        internal NzbgetAdapter(
            NzbgetConnectionTester connectionTester,
            NzbgetAddWorkflow addWorkflow,
            NzbgetRemovalWorkflow removalWorkflow,
            NzbgetQueueFetchWorkflow queueFetchWorkflow,
            NzbgetHistoryFetchWorkflow historyFetchWorkflow,
            NzbgetItemFetchWorkflow itemFetchWorkflow,
            NzbgetImportItemResolver importItemResolver)
        {
            _connectionTester = connectionTester ?? throw new ArgumentNullException(nameof(connectionTester));
            _addWorkflow = addWorkflow ?? throw new ArgumentNullException(nameof(addWorkflow));
            _removalWorkflow = removalWorkflow ?? throw new ArgumentNullException(nameof(removalWorkflow));
            _queueFetchWorkflow = queueFetchWorkflow ?? throw new ArgumentNullException(nameof(queueFetchWorkflow));
            _historyFetchWorkflow = historyFetchWorkflow ?? throw new ArgumentNullException(nameof(historyFetchWorkflow));
            _itemFetchWorkflow = itemFetchWorkflow ?? throw new ArgumentNullException(nameof(itemFetchWorkflow));
            _importItemResolver = importItemResolver ?? throw new ArgumentNullException(nameof(importItemResolver));
        }

        internal NzbgetAdapter(
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
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(timeProvider);

            var xmlRpcClient = new NzbgetXmlRpcClient(httpClientFactory, ClientType);
            var historyReader = new NzbgetHistoryReader(xmlRpcClient);
            var historyEnrichmentWorkflow = new NzbgetHistoryEnrichmentWorkflow(
                historyReader,
                logger,
                timeProvider);

            _connectionTester = new NzbgetConnectionTester(xmlRpcClient, logger);
            _addWorkflow = new NzbgetAddWorkflow(xmlRpcClient, logger);
            _removalWorkflow = new NzbgetRemovalWorkflow(xmlRpcClient, logger);
            _queueFetchWorkflow = new NzbgetQueueFetchWorkflow(xmlRpcClient, historyEnrichmentWorkflow, logger);
            _historyFetchWorkflow = new NzbgetHistoryFetchWorkflow(xmlRpcClient, logger);
            _itemFetchWorkflow = new NzbgetItemFetchWorkflow(xmlRpcClient, historyEnrichmentWorkflow, logger);
            _importItemResolver = new NzbgetImportItemResolver(xmlRpcClient, logger);
        }

        public Task<(bool Success, string Message)> TestConnectionAsync(DownloadClientConfiguration client, CancellationToken ct = default)
            => _connectionTester.TestConnectionAsync(client, ct);

        public Task<DownloadClientSubmissionResult> AddAsync(
            DownloadClientConfiguration client,
            PreparedDownloadSubmission submission,
            CancellationToken ct = default)
        {
            if (submission is not PreparedUsenetSubmission usenet)
                throw new DownloadClientSubmissionException("NZBGet requires a prepared Usenet submission.");
            return _addWorkflow.AddAsync(client, usenet, ct);
        }

        public Task<bool> RemoveAsync(DownloadClientConfiguration client, string id, bool deleteFiles = false, CancellationToken ct = default)
            => _removalWorkflow.RemoveAsync(client, id, deleteFiles, ct);

        public Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, List<string> ids, CancellationToken ct = default)
            => _queueFetchWorkflow.GetQueueAsync(client, ids, ct);

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
            => _importItemResolver.GetImportItemAsync(client, item);

        public Task<QueueItem> GetImportItemAsync(
            DownloadClientConfiguration client,
            Download download,
            QueueItem queueItem,
            QueueItem? previousAttempt = null,
            CancellationToken ct = default)
            => _importItemResolver.GetImportItemAsync(client, queueItem);
    }
}
