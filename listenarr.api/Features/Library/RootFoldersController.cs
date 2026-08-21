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
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Api.Features.Library
{
    public sealed record RootFolderDto(
        int Id,
        string Name,
        string Path,
        string? PathSyntax,
        bool IsDefault,
        string CaseSensitivityMode,
        string ResolvedCaseSensitivity,
        string PathIdentityState,
        string StorageState,
        string StorageReason,
        string? StorageMessage,
        string? StorageDetail,
        bool CanConfirmCurrentFolder,
        bool CanChangePath,
        bool CanReadFilesystem,
        bool CanScanFilesystem,
        bool CanPublishNewFiles,
        bool CanMutateFilesystem,
        string? ConfirmationToken,
        DateTime CreatedAt,
        DateTime? UpdatedAt,
        RootFolderPathChangeResult? ActiveRelocation);

    public sealed record RootFolderMetadataUpdateRequest(
        string Name,
        bool IsDefault,
        FileSystemCaseSensitivityMode CaseSensitivityMode);

    public sealed record RootFolderPathChangeRequest(
        string TargetPath,
        string Mode,
        bool DeleteEmptySource,
        string DesiredName,
        bool DesiredIsDefault,
        FileSystemCaseSensitivityMode TargetCaseSensitivityMode,
        string ExpectedCurrentPath);

    public sealed record RootFolderConfirmationRequest(
        string ExpectedCurrentPath,
        string ConfirmationToken);

    [ApiController]
    [Route("api/v{version:apiVersion}/rootfolders")]
    [Tags("Root Folders")]
    public partial class RootFoldersController : ControllerBase
    {
        private readonly IRootFolderService _service;
        private readonly IUnmatchedScanQueueService _unmatchedQueue;
        private readonly IAudiobookFileRepository _fileRepository;
        private readonly IAudiobookRepository _audiobookRepository;
        private readonly IFileSystem _fileSystem;
        private readonly IFileSystemSemanticsResolver _semanticsResolver;
        private readonly IRootFolderRelocationService _relocationService;
        private readonly IRootFolderStorageHealthResolver _storageHealthResolver;
        private readonly IRootFolderStorageConfirmationService _storageConfirmationService;
        private readonly ILibraryFilesystemReadiness _filesystemReadiness;
        private readonly ILibraryFilesystemMutationGate _filesystemMutationGate;

        public RootFoldersController(
            IRootFolderService service,
            IUnmatchedScanQueueService unmatchedQueue,
            IAudiobookFileRepository fileRepository,
            IAudiobookRepository audiobookRepository,
            IFileSystem fileSystem,
            IFileSystemSemanticsResolver semanticsResolver,
            IRootFolderRelocationService relocationService,
            IRootFolderStorageHealthResolver storageHealthResolver,
            IRootFolderStorageConfirmationService storageConfirmationService,
            ILibraryFilesystemReadiness filesystemReadiness,
            ILibraryFilesystemMutationGate filesystemMutationGate)
        {
            _service = service;
            _unmatchedQueue = unmatchedQueue;
            _fileRepository = fileRepository;
            _audiobookRepository = audiobookRepository;
            _fileSystem = fileSystem;
            _semanticsResolver = semanticsResolver;
            _relocationService = relocationService ?? throw new ArgumentNullException(nameof(relocationService));
            _storageHealthResolver = storageHealthResolver ?? throw new ArgumentNullException(nameof(storageHealthResolver));
            _storageConfirmationService = storageConfirmationService ?? throw new ArgumentNullException(nameof(storageConfirmationService));
            _filesystemReadiness = filesystemReadiness
                ?? throw new ArgumentNullException(nameof(filesystemReadiness));
            _filesystemMutationGate = filesystemMutationGate
                ?? throw new ArgumentNullException(nameof(filesystemMutationGate));
        }

        /// <summary>
        /// List all configured root folders.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var all = await _service.GetAllAsync();
            var response = new List<RootFolderDto>(all.Count);
            foreach (var root in all)
            {
                response.Add(await MapAsync(root));
            }

            return Ok(response);
        }

        /// <summary>
        /// Get a single root folder by ID.
        /// </summary>
        /// <param name="id">Root folder ID.</param>
        [HttpGet("{id}")]
        public async Task<IActionResult> Get(int id)
        {
            var r = await _service.GetByIdAsync(id);
            if (r == null) return NotFound(new { message = "Root folder not found" });
            return Ok(await MapAsync(r));
        }

        /// <summary>
        /// Create a new root folder.
        /// </summary>
        /// <param name="request">The root folder to create.</param>
        /// <returns>The newly created root folder.</returns>
        [HttpPost]
        public async Task<IActionResult> Create([FromBody] RootFolder request)
        {
            if (!Enum.IsDefined(request.CaseSensitivityMode))
            {
                return BadRequest(new { message = "The root folder request is invalid." });
            }

            _filesystemMutationGate.EnsureReady();

            try
            {
                var created = await _service.CreateAsync(new RootFolder
                {
                    Name = request.Name,
                    Path = request.Path,
                    IsDefault = request.IsDefault,
                    CaseSensitivityMode = request.CaseSensitivityMode
                });
                return CreatedAtAction(nameof(Get), new { id = created.Id }, await MapAsync(created));
            }
            catch (ArgumentException)
            {
                return BadRequest(new
                {
                    message = "The root folder request is invalid."
                });
            }
            catch (InvalidOperationException)
            {
                return Conflict(new
                {
                    message = "The root folder conflicts with existing configuration or cannot be created in its current state."
                });
            }
        }

        /// <summary>
        /// Update an existing root folder.
        /// </summary>
        /// <param name="id">Root folder ID.</param>
        /// <param name="request">Updated root folder data.</param>
        /// <param name="moveFiles">When true, physically move existing audiobook files to the new path.</param>
        /// <param name="deleteEmptySource">When true, delete the old root directory if it is empty after moving files.</param>
        /// <param name="cancellationToken">Request cancellation token.</param>
        [HttpPut("{id}")]
        public async Task<IActionResult> Update(
            int id,
            [FromBody] RootFolder request,
            [FromQuery] bool moveFiles = false,
            [FromQuery] bool deleteEmptySource = true,
            CancellationToken cancellationToken = default)
        {
            if (id != request.Id) return BadRequest(new { message = "Id mismatch" });
            if (!Enum.IsDefined(request.CaseSensitivityMode))
            {
                return BadRequest(new { message = "The root folder path change request is invalid." });
            }

            try
            {
                var existing = await _service.GetByIdAsync(id);
                if (existing == null)
                {
                    return NotFound(new { message = "Root folder not found" });
                }

                var normalizedRequestedPath = FileUtils.NormalizeRootFolderPathForStorage(request.Path);
                var pathChanged = HasRootPathChanged(
                    existing,
                    normalizedRequestedPath);
                var semanticsChanged =
                    existing.CaseSensitivityMode != request.CaseSensitivityMode;
                if (!pathChanged && !semanticsChanged)
                {
                    request.Path = existing.Path;
                    var updatedMetadata = await _service.UpdateAsync(request);
                    return Ok(await MapAsync(updatedMetadata));
                }

                var relocationMode = pathChanged && moveFiles
                    ? RootFolderRelocationMode.Relocate
                    : RootFolderRelocationMode.MetadataOnly;
                if (relocationMode == RootFolderRelocationMode.Relocate)
                {
                    _filesystemMutationGate.EnsureReady();
                }
                else
                {
                    _filesystemReadiness.EnsureMetadataRepairReady();
                }

                var relocationTargetPath = pathChanged
                    ? normalizedRequestedPath
                    : existing.Path;
                var relocation = await _relocationService.StartAsync(
                    id,
                    new RootFolderPathChangeCommand(
                        relocationTargetPath,
                        relocationMode,
                        deleteEmptySource,
                        request.Name,
                        request.IsDefault,
                        request.CaseSensitivityMode,
                        existing.Path),
                    cancellationToken);
                if (relocation.Status == RootFolderRelocationStatus.Completed)
                {
                    var updated = await _service.GetByIdAsync(id)
                        ?? throw new KeyNotFoundException("Root folder not found");
                    return Ok(await MapAsync(updated));
                }

                if (relocation.Status == RootFolderRelocationStatus.NeedsAttention)
                {
                    if (relocationMode == RootFolderRelocationMode.MetadataOnly)
                    {
                        var updated = await _service.GetByIdAsync(id)
                            ?? throw new KeyNotFoundException("Root folder not found");
                        return Ok(await MapAsync(updated));
                    }

                    return Conflict(RootFolderRelocationPublicProjection.Sanitize(relocation));
                }

                return AcceptedAtRoute(
                    "GetRootFolderRelocation",
                    new { id = relocation.RelocationId },
                    RootFolderRelocationPublicProjection.Sanitize(relocation));
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { message = "Root folder not found" });
            }
            catch (ArgumentException)
            {
                return BadRequest(new { message = "The root folder path change request is invalid." });
            }
            catch (RootFolderPathChangeRejectedException exception)
            {
                return LegacyRootFolderPathChangeConflict(exception);
            }
            catch (InvalidOperationException)
            {
                return LegacyRootFolderPathChangeBlocked();
            }
        }

        [HttpPost("{id}/confirm-current-folder")]
        public async Task<IActionResult> ConfirmCurrentFolder(
            int id,
            [FromBody] RootFolderConfirmationRequest request,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request.ExpectedCurrentPath)
                || string.IsNullOrWhiteSpace(request.ConfirmationToken))
            {
                return BadRequest(new
                {
                    message = "The current root folder and confirmation token are required."
                });
            }

            _filesystemMutationGate.EnsureReady();

            try
            {
                var root = await _storageConfirmationService.ConfirmCurrentFolderAsync(
                    id,
                    request.ExpectedCurrentPath,
                    request.ConfirmationToken,
                    cancellationToken);
                return Ok(await MapAsync(root));
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { message = "Root folder not found" });
            }
            catch (ArgumentException)
            {
                return BadRequest(new
                {
                    message = "The root folder confirmation request is invalid."
                });
            }
            catch (InvalidOperationException)
            {
                return Conflict(new
                {
                    message = "The current root folder could not be confirmed. Refresh its storage state and try again.",
                    code = "root_folder_confirmation_blocked"
                });
            }
        }

        /// <summary>
        /// Delete a root folder.
        /// </summary>
        /// <param name="id">Root folder ID to delete.</param>
        /// <param name="reassignTo">Optional ID of another root folder to reassign audiobooks to before deleting.</param>
        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(int id, [FromQuery] int? reassignTo = null)
        {
            try
            {
                await _service.DeleteAsync(id, reassignTo);
                return Ok(new { message = "Deleted" });
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { message = "Root folder not found" });
            }
            catch (InvalidOperationException)
            {
                return BadRequest(new
                {
                    message = "The root folder is in use or cannot be deleted in its current state."
                });
            }
            catch (DbUpdateException)
            {
                return Conflict(new
                {
                    message = "Root folder delete conflicted with persisted references. Resolve active references and retry."
                });
            }
        }

        /// <summary>
        /// Enqueues a background scan of a root folder to find audio files not in the library.
        /// Returns a jobId; subscribe to the realtime "UnmatchedScanComplete" event for completion notification.
        /// </summary>
        [HttpPost("{id}/scan-unmatched")]
        public async Task<IActionResult> ScanUnmatched(int id)
        {
            _filesystemMutationGate.EnsureReady();

            var folder = await _service.GetByIdAsync(id);
            if (folder == null) return NotFound(new { message = "Root folder not found" });

            var storage = await _storageHealthResolver.ResolveAsync(folder);
            if (!storage.CanScanFilesystem)
            {
                return Conflict(new
                {
                    message = storage.Message
                        ?? "The root folder cannot be scanned in its current storage state.",
                    code = "root_folder_scan_unavailable"
                });
            }

            var jobId = await _unmatchedQueue.EnqueueAsync(folder.Path);
            return Ok(new { jobId = jobId.ToString() });
        }

        /// <summary>
        /// Returns the status and results of a previously enqueued unmatched scan job.
        /// </summary>
        [HttpGet("unmatched-results/{jobId}")]
        public IActionResult GetUnmatchedResults(Guid jobId)
        {
            if (!_unmatchedQueue.TryGetJob(jobId, out var job) || job == null)
                return NotFound(new { message = "Scan job not found" });

            return Ok(new
            {
                jobId = job.Id.ToString(),
                status = job.Status,
                error = UnmatchedScanPublicError.FromInternal(job.Error),
                items = job.Results ?? new List<UnmatchedFileResult>()
            });
        }

        /// <summary>
        /// Returns the cached results from the last completed unmatched scan for a root folder.
        /// Returns an empty list if no scan has been run yet this session.
        /// </summary>
        [HttpGet("{id}/unmatched")]
        public async Task<IActionResult> GetSavedUnmatched(int id)
        {
            var folder = await _service.GetByIdAsync(id);
            if (folder == null) return NotFound(new { message = "Root folder not found" });

            if (_unmatchedQueue.TryGetLastJobForPath(folder.Path, out var job) && job != null)
            {
                // Filter out items already added to the library since the scan ran
                var trackedPathSemantics = await ResolveFolderSemanticsAsync(folder);
                var trackedFromFiles = await _fileRepository.GetAllFilePathsAsync(
                    trackedPathSemantics);
                var trackedFromAudiobooks = (await _audiobookRepository.GetAllAsync())
                    .Where(a => a.FilePath != null)
                    .Select(a => a.FilePath!)
                    .ToList();
                var tracked = trackedFromFiles
                    .Concat(trackedFromAudiobooks)
                    .Select(path => TryCanonicalizePathForComparison(path, trackedPathSemantics))
                    .Where(path => path != null)
                    .Select(path => path!)
                    .ToHashSet(trackedPathSemantics.Comparer);

                var filtered = (job.Results ?? new List<UnmatchedFileResult>())
                    .Where(result =>
                    {
                        var canonicalPath = TryCanonicalizePathForComparison(
                            result.FullPath,
                            trackedPathSemantics);
                        return canonicalPath != null
                            && !tracked.Contains(canonicalPath)
                            && _fileSystem.FileExists(result.FullPath);
                    })
                    .ToList();

                return Ok(new
                {
                    lastScannedAt = job.CompletedAt,
                    items = filtered
                });
            }

            return Ok(new { lastScannedAt = (DateTime?)null, items = new List<UnmatchedFileResult>() });
        }

    }
}
