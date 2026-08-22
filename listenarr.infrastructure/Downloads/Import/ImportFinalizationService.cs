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
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Downloads.Import
{
    public sealed class ImportFinalizationService(
        IDbContextFactory<ListenArrDbContext> dbFactory) : IImportFinalizationService
    {
        public async Task FinalizeAsync(
            string jobId,
            string downloadId,
            int audiobookId,
            string title,
            string downloadClientId,
            string correlationId,
            bool? sourceRetained,
            Dictionary<string, object>? details = null,
            CancellationToken ct = default)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var job = await db.DownloadProcessingJobs.FindAsync([jobId], ct)
                ?? throw new InvalidOperationException($"Import job {jobId} no longer exists");
            var download = await db.Downloads.FindAsync([downloadId], ct)
                ?? throw new InvalidOperationException($"Download {downloadId} no longer exists");

            if (job.Status == ProcessingJobStatus.Completed && download.Status == DownloadStatus.Moved)
            {
                return;
            }

            if (!job.HasCheckpoint("FilesImported") ||
                !job.HasCheckpoint("ClientMarkedImported") ||
                !job.HasCheckpoint("ScanEnqueued"))
            {
                throw new InvalidOperationException($"Import job {jobId} cannot be finalized before all checkpoints complete");
            }

            var durableDetails = details == null
                ? new Dictionary<string, object>()
                : new Dictionary<string, object>(details);
            durableDetails["SourceRetentionKnown"] = sourceRetained.HasValue;
            if (sourceRetained.HasValue)
            {
                durableDetails[Download.SourceRetainedMetadataKey] = sourceRetained.Value;
            }

            var alreadyRecorded = await db.History.AnyAsync(
                h => h.CorrelationId == correlationId &&
                     h.EventType == HistoryEvents.Imported &&
                     h.Outcome == HistoryOutcome.Succeeded,
                ct);
            if (!alreadyRecorded)
            {
                db.History.Add(new History
                {
                    AudiobookId = audiobookId,
                    AudiobookTitle = title,
                    SourceTitle = title,
                    DownloadId = downloadId.ToUpperInvariant(),
                    DownloadClientId = downloadClientId,
                    EventType = HistoryEvents.Imported,
                    Outcome = HistoryOutcome.Succeeded,
                    Source = "DownloadImport",
                    Message = "Download import committed",
                    Timestamp = DateTime.UtcNow,
                    CorrelationId = correlationId,
                    Data = JsonSerializer.Serialize(durableDetails)
                });
            }

            download.Imported();
            download.ActiveAudiobookDeduplicationKey = null;
            download.LastImportedAt = DateTime.UtcNow;
            if (sourceRetained.HasValue)
            {
                download.SetMetadata(Download.SourceRetainedMetadataKey, sourceRetained.Value);
            }
            else
            {
                download.Metadata.Remove(Download.SourceRetainedMetadataKey);
            }
            job.MarkAsCompleted();
            job.ActiveDeduplicationKey = null;
            job.SetCheckpoint("ImportCommitted");

            await db.SaveChangesAsync(ct);
        }
    }
}
