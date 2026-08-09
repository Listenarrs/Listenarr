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
using Listenarr.Application.Common.Exceptions;
using Listenarr.Domain.Common;
using Listenarr.Api.Dtos.ManualImport;

namespace Listenarr.Api.Features.Downloads;

[ApiController]
[Route("api/v{version:apiVersion}/library/manual-import")]
[Tags("Library")]
public partial class ManualImportController : ControllerBase
{
    private readonly ILogger<ManualImportController> _logger;
    private readonly IAudiobookRepository _audiobookRepository;
    private readonly IMetadataService _metadataService;
    private readonly IFileNamingService _fileNamingService;
    private readonly IConfigurationService _configService;
    private readonly IScanQueueService _scanQueueService;
    private readonly IScanPathAuthorizationService _scanPathAuthorizationService;
    private readonly IRootFolderService _rootFolderService;
    private readonly IFileMover _fileMover;
    private readonly IAudiobookFileService _audiobookFileService;
    private readonly IFileSystem _fileSystem;
    private readonly IFileSystemSemanticsResolver _semanticsResolver;
    private readonly IFilesystemMutationCoordinator _filesystemMutationCoordinator;
    private readonly IAudiobookOperationCoordinator _audiobookOperationCoordinator;
    private readonly IMoveQueueService _moveQueueService;
    private readonly ILibraryFilesystemMutationGate _filesystemMutationGate;
    private readonly ManualImportPathPlanner _pathPlanner;
    private readonly ManualImportCompanionImporter _companionImporter;
    private readonly ILibraryDirectoryOwnershipStore _directoryOwnershipStore;

    public ManualImportController(
        ILogger<ManualImportController> logger,
        IAudiobookRepository audiobookRepository,
        IMetadataService metadataService,
        IFileNamingService fileNamingService,
        IConfigurationService configService,
        IScanQueueService scanQueueService,
        IScanPathAuthorizationService scanPathAuthorizationService,
        IRootFolderService rootFolderService,
        IFileMover fileMover,
        IAudiobookFileService audiobookFileService,
        IFileSystem fileSystem,
        IFileSystemSemanticsResolver semanticsResolver,
        IFilesystemMutationCoordinator filesystemMutationCoordinator,
        IAudiobookOperationCoordinator audiobookOperationCoordinator,
        IMoveQueueService moveQueueService,
        ILibraryDirectoryOwnershipStore directoryOwnershipStore,
        ILibraryFilesystemMutationGate filesystemMutationGate,
        ManualImportPathPlanner? pathPlanner = null,
        ManualImportCompanionImporter? companionImporter = null)
    {
        _logger = logger;
        _audiobookRepository = audiobookRepository;
        _metadataService = metadataService;
        _fileNamingService = fileNamingService;
        _configService = configService;
        _scanQueueService = scanQueueService;
        _scanPathAuthorizationService = scanPathAuthorizationService
            ?? throw new ArgumentNullException(nameof(scanPathAuthorizationService));
        _rootFolderService = rootFolderService;
        _fileMover = fileMover;
        _audiobookFileService = audiobookFileService;
        _fileSystem = fileSystem;
        _semanticsResolver = semanticsResolver;
        _filesystemMutationCoordinator = filesystemMutationCoordinator ?? throw new ArgumentNullException(nameof(filesystemMutationCoordinator));
        _audiobookOperationCoordinator = audiobookOperationCoordinator ?? throw new ArgumentNullException(nameof(audiobookOperationCoordinator));
        _moveQueueService = moveQueueService ?? throw new ArgumentNullException(nameof(moveQueueService));
        _directoryOwnershipStore = directoryOwnershipStore ?? throw new ArgumentNullException(nameof(directoryOwnershipStore));
        _filesystemMutationGate = filesystemMutationGate
            ?? throw new ArgumentNullException(nameof(filesystemMutationGate));
        _pathPlanner = pathPlanner ?? new ManualImportPathPlanner(fileNamingService);
        _companionImporter = companionImporter ?? new ManualImportCompanionImporter(
            metadataService,
            fileMover,
            fileSystem,
            semanticsResolver,
            directoryOwnershipStore,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ManualImportCompanionImporter>.Instance,
            audiobookFileService);
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
            if (!_fileSystem.DirectoryExists(normalized)) return NotFound(new { error = "Directory not found" });

            var settings = await _configService.GetApplicationSettingsAsync();

            var files = _fileSystem.EnumerateFiles(normalized, "*.*", SearchOption.AllDirectories)
                .Where(f => !FileUtils.IsBlacklistedFile(f, settings.ImportBlacklistExtensions))
                .Select(f => new
                {
                    relativePath = Path.GetRelativePath(normalized, f),
                    fullPath = f,
                    size = _fileSystem.GetFileLength(f),
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
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <returns>Summary of imported files with success/failure details per item.</returns>
    [HttpPost]
    public async Task<ActionResult<object>> Start(
        [FromBody] ManualImportRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Path))
        {
            return BadRequest(new { error = "Invalid request" });
        }

        cancellationToken.ThrowIfCancellationRequested();
        var sourceDirectory = Path.GetFullPath(request.Path);
        if (!_fileSystem.DirectoryExists(sourceDirectory))
        {
            return NotFound(new { error = "Directory not found" });
        }

        if (request.Items == null || !request.Items.Any())
        {
            return BadRequest(new { error = "No items to import" });
        }

        _filesystemMutationGate.EnsureReady();

        var results = new List<ManualImportResultDto>();
        var destinationTracker = new ManualImportDestinationTracker(_fileSystem, _semanticsResolver);

        try
        {
            // Fetch root folders once for the whole batch (used for path containment validation)
            var rootFolders = await _rootFolderService.GetAllAsync();
            var appSettings = await _configService.GetApplicationSettingsAsync();
            var sourceSemantics = await ResolvePathSemanticsAsync(
                sourceDirectory,
                "Source filesystem identity is unavailable.",
                cancellationToken);
            var orderedItems = ManualImportPathPlanner.BuildOrderedItems(
                request.Items,
                sourceSemantics.Comparer);
            var selectedAudioProfiles = request.IncludeCompanionFiles
                && request.Action != FileAction.None
                ? await _companionImporter.BuildAudioMatchProfilesAsync(
                    orderedItems
                        .Where(item => !string.IsNullOrWhiteSpace(item.FullPath))
                        .Select(item => item.FullPath!)
                        .Where(FileUtils.IsAudioFile),
                    sourceSemantics.Comparer,
                    cancellationToken)
                : Array.Empty<FileUtils.AudioMatchProfile>();

            _logger.LogDebug("Manual import batch: {ItemCount} items", orderedItems.Count);
            var stoppedByCancellation = false;

            await ExecuteWithAudiobookLocksAsync(
                orderedItems.Select(item => item.MatchedAudiobookId),
                async operationToken =>
                {
                    var planningBasePaths = new Dictionary<int, string>();
                    try
                    {
                        foreach (var item in orderedItems)
                        {
                            operationToken.ThrowIfCancellationRequested();
                            var fileCount = orderedItems.Count(candidate =>
                                candidate.MatchedAudiobookId == item.MatchedAudiobookId);
                            _logger.LogDebug(
                                "Importing item {Index}: {Path} for audiobook {AudiobookId}, fileCount: {FileCount}",
                                orderedItems.IndexOf(item),
                                item.FullPath,
                                item.MatchedAudiobookId,
                                fileCount);
                            var result = await ImportFileAsync(
                                item,
                                request.Action,
                                sourceDirectory,
                                sourceSemantics,
                                destinationTracker,
                                planningBasePaths,
                                rootFolders,
                                appSettings,
                                fileCount > 1,
                                operationToken);
                            _logger.LogDebug(
                                "Import result {Index}: Success={Success}, Destination={Destination}, Error={Error}",
                                orderedItems.IndexOf(item),
                                result.Success,
                                result.DestinationPath,
                                result.Error);
                            results.Add(result);
                        }

                        if (request.IncludeCompanionFiles && request.Action != FileAction.None)
                        {
                            var companionImportCount = await _companionImporter.ImportAsync(
                                request.Action,
                                orderedItems,
                                results,
                                sourceDirectory,
                                selectedAudioProfiles,
                                destinationTracker,
                                sourceSemantics,
                                appSettings.ImportBlacklistExtensions,
                                operationToken);
                            _logger.LogInformation(
                                "Manual import companion-file pass completed with {Count} imported companion file(s)",
                                companionImportCount);
                        }

                        if (request.Action != FileAction.None
                            && request.CleanupEmptySourceFolders)
                        {
                            operationToken.ThrowIfCancellationRequested();
                            _fileSystem.DeleteEmptyDirectories(sourceDirectory);
                        }
                    }
                    catch (OperationCanceledException) when (
                        results.Any(result => result.Success))
                    {
                        stoppedByCancellation = true;
                    }

                    var hasSuccessfulMutation = results.Any(result => result.Success);
                    await EnqueueFocusedScansAsync(
                        results,
                        hasSuccessfulMutation
                            ? CancellationToken.None
                            : operationToken);

                    if (hasSuccessfulMutation
                        && operationToken.IsCancellationRequested)
                    {
                        stoppedByCancellation = true;
                    }
                    else
                    {
                        operationToken.ThrowIfCancellationRequested();
                    }
                },
                cancellationToken);

            var successCount = results.Count(r => r.Success);
            _logger.LogInformation("Manual import batch completed: {SuccessCount}/{TotalCount} succeeded, usedDestinations: {DestinationCount}", successCount, results.Count, destinationTracker.Count);
            return Ok(new
            {
                importedCount = successCount,
                totalCount = orderedItems.Count,
                stoppedByCancellation,
                results = results
            });
        }
        catch (ApplicationConflictException exception)
        {
            return Conflict(new
            {
                error = exception.SafeDetail,
                code = exception.Code
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogError(ex, "Error starting manual import");
            return StatusCode(500, new { error = "Failed to start import" });
        }
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
