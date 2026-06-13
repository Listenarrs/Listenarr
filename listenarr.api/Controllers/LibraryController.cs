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
using Listenarr.Domain.Common;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
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
        private readonly ILogger<LibraryController> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IAudiobookFileRepository _audioFileRepository;
        private readonly IMoveQueueService? _moveQueueService;
        private readonly IFileNamingService _fileNamingService;
        private readonly IRenameService? _renameService;
        private readonly ILibraryListService _libraryListService;
        private readonly LibraryAddWorkflow _addWorkflow;
        private readonly LibraryMetadataRescanWorkflow _metadataRescanWorkflow;
        private readonly LibraryScanPathResolver _scanPathResolver;
        private readonly LibraryScanQueueWorkflow _scanQueueWorkflow;
        private readonly LibraryManualScanWorkflow _manualScanWorkflow;
        private readonly LibraryBulkEditWorkflow _bulkEditWorkflow;
        private readonly LibraryMoveWorkflow _moveWorkflow;
        private readonly LibraryDeleteWorkflow _deleteWorkflow;
        private readonly LibraryUpdateWorkflow _updateWorkflow;
        private readonly LibraryIdentifierWorkflow _identifierWorkflow;
        /// <summary>Initializes a new instance of <see cref="LibraryController"/>.</summary>
        /// <param name="repo">Repository for audiobook persistence and queries.</param>
        /// <param name="logger">Logger instance for diagnostic messages.</param>
        /// <param name="scopeFactory">Service scope factory used to create scoped services when required.</param>
        /// <param name="audioFileRepository">Repository for audiobook file records.</param>
        /// <param name="fileNamingService">Service responsible for applying file naming patterns.</param>
        /// <param name="moveQueueService">Optional background move queue service for processing move requests.</param>
        /// <param name="renameService">Optional organize/rename service used for previewing and executing library file organization.</param>
        /// <param name="libraryListService">Application service that builds the slim library list payload.</param>
        /// <param name="addWorkflow">API workflow for add-to-library requests.</param>
        /// <param name="metadataRescanWorkflow">API workflow for on-demand audiobook metadata rescans.</param>
        /// <param name="scanPathResolver">API workflow for resolving and validating scan roots.</param>
        /// <param name="scanQueueWorkflow">API workflow for background scan queue operations.</param>
        /// <param name="manualScanWorkflow">API workflow for inline scan execution and reconciliation.</param>
        /// <param name="bulkEditWorkflow">API workflow for bulk update and delete operations.</param>
        /// <param name="moveWorkflow">API workflow for move queue operations.</param>
        /// <param name="deleteWorkflow">API workflow for single audiobook delete operations.</param>
        /// <param name="updateWorkflow">API workflow for single audiobook update operations.</param>
        /// <param name="identifierWorkflow">API workflow for audiobook identifier operations.</param>
        public LibraryController(
            IAudiobookRepository repo,
            ILogger<LibraryController> logger,
            IServiceScopeFactory scopeFactory,
            IAudiobookFileRepository audioFileRepository,
            IFileNamingService fileNamingService,
            ILibraryListService libraryListService,
            LibraryAddWorkflow addWorkflow,
            LibraryMetadataRescanWorkflow metadataRescanWorkflow,
            LibraryScanPathResolver scanPathResolver,
            LibraryScanQueueWorkflow scanQueueWorkflow,
            LibraryManualScanWorkflow manualScanWorkflow,
            LibraryBulkEditWorkflow bulkEditWorkflow,
            LibraryMoveWorkflow moveWorkflow,
            LibraryDeleteWorkflow deleteWorkflow,
            LibraryUpdateWorkflow updateWorkflow,
            LibraryIdentifierWorkflow identifierWorkflow,
            IMoveQueueService? moveQueueService = null,
            IRenameService? renameService = null)
        {
            _repo = repo;
            _logger = logger;
            _scopeFactory = scopeFactory;
            _audioFileRepository = audioFileRepository;
            _fileNamingService = fileNamingService;
            _moveQueueService = moveQueueService;
            _renameService = renameService;
            _libraryListService = libraryListService;
            _addWorkflow = addWorkflow;
            _metadataRescanWorkflow = metadataRescanWorkflow;
            _scanPathResolver = scanPathResolver;
            _scanQueueWorkflow = scanQueueWorkflow;
            _manualScanWorkflow = manualScanWorkflow;
            _bulkEditWorkflow = bulkEditWorkflow;
            _moveWorkflow = moveWorkflow;
            _deleteWorkflow = deleteWorkflow;
            _updateWorkflow = updateWorkflow;
            _identifierWorkflow = identifierWorkflow;
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
            return await _addWorkflow.AddAsync(request);
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
            return await _identifierWorkflow.GetAsync(id);
        }

        /// <summary>
        /// Replace all external identifiers for an audiobook in a single operation.
        /// </summary>
        /// <param name="id">Audiobook ID.</param>
        /// <param name="request">New set of identifiers. Existing identifiers will be removed and replaced.</param>
        [HttpPut("{id}/identifiers")]
        public async Task<IActionResult> ReplaceAudiobookIdentifiers(int id, [FromBody] ReplaceAudiobookIdentifiersRequest? request)
        {
            return await _identifierWorkflow.ReplaceAsync(id, request);
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
            return await _updateWorkflow.UpdateAsync(id, updatedAudiobook);
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
            return await _deleteWorkflow.DeleteAsync(id, deleteFiles, deleteFolder);
        }

        /// <summary>
        /// Delete multiple audiobooks in a single transaction.
        /// </summary>
        /// <param name="request">List of audiobook IDs to delete.</param>
        /// <returns>Summary with deleted count, image cleanup count, and any per-item errors.</returns>
        [HttpPost("delete-bulk")]
        public async Task<IActionResult> BulkDeleteAudiobooks([FromBody] BulkDeleteRequest request)
        {
            return await _bulkEditWorkflow.BulkDeleteAsync(request);
        }

        /// <summary>
        /// Bulk-update fields (monitored status, quality profile, root folder) for multiple audiobooks at once.
        /// </summary>
        /// <param name="request">Audiobook IDs and the fields to update.</param>
        [HttpPost("bulk-update")]
        public async Task<IActionResult> BulkUpdateAudiobooks([FromBody] BulkUpdateRequest request)
        {
            return await _bulkEditWorkflow.BulkUpdateAsync(request);
        }

        /// <summary>
        /// Scan the filesystem for files belonging to this audiobook, extract metadata (ffprobe) and persist AudiobookFile records.
        /// Optional body: { path: "C:\\some\\folder" } to scan a specific folder instead of the configured output path.
        /// </summary>
        [HttpPost("{id}/scan")]
        public async Task<IActionResult> ScanAudiobookFiles(int id, [FromBody] ScanRequest? request)
        {
            return await _manualScanWorkflow.ScanAsync(id, request);
        }

        /// <summary>
        /// Get in-memory scan job status by jobId (debugging/admin helper).
        /// </summary>
        [HttpGet("scan/{jobId}")]
        public IActionResult GetScanJobStatus(string jobId)
        {
            return _scanQueueWorkflow.GetStatus(jobId);
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
            return await _moveWorkflow.EnqueueAsync(id, request);
        }

        /// <summary>
        /// Get the current status of a file-move background job.
        /// </summary>
        /// <param name="jobId">The GUID returned when the move was enqueued.</param>
        [HttpGet("move/{jobId}")]
        public IActionResult GetMoveJobStatus(string jobId)
        {
            return _moveWorkflow.GetStatus(jobId);
        }

        /// <summary>
        /// Re-enqueue a previously failed or completed move job for retry.
        /// </summary>
        /// <param name="jobId">Original move job GUID.</param>
        /// <returns>Accepted with the new job ID.</returns>
        [HttpPost("move/requeue/{jobId}")]
        public async Task<IActionResult> RequeueMoveJob(string jobId)
        {
            return await _moveWorkflow.RequeueAsync(jobId);
        }

        /// <summary>
        /// Re-enqueue a previously failed or completed scan job for retry.
        /// </summary>
        /// <param name="jobId">Original scan job GUID.</param>
        /// <returns>Accepted with the new job ID.</returns>
        [HttpPost("scan/requeue/{jobId}")]
        public async Task<IActionResult> RequeueScanJob(string jobId)
        {
            return await _scanQueueWorkflow.RequeueAsync(jobId);
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
