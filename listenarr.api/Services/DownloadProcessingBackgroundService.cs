/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2025 Robbie Davis
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

using System.Runtime.InteropServices;
using Listenarr.Domain.Models;
using Microsoft.EntityFrameworkCore;
using System.Linq;
using Listenarr.Infrastructure.Models;

namespace Listenarr.Api.Services
{
    /// <summary>
    /// Background service that processes the download post-processing queue
    /// </summary>
    public class DownloadProcessingBackgroundService : BackgroundService
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly ILogger<DownloadProcessingBackgroundService> _logger;
        private readonly TimeSpan _processingInterval = TimeSpan.FromSeconds(10); // Check every 10 seconds
        private readonly IAppMetricsService _metrics;

        public DownloadProcessingBackgroundService(
            IServiceScopeFactory serviceScopeFactory,
            ILogger<DownloadProcessingBackgroundService> logger,
            IAppMetricsService metrics)
        {
            _serviceScopeFactory = serviceScopeFactory;
            _logger = logger;
            _metrics = metrics;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Download Processing Background Service started");

            // On startup, reset any jobs stuck in Processing status (from previous crash/restart)
            try
            {
                await ResetStuckJobsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Failed to reset stuck jobs on startup");
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Ensure any previously completed downloads are enqueued for processing
                    await EnqueueCompletedDownloadsAsync(stoppingToken);

                    await ProcessQueueAsync(stoppingToken);
                    await ProcessRetryJobsAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                    _logger.LogError(ex, "Error processing download queue");
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

            _logger.LogInformation("Download Processing Background Service stopped");
        }

        // Use FileUtils.GetUniqueDestinationPath instead of a local implementation

        private async Task ProcessQueueAsync(CancellationToken cancellationToken)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var queueService = scope.ServiceProvider.GetRequiredService<IDownloadProcessingQueueService>();
            var importItemResolution = scope.ServiceProvider.GetRequiredService<IImportItemResolutionService>();

            var job = await queueService.GetNextJobAsync();
            if (job == null) return;

            _logger.LogInformation("Processing job {JobId} for download {DownloadId}: {JobType}",
                job.Id, job.DownloadId, job.JobType);

            // Mark job as processing
            job.Status = ProcessingJobStatus.Processing;
            job.StartedAt = DateTime.UtcNow;
            job.AddLogEntry("Started processing");
            await queueService.UpdateJobAsync(job);

            try
            {
                await ProcessJobAsync(job, scope, cancellationToken);

                // Only mark the job as completed if it is still in Processing state.
                // Some job handlers may set the job to Failed/Retry/Skipped and we should respect that.
                if (job.Status == ProcessingJobStatus.Processing)
                {
                    job.MarkAsCompleted();
                    _logger.LogInformation("Successfully completed job {JobId} for download {DownloadId}",
                        job.Id, job.DownloadId);
                }
                else
                {
                    _logger.LogInformation("Job {JobId} for download {DownloadId} finished with status {Status}", job.Id, job.DownloadId, job.Status);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Failed to process job {JobId} for download {DownloadId}: {Error}",
                    job.Id, job.DownloadId, ex.Message);

                job.AddLogEntry($"Processing failed: {ex.Message}");
                job.ScheduleRetry();
            }

            await queueService.UpdateJobAsync(job);
        }

        /// <summary>
        /// Reset jobs that were stuck in Processing status from a previous session (e.g., after crash or restart).
        /// This prevents orphaned jobs from blocking new finalization attempts.
        /// </summary>
        private async Task ResetStuckJobsAsync(CancellationToken cancellationToken)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var queueService = scope.ServiceProvider.GetRequiredService<IDownloadProcessingQueueService>();
            var dbContext = scope.ServiceProvider.GetRequiredService<ListenArrDbContext>();

            // Find jobs stuck in Processing status (not updated recently)
            var stuckJobs = await dbContext.DownloadProcessingJobs
                .Where(j => j.Status == ProcessingJobStatus.Processing)
                .ToListAsync(cancellationToken);

            if (stuckJobs.Any())
            {
                _logger.LogInformation("Found {Count} stuck jobs in Processing status, resetting to Pending", stuckJobs.Count);
                foreach (var job in stuckJobs)
                {
                    job.Status = ProcessingJobStatus.Pending;
                    job.AddLogEntry("Reset from stuck Processing state after service restart");
                    _logger.LogInformation("Reset stuck job {JobId} for download {DownloadId}", job.Id, job.DownloadId);
                }
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        private async Task ProcessRetryJobsAsync(CancellationToken cancellationToken)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var queueService = scope.ServiceProvider.GetRequiredService<IDownloadProcessingQueueService>();

            var retryJobs = await queueService.GetRetryJobsAsync();

            foreach (var job in retryJobs)
            {
                _logger.LogInformation("Retrying job {JobId} for download {DownloadId} (attempt {Attempt}/{MaxAttempts})",
                    job.Id, job.DownloadId, job.RetryCount + 1, job.MaxRetries);

                // Reset job to pending for processing
                job.Status = ProcessingJobStatus.Pending;
                job.ErrorMessage = null;
                job.AddLogEntry($"Retry #{job.RetryCount} scheduled");

                await queueService.UpdateJobAsync(job);
            }
        }

        /// <summary>
        /// Find completed downloads that are not yet enqueued for processing and add them to the queue.
        /// This runs briefly each loop to ensure existing completed items are eventually processed.
        /// </summary>
        private async Task EnqueueCompletedDownloadsAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ListenArrDbContext>();
                var queueService = scope.ServiceProvider.GetRequiredService<IDownloadProcessingQueueService>();
                var pathMapping = scope.ServiceProvider.GetService<IRemotePathMappingService>();
                var importItemResolution = scope.ServiceProvider.GetRequiredService<IImportItemResolutionService>();

                // Find recent completed downloads that have not yet been processed into jobs
                var candidates = await dbContext.Downloads
                    .Where(d => d.Status == DownloadStatus.Completed)
                    .OrderByDescending(d => d.CompletedAt)
                    .Take(200)
                    .ToListAsync(cancellationToken);

                // Batch load all processing jobs for these candidates to avoid N+1 queries
                var candidateIds = candidates.Select(d => d.Id).ToList();
                var allJobsForCandidates = await dbContext.DownloadProcessingJobs
                    .Where(j => candidateIds.Contains(j.DownloadId))
                    .ToListAsync(cancellationToken);

                // Group jobs by DownloadId for efficient lookup
                var jobsByDownloadId = allJobsForCandidates
                    .GroupBy(j => j.DownloadId)
                    .ToDictionary(g => g.Key, g => g.ToList());

                foreach (var dl in candidates)
                {
                    try
                    {
                        // Skip if there is already a job for this download pending/processing/retry
                        if (!jobsByDownloadId.TryGetValue(dl.Id, out var existingJobs))
                        {
                            existingJobs = new List<DownloadProcessingJob>();
                        }
                        if (existingJobs.Any(j => j.Status == ProcessingJobStatus.Pending || j.Status == ProcessingJobStatus.Processing || j.Status == ProcessingJobStatus.Retry))
                        {
                            continue;
                        }

                        // Use V2 pattern: Call GetImportItem to resolve the accurate path
                        // Build a basic QueueItem from the download data
                        var preliminaryItem = new QueueItem
                        {
                            Id = dl.Id,
                            Title = dl.Title ?? "Unknown",
                            Status = "completed",
                            ContentPath = dl.FinalPath ?? dl.DownloadPath,
                            DownloadClientId = dl.DownloadClientId
                        };

                        // Resolve the import item via the download client adapter
                        var resolvedItem = await importItemResolution.ResolveImportItemAsync(
                            dl,
                            preliminaryItem,
                            previousAttempt: null,
                            cancellationToken);

                        var resolvedPath = resolvedItem.ContentPath;

                        // Apply path mapping if needed
                        if (pathMapping != null && !string.IsNullOrEmpty(dl.DownloadClientId) && !string.IsNullOrEmpty(resolvedPath))
                        {
                            try
                            {
                                var translated = await pathMapping.TranslatePathAsync(dl.DownloadClientId, resolvedPath);
                                if (!string.IsNullOrEmpty(translated))
                                {
                                    resolvedPath = translated;
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                                _logger.LogDebug(ex, "Path mapping failed for {Path}", resolvedPath);
                            }
                        }

                        if (!string.IsNullOrEmpty(resolvedPath) && (File.Exists(resolvedPath) || Directory.Exists(resolvedPath)))
                        {
                            // Queue for processing using the resolved path
                            await queueService.QueueDownloadProcessingAsync(dl.Id, resolvedPath, dl.DownloadClientId);
                            _logger.LogInformation("Enqueued completed download {DownloadId} for processing: {Source}", dl.Id, resolvedPath);
                        }
                        else if (!string.IsNullOrEmpty(resolvedPath))
                        {
                            _logger.LogDebug("Resolved path does not exist yet for download {DownloadId}: {Path}", dl.Id, resolvedPath);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                        _logger.LogDebug(ex, "Failed to consider completed download {DownloadId} for enqueue", dl.Id);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogWarning(ex, "Error while enqueuing completed downloads");
            }
        }

        private async Task ProcessJobAsync(DownloadProcessingJob job, IServiceScope scope, CancellationToken cancellationToken)
        {
            switch (job.JobType)
            {
                case ProcessingJobType.MoveOrCopyFile:
                    await ProcessMoveOrCopyJobAsync(job, scope, cancellationToken);
                    break;
                case ProcessingJobType.ExtractMetadata:
                    // Older jobs in the queue may use job types that are no longer supported.
                    // Mark them as failed with a helpful message and do not throw to avoid retry storms.
                    job.AddLogEntry("Job type ExtractMetadata is not supported");
                    job.ErrorMessage = "Job type ExtractMetadata is not supported";
                    job.Status = ProcessingJobStatus.Failed;
                    job.CompletedAt = DateTime.UtcNow;
                    break;
                default:
                    throw new NotSupportedException($"Job type {job.JobType} is not supported");
            }
        }

        private async Task ProcessMoveOrCopyJobAsync(DownloadProcessingJob job, IServiceScope scope, CancellationToken cancellationToken)
        {
            var downloadService = scope.ServiceProvider.GetRequiredService<IDownloadService>();
            var configService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
            var pathMappingService = scope.ServiceProvider.GetService<IRemotePathMappingService>();
            var fileNamingService = scope.ServiceProvider.GetService<IFileNamingService>();
            var metadataService = scope.ServiceProvider.GetService<IMetadataService>();

            job.AddLogEntry($"Starting file processing: {job.SourcePath}");

            if (string.IsNullOrEmpty(job.SourcePath) || (!File.Exists(job.SourcePath) && !Directory.Exists(job.SourcePath)))
            {
                // Apply path mapping if needed
                var localPath = job.SourcePath ?? "";
                if (pathMappingService != null && !string.IsNullOrEmpty(job.DownloadClientId))
                {
                    try
                    {
                        localPath = await pathMappingService.TranslatePathAsync(job.DownloadClientId, job.SourcePath ?? "");
                        if (!string.Equals(localPath, job.SourcePath, StringComparison.OrdinalIgnoreCase))
                        {
                            job.AddLogEntry($"Applied path mapping: {job.SourcePath} -> {localPath}");
                            job.SourcePath = localPath;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                        job.AddLogEntry($"Path mapping failed: {ex.Message}");
                    }
                }

                if (!File.Exists(localPath) && !Directory.Exists(localPath))
                {
                    // Source missing at processing-time. Schedule a retry instead of throwing so transient
                    // races (file still being moved by another process) don't permanently fail the job.
                    job.AddLogEntry($"Source path not found at processing time: {localPath}");
                    _metrics?.Increment("processing.source_missing");
                    job.ScheduleRetry();
                    job.ErrorMessage = $"Source path not found at processing time: {localPath}";
                    return;
                }
            }

            // Get application settings
            var settings = await configService.GetApplicationSettingsAsync();
            job.AddLogEntry($"Retrieved settings - OutputPath: {settings.OutputPath}, EnableMetadataProcessing: {settings.EnableMetadataProcessing}");

            // Process the file using the enhanced logic from ProcessCompletedDownloadAsync
            await ProcessFileWithEnhancedLogicAsync(job, downloadService, settings, fileNamingService, metadataService, cancellationToken);
        }

        private async Task ProcessFileWithEnhancedLogicAsync(
            DownloadProcessingJob job,
            IDownloadService downloadService,
            ApplicationSettings settings,
            IFileNamingService? fileNamingService,
            IMetadataService? metadataService,
            CancellationToken cancellationToken)
        {
            var sourcePath = job.SourcePath!;
            var destinationPath = sourcePath;

            // Handle file move/copy operations if configured
            if (!string.IsNullOrEmpty(settings.OutputPath))
            {
                job.AddLogEntry($"Processing with output path: {settings.OutputPath}");

                // Determine destination path based on settings
                if (fileNamingService != null && settings.EnableMetadataProcessing)
                {
                    job.AddLogEntry("Using file naming service for destination path");

                    // Build metadata for naming - get download info from database
                    var metadata = new AudioMetadata { Title = "Unknown Title" };
                    // When possible we'll build a namingMetadata from the linked Audiobook to ensure
                    // audiobook fields are authoritative for naming (avoid extracted tags overwriting them).
                    AudioMetadata? namingMetadata = null;

                    using var scope = _serviceScopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<ListenArrDbContext>();
                    var download = await dbContext.Downloads.FindAsync(job.DownloadId);
                    if (download != null)
                    {
                        // Start with values from the download record
                        metadata.Title = download.Title ?? metadata.Title;
                        metadata.Artist = download.Artist ?? string.Empty;
                        metadata.Album = download.Album ?? string.Empty;
                        job.AddLogEntry($"Using download metadata: {metadata.Title} by {metadata.Artist}");

                        // If the download is linked to an Audiobook, prefer its metadata for naming
                        if (download.AudiobookId != null)
                        {
                            try
                            {
                                var audiobook = await dbContext.Audiobooks.FindAsync(download.AudiobookId);
                                if (audiobook != null)
                                {
                                    // Create a naming-only metadata object from the Audiobook. This will be
                                    // used as the authoritative source for file naming fields.
                                    namingMetadata = new AudioMetadata
                                    {
                                        Title = audiobook.Title ?? metadata.Title,
                                        Artist = (audiobook.Authors != null && audiobook.Authors.Any()) ? string.Join(", ", audiobook.Authors) : metadata.Artist,
                                        AlbumArtist = (audiobook.Authors != null && audiobook.Authors.Any()) ? string.Join(", ", audiobook.Authors) : metadata.Artist,
                                        Series = audiobook.Series,
                                        // Prefer audiobook's publish year when available
                                        Year = int.TryParse(audiobook.PublishYear, out var py) ? py : (int?)null,
                                        // Series position / number
                                        SeriesPosition = !string.IsNullOrWhiteSpace(audiobook.SeriesNumber) && decimal.TryParse(audiobook.SeriesNumber, out var sp) ? sp : (decimal?)null,
                                        // Quality string from audiobook record
                                        // Map into Bitrate/Format heuristically if useful; for now store textual quality
                                        // We'll put it into AdditionalData so FileNamingService can use Format/Bitrate/Quality
                                        AdditionalData = new Dictionary<string, object> { { "Quality", audiobook.Quality ?? string.Empty } }
                                    };

                                    job.AddLogEntry($"Using audiobook metadata for naming: {namingMetadata.Title} by {namingMetadata.Artist}");
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                                job.AddLogEntry($"Failed to retrieve audiobook metadata: {ex.Message}");
                            }
                        }
                    }

                    // Only extract file metadata for naming when we do NOT have audiobook naming metadata.
                    // If the download is linked to an audiobook (namingMetadata != null) we must not use
                    // file-embedded tags for naming â€” the audiobook DB entry is authoritative.
                    if (namingMetadata == null && metadataService != null)
                    {
                        try
                        {
                            // Log source state immediately before attempting the file operation for diagnostics
                            try
                            {
                                var exists = File.Exists(sourcePath);
                                var size = exists ? new FileInfo(sourcePath).Length : (long?)null;
                                var last = exists ? File.GetLastWriteTimeUtc(sourcePath).ToString("o") : "(not found)";
                                job.AddLogEntry($"Operation pre-check: sourceExists={exists}, size={(size.HasValue ? size.ToString() : "(n/a)")}, lastWriteUtc={last}");
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                                job.AddLogEntry($"Failed to collect source diagnostics: {ex.Message}");
                            }
                            var extractedMetadata = await metadataService.ExtractFileMetadataAsync(sourcePath);
                            if (extractedMetadata != null)
                            {
                                // No audiobook naming metadata - merge extracted values without overwriting
                                string FirstNonEmpty(params string?[] candidates)
                                {
                                    foreach (var c in candidates)
                                    {
                                        if (!string.IsNullOrWhiteSpace(c)) return c!;
                                    }
                                    return string.Empty;
                                }

                                metadata.Title = FirstNonEmpty(metadata.Title, extractedMetadata.Title, "Unknown Title");
                                metadata.Artist = FirstNonEmpty(metadata.Artist, extractedMetadata.Artist, extractedMetadata.AlbumArtist, metadata.Artist);
                                metadata.Album = FirstNonEmpty(metadata.Album, extractedMetadata.Album, metadata.Album);

                                if (!metadata.SeriesPosition.HasValue && extractedMetadata.SeriesPosition.HasValue)
                                    metadata.SeriesPosition = extractedMetadata.SeriesPosition;
                                if (!metadata.TrackNumber.HasValue && extractedMetadata.TrackNumber.HasValue)
                                    metadata.TrackNumber = extractedMetadata.TrackNumber;
                                if (!metadata.DiscNumber.HasValue && extractedMetadata.DiscNumber.HasValue)
                                    metadata.DiscNumber = extractedMetadata.DiscNumber;
                                if (!metadata.Year.HasValue && extractedMetadata.Year.HasValue)
                                    metadata.Year = extractedMetadata.Year;
                                if (!metadata.Bitrate.HasValue && extractedMetadata.Bitrate.HasValue)
                                    metadata.Bitrate = extractedMetadata.Bitrate;
                                if (string.IsNullOrWhiteSpace(metadata.Format) && !string.IsNullOrWhiteSpace(extractedMetadata.Format))
                                    metadata.Format = extractedMetadata.Format;

                                job.AddLogEntry($"Merged extracted metadata: {metadata.Title} by {metadata.Artist}");
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                            job.AddLogEntry($"Failed to extract metadata: {ex.Message}");
                        }
                    }

                    // Generate path using naming pattern
                    // Use namingMetadata if present (authoritative audiobook fields), otherwise use metadata
                    var metadataForNaming = namingMetadata ?? metadata;

                    // Log naming variables for diagnostics
                    try
                    {
                        var dbgVars = $"Author={(metadataForNaming.Artist ?? "(null)")}, Series={(metadataForNaming.Series ?? "(null)")}, Title={(metadataForNaming.Title ?? "(null)")}";
                        job.AddLogEntry($"Resolved naming metadata: {dbgVars}");
                    }
                    catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException) { 
                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                    }

                    // Record the resolved naming metadata on the job for diagnostics
                    try
                    {
                        job.AddLogEntry($"Resolved naming metadata: Author='{metadataForNaming.Artist}', AlbumArtist='{metadataForNaming.AlbumArtist}', Series='{metadataForNaming.Series}', Title='{metadataForNaming.Title}', Year='{metadataForNaming.Year}'");
                    }
                    catch (Exception caughtEx_2) when (caughtEx_2 is not OperationCanceledException && caughtEx_2 is not OutOfMemoryException && caughtEx_2 is not StackOverflowException) {
                        // ignore logging errors
                                            System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                    }
                    // For processing jobs, compute the appropriate destination directory first.
                    // If the download is linked to an audiobook and the audiobook has a BasePath,
                    // prefer that as the base directory (and use filename-only pattern in those
                    // cases). Otherwise use the configured OutputPath. We will place the file into
                    // the destination directory using the original filename first, then later
                    // ProcessCompletedDownloadAsync will apply the full naming pattern (including
                    // creating subfolders when allowed).
                    var ext = Path.GetExtension(sourcePath);
                    var basePathForFile = settings.OutputPath ?? string.Empty;
                    var filenamePattern = settings.FileNamingPattern ?? string.Empty;

                    // If the download links to an audiobook and we've built an audiobook naming
                    // metadata above, prefer the audiobook BasePath and switch to a filename-only
                    // pattern so we don't create arbitrary folders inside an audiobook base path.
                    try
                    {
                        if (download != null && download.AudiobookId != null)
                        {
                            var audiobook = await dbContext.Audiobooks.FindAsync(download.AudiobookId);
                            if (audiobook != null && !string.IsNullOrWhiteSpace(audiobook.BasePath))
                            {
                                basePathForFile = audiobook.BasePath;

                                // If a global pattern exists, use only the filename portion when an
                                // audiobook BasePath is present; this avoids creating unintended
                                // subfolders under the audiobook base path.
                                // Use the configured filename pattern in full when computing the
                                // tentative generated path relative to the audiobook BasePath.
                                filenamePattern = settings.FileNamingPattern;
                                if (string.IsNullOrWhiteSpace(filenamePattern)) filenamePattern = "{Author}/{Series}/{Title}";
                            }
                        }
                    }
                    catch (Exception caughtEx_3) when (caughtEx_3 is not OperationCanceledException && caughtEx_3 is not OutOfMemoryException && caughtEx_3 is not StackOverflowException) { 
                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                    }

                    // Now generate a tentative path using the filename-only or relative pattern
                    // so we can compute the destination directory. We'll not actually apply the
                    // full pattern on the source; instead we will place the file into destDir
                    // using original filename first.
                    string generatedPath;
                    if (fileNamingService != null && settings.EnableMetadataProcessing)
                    {
                        // Generate a full path relative to the chosen basePathForFile (may include subfolders)
                        generatedPath = await fileNamingService.GenerateFilePathAsync(metadataForNaming, basePathForFile, null, null, ext);
                    }
                    else
                    {
                        generatedPath = Path.GetFileName(sourcePath);
                    }

                    // Preserve subdirectories from the generated path. The naming pattern may include
                    // subfolders (e.g. {Author}/{Series}/...). If the generatedPath is rooted, use it
                    // directly. If it's relative, combine it with the configured OutputPath so subfolders
                    // are retained instead of being stripped to a single filename.
                    _logger.LogDebug("GeneratedPath from FileNamingService: {GeneratedPath} (rooted={IsRooted})", generatedPath, Path.IsPathRooted(generatedPath));

                    // Only allow subfolders if the naming pattern includes DiskNumber or ChapterNumber
                    var fullPattern = settings.FileNamingPattern ?? string.Empty;
                    var patternAllowsSubfolders = fullPattern.IndexOf("DiskNumber", StringComparison.OrdinalIgnoreCase) >= 0
                        || fullPattern.IndexOf("ChapterNumber", StringComparison.OrdinalIgnoreCase) >= 0;

                    // Compute the destinationPath so we know where to place the file initially.
                    if (Path.IsPathRooted(generatedPath))
                    {
                        destinationPath = generatedPath;
                    }
                    else
                    {
                        var outputRoot = basePathForFile ?? string.Empty;

                        if (!patternAllowsSubfolders)
                        {
                            // Force filename-only: take only the filename portion of generatedPath and sanitize it
                            var forcedFilename = Path.GetFileName(generatedPath) ?? Path.GetFileName(sourcePath);
                            try
                            {
                                var invalid = Path.GetInvalidFileNameChars();
                                var sb = new System.Text.StringBuilder();
                                foreach (var c in forcedFilename)
                                {
                                    sb.Append(invalid.Contains(c) ? '_' : c);
                                }
                                forcedFilename = sb.ToString();
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                                job.AddLogEntry($"Failed to sanitize forced filename: {ex.Message}");
                            }

                            var relativeForcedFilename = forcedFilename.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                            if (string.IsNullOrWhiteSpace(outputRoot))
                            {
                                destinationPath = relativeForcedFilename;
                            }
                            else
                            {
                                var normalizedOutputRoot = outputRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                                destinationPath = normalizedOutputRoot + Path.DirectorySeparatorChar + relativeForcedFilename;
                            }
                            job.AddLogEntry($"Pattern does not allow subfolders. Forced filename-only destination: {destinationPath}");
                        }
                        else
                        {
                            var relativeGeneratedPath = generatedPath.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                            if (string.IsNullOrWhiteSpace(outputRoot))
                            {
                                destinationPath = relativeGeneratedPath;
                            }
                            else
                            {
                                var normalizedOutputRoot = outputRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                                destinationPath = normalizedOutputRoot + Path.DirectorySeparatorChar + relativeGeneratedPath;
                            }
                        }
                    }

                    job.AddLogEntry($"Initial destination inside output root: {destinationPath}");
                    try
                    {
                        var destDirForCheck = Path.GetDirectoryName(destinationPath) ?? string.Empty;
                        var exists = !string.IsNullOrEmpty(destDirForCheck) && Directory.Exists(destDirForCheck);
                        var root = string.Empty;
                        try { root = Path.GetPathRoot(destDirForCheck) ?? string.Empty; } catch (Exception caughtEx_4) when (caughtEx_4 is not OperationCanceledException && caughtEx_4 is not OutOfMemoryException && caughtEx_4 is not StackOverflowException) { root = string.Empty; }
                        job.AddLogEntry($"Destination dir exists: {exists} PathRoot={root}");

                        if (!string.IsNullOrEmpty(root) && string.Equals(root.TrimEnd(Path.DirectorySeparatorChar), destDirForCheck.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                        {
                            job.AddLogEntry($"Warning: destination dir is a root path: {destDirForCheck}");
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                        job.AddLogEntry($"Failed to inspect destination directory: {ex.Message}");
                    }
                }
                else
                {
                    // Simple naming - use original filename in output directory
                    var fileName = Path.GetFileName(sourcePath);
                    destinationPath = Path.Combine(settings.OutputPath, fileName);
                    job.AddLogEntry($"Using simple destination: {destinationPath}");
                }

                // Determine destination directory but DO NOT create it during import/processing
                var destDir = Path.GetDirectoryName(destinationPath);

                // Only perform file operations if the destination directory already exists.
                if (!string.IsNullOrEmpty(destDir) && Directory.Exists(destDir))
                {
                    // Check if already moved (use download loaded from dbContext above)
                    using var scope = _serviceScopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<ListenArrDbContext>();
                    var download = await dbContext.Downloads.FindAsync(job.DownloadId);
                    if (download != null && download.Status == DownloadStatus.Moved)
                    {
                        job.AddLogEntry("File already moved by DownloadService. Skipping background move.");
                        job.DestinationPath = destinationPath;
                        return;
                    }
                    if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase))
                    {
                        var action = settings.CompletedFileAction ?? "Move";
                        job.AddLogEntry($"Performing {action} operation");

                        // Capture source size before operation for later verification (move will remove source)
                        long? sourceSize = null;
                        try
                        {
                            if (File.Exists(sourcePath))
                            {
                                sourceSize = new FileInfo(sourcePath).Length;
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                            job.AddLogEntry($"Failed to read source file size: {ex.Message}");
                        }

                        try
                        {
                            // Ensure unique destination to avoid overwriting
                            _logger.LogDebug("Resolving unique destination for background job: {Dest}", destinationPath);
                            var uniqueDest = FileUtils.GetUniqueDestinationPath(destinationPath);
                            var fileMover = scope.ServiceProvider.GetService<IFileMover>();
                            if (string.Equals(action, "Copy", StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    if (fileMover != null)
                                    {
                                        var ok = await fileMover.CopyFileAsync(sourcePath, uniqueDest);
                                        if (ok) job.AddLogEntry($"Copied file: {sourcePath} -> {uniqueDest}");
                                        else throw new IOException("CopyFileAsync failed");
                                    }
                                    else
                                    {
                                        File.Copy(sourcePath, uniqueDest, true);
                                        job.AddLogEntry($"Copied file: {sourcePath} -> {uniqueDest}");
                                    }
                                }
                                catch (FileNotFoundException fnf)
                                {
                                    job.AddLogEntry($"Copy failed - source not found: {fnf.Message}");
                                    _metrics?.Increment("processing.copy_source_not_found");
                                    job.ScheduleRetry();
                                    job.ErrorMessage = fnf.Message;
                                    return;
                                }
                                catch (UnauthorizedAccessException uae)
                                {
                                    job.AddLogEntry($"Copy failed - unauthorized access: {uae.Message}");
                                    try
                                    {
                                        var diagDestDir = Path.GetDirectoryName(uniqueDest) ?? string.Empty;
                                        job.AddLogEntry($"Copy destination dir exists={Directory.Exists(diagDestDir)} PathRoot={(string.IsNullOrEmpty(diagDestDir) ? "(n/a)" : Path.GetPathRoot(diagDestDir) ?? "(no-root)")}");
                                    }
                                    catch (Exception caughtEx_5) when (caughtEx_5 is not OperationCanceledException && caughtEx_5 is not OutOfMemoryException && caughtEx_5 is not StackOverflowException) { 
                                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                    }
                                    try { _metrics?.Increment("processing.move_unauthorized"); } catch (Exception caughtEx_6) when (caughtEx_6 is not OperationCanceledException && caughtEx_6 is not OutOfMemoryException && caughtEx_6 is not StackOverflowException) { 
                                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                    }
                                    try
                                    {
                                        job.AddLogEntry($"Process identity: {Environment.UserDomainName}\\{Environment.UserName}");
                                    }
                                    catch (Exception caughtEx_7) when (caughtEx_7 is not OperationCanceledException && caughtEx_7 is not OutOfMemoryException && caughtEx_7 is not StackOverflowException) { 
                                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                    }
                                    job.ErrorMessage = uae.Message;
                                    job.ScheduleRetry();
                                    return;
                                }
                                catch (IOException ioex)
                                {
                                    var msg = ioex.Message ?? string.Empty;
                                    if (msg.IndexOf("being used by another process", StringComparison.OrdinalIgnoreCase) >= 0 || ioex.HResult == unchecked((int)0x80070020))
                                    {
                                        job.AddLogEntry($"Copy failed due to sharing violation (file locked): {ioex.Message}");
                                        try { _metrics?.Increment("processing.move_file_locked"); } catch (Exception caughtEx_8) when (caughtEx_8 is not OperationCanceledException && caughtEx_8 is not OutOfMemoryException && caughtEx_8 is not StackOverflowException) { 
                                            System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                        }
                                        job.ErrorMessage = ioex.Message;
                                        job.ScheduleRetry();
                                        return;
                                    }
                                    throw;
                                }
                            }
                            else if (string.Equals(action, "Hardlink/Copy", StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    if (fileMover != null)
                                    {
                                        var ok = await fileMover.HardlinkFileAsync(sourcePath, uniqueDest);
                                        if (ok) job.AddLogEntry($"Hardlinked file: {sourcePath} -> {uniqueDest}");
                                        else throw new IOException("HardlinkFileAsync failed");
                                    }
                                    else
                                    {
                                        // Fallback without IFileMover
                                        try
                                        {
                                            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                                            {
                                                if (!NativeFileMethods.CreateHardLinkWindows(uniqueDest, sourcePath))
                                                    throw new IOException("Hardlink failed");
                                            }
                                            else
                                            {
                                                if (NativeFileMethods.CreateHardLinkUnix(sourcePath, uniqueDest) != 0)
                                                    throw new IOException("Hardlink failed");
                                            }
                                            job.AddLogEntry($"Hardlinked file: {sourcePath} -> {uniqueDest}");
                                        }
                                        catch (Exception caughtEx_9) when (caughtEx_9 is not OperationCanceledException && caughtEx_9 is not OutOfMemoryException && caughtEx_9 is not StackOverflowException) {
                                            File.Copy(sourcePath, uniqueDest, true);
                                            job.AddLogEntry($"Hardlink failed, copied file: {sourcePath} -> {uniqueDest}");
                                        }
                                    }
                                }
                                catch (FileNotFoundException fnf)
                                {
                                    job.AddLogEntry($"Hardlink failed - source not found: {fnf.Message}");
                                    _metrics?.Increment("processing.copy_source_not_found");
                                    job.ScheduleRetry();
                                    job.ErrorMessage = fnf.Message;
                                    return;
                                }
                                catch (UnauthorizedAccessException uae)
                                {
                                    job.AddLogEntry($"Hardlink failed - unauthorized access: {uae.Message}");
                                    job.ErrorMessage = uae.Message;
                                    job.ScheduleRetry();
                                    return;
                                }
                                catch (IOException ioex)
                                {
                                    var msg = ioex.Message ?? string.Empty;
                                    if (msg.IndexOf("being used by another process", StringComparison.OrdinalIgnoreCase) >= 0 || ioex.HResult == unchecked((int)0x80070020))
                                    {
                                        job.AddLogEntry($"Hardlink failed due to sharing violation (file locked): {ioex.Message}");
                                        try { _metrics?.Increment("processing.move_file_locked"); } catch (Exception caughtEx_10) when (caughtEx_10 is not OperationCanceledException && caughtEx_10 is not OutOfMemoryException && caughtEx_10 is not StackOverflowException) { 
                                            System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                        }
                                        job.ErrorMessage = ioex.Message;
                                        job.ScheduleRetry();
                                        return;
                                    }
                                    throw;
                                }
                            }
                            else
                            {
                                // Default to Move
                                try
                                {
                                    if (fileMover != null)
                                    {
                                        var ok = await fileMover.MoveFileAsync(sourcePath, uniqueDest);
                                        if (ok) job.AddLogEntry($"Moved file: {sourcePath} -> {uniqueDest}");
                                        else throw new IOException("MoveFileAsync failed");
                                    }
                                    else
                                    {
                                        File.Move(sourcePath, uniqueDest, true);
                                        job.AddLogEntry($"Moved file: {sourcePath} -> {uniqueDest}");
                                    }
                                }
                                catch (FileNotFoundException fnf)
                                {
                                    // File disappeared between the earlier checks and the move. Treat as transient and retry.
                                    job.AddLogEntry($"Move failed - source not found: {fnf.Message}");
                                    _metrics?.Increment("processing.move_source_not_found");
                                    job.ScheduleRetry();
                                    job.ErrorMessage = fnf.Message;
                                    return;
                                }
                                catch (UnauthorizedAccessException uae)
                                {
                                    job.AddLogEntry($"Move failed - unauthorized access: {uae.Message}");
                                    try
                                    {
                                        var diagDestDir = Path.GetDirectoryName(uniqueDest) ?? string.Empty;
                                        job.AddLogEntry($"Move destination dir exists={Directory.Exists(diagDestDir)} PathRoot={(string.IsNullOrEmpty(diagDestDir) ? "(n/a)" : Path.GetPathRoot(diagDestDir) ?? "(no-root)")}");
                                    }
                                    catch (Exception caughtEx_11) when (caughtEx_11 is not OperationCanceledException && caughtEx_11 is not OutOfMemoryException && caughtEx_11 is not StackOverflowException) { 
                                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                    }
                                    try { _metrics?.Increment("processing.move_unauthorized"); } catch (Exception caughtEx_12) when (caughtEx_12 is not OperationCanceledException && caughtEx_12 is not OutOfMemoryException && caughtEx_12 is not StackOverflowException) { 
                                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                    }
                                    try
                                    {
                                        job.AddLogEntry($"Process identity: {Environment.UserDomainName}\\{Environment.UserName}");
                                    }
                                    catch (Exception caughtEx_13) when (caughtEx_13 is not OperationCanceledException && caughtEx_13 is not OutOfMemoryException && caughtEx_13 is not StackOverflowException) { 
                                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                    }
                                    job.ErrorMessage = uae.Message;
                                    job.ScheduleRetry();
                                    return;
                                }
                                catch (IOException ioex)
                                {
                                    var msg = ioex.Message ?? string.Empty;
                                    if (msg.IndexOf("being used by another process", StringComparison.OrdinalIgnoreCase) >= 0 || ioex.HResult == unchecked((int)0x80070020))
                                    {
                                        job.AddLogEntry($"Move failed due to sharing violation (file locked): {ioex.Message}");
                                        try { _metrics?.Increment("processing.move_file_locked"); } catch (Exception caughtEx_14) when (caughtEx_14 is not OperationCanceledException && caughtEx_14 is not OutOfMemoryException && caughtEx_14 is not StackOverflowException) { 
                                            System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                        }
                                        job.ErrorMessage = ioex.Message;
                                        job.ScheduleRetry();
                                        return;
                                    }
                                    throw;
                                }
                            }


                            destinationPath = uniqueDest;

                            // Verification: ensure destination exists and (if sourceSize available) sizes match
                            if (!File.Exists(destinationPath))
                            {
                                job.AddLogEntry($"Destination not found after {action}: {destinationPath}");
                                job.ErrorMessage = $"Destination not found after {action}";
                                throw new IOException($"Destination not found after {action}: {destinationPath}");
                            }

                            if (sourceSize.HasValue)
                            {
                                try
                                {
                                    var destSize = new FileInfo(destinationPath).Length;
                                    if (destSize != sourceSize.Value)
                                    {
                                        job.AddLogEntry($"Destination size ({destSize}) does not match source size ({sourceSize.Value})");
                                        job.ErrorMessage = $"Destination size mismatch: {destSize} != {sourceSize.Value}";
                                        throw new IOException("Destination size mismatch after file operation");
                                    }
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                                    // If verifying size fails for any reason, record and surface the error
                                    job.AddLogEntry($"Failed to verify destination size: {ex.Message}");
                                    job.ErrorMessage = ex.Message;
                                    throw;
                                }
                            }

                            job.AddLogEntry($"Verified destination: {destinationPath} (size: {new FileInfo(destinationPath).Length})");
                            job.DestinationPath = destinationPath;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                            // Ensure the error is recorded on the job so it surfaces in the queue stats/logs
                            job.AddLogEntry($"File operation failed: {ex.Message}");
                            job.ErrorMessage = ex.Message;
                            throw;
                        }
                    }
                    else
                    {
                        job.AddLogEntry("Source and destination are the same, no file operation needed");
                        job.DestinationPath = sourcePath;
                    }
                }
                else
                {
                    // Do not create directories during processing/import. If destination directory doesn't exist,
                    // leave the file in place and log a warning.
                    job.AddLogEntry($"Destination directory does not exist: {destDir}. Skipping file move/copy and keeping source: {sourcePath}");
                    job.ErrorMessage = $"Destination directory does not exist: {destDir}";
                    _metrics?.Increment("processing.dest_dir_missing");
                    job.DestinationPath = sourcePath;
                }
            }
            else
            {
                job.AddLogEntry("No output path configured, keeping file at original location");
                job.DestinationPath = sourcePath;
            }

            // Update the download record with the final path
            await downloadService.ProcessCompletedDownloadAsync(job.DownloadId, job.DestinationPath);
            job.AddLogEntry($"Updated download record with final path: {job.DestinationPath}");

            // If the download was linked to an Audiobook, enqueue a scan for that audiobook
            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetService<ListenArrDbContext>();
                var scanQueue = scope.ServiceProvider.GetService<IScanQueueService>();

                if (scanQueue != null && dbContext != null)
                {
                    var dl = await dbContext.Downloads.FindAsync(job.DownloadId);
                    if (dl != null && dl.AudiobookId != null)
                    {
                        try
                        {
                            // Enqueue a scan for the audiobook using the destination path so the scanner
                            // can focus on the moved/copied file location if appropriate.
                            var jobId = await scanQueue.EnqueueScanAsync(dl.AudiobookId.Value, job.DestinationPath);
                            job.AddLogEntry($"Enqueued scan job {jobId} for audiobook {dl.AudiobookId} path={job.DestinationPath}");
                            _logger.LogInformation("Enqueued scan job {JobId} for audiobook {AudiobookId} after processing download {DownloadId}", jobId, dl.AudiobookId, job.DownloadId);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                            job.AddLogEntry($"Failed to enqueue scan job: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                job.AddLogEntry($"Failed to attempt enqueueing scan job: {ex.Message}");
            }
        }
    }
}

