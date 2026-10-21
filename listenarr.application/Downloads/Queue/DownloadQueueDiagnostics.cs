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
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Downloads.Queue
{
    internal static class DownloadQueueDiagnostics
    {
        public static void ObserveFaultedPollTask(
            Task<List<QueueItem>> pollTask,
            DownloadClientConfiguration client,
            ILogger logger)
        {
            _ = pollTask.ContinueWith(task =>
            {
                if (task.Exception != null)
                {
                    logger.LogDebug(task.Exception, "Observed late poll failure after timeout for client {ClientName}", client.Name ?? client.Id);
                    _ = task.Exception;
                }
            }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }

        public static void TryIncrementMetric(IAppMetricsService metrics, string metricName, double value = 1)
        {
            try
            {
                metrics.Increment(metricName, value);
            }
            catch (Exception caughtEx) when (caughtEx is not OperationCanceledException && caughtEx is not OutOfMemoryException && caughtEx is not StackOverflowException)
            {
                // Nothing is logged here: these metric helpers take only IAppMetricsService; a dropped metric must not pull a logger into the call.
            }
        }

        public static void TryTimingMetric(IAppMetricsService metrics, string metricName, TimeSpan duration)
        {
            try
            {
                metrics.Timing(metricName, duration);
            }
            catch (Exception caughtEx) when (caughtEx is not OperationCanceledException && caughtEx is not OutOfMemoryException && caughtEx is not StackOverflowException)
            {
                // Nothing is logged here: these metric helpers take only IAppMetricsService; a dropped metric must not pull a logger into the call.
            }
        }
    }
}
