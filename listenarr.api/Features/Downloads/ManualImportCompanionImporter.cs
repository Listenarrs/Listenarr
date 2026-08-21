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
 */

using Listenarr.Api.Dtos.ManualImport;
using Listenarr.Domain.Common;

namespace Listenarr.Api.Features.Downloads;

public sealed partial class ManualImportCompanionImporter
{
    private readonly IMetadataService _metadataService;
    private readonly IFileMover _fileMover;
    private readonly IFilePublicationSourceCapability _filePublicationSourceCapability;
    private readonly IFileSystem _fileSystem;
    private readonly ILibraryDirectoryOwnershipStore _directoryOwnershipStore;
    private readonly ILogger<ManualImportCompanionImporter> _logger;
    private readonly IAudiobookFileService? _audiobookFileService;
    private readonly IFilePublicationCapabilityResolver?
        _filePublicationCapabilityResolver;

    public ManualImportCompanionImporter(
        IMetadataService metadataService,
        IFileMover fileMover,
        IFilePublicationSourceCapability filePublicationSourceCapability,
        IFileSystem fileSystem,
        ILibraryDirectoryOwnershipStore directoryOwnershipStore,
        ILogger<ManualImportCompanionImporter> logger,
        IAudiobookFileService? audiobookFileService = null,
        IFilePublicationCapabilityResolver? filePublicationCapabilityResolver = null)
    {
        _metadataService = metadataService;
        _fileMover = fileMover;
        _filePublicationSourceCapability = filePublicationSourceCapability
            ?? throw new ArgumentNullException(nameof(filePublicationSourceCapability));
        _fileSystem = fileSystem;
        _directoryOwnershipStore = directoryOwnershipStore;
        _logger = logger;
        _audiobookFileService = audiobookFileService;
        _filePublicationCapabilityResolver = filePublicationCapabilityResolver;
    }

    public async Task<IReadOnlyCollection<FileUtils.AudioMatchProfile>> BuildAudioMatchProfilesAsync(
        IEnumerable<string> filePaths,
        StringComparer sourcePathComparer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (await Task.WhenAll(filePaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(sourcePathComparer)
                .Select(async path =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return await BuildAudioMatchProfileAsync(path);
                })))
            .Where(profile => profile != null)
            .Cast<FileUtils.AudioMatchProfile>()
            .ToList();
    }

    public async Task<int> ImportAsync(
        FileAction action,
        IReadOnlyCollection<ManualImportItemDto> orderedItems,
        IReadOnlyCollection<ManualImportResultDto> results,
        string sourceRootPath,
        IReadOnlyCollection<FileUtils.AudioMatchProfile> selectedAudioProfiles,
        ManualImportDestinationTracker destinationTracker,
        FileSystemPathSemantics sourceSemantics,
        IReadOnlyDictionary<int, FileSystemSemanticsResolution> destinationResolutionsByAudiobook,
        IEnumerable<string> importBlacklist,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var audiobookIds = orderedItems
            .Select(item => item.MatchedAudiobookId)
            .Distinct()
            .ToList();

        if (audiobookIds.Count != 1)
        {
            _logger.LogDebug("Skipping companion-file import because the batch contains {Count} audiobook targets", audiobookIds.Count);
            return 0;
        }
        if (!destinationResolutionsByAudiobook.TryGetValue(
                audiobookIds[0],
                out var destinationResolution)
            || destinationResolution.State != PathIdentityState.Valid)
        {
            _logger.LogWarning(
                "Skipping companion-file import because no authoritative destination filesystem semantics are available for audiobook {AudiobookId}",
                audiobookIds[0]);
            return 0;
        }

        var targetAudiobook = results
            .Where(result => result.Success && result.Audiobook?.Id == audiobookIds[0])
            .Select(result => result.Audiobook)
            .FirstOrDefault();
        var destinationRoot = ManualImportPathPlanner.DetermineScanPath(results
            .Where(r => r.Success && !string.IsNullOrWhiteSpace(r.DestinationPath))
            .Select(r => r.DestinationPath!)
            .ToList());

        if (string.IsNullOrWhiteSpace(destinationRoot))
        {
            _logger.LogDebug("Skipping companion-file import because no destination root could be resolved for {SourceRoot}", sourceRootPath);
            return 0;
        }

        var selectedSourceFiles = new HashSet<string>(
            orderedItems
                .Where(item => !string.IsNullOrWhiteSpace(item.FullPath))
                .Select(item => Path.GetFullPath(item.FullPath!)),
            sourceSemantics.Comparer);

        var selectedDirectories = selectedSourceFiles
            .Select(Path.GetDirectoryName)
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(sourceSemantics.Comparer)
            .ToList();

        var companionFiles = selectedDirectories
            .Where(directory => directory != null && _fileSystem.DirectoryExists(directory))
            .SelectMany(dir => _fileSystem.EnumerateFiles(dir!, "*", SearchOption.TopDirectoryOnly))
            .Where(file => !FileUtils.IsBlacklistedFile(file, importBlacklist))
            .Select(Path.GetFullPath)
            .Where(file => !selectedSourceFiles.Contains(file))
            .Distinct(sourceSemantics.Comparer)
            .ToList();

        if (!FileSystemPathIdentity.IsSameOrInside(
                destinationRoot,
                destinationResolution.BoundaryPath,
                destinationResolution.Semantics))
        {
            _logger.LogWarning(
                "Skipping companion-file import because destination root {DestinationRoot} escaped its authorized filesystem boundary",
                destinationRoot);
            return 0;
        }

        var importedCount = 0;
        foreach (var companionFile in companionFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var sourceCapability = await _filePublicationSourceCapability.CheckAsync(
                    companionFile,
                    cancellationToken);
                if (!sourceCapability.IsSupported
                    || !sourceCapability.SourceProof.HasValue)
                {
                    _logger.LogWarning(
                        "Skipping companion file {FilePath} before destination creation because source publication capability is unavailable: {Reason}",
                        companionFile,
                        LogRedaction.SanitizeText(sourceCapability.Reason));
                    continue;
                }
                var sourceProof = sourceCapability.SourceProof.Value;

                var isAudioCompanion = FileUtils.IsAudioFile(companionFile);
                if (isAudioCompanion)
                {
                    var profile = await BuildAudioMatchProfileAsync(companionFile);
                    if (profile == null || !FileUtils.LikelyMatchesAnyReference(profile, selectedAudioProfiles))
                    {
                        _logger.LogInformation(
                            "Skipping unmatched audio companion file {FilePath} during manual import because it does not match the selected audiobook batch",
                            companionFile);
                        continue;
                    }
                }

                if (!TryResolveCompanionDestination(
                        sourceRootPath,
                        destinationRoot,
                        companionFile,
                        results,
                        sourceSemantics,
                        destinationResolution.Semantics,
                        out var destinationPath))
                {
                    _logger.LogWarning(
                        "Skipping companion file {FilePath} because no contained destination could be resolved",
                        companionFile);
                    continue;
                }

                var destinationReservation = await destinationTracker.PlanUniqueAsync(
                    destinationPath,
                    destinationResolution,
                    cancellationToken);
                destinationPath = destinationReservation.Path;
                var operationId = FileMoveOperationIdentity.CreateForPaths(
                    "manual-import-companion",
                    audiobookIds[0],
                    action,
                    companionFile,
                    sourceSemantics,
                    sourceProof,
                    destinationPath,
                    destinationResolution.Semantics);
                var publicationPlan = _filePublicationCapabilityResolver == null
                    ? sourceProof.HasDurablePhysicalObjectIdentity
                        ? FilePublicationPlan.Durable(action)
                        : FilePublicationPlan.Additive(action)
                    : await _filePublicationCapabilityResolver.ResolveAsync(
                        action,
                        companionFile,
                        destinationPath,
                        sourceProof,
                        cancellationToken);
                if (!publicationPlan.IsAllowed)
                {
                    _logger.LogWarning(
                        "Skipping companion file {FilePath}: {Reason}",
                        companionFile,
                        LogRedaction.SanitizeText(publicationPlan.Message));
                    continue;
                }

                var destinationDirectory = Path.GetDirectoryName(destinationPath)
                    ?? throw new InvalidOperationException(
                        "The companion import destination has no parent directory.");
                AudiobookFileOwnershipCheckResult? ownership = null;
                if (_audiobookFileService != null && targetAudiobook != null)
                {
                    ownership = await _audiobookFileService.CheckAudiobookFileOwnershipAsync(
                        targetAudiobook,
                        destinationPath,
                        destinationDirectory,
                        cancellationToken);
                    if (ownership.Outcome is not (
                            AudiobookFileOwnershipCheckOutcome.Available or
                            AudiobookFileOwnershipCheckOutcome.AlreadyOwnedByAudiobook))
                    {
                        _logger.LogWarning(
                            "Skipping companion file {FilePath} because destination {DestinationPath} is not available: {Outcome}. {Reason}",
                            companionFile,
                            destinationPath,
                            ownership.Outcome,
                            ownership.Reason);
                        continue;
                    }
                }
                else
                {
                    _logger.LogWarning(
                        "Skipping companion file {FilePath} because destination ownership cannot be verified",
                        companionFile);
                    continue;
                }

                if (publicationPlan.Mode
                    == FilePublicationExecutionMode.AdditiveCopyRetainSource)
                {
                    await _directoryOwnershipStore.EnsureAdditiveHierarchyAsync(
                        destinationDirectory,
                        destinationRoot,
                        destinationResolution.Semantics,
                        cancellationToken);
                }
                else
                {
                    await _directoryOwnershipStore.EnsureCreatedHierarchyAsync(
                        destinationDirectory,
                        destinationRoot,
                        destinationResolution.Semantics,
                        "manual-import-companion",
                        operationId,
                        audiobookIds[0],
                        cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                var success = isAudioCompanion
                    ? await PublishAndRegisterAudioCompanionAsync(
                        publicationPlan,
                        companionFile,
                        destinationPath,
                        operationId,
                        sourceProof,
                        targetAudiobook!,
                        ownership!,
                        cancellationToken)
                    : await PublishUnregisteredCompanionAsync(
                        publicationPlan,
                        companionFile,
                        destinationPath,
                        operationId,
                        sourceProof,
                        audiobookIds[0]);
                if (success)
                {
                    destinationTracker.Commit(destinationReservation);
                    importedCount++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to import companion file {FilePath} during manual import", companionFile);
            }
        }

        return importedCount;
    }

    private static bool TryResolveCompanionDestination(
        string sourceRootPath,
        string destinationRoot,
        string companionFile,
        IReadOnlyCollection<ManualImportResultDto> results,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics destinationSemantics,
        out string destinationPath)
    {
        var sourceRoot = FileSystemPathIdentity.ResolveNativeAbsolutePath(sourceRootPath);
        var companion = FileSystemPathIdentity.ResolveNativeAbsolutePath(companionFile);
        var destination = FileSystemPathIdentity.ResolveNativeAbsolutePath(destinationRoot);
        if (FileSystemPathIdentity.TryGetRelativePathWithinBase(
                sourceRoot,
                companion,
                sourceSemantics,
                out var relativePath)
            && FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                destination,
                relativePath,
                destinationSemantics,
                out destinationPath))
        {
            return true;
        }

        var companionDirectory = Path.GetDirectoryName(companion);
        if (string.IsNullOrWhiteSpace(companionDirectory))
        {
            destinationPath = string.Empty;
            return false;
        }

        var matchingImport = results.FirstOrDefault(result =>
        {
            if (!result.Success
                || string.IsNullOrWhiteSpace(result.SourcePath)
                || string.IsNullOrWhiteSpace(result.DestinationPath))
            {
                return false;
            }

            var importedSourceDirectory = Path.GetDirectoryName(
                FileSystemPathIdentity.ResolveNativeAbsolutePath(result.SourcePath));
            return importedSourceDirectory != null
                && sourceSemantics.Comparer.Equals(importedSourceDirectory, companionDirectory);
        });
        var importedDestinationDirectory = matchingImport?.DestinationPath == null
            ? null
            : Path.GetDirectoryName(
                FileSystemPathIdentity.ResolveNativeAbsolutePath(
                    matchingImport.DestinationPath));
        if (string.IsNullOrWhiteSpace(importedDestinationDirectory)
            || !FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                importedDestinationDirectory,
                Path.GetFileName(companion),
                destinationSemantics,
                out destinationPath))
        {
            destinationPath = string.Empty;
            return false;
        }

        return true;
    }

    private async Task<FileUtils.AudioMatchProfile?> BuildAudioMatchProfileAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        AudioMetadata? metadata = null;
        try
        {
            metadata = await _metadataService.ExtractFileMetadataAsync(filePath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogDebug(ex, "Failed to extract metadata while classifying manual-import companion file {FilePath}", filePath);
        }

        return FileUtils.CreateAudioMatchProfile(filePath, metadata);
    }
}
