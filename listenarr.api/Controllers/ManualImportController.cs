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
using Microsoft.AspNetCore.Mvc;
using Listenarr.Domain.Utils;
using static Listenarr.Api.Services.FileMover;
using Listenarr.Api.Services.Metadata;

namespace Listenarr.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/library/manual-import")]
[Tags("Library")]
public class ManualImportController : ControllerBase
{
    private readonly ILogger<ManualImportController> _logger;
    private readonly IAudiobookRepository _audiobookRepository;
    private readonly IMetadataService _metadataService;
    private readonly IFileNamingService _fileNamingService;
    private readonly IConfigurationService _configService;
    private readonly IScanQueueService _scanQueueService;
    private readonly IRootFolderService _rootFolderService;
    private readonly IFileMover _fileMover;

    public ManualImportController(
        ILogger<ManualImportController> logger,
        IAudiobookRepository audiobookRepository,
        IMetadataService metadataService,
        IFileNamingService fileNamingService,
        IConfigurationService configService,
        IScanQueueService scanQueueService,
        IRootFolderService rootFolderService,
        IFileMover fileMover)
    {
        _logger = logger;
        _audiobookRepository = audiobookRepository;
        _metadataService = metadataService;
        _fileNamingService = fileNamingService;
        _configService = configService;
        _scanQueueService = scanQueueService;
        _rootFolderService = rootFolderService;
        _fileMover = fileMover;
    }

    /// <summary>
    /// Preview the files available for manual import from a directory.
    /// </summary>
    /// <param name="path">Absolute path to the directory to scan.</param>
    /// <returns>List of files with relative paths, sizes, and tentative metadata.</returns>
    [HttpGet("preview")]
    public async Task<ActionResult<object>> Preview([FromQuery] string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return BadRequest(new { error = "Path is required" });

            var normalized = Path.GetFullPath(path);
            if (!Directory.Exists(normalized)) return NotFound(new { error = "Directory not found" });

            var settings = await _configService.GetApplicationSettingsAsync();

            var files = Directory.EnumerateFiles(normalized, "*.*", SearchOption.AllDirectories)
                .Where(f => !FileUtils.IsBlacklistedFile(f, settings.ImportBlacklistExtensions))
                .Select(f => new
                {
                    relativePath = Path.GetRelativePath(normalized, f),
                    fullPath = f,
                    size = new FileInfo(f).Length,
                    // Simple heuristics for sample metadata
                    series = (string?)null,
                    season = (string?)null,
                    episodes = (string?)null,
                    quality = (string?)null,
                    languages = new string[] { "English" },
                    releaseType = "Unknown"
                })
                .ToList();

            var items = files.Select(f => new
            {
                relativePath = f.relativePath,
                fullPath = f.fullPath,
                size = FormatSize(f.size),
                series = f.series,
                season = f.season,
                episodes = f.episodes,
                quality = f.quality,
                languages = f.languages,
                releaseType = f.releaseType
            }).ToList();

            return Ok(new { items });
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogError(ex, "Error previewing manual import for path {Path}", path);
            return StatusCode(500, new { error = "Failed to preview import" });
        }
    }

    /// <summary>
    /// Given a list of items, tries to import them all into the library
    /// </summary>
    /// <param name="request">Import configuration including source path, mode, import action (do nothing/copy/move/...), and selected file items.</param>
    /// <returns>Summary of imported files with success/failure details per item.</returns>
    [HttpPost]
    public async Task<ActionResult<object>> Start([FromBody] ManualImportRequestDto request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Path))
        {
            return BadRequest(new { error = "Invalid request" });
        }

        var sourceDirectory = Path.GetFullPath(request.Path);
        if (!Directory.Exists(sourceDirectory))
        {
            return NotFound(new { error = "Directory not found" });
        }

        if (request.Items == null || !request.Items.Any())
        {
            return BadRequest(new { error = "No items to import" });
        }

        var results = new List<ManualImportResultDto>();
        // Track destination paths used within this batch so we avoid collisions between items
        var usedDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // Fetch root folders once for the whole batch (used for path containment validation)
            var rootFolders = await _rootFolderService.GetAllAsync();
            var appSettings = await _configService.GetApplicationSettingsAsync() ?? new ApplicationSettings();
            var importBlacklist = appSettings.ImportBlacklistExtensions;
            var orderedItems = BuildOrderedItems(request.Items);
            var selectedAudioProfiles = request.IncludeCompanionFiles
                ? await BuildAudioMatchProfilesAsync(
                    orderedItems
                        .Where(item => !string.IsNullOrWhiteSpace(item.FullPath))
                        .Select(item => item.FullPath!)
                        .Where(FileUtils.IsAudioFile))
                : Array.Empty<FileUtils.AudioMatchProfile>();

            _logger.LogDebug("Manual import batch: {ItemCount} items", orderedItems.Count);

            foreach (var item in orderedItems)
            {
                var fileCount = orderedItems.Count(f => f.MatchedAudiobookId == item.MatchedAudiobookId);
                _logger.LogDebug("Importing item {Index}: {Path} for audiobook {AudiobookId}, fileCount: {FileCount}", orderedItems.IndexOf(item), item.FullPath, item.MatchedAudiobookId, fileCount);
                var result = await ImportFileAsync(item, request.Action, sourceDirectory, usedDestinations, rootFolders, appSettings, fileCount > 1);
                _logger.LogDebug("Import result {Index}: Success={Success}, Destination={Destination}, Error={Error}", orderedItems.IndexOf(item), result.Success, result.DestinationPath, result.Error);
                results.Add(result);
            }

            if (request.IncludeCompanionFiles && request.Action != FileAction.None)
            {
                var companionImportCount = await ImportCompanionFilesAsync(
                    request,
                    orderedItems,
                    results,
                    sourceDirectory,
                    selectedAudioProfiles,
                    usedDestinations,
                    appSettings.ImportBlacklistExtensions);
                _logger.LogInformation("Manual import companion-file pass completed with {Count} imported companion file(s)", companionImportCount);
            }

            if (request.CleanupEmptySourceFolders)
            {
                FileUtils.DeleteEmptyDirectories(sourceDirectory);
            }

            await EnqueueFocusedScansAsync(results);

            var successCount = results.Count(r => r.Success);
            _logger.LogInformation("Manual import batch completed: {SuccessCount}/{TotalCount} succeeded, usedDestinations: {DestinationCount}", successCount, results.Count, usedDestinations.Count);
            return Ok(new
            {
                importedCount = successCount,
                totalCount = results.Count,
                results = results
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogError(ex, "Error starting manual import");
            return StatusCode(500, new { error = "Failed to start import" });
        }
    }

    /// <summary>
    /// Import the file into the library
    /// </summary>
    /// <param name="item">File to import into the library</param>
    /// <param name="action">Action to perform on the file</param>
    /// <param name="sourceDirectory">Directory from which we are importing the file</param>
    /// <param name="usedDestinations">Already used file names to avoid collisions</param>
    /// <param name="rootFolders">Previously fetched list of configured root folders (to save DB hits)</param>
    /// <param name="settings">Application settings (to save DB hits)</param>
    /// <param name="hasMultipleFile">Indicates if this file is part of multiple files for a same audiobook</param>
    /// <returns>Result of the importation</returns>
    /// <exception cref="IOException"></exception>
    private async Task<ManualImportResultDto> ImportFileAsync(
        ManualImportItemDto item,
        FileAction action,
        string sourceDirectory,
        HashSet<string> usedDestinations,
        List<RootFolder> rootFolders,
        ApplicationSettings settings,
        bool hasMultipleFile = false)
    {
        try
        {
            // Validate FullPath
            if (string.IsNullOrWhiteSpace(item.FullPath))
            {
                return ManualImportResultDto.FailureResult("FullPath is required", item.FullPath);
            }

            // Get the associated audiobook
            var audiobook = await _audiobookRepository.GetByIdAsync(item.MatchedAudiobookId);
            if (audiobook == null)
            {
                return ManualImportResultDto.FailureResult($"Audiobook with ID {item.MatchedAudiobookId} not found", item.FullPath);
            }

            // Check if source file exists
            if (!System.IO.File.Exists(item.FullPath))
            {
                return ManualImportResultDto.FailureResult("Source file not found", item.FullPath);
            }

            // Validate source is within a configured root folder (prevents path traversal)
            var isUnderSourceDirectory = FileUtils.IsPathInsideOf(item.FullPath, sourceDirectory);

            var isUnderConfiguredRoot = rootFolders.Any(r => FileUtils.IsPathInsideOf(item.FullPath, r.Path));

            if (!isUnderSourceDirectory && !isUnderConfiguredRoot)
            {
                _logger.LogWarning("Rejected manual import: {Path} is not within the requested path or a configured root folder", item.FullPath);
                return ManualImportResultDto.FailureResult("Source file is not within the requested import path or a configured root folder", item.FullPath);
            }

            // Check if audiobook has a base path
            if (string.IsNullOrWhiteSpace(audiobook.BasePath))
            {
                audiobook.BasePath = Path.GetDirectoryName(item.FullPath);
                await PersistAudiobookBasePathAsync(audiobook, audiobook.BasePath);
            }

            // Extract metadata from the file
            var metadata = await _metadataService.ExtractFileMetadataAsync(item.FullPath);
            if (metadata == null)
            {
                return ManualImportResultDto.FailureResult("Failed to extract metadata from file", item.FullPath);
            }

            var destinationPath = item.FullPath;
            if (action != FileAction.None)
            {
                // Generate destination path using appropriate naming pattern
                destinationPath = await GenerateManualImportPathAsync(audiobook, metadata, item, rootFolders, settings, hasMultipleFile);

                await _fileMover.PerformActionOn(action, item.FullPath, destinationPath, usedDestinations);

                // Write ASIN to embedded file tags (non-critical — failure is logged, not thrown)
                if (!string.IsNullOrWhiteSpace(audiobook.Asin))
                    await _metadataService.WriteAsinTagAsync(destinationPath, audiobook.Asin);
            }

            return new ManualImportResultDto
            {
                Success = true,
                SourcePath = item.FullPath,
                DestinationPath = destinationPath,
                Audiobook = audiobook
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogError(ex, "Error importing file {FilePath}", item.FullPath);
            return ManualImportResultDto.FailureResult(ex.Message, item.FullPath);
        }
    }

    /// <summary>
    /// Order a scan for each audiobook impacted by the importation and update audiobook base path
    /// </summary>
    /// <param name="results">List of imported files</param>
    private async Task EnqueueFocusedScansAsync(IEnumerable<ManualImportResultDto> results)
    {
        if (_scanQueueService == null)
        {
            _logger.LogDebug("IScanQueueService not available - skipping focused scan enqueue after manual import");
            return;
        }

        var groupedResults = results
            .Where(r => r.Success && r.Audiobook != null && !string.IsNullOrWhiteSpace(r.DestinationPath))
            .GroupBy(r => r.Audiobook!.Id);

        foreach (var group in groupedResults)
        {
            var scanPath = DetermineScanPath(group
                .Select(r => r.DestinationPath!)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList());

            if (string.IsNullOrWhiteSpace(scanPath))
            {
                _logger.LogDebug("No focused scan path could be determined for audiobook {AudiobookId} after manual import", group.Key);
                continue;
            }

            var audiobook = group.First().Audiobook!;
            await PersistAudiobookBasePathAsync(audiobook, scanPath);

            try
            {
                var scanJobId = await _scanQueueService.EnqueueScanAsync(audiobook, scanPath);
                _logger.LogInformation(
                    "Enqueued focused scan {ScanJobId} for audiobook {AudiobookId} (path: {Path}) after manual import batch of {FileCount} file(s)",
                    scanJobId,
                    group.Key,
                    scanPath,
                    group.Count());
            }
            catch (ObjectDisposedException ex)
            {
                _logger.LogWarning(ex, "Failed to enqueue scan for audiobook {AudiobookId} after manual import", group.Key);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Failed to enqueue scan for audiobook {AudiobookId} after manual import", group.Key);
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogWarning(ex, "Failed to enqueue scan for audiobook {AudiobookId} after manual import", group.Key);
            }
        }
    }

    private async Task PersistAudiobookBasePathAsync(Audiobook audiobook, string? basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return;
        }

        try
        {
            basePath = FileUtils.NormalizeStoredPath(basePath);
            if (System.IO.File.Exists(basePath))
            {
                basePath = Path.GetDirectoryName(basePath);
            }
            if (!string.IsNullOrWhiteSpace(basePath) && !string.Equals(audiobook.BasePath, basePath, StringComparison.Ordinal))
            {
                audiobook.BasePath = basePath;
                await _audiobookRepository.UpdateAsync(audiobook);
                _logger.LogInformation(
                    "Updated audiobook {AudiobookId} BasePath to {BasePath}",
                    audiobook.Id,
                    basePath);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogWarning(ex, $"Failed to persist {basePath} for audiobook {audiobook.Id}");
        }
    }

    private static string? DetermineScanPath(IReadOnlyList<string> destinationPaths)
    {
        return FileUtils.GetCommonDirectory(destinationPaths);
    }

    /// <summary>
    /// Generate the path where the file should be imported
    /// </summary>
    /// <param name="audiobook">Audiobook related to the imported file</param>
    /// <param name="metadata">Metadata related to the imported file</param>
    /// <param name="item">File to import into the library</param>
    /// <param name="rootFolders">Previously fetched list of configured root folders (to save DB hits)</param>
    /// <param name="settings">Application settings (to save DB hits)</param>
    /// <param name="isMultiFile">Does the original import operation contained multiple files for this audiobook ?</param>
    /// <returns>Path where we should put the file</returns>
    private async Task<string> GenerateManualImportPathAsync(Audiobook audiobook, AudioMetadata metadata, ManualImportItemDto item, List<RootFolder> rootFolders, ApplicationSettings settings, bool isMultiFile = false)
    {
        var sourceFilePath = item.FullPath ?? string.Empty;
        // Get the configured folder/file naming patterns from settings
        var folderPattern = settings.FolderNamingPattern;
        var filePattern = isMultiFile ? settings.MultiFileNamingPattern : settings.FileNamingPattern;

        // If a custom BasePath is set (different from configured OutputPath AND not a known
        // root folder), store directly under that path using file-only naming.
        // If BasePath IS a configured root folder, treat it as a library destination and
        // apply the full folder+file naming pattern so files are properly organised.
        var basePath = string.IsNullOrWhiteSpace(audiobook.BasePath)
            ? string.Empty
            : FileUtils.NormalizeStoredPath(audiobook.BasePath);
        var configuredOutput = settings.OutputPath ?? string.Empty;
        var isCustomBasePath = false;
        try
        {
            if (!string.IsNullOrWhiteSpace(basePath))
            {
                var baseFull = FileUtils.NormalizeStoredPath(basePath);
                var configuredFull = string.IsNullOrWhiteSpace(configuredOutput) ? string.Empty : Path.GetFullPath(configuredOutput);
                isCustomBasePath = !string.Equals(baseFull, configuredFull, StringComparison.OrdinalIgnoreCase);

                // Even if it differs from OutputPath, don't treat it as custom when it
                // matches a configured root folder — those are all valid library destinations.
                if (isCustomBasePath)
                {
                    var isRootFolder = rootFolders.Any(r =>
                    {
                        try { return string.Equals(FileUtils.NormalizeStoredPath(r.Path), baseFull, StringComparison.OrdinalIgnoreCase); }
                        catch (ArgumentException) { return false; }
                        catch (NotSupportedException) { return false; }
                        catch (PathTooLongException) { return false; }
                    });
                    if (isRootFolder) isCustomBasePath = false;
                }
            }
        }
        catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException)
        {
            isCustomBasePath = !string.IsNullOrWhiteSpace(basePath) && !string.IsNullOrWhiteSpace(configuredOutput)
                && !string.Equals(basePath, configuredOutput, StringComparison.OrdinalIgnoreCase);
        }

        // Get the file extension from the source file (preserve original extension)
        var extension = Path.GetExtension(sourceFilePath).ToLowerInvariant();
        if (string.IsNullOrEmpty(extension))
        {
            extension = ".m4b"; // Fallback if no extension
        }

        // Build variables for the pattern - only include non-empty values
        var variables = new Dictionary<string, object>();

        // Get first author from Authors list
        var author = audiobook.Authors?.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(author))
            variables["Author"] = author;

        var narrator = audiobook.Narrators != null
            ? string.Join(", ", audiobook.Narrators.Where(n => !string.IsNullOrWhiteSpace(n)))
            : string.Empty;
        if (!string.IsNullOrWhiteSpace(narrator))
            variables["Narrator"] = narrator;

        if (!string.IsNullOrWhiteSpace(audiobook.Publisher))
            variables["Publisher"] = audiobook.Publisher;

        if (!string.IsNullOrWhiteSpace(audiobook.Language))
            variables["Language"] = audiobook.Language;

        if (!string.IsNullOrWhiteSpace(audiobook.Asin))
            variables["Asin"] = audiobook.Asin;

        if (!string.IsNullOrWhiteSpace(audiobook.Subtitle))
            variables["Subtitle"] = audiobook.Subtitle;

        if (!string.IsNullOrWhiteSpace(audiobook.Edition))
            variables["Edition"] = audiobook.Edition;

        // Preserve the older title+subtitle uniqueness behavior unless the user explicitly uses {Subtitle}.
        // (e.g. "The Land" + "Founding" → "The Land: Founding")
        var usesSubtitleToken = (!string.IsNullOrWhiteSpace(folderPattern) && folderPattern.IndexOf("Subtitle", StringComparison.OrdinalIgnoreCase) >= 0)
            || (!string.IsNullOrWhiteSpace(filePattern) && filePattern.IndexOf("Subtitle", StringComparison.OrdinalIgnoreCase) >= 0);

        var titleFull = !usesSubtitleToken
            && !string.IsNullOrWhiteSpace(audiobook.Subtitle)
            && !string.IsNullOrWhiteSpace(audiobook.Title)
            && !audiobook.Title.Contains(audiobook.Subtitle, StringComparison.OrdinalIgnoreCase)
            ? $"{audiobook.Title}: {audiobook.Subtitle}"
            : audiobook.Title;
        variables["Title"] = !string.IsNullOrWhiteSpace(titleFull)
            ? titleFull
            : "Unknown Title"; // Title is required as fallback

        if (!string.IsNullOrWhiteSpace(audiobook.Series))
            variables["Series"] = audiobook.Series;

        if (!string.IsNullOrWhiteSpace(audiobook.PublishYear))
            variables["Year"] = audiobook.PublishYear;

        var effectiveDiskNumber = item.DiskNumberHint
            ?? (metadata.DiscNumber.HasValue && metadata.DiscNumber.Value > 0 ? metadata.DiscNumber.Value : null);
        var effectiveChapterNumber = item.ChapterNumberHint
            ?? (metadata.TrackNumber.HasValue && metadata.TrackNumber.Value > 0 ? metadata.TrackNumber.Value : null);

        if (isMultiFile)
        {
            effectiveDiskNumber ??= effectiveChapterNumber;
            effectiveChapterNumber ??= effectiveDiskNumber;
        }

        if (effectiveDiskNumber.HasValue && effectiveDiskNumber.Value > 0)
            variables["DiskNumber"] = effectiveDiskNumber.Value;

        if (effectiveChapterNumber.HasValue && effectiveChapterNumber.Value > 0)
            variables["ChapterNumber"] = effectiveChapterNumber.Value;

        var stableSuffixNumber = effectiveChapterNumber ?? effectiveDiskNumber ?? item.SequenceNumberHint;

        string relativePath;
        var patternHasNumberTokens = !string.IsNullOrWhiteSpace(filePattern)
            && (filePattern.IndexOf("DiskNumber", StringComparison.OrdinalIgnoreCase) >= 0
                || filePattern.IndexOf("ChapterNumber", StringComparison.OrdinalIgnoreCase) >= 0);

        if (string.IsNullOrWhiteSpace(folderPattern))
        {
            // Legacy behavior: use FileNamingPattern as the full relative path pattern
            var legacyPattern = string.IsNullOrWhiteSpace(filePattern)
                ? "{Author}/{Title}/{Title}"
                : filePattern;

            relativePath = _fileNamingService.ApplyNamingPattern(legacyPattern, variables, treatAsFilename: false);
        }
        else if (isCustomBasePath)
        {
            // Custom base path: only apply file naming pattern, not folder pattern
            // (the BasePath already represents the folder location)
            var effectiveFilePattern = string.IsNullOrWhiteSpace(filePattern) ? "{Title}" : filePattern;

            var patternAllowsSubfolders = effectiveFilePattern.IndexOf("DiskNumber", StringComparison.OrdinalIgnoreCase) >= 0
                || effectiveFilePattern.IndexOf("ChapterNumber", StringComparison.OrdinalIgnoreCase) >= 0
                || effectiveFilePattern.IndexOf('/') >= 0
                || effectiveFilePattern.IndexOf('\\') >= 0;

            relativePath = _fileNamingService.ApplyNamingPattern(effectiveFilePattern, variables, treatAsFilename: !patternAllowsSubfolders);
        }
        else
        {
            // New behavior: separate folder and file patterns
            var effectiveFilePattern = string.IsNullOrWhiteSpace(filePattern) ? "{Title}" : filePattern;

            var folderRelative = _fileNamingService.ApplyNamingPattern(folderPattern, variables, treatAsFilename: false);

            var patternAllowsSubfolders = effectiveFilePattern.IndexOf("DiskNumber", StringComparison.OrdinalIgnoreCase) >= 0
                || effectiveFilePattern.IndexOf("ChapterNumber", StringComparison.OrdinalIgnoreCase) >= 0
                || effectiveFilePattern.IndexOf('/') >= 0
                || effectiveFilePattern.IndexOf('\\') >= 0;

            var fileRelative = _fileNamingService.ApplyNamingPattern(effectiveFilePattern, variables, treatAsFilename: !patternAllowsSubfolders);

            if (isMultiFile && !patternHasNumberTokens && stableSuffixNumber.HasValue)
                fileRelative = FileUtils.AppendSequenceSuffix(fileRelative, stableSuffixNumber.Value);

            relativePath = string.IsNullOrWhiteSpace(folderRelative)
                ? fileRelative
                : CombineWithOptionalBase(folderRelative, fileRelative);
        }

        if ((string.IsNullOrWhiteSpace(folderPattern) || isCustomBasePath)
            && isMultiFile
            && !patternHasNumberTokens
            && stableSuffixNumber.HasValue)
        {
            relativePath = FileUtils.AppendSequenceSuffix(relativePath, stableSuffixNumber.Value);
        }

        // Ensure it has the correct extension
        if (!relativePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            relativePath += extension;
        }

        return string.IsNullOrWhiteSpace(basePath)
            ? relativePath
            : CombineWithOptionalBase(basePath, relativePath);
    }

    private static string CombineWithOptionalBase(string? basePath, string candidatePath)
    {
        var normalizedPath = candidatePath.Trim();

        if (string.IsNullOrEmpty(normalizedPath))
        {
            return normalizedPath;
        }

        if (Path.IsPathRooted(normalizedPath) || string.IsNullOrWhiteSpace(basePath))
        {
            return normalizedPath;
        }

        var relativePath = normalizedPath.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Path.IsPathRooted(relativePath))
        {
            return relativePath;
        }

        var normalizedBasePath = basePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.IsNullOrEmpty(normalizedBasePath)
            ? relativePath
            : normalizedBasePath + Path.DirectorySeparatorChar + relativePath;
    }

    private static List<ManualImportItemDto> BuildOrderedItems(IEnumerable<ManualImportItemDto> items)
    {
        var ordered = new List<ManualImportItemDto>();

        foreach (var validItems in items.GroupBy(i => i.MatchedAudiobookId).Select(g => g.Where(i => !string.IsNullOrWhiteSpace(i.FullPath)).ToList()))
        {
            if (validItems.Count == 0)
            {
                continue;
            }

            var plans = MultiFileImportPlanner.BuildPlans(validItems.Select(i => (i.FullPath!, string.IsNullOrWhiteSpace(i.RelativePath) ? null : i.RelativePath)));
            var itemLookup = validItems.ToDictionary(i => i.FullPath!, StringComparer.OrdinalIgnoreCase);
            var diskNumbersForNaming = MultiFileImportPlanner.BuildStableNamingNumbers(plans, p => p.DiskNumberHint);
            var chapterNumbersForNaming = MultiFileImportPlanner.BuildStableNamingNumbers(plans, p => p.ChapterNumberHint);

            ordered.AddRange(plans
                .Select(plan =>
                {
                    if (!itemLookup.TryGetValue(plan.FullPath, out var item))
                    {
                        return null;
                    }

                    item.SequenceNumberHint = plan.SequenceNumber;
                    item.DiskNumberHint = diskNumbersForNaming.TryGetValue(plan.FullPath, out var diskNumber) ? diskNumber : plan.DiskNumberHint;
                    item.ChapterNumberHint = chapterNumbersForNaming.TryGetValue(plan.FullPath, out var chapterNumber) ? chapterNumber : plan.ChapterNumberHint;
                    return item;
                })
                .Where(item => item != null)!
                .Cast<ManualImportItemDto>());
        }

        foreach (var invalidItem in items.Where(i => string.IsNullOrWhiteSpace(i.FullPath)))
        {
            ordered.Add(invalidItem);
        }

        return ordered;
    }

    /// <summary>
    /// Allows to copy files that are contained in a directory from which we already imported files
    /// </summary>
    /// <param name="request"></param>
    /// <param name="orderedItems"></param>
    /// <param name="results"></param>
    /// <param name="sourceRootPath"></param>
    /// <param name="selectedAudioProfiles"></param>
    /// <param name="unavailableFilenames">Filenames that have already been reserved for operations from this batch</param>
    /// <param name="importBlacklist"></param>
    /// <returns></returns>
    private async Task<int> ImportCompanionFilesAsync(
        ManualImportRequestDto request,
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

        var destinationRoot = DetermineScanPath(results
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

        // Only scan for companion files in directories that actually contain
        // the selected import files. Previously, the entire sourceRootPath was
        // scanned recursively which could copy unrelated files when the source
        // root is a broad directory like a general downloads folder.
        var selectedDirectories = selectedSourceFiles
            .Select(f => Path.GetDirectoryName(f))
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var companionFiles = selectedDirectories
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir!, "*", SearchOption.TopDirectoryOnly))
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

                var destinationPath = CombineWithOptionalBase(destinationRoot, relativePath);

                await _fileMover.PerformActionOn(request.Action, companionFile, destinationPath, unavailableFilenames);

                importedCount++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to import companion file {FilePath} during manual import", companionFile);
            }
        }

        return importedCount;
    }

    private async Task<IReadOnlyCollection<FileUtils.AudioMatchProfile>> BuildAudioMatchProfilesAsync(IEnumerable<string> filePaths)
    {
        return (await Task.WhenAll(filePaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(BuildAudioMatchProfileAsync)))
            .Where(profile => profile != null)
            .Cast<FileUtils.AudioMatchProfile>()
            .ToList();
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

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        var units = new[] { "KiB", "MiB", "GiB", "TiB" };
        double size = bytes / 1024.0;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024.0;
            unit++;
        }
        return $"{size:F1} {units[unit]}";
    }
}
