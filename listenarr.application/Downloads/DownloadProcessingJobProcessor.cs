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
using Listenarr.Application.Interfaces;
using Listenarr.Domain.Models.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Listenarr.Domain.Models;
using Microsoft.Extensions.DependencyInjection;
using Listenarr.Application.Interfaces.Repositories;

namespace Listenarr.Application.Downloads
{
    /// <summary>
    /// Process the download processing jobs queued
    /// </summary>
    public class DownloadProcessingJobProcessor(
        IServiceScopeFactory scopeFactory,
        ILogger<DownloadProcessingJobProcessor> logger,
        IAppMetricsService metrics,
        IScanQueueService scanQueueService) : BackgroundService
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

        internal async Task ProcessQueueAsync(CancellationToken cancellationToken)
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
                    logger.LogError($"Download {job.DownloadId} disapeared after job {job.Id} failed");
                    return;
                }

                await downloadService.UpdateAsync(
                    download.Blocked($"Unable to import the download", "See the log of job {job.Id} for more informations"));
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
        private async Task ProcessJobAsync(DownloadProcessingJob job, CancellationToken cancellationToken)
        {
            logger.LogInformation($"Processing job {job.Id} for download {job.DownloadId}: {job.JobType}");

            using var scope = scopeFactory.CreateScope();
            var downloadRepository = scope.ServiceProvider.GetRequiredService<IDownloadRepository>();
            var download = await downloadRepository.GetByIdAsync(job.DownloadId);
            if (download == null)
            {
                throw new DownloadProcessingException($"The download {job.DownloadId} does not exist anymore");
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

            var configurationService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
            var client = await configurationService.GetDownloadClientConfigurationAsync(download.DownloadClientId);
            if (client == null)
            {
                throw new DownloadProcessingException($"Inconsistency: Download {download.Id}'s client {download.DownloadClientId} cannot be retrieved");
            }

            var downloadService = scope.ServiceProvider.GetRequiredService<IDownloadService>();
            var downloadProcessingJobService = scope.ServiceProvider.GetRequiredService<IDownloadProcessingJobService>();

            await downloadService.UpdateAsync(download.Importing());
            await downloadProcessingJobService.UpdateJobAsync(job.MarkAsProcessing());

            if (string.IsNullOrEmpty(download.DownloadPath) || (!File.Exists(download.DownloadPath) && !Directory.Exists(download.DownloadPath)))
            {
                // Source missing at processing-time. Schedule a retry instead of throwing so transient
                // races (file still being moved by another process) don't permanently fail the job.
                metrics.Increment("processing.source_missing");
                await downloadProcessingJobService.UpdateJobAsync(job.ScheduleRetry($"Source path not found at processing time: {job.SourcePath}"));
                return;
            }

            QueueItem queueItem;
            List<string> files = [];
            try
            {
                var downloadItemService = scope.ServiceProvider.GetRequiredService<IDownloadItemService>();
                queueItem = await downloadItemService.GetImportItemAsync(download, cancellationToken);
                if (queueItem == null || queueItem.SourceFiles == null)
                {
                    await downloadProcessingJobService.UpdateJobAsync(job.ScheduleRetry($"Unable to fetch the download from the download client"));
                    return;
                }

                job.AddLogEntry($"Download client reported {queueItem.SourceFiles.Count} file(s) downloaded");

                files = [.. queueItem.SourceFiles.Where(f => File.Exists(f))];

                job.AddLogEntry($"{files.Count} file(s) remaining after checking which ones are effectively on disk");
            }
            catch (DownloadProcessingException exception)
            {
                await downloadProcessingJobService.UpdateJobAsync(job.ScheduleRetry(exception.Message));
                return;
            }

            if (files.Count == 0)
            {
                await downloadProcessingJobService.UpdateJobAsync(job.ScheduleRetry("No importable files found"));
                return;
            }
            else if (files.Count != queueItem.SourceFiles.Count)
            {
                await downloadProcessingJobService.UpdateJobAsync(job.ScheduleRetry($"Files reported by the download client and files on disk do not match"));
                return;
            }

            List<ImportResult> results = [];
            try
            {
                var downloadImportService = scope.ServiceProvider.GetRequiredService<IDownloadImportService>();
                results = await downloadImportService.ImportDownloadFilesAsync(audiobook, files, cancellationToken);
            }
            catch (InvalidOperationException exception)
            {
                await downloadProcessingJobService.UpdateJobAsync(job.MarkAsFailed(exception.Message));
                return;
            }

            // Create the report on the job log
            foreach (var result in results)
            {
                if (!string.IsNullOrEmpty(result.Message))
                {
                    job.AddLogEntry(result.Message);
                }
            }

            // Create the report on the job log
            bool wasRegisteredToAudiobook = false;
            foreach (var result in results)
            {
                if (!result.Success)
                {
                    await downloadProcessingJobService.UpdateJobAsync(job.MarkAsFailed($"Unable to import at least one file for the job (see the log entries)"));
                    return;
                }

                wasRegisteredToAudiobook |= result.WasRegisteredToAudiobook;
            }

            if (!wasRegisteredToAudiobook)
            {
                // If the audiobook already had some audiobook file, this download has probably been skipped
                // FIXME: We should improve ImportResult to be able to report skipped files so we don't rely on DB check here
                var audiobookFileRepository = scope.ServiceProvider.GetRequiredService<IAudiobookFileRepository>();
                var existingAudiobookFiles = await audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id, cancellationToken);
                if (existingAudiobookFiles.Count <= 0)
                {
                    await downloadProcessingJobService.UpdateJobAsync(job.MarkAsFailed($"Unexpected issue: No audio files were registered to the audiobook but files have been imported"));
                    return;
                }
            }

            await downloadService.UpdateAsync(download.Imported());

            // Enqueue a scan using the audiobook's configured library path. The import process already
            // hardlinks/copies files into the library folder, so the scanner should
            // verify the library location and not the download directory, which would
            // trigger spurious "Refusing to associate file outside audiobook folder"
            // warnings from AudioFileService.
            var jobId = await scanQueueService.EnqueueScanAsync(audiobook);
            job.AddLogEntry($"Enqueued scan job {jobId} for audiobook {audiobook.Id}");

            await downloadProcessingJobService.UpdateJobAsync(job.MarkAsCompleted());
        }
    }
}


