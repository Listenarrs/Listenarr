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
using AsyncKeyedLock;
using Listenarr.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Metadata.Jobs
{
    // Background hosted service to rescan files missing metadata and populate DB fields
    public class MetadataRescanService(
        ILogger<MetadataRescanService> logger,
        IMetadataRescanProcessor processor,
        IWorkerCycleRunner cycleRunner,
        ILibraryFilesystemReadiness filesystemReadiness) : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation("MetadataRescanService waiting for library filesystem initialization");
            await filesystemReadiness.WaitUntilReadyAsync(stoppingToken);
            logger.LogInformation("MetadataRescanService starting");
            await cycleRunner.RunPeriodicAsync(
                nameof(MetadataRescanService),
                initialDelay: null,
                intervalProvider: () => Interval,
                runCycle: processor.RunCycleAsync,
                stoppingToken);
            logger.LogInformation("MetadataRescanService stopping");
        }
    }

    public class MetadataRescanProcessor(
        IServiceScopeFactory scopeFactory,
        IAudiobookOperationCoordinator audiobookOperationCoordinator,
        IMoveQueueService moveQueueService,
        ILogger<MetadataRescanProcessor> logger,
        TimeProvider? timeProvider = null) : IMetadataRescanProcessor
    {
        private const int BatchSize = 20;
        private readonly AsyncNonKeyedLocker _sem = new(2); // bound concurrent extractions
        private readonly MetadataRescanDeferrals _deferrals = new(timeProvider ?? TimeProvider.System);

        public async Task RunCycleAsync(CancellationToken cancellationToken)
        {
            using var scope = scopeFactory.CreateScope();
            var fileRepository = scope.ServiceProvider.GetRequiredService<IAudiobookFileRepository>();
            _deferrals.PruneSettled();
            var absentFileCount = 0;
            var candidates = await fileRepository.GetMissingMetadataAsync(
                BatchSize,
                file =>
                {
                    if (_deferrals.IsDeferred(file.Id))
                    {
                        return false;
                    }
                    if (!IsKnownAbsent(file.Path))
                    {
                        return true;
                    }

                    // Excluded before it takes a slot, and deferred so later
                    // cycles do not probe it again until its retry time.
                    _deferrals.RecordAttempt(file.Id, refreshed: false);
                    absentFileCount++;
                    logger.LogDebug(
                        "Deferring metadata rescan for file id={Id}; the file is not present",
                        file.Id);
                    return false;
                },
                cancellationToken);

            if (absentFileCount > 0)
            {
                logger.LogInformation(
                    "Deferred metadata rescan for {Count} files that are not present",
                    absentFileCount);
            }
            if (candidates.Any())
            {
                logger.LogInformation("Found {Count} files missing metadata to rescan", candidates.Count);
            }

            var tasks = new List<Task>();
            foreach (var candidate in candidates.Select(f => new { f.Id, f.AudiobookId, f.Path }))
            {
                tasks.Add(Task.Run(async () =>
                {
                    using var releaser = await _sem.LockAsync(cancellationToken);
                    var refreshed = false;
                    try
                    {
                        refreshed = await RescanCandidateAsync(
                            candidate.Id,
                            candidate.AudiobookId,
                            cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        logger.LogDebug("Metadata rescan cancelled for file id={Id}", candidate.Id);
                        return;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        if (_deferrals.ClaimFailureReport(candidate.Id))
                        {
                            logger.LogWarning(ex, "Failed to rescan metadata for file id={Id} path={Path}; further failures for this file are logged at Debug", candidate.Id, LogRedaction.SanitizeFilePath(candidate.Path));
                        }
                        else
                        {
                            logger.LogDebug(ex, "Failed to rescan metadata for file id={Id} path={Path}", candidate.Id, LogRedaction.SanitizeFilePath(candidate.Path));
                        }
                    }

                    _deferrals.RecordAttempt(candidate.Id, refreshed);
                }));
            }

            await Task.WhenAll(tasks);
        }

        // Only an absolute audio path that is not there counts: relative rows are
        // resolved against their base path later, and non-audio rows are removed.
        private static bool IsKnownAbsent(string? path)
        {
            return !string.IsNullOrWhiteSpace(path)
                && Path.IsPathFullyQualified(path)
                && FileUtils.IsAudioFile(path)
                && !File.Exists(path);
        }

        private async Task<bool> RescanCandidateAsync(
            int fileId,
            int audiobookId,
            CancellationToken cancellationToken)
        {
            using var taskScope = scopeFactory.CreateScope();
            var taskFileRepository = taskScope.ServiceProvider.GetRequiredService<IAudiobookFileRepository>();

            var recovery = await moveQueueService.GetRecoveryStateForAudiobookAsync(
                audiobookId,
                cancellationToken);
            if (recovery.BlocksFilesystemMutation)
            {
                logger.LogDebug(
                    "Skipping metadata rescan for file id={Id}; audiobook {AudiobookId} has unresolved move state {Disposition}",
                    fileId,
                    audiobookId,
                    recovery.Disposition);
                return false;
            }

            var file = await taskFileRepository.GetByIdAsync(fileId, cancellationToken);
            if (file == null)
            {
                logger.LogDebug("Skipping metadata rescan for missing file id={Id}", fileId);
                return false;
            }

            var observedPath = file.Path ?? string.Empty;
            if (!FileUtils.IsAudioFile(observedPath))
            {
                await RemoveNonAudioFileAsync(
                    file.Id,
                    file.AudiobookId,
                    cancellationToken);
                return false;
            }

            var taskAudiobookRepository = taskScope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            var filePathIdentityResolver = taskScope.ServiceProvider.GetRequiredService<IAudiobookFilePathIdentityResolver>();
            var audiobook = await taskAudiobookRepository.GetForScanSnapshotAsync(
                file.AudiobookId,
                cancellationToken);
            if (audiobook == null)
            {
                logger.LogDebug(
                    "Skipping metadata rescan for file id={Id}; audiobook {AudiobookId} no longer exists",
                    file.Id,
                    file.AudiobookId);
                return false;
            }

            var resolvedIdentity = await filePathIdentityResolver.ResolveAsync(
                audiobook,
                observedPath,
                cancellationToken);
            if (resolvedIdentity.State != PathIdentityState.Valid)
            {
                logger.LogDebug(
                    "Skipping metadata rescan for file id={Id}; the stored path is unavailable on this host: {Reason}",
                    file.Id,
                    LogRedaction.SanitizeText(resolvedIdentity.Reason));
                return false;
            }

            logger.LogInformation(
                "Re-extracting metadata for file id={Id} path={Path}",
                file.Id,
                LogRedaction.SanitizeFilePath(resolvedIdentity.CanonicalPath));

            var taskFileService = taskScope.ServiceProvider
                .GetRequiredService<IAudiobookFileService>();
            cancellationToken.ThrowIfCancellationRequested();
            using var registrationLease =
                PinnedAudiobookFileRegistrationLease.OpenForMetadataRead(
                    resolvedIdentity.CanonicalPath,
                    expectedPhysicalObjectIdentity: null);
            if (!registrationLease.MatchesCurrentPublication())
            {
                logger.LogDebug(
                    "Skipped metadata rescan for file id={Id}; the stored pathname no longer identifies the pinned generation",
                    file.Id);
                return false;
            }

            var updated = await taskFileService.RefreshMetadataAsync(
                new Audiobook { Id = file.AudiobookId },
                file.Id,
                registrationLease,
                cancellationToken);
            if (updated)
            {
                logger.LogInformation(
                    "Updated metadata for file id={Id}",
                    file.Id);
            }
            return updated;
        }

        private async Task RemoveNonAudioFileAsync(
            int fileId,
            int audiobookId,
            CancellationToken cancellationToken)
        {
            await audiobookOperationCoordinator.ExecuteExclusiveAsync(
                audiobookId,
                async token =>
                {
                    await moveQueueService.EnsureFilesystemMutationAllowedAsync(
                        audiobookId,
                        token);
                    using var applyScope = scopeFactory.CreateScope();
                    var fileRepository = applyScope.ServiceProvider.GetRequiredService<IAudiobookFileRepository>();
                    var audiobookRepository = applyScope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
                    var currentFile = await fileRepository.GetByIdAsync(fileId, token);
                    if (currentFile == null
                        || currentFile.AudiobookId != audiobookId
                        || FileUtils.IsAudioFile(currentFile.Path ?? string.Empty))
                    {
                        return;
                    }

                    var audiobook = await audiobookRepository.GetByIdAsync(
                        currentFile.AudiobookId);
                    var clearLegacyPath = audiobook != null
                        && await AreSameLibraryPathAsync(
                            audiobook,
                            currentFile.Path,
                            applyScope.ServiceProvider
                                .GetRequiredService<IFileSystemSemanticsResolver>(),
                            applyScope.ServiceProvider
                                .GetRequiredService<IRootFolderService>(),
                            token);
                    if (!await fileRepository.DeletePathStateAsync(
                            currentFile.Id,
                            currentFile.AudiobookId,
                            currentFile.CapturePathState(),
                            token))
                    {
                        logger.LogInformation(
                            "Preserved non-audio AudiobookFile entry id={Id} because its row changed before removal",
                            currentFile.Id);
                        return;
                    }

                    if (clearLegacyPath)
                    {
                        audiobook!.FilePath = null;
                        audiobook.FileSize = null;
                        if (!await audiobookRepository.UpdateAsync(audiobook))
                        {
                            logger.LogWarning(
                                "Removed non-audio AudiobookFile entry id={Id}, but its audiobook disappeared before legacy path cleanup",
                                currentFile.Id);
                        }
                    }

                    logger.LogInformation(
                        "Removed non-audio AudiobookFile entry id={Id} path={Path}",
                        currentFile.Id,
                        LogRedaction.SanitizeFilePath(currentFile.Path));
                },
                cancellationToken);
        }

        private static async Task<bool> AreSameLibraryPathAsync(
            Audiobook audiobook,
            string? filePath,
            IFileSystemSemanticsResolver semanticsResolver,
            IRootFolderService rootFolderService,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(audiobook.FilePath) || string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            var semantics = await ResolveLibrarySemanticsAsync(
                audiobook.FilePath,
                semanticsResolver,
                rootFolderService,
                cancellationToken);
            return semantics != null
                && FileSystemPathIdentity.AreEquivalent(
                    audiobook.FilePath,
                    filePath,
                    semantics.Value);
        }

        private static async Task<FileSystemPathSemantics?> ResolveLibrarySemanticsAsync(
            string path,
            IFileSystemSemanticsResolver semanticsResolver,
            IRootFolderService rootFolderService,
            CancellationToken cancellationToken)
        {
            if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    path,
                    out var canonicalPath,
                    out _))
            {
                return null;
            }

            if (!FileSystemPathIdentity.TryDetectAbsoluteSyntaxForHost(
                    canonicalPath,
                    out var pathSyntax))
            {
                return null;
            }

            FileSystemPathSemantics? bestSemantics = null;
            var bestRootLength = -1;
            var unavailableRootLength = -1;
            foreach (var root in await rootFolderService.GetAllAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                        root.Path,
                        out var canonicalRoot,
                        out _))
                {
                    if (FileSystemPathIdentity.AmbiguousStoredBoundaryMayContainPath(
                            root.Path,
                            canonicalPath,
                            pathSyntax,
                            root.CaseSensitivityMode))
                    {
                        unavailableRootLength = Math.Max(
                            unavailableRootLength,
                            root.Path.Length);
                    }

                    continue;
                }

                if (!FileSystemPathIdentity.StoredBoundaryMayContainPath(
                        canonicalRoot,
                        canonicalPath,
                        pathSyntax,
                        root.CaseSensitivityMode))
                {
                    continue;
                }

                var rootResolution = await semanticsResolver.ResolveAsync(
                    canonicalRoot,
                    root.CaseSensitivityMode,
                    cancellationToken);
                if (rootResolution.State != PathIdentityState.Valid)
                {
                    unavailableRootLength = Math.Max(
                        unavailableRootLength,
                        canonicalRoot.Length);
                    continue;
                }
                if (!FileSystemPathIdentity.IsSameOrInside(
                        canonicalPath,
                        canonicalRoot,
                        rootResolution.Semantics))
                {
                    continue;
                }

                if (canonicalRoot.Length > bestRootLength)
                {
                    bestSemantics = rootResolution.Semantics;
                    bestRootLength = canonicalRoot.Length;
                }
            }

            if (unavailableRootLength >= bestRootLength
                && unavailableRootLength >= 0)
            {
                return null;
            }

            if (bestSemantics.HasValue)
            {
                return bestSemantics.Value;
            }

            var resolution = await semanticsResolver.ResolveAsync(
                canonicalPath,
                FileSystemCaseSensitivityMode.Auto,
                cancellationToken);
            return resolution.State == PathIdentityState.Valid ? resolution.Semantics : null;
        }
    }
}
