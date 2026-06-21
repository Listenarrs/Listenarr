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

namespace Listenarr.Infrastructure.HostedServices
{
    public sealed class WorkerCycleRunner(
        TimeProvider timeProvider,
        IAppMetricsService metrics,
        ILogger<WorkerCycleRunner> logger) : IWorkerCycleRunner
    {
        public async Task RunPeriodicAsync(
            string workerName,
            TimeSpan? initialDelay,
            Func<TimeSpan> intervalProvider,
            Func<CancellationToken, Task> runCycle,
            CancellationToken cancellationToken)
        {
            if (initialDelay is { } delay && delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, timeProvider, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    metrics.Increment(BuildMetricName(workerName, "cycle.skipped"));
                    logger.LogInformation("{WorkerName} canceled before first cycle", workerName);
                    return;
                }
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                using var scope = logger.BeginScope(new Dictionary<string, object?>
                {
                    ["WorkerName"] = workerName,
                    ["WorkerCycleId"] = Guid.NewGuid().ToString("N")
                });
                try
                {
                    metrics.Increment(BuildMetricName(workerName, "cycle.started"));
                    await runCycle(cancellationToken);
                    metrics.Increment(BuildMetricName(workerName, "cycle.completed"));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    metrics.Increment(BuildMetricName(workerName, "cycle.skipped"));
                    break;
                }
                catch (OperationCanceledException ex)
                {
                    metrics.Increment(BuildMetricName(workerName, "cycle.failed"));
                    logger.LogWarning(ex, "{WorkerName} cycle canceled/timed out; continuing", workerName);
                }
                catch (Exception ex) when (Listenarr.Application.Common.WorkerExceptionClassifier.IsNonFatal(ex))
                {
                    metrics.Increment(BuildMetricName(workerName, "cycle.failed"));
                    logger.LogError(ex, "Error in {WorkerName} cycle", workerName);
                }

                try
                {
                    await Task.Delay(intervalProvider(), timeProvider, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private static string BuildMetricName(string workerName, string suffix)
        {
            var normalized = new string(workerName
                .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '.')
                .ToArray());

            return $"worker.{normalized}.{suffix}";
        }
    }
}
