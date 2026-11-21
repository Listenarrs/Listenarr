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
using Listenarr.Api.Attributes;

namespace Listenarr.Api.Features.Library
{
    [ApiController]
    [Route("api/v{version:apiVersion}/library")]
    [Tags("Library")]
    public partial class LibraryController : ControllerBase
    {
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
        private readonly LibraryPreviewPathWorkflow _previewPathWorkflow;
        private readonly LibraryQueryWorkflow _queryWorkflow;
        private readonly LibraryRenameWorkflow _renameWorkflow;
        /// <summary>Initializes the library transport façade.</summary>
        public LibraryController(
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
            LibraryPreviewPathWorkflow previewPathWorkflow,
            LibraryQueryWorkflow queryWorkflow,
            LibraryRenameWorkflow renameWorkflow)
        {
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
            _previewPathWorkflow = previewPathWorkflow;
            _queryWorkflow = queryWorkflow;
            _renameWorkflow = renameWorkflow;
        }

        /// <summary>
        /// Add a new audiobook to the library from search metadata.
        /// </summary>
        /// <param name="request">Audiobook metadata, monitoring preference, quality profile, and optional auto-search flag.</param>
        /// <param name="cancellationToken">Request cancellation token.</param>
        /// <returns>The newly created audiobook record.</returns>
        [HttpPost("add")]
        public async Task<IActionResult> AddToLibrary(
            [FromBody] AddToLibraryRequest request,
            CancellationToken cancellationToken = default)
        {
            return await _addWorkflow.AddAsync(request, cancellationToken);
        }

        /// <summary>
        /// Preview the destination path that would be computed for an audiobook based on current naming settings.
        /// </summary>
        /// <param name="request">Audiobook metadata and optional destination root override.</param>
        /// <returns>Full path, relative path, and root directory.</returns>
        [HttpPost("preview-path")]
        public async Task<IActionResult> PreviewPath([FromBody] PreviewPathRequest request)
        {
            return await _previewPathWorkflow.PreviewAsync(request);
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
            return await _queryWorkflow.GetByAsinAsync(asin);
        }

        /// <summary>
        /// Look up an audiobook by its ISBN.
        /// </summary>
        /// <param name="isbn">International Standard Book Number.</param>
        [HttpGet("by-isbn/{isbn}")]
        public async Task<IActionResult> GetByIsbn(string isbn)
        {
            return await _queryWorkflow.GetByIsbnAsync(isbn);
        }

        /// <summary>
        /// Get a single audiobook by its database ID, including files, external identifiers, and wanted status.
        /// </summary>
        /// <param name="id">Audiobook ID.</param>
        [HttpGet("{id}")]
        public async Task<ActionResult<Audiobook>> GetAudiobook(int id)
        {
            return await _queryWorkflow.GetByIdAsync(id);
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
            return await _queryWorkflow.GetFilesDebugAsync(id);
        }

        /// <summary>
        /// Update an existing audiobook's metadata and settings. Supports partial updates; omitted fields are left unchanged.
        /// </summary>
        /// <param name="id">Audiobook ID.</param>
        /// <param name="request">Fields to update.</param>
        /// <param name="cancellationToken">Request cancellation token.</param>
        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateAudiobook(
            int id,
            [FromBody] AudiobookUpdateRequest request,
            CancellationToken cancellationToken = default)
        {
            return await _updateWorkflow.UpdateAsync(
                id,
                request,
                cancellationToken);
        }

        /// <summary>
        /// Delete an audiobook from the library, including its cached cover image.
        /// </summary>
        /// <param name="id">Audiobook ID.</param>
        /// <param name="deleteFiles">When true, delete all files within the audiobook folder when it can be done safely; otherwise fall back to tracked audiobook files before removing the library record.</param>
        /// <param name="deleteFolder">When true, also delete the audiobook folder when it can be done safely.</param>
        /// <param name="cancellationToken">Request cancellation token.</param>
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteAudiobook(
            int id,
            [FromQuery] bool deleteFiles = false,
            [FromQuery] bool deleteFolder = false,
            CancellationToken cancellationToken = default)
        {
            return await _deleteWorkflow.DeleteAsync(
                id,
                deleteFiles,
                deleteFolder,
                cancellationToken);
        }

        [HttpGet("{id}/delete-capabilities")]
        public async Task<IActionResult> GetDeleteCapabilities(
            int id,
            CancellationToken cancellationToken = default)
        {
            var capabilities = await _deleteWorkflow.GetCapabilitiesAsync(
                id,
                cancellationToken);
            return capabilities == null
                ? NotFound(new { message = "Audiobook not found" })
                : Ok(capabilities);
        }

        /// <summary>
        /// Delete multiple audiobooks in a single transaction.
        /// </summary>
        /// <param name="request">List of audiobook IDs to delete.</param>
        /// <param name="cancellationToken">Request cancellation token.</param>
        /// <returns>Summary with deleted count, image cleanup count, and any per-item errors.</returns>
        [HttpPost("delete-bulk")]
        public async Task<IActionResult> BulkDeleteAudiobooks(
            [FromBody] BulkDeleteRequest request,
            CancellationToken cancellationToken = default)
        {
            return await _bulkEditWorkflow.BulkDeleteAsync(
                request,
                cancellationToken);
        }

        /// <summary>
        /// Bulk-update fields (monitored status, quality profile, root folder) for multiple audiobooks at once.
        /// </summary>
        /// <param name="request">Audiobook IDs and the fields to update.</param>
        /// <param name="cancellationToken">Request cancellation token.</param>
        [HttpPost("bulk-update")]
        public async Task<IActionResult> BulkUpdateAudiobooks(
            [FromBody] BulkUpdateRequest request,
            CancellationToken cancellationToken = default)
        {
            return await _bulkEditWorkflow.BulkUpdateAsync(
                request,
                cancellationToken);
        }

        /// <summary>
        /// Scan the filesystem for files belonging to this audiobook, extract metadata (ffprobe) and persist AudiobookFile records.
        /// Optional body: { path: "C:\\some\\folder" } to scan a specific folder instead of the configured output path.
        /// </summary>
        [HttpPost("{id}/scan")]
        public async Task<IActionResult> ScanAudiobookFiles(
            int id,
            [FromBody] ScanRequest? request,
            CancellationToken cancellationToken = default)
        {
            return await _manualScanWorkflow.ScanAsync(
                id,
                request,
                cancellationToken);
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
        /// <param name="cancellationToken">Request cancellation token.</param>
        /// <returns>Accepted with a job ID that can be polled for progress.</returns>
        [HttpPost("{id}/move")]
        public async Task<IActionResult> EnqueueMove(
            int id,
            [FromBody] MoveRequest request,
            CancellationToken cancellationToken = default)
        {
            return await _moveWorkflow.EnqueueAsync(id, request, cancellationToken);
        }

        /// <summary>
        /// Get the durable unresolved move state for an audiobook.
        /// </summary>
        /// <param name="id">Audiobook ID.</param>
        /// <param name="cancellationToken">Request cancellation token.</param>
        [HttpGet("{id}/move/recovery")]
        public async Task<IActionResult> GetMoveRecoveryState(
            int id,
            CancellationToken cancellationToken)
        {
            return await _moveWorkflow.GetRecoveryStateAsync(id, cancellationToken);
        }

        /// <summary>
        /// Get active file-move background jobs for activity recovery.
        /// </summary>
        /// <param name="cancellationToken">Request cancellation token.</param>
        [HttpGet("move")]
        public async Task<IActionResult> GetActiveMoveJobs(CancellationToken cancellationToken)
        {
            return await _moveWorkflow.GetActiveAsync(cancellationToken);
        }

        /// <summary>
        /// Get the current status of a file-move background job.
        /// </summary>
        /// <param name="jobId">The GUID returned when the move was enqueued.</param>
        /// <param name="cancellationToken">Request cancellation token.</param>
        [HttpGet("move/{jobId}")]
        public async Task<IActionResult> GetMoveJobStatus(string jobId, CancellationToken cancellationToken)
        {
            return await _moveWorkflow.GetStatusAsync(jobId, cancellationToken);
        }

        /// <summary>
        /// Re-enqueue a failed, needs-attention, or already queued move job for safe repair.
        /// </summary>
        /// <param name="jobId">Original move job GUID.</param>
        /// <param name="cancellationToken">Request cancellation token.</param>
        /// <returns>Accepted with the new job ID.</returns>
        [HttpPost("move/requeue/{jobId}")]
        public async Task<IActionResult> RequeueMoveJob(
            string jobId,
            CancellationToken cancellationToken = default)
        {
            return await _moveWorkflow.RequeueAsync(jobId, cancellationToken);
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

        [HttpPost("rename/preview")]
        public async Task<IActionResult> PreviewRename([FromBody] BulkRenameRequest request, CancellationToken ct)
        {
            return await _renameWorkflow.PreviewAsync(request, ct);
        }

        [HttpPost("rename")]
        public async Task<IActionResult> ExecuteRename([FromBody] ExecuteRenameRequest request, CancellationToken ct)
        {
            return await _renameWorkflow.ExecuteAsync(request, ct);
        }

        [HttpPost("{id}/rename/preview")]
        public async Task<IActionResult> PreviewRenameSingle(int id, CancellationToken ct)
        {
            return await _renameWorkflow.PreviewSingleAsync(id, ct);
        }

        [HttpPost("{id}/rename")]
        public async Task<IActionResult> ExecuteRenameSingle(int id, [FromBody] RenameOperation operation, CancellationToken ct)
        {
            return await _renameWorkflow.ExecuteSingleAsync(id, operation, ct);
        }

    }
}
