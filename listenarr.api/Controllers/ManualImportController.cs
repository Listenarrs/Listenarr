using Microsoft.AspNetCore.Mvc;
using Listenarr.Api.Services;
using System.Text.Json.Serialization;
using System.ComponentModel.DataAnnotations;

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
    private readonly IAudiobookshelfService _audiobookshelfService;
    private readonly IFileMover _fileMover;

    public ManualImportController(
        ILogger<ManualImportController> logger,
        IAudiobookRepository audiobookRepository,
        IMetadataService metadataService,
        IFileNamingService fileNamingService,
        IConfigurationService configService,
        IScanQueueService scanQueueService,
        IRootFolderService rootFolderService,
        IFileMover fileMover,
        IAudiobookshelfService audiobookshelfService)
    {
        _logger = logger;
        _audiobookRepository = audiobookRepository;
        _metadataService = metadataService;
        _fileNamingService = fileNamingService;
        _configService = configService;
        _scanQueueService = scanQueueService;
        _rootFolderService = rootFolderService;
        _fileMover = fileMover;
        _audiobookshelfService = audiobookshelfService;
    }

    /// <summary>
    /// Preview the files available for manual import from a directory.
    /// </summary>
    /// <param name="path">Absolute path to the directory to scan.</param>
    /// <returns>List of files with relative paths, sizes, and tentative metadata.</returns>
    [HttpGet("preview")]
    public ActionResult<object> Preview([FromQuery] string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return BadRequest(new { error = "Path is required" });

            var normalized = Path.GetFullPath(path);
            if (!Directory.Exists(normalized)) return NotFound(new { error = "Directory not found" });

            var files = Directory.EnumerateFiles(normalized, "*.*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
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
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
            _logger.LogError(ex, "Error previewing manual import for path {Path}", path);
            return StatusCode(500, new { error = "Failed to preview import" });
        }
    }

    private async Task<string?> ResolveAudiobookshelfLibraryIdAsync(IEnumerable<ManualImportResult> results)
{
    var successfulPaths = results
        .Where(r => r.Success && !string.IsNullOrWhiteSpace(r.DestinationPath))
        .Select(r => r.DestinationPath!)
        .ToList();

    if (!successfulPaths.Any())
        return null;

    var rootFolders = await _rootFolderService.GetAllAsync();

    foreach (var path in successfulPaths)
    {
        var fullPath = Path.GetFullPath(path);

        var matchingRoot = rootFolders.FirstOrDefault(r =>
        {
            try
            {
                return FileUtils.IsPathWithinRoot(fullPath, r.Path)
                    || string.Equals(fullPath, Path.GetFullPath(r.Path), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        });

        if (!string.IsNullOrWhiteSpace(matchingRoot?.AudiobookshelfLibraryId))
        {
            _logger.LogInformation(
                "Using root-folder-mapped Audiobookshelf library {LibraryId} for path {Path}",
                matchingRoot.AudiobookshelfLibraryId,
                path);

            return matchingRoot.AudiobookshelfLibraryId;
        }
    }

    // fallback to global setting
    var settings = await _configService.GetApplicationSettingsAsync();
    return settings?.AudiobookshelfLibraryId;
}

    /// <summary>
    /// Start a manual import operation, copying or moving selected files into the library.
    /// </summary>
    /// <param name="request">Import configuration including source path, mode, input mode (copy/move), and selected file items.</param>
    /// <returns>Summary of imported files with success/failure details per item.</returns>
    [HttpPost]
    public async Task<ActionResult<object>> Start([FromBody] ManualImportRequest request)
    {
        try
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Path))
                return BadRequest(new { error = "Invalid request" });

            var normalized = Path.GetFullPath(request.Path);
            if (!Directory.Exists(normalized))
                return NotFound(new { error = "Directory not found" });

            if (request.Mode == "automatic")
            {
                // TODO: Implement automatic import
                return BadRequest(new { error = "Automatic import not yet implemented" });
            }
            else if (request.Mode == "interactive" && request.Items != null && request.Items.Any())
            {
                var results = new List<ManualImportResult>();
                // Track destination paths used within this batch so we avoid collisions between items
                var usedDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // Fetch root folders once for the whole batch (used for path containment validation)
                var batchRootFolders = await _rootFolderService.GetAllAsync();
                var appSettings = await _configService.GetApplicationSettingsAsync() ?? new ApplicationSettings();
                var importBlacklist = FileUtils.NormalizeExtensions(appSettings.ImportBlacklistExtensions);
                var orderedItems = BuildOrderedItems(request.Items);
                var selectedAudioProfiles = request.IncludeCompanionFiles
                    ? await BuildAudioMatchProfilesAsync(
                        orderedItems
                            .Where(item => !string.IsNullOrWhiteSpace(item.FullPath))
                            .Select(item => item.FullPath!)
                            .Where(FileUtils.IsAudioFile))
                    : Array.Empty<FileUtils.AudioMatchProfile>();

                // Count files per audiobook to determine if multi-file import
                var filesPerAudiobook = orderedItems
                    .GroupBy(i => i.MatchedAudiobookId)
                    .ToDictionary(g => g.Key, g => g.Count());

                _logger.LogDebug("Manual import batch: {ItemCount} items, filesPerAudiobook: {AudiobookFileCount}", orderedItems.Count, string.Join(";", filesPerAudiobook.Select(x => $"{x.Key}:{x.Value}")));

                foreach (var item in orderedItems)
                {
                    var isMultiFile = filesPerAudiobook.TryGetValue(item.MatchedAudiobookId, out var count) && count > 1;
                    _logger.LogDebug("Importing item {Index}: {Path} for audiobook {AudiobookId}, isMultiFile: {IsMultiFile}", orderedItems.IndexOf(item), item.FullPath, item.MatchedAudiobookId, isMultiFile);
                    var result = await ImportFileAsync(item, request.InputMode ?? "copy", usedDestinations, isMultiFile, batchRootFolders, normalized);
                    _logger.LogDebug("Import result {Index}: Success={Success}, Destination={Destination}, Error={Error}", orderedItems.IndexOf(item), result.Success, result.DestinationPath, result.Error);
                    results.Add(result);
                }

                if (request.IncludeCompanionFiles)
                {
                    var companionImportCount = await ImportCompanionFilesAsync(
                        request,
                        orderedItems,
                        results,
                        normalized,
                        selectedAudioProfiles,
                        usedDestinations,
                        importBlacklist);
                    _logger.LogInformation("Manual import companion-file pass completed with {Count} imported companion file(s)", companionImportCount);
                }

                if (request.CleanupEmptySourceFolders && string.Equals(request.InputMode, "move", StringComparison.OrdinalIgnoreCase))
                {
                    FileUtils.DeleteEmptyDirectories(normalized);
                }

                await EnqueueFocusedScansAsync(results);

                var successCount = results.Count(r => r != null && r.Success);

                if (successCount > 0 &&
                    appSettings.AudiobookshelfEnabled &&
                    appSettings.AudiobookshelfScanAfterImport &&
                    appSettings.AudiobookshelfScanOnManualImport)
                {
                    try
                    {
                        var libraryId = await ResolveAudiobookshelfLibraryIdAsync(results);

                        if (!string.IsNullOrWhiteSpace(libraryId))
                        {
                            var (scanSuccess, scanMessage) =
                                await _audiobookshelfService.TriggerLibraryScanAsync(libraryId);

                            _logger.LogInformation(
                                "Triggered Audiobookshelf scan after manual import. LibraryId={LibraryId}, Success={Success}, Message={Message}",
                                libraryId,
                                scanSuccess,
                                scanMessage);
                        }
                        else
                        {
                            _logger.LogWarning("No Audiobookshelf library ID could be resolved for manual import scan");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to trigger Audiobookshelf scan after manual import");
                    }
                }

                _logger.LogInformation("Manual import batch completed: {SuccessCount}/{TotalCount} succeeded, usedDestinations: {DestinationCount}", successCount, results.Count, usedDestinations.Count);
                return Ok(new
                {
                    importedCount = successCount,
                    totalCount = results.Count,
                    results = results
                });
            }

            return BadRequest(new { error = "No items to import" });
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
            _logger.LogError(ex, "Error starting manual import");
            return StatusCode(500, new { error = "Failed to start import" });
        }
    }

    private async Task<ManualImportResult> ImportFileAsync(ManualImportItem item, string inputMode, HashSet<string>? usedDestinations = null, bool isMultiFile = false, IEnumerable<Listenarr.Domain.Models.RootFolder>? rootFolders = null, string? sourceRootPath = null)
    {
        try
        {
            // Validate FullPath
            if (string.IsNullOrWhiteSpace(item.FullPath))
            {
                return new ManualImportResult
                {
                    Success = false,
                    Error = "FullPath is required",
                    FilePath = item.FullPath
                };
            }

            // Get the associated audiobook
            var audiobook = await _audiobookRepository.GetByIdAsync(item.MatchedAudiobookId);
            if (audiobook == null)
            {
                return new ManualImportResult
                {
                    Success = false,
                    Error = $"Audiobook with ID {item.MatchedAudiobookId} not found",
                    FilePath = item.FullPath
                };
            }

            // Check if source file exists
            if (!System.IO.File.Exists(item.FullPath))
            {
                return new ManualImportResult
                {
                    Success = false,
                    Error = "Source file not found",
                    FilePath = item.FullPath
                };
            }

            // Validate source is within a configured root folder (prevents path traversal)
            var normalizedSource = Path.GetFullPath(item.FullPath);
            var allRootFolders = rootFolders ?? await _rootFolderService.GetAllAsync();
            var isUnderRequestedRoot = !string.IsNullOrWhiteSpace(sourceRootPath)
                && (string.Equals(normalizedSource, Path.GetFullPath(sourceRootPath), StringComparison.OrdinalIgnoreCase)
                    || FileUtils.IsPathWithinRoot(normalizedSource, sourceRootPath));
            var isUnderConfiguredRoot = allRootFolders.Any(r =>
            {
                try
                {
                    return FileUtils.IsPathWithinRoot(normalizedSource, r.Path)
                        || string.Equals(normalizedSource, Path.GetFullPath(r.Path), StringComparison.OrdinalIgnoreCase);
                }
                catch (ArgumentException) { return false; }
                catch (NotSupportedException) { return false; }
                catch (PathTooLongException) { return false; }
            });
            if (!isUnderRequestedRoot && !isUnderConfiguredRoot)
            {
                _logger.LogWarning("Rejected manual import: {Path} is not within the requested path or a configured root folder", item.FullPath);
                return new ManualImportResult
                {
                    Success = false,
                    Error = "Source file is not within the requested import path or a configured root folder",
                    FilePath = item.FullPath
                };
            }

            // Check if audiobook has a base path
            if (string.IsNullOrWhiteSpace(audiobook.BasePath))
            {
                var appSettings = await _configService.GetApplicationSettingsAsync() ?? new ApplicationSettings();
                var fallbackPath = appSettings.OutputPath;

                if (string.IsNullOrWhiteSpace(fallbackPath))
                {
                    return new ManualImportResult
                    {
                        Success = false,
                        Error = "No base path configured for audiobook and no default output path set",
                        FilePath = item.FullPath
                    };
                }

                // Use fallback path
                audiobook.BasePath = FileUtils.NormalizeStoredPath(fallbackPath);
            }

            // Extract metadata from the file
            var metadata = await _metadataService.ExtractFileMetadataAsync(item.FullPath);
            if (metadata == null)
            {
                return new ManualImportResult
                {
                    Success = false,
                    Error = "Failed to extract metadata from file",
                    FilePath = item.FullPath
                };
            }

            // Generate destination path using appropriate naming pattern
            var destinationPath = await GenerateManualImportPathAsync(audiobook, metadata, item, isMultiFile);

            // Ensure destination directory exists
            var destinationDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destinationDir))
            {
                Directory.CreateDirectory(destinationDir);
            }

            // If destination file exists, create a unique filename (append " (1)", " (2)", ...)
            var preUniquePath = destinationPath;
            try
            {
                _logger.LogDebug("Resolving unique destination for manual import: {Dest}, usedDestinations count: {Count}", destinationPath, usedDestinations?.Count ?? 0);
                destinationPath = FileUtils.GetUniqueDestinationPath(destinationPath, System.IO.File.Exists, usedDestinations);
                if (preUniquePath != destinationPath)
                {
                    _logger.LogDebug("Unique destination changed from {Old} to {New}", preUniquePath, destinationPath);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogWarning(ex, "Failed to generate unique destination filename for manual import: {Destination}", destinationPath);
            }

            // Move or copy the file
            try
            {
                _logger.LogDebug("Attempting to {Operation} file from {Source} to {Destination}", inputMode, item.FullPath, destinationPath);
                if (string.Equals(inputMode, "move", StringComparison.OrdinalIgnoreCase))
                {
                    System.IO.File.Move(item.FullPath, destinationPath, overwrite: false);
                    _logger.LogInformation("Moved file {Source} to {Destination}", item.FullPath, destinationPath);
                }
                else if (string.Equals(inputMode, "hardlink/copy", StringComparison.OrdinalIgnoreCase))
                {
                    var ok = await _fileMover.HardlinkFileAsync(item.FullPath, destinationPath);
                    if (!ok) throw new IOException($"HardlinkFileAsync failed: {item.FullPath} -> {destinationPath}");
                    _logger.LogInformation("Hardlinked file {Source} to {Destination}", item.FullPath, destinationPath);
                }
                else
                {
                    System.IO.File.Copy(item.FullPath, destinationPath, overwrite: false);
                    _logger.LogInformation("Copied file {Source} to {Destination}", item.FullPath, destinationPath);
                }
            }
            catch (IOException ex) when (System.IO.File.Exists(destinationPath))
            {
                _logger.LogWarning(ex, "Destination file already exists despite unique name generation: {Destination}", destinationPath);
                throw;
            }
            // Write ASIN to embedded file tags (non-critical — failure is logged, not thrown)
            await EnsureAudiobookBasePathAsync(audiobook, destinationPath);

            if (!string.IsNullOrWhiteSpace(audiobook.Asin))
                await _metadataService.WriteAsinTagAsync(destinationPath, audiobook.Asin);

            // Record the destination to avoid collisions with subsequent items in this batch
            try
            {
                if (usedDestinations != null)
                {
                    usedDestinations.Add(destinationPath);
                    _logger.LogDebug("Added destination to usedDestinations: {Destination}, total count now: {Count}", destinationPath, usedDestinations.Count);
                }
            }
            catch (ArgumentException ex)
            {
                _logger.LogDebug(ex, "Failed tracking used destination during manual import for {Destination}", destinationPath);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogDebug(ex, "Failed tracking used destination during manual import for {Destination}", destinationPath);
            }

            return new ManualImportResult
            {
                Success = true,
                FilePath = item.FullPath,
                DestinationPath = destinationPath,
                AudiobookId = audiobook.Id,
                AudiobookTitle = audiobook.Title
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
            _logger.LogError(ex, "Error importing file {FilePath}", item.FullPath);
            return new ManualImportResult
            {
                Success = false,
                Error = ex.Message,
                FilePath = item.FullPath
            };
        }
    }

    private async Task EnqueueFocusedScansAsync(IEnumerable<ManualImportResult> results)
    {
        if (_scanQueueService == null)
        {
            _logger.LogDebug("IScanQueueService not available - skipping focused scan enqueue after manual import");
            return;
        }

        var groupedResults = results
            .Where(r => r.Success && r.AudiobookId.HasValue && !string.IsNullOrWhiteSpace(r.DestinationPath))
            .GroupBy(r => r.AudiobookId!.Value);

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

            await PersistAudiobookBasePathAsync(group.Key, scanPath);

            try
            {
                var scanJobId = await _scanQueueService.EnqueueScanAsync(group.Key, scanPath);
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

    private async Task PersistAudiobookBasePathAsync(int audiobookId, string scanPath)
    {
        if (string.IsNullOrWhiteSpace(scanPath))
        {
            return;
        }

        try
        {
            var audiobook = await _audiobookRepository.GetByIdAsync(audiobookId);
            if (audiobook == null)
            {
                return;
            }

            var normalizedCurrent = string.IsNullOrWhiteSpace(audiobook.BasePath)
                ? string.Empty
                : FileUtils.NormalizeStoredPath(audiobook.BasePath);
            var normalizedScanPath = FileUtils.NormalizeStoredPath(scanPath);

            if (string.Equals(normalizedCurrent, normalizedScanPath, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(audiobook.BasePath, normalizedScanPath, StringComparison.Ordinal))
                {
                    audiobook.BasePath = normalizedScanPath;
                    await _audiobookRepository.UpdateAsync(audiobook);
                }

                return;
            }

            audiobook.BasePath = normalizedScanPath;
            await _audiobookRepository.UpdateAsync(audiobook);
            _logger.LogInformation(
                "Updated audiobook {AudiobookId} BasePath to imported scan root {BasePath} after manual import",
                audiobookId,
                normalizedScanPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogWarning(ex, "Failed to persist BasePath for audiobook {AudiobookId} after manual import", audiobookId);
        }
    }

    private static string? DetermineScanPath(IReadOnlyList<string> destinationPaths)
    {
        return FileUtils.GetCommonDirectory(destinationPaths);
    }

    private async Task<string> GenerateManualImportPathAsync(Audiobook audiobook, AudioMetadata metadata, ManualImportItem item, bool isMultiFile = false)
    {
        var sourceFilePath = item.FullPath ?? string.Empty;
        // Get the configured folder/file naming patterns from settings
        var settings = await _configService.GetApplicationSettingsAsync();
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
                    var rootFolders = await _rootFolderService.GetAllAsync();
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
        catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException) {
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

    private async Task EnsureAudiobookBasePathAsync(Audiobook audiobook, string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            return;
        }

        string? associationBasePath;
        try
        {
            associationBasePath = Path.GetDirectoryName(FileUtils.NormalizeStoredPath(destinationPath));
        }
        catch (ArgumentException)
        {
            associationBasePath = Path.GetDirectoryName(destinationPath);
        }
        catch (NotSupportedException)
        {
            associationBasePath = Path.GetDirectoryName(destinationPath);
        }
        catch (PathTooLongException)
        {
            associationBasePath = Path.GetDirectoryName(destinationPath);
        }

        if (string.IsNullOrWhiteSpace(associationBasePath))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(audiobook.BasePath))
        {
            try
            {
                var normalizedCurrent = FileUtils.NormalizeStoredPath(audiobook.BasePath);
                var matchesCurrent = string.Equals(normalizedCurrent, associationBasePath, StringComparison.OrdinalIgnoreCase);
                var destinationWithinCurrent = FileUtils.IsPathWithinRoot(destinationPath, normalizedCurrent);
                if (matchesCurrent || destinationWithinCurrent)
                {
                    if (!string.Equals(audiobook.BasePath, normalizedCurrent, StringComparison.Ordinal))
                    {
                        audiobook.BasePath = normalizedCurrent;
                        await _audiobookRepository.UpdateAsync(audiobook);
                    }

                    return;
                }
            }
            catch (ArgumentException)
            {
                if (string.Equals(audiobook.BasePath, associationBasePath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            catch (NotSupportedException)
            {
                if (string.Equals(audiobook.BasePath, associationBasePath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            catch (PathTooLongException)
            {
                if (string.Equals(audiobook.BasePath, associationBasePath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        try
        {
            audiobook.BasePath = associationBasePath;
            await _audiobookRepository.UpdateAsync(audiobook);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogDebug(ex, "Failed to persist BasePath {BasePath} for audiobook {AudiobookId} after manual import", associationBasePath, audiobook.Id);
        }
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

    private static List<ManualImportItem> BuildOrderedItems(IEnumerable<ManualImportItem> items)
    {
        var ordered = new List<ManualImportItem>();

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
                .Cast<ManualImportItem>());
        }

        foreach (var invalidItem in items.Where(i => string.IsNullOrWhiteSpace(i.FullPath)))
        {
            ordered.Add(invalidItem);
        }

        return ordered;
    }

    private async Task<int> ImportCompanionFilesAsync(
        ManualImportRequest request,
        IReadOnlyCollection<ManualImportItem> orderedItems,
        IReadOnlyCollection<ManualImportResult> results,
        string sourceRootPath,
        IReadOnlyCollection<FileUtils.AudioMatchProfile> selectedAudioProfiles,
        HashSet<string> usedDestinations,
        ISet<string> importBlacklist)
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
            .Where(file => !FileUtils.ShouldSkipImportFile(file, importBlacklist))
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
                var destinationDir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(destinationDir))
                {
                    Directory.CreateDirectory(destinationDir);
                }

                destinationPath = FileUtils.GetUniqueDestinationPath(destinationPath, System.IO.File.Exists, usedDestinations);

                if (string.Equals(request.InputMode, "move", StringComparison.OrdinalIgnoreCase))
                {
                    System.IO.File.Move(companionFile, destinationPath, overwrite: false);
                }
                else if (string.Equals(request.InputMode, "hardlink/copy", StringComparison.OrdinalIgnoreCase))
                {
                    var ok = await _fileMover.HardlinkFileAsync(companionFile, destinationPath);
                    if (!ok)
                    {
                        _logger.LogWarning("Hardlink failed for companion file {Source}, falling back to copy", companionFile);
                        System.IO.File.Copy(companionFile, destinationPath, overwrite: false);
                    }
                }
                else
                {
                    System.IO.File.Copy(companionFile, destinationPath, overwrite: false);
                }

                usedDestinations.Add(destinationPath);
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

public class ManualImportRequest
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "interactive";

    [JsonPropertyName("inputMode")]
    public string? InputMode { get; set; } // "move", "copy", or "hardlink/copy"

    [JsonPropertyName("includeCompanionFiles")]
    public bool IncludeCompanionFiles { get; set; }

    [JsonPropertyName("cleanupEmptySourceFolders")]
    public bool CleanupEmptySourceFolders { get; set; }

    [JsonPropertyName("items")]
    public List<ManualImportItem>? Items { get; set; }
}

public class ManualImportItem
{
    [JsonPropertyName("relativePath")]
    public string RelativePath { get; set; } = string.Empty;

    [JsonPropertyName("fullPath")]
    [Required]
    public string? FullPath { get; set; }

    [JsonPropertyName("matchedAudiobookId")]
    public int MatchedAudiobookId { get; set; }

    [JsonPropertyName("releaseGroup")]
    public string? ReleaseGroup { get; set; }

    [JsonPropertyName("qualityProfileId")]
    public int? QualityProfileId { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("size")]
    public string? Size { get; set; }

    [JsonIgnore]
    public int? SequenceNumberHint { get; set; }

    [JsonIgnore]
    public int? DiskNumberHint { get; set; }

    [JsonIgnore]
    public int? ChapterNumberHint { get; set; }
}

public class ManualImportResult
{
    public bool Success { get; set; }
    public string? FilePath { get; set; }
    public string? DestinationPath { get; set; }
    public int? AudiobookId { get; set; }
    public string? AudiobookTitle { get; set; }
    public string? Error { get; set; }
}


