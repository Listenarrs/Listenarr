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
using Listenarr.Application.Audiobooks;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Domain.Models;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Controllers
{
    [ApiController]
    [Route("api/v{version:apiVersion}/rootfolders")]
    [Tags("Root Folders")]
    public class RootFoldersController : ControllerBase
    {
        private readonly IRootFolderService _service;
        private readonly IUnmatchedScanQueueService _unmatchedQueue;
        private readonly IAudiobookFileRepository _fileRepository;
        private readonly IAudiobookRepository _audiobookRepository;

        public RootFoldersController(IRootFolderService service, IUnmatchedScanQueueService unmatchedQueue, IAudiobookFileRepository fileRepository, IAudiobookRepository audiobookRepository)
        {
            _service = service;
            _unmatchedQueue = unmatchedQueue;
            _fileRepository = fileRepository;
            _audiobookRepository = audiobookRepository;
        }

        /// <summary>
        /// List all configured root folders.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var all = await _service.GetAllAsync();
            return Ok(all);
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
            return Ok(r);
        }

        /// <summary>
        /// Create a new root folder.
        /// </summary>
        /// <param name="request">The root folder to create.</param>
        /// <returns>The newly created root folder.</returns>
        [HttpPost]
        public async Task<IActionResult> Create([FromBody] RootFolder request)
        {
            try
            {
                var created = await _service.CreateAsync(request);
                return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                return StatusCode(500, new { message = "Failed to create root folder", error = ex.Message });
            }
        }

        /// <summary>
        /// Update an existing root folder.
        /// </summary>
        /// <param name="id">Root folder ID.</param>
        /// <param name="request">Updated root folder data.</param>
        /// <param name="moveFiles">When true, physically move existing audiobook files to the new path.</param>
        /// <param name="deleteEmptySource">When true, delete the old root directory if it is empty after moving files.</param>
        [HttpPut("{id}")]
        public async Task<IActionResult> Update(int id, [FromBody] RootFolder request, [FromQuery] bool moveFiles = false, [FromQuery] bool deleteEmptySource = true)
        {
            if (id != request.Id) return BadRequest(new { message = "Id mismatch" });
            try
            {
                var updated = await _service.UpdateAsync(request, moveFiles, deleteEmptySource);
                return Ok(updated);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                return StatusCode(500, new { message = "Failed to update root folder", error = ex.Message });
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
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                return StatusCode(500, new { message = "Failed to delete root folder", error = ex.Message });
            }
        }

        /// <summary>
        /// Enqueues a background scan of a root folder to find audio files not in the library.
        /// Returns a jobId; subscribe to SignalR "UnmatchedScanComplete" for completion notification.
        /// </summary>
        [HttpPost("{id}/scan-unmatched")]
        public async Task<IActionResult> ScanUnmatched(int id)
        {
            var folder = await _service.GetByIdAsync(id);
            if (folder == null) return NotFound(new { message = "Root folder not found" });

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
                error = job.Error,
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
                var trackedFromFiles = await _fileRepository.GetAllFilePathsAsync();
                var trackedFromAudiobooks = (await _audiobookRepository.GetAllAsync())
                    .Where(a => a.FilePath != null)
                    .Select(a => a.FilePath!)
                    .ToList();
                var tracked = new HashSet<string>(
                    trackedFromFiles.Concat(trackedFromAudiobooks),
                    StringComparer.OrdinalIgnoreCase);

                var filtered = (job.Results ?? new List<UnmatchedFileResult>())
                    .Where(r => !tracked.Contains(r.FullPath) && System.IO.File.Exists(r.FullPath))
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
