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

namespace Listenarr.Infrastructure.Downloads.Processing
{
    public partial class DownloadProcessingJobProcessor
    {
        private static async Task ScheduleRetryAsync(
            DownloadProcessingJob job,
            IDownloadProcessingJobService jobService,
            IHistoryRepository historyRepository,
            Download download,
            Audiobook audiobook,
            string correlationId,
            string reason,
            CancellationToken ct)
        {
            job.ScheduleRetry(reason);
            await jobService.UpdateJobAsync(job);
            var exhausted = job.Status == ProcessingJobStatus.Failed;
            await RecordHistoryAsync(
                historyRepository,
                download,
                audiobook,
                exhausted ? HistoryEvents.ImportFailed : HistoryEvents.ImportRetry,
                exhausted ? HistoryOutcome.Failed : HistoryOutcome.Retrying,
                correlationId,
                reason,
                new Dictionary<string, object>
                {
                    ["JobId"] = job.Id,
                    ["RetryCount"] = job.RetryCount
                },
                ct);
        }

        private static async Task FailImportAsync(
            DownloadProcessingJob job,
            IDownloadProcessingJobService jobService,
            IHistoryRepository historyRepository,
            Download download,
            Audiobook audiobook,
            string correlationId,
            string reason,
            CancellationToken ct)
        {
            await jobService.UpdateJobAsync(job.MarkAsFailed(reason));
            await RecordHistoryAsync(
                historyRepository,
                download,
                audiobook,
                HistoryEvents.ImportFailed,
                HistoryOutcome.Failed,
                correlationId,
                reason,
                new Dictionary<string, object>
                {
                    ["JobId"] = job.Id,
                    ["RetryCount"] = job.RetryCount
                },
                ct);
        }

        private static Task RecordHistoryAsync(
            IHistoryRepository historyRepository,
            Download download,
            Audiobook audiobook,
            string eventType,
            HistoryOutcome outcome,
            string correlationId,
            string message,
            Dictionary<string, object> details,
            CancellationToken ct) =>
            historyRepository.AddAsync(new History
            {
                AudiobookId = audiobook.Id,
                AudiobookTitle = audiobook.Title,
                SourceTitle = download.Title,
                DownloadId = download.Id.ToUpperInvariant(),
                DownloadClientId = download.DownloadClientId,
                EventType = eventType,
                Outcome = outcome,
                Source = "DownloadImport",
                Message = message,
                Error = outcome == HistoryOutcome.Failed ? message : null,
                Timestamp = DateTime.UtcNow,
                CorrelationId = correlationId,
                Data = JsonSerializer.Serialize(details)
            }, ct);
    }
}
