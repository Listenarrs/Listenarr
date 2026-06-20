/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Api.Dtos.ManualImport;
using Listenarr.Domain.Common;

namespace Listenarr.Api.Features.Downloads;

public sealed class ManualImportCompanionImporter
{
    private readonly IMetadataService _metadataService;
    private readonly IFileMover _fileMover;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<ManualImportCompanionImporter> _logger;

    public ManualImportCompanionImporter(
        IMetadataService metadataService,
        IFileMover fileMover,
        IFileSystem fileSystem,
        ILogger<ManualImportCompanionImporter> logger)
    {
        _metadataService = metadataService;
        _fileMover = fileMover;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    public async Task<IReadOnlyCollection<FileUtils.AudioMatchProfile>> BuildAudioMatchProfilesAsync(IEnumerable<string> filePaths)
    {
        return (await Task.WhenAll(filePaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(BuildAudioMatchProfileAsync)))
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
        HashSet<string> unavailableFilenames,
        IEnumerable<string> importBlacklist)
    {
        var audiobookIds = orderedItems
            .Select(item => item.MatchedAudiobookId)
            .Distinct()
            .ToList();

        if (audiobookIds.Count != 1)
        {
            _logger.LogDebug("Skipping companion-file import because the batch contains {Count} audiobook targets", audiobookIds.Count);
            return 0;
        }

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
            StringComparer.OrdinalIgnoreCase);

        var selectedDirectories = selectedSourceFiles
            .Select(Path.GetDirectoryName)
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var companionFiles = selectedDirectories
            .Where(directory => directory != null && _fileSystem.DirectoryExists(directory))
            .SelectMany(dir => _fileSystem.EnumerateFiles(dir!, "*", SearchOption.TopDirectoryOnly))
            .Where(file => !FileUtils.IsBlacklistedFile(file, importBlacklist))
            .Select(Path.GetFullPath)
            .Where(file => !selectedSourceFiles.Contains(file))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var importedCount = 0;
        foreach (var companionFile in companionFiles)
        {
            try
            {
                if (FileUtils.IsAudioFile(companionFile))
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

                var relativePath = Path.GetRelativePath(sourceRootPath, companionFile);
                if (relativePath.StartsWith("..", StringComparison.Ordinal))
                {
                    continue;
                }

                var destinationPath = ManualImportPathPlanner.CombineWithOptionalBase(destinationRoot, relativePath);

                var success = await _fileMover.PerformActionOn(action, companionFile, destinationPath);
                if (success)
                {
                    unavailableFilenames.Add(destinationPath);
                }

                importedCount++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to import companion file {FilePath} during manual import", companionFile);
            }
        }

        return importedCount;
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
