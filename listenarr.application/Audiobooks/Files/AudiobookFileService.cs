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
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;
using Listenarr.Application.Common;
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Files
{
    public partial class AudiobookFileService(
        IMemoryCache memoryCache,
        MetadataExtractionLimiter limiter,
        IAudiobookRepository audiobookRepository,
        IAudiobookFileRepository audiobookFileRepository,
        IHistoryRepository historyRepository,
        IMetadataService metadataService,
        IToastService toastService,
        IFfmpegService ffmpegService,
        IFileSystem fileSystem,
        IFileSystemSemanticsResolver semanticsResolver,
        IAudiobookFilePathIdentityResolver filePathIdentityResolver,
        IRootFolderService rootFolderService,
        ILogger<AudiobookFileService> logger,
        IFilesystemMutationCoordinator filesystemMutationCoordinator,
        IAudiobookOperationCoordinator audiobookOperationCoordinator,
        IMoveQueueService moveQueueService) : IAudiobookFileService
    {
        public Task<bool> EnsureAudiobookFileAsync(
            Audiobook audiobook,
            string filePath,
            string? source = "scan",
            CancellationToken cancellationToken = default) =>
            EnsureAudiobookFileAsync(
                audiobook,
                filePath,
                registrationLease: null,
                authoritativeBasePath: null,
                basePathCommitContext: null,
                source,
                cancellationToken);

        public Task<bool> EnsureAudiobookFileAsync(
            Audiobook audiobook,
            IAudiobookFileRegistrationLease registrationLease,
            string? source = "scan",
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(registrationLease);
            ArgumentException.ThrowIfNullOrWhiteSpace(registrationLease.PublicPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(registrationLease.MetadataPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(
                registrationLease.PhysicalObjectIdentity);
            return EnsureAudiobookFileAsync(
                audiobook,
                registrationLease.PublicPath,
                registrationLease,
                authoritativeBasePath: null,
                basePathCommitContext: null,
                source,
                cancellationToken);
        }

        private async Task<BasePathRegistrationOutcome>
            EnsureAudiobookFileWithBasePathAsync(
                Audiobook audiobook,
                IAudiobookFileRegistrationLease registrationLease,
                string authoritativeBasePath,
                string? source,
                CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(registrationLease);
            ArgumentException.ThrowIfNullOrWhiteSpace(authoritativeBasePath);
            var context = new AudiobookBasePathCommitContext();
            var success = await EnsureAudiobookFileAsync(
                audiobook,
                registrationLease.PublicPath,
                registrationLease,
                FileUtils.NormalizeStoredPath(authoritativeBasePath),
                context,
                source,
                cancellationToken);
            return new BasePathRegistrationOutcome(
                success,
                success ? context.Mutation : null);
        }

        private Task<bool> EnsureAudiobookFileAsync(
            Audiobook audiobook,
            string filePath,
            IAudiobookFileRegistrationLease? registrationLease,
            string? authoritativeBasePath,
            AudiobookBasePathCommitContext? basePathCommitContext,
            string? source,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(audiobook);
            return filesystemMutationCoordinator.ExecuteExclusiveAsync(
                globalToken => audiobookOperationCoordinator.ExecuteExclusiveAsync(
                    audiobook.Id,
                    async token =>
                    {
                        await moveQueueService.EnsureFilesystemMutationAllowedAsync(
                            audiobook.Id,
                            token);
                        var currentAudiobook = await audiobookRepository.GetByIdSnapshotAsync(audiobook.Id, token);
                        if (currentAudiobook == null)
                        {
                            logger.LogDebug(
                                "Skipping audiobook file registration because audiobook {AudiobookId} no longer exists",
                                audiobook.Id);
                            return false;
                        }

                        AudiobookBasePathMutation? basePathMutation = null;
                        if (!string.IsNullOrWhiteSpace(authoritativeBasePath))
                        {
                            basePathMutation = new AudiobookBasePathMutation(
                                currentAudiobook.Id,
                                currentAudiobook.BasePath,
                                authoritativeBasePath);
                            basePathCommitContext!.Mutation = basePathMutation;
                            currentAudiobook.BasePath = authoritativeBasePath;
                        }

                        return await EnsureAudiobookFileCoreAsync(
                            currentAudiobook,
                            filePath,
                            registrationLease,
                            basePathMutation,
                            source,
                            token);
                    },
                    globalToken),
                cancellationToken);
        }

        private async Task<bool> EnsureAudiobookFileCoreAsync(
            Audiobook audiobook,
            string filePath,
            IAudiobookFileRegistrationLease? registrationLease,
            AudiobookBasePathMutation? basePathMutation,
            string? source,
            CancellationToken cancellationToken)
        {
            try
            {
                if (registrationLease != null
                    && !registrationLease.MatchesCurrentPublication())
                {
                    logger.LogInformation(
                        "Skipping audiobook file registration because the discovered file generation is no longer published for audiobook {AudiobookId}: {Path}",
                        audiobook.Id,
                        LogRedaction.SanitizeFilePath(filePath));
                    return false;
                }

                var metadataPath = registrationLease?.MetadataPath ?? filePath;
                if (!fileSystem.FileExists(filePath)
                    || fileSystem.IsReparsePoint(filePath))
                {
                    return false;
                }

                if (!FileUtils.IsAudioFile(filePath))
                {
                    logger.LogInformation("Skipping non-audio audiobook file registration for audiobook {AudiobookId}: {Path}", audiobook.Id, LogRedaction.SanitizeFilePath(filePath));
                    return false;
                }

                // Conservative safety: if the audiobook already has a stored FilePath prefer
                // to only associate files in the same containing directory or BasePath.
                try
                {
                    if (!string.IsNullOrWhiteSpace(audiobook.FilePath)
                        || !string.IsNullOrWhiteSpace(audiobook.BasePath))
                    {
                        var normalizedBasePath = ResolveStoredAbsolutePathForHost(
                            audiobook.BasePath);
                        if (!string.IsNullOrWhiteSpace(audiobook.BasePath)
                            && string.IsNullOrWhiteSpace(normalizedBasePath))
                        {
                            logger.LogWarning(
                                "Refusing audiobook file registration because the persisted BasePath is unavailable on this host. AudiobookId={AudiobookId} BasePath={BasePath}",
                                audiobook.Id,
                                LogRedaction.SanitizeFilePath(audiobook.BasePath));
                            return false;
                        }

                        var existingDir = string.IsNullOrWhiteSpace(normalizedBasePath)
                            ? ResolveStoredFileDirectory(audiobook)
                            : string.Empty;
                        var candidateDir = ResolveAbsolutePath(Path.GetDirectoryName(filePath));
                        var candidateFull = ResolveAbsolutePath(filePath);

                        if (!string.IsNullOrEmpty(candidateDir)
                            && !string.IsNullOrEmpty(candidateFull)
                            && (!string.IsNullOrEmpty(existingDir)
                                || !string.IsNullOrEmpty(normalizedBasePath)))
                        {
                            var rootFolders = await GetRootFoldersForSemanticsAsync(cancellationToken);
                            var existingDirResolution = string.IsNullOrWhiteSpace(existingDir)
                                ? null
                                : await ResolveLibraryPathSemanticsAsync(
                                    existingDir,
                                    rootFolders,
                                    cancellationToken);
                            var isInExistingDir = existingDirResolution != null
                                && FileSystemPathIdentity.IsSameOrInside(
                                    candidateDir,
                                    existingDir,
                                    existingDirResolution.Semantics);

                            LibraryPathSemanticsResolution? basePathResolution = null;
                            var isInBasePath = false;
                            if (!string.IsNullOrWhiteSpace(normalizedBasePath))
                            {
                                basePathResolution = await ResolveLibraryPathSemanticsAsync(
                                    normalizedBasePath,
                                    rootFolders,
                                    cancellationToken);
                                isInBasePath = basePathResolution != null
                                    && FileSystemPathIdentity.IsSameOrInside(
                                        candidateFull,
                                        normalizedBasePath,
                                        basePathResolution.Semantics);
                            }

                            if (!isInExistingDir && !isInBasePath)
                            {
                                var audiobookTitle = audiobook.Title ?? "Unknown";
                                logger.LogWarning("Refusing to associate file outside audiobook folder. AudiobookId={AudiobookId}, AudiobookDir={AudiobookDir}, BasePath={BasePath}, File={File}", audiobook.Id, LogRedaction.SanitizeFilePath(existingDir), LogRedaction.SanitizeFilePath(audiobook.BasePath), LogRedaction.SanitizeFilePath(filePath));
                                try
                                {
                                    var historyEntry = new History
                                    {
                                        AudiobookId = audiobook.Id,
                                        AudiobookTitle = audiobookTitle,
                                        EventType = "File Association Refused",
                                        Message = $"Refused to associate file outside audiobook folder: {Path.GetFileName(filePath)}",
                                        Source = source ?? "Scan",
                                        Data = JsonSerializer.Serialize(new { FilePath = filePath, AudiobookDir = existingDir, BasePath = audiobook.BasePath }),
                                        Timestamp = DateTime.UtcNow
                                    };
                                    await historyRepository.AddAsync(historyEntry);

                                    try
                                    {
                                        await toastService.PublishToastAsync("warning", "File not associated", $"Refused to associate {Path.GetFileName(filePath)} to {audiobookTitle}");
                                    }
                                    catch (Exception thx) when (thx is not OperationCanceledException && thx is not OutOfMemoryException && thx is not StackOverflowException)
                                    {
                                        logger.LogDebug(thx, "Failed to publish toast for refused file association");
                                    }
                                }
                                catch (Exception hx) when (hx is not OperationCanceledException && hx is not OutOfMemoryException && hx is not StackOverflowException)
                                {
                                    logger.LogDebug(hx, "Failed to persist history for refused file association (AudiobookId={AudiobookId}, File={File})", audiobook.Id, LogRedaction.SanitizeFilePath(filePath));
                                }

                                return false;
                            }

                            var allowedContainmentRoots = new List<string?>();
                            if (isInExistingDir)
                            {
                                allowedContainmentRoots.Add(ResolvePhysicalSafetyRoot(
                                    candidateFull,
                                    existingDir,
                                    existingDirResolution!));
                            }

                            if (isInBasePath)
                            {
                                allowedContainmentRoots.Add(ResolvePhysicalSafetyRoot(
                                    candidateFull,
                                    normalizedBasePath,
                                    basePathResolution!));
                            }
                            if (!fileSystem.TryValidateMutationTarget(
                                    candidateFull,
                                    allowedContainmentRoots,
                                    out var validatedCandidate,
                                    out var validationReason))
                            {
                                logger.LogWarning(
                                    "Refusing audiobook file registration because its path did not resolve safely inside the audiobook folder. AudiobookId={AudiobookId} File={File} Reason={Reason}",
                                    audiobook.Id,
                                    LogRedaction.SanitizeFilePath(filePath),
                                    validationReason);
                                return false;
                            }

                            filePath = validatedCandidate;
                        }
                    }
                }
                catch (Exception exDir) when (exDir is not OperationCanceledException && exDir is not OutOfMemoryException && exDir is not StackOverflowException)
                {
                    logger.LogWarning(
                        exDir,
                        "Refusing audiobook file registration because folder containment could not be verified. AudiobookId={AudiobookId} File={File}",
                        audiobook.Id,
                        LogRedaction.SanitizeFilePath(filePath));
                    return false;
                }

                var cacheIdentity = registrationLease?.PhysicalObjectIdentity
                    ?? filePath;
                var meta = await ExtractMetadataAsync(
                    metadataPath,
                    cacheIdentity,
                    filePath);

                var fileRecord = AudiobookFile.CreateUnresolved(filePath);
                fileRecord.AudiobookId = audiobook.Id;
                fileRecord.Size = ResolveRegisteredLength(
                    registrationLease,
                    metadataPath);
                fileRecord.Source = source;
                fileRecord.CreatedAt = DateTime.UtcNow;
                fileRecord.DurationSeconds = meta?.Duration.TotalSeconds;
                fileRecord.Format = meta?.Format;
                fileRecord.Container = meta?.Container;
                fileRecord.Codec = meta?.Codec;
                fileRecord.Bitrate = meta?.BitRate;
                fileRecord.SampleRate = meta?.SampleRate;
                fileRecord.Channels = meta?.Channels;
                if (registrationLease?.HasDurablePhysicalObjectIdentity == true)
                {
                    fileRecord.ApplyPhysicalObjectIdentity(
                        registrationLease.PhysicalObjectIdentity,
                        DateTime.UtcNow);
                }

                var attempts = 0;
                while (true)
                {
                    try
                    {
                        if (registrationLease != null
                            && !registrationLease.MatchesCurrentPublication())
                        {
                            logger.LogInformation(
                                "Skipping audiobook file claim because the discovered file generation changed before persistence for audiobook {AudiobookId}: {Path}",
                                audiobook.Id,
                                LogRedaction.SanitizeFilePath(filePath));
                            return false;
                        }

                        var claim = await ClaimAudiobookFileCoreAsync(
                            audiobook,
                            fileRecord,
                            filePath,
                            basePathMutation,
                            cancellationToken);
                        if (!claim.Created)
                        {
                            LogClaimRejection(audiobook.Id, filePath, claim);
                            return false;
                        }

                        if (registrationLease != null
                            && ProbeCurrentPublication(registrationLease)
                                == RegistrationPublicationMatchOutcome.Mismatch)
                        {
                            await DeleteCreatedPhysicalGenerationAsync(
                                fileRecord,
                                basePathMutation);
                            logger.LogWarning(
                                "Removed audiobook file claim because the public file generation changed during persistence for audiobook {AudiobookId}: {Path}",
                                audiobook.Id,
                                LogRedaction.SanitizeFilePath(filePath));
                            return false;
                        }

                        logger.LogInformation("Created AudiobookFile for audiobook {AudiobookId}: {Path} Id={Id}", audiobook.Id, LogRedaction.SanitizeFilePath(filePath), fileRecord.Id);

                        // Add history entry and update audiobook backward-compat fields
                        try
                        {
                            var historyEntry = new History
                            {
                                AudiobookId = audiobook.Id,
                                AudiobookTitle = audiobook.Title ?? "Unknown",
                                EventType = "File Added",
                                Message = $"File scanned and added: {Path.GetFileName(filePath)}",
                                Source = source ?? "Scan",
                                Data = JsonSerializer.Serialize(new
                                {
                                    FilePath = fileRecord.Path,
                                    FileSize = fileRecord.Size,
                                    Format = fileRecord.Format,
                                    Source = fileRecord.Source
                                }),
                                Timestamp = DateTime.UtcNow
                            };
                            await historyRepository.AddAsync(historyEntry);
                        }
                        catch (Exception hx) when (hx is not OperationCanceledException && hx is not OutOfMemoryException && hx is not StackOverflowException)
                        {
                            logger.LogDebug(hx, "Failed to create history entry for added audiobook file {Path}", LogRedaction.SanitizeFilePath(filePath));
                        }

                        return true;
                    }
                    catch (PersistenceException dbEx)
                    {
                        attempts++;
                        if (attempts >= 3)
                        {
                            logger.LogWarning(dbEx, "Failed to save AudiobookFile after {Attempts} attempts: {Path}", attempts, LogRedaction.SanitizeFilePath(filePath));
                            return false;
                        }
                        await Task.Delay(100 * attempts, cancellationToken);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Failed to create AudiobookFile record for audiobook {AudiobookId} at {Path}", audiobook.Id, LogRedaction.SanitizeFilePath(filePath));
                return false;
            }
        }

        private static string ResolveAbsolutePath(string? path) =>
            string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : FileSystemPathIdentity.ResolveNativeAbsolutePath(path);

        private void LogClaimRejection(
            int audiobookId,
            string path,
            AudiobookFileClaimResult claim)
        {
            var sanitizedPath = LogRedaction.SanitizeFilePath(path);
            if (claim.Outcome == AudiobookFileClaimOutcome.AlreadyOwnedByAudiobook)
            {
                logger.LogDebug(
                    "AudiobookFile already exists for audiobook {AudiobookId} at path {Path}",
                    audiobookId,
                    sanitizedPath);
                return;
            }

            logger.LogWarning(
                "Audiobook file ownership claim rejected for audiobook {AudiobookId} at {Path}: {Outcome}. {Reason}",
                audiobookId,
                sanitizedPath,
                claim.Outcome,
                claim.Reason);
        }

    }
}
