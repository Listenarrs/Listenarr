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
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace Listenarr.Infrastructure.Downloads.Processing
{
    /// <summary>
    /// Process the download processing jobs queued
    /// </summary>
    public class DownloadProcessingJobProcessor(
        IServiceScopeFactory scopeFactory,
        ILogger<DownloadProcessingJobProcessor> logger,
        IAppMetricsService metrics,
        IScanQueueService scanQueueService) : BackgroundService, IDownloadImportProcessor
    {
        private readonly TimeSpan _processingInterval = TimeSpan.FromSeconds(10); // Check every 10 seconds

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation("Download Processing Background Service started");

            // On startup, reset any jobs stuck in Processing status (from previous crash/restart)
            try
            {
                using var scope = scopeFactory.CreateScope();
                var downloadProcessingJobService = scope.ServiceProvider.GetRequiredService<IDownloadProcessingJobService>();
                await downloadProcessingJobService.ResetStuckJobsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Download processing startup reset canceled during shutdown");
            }
            catch (OperationCanceledException ex)
            {
                logger.LogWarning(ex, "Download processing startup reset canceled/timed out; continuing");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex, "Failed to reset stuck jobs on startup");
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ProcessQueueAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException ex)
                {
                    logger.LogWarning(ex, "Download processing cycle canceled/timed out; continuing");
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    logger.LogError(ex, "Error processing download queue");
                }

                try
                {
                    await Task.Delay(_processingInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            logger.LogInformation("Download Processing Background Service stopped");
        }

        public async Task ProcessQueueAsync(CancellationToken cancellationToken)
        {
            using var scope = scopeFactory.CreateScope();
            var downloadProcessingJobService = scope.ServiceProvider.GetRequiredService<IDownloadProcessingJobService>();
            var downloadRepository = scope.ServiceProvider.GetRequiredService<IDownloadRepository>();
            var downloadService = scope.ServiceProvider.GetRequiredService<IDownloadService>();

            var job = await downloadProcessingJobService.GetNextJobAsync();
            if (job == null)
            {
                return;
            }

            using var logScope = logger.BeginScope(new Dictionary<string, object?>
            {
                ["JobId"] = job.Id,
                ["DownloadId"] = job.DownloadId,
                ["CorrelationId"] = job.GetOrCreateCorrelationId()
            });
            try
            {
                await ProcessJobAsync(job, cancellationToken);
            }
            catch (DownloadProcessingException exception)
            {
                logger.LogError($"Job {job.Id} failed: {exception.Message}");
                await downloadProcessingJobService.UpdateJobAsync(job.MarkAsFailed(exception.Message));
            }

            if (job.Status == ProcessingJobStatus.Failed)
            {
                // Unable to process import job and retries exceeded
                var download = await downloadRepository.GetByIdAsync(job.DownloadId);
                if (download == null)
                {
                    logger.LogError($"Download {job.DownloadId} disappeared after job {job.Id} failed");
                    return;
                }

                await downloadService.UpdateAsync(
                    download.Blocked(
                        "Unable to import the download",
                        $"See the log of job {job.Id} for more information"));
            }
        }

        /// <summary>
        /// Make sure the job is consistent, recover all related files and 
        /// send them for processing to IDownloadImportService
        /// </summary>
        /// <param name="job"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        /// <exception cref="DownloadProcessingException"></exception>
        public async Task ProcessJobAsync(DownloadProcessingJob job, CancellationToken cancellationToken)
        {
            logger.LogInformation($"Processing job {job.Id} for download {job.DownloadId}: {job.JobType}");

            using var scope = scopeFactory.CreateScope();
            var downloadRepository = scope.ServiceProvider.GetRequiredService<IDownloadRepository>();
            var downloadProcessingJobService = scope.ServiceProvider.GetRequiredService<IDownloadProcessingJobService>();
            var download = await downloadRepository.GetByIdAsync(job.DownloadId);
            if (download == null)
            {
                throw new DownloadProcessingException($"The download {job.DownloadId} does not exist anymore");
            }

            if (download.Status == DownloadStatus.Moved)
            {
                job.AddLogEntry($"Download {download.Id} is already imported; completing stale import job without work");
                await downloadProcessingJobService.UpdateJobAsync(job.MarkAsCompleted());
                return;
            }

            if (!download.AwaitsImportation())
            {
                throw new DownloadProcessingException($"Inconsistency: Download {download.Id} is not ready for importation. Current status: {download.Status}");
            }

            if (string.IsNullOrEmpty(download.DownloadPath))
            {
                throw new DownloadProcessingException($"Inconsistency: Download {download.Id} has no path set");
            }

            if (download.AudiobookId == null)
            {
                throw new DownloadProcessingException($"Inconsistency: Download {download.Id} has no audiobook related to it");
            }

            if (download.DownloadClientId == null)
            {
                throw new DownloadProcessingException($"Inconsistency: Download {download.Id} has no download client");
            }

            var audiobookRepository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            var audiobook = await audiobookRepository.GetByIdAsync((int)download.AudiobookId);
            if (audiobook == null)
            {
                throw new DownloadProcessingException($"Inconsistency: Download {download.Id}'s audiobook {download.AudiobookId} cannot be retrieved");
            }

            var isDirectDownload = string.Equals(download.DownloadClientId, DirectDownloadMetadataKeys.ClientId, StringComparison.OrdinalIgnoreCase);
            DownloadClientConfiguration? client = null;
            if (!isDirectDownload)
            {
                var configurationService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
                client = await configurationService.GetDownloadClientConfigurationAsync(download.DownloadClientId);
                if (client == null)
                {
                    throw new DownloadProcessingException($"Inconsistency: Download {download.Id}'s client {download.DownloadClientId} cannot be retrieved");
                }
            }

            var downloadService = scope.ServiceProvider.GetRequiredService<IDownloadService>();
            var historyRepository = scope.ServiceProvider.GetRequiredService<IHistoryRepository>();
            var correlationId = job.GetOrCreateCorrelationId();

            await downloadService.UpdateAsync(download.Importing());
            await downloadProcessingJobService.UpdateJobAsync(job.MarkAsProcessing());
            await RecordHistoryAsync(
                historyRepository,
                download,
                audiobook,
                HistoryEvents.ImportStarted,
                HistoryOutcome.Requested,
                correlationId,
                $"Import attempt {job.RetryCount + 1} started",
                new Dictionary<string, object> { ["JobId"] = job.Id, ["Attempt"] = job.RetryCount + 1 },
                cancellationToken);

            if (!job.HasCheckpoint("FilesImported"))
            {
                if (isDirectDownload &&
                    (string.IsNullOrEmpty(download.DownloadPath) || (!File.Exists(download.DownloadPath) && !Directory.Exists(download.DownloadPath))))
                {
                    metrics.Increment("processing.source_missing");
                    await ScheduleRetryAsync(job, downloadProcessingJobService, historyRepository, download, audiobook,
                        correlationId, $"Direct-download source path not found at processing time: {download.DownloadPath}", cancellationToken);
                    return;
                }

                QueueItem queueItem;
                List<string> files;
                try
                {
                    var downloadItemService = scope.ServiceProvider.GetRequiredService<IDownloadItemService>();
                    // External client DownloadPath may be stale or missing. Client-specific
                    // import resolvers own recovery from queue/history before the processor
                    // decides the source is unavailable.
                    queueItem = await downloadItemService.GetImportItemAsync(download, cancellationToken);
                    if (queueItem?.SourceFiles == null)
                    {
                        await ScheduleRetryAsync(job, downloadProcessingJobService, historyRepository, download, audiobook,
                            correlationId, isDirectDownload
                                ? "Unable to resolve the local direct-download file"
                                : "Unable to fetch the download from the download client", cancellationToken);
                        return;
                    }

                    job.AddLogEntry($"Resolved {queueItem.SourceFiles.Count} file(s) for import");
                    files = [.. queueItem.SourceFiles.Where(File.Exists)];
                    job.AddLogEntry($"{files.Count} file(s) remaining after checking which ones are effectively on disk");
                }
                catch (DownloadProcessingException exception)
                {
                    await ScheduleRetryAsync(job, downloadProcessingJobService, historyRepository, download, audiobook,
                        correlationId, exception.Message, cancellationToken);
                    return;
                }

                if (files.Count == 0 || files.Count != queueItem.SourceFiles.Count)
                {
                    var reason = files.Count == 0
                        ? "No importable files found"
                        : "Files reported by the download client and files on disk do not match";
                    await ScheduleRetryAsync(job, downloadProcessingJobService, historyRepository, download, audiobook,
                        correlationId, reason, cancellationToken);
                    return;
                }

                List<ImportResult> results;
                try
                {
                    var downloadImportService = scope.ServiceProvider.GetRequiredService<IDownloadImportService>();
                    var importOptions = isDirectDownload && string.Equals(
                        download.GetMetadataString(DirectDownloadMetadataKeys.RequiresArchiveExtraction),
                        bool.TrueString,
                        StringComparison.OrdinalIgnoreCase)
                        ? new DownloadImportOptions(ForceArchiveExtraction: true)
                        : null;
                    results = await downloadImportService.ImportDownloadFilesAsync(
                        audiobook,
                        files,
                        cancellationToken,
                        importOptions);
                }
                catch (InvalidOperationException exception)
                {
                    await FailImportAsync(job, downloadProcessingJobService, historyRepository, download, audiobook,
                        correlationId, exception.Message, cancellationToken);
                    return;
                }

                foreach (var result in results)
                {
                    if (!string.IsNullOrEmpty(result.Message)) job.AddLogEntry(result.Message);
                }

                if (results.Any(result => !result.Success))
                {
                    await FailImportAsync(job, downloadProcessingJobService, historyRepository, download, audiobook,
                        correlationId, "Unable to import at least one file for the job (see the log entries)", cancellationToken);
                    return;
                }

                var wasRegisteredToAudiobook = results.Any(result => result.WasRegisteredToAudiobook);
                if (!wasRegisteredToAudiobook)
                {
                    var audiobookFileRepository = scope.ServiceProvider.GetRequiredService<IAudiobookFileRepository>();
                    var existingAudiobookFiles = await audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id, cancellationToken);
                    if (existingAudiobookFiles.Count <= 0)
                    {
                        await FailImportAsync(job, downloadProcessingJobService, historyRepository, download, audiobook,
                            correlationId, "No audio files were registered after file import", cancellationToken);
                        return;
                    }
                }

                foreach (var result in results)
                {
                    var outcome = string.IsNullOrWhiteSpace(result.SourcePath) || string.IsNullOrWhiteSpace(result.FinalPath)
                        ? HistoryOutcome.Skipped
                        : HistoryOutcome.Succeeded;
                    var eventType = outcome == HistoryOutcome.Skipped
                        ? HistoryEvents.FileSkipped
                        : result.Action == Listenarr.Domain.Audiobooks.Enumerations.FileAction.Move
                            ? HistoryEvents.FileMoved
                            : HistoryEvents.FileCopied;
                    await historyRepository.AddAsync(new History
                    {
                        AudiobookId = audiobook.Id,
                        AudiobookTitle = audiobook.Title,
                        SourceTitle = Path.GetFileName(result.FinalPath ?? result.SourcePath ?? download.Title),
                        DownloadId = download.Id.ToUpperInvariant(),
                        DownloadClientId = download.DownloadClientId,
                        EventType = eventType,
                        Outcome = outcome,
                        Source = "DownloadImport",
                        Message = result.Message ?? $"{result.Action} completed",
                        Timestamp = DateTime.UtcNow,
                        CorrelationId = correlationId,
                        Data = JsonSerializer.Serialize(new
                        {
                            JobId = job.Id,
                            result.Action,
                            result.SourcePath,
                            result.FinalPath,
                            result.WasRegisteredToAudiobook
                        })
                    }, cancellationToken);
                }

                job.SetCheckpoint("FilesImported", results.Count);
                await downloadProcessingJobService.UpdateJobAsync(job);
            }

            if (!job.HasCheckpoint("ClientMarkedImported"))
            {
                if (isDirectDownload)
                {
                    // There is no external client history to mark for DDLs. The
                    // local staged file is already under Listenarr's ownership.
                    job.SetCheckpoint("ClientMarkedImported");
                    await downloadProcessingJobService.UpdateJobAsync(job);
                }
                else
                {
                    var downloadClientGateway = scope.ServiceProvider.GetRequiredService<IDownloadClientGateway>();
                    if (!await downloadClientGateway.MarkItemAsImportedAsync(client!, download, cancellationToken))
                    {
                        await ScheduleRetryAsync(job, downloadProcessingJobService, historyRepository, download, audiobook,
                            correlationId, $"Unable to mark the item imported in client {client!.Id}", cancellationToken);
                        return;
                    }

                    job.SetCheckpoint("ClientMarkedImported");
                    await downloadProcessingJobService.UpdateJobAsync(job);
                }
            }

            if (!job.HasCheckpoint("ScanEnqueued"))
            {
                Guid scanJobId;
                try
                {
                    scanJobId = await scanQueueService.EnqueueScanAsync(
                        audiobook,
                        correlationId: correlationId,
                        downloadId: download.Id);
                }
                catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
                {
                    await ScheduleRetryAsync(job, downloadProcessingJobService, historyRepository, download, audiobook,
                        correlationId, $"Unable to enqueue the post-import library scan: {exception.Message}", cancellationToken);
                    return;
                }
                job.SetCheckpoint("ScanEnqueued", scanJobId.ToString());
                job.AddLogEntry($"Enqueued scan job {scanJobId} for audiobook {audiobook.Id}");
                await downloadProcessingJobService.UpdateJobAsync(job);
                await RecordHistoryAsync(
                    historyRepository,
                    download,
                    audiobook,
                    HistoryEvents.ScanQueued,
                    HistoryOutcome.Succeeded,
                    correlationId,
                    $"Library scan {scanJobId} queued",
                    new Dictionary<string, object> { ["JobId"] = job.Id, ["ScanJobId"] = scanJobId },
                    cancellationToken);
            }

            var finalizationService = scope.ServiceProvider.GetRequiredService<IImportFinalizationService>();
            try
            {
                await finalizationService.FinalizeAsync(
                    job.Id,
                    download.Id,
                    audiobook.Id,
                    audiobook.Title ?? download.Title,
                    client?.Id ?? download.DownloadClientId,
                    correlationId,
                    new Dictionary<string, object>
                    {
                        ["JobId"] = job.Id,
                        ["ScanJobId"] = job.TryGetJobDataString("ScanEnqueuedDetail", out var scanId) ? scanId : string.Empty
                    },
                    cancellationToken);
            }
            catch (InvalidOperationException exception)
            {
                await ScheduleRetryAsync(job, downloadProcessingJobService, historyRepository, download, audiobook,
                    correlationId, $"Unable to commit import finalization: {exception.Message}", cancellationToken);
            }
        }

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
            await RecordHistoryAsync(historyRepository, download, audiobook,
                exhausted ? HistoryEvents.ImportFailed : HistoryEvents.ImportRetry,
                exhausted ? HistoryOutcome.Failed : HistoryOutcome.Retrying, correlationId, reason,
                new Dictionary<string, object> { ["JobId"] = job.Id, ["RetryCount"] = job.RetryCount }, ct);
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
            await RecordHistoryAsync(historyRepository, download, audiobook, HistoryEvents.ImportFailed,
                HistoryOutcome.Failed, correlationId, reason,
                new Dictionary<string, object> { ["JobId"] = job.Id, ["RetryCount"] = job.RetryCount }, ct);
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
