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
using Listenarr.Application.Audiobooks.Bookmarks;
using Listenarr.Application.Audiobooks.Contracts;
using Listenarr.Application.Audiobooks.Playback;
using Listenarr.Domain.Common;

namespace Listenarr.Api.Features.Listening
{
    [ApiController]
    [Route("api/v{version:apiVersion}/audiobooks")]
    [Tags("Listening")]
    public class ListenController : ControllerBase
    {
        private readonly IPlaybackService _playback;
        private readonly IBookmarkService _bookmarkService;
        private readonly ILibraryListService _libraryListService;
        private readonly ILogger<ListenController> _logger;
        private readonly IFileSystem _fileSystem;
        private readonly IRootFolderRepository _rootFolderRepository;

        public ListenController(
            IPlaybackService playback,
            IBookmarkService bookmarkService,
            ILibraryListService libraryListService,
            ILogger<ListenController> logger,
            IFileSystem fileSystem,
            IRootFolderRepository rootFolderRepository)
        {
            _playback = playback;
            _bookmarkService = bookmarkService;
            _libraryListService = libraryListService;
            _logger = logger;
            _fileSystem = fileSystem;
            _rootFolderRepository = rootFolderRepository;
        }

        /// <summary>
        /// Get the current playback state for an audiobook.
        /// </summary>
        /// <param name="id">Audiobook database ID.</param>
        [HttpGet("{id:int}/playback")]
        public async Task<IActionResult> GetPlayback(int id, CancellationToken ct)
        {
            var state = await _playback.GetStateAsync(id, ct);
            return state is null ? NotFound() : Ok(state);
        }

        /// <summary>
        /// Stream an audiobook file by index. Supports HTTP range requests (206 Partial Content).
        /// </summary>
        /// <param name="id">Audiobook database ID.</param>
        /// <param name="index">Zero-based file index within the audiobook.</param>
        [HttpGet("{id:int}/files/{index:int}/stream")]
        public async Task<IActionResult> Stream(int id, int index, CancellationToken ct)
        {
            var resolved = await _playback.ResolveFileAsync(id, index, ct);
            if (resolved is null) return NotFound();
            var (path, contentType) = resolved.Value;

            // Trust boundary: reject path traversal before touching the filesystem.
            if (!await IsUnderConfiguredRootAsync(path))
            {
                _logger.LogWarning(
                    "Blocked stream request for audiobook {AudiobookId} file {FileIndex}: path is outside configured roots",
                    id, index);
                return Forbid();
            }

            if (!_fileSystem.FileExists(path)) return NotFound();

            // IFileSystem has no stream-open method; System.IO.File.OpenRead is intentional here.
            var stream = System.IO.File.OpenRead(path);
            return File(stream, contentType, enableRangeProcessing: true);
        }

        /// <summary>
        /// Save playback progress for an audiobook.
        /// </summary>
        /// <param name="id">Audiobook database ID.</param>
        /// <param name="req">Progress state to persist.</param>
        [HttpPut("{id:int}/playback")]
        public async Task<IActionResult> SavePlayback(int id, [FromBody] SavePlaybackRequest req, CancellationToken ct)
        {
            var ok = await _playback.SaveAsync(id, req, ct);
            return ok ? NoContent() : NotFound();
        }

        // ── Continue Listening ────────────────────────────────────────────────────

        /// <summary>
        /// Get in-progress audiobooks (started but not finished), ordered most-recently-played first, capped at 20.
        /// </summary>
        [HttpGet("continue-listening")]
        public async Task<IActionResult> GetContinueListening()
        {
            return Ok(await _libraryListService.GetContinueListeningAsync());
        }

        // ── Bookmarks ────────────────────────────────────────────────────────────

        /// <summary>
        /// List all bookmarks for an audiobook.
        /// </summary>
        /// <param name="id">Audiobook database ID.</param>
        [HttpGet("{id:int}/bookmarks")]
        public async Task<IActionResult> GetBookmarks(int id, CancellationToken ct)
        {
            return Ok(await _bookmarkService.GetAsync(id, ct));
        }

        /// <summary>
        /// Create a bookmark for an audiobook.
        /// </summary>
        /// <param name="id">Audiobook database ID.</param>
        /// <param name="request">File index, position, and optional label.</param>
        [HttpPost("{id:int}/bookmarks")]
        public async Task<IActionResult> CreateBookmark(int id, [FromBody] CreateBookmarkRequest request, CancellationToken ct)
        {
            var dto = await _bookmarkService.AddAsync(id, request, ct);
            return StatusCode(201, dto);
        }

        /// <summary>
        /// Delete a bookmark.
        /// </summary>
        /// <param name="id">Audiobook database ID.</param>
        /// <param name="bookmarkId">Bookmark ID to delete.</param>
        [HttpDelete("{id:int}/bookmarks/{bookmarkId:int}")]
        public async Task<IActionResult> DeleteBookmark(int id, int bookmarkId, CancellationToken ct)
        {
            var deleted = await _bookmarkService.DeleteAsync(id, bookmarkId, ct);
            return deleted ? NoContent() : NotFound();
        }

        // ── Internal helpers ──────────────────────────────────────────────────

        /// <summary>
        /// Returns true only when <paramref name="path"/> is inside one of the app's configured
        /// root folders. Uses <see cref="FileUtils.IsPathSameOrInside"/> for case-insensitive,
        /// normalised comparison so that path traversal sequences ("..") are collapsed before
        /// the boundary check.
        /// </summary>
        private async Task<bool> IsUnderConfiguredRootAsync(string path)
        {
            var roots = await _rootFolderRepository.GetAllAsync();
            return roots.Any(r => FileUtils.IsPathSameOrInside(path, r.Path));
        }
    }
}
