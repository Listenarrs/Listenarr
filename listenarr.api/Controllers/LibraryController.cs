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
using Listenarr.Domain.Models;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Listenarr.Domain.Common;
using Listenarr.Application.Interfaces;
using Listenarr.Domain.Models.Configurations;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Application.Notification;
using Listenarr.Application.Security;
using Listenarr.Application.Metadata;
using Listenarr.Application.Audiobooks;
using Listenarr.Api.Attributes;

namespace Listenarr.Api.Controllers
{
    [ApiController]
    [Route("api/v{version:apiVersion}/library")]
    [Tags("Library")]
    public class LibraryController : ControllerBase
    {
        private readonly IAudiobookRepository _repo;
        private readonly IImageCacheService _imageCacheService;
        private readonly ILogger<LibraryController> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IHistoryRepository _historyRepository;
        private readonly IAudiobookFileRepository _audioFileRepository;
        private readonly IQualityProfileRepository _qualityProfileRepository;
        private readonly IDownloadRepository _downloadRepository;
        private readonly IScanQueueService? _scanQueueService;
        private readonly IMoveQueueService? _moveQueueService;
        private readonly IFileNamingService _fileNamingService;
        private readonly NotificationService? _notificationService;
        private readonly IRootFolderService? _rootFolderService;
        private readonly ILibraryAddService? _libraryAddService;
        private readonly IRenameService? _renameService;
        private readonly ILibraryListService _libraryListService;
        private readonly IAudiobookFilesystemDeleteService _audiobookFilesystemDeleteService;
        private readonly LibraryMetadataRescanWorkflow _metadataRescanWorkflow;
        private readonly string _contentRootPath;
        /// <summary>Initializes a new instance of <see cref="LibraryController"/>.</summary>
        /// <param name="repo">Repository for audiobook persistence and queries.</param>
        /// <param name="imageCacheService">Service for caching and moving cover images.</param>
        /// <param name="logger">Logger instance for diagnostic messages.</param>
        /// <param name="scopeFactory">Service scope factory used to create scoped services when required.</param>
        /// <param name="historyRepository">Repository for download history records.</param>
        /// <param name="audioFileRepository">Repository for audiobook file records.</param>
        /// <param name="qualityProfileRepository">Repository for quality profile configuration.</param>
        /// <param name="downloadRepository">Repository for active download records.</param>
        /// <param name="fileNamingService">Service responsible for applying file naming patterns.</param>
        /// <param name="scanQueueService">Optional background scan queue service for asynchronous scans.</param>
        /// <param name="moveQueueService">Optional background move queue service for processing move requests.</param>
        /// <param name="notificationService">Service for sending webhook notifications.</param>
        /// <param name="rootFolderService">Optional root folder service for managing and enumerating configured root folders used for validating explicit scan paths.</param>
        /// <param name="libraryAddService">Optional shared add-to-library service used by runtime requests and background syncs.</param>
        /// <param name="renameService">Optional organize/rename service used for previewing and executing library file organization.</param>
        /// <param name="applicationPathService">Application path service used to resolve content-root-relative cache files.</param>
        /// <param name="libraryListService">Application service that builds the slim library list payload.</param>
        /// <param name="audiobookFilesystemDeleteService">Application service responsible for safe audiobook filesystem cleanup.</param>
        /// <param name="metadataRescanWorkflow">API workflow for on-demand audiobook metadata rescans.</param>
        public LibraryController(
            IAudiobookRepository repo,
            IImageCacheService imageCacheService,
            ILogger<LibraryController> logger,
            IServiceScopeFactory scopeFactory,
            IHistoryRepository historyRepository,
            IAudiobookFileRepository audioFileRepository,
            IQualityProfileRepository qualityProfileRepository,
            IDownloadRepository downloadRepository,
            IFileNamingService fileNamingService,
            IApplicationPathService applicationPathService,
            ILibraryListService libraryListService,
            IAudiobookFilesystemDeleteService audiobookFilesystemDeleteService,
            LibraryMetadataRescanWorkflow metadataRescanWorkflow,
            IScanQueueService? scanQueueService = null,
            IMoveQueueService? moveQueueService = null,
            NotificationService? notificationService = null,
            IRootFolderService? rootFolderService = null,
            ILibraryAddService? libraryAddService = null,
            IRenameService? renameService = null)
        {
            _repo = repo;
            _imageCacheService = imageCacheService;
            _logger = logger;
            _scopeFactory = scopeFactory;
            _historyRepository = historyRepository;
            _audioFileRepository = audioFileRepository;
            _qualityProfileRepository = qualityProfileRepository;
            _downloadRepository = downloadRepository;
            _fileNamingService = fileNamingService;
            _scanQueueService = scanQueueService;
            _moveQueueService = moveQueueService;
            _notificationService = notificationService;
            _rootFolderService = rootFolderService;
            _libraryAddService = libraryAddService;
            _renameService = renameService;
            _libraryListService = libraryListService;
            _audiobookFilesystemDeleteService = audiobookFilesystemDeleteService;
            _metadataRescanWorkflow = metadataRescanWorkflow;
            _contentRootPath = applicationPathService.ContentRootPath;
        }

        private static string ResolvePathWithOptionalBase(string? basePath, string candidatePath)
        {
            return FileUtils.CombineWithOptionalBase(basePath, candidatePath);
        }

        public class ScanRequest
        {
            public string? Path { get; set; }
        }

        /// <summary>
        /// Add a new audiobook to the library from search metadata.
        /// </summary>
        /// <param name="request">Audiobook metadata, monitoring preference, quality profile, and optional auto-search flag.</param>
        /// <returns>The newly created audiobook record.</returns>
        [HttpPost("add")]
        public async Task<IActionResult> AddToLibrary([FromBody] AddToLibraryRequest request)
        {
            if (_libraryAddService != null)
            {
                var result = await _libraryAddService.AddToLibraryAsync(new LibraryAddOperationRequest
                {
                    Metadata = request.Metadata,
                    Monitored = request.Monitored,
                    QualityProfileId = request.QualityProfileId,
                    AutoSearch = request.AutoSearch,
                    DestinationPath = request.DestinationPath,
                    SearchResult = request.SearchResult,
                    HistorySource = "AddNew",
                    HistoryMessage = $"Audiobook '{request.Metadata.Title}' added to library from Add New page"
                });

                if (result.AlreadyExists)
                {
                    return Conflict(new { message = result.Message, audiobook = result.Audiobook });
                }

                return Ok(new { message = result.Message, audiobook = result.Audiobook });
            }

            var metadata = request.Metadata;

            _logger.LogInformation("AddToLibrary received metadata: Title={Title}, Asin={Asin}, PublishYear={PublishYear}, Authors={Authors}, Series={Series}",
                LogRedaction.SanitizeText(metadata.Title), LogRedaction.SanitizeText(metadata.Asin), LogRedaction.SanitizeText(metadata.PublishYear),
                LogRedaction.SanitizeText(metadata.Authors != null ? string.Join(", ", metadata.Authors) : "null"),
                LogRedaction.SanitizeText(metadata.Series));

            // If metadata doesn't have PublishYear but we have search result with publishedDate, try to extract year
            if (string.IsNullOrWhiteSpace(metadata.PublishYear) && request.SearchResult != null)
            {
                try
                {
                    if (DateTime.TryParse(request.SearchResult.PublishedDate, out var publishDate))
                    {
                        metadata.PublishYear = publishDate.Year.ToString();
                        _logger.LogInformation("Extracted publish year from search result publishedDate: {Year}", metadata.PublishYear);
                    }
                    else
                    {
                        _logger.LogWarning("Could not parse PublishedDate as DateTime: {PublishedDate}", LogRedaction.SanitizeText(request.SearchResult.PublishedDate));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to extract publish year from search result publishedDate");
                }
            }

            // Check if audiobook already exists in library
            if (!string.IsNullOrEmpty(metadata.Asin))
            {
                var existingByAsin = await _repo.GetByAsinAsync(metadata.Asin);
                if (existingByAsin != null)
                {
                    return Conflict(new { message = "Audiobook already exists in library", audiobook = existingByAsin });
                }
            }

            var firstIsbn = (metadata.Isbn != null && metadata.Isbn.Any()) ? metadata.Isbn.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i)) : null;
            if (!string.IsNullOrWhiteSpace(firstIsbn))
            {
                var existingByIsbn = await _repo.GetByIsbnAsync(firstIsbn);
                if (existingByIsbn != null)
                {
                    return Conflict(new { message = "Audiobook already exists in library", audiobook = existingByIsbn });
                }
            }

            // Move image from temp cache to permanent library storage
            string? imageUrl = metadata.ImageUrl;
            if (!string.IsNullOrEmpty(metadata.Asin))
            {
                try
                {
                    var libraryImagePath = await _imageCacheService.MoveToLibraryStorageAsync(metadata.Asin, metadata.ImageUrl);
                    if (!string.IsNullOrWhiteSpace(libraryImagePath))
                    {
                        imageUrl = $"/{libraryImagePath}";
                        _logger.LogInformation("Moved image for ASIN {Asin} to permanent library storage", LogRedaction.SanitizeText(metadata.Asin));
                    }
                    else
                    {
                        _logger.LogWarning("Failed to move image for ASIN {Asin}, image may not be in temp cache", LogRedaction.SanitizeText(metadata.Asin));
                    }
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Error moving image for ASIN {Asin} to library storage", LogRedaction.SanitizeText(metadata.Asin));
                    // Continue with original image URL if move fails
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogWarning(ex, "Error moving image for ASIN {Asin} to library storage", LogRedaction.SanitizeText(metadata.Asin));
                    // Continue with original image URL if move fails
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(ex, "Error moving image for ASIN {Asin} to library storage", LogRedaction.SanitizeText(metadata.Asin));
                    // Continue with original image URL if move fails
                }
                catch (TaskCanceledException ex)
                {
                    _logger.LogWarning(ex, "Error moving image for ASIN {Asin} to library storage", LogRedaction.SanitizeText(metadata.Asin));
                    // Continue with original image URL if move fails
                }
                catch (InvalidOperationException ex)
                {
                    _logger.LogWarning(ex, "Error moving image for ASIN {Asin} to library storage", LogRedaction.SanitizeText(metadata.Asin));
                    // Continue with original image URL if move fails
                }
                catch (UriFormatException ex)
                {
                    _logger.LogWarning(ex, "Error moving image for ASIN {Asin} to library storage", LogRedaction.SanitizeText(metadata.Asin));
                    // Continue with original image URL if move fails
                }
            }
            else if (metadata.Isbn != null && metadata.Isbn.Any(i => !string.IsNullOrWhiteSpace(i)))
            {
                firstIsbn = metadata.Isbn.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i));
                if (!string.IsNullOrWhiteSpace(firstIsbn))
                {
                    var existingByIsbn = await _repo.GetByIsbnAsync(firstIsbn);
                    if (existingByIsbn != null)
                    {
                        return Conflict(new { message = "Audiobook already exists in library", audiobook = existingByIsbn });
                    }
                }

                try
                {
                    var derivedKey = "img-" + ComputeShortHash(firstIsbn ?? metadata.ImageUrl ?? string.Empty);
                    var libraryImagePath = await _imageCacheService.MoveToLibraryStorageAsync(derivedKey, metadata.ImageUrl);
                    if (!string.IsNullOrWhiteSpace(libraryImagePath))
                    {
                        imageUrl = $"/{libraryImagePath}";
                        _logger.LogInformation("Moved image for derived ISBN {Key} to permanent library storage", LogRedaction.SanitizeText(derivedKey));
                    }
                    else
                    {
                        _logger.LogWarning("Failed to move image for derived ISBN {Key}, image may not be reachable", LogRedaction.SanitizeText(derivedKey));
                    }
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Error moving image for derived ISBN to library storage");
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogWarning(ex, "Error moving image for derived ISBN to library storage");
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(ex, "Error moving image for derived ISBN to library storage");
                }
                catch (TaskCanceledException ex)
                {
                    _logger.LogWarning(ex, "Error moving image for derived ISBN to library storage");
                }
                catch (InvalidOperationException ex)
                {
                    _logger.LogWarning(ex, "Error moving image for derived ISBN to library storage");
                }
                catch (UriFormatException ex)
                {
                    _logger.LogWarning(ex, "Error moving image for derived ISBN to library storage");
                }
            }
            else if (!string.IsNullOrEmpty(metadata.ImageUrl))
            {
                // No ASIN or ISBN available; attempt to move/download the image using a derived key
                try
                {
                    var rawKey = request.SearchResult?.Id ?? request.SearchResult?.ResultUrl ?? request.SearchResult?.ProductUrl ?? metadata.ImageUrl;
                    var derivedKey = "img-" + ComputeShortHash(rawKey);
                    var libraryImagePath = await _imageCacheService.MoveToLibraryStorageAsync(derivedKey, metadata.ImageUrl);
                    if (!string.IsNullOrWhiteSpace(libraryImagePath))
                    {
                        imageUrl = $"/{libraryImagePath}";
                        _logger.LogInformation("Moved image for derived key {Key} to permanent library storage", LogRedaction.SanitizeText(derivedKey));
                    }
                    else
                    {
                        _logger.LogWarning("Failed to move image for derived key {Key}, image may not be reachable", LogRedaction.SanitizeText(derivedKey));
                    }
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Error moving image for derived key when ASIN is missing");
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogWarning(ex, "Error moving image for derived key when ASIN is missing");
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(ex, "Error moving image for derived key when ASIN is missing");
                }
                catch (TaskCanceledException ex)
                {
                    _logger.LogWarning(ex, "Error moving image for derived key when ASIN is missing");
                }
                catch (InvalidOperationException ex)
                {
                    _logger.LogWarning(ex, "Error moving image for derived key when ASIN is missing");
                }
                catch (UriFormatException ex)
                {
                    _logger.LogWarning(ex, "Error moving image for derived key when ASIN is missing");
                }
            }

            // Convert metadata to Audiobook entity and save to database
            var audiobook = metadata.ToAudiobook();

            audiobook.Monitored = request.Monitored; // Use custom monitored setting
            audiobook.ImageUrl = imageUrl;

            AudiobookSeriesMembershipHelper.ApplyToAudiobook(
                audiobook,
                metadata.SeriesMemberships,
                metadata.Series,
                AudibleBookMetadata.ToStringOrFirst(metadata.SeriesNumber));

            AudiobookIdentifierMapper.SyncImportedIdentifiersFromLegacyFields(audiobook);

            _logger.LogInformation("Created Audiobook entity: Title={Title}, Asin={Asin}, PublishYear={PublishYear}",
                LogRedaction.SanitizeText(audiobook.Title), LogRedaction.SanitizeText(audiobook.Asin), LogRedaction.SanitizeText(audiobook.PublishYear));

            // Assign quality profile - use custom if provided, otherwise default
            if (request.QualityProfileId.HasValue)
            {
                audiobook.QualityProfileId = request.QualityProfileId.Value;
                _logger.LogInformation("Assigned custom quality profile ID {ProfileId} to new audiobook '{Title}'",
                    request.QualityProfileId.Value, LogRedaction.SanitizeText(audiobook.Title));
            }
            else
            {
                // Assign default quality profile to new audiobooks
                using (var scope = _scopeFactory.CreateScope())
                {
                    var qualityProfileService = scope.ServiceProvider.GetRequiredService<IQualityProfileService>();
                    var defaultProfile = await qualityProfileService.GetDefaultAsync();
                    if (defaultProfile != null)
                    {
                        audiobook.QualityProfileId = defaultProfile.Id;
                        _logger.LogInformation("Assigned default quality profile '{ProfileName}' (ID: {ProfileId}) to new audiobook '{Title}'",
                            defaultProfile.Name, defaultProfile.Id, audiobook.Title);
                    }
                    else
                    {
                        _logger.LogWarning("No default quality profile found. New audiobook '{Title}' will not have a quality profile assigned.", LogRedaction.SanitizeText(audiobook.Title));
                    }
                }
            }

            // Compute or use custom BasePath (but don't create the directory yet - that happens during import)
            if (!string.IsNullOrWhiteSpace(request.DestinationPath))
            {
                // User provided a custom destination path - store it as BasePath
                // ImportService will recognize BasePath as set and use filename-only pattern
                audiobook.BasePath = FileUtils.NormalizeStoredPath(request.DestinationPath);
                _logger.LogInformation("Using custom destination path for audiobook '{Title}': {BasePath}",
                    audiobook.Title, audiobook.BasePath);
            }
            // If no custom path provided, leave BasePath null
            // ImportService will use the default naming pattern from settings

            await _repo.AddAsync(audiobook);

            // Resolve author ASINs and cache author images via Audible when possible
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var audible = scope.ServiceProvider.GetRequiredService<AudibleService>();

                if (audiobook.Authors != null && audiobook.Authors.Any())
                {
                    audiobook.AuthorAsins = audiobook.AuthorAsins ?? new List<string>();
                    foreach (var authorName in audiobook.Authors)
                    {
                        try
                        {
                            var info = await audible.LookupAuthorAsync(authorName);
                            if (info != null && !string.IsNullOrWhiteSpace(info.Asin))
                            {
                                // Avoid duplicates
                                if (!audiobook.AuthorAsins.Contains(info.Asin))
                                {
                                    audiobook.AuthorAsins.Add(info.Asin);
                                }

                                // Ensure author image is cached in authors folder (will download if necessary)
                                try
                                {
                                    var moved = await _imageCacheService.MoveToAuthorLibraryStorageAsync(info.Asin, info.Image);
                                    if (moved != null)
                                    {
                                        _logger.LogInformation("Cached author image for {Author} (ASIN: {Asin})", authorName, info.Asin);
                                    }
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                {
                                    _logger.LogWarning(ex, "Failed to cache author image for {Author}", authorName);
                                }
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger.LogWarning(ex, "Author lookup failed for {Author}", authorName);
                        }
                    }

                    // Persist any updated author ASINs
                    try
                    {
                        await _repo.UpdateAsync(audiobook);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Failed to persist author ASINs for audiobook '{Title}'", LogRedaction.SanitizeText(audiobook.Title));
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Error resolving author ASINs for audiobook '{Title}'", LogRedaction.SanitizeText(audiobook.Title));
            }

            // Send notification if configured
            if (_notificationService != null)
            {
                using var scope = _scopeFactory.CreateScope();
                var configService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
                var settings = await configService.GetApplicationSettingsAsync();
                var data = new
                {
                    id = audiobook.Id,
                    title = audiobook.Title ?? "Unknown Title",
                    authors = audiobook.Authors,
                    narrators = audiobook.Narrators,
                    description = audiobook.Description,
                    asin = audiobook.Asin,
                    publisher = audiobook.Publisher,
                    year = audiobook.PublishYear,
                    imageUrl = audiobook.ImageUrl
                };
                await _notificationService.SendNotificationAsync("book-added", data, settings.WebhookUrl, settings.EnabledNotificationTriggers);
            }


            // Directory creation has been deferred to file import time to avoid creating empty directories
            // for audiobooks that may never be downloaded. If a custom destination path was specified when
            // adding the audiobook, it will be stored in BasePath and used when ImportService processes
            // the downloaded files. If no custom path was specified, ImportService will use the configured
            // naming pattern and output path to determine the directory structure.

            // Log history entry for the added audiobook
            var historyEntry = new History
            {
                AudiobookId = audiobook.Id,
                AudiobookTitle = audiobook.Title ?? "Unknown Title",
                EventType = "Added",
                Message = $"Audiobook '{audiobook.Title}' added to library from Add New page",
                Source = "AddNew",
                Timestamp = DateTime.UtcNow
            };

            await _historyRepository.AddAsync(historyEntry);

            _logger.LogInformation("Added audiobook '{Title}' (ASIN: {Asin}) to library with Monitored={Monitored}, QualityProfileId={QualityProfileId}, AutoSearch={AutoSearch}",
                audiobook.Title, audiobook.Asin, request.Monitored, audiobook.QualityProfileId, request.AutoSearch);

            return Ok(new { message = "Audiobook added to library successfully", audiobook });
        }

        /// <summary>
        /// Preview the destination path that would be computed for an audiobook based on current naming settings.
        /// </summary>
        /// <param name="request">Audiobook metadata and optional destination root override.</param>
        /// <returns>Full path, relative path, and root directory.</returns>
        [HttpPost("preview-path")]
        public async Task<IActionResult> PreviewPath([FromBody] PreviewPathRequest request)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var configService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
                var settings = await configService.GetApplicationSettingsAsync();

                var root = !string.IsNullOrEmpty(request.DestinationRoot) ? request.DestinationRoot : settings.OutputPath;

                // Build a temporary Audiobook to feed naming pattern logic
                var temp = request.Metadata.ToAudiobook();

                AudiobookSeriesMembershipHelper.ApplyToAudiobook(
                    temp,
                    request.Metadata.SeriesMemberships,
                    request.Metadata.Series,
                    request.Metadata.SeriesNumber);

                var namingPattern = !string.IsNullOrWhiteSpace(settings.FolderNamingPattern)
                    ? settings.FolderNamingPattern
                    : settings.FileNamingPattern;
                var full = LibraryPathPlanner.ComputeAudiobookBaseDirectoryFromPattern(temp, root ?? string.Empty, namingPattern, _fileNamingService);

                var relative = full;
                if (!string.IsNullOrEmpty(root) && full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    relative = full.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }

                return Ok(new { fullPath = full, relativePath = relative, root = root });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to compute preview path");
                return StatusCode(500, new { message = "Failed to compute preview path" });
            }
        }

        /// <summary>
        /// Get all audiobooks in the library using a slim list payload.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            return Ok(await _libraryListService.GetAllAsync());
        }

        /// <summary>
        /// Look up an audiobook by its ASIN.
        /// </summary>
        /// <param name="asin">Amazon Standard Identification Number.</param>
        [HttpGet("by-asin/{asin}")]
        public async Task<IActionResult> GetByAsin(string asin)
        {
            var book = await _repo.GetByAsinAsync(asin);
            if (book == null) return NotFound();
            return Ok(book);
        }

        /// <summary>
        /// Look up an audiobook by its ISBN.
        /// </summary>
        /// <param name="isbn">International Standard Book Number.</param>
        [HttpGet("by-isbn/{isbn}")]
        public async Task<IActionResult> GetByIsbn(string isbn)
        {
            var book = await _repo.GetByIsbnAsync(isbn);
            if (book == null) return NotFound();
            return Ok(book);
        }

        /// <summary>
        /// Get a single audiobook by its database ID, including files, external identifiers, and wanted status.
        /// </summary>
        /// <param name="id">Audiobook ID.</param>
        [HttpGet("{id}")]
        public async Task<ActionResult<Audiobook>> GetAudiobook(int id)
        {
            var updated = await _repo.GetByIdAsync(id);

            if (updated == null)
                return NotFound(new { message = "Audiobook not found" });

            var audiobookDto = new
            {
                id = updated.Id,
                title = updated.Title,
                subtitle = updated.Subtitle,
                authors = updated.Authors,
                narrators = updated.Narrators,
                description = updated.Description,
                genres = updated.Genres,
                isbn = updated.Isbn != null ? updated.Isbn.FirstOrDefault() : null,
                isbns = updated.Isbn,
                asin = updated.Asin,
                openLibraryId = updated.OpenLibraryId,
                identifiers = AudiobookIdentifierMapper.GetEffectiveIdentifiers(updated)
                    .Select(AudiobookIdentifierMapper.ToIdentifierResponse)
                    .ToList(),
                imageUrl = updated.ImageUrl,
                publishYear = updated.PublishYear,
                publisher = updated.Publisher,
                language = updated.Language,
                filePath = updated.FilePath,
                fileSize = updated.FileSize,
                basePath = updated.BasePath,
                runtime = updated.Runtime,
                edition = updated.Edition,
                version = updated.Version,
                @explicit = updated.Explicit,
                abridged = updated.Abridged,
                monitored = updated.Monitored,
                quality = updated.Quality,
                qualityProfileId = updated.QualityProfileId,
                authorAsins = updated.AuthorAsins,
                series = updated.Series,
                seriesNumber = updated.SeriesNumber,
                publishedDate = updated.PublishedDate,
                seriesMemberships = updated.SeriesMemberships?
                    .OrderByDescending(m => m.IsPrimary)
                    .ThenBy(m => m.SortOrder)
                    .Select(m => new
                    {
                        id = m.Id,
                        seriesName = m.SeriesName,
                        seriesNumber = m.SeriesNumber,
                        seriesAsin = m.SeriesAsin,
                        isPrimary = m.IsPrimary,
                        sortOrder = m.SortOrder
                    })
                    .ToList(),
                tags = updated.Tags,
                files = updated.Files?.Select(f => new
                {
                    id = f.Id,
                    path = f.Path,
                    size = f.Size,
                    durationSeconds = f.DurationSeconds,
                    format = f.Format,
                    container = f.Container,
                    codec = f.Codec,
                    bitrate = f.Bitrate,
                    sampleRate = f.SampleRate,
                    channels = f.Channels,
                    source = f.Source,
                    createdAt = f.CreatedAt
                }).ToList(),
                wanted = AudiobookWantedEvaluator.Compute(updated)
            };

            return Ok(audiobookDto);
        }

        /// <summary>
        /// Get all external identifiers (ASIN, ISBN, Goodreads ID, etc.) for an audiobook.
        /// </summary>
        /// <param name="id">Audiobook ID.</param>
        [HttpGet("{id}/identifiers")]
        public async Task<IActionResult> GetAudiobookIdentifiers(int id)
        {
            var audiobook = await _repo.GetByIdAsync(id);

            if (audiobook == null)
            {
                return NotFound(new { message = "Audiobook not found" });
            }

            var identifiers = AudiobookIdentifierMapper.GetEffectiveIdentifiers(audiobook)
                .Select(AudiobookIdentifierMapper.ToIdentifierResponse)
                .ToList();

            return Ok(new
            {
                audiobookId = audiobook.Id,
                identifiers
            });
        }

        /// <summary>
        /// Replace all external identifiers for an audiobook in a single operation.
        /// </summary>
        /// <param name="id">Audiobook ID.</param>
        /// <param name="request">New set of identifiers. Existing identifiers will be removed and replaced.</param>
        [HttpPut("{id}/identifiers")]
        public async Task<IActionResult> ReplaceAudiobookIdentifiers(int id, [FromBody] ReplaceAudiobookIdentifiersRequest? request)
        {
            var audiobook = await _repo.GetByIdAsync(id);

            if (audiobook == null)
            {
                return NotFound(new { message = "Audiobook not found" });
            }

            var incoming = request?.Identifiers ?? new List<AudiobookIdentifierWriteItem>();
            if (incoming.Count > 50)
            {
                return BadRequest(new { message = "Too many identifiers. Maximum is 50." });
            }

            var validationErrors = new List<object>();
            var normalized = new List<AudiobookExternalIdentifier>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var primaryCountByType = new Dictionary<AudiobookExternalIdentifierType, int>();
            var now = DateTime.UtcNow;
            var existingServerOwnedSourceKeys = new HashSet<string>(
                (audiobook.ExternalIdentifiers ?? new List<AudiobookExternalIdentifier>())
                    .Where(i =>
                        i.Source != AudiobookExternalIdentifierSource.Manual &&
                        !string.IsNullOrWhiteSpace(i.ValueNormalized))
                    .Select(AudiobookIdentifierMapper.FullSourceKey),
                StringComparer.OrdinalIgnoreCase);

            for (var index = 0; index < incoming.Count; index++)
            {
                var item = incoming[index];
                if (!Enum.IsDefined(typeof(AudiobookExternalIdentifierType), item.Type))
                {
                    validationErrors.Add(new { index, field = "type", error = "Unsupported identifier type." });
                    continue;
                }

                if (!AudiobookIdentifierNormalizer.TryNormalize(item.Type, item.Value, out var normalizedValue, out var error))
                {
                    validationErrors.Add(new { index, field = "value", error = error ?? "Invalid identifier value." });
                    continue;
                }

                var normalizedRegion = item.Type == AudiobookExternalIdentifierType.Asin
                    ? AudiobookIdentifierNormalizer.NormalizeRegion(item.Region)
                    : null;

                var key = $"{item.Type}|{normalizedValue}|{normalizedRegion ?? string.Empty}";
                if (!seen.Add(key))
                {
                    validationErrors.Add(new { index, field = "value", error = "Duplicate identifier." });
                    continue;
                }

                if (item.IsPrimary)
                {
                    primaryCountByType.TryGetValue(item.Type, out var count);
                    primaryCountByType[item.Type] = count + 1;
                }

                var source = item.Source ?? AudiobookExternalIdentifierSource.Manual;
                if (!Enum.IsDefined(typeof(AudiobookExternalIdentifierSource), source))
                {
                    source = AudiobookExternalIdentifierSource.Manual;
                }
                else if (source != AudiobookExternalIdentifierSource.Manual)
                {
                    // Client writes cannot create or spoof Provider/Imported provenance.
                    // Preserve server-owned provenance only for exact existing rows.
                    var requestedKey = AudiobookIdentifierMapper.FullSourceKey(item.Type, normalizedValue, normalizedRegion, source);
                    if (!existingServerOwnedSourceKeys.Contains(requestedKey))
                    {
                        source = AudiobookExternalIdentifierSource.Manual;
                    }
                }

                normalized.Add(new AudiobookExternalIdentifier
                {
                    AudiobookId = audiobook.Id,
                    Type = item.Type,
                    ValueRaw = AudiobookIdentifierNormalizer.NormalizeRawValueForStorage(item.Value),
                    ValueNormalized = normalizedValue,
                    Region = normalizedRegion,
                    IsPrimary = item.IsPrimary,
                    Source = source,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }

            foreach (var kvp in primaryCountByType.Where(kvp => kvp.Value > 1))
            {
                validationErrors.Add(new
                {
                    field = "isPrimary",
                    type = kvp.Key,
                    error = $"Only one primary identifier is allowed for type {kvp.Key}."
                });
            }

            if (validationErrors.Count > 0)
            {
                return BadRequest(new { message = "Identifier validation failed.", errors = validationErrors });
            }

            // Ensure a primary ASIN exists when ASINs are present.
            var asins = normalized.Where(i => i.Type == AudiobookExternalIdentifierType.Asin).ToList();
            if (asins.Count > 0 && !asins.Any(i => i.IsPrimary))
            {
                asins[0].IsPrimary = true;
            }

            var olids = normalized.Where(i => i.Type == AudiobookExternalIdentifierType.OpenLibraryId).ToList();
            if (olids.Count == 1)
            {
                olids[0].IsPrimary = true;
            }

            audiobook.ExternalIdentifiers = normalized;
            AudiobookIdentifierMapper.SyncLegacyFieldsFromIdentifiers(audiobook);

            await _repo.UpdateWithIdentifierReplaceAsync(audiobook, normalized);

            _logger.LogInformation(
                "Replaced identifiers for audiobook {AudiobookId} ({Title}). Count={Count}",
                audiobook.Id,
                audiobook.Title,
                normalized.Count);

            return Ok(new
            {
                message = "Audiobook identifiers updated successfully",
                audiobook = new
                {
                    id = audiobook.Id,
                    asin = audiobook.Asin,
                    isbn = audiobook.Isbn,
                    openLibraryId = audiobook.OpenLibraryId
                },
                identifiers = AudiobookIdentifierMapper.OrderIdentifiers(audiobook.ExternalIdentifiers)
                    .Select(AudiobookIdentifierMapper.ToIdentifierResponse)
                    .ToList()
            });
        }

        /// <summary>
        /// Re-fetch metadata for an audiobook from upstream sources (Audible / Audnexus) and update the local record.
        /// </summary>
        /// <param name="id">Audiobook ID.</param>
        [HttpPost("{id}/rescan-metadata")]
        public async Task<IActionResult> RescanAudiobookMetadata(int id)
        {
            return await _metadataRescanWorkflow.RescanAsync(id, HttpContext);
        }

        // NOTE: Do not perform ad-hoc schema changes at runtime. Use EF Core migrations to modify the database schema.

        /// <summary>
        /// [Debug] Return raw AudiobookFile database rows for an audiobook. Restricted to local/admin callers.
        /// </summary>
        /// <param name="id">Audiobook ID.</param>
        [HttpGet("{id}/files-debug")]
        [LocalOrAdmin]
        public async Task<IActionResult> GetAudiobookFilesDebug(int id)
        {
            var files = await _audioFileRepository.GetByAudiobookIdAsync(id);
            return Ok(files);
        }

        /// <summary>
        /// Update an existing audiobook's metadata and settings. Supports partial updates — only non-null fields are applied.
        /// </summary>
        /// <param name="id">Audiobook ID.</param>
        /// <param name="updatedAudiobook">Fields to update (null fields are left unchanged).</param>
        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateAudiobook(int id, [FromBody] Audiobook updatedAudiobook)
        {
            var existingAudiobook = await _repo.GetByIdAsync(id);
            if (existingAudiobook == null)
            {
                return NotFound(new { message = "Audiobook not found" });
            }

            var legacyIdentifierFieldsTouched = false;

            // Only update non-null properties to support partial updates
            if (updatedAudiobook.Title != null) existingAudiobook.Title = updatedAudiobook.Title;
            if (updatedAudiobook.Subtitle != null) existingAudiobook.Subtitle = updatedAudiobook.Subtitle;
            if (updatedAudiobook.Authors != null) existingAudiobook.Authors = updatedAudiobook.Authors;
            if (updatedAudiobook.ImageUrl != null) existingAudiobook.ImageUrl = updatedAudiobook.ImageUrl;
            if (updatedAudiobook.PublishYear != null) existingAudiobook.PublishYear = updatedAudiobook.PublishYear;
            if (updatedAudiobook.PublishedDate != null) existingAudiobook.PublishedDate = updatedAudiobook.PublishedDate;
            if (updatedAudiobook.Description != null) existingAudiobook.Description = updatedAudiobook.Description;
            if (updatedAudiobook.Genres != null) existingAudiobook.Genres = updatedAudiobook.Genres;
            if (updatedAudiobook.Tags != null) existingAudiobook.Tags = updatedAudiobook.Tags;
            if (updatedAudiobook.Narrators != null) existingAudiobook.Narrators = updatedAudiobook.Narrators;
            if (updatedAudiobook.Isbn != null)
            {
                existingAudiobook.Isbn = updatedAudiobook.Isbn;
                legacyIdentifierFieldsTouched = true;
            }
            if (updatedAudiobook.Asin != null)
            {
                existingAudiobook.Asin = updatedAudiobook.Asin;
                legacyIdentifierFieldsTouched = true;
            }
            if (updatedAudiobook.OpenLibraryId != null)
            {
                existingAudiobook.OpenLibraryId = updatedAudiobook.OpenLibraryId;
                legacyIdentifierFieldsTouched = true;
            }
            if (updatedAudiobook.Publisher != null) existingAudiobook.Publisher = updatedAudiobook.Publisher;
            if (updatedAudiobook.Language != null) existingAudiobook.Language = updatedAudiobook.Language;
            if (updatedAudiobook.Runtime != null) existingAudiobook.Runtime = updatedAudiobook.Runtime;
            if (updatedAudiobook.Edition != null) existingAudiobook.Edition = updatedAudiobook.Edition;
            if (updatedAudiobook.Version != null) existingAudiobook.Version = updatedAudiobook.Version;

            var seriesMembershipsTouched =
                updatedAudiobook.SeriesMemberships != null ||
                updatedAudiobook.Series != null ||
                updatedAudiobook.SeriesNumber != null;

            if (seriesMembershipsTouched)
            {
                var mergedSeries = updatedAudiobook.Series ?? existingAudiobook.Series;
                var mergedSeriesNumber = updatedAudiobook.SeriesNumber ?? existingAudiobook.SeriesNumber;
                var existingPrimaryMembership = AudiobookSeriesMembershipHelper.GetPrimaryMembership(existingAudiobook.SeriesMemberships);

                var normalizedMemberships = AudiobookSeriesMembershipHelper.Normalize(
                    updatedAudiobook.SeriesMemberships,
                    mergedSeries,
                    mergedSeriesNumber,
                    existingPrimaryMembership?.SeriesAsin);

                if (existingAudiobook.SeriesMemberships == null)
                {
                    existingAudiobook.SeriesMemberships = new List<AudiobookSeriesMembership>();
                }
                else
                {
                    existingAudiobook.SeriesMemberships.Clear();
                }

                foreach (var membership in normalizedMemberships)
                {
                    existingAudiobook.SeriesMemberships.Add(membership);
                }

                AudiobookSeriesMembershipHelper.ApplyPrimarySeriesFields(existingAudiobook);
            }

            // Always update these fields as they have default values
            existingAudiobook.Explicit = updatedAudiobook.Explicit;
            existingAudiobook.Abridged = updatedAudiobook.Abridged;
            existingAudiobook.Monitored = updatedAudiobook.Monitored;

            if (updatedAudiobook.FilePath != null) existingAudiobook.FilePath = updatedAudiobook.FilePath;
            if (updatedAudiobook.FileSize.HasValue) existingAudiobook.FileSize = updatedAudiobook.FileSize;
            if (updatedAudiobook.Quality != null) existingAudiobook.Quality = updatedAudiobook.Quality;

            // Handle QualityProfileId - if -1 is sent, use default profile
            if (updatedAudiobook.QualityProfileId.HasValue)
            {
                if (updatedAudiobook.QualityProfileId.Value == -1)
                {
                    // -1 means "use default profile"
                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var qualityProfileService = scope.ServiceProvider.GetRequiredService<IQualityProfileService>();
                        var defaultProfile = await qualityProfileService.GetDefaultAsync();
                        if (defaultProfile != null)
                        {
                            existingAudiobook.QualityProfileId = defaultProfile.Id;
                            _logger.LogInformation("Assigned default quality profile '{ProfileName}' (ID: {ProfileId}) to audiobook '{Title}'",
                                defaultProfile.Name, defaultProfile.Id, existingAudiobook.Title);
                        }
                        else
                        {
                            _logger.LogWarning("No default quality profile found. Audiobook '{Title}' quality profile set to null.", LogRedaction.SanitizeText(existingAudiobook.Title));
                            existingAudiobook.QualityProfileId = null;
                        }
                    }
                }
                else
                {
                    existingAudiobook.QualityProfileId = updatedAudiobook.QualityProfileId.Value;
                    _logger.LogInformation("Updated quality profile for audiobook '{Title}' to ID {ProfileId}",
                        existingAudiobook.Title, updatedAudiobook.QualityProfileId.Value);
                }
            }

            // Allow updating BasePath (destination) from the frontend when provided
            if (updatedAudiobook.BasePath != null)
            {
                existingAudiobook.BasePath = FileUtils.NormalizeStoredPath(updatedAudiobook.BasePath);
                _logger.LogInformation("Updated BasePath for audiobook '{Title}' to: {BasePath}", LogRedaction.SanitizeText(existingAudiobook.Title), LogRedaction.SanitizeFilePath(updatedAudiobook.BasePath));
            }

            if (legacyIdentifierFieldsTouched)
            {
                AudiobookIdentifierMapper.SyncImportedIdentifiersFromLegacyFields(existingAudiobook);
            }

            await _repo.UpdateAsync(existingAudiobook);

            _logger.LogInformation("Updated audiobook '{Title}' (ID: {Id})", LogRedaction.SanitizeText(existingAudiobook.Title), id);

            return Ok(new { message = "Audiobook updated successfully", audiobook = existingAudiobook });
        }

        /// <summary>
        /// Delete an audiobook from the library, including its cached cover image.
        /// </summary>
        /// <param name="id">Audiobook ID.</param>
        /// <param name="deleteFiles">When true, delete all files within the audiobook folder when it can be done safely; otherwise fall back to tracked audiobook files before removing the library record.</param>
        /// <param name="deleteFolder">When true, also delete the audiobook folder when it can be done safely.</param>
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteAudiobook(int id, [FromQuery] bool deleteFiles = false, [FromQuery] bool deleteFolder = false)
        {
            var audiobook = await _repo.GetByIdAsync(id);
            if (audiobook == null)
            {
                return NotFound(new { message = "Audiobook not found" });
            }

            deleteFiles = deleteFiles || deleteFolder;

            AudiobookFilesystemDeleteResult? filesystemResult = null;
            if (deleteFiles)
            {
                filesystemResult = await _audiobookFilesystemDeleteService.DeleteAsync(audiobook, deleteFolder);
            }

            // Delete associated image from cache if it exists
            try
            {
                // Prefer ASIN-based cleanup when available
                if (!string.IsNullOrEmpty(audiobook.Asin))
                {
                    var imagePath = await _imageCacheService.GetCachedImagePathAsync(audiobook.Asin);
                    if (imagePath != null)
                    {
                        var fullPath = ResolvePathWithOptionalBase(_contentRootPath, imagePath);
                        if (System.IO.File.Exists(fullPath))
                        {
                            System.IO.File.Delete(fullPath);
                            _logger.LogInformation("Deleted cached image for ASIN {Asin}", LogRedaction.SanitizeText(audiobook.Asin));
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(audiobook.ImageUrl))
                {
                    // If ImageUrl points to our cached library folder, extract the filename and delete it
                    try
                    {
                        // Safely extract identifier from an internal library image URL
                        const string __marker = "/config/cache/images/library/";
                        var __url = audiobook.ImageUrl;
                        var __idx = __url.IndexOf(__marker, StringComparison.OrdinalIgnoreCase);
                        if (__idx >= 0)
                        {
                            var filename = __url.Substring(__idx + __marker.Length);
                            // Ensure we only take the file name portion (prevent embedded paths)
                            filename = System.IO.Path.GetFileName(filename);
                            var identifier = System.IO.Path.GetFileNameWithoutExtension(filename);

                            // Validate identifier to a conservative whitelist (alnum, dash, underscore, dot)
                            if (!string.IsNullOrEmpty(identifier) && System.Text.RegularExpressions.Regex.IsMatch(identifier, "^[A-Za-z0-9_\\-\\.]{1,128}$"))
                            {
                                var imagePath = await _imageCacheService.GetCachedImagePathAsync(identifier);
                                if (!string.IsNullOrEmpty(imagePath))
                                {
                                    var fullPath = ResolvePathWithOptionalBase(_contentRootPath, imagePath);
                                    if (System.IO.File.Exists(fullPath))
                                    {
                                        System.IO.File.Delete(fullPath);
                                        _logger.LogInformation("Deleted cached image for identifier (from ImageUrl): {Identifier}", LogRedaction.SanitizeText(identifier));
                                    }
                                }
                            }
                            else
                            {
                                _logger.LogWarning("Image identifier from ImageUrl for audiobook id {Id} is invalid: {Identifier}", audiobook.Id, LogRedaction.SanitizeText(identifier));
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Failed to delete cached image based on stored ImageUrl for audiobook id {Id}", audiobook.Id);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to delete cached image for audiobook id {Id}", audiobook.Id);
                // Continue with deletion even if image cleanup fails
            }

            var deleted = await _repo.DeleteByIdAsync(id);
            if (deleted)
            {
                var message = filesystemResult?.BuildDeleteMessage() ?? "Audiobook deleted successfully.";
                return Ok(new
                {
                    message,
                    id,
                    deletedFiles = filesystemResult?.DeletedFiles ?? 0,
                    deletedFolder = filesystemResult?.DeletedFolder,
                    deletedParentFolder = filesystemResult?.DeletedParentFolder,
                    warnings = filesystemResult?.Warnings ?? new List<string>()
                });
            }

            return StatusCode(500, new { message = "Failed to delete audiobook" });
        }

        /// <summary>
        /// Delete multiple audiobooks in a single transaction.
        /// </summary>
        /// <param name="request">List of audiobook IDs to delete.</param>
        /// <returns>Summary with deleted count, image cleanup count, and any per-item errors.</returns>
        [HttpPost("delete-bulk")]
        public async Task<IActionResult> BulkDeleteAudiobooks([FromBody] BulkDeleteRequest request)
        {
            if (request.Ids == null || !request.Ids.Any())
            {
                return BadRequest(new { message = "No audiobook IDs provided for bulk deletion" });
            }

            var deletedCount = 0;
            var deletedImagesCount = 0;
            var errors = new List<string>();
            var deletedIds = new List<int>();

            foreach (var id in request.Ids.Distinct())
            {
                try
                {
                    var audiobook = await _repo.GetByIdAsync(id);
                    if (audiobook == null)
                    {
                        errors.Add($"Audiobook with ID {id} not found");
                        continue;
                    }

                    // Delete associated image from cache if it exists
                    try
                    {
                        if (!string.IsNullOrEmpty(audiobook.Asin))
                        {
                            var imagePath = await _imageCacheService.GetCachedImagePathAsync(audiobook.Asin);
                            if (imagePath != null)
                            {
                                var fullPath = ResolvePathWithOptionalBase(_contentRootPath, imagePath);
                                if (System.IO.File.Exists(fullPath))
                                {
                                    System.IO.File.Delete(fullPath);
                                    deletedImagesCount++;
                                    _logger.LogInformation("Deleted cached image for ASIN {Asin}", LogRedaction.SanitizeText(audiobook.Asin));
                                }
                            }
                        }
                        else if (!string.IsNullOrEmpty(audiobook.ImageUrl))
                        {
                            try
                            {
                                // Safely extract identifier from an internal library image URL
                                const string __marker = "/config/cache/images/library/";
                                var __url = audiobook.ImageUrl;
                                var __idx = __url.IndexOf(__marker, StringComparison.OrdinalIgnoreCase);
                                if (__idx >= 0)
                                {
                                    var filename = __url.Substring(__idx + __marker.Length);
                                    filename = System.IO.Path.GetFileName(filename);
                                    var identifier = System.IO.Path.GetFileNameWithoutExtension(filename);

                                    if (!string.IsNullOrEmpty(identifier) && System.Text.RegularExpressions.Regex.IsMatch(identifier, "^[A-Za-z0-9_\\-\\.]{1,128}$"))
                                    {
                                        var imagePath = await _imageCacheService.GetCachedImagePathAsync(identifier);
                                        if (!string.IsNullOrEmpty(imagePath))
                                        {
                                            var fullPath = ResolvePathWithOptionalBase(_contentRootPath, imagePath);
                                            if (System.IO.File.Exists(fullPath))
                                            {
                                                System.IO.File.Delete(fullPath);
                                                deletedImagesCount++;
                                                _logger.LogInformation("Deleted cached image for identifier (from ImageUrl): {Identifier}", LogRedaction.SanitizeText(identifier));
                                            }
                                        }
                                    }
                                    else
                                    {
                                        _logger.LogWarning("Image identifier from ImageUrl for audiobook id {Id} is invalid: {Identifier}", audiobook.Id, LogRedaction.SanitizeText(identifier));
                                    }
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                            {
                                _logger.LogWarning(ex, "Failed to delete cached image based on stored ImageUrl for audiobook id {Id}", audiobook.Id);
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Failed to delete cached image for audiobook id {Id}", audiobook.Id);
                        // Continue with deletion even if image cleanup fails
                    }

                    // Log history entry for the deleted audiobook
                    var historyEntry = new History
                    {
                        AudiobookId = audiobook.Id,
                        AudiobookTitle = audiobook.Title ?? "Unknown Title",
                        EventType = "Deleted",
                        Message = $"Audiobook '{audiobook.Title}' deleted via bulk operation",
                        Source = "BulkDelete",
                        Timestamp = DateTime.UtcNow
                    };

                    await _historyRepository.AddAsync(historyEntry);

                    var deleted = await _repo.DeleteByIdAsync(id);
                    if (deleted)
                    {
                        deletedCount++;
                        deletedIds.Add(id);
                        _logger.LogInformation("Deleted audiobook '{Title}' (ID: {Id}) via bulk operation", LogRedaction.SanitizeText(audiobook.Title), id);
                    }
                    else
                    {
                        errors.Add($"Failed to delete audiobook with ID {id}");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogError(ex, "Error during bulk delete for ID {Id}: {Message}", id, ex.Message);
                    errors.Add($"Error deleting audiobook with ID {id}: {ex.Message}");
                }
            }

            if (deletedCount == 0 && errors.Any())
            {
                return BadRequest(new { message = "No audiobooks were successfully deleted", errors });
            }

            object result = errors.Any()
                ? new
                {
                    message = $"Partially successful: deleted {deletedCount} audiobook{(deletedCount != 1 ? "s" : "")}, {errors.Count} error{(errors.Count != 1 ? "s" : "")} occurred",
                    deletedCount,
                    deletedImagesCount,
                    ids = deletedIds,
                    errors
                }
                : new
                {
                    message = $"Successfully deleted {deletedCount} audiobook{(deletedCount != 1 ? "s" : "")}",
                    deletedCount,
                    deletedImagesCount,
                    ids = deletedIds
                };

            return Ok(result);
        }

        /// <summary>
        /// Bulk-update fields (monitored status, quality profile, root folder) for multiple audiobooks at once.
        /// </summary>
        /// <param name="request">Audiobook IDs and the fields to update.</param>
        [HttpPost("bulk-update")]
        public async Task<IActionResult> BulkUpdateAudiobooks([FromBody] BulkUpdateRequest request)
        {
            if (request?.Ids == null || !request.Ids.Any())
            {
                return BadRequest(new { message = "No audiobook IDs provided for bulk update" });
            }

            var results = new List<object>();

            // Fetch application settings once for naming pattern when processing rootFolder changes
            ApplicationSettings? settings = null;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var configService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
                settings = await configService.GetApplicationSettingsAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to load application settings while performing bulk update");
            }

            foreach (var id in request.Ids.Distinct())
            {
                var entryErrors = new List<string>();
                var success = false;

                try
                {
                    var audiobook = await _repo.GetByIdAsync(id);
                    if (audiobook == null)
                    {
                        entryErrors.Add($"Audiobook with ID {id} not found");
                        results.Add(new { id, success, errors = entryErrors });
                        continue;
                    }

                    // Track whether any change was applied
                    var changed = false;

                    // Monitored
                    if (request.Updates != null && request.Updates.TryGetValue("monitored", out var monitoredObj))
                    {
                        try
                        {
                            var monVal = monitoredObj is JsonElement je
                                ? je.ValueKind == JsonValueKind.True
                                : Convert.ToBoolean(monitoredObj);

                            audiobook.Monitored = monVal;
                            changed = true;
                            _logger.LogInformation("Set Monitored={Monitored} for audiobook id={Id}", monVal, id);

                            // History entry
                            await _historyRepository.AddAsync(new History
                            {
                                AudiobookId = audiobook.Id,
                                AudiobookTitle = audiobook.Title ?? "Unknown",
                                EventType = "Updated",
                                Message = $"Monitored set to {monVal}",
                                Source = "BulkUpdate",
                                Timestamp = DateTime.UtcNow
                            });
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            entryErrors.Add($"Invalid monitored value: {ex.Message}");
                        }
                    }

                    // QualityProfileId
                    if (request.Updates != null && request.Updates.TryGetValue("qualityProfileId", out var qpObj))
                    {
                        try
                        {
                            var qpVal = qpObj is JsonElement jq
                                ? jq.GetInt32()
                                : Convert.ToInt32(qpObj);

                            audiobook.QualityProfileId = qpVal;
                            changed = true;
                            _logger.LogInformation("Set QualityProfileId={Profile} for audiobook id={Id}", qpVal, id);

                            await _historyRepository.AddAsync(new History
                            {
                                AudiobookId = audiobook.Id,
                                AudiobookTitle = audiobook.Title ?? "Unknown",
                                EventType = "Updated",
                                Message = $"Quality profile set to {qpVal}",
                                Source = "BulkUpdate",
                                Timestamp = DateTime.UtcNow
                            });
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            entryErrors.Add($"Invalid qualityProfileId value: {ex.Message}");
                        }
                    }

                    // Root folder change (rootFolder => path string)
                    if (request.Updates != null && request.Updates.TryGetValue("rootFolder", out var rootObj))
                    {
                        try
                        {
                            string? rootPath = null;
                            if (rootObj is JsonElement jr)
                            {
                                if (jr.ValueKind == JsonValueKind.String)
                                    rootPath = jr.GetString();
                            }
                            else if (rootObj != null)
                            {
                                rootPath = rootObj.ToString();
                            }

                            if (!string.IsNullOrWhiteSpace(rootPath))
                            {
                                // Use configured naming pattern to compute full base directory for this audiobook
                                var fileNamingPattern = !string.IsNullOrWhiteSpace(settings?.FolderNamingPattern)
                                    ? settings!.FolderNamingPattern
                                    : settings?.FileNamingPattern ?? string.Empty;
                                var newBase = LibraryPathPlanner.ComputeAudiobookBaseDirectoryFromPattern(audiobook, rootPath, fileNamingPattern, _fileNamingService);

                                try
                                {
                                    if (!Directory.Exists(newBase))
                                    {
                                        Directory.CreateDirectory(newBase);
                                        _logger.LogInformation("Created directory for audiobook id={Id} at {Path}", id, newBase);
                                    }

                                    audiobook.BasePath = newBase;
                                    changed = true;

                                    await _historyRepository.AddAsync(new History
                                    {
                                        AudiobookId = audiobook.Id,
                                        AudiobookTitle = audiobook.Title ?? "Unknown",
                                        EventType = "Updated",
                                        Message = $"BasePath set to {newBase} via bulk update",
                                        Source = "BulkUpdate",
                                        Timestamp = DateTime.UtcNow
                                    });
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                {
                                    entryErrors.Add($"Failed to apply root folder for audiobook {id}: {ex.Message}");
                                }
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            entryErrors.Add($"Invalid rootFolder value: {ex.Message}");
                        }
                    }

                    if (changed)
                    {
                        await _repo.UpdateAsync(audiobook);
                        success = true;
                    }
                    else
                    {
                        entryErrors.Add("No valid updates provided for this audiobook");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    entryErrors.Add($"Unhandled error: {ex.Message}");
                }

                results.Add(new { id, success, errors = entryErrors });
            }

            return Ok(new { message = "Bulk update completed", results });
        }

        /// <summary>
        /// Scan the filesystem for files belonging to this audiobook, extract metadata (ffprobe) and persist AudiobookFile records.
        /// Optional body: { path: "C:\\some\\folder" } to scan a specific folder instead of the configured output path.
        /// </summary>
        [HttpPost("{id}/scan")]
        public async Task<IActionResult> ScanAudiobookFiles(int id, [FromBody] ScanRequest? request)
        {
            var audiobook = await _repo.GetByIdAsync(id);
            if (audiobook == null) return NotFound(new { message = "Audiobook not found" });

            // If a background scan queue is available, enqueue the job and return Accepted
            if (_scanQueueService != null)
            {
                try
                {
                    var jobId = await _scanQueueService.EnqueueScanAsync(audiobook, request?.Path);
                    _logger.LogInformation("Enqueued scan job {JobId} for audiobook {AudiobookId}", jobId, id);

                    // Broadcast initial job status so realtime clients can show queued state
                    try
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var hub = scope.ServiceProvider.GetRequiredService<IHubBroadcaster>();
                        var job = new { jobId = jobId.ToString(), audiobookId = id, status = "Queued", enqueuedAt = DateTime.UtcNow };
                        await hub.BroadcastAsync(RealtimeHubTarget.Downloads, "ScanJobUpdate", job);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Failed to broadcast ScanJobUpdate for job {JobId}", jobId);
                    }

                    return Accepted(new { message = "Scan enqueued", jobId });
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogError(ex, "Failed to enqueue scan job for audiobook {AudiobookId}", id);
                    return StatusCode(500, new { message = "Failed to enqueue scan job", error = ex.Message });
                }
            }

            // Determine scan root: request.Path, audiobook.BasePath, or application settings output path
            string? scanRoot = null;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var configService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
                var settings = await configService.GetApplicationSettingsAsync();

                // If audiobook has a BasePath configured, always scan that path for safety
                // Do not fall back to the global output path when a BasePath is present.
                if (!string.IsNullOrEmpty(audiobook.BasePath))
                {
                    scanRoot = Path.GetFullPath(audiobook.BasePath);
                    _logger.LogDebug("Audiobook has BasePath; using it as scan root: {ScanRoot}", LogRedaction.SanitizeFilePath(scanRoot));
                }
                else if (!string.IsNullOrEmpty(request?.Path))
                {
                    // Validate requested path is absolute and contained within a configured root folder or the global output path
                    string requestedFull;
                    try
                    {
                        requestedFull = Path.GetFullPath(request.Path!);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Invalid requested scan path provided: {Path}", LogRedaction.SanitizeFilePath(request.Path));
                        return BadRequest(new { message = "Invalid scan path", path = request.Path });
                    }

                    // Build whitelist of allowed root paths
                    var allowedRoots = new List<string>();
                    if (_rootFolderService != null)
                    {
                        var roots = await _rootFolderService.GetAllAsync();
                        foreach (var r in roots)
                        {
                            try
                            {
                                allowedRoots.Add(Path.GetFullPath(r.Path));
                            }
                            catch (Exception rootPathEx) when (
                                rootPathEx is ArgumentException
                                || rootPathEx is NotSupportedException
                                || rootPathEx is PathTooLongException
                                || rootPathEx is System.Security.SecurityException)
                            {
                                _logger.LogDebug(rootPathEx, "Skipping invalid root folder path during scan allowlist build: {RootPath}", LogRedaction.SanitizeFilePath(r.Path));
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(settings?.OutputPath))
                    {
                        try
                        {
                            allowedRoots.Add(Path.GetFullPath(settings.OutputPath));
                        }
                        catch (Exception outputPathEx) when (
                            outputPathEx is ArgumentException
                            || outputPathEx is NotSupportedException
                            || outputPathEx is PathTooLongException
                            || outputPathEx is System.Security.SecurityException)
                        {
                            _logger.LogDebug(outputPathEx, "Skipping invalid output path during scan allowlist build: {OutputPath}", settings.OutputPath);
                        }
                    }

                    if (allowedRoots.Count == 0)
                    {
                        _logger.LogWarning("Scan request path provided but no root folders are configured; rejecting request.");
                        return BadRequest(new { message = "No root folders configured; cannot accept explicit scan path" });
                    }

                    // Check that requestedFull is equal to or under one of the allowed roots
                    var allowed = allowedRoots.Any(ar => string.Equals(requestedFull, ar, StringComparison.OrdinalIgnoreCase)
                        || requestedFull.StartsWith(ar.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        || requestedFull.StartsWith(ar.TrimEnd(Path.AltDirectorySeparatorChar) + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

                    if (!allowed)
                    {
                        _logger.LogWarning("Requested scan path {Path} is not inside configured root folders", LogRedaction.SanitizeFilePath(request.Path));
                        return BadRequest(new { message = "Requested scan path is not within configured root folders", path = request.Path });
                    }

                    scanRoot = requestedFull;
                }
                else
                {
                    // No BasePath and no explicit path - fall back to configured output path
                    scanRoot = !string.IsNullOrEmpty(settings?.OutputPath) ? Path.GetFullPath(settings.OutputPath) : null;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to read application settings for scan; cannot validate request path without configured roots");
                // If BasePath exists prefer it; otherwise, we cannot determine a safe scan root
                if (!string.IsNullOrEmpty(audiobook.BasePath))
                {
                    scanRoot = Path.GetFullPath(audiobook.BasePath);
                }
                else
                {
                    _logger.LogWarning("Configuration unavailable and audiobook has no BasePath; rejecting scan request for audiobook {AudiobookId}", id);
                    return StatusCode(500, new { message = "Failed to determine a safe scan path" });
                }
            }

            if (string.IsNullOrEmpty(scanRoot) || !Directory.Exists(scanRoot))
            {
                return BadRequest(new { message = "Scan path not provided or does not exist", path = scanRoot });
            }

            _logger.LogInformation("Scanning for audiobook files for '{Title}' under: {Path}", LogRedaction.SanitizeText(audiobook.Title), LogRedaction.SanitizeFilePath(scanRoot));

            // Build a simple matching predicate based on title and first author
            var titleToken = (audiobook.Title ?? string.Empty).Replace("\"", string.Empty).Trim();
            var authorToken = audiobook.Authors?.FirstOrDefault() ?? string.Empty;

            var foundFiles = new List<string>();
            try
            {
                // Search recursively but limit to common audio file extensions
                var exts = FileUtils.AudioExtensions;

                // Iterative safe directory traversal to avoid unhandled IO/Access exceptions and handle special characters
                var dirs = new Stack<string>();
                dirs.Push(scanRoot);

                while (dirs.Count > 0)
                {
                    var dir = dirs.Pop();
                    try
                    {
                        var normalizedDir = Path.GetFullPath(dir);

                        foreach (var file in Directory.EnumerateFiles(normalizedDir))
                        {
                            try
                            {
                                var ext = Path.GetExtension(file);
                                if (!exts.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;
                                var fname = Path.GetFileNameWithoutExtension(file);
                                if (!string.IsNullOrEmpty(titleToken) && fname.IndexOf(titleToken, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    foundFiles.Add(file);
                                    continue;
                                }
                                if (!string.IsNullOrEmpty(authorToken) && file.IndexOf(authorToken, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    foundFiles.Add(file);
                                    continue;
                                }
                            }
                            catch (Exception innerFileEx) when (innerFileEx is not OperationCanceledException && innerFileEx is not OutOfMemoryException && innerFileEx is not StackOverflowException)
                            {
                                _logger.LogDebug(innerFileEx, "Skipped file while scanning {Dir}", normalizedDir);
                                continue;
                            }
                        }

                        foreach (var sub in Directory.EnumerateDirectories(normalizedDir))
                        {
                            dirs.Push(sub);
                        }
                    }
                    catch (System.IO.IOException ioEx)
                    {
                        _logger.LogWarning(ioEx, "IO error while enumerating directory during scan: {Dir}", dir);
                        continue;
                    }
                    catch (UnauthorizedAccessException uaEx)
                    {
                        _logger.LogWarning(uaEx, "Access denied while enumerating directory during scan: {Dir}", dir);
                        continue;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Unexpected error while enumerating directory during scan: {Dir}", dir);
                        continue;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error while scanning filesystem for audiobook files");
                return StatusCode(500, new { message = "Error scanning filesystem", error = ex.Message });
            }

            if (!foundFiles.Any())
            {
                return Ok(new { message = "No files found during scan", scannedPath = scanRoot, found = 0 });
            }

            // Calculate base path for the audiobook files
            var basePath = LibraryPathPlanner.CalculateBasePath(foundFiles, _logger);
            _logger.LogInformation("Calculated base path for audiobook '{Title}': {BasePath}", LogRedaction.SanitizeText(audiobook.Title), LogRedaction.SanitizeFilePath(basePath));

            var created = new List<AudiobookFile>();

            // Extract metadata and persist
            using (var scope = _scopeFactory.CreateScope())
            {
                var metadataService = scope.ServiceProvider.GetRequiredService<IMetadataService>();
                var audioFileRepository = scope.ServiceProvider.GetRequiredService<IAudiobookFileRepository>();
                var historyRepository = scope.ServiceProvider.GetRequiredService<IHistoryRepository>();

                var existingFilesList = await audioFileRepository.GetByAudiobookIdAsync(audiobook.Id);

                foreach (var filePath in foundFiles)
                {
                    try
                    {
                        // Calculate relative path from base path
                        var relativePath = Path.GetRelativePath(basePath, filePath);

                        var existing = existingFilesList.FirstOrDefault(f => f.Path == relativePath);
                        if (existing != null)
                        {
                            _logger.LogInformation("Skipping existing AudiobookFile for audiobook {AudiobookId}: {Path}", audiobook.Id, relativePath);
                            continue;
                        }

                        AudioMetadata? meta = null;
                        try
                        {
                            meta = await metadataService.ExtractFileMetadataAsync(filePath);
                        }
                        catch (Exception mex) when (mex is not OperationCanceledException && mex is not OutOfMemoryException && mex is not StackOverflowException)
                        {
                            _logger.LogWarning(mex, "Failed to extract metadata for file {File}", filePath);
                        }

                        var fi = new FileInfo(filePath);
                        var fileRecord = new AudiobookFile
                        {
                            AudiobookId = audiobook.Id,
                            Path = relativePath, // Store relative path
                            Size = fi.Length,
                            Source = "scan",
                            CreatedAt = DateTime.UtcNow,
                            DurationSeconds = meta?.Duration.TotalSeconds,
                            Format = meta?.Format,
                            Bitrate = meta?.BitRate,
                            SampleRate = meta?.SampleRate,
                            Channels = meta?.Channels
                        };

                        await audioFileRepository.AddAsync(fileRecord);
                        created.Add(fileRecord);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Failed to create AudiobookFile for {File}", filePath);
                    }
                }

                // Update audiobook base path only when we have a non-empty value.
                if (!string.IsNullOrEmpty(basePath))
                {
                    audiobook.BasePath = basePath;
                    await _repo.UpdateAsync(audiobook);
                }

                // Add history entries for newly scanned files
                foreach (var historyEntry in created.Select(fileRecord => new History
                {
                    AudiobookId = audiobook.Id,
                    AudiobookTitle = audiobook.Title ?? "Unknown",
                    EventType = "File Added",
                    Message = $"File scanned and added: {Path.GetFileName(fileRecord.Path)}",
                    Source = "Scan",
                    Data = JsonSerializer.Serialize(new
                    {
                        FilePath = fileRecord.Path,
                        FileSize = fileRecord.Size,
                        Format = fileRecord.Format,
                        Source = fileRecord.Source
                    }),
                    Timestamp = DateTime.UtcNow
                }))
                {
                    await historyRepository.AddAsync(historyEntry);
                }

                // Remove AudiobookFile DB rows for files that no longer exist on disk
                try
                {
                    var allExistingFiles = await audioFileRepository.GetByAudiobookIdAsync(audiobook.Id);

                    var foundSet = new HashSet<string>(foundFiles.Select(f => Path.GetRelativePath(basePath, f)), StringComparer.OrdinalIgnoreCase);
                    var toRemove = allExistingFiles
                        .Where(f => f.Path != null && FileUtils.IsAudioFile(f.Path) && !foundSet.Contains(f.Path))
                        .ToList();

                    List<object> removedFilesDto = new();
                    if (toRemove.Count > 0)
                    {
                        foreach (var rem in toRemove)
                        {
                            try
                            {
                                removedFilesDto.Add(new { id = rem.Id, path = rem.Path });
                                await audioFileRepository.DeleteAsync(rem.Id);
                                _logger.LogInformation("Removing missing AudiobookFile DB row Id={Id} Path={Path}", rem.Id, rem.Path);

                                // Add history entry for removed file
                                var historyEntry = new History
                                {
                                    AudiobookId = audiobook.Id,
                                    AudiobookTitle = audiobook.Title ?? "Unknown",
                                    EventType = "File Removed",
                                    Message = $"File removed (no longer exists): {Path.GetFileName(rem.Path)}",
                                    Source = "Scan",
                                    Data = JsonSerializer.Serialize(new
                                    {
                                        FilePath = rem.Path,
                                        FileSize = rem.Size,
                                        Format = rem.Format,
                                        Source = rem.Source
                                    }),
                                    Timestamp = DateTime.UtcNow
                                };
                                await historyRepository.AddAsync(historyEntry);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                            {
                                _logger.LogWarning(ex, "Failed to remove AudiobookFile Id={Id} Path={Path}", rem.Id, rem.Path);
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to reconcile audiobook files after scan for audiobook {AudiobookId}", audiobook.Id);
                }

                // Handle legacy filePath field migration
                try
                {
                    var needsUpdate = false;
                    if (!string.IsNullOrEmpty(audiobook.FilePath))
                    {
                        // Check if the legacy filePath exists
                        if (System.IO.File.Exists(audiobook.FilePath))
                        {
                            // File exists - check if we already have an AudiobookFile record for it
                            var existingFileRecord = await audioFileRepository.ExistsAtPathAsync(audiobook.Id, audiobook.FilePath!);

                            if (!existingFileRecord)
                            {
                                // Create AudiobookFile record for the legacy filePath
                                try
                                {
                                    using var afScope = _scopeFactory.CreateScope();
                                    var audioFileService = afScope.ServiceProvider.GetRequiredService<IAudiobookFileService>();
                                    var migrated = await audioFileService.EnsureAudiobookFileAsync(audiobook, audiobook.FilePath, "scan-legacy");
                                    if (migrated)
                                    {
                                        _logger.LogInformation("Migrated legacy filePath to AudiobookFile record for audiobook {AudiobookId}: {Path}", audiobook.Id, audiobook.FilePath);
                                        created.Add(new AudiobookFile { Path = audiobook.FilePath }); // Add to created list for response
                                    }
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                {
                                    _logger.LogWarning(ex, "Failed to migrate legacy filePath for audiobook {AudiobookId}: {Path}", audiobook.Id, audiobook.FilePath);
                                }
                            }
                        }
                        else
                        {
                            // File doesn't exist - clear the legacy filePath and related fields
                            audiobook.FilePath = null;
                            audiobook.FileSize = null;
                            needsUpdate = true;
                            _logger.LogInformation("Cleared missing legacy filePath for audiobook {AudiobookId}: {Path}", audiobook.Id, audiobook.FilePath);

                            // Add history entry for cleared filePath
                            var historyEntry = new History
                            {
                                AudiobookId = audiobook.Id,
                                AudiobookTitle = audiobook.Title ?? "Unknown",
                                EventType = "File Removed",
                                Message = $"Legacy file path cleared (file no longer exists)",
                                Source = "Scan",
                                Data = JsonSerializer.Serialize(new
                                {
                                    FilePath = audiobook.FilePath,
                                    Source = "legacy-migration"
                                }),
                                Timestamp = DateTime.UtcNow
                            };
                            await historyRepository.AddAsync(historyEntry);
                        }
                    }

                    if (needsUpdate)
                    {
                        await _repo.UpdateAsync(audiobook);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to handle legacy filePath migration for audiobook {AudiobookId}", audiobook.Id);
                }

                // Reload audiobook with files to return
                var updated = await _repo.GetByIdAsync(audiobook.Id);

                // Send "book-available" notification if the audiobook is monitored and files were imported
                if (_notificationService != null && audiobook.Monitored && created.Count > 0)
                {
                    try
                    {
                        using var notificationScope = _scopeFactory.CreateScope();
                        var configService = notificationScope.ServiceProvider.GetRequiredService<IConfigurationService>();
                        var settings = await configService.GetApplicationSettingsAsync();
                        var availableData = new
                        {
                            id = audiobook.Id,
                            title = audiobook.Title ?? "Unknown Title",
                            authors = audiobook.Authors,
                            asin = audiobook.Asin,
                            imageUrl = audiobook.ImageUrl,
                            description = audiobook.Description,
                            monitored = audiobook.Monitored,
                            qualityProfileId = audiobook.QualityProfileId,
                            filesImported = created.Count,
                            totalFiles = updated?.Files?.Count ?? 0
                        };
                        await _notificationService.SendNotificationAsync("book-available", availableData, settings.WebhookUrl, settings.EnabledNotificationTriggers);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Failed to send book-available notification for audiobook {AudiobookId}", audiobook.Id);
                    }
                }

                return Ok(new { message = "Scan complete", scannedPath = scanRoot, found = foundFiles.Count, created = created.Count, audiobook = updated });
            }
        }

        /// <summary>
        /// Get in-memory scan job status by jobId (debugging/admin helper).
        /// </summary>
        [HttpGet("scan/{jobId}")]
        public IActionResult GetScanJobStatus(string jobId)
        {
            if (_scanQueueService == null) return NotFound(new { message = "Scan queue not available" });
            if (!Guid.TryParse(jobId, out var gid)) return BadRequest(new { message = "Invalid jobId" });
            if (_scanQueueService.TryGetJob(gid, out var job))
            {
                _logger.LogInformation("Queried scan job {JobId} status: {Status}", gid, job!.Status);
                return Ok(job);
            }
            return NotFound(new { message = "Job not found" });
        }

        /// <summary>
        /// Enqueue a background job to move an audiobook's files to a new destination path.
        /// </summary>
        /// <param name="id">Audiobook ID.</param>
        /// <param name="request">Move request with destination path and optional source override.</param>
        /// <returns>Accepted with a job ID that can be polled for progress.</returns>
        [HttpPost("{id}/move")]
        public async Task<IActionResult> EnqueueMove(int id, [FromBody] MoveRequest request)
        {
            if (_moveQueueService == null) return NotFound(new { message = "Move queue not available" });
            var audiobook = await _repo.GetByIdAsync(id);
            if (audiobook == null) return NotFound(new { message = "Audiobook not found" });
            if (request == null) return BadRequest(new { message = "Request body is required" });

            if (string.IsNullOrEmpty(request.DestinationPath))
            {
                return BadRequest(new { message = "DestinationPath is required" });
            }
            if (FileUtils.IsPathInvalidForCurrentOs(request.DestinationPath))
            {
                return BadRequest(new { message = "DestinationPath is not valid for this operating system" });
            }

            try
            {
                // If the path is not rooted, combine with configured output path
                using var scope = _scopeFactory.CreateScope();
                var configService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
                var settings = await configService.GetApplicationSettingsAsync();

                var final = FileUtils.CombineWithOptionalBase(settings.OutputPath, request.DestinationPath!);
                final = FileUtils.NormalizeStoredPath(final);

                // If caller explicitly asked to change the DB without moving files, update the BasePath and return early.
                if (request.MoveFiles == false)
                {
                    try
                    {
                        audiobook.BasePath = final;
                        await _repo.UpdateAsync(audiobook);
                        _logger.LogInformation("Updated BasePath for audiobook {AudiobookId} without moving files: {BasePath}", id, final);
                        return Ok(new { message = "Destination updated" });
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogError(ex, "Failed to update BasePath for audiobook {AudiobookId}", id);
                        return StatusCode(500, new { message = "Failed to update BasePath", error = ex.Message });
                    }
                }

                // Determine source path snapshot to use for the move. Prefer an explicit source from the request
                // (the frontend should send the original source if it updated the audiobook BasePath before requesting a move),
                // otherwise fall back to the current audiobook.BasePath as a best-effort.
                var sourcePath = !string.IsNullOrEmpty(request.SourcePath)
                    ? request.SourcePath
                    : audiobook.BasePath;

                if (string.IsNullOrEmpty(sourcePath))
                {
                    return BadRequest(new { message = "Source path not provided. Supply current source path in the Move request or ensure audiobook has a valid BasePath." });
                }
                if (FileUtils.IsPathInvalidForCurrentOs(sourcePath))
                {
                    return BadRequest(new { message = "Source path is not valid for this operating system." });
                }

                // Validate source exists now to provide earlier feedback to clients (avoids enqueueing doomed jobs)
                if (!Directory.Exists(sourcePath))
                {
                    return BadRequest(new { message = "Source path does not exist. Ensure the audiobook's current BasePath exists or provide a valid SourcePath in the request." });
                }

                // Validate target parent is valid and writable (try to create if necessary)
                var targetParent = Path.GetDirectoryName(final);
                if (string.IsNullOrEmpty(targetParent))
                {
                    return BadRequest(new { message = "Invalid target path" });
                }
                try
                {
                    if (!Directory.Exists(targetParent)) Directory.CreateDirectory(targetParent);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to access or create target parent {TargetParent}", targetParent);
                    return BadRequest(new { message = "Target parent path is not writable or unavailable" });
                }

                // If source and target are identical, nothing to do
                try
                {
                    var srcFull = Path.GetFullPath(sourcePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var tgtFull = Path.GetFullPath(final).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (string.Equals(srcFull, tgtFull, StringComparison.OrdinalIgnoreCase))
                    {
                        return BadRequest(new { message = "Source and target paths are identical; nothing to move." });
                    }
                }
                catch (Exception normalizeEx) when (
                    normalizeEx is ArgumentException
                    || normalizeEx is NotSupportedException
                    || normalizeEx is PathTooLongException
                    || normalizeEx is System.Security.SecurityException)
                {
                    // Ignore errors normalizing paths; background worker will fail if invalid
                    _logger.LogDebug(normalizeEx, "Unable to normalize move paths for audiobook {AudiobookId}", id);
                }

                var jobId = await _moveQueueService.EnqueueMoveAsync(id, final, sourcePath);

                // Broadcast initial job status
                try
                {
                    using var hubScope = _scopeFactory.CreateScope();
                    var hub = hubScope.ServiceProvider.GetRequiredService<IHubBroadcaster>();
                    var job = new { jobId = jobId.ToString(), audiobookId = id, status = "Queued", enqueuedAt = DateTime.UtcNow };
                    await hub.BroadcastAsync(RealtimeHubTarget.Downloads, "MoveJobUpdate", job);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to broadcast MoveJobUpdate for job {JobId}", jobId);
                }

                return Accepted(new { message = "Move enqueued", jobId });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Failed to enqueue move job for audiobook {AudiobookId}", id);
                return StatusCode(500, new { message = "Failed to enqueue move job", error = ex.Message });
            }
        }

        /// <summary>
        /// Get the current status of a file-move background job.
        /// </summary>
        /// <param name="jobId">The GUID returned when the move was enqueued.</param>
        [HttpGet("move/{jobId}")]
        public IActionResult GetMoveJobStatus(string jobId)
        {
            if (_moveQueueService == null) return NotFound(new { message = "Move queue not available" });
            if (!Guid.TryParse(jobId, out var gid)) return BadRequest(new { message = "Invalid jobId" });
            if (_moveQueueService.TryGetJob(gid, out var job))
            {
                _logger.LogInformation("Queried move job {JobId} status: {Status}", gid, job!.Status);
                return Ok(job);
            }
            return NotFound(new { message = "Job not found" });
        }

        /// <summary>
        /// Re-enqueue a previously failed or completed move job for retry.
        /// </summary>
        /// <param name="jobId">Original move job GUID.</param>
        /// <returns>Accepted with the new job ID.</returns>
        [HttpPost("move/requeue/{jobId}")]
        public async Task<IActionResult> RequeueMoveJob(string jobId)
        {
            if (_moveQueueService == null) return NotFound(new { message = "Move queue not available" });
            if (!Guid.TryParse(jobId, out var gid)) return BadRequest(new { message = "Invalid jobId" });

            var newJobId = await _moveQueueService.RequeueMoveAsync(gid);
            if (newJobId == null)
            {
                return BadRequest(new { message = "Unable to requeue job (not found or invalid status)" });
            }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var hub = scope.ServiceProvider.GetRequiredService<IHubBroadcaster>();
                var job = new { jobId = newJobId.ToString(), status = "Queued", enqueuedAt = DateTime.UtcNow };
                await hub.BroadcastAsync(RealtimeHubTarget.Downloads, "MoveJobUpdate", job);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to broadcast MoveJobUpdate for requeued job {JobId}", newJobId);
            }

            return Accepted(new { message = "Requeued move job", jobId = newJobId });
        }

        /// <summary>
        /// Re-enqueue a previously failed or completed scan job for retry.
        /// </summary>
        /// <param name="jobId">Original scan job GUID.</param>
        /// <returns>Accepted with the new job ID.</returns>
        [HttpPost("scan/requeue/{jobId}")]
        public async Task<IActionResult> RequeueScanJob(string jobId)
        {
            if (_scanQueueService == null) return NotFound(new { message = "Scan queue not available" });
            if (!Guid.TryParse(jobId, out var gid)) return BadRequest(new { message = "Invalid jobId" });

            var newJobId = await _scanQueueService.RequeueScanAsync(gid);
            if (newJobId == null)
            {
                return BadRequest(new { message = "Unable to requeue job (not found or invalid status)" });
            }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var hub = scope.ServiceProvider.GetRequiredService<IHubBroadcaster>();
                var job = new { jobId = newJobId.ToString(), status = "Queued", enqueuedAt = DateTime.UtcNow };
                await hub.BroadcastAsync(RealtimeHubTarget.Downloads, "ScanJobUpdate", job);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to broadcast ScanJobUpdate for requeued job {JobId}", newJobId);
            }

            return Accepted(new { message = "Requeued scan job", jobId = newJobId });
        }

        // Helper to convert incoming update values (possibly JsonElement or boxed types) to the target property type
        private static object? ConvertUpdateValue(object? value, Type targetType)
        {
            if (value == null)
            {
                if (targetType == typeof(string)) return string.Empty;
                if (targetType.IsValueType) return Activator.CreateInstance(targetType);
                return null;
            }

            // Unwrap JsonElement if present (from System.Text.Json)
            if (value is JsonElement je)
            {
                try
                {
                    if (je.ValueKind == JsonValueKind.Number && (targetType == typeof(int) || targetType == typeof(int?)))
                        return je.GetInt32();
                    if (je.ValueKind == JsonValueKind.Number && targetType == typeof(double))
                        return je.GetDouble();
                    if (je.ValueKind == JsonValueKind.True || je.ValueKind == JsonValueKind.False)
                        return je.GetBoolean();
                    if (je.ValueKind == JsonValueKind.String)
                        return je.GetString();
                    // Fall back to raw string
                    return je.GetRawText();
                }
                catch (Exception jsonElementConvertEx) when (
                    jsonElementConvertEx is InvalidOperationException
                    || jsonElementConvertEx is FormatException
                    || jsonElementConvertEx is OverflowException)
                {
                    // continue to other conversion attempts
                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                }
            }

            // Handle nullable types
            var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

            // Enums
            if (underlying.IsEnum)
            {
                if (value is string s)
                    return Enum.Parse(underlying, s, true);
                return Enum.ToObject(underlying, Convert.ChangeType(value, Enum.GetUnderlyingType(underlying)));
            }

            // If value already matches
            if (underlying.IsInstanceOfType(value))
                return value;

            // Try Convert.ChangeType on primitives
            try
            {
                return Convert.ChangeType(value, underlying);
            }
            catch (Exception changeTypeEx) when (
                changeTypeEx is InvalidCastException
                || changeTypeEx is FormatException
                || changeTypeEx is OverflowException
                || changeTypeEx is ArgumentException)
            {
                // Final fallback: attempt parse from string
                var str = value.ToString();
                if (underlying == typeof(int) && int.TryParse(str, out var i)) return i;
                if (underlying == typeof(double) && double.TryParse(str, out var d)) return d;
                if (underlying == typeof(bool) && bool.TryParse(str, out var b)) return b;
                if (underlying == typeof(string)) return str;
            }

            // As a last resort, return the original value
            return value;
        }

        private static string ComputeShortHash(string? input)
        {
            if (string.IsNullOrEmpty(input))
                return Guid.NewGuid().ToString("N").Substring(0, 12);

            var bytes = Encoding.UTF8.GetBytes(input);
            var hash = SHA1.HashData(bytes);
            // Return first 16 hex characters for a compact identifier
            return BitConverter.ToString(hash).Replace("-", "").Substring(0, 16).ToLowerInvariant();
        }

        public class BulkDeleteRequest
        {
            public List<int> Ids { get; set; } = new List<int>();
        }

        public class BulkUpdateRequest
        {
            public List<int> Ids { get; set; } = new List<int>();
            public Dictionary<string, object> Updates { get; set; } = new Dictionary<string, object>();
        }

        [HttpPost("rename/preview")]
        public async Task<IActionResult> PreviewRename([FromBody] BulkRenameRequest request, CancellationToken ct)
        {
            if (_renameService == null)
            {
                return StatusCode(503, new { message = "Rename service not available" });
            }

            if (request?.AudiobookIds == null || request.AudiobookIds.Length == 0)
            {
                return BadRequest(new { message = "At least one audiobook ID is required" });
            }

            if (request.AudiobookIds.Length > 500)
            {
                return BadRequest(new { message = "Cannot preview more than 500 audiobooks at once" });
            }

            var previews = await _renameService.PreviewRenameAsync(request.AudiobookIds, ct);
            return Ok(previews);
        }

        [HttpPost("rename")]
        public async Task<IActionResult> ExecuteRename([FromBody] ExecuteRenameRequest request, CancellationToken ct)
        {
            if (_renameService == null)
            {
                return StatusCode(503, new { message = "Rename service not available" });
            }

            if (request?.Operations == null || request.Operations.Count == 0)
            {
                return BadRequest(new { message = "At least one rename operation is required" });
            }

            if (request.Operations.Count > 500)
            {
                return BadRequest(new { message = "Cannot execute more than 500 rename operations at once" });
            }

            var results = await _renameService.ExecuteRenameAsync(request.Operations, ct);
            return Ok(results);
        }

        [HttpPost("{id}/rename/preview")]
        public async Task<IActionResult> PreviewRenameSingle(int id, CancellationToken ct)
        {
            if (_renameService == null)
            {
                return StatusCode(503, new { message = "Rename service not available" });
            }

            var previews = await _renameService.PreviewRenameAsync(new[] { id }, ct);
            var preview = previews.FirstOrDefault();
            if (preview == null)
            {
                return NotFound(new { message = "Audiobook not found" });
            }

            return Ok(preview);
        }

        [HttpPost("{id}/rename")]
        public async Task<IActionResult> ExecuteRenameSingle(int id, [FromBody] RenameOperation operation, CancellationToken ct)
        {
            if (_renameService == null)
            {
                return StatusCode(503, new { message = "Rename service not available" });
            }

            if (operation == null)
            {
                return BadRequest(new { message = "Rename operation is required" });
            }

            operation.AudiobookId = id;
            var results = await _renameService.ExecuteRenameAsync(new List<RenameOperation> { operation }, ct);
            var result = results.FirstOrDefault();
            if (result == null)
            {
                return NotFound(new { message = "Audiobook not found" });
            }

            return Ok(result);
        }

        public class AddToLibraryRequest
        {
            public AudibleBookMetadata Metadata { get; set; } = new();
            public bool Monitored { get; set; } = true;
            public int? QualityProfileId { get; set; }
            public bool AutoSearch { get; set; } = false;
            // Optional destination override for placing the audiobook base directory
            public string? DestinationPath { get; set; }
            public SearchResult? SearchResult { get; set; }
        }

        public class PreviewPathRequest
        {
            public AudibleBookMetadata Metadata { get; set; } = new();
            public string? DestinationRoot { get; set; }
        }

        public class MoveRequest
        {
            public string? DestinationPath { get; set; }
            public string? SourcePath { get; set; }
            // If provided and false, update DB only and do not enqueue a move job
            public bool? MoveFiles { get; set; }
            // When moving files, whether to delete the original folder if empty after the move
            public bool? DeleteEmptySource { get; set; }
        }

    }
}
