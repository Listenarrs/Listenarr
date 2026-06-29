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
namespace Listenarr.Tests.Mocks
{
    /// <summary>
    /// Lightweight test implementation of IDownloadClientGateway used by unit tests.
    /// It attempts to use a provided HttpClient/IHttpClientFactory to handle simple SABnzbd
    /// queue/history requests so tests that register a DelegatingHandlerMock will work.
    /// For other operations it returns conservative defaults.
    /// </summary>
    public class DownloadClientGatewayMock : IDownloadClientGateway
    {
        public List<string> SourceFiles { get; set; } = [];
        public bool RemoveResult { get; set; }
        public bool? LastRemoveDeleteFiles { get; private set; }
        public bool MarkImportedResult { get; set; } = true;
        private readonly Dictionary<string, int> MethodCalls = [];

        public Task<(bool Success, string Message)> TestConnectionAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            RegisterMethodCall(nameof(TestConnectionAsync));

            return Task.FromResult((true, "ok"));
        }

        public Task<DownloadClientSubmissionResult> AddAsync(
            DownloadClientConfiguration client,
            PreparedDownloadSubmission submission,
            CancellationToken ct = default)
        {
            RegisterMethodCall(nameof(AddAsync));

            return Task.FromResult(new DownloadClientSubmissionResult("mock-external-id"));
        }

        public Task<bool> RemoveAsync(DownloadClientConfiguration client, string id, bool deleteFiles = false, CancellationToken ct = default)
        {
            RegisterMethodCall(nameof(RemoveAsync));
            LastRemoveDeleteFiles = deleteFiles;

            return Task.FromResult(RemoveResult);
        }

        public async Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            RegisterMethodCall(nameof(GetQueueAsync));

            return [];
        }

        public async Task<List<(string Id, string Name)>> GetRecentHistoryAsync(DownloadClientConfiguration client, int limit = 100, CancellationToken ct = default)
        {
            RegisterMethodCall(nameof(GetRecentHistoryAsync));

            return [];
        }

        public Task<bool> MarkItemAsImportedAsync(DownloadClientConfiguration client, Download download, CancellationToken ct = default)
        {
            RegisterMethodCall(nameof(MarkItemAsImportedAsync));

            return Task.FromResult(MarkImportedResult);
        }

        public Task<QueueItem> GetQueueItemAsync(DownloadClientConfiguration client, Download download, QueueItem queueItem, CancellationToken ct = default)
        {
            RegisterMethodCall(nameof(GetQueueItemAsync));

            queueItem.SourceFiles = SourceFiles;
            queueItem.LocalPath = download.DownloadPath;

            return Task.FromResult(queueItem);
        }

        public async Task<List<Download>> FetchDownloadsAsync(DownloadClientConfiguration client, List<Download> downloads, CancellationToken cancellationToken = default)
        {
            RegisterMethodCall(nameof(FetchDownloadsAsync));

            return [];
        }

        private void RegisterMethodCall(string methodName)
        {
            MethodCalls.TryGetValue(methodName, out int calls);
            MethodCalls[methodName] = calls + 1;
        }

        public int GetCallCount(string methodName)
        {
            MethodCalls.TryGetValue(methodName, out int calls);
            return calls;
        }
    }
}
