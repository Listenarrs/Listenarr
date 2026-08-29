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
using Listenarr.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Scanning
{
    public class UnmatchedScanBackgroundService(
        IUnmatchedScanQueueService queue,
        IUnmatchedScanProcessor processor,
        ILibraryFilesystemReadiness filesystemReadiness,
        ILogger<UnmatchedScanBackgroundService> logger,
        IHubContext<SettingsHub> hubContext,
        IAppMetricsService metrics) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation("UnmatchedScanBackgroundService waiting for library filesystem initialization");
            await filesystemReadiness.WaitUntilReadyAsync(stoppingToken);
            logger.LogInformation("UnmatchedScanBackgroundService started");
            try
            {
                await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
                {
                    try
                    {
                        metrics.Increment("worker.unmatchedscanbackgroundservice.job.started");
                        await processor.ProcessJobAsync(job, stoppingToken);
                        metrics.Increment("worker.unmatchedscanbackgroundservice.job.completed");
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        metrics.Increment("worker.unmatchedscanbackgroundservice.job.skipped");
                        throw;
                    }
                    catch (OperationCanceledException ex)
                    {
                        await HandleJobFailureAsync(job.Id, ex, stoppingToken);
                    }
                    catch (Exception ex) when (WorkerExceptionClassifier.IsNonFatal(ex))
                    {
                        await HandleJobFailureAsync(job.Id, ex, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("UnmatchedScanBackgroundService stopping due to host shutdown");
            }
        }

        private async Task HandleJobFailureAsync(Guid jobId, Exception ex, CancellationToken stoppingToken)
        {
            if (TryGetTerminalJobStatus(jobId, out var terminalStatus)
                && string.Equals(terminalStatus, "Completed", StringComparison.Ordinal))
            {
                // The processor commits results before publishing its SignalR notification.
                // A post-completion notification failure must never downgrade a successful scan.
                metrics.Increment("worker.unmatchedscanbackgroundservice.job.completed");
                logger.LogWarning(
                    ex,
                    "Unmatched scan job {JobId} completed, but a post-completion side effect failed",
                    jobId);
                return;
            }

            metrics.Increment("worker.unmatchedscanbackgroundservice.job.failed");
            logger.LogError(ex, "Unmatched scan job {JobId} failed", jobId);
            if (!string.Equals(terminalStatus, "Failed", StringComparison.Ordinal))
            {
                try
                {
                    queue.UpdateJob(jobId, "Failed", error: ex.Message);
                }
                catch (OperationCanceledException statusException) when (
                    !stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(
                        statusException,
                        "Unmatched scan job {JobId} failed, but its failed status update was canceled internally",
                        jobId);
                }
                catch (Exception statusException) when (
                    WorkerExceptionClassifier.IsNonFatal(statusException))
                {
                    logger.LogWarning(
                        statusException,
                        "Unmatched scan job {JobId} failed, but its failed status could not be recorded",
                        jobId);
                }
            }

            try
            {
                await hubContext.Clients.All.SendAsync(
                    "UnmatchedScanComplete",
                    new
                    {
                        jobId = jobId.ToString(),
                        count = 0,
                        error = UnmatchedScanPublicError.FromInternal(ex.Message)
                    },
                    stoppingToken);
            }
            catch (OperationCanceledException notificationException) when (
                !stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    notificationException,
                    "Unmatched scan job {JobId} failed, but its completion notification was canceled internally",
                    jobId);
            }
            catch (Exception notificationException) when (
                WorkerExceptionClassifier.IsNonFatal(notificationException))
            {
                logger.LogWarning(
                    notificationException,
                    "Unmatched scan job {JobId} failed, but its completion notification could not be published",
                    jobId);
            }
        }

        private bool TryGetTerminalJobStatus(Guid jobId, out string? terminalStatus)
        {
            terminalStatus = null;
            try
            {
                if (!queue.TryGetJob(jobId, out var current)
                    || current?.Status is not ("Completed" or "Failed"))
                {
                    return false;
                }

                terminalStatus = current.Status;
                return true;
            }
            catch (Exception statusException) when (
                WorkerExceptionClassifier.IsNonFatal(statusException))
            {
                logger.LogWarning(
                    statusException,
                    "Could not inspect terminal state for unmatched scan job {JobId}",
                    jobId);
                return false;
            }
        }

        internal static List<List<string>> BuildGroupedFilesForFolder(
            IEnumerable<string> files,
            string folderPath,
            FileSystemPathSemantics semantics,
            IReadOnlyDictionary<string, PathParsedMetadata>? embeddedTagsByFile = null) =>
            UnmatchedScanProcessor.BuildGroupedFilesForFolder(
                files,
                folderPath,
                semantics,
                embeddedTagsByFile);
    }

    public partial class UnmatchedScanProcessor : IUnmatchedScanProcessor
    {
        private static readonly string[] AudioExtensions = { ".m4b", ".mp3", ".flac", ".ogg", ".opus", ".m4a", ".aac", ".wav" };
        private sealed record StemGroup(string Stem, List<string> Files);
        private sealed record GroupCandidate(string FilePath, string Stem, bool IsAncillary, string TitleKey, string AuthorKey);

        private readonly IUnmatchedScanQueueService _queue;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<UnmatchedScanProcessor> _logger;
        private readonly IHubContext<SettingsHub> _hubContext;
        private readonly IFfmpegService _ffmpegService;
        private readonly IFileSystemSemanticsResolver _semanticsResolver;

        public UnmatchedScanProcessor(
            IUnmatchedScanQueueService queue,
            IServiceScopeFactory scopeFactory,
            ILogger<UnmatchedScanProcessor> logger,
            IHubContext<SettingsHub> hubContext,
            IFfmpegService ffmpegService,
            IFileSystemSemanticsResolver semanticsResolver)
        {
            _queue = queue;
            _scopeFactory = scopeFactory;
            _logger = logger;
            _hubContext = hubContext;
            _ffmpegService = ffmpegService;
            _semanticsResolver = semanticsResolver;
        }

        public async Task ProcessJobAsync(UnmatchedScanJob job, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Processing unmatched scan job {JobId} for {Path}", job.Id, job.RootFolderPath);
            _queue.UpdateJob(job.Id, "Processing");

            var results = await ScanAsync(job.RootFolderPath, cancellationToken);

            _queue.UpdateJob(job.Id, "Completed", results);
            _logger.LogInformation("Unmatched scan job {JobId} completed: {Count} unmatched items", job.Id, results.Count);

            await _hubContext.Clients.All.SendAsync(
                "UnmatchedScanComplete",
                new { jobId = job.Id.ToString(), count = results.Count },
                cancellationToken);
        }

        private async Task<List<UnmatchedFileResult>> ScanAsync(string rootFolderPath, CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var fileRepository = scope.ServiceProvider.GetRequiredService<IAudiobookFileRepository>();
            var audiobookRepository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            var configService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
            var scanAuthorizationService = scope.ServiceProvider
                .GetRequiredService<IScanPathAuthorizationService>();
            var fileSystem = scope.ServiceProvider.GetRequiredService<IFileSystem>();
            var appSettings = await configService.GetApplicationSettingsAsync();
            var concurrency = Math.Clamp(appSettings?.UnmatchedScanConcurrency ?? 2, 1, 8);
            var authorization = await scanAuthorizationService.AuthorizeAsync(
                rootFolderPath,
                ct);
            if (!authorization.IsAuthorized
                || authorization.Path == null
                || !authorization.Identity.HasValue
                || !authorization.PhysicalIdentity.HasValue)
            {
                throw new InvalidOperationException(
                    authorization.Error
                        ?? "The unmatched scan root could not be authorized safely.");
            }

            var canonicalRootFolderPath = authorization.Path;
            var semantics = authorization.Identity.Value.Semantics;
            var hasDurableGenerationProof =
                authorization.PhysicalIdentity.Value.HasDurableGenerationProof;

            // Load all tracked file paths (normalized) from DB.
            // Check BOTH AudiobookFiles (multi-file imports) AND Audiobook.FilePath (single-file imports)
            // so that files already in the library are not reported as unmatched.
            var trackedFromFiles = await fileRepository.GetAllFilePathsAsync(semantics, ct);

            var allAudiobooks = await audiobookRepository.GetAllAsync();
            var trackedFromAudiobooks = allAudiobooks
                .Where(a => a.FilePath != null)
                .Select(a => a.FilePath!)
                .ToList();

            var trackedNormalized = new HashSet<string>(
                trackedFromFiles.Concat(trackedFromAudiobooks)
                    .Select(path => NormalizePath(path, semantics.Syntax)),
                semantics.Comparer);

            // Walk the root folder tree through the same pinned/generation-aware
            // enumeration primitive used by authoritative audiobook scans.
            using var pinnedRoot = PinnedDirectoryCreation.OpenPinnedBoundary(
                canonicalRootFolderPath);
            if (!pinnedRoot.VisiblePathMatches()
                || (authorization.PhysicalIdentity.Value.HasDurableGenerationProof
                    && !pinnedRoot.MatchesDirectoryObjectIdentity(
                        authorization.PhysicalIdentity.Value.ScanRootObjectIdentity!)))
            {
                throw new InvalidOperationException(
                    "The unmatched scan root changed after authorization.");
            }
            var enumeration = ScanFileDiscovery.CollectCandidates(
                fileSystem,
                canonicalRootFolderPath,
                jobId: Guid.Empty,
                _logger,
                semantics,
                pinnedRoot,
                authorization.PhysicalIdentity.Value.HasDurableGenerationProof);
            if (enumeration.Issues.Any(issue => issue.Kind is
                    ScanDiscoveryIssueKind.DirectoryGenerationChanged
                    or ScanDiscoveryIssueKind.EnumerationFailure))
            {
                throw new InvalidOperationException(
                    "The unmatched scan root changed or became unavailable during enumeration.");
            }
            var candidates = enumeration.Candidates.ToList();

            // Filter to untracked files
            var unmatched = candidates
                .Where(f => !trackedNormalized.Contains(NormalizePath(f, semantics.Syntax)))
                .ToList();

            // Two-level grouping:
            // 1. Group by parent directory (folder = one audiobook in the common case).
            // 2. Within each directory that has multiple files, sub-group by a normalized
            //    title stem extracted from the filename. Files that share the same stem
            //    are parts of the same audiobook. Distinct stems remain separate entries,
            //    except for ancillary tracks like "Foreword" or "Introduction", which stay
            //    attached when there is only one primary book group in the folder.
            var folderGroups = unmatched
                .GroupBy(
                    f => Path.GetFullPath(Path.GetDirectoryName(f) ?? canonicalRootFolderPath),
                    semantics.Comparer)
                .ToList();

            // Resolve ffprobe path once for the whole scan (null = not available)
            var ffprobePath = hasDurableGenerationProof
                && (OperatingSystem.IsWindows()
                    || OperatingSystem.IsLinux()
                    || OperatingSystem.IsMacOS())
                    ? await _ffmpegService.GetFfprobePathAsync()
                    : null;

            var results = new System.Collections.Concurrent.ConcurrentBag<UnmatchedFileResult>();

            // Parallel.ForEachAsync only allocates active slots and avoids creating all tasks up front.
            await Parallel.ForEachAsync(folderGroups,
                new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = ct },
                async (folderGroup, token) =>
                {
                    var bookFolder = folderGroup.Key;
                    var folderFiles = folderGroup.ToList();
                    IReadOnlyDictionary<string, PathParsedMetadata>? embeddedTagsByFile = null;

                    var groupedFiles = BuildGroupedFilesForFolder(folderFiles, bookFolder, semantics);
                    if (groupedFiles.Count > 1 && !string.IsNullOrEmpty(ffprobePath))
                    {
                        embeddedTagsByFile = await ReadEmbeddedTagsForFilesAsync(
                            folderFiles,
                            ffprobePath,
                            semantics,
                            enumeration.FileObjectIdentities,
                            token);
                        groupedFiles = BuildGroupedFilesForFolder(
                            folderFiles,
                            bookFolder,
                            semantics,
                            embeddedTagsByFile);
                    }

                    foreach (var files in groupedFiles)
                    {
                        var plans = MultiFileImportPlanner.BuildPlans(
                            files.Select(f => (FullPath: f, RelativePath: (string?)Path.GetRelativePath(bookFolder, f))),
                            semantics.Comparer);
                        var orderedFiles = plans.Select(p => p.FullPath).ToList();
                        var representative = orderedFiles.First();
                        var parsed = PathMetadataParser.ParsePathOnly(
                            representative,
                            rootFolderPath,
                            semantics);
                        if (hasDurableGenerationProof)
                        {
                            await ApplyPinnedFolderMetadataAsync(
                                parsed,
                                parsed.BookFolderPath ?? string.Empty,
                                enumeration,
                                semantics,
                                token);
                        }

                        PathParsedMetadata? tags = null;
                        if (embeddedTagsByFile != null && embeddedTagsByFile.TryGetValue(representative, out var cachedTags))
                        {
                            tags = cachedTags;
                        }
                        else if (!string.IsNullOrEmpty(ffprobePath))
                        {
                            var canonicalRepresentative = FileSystemPathIdentity.Canonicalize(
                                representative,
                                semantics.Syntax);
                            if (!enumeration.FileObjectIdentities.TryGetValue(
                                    canonicalRepresentative,
                                    out var expectedPhysicalObjectIdentity))
                            {
                                throw new InvalidOperationException(
                                    "The unmatched metadata candidate lacks its enumerated physical generation.");
                            }

                            using var lease = PinnedAudiobookFileRegistrationLease.Open(
                                representative,
                                expectedPhysicalObjectIdentity);
                            tags = await PathMetadataParser.ReadEmbeddedTagsAsync(
                                lease.MetadataPath,
                                ffprobePath,
                                token);
                            if (!lease.MatchesCurrentPublication())
                            {
                                throw new InvalidOperationException(
                                    "The unmatched metadata candidate changed during embedded-tag extraction.");
                            }
                        }

                        if (tags != null)
                        {
                            ApplyEmbeddedTags(parsed, tags);
                        }

                        var relativeFolder = bookFolder.Length > rootFolderPath.Length
                            ? bookFolder[(rootFolderPath.Length)..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            : bookFolder;

                        var totalSize = files.Sum(file =>
                            enumeration.FileLengths.TryGetValue(file, out var length)
                                ? length
                                : 0L);

                        results.Add(new UnmatchedFileResult
                        {
                            FullPath = representative,
                            SourceFiles = orderedFiles,
                            RelativePath = relativeFolder,
                            BookFolder = bookFolder,
                            Size = totalSize,
                            FileCount = orderedFiles.Count,
                            Title = parsed.Title,
                            Author = parsed.Author,
                            Series = parsed.Series,
                            SeriesNumber = parsed.SeriesNumber,
                            Year = parsed.Year,
                            Narrator = parsed.Narrator,
                            Description = parsed.Description,
                            CoverPath = parsed.CoverPath,
                            Asin = parsed.Asin,
                            Format = Path.GetExtension(representative).TrimStart('.').ToUpperInvariant()
                        });
                    }
                });

            return results.OrderBy(r => r.Author).ThenBy(r => r.Series).ThenBy(r => r.Title).ToList();
        }

    }
}
