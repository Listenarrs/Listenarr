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
using System.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Downloads.Queue
{
    public sealed class DownloadClientQueuePoller(
        IMemoryCache cache,
        IDownloadClientGateway clientGateway,
        IAppMetricsService metrics,
        ILogger<DownloadClientQueuePoller> logger)
    {
        public async Task<List<ClientQueueFetchResult>> FetchAsync(
            List<DownloadClientConfiguration> enabledClients,
            TimeSpan clientQueueTimeout,
            TimeSpan staleSnapshotMaxAge,
            int maxParallelClientPolls)
        {
            if (enabledClients == null || enabledClients.Count == 0)
            {
                return new List<ClientQueueFetchResult>();
            }

            using var throttler = new SemaphoreSlim(maxParallelClientPolls);
            var tasks = enabledClients
                .Select(client => FetchAsync(client, throttler, clientQueueTimeout, staleSnapshotMaxAge))
                .ToArray();

            var results = await Task.WhenAll(tasks);
            return results.ToList();
        }

        private async Task<ClientQueueFetchResult> FetchAsync(
            DownloadClientConfiguration client,
            SemaphoreSlim throttler,
            TimeSpan clientQueueTimeout,
            TimeSpan staleSnapshotMaxAge)
        {
            await throttler.WaitAsync();
            try
            {
                return await FetchAsync(client, clientQueueTimeout, staleSnapshotMaxAge);
            }
            finally
            {
                throttler.Release();
            }
        }

        private async Task<ClientQueueFetchResult> FetchAsync(
            DownloadClientConfiguration client,
            TimeSpan clientQueueTimeout,
            TimeSpan staleSnapshotMaxAge)
        {
            var stopwatch = Stopwatch.StartNew();
            using var timeoutCts = new CancellationTokenSource();

            try
            {
                var pollTask = clientGateway.GetQueueAsync(client, timeoutCts.Token);
                var completedTask = await Task.WhenAny(pollTask, Task.Delay(clientQueueTimeout));
                if (completedTask != pollTask)
                {
                    timeoutCts.Cancel();
                    DownloadQueueDiagnostics.ObserveFaultedPollTask(pollTask, client, logger);

                    stopwatch.Stop();
                    DownloadQueueDiagnostics.TryIncrementMetric(metrics, "download.queue.client.poll.timeout");
                    DownloadQueueDiagnostics.TryTimingMetric(metrics, "download.queue.client.poll.duration", stopwatch.Elapsed);
                    return BuildFallbackQueueResult(client, staleSnapshotMaxAge, "timeout");
                }

                var clientQueue = await pollTask;
                stopwatch.Stop();
                var refreshedAtUtc = DateTimeOffset.UtcNow;

                CacheClientQueueSnapshot(client, clientQueue, staleSnapshotMaxAge, refreshedAtUtc);
                DownloadQueueDiagnostics.TryTimingMetric(metrics, "download.queue.client.poll.duration", stopwatch.Elapsed);

                return new ClientQueueFetchResult(
                    client,
                    DownloadQueueSnapshotMapper.CloneQueueItems(clientQueue),
                    usedCachedSnapshot: false,
                    isUnavailable: false,
                    snapshotAge: null,
                    failureReason: null,
                    snapshotState: "live",
                    snapshotRefreshedAtUtc: refreshedAtUtc);
            }
            catch (OperationCanceledException) when (!timeoutCts.IsCancellationRequested)
            {
                stopwatch.Stop();
                DownloadQueueDiagnostics.TryIncrementMetric(metrics, "download.queue.client.poll.failure");
                DownloadQueueDiagnostics.TryTimingMetric(metrics, "download.queue.client.poll.duration", stopwatch.Elapsed);

                logger.LogWarning("Queue poll for client {ClientName} was canceled before timeout; using fallback behavior", client.Name ?? client.Id);
                return BuildFallbackQueueResult(client, staleSnapshotMaxAge, "canceled");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                stopwatch.Stop();
                DownloadQueueDiagnostics.TryIncrementMetric(metrics, "download.queue.client.poll.failure");
                DownloadQueueDiagnostics.TryTimingMetric(metrics, "download.queue.client.poll.duration", stopwatch.Elapsed);

                logger.LogWarning(ex, "Error getting queue snapshot from download client {ClientName}", client.Name ?? client.Id);
                return BuildFallbackQueueResult(client, staleSnapshotMaxAge, "error");
            }
        }

        private ClientQueueFetchResult BuildFallbackQueueResult(
            DownloadClientConfiguration client,
            TimeSpan staleSnapshotMaxAge,
            string failureReason)
        {
            if (cache.TryGetValue(DownloadQueueSnapshotMapper.GetClientQueueSnapshotCacheKey(client), out ClientQueueSnapshotCacheEntry? cachedSnapshot) &&
                cachedSnapshot != null)
            {
                var snapshotAge = DateTimeOffset.UtcNow - cachedSnapshot.RefreshedAtUtc;
                if (snapshotAge <= staleSnapshotMaxAge)
                {
                    DownloadQueueDiagnostics.TryIncrementMetric(metrics, "download.queue.client.snapshot.fallback");

                    return new ClientQueueFetchResult(
                        client,
                        DownloadQueueSnapshotMapper.CloneQueueItems(cachedSnapshot.QueueItems),
                        usedCachedSnapshot: true,
                        isUnavailable: false,
                        snapshotAge: snapshotAge,
                        failureReason: failureReason,
                        snapshotState: "cached",
                        snapshotRefreshedAtUtc: cachedSnapshot.RefreshedAtUtc);
                }
            }

            return new ClientQueueFetchResult(
                client,
                new List<QueueItem>(),
                usedCachedSnapshot: false,
                isUnavailable: true,
                snapshotAge: null,
                failureReason: failureReason,
                snapshotState: "unavailable",
                snapshotRefreshedAtUtc: null);
        }

        private void CacheClientQueueSnapshot(
            DownloadClientConfiguration client,
            List<QueueItem> clientQueue,
            TimeSpan staleSnapshotMaxAge,
            DateTimeOffset refreshedAtUtc)
        {
            var cacheEntry = new ClientQueueSnapshotCacheEntry(DownloadQueueSnapshotMapper.CloneQueueItems(clientQueue), refreshedAtUtc);
            cache.Set(
                DownloadQueueSnapshotMapper.GetClientQueueSnapshotCacheKey(client),
                cacheEntry,
                new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = staleSnapshotMaxAge
                });
        }
    }
}
