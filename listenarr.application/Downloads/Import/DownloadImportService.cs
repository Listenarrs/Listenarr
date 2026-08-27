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
using Microsoft.Extensions.Logging;
namespace Listenarr.Application.Downloads.Import
{
    public partial class DownloadImportService(
        IFileNamingService fileNamingService,
        IMetadataService metadataService,
        IFileMover fileMover,
        IFilePublicationSourceCapability filePublicationSourceCapability,
        IAudiobookFileService audiobookFileService,
        IArchiveExtractor archiveExtractor,
        IConfigurationService configurationService,
        IRootFolderService rootFolderService,
        ImportDestinationPlanner destinationPlanner,
        IFileSystemSemanticsResolver semanticsResolver,
        ArchiveImportExtractor archiveImportExtractor,
        IAudiobookRepository audiobookRepository,
        IFilesystemMutationCoordinator filesystemMutationCoordinator,
        IAudiobookOperationCoordinator audiobookOperationCoordinator,
        IFileRegistrationRecoveryService fileRegistrationRecoveryService,
        IMoveQueueService moveQueueService,
        ILibraryDirectoryOwnershipStore directoryOwnershipStore,
        ILogger<DownloadImportService> logger,
        IFilePublicationCapabilityResolver? filePublicationCapabilityResolver = null,
        ICompatibilitySourceCleanupCoordinator? compatibilitySourceCleanupCoordinator = null)
        : IDownloadImportService
    {
        private async Task<List<ImportResult>> ImportDownloadFilesCoreAsync(
            Audiobook audiobook,
            List<string> files,
            CancellationToken ct,
            DownloadImportOptions? options,
            IReadOnlyList<FileRegistrationRecoveryReceipt> recoveryReceipts,
            Guid compatibilityBatchId)
        {
            if (string.IsNullOrEmpty(audiobook.BasePath))
            {
                throw new InvalidOperationException($"Audiobook {audiobook.Id} basePath cannot be empty or null");
            }

            var (remainingFiles, recoveredResults) = await ConsumeRecoveredImportsAsync(
                files,
                recoveryReceipts,
                ct);
            files = remainingFiles;
            if (files.Count == 0)
            {
                return recoveredResults;
            }

            var settings = await configurationService.GetApplicationSettingsAsync();
            var expectedBasePath = audiobook.BasePath;
            var destinationResolution = await ResolveDestinationResolutionAsync(
                expectedBasePath,
                ct);
            var destinationSemantics = destinationResolution.Semantics;
            var normalizedBasePath = NormalizeAuthoritativeBasePath(
                expectedBasePath,
                destinationResolution);
            var destinationOwnershipBoundary = await ResolveDestinationOwnershipBoundaryAsync(
                normalizedBasePath,
                destinationResolution,
                ct);
            if (!string.Equals(expectedBasePath, normalizedBasePath, StringComparison.Ordinal))
            {
                var updated = await audiobookRepository.TryUpdateBasePathAsync(
                    audiobook.Id,
                    expectedBasePath,
                    normalizedBasePath,
                    ct);
                if (!updated)
                {
                    throw new InvalidOperationException(
                        "The audiobook base path changed while the import destination was being resolved.");
                }

                audiobook.BasePath = normalizedBasePath;
            }

            try
            {
                var completedFileAction = settings.CompletedFileAction;

                if (settings.ExtractArchives || options?.ForceArchiveExtraction == true)
                {
                    var archives = files
                        .Where(archiveExtractor.IsArchive)
                        .Where(file => !FileUtils.IsBlacklistedFile(file, settings.ImportBlacklistExtensions))
                        .ToList();
                    files = [.. files.Where(file => !archives.Contains(file))];
                    files.AddRange(await archiveImportExtractor.ExtractAsync(archives));
                    if (archives.Count > 0 && completedFileAction == FileAction.HardlinkCopy)
                    {
                        completedFileAction = FileAction.Copy;
                        logger.LogWarning($"Audiobook {audiobook.Id} contains archives thus Hard link mode is impossible: Completed action switched to copy");
                    }
                }

                var results = recoveredResults;
                var folderPattern = settings.FolderNamingPattern;
                var candidateFiles = files.Where(file => !FileUtils.IsBlacklistedFile(file, settings.ImportBlacklistExtensions)).ToList();
                var sourceRootPath = FileUtils.GetCommonDirectory(candidateFiles);
                FileSystemPathSemantics? sourceSemantics = null;
                var sourcePathComparer = StringComparer.Ordinal;
                if (!string.IsNullOrWhiteSpace(sourceRootPath))
                {
                    sourceSemantics = await ResolvePathSemanticsAsync(
                        sourceRootPath,
                        "Source filesystem identity is unavailable.",
                        ct);
                    sourcePathComparer = sourceSemantics.Value.Comparer;
                }
                var sourceFiles = candidateFiles.Distinct(sourcePathComparer).ToList();
                sourceRootPath = FileUtils.GetCommonDirectory(sourceFiles);
                var plannedAudioFiles = MultiFileImportPlanner.BuildPlans(
                    sourceFiles.Where(FileUtils.IsAudioFile).Select(f => (f, (string?)null)),
                    sourcePathComparer);
                var planByPath = plannedAudioFiles.ToDictionary(p => p.FullPath, sourcePathComparer);
                var diskNumbersForNaming = MultiFileImportPlanner.BuildStableNamingNumbers(plannedAudioFiles, p => p.DiskNumberHint, sourcePathComparer);
                var chapterNumbersForNaming = MultiFileImportPlanner.BuildStableNamingNumbers(plannedAudioFiles, p => p.ChapterNumberHint, sourcePathComparer);
                var isMultiFileBatch = plannedAudioFiles.Count > 1;
                var usedDestinations = new HashSet<string>(destinationSemantics.Comparer);
                var orderedFiles = plannedAudioFiles.Select(p => p.FullPath)
                    .Concat(sourceFiles.Where(f => !planByPath.ContainsKey(f)))
                    .ToList();

                try
                {
                    string? bestExisting = null;
                    QualityProfile? abProfile = audiobook.QualityProfile;
                    if (audiobook.Files != null && audiobook.Files.Count != 0)
                    {
                        foreach (var f in audiobook.Files)
                        {
                            string q = string.Empty;
                            if (!string.IsNullOrEmpty(f.Format)) q = f.Format;
                            if (f.Bitrate.HasValue)
                            {
                                var kb = f.Bitrate.Value / 1000;
                                if (kb >= 320) q = "MP3 320kbps";
                                else if (kb >= 256) q = "MP3 256kbps";
                                else if (kb >= 192) q = "MP3 192kbps";
                                else if (kb >= 128) q = "MP3 128kbps";
                            }
                            if (string.IsNullOrEmpty(q) && !string.IsNullOrEmpty(f.Path)) q = ImportQualityEvaluator.Determine(null, f.Path);
                            if (string.IsNullOrEmpty(bestExisting)) bestExisting = q;
                            else if (!string.IsNullOrEmpty(q) && !string.IsNullOrEmpty(bestExisting) && abProfile != null && ImportQualityEvaluator.IsAcceptable(q, bestExisting, abProfile)) bestExisting = q;
                        }
                    }

                    foreach (var file in orderedFiles)
                    {
                        var fileSourceSemantics = sourceSemantics
                            ?? await ResolvePathSemanticsAsync(
                                file,
                                "Source filesystem identity is unavailable.",
                                ct);
                        if (!FileUtils.IsAudioFile(file))
                        {
                            var hasSuccessfulAudioImport = results.Any(r => r.Success && !string.IsNullOrWhiteSpace(r.FinalPath) && !string.IsNullOrWhiteSpace(r.SourcePath) && FileUtils.IsAudioFile(r.SourcePath!));
                            if (!hasSuccessfulAudioImport || string.IsNullOrWhiteSpace(audiobook.BasePath))
                            {
                                results.Add(ImportResult.Skipped("No successful audio import in batch"));
                                logger.LogDebug("ImportFilesFromDirectory: Skipping companion file {File} because no successful audio import was recorded for the batch", file);
                                continue;
                            }

                            try
                            {
                                var relativePath = !string.IsNullOrWhiteSpace(sourceRootPath) ? Path.GetRelativePath(sourceRootPath, file) : Path.GetFileName(file);
                                if (!destinationPlanner.TryResolve(audiobook.BasePath, relativePath, destinationSemantics, out var destination))
                                {
                                    results.Add(ImportResult.ImportFailure(completedFileAction, file, audiobook.BasePath));
                                    logger.LogWarning("Blocked companion import outside audiobook base path. Audiobook {AudiobookId}, Source {Source}, Relative {Relative}, BasePath {BasePath}", audiobook.Id, file, relativePath, audiobook.BasePath);
                                    continue;
                                }

                                var sourceProof =
                                    await ResolvePublishableSourceProofAsync(
                                        file,
                                        ct);
                                if (!sourceProof.HasValue)
                                {
                                    results.Add(ImportResult.ImportFailure(completedFileAction, file, destination));
                                    continue;
                                }
                                var destinationReservation = await destinationPlanner.PlanIdempotentOrUniqueAsync(
                                    sourceProof.Value,
                                    destination,
                                    usedDestinations,
                                    destinationSemantics,
                                    ct);
                                destination = destinationReservation.Path;
                                var companionPublication =
                                    await PerformOwnedFileActionAsync(
                                        completedFileAction,
                                        file,
                                        destination,
                                        destinationOwnershipBoundary,
                                        destinationSemantics,
                                        FileMoveOperationIdentity.CreateForPaths(
                                            "download-import",
                                            audiobook.Id,
                                            completedFileAction,
                                            file,
                                            fileSourceSemantics,
                                            sourceProof.Value,
                                            destination,
                                            destinationSemantics),
                                        sourceProof.Value,
                                        audiobook.Id,
                                        compatibilityBatchId,
                                        ct);
                                if (companionPublication == null)
                                {
                                    results.Add(ImportResult.ImportFailure(completedFileAction, file, destination));
                                    continue;
                                }

                                ImportDestinationPlanner.Commit(destinationReservation, usedDestinations);
                                results.Add(ImportResult.ImportSuccess(
                                    completedFileAction,
                                    companionPublication.EffectiveAction,
                                    companionPublication.SourceDisposition
                                        == FilePublicationSourceDisposition.Retained
                                            ? ImportSourceDisposition.Retained
                                            : companionPublication.SourceDisposition
                                                == FilePublicationSourceDisposition.Retired
                                                    ? ImportSourceDisposition.Retired
                                                    : ImportSourceDisposition.Unchanged,
                                    file,
                                    destination,
                                    warningCode: companionPublication.ReasonCode,
                                    message: companionPublication.Message));
                            }
                            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
                            {
                                results.Add(ImportResult.Exception(exception, file));
                                logger.LogWarning(exception, $"Failed companion-file import {file}");
                            }
                            continue;
                        }

                        try
                        {
                            var sourceProof =
                                await ResolvePublishableSourceProofAsync(
                                    file,
                                    ct);
                            if (!sourceProof.HasValue)
                            {
                                results.Add(ImportResult.ImportFailure(
                                    completedFileAction,
                                    file,
                                    audiobook.BasePath));
                                continue;
                            }

                            planByPath.TryGetValue(file, out var plan);
                            diskNumbersForNaming.TryGetValue(file, out var namingDiskNumber);
                            chapterNumbersForNaming.TryGetValue(file, out var namingChapterNumber);
                            AudioMetadata? candidateMetadata = settings.EnableMetadataProcessing ? await metadataService.ExtractFileMetadataAsync(file) : null;
                            var candidateQuality = ImportQualityEvaluator.Determine(candidateMetadata, file);
                            try
                            {
                                if (audiobook.Files != null && audiobook.Files.Count != 0 && !ImportQualityEvaluator.IsAcceptable(candidateQuality, bestExisting, abProfile))
                                {
                                    results.Add(ImportResult.Skipped($"candidate quality '{candidateQuality}' is not better than existing '{bestExisting}'"));
                                    logger.LogInformation($"Skipping import of file {file} for audiobook {audiobook.Id} because candidate quality '{candidateQuality}' is not better than existing '{bestExisting}'");
                                    continue;
                                }
                            }
                            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
                            {
                                logger.LogDebug(exception, $"ImportFilesFromDirectory: Failed to evaluate quality for multi-file import {file}");
                            }

                            string destDirForFile = audiobook.BasePath;
                            var namingMetadata = BuildNamingMetadata(audiobook, candidateMetadata, Path.GetFileNameWithoutExtension(file));
                            var effectiveDiskNumber = namingDiskNumber > 0 ? namingDiskNumber : (namingMetadata.DiscNumber ?? plan?.DiskNumberHint);
                            var effectiveChapterNumber = namingChapterNumber > 0 ? namingChapterNumber : (namingMetadata.TrackNumber ?? plan?.ChapterNumberHint);
                            if (isMultiFileBatch)
                            {
                                effectiveDiskNumber ??= effectiveChapterNumber;
                                effectiveChapterNumber ??= effectiveDiskNumber;
                            }

                            var variablesForFile = new Dictionary<string, object>
                            {
                                { "Author", namingMetadata.Artist ?? "Unknown Author" },
                                { "Series", string.IsNullOrWhiteSpace(namingMetadata.Series) ? string.Empty : namingMetadata.Series },
                                { "Title", namingMetadata.Title ?? Path.GetFileNameWithoutExtension(file) },
                                { "Subtitle", string.IsNullOrWhiteSpace(namingMetadata.Subtitle) ? string.Empty : namingMetadata.Subtitle },
                                { "Edition", string.IsNullOrWhiteSpace(namingMetadata.Edition) ? string.Empty : namingMetadata.Edition },
                                { "Narrator", string.IsNullOrWhiteSpace(namingMetadata.Narrator) ? string.Empty : namingMetadata.Narrator },
                                { "Publisher", string.IsNullOrWhiteSpace(namingMetadata.Publisher) ? string.Empty : namingMetadata.Publisher },
                                { "Language", string.IsNullOrWhiteSpace(namingMetadata.Language) ? string.Empty : namingMetadata.Language },
                                { "Asin", string.IsNullOrWhiteSpace(namingMetadata.Asin) ? string.Empty : namingMetadata.Asin },
                                { "SeriesNumber", namingMetadata.SeriesPosition?.ToString() ?? effectiveChapterNumber?.ToString() ?? string.Empty },
                                { "Year", namingMetadata.Year?.ToString() ?? string.Empty },
                                { "Quality", (namingMetadata.BitRate.HasValue ? $"{namingMetadata.BitRate}kbps" : null) ?? namingMetadata.Format ?? string.Empty },
                                { "DiskNumber", effectiveDiskNumber?.ToString() ?? string.Empty },
                                { "ChapterNumber", effectiveChapterNumber?.ToString() ?? string.Empty }
                            };

                            var folderRelative = fileNamingService.ApplyNamingPattern(folderPattern, variablesForFile, treatAsFilename: false);
                            if (string.IsNullOrEmpty(audiobook.BasePath) && !string.IsNullOrWhiteSpace(folderRelative))
                            {
                                if (!destinationPlanner.TryResolve(destDirForFile, folderRelative, destinationSemantics, out destDirForFile))
                                {
                                    results.Add(ImportResult.ImportFailure(completedFileAction, file, audiobook.BasePath));
                                    logger.LogWarning("Blocked folder pattern outside audiobook base path. Audiobook {AudiobookId}, Source {Source}, FolderRelative {FolderRelative}, BasePath {BasePath}", audiobook.Id, file, folderRelative, audiobook.BasePath);
                                    continue;
                                }
                            }

                            var baseFilePattern = isMultiFileBatch ? settings.MultiFileNamingPattern : settings.FileNamingPattern;
                            var ext = Path.GetExtension(file);
                            var patternAllowsSubfolders = baseFilePattern.IndexOf("DiskNumber", StringComparison.OrdinalIgnoreCase) >= 0 || baseFilePattern.Contains("ChapterNumber", StringComparison.OrdinalIgnoreCase) || baseFilePattern.Contains('/') || baseFilePattern.Contains('\\');
                            var filename = fileNamingService.ApplyNamingPattern(baseFilePattern, variablesForFile, !patternAllowsSubfolders);
                            if (!filename.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) filename += ext;
                            if (!patternAllowsSubfolders)
                            {
                                try
                                {
                                    var forced = Path.GetFileName(filename);
                                    var invalid = Path.GetInvalidFileNameChars();
                                    var sb = new System.Text.StringBuilder();
                                    foreach (var c in forced) sb.Append(invalid.Contains(c) ? '_' : c);
                                    filename = sb.ToString();
                                }
                                catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
                                {
                                    filename = Path.GetFileName(filename);
                                }
                            }

                            if (!destinationPlanner.TryResolve(destDirForFile, filename, destinationSemantics, out var destination))
                            {
                                results.Add(ImportResult.ImportFailure(completedFileAction, file, destDirForFile));
                                logger.LogWarning("Blocked audio import outside audiobook base path. Audiobook {AudiobookId}, Source {Source}, Filename {Filename}, BasePath {BasePath}", audiobook.Id, file, filename, destDirForFile);
                                continue;
                            }

                            var requestedDestination = destination;
                            ImportDestinationReservation? destinationReservation = null;
                            AudiobookFileOwnershipCheckResult? ownership = null;
                            while (true)
                            {
                                destinationReservation = await destinationPlanner.PlanIdempotentOrUniqueAsync(
                                    sourceProof.Value,
                                    requestedDestination,
                                    usedDestinations,
                                    destinationSemantics,
                                    ct);
                                destination = destinationReservation.Path;
                                ownership = await audiobookFileService.CheckAudiobookFileOwnershipAsync(
                                    audiobook,
                                    destination,
                                    cancellationToken: ct);
                                if (ownership.Outcome is
                                    AudiobookFileOwnershipCheckOutcome.Available or
                                    AudiobookFileOwnershipCheckOutcome.AlreadyOwnedByAudiobook)
                                {
                                    if (destinationReservation.ReusesExistingFile
                                        && !sourceProof.Value.HasDurablePhysicalObjectIdentity
                                        && ownership.Outcome
                                            == AudiobookFileOwnershipCheckOutcome.Available)
                                    {
                                        // Matching bytes are not an ownership claim.
                                        // Preserve the existing path and plan another suffix.
                                        usedDestinations.Add(destination);
                                        continue;
                                    }
                                    break;
                                }

                                if (!destinationReservation.ReusesExistingFile)
                                {
                                    results.Add(ImportResult.ImportFailure(
                                        completedFileAction,
                                        file,
                                        destination));
                                    logger.LogWarning(
                                        "Blocked audio import because destination ownership is unavailable. Audiobook {AudiobookId}, Source {Source}, Destination {Destination}, Outcome {Outcome}, Reason {Reason}",
                                        audiobook.Id,
                                        file,
                                        destination,
                                        ownership.Outcome,
                                        ownership.Reason);
                                    destinationReservation = null;
                                    break;
                                }

                                // An existing byte-identical suffix may legitimately
                                // belong to another audiobook. Exclude that occupied
                                // path and continue planning instead of turning a safe
                                // import into an ownership conflict.
                                usedDestinations.Add(destination);
                            }
                            if (destinationReservation == null || ownership == null)
                            {
                                continue;
                            }

                            var operationId = FileMoveOperationIdentity.CreateForPaths(
                                "download-import",
                                audiobook.Id,
                                completedFileAction,
                                file,
                                fileSourceSemantics,
                                sourceProof.Value,
                                destination,
                                destinationSemantics);
                            var publicationPlan = await ResolvePublicationPlanAsync(
                                completedFileAction,
                                file,
                                destination,
                                sourceProof.Value,
                                compatibilityBatchId,
                                ct);
                            if (!publicationPlan.IsAllowed)
                            {
                                results.Add(CreateBlockedImportResult(
                                    publicationPlan,
                                    file,
                                    destination));
                                continue;
                            }

                            var publicationFailure = await PrepareRegisterAndCompletePublicationAsync(
                                    publicationPlan,
                                    file,
                                    destination,
                                    destinationOwnershipBoundary,
                                    destinationSemantics,
                                    operationId,
                                    ownership.ExistingFile?.PhysicalObjectIdentity,
                                    sourceProof.Value,
                                    audiobook,
                                    ownership,
                                    ct);
                            if (publicationFailure != null)
                            {
                                results.Add(publicationFailure);
                                continue;
                            }

                            ImportDestinationPlanner.Commit(
                                destinationReservation,
                                usedDestinations);
                            results.Add(ImportResult.ImportSuccess(
                                completedFileAction,
                                publicationPlan.EffectiveAction,
                                ToImportSourceDisposition(
                                    publicationPlan.SourceDisposition),
                                file,
                                destination,
                                wasRegisteredToAudiobook: true,
                                publicationPlan.ReasonCode,
                                publicationPlan.Message));
                        }
                        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
                        {
                            results.Add(ImportResult.Exception(exception, file));
                            logger.LogWarning(exception, $"ImportFilesFromDirectory: Failed processing file in directory import: {file}");
                        }
                    }
                }
                catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
                {
                    logger.LogWarning(exception, $"Failed to import files for audiobook {audiobook.Id}");
                }

                return results;
            }
            finally
            {
                archiveImportExtractor.DisposeTemporaryDirectories();
            }
        }
    }
}
